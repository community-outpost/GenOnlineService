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

using Microsoft.AspNetCore.SignalR;
using System.Reflection;

namespace GenOnlineService.Realtime
{
	// Group names from section 8 of the protocol
	public static class RealtimeGroups
	{
		public const string LobbyList = "lobby-list";

		// Every connection of the user, used where one connection must be left out
		public static string User(Int64 userID) => $"user:{userID}";
		public static string UserGame(Int64 userID) => $"user:{userID}:game";

		// The one connection of a user and session type, a newer connection supersedes the older one
		public static string UserSession(Int64 userID, EUserSessionType sessionType) => sessionType switch
		{
			EUserSessionType.GameLauncher => $"user:{userID}:launcher",
			EUserSessionType.ChatClient => $"user:{userID}:chat",
			_ => UserGame(userID),
		};

		public static string Channel(Int64 channelID) => $"channel:{channelID}";
		public static string Lobby(Int64 lobbyID) => $"lobby:{lobbyID}";
	}

	// Sends server events from anywhere in the service, only through SignalR users and groups so a backplane carries them to every copy
	public static class RealtimeOut
	{
		private static readonly ILogger s_log = AppLog.For(typeof(RealtimeOut));
		private static readonly IRealtimeClient s_nullClient = NullRealtimeClient.Create();
		private static IHubContext<RealtimeHub, IRealtimeClient>? s_hub;

		public static void Initialize(IHubContext<RealtimeHub, IRealtimeClient> hub)
		{
			s_hub = hub;
		}

		public static IRealtimeClient All => s_hub?.Clients.All ?? s_nullClient;
		public static IRealtimeClient Group(string group) => s_hub?.Clients.Group(group) ?? s_nullClient;
		public static IRealtimeClient Groups(IReadOnlyList<string> groups) => groups.Count == 0 ? s_nullClient : s_hub?.Clients.Groups(groups) ?? s_nullClient;

		// The excluded connection ids come from this copy's sessions, so the exclusion holds only for connections on this copy
		public static IRealtimeClient GroupExcept(string group, IReadOnlyList<string> excludedConnectionIDs) => s_hub?.Clients.GroupExcept(group, excludedConnectionIDs) ?? s_nullClient;

		// Every connection of the user on any copy through the user group, which a connection joins just before Ready so Ready stays the first event
		public static IRealtimeClient User(Int64 userID) => Group(RealtimeGroups.User(userID));
		public static IRealtimeClient Users(IEnumerable<Int64> userIDs) => Groups(userIDs.Distinct().Select(RealtimeGroups.User).ToArray());
		public static IRealtimeClient UserGame(Int64 userID) => Group(RealtimeGroups.UserGame(userID));
		public static IRealtimeClient Lobby(Int64 lobbyID) => Group(RealtimeGroups.Lobby(lobbyID));

		public static Task AddToGroupAsync(string connectionID, string group) => s_hub?.Groups.AddToGroupAsync(connectionID, group) ?? Task.CompletedTask;
		public static Task RemoveFromGroupAsync(string connectionID, string group) => s_hub?.Groups.RemoveFromGroupAsync(connectionID, group) ?? Task.CompletedTask;

		// Lets synchronous game code send without awaiting, a failed send is only logged
		public static void Fire(Task send)
		{
			if (send.IsCompletedSuccessfully)
			{
				return;
			}

			send.ContinueWith(t => s_log.LogWarning(t.Exception, "Realtime send failed"), TaskContinuationOptions.OnlyOnFaulted);
		}

		// Sends to one session's connection through its user and session type group, nothing when it is disconnected
		public static IRealtimeClient Session(UserSession? session)
		{
			// the lookup only decides whether the session still owns a connection, delivery goes through the group
			if (session == null || WebSocketManager.GetWebSocketForSession(session) == null)
			{
				return s_nullClient;
			}
			return Group(RealtimeGroups.UserSession(session.m_UserID, session.GetSessionType()));
		}
	}

