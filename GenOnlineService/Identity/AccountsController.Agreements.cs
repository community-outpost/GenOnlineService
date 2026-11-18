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
using System.Globalization;
using System.Net;
using System.Text;

namespace GenOnlineService.Identity
{
	// The agreement pages (S19): the rule cards one by one, then the Terms, then back to the sign-in that sent the player
	// here. Only what is owed is shown. Nothing is recorded until the last step's post, which carries both steps: the
	// rules version the cards were agreed under and the Terms tick box.
	public sealed partial class AccountsController
	{
		public const string AgreementsPath = "/users/agreements";

		private const string AgreementStepField = "step";
		private const string AgreementCardField = "card";
		private const string AgreementRulesField = "rules";
		private const string AgreementTermsField = "agree_terms";
		private const string CardStep = "card";
		private const string TermsStep = "terms";

		// the audit source of an agreement made on these pages
		private const string AgreementSource = "web";

		private static string AgreementsUrl(string target) => $"{AgreementsPath}?{ReturnUrlField}={Uri.EscapeDataString(target)}";

		private static string AgreementsUrl(string target, int card) => $"{AgreementsUrl(target)}&{AgreementCardField}={card.ToString(CultureInfo.InvariantCulture)}";

		private static string TermsStepUrl(string target, string rules) => $"{AgreementsUrl(target)}&{AgreementStepField}={TermsStep}&{AgreementRulesField}={Uri.EscapeDataString(rules)}";

		private AgreementsSettings AgreementSettings => m_settings.Agreements;

		private IDbContextFactory<AppDbContext> DbFactory => HttpContext.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>();

		[HttpGet("~" + AgreementsPath)]
		public async Task<IActionResult> AgreementsPage([FromQuery(Name = ReturnUrlField)] string? returnUrl, [FromQuery(Name = AgreementStepField)] string? step,
			[FromQuery(Name = AgreementCardField)] int? card, [FromQuery(Name = AgreementRulesField)] string? rules)
		{
			string target = SafeReturnUrl(returnUrl);
			User? user = await CookieUser();
			if (user == null)
			{
				return SignInFirst(target);
			}
			IReadOnlyList<AgreementDocument> owed = await OwedAgreements(user.ID);
			if (owed.Count == 0)
			{
				return LocalRedirect(target);
			}

			AgreementDocument? rulesOwed = owed.FirstOrDefault(d => d.Name == AgreementsSettings.Rules);
			AgreementDocument? termsOwed = owed.FirstOrDefault(d => d.Name == AgreementsSettings.Terms);
			// the Terms step comes after the cards when the rules are owed too
			if (termsOwed != null && (rulesOwed == null || (step == TermsStep && rules == rulesOwed.Version)))
			{
				return await TermsForm(200, user.ID, target, termsOwed, rulesOwed?.Version, null);
			}
			return await CardForm(user.ID, target, rulesOwed!, Math.Clamp(card ?? 0, 0, Agreements.RuleCards.Count - 1));
		}

		[HttpPost("~" + AgreementsPath)]
		public async Task<IActionResult> Agree([FromForm(Name = ReturnUrlField)] string? returnUrl, [FromForm(Name = AgreementStepField)] string? step,
			[FromForm(Name = AgreementCardField)] int? card, [FromForm(Name = AgreementRulesField)] string? rules, [FromForm(Name = AgreementTermsField)] string? agreeTerms)
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
			IReadOnlyList<AgreementDocument> owed = await OwedAgreements(user.ID);
			if (owed.Count == 0)
			{
				return LocalRedirect(target);
			}
			AgreementDocument? rulesOwed = owed.FirstOrDefault(d => d.Name == AgreementsSettings.Rules);
			AgreementDocument? termsOwed = owed.FirstOrDefault(d => d.Name == AgreementsSettings.Terms);

			// the cards were agreed under another rules version, or not at all: from the first card again
			if (rulesOwed != null && rules != rulesOwed.Version)
			{
				return LocalRedirect(AgreementsUrl(target));
			}

			if (step == CardStep && rulesOwed != null)
			{
				int agreed = card ?? -1;
				if (agreed < 0 || agreed >= Agreements.RuleCards.Count)
				{
					return LocalRedirect(AgreementsUrl(target));
				}
				if (agreed < Agreements.RuleCards.Count - 1)
				{
					return LocalRedirect(AgreementsUrl(target, agreed + 1));
				}
				if (termsOwed != null)
				{
					return LocalRedirect(TermsStepUrl(target, rulesOwed.Version!));
				}
				// with only the rules owed, the last card is the last step
				await Agreements.AcceptAsync(DbFactory, AgreementSettings, user.ID, rulesOwed.Name, rulesOwed.Version!, AgreementSource);
				return LocalRedirect(target);
			}

