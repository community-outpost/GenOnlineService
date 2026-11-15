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

using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GenOnlineService.Identity
{
	// Applies Keycloak group membership and display name changes between sign-ins by polling the realm's events
	public static class KeycloakGroupSync
	{
		private static readonly ILogger s_log = AppLog.For(typeof(KeycloakGroupSync));
		private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(15) };
		private const int PageSize = 100;

		// events are read again this far behind the cursor, so one committed late is not missed; applying is idempotent
		private const long LookbackMs = 60_000;

		private static string CursorKey(UpstreamProviderSettings provider) => $"group_sync.{provider.Name}.cursor";

		// names have their own cursor, so a failure in one feed never holds the other back
		private static string NameCursorKey(UpstreamProviderSettings provider) => $"group_sync.{provider.Name}.names_cursor";

		// Keycloak base URL: configured, or the issuer without its /realms/<realm> part
		private static string BaseUrl(UpstreamProviderSettings provider)
		{
			if (!String.IsNullOrWhiteSpace(provider.GroupSync.AdminBaseUrl))
			{
				return provider.GroupSync.AdminBaseUrl.TrimEnd('/');
			}

			int index = provider.Issuer.IndexOf("/realms/", StringComparison.Ordinal);
			return index < 0 ? provider.Issuer.TrimEnd('/') : provider.Issuer.Substring(0, index);
		}

		private static string Realm(UpstreamProviderSettings provider)
		{
			if (!String.IsNullOrWhiteSpace(provider.GroupSync.Realm))
			{
				return provider.GroupSync.Realm;
			}

			int index = provider.Issuer.IndexOf("/realms/", StringComparison.Ordinal);
			return index < 0 ? String.Empty : provider.Issuer.Substring(index + "/realms/".Length).Trim('/');
		}

		// Unreachable Keycloak throws: the caller logs it, nothing changes and the cursors stay for the next attempt
		public static async Task Run(AppDbContext db, UpstreamProviderSettings provider)
		{
			Exception? failure = null;
			try
			{
				await RunGroups(db, provider);
			}
			catch (Exception ex)
			{
				failure = ex;
			}

			try
			{
				// with the plugin's change log on, names arrive through it and the event poll stands down
				if (!provider.ChangeLog.Enabled)
				{
					await RunNames(db, provider);
				}
			}
			catch (Exception ex)
			{
				if (failure != null)
				{
					// the caller logs the first failure only
					s_log.LogWarning(ex, "Name sync for {Provider} failed as well", provider.Name);
				}
				failure ??= ex;
			}

			if (failure != null)
			{
				System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
			}
		}

		// Renames: a player's own (user event UPDATE_PROFILE) and an admin's (admin event UPDATE on the user). The plugin
		// re-tags after an admin change commits, so the lookback reads the user again and picks up the final tag.
		private static async Task RunNames(AppDbContext db, UpstreamProviderSettings provider)
		{
			string baseUrl = BaseUrl(provider);
			string realm = Uri.EscapeDataString(Realm(provider));
			long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

			string? stored = await Database.Upstream.GetSetting(db, NameCursorKey(provider));
			if (stored == null || !long.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out long cursor))
			{
				// names before the first run are copied at each user's next sign-in
				await Database.Upstream.SetSetting(db, NameCursorKey(provider), nowMs.ToString(CultureInfo.InvariantCulture), "group_sync");
				s_log.LogInformation("Name sync for {Provider} starts at {Cursor}", provider.Name, nowMs);
				return;
			}

			string accessToken = await GetAccessToken(baseUrl, realm, provider);
			string dateFrom = DateTimeOffset.FromUnixTimeMilliseconds(cursor).UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			var affected = new HashSet<string>(StringComparer.Ordinal);
			long newest = cursor;

			void Take(long time, string? keycloakUserID)
			{
				if (time >= cursor - LookbackMs && !String.IsNullOrEmpty(keycloakUserID))
				{
					affected.Add(keycloakUserID);
					newest = Math.Max(newest, time);
				}
			}

			await ReadPages($"{baseUrl}/admin/realms/{realm}/events?type=UPDATE_PROFILE&dateFrom={dateFrom}", accessToken, userEvent =>
				Take(userEvent.TryGetProperty("time", out JsonElement t) ? t.GetInt64() : 0,
					userEvent.TryGetProperty("userId", out JsonElement u) ? u.GetString() : null));

			await ReadPages($"{baseUrl}/admin/realms/{realm}/admin-events?resourceTypes=USER&operationTypes=UPDATE&dateFrom={dateFrom}", accessToken, adminEvent =>
			{
				string path = adminEvent.TryGetProperty("resourcePath", out JsonElement p) ? p.GetString() ?? String.Empty : String.Empty;
				string[] parts = path.Split('/');
				Take(adminEvent.TryGetProperty("time", out JsonElement t) ? t.GetInt64() : 0, parts.Length == 2 && parts[0] == "users" ? parts[1] : null);
			});

			foreach (string keycloakUserID in affected)
			{
				Int64? userID = await db.Identities.AsNoTracking()
					.Where(i => i.Provider == provider.Name && i.ExternUid == keycloakUserID)
					.Select(i => (Int64?)i.UserID)
					.FirstOrDefaultAsync();
				if (userID == null)
				{
					continue;
				}

				(string? Name, string? Tag)? profile = await GetName(baseUrl, realm, keycloakUserID, accessToken);
				if (profile != null)
				{
					await UpstreamAccounts.ApplyUpstreamName(db, provider, userID.Value, profile.Value.Name, profile.Value.Tag);
				}
			}

			if (newest != cursor)
			{
				await Database.Upstream.SetSetting(db, NameCursorKey(provider), newest.ToString(CultureInfo.InvariantCulture), "group_sync");
			}
		}

		private static async Task ReadPages(string url, string accessToken, Action<JsonElement> each)
		{
			for (int first = 0; ; first += PageSize)
			{
				using JsonDocument page = await GetJson($"{url}&first={first}&max={PageSize}", accessToken);
				int count = 0;
				foreach (JsonElement item in page.RootElement.EnumerateArray())
				{
					count++;
					each(item);
				}

				if (count < PageSize)
				{
					return;
				}
			}
		}

		// The user's display_name and name_tag attributes; null when the Keycloak user no longer exists
		private static async Task<(string? Name, string? Tag)?> GetName(string baseUrl, string realm, string keycloakUserID, string accessToken)
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/admin/realms/{realm}/users/{Uri.EscapeDataString(keycloakUserID)}");
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
			using HttpResponseMessage response = await s_http.SendAsync(request);
			if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
			{
				return null;
			}

			response.EnsureSuccessStatusCode();
			using JsonDocument user = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
			string? First(string attribute) => user.RootElement.TryGetProperty("attributes", out JsonElement attributes)
				&& attributes.TryGetProperty(attribute, out JsonElement values) && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > 0
				? values[0].GetString()
				: null;
			return (First("display_name"), First("name_tag"));
		}

		private static async Task RunGroups(AppDbContext db, UpstreamProviderSettings provider)
		{
			string baseUrl = BaseUrl(provider);
			string realm = Uri.EscapeDataString(Realm(provider));
			long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

			string? stored = await Database.Upstream.GetSetting(db, CursorKey(provider));
			if (stored == null || !long.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out long cursor))
			{
				// the first run starts now; membership before it is applied at each user's next sign-in
				await Database.Upstream.SetSetting(db, CursorKey(provider), nowMs.ToString(CultureInfo.InvariantCulture), "group_sync");
				s_log.LogInformation("Group sync for {Provider} starts at {Cursor}", provider.Name, nowMs);
				return;
			}

			string accessToken = await GetAccessToken(baseUrl, realm, provider);

			var affected = new HashSet<string>(StringComparer.Ordinal);
			long newest = cursor;
			string dateFrom = DateTimeOffset.FromUnixTimeMilliseconds(cursor).UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

			for (int first = 0; ; first += PageSize)
			{
				string url = $"{baseUrl}/admin/realms/{realm}/admin-events?resourceTypes=GROUP_MEMBERSHIP&dateFrom={dateFrom}&first={first}&max={PageSize}";
				using JsonDocument page = await GetJson(url, accessToken);

				int count = 0;
				foreach (JsonElement adminEvent in page.RootElement.EnumerateArray())
				{
					count++;
					long time = adminEvent.TryGetProperty("time", out JsonElement t) ? t.GetInt64() : 0;
					string path = adminEvent.TryGetProperty("resourcePath", out JsonElement p) ? p.GetString() ?? String.Empty : String.Empty;

					if (time < cursor - LookbackMs)
					{
						continue;
					}

					string[] parts = path.Split('/');
					if (parts.Length >= 2 && parts[0] == "users")
					{
						affected.Add(parts[1]);
					}

					newest = Math.Max(newest, time);
				}

				if (count < PageSize)
				{
					break;
				}
			}

			foreach (string keycloakUserID in affected)
			{
				Int64? userID = await db.Identities.AsNoTracking()
					.Where(i => i.Provider == provider.Name && i.ExternUid == keycloakUserID)
					.Select(i => (Int64?)i.UserID)
					.FirstOrDefaultAsync();

				// a person who never signed in to this environment has nothing to update
				if (userID == null)
				{
					continue;
				}

				HashSet<string>? groups = await GetGroups(baseUrl, realm, keycloakUserID, accessToken);
				if (groups == null)
				{
					// a user deleted in Keycloak loses its upstream roles, but an upstream ban outlives the Keycloak account
					groups = new HashSet<string>(StringComparer.Ordinal);
					bool bBanned = provider.BanGroup != null && await db.Sanctions.AnyAsync(s => s.UserID == userID.Value && s.Source == provider.Source
						&& s.Type == SanctionTypes.Ban && s.LiftedAt == null && (s.ExpiresAt == null || s.ExpiresAt > DateTime.UtcNow));
					if (bBanned)
					{
						groups.Add(provider.BanGroup!);
					}
				}

				Database.Upstream.Changes changes = await UpstreamAccounts.ApplyTrustedClaims(db, provider, userID.Value, groups);

				// losing a role without permissions, such as streamer, only changes features and keeps the sessions
				if (changes.RolesRemoved.Any(StaffRoles.IsStaff))
				{
					await SessionRevocation.RevokeUser(userID.Value, true, $"removed from a staff group in {provider.DisplayName}");
				}
			}

			if (newest != cursor)
			{
				await Database.Upstream.SetSetting(db, CursorKey(provider), newest.ToString(CultureInfo.InvariantCulture), "group_sync");
			}
		}

		private static async Task<string> GetAccessToken(string baseUrl, string realm, UpstreamProviderSettings provider)
		{
			using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/realms/{realm}/protocol/openid-connect/token")
			{
				Content = new FormUrlEncodedContent(new Dictionary<string, string>
				{
					["grant_type"] = "client_credentials",
					["client_id"] = provider.GroupSync.ClientID,
					["client_secret"] = provider.GroupSync.ClientSecret
				})
			};

			using HttpResponseMessage response = await s_http.SendAsync(request);
			response.EnsureSuccessStatusCode();
			using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
			return body.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Keycloak returned no access token.");
		}

		private static async Task<JsonDocument> GetJson(string url, string accessToken)
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, url);
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
			using HttpResponseMessage response = await s_http.SendAsync(request);
			response.EnsureSuccessStatusCode();
			return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		}

		// Null when the Keycloak user no longer exists
		private static async Task<HashSet<string>?> GetGroups(string baseUrl, string realm, string keycloakUserID, string accessToken)
		{
			// a deleted user is a final state, not an error, so it never stalls the sync
			using var probe = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/admin/realms/{realm}/users/{Uri.EscapeDataString(keycloakUserID)}");
			probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
			using (HttpResponseMessage exists = await s_http.SendAsync(probe))
			{
				if (exists.StatusCode == System.Net.HttpStatusCode.NotFound)
				{
					s_log.LogInformation("Keycloak user {KeycloakUserId} no longer exists; removing its upstream roles", keycloakUserID);
					return null;
				}

				exists.EnsureSuccessStatusCode();
			}

			using JsonDocument groups = await GetJson($"{baseUrl}/admin/realms/{realm}/users/{Uri.EscapeDataString(keycloakUserID)}/groups?briefRepresentation=true&max=1000", accessToken);
			return groups.RootElement.EnumerateArray()
				.Select(g => g.TryGetProperty("name", out JsonElement n) ? n.GetString() : null)
				.Where(n => !String.IsNullOrEmpty(n))
				.Select(n => n!)
				.ToHashSet(StringComparer.Ordinal);
		}
	}
}
