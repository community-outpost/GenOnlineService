using Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace GenOnlineService.Tests;

// Runs only when GENONLINE_PROBE_POSTGRES / GENONLINE_PROBE_MARIADB hold a server connection string (no database name);
// each test migrates its own new database on that server and drops it afterwards
public sealed class ProbeTheoryAttribute : TheoryAttribute
{
	public ProbeTheoryAttribute()
	{
		if (Environment.GetEnvironmentVariable(ProbeDatabase.PostgresVariable) == null && Environment.GetEnvironmentVariable(ProbeDatabase.MariaDbVariable) == null)
		{
			Skip = $"Set {ProbeDatabase.PostgresVariable} and/or {ProbeDatabase.MariaDbVariable} to run the database probes";
		}
	}
}

public sealed class ProbeDatabase : IAsyncDisposable
{
	public const string PostgresVariable = "GENONLINE_PROBE_POSTGRES";
	public const string MariaDbVariable = "GENONLINE_PROBE_MARIADB";

	public static IEnumerable<object[]> Providers()
	{
		yield return new object[] { "postgres" };
		yield return new object[] { "mariadb" };
	}

	private readonly Func<AppDbContext> m_create;
	public string Provider { get; }

	private ProbeDatabase(string provider, Func<AppDbContext> create)
	{
		Provider = provider;
		m_create = create;
	}

	// Null when this provider's server is not configured
	public static async Task<ProbeDatabase?> Create(string provider, bool migrate = true)
	{
		string? server = Environment.GetEnvironmentVariable(provider == "postgres" ? PostgresVariable : MariaDbVariable);
		if (server == null)
		{
			return null;
		}

		string name = "gn_probe_" + Guid.NewGuid().ToString("N").Substring(0, 12);
		Func<AppDbContext> create;
		if (provider == "postgres")
		{
			string connection = $"{server};Database={name}";
			create = () => new PostgresAppDbContext(new DbContextOptionsBuilder<PostgresAppDbContext>().UseNpgsql(connection).Options);
		}
		else
		{
			string connection = $"{server};Database={name}";
			ServerVersion version = ServerVersion.AutoDetect(server);
			create = () => new MySqlAppDbContext(new DbContextOptionsBuilder<MySqlAppDbContext>().UseMySql(connection, version).Options);
		}

		var probe = new ProbeDatabase(provider, create);
		if (migrate)
		{
			await using AppDbContext db = probe.Open();
			await db.Database.MigrateAsync();
		}
		return probe;
	}

	public AppDbContext Open() => m_create();

	public async ValueTask DisposeAsync()
	{
		await using AppDbContext db = Open();
		await db.Database.EnsureDeletedAsync();
	}
}

