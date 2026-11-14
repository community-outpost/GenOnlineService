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
	}

	// Staff roles of one player; PUT and DELETE manage the local rows, rows from an upstream provider follow its sign-ins
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

			await SetLocalRoles(db, id, roles!);
			return RestResults.Ok(await Answer(db, id));
		}

		[HttpDelete("~/api/v1/players/{id:long}/roles")]
		public async Task<IActionResult> Delete(long id)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			if (!await Database.Users.Exists(db, id))
			{
				return RestResults.NotFound("user");
			}

			await SetLocalRoles(db, id, []);
			return new NoContentResult();
		}

		// Each grant and revoke is saved with its audit row, as in the CLI; the reload applies the change at once
		private async Task SetLocalRoles(AppDbContext db, long userID, IReadOnlyList<string> roles)
		{
			Database.ModerationActor actor = CallerActors.Of(User);
			List<string> current = (await Database.Moderation.GetRoles(db, userID)).Where(r => r.Source == AuthoritySources.Local).Select(r => r.Role).ToList();

			foreach (string role in roles.Except(current, StringComparer.Ordinal))
			{
				await Database.Moderation.GrantRole(db, userID, role, AuthoritySources.Local, String.Empty, actor);
			}
			foreach (string role in current.Except(roles, StringComparer.Ordinal))
			{
				await Database.Moderation.RevokeRole(db, userID, role, AuthoritySources.Local, String.Empty, actor);
			}

			await StaffRoles.Reload(db);
		}

		// Every source counts, so a role granted upstream shows next to the local ones
		private static async Task<RolesResponse> Answer(AppDbContext db, long userID)
		{
			List<UserRole> rows = await Database.Moderation.GetRoles(db, userID);
			return new RolesResponse { Roles = rows.Select(r => r.Role).Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal).ToList() };
		}
	}
}
