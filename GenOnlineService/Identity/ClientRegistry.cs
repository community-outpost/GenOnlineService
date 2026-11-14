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
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using System.Text.Json;
using System.Text.RegularExpressions;
using static OpenIddict.Abstractions.OpenIddictConstants;
using OidcPermissions = OpenIddict.Abstractions.OpenIddictConstants.Permissions;

// A registered OAuth client; name, redirects, type, secret and scopes live in the oauth_applications row with the same client_id
public class Client
{
	public const int ClientIDMaxLength = 32;

	public Int64 ID { get; set; }
	public string ClientID { get; set; } = String.Empty;
	public string Kind { get; set; } = String.Empty;
	public string Game { get; set; } = ClientGames.None;
	public bool FirstParty { get; set; }
	public bool Enabled { get; set; } = true;
	public string? MinVersion { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;
	public DateTime UpdatedAt { get; set; } = DateTime.UnixEpoch;
}

// One accepted build of a game client; CRCs are stored widened so every provider keeps the full unsigned range
public class ClientBuild
{
	public Int64 ClientDbID { get; set; }
	public Int64 ExeCrc { get; set; }
	public Int64 IniCrc { get; set; }
}

public static class ClientKinds
{
	public const string Game = "game";
	public const string Launcher = "launcher";
	public const string Chat = "chat";
	public const string App = "app";

	public static readonly IReadOnlyList<string> All = new[] { Game, Launcher, Chat, App };

	// The kind alone decides the session a sign-in may open; an app opens none
	public static EUserSessionType SessionType(string kind) => kind switch
	{
		Game => EUserSessionType.GameClient,
		Launcher => EUserSessionType.GameLauncher,
		Chat => EUserSessionType.ChatClient,
		_ => EUserSessionType.None
	};
}

public static class ClientGames
{
	public const string ZeroHour = "zerohour";
	public const string Generals = "generals";
	public const string None = "none";

	public static readonly IReadOnlyList<string> All = new[] { ZeroHour, Generals, None };
}

public class ClientConfiguration : IEntityTypeConfiguration<Client>
{
	public void Configure(EntityTypeBuilder<Client> builder)
	{
		builder.ToTable("clients");

		builder.HasKey(e => e.ID);
		builder.HasIndex(e => e.ClientID).IsUnique().HasDatabaseName("ux_clients_client_id");

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.ClientID).HasColumnName("client_id").HasMaxLength(Client.ClientIDMaxLength);
		builder.Property(e => e.Kind).HasColumnName("kind").HasMaxLength(16);
		builder.Property(e => e.Game).HasColumnName("game").HasMaxLength(16);
		builder.Property(e => e.FirstParty).HasColumnName("first_party");
		builder.Property(e => e.Enabled).HasColumnName("enabled");
		builder.Property(e => e.MinVersion).HasColumnName("min_version").HasMaxLength(32);
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");
	}
}

public class ClientBuildConfiguration : IEntityTypeConfiguration<ClientBuild>
{
	public void Configure(EntityTypeBuilder<ClientBuild> builder)
	{
		builder.ToTable("client_builds");

		builder.HasKey(e => new { e.ClientDbID, e.ExeCrc, e.IniCrc });
		builder.HasOne<Client>().WithMany().HasForeignKey(e => e.ClientDbID).OnDelete(DeleteBehavior.Cascade);

		builder.Property(e => e.ClientDbID).HasColumnName("client_id");
		builder.Property(e => e.ExeCrc).HasColumnName("exe_crc");
		builder.Property(e => e.IniCrc).HasColumnName("ini_crc");
	}
}

namespace GenOnlineService.Identity
{
	public sealed record AcceptedBuild(uint ExeCrc, uint IniCrc);

	public sealed record RegisteredClient(Int64 ID, string ClientID, string Kind, string Game, bool FirstParty, bool Enabled, string? MinVersion, IReadOnlyList<AcceptedBuild> Builds)
	{
		public EUserSessionType SessionType => ClientKinds.SessionType(Kind);

		// An empty list leaves build identity to the version check's crcfiles and to anti-cheat
		public bool AcceptsBuild(uint exeCrc, uint iniCrc) => Builds.Count == 0 || Builds.Any(b => b.ExeCrc == exeCrc && b.IniCrc == iniCrc);
	}

	// The client list for every request, reloaded on the reconcile tick; no database read per request
	public static class ClientRegistry
	{
		private static readonly ILogger s_log = AppLog.For(typeof(ClientRegistry));
		private static readonly SemaphoreSlim s_reloadGate = new SemaphoreSlim(1, 1);

		private static volatile Dictionary<string, RegisteredClient> s_clients = new(StringComparer.Ordinal);
		private static bool s_seeded;

		public static readonly Regex ClientIDPattern = new("^[a-z0-9_]{3,32}$", RegexOptions.CultureInvariant);

