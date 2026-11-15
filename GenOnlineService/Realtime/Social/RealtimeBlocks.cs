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

using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Text.Json;

namespace GenOnlineService.Realtime
{
	// One block or unblock on its way to every copy: the user who blocked or unblocked, the other user, and which of the two
	internal sealed record BlockChange
	{
		public long UserId { get; init; }
		public long OtherId { get; init; }
		public bool Blocked { get; init; }
	}

	// Users whose friends, friend requests or blocks changed, every copy reads their cached social sets again
	internal sealed record SocialChange
	{
		public long[] UserIds { get; init; } = Array.Empty<long>();
	}

	// The cached social sets of the users online on one copy and of the lobby hosts it keeps, kept current by the social changes of every copy
	internal sealed class BlockCaches
	{
		private static readonly ILogger s_log = AppLog.For(typeof(BlockCaches));

		// Reads while concurrent changes keep moving the sequence, the next change reads again anyway
		private const int ReloadAttempts = 3;

		private readonly Func<long, UserSocialContainer?> m_of;
		private long m_sequence;

		public BlockCaches(Func<long, UserSocialContainer?> of)
		{
			m_of = of;
		}

		// Counts every change this copy applied, a read of the database that saw it move reads again
		public long Sequence => Interlocked.Read(ref m_sequence);

		public UserSocialContainer? Of(long userID) => m_of(userID);

		public bool HasBlocked(long userID, long otherID) => Read(userID, container => container.Blocked.Contains(otherID)) == true;

		// Null when the user has no cached sets here
		public bool? HasFriend(long userID, long otherID) => Read(userID, container => container.Friends.Contains(otherID));

		// Marks a change of a cached set made outside Apply, so a database read that raced it reads again
		public void Touch() => Interlocked.Increment(ref m_sequence);

		private bool? Read(long userID, Func<UserSocialContainer, bool> read)
		{
			UserSocialContainer? container = m_of(userID);
			if (container == null)
			{
				return null;
			}
			lock (container)
			{
				return read(container);
			}
		}

		// A block also ends the pair's friendship and pending requests in both users' sets, as SocialActions.BlockAsync does
		public void Apply(BlockChange change)
		{
			Interlocked.Increment(ref m_sequence);
			Update(change.UserId, container =>
			{
				if (!change.Blocked)
				{
					container.Blocked.Remove(change.OtherId);
					return;
				}
				container.Blocked.Add(change.OtherId);
				container.Friends.Remove(change.OtherId);
				container.PendingRequests.Remove(change.OtherId);
			});
			if (change.Blocked)
			{
				Update(change.OtherId, container =>
				{
					container.Friends.Remove(change.UserId);
					container.PendingRequests.Remove(change.UserId);
				});
			}
		}

		// Replaces the user's cached blocks, friends and incoming requests with the stored ones, so the last change any copy made wins whatever order the messages took
		public async Task ReloadAsync(IDbContextFactory<AppDbContext> dbFactory, long userID)
		{
			if (m_of(userID) == null)
			{
				return;
			}

			try
			{
				await using AppDbContext db = await dbFactory.CreateDbContextAsync();
				for (int attempt = 0; attempt < ReloadAttempts; attempt++)
				{
					long before = Sequence;
					UserSocialContainer stored = await ReadAsync(db, userID);
					UserSocialContainer? container = m_of(userID);
					if (container == null)
					{
						return;
					}
					lock (container)
					{
						// a set swapped whole keeps readers outside the lock on a consistent set
						if (Sequence == before)
						{
							container.Blocked = stored.Blocked;
							container.Friends = stored.Friends;
							container.PendingRequests = stored.PendingRequests;
							return;
						}
					}
				}
				s_log.LogWarning("The cached social sets of user {UserId} stayed as they were, other changes kept arriving during {Attempts} reloads", userID, ReloadAttempts);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Reloading the cached social sets of user {UserId} failed", userID);
			}
		}

		// The stored blocks, friends and incoming friend requests of the user, a failed read throws
		public static async Task<UserSocialContainer> ReadAsync(AppDbContext db, long userID) => new UserSocialContainer
		{
			Blocked = (await db.Blocks.AsNoTracking().Where(b => b.UserId == userID).Select(b => b.BlockedUserId).ToListAsync()).ToHashSet(),
			Friends = (await db.Friendships.AsNoTracking().Where(f => f.UserIdLow == userID || f.UserIdHigh == userID)
				.Select(f => f.UserIdLow == userID ? f.UserIdHigh : f.UserIdLow).ToListAsync()).ToHashSet(),
			PendingRequests = (await db.FriendRequests.AsNoTracking().Where(r => r.ToUserId == userID).Select(r => r.FromUserId).ToListAsync()).ToHashSet()
		};

