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
using GenOnlineService.OpenApi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;

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

		// Replaces an offensive name, the tag follows the name
		[AllowSignInCookie]
		[EndpointSummary("Renames a player as staff.")]
		[ProducesResponseType(StatusCodes.Status204NoContent)]
		[ApiRequestBody(typeof(RenamePlayerRequest))]
		[ApiPermission(Permissions.ModerationRename)]
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

		// Flags an inappropriate name: a placeholder replaces it, the player chooses a new one at their next sign-in, which
		// this forces by ending every sign-in (S17)
		[AllowSignInCookie]
		[EndpointSummary("Resets a player name to a generated one as staff.")]
		[ProducesResponseType(StatusCodes.Status204NoContent)]
		[ApiRequestBody(typeof(ResetNameRequest))]
		[ApiPermission(Permissions.ModerationRename)]
		[HttpPost("~/api/v1/players/{userID:long}/reset_name")]
		public async Task<IActionResult> ResetPlayerName(long userID)
		{
			if (await RequirePermissionAsync(Permissions.ModerationRename) is Failure denied)
			{
				return ApiResults.Fail(denied);
			}

			(ResetNameRequest? body, Failure? malformed) = await ReadBodyAsync<ResetNameRequest>();
			if (malformed != null)
			{
				return ApiResults.Fail(malformed.Value);
			}
			return ApiResults.NoContent(await m_names.ResetNameByStaffAsync(SanctionActions.ApiActorOf(Me), userID, body?.Reason));
		}

		[AllowSignInCookie]
		[EndpointSummary("Kicks, mutes or bans a player.")]
		[ProducesResponseType<CreateSanctionResponse>(StatusCodes.Status200OK)]
		[ApiRequestBody(typeof(CreateSanctionRequest))]
		[ApiPermission(Permissions.ModerationKick, Permissions.ModerationMute, Permissions.ModerationBan)]
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
		[EndpointSummary("Lists the sanctions of a player.")]
		[ProducesResponseType<SanctionsResponse>(StatusCodes.Status200OK)]
		[ApiPermission(Permissions.ModerationKick, Permissions.ModerationMute, Permissions.ModerationBan)]
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

		[AllowSignInCookie]
		[EndpointSummary("Lifts a mute or a ban.")]
		[ProducesResponseType(StatusCodes.Status204NoContent)]
		[ApiRequestBody(typeof(ReasonRequest))]
		[ApiPermission(Permissions.ModerationMute, Permissions.ModerationBan)]
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

		// The report queue of the staff pages deletes a reported message with the sign-in cookie
		[AllowSignInCookie]
		[EndpointSummary("Deletes a chat message for everyone.")]
		[ProducesResponseType(StatusCodes.Status204NoContent)]
		[ApiRequestBody(typeof(ReasonRequest))]
		[ApiPermission(Permissions.ChatDeleteAny)]
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
		[EndpointSummary("Lists player reports by status.")]
		[ProducesResponseType<ReportsResponse>(StatusCodes.Status200OK)]
		[ApiPermission(Permissions.ModerationKick, Permissions.ModerationMute, Permissions.ModerationBan)]
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

		// The staff queue of the staff pages: several statuses (comma separated) or all, a search, oldest (asc, default) or
		// newest (desc) first, the reports against (reported_id) or by (reporter_id) one player, filtered and offset paged in the
		// database; moderation.kick or higher
		[AllowSignInCookie]
		[EndpointSummary("Searches the player report queue with paging.")]
		[ProducesResponseType<ReportsResponse>(StatusCodes.Status200OK)]
		[ApiPermission(Permissions.ModerationKick, Permissions.ModerationMute, Permissions.ModerationBan)]
		[HttpGet("~/api/v1/reports/search")]
		public async Task<IActionResult> SearchReports([FromQuery] string? status, [FromQuery] string? search, [FromQuery] string? sort,
			[FromQuery(Name = "reported_id")] string? reportedID, [FromQuery(Name = "reporter_id")] string? reporterID)
		{
			if (await RequireStaffAsync() is Failure denied)
			{
				return ApiResults.Fail(denied);
			}

			string[] statuses = (status ?? PlayerReportStatuses.Open).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (statuses.Length == 0 || statuses.Any(s => s != "all" && s != PlayerReportStatuses.Open && s != PlayerReportStatuses.Actioned && s != PlayerReportStatuses.Dismissed))
			{
				return ApiResults.Fail(Result.Invalid("status"));
			}
			if (sort != null && sort != "asc" && sort != "desc")
			{
				return ApiResults.Fail(Result.Invalid("sort"));
			}
			if (!TryReadPlayerID(reportedID, out long? reported))
			{
				return ApiResults.Fail(Result.Invalid("reported_id"));
			}
			if (!TryReadPlayerID(reporterID, out long? reporter))
			{
				return ApiResults.Fail(Result.Invalid("reporter_id"));
			}
			(Rest.OffsetPage page, Failure? pageFailure) = Rest.Paging.ReadOffset(Request);
			if (pageFailure != null)
			{
				return ApiResults.Fail(pageFailure.Value);
			}

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(HttpContext.RequestAborted);
			(int total, List<PlayerReport> rows) = await Database.Chat.GetReportsPage(db, statuses.Contains("all") ? null : statuses, search, sort == "desc", page.Skip, page.PerPage, HttpContext.RequestAborted, reported, reporter);
			Dictionary<long, (string Name, int Tag)> names = await Database.DisplayNames.GetBulk(db, rows.SelectMany(Rest.StaffOverview.ReportUserIDs).ToHashSet());

			// the page leaves the evidence out, so it is read for the message reports of this page only
			long[] messageReports = rows.Where(r => r.MessageID != null).Select(r => r.ID).ToArray();
			Dictionary<long, string> evidence = messageReports.Length == 0
				? new Dictionary<long, string>()
				: await db.PlayerReports.AsNoTracking().Where(r => messageReports.Contains(r.ID)).Select(r => new { r.ID, r.Evidence }).ToDictionaryAsync(r => r.ID, r => r.Evidence, HttpContext.RequestAborted);

			// the fields of GET /api/v1/reports, and the reported message's text so staff see what a message report is about
			Rest.Paging.WriteOffsetHeaders(HttpContext, page, total);
			return ApiResults.Ok(new ReportsResponse
			{
				Reports = rows.Select(r => Rest.StaffOverview.ReportOf(r, names) with { MessageContent = evidence.TryGetValue(r.ID, out string? stored) ? ReportedContentOf(stored) : null }).ToArray()
			});
		}

		// The message of a message report's evidence ({message, before, after}); null for evidence that does not parse
		private static string? ReportedContentOf(string stored)
		{
			try
			{
				return JsonNode.Parse(stored) is JsonObject evidence && evidence["message"] is JsonObject message
					&& message["content"] is JsonValue content && content.TryGetValue(out string? text) ? text : null;
			}
			catch (JsonException)
			{
				return null;
			}
		}

		[AllowSignInCookie]
		[EndpointSummary("Resolves or reopens a player report.")]
		[ProducesResponseType(StatusCodes.Status204NoContent)]
		[ApiRequestBody(typeof(ResolveReportRequest))]
		[ApiPermission(Permissions.ModerationKick, Permissions.ModerationMute, Permissions.ModerationBan)]
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

		// An absent argument, or a positive player id
		private static bool TryReadPlayerID(string? text, out long? id)
		{
			id = null;
			if (text == null)
			{
				return true;
			}
			if (long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long parsed) && parsed > 0)
			{
				id = parsed;
				return true;
			}
			return false;
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
		[EndpointSummary("Reports a player or a chat message.")]
		[ProducesResponseType<CreateReportResponse>(StatusCodes.Status200OK)]
		[ApiRequestBody(typeof(CreateReportRequest))]
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
