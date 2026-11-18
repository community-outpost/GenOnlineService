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
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// The one row per environment that names the active realtime copy, see docs/realtime-failover.md
public class RealtimeLease
{
	public int ID { get; set; }
	public string Holder { get; set; } = String.Empty;
	public long Epoch { get; set; }
	public DateTime ExpiresAt { get; set; }
	public string? Standby { get; set; }
	public DateTime? StandbyExpiresAt { get; set; }
	public DateTime? HandoverRequestedAt { get; set; }

	// The copy a running planned handover moves to, and when it confirmed that it serves (build step 4)
	public string? HandoverTarget { get; set; }
	public DateTime? HandoverConfirmedAt { get; set; }
}

public class RealtimeLeaseConfiguration : IEntityTypeConfiguration<RealtimeLease>
{
	public void Configure(EntityTypeBuilder<RealtimeLease> builder)
	{
		builder.ToTable("realtime_lease");

		builder.HasKey(e => e.ID);

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedNever();
		builder.Property(e => e.Holder).HasColumnName("holder").HasMaxLength(128).IsRequired();
		builder.Property(e => e.Epoch).HasColumnName("epoch");
		builder.Property(e => e.ExpiresAt).HasColumnName("expires_at");
		builder.Property(e => e.Standby).HasColumnName("standby").HasMaxLength(128);
		builder.Property(e => e.StandbyExpiresAt).HasColumnName("standby_expires_at");
		builder.Property(e => e.HandoverRequestedAt).HasColumnName("handover_requested_at");
		builder.Property(e => e.HandoverTarget).HasColumnName("handover_target").HasMaxLength(128);
		builder.Property(e => e.HandoverConfirmedAt).HasColumnName("handover_confirmed_at");

		// the row exists from the first migration on, expired, so the first copy takes it at once
		builder.HasData(new RealtimeLease { ID = Database.Lease.RowID, Holder = String.Empty, Epoch = 0, ExpiresAt = DateTime.UnixEpoch });
	}
}

namespace Database
{
	// Lease statements that compare with the database clock, never a copy's clock, each change is one conditional UPDATE.
	// PostgreSQL only: more than one service copy is supported there alone, the table also exists in the MySQL model but stays unused.
	public static class Lease
	{
		public const int RowID = 1;

		public sealed record Handover(string Target, long Epoch, DateTime ExpiresAt);

		private static string Now(AppDbContext db) => db.Database.IsNpgsql() ? "now()" : throw new NotSupportedException(GenOnlineService.Failover.FailoverSettings.PostgresOnly);

		// The database time plus a parameter in milliseconds
		private static string NowPlus(AppDbContext db, int parameter) => $"({Now(db)} + {{{parameter}}} * interval '1 millisecond')";

		public static Task<RealtimeLease?> Read(AppDbContext db, CancellationToken cancellationToken = default) =>
			db.RealtimeLeases.AsNoTracking().SingleOrDefaultAsync(l => l.ID == RowID, cancellationToken);

		public static Task<DateTime> DatabaseNow(AppDbContext db, CancellationToken cancellationToken = default)
		{
			return db.Database.SqlQueryRaw<DateTime>($"SELECT {Now(db)} AS \"Value\"").SingleAsync(cancellationToken);
		}

		// Takes an expired lease and raises the epoch, null when another copy holds it. The row lock serializes racing takers and the loser re-checks the new expiry.
		public static async Task<long?> TryTake(AppDbContext db, string copyID, int leaseMs, CancellationToken cancellationToken = default)
		{
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

			int updated = await db.Database.ExecuteSqlRawAsync(
				$"UPDATE realtime_lease SET holder = {{0}}, epoch = epoch + 1, expires_at = {NowPlus(db, 1)}, standby = NULL, standby_expires_at = NULL, handover_requested_at = NULL, "
				+ $"handover_target = NULL, handover_confirmed_at = NULL WHERE id = {RowID} AND expires_at < {Now(db)}",
				new object[] { copyID, leaseMs }, cancellationToken);
			if (updated != 1)
			{
				await transaction.RollbackAsync(cancellationToken);
				return null;
			}

			long epoch = await db.RealtimeLeases.AsNoTracking().Where(l => l.ID == RowID).Select(l => l.Epoch).SingleAsync(cancellationToken);
			await transaction.CommitAsync(cancellationToken);
			return epoch;
		}

