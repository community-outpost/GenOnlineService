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

	// How a provider signs people in: OpenID Connect, OpenIddict's Discord or Google provider, a plain OAuth 2.0 server
	// (GameReplays) or OpenID 2.0 (Steam)
	public enum ProviderKind
	{
		Oidc,
		Discord,
		Google,
		Steam,
		GameReplays
	}

	// One upstream sign-in provider: it signs people in, the environment owns their names, roles and bans
	public sealed class UpstreamProviderSettings
	{
		// The name a provider's button shows when its configuration sets no display_name
		public static string DefaultDisplayName(string provider) => provider switch
		{
			"discord" => "Discord",
			"steam" => "Steam",
			"google" => "Google",
			"gamereplays" => "GameReplays",
			_ => provider,
		};

		public string Name { get; init; } = String.Empty;
		public ProviderKind Kind { get; init; } = ProviderKind.Oidc;
		// GameReplays: the folder of authorize.php, token.php and resource.php
		public string BaseUrl { get; init; } = String.Empty;
		// Steam: an optional Steam Web API key, only for the persona name
		public string? ApplicationKey { get; init; }
		public string DisplayName { get; init; } = String.Empty;
		public bool Enabled { get; init; }
		public string Issuer { get; init; } = String.Empty;
		public string ClientID { get; init; } = String.Empty;
		public string ClientSecret { get; init; } = String.Empty;
		public bool AllowSignUp { get; init; } = true;
		public bool OnlyProvider { get; init; }
		public bool AutoLinkVerifiedEmail { get; init; }
		// The return URL registered at the provider when it is not this service's callback: sent as is in the
		// authorization and token requests; that URL must redirect the browser, query intact, to CallbackPath here
		public string? RedirectUri { get; init; }

		public string Source => AuthoritySources.UpstreamPrefix + Name;
		public string CallbackPath => $"users/auth/{Name}/callback";
		// What the provider is told to return to: the relay URL when one is set, else the callback here
		public string ReturnUri => RedirectUri ?? CallbackPath;
		// Steam: the path its OpenID 2.0 handler answers itself before handing over to CallbackPath
		public string SteamHandlerPath => $"/users/auth/{Name}/openid";

		private static readonly Dictionary<string, ProviderKind> s_kinds = new(StringComparer.Ordinal)
		{
			["oidc"] = ProviderKind.Oidc,
			["discord"] = ProviderKind.Discord,
			["google"] = ProviderKind.Google,
			["steam"] = ProviderKind.Steam,
			["gamereplays"] = ProviderKind.GameReplays
		};

		// The kind setting, else the provider's key when it names a kind, else OpenID Connect
		internal static ProviderKind KindOf(string name, string? kind)
		{
			if (!String.IsNullOrEmpty(kind))
			{
				return s_kinds.TryGetValue(kind, out ProviderKind configured) ? configured
					: throw new InvalidOperationException($"Accounts:providers:{name}:kind \"{kind}\" is unknown. Use one of {String.Join(", ", s_kinds.Keys)}.");
			}
			return s_kinds.TryGetValue(name, out ProviderKind byName) ? byName : ProviderKind.Oidc;
		}

		// What each kind needs before the start goes on
		internal void Validate()
		{
			string prefix = $"Accounts:providers:{Name}";
			switch (Kind)
			{
				case ProviderKind.Oidc:
					if (!Uri.TryCreate(Issuer, UriKind.Absolute, out _))
					{
						throw new InvalidOperationException($"{prefix}:issuer must be the provider's absolute issuer URL.");
					}
					break;
				case ProviderKind.Discord:
				case ProviderKind.Google:
				case ProviderKind.GameReplays:
					if (String.IsNullOrEmpty(ClientID) || String.IsNullOrEmpty(ClientSecret))
					{
						throw new InvalidOperationException($"{prefix} needs client_id and client_secret of the application registered at {Kind}.");
					}
					if (Kind == ProviderKind.GameReplays
						&& (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)))
					{
						throw new InvalidOperationException($"{prefix}:base_url must be the absolute http or https URL of the folder holding authorize.php, token.php and resource.php.");
					}
					break;
			}

			if (RedirectUri != null)
			{
				if (Kind == ProviderKind.Steam)
				{
					throw new InvalidOperationException($"{prefix}:redirect_uri is not supported for Steam, whose return URL is always this service's.");
				}
				if (!Uri.TryCreate(RedirectUri, UriKind.Absolute, out Uri? relay) || (relay.Scheme != Uri.UriSchemeHttps && relay.Scheme != Uri.UriSchemeHttp))
				{
					throw new InvalidOperationException($"{prefix}:redirect_uri must be the absolute http or https return URL registered at the provider.");
				}
			}
		}
	}

	// Accounts:password: email + password accounts, off unless the operator turns them on
	public sealed class PasswordSignInSettings
	{
		public const int MinimumLength = 8;
		public const int MaximumLength = 128;

		public bool Enabled { get; init; }
		public bool AllowSignUp { get; init; } = true;
		// Length is the only rule (NIST SP 800-63B): at least MinLength, at most MaximumLength characters
		public int MinLength { get; init; } = MinimumLength;
		// An invisible sign-up field that people leave empty and form-filling bots do not
		public bool Honeypot { get; init; } = true;
		// Sign-up refuses an email address at one of these domains (lower case, compared without case)
		public List<string> EmailDomainDenyList { get; init; } = new();

		internal static PasswordSignInSettings Read(IConfigurationSection section)
		{
			int minLength = section.GetValue<int?>("min_length") ?? MinimumLength;
			if (minLength < MinimumLength || minLength > MaximumLength)
			{
				throw new InvalidOperationException($"Accounts:password:min_length must be between {MinimumLength} and {MaximumLength}.");
			}

			return new PasswordSignInSettings
			{
				Enabled = section.GetValue<bool>("enabled"),
				AllowSignUp = section.GetValue<bool?>("allow_sign_up") ?? true,
				MinLength = minLength,
				Honeypot = section.GetValue<bool?>("honeypot") ?? true,
				EmailDomainDenyList = (section.GetSection("email_domain_deny_list").Get<List<string>>() ?? new())
					.Select(d => d.Trim().ToLowerInvariant()).Where(d => d.Length > 0).Distinct().ToList()
			};
		}
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
		// Each entry is a .pfx/.p12 path, a PEM file with the private key, or PEM text; the first signs or encrypts, every entry is accepted
		public List<string> SigningCertificates { get; init; } = new();
		public List<string> EncryptionCertificates { get; init; } = new();
		public string? CertificatePassword { get; init; }

		// Development only: keys live in memory and every restart invalidates every token
		public bool EphemeralKeys { get; init; }
		public bool RequireHttps { get; init; } = true;
		public List<OAuthClientSettings> Clients { get; init; } = new();
		public List<UpstreamProviderSettings> Providers { get; init; } = new();
		public PasswordSignInSettings Password { get; init; } = new();

		public IEnumerable<UpstreamProviderSettings> EnabledProviders => Providers.Where(p => p.Enabled);

		// Core:enforce_https, true when missing: https-only cookies and OpenIddict transport security here, HSTS and the
		// http to https redirect in Program.UseHttpsEnforcement. A local plain http run sets it to false.
		public static bool EnforceHttps(IConfiguration config) => config.GetSection("Core").GetValue<bool?>("enforce_https") ?? true;

		// Provider settings of the retired Keycloak setup (player ids, trusted claims, group sync, the plugin's rename and
		// change log). Each one used to change who owns names, roles and bans, so a leftover one stops the start.
		private static readonly string[] s_keycloakProviderSettings =
		{
			"trusted", "player_ids", "player_id_claim", "groups_claim", "role_groups", "ban_group", "group_sync", "rename", "change_log"
		};

		public static AccountsSettings Read(IConfiguration config)
		{
			IConfigurationSection section = config.GetSection("Accounts");

			foreach (IConfigurationSection provider in section.GetSection("providers").GetChildren())
			{
				foreach (string setting in s_keycloakProviderSettings)
				{
					IConfigurationSection value = provider.GetSection(setting);
					if (value.Value != null || value.GetChildren().Any())
					{
						throw new InvalidOperationException($"Accounts:providers:{provider.Key}:{setting} belonged to the Keycloak setup, which is gone: names, roles and bans are owned by this environment now. Remove the setting.");
					}
				}
			}

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
				Kind = UpstreamProviderSettings.KindOf(p.Key, p.GetValue<string>("kind")),
				BaseUrl = (p.GetValue<string>("base_url") ?? String.Empty).TrimEnd('/'),
				ApplicationKey = String.IsNullOrEmpty(p.GetValue<string>("application_key")) ? null : p.GetValue<string>("application_key"),
				DisplayName = p.GetValue<string>("display_name") ?? UpstreamProviderSettings.DefaultDisplayName(p.Key),
				Enabled = p.GetValue<bool>("enabled"),
				Issuer = p.GetValue<string>("issuer") ?? String.Empty,
				ClientID = p.GetValue<string>("client_id") ?? String.Empty,
				ClientSecret = p.GetValue<string>("client_secret") ?? String.Empty,
				AllowSignUp = p.GetValue<bool?>("allow_sign_up") ?? true,
				OnlyProvider = p.GetValue<bool>("only_provider"),
				AutoLinkVerifiedEmail = p.GetValue<bool>("auto_link_verified_email"),
				RedirectUri = String.IsNullOrEmpty(p.GetValue<string>("redirect_uri")) ? null : p.GetValue<string>("redirect_uri")
			}).ToList();

			foreach (UpstreamProviderSettings provider in providers.Where(p => p.Enabled))
			{
				provider.Validate();
			}

			return new AccountsSettings
			{
				Enabled = section.GetValue<bool?>("enabled") ?? true,
				Issuer = section.GetValue<string>("issuer"),
				Audience = section.GetValue<string>("audience") ?? DefaultAudience,
				AccessTokenMinutes = section.GetValue<int?>("access_token_minutes") ?? 15,
				RefreshTokenDays = section.GetValue<int?>("refresh_token_days") ?? 30,
				RefreshReuseLeewaySeconds = section.GetValue<int?>("refresh_reuse_leeway_seconds") ?? 30,
				SigningCertificates = section.GetSection("signing_certificates").Get<List<string>>() ?? new(),
				EncryptionCertificates = section.GetSection("encryption_certificates").Get<List<string>>() ?? new(),
				CertificatePassword = section.GetValue<string>("certificate_password"),
				EphemeralKeys = section.GetValue<bool>("ephemeral_keys"),
				RequireHttps = EnforceHttps(config),
				Clients = clients,
				Providers = providers,
				Password = PasswordSignInSettings.Read(section.GetSection("password"))
			};
		}
	}
}
