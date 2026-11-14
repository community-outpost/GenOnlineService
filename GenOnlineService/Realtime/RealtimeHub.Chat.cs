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

using System.Globalization;
using System.Text.RegularExpressions;

namespace GenOnlineService.Realtime
{
	// Rooms, chat and display names (section 5.1 and 6.2)
	public sealed partial class RealtimeHub
	{
		private const int StoredNameMaxLength = 32;

		// Words only staff may use in a display name, badge ids and badge texts are added at check time
		private static readonly string[] s_reservedNameWords =
		{
			"admin", "staff", "mass^", "mas^", "m4ss^", "m4s^", "moderator",
			"hitler", "h1tler", "h1tl3r", "hittler", "h1ttler", "h1ttl3r",
			"olda", "oldanalytics", "ibra", "x64", "ronin"
		};

		// Lobbies add (N) to tell duplicate names apart, so a name never ends with one
		private static readonly Regex s_dedupeSuffix = new(@"\((1[0-9]|20|[0-9])\)$", RegexOptions.Compiled);

		private static long s_lastMessageID;

		public async Task<Result<Empty>> JoinRoom(JoinRoomRequest request)
		{
			if (request.RoomId < 0 || request.RoomId > Int16.MaxValue)
			{
				return Result.Fail(ErrorCodes.RoomUnavailable);
			}

			UserSession session = Session;
			Int16 oldRoom = session.networkRoomID;
			if (!session.TryUpdateSessionNetworkRoom((Int16)request.RoomId))
			{
				return Result.Fail(ErrorCodes.RoomUnavailable);
			}

			await ChangeRoomAsync(session, oldRoom, session.networkRoomID);
			return Result.Success();
		}

		public async Task<Result<Empty>> LeaveRoom(Empty request)
		{
			UserSession session = Session;
			Int16 oldRoom = session.networkRoomID;
			session.ClearSessionNetworkRoom();
			await ChangeRoomAsync(session, oldRoom, -1);
			return Result.Success();
		}

		public async Task<Result<SendMessageData>> SendMessage(SendMessageRequest request)
		{
			string content = request.Content ?? String.Empty;
			if (content.Length == 0)
			{
				return Result.Invalid("content");
			}
			if (content.Length > ChatMaxLength)
			{
				return Result.Fail(ErrorCodes.ChatTooLong, new { Max = ChatMaxLength });
			}

			UserSession session = Session;
			Int64 author = UserID;
			SharedUserData? authorData = WebSocketManager.GetSharedDataForUser(author);
			if (authorData == null)
			{
				return Result.NotFound("user");
			}

			if (!TryParseChannel(request.ChannelId, out string kind, out Int64 targetID))
			{
				return Result.Invalid("channel_id");
			}

			if (request.Type == MessageType.Announcement && kind != "lobby")
			{
				return Result.Invalid("type");
			}

			// the audience is resolved before any limit is spent
			Func<MessageCreateEvent, Task> deliver;
			switch (kind)
			{
				case "room":
				{
					Int16 roomID = session.networkRoomID;
					if (roomID < 0 || targetID != roomID)
					{
						return Result.NotFound("channel");
					}

					List<string> excluded = BlockedConnections(author, RealtimeAudience.AllSessions().Where(s => s.networkRoomID == roomID));
					deliver = message => RealtimeOut.GroupExcept(RealtimeGroups.Room(roomID), excluded).MessageCreate(message);
					break;
				}
				case "lobby":
				{
					Lobby? lobby = session.currentLobbyID == targetID ? m_lobbyManager.GetLobby(targetID) : null;
					if (lobby == null)
					{
						return Result.NotFound("channel");
					}
					if (request.Type == MessageType.Announcement)
					{
						if (lobby.Owner != author)
						{
							return Result.Fail(ErrorCodes.LobbyNotHost);
						}
					}

					List<UserSession> memberSessions = new();
					foreach (LobbyMember? member in lobby.Members.ToArray())
					{
						UserSession? memberSession = member != null && member.IsHuman() ? WebSocketManager.GetSessionFromUser(member.UserID, EUserSessionType.GameClient) : null;
						if (memberSession != null)
						{
							memberSessions.Add(memberSession);
						}
					}

					List<string> excluded = BlockedConnections(author, memberSessions);
					deliver = message => RealtimeOut.GroupExcept(RealtimeGroups.Lobby(targetID), excluded).MessageCreate(message);
					break;
				}
				default:
				{
					if (targetID == author)
					{
						return Result.Invalid("channel_id");
					}
					if (!SocialState.HasFriend(author, targetID) || RealtimeAudience.BlockedEitherWay(author, targetID))
					{
						return Result.Fail(ErrorCodes.ChatNotFriends);
					}
					if (WebSocketManager.GetSharedDataForUser(targetID) == null)
					{
						return Result.Fail(ErrorCodes.ChatRecipientOffline);
					}
					if (!SocialState.HasFriend(targetID, author))
					{
						return Result.Fail(ErrorCodes.ChatNotFriends);
					}

					Int64 recipient = targetID;
					deliver = message =>
					{
						List<string> connections = RealtimeAudience.ConnectionsOf(author);
						connections.AddRange(RealtimeAudience.ConnectionsOf(recipient));
						return RealtimeOut.Connections(connections.Distinct().ToArray()).MessageCreate(message);
					};
					break;
				}
			}

			Ban? mute;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				mute = await Database.Moderation.GetActiveChatMute(db, author);
			}
			if (mute != null)
			{
				return mute.ExpiresAt == null ? Result.Fail(ErrorCodes.ChatMuted) : Result.Fail(ErrorCodes.ChatMuted, new { Until = mute.ExpiresAt.Value });
			}

