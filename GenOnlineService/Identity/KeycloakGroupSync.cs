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
	// Applies Keycloak group membership changes between sign-ins by polling the realm's admin events
	public static class KeycloakGroupSync
	{
		private static readonly ILogger s_log = AppLog.For(typeof(KeycloakGroupSync));
		private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(15) };
		private const int PageSize = 100;

		// events are read again this far behind the cursor, so one committed late is not missed; applying is idempotent
		private const long LookbackMs = 60_000;

		private static string CursorKey(UpstreamProviderSettings provider) => $"group_sync.{provider.Name}.cursor";

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

		// Unreachable Keycloak throws: the caller logs it, nothing changes and the cursor stays for the next attempt
		public static async Task Run(AppDbContext db, UpstreamProviderSettings provider)
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
					bool bBanned = provider.BanGroup != null && await db.Bans.AnyAsync(b => b.UserID == userID.Value && b.Source == provider.Source
						&& b.Restriction == BanRestrictions.Play && b.RevokedAt == null && (b.ExpiresAt == null || b.ExpiresAt > DateTime.UtcNow));
					if (bBanned)
					{
						groups.Add(provider.BanGroup!);
					}
				}

				Database.Upstream.Changes changes = await UpstreamAccounts.ApplyTrustedClaims(db, provider, userID.Value, groups);

				if (changes.RolesRemoved.Count > 0)
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
