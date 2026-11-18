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
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Server.AspNetCore;
using Xunit;

namespace GenOnlineService.Tests;

// Core:enforce_https: one value for the accounts server and the pipeline, true when missing
public class HttpsEnforcementTests
{
	private static IConfiguration Config(string? enforceHttps)
	{
		Dictionary<string, string?> values = new() { ["Accounts:ephemeral_keys"] = "true" };
		if (enforceHttps != null)
		{
			values["Core:enforce_https"] = enforceHttps;
		}
		return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
	}

	[Theory]
	[InlineData(null, true)]
	[InlineData("true", true)]
	[InlineData("false", false)]
	public void MissingKeyMeansEnforced(string? value, bool expected)
	{
		IConfiguration config = Config(value);
		Assert.Equal(expected, AccountsSettings.EnforceHttps(config));
		Assert.Equal(expected, AccountsSettings.Read(config).RequireHttps);
	}

	[Theory]
	[InlineData(null, true)]
	[InlineData("false", false)]
	public void CookiesAndTransportSecurityFollowTheKey(string? value, bool enforced)
	{
		ServiceCollection services = new();
		services.AddLogging();
		services.AddGenOnlineIdentity(AccountsSettings.Read(Config(value)), development: true);
		using ServiceProvider provider = services.BuildServiceProvider();

		CookieSecurePolicy expected = enforced ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
		Assert.Equal(expected, provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme).Cookie.SecurePolicy);
		Assert.Equal(expected, provider.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.SecurePolicy);
		Assert.Equal(!enforced, provider.GetRequiredService<IOptionsMonitor<OpenIddictServerAspNetCoreOptions>>().CurrentValue.DisableTransportSecurityRequirement);
	}

	private static async Task<HttpContext> Send(RequestDelegate pipeline, string scheme)
	{
		DefaultHttpContext context = new();
		context.Request.Scheme = scheme;
		context.Request.Host = new HostString("game.example");
		context.Request.Path = "/api/v1/me";
		await pipeline(context);
		return context;
	}

	[Theory]
	[InlineData(null, true)]
	[InlineData("false", false)]
	public async Task RedirectAndHstsFollowTheKey(string? value, bool enforced)
	{
		IConfiguration config = Config(value);
		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton(config);
		services.AddHttpsRedirection(options => options.HttpsPort = 443);
		using ServiceProvider provider = services.BuildServiceProvider();
		ApplicationBuilder app = new(provider);
		ListLogger logger = new();

		Program.UseHttpsEnforcement(app, config, logger);
		app.Run(context =>
		{
			context.Response.StatusCode = StatusCodes.Status200OK;
			return Task.CompletedTask;
		});
		RequestDelegate pipeline = app.Build();

		HttpContext plain = await Send(pipeline, "http");
		HttpContext secure = await Send(pipeline, "https");
		if (enforced)
		{
			Assert.Equal(StatusCodes.Status307TemporaryRedirect, plain.Response.StatusCode);
			Assert.Equal("https://game.example/api/v1/me", plain.Response.Headers.Location.ToString());
			Assert.StartsWith("max-age=", secure.Response.Headers.StrictTransportSecurity.ToString());
			Assert.Empty(logger.Entries);
		}
		else
		{
			Assert.Equal(StatusCodes.Status200OK, plain.Response.StatusCode);
			Assert.Empty(secure.Response.Headers.StrictTransportSecurity.ToString());
			var entry = Assert.Single(logger.Entries);
			Assert.Equal(LogLevel.Warning, entry.Level);
			Assert.Equal("Core:enforce_https is disabled. Bearer tokens will be sent in clear text over any plain-HTTP listener.", entry.Message);
		}
		Assert.Equal(StatusCodes.Status200OK, secure.Response.StatusCode);
	}

	// An expected outage is one Warning line without the exception; anything else keeps the full Error
	[Fact]
	public async Task BackgroundJobLogsAnOutageAsOneWarning()
	{
		ListLogger logger = new();
		await Program.RunObservedBackgroundJobAsync("upstream_sync", logger, () => throw new UpstreamUnavailableException("The upstream is unreachable at http://upstream.test/: The response ended prematurely. (ResponseEnded)", new HttpRequestException("inner")));
		var outage = Assert.Single(logger.Entries);
		Assert.Equal(LogLevel.Warning, outage.Level);
		Assert.Null(outage.Exception);
		Assert.Equal("Background job upstream_sync could not reach its upstream: The upstream is unreachable at http://upstream.test/: The response ended prematurely. (ResponseEnded)", outage.Message);

		logger.Entries.Clear();
		InvalidOperationException bug = new("The sync skipped an entry after 4");
		await Program.RunObservedBackgroundJobAsync("upstream_sync", logger, () => throw bug);
		var failure = Assert.Single(logger.Entries);
		Assert.Equal(LogLevel.Error, failure.Level);
		Assert.Same(bug, failure.Exception);
		Assert.Equal("Background job upstream_sync failed", failure.Message);
	}
}

internal sealed class ListLogger : ILogger
{
	public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		lock (Entries)
		{
			Entries.Add((logLevel, formatter(state, exception), exception));
		}
	}
}
