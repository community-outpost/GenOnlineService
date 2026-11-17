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

public class AnticheatModuleReport
{
	public Int64 ReportId { get; set; }
	public Int64 UserId { get; set; }
	public Int64 MatchId { get; set; }
	public string ModuleName { get; set; } = null!;
	public string ModulePath { get; set; } = null!;
	public Int64 ModuleSize { get; set; }

	// Null until a staff member marks the row reviewed, a review never deletes the row
	public DateTime? ReviewedAt { get; set; }
	public Int64? ReviewedBy { get; set; }
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

public class AnticheatNewAccountGame
{
	public Int64 ReportId { get; set; }
	public Int64 UserId { get; set; }
	public Int64 MatchId { get; set; }
	public DateTime? ReviewedAt { get; set; }
	public Int64? ReviewedBy { get; set; }
}

public class AnticheatProbeReport
{
	public Int64 ReportId { get; set; }
	public Int64 UserId { get; set; }
	public Int64 MatchId { get; set; }
	public string Reason { get; set; } = null!;
	public DateTime? ReviewedAt { get; set; }
	public Int64? ReviewedBy { get; set; }
}

// The review lists of the staff tools, one per signal
public static class AntiCheatReviewKinds
{
	public const string NewAccounts = "new_accounts";
	public const string Modules = "modules";

	// Probe reports raised inside a match
	public const string Probes = "probes";

	// Probe reports raised at sign-in for a device value without its marker, stored with match 0
	public const string Devices = "devices";

	public static readonly IReadOnlyList<string> All = [NewAccounts, Modules, Probes, Devices];

	public static bool IsKnown(string? kind) => kind != null && All.Contains(kind);
}


public class AnticheatModuleReportConfiguration : IEntityTypeConfiguration<AnticheatModuleReport>
{
	public void Configure(EntityTypeBuilder<AnticheatModuleReport> entity)
	{
		entity.ToTable("anticheat_module_reports");

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

		entity.Property(e => e.ReviewedAt).HasColumnName("reviewed_at");
		entity.Property(e => e.ReviewedBy).HasColumnName("reviewed_by");
		entity.HasIndex(e => e.ReviewedAt).HasDatabaseName("ix_anticheat_module_reports_reviewed_at");

		entity.HasIndex(e => new { e.UserId, e.MatchId, e.ModuleName })
			.IsUnique()
			.HasDatabaseName("ux_anticheat_module_reports_user_match_module");
	}
}

public class AnticheatNewAccountGameConfiguration : IEntityTypeConfiguration<AnticheatNewAccountGame>
{
	public void Configure(EntityTypeBuilder<AnticheatNewAccountGame> entity)
	{
		entity.ToTable("anticheat_new_account_games");

		entity.HasKey(e => e.ReportId);

		entity.Property(e => e.ReportId)
			.HasColumnName("report_id")
			.ValueGeneratedOnAdd();

		entity.Property(e => e.UserId)
			.HasColumnName("user_id");

		entity.Property(e => e.MatchId)
			.HasColumnName("match_id");

		entity.Property(e => e.ReviewedAt).HasColumnName("reviewed_at");
		entity.Property(e => e.ReviewedBy).HasColumnName("reviewed_by");
		entity.HasIndex(e => e.ReviewedAt).HasDatabaseName("ix_anticheat_new_account_games_reviewed_at");
	}
}

public class AnticheatProbeReportConfiguration : IEntityTypeConfiguration<AnticheatProbeReport>
{
	public void Configure(EntityTypeBuilder<AnticheatProbeReport> entity)
	{
		entity.ToTable("anticheat_probe_reports");

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

		entity.Property(e => e.ReviewedAt).HasColumnName("reviewed_at");
		entity.Property(e => e.ReviewedBy).HasColumnName("reviewed_by");
		entity.HasIndex(e => e.ReviewedAt).HasDatabaseName("ix_anticheat_probe_reports_reviewed_at");
	}
}

	

namespace Database
{
	// One row of a review list, the module and reason fields are set for the kinds that carry them
	public sealed class AntiCheatReviewRow
	{
		public Int64 ReportID { get; init; }
		public Int64 UserID { get; init; }
		public Int64 MatchID { get; init; }
		public string? ModuleName { get; init; }
		public string? ModulePath { get; init; }
		public Int64? ModuleSize { get; init; }
		public string? Reason { get; init; }
		public DateTime? ReviewedAt { get; init; }
		public Int64? ReviewedBy { get; init; }
	}

	public enum AntiCheatMarkResult { NotFound, AlreadyReviewed, Marked }

	// The staff review lists over the three anti-cheat tables, filtered and paged in the database
	public static class AntiCheatReviews
	{
		private static IQueryable<AntiCheatReviewRow> Rows(AppDbContext db, string kind)
		{
			return kind switch
			{
				AntiCheatReviewKinds.NewAccounts => db.AnticheatNewAccountGames.AsNoTracking().Select(r => new AntiCheatReviewRow
				{
					ReportID = r.ReportId, UserID = r.UserId, MatchID = r.MatchId, ReviewedAt = r.ReviewedAt, ReviewedBy = r.ReviewedBy
				}),
				AntiCheatReviewKinds.Modules => db.AnticheatModuleReports.AsNoTracking().Select(r => new AntiCheatReviewRow
				{
					ReportID = r.ReportId, UserID = r.UserId, MatchID = r.MatchId, ModuleName = r.ModuleName, ModulePath = r.ModulePath, ModuleSize = r.ModuleSize, ReviewedAt = r.ReviewedAt, ReviewedBy = r.ReviewedBy
				}),
				_ => Probes(db, kind).AsNoTracking().Select(r => new AntiCheatReviewRow
				{
					ReportID = r.ReportId, UserID = r.UserId, MatchID = r.MatchId, Reason = r.Reason, ReviewedAt = r.ReviewedAt, ReviewedBy = r.ReviewedBy
				})
			};
		}

