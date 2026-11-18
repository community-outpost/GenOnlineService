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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace GenOnlineService.Tests;

// The accounts pages in one in-process MVC pipeline against a probe database, with a cookie jar like a browser's
internal sealed class ProbeFactory : IDbContextFactory<AppDbContext>
{
	private readonly ProbeDatabase m_probe;

	public ProbeFactory(ProbeDatabase probe)
	{
		m_probe = probe;
	}

	public AppDbContext CreateDbContext() => m_probe.Open();
}

internal sealed record PageResponse(int Status, string Body, string? Location, List<string> SetCookies, string FrameOptions)
{
	// every response header, for the paging headers of /api/v1 lists (set by AdminBrowser)
	public Microsoft.AspNetCore.Http.IHeaderDictionary? Headers { get; init; }
}

// The accounts pages in one in-process pipeline, with a cookie jar like a browser's
internal sealed class AccountsBrowser
{
	private readonly RequestDelegate m_pipeline;
	private readonly IServiceProvider m_services;
	private readonly Dictionary<string, string> m_cookies = new();

	public AccountsBrowser(ProbeDatabase probe, Dictionary<string, string?> settings, Action<IServiceCollection>? configure = null)
	{
		Dictionary<string, string?> values = new(settings) { ["Accounts:ephemeral_keys"] = "true", ["Core:enforce_https"] = "false" };
		AccountsSettings accounts = AccountsSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
		Settings = accounts;

		ServiceCollection services = new();
		services.AddLogging();
		// what a host would register for MVC
		var diagnostics = new System.Diagnostics.DiagnosticListener("PasswordPagesTests");
		services.AddSingleton(diagnostics);
		services.AddSingleton<System.Diagnostics.DiagnosticSource>(diagnostics);
		services.AddSingleton<IDbContextFactory<AppDbContext>>(new ProbeFactory(probe));
		configure?.Invoke(services);
		services.AddSingleton(accounts);
		services.AddGenOnlineIdentity(accounts, development: true);
		services.AddRouting();
		services.AddControllers().AddApplicationPart(typeof(AccountsController).Assembly);
		m_services = services.BuildServiceProvider();

		var app = new ApplicationBuilder(m_services);
		app.UseRouting();
		app.UseAuthentication();
		app.UseEndpoints(endpoints => endpoints.MapControllers());
		m_pipeline = app.Build();
	}

	private AccountsBrowser(RequestDelegate pipeline, IServiceProvider services, AccountsSettings settings)
	{
		m_pipeline = pipeline;
		m_services = services;
		Settings = settings;
	}

	// Another browser on the same host: the same services (and in-memory limits), its own cookie jar (a copy of this
	// one's with keepCookies, as a second tab of the same browser)
	public AccountsBrowser Fork(bool keepCookies = false)
	{
		var fork = new AccountsBrowser(m_pipeline, m_services, Settings) { Host = Host };
		if (keepCookies)
		{
			foreach ((string name, string value) in m_cookies)
			{
				fork.m_cookies[name] = value;
			}
		}
		return fork;
	}

	// The Host header the browser sends, as a client may set any
	public string Host { get; set; } = "accounts.test";

	public IServiceProvider Services => m_services;

	public AccountsSettings Settings { get; }

	public Task<PageResponse> Get(string pathAndQuery) => Send("GET", pathAndQuery, null);

	public Task<PageResponse> Post(string pathAndQuery, Dictionary<string, string> form) => Send("POST", pathAndQuery, form);

	private async Task<PageResponse> Send(string method, string pathAndQuery, Dictionary<string, string>? form)
	{
		using IServiceScope scope = m_services.CreateScope();
		var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
		scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
		context.Request.Method = method;
		context.Request.Scheme = "http";
		context.Request.Host = new HostString(Host);
		int query = pathAndQuery.IndexOf('?');
		context.Request.Path = query < 0 ? pathAndQuery : pathAndQuery.Substring(0, query);
		context.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(pathAndQuery.Substring(query));
		if (m_cookies.Count > 0)
		{
			context.Request.Headers.Cookie = String.Join("; ", m_cookies.Select(c => $"{c.Key}={c.Value}"));
		}
		if (form != null)
		{
			byte[] body = Encoding.UTF8.GetBytes(String.Join("&", form.Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}")));
			context.Request.ContentType = "application/x-www-form-urlencoded";
			context.Request.ContentLength = body.Length;
			context.Request.Body = new MemoryStream(body);
		}
		var responseBody = new MemoryStream();
		context.Response.Body = responseBody;

		await m_pipeline(context);

		List<string> setCookies = context.Response.Headers.SetCookie.Select(c => c!).ToList();
		foreach (string cookie in setCookies)
		{
			string pair = cookie.Split(';')[0];
			int equals = pair.IndexOf('=');
			string name = pair.Substring(0, equals);
			string value = pair.Substring(equals + 1);
			if (value.Length == 0 || cookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
			{
				m_cookies.Remove(name);
			}
			else
			{
				m_cookies[name] = value;
			}
		}

		return new PageResponse(context.Response.StatusCode, Encoding.UTF8.GetString(responseBody.ToArray()), context.Response.Headers.Location.ToString() is { Length: > 0 } l ? l : null, setCookies, context.Response.Headers.XFrameOptions.ToString());
	}

	public bool SignedIn => m_cookies.ContainsKey("go_session");

	public void SignOutLocally() => m_cookies.Remove("go_session");

	public void SetCookie(string name, string value) => m_cookies[name] = value;

	// The form's hidden antiforgery token, as a browser would post it back
	public static string Token(PageResponse page)
	{
		System.Text.RegularExpressions.Match match = Regex.Match(page.Body, "name=\"__go_consent_token\" value=\"([^\"]+)\"");
		Assert.True(match.Success, "the page has no antiforgery field: " + page.Body);
		return WebUtility.HtmlDecode(match.Groups[1].Value);
	}
}
