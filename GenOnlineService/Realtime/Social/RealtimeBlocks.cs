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

	// The cached social sets of the users online on one copy, kept current by the block changes of every copy
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

		public bool HasBlocked(long userID, long otherID)
		{
			UserSocialContainer? container = m_of(userID);
			if (container == null)
			{
				return false;
			}
			lock (container)
			{
				return container.Blocked.Contains(otherID);
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

		// Replaces the user's cached block list with the stored one, so the last change any copy made wins whatever order the messages took
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
					HashSet<long> stored = (await db.Blocks.AsNoTracking().Where(b => b.UserId == userID).Select(b => b.BlockedUserId).ToListAsync()).ToHashSet();
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
							container.Blocked = stored;
							return;
						}
					}
				}
				s_log.LogWarning("The cached block list of user {UserId} stayed as it was, other changes kept arriving during {Attempts} reloads", userID, ReloadAttempts);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Reloading the cached block list of user {UserId} failed", userID);
			}
		}

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

	// Carries block changes between copies on one Redis channel, every copy including the sender applies them and reloads the blocker's list
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
					await m_caches.ReloadAsync(m_dbFactory, change.UserId);
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Applying a block change from another copy failed");
			}
		}
	}

	// Block decisions that stay current across copies: cached lists for the lobby list and matchmaking pairing, the database for joins and formed matches
	public static class RealtimeBlocks
	{
		private static readonly ILogger s_log = AppLog.For(typeof(RealtimeBlocks));
		private static readonly RedisChannel s_channel = RedisChannel.Literal("realtime.blocks");
		private static BlockFanout? s_fanout;

		internal static BlockCaches Local { get; } = new BlockCaches(SocialState.Of);

		public static long Sequence => Local.Sequence;

		private static IDbContextFactory<AppDbContext> DbFactory => ServiceLocator.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();

		internal static async Task StartAsync(ISubscriber subscriber)
		{
			BlockFanout fanout = await BlockFanout.StartAsync(subscriber, s_channel, Local, DbFactory);
			Volatile.Write(ref s_fanout, fanout);
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

		public static Task ReloadAsync(long userID) => Local.ReloadAsync(DbFactory, userID);

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
