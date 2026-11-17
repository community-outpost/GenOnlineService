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

using GenOnlineService.Names;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MySqlConnector;
using Npgsql;

// All timestamps are UTC. MySQL hands back Unspecified and timestamptz refuses anything but Utc on write.
// Unspecified values are assumed to already be UTC, which is how the service has always used them.
public sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
	public UtcDateTimeConverter()
		: base(v => ToUtc(v), v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
	{
	}

	private static DateTime ToUtc(DateTime value)
	{
		return value.Kind switch
		{
			DateTimeKind.Utc => value,
			DateTimeKind.Local => value.ToUniversalTime(),
			_ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
		};
	}
}

public static class DbHelpers
{
	public static bool IsUniqueViolation(Exception? ex)
	{
		for (Exception? current = ex; current != null; current = current.InnerException)
		{
			if (current is PostgresException pgEx && pgEx.SqlState == PostgresErrorCodes.UniqueViolation)
			{
				return true;
			}

			if (current is MySqlException mySqlEx && mySqlEx.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
			{
				return true;
			}
		}

		return false;
	}
}

// One model for both providers; provider differences live in Database.Providers.cs.
// Every model change needs a migration for BOTH contexts.
public abstract class AppDbContext : DbContext
{
	// Accounts
	public DbSet<User> Users => Set<User>();
	public DbSet<UserUsernameHistory> UserUsernameHistory => Set<UserUsernameHistory>();
	public DbSet<UserIdentity> Identities => Set<UserIdentity>();
	public DbSet<UserRecoveryCode> UserRecoveryCodes => Set<UserRecoveryCode>();
	public DbSet<ApplicationSetting> ApplicationSettings => Set<ApplicationSetting>();
	public DbSet<Client> Clients => Set<Client>();
	public DbSet<ClientBuild> ClientBuilds => Set<ClientBuild>();

	// Moderation and audit
	public DbSet<UserRole> UserRoles => Set<UserRole>();
	public DbSet<Sanction> Sanctions => Set<Sanction>();
	public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

	// Social
	public DbSet<Friendship> Friendships => Set<Friendship>();
	public DbSet<FriendRequest> FriendRequests => Set<FriendRequest>();
	public DbSet<UserBlock> Blocks => Set<UserBlock>();

	// Chat
	public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
	public DbSet<ChatReadMarker> ChatReadMarkers => Set<ChatReadMarker>();
	public DbSet<PlayerReport> PlayerReports => Set<PlayerReport>();
	public DbSet<ChatGroup> ChatGroups => Set<ChatGroup>();
	public DbSet<ChatGroupMember> ChatGroupMembers => Set<ChatGroupMember>();

	// Lobbies
	public DbSet<ConnectionReport> ConnectionReports => Set<ConnectionReport>();
	public DbSet<LobbyIdCounter> LobbyIdCounters => Set<LobbyIdCounter>();
	public DbSet<MatchIdWorker> MatchIdWorkers => Set<MatchIdWorker>();
	public DbSet<RealtimeLease> RealtimeLeases => Set<RealtimeLease>();
	public DbSet<GameSessionRow> GameSessions => Set<GameSessionRow>();

	// Players and devices
	public DbSet<PlayerRating> PlayerRatings => Set<PlayerRating>();
	public DbSet<PlayerStat> PlayerStats => Set<PlayerStat>();
	public DbSet<PlayerPreference> PlayerPreferences => Set<PlayerPreference>();
	public DbSet<PlayerNetwork> PlayerNetwork => Set<PlayerNetwork>();
	public DbSet<Device> Devices => Set<Device>();
	public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();

	// Matches and stats
	public DbSet<Match> Matches => Set<Match>();
	public DbSet<MatchPlayer> MatchPlayers => Set<MatchPlayer>();
	public DbSet<MatchPublication> MatchPublications => Set<MatchPublication>();
	public DbSet<LeaderboardEntry> LeaderboardEntries => Set<LeaderboardEntry>();

	// Anti-cheat
	public DbSet<AnticheatModuleReport> AnticheatModuleReports => Set<AnticheatModuleReport>();
	public DbSet<AnticheatNewAccountGame> AnticheatNewAccountGames => Set<AnticheatNewAccountGame>();
	public DbSet<AnticheatProbeReport> AnticheatProbeReports => Set<AnticheatProbeReport>();
	public DbSet<AnticheatQuickMatchReport> AnticheatQuickMatchReports => Set<AnticheatQuickMatchReport>();

	// Machine tokens
	public DbSet<ServiceAccount> ServiceAccounts => Set<ServiceAccount>();
	public DbSet<ServiceAccountPermission> ServiceAccountPermissions => Set<ServiceAccountPermission>();
	public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

	// ASP.NET Core data protection key ring, shared by every copy
	public DbSet<GenOnlineService.Identity.DataProtectionKeyRow> DataProtectionKeys => Set<GenOnlineService.Identity.DataProtectionKeyRow>();

	// Non-generic so the derived contexts can pass their own DbContextOptions<TDerived>
	protected AppDbContext(DbContextOptions options)
		: base(options)
	{
	}

	public override int SaveChanges(bool acceptAllChangesOnSuccess)
	{
		WriteNameKeys();
		return base.SaveChanges(acceptAllChangesOnSuccess);
	}

