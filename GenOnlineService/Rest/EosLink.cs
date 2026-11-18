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

using GenOnlineService.Realtime;
using Microsoft.IdentityModel.Tokens;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;

namespace GenOnlineService.Rest
{
	public enum EosLinkOutcome { Linked, InUse, Locked }

	// Validates the Epic Connect ID token that PUT /api/v1/me/linked_accounts/eos takes as proof and maps every refusal to its code
	public static class EosLink
	{
		// The provider name of the linked-account routes
		public const string Provider = "eos";

		private static readonly ILogger s_log = AppLog.For(typeof(EosLink));

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
			public List<Jwk>? Keys { get; set; }
		}

		public class Jwk
		{
			public string Kid { get; set; } = String.Empty;
			public string Kty { get; set; } = String.Empty;
			public string N { get; set; } = String.Empty;
			public string E { get; set; } = String.Empty;
		}

		// A rejected token is the caller's fault, a JWKS fetch that fails is ours; any other exception is not handled here
		public static Failure? FailureOfValidation(Exception ex)
		{
			if (ex is SecurityTokenException || ex is ArgumentException)
			{
				return LinkedAccounts.InvalidProof(Provider);
			}

			if (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException
				|| ex is CryptographicException || ex is NotSupportedException || ex is InvalidOperationException)
			{
				return LinkedAccounts.Unavailable(Provider);
			}

			return null;
		}

		// Every binding refusal is one code; the reason goes to the log only
		public static Failure? FailureOfBinding(string? bindingError)
		{
			return bindingError == null ? null : LinkedAccounts.Mismatch(Provider);
		}

		public static Failure? FailureOf(EosLinkOutcome outcome) => outcome switch
		{
			EosLinkOutcome.InUse => LinkedAccounts.InUse(Provider),
			EosLinkOutcome.Locked => LinkedAccounts.Locked(Provider),
			_ => null
		};

		internal static EosLinkOutcome OutcomeOf(WebSocketManager.EMiddlewareIDApplyResult result) => result switch
		{
			WebSocketManager.EMiddlewareIDApplyResult.HeldByOtherUser => EosLinkOutcome.InUse,
			WebSocketManager.EMiddlewareIDApplyResult.LockedInLobby => EosLinkOutcome.Locked,
			_ => EosLinkOutcome.Linked
		};

		// Links the EOS account, the steps and their order are those of the legacy ProvideMWToken
		public static async Task<Failure?> LinkAsync(LobbyManager lobbyManager, Int64 userID, EUserSessionType sessionType, string idToken)
		{
			MiddlewareSettings? settings = LoadMiddlewareSettings();
			if (settings == null)
			{
				s_log.LogError("Middleware config needs jwks_endpoint, audience, issuer and deployment_id");
				return LinkedAccounts.Unavailable(Provider);
			}

			ClaimsPrincipal validatedClaims;
			try
			{
				validatedClaims = await ValidateEpicJwtAsync(idToken, settings);
			}
			catch (Exception ex) when (FailureOfValidation(ex) is Failure failure)
			{
				if (failure.Code == RestCodes.LinkedAccountInvalidProof)
				{
					s_log.LogInformation("Rejected middleware token for user {UserId}: {Reason}", userID, ex.GetType().Name);
				}
				else
				{
					s_log.LogWarning(ex, "Could not fetch the middleware JWKS");
				}
				return failure;
			}

			// read from the validated principal, never by re-parsing the raw token
			string? mwUserID = GetClaimValue(validatedClaims, "sub");
			if (String.IsNullOrEmpty(mwUserID))
			{
				return LinkedAccounts.InvalidProof(Provider);
			}

			string? bindingError = CheckEpicAccountBinding(validatedClaims, settings, userID);
			if (bindingError != null)
			{
				s_log.LogWarning("Rejected middleware token for user {UserId}: {Reason}", userID, bindingError);
				return FailureOfBinding(bindingError);
			}

			// stored per user (not per session) so it survives session churn and lobby members can resolve it live
			// if the player is already in a lobby, peers were signalled before the ID was known, so the manager tells
			// them (serialized under the user's membership gate and the lobby gate, and only when the value changed).
			WebSocketManager.EMiddlewareIDApplyResult applyResult = await lobbyManager.ApplyMiddlewareIDAsync(userID, sessionType, mwUserID);
			return FailureOf(OutcomeOf(applyResult));
		}
	}
}
