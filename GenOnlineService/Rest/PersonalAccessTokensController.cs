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

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService.Rest
{
	// The caller's own personal access tokens; another user's token id answers not_found {what: token}
	public sealed class PersonalAccessTokensController : ApiTokenAdminControllerBase
	{
		public PersonalAccessTokensController(IDbContextFactory<AppDbContext> dbFactory, IConfiguration configuration) : base(dbFactory, configuration)
		{
		}

		[HttpGet("~/api/v1/personal_access_tokens")]
		public async Task<IActionResult> List()
		{
			if (RequireStaffWebSession() is IActionResult denied)
			{
				return denied;
			}

			await using AppDbContext db = await DbFactory.CreateDbContextAsync();
			return await ListTokens(db, ApiTokenOwner.User(Me));
		}

		[HttpPost("~/api/v1/personal_access_tokens")]
		public async Task<IActionResult> Create()
		{
			if (RequireStaffWebSession() is IActionResult denied)
			{
				return denied;
			}

			await using AppDbContext db = await DbFactory.CreateDbContextAsync();
			return await MintToken(db, ApiTokenOwner.User(Me));
		}

		// Describes the gopat- token making the request; any other caller has no such token
		[HttpGet("~/api/v1/personal_access_tokens/self")]
		public async Task<IActionResult> Self()
		{
			Int64? tokenID = ApiTokenAdmin.CallerOf(User) == ApiTokenCallers.Token ? GenOnlineService.ApiTokenPrincipals.TokenID(User) : null;
			if (tokenID == null)
			{
				return RestResults.NotFound("token");
			}

			await using AppDbContext db = await DbFactory.CreateDbContextAsync();
			ApiToken? token = await db.ApiTokens.AsNoTracking().FirstOrDefaultAsync(t => t.ID == tokenID);
			return token == null ? RestResults.NotFound("token") : RestResults.Ok(InfoOf(token));
		}

		// ?overlap_minutes= (0 to 1440, default 0) keeps the old token working that long; ?expires_at= replaces the old lifetime
		[HttpPost("~/api/v1/personal_access_tokens/{tokenID:long}/rotate")]
		public async Task<IActionResult> Rotate(long tokenID)
		{
			if (RequireStaffWebSession() is IActionResult denied)
			{
				return denied;
			}

			await using AppDbContext db = await DbFactory.CreateDbContextAsync();
			ApiToken? old = await FindOwn(db, tokenID);
			if (old == null)
			{
				return RestResults.NotFound("token");
			}

			return await Answer(async () =>
			{
				(ApiToken token, string secret) = await ApiTokenAdmin.Rotate(db, Settings, Actor, old,
					() => ApiTokenAdmin.CheckOverlap(QueryValue("overlap_minutes")),
					() => QueryValue("expires_at") is string text ? ApiTokenAdmin.ParseExpiresAt(text) : null);
				// The change takes effect at once in this instance; other instances follow on the reconcile tick
				await ApiTokenCache.Reload(db);
				return RestResults.Created(CreatedOf(token, secret));
			});
		}

		// Revoking needs no staff permission, so a user who lost their role can still stop their own tokens
		[HttpDelete("~/api/v1/personal_access_tokens/{tokenID:long}")]
		public async Task<IActionResult> Revoke(long tokenID)
		{
			if (RequireWebSession() is IActionResult denied)
			{
				return denied;
			}

			await using AppDbContext db = await DbFactory.CreateDbContextAsync();
			ApiToken? token = await FindOwn(db, tokenID);
			if (token == null)
			{
				return RestResults.NotFound("token");
			}

			return await Answer(async () =>
			{
				await ApiTokenAdmin.Revoke(db, Actor, token, String.Empty);
				// The change takes effect at once in this instance; other instances follow on the reconcile tick
				await ApiTokenCache.Reload(db);
				return NoContent();
			});
		}

		private Task<ApiToken?> FindOwn(AppDbContext db, long tokenID)
		{
			Int64 me = Me;
			return db.ApiTokens.AsTracking().FirstOrDefaultAsync(t => t.ID == tokenID && t.OwnerUserID == me);
		}
	}
}
