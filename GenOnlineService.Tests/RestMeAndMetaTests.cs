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
using GenOnlineService.Api;
using GenOnlineService.Identity;
using GenOnlineService.Realtime;
using GenOnlineService.Rest;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests
{
	// Version checks, MOTD handling, the EOS link errors and the global stats of /api/v1/me, /compatibility, /config and /stats/global
	public class RestMeAndMetaTests
	{
		private static RegisteredClient Client(string clientID, string? minVersion) => new RegisteredClient(1, clientID, ClientKinds.Game, ClientGames.ZeroHour, true, true, minVersion, []);

		private static readonly CompatibilityUpdate s_update = new CompatibilityUpdate { PatcherName = "update.exe", PatcherPath = "https://cdn.example/update.exe", PatcherSize = null };

		private static Task<Result<CompatibilityResponse>> Evaluate(string? clientID, string? version, string? minVersion = "2026.11.2")
		{
			return Compatibility.EvaluateAsync(clientID, version, id => id == "zh_game" ? Client(id, minVersion) : null, () => Task.FromResult<CompatibilityUpdate?>(s_update));
		}

		private static string ErrorOf<T>(Result<T> result) where T : class => JsonSerializer.Serialize(ApiResults.BodyOf(new Failure(result.Code!, result.Params!)), ApiResults.Json);

		[Theory]
		[InlineData("2026.11.0", true)]
		[InlineData("2026.1.15", true)]
		[InlineData("2026.13.0", false)]
		[InlineData("2026.0.1", false)]
		[InlineData("26.11.0", false)]
		[InlineData("2026.11", false)]
		[InlineData("2026.11.0.1", false)]
		[InlineData("2026.11.x", false)]
		[InlineData("2026.11.0-rc1", false)]
		[InlineData(" 2026.11.0", false)]
		[InlineData("2026.11.0\n", false)]
		public void VersionIsCalVer(string version, bool valid)
		{
			Assert.Equal(valid, Compatibility.TryParseVersion(version, out _));
		}

		[Theory]
		[InlineData("2026.11.2", "2026.11.2", true)]
		[InlineData("2026.11.3", "2026.11.2", true)]
		[InlineData("2026.12.0", "2026.11.9", true)]
		[InlineData("2027.1.0", "2026.12.9", true)]
		[InlineData("2026.11.10", "2026.11.9", true)]
		[InlineData("2026.11.1", "2026.11.2", false)]
		[InlineData("2026.10.9", "2026.11.0", false)]
		[InlineData("2025.12.9", "2026.1.0", false)]
		public void VersionAtOrAboveTheMinimumIsCompatible(string version, string minVersion, bool compatible)
		{
			Assert.True(Compatibility.TryParseVersion(version, out var parsed));
			Assert.Equal(compatible, Compatibility.IsCompatible(parsed, minVersion));
		}

		[Theory]
		[InlineData(null)]
		[InlineData("not-a-version")]
		public void ClientWithoutAUsableMinimumAcceptsEveryVersion(string? minVersion)
		{
			Assert.True(Compatibility.TryParseVersion("2020.1.0", out var parsed));
			Assert.True(Compatibility.IsCompatible(parsed, minVersion));
		}

		[Fact]
		public async Task CompatibleVersionAnswersWithoutUpdate()
		{
			Result<CompatibilityResponse> result = await Evaluate("zh_game", "2026.11.2");
			Assert.True(result.Ok);
			Assert.True(result.Data!.Compatible);
			Assert.Equal("2026.11.2", result.Data.MinVersion);
			Assert.Equal(Compatibility.ServiceVersion, result.Data.ServiceVersion);
			Assert.Null(result.Data.Update);
		}

		[Fact]
		public async Task OutdatedVersionAnswersWithTheUpdate()
		{
			Result<CompatibilityResponse> result = await Evaluate("zh_game", "2026.11.1");
			Assert.True(result.Ok);
			Assert.False(result.Data!.Compatible);
			Assert.Same(s_update, result.Data.Update);
		}

		[Fact]
		public async Task ClientWithoutMinimumAnswersNullMinVersion()
		{
			Result<CompatibilityResponse> result = await Evaluate("zh_game", "2026.1.0", minVersion: null);
			Assert.True(result.Data!.Compatible);
			Assert.Null(result.Data.MinVersion);
		}

		[Theory]
		[InlineData(null, "2026.11.0", "{\"error\":\"invalid_argument\",\"params\":{\"field\":\"client\"}}")]
		[InlineData("", "2026.11.0", "{\"error\":\"invalid_argument\",\"params\":{\"field\":\"client\"}}")]
		[InlineData("zh_game", null, "{\"error\":\"invalid_argument\",\"params\":{\"field\":\"version\"}}")]
		[InlineData("zh_game", "2026.11", "{\"error\":\"invalid_argument\",\"params\":{\"field\":\"version\"}}")]
		[InlineData("unknown", "2026.11.0", "{\"error\":\"not_found\",\"params\":{\"what\":\"client\"}}")]
		public async Task BadArgumentsAnswerTheirCode(string? clientID, string? version, string expected)
		{
			Result<CompatibilityResponse> result = await Evaluate(clientID, version);
			Assert.False(result.Ok);
			Assert.Equal(expected, ErrorOf(result));
		}

		[Fact]
		public void CompatibilityAnswerIsSnakeCase()
		{
			string json = JsonSerializer.Serialize(new CompatibilityResponse { Compatible = false, MinVersion = "2026.11.2", ServiceVersion = "2026.11.5", Update = s_update }, ApiResults.Json);
			Assert.Equal("{\"compatible\":false,\"min_version\":\"2026.11.2\",\"service_version\":\"2026.11.5\",\"update\":{\"patcher_name\":\"update.exe\",\"patcher_path\":\"https://cdn.example/update.exe\",\"patcher_size\":null}}", json);
		}

		[Theory]
		[InlineData("There are {0} player(s) online.", 7, "There are 7 player(s) online.")]
		[InlineData("No placeholder", 7, "No placeholder")]
		[InlineData("Broken {1} format", 7, "Broken {1} format")]
		[InlineData("Unclosed { brace", 7, "Unclosed { brace")]
		[InlineData("", 7, null)]
		public void MotdFillsThePlayerCount(string template, int players, string? expected)
		{
			Assert.Equal(expected, ConfigSources.FormatMotd(template, players));
		}

		[Theory]
		[InlineData(null, false, null)]
		[InlineData("\"Welcome {0}\"", true, "Welcome {0}")]
		[InlineData("\"\"", true, "")]
		[InlineData("null", false, null)]
		[InlineData("42", false, null)]
		[InlineData("not json", false, null)]
		public void MotdSettingIsAJsonString(string? stored, bool used, string? value)
		{
			Assert.Equal(used, ConfigSources.TryReadMotdSetting(stored, out string? motd));
			Assert.Equal(value, motd);
		}

		[Theory]
		[InlineData("{\"use_mapped_port\":true,\"min_run_ahead_frames\":4}", true)]
		[InlineData("[1,2]", false)]
		[InlineData("not json", false)]
		public void ServiceConfigIsAJsonObject(string text, bool valid)
		{
			Assert.Equal(valid, ConfigSources.ParseServiceConfig(text) != null);
		}

		[Fact]
		public void EosRequestNeedsTheIdToken()
		{
			Assert.Equal(Result.Invalid("id_token"), EosLink.ValidateRequest(null));
			Assert.Equal(Result.Invalid("id_token"), EosLink.ValidateRequest(new LinkEosRequest { IdToken = "" }));
			Assert.Null(EosLink.ValidateRequest(new LinkEosRequest { IdToken = "eyJ" }));
		}

		[Fact]
		public void EosValidationErrorsMapToTheirCodes()
		{
			Assert.Equal(RestCodes.EosInvalidToken, EosLink.FailureOfValidation(new SecurityTokenException("bad"))!.Value.Code);
			Assert.Equal(RestCodes.EosInvalidToken, EosLink.FailureOfValidation(new SecurityTokenExpiredException("old"))!.Value.Code);
			Assert.Equal(RestCodes.EosInvalidToken, EosLink.FailureOfValidation(new ArgumentException("malformed"))!.Value.Code);

			Failure unavailable = EosLink.FailureOfValidation(new HttpRequestException("down"))!.Value;
			Assert.Equal("{\"error\":\"service.unavailable\",\"params\":{\"what\":\"eos\"}}", JsonSerializer.Serialize(ApiResults.BodyOf(unavailable), ApiResults.Json));
			Assert.Equal(RestCodes.ServiceUnavailable, EosLink.FailureOfValidation(new TaskCanceledException())!.Value.Code);
			Assert.Equal(RestCodes.ServiceUnavailable, EosLink.FailureOfValidation(new JsonException())!.Value.Code);
			Assert.Equal(RestCodes.ServiceUnavailable, EosLink.FailureOfValidation(new CryptographicException())!.Value.Code);

			Assert.Null(EosLink.FailureOfValidation(new NullReferenceException()));
		}

		[Fact]
		public void EosLinkOutcomesMapToTheirCodes()
		{
			Assert.Null(EosLink.FailureOf(EosLinkOutcome.Linked));
			Assert.Equal(RestCodes.EosAccountInUse, EosLink.FailureOf(EosLinkOutcome.InUse)!.Value.Code);
			Assert.Equal(RestCodes.EosAccountLocked, EosLink.FailureOf(EosLinkOutcome.Locked)!.Value.Code);
		}

		[Fact]
		public void EosBindingOfAnotherAccountIsAMismatch()
		{
			EosLink.MiddlewareSettings settings = new EosLink.MiddlewareSettings { ExternalAccountType = "openid", DeploymentId = "dep" };
			ClaimsPrincipal Claims(string accountID) => new ClaimsPrincipal(new ClaimsIdentity(new[]
			{
				new Claim("act", "{\"eat\":\"openid\",\"eaid\":\"" + accountID + "\"}"),
				new Claim("pfdid", "dep")
			}));

			Assert.Null(EosLink.CheckEpicAccountBinding(Claims("42"), settings, 42));
			Assert.Equal("account_mismatch", EosLink.CheckEpicAccountBinding(Claims("43"), settings, 42));
			Assert.Equal(RestCodes.EosAccountMismatch, EosLink.FailureOfBinding("account_mismatch")!.Value.Code);
			Assert.Equal(RestCodes.EosAccountMismatch, EosLink.FailureOfBinding("wrong_deployment")!.Value.Code);
			Assert.Null(EosLink.FailureOfBinding(null));
		}

		[Fact]
		public void FactionStatsNameTheirSide()
		{
			FactionStatsStructure stats = new FactionStatsStructure();
			stats.matches[0] = 10;
			stats.wins[0] = 4;
			stats.matches[11] = 3;
			stats.wins[11] = 1;

			IReadOnlyList<FactionStat> factions = GlobalStats.FactionsOf(stats);
			Assert.Equal(FactionStatsStructure.numSides, factions.Count);
			Assert.Equal(new FactionStat { Side = 2, Matches = 10, Wins = 4 }, factions[0]);
			Assert.Equal(new FactionStat { Side = 13, Matches = 3, Wins = 1 }, factions[11]);
		}
	}
}
