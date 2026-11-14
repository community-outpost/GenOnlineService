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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace GenOnlineService.Api
{
	// Answers a refused REST caller with the section 7 forbidden body instead of a bare 403, naming the permission when a perm:<name> policy failed
	public sealed class ApiAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
	{
		private readonly AuthorizationMiddlewareResultHandler m_default = new();

		public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
		{
			if (authorizeResult.Forbidden && IsApiEndpoint(context) && !context.Response.HasStarted)
			{
				context.Response.StatusCode = StatusCodes.Status403Forbidden;
				await context.Response.WriteAsJsonAsync(ApiResults.BodyOf(ForbiddenOf(authorizeResult)), ApiResults.Json);
				return;
			}

			// a missing or refused token goes through the scheme challenge, which writes the 401 or banned body
			await m_default.HandleAsync(next, context, policy, authorizeResult);
		}

		// A failed role check alone has no permission to name
		public static Realtime.Failure ForbiddenOf(PolicyAuthorizationResult authorizeResult)
		{
			string? permission = authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<PermissionRequirement>().Select(requirement => requirement.Permission).FirstOrDefault();
			return permission != null ? Realtime.Result.Forbidden(permission) : Realtime.Result.Fail(Realtime.ErrorCodes.Forbidden);
		}

		private static bool IsApiEndpoint(HttpContext context)
		{
			ControllerActionDescriptor? action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
			return action != null && typeof(ApiControllerBase).IsAssignableFrom(action.ControllerTypeInfo);
		}
	}
}
