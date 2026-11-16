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

using GenOnlineService.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Client;
using static OpenIddict.Client.OpenIddictClientEvents;
using System.Security.Claims;
using Xunit;

namespace GenOnlineService.Tests
{
	// Sign-in providers by kind (identity hub T2): Discord and Google through OpenIddict's web providers, GameReplays as a
	// plain OAuth 2.0 server, Steam through OpenID 2.0, any other OpenID Connect provider as before
	public class ProviderKindsTests
	{
		private static AccountsSettings Read(Dictionary<string, string?> values) => AccountsSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

		private static Dictionary<string, string?> Provider(string name, params (string Key, string Value)[] settings)
		{
			Dictionary<string, string?> values = new() { [$"Accounts:providers:{name}:enabled"] = "true" };
			foreach ((string key, string value) in settings)
			{
				values[$"Accounts:providers:{name}:{key}"] = value;
			}
			return values;
		}

		private static Dictionary<string, string?> AllKinds() => new[]
		{
			Provider("discord", ("client_id", "d-id"), ("client_secret", "d-secret")),
			Provider("google", ("client_id", "g-id"), ("client_secret", "g-secret")),
			Provider("steam", ("application_key", "steam-key")),
			Provider("gamereplays", ("client_id", "gr-id"), ("client_secret", "gr-secret"), ("base_url", "https://www.gamereplays.org/community/modules/oauth/")),
			Provider("idp", ("issuer", "https://idp.test/"), ("client_id", "i-id"))
		}.SelectMany(d => d).ToDictionary(p => p.Key, p => p.Value);

		// a key that names a kind is that kind, any other key is OpenID Connect unless kind says otherwise
		[Fact]
		public void KindFollowsTheKeyOrTheKindSetting()
		{
			Dictionary<string, string?> values = AllKinds();
			foreach (var pair in Provider("gr2", ("kind", "gamereplays"), ("client_id", "x"), ("client_secret", "y"), ("base_url", "https://gr.test")))
			{
				values[pair.Key] = pair.Value;
			}

			Dictionary<string, ProviderKind> kinds = Read(values).EnabledProviders.ToDictionary(p => p.Name, p => p.Kind);

			Assert.Equal(ProviderKind.Discord, kinds["discord"]);
			Assert.Equal(ProviderKind.Google, kinds["google"]);
			Assert.Equal(ProviderKind.Steam, kinds["steam"]);
			Assert.Equal(ProviderKind.GameReplays, kinds["gamereplays"]);
			Assert.Equal(ProviderKind.Oidc, kinds["idp"]);
			Assert.Equal(ProviderKind.GameReplays, kinds["gr2"]);
		}

		[Fact]
		public void KindSpecificSettingsAreRead()
		{
			List<UpstreamProviderSettings> providers = Read(AllKinds()).EnabledProviders.ToList();
			Assert.Equal("steam-key", providers.Single(p => p.Name == "steam").ApplicationKey);
			Assert.Equal("https://www.gamereplays.org/community/modules/oauth", providers.Single(p => p.Name == "gamereplays").BaseUrl);
		}

		[Theory]
		[InlineData("idp", "kind", "facebook", "kind")]
		[InlineData("idp", "client_id", "x", "issuer")]
		[InlineData("discord", "client_id", "x", "client_secret")]
		[InlineData("google", "client_secret", "x", "client_id")]
		[InlineData("gamereplays", "client_id", "x", "client_secret")]
		public void IncompleteProvidersStopTheStart(string name, string key, string value, string named)
		{
			InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Read(Provider(name, (key, value))));
			Assert.Contains($"Accounts:providers:{name}", ex.Message);
			Assert.Contains(named, ex.Message);
		}

