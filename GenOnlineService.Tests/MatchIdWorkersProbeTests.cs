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

using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GenOnlineService.Tests;

// The match id worker slots and snowflake match ids against a real database (GENONLINE_PROBE_POSTGRES / GENONLINE_PROBE_MARIADB)
public class MatchIdWorkersProbeTests
{
	private const int Window = 60;

	private static async Task<MatchIdSlot?> Acquire(ProbeDatabase probe, string holder, long nowSecond)
	{
		await using AppDbContext db = probe.Open();
		return await Database.MatchIdWorkers.Acquire(db, holder, nowSecond, Window);
	}

	private static async Task<bool> Extend(ProbeDatabase probe, MatchIdSlot slot, long reservedUntil)
	{
		await using AppDbContext db = probe.Open();
		return await Database.MatchIdWorkers.Extend(db, slot, reservedUntil);
	}

	private static async Task<MatchIdWorker> Row(ProbeDatabase probe, int worker)
	{
		await using AppDbContext db = probe.Open();
		return await db.MatchIdWorkers.AsNoTracking().SingleAsync(w => w.WorkerID == worker);
	}

	// Every slot but the first reserved far ahead, so the tests below compete for one slot
	private static async Task ReserveAllButFirst(ProbeDatabase probe)
	{
		await using AppDbContext db = probe.Open();
		await db.MatchIdWorkers.Where(w => w.WorkerID != 0).ExecuteUpdateAsync(s => s.SetProperty(w => w.ReservedUntil, long.MaxValue / 2));
	}

	private static Match NewMatch(string name, long id = 0) => new Match
	{
		Id = id,
		Name = name,
		MapName = "Tournament Desert",
		RosterType = "1v1",
		StartedAt = DateTime.UtcNow,
	};

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheMigrationSeedsEveryWorkerSlotFree(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		Assert.NotNull(probe);

		await using AppDbContext db = probe.Open();
		List<MatchIdWorker> rows = await db.MatchIdWorkers.AsNoTracking().OrderBy(w => w.WorkerID).ToListAsync();

		Assert.Equal(Enumerable.Range(0, MatchIdLayout.WorkerCount), rows.Select(w => w.WorkerID));
		Assert.All(rows, w => Assert.Equal((String.Empty, 0L, 0L), (w.Holder, w.Generation, w.ReservedUntil)));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task EachCopyTakesItsOwnSlot(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		Assert.NotNull(probe);

		MatchIdSlot? a = await Acquire(probe, "copy-a", 1_000);
		MatchIdSlot? b = await Acquire(probe, "copy-b", 1_000);

		Assert.NotNull(a);
		Assert.NotNull(b);
		Assert.NotEqual(a.WorkerID, b.WorkerID);
		Assert.Equal(new MatchIdSlot(a.WorkerID, 1, 1_000, 1_000 + Window), a);

		MatchIdWorker row = await Row(probe, a.WorkerID);
		Assert.Equal(("copy-a", 1L, 1_000L + Window), (row.Holder, row.Generation, row.ReservedUntil));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AReservedSlotIsTakenOnlyAfterItsReservationAndTheOldHolderCanNoLongerExtendIt(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		Assert.NotNull(probe);
		await ReserveAllButFirst(probe);

		MatchIdSlot? a = await Acquire(probe, "copy-a", 1_000);
		Assert.NotNull(a);
		Assert.Equal(0, a.WorkerID);

		// while copy A's reservation runs nobody else gets a slot, and copy A extends its own
		Assert.Null(await Acquire(probe, "copy-b", 1_000 + Window));
		Assert.True(await Extend(probe, a, 1_000 + 2 * Window));
		Assert.Null(await Acquire(probe, "copy-b", 1_000 + 2 * Window));

		// after it copy B takes the slot with a new generation, copy A's extension is refused
		MatchIdSlot? b = await Acquire(probe, "copy-b", 1_000 + 2 * Window + 1);
		Assert.NotNull(b);
		Assert.Equal((0, 2L), (b.WorkerID, b.Generation));
		Assert.True(b.FirstSecond > 1_000 + 2 * Window);
		Assert.False(await Extend(probe, a, 1_000 + 3 * Window));

		MatchIdWorker row = await Row(probe, 0);
		Assert.Equal(("copy-b", 2L, b.ReservedUntil), (row.Holder, row.Generation, row.ReservedUntil));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ATakeoverReadBeforeTheHolderExtendedIsRefused(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		Assert.NotNull(probe);
		await ReserveAllButFirst(probe);

		// copy A holds slot 0 until 1_000 + Window and made no id in its last second
		MatchIdSlot? a = await Acquire(probe, "copy-a", 1_000);
		Assert.NotNull(a);
		long now = 1_000 + Window + 1;

		// copy B reads slot 0 as free, then copy A extends it before copy B's update runs
		MatchIdWorker stale = await Row(probe, 0);
		Assert.True(stale.ReservedUntil < now);
		Assert.True(await Extend(probe, a, now + Window));

		await using (AppDbContext db = probe.Open())
		{
			Assert.Null(await Database.MatchIdWorkers.TryTake(db, stale, "copy-b", now, Window));
		}

		MatchIdWorker row = await Row(probe, 0);
		Assert.Equal(("copy-a", a.Generation, now + Window), (row.Holder, row.Generation, row.ReservedUntil));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnExtensionNeverMovesTheReservationBack(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		Assert.NotNull(probe);

		MatchIdSlot? a = await Acquire(probe, "copy-a", 1_000);
		Assert.NotNull(a);

		Assert.False(await Extend(probe, a, 1_000));
		Assert.Equal(1_000 + Window, (await Row(probe, a.WorkerID)).ReservedUntil);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task MatchesStoreSnowflakeIdsNextToSequentialOnes(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		Assert.NotNull(probe);

		// a match written before the switch keeps its sequential id
		long sequential;
		await using (AppDbContext db = probe.Open())
		{
			Match old = NewMatch("before the switch");
			db.Matches.Add(old);
			await db.SaveChangesAsync();
			sequential = old.Id;
		}

		MatchIdGenerator generator = new MatchIdGenerator(new DatabaseMatchIdWorkerStore(new ProbeFactory(probe)), TimeProvider.System, "probe");
		long[] snowflakes = { await generator.NextAsync(), await generator.NextAsync() };
		await using (AppDbContext db = probe.Open())
		{
			db.Matches.AddRange(snowflakes.Select((id, i) => NewMatch($"snowflake {i}", id)));
			await db.SaveChangesAsync();
		}

		await using (AppDbContext db = probe.Open())
		{
			List<long> newestFirst = await db.Matches.AsNoTracking().OrderByDescending(m => m.Id).Select(m => m.Id).ToListAsync();
			Assert.Equal(new[] { snowflakes[1], snowflakes[0], sequential }, newestFirst);
			Assert.True(sequential < snowflakes[0]);
		}

		if (provider == "postgres")
		{
			// an older copy that still lets the database pick the id gets the next sequence value, never a snowflake
			await using AppDbContext db = probe.Open();
			Match fromOldCopy = NewMatch("old copy after the switch");
			db.Matches.Add(fromOldCopy);
			await db.SaveChangesAsync();
			Assert.Equal(sequential + 1, fromOldCopy.Id);
		}
	}
}
