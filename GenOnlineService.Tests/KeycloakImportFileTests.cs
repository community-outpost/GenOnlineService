using Database;
using System.Text.Json.Nodes;
using Xunit;

namespace GenOnlineService.Tests
{
	// The Keycloak user import file the import writes for the official deployment (identity section 10)
	public class KeycloakImportFileTests
	{
		private static readonly KeycloakImportUser[] s_users =
		{
			new(42, new[] { new KeycloakImportIdentity("steam", "7656", "Steamer"), new KeycloakImportIdentity("gamereplays", "9", "gr") }),
			new(7, new[] { new KeycloakImportIdentity("discord", "1234", null), new KeycloakImportIdentity("google", "g-1", "g@example.org") })
		};

		[Fact]
		public void EveryUserCarriesItsOldIdAsPlayerId()
		{
			JsonObject file = KeycloakImportFile.BuildUsers(s_users);
			var users = file["users"]!.AsArray();

			Assert.Equal(2, users.Count);
			Assert.Equal("42", users[0]!["attributes"]!["player_id"]![0]!.GetValue<string>());
			Assert.Equal("7", users[1]!["attributes"]!["player_id"]![0]!.GetValue<string>());
			Assert.Equal("player_42", users[0]!["username"]!.GetValue<string>());
			Assert.True(users[0]!["enabled"]!.GetValue<bool>());
		}

		[Fact]
		public void OnlyBrokeredProvidersBecomeFederatedIdentities()
		{
			JsonObject file = KeycloakImportFile.BuildUsers(s_users);
			var first = file["users"]![0]!["federatedIdentities"]!.AsArray();
			var second = file["users"]![1]!["federatedIdentities"]!.AsArray();

			Assert.Single(first);
			Assert.Equal("steam", first[0]!["identityProvider"]!.GetValue<string>());
			Assert.Equal("7656", first[0]!["userId"]!.GetValue<string>());
			Assert.Equal("Steamer", first[0]!["userName"]!.GetValue<string>());
			Assert.Equal(new[] { "discord", "google" }, second.Select(i => i!["identityProvider"]!.GetValue<string>()));

			// Keycloak requires a user name on a link; the provider's own id stands in when none was stored
			Assert.Equal("1234", second[0]!["userName"]!.GetValue<string>());
		}

		[Fact]
		public void NoUserCarriesAnEmail()
		{
			JsonObject file = KeycloakImportFile.BuildUsers(s_users);

			Assert.All(file["users"]!.AsArray(), u => Assert.False(u!.AsObject().ContainsKey("email")));
		}

		[Fact]
		public void ExistingKeycloakUsersAreSkippedNotOverwritten()
		{
			Assert.Equal("SKIP", KeycloakImportFile.BuildUsers(s_users)["ifResourceExists"]!.GetValue<string>());
		}

		[Fact]
		public void CounterStartsAboveTheHighestImportedId()
		{
			JsonObject counter = KeycloakImportFile.BuildCounter(s_users);

			Assert.Equal(43, counter["player_id_next"]!.GetValue<long>());
			Assert.Equal(42, counter["highest_imported_id"]!.GetValue<long>());
			Assert.Equal(1, KeycloakImportFile.BuildCounter(Array.Empty<KeycloakImportUser>())["player_id_next"]!.GetValue<long>());
		}

		[Fact]
		public void WriteCreatesTheUserFileAndTheCounterFile()
		{
			string dir = Path.Combine(Path.GetTempPath(), "go-kc-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			try
			{
				string path = Path.Combine(dir, "keycloak-users.json");
				(string usersPath, string counterPath) = KeycloakImportFile.Write(path, s_users);

				Assert.Equal(path, usersPath);
				Assert.Equal(path + ".counter.json", counterPath);
				Assert.Equal(2, JsonNode.Parse(File.ReadAllText(usersPath))!["users"]!.AsArray().Count);
				Assert.Equal(43, JsonNode.Parse(File.ReadAllText(counterPath))!["player_id_next"]!.GetValue<long>());
			}
			finally
			{
				Directory.Delete(dir, true);
			}
		}
	}
}
