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
using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService.Rest
{
	[ApiFixture("put-players-roles.request")]
	public sealed record RolesRequest
	{
		public IReadOnlyList<string?>? Roles { get; init; }
	}

	[ApiFixture("get-players-roles.response")]
	public sealed record RolesResponse
	{
		public IReadOnlyList<string> Roles { get; init; } = [];
	}

	public static class RoleRules
	{
		// The same names the CLI accepts: known roles, case folded, each once
		public static (IReadOnlyList<string>? Roles, Failure? Failure) Normalize(IReadOnlyList<string?>? roles)
		{
			if (roles == null)
			{
				return (null, Result.Invalid("roles"));
			}

			var result = new SortedSet<string>(StringComparer.Ordinal);
			foreach (string? role in roles)
			{
				string name = role?.ToLowerInvariant() ?? String.Empty;
				if (!StaffRoles.IsKnown(name))
				{
					return (null, Result.Invalid("roles"));
				}
				result.Add(name);
			}

			return (result.ToList(), null);
		}

		// The rank of a user from the role rows in the database (every source), so a change made a moment ago counts; a
		// failed read throws instead of ranking the user as a player
		public static async Task<int> RankAsync(AppDbContext db, long userID) => StaffRoles.RankOf((await Database.Moderation.GetRoles(db, userID)).Select(r => r.Role));

		// The account acting: a session or a user's token; a service account token stores its own id in UserID
		private static long? AccountOf(Database.ModerationActor actor) => actor.Kind == AuditActorKinds.User ? actor.UserID : null;

		// A caller without an account (a service account token) keeps admin standing, as for staff renames
		public static async Task<int> CallerRankAsync(AppDbContext db, Database.ModerationActor actor) => AccountOf(actor) is long id ? await RankAsync(db, id) : StaffRoles.AdminRank;

		// The rank rule of role changes, the moderation rank rule applied to roles: the caller outranks the player (so
		// never acts on themself or a peer), and every role granted or revoked ranks strictly below the caller's own.
		// So an owner manages admins, an admin moderators and streamers, and owner itself is granted with the CLI only
		public static Failure? Check(Database.ModerationActor actor, int actorRank, long target, int targetRank, IEnumerable<string> changed)
		{
			Failure? failure = Names.PlayerNames.CheckStaff(AccountOf(actor), actorRank, target, targetRank, null);
			if (failure != null)
			{
				return failure;
			}
			foreach (string role in changed)
			{
				if (StaffRoles.RankOf([role]) >= actorRank)
				{
					return Result.Fail(RestCodes.RoleRank, new { Role = role });
				}
			}
			return null;
		}

		// The known roles the caller may grant or revoke on this player, empty when the caller does not outrank them
		public static IReadOnlyList<string> Assignable(Database.ModerationActor actor, int actorRank, long target, int targetRank)
		{
			if (Names.PlayerNames.CheckStaff(AccountOf(actor), actorRank, target, targetRank, null) != null)
			{
				return [];
			}
			return StaffRoles.All.Where(role => StaffRoles.RankOf([role]) < actorRank).Order(StringComparer.Ordinal).ToList();
		}
	}

	// Staff roles of one player; PUT and DELETE manage the local rows, rows from an upstream provider follow its sign-ins.
	// Every change follows the rank rule (RoleRules.Check); the routes of one role also take the sign-in cookie
	[ApiController]
	[Authorize(Policy = PermissionPolicies.Prefix + Permissions.RolesManage)]
	public sealed class RolesController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public RolesController(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		[HttpGet("~/api/v1/players/{id:long}/roles")]
		public async Task<IActionResult> Get(long id)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			if (!await Database.Users.Exists(db, id))
			{
				return RestResults.NotFound("user");
			}
			return RestResults.Ok(await Answer(db, id));
		}

		[HttpPut("~/api/v1/players/{id:long}/roles")]
		public async Task<IActionResult> Put(long id, [FromBody] RolesRequest? body)
		{
			(IReadOnlyList<string>? roles, Failure? failure) = RoleRules.Normalize(body?.Roles);
			if (failure != null)
			{
				return RestResults.Fail(failure.Value);
			}

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			if (!await Database.Users.Exists(db, id))
			{
				return RestResults.NotFound("user");
			}

			failure = await SetLocalRoles(db, id, roles!);
			return failure != null ? RestResults.Fail(failure.Value) : RestResults.Ok(await Answer(db, id));
		}

		[HttpDelete("~/api/v1/players/{id:long}/roles")]
		public async Task<IActionResult> Delete(long id)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			if (!await Database.Users.Exists(db, id))
			{
				return RestResults.NotFound("user");
			}

			Failure? failure = await SetLocalRoles(db, id, []);
			return failure != null ? RestResults.Fail(failure.Value) : new NoContentResult();
		}

		// Grants one local role; a role the player already holds locally answers the same and changes nothing
		[AllowSignInCookie]
		[HttpPut("~/api/v1/players/{id:long}/roles/{role}")]
		public async Task<IActionResult> Grant(long id, string role)
		{
			string name = role.ToLowerInvariant();
			if (!StaffRoles.IsKnown(name))
			{
				return RestResults.Invalid("role");
			}

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			if (!await Database.Users.Exists(db, id))
			{
				return RestResults.NotFound("user");
			}

			Database.ModerationActor actor = CallerActors.Of(User);
			Failure? failure = RoleRules.Check(actor, await RoleRules.CallerRankAsync(db, actor), id, await RoleRules.RankAsync(db, id), [name]);
			if (failure != null)
			{
				return RestResults.Fail(failure.Value);
			}

			if (await Database.Moderation.GrantRole(db, id, name, AuthoritySources.Local, String.Empty, actor))
			{
				await StaffRoles.Reload(db);
			}
			return RestResults.Ok(await Answer(db, id));
		}

		// Revokes one local role; a role held only through a sign-in provider follows its sign-ins and is refused
		[AllowSignInCookie]
		[HttpDelete("~/api/v1/players/{id:long}/roles/{role}")]
		public async Task<IActionResult> Revoke(long id, string role)
		{
			string name = role.ToLowerInvariant();
			if (!StaffRoles.IsKnown(name))
			{
				return RestResults.Invalid("role");
			}

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			if (!await Database.Users.Exists(db, id))
			{
				return RestResults.NotFound("user");
			}

			Database.ModerationActor actor = CallerActors.Of(User);
			Failure? failure = RoleRules.Check(actor, await RoleRules.CallerRankAsync(db, actor), id, await RoleRules.RankAsync(db, id), [name]);
			if (failure != null)
			{
				return RestResults.Fail(failure.Value);
			}

			List<UserRole> rows = (await Database.Moderation.GetRoles(db, id)).Where(r => r.Role == name).ToList();
			if (!rows.Any(r => r.Source == AuthoritySources.Local))
			{
				return rows.Count > 0 ? RestResults.Fail(RestCodes.RoleUpstream, new { Role = name, Source = rows[0].Source }) : RestResults.NotFound("role");
			}

			if (await Database.Moderation.RevokeRole(db, id, name, AuthoritySources.Local, String.Empty, actor))
			{
				await StaffRoles.Reload(db);
			}
			return new NoContentResult();
		}

		// Each grant and revoke is saved with its audit row, as in the CLI; the reload applies the change at once. Only the
		// roles that change pass the rank rule, so a request that changes nothing answers as before
		private async Task<Failure?> SetLocalRoles(AppDbContext db, long userID, IReadOnlyList<string> roles)
		{
			Database.ModerationActor actor = CallerActors.Of(User);
			List<string> current = (await Database.Moderation.GetRoles(db, userID)).Where(r => r.Source == AuthoritySources.Local).Select(r => r.Role).ToList();
			List<string> granted = roles.Except(current, StringComparer.Ordinal).ToList();
			List<string> revoked = current.Except(roles, StringComparer.Ordinal).ToList();

			if (granted.Count + revoked.Count > 0)
			{
				Failure? failure = RoleRules.Check(actor, await RoleRules.CallerRankAsync(db, actor), userID, await RoleRules.RankAsync(db, userID), granted.Concat(revoked));
				if (failure != null)
				{
					return failure;
				}
			}

			foreach (string role in granted)
			{
				await Database.Moderation.GrantRole(db, userID, role, AuthoritySources.Local, String.Empty, actor);
			}
			foreach (string role in revoked)
			{
				await Database.Moderation.RevokeRole(db, userID, role, AuthoritySources.Local, String.Empty, actor);
			}

			await StaffRoles.Reload(db);
			return null;
		}

		// Every source counts, so a role granted upstream shows next to the local ones
		private static async Task<RolesResponse> Answer(AppDbContext db, long userID)
		{
			List<UserRole> rows = await Database.Moderation.GetRoles(db, userID);
			return new RolesResponse { Roles = rows.Select(r => r.Role).Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal).ToList() };
		}
	}
}
