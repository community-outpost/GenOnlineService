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

using GenOnlineService.Api;
using GenOnlineService.Identity;
using GenOnlineService.Realtime;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GenOnlineService.Rest
{
	// A broken token or service account rule: the message is the CLI text, the failure is the REST answer
	public sealed class ApiTokenAdminException : Exception
	{
		public ApiTokenAdminException(string message, Failure failure) : base(message)
		{
			Failure = failure;
		}

		public Failure Failure { get; }
	}

	// Who changes tokens and service accounts; the label goes to created_by, granted_by, revoked_by and the audit actor
	public sealed record ApiTokenActor(string Kind, Int64? UserID, string Label, string Source)
	{
		public static ApiTokenActor Cli() => new ApiTokenActor(AuditActorKinds.Cli, null, "cli:" + Environment.UserName, ModerationSources.Cli);

		public static ApiTokenActor User(Int64 userID) => new ApiTokenActor(AuditActorKinds.User, userID, "user:" + userID, ModerationSources.Api);
	}

	public sealed record ApiTokenOwner(string Kind, Int64 ID, string Label)
	{
		public static ApiTokenOwner User(Int64 userID) => new ApiTokenOwner(ApiTokenOwnerKinds.User, userID, "user:" + userID);

		public static ApiTokenOwner Service(ServiceAccount account) => new ApiTokenOwner(ApiTokenOwnerKinds.ServiceAccount, account.ID, "service:" + account.Name);
	}

	public enum ApiTokenCallers { None, Token, GameSession, WebSession }

	// The token and service account rules and writes shared by the CLI and /api/v1; the CLI is the only caller with bFromHost
	public static class ApiTokenAdmin
	{
		public const int MaximumOverlapMinutes = 1440;

		private static readonly Regex s_serviceAccountName = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);

		private static ApiTokenAdminException Invalid(string field, string message) => new ApiTokenAdminException(message, Result.Invalid(field));

		private static ApiTokenAdminException Forbidden(string permission, string message) => new ApiTokenAdminException(message, Result.Forbidden(permission));

		// A gopat- token never manages tokens, and neither does a session of a game, launcher or chat client (design section 9, v2)
		public static ApiTokenCallers CallerOf(ClaimsPrincipal user) => CallerOf(user, clientID => ClientRegistry.FindEnabled(clientID)?.Kind);

		// clientKind maps the session's client_id to the registered client kind; only an app client counts as a web session,
		// and so does the staff pages' sign-in cookie, which only the routes marked [AllowSignInCookie] accept
		public static ApiTokenCallers CallerOf(ClaimsPrincipal user, Func<string?, string?> clientKind)
		{
			if (user.Identities.Any(i => i.IsAuthenticated && i.AuthenticationType == TokenPrincipal.AuthenticationType))
			{
				return ApiTokenCallers.Token;
			}

			if (ApiAuthentication.IsSignInCookie(user))
			{
				return RealtimeClaims.UserID(user) > 0 ? ApiTokenCallers.WebSession : ApiTokenCallers.None;
			}

			ClaimsIdentity? session = user.Identities.FirstOrDefault(i => i.IsAuthenticated && i.AuthenticationType == SessionTokenHandler.AuthenticationType);
			if (session == null || !Int64.TryParse(session.FindFirst(ClaimTypes.NameIdentifier)?.Value, out Int64 userID) || userID <= 0)
			{
				return ApiTokenCallers.None;
			}

			bool bApp = !session.HasClaim(c => c.Type == SessionTokenHandler.SessionTypeClaim)
				&& clientKind(session.FindFirst(SessionTokenHandler.ClientIDClaim)?.Value) == ClientKinds.App;
			return bApp ? ApiTokenCallers.WebSession : ApiTokenCallers.GameSession;
		}

		public static string RequirePermission(string permission, string field)
		{
			if (!Permissions.IsKnown(permission))
			{
				throw Invalid(field, $"Unknown permission: {permission}. Known: {String.Join(", ", Permissions.All.OrderBy(p => p, StringComparer.Ordinal))}");
			}

			return permission;
		}

		public static void CheckServiceAccountName(string name)
		{
			if (!s_serviceAccountName.IsMatch(name))
			{
				throw Invalid("name", "A service account name is 1 to 64 characters of a-z, 0-9, '-' and '_', starting with a letter or digit.");
			}
		}

		public static void CheckDescription(string description)
		{
			if (description.Length > 256)
			{
				throw Invalid("description", "The description must be 256 characters or fewer.");
			}
		}

		public static void CheckTokenName(string name)
		{
			if (name.Length == 0 || name.Length > ApiToken.NameMaxLength)
			{
				throw Invalid("name", $"--name is required, at most {ApiToken.NameMaxLength} characters.");
			}
		}

		// Known, never staff only, not machine on a user, privileged only from the host, and never more than the owner holds
		public static void CheckScopes(string ownerKind, IReadOnlyList<string> scopes, IReadOnlySet<string> held, string ownerLabel, bool bFromHost)
		{
			if (scopes.Count == 0)
			{
				throw Invalid("scopes", "--scopes needs at least one permission.");
			}

			foreach (string scope in scopes)
			{
				RequirePermission(scope, "scopes");
			}

			string? staffOnly = scopes.FirstOrDefault(Permissions.IsStaffOnly);
			if (staffOnly != null)
			{
				throw Forbidden(staffOnly, $"{staffOnly} works through the staff pages only, so no token carries it.");
			}

			if (ownerKind == ApiTokenOwnerKinds.User)
			{
				string? machine = scopes.FirstOrDefault(Permissions.IsMachine);
				if (machine != null)
				{
					throw Forbidden(machine, $"{machine} is a machine permission and only service accounts hold it.");
				}
			}

			string? privileged = bFromHost ? null : scopes.FirstOrDefault(Permissions.IsPrivileged);
			if (privileged != null)
			{
				throw Forbidden(privileged, $"{privileged} is privileged; tokens carrying it are minted through the CLI on the host.");
			}

			string? missing = scopes.FirstOrDefault(s => !held.Contains(s));
			if (missing != null)
			{
				throw Forbidden(missing, $"{ownerLabel} does not hold {missing}; a token never grants more than its owner holds.");
			}
		}

		// Over HTTP a privileged permission is never granted, and a human one only by a caller who holds it
		public static void CheckHttpGrant(string permission, IReadOnlyCollection<string> callerPermissions)
		{
			RequirePermission(permission, "grants");

			if (Permissions.IsPrivileged(permission))
			{
				throw Forbidden(permission, $"{permission} is privileged and is granted through the CLI on the host.");
			}

			RefuseStaffOnly(permission);

			if (!Permissions.IsMachine(permission) && !callerPermissions.Contains(permission))
			{
				throw Forbidden(permission, $"Granting {permission} needs the caller to hold it.");
			}
		}

		public static void CheckAllowedIPs(string? allowedIPs)
		{
			try
			{
				ApiTokenFormat.ParseAllowedIPs(allowedIPs);
			}
			catch (FormatException ex)
			{
				throw Invalid("allowed_ips", ex.Message);
			}
		}

		public static void CheckLifetime(ApiTokenSettings settings, IReadOnlyCollection<string> scopes, string? allowedIPs, DateTime expiresAt, DateTime now)
		{
			if (expiresAt <= now)
			{
				throw Invalid("expires_at", "The expiry date must be in the future.");
			}

			bool bPrivileged = scopes.Any(Permissions.IsPrivileged);
			int maxDays = bPrivileged ? settings.PrivilegedMaxLifetimeDays : settings.MaxLifetimeDays;
			if (expiresAt > now.AddDays(maxDays))
			{
				throw Invalid("expires_at", $"{(bPrivileged ? "A token with a privileged scope" : "A token")} may live at most {maxDays} days.");
			}

			if (bPrivileged && ApiTokenFormat.ParseAllowedIPs(allowedIPs).Count == 0)
			{
				throw Invalid("allowed_ips", "A token with a privileged scope needs --allowed-ips.");
			}
		}

		public static void CheckActiveCount(int activeCount, ApiTokenSettings settings, string ownerLabel)
		{
			if (activeCount >= settings.MaxActivePerOwner)
			{
				throw new ApiTokenAdminException($"{ownerLabel} already has {settings.MaxActivePerOwner} active tokens.", Result.Fail(RestCodes.TokenLimitReached, new { Max = settings.MaxActivePerOwner }));
			}
		}

		public static int CheckOverlap(string? text)
		{
			int overlap = 0;
			if (text != null && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out overlap) || overlap > MaximumOverlapMinutes))
			{
				throw Invalid("overlap_minutes", $"--overlap-minutes is 0 to {MaximumOverlapMinutes}.");
			}

			return overlap;
		}

		// A date is midnight UTC, a timestamp without an offset is UTC
		public static DateTime ParseExpiresAt(string? text)
		{
			if (String.IsNullOrWhiteSpace(text)
				|| !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime value))
			{
				throw Invalid("expires_at", "expires_at must be an ISO 8601 date or UTC timestamp.");
			}

			return DateTime.SpecifyKind(value, DateTimeKind.Utc);
		}

		public static AuditLogEntry Audit(ApiTokenActor actor, string action, string targetKind, Int64 targetID, object details, string? reason = null)
		{
			return new AuditLogEntry
			{
				CreatedAt = DateTime.UtcNow,
				ActorKind = actor.Kind,
				ActorID = actor.UserID,
				Actor = actor.Label.Length > 64 ? actor.Label.Substring(0, 64) : actor.Label,
				Action = action,
				TargetKind = targetKind,
				TargetID = targetID,
				Reason = String.IsNullOrEmpty(reason) ? null : reason,
				Source = actor.Source,
				Details = JsonSerializer.Serialize(details)
			};
		}

		// What the owner holds right now; a token never grants more
		public static async Task<HashSet<string>> OwnerPermissions(AppDbContext db, ApiTokenOwner owner)
		{
			if (owner.Kind == ApiTokenOwnerKinds.ServiceAccount)
			{
				return (await db.ServiceAccountPermissions.AsNoTracking().Where(p => p.ServiceAccountID == owner.ID).Select(p => p.Permission).ToListAsync())
					.ToHashSet(StringComparer.Ordinal);
			}

			List<string> roles = await db.UserRoles.AsNoTracking().Where(r => r.UserID == owner.ID).Select(r => r.Role).ToListAsync();
			return StaffRoles.PermissionsFor(roles);
		}

		public static IQueryable<ApiToken> OwnedBy(IQueryable<ApiToken> tokens, ApiTokenOwner owner)
		{
			return owner.Kind == ApiTokenOwnerKinds.ServiceAccount ? tokens.Where(t => t.OwnerServiceID == owner.ID) : tokens.Where(t => t.OwnerUserID == owner.ID);
		}

		public static async Task CheckActiveLimit(AppDbContext db, ApiTokenSettings settings, ApiTokenOwner owner)
		{
			DateTime now = DateTime.UtcNow;
			IQueryable<ApiToken> active = OwnedBy(db.ApiTokens.AsNoTracking(), owner).Where(t => t.ExpiresAt > now && (t.RevokedAt == null || t.RevokedAt > now));
			CheckActiveCount(await active.CountAsync(), settings, owner.Label);
		}

		private static ApiToken NewToken(ApiTokenActor actor, ApiTokenOwner owner, string name, string scopes, string? allowedIPs, DateTime expiresAt, out string secret)
		{
			secret = ApiTokenFormat.Generate();
			return new ApiToken
			{
				OwnerUserID = owner.Kind == ApiTokenOwnerKinds.User ? owner.ID : null,
				OwnerServiceID = owner.Kind == ApiTokenOwnerKinds.ServiceAccount ? owner.ID : null,
				Name = name,
				TokenHash = ApiTokenFormat.Hash(secret),
				TokenLast4 = ApiTokenFormat.Last4(secret),
				Scopes = scopes,
				AllowedIPs = String.IsNullOrWhiteSpace(allowedIPs) ? null : allowedIPs,
				CreatedBy = actor.Label,
				CreatedAt = DateTime.UtcNow,
				ExpiresAt = expiresAt
			};
		}

		// Checks every rule in the CLI's order, then writes the row and its token_mint entry together; the secret is returned once and never stored
		public static async Task<(ApiToken Token, string Secret)> Mint(AppDbContext db, ApiTokenSettings settings, ApiTokenActor actor, ApiTokenOwner owner, string name, List<string> scopes, string? allowedIPs, Func<DateTime> readExpiresAt, bool bFromHost)
		{
			CheckTokenName(name);
			CheckScopes(owner.Kind, scopes, await OwnerPermissions(db, owner), owner.Label, bFromHost);
			CheckAllowedIPs(allowedIPs);
			DateTime expiresAt = readExpiresAt();
			CheckLifetime(settings, scopes, allowedIPs, expiresAt, DateTime.UtcNow);
			await CheckActiveLimit(db, settings, owner);

			ApiToken token = NewToken(actor, owner, name, String.Join(' ', scopes), allowedIPs, expiresAt, out string secret);
			db.ApiTokens.Add(token);
			// the row and its audit entry commit together
			await using (var tx = await db.Database.BeginTransactionAsync())
			{
				await db.SaveChangesAsync();
				db.AuditLog.Add(Audit(actor, "token_mint", AuditTargetKinds.Token, token.ID, new { name, owner = owner.Label, scopes, expires_at = expiresAt, allowed_ips = token.AllowedIPs }));
				await db.SaveChangesAsync();
				await tx.CommitAsync();
			}

			return (token, secret);
		}

		public static async Task<ApiTokenOwner> OwnerOf(AppDbContext db, ApiToken token)
		{
			return token.OwnerServiceID != null
				? new ApiTokenOwner(ApiTokenOwnerKinds.ServiceAccount, token.OwnerServiceID.Value, "service:" + (await db.ServiceAccounts.AsNoTracking().Where(a => a.ID == token.OwnerServiceID).Select(a => a.Name).FirstAsync()))
				: ApiTokenOwner.User(token.OwnerUserID!.Value);
		}

		// Same scopes and allow-list, the old token stops after the overlap; a null expiry keeps the old lifetime. A token with
		// a privileged scope is rotated only from the host, as it is minted
		public static async Task<(ApiToken Token, string Secret)> Rotate(AppDbContext db, ApiTokenSettings settings, ApiTokenActor actor, ApiToken old, Func<int> readOverlap, Func<DateTime?> readExpiresAt, bool bFromHost)
		{
			DateTime now = DateTime.UtcNow;
			if (old.ExpiresAt <= now || old.RevokedAt != null)
			{
				throw new ApiTokenAdminException($"Token {old.ID} is revoked, expired or already rotated; rotate its replacement or create a new one.", Result.Fail(RestCodes.Conflict));
			}

			string? privileged = bFromHost ? null : ApiTokenFormat.ParseScopes(old.Scopes).FirstOrDefault(Permissions.IsPrivileged);
			if (privileged != null)
			{
				throw Forbidden(privileged, $"{privileged} is privileged; tokens carrying it are rotated through the CLI on the host.");
			}

			int overlap = readOverlap();
			ApiTokenOwner owner = await OwnerOf(db, old);
			List<string> scopes = ApiTokenFormat.ParseScopes(old.Scopes);
			DateTime newExpiresAt = readExpiresAt() ?? now + (old.ExpiresAt - old.CreatedAt);
			CheckLifetime(settings, scopes, old.AllowedIPs, newExpiresAt, now);
			if (overlap > 0)
			{
				await CheckActiveLimit(db, settings, owner);
			}

			ApiToken token = NewToken(actor, owner, old.Name, old.Scopes, old.AllowedIPs, newExpiresAt, out string secret);
			old.RevokedAt = now.AddMinutes(overlap);
			old.RevokedBy = actor.Label;
			db.ApiTokens.Add(token);
			// the row and its audit entry commit together
			await using (var tx = await db.Database.BeginTransactionAsync())
			{
				await db.SaveChangesAsync();
				db.AuditLog.Add(Audit(actor, "token_rotate", AuditTargetKinds.Token, token.ID, new { old_token_id = old.ID, overlap_minutes = overlap, expires_at = newExpiresAt }));
				await db.SaveChangesAsync();
				await tx.CommitAsync();
			}

			return (token, secret);
		}

		// False when the token is already revoked; a token inside a rotation overlap stops now
		public static async Task<bool> Revoke(AppDbContext db, ApiTokenActor actor, ApiToken token, string reason)
		{
			if (reason.Length > ModerationManager.MaximumReasonLength)
			{
				throw Invalid("reason", $"The reason must be {ModerationManager.MaximumReasonLength} characters or fewer.");
			}

			DateTime now = DateTime.UtcNow;
			if (token.RevokedAt != null && token.RevokedAt <= now)
			{
				return false;
			}

			token.RevokedAt = now;
			token.RevokedBy = actor.Label;
			db.AuditLog.Add(Audit(actor, "token_revoke", AuditTargetKinds.Token, token.ID, new { reason }, reason));
			await db.SaveChangesAsync();
			return true;
		}

		public static async Task<ServiceAccount> CreateServiceAccount(AppDbContext db, ApiTokenActor actor, string name, string description)
		{
			CheckServiceAccountName(name);
			CheckDescription(description);

			if (await db.ServiceAccounts.AnyAsync(a => a.Name == name))
			{
				throw new ApiTokenAdminException($"Service account {name} already exists.", Result.Fail(RestCodes.Conflict));
			}

			var account = new ServiceAccount { Name = name, Description = description, CreatedBy = actor.Label, CreatedAt = DateTime.UtcNow };
			db.ServiceAccounts.Add(account);
			// the row and its audit entry commit together
			await using (var tx = await db.Database.BeginTransactionAsync())
			{
				await db.SaveChangesAsync();
				db.AuditLog.Add(Audit(actor, "sa_create", AuditTargetKinds.ServiceAccount, account.ID, new { name, description }));
				await db.SaveChangesAsync();
				await tx.CommitAsync();
			}

			return account;
		}

		// A staff-only permission never goes to a service account, over HTTP or on the host
		private static void RefuseStaffOnly(string permission)
		{
			if (Permissions.IsStaffOnly(permission))
			{
				throw Forbidden(permission, $"{permission} is for staff on the staff pages and is never granted to a service account.");
			}
		}

		// Stages the grant and its sa_grant entry for the caller's SaveChanges; false when the account already holds it
		public static async Task<bool> StageGrant(AppDbContext db, ApiTokenActor actor, ServiceAccount account, string permission)
		{
			RefuseStaffOnly(permission);
			if (await db.ServiceAccountPermissions.AnyAsync(p => p.ServiceAccountID == account.ID && p.Permission == permission))
			{
				return false;
			}

			db.ServiceAccountPermissions.Add(new ServiceAccountPermission { ServiceAccountID = account.ID, Permission = permission, GrantedBy = actor.Label, GrantedAt = DateTime.UtcNow });
			db.AuditLog.Add(Audit(actor, "sa_grant", AuditTargetKinds.ServiceAccount, account.ID, new { permission, privileged = Permissions.IsPrivileged(permission) }));
			return true;
		}

		// Stages the removal and its sa_revoke entry for the caller's SaveChanges; false when the account does not hold it
		public static async Task<bool> StageRevokeGrant(AppDbContext db, ApiTokenActor actor, ServiceAccount account, string permission)
		{
			ServiceAccountPermission? existing = await db.ServiceAccountPermissions.AsTracking().FirstOrDefaultAsync(p => p.ServiceAccountID == account.ID && p.Permission == permission);
			if (existing == null)
			{
				return false;
			}

			db.ServiceAccountPermissions.Remove(existing);
			db.AuditLog.Add(Audit(actor, "sa_revoke", AuditTargetKinds.ServiceAccount, account.ID, new { permission }));
			return true;
		}

		// Stages the new description and its sa_update entry for the caller's SaveChanges; false when it does not change
		public static bool StageDescription(AppDbContext db, ApiTokenActor actor, ServiceAccount account, string description)
		{
			CheckDescription(description);
			if (account.Description == description)
			{
				return false;
			}

			string old = account.Description;
			account.Description = description;
			db.AuditLog.Add(Audit(actor, "sa_update", AuditTargetKinds.ServiceAccount, account.ID, new { name = account.Name, old_description = old, description }));
			return true;
		}

		// Stages the change and its sa_disable or sa_enable entry; the entry is written even when the state does not change, as the CLI does
		public static void StageSetDisabled(AppDbContext db, ApiTokenActor actor, ServiceAccount account, bool bDisabled)
		{
			account.DisabledAt = bDisabled ? DateTime.UtcNow : null;
			db.AuditLog.Add(Audit(actor, bDisabled ? "sa_disable" : "sa_enable", AuditTargetKinds.ServiceAccount, account.ID, new { name = account.Name }));
		}
	}
}
