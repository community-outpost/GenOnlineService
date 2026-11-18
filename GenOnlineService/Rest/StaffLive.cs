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
using GenOnlineService.Failover;
using GenOnlineService.OpenApi;
using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService.Rest
{
	// The failover lease as "realtime status" prints it: holder and standby are null for none, expires_in_ms is null once expired
	public sealed record StaffRealtimeLease
	{
		public string? Holder { get; init; }
		public long Epoch { get; init; }
		public long? ExpiresInMs { get; init; }
		public string? Standby { get; init; }
		public bool HandoverRequested { get; init; }

		// now is the database time; a standby counts while its own lease runs
		public static StaffRealtimeLease Of(RealtimeLease lease, DateTime now) => new StaffRealtimeLease
		{
			Holder = lease.Holder.Length == 0 ? null : lease.Holder,
			Epoch = lease.Epoch,
			ExpiresInMs = lease.ExpiresAt > now ? (long)Math.Round((lease.ExpiresAt - now).TotalMilliseconds, MidpointRounding.AwayFromZero) : null,
			Standby = lease.Standby != null && lease.StandbyExpiresAt > now ? lease.Standby : null,
			HandoverRequested = lease.HandoverRequestedAt != null
		};
	}

	// The service copy that answers: its id as the lease names it, whether it serves players, whether failover runs
	public sealed record StaffRealtimeCopy
	{
		public string Id { get; init; } = "";
		public bool Serving { get; init; }
		public bool Failover { get; init; }
	}

	// The players waiting in one quick match playlist on this copy
	public sealed record StaffRealtimeQueue
	{
		public int PlaylistId { get; init; }
		public string Name { get; init; } = "";
		public int Queued { get; init; }
	}

	// GET /api/v1/staff/realtime: the lease is null where the CLI refuses it (no PostgreSQL), the counts are this copy's
	public sealed record StaffRealtimeResponse
	{
		public StaffRealtimeCopy Copy { get; init; } = new StaffRealtimeCopy();
		public StaffRealtimeLease? Lease { get; init; }
		public int PlayersOnline { get; init; }
		public int LobbiesOpen { get; init; }
		public IReadOnlyList<StaffRealtimeQueue> Queues { get; init; } = [];
	}

	// GET /api/v1/staff/lobbies: every lobby of this copy in the fields of the hub's LobbyState, never the password
	public sealed record StaffLobbyListResponse
	{
		public IReadOnlyList<LobbyInfo> Lobbies { get; init; } = [];
	}

	public sealed record StaffConnectionEvent
	{
		public string State { get; init; } = "";
		public long TMs { get; init; }
	}

	// One connection report as "connections player|lobby" prints it, never with an address
	public sealed record StaffConnectionReport
	{
		public long Id { get; init; }
		public long LobbyId { get; init; }
		public Member Reporter { get; init; } = new Member();
		public Member Target { get; init; } = new Member();
		public int Attempt { get; init; }
		public string Outcome { get; init; } = "";
		public string Transport { get; init; } = "";
		public int IpVersion { get; init; }
		public IReadOnlyList<StaffConnectionEvent> Events { get; init; } = [];
		public DateTime CreatedAt { get; init; }
		public DateTime UpdatedAt { get; init; }
	}

	// The reports of a player newest first, or of a lobby pair by pair, as the CLI orders them
	public sealed record StaffConnectionReportsResponse
	{
		public IReadOnlyList<StaffConnectionReport> Reports { get; init; } = [];
	}

	public static class StaffLive
	{
		// One audit row per staff member and target inside this window, so the auto refresh of the pages adds none
		public static readonly TimeSpan LookupAuditWindow = TimeSpan.FromHours(1);

		public static async Task<StaffRealtimeResponse> StatusAsync(IDbContextFactory<AppDbContext> dbFactory, int playersOnline, int lobbiesOpen, CancellationToken cancellationToken = default)
		{
			StaffRealtimeLease? lease = null;
			await using (AppDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken))
			{
				// more than one service copy runs on PostgreSQL only, as realtime status says
				if (db.Database.IsNpgsql() && await Database.Lease.Read(db, cancellationToken) is RealtimeLease row)
				{
					lease = StaffRealtimeLease.Of(row, await Database.Lease.DatabaseNow(db, cancellationToken));
				}
			}

			return new StaffRealtimeResponse
			{
				Copy = new StaffRealtimeCopy { Id = CopyIdentity.ID, Serving = RealtimeFailover.Serving, Failover = RealtimeFailover.Enabled },
				Lease = lease,
				PlayersOnline = playersOnline,
				LobbiesOpen = lobbiesOpen,
				Queues = MatchmakingManager.GetPlaylists().OrderBy(p => p.Key).Select(p => new StaffRealtimeQueue
				{
					PlaylistId = p.Key,
					Name = p.Value.Name,
					Queued = MatchmakingManager.GetTotalQueuedPlayersInPlaylist(p.Key)
				}).ToList()
			};
		}

		public static StaffLobbyListResponse Lobbies(LobbyManager? lobbies) => new StaffLobbyListResponse
		{
			Lobbies = (lobbies?.GetLobbies() ?? []).Select(RealtimeLobbies.StateOf).OrderBy(lobby => lobby.Id).ToList()
		};

		// The audit row is written first and a failed write shows nothing, as connections player does
		public static async Task<Result<StaffConnectionReportsResponse>> OfPlayerAsync(IDbContextFactory<AppDbContext> dbFactory, Database.ModerationActor actor, long userID, CancellationToken cancellationToken = default)
		{
			await using AppDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
			if (!await Database.Users.Exists(db, userID))
			{
				return Result.NotFound("user");
			}
			await Database.ConnectionReports.LogLookup(db, actor, AuditTargetKinds.User, userID, "player", DateTime.UtcNow, LookupAuditWindow);
			List<ConnectionReport> rows = await Database.ConnectionReports.GetByPlayer(db, userID, cancellationToken: cancellationToken);
			return Result.Success(await ResponseOfAsync(db, rows));
		}

		public static async Task<Result<StaffConnectionReportsResponse>> OfLobbyAsync(IDbContextFactory<AppDbContext> dbFactory, Database.ModerationActor actor, long lobbyID, CancellationToken cancellationToken = default)
		{
			// lobby ids count from 1
			if (lobbyID < 1)
			{
				return Result.Invalid("lobby_id");
			}
			await using AppDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
			await Database.ConnectionReports.LogLookup(db, actor, AuditTargetKinds.Lobby, lobbyID, "lobby", DateTime.UtcNow, LookupAuditWindow);
			List<ConnectionReport> rows = await Database.ConnectionReports.GetByLobby(db, lobbyID, cancellationToken: cancellationToken);
			return Result.Success(await ResponseOfAsync(db, Database.ConnectionReports.InPairs(rows).SelectMany(pair => pair.Reports)));
		}

		private static async Task<StaffConnectionReportsResponse> ResponseOfAsync(AppDbContext db, IEnumerable<ConnectionReport> reports)
		{
			List<ConnectionReport> rows = reports.ToList();
			Dictionary<long, (string Name, int Tag)> names = await Database.DisplayNames.GetBulk(db, rows.SelectMany(r => new[] { r.ReporterID, r.TargetUserID }).ToHashSet());
			return new StaffConnectionReportsResponse
			{
				Reports = rows.Select(r => new StaffConnectionReport
				{
					Id = r.ID,
					LobbyId = r.LobbyID,
					Reporter = RealtimeMembers.Of(r.ReporterID, names),
					Target = RealtimeMembers.Of(r.TargetUserID, names),
					Attempt = r.Attempt,
					Outcome = r.Outcome,
					Transport = r.Transport,
					IpVersion = r.IpVersion,
					Events = Database.ConnectionReports.ParseEvents(r.Events).Select(e => new StaffConnectionEvent { State = e.State, TMs = e.TMs }).ToList(),
					CreatedAt = r.CreatedAt,
					UpdatedAt = r.UpdatedAt
				}).ToList()
			};
		}
	}

	// The live operations of the staff pages, read only: the realtime status needs server.stats, the lobbies and the
	// connection reports any staff permission. Connection reports name who played whom but never an address, so they are
	// staff reads, audited as connection_lookup like the connections CLI. Handover stays a CLI operation.
	[ApiController]
	[Authorize]
	public sealed class StaffLiveController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;
		private readonly IAuthorizationService m_authorization;

		public StaffLiveController(IDbContextFactory<AppDbContext> dbFactory, IAuthorizationService authorization)
		{
			m_dbFactory = dbFactory;
			m_authorization = authorization;
		}

		// a host without the lobby side (the in-process test pipeline) has no live lobbies
		private LobbyManager? LiveLobbies => HttpContext.RequestServices.GetService<LobbyManager>();

		[AllowSignInCookie]
		[Authorize(Policy = PermissionPolicies.Prefix + Permissions.ServerStats)]
		[EndpointSummary("Reads the realtime status: the failover lease, this copy, its players, lobbies and queues.")]
		[ProducesResponseType<StaffRealtimeResponse>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/staff/realtime")]
		public async Task<IActionResult> Realtime()
		{
			return RestResults.Ok(await StaffLive.StatusAsync(m_dbFactory, WebSocketManager.GetNumberOfUsersOnline(), LiveLobbies?.GetNumLobbies() ?? 0, HttpContext.RequestAborted));
		}

		[AllowSignInCookie]
		[EndpointSummary("Lists every live lobby with its members for the staff pages.")]
		[ProducesResponseType<StaffLobbyListResponse>(StatusCodes.Status200OK)]
		[ApiPermission(Permissions.ModerationKick, Permissions.ModerationMute, Permissions.ModerationBan)]
		[HttpGet("~/api/v1/staff/lobbies")]
		public async Task<IActionResult> Lobbies()
		{
			if (!await StaffAccess.IsStaffAsync(m_authorization, User))
			{
				return RestResults.Fail(Result.Forbidden(Permissions.ModerationKick));
			}
			return RestResults.Ok(StaffLive.Lobbies(LiveLobbies));
		}

		[AllowSignInCookie]
		[EndpointSummary("Reads the connection reports a player sent or that name the player.")]
		[ProducesResponseType<StaffConnectionReportsResponse>(StatusCodes.Status200OK)]
		[ApiPermission(Permissions.ModerationKick, Permissions.ModerationMute, Permissions.ModerationBan)]
		[HttpGet("~/api/v1/players/{userID:long}/connection_reports")]
		public async Task<IActionResult> OfPlayer(long userID)
		{
			if (!await StaffAccess.IsStaffAsync(m_authorization, User))
			{
				return RestResults.Fail(Result.Forbidden(Permissions.ModerationKick));
			}
			return ApiResults.Answer(await StaffLive.OfPlayerAsync(m_dbFactory, CallerActors.Of(User), userID, HttpContext.RequestAborted));
		}

		[AllowSignInCookie]
		[EndpointSummary("Reads the connection reports of a lobby, pair by pair.")]
		[ProducesResponseType<StaffConnectionReportsResponse>(StatusCodes.Status200OK)]
		[ApiPermission(Permissions.ModerationKick, Permissions.ModerationMute, Permissions.ModerationBan)]
		[HttpGet("~/api/v1/lobbies/{lobbyID:long}/connection_reports")]
		public async Task<IActionResult> OfLobby(long lobbyID)
		{
			if (!await StaffAccess.IsStaffAsync(m_authorization, User))
			{
				return RestResults.Fail(Result.Forbidden(Permissions.ModerationKick));
			}
			return ApiResults.Answer(await StaffLive.OfLobbyAsync(m_dbFactory, CallerActors.Of(User), lobbyID, HttpContext.RequestAborted));
		}
	}
}
