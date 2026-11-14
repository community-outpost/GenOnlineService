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
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.Text.Json;
using System.Text.Json.Serialization;

public static class ChatMessageKinds
{
	public const string Channel = "channel";
	public const string Lobby = "lobby";
	public const string Direct = "direct";
	public const string Group = "group";
}

public static class ChatMessageTypes
{
	public const string Chat = "chat";
	public const string Action = "action";
	public const string Announcement = "announcement";
}

public static class ChatReportStatuses
{
	public const string Open = "open";
	public const string Closed = "closed";
}

// Stored as sent without encryption, who deleted a message and why live in audit_log
public class ChatMessage
{
	public Int64 ID { get; set; }
	public string Kind { get; set; } = String.Empty;

	// Catalog channel id, set for channel messages only
	public Int64? ChannelID { get; set; }

	// Set for lobby messages only, the match id stays null until the game starts
	public Int64? LobbyID { get; set; }
	public Int64? MatchID { get; set; }

	public Int64 SenderID { get; set; }

	// Set for direct messages only
	public Int64? RecipientID { get; set; }

	// Set for group messages only, the group row may be gone while its messages wait for the retention purge
	public Int64? GroupID { get; set; }

	public string Type { get; set; } = String.Empty;
	public string Content { get; set; } = String.Empty;
	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;

	// Set when staff delete the message, history then shows a tombstone
	public DateTime? DeletedAt { get; set; }
}

// A group chat, deleted when its last member leaves
public class ChatGroup
{
	public Int64 ID { get; set; }

	// Null when the owner set none, clients then show the member names
	public string? Name { get; set; }
	public Int64 OwnerID { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;
}

// One member of a group, the id keeps the join order
public class ChatGroupMember
{
	public Int64 ID { get; set; }
	public Int64 GroupID { get; set; }
	public Int64 UserID { get; set; }
	public DateTime JoinedAt { get; set; } = DateTime.UnixEpoch;
}

// The last message a user has read in one conversation, keyed by the wire channel id (dm:<user_id>, channel:<id>, group:<id>)
public class ChatReadMarker
{
	public Int64 UserID { get; set; }
	public string Conversation { get; set; } = String.Empty;
	public Int64 LastReadMessageID { get; set; }
	public DateTime UpdatedAt { get; set; } = DateTime.UnixEpoch;
}

// A player report of one message, the snapshot outlives the purged messages until the case is closed
public class ChatReport
{
	public Int64 ID { get; set; }
	public Int64 MessageID { get; set; }
	public Int64 ReporterID { get; set; }
	public string? Reason { get; set; }

	// A JSON object with the reported message, up to 10 messages before it and up to 10 after it in the same conversation
	// The retention purge fills "after" and "after_filled_at" just before the reported message expires
	public string Snapshot { get; set; } = "{}";

	// Whatever closes a report sets closed_at too, the purge counts from it
	public string Status { get; set; } = ChatReportStatuses.Open;
	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;
	public DateTime? ClosedAt { get; set; }
}

public class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
	public void Configure(EntityTypeBuilder<ChatMessage> builder)
	{
		builder.ToTable("chat_messages");

		builder.HasKey(e => e.ID);

		// Channel history paging
		builder.HasIndex(e => new { e.Kind, e.ChannelID, e.ID }).HasDatabaseName("ix_chat_messages_channel");

		// Lobby context for report snapshots
		builder.HasIndex(e => new { e.LobbyID, e.ID }).HasDatabaseName("ix_chat_messages_lobby");

		// Direct conversation paging in both directions, conversation lists and unread counts
		builder.HasIndex(e => new { e.SenderID, e.RecipientID, e.ID }).HasDatabaseName("ix_chat_messages_sender_recipient");
		builder.HasIndex(e => new { e.RecipientID, e.SenderID, e.ID }).HasDatabaseName("ix_chat_messages_recipient_sender");

		// Group history paging and conversation lists
		builder.HasIndex(e => new { e.GroupID, e.ID }).HasDatabaseName("ix_chat_messages_group");

		// Retention purge
		builder.HasIndex(e => e.CreatedAt).HasDatabaseName("ix_chat_messages_created_at");

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.Kind).HasColumnName("kind").HasMaxLength(16);
		builder.Property(e => e.ChannelID).HasColumnName("channel_id");
		builder.Property(e => e.LobbyID).HasColumnName("lobby_id");
		builder.Property(e => e.MatchID).HasColumnName("match_id");
		builder.Property(e => e.SenderID).HasColumnName("sender_id");
		builder.Property(e => e.RecipientID).HasColumnName("recipient_id");
		builder.Property(e => e.GroupID).HasColumnName("group_id");
		builder.Property(e => e.Type).HasColumnName("type").HasMaxLength(16);
		builder.Property(e => e.Content).HasColumnName("content").HasMaxLength(Database.Chat.MaximumContentLength);
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");
	}
}

public class ChatGroupConfiguration : IEntityTypeConfiguration<ChatGroup>
{
	public void Configure(EntityTypeBuilder<ChatGroup> builder)
	{
		builder.ToTable("chat_groups");

		builder.HasKey(e => e.ID);

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(Database.Chat.MaximumGroupNameLength);
		builder.Property(e => e.OwnerID).HasColumnName("owner_id");
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
	}
}

