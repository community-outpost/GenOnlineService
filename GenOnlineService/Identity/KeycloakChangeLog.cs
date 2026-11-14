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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GenOnlineService.Identity
{
	// One change log entry: full state, never a delta (keycloak-changes-contract.md)
	public sealed record ChangeLogEntry(long Seq, long PlayerID, string Kind, string? Name, string? Tag, string? Actor);

	// Follows the GenOnline Keycloak plugin's change log (identity design 5b): pushes apply at once, a gap or the
	// minute check pulls, and a log pruned past last_seq means a full resync of every player's name and tag
	public sealed class KeycloakChangeLog
	{
		public const string KindName = "name";
		public const int MaxSkewSeconds = 300;
		public const int MaxPushBytes = 64 * 1024;
		private const int PullLimit = 1000;
		private const int ResyncLimit = 5000;

		private static readonly ILogger s_log = AppLog.For(typeof(KeycloakChangeLog));
		private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(15) };

		private readonly UpstreamProviderSettings m_provider;
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		// one apply, pull or resync at a time on this copy; the locked last_seq row orders the copies among themselves
		private readonly SemaphoreSlim m_gate = new(1, 1);
		private string? m_token;
		private DateTime m_tokenExpires;

		public KeycloakChangeLog(UpstreamProviderSettings provider, IDbContextFactory<AppDbContext> dbFactory)
		{
			m_provider = provider;
			m_dbFactory = dbFactory;
		}

		public string LastSeqKey => $"keycloak.{m_provider.Name}.change_log.last_seq";

		private string BaseUrl
		{
			get
			{
				if (!String.IsNullOrWhiteSpace(m_provider.ChangeLog.BaseUrl))
				{
					return m_provider.ChangeLog.BaseUrl.TrimEnd('/');
				}

				int index = m_provider.Issuer.IndexOf("/realms/", StringComparison.Ordinal);
				return index < 0 ? m_provider.Issuer.TrimEnd('/') : m_provider.Issuer.Substring(0, index);
			}
		}

		private string Realm
		{
			get
			{
				if (!String.IsNullOrWhiteSpace(m_provider.ChangeLog.Realm))
				{
					return m_provider.ChangeLog.Realm;
				}

				int index = m_provider.Issuer.IndexOf("/realms/", StringComparison.Ordinal);
				return index < 0 ? String.Empty : m_provider.Issuer.Substring(index + "/realms/".Length).Trim('/');
			}
		}

		private string LogUrl => $"{BaseUrl}/realms/{Uri.EscapeDataString(Realm)}/genonline";

		// sha256=<lowercase hex HMAC-SHA256(secret, timestamp + "." + raw body)>, timestamp within MaxSkewSeconds
		public bool VerifyPush(string? timestamp, string? signature, byte[] body, DateTimeOffset now)
		{
			if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
				|| Math.Abs(now.ToUnixTimeSeconds() - seconds) > MaxSkewSeconds
				|| signature == null || !signature.StartsWith("sha256=", StringComparison.Ordinal) || signature.Length != 7 + 64)
			{
				return false;
			}

			byte[] given;
			try
			{
				given = Convert.FromHexString(signature.AsSpan(7));
			}
			catch (FormatException)
			{
				return false;
			}

			byte[] prefix = Encoding.ASCII.GetBytes(timestamp + ".");
			using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(m_provider.ChangeLog.PushSecret));
			hmac.TransformBlock(prefix, 0, prefix.Length, null, 0);
			hmac.TransformFinalBlock(body, 0, body.Length);
			return CryptographicOperations.FixedTimeEquals(given, hmac.Hash!);
		}

		// Null with the offending field when the entry is malformed
		public static ChangeLogEntry? ParseEntry(JsonElement e, out string? field)
		{
			field = null;
			if (e.ValueKind != JsonValueKind.Object)
			{
				field = "entry";
				return null;
			}

			if (!e.TryGetProperty("seq", out JsonElement seq) || seq.ValueKind != JsonValueKind.Number || !seq.TryGetInt64(out long seqValue) || seqValue <= 0)
			{
				field = "seq";
				return null;
			}

			if (!e.TryGetProperty("player_id", out JsonElement player) || player.ValueKind != JsonValueKind.Number || !player.TryGetInt64(out long playerID) || playerID <= 0)
			{
				field = "player_id";
				return null;
			}

			if (!e.TryGetProperty("kind", out JsonElement kind) || kind.ValueKind != JsonValueKind.String)
			{
				field = "kind";
				return null;
			}

			string? name = null, tag = null;
			if (kind.GetString() == KindName)
			{
				if (!e.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object
					|| !payload.TryGetProperty("name", out JsonElement n) || n.ValueKind != JsonValueKind.String
					|| !payload.TryGetProperty("tag", out JsonElement t) || t.ValueKind != JsonValueKind.String)
				{
					field = "payload";
					return null;
				}

				name = n.GetString();
				tag = t.GetString();
			}

			string? actor = e.TryGetProperty("actor", out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
			return new ChangeLogEntry(seqValue, playerID, kind.GetString()!, name, tag, actor);
		}

		// A push: applied when it is next, ignored when already applied, and a gap starts a pull in the background
		public async Task Receive(ChangeLogEntry entry)
		{
			bool gap;
			await m_gate.WaitAsync();
			try
			{
				(_, gap) = await ApplyInOrder(new[] { entry });
			}
			finally
			{
				m_gate.Release();
			}

			if (gap)
			{
				s_log.LogInformation("Change log push {Seq} arrived after a gap; pulling", entry.Seq);
				_ = Task.Run(async () =>
				{
					try
					{
						await Sync();
					}
					catch (Exception ex)
					{
						s_log.LogWarning(ex, "Change log pull after a gap failed; the minute check retries");
					}
				});
			}
		}

		// The minute check and the startup catch-up: pull when the log is ahead, resync when it no longer reaches last_seq
		public async Task Sync()
		{
			await m_gate.WaitAsync();
			try
			{
				long last = await ReadLastSeq();
				JsonElement latest = await Get($"{LogUrl}/changes/latest");
				long latestSeq = latest.GetProperty("latest_seq").GetInt64();
				long oldestSeq = latest.GetProperty("oldest_seq").GetInt64();

				if (latestSeq < last)
				{
					s_log.LogWarning("The Keycloak change log ends at {Latest}, behind this environment's {Last}; nothing to apply", latestSeq, last);
					return;
				}

				if (latestSeq > last)
				{
					await PullFrom(last, oldestSeq);
				}
			}
			finally
			{
				m_gate.Release();
			}
		}

		private async Task PullFrom(long last, long oldestSeq)
		{
			while (true)
			{
				if (last + 1 < oldestSeq)
				{
					await Resync();
					return;
				}

				(HttpStatusCode status, JsonElement page) = await GetWithStatus($"{LogUrl}/changes?after={last.ToString(CultureInfo.InvariantCulture)}&limit={PullLimit}");
				if (status == HttpStatusCode.Gone)
				{
					await Resync();
					return;
				}

				List<ChangeLogEntry> entries = new();
				foreach (JsonElement item in page.GetProperty("entries").EnumerateArray())
				{
					ChangeLogEntry entry = ParseEntry(item, out string? field)
						?? throw new InvalidOperationException($"The Keycloak change log answered a malformed entry ({field}) after {last}");
					entries.Add(entry);
				}

				(long applied, bool gap) = await ApplyInOrder(entries);
				if (gap)
				{
					// the plugin never shows a hole; stop rather than loop, the next check tries again
					throw new InvalidOperationException($"The Keycloak change log skipped a seq after {applied}");
				}

				bool more = page.TryGetProperty("more", out JsonElement m) && m.ValueKind == JsonValueKind.True;
				if (!more || entries.Count == 0 || applied == last)
				{
					return;
				}

				last = applied;
			}
		}

		// Every player's current name and tag, then the changes after the first page's as_of_seq
		private async Task Resync()
		{
			s_log.LogWarning("The Keycloak change log no longer reaches this environment's last_seq; resyncing every name");
			long? asOf = null;
			long afterID = 0;
			int pages = 0;

			while (true)
			{
				JsonElement page = await Get($"{LogUrl}/players?names&after_id={afterID.ToString(CultureInfo.InvariantCulture)}&limit={ResyncLimit}");
				asOf ??= page.GetProperty("as_of_seq").GetInt64();
				pages++;

				var players = page.GetProperty("players").EnumerateArray()
					.Select(p => (ID: p.GetProperty("player_id").GetInt64(), Name: p.GetProperty("name").GetString(), Tag: p.GetProperty("tag").GetString()))
					.ToList();
				await ApplyPlayers(players);

				JsonElement next = page.GetProperty("next_after_id");
				if (next.ValueKind == JsonValueKind.Null || players.Count == 0)
				{
					break;
				}

				afterID = next.GetInt64();
			}

			// names newer than a page were read before it is applied again here, so last_seq goes back to as_of_seq and the pull repeats them
			await WithLockedLastSeq(async (db, row) =>
			{
				SetValue(row, asOf!.Value);
				await Task.CompletedTask;
				return Array.Empty<NameChange>();
			});
			s_log.LogInformation("Resynced {Pages} page(s) of names as of seq {Seq}", pages, asOf);

			JsonElement latest = await Get($"{LogUrl}/changes/latest");
			await PullFromAfterResync(asOf!.Value, latest.GetProperty("oldest_seq").GetInt64());
		}

		// A log pruned again during the resync would loop; one resync per check is enough
		private async Task PullFromAfterResync(long last, long oldestSeq)
		{
			if (last + 1 < oldestSeq)
			{
				s_log.LogWarning("The Keycloak change log was pruned past {Seq} during the resync; the next check continues", last);
				return;
			}

			await PullFrom(last, oldestSeq);
		}

		private async Task ApplyPlayers(IReadOnlyList<(long ID, string? Name, string? Tag)> players)
		{
			await WithLockedLastSeq(async (db, row) =>
			{
				List<NameChange> changes = new();
				foreach ((long id, string? name, string? tag) in players)
				{
					changes.AddRange(await Database.DisplayNames.ApplyUpstreamInTransaction(db, id, name, tag, m_provider.Source));
				}
				return changes;
			});
		}

		// Applies the entries that continue last_seq in one transaction; returns the new last_seq and whether a gap stopped it
		private async Task<(long LastSeq, bool Gap)> ApplyInOrder(IReadOnlyList<ChangeLogEntry> entries)
		{
			long last = 0;
			bool gap = false;
			await WithLockedLastSeq(async (db, row) =>
			{
				List<NameChange> changes = new();
				last = ValueOf(row);
				gap = false;
				foreach (ChangeLogEntry entry in entries.OrderBy(e => e.Seq))
				{
					if (entry.Seq <= last)
					{
						continue;
					}

					if (entry.Seq != last + 1)
					{
						gap = true;
						break;
					}

					// unknown kinds still advance last_seq, so a newer plugin never stalls an older environment
					if (entry.Kind == KindName)
					{
						changes.AddRange(await Database.DisplayNames.ApplyUpstreamInTransaction(db, entry.PlayerID, entry.Name, entry.Tag, ChangedBy(entry)));
					}

					last = entry.Seq;
				}

				SetValue(row, last);
				return changes;
			});
			return (last, gap);
		}

		private string ChangedBy(ChangeLogEntry entry)
		{
			string changedBy = String.IsNullOrEmpty(entry.Actor) ? m_provider.Source : $"{m_provider.Source}:{entry.Actor}";
			return changedBy.Length <= 64 ? changedBy : changedBy.Substring(0, 64);
		}

		// Runs work inside one transaction holding the last_seq row lock, so two copies never apply out of order; publishes after commit
		private async Task WithLockedLastSeq(Func<AppDbContext, ApplicationSetting, Task<IReadOnlyList<NameChange>>> work)
		{
			for (int attempt = 1; ; attempt++)
			{
				await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
				await EnsureRow(db);
				try
				{
					await using var tx = await db.Database.BeginTransactionAsync();
					string sql = db.Database.IsNpgsql()
						? "SELECT * FROM application_settings WHERE \"key\" = {0} FOR UPDATE"
						: "SELECT * FROM application_settings WHERE `key` = {0} FOR UPDATE";
					ApplicationSetting row = await db.ApplicationSettings.FromSqlRaw(sql, LastSeqKey).AsTracking().SingleAsync();

					IReadOnlyList<NameChange> changes = await work(db, row);
					row.UpdatedBy = m_provider.Source;
					row.UpdatedAt = DateTime.UtcNow;
					await db.SaveChangesAsync();
					await tx.CommitAsync();

					RealtimeNames.Publish(changes);
					return;
				}
				catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex) && attempt < 3)
				{
					// a sign-in moved a name at the same moment; read everything again
				}
			}
		}

		private async Task EnsureRow(AppDbContext db)
		{
			if (await Database.Upstream.GetSetting(db, LastSeqKey) != null)
			{
				return;
			}

			try
			{
				await Database.Upstream.SetSetting(db, LastSeqKey, "0", m_provider.Source);
			}
			catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex))
			{
				// another copy created it first
			}
			db.ChangeTracker.Clear();
		}

		public async Task<long> ReadLastSeq()
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			string? value = await Database.Upstream.GetSetting(db, LastSeqKey);
			return value == null ? 0 : ParseValue(value);
		}

		private static long ValueOf(ApplicationSetting row) => ParseValue(row.Value);

		private static long ParseValue(string value) => long.Parse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture);

		private static void SetValue(ApplicationSetting row, long seq) => row.Value = seq.ToString(CultureInfo.InvariantCulture);

		private async Task<JsonElement> Get(string url)
		{
			(HttpStatusCode status, JsonElement body) = await GetWithStatus(url);
			if (status == HttpStatusCode.Gone)
			{
				throw new InvalidOperationException($"The Keycloak change log answered 410 for {url}");
			}
			return body;
		}

		// 200 and 410 come back; anything else throws, and a 401 forgets the cached token first
		private async Task<(HttpStatusCode, JsonElement)> GetWithStatus(string url)
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, url);
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken());
			using HttpResponseMessage response = await s_http.SendAsync(request);

			if (response.StatusCode == HttpStatusCode.Unauthorized)
			{
				m_token = null;
			}

			if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.Gone)
			{
				// the caller logs it and the next check retries; the plugin's error code says whose bug it is
				string error = String.Empty;
				try
				{
					using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
					error = body.RootElement.TryGetProperty("error", out JsonElement code) && code.ValueKind == JsonValueKind.String ? $" ({code.GetString()})" : String.Empty;
				}
				catch (JsonException)
				{
				}

				throw new HttpRequestException($"The Keycloak change log answered {(int)response.StatusCode}{error} for {url}", null, response.StatusCode);
			}

			using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
			return (response.StatusCode, doc.RootElement.Clone());
		}

		// Client credentials of the genonline-sync client, cached until shortly before they expire
		private async Task<string> AccessToken()
		{
			if (m_token != null && DateTime.UtcNow < m_tokenExpires)
			{
				return m_token;
			}

			using var content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["grant_type"] = "client_credentials",
				["client_id"] = m_provider.ChangeLog.ClientID,
				["client_secret"] = m_provider.ChangeLog.ClientSecret
			});
			using HttpResponseMessage response = await s_http.PostAsync($"{BaseUrl}/realms/{Uri.EscapeDataString(Realm)}/protocol/openid-connect/token", content);
			if (!response.IsSuccessStatusCode)
			{
				throw new HttpRequestException($"The change log client could not get a token: {(int)response.StatusCode}", null, response.StatusCode);
			}

			using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
			int expiresIn = doc.RootElement.TryGetProperty("expires_in", out JsonElement e) && e.TryGetInt32(out int s) ? s : 60;
			m_token = doc.RootElement.GetProperty("access_token").GetString();
			m_tokenExpires = DateTime.UtcNow.AddSeconds(Math.Max(5, expiresIn - 30));
			return m_token!;
		}
	}

	// The plugin's pushes; internal only: the edge proxy never routes /-/keycloak, and every push carries the environment's HMAC
	[ApiController]
	[Route("-/keycloak/changes")]
	[AllowAnonymous]
	[IgnoreAntiforgeryToken]
	[RequestSizeLimit(KeycloakChangeLog.MaxPushBytes)]
	public sealed class KeycloakChangesController : ControllerBase
	{
		private static readonly ILogger s_log = AppLog.For(typeof(KeycloakChangesController));

		[HttpPost]
		public async Task<IActionResult> Post()
		{
			KeycloakChangeLog? log = HttpContext.RequestServices.GetService<KeycloakChangeLog>();
			if (log == null)
			{
				return NotFound();
			}

			if (Request.ContentLength > KeycloakChangeLog.MaxPushBytes)
			{
				return BadRequest(new { error = "invalid_argument", @params = new { field = "body" } });
			}

			using var buffer = new MemoryStream();
			await Request.Body.CopyToAsync(buffer);
			if (buffer.Length > KeycloakChangeLog.MaxPushBytes)
			{
				return BadRequest(new { error = "invalid_argument", @params = new { field = "body" } });
			}

			byte[] body = buffer.ToArray();
			if (!log.VerifyPush(Request.Headers["X-GenOnline-Timestamp"], Request.Headers["X-GenOnline-Signature"], body, DateTimeOffset.UtcNow))
			{
				s_log.LogWarning("Refused a change log push from {Address}: bad signature or timestamp", HttpContext.Connection.RemoteIpAddress);
				return Unauthorized(UniformAuthResponses.UnauthorizedBody);
			}

			ChangeLogEntry? entry;
			string? field;
			try
			{
				using JsonDocument doc = JsonDocument.Parse(body);
				entry = KeycloakChangeLog.ParseEntry(doc.RootElement, out field);
			}
			catch (JsonException)
			{
				entry = null;
				field = "body";
			}

			if (entry == null)
			{
				return BadRequest(new { error = "invalid_argument", @params = new { field } });
			}

			await log.Receive(entry);
			return NoContent();
		}
	}
}