			// only the public room chat is rate limited, lobby hosts can kick and direct messages go to friends
			if (kind == "room" && !ChatRateLimiter.TryConsume(authorData, out long retryAfterMs))
			{
				return Result.Fail(ErrorCodes.ChatRateLimited, new { RetryAfterMs = retryAfterMs });
			}

			string messageID = Interlocked.Increment(ref s_lastMessageID).ToString(CultureInfo.InvariantCulture);
			MessageCreateEvent message = new MessageCreateEvent
			{
				Id = messageID,
				ChannelId = request.ChannelId,
				Type = request.Type,
				Author = RealtimeMembers.Of(author, authorData.m_strDisplayName, null, authorData.m_NameTag),
				Content = content
			};
			await deliver(message);

			if (kind == "room" && Program.g_Discord != null)
			{
				RealtimeOut.Fire(Program.g_Discord.SendNetworkRoomChat((int)targetID, author, authorData.m_strDisplayName, content));
			}

			return Result.Success(new SendMessageData { MessageId = messageID });
		}

		// The rules every chosen name follows (the Keycloak plugin applies the same ones); reserved words are checked apart
		private static Failure? CheckNameRules(string name)
		{
			// '#' separates the name from its tag
			if (name.Contains('#') || name.Any(Char.IsControl) || !String.Equals(TextSanitizer.Clean(name, StoredNameMaxLength), name, StringComparison.Ordinal))
			{
				return Result.Fail(ErrorCodes.NameInvalidCharacters);
			}
			if (name.Length < NameMinLength || name.Length > NameMaxLength)
			{
				return Result.Fail(ErrorCodes.NameLength, new { Min = NameMinLength, Max = NameMaxLength });
			}
			if (Char.IsWhiteSpace(name[0]) || Char.IsWhiteSpace(name[^1]))
			{
				return Result.Fail(ErrorCodes.NameWhitespace);
			}
			if (s_dedupeSuffix.IsMatch(name))
			{
				return Result.Fail(ErrorCodes.NameInvalidCharacters);
			}
			return null;
		}

		public async Task<Result<Empty>> SetDisplayName(SetDisplayNameRequest request)
		{
			// Where Keycloak owns names, players rename there (kc_action=UPDATE_PROFILE) and the change arrives by copy
			if (m_accounts.NamesFromUpstream)
			{
				return Result.Fail(ErrorCodes.NameManaged);
			}

			string name = request.Name ?? String.Empty;
			Failure? invalid = CheckNameRules(name);
			if (invalid != null)
			{
				return invalid.Value;
			}

			Int64 userID = UserID;
			if (!StaffRoles.HasPermission(userID, Permissions.NamesProtected))
			{
				string? word = ReservedWordIn(name);
				if (word != null)
				{
					return Result.Fail(ErrorCodes.NameReserved, new { Word = word });
				}
			}

			SharedUserData? userData = WebSocketManager.GetSharedDataForUser(userID);
			if (userData == null)
			{
				return Result.NotFound("user");
			}

			NameChange? change;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				change = await Database.Users.SetDisplayName(db, userID, name);
			}
			if (change == null)
			{
				// every tag of the name is in use
				return Result.Fail(ErrorCodes.NameTaken);
			}

