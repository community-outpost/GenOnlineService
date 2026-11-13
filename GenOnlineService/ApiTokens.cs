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

using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

// A machine identity that owns gopat- tokens; it never signs in and is not a user
public class ServiceAccount
{
	public const int NameMaxLength = 64;

	public Int64 ID { get; set; }
	public string Name { get; set; } = String.Empty;
	public string Description { get; set; } = String.Empty;
	public string CreatedBy { get; set; } = String.Empty;
	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;
	public DateTime? DisabledAt { get; set; }
}

public class ServiceAccountPermission
{
	public Int64 ServiceAccountID { get; set; }
	public string Permission { get; set; } = String.Empty;
	public string GrantedBy { get; set; } = String.Empty;
	public DateTime GrantedAt { get; set; } = DateTime.UnixEpoch;
}

// Only the SHA-256 of the full token string is stored; revoked_at in the future is the end of a rotation overlap
public class ApiToken
{
	public const int NameMaxLength = 64;

	public Int64 ID { get; set; }
	public Int64? OwnerUserID { get; set; }
	public Int64? OwnerServiceID { get; set; }
	public string Name { get; set; } = String.Empty;
	public byte[] TokenHash { get; set; } = Array.Empty<byte>();
	public string TokenLast4 { get; set; } = String.Empty;
	public string Scopes { get; set; } = String.Empty;
	public string? AllowedIPs { get; set; }
	public string CreatedBy { get; set; } = String.Empty;
	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;
	public DateTime ExpiresAt { get; set; } = DateTime.UnixEpoch;
	public DateTime? LastUsedAt { get; set; }
	public string? LastUsedIP { get; set; }
	public DateTime? RevokedAt { get; set; }
	public string? RevokedBy { get; set; }
}

public class ServiceAccountConfiguration : IEntityTypeConfiguration<ServiceAccount>
{
	public void Configure(EntityTypeBuilder<ServiceAccount> builder)
	{
		builder.ToTable("service_accounts");

		builder.HasKey(e => e.ID);
		builder.HasIndex(e => e.Name).IsUnique().HasDatabaseName("ux_service_accounts_name");

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(ServiceAccount.NameMaxLength);
		builder.Property(e => e.Description).HasColumnName("description").HasMaxLength(256);
		builder.Property(e => e.CreatedBy).HasColumnName("created_by").HasMaxLength(128);
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		builder.Property(e => e.DisabledAt).HasColumnName("disabled_at");
	}
}

public class ServiceAccountPermissionConfiguration : IEntityTypeConfiguration<ServiceAccountPermission>
{
	public void Configure(EntityTypeBuilder<ServiceAccountPermission> builder)
	{
		builder.ToTable("service_account_permissions");

		builder.HasKey(e => new { e.ServiceAccountID, e.Permission });
		builder.HasOne<ServiceAccount>().WithMany().HasForeignKey(e => e.ServiceAccountID).OnDelete(DeleteBehavior.Cascade);

		builder.Property(e => e.ServiceAccountID).HasColumnName("service_account_id");
		builder.Property(e => e.Permission).HasColumnName("permission").HasMaxLength(64);
		builder.Property(e => e.GrantedBy).HasColumnName("granted_by").HasMaxLength(128);
		builder.Property(e => e.GrantedAt).HasColumnName("granted_at");
	}
}

public class ApiTokenConfiguration : IEntityTypeConfiguration<ApiToken>
{
	public void Configure(EntityTypeBuilder<ApiToken> builder)
	{
		builder.ToTable("api_tokens", t => t.HasCheckConstraint("ck_api_tokens_one_owner", "(owner_user_id IS NULL) <> (owner_service_id IS NULL)"));

		builder.HasKey(e => e.ID);
		builder.HasIndex(e => e.TokenHash).IsUnique().HasDatabaseName("ux_api_tokens_token_hash");
		builder.HasIndex(e => new { e.OwnerUserID, e.Name }).HasDatabaseName("ix_api_tokens_owner_user");
		builder.HasIndex(e => new { e.OwnerServiceID, e.Name }).HasDatabaseName("ix_api_tokens_owner_service");
		// no referential action: MySQL refuses a check constraint on columns a cascading key uses
		builder.HasOne<User>().WithMany().HasForeignKey(e => e.OwnerUserID).OnDelete(DeleteBehavior.Restrict);
		builder.HasOne<ServiceAccount>().WithMany().HasForeignKey(e => e.OwnerServiceID).OnDelete(DeleteBehavior.Restrict);

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.OwnerUserID).HasColumnName("owner_user_id");
		builder.Property(e => e.OwnerServiceID).HasColumnName("owner_service_id");
		builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(ApiToken.NameMaxLength);
		builder.Property(e => e.TokenHash).HasColumnName("token_hash").HasMaxLength(32).IsFixedLength();
		builder.Property(e => e.TokenLast4).HasColumnName("token_last4").HasMaxLength(4).IsFixedLength();
		builder.Property(e => e.Scopes).HasColumnName("scopes");
		builder.Property(e => e.AllowedIPs).HasColumnName("allowed_ips");
		builder.Property(e => e.CreatedBy).HasColumnName("created_by").HasMaxLength(128);
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		builder.Property(e => e.ExpiresAt).HasColumnName("expires_at");
		builder.Property(e => e.LastUsedAt).HasColumnName("last_used_at");
		builder.Property(e => e.LastUsedIP).HasColumnName("last_used_ip").HasMaxLength(45);
		builder.Property(e => e.RevokedAt).HasColumnName("revoked_at");
		builder.Property(e => e.RevokedBy).HasColumnName("revoked_by").HasMaxLength(128);
	}
}

