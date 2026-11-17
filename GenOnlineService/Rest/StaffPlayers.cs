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

using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Security.Claims;

namespace GenOnlineService.Rest
{
	// An account as staff find it: blocked and deactivated accounts are listed with their state
	public sealed record StaffPlayer
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public string? Tag { get; init; }
		public string State { get; init; } = "";
		public DateTime CreatedAt { get; init; }
		public DateTime? LastSignInAt { get; init; }
	}

	// GET /api/v1/players/search
	public sealed record StaffPlayerListResponse
	{
		public IReadOnlyList<StaffPlayer> Players { get; init; } = [];
	}

	// moderation.kick or higher, the rule of the report queue
	public static class StaffAccess
	{
		private static readonly string[] s_permissions = [Permissions.ModerationKick, Permissions.ModerationMute, Permissions.ModerationBan];

		public static async Task<bool> IsStaffAsync(IAuthorizationService authorization, ClaimsPrincipal user)
		{
			foreach (string permission in s_permissions)
			{
				if ((await authorization.AuthorizeAsync(user, PermissionPolicies.For(permission))).Succeeded)
				{
					return true;
				}
			}
			return false;
		}
	}

	public static class StaffPlayerActions
	{
		public const int MinSearchLength = 2;
		public const int MaxSearchLength = User.UsernameMaxLength;

		// A part of a name of 2 to 32 characters, or a player id of any length
		public static (string? Search, Failure? Failure) ReadSearch(string? text)
		{
			string search = text?.Trim() ?? String.Empty;
			bool id = long.TryParse(search, NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value > 0;
			if (search.Length > MaxSearchLength || (search.Length < MinSearchLength && !id))
			{
				return (null, Result.Invalid("query"));
			}
			return (search, null);
		}

		public static async Task<(int Total, StaffPlayerListResponse Response)> SearchAsync(IDbContextFactory<AppDbContext> dbFactory, string search, int skip, int take, CancellationToken cancellationToken = default)
		{
			await using AppDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
			(int total, List<Database.StaffPlayerRow> rows) = await Database.Users.StaffSearch(db, search, skip, take, cancellationToken);
			return (total, new StaffPlayerListResponse
			{
				Players = rows.Select(row => new StaffPlayer
				{
					Id = row.ID,
					Name = row.Name,
					Tag = PlayerTags.Of(row.NameTag),
					State = row.State,
					CreatedAt = row.CreatedAt,
					LastSignInAt = row.LastSignInAt
				}).ToArray()
			});
		}
	}

	// The staff search over every account, the public search of GET /api/v1/players matches a prefix of visible accounts only
	[ApiController]
	[Authorize]
	public sealed class StaffPlayersController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;
		private readonly IAuthorizationService m_authorization;

		public StaffPlayersController(IDbContextFactory<AppDbContext> dbFactory, IAuthorizationService authorization)
		{
			m_dbFactory = dbFactory;
			m_authorization = authorization;
		}

		[HttpGet("~/api/v1/players/search")]
		public async Task<IActionResult> Search([FromQuery] string? query)
		{
			if (!await StaffAccess.IsStaffAsync(m_authorization, User))
			{
				return RestResults.Fail(Result.Forbidden(Permissions.ModerationKick));
			}

			(string? search, Failure? failure) = StaffPlayerActions.ReadSearch(query);
			if (failure == null)
			{
				(OffsetPage page, Failure? pageFailure) = Paging.ReadOffset(Request);
				if (pageFailure == null)
				{
					(int total, StaffPlayerListResponse response) = await StaffPlayerActions.SearchAsync(m_dbFactory, search!, page.Skip, page.PerPage, HttpContext.RequestAborted);
					Paging.WriteOffsetHeaders(HttpContext, page, total);
					return RestResults.Ok(response);
				}
				failure = pageFailure;
			}
			return RestResults.Fail(failure.Value);
		}
	}
}
