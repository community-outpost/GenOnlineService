using Database;
using GenOnlineService.Names;
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

		// a look-alike has another normalized name but the same key
		db.ChangeTracker.Clear();
		DbUpdateException lookAlike = await Assert.ThrowsAsync<DbUpdateException>(() => AddUser(db, "B0b", 42));
		Assert.True(DbHelpers.IsUniqueViolation(lookAlike));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task UpstreamCopyWritesNameTagAndHistory(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		await using AppDbContext db = probe.Open();
		User user = await AddUser(db, "Before", 7);

		IReadOnlyList<NameChange> changes = await DisplayNames.ApplyUpstream(db, user.ID, "After", "0042", "upstream:prod");

		NameChange change = Assert.Single(changes);
		Assert.Equal(new NameChange(user.ID, "Before", 7, "After", 42), change);
		db.ChangeTracker.Clear();
		User stored = await db.Users.SingleAsync(u => u.ID == user.ID);
		Assert.Equal(("After", "AFTER", "after", 42), (stored.Username, stored.UsernameNormalized, stored.NameKey, stored.NameTag));
		Assert.NotNull(stored.RenamedAt);
		UserUsernameHistory history = await db.UserUsernameHistory.SingleAsync(h => h.UserID == user.ID);
		Assert.Equal(("Before", (int?)7, "upstream:prod"), (history.Username, history.NameTag, history.ChangedBy));

		// the same copy again changes nothing and writes no history
		Assert.Empty(await DisplayNames.ApplyUpstream(db, user.ID, "After", "42", "upstream:prod"));
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

		IReadOnlyList<NameChange> changes = await DisplayNames.ApplyUpstream(db, user.ID, "shared", "5", "upstream:prod");

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
	public async Task UpstreamCopyMovesAStaleLookAlikeHolder(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		await using AppDbContext db = probe.Open();
		User stale = await AddUser(db, "B0b", 5);
		User user = await AddUser(db, "Other", 9);

		IReadOnlyList<NameChange> changes = await DisplayNames.ApplyUpstream(db, user.ID, "Bob", "5", "upstream:prod");

		Assert.Equal(2, changes.Count);
		db.ChangeTracker.Clear();
		User copied = await db.Users.SingleAsync(u => u.ID == user.ID);
		User movedStale = await db.Users.SingleAsync(u => u.ID == stale.ID);
		Assert.Equal(("Bob", 5), (copied.Username, copied.NameTag));
		Assert.Equal("B0b", movedStale.Username);
		Assert.NotEqual(5, movedStale.NameTag);
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
		Assert.False(await DisplayNames.TryAssignUpstream(db, clash, "He1d", "8"));
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
			Assert.Empty(await DisplayNames.ApplyUpstream(db, user.ID, name, tag, "upstream:prod"));
		}

		db.ChangeTracker.Clear();
		User stored = await db.Users.SingleAsync(u => u.ID == user.ID);
		Assert.Equal(("Steady", 3), (stored.Username, stored.NameTag));
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
		await Assert.ThrowsAsync<NameTagsExhaustedException>(() => DisplayNames.FreeTag(db, "Fu11"));
	}

	// Self-hosted sign-up: a look-alike of a name never gets a tag that name holds
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task SignUpGivesALookAlikeAnotherTag(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		await using AppDbContext db = probe.Open();

		DateTime now = DateTime.UtcNow;
		db.Users.AddRange(Enumerable.Range(DisplayNames.MinTag, DisplayNames.MaxTag - 1).Select(tag =>
			new User { Username = "Bob", UsernameNormalized = "BOB", NameTag = tag, CreatedAt = now, UpdatedAt = now }));
		await db.SaveChangesAsync();
		db.ChangeTracker.Clear();

		User lookAlike = await Users.Create(db, "B0b");
		Assert.Equal((DisplayNames.MaxTag, "bob", "B0B"), (lookAlike.NameTag, lookAlike.NameKey, lookAlike.UsernameNormalized));
	}

	private static async Task<long> Scalar(AppDbContext db, string sql)
	{
		await db.Database.OpenConnectionAsync();
		await using System.Data.Common.DbCommand command = db.Database.GetDbConnection().CreateCommand();
		command.CommandText = sql;
		return Convert.ToInt64(await command.ExecuteScalarAsync());
	}

	private static Task<long> UniqueKeyIndexColumns(AppDbContext db, string provider)
	{
		return Scalar(db, provider == "postgres"
			? "SELECT COUNT(*) FROM pg_indexes WHERE tablename = 'users' AND indexname = 'ux_users_name_key_name_tag' AND indexdef LIKE 'CREATE UNIQUE INDEX%(name_key, name_tag)'"
			: "SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'users' AND index_name = 'ux_users_name_key_name_tag' AND non_unique = 0");
	}

	// Rows from before the NameKeys migration: look-alikes on one tag are reported and keep their placeholders while every
	// other row is keyed, and after an operator renames one the rest are keyed and the unique index holds them
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheKeyBackfillReportsLookAlikesAndKeysTheRest(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider, migrate: false);
		if (probe == null) return;
		await using AppDbContext db = probe.Open();
		IMigrator migrator = db.GetService<IMigrator>();

		List<string> migrations = db.Database.GetMigrations().ToList();
		await migrator.MigrateAsync(migrations[migrations.FindIndex(m => m.EndsWith("_NameKeys", StringComparison.Ordinal)) - 1]);
		foreach ((string name, int tag) in new[] { ("Bob", 42), ("B0b", 42), ("Rob", 42), ("Ann", 7) })
		{
			await db.Database.ExecuteSqlRawAsync(
				"INSERT INTO users (username, username_normalized, name_tag, security_stamp, state, two_factor_enabled, failed_sign_in_count, created_at, updated_at) "
				+ "VALUES ({0}, {1}, {2}, 'x', 'active', FALSE, 0, {3}, {3})", name, NameNormalizer.Normalize(name), tag, DateTime.UtcNow);
		}
		await migrator.MigrateAsync();
		Assert.NotEqual(0, await UniqueKeyIndexColumns(db, provider));

		db.ChangeTracker.Clear();
		var before = await db.Users.AsNoTracking().OrderBy(u => u.ID).Select(u => new { u.ID, u.Username, u.NameTag, u.NameKey }).ToListAsync();
		Assert.All(before, u => Assert.Equal(NameKeyBackfill.Placeholder(u.ID), u.NameKey));
		Int64 bob = before.Single(u => u.Username == "Bob").ID;
		Int64 b0b = before.Single(u => u.Username == "B0b").ID;

		StringWriter output = new();
		StringWriter error = new();
		Assert.Equal(NameKeyBackfill.CollisionExitCode, await NameKeyBackfill.RunCommand(db, output, error));
		Assert.Contains($"tag 0042, key \"bob\": user {bob} \"Bob\", user {b0b} \"B0b\"", error.ToString());
		Assert.DoesNotContain("Rob", error.ToString());
		Assert.Contains("Computed the name key of 2 other user(s).", error.ToString());

		// names and tags stay, the colliding pair keeps its placeholders and every other user is keyed
		db.ChangeTracker.Clear();
		var after = await db.Users.AsNoTracking().OrderBy(u => u.ID).Select(u => new { u.ID, u.Username, u.NameTag, u.NameKey }).ToListAsync();
		Assert.Equal(before.Select(u => (u.ID, u.Username, u.NameTag)), after.Select(u => (u.ID, u.Username, u.NameTag)));
		Assert.Equal(new Dictionary<string, string>
		{
			["Bob"] = NameKeyBackfill.Placeholder(bob),
			["B0b"] = NameKeyBackfill.Placeholder(b0b),
			["Rob"] = "rob",
			["Ann"] = "ann"
		}, after.ToDictionary(u => u.Username, u => u.NameKey));

		// the operator renames one of the pair, and the next migrate run keys every row
		await db.Database.ExecuteSqlRawAsync("UPDATE users SET username = 'Bert', username_normalized = 'BERT' WHERE id = {0}", b0b);
		output = new StringWriter();
		Assert.Equal(0, await NameKeyBackfill.RunCommand(db, output, new StringWriter()));
		Assert.Contains("Computed the name key of 2 user(s).", output.ToString());
		db.ChangeTracker.Clear();
		Dictionary<string, string> keys = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Username, u => u.NameKey);
		Assert.Equal(new Dictionary<string, string> { ["Bob"] = "bob", ["Bert"] = "bert", ["Rob"] = "rob", ["Ann"] = "ann" }, keys);
		Assert.NotEqual(0, await UniqueKeyIndexColumns(db, provider));

		output = new StringWriter();
		Assert.Equal(0, await NameKeyBackfill.RunCommand(db, output, new StringWriter()));
		Assert.Contains("Name keys are up to date.", output.ToString());
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