namespace GenOnlineService
{
	public sealed class ApiTokenSettings
	{
		public int MaxLifetimeDays { get; init; } = 365;
		public int PrivilegedMaxLifetimeDays { get; init; } = 30;
		public int MaxActivePerOwner { get; init; } = 20;
		public int ExpiryWarningDays { get; init; } = 14;
		public int RetentionDays { get; init; } = 90;

		public static ApiTokenSettings Read(IConfiguration config)
		{
			IConfigurationSection section = config.GetSection("ApiTokens");
			return new ApiTokenSettings
			{
				MaxLifetimeDays = section.GetValue<int?>("max_lifetime_days") ?? 365,
				PrivilegedMaxLifetimeDays = section.GetValue<int?>("privileged_max_lifetime_days") ?? 30,
				MaxActivePerOwner = section.GetValue<int?>("max_active_per_owner") ?? 20,
				ExpiryWarningDays = section.GetValue<int?>("expiry_warning_days") ?? 14,
				RetentionDays = section.GetValue<int?>("retention_days") ?? 90
			};
		}
	}

	// gopat-<40 base62>; nothing is encoded in it, the database row holds everything
	public static class ApiTokenFormat
	{
		public const string Prefix = "gopat-";
		public const int SecretLength = 40;

		private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
		private static readonly Regex s_pattern = new("^gopat-[0-9A-Za-z]{40}$", RegexOptions.CultureInvariant);

		public static string Generate()
		{
			return Prefix + new string(RandomNumberGenerator.GetItems<char>(Alphabet, SecretLength));
		}

		public static bool IsWellFormed(string token) => s_pattern.IsMatch(token);

		public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

		public static string Last4(string token) => token.Substring(token.Length - 4);

		public static List<string> ParseScopes(string scopes)
		{
			return scopes.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
		}

		// An unparsable entry throws, so a typo never turns into an open allow-list
		public static List<IPNetwork> ParseAllowedIPs(string? allowedIPs)
		{
			var result = new List<IPNetwork>();
			if (String.IsNullOrWhiteSpace(allowedIPs))
			{
				return result;
			}

			foreach (string entry in allowedIPs.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				// short IPv4 forms such as "10.1" are refused so a typo is reported instead of read as another address
				if (!entry.Contains(':') && entry.Split('/')[0].Split('.').Length != 4)
				{
					throw new FormatException($"Not an IP address or CIDR range: {entry}");
				}

				if (IPNetwork.TryParse(entry, out IPNetwork network))
				{
					result.Add(network);
				}
				else if (IPAddress.TryParse(entry, out IPAddress? address))
				{
					result.Add(new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
				}
				else
				{
					throw new FormatException($"Not an IP address or CIDR range: {entry}");
				}
			}

			return result;
		}
	}

	public static class ApiTokenOwnerKinds
	{
		public const string User = "user";
		public const string ServiceAccount = "service_account";
	}

	public static class ApiTokenFailureReasons
	{
		public const string Malformed = "malformed";
		public const string Unknown = "unknown";
		public const string Expired = "expired";
		public const string OwnerInactive = "owner_inactive";
		public const string AddressNotAllowed = "address_not_allowed";
	}

	// The active token set, service account grants and owner validity, rebuilt on the reconcile tick
	public static class ApiTokenCache
	{
		private static readonly ILogger s_log = AppLog.For(typeof(ApiTokenCache));

