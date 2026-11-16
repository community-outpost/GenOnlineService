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
using Microsoft.AspNetCore.Antiforgery;
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
		private readonly PasswordAccounts m_passwords;
		private readonly IOpenIddictApplicationManager m_applications;
		private readonly IOpenIddictAuthorizationManager m_authorizations;

		public AccountsController(AccountsSettings settings, SignInManager<User> signIn, UserManager<User> users, UpstreamAccounts upstream,
			PasswordAccounts passwords, IOpenIddictApplicationManager applications, IOpenIddictAuthorizationManager authorizations)
		{
			m_settings = settings;
			m_signIn = signIn;
			m_users = users;
			m_upstream = upstream;
			m_passwords = passwords;
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

		private const string ReturnUrlField = "ReturnUrl";
		private const string HoneypotField = "website";

		private static string Hidden(string name, string? value) => $"<input type=\"hidden\" name=\"{WebUtility.HtmlEncode(name)}\" value=\"{WebUtility.HtmlEncode(value)}\">";

		// A page with a form: an antiforgery token, never inside another site's frame, never cached
		private string FormStart(string action, string returnUrl)
		{
			AntiforgeryTokenSet tokens = HttpContext.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(HttpContext);
			Response.Headers["X-Frame-Options"] = "DENY";
			Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
			Response.Headers.CacheControl = "no-store";
			return $"<form method=\"post\" action=\"{WebUtility.HtmlEncode(action)}\">{Hidden(tokens.FormFieldName!, tokens.RequestToken)}{Hidden(ReturnUrlField, returnUrl)}";
		}

		private Task<bool> ValidForm() => HttpContext.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(HttpContext);

		private ContentResult Expired() => Page(400, "Request expired", "<p>The page expired. Please go back and try again.</p>");

		private static string Message(string? message) => message == null ? String.Empty : $"<p role=\"alert\">{WebUtility.HtmlEncode(message)}</p>";

		[HttpGet("~/users/sign_in")]
		public IActionResult SignInPage([FromQuery(Name = ReturnUrlField)] string? returnUrl) => SignInForm(200, SafeReturnUrl(returnUrl), null, null);

		private IActionResult SignInForm(int status, string target, string? email, string? message)
		{
			List<UpstreamProviderSettings> providers = m_settings.EnabledProviders.ToList();
			bool passwords = m_settings.Password.Enabled;

			// a provider marked only_provider takes the page over, unless password sign-in is offered too
			UpstreamProviderSettings? only = passwords ? null : providers.FirstOrDefault(p => p.OnlyProvider);
			if (only != null)
			{
				return Redirect($"/users/auth/{Uri.EscapeDataString(only.Name)}?returnUrl={Uri.EscapeDataString(target)}");
			}

			if (providers.Count == 0 && !passwords)
			{
				return Page(503, "Sign-in unavailable", "<p>No sign-in method is configured for this environment.</p>");
			}

			var body = new System.Text.StringBuilder(Message(message));
			if (passwords)
			{
				body.Append(FormStart("/users/sign_in", target));
				body.Append($"<p><label>Email <input type=\"email\" name=\"email\" autocomplete=\"username\" required value=\"{WebUtility.HtmlEncode(email)}\"></label></p>");
				body.Append("<p><label>Password <input type=\"password\" name=\"password\" autocomplete=\"current-password\" required></label></p>");
				body.Append("<p><button type=\"submit\">Sign in</button></p></form>");
				if (m_settings.Password.AllowSignUp)
				{
					body.Append($"<p><a href=\"/users/sign_up?{ReturnUrlField}={Uri.EscapeDataString(target)}\">Create an account</a></p>");
				}
			}
			body.Append(String.Concat(providers.Select(p =>
				$"<p><a href=\"/users/auth/{Uri.EscapeDataString(p.Name)}?returnUrl={Uri.EscapeDataString(target)}\">Sign in with {WebUtility.HtmlEncode(p.DisplayName)}</a></p>")));
			return Page(status, "Sign in", body.ToString());
		}

		[HttpPost("~/users/sign_in")]
		public async Task<IActionResult> SignInWithPassword([FromForm] string? email, [FromForm] string? password, [FromForm(Name = ReturnUrlField)] string? returnUrl)
		{
			if (!m_settings.Password.Enabled)
			{
				return NotFound();
			}
			if (!await ValidForm())
			{
				return Expired();
			}

			string target = SafeReturnUrl(returnUrl);
			PasswordAccountResult result = await m_passwords.SignIn(email, password);
			AppMetrics.RecordAuthentication("password", "password", result.Error ?? "success");

			if (result.Error == null)
			{
				await m_signIn.SignInAsync(result.User!, isPersistent: false, authenticationMethod: "password");
				return LocalRedirect(target);
			}

			return result.Error switch
			{
				PasswordAccounts.ErrorBlocked => Page(403, "Sign-in refused", "<p>This account is blocked.</p>"),
				PasswordAccounts.ErrorDeactivated => Page(403, "Sign-in refused", "<p>This account is deactivated.</p>"),
				PasswordAccounts.ErrorTwoFactorRequired => Page(403, "Sign-in refused", "<p>This account needs a second factor, which this server cannot ask for yet.</p>"),
				_ => SignInForm(401, target, email, "Wrong email or password.")
			};
		}

		[HttpGet("~/users/sign_up")]
		public IActionResult SignUpPage([FromQuery(Name = ReturnUrlField)] string? returnUrl)
		{
			if (!m_settings.Password.Enabled || !m_settings.Password.AllowSignUp)
			{
				return NotFound();
			}
			return SignUpForm(200, SafeReturnUrl(returnUrl), null, null, null);
		}

		private ContentResult SignUpForm(int status, string target, string? displayName, string? email, string? message)
		{
			var body = new System.Text.StringBuilder(Message(message));
			body.Append(FormStart("/users/sign_up", target));
			body.Append($"<p><label>Player name <input type=\"text\" name=\"display_name\" autocomplete=\"nickname\" required value=\"{WebUtility.HtmlEncode(displayName)}\"></label></p>");
			body.Append($"<p><label>Email <input type=\"email\" name=\"email\" autocomplete=\"email\" required value=\"{WebUtility.HtmlEncode(email)}\"></label></p>");
			body.Append($"<p><label>Password <input type=\"password\" name=\"password\" autocomplete=\"new-password\" minlength=\"{m_settings.Password.MinLength}\" maxlength=\"{PasswordSignInSettings.MaximumLength}\" required></label></p>");
			if (m_settings.Password.Honeypot)
			{
				// people never see this field; form-filling bots fill it in
				body.Append($"<p style=\"position:absolute;left:-10000px\" aria-hidden=\"true\"><label>Website <input type=\"text\" name=\"{HoneypotField}\" tabindex=\"-1\" autocomplete=\"off\"></label></p>");
			}
			body.Append("<p><button type=\"submit\">Create account</button></p></form>");
			body.Append($"<p><a href=\"/users/sign_in?{ReturnUrlField}={Uri.EscapeDataString(target)}\">I already have an account</a></p>");
			return Page(status, "Create an account", body.ToString());
		}

		[HttpPost("~/users/sign_up")]
		public async Task<IActionResult> SignUp([FromForm(Name = "display_name")] string? displayName, [FromForm] string? email, [FromForm] string? password,
			[FromForm(Name = HoneypotField)] string? honeypot, [FromForm(Name = ReturnUrlField)] string? returnUrl)
		{
			if (!m_settings.Password.Enabled || !m_settings.Password.AllowSignUp)
			{
				return NotFound();
			}
			if (!await ValidForm())
			{
				return Expired();
			}

			string target = SafeReturnUrl(returnUrl);
			PasswordAccountResult result = await m_passwords.SignUp(displayName, email, password, honeypot);
			AppMetrics.RecordAuthentication("password_sign_up", "password", result.Error ?? "success");

			if (result.Error != null)
			{
				string message = result.Error switch
				{
					PasswordAccounts.ErrorInvalidEmail => "Enter a valid email address.",
					PasswordAccounts.ErrorEmailDenied => "This email address cannot be used.",
					PasswordAccounts.ErrorEmailTaken => "An account with this email address already exists.",
					PasswordAccounts.ErrorNameInvalid => "This name has characters that are not allowed.",
					PasswordAccounts.ErrorNameLength => $"Names must be {Realtime.RealtimeHub.NameMinLength} to {Realtime.RealtimeHub.NameMaxLength} characters.",
					PasswordAccounts.ErrorNameReserved => "This name contains a reserved word.",
					PasswordAccounts.ErrorPasswordTooShort => $"Passwords must be at least {m_settings.Password.MinLength} characters.",
					PasswordAccounts.ErrorPasswordTooLong => $"Passwords must be at most {PasswordSignInSettings.MaximumLength} characters.",
					_ => "Sign-up failed."
				};
				return SignUpForm(400, target, displayName, email, message);
			}

			await m_signIn.SignInAsync(result.User!, isPersistent: false, authenticationMethod: "password");
			return LocalRedirect(target);
		}

		[HttpGet("~/users/auth/{provider}")]
		public IActionResult Challenge(string provider, [FromQuery] string? returnUrl)
		{
			UpstreamProviderSettings? settings = m_settings.EnabledProviders.FirstOrDefault(p => p.Name == provider);
			if (settings == null)
			{
				return NotFound();
			}

			if (settings.Kind == ProviderKind.Steam)
			{
				// Steam's handler verifies the OpenID 2.0 answer, signs into the external cookie and comes back to the callback
				var steam = new AuthenticationProperties(new Dictionary<string, string?>
				{
					[SteamProviderItem] = settings.Name,
					[SteamReturnUrlItem] = SafeReturnUrl(returnUrl)
				})
				{
					RedirectUri = "/" + settings.CallbackPath
				};
				return Challenge(steam, settings.Name);
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

		private const string SteamProviderItem = "go_provider";
		private const string SteamReturnUrlItem = "go_return_url";

		[AcceptVerbs("GET", "POST", Route = "~/users/auth/{provider}/callback")]
		[IgnoreAntiforgeryToken]
		public async Task<IActionResult> Callback(string provider)
		{
			UpstreamProviderSettings? settings = m_settings.EnabledProviders.FirstOrDefault(p => p.Name == provider);
			if (settings == null)
			{
				return NotFound();
			}

			bool steam = settings.Kind == ProviderKind.Steam;
			AuthenticateResult result = await HttpContext.AuthenticateAsync(steam ? IdentityConstants.ExternalScheme : OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
			if (steam)
			{
				// the external cookie only carries this one hand-over
				await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
			}

			if (!result.Succeeded || result.Principal == null)
			{
				s_log.LogWarning("Upstream sign-in with {Provider} failed: {Error}", provider, result.Failure?.Message);
				AppMetrics.RecordAuthentication("upstream", provider, "failed");
				return Page(401, "Sign-in failed", "<p>The sign-in could not be completed. Please try again.</p>");
			}

			// the flow names the provider it started with; another callback path must not finish it
			string providerItem = steam ? SteamProviderItem : OpenIddictClientAspNetCoreConstants.Properties.ProviderName;
			if (result.Properties?.Items.TryGetValue(providerItem, out string? started) != true || started != provider)
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
					UpstreamAccounts.ErrorIdentityConflict => "Another sign-in of this provider is already linked to the account. Please contact support.",
					_ => "The sign-in could not be completed."
				};
				return Page(403, "Sign-in refused", $"<p>{WebUtility.HtmlEncode(message)}</p>");
			}

			await m_signIn.SignInAsync(signIn.User, isPersistent: false, authenticationMethod: provider);
			string? returnUrl = steam ? result.Properties!.GetString(SteamReturnUrlItem) : result.Properties?.RedirectUri;
			return LocalRedirect(SafeReturnUrl(returnUrl));
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

			// OpenIddict already refuses a disabled client, which has no endpoints; the registry check covers the reload window
			RegisteredClient? client = ClientRegistry.FindEnabled(request.ClientId);
			if (client == null)
			{
				return Forbid(Error(Errors.UnauthorizedClient, "This client is disabled."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
			}

			// a third-party client is shown to the user on every sign-in; first-party clients skip the page
			if (!client.FirstParty)
			{
				IActionResult? consent = await Consent(request, cookie.Principal!, await m_applications.GetDisplayNameAsync(application) ?? client.ClientID);
				if (consent != null)
				{
					return consent;
				}
			}

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

			if (ClientRegistry.FindEnabled(request.ClientId) == null)
			{
				return Forbid(Error(Errors.InvalidGrant, "This client is disabled."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
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

		private const string ConsentField = "consent";
		private const string ConsentAllow = "allow";

		private static readonly Dictionary<string, string> s_scopeDescriptions = new(StringComparer.Ordinal)
		{
			[Scopes.OpenId] = "Know who you are",
			[Scopes.Profile] = "See your player name",
			[Scopes.OfflineAccess] = "Stay signed in until you sign out",
			[AccountsSettings.GameScope] = "Play online as you: lobbies, matchmaking and chat"
		};

		// Null when the user allowed this request; otherwise the consent page or the refusal to send back
		private async Task<IActionResult?> Consent(OpenIddictRequest request, ClaimsPrincipal cookieUser, string displayName)
		{
			if (request.HasPromptValue(PromptValues.None))
			{
				return Forbid(Error(Errors.ConsentRequired, "This application needs the user's consent."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
			}

			// the antiforgery token is bound to the signed-in user, so a page fetched for one account cannot approve for another
			HttpContext.User = cookieUser;
			var antiforgery = HttpContext.RequestServices.GetRequiredService<IAntiforgery>();

			if (HttpMethods.IsPost(Request.Method) && Request.HasFormContentType && Request.Form.ContainsKey(ConsentField))
			{
				if (!await antiforgery.IsRequestValidAsync(HttpContext))
				{
					return Page(400, "Request expired", "<p>The page expired. Please start the sign-in again from the application.</p>");
				}

				return Request.Form[ConsentField] == ConsentAllow
					? null
					: Forbid(Error(Errors.AccessDenied, "The user declined."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
			}

			AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(HttpContext);
			IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> parameters = Request.HasFormContentType ? Request.Form : Request.Query;

			var form = new System.Text.StringBuilder();
			form.Append($"<form method=\"post\" action=\"{WebUtility.HtmlEncode(Request.PathBase + Request.Path)}\">");
			foreach ((string key, Microsoft.Extensions.Primitives.StringValues values) in parameters.Where(p => p.Key != ConsentField && p.Key != Parameters.Prompt && p.Key != tokens.FormFieldName))
			{
				foreach (string? value in values)
				{
					form.Append($"<input type=\"hidden\" name=\"{WebUtility.HtmlEncode(key)}\" value=\"{WebUtility.HtmlEncode(value)}\">");
				}
			}
			form.Append($"<input type=\"hidden\" name=\"{WebUtility.HtmlEncode(tokens.FormFieldName)}\" value=\"{WebUtility.HtmlEncode(tokens.RequestToken)}\">");

			string scopes = String.Concat(request.GetScopes().Where(s_scopeDescriptions.ContainsKey).Select(s => $"<li>{WebUtility.HtmlEncode(s_scopeDescriptions[s])}</li>"));
			form.Append($"<p><strong>{WebUtility.HtmlEncode(displayName)}</strong> is not made by Generals Online. It asks to:</p><ul>{scopes}</ul>");
			form.Append($"<p>Signed in as {WebUtility.HtmlEncode(cookieUser.Identity?.Name ?? String.Empty)}.</p>");
			form.Append($"<button type=\"submit\" name=\"{ConsentField}\" value=\"{ConsentAllow}\">Allow</button> <button type=\"submit\" name=\"{ConsentField}\" value=\"deny\">Deny</button></form>");

			// the page approves a sign-in, so it never renders inside another site's frame or a cache
			Response.Headers["X-Frame-Options"] = "DENY";
			Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
			Response.Headers.CacheControl = "no-store";
			return Page(200, "Allow " + displayName + "?", form.ToString());
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

			// OpenIddict has already limited the request to the client's scope permissions
			identity.SetScopes(requestedScopes.Where(AccountsSetup.RegisteredScopes.Contains));
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
