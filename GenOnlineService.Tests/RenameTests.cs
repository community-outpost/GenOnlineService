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

using GenOnlineService.Api;
using GenOnlineService.Identity;
using GenOnlineService.Names;
using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests
{
	// Managed renames: who renames (NameOwner), the plugin client and its error mapping, the staff checks, ForceRename gone
	public class RenameTests
	{
		internal static AccountsSettings Accounts(bool playerIDs, bool rename, int timeoutSeconds = 5)
		{
			Dictionary<string, string?> values = new()
			{
				["Accounts:providers:keycloak:enabled"] = "true",
				["Accounts:providers:keycloak:trusted"] = "true",
				["Accounts:providers:keycloak:only_provider"] = "true",
				["Accounts:providers:keycloak:player_ids"] = playerIDs ? "true" : "false"
			};
			if (rename)
			{
				values["Accounts:providers:keycloak:rename:base_url"] = "http://kc.test/";
				values["Accounts:providers:keycloak:rename:realm"] = "genonline";
				values["Accounts:providers:keycloak:rename:client_id"] = "genonline-rename";
				values["Accounts:providers:keycloak:rename:client_secret"] = "secret";
				values["Accounts:providers:keycloak:rename:timeout_seconds"] = timeoutSeconds.ToString();
			}
			return AccountsSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
		}

		[Fact]
		public void NameOwnerIsDecidedByThePlayerIDProviderAndItsRenameClient()
		{
			Assert.Equal(NameOwner.Local, AccountsSettings.Read(new ConfigurationBuilder().Build()).NameOwner);
			Assert.Equal(NameOwner.Local, Accounts(playerIDs: false, rename: true).NameOwner);
			Assert.Equal(NameOwner.Managed, Accounts(playerIDs: true, rename: false).NameOwner);
			Assert.Equal(NameOwner.Plugin, Accounts(playerIDs: true, rename: true).NameOwner);
			Assert.Equal("http://kc.test", Accounts(playerIDs: true, rename: true).NameProvider!.Rename.BaseUrl);
		}

		// The plugin's key: Bob, BOB and B0b are one name, so are look-alike letters from other scripts
		[Fact]
		public void NameKeyMatchesThePluginKey()
		{
			Assert.True(NameKey.TableSize > 9000);
			Assert.Equal(NameKey.Of("Bob"), NameKey.Of("BOB"));
			Assert.Equal(NameKey.Of("Bob"), NameKey.Of("B0b"));
			Assert.Equal(NameKey.Of("Bob"), NameKey.Of("B\u043Eb"));
			Assert.Equal(NameKey.Of("Ann"), NameKey.Of("\uFF21nn"));
			Assert.Equal("bob", NameKey.Of("B0b"));
			Assert.NotEqual(NameKey.Of("Bob"), NameKey.Of("Rob"));
		}

		// REST staff actions are audited as api, the realtime hub keeps realtime
		[Fact]
		public void RestStaffActionsUseTheApiSource()
		{
			Assert.Equal(("api", "user:5", 5L), (SanctionActions.ApiActorOf(5).Source, SanctionActions.ApiActorOf(5).Name, SanctionActions.ApiActorOf(5).UserID!.Value));
			Assert.Equal("realtime", SanctionActions.ActorOf(5).Source);
		}

		// The plugin's rule (NameRules.check): its default list, a case-insensitive substring match, the word as listed
		[Fact]
		public void ReservedWordsFollowThePluginRule()
		{
			// genonline-keycloak NameRules.DEFAULT_RESERVED_WORDS
			Assert.Equal(new[]
			{
				"admin", "staff", "mass^", "mas^", "m4ss^", "m4s^", "moderator",
				"hitler", "h1tler", "h1tl3r", "hittler", "h1ttler", "h1ttl3r",
				"olda", "oldanalytics", "ibra", "x64", "ronin"
			}, ProfileActions.ReservedNameWords);

			Assert.Null(ProfileActions.ReservedWordIn("AliceByMod"));
			Assert.Null(ProfileActions.ReservedWordIn("Modern"));
			Assert.Null(ProfileActions.ReservedWordIn("Model"));
			Assert.Equal("staff", ProfileActions.ReservedWordIn("AliceStaffSet"));
			Assert.Equal("admin", ProfileActions.ReservedWordIn("Admin"));
			Assert.Equal("admin", ProfileActions.ReservedWordIn("SUPERADMIN"));
			Assert.Equal("moderator", ProfileActions.ReservedWordIn("TheModerator"));
			Assert.Equal("staff", ProfileActions.ReservedWordIn("\u0130STAFF"));
		}

		private static object? Param(Failure failure, string name)
		{
			JsonElement element = JsonSerializer.SerializeToElement(failure.Params, ApiResults.Json);
			if (!element.TryGetProperty(name, out JsonElement value))
			{
				return null;
			}
			return value.ValueKind == JsonValueKind.Number ? value.GetInt32() : value.GetString();
		}

		[Theory]
		[InlineData("length", ErrorCodes.NameLength)]
		[InlineData("characters", ErrorCodes.NameInvalidCharacters)]
		[InlineData("whitespace", ErrorCodes.NameWhitespace)]
		[InlineData("reserved", ErrorCodes.NameReserved)]
		public void PluginRulesMapToNameCodes(string rule, string code)
		{
			string body = $"{{\"error\":\"name.invalid\",\"params\":{{\"rule\":\"{rule}\",\"min\":3,\"max\":16,\"word\":\"admin\"}}}}";
			(Failure failure, LogLevel level) = KeycloakRenameClient.MapRefusal(400, body);
			Assert.Equal(code, failure.Code);
			Assert.Equal(LogLevel.None, level);
			if (rule == "length")
			{
				Assert.Equal(3, Param(failure, "min"));
				Assert.Equal(16, Param(failure, "max"));
			}
			if (rule == "reserved")
			{
				Assert.Equal("admin", Param(failure, "word"));
			}
		}

		[Fact]
		public void PluginNameTakenMapsToNameTaken()
		{
			(Failure failure, LogLevel level) = KeycloakRenameClient.MapRefusal(409, "{\"error\":\"name.taken\",\"params\":{}}");
			Assert.Equal(ErrorCodes.NameTaken, failure.Code);
			Assert.Equal(LogLevel.None, level);
		}

		// request.invalid is a bug here, 401, 403 and 404 a wrong client, role or player: all logged as errors
		[Theory]
		[InlineData(400, "{\"error\":\"request.invalid\",\"params\":{\"field\":\"name\"}}", LogLevel.Error)]
		[InlineData(400, "{\"error\":\"name.invalid\",\"params\":{\"rule\":\"unknown\"}}", LogLevel.Error)]
		[InlineData(401, "{\"error\":\"unauthorized\",\"params\":{}}", LogLevel.Error)]
		[InlineData(403, "{\"error\":\"forbidden\",\"params\":{}}", LogLevel.Error)]
		[InlineData(404, "{\"error\":\"player.not_found\"}", LogLevel.Error)]
		[InlineData(500, "<html>oops</html>", LogLevel.Warning)]
		[InlineData(503, "", LogLevel.Warning)]
		public void OtherPluginAnswersMapToNameUnavailable(int status, string body, LogLevel expected)
		{
			(Failure failure, LogLevel level) = KeycloakRenameClient.MapRefusal(status, body);
			Assert.Equal(ErrorCodes.NameUnavailable, failure.Code);
			Assert.Equal(expected, level);
			Assert.Equal(503, ApiResults.StatusOf(failure.Code));
		}

		[Fact]
		public async Task PluginClientReturnsTheNewNameAndTagAndCachesItsToken()
		{
			FakeRenamePlugin plugin = new() { Rename = (_, _) => Task.FromResult(FakeRenamePlugin.Json(200, "{\"player_id\":42,\"name\":\"Bob\",\"tag\":\"0042\",\"seq\":1234}")) };
			KeycloakRenameClient client = new(plugin, Accounts(playerIDs: true, rename: true));

			PluginRenameResult first = await client.RenameAsync(42, "Bob", "player:7", "offensive name");
			PluginRenameResult second = await client.RenameAsync(42, "Bob", "discord:99", null);

			Assert.True(first.Ok);
			Assert.Equal(("Bob", "0042", (long?)1234), (first.Name, first.Tag, first.Seq));
			Assert.True(second.Ok);
			Assert.Equal(1, plugin.TokenRequests);

			(string url, string? auth, string body) = plugin.RenameRequests[0];
			Assert.Equal("http://kc.test/realms/genonline/genonline/players/42/name", url);
			Assert.Equal("Bearer token-1", auth);
			using JsonDocument sent = JsonDocument.Parse(body);
			Assert.Equal("Bob", sent.RootElement.GetProperty("name").GetString());
			Assert.Equal("player:7", sent.RootElement.GetProperty("actor").GetString());
			Assert.Equal("offensive name", sent.RootElement.GetProperty("reason").GetString());

			using JsonDocument bot = JsonDocument.Parse(plugin.RenameRequests[1].Body);
			Assert.Equal("discord:99", bot.RootElement.GetProperty("actor").GetString());
			Assert.False(bot.RootElement.TryGetProperty("reason", out _));
		}

		[Fact]
		public async Task PluginClientSendsTheFullActorAndReason()
		{
			FakeRenamePlugin plugin = new() { Rename = (_, _) => Task.FromResult(FakeRenamePlugin.Json(200, "{\"player_id\":42,\"name\":\"Bob\",\"tag\":\"0042\",\"seq\":null}")) };
			KeycloakRenameClient client = new(plugin, Accounts(playerIDs: true, rename: true));
			string reason = new string('r', 600);

			PluginRenameResult result = await client.RenameAsync(42, "Bob", "player:7", reason);

			Assert.True(result.Ok);
			Assert.Null(result.Seq);
			using JsonDocument sent = JsonDocument.Parse(plugin.RenameRequests[0].Body);
			Assert.Equal(reason, sent.RootElement.GetProperty("reason").GetString());
		}

		[Theory]
		[InlineData(409, "{\"error\":\"name.taken\",\"params\":{}}", ErrorCodes.NameTaken)]
		[InlineData(400, "{\"error\":\"name.invalid\",\"params\":{\"rule\":\"length\",\"min\":3,\"max\":16}}", ErrorCodes.NameLength)]
		[InlineData(400, "{\"error\":\"name.invalid\",\"params\":{\"rule\":\"characters\"}}", ErrorCodes.NameInvalidCharacters)]
		[InlineData(400, "{\"error\":\"name.invalid\",\"params\":{\"rule\":\"whitespace\"}}", ErrorCodes.NameWhitespace)]
		[InlineData(400, "{\"error\":\"name.invalid\",\"params\":{\"rule\":\"reserved\",\"word\":\"admin\"}}", ErrorCodes.NameReserved)]
		[InlineData(400, "{\"error\":\"request.invalid\",\"params\":{\"field\":\"name\"}}", ErrorCodes.NameUnavailable)]
		[InlineData(500, "{}", ErrorCodes.NameUnavailable)]
		[InlineData(404, "{\"error\":\"player.not_found\",\"params\":{}}", ErrorCodes.NameUnavailable)]
		public async Task PluginClientMapsEachRefusal(int status, string body, string code)
		{
			FakeRenamePlugin plugin = new() { Rename = (_, _) => Task.FromResult(FakeRenamePlugin.Json(status, body)) };
			KeycloakRenameClient client = new(plugin, Accounts(playerIDs: true, rename: true));

			PluginRenameResult result = await client.RenameAsync(42, "Bob", "player:7", null);

			Assert.False(result.Ok);
			Assert.Equal(code, result.Failure!.Value.Code);
			Assert.Null(result.Name);
		}

		// A 401 answers name.unavailable and drops the cached token, so the next call fetches a new one
		[Fact]
		public async Task PluginClientUnauthorizedIsUnavailableAndRenewsTheToken()
		{
			FakeRenamePlugin plugin = new() { Rename = (_, _) => Task.FromResult(FakeRenamePlugin.Json(401, "{\"error\":\"unauthorized\",\"params\":{}}")) };
			KeycloakRenameClient client = new(plugin, Accounts(playerIDs: true, rename: true));

			PluginRenameResult first = await client.RenameAsync(42, "Bob", "player:7", null);
			await client.RenameAsync(42, "Bob", "player:7", null);

			Assert.Equal(ErrorCodes.NameUnavailable, first.Failure!.Value.Code);
			Assert.Equal(2, plugin.TokenRequests);
		}

		[Fact]
		public async Task PluginClientTimeoutIsUnavailable()
		{
			FakeRenamePlugin plugin = new()
			{
				Rename = async (_, token) =>
				{
					await Task.Delay(TimeSpan.FromSeconds(30), token);
					return FakeRenamePlugin.Json(200, "{}");
				}
			};
			KeycloakRenameClient client = new(plugin, Accounts(playerIDs: true, rename: true, timeoutSeconds: 1));

			PluginRenameResult result = await client.RenameAsync(42, "Bob", "player:7", null);

			Assert.Equal(ErrorCodes.NameUnavailable, result.Failure!.Value.Code);
		}

		[Fact]
		public async Task PluginClientRefusedConnectionIsUnavailable()
		{
			FakeRenamePlugin plugin = new() { Rename = (_, _) => throw new HttpRequestException("connection refused") };
			KeycloakRenameClient client = new(plugin, Accounts(playerIDs: true, rename: true));

			PluginRenameResult result = await client.RenameAsync(42, "Bob", "player:7", null);

			Assert.Equal(ErrorCodes.NameUnavailable, result.Failure!.Value.Code);
		}

		// A 200 for another player than the one in the URL is a plugin fault, nothing is applied
		[Fact]
		public async Task PluginAnswerForAnotherPlayerIsUnavailable()
		{
			FakeRenamePlugin plugin = new() { Rename = (_, _) => Task.FromResult(FakeRenamePlugin.Json(200, "{\"player_id\":43,\"name\":\"Bob\",\"tag\":\"0042\",\"seq\":1}")) };
			KeycloakRenameClient client = new(plugin, Accounts(playerIDs: true, rename: true));

			PluginRenameResult result = await client.RenameAsync(42, "Bob", "player:7", null);

			Assert.Equal(ErrorCodes.NameUnavailable, result.Failure!.Value.Code);
			Assert.Null(result.Name);
		}

		[Fact]
		public void MalformedRenameBaseUrlStopsTheStart()
		{
			Dictionary<string, string?> values = new()
			{
				["Accounts:providers:keycloak:enabled"] = "true",
				["Accounts:providers:keycloak:rename:base_url"] = "not a url"
			};
			Assert.Throws<InvalidOperationException>(() => AccountsSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
		}

		// Settings that bypass Read still never throw out of the client
		[Fact]
		public async Task MalformedRenameBaseUrlIsUnavailable()
		{
			AccountsSettings accounts = new()
			{
				Providers = new()
				{
					new UpstreamProviderSettings
					{
						Name = "keycloak",
						Enabled = true,
						Trusted = true,
						PlayerIDs = true,
						Rename = new RenameSettings { BaseUrl = "not a url", Realm = "genonline", ClientID = "genonline-rename", ClientSecret = "secret" }
					}
				}
			};
			FakeRenamePlugin plugin = new();
			KeycloakRenameClient client = new(plugin, accounts);

			PluginRenameResult result = await client.RenameAsync(42, "Bob", "player:7", null);

			Assert.Equal(ErrorCodes.NameUnavailable, result.Failure!.Value.Code);
			Assert.Empty(plugin.RenameRequests);
		}

		// A name whose normalized form outgrows the 32-character column answers name.length before any write or plugin call
		[Theory]
		[InlineData("\uFB03\uFB03\uFB03\uFB03\uFB03\uFB03\uFB03\uFB03\uFB03\uFB03\uFB03")]
		[InlineData("\uFDFA\uFDFA\uFDFA")]
		public void UnstorableNamesAnswerNameLength(string name)
		{
			Failure? failure = ProfileActions.CheckNameRules(name);
			Assert.NotNull(failure);
			Assert.Equal(ErrorCodes.NameLength, failure!.Value.Code);
			Assert.Equal(3, Param(failure.Value, "min"));
			Assert.Equal(16, Param(failure.Value, "max"));
			Assert.Null(ProfileActions.CheckNameRules("Bobby"));
		}

		// Staff never rename themselves, need a higher rank than the target, and keep reasons to 512 characters
		[Fact]
		public void StaffRenameFollowsTheRankRule()
		{
			Assert.Null(PlayerNames.CheckStaff(1, 1, 2, 0, null));
			Assert.Null(PlayerNames.CheckStaff(1, 2, 2, 1, "offensive name"));
			Assert.Null(PlayerNames.CheckStaff(null, StaffRoles.AdminRank, 2, 1, null));
			Assert.Equal(ErrorCodes.ModerationTargetOutranks, PlayerNames.CheckStaff(1, 1, 2, 1, null)!.Value.Code);
			Assert.Equal(ErrorCodes.ModerationTargetOutranks, PlayerNames.CheckStaff(1, 1, 2, 2, null)!.Value.Code);
			Assert.Equal(ErrorCodes.ModerationTargetOutranks, PlayerNames.CheckStaff(null, StaffRoles.AdminRank, 2, StaffRoles.AdminRank, null)!.Value.Code);
			Assert.Equal(ErrorCodes.InvalidArgument, PlayerNames.CheckStaff(1, 2, 1, 0, null)!.Value.Code);
			Assert.Equal(ErrorCodes.InvalidArgument, PlayerNames.CheckStaff(1, 2, 0, 0, null)!.Value.Code);
			Assert.Equal(ErrorCodes.ModerationReasonTooLong, PlayerNames.CheckStaff(1, 2, 2, 0, new string('r', ModerationManager.MaximumReasonLength + 1))!.Value.Code);
		}

		private sealed class DenyAll : IAuthorizationService
		{
			public List<string> Policies { get; } = new();

			public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) => Task.FromResult(AuthorizationResult.Failed());

			public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
			{
				Policies.Add(policyName);
				return Task.FromResult(AuthorizationResult.Failed());
			}
		}

		[Fact]
		public async Task StaffRenameNeedsModerationRename()
		{
			DenyAll authorization = new();
			ModerationController controller = new(null!, authorization, null!)
			{
				ControllerContext = new ControllerContext
				{
					HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "7") }, "test")) }
				}
			};

			IActionResult answer = await controller.RenamePlayer(8);

			JsonResult json = Assert.IsType<JsonResult>(answer);
			Assert.Equal(403, json.StatusCode);
			ApiError error = Assert.IsType<ApiError>(json.Value);
			Assert.Equal(ErrorCodes.Forbidden, error.Error);
			Assert.Equal(new[] { PermissionPolicies.For(Permissions.ModerationRename) }, authorization.Policies);
		}

		[Fact]
		public void ForceRenameIsGone()
		{
			Assert.Null(typeof(RealtimeHub).GetMethod("ForceRename"));
			Assert.DoesNotContain(typeof(RealtimeHub).Assembly.GetTypes(), t => t.Name == "ForceRenameRequest");
			Assert.DoesNotContain(typeof(IRealtimeClient).GetMethods(), m => m.Name.Contains("Rename", StringComparison.Ordinal));

			// the docs in the repository, a stale copy in the build output does not count
			DirectoryInfo? dir = new(AppContext.BaseDirectory);
			while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "realtime-fixtures")))
			{
				dir = dir.Parent;
			}
			Assert.NotNull(dir);
			Assert.False(File.Exists(Path.Combine(dir!.FullName, "docs", "realtime-fixtures", "ForceRename.request.json")));
		}
	}

	// A fake Keycloak: the token endpoint answers a token, the rename endpoint answers what the test sets
	internal sealed class FakeRenamePlugin : HttpMessageHandler, IHttpClientFactory
	{
		public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Rename { get; set; } = (_, _) => Task.FromResult(Json(200, "{}"));
		public int TokenRequests;
		public List<(string Url, string? Auth, string Body)> RenameRequests { get; } = new();

		public HttpClient CreateClient(string name) => new HttpClient(this, disposeHandler: false);

		public static HttpResponseMessage Json(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			string url = request.RequestUri!.ToString();
			if (url.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal))
			{
				Interlocked.Increment(ref TokenRequests);
				string form = await request.Content!.ReadAsStringAsync(cancellationToken);
				Assert.Contains("grant_type=client_credentials", form);
				Assert.Contains("client_id=genonline-rename", form);
				return Json(200, "{\"access_token\":\"token-1\",\"expires_in\":300}");
			}

			RenameRequests.Add((url, request.Headers.Authorization?.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken)));
			return await Rename(request, cancellationToken);
		}
	}
}
