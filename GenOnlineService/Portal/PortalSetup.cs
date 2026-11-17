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
	// What the host page of the staff pages carries for the browser: who is signed in (PortalSessionInfo as JSON) and
	// the antiforgery header and token every /api/v1 change through the sign-in cookie needs
	public sealed record PortalHostData(string Session, string CsrfHeader, string CsrfToken);

	// Each environment's own staff pages (S12): the WebAssembly pages under /admin, which call /api/v1 with the sign-in
	// cookie (T16). The sign-in pages and /profile belong to AccountsController; nothing here serves players.
	public static class PortalSetup
	{
		private const string HostDataItem = "go_portal_host";

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
			app.MapRazorComponents<PortalApp>()
				.AddInteractiveWebAssemblyRenderMode()
				.AddAdditionalAssemblies(typeof(Routes).Assembly);
		}

		// moderation.kick or higher, the rule of GET /api/v1/reports
		public static Task<bool> IsStaffAsync(IAuthorizationService authorization, ClaimsPrincipal user) => StaffAccess.IsStaffAsync(authorization, user);

		// The data PortalDocument writes into the host page, set by the gate for a staff member only
		public static PortalHostData? HostDataOf(HttpContext? context) => context?.Items[HostDataItem] as PortalHostData;

		// A page under /admin needs the sign-in cookie and a staff permission, as the /api/v1 routes it calls do
		private static async Task GatePages(HttpContext context, RequestDelegate next)
		{
			PathString path = context.Request.Path;
			if (!path.StartsWithSegments(PortalRoutes.AdminPages))
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

			IAuthorizationService authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();
			if (!await IsStaffAsync(authorization, context.User))
			{
				await WriteRefusal(context, StatusCodes.Status403Forbidden, "Staff only", "Your account holds no staff permission.");
				return;
			}

			// staff with a password set up two-factor sign-in before they act here, as before any token is issued
			if (await ApiAuthentication.MustSetUpTwoFactorAsync(context))
			{
				string back = context.Request.PathBase + path + context.Request.QueryString;
				context.Response.Redirect("/profile/two_factor_auth?ReturnUrl=" + Uri.EscapeDataString(back));
				return;
			}

			// the token is made before the response starts, so its cookie can still be set
			context.Items[HostDataItem] = await HostDataAsync(context);

			// never cache or frame a staff page, its head carries the antiforgery token
			context.Response.Headers.CacheControl = "no-store";
			context.Response.Headers["X-Frame-Options"] = "DENY";
			context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
			await next(context);
		}

		// The one place the staff area authenticates, for its pages and for the /api/v1 routes they call (SignInCookieHandler):
		// the Identity application cookie for now. A later portal cookie (signing in as an OAuth client of the login
		// component) replaces the scheme here only.
		public static readonly string SignInScheme = IdentityConstants.ApplicationScheme;

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

		// Who is signed in, for the shell's header and for hiding what the account may not do; /api/v1 checks every call again
		private static async Task<PortalHostData> HostDataAsync(HttpContext context)
		{
			Int64 me = RealtimeClaims.UserID(context.User);
			(string Name, int Tag)? name;
			await using (AppDbContext db = await context.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(context.RequestAborted))
			{
				name = await Database.DisplayNames.Get(db, me);
			}

			var session = new PortalSessionInfo
			{
				UserId = me,
				DisplayName = name?.Name ?? String.Empty,
				Tag = PlayerTags.Of(name?.Tag ?? 0),
				Roles = StaffRoles.RolesOf(me).Order(StringComparer.Ordinal).ToList(),
				Permissions = StaffRoles.PermissionsOf(me).Order(StringComparer.Ordinal).ToList(),
				// the gate let only staff through
				Staff = true
			};
			AntiforgeryTokenSet tokens = context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
			return new PortalHostData(JsonSerializer.Serialize(session, PortalJson.Options), tokens.HeaderName ?? String.Empty, tokens.RequestToken ?? String.Empty);
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
	}
}