			if (change.OldName != change.NewName || change.OldTag != change.NewTag)
			{
				await RealtimeNames.PublishOne(change);
			}
			return Result.Success();
		}

		private partial async Task RestoreRoomAsync(UserSession session)
		{
			Int16 roomID = session.networkRoomID;
			if (roomID < 0)
			{
				return;
			}

			string channel = RealtimeGroups.Room(roomID);
			await Clients.Group(channel).MemberAdd(new MemberAddEvent { ChannelId = channel, Member = RealtimeMembers.Of(session) });
			await Groups.AddToGroupAsync(Context.ConnectionId, channel);
			await Clients.Caller.RoomMembers(BuildRoomMembers(roomID));
		}

		// A dropped connection leaves its room for the others, the session keeps the room for RestoreRoomAsync
		private static async Task LeaveRoomOnCloseAsync(UserSession session)
		{
			// a newer connection of the same session took over and stays in the room
			if (WebSocketManager.GetWebSocketForSession(session) != null)
			{
				return;
			}

			Int16 roomID = session.networkRoomID;
			if (roomID >= 0)
			{
				await AnnounceRoomLeaveAsync(session, roomID);
			}
		}

		private async Task ChangeRoomAsync(UserSession session, Int16 oldRoom, Int16 newRoom)
		{
			if (oldRoom != newRoom)
			{
				if (oldRoom >= 0)
				{
					await Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeGroups.Room(oldRoom));
					await AnnounceRoomLeaveAsync(session, oldRoom);
				}

				if (newRoom >= 0)
				{
					string channel = RealtimeGroups.Room(newRoom);
					await Clients.Group(channel).MemberAdd(new MemberAddEvent { ChannelId = channel, Member = RealtimeMembers.Of(session) });
					await Groups.AddToGroupAsync(Context.ConnectionId, channel);
				}

				PublishPresence(session.m_UserID);
			}

			if (newRoom >= 0)
			{
				await Clients.Caller.RoomMembers(BuildRoomMembers(newRoom));
			}
		}

		// MemberRemove carries only the user id, so the user's sessions still in the room are added again
		private static async Task AnnounceRoomLeaveAsync(UserSession leaving, Int16 roomID)
		{
			string channel = RealtimeGroups.Room(roomID);
			IRealtimeClient room = RealtimeOut.Group(channel);
			await room.MemberRemove(new MemberRemoveEvent { ChannelId = channel, UserId = leaving.m_UserID });

			foreach (UserSession remaining in WebSocketManager.GetAllDataFromUser(leaving.m_UserID))
			{
				if (!ReferenceEquals(remaining, leaving) && remaining.networkRoomID == roomID && RealtimeAudience.ConnectionOf(remaining) != null)
				{
					await room.MemberAdd(new MemberAddEvent { ChannelId = channel, Member = RealtimeMembers.Of(remaining) });
				}
			}
		}

		private static RoomMembersEvent BuildRoomMembers(Int16 roomID)
		{
			List<Member> members = new();
			foreach (UserSession session in RealtimeAudience.AllSessions())
			{
				if (session.networkRoomID == roomID && RealtimeAudience.ConnectionOf(session) != null)
				{
					members.Add(RealtimeMembers.Of(session));
				}
			}

			return new RoomMembersEvent { RoomId = roomID, Members = members };
		}

		// Connections of the given sessions whose user blocked the author or was blocked by them
		private static List<string> BlockedConnections(Int64 author, IEnumerable<UserSession> sessions)
		{
			List<string> excluded = new();
			foreach (UserSession session in sessions)
			{
				if (session.m_UserID == author || !RealtimeAudience.BlockedEitherWay(author, session.m_UserID))
				{
					continue;
				}

				string? connectionID = RealtimeAudience.ConnectionOf(session);
				if (connectionID != null)
				{
					excluded.Add(connectionID);
				}
			}
			return excluded;
		}

		private static string? ReservedWordIn(string name)
		{
			string lower = name.ToLowerInvariant();
			IEnumerable<string> badgeWords = RealtimeBadges.Definitions.SelectMany(b => new[] { b.Key, b.Value.Text });
			foreach (string word in s_reservedNameWords.Concat(badgeWords))
			{
				if (!String.IsNullOrWhiteSpace(word) && lower.Contains(word.ToLowerInvariant(), StringComparison.Ordinal))
				{
					return word;
				}
			}
			return null;
		}

		private static bool TryParseChannel(string? channelID, out string kind, out Int64 id)
		{
			kind = String.Empty;
			id = -1;
			int separator = channelID?.IndexOf(':') ?? -1;
			if (channelID == null || separator <= 0)
			{
				return false;
			}

			kind = channelID.Substring(0, separator);
			return (kind == "room" || kind == "lobby" || kind == "dm")
				&& Int64.TryParse(channelID.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out id);
		}
	}
}
