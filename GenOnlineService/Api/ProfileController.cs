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

using GenOnlineService.Names;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace GenOnlineService.Api
{
	using GenOnlineService.Realtime;

	// The caller's display name, their match results and the matchmaking playlists (section 10.3)
	public sealed class ProfileController : ApiControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;
		private readonly PlayerNames m_names;
		private readonly LobbyManager m_lobbyManager;

		public ProfileController(IDbContextFactory<AppDbContext> dbFactory, LobbyManager lobbyManager, IAuthorizationService authorization, PlayerNames names) : base(authorization)
		{
			m_dbFactory = dbFactory;
			m_names = names;
			m_lobbyManager = lobbyManager;
		}

		[HttpPatch("~/api/v1/me")]
		public async Task<IActionResult> UpdateMe()
		{
			(UpdateMeRequest? body, Failure? malformed) = await ReadBodyAsync<UpdateMeRequest>();
			if (malformed != null)
			{
				return ApiResults.Fail(malformed.Value);
			}
			return ApiResults.NoContent(await m_names.RenameSelfAsync(Me, body?.Name));
		}

		// Idempotent: the same body again gets the same answer
		[HttpPut("~/api/v1/matches/{matchID:long}/results/me")]
		public async Task<IActionResult> SubmitResult(long matchID)
		{
			(MatchResultRequest? body, Failure? malformed) = await ReadBodyAsync<MatchResultRequest>();
			if (malformed != null)
			{
				return ApiResults.Fail(malformed.Value);
			}
			return ApiResults.Answer(await MatchActions.SubmitResultAsync(m_dbFactory, m_lobbyManager, Me, matchID, body ?? new MatchResultRequest()));
		}

		// Cacheable: the ETag is a hash of the body, a matching If-None-Match gets 304
		[HttpGet("~/api/v1/matchmaking/playlists")]
		public IActionResult GetPlaylists()
		{
			byte[] body = JsonSerializer.SerializeToUtf8Bytes(MatchActions.Playlists(), ApiResults.Json);
			string etag = "\"" + Convert.ToHexString(SHA256.HashData(body), 0, 16).ToLowerInvariant() + "\"";
			Response.Headers[HeaderNames.ETag] = etag;
			Response.Headers[HeaderNames.CacheControl] = "no-cache";

			if (Request.Headers.IfNoneMatch.Any(value => value != null && value.Split(',').Any(tag => tag.Trim() == etag || tag.Trim() == "*")))
			{
				return StatusCode(StatusCodes.Status304NotModified);
			}
			return File(body, "application/json");
		}
	}
}
