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

using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace GenOnlineService.Identity
{
	// What a player agreed to (S19): the user_agreements rows against the versions in AgreementsSettings. A document is owed
	// while its current version has no row for the account; an older version's row does not count.
	public static class Agreements
	{
		public const string AcceptedAction = "agreement_accepted";

		// The rule cards of the sign-in, sections 1-6 of the Rules of Engagement, with the number of items each lists. The ids
		// are stable so later sanction work can name a section; the words are AccountsText keys Rules.<id>.Title, .Intro, .1..n
		public static readonly IReadOnlyList<(string Id, int Items)> RuleCards =
		[
			("respect", 5),
			("chat", 3),
			("fair_play", 4),
			("match", 3),
			("accounts", 3),
			("service", 2)
		];

		// The documents this account still has to agree to, in the order the sign-in asks; empty when nothing is configured
		public static async Task<IReadOnlyList<AgreementDocument>> OwedAsync(AppDbContext db, AgreementsSettings settings, Int64 userID, CancellationToken cancellationToken = default)
		{
			List<AgreementDocument> required = settings.Required.ToList();
			if (required.Count == 0)
			{
				return [];
			}
			List<(string Document, string Version)> agreed = (await db.UserAgreements.AsNoTracking()
				.Where(a => a.UserID == userID)
				.Select(a => new { a.Document, a.Version })
				.ToListAsync(cancellationToken))
				.Select(a => (a.Document, a.Version)).ToList();
			return required.Where(d => !agreed.Contains((d.Name, d.Version!))).ToList();
		}

		public static async Task<IReadOnlyList<AgreementDocument>> OwedAsync(IDbContextFactory<AppDbContext> dbFactory, AgreementsSettings settings, Int64 userID, CancellationToken cancellationToken = default)
		{
			if (!settings.Any)
			{
				return [];
			}
			await using AppDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
			return await OwedAsync(db, settings, userID, cancellationToken);
		}

		// The latest agreement of each document this account agreed to, by document name
		public static async Task<Dictionary<string, UserAgreement>> LatestAsync(AppDbContext db, Int64 userID, CancellationToken cancellationToken = default)
		{
			List<UserAgreement> rows = await db.UserAgreements.AsNoTracking().Where(a => a.UserID == userID).ToListAsync(cancellationToken);
			return rows.GroupBy(a => a.Document, StringComparer.Ordinal)
				.ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.AgreedAt).First(), StringComparer.Ordinal);
		}

		// Records that the player agreed to the current version of a configured document, with an agreement_accepted audit
		// row (actor self). False for any other document or version. Agreeing again changes nothing and writes no second row.
		public static async Task<bool> AcceptAsync(IDbContextFactory<AppDbContext> dbFactory, AgreementsSettings settings, Int64 userID, string document, string version, string source)
		{
			AgreementDocument? current = settings.Find(document);
			if (current == null || !current.Required || current.Version != version)
			{
				return false;
			}

			await using AppDbContext db = await dbFactory.CreateDbContextAsync();
			if (await db.UserAgreements.AnyAsync(a => a.UserID == userID && a.Document == document && a.Version == version))
			{
				return true;
			}

			DateTime now = DateTime.UtcNow;
			db.UserAgreements.Add(new UserAgreement { UserID = userID, Document = document, Version = version, AgreedAt = now });
			db.AuditLog.Add(new AuditLogEntry
			{
				CreatedAt = now,
				ActorKind = AuditActorKinds.User,
				ActorID = userID,
				Actor = $"user:{userID}",
				Action = AcceptedAction,
				TargetKind = AuditTargetKinds.User,
				TargetID = userID,
				Source = source,
				Details = JsonSerializer.Serialize(new { document, version })
			});
			try
			{
				await db.SaveChangesAsync();
			}
			catch (DbUpdateException)
			{
				// the same agreement from two requests at once: the other one recorded it (and its audit row)
				await using AppDbContext again = await dbFactory.CreateDbContextAsync();
				if (!await again.UserAgreements.AnyAsync(a => a.UserID == userID && a.Document == document && a.Version == version))
				{
					throw;
				}
			}
			return true;
		}
	}
}
