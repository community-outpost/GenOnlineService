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
using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.Globalization;
using System.Text.Json;

namespace GenOnlineService.Rest
{
	// Codes of section 7 that only the /api/v1 routes outside section 10 raise
	public static class RestCodes
	{
		public const string Unauthorized = "unauthorized";
		public const string ServiceUnavailable = "service.unavailable";
		public const string SessionGameRequired = "session.game_required";
		public const string EosInvalidToken = "eos.invalid_token";
		public const string EosAccountMismatch = "eos.account_mismatch";
		public const string EosAccountInUse = "eos.account_in_use";
		public const string EosAccountLocked = "eos.account_locked";
		public const string TokenLimitReached = "token.limit_reached";
		public const string Conflict = "conflict";
		public const string MethodNotAllowed = "method_not_allowed";
	}

	// Answers with the single error body; codes not listed here keep the status of ApiResults.StatusOf
	public static class RestResults
	{
		private static readonly Dictionary<string, int> s_statuses = new Dictionary<string, int>(StringComparer.Ordinal)
		{
			[RestCodes.Unauthorized] = StatusCodes.Status401Unauthorized,
			[RestCodes.ServiceUnavailable] = StatusCodes.Status503ServiceUnavailable,
			[RestCodes.SessionGameRequired] = StatusCodes.Status403Forbidden,
			[RestCodes.EosAccountMismatch] = StatusCodes.Status403Forbidden,
			[RestCodes.EosAccountInUse] = StatusCodes.Status409Conflict,
			[RestCodes.EosAccountLocked] = StatusCodes.Status409Conflict,
			[RestCodes.Conflict] = StatusCodes.Status409Conflict,
			[RestCodes.MethodNotAllowed] = StatusCodes.Status405MethodNotAllowed,
		};

		public static int StatusOf(string code) => s_statuses.TryGetValue(code, out int status) ? status : ApiResults.StatusOf(code);

		public static IActionResult Fail(Failure failure) => new JsonResult(ApiResults.BodyOf(failure), ApiResults.Json) { StatusCode = StatusOf(failure.Code) };

		public static IActionResult Fail(string code, object? parameters = null) => Fail(Result.Fail(code, parameters));

		public static IActionResult Ok<T>(T body) where T : class => ApiResults.Ok(body);

		public static IActionResult Created<T>(T body) where T : class => new JsonResult(body, ApiResults.Json) { StatusCode = StatusCodes.Status201Created };

		public static IActionResult NotFound(string what) => Fail(Result.NotFound(what));

		public static IActionResult Invalid(string field) => Fail(Result.Invalid(field));

		// Writes the body for middleware that has no IActionResult
		public static Task WriteAsync(HttpContext context, string code, object parameters)
		{
			context.Response.StatusCode = StatusOf(code);
			return context.Response.WriteAsJsonAsync(new ApiError { Error = code, Params = parameters }, ApiResults.Json);
		}

		public static bool IsRestPath(PathString path) => path.StartsWithSegments("/api") || path.StartsWithSegments("/-");
	}

	// ?page=&per_page= with the X-Total, X-Page, X-Per-Page, X-Next-Page and Link headers
	public readonly record struct OffsetPage(int Page, int PerPage)
	{
		public const int DefaultPerPage = 20;
		public const int MaxPerPage = 100;

		public int Skip => (Page - 1) * PerPage;
	}

	public static class Paging
	{
		// Keeps (page - 1) * per_page inside an int for Skip
		public const int MaxPage = int.MaxValue / OffsetPage.MaxPerPage;

