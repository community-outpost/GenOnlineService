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
	// ForceRename stays a hub method; the other staff actions are REST endpoints (Api/ModerationController)
	public sealed partial class RealtimeHub
	{
		// Staff replace an offensive name; the player keeps the account and gets a free tag for the new name
		public async Task<Result<Empty>> ForceRename(ForceRenameRequest request)
		{
			Failure? failure = await RequirePermissionAsync(Permissions.ModerationRename) ?? CheckModeration(request.UserId, request.Reason);
			if (failure != null)
			{
				return failure.Value;
			}

			// Keycloak owns the names there: staff edit display_name in Keycloak and the group sync brings it here
			if (m_accounts.NamesFromUpstream)
			{
				return Result.Fail(ErrorCodes.NameManaged);
			}

			string name = String.IsNullOrEmpty(request.Name) ? ForcedRenameDefault : request.Name;
			Failure? invalid = ProfileActions.CheckNameRules(name);
			if (invalid != null)
			{
				return invalid.Value;
			}

			NameChange? change;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				(string Name, int Tag)? before = await Database.DisplayNames.Get(db, request.UserId);
				if (before == null)
				{
					return Result.NotFound("user");
				}

				try
				{
					change = await Database.DisplayNames.Rename(db, request.UserId, name, $"user:{UserID}");
				}
				catch (NameTagsExhaustedException)
				{
					return Result.Fail(ErrorCodes.NameTaken);
				}

				// renaming to the current name changes nothing and leaves no audit row
				if (change != null)
				{
					string from = Database.DisplayNames.Format(before.Value.Name, before.Value.Tag);
					await Database.Moderation.LogUserAction(db, SanctionActions.ActorOf(UserID), ModerationActions.Rename, request.UserId,
						String.IsNullOrEmpty(request.Reason) ? $"was {from}" : $"{request.Reason} (was {from})");
				}
			}

			if (change != null)
			{
				await RealtimeNames.PublishOne(change);
			}
			return Result.Success();
		}

		// The name a forced rename gives when staff name none
		private const string ForcedRenameDefault = "Player";

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
				return SanctionActions.ReasonTooLong();
			}
			if (!StaffRoles.CanModerate(UserID, target))
			{
				return Result.Fail(ErrorCodes.ModerationTargetOutranks);
			}
			return null;
		}
	}
}
