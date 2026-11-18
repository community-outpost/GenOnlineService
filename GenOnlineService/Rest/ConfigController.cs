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
using System.Text.Json.Nodes;

namespace GenOnlineService.Rest
{
	// Replaces ServiceConfig and MOTD; any signed-in session reads it
	public sealed class ConfigController : ApiControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		private readonly IHostEnvironment m_environment;

		public ConfigController(IDbContextFactory<AppDbContext> dbFactory, IAuthorizationService authorization, IHostEnvironment environment) : base(authorization)
		{
			m_dbFactory = dbFactory;
			m_environment = environment;
		}

		// service_config is the client.* flags of the live revision, evaluated for the caller and grouped by key
		[HttpGet("~/api/v1/config")]
		public async Task<IActionResult> Get()
		{
			GenOnlineService.Flags.FlagSnapshot flags = GenOnlineService.Flags.FlagCatalog.Current;
			if (flags.Revision == 0)
			{
				return RestResults.Fail(RestCodes.ServiceUnavailable, new { What = "config" });
			}

			JsonObject grouped = flags.ServiceConfigFor(GenOnlineService.Flags.OfrepController.ContextOf(HttpContext, m_environment));
			return RestResults.Ok(new ConfigResponse
			{
				ServiceConfig = GenOnlineService.Flags.Flagd.Element(grouped),
				Motd = await ConfigSources.ReadMotdAsync(m_dbFactory)
			});
		}
	}
}