public class ChatGroupMemberConfiguration : IEntityTypeConfiguration<ChatGroupMember>
{
	public void Configure(EntityTypeBuilder<ChatGroupMember> builder)
	{
		builder.ToTable("chat_group_members");

		builder.HasKey(e => e.ID);
		builder.HasIndex(e => new { e.GroupID, e.UserID }).IsUnique().HasDatabaseName("ux_chat_group_members_group_user");
		builder.HasIndex(e => e.UserID).HasDatabaseName("ix_chat_group_members_user_id");

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.GroupID).HasColumnName("group_id");
		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.JoinedAt).HasColumnName("joined_at");
	}
}

public class ChatReadMarkerConfiguration : IEntityTypeConfiguration<ChatReadMarker>
{
	public void Configure(EntityTypeBuilder<ChatReadMarker> builder)
	{
		builder.ToTable("chat_read_markers");

		builder.HasKey(e => new { e.UserID, e.Conversation });
		builder.HasIndex(e => e.UpdatedAt).HasDatabaseName("ix_chat_read_markers_updated_at");

		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.Conversation).HasColumnName("conversation").HasMaxLength(Database.Chat.MaximumConversationLength);
		builder.Property(e => e.LastReadMessageID).HasColumnName("last_read_message_id");
		builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");
	}
}

public class ChatReportConfiguration : IEntityTypeConfiguration<ChatReport>
{
	public void Configure(EntityTypeBuilder<ChatReport> builder)
	{
		builder.ToTable("chat_reports");

		builder.HasKey(e => e.ID);
		builder.HasIndex(e => new { e.MessageID, e.ReporterID }).IsUnique().HasDatabaseName("ux_chat_reports_message_reporter");
		builder.HasIndex(e => e.ReporterID).HasDatabaseName("ix_chat_reports_reporter_id");
		builder.HasIndex(e => new { e.Status, e.ClosedAt }).HasDatabaseName("ix_chat_reports_status_closed_at");

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.MessageID).HasColumnName("message_id");
		builder.Property(e => e.ReporterID).HasColumnName("reporter_id");
		builder.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(Database.Chat.MaximumReasonLength);
		builder.Property(e => e.Snapshot).HasColumnName("snapshot");
		builder.Property(e => e.Status).HasColumnName("status").HasMaxLength(16);
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		builder.Property(e => e.ClosedAt).HasColumnName("closed_at");
	}
}

namespace Database
{
	// One page of a conversation oldest first, HasMore is true when older messages exist
	public sealed record ChatPage(List<ChatMessage> Messages, bool HasMore);

	// One direct conversation of a user, for the Conversations snapshot
	public sealed record DirectConversation(long OtherUserID, int Unread, ChatMessage Last);

	// A group with its member ids in join order, the owner included
	public sealed record GroupInfo(ChatGroup Group, List<long> MemberIDs);

	// One group conversation of a user, Last is null while the group has no message the user may see
	public sealed record GroupConversation(long GroupID, int Unread, ChatMessage? Last);

	// WasMember is false when the user was not in the group, Remaining is null when the last member left and the group is gone
	public sealed record GroupLeaveResult(bool WasMember, GroupInfo? Remaining);

	// The message is null when no message has that id, AlreadyDeleted is true when it was a tombstone before the call
	public sealed record ChatTombstone(ChatMessage? Message, bool AlreadyDeleted);

	public enum ChatReportResult
	{
		Created,
		Duplicate
	}

	public sealed record ChatPurgeCounts(int Messages, int ReadMarkers, int Reports);

	// Chat storage, block filtering stays with the caller and the queries only take ids to leave out
	public static class Chat
	{
		public const int MaximumContentLength = 512;
		public const int MaximumReasonLength = 512;
		public const int MaximumConversationLength = 64;
		public const int SnapshotContextMessages = 10;
		public const int MaximumPageSize = 100;
		public const int MaximumGroupNameLength = 32;

		// Every stored message and tombstone is kept 30 days, closed reports 30 days after closing
		public static readonly TimeSpan MessageRetention = TimeSpan.FromDays(30);
		public static readonly TimeSpan ClosedReportRetention = TimeSpan.FromDays(30);

		public const string DirectConversationPrefix = "dm:";
		public const string ChannelConversationPrefix = "channel:";
		public const string GroupConversationPrefix = "group:";

		public static string DirectConversationKey(long otherUserId) => DirectConversationPrefix + otherUserId;
		public static string ChannelConversationKey(long channelId) => ChannelConversationPrefix + channelId;
		public static string GroupConversationKey(long groupId) => GroupConversationPrefix + groupId;

		public static Task<ChatMessage> InsertChannelMessage(AppDbContext db, long channelId, long senderId, string type, string content, CancellationToken cancellationToken = default)
		{
			return Insert(db, new ChatMessage { Kind = ChatMessageKinds.Channel, ChannelID = channelId, SenderID = senderId, Type = type, Content = content }, cancellationToken);
		}

		public static Task<ChatMessage> InsertLobbyMessage(AppDbContext db, long lobbyId, long? matchId, long senderId, string type, string content, CancellationToken cancellationToken = default)
		{
			return Insert(db, new ChatMessage { Kind = ChatMessageKinds.Lobby, LobbyID = lobbyId, MatchID = matchId, SenderID = senderId, Type = type, Content = content }, cancellationToken);
		}

		public static Task<ChatMessage> InsertDirectMessage(AppDbContext db, long senderId, long recipientId, string type, string content, CancellationToken cancellationToken = default)
		{
			return Insert(db, new ChatMessage { Kind = ChatMessageKinds.Direct, SenderID = senderId, RecipientID = recipientId, Type = type, Content = content }, cancellationToken);
		}

