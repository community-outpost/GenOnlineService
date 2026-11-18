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
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
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
	private static readonly string s_fixtureDir = Path.Combine(AppContext.BaseDirectory, "api-fixtures");

	[Fact]
	public async Task DocumentIsOpenApi3AndListsEveryApiRoute()
	{
		var docs = new DocsHost();
		(int status, string body, string? type) = await docs.Get("/api/openapi/v1.json");
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
	public async Task EveryOperationHasSummaryBodyAnswerAndSecurity()
	{
		var docs = new DocsHost();
		JsonNode document = JsonNode.Parse((await docs.Get("/api/openapi/v1.json")).Body)!;
		List<string> problems = new();

		foreach ((string method, string path, ControllerActionDescriptor action) in docs.ApiRoutes())
		{
			JsonNode operation = document["paths"]![path]![method]!;
			string name = $"{method} {path}";

			if (String.IsNullOrWhiteSpace((string?)operation["summary"]))
			{
				problems.Add(name + ": no summary");
			}

			if (ReadsBody(action) && operation["requestBody"]?["content"]?["application/json"]?["schema"] == null)
			{
				problems.Add(name + ": reads a body but has no request schema");
			}

			JsonObject responses = operation["responses"]!.AsObject();
			bool answers = responses.Any(r => r.Key is "200" or "201" && r.Value?["content"]?["application/json"]?["schema"] != null)
				|| responses.Any(r => r.Key == "204" && r.Value?["content"] == null);
			if (!answers)
			{
				problems.Add(name + ": no 2xx schema and no 204");
			}
			if (responses["default"]?["content"]?["application/json"]?["schema"]?["$ref"]?.ToString() != "#/components/schemas/ApiError")
			{
				problems.Add(name + ": no shared error answer");
			}

			bool anonymous = action.EndpointMetadata.OfType<IAllowAnonymous>().Any() || !action.EndpointMetadata.OfType<IAuthorizeData>().Any();
			int requirements = operation["security"]?.AsArray().Count ?? 0;
			if (anonymous != (requirements == 0))
			{
				problems.Add(name + (anonymous ? ": anonymous but has security" : ": has no security requirement"));
			}
			if (action.EndpointMetadata.OfType<AllowSignInCookieAttribute>().Any() && operation["security"]?.AsArray().All(r => r?["sign_in_cookie"] == null) != false)
			{
				problems.Add(name + ": takes the sign-in cookie but does not say so");
			}

			string description = (string?)operation["description"] ?? "";
			foreach (string permission in action.EndpointMetadata.OfType<IAuthorizeData>().Select(a => a.Policy).OfType<string>().Where(p => p.StartsWith(PermissionPolicies.Prefix)))
			{
				if (!description.Contains($"`{permission.Substring(PermissionPolicies.Prefix.Length)}`"))
				{
					problems.Add(name + ": does not name " + permission);
				}
			}
		}

		Assert.True(problems.Count == 0, String.Join("\n", problems));
	}

	[Fact]
	public async Task SecuritySchemesNameTheThreeWaysIn()
	{
		var docs = new DocsHost();
		JsonNode document = JsonNode.Parse((await docs.Get("/api/openapi/v1.json")).Body)!;
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

	[Theory]
	[InlineData("get-service_accounts-item.response")]
	[InlineData("get-personal_access_tokens-self.response")]
	[InlineData("post-service_accounts.request")]
	[InlineData("post-personal_access_tokens.request")]
	public async Task SchemaNamesMatchTheFixture(string fixture)
	{
		var docs = new DocsHost();
		JsonNode document = JsonNode.Parse((await docs.Get("/api/openapi/v1.json")).Body)!;
		Type type = typeof(ApiError).Assembly.GetTypes().Single(t => t.GetCustomAttributes<ApiFixtureAttribute>().Any(a => a.Name == fixture));
		JsonObject expected = JsonNode.Parse(File.ReadAllText(Path.Combine(s_fixtureDir, fixture + ".json")))!.AsObject();

		JsonObject? properties = document["components"]?["schemas"]?[type.Name]?["properties"]?.AsObject();
		Assert.True(properties != null, $"no schema {type.Name}");
		Assert.Equal(expected.Select(p => p.Key).Order(), properties!.Select(p => p.Key).Order());
	}

	[Fact]
	public async Task EveryPropertyNameIsSnakeCase()
	{
		var docs = new DocsHost();
		JsonNode document = JsonNode.Parse((await docs.Get("/api/openapi/v1.json")).Body)!;
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
		string body = (await docs.Get("/api/openapi/v1.json")).Body;
		JsonNode document = JsonNode.Parse(body)!;
		Assert.Equal("http://" + Host + "/", ((string)document["servers"]![0]!["url"]!).TrimEnd('/') + "/");
		Assert.DoesNotContain(ProbeSecret, body);
		Assert.DoesNotContain("localhost", body);
		Assert.DoesNotContain(Environment.MachineName, body, StringComparison.OrdinalIgnoreCase);

		string other = (await docs.Get("/api/openapi/v1.json", "other.example")).Body;
		Assert.Contains("http://other.example", other);
		Assert.DoesNotContain(Host, other);
	}

	[Fact]
	public async Task YamlDocumentIsServedToo()
	{
		(int status, string body, _) = await new DocsHost().Get("/api/openapi/v1.yaml");
		Assert.Equal(200, status);
		Assert.Contains("openapi: ", body);
	}

	[Fact]
	public async Task ViewerIsAPageOfTheDocument()
	{
		var docs = new DocsHost();
		// the viewer lives under its trailing slash, so its relative asset links resolve
		(int redirect, _, _, string? location) = await docs.Send("/api/docs");
		Assert.Equal(302, redirect);
		Assert.Equal("/api/docs/", new Uri(new Uri("http://" + Host + "/api/docs"), location).AbsolutePath);

		(int status, string body, string? type) = await docs.Get("/api/docs/");
		Assert.Equal(200, status);
		Assert.StartsWith("text/html", type);
		// the document path is relative to the site root the page finds above /api/docs/
		Assert.Contains("\"url\":\"api/openapi/v1.json\"", body);
		Assert.Contains("<script src=\"scalar.js\">", body);
		Assert.DoesNotContain("https://", body);

		// its script comes from this service, not from a CDN
		(int scriptStatus, string script, string? scriptType) = await docs.Get("/api/docs/scalar.js");
		Assert.Equal(200, scriptStatus);
		Assert.Contains("javascript", scriptType);
		Assert.True(script.Length > 100_000);
	}

	[Fact]
	public async Task SwitchedOffBothAnswer404()
	{
		var docs = new DocsHost(("OpenApi:enabled", "false"));
		Assert.Equal(404, (await docs.Get("/api/openapi/v1.json")).Status);
		Assert.Equal(404, (await docs.Get("/api/openapi/v1.yaml")).Status);
		Assert.Equal(404, (await docs.Get("/api/docs")).Status);
		Assert.Equal(404, (await docs.Get("/api/docs/")).Status);
		Assert.Equal(404, (await docs.Get("/api")).Status);
	}

	[Fact]
	public async Task ApiAloneLeadsToTheViewer()
	{
		(int status, _, _, string? location) = await new DocsHost().Send("/api");
		Assert.Equal(302, status);
		Assert.Equal("/api/docs/", new Uri(new Uri("http://" + Host + "/api"), location).AbsolutePath);
	}

	// A body read through ReadBodyAsync, JsonSerializer or [FromBody], in the action or a helper of its controller
	private static bool ReadsBody(ControllerActionDescriptor action)
	{
		if (action.Parameters.Any(p => p.BindingInfo?.BindingSource == BindingSource.Body))
		{
			return true;
		}
		return Calls(action.MethodInfo, action.ControllerTypeInfo, 3).Any(method =>
			method.Name == "ReadBodyAsync" || (method.DeclaringType == typeof(JsonSerializer) && method.Name.StartsWith("Deserialize")));
	}

	private static IEnumerable<MethodBase> Calls(MethodInfo method, Type controller, int depth)
	{
		MethodInfo body = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) ?? method;
		byte[] il = body.GetMethodBody()?.GetILAsByteArray() ?? [];
		for (int i = 0; i + 4 < il.Length; i++)
		{
			if (il[i] != OpCodes.Call.Value && il[i] != OpCodes.Callvirt.Value)
			{
				continue;
			}
			MethodBase? called;
			try
			{
				called = body.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1), body.DeclaringType?.GetGenericArguments(), null);
			}
			catch (ArgumentException)
			{
				continue;
			}
			if (called == null)
			{
				continue;
			}
			yield return called;
			if (depth > 0 && called is MethodInfo helper && called.DeclaringType != null && called.DeclaringType.IsAssignableFrom(controller) && called.DeclaringType != typeof(object))
			{
				foreach (MethodBase inner in Calls(helper.IsGenericMethod ? helper.GetGenericMethodDefinition() : helper, controller, depth - 1))
				{
					yield return inner;
				}
			}
		}
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
