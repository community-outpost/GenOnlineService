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

	private static async Task<string?> Begin(ProbeDatabase probe, string copy, long epoch)
	{
		await using AppDbContext db = probe.Open();
		return await Database.Lease.BeginHandover(db, copy, epoch);
	}

	private static async Task<T> With<T>(ProbeDatabase probe, Func<AppDbContext, Task<T>> statement)
	{
		await using AppDbContext db = probe.Open();
		return await statement(db);
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
	public async Task AHandoverNeedsALiveStandbyAndKeepsTheLeaseOnTheHolderUntilItEnds(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		Assert.Equal(1L, await Take(probe, "a", 60_000));
		Assert.False(await With(probe, db => Database.Lease.RequestHandover(db)));
		Assert.Null(await Begin(probe, "a", 1));

		// a standby whose announcement expired does not count
		await Announce(probe, "b", 100);
		await Task.Delay(300);
		Assert.Null(await Begin(probe, "a", 1));

		await Announce(probe, "b", 60_000);
		Assert.True(await With(probe, db => Database.Lease.RequestHandover(db)));

		// only the holder with its own epoch starts it, the lease stays where it is
		Assert.Null(await Begin(probe, "b", 1));
		Assert.Null(await Begin(probe, "a", 7));
		Assert.Equal("b", await Begin(probe, "a", 1));
		RealtimeLease row = await Row(probe);
		Assert.Equal(("a", 1L, "b", (DateTime?)null, (DateTime?)null), (row.Holder, row.Epoch, row.HandoverTarget, row.HandoverConfirmedAt, row.HandoverRequestedAt));
		Assert.True(await Renew(probe, "a", 1, 60_000));

		// another standby never replaces the target, only the target confirms
		await Announce(probe, "c", 60_000);
		Assert.Equal("b", (await Row(probe)).Standby);
		Assert.False(await With(probe, db => Database.Lease.ConfirmHandover(db, "c")));
		Assert.False(await With(probe, db => Database.Lease.ConfirmHandover(db, "a")));
		Assert.Null(await With(probe, db => Database.Lease.CompleteHandover(db, "a", 1, 60_000)));
		Assert.True(await With(probe, db => Database.Lease.ConfirmHandover(db, "b")));
		Assert.True(await With(probe, db => Database.Lease.TargetAlive(db, "a", 1, "b")));
		Assert.False(await With(probe, db => Database.Lease.TargetAlive(db, "a", 1, "c")));

		// the end moves the lease to the target with the next epoch
		Assert.Null(await With(probe, db => Database.Lease.CompleteHandover(db, "b", 1, 60_000)));
		Database.Lease.Handover? handover = await With(probe, db => Database.Lease.CompleteHandover(db, "a", 1, 60_000));
		Assert.Equal(("b", 2L), (handover!.Target, handover.Epoch));
		row = await Row(probe);
		Assert.Equal(("b", 2L, (string?)null, (string?)null), (row.Holder, row.Epoch, row.Standby, row.HandoverTarget));
		Assert.False(await Renew(probe, "a", 1));
		Assert.True(await Renew(probe, "b", 2));

		// the holder cannot announce itself as standby
		await Announce(probe, "b");
		Assert.Null((await Row(probe)).Standby);
	}

	[ProbeTheory]
	[MemberData(nameof(Postgres))]
	public async Task ACancelledHandoverRaisesTheEpochAndAGoneTargetGetsNoLease(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		Assert.Equal(1L, await Take(probe, "a", 60_000));
		await Announce(probe, "b", 60_000);
		Assert.Equal("b", await Begin(probe, "a", 1));
		Assert.True(await With(probe, db => Database.Lease.ConfirmHandover(db, "b")));

		// the holder cancels: the lease stays with it at the next epoch and the target is no target any more
		Assert.Null(await With(probe, db => Database.Lease.CancelHandover(db, "a", 7, 60_000)));
		Assert.Equal(2L, await With(probe, db => Database.Lease.CancelHandover(db, "a", 1, 60_000)));
		RealtimeLease row = await Row(probe);
		Assert.Equal(("a", 2L, (string?)null, (DateTime?)null), (row.Holder, row.Epoch, row.HandoverTarget, row.HandoverConfirmedAt));
		Assert.False(await Renew(probe, "a", 1));
		Assert.True(await Renew(probe, "a", 2, 60_000));
		Assert.False(await With(probe, db => Database.Lease.ConfirmHandover(db, "b")));

		// a target whose standby entry lapsed is not alive and gets no lease
		await Announce(probe, "b", 100);
		Assert.Equal("b", await Begin(probe, "a", 2));
		Assert.True(await With(probe, db => Database.Lease.ConfirmHandover(db, "b")));
		await Task.Delay(300);
		Assert.False(await With(probe, db => Database.Lease.TargetAlive(db, "a", 2, "b")));
		Assert.Null(await With(probe, db => Database.Lease.CompleteHandover(db, "a", 2, 60_000)));
		Assert.Equal("a", (await Row(probe)).Holder);

		// a holder that dies during the handover lets the target take the expired lease, which clears the handover
		Assert.True(await Renew(probe, "a", 2, 100));
		await Task.Delay(300);
		Assert.Equal(3L, await Take(probe, "b"));
		row = await Row(probe);
		Assert.Equal(("b", (string?)null), (row.Holder, row.HandoverTarget));
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
