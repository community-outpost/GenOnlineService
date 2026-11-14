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

namespace GenOnlineService.Identity
{
	// A first-party public client inserted on first start when its client_id is missing; the CLI owns it afterwards
	public sealed class OAuthClientSettings
	{
		public string ClientID { get; init; } = String.Empty;
		public string DisplayName { get; init; } = String.Empty;
		public string Kind { get; init; } = String.Empty;
		public string Game { get; init; } = ClientGames.None;
		public string? MinVersion { get; init; }
		public List<string> RedirectUris { get; init; } = new();
	}

	public sealed class GroupSyncSettings
	{
		public bool Enabled { get; init; }
		public int IntervalSeconds { get; init; } = 15;
		public string Realm { get; init; } = String.Empty;
		public string AdminBaseUrl { get; init; } = String.Empty;
		public string ClientID { get; init; } = String.Empty;
		public string ClientSecret { get; init; } = String.Empty;
	}

	// One upstream OpenID Connect provider (Keycloak in the official deployment)
	public sealed class UpstreamProviderSettings
	{
		public string Name { get; init; } = String.Empty;
		public string DisplayName { get; init; } = String.Empty;
		public bool Enabled { get; init; }
		public string Issuer { get; init; } = String.Empty;
		public string ClientID { get; init; } = String.Empty;
		public string ClientSecret { get; init; } = String.Empty;
		public bool AllowSignUp { get; init; } = true;
		public bool OnlyProvider { get; init; }
		public bool AutoLinkVerifiedEmail { get; init; }

		// Only a trusted provider may assert roles, bans and an existing account link
		public bool Trusted { get; init; }
		// The official deployment's Keycloak hands out player ids; with player_ids the claim is the user id itself
		public string PlayerIDClaim { get; init; } = "player_id";
		public bool PlayerIDs { get; init; }
		public string GroupsClaim { get; init; } = "groups";
		public Dictionary<string, string> RoleGroups { get; init; } = new(StringComparer.Ordinal);
		public string? BanGroup { get; init; }
		public GroupSyncSettings GroupSync { get; init; } = new();

		public string Source => AuthoritySources.UpstreamPrefix + Name;
		public string CallbackPath => $"users/auth/{Name}/callback";
	}

	public sealed class AccountsSettings
	{
		public const string GameScope = "game";
		public const string DefaultAudience = "genonline-api";

		// Runs the accounts profile (sign-in pages and the OpenIddict server) in this process
		public bool Enabled { get; init; } = true;

		// The issuer the application profile trusts when accounts runs elsewhere
		public string? Issuer { get; init; }
		public string Audience { get; init; } = DefaultAudience;
		public int AccessTokenMinutes { get; init; } = 15;
		public int RefreshTokenDays { get; init; } = 30;
		public int RefreshReuseLeewaySeconds { get; init; } = 30;
		public List<string> SigningCertificatePaths { get; init; } = new();
		public List<string> EncryptionCertificatePaths { get; init; } = new();
		public string? CertificatePassword { get; init; }

		// Development only: keys live in memory and every restart invalidates every token
		public bool EphemeralKeys { get; init; }
		public bool RequireHttps { get; init; } = true;
		public List<OAuthClientSettings> Clients { get; init; } = new();
		public List<UpstreamProviderSettings> Providers { get; init; } = new();

		public IEnumerable<UpstreamProviderSettings> EnabledProviders => Providers.Where(p => p.Enabled);

		// Keycloak with player ids owns display names and tags; the environment only copies them (identity section 5a)
		public bool NamesFromUpstream => EnabledProviders.Any(p => p.PlayerIDs && p.Trusted);

