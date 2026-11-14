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

namespace GenOnlineService.Realtime
{
	// Kick, mute and ban (section 5.5), each checked with its perm:<permission> policy and written to audit_log
	public sealed partial class RealtimeHub
	{
		public async Task<Result<Empty>> Kick(ModerationRequest request)
		{
			Failure? failure = await RequirePermissionAsync(Permissions.ModerationKick) ?? CheckModeration(request.UserId, request.Reason);
			if (failure != null)
			{
				return failure.Value;
			}

			if (WebSocketManager.GetSharedDataForUser(request.UserId) == null)
			{
				await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
				return await Database.Users.Exists(db, request.UserId) ? Result.Fail(ErrorCodes.ModerationTargetOffline) : Result.NotFound("user");
			}

			ModerationResult result = await ModerationManager.KickUser(request.UserId, request.Reason, Actor());
			return result.Result switch
			{
				EModerationResult.Success => Result.Success(),
				EModerationResult.ReasonTooLong => ReasonTooLong(),
				_ => Result.Fail(ErrorCodes.ModerationTargetOffline)
			};
		}

		public async Task<Result<Empty>> Mute(MuteRequest request)
		{
			Failure? failure = await RequirePermissionAsync(Permissions.ModerationMute) ?? CheckModeration(request.UserId, request.Reason);
			if (failure != null)
			{
				return failure.Value;
			}
			if (request.DurationMs <= 0)
			{
				return Result.Invalid("duration_ms");
			}

			DateTime until = DateTime.UtcNow.AddMilliseconds(request.DurationMs);
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				if (!await Database.Users.Exists(db, request.UserId))
				{
					return Result.NotFound("user");
				}
				await Database.Moderation.AddBan(db, request.UserId, BanRestrictions.Chat, request.Reason, until, Actor());
			}

			await RealtimeOut.User(request.UserId).ModerationNotice(new ModerationNoticeEvent { Action = ModerationAction.Mute, Reason = request.Reason, Until = until });
			return Result.Success();
		}

		public Task<Result<Empty>> Unmute(ModerationRequest request) => RevokeAsync(request, Permissions.ModerationMute, BanRestrictions.Chat, ModerationActions.Unmute);

		public async Task<Result<Empty>> Ban(BanRequest request)
		{
			Failure? failure = await RequirePermissionAsync(Permissions.ModerationBan) ?? CheckModeration(request.UserId, request.Reason);
			if (failure != null)
			{
				return failure.Value;
			}
			if (request.DurationMs is <= 0)
			{
				return Result.Invalid("duration_ms");
			}

			DateTime? until = request.DurationMs == null ? null : DateTime.UtcNow.AddMilliseconds(request.DurationMs.Value);
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				if (!await Database.Users.Exists(db, request.UserId))
				{
					return Result.NotFound("user");
				}
				await Database.Moderation.AddBan(db, request.UserId, BanRestrictions.Play, request.Reason, until, Actor());

				// the reload refuses new connections and revokes and closes the game sessions at once
				await AccessControl.Reload(db);
			}
			return Result.Success();
		}

		public Task<Result<Empty>> Unban(ModerationRequest request) => RevokeAsync(request, Permissions.ModerationBan, BanRestrictions.Play, ModerationActions.Unban);

		private async Task<Result<Empty>> RevokeAsync(ModerationRequest request, string permission, string restriction, string action)
		{
			Failure? failure = await RequirePermissionAsync(permission) ?? CheckModeration(request.UserId, request.Reason);
			if (failure != null)
			{
				return failure.Value;
			}

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			if (!await Database.Users.Exists(db, request.UserId))
			{
				return Result.NotFound("user");
			}

			// a revoke with nothing active still reaches the audit log
			if (await Database.Moderation.RevokeActiveBans(db, request.UserId, restriction, request.Reason, Actor()) == 0)
			{
				await Database.Moderation.LogUserAction(db, Actor(), action, request.UserId, request.Reason);
			}
			if (restriction == BanRestrictions.Play)
			{
				// lets the player connect again without waiting for the reload tick
				await AccessControl.Reload(db);
			}
			return Result.Success();
		}

		private Failure? CheckModeration(Int64 target, string? reason)
		{
			if (target <= 0 || target == UserID)
			{
				return Result.Invalid("user_id");
			}
			if (reason == null)
			{
				return Result.Invalid("reason");
			}
			if (reason.Length > ModerationManager.MaximumReasonLength)
			{
				return ReasonTooLong();
			}
			return null;
		}

		private static Failure ReasonTooLong() => Result.Fail(ErrorCodes.ModerationReasonTooLong, new { Max = ModerationManager.MaximumReasonLength });

		private Database.ModerationActor Actor() => new Database.ModerationActor
		{
			Kind = AuditActorKinds.User,
			UserID = UserID,
			Name = $"user:{UserID}",
			Source = ModerationSources.Realtime
		};
	}
}