		// The scopes a player's own sign-in may carry; anything beyond them needs a confidential client
		public static readonly IReadOnlyList<string> PlayerScopes = new[] { Scopes.OpenId, Scopes.Profile, Scopes.OfflineAccess, AccountsSettings.GameScope };

		public static IReadOnlyCollection<RegisteredClient> All => s_clients.Values;

		public static RegisteredClient? Find(string? clientID) => clientID == null ? null : s_clients.GetValueOrDefault(clientID);

		public static RegisteredClient? FindEnabled(string? clientID) => Find(clientID) is { Enabled: true } client ? client : null;

		// The session an enabled client's sign-in opens; None for apps, disabled and unknown clients
		public static EUserSessionType SessionTypeOf(string? clientID) => FindEnabled(clientID)?.SessionType ?? EUserSessionType.None;

		public static async Task Reload(AppDbContext db)
		{
			await s_reloadGate.WaitAsync();
			try
			{
				await ReloadCore(db);
			}
			finally
			{
				s_reloadGate.Release();
			}
		}

		private static async Task ReloadCore(AppDbContext db)
		{
			List<Client> rows = await db.Clients.AsNoTracking().ToListAsync();
			ILookup<Int64, ClientBuild> builds = (await db.ClientBuilds.AsNoTracking().ToListAsync()).ToLookup(b => b.ClientDbID);

			Dictionary<string, RegisteredClient> clients = rows.ToDictionary(c => c.ClientID, c => new RegisteredClient(c.ID, c.ClientID, c.Kind, c.Game, c.FirstParty, c.Enabled, c.MinVersion,
				builds[c.ID].Select(b => new AcceptedBuild((uint)b.ExeCrc, (uint)b.IniCrc)).ToList()), StringComparer.Ordinal);

			Dictionary<string, RegisteredClient> previous = s_clients;
			bool bSeeded = s_seeded;
			s_clients = clients;
			s_seeded = true;

			// the first load only seeds; the startup sweep handles clients disabled while the service was stopped
			if (!bSeeded)
			{
				return;
			}

			foreach (RegisteredClient was in previous.Values.Where(c => c.Enabled && clients.GetValueOrDefault(c.ClientID)?.Enabled != true))
			{
				s_log.LogInformation("Client {ClientId} was disabled: revoking its authorizations", was.ClientID);
				await RevokeClient(was.ClientID);
			}
		}

		// Revokes every valid authorization of the client; each revocation ends its sign-in, registration and realtime connections
		public static async Task<int> RevokeClient(string clientID)
		{
			int revoked = 0;
			try
			{
				using IServiceScope scope = ServiceLocator.Services.CreateScope();
				var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
				var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
				var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();

				object? application = await applications.FindByClientIdAsync(clientID);
				string? applicationID = application == null ? null : await applications.GetIdAsync(application);
				if (applicationID == null)
				{
					return 0;
				}

				var targets = new List<object>();
				await foreach (object authorization in authorizations.FindByApplicationIdAsync(applicationID))
				{
					if (await authorizations.HasStatusAsync(authorization, Statuses.Valid))
					{
						targets.Add(authorization);
					}
				}

				foreach (object authorization in targets)
				{
					string? id = await authorizations.GetIdAsync(authorization);
					bool bRevoked = await authorizations.TryRevokeAsync(authorization);
					if (id != null)
					{
						await tokens.RevokeByAuthorizationIdAsync(id);
						revoked += bRevoked ? 1 : 0;
					}
				}
			}
			catch (Exception ex)
			{
				// the registry still refuses the client's tokens; a failed revocation is only logged
				s_log.LogError(ex, "Revoking the authorizations of client {ClientId} failed", clientID);
			}

			s_log.LogInformation("Revoked {Count} authorization(s) of client {ClientId}", revoked, clientID);
			return revoked;
		}

		// A client disabled while the service was down kept its authorizations; startup revokes them in the background
		public static async Task SweepDisabledClients(IDbContextFactory<AppDbContext> dbFactory)
		{
			await using AppDbContext db = await dbFactory.CreateDbContextAsync();
			string valid = Statuses.Valid;

			List<string> disabled = await db.Clients.AsNoTracking()
				.Where(c => !c.Enabled && db.Set<OpenIddictEntityFrameworkCoreAuthorization>().Any(a => a.Status == valid && a.Application != null && a.Application.ClientId == c.ClientID))
				.Select(c => c.ClientID)
				.ToListAsync();

			if (disabled.Count == 0)
			{
				return;
			}

			_ = Task.Run(async () =>
			{
				try
				{
					await using AppDbContext auditDb = await dbFactory.CreateDbContextAsync();
					foreach (string clientID in disabled)
					{
						int revoked = await RevokeClient(clientID);
						Int64 id = Find(clientID)?.ID ?? 0;
						auditDb.AuditLog.Add(new AuditLogEntry
						{
							CreatedAt = DateTime.UtcNow,
							ActorKind = AuditActorKinds.Cli,
							Actor = "startup",
							Action = "revoke_client_sessions",
							TargetKind = AuditTargetKinds.Client,
							TargetID = id,
							Reason = "client disabled while the service was stopped",
							Source = "startup_sweep",
							Details = JsonSerializer.Serialize(new { client_id = clientID, authorizations = revoked })
						});
						await auditDb.SaveChangesAsync();
					}
				}
				catch (Exception ex)
				{
					s_log.LogError(ex, "Startup sweep of disabled clients failed");
				}
			});
		}

