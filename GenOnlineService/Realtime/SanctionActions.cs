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

namespace GenOnlineService.Realtime
{
	// Kick, mute and ban as sanctions (section 10.2), each checked with its perm:<permission> policy and written to audit_log
	public static class SanctionActions
	{
		public const long MinimumDurationS = 60;
		public const long MaximumDurationS = 315360000;

		public static string PermissionFor(SanctionType type) => type switch
		{
			SanctionType.Kick => Permissions.ModerationKick,
			SanctionType.Mute => Permissions.ModerationMute,
			_ => Permissions.ModerationBan
		};

		// A staff action through the realtime hub
		public static Database.ModerationActor ActorOf(Int64 userID) => new Database.ModerationActor
		{
			Kind = AuditActorKinds.User,
			UserID = userID,
			Name = $"user:{userID}",
			Source = ModerationSources.Realtime
		};

		// A staff action through the REST API, such as these sanction endpoints and the staff rename
		public static Database.ModerationActor ApiActorOf(Int64 userID) => new Database.ModerationActor
		{
			Kind = AuditActorKinds.User,
			UserID = userID,
			Name = $"user:{userID}",
			Source = ModerationSources.Api
		};

		public static async Task<Result<CreateSanctionResponse>> CreateAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, Int64 target, CreateSanctionRequest request, Func<string, Task<Failure?>> requirePermission)
		{
			if (request.Type is not SanctionType type)
			{
				return Result.Invalid("type");
			}

			Failure? failure = await requirePermission(PermissionFor(type));
			if (failure != null)
			{
				return failure.Value;
			}

			return type switch
			{
				SanctionType.Kick => await KickAsync(dbFactory, me, target, request.Reason),
				SanctionType.Mute => await MuteAsync(dbFactory, me, target, request.DurationS, request.Reason),
				_ => await BanAsync(dbFactory, me, target, request.DurationS, request.Reason)
			};
		}

		// A kick, mute or ban by any actor, such as an automated rule. Staff are never sanctioned this way, the reason and
		// the length follow the staff rules, and a ban disconnects the player on every copy through the access reload.
		public static async Task<Result<CreateSanctionResponse>> ApplyAsync(IDbContextFactory<AppDbContext> dbFactory, Database.ModerationActor actor, Int64 target, SanctionType type, long? durationS, string? reason)
		{
			if (target <= 0)
			{
				return Result.Invalid("id");
			}
			if (type == SanctionType.Ban && String.IsNullOrWhiteSpace(reason))
			{
				return Result.Invalid("reason");
			}
			if (reason != null && reason.Length > ModerationManager.MaximumReasonLength)
			{
				return ReasonTooLong();
			}
			Failure? duration = type switch
			{
				SanctionType.Mute => CheckDuration(durationS, required: true),
				SanctionType.Ban => CheckDuration(durationS, required: false),
				_ => null
			};
			if (duration != null)
			{
				return duration.Value;
			}
			await using (AppDbContext db = await dbFactory.CreateDbContextAsync())
			{
				// the roles in the database decide, so a role granted on another copy counts at once
				if (StaffRoles.RankOf(await Database.Moderation.GetRoleNames(db, target)) > 0)
				{
					return Result.Fail(ErrorCodes.ModerationTargetOutranks);
				}
			}

			return type switch
			{
				SanctionType.Kick => await KickAsActorAsync(dbFactory, actor, target, reason),
				SanctionType.Mute => await MuteAsActorAsync(dbFactory, actor, target, DateTime.UtcNow.AddSeconds(durationS!.Value), reason),
				_ => await BanAsActorAsync(dbFactory, actor, target, durationS == null ? null : DateTime.UtcNow.AddSeconds(durationS.Value), reason)
			};
		}

