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
using GenOnlineService.Identity;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace GenOnlineService.Realtime
{
	// Staff message deletes, reports of messages and players and the staff report queue (section 10.2)
	public static class MessageActions
	{
		private const int EvidenceMessages = 10;
		private const int EvidenceCandidates = 200;
		private const int ReportListLimit = 100;

		// One player report per reporter and player at a time on this copy, so the open report check holds
		private static readonly SemaphoreSlim[] s_reportLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

		public static async Task<Result<Empty>> DeleteAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, string messageText, string? reason, Func<string, Task<Failure?>> requirePermission)
		{
			Failure? failure = await requirePermission(Permissions.ChatDeleteAny);
			if (failure != null)
			{
				return failure.Value;
			}
			if (reason != null && reason.Length > ModerationManager.MaximumReasonLength)
			{
				return SanctionActions.ReasonTooLong();
			}
			if (!RealtimeHub.TryParseMessageID(messageText, out Int64 messageID))
			{
				return Result.Invalid("id");
			}

			ChatMessage row;
			List<long> groupMembers = new();
			await using (AppDbContext db = await dbFactory.CreateDbContextAsync())
			{
				// staff delete their own messages and those of lower ranked users only, checked before any change
				ChatMessage? existing = await Database.Chat.GetMessage(db, messageID);
				if (existing == null)
				{
					return Result.NotFound("message");
				}
				if (existing.SenderID != me && !StaffRoles.CanModerate(me, existing.SenderID))
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
				await Database.Moderation.LogAction(db, SanctionActions.ApiActorOf(me), ModerationActions.MessageDelete, AuditTargetKinds.Message, messageID, reason, details);

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

		// A report names exactly one of a message and a player, both kinds share the report rate limit
		public static async Task<Result<CreateReportResponse>> ReportAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, CreateReportRequest request)
		{
			if (request.Reason != null && request.Reason.Length > Database.Chat.MaximumReasonLength)
			{
				return Result.Fail(ErrorCodes.ModerationReasonTooLong, new { Max = Database.Chat.MaximumReasonLength });
			}
			if (request.MessageId != null && request.UserId != null)
			{
				return Result.Invalid("user_id");
			}

			if (request.UserId is long player)
			{
				return await ReportPlayerAsync(dbFactory, me, player, request.Reason);
			}

			if (!RealtimeHub.TryParseMessageID(request.MessageId, out Int64 messageID))
			{
				return Result.Invalid("message_id");
			}

			if (!ReportRateLimiter.TryConsume(me, out long retryAfterMs))
			{
				return Result.Fail(ErrorCodes.RateLimited, new { RetryAfterMs = retryAfterMs });
			}

			await using AppDbContext db = await dbFactory.CreateDbContextAsync();
			ChatMessage? row = await Database.Chat.GetMessage(db, messageID);

			// a message the caller never received answers like an unknown one, so reports cannot probe for messages
			bool received = row != null && (row.Kind == ChatMessageKinds.Group
				? row.SenderID != me && await Database.Chat.IsGroupMember(db, row.GroupID ?? -1, me)
				: ReceivedBy(row, me));
			if (row == null || !received || ((await RealtimeHub.BlockedEitherWayAsync(db, me)).Contains(row.SenderID) && !(row.Kind == ChatMessageKinds.Direct && RealtimeHub.IsStaffConversation(me, row.SenderID))))
			{
				return Result.NotFound("message");
			}

			string? reason = String.IsNullOrEmpty(request.Reason) ? null : request.Reason;
			(Database.ReportResult result, long reportID) = await Database.Chat.CreateReport(db, row, me, reason);
			return result == Database.ReportResult.Duplicate ? Result.Fail(ErrorCodes.ReportDuplicate) : Result.Success(new CreateReportResponse { ReportId = reportID });
		}

		// Any other user can be reported, one open report per reporter and player
		private static async Task<Result<CreateReportResponse>> ReportPlayerAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, Int64 player, string? reason)
		{
			if (player <= 0 || player == me)
			{
				return Result.Invalid("user_id");
			}

			if (!ReportRateLimiter.TryConsume(me, out long retryAfterMs))
			{
				return Result.Fail(ErrorCodes.RateLimited, new { RetryAfterMs = retryAfterMs });
			}

			await using AppDbContext db = await dbFactory.CreateDbContextAsync();
			if (!await Database.Users.Exists(db, player))
			{
				return Result.NotFound("user");
			}

			SemaphoreSlim gate = s_reportLocks[(int)((UInt64)HashCode.Combine(me, player) % (UInt64)s_reportLocks.Length)];
			await gate.WaitAsync();
			try
			{
				if (await Database.Chat.HasOpenPlayerReport(db, me, player))
				{
					return Result.Fail(ErrorCodes.ReportDuplicate);
				}

				List<ChatMessage> evidence = await EvidenceAsync(db, me, player);
				long reportID = await Database.Chat.CreatePlayerReport(db, me, player, String.IsNullOrEmpty(reason) ? null : reason, evidence);
				return Result.Success(new CreateReportResponse { ReportId = reportID });
			}
			finally
			{
				gate.Release();
			}
		}

		// Up to 10 of the player's most recent messages the reporter could see, oldest first
		// Channel and lobby messages follow ReceivedBy, group messages need the reporter in the group, a block either way hides both like history does
		// Direct messages are those the player sent to the reporter
		private static async Task<List<ChatMessage>> EvidenceAsync(AppDbContext db, Int64 me, Int64 player)
		{
			bool blocked = (await RealtimeHub.BlockedEitherWayAsync(db, me)).Contains(player);
			HashSet<long> groups = (await Database.Chat.GetGroupsOf(db, me)).Select(g => g.Group.ID).ToHashSet();
			List<ChatMessage> recent = await Database.Chat.GetRecentMessagesBy(db, player, EvidenceCandidates);

			List<ChatMessage> seen = recent.Where(m => m.Kind switch
			{
				ChatMessageKinds.Direct => m.RecipientID == me,
				ChatMessageKinds.Group => !blocked && groups.Contains(m.GroupID ?? -1),
				_ => !blocked && ReceivedBy(m, me)
			}).Take(EvidenceMessages).ToList();
			seen.Reverse();
			return seen;
		}

		// The staff queue, oldest first, a null status lists every report
		public static async Task<ReportsResponse> ListReportsAsync(IDbContextFactory<AppDbContext> dbFactory, string? status)
		{
			await using AppDbContext db = await dbFactory.CreateDbContextAsync();
			List<PlayerReport> rows = await Database.Chat.GetReports(db, status, ReportListLimit);
			Dictionary<long, (string Name, int Tag)> names = await RealtimeHub.NamesAsync(db, rows.SelectMany(Rest.StaffOverview.ReportUserIDs));
			return new ReportsResponse { Reports = rows.Select(r => Rest.StaffOverview.ReportOf(r, names)).ToArray() };
		}

		// Resolves an open report as actioned or dismissed with an audit row, a report resolved before stays as it is
		public static async Task<Result<Empty>> ResolveReportAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, Int64 reportID, ResolveReportRequest request)
		{
			if (request.Status != PlayerReportStatuses.Actioned && request.Status != PlayerReportStatuses.Dismissed)
			{
				return Result.Invalid("status");
			}
			if (request.ResolutionNote != null && request.ResolutionNote.Length > Database.Chat.MaximumReasonLength)
			{
				return Result.Fail(ErrorCodes.ModerationReasonTooLong, new { Max = Database.Chat.MaximumReasonLength });
			}

			await using AppDbContext db = await dbFactory.CreateDbContextAsync();
			if (reportID <= 0 || await Database.Chat.GetReport(db, reportID) == null)
			{
				return Result.NotFound("report");
			}

			Database.ModerationActor actor = SanctionActions.ApiActorOf(me);
			if (await Database.Chat.ResolveReport(db, reportID, request.Status, request.ResolutionNote, actor.Kind, actor.UserID))
			{
				await Database.Moderation.LogAction(db, actor, ModerationActions.ReportClose, AuditTargetKinds.Report, reportID, request.ResolutionNote, new { status = request.Status });
			}
			return Result.Success();
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
