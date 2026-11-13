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
using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace GenOnlineService.Controllers
{
	[ApiController]
	[Authorize(Roles = "GameClient")]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class ServiceConfigController : ControllerBase
	{
		public ServiceConfigController()
		{
			
		}

		[HttpGet(Name = "GetServiceConfig")]

		public async Task<string?> Get()
		{
			try
			{
				string strFileData = await System.IO.File.ReadAllTextAsync(Path.Combine("data", "serviceconfig.json"));

				Response.StatusCode = (int)HttpStatusCode.OK;
				return strFileData;
			}
			catch
			{
				Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				return null;
			}
		}
	}

	[ApiController]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class AnticheatConfigController : ControllerBase
	{
		public AnticheatConfigController()
		{

		}

		[HttpGet(Name = "GetAnticheatConfig")]

		public async Task<string?> Get()
		{
			try
			{
				string strFileData = await System.IO.File.ReadAllTextAsync(Path.Combine("data", "anticheatconfig.dat"));

				// 0 = normal behavior
				// 1 = force goac
				// 2 = force eac

				Response.StatusCode = (int)HttpStatusCode.OK;
				return strFileData;
			}
			catch
			{
				Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				return null;
			}
		}
	}
}