		private static async Task<Result<CreateSanctionResponse>> KickAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, Int64 target, string? reason)
		{
			Failure? failure = CheckModeration(me, target, reason);
			if (failure != null)
			{
				return failure.Value;
			}

			return await KickAsActorAsync(dbFactory, ApiActorOf(me), target, reason);
		}

		private static async Task<Result<CreateSanctionResponse>> KickAsActorAsync(IDbContextFactory<AppDbContext> dbFactory, Database.ModerationActor actor, Int64 target, string? reason)
		{
			// without a backplane only this copy can hold the target, with one another copy may
			if (WebSocketManager.GetSharedDataForUser(target) == null && !RealtimeControl.IsShared)
			{
				return await TargetOfflineAsync(dbFactory, target);
			}

			ModerationResult result = await ModerationManager.KickUser(target, reason ?? String.Empty, actor);
			return result.Result switch
			{
				EModerationResult.Success => Result.Success(new CreateSanctionResponse { SanctionId = result.SanctionID }),
				EModerationResult.ReasonTooLong => ReasonTooLong(),
				_ => await TargetOfflineAsync(dbFactory, target)
			};
		}

		private static async Task<Failure> TargetOfflineAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 target)
		{
			await using AppDbContext db = await dbFactory.CreateDbContextAsync();
			return await Database.Users.Exists(db, target) ? Result.Fail(ErrorCodes.ModerationTargetOffline) : Result.NotFound("user");
		}

		private static async Task<Result<CreateSanctionResponse>> MuteAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, Int64 target, long? durationS, string? reason)
		{
			Failure? failure = CheckModeration(me, target, reason) ?? CheckDuration(durationS, required: true);
			if (failure != null)
			{
				return failure.Value;
			}

			return await MuteAsActorAsync(dbFactory, ApiActorOf(me), target, DateTime.UtcNow.AddSeconds(durationS!.Value), reason);
		}

		private static async Task<Result<CreateSanctionResponse>> MuteAsActorAsync(IDbContextFactory<AppDbContext> dbFactory, Database.ModerationActor actor, Int64 target, DateTime expiresAt, string? reason)
		{
			Sanction mute;
			await using (AppDbContext db = await dbFactory.CreateDbContextAsync())
			{
				if (!await Database.Users.Exists(db, target))
				{
					return Result.NotFound("user");
				}
				mute = await Database.Moderation.AddSanction(db, target, SanctionTypes.Mute, reason, expiresAt, actor);
			}

			ModerationNoticeEvent notice = new ModerationNoticeEvent { Action = ModerationAction.Mute, Reason = String.IsNullOrEmpty(reason) ? null : reason, ExpiresAt = expiresAt };
			await RealtimeOut.User(target).ModerationNotice(notice);
			return Result.Success(new CreateSanctionResponse { SanctionId = mute.ID });
		}

		private static async Task<Result<CreateSanctionResponse>> BanAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, Int64 target, long? durationS, string? reason)
		{
			Failure? failure = CheckModeration(me, target, reason, reasonRequired: true) ?? CheckDuration(durationS, required: false);
			if (failure != null)
			{
				return failure.Value;
			}

			return await BanAsActorAsync(dbFactory, ApiActorOf(me), target, durationS == null ? null : DateTime.UtcNow.AddSeconds(durationS.Value), reason);
		}

		private static async Task<Result<CreateSanctionResponse>> BanAsActorAsync(IDbContextFactory<AppDbContext> dbFactory, Database.ModerationActor actor, Int64 target, DateTime? expiresAt, string? reason)
		{
			Sanction ban;
			await using (AppDbContext db = await dbFactory.CreateDbContextAsync())
			{
				if (!await Database.Users.Exists(db, target))
				{
					return Result.NotFound("user");
				}
				ban = await Database.Moderation.AddSanction(db, target, SanctionTypes.Ban, reason, expiresAt, actor);

				// the reload refuses new connections and revokes and closes the game sessions at once
				await AccessControl.Reload(db);
			}
			return Result.Success(new CreateSanctionResponse { SanctionId = ban.ID });
		}

		// Any staff permission reads the list, kicks are never active
		public static async Task<Result<SanctionsResponse>> ListAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 target, bool activeOnly)
		{
			await using AppDbContext db = await dbFactory.CreateDbContextAsync();
			if (!await Database.Users.Exists(db, target))
			{
				return Result.NotFound("user");
			}

			List<Sanction> rows = await Database.Moderation.GetSanctions(db, target, activeOnly);
			Dictionary<long, (string Name, int Tag)> names = await RealtimeHub.NamesAsync(db, rows.Where(r => r.CreatedByID != null).Select(r => r.CreatedByID!.Value));

			SanctionInfo[] sanctions = rows.Select(row => new SanctionInfo
			{
				Id = row.ID,
				Type = TypeOf(row.Type),
				Reason = String.IsNullOrEmpty(row.Reason) ? null : row.Reason,
				CreatedAt = row.CreatedAt,
				ExpiresAt = row.Type == SanctionTypes.Kick ? null : row.ExpiresAt,
				LiftedAt = row.LiftedAt,
				CreatedBy = row.CreatedByID is long creator ? RealtimeMembers.Of(creator, names) : null
			}).ToArray();
			return Result.Success(new SanctionsResponse { Sanctions = sanctions });
		}

		// Lifts one mute or ban of this environment, lifting one that already ended succeeds and is still audited
		public static async Task<Result<Empty>> LiftAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, Int64 sanctionID, string? reason, Func<string, Task<Failure?>> requirePermission)
		{
			await using AppDbContext db = await dbFactory.CreateDbContextAsync();
			Sanction? sanction = sanctionID > 0 ? await Database.Moderation.GetSanction(db, sanctionID) : null;
			if (sanction == null || sanction.Type == SanctionTypes.Kick)
			{
				return Result.NotFound("sanction");
			}

			string permission = sanction.Type == SanctionTypes.Mute ? Permissions.ModerationMute : Permissions.ModerationBan;
			Failure? failure = await requirePermission(permission) ?? CheckModeration(me, sanction.UserID, reason);
			if (failure != null)
			{
				return failure.Value;
			}

			// rows of a sign-in provider or another tool are managed there, like the bans revoke of the CLI
			if (sanction.Source != AuthoritySources.Local)
			{
				return Result.NotFound("sanction");
			}

			await Database.Moderation.LiftSanction(db, sanction.ID, reason ?? String.Empty, ApiActorOf(me));
			if (sanction.Type == SanctionTypes.Ban)
			{
				// lets the player connect again without waiting for the reload tick
				await AccessControl.Reload(db);
			}
			return Result.Success();
		}

		// Reasons are optional except on a ban, where a blank one is refused, and staff never act on themselves
		private static Failure? CheckModeration(Int64 me, Int64 target, string? reason, bool reasonRequired = false)
		{
			if (target <= 0 || target == me)
			{
				return Result.Invalid("id");
			}
			if (reasonRequired && String.IsNullOrWhiteSpace(reason))
			{
				return Result.Invalid("reason");
			}
			if (reason != null && reason.Length > ModerationManager.MaximumReasonLength)
			{
				return ReasonTooLong();
			}
			if (!StaffRoles.CanModerate(me, target))
			{
				return Result.Fail(ErrorCodes.ModerationTargetOutranks);
			}
			return null;
		}

		// Whole minutes from one minute up to ten years
		private static Failure? CheckDuration(long? durationS, bool required)
		{
			if (durationS == null)
			{
				return required ? Result.Invalid("duration_s") : null;
			}
			if (durationS.Value < MinimumDurationS || durationS.Value > MaximumDurationS || durationS.Value % 60 != 0)
			{
				return Result.Invalid("duration_s");
			}
			return null;
		}

		public static Failure ReasonTooLong() => Result.Fail(ErrorCodes.ModerationReasonTooLong, new { Max = ModerationManager.MaximumReasonLength });

		private static SanctionType TypeOf(string restriction) => restriction switch
		{
			SanctionTypes.Kick => SanctionType.Kick,
			SanctionTypes.Mute => SanctionType.Mute,
			_ => SanctionType.Ban
		};
	}
}
