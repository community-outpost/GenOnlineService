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
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.Collections.Concurrent;
using GenOnlineService;

// Persisted per-user/per-session-type token state, used to revoke JWTs before their natural expiry.
//
// Two mechanisms live here:
//   1. TokenGeneration - a counter stamped into every issued token as the "tgen" claim. Bumping it
//      instantly invalidates every session AND refresh token previously issued to that user.
//   2. RefreshJti - the jti of the only refresh token currently accepted for that user/session type.
//      Refreshing rotates it, which makes the presented (old) refresh token single-use. The jti it
//      replaced is kept in PreviousRefreshJti for a short grace window so that a client which never
//      received the response to its refresh (dropped connection, timeout) can retry instead of being
//      forced through a full re-login.
public class UserTokenState
{
	public Int64 UserID { get; set; }
	public EUserSessionType SessionType { get; set; } = EUserSessionType.None;
	public int TokenGeneration { get; set; } = 0;
	public string RefreshJti { get; set; } = String.Empty;
	public string PreviousRefreshJti { get; set; } = String.Empty;
	public DateTime PreviousRefreshJtiExpires { get; set; } = DateTime.UnixEpoch;
	public DateTime Updated { get; set; } = DateTime.UnixEpoch;
}

public class UserTokenStateConfiguration : IEntityTypeConfiguration<UserTokenState>
{
	public void Configure(EntityTypeBuilder<UserTokenState> builder)
	{
		// Interim refresh rotation state until OpenIddict authorizations replace it
		builder.ToTable("user_token_states");

		builder.HasKey(e => new { e.UserID, e.SessionType });

		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.SessionType).HasColumnName("session_type");
		builder.Property(e => e.TokenGeneration).HasColumnName("token_generation");
		builder.Property(e => e.RefreshJti).HasColumnName("refresh_jti").HasMaxLength(64);
		builder.Property(e => e.PreviousRefreshJti).HasColumnName("previous_refresh_jti").HasMaxLength(64);
		builder.Property(e => e.PreviousRefreshJtiExpires).HasColumnName("previous_refresh_jti_expires_at");
		builder.Property(e => e.Updated).HasColumnName("updated_at");
	}
}

namespace Database
{
	public static class UserTokens
	{
		private static readonly ILogger s_log = AppLog.For(typeof(UserTokens));

		public static async Task<List<UserTokenState>> GetAll(AppDbContext db)
		{
			try
			{
				return await db.UserTokenStates.AsNoTracking().ToListAsync();
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "UserTokens.GetAll failed");
				return new List<UserTokenState>();
			}
		}

		public static async Task Upsert(AppDbContext db, Int64 userID, EUserSessionType sessionType, int tokenGeneration, string refreshJti, string previousRefreshJti, DateTime previousRefreshJtiExpires)
		{
			try
			{
				DateTime now = DateTime.UtcNow;

				Task<int> UpdateExisting() => db.UserTokenStates
					.Where(t => t.UserID == userID && t.SessionType == sessionType)
					.ExecuteUpdateAsync(s => s
						.SetProperty(t => t.TokenGeneration, tokenGeneration)
						.SetProperty(t => t.RefreshJti, refreshJti)
						.SetProperty(t => t.PreviousRefreshJti, previousRefreshJti)
						.SetProperty(t => t.PreviousRefreshJtiExpires, previousRefreshJtiExpires)
						.SetProperty(t => t.Updated, now));

				if (await UpdateExisting() > 0)
				{
					return;
				}

				db.UserTokenStates.Add(new UserTokenState
				{
					UserID = userID,
					SessionType = sessionType,
					TokenGeneration = tokenGeneration,
					RefreshJti = refreshJti,
					PreviousRefreshJti = previousRefreshJti,
					PreviousRefreshJtiExpires = previousRefreshJtiExpires,
					Updated = now
				});

				try
				{
					await db.SaveChangesAsync();
				}
				catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex))
				{
					// A concurrent call inserted the row first, last write wins
					db.ChangeTracker.Clear();
					await UpdateExisting();
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "UserTokens.Upsert failed");
			}
		}

		public static async Task<List<Int64>> GetBannedUserIDs(AppDbContext db)
		{
			try
			{
				return await Database.Moderation.GetPlayBannedUserIDs(db);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "UserTokens.GetBannedUserIDs failed");
				throw;
			}
		}
	}
}

