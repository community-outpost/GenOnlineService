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

// The realtime lease statements against a real database (GENONLINE_PROBE_POSTGRES): take, renew, loss, epoch fencing, handover and take back.
// More than one service copy is supported on PostgreSQL only, so the lease runs there alone.
public class LeaseProbeTests
{
	public static IEnumerable<object[]> Postgres()
	{
		yield return new object[] { "postgres" };
	}

	private const int Lease = 400;

	private static async Task<RealtimeLease> Row(ProbeDatabase probe)
	{
		await using AppDbContext db = probe.Open();
		return (await Database.Lease.Read(db))!;
	}

	private static async Task<long?> Take(ProbeDatabase probe, string copy, int leaseMs = Lease)
	{
		await using AppDbContext db = probe.Open();
		return await Database.Lease.TryTake(db, copy, leaseMs);
	}

	private static async Task<bool> Renew(ProbeDatabase probe, string copy, long epoch, int leaseMs = Lease)
	{
		await using AppDbContext db = probe.Open();
		return await Database.Lease.Renew(db, copy, epoch, leaseMs);
	}

	private static async Task Announce(ProbeDatabase probe, string copy, int leaseMs = Lease)
	{
		await using AppDbContext db = probe.Open();
		await Database.Lease.AnnounceStandby(db, copy, leaseMs);
	}

	private static async Task<Database.Lease.Handover?> HandOver(ProbeDatabase probe, string copy, long epoch, int leaseMs = Lease)
	{
		await using AppDbContext db = probe.Open();
		return await Database.Lease.HandOver(db, copy, epoch, leaseMs);
	}

	[ProbeTheory]
	[MemberData(nameof(Postgres))]
	public async Task TheFirstCopyTakesTheMigratedRowAndOnlyTheHolderRenews(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		RealtimeLease seeded = await Row(probe);
		Assert.Equal((String.Empty, 0L), (seeded.Holder, seeded.Epoch));

		Assert.Equal(1L, await Take(probe, "a"));
		Assert.Null(await Take(probe, "b"));

		RealtimeLease taken = await Row(probe);
		await using (AppDbContext db = probe.Open())
		{
			DateTime now = await Database.Lease.DatabaseNow(db);
			Assert.InRange((taken.ExpiresAt - now).TotalMilliseconds, 0, Lease + 50);
		}

		Assert.True(await Renew(probe, "a", 1, 5000));
		Assert.True((await Row(probe)).ExpiresAt > taken.ExpiresAt);
		Assert.False(await Renew(probe, "b", 1));
		Assert.False(await Renew(probe, "a", 2));
	}

	[ProbeTheory]
	[MemberData(nameof(Postgres))]
	public async Task AnExpiredLeaseGoesToTheStandbyWithTheNextEpochAndFencesTheOldHolder(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		Assert.Equal(1L, await Take(probe, "a"));
		await Announce(probe, "b");
		Assert.Equal("b", (await Row(probe)).Standby);

		await Task.Delay(Lease + 200);
		Assert.Equal(2L, await Take(probe, "b"));

		RealtimeLease row = await Row(probe);
		Assert.Equal(("b", 2L, (string?)null), (row.Holder, row.Epoch, row.Standby));
		Assert.False(await Renew(probe, "a", 1));
		Assert.True(await Renew(probe, "b", 2));
	}

	[ProbeTheory]
	[MemberData(nameof(Postgres))]
	public async Task TwoCopiesRacingForAnExpiredLeaseLetExactlyOneWin(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		for (int round = 1; round <= 10; round++)
		{
			long before = (await Row(probe)).Epoch;
			long?[] results = await Task.WhenAll(
				Task.Run(() => Take(probe, "a", 60_000)),
				Task.Run(() => Take(probe, "b", 60_000)));

			long winner = Assert.Single(results, r => r != null)!.Value;
			Assert.Equal(before + 1, winner);
			RealtimeLease row = await Row(probe);
			Assert.Equal(before + 1, row.Epoch);
			Assert.Equal(results[0] != null ? "a" : "b", row.Holder);

			// expire it again for the next round
			await using AppDbContext db = probe.Open();
			Assert.True(await Database.Lease.Release(db, row.Holder, row.Epoch));
		}
	}

