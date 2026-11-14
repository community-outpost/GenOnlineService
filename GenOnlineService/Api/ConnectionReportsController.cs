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
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService.Api
{
	using GenOnlineService.Realtime;

	// Checks a connection report body and turns it into the stored row, the first broken field is named
	public static class ConnectionReportValidation
	{
		// The caller's lobby membership is checked before the body, isMember is a current member and wasMember a current or former one
		public static Result<ConnectionReport> Check(bool lobbyFound, Func<long, bool> isMember, Func<long, bool> wasMember, ConnectionReportRequest? body, Failure? malformed, long reporterId, long lobbyId)
		{
			if (!lobbyFound)
			{
				return Result.NotFound("lobby");
			}

			if (!isMember(reporterId))
			{
				return Result.Fail(ErrorCodes.LobbyNotMember);
			}

			if (malformed != null)
			{
				return malformed.Value;
			}

			return Validate(body ?? new ConnectionReportRequest(), reporterId, lobbyId, wasMember);
		}

		// wasLobbyMember tells whether a player is or was a member of the lobby
		public static Result<ConnectionReport> Validate(ConnectionReportRequest body, long reporterId, long lobbyId, Func<long, bool> wasLobbyMember)
		{
			if (body.TargetUserId is not long target || target <= 0 || target == reporterId || !wasLobbyMember(target))
			{
				return Result.Invalid("target_user_id");
			}

			if (body.Outcome is not string outcome || !ConnectionReportOutcomes.All.Contains(outcome))
			{
				return Result.Invalid("outcome");
			}

			if (body.Transport is not string transport || !ConnectionReportTransports.All.Contains(transport))
			{
				return Result.Invalid("transport");
			}

			if (body.IpVersion is not (4 or 6))
			{
				return Result.Invalid("ip_version");
			}

			int attempt = body.Attempt ?? 1;
			if (attempt < 1)
			{
				return Result.Invalid("attempt");
			}

			IReadOnlyList<ConnectionReportEvent> events = body.Events ?? [];
			if (events.Count > Database.ConnectionReports.MaximumEvents)
			{
				return Result.Invalid("events");
			}

			List<Database.ConnectionReports.Event> timeline = new List<Database.ConnectionReports.Event>(events.Count);
			long previous = 0;
			foreach (ConnectionReportEvent e in events)
			{
				if (e == null || e.State is not string state || !ConnectionReportStates.All.Contains(state)
					|| e.TMs is not long ms || ms < previous || ms > Database.ConnectionReports.MaximumEventMilliseconds)
				{
					return Result.Invalid("events");
				}
				previous = ms;
				timeline.Add(new Database.ConnectionReports.Event(state, ms));
			}

			return Result.Success(new ConnectionReport
			{
				ReporterID = reporterId,
				LobbyID = lobbyId,
				TargetUserID = target,
				Attempt = attempt,
				Outcome = outcome,
				Transport = transport,
				IpVersion = body.IpVersion.Value,
				Events = Database.ConnectionReports.SerializeEvents(timeline)
			});
		}
	}

	// Connection reports of lobby members (section 10.3), replaces the legacy ConnectionOutcome route
	public sealed class ConnectionReportsController : ApiControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;
		private readonly LobbyManager m_lobbyManager;

		public ConnectionReportsController(IDbContextFactory<AppDbContext> dbFactory, LobbyManager lobbyManager, IAuthorizationService authorization) : base(authorization)
		{
			m_dbFactory = dbFactory;
			m_lobbyManager = lobbyManager;
		}

		// 32 events fit in about 1.5 KB, larger bodies are refused before parsing
		[HttpPost("~/api/v1/lobbies/{lobbyID:long}/connection_reports")]
		[RequestSizeLimit(8192)]
		public async Task<IActionResult> Create(long lobbyID)
		{
			(ConnectionReportRequest? body, Failure? malformed) = await ReadBodyAsync<ConnectionReportRequest>();
			Lobby? lobby = m_lobbyManager.GetLobby(lobbyID);
			Result<ConnectionReport> report = ConnectionReportValidation.Check(lobby != null, userID => lobby?.GetMemberFromUserID(userID) != null, userID => lobby?.WasEverMember(userID) == true, body, malformed, Me, lobbyID);
			if (!report.Ok)
			{
				return ApiResults.Fail(new Failure(report.Code!, report.Params!));
			}

			ConnectionReport row = report.Data!;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				await Database.ConnectionReports.Upsert(db, row);
			}

			AppMetrics.RecordConnectionOutcome(row.Outcome, row.Transport, row.IpVersion);
			return ApiResults.NoContent(Result.Success());
		}
	}
}
