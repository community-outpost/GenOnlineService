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
using System.Text.Json;

namespace GenOnlineService.Realtime
{
	// Stored chat: ChannelHistory, GetHistory, Conversations and read markers (sections 5.1 and 6.2)
	public sealed partial class RealtimeHub
	{
		private const int DefaultHistorySize = 50;
		private const int LobbyHistorySize = 50;
		private const int DirectConversationsMax = 50;

		// One ChannelHistory event stays under 64 KB with room for the event envelope
		private const int HistoryEventBudget = 64 * 1024 - 512;

		private static readonly JsonSerializerOptions s_wireJson = RealtimeJson.Create();

		// Realtime:history_size, the number of channel messages replayed on join
		private static int HistorySize
		{
			get
			{
				int configured = Program.g_Config?.GetSection("Realtime").GetValue("history_size", DefaultHistorySize) ?? DefaultHistorySize;
				return Math.Clamp(configured, 1, Database.Chat.MaximumPageSize);
			}
		}

		public async Task<Result<HistoryData>> GetHistory(GetHistoryRequest request)
		{
			if (!TryParseChannel(request.ChannelId, out string kind, out Int64 targetID))
			{
				return Result.Invalid("channel_id");
			}
			if (request.Limit < 1 || request.Limit > Database.Chat.MaximumPageSize)
			{
				return Result.Invalid("limit");
			}

			Int64? before = null;
			if (request.Before != null)
			{
				if (!TryParseMessageID(request.Before, out Int64 beforeID))
				{
					return Result.Invalid("before");
				}
				before = beforeID;
			}

			Int64 me = UserID;
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			HashSet<long> blocked = await BlockedEitherWayAsync(db, me);

			Database.ChatPage page;
			if (kind == "channel")
			{
				if (!RoomCatalog.Rooms.Any(room => room.ID == targetID))
				{
					return Result.NotFound("channel");
				}
				page = await Database.Chat.GetChannelHistory(db, targetID, before, request.Limit, blocked);
			}
			else if (kind == "lobby")
			{
				// current members only, and only the messages of this lobby since it was created
				Lobby? lobby = m_lobbyManager.GetLobby(targetID);
				if (lobby == null || lobby.GetMemberFromUserID(me) == null)
				{
					return Result.Fail(ErrorCodes.LobbyNotMember);
				}
				page = await Database.Chat.GetLobbyHistory(db, targetID, lobby.TimeCreated, before, request.Limit, blocked);
			}
			else if (kind == "group")
			{
				if (!await Database.Chat.IsGroupMember(db, targetID, me))
				{
					return Result.Fail(ErrorCodes.GroupNotMember);
				}

				// members who block each other stay in the group and never see each other's messages
				page = await Database.Chat.GetGroupHistory(db, targetID, before, request.Limit, blocked);
			}
			else
			{
				if (targetID <= 0 || targetID == me)
				{
					return Result.Invalid("channel_id");
				}

				// a removed friend stays readable, a block hides the whole conversation like SendMessage refuses it, except with staff
				if (blocked.Contains(targetID) && !IsStaffConversation(me, targetID))
				{
					return Result.Success(new HistoryData());
				}
				page = await Database.Chat.GetDirectHistory(db, me, targetID, before, request.Limit);
			}

			Dictionary<long, (string Name, int Tag)> names = await NamesAsync(db, page.Messages.Select(m => m.SenderID));
			return Result.Success(new HistoryData { Messages = page.Messages.Select(m => ToWire(m, me, names)).ToArray(), HasMore = page.HasMore });
		}

		// Marks the whole dm: or group: conversation read up to its newest message
		public async Task<Result<Empty>> MarkRead(MarkReadRequest request)
		{
			if (!TryParseChannel(request.ChannelId, out string kind, out Int64 targetID))
			{
				return Result.Invalid("channel_id");
			}
			Int64 me = UserID;
			if ((kind != "dm" && kind != "group") || targetID <= 0 || (kind == "dm" && targetID == me))
			{
				return Result.NotFound("channel");
			}

			string conversation = kind == "dm" ? Database.Chat.DirectConversationKey(targetID) : Database.Chat.GroupConversationKey(targetID);

			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				if (kind == "group" && !await Database.Chat.IsGroupMember(db, targetID, me))
				{
					return Result.Fail(ErrorCodes.GroupNotMember);
				}

				// an unchanged marker or an empty conversation is already read
				if (!await Database.Chat.MarkConversationRead(db, me, conversation))
				{
					return Result.Success();
				}
			}

			// the user's other connections on any copy
			await Clients.OthersInGroup(RealtimeGroups.User(me)).ReadMarker(new ReadMarkerEvent { ChannelId = conversation });
			return Result.Success();
		}