		[Theory]
		[InlineData("")]
		[InlineData("not a url")]
		[InlineData("ftp://gr.test/")]
		public void GameReplaysNeedsAnHttpBaseUrl(string baseUrl)
		{
			Dictionary<string, string?> values = Provider("gamereplays", ("client_id", "x"), ("client_secret", "y"), ("base_url", baseUrl));
			InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Read(values));
			Assert.Contains("Accounts:providers:gamereplays:base_url", ex.Message);
		}

		// Steam needs nothing: the Web API key only adds the persona name
		[Fact]
		public void SteamWithoutAKeyIsValid()
		{
			UpstreamProviderSettings steam = Assert.Single(Read(Provider("steam")).EnabledProviders);
			Assert.Equal(ProviderKind.Steam, steam.Kind);
			Assert.Null(steam.ApplicationKey);
		}

		private static UpstreamProviderSettings Of(string name) => Read(AllKinds()).EnabledProviders.Single(p => p.Name == name);

		private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

		// the subject is what the import stored in identities: SteamID64, the Discord snowflake, Google's sub, the GameReplays member id
		[Fact]
		public void SteamSubjectIsTheSteamID64()
		{
			UpstreamProfile profile = UpstreamAccounts.ReadProfile(Of("steam"), Principal(
				(ClaimTypes.NameIdentifier, "https://steamcommunity.com/openid/id/76561197960287930"),
				(ClaimTypes.Name, "Persona")));
			Assert.Equal(("76561197960287930", "Persona"), (profile.Subject, profile.Name));
		}

		[Theory]
		[InlineData("https://evil.example/openid/id/76561197960287930")]
		[InlineData("http://steamcommunity.com/openid/id/76561197960287930")]
		[InlineData("https://steamcommunity.com/openid/id/notanumber")]
		[InlineData("https://steamcommunity.com/openid/id/76561197960287930/extra")]
		[InlineData("76561197960287930")]
		public void SteamSubjectFromAnythingElseIsRefused(string claimedID)
		{
			Assert.Null(UpstreamAccounts.ReadProfile(Of("steam"), Principal((ClaimTypes.NameIdentifier, claimedID))).Subject);
		}

		[Fact]
		public void DiscordAndGoogleUseTheProvidersUserId()
		{
			UpstreamProfile discord = UpstreamAccounts.ReadProfile(Of("discord"), Principal((ClaimTypes.NameIdentifier, "80351110224678912"), (ClaimTypes.Name, "nelly")));
			Assert.Equal(("80351110224678912", "nelly"), (discord.Subject, discord.Name));

			UpstreamProfile google = UpstreamAccounts.ReadProfile(Of("google"), Principal(("sub", "110169484474386276334"), ("name", "Ann Example")));
			Assert.Equal(("110169484474386276334", "Ann Example"), (google.Subject, google.Name));
		}

		// the member's id and members_display_name; GameReplays: "You can ignore username."
		[Fact]
		public void GameReplaysReadsTheMemberFromTheUserInfoAnswer()
		{
			UpstreamProfile flat = UpstreamAccounts.ReadProfile(Of("gamereplays"), Principal(("id", "4242"), ("members_display_name", "GrPlayer"), ("username", "grplayer")));
			Assert.Equal(("4242", "GrPlayer"), (flat.Subject, flat.Name));

			// some answers wrap the member in a user object
			UpstreamProfile wrapped = UpstreamAccounts.ReadProfile(Of("gamereplays"), Principal(("user", "{\"id\":4343,\"members_display_name\":\"Wrapped\",\"username\":\"other\"}")));
			Assert.Equal(("4343", "Wrapped"), (wrapped.Subject, wrapped.Name));

			Assert.Null(UpstreamAccounts.ReadProfile(Of("gamereplays"), Principal(("id", "1"), ("username", "nobody"))).Name);
			Assert.Null(UpstreamAccounts.ReadProfile(Of("gamereplays"), Principal(("user", "{\"id\":5,\"username\":\"nobody\"}"))).Name);
			Assert.Null(UpstreamAccounts.ReadProfile(Of("gamereplays"), Principal(("username", "nobody"))).Subject);
		}

		// unchanged for OpenID Connect providers: sub (or the name identifier) and preferred_username before name
		[Fact]
		public void OidcProfileIsReadAsBefore()
		{
			UpstreamProfile profile = UpstreamAccounts.ReadProfile(Of("idp"), Principal(("sub", "abc"), ("preferred_username", "alice"), ("name", "Alice A")));
			Assert.Equal(("abc", "alice"), (profile.Subject, profile.Name));
			Assert.Equal("def", UpstreamAccounts.ReadProfile(Of("idp"), Principal((ClaimTypes.NameIdentifier, "def"))).Subject);
		}

		[Fact]
		public void RegistrationsFollowTheKinds()
		{
			List<UpstreamProviderSettings> providers = Read(AllKinds()).EnabledProviders.ToList();
			ServiceCollection services = new();
			services.AddLogging();
			services.AddOpenIddict().AddClient(options =>
			{
				options.AllowAuthorizationCodeFlow();
				options.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
				AccountsSetup.AddUpstreamRegistrations(options, providers);
			});
			AccountsSetup.AddSteamProviders(services.AddAuthentication(), providers);
			using ServiceProvider provider = services.BuildServiceProvider();

			OpenIddictClientOptions client = provider.GetRequiredService<IOptionsMonitor<OpenIddictClientOptions>>().CurrentValue;
			Dictionary<string, OpenIddictClientRegistration> registrations = client.Registrations.ToDictionary(r => r.ProviderName!);

			// steam is not an OpenIddict registration
			Assert.Equal(new[] { "discord", "gamereplays", "google", "idp" }, registrations.Keys.Order());
			Assert.Equal(new[] { "users/auth/discord/callback", "users/auth/gamereplays/callback", "users/auth/google/callback", "users/auth/idp/callback" },
				client.RedirectionEndpointUris.Select(u => u.OriginalString).Order());
			Assert.Equal("d-id", registrations["discord"].ClientId);
			Assert.Equal("users/auth/discord/callback", registrations["discord"].RedirectUri!.OriginalString);
			Assert.Equal("g-id", registrations["google"].ClientId);

			OpenIddictClientRegistration gameReplays = registrations["gamereplays"];
			Assert.Equal(("gr-id", "gr-secret"), (gameReplays.ClientId, gameReplays.ClientSecret));
			// GameReplays: "Scope for you would be user_profile."
			Assert.Equal(new[] { "user_profile" }, gameReplays.Scopes);
			Assert.Equal(new[] { "user_profile" }, gameReplays.Configuration!.ScopesSupported);
			Assert.Equal("users/auth/gamereplays/callback", gameReplays.RedirectUri!.OriginalString);
			Assert.Equal("https://www.gamereplays.org/community/modules/oauth/authorize.php", gameReplays.Configuration.AuthorizationEndpoint!.AbsoluteUri);
			Assert.Equal("https://www.gamereplays.org/community/modules/oauth/token.php", gameReplays.Configuration.TokenEndpoint!.AbsoluteUri);
			Assert.Equal("https://www.gamereplays.org/community/modules/oauth/resource.php", gameReplays.Configuration.UserInfoEndpoint!.AbsoluteUri);

			// without a relayed redirect_uri the built-in endpoint check stays
			Assert.Contains(client.Handlers, h => h.ServiceDescriptor.ServiceType == typeof(OpenIddictClientHandlers.ValidateEndpointUri));
			Assert.DoesNotContain(client.Handlers, h => h.ServiceDescriptor.ServiceType == typeof(RelayedEndpointUri));

			AuthenticationScheme? steam = provider.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync("steam").GetAwaiter().GetResult();
			Assert.NotNull(steam);
			Assert.Equal("/users/auth/steam/openid", provider.GetRequiredService<IOptionsMonitor<AspNet.Security.OpenId.Steam.SteamAuthenticationOptions>>().Get("steam").CallbackPath.Value);
		}
	
		private const string Relay = "https://gatehouse.go.dev.community-outpost.com/login/gamereplays/return";

		private static Dictionary<string, string?> WithRelay(string name = "gamereplays", string uri = Relay)
		{
			Dictionary<string, string?> values = AllKinds();
			values[$"Accounts:providers:{name}:redirect_uri"] = uri;
			return values;
		}

		// a provider registered with another host's return URL sends that exact URL; that host redirects to the callback
		[Fact]
		public void ARelayedRedirectUriIsSentAsIs()
		{
			Assert.Equal(Relay, Read(WithRelay()).EnabledProviders.Single(p => p.Name == "gamereplays").RedirectUri);
			Assert.Null(Of("gamereplays").RedirectUri);

			ServiceCollection services = new();
			services.AddLogging();
			services.AddOpenIddict().AddClient(options =>
			{
				options.AllowAuthorizationCodeFlow();
				options.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
				AccountsSetup.AddUpstreamRegistrations(options, Read(WithRelay()).EnabledProviders.Where(p => p.Kind != ProviderKind.Steam));
			});
			using ServiceProvider provider = services.BuildServiceProvider();
			OpenIddictClientOptions client = provider.GetRequiredService<IOptionsMonitor<OpenIddictClientOptions>>().CurrentValue;

			OpenIddictClientRegistration gameReplays = client.Registrations.Single(r => r.ProviderName == "gamereplays");
			Assert.True(gameReplays.RedirectUri!.IsAbsoluteUri);
			Assert.Equal(Relay, gameReplays.RedirectUri.AbsoluteUri);
			// the callback still answers at the provider's own path
			Assert.Contains("users/auth/gamereplays/callback", client.RedirectionEndpointUris.Select(u => u.OriginalString));
			Assert.Equal("users/auth/discord/callback", client.Registrations.Single(r => r.ProviderName == "discord").RedirectUri!.OriginalString);

			// the built-in endpoint check is replaced by the relay-aware one, at the same place in the pipeline
			Assert.DoesNotContain(client.Handlers, h => h.ServiceDescriptor.ServiceType == typeof(OpenIddictClientHandlers.ValidateEndpointUri));
			OpenIddictClientHandlerDescriptor relayed = Assert.Single(client.Handlers, h => h.ServiceDescriptor.ServiceType == typeof(RelayedEndpointUri));
			Assert.Equal(OpenIddictClientHandlers.ValidateEndpointUri.Descriptor.Order, relayed.Order);
		}

		[Theory]
		[InlineData("gamereplays", "users/auth/gamereplays/callback")]
		[InlineData("gamereplays", "not a url")]
		[InlineData("gamereplays", "ftp://relay.test/return")]
		[InlineData("steam", "https://relay.test/return")]
		public void ABadRelayedRedirectUriStopsTheStart(string name, string uri)
		{
			InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Read(WithRelay(name, uri)));
			Assert.Contains($"Accounts:providers:{name}:redirect_uri", ex.Message);
		}

		private static ProcessAuthenticationContext Callback(string provider, string storedRedirectUri, string requestUri)
		{
			var identity = new ClaimsIdentity("state");
			identity.AddClaim(new Claim(OpenIddictConstants.Claims.Private.EndpointType, "redirection"));
			identity.AddClaim(new Claim(OpenIddictConstants.Claims.Private.RedirectUri, storedRedirectUri));
			identity.AddClaim(new Claim(OpenIddictConstants.Claims.Private.ProviderName, provider));
			// as in the real pipeline: the registration is resolved from the state token only after the endpoint check
			return new ProcessAuthenticationContext(new OpenIddictClientTransaction())
			{
				EndpointType = OpenIddictClientEndpointType.Redirection,
				BaseUri = new Uri("https://api-test.generals.online/"),
				RequestUri = new Uri(requestUri),
				StateTokenPrincipal = new ClaimsPrincipal(identity)
			};
		}

		private static bool Rejected(string provider, string stored, string request)
		{
			ProcessAuthenticationContext context = Callback(provider, stored, request);
			var handler = new RelayedEndpointUri(Read(WithRelay()).EnabledProviders);
			handler.HandleAsync(context).AsTask().GetAwaiter().GetResult();
			// the request URI is left as the host resolved it
			Assert.Equal(request, context.RequestUri!.AbsoluteUri);
			return context.IsRejected;
		}

		// the browser that came back through the relay lands on the provider's own callback, which counts as the relay
		[Fact]
		public void TheRelayedCallbackIsAccepted()
		{
			Assert.False(Rejected("gamereplays", Relay, "https://api-test.generals.online/users/auth/gamereplays/callback?code=c&state=s"));
		}

		// mix-up protection stays: only the relayed provider's own callback stands in for the relay
		[Theory]
		[InlineData("gamereplays", Relay, "https://api-test.generals.online/users/auth/discord/callback?code=c&state=s")]
		[InlineData("gamereplays", Relay, "https://elsewhere.test/users/auth/gamereplays/callback?code=c&state=s")]
		[InlineData("discord", Relay, "https://api-test.generals.online/users/auth/gamereplays/callback?code=c&state=s")]
		[InlineData("discord", "https://api-test.generals.online/users/auth/discord/callback", "https://api-test.generals.online/users/auth/gamereplays/callback?code=c")]
		public void OtherCallbacksAreStillRejected(string provider, string stored, string request)
		{
			Assert.True(Rejected(provider, stored, request));
		}

		// providers without a relay keep the built-in comparison
		[Fact]
		public void UnrelayedCallbacksBehaveAsBuiltIn()
		{
			Assert.False(Rejected("discord", "https://api-test.generals.online/users/auth/discord/callback", "https://api-test.generals.online/users/auth/discord/callback?code=c"));
		}

		// the whole OpenIddict pipeline: a challenge sends the relay URL and keeps it for the token request, and the
		// browser's return at this service's callback passes the endpoint check
		[Fact]
		public async Task TheRelayedFlowPassesTheRealPipeline()
		{
			var services = new ServiceCollection();
			services.AddLogging();
			bool passedEndpointCheck = false;
			services.AddOpenIddict().AddClient(options =>
			{
				options.AllowAuthorizationCodeFlow();
				options.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
				options.DisableTokenStorage();
				AccountsSetup.AddUpstreamRegistrations(options, Read(WithRelay()).EnabledProviders.Where(p => p.Kind != ProviderKind.Steam));
				// stops the flow right after the endpoint check, before any request to GameReplays
				options.AddEventHandler<ProcessAuthenticationContext>(b => b
					.UseInlineHandler(context =>
					{
						passedEndpointCheck = true;
						context.HandleRequest();
						return default;
					})
					.SetOrder(OpenIddictClientHandlers.ValidateEndpointUri.Descriptor.Order + 1));
			});
			using ServiceProvider provider = services.BuildServiceProvider();
			var factory = provider.GetRequiredService<IOpenIddictClientFactory>();
			var dispatcher = provider.GetRequiredService<IOpenIddictClientDispatcher>();
			var baseUri = new Uri("https://api-test.generals.online/");

			OpenIddictClientTransaction start = await factory.CreateTransactionAsync();
			start.Request = new OpenIddictRequest();
			var challenge = new ProcessChallengeContext(start)
			{
				ProviderName = "gamereplays",
				BaseUri = baseUri,
				RequestUri = new Uri(baseUri, "users/auth/gamereplays/login"),
				EndpointType = OpenIddictClientEndpointType.Unknown,
				Principal = new ClaimsPrincipal(new ClaimsIdentity())
			};
			await dispatcher.DispatchAsync(challenge);
			Assert.False(challenge.IsRejected, challenge.ErrorDescription);
			Assert.Equal(Relay, challenge.Request.RedirectUri);
			Assert.Equal("user_profile", challenge.Request.Scope);

			OpenIddictClientTransaction transaction = await factory.CreateTransactionAsync();
			transaction.Request = new OpenIddictRequest { State = challenge.StateToken, Code = "c" };
			var callback = new ProcessAuthenticationContext(transaction)
			{
				EndpointType = OpenIddictClientEndpointType.Redirection,
				BaseUri = baseUri,
				RequestUri = new Uri($"https://api-test.generals.online/users/auth/gamereplays/callback?code=c&state={Uri.EscapeDataString(challenge.StateToken!)}"),
				RequestForgeryProtection = challenge.RequestForgeryProtection
			};
			await dispatcher.DispatchAsync(callback);

			Assert.False(callback.IsRejected, callback.ErrorDescription);
			Assert.True(passedEndpointCheck);
			// the token request sends this same value
			Assert.Equal(Relay, callback.StateTokenPrincipal!.FindFirst(OpenIddictConstants.Claims.Private.RedirectUri)!.Value);
		}
	}
}