		public static Task<ChatMessage> InsertGroupMessage(AppDbContext db, long groupId, long senderId, string type, string content, CancellationToken cancellationToken = default)
		{
			return Insert(db, new ChatMessage { Kind = ChatMessageKinds.Group, GroupID = groupId, SenderID = senderId, Type = type, Content = content }, cancellationToken);
		}

		// Returns the stored row with its id and created_at, failures throw so the caller can answer the sender
		private static async Task<ChatMessage> Insert(AppDbContext db, ChatMessage message, CancellationToken cancellationToken)
		{
			if (message.Content.Length == 0 || message.Content.Length > MaximumContentLength)
			{
				throw new ArgumentOutOfRangeException(nameof(message), "Chat content must be 1 to 512 characters.");
			}

			message.CreatedAt = TruncateToMicroseconds(DateTime.UtcNow);
			db.ChatMessages.Add(message);

			try
			{
				await db.SaveChangesAsync(cancellationToken);
			}
			finally
			{
				db.ChangeTracker.Clear();
			}

			return message;
		}

		// The limit messages just older than beforeId or the newest, returned oldest first with tombstones included
		public static async Task<ChatPage> GetChannelHistory(AppDbContext db, long channelId, long? beforeId, int limit, IReadOnlyCollection<long>? excludeSenderIds = null, CancellationToken cancellationToken = default)
		{
			limit = Math.Clamp(limit, 1, MaximumPageSize);

			IQueryable<ChatMessage> query = db.ChatMessages.AsNoTracking()
				.Where(m => m.Kind == ChatMessageKinds.Channel && m.ChannelID == channelId);

			if (beforeId is long before)
			{
				query = query.Where(m => m.ID < before);
			}

			if (excludeSenderIds is { Count: > 0 })
			{
				long[] excluded = excludeSenderIds.ToArray();
				query = query.Where(m => !excluded.Contains(m.SenderID));
			}

			List<ChatMessage> rows = await query.OrderByDescending(m => m.ID).Take(limit + 1).ToListAsync(cancellationToken);
			return ToPage(rows, limit);
		}

		// Same shape as channel history for the lobby messages since the lobby was created, lobby ids restart with the service
		public static async Task<ChatPage> GetLobbyHistory(AppDbContext db, long lobbyId, DateTime since, long? beforeId, int limit, IReadOnlyCollection<long>? excludeSenderIds = null, CancellationToken cancellationToken = default)
		{
			limit = Math.Clamp(limit, 1, MaximumPageSize);
			DateTime from = TruncateToMicroseconds(since);

			IQueryable<ChatMessage> query = db.ChatMessages.AsNoTracking()
				.Where(m => m.Kind == ChatMessageKinds.Lobby && m.LobbyID == lobbyId && m.CreatedAt >= from);

			if (beforeId is long before)
			{
				query = query.Where(m => m.ID < before);
			}

			if (excludeSenderIds is { Count: > 0 })
			{
				long[] excluded = excludeSenderIds.ToArray();
				query = query.Where(m => !excluded.Contains(m.SenderID));
			}

			List<ChatMessage> rows = await query.OrderByDescending(m => m.ID).Take(limit + 1).ToListAsync(cancellationToken);
			return ToPage(rows, limit);
		}

		// Same shape as channel history, messages of the excluded senders are left out
		public static async Task<ChatPage> GetGroupHistory(AppDbContext db, long groupId, long? beforeId, int limit, IReadOnlyCollection<long>? excludeSenderIds = null, CancellationToken cancellationToken = default)
		{
			limit = Math.Clamp(limit, 1, MaximumPageSize);

			IQueryable<ChatMessage> query = VisibleGroupMessages(db, groupId, excludeSenderIds);
			if (beforeId is long before)
			{
				query = query.Where(m => m.ID < before);
			}

			List<ChatMessage> rows = await query.OrderByDescending(m => m.ID).Take(limit + 1).ToListAsync(cancellationToken);
			return ToPage(rows, limit);
		}

		private static IQueryable<ChatMessage> VisibleGroupMessages(AppDbContext db, long groupId, IReadOnlyCollection<long>? excludeSenderIds)
		{
			IQueryable<ChatMessage> query = db.ChatMessages.AsNoTracking().Where(m => m.GroupID == groupId);
			if (excludeSenderIds is { Count: > 0 })
			{
				long[] excluded = excludeSenderIds.ToArray();
				query = query.Where(m => !excluded.Contains(m.SenderID));
			}
			return query;
		}

		// Both directions between two users, same shape as channel history
		public static async Task<ChatPage> GetDirectHistory(AppDbContext db, long userId, long otherUserId, long? beforeId, int limit, CancellationToken cancellationToken = default)
		{
			limit = Math.Clamp(limit, 1, MaximumPageSize);
			long before = beforeId ?? long.MaxValue;

			// One range scan per direction keeps both on their index and the merge happens here
			List<ChatMessage> sent = await db.ChatMessages.AsNoTracking()
				.Where(m => m.SenderID == userId && m.RecipientID == otherUserId && m.ID < before)
				.OrderByDescending(m => m.ID).Take(limit + 1).ToListAsync(cancellationToken);

			List<ChatMessage> received = await db.ChatMessages.AsNoTracking()
				.Where(m => m.SenderID == otherUserId && m.RecipientID == userId && m.ID < before)
				.OrderByDescending(m => m.ID).Take(limit + 1).ToListAsync(cancellationToken);

			List<ChatMessage> rows = sent.Concat(received).OrderByDescending(m => m.ID).Take(limit + 1).ToList();
			return ToPage(rows, limit);
		}