		// A sign-in device report has no match, every other probe report has one
		private static IQueryable<AnticheatProbeReport> Probes(AppDbContext db, string kind)
		{
			return kind == AntiCheatReviewKinds.Devices
				? db.AnticheatProbeReports.Where(r => r.MatchId == 0)
				: db.AnticheatProbeReports.Where(r => r.MatchId != 0);
		}

		// Newest first, reviewed null lists both states, userID null lists every player
		public static async Task<(int Total, List<AntiCheatReviewRow> Page)> List(AppDbContext db, string kind, bool? reviewed, Int64? userID, int skip, int take, CancellationToken cancellationToken = default)
		{
			IQueryable<AntiCheatReviewRow> query = Rows(db, kind);
			if (reviewed == true)
			{
				query = query.Where(r => r.ReviewedAt != null);
			}
			else if (reviewed == false)
			{
				query = query.Where(r => r.ReviewedAt == null);
			}
			if (userID != null)
			{
				query = query.Where(r => r.UserID == userID);
			}

			int total = await query.CountAsync(cancellationToken);
			List<AntiCheatReviewRow> page = await query.OrderByDescending(r => r.ReportID).Skip(skip).Take(take).ToListAsync(cancellationToken);
			return (total, page);
		}

		public static Task<AntiCheatReviewRow?> Get(AppDbContext db, string kind, Int64 reportID, CancellationToken cancellationToken = default)
		{
			return Rows(db, kind).FirstOrDefaultAsync(r => r.ReportID == reportID, cancellationToken);
		}

		// Sets the reviewed state once, a row reviewed before keeps its first reviewer
		public static async Task<AntiCheatMarkResult> MarkReviewed(AppDbContext db, string kind, Int64 reportID, Int64? reviewerID, DateTime now, CancellationToken cancellationToken = default)
		{
			int updated = kind switch
			{
				AntiCheatReviewKinds.NewAccounts => await db.AnticheatNewAccountGames
					.Where(r => r.ReportId == reportID && r.ReviewedAt == null)
					.ExecuteUpdateAsync(s => s.SetProperty(r => r.ReviewedAt, now).SetProperty(r => r.ReviewedBy, reviewerID), cancellationToken),
				AntiCheatReviewKinds.Modules => await db.AnticheatModuleReports
					.Where(r => r.ReportId == reportID && r.ReviewedAt == null)
					.ExecuteUpdateAsync(s => s.SetProperty(r => r.ReviewedAt, now).SetProperty(r => r.ReviewedBy, reviewerID), cancellationToken),
				_ => await Probes(db, kind)
					.Where(r => r.ReportId == reportID && r.ReviewedAt == null)
					.ExecuteUpdateAsync(s => s.SetProperty(r => r.ReviewedAt, now).SetProperty(r => r.ReviewedBy, reviewerID), cancellationToken)
			};
			if (updated > 0)
			{
				return AntiCheatMarkResult.Marked;
			}
			return await Get(db, kind, reportID, cancellationToken) == null ? AntiCheatMarkResult.NotFound : AntiCheatMarkResult.AlreadyReviewed;
		}
	}

	public static class AntiCheat
	{
		private static readonly ILogger s_log = AppLog.For(typeof(AntiCheat));

		// Column limits of anticheat_module_reports (module_name varchar(128), module_path varchar(512))
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
					StringBuilder sql = new StringBuilder("INSERT INTO anticheat_module_reports (user_id, match_id, module_name, module_path, module_size) VALUES ");
					List<object> parameters = new List<object>(2 + (count * 3));
					parameters.Add(CreateParameter(postgres, "@uid", userId));
					parameters.Add(CreateParameter(postgres, "@mid", (long)matchId));

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
						parameters.Add(CreateParameter(postgres, "@s" + i, (long)module.ModuleSize));
					}

					// Repeats on the unique key (user_id, match_id, module_name) are ignored (a no-op update on MySQL), other errors still surface
					sql.Append(postgres
						? " ON CONFLICT (user_id, match_id, module_name) DO NOTHING"
						: " ON DUPLICATE KEY UPDATE report_id = report_id");

					await db.Database.ExecuteSqlRawAsync(sql.ToString(), parameters);
				}

				AppMetrics.RecordSecurityAction("anticheat_module_review", "success");
				return true;
			}
			catch (Exception ex)
			{
				if (DbHelpers.IsUniqueViolation(ex))
				{
					return true;
				}

				AppMetrics.RecordSecurityAction("anticheat_module_review", "error");
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
				db.AnticheatProbeReports.Add(new AnticheatProbeReport
				{
					UserId = userId,
					MatchId = (long)matchId,
					Reason = reason
				});

				await db.SaveChangesAsync();
				AppMetrics.RecordSecurityAction("anticheat_probe_review", "success");
			}
			catch (Exception ex)
			{
				AppMetrics.RecordSecurityAction("anticheat_probe_review", "error");
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
				db.AnticheatNewAccountGames.Add(new AnticheatNewAccountGame
				{
					UserId = userId,
					MatchId = (long)matchId
				});

				await db.SaveChangesAsync();
				AppMetrics.RecordSecurityAction("anticheat_new_account_review", "success");
			}
			catch (Exception ex)
			{
				AppMetrics.RecordSecurityAction("anticheat_new_account_review", "error");
				s_log.LogError(ex, "FlagAccountForReview_NewAccount_FirstMatches failed");
			}
		}




	}
}