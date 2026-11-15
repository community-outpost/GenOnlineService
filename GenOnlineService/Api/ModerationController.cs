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

using GenOnlineService.Names;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService.Api
{
	using GenOnlineService.Realtime;

	// Sanctions, renames, message deletes and reports (section 10.2), staff actions check their perm:<permission> policy and the rank rule
	public sealed class ModerationController : ApiControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;
		private readonly PlayerNames m_names;

		public ModerationController(IDbContextFactory<AppDbContext> dbFactory, IAuthorizationService authorization, PlayerNames names) : base(authorization)
		{
			m_dbFactory = dbFactory;
			m_names = names;
		}

		// Replaces an offensive name, the tag stays
		[HttpPatch("~/api/v1/players/{userID:long}")]
		public async Task<IActionResult> RenamePlayer(long userID)
		{
			if (await RequirePermissionAsync(Permissions.ModerationRename) is Failure denied)
			{
				return ApiResults.Fail(denied);
			}

			(RenamePlayerRequest? body, Failure? malformed) = await ReadBodyAsync<RenamePlayerRequest>();
			if (malformed != null)
			{
				return ApiResults.Fail(malformed.Value);
			}
			return ApiResults.NoContent(await m_names.RenameByStaffAsync(SanctionActions.ApiActorOf(Me), userID, body?.Name, body?.Reason));
		}

		[HttpPost("~/api/v1/players/{userID:long}/sanctions")]
		public async Task<IActionResult> CreateSanction(long userID)
		{
			(CreateSanctionRequest? body, Failure? malformed) = await ReadBodyAsync<CreateSanctionRequest>();
			if (malformed != null)
			{
				return ApiResults.Fail(malformed.Value);
			}
			return ApiResults.Answer(await SanctionActions.CreateAsync(m_dbFactory, Me, userID, body ?? new CreateSanctionRequest(), RequirePermissionAsync));
		}

		// moderation.kick or higher reads the list
		[HttpGet("~/api/v1/players/{userID:long}/sanctions")]
		public async Task<IActionResult> GetSanctions(long userID, [FromQuery] string? active)
		{
			if (await RequireStaffAsync() is Failure denied)
			{
				return ApiResults.Fail(denied);
			}

			bool activeOnly = false;
			if (active != null && !Boolean.TryParse(active, out activeOnly))
			{
				return ApiResults.Fail(Result.Invalid("active"));
			}
			return ApiResults.Answer(await SanctionActions.ListAsync(m_dbFactory, userID, activeOnly));
		}

		[HttpDelete("~/api/v1/sanctions/{sanctionID:long}")]
		public async Task<IActionResult> LiftSanction(long sanctionID)
		{
			// a caller who can lift neither kind learns nothing about the sanction
			if (await RequirePermissionAsync(Permissions.ModerationMute) != null && await RequirePermissionAsync(Permissions.ModerationBan) != null)
			{
				return ApiResults.Fail(Result.Forbidden(Permissions.ModerationMute));
			}

			(ReasonRequest? body, Failure? malformed) = await ReadBodyAsync<ReasonRequest>();
			if (malformed != null)
			{
				return ApiResults.Fail(malformed.Value);
			}
			return ApiResults.NoContent(await SanctionActions.LiftAsync(m_dbFactory, Me, sanctionID, body?.Reason, RequirePermissionAsync));
		}

		[HttpDelete("~/api/v1/messages/{messageID}")]
		public async Task<IActionResult> DeleteMessage(string messageID)
		{
			(ReasonRequest? body, Failure? malformed) = await ReadBodyAsync<ReasonRequest>();
			if (malformed != null)
			{
				return ApiResults.Fail(malformed.Value);
			}
			return ApiResults.NoContent(await MessageActions.DeleteAsync(m_dbFactory, Me, messageID, body?.Reason, RequirePermissionAsync));
		}

		// The staff queue, moderation.kick or higher; status is open (default), actioned, dismissed or all
		[HttpGet("~/api/v1/reports")]
		public async Task<IActionResult> GetReports([FromQuery] string? status)
		{
			if (await RequireStaffAsync() is Failure denied)
			{
				return ApiResults.Fail(denied);
			}

			string? filter = status ?? PlayerReportStatuses.Open;
			if (filter == "all")
			{
				filter = null;
			}
			else if (filter != PlayerReportStatuses.Open && filter != PlayerReportStatuses.Actioned && filter != PlayerReportStatuses.Dismissed)
			{
				return ApiResults.Fail(Result.Invalid("status"));
			}
			return ApiResults.Ok(await MessageActions.ListReportsAsync(m_dbFactory, filter));
		}

		[HttpPatch("~/api/v1/reports/{reportID:long}")]
		public async Task<IActionResult> ResolveReport(long reportID)
		{
			if (await RequireStaffAsync() is Failure denied)
			{
				return ApiResults.Fail(denied);
			}

			(ResolveReportRequest? body, Failure? malformed) = await ReadBodyAsync<ResolveReportRequest>();
			if (malformed != null)
			{
				return ApiResults.Fail(malformed.Value);
			}
			return ApiResults.NoContent(await MessageActions.ResolveReportAsync(m_dbFactory, Me, reportID, body ?? new ResolveReportRequest()));
		}

		// moderation.kick or higher
		private async Task<Failure?> RequireStaffAsync()
		{
			if (await RequirePermissionAsync(Permissions.ModerationKick) != null
				&& await RequirePermissionAsync(Permissions.ModerationMute) != null
				&& await RequirePermissionAsync(Permissions.ModerationBan) != null)
			{
				return Result.Forbidden(Permissions.ModerationKick);
			}
			return null;
		}

		// A report of a message or of a player, open to every player
		[HttpPost("~/api/v1/reports")]
		public async Task<IActionResult> CreateReport()
		{
			(CreateReportRequest? body, Failure? malformed) = await ReadBodyAsync<CreateReportRequest>();
			if (malformed != null)
			{
				return ApiResults.Fail(malformed.Value);
			}
			return ApiResults.Answer(await MessageActions.ReportAsync(m_dbFactory, Me, body ?? new CreateReportRequest()));
		}
	}
}
