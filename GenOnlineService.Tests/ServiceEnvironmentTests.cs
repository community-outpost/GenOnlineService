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

using GenOnlineService.Flags;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json.Nodes;
using Xunit;

namespace GenOnlineService.Tests;

public class ServiceEnvironmentTests
{
	private static IConfiguration Config(string? value)
	{
		var values = new Dictionary<string, string?>();
		if (value != null)
		{
			values["Service:environment"] = value;
		}
		return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
	}

	private static Microsoft.Extensions.Hosting.Internal.HostingEnvironment Host(string name) => new() { EnvironmentName = name };

	// live, test and dev are the only names, and each is taken as written
	[Theory]
	[InlineData("live")]
	[InlineData("test")]
	[InlineData("dev")]
	public void EachKnownNameIsAccepted(string name)
	{
		Assert.Equal(name, ServiceEnvironment.Read(Config(name), isDevelopment: false).Name);
		Assert.Equal(name, ServiceEnvironment.Read(Config(name), isDevelopment: true).Name);
	}

	// an unset value is dev only on an ASP.NET Development host
	[Fact]
	public void DevelopmentDefaultsToDevAndEveryOtherHostNeedsTheSetting()
	{
		Assert.Equal("dev", ServiceEnvironment.Read(Config(null), isDevelopment: true).Name);
		Assert.Equal("dev", ServiceEnvironment.Read(Config(""), isDevelopment: true).Name);

		InvalidOperationException missing = Assert.Throws<InvalidOperationException>(() => ServiceEnvironment.Read(Config(null), isDevelopment: false));
		Assert.Contains("Service:environment is not set", missing.Message);
		Assert.Contains("live, test or dev", missing.Message);
		Assert.Throws<InvalidOperationException>(() => ServiceEnvironment.Read(Config(" "), isDevelopment: false));
	}

	// a deploy name from before the rename or a typo stops the start instead of reporting a wrong environment
	[Theory]
	[InlineData("production")]
	[InlineData("prod")]
	[InlineData("staging")]
	[InlineData("Production")]
	[InlineData("Live")]
	[InlineData(" live")]
	public void AnUnknownNameStopsTheStart(string name)
	{
		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => ServiceEnvironment.Read(Config(name), isDevelopment: true));
		Assert.Contains("Service:environment", ex.Message);
		Assert.Contains($"`{name}`", ex.Message);
		Assert.Contains("live, test or dev", ex.Message);
	}

	// startup reads the setting with the host's ASP.NET environment
	[Fact]
	public void ConfigureUsesTheHostEnvironment()
	{
		Assert.Equal("dev", ServiceEnvironment.Configure(Config(null), Host(Environments.Development)).Name);
		Assert.Equal("live", ServiceEnvironment.Configure(Config("live"), Host(Environments.Production)).Name);
		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => ServiceEnvironment.Configure(Config(null), Host(Environments.Production)));
		Assert.Contains("Service:environment", ex.Message);
	}

	private static HttpContext Request(ServiceEnvironment environment)
	{
		var services = new ServiceCollection();
		services.AddSingleton(environment);
		return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
	}

	// OFREP and the service config evaluate with the service environment, not the ASP.NET one
	[Theory]
	[InlineData("live")]
	[InlineData("test")]
	[InlineData("dev")]
	public void TheEvaluationContextCarriesTheServiceEnvironment(string name)
	{
		HttpContext http = Request(ServiceEnvironment.Read(Config(name), isDevelopment: false));

		Assert.Equal(name, OfrepController.ContextOf(http)[FlagContext.Environment]!.GetValue<string>());
		Assert.Equal(name, OfrepController.ContextOf(http, Host(Environments.Production))[FlagContext.Environment]!.GetValue<string>());
	}

	// a flag that targets live is on for a live pod and off for a test pod running the same ASP.NET environment
	[Fact]
	public void TargetingOnEnvironmentTellsLiveFromTest()
	{
		var flag = new JsonObject
		{
			["variants"] = new JsonObject { ["on"] = true, ["off"] = false },
			["defaultVariant"] = "off",
			["targeting"] = new JsonObject { ["if"] = new JsonArray(new JsonObject { ["=="] = new JsonArray(new JsonObject { ["var"] = FlagContext.Environment }, "live") }, "on", "off") }
		};

		JsonObject live = OfrepController.ContextOf(Request(ServiceEnvironment.Read(Config("live"), isDevelopment: false)));
		JsonObject test = OfrepController.ContextOf(Request(ServiceEnvironment.Read(Config("test"), isDevelopment: false)));

		Assert.True(FlagEvaluator.Evaluate("k", flag, live).Value!.GetValue<bool>());
		Assert.False(FlagEvaluator.Evaluate("k", flag, test).Value!.GetValue<bool>());
	}

	// Sentry reports the service environment unless Sentry:environment names one
	[Fact]
	public void SentryDefaultsToTheServiceEnvironment()
	{
		ServiceEnvironment live = ServiceEnvironment.Read(Config("live"), isDevelopment: false);

		Assert.Equal("live", Program.SentryEnvironmentFor(null, live));
		Assert.Equal("live", Program.SentryEnvironmentFor("", live));
		Assert.Equal("canary", Program.SentryEnvironmentFor("canary", live));
		Assert.Equal("dev", Program.SentryEnvironmentFor(null, ServiceEnvironment.Read(Config(null), isDevelopment: true)));
	}
}
