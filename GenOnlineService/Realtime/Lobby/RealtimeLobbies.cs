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

namespace GenOnlineService
{
	public enum ELobbyJoinability
	{
		Public,
		FriendsOnly
	}
}

namespace GenOnlineService.Realtime
{
	// Maps the in-memory lobby model to the wire objects of section 4
	internal static class RealtimeLobbies
	{
		public static SlotState SlotStateOf(EPlayerType type) => type switch
		{
			EPlayerType.SLOT_OPEN => SlotState.Open,
			EPlayerType.SLOT_CLOSED => SlotState.Closed,
			EPlayerType.SLOT_EASY_AI => SlotState.AiEasy,
			EPlayerType.SLOT_MED_AI => SlotState.AiMedium,
			EPlayerType.SLOT_BRUTAL_AI => SlotState.AiBrutal,
			_ => SlotState.Player,
		};

		public static EPlayerType PlayerTypeOf(SlotState state) => state switch
		{
			SlotState.Open => EPlayerType.SLOT_OPEN,
			SlotState.Closed => EPlayerType.SLOT_CLOSED,
			SlotState.AiEasy => EPlayerType.SLOT_EASY_AI,
			SlotState.AiMedium => EPlayerType.SLOT_MED_AI,
			SlotState.AiBrutal => EPlayerType.SLOT_BRUTAL_AI,
			_ => EPlayerType.SLOT_PLAYER,
		};

		public static LobbyStage StageOf(ELobbyState state) => state switch
		{
			ELobbyState.COUNTDOWN => LobbyStage.Countdown,
			ELobbyState.INGAME => LobbyStage.InGame,
			ELobbyState.COMPLETE => LobbyStage.Complete,
			_ => LobbyStage.Setup,
		};

		public static AnticheatKind AnticheatOf(EKnownAnticheatID anticheatID) => anticheatID switch
		{
			EKnownAnticheatID.GO_INTEGRATED_AC => AnticheatKind.GoIntegrated,
			EKnownAnticheatID.EASY_ANTICHEAT => AnticheatKind.EasyAnticheat,
			_ => AnticheatKind.None,
		};

		public static Slot SlotOf(LobbyMember member)
		{
			bool human = member.IsHuman();
			bool ai = member.IsAI();
			return new Slot
			{
				Index = member.SlotIndex,
				State = SlotStateOf(member.SlotState),
				Member = human ? RealtimeMembers.Of(member.UserID) : null,
				Name = human ? member.DisplayName : null,
				Ready = (human || ai) && member.IsReady,
				HasMap = human ? member.HasMap : ai,
				Side = member.Side,
				Color = member.Color,
				Team = member.Team < 0 ? null : member.Team,
				StartPosition = member.StartingPosition
			};
		}

		public static LobbyInfo StateOf(Lobby lobby)
		{
			return new LobbyInfo
			{
				Id = lobby.LobbyID,
				Name = lobby.Name,
				HostId = lobby.Owner,
				Type = lobby.LobbyType == ELobbyType.QuickMatch ? LobbyType.QuickMatch : LobbyType.Custom,
				State = StageOf(lobby.State),
				Visibility = lobby.LobbyJoinability == ELobbyJoinability.FriendsOnly ? LobbyVisibility.Friends : LobbyVisibility.Public,
				HasPassword = lobby.IsPassworded,
				Map = new MapRef { Name = lobby.MapName, Path = lobby.MapPathWire, Official = lobby.IsMapOfficial },
				Region = lobby.Region,
				Anticheat = AnticheatOf(lobby.AnticheatID),
				Settings = new LobbySettings
				{
					StartingCash = (int)lobby.StartingCash,
					LimitSuperweapons = lobby.IsLimitSuperweapons,
					VanillaTeams = lobby.IsVanillaTeamsOnly,
					TrackStats = lobby.IsTrackingStats,
					AllowObservers = lobby.AllowObservers,
					MaxCameraHeight = lobby.MaximumCameraHeight
				},
				MatchId = lobby.MatchID == 0 ? null : (long)lobby.MatchID,
				CreatedAt = lobby.TimeCreated,
				Slots = lobby.Members.Select(SlotOf).ToArray()
			};
		}

		public static LobbySummary SummaryOf(Lobby lobby, UserSession viewer)
		{
			return new LobbySummary
			{
				Id = lobby.LobbyID,
				Name = lobby.Name,
				Host = RealtimeMembers.Of(lobby.Owner),
				State = StageOf(lobby.State),
				Map = new LobbySummaryMap { Name = lobby.MapName, Official = lobby.IsMapOfficial },
				Players = lobby.NumCurrentPlayers,
				MaxPlayers = lobby.MaxPlayers,
				HasPassword = lobby.IsPassworded,
				Region = lobby.Region,
				LatencyMs = EstimateLatencyMs(lobby, viewer),
				Anticheat = AnticheatOf(lobby.AnticheatID),
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

		// Custom lobbies in setup or countdown that the viewer may join
		public static bool IsListedFor(Lobby lobby, Int64 viewerID)
		{
			if (lobby.LobbyType != ELobbyType.CustomGame || (lobby.State != ELobbyState.GAME_SETUP && lobby.State != ELobbyState.COUNTDOWN) || lobby.GetNumberOfHumans() == 0)
			{
				return false;
			}

			SharedUserData? host = WebSocketManager.GetSharedDataForUser(lobby.Owner);
			if (host == null || viewerID == lobby.Owner)
			{
				return true;
			}

			UserSocialContainer social = host.GetSocialContainer();
			if (social.Blocked.Contains(viewerID))
			{
				return false;
			}

			return lobby.LobbyJoinability != ELobbyJoinability.FriendsOnly || social.Friends.Contains(viewerID);
		}

		// The slowest link to any other player bounds the viewer, so the lobby reports the largest estimate
		public static int EstimateLatencyMs(Lobby lobby, UserSession viewer)
		{
			int largest = 0;
			foreach (LobbyMember member in lobby.Members)
			{
				if (member.SlotState != EPlayerType.SLOT_PLAYER || member.UserID == viewer.m_UserID)
				{
					continue;
				}

				if (member.GetSession().TryGetTarget(out UserSession? session) && session != null)
				{
					double distanceKm = HaversineDistanceKm(viewer.m_dLatitude, viewer.m_dLongitude, session.m_dLatitude, session.m_dLongitude);
					largest = Math.Max(largest, Convert.ToInt32(2.0 * (PropagationDelayMs(distanceKm) * 2.0)));
				}
			}
			return largest;
		}

		private static double ToRadians(double angleInDegrees) => angleInDegrees * Math.PI / 180.0;

		private static double HaversineDistanceKm(double lat1, double lon1, double lat2, double lon2)
		{
			const double EarthRadiusKm = 6371;
			double dLat = ToRadians(lat2 - lat1);
			double dLon = ToRadians(lon2 - lon1);
			double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
				Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
				Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
			return EarthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
		}

		// One way delay through fiber at about 200000 km/s
		private static double PropagationDelayMs(double distanceKm) => distanceKm / 200000.0 * 1000;

		// The game connection of a lobby member, nothing when they are disconnected
		public static IRealtimeClient GameOf(LobbyMember member) =>
			RealtimeOut.Session(member.GetSession().TryGetTarget(out UserSession? session) ? session : null);

		public static string? ConnectionOf(UserSession? session) =>
			session == null ? null : WebSocketManager.GetWebSocketForSession(session)?.ConnectionId;
	}
}
