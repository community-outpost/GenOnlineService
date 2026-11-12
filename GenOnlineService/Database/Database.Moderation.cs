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
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

// Local rows apply to this instance only, global rows are shared by every environment once the accounts service exists
public enum EAuthorityScope
{
	Local,
	Global
}

public static class AuthorityScopeColumn
{
	public static readonly ValueConverter<EAuthorityScope, string> Converter = new(
		v => v == EAuthorityScope.Global ? "global" : "local",
		v => v == "global" ? EAuthorityScope.Global : EAuthorityScope.Local);

	public static string ToText(EAuthorityScope scope) => scope == EAuthorityScope.Global ? "global" : "local";
}

public class UserRole
{
	public Int64 UserID { get; set; }
	public string Role { get; set; } = String.Empty;
	public EAuthorityScope Scope { get; set; } = EAuthorityScope.Local;
	public Int64? GrantedBy { get; set; }
	public DateTime GrantedAt { get; set; } = DateTime.UnixEpoch;
	public string Reason { get; set; } = String.Empty;
}

public class Ban
{
	public Int64 ID { get; set; }
	public Int64 UserID { get; set; }
	public EAuthorityScope Scope { get; set; } = EAuthorityScope.Local;
	public string Reason { get; set; } = String.Empty;
	public Int64? IssuedBy { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;

	// Null means the ban never expires
	public DateTime? ExpiresAt { get; set; }

	public DateTime? RevokedAt { get; set; }
	public Int64? RevokedBy { get; set; }
	public string? RevokeReason { get; set; }
}

// Append only, every row is a moderation action that already happened
public class ModerationLogEntry
{
	public Int64 ID { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;
	public Int64? ActorUserID { get; set; }

	// Who acted when there is no account, such as "cli" or "discord:<id>"
	public string Actor { get; set; } = String.Empty;

	public string Action { get; set; } = String.Empty;
	public Int64? TargetUserID { get; set; }
	public string Reason { get; set; } = String.Empty;
	public string Source { get; set; } = String.Empty;
	public EAuthorityScope Scope { get; set; } = EAuthorityScope.Local;
	public string? Details { get; set; }
}

public static class ModerationActions
{
	public const string Kick = "kick";
	public const string Ban = "ban";
	public const string Unban = "unban";
	public const string RoleGrant = "role_grant";
	public const string RoleRevoke = "role_revoke";
}

public static class ModerationSources
{
	public const string WebSocket = "websocket";
	public const string Discord = "discord";
	public const string Cli = "cli";
}

public class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
	public void Configure(EntityTypeBuilder<UserRole> builder)
	{
		builder.ToTable("user_roles");

		builder.HasKey(e => new { e.UserID, e.Role, e.Scope });

		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.Role).HasColumnName("role").HasMaxLength(32);
		builder.Property(e => e.Scope).HasColumnName("scope").HasMaxLength(16).HasConversion(AuthorityScopeColumn.Converter);
		builder.Property(e => e.GrantedBy).HasColumnName("granted_by");
		builder.Property(e => e.GrantedAt).HasColumnName("granted_at");
		builder.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(256);
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
		builder.Property(e => e.Scope).HasColumnName("scope").HasMaxLength(16).HasConversion(AuthorityScopeColumn.Converter);
		builder.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(256);
		builder.Property(e => e.IssuedBy).HasColumnName("issued_by");
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		builder.Property(e => e.ExpiresAt).HasColumnName("expires_at");
		builder.Property(e => e.RevokedAt).HasColumnName("revoked_at");
		builder.Property(e => e.RevokedBy).HasColumnName("revoked_by");
		builder.Property(e => e.RevokeReason).HasColumnName("revoke_reason").HasMaxLength(256);
	}
}

public class ModerationLogConfiguration : IEntityTypeConfiguration<ModerationLogEntry>
{
	public void Configure(EntityTypeBuilder<ModerationLogEntry> builder)
	{
		builder.ToTable("moderation_log");

		builder.HasKey(e => e.ID);
		builder.HasIndex(e => e.TargetUserID).HasDatabaseName("ix_moderation_log_target_user_id");

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		builder.Property(e => e.ActorUserID).HasColumnName("actor_user_id");
		builder.Property(e => e.Actor).HasColumnName("actor").HasMaxLength(64);
		builder.Property(e => e.Action).HasColumnName("action").HasMaxLength(32);
		builder.Property(e => e.TargetUserID).HasColumnName("target_user_id");
		builder.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(256);
		builder.Property(e => e.Source).HasColumnName("source").HasMaxLength(16);
		builder.Property(e => e.Scope).HasColumnName("scope").HasMaxLength(16).HasConversion(AuthorityScopeColumn.Converter);
		builder.Property(e => e.Details).HasColumnName("details").HasMaxLength(1024);
	}
}

namespace Database
{
	public sealed class ModerationActor
	{
		public Int64? UserID { get; init; }
		public string Name { get; init; } = String.Empty;
		public string Source { get; init; } = String.Empty;
	}

	public static class Moderation
	{
		private static readonly ILogger s_log = AppLog.For(typeof(Moderation));

		private static IQueryable<Ban> ActiveBans(AppDbContext db, DateTime now)
		{
			return db.Bans.AsNoTracking().Where(b => b.RevokedAt == null && (b.ExpiresAt == null || b.ExpiresAt > now));
		}

