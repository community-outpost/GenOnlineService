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
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Client;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Core;
using OpenIddict.Server;
using OpenIddict.EntityFrameworkCore.Models;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using static OpenIddict.Abstractions.OpenIddictConstants;
using OidcPermissions = OpenIddict.Abstractions.OpenIddictConstants.Permissions;

namespace GenOnlineService.Identity
{
	// RFC 8252 section 7.3: a native client's loopback redirect URI matches on any port
	public sealed class LoopbackApplicationManager : OpenIddictApplicationManager<OpenIddictEntityFrameworkCoreApplication>
	{
		public LoopbackApplicationManager(
			IOpenIddictApplicationCache<OpenIddictEntityFrameworkCoreApplication> cache,
			ILogger<OpenIddictApplicationManager<OpenIddictEntityFrameworkCoreApplication>> logger,
			IOptionsMonitor<OpenIddictCoreOptions> options,
			IOpenIddictApplicationStore<OpenIddictEntityFrameworkCoreApplication> store)
			: base(cache, logger, options, store)
		{
		}

		public override async ValueTask<bool> ValidateRedirectUriAsync(OpenIddictEntityFrameworkCoreApplication application, string uri, CancellationToken cancellationToken = default)
		{
			if (await base.ValidateRedirectUriAsync(application, uri, cancellationToken))
			{
				return true;
			}

			if (!IsLoopback(uri, out Uri? given))
			{
				return false;
			}

			foreach (string registered in await GetRedirectUrisAsync(application, cancellationToken))
			{
				// only a registration without a port opts in, and host, path and query must match exactly
				if (IsLoopback(registered, out Uri? expected)
					&& expected!.IsDefaultPort
					&& !registered.Contains(":80/", StringComparison.Ordinal)
					&& String.Equals(expected.Host, given!.Host, StringComparison.OrdinalIgnoreCase)
					&& String.Equals(expected.AbsolutePath, given.AbsolutePath, StringComparison.Ordinal)
					&& String.Equals(expected.Query, given.Query, StringComparison.Ordinal)
					&& given.Fragment.Length == 0)
				{
					return true;
				}
			}

			return false;
		}

		private static bool IsLoopback(string uri, out Uri? parsed)
		{
			return Uri.TryCreate(uri, UriKind.Absolute, out parsed)
				&& parsed.Scheme == Uri.UriSchemeHttp
				&& String.IsNullOrEmpty(parsed.UserInfo)
				&& IPAddress.TryParse(parsed.Host.Trim('[', ']'), out IPAddress? address)
				&& IPAddress.IsLoopback(address);
		}
	}

	public static class AccountsSetup
	{
		private static readonly ILogger s_log = AppLog.For(typeof(AccountsSetup));

		// Every scope the server issues; ClientRegistry.PlayerScopes is the part a public client may hold
		public static readonly IReadOnlyList<string> RegisteredScopes = new[] { Scopes.OpenId, Scopes.Profile, Scopes.OfflineAccess, AccountsSettings.GameScope };

		// The OpenIddict stores and managers alone; the clients CLI uses them without the server
		public static OpenIddictBuilder AddOpenIddictCore(this IServiceCollection services)
		{
			// OpenIddict stores modify the entities they read, so their context tracks changes
			services.AddScoped<AppDbContext>(sp =>
			{
				AppDbContext db = sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();
				db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
				return db;
			});

			return services.AddOpenIddict()
				.AddCore(options =>
				{
					options.UseEntityFrameworkCore().UseDbContext<AppDbContext>();
					options.ReplaceApplicationManager<OpenIddictEntityFrameworkCoreApplication, LoopbackApplicationManager>();
					options.ReplaceAuthorizationManager<OpenIddictEntityFrameworkCoreAuthorization, SignInAuthorizationManager>();
					options.ReplaceTokenManager<OpenIddictEntityFrameworkCoreToken, SignInTokenManager>();
				});
		}

