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

public static class RatingKinds
{
	public const string AllTime = "all_time";
	public const string Monthly = "monthly";
}

public class PlayerRating
{
	public Int64 UserID { get; set; }
	public string Kind { get; set; } = RatingKinds.AllTime;
	public int Rating { get; set; } = EloConfig.BaseRating;
	public int MatchesPlayed { get; set; }
	public DateTime UpdatedAt { get; set; } = DateTime.UnixEpoch;
}

// Lobby favourites; null means the player never chose one
public class PlayerPreference
{
	public const int MapMaxLength = 128;

	public Int64 UserID { get; set; }
	public int? Color { get; set; }
	public int? Side { get; set; }
	public string? Map { get; set; }
	public int? StartingMoney { get; set; }
	public bool? LimitSuperweapons { get; set; }
	public DateTime UpdatedAt { get; set; } = DateTime.UnixEpoch;
}

public class PlayerNetwork
{
	public Int64 UserID { get; set; }
	public string? RelayRegion { get; set; }
	public string? PortMapping { get; set; }
	public string? Ipv4 { get; set; }
	public string? Ipv6 { get; set; }
	public DateTime UpdatedAt { get; set; } = DateTime.UnixEpoch;
}

public class PlayerRatingConfiguration : IEntityTypeConfiguration<PlayerRating>
{
	public void Configure(EntityTypeBuilder<PlayerRating> builder)
	{
		builder.ToTable("player_ratings");

		builder.HasKey(e => new { e.UserID, e.Kind });

		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.Kind).HasColumnName("kind").HasMaxLength(16);
		builder.Property(e => e.Rating).HasColumnName("rating");
		builder.Property(e => e.MatchesPlayed).HasColumnName("matches_played");
		builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");
	}
}

public class PlayerPreferenceConfiguration : IEntityTypeConfiguration<PlayerPreference>
{
	public void Configure(EntityTypeBuilder<PlayerPreference> builder)
	{
		builder.ToTable("player_preferences");

		builder.HasKey(e => e.UserID);

		builder.Property(e => e.UserID).HasColumnName("user_id").ValueGeneratedNever();
		builder.Property(e => e.Color).HasColumnName("color");
		builder.Property(e => e.Side).HasColumnName("side");
		builder.Property(e => e.Map).HasColumnName("map").HasMaxLength(PlayerPreference.MapMaxLength);
		builder.Property(e => e.StartingMoney).HasColumnName("starting_money");
		builder.Property(e => e.LimitSuperweapons).HasColumnName("limit_superweapons");
		builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");
	}
}

public class PlayerNetworkConfiguration : IEntityTypeConfiguration<PlayerNetwork>
{
	public void Configure(EntityTypeBuilder<PlayerNetwork> builder)
	{
		builder.ToTable("player_network");

		builder.HasKey(e => e.UserID);

		builder.Property(e => e.UserID).HasColumnName("user_id").ValueGeneratedNever();
		builder.Property(e => e.RelayRegion).HasColumnName("relay_region").HasMaxLength(16);
		builder.Property(e => e.PortMapping).HasColumnName("port_mapping").HasMaxLength(16);
		builder.Property(e => e.Ipv4).HasColumnName("ipv4").HasMaxLength(15);
		builder.Property(e => e.Ipv6).HasColumnName("ipv6").HasMaxLength(45);
		builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");
	}
}

namespace Database
{
	public static class PlayerRatings
	{
		// Users without rows are left out
		public static async Task<Dictionary<long, EloData>> GetBulk(AppDbContext db, List<long> userIds)
		{
			var rows = await db.PlayerRatings.AsNoTracking()
				.Where(r => userIds.Contains(r.UserID))
				.ToListAsync();

			var results = new Dictionary<long, EloData>();

			foreach (var group in rows.GroupBy(r => r.UserID))
			{
				PlayerRating? allTime = group.FirstOrDefault(r => r.Kind == RatingKinds.AllTime);
				PlayerRating? monthly = group.FirstOrDefault(r => r.Kind == RatingKinds.Monthly);

				results[group.Key] = new EloData(
					allTime?.Rating ?? EloConfig.BaseRating,
					monthly?.Rating ?? EloConfig.BaseRating,
					allTime?.MatchesPlayed ?? 0);
			}

			return results;
		}

		// The all-time row holds the match count; the monthly row keeps its own
		public static async Task Save(AppDbContext db, long userId, EloData data, CancellationToken cancellationToken = default)
		{
			for (int attempt = 0; ; attempt++)
			{
				try
				{
					DateTime now = DateTime.UtcNow;
					var rows = await db.PlayerRatings.AsTracking()
						.Where(r => r.UserID == userId)
						.ToListAsync(cancellationToken);

					PlayerRating allTime = rows.FirstOrDefault(r => r.Kind == RatingKinds.AllTime) ?? Add(db, userId, RatingKinds.AllTime);
					PlayerRating monthly = rows.FirstOrDefault(r => r.Kind == RatingKinds.Monthly) ?? Add(db, userId, RatingKinds.Monthly);

					allTime.Rating = data.Rating;
					allTime.MatchesPlayed = data.NumMatches;
					allTime.UpdatedAt = now;
					monthly.Rating = data.MonthlyRating;
					monthly.UpdatedAt = now;

					await db.SaveChangesAsync(cancellationToken);
					return;
				}
				catch (DbUpdateException ex) when (attempt == 0 && DbHelpers.IsUniqueViolation(ex))
				{
					// A concurrent save created the rows first, so the second attempt updates them
					db.ChangeTracker.Clear();
				}
				catch
				{
					db.ChangeTracker.Clear();
					throw;
				}
			}
		}

		private static PlayerRating Add(AppDbContext db, long userId, string kind)
		{
			var row = new PlayerRating { UserID = userId, Kind = kind, Rating = EloConfig.BaseRating };
			db.PlayerRatings.Add(row);
			return row;
		}
	}
}
