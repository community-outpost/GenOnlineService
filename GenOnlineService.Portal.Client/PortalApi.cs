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

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlazorBlueprint.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace GenOnlineService.Portal.Client
{
	// The outcome of a call that changes something, with the error code when the host refused it
	public readonly record struct PortalOutcome(bool Ok, string? Error)
	{
		public static readonly PortalOutcome Success = new PortalOutcome(true, null);
	}

	// The answer of a read: the value, or the error code when the host refused it
	public readonly record struct PortalAnswer<T>(T? Value, string? Error) where T : class
	{
		public bool Ok => Error == null && Value != null;
	}

	// The outcome of a game config change: the saved value, or the error code and the field the host refused
	public readonly record struct PortalFlagOutcome<T>(T? Value, string? Error, string? Field) where T : class
	{
		public bool Ok => Error == null && Value != null;
	}

	// Calls /api/v1 with the sign-in cookie the browser already holds, the browser never sees a token. Who is signed in
	// and the antiforgery token come with the host page, in its <meta> tags (PortalDocument)
	public sealed class PortalApi
	{
		private const string SessionMeta = "go-portal-session";
		private const string CsrfHeaderMeta = "csrf-header";
		private const string CsrfTokenMeta = "csrf-token";

		// /api/v1 pages hold at most this many rows
		private const int MaximumPerPage = 100;

		private readonly HttpClient m_http;
		private readonly NavigationManager m_navigation;
		private readonly IJSRuntime m_js;
		private PortalSessionInfo? m_session;

		public PortalApi(HttpClient http, NavigationManager navigation, IJSRuntime js)
		{
			m_http = http;
			m_navigation = navigation;
			m_js = js;
		}

		public PortalSessionInfo? Session => m_session;

		public event Action? SessionChanged;

		// Null when the page carries no session, the browser then leaves for the sign-in page
		public async Task<PortalSessionInfo?> LoadSessionAsync()
		{
			if (m_session != null)
			{
				return m_session;
			}

			string? json = await MetaAsync(SessionMeta);
			PortalSessionInfo? session = String.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<PortalSessionInfo>(json, PortalJson.Options);
			if (session == null)
			{
				SignIn();
				return null;
			}

			m_session = session with { CsrfHeader = await MetaAsync(CsrfHeaderMeta) ?? "", CsrfToken = await MetaAsync(CsrfTokenMeta) ?? "" };
			SessionChanged?.Invoke();
			return m_session;
		}

		public Task<PortalAnswer<PortalOverview>> GetOverviewAsync(CancellationToken cancellationToken = default) => GetAsync<PortalOverview>(PortalRoutes.Overview, cancellationToken);

		// The queue, or with reportedID / reporterID the reports against or by one player; a refusal comes back as its error code
		public async Task<PortalAnswer<PortalReportPage>> GetReportsAsync(string status, string? search, bool newestFirst, int start, int count, CancellationToken cancellationToken, long? reportedID = null, long? reporterID = null)
		{
			string query = $"{PortalRoutes.ReportSearch}?status={Uri.EscapeDataString(status)}&{PageOf(start, count)}&sort={(newestFirst ? "desc" : "asc")}";
			if (!String.IsNullOrWhiteSpace(search))
			{
				query += "&search=" + Uri.EscapeDataString(search);
			}
			if (reportedID is long reported)
			{
				query += "&reported_id=" + reported;
			}
			if (reporterID is long reporter)
			{
				query += "&reporter_id=" + reporter;
			}
			(PortalAnswer<PortalReportPage> answer, int total) = await GetPageAsync<PortalReportPage>(query, cancellationToken);
			return answer.Value == null ? answer : answer with { Value = answer.Value with { Total = total } };
		}

		public Task<PortalOutcome> ResolveReportAsync(long reportID, string status, string? note)
		{
			return SendAsync(HttpMethod.Patch, $"{PortalRoutes.Reports}/{reportID}", new PortalResolveRequest { Status = status, ResolutionNote = note });
		}

		public async Task<PortalAnswer<PortalPlayerPage>> SearchPlayersAsync(string search, int start, int count, CancellationToken cancellationToken)
		{
			(PortalAnswer<PortalPlayerPage> answer, int total) = await GetPageAsync<PortalPlayerPage>($"{PortalRoutes.PlayerSearch}?query={Uri.EscapeDataString(search)}&{PageOf(start, count)}", cancellationToken);
			return answer.Value == null ? answer : answer with { Value = answer.Value with { Total = total } };
		}

		// The player list: every account without a search, filtered and sorted by the host
		public async Task<PortalAnswer<PortalPlayerPage>> ListPlayersAsync(PortalPlayerQuery query, int start, int count, CancellationToken cancellationToken)
		{
			string arguments = query.ToApiQuery();
			(PortalAnswer<PortalPlayerPage> answer, int total) = await GetPageAsync<PortalPlayerPage>($"{PortalRoutes.PlayerSearch}?{arguments}{(arguments.Length > 0 ? "&" : "")}{PageOf(start, count)}", cancellationToken);
			return answer.Value == null ? answer : answer with { Value = answer.Value with { Total = total } };
		}

		public Task<PortalAnswer<PortalPlayer>> GetPlayerAsync(long userID) => GetAsync<PortalPlayer>($"{PortalRoutes.Players}/{userID}/staff_view");

		// The host writes an audit row for this read
		public Task<PortalAnswer<PortalInspection>> GetInspectionAsync(long userID) => GetAsync<PortalInspection>($"{PortalRoutes.Players}/{userID}/inspection");

		public Task<PortalAnswer<PortalMatch>> GetMatchAsync(long matchID) => GetAsync<PortalMatch>($"{PortalRoutes.Matches}/{matchID}");

		public Task<PortalAnswer<PortalRealtime>> GetRealtimeAsync(CancellationToken cancellationToken = default) => GetAsync<PortalRealtime>(PortalRoutes.StaffRealtime, cancellationToken);

		public Task<PortalAnswer<PortalLobbyList>> GetLobbiesAsync(CancellationToken cancellationToken = default) => GetAsync<PortalLobbyList>(PortalRoutes.StaffLobbies, cancellationToken);

		// The host writes an audit row for these reads, once an hour per staff member and player or lobby
		public Task<PortalAnswer<PortalConnectionReports>> GetPlayerConnectionsAsync(long userID) => GetAsync<PortalConnectionReports>($"{PortalRoutes.Players}/{userID}/connection_reports");

		public Task<PortalAnswer<PortalConnectionReports>> GetLobbyConnectionsAsync(long lobbyID) => GetAsync<PortalConnectionReports>($"{PortalRoutes.Lobbies}/{lobbyID}/connection_reports");

		// Every account that used one identifier, value or the key of a device; the host writes an audit row for each search
		public async Task<PortalAnswer<PortalInvestigation>> InvestigateAsync(string kind, string? value, string? key, int page, int perPage, CancellationToken cancellationToken = default)
		{
			string what = key != null ? "key=" + Uri.EscapeDataString(key) : "value=" + Uri.EscapeDataString(value ?? "");
			using HttpResponseMessage response = await m_http.GetAsync($"{PortalRoutes.StaffInvestigate}?kind={Uri.EscapeDataString(kind)}&{what}&page={Math.Max(page, 1)}&per_page={Math.Clamp(perPage, 1, MaximumPerPage)}", cancellationToken);
			if (!response.IsSuccessStatusCode)
			{
				if (response.StatusCode == HttpStatusCode.Unauthorized)
				{
					SignIn();
					return new PortalAnswer<PortalInvestigation>(null, "unauthorized");
				}
				return new PortalAnswer<PortalInvestigation>(null, await ErrorOfAsync(response));
			}

			PortalInvestigation? found = await response.Content.ReadFromJsonAsync<PortalInvestigation>(PortalJson.Options, cancellationToken);
			bool hasNext = response.Headers.TryGetValues("X-Next-Page", out IEnumerable<string>? next) && !String.IsNullOrEmpty(next.FirstOrDefault());
			return new PortalAnswer<PortalInvestigation>(found == null ? null : found with { HasNext = hasNext }, null);
		}

		// The match list, newest first; past 10,000 rows the host does not count, the page then only knows whether one follows
		public async Task<PortalAnswer<PortalMatchPage>> ListMatchesAsync(PortalMatchQuery query, int start, int count, CancellationToken cancellationToken)
		{
			string arguments = query.ToApiQuery();
			using HttpResponseMessage response = await m_http.GetAsync($"{PortalRoutes.MatchSearch}?{arguments}{(arguments.Length > 0 ? "&" : "")}{PageOf(start, count)}", cancellationToken);
			if (!response.IsSuccessStatusCode)
			{
				if (response.StatusCode == HttpStatusCode.Unauthorized)
				{
					SignIn();
					return new PortalAnswer<PortalMatchPage>(null, "unauthorized");
				}
				return new PortalAnswer<PortalMatchPage>(null, await ErrorOfAsync(response));
			}

			PortalMatchPage? page = await response.Content.ReadFromJsonAsync<PortalMatchPage>(PortalJson.Options, cancellationToken);
			int? total = response.Headers.TryGetValues("X-Total", out IEnumerable<string>? totals) && int.TryParse(totals.FirstOrDefault(), out int value) ? value : null;
			bool hasNext = response.Headers.TryGetValues("X-Next-Page", out IEnumerable<string>? next) && !String.IsNullOrEmpty(next.FirstOrDefault());
			return new PortalAnswer<PortalMatchPage>(page == null ? null : page with { Total = total, HasNext = hasNext }, null);
		}

		// reviewed is false, true or all; userID keeps the rows of one player
		public async Task<PortalAnswer<PortalAntiCheatPage>> GetAntiCheatAsync(string kind, string reviewed, int start, int count, CancellationToken cancellationToken, long? userID = null)
		{
			string player = userID is long id ? "&user_id=" + id : "";
			(PortalAnswer<PortalAntiCheatPage> answer, int total) = await GetPageAsync<PortalAntiCheatPage>($"{PortalRoutes.AntiCheat}/{Uri.EscapeDataString(kind)}?reviewed={reviewed}{player}&{PageOf(start, count)}", cancellationToken);
			return answer.Value == null ? answer : answer with { Value = answer.Value with { Total = total } };
		}

		public Task<PortalOutcome> ReviewAsync(string kind, long reportID)
		{
			return SendAsync(HttpMethod.Patch, $"{PortalRoutes.AntiCheat}/{Uri.EscapeDataString(kind)}/{reportID}", new PortalReviewRequest { Reviewed = true });
		}

		public Task<PortalOutcome> CreateSanctionAsync(long userID, string type, long? durationS, string? reason)
		{
			return SendAsync(HttpMethod.Post, $"{PortalRoutes.Players}/{userID}/sanctions", new PortalSanctionRequest { Type = type, DurationS = durationS, Reason = reason });
		}

		public Task<PortalOutcome> LiftSanctionAsync(long sanctionID, string? reason)
		{
			return SendAsync(HttpMethod.Delete, $"{PortalRoutes.Sanctions}/{sanctionID}", new PortalReasonRequest { Reason = reason });
		}

		public Task<PortalOutcome> RenamePlayerAsync(long userID, string? name, string? reason)
		{
			return SendAsync(HttpMethod.Patch, $"{PortalRoutes.Players}/{userID}", new PortalRenameRequest { Name = name, Reason = reason });
		}

		// A placeholder replaces the name and the player picks a new one at the next sign-in, which the host forces
		public Task<PortalOutcome> ResetNameAsync(long userID, string? reason)
		{
			return SendAsync(HttpMethod.Post, $"{PortalRoutes.Players}/{userID}/reset_name", new PortalReasonRequest { Reason = reason });
		}

		// A new random password that the answer carries once; the reason is required
		public Task<PortalAnswer<PortalPasswordReset>> ResetPasswordAsync(long userID, string? reason)
		{
			return SendForAsync<PortalPasswordReset>(HttpMethod.Post, $"{PortalRoutes.Players}/{userID}/password_reset", new PortalReasonRequest { Reason = reason });
		}

		// Two-factor sign-in off, the authenticator and recovery codes removed; the reason is required
		public Task<PortalOutcome> ResetTwoFactorAsync(long userID, string? reason)
		{
			return SendAsync(HttpMethod.Post, $"{PortalRoutes.Players}/{userID}/two_factor_reset", new PortalReasonRequest { Reason = reason });
		}

		// A reported message, the id as the report names it
		public Task<PortalOutcome> DeleteMessageAsync(string messageID, string? reason)
		{
			return SendAsync(HttpMethod.Delete, $"{PortalRoutes.Messages}/{Uri.EscapeDataString(messageID)}", new PortalReasonRequest { Reason = reason });
		}

		public Task<PortalAnswer<PortalSettings>> GetSettingsAsync() => GetAsync<PortalSettings>(PortalRoutes.Settings);

		// An empty message of the day clears it; the answer is the settings as saved
		public Task<PortalAnswer<PortalSettings>> SaveSettingsAsync(string? motd)
		{
			// an explicit null clears the row, a dictionary writes it where the options would leave a null property out
			var body = new Dictionary<string, string?> { ["motd"] = String.IsNullOrEmpty(motd) ? null : motd };
			return SendForAsync<PortalSettings>(HttpMethod.Put, PortalRoutes.Settings, body);
		}

		// Goes to every connected player at once
		public Task<PortalAnswer<PortalAnnouncementResult>> SendAnnouncementAsync(string message)
		{
			return SendForAsync<PortalAnnouncementResult>(HttpMethod.Post, PortalRoutes.Announcements, new PortalAnnouncementRequest { Message = message });
		}

		// The words of the text rules, filtered on the host; the answer holds at most 1000 with the count of all
		public Task<PortalAnswer<PortalTextTerms>> GetTextTermsAsync(string? query, string? language, string? category, string? severity)
		{
			var filters = new List<string>();
			foreach ((string name, string? value) in new[] { ("query", query), ("language", language), ("category", category), ("severity", severity) })
			{
				if (!String.IsNullOrWhiteSpace(value))
				{
					filters.Add(name + "=" + Uri.EscapeDataString(value));
				}
			}
			return GetAsync<PortalTextTerms>($"{PortalRoutes.TextRules}/terms" + (filters.Count == 0 ? "" : "?" + String.Join("&", filters)));
		}

		public Task<PortalAnswer<PortalTextTerm>> AddTextTermAsync(PortalTextTermRequest term) => SendForAsync<PortalTextTerm>(HttpMethod.Post, $"{PortalRoutes.TextRules}/terms", term);

		public Task<PortalAnswer<PortalTextTerm>> UpdateTextTermAsync(long id, PortalTextTermRequest change) => SendForAsync<PortalTextTerm>(HttpMethod.Patch, $"{PortalRoutes.TextRules}/terms/{id}", change);

		public Task<PortalOutcome> RemoveTextTermAsync(long id) => SendAsync(HttpMethod.Delete, $"{PortalRoutes.TextRules}/terms/{id}", new { });

		public Task<PortalAnswer<PortalTextExceptions>> GetTextExceptionsAsync() => GetAsync<PortalTextExceptions>($"{PortalRoutes.TextRules}/exceptions");

		public Task<PortalAnswer<PortalTextException>> AddTextExceptionAsync(string term, string language) =>
			SendForAsync<PortalTextException>(HttpMethod.Post, $"{PortalRoutes.TextRules}/exceptions", new { term, language });

		public Task<PortalOutcome> RemoveTextExceptionAsync(long id) => SendAsync(HttpMethod.Delete, $"{PortalRoutes.TextRules}/exceptions/{id}", new { });

		public Task<PortalAnswer<PortalTextWorkflows>> GetTextWorkflowsAsync() => GetAsync<PortalTextWorkflows>($"{PortalRoutes.TextRules}/workflows");

		// The host compiles and runs the rules before it stores them; a refusal brings the reason, such as the rule that fails
		public async Task<(PortalAnswer<PortalTextWorkflow> Answer, string? Reason)> SaveTextWorkflowAsync(string kind, PortalTextWorkflowDefinition definition)
		{
			PortalSessionInfo? session = await LoadSessionAsync();
			if (session == null)
			{
				return (new PortalAnswer<PortalTextWorkflow>(null, "unauthorized"), null);
			}

			using var request = new HttpRequestMessage(HttpMethod.Put, $"{PortalRoutes.TextRules}/workflows/{Uri.EscapeDataString(kind)}") { Content = JsonContent.Create(definition, options: PortalJson.Options) };
			request.Headers.Add(session.CsrfHeader, session.CsrfToken);
			using HttpResponseMessage response = await m_http.SendAsync(request);
			if (response.IsSuccessStatusCode)
			{
				return (new PortalAnswer<PortalTextWorkflow>(await response.Content.ReadFromJsonAsync<PortalTextWorkflow>(PortalJson.Options), null), null);
			}
			if (response.StatusCode == HttpStatusCode.Unauthorized)
			{
				SignIn();
				return (new PortalAnswer<PortalTextWorkflow>(null, "unauthorized"), null);
			}
			try
			{
				PortalReasonError? error = await response.Content.ReadFromJsonAsync<PortalReasonError>(PortalJson.Options);
				return (new PortalAnswer<PortalTextWorkflow>(null, String.IsNullOrEmpty(error?.Error) ? ((int)response.StatusCode).ToString() : error.Error), error?.Params?.Reason);
			}
			catch (JsonException)
			{
				return (new PortalAnswer<PortalTextWorkflow>(null, ((int)response.StatusCode).ToString()), null);
			}
		}

		// What the current rules decide for a text; nothing is stored
		public Task<PortalAnswer<PortalTextTest>> TestTextAsync(string kind, string text) =>
			SendForAsync<PortalTextTest>(HttpMethod.Post, $"{PortalRoutes.TextRules}/test", new { kind, text });

		// One local role, under the host's rank rule; each change is in the audit log
		public Task<PortalOutcome> GrantRoleAsync(long userID, string role) => SendAsync(HttpMethod.Put, $"{PortalRoutes.Players}/{userID}/roles/{Uri.EscapeDataString(role)}", new { });

		public Task<PortalOutcome> RevokeRoleAsync(long userID, string role) => SendAsync(HttpMethod.Delete, $"{PortalRoutes.Players}/{userID}/roles/{Uri.EscapeDataString(role)}", new { });

		// Newest first; afterID continues below that row. The actor and the target are players by id, Name#tag or name
		public Task<PortalAnswer<PortalAuditPage>> GetAuditAsync(PortalAuditQuery filter, long? afterID, int perPage, CancellationToken cancellationToken)
		{
			var query = new List<string> { "per_page=" + Math.Clamp(perPage, 1, MaximumPerPage) };
			string arguments = filter.ToApiQuery();
			if (arguments.Length > 0)
			{
				query.Add(arguments);
			}
			if (afterID is long after)
			{
				query.Add("after_id=" + after);
			}
			return GetAsync<PortalAuditPage>(PortalRoutes.AuditEvents + "?" + String.Join("&", query), cancellationToken);
		}

		// Every partner, by name; tens of accounts, read page by page
		public async Task<PortalAnswer<List<PortalPartner>>> ListPartnersAsync(CancellationToken cancellationToken = default)
		{
			var partners = new List<PortalPartner>();
			for (int page = 1; ; page++)
			{
				PortalAnswer<PortalPartnerPage> answer = await GetAsync<PortalPartnerPage>($"{PortalRoutes.ServiceAccounts}?page={page}&per_page={MaximumPerPage}", cancellationToken);
				if (!answer.Ok)
				{
					return new PortalAnswer<List<PortalPartner>>(null, answer.Error ?? "");
				}
				partners.AddRange(answer.Value!.ServiceAccounts);
				if (answer.Value.ServiceAccounts.Count < MaximumPerPage)
				{
					return new PortalAnswer<List<PortalPartner>>(partners, null);
				}
			}
		}

		public Task<PortalAnswer<PortalPartner>> GetPartnerAsync(long id, CancellationToken cancellationToken = default) => GetAsync<PortalPartner>($"{PortalRoutes.ServiceAccounts}/{id}", cancellationToken);

		// Every token of a partner, newest first, revoked and expired ones included
		public async Task<PortalAnswer<List<PortalPartnerToken>>> ListPartnerTokensAsync(long id, CancellationToken cancellationToken = default)
		{
			var tokens = new List<PortalPartnerToken>();
			for (int page = 1; ; page++)
			{
				PortalAnswer<PortalPartnerTokenPage> answer = await GetAsync<PortalPartnerTokenPage>($"{PortalRoutes.ServiceAccounts}/{id}/personal_access_tokens?page={page}&per_page={MaximumPerPage}", cancellationToken);
				if (!answer.Ok)
				{
					return new PortalAnswer<List<PortalPartnerToken>>(null, answer.Error ?? "");
				}
				tokens.AddRange(answer.Value!.PersonalAccessTokens);
				if (answer.Value.PersonalAccessTokens.Count < MaximumPerPage)
				{
					return new PortalAnswer<List<PortalPartnerToken>>(tokens, null);
				}
			}
		}

		public Task<PortalAnswer<PortalPartner>> CreatePartnerAsync(string name, string? description)
		{
			return SendForAsync<PortalPartner>(HttpMethod.Post, PortalRoutes.ServiceAccounts, new PortalCreatePartnerRequest { Name = name, Description = description });
		}

		// Grants, removals and the disable or enable in one change, each in the audit log
		public Task<PortalAnswer<PortalPartner>> UpdatePartnerAsync(long id, PortalUpdatePartnerRequest change)
		{
			return SendForAsync<PortalPartner>(HttpMethod.Patch, $"{PortalRoutes.ServiceAccounts}/{id}", change);
		}

		// The answer carries the token's secret, shown once
		public Task<PortalAnswer<PortalCreatedToken>> CreatePartnerTokenAsync(long id, PortalCreateTokenRequest token)
		{
			return SendForAsync<PortalCreatedToken>(HttpMethod.Post, $"{PortalRoutes.ServiceAccounts}/{id}/personal_access_tokens", token);
		}

		// Same scopes and allowed addresses; the old token keeps working for overlapMinutes, then stops
		public Task<PortalAnswer<PortalCreatedToken>> RotatePartnerTokenAsync(long id, long tokenID, int overlapMinutes)
		{
			return SendForAsync<PortalCreatedToken>(HttpMethod.Post, $"{PortalRoutes.ServiceAccounts}/{id}/personal_access_tokens/{tokenID}/rotate?overlap_minutes={overlapMinutes}", new { });
		}

		public Task<PortalOutcome> RevokePartnerTokenAsync(long id, long tokenID) => SendAsync(HttpMethod.Delete, $"{PortalRoutes.ServiceAccounts}/{id}/personal_access_tokens/{tokenID}", new { });

		// The game config draft, every field with its status against the live revision
		public Task<PortalAnswer<PortalFlagDraft>> GetFlagsAsync(CancellationToken cancellationToken = default) => GetAsync<PortalFlagDraft>(PortalRoutes.Flags, cancellationToken);

		// One field of the draft; version is the If-Match value, so a change made meanwhile is refused (conflict.stale)
		public Task<PortalFlagOutcome<PortalDraftFlag>> SaveFlagAsync(PortalFlag flag, int version)
		{
			return SendFlagAsync<PortalDraftFlag>(HttpMethod.Put, $"{PortalRoutes.Flags}/{Uri.EscapeDataString(flag.Key)}", flag, Quoted(version));
		}

		// The field leaves the next revision and can be restored
		public Task<PortalFlagOutcome<PortalDraftFlag>> ArchiveFlagAsync(string key, int version)
		{
			return SendFlagAsync<PortalDraftFlag>(HttpMethod.Delete, $"{PortalRoutes.Flags}/{Uri.EscapeDataString(key)}", new { }, Quoted(version));
		}

		public Task<PortalFlagOutcome<PortalDraftFlag>> RestoreFlagAsync(string key, int version)
		{
			return SendFlagAsync<PortalDraftFlag>(HttpMethod.Post, $"{PortalRoutes.Flags}/{Uri.EscapeDataString(key)}/restore", new { }, Quoted(version));
		}

		// What publishing the draft would change
		public Task<PortalAnswer<PortalFlagChanges>> GetDraftChangesAsync(CancellationToken cancellationToken = default) => GetAsync<PortalFlagChanges>(PortalRoutes.Flags + "/draft/changes", cancellationToken);

		// draftEtag is the draft the page showed, so a draft changed meanwhile is refused (conflict.stale)
		public Task<PortalFlagOutcome<PortalFlagRevision>> PublishFlagsAsync(string? comment, string draftEtag)
		{
			return SendFlagAsync<PortalFlagRevision>(HttpMethod.Post, PortalRoutes.Flags + "/draft/publish", new PortalFlagComment { Comment = comment }, draftEtag);
		}

		// The draft starts again from the live revision
		public Task<PortalFlagOutcome<PortalFlagDraft>> ResetFlagsAsync() => SendFlagAsync<PortalFlagDraft>(HttpMethod.Post, PortalRoutes.Flags + "/draft/reset", new { }, null);

		// A flagd file, such as an export, becomes the draft
		public Task<PortalFlagOutcome<PortalFlagDraft>> ImportFlagsAsync(JsonElement document) => SendFlagAsync<PortalFlagDraft>(HttpMethod.Post, PortalRoutes.Flags + "/draft/import", document, null);

		// Every revision, newest first; one per publish or rollback, read page by page
		public async Task<PortalAnswer<List<PortalFlagRevision>>> ListFlagRevisionsAsync(CancellationToken cancellationToken = default)
		{
			var revisions = new List<PortalFlagRevision>();
			for (int page = 1; ; page++)
			{
				PortalAnswer<List<PortalFlagRevision>> answer = await GetAsync<List<PortalFlagRevision>>($"{PortalRoutes.FlagRevisions}?page={page}&per_page={MaximumPerPage}", cancellationToken);
				if (!answer.Ok)
				{
					return new PortalAnswer<List<PortalFlagRevision>>(null, answer.Error ?? "");
				}
				revisions.AddRange(answer.Value!);
				if (answer.Value!.Count < MaximumPerPage)
				{
					return new PortalAnswer<List<PortalFlagRevision>>(revisions, null);
				}
			}
		}

		// One revision with its flagd file, the export
		public Task<PortalAnswer<PortalFlagRevision>> GetFlagRevisionAsync(int number) => GetAsync<PortalFlagRevision>($"{PortalRoutes.FlagRevisions}/{number}");

		// What one revision changed against the one before it
		public Task<PortalAnswer<PortalFlagChanges>> GetFlagRevisionChangesAsync(int number) => GetAsync<PortalFlagChanges>($"{PortalRoutes.FlagRevisions}/{number}/changes");

		// The revision goes live again as the next revision
		public Task<PortalFlagOutcome<PortalFlagRevision>> RollbackFlagsAsync(int number, string? comment)
		{
			return SendFlagAsync<PortalFlagRevision>(HttpMethod.Post, $"{PortalRoutes.FlagRevisions}/{number}/rollback", new PortalFlagComment { Comment = comment }, null);
		}

		// False when the browser refuses the clipboard, the value is then copied by hand
		public async Task<bool> CopyAsync(string text)
		{
			try
			{
				await m_js.InvokeVoidAsync("navigator.clipboard.writeText", text);
				return true;
			}
			catch (JSException)
			{
				return false;
			}
		}

		// The same POST as the sign-out form of the accounts pages, then the static sign-in page
		public async Task SignOutAsync()
		{
			using HttpResponseMessage response = await m_http.PostAsync(PortalRoutes.SignOut, null);
			m_navigation.NavigateTo(PortalRoutes.SignIn, forceLoad: true);
		}

		public void SignIn()
		{
			string returnUrl = "/" + m_navigation.ToBaseRelativePath(m_navigation.Uri);
			m_navigation.NavigateTo($"{PortalRoutes.SignIn}?ReturnUrl={Uri.EscapeDataString(returnUrl)}", forceLoad: true);
		}

		// window.goPortalMeta of the host page
		private ValueTask<string?> MetaAsync(string name) => m_js.InvokeAsync<string?>("goPortalMeta", name);

		// The grids ask for rows from a start index, /api/v1 pages by page and per_page
		private static string PageOf(int start, int count)
		{
			int perPage = Math.Clamp(count, 1, MaximumPerPage);
			return $"page={start / perPage + 1}&per_page={perPage}";
		}

		private async Task<PortalAnswer<T>> GetAsync<T>(string path, CancellationToken cancellationToken = default) where T : class
		{
			(PortalAnswer<T> answer, _) = await GetPageAsync<T>(path, cancellationToken);
			return answer;
		}

		// A read with the cookie: the body on success, otherwise the error code of the answer; a list also brings the
		// size of the whole filtered list in the X-Total header
		private async Task<(PortalAnswer<T> Answer, int Total)> GetPageAsync<T>(string path, CancellationToken cancellationToken) where T : class
		{
			using HttpResponseMessage response = await m_http.GetAsync(path, cancellationToken);
			if (response.IsSuccessStatusCode)
			{
				int total = response.Headers.TryGetValues("X-Total", out IEnumerable<string>? values) && int.TryParse(values.FirstOrDefault(), out int value) ? value : 0;
				return (new PortalAnswer<T>(await response.Content.ReadFromJsonAsync<T>(PortalJson.Options, cancellationToken), null), total);
			}

			if (response.StatusCode == HttpStatusCode.Unauthorized)
			{
				SignIn();
				return (new PortalAnswer<T>(null, "unauthorized"), 0);
			}
			return (new PortalAnswer<T>(null, await ErrorOfAsync(response)), 0);
		}

		private static string Quoted(int version) => "\"" + version.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"";

		// A game config change: the answer, or the error code with the field an invalid_argument names
		private async Task<PortalFlagOutcome<TResult>> SendFlagAsync<TResult>(HttpMethod method, string path, object body, string? ifMatch) where TResult : class
		{
			(HttpResponseMessage? response, PortalError? failure) = await SendRawAsync(method, path, body, ifMatch);
			using (response)
			{
				return failure != null ? new PortalFlagOutcome<TResult>(null, failure.Error, failure.Field) : new PortalFlagOutcome<TResult>(await response!.Content.ReadFromJsonAsync<TResult>(PortalJson.Options), null, null);
			}
		}

		// Every change carries the antiforgery token of the host page
		private async Task<PortalOutcome> SendAsync<T>(HttpMethod method, string path, T body)
		{
			(HttpResponseMessage? response, string? error) = await SendCoreAsync(method, path, body);
			using (response)
			{
				return error == null ? PortalOutcome.Success : new PortalOutcome(false, error);
			}
		}

		// A change whose answer the page reads, such as the created account or token
		private async Task<PortalAnswer<TResult>> SendForAsync<TResult>(HttpMethod method, string path, object body) where TResult : class
		{
			(HttpResponseMessage? response, string? error) = await SendCoreAsync(method, path, body);
			using (response)
			{
				return error != null ? new PortalAnswer<TResult>(null, error) : new PortalAnswer<TResult>(await response!.Content.ReadFromJsonAsync<TResult>(PortalJson.Options), null);
			}
		}

		// The answer on success, otherwise the error code; the caller disposes the answer
		private async Task<(HttpResponseMessage? Response, string? Error)> SendCoreAsync<T>(HttpMethod method, string path, T body)
		{
			(HttpResponseMessage? response, PortalError? failure) = await SendRawAsync(method, path, body, null);
			return (response, failure?.Error);
		}

		// The answer on success, otherwise the error with its details; ifMatch goes in the If-Match header when set
		private async Task<(HttpResponseMessage? Response, PortalError? Failure)> SendRawAsync<T>(HttpMethod method, string path, T body, string? ifMatch)
		{
			PortalSessionInfo? session = await LoadSessionAsync();
			if (session == null)
			{
				return (null, new PortalError { Error = "unauthorized" });
			}

			using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: PortalJson.Options) };
			request.Headers.Add(session.CsrfHeader, session.CsrfToken);
			if (ifMatch != null)
			{
				request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
			}

			HttpResponseMessage response = await m_http.SendAsync(request);
			if (response.IsSuccessStatusCode)
			{
				return (response, null);
			}

			using (response)
			{
				if (response.StatusCode == HttpStatusCode.Unauthorized)
				{
					SignIn();
					return (null, new PortalError { Error = "unauthorized" });
				}

				return (null, await FailureOfAsync(response));
			}
		}

		private static async Task<string> ErrorOfAsync(HttpResponseMessage response) => (await FailureOfAsync(response)).Error;

		// The error and its details, or the status code when the body is not the error shape
		private static async Task<PortalError> FailureOfAsync(HttpResponseMessage response)
		{
			var failure = new PortalError { Error = ((int)response.StatusCode).ToString() };
			try
			{
				PortalError? error = await response.Content.ReadFromJsonAsync<PortalError>(PortalJson.Options);
				if (!String.IsNullOrEmpty(error?.Error))
				{
					failure = error;
				}
			}
			catch (JsonException)
			{
				// a body that is not the error shape leaves the status code as the answer
			}
			return failure;
		}
	}

	public static class PortalClientSetup
	{
		public static void AddPortalClient(this IServiceCollection services, string baseAddress)
		{
			services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(baseAddress) });
			services.AddScoped<PortalApi>();
			services.AddBlazorBlueprintComponents();
		}
	}
}
