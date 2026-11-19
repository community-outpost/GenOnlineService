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

using Database;
using GenOnlineService.Names;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GenOnlineService.Tests;

// The import of the old production database: every imported user takes tag 1, so look-alike names must differ under NameKey
public class ImportNameTests
{
	private static Plan PlanOf(params (long Id, string Name)[] users)
	{
		List<OldUser> old = users.Select(u => new OldUser { Id = u.Id, DisplayName = u.Name, LastLogin = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-u.Id) }).ToList();
		ImportSettings settings = new("source", 3306, "old", "user", "password", MySqlConnector.MySqlSslMode.None, false, new HashSet<string>(), "report.txt");
		return Plan.Build(old, new ImportReport(), settings);
	}

	// the exact address is the login identifier: one address is imported on one account only (case folds together), while another
	// spelling of the same mailbox (a +tag, Gmail's dots) is a different address and keeps its own account
	[Fact]
	public void OneAddressIsImportedOnceAndAnotherSpellingKeepsItsAccount()
	{
		DateTime seen = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		List<OldUser> old = new()
		{
			new OldUser { Id = 1, DisplayName = "Ann", LastLogin = seen, GoogleEmail = "ann.smith@gmail.com" },
			new OldUser { Id = 2, DisplayName = "Bea", LastLogin = seen.AddDays(-1), GoogleEmail = "annsmith+alt@gmail.com" },
			new OldUser { Id = 3, DisplayName = "Cid", LastLogin = seen.AddDays(-2), GoogleEmail = "cid@example.org" },
			new OldUser { Id = 4, DisplayName = "Dee", LastLogin = seen.AddDays(-3), GoogleEmail = "ANN.SMITH@gmail.com" },
		};
		ImportSettings settings = new("source", 3306, "old", "user", "password", MySqlConnector.MySqlSslMode.None, false, new HashSet<string>(), "report.txt");
		Plan plan = Plan.Build(old, new ImportReport(), settings);

		Assert.Equal("ann.smith@gmail.com", plan.Kept[1].Email);
		Assert.Equal("annsmith+alt@gmail.com", plan.Kept[2].Email);
		Assert.Equal("cid@example.org", plan.Kept[3].Email);
		Assert.Null(plan.Kept[4].Email);
		// the repeated address is reported as a duplicate email finding, and that finding is the only one
		Assert.True(plan.Findings.HasUnwaived(new HashSet<string>()));
		Assert.False(plan.Findings.HasUnwaived(new HashSet<string> { ImportFindings.DuplicateEmails }));
	}

	[Fact]
	public void ImportedLookAlikesGetDistinctKeys()
	{
		Plan plan = PlanOf((1, "Bob"), (2, "B0b"), (3, "Rob"));

		Assert.Equal("Bob", plan.Kept[1].Username);
		Assert.Equal("B0b_2", plan.Kept[2].Username);
		Assert.Equal("Rob", plan.Kept[3].Username);
		Assert.Equal(3, plan.Kept.Values.Select(k => NameKey.Of(k.Username)).Distinct().Count());

		// the rename is a username collision finding, as for two equal names
		Assert.True(plan.Findings.HasUnwaived(new HashSet<string>()));
		Assert.False(plan.Findings.HasUnwaived(new HashSet<string> { ImportFindings.UsernameCollisions }));
	}

	// NFKC upper folds both sigmas to one letter while the key keeps them apart, so either match is a collision
	[Fact]
	public void NamesEqualOnlyAfterUpperCaseCollideToo()
	{
		Plan plan = PlanOf((1, "NOOB\u03A3"), (2, "noob\u03C3"));

		Assert.NotEqual(NameKey.Of("NOOB\u03A3"), NameKey.Of("noob\u03C3"));
		Assert.Equal(NameNormalizer.Normalize("NOOB\u03A3"), NameNormalizer.Normalize("noob\u03C3"));
		Assert.Equal("NOOB\u03A3", plan.Kept[1].Username);
		Assert.Equal("noob\u03C3_2", plan.Kept[2].Username);
		Assert.True(plan.Findings.HasUnwaived(new HashSet<string>()));
		Assert.False(plan.Findings.HasUnwaived(new HashSet<string> { ImportFindings.UsernameCollisions }));
	}

	// MySQL compares with PAD SPACE, so a name with outer whitespace is invalid and renamed like any invalid name
	[Fact]
	public void ANameWithOuterWhitespaceIsInvalid()
	{
		Plan plan = PlanOf((1, "Bob"), (2, "Bob "), (3, " Ann"));

		Assert.Equal("Bob", plan.Kept[1].Username);
		Assert.Equal("player_2", plan.Kept[2].Username);
		Assert.Equal("player_3", plan.Kept[3].Username);
		Assert.True(plan.Findings.HasUnwaived(new HashSet<string> { ImportFindings.UsernameCollisions }));
		Assert.False(plan.Findings.HasUnwaived(new HashSet<string> { ImportFindings.InvalidUsernames }));
	}

	// The imported users all take tag 1, and both unique indexes, (username_normalized, name_tag) and (name_key, name_tag), accept them
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ImportedLookAlikesFitTheUniqueKeyIndex(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		await using AppDbContext db = probe.Open();
		Plan plan = PlanOf((1, "Bob"), (2, "B0b"), (3, "B\u043Eb"), (4, "NOOB\u03A3"), (5, "noob\u03C3"), (6, "Bob "));

		DateTime now = DateTime.UtcNow;
		db.Users.AddRange(plan.Kept.Select(k => new User { ID = k.Key, Username = k.Value.Username, UsernameNormalized = NameNormalizer.Normalize(k.Value.Username), NameTag = 1, CreatedAt = now, UpdatedAt = now }));
		await db.SaveChangesAsync();

		db.ChangeTracker.Clear();
		List<User> rows = await db.Users.OrderBy(u => u.ID).ToListAsync();
		Assert.Equal(new[] { "Bob", "B0b_2", "B\u043Eb_3", "NOOB\u03A3", "noob\u03C3_5", "player_6" }, rows.Select(u => u.Username));
		Assert.All(rows, u => Assert.Equal(1, u.NameTag));
		Assert.Equal(6, rows.Select(u => u.NameKey).Distinct().Count());
		Assert.Equal(6, rows.Select(u => u.UsernameNormalized).Distinct().Count());
	}
}