		public static void AddGenOnlineIdentity(this IServiceCollection services, AccountsSettings settings, bool development)
		{
			OpenIddictBuilder openIddict = services.AddOpenIddictCore();

			if (settings.Enabled)
			{
				AddAccounts(services, openIddict, settings, development);
			}

			openIddict.AddValidation(options =>
			{
				if (settings.Enabled)
				{
					options.UseLocalServer();
				}
				else
				{
					if (String.IsNullOrWhiteSpace(settings.Issuer))
					{
						throw new InvalidOperationException("Accounts:issuer is required when Accounts:enabled is false.");
					}

					options.SetIssuer(settings.Issuer);
					options.UseSystemNetHttp();
				}

				options.AddAudiences(settings.Audience);
				options.UseAspNetCore();
			});
		}

		private static void AddAccounts(IServiceCollection services, OpenIddictBuilder openIddict, AccountsSettings settings, bool development)
		{
			OAuthKeys? keys = LoadKeys(settings, development);

			services.AddIdentityCore<User>(options =>
				{
					options.User.RequireUniqueEmail = false;
					options.User.AllowedUserNameCharacters = String.Empty;
				})
				.AddUserStore<GoUserStore>()
				.AddSignInManager();

			services.AddAuthentication().AddIdentityCookies(cookies => cookies.ApplicationCookie?.Configure(options =>
			{
				options.LoginPath = "/users/sign_in";
				options.LogoutPath = "/users/sign_out";
				options.AccessDeniedPath = "/users/sign_in";
				options.Cookie.Name = "go_session";
				options.Cookie.SameSite = SameSiteMode.Lax;
				options.Cookie.SecurePolicy = settings.RequireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
				options.ExpireTimeSpan = TimeSpan.FromDays(14);
			}));

			// a blocked user's stamp is rotated, so their website cookie stops within a minute
			services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));

			services.AddScoped<UpstreamAccounts>();

			// the consent page posts back to /oauth/authorize with an antiforgery token
			services.AddAntiforgery(options =>
			{
				options.Cookie.Name = "go_antiforgery";
				options.Cookie.SecurePolicy = settings.RequireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
				options.FormFieldName = "__go_consent_token";
			});

			openIddict.AddServer(options =>
			{
				options.SetAuthorizationEndpointUris("oauth/authorize")
					.SetTokenEndpointUris("oauth/token")
					.SetRevocationEndpointUris("oauth/revoke")
					.SetIntrospectionEndpointUris("oauth/introspect")
					.SetUserInfoEndpointUris("oauth/userinfo")
					.SetJsonWebKeySetEndpointUris("oauth/discovery/keys");

				if (!String.IsNullOrWhiteSpace(settings.Issuer))
				{
					options.SetIssuer(settings.Issuer);
				}

				options.AllowAuthorizationCodeFlow()
					.AllowRefreshTokenFlow()
					.RequireProofKeyForCodeExchange();

				options.RegisterScopes(RegisteredScopes.ToArray());
				options.RegisterAudiences(settings.Audience);

				options.SetAccessTokenLifetime(TimeSpan.FromMinutes(settings.AccessTokenMinutes))
					.SetIdentityTokenLifetime(TimeSpan.FromMinutes(settings.AccessTokenMinutes))
					.SetRefreshTokenLifetime(TimeSpan.FromDays(settings.RefreshTokenDays))
					.SetRefreshTokenReuseLeeway(TimeSpan.FromSeconds(settings.RefreshReuseLeewaySeconds));

				// refresh tokens are stored and rotate on every use; a reused one revokes its whole chain
				options.UseReferenceRefreshTokens();

				// the API reads access tokens as signed JWTs (RS256, typ at+jwt)
				options.DisableAccessTokenEncryption();

				AddKeys(keys, options.AddEphemeralSigningKey, options.AddEphemeralEncryptionKey, c => options.AddSigningCertificate(c), c => options.AddEncryptionCertificate(c));

				var aspNetCore = options.UseAspNetCore()
					.EnableAuthorizationEndpointPassthrough()
					.EnableTokenEndpointPassthrough()
					.EnableUserInfoEndpointPassthrough()
					.EnableStatusCodePagesIntegration();

				if (!settings.RequireHttps)
				{
					aspNetCore.DisableTransportSecurityRequirement();
				}
			});

