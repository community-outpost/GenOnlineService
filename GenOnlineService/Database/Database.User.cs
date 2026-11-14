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
using System.Text;

// Account states: blocked refuses every sign-in, deactivated is closed by its owner or an operator
public static class UserStates
{
	public const string Active = "active";
	public const string Blocked = "blocked";
	public const string Deactivated = "deactivated";
}

// Sign-in providers an identity row can come from
public static class IdentityProviders
{
	public const string Keycloak = "keycloak";
	public const string Steam = "steam";
	public const string Discord = "discord";
	public const string Google = "google";
	public const string GameReplays = "gamereplays";
}

// Uniqueness keys: NFKC, then upper invariant
public static class NameNormalizer
{
	public static string Normalize(string value)
	{
		return value.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
	}
}

// The account; the identity columns follow ASP.NET Core Identity so the identity work can map onto them
public class User
{
	public const int UsernameMaxLength = 32;
	public const int EmailMaxLength = 254;

	public Int64 ID { get; set; }
	public string Username { get; set; } = String.Empty;
	public string UsernameNormalized { get; set; } = String.Empty;
	public string? Email { get; set; }
	public string? EmailNormalized { get; set; }
	public DateTime? EmailConfirmedAt { get; set; }

	// Null without a local password
	public string? PasswordHash { get; set; }

	public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
	public string State { get; set; } = UserStates.Active;
	public bool TwoFactorEnabled { get; set; }
	public DateTime? LockoutEndAt { get; set; }
	public int FailedSignInCount { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;
	public DateTime UpdatedAt { get; set; } = DateTime.UnixEpoch;
	public DateTime? LastSignInAt { get; set; }
	public DateTime? RenamedAt { get; set; }
}

// Every earlier username of a user, written when the name changes
public class UserUsernameHistory
{
	public Int64 ID { get; set; }
	public Int64 UserID { get; set; }
	public string Username { get; set; } = String.Empty;
	public DateTime ChangedAt { get; set; } = DateTime.UnixEpoch;

	// An actor label such as "user:42" or "cli"
	public string ChangedBy { get; set; } = String.Empty;
}

// A linked sign-in method: the account at provider with extern_uid belongs to user_id
public class UserIdentity
{
	public Int64 ID { get; set; }
	public Int64 UserID { get; set; }
	public string Provider { get; set; } = String.Empty;
	public string ExternUid { get; set; } = String.Empty;
	public string? ExternUsername { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;
	public DateTime? LastUsedAt { get; set; }
}

public class UserLobbyPreferences
{
	public int favorite_color = -1;
	public int favorite_side = -1;
	public string favorite_map = String.Empty;
	public int favorite_starting_money = -1;
	public bool favorite_limit_superweapons = false;
}

public sealed class UserBanStatus
{
	public bool IsBanned { get; set; }
	public string BanReason { get; set; } = String.Empty;
}

public class UserConfiguration : IEntityTypeConfiguration<User>
{
	public void Configure(EntityTypeBuilder<User> builder)
	{
		builder.ToTable("users");

		builder.HasKey(e => e.ID);
		builder.HasIndex(e => e.UsernameNormalized).IsUnique().HasDatabaseName("ux_users_username_normalized");
		builder.HasIndex(e => e.EmailNormalized).IsUnique().HasDatabaseName("ux_users_email_normalized");

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.Username).HasColumnName("username").HasMaxLength(User.UsernameMaxLength);
		builder.Property(e => e.UsernameNormalized).HasColumnName("username_normalized").HasMaxLength(User.UsernameMaxLength);
		builder.Property(e => e.Email).HasColumnName("email").HasMaxLength(User.EmailMaxLength);
		builder.Property(e => e.EmailNormalized).HasColumnName("email_normalized").HasMaxLength(User.EmailMaxLength);
		builder.Property(e => e.EmailConfirmedAt).HasColumnName("email_confirmed_at");
		builder.Property(e => e.PasswordHash).HasColumnName("password_hash").HasMaxLength(255);
		builder.Property(e => e.SecurityStamp).HasColumnName("security_stamp").HasMaxLength(64);
		builder.Property(e => e.State).HasColumnName("state").HasMaxLength(16);
		builder.Property(e => e.TwoFactorEnabled).HasColumnName("two_factor_enabled");
		builder.Property(e => e.LockoutEndAt).HasColumnName("lockout_end_at");
		builder.Property(e => e.FailedSignInCount).HasColumnName("failed_sign_in_count");
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");
		builder.Property(e => e.LastSignInAt).HasColumnName("last_sign_in_at");
		builder.Property(e => e.RenamedAt).HasColumnName("renamed_at");
	}
}

public class UserUsernameHistoryConfiguration : IEntityTypeConfiguration<UserUsernameHistory>
{
	public void Configure(EntityTypeBuilder<UserUsernameHistory> builder)
	{
		builder.ToTable("user_username_history");

		builder.HasKey(e => e.ID);
		builder.HasIndex(e => e.UserID).HasDatabaseName("ix_user_username_history_user_id");

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.Username).HasColumnName("username").HasMaxLength(User.UsernameMaxLength);
		builder.Property(e => e.ChangedAt).HasColumnName("changed_at");
		builder.Property(e => e.ChangedBy).HasColumnName("changed_by").HasMaxLength(64);
	}
}

