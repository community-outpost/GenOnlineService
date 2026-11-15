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
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService.Realtime
{
	// The friend and block lists of one user, members only for users that still exist
	public sealed record SocialLists(IReadOnlyList<FriendMember> Friends, IReadOnlyList<Member> Incoming, IReadOnlyList<Member> Outgoing, IReadOnlyList<Member> Blocked);

	// Friend requests, friends and blocks (section 10.1), the hub pushes the friendship events of section 6.5
	public static class SocialActions
	{
		public const int FriendsLimit = 200;

		// The cached sets of a user online on this copy, the database for anyone else
		private static async Task<long[]> FriendsOf(AppDbContext db, Int64 userID) => SocialState.Of(userID) != null ? SocialState.Snapshot(userID, c => c.Friends) : (await Database.Social.GetFriends(db, userID)).ToArray();
		private static async Task<long[]> IncomingOf(AppDbContext db, Int64 userID) => SocialState.Of(userID) != null ? SocialState.Snapshot(userID, c => c.PendingRequests) : (await Database.Social.GetPendingFriendsRequests(db, userID)).ToArray();
		private static async Task<long[]> BlockedOf(AppDbContext db, Int64 userID) => SocialState.Of(userID) != null ? SocialState.Snapshot(userID, c => c.Blocked) : (await Database.Social.GetBlocked(db, userID)).ToArray();

		private static async Task<bool> HasFriendAsync(AppDbContext db, Int64 userID, Int64 otherID) => SocialState.Of(userID) != null ? SocialState.HasFriend(userID, otherID) : (await Database.Social.GetFriends(db, userID)).Contains(otherID);
		private static async Task<bool> HasIncomingAsync(AppDbContext db, Int64 userID, Int64 fromID) => SocialState.Of(userID) != null ? SocialState.HasIncoming(userID, fromID) : await Database.Social.HasFriendRequest(db, fromID, userID);
		// Block decisions read the database, so a block or unblock made through another copy counts at once
		private static Task<bool> HasBlockedAsync(AppDbContext db, Int64 userID, Int64 otherID) => Database.Social.IsBlocked(db, userID, otherID);

		// The lists of the Friends snapshot and of GET /api/v1/me/friends and /api/v1/me/blocks
		public static async Task<SocialLists> ListsAsync(AppDbContext db, Int64 me)
		{
			long[] friends = await FriendsOf(db, me);
			long[] incoming = await IncomingOf(db, me);
			long[] blocked = await BlockedOf(db, me);
			HashSet<long> outgoing = await Database.Social.GetOutgoingFriendRequests(db, me);
			List<long> everyone = friends.Concat(incoming).Concat(blocked).Concat(outgoing).Distinct().ToList();
			Dictionary<long, (string Name, int Tag)> names = await Database.DisplayNames.GetBulk(db, everyone);

			// online users show their live name, users without a row no longer exist and are left out
			Member? MemberOf(long userID)
			{
				SharedUserData? online = WebSocketManager.GetSharedDataForUser(userID);
				if (online != null)
				{
					return RealtimeMembers.Of(userID, online.m_strDisplayName, null, online.m_NameTag);
				}
				return names.TryGetValue(userID, out (string Name, int Tag) stored) ? RealtimeMembers.Of(userID, stored.Name, null, stored.Tag) : null;
			}

			List<FriendMember> friendList = new();
			foreach (long friendID in friends)
			{
				Member? member = MemberOf(friendID);
				if (member == null)
				{
					continue;
				}

				FriendPresenceEvent presence = RealtimePresence.Of(friendID);
				friendList.Add(new FriendMember { Id = member.Id, Name = member.Name, Badges = member.Badges, Online = presence.Online, Activity = presence.Activity, ChannelId = presence.ChannelId });
			}

			return new SocialLists(
				friendList,
				incoming.Select(MemberOf).OfType<Member>().ToArray(),
				outgoing.Select(MemberOf).OfType<Member>().ToArray(),
				blocked.Select(MemberOf).OfType<Member>().ToArray());
		}

		public static async Task<Result<Empty>> SendFriendRequestAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, Int64 target)
		{
			await using AppDbContext db = await dbFactory.CreateDbContextAsync();
			Failure? invalid = await CheckTargetAsync(db, me, target);
			if (invalid != null)
			{
				return invalid.Value;
			}

			if (await HasFriendAsync(db, me, target))
			{
				return Result.Success();
			}

			if (await HasBlockedAsync(db, target, me))
			{
				return Result.Fail(ErrorCodes.SocialBlocked);
			}

			if (await FriendCountAsync(db, me) >= FriendsLimit)
			{
				return Result.Fail(ErrorCodes.SocialFriendListFull, new { Max = FriendsLimit });
			}

			// asking for a friendship lifts the caller's own block
			if (await HasBlockedAsync(db, me, target))
			{
				await Database.Social.RemoveBlock(db, me, target);
				await RealtimeBlocks.ChangedAsync(me, target, false);
			}

			// a crossed request means both want the friendship
			if (await HasIncomingAsync(db, me, target))
			{
				return await AcceptAsync(db, me, target);
			}

			if (await Database.Social.HasFriendRequest(db, me, target))
			{
				return Result.Success();
			}

			await Database.Social.AddPendingFriendRequest(db, me, target);
			SocialState.Update(target, c => c.PendingRequests.Add(me));
			await RealtimeOut.User(target).FriendRequestReceived(new FriendRequestReceivedEvent { From = RealtimeMembers.Of(me) });
			return Result.Success();
		}

		public static async Task<Result<Empty>> AcceptFriendRequestAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, Int64 target)
		{
			if (target == me)
			{
				return Result.Invalid("user_id");
			}

			await using AppDbContext db = await dbFactory.CreateDbContextAsync();
			if (!await HasIncomingAsync(db, me, target))
			{
				return Result.NotFound("friend_request");
			}
			return await AcceptAsync(db, me, target);
		}

		// Declines an incoming request, otherwise cancels the caller's outgoing one
		public static async Task<Result<Empty>> DeleteFriendRequestAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, Int64 target)
		{
			await using AppDbContext db = await dbFactory.CreateDbContextAsync();
			if (await HasIncomingAsync(db, me, target))
			{
				SocialState.Update(me, c => c.PendingRequests.Remove(target));
				await Database.Social.RemoveFriendRequest(db, target, me);
				return Result.Success();
			}

			if (await Database.Social.HasFriendRequest(db, me, target))
			{
				await Database.Social.RemoveFriendRequest(db, me, target);
				SocialState.Update(target, c => c.PendingRequests.Remove(me));
				return Result.Success();
			}

			return Result.NotFound("friend_request");
		}

		public static async Task<Result<Empty>> RemoveFriendAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, Int64 target)
		{
			await using AppDbContext db = await dbFactory.CreateDbContextAsync();
			if (!await HasFriendAsync(db, me, target))
			{
				return Result.NotFound("friend");
			}

			await EndFriendshipAsync(db, me, target);
			return Result.Success();
		}

		public static async Task<Result<Empty>> BlockAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, Int64 target)
		{
			await using AppDbContext db = await dbFactory.CreateDbContextAsync();
			Failure? invalid = await CheckTargetAsync(db, me, target);
			if (invalid != null)
			{
				return invalid.Value;
			}

			// staff holding social.unblockable cannot be blocked
			if (StaffRoles.HasPermission(target, Permissions.SocialUnblockable))
			{
				return Result.Forbidden(Permissions.SocialUnblockable);
			}

			if (await HasBlockedAsync(db, me, target))
			{
				// a repeat publishes again, so a copy that missed the first message catches up
				await RealtimeBlocks.ChangedAsync(me, target, true);
				return Result.Success();
			}

			// the database decides, a friendship made through another copy ends too
			if ((await Database.Social.GetFriends(db, me)).Contains(target))
			{
				await EndFriendshipAsync(db, me, target);
			}

			// pending requests in either direction end with the block
			await Database.Social.RemovePendingFriendRequest(db, me, target);
			SocialState.Update(me, c => c.PendingRequests.Remove(target));
			SocialState.Update(target, c => c.PendingRequests.Remove(me));

			await Database.Social.AddBlock(db, me, target);
			await RealtimeBlocks.ChangedAsync(me, target, true);
			return Result.Success();
		}

		public static async Task<Result<Empty>> UnblockAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 me, Int64 target)
		{
			await using AppDbContext db = await dbFactory.CreateDbContextAsync();
			if (!await HasBlockedAsync(db, me, target))
			{
				// a repeat publishes again, so a copy that missed the first message catches up
				await RealtimeBlocks.ChangedAsync(me, target, false);
				return Result.Success();
			}

			await Database.Social.RemoveBlock(db, me, target);
			await RealtimeBlocks.ChangedAsync(me, target, false);
			return Result.Success();
		}

		private static async Task<Failure?> CheckTargetAsync(AppDbContext db, Int64 me, Int64 target)
		{
			if (target <= 0 || target == me)
			{
				return Result.Invalid("user_id");
			}
			if (!await Database.Users.Exists(db, target))
			{
				return Result.NotFound("user");
			}
			return null;
		}

		private static async Task<int> FriendCountAsync(AppDbContext db, Int64 userID)
		{
			return SocialState.Of(userID) != null ? SocialState.Snapshot(userID, c => c.Friends).Length : await Database.Social.CountFriends(db, userID);
		}

		private static async Task<Result<Empty>> AcceptAsync(AppDbContext db, Int64 me, Int64 target)
		{
			// a friendship adds an entry to both lists, so both need room
			if (await FriendCountAsync(db, me) >= FriendsLimit || await FriendCountAsync(db, target) >= FriendsLimit)
			{
				return Result.Fail(ErrorCodes.SocialFriendListFull, new { Max = FriendsLimit });
			}

			await Database.Social.RemovePendingFriendRequest(db, me, target);
			await Database.Social.CreateFriendship(db, me, target);
			SocialState.Update(me, c => { c.PendingRequests.Remove(target); c.Friends.Add(target); });
			SocialState.Update(target, c => { c.PendingRequests.Remove(me); c.Friends.Add(me); });

			await RealtimeOut.User(target).FriendRequestAccepted(new FriendRequestAcceptedEvent { By = RealtimeMembers.Of(me) });
			await RealtimeOut.User(target).FriendPresence(RealtimePresence.Of(me));
			await RealtimeOut.User(me).FriendPresence(RealtimePresence.Of(target));
			return Result.Success();
		}

		private static async Task EndFriendshipAsync(AppDbContext db, Int64 me, Int64 target)
		{
			await Database.Social.RemoveFriendship(db, me, target);
			SocialState.Update(me, c => c.Friends.Remove(target));
			SocialState.Update(target, c => c.Friends.Remove(me));

			await RealtimeOut.User(me).FriendRemoved(new UserIdEvent { UserId = target });
			await RealtimeOut.User(target).FriendRemoved(new UserIdEvent { UserId = me });
		}
	}
}
