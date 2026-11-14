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

namespace GenOnlineService.Rest
{
	// The public keys of the EOS ID tokens; 404 while the issuer is not configured
	[ApiController]
	[AllowAnonymous]
	public sealed class JwksController : ControllerBase
	{
		[HttpGet("/.well-known/jwks.json")]
		public IActionResult Get()
		{
			EosTokenIssuer.Settings? settings = EosTokenIssuer.GetSettings();
			if (settings == null)
			{
				return NotFound();
			}

			Response.Headers.CacheControl = "public, max-age=300";
			return new JsonResult(new
			{
				keys = settings.PublishedKeys.Select(k => new { kty = k.Kty, use = k.Use, alg = k.Alg, kid = k.Kid, n = k.N, e = k.E })
			});
		}
	}
}
