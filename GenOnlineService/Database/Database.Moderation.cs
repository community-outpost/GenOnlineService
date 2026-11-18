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

// Local rows are managed in this environment, upstream rows are replaced at each sign-in through that provider
public static class AuthoritySources
{
	public const string Local = "local";
	public const string UpstreamPrefix = "upstream:";

	public static string Upstream(string provider) => UpstreamPrefix + provider;
}

// A kick closes the connections and is never active, a mute only silences the user, a ban keeps them out of the game
public static class SanctionTypes
{
	public const string Kick = "kick";
	public const string Mute = "mute";
	public const string Ban = "ban";
}

public static class AuditActorKinds
{
	public const string User = "user";
	public const string Cli = "cli";
	public const string Discord = "discord";
	public const string ServiceAccount = "service_account";
	public const string Upstream = "upstream";
}

public static class AuditTargetKinds
{
	public const string User = "user";
	public const string ServiceAccount = "service_account";
	public const string Token = "token";
	public const string Message = "message";
	public const string Report = "report";
	public const string Client = "client";
	public const string Setting = "setting";
	public const string Announcement = "announcement";
	public const string Lobby = "lobby";
	public const string Flag = "flag";
	public const string FlagRevision = "flag_revision";
	public const string AnticheatReport = "anticheat_report";
	// a staff search by an identifier, which names no single target
	public const string Investigation = "investigation";
}

public static class ModerationActions
{
	public const string Kick = "kick";
	public const string Ban = "ban";
	public const string Unban = "unban";
	public const string Mute = "mute";
	public const string Unmute = "unmute";
	public const string Rename = "rename";
	// staff replaced an inappropriate name with a placeholder; the player chooses a new one (S17)
	public const string NameReset = "name_reset";
	public const string RoleGrant = "role_grant";
	public const string RoleRevoke = "role_revoke";
	public const string MessageDelete = "message_delete";
	public const string MessageLookup = "message_lookup";
	public const string StaffMessage = "staff_message";
	public const string ReportView = "report_view";
	public const string ReportClose = "report_close";
	public const string SettingUpdate = "setting_update";
	public const string Announcement = "announcement";
	public const string ConnectionLookup = "connection_lookup";

	// A staff member opened the sign-ins, devices and addresses of a player
	public const string PlayerInspect = "player_inspect";
	// A staff member searched the accounts that used a device or network identifier, every search is a row
	public const string InvestigateSearch = "investigate_search";
	public const string AnticheatReview = "anticheat_review";
}

public static class ModerationSources
{
	public const string WebSocket = "websocket";
	public const string Discord = "discord";
	public const string Cli = "cli";
	public const string Realtime = "realtime";
	public const string Api = "api";
}

public class UserRole
{
	public Int64 UserID { get; set; }
	public string Role { get; set; } = String.Empty;
	public string Source { get; set; } = AuthoritySources.Local;

	// An actor label such as "cli", "user:42" or "discord:123"
	public string? GrantedBy { get; set; }

	public DateTime GrantedAt { get; set; } = DateTime.UnixEpoch;
	public string? Reason { get; set; }
}

// A kick, mute or ban, lifted ones stay as history and sanctions are never purged
public class Sanction
{
	public Int64 ID { get; set; }
	public Int64 UserID { get; set; }
	public string Type { get; set; } = SanctionTypes.Ban;
	public string Source { get; set; } = AuthoritySources.Local;
	public string? Reason { get; set; }
	public string CreatedByKind { get; set; } = AuditActorKinds.Cli;
	public Int64? CreatedByID { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;

	// A kick expires as it starts, a mute always expires, a ban without one is permanent
	public DateTime? ExpiresAt { get; set; }

	public DateTime? LiftedAt { get; set; }
	public string? LiftedByKind { get; set; }
	public Int64? LiftedByID { get; set; }
	public string? LiftReason { get; set; }

	// The player report this sanction answered, set by tooling
	public Int64? ReportID { get; set; }
}

// Append only, every row is an action that already happened
public class AuditLogEntry
{
	public Int64 ID { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;
	public string ActorKind { get; set; } = String.Empty;
	public Int64? ActorID { get; set; }
	public string Actor { get; set; } = String.Empty;
	public string Action { get; set; } = String.Empty;
	public string TargetKind { get; set; } = String.Empty;
	public Int64? TargetID { get; set; }
	public string? Reason { get; set; }
	public string Source { get; set; } = String.Empty;

	// A JSON object, null when the action needs no detail
	public string? Details { get; set; }
}

public class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
	public void Configure(EntityTypeBuilder<UserRole> builder)
	{
		builder.ToTable("user_roles");

		builder.HasKey(e => new { e.UserID, e.Role, e.Source });

		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.Role).HasColumnName("role").HasMaxLength(32);
		builder.Property(e => e.Source).HasColumnName("source").HasMaxLength(32);
		builder.Property(e => e.GrantedBy).HasColumnName("granted_by").HasMaxLength(64);
		builder.Property(e => e.GrantedAt).HasColumnName("granted_at");
		builder.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(ModerationManager.MaximumReasonLength);
	}
}

