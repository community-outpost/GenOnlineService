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

using GenOnlineService.Api;
using GenOnlineService.Identity;
using GenOnlineService.OpenApi;
using GenOnlineService.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace GenOnlineService.Tests;

// The OpenAPI document of /api/v1 and its viewer, through the in-process pipeline as Program wires them (S3)
public class ApiDocsTests
{
	private const string Host = "docs.test";
	private const string ProbeSecret = "probe-secret-value-1f3a";

	[Fact]
	public async Task DocumentIsOpenApi3AndListsEveryApiRoute()
	{
		var docs = new DocsHost();
		(int status, string body, string? type) = await docs.Get("/openapi/v1.json");
		Assert.Equal(200, status);
		Assert.StartsWith("application/json", type);
		JsonNode document = JsonNode.Parse(body)!;
		Assert.StartsWith("3.", (string)document["openapi"]!);

		JsonObject paths = document["paths"]!.AsObject();
		List<string> missing = docs.ApiRoutes().Where(route => paths[route.Path]?[route.Method] == null).Select(route => $"{route.Method} {route.Path}").ToList();
		Assert.True(missing.Count == 0, "routes without docs: " + String.Join(", ", missing));
		Assert.True(docs.ApiRoutes().Count >= 70, $"only {docs.ApiRoutes().Count} /api/v1 routes found");
		Assert.All(paths.Select(p => p.Key), path => Assert.StartsWith("/api/v1/", path));
	}

	[Fact]
	public async Task SecuritySchemesNameTheThreeWaysIn()
	{
		var docs = new DocsHost();
		JsonNode document = JsonNode.Parse((await docs.Get("/openapi/v1.json")).Body)!;
		JsonNode schemes = document["components"]!["securitySchemes"]!;
		Assert.Equal("bearer", (string)schemes["access_token"]!["scheme"]!);
		Assert.Equal("bearer", (string)schemes["personal_access_token"]!["scheme"]!);
		Assert.Equal("cookie", (string)schemes["sign_in_cookie"]!["in"]!);

		// a token admin route is a web session route: never a gopat- token
		JsonArray tokens = document["paths"]!["/api/v1/personal_access_tokens"]!["get"]!["security"]!.AsArray();
		Assert.DoesNotContain(tokens, r => r?["personal_access_token"] != null);
		// a perm: policy route takes a gopat- token and names its permission
		JsonNode audit = document["paths"]!["/api/v1/audit_events"]!["get"]!;
		Assert.Contains(audit["security"]!.AsArray(), r => r?["personal_access_token"] != null);
		Assert.Contains("`audit.read`", (string)audit["description"]!);
		// a machine permission is never part of a staff role, so only a gopat- token gets in
		JsonArray revoke = document["paths"]!["/api/v1/users/{id}/revoke_sessions"]!["post"]!["security"]!.AsArray();
		Assert.Equal("personal_access_token", Assert.Single(revoke)!.AsObject().Single().Key);
		// a game client route takes no gopat- token
		JsonArray session = document["paths"]!["/api/v1/auth/session"]!["post"]!["security"]!.AsArray();
		Assert.Equal("access_token", Assert.Single(session)!.AsObject().Single().Key);
	}

	[Fact]
	public async Task EveryPropertyNameIsSnakeCase()
	{
		var docs = new DocsHost();
		JsonNode document = JsonNode.Parse((await docs.Get("/openapi/v1.json")).Body)!;
		List<string> wrong = document["components"]!["schemas"]!.AsObject()
			.SelectMany(schema => (schema.Value?["properties"]?.AsObject() ?? new JsonObject()).Select(p => $"{schema.Key}.{p.Key}"))
			.Where(name => !Regex.IsMatch(name.Substring(name.IndexOf('.') + 1), "^[a-z][a-z0-9_]*$"))
			.ToList();
		Assert.True(wrong.Count == 0, String.Join(", ", wrong));
	}

	[Fact]
	public async Task DocumentTakesItsBaseUrlFromTheRequestAndHoldsNoSecrets()
	{
		var docs = new DocsHost();
		string body = (await docs.Get("/openapi/v1.json")).Body;
		JsonNode document = JsonNode.Parse(body)!;
		Assert.Equal("http://" + Host + "/", ((string)document["servers"]![0]!["url"]!).TrimEnd('/') + "/");
		Assert.DoesNotContain(ProbeSecret, body);
		Assert.DoesNotContain("localhost", body);
		Assert.DoesNotContain(Environment.MachineName, body, StringComparison.OrdinalIgnoreCase);

		string other = (await docs.Get("/openapi/v1.json", "other.example")).Body;
		Assert.Contains("http://other.example", other);
		Assert.DoesNotContain(Host, other);
	}

	[Fact]
	public async Task YamlDocumentIsServedToo()
	{
		(int status, string body, _) = await new DocsHost().Get("/openapi/v1.yaml");
		Assert.Equal(200, status);
		Assert.Contains("openapi: ", body);
	}

