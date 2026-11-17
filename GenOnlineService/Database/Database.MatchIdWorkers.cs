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

using GenOnlineService;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// One row per match id worker slot: the service copy that holds it and the last second it may put into a match id, see MatchIds.cs
public class MatchIdWorker
{
	public int WorkerID { get; set; }
	public string Holder { get; set; } = String.Empty;

	// Raised on every take, a copy changes the row only while the generation is still the one it took
	public long Generation { get; set; }

	// Seconds since MatchIdLayout.Epoch; the holder never puts a later second into an id, the next holder starts after it
	public long ReservedUntil { get; set; }
}

public class MatchIdWorkerConfiguration : IEntityTypeConfiguration<MatchIdWorker>
{
	public void Configure(EntityTypeBuilder<MatchIdWorker> builder)
	{
		builder.ToTable("match_id_workers");

		builder.HasKey(e => e.WorkerID);

		builder.Property(e => e.WorkerID).HasColumnName("worker_id").ValueGeneratedNever();
		builder.Property(e => e.Holder).HasColumnName("holder").HasMaxLength(Database.MatchIdWorkers.HolderMaxLength).IsRequired();
		builder.Property(e => e.Generation).HasColumnName("generation");
		builder.Property(e => e.ReservedUntil).HasColumnName("reserved_until");

		// every slot exists from the migration on and is free
		builder.HasData(Enumerable.Range(0, MatchIdLayout.WorkerCount)
			.Select(worker => new MatchIdWorker { WorkerID = worker, Holder = String.Empty, Generation = 0, ReservedUntil = 0 }));
	}
}

namespace Database
{
	// Each change is one UPDATE conditioned on the generation the caller read, so the statements need no database clock and run on both providers
	public static class MatchIdWorkers
	{
		public const int HolderMaxLength = 128;

		// Takes a slot whose reservation ended before nowSecond, null when every slot is still reserved.
		// The ids of the slot start after the previous holder's last second, whatever the clocks of the two copies say.
		public static async Task<MatchIdSlot?> Acquire(AppDbContext db, string holder, long nowSecond, int windowSeconds, CancellationToken cancellationToken = default)
		{
			if (windowSeconds < 1)
			{
				throw new ArgumentOutOfRangeException(nameof(windowSeconds));
			}

			string name = holder.Length > HolderMaxLength ? holder[..HolderMaxLength] : holder;

			List<MatchIdWorker> free = await db.MatchIdWorkers.AsNoTracking()
				.Where(w => w.ReservedUntil < nowSecond)
				.OrderBy(w => w.ReservedUntil)
				.ThenBy(w => w.WorkerID)
				.ToListAsync(cancellationToken);

			foreach (MatchIdWorker candidate in free)
			{
				long first = Math.Max(nowSecond, candidate.ReservedUntil + 1);
				long until = first + windowSeconds;
				long generation = candidate.Generation + 1;

				int updated = await db.MatchIdWorkers
					.Where(w => w.WorkerID == candidate.WorkerID && w.Generation == candidate.Generation)
					.ExecuteUpdateAsync(s => s
						.SetProperty(w => w.Holder, name)
						.SetProperty(w => w.Generation, generation)
						.SetProperty(w => w.ReservedUntil, until), cancellationToken);
				if (updated == 1)
				{
					return new MatchIdSlot(candidate.WorkerID, generation, first, until);
				}
				// another copy took this slot between the read and the update, try the next one
			}

			return null;
		}

		// Moves the reservation of a held slot forward to reservedUntil, false once another copy took the slot
		public static async Task<bool> Extend(AppDbContext db, MatchIdSlot slot, long reservedUntil, CancellationToken cancellationToken = default)
		{
			int updated = await db.MatchIdWorkers
				.Where(w => w.WorkerID == slot.WorkerID && w.Generation == slot.Generation && w.ReservedUntil <= reservedUntil)
				.ExecuteUpdateAsync(s => s.SetProperty(w => w.ReservedUntil, reservedUntil), cancellationToken);
			return updated == 1;
		}
	}
}
