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
using System.Net;
using System.Text.Json;

namespace GenOnlineService.Controllers
{
	public static class RevocationHelpers
	{
		private static readonly EUserSessionType[] s_allSessionTypes = new[]
		{
			EUserSessionType.GameClient,
			EUserSessionType.ChatClient,
			EUserSessionType.GameLauncher
		};

		// Revoking a session type invalidates its tokens first, then drops its live connections.
		public static async Task<List<string>> RevokeAsync(Int64 userID, EUserSessionType? sessionType, string reason)
		{
			List<string> revoked = new();
			foreach (EUserSessionType type in sessionType != null ? new[] { sessionType.Value } : s_allSessionTypes)
			{
				await TokenRevocationManager.RevokeTokensForSession(userID, type, reason);
				revoked.Add(type.ToString());
			}

			await ModerationManager.DisconnectUser(userID, EModerationAction.Kick, reason, sessionType);
			return revoked;
		}
	}

	// A client signs itself out, which revokes the tokens of its own session type or, with all set, of every session type.
	[ApiController]
	[Authorize(Roles = "Player")]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class Logout : ControllerBase
	{
		[HttpPost]
		public async Task<IActionResult> Post()
		{
			Int64 userID = TokenHelper.GetUserID(this);
			EUserSessionType sessionType = TokenHelper.GetSessionType(this);
			if (userID == -1 || sessionType == EUserSessionType.None)
			{
				return Unauthorized();
			}

			bool bAll = false;
			try
			{
				using var reader = new StreamReader(HttpContext.Request.Body);
				string body = await reader.ReadToEndAsync();
				if (!String.IsNullOrWhiteSpace(body))
				{
					var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
					bAll = data != null && data.TryGetValue("all", out JsonElement allElement) && allElement.ValueKind == JsonValueKind.True;
				}
			}
			catch (JsonException)
			{
				return BadRequest(new { error = "malformed_body" });
			}

			List<string> revoked = await RevocationHelpers.RevokeAsync(userID, bAll ? null : sessionType, "Signed out");
			return Ok(new { user_id = userID, revoked_session_types = revoked });
		}
	}
}
