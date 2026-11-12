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
using Microsoft.Extensions.Primitives;
using MySqlConnector;
using Npgsql;
using System.Data.Common;
using System.Text;
using System.Text.Json;

public class AcReviewCrc
{
	public Int64 UserId { get; set; }
	public string OriginalCrc { get; set; } = null!;
	public string DiffCrc { get; set; } = null!;
	public ulong MostRecentMatch { get; set; }
}

public class AcReviewModule
{
	public ulong ReportId { get; set; }
	public Int64 UserId { get; set; }
	public ulong MatchId { get; set; }
	public string ModuleName { get; set; } = null!;
	public string ModulePath { get; set; } = null!;
	public ulong ModuleSize { get; set; }
}

// One module reported by an anticheat Type2 probe response
public readonly record struct AcModuleReport(string ModuleName, string ModulePath, int ModuleSize);

// Cached AC:allowed_modules / AC:allowed_module_paths, refreshed whenever the configuration reloads
internal static class AntiCheatModuleAllowList
{
	private sealed class Snapshot
	{
		public string[] Modules = Array.Empty<string>();
		public string[] Paths = Array.Empty<string>();
	}

	private static readonly object s_lock = new();
	private static volatile Snapshot? s_snapshot = null;
	private static IDisposable? s_reloadRegistration = null;

	private static Snapshot? GetSnapshot()
	{
		Snapshot? snapshot = s_snapshot;
		if (snapshot != null)
		{
			return snapshot;
		}

		IConfiguration? config = GenOnlineService.Program.g_Config;
		if (config == null)
		{
			return null;
		}

		lock (s_lock)
		{
			if (s_reloadRegistration == null)
			{
				s_reloadRegistration = ChangeToken.OnChange(() => config.GetReloadToken(), () => Reload());
			}

			return Reload();
		}
	}

	private static Snapshot? Reload()
	{
		IConfiguration? config = GenOnlineService.Program.g_Config;
		if (config == null)
		{
			return null;
		}

		IConfiguration acSettings = config.GetSection("AC");
		Snapshot snapshot = new Snapshot
		{
			Modules = acSettings.GetSection("allowed_modules").Get<List<string>>()?.ToArray() ?? Array.Empty<string>(),
			Paths = acSettings.GetSection("allowed_module_paths").Get<List<string>>()?.ToArray() ?? Array.Empty<string>()
		};

		s_snapshot = snapshot;
		return snapshot;
	}

	// Filename must match an allowed module exactly, or the directory must start with an allowed path
	public static bool IsAllowed(string moduleFilename, string modulePath)
	{
		Snapshot? snapshot = GetSnapshot();
		if (snapshot == null)
		{
			return false;
		}

		if (Array.IndexOf(snapshot.Modules, moduleFilename) >= 0)
		{
			return true;
		}

		foreach (string allowedDir in snapshot.Paths)
		{
			if (modulePath.StartsWith(allowedDir))
			{
				return true;
			}
		}

		return false;
	}
}

public class AcReviewNewAccountGame
{
	public ulong ReportId { get; set; }
	public Int64 UserId { get; set; }
	public ulong MatchId { get; set; }
}

public class AcReviewProbe
{
	public ulong ReportId { get; set; }
	public Int64 UserId { get; set; }
	public ulong MatchId { get; set; }
	public string Reason { get; set; } = null!;
}


public class AcReviewCrcConfiguration : IEntityTypeConfiguration<AcReviewCrc>
{
	public void Configure(EntityTypeBuilder<AcReviewCrc> entity)
	{
		entity.ToTable("ac_reviews_crc");

		entity.HasKey(e => new { e.UserId, e.OriginalCrc, e.DiffCrc, e.MostRecentMatch });

		entity.Property(e => e.UserId)
			.HasColumnName("user_id");

		entity.Property(e => e.OriginalCrc)
			.HasColumnName("original_crc")
			.HasMaxLength(64)
			.IsRequired();

		entity.Property(e => e.DiffCrc)
			.HasColumnName("diff_crc")
			.HasMaxLength(64)
			.IsRequired();

		entity.Property(e => e.MostRecentMatch)
			.HasColumnName("most_recent_match");
	}
}

