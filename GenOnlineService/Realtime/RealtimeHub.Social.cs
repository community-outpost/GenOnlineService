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

namespace GenOnlineService.Realtime
{
	// Friends, blocks and presence (section 5.4 and 6.5)
	public sealed partial class RealtimeHub
	{
		public const int FriendsLimit = 200;

		public async Task<Result<Empty>> SendFriendRequest(UserRequest request)
		{
			Int64 me = UserID;
			Int64 target = request.UserId;
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			Failure? invalid = await CheckSocialTargetAsync(db, target);
			if (invalid != null)
			{
				return invalid.Value;
			}

			if (SocialState.HasFriend(me, target))
			{
				return Result.Success();
			}

			bool blockedByTarget = SocialState.Of(target) != null ? SocialState.HasBlocked(target, me) : await Database.Social.IsBlocked(db, target, me);
			if (blockedByTarget)
			{
				return Result.Fail(ErrorCodes.SocialBlocked);
			}

			if (await FriendCountAsync(db, me) >= FriendsLimit)
			{
				return Result.Fail(ErrorCodes.SocialFriendListFull, new { Max = FriendsLimit });
			}

			// asking for a friendship lifts the caller's own block
			if (SocialState.HasBlocked(me, target))
			{
				SocialState.Update(me, c => c.Blocked.Remove(target));
				await Database.Social.RemoveBlock(db, me, target);
			}

			// a crossed request means both want the friendship
			if (SocialState.HasIncoming(me, target))
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

		public async Task<Result<Empty>> AcceptFriendRequest(UserRequest request)
		{
			Int64 me = UserID;
			Int64 target = request.UserId;
			if (target == me)
			{
				return Result.Invalid("user_id");
			}
			if (!SocialState.HasIncoming(me, target))
			{
				return Result.NotFound("friend_request");
			}

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			return await AcceptAsync(db, me, target);
		}

		public async Task<Result<Empty>> DeclineFriendRequest(UserRequest request)
		{
			Int64 me = UserID;
			Int64 target = request.UserId;
			if (!SocialState.HasIncoming(me, target))
			{
				return Result.NotFound("friend_request");
			}

			SocialState.Update(me, c => c.PendingRequests.Remove(target));
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			await Database.Social.RemoveFriendRequest(db, target, me);
			return Result.Success();
		}

		public async Task<Result<Empty>> CancelFriendRequest(UserRequest request)
		{
			Int64 me = UserID;
			Int64 target = request.UserId;
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			if (!await Database.Social.HasFriendRequest(db, me, target))
			{
				return Result.NotFound("friend_request");
			}

			await Database.Social.RemoveFriendRequest(db, me, target);
			SocialState.Update(target, c => c.PendingRequests.Remove(me));
			return Result.Success();
		}

		public async Task<Result<Empty>> RemoveFriend(UserRequest request)
		{
			Int64 me = UserID;
			Int64 target = request.UserId;
			if (!SocialState.HasFriend(me, target))
			{
				return Result.NotFound("friend");
			}

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			await EndFriendshipAsync(db, me, target);
			return Result.Success();
		}

		public async Task<Result<Empty>> BlockUser(UserRequest request)
		{
			Int64 me = UserID;
			Int64 target = request.UserId;
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			Failure? invalid = await CheckSocialTargetAsync(db, target);
			if (invalid != null)
			{
				return invalid.Value;
			}

			// staff holding social.unblockable cannot be blocked
			if (StaffRoles.HasPermission(target, Permissions.SocialUnblockable))
			{
				return Result.Forbidden(Permissions.SocialUnblockable);
			}

			if (SocialState.HasBlocked(me, target))
			{
				return Result.Success();
			}

			if (SocialState.HasFriend(me, target))
			{
				await EndFriendshipAsync(db, me, target);
			}

			// pending requests in either direction end with the block
			await Database.Social.RemovePendingFriendRequest(db, me, target);
			SocialState.Update(me, c => c.PendingRequests.Remove(target));
			SocialState.Update(target, c => c.PendingRequests.Remove(me));

			await Database.Social.AddBlock(db, me, target);
			SocialState.Update(me, c => c.Blocked.Add(target));
			return Result.Success();
		}

		public async Task<Result<Empty>> UnblockUser(UserRequest request)
		{
			Int64 me = UserID;
			Int64 target = request.UserId;
			if (!SocialState.HasBlocked(me, target))
			{
				return Result.Success();
			}

			SocialState.Update(me, c => c.Blocked.Remove(target));
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			await Database.Social.RemoveBlock(db, me, target);
			return Result.Success();
		}

		private partial async Task SendFriendsSnapshotAsync(UserSession session)
		{
			Int64 me = session.m_UserID;
			long[] friends = SocialState.Snapshot(me, c => c.Friends);
			long[] incoming = SocialState.Snapshot(me, c => c.PendingRequests);
			long[] blocked = SocialState.Snapshot(me, c => c.Blocked);

			HashSet<long> outgoing;
			Dictionary<long, string> names;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				outgoing = await Database.Social.GetOutgoingFriendRequests(db, me);
				List<long> everyone = friends.Concat(incoming).Concat(blocked).Concat(outgoing).Distinct().ToList();
				names = await Database.Users.GetDisplayNameBulk(db, everyone);
			}

			// online users show their live name, users without a row no longer exist and are left out
			Member? MemberOf(long userID)
			{
				string? name = WebSocketManager.GetSharedDataForUser(userID)?.m_strDisplayName;
				if (name == null && !names.TryGetValue(userID, out name))
				{
					return null;
				}
				return RealtimeMembers.Of(userID, name);
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
				friendList.Add(new FriendMember { Id = member.Id, Name = member.Name, Badges = member.Badges, Online = presence.Online, Activity = presence.Activity });
			}

			await Clients.Caller.Friends(new FriendsEvent
			{
				Friends = friendList,
				Incoming = incoming.Select(MemberOf).OfType<Member>().ToArray(),
				Outgoing = outgoing.Select(MemberOf).OfType<Member>().ToArray(),
				Blocked = blocked.Select(MemberOf).OfType<Member>().ToArray()
			});
		}

		private partial Task OnConnectionOpenedAsync(UserSession session)
		{
			PublishPresence(session.m_UserID);
			return Task.CompletedTask;
		}

		private partial Task OnConnectionClosedAsync(UserSession session) => LeaveRoomOnCloseAsync(session);

		// Sends FriendPresence about the user to their online friends
		internal static void PublishPresence(Int64 userID)
		{
			FriendPresenceEvent presence = RealtimePresence.Of(userID);
			List<string> connections = new();
			foreach (Int64 friendID in OnlineFriendsOf(userID))
			{
				connections.AddRange(RealtimeAudience.ConnectionsOf(friendID));
			}

			if (connections.Count > 0)
			{
				RealtimeOut.Fire(RealtimeOut.Connections(connections.Distinct().ToArray()).FriendPresence(presence));
			}
		}

		// The user's own cache when they are online, otherwise every online user who lists them as a friend
		private static IEnumerable<Int64> OnlineFriendsOf(Int64 userID)
		{
			if (SocialState.Of(userID) != null)
			{
				return SocialState.Snapshot(userID, c => c.Friends).Where(id => WebSocketManager.GetSharedDataForUser(id) != null);
			}

			return RealtimeAudience.AllSessions().Select(s => s.m_UserID).Distinct().Where(id => SocialState.HasFriend(id, userID)).ToArray();
		}

		private async Task<Failure?> CheckSocialTargetAsync(AppDbContext db, Int64 target)
		{
			if (target <= 0 || target == UserID)
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