public class SanctionConfiguration : IEntityTypeConfiguration<Sanction>
{
	public void Configure(EntityTypeBuilder<Sanction> builder)
	{
		builder.ToTable("sanctions");

		builder.HasKey(e => e.ID);
		builder.HasIndex(e => new { e.UserID, e.Type, e.LiftedAt, e.ExpiresAt }).HasDatabaseName("ix_sanctions_user_type_lifted_expires");

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.Type).HasColumnName("type").HasMaxLength(16);
		builder.Property(e => e.Source).HasColumnName("source").HasMaxLength(32);
		builder.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(ModerationManager.MaximumReasonLength);
		builder.Property(e => e.CreatedByKind).HasColumnName("created_by_kind").HasMaxLength(16);
		builder.Property(e => e.CreatedByID).HasColumnName("created_by_id");
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		builder.Property(e => e.ExpiresAt).HasColumnName("expires_at");
		builder.Property(e => e.LiftedAt).HasColumnName("lifted_at");
		builder.Property(e => e.LiftedByKind).HasColumnName("lifted_by_kind").HasMaxLength(16);
		builder.Property(e => e.LiftedByID).HasColumnName("lifted_by_id");
		builder.Property(e => e.LiftReason).HasColumnName("lift_reason").HasMaxLength(ModerationManager.MaximumReasonLength);
		builder.Property(e => e.ReportID).HasColumnName("report_id");
	}
}

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
	public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
	{
		builder.ToTable("audit_log");

		builder.HasKey(e => e.ID);
		builder.HasIndex(e => new { e.TargetKind, e.TargetID }).HasDatabaseName("ix_audit_log_target");

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		builder.Property(e => e.ActorKind).HasColumnName("actor_kind").HasMaxLength(16);
		builder.Property(e => e.ActorID).HasColumnName("actor_id");
		builder.Property(e => e.Actor).HasColumnName("actor").HasMaxLength(64);
		builder.Property(e => e.Action).HasColumnName("action").HasMaxLength(64);
		builder.Property(e => e.TargetKind).HasColumnName("target_kind").HasMaxLength(16);
		builder.Property(e => e.TargetID).HasColumnName("target_id");
		builder.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(ModerationManager.MaximumReasonLength);
		builder.Property(e => e.Source).HasColumnName("source").HasMaxLength(32);
		builder.Property(e => e.Details).HasColumnName("details");
	}
}

namespace Database
{
	// Who performed an action: Kind is one of AuditActorKinds, Name is the label stored in granted_by, created_by and revoked_by
	public sealed class ModerationActor
	{
		public string Kind { get; init; } = AuditActorKinds.Cli;
		public Int64? UserID { get; init; }
		public string Name { get; init; } = String.Empty;
		public string Source { get; init; } = String.Empty;
	}

	public static class Moderation
	{
		private static readonly ILogger s_log = AppLog.For(typeof(Moderation));

		// Unlifted and unexpired, so a kick is never active
		public static IQueryable<Sanction> ActiveSanctions(AppDbContext db, string type, DateTime now)
		{
			return db.Sanctions.AsNoTracking().Where(s => s.Type == type && s.LiftedAt == null && (s.ExpiresAt == null || s.ExpiresAt > now));
		}

		private static AuditLogEntry UserEntry(ModerationActor actor, string action, Int64 target, string? reason, object? details, DateTime now)
		{
			return Entry(actor, action, AuditTargetKinds.User, target, reason, details, now);
		}

		private static AuditLogEntry Entry(ModerationActor actor, string action, string targetKind, Int64? target, string? reason, object? details, DateTime now)
		{
			return new AuditLogEntry
			{
				CreatedAt = now,
				ActorKind = actor.Kind,
				ActorID = actor.UserID,
				Actor = actor.Name,
				Action = action,
				TargetKind = targetKind,
				TargetID = target,
				Reason = String.IsNullOrEmpty(reason) ? null : reason,
				Source = actor.Source,
				Details = details == null ? null : JsonSerializer.Serialize(details)
			};
		}

