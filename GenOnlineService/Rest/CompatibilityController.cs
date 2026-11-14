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

using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GenOnlineService.Rest
{
	// The version check runs before sign-in, so it is anonymous; the build check is POST /api/v1/auth/session
	[ApiController]
	[AllowAnonymous]
	public sealed class CompatibilityController : ControllerBase
	{
		[HttpGet("~/api/v1/compatibility")]
		public async Task<IActionResult> Get()
		{
			Result<CompatibilityResponse> result = await Compatibility.CheckAsync(Request.Query["client"].FirstOrDefault(), Request.Query["version"].FirstOrDefault());
			return result.Ok ? RestResults.Ok(result.Data!) : RestResults.Fail(new Failure(result.Code!, result.Params!));
		}
	}
}