			if (step != TermsStep || termsOwed == null)
			{
				return LocalRedirect(AgreementsUrl(target));
			}
			if (String.IsNullOrEmpty(agreeTerms))
			{
				return await TermsForm(400, user.ID, target, termsOwed, rulesOwed?.Version, T("Agreements.Terms.Required"));
			}

			if (rulesOwed != null)
			{
				await Agreements.AcceptAsync(DbFactory, AgreementSettings, user.ID, rulesOwed.Name, rulesOwed.Version!, AgreementSource);
			}
			await Agreements.AcceptAsync(DbFactory, AgreementSettings, user.ID, termsOwed.Name, termsOwed.Version!, AgreementSource);
			return LocalRedirect(target);
		}

		// "changed on <date>" for a player who agreed to an earlier version, shown before the first card or on the Terms step
		private async Task<string> ChangedNotice(Int64 userID, AgreementDocument document, string withDate, string withoutDate)
		{
			bool earlier;
			await using (AppDbContext db = await DbFactory.CreateDbContextAsync(HttpContext.RequestAborted))
			{
				earlier = (await Agreements.LatestAsync(db, userID, HttpContext.RequestAborted)).ContainsKey(document.Name);
			}
			if (!earlier)
			{
				return String.Empty;
			}
			string text = document.Date is DateOnly date ? T(withDate, Day(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))) : T(withoutDate);
			return $"<p class=\"bb:font-medium\" role=\"status\">{WebUtility.HtmlEncode(text)}</p>";
		}

		// A link to a document, opened beside the page so the step is not lost
		private static string ExternalLink(string href, string text)
			=> $"<a href=\"{WebUtility.HtmlEncode(href)}\" target=\"_blank\" rel=\"noopener noreferrer\" class=\"bb:text-primary bb:underline-offset-4 bb:hover:underline\">{WebUtility.HtmlEncode(text)}</a>";

		private async Task<ContentResult> CardForm(Int64 userID, string target, AgreementDocument rules, int index)
		{
			(string id, int items) = Agreements.RuleCards[index];
			var body = new StringBuilder();
			if (index == 0)
			{
				body.Append(await ChangedNotice(userID, rules, "Agreements.RulesChanged", "Agreements.RulesChangedNoDate"));
			}
			body.Append(PortalLook.Text(WebUtility.HtmlEncode(T($"Rules.{id}.Intro"))));
			body.Append("<ul class=\"go-rule-items\">");
			for (int item = 1; item <= items; item++)
			{
				body.Append($"<li>{WebUtility.HtmlEncode(T($"Rules.{id}.{item.ToString(CultureInfo.InvariantCulture)}"))}</li>");
			}
			body.Append("</ul>");
			body.Append($"<p>{ExternalLink(rules.Url!, T("Agreements.ReadRules"))}</p>");
			body.Append(FormStart(AgreementsPath, target));
			body.Append(Hidden(AgreementStepField, CardStep)).Append(Hidden(AgreementCardField, index.ToString(CultureInfo.InvariantCulture))).Append(Hidden(AgreementRulesField, rules.Version));
			body.Append(PortalLook.Submit(T("Agreements.Agree"), icon: "check")).Append("</form>");
			return Page(200, T($"Rules.{id}.Title"), body.ToString(), T("Agreements.Card.Description", index + 1, Agreements.RuleCards.Count));
		}

		private async Task<ContentResult> TermsForm(int status, Int64 userID, string target, AgreementDocument terms, string? rulesVersion, string? message)
		{
			var body = new StringBuilder(Message(message));
			body.Append(await ChangedNotice(userID, terms, "Agreements.TermsChanged", "Agreements.TermsChangedNoDate"));
			body.Append(PortalLook.Text(WebUtility.HtmlEncode(T("Agreements.Terms.Intro"))));
			string links = ExternalLink(terms.Url!, T("Agreements.TermsLink"));
			if (AgreementSettings.PrivacyUrl != null)
			{
				links += " · " + ExternalLink(AgreementSettings.PrivacyUrl, T("Agreements.PrivacyLink"));
			}
			body.Append($"<p>{links}</p>");
			body.Append(FormStart(AgreementsPath, target));
			body.Append(Hidden(AgreementStepField, TermsStep));
			if (rulesVersion != null)
			{
				body.Append(Hidden(AgreementRulesField, rulesVersion));
			}
			string invalid = message == null ? String.Empty : " aria-invalid=\"true\"";
			body.Append($"<label class=\"go-check\"><input type=\"checkbox\" name=\"{AgreementTermsField}\" value=\"1\" required{invalid}><span>{WebUtility.HtmlEncode(T("Agreements.Terms.Tick"))}</span></label>");
			body.Append(PortalLook.Submit(T("Agreements.Start"), icon: "arrow-right")).Append("</form>");
			return Page(status, T("Agreements.Terms.Title"), body.ToString(), T("Agreements.Terms.Description"));
		}
	}
}