		private static ModerationLogEntry LogEntry(ModerationActor actor, string action, Int64? target, string reason, EAuthorityScope scope, string? details, DateTime now)
		{
			return new ModerationLogEntry
			{
				CreatedAt = now,
				ActorUserID = actor.UserID,
				Actor = actor.Name,
				Action = action,
				TargetUserID = target,
				Reason = reason,
				Source = actor.Source,
				Scope = scope,
				Details = details
			};
		}

		public static async Task<List<UserRole>> GetAllRoles(AppDbContext db)
		{
			return await db.UserRoles.AsNoTracking().ToListAsync();
		}

		public static async Task<List<UserRole>> GetRoles(AppDbContext db, Int64 userID)
		{
			return await db.UserRoles.AsNoTracking().Where(r => r.UserID == userID).OrderBy(r => r.Role).ToListAsync();
		}

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

		public static async Task<List<Int64>> GetActiveBannedUserIDs(AppDbContext db)
		{
			return await ActiveBans(db, DateTime.UtcNow).Select(b => b.UserID).Distinct().ToListAsync();
		}

		// The ban that lasts longest decides what the player sees
		public static async Task<Ban?> GetActiveBan(AppDbContext db, Int64 userID)
		{
			return await ActiveBans(db, DateTime.UtcNow)
				.Where(b => b.UserID == userID)
				.OrderByDescending(b => b.ExpiresAt == null)
				.ThenByDescending(b => b.ExpiresAt)
				.FirstOrDefaultAsync();
		}

		public static async Task<List<Ban>> GetBans(AppDbContext db, Int64 userID)
		{
			return await db.Bans.AsNoTracking().Where(b => b.UserID == userID).OrderBy(b => b.CreatedAt).ToListAsync();
		}

		public static async Task<List<ModerationLogEntry>> GetLog(AppDbContext db, Int64 targetUserID, int limit)
		{
			return await db.ModerationLog.AsNoTracking().Where(e => e.TargetUserID == targetUserID).OrderByDescending(e => e.ID).Take(limit).ToListAsync();
		}

		// The role row and its log row are saved together, so the log never misses a change
		public static async Task<bool> GrantRole(AppDbContext db, Int64 userID, string role, EAuthorityScope scope, string reason, ModerationActor actor)
		{
			if (await db.UserRoles.AnyAsync(r => r.UserID == userID && r.Role == role && r.Scope == scope))
			{
				return false;
			}

			DateTime now = DateTime.UtcNow;
			db.UserRoles.Add(new UserRole { UserID = userID, Role = role, Scope = scope, GrantedBy = actor.UserID, GrantedAt = now, Reason = reason });
			db.ModerationLog.Add(LogEntry(actor, ModerationActions.RoleGrant, userID, reason, scope, role, now));

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

		public static async Task<bool> RevokeRole(AppDbContext db, Int64 userID, string role, EAuthorityScope scope, string reason, ModerationActor actor)
		{
			UserRole? existing = await db.UserRoles.FirstOrDefaultAsync(r => r.UserID == userID && r.Role == role && r.Scope == scope);
			if (existing == null)
			{
				return false;
			}

			db.UserRoles.Remove(existing);
			db.ModerationLog.Add(LogEntry(actor, ModerationActions.RoleRevoke, userID, reason, scope, role, DateTime.UtcNow));
			await db.SaveChangesAsync();
			return true;
		}

		public static async Task<Ban> AddBan(AppDbContext db, Int64 userID, EAuthorityScope scope, string reason, DateTime? expiresAt, ModerationActor actor)
		{
			DateTime now = DateTime.UtcNow;
			var ban = new Ban { UserID = userID, Scope = scope, Reason = reason, IssuedBy = actor.UserID, CreatedAt = now, ExpiresAt = expiresAt };
			string details = expiresAt == null ? "permanent" : $"expires {expiresAt.Value:yyyy-MM-dd HH:mm} UTC";

			db.Bans.Add(ban);
			db.ModerationLog.Add(LogEntry(actor, ModerationActions.Ban, userID, reason, scope, details, now));
			await db.SaveChangesAsync();
			return ban;
		}

		// Revoked bans stay in the table, so the history of every ban is kept
		public static async Task<int> RevokeActiveBans(AppDbContext db, Int64 userID, EAuthorityScope scope, string reason, ModerationActor actor)
		{
			DateTime now = DateTime.UtcNow;
			// every context defaults to no tracking, so the rows must be tracked for the revoke to be saved
			List<Ban> active = await db.Bans
				.AsTracking()
				.Where(b => b.UserID == userID && b.Scope == scope && b.RevokedAt == null && (b.ExpiresAt == null || b.ExpiresAt > now))
				.ToListAsync();

			if (active.Count == 0)
			{
				return 0;
			}

			foreach (Ban ban in active)
			{
				ban.RevokedAt = now;
				ban.RevokedBy = actor.UserID;
				ban.RevokeReason = reason;
			}

			db.ModerationLog.Add(LogEntry(actor, ModerationActions.Unban, userID, reason, scope, $"{active.Count} ban(s) revoked", now));
			await db.SaveChangesAsync();
			return active.Count;
		}

		// A failed log write never undoes an action that already happened, it is only reported
		public static async Task LogAction(AppDbContext db, ModerationActor actor, string action, Int64? target, string reason, string? details = null)
		{
			try
			{
				db.ModerationLog.Add(LogEntry(actor, action, target, reason, EAuthorityScope.Local, details, DateTime.UtcNow));
				await db.SaveChangesAsync();
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Moderation.LogAction failed for {Action} on user {UserId}", action, target);
			}
		}
	}
}
