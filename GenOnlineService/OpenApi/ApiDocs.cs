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

using Scalar.AspNetCore;

namespace GenOnlineService.OpenApi
{
	// The OpenAPI document of /api/v1 at /openapi/v1.json (and .yaml) and its interactive viewer at /docs/api.
	// Both are read-only metadata: they need no sign-in and grant nothing, every call made from the viewer
	// goes through the same authentication and permission checks as any other call
	public static class ApiDocs
	{
		public const string DocumentName = "v1";
		public const string DocumentPath = "/openapi/v1.json";
		public const string DocumentYamlPath = "/openapi/v1.yaml";
		public const string ViewerPath = "/docs/api";

		private const string DocumentRoutePattern = "/openapi/{documentName}.json";
		private const string DocumentYamlRoutePattern = "/openapi/{documentName}.yaml";

		// OpenApi:enabled, on unless an environment turns the document and the viewer off
		public static bool Enabled(IConfiguration configuration) => configuration.GetValue("OpenApi:enabled", true);

		public static IServiceCollection AddApiDocs(this IServiceCollection services, IConfiguration configuration)
		{
			if (!Enabled(configuration))
			{
				return services;
			}

			services.AddOpenApi(DocumentName, options =>
			{
				// only the REST API, the sign-in pages, the OAuth endpoints and the ops probes have their own docs
				options.ShouldInclude = description => description.RelativePath != null && description.RelativePath.StartsWith("api/v1/", StringComparison.Ordinal);
				options.AddSchemaTransformer<WireNamesSchemaTransformer>();
				options.AddOperationTransformer<ApiOperationTransformer>();
				options.AddDocumentTransformer<ApiDocumentTransformer>();
			});
			return services;
		}

		public static IEndpointRouteBuilder MapApiDocs(this IEndpointRouteBuilder endpoints, IConfiguration configuration)
		{
			if (!Enabled(configuration))
			{
				return endpoints;
			}

			endpoints.MapOpenApi(DocumentRoutePattern);
			endpoints.MapOpenApi(DocumentYamlRoutePattern);
			endpoints.MapScalarApiReference(ViewerPath, options =>
			{
				// same origin: the page, its script and the document all come from this service, nothing from a CDN
				options.WithTitle("Generals Online API")
					.WithOpenApiRoutePattern(DocumentRoutePattern)
					.AddPreferredSecuritySchemes(ApiDocumentTransformer.AccessTokenScheme)
					.DisableDefaultFonts()
					.DisableTelemetry()
					.DisableAgent()
					.DisableMcp();
			});
			return endpoints;
		}
	}
}
