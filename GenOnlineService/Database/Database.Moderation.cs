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

// A play ban keeps the user out of the game, a chat ban (mute) only silences them
public static class BanRestrictions
{
	public const string Play = "play";
	public const string Chat = "chat";
}

public static class AuditActorKinds
{
	public const string User = "user";
	public const string Cli = "cli";
	public const string Discord = "discord";
	public const string ServiceAccount = "service_account";
}

public static class AuditTargetKinds
{
	public const string User = "user";
	public const string ServiceAccount = "service_account";
	public const string Token = "token";
}

public static class ModerationActions
{
	public const string Kick = "kick";
	public const string Ban = "ban";
	public const string Unban = "unban";
	public const string Mute = "mute";
	public const string Unmute = "unmute";
	public const string RoleGrant = "role_grant";
	public const string RoleRevoke = "role_revoke";
}

public static class ModerationSources
{
	public const string WebSocket = "websocket";
	public const string Discord = "discord";
	public const string Cli = "cli";
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

public class Ban
{
	public Int64 ID { get; set; }
	public Int64 UserID { get; set; }
	public string Restriction { get; set; } = BanRestrictions.Play;
	public string Source { get; set; } = AuthoritySources.Local;
	public string Reason { get; set; } = String.Empty;
	public string? CreatedBy { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;

	// Null means the ban never expires
	public DateTime? ExpiresAt { get; set; }

	public DateTime? RevokedAt { get; set; }
	public string? RevokedBy { get; set; }
	public string? RevokeReason { get; set; }
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

public class BanConfiguration : IEntityTypeConfiguration<Ban>
{
	public void Configure(EntityTypeBuilder<Ban> builder)
	{
		builder.ToTable("bans");

		builder.HasKey(e => e.ID);
		builder.HasIndex(e => e.UserID).HasDatabaseName("ix_bans_user_id");

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.Restriction).HasColumnName("restriction").HasMaxLength(16);
		builder.Property(e => e.Source).HasColumnName("source").HasMaxLength(32);
		builder.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(ModerationManager.MaximumReasonLength);
		builder.Property(e => e.CreatedBy).HasColumnName("created_by").HasMaxLength(64);
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		builder.Property(e => e.ExpiresAt).HasColumnName("expires_at");
		builder.Property(e => e.RevokedAt).HasColumnName("revoked_at");
		builder.Property(e => e.RevokedBy).HasColumnName("revoked_by").HasMaxLength(64);
		builder.Property(e => e.RevokeReason).HasColumnName("revoke_reason").HasMaxLength(ModerationManager.MaximumReasonLength);
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

		private static IQueryable<Ban> ActiveBans(AppDbContext db, string restriction, DateTime now)
		{
			return db.Bans.AsNoTracking().Where(b => b.Restriction == restriction && b.RevokedAt == null && (b.ExpiresAt == null || b.ExpiresAt > now));
		}

		private static AuditLogEntry UserEntry(ModerationActor actor, string action, Int64 target, string? reason, object? details, DateTime now)
		{
			return new AuditLogEntry
			{
				CreatedAt = now,
				ActorKind = actor.Kind,
				ActorID = actor.UserID,
				Actor = actor.Name,
				Action = action,
				TargetKind = AuditTargetKinds.User,
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
			return await ActiveBans(db, BanRestrictions.Play, DateTime.UtcNow).Select(b => b.UserID).Distinct().ToListAsync();
		}

		// The play ban that lasts longest decides what the player sees
		public static async Task<Ban?> GetActivePlayBan(AppDbContext db, Int64 userID)
		{
			return await ActiveBans(db, BanRestrictions.Play, DateTime.UtcNow)
				.Where(b => b.UserID == userID)
				.OrderByDescending(b => b.ExpiresAt == null)
				.ThenByDescending(b => b.ExpiresAt)
				.FirstOrDefaultAsync();
		}

		public static async Task<List<Ban>> GetBans(AppDbContext db, Int64 userID)
		{
			return await db.Bans.AsNoTracking().Where(b => b.UserID == userID).OrderBy(b => b.CreatedAt).ToListAsync();
		}

		public static async Task<List<AuditLogEntry>> GetUserLog(AppDbContext db, Int64 targetUserID, int limit)
		{
			return await db.AuditLog.AsNoTracking()
				.Where(e => e.TargetKind == AuditTargetKinds.User && e.TargetID == targetUserID)
				.OrderByDescending(e => e.ID)
				.Take(limit)
				.ToListAsync();
		}

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

		public static async Task<Ban> AddBan(AppDbContext db, Int64 userID, string restriction, string reason, DateTime? expiresAt, ModerationActor actor)
		{
			DateTime now = DateTime.UtcNow;
			var ban = new Ban { UserID = userID, Restriction = restriction, Source = AuthoritySources.Local, Reason = reason, CreatedBy = actor.Name, CreatedAt = now, ExpiresAt = expiresAt };
			string action = restriction == BanRestrictions.Chat ? ModerationActions.Mute : ModerationActions.Ban;

			db.Bans.Add(ban);
			db.AuditLog.Add(UserEntry(actor, action, userID, reason, new { restriction, expires_at = expiresAt }, now));
			await db.SaveChangesAsync();
			return ban;
		}

		// Revoked bans stay in the table, so the history of every ban is kept
		public static async Task<int> RevokeActiveBans(AppDbContext db, Int64 userID, string restriction, string reason, ModerationActor actor)
		{
			DateTime now = DateTime.UtcNow;

			// every context defaults to no tracking, so the rows must be tracked for the revoke to be saved
			List<Ban> active = await db.Bans
				.AsTracking()
				.Where(b => b.UserID == userID && b.Restriction == restriction && b.Source == AuthoritySources.Local && b.RevokedAt == null && (b.ExpiresAt == null || b.ExpiresAt > now))
				.ToListAsync();

			if (active.Count == 0)
			{
				return 0;
			}

			foreach (Ban ban in active)
			{
				ban.RevokedAt = now;
				ban.RevokedBy = actor.Name;
				ban.RevokeReason = String.IsNullOrEmpty(reason) ? null : reason;
			}

			string action = restriction == BanRestrictions.Chat ? ModerationActions.Unmute : ModerationActions.Unban;
			db.AuditLog.Add(UserEntry(actor, action, userID, reason, new { restriction, revoked = active.Count }, now));
			await db.SaveChangesAsync();
			return active.Count;
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