		public sealed class Entry
		{
			public Int64 ID { get; init; }
			public string Name { get; init; } = String.Empty;
			public string Last4 { get; init; } = String.Empty;
			public string OwnerKind { get; init; } = String.Empty;
			public Int64 OwnerID { get; init; }
			public string OwnerLabel { get; init; } = String.Empty;
			public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();
			public IReadOnlyList<IPNetwork> AllowedIPs { get; init; } = Array.Empty<IPNetwork>();
			public DateTime ExpiresAt { get; init; }
			public DateTime? RevokedAt { get; init; }
		}

		private sealed class Snapshot
		{
			public Dictionary<string, Entry> ByHash = new(StringComparer.Ordinal);
			public Dictionary<Int64, HashSet<string>> ServiceGrants = new();
			public HashSet<Int64> ActiveServiceAccounts = new();
			public HashSet<Int64> ActiveUserOwners = new();
		}

		private static volatile Snapshot s_snapshot = new();
		private static readonly ConcurrentDictionary<Int64, (DateTime At, string IP)> s_lastUsed = new();
		private static DateTime s_lastAllowListCheck = DateTime.MinValue;

		public static IEnumerable<Entry> ActiveTokens => s_snapshot.ByHash.Values;

		public static async Task Reload(AppDbContext db)
		{
			DateTime now = DateTime.UtcNow;

			List<ApiToken> tokens = await db.ApiTokens.AsNoTracking()
				.Where(t => t.ExpiresAt > now && (t.RevokedAt == null || t.RevokedAt > now))
				.ToListAsync();
			List<ServiceAccount> accounts = await db.ServiceAccounts.AsNoTracking().ToListAsync();
			List<ServiceAccountPermission> grants = await db.ServiceAccountPermissions.AsNoTracking().ToListAsync();

			List<Int64> userOwnerIDs = tokens.Where(t => t.OwnerUserID != null).Select(t => t.OwnerUserID!.Value).Distinct().ToList();
			HashSet<Int64> activeUsers = (await db.Users.AsNoTracking()
				.Where(u => userOwnerIDs.Contains(u.ID) && u.State == UserStates.Active)
				.Select(u => u.ID)
				.ToListAsync()).ToHashSet();
			List<Int64> banned = await db.Bans.AsNoTracking()
				.Where(b => userOwnerIDs.Contains(b.UserID) && b.Restriction == BanRestrictions.Play && b.RevokedAt == null && (b.ExpiresAt == null || b.ExpiresAt > now))
				.Select(b => b.UserID)
				.ToListAsync();
			activeUsers.ExceptWith(banned);

			Dictionary<Int64, string> accountNames = accounts.ToDictionary(a => a.ID, a => a.Name);

			var next = new Snapshot
			{
				ActiveServiceAccounts = accounts.Where(a => a.DisabledAt == null).Select(a => a.ID).ToHashSet(),
				ActiveUserOwners = activeUsers
			};

			foreach (IGrouping<Int64, ServiceAccountPermission> group in grants.GroupBy(g => g.ServiceAccountID))
			{
				next.ServiceGrants[group.Key] = group.Select(g => g.Permission).ToHashSet(StringComparer.Ordinal);
			}

			foreach (ApiToken token in tokens)
			{
				List<IPNetwork> allowed;
				try
				{
					allowed = ApiTokenFormat.ParseAllowedIPs(token.AllowedIPs);
				}
				catch (FormatException ex)
				{
					// an unreadable allow-list disables the token instead of opening it
					s_log.LogError("API token {TokenID} has an unreadable allowed_ips value and is ignored: {Error}", token.ID, ex.Message);
					continue;
				}

				bool bService = token.OwnerServiceID != null;
				Int64 ownerID = bService ? token.OwnerServiceID!.Value : token.OwnerUserID!.Value;

				next.ByHash[Convert.ToHexString(token.TokenHash)] = new Entry
				{
					ID = token.ID,
					Name = token.Name,
					Last4 = token.TokenLast4,
					OwnerKind = bService ? ApiTokenOwnerKinds.ServiceAccount : ApiTokenOwnerKinds.User,
					OwnerID = ownerID,
					OwnerLabel = bService ? "service:" + accountNames.GetValueOrDefault(ownerID, ownerID.ToString()) : "user:" + ownerID,
					Scopes = ApiTokenFormat.ParseScopes(token.Scopes),
					AllowedIPs = allowed,
					ExpiresAt = token.ExpiresAt,
					RevokedAt = token.RevokedAt
				};
			}

			s_snapshot = next;
		}

		public static Entry? Find(string token)
		{
			return s_snapshot.ByHash.GetValueOrDefault(Convert.ToHexString(ApiTokenFormat.Hash(token)));
		}