		private void Update(long userID, Action<UserSocialContainer> update)
		{
			UserSocialContainer? container = m_of(userID);
			if (container == null)
			{
				return;
			}
			lock (container)
			{
				update(container);
			}
		}
	}

	// Carries block changes between copies on one Redis channel, every copy including the sender applies them and reloads both users' sets
	internal sealed class BlockFanout
	{
		private static readonly ILogger s_log = AppLog.For(typeof(BlockFanout));
		private static readonly JsonSerializerOptions s_json = RealtimeJson.Create();

		private readonly ISubscriber m_subscriber;
		private readonly RedisChannel m_channel;
		private readonly BlockCaches m_caches;
		private readonly IDbContextFactory<AppDbContext>? m_dbFactory;

		private BlockFanout(ISubscriber subscriber, RedisChannel channel, BlockCaches caches, IDbContextFactory<AppDbContext>? dbFactory)
		{
			m_subscriber = subscriber;
			m_channel = channel;
			m_caches = caches;
			m_dbFactory = dbFactory;
		}

		// The queue runs one change at a time, so the reload of a later change always follows the reload of an earlier one
		public static async Task<BlockFanout> StartAsync(ISubscriber subscriber, RedisChannel channel, BlockCaches caches, IDbContextFactory<AppDbContext>? dbFactory)
		{
			BlockFanout fanout = new BlockFanout(subscriber, channel, caches, dbFactory);
			ChannelMessageQueue queue = await subscriber.SubscribeAsync(channel);
			queue.OnMessage(message => fanout.HandleAsync(message.Message));
			return fanout;
		}

		public static string Serialize(BlockChange change) => JsonSerializer.Serialize(change, s_json);
		public static BlockChange? Deserialize(string payload) => JsonSerializer.Deserialize<BlockChange>(payload, s_json);

		public Task PublishAsync(BlockChange change) => m_subscriber.PublishAsync(m_channel, Serialize(change));

		private async Task HandleAsync(RedisValue payload)
		{
			try
			{
				BlockChange? change = Deserialize(payload.ToString());
				if (change == null)
				{
					return;
				}
				m_caches.Apply(change);
				if (m_dbFactory != null)
				{
					// the reload of both users also restores a friendship made again after a late block message removed it
					await m_caches.ReloadAsync(m_dbFactory, change.UserId);
					await m_caches.ReloadAsync(m_dbFactory, change.OtherId);
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Applying a block change from another copy failed");
			}
		}
	}

	// Carries friend and friend request changes between copies on one Redis channel, every copy including the sender reloads the named users' sets
	internal sealed class SocialFanout
	{
		private static readonly ILogger s_log = AppLog.For(typeof(SocialFanout));
		private static readonly JsonSerializerOptions s_json = RealtimeJson.Create();

		private readonly ISubscriber m_subscriber;
		private readonly RedisChannel m_channel;
		private readonly BlockCaches m_caches;
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		private SocialFanout(ISubscriber subscriber, RedisChannel channel, BlockCaches caches, IDbContextFactory<AppDbContext> dbFactory)
		{
			m_subscriber = subscriber;
			m_channel = channel;
			m_caches = caches;
			m_dbFactory = dbFactory;
		}

		public static async Task<SocialFanout> StartAsync(ISubscriber subscriber, RedisChannel channel, BlockCaches caches, IDbContextFactory<AppDbContext> dbFactory)
		{
			SocialFanout fanout = new SocialFanout(subscriber, channel, caches, dbFactory);
			ChannelMessageQueue queue = await subscriber.SubscribeAsync(channel);
			queue.OnMessage(message => fanout.HandleAsync(message.Message));
			return fanout;
		}

		public static string Serialize(SocialChange change) => JsonSerializer.Serialize(change, s_json);
		public static SocialChange? Deserialize(string payload) => JsonSerializer.Deserialize<SocialChange>(payload, s_json);

		public Task PublishAsync(SocialChange change) => m_subscriber.PublishAsync(m_channel, Serialize(change));

		private async Task HandleAsync(RedisValue payload)
		{
			try
			{
				SocialChange? change = Deserialize(payload.ToString());

				// the change moves the sequence, so a session or lobby host loading its sets meanwhile reads again
				m_caches.Touch();
				foreach (long userID in change?.UserIds ?? Array.Empty<long>())
				{
					await m_caches.ReloadAsync(m_dbFactory, userID);
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Applying a social change from another copy failed");
			}
		}
	}

