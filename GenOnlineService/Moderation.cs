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
			if (target == null)
			{
				AppMetrics.RecordSecurityAction(EModerationAction.Kick.ToMetricLabel(), "target_offline");
				return new ModerationResult { Result = EModerationResult.TargetNotOnline };
			}

			await DisconnectUser(targetUserID, EModerationAction.Kick, reason);

			using (var scope = ServiceLocator.Services.CreateScope())
			{
				var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
				await using var db = await factory.CreateDbContextAsync();
				await Database.Moderation.LogUserAction(db, actor, ModerationActions.Kick, targetUserID, reason);
			}

			return new ModerationResult
			{
				Result = EModerationResult.Success,
				// staff see Name#tag: the name alone may belong to several players
				TargetDisplayName = Database.DisplayNames.Format(target.m_strDisplayName, target.m_NameTag)
			};
		}

		// Shows the notice, then closes with moderation.kicked (reconnect allowed) or moderation.banned
		public static async Task DisconnectUser(Int64 userID, EModerationAction action, string? reason, DateTime? until = null, EUserSessionType? onlySessionType = null)
		{
			AppMetrics.RecordSecurityAction(action.ToMetricLabel(), "applied");
			s_log.LogInformation("Applying moderation action {Action} to user {UserId}", action, userID);
			(ModerationAction noticeAction, string closeCode, bool allowReconnect) = action switch
			{
				EModerationAction.Ban => (ModerationAction.Ban, CloseCodes.ModerationBanned, false),
				EModerationAction.Kick => (ModerationAction.Kick, CloseCodes.ModerationKicked, true),
				_ => throw new ArgumentOutOfRangeException(nameof(action))
			};
			ModerationNoticeEvent notice = new ModerationNoticeEvent { Action = noticeAction, Reason = reason ?? String.Empty, Until = until };

			await WebSocketManager.DisconnectUser(userID, closeCode, allowReconnect, notice, onlySessionType);
		}
	}
}
