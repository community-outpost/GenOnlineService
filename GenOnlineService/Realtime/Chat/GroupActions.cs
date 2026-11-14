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
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService.Realtime
{
	// Group chats (section 10.1): create, add members, remove a member or leave, rename, the hub pushes GroupCreate, GroupUpdate and GroupRemoved
	public static class GroupActions
	{
		// Membership changes of one group run one at a time on this copy, so the size limit and the owner checks hold
		private static readonly SemaphoreSlim[] s_groupLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

		public static async Task<Result<CreateGroupResponse>> CreateAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, CreateGroupRequest request)
		{
			if (!IsValidGroupName(request.Name))
			{
				return Result.Invalid("name");
			}

			Int64[] invited = (request.UserIds ?? []).Where(id => id != me).Distinct().ToArray();
			if (invited.Length == 0)
			{
				return Result.Invalid("user_ids");
			}
			if (invited.Length + 1 > RealtimeHub.GroupMaxMembers)
			{
				return Result.Fail(ErrorCodes.GroupFull, new { Max = RealtimeHub.GroupMaxMembers });
			}

			Database.GroupInfo group;
			Dictionary<long, string> names;
			await using (AppDbContext db = await dbFactory.CreateDbContextAsync())
			{
				Failure? refused = await CheckInviteesAsync(db, me, invited);
				if (refused != null)
				{
					return refused.Value;
				}

				group = await Database.Chat.CreateGroup(db, me, request.Name, invited);
				names = await RealtimeHub.NamesAsync(db, group.MemberIDs);
			}

			await RealtimeOut.Users(group.MemberIDs).GroupCreate(new GroupEvent { Group = RealtimeHub.ToWireGroup(group, names) });
			return Result.Success(new CreateGroupResponse { GroupId = group.Group.ID });
		}

		public static async Task<Result<Empty>> AddMembersAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, Int64 groupID, AddGroupMembersRequest request)
		{
			Int64[] requested = (request.UserIds ?? []).Where(id => id != me).Distinct().ToArray();
			if (requested.Length == 0)
			{
				return Result.Invalid("user_ids");
			}

			Database.GroupInfo group;
			Int64[] added;
			Dictionary<long, string> names;
			SemaphoreSlim gate = GroupLock(groupID);
			await gate.WaitAsync();
			try
			{
				await using AppDbContext db = await dbFactory.CreateDbContextAsync();
				Database.GroupInfo? current = await Database.Chat.GetGroup(db, groupID);
				if (current == null || !current.MemberIDs.Contains(me))
				{
					return Result.Fail(ErrorCodes.GroupNotMember);
				}
				if (current.Group.OwnerID != me)
				{
					return Result.Fail(ErrorCodes.GroupNotOwner);
				}

				// users already in the group are left as they are
				added = requested.Where(id => !current.MemberIDs.Contains(id)).ToArray();
				if (added.Length == 0)
				{
					return Result.Success();
				}
				if (current.MemberIDs.Count + added.Length > RealtimeHub.GroupMaxMembers)
				{
					return Result.Fail(ErrorCodes.GroupFull, new { Max = RealtimeHub.GroupMaxMembers });
				}

				Failure? refused = await CheckInviteesAsync(db, me, added);
				if (refused != null)
				{
					return refused.Value;
				}

				await Database.Chat.AddGroupMembers(db, groupID, added);
				group = await Database.Chat.GetGroup(db, groupID) ?? current;
				names = await RealtimeHub.NamesAsync(db, group.MemberIDs);
			}
			finally
			{
				gate.Release();
			}

			// new members get the whole group and read its stored history through GetHistory
			Group wire = RealtimeHub.ToWireGroup(group, names);
			await RealtimeOut.Users(added).GroupCreate(new GroupEvent { Group = wire });
			await RealtimeOut.Users(group.MemberIDs.Where(id => !added.Contains(id))).GroupUpdate(new GroupEvent { Group = wire });
			return Result.Success();
		}

		// The caller's own id leaves the group, another member's id is a removal by the owner
		public static async Task<Result<Empty>> RemoveMemberAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, Int64 groupID, Int64 userID)
		{
			if (userID == me)
			{
				return await LeaveAsync(dbFactory, me, groupID);
			}

			Database.GroupInfo? remaining;
			Dictionary<long, string> names;
			SemaphoreSlim gate = GroupLock(groupID);
			await gate.WaitAsync();
			try
			{
				await using AppDbContext db = await dbFactory.CreateDbContextAsync();
				Database.GroupInfo? current = await Database.Chat.GetGroup(db, groupID);
				if (current == null || !current.MemberIDs.Contains(me))
				{
					return Result.Fail(ErrorCodes.GroupNotMember);
				}
				if (current.Group.OwnerID != me)
				{
					return Result.Fail(ErrorCodes.GroupNotOwner);
				}
				if (!current.MemberIDs.Contains(userID))
				{
					return Result.NotFound("user");
				}

				remaining = (await Database.Chat.LeaveGroup(db, groupID, userID)).Remaining;
				names = remaining == null ? new Dictionary<long, string>() : await RealtimeHub.NamesAsync(db, remaining.MemberIDs);
			}
			finally
			{
				gate.Release();
			}

			await RealtimeOut.User(userID).GroupRemoved(new GroupRemovedEvent { GroupId = groupID, Reason = GroupRemovedReason.Removed });
			if (remaining != null)
			{
				await RealtimeOut.Users(remaining.MemberIDs).GroupUpdate(new GroupEvent { Group = RealtimeHub.ToWireGroup(remaining, names) });
			}
			return Result.Success();
		}

		public static async Task<Result<Empty>> RenameAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, Int64 groupID, RenameGroupRequest request)
		{
			if (!request.Name.HasValue || !IsValidGroupName(request.Name.Value))
			{
				return Result.Invalid("name");
			}

			Database.GroupInfo? group;
			Dictionary<long, string> names;
			SemaphoreSlim gate = GroupLock(groupID);
			await gate.WaitAsync();
			try
			{
				await using AppDbContext db = await dbFactory.CreateDbContextAsync();
				Database.GroupInfo? current = await Database.Chat.GetGroup(db, groupID);
				if (current == null || !current.MemberIDs.Contains(me))
				{
					return Result.Fail(ErrorCodes.GroupNotMember);
				}
				if (current.Group.OwnerID != me)
				{
					return Result.Fail(ErrorCodes.GroupNotOwner);
				}

				await Database.Chat.RenameGroup(db, groupID, request.Name.Value);
				group = await Database.Chat.GetGroup(db, groupID);
				if (group == null)
				{
					return Result.Fail(ErrorCodes.GroupNotMember);
				}
				names = await RealtimeHub.NamesAsync(db, group.MemberIDs);
			}
			finally
			{
				gate.Release();
			}

			await RealtimeOut.Users(group.MemberIDs).GroupUpdate(new GroupEvent { Group = RealtimeHub.ToWireGroup(group, names) });
			return Result.Success();
		}

		private static async Task<Result<Empty>> LeaveAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, Int64 groupID)
		{
			Database.GroupLeaveResult result;
			Dictionary<long, string> names;
			SemaphoreSlim gate = GroupLock(groupID);
			await gate.WaitAsync();
			try
			{
				await using AppDbContext db = await dbFactory.CreateDbContextAsync();

				// the earliest member left becomes owner, the last member to leave deletes the group
				result = await Database.Chat.LeaveGroup(db, groupID, me);
				if (!result.WasMember)
				{
					return Result.Fail(ErrorCodes.GroupNotMember);
				}
				names = result.Remaining == null ? new Dictionary<long, string>() : await RealtimeHub.NamesAsync(db, result.Remaining.MemberIDs);
			}
			finally
			{
				gate.Release();
			}

			await RealtimeOut.User(me).GroupRemoved(new GroupRemovedEvent { GroupId = groupID, Reason = GroupRemovedReason.Left });
			if (result.Remaining != null)
			{
				await RealtimeOut.Users(result.Remaining.MemberIDs).GroupUpdate(new GroupEvent { Group = RealtimeHub.ToWireGroup(result.Remaining, names) });
			}
			return Result.Success();
		}

		// Every invitee is a mutual friend of the caller with no block either way, a block is reported first
		private static async Task<Failure?> CheckInviteesAsync(AppDbContext db, Int64 me, IReadOnlyCollection<Int64> invitees)
		{
			HashSet<long> blocked = await RealtimeHub.BlockedEitherWayAsync(db, me);
			if (invitees.Any(blocked.Contains))
			{
				return Result.Fail(ErrorCodes.SocialBlocked);
			}

			HashSet<long> friends = await Database.Social.GetFriends(db, me);
			return invitees.All(friends.Contains) ? null : Result.Fail(ErrorCodes.ChatNotFriends);
		}

		// Null means no name, otherwise 1 to 32 characters of clean text that is not only whitespace
		private static bool IsValidGroupName(string? name)
		{
			if (name == null)
			{
				return true;
			}

			return name.Length >= 1 && name.Length <= Database.Chat.MaximumGroupNameLength
				&& !String.IsNullOrWhiteSpace(name)
				&& !name.Any(Char.IsControl)
				&& String.Equals(TextSanitizer.Clean(name, Database.Chat.MaximumGroupNameLength), name, StringComparison.Ordinal);
		}

		private static SemaphoreSlim GroupLock(Int64 groupID) => s_groupLocks[(int)((UInt64)groupID % (UInt64)s_groupLocks.Length)];
	}
}
