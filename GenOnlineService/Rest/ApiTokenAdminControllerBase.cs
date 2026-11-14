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
using System.Text.Json;

namespace GenOnlineService.Rest
{
	// Token and service account management: only a staff web session, never a gopat- token or a game client session (api-tokens.md section 9)
	[ApiController]
	[Authorize]
	public abstract class ApiTokenAdminControllerBase : ControllerBase
	{
		protected ApiTokenAdminControllerBase(IDbContextFactory<AppDbContext> dbFactory, IConfiguration configuration)
		{
			DbFactory = dbFactory;
			Settings = ApiTokenSettings.Read(configuration);
		}

		protected IDbContextFactory<AppDbContext> DbFactory { get; }

		protected ApiTokenSettings Settings { get; }

		protected Int64 Me => RealtimeClaims.UserID(User);

		protected ApiTokenActor Actor => ApiTokenActor.User(Me);

		// Refuses every caller but a web session with forbidden {permission: tokens.manage}
		protected IActionResult? RequireWebSession()
		{
			return ApiTokenAdmin.CallerOf(User) == ApiTokenCallers.WebSession ? null : RestResults.Fail(Result.Forbidden(Permissions.TokensManage));
		}

		// Personal tokens are only for users holding at least one staff permission
		protected IActionResult? RequireStaffWebSession()
		{
			if (RequireWebSession() is IActionResult denied)
			{
				return denied;
			}

			return StaffRoles.PermissionsOf(Me).Count > 0 ? null : RestResults.Fail(ErrorCodes.Forbidden);
		}

		// Answers a broken rule with its REST failure
		protected static async Task<IActionResult> Answer(Func<Task<IActionResult>> action)
		{
			try
			{
				return await action();
			}
			catch (ApiTokenAdminException ex)
			{
				return RestResults.Fail(ex.Failure);
			}
		}

		// Reads the JSON body with the snake_case options; an empty body is null and a malformed one names the field it broke on
		protected async Task<(T? Body, Failure? Failure)> ReadBodyAsync<T>() where T : class
		{
			try
			{
				using StreamReader reader = new StreamReader(Request.Body);
				string text = await reader.ReadToEndAsync();
				if (String.IsNullOrWhiteSpace(text))
				{
					return (null, null);
				}
				return (JsonSerializer.Deserialize<T>(text, ApiResults.Json), null);
			}
			catch (JsonException ex)
			{
				string field = ex.Path is string path && path.StartsWith("$.", StringComparison.Ordinal) ? path.Substring(2) : "body";
				return (null, Result.Invalid(field));
			}
		}

		protected string? QueryValue(string name) => Request.Query.TryGetValue(name, out var value) ? value.ToString() : null;

		protected static PersonalAccessTokenInfo InfoOf(ApiToken token) => new PersonalAccessTokenInfo
		{
			Id = token.ID,
			Name = token.Name,
			Scopes = ApiTokenFormat.ParseScopes(token.Scopes),
			CreatedAt = token.CreatedAt,
			ExpiresAt = token.ExpiresAt,
			LastUsedAt = token.LastUsedAt,
			LastUsedIp = token.LastUsedIP,
			RevokedAt = token.RevokedAt,
			TokenLast4 = token.TokenLast4
		};

		// The only answer that carries the secret
		protected static CreatedPersonalAccessToken CreatedOf(ApiToken token, string secret) => new CreatedPersonalAccessToken
		{
			Id = token.ID,
			Name = token.Name,
			Scopes = ApiTokenFormat.ParseScopes(token.Scopes),
			CreatedAt = token.CreatedAt,
			ExpiresAt = token.ExpiresAt,
			LastUsedAt = token.LastUsedAt,
			LastUsedIp = token.LastUsedIP,
			RevokedAt = token.RevokedAt,
			TokenLast4 = token.TokenLast4,
			Token = secret
		};

		// A paged list of an owner's tokens, newest first
		protected async Task<IActionResult> ListTokens(AppDbContext db, ApiTokenOwner owner)
		{
			(OffsetPage page, Failure? badPage) = Paging.ReadOffset(Request);
			if (badPage != null)
			{
				return RestResults.Fail(badPage.Value);
			}

			IQueryable<ApiToken> query = ApiTokenAdmin.OwnedBy(db.ApiTokens.AsNoTracking(), owner);
			int total = await query.CountAsync();
			List<ApiToken> tokens = await query.OrderByDescending(t => t.ID).Skip(page.Skip).Take(page.PerPage).ToListAsync();
			Paging.WriteOffsetHeaders(HttpContext, page, total);
			return RestResults.Ok(new PersonalAccessTokensResponse { PersonalAccessTokens = tokens.Select(InfoOf).ToList() });
		}

		// Mints a token for the owner from a POST body; privileged scopes stay CLI only
		protected async Task<IActionResult> MintToken(AppDbContext db, ApiTokenOwner owner)
		{
			(CreatePersonalAccessTokenRequest? body, Failure? malformed) = await ReadBodyAsync<CreatePersonalAccessTokenRequest>();
			if (malformed != null)
			{
				return RestResults.Fail(malformed.Value);
			}

			body ??= new CreatePersonalAccessTokenRequest();
			List<string> scopes = ApiTokenFormat.ParseScopes(String.Join(' ', body.Scopes ?? []));
			string? allowedIPs = body.AllowedIps is { Count: > 0 } ips ? String.Join(',', ips) : null;

			return await Answer(async () =>
			{
				(ApiToken token, string secret) = await ApiTokenAdmin.Mint(db, Settings, Actor, owner, (body.Name ?? String.Empty).Trim(), scopes, allowedIPs,
					() => ApiTokenAdmin.ParseExpiresAt(body.ExpiresAt), bFromHost: false);
				// The change takes effect at once in this instance; other instances follow on the reconcile tick
				await ApiTokenCache.Reload(db);
				return RestResults.Created(CreatedOf(token, secret));
			});
		}
	}
}