		// Conversations of a user, newest first, with the last message and the unread count past the read marker
		public static async Task<List<DirectConversation>> GetDirectConversations(AppDbContext db, long userId, int maxConversations = 50, IReadOnlyCollection<long>? excludeUserIds = null, CancellationToken cancellationToken = default)
		{
			var outgoing = await db.ChatMessages.AsNoTracking()
				.Where(m => m.SenderID == userId && m.RecipientID != null)
				.GroupBy(m => m.RecipientID!.Value)
				.Select(g => new { Other = g.Key, Last = g.Max(m => m.ID) })
				.ToListAsync(cancellationToken);

			var incoming = await db.ChatMessages.AsNoTracking()
				.Where(m => m.RecipientID == userId)
				.GroupBy(m => m.SenderID)
				.Select(g => new { Other = g.Key, Last = g.Max(m => m.ID) })
				.ToListAsync(cancellationToken);

			Dictionary<long, long> lastIncoming = incoming.ToDictionary(x => x.Other, x => x.Last);
			Dictionary<long, long> lastAny = new(lastIncoming);
			foreach (var row in outgoing)
			{
				lastAny[row.Other] = lastAny.TryGetValue(row.Other, out long existing) ? Math.Max(existing, row.Last) : row.Last;
			}

			List<KeyValuePair<long, long>> selected = lastAny
				.Where(kv => excludeUserIds == null || !excludeUserIds.Contains(kv.Key))
				.OrderByDescending(kv => kv.Value)
				.Take(maxConversations)
				.ToList();

			if (selected.Count == 0)
			{
				return new List<DirectConversation>();
			}

			long[] lastIds = selected.Select(kv => kv.Value).ToArray();
			Dictionary<long, ChatMessage> lastMessages = await db.ChatMessages.AsNoTracking()
				.Where(m => lastIds.Contains(m.ID))
				.ToDictionaryAsync(m => m.ID, cancellationToken);

			// One grouped query counts every sender past the marker of that conversation
			long[] senders = selected.Where(kv => lastIncoming.ContainsKey(kv.Key)).Select(kv => kv.Key).ToArray();
			Dictionary<long, int> unreadBySender = new();

			if (senders.Length > 0)
			{
				var unreadRows = await (
					from m in db.ChatMessages.AsNoTracking()
					where m.RecipientID == userId && m.DeletedAt == null && senders.Contains(m.SenderID)
					join r in db.ChatReadMarkers.AsNoTracking().Where(r => r.UserID == userId)
						on DirectConversationPrefix + m.SenderID.ToString() equals r.Conversation into joined
					from r in joined.DefaultIfEmpty()
					where r == null || m.ID > r.LastReadMessageID
					group m by m.SenderID into g
					select new { Sender = g.Key, Count = g.Count() })
					.ToListAsync(cancellationToken);

				unreadBySender = unreadRows.ToDictionary(x => x.Sender, x => x.Count);
			}

			List<DirectConversation> result = new(selected.Count);
			foreach (var (other, lastId) in selected)
			{
				if (lastMessages.TryGetValue(lastId, out ChatMessage? last))
				{
					result.Add(new DirectConversation(other, unreadBySender.GetValueOrDefault(other), last));
				}
			}

			return result;
		}

		// Read positions of a user keyed by conversation, optionally only those starting with a prefix
		public static async Task<Dictionary<string, long>> GetReadMarkers(AppDbContext db, long userId, string? conversationPrefix = null, CancellationToken cancellationToken = default)
		{
			IQueryable<ChatReadMarker> query = db.ChatReadMarkers.AsNoTracking().Where(r => r.UserID == userId);

			if (!String.IsNullOrEmpty(conversationPrefix))
			{
				query = query.Where(r => r.Conversation.StartsWith(conversationPrefix));
			}

			return await query.ToDictionaryAsync(r => r.Conversation, r => r.LastReadMessageID, cancellationToken);
		}

		// Moves the read position forward only and returns true when it moved
		public static async Task<bool> SetReadMarker(AppDbContext db, long userId, string conversation, long messageId, CancellationToken cancellationToken = default)
		{
			if (conversation.Length == 0 || conversation.Length > MaximumConversationLength)
			{
				throw new ArgumentOutOfRangeException(nameof(conversation));
			}

			if (!await MessageBelongsTo(db, userId, conversation, messageId, cancellationToken))
			{
				return false;
			}

			DateTime now = TruncateToMicroseconds(DateTime.UtcNow);

			for (int attempt = 0; attempt < 2; attempt++)
			{
				int updated = await db.ChatReadMarkers
					.Where(r => r.UserID == userId && r.Conversation == conversation && r.LastReadMessageID < messageId)
					.ExecuteUpdateAsync(s => s.SetProperty(r => r.LastReadMessageID, messageId).SetProperty(r => r.UpdatedAt, now), cancellationToken);

				if (updated > 0)
				{
					return true;
				}

				bool exists = await db.ChatReadMarkers.AnyAsync(r => r.UserID == userId && r.Conversation == conversation, cancellationToken);
				if (exists)
				{
					return false;
				}

				db.ChatReadMarkers.Add(new ChatReadMarker { UserID = userId, Conversation = conversation, LastReadMessageID = messageId, UpdatedAt = now });

				try
				{
					await db.SaveChangesAsync(cancellationToken);
					return true;
				}
				catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex))
				{
					// Another connection created the row first, the update path runs again
				}
				finally
				{
					db.ChangeTracker.Clear();
				}
			}

