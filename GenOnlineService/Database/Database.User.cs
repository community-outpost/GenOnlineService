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
using GenOnlineService.Names;
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

	// The display name is not unique; the 4-digit tag makes Name#tag unique (DisplayNames)
	public int NameTag { get; set; }

	// The plugin's look-alike key of Username (NameKey.Of), unique with NameTag. AppDbContext writes it on every save.
	public string NameKey { get; set; } = String.Empty;
	public string? Email { get; set; }
	public string? EmailNormalized { get; set; }
	public DateTime? EmailConfirmedAt { get; set; }

	// Null without a local password
	public string? PasswordHash { get; set; }

	public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
	// The TOTP key, encrypted with Data Protection; null without an authenticator app
	public string? AuthenticatorKey { get; set; }
	public string State { get; set; } = UserStates.Active;
	public bool TwoFactorEnabled { get; set; }
	public DateTime? LockoutEndAt { get; set; }
	public int FailedSignInCount { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;
	public DateTime UpdatedAt { get; set; } = DateTime.UnixEpoch;
	public DateTime? LastSignInAt { get; set; }
	public DateTime? RenamedAt { get; set; }

	// When the player was asked to choose a name (a provider sign-up, or staff reset an inappropriate one); null once chosen
	public DateTime? NameRequiredAt { get; set; }
}

// Every earlier username of a user, written when the name changes
public class UserUsernameHistory
{
	public Int64 ID { get; set; }
	public Int64 UserID { get; set; }
	public string Username { get; set; } = String.Empty;

	// The tag the earlier name had; null on rows written before tags existed
	public int? NameTag { get; set; }
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
	// The provider's id only; no provider username is kept (S17)
	public string ExternUid { get; set; } = String.Empty;
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
		builder.HasIndex(e => new { e.UsernameNormalized, e.NameTag }).IsUnique().HasDatabaseName("ux_users_username_normalized_name_tag");
		// closes the race between two look-alike names on one tag, which the code checks cannot
		builder.HasIndex(e => new { e.NameKey, e.NameTag }).IsUnique().HasDatabaseName("ux_users_name_key_name_tag");
		// a mailbox is no identity: several accounts may share one, so this only speeds the lookup
		builder.HasIndex(e => e.EmailNormalized).HasDatabaseName("ix_users_email_normalized");
		// the staff player list's sorts (T17f): last seen newest first (a backward scan serves the other way), and creation
		builder.HasIndex(e => new { e.LastSignInAt, e.ID }).IsDescending(true, true).HasDatabaseName("ix_users_last_sign_in_at");
		builder.HasIndex(e => new { e.CreatedAt, e.ID }).HasDatabaseName("ix_users_created_at");

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.Username).HasColumnName("username").HasMaxLength(User.UsernameMaxLength);
		builder.Property(e => e.UsernameNormalized).HasColumnName("username_normalized").HasMaxLength(User.UsernameMaxLength);
		builder.Property(e => e.NameTag).HasColumnName("name_tag");
		builder.Property(e => e.NameKey).HasColumnName("name_key").HasMaxLength(NameKey.MaxLength);
		builder.Property(e => e.Email).HasColumnName("email").HasMaxLength(User.EmailMaxLength);
		builder.Property(e => e.EmailNormalized).HasColumnName("email_normalized").HasMaxLength(User.EmailMaxLength);
		builder.Property(e => e.EmailConfirmedAt).HasColumnName("email_confirmed_at");
		builder.Property(e => e.PasswordHash).HasColumnName("password_hash").HasMaxLength(255);
		builder.Property(e => e.SecurityStamp).HasColumnName("security_stamp").HasMaxLength(64);
		builder.Property(e => e.AuthenticatorKey).HasColumnName("authenticator_key").HasMaxLength(512);
		builder.Property(e => e.State).HasColumnName("state").HasMaxLength(16);
		builder.Property(e => e.TwoFactorEnabled).HasColumnName("two_factor_enabled");
		builder.Property(e => e.LockoutEndAt).HasColumnName("lockout_end_at");
		builder.Property(e => e.FailedSignInCount).HasColumnName("failed_sign_in_count");
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");
		builder.Property(e => e.LastSignInAt).HasColumnName("last_sign_in_at");
		builder.Property(e => e.RenamedAt).HasColumnName("renamed_at");
		builder.Property(e => e.NameRequiredAt).HasColumnName("name_required_at");
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
		builder.Property(e => e.NameTag).HasColumnName("name_tag");
		builder.Property(e => e.ChangedAt).HasColumnName("changed_at");
		builder.Property(e => e.ChangedBy).HasColumnName("changed_by").HasMaxLength(64);
	}
}

