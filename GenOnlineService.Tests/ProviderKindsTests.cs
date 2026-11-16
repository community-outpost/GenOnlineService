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
using OpenIddict.Client;
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
			Provider("gamereplays", ("client_id", "gr-id"), ("client_secret", "gr-secret"), ("base_url", "https://www.gamereplays.org/oauth/")),
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
			Assert.Equal("https://www.gamereplays.org/oauth", providers.Single(p => p.Name == "gamereplays").BaseUrl);
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

		[Fact]
		public void GameReplaysReadsTheMemberFromTheUserInfoAnswer()
		{
			UpstreamProfile flat = UpstreamAccounts.ReadProfile(Of("gamereplays"), Principal(("id", "4242"), ("members_display_name", "GrPlayer"), ("username", "grplayer")));
			Assert.Equal(("4242", "GrPlayer"), (flat.Subject, flat.Name));

			// some answers wrap the member in a user object
			UpstreamProfile wrapped = UpstreamAccounts.ReadProfile(Of("gamereplays"), Principal(("user", "{\"id\":4343,\"username\":\"other\"}")));
			Assert.Equal(("4343", "other"), (wrapped.Subject, wrapped.Name));

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
			Assert.Equal(new[] { "sso" }, gameReplays.Scopes);
			Assert.Equal("https://www.gamereplays.org/oauth/authorize.php", gameReplays.Configuration!.AuthorizationEndpoint!.AbsoluteUri);
			Assert.Equal("https://www.gamereplays.org/oauth/token.php", gameReplays.Configuration.TokenEndpoint!.AbsoluteUri);
			Assert.Equal("https://www.gamereplays.org/oauth/resource.php", gameReplays.Configuration.UserInfoEndpoint!.AbsoluteUri);

			AuthenticationScheme? steam = provider.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync("steam").GetAwaiter().GetResult();
			Assert.NotNull(steam);
			Assert.Equal("/users/auth/steam/openid", provider.GetRequiredService<IOptionsMonitor<AspNet.Security.OpenId.Steam.SteamAuthenticationOptions>>().Get("steam").CallbackPath.Value);
		}
	}
}
