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

			// the realtime upgrade reaches the hub, which closes it after the handshake with unauthorized or moderation.banned (RealtimeAdmission):
			// behind the tunnel a 401 or 403 here would reach the client only as a 502. SignalR checks the hub's class-level [Authorize] only here, so RealtimeAdmittedFilter is what keeps a refused connection from running hub methods
			if ((authorizeResult.Challenged || authorizeResult.Forbidden) && Realtime.RealtimeAdmission.IsRealtimeUpgrade(context))
			{
				await next(context);
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

		// Every /api and /-/ path, and every ApiControllerBase action wherever it is routed
		private static bool IsApiEndpoint(HttpContext context)
		{
			if (Rest.RestResults.IsRestPath(context.Request.Path))
			{
				return true;
			}

			ControllerActionDescriptor? action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
			return action != null && typeof(ApiControllerBase).IsAssignableFrom(action.ControllerTypeInfo);
		}
	}
}
