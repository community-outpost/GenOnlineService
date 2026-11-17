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

using BlazorBlueprint.Components;
using GenOnlineService.Api;
using GenOnlineService.Identity;
using GenOnlineService.Portal.Client;
using GenOnlineService.Realtime;
using GenOnlineService.Rest;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Security.Claims;
using System.Text.Json;

namespace GenOnlineService.Portal
{
	// Each environment's own staff pages (S12): the WebAssembly pages under /admin and their cookie endpoints under
	// /admin/api. The sign-in pages and /profile belong to AccountsController; nothing here serves players.
	public static partial class PortalSetup
	{
		private const string AdminApi = "/admin/api";
		private const int MaximumPageSize = 100;

		// The error code of a staff password account that has not set up two-factor sign-in yet
		public const string TwoFactorRequired = "two_factor_required";

		private static readonly (EUserSessionType SessionType, string Kind)[] s_sessionKinds =
		{
			(EUserSessionType.GameClient, "game"),
			(EUserSessionType.GameLauncher, "launcher"),
			(EUserSessionType.ChatClient, "chat")
		};

		public static void AddPortal(this IServiceCollection services)
		{
			// WebAssembly rendering only, so no visitor ever holds a Blazor Server circuit in this process
			services.AddRazorComponents().AddInteractiveWebAssemblyComponents();
			services.AddBlazorBlueprintComponents();
		}

		// Runs after UseAuthorization: gates the staff pages by the sign-in cookie
		public static void UsePortal(this IApplicationBuilder app)
		{
			app.Use(GatePages);
			app.UseAntiforgery();
		}

		// After MapStaticAssets, which serves the WebAssembly files (_framework) and the stylesheets
		public static void MapPortal(this IEndpointRouteBuilder app)
		{
			app.MapGroup(AdminApi).AddEndpointFilter(RequireSignIn).MapGet("/session", GetSession);

			RouteGroupBuilder admin = app.MapGroup(AdminApi).AddEndpointFilter(RequireSignIn).AddEndpointFilter(RequireStaff);
			admin.MapGet("/reports", GetReports);
			admin.MapPatch("/reports/{reportID:long}", ResolveReport);
			MapAdmin(admin);

			app.MapRazorComponents<PortalApp>()
				.AddInteractiveWebAssemblyRenderMode()
				.AddAdditionalAssemblies(typeof(Routes).Assembly);
		}

		// moderation.kick or higher, the rule of GET /api/v1/reports
		public static Task<bool> IsStaffAsync(IAuthorizationService authorization, ClaimsPrincipal user) => StaffAccess.IsStaffAsync(authorization, user);

		// A page under /admin needs the sign-in cookie and a staff permission, as the API under it does
		private static async Task GatePages(HttpContext context, RequestDelegate next)
		{
			PathString path = context.Request.Path;
			if (!path.StartsWithSegments(PortalRoutes.AdminPages) || path.StartsWithSegments(AdminApi))
			{
				await next(context);
				return;
			}

			if (!await SignInAsync(context))
			{
				// the scheme redirects to the sign-in page with this URL as ReturnUrl
				await context.ChallengeAsync(SignInScheme, new AuthenticationProperties { RedirectUri = context.Request.PathBase + path + context.Request.QueryString });
				return;
			}

			if (!await IsStaffAsync(context.RequestServices.GetRequiredService<IAuthorizationService>(), context.User))
			{
				await WriteRefusal(context, StatusCodes.Status403Forbidden, "Staff only", "Your account holds no staff permission.");
				return;
			}

			// staff with a password set up two-factor sign-in before they act here, as before any token is issued
			if (await MustSetUpTwoFactorAsync(context))
			{
				string back = context.Request.PathBase + path + context.Request.QueryString;
				context.Response.Redirect("/profile/two_factor_auth?ReturnUrl=" + Uri.EscapeDataString(back));
				return;
			}

			// the shell holds no data, the browser asks for it; still never cache or frame a staff page
			context.Response.Headers.CacheControl = "no-store";
			context.Response.Headers["X-Frame-Options"] = "DENY";
			context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
			await next(context);
		}

		// The one place the staff area authenticates, for its pages and its API alike: the Identity application cookie for now.
		// A later portal cookie (signing in as an OAuth client of the login component) replaces the scheme here only.
		private static readonly string SignInScheme = IdentityConstants.ApplicationScheme;

		// Sets context.User to the signed-in account; false without a sign-in that names a player
		private static async Task<bool> SignInAsync(HttpContext context)
		{
			AuthenticateResult result = await context.AuthenticateAsync(SignInScheme);
			if (!result.Succeeded || result.Principal == null || RealtimeClaims.UserID(result.Principal) <= 0)
			{
				return false;
			}
			context.User = result.Principal;
			return true;
		}

