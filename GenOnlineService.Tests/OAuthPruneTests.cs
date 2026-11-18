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

using GenOnlineService.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using Xunit;

namespace GenOnlineService.Tests;

// The daily OAuth prune: dead rows older than 14 days go, in batches of 1000 until none is left; valid tokens, recent
// redeemed ones (still needed to catch a reuse) and authorizations that still hold tokens stay
public class OAuthPruneTests
{
	private static OpenIddictEntityFrameworkCoreToken Token(OpenIddictEntityFrameworkCoreApplication app, OpenIddictEntityFrameworkCoreAuthorization auth, string status, DateTime created, DateTime expires) => new()
	{
		Id = Guid.NewGuid().ToString(),
		Application = app,
		Authorization = auth,
		Subject = "1",
		Type = OpenIddictConstants.TokenTypeHints.RefreshToken,
		Status = status,
		CreationDate = created,
		ExpirationDate = expires
	};

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task OnlyDeadRowsAreRemovedAcrossBatches(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		DateTime now = DateTime.UtcNow;
		string keptValid, keptRecent, keptAuthorization, emptyOld;

		await using (AppDbContext db = probe.Open())
		{
			var app = new OpenIddictEntityFrameworkCoreApplication { Id = Guid.NewGuid().ToString(), ClientId = "prune-test" };
			var live = new OpenIddictEntityFrameworkCoreAuthorization { Id = Guid.NewGuid().ToString(), Application = app, Subject = "1", Status = OpenIddictConstants.Statuses.Valid, Type = OpenIddictConstants.AuthorizationTypes.Permanent, CreationDate = now.AddDays(-20) };
			var empty = new OpenIddictEntityFrameworkCoreAuthorization { Id = Guid.NewGuid().ToString(), Application = app, Subject = "1", Status = OpenIddictConstants.Statuses.Valid, Type = OpenIddictConstants.AuthorizationTypes.Permanent, CreationDate = now.AddDays(-40) };
			db.AddRange(app, live, empty);

			// more than one batch of dead tokens: redeemed 20 days ago
			for (int i = 0; i < 1001; i++)
			{
				db.Add(Token(app, live, OpenIddictConstants.Statuses.Redeemed, now.AddDays(-20), now.AddDays(10)));
			}
			OpenIddictEntityFrameworkCoreToken valid = Token(app, live, OpenIddictConstants.Statuses.Valid, now.AddDays(-20), now.AddDays(10));
			OpenIddictEntityFrameworkCoreToken recent = Token(app, live, OpenIddictConstants.Statuses.Redeemed, now.AddDays(-2), now.AddDays(10));
			db.AddRange(valid, recent);
			await db.SaveChangesAsync();
			(keptValid, keptRecent, keptAuthorization, emptyOld) = (valid.Id!, recent.Id!, live.Id!, empty.Id!);
		}

		await using (AppDbContext db = probe.Open())
		{
			(long tokens, long authorizations) = await OAuthMaintenance.Prune(db, now);
			Assert.Equal(1001, tokens);
			Assert.Equal(1, authorizations);
		}

		await using (AppDbContext db = probe.Open())
		{
			List<string> tokenIds = await db.Set<OpenIddictEntityFrameworkCoreToken>().Select(t => t.Id!).ToListAsync();
			Assert.Equal(new[] { keptRecent, keptValid }.OrderBy(id => id), tokenIds.OrderBy(id => id));
			List<string> authorizationIds = await db.Set<OpenIddictEntityFrameworkCoreAuthorization>().Select(a => a.Id!).ToListAsync();
			Assert.Contains(keptAuthorization, authorizationIds);
			Assert.DoesNotContain(emptyOld, authorizationIds);
		}

		// nothing left to remove: a second run deletes nothing
		await using (AppDbContext db = probe.Open())
		{
			Assert.Equal((0L, 0L), await OAuthMaintenance.Prune(db, now));
		}
	}
}