		// Extends the lease only while holder and epoch are still the caller's
		public static async Task<bool> Renew(AppDbContext db, string copyID, long epoch, int leaseMs, CancellationToken cancellationToken = default)
		{
			int updated = await db.Database.ExecuteSqlRawAsync(
				$"UPDATE realtime_lease SET expires_at = {NowPlus(db, 2)} WHERE id = {RowID} AND holder = {{0}} AND epoch = {{1}}",
				new object[] { copyID, epoch, leaseMs }, cancellationToken);
			return updated == 1;
		}

		// A standby names itself so the holder can hand over to it
		public static async Task<bool> AnnounceStandby(AppDbContext db, string copyID, int leaseMs, CancellationToken cancellationToken = default)
		{
			int updated = await db.Database.ExecuteSqlRawAsync(
				$"UPDATE realtime_lease SET standby = {{0}}, standby_expires_at = {NowPlus(db, 1)} WHERE id = {RowID} AND holder <> {{0}} AND (handover_target IS NULL OR handover_target = {{0}})",
				new object[] { copyID, leaseMs }, cancellationToken);
			return updated == 1;
		}

		// Starts a planned handover to the live standby while the caller keeps the lease, null without one or when the caller no longer holds it
		public static async Task<string?> BeginHandover(AppDbContext db, string copyID, long epoch, CancellationToken cancellationToken = default)
		{
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

			int updated = await db.Database.ExecuteSqlRawAsync(
				$"UPDATE realtime_lease SET handover_target = standby, handover_confirmed_at = NULL, handover_requested_at = NULL "
				+ $"WHERE id = {RowID} AND holder = {{0}} AND epoch = {{1}} AND expires_at > {Now(db)} AND standby IS NOT NULL AND standby <> {{0}} AND standby_expires_at > {Now(db)}",
				new object[] { copyID, epoch }, cancellationToken);
			if (updated != 1)
			{
				await transaction.RollbackAsync(cancellationToken);
				return null;
			}

			string? target = await db.RealtimeLeases.AsNoTracking().Where(l => l.ID == RowID).Select(l => l.HandoverTarget).SingleAsync(cancellationToken);
			await transaction.CommitAsync(cancellationToken);
			return target;
		}

		// The target says it serves, only while it is the target of a live holder
		public static async Task<bool> ConfirmHandover(AppDbContext db, string copyID, CancellationToken cancellationToken = default)
		{
			int updated = await db.Database.ExecuteSqlRawAsync(
				$"UPDATE realtime_lease SET handover_confirmed_at = {Now(db)} WHERE id = {RowID} AND handover_target = {{0}} AND holder <> {{0}} AND expires_at > {Now(db)}",
				new object[] { copyID }, cancellationToken);
			return updated == 1;
		}

		// True while the target of the caller's handover still renews its standby entry
		public static async Task<bool> TargetAlive(AppDbContext db, string copyID, long epoch, string target, CancellationToken cancellationToken = default)
		{
			int count = await db.Database.SqlQueryRaw<int>(
				$"SELECT count(*)::int AS \"Value\" FROM realtime_lease WHERE id = {RowID} AND holder = {{0}} AND epoch = {{1}} AND handover_target = {{2}} AND standby = {{2}} AND standby_expires_at > {Now(db)}",
				copyID, epoch, target).SingleAsync(cancellationToken);
			return count == 1;
		}