		public static (OffsetPage Page, Failure? Failure) ReadOffset(HttpRequest request)
		{
			int page = 1;
			int perPage = OffsetPage.DefaultPerPage;
			if (request.Query.TryGetValue("page", out var pageText) && (!int.TryParse(pageText, NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1 || page > MaxPage))
			{
				return (default, Result.Invalid("page"));
			}
			if (request.Query.TryGetValue("per_page", out var perPageText) && (!int.TryParse(perPageText, NumberStyles.None, CultureInfo.InvariantCulture, out perPage) || perPage < 1 || perPage > OffsetPage.MaxPerPage))
			{
				return (default, Result.Invalid("per_page"));
			}
			return (new OffsetPage(page, perPage), null);
		}

		public static void WriteOffsetHeaders(HttpContext context, OffsetPage page, long total)
		{
			IHeaderDictionary headers = context.Response.Headers;
			headers["X-Total"] = total.ToString(CultureInfo.InvariantCulture);
			headers["X-Page"] = page.Page.ToString(CultureInfo.InvariantCulture);
			headers["X-Per-Page"] = page.PerPage.ToString(CultureInfo.InvariantCulture);
			bool hasNext = (long)page.Page * page.PerPage < total;
			headers["X-Next-Page"] = hasNext ? (page.Page + 1).ToString(CultureInfo.InvariantCulture) : "";
			if (hasNext)
			{
				headers["Link"] = $"<{LinkTo(context.Request, "page", (page.Page + 1).ToString(CultureInfo.InvariantCulture))}>; rel=\"next\"";
			}
		}

		// Keyset pages only link forward: the next page continues after the last id of this one
		public static void WriteKeysetNext(HttpContext context, string parameter, long? nextValue)
		{
			if (nextValue != null)
			{
				context.Response.Headers["Link"] = $"<{LinkTo(context.Request, parameter, nextValue.Value.ToString(CultureInfo.InvariantCulture))}>; rel=\"next\"";
			}
		}

		private static string LinkTo(HttpRequest request, string parameter, string value)
		{
			// The argument keeps its place in the query, a new one goes last
			List<KeyValuePair<string, string?>> query = request.Query.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Key == parameter ? value : pair.Value.ToString())).ToList();
			if (!request.Query.ContainsKey(parameter))
			{
				query.Add(new KeyValuePair<string, string?>(parameter, value));
			}
			return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(request.PathBase + request.Path, query);
		}
	}

	// Keys of application_settings that an admin may read and set through /api/v1/application/settings
	public static class SettingKeys
	{
		public const string Motd = "motd";

		public static readonly IReadOnlyList<string> Editable = [Motd];
	}

	// A failed policy on /api and /-/ answers 403 forbidden {permission}; other paths keep the default answer
	public sealed class RestAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
	{
		private readonly AuthorizationMiddlewareResultHandler m_default = new AuthorizationMiddlewareResultHandler();

		public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
		{
			if (authorizeResult.Forbidden && RestResults.IsRestPath(context.Request.Path))
			{
				string? permission = authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<PermissionRequirement>().Select(r => r.Permission).FirstOrDefault();
				return RestResults.WriteAsync(context, ErrorCodes.Forbidden, permission == null ? Empty.Instance : new { Permission = permission });
			}
			return m_default.HandleAsync(next, context, policy, authorizeResult);
		}
	}

	public static class RestSetup
	{
		// Model binding failures become invalid_argument {field} with the snake_case name of the first bad field
		public static IActionResult InvalidModelState(ActionContext context)
		{
			string field = context.ModelState.Where(pair => pair.Value?.ValidationState == ModelValidationState.Invalid).Select(pair => pair.Key).FirstOrDefault() ?? "body";
			field = field.StartsWith("$.", StringComparison.Ordinal) ? field.Substring(2) : field;
			return RestResults.Invalid(field.Length == 0 ? "body" : JsonNamingPolicy.SnakeCaseLower.ConvertName(field));
		}

		// An empty 404 or 405 on /api and /-/ (no route, or a route without that method) gets the single error body
		public static async Task StatusBodies(HttpContext context, Func<Task> next)
		{
			await next();
			if (context.Response.HasStarted || context.Response.ContentType != null || !RestResults.IsRestPath(context.Request.Path))
			{
				return;
			}
			if (context.Response.StatusCode == StatusCodes.Status404NotFound)
			{
				await RestResults.WriteAsync(context, ErrorCodes.NotFound, new { What = "route" });
			}
			else if (context.Response.StatusCode == StatusCodes.Status405MethodNotAllowed)
			{
				await RestResults.WriteAsync(context, RestCodes.MethodNotAllowed, Empty.Instance);
			}
		}
	}
}
