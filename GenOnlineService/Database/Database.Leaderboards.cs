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

public static class LeaderboardPeriods
{
	public const string Day = "day";
	public const string Month = "month";
	public const string Year = "year";

	public static DateOnly DayStart(DateTime utc) => DateOnly.FromDateTime(utc);
	public static DateOnly MonthStart(DateTime utc) => new DateOnly(utc.Year, utc.Month, 1);
	public static DateOnly YearStart(DateTime utc) => new DateOnly(utc.Year, 1, 1);
}

// One row per user and period; period_start is the first day of that day, month or year
public class LeaderboardEntry
{
	public string Period { get; set; } = LeaderboardPeriods.Day;
	public DateOnly PeriodStart { get; set; }
	public long UserId { get; set; }
	public int Points { get; set; }
	public int Wins { get; set; }
	public int Losses { get; set; }
}

public class LeaderboardEntryConfiguration : IEntityTypeConfiguration<LeaderboardEntry>
{
	public void Configure(EntityTypeBuilder<LeaderboardEntry> builder)
	{
		builder.ToTable("leaderboard_entries");

		builder.HasKey(x => new { x.Period, x.PeriodStart, x.UserId });
		builder.HasIndex(x => x.UserId).HasDatabaseName("ix_leaderboard_entries_user_id");

		builder.Property(x => x.Period).HasColumnName("period").HasMaxLength(8);
		builder.Property(x => x.PeriodStart).HasColumnName("period_start");
		builder.Property(x => x.UserId).HasColumnName("user_id");
		builder.Property(x => x.Points).HasColumnName("points");
		builder.Property(x => x.Wins).HasColumnName("wins");
		builder.Property(x => x.Losses).HasColumnName("losses");
	}
}

namespace Database
{
	public static class Leaderboards
	{
		private static readonly ILogger s_log = AppLog.For(typeof(Leaderboards));

		public struct LeaderboardPoints
		{
			public int daily;
			public int daily_matches;
			public int monthly;
			public int monthly_matches;
			public int yearly;
			public int yearly_matches;
		}

		public static async Task CreateUserEntriesIfNotExists(AppDbContext db, long playerId)
		{
			try
			{
				DateTime now = DateTime.UtcNow;
				int basePoints = EloConfig.BaseRating;

				// One idempotent insert per period, so an existing row in one period cannot stop the others
				foreach ((string period, DateOnly start) in Periods(now))
				{
					if (db.Database.IsNpgsql())
					{
						await db.Database.ExecuteSqlInterpolatedAsync($@"
							INSERT INTO leaderboard_entries (period, period_start, user_id, points, wins, losses)
							VALUES ({period}, {start}, {playerId}, {basePoints}, 0, 0)
							ON CONFLICT (period, period_start, user_id) DO NOTHING");
					}
					else
					{
						await db.Database.ExecuteSqlInterpolatedAsync($@"
							INSERT INTO leaderboard_entries (period, period_start, user_id, points, wins, losses)
							VALUES ({period}, {start}, {playerId}, {basePoints}, 0, 0)
							ON DUPLICATE KEY UPDATE user_id = user_id");
					}
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "CreateUserEntriesIfNotExists failed");
			}
		}

		public static IEnumerable<(string Period, DateOnly Start)> Periods(DateTime utc)
		{
			yield return (LeaderboardPeriods.Day, LeaderboardPeriods.DayStart(utc));
			yield return (LeaderboardPeriods.Month, LeaderboardPeriods.MonthStart(utc));
			yield return (LeaderboardPeriods.Year, LeaderboardPeriods.YearStart(utc));
		}

		public static async ValueTask<Dictionary<long, LeaderboardPoints>> GetBulkLeaderboardData(AppDbContext db, List<long> playerIDs, DateTime matchTimeUtc)
		{
			var results = new Dictionary<long, LeaderboardPoints>();

			if (playerIDs == null || playerIDs.Count == 0)
				return results;

			foreach (var id in playerIDs)
				results[id] = new LeaderboardPoints();

			try
			{
				DateOnly day = LeaderboardPeriods.DayStart(matchTimeUtc);
				DateOnly month = LeaderboardPeriods.MonthStart(matchTimeUtc);
				DateOnly year = LeaderboardPeriods.YearStart(matchTimeUtc);

				var rows = await db.LeaderboardEntries.AsNoTracking()
					.Where(x => playerIDs.Contains(x.UserId) &&
						((x.Period == LeaderboardPeriods.Day && x.PeriodStart == day) ||
						 (x.Period == LeaderboardPeriods.Month && x.PeriodStart == month) ||
						 (x.Period == LeaderboardPeriods.Year && x.PeriodStart == year)))
					.ToListAsync();

				foreach (var row in rows)
				{
					var entry = results[row.UserId];
					int matches = row.Wins + row.Losses;

					switch (row.Period)
					{
						case LeaderboardPeriods.Day:
							entry.daily = row.Points;
							entry.daily_matches = matches;
							break;
						case LeaderboardPeriods.Month:
							entry.monthly = row.Points;
							entry.monthly_matches = matches;
							break;
						case LeaderboardPeriods.Year:
							entry.yearly = row.Points;
							entry.yearly_matches = matches;
							break;
					}

					results[row.UserId] = entry;
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetBulkLeaderboardData failed");
			}

			return results;
		}
	}
}