	public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
	{
		WriteNameKeys();
		return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
	}

	// A new user or a changed name gets its key here, so sign-up, rename, upstream copy and import never write a stale one
	private void WriteNameKeys()
	{
		foreach (EntityEntry<User> entry in ChangeTracker.Entries<User>())
		{
			if (entry.State == EntityState.Added || entry.State == EntityState.Modified && entry.Property(u => u.Username).IsModified)
			{
				entry.Entity.NameKey = NameKey.Of(entry.Entity.Username);
			}
		}
	}

	protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
	{
		base.ConfigureConventions(configurationBuilder);

		configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

		// Unsigned types are never stored; each widens to the next signed type on both providers.
		configurationBuilder.Properties<ulong>().HaveConversion<long>();
		configurationBuilder.Properties<uint>().HaveConversion<long>();
		configurationBuilder.Properties<ushort>().HaveConversion<int>();
	}

	// Every JSON column, so the derived contexts can set the provider specific column type
	protected static void ConfigureJsonColumns(ModelBuilder modelBuilder, Action<PropertyBuilder> configure)
	{
		configure(modelBuilder.Entity<PlayerStat>().Property(e => e.Stats));
		configure(modelBuilder.Entity<MatchPlayer>().Property(e => e.Details));
		configure(modelBuilder.Entity<AuditLogEntry>().Property(e => e.Details));
		configure(modelBuilder.Entity<ApplicationSetting>().Property(e => e.Value));
		configure(modelBuilder.Entity<PlayerReport>().Property(e => e.Evidence));
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		modelBuilder.ApplyConfiguration(new UserConfiguration());
		if (this is MySqlAppDbContext)
		{
			// the look-alike key compares exactly, as on PostgreSQL, where the default MySQL collations ignore case and accents
			modelBuilder.Entity<User>().Property(u => u.NameKey).UseCollation("utf8mb4_bin");
		}
		modelBuilder.ApplyConfiguration(new UserUsernameHistoryConfiguration());
		modelBuilder.ApplyConfiguration(new UserIdentityConfiguration());
		modelBuilder.ApplyConfiguration(new UserRecoveryCodeConfiguration());
		modelBuilder.ApplyConfiguration(new ApplicationSettingConfiguration());
		modelBuilder.ApplyConfiguration(new ClientConfiguration());
		modelBuilder.ApplyConfiguration(new ClientBuildConfiguration());
		modelBuilder.ApplyConfiguration(new GenOnlineService.Identity.DataProtectionKeyConfiguration());
		modelBuilder.ApplyConfiguration(new UserRoleConfiguration());
		modelBuilder.ApplyConfiguration(new SanctionConfiguration());
		modelBuilder.ApplyConfiguration(new AuditLogConfiguration());
		modelBuilder.ApplyConfiguration(new FriendshipConfiguration());
		modelBuilder.ApplyConfiguration(new FriendRequestConfiguration());
		modelBuilder.ApplyConfiguration(new UserBlockConfiguration());
		modelBuilder.ApplyConfiguration(new ChatMessageConfiguration());
		modelBuilder.ApplyConfiguration(new ChatReadMarkerConfiguration());
		modelBuilder.ApplyConfiguration(new PlayerReportConfiguration());
		modelBuilder.ApplyConfiguration(new ChatGroupConfiguration());
		modelBuilder.ApplyConfiguration(new ChatGroupMemberConfiguration());
		modelBuilder.ApplyConfiguration(new ConnectionReportConfiguration());
		modelBuilder.ApplyConfiguration(new LobbyIdCounterConfiguration());
		modelBuilder.ApplyConfiguration(new MatchIdWorkerConfiguration());
		modelBuilder.ApplyConfiguration(new RealtimeLeaseConfiguration());
		modelBuilder.ApplyConfiguration(new GameSessionRowConfiguration());
		modelBuilder.ApplyConfiguration(new PlayerRatingConfiguration());
		modelBuilder.ApplyConfiguration(new PlayerStatConfiguration());
		modelBuilder.ApplyConfiguration(new PlayerPreferenceConfiguration());
		modelBuilder.ApplyConfiguration(new PlayerNetworkConfiguration());
		modelBuilder.ApplyConfiguration(new DeviceConfiguration());
		modelBuilder.ApplyConfiguration(new SessionEventConfiguration());
		modelBuilder.ApplyConfiguration(new MatchConfiguration());
		modelBuilder.ApplyConfiguration(new MatchPlayerConfiguration());
		modelBuilder.ApplyConfiguration(new MatchPublicationConfiguration());
		modelBuilder.ApplyConfiguration(new LeaderboardEntryConfiguration());
		modelBuilder.ApplyConfiguration(new AnticheatModuleReportConfiguration());
		modelBuilder.ApplyConfiguration(new AnticheatNewAccountGameConfiguration());
		modelBuilder.ApplyConfiguration(new AnticheatProbeReportConfiguration());
		modelBuilder.ApplyConfiguration(new AnticheatQuickMatchReportConfiguration());
		modelBuilder.ApplyConfiguration(new ServiceAccountConfiguration());
		modelBuilder.ApplyConfiguration(new ServiceAccountPermissionConfiguration());
		modelBuilder.ApplyConfiguration(new ApiTokenConfiguration());
		OpenIddictModel.Configure(modelBuilder);
	}
}