	// Block decisions that stay current across copies: cached lists for the lobby list and matchmaking pairing, the database for joins and formed matches
	public static class RealtimeBlocks
	{
		private static readonly ILogger s_log = AppLog.For(typeof(RealtimeBlocks));
		private static readonly RedisChannel s_channel = RedisChannel.Literal("realtime.blocks");
		private static readonly RedisChannel s_socialChannel = RedisChannel.Literal("realtime.social");
		private static BlockFanout? s_fanout;
		private static SocialFanout? s_socialFanout;

		// The sets of a user online here, otherwise those this copy keeps for a lobby host without a session here
		internal static BlockCaches Local { get; } = new BlockCaches(userID => SocialState.Of(userID) ?? LobbyHosts.Of(userID));

		public static long Sequence => Local.Sequence;

		private static IDbContextFactory<AppDbContext> DbFactory => ServiceLocator.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();

		internal static async Task StartAsync(ISubscriber subscriber)
		{
			BlockFanout fanout = await BlockFanout.StartAsync(subscriber, s_channel, Local, DbFactory);
			SocialFanout social = await SocialFanout.StartAsync(subscriber, s_socialChannel, Local, DbFactory);
			Volatile.Write(ref s_fanout, fanout);
			Volatile.Write(ref s_socialFanout, social);
		}

		// Called after the database write and the local cache update of a friend or friend request change, with the Redis backplane every copy reads both users' sets again
		public static async Task SocialChangedAsync(long userID, long otherID)
		{
			SocialFanout? fanout = Volatile.Read(ref s_socialFanout);
			if (fanout == null)
			{
				return;
			}
			try
			{
				await fanout.PublishAsync(new SocialChange { UserIds = new[] { userID, otherID } });
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Publishing a social change of user {UserId} failed, other copies correct it at the next decision that reads the database", userID);
			}
		}

		// Called after the database write: applies the change here at once and, with the Redis backplane, on every copy
		public static async Task ChangedAsync(long userID, long otherID, bool blocked)
		{
			BlockChange change = new BlockChange { UserId = userID, OtherId = otherID, Blocked = blocked };
			Local.Apply(change);

			BlockFanout? fanout = Volatile.Read(ref s_fanout);
			if (fanout == null)
			{
				return;
			}
			try
			{
				await fanout.PublishAsync(change);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Publishing a block change of user {UserId} failed, other copies correct it at the next lobby join or match that reads the database", userID);
			}
		}

		// Nothing to read before the service started, as in tests
		public static Task ReloadAsync(long userID) => ServiceLocator.Services == null ? Task.CompletedTask : Local.ReloadAsync(DbFactory, userID);

		// The users among the given ones who blocked the target, from the database, null when the read failed and the caches decide alone
		public static async Task<HashSet<long>?> BlockersAsync(AppDbContext db, long targetID, IReadOnlyCollection<long> userIDs)
		{
			try
			{
				List<long> blockers = await db.Blocks.AsNoTracking().Where(b => b.BlockedUserId == targetID && userIDs.Contains(b.UserId)).Select(b => b.UserId).ToListAsync();
				return blockers.ToHashSet();
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Reading the blockers of user {UserId} failed, the cached block lists decide", targetID);
				return null;
			}
		}

		// The users among the given ones who are friends of the user, from the database, null when the read failed and the caches decide alone
		public static async Task<HashSet<long>?> FriendsAmongAsync(AppDbContext db, long userID, IReadOnlyCollection<long> userIDs)
		{
			try
			{
				List<long> friends = await db.Friendships.AsNoTracking()
					.Where(f => (f.UserIdLow == userID && userIDs.Contains(f.UserIdHigh)) || (f.UserIdHigh == userID && userIDs.Contains(f.UserIdLow)))
					.Select(f => f.UserIdLow == userID ? f.UserIdHigh : f.UserIdLow).ToListAsync();
				return friends.ToHashSet();
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Reading the friends of user {UserId} failed, the cached friend lists decide", userID);
				return null;
			}
		}

		// Every stored block between two of the given users, null when the read failed and the caches decide alone
		public static async Task<List<(long UserId, long BlockedUserId)>?> BlocksAmongAsync(AppDbContext db, IReadOnlyCollection<long> userIDs)
		{
			try
			{
				var rows = await db.Blocks.AsNoTracking().Where(b => userIDs.Contains(b.UserId) && userIDs.Contains(b.BlockedUserId))
					.Select(b => new { b.UserId, b.BlockedUserId }).ToListAsync();
				return rows.Select(row => (row.UserId, row.BlockedUserId)).ToList();
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Reading the blocks among {Count} matched players failed, the cached block lists decide", userIDs.Count);
				return null;
			}
		}

		// A cached answer the database contradicts was missed or raced, the reload makes the cache agree again
		public static void Resync(long userID)
		{
			if (Local.Of(userID) == null)
			{
				return;
			}
			RealtimeOut.Fire(ReloadAsync(userID));
		}
	}
}
