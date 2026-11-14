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

namespace GenOnlineService.Rest
{
	// GET /api/v1/lobbies
	[ApiFixture("get-lobbies.response")]
	public sealed record LobbyListResponse
	{
		public IReadOnlyList<LobbySummary> Lobbies { get; init; } = [];
	}

	// A read only lobby list; clients that need changes use the hub's WatchLobbyList
	[ApiController]
	[Authorize(Policy = AuthPolicies.PlayerSessionOrLobbiesRead)]
	public sealed class LobbiesController : ControllerBase
	{
		private readonly LobbyManager m_lobbyManager;

		public LobbiesController(LobbyManager lobbyManager)
		{
			m_lobbyManager = lobbyManager;
		}

		// A player connected to the hub sees the list WatchLobbyList gives them. Any other caller (a lobbies.read token,
		// or a player without a hub connection) sees every public custom lobby in setup or countdown, of every build, with latency_ms 0.
		[HttpGet("~/api/v1/lobbies")]
		public IActionResult List()
		{
			UserSession? viewer = ViewerOf(User);
			List<LobbySummary> lobbies = new();
			foreach (Lobby lobby in m_lobbyManager.GetLobbies())
			{
				if (viewer != null)
				{
					if (RealtimeLobbies.IsListedFor(lobby, viewer))
					{
						lobbies.Add(RealtimeLobbies.SummaryOf(lobby, viewer));
					}
				}
				else if (IsPublicListed(lobby))
				{
					lobbies.Add(SummaryWithoutViewer(lobby));
				}
			}
			return RestResults.Ok(new LobbyListResponse { Lobbies = lobbies.OrderBy(lobby => lobby.Id).ToArray() });
		}

		private static UserSession? ViewerOf(System.Security.Claims.ClaimsPrincipal user)
		{
			EUserSessionType sessionType = RealtimeClaims.SessionType(user);
			long userID = RealtimeClaims.UserID(user);
			return sessionType == EUserSessionType.None || userID == -1 ? null : WebSocketManager.GetSessionFromUser(userID, sessionType);
		}

		private static bool IsPublicListed(Lobby lobby)
		{
			return lobby.LobbyType == ELobbyType.CustomGame
				&& (lobby.State == ELobbyState.GAME_SETUP || lobby.State == ELobbyState.COUNTDOWN)
				&& lobby.GetNumberOfHumans() > 0
				&& lobby.LobbyJoinability != ELobbyJoinability.FriendsOnly;
		}

		// RealtimeLobbies.SummaryOf needs a viewer session for latency_ms; without one the estimate is 0
		private static LobbySummary SummaryWithoutViewer(Lobby lobby)
		{
			return new LobbySummary
			{
				Id = lobby.LobbyID,
				Name = lobby.Name,
				Host = RealtimeMembers.Of(lobby.Owner),
				State = RealtimeLobbies.StageOf(lobby.State),
				Map = new LobbySummaryMap { Name = lobby.MapName, Official = lobby.IsMapOfficial },
				Players = lobby.NumCurrentPlayers,
				MaxPlayers = lobby.MaxPlayers,
				HasPassword = lobby.IsPassworded,
				Region = lobby.Region,
				LatencyMs = 0,
				Anticheat = RealtimeLobbies.AnticheatOf(lobby.AnticheatID),
				Settings = new LobbySummarySettings
				{
					StartingCash = (int)lobby.StartingCash,
					LimitSuperweapons = lobby.IsLimitSuperweapons,
					VanillaTeams = lobby.IsVanillaTeamsOnly,
					TrackStats = lobby.IsTrackingStats,
					AllowObservers = lobby.AllowObservers
				},
				Build = new BuildIdentity { ExeCrc = lobby.ExeCRC, IniCrc = lobby.IniCRC },
				CreatedAt = lobby.TimeCreated
			};
		}
	}
}
