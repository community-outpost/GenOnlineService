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

using GenOnlineService.Identity;
using System.Globalization;

namespace GenOnlineService.Realtime
{
	// DeleteMessage (section 5.5) and ReportMessage (section 5.1)
	public sealed partial class RealtimeHub
	{
		public async Task<Result<Empty>> DeleteMessage(MessageActionRequest request)
		{
			Failure? failure = await RequirePermissionAsync(Permissions.ChatDeleteAny);
			if (failure != null)
			{
				return failure.Value;
			}
			if (request.Reason != null && request.Reason.Length > ModerationManager.MaximumReasonLength)
			{
				return ReasonTooLong();
			}
			if (!TryParseMessageID(request.MessageId, out Int64 messageID))
			{
				return Result.Invalid("message_id");
			}

			ChatMessage row;
			List<long> groupMembers = new();
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				// staff delete their own messages and those of lower ranked users only, checked before any change
				ChatMessage? existing = await Database.Chat.GetMessage(db, messageID);
				if (existing == null)
				{
					return Result.NotFound("message");
				}
				if (existing.SenderID != UserID && !StaffRoles.CanModerate(UserID, existing.SenderID))
				{
					return Result.Fail(ErrorCodes.ModerationTargetOutranks);
				}

				Database.ChatTombstone tombstone = await Database.Chat.TombstoneMessage(db, messageID);
				if (tombstone.Message == null)
				{
					return Result.NotFound("message");
				}
				if (tombstone.AlreadyDeleted)
				{
					return Result.Success();
				}
				row = tombstone.Message;

				// ids only, the audit row never holds the chat text
				object details = new { kind = row.Kind, sender_id = row.SenderID, channel_id = row.ChannelID, lobby_id = row.LobbyID, recipient_id = row.RecipientID, group_id = row.GroupID };
				await Database.Moderation.LogAction(db, Actor(), ModerationActions.MessageDelete, AuditTargetKinds.Message, messageID, request.Reason, details);

				if (row.Kind == ChatMessageKinds.Group)
				{
					groupMembers = (await Database.Chat.GetGroup(db, row.GroupID ?? -1))?.MemberIDs ?? new List<long>();
				}
			}

			string id = messageID.ToString(CultureInfo.InvariantCulture);
			switch (row.Kind)
			{
				case ChatMessageKinds.Channel:
				{
					string channel = Database.Chat.ChannelConversationKey(row.ChannelID ?? -1);
					await RealtimeOut.Group(RealtimeGroups.Channel(row.ChannelID ?? -1)).MessageDelete(new MessageRefEvent { ChannelId = channel, MessageId = id });
					break;
				}
				case ChatMessageKinds.Lobby:
				{
					Int64 lobbyID = row.LobbyID ?? -1;
					await RealtimeOut.Lobby(lobbyID).MessageDelete(new MessageRefEvent { ChannelId = RealtimeGroups.Lobby(lobbyID), MessageId = id });
					break;
				}
				case ChatMessageKinds.Group:
				{
					// the current members, a member who left lost the history with the group
					await RealtimeOut.Users(groupMembers).MessageDelete(new MessageRefEvent { ChannelId = Database.Chat.GroupConversationKey(row.GroupID ?? -1), MessageId = id });
					break;
				}
				default:
				{
					Int64 recipient = row.RecipientID ?? -1;
					await RealtimeOut.User(row.SenderID).MessageDelete(new MessageRefEvent { ChannelId = Database.Chat.DirectConversationKey(recipient), MessageId = id });
					await RealtimeOut.User(recipient).MessageDelete(new MessageRefEvent { ChannelId = Database.Chat.DirectConversationKey(row.SenderID), MessageId = id });
					break;
				}
			}
			return Result.Success();
		}

		public async Task<Result<Empty>> ReportMessage(MessageActionRequest request)
		{
			if (request.Reason != null && request.Reason.Length > Database.Chat.MaximumReasonLength)
			{
				return Result.Fail(ErrorCodes.ModerationReasonTooLong, new { Max = Database.Chat.MaximumReasonLength });
			}
			if (!TryParseMessageID(request.MessageId, out Int64 messageID))
			{
				return Result.Invalid("message_id");
			}

			Int64 me = UserID;
			if (!ReportRateLimiter.TryConsume(me, out long retryAfterMs))
			{
				return Result.Fail(ErrorCodes.RateLimited, new { RetryAfterMs = retryAfterMs });
			}

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			ChatMessage? row = await Database.Chat.GetMessage(db, messageID);

			// a message the caller never received answers like an unknown one, so reports cannot probe for messages
			bool received = row != null && (row.Kind == ChatMessageKinds.Group
				? row.SenderID != me && await Database.Chat.IsGroupMember(db, row.GroupID ?? -1, me)
				: ReceivedBy(row, me));
			if (row == null || !received || ((await BlockedEitherWayAsync(db, me)).Contains(row.SenderID) && !(row.Kind == ChatMessageKinds.Direct && IsStaffConversation(me, row.SenderID))))
			{
				return Result.NotFound("message");
			}

			string? reason = String.IsNullOrEmpty(request.Reason) ? null : request.Reason;
			Database.ChatReportResult result = await Database.Chat.CreateReport(db, row, me, reason);
			return result == Database.ChatReportResult.Duplicate ? Result.Fail(ErrorCodes.ReportDuplicate) : Result.Success();
		}

		// Who counts as having received a message, members who left since still count
		// Direct: the recipient. Channel: any user, channel history is public through GetHistory. Group: any other current member, checked by the caller
		// Lobby: a member when lobby chat was sent (kept 24 h after the lobby's last message), a current member, or a player of its match
		// The author never reports their own message, and a block between author and caller means it was never delivered, the caller checks blocks
		private static bool ReceivedBy(ChatMessage row, Int64 userID)
		{
			if (row.SenderID == userID)
			{
				return false;
			}

			switch (row.Kind)
			{
				case ChatMessageKinds.Direct:
					return row.RecipientID == userID;
				case ChatMessageKinds.Channel:
					return true;
				case ChatMessageKinds.Lobby:
				{
					Int64 lobbyID = row.LobbyID ?? -1;
					if (LobbyChatAudience.WasMember(lobbyID, userID))
					{
						return true;
					}

					List<UserSession> sessions = WebSocketManager.GetAllDataFromUser(userID);
					return sessions.Any(s => s.currentLobbyID != -1 && s.currentLobbyID == lobbyID)
						|| (row.MatchID is Int64 matchID && matchID > 0 && sessions.Any(s => s.WasPlayerInMatch((UInt64)matchID, out _, out _, out _)));
				}
				default:
					return false;
			}
		}
	}
}
