/*
**    GeneralsOnline Game Services - Backend Services for Command & Conquer Generals Online: Zero Hour
**    Copyright (C) 2025  GeneralsOnline Development Team
**
**    This program is free software: you can redistribute it and/or modify
**    it under the terms of the GNU Affero General Public License as
**    published by the Free Software Foundation, either version 3 of the
**    License, or (at your option) any later version.
*/

using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

using GenOnlineService.Realtime;

namespace GenOnlineService
{
	public enum EModerationResult
	{
		Success,
		TargetNotOnline,
		ReasonTooLong
	}

	public enum EModerationAction
	{
		Ban,
		Kick
	}

	public sealed class ModerationResult
	{
		public EModerationResult Result { get; init; }
		public string TargetDisplayName { get; init; } = String.Empty;

		// The stored kick, 0 when the kick was not applied
		public Int64 SanctionID { get; init; }
	}

	public static class ModerationManager
	{
		private static readonly ILogger s_log = AppLog.For(typeof(ModerationManager));
		public const int MaximumReasonLength = 512;

		public static async Task<ModerationResult> KickUser(Int64 targetUserID, string reason, Database.ModerationActor actor)
		{
			if (reason.Length > MaximumReasonLength)
			{
				AppMetrics.RecordSecurityAction(EModerationAction.Kick.ToMetricLabel(), "reason_too_long");
				return new ModerationResult { Result = EModerationResult.ReasonTooLong };
			}

			SharedUserData? target = WebSocketManager.GetSharedDataForUser(targetUserID);
			if (target == null && !RealtimeControl.IsShared)
			{
				AppMetrics.RecordSecurityAction(EModerationAction.Kick.ToMetricLabel(), "target_offline");
				return new ModerationResult { Result = EModerationResult.TargetNotOnline };
			}

			string targetDisplayName;
			Sanction kick;
			using (var scope = ServiceLocator.Services.CreateScope())
			{
				var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
				await using var db = await factory.CreateDbContextAsync();

				// The kick is stored before it is applied and kept only when it was applied, so the sanction, the audit row and the kick always agree
				await using var transaction = await db.Database.BeginTransactionAsync();
				kick = await Database.Moderation.AddKick(db, targetUserID, reason, actor);
				// staff see Name#tag: the name alone may belong to several players
				(string Name, int Tag)? stored = target == null ? await Database.DisplayNames.Get(db, targetUserID) : null;
				targetDisplayName = target != null ? Database.DisplayNames.Format(target.m_strDisplayName, target.m_NameTag)
					: stored != null ? Database.DisplayNames.Format(stored.Value.Name, stored.Value.Tag) : String.Empty;

				// with a backplane the target may be online on another copy, which confirms the close
				bool applied = true;
				if (target != null)
				{
					await DisconnectUser(targetUserID, EModerationAction.Kick, reason);
				}
				else
				{
					applied = await DisconnectUser(targetUserID, EModerationAction.Kick, reason, confirmRemote: true);
				}
				if (!applied)
				{
					await transaction.RollbackAsync();
					AppMetrics.RecordSecurityAction(EModerationAction.Kick.ToMetricLabel(), "target_offline");
					return new ModerationResult { Result = EModerationResult.TargetNotOnline };
				}

				await transaction.CommitAsync();
			}

			return new ModerationResult
			{
				Result = EModerationResult.Success,
				TargetDisplayName = targetDisplayName,
				SanctionID = kick.ID
			};
		}

		// Shows the notice, then closes with moderation.kicked (reconnect allowed) or moderation.banned on every copy; true when a copy had a session to close
		public static async Task<bool> DisconnectUser(Int64 userID, EModerationAction action, string? reason, DateTime? expiresAt = null, EUserSessionType? onlySessionType = null, bool confirmRemote = false)
		{
			AppMetrics.RecordSecurityAction(action.ToMetricLabel(), "applied");
			s_log.LogInformation("Applying moderation action {Action} to user {UserId}", action, userID);
			(ModerationAction noticeAction, string closeCode, bool allowReconnect) = action switch
			{
				EModerationAction.Ban => (ModerationAction.Ban, CloseCodes.ModerationBanned, false),
				EModerationAction.Kick => (ModerationAction.Kick, CloseCodes.ModerationKicked, true),
				_ => throw new ArgumentOutOfRangeException(nameof(action))
			};
			ModerationNoticeEvent notice = new ModerationNoticeEvent { Action = noticeAction, Reason = String.IsNullOrEmpty(reason) ? null : reason, ExpiresAt = expiresAt };

			return await RealtimeControl.RequestClose(CloseRequest.ForUser(userID, closeCode, allowReconnect, notice, onlySessionType), confirmRemote);
		}
	}
}
