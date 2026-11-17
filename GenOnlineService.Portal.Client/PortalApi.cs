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

		public async Task<PortalReportPage> GetReportsAsync(string status, string? search, bool newestFirst, int start, int count, CancellationToken cancellationToken)
		{
			string query = $"{PortalRoutes.ReportSearch}?status={Uri.EscapeDataString(status)}&{PageOf(start, count)}&sort={(newestFirst ? "desc" : "asc")}";
			if (!String.IsNullOrWhiteSpace(search))
			{
				query += "&search=" + Uri.EscapeDataString(search);
			}
			(PortalAnswer<PortalReportPage> answer, int total) = await GetPageAsync<PortalReportPage>(query, cancellationToken);
			return answer.Value == null ? new PortalReportPage() : answer.Value with { Total = total };
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

		// reviewed is false, true or all
		public async Task<PortalAnswer<PortalAntiCheatPage>> GetAntiCheatAsync(string kind, string reviewed, int start, int count, CancellationToken cancellationToken)
		{
			(PortalAnswer<PortalAntiCheatPage> answer, int total) = await GetPageAsync<PortalAntiCheatPage>($"{PortalRoutes.AntiCheat}/{Uri.EscapeDataString(kind)}?reviewed={reviewed}&{PageOf(start, count)}", cancellationToken);
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
			return SendForAsync<Dictionary<string, string?>, PortalSettings>(HttpMethod.Put, PortalRoutes.Settings, body);
		}

		// Goes to every connected player at once
		public Task<PortalAnswer<PortalAnnouncementResult>> SendAnnouncementAsync(string message)
		{
			return SendForAsync<PortalAnnouncementRequest, PortalAnnouncementResult>(HttpMethod.Post, PortalRoutes.Announcements, new PortalAnnouncementRequest { Message = message });
		}

		// One local role, under the host's rank rule; each change is in the audit log
		public Task<PortalOutcome> GrantRoleAsync(long userID, string role) => SendAsync(HttpMethod.Put, $"{PortalRoutes.Players}/{userID}/roles/{Uri.EscapeDataString(role)}", new { });

		public Task<PortalOutcome> RevokeRoleAsync(long userID, string role) => SendAsync(HttpMethod.Delete, $"{PortalRoutes.Players}/{userID}/roles/{Uri.EscapeDataString(role)}", new { });

		// Newest first; afterID continues below that row. A player id filters the actor or the target, which is then a player
		public Task<PortalAnswer<PortalAuditPage>> GetAuditAsync(long? actorID, long? targetID, string? action, long? afterID, int perPage, CancellationToken cancellationToken)
		{
			var query = new List<string> { "per_page=" + Math.Clamp(perPage, 1, MaximumPerPage) };
			if (actorID is long actor)
			{
				query.Add("actor_id=" + actor);
			}
			if (targetID is long target)
			{
				query.Add("target_id=" + target);
				query.Add("target_kind=user");
			}
			if (!String.IsNullOrEmpty(action))
			{
				query.Add("action=" + Uri.EscapeDataString(action));
			}
			if (afterID is long after)
			{
				query.Add("after_id=" + after);
			}
			return GetAsync<PortalAuditPage>(PortalRoutes.AuditEvents + "?" + String.Join("&", query), cancellationToken);
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

		// Every change carries the antiforgery token of the host page
		private async Task<PortalOutcome> SendAsync<T>(HttpMethod method, string path, T body)
		{
			(PortalOutcome outcome, HttpResponseMessage? response) = await SendRequestAsync(method, path, body);
			response?.Dispose();
			return outcome;
		}

		// A change whose answer has a body, read on success
		private async Task<PortalAnswer<TAnswer>> SendForAsync<T, TAnswer>(HttpMethod method, string path, T body) where TAnswer : class
		{
			(PortalOutcome outcome, HttpResponseMessage? response) = await SendRequestAsync(method, path, body);
			using (response)
			{
				return outcome.Ok && response != null
					? new PortalAnswer<TAnswer>(await response.Content.ReadFromJsonAsync<TAnswer>(PortalJson.Options), null)
					: new PortalAnswer<TAnswer>(null, outcome.Error);
			}
		}

		// The response comes back on success only, for the caller to read and dispose
		private async Task<(PortalOutcome Outcome, HttpResponseMessage? Response)> SendRequestAsync<T>(HttpMethod method, string path, T body)
		{
			PortalSessionInfo? session = await LoadSessionAsync();
			if (session == null)
			{
				return (new PortalOutcome(false, "unauthorized"), null);
			}

			using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: PortalJson.Options) };
			request.Headers.Add(session.CsrfHeader, session.CsrfToken);

			HttpResponseMessage response = await m_http.SendAsync(request);
			if (response.IsSuccessStatusCode)
			{
				return (PortalOutcome.Success, response);
			}

			using (response)
			{
				if (response.StatusCode == HttpStatusCode.Unauthorized)
				{
					SignIn();
					return (new PortalOutcome(false, "unauthorized"), null);
				}

				return (new PortalOutcome(false, await ErrorOfAsync(response)), null);
			}
		}

		private static async Task<string> ErrorOfAsync(HttpResponseMessage response)
		{
			string code = ((int)response.StatusCode).ToString();
			try
			{
				PortalError? error = await response.Content.ReadFromJsonAsync<PortalError>(PortalJson.Options);
				if (!String.IsNullOrEmpty(error?.Error))
				{
					code = error.Error;
				}
			}
			catch (JsonException)
			{
				// a body that is not the error shape leaves the status code as the answer
			}
			return code;
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