			List<UpstreamProviderSettings> providers = settings.EnabledProviders.ToList();
			if (providers.Any(p => p.Kind != ProviderKind.Steam))
			{
				openIddict.AddClient(options =>
				{
					options.AllowAuthorizationCodeFlow();
					AddKeys(keys, options.AddEphemeralSigningKey, options.AddEphemeralEncryptionKey, c => options.AddSigningCertificate(c), c => options.AddEncryptionCertificate(c));

					var aspNetCore = options.UseAspNetCore().EnableRedirectionEndpointPassthrough();
					if (!settings.RequireHttps)
					{
						aspNetCore.DisableTransportSecurityRequirement();
					}

					options.UseSystemNetHttp();
					AddUpstreamRegistrations(options, providers);
				});
			}

			AddSteamProviders(services.AddAuthentication(), providers);

			if (keys != null)
			{
				// registered after AddServer and AddClient, so it runs after OpenIddict sorts the keys
				var first = new CurrentCertificateFirst(keys.Signing[0].Thumbprint, keys.Encryption[0].Thumbprint);
				services.AddSingleton<IPostConfigureOptions<OpenIddictServerOptions>>(first);
				services.AddSingleton<IPostConfigureOptions<OpenIddictClientOptions>>(first);
			}
		}

		// Every provider except Steam is an OpenIddict client registration, its callback the passthrough redirection endpoint
		public static void AddUpstreamRegistrations(OpenIddictClientBuilder options, IEnumerable<UpstreamProviderSettings> providers)
		{
			List<UpstreamProviderSettings> registered = providers.Where(p => p.Kind != ProviderKind.Steam).ToList();
			options.SetRedirectionEndpointUris(registered.Select(p => p.CallbackPath).ToArray());

			// a provider whose return URL is another host's comes back here through that host's redirect, which the
			// built-in endpoint check would take for a mix-up
			if (registered.Any(p => p.RedirectUri != null))
			{
				options.RemoveEventHandler(OpenIddictClientHandlers.ValidateEndpointUri.Descriptor);
				options.AddEventHandler(RelayedEndpointUri.CreateDescriptor(new RelayedEndpointUri(registered)));
			}

			foreach (UpstreamProviderSettings provider in registered)
			{
				// an email is asked for only where it may link an existing account
				string[] email = provider.AutoLinkVerifiedEmail ? new[] { Scopes.Email } : Array.Empty<string>();
				switch (provider.Kind)
				{
					case ProviderKind.Discord:
						options.UseWebProviders().AddDiscord(discord => discord
							.SetProviderName(provider.Name)
							.SetProviderDisplayName(provider.DisplayName)
							.SetClientId(provider.ClientID)
							.SetClientSecret(provider.ClientSecret)
							.SetRedirectUri(provider.ReturnUri)
							.AddScopes(email.Prepend("identify").ToArray()));
						break;

					case ProviderKind.Google:
						options.UseWebProviders().AddGoogle(google => google
							.SetProviderName(provider.Name)
							.SetProviderDisplayName(provider.DisplayName)
							.SetClientId(provider.ClientID)
							.SetClientSecret(provider.ClientSecret)
							.SetRedirectUri(provider.ReturnUri)
							.AddScopes(email.Prepend(Scopes.Profile).ToArray()));
						break;

					case ProviderKind.GameReplays:
						options.AddRegistration(GameReplaysRegistration(provider));
						break;

					default:
						var registration = new OpenIddictClientRegistration
						{
							ProviderName = provider.Name,
							ProviderDisplayName = provider.DisplayName,
							Issuer = new Uri(provider.Issuer, UriKind.Absolute),
							ClientId = provider.ClientID,
							ClientSecret = String.IsNullOrEmpty(provider.ClientSecret) ? null : provider.ClientSecret,
							RedirectUri = new Uri(provider.ReturnUri, UriKind.RelativeOrAbsolute)
						};
						registration.Scopes.UnionWith(new[] { Scopes.Email, Scopes.Profile });
						options.AddRegistration(registration);
						break;
				}
			}
		}

		// GameReplays is a plain OAuth 2.0 server (no discovery, no ID token): authorize.php, token.php with the client
		// secret in the form, then the member from resource.php with the access token; scope user_profile
		// GameReplays: "Scope for you would be user_profile. It will give you the display name, username and email."
		private const string GameReplaysScope = "user_profile";

		private static OpenIddictClientRegistration GameReplaysRegistration(UpstreamProviderSettings provider)
		{
			var issuer = new Uri(provider.BaseUrl + "/", UriKind.Absolute);
			var configuration = new OpenIddictConfiguration
			{
				Issuer = issuer,
				AuthorizationEndpoint = new Uri(issuer, "authorize.php"),
				TokenEndpoint = new Uri(issuer, "token.php"),
				UserInfoEndpoint = new Uri(issuer, "resource.php")
			};
			configuration.GrantTypesSupported.Add(GrantTypes.AuthorizationCode);
			configuration.ResponseTypesSupported.Add(ResponseTypes.Code);
			configuration.ScopesSupported.Add(GameReplaysScope);
			configuration.TokenEndpointAuthMethodsSupported.Add(ClientAuthenticationMethods.ClientSecretPost);

			var registration = new OpenIddictClientRegistration
			{
				ProviderName = provider.Name,
				ProviderDisplayName = provider.DisplayName,
				Issuer = issuer,
				ClientId = provider.ClientID,
				ClientSecret = provider.ClientSecret,
				RedirectUri = new Uri(provider.ReturnUri, UriKind.RelativeOrAbsolute),
				Configuration = configuration
			};
			registration.Scopes.Add(GameReplaysScope);
			return registration;
		}

		// Steam speaks OpenID 2.0: its handler verifies the assertion at SteamHandlerPath, signs into the external cookie
		// and hands over to the provider's callback, where the sign-in goes on like every other provider's
		public static void AddSteamProviders(AuthenticationBuilder authentication, IEnumerable<UpstreamProviderSettings> providers)
		{
			foreach (UpstreamProviderSettings provider in providers.Where(p => p.Kind == ProviderKind.Steam))
			{
				authentication.AddSteam(provider.Name, provider.DisplayName, options =>
				{
					options.CallbackPath = provider.SteamHandlerPath;
					options.SignInScheme = IdentityConstants.ExternalScheme;
					options.ApplicationKey = provider.ApplicationKey;
				});
			}
		}

		private sealed record OAuthKeys(X509Certificate2[] Signing, X509Certificate2[] Encryption);

		// Certificates from configuration or a mounted secret; ephemeral keys only in Development, where a restart may sign everyone out
		private static OAuthKeys? LoadKeys(AccountsSettings settings, bool development)
		{
			if (settings.EphemeralKeys && !development)
			{
				throw new InvalidOperationException("Accounts:ephemeral_keys is for Development only: every restart would sign everyone out. Configure Accounts:signing_certificates and Accounts:encryption_certificates.");
			}

			if (settings.SigningCertificates.Count == 0 || settings.EncryptionCertificates.Count == 0)
			{
				if (!settings.EphemeralKeys)
				{
					throw new InvalidOperationException("Accounts needs signing_certificates and encryption_certificates (a .pfx/.p12 file, a PEM file with its private key, or PEM text), or ephemeral_keys in Development.");
				}

				return null;
			}

			return new OAuthKeys(
				KeyStorage.LoadAll(settings.SigningCertificates, settings.CertificatePassword, "Accounts:signing_certificates"),
				KeyStorage.LoadAll(settings.EncryptionCertificates, settings.CertificatePassword, "Accounts:encryption_certificates"));
		}

		private static void AddKeys(OAuthKeys? keys, Func<object> ephemeralSigning, Func<object> ephemeralEncryption, Action<X509Certificate2> signing, Action<X509Certificate2> encryption)
		{
			if (keys == null)
			{
				ephemeralSigning();
				ephemeralEncryption();
				return;
			}

			// every listed certificate verifies or decrypts (signing keys are published), so the next key is known everywhere before it is used
			foreach (X509Certificate2 certificate in keys.Signing)
			{
				signing(certificate);
			}

			foreach (X509Certificate2 certificate in keys.Encryption)
			{
				encryption(certificate);
			}
		}

		// OpenIddict signs with the certificate that expires last; here the first configured one signs and encrypts, so rotation is an explicit step
		private sealed class CurrentCertificateFirst : IPostConfigureOptions<OpenIddictServerOptions>, IPostConfigureOptions<OpenIddictClientOptions>
		{
			private readonly string m_signing;
			private readonly string m_encryption;

			public CurrentCertificateFirst(string signing, string encryption)
			{
				m_signing = signing;
				m_encryption = encryption;
			}

			public void PostConfigure(string? name, OpenIddictServerOptions options)
			{
				MoveFirst(options.SigningCredentials, c => c.Key, m_signing);
				MoveFirst(options.EncryptionCredentials, c => c.Key, m_encryption);
			}

			public void PostConfigure(string? name, OpenIddictClientOptions options)
			{
				MoveFirst(options.SigningCredentials, c => c.Key, m_signing);
				MoveFirst(options.EncryptionCredentials, c => c.Key, m_encryption);
			}

			private static void MoveFirst<T>(List<T> credentials, Func<T, SecurityKey> key, string thumbprint)
			{
				int index = credentials.FindIndex(c => key(c) is X509SecurityKey x509 && String.Equals(x509.Certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));
				if (index > 0)
				{
					T current = credentials[index];
					credentials.RemoveAt(index);
					credentials.Insert(0, current);
				}
			}
		}

		// Seeds the first-party public clients from configuration when their client_id is missing; never updates, the CLI owns them afterwards
		public static async Task SeedClients(IServiceProvider services, AccountsSettings settings)
		{
			using IServiceScope scope = services.CreateScope();
			var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
			AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

			foreach (OAuthClientSettings client in settings.Clients)
			{
				if (await db.Clients.AnyAsync(c => c.ClientID == client.ClientID) || await manager.FindByClientIdAsync(client.ClientID) != null)
				{
					continue;
				}

				List<string> scopes = DefaultScopes(client.Kind);
				string? error = ClientRegistry.Validate(client.ClientID, client.Kind, client.Game, false, scopes, client.RedirectUris)
					?? ClientRegistry.ValidateDetails(client.DisplayName, client.MinVersion);
				if (error != null)
				{
					throw new InvalidOperationException($"Accounts:clients entry {client.ClientID}: {error}");
				}

				await using var transaction = await db.Database.BeginTransactionAsync();
				await manager.CreateAsync(ClientRegistry.Descriptor(client.ClientID, client.DisplayName, false, client.Kind, true, true, client.RedirectUris, scopes));

				DateTime now = DateTime.UtcNow;
				var row = new Client { ClientID = client.ClientID, Kind = client.Kind, Game = client.Game, FirstParty = true, Enabled = true, MinVersion = client.MinVersion, CreatedAt = now, UpdatedAt = now };
				db.Clients.Add(row);
				await db.SaveChangesAsync();

				db.AuditLog.Add(new AuditLogEntry
				{
					CreatedAt = now,
					ActorKind = AuditActorKinds.Cli,
					Actor = "startup",
					Action = "client_add",
					TargetKind = AuditTargetKinds.Client,
					TargetID = row.ID,
					Source = "config_seed",
					Details = System.Text.Json.JsonSerializer.Serialize(new { client_id = client.ClientID, kind = client.Kind, game = client.Game, first_party = true, type = ClientTypes.Public, scopes })
				});
				await db.SaveChangesAsync();
				await transaction.CommitAsync();

				s_log.LogInformation("Seeded first-party client {ClientId}", client.ClientID);
			}

			await ClientRegistry.Reload(db);
		}

		// A game, launcher or chat client opens a session with the game scope; an app gets the user's profile only
		public static List<string> DefaultScopes(string kind) => kind == ClientKinds.App
			? new List<string> { Scopes.OpenId, Scopes.Profile, Scopes.OfflineAccess }
			: new List<string> { Scopes.OpenId, Scopes.Profile, Scopes.OfflineAccess, AccountsSettings.GameScope };
	}
}
