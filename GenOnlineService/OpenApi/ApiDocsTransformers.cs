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
using GenOnlineService.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace GenOnlineService.OpenApi
{
	// Adds what the action attributes cannot say to the generator: the body an action reads itself, the shared
	// error answer, and who may call it (the security requirement and the permission, from the endpoint metadata)
	internal sealed class ApiOperationTransformer : IOpenApiOperationTransformer
	{
		public const string ErrorSchema = "ApiError";

		public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
		{
			IList<object> metadata = context.Description.ActionDescriptor.EndpointMetadata;
			OpenApiDocument document = context.Document!;

			// also over a [FromBody] JsonNode, whose own schema says nothing
			if (metadata.OfType<ApiRequestBodyAttribute>().FirstOrDefault() is ApiRequestBodyAttribute body)
			{
				operation.RequestBody = new OpenApiRequestBody
				{
					Required = false,
					Content = new Dictionary<string, OpenApiMediaType>
					{
						["application/json"] = new OpenApiMediaType { Schema = await ComponentAsync(context, document, body.Type, cancellationToken) }
					}
				};
			}

			OpenApiSchemaReference error = await ErrorAsync(context, document, cancellationToken);
			operation.Responses ??= new OpenApiResponses();
			operation.Responses.TryAdd("default", ErrorResponse(error, "A failure: the status and the error code say which (docs/api.md section 7)."));

			ApiCallers callers = ApiCallers.Of(context.Description.ActionDescriptor);
			if (callers.Anonymous)
			{
				operation.Security = new List<OpenApiSecurityRequirement>();
				Describe(operation, "No sign-in needed.");
				return;
			}

			operation.Responses.TryAdd("401", ErrorResponse(error, "No valid credentials."));
			operation.Responses.TryAdd("403", ErrorResponse(error, "The caller may not do this: `params.permission` names a missing permission."));

			var security = new List<OpenApiSecurityRequirement>();
			if (callers.AccessToken)
			{
				security.Add(Requirement(document, ApiDocumentTransformer.AccessTokenScheme));
			}
			if (callers.PersonalAccessToken)
			{
				security.Add(Requirement(document, ApiDocumentTransformer.PersonalAccessTokenScheme));
			}
			if (callers.SignInCookie)
			{
				security.Add(Requirement(document, ApiDocumentTransformer.SignInCookieScheme));
			}
			operation.Security = security;

			Describe(operation, callers.Describe(context.Description.HttpMethod));
		}

		private static OpenApiSecurityRequirement Requirement(OpenApiDocument document, string scheme)
		{
			return new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(scheme, document)] = new List<string>() };
		}

		private static OpenApiResponse ErrorResponse(OpenApiSchemaReference error, string description)
		{
			return new OpenApiResponse
			{
				Description = description,
				Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new OpenApiMediaType { Schema = error } }
			};
		}

		private static void Describe(OpenApiOperation operation, string text)
		{
			operation.Description = String.IsNullOrEmpty(operation.Description) ? text : operation.Description + "\n\n" + text;
		}

		private static async Task<OpenApiSchemaReference> ErrorAsync(OpenApiOperationTransformerContext context, OpenApiDocument document, CancellationToken cancellationToken)
		{
			if (document.Components?.Schemas?.ContainsKey(ErrorSchema) != true)
			{
				IOpenApiSchema schema = await context.GetOrCreateSchemaAsync(typeof(ApiError), null, cancellationToken);
				if (schema is OpenApiSchema error)
				{
					error.Description = "Every failure: an error code and its params, such as {\"error\": \"not_found\", \"params\": {\"what\": \"user\"}}.";
					if (error.Properties != null && error.Properties.ContainsKey("params"))
					{
						error.Properties["params"] = new OpenApiSchema { Type = JsonSchemaType.Object, Description = "Details of the error, {} when it has none." };
					}
				}
				document.AddComponent(ErrorSchema, schema);
			}
			return new OpenApiSchemaReference(ErrorSchema, document);
		}

		// A request body schema, kept once under components like the generator keeps the response schemas
		private static async Task<IOpenApiSchema> ComponentAsync(OpenApiOperationTransformerContext context, OpenApiDocument document, Type type, CancellationToken cancellationToken)
		{
			string id = type.Name;
			if (document.Components?.Schemas?.ContainsKey(id) != true)
			{
				document.AddComponent(id, await context.GetOrCreateSchemaAsync(type, null, cancellationToken));
			}
			return new OpenApiSchemaReference(id, document);
		}
	}

	// Who may call an action, read from the same metadata the authorization middleware reads
	internal sealed class ApiCallers
	{
		public bool Anonymous { get; private init; }

		// an OpenIddict access token, unless every permission it needs is a machine permission no staff role holds
		public bool AccessToken { get; private init; }

		// a gopat- token passes the permission policies, never the game session policy nor the web-session-only token admin
		public bool PersonalAccessToken { get; private init; }

		public bool SignInCookie { get; private init; }

		public bool GameSession { get; private init; }

		// a game session, or a gopat- token with lobbies.read (AuthPolicies.PlayerSessionOrLobbiesRead)
		public bool GameSessionOrLobbiesRead { get; private init; }

		public bool WebSession { get; private init; }

		public IReadOnlyList<string> Permissions { get; private init; } = [];

		public bool PermissionsAnyOf { get; private init; }

		public static ApiCallers Of(Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor action)
		{
			IList<object> metadata = action.EndpointMetadata;
			IAuthorizeData[] authorize = metadata.OfType<IAuthorizeData>().ToArray();
			if (metadata.OfType<IAllowAnonymous>().Any() || authorize.Length == 0)
			{
				return new ApiCallers { Anonymous = true };
			}

			bool gameSession = authorize.Any(a => !String.IsNullOrEmpty(a.Roles) || a.Policy == ApiAuthentication.GameSessionOrSignInCookie);
			bool lobbiesRead = authorize.Any(a => a.Policy == AuthPolicies.PlayerSessionOrLobbiesRead);
			bool sessionOnly = authorize.Any(a => a.AuthenticationSchemes == Identity.SessionTokenHandler.SchemeName);
			bool webSession = action is ControllerActionDescriptor controller && typeof(ApiTokenAdminControllerBase).IsAssignableFrom(controller.ControllerTypeInfo);

			List<string> fromPolicies = authorize.Select(a => a.Policy).OfType<string>()
				.Where(policy => policy.StartsWith(PermissionPolicies.Prefix, StringComparison.Ordinal))
				.Select(policy => policy.Substring(PermissionPolicies.Prefix.Length)).ToList();
			IReadOnlyList<string> inCode = metadata.OfType<ApiPermissionAttribute>().FirstOrDefault()?.AnyOf ?? [];
			IReadOnlyList<string> permissions = fromPolicies.Count > 0 ? fromPolicies : inCode;
			bool machineOnly = permissions.Count > 0 && permissions.All(GenOnlineService.Permissions.IsMachine);

			return new ApiCallers
			{
				AccessToken = !machineOnly,
				PersonalAccessToken = !gameSession && !sessionOnly && !webSession,
				SignInCookie = !sessionOnly && metadata.OfType<AllowSignInCookieAttribute>().Any(),
				GameSession = gameSession,
				GameSessionOrLobbiesRead = lobbiesRead,
				WebSession = webSession,
				Permissions = permissions,
				PermissionsAnyOf = fromPolicies.Count == 0 && inCode.Count > 1
			};
		}

		public string Describe(string? httpMethod)
		{
			string cookie = SignInCookie ? ", or the staff pages' sign-in cookie" : "";
			List<string> lines = new();
			if (WebSession)
			{
				lines.Add("Callers: a staff web session (an access token of the website client" + (SignInCookie ? " or the staff pages' sign-in cookie" : "") + "); a `gopat-` token or a game client session is refused.");
			}
			else if (GameSessionOrLobbiesRead)
			{
				lines.Add("Callers: an access token of a game client, chat client or launcher session, or a `gopat-` token with `lobbies.read`" + cookie + ".");
			}
			else if (GameSession)
			{
				lines.Add("Callers: an access token of a game client, chat client or launcher session" + cookie + ".");
			}
			else if (!AccessToken)
			{
				lines.Add("Callers: a `gopat-` token of a service account; its permission is never part of a staff role" + cookie + ".");
			}
			else
			{
				lines.Add("Callers: an access token or a `gopat-` personal access token" + cookie + ".");
			}

			if (Permissions.Count == 1)
			{
				lines.Add($"Requires the permission `{Permissions[0]}`.");
			}
			else if (Permissions.Count > 1)
			{
				lines.Add((PermissionsAnyOf ? "Requires one of the permissions " : "Requires the permissions ") + String.Join(", ", Permissions.Select(p => $"`{p}`")) + ".");
			}

			bool write = httpMethod != null && !HttpMethods.IsGet(httpMethod) && !HttpMethods.IsHead(httpMethod);
			if (SignInCookie && write)
			{
				lines.Add("With the cookie, the call also needs the antiforgery header of the staff pages.");
			}
			return String.Join("\n\n", lines);
		}
	}

	// The document's title, its description and the three ways to authenticate
	internal sealed class ApiDocumentTransformer : IOpenApiDocumentTransformer
	{
		public const string AccessTokenScheme = "access_token";
		public const string PersonalAccessTokenScheme = "personal_access_token";
		public const string SignInCookieScheme = "sign_in_cookie";

		public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
		{
			document.Info = new OpenApiInfo
			{
				Title = "Generals Online API",
				Version = "v1",
				Description = "The REST API of the Generals Online services. docs/api.md in the services repository is the reference for the rules behind each route; "
					+ "the realtime hub (/realtime) is described in docs/realtime-protocol.md and is not part of this document.\n\n"
					+ "Every answer is JSON with snake_case names. A failure is {\"error\": \"<code>\", \"params\": {...}} with the status of its code."
			};

			WireNamesSchemaTransformer.FixComponents(document, context.ApplicationServices);

			document.Components ??= new OpenApiComponents();
			document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
			document.Components.SecuritySchemes[AccessTokenScheme] = new OpenApiSecurityScheme
			{
				Type = SecuritySchemeType.Http,
				Scheme = "bearer",
				BearerFormat = "JWT",
				Description = "An OAuth 2 access token, sent as `Authorization: Bearer <token>`. Clients get it with the authorization code flow and PKCE: "
					+ "/oauth/authorize, then /oauth/token, which also refreshes it. Game clients register their session with POST /api/v1/auth/session."
			};
			document.Components.SecuritySchemes[PersonalAccessTokenScheme] = new OpenApiSecurityScheme
			{
				Type = SecuritySchemeType.Http,
				Scheme = "bearer",
				BearerFormat = "gopat",
				Description = "A personal access token of a staff member or a service account, sent as `Authorization: Bearer gopat-...`. "
					+ "It carries its own permissions (docs/api-tokens.md)."
			};
			document.Components.SecuritySchemes[SignInCookieScheme] = new OpenApiSecurityScheme
			{
				Type = SecuritySchemeType.ApiKey,
				In = ParameterLocation.Cookie,
				Name = "go_session",
				Description = "The website sign-in cookie, accepted only on the routes the staff pages call and only without an Authorization header. "
					+ "A write also needs the antiforgery header the staff pages read from their host page; another origin is refused."
			};
			return Task.CompletedTask;
		}
	}
}
