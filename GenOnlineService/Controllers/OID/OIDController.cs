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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Globalization;

namespace GenOnlineService.Controllers.LoginWithToken
{

	public class POST_OID_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return typeof(POST_OID_Result);
		}
		
		public string user_id { get; set; } = null; // string provides max compat
		public string display_name { get; set; } = null;
		public List<string> roles { get; set; } = new();
		public KnownClients.EKnownClients client_id { get; set; } = KnownClients.EKnownClients.unknown;
	}

	[ApiController]
	[Authorize(Roles = "Player")]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class OID : ControllerBase
	{

		public OID()
		{

		}

		[HttpPost(Name = "PostOID")]
		public async Task<APIResult> Post()
		{
			// if we reach here, the token was valid
			POST_OID_Result result = new POST_OID_Result();

			Int64 user_id = TokenHelper.GetUserID(this);
			if (user_id != -1)
			{
				string strDisplayName = TokenHelper.GetDisplayName(this);
				KnownClients.EKnownClients client_id = TokenHelper.GetClientID(this);

				result.user_id = user_id.ToString();
				result.display_name = strDisplayName;
				result.roles = TokenHelper.GetRoles(this);

				// staff hints for clients come from the roles the service holds, never from the token
				if (StaffRoles.HasPermission(user_id, Permissions.RolesManage))
				{
					result.roles.Add("Admin");
				}

				if (StaffRoles.HasPermission(user_id, Permissions.ModerationKick))
				{
					result.roles.Add("Moderator");
				}
				result.client_id = client_id;
			}

			return result;
		}
	}

	[ApiController]
	[Authorize(Roles = "Player")]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class ProvideMWToken : ControllerBase
	{

		public ProvideMWToken()
		{

		}

		public static string? GetClaimValue(ClaimsPrincipal principal, string claimType)
		{
			return principal.FindFirst(claimType)?.Value;
		}

		public static byte[] Base64UrlDecode(string input)
		{
			return Base64UrlEncoder.DecodeBytes(input);
		}

		// One shared client for JWKS fetches. A new HttpClient per validation exhausts sockets and
		// makes this endpoint trivially DoS-able.
		private static readonly Lazy<HttpClient> s_httpClient = new Lazy<HttpClient>(() =>
		{
			var handler = new SocketsHttpHandler
			{
				PooledConnectionLifetime = TimeSpan.FromMinutes(5),
				PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
			};

			return new HttpClient(handler)
			{
				Timeout = TimeSpan.FromSeconds(10)
			};
		}, true);

		private static readonly SemaphoreSlim s_jwksLock = new SemaphoreSlim(1, 1);
		private static Jwks? s_cachedJwks = null;
		private static DateTime s_cachedJwksExpiry = DateTime.MinValue;
		private static readonly TimeSpan s_jwksCacheDuration = TimeSpan.FromMinutes(10);

		private static async Task<Jwks?> GetJwks(string endpoint, bool bForceRefresh)
		{
			if (!bForceRefresh && s_cachedJwks != null && DateTime.UtcNow < s_cachedJwksExpiry)
			{
				return s_cachedJwks;
			}

			await s_jwksLock.WaitAsync();
			try
			{
				// another caller may have refreshed while we waited
				if (!bForceRefresh && s_cachedJwks != null && DateTime.UtcNow < s_cachedJwksExpiry)
				{
					return s_cachedJwks;
				}

				Jwks? jwks = await s_httpClient.Value.GetFromJsonAsync<Jwks>(endpoint);
				if (jwks != null)
				{
					s_cachedJwks = jwks;
					s_cachedJwksExpiry = DateTime.UtcNow.Add(s_jwksCacheDuration);
				}

				return jwks;
			}
			finally
			{
				s_jwksLock.Release();
			}
		}


		public sealed class MiddlewareSettings
		{
			public string JwksEndpoint { get; init; } = String.Empty;
			public string Audience { get; init; } = String.Empty;
			public string Issuer { get; init; } = String.Empty;
			public string ExternalAccountType { get; init; } = String.Empty;
			public string DeploymentId { get; init; } = String.Empty;
			public string? ProductId { get; init; }
			public string? SandboxId { get; init; }
		}

		private static readonly ILogger s_log = AppLog.For(typeof(ProvideMWToken));

		// Settings without a JWKS endpoint, audience, issuer or deployment ID are unusable and yield null.
		public static MiddlewareSettings? LoadMiddlewareSettings()
		{
			IConfigurationSection middlewareSettings = Program.g_Config.GetSection("Middleware");

			string? jwksEndpoint = middlewareSettings.GetValue<string>("jwks_endpoint");
			string? audience = middlewareSettings.GetValue<string>("audience");
			string? issuer = middlewareSettings.GetValue<string>("issuer");
			string? deploymentId = middlewareSettings.GetValue<string>("deployment_id");

			if (String.IsNullOrEmpty(jwksEndpoint) || String.IsNullOrEmpty(audience) || String.IsNullOrEmpty(issuer) || String.IsNullOrEmpty(deploymentId))
			{
				return null;
			}

			string? externalAccountType = middlewareSettings.GetValue<string>("external_account_type");
			string? productId = middlewareSettings.GetValue<string>("product_id");
			string? sandboxId = middlewareSettings.GetValue<string>("sandbox_id");

			return new MiddlewareSettings
			{
				JwksEndpoint = jwksEndpoint,
				Audience = audience,
				Issuer = issuer,
				ExternalAccountType = String.IsNullOrEmpty(externalAccountType) ? "openid" : externalAccountType,
				DeploymentId = deploymentId,
				ProductId = String.IsNullOrEmpty(productId) ? null : productId,
				SandboxId = String.IsNullOrEmpty(sandboxId) ? null : sandboxId
			};
		}

		// Throws SecurityTokenException or ArgumentException for a token that fails validation.
		public static async Task<ClaimsPrincipal> ValidateEpicJwtAsync(string jwt, MiddlewareSettings settings)
		{
			// MapInboundClaims defaults to true, which renames "sub" to ClaimTypes.NameIdentifier
			// (and friends) on the validated principal. We read the Epic claims by their original
			// names, so leave them untouched.
			var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
			var token = handler.ReadJwtToken(jwt);

			var kid = token.Header.Kid;
			if (kid == null)
				throw new SecurityTokenException("JWT missing kid header");

			// get JWKS (cached; refreshed on an unknown kid in case of key rotation)
			var jwks = await GetJwks(settings.JwksEndpoint, false);

			var key = jwks?.Keys?.FirstOrDefault(k => k.Kid == kid);
			if (key == null)
			{
				jwks = await GetJwks(settings.JwksEndpoint, true);
				key = jwks?.Keys?.FirstOrDefault(k => k.Kid == kid);
			}

			if (key == null)
				throw new SecurityTokenException($"No matching JWKS key for kid={kid}");

			// build RSA pub key
			var rsa = RSA.Create();
			rsa.ImportParameters(new RSAParameters
			{
				Modulus = Base64UrlDecode(key.N),
				Exponent = Base64UrlDecode(key.E)
			});

			var validationParameters = new TokenValidationParameters
			{
				ValidateIssuer = true,
				ValidIssuer = settings.Issuer,

				ValidateAudience = true,
				ValidAudience = settings.Audience,

				ValidateLifetime = true,
				ClockSkew = TimeSpan.FromMinutes(2),

				ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },

				ValidateIssuerSigningKey = true,
				IssuerSigningKey = new RsaSecurityKey(rsa)
				{
					KeyId = key.Kid
				}
			};

			return handler.ValidateToken(jwt, validationParameters, out _);
		}

		// The Epic account must come from the configured identity provider, name the calling user and belong to the configured deployment.
		public static string? CheckEpicAccountBinding(ClaimsPrincipal epicClaims, MiddlewareSettings settings, Int64 user_id)
		{
			string? actJson = GetClaimValue(epicClaims, "act");
			if (String.IsNullOrEmpty(actJson))
			{
				return "missing_external_account";
			}

			string? externalAccountType;
			string? externalAccountId;
			try
			{
				using JsonDocument actDoc = JsonDocument.Parse(actJson);
				if (actDoc.RootElement.ValueKind != JsonValueKind.Object)
				{
					return "malformed_external_account";
				}

				externalAccountType = ReadStringProperty(actDoc.RootElement, "eat");
				externalAccountId = ReadStringProperty(actDoc.RootElement, "eaid");
			}
			catch (JsonException)
			{
				return "malformed_external_account";
			}

			if (!String.Equals(externalAccountType, settings.ExternalAccountType, StringComparison.Ordinal))
			{
				return "wrong_identity_provider";
			}

			if (!String.Equals(externalAccountId, user_id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
			{
				return "account_mismatch";
			}

			if (!String.Equals(GetClaimValue(epicClaims, "pfdid"), settings.DeploymentId, StringComparison.Ordinal))
			{
				return "wrong_deployment";
			}

			if (settings.ProductId != null && !String.Equals(GetClaimValue(epicClaims, "pfpid"), settings.ProductId, StringComparison.Ordinal))
			{
				return "wrong_product";
			}

			if (settings.SandboxId != null && !String.Equals(GetClaimValue(epicClaims, "pfsid"), settings.SandboxId, StringComparison.Ordinal))
			{
				return "wrong_sandbox";
			}

			return null;
		}

		private static string? ReadStringProperty(JsonElement element, string propertyName)
		{
			if (element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String)
			{
				return value.GetString();
			}

			return null;
		}

		public class Jwks
		{
			public List<Jwk> Keys { get; set; }
		}

		public class Jwk
		{
			public string Kid { get; set; }
			public string Kty { get; set; }
			public string N { get; set; }
			public string E { get; set; }
		}

		private ObjectResult Fail(HttpStatusCode statusCode, string error)
		{
			return StatusCode((int)statusCode, new { error });
		}

		[HttpPost(Name = "ProvideMWToken")]
		public async Task<IActionResult> Post()
		{
			string? mw_token = null;
			try
			{
				using var reader = new StreamReader(HttpContext.Request.Body);
				var options = new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				};

				string jsonData = await reader.ReadToEndAsync();
				var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData, options);
				if (data != null && data.TryGetValue("mw_token", out JsonElement tokenElement) && tokenElement.ValueKind == JsonValueKind.String)
				{
					mw_token = tokenElement.GetString();
				}
			}
			catch (JsonException)
			{
				return Fail(HttpStatusCode.BadRequest, "malformed_body");
			}

			if (String.IsNullOrEmpty(mw_token))
			{
				return Fail(HttpStatusCode.BadRequest, "missing_mw_token");
			}

			// only game clients should be doing middleware login
			Int64 user_id = TokenHelper.GetUserID(this);
			EUserSessionType sessionType = TokenHelper.GetSessionType(this);
			if (user_id == -1 || !SessionHelpers.SessionTypeHasAccessTo(sessionType, ESessionAccessType.Gameplay))
			{
				return Fail(HttpStatusCode.Forbidden, "gameplay_session_required");
			}

			MiddlewareSettings? settings = LoadMiddlewareSettings();
			if (settings == null)
			{
				s_log.LogError("Middleware config needs jwks_endpoint, audience, issuer and deployment_id");
				return Fail(HttpStatusCode.ServiceUnavailable, "middleware_not_configured");
			}

			ClaimsPrincipal validatedClaims;
			try
			{
				validatedClaims = await ValidateEpicJwtAsync(mw_token, settings);
			}
			catch (Exception ex) when (ex is SecurityTokenException || ex is ArgumentException)
			{
				s_log.LogInformation("Rejected middleware token for user {UserId}: {Reason}", user_id, ex.GetType().Name);
				return Fail(HttpStatusCode.Unauthorized, "invalid_mw_token");
			}
			catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException
				|| ex is CryptographicException || ex is NotSupportedException || ex is InvalidOperationException)
			{
				s_log.LogWarning(ex, "Could not fetch the middleware JWKS");
				return Fail(HttpStatusCode.ServiceUnavailable, "middleware_jwks_unavailable");
			}

			// read from the validated principal, never by re-parsing the raw token
			string? mwUserID = GetClaimValue(validatedClaims, "sub");
			if (String.IsNullOrEmpty(mwUserID))
			{
				return Fail(HttpStatusCode.Unauthorized, "invalid_mw_token");
			}

			string? bindingError = CheckEpicAccountBinding(validatedClaims, settings, user_id);
			if (bindingError != null)
			{
				s_log.LogWarning("Rejected middleware token for user {UserId}: {Reason}", user_id, bindingError);
				return Fail(HttpStatusCode.Forbidden, bindingError);
			}

			// stored per user (not per session) so it survives session churn and lobby members can resolve it live
			// if the player is already in a lobby, peers were signalled before the ID was known, so the manager tells
			// them (serialized under the user's membership gate and the lobby gate, and only when the value changed).
			LobbyManager lobbyManager = ServiceLocator.Services.GetRequiredService<LobbyManager>();
			var applyResult = await lobbyManager.ApplyMiddlewareIDAsync(user_id, sessionType, mwUserID);
			switch (applyResult)
			{
				case WebSocketManager.EMiddlewareIDApplyResult.HeldByOtherUser:
					return Fail(HttpStatusCode.Conflict, "mw_id_in_use");
				case WebSocketManager.EMiddlewareIDApplyResult.LockedInLobby:
					return Fail(HttpStatusCode.Conflict, "mw_id_locked_in_lobby");
				default:
					return Ok();
			}
		}
	}
}
