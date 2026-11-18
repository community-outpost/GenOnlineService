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

using Database;
using GenOnlineService.Identity;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GenOnlineService.Tests
{
	// Keycloak is gone (identity hub, S1): its settings stop the start instead of being ignored, plain providers still work
	public class KeycloakRemovalTests
	{
		private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

		[Theory]
		[InlineData("trusted", "true")]
		[InlineData("player_ids", "true")]
		[InlineData("player_id_claim", "player_id")]
		[InlineData("groups_claim", "groups")]
		[InlineData("role_groups:genonline-admin", "admin")]
		[InlineData("ban_group", "genonline-banned")]
		[InlineData("group_sync:enabled", "true")]
		[InlineData("rename:base_url", "http://kc.test")]
		[InlineData("change_log:enabled", "true")]
		public void KeycloakProviderSettingsStopTheStart(string key, string value)
		{
			IConfiguration config = Config(new()
			{
				["Accounts:providers:upstream:enabled"] = "true",
				["Accounts:providers:upstream:issuer"] = "https://idp.test/",
				[$"Accounts:providers:upstream:{key}"] = value
			});

			InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => AccountsSettings.Read(config));
			string setting = key.Split(':')[0];
			Assert.Contains($"Accounts:providers:upstream:{setting}", ex.Message);
			Assert.Contains("Keycloak", ex.Message);
		}

		// a disabled provider is still read, so a forgotten Keycloak block stops the start as well
		[Fact]
		public void KeycloakSettingsOnADisabledProviderStopTheStart()
		{
			IConfiguration config = Config(new()
			{
				["Accounts:providers:keycloak:enabled"] = "false",
				["Accounts:providers:keycloak:player_ids"] = "true"
			});

			Assert.Throws<InvalidOperationException>(() => AccountsSettings.Read(config));
		}

		// the settings a plain OpenID Connect provider keeps read as before
		[Fact]
		public void PlainProviderSettingsStillRead()
		{
			AccountsSettings settings = AccountsSettings.Read(Config(new()
			{
				["Accounts:providers:idp:enabled"] = "true",
				["Accounts:providers:idp:display_name"] = "Our IdP",
				["Accounts:providers:idp:issuer"] = "https://idp.test/",
				["Accounts:providers:idp:client_id"] = "genonline",
				["Accounts:providers:idp:client_secret"] = "secret",
				["Accounts:providers:idp:allow_sign_up"] = "false",
				["Accounts:providers:idp:only_provider"] = "true",
				["Accounts:providers:idp:auto_link_verified_email"] = "true"
			}));

			UpstreamProviderSettings provider = Assert.Single(settings.EnabledProviders);
			Assert.Equal(("idp", "Our IdP", "https://idp.test/", "genonline", "secret"), (provider.Name, provider.DisplayName, provider.Issuer, provider.ClientID, provider.ClientSecret));
			Assert.False(provider.AllowSignUp);
			Assert.True(provider.OnlyProvider);
			Assert.True(provider.AutoLinkVerifiedEmail);
			Assert.Equal("users/auth/idp/callback", provider.CallbackPath);
		}

		private static Dictionary<string, string?> ImportValues() => new()
		{
			["Import:db_provider"] = "mariadb",
			["Import:db_host"] = "old.test",
			["Import:db_name"] = "genonline",
			["Import:db_username"] = "reader",
			["Import:db_password"] = "secret"
		};

		[Fact]
		public void ImportKeycloakFileStopsTheImport()
		{
			Dictionary<string, string?> values = ImportValues();
			values["Import:keycloak_file"] = "keycloak-users.json";

			ImportException ex = Assert.Throws<ImportException>(() => Import.ReadSettings(Config(values)));
			Assert.Contains("Import:keycloak_file", ex.Message);
		}

		// an empty keycloak_file is what the shipped appsettings had: nothing to refuse
		[Fact]
		public void ImportSettingsWithoutKeycloakFileStillRead()
		{
			Dictionary<string, string?> values = ImportValues();
			values["Import:keycloak_file"] = "";

			ImportSettings settings = Import.ReadSettings(Config(values));
			Assert.Equal(("old.test", (ushort)3306, "genonline"), (settings.Host, settings.Port, settings.Name));
		}
	}
}