	// Names SignalR users by the user id the session principal carries in ClaimTypes.NameIdentifier, the OpenIddict subject
	public sealed class RealtimeUserIdProvider : IUserIdProvider
	{
		public static string Format(Int64 userID) => userID.ToString(System.Globalization.CultureInfo.InvariantCulture);

		public string? GetUserId(HubConnectionContext connection)
		{
			Int64 userID = RealtimeClaims.UserID(connection.User);
			return userID > 0 ? Format(userID) : null;
		}
	}

	// Stands in for the hub before startup and for sessions without a connection
	public class NullRealtimeClient : DispatchProxy
	{
		public static IRealtimeClient Create() => DispatchProxy.Create<IRealtimeClient, NullRealtimeClient>();

		protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Task.CompletedTask;
	}

	// Builds the Member objects other users see
	public static class RealtimeMembers
	{
		public static ClientType ClientOf(EUserSessionType sessionType) => sessionType switch
		{
			EUserSessionType.GameLauncher => ClientType.Launcher,
			EUserSessionType.ChatClient => ClientType.Chat,
			_ => ClientType.Game,
		};

		public static string NameOf(Int64 userID) => WebSocketManager.GetSharedDataForUser(userID)?.m_strDisplayName ?? String.Empty;

		// The tag of an online user; 0 when offline or not known
		public static int TagOf(Int64 userID) => WebSocketManager.GetSharedDataForUser(userID)?.m_NameTag ?? 0;

		// displayName and nameTag come from the database for users who may be offline; otherwise the online data is used
		public static Member Of(Int64 userID, string? displayName = null, ClientType? client = null, int? nameTag = null)
		{
			int tag = nameTag ?? (displayName == null ? TagOf(userID) : 0);
			return new Member
			{
				Id = userID,
				Name = displayName ?? NameOf(userID),
				Tag = tag > 0 ? Database.DisplayNames.FormatTag(tag) : null,
				Badges = RealtimeBadges.For(userID),
				Client = client
			};
		}

		// A name looked up with RealtimeHub.NamesAsync; users not in it fall back to the online data
		public static Member Of(Int64 userID, IReadOnlyDictionary<long, (string Name, int Tag)> names) =>
			names.TryGetValue(userID, out (string Name, int Tag) stored) ? Of(userID, stored.Name, null, stored.Tag) : Of(userID);

		public static Member Of(UserSession session) => Of(session.m_UserID, null, ClientOf(session.GetSessionType()));
	}

	// Badge definitions come from Realtime:badges in configuration, each staff role shows the badge of the same id
	public static class RealtimeBadges
	{
		private static readonly Dictionary<string, BadgeDefinition> s_defaults = new(StringComparer.Ordinal)
		{
			[StaffRoles.Admin] = new BadgeDefinition { Text = "★★GO STAFF★★", Color = "#f5c542" },
			[StaffRoles.Moderator] = new BadgeDefinition { Text = "MOD", Color = "#4aa3ff" },
		};

		// Highest priority first
		private static readonly string[] s_priority = { StaffRoles.Admin, StaffRoles.Moderator };

		public static IReadOnlyDictionary<string, BadgeDefinition> Definitions
		{
			get
			{
				IConfigurationSection? section = Program.g_Config?.GetSection("Realtime:badges");
				if (section == null || !section.GetChildren().Any())
				{
					return s_defaults;
				}

				Dictionary<string, BadgeDefinition> result = new(StringComparer.Ordinal);
				foreach (IConfigurationSection badge in section.GetChildren())
				{
					result[badge.Key] = new BadgeDefinition { Text = badge["text"] ?? badge.Key, Color = badge["color"] ?? "#ffffff" };
				}
				return result;
			}
		}

		public static IReadOnlyList<string> For(Int64 userID)
		{
			IReadOnlyCollection<string> roles = StaffRoles.RolesOf(userID);
			if (roles.Count == 0)
			{
				return Array.Empty<string>();
			}

			IReadOnlyDictionary<string, BadgeDefinition> definitions = Definitions;
			return s_priority.Where(r => roles.Contains(r) && definitions.ContainsKey(r)).ToArray();
		}
	}
}
