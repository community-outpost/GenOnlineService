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
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService.Rest
{
	// Service accounts and their tokens for holders of tokens.manage; privileged grants and tokens stay CLI only. Every
	// route also takes the staff pages' sign-in cookie
	[Authorize(Policy = PermissionPolicies.Prefix + Permissions.TokensManage)]
	public sealed class ServiceAccountsController : ApiTokenAdminControllerBase
	{
		public ServiceAccountsController(IDbContextFactory<AppDbContext> dbFactory, IConfiguration configuration) : base(dbFactory, configuration)
		{
		}

		[AllowSignInCookie]
		[HttpGet("~/api/v1/service_accounts")]
		public async Task<IActionResult> List()
		{
			if (RequireWebSession() is IActionResult denied)
			{
				return denied;
			}

			(OffsetPage page, Failure? badPage) = Paging.ReadOffset(Request);
			if (badPage != null)
			{
				return RestResults.Fail(badPage.Value);
			}

			await using AppDbContext db = await DbFactory.CreateDbContextAsync();
			int total = await db.ServiceAccounts.CountAsync();
			List<ServiceAccount> accounts = await db.ServiceAccounts.AsNoTracking().OrderBy(a => a.Name).Skip(page.Skip).Take(page.PerPage).ToListAsync();
			List<Int64> ids = accounts.Select(a => a.ID).ToList();
			ILookup<Int64, string> grants = (await db.ServiceAccountPermissions.AsNoTracking().Where(p => ids.Contains(p.ServiceAccountID)).ToListAsync())
				.ToLookup(p => p.ServiceAccountID, p => p.Permission);

			Dictionary<Int64, TokenSummary> summaries = await SummariesOf(db, ids);

			Paging.WriteOffsetHeaders(HttpContext, page, total);
			return RestResults.Ok(new ServiceAccountsResponse { ServiceAccounts = accounts.Select(a => InfoOf(a, grants[a.ID], summaries.GetValueOrDefault(a.ID))).ToList() });
		}

		[AllowSignInCookie]
		[HttpGet("~/api/v1/service_accounts/{accountID:long}")]
		public async Task<IActionResult> Get(long accountID)
		{
			if (RequireWebSession() is IActionResult denied)
			{
				return denied;
			}

			await using AppDbContext db = await DbFactory.CreateDbContextAsync();
			ServiceAccount? account = await db.ServiceAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.ID == accountID);
			if (account == null)
			{
				return RestResults.NotFound("service_account");
			}

			List<string> grants = await db.ServiceAccountPermissions.AsNoTracking().Where(p => p.ServiceAccountID == accountID).Select(p => p.Permission).ToListAsync();
			return RestResults.Ok(InfoOf(account, grants, (await SummariesOf(db, [accountID])).GetValueOrDefault(accountID)));
		}

		[AllowSignInCookie]
		[HttpPost("~/api/v1/service_accounts")]
		public async Task<IActionResult> Create()
		{
			if (RequireWebSession() is IActionResult denied)
			{
				return denied;
			}

			(CreateServiceAccountRequest? body, Failure? malformed) = await ReadBodyAsync<CreateServiceAccountRequest>();
			if (malformed != null)
			{
				return RestResults.Fail(malformed.Value);
			}

			await using AppDbContext db = await DbFactory.CreateDbContextAsync();
			return await Answer(async () =>
			{
				try
				{
					ServiceAccount account = await ApiTokenAdmin.CreateServiceAccount(db, Actor, body?.Name ?? String.Empty, (body?.Description ?? String.Empty).Trim());
					return RestResults.Created(InfoOf(account, [], default));
				}
				catch (DbUpdateException)
				{
					// a concurrent create of the same name hits the unique index
					return RestResults.Fail(RestCodes.Conflict);
				}
			});
		}

		// Checks every change first, then writes the description, grants, removals, the disable or enable and their audit entries
		// in one save
		[AllowSignInCookie]
		[HttpPatch("~/api/v1/service_accounts/{accountID:long}")]
		public async Task<IActionResult> Update(long accountID)
		{
			if (RequireWebSession() is IActionResult denied)
			{
				return denied;
			}

			(UpdateServiceAccountRequest? body, Failure? malformed) = await ReadBodyAsync<UpdateServiceAccountRequest>();
			if (malformed != null)
			{
				return RestResults.Fail(malformed.Value);
			}

			await using AppDbContext db = await DbFactory.CreateDbContextAsync();
			ServiceAccount? account = await db.ServiceAccounts.AsTracking().FirstOrDefaultAsync(a => a.ID == accountID);
			if (account == null)
			{
				return RestResults.NotFound("service_account");
			}

			List<string> add = (body?.Grants?.Add ?? []).Distinct(StringComparer.Ordinal).ToList();
			List<string> remove = (body?.Grants?.Remove ?? []).Distinct(StringComparer.Ordinal).ToList();
			string? description = body?.Description?.Trim();

			return await Answer(async () =>
			{
				IReadOnlyCollection<string> mine = StaffRoles.PermissionsOf(Me);
				add.ForEach(p => ApiTokenAdmin.CheckHttpGrant(p, mine));
				remove.ForEach(p => ApiTokenAdmin.RequirePermission(p, "grants"));
				if (add.Intersect(remove, StringComparer.Ordinal).Any())
				{
					return RestResults.Invalid("grants");
				}
				// the description check is the last one, so nothing is staged before every change has passed
				if (description != null)
				{
					ApiTokenAdmin.StageDescription(db, Actor, account, description);
				}

				foreach (string permission in add)
				{
					await ApiTokenAdmin.StageGrant(db, Actor, account, permission);
				}
				foreach (string permission in remove)
				{
					await ApiTokenAdmin.StageRevokeGrant(db, Actor, account, permission);
				}
				if (body?.Disabled is bool bDisabled)
				{
					ApiTokenAdmin.StageSetDisabled(db, Actor, account, bDisabled);
				}

				await db.SaveChangesAsync();
				// The change takes effect at once in this instance; other instances follow on the reconcile tick
				await ApiTokenCache.Reload(db);
				List<string> grants = await db.ServiceAccountPermissions.AsNoTracking().Where(p => p.ServiceAccountID == account.ID).Select(p => p.Permission).ToListAsync();
				return RestResults.Ok(InfoOf(account, grants, (await SummariesOf(db, [account.ID])).GetValueOrDefault(account.ID)));
			});
		}

		[AllowSignInCookie]
		[HttpGet("~/api/v1/service_accounts/{accountID:long}/personal_access_tokens")]
		public async Task<IActionResult> ListAccountTokens(long accountID)
		{
			if (RequireWebSession() is IActionResult denied)
			{
				return denied;
			}

			await using AppDbContext db = await DbFactory.CreateDbContextAsync();
			ServiceAccount? account = await db.ServiceAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.ID == accountID);
			return account == null ? RestResults.NotFound("service_account") : await ListTokens(db, ApiTokenOwner.Service(account));
		}

		[AllowSignInCookie]
		[HttpPost("~/api/v1/service_accounts/{accountID:long}/personal_access_tokens")]
		public async Task<IActionResult> CreateToken(long accountID)
		{
			if (RequireWebSession() is IActionResult denied)
			{
				return denied;
			}

			await using AppDbContext db = await DbFactory.CreateDbContextAsync();
			ServiceAccount? account = await db.ServiceAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.ID == accountID);
			return account == null ? RestResults.NotFound("service_account") : await MintToken(db, ApiTokenOwner.Service(account));
		}

		// Same query as the personal token rotate; a token of another owner answers not_found {what: token}
		[AllowSignInCookie]
		[HttpPost("~/api/v1/service_accounts/{accountID:long}/personal_access_tokens/{tokenID:long}/rotate")]
		public async Task<IActionResult> RotateAccountToken(long accountID, long tokenID)
		{
			if (RequireWebSession() is IActionResult denied)
			{
				return denied;
			}

			await using AppDbContext db = await DbFactory.CreateDbContextAsync();
			ApiToken? old = await FindAccountToken(db, accountID, tokenID);
			return old == null ? RestResults.NotFound("token") : await RotateToken(db, old);
		}

		[AllowSignInCookie]
		[HttpDelete("~/api/v1/service_accounts/{accountID:long}/personal_access_tokens/{tokenID:long}")]
		public async Task<IActionResult> RevokeAccountToken(long accountID, long tokenID)
		{
			if (RequireWebSession() is IActionResult denied)
			{
				return denied;
			}

			await using AppDbContext db = await DbFactory.CreateDbContextAsync();
			ApiToken? token = await FindAccountToken(db, accountID, tokenID);
			return token == null ? RestResults.NotFound("token") : await RevokeToken(db, token);
		}

		private static Task<ApiToken?> FindAccountToken(AppDbContext db, long accountID, long tokenID)
		{
			return db.ApiTokens.AsTracking().FirstOrDefaultAsync(t => t.ID == tokenID && t.OwnerServiceID == accountID);
		}

		private readonly record struct TokenSummary(int Active, DateTime? LastUsedAt);

		// The active tokens and the latest token use of each account, counted as the active token limit counts them
		private static async Task<Dictionary<Int64, TokenSummary>> SummariesOf(AppDbContext db, List<Int64> accountIDs)
		{
			DateTime now = DateTime.UtcNow;
			var rows = await db.ApiTokens.AsNoTracking()
				.Where(t => t.OwnerServiceID != null && accountIDs.Contains(t.OwnerServiceID.Value))
				.Select(t => new { Owner = t.OwnerServiceID!.Value, t.ExpiresAt, t.RevokedAt, t.LastUsedAt })
				.ToListAsync();
			return rows.GroupBy(r => r.Owner).ToDictionary(g => g.Key, g => new TokenSummary(
				g.Count(r => r.ExpiresAt > now && (r.RevokedAt == null || r.RevokedAt > now)),
				g.Max(r => r.LastUsedAt)));
		}

		private static ServiceAccountInfo InfoOf(ServiceAccount account, IEnumerable<string> grants, TokenSummary tokens) => new ServiceAccountInfo
		{
			Id = account.ID,
			Name = account.Name,
			Description = String.IsNullOrEmpty(account.Description) ? null : account.Description,
			CreatedAt = account.CreatedAt,
			DisabledAt = account.DisabledAt,
			Permissions = grants.OrderBy(p => p, StringComparer.Ordinal).ToList(),
			ActiveTokens = tokens.Active,
			LastTokenUsedAt = tokens.LastUsedAt
		};
	}
}