public class UserIdentityConfiguration : IEntityTypeConfiguration<UserIdentity>
{
	public void Configure(EntityTypeBuilder<UserIdentity> builder)
	{
		builder.ToTable("identities");

		builder.HasKey(e => e.ID);
		builder.HasIndex(e => new { e.Provider, e.ExternUid }).IsUnique().HasDatabaseName("ux_identities_provider_extern_uid");
		builder.HasIndex(e => e.UserID).HasDatabaseName("ix_identities_user_id");

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.Provider).HasColumnName("provider").HasMaxLength(32);
		builder.Property(e => e.ExternUid).HasColumnName("extern_uid").HasMaxLength(128);
		builder.Property(e => e.ExternUsername).HasColumnName("extern_username").HasMaxLength(128);
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		builder.Property(e => e.LastUsedAt).HasColumnName("last_used_at");
	}
}

namespace Database
{
	public static class Users
	{
		private static readonly ILogger s_log = AppLog.For(typeof(Users));

		private const int FavoriteMapMaxLength = 128;

		private static readonly Func<AppDbContext, long, Task<string?>> _getDisplayNameQuery =
			EF.CompileAsyncQuery((AppDbContext db, long userId) =>
				db.Users
				  .AsNoTracking()
				  .Where(u => u.ID == userId)
				  .Select(u => u.Username)
				  .FirstOrDefault());

		private static readonly Func<AppDbContext, long, Task<PlayerPreference?>> _getPreferencesQuery =
			EF.CompileAsyncQuery((AppDbContext db, long userId) =>
				db.PlayerPreferences
				  .AsNoTracking()
				  .FirstOrDefault(p => p.UserID == userId));

		public static async Task<Dictionary<long, EloData>> GetBulkELOData(AppDbContext db, List<long> userIds)
		{
			Dictionary<long, EloData> results = new();

			if (userIds == null || userIds.Count == 0)
				return results;

			try
			{
				results = await PlayerRatings.GetBulk(db, userIds);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetBulkELOData failed");
			}

			foreach (long id in userIds)
			{
				if (!results.ContainsKey(id))
					results[id] = new EloData(EloConfig.BaseRating, EloConfig.BaseRating, 0);
			}

			return results;
		}

		// Creates a user with the next free id, or with userId when the caller keeps an existing id
		public static async Task<User> Create(AppDbContext db, string username, Int64? userId = null)
		{
			DateTime now = DateTime.UtcNow;
			var user = new User
			{
				Username = username,
				UsernameNormalized = NameNormalizer.Normalize(username),
				CreatedAt = now,
				UpdatedAt = now
			};

			if (userId != null)
			{
				user.ID = userId.Value;
			}

			db.Users.Add(user);
			await db.SaveChangesAsync();
			return user;
		}

#if DEBUG
		internal static async Task CreateUserIfNotExists_DevAccount(AppDbContext db, long userId, string displayName)
		{
			displayName = displayName?.Trim() ?? String.Empty;

			if (await db.Users.AnyAsync(u => u.ID == userId))
			{
				return;
			}

			// The debug client repeats this call, so a concurrent insert is expected
			try
			{
				await Create(db, displayName, userId);
			}
			catch
			{
			}
		}
#endif

