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

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Client;
using OpenIddict.Core;
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

		public static void AddGenOnlineIdentity(this IServiceCollection services, AccountsSettings settings)
		{
			// OpenIddict stores modify the entities they read, so their context tracks changes
			services.AddScoped<AppDbContext>(sp =>
			{
				AppDbContext db = sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();
				db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
				return db;
			});

			OpenIddictBuilder openIddict = services.AddOpenIddict()
				.AddCore(options =>
				{
					options.UseEntityFrameworkCore().UseDbContext<AppDbContext>();
					options.ReplaceApplicationManager<OpenIddictEntityFrameworkCoreApplication, LoopbackApplicationManager>();
					options.ReplaceAuthorizationManager<OpenIddictEntityFrameworkCoreAuthorization, SignInAuthorizationManager>();
					options.ReplaceTokenManager<OpenIddictEntityFrameworkCoreToken, SignInTokenManager>();
				});

			if (settings.Enabled)
			{
				AddAccounts(services, openIddict, settings);
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

		private static void AddAccounts(IServiceCollection services, OpenIddictBuilder openIddict, AccountsSettings settings)
		{
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

				options.RegisterScopes(Scopes.OpenId, Scopes.Profile, Scopes.OfflineAccess, AccountsSettings.GameScope);
				options.RegisterAudiences(settings.Audience);

				options.SetAccessTokenLifetime(TimeSpan.FromMinutes(settings.AccessTokenMinutes))
					.SetIdentityTokenLifetime(TimeSpan.FromMinutes(settings.AccessTokenMinutes))
					.SetRefreshTokenLifetime(TimeSpan.FromDays(settings.RefreshTokenDays))
					.SetRefreshTokenReuseLeeway(TimeSpan.FromSeconds(settings.RefreshReuseLeewaySeconds));

				// refresh tokens are stored and rotate on every use; a reused one revokes its whole chain
				options.UseReferenceRefreshTokens();

				// the API reads access tokens as signed JWTs (RS256, typ at+jwt)
				options.DisableAccessTokenEncryption();

				AddKeys(settings, options.AddEphemeralSigningKey, options.AddEphemeralEncryptionKey, c => options.AddSigningCertificate(c), c => options.AddEncryptionCertificate(c));

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
			if (providers.Count > 0)
			{
				openIddict.AddClient(options =>
				{
					options.AllowAuthorizationCodeFlow();
					AddKeys(settings, options.AddEphemeralSigningKey, options.AddEphemeralEncryptionKey, c => options.AddSigningCertificate(c), c => options.AddEncryptionCertificate(c));

					options.SetRedirectionEndpointUris(providers.Select(p => p.CallbackPath).ToArray());

					var aspNetCore = options.UseAspNetCore().EnableRedirectionEndpointPassthrough();
					if (!settings.RequireHttps)
					{
						aspNetCore.DisableTransportSecurityRequirement();
					}

					options.UseSystemNetHttp();

					foreach (UpstreamProviderSettings provider in providers)
					{
						var registration = new OpenIddictClientRegistration
						{
							ProviderName = provider.Name,
							ProviderDisplayName = provider.DisplayName,
							Issuer = new Uri(provider.Issuer, UriKind.Absolute),
							ClientId = provider.ClientID,
							ClientSecret = String.IsNullOrEmpty(provider.ClientSecret) ? null : provider.ClientSecret,
							RedirectUri = new Uri(provider.CallbackPath, UriKind.Relative)
						};
						registration.Scopes.UnionWith(new[] { Scopes.Email, Scopes.Profile });
						options.AddRegistration(registration);
					}
				});
			}
		}

		private static void AddKeys(AccountsSettings settings, Func<object> ephemeralSigning, Func<object> ephemeralEncryption, Action<X509Certificate2> signing, Action<X509Certificate2> encryption)
		{
			if (settings.SigningCertificatePaths.Count == 0 || settings.EncryptionCertificatePaths.Count == 0)
			{
				if (!settings.EphemeralKeys)
				{
					throw new InvalidOperationException("Accounts needs signing_certificate_paths and encryption_certificate_paths (PFX files), or ephemeral_keys for development.");
				}

				ephemeralSigning();
				ephemeralEncryption();
				return;
			}

			// every listed certificate is published, so the next key can be added before the current one is retired
			foreach (string path in settings.SigningCertificatePaths)
			{
				signing(X509CertificateLoader.LoadPkcs12FromFile(path, settings.CertificatePassword, X509KeyStorageFlags.EphemeralKeySet));
			}

			foreach (string path in settings.EncryptionCertificatePaths)
			{
				encryption(X509CertificateLoader.LoadPkcs12FromFile(path, settings.CertificatePassword, X509KeyStorageFlags.EphemeralKeySet));
			}
		}

		// First-party clients come from configuration: public, authorization code with PKCE, refresh tokens
		public static async Task SeedClients(IServiceProvider services, AccountsSettings settings)
		{
			using IServiceScope scope = services.CreateScope();
			var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

			foreach (OAuthClientSettings client in settings.Clients)
			{
				var descriptor = new OpenIddictApplicationDescriptor
				{
					ClientId = client.ClientID,
					DisplayName = client.DisplayName,
					ClientType = ClientTypes.Public,
					ConsentType = ConsentTypes.Implicit,
					Permissions =
					{
						OidcPermissions.Endpoints.Authorization,
						OidcPermissions.Endpoints.Token,
						OidcPermissions.Endpoints.Revocation,
						OidcPermissions.GrantTypes.AuthorizationCode,
						OidcPermissions.GrantTypes.RefreshToken,
						OidcPermissions.ResponseTypes.Code,
						OidcPermissions.Scopes.Profile,
						OidcPermissions.Prefixes.Scope + Scopes.OfflineAccess,
						OidcPermissions.Prefixes.Scope + AccountsSettings.GameScope
					},
					Requirements = { Requirements.Features.ProofKeyForCodeExchange }
				};

				foreach (string uri in client.RedirectUris)
				{
					descriptor.RedirectUris.Add(new Uri(uri, UriKind.Absolute));
				}

				object? existing = await manager.FindByClientIdAsync(client.ClientID);
				if (existing == null)
				{
					await manager.CreateAsync(descriptor);
					s_log.LogInformation("Registered OAuth client {ClientId}", client.ClientID);
				}
				else
				{
					await manager.UpdateAsync(existing, descriptor);
				}
			}
		}
	}
}
