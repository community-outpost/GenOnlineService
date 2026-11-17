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
using GenOnlineService.Rest;
using System.Security.Claims;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests
{
	// The token and service account rules the CLI and /api/v1 share
	public class RestTokensTests
	{
		private static readonly HashSet<string> s_moderator = new HashSet<string>(StringComparer.Ordinal) { Permissions.ModerationKick, Permissions.ModerationMute };
		private static readonly ApiTokenSettings s_settings = new ApiTokenSettings();
		private static readonly DateTime s_now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

		private static string Body(Action action)
		{
			ApiTokenAdminException ex = Assert.Throws<ApiTokenAdminException>(action);
			return JsonSerializer.Serialize(ApiResults.BodyOf(ex.Failure), ApiResults.Json);
		}

		[Fact]
		public void ScopesTheOwnerHoldsPass()
		{
			ApiTokenAdmin.CheckScopes(ApiTokenOwnerKinds.User, [Permissions.ModerationKick], s_moderator, "user:7", bFromHost: false);
		}

		[Fact]
		public void ScopeTheOwnerLacksIsForbidden()
		{
			string body = Body(() => ApiTokenAdmin.CheckScopes(ApiTokenOwnerKinds.User, [Permissions.ModerationKick, Permissions.ModerationBan], s_moderator, "user:7", bFromHost: false));
			Assert.Equal("{\"error\":\"forbidden\",\"params\":{\"permission\":\"moderation.ban\"}}", body);
		}

		[Fact]
		public void ScopeTheOwnerLacksKeepsTheCliMessage()
		{
			ApiTokenAdminException ex = Assert.Throws<ApiTokenAdminException>(() => ApiTokenAdmin.CheckScopes(ApiTokenOwnerKinds.User, [Permissions.ModerationBan], s_moderator, "user:7", bFromHost: true));
			Assert.Equal("user:7 does not hold moderation.ban; a token never grants more than its owner holds.", ex.Message);
		}

		[Fact]
		public void UnknownScopeIsInvalid()
		{
			string body = Body(() => ApiTokenAdmin.CheckScopes(ApiTokenOwnerKinds.User, ["moderation.everything"], s_moderator, "user:7", bFromHost: false));
			Assert.Equal("{\"error\":\"invalid_argument\",\"params\":{\"field\":\"scopes\"}}", body);
		}

		[Fact]
		public void NoScopeIsInvalid()
		{
			string body = Body(() => ApiTokenAdmin.CheckScopes(ApiTokenOwnerKinds.User, [], s_moderator, "user:7", bFromHost: false));
			Assert.Equal("{\"error\":\"invalid_argument\",\"params\":{\"field\":\"scopes\"}}", body);
		}

		[Fact]
		public void MachinePermissionOnAUserTokenIsRefused()
		{
			HashSet<string> held = new HashSet<string>(s_moderator, StringComparer.Ordinal) { Permissions.LobbiesRead };
			ApiTokenAdminException ex = Assert.Throws<ApiTokenAdminException>(() => ApiTokenAdmin.CheckScopes(ApiTokenOwnerKinds.User, [Permissions.LobbiesRead], held, "user:7", bFromHost: true));
			Assert.Equal("lobbies.read is a machine permission and only service accounts hold it.", ex.Message);
			Assert.Equal("{\"error\":\"forbidden\",\"params\":{\"permission\":\"lobbies.read\"}}", JsonSerializer.Serialize(ApiResults.BodyOf(ex.Failure), ApiResults.Json));
		}

		[Fact]
		public void PrivilegedScopeOnlyFromTheHost()
		{
			HashSet<string> held = new HashSet<string>(StringComparer.Ordinal) { Permissions.UsersRevoke };
			ApiTokenAdmin.CheckScopes(ApiTokenOwnerKinds.ServiceAccount, [Permissions.UsersRevoke], held, "service:website", bFromHost: true);
			string body = Body(() => ApiTokenAdmin.CheckScopes(ApiTokenOwnerKinds.ServiceAccount, [Permissions.UsersRevoke], held, "service:website", bFromHost: false));
			Assert.Equal("{\"error\":\"forbidden\",\"params\":{\"permission\":\"users.revoke\"}}", body);
		}

		[Fact]
		public void MachineScopeOnAServiceAccountPasses()
		{
			HashSet<string> held = new HashSet<string>(StringComparer.Ordinal) { Permissions.LobbiesRead };
			ApiTokenAdmin.CheckScopes(ApiTokenOwnerKinds.ServiceAccount, [Permissions.LobbiesRead], held, "service:website", bFromHost: false);
		}

		[Theory]
		[InlineData(Permissions.UsersRevoke)]
		[InlineData(Permissions.OpsDatabase)]
		public void PrivilegedGrantIsRefusedOverHttp(string permission)
		{
			string body = Body(() => ApiTokenAdmin.CheckHttpGrant(permission, s_moderator));
			Assert.Equal($"{{\"error\":\"forbidden\",\"params\":{{\"permission\":\"{permission}\"}}}}", body);
		}

		[Fact]
		public void HumanGrantNeedsTheCallerToHoldIt()
		{
			ApiTokenAdmin.CheckHttpGrant(Permissions.ModerationKick, s_moderator);
			string body = Body(() => ApiTokenAdmin.CheckHttpGrant(Permissions.ModerationBan, s_moderator));
			Assert.Equal("{\"error\":\"forbidden\",\"params\":{\"permission\":\"moderation.ban\"}}", body);
		}

		[Fact]
		public void UnprivilegedMachineGrantPasses()
		{
			ApiTokenAdmin.CheckHttpGrant(Permissions.LobbiesRead, s_moderator);
		}

		[Fact]
		public void UnknownGrantIsInvalid()
		{
			string body = Body(() => ApiTokenAdmin.CheckHttpGrant("nope", s_moderator));
			Assert.Equal("{\"error\":\"invalid_argument\",\"params\":{\"field\":\"grants\"}}", body);
		}

		[Fact]
		public void ExpiryMustBeInTheFuture()
		{
			ApiTokenAdminException ex = Assert.Throws<ApiTokenAdminException>(() => ApiTokenAdmin.CheckLifetime(s_settings, [Permissions.ModerationKick], null, s_now, s_now));
			Assert.Equal("The expiry date must be in the future.", ex.Message);
			Assert.Equal("{\"error\":\"invalid_argument\",\"params\":{\"field\":\"expires_at\"}}", JsonSerializer.Serialize(ApiResults.BodyOf(ex.Failure), ApiResults.Json));
		}

		[Fact]
		public void ExpiryAtMostTheMaximumLifetime()
		{
			ApiTokenAdmin.CheckLifetime(s_settings, [Permissions.ModerationKick], null, s_now.AddDays(365), s_now);
			ApiTokenAdminException ex = Assert.Throws<ApiTokenAdminException>(() => ApiTokenAdmin.CheckLifetime(s_settings, [Permissions.ModerationKick], null, s_now.AddDays(365).AddSeconds(1), s_now));
			Assert.Equal("A token may live at most 365 days.", ex.Message);
		}

		[Fact]
		public void PrivilegedTokenLivesShorterAndNeedsAnAllowList()
		{
			ApiTokenAdmin.CheckLifetime(s_settings, [Permissions.UsersRevoke], "203.0.113.4/32", s_now.AddDays(30), s_now);
			ApiTokenAdminException tooLong = Assert.Throws<ApiTokenAdminException>(() => ApiTokenAdmin.CheckLifetime(s_settings, [Permissions.UsersRevoke], "203.0.113.4/32", s_now.AddDays(31), s_now));
			Assert.Equal("A token with a privileged scope may live at most 30 days.", tooLong.Message);
			ApiTokenAdminException open = Assert.Throws<ApiTokenAdminException>(() => ApiTokenAdmin.CheckLifetime(s_settings, [Permissions.UsersRevoke], null, s_now.AddDays(10), s_now));
			Assert.Equal("A token with a privileged scope needs --allowed-ips.", open.Message);
		}

		[Fact]
		public void ActiveLimitIsReachedAtTheMaximum()
		{
			ApiTokenAdmin.CheckActiveCount(19, s_settings, "user:7");
			ApiTokenAdminException ex = Assert.Throws<ApiTokenAdminException>(() => ApiTokenAdmin.CheckActiveCount(20, s_settings, "user:7"));
			Assert.Equal("user:7 already has 20 active tokens.", ex.Message);
			Assert.Equal("{\"error\":\"token.limit_reached\",\"params\":{\"max\":20}}", JsonSerializer.Serialize(ApiResults.BodyOf(ex.Failure), ApiResults.Json));
			Assert.Equal(422, RestResults.StatusOf(ex.Failure.Code));
		}

		[Fact]
		public void UnreadableAllowListIsInvalid()
		{
			string body = Body(() => ApiTokenAdmin.CheckAllowedIPs("10.1"));
			Assert.Equal("{\"error\":\"invalid_argument\",\"params\":{\"field\":\"allowed_ips\"}}", body);
		}

		[Theory]
		[InlineData("2027-01-31", "2027-01-31T00:00:00Z")]
		[InlineData("2027-01-31T10:20:30Z", "2027-01-31T10:20:30Z")]
		public void ExpiresAtReadsADateOrATimestamp(string text, string expected)
		{
			Assert.Equal(expected, ApiTokenAdmin.ParseExpiresAt(text).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("tomorrow")]
		public void ExpiresAtIsRequired(string? text)
		{
			string body = Body(() => ApiTokenAdmin.ParseExpiresAt(text));
			Assert.Equal("{\"error\":\"invalid_argument\",\"params\":{\"field\":\"expires_at\"}}", body);
		}

		[Fact]
		public void ServiceAccountNameFollowsTheCliRule()
		{
			ApiTokenAdmin.CheckServiceAccountName("website");
			ApiTokenAdminException ex = Assert.Throws<ApiTokenAdminException>(() => ApiTokenAdmin.CheckServiceAccountName("Web Site"));
			Assert.Equal("{\"error\":\"invalid_argument\",\"params\":{\"field\":\"name\"}}", JsonSerializer.Serialize(ApiResults.BodyOf(ex.Failure), ApiResults.Json));
		}

		private static ClaimsPrincipal Principal(string authenticationType, params Claim[] claims) => new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));

		private static string? KindOf(string? clientID) => clientID switch { "website" => ClientKinds.App, "zh-client" => ClientKinds.Game, _ => null };

		[Fact]
		public void OnlyAnAppClientSessionManagesTokens()
		{
			Claim me = new Claim(ClaimTypes.NameIdentifier, "7");
			Assert.Equal(ApiTokenCallers.WebSession, ApiTokenAdmin.CallerOf(Principal("session", me, new Claim("client_id", "website")), KindOf));
			Assert.Equal(ApiTokenCallers.GameSession, ApiTokenAdmin.CallerOf(Principal("session", me, new Claim("client_id", "zh-client"), new Claim("session_type", "0")), KindOf));
			// a game client token without the game scope carries no session_type and is still not a web session
			Assert.Equal(ApiTokenCallers.GameSession, ApiTokenAdmin.CallerOf(Principal("session", me, new Claim("client_id", "zh-client")), KindOf));
			Assert.Equal(ApiTokenCallers.GameSession, ApiTokenAdmin.CallerOf(Principal("session", me, new Claim("client_id", "website"), new Claim("session_type", "0")), KindOf));
			Assert.Equal(ApiTokenCallers.GameSession, ApiTokenAdmin.CallerOf(Principal("session", me, new Claim("client_id", "gone")), KindOf));
			Assert.Equal(ApiTokenCallers.Token, ApiTokenAdmin.CallerOf(Principal(TokenPrincipal.AuthenticationType, new Claim(ApiTokenHandler.TokenIDClaim, "3")), KindOf));
			Assert.Equal(ApiTokenCallers.None, ApiTokenAdmin.CallerOf(new ClaimsPrincipal(new ClaimsIdentity()), KindOf));
		}

		// The staff pages' sign-in cookie, as SignInCookieHandler builds it, counts as a web session; its marker alone does not
		[Fact]
		public void TheSignInCookieOfAUserIsAWebSession()
		{
			var cookie = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "7")], "Identity.Application"));
			cookie.AddIdentity(new ClaimsIdentity(ApiAuthentication.SignInCookieScheme));
			Assert.Equal(ApiTokenCallers.WebSession, ApiTokenAdmin.CallerOf(cookie, KindOf));

			var markerOnly = new ClaimsPrincipal(new ClaimsIdentity(ApiAuthentication.SignInCookieScheme));
			Assert.Equal(ApiTokenCallers.None, ApiTokenAdmin.CallerOf(markerOnly, KindOf));
		}
	}
}
