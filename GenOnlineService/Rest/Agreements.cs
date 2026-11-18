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
using GenOnlineService.Identity;
using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService.Rest
{
	// One document of GET /api/v1/me/agreements: the version in force and what this account agreed
	public sealed record AgreementDocumentState
	{
		public string Document { get; init; } = "";
		public string Version { get; init; } = "";
		// yyyy-MM-dd, null when the operator gave no date
		public string? Date { get; init; }
		public string Url { get; init; } = "";
		public AgreedVersion? Agreed { get; init; }
		public bool Owed { get; init; }
	}

	// The latest version of a document the account agreed to, which may be older than the current one
	public sealed record AgreedVersion
	{
		public string Version { get; init; } = "";
		public DateTime AgreedAt { get; init; }
	}

	public sealed record AgreementsResponse
	{
		public IReadOnlyList<AgreementDocumentState> Documents { get; init; } = [];
		public string? PrivacyUrl { get; init; }
		// the documents still to agree to, in the order the sign-in asks
		public IReadOnlyList<string> Owed { get; init; } = [];
	}

	public sealed record AcceptAgreementRequest
	{
		public string? Document { get; init; }
		public string? Version { get; init; }
	}

	// The caller's agreements (S19): what is in force, what they agreed and what they owe, and agreeing to a current version.
	// The website's own pages (/profile, the sign-in wizard) call these with the sign-in cookie; a game session with its token.
	public sealed class AgreementsController : ApiControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;
		private readonly AgreementsSettings m_settings;

		public AgreementsController(IDbContextFactory<AppDbContext> dbFactory, AccountsSettings accounts, IAuthorizationService authorization) : base(authorization)
		{
			m_dbFactory = dbFactory;
			m_settings = accounts.Agreements;
		}

		[AllowSignInCookie]
		[HttpGet("~/api/v1/me/agreements")]
		public async Task<IActionResult> Get()
		{
			return ApiResults.Ok(await StateAsync(m_dbFactory, m_settings, Me, HttpContext.RequestAborted));
		}

		[AllowSignInCookie]
		[HttpPost("~/api/v1/me/agreements")]
		public async Task<IActionResult> Accept()
		{
			(AcceptAgreementRequest? body, Failure? malformed) = await ReadBodyAsync<AcceptAgreementRequest>();
			if (malformed is Failure broken)
			{
				return ApiResults.Fail(broken);
			}

			AgreementDocument? document = m_settings.Find(body?.Document);
			if (document == null || !document.Required)
			{
				return ApiResults.Fail(Result.Invalid("document"));
			}
			// only the version in force: a page loaded before a new version was published cannot agree to the old one
			if (body!.Version != document.Version)
			{
				return ApiResults.Fail(Result.Invalid("version"));
			}

			await Agreements.AcceptAsync(m_dbFactory, m_settings, Me, document.Name, document.Version!, ModerationSources.Api);
			return NoContent();
		}

		public static async Task<AgreementsResponse> StateAsync(IDbContextFactory<AppDbContext> dbFactory, AgreementsSettings settings, Int64 userID, CancellationToken cancellationToken = default)
		{
			if (!settings.Any)
			{
				return new AgreementsResponse { PrivacyUrl = settings.PrivacyUrl };
			}

			Dictionary<string, UserAgreement> latest;
			IReadOnlyList<AgreementDocument> owed;
			await using (AppDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken))
			{
				latest = await Agreements.LatestAsync(db, userID, cancellationToken);
				owed = await Agreements.OwedAsync(db, settings, userID, cancellationToken);
			}

			return new AgreementsResponse
			{
				Documents = settings.Required.Select(d => new AgreementDocumentState
				{
					Document = d.Name,
					Version = d.Version!,
					Date = d.Date?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
					Url = d.Url!,
					Agreed = latest.TryGetValue(d.Name, out UserAgreement? agreed) ? new AgreedVersion { Version = agreed.Version, AgreedAt = agreed.AgreedAt } : null,
					Owed = owed.Contains(d)
				}).ToList(),
				PrivacyUrl = settings.PrivacyUrl,
				Owed = owed.Select(d => d.Name).ToList()
			};
		}
	}
}
