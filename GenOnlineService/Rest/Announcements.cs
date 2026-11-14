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
	[ApiFixture("post-announcements.request")]
	public sealed record AnnouncementRequest
	{
		public string? Message { get; init; }
	}

	[ApiFixture("post-announcements.response")]
	public sealed record AnnouncementResponse
	{
		public int DeliveredTo { get; init; }
	}

	public static class Announcements
	{
		public const int MaximumLength = 512;

		// The trimmed text, 1 to 512 characters
		public static (string? Message, Failure? Failure) Validate(string? message)
		{
			string text = message?.Trim() ?? String.Empty;
			return text.Length == 0 || text.Length > MaximumLength ? (null, Result.Invalid("message")) : (text, null);
		}

		// Staff text travels as a notice parameter to every connection, clients render it as an announcement; answers the users online
		public static int Deliver(string message)
		{
			int delivered = WebSocketManager.GetNumberOfUsersOnline();
			RealtimeOut.Fire(RealtimeOut.All.Notice(new NoticeEvent
			{
				Code = ErrorCodes.ServerAnnouncement,
				Params = new { Message = message }
			}));
			return delivered;
		}
	}

	[ApiController]
	[Authorize(Policy = PermissionPolicies.Prefix + Permissions.AnnouncementsSend)]
	public sealed class AnnouncementsController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public AnnouncementsController(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		[HttpPost("~/api/v1/announcements")]
		public async Task<IActionResult> Post([FromBody] AnnouncementRequest? body)
		{
			(string? message, Failure? failure) = Announcements.Validate(body?.Message);
			if (failure != null)
			{
				return RestResults.Fail(failure.Value);
			}

			// the audit row is written first and a failed write stops the broadcast
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				await Database.Moderation.AddAuditEntry(db, CallerActors.Of(User), ModerationActions.Announcement, AuditTargetKinds.Announcement, null, null, new { message });
			}

			return RestResults.Ok(new AnnouncementResponse { DeliveredTo = Announcements.Deliver(message!) });
		}
	}
}
