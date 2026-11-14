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
	// Stored chat: ChannelHistory, GetHistory, DirectMessages and read markers (sections 5.1 and 6.2)
	public sealed partial class RealtimeHub
	{
		private const int DefaultHistorySize = 50;
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
			if (!TryParseChannel(request.ChannelId, out string kind, out Int64 targetID) || kind == "lobby")
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
			else
			{
				if (targetID <= 0 || targetID == me)
				{
					return Result.Invalid("channel_id");
				}

				// a removed friend stays readable, a block hides the whole conversation like SendMessage refuses it
				if (blocked.Contains(targetID))
				{
					return Result.Success(new HistoryData());
				}
				page = await Database.Chat.GetDirectHistory(db, me, targetID, before, request.Limit);
			}

			Dictionary<long, string> names = await NamesAsync(db, page.Messages.Select(m => m.SenderID));
			return Result.Success(new HistoryData { Messages = page.Messages.Select(m => ToWire(m, me, names)).ToArray(), HasMore = page.HasMore });
		}

		public async Task<Result<Empty>> MarkRead(MarkReadRequest request)
		{
			if (!TryParseChannel(request.ChannelId, out string kind, out Int64 targetID) || kind == "lobby" || (kind == "dm" && (targetID <= 0 || targetID == UserID)))
			{
				return Result.Invalid("channel_id");
			}
			if (!TryParseMessageID(request.MessageId, out Int64 messageID))
			{
				return Result.Invalid("message_id");
			}

			Int64 me = UserID;
			string conversation = kind == "dm" ? Database.Chat.DirectConversationKey(targetID) : Database.Chat.ChannelConversationKey(targetID);

			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				if (!await Database.Chat.SetReadMarker(db, me, conversation, messageID))
				{
					// an unchanged marker is fine, a message outside the conversation is not
					ChatMessage? row = await Database.Chat.GetMessage(db, messageID);
					return row != null && IsParty(row, me) && ConversationIDOf(row, me) == conversation ? Result.Success() : Result.NotFound("message");
				}
			}

			// the user's other connections on any copy
			await Clients.OthersInGroup(RealtimeGroups.User(me)).ReadMarker(new MessageRefEvent { ChannelId = conversation, MessageId = messageID.ToString(CultureInfo.InvariantCulture) });
			return Result.Success();
		}

		// The last messages of the channel after ChannelMembers, oldest first, split to keep each event under 64 KB
		private async Task SendChannelHistoryAsync(Int64 channelID)
		{
			Int64 me = UserID;
			Database.ChatPage page;
			Dictionary<long, string> names;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				HashSet<long> blocked = await BlockedEitherWayAsync(db, me);
				page = await Database.Chat.GetChannelHistory(db, channelID, null, HistorySize, blocked);
				names = await NamesAsync(db, page.Messages.Select(m => m.SenderID));
			}

			string channel = Database.Chat.ChannelConversationKey(channelID);
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

		// Snapshot after Friends: every direct conversation with its unread count and last message, blocked users left out
		private async Task SendDirectMessagesSnapshotAsync(UserSession session)
		{
			Int64 me = session.m_UserID;
			List<Database.DirectConversation> conversations;
			Dictionary<long, string> names;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				HashSet<long> blocked = await BlockedEitherWayAsync(db, me);
				conversations = await Database.Chat.GetDirectConversations(db, me, DirectConversationsMax, blocked);
				names = await NamesAsync(db, conversations.Select(c => c.OtherUserID));
			}

			// users without a row no longer exist and are left out
			DirectConversationInfo[] list = conversations
				.Where(c => names.ContainsKey(c.OtherUserID))
				.Select(c => new DirectConversationInfo { UserId = c.OtherUserID, Unread = c.Unread, Last = ToWire(c.Last, me, names) })
				.ToArray();
			await Clients.Caller.DirectMessages(new DirectMessagesEvent { Conversations = list });
		}

		// Users the viewer blocked and users who blocked the viewer, read from the database so offline users count too
		private static async Task<HashSet<long>> BlockedEitherWayAsync(AppDbContext db, Int64 userID)
		{
			List<long> ids = await db.Blocks.AsNoTracking()
				.Where(b => b.UserId == userID || b.BlockedUserId == userID)
				.Select(b => b.UserId == userID ? b.BlockedUserId : b.UserId)
				.ToListAsync();
			return ids.ToHashSet();
		}

		// Live names of online users, stored names of the others
		private static async Task<Dictionary<long, string>> NamesAsync(AppDbContext db, IEnumerable<long> userIDs)
		{
			Dictionary<long, string> names = new();
			List<long> offline = new();
			foreach (long userID in userIDs.Distinct())
			{
				string? live = WebSocketManager.GetSharedDataForUser(userID)?.m_strDisplayName;
				if (live != null)
				{
					names[userID] = live;
				}
				else
				{
					offline.Add(userID);
				}
			}

			if (offline.Count > 0)
			{
				foreach ((long userID, string name) in await Database.Users.GetDisplayNameBulk(db, offline))
				{
					names[userID] = name;
				}
			}
			return names;
		}

		// A stored row as the viewer sees it, a deleted row becomes a tombstone without author and content
		private static Message ToWire(ChatMessage row, Int64 viewer, IReadOnlyDictionary<long, string> names)
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
				Author = RealtimeMembers.Of(row.SenderID, names.TryGetValue(row.SenderID, out string? name) ? name : null),
				Content = row.Content,
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
		private static bool TryParseMessageID(string? text, out Int64 id)
		{
			return Int64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
		}
	}
}