		// The refusal in the sign-in pages' look, with the way back to the player page
		private static async Task WriteRefusal(HttpContext context, int status, string title, string message)
		{
			context.Response.StatusCode = status;
			context.Response.ContentType = "text/html; charset=utf-8";
			context.Response.Headers.CacheControl = "no-store";
			string body = PortalLook.Text(WebUtility.HtmlEncode(message)) + PortalLook.Link(PortalRoutes.AccountPage, "Back to your account");
			await context.Response.WriteAsync(PortalLook.Document(title, body));
		}

		// Identity rebuilds the cookie every minute and its claims are not evidence, so the account itself is read
		private static async Task<bool> MustSetUpTwoFactorAsync(HttpContext context)
		{
			User? user = await context.RequestServices.GetRequiredService<UserManager<User>>().GetUserAsync(context.User);
			return user == null || await context.RequestServices.GetRequiredService<PasswordAccounts>().MustSetUpTwoFactor(user);
		}

		// The portal endpoints accept the sign-in cookie only, and every change needs the anti-forgery token
		private static async ValueTask<object?> RequireSignIn(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
		{
			HttpContext context = invocation.HttpContext;
			context.Response.Headers.CacheControl = "no-store";
			if (!await SignInAsync(context))
			{
				return Fail(StatusCodes.Status401Unauthorized, "unauthorized");
			}

			// the token is bound to the signed-in user, so it is checked as that user
			if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
			{
				try
				{
					await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
				}
				catch (AntiforgeryValidationException)
				{
					return Fail(StatusCodes.Status400BadRequest, "csrf");
				}
			}

			return await next(invocation);
		}

		private static async ValueTask<object?> RequireStaff(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
		{
			HttpContext context = invocation.HttpContext;
			if (!await IsStaffAsync(context.RequestServices.GetRequiredService<IAuthorizationService>(), context.User))
			{
				return Answer(Result.Forbidden(Permissions.ModerationKick));
			}
			if (await MustSetUpTwoFactorAsync(context))
			{
				return Fail(StatusCodes.Status403Forbidden, TwoFactorRequired);
			}
			return await next(invocation);
		}

		private static async Task<IResult> GetSession(HttpContext context, IAntiforgery antiforgery, IAuthorizationService authorization, IDbContextFactory<AppDbContext> dbFactory)
		{
			Int64 me = RealtimeClaims.UserID(context.User);
			(string Name, int Tag)? name;
			await using (AppDbContext db = await dbFactory.CreateDbContextAsync())
			{
				name = await Database.DisplayNames.Get(db, me);
			}

			AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
			return Json(new PortalSessionInfo
			{
				UserId = me,
				DisplayName = name?.Name ?? String.Empty,
				Tag = PlayerTags.Of(name?.Tag ?? 0),
				Roles = StaffRoles.RolesOf(me).Order(StringComparer.Ordinal).ToList(),
				Permissions = StaffRoles.PermissionsOf(me).Order(StringComparer.Ordinal).ToList(),
				Staff = await IsStaffAsync(authorization, context.User),
				CsrfHeader = tokens.HeaderName ?? String.Empty,
				CsrfToken = tokens.RequestToken ?? String.Empty
			});
		}

		// The same action as PATCH /api/v1/reports/:id
		private static async Task<IResult> ResolveReport(HttpContext context, IDbContextFactory<AppDbContext> dbFactory, long reportID)
		{
			PortalResolveRequest? body = await ReadAsync<PortalResolveRequest>(context);
			var request = new ResolveReportRequest { Status = body?.Status, ResolutionNote = body?.ResolutionNote };
			return Answer(await MessageActions.ResolveReportAsync(dbFactory, RealtimeClaims.UserID(context.User), reportID, request));
		}

		// A malformed body reads as an empty one, the action then names the missing field
		private static async Task<T?> ReadAsync<T>(HttpContext context) where T : class
		{
			try
			{
				return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, PortalJson.Options, context.RequestAborted);
			}
			catch (JsonException)
			{
				return null;
			}
		}

		private static IResult Json<T>(T body) => Results.Json(body, PortalJson.Options);

		// 204 on success, otherwise the {error, params} body and status of /api/v1
		private static IResult Answer(Result<Empty> result) => result.Ok ? Results.NoContent() : Answer(new Failure(result.Code!, result.Params!));

		private static IResult Answer(Failure failure) => Results.Json(ApiResults.BodyOf(failure), ApiResults.Json, statusCode: ApiResults.StatusOf(failure.Code));

		private static IResult Fail(int status, string code) => Results.Json(new PortalError { Error = code }, PortalJson.Options, statusCode: status);
	}
}
