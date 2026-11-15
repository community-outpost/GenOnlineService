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

using GenOnlineService;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// One row per pair, stored with the lower user id first
public class Friendship
{
	public long UserIdLow { get; set; }
	public long UserIdHigh { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;
}

public class FriendRequest
{
	public long FromUserId { get; set; }
	public long ToUserId { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;
}

public class UserBlock
{
	public long UserId { get; set; }
	public long BlockedUserId { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;
}

public class FriendshipConfiguration : IEntityTypeConfiguration<Friendship>
{
	public void Configure(EntityTypeBuilder<Friendship> builder)
	{
		builder.ToTable("friendships");

		builder.HasKey(f => new { f.UserIdLow, f.UserIdHigh });
		builder.HasIndex(f => f.UserIdHigh).HasDatabaseName("ix_friendships_user_id_high");

		builder.Property(f => f.UserIdLow).HasColumnName("user_id_low");
		builder.Property(f => f.UserIdHigh).HasColumnName("user_id_high");
		builder.Property(f => f.CreatedAt).HasColumnName("created_at");
	}
}

public class FriendRequestConfiguration : IEntityTypeConfiguration<FriendRequest>
{
	public void Configure(EntityTypeBuilder<FriendRequest> builder)
	{
		builder.ToTable("friend_requests");

		builder.HasKey(f => new { f.FromUserId, f.ToUserId });
		builder.HasIndex(f => f.ToUserId).HasDatabaseName("ix_friend_requests_to_user_id");

		builder.Property(f => f.FromUserId).HasColumnName("from_user_id");
		builder.Property(f => f.ToUserId).HasColumnName("to_user_id");
		builder.Property(f => f.CreatedAt).HasColumnName("created_at");
	}
}

public class UserBlockConfiguration : IEntityTypeConfiguration<UserBlock>
{
	public void Configure(EntityTypeBuilder<UserBlock> builder)
	{
		builder.ToTable("blocks");

		builder.HasKey(f => new { f.UserId, f.BlockedUserId });

		builder.Property(f => f.UserId).HasColumnName("user_id");
		builder.Property(f => f.BlockedUserId).HasColumnName("blocked_user_id");
		builder.Property(f => f.CreatedAt).HasColumnName("created_at");
	}
}

namespace Database
{
	public static class Social
	{
		private static readonly ILogger s_log = AppLog.For(typeof(Social));

		private static readonly Func<AppDbContext, long, IAsyncEnumerable<Friendship>> _getFriends =
		EF.CompileAsyncQuery(
			(AppDbContext db, long userId) =>
				db.Friendships
				  .Where(f => f.UserIdLow == userId || f.UserIdHigh == userId)
		);

		private static readonly Func<AppDbContext, long, Task<int>> _countFriends =
		EF.CompileAsyncQuery(
			(AppDbContext db, long userId) =>
				db.Friendships
				  .Count(f => f.UserIdLow == userId || f.UserIdHigh == userId)
		);

		private static readonly Func<AppDbContext, long, IAsyncEnumerable<long>> _getBlocked =
		EF.CompileAsyncQuery(
			(AppDbContext db, long userId) =>
				db.Blocks
				  .Where(b => b.UserId == userId)
				  .Select(b => b.BlockedUserId)
		);
		private static readonly Func<AppDbContext, long, IAsyncEnumerable<long>> _getPendingRequests =
		EF.CompileAsyncQuery(
			(AppDbContext db, long targetUserId) =>
				db.FriendRequests
				  .Where(r => r.ToUserId == targetUserId)
				  .Select(r => r.FromUserId)
		);




		public static async Task<HashSet<long>> GetFriends(AppDbContext db, long userId)
		{
			HashSet<long> result = new();

			try
			{
				await foreach (var f in _getFriends(db, userId))
				{
					result.Add(f.UserIdLow == userId ? f.UserIdHigh : f.UserIdLow);
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetFriends failed");
			}

			return result;
		}


		public static async Task<int> CountFriends(AppDbContext db, long userId)
		{
			try
			{
				return await _countFriends(db, userId);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "CountFriends failed");

				// Treat an unreadable count as full so the friends limit is never bypassed.
				return int.MaxValue;
			}
		}


		public static async Task<HashSet<long>> GetBlocked(AppDbContext db, long sourceUserId)
		{
			HashSet<long> result = new();

			try
			{
				await foreach (var id in _getBlocked(db, sourceUserId))
					result.Add(id);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetBlocked failed");
			}

			return result;
		}