		// Ends the handover without moving the lease and raises the epoch, so the caller takes back what the target owned under a new epoch
		public static async Task<long?> CancelHandover(AppDbContext db, string copyID, long epoch, int leaseMs, CancellationToken cancellationToken = default)
		{
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

			int updated = await db.Database.ExecuteSqlRawAsync(
				$"UPDATE realtime_lease SET epoch = epoch + 1, expires_at = {NowPlus(db, 2)}, handover_target = NULL, handover_confirmed_at = NULL, handover_requested_at = NULL "
				+ $"WHERE id = {RowID} AND holder = {{0}} AND epoch = {{1}}",
				new object[] { copyID, epoch, leaseMs }, cancellationToken);
			if (updated != 1)
			{
				await transaction.RollbackAsync(cancellationToken);
				return null;
			}

			long next = await db.RealtimeLeases.AsNoTracking().Where(l => l.ID == RowID).Select(l => l.Epoch).SingleAsync(cancellationToken);
			await transaction.CommitAsync(cancellationToken);
			return next;
		}

		// The end of the handover: the confirmed and live target gets the lease with the epoch plus one, null when it is gone
		public static async Task<Handover?> CompleteHandover(AppDbContext db, string copyID, long epoch, int leaseMs, CancellationToken cancellationToken = default)
		{
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

			int updated = await db.Database.ExecuteSqlRawAsync(
				$"UPDATE realtime_lease SET holder = handover_target, epoch = epoch + 1, expires_at = {NowPlus(db, 2)}, standby = NULL, standby_expires_at = NULL, "
				+ $"handover_target = NULL, handover_confirmed_at = NULL, handover_requested_at = NULL "
				+ $"WHERE id = {RowID} AND holder = {{0}} AND epoch = {{1}} AND handover_target IS NOT NULL AND handover_confirmed_at IS NOT NULL "
				+ $"AND standby = handover_target AND standby_expires_at > {Now(db)}",
				new object[] { copyID, epoch, leaseMs }, cancellationToken);
			if (updated != 1)
			{
				await transaction.RollbackAsync(cancellationToken);
				return null;
			}

			RealtimeLease lease = await db.RealtimeLeases.AsNoTracking().SingleAsync(l => l.ID == RowID, cancellationToken);
			await transaction.CommitAsync(cancellationToken);
			return new Handover(lease.Holder, lease.Epoch, lease.ExpiresAt);
		}

		// Ends the caller's lease now so the next copy takes it without waiting for the expiry
		public static async Task<bool> Release(AppDbContext db, string copyID, long epoch, CancellationToken cancellationToken = default)
		{
			int updated = await db.Database.ExecuteSqlRawAsync(
				$"UPDATE realtime_lease SET expires_at = {NowPlus(db, 2)}, handover_requested_at = NULL WHERE id = {RowID} AND holder = {{0}} AND epoch = {{1}}",
				new object[] { copyID, epoch, -1000 }, cancellationToken);
			return updated == 1;
		}

		// The operator command: asks the live holder to hand over, only while a live standby waits
		public static async Task<bool> RequestHandover(AppDbContext db, CancellationToken cancellationToken = default)
		{
			int updated = await db.Database.ExecuteSqlRawAsync(
				$"UPDATE realtime_lease SET handover_requested_at = {Now(db)} "
				+ $"WHERE id = {RowID} AND holder <> '' AND expires_at > {Now(db)} AND standby IS NOT NULL AND standby_expires_at > {Now(db)}",
				Array.Empty<object>(), cancellationToken);
			return updated == 1;
		}

		public static async Task ClearHandoverRequest(AppDbContext db, string copyID, long epoch, CancellationToken cancellationToken = default)
		{
			await db.Database.ExecuteSqlRawAsync(
				$"UPDATE realtime_lease SET handover_requested_at = NULL WHERE id = {RowID} AND holder = {{0}} AND epoch = {{1}}",
				new object[] { copyID, epoch }, cancellationToken);
		}
	}
}
