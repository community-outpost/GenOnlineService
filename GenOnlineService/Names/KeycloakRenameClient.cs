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

using GenOnlineService.Identity;
using GenOnlineService.Realtime;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GenOnlineService.Names
{
	// The plugin's answer: the name and tag it now holds (Seq null when the name was already the current one), or the failure to answer with
	public sealed record PluginRenameResult(string? Name, string? Tag, long? Seq, Failure? Failure)
	{
		public bool Ok => Failure == null;
	}

	// Calls the Keycloak plugin's rename endpoint (keycloak-changes-contract.md) with a client-credentials token
	public sealed class KeycloakRenameClient
	{
		public const string HttpClientName = "keycloak-rename";

		// a cached token is renewed this long before it expires
		private static readonly TimeSpan s_tokenMargin = TimeSpan.FromSeconds(30);

		private static readonly ILogger s_log = AppLog.For(typeof(KeycloakRenameClient));

		private readonly IHttpClientFactory m_httpFactory;
		private readonly RenameSettings m_settings;
		private readonly TimeProvider m_time;
		private readonly SemaphoreSlim m_tokenLock = new(1, 1);
		private string? m_token;
		private DateTimeOffset m_tokenExpiresAt;

		public KeycloakRenameClient(IHttpClientFactory httpFactory, AccountsSettings accounts, TimeProvider? time = null)
		{
			m_httpFactory = httpFactory;
			m_settings = accounts.NameProvider?.Rename ?? new RenameSettings();
			m_time = time ?? TimeProvider.System;
		}

		public static Failure Unavailable => Result.Fail(ErrorCodes.NameUnavailable);

		// actor is player:<player_id> or discord:<id>; both actor and reason travel in full, the plugin truncates them
		public async Task<PluginRenameResult> RenameAsync(Int64 playerID, string name, string actor, string? reason, CancellationToken cancellationToken = default)
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(m_settings.TimeoutSeconds));
			HttpClient http = m_httpFactory.CreateClient(HttpClientName);

			try
			{
				string? token = await GetTokenAsync(http, timeout.Token);
				if (token == null)
				{
					return Refused(Unavailable);
				}

				Dictionary<string, string> body = new() { ["name"] = name, ["actor"] = actor };
				if (reason != null)
				{
					body["reason"] = reason;
				}

				using HttpRequestMessage request = new(HttpMethod.Post, $"{m_settings.BaseUrl}/realms/{Uri.EscapeDataString(m_settings.Realm)}/genonline/players/{playerID}/name")
				{
					Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
				};
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

				using HttpResponseMessage response = await http.SendAsync(request, timeout.Token);
				string text = await response.Content.ReadAsStringAsync(timeout.Token);
				int status = (int)response.StatusCode;

				if (status == StatusCodes.Status200OK)
				{
					return ReadRenamed(playerID, text);
				}
				if (status == StatusCodes.Status401Unauthorized)
				{
					// a token the plugin no longer accepts is fetched again next time
					await DropTokenAsync(token);
				}

				(Failure failure, LogLevel level) = MapRefusal(status, text);
				if (level != LogLevel.None)
				{
					s_log.Log(level, "Keycloak rename of player {PlayerId} answered HTTP {Status}: {Body}", playerID, status, Shorten(text));
				}
				return Refused(failure);
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				s_log.LogWarning("Keycloak rename of player {PlayerId} timed out after {Seconds} s", playerID, m_settings.TimeoutSeconds);
				return Refused(Unavailable);
			}
			catch (HttpRequestException ex)
			{
				s_log.LogWarning(ex, "Keycloak rename of player {PlayerId} could not reach the plugin", playerID);
				return Refused(Unavailable);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// a bad base URL or any other fault in building or sending the call
				s_log.LogError(ex, "Keycloak rename of player {PlayerId} failed", playerID);
				return Refused(Unavailable);
			}
		}

		// Maps a plugin refusal to the service's answer, with the level to log it at (None for a rule the player broke)
		public static (Failure Failure, LogLevel Level) MapRefusal(int status, string? body)
		{
			string? error = null;
			JsonElement parameters = default;
			try
			{
				using JsonDocument document = JsonDocument.Parse(String.IsNullOrWhiteSpace(body) ? "{}" : body);
				JsonElement root = document.RootElement;
				if (root.ValueKind == JsonValueKind.Object)
				{
					error = root.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
					if (root.TryGetProperty("params", out JsonElement p) && p.ValueKind == JsonValueKind.Object)
					{
						parameters = p.Clone();
					}
				}
			}
			catch (JsonException)
			{
				// not the plugin's error body: decided by the status alone
			}

			if (status == StatusCodes.Status400BadRequest && error == "name.invalid")
			{
				return StringParam(parameters, "rule") switch
				{
					"length" => (Result.Fail(ErrorCodes.NameLength, new { Min = IntParam(parameters, "min") ?? RealtimeHub.NameMinLength, Max = IntParam(parameters, "max") ?? RealtimeHub.NameMaxLength }), LogLevel.None),
					"characters" => (Result.Fail(ErrorCodes.NameInvalidCharacters), LogLevel.None),
					"whitespace" => (Result.Fail(ErrorCodes.NameWhitespace), LogLevel.None),
					"reserved" => (Result.Fail(ErrorCodes.NameReserved, new { Word = StringParam(parameters, "word") ?? String.Empty }), LogLevel.None),
					_ => (Unavailable, LogLevel.Error)
				};
			}
			if (status == StatusCodes.Status409Conflict && error == "name.taken")
			{
				return (Result.Fail(ErrorCodes.NameTaken), LogLevel.None);
			}

			// request.invalid is a bug in this service, 401 and 403 a wrong client or role, 404 a player the plugin does not know
			if (status is >= 400 and < 500)
			{
				return (Unavailable, LogLevel.Error);
			}
			return (Unavailable, LogLevel.Warning);
		}

		private static PluginRenameResult ReadRenamed(Int64 playerID, string text)
		{
			try
			{
				using JsonDocument document = JsonDocument.Parse(text);
				JsonElement root = document.RootElement;
				long? player = root.TryGetProperty("player_id", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : null;
				if (player != playerID)
				{
					s_log.LogError("Keycloak rename of player {PlayerId} answered 200 for player {AnsweredId}", playerID, player);
					return Refused(Unavailable);
				}

				string? name = root.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
				string? tag = root.TryGetProperty("tag", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
				long? seq = root.TryGetProperty("seq", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null;
				if (name != null && tag != null)
				{
					return new PluginRenameResult(name, tag, seq, null);
				}
			}
			catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
			{
				// falls through to the log below
			}

			s_log.LogError("Keycloak rename of player {PlayerId} answered 200 without a name and tag: {Body}", playerID, Shorten(text));
			return Refused(Unavailable);
		}

		private static PluginRenameResult Refused(Failure failure) => new(null, null, null, failure);

		private static string? StringParam(JsonElement parameters, string name)
		{
			return parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
		}

		private static int? IntParam(JsonElement parameters, string name)
		{
			return parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;
		}

		private static string Shorten(string? text) => text == null ? String.Empty : text.Length <= 300 ? text : text.Substring(0, 300);

		// Null when Keycloak refuses the client; the token is cached until shortly before it expires
		private async Task<string?> GetTokenAsync(HttpClient http, CancellationToken cancellationToken)
		{
			await m_tokenLock.WaitAsync(cancellationToken);
			try
			{
				if (m_token != null && m_time.GetUtcNow() < m_tokenExpiresAt)
				{
					return m_token;
				}

				using HttpRequestMessage request = new(HttpMethod.Post, $"{m_settings.BaseUrl}/realms/{Uri.EscapeDataString(m_settings.Realm)}/protocol/openid-connect/token")
				{
					Content = new FormUrlEncodedContent(new Dictionary<string, string>
					{
						["grant_type"] = "client_credentials",
						["client_id"] = m_settings.ClientID,
						["client_secret"] = m_settings.ClientSecret
					})
				};

				using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
				string text = await response.Content.ReadAsStringAsync(cancellationToken);
				if (!response.IsSuccessStatusCode)
				{
					s_log.LogError("Keycloak refused a token for the rename client {ClientId}: HTTP {Status}", m_settings.ClientID, (int)response.StatusCode);
					return null;
				}

				using JsonDocument body = JsonDocument.Parse(text);
				string? token = body.RootElement.TryGetProperty("access_token", out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
				if (String.IsNullOrEmpty(token))
				{
					s_log.LogError("Keycloak returned no access token for the rename client {ClientId}", m_settings.ClientID);
					return null;
				}

				int lifetime = body.RootElement.TryGetProperty("expires_in", out JsonElement expires) && expires.TryGetInt32(out int seconds) ? seconds : 60;
				TimeSpan keep = TimeSpan.FromSeconds(lifetime) - s_tokenMargin;
				m_token = token;
				m_tokenExpiresAt = m_time.GetUtcNow() + (keep > TimeSpan.Zero ? keep : TimeSpan.Zero);
				return token;
			}
			catch (JsonException)
			{
				s_log.LogError("Keycloak answered the rename client's token request with a body that is not JSON");
				return null;
			}
			finally
			{
				m_tokenLock.Release();
			}
		}

		private async Task DropTokenAsync(string token)
		{
			await m_tokenLock.WaitAsync();
			try
			{
				if (m_token == token)
				{
					m_token = null;
				}
			}
			finally
			{
				m_tokenLock.Release();
			}
		}
	}
}