public class AcReviewModuleConfiguration : IEntityTypeConfiguration<AcReviewModule>
{
	public void Configure(EntityTypeBuilder<AcReviewModule> entity)
	{
		entity.ToTable("ac_reviews_modules");

		entity.HasKey(e => e.ReportId);

		entity.Property(e => e.ReportId)
			.HasColumnName("report_id")
			.ValueGeneratedOnAdd();

		entity.Property(e => e.UserId)
			.HasColumnName("user_id");

		entity.Property(e => e.MatchId)
			.HasColumnName("match_id");

		entity.Property(e => e.ModuleName)
			.HasColumnName("module_name")
			.HasMaxLength(128)
			.IsRequired();

		entity.Property(e => e.ModulePath)
			.HasColumnName("module_path")
			.HasMaxLength(512)
			.IsRequired();

		entity.Property(e => e.ModuleSize)
			.HasColumnName("module_size");

		entity.HasIndex(e => new { e.UserId, e.MatchId, e.ModuleName })
			.IsUnique()
			.HasDatabaseName("user_id_match_id_module_name");
	}
}

public class AcReviewNewAccountGameConfiguration : IEntityTypeConfiguration<AcReviewNewAccountGame>
{
	public void Configure(EntityTypeBuilder<AcReviewNewAccountGame> entity)
	{
		entity.ToTable("ac_reviews_new_account_games");

		entity.HasKey(e => e.ReportId);

		entity.Property(e => e.ReportId)
			.HasColumnName("report_id")
			.ValueGeneratedOnAdd();

		entity.Property(e => e.UserId)
			.HasColumnName("user_id");

		entity.Property(e => e.MatchId)
			.HasColumnName("match_id");
	}
}

public class AcReviewProbeConfiguration : IEntityTypeConfiguration<AcReviewProbe>
{
	public void Configure(EntityTypeBuilder<AcReviewProbe> entity)
	{
		entity.ToTable("ac_reviews_probes");

		entity.HasKey(e => e.ReportId);

		entity.Property(e => e.ReportId)
			.HasColumnName("report_id")
			.ValueGeneratedOnAdd();

		entity.Property(e => e.UserId)
			.HasColumnName("user_id");

		entity.Property(e => e.MatchId)
			.HasColumnName("match_id");

		entity.Property(e => e.Reason)
			.HasColumnName("reason")
			.HasMaxLength(256)
			.IsRequired();
	}
}

	

namespace Database
{
	public static class AntiCheat
	{
		private static readonly ILogger s_log = AppLog.For(typeof(AntiCheat));

		// Column limits of ac_reviews_modules (module_name varchar(128), module_path varchar(512))
		public const int ModuleNameMaxLength = 128;
		public const int ModulePathMaxLength = 512;

		// Client-supplied module text must fit the column: see TextSanitizer.Clean
		public static string CleanModuleText(string? value, int maxLength)
		{
			return TextSanitizer.Clean(value, maxLength);
		}

		private static DbParameter CreateParameter(bool postgres, string name, object value)
		{
			return postgres ? new NpgsqlParameter(name, value) : new MySqlParameter(name, value);
		}

		public static async Task AC_BanUser(AppDbContext db, long userId, string reason)
		{
			try
			{
				await db.Users
					.Where(u => u.ID == userId)
					.ExecuteUpdateAsync(setters => setters
						.SetProperty(u => u.IsBanned, true)
						.SetProperty(u => u.BanReason, reason)
						.SetProperty(u => u.BannedBy, "Anticheat")
						.SetProperty(u => u.BanVerifiedBy, "x64")
						.SetProperty(u => u.BanAliases, "")
					);
				AppMetrics.RecordSecurityAction("anticheat_", "success");
			}
			catch (Exception ex)
			{
				AppMetrics.RecordSecurityAction("anticheat_", "error");
				s_log.LogError(ex, "AC_BanUser failed");
			}
		}


