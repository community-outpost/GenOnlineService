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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace GenOnlineService.Rest
{
	// Replaces ServiceConfig and MOTD; any signed-in session reads it
	public sealed class ConfigController : ApiControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public ConfigController(IDbContextFactory<AppDbContext> dbFactory, IAuthorizationService authorization) : base(authorization)
		{
			m_dbFactory = dbFactory;
		}

		[EndpointSummary("Reads the service configuration a game client needs.")]
		[ProducesResponseType<ConfigResponse>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/config")]
		public async Task<IActionResult> Get()
		{
			JsonElement? serviceConfig = await ConfigSources.ReadServiceConfigAsync();
			if (serviceConfig == null)
			{
				return RestResults.Fail(RestCodes.ServiceUnavailable, new { What = "config" });
			}

			return RestResults.Ok(new ConfigResponse
			{
				ServiceConfig = serviceConfig.Value,
				Motd = await ConfigSources.ReadMotdAsync(m_dbFactory)
			});
		}
	}
}
