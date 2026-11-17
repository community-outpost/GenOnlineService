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
using BlazorBlueprint.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

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

	// Calls the host with the sign-in cookie the browser already holds, the browser never sees a token
	public sealed class PortalApi
	{
		private readonly HttpClient m_http;
		private readonly NavigationManager m_navigation;
		private PortalSessionInfo? m_session;

		public PortalApi(HttpClient http, NavigationManager navigation)
		{
			m_http = http;
			m_navigation = navigation;
		}

		public PortalSessionInfo? Session => m_session;

		public event Action? SessionChanged;

		// Null when nobody is signed in, the browser then leaves for the sign-in page
		public async Task<PortalSessionInfo?> LoadSessionAsync(bool reload = false)
		{
			if (m_session != null && !reload)
			{
				return m_session;
			}

			using HttpResponseMessage response = await m_http.GetAsync(PortalRoutes.Session);
			if (response.StatusCode == HttpStatusCode.Unauthorized)
			{
				SignIn();
				return null;
			}

			response.EnsureSuccessStatusCode();
			m_session = await response.Content.ReadFromJsonAsync<PortalSessionInfo>(PortalJson.Options);
			SessionChanged?.Invoke();
			return m_session;
		}

		public async Task<PortalReportPage> GetReportsAsync(string status, string? search, bool newestFirst, int start, int count, CancellationToken cancellationToken)
		{
			string query = $"{PortalRoutes.Reports}?status={Uri.EscapeDataString(status)}&start={start}&count={count}&newest={(newestFirst ? "true" : "false")}";
			if (!String.IsNullOrWhiteSpace(search))
			{
				query += "&search=" + Uri.EscapeDataString(search);
			}
			return await m_http.GetFromJsonAsync<PortalReportPage>(query, PortalJson.Options, cancellationToken) ?? new PortalReportPage();
		}

		public Task<PortalOutcome> ResolveReportAsync(long reportID, string status, string? note)
		{
			return SendAsync(HttpMethod.Patch, $"{PortalRoutes.Reports}/{reportID}", new PortalResolveRequest { Status = status, ResolutionNote = note });
		}

		public Task<PortalAnswer<PortalPlayerPage>> SearchPlayersAsync(string search, int start, int count, CancellationToken cancellationToken)
		{
			return GetAsync<PortalPlayerPage>($"{PortalRoutes.Players}?search={Uri.EscapeDataString(search)}&start={start}&count={count}", cancellationToken);
		}

		public Task<PortalAnswer<PortalPlayer>> GetPlayerAsync(long userID) => GetAsync<PortalPlayer>($"{PortalRoutes.Players}/{userID}");

		// The host writes an audit row for this read
		public Task<PortalAnswer<PortalInspection>> GetInspectionAsync(long userID) => GetAsync<PortalInspection>($"{PortalRoutes.Players}/{userID}/inspection");

		public Task<PortalAnswer<PortalMatch>> GetMatchAsync(long matchID) => GetAsync<PortalMatch>($"{PortalRoutes.Matches}/{matchID}");

		// reviewed is false, true or all
		public Task<PortalAnswer<PortalAntiCheatPage>> GetAntiCheatAsync(string kind, string reviewed, int start, int count, CancellationToken cancellationToken)
		{
			return GetAsync<PortalAntiCheatPage>($"{PortalRoutes.AntiCheat}/{Uri.EscapeDataString(kind)}?reviewed={reviewed}&start={start}&count={count}", cancellationToken);
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

		// A read with the cookie: the body on success, otherwise the error code of the answer
		private async Task<PortalAnswer<T>> GetAsync<T>(string path, CancellationToken cancellationToken = default) where T : class
		{
			using HttpResponseMessage response = await m_http.GetAsync(path, cancellationToken);
			if (response.IsSuccessStatusCode)
			{
				return new PortalAnswer<T>(await response.Content.ReadFromJsonAsync<T>(PortalJson.Options, cancellationToken), null);
			}

			if (response.StatusCode == HttpStatusCode.Unauthorized)
			{
				SignIn();
				return new PortalAnswer<T>(null, "unauthorized");
			}
			return new PortalAnswer<T>(null, await ErrorOfAsync(response));
		}

		// Every change carries the anti-forgery token of the session answer
		private async Task<PortalOutcome> SendAsync<T>(HttpMethod method, string path, T body)
		{
			PortalSessionInfo? session = await LoadSessionAsync();
			if (session == null)
			{
				return new PortalOutcome(false, "unauthorized");
			}

			using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: PortalJson.Options) };
			request.Headers.Add(session.CsrfHeader, session.CsrfToken);

			using HttpResponseMessage response = await m_http.SendAsync(request);
			if (response.IsSuccessStatusCode)
			{
				return PortalOutcome.Success;
			}

			if (response.StatusCode == HttpStatusCode.Unauthorized)
			{
				SignIn();
				return new PortalOutcome(false, "unauthorized");
			}

			return new PortalOutcome(false, await ErrorOfAsync(response));
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
			catch (System.Text.Json.JsonException)
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