		// Flags all given modules for one probe response in a single idempotent statement.
		// Returns true when the rows are persisted (or already were), false on a real database error.
		public static async Task<bool> FlagAccountForReview_Modules(
	AppDbContext db,
	Int64 userId,
	ulong matchId,
	IReadOnlyList<AcModuleReport> modules)
		{
			if (modules.Count == 0)
			{
				return true;
			}

			try
			{
				const int ChunkSize = 200;

				for (int offset = 0; offset < modules.Count; offset += ChunkSize)
				{
					int count = Math.Min(ChunkSize, modules.Count - offset);
					bool postgres = db.Database.IsNpgsql();

					// Only parameter placeholders are concatenated here, all values are bound as parameters
					StringBuilder sql = new StringBuilder("INSERT INTO ac_reviews_modules (user_id, match_id, module_name, module_path, module_size) VALUES ");
					List<object> parameters = new List<object>(2 + (count * 3));
					parameters.Add(CreateParameter(postgres, "@uid", userId));
					parameters.Add(CreateParameter(postgres, "@mid", postgres ? (long)matchId : (object)matchId));

					for (int i = 0; i < count; ++i)
					{
						AcModuleReport module = modules[offset + i];

						if (i > 0)
						{
							sql.Append(',');
						}

						sql.Append("(@uid, @mid, @n").Append(i).Append(", @p").Append(i).Append(", @s").Append(i).Append(')');
						parameters.Add(CreateParameter(postgres, "@n" + i, module.ModuleName));
						parameters.Add(CreateParameter(postgres, "@p" + i, module.ModulePath));
						parameters.Add(CreateParameter(postgres, "@s" + i, postgres ? (long)module.ModuleSize : (object)(ulong)module.ModuleSize));
					}

					// Repeats on the unique key (user_id, match_id, module_name) are ignored (a no-op update on MySQL), other errors still surface
					sql.Append(postgres
						? " ON CONFLICT (user_id, match_id, module_name) DO NOTHING"
						: " ON DUPLICATE KEY UPDATE report_id = report_id");

					await db.Database.ExecuteSqlRawAsync(sql.ToString(), parameters);
				}

				AppMetrics.RecordSecurityAction("anticheat_", "success");
				return true;
			}
			catch (Exception ex)
			{
				if (DbHelpers.IsUniqueViolation(ex))
				{
					return true;
				}

				AppMetrics.RecordSecurityAction("anticheat_", "error");
				s_log.LogError(ex, "FlagAccountForReview_Modules failed");
				return false;
			}
		}



		public static async Task FlagAccountForReview_SuspectProbes(
	AppDbContext db,
	Int64 userId,
	ulong matchId,
	string reason)
		{
			try
			{
				db.AcReviewsProbes.Add(new AcReviewProbe
				{
					UserId = userId,
					MatchId = matchId,
					Reason = reason
				});

				await db.SaveChangesAsync();
				AppMetrics.RecordSecurityAction("anticheat_", "success");
			}
			catch (Exception ex)
			{
				AppMetrics.RecordSecurityAction("anticheat_", "error");
				s_log.LogError(ex, "FlagAccountForReview_SuspectProbes failed");
			}
		}


		public static async Task FlagAccountForReview_NewAccount_FirstMatches(
	AppDbContext db,
	Int64 userId,
	ulong matchId)
		{
			try
			{
				db.AcReviewsNewAccountGames.Add(new AcReviewNewAccountGame
				{
					UserId = userId,
					MatchId = matchId
				});

				await db.SaveChangesAsync();
				AppMetrics.RecordSecurityAction("anticheat_", "success");
			}
			catch (Exception ex)
			{
				AppMetrics.RecordSecurityAction("anticheat_", "error");
				s_log.LogError(ex, "FlagAccountForReview_NewAccount_FirstMatches failed");
			}
		}




	}
}