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
using System.Text.Json;

// Per-stat counters as a JSON object keyed by stat id
public class PlayerStat
{
	public long UserId { get; set; }
	public string Stats { get; set; } = "{}";
	public DateTime UpdatedAt { get; set; } = DateTime.UnixEpoch;
}

public class PlayerStatConfiguration : IEntityTypeConfiguration<PlayerStat>
{
	public void Configure(EntityTypeBuilder<PlayerStat> builder)
	{
		builder.ToTable("player_stats");

		builder.HasKey(x => x.UserId);

		builder.Property(x => x.UserId)
			.HasColumnName("user_id")
			.ValueGeneratedNever();

		builder.Property(x => x.Stats)
			.HasColumnName("stats")
			.IsRequired();

		builder.Property(x => x.UpdatedAt)
			.HasColumnName("updated_at");
	}
}


namespace Database
{
	public static class UserStats
	{
		private static readonly ILogger s_log = AppLog.For(typeof(UserStats));

		private static readonly Func<AppDbContext, long, Task<string?>> _getUserStatsJson =
	EF.CompileAsyncQuery(
		(AppDbContext db, long userId) =>
			db.PlayerStats
			  .Where(s => s.UserId == userId)
			  .Select(s => s.Stats)
			  .FirstOrDefault()
	);


		public static async Task<PlayerStats> GetPlayerStats(
	AppDbContext db,
	long userId)
		{
			EloData elo;
			try
			{
				// Load ELO (already EF-based)
				elo = await Database.Users.GetELOData(db, userId);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetPlayerStats failed");
				return new PlayerStats(userId, EloConfig.BaseRating, 0, EloConfig.BaseRating);
			}

			PlayerStats ps = new PlayerStats(userId, elo.Rating, elo.NumMatches, elo.MonthlyRating);

			try
			{
				// Load stats JSON via EF
				string? json = await _getUserStatsJson(db, userId);

				if (string.IsNullOrEmpty(json))
					return ps; // no stats row → return ELO-only stats

				// Deserialize dictionary
				Dictionary<int, int>? dict =
					JsonSerializer.Deserialize<Dictionary<int, int>>(json);

				if (dict == null)
					return ps;

				// Feed into PlayerStats
				foreach (var kv in dict)
				{
					EStatIndex statId = (EStatIndex)kv.Key;
					int statValue = kv.Value;

					ps.ProcessFromDB(statId, statValue);
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetPlayerStats failed");
			}

			return ps;
		}


		public static async Task UpdatePlayerStat(
	AppDbContext db,
	long userId,
	int statId,
	int statVal)
		{
			try
			{
				// Atomic upsert: insert a new {"statId":value} document, or set that single key inside the existing JSON.
				// Avoids the read-modify-write race (duplicate insert for a new user, lost updates for concurrent stats).
				string statKey = statId.ToString();
				string statPath = "$.\"" + statKey + "\"";
				string initialJson = JsonSerializer.Serialize(new Dictionary<string, int> { [statKey] = statVal });

				DateTime now = DateTime.UtcNow;

				if (db.Database.IsNpgsql())
				{
					// stats is jsonb: the document is cast explicitly, and jsonb_set takes the key as a path element instead of a JSON path
					await db.Database.ExecuteSqlInterpolatedAsync($@"
						INSERT INTO player_stats (user_id, stats, updated_at)
						VALUES ({userId}, {initialJson}::jsonb, {now})
						ON CONFLICT (user_id) DO UPDATE SET stats = jsonb_set(player_stats.stats, ARRAY[{statKey}]::text[], to_jsonb({statVal}::int)), updated_at = EXCLUDED.updated_at");
				}
				else
				{
					await db.Database.ExecuteSqlInterpolatedAsync($@"
						INSERT INTO player_stats (user_id, stats, updated_at)
						VALUES ({userId}, {initialJson}, {now})
						ON DUPLICATE KEY UPDATE stats = JSON_SET(stats, {statPath}, {statVal}), updated_at = VALUES(updated_at)");
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "UpdatePlayerStat failed");
			}
		}

	}
}