		public static bool IsOwnerActive(Entry entry)
		{
			Snapshot snapshot = s_snapshot;
			return entry.OwnerKind == ApiTokenOwnerKinds.ServiceAccount
				? snapshot.ActiveServiceAccounts.Contains(entry.OwnerID)
				: snapshot.ActiveUserOwners.Contains(entry.OwnerID);
		}

		// Effective permissions are the token scopes the owner still holds right now
		public static List<string> EffectivePermissions(Entry entry)
		{
			Snapshot snapshot = s_snapshot;
			var result = new List<string>();

			foreach (string scope in entry.Scopes)
			{
				bool bHeld = entry.OwnerKind == ApiTokenOwnerKinds.ServiceAccount
					? snapshot.ServiceGrants.TryGetValue(entry.OwnerID, out HashSet<string>? grants) && grants.Contains(scope)
					: !Permissions.IsMachine(scope) && StaffRoles.HasPermission(entry.OwnerID, scope);

				if (bHeld)
				{
					result.Add(scope);
				}
			}

			return result;
		}

		public static void RecordUse(Int64 tokenID, string ip)
		{
			s_lastUsed[tokenID] = (DateTime.UtcNow, ip);
		}

		public static async Task FlushLastUsed(AppDbContext db)
		{
			foreach (Int64 tokenID in s_lastUsed.Keys.ToList())
			{
				if (!s_lastUsed.TryRemove(tokenID, out (DateTime At, string IP) used))
				{
					continue;
				}

				string ip = used.IP.Length > 45 ? used.IP.Substring(0, 45) : used.IP;
				await db.ApiTokens.Where(t => t.ID == tokenID)
					.ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, used.At).SetProperty(t => t.LastUsedIP, ip));
			}
		}

		private static DateTime s_lastHousekeeping = DateTime.MinValue;

		// Once a day: delete rows past retention and warn about tokens close to expiry
		public static async Task Housekeeping(AppDbContext db, ApiTokenSettings settings)
		{
			DateTime now = DateTime.UtcNow;
			if (now - s_lastHousekeeping < TimeSpan.FromDays(1))
			{
				return;
			}

			s_lastHousekeeping = now;
			DateTime cutoff = now.AddDays(-settings.RetentionDays);
			int deleted = await db.ApiTokens.Where(t => (t.RevokedAt != null && t.RevokedAt < cutoff) || t.ExpiresAt < cutoff).ExecuteDeleteAsync();
			if (deleted > 0)
			{
				s_log.LogInformation("Deleted {Count} API token rows past the {Days} day retention", deleted, settings.RetentionDays);
			}

			foreach (Entry token in ActiveTokens.Where(t => t.ExpiresAt < now.AddDays(settings.ExpiryWarningDays)).OrderBy(t => t.ExpiresAt))
			{
				s_log.LogWarning("API token {TokenID} {Name} of {Owner} expires {ExpiresAt:u}", token.ID, token.Name, token.OwnerLabel, token.ExpiresAt);
			}
		}