namespace GenOnlineService
{
	// In-memory authority for token revocation. Reads are lock-free so they can happen on every
	// authenticated request without touching the database; the database is only used so that state
	// survives a restart.
	public static class TokenRevocationManager
	{
		private static readonly ILogger s_log = AppLog.For(typeof(TokenRevocationManager));

		private sealed class CachedState
		{
			public int Generation;
			public string RefreshJti = String.Empty;
			public string PreviousRefreshJti = String.Empty;
			public DateTime PreviousRefreshJtiExpires = DateTime.UnixEpoch;

			// The grace jti redeems at most one refresh.
			public bool PreviousRefreshJtiUsed = false;

			// Refresh jtis this login chain superseded, newest last, kept in memory only.
			public (string Jti, DateTime SupersededAt)[] Superseded = Array.Empty<(string, DateTime)>();
		}

		public enum ERefreshTokenCheck
		{
			Accepted,
			Rejected,
			Reused
		}

		// How long the refresh token that was just rotated out stays usable. Without this a client
		// that never saw the response to its refresh (dropped connection, timeout, retry) would be
		// locked out permanently, because the only token it holds has already been superseded.
		private static readonly TimeSpan s_previousRefreshJtiGrace = TimeSpan.FromMinutes(5);

		// The number of superseded refresh jtis kept per user and session type for reuse detection.
		private const int SupersededJtiHistory = 16;

		private static readonly ConcurrentDictionary<(Int64, EUserSessionType), CachedState> s_state = new();

		// Persisting one key at a time writes the latest state last.
		private static readonly ConcurrentDictionary<(Int64, EUserSessionType), SemaphoreSlim> s_persistLocks = new();

		// Users currently flagged as banned in the database. Refreshed periodically so that a ban
		// applied directly in the database takes effect without waiting for token expiry.
		private static volatile HashSet<Int64> s_bannedUsers = new();
		private static readonly object s_bannedLock = new object();

		private static IDbContextFactory<AppDbContext>? s_dbFactory = null;

		private static readonly EUserSessionType[] s_allSessionTypes = new[]
		{
			EUserSessionType.GameClient,
			EUserSessionType.ChatClient,
			EUserSessionType.GameLauncher
		};

		public static async Task Initialize(IDbContextFactory<AppDbContext> dbFactory, AppDbContext db)
		{
			s_dbFactory = dbFactory;

			foreach (UserTokenState row in await Database.UserTokens.GetAll(db))
			{
				s_state[(row.UserID, row.SessionType)] = new CachedState
				{
					Generation = row.TokenGeneration,
					RefreshJti = row.RefreshJti ?? String.Empty,
					PreviousRefreshJti = row.PreviousRefreshJti ?? String.Empty,
					PreviousRefreshJtiExpires = row.PreviousRefreshJtiExpires
				};
			}

			// Seed the banned set without revoking. Anyone banned while the service was running was
			// already revoked at ban time and that revocation was persisted above.
			List<Int64> banned = await Database.UserTokens.GetBannedUserIDs(db);
			lock (s_bannedLock)
			{
				s_bannedUsers = new HashSet<Int64>(banned);
			}

			s_log.LogInformation("Loaded {StateEntryCount} token state entries, {BannedUserCount} banned users", s_state.Count, banned.Count);
		}

		public static int GetGeneration(Int64 userID, EUserSessionType sessionType)
		{
			return s_state.TryGetValue((userID, sessionType), out CachedState? state) ? state.Generation : 0;
		}

		public static bool IsUserBanned(Int64 userID)
		{
			return s_bannedUsers.Contains(userID);
		}