		// The OpenIddict side of a client; a disabled client keeps its row but loses its endpoints, so OpenIddict refuses it first
		public static OpenIddictApplicationDescriptor Descriptor(string clientID, string displayName, bool bConfidential, string kind, bool bFirstParty, bool bEnabled,
			IEnumerable<string> redirectUris, IEnumerable<string> scopes)
		{
			var descriptor = new OpenIddictApplicationDescriptor
			{
				ClientId = clientID,
				DisplayName = displayName,
				ClientType = bConfidential ? ClientTypes.Confidential : ClientTypes.Public,
				ConsentType = bFirstParty ? ConsentTypes.Implicit : ConsentTypes.Explicit,
				Requirements = { Requirements.Features.ProofKeyForCodeExchange }
			};

			if (bEnabled)
			{
				descriptor.Permissions.UnionWith(new[]
				{
					OidcPermissions.Endpoints.Authorization,
					OidcPermissions.Endpoints.Token,
					OidcPermissions.Endpoints.Revocation,
					OidcPermissions.GrantTypes.AuthorizationCode,
					OidcPermissions.GrantTypes.RefreshToken,
					OidcPermissions.ResponseTypes.Code
				});
			}

			foreach (string scope in scopes.Where(s => s != Scopes.OpenId))
			{
				descriptor.Permissions.Add(OidcPermissions.Prefixes.Scope + scope);
			}

			foreach (string uri in redirectUris)
			{
				descriptor.RedirectUris.Add(new Uri(uri, UriKind.Absolute));
			}

			return descriptor;
		}

		private static readonly Regex s_minVersion = new(@"^\d{1,9}\.\d{1,9}\.\d{1,9}$", RegexOptions.CultureInvariant);

		// The display name and minimum version, shared by the CLI and the config seed; null when both are valid
		public static string? ValidateDetails(string displayName, string? minVersion)
		{
			if (displayName.Trim().Length == 0 || displayName.Length > 100)
			{
				return "The display name must be 1 to 100 characters.";
			}

			if (minVersion != null && !s_minVersion.IsMatch(minVersion))
			{
				return "min_version takes yyyy.mm.patch (for example 2026.11.0).";
			}

			return null;
		}

		// Returns an error message, or null when the combination is allowed
		public static string? Validate(string clientID, string kind, string game, bool bConfidential, IReadOnlyCollection<string> scopes, IReadOnlyCollection<string> redirectUris)
		{
			if (!ClientIDPattern.IsMatch(clientID))
			{
				return "client_id must be 3-32 characters of a-z, 0-9 and _.";
			}

			if (!ClientKinds.All.Contains(kind))
			{
				return $"kind must be one of {String.Join(", ", ClientKinds.All)}.";
			}

			if (!ClientGames.All.Contains(game))
			{
				return $"game must be one of {String.Join(", ", ClientGames.All)}.";
			}

			if ((kind == ClientKinds.Game) != (game != ClientGames.None))
			{
				return "A game client names its game (zerohour or generals); every other kind has game none.";
			}

			string? unknown = scopes.FirstOrDefault(s => !AccountsSetup.RegisteredScopes.Contains(s));
			if (unknown != null)
			{
				return $"Unknown scope {unknown}; registered scopes are {String.Join(", ", AccountsSetup.RegisteredScopes)}.";
			}

			// a public client's id is only a label anyone can copy, so it never carries more than a player's own access
			if (!bConfidential && scopes.Any(s => !PlayerScopes.Contains(s)))
			{
				return "A public client may only hold player scopes (" + String.Join(", ", PlayerScopes) + ").";
			}

			if (kind == ClientKinds.App && scopes.Contains(AccountsSettings.GameScope))
			{
				return "An app opens no game session, so it may not hold the game scope.";
			}

			if (kind != ClientKinds.App && !scopes.Contains(AccountsSettings.GameScope))
			{
				return "A game, launcher or chat client needs the game scope to open its session.";
			}

			if (redirectUris.Count == 0)
			{
				return "At least one redirect URI is required.";
			}

			foreach (string uri in redirectUris)
			{
				if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) || parsed.Fragment.Length > 0
					|| (parsed.Scheme != Uri.UriSchemeHttps && !(parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback)))
				{
					return $"Redirect URI {uri} must be https, or http on a loopback address, without a fragment.";
				}
			}

			return null;
		}
	}
}
