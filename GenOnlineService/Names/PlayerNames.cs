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
using GenOnlineService.Realtime;
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService.Names
{
	// Every rename (PATCH /api/v1/me, PATCH /api/v1/players/:id, the Discord bot): this environment owns names and tags
	public sealed class PlayerNames
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public PlayerNames(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		// The player renames themselves, the tag stays
		public async Task<Result<Empty>> RenameSelfAsync(Int64 me, string? requested)
		{
			string name = requested ?? String.Empty;
			Failure? invalid = CheckRules(me, name);
			if (invalid != null)
			{
				return invalid.Value;
			}

			(Result<Empty> result, _) = await RenameAsync(me, name, $"user:{me}");
			return result;
		}

		// The player chooses the name they owe (S17), with the rules of a rename; a taken tag draws a free one instead
		public async Task<Result<Empty>> ChooseAsync(Int64 me, string? requested)
		{
			string name = (requested ?? String.Empty).Trim();
			Failure? invalid = CheckRules(me, name);
			if (invalid != null)
			{
				return invalid.Value;
			}

			bool found;
			NameChange? change;
			try
			{
				await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
				(found, change) = await Database.DisplayNames.Choose(db, me, name, $"user:{me}");
			}
			catch (NameTagsExhaustedException)
			{
				return Result.Fail(ErrorCodes.NameTaken);
			}

			if (!found)
			{
				return Result.NotFound("user");
			}
			if (change != null)
			{
				await RealtimeNames.PublishOne(change);
			}
			return Result.Success();
		}

		// Staff replace an offensive name: moderation.rename (checked by the caller), the rank rule and an audit_log row
		public async Task<Result<Empty>> RenameByStaffAsync(Database.ModerationActor actor, Int64 target, string? requested, string? reason)
		{
			int actorRank = actor.UserID is Int64 actorID ? StaffRoles.Rank(actorID) : StaffRoles.AdminRank;
			Failure? failure = CheckStaff(actor.UserID, actorRank, target, StaffRoles.Rank(target), reason);
			if (failure != null)
			{
				return failure.Value;
			}

			string name = requested ?? String.Empty;
			Failure? invalid = CheckRules(target, name);
			if (invalid != null)
			{
				return invalid.Value;
			}

			(string Name, int Tag)? before;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				before = await Database.DisplayNames.Get(db, target);
			}
			if (before == null)
			{
				return Result.NotFound("user");
			}

			(Result<Empty> result, bool renamed) = await RenameAsync(target, name, actor.Name);

			// renaming to the current name changes nothing and leaves no audit row
			if (result.Ok && renamed)
			{
				await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
				(string Name, int Tag)? after = await Database.DisplayNames.Get(db, target);
				await Database.Moderation.LogAction(db, actor, ModerationActions.Rename, AuditTargetKinds.User, target, String.IsNullOrEmpty(reason) ? null : reason, new
				{
					from = Database.DisplayNames.Format(before.Value.Name, before.Value.Tag),
					to = after == null ? name : Database.DisplayNames.Format(after.Value.Name, after.Value.Tag)
				});
			}
			return result;
		}

		// Staff flag an inappropriate name (S17): moderation.rename (checked by the caller) and the rank rule, then a neutral
		// placeholder, the player owes a new name and every sign-in ends, so the next one asks at once; audited with both names.
		// live is false outside the running service (the CLI), which can only revoke the stored sign-ins.
		public async Task<Result<Empty>> ResetNameByStaffAsync(Database.ModerationActor actor, Int64 target, string? reason, bool live = true)
		{
			int actorRank = actor.UserID is Int64 actorID ? StaffRoles.Rank(actorID) : StaffRoles.AdminRank;
			Failure? failure = CheckStaff(actor.UserID, actorRank, target, StaffRoles.Rank(target), reason);
			if (failure != null)
			{
				return failure.Value;
			}

			NameChange? change;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				change = await Database.DisplayNames.ResetName(db, target, actor.Name);
				if (change == null)
				{
					return Result.NotFound("user");
				}

				await Database.Moderation.LogAction(db, actor, ModerationActions.NameReset, AuditTargetKinds.User, target, String.IsNullOrEmpty(reason) ? null : reason, new
				{
					from = Database.DisplayNames.Format(change.OldName, change.OldTag),
					to = Database.DisplayNames.Format(change.NewName, change.NewTag)
				});

				if (!live)
				{
					await SignInRevocation.RevokeStored(db, target);
				}
			}

			if (live)
			{
				await RealtimeNames.PublishOne(change);
				await SessionRevocation.RevokeUser(target, false, "name reset");
			}
			return Result.Success();
		}

		// The rank rule and the reason limit of every staff action, staff never act on themselves
		public static Failure? CheckStaff(Int64? actorID, int actorRank, Int64 target, int targetRank, string? reason)
		{
			if (target <= 0 || target == actorID)
			{
				return Result.Invalid("id");
			}
			if (reason != null && reason.Length > ModerationManager.MaximumReasonLength)
			{
				return SanctionActions.ReasonTooLong();
			}
			if (actorRank <= targetRank)
			{
				return Result.Fail(ErrorCodes.ModerationTargetOutranks);
			}
			return null;
		}

		// The rules of a chosen name; reserved words apply unless the name's owner holds names.protected
		private static Failure? CheckRules(Int64 owner, string name)
		{
			Failure? invalid = ProfileActions.CheckNameRules(name);
			if (invalid != null)
			{
				return invalid;
			}
			if (!StaffRoles.HasPermission(owner, Permissions.NamesProtected))
			{
				string? word = ProfileActions.ReservedWordIn(name);
				if (word != null)
				{
					return Result.Fail(ErrorCodes.NameReserved, new { Word = word });
				}
			}
			return null;
		}

		// Renamed is true when the name actually changed
		private async Task<(Result<Empty> Result, bool Renamed)> RenameAsync(Int64 userID, string name, string changedBy)
		{
			LocalRenameOutcome outcome;
			NameChange? change;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				(outcome, change) = await Database.DisplayNames.RenameKeepingTag(db, userID, name, changedBy);
			}

			switch (outcome)
			{
				case LocalRenameOutcome.NotFound:
					return (Result.NotFound("user"), false);
				case LocalRenameOutcome.Taken:
					return (Result.Fail(ErrorCodes.NameTaken), false);
				case LocalRenameOutcome.Unchanged:
					return (Result.Success(), false);
			}

			await RealtimeNames.PublishOne(change!);
			return (Result.Success(), true);
		}
	}
}