		// Behind an unconfigured proxy every caller looks like the proxy, so an allow-list would match the proxy
		public static void CheckAllowListBehindProxy(HttpContext context, bool bTrustedProxiesConfigured)
		{
			if (bTrustedProxiesConfigured || !context.Request.Headers.ContainsKey("X-Forwarded-For"))
			{
				return;
			}

			DateTime now = DateTime.UtcNow;
			if (now - s_lastAllowListCheck < TimeSpan.FromHours(1) || !ActiveTokens.Any(t => t.AllowedIPs.Count > 0))
			{
				return;
			}

			s_lastAllowListCheck = now;
			s_log.LogError("API tokens with allowed_ips are active, TrustedProxies is empty and requests carry X-Forwarded-For: every caller appears as the proxy address");
		}
	}

	// Authenticates "Authorization: Bearer gopat-..." and produces a principal PermissionHandler reads through its permission claims
	public sealed class ApiTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
	{
		public const string SchemeName = "gopat";

		public const string TokenIDClaim = "token_id";
		public const string OwnerKindClaim = "owner_kind";
		public const string OwnerIDClaim = "owner_id";
		public const string AuthKindClaim = "auth_kind";

		private static readonly ILogger s_log = AppLog.For(typeof(ApiTokenHandler));

		public ApiTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
			: base(options, logger, encoder)
		{
		}

		// The scheme name is case-insensitive (RFC 9110); the token itself is not
		public static bool HasApiToken(HttpRequest request)
		{
			return BearerValue(request)?.StartsWith(ApiTokenFormat.Prefix, StringComparison.Ordinal) == true;
		}

		private static string? BearerValue(HttpRequest request)
		{
			string header = request.Headers.Authorization.ToString();
			return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header.Substring("Bearer ".Length).Trim() : null;
		}

		protected override Task<AuthenticateResult> HandleAuthenticateAsync()
		{
			if (!HasApiToken(Request))
			{
				return Task.FromResult(AuthenticateResult.NoResult());
			}

			string token = BearerValue(Request)!;

			if (!ApiTokenFormat.IsWellFormed(token))
			{
				return Task.FromResult(Fail(ApiTokenFailureReasons.Malformed, null));
			}

			ApiTokenCache.Entry? entry = ApiTokenCache.Find(token);
			if (entry == null)
			{
				return Task.FromResult(Fail(ApiTokenFailureReasons.Unknown, null));
			}

			DateTime now = DateTime.UtcNow;
			if (entry.ExpiresAt <= now || (entry.RevokedAt != null && entry.RevokedAt <= now))
			{
				return Task.FromResult(Fail(ApiTokenFailureReasons.Expired, entry));
			}

			if (!ApiTokenCache.IsOwnerActive(entry))
			{
				return Task.FromResult(Fail(ApiTokenFailureReasons.OwnerInactive, entry));
			}

			IPAddress? remote = Context.Connection.RemoteIpAddress;
			if (remote != null && remote.IsIPv4MappedToIPv6)
			{
				remote = remote.MapToIPv4();
			}

			if (entry.AllowedIPs.Count > 0 && (remote == null || !entry.AllowedIPs.Any(n => n.Contains(remote))))
			{
				return Task.FromResult(Fail(ApiTokenFailureReasons.AddressNotAllowed, entry));
			}

			var claims = new List<Claim>
			{
				new Claim(ClaimTypes.Name, "token:" + entry.ID),
				new Claim(AuthKindClaim, "token"),
				new Claim(TokenIDClaim, entry.ID.ToString()),
				new Claim(OwnerKindClaim, entry.OwnerKind),
				new Claim(OwnerIDClaim, entry.OwnerID.ToString())
			};

			foreach (string permission in ApiTokenCache.EffectivePermissions(entry))
			{
				claims.Add(new Claim(TokenPrincipal.PermissionClaim, permission));
			}

			string ip = remote?.ToString() ?? String.Empty;
			ApiTokenCache.RecordUse(entry.ID, ip);
			AppMetrics.RecordApiTokenRequest(entry.OwnerLabel);

			var identity = new ClaimsIdentity(claims, TokenPrincipal.AuthenticationType);
			return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
		}

		// The reason goes to the log and metrics only; every caller sees the same 401
		private AuthenticateResult Fail(string reason, ApiTokenCache.Entry? entry)
		{
			AppMetrics.RecordApiTokenAuthFailure(reason);

			if (entry != null)
			{
				s_log.LogWarning("API token {TokenID} (...{Last4}) refused: {Reason}", entry.ID, entry.Last4, reason);
			}
			else
			{
				s_log.LogInformation("API token refused: {Reason}", reason);
			}

			return AuthenticateResult.Fail("invalid token");
		}

		protected override Task HandleChallengeAsync(AuthenticationProperties properties)
		{
			return UniformAuthResponses.WriteUnauthorized(Context);
		}
	}

	public static class ApiTokenPrincipals
	{
		// Only an identity built by the token scheme counts, so a claim carried inside a session token can never pass
		public static bool HasPermission(ClaimsPrincipal user, string permission)
		{
			return user.Identities.Any(i => i.IsAuthenticated && i.AuthenticationType == TokenPrincipal.AuthenticationType && i.HasClaim(TokenPrincipal.PermissionClaim, permission));
		}

		public static Int64? TokenID(ClaimsPrincipal user)
		{
			ClaimsIdentity? identity = user.Identities.FirstOrDefault(i => i.IsAuthenticated && i.AuthenticationType == TokenPrincipal.AuthenticationType);
			return Int64.TryParse(identity?.FindFirst(ApiTokenHandler.TokenIDClaim)?.Value, out Int64 id) ? id : null;
		}
	}

	public static class AuthPolicies
	{
		// A player session of any type, or a machine token holding lobbies.read
		public const string PlayerSessionOrLobbiesRead = "player_session_or_lobbies_read";
	}

	// One 401 body for every authentication failure, so a response never tells why a credential was refused
	public static class UniformAuthResponses
	{
		public static async Task WriteUnauthorized(HttpContext context)
		{
			if (context.Response.HasStarted)
			{
				return;
			}

			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			context.Response.Headers.WWWAuthenticate = "Bearer";
			await context.Response.WriteAsJsonAsync(new { message = "401 Unauthorized", error = "unauthorized" });
		}
	}
}
