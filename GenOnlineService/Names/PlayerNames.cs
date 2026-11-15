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
	// Every rename (PATCH /api/v1/me, PATCH /api/v1/players/:id, the Discord bot), AccountsSettings.NameOwner picks who performs it
	public sealed class PlayerNames
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;
		private readonly AccountsSettings m_accounts;
		private readonly KeycloakRenameClient m_plugin;
		private readonly KeycloakChangeLog? m_changeLog;

		public PlayerNames(IDbContextFactory<AppDbContext> dbFactory, AccountsSettings accounts, KeycloakRenameClient plugin, KeycloakChangeLog? changeLog = null)
		{
			m_dbFactory = dbFactory;
			m_accounts = accounts;
			m_plugin = plugin;
			m_changeLog = changeLog;
		}

		// The player renames themselves, the tag stays
		public async Task<Result<Empty>> RenameSelfAsync(Int64 me, string? requested)
		{
			NameOwner owner = m_accounts.NameOwner;
			if (owner == NameOwner.Managed)
			{
				return Result.Fail(ErrorCodes.NameManaged);
			}

			string name = requested ?? String.Empty;
			Failure? invalid = CheckRules(me, name);
			if (invalid != null)
			{
				return invalid.Value;
			}

			(Result<Empty> result, _) = await RenameAsync(owner, me, name, $"user:{me}", $"player:{me}", null);
			return result;
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

			NameOwner owner = m_accounts.NameOwner;
			if (owner == NameOwner.Managed)
			{
				return Result.Fail(ErrorCodes.NameManaged);
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

			// the Discord bot speaks as discord:<id>, everyone else as the player
			string pluginActor = actor.Kind == AuditActorKinds.Discord ? actor.Name : $"player:{actor.UserID}";
			(Result<Empty> result, bool renamed) = await RenameAsync(owner, target, name, actor.Name, pluginActor, reason);

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
		private async Task<(Result<Empty> Result, bool Renamed)> RenameAsync(NameOwner owner, Int64 userID, string name, string changedBy, string pluginActor, string? reason)
		{
			if (owner == NameOwner.Plugin)
			{
				return await RenameThroughPluginAsync(userID, name, changedBy, pluginActor, reason);
			}

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

		// Prod applies the plugin's answer at once and leaves last_seq to the change-log consumer, whose entry carries the same state.
		// With the change log the answer is applied under its last_seq row lock, so a racing push of the same seq writes nothing.
		private async Task<(Result<Empty> Result, bool Renamed)> RenameThroughPluginAsync(Int64 userID, string name, string changedBy, string pluginActor, string? reason)
		{
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				if (!await Database.Users.Exists(db, userID))
				{
					return (Result.NotFound("user"), false);
				}
			}

			PluginRenameResult answer = await m_plugin.RenameAsync(userID, name, pluginActor, reason);
			if (!answer.Ok)
			{
				return (answer.Failure!.Value, false);
			}

			if (m_changeLog != null)
			{
				// the change log publishes after its commit
				await m_changeLog.ApplyAnswer(userID, answer.Name, answer.Tag, changedBy);
				return (Result.Success(), answer.Seq != null);
			}

			IReadOnlyList<NameChange> changes;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				changes = await Database.DisplayNames.ApplyUpstream(db, userID, answer.Name, answer.Tag, changedBy);
			}
			RealtimeNames.Publish(changes);
			return (Result.Success(), answer.Seq != null);
		}
	}
}
