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
using GenOnlineService.Realtime;
using GenOnlineService.Rest;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests
{
	// The linked-account routes of section 10.4: provider lookup, the game-session rule, the body and the eos provider
	public class LinkedAccountTests
	{
		private sealed class FakeProvider : ILinkedAccountProvider
		{
			public string Name { get; init; } = "eos";
			public Failure? IssueFailure { get; init; }
			public Failure? LinkFailure { get; init; }
			public List<(Int64 UserID, EUserSessionType SessionType, string Proof)> Links { get; } = new();
			public int Issued { get; private set; }

			public Result<SignInToken> IssueSignInToken(Int64 userID, string displayName)
			{
				Issued++;
				return IssueFailure is Failure failure ? failure : Result.Success(new SignInToken("tok-" + userID + "-" + displayName, 300));
			}

			public Task<Failure?> LinkAsync(Int64 userID, EUserSessionType sessionType, string proof)
			{
				Links.Add((userID, sessionType, proof));
				return Task.FromResult(LinkFailure);
			}
		}

		private static MeController Controller(ILinkedAccountProvider provider, EUserSessionType sessionType, string? body = null)
		{
			DefaultHttpContext context = new DefaultHttpContext
			{
				User = new ClaimsPrincipal(new ClaimsIdentity(new[]
				{
					new Claim(ClaimTypes.NameIdentifier, "42"),
					new Claim("session_type", ((int)sessionType).ToString()),
					new Claim(JwtRegisteredClaimNames.Name, "Ace")
				}, "test"))
			};
			if (body != null)
			{
				byte[] bytes = Encoding.UTF8.GetBytes(body);
				context.Request.Body = new MemoryStream(bytes);
				context.Request.ContentLength = bytes.Length;
			}
			return new MeController(new[] { provider }, null!)
			{
				ControllerContext = new ControllerContext { HttpContext = context }
			};
		}

		private static (int Status, string Body) Answer(IActionResult result)
		{
			JsonResult json = Assert.IsType<JsonResult>(result);
			return (json.StatusCode ?? 200, JsonSerializer.Serialize(json.Value, ApiResults.Json));
		}

		[Fact]
		public void SignInTokenAnswersTokenAndExpiresIn()
		{
			FakeProvider provider = new FakeProvider();
			(int status, string body) = Answer(Controller(provider, EUserSessionType.GameClient).IssueSignInToken("eos"));
			Assert.Equal(200, status);
			Assert.Equal("{\"token\":\"tok-42-Ace\",\"expires_in\":300}", body);
		}

		[Theory]
		[InlineData("steam")]
		[InlineData("EOS")]
		[InlineData("eos ")]
		[InlineData("")]
		public async Task UnknownProviderIsNotFoundOnBothRoutes(string name)
		{
			FakeProvider provider = new FakeProvider();
			(int status, string body) = Answer(Controller(provider, EUserSessionType.GameClient).IssueSignInToken(name));
			Assert.Equal(404, status);
			Assert.Equal("{\"error\":\"not_found\",\"params\":{\"what\":\"provider\"}}", body);

			// the provider is checked before the body and the session
			(status, body) = Answer(await Controller(provider, EUserSessionType.GameLauncher, "{}").LinkAccount(name));
			Assert.Equal(404, status);
			Assert.Equal("{\"error\":\"not_found\",\"params\":{\"what\":\"provider\"}}", body);
			Assert.Equal(0, provider.Issued);
			Assert.Empty(provider.Links);
		}

		[Fact]
		public async Task BothRoutesNeedAGameSession()
		{
			FakeProvider provider = new FakeProvider();
			(int status, string body) = Answer(Controller(provider, EUserSessionType.GameLauncher).IssueSignInToken("eos"));
			Assert.Equal(403, status);
			Assert.Equal("{\"error\":\"session.game_required\",\"params\":{}}", body);

			(status, body) = Answer(await Controller(provider, EUserSessionType.ChatClient, "{\"proof\":\"eyJ\"}").LinkAccount("eos"));
			Assert.Equal(403, status);
			Assert.Equal("{\"error\":\"session.game_required\",\"params\":{}}", body);
			Assert.Equal(0, provider.Issued);
			Assert.Empty(provider.Links);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("{}")]
		[InlineData("{\"proof\":\"\"}")]
		[InlineData("{\"id_token\":\"eyJ\"}")]
		public async Task LinkNeedsTheProof(string? body)
		{
			FakeProvider provider = new FakeProvider();
			(int status, string answer) = Answer(await Controller(provider, EUserSessionType.GameClient, body).LinkAccount("eos"));
			Assert.Equal(400, status);
			Assert.Equal("{\"error\":\"invalid_argument\",\"params\":{\"field\":\"proof\"}}", answer);
			Assert.Empty(provider.Links);
		}

		[Fact]
		public async Task LinkHandsTheProofToTheProvider()
		{
			FakeProvider provider = new FakeProvider();
			IActionResult result = await Controller(provider, EUserSessionType.GameClient, "{\"proof\":\"eyJ\"}").LinkAccount("eos");
			Assert.Equal(204, Assert.IsType<NoContentResult>(result).StatusCode);
			Assert.Equal(new[] { (42L, EUserSessionType.GameClient, "eyJ") }, provider.Links);
		}

		[Fact]
		public async Task ProviderFailuresKeepTheirCodeAndStatus()
		{
			FakeProvider unavailable = new FakeProvider { IssueFailure = LinkedAccounts.Unavailable("eos") };
			(int status, string body) = Answer(Controller(unavailable, EUserSessionType.GameClient).IssueSignInToken("eos"));
			Assert.Equal(503, status);
			Assert.Equal("{\"error\":\"service.unavailable\",\"params\":{\"what\":\"linked_account\",\"provider\":\"eos\"}}", body);

			FakeProvider mismatch = new FakeProvider { LinkFailure = LinkedAccounts.Mismatch("eos") };
			(status, body) = Answer(await Controller(mismatch, EUserSessionType.GameClient, "{\"proof\":\"eyJ\"}").LinkAccount("eos"));
			Assert.Equal(403, status);
			Assert.Equal("{\"error\":\"linked_account.mismatch\",\"params\":{\"provider\":\"eos\"}}", body);
		}

		[Fact]
		public void EosSignInTokenIsTheEosIdToken()
		{
			using RSA rsa = RSA.Create(2048);
			EosTokenIssuer.Settings settings = new EosTokenIssuer.Settings
			{
				Issuer = "https://accounts.example",
				Audience = "genonline-eos",
				Lifetime = TimeSpan.FromSeconds(300),
				SigningKey = new RsaSecurityKey(rsa) { KeyId = "k1" },
				PublishedKeys = new List<JsonWebKey>()
			};
			EosLinkedAccountProvider provider = new EosLinkedAccountProvider(null!, () => settings);
			Assert.Equal("eos", provider.Name);

			Result<SignInToken> issued = provider.IssueSignInToken(42, "Ace");
			Assert.True(issued.Ok);
			Assert.Equal(300, issued.Data!.ExpiresIn);
			JwtSecurityToken jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.Data.Token);
			Assert.Equal(SecurityAlgorithms.RsaSha256, jwt.Header.Alg);
			Assert.Equal("42", jwt.Subject);
			Assert.Equal(new[] { "genonline-eos" }, jwt.Audiences);
			Assert.Equal("Ace", jwt.Claims.Single(c => c.Type == "display_name").Value);
			Assert.Equal("eos", jwt.Claims.Single(c => c.Type == "typ").Value);
		}

		[Fact]
		public void EosWithoutTokenSettingsIsUnavailable()
		{
			EosLinkedAccountProvider provider = new EosLinkedAccountProvider(null!, () => null);
			Result<SignInToken> issued = provider.IssueSignInToken(42, "Ace");
			Assert.False(issued.Ok);
			Assert.Null(issued.Data);
			Assert.Equal("{\"error\":\"service.unavailable\",\"params\":{\"what\":\"linked_account\",\"provider\":\"eos\"}}", JsonSerializer.Serialize(ApiResults.BodyOf(new Failure(issued.Code!, issued.Params!)), ApiResults.Json));
		}
	}
}
