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
using System.Text.Json;

namespace Database
{
	// Roles and bans a trusted upstream provider manages; rows with source "local" are never touched here
	public static class Upstream
	{
		public const string ActorKind = "upstream";

		public sealed class Changes
		{
			public List<string> RolesAdded { get; } = new();
			public List<string> RolesRemoved { get; } = new();
			public bool BanStarted { get; set; }
			public bool BanEnded { get; set; }

			public bool Any => RolesAdded.Count > 0 || RolesRemoved.Count > 0 || BanStarted || BanEnded;
		}

		private static AuditLogEntry Entry(string source, string action, Int64 userID, object details, DateTime now)
		{
			return new AuditLogEntry
			{
				CreatedAt = now,
				ActorKind = ActorKind,
				// the actor is the provider name; source keeps the full "upstream:<provider>"
				Actor = source.StartsWith(AuthoritySources.UpstreamPrefix, StringComparison.Ordinal) ? source.Substring(AuthoritySources.UpstreamPrefix.Length) : source,
				Action = action,
				TargetKind = AuditTargetKinds.User,
				TargetID = userID,
				Source = source,
				Details = JsonSerializer.Serialize(details)
			};
		}

		// Makes the user's rows from this source equal the asserted state, in one transaction with their audit entries
		public static async Task<Changes> Apply(AppDbContext db, Int64 userID, string source, IReadOnlyCollection<string> roles, bool bBanned, string banReason)
		{
			var changes = new Changes();
			DateTime now = DateTime.UtcNow;

			await using var tx = await db.Database.BeginTransactionAsync();

			// owner is local only: a provider's mapping can never make anyone an owner, so it is dropped here
			roles = roles.Where(r => r != StaffRoles.Owner).ToList();

			List<UserRole> current = await db.UserRoles.AsTracking().Where(r => r.UserID == userID && r.Source == source).ToListAsync();
			foreach (UserRole row in current.Where(r => !roles.Contains(r.Role)))
			{
				db.UserRoles.Remove(row);
				changes.RolesRemoved.Add(row.Role);
				db.AuditLog.Add(Entry(source, ModerationActions.RoleRevoke, userID, new { role = row.Role }, now));
			}

			foreach (string role in roles.Where(r => current.All(c => c.Role != r)))
			{
				db.UserRoles.Add(new UserRole { UserID = userID, Role = role, Source = source, GrantedBy = source, GrantedAt = now });
				changes.RolesAdded.Add(role);
				db.AuditLog.Add(Entry(source, ModerationActions.RoleGrant, userID, new { role }, now));
			}

			List<Sanction> bans = await db.Sanctions.AsTracking()
				.Where(s => s.UserID == userID && s.Source == source && s.Type == SanctionTypes.Ban && s.LiftedAt == null && (s.ExpiresAt == null || s.ExpiresAt > now))
				.ToListAsync();

			if (bBanned && bans.Count == 0)
			{
				db.Sanctions.Add(new Sanction { UserID = userID, Type = SanctionTypes.Ban, Source = source, Reason = String.IsNullOrEmpty(banReason) ? null : banReason, CreatedByKind = ActorKind, CreatedAt = now });
				db.AuditLog.Add(Entry(source, ModerationActions.Ban, userID, new { type = SanctionTypes.Ban }, now));
				changes.BanStarted = true;
			}
			else if (!bBanned && bans.Count > 0)
			{
				foreach (Sanction ban in bans)
				{
					ban.LiftedAt = now;
					ban.LiftedByKind = ActorKind;
					ban.LiftReason = "removed upstream";
				}

				db.AuditLog.Add(Entry(source, ModerationActions.Unban, userID, new { type = SanctionTypes.Ban }, now));
				changes.BanEnded = true;
			}

			await db.SaveChangesAsync();
			await tx.CommitAsync();
			return changes;
		}

		public static async Task<string?> GetSetting(AppDbContext db, string key)
		{
			return await db.ApplicationSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync();
		}

		public static async Task SetSetting(AppDbContext db, string key, string jsonValue, string updatedBy)
		{
			ApplicationSetting? row = await db.ApplicationSettings.AsTracking().FirstOrDefaultAsync(s => s.Key == key);
			if (row == null)
			{
				db.ApplicationSettings.Add(new ApplicationSetting { Key = key, Value = jsonValue, UpdatedBy = updatedBy, UpdatedAt = DateTime.UtcNow });
			}
			else
			{
				row.Value = jsonValue;
				row.UpdatedBy = updatedBy;
				row.UpdatedAt = DateTime.UtcNow;
			}

			await db.SaveChangesAsync();
		}

		// True when a row was removed
		public static async Task<bool> DeleteSetting(AppDbContext db, string key)
		{
			ApplicationSetting? row = await db.ApplicationSettings.AsTracking().FirstOrDefaultAsync(s => s.Key == key);
			if (row == null)
			{
				return false;
			}

			db.ApplicationSettings.Remove(row);
			await db.SaveChangesAsync();
			return true;
		}
	}
}