	[Fact]
	public async Task ViewerIsAPageOfTheDocument()
	{
		var docs = new DocsHost();
		// the viewer lives under its trailing slash, so its relative asset links resolve
		(int redirect, _, _, string? location) = await docs.Send("/docs/api");
		Assert.Equal(302, redirect);
		Assert.Equal("/docs/api/", new Uri(new Uri("http://" + Host + "/docs/api"), location).AbsolutePath);

		(int status, string body, string? type) = await docs.Get("/docs/api/");
		Assert.Equal(200, status);
		Assert.StartsWith("text/html", type);
		// the document path is relative to the site root the page finds above /docs/api/
		Assert.Contains("\"url\":\"openapi/v1.json\"", body);
		Assert.Contains("<script src=\"scalar.js\">", body);
		Assert.DoesNotContain("https://", body);

		// its script comes from this service, not from a CDN
		(int scriptStatus, string script, string? scriptType) = await docs.Get("/docs/api/scalar.js");
		Assert.Equal(200, scriptStatus);
		Assert.Contains("javascript", scriptType);
		Assert.True(script.Length > 100_000);
	}

	[Fact]
	public async Task SwitchedOffBothAnswer404()
	{
		var docs = new DocsHost(("OpenApi:enabled", "false"));
		Assert.Equal(404, (await docs.Get("/openapi/v1.json")).Status);
		Assert.Equal(404, (await docs.Get("/openapi/v1.yaml")).Status);
		Assert.Equal(404, (await docs.Get("/docs/api")).Status);
		Assert.Equal(404, (await docs.Get("/docs/api/")).Status);
	}

	// The services and middleware of Program that decide the document: the controllers, /api/v1 authentication
	// and authorization metadata, and the docs switch
	private sealed class DocsHost
	{
		private readonly RequestDelegate m_pipeline;
		private readonly IServiceProvider m_services;

		public DocsHost(params (string Key, string Value)[] extra)
		{
			Dictionary<string, string?> values = new() { ["Accounts:certificate_password"] = ProbeSecret, ["Core:enforce_https"] = "false" };
			foreach ((string key, string value) in extra)
			{
				values[key] = value;
			}
			IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

			ServiceCollection services = new();
			services.AddLogging();
			services.AddSingleton(configuration);
			services.AddSingleton<IWebHostEnvironment>(new DocsEnvironment());
			services.AddSingleton<IHostEnvironment>(sp => sp.GetRequiredService<IWebHostEnvironment>());
			var diagnostics = new System.Diagnostics.DiagnosticListener(nameof(ApiDocsTests));
			services.AddSingleton(diagnostics);
			services.AddSingleton<System.Diagnostics.DiagnosticSource>(diagnostics);
			services.AddApiAuthentication(signInCookie: true);
			services.AddRouting();
			services.AddControllers().AddApplicationPart(typeof(AccountsController).Assembly).AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = null);
			services.AddAuthorization();
			services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
			services.AddApiDocs(configuration);
			m_services = services.BuildServiceProvider();

			var app = new ApplicationBuilder(m_services);
			app.UseRouting();
			app.Use(RestSetup.StatusBodies);
			app.UseEndpoints(endpoints =>
			{
				endpoints.MapControllers();
				endpoints.MapApiDocs(configuration);
			});
			m_pipeline = app.Build();
		}

		// Every /api/v1 route of the endpoint data source with its method, as the document writes the path
		public List<(string Method, string Path, ControllerActionDescriptor Action)> ApiRoutes()
		{
			var routes = new List<(string, string, ControllerActionDescriptor)>();
			foreach (RouteEndpoint endpoint in m_services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
			{
				string path = "/" + (endpoint.RoutePattern.RawText ?? "").TrimStart('~', '/');
				ControllerActionDescriptor? action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
				if (!path.StartsWith("/api/v1/", StringComparison.Ordinal) || action == null)
				{
					continue;
				}
				path = Regex.Replace(path, @"\{([A-Za-z0-9_]+)(:[^}]*)?\}", "{$1}");
				foreach (string method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
				{
					routes.Add((method.ToLowerInvariant(), path, action));
				}
			}
			return routes;
		}

		public async Task<(int Status, string Body, string? ContentType)> Get(string path, string host = Host)
		{
			(int status, string body, string? type, _) = await Send(path, host);
			return (status, body, type);
		}

		public async Task<(int Status, string Body, string? ContentType, string? Location)> Send(string path, string host = Host)
		{
			using IServiceScope scope = m_services.CreateScope();
			var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
			context.Request.Method = "GET";
			context.Request.Scheme = "http";
			context.Request.Host = new HostString(host);
			context.Request.Path = path;
			var body = new MemoryStream();
			context.Response.Body = body;
			await m_pipeline(context);
			return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()), context.Response.ContentType, context.Response.Headers.Location.ToString());
		}
	}

	private sealed class DocsEnvironment : IWebHostEnvironment
	{
		public string ApplicationName { get; set; } = typeof(ApiDocs).Assembly.GetName().Name!;
		public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
		public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
		public string EnvironmentName { get; set; } = Environments.Production;
		public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
		public string WebRootPath { get; set; } = AppContext.BaseDirectory;
	}
}