	[ProbeTheory]
	[MemberData(nameof(Postgres))]
	public async Task AHandoverNeedsALiveStandbyAndRaisesTheEpoch(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		Assert.Equal(1L, await Take(probe, "a", 60_000));
		await using (AppDbContext db = probe.Open())
		{
			Assert.False(await Database.Lease.RequestHandover(db));
		}
		Assert.Null(await HandOver(probe, "a", 1));

		// a standby whose announcement expired does not count
		await Announce(probe, "b", 100);
		await Task.Delay(300);
		Assert.Null(await HandOver(probe, "a", 1));

		await Announce(probe, "b", 60_000);
		await using (AppDbContext db = probe.Open())
		{
			Assert.True(await Database.Lease.RequestHandover(db));
		}
		Assert.NotNull((await Row(probe)).HandoverRequestedAt);

		// only the holder with its own epoch hands over
		Assert.Null(await HandOver(probe, "b", 1));
		Assert.Null(await HandOver(probe, "a", 7));
		Database.Lease.Handover? handover = await HandOver(probe, "a", 1, 60_000);
		Assert.NotNull(handover);
		Assert.Equal(("b", 2L), (handover!.Target, handover.Epoch));

		RealtimeLease row = await Row(probe);
		Assert.Equal(("b", 2L, (string?)null, (DateTime?)null), (row.Holder, row.Epoch, row.Standby, row.HandoverRequestedAt));
		Assert.False(await Renew(probe, "a", 1));
		Assert.True(await Renew(probe, "b", 2));

		// the holder cannot announce itself as standby
		await Announce(probe, "b");
		Assert.Null((await Row(probe)).Standby);
	}

	[ProbeTheory]
	[MemberData(nameof(Postgres))]
	public async Task AHandedLeaseIsTakenBackOnlyWhileTheTargetNeverRenewedOrLetItLapse(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		Assert.Equal(1L, await Take(probe, "a", 60_000));
		await Announce(probe, "b", 60_000);
		Database.Lease.Handover handover = (await HandOver(probe, "a", 1, 60_000))!;

		// the target never renewed: the old holder takes it back with the next epoch, the target is fenced
		await using (AppDbContext db = probe.Open())
		{
			Assert.Null(await Database.Lease.Reclaim(db, "a", handover with { Epoch = 7 }, Lease));
			Assert.Equal(3L, await Database.Lease.Reclaim(db, "a", handover, 60_000));
		}
		Assert.Equal(("a", 3L), ((await Row(probe)).Holder, (await Row(probe)).Epoch));
		Assert.False(await Renew(probe, "b", 2));

		// a target that renewed keeps the lease
		await Announce(probe, "b", 60_000);
		handover = (await HandOver(probe, "a", 3, 60_000))!;
		Assert.True(await Renew(probe, "b", 4, 60_000));
		await using (AppDbContext db = probe.Open())
		{
			Assert.Null(await Database.Lease.Reclaim(db, "a", handover, 60_000));
		}
		Assert.Equal("b", (await Row(probe)).Holder);

		// once its lease lapses the old holder takes it back
		Assert.True(await Renew(probe, "b", 4, 100));
		await Task.Delay(300);
		await using (AppDbContext db = probe.Open())
		{
			Assert.Equal(5L, await Database.Lease.Reclaim(db, "a", handover, 60_000));
		}
	}

	[ProbeTheory]
	[MemberData(nameof(Postgres))]
	public async Task AReleasedLeaseIsTakenAtOnce(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		Assert.Equal(1L, await Take(probe, "a", 60_000));
		await using (AppDbContext db = probe.Open())
		{
			Assert.False(await Database.Lease.Release(db, "a", 5));
			Assert.False(await Database.Lease.Release(db, "b", 1));
			Assert.True(await Database.Lease.Release(db, "a", 1));
		}
		Assert.Equal(2L, await Take(probe, "b"));
	}
}