		public static AccountsSettings Read(IConfiguration config)
		{
			IConfigurationSection section = config.GetSection("Accounts");

			var clients = section.GetSection("clients").GetChildren().Select(c => new OAuthClientSettings
			{
				ClientID = c.GetValue<string>("client_id") ?? String.Empty,
				DisplayName = c.GetValue<string>("display_name") ?? c.GetValue<string>("client_id") ?? String.Empty,
				Kind = c.GetValue<string>("kind") ?? String.Empty,
				Game = c.GetValue<string>("game") ?? ClientGames.None,
				MinVersion = c.GetValue<string>("min_version"),
				RedirectUris = c.GetSection("redirect_uris").Get<List<string>>() ?? new()
			}).Where(c => c.ClientID.Length > 0).ToList();

			var providers = section.GetSection("providers").GetChildren().Select(p => new UpstreamProviderSettings
			{
				Name = p.Key,
				DisplayName = p.GetValue<string>("display_name") ?? p.Key,
				Enabled = p.GetValue<bool>("enabled"),
				Issuer = p.GetValue<string>("issuer") ?? String.Empty,
				ClientID = p.GetValue<string>("client_id") ?? String.Empty,
				ClientSecret = p.GetValue<string>("client_secret") ?? String.Empty,
				AllowSignUp = p.GetValue<bool?>("allow_sign_up") ?? true,
				OnlyProvider = p.GetValue<bool>("only_provider"),
				AutoLinkVerifiedEmail = p.GetValue<bool>("auto_link_verified_email"),
				Trusted = p.GetValue<bool>("trusted"),
				PlayerIDClaim = p.GetValue<string>("player_id_claim") ?? "player_id",
				PlayerIDs = p.GetValue<bool>("player_ids"),
				GroupsClaim = p.GetValue<string>("groups_claim") ?? "groups",
				RoleGroups = p.GetSection("role_groups").GetChildren().Where(g => !String.IsNullOrEmpty(g.Value)).ToDictionary(g => g.Key, g => g.Value!, StringComparer.Ordinal),
				BanGroup = p.GetValue<string>("ban_group"),
				GroupSync = new GroupSyncSettings
				{
					Enabled = p.GetValue<bool>("group_sync:enabled"),
					IntervalSeconds = Math.Max(5, p.GetValue<int?>("group_sync:interval_seconds") ?? 15),
					Realm = p.GetValue<string>("group_sync:realm") ?? String.Empty,
					AdminBaseUrl = p.GetValue<string>("group_sync:admin_base_url") ?? String.Empty,
					ClientID = p.GetValue<string>("group_sync:client_id") ?? String.Empty,
					ClientSecret = p.GetValue<string>("group_sync:client_secret") ?? String.Empty
				}
			}).ToList();

			// player ids come from one upstream counter, so no other sign-in method may let the database pick a user id
			foreach (UpstreamProviderSettings provider in providers.Where(p => p.Enabled && p.PlayerIDs))
			{
				if (!provider.Trusted || !provider.OnlyProvider || providers.Count(p => p.Enabled) > 1)
				{
					throw new InvalidOperationException($"Accounts:providers:{provider.Name}: player_ids needs trusted and only_provider, and no other enabled provider.");
				}
			}

			return new AccountsSettings
			{
				Enabled = section.GetValue<bool?>("enabled") ?? true,
				Issuer = section.GetValue<string>("issuer"),
				Audience = section.GetValue<string>("audience") ?? DefaultAudience,
				AccessTokenMinutes = section.GetValue<int?>("access_token_minutes") ?? 15,
				RefreshTokenDays = section.GetValue<int?>("refresh_token_days") ?? 30,
				RefreshReuseLeewaySeconds = section.GetValue<int?>("refresh_reuse_leeway_seconds") ?? 30,
				SigningCertificatePaths = section.GetSection("signing_certificate_paths").Get<List<string>>() ?? new(),
				EncryptionCertificatePaths = section.GetSection("encryption_certificate_paths").Get<List<string>>() ?? new(),
				CertificatePassword = section.GetValue<string>("certificate_password"),
				EphemeralKeys = section.GetValue<bool>("ephemeral_keys"),
				RequireHttps = config.GetSection("Core").GetValue<bool?>("enforce_https") ?? true,
				Clients = clients,
				Providers = providers
			};
		}
	}
}