// A two-factor recovery code of a password account: only its SHA-256, and when it was used
public class UserRecoveryCode
{
	public Int64 ID { get; set; }
	public Int64 UserID { get; set; }
	public string CodeHash { get; set; } = String.Empty;
	public DateTime CreatedAt { get; set; }
	public DateTime? UsedAt { get; set; }
}

public class UserRecoveryCodeConfiguration : IEntityTypeConfiguration<UserRecoveryCode>
{
	public void Configure(EntityTypeBuilder<UserRecoveryCode> builder)
	{
		builder.ToTable("user_recovery_codes");

		builder.HasKey(e => e.ID);
		builder.HasIndex(e => e.UserID).HasDatabaseName("ix_user_recovery_codes_user_id");

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.CodeHash).HasColumnName("code_hash").HasMaxLength(64);
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		builder.Property(e => e.UsedAt).HasColumnName("used_at");
	}
}

// A version of a document the player agreed to (S19): rules or terms. Add-only; the key keeps each version once.
public class UserAgreement
{
	public const int DocumentMaxLength = 16;

	public Int64 UserID { get; set; }
	public string Document { get; set; } = String.Empty;
	public string Version { get; set; } = String.Empty;
	public DateTime AgreedAt { get; set; }
}

public class UserAgreementConfiguration : IEntityTypeConfiguration<UserAgreement>
{
	public void Configure(EntityTypeBuilder<UserAgreement> builder)
	{
		builder.ToTable("user_agreements");

		builder.HasKey(e => new { e.UserID, e.Document, e.Version });
		builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserID).OnDelete(DeleteBehavior.Cascade);

		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.Document).HasColumnName("document").HasMaxLength(UserAgreement.DocumentMaxLength);
		builder.Property(e => e.Version).HasColumnName("version").HasMaxLength(GenOnlineService.Identity.AgreementDocument.VersionMaxLength);
		builder.Property(e => e.AgreedAt).HasColumnName("agreed_at");
	}
}

public class UserIdentityConfiguration : IEntityTypeConfiguration<UserIdentity>
{
	public void Configure(EntityTypeBuilder<UserIdentity> builder)
	{
		builder.ToTable("identities");

		builder.HasKey(e => e.ID);
		builder.HasIndex(e => new { e.Provider, e.ExternUid }).IsUnique().HasDatabaseName("ux_identities_provider_extern_uid");
		// one identity per provider per user, so two upstream accounts can never both land on one user
		builder.HasIndex(e => new { e.UserID, e.Provider }).IsUnique().HasDatabaseName("ux_identities_user_id_provider");

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.Provider).HasColumnName("provider").HasMaxLength(32);
		builder.Property(e => e.ExternUid).HasColumnName("extern_uid").HasMaxLength(128);
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		builder.Property(e => e.LastUsedAt).HasColumnName("last_used_at");
	}
}

namespace Database
{
	public readonly record struct StaffPlayerRow(long ID, string Name, int NameTag, string State, DateTime CreatedAt, DateTime? LastSignInAt);

	public static class Users
	{
		private static readonly ILogger s_log = AppLog.For(typeof(Users));

		private const int FavoriteMapMaxLength = 128;

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
				CreatedAt = now,
				UpdatedAt = now
			};
			if (userId != null)
			{
				user.ID = userId.Value;
			}

			for (int attempt = 1; ; attempt++)
			{
				await DisplayNames.AssignNewUser(db, user, attempt);
				db.Users.Add(user);
				try
				{
					await db.SaveChangesAsync();
					return user;
				}
				catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex) && attempt < DisplayNames.NewUserAttempts)
				{
					// a look-alike sign-up took the same tag at the same moment, so pick another
					db.Entry(user).State = EntityState.Detached;
				}
			}
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
				return await db.Users.AsNoTracking().Where(u => u.ID == userId).Select(u => u.Username).FirstOrDefaultAsync() ?? string.Empty;
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
				PlayerPreference? row = await db.PlayerPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserID == userId);
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
