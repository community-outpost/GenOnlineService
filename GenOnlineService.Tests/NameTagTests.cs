/*
**    GeneralsOnline Game Services - Backend Services for Command & Conquer Generals Online: Zero Hour
**    Copyright (C) 2025  GeneralsOnline Development Team
**
**    This program is free software: you can redistribute it and/or modify
**    it under the terms of the GNU Affero General Public License as
**    published by the Free Software Foundation, either version 3 of the
**    License, or (at your option) any later version.
**
**    This program is distributed in the hope that it will be useful,
**    but WITHOUT ANY WARRANTY; without even the implied warranty of
**    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
**    GNU Affero General Public License for more details.
**
**    You should have received a copy of the GNU Affero General Public License
**    along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using Database;
using GenOnlineService.Names;
using GenOnlineService.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using System.Runtime.CompilerServices;
using Xunit;

namespace GenOnlineService.Tests;

// Every test in this assembly derives tags with one fixed key
internal static class NameTagsTestKey
{
	public static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 1)).ToArray();

#pragma warning disable CA2255
	[ModuleInitializer]
#pragma warning restore CA2255
	internal static void Use() => NameTags.UseKey(Key);
}

// Tags follow the name (S11): a player's tag for a name is deterministic, and every name#tag they held stays theirs
public class NameTagTests
{
	private static readonly Database.ModerationActor s_bot = new() { Kind = AuditActorKinds.Discord, Name = "discord:99", Source = ModerationSources.Discord };

	private static List<int> Sequence(Int64 userID, string name, int count = 8) => NameTags.Candidates(userID, NameKey.Of(name)).Take(count).ToList();

	private static async Task<User> AddUser(ProbeDatabase probe, string name, int tag)
	{
		await using AppDbContext db = probe.Open();
		var user = new User { Username = name, UsernameNormalized = NameNormalizer.Normalize(name), NameTag = tag, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user;
	}

	private static async Task<(string Name, int Tag)> Read(ProbeDatabase probe, Int64 userID)
	{
		await using AppDbContext db = probe.Open();
		User user = await db.Users.SingleAsync(u => u.ID == userID);
		return (user.Username, user.NameTag);
	}

	private static async Task Rename(PlayerNames names, Int64 userID, string name)
	{
		Result<Empty> result = await names.RenameSelfAsync(userID, name);
		Assert.True(result.Ok, result.Code);
	}

	[Fact]
	public void LookAlikesShareTheSequenceAndPlayersHaveTheirOwn()
	{
		List<int> bob = Sequence(5, "Bob", 20);
		Assert.Equal(bob, Sequence(5, "B0b", 20));
		Assert.Equal(bob, Sequence(5, "BOB", 20));
		Assert.Equal(bob, Sequence(5, "Bob", 20));
		Assert.NotEqual(bob, Sequence(6, "Bob", 20));
		Assert.NotEqual(bob, Sequence(5, "Rob", 20));
		Assert.Equal(20, bob.Distinct().Count());
	}

	// rejection sampling maps into the whole range: a long enough sequence reaches every tag exactly once
	[Fact]
	public void TheSequenceReachesEveryTagOnce()
	{
		List<int> all = NameTags.Candidates(42, "bob").Take(DisplayNames.MaxTag - DisplayNames.MinTag + 1).ToList();
		Assert.Equal(Enumerable.Range(DisplayNames.MinTag, DisplayNames.MaxTag), all.Order());
	}

	[Fact]
	public void ADevelopmentKeyIsFixedAndEveryOtherEnvironmentNeedsOne()
	{
		IConfiguration empty = new ConfigurationBuilder().Build();
		InvalidOperationException missing = Assert.Throws<InvalidOperationException>(() => NameTags.Read(empty, isDevelopment: false));
		Assert.Contains("Names:tag_key is not set", missing.Message);
		Assert.Equal(NameTags.Read(empty, isDevelopment: true), NameTags.Read(empty, isDevelopment: true));
		Assert.Equal(32, NameTags.Read(empty, isDevelopment: true).Length);

		IConfiguration Config(string value) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Names:tag_key"] = value }).Build();
		Assert.Contains("at least 32 bytes", Assert.Throws<InvalidOperationException>(() => NameTags.Read(Config(Convert.ToBase64String(new byte[31])), isDevelopment: true)).Message);
		Assert.Contains("not valid base64", Assert.Throws<InvalidOperationException>(() => NameTags.Read(Config("not base64!"), isDevelopment: false)).Message);
		byte[] key = Enumerable.Range(0, 48).Select(i => (byte)i).ToArray();
		Assert.Equal(key, NameTags.Read(Config(Convert.ToBase64String(key)), isDevelopment: false));
		Assert.DoesNotContain(Convert.ToBase64String(key), Assert.Throws<InvalidOperationException>(() => NameTags.Read(Config(Convert.ToBase64String(key[..20])), isDevelopment: false)).Message);
	}

	// a production host without the key stops at startup
	[Fact]
	public void StartupFailsInProductionWithoutTheKey()
	{
		var environment = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Production" };
		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => NameTags.Configure(new ConfigurationBuilder().Build(), environment));
		Assert.Contains("Names:tag_key", ex.Message);
	}

	// the tag follows the name, a look-alike keeps it, and the old name brings the old tag back
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ATagFollowsTheNameAndComesBackWithIt(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User user = await AddUser(probe, "Before", 7);
		PlayerNames names = new(new ProbeFactory(probe));
		int bobby = Sequence(user.ID, "Bobby")[0];

		await Rename(names, user.ID, "Bobby");
		Assert.Equal(("Bobby", bobby), await Read(probe, user.ID));
		await Rename(names, user.ID, "Before");
		Assert.Equal(("Before", 7), await Read(probe, user.ID));
		await Rename(names, user.ID, "BOBBY");
		Assert.Equal(("BOBBY", bobby), await Read(probe, user.ID));
		await Rename(names, user.ID, "B0bby");
		Assert.Equal(("B0bby", bobby), await Read(probe, user.ID));
		await Rename(names, user.ID, "Before");
		Assert.Equal(("Before", 7), await Read(probe, user.ID));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task PlayersWithOneNameGetTheirOwnTags(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User first = await AddUser(probe, "First", 1);
		User second = await AddUser(probe, "Second", 1);
		PlayerNames names = new(new ProbeFactory(probe));

		await Rename(names, first.ID, "Shared");
		await Rename(names, second.ID, "Shared");
		int firstTag = Sequence(first.ID, "Shared")[0];
		List<int> secondSequence = Sequence(second.ID, "Shared");
		Assert.Equal(("Shared", firstTag), await Read(probe, first.ID));
		Assert.Equal(("Shared", secondSequence.First(t => t != firstTag)), await Read(probe, second.ID));
	}

	// a look-alike that holds the player's first candidate moves the rename on to the second, it never fails
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AHeldCandidateMovesTheRenameToTheNext(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User user = await AddUser(probe, "Bert", 1);
		List<int> bob = Sequence(user.ID, "Bob");
		await AddUser(probe, "B0b", bob[0]);
		PlayerNames names = new(new ProbeFactory(probe));

		await Rename(names, user.ID, "Bob");
		Assert.Equal(("Bob", bob[1]), await Read(probe, user.ID));
	}

	// a pair another player held stays theirs even when it is this player's first candidate
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AReservedPairIsNeverGivenToAnotherPlayer(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User player = await AddUser(probe, "Bert", 1);
		List<int> gone = Sequence(player.ID, "Gone");
		User holder = await AddUser(probe, "Gone", gone[0]);
		PlayerNames names = new(new ProbeFactory(probe));

		await Rename(names, holder.ID, "Elsewhere");
		await Rename(names, player.ID, "G0ne");
		Assert.Equal(("G0ne", gone[1]), await Read(probe, player.ID));

		await Rename(names, player.ID, "Bert");
		await Rename(names, holder.ID, "Gone");
		Assert.Equal(("Gone", gone[0]), await Read(probe, holder.ID));
	}

	// a sign-up has no id yet, so it takes the first free candidate of user id 0 for the name
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task SignUpsTakeTheNamesCandidatesInOrder(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		List<int> fresh = Sequence(0, "Fresh");

		await using AppDbContext db = probe.Open();
		Assert.Equal(fresh[0], (await Users.Create(db, "Fresh")).NameTag);
		Assert.Equal(fresh[1], (await Users.Create(db, "FRESH")).NameTag);
		Assert.Equal(fresh[2], (await Users.Create(db, "fresh")).NameTag);
	}

	// two sign-ups on one name at once both want the first candidate: a unique index stops one, and it takes the next
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ConcurrentSignUpsOnOneNameBothGetATag(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		for (int round = 0; round < 5; round++)
		{
			string name = "Racer" + round;
			async Task<User> SignUp(string asTyped)
			{
				await using AppDbContext db = probe.Open();
				return await Users.Create(db, asTyped);
			}

			User[] created = await Task.WhenAll(Task.Run(() => SignUp(name)), Task.Run(() => SignUp(name.ToUpperInvariant())));
			Assert.Equal(Sequence(0, name, 2).Order(), created.Select(u => u.NameTag).Order());
		}
	}

	// two look-alike renames onto one candidate at once: both succeed, the unique index sends the loser to its next candidate
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ConcurrentRenamesOnOneCandidateBothSucceed(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		PlayerNames names = new(new ProbeFactory(probe));

		for (int round = 0; round < 3; round++)
		{
			User first = await AddUser(probe, "Anna", 1 + round * 2);
			User second = await AddUser(probe, "Bert", 2 + round * 2);
			// a name both players' sequences start with
			string name = Enumerable.Range(0, 1_000_000).Select(i => "Race" + i).First(n => Sequence(first.ID, n, 1)[0] == Sequence(second.ID, n, 1)[0]);
			int shared = Sequence(first.ID, name, 1)[0];

			Result<Empty>[] results = await Task.WhenAll(
				Task.Run(() => names.RenameSelfAsync(first.ID, name)),
				Task.Run(() => names.RenameSelfAsync(second.ID, name.ToUpperInvariant())));

			Assert.All(results, r => Assert.True(r.Ok, r.Code));
			int firstTag = (await Read(probe, first.ID)).Tag;
			int secondTag = (await Read(probe, second.ID)).Tag;
			Assert.Equal(new[] { shared, firstTag == shared ? Sequence(second.ID, name)[1] : Sequence(first.ID, name)[1] }.Order(), new[] { firstTag, secondTag }.Order());
		}
	}

	// choosing the name a player owes and a staff name reset take the player's candidates too
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ChoiceAndResetFollowTheName(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User user = await AddUser(probe, "Owing", 1);
		PlayerNames names = new(new ProbeFactory(probe));

		Result<Empty> chosen = await names.ChooseAsync(user.ID, "Chosen");
		Assert.True(chosen.Ok, chosen.Code);
		Assert.Equal(("Chosen", Sequence(user.ID, "Chosen")[0]), await Read(probe, user.ID));

		Result<Empty> reset = await names.ResetNameByStaffAsync(s_bot, user.ID, null, live: false);
		Assert.True(reset.Ok, reset.Code);
		(string placeholder, int tag) = await Read(probe, user.ID);
		Assert.True(DisplayNames.IsPlaceholderShaped(placeholder));
		Assert.Equal(Sequence(user.ID, placeholder)[0], tag);

		Assert.True((await names.ChooseAsync(user.ID, "Chosen")).Ok);
		Assert.Equal(("Chosen", Sequence(user.ID, "Chosen")[0]), await Read(probe, user.ID));
	}

	private static async Task<List<(Int64 UserID, string Name, string Key, int Tag)>> Reservations(ProbeDatabase probe)
	{
		await using AppDbContext db = probe.Open();
		return (await db.NameReservations.AsNoTracking().OrderBy(r => r.NameKey).ThenBy(r => r.NameTag).ToListAsync())
			.Select(r => (r.UserID, r.Name, r.NameKey, r.NameTag)).ToList();
	}

	// sign-up, rename and look-alike rename leave one reservation per pair the player held
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task EveryLocalNameReservesItsPair(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User user;
		await using (AppDbContext db = probe.Open())
		{
			user = await Users.Create(db, "Signed");
		}
		PlayerNames names = new(new ProbeFactory(probe));
		await Rename(names, user.ID, "Moved");
		await Rename(names, user.ID, "M0ved");
		int moved = Sequence(user.ID, "Moved")[0];

		Assert.Equal(new[] { (user.ID, "M0ved", NameKey.Of("Moved"), moved), (user.ID, "Signed", NameKey.Of("Signed"), Sequence(0, "Signed")[0]) }.OrderBy(r => r.Item3, StringComparer.Ordinal), await Reservations(probe));

		// the database refuses a second reservation of a pair, look-alikes included
		await using (AppDbContext db = probe.Open())
		{
			db.NameReservations.Add(new NameReservation { UserID = user.ID, Name = "Moved", NameTag = moved, HeldAt = DateTime.UtcNow });
			DbUpdateException ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
			Assert.True(DbHelpers.IsUniqueViolation(ex));
		}
	}

	// the follow-prod mirror copies an upstream pair even when another player has it reserved, and reserves nothing; the
	// reservation's owner then gets their next candidate while the copy holds the pair
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task UpstreamCopiesIgnoreReservations(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User owner = await AddUser(probe, "Owner", 1);
		List<int> mirror = Sequence(owner.ID, "Mirror");
		PlayerNames names = new(new ProbeFactory(probe));
		await Rename(names, owner.ID, "Mirror");
		await Rename(names, owner.ID, "Owner");
		User copy = await AddUser(probe, "Local", 1);
		var before = await Reservations(probe);

		await using (AppDbContext db = probe.Open())
		{
			IReadOnlyList<NameChange> changes = await DisplayNames.ApplyUpstream(db, copy.ID, "Mirror", DisplayNames.FormatTag(mirror[0]), "upstream");
			Assert.Equal(new NameChange(copy.ID, "Local", 1, "Mirror", mirror[0]), Assert.Single(changes));
		}
		Assert.Equal(("Mirror", mirror[0]), await Read(probe, copy.ID));
		Assert.Equal(before, await Reservations(probe));

		await Rename(names, owner.ID, "Mirror");
		Assert.Equal(("Mirror", mirror[1]), await Read(probe, owner.ID));
	}

	// existing players keep their names and tags; the migrate step reserves their current and past pairs, and a pair
	// several players held stays with the one holding it now, else the one who held it last
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheBackfillReservesCurrentAndPastPairs(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider, migrate: false);
		if (probe == null) return;
		Dictionary<string, Int64> ids = new();
		await using (AppDbContext db = probe.Open())
		{
			List<string> migrations = db.Database.GetMigrations().ToList();
			await db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync(migrations[migrations.FindIndex(m => m.EndsWith("_NameReservations", StringComparison.Ordinal)) - 1]);

			DateTime now = DateTime.UtcNow;
			foreach ((string name, int tag) in new[] { ("Ann", 5), ("Bob", 3), ("Carl", 9) })
			{
				User user = new() { Username = name, UsernameNormalized = NameNormalizer.Normalize(name), NameTag = tag, CreatedAt = now, UpdatedAt = now };
				db.Users.Add(user);
				await db.SaveChangesAsync();
				ids[name] = user.ID;
			}
			db.UserUsernameHistory.AddRange(
				new UserUsernameHistory { UserID = ids["Ann"], Username = "Annie", NameTag = 9, ChangedAt = now.AddDays(-3), ChangedBy = "cli" },
				new UserUsernameHistory { UserID = ids["Ann"], Username = "Untagged", NameTag = null, ChangedAt = now.AddDays(-9), ChangedBy = "cli" },
				new UserUsernameHistory { UserID = ids["Bob"], Username = "ANNIE", NameTag = 9, ChangedAt = now.AddDays(-1), ChangedBy = "cli" },
				new UserUsernameHistory { UserID = ids["Carl"], Username = "B0b", NameTag = 3, ChangedAt = now.AddDays(-2), ChangedBy = "cli" },
				new UserUsernameHistory { UserID = 999_999, Username = "Ghost", NameTag = 1, ChangedAt = now, ChangedBy = "cli" });
			await db.SaveChangesAsync();

			await db.Database.MigrateAsync();
			Assert.Empty(await db.NameReservations.ToListAsync());

			StringWriter output = new();
			await NameReservationBackfill.RunCommand(db, output);
			Assert.Contains("Reserved 4 name#tag pair(s) and removed 0 outdated reservation(s).", output.ToString());
			Assert.Contains("2 name#tag pair(s) were held by more than one user", output.ToString());
		}

		Assert.Equal(new[]
		{
			(ids["Ann"], "Ann", "ann", 5),
			(ids["Bob"], "ANNIE", "annie", 9),
			(ids["Bob"], "Bob", "bob", 3),
			(ids["Carl"], "Carl", "carl", 9)
		}, await Reservations(probe));
		Assert.Equal(("Ann", 5), await Read(probe, ids["Ann"]));
		Assert.Equal(("Bob", 3), await Read(probe, ids["Bob"]));
		Assert.Equal(("Carl", 9), await Read(probe, ids["Carl"]));

		// a second run changes nothing, and a stale key is rekeyed
		await using (AppDbContext db = probe.Open())
		{
			StringWriter output = new();
			await NameReservationBackfill.RunCommand(db, output);
			Assert.StartsWith("Name reservations are up to date.", output.ToString());

			await db.Database.ExecuteSqlRawAsync("UPDATE name_reservations SET name_key = 'stale' WHERE name_key = 'carl'");
			Assert.Equal(new NameReservationBackfill.Outcome(1, 1, 2), await NameReservationBackfill.Run(db));
			Assert.Equal(new NameReservationBackfill.Outcome(0, 0, 2), await NameReservationBackfill.Run(db));
		}

		// Bob held Annie#0009 last, so Ann gets her own candidate for it and Bob his old tag
		PlayerNames names = new(new ProbeFactory(probe));
		await Rename(names, ids["Ann"], "Annie");
		Assert.Equal(("Annie", Sequence(ids["Ann"], "Annie").First(t => t != 9)), await Read(probe, ids["Ann"]));
		await Rename(names, ids["Bob"], "Annie");
		Assert.Equal(("Annie", 9), await Read(probe, ids["Bob"]));
	}
}
