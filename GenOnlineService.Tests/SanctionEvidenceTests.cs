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

using System.Collections.Concurrent;
using Database;
using GenOnlineService.Service;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GenOnlineService.Tests;

// The match data bucket in memory, with a count of each operation
public sealed class MemoryMatchDataStore : IMatchDataStore
{
	public ConcurrentDictionary<string, StoredObject> Objects { get; } = new(StringComparer.Ordinal);
	public int Puts;
	public int Deletes;

	public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default) =>
		Task.FromResult<IReadOnlyList<string>>(Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToList());

	public Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken = default) =>
		Task.FromResult(Objects.TryGetValue(key, out StoredObject? value) ? value : null);

	public Task PutAsync(string key, StoredObject value, CancellationToken cancellationToken = default)
	{
		Interlocked.Increment(ref Puts);
		Objects[key] = value;
		return Task.CompletedTask;
	}

	public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
	{
		Interlocked.Increment(ref Deletes);
		Objects.TryRemove(key, out _);
		return Task.CompletedTask;
	}
}

// Privacy Policy: a screenshot a sanction relies on is kept until 90 days after that sanction ends
public class SanctionEvidenceTests
{
	private const long Player = 7, Other = 8, MatchID = 900001;
	private static readonly ModerationActor Staff = new() { Kind = AuditActorKinds.Cli, UserID = 1, Name = "cli:user:1", Source = ModerationSources.Cli };

	private static string Folder(long userID, DateTime day) =>
		$"screenshots/{day.Year}/{day.Month}/{day.Day}/match_{MatchID}/user_{Helpers.ComputeMD5Hash($"{MatchID}_{userID}")}/";

	private static async Task<long> Seed(AppDbContext db, DateTime matchStart, Sanction sanction)
	{
		db.Matches.Add(new Match { Id = MatchID, Name = "evidence", MapName = "Tournament Desert", RosterType = "1v1", StartedAt = matchStart });
		db.MatchPlayers.AddRange(new MatchPlayer { MatchId = MatchID, Slot = 0, UserId = Player }, new MatchPlayer { MatchId = MatchID, Slot = 1, UserId = Other });
		db.Sanctions.Add(sanction);
		await db.SaveChangesAsync();
		return sanction.ID;
	}

