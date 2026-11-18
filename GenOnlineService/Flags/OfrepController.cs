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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace GenOnlineService.Flags
{
	// OpenFeature Remote Evaluation Protocol (https://openfeature.dev/docs/reference/other-technologies/ofrep/openapi) for the game.
	// The evaluation context comes from the session; whatever context the client sends is ignored.
	public sealed class OfrepController : ApiControllerBase
	{
		public OfrepController(IAuthorizationService authorization) : base(authorization)
		{
		}

		// Every enabled client flag; If-None-Match with the last ETag answers 304 while nothing changed
		[HttpPost("~/ofrep/v1/evaluate/flags")]
		public IActionResult Bulk()
		{
			FlagSnapshot snapshot = FlagCatalog.Current;
			JsonObject context = ContextOf();
			string etag = snapshot.ETagFor(context);
			Response.Headers[HeaderNames.ETag] = etag;
			Response.Headers[HeaderNames.CacheControl] = "no-cache";

			if (Request.Headers.IfNoneMatch.Any(value => value != null && value.Split(',').Any(tag => tag.Trim() == etag)))
			{
				return StatusCode(StatusCodes.Status304NotModified);
			}

			var flags = new JsonArray();
			foreach ((string key, FlagResult result) in snapshot.EvaluateClient(context))
			{
				flags.Add(Evaluation(key, result));
			}
			return Json(new JsonObject { ["flags"] = flags });
		}

		[HttpPost("~/ofrep/v1/evaluate/flags/{key}")]
		public IActionResult Single(string key)
		{
			FlagSnapshot snapshot = FlagCatalog.Current;
			if (!snapshot.Flags.TryGetValue(key, out JsonObject? flag) || FlagSnapshot.ScopeOf(flag) != FlagScopes.Client || !FlagSnapshot.IsEnabled(flag))
			{
				return Json(new JsonObject { ["key"] = key, ["errorCode"] = "FLAG_NOT_FOUND", ["errorDetails"] = $"flag `{key}` does not exist" }, StatusCodes.Status404NotFound);
			}
			return Json(Evaluation(key, FlagEvaluator.Evaluate(key, flag, ContextOf())));
		}

		private JsonObject ContextOf() => ContextOf(HttpContext);

		// The caller's evaluation context: user id, roles, client id, country of the address, environment
		// The environment is the service environment live, test or dev, never the ASP.NET environment name
		internal static JsonObject ContextOf(HttpContext http)
		{
			Int64 userID = RealtimeClaims.UserID(http.User);
			string ipAddress = IPHelpers.NormalizeIP(http.Connection.RemoteIpAddress?.ToString());
			ServiceEnvironment environment = http.RequestServices.GetRequiredService<ServiceEnvironment>();
			return FlagContext.Create(userID, StaffRoles.RolesOf(userID), RealtimeClaims.ClientID(http.User), null, GeoLocation.Lookup(ipAddress).Country, environment.Name);
		}

		private static JsonObject Evaluation(string key, FlagResult result)
		{
			return new JsonObject
			{
				["key"] = key,
				["value"] = result.Value?.DeepClone(),
				["reason"] = result.Reason,
				["variant"] = result.Variant,
				["metadata"] = new JsonObject()
			};
		}

		private static ContentResult Json(JsonObject body, int status = StatusCodes.Status200OK)
		{
			return new ContentResult { Content = body.ToJsonString(), ContentType = "application/json; charset=utf-8", StatusCode = status };
		}
	}
}
