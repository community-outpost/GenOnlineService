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

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace GenOnlineService
{
	// Code checks these permissions, never role names, so a new role only changes the mapping below
	public static class Permissions
	{
		public const string ModerationKick = "moderation.kick";
		public const string ModerationBan = "moderation.ban";
		public const string ModerationMute = "moderation.mute";
		public const string ChatStaffBadge = "chat.staff_badge";
		public const string NamesProtected = "names.protected";
		public const string SocialUnblockable = "social.unblockable";
		public const string RolesManage = "roles.manage";
		public const string ServerStats = "server.stats";
		public const string AnnouncementsSend = "announcements.send";
	}

	// The in-memory copy is reloaded on the reconcile tick, so a change made in the database applies without a restart
	public static class StaffRoles
	{
		private static readonly ILogger s_log = AppLog.For(typeof(StaffRoles));

		public const string Moderator = "moderator";
		public const string Admin = "admin";

		private static readonly string[] s_moderatorPermissions =
		{
			Permissions.ModerationKick,
			Permissions.ModerationMute
		};

		private static readonly string[] s_adminPermissions = s_moderatorPermissions.Concat(new[]
		{
			Permissions.ModerationBan,
			Permissions.ChatStaffBadge,
			Permissions.NamesProtected,
			Permissions.SocialUnblockable,
			Permissions.RolesManage,
			Permissions.ServerStats,
			Permissions.AnnouncementsSend
		}).ToArray();

		private static readonly Dictionary<string, HashSet<string>> s_rolePermissions = new(StringComparer.Ordinal)
		{
			[Moderator] = new HashSet<string>(s_moderatorPermissions, StringComparer.Ordinal),
			[Admin] = new HashSet<string>(s_adminPermissions, StringComparer.Ordinal)
		};

		private static volatile Dictionary<Int64, HashSet<string>> s_permissionsByUser = new();
		private static string s_lastUnknownRoles = String.Empty;

		public static IReadOnlyCollection<string> All => s_rolePermissions.Keys;

		public static bool IsKnown(string role) => s_rolePermissions.ContainsKey(role);

		public static bool HasPermission(Int64 userID, string permission)
		{
			return s_permissionsByUser.TryGetValue(userID, out HashSet<string>? permissions) && permissions.Contains(permission);
		}

		// A role the code does not know grants nothing, so a typo in the database never widens access
		public static HashSet<string> PermissionsFor(IEnumerable<string> roles)
		{
			var result = new HashSet<string>(StringComparer.Ordinal);

			foreach (string role in roles)
			{
				if (s_rolePermissions.TryGetValue(role, out HashSet<string>? permissions))
				{
					result.UnionWith(permissions);
				}
			}

			return result;
		}

		public static async Task Reload(AppDbContext db)
		{
			List<UserRole> rows = await Database.Moderation.GetAllRoles(db);

			var next = rows
				.GroupBy(r => r.UserID)
				.ToDictionary(g => g.Key, g => PermissionsFor(g.Select(r => r.Role)));

			// the warning repeats only when the set of unknown roles changes, not on every reload
			string unknown = string.Join(", ", rows.Select(r => r.Role).Where(r => !IsKnown(r)).Distinct().OrderBy(r => r, StringComparer.Ordinal));
			if (unknown.Length > 0 && unknown != s_lastUnknownRoles)
			{
				s_log.LogWarning("Ignoring unknown staff role(s) {Roles} in user_roles", unknown);
			}

			s_lastUnknownRoles = unknown;
			s_permissionsByUser = next;
		}
	}

	// [Authorize(Policy = PermissionPolicies.Prefix + Permissions.ModerationKick)] or PermissionPolicies.For(...) selects a permission policy
	public static class PermissionPolicies
	{
		public const string Prefix = "perm:";

		public static string For(string permission) => Prefix + permission;
	}

	// Identities created by the API token scheme use this authentication type and carry their permissions as claims
	public static class TokenPrincipal
	{
		public const string AuthenticationType = "gopat";
		public const string PermissionClaim = "go_permission";
	}

	public sealed class PermissionRequirement : IAuthorizationRequirement
	{
		public PermissionRequirement(string permission)
		{
			Permission = permission;
		}

		public string Permission { get; }
	}

	// Builds "perm:<name>" policies on demand, every other policy name keeps its configured definition
	public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
	{
		private readonly DefaultAuthorizationPolicyProvider m_fallback;

		public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
		{
			m_fallback = new DefaultAuthorizationPolicyProvider(options);
		}

		public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => m_fallback.GetDefaultPolicyAsync();

		public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => m_fallback.GetFallbackPolicyAsync();

		public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
		{
			if (policyName.StartsWith(PermissionPolicies.Prefix, StringComparison.Ordinal) && policyName.Length > PermissionPolicies.Prefix.Length)
			{
				AuthorizationPolicy policy = new AuthorizationPolicyBuilder()
					.RequireAuthenticatedUser()
					.AddRequirements(new PermissionRequirement(policyName.Substring(PermissionPolicies.Prefix.Length)))
					.Build();
				return Task.FromResult<AuthorizationPolicy?>(policy);
			}

			return m_fallback.GetPolicyAsync(policyName);
		}
	}

	// A token principal passes on its permission claims, a user session passes on the staff roles of its user
	public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
	{
		protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
		{
			// the identity type is set by the scheme that authenticated it, so a claim inside a token can never fake it
			ClaimsIdentity? tokenIdentity = context.User.Identities.FirstOrDefault(i => i.IsAuthenticated && i.AuthenticationType == TokenPrincipal.AuthenticationType);
			if (tokenIdentity != null)
			{
				if (tokenIdentity.HasClaim(TokenPrincipal.PermissionClaim, requirement.Permission))
				{
					context.Succeed(requirement);
				}

				return Task.CompletedTask;
			}

			string? subject = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
			if (Int64.TryParse(subject, out Int64 userID) && userID > 0 && StaffRoles.HasPermission(userID, requirement.Permission))
			{
				context.Succeed(requirement);
			}

			return Task.CompletedTask;
		}
	}
}
