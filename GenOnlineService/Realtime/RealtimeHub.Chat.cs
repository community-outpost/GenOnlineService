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

using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace GenOnlineService.Realtime
{
	// Channels and chat (section 5.1 and 6.2)
	public sealed partial class RealtimeHub
	{
		public async Task<Result<Empty>> JoinChannel(JoinChannelRequest request)
		{
			if (request.ChannelId < 0 || request.ChannelId > Int16.MaxValue)
			{
				return Result.Fail(ErrorCodes.ChannelUnavailable);
			}

			UserSession session = Session;
			Int16 oldChannel = session.networkRoomID;
			if (!session.TryUpdateSessionNetworkRoom((Int16)request.ChannelId))
			{
				return Result.Fail(ErrorCodes.ChannelUnavailable);
			}

			await ChangeChannelAsync(session, oldChannel, session.networkRoomID);
			return Result.Success();
		}

		public async Task<Result<Empty>> LeaveChannel(Empty request)
		{
			UserSession session = Session;
			Int16 oldChannel = session.networkRoomID;
			session.ClearSessionNetworkRoom();
			await ChangeChannelAsync(session, oldChannel, -1);
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
			string storedType = StoredType(request.Type);
			Func<AppDbContext, Task<ChatMessage>> store;
			Func<Message, Task> deliver;
			Int64? staffRecipient = null;
			switch (kind)
			{
				case "channel":
				{
					Int16 channelID = session.networkRoomID;
					if (channelID < 0 || targetID != channelID)
					{
						return Result.NotFound("channel");
					}

					List<string> excluded = BlockedConnections(author, RealtimeAudience.AllSessions().Where(s => s.networkRoomID == channelID));
					store = db => Database.Chat.InsertChannelMessage(db, channelID, author, storedType, content);
					deliver = message => RealtimeOut.GroupExcept(RealtimeGroups.Channel(channelID), excluded).MessageCreate(message);
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
					List<Int64> memberIDs = new();
					foreach (LobbyMember? member in lobby.Members.ToArray())
					{
						if (member != null && member.IsHuman())
						{
							memberIDs.Add(member.UserID);
						}
						UserSession? memberSession = member != null && member.IsHuman() ? WebSocketManager.GetSessionFromUser(member.UserID, EUserSessionType.GameClient) : null;
						if (memberSession != null)
						{
							memberSessions.Add(memberSession);
						}
					}

					List<string> excluded = BlockedConnections(author, memberSessions);
					Int64? matchID = lobby.MatchID == 0 ? null : (Int64)lobby.MatchID;
					store = db => Database.Chat.InsertLobbyMessage(db, targetID, matchID, author, storedType, content);
					deliver = message =>
					{
						// lets members who leave later still report what they received
						LobbyChatAudience.Record(targetID, memberIDs);
						return RealtimeOut.GroupExcept(RealtimeGroups.Lobby(targetID), excluded).MessageCreate(message);
					};
					break;
				}
				case "dm":
				{
					if (targetID <= 0 || targetID == author)
					{
						return Result.Invalid("channel_id");
					}

					DirectAccess access = await DirectAccessAsync(author, targetID);
					if (access == DirectAccess.UnknownUser)
					{
						return Result.NotFound("user");
					}
					if (access == DirectAccess.Denied)
					{
						return Result.Fail(ErrorCodes.ChatNotFriends);
					}
					if (access == DirectAccess.Staff)
					{
						staffRecipient = targetID;
					}

					Int64 recipient = targetID;
					store = db => Database.Chat.InsertDirectMessage(db, author, recipient, storedType, content);

					// each side sees the conversation as dm:<the other user>
					deliver = async message =>
					{
						await RealtimeOut.User(author).MessageCreate(message with { ChannelId = Database.Chat.DirectConversationKey(recipient) });
						await RealtimeOut.User(recipient).MessageCreate(message with { ChannelId = Database.Chat.DirectConversationKey(author) });
					};
					break;
				}
				default:
				{
					// group:<id>, members who block each other stay in the group and never receive each other's messages
					Int64 groupID = targetID;
					Int64[] recipients;
					await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
					{
						Database.GroupInfo? group = await Database.Chat.GetGroup(db, groupID);
						if (group == null || !group.MemberIDs.Contains(author))
						{
							return Result.Fail(ErrorCodes.GroupNotMember);
						}

						HashSet<long> blocked = await BlockedEitherWayAsync(db, author);
						recipients = group.MemberIDs.Where(id => id == author || !blocked.Contains(id)).ToArray();
					}

					store = db => Database.Chat.InsertGroupMessage(db, groupID, author, storedType, content);
					deliver = message => RealtimeOut.Users(recipients).MessageCreate(message);
					break;
				}
			}

			Sanction? mute;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				mute = await Database.Moderation.GetActiveMute(db, author);
			}
			if (mute != null)
			{
				return mute.ExpiresAt == null ? Result.Fail(ErrorCodes.ChatMuted) : Result.Fail(ErrorCodes.ChatMuted, new { ExpiresAt = mute.ExpiresAt.Value });
			}

			// only the public channel chat is rate limited, lobby hosts can kick and direct and group messages go to friends
			if (kind == "channel" && !ChatRateLimiter.TryConsume(authorData, out long retryAfterMs))
			{
				return Result.Fail(ErrorCodes.ChatRateLimited, new { RetryAfterMs = retryAfterMs });
			}

			// stored before delivery, so the wire id and created_at are the stored ones and history never misses a delivered message
			ChatMessage stored;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				stored = await store(db);

				// ids only, the audit row never holds the chat text
				if (staffRecipient is Int64 staffTarget)
				{
					await Database.Moderation.LogAction(db, Actor(), ModerationActions.StaffMessage, AuditTargetKinds.User, staffTarget, null, new { message_id = stored.ID });
				}
			}

			string messageID = stored.ID.ToString(CultureInfo.InvariantCulture);
			Message message = new Message
			{
				Id = messageID,
				ChannelId = ConversationIDOf(stored, author),
				Type = request.Type,
				Author = RealtimeMembers.Of(author, authorData.m_strDisplayName, null, authorData.m_NameTag),
				Content = content,
				CreatedAt = stored.CreatedAt
			};
			await deliver(message);

			if (kind == "channel" && Program.g_Discord != null)
			{
				RealtimeOut.Fire(Program.g_Discord.SendNetworkRoomChat((int)targetID, author, authorData.m_strDisplayName, content));
			}

			return Result.Success(new SendMessageData { MessageId = messageID });
		}

		private partial async Task RestoreChannelAsync(UserSession session)
		{
			Int16 channelID = session.networkRoomID;
			if (channelID < 0)
			{
				return;
			}

			string channel = RealtimeGroups.Channel(channelID);
			await Clients.Group(channel).MemberAdd(new MemberAddEvent { ChannelId = channel, Member = RealtimeMembers.Of(session) });
			await Groups.AddToGroupAsync(Context.ConnectionId, channel);
			await Clients.Caller.ChannelMembers(BuildChannelMembers(channelID));
			await SendChannelHistoryAsync(channelID);
		}

		// A dropped connection leaves its channel for the others, the session keeps the channel for RestoreChannelAsync
		private static async Task LeaveChannelOnCloseAsync(UserSession session)
		{
			// a newer connection of the same session took over and stays in the channel
			if (WebSocketManager.GetWebSocketForSession(session) != null)
			{
				return;
			}

			Int16 channelID = session.networkRoomID;
			if (channelID >= 0)
			{
				await AnnounceChannelLeaveAsync(session, channelID);
			}
		}

		private async Task ChangeChannelAsync(UserSession session, Int16 oldChannel, Int16 newChannel)
		{
			if (oldChannel != newChannel)
			{
				if (oldChannel >= 0)
				{
					await Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeGroups.Channel(oldChannel));
					await AnnounceChannelLeaveAsync(session, oldChannel);
				}

				if (newChannel >= 0)
				{
					string channel = RealtimeGroups.Channel(newChannel);
					await Clients.Group(channel).MemberAdd(new MemberAddEvent { ChannelId = channel, Member = RealtimeMembers.Of(session) });
					await Groups.AddToGroupAsync(Context.ConnectionId, channel);
				}

				PublishPresence(session.m_UserID);
			}

			if (newChannel >= 0)
			{
				await Clients.Caller.ChannelMembers(BuildChannelMembers(newChannel));
				await SendChannelHistoryAsync(newChannel);
			}
		}

		// MemberRemove carries only the user id, so the user's sessions still in the channel are added again
		private static async Task AnnounceChannelLeaveAsync(UserSession leaving, Int16 channelID)
		{
			string channel = RealtimeGroups.Channel(channelID);
			IRealtimeClient audience = RealtimeOut.Group(channel);
			await audience.MemberRemove(new MemberRemoveEvent { ChannelId = channel, UserId = leaving.m_UserID });

			foreach (UserSession remaining in WebSocketManager.GetAllDataFromUser(leaving.m_UserID))
			{
				if (!ReferenceEquals(remaining, leaving) && remaining.networkRoomID == channelID && RealtimeAudience.ConnectionOf(remaining) != null)
				{
					await audience.MemberAdd(new MemberAddEvent { ChannelId = channel, Member = RealtimeMembers.Of(remaining) });
				}
			}
		}

		private static ChannelMembersEvent BuildChannelMembers(Int16 channelID)
		{
			List<Member> members = new();
			foreach (UserSession session in RealtimeAudience.AllSessions())
			{
				if (session.networkRoomID == channelID && RealtimeAudience.ConnectionOf(session) != null)
				{
					members.Add(RealtimeMembers.Of(session));
				}
			}

			return new ChannelMembersEvent { ChannelId = channelID, Members = members };
		}

		private enum DirectAccess { Denied, UnknownUser, Friend, Staff, Reply }

		// Mutual friends without a block, staff holding chat.message_anyone to anyone, and a user answering staff who wrote to them
		private async Task<DirectAccess> DirectAccessAsync(Int64 author, Int64 recipient)
		{
			Int64 low = Math.Min(author, recipient);
			Int64 high = Math.Max(author, recipient);
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();

			bool friends = await db.Friendships.AnyAsync(f => f.UserIdLow == low && f.UserIdHigh == high);
			bool blocked = await db.Blocks.AnyAsync(b => (b.UserId == author && b.BlockedUserId == recipient) || (b.UserId == recipient && b.BlockedUserId == author));
			if (friends && !blocked)
			{
				return DirectAccess.Friend;
			}

			// staff reach any user, friend or not, and a block does not stop them
			if (await RequirePermissionAsync(Permissions.ChatMessageAnyone) == null)
			{
				return await Database.Users.Exists(db, recipient) ? DirectAccess.Staff : DirectAccess.UnknownUser;
			}

			// the user answers staff for as long as the conversation holds a stored message from them
			if (!blocked && StaffRoles.HasPermission(recipient, Permissions.ChatMessageAnyone) && await Database.Chat.HasDirectMessage(db, recipient, author))
			{
				return DirectAccess.Reply;
			}
			return DirectAccess.Denied;
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
			return (kind == "channel" || kind == "lobby" || kind == "dm" || kind == "group")
				&& Int64.TryParse(channelID.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out id);
		}
	}
}
