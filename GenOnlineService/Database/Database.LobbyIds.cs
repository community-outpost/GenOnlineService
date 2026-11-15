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

// The one row that holds the next lobby id every service copy reserves blocks from
public class LobbyIdCounter
{
	public int ID { get; set; }
	public Int64 NextID { get; set; }
}

public class LobbyIdCounterConfiguration : IEntityTypeConfiguration<LobbyIdCounter>
{
	public void Configure(EntityTypeBuilder<LobbyIdCounter> builder)
	{
		builder.ToTable("lobby_ids");

		builder.HasKey(e => e.ID);

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedNever();
		builder.Property(e => e.NextID).HasColumnName("next_id");
	}
}

namespace Database
{
	public static class LobbyIds
	{
		public const int CounterRowID = 1;

		// Lobby ids stay JSON safe integers
		public const long MaximumID = (1L << 53) - 1;

		// An UPDATE inside a transaction row locks the counter on both providers, so the following read sees only this caller's bump
		public static async Task<long> ReserveBlock(AppDbContext db, int size, CancellationToken cancellationToken = default)
		{
			if (size < 1)
			{
				throw new ArgumentOutOfRangeException(nameof(size));
			}

			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

			int updated = await db.LobbyIdCounters
				.Where(c => c.ID == CounterRowID)
				.ExecuteUpdateAsync(s => s.SetProperty(c => c.NextID, c => c.NextID + size), cancellationToken);
			if (updated != 1)
			{
				throw new InvalidOperationException("lobby_ids has no counter row");
			}

			long next = await db.LobbyIdCounters
				.Where(c => c.ID == CounterRowID)
				.Select(c => c.NextID)
				.SingleAsync(cancellationToken);

			await transaction.CommitAsync(cancellationToken);

			// the block is [next - size, next)
			return next - size;
		}
	}
}
