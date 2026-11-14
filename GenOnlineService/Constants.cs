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

using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Threading.Tasks;
using GenOnlineService.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace GenOnlineService
{
	public static class Constants
	{
		public const int GENERALS_ONLINE_VERSION = 1;
		public const int GENERALS_ONLINE_NET_VERSION = 1;
		public const int GENERALS_ONLINE_SERVICE_VERSION = 1;

		public const UInt16 g_DefaultCameraMaxHeight = 310;

		public const int OBSERVER_SIDE_VALUE = 1;
	}
	public enum EPendingLoginState
	{
		None = -1,
		Waiting = 0,
		LoginSuccess = 1,
		LoginFailed = 2
	};

	public enum EIPVersion
	{
		IPV4 = 0,
		IPV6
	}

	public enum EConnectionStateClient
	{
		NOT_CONNECTED,
		CONNECTING_DIRECT,
		FINDING_ROUTE,
		CONNECTED_DIRECT,
		CONNECTION_FAILED,
		CONNECTION_DISCONNECTED
	};

	public enum EConnectionState
	{
		NOT_CONNECTED,
		CONNECTING_DIRECT,
		CONNECTING_RELAY,
		CONNECTED_DIRECT,
		CONNECTED_RELAY,
		CONNECTION_FAILED
	};

	public enum ELobbyState
	{
		UNKNOWN = -1,
		GAME_SETUP,
		INGAME,
		COMPLETE,
		// a custom lobby start runs its mesh check and countdown in this state
		COUNTDOWN
	}

	[Flags]
	public enum ERoomFlags : int
	{
		ROOM_FLAGS_NONE = 0,
		ROOM_FLAGS_SHOW_ALL_MATCHES = 1 << 0
	}
	public class RoomData
	{
		public int id { get; set; } = -1;
		public string name { get; set; } = "";
		public int? parent_id { get; set; }
		public ERoomFlags flags { get; set; } = ERoomFlags.ROOM_FLAGS_NONE;
	}

	internal sealed class RoomConfigData
	{
		[JsonPropertyName("name")]
		public required string Name { get; init; }

		[JsonPropertyName("default")]
		public bool IsDefault { get; init; }

		[JsonPropertyName("rooms")]
		public List<RoomConfigData?> Rooms { get; init; } = [];
	}

	internal sealed class RoomCatalogEntry
	{
		public required int ID { get; init; }
		public required string Name { get; init; }
		public int? ParentID { get; init; }
		public required Int16 TargetRoomID { get; set; }
	}

	internal sealed class RoomCatalogData
	{
		public required IReadOnlyList<RoomCatalogEntry> Rooms { get; init; }
		public required IReadOnlyDictionary<int, RoomCatalogEntry> RoomsByID { get; init; }
		public required IReadOnlyDictionary<int, HashSet<int>> DescendantRoomIDs { get; init; }

		public bool TryResolveTargetRoomID(int selectedRoomID, out Int16 targetRoomID)
		{
			if (selectedRoomID == -1)
			{
				targetRoomID = -1;
				return true;
			}

			if (!RoomsByID.TryGetValue(selectedRoomID, out RoomCatalogEntry? room))
			{
				targetRoomID = -1;
				return false;
			}

			targetRoomID = room.TargetRoomID;
			return true;
		}

		public bool CanViewLobby(int selectedRoomID, int lobbyRoomID)
		{
			if (!RoomsByID.ContainsKey(selectedRoomID) || !RoomsByID.ContainsKey(lobbyRoomID))
			{
				return false;
			}

			return selectedRoomID == Rooms[0].ID
				|| selectedRoomID == lobbyRoomID
				|| DescendantRoomIDs[selectedRoomID].Contains(lobbyRoomID);
		}
	}

	public static class RoomCatalog
	{
		private static readonly JsonSerializerOptions s_jsonOptions = new()
		{
			ReadCommentHandling = JsonCommentHandling.Skip,
			AllowTrailingCommas = true,
			RespectNullableAnnotations = true
		};

		private static RoomCatalogData? s_catalog;

		internal static IReadOnlyList<RoomCatalogEntry> Rooms => Current.Rooms;
		private static RoomCatalogData Current => s_catalog
			?? throw new InvalidOperationException("The room catalog has not been initialized.");

		public static void Initialize(string catalogPath)
		{
			try
			{
				s_catalog = CreateFromJson(File.ReadAllText(catalogPath));
			}
			catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
			{
				throw new InvalidDataException($"Failed to load room catalog '{catalogPath}': {ex.Message}", ex);
			}
		}

		public static bool TryResolveTargetRoomID(int selectedRoomID, out Int16 targetRoomID)
		{
			return Current.TryResolveTargetRoomID(selectedRoomID, out targetRoomID);
		}

		public static bool CanViewLobby(int selectedRoomID, int lobbyRoomID)
		{
			return Current.CanViewLobby(selectedRoomID, lobbyRoomID);
		}

		private static RoomCatalogData CreateFromJson(string json)
		{
			List<RoomConfigData?> configuredRooms = JsonSerializer.Deserialize<List<RoomConfigData?>>(json, s_jsonOptions)
				?? throw new InvalidDataException("The room catalog must contain a JSON array.");

			if (configuredRooms.Count == 0)
			{
				throw new InvalidDataException("The room catalog must contain at least one room.");
			}
			if (configuredRooms.Any(room => room is null))
			{
				throw new InvalidDataException("The room catalog cannot contain null room entries.");
			}

			List<RoomCatalogEntry> rooms = [];
			Dictionary<int, HashSet<int>> descendantRoomIDs = [];

			RoomCatalogEntry FlattenRoom(RoomConfigData configuredRoom, int? parentID)
			{
				if (string.IsNullOrWhiteSpace(configuredRoom.Name))
				{
					throw new InvalidDataException("Room names cannot be empty.");
				}

				if (parentID is null && configuredRoom.IsDefault)
				{
					throw new InvalidDataException($"Top-level room '{configuredRoom.Name}' cannot be marked as default.");
				}

				if (configuredRoom.Rooms.Any(room => room is null))
				{
					throw new InvalidDataException("The room catalog cannot contain null room entries.");
				}

				int defaultRoomCount = configuredRoom.Rooms.Count(room => room!.IsDefault);
				if (configuredRoom.Rooms.Count > 0 && defaultRoomCount != 1)
				{
					throw new InvalidDataException($"Room '{configuredRoom.Name}' must contain exactly one default child room.");
				}

				if (rooms.Count > Int16.MaxValue)
				{
					throw new InvalidDataException($"The room catalog cannot contain more than {Int16.MaxValue + 1} rooms.");
				}

				RoomCatalogEntry room = new()
				{
					ID = rooms.Count,
					Name = configuredRoom.Name,
					ParentID = parentID,
					TargetRoomID = (Int16)rooms.Count
				};
				rooms.Add(room);

				HashSet<int> descendants = [];
				foreach (RoomConfigData? childConfig in configuredRoom.Rooms)
				{
					RoomConfigData childRoomConfig = childConfig!;
					RoomCatalogEntry child = FlattenRoom(childRoomConfig, room.ID);
					descendants.Add(child.ID);
					descendants.UnionWith(descendantRoomIDs[child.ID]);
					if (childRoomConfig.IsDefault)
					{
						room.TargetRoomID = child.TargetRoomID;
					}
				}

				descendantRoomIDs.Add(room.ID, descendants);
				return room;
			}

			foreach (RoomConfigData? configuredRoom in configuredRooms)
			{
				FlattenRoom(configuredRoom!, null);
			}

			return new RoomCatalogData
			{
				Rooms = rooms,
				RoomsByID = rooms.ToDictionary(room => room.ID),
				DescendantRoomIDs = descendantRoomIDs
			};
		}
	}

	public class UserSocialContainer
	{
		public HashSet<Int64> Friends { get; set; } = new HashSet<Int64>();
		public HashSet<Int64> PendingRequests { get; set; } = new HashSet<Int64>();
		public HashSet<Int64> Blocked { get; set; } = new HashSet<Int64>();
	}

	// NOTE: If you add to the below, make sure you initialize the dictionary
	public enum EUserSessionType
	{
		None = -1,
		GameClient = 0,
		ChatClient = 1,
		GameLauncher = 2
	}

	public static class KnownClients
	{
		public enum EKnownClients
		{
			unknown = -1,
			generalsmd = 0,
			generals = 1,
			launcher = 2,
			chat = 3,
			genhub = 4,
			custom_third_party_client = 5
		}

		public static ConcurrentDictionary<EKnownClients, EUserSessionType> KnownClientSessionTypes = new()
		{
			[EKnownClients.generalsmd] = EUserSessionType.GameClient,
			[EKnownClients.generals] = EUserSessionType.GameClient,
			[EKnownClients.launcher] = EUserSessionType.GameLauncher,
			[EKnownClients.chat] = EUserSessionType.ChatClient,
			[EKnownClients.genhub] = EUserSessionType.GameLauncher,
			[EKnownClients.custom_third_party_client] = EUserSessionType.GameClient
		};
	}



	// TODO
	static class WebSocketManager
	{
		private static readonly ILogger s_log = AppLog.For(typeof(WebSocketManager));

		public static int g_PeakConnectionCount = 0;

		private static void RecordConnectionSnapshot()
		{
			foreach (var entry in m_dictWebsockets)
			{
				AppMetrics.RecordWebSocketConnections(entry.Key, entry.Value.Count);
			}
		}

		public static Task<RealtimeConnection?> CreateSession(AppDbContext _db, EUserSessionType sessionType, bool bIsReconnect, Int64 ownerID, KnownClients.EKnownClients client_id, string ipAddr, string strContinent, string strCountry, double dLatitude, double dLongitude, HubConnectionContext transport)
		{
			return ServiceLocator.Services.GetRequiredService<LobbyManager>().OpenSessionAsync(_db, sessionType, bIsReconnect, ownerID, client_id, ipAddr, strContinent, strCountry, dLatitude, dLongitude, transport);
		}

		internal static async Task<RealtimeConnection?> InstallSession(AppDbContext _db, EUserSessionType sessionType, bool bIsReconnect, Int64 ownerID, KnownClients.EKnownClients client_id, string ipAddr, string strContinent, string strCountry, double dLatitude, double dLongitude, HubConnectionContext transport)
		{
			string strDisplayName = await Database.Users.GetDisplayName(_db, ownerID);
			await Database.Users.UpdateLastLoginData(_db, ownerID, ipAddr, client_id, strCountry, bIsReconnect);

			// if we have cache data, that means its a reconnect, noraml connections go through login flows which reset cache data
			UserSession? userCacheData = WebSocketManager.GetSessionFromUser(ownerID, sessionType);
			if (bIsReconnect)
			{
				// this is a reconnect, re-use cache
				s_log.LogDebug("WebSocket reconnect for user {UserId} ({SessionType})", ownerID, sessionType);

				// if its a reconnect, and we dont have cache OR shared data, its probably a server restart, so return null
				if (userCacheData == null)
				{
					return null;
				}
				else
				{
					// depending on what our ref count was, we MAY need to recreate our shared data
					if (m_dictSharedUserData.TryGetValue(ownerID, out SharedUserData? sharedData))
					{
						// nothing to do here for shared user data, since the session was abandoned but not fully destroyed, it should still have user data
					}
					else
					{
						// get and cache social container
						UserSocialContainer socialContainer = new();
						socialContainer.Friends = await Database.Social.GetFriends(_db, ownerID);
						socialContainer.PendingRequests = await Database.Social.GetPendingFriendsRequests(_db, ownerID);
						socialContainer.Blocked = await Database.Social.GetBlocked(_db, ownerID);

						// get stats
						PlayerStats GameStats = await Database.UserStats.GetPlayerStats(_db, ownerID);

						m_dictSharedUserData[ownerID] = new SharedUserData(ownerID, socialContainer, strDisplayName, GameStats);
					}

					// clear abandoned flag
					userCacheData.MarkNotAbandoned();

					// NOTE: We intentionally do NOT call ClearPlayerIngameAbandon on reconnect.
					// RecordPlayerIngameAbandon only stores the FIRST disconnect time.  Clearing
					// it here was the root cause of the wrong-winner ELO bug:
					//   A kills game → RecordPlayerIngameAbandon(A) set
					//   A relaunches and reconnects → ClearPlayerIngameAbandon(A) wipes the record
					//   Fallback uses TimeMemberLeft[A] (set much later) > IngameAbandon[B]
					//   → A incorrectly wins
				}
			}
			else
			{
				s_log.LogDebug("WebSocket connect for user {UserId} ({SessionType})", ownerID, sessionType);

				// get and cache social container
				UserSocialContainer socialContainer = new();
				socialContainer.Friends = await Database.Social.GetFriends(_db, ownerID);
				socialContainer.PendingRequests = await Database.Social.GetPendingFriendsRequests(_db, ownerID);
				socialContainer.Blocked = await Database.Social.GetBlocked(_db, ownerID);

				// get stats
				PlayerStats GameStats = await Database.UserStats.GetPlayerStats(_db, ownerID);

				userCacheData = new UserSession(ownerID, sessionType, client_id, strContinent, strCountry, dLatitude, dLongitude);
				m_dictUserSessions[sessionType][ownerID] = userCacheData;
				
				// TODO_EFCORE: check reconnect again, reconnect shouldnt increment ref count (nothing is done above)
				// create or increment shared data
				if (m_dictSharedUserData.TryGetValue(ownerID, out SharedUserData? sharedData))
				{
					// increment
					sharedData.IncrementRefCount();
				}
				else
				{
					m_dictSharedUserData[ownerID] = new SharedUserData(ownerID, socialContainer, strDisplayName, GameStats);
				}
			}

			// a connection of the same user and session type replaces the previous one, which is told why
			if (m_dictWebsockets[sessionType].TryRemove(ownerID, out RealtimeConnection? supersededSess) && supersededSess != null)
			{
				s_log.LogDebug("Closing superseded connection for {UserId} ({DisplayName})", ownerID, strDisplayName);
				await supersededSess.CloseAsync(CloseCodes.SessionSuperseded, allowReconnect: false);
			}

			RealtimeConnection newSess = new RealtimeConnection(transport, sessionType, ownerID, userCacheData);
			m_dictWebsockets[sessionType][ownerID] = newSess;
			RecordConnectionSnapshot();


			// TODO_EFCORE: Optimize this, dont iterate all the time
			int numSessions = WebSocketManager.GetNumberOfUsersOnline();

			if (numSessions > g_PeakConnectionCount)
			{
				g_PeakConnectionCount = numSessions;
			}

			Console.Title = String.Format("GenOnline - {0} players", numSessions);

			return newSess;
		}

		public static void Tick()
		{
			FlushLobbyListUpdates();
		}

		public static int GetNumberOfUsersOnline()
		{
			int numSessions = 0;
			foreach (var kvPair in m_dictUserSessions)
			{
				numSessions += kvPair.Value.Count;
			}

			return numSessions;
		}

		// Pure re-check used by CheckForTimeouts right before actually clearing a snapshotted
		// abandoned+expired entry: refuses unless it's still the SAME session object (a reconnect in
		// between would have registered a new one) and it is STILL abandoned and expired.
		internal static bool ShouldStillClearAbandonedSession(UserSession? currentSession, UserSession snapshotSession)
		{
			if (!ReferenceEquals(currentSession, snapshotSession))
			{
				return false;
			}

			return currentSession.IsAbandoned() && currentSession.NeedsCleanup();
		}

		public static async Task CheckForTimeouts()
		{
			// do we need to clear out cache entries?
			List<(Int64 UserID, EUserSessionType SessionType, UserSession Session)> lstCacheEntriesToDestroy = new();
			foreach (var sessionDataPerClientType in m_dictUserSessions)
			{
				foreach (var sessionData in sessionDataPerClientType.Value)
				{
					if (sessionData.Value.IsAbandoned())
					{
						if (sessionData.Value.NeedsCleanup())
						{
							lstCacheEntriesToDestroy.Add((sessionData.Key, sessionData.Value.GetSessionType(), sessionData.Value));
						}
					}
				}
			}

			foreach (var userData in lstCacheEntriesToDestroy)
				await ServiceLocator.Services.GetRequiredService<LobbyManager>().ExpireSessionAsync(userData.Session);
		}

		public static Task DeleteSession(Int64 userID, EUserSessionType sessionType, RealtimeConnection? oldWS, bool destroySession, UserSession? expectedSession = null)
		{
			var session = expectedSession ?? oldWS?.OwnerSession ?? GetSessionFromUser(userID, sessionType);
			return ServiceLocator.Services.GetRequiredService<LobbyManager>().DisconnectSessionAsync(session, oldWS, destroySession);
		}

		internal static RealtimeConnection? DetachSocket(UserSession session)
		{
			var sockets = m_dictWebsockets[session.GetSessionType()];
			if (!sockets.TryGetValue(session.m_UserID, out var socket) || !ReferenceEquals(socket.OwnerSession, session)) return null;
			if (!sockets.TryRemove(new KeyValuePair<long, RealtimeConnection>(session.m_UserID, socket))) return null;
			RecordConnectionSnapshot();
			return socket;
		}

		internal static bool RemoveSessionData(UserSession session)
		{
			if (!m_dictUserSessions[session.GetSessionType()].TryRemove(new KeyValuePair<long, UserSession>(session.m_UserID, session)))
				return false;

			// Every real teardown (abandoned expiry, logout, kick/ban, superseding or fresh login) ends here via
			// DestroySessionAsync, so the per-user middleware ID is dropped here and nowhere else. Transient
			// websocket drops never reach this, which is what lets the ID survive a reconnect.
			if (session.GetSessionType() == EUserSessionType.GameClient)
			{
				ClearMiddlewareIDForUser(session.m_UserID);
			}

			if (m_dictSharedUserData.TryGetValue(session.m_UserID, out var shared))
			{
				shared.DecrementRefCount();
				if (shared.NeedsGC())
				{
					m_dictSharedUserData.TryRemove(new KeyValuePair<long, SharedUserData>(session.m_UserID, shared));
				}
			}

			// friends see the session type disappear, or the user go offline with the last one
			RealtimeHub.PublishPresence(session.m_UserID);
			Console.Title = $"GenOnline - {GetNumberOfUsersOnline()} players";
			return true;
		}

		/*
		public static ChatSession? GetWebSocketForUser(Int64 userID)
		{
			if (m_dictSessions.TryGetValue(userID, out ChatSession? retVal))
			{
				return retVal;
			}
			else
			{
				return null;
			}
		}
		*/

		// TODO_EFCORE: Just use a weakref to the websocket from the user session instead of lookups
		public static RealtimeConnection? GetWebSocketForSession(UserSession session)
		{
			if (m_dictWebsockets[session.GetSessionType()].TryGetValue(session.m_UserID, out RealtimeConnection? retVal) && ReferenceEquals(retVal.OwnerSession, session))
			{
				return retVal;
			}
			else
			{
				return null;
			}
		}


		private static ConcurrentDictionary<EUserSessionType, ConcurrentDictionary<Int64, RealtimeConnection>> m_dictWebsockets = new()
		{
			// Initialize everything ahead of time so we don't have to keep doing lookups to see if it exists
			[EUserSessionType.GameClient] = new(),
			[EUserSessionType.GameLauncher] = new(),
			[EUserSessionType.ChatClient] = new(),
		};

		private static ConcurrentDictionary<EUserSessionType, ConcurrentDictionary<Int64, UserSession>> m_dictUserSessions = new()
		{
			// Initialize everything ahead of time so we don't have to keep doing lookups to see if it exists
			[EUserSessionType.GameClient] = new (),
			[EUserSessionType.GameLauncher] = new (),
			[EUserSessionType.ChatClient] = new (),
		};

		private static ConcurrentDictionary<Int64, SharedUserData> m_dictSharedUserData = new();
		private static ConcurrentDictionary<Int64, string> m_dictMiddlewareIDs = new();
		private static ConcurrentDictionary<string, Int64> m_dictMiddlewareIDOwners = new(StringComparer.Ordinal);
		private static readonly object m_middlewareIDLock = new();

		public static ConcurrentDictionary<EUserSessionType, ConcurrentDictionary<Int64, UserSession>> GetUserDataCache()
		{
			return m_dictUserSessions;
		}

		public static SharedUserData? GetSharedDataForUser(string strDisplayName)
		{
			foreach (var kvPair in m_dictSharedUserData)
			{
				if (String.Equals(kvPair.Value.m_strDisplayName, strDisplayName, StringComparison.OrdinalIgnoreCase))
				{
					return kvPair.Value;
				}
			}

			return null;
		}


		public static SharedUserData? GetSharedDataForUser(Int64 userID)
		{
			if (m_dictSharedUserData.TryGetValue(userID, out SharedUserData? retVal))
			{
				return retVal;
			}
			else
			{
				return null;
			}
		}

		public static UserSession? GetSessionFromUser(Int64 userID, EUserSessionType sessionType)
		{
			if (m_dictUserSessions[sessionType].TryGetValue(userID, out UserSession? retVal))
			{
				return retVal;
			}
			else
			{
				return null;
			}
		}

		public enum EMiddlewareIDApplyResult
		{
			Unchanged,
			Changed,
			HeldByOtherUser,
			LockedInLobby
		}

		// While the user is a member of a lobby (any state) their middleware ID may go from empty to set (late resolve),
		// but an existing ID is never replaced by a different one: peers and anti-cheat have already registered it.
		// Outside a lobby a new login may set a new value.
		// The caller must hold the user's membership gate so the in-lobby decision cannot race a join or leave.
		// A middleware ID belongs to at most one user, and a holder without any session loses it to the next claimant.
		public static EMiddlewareIDApplyResult ApplyMiddlewareIDForUser(Int64 userID, string strMiddlewareUserID, bool bInLobby)
		{
			if (userID == -1 || String.IsNullOrEmpty(strMiddlewareUserID))
			{
				return EMiddlewareIDApplyResult.Unchanged;
			}

			lock (m_middlewareIDLock)
			{
				m_dictMiddlewareIDs.TryGetValue(userID, out string? strPrevious);
				if (String.Equals(strPrevious, strMiddlewareUserID, StringComparison.Ordinal))
				{
					return EMiddlewareIDApplyResult.Unchanged;
				}

				if (bInLobby && !String.IsNullOrEmpty(strPrevious))
				{
					s_log.LogWarning("Ignoring attempt to replace the middleware ID of user {UserId} while in a lobby", userID);
					return EMiddlewareIDApplyResult.LockedInLobby;
				}

				if (m_dictMiddlewareIDOwners.TryGetValue(strMiddlewareUserID, out Int64 ownerID) && ownerID != userID)
				{
					if (UserHasAnySession(ownerID))
					{
						s_log.LogWarning("Rejecting middleware ID for user {UserId} because user {OwnerId} holds it", userID, ownerID);
						return EMiddlewareIDApplyResult.HeldByOtherUser;
					}

					m_dictMiddlewareIDs.TryRemove(new KeyValuePair<Int64, string>(ownerID, strMiddlewareUserID));
					m_dictMiddlewareIDOwners.TryRemove(new KeyValuePair<string, Int64>(strMiddlewareUserID, ownerID));
				}

				if (!String.IsNullOrEmpty(strPrevious))
				{
					m_dictMiddlewareIDOwners.TryRemove(new KeyValuePair<string, Int64>(strPrevious, userID));
				}

				m_dictMiddlewareIDs[userID] = strMiddlewareUserID;
				m_dictMiddlewareIDOwners[strMiddlewareUserID] = userID;
				return EMiddlewareIDApplyResult.Changed;
			}
		}

		private static bool UserHasAnySession(Int64 userID)
		{
			foreach (var sessionsByType in m_dictUserSessions.Values)
			{
				if (sessionsByType.ContainsKey(userID))
				{
					return true;
				}
			}

			return false;
		}

		public static string GetMiddlewareIDForUser(Int64 userID)
		{
			if (userID != -1 && m_dictMiddlewareIDs.TryGetValue(userID, out string? strMiddlewareUserID))
			{
				return strMiddlewareUserID ?? String.Empty;
			}

			return String.Empty;
		}

		public static void ClearMiddlewareIDForUser(Int64 userID)
		{
			lock (m_middlewareIDLock)
			{
				if (m_dictMiddlewareIDs.TryRemove(userID, out string? strPrevious) && !String.IsNullOrEmpty(strPrevious))
				{
					m_dictMiddlewareIDOwners.TryRemove(new KeyValuePair<string, Int64>(strPrevious, userID));
				}
			}
		}

		public static List<UserSession> GetAllDataFromUser(Int64 userID)
		{
			List<UserSession> lstRet = new();

			foreach (var sessionByClient in m_dictUserSessions)
			{
				if (sessionByClient.Value.TryGetValue(userID, out UserSession? sess))
				{
					lstRet.Add(sess);
				}
			}

			return lstRet;
		}

		// Closes the user's connections with a protocol close code and destroys their sessions, all or one session type
		public static async Task DisconnectUser(Int64 userID, string closeCode, bool allowReconnect, ModerationNoticeEvent? notice = null, EUserSessionType? onlySessionType = null)
		{
			List<UserSession> userSessions = GetAllDataFromUser(userID);

			foreach (UserSession userSession in userSessions)
			{
				if (onlySessionType != null && userSession.GetSessionType() != onlySessionType)
				{
					continue;
				}

				try
				{
					RealtimeConnection? oldWS = GetWebSocketForSession(userSession);
					if (oldWS != null)
					{
						if (notice != null)
						{
							await oldWS.Client.ModerationNotice(notice);
						}
						await oldWS.CloseAsync(closeCode, allowReconnect);
					}

					await DeleteSession(userID, userSession.GetSessionType(), oldWS, true, userSession);
				}
				catch (Exception ex)
				{
					s_log.LogError(ex, "DisconnectUser failed for user {UserId}, session {SessionType}", userID, userSession.GetSessionType());
				}
			}
		}

		public static Task<bool> ClearDataFromUser(Int64 userID, EUserSessionType sessionType)
		{
			var session = GetSessionFromUser(userID, sessionType);
			return ServiceLocator.Services.GetRequiredService<LobbyManager>().ClearSessionAsync(session);
		}


		// Lobby list changes coalesce in LobbyListPublisher and flush from the lobby tick
		internal static void FlushLobbyListUpdates()
		{
			LobbyListPublisher.Flush(ServiceLocator.Services.GetRequiredService<LobbyManager>());
		}
	}

	// NOTE: only one instance for ALL websockets/sessions, and is destroyed when the last one of the former is destroyed
	public class SharedUserData
	{
		private int m_RefCount = 0;
		private readonly Queue<Int64> m_chatMessageTimestamps = new();
		private Int64? m_lastChatRateLimitNoticeTimestamp;

		public void IncrementRefCount()
		{
			Interlocked.Increment(ref m_RefCount);
		}

		public void DecrementRefCount()
		{
			Interlocked.Decrement(ref m_RefCount);
		}

		public bool NeedsGC()
		{
			return m_RefCount <= 0;
		}

		public Int64 m_UserID = -1;
		public string m_strDisplayName = String.Empty;

		// contains ELO too
		public PlayerStats? GameStats { get; private set; } = null;

		private UserSocialContainer m_socialContainer;

		public UserSocialContainer GetSocialContainer() { return m_socialContainer; }

		public bool HasPermission(string permission) { return StaffRoles.HasPermission(m_UserID, permission); }

		public bool TryConsumeChatMessage()
		{
			const int maxMessages = 3;
			const Int64 windowMilliseconds = 9000;
			Int64 now = Environment.TickCount64;

			lock (m_chatMessageTimestamps)
			{
				while (m_chatMessageTimestamps.TryPeek(out Int64 timestamp)
					&& now - timestamp >= windowMilliseconds)
				{
					m_chatMessageTimestamps.Dequeue();
				}

				if (m_chatMessageTimestamps.Count >= maxMessages)
				{
					return false;
				}

				m_chatMessageTimestamps.Enqueue(now);
				return true;
			}
		}

		public bool TryConsumeChatRateLimitNotice()
		{
			const Int64 intervalMilliseconds = 9000;
			Int64 now = Environment.TickCount64;

			lock (m_chatMessageTimestamps)
			{
				if (m_lastChatRateLimitNoticeTimestamp.HasValue
					&& now - m_lastChatRateLimitNoticeTimestamp.Value < intervalMilliseconds)
				{
					return false;
				}

				m_lastChatRateLimitNoticeTimestamp = now;
				return true;
			}
		}

		public SharedUserData(Int64 ownerID, UserSocialContainer socialContainer, string strDisplayName, PlayerStats userStats)
		{
			m_strDisplayName = strDisplayName;

			m_UserID = ownerID;

			m_socialContainer = socialContainer;

			GameStats = userStats;

			// upon creation, immediately increment ref count
			IncrementRefCount();
		}
	}

	// Core:reconnect_grace_period_ms in appsettings.json, read on use. Mirrors the pattern used by
	// FullMeshCheckSettings in LobbyManager.cs.
	internal static class UserSessionSettings
	{
		internal static Int64 ReconnectGracePeriodMS => Get("reconnect_grace_period_ms", 30000);

		private static Int64 Get(string key, Int64 defaultValue)
		{
			Int64 value = Program.g_Config?.GetSection("Core").GetValue(key, defaultValue) ?? defaultValue;
			return value > 0 ? value : defaultValue;
		}
	}

	public class UserSession
	{
		private static readonly ILogger s_log = AppLog.For(typeof(UserSession));

		private int retired;
		internal bool IsRetired => Volatile.Read(ref retired) != 0;
		internal void Retire() => Interlocked.Exchange(ref retired, 1);

		public Int64 m_UserID = -1;
		
		public string m_strContinent;
		public string m_strCountry;
		public double m_dLatitude;
		public double m_dLongitude;
		
		private EUserSessionType m_sessionType = EUserSessionType.None;

		// Matchmaking data
		public UInt16 MatchmakingPlaylistID = 0;
		public long MatchmakingStartedTimestamp = 0;
		public ConcurrentList<int> MatchmakingMapIndicies = new();
		internal bool IsRegisteredForMatchmaking { get; set; } = false;
		internal SemaphoreSlim MatchmakingStateLock { get; } = new(1, 1);

		// NOTE: These are not set on login, only when in quickmatch!
		public UInt32 ExeCRC = 0;
		public UInt32 IniCRC = 0;
		public EKnownAnticheatID AnticheatID = EKnownAnticheatID.NONE;

		private ConcurrentList<UInt64> m_lstHistoricMatchIDs = new();
		private ConcurrentDictionary<UInt64, int> m_lstHistoricMatchIDToSlotIndexMap = new();
		private ConcurrentDictionary<UInt64, int> m_lstHistoricMatchIDToArmy = new();
		private ConcurrentDictionary<UInt64, DateTime> m_lstHistoricMatchIDToCreationTime = new();

		private Int64 m_timeAbandoned = -1;

		public KnownClients.EKnownClients m_client_id = KnownClients.EKnownClients.unknown;
		DateTime m_CreateTime = DateTime.Now;
		public DateTime GetCreationTime()
		{
			return m_CreateTime;
		}


		public string GetMiddlewareID()
		{
			return WebSocketManager.GetMiddlewareIDForUser(m_UserID);
		}

		public EUserSessionType GetSessionType()
		{
			return m_sessionType;
		}

		public UInt64 GetLatestMatchID()
		{
			UInt64 mostRecentMatchID = 0;
			if (m_lstHistoricMatchIDs.Count > 0)
			{
				mostRecentMatchID = m_lstHistoricMatchIDs[m_lstHistoricMatchIDs.Count - 1];
			}

			return mostRecentMatchID;
		}

		// Modules already reported for the current match. Type2 probes repeat and the DB has a unique key
		// on (user_id, match_id, module_name), so only the current match is relevant and older entries are dropped.
		private const int MaxFlaggedModulesPerMatch = 4096;
		private readonly object m_flaggedModulesLock = new();
		private UInt64 m_flaggedModulesMatchID = 0;
		private readonly HashSet<string> m_flaggedModules = new(StringComparer.Ordinal);

		public bool IsModuleFlagged(UInt64 matchID, string moduleName)
		{
			lock (m_flaggedModulesLock)
			{
				return m_flaggedModulesMatchID == matchID && m_flaggedModules.Contains(moduleName);
			}
		}

		// Call only after the DB write succeeded, so a transient failure is retried on the next probe
		public void MarkModulesFlagged(UInt64 matchID, IEnumerable<string> moduleNames)
		{
			lock (m_flaggedModulesLock)
			{
				if (m_flaggedModulesMatchID != matchID)
				{
					m_flaggedModules.Clear();
					m_flaggedModulesMatchID = matchID;
				}

				foreach (string moduleName in moduleNames)
				{
					// Bound memory against clients spamming unique names, the DB upsert stays idempotent anyway
					if (m_flaggedModules.Count >= MaxFlaggedModulesPerMatch)
					{
						break;
					}

					m_flaggedModules.Add(moduleName);
				}
			}
		}

		public TimeSpan GetDuration()
		{
			return DateTime.Now - m_CreateTime;
		}

		public UserSession(Int64 ownerID, EUserSessionType sessionType, KnownClients.EKnownClients client_id, string strContinent, string strCountry, double dLatitude, double dLongitude)
		{
			m_sessionType = sessionType;
			m_client_id = client_id;
			m_strContinent = strContinent;
			m_strCountry = strCountry;
			m_dLatitude = dLatitude;
			m_dLongitude = dLongitude;

			m_UserID = ownerID;
		}

		public void MarkAbandoned()
		{
			m_timeAbandoned = Environment.TickCount64;
		}
		public void MarkNotAbandoned()
		{
			m_timeAbandoned = -1;
		}

		public bool IsAbandoned()
		{
			RealtimeConnection? websocketForUser = WebSocketManager.GetWebSocketForSession(this);
			return m_timeAbandoned != -1 && websocketForUser == null;
		}

		// Events for this session's current connection only, nothing while it is disconnected
		public IRealtimeClient Client => RealtimeOut.Session(this);

		public async Task<RealtimeConnection?> CloseWebsocket(string closeCode, bool allowReconnect)
		{
			RealtimeConnection? websocketForUser = WebSocketManager.GetWebSocketForSession(this);
			if (websocketForUser != null)
			{
				await websocketForUser.CloseAsync(closeCode, allowReconnect);
			}

			return websocketForUser;
		}

		public bool NeedsCleanup()
		{
			// Grace period an abandoned (no live websocket) session gets before it's torn down,
			// letting a brief disconnect reconnect instead of losing the slot. Configurable via
			// Core:reconnect_grace_period_ms; defaults to 30 seconds, unchanged from before this
			// was configurable (the old comment here said "5 minutes", which was wrong - 30000 is
			// milliseconds, i.e. 30 seconds).
			return Environment.TickCount64 - m_timeAbandoned >= UserSessionSettings.ReconnectGracePeriodMS;
		}

		public string GetFullCountryName()
		{
			RegionInfo ri = new RegionInfo(m_strCountry);
			return ri.EnglishName;
		}

		public string GetFullContinentName()
		{
			switch (m_strContinent)
			{
				case "AF": return "Africa";
				case "AN": return "Antartica";
				case "AS": return "Asia";
				case "EU": return "Europe";
				case "NA": return "North America";
				case "OC": return "Oceania";
				case "SA": return "South America";
				case "T1": return "Tor";
				default: return "Unknown";
			}
		}

		// Enough history to cover any realistic upload/outcome window; old entries just waste memory.
		private const int MaxHistoricMatches = 50;

		public void RegisterHistoricMatchID(UInt64 matchID, int slotIndex, int army, DateTime creationTime)
		{
			m_lstHistoricMatchIDs.Add(matchID);
			m_lstHistoricMatchIDToSlotIndexMap[matchID] = slotIndex;
			m_lstHistoricMatchIDToArmy[matchID] = army;
			m_lstHistoricMatchIDToCreationTime[matchID] = creationTime;

			// Trim oldest entries so the lists don't grow without bound over long sessions.
			while (m_lstHistoricMatchIDs.Count > MaxHistoricMatches)
			{
				UInt64 oldest = m_lstHistoricMatchIDs[0];
				m_lstHistoricMatchIDs.Remove(oldest);
				m_lstHistoricMatchIDToSlotIndexMap.TryRemove(oldest, out _);
				m_lstHistoricMatchIDToArmy.TryRemove(oldest, out _);
				m_lstHistoricMatchIDToCreationTime.TryRemove(oldest, out _);
				m_dictReportedMatchOutcomes.TryRemove(oldest, out _);
			}
		}

		private ConcurrentDictionary<UInt64, byte> m_dictReportedMatchOutcomes = new();

		// Returns true only the first time an outcome is reported for a given match, so a client cannot replay the
		// same result to inflate aggregate stats.
		public bool TryMarkMatchOutcomeReported(UInt64 matchID)
		{
			return m_dictReportedMatchOutcomes.TryAdd(matchID, 0);
		}

		public bool WasPlayerInMatch(UInt64 matchID, out int slotIndexInLobby, out int army, out DateTime lobbyCreationTime)
		{
			slotIndexInLobby = -1;
			army = -1;
			lobbyCreationTime = DateTime.UnixEpoch;

			bool bWasInMatch = m_lstHistoricMatchIDs.Contains(matchID);

			if (bWasInMatch)
			{
				// TODO: Optimize storage here
				slotIndexInLobby = m_lstHistoricMatchIDToSlotIndexMap[matchID];
				army = m_lstHistoricMatchIDToArmy[matchID];
				lobbyCreationTime = m_lstHistoricMatchIDToCreationTime[matchID];
			}

			return bWasInMatch;
		}

		public bool TryUpdateSessionNetworkRoom(Int16 newRoomID)
		{
			if (!RoomCatalog.TryResolveTargetRoomID(newRoomID, out Int16 targetRoomID))
			{
				s_log.LogWarning("Rejected invalid network room selection {RequestedRoomId} from user {UserId}, keeping selected room {SelectedRoomId}",
					newRoomID, m_UserID, selectedNetworkRoomID);
				return false;
			}

			selectedNetworkRoomID = newRoomID;
			networkRoomID = targetRoomID;
			return true;
		}

		public void ClearSessionNetworkRoom()
		{
			selectedNetworkRoomID = -1;
			networkRoomID = -1;
		}

		public void UpdateSessionLobbyID(Int64 newLobbyID)
		{
			if (m_sessionType == EUserSessionType.GameClient)
			{
				currentLobbyID = newLobbyID;
			}
		}

		// Client-facing room used for lobby filtering.
		public Int16 selectedNetworkRoomID = -1;

		// Concrete room used for presence, chat, and lobby creation.
		public Int16 networkRoomID = -1;


		// lobby id
		public Int64 currentLobbyID = -1;
	}

	public enum ESessionAccessType
	{
		Authenticate, // log in and out
		Social, // friends lists
		ServerListReadOnly, // can read lobby list and players etc, but cannot join
		StatsReadOnly, // can read stats for any user, but not write anything
		Gameplay, // Create lobbies, Anticheat, Middleware login, Matchmaking, match screenshots, replays, join lobby, etc
	};
	public static class SessionHelpers
	{
		private static readonly ILogger s_log = AppLog.For(typeof(SessionHelpers));

		public static bool SessionTypeHasAccessTo(EUserSessionType sessType, ESessionAccessType accessType)
		{
			if (sessType == EUserSessionType.GameClient) // client can do anything
			{
				return true;
			}

			// GameLauncher can read the server list but cannot join or mutate lobbies.
			if (accessType == ESessionAccessType.ServerListReadOnly
				&& sessType == EUserSessionType.GameLauncher)
			{
				return true;
			}

			return false;
		}

		public async static Task SetUsedLoggedIn(Int64 userID, KnownClients.EKnownClients clientID, EUserSessionType sessionType)
		{
			// TODO_EFCORE: website uses this index as 1 (60hz) to 0 (30hz), update it to use new enum + support new clients, also need to update DB to match
			// TODO_EFCORE: Move away from db for this and just have website login call endpoint on service

			s_log.LogInformation("StartSession deleting other sessions for user {UserId}", userID);

			// kill any WS they had too, StartSession comes before WS connects
			// disconnect any other sessions with this ID
			UserSession? sess = GenOnlineService.WebSocketManager.GetSessionFromUser(userID, sessionType);
			if (sess != null)
			{
				s_log.LogInformation("Found duplicate session for user {UserId}", userID);

				RealtimeConnection? oldWS = GenOnlineService.WebSocketManager.GetWebSocketForSession(sess);
				await GenOnlineService.WebSocketManager.DeleteSession(userID, sessionType, oldWS, false, sess);
			}
		}
	}

	public enum EAccountType
	{
		Unknown = -1,
		Steam = 0,
		Discord = 1,
		Ghost = 2,
		DevAccount = 3
	}

	public class PlayerStats
	{
		const int numGeneralsEntries = 15;

		public PlayerStats(Int64 inUserID, int inEloRating, int inEloMatches, int inMonthlyEloRating)
		{
			userID = inUserID;
			EloRating = inEloRating;
			EloMatches = inEloMatches;
			MonthlyEloRating = inMonthlyEloRating;

            // init arrays, rest are init'ed below
            for (int i = 0; i < numGeneralsEntries; ++i)
			{
				losses[i] = 0;
				games[i] = 0;
				duration[i] = 0;
				wins[i] = 0;
				unitsKilled[i] = 0;
				unitsLost[i] = 0;
				unitsBuilt[i] = 0;
				buildingsKilled[i] = 0;
				buildingsLost[i] = 0;
				buildingsBuilt[i] = 0;
				earnings[i] = 0;
				techCaptured[i] = 0;
				discons[i] = 0;
				desyncs[i] = 0;
				surrenders[i] = 0;
				gamesOf2p[i] = 0;
				gamesOf3p[i] = 0;
				gamesOf4p[i] = 0;
				gamesOf5p[i] = 0;
				gamesOf6p[i] = 0;
				gamesOf7p[i] = 0;
				gamesOf8p[i] = 0;
				customGames[i] = 0;
				QMGames[i] = 0;
			}
		}

		public Int64 userID { get; set; } = -1;
		public int EloRating { get; set; } = EloConfig.BaseRating;
		public int EloMatches { get; set; } = 0;
		public int MonthlyEloRating { get; set; } = EloConfig.BaseRating;

		public int[] wins { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] losses { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] games { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] duration { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] unitsKilled { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] unitsLost { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] unitsBuilt { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] buildingsKilled { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] buildingsLost { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] buildingsBuilt { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] earnings { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] techCaptured { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] discons { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] desyncs { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] surrenders { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] gamesOf2p { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] gamesOf3p { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] gamesOf4p { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] gamesOf5p { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] gamesOf6p { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] gamesOf7p { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] gamesOf8p { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] customGames { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] QMGames { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] customGamesPerGeneral { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] quickMatchesPerGeneral { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int locale { get; set; } = 0;
		public int gamesAsRandom { get; set; } = 0;
		public string options { get; set; } = String.Empty;
		public string systemSpec { get; set; } = String.Empty;
		public float lastFPS { get; set; } = 0F;
		public int lastGeneral { get; set; } = 0;
		public int gamesInRowWithLastGeneral { get; set; } = 0;
		public int challengeMedals { get; set; } = 0;
		public int battleHonors { get; set; } = 0;
		public int QMwinsInARow { get; set; } = 0;
		public int maxQMwinsInARow { get; set; } = 0;
		public int winsInARow { get; set; } = 0;
		public int maxWinsInARow { get; set; } = 0;
		public int lossesInARow { get; set; } = 0;
		public int maxLossesInARow { get; set; } = 0;
		public int disconsInARow { get; set; } = 0;
		public int maxDisconsInARow { get; set; } = 0;
		public int desyncsInARow { get; set; } = 0;
		public int maxDesyncsInARow { get; set; } = 0;
		public int builtParticleCannon { get; set; } = 0;
		public int builtNuke { get; set; } = 0;
		public int builtSCUD { get; set; } = 0;
		public int lastLadderPort { get; set; } = 0;
		public string lastLadderHost { get; set; } = String.Empty;

		public void ProcessFromDB(EStatIndex stat_id, int value)
		{
			int index = (int)stat_id % numGeneralsEntries;
			switch (stat_id)
			{
				case >= EStatIndex.WINS_PER_GENERAL_0 and <= EStatIndex.WINS_PER_GENERAL_14:
					wins[index] = value;
					break;
				case >= EStatIndex.LOSSES_PER_GENERAL_0 and <= EStatIndex.LOSSES_PER_GENERAL_14:
					losses[index] = value;
					break;
				case >= EStatIndex.GAMES_PER_GENERAL_0 and <= EStatIndex.GAMES_PER_GENERAL_14:
					games[index] = value;
					break;
				case >= EStatIndex.DURATION_PER_GENERAL_0 and <= EStatIndex.DURATION_PER_GENERAL_14:
					duration[index] = value;
					break;
				case >= EStatIndex.UNITSKILLED_PER_GENERAL_0 and <= EStatIndex.UNITSKILLED_PER_GENERAL_14:
					unitsKilled[index] = value;
					break;
				case >= EStatIndex.UNITSLOST_PER_GENERAL_0 and <= EStatIndex.UNITSLOST_PER_GENERAL_14:
					unitsLost[index] = value;
					break;
				case >= EStatIndex.UNITSBUILT_PER_GENERAL_0 and <= EStatIndex.UNITSBUILT_PER_GENERAL_14:
					unitsBuilt[index] = value;
					break;
				case >= EStatIndex.BUILDINGSKILLED_PER_GENERAL_0 and <= EStatIndex.BUILDINGSKILLED_PER_GENERAL_14:
					buildingsKilled[index] = value;
					break;
				case >= EStatIndex.BUILDINGSLOST_PER_GENERAL_0 and <= EStatIndex.BUILDINGSLOST_PER_GENERAL_14:
					buildingsLost[index] = value;
					break;
				case >= EStatIndex.BUILDINGSBUILT_PER_GENERAL_0 and <= EStatIndex.BUILDINGSBUILT_PER_GENERAL_14:
					buildingsBuilt[index] = value;
					break;
				case >= EStatIndex.EARNINGS_PER_GENERAL_0 and <= EStatIndex.EARNINGS_PER_GENERAL_14:
					earnings[index] = value;
					break;
				case >= EStatIndex.TECHCAPTURED_PER_GENERAL_0 and <= EStatIndex.TECHCAPTURED_PER_GENERAL_14:
					techCaptured[index] = value;
					break;
				case >= EStatIndex.DISCONS_PER_GENERAL_0 and <= EStatIndex.DISCONS_PER_GENERAL_14:
					discons[index] = value;
					break;
				case >= EStatIndex.DESYNCS_PER_GENERAL_0 and <= EStatIndex.DESYNCS_PER_GENERAL_14:
					desyncs[index] = value;
					break;
				case >= EStatIndex.SURRENDERS_PER_GENERAL_0 and <= EStatIndex.SURRENDERS_PER_GENERAL_14:
					surrenders[index] = value;
					break;
				case >= EStatIndex.GAMESOF2P_PER_GENERAL_0 and <= EStatIndex.GAMESOF2P_PER_GENERAL_14:
					gamesOf2p[index] = value;
					break;
				case >= EStatIndex.GAMESOF3P_PER_GENERAL_0 and <= EStatIndex.GAMESOF3P_PER_GENERAL_14:
					gamesOf3p[index] = value;
					break;
				case >= EStatIndex.GAMESOF4P_PER_GENERAL_0 and <= EStatIndex.GAMESOF4P_PER_GENERAL_14:
					gamesOf4p[index] = value;
					break;
				case >= EStatIndex.GAMESOF5P_PER_GENERAL_0 and <= EStatIndex.GAMESOF5P_PER_GENERAL_14:
					gamesOf5p[index] = value;
					break;
				case >= EStatIndex.GAMESOF6P_PER_GENERAL_0 and <= EStatIndex.GAMESOF6P_PER_GENERAL_14:
					gamesOf6p[index] = value;
					break;
				case >= EStatIndex.GAMESOF7P_PER_GENERAL_0 and <= EStatIndex.GAMESOF7P_PER_GENERAL_14:
					gamesOf7p[index] = value;
					break;
				case >= EStatIndex.GAMESOF8P_PER_GENERAL_0 and <= EStatIndex.GAMESOF8P_PER_GENERAL_14:
					gamesOf8p[index] = value;
					break;
				case >= EStatIndex.CUSTOMGAMES_PER_GENERAL_0 and <= EStatIndex.CUSTOMGAMES_PER_GENERAL_14:
					customGamesPerGeneral[index] = value;
					break;
				case >= EStatIndex.QUICKMATCHES_PER_GENERAL_0 and <= EStatIndex.QUICKMATCHES_PER_GENERAL_14:
					quickMatchesPerGeneral[index] = value;
					break;
				case EStatIndex.LOCALE:
					locale = value;
					break;
				case EStatIndex.GAMES_AS_RANDOM:
					gamesAsRandom = value;
					break;
				case EStatIndex.LASTFPS:
					lastFPS = value;
					break;
				case EStatIndex.LASTGENERAL:
					lastGeneral = value;
					break;
				case EStatIndex.GAMESINROWWITHLASTGENERAL:
					gamesInRowWithLastGeneral = value;
					break;
				case EStatIndex.CHALLENGEMEDALS:
					challengeMedals = value;
					break;
				case EStatIndex.BATTLEHONORS:
					battleHonors = value;
					break;
				case EStatIndex.QMWINSINAROW:
					QMwinsInARow = value;
					break;
				case EStatIndex.MAXQMWINSINAROW:
					maxQMwinsInARow = value;
					break;
				case EStatIndex.WINSINAROW:
					winsInARow = value;
					break;
				case EStatIndex.MAXWINSINAROW:
					maxWinsInARow = value;
					break;
				case EStatIndex.LOSSESINAROW:
					lossesInARow = value;
					break;
				case EStatIndex.MAXLOSSESINAROW:
					maxLossesInARow = value;
					break;
				case EStatIndex.DISCONSINAROW:
					disconsInARow = value;
					break;
				case EStatIndex.MAXDISCONSINAROW:
					maxDisconsInARow = value;
					break;
				case EStatIndex.DESYNCSINAROW:
					desyncsInARow = value;
					break;
				case EStatIndex.MAXDESYNCSINAROW:
					maxDesyncsInARow = value;
					break;
				case EStatIndex.BUILTPARTICLECANNON:
					builtParticleCannon = value;
					break;
				case EStatIndex.BUILTNUKE:
					builtNuke = value;
					break;
				case EStatIndex.BUILTSCUD:
					builtSCUD = value;
					break;
				case EStatIndex.LASTLADDERPORT:
					lastLadderPort = value;
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(stat_id), $"Unhandled stat_id: {stat_id}");
			}
		}
	}

	public enum EStatIndex : UInt16
	{
		WINS_PER_GENERAL_0,
		WINS_PER_GENERAL_1,
		WINS_PER_GENERAL_2,
		WINS_PER_GENERAL_3,
		WINS_PER_GENERAL_4,
		WINS_PER_GENERAL_5,
		WINS_PER_GENERAL_6,
		WINS_PER_GENERAL_7,
		WINS_PER_GENERAL_8,
		WINS_PER_GENERAL_9,
		WINS_PER_GENERAL_10,
		WINS_PER_GENERAL_11,
		WINS_PER_GENERAL_12,
		WINS_PER_GENERAL_13,
		WINS_PER_GENERAL_14,

		LOSSES_PER_GENERAL_0,
		LOSSES_PER_GENERAL_1,
		LOSSES_PER_GENERAL_2,
		LOSSES_PER_GENERAL_3,
		LOSSES_PER_GENERAL_4,
		LOSSES_PER_GENERAL_5,
		LOSSES_PER_GENERAL_6,
		LOSSES_PER_GENERAL_7,
		LOSSES_PER_GENERAL_8,
		LOSSES_PER_GENERAL_9,
		LOSSES_PER_GENERAL_10,
		LOSSES_PER_GENERAL_11,
		LOSSES_PER_GENERAL_12,
		LOSSES_PER_GENERAL_13,
		LOSSES_PER_GENERAL_14,

		GAMES_PER_GENERAL_0,
		GAMES_PER_GENERAL_1,
		GAMES_PER_GENERAL_2,
		GAMES_PER_GENERAL_3,
		GAMES_PER_GENERAL_4,
		GAMES_PER_GENERAL_5,
		GAMES_PER_GENERAL_6,
		GAMES_PER_GENERAL_7,
		GAMES_PER_GENERAL_8,
		GAMES_PER_GENERAL_9,
		GAMES_PER_GENERAL_10,
		GAMES_PER_GENERAL_11,
		GAMES_PER_GENERAL_12,
		GAMES_PER_GENERAL_13,
		GAMES_PER_GENERAL_14,

		DURATION_PER_GENERAL_0,
		DURATION_PER_GENERAL_1,
		DURATION_PER_GENERAL_2,
		DURATION_PER_GENERAL_3,
		DURATION_PER_GENERAL_4,
		DURATION_PER_GENERAL_5,
		DURATION_PER_GENERAL_6,
		DURATION_PER_GENERAL_7,
		DURATION_PER_GENERAL_8,
		DURATION_PER_GENERAL_9,
		DURATION_PER_GENERAL_10,
		DURATION_PER_GENERAL_11,
		DURATION_PER_GENERAL_12,
		DURATION_PER_GENERAL_13,
		DURATION_PER_GENERAL_14,

		UNITSKILLED_PER_GENERAL_0,
		UNITSKILLED_PER_GENERAL_1,
		UNITSKILLED_PER_GENERAL_2,
		UNITSKILLED_PER_GENERAL_3,
		UNITSKILLED_PER_GENERAL_4,
		UNITSKILLED_PER_GENERAL_5,
		UNITSKILLED_PER_GENERAL_6,
		UNITSKILLED_PER_GENERAL_7,
		UNITSKILLED_PER_GENERAL_8,
		UNITSKILLED_PER_GENERAL_9,
		UNITSKILLED_PER_GENERAL_10,
		UNITSKILLED_PER_GENERAL_11,
		UNITSKILLED_PER_GENERAL_12,
		UNITSKILLED_PER_GENERAL_13,
		UNITSKILLED_PER_GENERAL_14,

		UNITSLOST_PER_GENERAL_0,
		UNITSLOST_PER_GENERAL_1,
		UNITSLOST_PER_GENERAL_2,
		UNITSLOST_PER_GENERAL_3,
		UNITSLOST_PER_GENERAL_4,
		UNITSLOST_PER_GENERAL_5,
		UNITSLOST_PER_GENERAL_6,
		UNITSLOST_PER_GENERAL_7,
		UNITSLOST_PER_GENERAL_8,
		UNITSLOST_PER_GENERAL_9,
		UNITSLOST_PER_GENERAL_10,
		UNITSLOST_PER_GENERAL_11,
		UNITSLOST_PER_GENERAL_12,
		UNITSLOST_PER_GENERAL_13,
		UNITSLOST_PER_GENERAL_14,

		UNITSBUILT_PER_GENERAL_0,
		UNITSBUILT_PER_GENERAL_1,
		UNITSBUILT_PER_GENERAL_2,
		UNITSBUILT_PER_GENERAL_3,
		UNITSBUILT_PER_GENERAL_4,
		UNITSBUILT_PER_GENERAL_5,
		UNITSBUILT_PER_GENERAL_6,
		UNITSBUILT_PER_GENERAL_7,
		UNITSBUILT_PER_GENERAL_8,
		UNITSBUILT_PER_GENERAL_9,
		UNITSBUILT_PER_GENERAL_10,
		UNITSBUILT_PER_GENERAL_11,
		UNITSBUILT_PER_GENERAL_12,
		UNITSBUILT_PER_GENERAL_13,
		UNITSBUILT_PER_GENERAL_14,

		BUILDINGSKILLED_PER_GENERAL_0,
		BUILDINGSKILLED_PER_GENERAL_1,
		BUILDINGSKILLED_PER_GENERAL_2,
		BUILDINGSKILLED_PER_GENERAL_3,
		BUILDINGSKILLED_PER_GENERAL_4,
		BUILDINGSKILLED_PER_GENERAL_5,
		BUILDINGSKILLED_PER_GENERAL_6,
		BUILDINGSKILLED_PER_GENERAL_7,
		BUILDINGSKILLED_PER_GENERAL_8,
		BUILDINGSKILLED_PER_GENERAL_9,
		BUILDINGSKILLED_PER_GENERAL_10,
		BUILDINGSKILLED_PER_GENERAL_11,
		BUILDINGSKILLED_PER_GENERAL_12,
		BUILDINGSKILLED_PER_GENERAL_13,
		BUILDINGSKILLED_PER_GENERAL_14,

		BUILDINGSLOST_PER_GENERAL_0,
		BUILDINGSLOST_PER_GENERAL_1,
		BUILDINGSLOST_PER_GENERAL_2,
		BUILDINGSLOST_PER_GENERAL_3,
		BUILDINGSLOST_PER_GENERAL_4,
		BUILDINGSLOST_PER_GENERAL_5,
		BUILDINGSLOST_PER_GENERAL_6,
		BUILDINGSLOST_PER_GENERAL_7,
		BUILDINGSLOST_PER_GENERAL_8,
		BUILDINGSLOST_PER_GENERAL_9,
		BUILDINGSLOST_PER_GENERAL_10,
		BUILDINGSLOST_PER_GENERAL_11,
		BUILDINGSLOST_PER_GENERAL_12,
		BUILDINGSLOST_PER_GENERAL_13,
		BUILDINGSLOST_PER_GENERAL_14,

		BUILDINGSBUILT_PER_GENERAL_0,
		BUILDINGSBUILT_PER_GENERAL_1,
		BUILDINGSBUILT_PER_GENERAL_2,
		BUILDINGSBUILT_PER_GENERAL_3,
		BUILDINGSBUILT_PER_GENERAL_4,
		BUILDINGSBUILT_PER_GENERAL_5,
		BUILDINGSBUILT_PER_GENERAL_6,
		BUILDINGSBUILT_PER_GENERAL_7,
		BUILDINGSBUILT_PER_GENERAL_8,
		BUILDINGSBUILT_PER_GENERAL_9,
		BUILDINGSBUILT_PER_GENERAL_10,
		BUILDINGSBUILT_PER_GENERAL_11,
		BUILDINGSBUILT_PER_GENERAL_12,
		BUILDINGSBUILT_PER_GENERAL_13,
		BUILDINGSBUILT_PER_GENERAL_14,

		EARNINGS_PER_GENERAL_0,
		EARNINGS_PER_GENERAL_1,
		EARNINGS_PER_GENERAL_2,
		EARNINGS_PER_GENERAL_3,
		EARNINGS_PER_GENERAL_4,
		EARNINGS_PER_GENERAL_5,
		EARNINGS_PER_GENERAL_6,
		EARNINGS_PER_GENERAL_7,
		EARNINGS_PER_GENERAL_8,
		EARNINGS_PER_GENERAL_9,
		EARNINGS_PER_GENERAL_10,
		EARNINGS_PER_GENERAL_11,
		EARNINGS_PER_GENERAL_12,
		EARNINGS_PER_GENERAL_13,
		EARNINGS_PER_GENERAL_14,

		TECHCAPTURED_PER_GENERAL_0,
		TECHCAPTURED_PER_GENERAL_1,
		TECHCAPTURED_PER_GENERAL_2,
		TECHCAPTURED_PER_GENERAL_3,
		TECHCAPTURED_PER_GENERAL_4,
		TECHCAPTURED_PER_GENERAL_5,
		TECHCAPTURED_PER_GENERAL_6,
		TECHCAPTURED_PER_GENERAL_7,
		TECHCAPTURED_PER_GENERAL_8,
		TECHCAPTURED_PER_GENERAL_9,
		TECHCAPTURED_PER_GENERAL_10,
		TECHCAPTURED_PER_GENERAL_11,
		TECHCAPTURED_PER_GENERAL_12,
		TECHCAPTURED_PER_GENERAL_13,
		TECHCAPTURED_PER_GENERAL_14,

		DISCONS_PER_GENERAL_0,
		DISCONS_PER_GENERAL_1,
		DISCONS_PER_GENERAL_2,
		DISCONS_PER_GENERAL_3,
		DISCONS_PER_GENERAL_4,
		DISCONS_PER_GENERAL_5,
		DISCONS_PER_GENERAL_6,
		DISCONS_PER_GENERAL_7,
		DISCONS_PER_GENERAL_8,
		DISCONS_PER_GENERAL_9,
		DISCONS_PER_GENERAL_10,
		DISCONS_PER_GENERAL_11,
		DISCONS_PER_GENERAL_12,
		DISCONS_PER_GENERAL_13,
		DISCONS_PER_GENERAL_14,

		DESYNCS_PER_GENERAL_0,
		DESYNCS_PER_GENERAL_1,
		DESYNCS_PER_GENERAL_2,
		DESYNCS_PER_GENERAL_3,
		DESYNCS_PER_GENERAL_4,
		DESYNCS_PER_GENERAL_5,
		DESYNCS_PER_GENERAL_6,
		DESYNCS_PER_GENERAL_7,
		DESYNCS_PER_GENERAL_8,
		DESYNCS_PER_GENERAL_9,
		DESYNCS_PER_GENERAL_10,
		DESYNCS_PER_GENERAL_11,
		DESYNCS_PER_GENERAL_12,
		DESYNCS_PER_GENERAL_13,
		DESYNCS_PER_GENERAL_14,

		SURRENDERS_PER_GENERAL_0,
		SURRENDERS_PER_GENERAL_1,
		SURRENDERS_PER_GENERAL_2,
		SURRENDERS_PER_GENERAL_3,
		SURRENDERS_PER_GENERAL_4,
		SURRENDERS_PER_GENERAL_5,
		SURRENDERS_PER_GENERAL_6,
		SURRENDERS_PER_GENERAL_7,
		SURRENDERS_PER_GENERAL_8,
		SURRENDERS_PER_GENERAL_9,
		SURRENDERS_PER_GENERAL_10,
		SURRENDERS_PER_GENERAL_11,
		SURRENDERS_PER_GENERAL_12,
		SURRENDERS_PER_GENERAL_13,
		SURRENDERS_PER_GENERAL_14,

		GAMESOF2P_PER_GENERAL_0,
		GAMESOF2P_PER_GENERAL_1,
		GAMESOF2P_PER_GENERAL_2,
		GAMESOF2P_PER_GENERAL_3,
		GAMESOF2P_PER_GENERAL_4,
		GAMESOF2P_PER_GENERAL_5,
		GAMESOF2P_PER_GENERAL_6,
		GAMESOF2P_PER_GENERAL_7,
		GAMESOF2P_PER_GENERAL_8,
		GAMESOF2P_PER_GENERAL_9,
		GAMESOF2P_PER_GENERAL_10,
		GAMESOF2P_PER_GENERAL_11,
		GAMESOF2P_PER_GENERAL_12,
		GAMESOF2P_PER_GENERAL_13,
		GAMESOF2P_PER_GENERAL_14,

		GAMESOF3P_PER_GENERAL_0,
		GAMESOF3P_PER_GENERAL_1,
		GAMESOF3P_PER_GENERAL_2,
		GAMESOF3P_PER_GENERAL_3,
		GAMESOF3P_PER_GENERAL_4,
		GAMESOF3P_PER_GENERAL_5,
		GAMESOF3P_PER_GENERAL_6,
		GAMESOF3P_PER_GENERAL_7,
		GAMESOF3P_PER_GENERAL_8,
		GAMESOF3P_PER_GENERAL_9,
		GAMESOF3P_PER_GENERAL_10,
		GAMESOF3P_PER_GENERAL_11,
		GAMESOF3P_PER_GENERAL_12,
		GAMESOF3P_PER_GENERAL_13,
		GAMESOF3P_PER_GENERAL_14,

		GAMESOF4P_PER_GENERAL_0,
		GAMESOF4P_PER_GENERAL_1,
		GAMESOF4P_PER_GENERAL_2,
		GAMESOF4P_PER_GENERAL_3,
		GAMESOF4P_PER_GENERAL_4,
		GAMESOF4P_PER_GENERAL_5,
		GAMESOF4P_PER_GENERAL_6,
		GAMESOF4P_PER_GENERAL_7,
		GAMESOF4P_PER_GENERAL_8,
		GAMESOF4P_PER_GENERAL_9,
		GAMESOF4P_PER_GENERAL_10,
		GAMESOF4P_PER_GENERAL_11,
		GAMESOF4P_PER_GENERAL_12,
		GAMESOF4P_PER_GENERAL_13,
		GAMESOF4P_PER_GENERAL_14,

		GAMESOF5P_PER_GENERAL_0,
		GAMESOF5P_PER_GENERAL_1,
		GAMESOF5P_PER_GENERAL_2,
		GAMESOF5P_PER_GENERAL_3,
		GAMESOF5P_PER_GENERAL_4,
		GAMESOF5P_PER_GENERAL_5,
		GAMESOF5P_PER_GENERAL_6,
		GAMESOF5P_PER_GENERAL_7,
		GAMESOF5P_PER_GENERAL_8,
		GAMESOF5P_PER_GENERAL_9,
		GAMESOF5P_PER_GENERAL_10,
		GAMESOF5P_PER_GENERAL_11,
		GAMESOF5P_PER_GENERAL_12,
		GAMESOF5P_PER_GENERAL_13,
		GAMESOF5P_PER_GENERAL_14,

		GAMESOF6P_PER_GENERAL_0,
		GAMESOF6P_PER_GENERAL_1,
		GAMESOF6P_PER_GENERAL_2,
		GAMESOF6P_PER_GENERAL_3,
		GAMESOF6P_PER_GENERAL_4,
		GAMESOF6P_PER_GENERAL_5,
		GAMESOF6P_PER_GENERAL_6,
		GAMESOF6P_PER_GENERAL_7,
		GAMESOF6P_PER_GENERAL_8,
		GAMESOF6P_PER_GENERAL_9,
		GAMESOF6P_PER_GENERAL_10,
		GAMESOF6P_PER_GENERAL_11,
		GAMESOF6P_PER_GENERAL_12,
		GAMESOF6P_PER_GENERAL_13,
		GAMESOF6P_PER_GENERAL_14,

		GAMESOF7P_PER_GENERAL_0,
		GAMESOF7P_PER_GENERAL_1,
		GAMESOF7P_PER_GENERAL_2,
		GAMESOF7P_PER_GENERAL_3,
		GAMESOF7P_PER_GENERAL_4,
		GAMESOF7P_PER_GENERAL_5,
		GAMESOF7P_PER_GENERAL_6,
		GAMESOF7P_PER_GENERAL_7,
		GAMESOF7P_PER_GENERAL_8,
		GAMESOF7P_PER_GENERAL_9,
		GAMESOF7P_PER_GENERAL_10,
		GAMESOF7P_PER_GENERAL_11,
		GAMESOF7P_PER_GENERAL_12,
		GAMESOF7P_PER_GENERAL_13,
		GAMESOF7P_PER_GENERAL_14,

		GAMESOF8P_PER_GENERAL_0,
		GAMESOF8P_PER_GENERAL_1,
		GAMESOF8P_PER_GENERAL_2,
		GAMESOF8P_PER_GENERAL_3,
		GAMESOF8P_PER_GENERAL_4,
		GAMESOF8P_PER_GENERAL_5,
		GAMESOF8P_PER_GENERAL_6,
		GAMESOF8P_PER_GENERAL_7,
		GAMESOF8P_PER_GENERAL_8,
		GAMESOF8P_PER_GENERAL_9,
		GAMESOF8P_PER_GENERAL_10,
		GAMESOF8P_PER_GENERAL_11,
		GAMESOF8P_PER_GENERAL_12,
		GAMESOF8P_PER_GENERAL_13,
		GAMESOF8P_PER_GENERAL_14,

		CUSTOMGAMES_PER_GENERAL_0,
		CUSTOMGAMES_PER_GENERAL_1,
		CUSTOMGAMES_PER_GENERAL_2,
		CUSTOMGAMES_PER_GENERAL_3,
		CUSTOMGAMES_PER_GENERAL_4,
		CUSTOMGAMES_PER_GENERAL_5,
		CUSTOMGAMES_PER_GENERAL_6,
		CUSTOMGAMES_PER_GENERAL_7,
		CUSTOMGAMES_PER_GENERAL_8,
		CUSTOMGAMES_PER_GENERAL_9,
		CUSTOMGAMES_PER_GENERAL_10,
		CUSTOMGAMES_PER_GENERAL_11,
		CUSTOMGAMES_PER_GENERAL_12,
		CUSTOMGAMES_PER_GENERAL_13,
		CUSTOMGAMES_PER_GENERAL_14,

		QUICKMATCHES_PER_GENERAL_0,
		QUICKMATCHES_PER_GENERAL_1,
		QUICKMATCHES_PER_GENERAL_2,
		QUICKMATCHES_PER_GENERAL_3,
		QUICKMATCHES_PER_GENERAL_4,
		QUICKMATCHES_PER_GENERAL_5,
		QUICKMATCHES_PER_GENERAL_6,
		QUICKMATCHES_PER_GENERAL_7,
		QUICKMATCHES_PER_GENERAL_8,
		QUICKMATCHES_PER_GENERAL_9,
		QUICKMATCHES_PER_GENERAL_10,
		QUICKMATCHES_PER_GENERAL_11,
		QUICKMATCHES_PER_GENERAL_12,
		QUICKMATCHES_PER_GENERAL_13,
		QUICKMATCHES_PER_GENERAL_14,

		LOCALE,
		GAMES_AS_RANDOM,
		OPTIONS,
		SYSTEM_SPEC,
		LASTFPS,
		LASTGENERAL,
		GAMESINROWWITHLASTGENERAL,
		CHALLENGEMEDALS,
		BATTLEHONORS,
		QMWINSINAROW,
		MAXQMWINSINAROW,
		WINSINAROW,
		MAXWINSINAROW,
		LOSSESINAROW,
		MAXLOSSESINAROW,
		DISCONSINAROW,

		MAXDISCONSINAROW,
		DESYNCSINAROW,
		MAXDESYNCSINAROW,
		BUILTPARTICLECANNON,
		BUILTNUKE,
		BUILTSCUD,
		LASTLADDERPORT,
		LASTLADDERHOST
	}

	public class TURNCredentialContainer
	{
		public TURNCredentialContainer(string strUsername, string strToken)
		{
			m_strToken = strToken;
			m_strUsername = strUsername;
		}

		public string m_strUsername;
		public string m_strToken;
	}

	public class iceEntry
	{
		public List<string>? urls { get; set; } = null;
		public string? username { get; set; } = null;
		public string? credential { get; set; } = null;
	}

	public class TURNResponse
	{
		public List<iceEntry>? iceServers { get; set; }
	}

	public static class S3CredentialManager
	{
		private static readonly ILogger s_log = AppLog.For(typeof(S3CredentialManager));

		private static AmazonS3Client m_s3client = null;
		private static bool s_uploadsEnabled = false;

		public static void Initialize()
		{
			if (Program.g_Config == null)
			{
				throw new Exception("Config not loaded");
			}

			s_uploadsEnabled = Program.g_Config.GetSection("MatchData").GetValue<bool?>("upload_match_data") ?? false;

			// When uploads are disabled, the S3 settings are intentionally optional.
			if (!s_uploadsEnabled)
			{
				s_log.LogInformation("MatchData: upload_match_data is false, replay and screenshot uploads are disabled");
				return;
			}

			GetS3Config(out int TTL, out string access_key, out string secret_key, out string bucket_name, out string client_endpoint);

			var config = new AmazonS3Config
			{
				ServiceURL = client_endpoint,
				ForcePathStyle = true
			};

			m_s3client = new AmazonS3Client(access_key, secret_key, config);
		}

		private static void GetS3Config(out int TTL, out string access_key, out string secret_key, out string bucket_name, out string endpoint)
		{
			TTL = -1;
			access_key = String.Empty;
			secret_key = String.Empty;
			bucket_name = String.Empty;
			endpoint = String.Empty;

			if (Program.g_Config == null)
			{
				throw new Exception("Config not loaded");
			}

			IConfigurationSection? turnSettings = Program.g_Config.GetSection("MatchData");

			if (turnSettings == null)
			{
				throw new Exception("MatchData section missing in config");
			}

			string? s3_access_key = turnSettings.GetValue<string>("s3_access_key");
			string? s3_secret_key = turnSettings.GetValue<string>("s3_secret_key");
			string? s3_bucket_name = turnSettings.GetValue<string>("s3_bucket_name");
			string? s3_endpoint = turnSettings.GetValue<string>("s3_endpoint");
			int? s3_url_ttl_minutes = turnSettings.GetValue<int>("s3_url_ttl_minutes");

			if (s3_access_key == null)
			{
				throw new Exception("s3_access_key missing in config");
			}

			if (s3_secret_key == null)
			{
				throw new Exception("s3_secret_key missing in config");
			}

			if (s3_bucket_name == null)
			{
				throw new Exception("s3_bucket_name missing in config");
			}

			if (s3_endpoint == null)
			{
				throw new Exception("s3_endpoint missing in config");
			}

			if (s3_url_ttl_minutes == null)
			{
				throw new Exception("s3_url_ttl_minutes missing in config");
			}

			TTL = (int)s3_url_ttl_minutes;
			access_key = s3_access_key;
			secret_key = s3_secret_key;
			bucket_name = s3_bucket_name;
			endpoint = s3_endpoint;
		}

		public static async Task<string?> GetPresignedURL(EMetadataFileType fileType, EScreenshotType screenshotTypeIfScreenshot, UInt64 matchID, Int64 userID, int slotIndex, DateTime matchStartTime)
		{
			if (!s_uploadsEnabled || m_s3client == null)
			{
				return null;
			}

			GetS3Config(out int TTL, out string access_key, out string secret_key, out string bucket_name, out string client_endpoint);
			TimeSpan expiresIn = TimeSpan.FromMinutes(TTL);

			DateTime utcNow = DateTime.UtcNow;
			int hour = utcNow.Hour;
			int minute = utcNow.Minute;

			string strPerMatchUserIDKey = Helpers.ComputeMD5Hash(String.Format("{0}_{1}", matchID, userID));
			string strFileName = null;

			string strContentType = String.Empty;
			string strFolderPrefix = String.Empty; ;


			if (fileType == EMetadataFileType.FILE_TYPE_SCREENSHOT)
			{
				strContentType = "image/jpeg";
				fileType = EMetadataFileType.FILE_TYPE_SCREENSHOT;

				string screenshotTypePrefix = "screenshot";
				if (screenshotTypeIfScreenshot == EScreenshotType.SCREENSHOT_TYPE_LOADSCREEN)
				{
					screenshotTypePrefix = "loadscreen";
				}
				else if (screenshotTypeIfScreenshot == EScreenshotType.SCREENSHOT_TYPE_GAMEPLAY)
				{
					screenshotTypePrefix = "gameplay";

					string? strGameplayKey = Program.g_Config.GetSection("AC")?.GetValue<string>("sskey");
					if (strGameplayKey != null)
					{
						screenshotTypePrefix = strGameplayKey;
					}
				}
				else if (screenshotTypeIfScreenshot == EScreenshotType.SCREENSHOT_TYPE_SCORESCREEN)
				{
					screenshotTypePrefix = "scorescreen";
				}

				strFileName = String.Format("screenshot_{0}_{1}_{2}.jpg", screenshotTypePrefix, hour, minute);
				strFolderPrefix = "screenshots";
			}
			else if (fileType == EMetadataFileType.FILE_TYPE_REPLAY)
			{
				strContentType = "application/octet-stream";
				fileType = EMetadataFileType.FILE_TYPE_REPLAY;

				strFileName = String.Format("match_{0}_user_{1}_replay.rep", matchID, strPerMatchUserIDKey);
				strFolderPrefix = "replays";
			}

			if (strFileName == null)
			{
				return null;
			}

			string objectKey = String.Format("{3}/{4}/{5}/{6}/match_{0}/user_{1}/{2}", matchID, strPerMatchUserIDKey, strFileName, strFolderPrefix, matchStartTime.Year, matchStartTime.Month, matchStartTime.Day);

			var request = new GetPreSignedUrlRequest
			{
				BucketName = bucket_name,
				Key = objectKey,
				Verb = HttpVerb.PUT,
				Expires = DateTime.UtcNow.Add(expiresIn),
				ContentType = strContentType
			};

			// anytime we get an S3 uri, register the metadata
			using var scope = ServiceLocator.Services.CreateScope();
			var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
			await using var db = await factory.CreateDbContextAsync();

			await Database.MatchHistory.AttachMatchHistoryMetadata(db, matchID, slotIndex, strFileName, fileType);

			return await m_s3client.GetPreSignedURLAsync(request);
		}
	}


	public static class TURNCredentialManager
	{
		private static readonly ILogger s_log = AppLog.For(typeof(TURNCredentialManager));

		private static ConcurrentDictionary<Int64, string> g_DictTURNUsernames = new();

		private static void GetTURNConfig(out int TTL, out string token, out string key, out bool bShouldInvalidateTokensAutomatically)
		{
			TTL = -1;
			token = String.Empty;
			key = String.Empty;

			if (Program.g_Config == null)
			{
				throw new Exception("Config not loaded");
			}

			IConfigurationSection? turnSettings = Program.g_Config.GetSection("TURN");

			if (turnSettings == null)
			{
				throw new Exception("TURN section missing in config");
			}

			string? turn_key = turnSettings.GetValue<string>("key");
			string? turn_token = turnSettings.GetValue<string>("token");
			int? token_ttl = turnSettings.GetValue<int>("token_ttl");
			bool? automatic_token_invalidate = turnSettings.GetValue<bool>("automatic_token_invalidate");

			if (turn_key == null)
			{
				throw new Exception("turn_key missing in config");
			}

			if (turn_token == null)
			{
				throw new Exception("turn_token missing in config");
			}

			if (token_ttl == null)
			{
				throw new Exception("token_ttl missing in config");
			}

			if (automatic_token_invalidate == null)
			{
				throw new Exception("automatic_token_invalidate missing in config");
			}

			TTL = (int)token_ttl;
			token = turn_token;
			key = turn_key;
			bShouldInvalidateTokensAutomatically = (bool)automatic_token_invalidate;
		}

		public static async Task<TURNCredentialContainer?> CreateCredentialsForUser(Int64 userID)
		{
#if DEBUG
			TURNCredentialContainer fakeCreds = new("fake", "fake");
			await Task.Delay(1);
			return fakeCreds;
#endif
			GetTURNConfig(out int TurnTTL, out string TurnToken, out string TurnKey, out bool bShouldInvalidateTokensAutomatically);

			// we should only have 1 turn credential at a time... clean it up
			if (g_DictTURNUsernames.ContainsKey(userID))
			{
				await DeleteCredentialsForUser(userID);
			}

			// create new credential
			Dictionary<string, object> dictReqData = new();
			dictReqData.Add("ttl", TurnTTL); // 4 hours
			dictReqData.Add("go_user_id", userID); // go user id
			var jsonContent = JsonSerializer.Serialize(dictReqData);
			using var requestContent = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

			using (HttpClient client = new HttpClient(new SocketsHttpHandler()
			{
				ConnectCallback = async (context, cancellationToken) =>
				{
					// Use DNS to look up the IP addresses of the target host:
					// - IP v4: AddressFamily.InterNetwork
					// - IP v6: AddressFamily.InterNetworkV6
					// - IP v4 or IP v6: AddressFamily.Unspecified
					// note: this method throws a SocketException when there is no IP address for the host
					var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);

					// Open the connection to the target host/port
					var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

					// Turn off Nagle's algorithm since it degrades performance in most HttpClient scenarios.
					socket.NoDelay = true;

					try
					{
						await socket.ConnectAsync(entry.AddressList, context.DnsEndPoint.Port, cancellationToken);

						// If you want to choose a specific IP address to connect to the server
						// await socket.ConnectAsync(
						//    entry.AddressList[Random.Shared.Next(0, entry.AddressList.Length)],
						//    context.DnsEndPoint.Port, cancellationToken);

						// Return the NetworkStream to the caller
						return new NetworkStream(socket, ownsSocket: true);
					}
					catch
					{
						socket.Dispose();
						throw;
					}
				}
			}))
			{
				client.Timeout = TimeSpan.FromSeconds(10);
				client.DefaultRequestHeaders.Add("Authorization", String.Format("Bearer {0}", TurnToken));
				client.DefaultRequestHeaders.Add("Accept", "application/json");
				//client.DefaultRequestHeaders.Add("Content-Type", "application/json");
				try
				{
					var sw = Stopwatch.StartNew();
					string strURI = String.Format("https://rtc.live.cloudflare.com/v1/turn/keys/{0}/credentials/generate-ice-servers", TurnKey);
					HttpResponseMessage response = await client.PostAsync(strURI, requestContent);

					if (response.IsSuccessStatusCode)
					{
						s_log.LogDebug("TURN credential request for user {UserId} took {ElapsedMs}ms", userID, sw.ElapsedMilliseconds);
						string responseBody = await response.Content.ReadAsStringAsync();
						TURNResponse? resp = JsonSerializer.Deserialize<TURNResponse>(responseBody);

						try
						{
							if (resp != null && resp.iceServers != null)
							{
								foreach (iceEntry? entry in resp.iceServers)
								{
									if (!string.IsNullOrEmpty(entry.username) && !string.IsNullOrEmpty(entry.credential))
									{
										TURNCredentialContainer creds = new(entry.username, entry.credential);
										g_DictTURNUsernames[userID] = entry.username;
										return creds;
									}
								}
							}

							return null;
						}
						catch
						{

						}

						return null;
					}
				}
				catch
				{

				}
			}

			return null;
		}

		public static async Task DeleteCredentialsForUser(Int64 userID)
		{
#if DEBUG
            await Task.Delay(1);
            return;
#endif

            GetTURNConfig(out int TurnTTL, out string TurnToken, out string TurnKey, out bool bShouldInvalidateTokensAutomatically);

			if (!bShouldInvalidateTokensAutomatically)
			{
				return;
			}

			if (g_DictTURNUsernames.ContainsKey(userID))
			{
				string strTURNUsername = g_DictTURNUsernames[userID];

				g_DictTURNUsernames.Remove(userID, out string? strUsername);

				// revoke credential
				Dictionary<string, object> dictReqData = new();
				var jsonContent = JsonSerializer.Serialize(dictReqData);
				using var requestContent = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

				using (HttpClient client = new HttpClient(new SocketsHttpHandler()
				{
					ConnectCallback = async (context, cancellationToken) =>
					{
						// Use DNS to look up the IP addresses of the target host:
						// - IP v4: AddressFamily.InterNetwork
						// - IP v6: AddressFamily.InterNetworkV6
						// - IP v4 or IP v6: AddressFamily.Unspecified
						// note: this method throws a SocketException when there is no IP address for the host
						var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);

						// Open the connection to the target host/port
						var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

						// Turn off Nagle's algorithm since it degrades performance in most HttpClient scenarios.
						socket.NoDelay = true;

						try
						{
							await socket.ConnectAsync(entry.AddressList, context.DnsEndPoint.Port, cancellationToken);

							// If you want to choose a specific IP address to connect to the server
							// await socket.ConnectAsync(
							//    entry.AddressList[Random.Shared.Next(0, entry.AddressList.Length)],
							//    context.DnsEndPoint.Port, cancellationToken);

							// Return the NetworkStream to the caller
							return new NetworkStream(socket, ownsSocket: true);
						}
						catch
						{
							socket.Dispose();
							throw;
						}
					}
				}))
				{
					client.Timeout = TimeSpan.FromSeconds(10);
					client.DefaultRequestHeaders.Add("Authorization", String.Format("Bearer {0}", TurnToken));
					client.DefaultRequestHeaders.Add("Accept", "application/json");
					try
					{
						string strURI = String.Format("https://rtc.live.cloudflare.com/v1/turn/keys/{0}/credentials/{1}/revoke", TurnKey, strTURNUsername);
						using HttpResponseMessage response = await client.PostAsync(strURI, requestContent);
						response.EnsureSuccessStatusCode();
					}
					catch
					{

					}
				}
			}
		}
	}

	public enum EPlayerType
	{
		SLOT_OPEN,
		SLOT_CLOSED,
		SLOT_EASY_AI,
		SLOT_MED_AI,
		SLOT_BRUTAL_AI,
		SLOT_PLAYER
	}

	public static class UserPresence
	{
		public enum EPresencePriority
		{
			Highest = 2,
			Middle = 1,
			Lowest = 0
		}

		public static string DetermineUserStatusFromAllSessions(Int64 user_id, out bool IsOnline)
		{
			List<UserSession> lstUserSessions = WebSocketManager.GetAllDataFromUser(user_id);
			IsOnline = false;

			string strOverallPresence = "Offline";
			UserPresence.EPresencePriority overallPriority = UserPresence.EPresencePriority.Lowest;

			foreach (UserSession userData in lstUserSessions)
			{
				string strThisPresence = "Offline";
				EPresencePriority thisPriority = EPresencePriority.Lowest;

				if (userData == null)
				{
					thisPriority = EPresencePriority.Lowest;
					strThisPresence = "Offline";
				}

				IsOnline = true;

				if (userData.currentLobbyID == -1)
				{
					thisPriority = EPresencePriority.Middle;
					strThisPresence = "In Server List / Chat Room";
				}
				else
				{
					var lobbyManager = ServiceLocator.Services.GetRequiredService<LobbyManager>();
					Lobby? plrLobby = lobbyManager.GetLobby(userData.currentLobbyID);

					thisPriority = EPresencePriority.Highest;

					if (plrLobby == null)
					{
						strThisPresence = "In A Lobby";
					}
					else
					{
						if (plrLobby.State == ELobbyState.GAME_SETUP)
						{
							strThisPresence = String.Format("In lobby '{0}' - Waiting on game setup", plrLobby.Name);
						}
						else if (plrLobby.State == ELobbyState.COUNTDOWN)
						{
							strThisPresence = String.Format("In lobby '{0}' - Match Starting", plrLobby.Name);
						}
						else if (plrLobby.State == ELobbyState.INGAME)
						{
							strThisPresence = String.Format("In lobby '{0}' -  Match In Progress", plrLobby.Name);
						}
						else if (plrLobby.State == ELobbyState.COMPLETE)
						{
							strThisPresence = String.Format("In lobby '{0}' - Game Just Finished", plrLobby.Name);
						}
						else
						{
							strThisPresence = String.Format("In lobby '{0}'", plrLobby.Name);
						}
					}
				}

				// higher than our current priority?
				if (thisPriority > overallPriority)
				{
					strOverallPresence = strThisPresence;
					overallPriority = thisPriority;
				}
			}

			return strOverallPresence;
		}
	}
}
