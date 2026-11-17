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

		// Validates, writes the audit row and broadcasts; a failed audit write throws and stops the broadcast. Answers the users online
		public static async Task<(int DeliveredTo, Failure? Failure)> SendAsync(AppDbContext db, Database.ModerationActor actor, string? message)
		{
			(string? text, Failure? failure) = Validate(message);
			if (failure != null)
			{
				return (0, failure);
			}

			await Database.Moderation.AddAuditEntry(db, actor, ModerationActions.Announcement, AuditTargetKinds.Announcement, null, null, new { message = text });
			return (Deliver(text!), null);
		}

		// Staff text travels as a notice parameter to every connection, clients render it as an announcement; answers the users online
		private static int Deliver(string message)
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

		// The announcements page of the staff pages takes the sign-in cookie too
		[AllowSignInCookie]
		[HttpPost("~/api/v1/announcements")]
		public async Task<IActionResult> Post([FromBody] AnnouncementRequest? body)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			(int deliveredTo, Failure? failure) = await Announcements.SendAsync(db, CallerActors.Of(User), body?.Message);
			return failure != null
				? RestResults.Fail(failure.Value)
				: RestResults.Ok(new AnnouncementResponse { DeliveredTo = deliveredTo });
		}
	}
}