		public static async Task<Dictionary<long, string>> GetDisplayNameBulk(AppDbContext db, List<long> lstUserIDs)
		{
			var dict = new Dictionary<long, string>(lstUserIDs.Count);

			try
			{
				var rows = await db.Users.AsNoTracking()
					.Where(u => lstUserIDs.Contains(u.ID))
					.Select(u => new { u.ID, u.Username })
					.ToListAsync();

				foreach (var row in rows)
				{
					dict[row.ID] = row.Username;
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetDisplayNameBulk failed");
			}

			return dict;
		}

		// The longest lasting active play ban decides what the player sees
		public static async Task<UserBanStatus?> GetUserBanStatus(AppDbContext db, long userId)
		{
			try
			{
				Sanction? ban = await Database.Moderation.GetActivePlayBan(db, userId);
				if (ban == null)
				{
					return new UserBanStatus { IsBanned = false };
				}

				string reason = ban.ExpiresAt == null ? ban.Reason ?? String.Empty : $"{ban.Reason} (until {ban.ExpiresAt.Value:yyyy-MM-dd HH:mm} UTC)";
				return new UserBanStatus { IsBanned = true, BanReason = reason };
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetUserBanStatus failed");
				return null;
			}
		}

		public static async Task<bool> Exists(AppDbContext db, long userId)
		{
			return await db.Users.AsNoTracking().AnyAsync(u => u.ID == userId);
		}

		public static async Task<string> GetDisplayName(AppDbContext db, long userId)
		{
			try
			{
				return await _getDisplayNameQuery(db, userId) ?? string.Empty;
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetDisplayName failed");
				return string.Empty;
			}
		}

		// A user without a preferences row gets the defaults
		public static async Task<UserLobbyPreferences?> GetUserLobbyPreferences(AppDbContext db, long userId)
		{
			try
			{
				PlayerPreference? row = await _getPreferencesQuery(db, userId);
				if (row == null)
				{
					return new UserLobbyPreferences();
				}

				return new UserLobbyPreferences
				{
					favorite_color = row.Color ?? -1,
					favorite_side = row.Side ?? -1,
					favorite_map = row.Map ?? String.Empty,
					favorite_starting_money = row.StartingMoney ?? -1,
					favorite_limit_superweapons = row.LimitSuperweapons ?? false
				};
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetUserLobbyPreferences failed");
				return null;
			}
		}

		// Sets one preference, creating the row on first use
		private static async Task SetPreference(AppDbContext db, long userId, Action<PlayerPreference> apply, string operation)
		{
			for (int attempt = 0; attempt < 2; attempt++)
			{
				try
				{
					PlayerPreference? row = await db.PlayerPreferences.AsTracking().FirstOrDefaultAsync(p => p.UserID == userId);
					if (row == null)
					{
						row = new PlayerPreference { UserID = userId };
						db.PlayerPreferences.Add(row);
					}

					apply(row);
					row.UpdatedAt = DateTime.UtcNow;
					await db.SaveChangesAsync();
					return;
				}
				catch (DbUpdateException ex) when (attempt == 0 && DbHelpers.IsUniqueViolation(ex))
				{
					// A concurrent call created the row first, so the second attempt updates it
					db.ChangeTracker.Clear();
				}
				catch (Exception ex)
				{
					s_log.LogError(ex, "{Operation} failed", operation);
					db.ChangeTracker.Clear();
					return;
				}
			}
		}

		public static Task SetFavorite_LimitSuperweapons(AppDbContext db, long userId, bool bLimitSuperweapons)
		{
			return SetPreference(db, userId, p => p.LimitSuperweapons = bLimitSuperweapons, "SetFavorite_LimitSuperweapons");
		}

		public static Task SetFavorite_Map(AppDbContext db, long userId, string strMap)
		{
			string cleanMap = TextSanitizer.Clean(strMap, FavoriteMapMaxLength);
			return SetPreference(db, userId, p => p.Map = cleanMap, "SetFavorite_Map");
		}

		public static Task SetFavorite_StartingMoney(AppDbContext db, long userId, int startingMoney)
		{
			return SetPreference(db, userId, p => p.StartingMoney = startingMoney, "SetFavorite_StartingMoney");
		}

		public static Task SetFavorite_Side(AppDbContext db, long userId, int side)
		{
			return SetPreference(db, userId, p => p.Side = side, "SetFavorite_Side");
		}

		public static Task SetFavorite_Color(AppDbContext db, long userId, int color)
		{
			return SetPreference(db, userId, p => p.Color = color, "SetFavorite_Color");
		}

		public static async Task<EloData> GetELOData(AppDbContext db, long userId)
		{
			try
			{
				var apiElo = await ExternalLeaderboardsClient.GetEloFromApi(userId);
				if (apiElo != null)
				{
					// The stored rating is the fallback when the external API is unavailable
					await SaveELOData(db, userId, apiElo);
					return apiElo;
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetELOData API call failed");
			}

			try
			{
				Dictionary<long, EloData> stored = await PlayerRatings.GetBulk(db, new List<long> { userId });
				if (stored.TryGetValue(userId, out EloData? result))
					return result;
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetELOData fallback failed");
			}

			return new EloData(EloConfig.BaseRating, EloConfig.BaseRating, 0);
		}

		// Records the sign-in on the account and as a session event
		public static async Task UpdateLastLoginData(AppDbContext db, long userId, string ipAddr, string client, string? country, bool isReconnect)
		{
			try
			{
				DateTime now = DateTime.UtcNow;

				await db.Users
					.Where(u => u.ID == userId)
					.ExecuteUpdateAsync(setters => setters.SetProperty(u => u.LastSignInAt, now));

				await SessionEvents.Record(db, userId, isReconnect ? SessionEventKinds.Refresh : SessionEventKinds.Start, SessionEventResults.Ok, client, ipAddr, country);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "UpdateLastLoginData failed");
			}
		}

		public static async Task<bool> SetDisplayName(AppDbContext db, long userId, string newName)
		{
			try
			{
				// Client-supplied: refuse a name that contains NUL (PostgreSQL rejects it) or does not fit instead of rewriting it
				if (!String.Equals(TextSanitizer.Clean(newName, User.UsernameMaxLength), newName, StringComparison.Ordinal))
				{
					return false;
				}

				string normalized = NameNormalizer.Normalize(newName);
				if (normalized.Length > User.UsernameMaxLength)
				{
					return false;
				}

				if (await db.Users.AnyAsync(u => u.ID != userId && u.UsernameNormalized == normalized))
				{
					return false;
				}

				string? previous = await db.Users.Where(u => u.ID == userId).Select(u => u.Username).FirstOrDefaultAsync();
				if (previous == null)
				{
					return false;
				}

				if (String.Equals(previous, newName, StringComparison.Ordinal))
				{
					return true;
				}

				DateTime now = DateTime.UtcNow;

				await using var transaction = await db.Database.BeginTransactionAsync();

				await db.Users
					.Where(u => u.ID == userId)
					.ExecuteUpdateAsync(setters => setters
						.SetProperty(u => u.Username, newName)
						.SetProperty(u => u.UsernameNormalized, normalized)
						.SetProperty(u => u.RenamedAt, now)
						.SetProperty(u => u.UpdatedAt, now));

				db.UserUsernameHistory.Add(new UserUsernameHistory
				{
					UserID = userId,
					Username = previous,
					ChangedAt = now,
					ChangedBy = $"user:{userId}"
				});
				await db.SaveChangesAsync();

				await transaction.CommitAsync();
				return true;
			}
			catch (Exception ex)
			{
				db.ChangeTracker.Clear();

				// Two renames to the same name raced; the unique index keeps one
				if (DbHelpers.IsUniqueViolation(ex))
				{
					return false;
				}

				s_log.LogError(ex, "SetDisplayName failed");
				return false;
			}
		}

		public static async Task SaveELOData(AppDbContext db, long userId, EloData newEloData, CancellationToken cancellationToken = default)
		{
			try
			{
				await PlayerRatings.Save(db, userId, newEloData, cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "SaveELOData failed");
			}
		}

		// Returns false when the user does not exist
		public static async Task<bool> SaveExternalELOData(AppDbContext db, long userId, EloData newEloData, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(db);
			ArgumentNullException.ThrowIfNull(newEloData);

			if (!await db.Users.AnyAsync(u => u.ID == userId, cancellationToken))
			{
				return false;
			}

			await PlayerRatings.Save(db, userId, newEloData, cancellationToken);
			return true;
		}
	}
}
