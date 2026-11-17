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
using System.Globalization;
using System.Text.Json;

namespace GenOnlineService.Rest
{
	public sealed record AuditActor
	{
		public long Id { get; init; }
		public string? DisplayName { get; init; }
	}

	public sealed record AuditEvent
	{
		public long Id { get; init; }
		public string Action { get; init; } = "";
		public string ActorKind { get; init; } = "";

		// The stored label, such as "user:42", "cli" or "discord:123"
		public string ActorName { get; init; } = "";

		// The user behind the action, null for the CLI, service accounts and upstream providers
		public AuditActor? Actor { get; init; }

		public string TargetKind { get; init; } = "";
		public long? TargetId { get; init; }
		public string? Reason { get; init; }
		public string Source { get; init; } = "";
		public JsonElement? Details { get; init; }
		public DateTime CreatedAt { get; init; }
	}

	[ApiFixture("get-audit_events.response")]
	public sealed record AuditEventsResponse
	{
		public IReadOnlyList<AuditEvent> AuditEvents { get; init; } = [];
	}

	// ?actor_id=&target_id=&target_kind=&action=&after_id=&per_page=; after_id continues below that id
	public sealed record AuditEventsQuery(long? ActorID, long? TargetID, string? Action, long? AfterID, int PerPage, string? TargetKind = null)
	{
		public const int MaximumActionLength = 64;
		public const int MaximumTargetKindLength = 32;

		public static (AuditEventsQuery? Query, Failure? Failure) Read(HttpRequest request)
		{
			(long? actorID, Failure? actorFailure) = ReadID(request, "actor_id");
			(long? targetID, Failure? targetFailure) = ReadID(request, "target_id");
			(long? afterID, Failure? afterFailure) = ReadID(request, "after_id");
			Failure? failure = actorFailure ?? targetFailure ?? afterFailure;
			if (failure != null)
			{
				return (null, failure);
			}

			(string? action, Failure? actionFailure) = ReadText(request, "action", MaximumActionLength);
			(string? targetKind, Failure? kindFailure) = ReadText(request, "target_kind", MaximumTargetKindLength);
			failure = actionFailure ?? kindFailure;
			if (failure != null)
			{
				return (null, failure);
			}

			int perPage = OffsetPage.DefaultPerPage;
			if (request.Query.TryGetValue("per_page", out var perPageText) && (!int.TryParse(perPageText, NumberStyles.None, CultureInfo.InvariantCulture, out perPage) || perPage < 1 || perPage > OffsetPage.MaxPerPage))
			{
				return (null, Result.Invalid("per_page"));
			}

			return (new AuditEventsQuery(actorID, targetID, action, afterID, perPage, targetKind), null);
		}

		// Absent is no filter; present must be 1 to maximumLength characters
		private static (string? Value, Failure? Failure) ReadText(HttpRequest request, string name, int maximumLength)
		{
			if (!request.Query.TryGetValue(name, out var text))
			{
				return (null, null);
			}
			string value = text.ToString();
			return value.Length == 0 || value.Length > maximumLength ? (null, Result.Invalid(name)) : (value, null);
		}

		private static (long? Value, Failure? Failure) ReadID(HttpRequest request, string name)
		{
			if (!request.Query.TryGetValue(name, out var text))
			{
				return (null, null);
			}
			return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value > 0 ? (value, null) : (null, Result.Invalid(name));
		}
	}

	[ApiController]
	[Authorize(Policy = PermissionPolicies.Prefix + Permissions.AuditRead)]
	public sealed class AuditEventsController : ControllerBase
	{
		private static readonly ILogger s_log = AppLog.For(typeof(AuditEventsController));

		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public AuditEventsController(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		// The audit log page of the staff area reads this with the sign-in cookie
		[AllowSignInCookie]
		[HttpGet("~/api/v1/audit_events")]
		public async Task<IActionResult> List()
		{
			(AuditEventsQuery? query, Failure? failure) = AuditEventsQuery.Read(Request);
			if (failure != null)
			{
				return RestResults.Fail(failure.Value);
			}

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			List<AuditLogEntry> rows = await Database.Moderation.GetAuditEvents(db, query!.ActorID, query.TargetID, query.Action, query.AfterID, query.PerPage, query.TargetKind);

			List<long> userIDs = rows.Where(IsUserActor).Select(r => r.ActorID!.Value).Distinct().ToList();
			Dictionary<long, string> names = await db.Users.AsNoTracking()
				.Where(u => userIDs.Contains(u.ID))
				.Select(u => new { u.ID, u.Username })
				.ToDictionaryAsync(u => u.ID, u => u.Username);

			List<AuditEvent> events = rows.Select(r => new AuditEvent
			{
				Id = r.ID,
				Action = r.Action,
				ActorKind = r.ActorKind,
				ActorName = r.Actor,
				Actor = IsUserActor(r) ? new AuditActor { Id = r.ActorID!.Value, DisplayName = names.GetValueOrDefault(r.ActorID!.Value) } : null,
				TargetKind = r.TargetKind,
				TargetId = r.TargetID,
				Reason = r.Reason,
				Source = r.Source,
				Details = ParseDetails(r),
				CreatedAt = DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)
			}).ToList();

			Paging.WriteKeysetNext(HttpContext, "after_id", rows.Count == query.PerPage ? rows[^1].ID : null);
			return RestResults.Ok(new AuditEventsResponse { AuditEvents = events });
		}

		// The CLI and Discord store the account they acted for, a service account stores its own id
		private static bool IsUserActor(AuditLogEntry row) => row.ActorID != null && row.ActorKind != AuditActorKinds.ServiceAccount && row.ActorKind != AuditActorKinds.Upstream;

		// Rows hold ids, names and settings, never chat text or credentials, so details pass through as stored
		private static JsonElement? ParseDetails(AuditLogEntry row)
		{
			if (row.Details == null)
			{
				return null;
			}

			try
			{
				using JsonDocument document = JsonDocument.Parse(row.Details);
				return document.RootElement.Clone();
			}
			catch (JsonException ex)
			{
				s_log.LogWarning(ex, "Audit row {AuditId} has details that are not JSON", row.ID);
				return null;
			}
		}
	}
}