	private static StoredObject Jpeg(string text) => new(System.Text.Encoding.UTF8.GetBytes(text), "image/jpeg");

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task HoldingCopiesThePlayersScreenshotsOfTheMatchToEvidence(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		DateTime now = DateTime.UtcNow;
		DateTime matchStart = new DateTime(2026, 11, 1, 0, 30, 0, DateTimeKind.Utc);
		var store = new MemoryMatchDataStore();
		// the lobby was created the day before the match started, so that is the folder of the key
		string lobbyDay = Folder(Player, matchStart.AddDays(-1));
		store.Objects[lobbyDay + "screenshot_loadscreen_23_59.jpg"] = Jpeg("load");
		store.Objects[Folder(Player, matchStart) + "screenshot_scorescreen_0_45.jpg"] = Jpeg("score");
		store.Objects[Folder(Other, matchStart) + "screenshot_scorescreen_0_45.jpg"] = Jpeg("other player");
		long sanctionID;

		await using (AppDbContext db = probe.Open())
		{
			sanctionID = await Seed(db, matchStart, new Sanction { UserID = Player, Type = SanctionTypes.Ban, Reason = "maphack", CreatedAt = now });
		}

		await using (AppDbContext db = probe.Open())
		{
			EvidenceResult result = await SanctionEvidenceStore.HoldMatchScreenshots(db, store, sanctionID, MatchID, Staff, now);
			Assert.Equal(EvidenceOutcome.Held, result.Outcome);
			Assert.Equal(2, result.Held.Count);
			Assert.All(result.Held, k => Assert.StartsWith("evidence/", k));
		}

		// the copies are fresh objects with the same bytes, the other player's screenshot is not taken
		Assert.Equal("load", System.Text.Encoding.UTF8.GetString(store.Objects["evidence/" + lobbyDay.Substring("screenshots/".Length) + "screenshot_loadscreen_23_59.jpg"].Bytes));
		Assert.Equal(2, store.Objects.Keys.Count(k => k.StartsWith("evidence/")));
		Assert.Equal(2, store.Puts);

		await using (AppDbContext db = probe.Open())
		{
			Assert.Equal(2, await db.SanctionEvidence.CountAsync(e => e.SanctionID == sanctionID && e.UserID == Player));
			AuditLogEntry audit = Assert.Single(await db.AuditLog.ToListAsync());
			Assert.Equal(ModerationActions.SanctionEvidence, audit.Action);
			Assert.Equal(Player, audit.TargetID);
			using var details = System.Text.Json.JsonDocument.Parse(audit.Details!);
			Assert.Equal(sanctionID, details.RootElement.GetProperty("sanction_id").GetInt64());
			Assert.Equal(2, details.RootElement.GetProperty("files").GetInt32());
		}

		// holding again copies nothing twice
		await using (AppDbContext db = probe.Open())
		{
			EvidenceResult again = await SanctionEvidenceStore.HoldMatchScreenshots(db, store, sanctionID, MatchID, Staff, now);
			Assert.Equal(2, again.Held.Count);
			Assert.Equal(2, store.Puts);
			Assert.Equal(2, await db.SanctionEvidence.CountAsync());
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task HoldingIsRefusedWhenNothingCanBeHeld(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		DateTime now = DateTime.UtcNow;
		var store = new MemoryMatchDataStore();
		long active, longEnded;

		await using (AppDbContext db = probe.Open())
		{
			active = await Seed(db, now.AddDays(-1), new Sanction { UserID = Player, Type = SanctionTypes.Ban, CreatedAt = now });
			var ended = new Sanction { UserID = Player, Type = SanctionTypes.Mute, CreatedAt = now.AddDays(-200), ExpiresAt = now.AddDays(-91) };
			var otherPlayer = new Sanction { UserID = 99, Type = SanctionTypes.Ban, CreatedAt = now };
			db.Sanctions.AddRange(ended, otherPlayer);
			await db.SaveChangesAsync();
			longEnded = ended.ID;

			Assert.Equal(EvidenceOutcome.SanctionNotFound, (await SanctionEvidenceStore.HoldMatchScreenshots(db, store, 424242, MatchID, Staff, now)).Outcome);
			Assert.Equal(EvidenceOutcome.HoldOver, (await SanctionEvidenceStore.HoldMatchScreenshots(db, store, longEnded, MatchID, Staff, now)).Outcome);
			Assert.Equal(EvidenceOutcome.MatchNotFound, (await SanctionEvidenceStore.HoldMatchScreenshots(db, store, active, 1, Staff, now)).Outcome);
			Assert.Equal(EvidenceOutcome.NotInMatch, (await SanctionEvidenceStore.HoldMatchScreenshots(db, store, otherPlayer.ID, MatchID, Staff, now)).Outcome);
			Assert.Equal(EvidenceOutcome.StoreUnavailable, (await SanctionEvidenceStore.HoldMatchScreenshots(db, null, active, MatchID, Staff, now)).Outcome);
		}

		await using (AppDbContext db = probe.Open())
		{
			// a refusal writes nothing
			Assert.Equal(0, await db.SanctionEvidence.CountAsync());
			Assert.Equal(0, await db.AuditLog.CountAsync());
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task CopiesGoNinetyDaysAfterTheSanctionEndedUnlessAnotherSanctionHoldsThem(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		DateTime now = DateTime.UtcNow;
		var store = new MemoryMatchDataStore();
		const string shared = "evidence/shared.jpg", endedLong = "evidence/ended-long.jpg", endedRecent = "evidence/ended-recent.jpg",
			running = "evidence/running.jpg", orphan = "evidence/orphan.jpg";
		foreach (string key in new[] { shared, endedLong, endedRecent, running, orphan })
		{
			store.Objects[key] = Jpeg(key);
		}

		await using (AppDbContext db = probe.Open())
		{
			var liftedLong = new Sanction { UserID = Player, Type = SanctionTypes.Ban, CreatedAt = now.AddDays(-300), LiftedAt = now.AddDays(-91) };
			var expiredRecent = new Sanction { UserID = Player, Type = SanctionTypes.Mute, CreatedAt = now.AddDays(-100), ExpiresAt = now.AddDays(-89) };
			var permanent = new Sanction { UserID = Player, Type = SanctionTypes.Ban, CreatedAt = now.AddDays(-400) };
			db.Sanctions.AddRange(liftedLong, expiredRecent, permanent);
			await db.SaveChangesAsync();

			SanctionEvidence Row(long sanctionID, string key) => new() { SanctionID = sanctionID, UserID = Player, MatchID = MatchID, ObjectKey = key, CreatedAt = now.AddDays(-300) };
			db.SanctionEvidence.AddRange(
				Row(liftedLong.ID, endedLong),     // goes with its object
				Row(liftedLong.ID, shared),        // the row goes, the object stays: the permanent ban still holds it
				Row(permanent.ID, shared),
				Row(expiredRecent.ID, endedRecent), // ended 89 days ago: stays
				Row(permanent.ID, running),
				Row(424242, orphan));              // its sanction was purged: goes
			await db.SaveChangesAsync();
		}

		await using (AppDbContext db = probe.Open())
		{
			// without the bucket nothing is removed
			Assert.Equal(0, await SanctionEvidenceStore.PurgeExpired(db, null, now));
			Assert.Equal(3, await SanctionEvidenceStore.PurgeExpired(db, store, now));
		}

		Assert.Equal(new[] { endedRecent, running, shared }, store.Objects.Keys.OrderBy(k => k, StringComparer.Ordinal));
		await using (AppDbContext db = probe.Open())
		{
			Assert.Equal(3, await db.SanctionEvidence.CountAsync());
			Assert.Equal(0, await SanctionEvidenceStore.PurgeExpired(db, store, now));
			// the purge never touches sanctions
			Assert.Equal(3, await db.Sanctions.CountAsync());
		}
	}
}