			return false;
		}

		// True when the message exists and is part of that conversation as the user sees it
		private static async Task<bool> MessageBelongsTo(AppDbContext db, long userId, string conversation, long messageId, CancellationToken cancellationToken)
		{
			if (conversation.StartsWith(DirectConversationPrefix, StringComparison.Ordinal) && long.TryParse(conversation.AsSpan(DirectConversationPrefix.Length), out long other))
			{
				return await db.ChatMessages.AnyAsync(m => m.ID == messageId && ((m.SenderID == userId && m.RecipientID == other) || (m.SenderID == other && m.RecipientID == userId)), cancellationToken);
			}

			if (conversation.StartsWith(ChannelConversationPrefix, StringComparison.Ordinal) && long.TryParse(conversation.AsSpan(ChannelConversationPrefix.Length), out long channelId))
			{
				return await db.ChatMessages.AnyAsync(m => m.ID == messageId && m.Kind == ChatMessageKinds.Channel && m.ChannelID == channelId, cancellationToken);
			}

			// membership is the caller's check, the message only has to be in the group
			if (conversation.StartsWith(GroupConversationPrefix, StringComparison.Ordinal) && long.TryParse(conversation.AsSpan(GroupConversationPrefix.Length), out long groupId))
			{
				return await db.ChatMessages.AnyAsync(m => m.ID == messageId && m.Kind == ChatMessageKinds.Group && m.GroupID == groupId, cancellationToken);
			}

			return false;
		}

		public static async Task<ChatMessage?> GetMessage(AppDbContext db, long messageId, CancellationToken cancellationToken = default)
		{
			return await db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(m => m.ID == messageId, cancellationToken);
		}

		// Sets deleted_at once and returns the row so the caller can address MessageDelete
		public static async Task<ChatTombstone> TombstoneMessage(AppDbContext db, long messageId, CancellationToken cancellationToken = default)
		{
			DateTime now = TruncateToMicroseconds(DateTime.UtcNow);

			int updated = await db.ChatMessages
				.Where(m => m.ID == messageId && m.DeletedAt == null)
				.ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedAt, now), cancellationToken);

			ChatMessage? message = await GetMessage(db, messageId, cancellationToken);
			return new ChatTombstone(message, message != null && updated == 0);
		}

		// Stores the report with a snapshot of the message and the messages before it in the same conversation
		public static async Task<ChatReportResult> CreateReport(AppDbContext db, ChatMessage message, long reporterId, string? reason, CancellationToken cancellationToken = default)
		{
			if (reason != null && reason.Length > MaximumReasonLength)
			{
				throw new ArgumentOutOfRangeException(nameof(reason));
			}

			if (await db.ChatReports.AnyAsync(r => r.MessageID == message.ID && r.ReporterID == reporterId, cancellationToken))
			{
				return ChatReportResult.Duplicate;
			}

			List<ChatMessage> context = await ConversationOf(db, message).Where(m => m.ID < message.ID).OrderByDescending(m => m.ID).Take(SnapshotContextMessages).ToListAsync(cancellationToken);
			context.Reverse();

			ChatReportSnapshot snapshot = new(ToSnapshot(message), context.Select(ToSnapshot).ToList(), new List<ChatSnapshotMessage>(), null);

			db.ChatReports.Add(new ChatReport
			{
				MessageID = message.ID,
				ReporterID = reporterId,
				Reason = reason,
				Snapshot = JsonSerializer.Serialize(snapshot, s_snapshotJson),
				Status = ChatReportStatuses.Open,
				CreatedAt = TruncateToMicroseconds(DateTime.UtcNow)
			});

			try
			{
				await db.SaveChangesAsync(cancellationToken);
				return ChatReportResult.Created;
			}
			catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex))
			{
				return ChatReportResult.Duplicate;
			}
			finally
			{
				db.ChangeTracker.Clear();
			}
		}

		// Every message of the same conversation as the given one: same channel, same lobby or the same direct pair
		public static IQueryable<ChatMessage> ConversationOf(AppDbContext db, ChatMessage message)
		{
			IQueryable<ChatMessage> conversation = db.ChatMessages.AsNoTracking();
			return message.Kind switch
			{
				ChatMessageKinds.Channel => conversation.Where(m => m.Kind == ChatMessageKinds.Channel && m.ChannelID == message.ChannelID),
				ChatMessageKinds.Lobby => conversation.Where(m => m.Kind == ChatMessageKinds.Lobby && m.LobbyID == message.LobbyID),
				ChatMessageKinds.Group => conversation.Where(m => m.Kind == ChatMessageKinds.Group && m.GroupID == message.GroupID),
				_ => conversation.Where(m => m.Kind == ChatMessageKinds.Direct && ((m.SenderID == message.SenderID && m.RecipientID == message.RecipientID) || (m.SenderID == message.RecipientID && m.RecipientID == message.SenderID)))
			};
		}

		// Up to 10 messages after the given one in its conversation, oldest first
		public static async Task<List<ChatMessage>> GetMessagesAfter(AppDbContext db, ChatMessage message, CancellationToken cancellationToken = default)
		{
			return await ConversationOf(db, message).Where(m => m.ID > message.ID).OrderBy(m => m.ID).Take(SnapshotContextMessages).ToListAsync(cancellationToken);
		}

		public static async Task<List<ChatReport>> GetReports(AppDbContext db, string status, int limit, CancellationToken cancellationToken = default)
		{
			return await db.ChatReports.AsNoTracking().Where(r => r.Status == status).OrderBy(r => r.ID).Take(limit).ToListAsync(cancellationToken);
		}

		public static async Task<ChatReport?> GetReport(AppDbContext db, long reportId, CancellationToken cancellationToken = default)
		{
			return await db.ChatReports.AsNoTracking().FirstOrDefaultAsync(r => r.ID == reportId, cancellationToken);
		}

		// Closes an open report and returns false when it was not open, the purge removes it 30 days later
		public static async Task<bool> CloseReport(AppDbContext db, long reportId, CancellationToken cancellationToken = default)
		{
			DateTime now = TruncateToMicroseconds(DateTime.UtcNow);
			int updated = await db.ChatReports
				.Where(r => r.ID == reportId && r.Status == ChatReportStatuses.Open)
				.ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ChatReportStatuses.Closed).SetProperty(r => r.ClosedAt, now), cancellationToken);
			return updated > 0;
		}

		// True when the stored snapshot still waits for the purge to freeze its later messages
		public static bool SnapshotAfterPending(string snapshot)
		{
			ChatReportSnapshot? parsed = ParseSnapshot(snapshot);
			return parsed != null && parsed.AfterFilledAt == null;
		}

		// Runs hourly with the other retention purges, open reports are never purged
		public static async Task<ChatPurgeCounts> PurgeExpired(AppDbContext db, DateTime utcNow, CancellationToken cancellationToken = default)
		{
			DateTime messageCutoff = utcNow - MessageRetention;
			DateTime reportCutoff = utcNow - ClosedReportRetention;

			await FillReportsAfter(db, messageCutoff, utcNow, cancellationToken);

			int messages = await db.ChatMessages.Where(m => m.CreatedAt < messageCutoff).ExecuteDeleteAsync(cancellationToken);

			// A marker untouched for the retention period only points at purged messages
			int markers = await db.ChatReadMarkers.Where(r => r.UpdatedAt < messageCutoff).ExecuteDeleteAsync(cancellationToken);

			int reports = await db.ChatReports
				.Where(r => r.Status == ChatReportStatuses.Closed && r.ClosedAt != null && r.ClosedAt < reportCutoff)
				.ExecuteDeleteAsync(cancellationToken);

			return new ChatPurgeCounts(messages, markers, reports);
		}

		// Account deletion removes rows instead of anonymising them: messages the user sent, the user's read markers, closed reports and group memberships
		// Open reports keep their snapshot until the case is closed, then the normal purge removes them
		// A group the user owned passes to its earliest member, a group the user was alone in is deleted
		public static async Task<int> DeleteUserData(AppDbContext db, long userId, CancellationToken cancellationToken = default)
		{
			string conversationWithUser = DirectConversationKey(userId);

			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

			int removed = await db.ChatMessages.Where(m => m.SenderID == userId).ExecuteDeleteAsync(cancellationToken);
			removed += await db.ChatReadMarkers.Where(r => r.UserID == userId || r.Conversation == conversationWithUser).ExecuteDeleteAsync(cancellationToken);
			removed += await db.ChatReports.Where(r => r.ReporterID == userId && r.Status == ChatReportStatuses.Closed).ExecuteDeleteAsync(cancellationToken);

			List<long> groups = await db.ChatGroupMembers.AsNoTracking().Where(m => m.UserID == userId).Select(m => m.GroupID).ToListAsync(cancellationToken);
			foreach (long groupId in groups)
			{
				if ((await LeaveGroupCore(db, groupId, userId, cancellationToken)).WasMember)
				{
					removed++;
				}
			}

			await transaction.CommitAsync(cancellationToken);
			return removed;
		}

		// Creates the group with the owner first and the members in the given order
		public static async Task<GroupInfo> CreateGroup(AppDbContext db, long ownerId, string? name, IReadOnlyList<long> memberIds, CancellationToken cancellationToken = default)
		{
			if (name != null && (name.Length == 0 || name.Length > MaximumGroupNameLength))
			{
				throw new ArgumentOutOfRangeException(nameof(name));
			}

			DateTime now = TruncateToMicroseconds(DateTime.UtcNow);
			List<long> members = new() { ownerId };
			members.AddRange(memberIds.Where(id => id != ownerId).Distinct());

			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
			ChatGroup group = new() { Name = name, OwnerID = ownerId, CreatedAt = now };
			try
			{
				db.ChatGroups.Add(group);
				await db.SaveChangesAsync(cancellationToken);

				// one save per member keeps the ids in join order on both providers
				foreach (long userId in members)
				{
					db.ChatGroupMembers.Add(new ChatGroupMember { GroupID = group.ID, UserID = userId, JoinedAt = now });
					await db.SaveChangesAsync(cancellationToken);
				}
			}
			finally
			{
				db.ChangeTracker.Clear();
			}

			await transaction.CommitAsync(cancellationToken);
			return new GroupInfo(group, members);
		}

		public static async Task<GroupInfo?> GetGroup(AppDbContext db, long groupId, CancellationToken cancellationToken = default)
		{
			ChatGroup? group = await db.ChatGroups.AsNoTracking().FirstOrDefaultAsync(g => g.ID == groupId, cancellationToken);
			if (group == null)
			{
				return null;
			}

			List<long> members = await db.ChatGroupMembers.AsNoTracking().Where(m => m.GroupID == groupId).OrderBy(m => m.ID).Select(m => m.UserID).ToListAsync(cancellationToken);
			return new GroupInfo(group, members);
		}

		// Every group the user is in, oldest membership first
		public static async Task<List<GroupInfo>> GetGroupsOf(AppDbContext db, long userId, CancellationToken cancellationToken = default)
		{
			List<long> groupIds = await db.ChatGroupMembers.AsNoTracking().Where(m => m.UserID == userId).OrderBy(m => m.ID).Select(m => m.GroupID).ToListAsync(cancellationToken);
			if (groupIds.Count == 0)
			{
				return new List<GroupInfo>();
			}

			Dictionary<long, ChatGroup> groups = await db.ChatGroups.AsNoTracking().Where(g => groupIds.Contains(g.ID)).ToDictionaryAsync(g => g.ID, cancellationToken);
			var members = await db.ChatGroupMembers.AsNoTracking().Where(m => groupIds.Contains(m.GroupID)).OrderBy(m => m.ID).Select(m => new { m.GroupID, m.UserID }).ToListAsync(cancellationToken);
			ILookup<long, long> byGroup = members.ToLookup(m => m.GroupID, m => m.UserID);

			return groupIds.Where(groups.ContainsKey).Select(id => new GroupInfo(groups[id], byGroup[id].ToList())).ToList();
		}

		public static async Task<bool> IsGroupMember(AppDbContext db, long groupId, long userId, CancellationToken cancellationToken = default)
		{
			return await db.ChatGroupMembers.AnyAsync(m => m.GroupID == groupId && m.UserID == userId, cancellationToken);
		}

		// Adds the users in the given order and skips current members, the caller checks the size limit
		public static async Task AddGroupMembers(AppDbContext db, long groupId, IReadOnlyList<long> userIds, CancellationToken cancellationToken = default)
		{
			DateTime now = TruncateToMicroseconds(DateTime.UtcNow);
			HashSet<long> current = (await db.ChatGroupMembers.AsNoTracking().Where(m => m.GroupID == groupId).Select(m => m.UserID).ToListAsync(cancellationToken)).ToHashSet();
			try
			{
				foreach (long userId in userIds.Distinct().Where(id => !current.Contains(id)))
				{
					db.ChatGroupMembers.Add(new ChatGroupMember { GroupID = groupId, UserID = userId, JoinedAt = now });
					await db.SaveChangesAsync(cancellationToken);
				}
			}
			finally
			{
				db.ChangeTracker.Clear();
			}
		}

		// Null clears the name, returns false when the group does not exist
		public static async Task<bool> RenameGroup(AppDbContext db, long groupId, string? name, CancellationToken cancellationToken = default)
		{
			if (name != null && (name.Length == 0 || name.Length > MaximumGroupNameLength))
			{
				throw new ArgumentOutOfRangeException(nameof(name));
			}

			return await db.ChatGroups.Where(g => g.ID == groupId).ExecuteUpdateAsync(s => s.SetProperty(g => g.Name, name), cancellationToken) > 0;
		}

		// Removes one member, passes ownership to the earliest member left and deletes the group when nobody is left
		public static async Task<GroupLeaveResult> LeaveGroup(AppDbContext db, long groupId, long userId, CancellationToken cancellationToken = default)
		{
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
			GroupLeaveResult result = await LeaveGroupCore(db, groupId, userId, cancellationToken);
			await transaction.CommitAsync(cancellationToken);
			return result;
		}

		private static async Task<GroupLeaveResult> LeaveGroupCore(AppDbContext db, long groupId, long userId, CancellationToken cancellationToken)
		{
			int removed = await db.ChatGroupMembers.Where(m => m.GroupID == groupId && m.UserID == userId).ExecuteDeleteAsync(cancellationToken);
			if (removed == 0)
			{
				return new GroupLeaveResult(false, await GetGroup(db, groupId, cancellationToken));
			}

			// a member who left keeps no read position, the history is theirs again only when added back
			string key = GroupConversationKey(groupId);
			await db.ChatReadMarkers.Where(r => r.UserID == userId && r.Conversation == key).ExecuteDeleteAsync(cancellationToken);

			long? earliest = await db.ChatGroupMembers.AsNoTracking().Where(m => m.GroupID == groupId).OrderBy(m => m.ID).Select(m => (long?)m.UserID).FirstOrDefaultAsync(cancellationToken);
			if (earliest == null)
			{
				// the stored messages stay until the normal retention purge
				await db.ChatGroups.Where(g => g.ID == groupId).ExecuteDeleteAsync(cancellationToken);
				return new GroupLeaveResult(true, null);
			}

			long newOwner = earliest.Value;
			await db.ChatGroups.Where(g => g.ID == groupId && g.OwnerID == userId).ExecuteUpdateAsync(s => s.SetProperty(g => g.OwnerID, newOwner), cancellationToken);
			return new GroupLeaveResult(true, await GetGroup(db, groupId, cancellationToken));
		}

		// The last visible message and the unread count past the read marker of every given group
		public static async Task<List<GroupConversation>> GetGroupConversations(AppDbContext db, long userId, IReadOnlyCollection<long> groupIds, IReadOnlyCollection<long>? excludeSenderIds = null, CancellationToken cancellationToken = default)
		{
			Dictionary<string, long> markers = await GetReadMarkers(db, userId, GroupConversationPrefix, cancellationToken);
			List<GroupConversation> result = new(groupIds.Count);
			foreach (long groupId in groupIds)
			{
				IQueryable<ChatMessage> visible = VisibleGroupMessages(db, groupId, excludeSenderIds);
				ChatMessage? last = await visible.OrderByDescending(m => m.ID).FirstOrDefaultAsync(cancellationToken);

				int unread = 0;
				if (last != null)
				{
					long readUpTo = markers.GetValueOrDefault(GroupConversationKey(groupId));
					unread = await visible.CountAsync(m => m.ID > readUpTo && m.SenderID != userId && m.DeletedAt == null, cancellationToken);
				}
				result.Add(new GroupConversation(groupId, unread, last));
			}
			return result;
		}

		// True when the sender holds a stored direct message to the recipient, tombstones included
		public static async Task<bool> HasDirectMessage(AppDbContext db, long senderId, long recipientId, CancellationToken cancellationToken = default)
		{
			return await db.ChatMessages.AnyAsync(m => m.Kind == ChatMessageKinds.Direct && m.SenderID == senderId && m.RecipientID == recipientId, cancellationToken);
		}

		private const int FillBatchSize = 100;

		// Freezes the later messages of every open report whose reported message is about to be purged, in batches by report id
		private static async Task FillReportsAfter(AppDbContext db, DateTime messageCutoff, DateTime utcNow, CancellationToken cancellationToken)
		{
			DateTime filledAt = TruncateToMicroseconds(utcNow);
			long lastReportId = 0;

			while (true)
			{
				var batch = await (
					from r in db.ChatReports.AsNoTracking()
					join m in db.ChatMessages.AsNoTracking() on r.MessageID equals m.ID
					where r.Status == ChatReportStatuses.Open && r.ID > lastReportId && m.CreatedAt < messageCutoff
					orderby r.ID
					select new { Report = r, Message = m })
					.Take(FillBatchSize)
					.ToListAsync(cancellationToken);

				if (batch.Count == 0)
				{
					return;
				}

				foreach (var row in batch)
				{
					ChatReportSnapshot? snapshot = ParseSnapshot(row.Report.Snapshot);
					if (snapshot == null || snapshot.AfterFilledAt != null)
					{
						continue;
					}

					List<ChatMessage> after = await GetMessagesAfter(db, row.Message, cancellationToken);
					string filled = JsonSerializer.Serialize(snapshot with { After = after.Select(ToSnapshot).ToList(), AfterFilledAt = filledAt }, s_snapshotJson);
					long reportId = row.Report.ID;
					await db.ChatReports.Where(r => r.ID == reportId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Snapshot, filled), cancellationToken);
				}

				lastReportId = batch[^1].Report.ID;
			}
		}

		private static ChatReportSnapshot? ParseSnapshot(string snapshot)
		{
			try
			{
				return JsonSerializer.Deserialize<ChatReportSnapshot>(snapshot, s_snapshotJson);
			}
			catch (JsonException)
			{
				return null;
			}
		}

		private static ChatPage ToPage(List<ChatMessage> newestFirst, int limit)
		{
			bool hasMore = newestFirst.Count > limit;
			if (hasMore)
			{
				newestFirst.RemoveRange(limit, newestFirst.Count - limit);
			}

			newestFirst.Reverse();
			return new ChatPage(newestFirst, hasMore);
		}

		// Both providers store microseconds, so the returned row matches what a later read returns
		private static DateTime TruncateToMicroseconds(DateTime value)
		{
			return new DateTime(value.Ticks - (value.Ticks % 10), DateTimeKind.Utc);
		}

		private static readonly JsonSerializerOptions s_snapshotJson = new()
		{
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
		};

		private static ChatSnapshotMessage ToSnapshot(ChatMessage m)
		{
			return new ChatSnapshotMessage(m.ID, m.Kind, m.ChannelID, m.LobbyID, m.MatchID, m.SenderID, m.RecipientID, m.GroupID, m.Type, m.Content, m.CreatedAt, m.DeletedAt);
		}

		private sealed record ChatReportSnapshot(
			[property: JsonPropertyName("message")] ChatSnapshotMessage Message,
			[property: JsonPropertyName("before")] List<ChatSnapshotMessage> Before,
			[property: JsonPropertyName("after")] List<ChatSnapshotMessage>? After,
			[property: JsonPropertyName("after_filled_at"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? AfterFilledAt);

		private sealed record ChatSnapshotMessage(
			[property: JsonPropertyName("id")] long ID,
			[property: JsonPropertyName("kind")] string Kind,
			[property: JsonPropertyName("channel_id")] long? ChannelID,
			[property: JsonPropertyName("lobby_id")] long? LobbyID,
			[property: JsonPropertyName("match_id")] long? MatchID,
			[property: JsonPropertyName("sender_id")] long SenderID,
			[property: JsonPropertyName("recipient_id")] long? RecipientID,
			[property: JsonPropertyName("group_id")] long? GroupID,
			[property: JsonPropertyName("type")] string Type,
			[property: JsonPropertyName("content")] string Content,
			[property: JsonPropertyName("created_at")] DateTime CreatedAt,
			[property: JsonPropertyName("deleted_at")] DateTime? DeletedAt);
	}
}