		// The last messages of the channel after ChannelMembers, oldest first, split to keep each event under 64 KB
		private async Task SendChannelHistoryAsync(Int64 channelID)
		{
			Int64 me = UserID;
			Database.ChatPage page;
			Dictionary<long, (string Name, int Tag)> names;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				HashSet<long> blocked = await BlockedEitherWayAsync(db, me);
				page = await Database.Chat.GetChannelHistory(db, channelID, null, HistorySize, blocked);
				names = await NamesAsync(db, page.Messages.Select(m => m.SenderID));
			}

			await SendHistoryEventsAsync(Database.Chat.ChannelConversationKey(channelID), page, names);
		}

		// The lobby messages since the lobby was created after LobbyState, at most LobbyHistorySize, oldest first
		private async Task SendLobbyHistoryAsync(Lobby lobby)
		{
			Int64 me = UserID;
			Database.ChatPage page;
			Dictionary<long, (string Name, int Tag)> names;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				HashSet<long> blocked = await BlockedEitherWayAsync(db, me);
				page = await Database.Chat.GetLobbyHistory(db, lobby.LobbyID, lobby.TimeCreated, null, LobbyHistorySize, blocked);
				names = await NamesAsync(db, page.Messages.Select(m => m.SenderID));
			}

			await SendHistoryEventsAsync(RealtimeGroups.Lobby(lobby.LobbyID), page, names);
		}

		// One replay as ChannelHistory events to the caller, split to keep each event under 64 KB
		private async Task SendHistoryEventsAsync(string channel, Database.ChatPage page, IReadOnlyDictionary<long, (string Name, int Tag)> names)
		{
			Int64 me = UserID;
			List<Message> chunk = new();
			int size = 0;
			foreach (ChatMessage row in page.Messages)
			{
				Message message = ToWire(row, me, names);
				int messageSize = JsonSerializer.SerializeToUtf8Bytes(message, s_wireJson).Length + 1;
				if (chunk.Count > 0 && size + messageSize > HistoryEventBudget)
				{
					await Clients.Caller.ChannelHistory(new ChannelHistoryEvent { ChannelId = channel, Messages = chunk, HasMore = page.HasMore });
					chunk = new List<Message>();
					size = 0;
				}
				chunk.Add(message);
				size += messageSize;
			}

			// every event of one replay carries the same has_more, it tells whether older messages exist before the replay
			await Clients.Caller.ChannelHistory(new ChannelHistoryEvent { ChannelId = channel, Messages = chunk, HasMore = page.HasMore });
		}

		// Snapshot after Friends: every direct conversation and group chat with its unread count and last message, newest activity first
		// Direct conversations with users who are not friends or are blocked are left out unless one side is staff, group messages of blocked members are left out
		private async Task SendConversationsSnapshotAsync(UserSession session)
		{
			Int64 me = session.m_UserID;
			List<Database.DirectConversation> direct;
			List<Database.GroupInfo> groups;
			List<Database.GroupConversation> groupConversations;
			Dictionary<long, (string Name, int Tag)> names;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				HashSet<long> blocked = await BlockedEitherWayAsync(db, me);
				direct = await ListedDirectConversationsAsync(db, me, blocked);
				groups = await Database.Chat.GetGroupsOf(db, me);
				groupConversations = await Database.Chat.GetGroupConversations(db, me, groups.Select(g => g.Group.ID).ToArray(), blocked);

				IEnumerable<long> people = direct.Select(c => c.OtherUserID)
					.Concat(groups.SelectMany(g => g.MemberIDs))
					.Concat(groupConversations.Where(c => c.Last != null).Select(c => c.Last!.SenderID));
				names = await NamesAsync(db, people);
			}

			// users without a row no longer exist and are left out
			List<(DateTime Activity, ConversationInfo Info)> entries = direct
				.Where(c => names.ContainsKey(c.OtherUserID))
				.Select(c => (c.Last.CreatedAt, new ConversationInfo { ChannelId = Database.Chat.DirectConversationKey(c.OtherUserID), Unread = c.Unread, Last = ToWire(c.Last, me, names) }))
				.ToList();

			Dictionary<long, Database.GroupInfo> groupsByID = groups.ToDictionary(g => g.Group.ID);
			foreach (Database.GroupConversation c in groupConversations)
			{
				DateTime activity = c.Last?.CreatedAt ?? groupsByID[c.GroupID].Group.CreatedAt;
				entries.Add((activity, new ConversationInfo { ChannelId = Database.Chat.GroupConversationKey(c.GroupID), Unread = c.Unread, Last = c.Last == null ? null : ToWire(c.Last, me, names) }));
			}

			ConversationInfo[] list = entries.OrderByDescending(e => e.Activity).Select(e => e.Info).ToArray();
			await Clients.Caller.Conversations(new ConversationsEvent { Conversations = list, Groups = groups.Select(g => ToWireGroup(g, names)).ToArray() });
		}

		// The direct conversations of the Conversations snapshot, blocked being the users blocked either way with me: those with friends
		// neither side blocked, and staff conversations whatever the friendship or block. Only friends exchange direct messages, so the
		// conversation of a former friend stays stored but is not listed, and its unread messages no longer count
		internal static async Task<List<Database.DirectConversation>> ListedDirectConversationsAsync(AppDbContext db, Int64 me, IReadOnlySet<long> blocked)
		{
			HashSet<long> friends = await Database.Social.GetFriends(db, me);
			return await Database.Chat.GetDirectConversations(db, me, DirectConversationsMax, other => IsStaffConversation(me, other) || (friends.Contains(other) && !blocked.Contains(other)));
		}

		// True when either user holds chat.message_anyone, a block then does not hide their direct conversation
		internal static bool IsStaffConversation(Int64 userA, Int64 userB)
		{
			return StaffRoles.HasPermission(userA, Permissions.ChatMessageAnyone) || StaffRoles.HasPermission(userB, Permissions.ChatMessageAnyone);
		}

		internal static Group ToWireGroup(Database.GroupInfo group, IReadOnlyDictionary<long, (string Name, int Tag)> names)
		{
			return new Group
			{
				Id = group.Group.ID,
				Name = group.Group.Name,
				OwnerId = group.Group.OwnerID,
				Members = group.MemberIDs.Select(id => RealtimeMembers.Of(id, names)).ToArray(),
				CreatedAt = group.Group.CreatedAt
			};
		}

		// Users the viewer blocked and users who blocked the viewer, read from the database so offline users count too
		internal static async Task<HashSet<long>> BlockedEitherWayAsync(AppDbContext db, Int64 userID)
		{
			List<long> ids = await db.Blocks.AsNoTracking()
				.Where(b => b.UserId == userID || b.BlockedUserId == userID)
				.Select(b => b.UserId == userID ? b.BlockedUserId : b.UserId)
				.ToListAsync();
			return ids.ToHashSet();
		}

		// Live names of online users, stored names of the others
		internal static async Task<Dictionary<long, (string Name, int Tag)>> NamesAsync(AppDbContext db, IEnumerable<long> userIDs)
		{
			Dictionary<long, (string Name, int Tag)> names = new();
			List<long> offline = new();
			foreach (long userID in userIDs.Distinct())
			{
				SharedUserData? live = WebSocketManager.GetSharedDataForUser(userID);
				if (live != null)
				{
					names[userID] = (live.m_strDisplayName, live.m_NameTag);
				}
				else
				{
					offline.Add(userID);
				}
			}

			if (offline.Count > 0)
			{
				foreach ((long userID, (string Name, int Tag) name) in await Database.DisplayNames.GetBulk(db, offline))
				{
					names[userID] = name;
				}
			}
			return names;
		}

		// A stored row as the viewer sees it, a deleted row becomes a tombstone without author and content
		internal static Message ToWire(ChatMessage row, Int64 viewer, IReadOnlyDictionary<long, (string Name, int Tag)> names)
		{
			string id = row.ID.ToString(CultureInfo.InvariantCulture);
			string channelID = ConversationIDOf(row, viewer);
			if (row.DeletedAt != null)
			{
				return new Message { Id = id, ChannelId = channelID, CreatedAt = row.CreatedAt, Deleted = true };
			}

			return new Message
			{
				Id = id,
				ChannelId = channelID,
				Type = WireType(row.Type),
				Author = RealtimeMembers.Of(row.SenderID, names),
				Content = row.Content,
				Mentions = ChatMentions.OrNull(Database.Chat.DecodeMentions(row.Mentions)),
				CreatedAt = row.CreatedAt
			};
		}

		// The wire channel id of a stored row, a direct message is dm:<the other user> for either party
		private static string ConversationIDOf(ChatMessage row, Int64 viewer)
		{
			return row.Kind switch
			{
				ChatMessageKinds.Channel => Database.Chat.ChannelConversationKey(row.ChannelID ?? -1),
				ChatMessageKinds.Lobby => RealtimeGroups.Lobby(row.LobbyID ?? -1),
				ChatMessageKinds.Group => Database.Chat.GroupConversationKey(row.GroupID ?? -1),
				_ => Database.Chat.DirectConversationKey(row.SenderID == viewer ? row.RecipientID ?? -1 : row.SenderID)
			};
		}

		// True when the user is the sender or the recipient of a direct message, any user is a party of channel and lobby rows
		private static bool IsParty(ChatMessage row, Int64 userID)
		{
			return row.Kind != ChatMessageKinds.Direct || row.SenderID == userID || row.RecipientID == userID;
		}

		private static MessageType WireType(string stored) => stored switch
		{
			ChatMessageTypes.Action => MessageType.Action,
			ChatMessageTypes.Announcement => MessageType.Announcement,
			_ => MessageType.Chat
		};

		private static string StoredType(MessageType type) => type switch
		{
			MessageType.Action => ChatMessageTypes.Action,
			MessageType.Announcement => ChatMessageTypes.Announcement,
			_ => ChatMessageTypes.Chat
		};

		// Message ids are positive decimal strings
		internal static bool TryParseMessageID(string? text, out Int64 id)
		{
			return Int64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
		}
	}
}