		public static async Task<HashSet<long>> GetPendingFriendsRequests(AppDbContext db, long targetUserId)
		{
			HashSet<long> result = new();

			try
			{
				await foreach (var id in _getPendingRequests(db, targetUserId))
					result.Add(id);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetPendingFriendsRequests failed");
			}

			return result;
		}


		public static async Task RemovePendingFriendRequest(AppDbContext db, long sourceUserId, long targetUserId)
		{
			try
			{
				await db.FriendRequests
					.Where(r =>
						(r.FromUserId == sourceUserId && r.ToUserId == targetUserId) ||
						(r.FromUserId == targetUserId && r.ToUserId == sourceUserId))
					.ExecuteDeleteAsync();
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "RemovePendingFriendRequest failed");
			}
		}

		public static async Task CreateFriendship(AppDbContext db, long userId1, long userId2)
		{
			try
			{
				long low = Math.Min(userId1, userId2);
				long high = Math.Max(userId1, userId2);

				// Duplicate calls are expected; the pair key makes one row
				if (await db.Friendships.AnyAsync(f => f.UserIdLow == low && f.UserIdHigh == high))
					return;

				db.Friendships.Add(new Friendship
				{
					UserIdLow = low,
					UserIdHigh = high,
					CreatedAt = DateTime.UtcNow
				});

				await db.SaveChangesAsync();
			}
			catch (Exception ex)
			{
				// A concurrent call already created the friendship
				if (DbHelpers.IsUniqueViolation(ex))
					return;

				s_log.LogError(ex, "CreateFriendship failed");
			}
		}

		public static async Task RemoveFriendship(AppDbContext db, long userId1, long userId2)
		{
			try
			{
				long low = Math.Min(userId1, userId2);
				long high = Math.Max(userId1, userId2);

				await db.Friendships
					.Where(f => f.UserIdLow == low && f.UserIdHigh == high)
					.ExecuteDeleteAsync();
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "RemoveFriendship failed");
			}
		}

		public static async Task AddBlock(AppDbContext db, long sourceUserId, long targetUserId)
		{
			try
			{
				db.Blocks.Add(new UserBlock
				{
					UserId = sourceUserId,
					BlockedUserId = targetUserId,
					CreatedAt = DateTime.UtcNow
				});

				await db.SaveChangesAsync();
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "AddBlock failed");
			}
		}

		public static async Task RemoveBlock(AppDbContext db, long sourceUserId, long targetUserId)
		{
			try
			{
				await db.Blocks
					.Where(b => b.UserId == sourceUserId && b.BlockedUserId == targetUserId)
					.ExecuteDeleteAsync();
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "RemoveBlock failed");
			}
		}

		// Requests the user sent that are still waiting for an answer
		public static async Task<HashSet<long>> GetOutgoingFriendRequests(AppDbContext db, long sourceUserId)
		{
			HashSet<long> result = new();

			try
			{
				List<long> ids = await db.FriendRequests.AsNoTracking().Where(r => r.FromUserId == sourceUserId).Select(r => r.ToUserId).ToListAsync();
				result.UnionWith(ids);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetOutgoingFriendRequests failed");
			}

			return result;
		}

		public static async Task<bool> HasFriendRequest(AppDbContext db, long fromUserId, long toUserId)
		{
			return await db.FriendRequests.AsNoTracking().AnyAsync(r => r.FromUserId == fromUserId && r.ToUserId == toUserId);
		}

		// Removes one direction only, the request the sender withdraws
		public static async Task RemoveFriendRequest(AppDbContext db, long fromUserId, long toUserId)
		{
			try
			{
				await db.FriendRequests
					.Where(r => r.FromUserId == fromUserId && r.ToUserId == toUserId)
					.ExecuteDeleteAsync();
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "RemoveFriendRequest failed");
			}
		}

		public static async Task<bool> IsBlocked(AppDbContext db, long userId, long blockedUserId)
		{
			return await db.Blocks.AsNoTracking().AnyAsync(b => b.UserId == userId && b.BlockedUserId == blockedUserId);
		}

		public static async Task AddPendingFriendRequest(AppDbContext db, long sourceUserId, long targetUserId)
		{
			try
			{
				db.FriendRequests.Add(new FriendRequest
				{
					FromUserId = sourceUserId,
					ToUserId = targetUserId,
					CreatedAt = DateTime.UtcNow
				});

				await db.SaveChangesAsync();
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "AddPendingFriendRequest failed");
			}
		}


	}
}