public class DisplayNameProbeTests
{
	private static async Task<User> AddUser(AppDbContext db, string name, int tag)
	{
		var user = new User { Username = name, UsernameNormalized = NameNormalizer.Normalize(name), NameTag = tag, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user;
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task NewUsersWithOneNameGetDifferentTagsInRange(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		await using AppDbContext db = probe.Open();

		var tags = new HashSet<int>();
		for (int i = 0; i < 20; i++)
		{
			User user = await Users.Create(db, i % 2 == 0 ? "Twin" : "TWIN");
			Assert.InRange(user.NameTag, DisplayNames.MinTag, DisplayNames.MaxTag);
			Assert.True(tags.Add(user.NameTag), $"tag {user.NameTag} given twice");
		}

		// Users.Create keeps a given id, as before tags
		User kept = await Users.Create(db, "Kept", 777_777);
		Assert.Equal(777_777, kept.ID);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheIndexRefusesTheSameNameAndTagTwiceButAllowsTheNameAlone(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		await using AppDbContext db = probe.Open();

		await AddUser(db, "Bob", 42);
		await AddUser(db, "Bob", 43);

		db.ChangeTracker.Clear();
		DbUpdateException ex = await Assert.ThrowsAsync<DbUpdateException>(() => AddUser(db, "BOB", 42));
		Assert.True(DbHelpers.IsUniqueViolation(ex));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task UpstreamCopyWritesNameTagAndHistory(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		await using AppDbContext db = probe.Open();
		User user = await AddUser(db, "Before", 7);

		IReadOnlyList<NameChange> changes = await DisplayNames.ApplyUpstream(db, user.ID, "After", "0042", "upstream:keycloak");

		NameChange change = Assert.Single(changes);
		Assert.Equal(new NameChange(user.ID, "Before", 7, "After", 42), change);
		db.ChangeTracker.Clear();
		User stored = await db.Users.SingleAsync(u => u.ID == user.ID);
		Assert.Equal(("After", "AFTER", 42), (stored.Username, stored.UsernameNormalized, stored.NameTag));
		Assert.NotNull(stored.RenamedAt);
		UserUsernameHistory history = await db.UserUsernameHistory.SingleAsync(h => h.UserID == user.ID);
		Assert.Equal(("Before", (int?)7, "upstream:keycloak"), (history.Username, history.NameTag, history.ChangedBy));

		// the same copy again changes nothing and writes no history
		Assert.Empty(await DisplayNames.ApplyUpstream(db, user.ID, "After", "42", "upstream:keycloak"));
		Assert.Equal(1, await db.UserUsernameHistory.CountAsync(h => h.UserID == user.ID));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task UpstreamCopyMovesAStaleHolderOfTheSameNameAndTag(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		await using AppDbContext db = probe.Open();
		User stale = await AddUser(db, "Shared", 5);
		User user = await AddUser(db, "Other", 9);

		IReadOnlyList<NameChange> changes = await DisplayNames.ApplyUpstream(db, user.ID, "shared", "5", "upstream:keycloak");

		Assert.Equal(2, changes.Count);
		db.ChangeTracker.Clear();
		User movedStale = await db.Users.SingleAsync(u => u.ID == stale.ID);
		User copied = await db.Users.SingleAsync(u => u.ID == user.ID);
		Assert.Equal(("shared", 5), (copied.Username, copied.NameTag));
		Assert.Equal("Shared", movedStale.Username);
		Assert.NotEqual(5, movedStale.NameTag);
		Assert.InRange(movedStale.NameTag, DisplayNames.MinTag, DisplayNames.MaxTag);
		Assert.Contains(changes, c => c.UserID == stale.ID && c.OldTag == 5 && c.NewTag == movedStale.NameTag && !c.NameChanged);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task SignUpTakesTheUpstreamNameAndTagOnlyWhenFree(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		await using AppDbContext db = probe.Open();
		await AddUser(db, "Held", 8);

		var fresh = new User { Username = "ignored" };
		Assert.True(await DisplayNames.TryAssignUpstream(db, fresh, "Newbie", "0031"));
		Assert.Equal(("Newbie", "NEWBIE", 31), (fresh.Username, fresh.UsernameNormalized, fresh.NameTag));

		var clash = new User { Username = "kept" };
		Assert.False(await DisplayNames.TryAssignUpstream(db, clash, "held", "8"));
		Assert.False(await DisplayNames.TryAssignUpstream(db, clash, "Fine", "0"));
		Assert.Equal(("kept", 0), (clash.Username, clash.NameTag));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task UpstreamCopyIgnoresBadTagsAndNames(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		await using AppDbContext db = probe.Open();
		User user = await AddUser(db, "Steady", 3);

		foreach ((string? name, string? tag) in new (string?, string?)[] { ("X", "0"), ("X", "10000"), ("X", "abc"), ("X", null), (null, "12"), ("Bad\0", "12"), (new string('a', 33), "12") })
		{
			Assert.Empty(await DisplayNames.ApplyUpstream(db, user.ID, name, tag, "upstream:keycloak"));
		}

		db.ChangeTracker.Clear();
		User stored = await db.Users.SingleAsync(u => u.ID == user.ID);
		Assert.Equal(("Steady", 3), (stored.Username, stored.NameTag));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task LocalRenameKeepsTheTagForCaseAndPicksAFreeOneOtherwise(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		await using AppDbContext db = probe.Open();
		User user = await AddUser(db, "casey", 11);
		await AddUser(db, "Taken", 1);

		NameChange? caseOnly = await Users.SetDisplayName(db, user.ID, "Casey");
		Assert.Equal(new NameChange(user.ID, "casey", 11, "Casey", 11), caseOnly);

		NameChange? renamed = await Users.SetDisplayName(db, user.ID, "Taken");
		Assert.NotNull(renamed);
		Assert.NotEqual(1, renamed!.NewTag);
		Assert.Equal(2, await db.UserUsernameHistory.CountAsync(h => h.UserID == user.ID));

		// an unchanged name reads back as itself, an unstorable one fails as before
		Assert.Equal(new NameChange(user.ID, "Taken", renamed.NewTag, "Taken", renamed.NewTag), await Users.SetDisplayName(db, user.ID, "Taken"));
		Assert.Null(await Users.SetDisplayName(db, user.ID, "Nul\0"));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ANameWithEveryTagTakenHasNoFreeTag(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		await using AppDbContext db = probe.Open();

		DateTime now = DateTime.UtcNow;
		db.Users.AddRange(Enumerable.Range(DisplayNames.MinTag, DisplayNames.MaxTag).Select(tag =>
			new User { Username = "Full", UsernameNormalized = "FULL", NameTag = tag, CreatedAt = now, UpdatedAt = now }));
		await db.SaveChangesAsync();
		User other = await AddUser(db, "Elsewhere", 1);

		await Assert.ThrowsAsync<NameTagsExhaustedException>(() => DisplayNames.FreeTag(db, "FULL"));
		Assert.Null(await Users.SetDisplayName(db, other.ID, "full"));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheMigrationGivesExistingUsersTagOne(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider, migrate: false);
		if (probe == null) return;
		await using AppDbContext db = probe.Open();
		IMigrator migrator = db.GetService<IMigrator>();

		string before = db.Database.GetMigrations().Last(m => !m.EndsWith("_DisplayNameTags", StringComparison.Ordinal));
		await migrator.MigrateAsync(before);
		await db.Database.ExecuteSqlRawAsync(
			"INSERT INTO users (username, username_normalized, security_stamp, state, two_factor_enabled, failed_sign_in_count, created_at, updated_at) "
			+ "VALUES ('Old', 'OLD', 'x', 'active', FALSE, 0, {0}, {0})", DateTime.UtcNow);
		await migrator.MigrateAsync();

		db.ChangeTracker.Clear();
		Assert.Equal(1, (await db.Users.SingleAsync(u => u.UsernameNormalized == "OLD")).NameTag);
	}
}