		// A refresh token is accepted when it is the current one, or the unused grace jti, and carries the current generation.
		// A superseded jti from this login chain presented after its grace window is reuse of a stolen or replayed token.
		private static ERefreshTokenCheck Classify(CachedState? state, string jti, int tokenGeneration, DateTime utcNow)
		{
			if (String.IsNullOrEmpty(jti) || tokenGeneration != (state?.Generation ?? 0))
			{
				return ERefreshTokenCheck.Rejected;
			}

			// A record without a refresh jti predates rotation tracking and accepts the token it is shown.
			if (state == null || String.IsNullOrEmpty(state.RefreshJti) || String.Equals(state.RefreshJti, jti, StringComparison.Ordinal))
			{
				return ERefreshTokenCheck.Accepted;
			}

			if (String.Equals(state.PreviousRefreshJti, jti, StringComparison.Ordinal))
			{
				if (utcNow >= state.PreviousRefreshJtiExpires)
				{
					return ERefreshTokenCheck.Reused;
				}

				return state.PreviousRefreshJtiUsed ? ERefreshTokenCheck.Rejected : ERefreshTokenCheck.Accepted;
			}

			foreach ((string supersededJti, DateTime supersededAt) in state.Superseded)
			{
				if (String.Equals(supersededJti, jti, StringComparison.Ordinal))
				{
					return utcNow >= supersededAt + s_previousRefreshJtiGrace ? ERefreshTokenCheck.Reused : ERefreshTokenCheck.Rejected;
				}
			}

			return ERefreshTokenCheck.Rejected;
		}

		// Read only check for the authentication pipeline, the refresh endpoints rotate with TryRotateRefreshToken.
		public static ERefreshTokenCheck CheckRefreshToken(Int64 userID, EUserSessionType sessionType, string jti, int tokenGeneration)
		{
			s_state.TryGetValue((userID, sessionType), out CachedState? state);
			return Classify(state, jti, tokenGeneration, DateTime.UtcNow);
		}

		// Atomically redeems the presented refresh jti for the new one, so concurrent refreshes with one token cannot both succeed.
		public static async Task<ERefreshTokenCheck> TryRotateRefreshToken(Int64 userID, EUserSessionType sessionType, string presentedJti, int tokenGeneration, string newJti)
		{
			var key = (userID, sessionType);
			while (true)
			{
				DateTime utcNow = DateTime.UtcNow;
				s_state.TryGetValue(key, out CachedState? existing);
				ERefreshTokenCheck check = Classify(existing, presentedJti, tokenGeneration, utcNow);

				// The pipeline already revoked on reuse, so a grace window expiring between the two checks only rejects.
				if (check != ERefreshTokenCheck.Accepted)
				{
					return check;
				}

				CachedState rotated;
				if (existing != null && !String.IsNullOrEmpty(existing.RefreshJti)
					&& !String.Equals(existing.RefreshJti, presentedJti, StringComparison.Ordinal))
				{
					// The grace jti is spent and the token it replaced becomes superseded.
					rotated = new CachedState
					{
						Generation = existing.Generation,
						RefreshJti = newJti,
						PreviousRefreshJti = existing.PreviousRefreshJti,
						PreviousRefreshJtiExpires = existing.PreviousRefreshJtiExpires,
						PreviousRefreshJtiUsed = true,
						Superseded = AppendSuperseded(existing.Superseded, existing.RefreshJti, utcNow)
					};
				}
				else
				{
					rotated = new CachedState
					{
						Generation = existing?.Generation ?? 0,
						RefreshJti = newJti,
						PreviousRefreshJti = presentedJti,
						PreviousRefreshJtiExpires = utcNow.Add(s_previousRefreshJtiGrace),
						PreviousRefreshJtiUsed = false,
						Superseded = AppendSuperseded(existing?.Superseded ?? Array.Empty<(string, DateTime)>(), presentedJti, utcNow)
					};
				}

				bool bSwapped = existing == null ? s_state.TryAdd(key, rotated) : s_state.TryUpdate(key, rotated, existing);
				if (bSwapped)
				{
					await Persist(userID, sessionType);
					return ERefreshTokenCheck.Accepted;
				}
			}
		}

