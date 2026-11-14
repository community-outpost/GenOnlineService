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
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace GenOnlineService.Api
{
	using GenOnlineService.Realtime;

	// Turns domain results into REST answers: 200 with a body, 204 without one, failures as {error, params} with the status of section 7
	public static class ApiResults
	{
		public static readonly JsonSerializerOptions Json = RealtimeJson.Create();

		public static int StatusOf(string code) => code switch
		{
			ErrorCodes.InvalidArgument => StatusCodes.Status400BadRequest,
			ErrorCodes.Forbidden or ErrorCodes.ModerationTargetOutranks => StatusCodes.Status403Forbidden,
			ErrorCodes.NotFound => StatusCodes.Status404NotFound,
			ErrorCodes.ReportDuplicate => StatusCodes.Status409Conflict,
			ErrorCodes.RateLimited => StatusCodes.Status429TooManyRequests,
			_ => StatusCodes.Status422UnprocessableEntity
		};

		public static ApiError BodyOf(Failure failure) => new ApiError { Error = failure.Code, Params = failure.Params };

		public static IActionResult Fail(Failure failure) => new JsonResult(BodyOf(failure), Json) { StatusCode = StatusOf(failure.Code) };

		public static IActionResult Ok<T>(T body) where T : class => new JsonResult(body, Json) { StatusCode = StatusCodes.Status200OK };

		// 204 on success, the data of an empty result is never sent
		public static IActionResult NoContent(Result<Empty> result) => result.Ok ? new NoContentResult() : Fail(new Failure(result.Code!, result.Params!));

		public static IActionResult Answer<T>(Result<T> result) where T : class => result.Ok ? Ok(result.Data!) : Fail(new Failure(result.Code!, result.Params!));
	}

	// The REST endpoints of section 10 that act for a signed-in player, with the same session principal as the realtime hub
	[ApiController]
	[Authorize(Roles = "GameClient,ChatClient,GameLauncher")]
	public abstract class ApiControllerBase : ControllerBase
	{
		protected ApiControllerBase(IAuthorizationService authorization)
		{
			Authorization = authorization;
		}

		protected IAuthorizationService Authorization { get; }

		protected Int64 Me => RealtimeClaims.UserID(User);

		// Checks one of the perm:<permission> policies against the caller
		protected async Task<Failure?> RequirePermissionAsync(string permission)
		{
			AuthorizationResult result = await Authorization.AuthorizeAsync(User, PermissionPolicies.For(permission));
			return result.Succeeded ? null : Result.Forbidden(permission);
		}

		// Reads the JSON body with the hub's options, an empty body is null and a malformed one names the field it broke on
		protected async Task<(T? Body, Failure? Failure)> ReadBodyAsync<T>() where T : class
		{
			if (Request.ContentLength == 0)
			{
				return (null, null);
			}

			try
			{
				using StreamReader reader = new StreamReader(Request.Body);
				string text = await reader.ReadToEndAsync();
				if (String.IsNullOrWhiteSpace(text))
				{
					return (null, null);
				}
				return (JsonSerializer.Deserialize<T>(text, ApiResults.Json), null);
			}
			catch (JsonException ex)
			{
				string field = ex.Path is string path && path.StartsWith("$.", StringComparison.Ordinal) ? path.Substring(2) : "body";
				return (null, Result.Invalid(field));
			}
		}
	}
}
