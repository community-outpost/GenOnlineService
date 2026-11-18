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
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Client.AspNetCore;
using OpenIddict.Server.AspNetCore;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GenOnlineService.Identity
{
	// The accounts profile: sign-in pages, upstream providers and the OpenIddict passthrough endpoints
	// Not an [ApiController]: cookie sign-in must redirect to the sign-in page instead of answering 401
	// The pages speak the player's language (AccountsText.CultureOf); the staff pages and the API never come here
	[ApiExplorerSettings(IgnoreApi = true)]
	public sealed partial class AccountsController : ControllerBase
	{
		private static readonly ILogger s_log = AppLog.For(typeof(AccountsController));

		private readonly AccountsSettings m_settings;
		private readonly SignInManager<User> m_signIn;
		private readonly UserManager<User> m_users;
		private readonly UpstreamAccounts m_upstream;
		private readonly PasswordAccounts m_passwords;
		private readonly Names.PlayerNames m_names;
		private readonly IOpenIddictApplicationManager m_applications;
		private readonly IOpenIddictAuthorizationManager m_authorizations;
		private readonly AccountMail m_mail;
		private readonly AccountMailLimiter m_mailLimiter;

		public AccountsController(AccountsSettings settings, SignInManager<User> signIn, UserManager<User> users, UpstreamAccounts upstream,
			PasswordAccounts passwords, Names.PlayerNames names, IOpenIddictApplicationManager applications, IOpenIddictAuthorizationManager authorizations,
			AccountMail mail, AccountMailLimiter mailLimiter)
		{
			m_settings = settings;
			m_signIn = signIn;
			m_users = users;
			m_upstream = upstream;
			m_passwords = passwords;
			m_names = names;
			m_applications = applications;
			m_authorizations = authorizations;
			m_mail = mail;
			m_mailLimiter = mailLimiter;
		}

		// a page reached without a return address (or with a foreign one) goes on to the account page, since nothing serves /
		private string SafeReturnUrl(string? returnUrl) => !String.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : AccountPath;

		private ContentResult Page(int status, string title, string body, string? description = null)
		{
			return new ContentResult
			{
				StatusCode = status,
				ContentType = "text/html; charset=utf-8",
				// the portal look (PortalLook): Blazor Blueprint's split-screen sign-in in Zero Hour colours, dark only
				Content = PortalLook.Document(title, body, description, Language)
			};
		}

		// The language of this request: the switch's cookie, then Accept-Language, then English
		private CultureInfo? m_culture;

		private CultureInfo Culture => m_culture ??= AccountsText.CultureOf(Request);

		private string T(string key) => AccountsText.Get(key, Culture);

		private string T(string key, params object[] values) => AccountsText.Format(key, Culture, values);

		// A paragraph of this language's text
		private string P(string key) => $"<p>{WebUtility.HtmlEncode(T(key))}</p>";

		private PortalLanguage Language => new(Culture, LanguageReturnUrl());

		// Where the language switch comes back to: this page, or for a form's answer the page that shows the form again
		private string LanguageReturnUrl()
		{
			string path = Request.Path.Value ?? AccountPath;
			if (HttpMethods.IsGet(Request.Method))
			{
				return path + Request.QueryString;
			}
			if (path == "/oauth/authorize")
			{
				return AuthorizeAgain();
			}
			if (path.StartsWith("/users/auth/", StringComparison.Ordinal) || path == "/users/sign_out")
			{
				return "/users/sign_in";
			}
			if (path == ResetRequestAction)
			{
				return ResetRequestPath;
			}
			if (path == AccountMail.ResetPasswordEditPath)
			{
				// the reset form again, with its link's token
				string token = Request.HasFormContentType ? Request.Form[ResetTokenField].ToString() : String.Empty;
				return $"{path}?{ResetTokenField}={Uri.EscapeDataString(token)}";
			}
			string back = path switch
			{
				"/profile/two_factor_auth/disable" => "/profile/two_factor_auth",
				AccountMail.ConfirmationPath => ConfirmationPendingPath,
				_ => path
			};
			string? returnUrl = Request.HasFormContentType ? Request.Form[ReturnUrlField].ToString() : null;
			return String.IsNullOrEmpty(returnUrl) ? back : $"{back}?{ReturnUrlField}={Uri.EscapeDataString(returnUrl)}";
		}

		// The language switch: remembers the language for a year and goes back to the page it came from. Only a supported
		// language is written; the cookie is no secret and changes nothing but the words, so a plain link may set it.
		[HttpGet("~" + PortalLook.LanguagePath)]
		public IActionResult SetLanguage([FromQuery] string? culture, [FromQuery(Name = ReturnUrlField)] string? returnUrl)
		{
			CultureInfo? chosen = AccountsText.Supported.FirstOrDefault(c => String.Equals(c.Name, culture, StringComparison.OrdinalIgnoreCase));
			if (chosen != null)
			{
				Response.Cookies.Append(AccountsText.CookieName, AccountsText.CookieValue(chosen), new CookieOptions
				{
					Expires = DateTimeOffset.UtcNow.AddYears(1),
					HttpOnly = true,
					IsEssential = true,
					SameSite = SameSiteMode.Lax,
					Secure = m_settings.RequireHttps || Request.IsHttps,
					Path = "/"
				});
			}
			return LocalRedirect(SafeReturnUrl(returnUrl));
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
			return $"<form method=\"post\" action=\"{WebUtility.HtmlEncode(action)}\" class=\"go-form\">{Hidden(tokens.FormFieldName!, tokens.RequestToken)}{Hidden(ReturnUrlField, returnUrl)}";
		}

		private Task<bool> ValidForm() => HttpContext.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(HttpContext);

		private ContentResult Expired() => Page(400, T("Expired.Title"), P("Expired.Body"));

		private static string Message(string? message) => message == null ? String.Empty : PortalLook.Alert(message);

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
				return Page(503, T("SignIn.Unavailable.Title"), P("SignIn.Unavailable.Body"));
			}

			var body = new System.Text.StringBuilder(Message(message));
			if (passwords)
			{
				body.Append(FormStart("/users/sign_in", target));
				body.Append(PortalLook.Field("email", T("Field.Email"), "type=\"email\" name=\"email\" autocomplete=\"username\" required", email ?? String.Empty, icon: "mail"));
				body.Append(PortalLook.Field("password", T("Field.Password"), "type=\"password\" name=\"password\" autocomplete=\"current-password\" required", icon: "lock-keyhole"));
				body.Append(PortalLook.Submit(T("SignIn.Submit"), icon: "log-in")).Append("</form>");
				// self-service reset needs mail; without it an operator resets (users password-reset)
				if (m_mail.Enabled)
				{
					body.Append(PortalLook.Link(ResetRequestPath, T("SignIn.Forgot")));
				}
				if (providers.Count > 0)
				{
					body.Append(PortalLook.Separator(T("SignIn.OrContinue")));
				}
			}
			body.Append(PortalLook.Providers(providers.Select(p =>
				($"/users/auth/{Uri.EscapeDataString(p.Name)}?returnUrl={Uri.EscapeDataString(target)}", p.Name, p.DisplayName)), Culture));
			// the way to sign up last, under the providers, as login-02
			if (passwords && m_settings.Password.AllowSignUp)
			{
				body.Append(PortalLook.Link($"/users/sign_up?{ReturnUrlField}={Uri.EscapeDataString(target)}", T("SignIn.SignUpLink"), T("SignIn.NoAccount")));
			}
			return Page(status, T("SignIn.Title"), body.ToString(), T("SignIn.Description"));
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
				await m_passwords.RecordSignIn(result.User!);
				await m_signIn.SignInAsync(result.User!, isPersistent: false, authenticationMethod: PasswordAccounts.PasswordMethod);
				return LocalRedirect(target);
			}

			if (result.Error == PasswordAccounts.ErrorTwoFactorRequired)
			{
				// the right password only earns the code page: the same short-lived cookie Identity's own two-factor
				// sign-in reads (its Name claim is the user id)
				var pending = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
				pending.AddClaim(new Claim(ClaimTypes.Name, result.User!.ID.ToString()));
				await HttpContext.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, new ClaimsPrincipal(pending));
				return LocalRedirect($"/users/two_factor?{ReturnUrlField}={Uri.EscapeDataString(target)}");
			}

			return result.Error switch
			{
				PasswordAccounts.ErrorBlocked => Page(403, T("Refused.Title"), P("Refused.Blocked")),
				PasswordAccounts.ErrorDeactivated => Page(403, T("Refused.Title"), P("Refused.Deactivated")),
				_ => SignInForm(401, target, email, T("SignIn.WrongCredentials"))
			};
		}

		// Second factor: a code from the authenticator app or a recovery code, after the right password

		private const string CodeField = "code";
		private const string RecoveryCodeField = "recovery_code";

		[HttpGet("~/users/two_factor")]
		public async Task<IActionResult> TwoFactorPage([FromQuery(Name = ReturnUrlField)] string? returnUrl)
		{
			if (!m_settings.Password.Enabled)
			{
				return NotFound();
			}
			string target = SafeReturnUrl(returnUrl);
			if (await m_signIn.GetTwoFactorAuthenticationUserAsync() == null)
			{
				return LocalRedirect($"/users/sign_in?{ReturnUrlField}={Uri.EscapeDataString(target)}");
			}
			return TwoFactorForm(200, target, null);
		}

		private ContentResult TwoFactorForm(int status, string target, string? message)
		{
			var body = new System.Text.StringBuilder(Message(message));
			body.Append(FormStart("/users/two_factor", target));
			body.Append(PortalLook.Field("code", T("TwoFactor.CodeLabel"), $"type=\"text\" name=\"{CodeField}\" inputmode=\"numeric\" autocomplete=\"one-time-code\" maxlength=\"7\"", icon: "shield-check"));
			body.Append(PortalLook.Field("recovery_code", T("TwoFactor.RecoveryLabel"), $"type=\"text\" name=\"{RecoveryCodeField}\" autocomplete=\"off\"", icon: "key-round"));
			body.Append(PortalLook.Submit(T("SignIn.Submit"), icon: "log-in")).Append("</form>");
			return Page(status, T("TwoFactor.Title"), body.ToString(), T("TwoFactor.Description"));
		}

		[HttpPost("~/users/two_factor")]
		public async Task<IActionResult> TwoFactor([FromForm(Name = CodeField)] string? code, [FromForm(Name = RecoveryCodeField)] string? recoveryCode,
			[FromForm(Name = ReturnUrlField)] string? returnUrl)
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
			User? pending = await m_signIn.GetTwoFactorAuthenticationUserAsync();
			if (pending == null)
			{
				return LocalRedirect($"/users/sign_in?{ReturnUrlField}={Uri.EscapeDataString(target)}");
			}

			// both sign in with amr mfa. Identity's authenticator sign-in counts a wrong code toward the lockout and
			// refuses while locked; its recovery code sign-in does neither, so that is done here.
			string authenticator = (code ?? String.Empty).Replace(" ", String.Empty);
			string recovery = (recoveryCode ?? String.Empty).Trim();
			Microsoft.AspNetCore.Identity.SignInResult result;
			if (authenticator.Length > 0)
			{
				result = await m_signIn.TwoFactorAuthenticatorSignInAsync(authenticator, isPersistent: false, rememberClient: false);
			}
			else if (await m_users.IsLockedOutAsync(pending))
			{
				result = Microsoft.AspNetCore.Identity.SignInResult.LockedOut;
			}
			else
			{
				result = recovery.Length > 0 ? await m_signIn.TwoFactorRecoveryCodeSignInAsync(recovery) : Microsoft.AspNetCore.Identity.SignInResult.Failed;
				if (result.Succeeded)
				{
					await m_users.ResetAccessFailedCountAsync(pending);
				}
				else
				{
					await m_users.AccessFailedAsync(pending);
				}
			}
			AppMetrics.RecordAuthentication("password_two_factor", "password", result.Succeeded ? "success" : result.IsLockedOut ? "locked_out" : "failed");
			if (result.Succeeded)
			{
				await m_passwords.RecordSignIn(pending);
			}

			return result.Succeeded ? LocalRedirect(target) : TwoFactorForm(401, target, T("TwoFactor.WrongCode"));
		}

		// Setting up an authenticator app: the key, a first code, then the recovery codes once

		private async Task<User?> CookieUser()
		{
			AuthenticateResult cookie = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
			return cookie.Succeeded ? await m_users.GetUserAsync(cookie.Principal!) : null;
		}

		private IActionResult SignInFirst(string target)
			=> Challenge(new AuthenticationProperties { RedirectUri = $"{Request.PathBase}{Request.Path}?{ReturnUrlField}={Uri.EscapeDataString(target)}" }, IdentityConstants.ApplicationScheme);

		private const string TotpIssuer = "Generals Online";

		[HttpGet("~/profile/two_factor_auth")]
		public async Task<IActionResult> TwoFactorSetupPage([FromQuery(Name = ReturnUrlField)] string? returnUrl)
		{
			if (!m_settings.Password.Enabled)
			{
				return NotFound();
			}
			string target = SafeReturnUrl(returnUrl);
			User? user = await CookieUser();
			if (user == null)
			{
				return SignInFirst(target);
			}
			if (!await m_users.HasPasswordAsync(user))
			{
				return Page(403, T("TwoFactor.Title"), P("TwoFactor.PasswordOnly"));
			}

			if (user.TwoFactorEnabled)
			{
				var manage = new System.Text.StringBuilder(P("TwoFactor.IsOn"));
				manage.Append(FormStart("/profile/two_factor_auth/disable", target));
				manage.Append(PortalLook.Field("code", T("TwoFactor.CodeLabel"), $"type=\"text\" name=\"{CodeField}\" inputmode=\"numeric\" autocomplete=\"one-time-code\" maxlength=\"7\" required", icon: "shield-check"));
				manage.Append(PortalLook.Submit(T("TwoFactor.TurnOff"), outline: true, icon: "shield-off")).Append("</form>");
				return Page(200, T("TwoFactor.Title"), manage.ToString());
			}

			return await TwoFactorSetupForm(200, user, target, null);
		}

		private async Task<ContentResult> TwoFactorSetupForm(int status, User user, string target, string? message)
		{
			string? key = await m_users.GetAuthenticatorKeyAsync(user);
			if (String.IsNullOrEmpty(key))
			{
				await m_users.ResetAuthenticatorKeyAsync(user);
				key = await m_users.GetAuthenticatorKeyAsync(user);
			}

			string label = Uri.EscapeDataString($"{TotpIssuer}:{Database.DisplayNames.Format(user.Username, user.NameTag)}");
			string uri = $"otpauth://totp/{label}?secret={key}&issuer={Uri.EscapeDataString(TotpIssuer)}&digits=6";
			string grouped = String.Join(' ', Enumerable.Range(0, (key!.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4))));

			var body = new System.Text.StringBuilder(Message(message));
			body.Append(P("TwoFactor.Setup.Intro"));
			body.Append($"<p class=\"bb:break-all\" dir=\"ltr\"><a href=\"{WebUtility.HtmlEncode(uri)}\" class=\"bb:text-primary bb:underline-offset-4 bb:hover:underline\">{WebUtility.HtmlEncode(uri)}</a></p>");
			// the key stays left to right inside any language's sentence
			string keyCode = $"<code dir=\"ltr\" class=\"bb:font-mono bb:rounded-md bb:bg-muted bb:px-2 bb:py-1\">{WebUtility.HtmlEncode(grouped)}</code>";
			body.Append($"<p>{String.Format(Culture, WebUtility.HtmlEncode(T("TwoFactor.Setup.Key")), keyCode)}</p>");
			body.Append(FormStart("/profile/two_factor_auth", target));
			body.Append(PortalLook.Field("code", T("Field.Code"), $"type=\"text\" name=\"{CodeField}\" inputmode=\"numeric\" autocomplete=\"one-time-code\" maxlength=\"7\" required", icon: "shield-check"));
			body.Append(PortalLook.Submit(T("TwoFactor.TurnOn"), icon: "shield-check")).Append("</form>");
			return Page(status, T("TwoFactor.Setup.Title"), body.ToString());
		}

		[HttpPost("~/profile/two_factor_auth")]
		public async Task<IActionResult> TwoFactorSetup([FromForm(Name = CodeField)] string? code, [FromForm(Name = ReturnUrlField)] string? returnUrl)
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
			User? user = await CookieUser();
			if (user == null)
			{
				return SignInFirst(target);
			}
			if (user.TwoFactorEnabled || !await m_users.HasPasswordAsync(user))
			{
				return LocalRedirect($"/profile/two_factor_auth?{ReturnUrlField}={Uri.EscapeDataString(target)}");
			}

			string token = (code ?? String.Empty).Replace(" ", String.Empty);
			if (!await m_users.VerifyTwoFactorTokenAsync(user, m_users.Options.Tokens.AuthenticatorTokenProvider, token))
			{
				return await TwoFactorSetupForm(400, user, target, T("TwoFactor.WrongCode"));
			}

			await m_users.SetTwoFactorEnabledAsync(user, true);
			IEnumerable<string> codes = await m_users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10) ?? Array.Empty<string>();
			// the security stamp changed; keep this browser signed in
			await m_signIn.RefreshSignInAsync(user);

			string list = String.Concat(codes.Select(c => $"<li><code>{WebUtility.HtmlEncode(c)}</code></li>"));
			Response.Headers.CacheControl = "no-store";
			return Page(200, T("TwoFactor.OnTitle"),
				PortalLook.Text(WebUtility.HtmlEncode(T("TwoFactor.RecoveryCodes")))
				+ $"<ul dir=\"ltr\" class=\"bb:grid bb:grid-cols-2 bb:gap-2 bb:font-mono bb:text-center bb:rounded-md bb:bg-muted bb:p-3\">{list}</ul>"
				+ PortalLook.LinkButton(target, T("Continue"), outline: false, icon: "arrow-right"));
		}

		[HttpPost("~/profile/two_factor_auth/disable")]
		public async Task<IActionResult> TwoFactorDisable([FromForm(Name = CodeField)] string? code, [FromForm(Name = ReturnUrlField)] string? returnUrl)
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
			User? user = await CookieUser();
			if (user == null)
			{
				return SignInFirst(target);
			}
			if (!user.TwoFactorEnabled)
			{
				return LocalRedirect($"/profile/two_factor_auth?{ReturnUrlField}={Uri.EscapeDataString(target)}");
			}
			if (await m_passwords.IsStaff(user.ID))
			{
				return Page(403, T("TwoFactor.Title"), P("TwoFactor.StaffKeeps"));
			}

			string token = (code ?? String.Empty).Replace(" ", String.Empty);
			if (!await m_users.VerifyTwoFactorTokenAsync(user, m_users.Options.Tokens.AuthenticatorTokenProvider, token))
			{
				return Page(400, T("TwoFactor.Title"), PortalLook.Alert(T("TwoFactor.WrongCode")));
			}

			// a new key and no recovery codes, so nothing of the old setup works again
			await m_users.SetTwoFactorEnabledAsync(user, false);
			await m_users.ResetAuthenticatorKeyAsync(user);
			await m_users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0);
			await m_signIn.RefreshSignInAsync(user);
			return Page(200, T("TwoFactor.OffTitle"), PortalLook.LinkButton(target, T("Continue"), outline: false, icon: "arrow-right"));
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
			body.Append(PortalLook.Field("display_name", T("Field.PlayerName"), "type=\"text\" name=\"display_name\" autocomplete=\"nickname\" required", displayName ?? String.Empty, icon: "user"));
			body.Append(PortalLook.Field("email", T("Field.Email"), "type=\"email\" name=\"email\" autocomplete=\"email\" required", email ?? String.Empty, icon: "mail"));
			body.Append(PortalLook.Field("password", T("Field.Password"), $"type=\"password\" name=\"password\" autocomplete=\"new-password\" minlength=\"{m_settings.Password.MinLength}\" maxlength=\"{PasswordSignInSettings.MaximumLength}\" required", icon: "lock-keyhole"));
			if (m_settings.Password.Honeypot)
			{
				// people never see this field; form-filling bots fill it in. Off the start edge, so a right-to-left page gets
				// no scroll width from it
				body.Append($"<p style=\"position:absolute;inset-inline-start:-10000px\" aria-hidden=\"true\"><label>Website <input type=\"text\" name=\"{HoneypotField}\" tabindex=\"-1\" autocomplete=\"off\"></label></p>");
			}
			body.Append(PortalLook.Submit(T("SignUp.Submit"), icon: "user-plus")).Append("</form>");
			body.Append(PortalLook.Link($"/users/sign_in?{ReturnUrlField}={Uri.EscapeDataString(target)}", T("SignIn.Title"), T("SignUp.HaveAccount")));
			return Page(status, T("SignUp.Title"), body.ToString(), T("SignUp.Description"));
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
					PasswordAccounts.ErrorInvalidEmail => T("SignUp.InvalidEmail"),
					PasswordAccounts.ErrorEmailDenied => T("SignUp.EmailDenied"),
					PasswordAccounts.ErrorEmailTaken => T("SignUp.EmailTaken"),
					PasswordAccounts.ErrorNameInvalid => T("Name.Invalid"),
					PasswordAccounts.ErrorNameLength => T("Name.Length", Realtime.RealtimeHub.NameMinLength, Realtime.RealtimeHub.NameMaxLength),
					PasswordAccounts.ErrorNameReserved => T("Name.Reserved"),
					PasswordAccounts.ErrorPasswordTooShort => T("SignUp.PasswordTooShort", m_settings.Password.MinLength),
					PasswordAccounts.ErrorPasswordTooLong => T("SignUp.PasswordTooLong", PasswordSignInSettings.MaximumLength),
					_ => T("SignUp.Failed")
				};
				return SignUpForm(400, target, displayName, email, message);
			}

			// with mail, the new address is confirmed by a link before the first token (S4); a failed send is retried from the waiting page
			if (m_mail.Enabled)
			{
				await m_mail.SendConfirmation(result.User!, Culture);
			}

			await m_passwords.RecordSignIn(result.User!);
			await m_signIn.SignInAsync(result.User!, isPersistent: false, authenticationMethod: "password");
			return LocalRedirect(target);
		}

		// Choosing a name (S17), GitLab's post-sign-up step: a provider sign-up owes one, and so does a player whose name staff reset

		private static string WelcomeUrl(string target) => $"{NameSuggestion.PagePath}?{ReturnUrlField}={Uri.EscapeDataString(target)}";

		[HttpGet("~/users/sign_up/welcome")]
		public async Task<IActionResult> WelcomePage([FromQuery(Name = ReturnUrlField)] string? returnUrl)
		{
			string target = SafeReturnUrl(returnUrl);
			User? user = await CookieUser();
			if (user == null)
			{
				return SignInFirst(target);
			}
			if (user.NameRequiredAt == null)
			{
				return LocalRedirect(target);
			}
			return WelcomeForm(200, target, NameSuggestion.Read(HttpContext, user.ID), null);
		}

		private ContentResult WelcomeForm(int status, string target, string? displayName, string? message)
		{
			var body = new System.Text.StringBuilder(Message(message));
			body.Append(PortalLook.Text(WebUtility.HtmlEncode(T("Welcome.Intro"))));
			body.Append(FormStart(NameSuggestion.PagePath, target));
			body.Append(PortalLook.Field("display_name", T("Field.PlayerName"), $"type=\"text\" name=\"display_name\" autocomplete=\"nickname\" minlength=\"{Realtime.RealtimeHub.NameMinLength}\" maxlength=\"{Realtime.RealtimeHub.NameMaxLength}\" required", displayName, icon: "user"));
			body.Append(PortalLook.Submit(T("Continue"), icon: "arrow-right")).Append("</form>");
			return Page(status, T("Welcome.Title"), body.ToString(), T("Welcome.Description"));
		}

		[HttpPost("~/users/sign_up/welcome")]
		public async Task<IActionResult> Welcome([FromForm(Name = "display_name")] string? displayName, [FromForm(Name = ReturnUrlField)] string? returnUrl)
		{
			if (!await ValidForm())
			{
				return Expired();
			}

			string target = SafeReturnUrl(returnUrl);
			User? user = await CookieUser();
			if (user == null)
			{
				return SignInFirst(target);
			}
			if (user.NameRequiredAt == null)
			{
				return LocalRedirect(target);
			}

			Realtime.Result<Realtime.Empty> chosen = await m_names.ChooseAsync(user.ID, displayName);
			if (!chosen.Ok)
			{
				return WelcomeForm(400, target, displayName, NameMessage(chosen.Code));
			}

			NameSuggestion.Clear(HttpContext);
			// the sign-in cookie carries the name, so it is reissued with the chosen one
			User? renamed = await m_users.FindByIdAsync(user.ID.ToString());
			if (renamed != null)
			{
				await m_signIn.RefreshSignInAsync(renamed);
			}
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
				return Page(401, T("Upstream.Failed.Title"), P("Upstream.Failed.Body"));
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
					UpstreamAccounts.ErrorBlocked => "Refused.Blocked",
					UpstreamAccounts.ErrorDeactivated => "Refused.Deactivated",
					UpstreamAccounts.ErrorSignUpDisabled => "Upstream.NoSignUp",
					UpstreamAccounts.ErrorIdentityConflict => "Upstream.Conflict",
					_ => "Upstream.Other"
				};
				return Page(403, T("Refused.Title"), P(message));
			}

			await m_signIn.SignInAsync(signIn.User, isPersistent: false, authenticationMethod: provider);
			string? returnUrl = steam ? result.Properties!.GetString(SteamReturnUrlItem) : result.Properties?.RedirectUri;
			if (signIn.User.NameRequiredAt != null)
			{
				// a new player chooses their name first; the provider's name only fills the box (S17)
				if (signIn.SuggestedName != null)
				{
					NameSuggestion.Write(HttpContext, signIn.User.ID, signIn.SuggestedName);
				}
				return LocalRedirect(WelcomeUrl(SafeReturnUrl(returnUrl)));
			}
			return LocalRedirect(SafeReturnUrl(returnUrl));
		}

		[HttpPost("~/users/sign_out")]
		public async Task<IActionResult> SignOutPage()
		{
			await m_signIn.SignOutAsync();
			return Page(200, T("SignedOut.Title"), P("SignedOut.Body"));
		}

		// The documents this account still has to agree to (S19); none when no version is configured
		private Task<IReadOnlyList<AgreementDocument>> OwedAgreements(Int64 userID)
			=> Agreements.OwedAsync(HttpContext.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>(), m_settings.Agreements, userID, HttpContext.RequestAborted);

		// A realtime connection of the player on this copy, any client type: the same check as "online" on the staff player page
		private static bool HasLiveGameConnection(Int64 userID) => WebSocketManager.GetSharedDataForUser(userID) != null;

		// This authorize request again, without prompt, for a page to send the user back to
		private string AuthorizeAgain()
		{
			string query = QueryString.Create((Request.HasFormContentType ? Request.Form.ToList() : Request.Query.ToList()).Where(p => p.Key != Parameters.Prompt).ToList()).ToString();
			return Request.PathBase + Request.Path + query;
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

			// a password account confirms its address first when mail is configured, then comes back here (S4)
			if (m_mail.MustConfirm(user))
			{
				if (request.HasPromptValue(PromptValues.None))
				{
					return Forbid(Error(Errors.InteractionRequired, "The email address must be confirmed first."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
				}

				return LocalRedirect(ConfirmationPendingUrl(AuthorizeAgain()));
			}

			// staff with a password and no second factor set it up first, then come back here
			if (await m_passwords.MustSetUpTwoFactor(user))
			{
				if (request.HasPromptValue(PromptValues.None))
				{
					return Forbid(Error(Errors.InteractionRequired, "Two-factor sign-in must be set up first."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
				}

				return LocalRedirect($"/profile/two_factor_auth?{ReturnUrlField}={Uri.EscapeDataString(AuthorizeAgain())}");
			}

			// a player who owes a name chooses it before any token, then comes back here (S17)
			if (user.NameRequiredAt != null)
			{
				if (request.HasPromptValue(PromptValues.None))
				{
					return Forbid(Error(Errors.InteractionRequired, "The player must choose a name first."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
				}

				return LocalRedirect(WelcomeUrl(AuthorizeAgain()));
			}

			// a player who owes an agreement (S19) agrees to the rules and terms in force before any token, then comes back here
			if ((await OwedAgreements(user.ID)).Count > 0)
			{
				if (request.HasPromptValue(PromptValues.None))
				{
					return Forbid(Error(Errors.InteractionRequired, "The player must accept the current agreements first."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
				}

				return LocalRedirect(AgreementsUrl(AuthorizeAgain()));
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

			// a player whose name staff reset signs in again and chooses one first (S17); this holds even when revoking
			// their sign-ins failed. A new account never gets this far: authorize issues no code before the name.
			if (request.IsRefreshTokenGrantType() && user.NameRequiredAt != null)
			{
				return Forbid(Error(Errors.InvalidGrant, "The player must choose a new name."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
			}

			// The agreement refresh rule (S19): the game client's refresh is refused while an agreement is owed only when the
			// player has no live game connection on this copy of the service (WebSocketManager, the check behind "online" on
			// the staff player page). That is the client's login with a saved sign-in: invalid_grant makes it drop the saved
			// sign-in and open the browser, where authorize sends it through the agreement pages. A refresh while connected
			// passes, so a new rules version never logs a player out of a lobby or a match; they agree at their next login.
			if (request.IsRefreshTokenGrantType() && SessionRevocation.IsGameClient(request.ClientId) && !HasLiveGameConnection(user.ID)
				&& (await OwedAgreements(user.ID)).Count > 0)
			{
				return Forbid(Error(Errors.InvalidGrant, "The player must accept the current agreements first."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
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

		// the words (AccountsText keys) for each scope a third-party client can ask for
		private static readonly Dictionary<string, string> s_scopeDescriptions = new(StringComparer.Ordinal)
		{
			[Scopes.OpenId] = "Scope.OpenId",
			[Scopes.Profile] = "Scope.Profile",
			[Scopes.OfflineAccess] = "Scope.OfflineAccess",
			[AccountsSettings.GameScope] = "Scope.Game"
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
					return Page(400, T("Expired.Title"), P("Consent.Expired"));
				}

				return Request.Form[ConsentField] == ConsentAllow
					? null
					: Forbid(Error(Errors.AccessDenied, "The user declined."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
			}

			AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(HttpContext);
			IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> parameters = Request.HasFormContentType ? Request.Form : Request.Query;

			var form = new System.Text.StringBuilder();
			form.Append($"<form method=\"post\" action=\"{WebUtility.HtmlEncode(Request.PathBase + Request.Path)}\" class=\"go-form\">");
			foreach ((string key, Microsoft.Extensions.Primitives.StringValues values) in parameters.Where(p => p.Key != ConsentField && p.Key != Parameters.Prompt && p.Key != tokens.FormFieldName))
			{
				foreach (string? value in values)
				{
					form.Append($"<input type=\"hidden\" name=\"{WebUtility.HtmlEncode(key)}\" value=\"{WebUtility.HtmlEncode(value)}\">");
				}
			}
			form.Append($"<input type=\"hidden\" name=\"{WebUtility.HtmlEncode(tokens.FormFieldName)}\" value=\"{WebUtility.HtmlEncode(tokens.RequestToken)}\">");

			string scopes = String.Concat(request.GetScopes().Where(s_scopeDescriptions.ContainsKey).Select(s => $"<li>{WebUtility.HtmlEncode(T(s_scopeDescriptions[s]))}</li>"));
			form.Append($"<p>{String.Format(Culture, WebUtility.HtmlEncode(T("Consent.Asks")), $"<strong><bdi>{WebUtility.HtmlEncode(displayName)}</bdi></strong>")}</p><ul>{scopes}</ul>");
			form.Append($"<p>{WebUtility.HtmlEncode(T("Consent.SignedInAs", cookieUser.Identity?.Name ?? String.Empty))}</p>");
			form.Append(PortalLook.Submit(T("Consent.Allow"), ConsentField, ConsentAllow, icon: "check")).Append(PortalLook.Submit(T("Consent.Deny"), ConsentField, "deny", outline: true, icon: "x")).Append("</form>");

			// the page approves a sign-in, so it never renders inside another site's frame or a cache
			Response.Headers["X-Frame-Options"] = "DENY";
			Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
			Response.Headers.CacheControl = "no-store";
			return Page(200, T("Consent.Title", displayName), form.ToString());
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