		private static (string, DateTime)[] AppendSuperseded((string Jti, DateTime SupersededAt)[] current, string jti, DateTime utcNow)
		{
			var list = new List<(string, DateTime)>(current.Length + 1);
			foreach (var entry in current)
			{
				if (!String.Equals(entry.Jti, jti, StringComparison.Ordinal))
				{
					list.Add(entry);
				}
			}

			list.Add((jti, utcNow));
			return list.Count > SupersededJtiHistory ? list.GetRange(list.Count - SupersededJtiHistory, SupersededJtiHistory).ToArray() : list.ToArray();
		}

		// A fresh login starts a new chain, so tokens from the previous chain are rejected without counting as reuse.
		public static async Task OnTokensIssued(Int64 userID, EUserSessionType sessionType, string refreshJti)
		{
			s_state.AddOrUpdate(
				(userID, sessionType),
				_ => new CachedState { Generation = 0, RefreshJti = refreshJti },
				(_, existing) => new CachedState { Generation = existing.Generation, RefreshJti = refreshJti });

			await Persist(userID, sessionType);
		}

		// Invalidates every token previously issued to this user across all session types.
		public static async Task RevokeAllTokensForUser(Int64 userID, string reason)
		{
			s_log.LogInformation("Revoking all tokens for user {UserId} ({Reason})", userID, reason);

			foreach (EUserSessionType sessionType in s_allSessionTypes)
			{
				await BumpGeneration(userID, sessionType);
			}
		}

		// Invalidates every session and refresh token previously issued to this user for one session type.
		public static async Task RevokeTokensForSession(Int64 userID, EUserSessionType sessionType, string reason)
		{
			s_log.LogWarning("Revoking {SessionType} tokens for user {UserId} ({Reason})", sessionType, userID, reason);
			await BumpGeneration(userID, sessionType);
		}

		private static async Task BumpGeneration(Int64 userID, EUserSessionType sessionType)
		{
			s_state.AddOrUpdate(
				(userID, sessionType),
				_ => new CachedState { Generation = 1, RefreshJti = String.Empty },
				(_, existing) => new CachedState { Generation = existing.Generation + 1, RefreshJti = String.Empty });

			await Persist(userID, sessionType);
		}

		// Picks up bans applied directly in the database or by the CLI, and drops bans that expired or were revoked
		public static async Task ReconcileBans(AppDbContext db)
		{
			// Don't run before the initial seed, or every already-banned user would look "newly banned".
			if (s_dbFactory == null)
			{
				return;
			}

			List<Int64> banned = await Database.UserTokens.GetBannedUserIDs(db);

			HashSet<Int64> newlyBanned = new HashSet<Int64>();

			lock (s_bannedLock)
			{
				foreach (Int64 userID in banned)
				{
					if (!s_bannedUsers.Contains(userID))
					{
						newlyBanned.Add(userID);
					}
				}

				s_bannedUsers = new HashSet<Int64>(banned);
			}

			foreach (Int64 userID in newlyBanned)
			{
				UserBanStatus? banStatus = await Database.Users.GetUserBanStatus(db, userID);
				await RevokeAllTokensForUser(userID, "user was banned");
				await ModerationManager.DisconnectUser(userID, EModerationAction.Ban, banStatus?.BanReason);
			}
		}

		private static async Task Persist(Int64 userID, EUserSessionType sessionType)
		{
			if (s_dbFactory == null)
			{
				return;
			}

			SemaphoreSlim persistLock = s_persistLocks.GetOrAdd((userID, sessionType), _ => new SemaphoreSlim(1, 1));
			await persistLock.WaitAsync();
			try
			{
				if (!s_state.TryGetValue((userID, sessionType), out CachedState? state))
				{
					return;
				}

				await using AppDbContext db = await s_dbFactory.CreateDbContextAsync();
				await Database.UserTokens.Upsert(db, userID, sessionType, state.Generation, state.RefreshJti, state.PreviousRefreshJti, state.PreviousRefreshJtiExpires);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "TokenRevocation.Persist failed");
			}
			finally
			{
				persistLock.Release();
			}
		}

	}
}