		public static async Task<List<UserRole>> GetAllRoles(AppDbContext db)
		{
			return await db.UserRoles.AsNoTracking().ToListAsync();
		}

		public static async Task<List<UserRole>> GetRoles(AppDbContext db, Int64 userID)
		{
			return await db.UserRoles.AsNoTracking().Where(r => r.UserID == userID).OrderBy(r => r.Role).ThenBy(r => r.Source).ToListAsync();
		}

		// Every source counts, so an upstream grant and a local grant of the same role read as one role
		public static async Task<List<string>> GetRoleNames(AppDbContext db, Int64 userID)
		{
			try
			{
				return await db.UserRoles.AsNoTracking().Where(r => r.UserID == userID).Select(r => r.Role).Distinct().ToListAsync();
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetRoleNames failed");
				return new List<string>();
			}
		}

		public static async Task<List<Int64>> GetPlayBannedUserIDs(AppDbContext db)
		{
			return await ActiveSanctions(db, SanctionTypes.Ban, DateTime.UtcNow).Select(s => s.UserID).Distinct().ToListAsync();
		}

		// The ban that lasts longest decides what the player sees
		public static Task<Sanction?> GetActivePlayBan(AppDbContext db, Int64 userID) => LongestActive(db, SanctionTypes.Ban, userID);

		// The mute that lasts longest decides when the mute ends
		public static Task<Sanction?> GetActiveMute(AppDbContext db, Int64 userID) => LongestActive(db, SanctionTypes.Mute, userID);

		private static async Task<Sanction?> LongestActive(AppDbContext db, string type, Int64 userID)
		{
			return await ActiveSanctions(db, type, DateTime.UtcNow)
				.Where(s => s.UserID == userID)
				.OrderByDescending(s => s.ExpiresAt == null)
				.ThenByDescending(s => s.ExpiresAt)
				.FirstOrDefaultAsync();
		}

		public static async Task<List<AuditLogEntry>> GetUserLog(AppDbContext db, Int64 targetUserID, int limit)
		{
			return await db.AuditLog.AsNoTracking()
				.Where(e => e.TargetKind == AuditTargetKinds.User && e.TargetID == targetUserID)
				.OrderByDescending(e => e.ID)
				.Take(limit)
				.ToListAsync();
		}

		// Newest first, below afterID when set; every filter is optional
		public static async Task<List<AuditLogEntry>> GetAuditEvents(AppDbContext db, Int64? actorID, Int64? targetID, string? action, Int64? afterID, int limit, string? targetKind = null, AuditFilter? filter = null)
		{
			IQueryable<AuditLogEntry> query = db.AuditLog.AsNoTracking();
			if (filter?.ActorIDs is IReadOnlyCollection<long> actors)
			{
				query = query.Where(e => e.ActorID != null && actors.Contains(e.ActorID.Value) && e.ActorKind == AuditActorKinds.User);
			}
			if (filter?.TargetUserIDs is IReadOnlyCollection<long> targets)
			{
				query = query.Where(e => e.TargetID != null && targets.Contains(e.TargetID.Value) && e.TargetKind == AuditTargetKinds.User);
			}
			if (filter?.From is DateTime from)
			{
				query = query.Where(e => e.CreatedAt >= from);
			}
			if (filter?.To is DateTime to)
			{
				query = query.Where(e => e.CreatedAt < to);
			}
			if (actorID != null)
			{
				// actor_id names a user; service account and token rows keep other ids in that column
				query = query.Where(e => e.ActorID == actorID && e.ActorKind == AuditActorKinds.User);
			}
			if (targetID != null)
			{
				query = query.Where(e => e.TargetID == targetID);
			}
			if (targetKind != null)
			{
				query = query.Where(e => e.TargetKind == targetKind);
			}
			if (action != null)
			{
				query = query.Where(e => e.Action == action);
			}
			if (afterID != null)
			{
				query = query.Where(e => e.ID < afterID);
			}
			return await query.OrderByDescending(e => e.ID).Take(limit).ToListAsync();
		}

		// The filters of the staff audit log beyond the single ids: players by name (users only), a time range [From, To)
		public sealed record AuditFilter(IReadOnlyCollection<long>? ActorIDs, IReadOnlyCollection<long>? TargetUserIDs, DateTime? From, DateTime? To);

