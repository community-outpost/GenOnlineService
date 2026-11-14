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

using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Client.AspNetCore;
using OpenIddict.Server.AspNetCore;
using System.Collections.Immutable;
using System.Net;
using System.Security.Claims;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GenOnlineService.Identity
{
	// The accounts profile: sign-in pages, upstream providers and the OpenIddict passthrough endpoints
	// Not an [ApiController]: cookie sign-in must redirect to the sign-in page instead of answering 401
	[ApiExplorerSettings(IgnoreApi = true)]
	public sealed class AccountsController : ControllerBase
	{
		private static readonly ILogger s_log = AppLog.For(typeof(AccountsController));

		private readonly AccountsSettings m_settings;
		private readonly SignInManager<User> m_signIn;
		private readonly UserManager<User> m_users;
		private readonly UpstreamAccounts m_upstream;
		private readonly IOpenIddictApplicationManager m_applications;
		private readonly IOpenIddictAuthorizationManager m_authorizations;

		public AccountsController(AccountsSettings settings, SignInManager<User> signIn, UserManager<User> users, UpstreamAccounts upstream,
			IOpenIddictApplicationManager applications, IOpenIddictAuthorizationManager authorizations)
		{
			m_settings = settings;
			m_signIn = signIn;
			m_users = users;
			m_upstream = upstream;
			m_applications = applications;
			m_authorizations = authorizations;
		}

		private string SafeReturnUrl(string? returnUrl) => !String.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";

		private ContentResult Page(int status, string title, string body)
		{
			return new ContentResult
			{
				StatusCode = status,
				ContentType = "text/html; charset=utf-8",
				Content = $"<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>{WebUtility.HtmlEncode(title)}</title></head><body><h1>{WebUtility.HtmlEncode(title)}</h1>{body}</body></html>"
			};
		}

		[HttpGet("~/users/sign_in")]
		public IActionResult SignInPage([FromQuery(Name = "ReturnUrl")] string? returnUrl)
		{
			string target = SafeReturnUrl(returnUrl);
			List<UpstreamProviderSettings> providers = m_settings.EnabledProviders.ToList();

			UpstreamProviderSettings? only = providers.FirstOrDefault(p => p.OnlyProvider);
			if (only != null)
			{
				return Redirect($"/users/auth/{Uri.EscapeDataString(only.Name)}?returnUrl={Uri.EscapeDataString(target)}");
			}

			if (providers.Count == 0)
			{
				return Page(503, "Sign-in unavailable", "<p>No sign-in method is configured for this environment.</p>");
			}

			string links = String.Concat(providers.Select(p =>
				$"<p><a href=\"/users/auth/{Uri.EscapeDataString(p.Name)}?returnUrl={Uri.EscapeDataString(target)}\">Sign in with {WebUtility.HtmlEncode(p.DisplayName)}</a></p>"));
			return Page(200, "Sign in", links);
		}

		[HttpGet("~/users/auth/{provider}")]
		public IActionResult Challenge(string provider, [FromQuery] string? returnUrl)
		{
			UpstreamProviderSettings? settings = m_settings.EnabledProviders.FirstOrDefault(p => p.Name == provider);
			if (settings == null)
			{
				return NotFound();
			}

			var properties = new AuthenticationProperties(new Dictionary<string, string?>
			{
				[OpenIddictClientAspNetCoreConstants.Properties.ProviderName] = settings.Name
			})
			{
				RedirectUri = SafeReturnUrl(returnUrl)
			};

			return Challenge(properties, OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
		}

		[AcceptVerbs("GET", "POST", Route = "~/users/auth/{provider}/callback")]
		[IgnoreAntiforgeryToken]
		public async Task<IActionResult> Callback(string provider)
		{
			UpstreamProviderSettings? settings = m_settings.EnabledProviders.FirstOrDefault(p => p.Name == provider);
			if (settings == null)
			{
				return NotFound();
			}

			AuthenticateResult result = await HttpContext.AuthenticateAsync(OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
			if (!result.Succeeded || result.Principal == null)
			{
				s_log.LogWarning("Upstream sign-in with {Provider} failed: {Error}", provider, result.Failure?.Message);
				AppMetrics.RecordAuthentication("upstream", provider, "failed");
				return Page(401, "Sign-in failed", "<p>The sign-in could not be completed. Please try again.</p>");
			}

			// the state token names the provider the flow started with; another callback path must not finish it
			if (result.Properties?.Items.TryGetValue(OpenIddictClientAspNetCoreConstants.Properties.ProviderName, out string? started) == true && started != provider)
			{
				return BadRequest();
			}

			UpstreamSignInResult signIn = await m_upstream.SignIn(settings, result.Principal);
			AppMetrics.RecordAuthentication("upstream", provider, signIn.User != null ? "success" : signIn.Error ?? "refused");

			if (signIn.User == null)
			{
				string message = signIn.Error switch
				{
					UpstreamAccounts.ErrorBlocked => "This account is blocked.",
					UpstreamAccounts.ErrorDeactivated => "This account is deactivated.",
					UpstreamAccounts.ErrorSignUpDisabled => "No account is linked to this sign-in, and sign-up is closed.",
					_ => "The sign-in could not be completed."
				};
				return Page(403, "Sign-in refused", $"<p>{WebUtility.HtmlEncode(message)}</p>");
			}

			await m_signIn.SignInAsync(signIn.User, isPersistent: false, authenticationMethod: provider);
			return LocalRedirect(SafeReturnUrl(result.Properties?.RedirectUri));
		}

		[HttpPost("~/users/sign_out")]
		public async Task<IActionResult> SignOutPage()
		{
			await m_signIn.SignOutAsync();
			return Page(200, "Signed out", "<p>You are signed out.</p>");
		}

		[AcceptVerbs("GET", "POST", Route = "~/oauth/authorize")]
		[IgnoreAntiforgeryToken]
		public async Task<IActionResult> Authorize()
		{
			OpenIddictRequest request = HttpContext.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

			AuthenticateResult cookie = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
			User? user = cookie.Succeeded ? await m_users.GetUserAsync(cookie.Principal!) : null;

			if (user == null || request.HasPromptValue(PromptValues.Login))
			{
				if (request.HasPromptValue(PromptValues.None))
				{
					return Forbid(Error(Errors.LoginRequired, "The user is not signed in."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
				}

				string query = QueryString.Create((Request.HasFormContentType ? Request.Form.ToList() : Request.Query.ToList()).Where(p => p.Key != Parameters.Prompt).ToList()).ToString();
				return Challenge(new AuthenticationProperties { RedirectUri = Request.PathBase + Request.Path + query }, IdentityConstants.ApplicationScheme);
			}

			// a blocked or deactivated account gets no tokens; a ban does not stop signing in
			if (user.State != UserStates.Active)
			{
				await m_signIn.SignOutAsync();
				return Forbid(Error(Errors.AccessDenied, "This account cannot sign in."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
			}

			object application = await m_applications.FindByClientIdAsync(request.ClientId!) ?? throw new InvalidOperationException("Unknown client.");
			string? applicationID = await m_applications.GetIdAsync(application);

			ClaimsIdentity identity = BuildIdentity(user, request.GetScopes());

			// one authorization per sign-in, so revoking it ends exactly that device's refresh chain
			object authorization = await m_authorizations.CreateAsync(
				principal: new ClaimsPrincipal(identity),
				subject: user.ID.ToString(),
				client: applicationID!,
				type: AuthorizationTypes.Permanent,
				scopes: identity.GetScopes());
			identity.SetAuthorizationId(await m_authorizations.GetIdAsync(authorization));

			return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
		}

		[HttpPost("~/oauth/token")]
		[IgnoreAntiforgeryToken]
		public async Task<IActionResult> Exchange()
		{
			OpenIddictRequest request = HttpContext.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");
			if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
			{
				return Forbid(Error(Errors.UnsupportedGrantType, "The grant type is not supported."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
			}

			AuthenticateResult result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
			User? user = result.Principal == null ? null : await m_users.FindByIdAsync(result.Principal.GetClaim(Claims.Subject) ?? String.Empty);

			if (user == null || user.State != UserStates.Active)
			{
				return Forbid(Error(Errors.InvalidGrant, "The token is no longer valid."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
			}

			// a play ban ends the game clients' sessions; the website keeps working so the player can appeal
			if (request.IsRefreshTokenGrantType() && SessionRevocation.IsGameClient(request.ClientId) && AccessControl.GetPlayBan(user.ID) != null)
			{
				return Forbid(Error(Errors.InvalidGrant, "The account is banned from playing."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
			}

			// the name is read again so a rename reaches the next access token
			ClaimsIdentity identity = BuildIdentity(user, result.Principal!.GetScopes());
			identity.SetAuthorizationId(result.Principal!.GetAuthorizationId());
			return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
		}

		[AcceptVerbs("GET", "POST", Route = "~/oauth/userinfo")]
		[IgnoreAntiforgeryToken]
		public async Task<IActionResult> UserInfo()
		{
			AuthenticateResult result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
			User? user = result.Principal == null ? null : await m_users.FindByIdAsync(result.Principal.GetClaim(Claims.Subject) ?? String.Empty);
			if (user == null || user.State != UserStates.Active)
			{
				return Challenge(Error(Errors.InvalidToken, "The token is no longer valid."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
			}

			return Ok(new Dictionary<string, object>
			{
				[Claims.Subject] = user.ID.ToString(),
				[Claims.Name] = user.Username,
				[Claims.PreferredUsername] = user.Username
			});
		}

		private static AuthenticationProperties Error(string error, string description)
		{
			return new AuthenticationProperties(new Dictionary<string, string?>
			{
				[OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
				[OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
			});
		}

		// Access tokens carry the user id, the name and the scopes; never roles, the API reads those itself
		private ClaimsIdentity BuildIdentity(User user, ImmutableArray<string> requestedScopes)
		{
			var identity = new ClaimsIdentity(TokenValidationParametersDefaults.AuthenticationType, Claims.Name, Claims.Role);
			identity.SetClaim(Claims.Subject, user.ID.ToString())
				.SetClaim(Claims.Name, user.Username)
				.SetClaim(Claims.PreferredUsername, user.Username);

			var allowed = new HashSet<string>(StringComparer.Ordinal) { Scopes.OpenId, Scopes.Profile, Scopes.OfflineAccess, AccountsSettings.GameScope };
			identity.SetScopes(requestedScopes.Where(allowed.Contains));
			identity.SetAudiences(m_settings.Audience);
			identity.SetDestinations(claim => claim.Type switch
			{
				Claims.Name or Claims.PreferredUsername => new[] { Destinations.AccessToken, Destinations.IdentityToken },
				_ => new[] { Destinations.AccessToken }
			});

			return identity;
		}

		private static class TokenValidationParametersDefaults
		{
			public const string AuthenticationType = "accounts";
		}
	}
}
