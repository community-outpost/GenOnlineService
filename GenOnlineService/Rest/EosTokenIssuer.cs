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

using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;

namespace GenOnlineService.Rest
{
	// Signs the short lived ID tokens that EOS Connect verifies through the published JWKS.
	public static class EosTokenIssuer
	{
		private static readonly ILogger s_log = AppLog.For(typeof(EosTokenIssuer));
		private static readonly object s_lock = new();
		private static Settings? s_settings = null;
		private static bool s_bLoaded = false;

		public sealed class Settings
		{
			public required string Issuer { get; init; }
			public required string Audience { get; init; }
			public required TimeSpan Lifetime { get; init; }
			public required RsaSecurityKey SigningKey { get; init; }
			public required List<JsonWebKey> PublishedKeys { get; init; }
		}

		// Missing or unreadable settings disable the endpoint until the next restart.
		public static Settings? GetSettings()
		{
			if (s_bLoaded)
			{
				return s_settings;
			}

			lock (s_lock)
			{
				if (!s_bLoaded)
				{
					s_settings = Load();
					s_bLoaded = true;
				}

				return s_settings;
			}
		}

		private static Settings? Load()
		{
			IConfigurationSection section = Program.g_Config.GetSection("EosToken");
			string? issuer = section["issuer"];
			string? audience = section["audience"];
			string? keyID = section["key_id"];
			string? keyPath = section["private_key_pem_path"];
			if (String.IsNullOrEmpty(issuer) || String.IsNullOrEmpty(audience) || String.IsNullOrEmpty(keyID) || String.IsNullOrEmpty(keyPath))
			{
				s_log.LogInformation("EosToken is disabled because issuer, audience, key_id or private_key_pem_path is not set");
				return null;
			}

			try
			{
				RSA rsa = RSA.Create();
				rsa.ImportFromPem(File.ReadAllText(keyPath));
				if (rsa.KeySize < 2048)
				{
					rsa.Dispose();
					s_log.LogError("EosToken signing key must be at least 2048 bits");
					return null;
				}

				var signingKey = new RsaSecurityKey(rsa) { KeyId = keyID };
				List<JsonWebKey> publishedKeys = new() { PublicJwk(rsa, keyID) };

				// A previous public key stays published while tokens signed with it may still be cached by EOS.
				string? previousKeyID = section["previous_key_id"];
				string? previousKeyPath = section["previous_public_key_pem_path"];
				if (!String.IsNullOrEmpty(previousKeyID) && !String.IsNullOrEmpty(previousKeyPath))
				{
					// An unusable previous key is skipped so the current key keeps working.
					try
					{
						using RSA previous = RSA.Create();
						previous.ImportFromPem(File.ReadAllText(previousKeyPath));
						if (previous.KeySize >= 2048)
						{
							publishedKeys.Add(PublicJwk(previous, previousKeyID));
						}
						else
						{
							s_log.LogError("EosToken previous public key must be at least 2048 bits");
						}
					}
					catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is CryptographicException)
					{
						s_log.LogError(ex, "EosToken previous public key could not be loaded");
					}
				}

				int lifetimeSeconds = Math.Clamp(int.TryParse(section["lifetime_seconds"], out int configuredLifetime) ? configuredLifetime : 300, 60, 900);
				return new Settings
				{
					Issuer = issuer,
					Audience = audience,
					Lifetime = TimeSpan.FromSeconds(lifetimeSeconds),
					SigningKey = signingKey,
					PublishedKeys = publishedKeys
				};
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is CryptographicException)
			{
				s_log.LogError(ex, "EosToken signing key could not be loaded");
				return null;
			}
		}

		private static JsonWebKey PublicJwk(RSA rsa, string keyID)
		{
			RSAParameters parameters = rsa.ExportParameters(false);
			return new JsonWebKey
			{
				Kty = "RSA",
				Use = "sig",
				Alg = SecurityAlgorithms.RsaSha256,
				Kid = keyID,
				N = Base64UrlEncoder.Encode(parameters.Modulus),
				E = Base64UrlEncoder.Encode(parameters.Exponent)
			};
		}

		// The token names the player in sub and carries no roles and no token generation.
		public static string Issue(Settings settings, Int64 userID, string displayName, out DateTime expiresUtc)
		{
			DateTime now = DateTime.UtcNow;
			expiresUtc = now.Add(settings.Lifetime);
			var header = new JwtHeader(new SigningCredentials(settings.SigningKey, SecurityAlgorithms.RsaSha256));
			var payload = new JwtPayload(
				settings.Issuer,
				settings.Audience,
				new List<Claim>
				{
					new Claim(JwtRegisteredClaimNames.Sub, userID.ToString()),
					new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
					new Claim("typ", "eos"),
					new Claim("display_name", displayName)
				},
				now.AddSeconds(-30),
				expiresUtc,
				now);
			return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
		}
	}
}