		// The role row and its audit row are saved together, so the log never misses a change
		public static async Task<bool> GrantRole(AppDbContext db, Int64 userID, string role, string source, string reason, ModerationActor actor)
		{
			if (await db.UserRoles.AnyAsync(r => r.UserID == userID && r.Role == role && r.Source == source))
			{
				return false;
			}

			DateTime now = DateTime.UtcNow;
			db.UserRoles.Add(new UserRole { UserID = userID, Role = role, Source = source, GrantedBy = actor.Name, GrantedAt = now, Reason = String.IsNullOrEmpty(reason) ? null : reason });
			db.AuditLog.Add(UserEntry(actor, ModerationActions.RoleGrant, userID, reason, new { role, source }, now));

			try
			{
				await db.SaveChangesAsync();
			}
			catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex))
			{
				// a concurrent grant of the same role won, so this one changes nothing and logs nothing
				db.ChangeTracker.Clear();
				return false;
			}

			return true;
		}

		public static async Task<bool> RevokeRole(AppDbContext db, Int64 userID, string role, string source, string reason, ModerationActor actor)
		{
			UserRole? existing = await db.UserRoles.AsTracking().FirstOrDefaultAsync(r => r.UserID == userID && r.Role == role && r.Source == source);
			if (existing == null)
			{
				return false;
			}

			db.UserRoles.Remove(existing);
			db.AuditLog.Add(UserEntry(actor, ModerationActions.RoleRevoke, userID, reason, new { role, source }, DateTime.UtcNow));
			await db.SaveChangesAsync();
			return true;
		}

		public static async Task<Sanction> AddSanction(AppDbContext db, Int64 userID, string type, string? reason, DateTime? expiresAt, ModerationActor actor)
		{
			DateTime now = DateTime.UtcNow;
			var sanction = new Sanction { UserID = userID, Type = type, Source = AuthoritySources.Local, Reason = String.IsNullOrEmpty(reason) ? null : reason, CreatedByKind = actor.Kind, CreatedByID = actor.UserID, CreatedAt = now, ExpiresAt = expiresAt };

			db.Sanctions.Add(sanction);
			db.AuditLog.Add(UserEntry(actor, type, userID, reason, new { type, expires_at = expiresAt }, now));
			try
			{
				await db.SaveChangesAsync();
			}
			finally
			{
				db.ChangeTracker.Clear();
			}
			return sanction;
		}

		// Lifted sanctions stay in the table as history, the count is how many were active
		public static async Task<int> LiftActiveSanctions(AppDbContext db, Int64 userID, string type, string reason, ModerationActor actor)
		{
			DateTime now = DateTime.UtcNow;

			// every context defaults to no tracking, so the rows must be tracked for the lift to be saved
			List<Sanction> active = await db.Sanctions
				.AsTracking()
				.Where(s => s.UserID == userID && s.Type == type && s.Source == AuthoritySources.Local && s.LiftedAt == null && (s.ExpiresAt == null || s.ExpiresAt > now))
				.ToListAsync();

			if (active.Count == 0)
			{
				return 0;
			}

			foreach (Sanction sanction in active)
			{
				Lift(sanction, reason, actor, now);
			}

			db.AuditLog.Add(UserEntry(actor, LiftAction(type), userID, reason, new { type, lifted = active.Count }, now));
			try
			{
				await db.SaveChangesAsync();
			}
			finally
			{
				db.ChangeTracker.Clear();
			}
			return active.Count;
		}

		private static void Lift(Sanction sanction, string? reason, ModerationActor actor, DateTime now)
		{
			sanction.LiftedAt = now;
			sanction.LiftedByKind = actor.Kind;
			sanction.LiftedByID = actor.UserID;
			sanction.LiftReason = String.IsNullOrEmpty(reason) ? null : reason;
		}

		private static string LiftAction(string type) => type == SanctionTypes.Mute ? ModerationActions.Unmute : ModerationActions.Unban;

		// Stores a kick with its audit row before it is applied, a failed write throws so the kick is never applied
		public static async Task<Sanction> AddKick(AppDbContext db, Int64 userID, string reason, ModerationActor actor)
		{
			DateTime now = DateTime.UtcNow;
			var kick = new Sanction { UserID = userID, Type = SanctionTypes.Kick, Source = AuthoritySources.Local, Reason = String.IsNullOrEmpty(reason) ? null : reason, CreatedByKind = actor.Kind, CreatedByID = actor.UserID, CreatedAt = now, ExpiresAt = now };
			try
			{
				db.Sanctions.Add(kick);
				db.AuditLog.Add(UserEntry(actor, ModerationActions.Kick, userID, reason, null, now));
				await db.SaveChangesAsync();
				return kick;
			}
			finally
			{
				db.ChangeTracker.Clear();
			}
		}

		public static async Task<Sanction?> GetSanction(AppDbContext db, Int64 sanctionID)
		{
			return await db.Sanctions.AsNoTracking().FirstOrDefaultAsync(s => s.ID == sanctionID);
		}

		// Every sanction of a user newest first, active only leaves out lifted and expired ones and therefore every kick
		public static async Task<List<Sanction>> GetSanctions(AppDbContext db, Int64 userID, bool activeOnly)
		{
			DateTime now = DateTime.UtcNow;
			IQueryable<Sanction> query = db.Sanctions.AsNoTracking().Where(s => s.UserID == userID);
			if (activeOnly)
			{
				query = query.Where(s => s.LiftedAt == null && (s.ExpiresAt == null || s.ExpiresAt > now));
			}
			return await query.OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.ID).ToListAsync();
		}

		// Lifts one mute or ban, a sanction that already ended still gets its audit row, true when it was active
		public static async Task<bool> LiftSanction(AppDbContext db, Int64 sanctionID, string reason, ModerationActor actor)
		{
			DateTime now = DateTime.UtcNow;
			Sanction? sanction = await db.Sanctions.AsTracking().FirstOrDefaultAsync(s => s.ID == sanctionID);
			if (sanction == null)
			{
				return false;
			}

			bool active = sanction.LiftedAt == null && (sanction.ExpiresAt == null || sanction.ExpiresAt > now);
			if (active)
			{
				Lift(sanction, reason, actor, now);
			}

			db.AuditLog.Add(UserEntry(actor, LiftAction(sanction.Type), sanction.UserID, reason, new { type = sanction.Type, sanction_id = sanction.ID, lifted = active ? 1 : 0 }, now));
			try
			{
				await db.SaveChangesAsync();
			}
			finally
			{
				db.ChangeTracker.Clear();
			}
			return active;
		}

		// One row per actor and player inside the window, written before the personal data is read, true when a row was written
		public static async Task<bool> LogInspection(AppDbContext db, ModerationActor actor, Int64 target, DateTime now, TimeSpan window)
		{
			DateTime since = now - window;
			bool logged = await db.AuditLog.AsNoTracking().AnyAsync(e => e.TargetKind == AuditTargetKinds.User && e.TargetID == target
				&& e.Action == ModerationActions.PlayerInspect && e.ActorKind == actor.Kind && e.ActorID == actor.UserID && e.CreatedAt > since);
			if (logged)
			{
				return false;
			}

			db.AuditLog.Add(UserEntry(actor, ModerationActions.PlayerInspect, target, null, null, now));
			try
			{
				await db.SaveChangesAsync();
			}
			finally
			{
				db.ChangeTracker.Clear();
			}
			return true;
		}

		// Writes one audit row for a non user target and throws on failure, for actions that must not happen unlogged
		public static async Task AddAuditEntry(AppDbContext db, ModerationActor actor, string action, string targetKind, Int64? target, string? reason, object? details = null)
		{
			db.AuditLog.Add(Entry(actor, action, targetKind, target, reason, details, DateTime.UtcNow));
			try
			{
				await db.SaveChangesAsync();
			}
			finally
			{
				db.ChangeTracker.Clear();
			}
		}

		// Same as AddAuditEntry for an action that already happened, a failed write is only reported
		public static async Task LogAction(AppDbContext db, ModerationActor actor, string action, string targetKind, Int64 target, string? reason, object? details = null)
		{
			try
			{
				await AddAuditEntry(db, actor, action, targetKind, target, reason, details);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Moderation.LogAction failed for {Action} on {TargetKind} {TargetId}", action, targetKind, target);
			}
		}

		// A failed log write never undoes an action that already happened, it is only reported
		public static async Task LogUserAction(AppDbContext db, ModerationActor actor, string action, Int64 target, string reason)
		{
			try
			{
				db.AuditLog.Add(UserEntry(actor, action, target, reason, null, DateTime.UtcNow));
				await db.SaveChangesAsync();
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Moderation.LogUserAction failed for {Action} on user {UserId}", action, target);
			}
		}
	}
}
