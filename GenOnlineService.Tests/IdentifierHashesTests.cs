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

using System.Runtime.CompilerServices;
using GenOnlineService.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GenOnlineService.Tests;

// Every test in this assembly hashes identifiers with one key, as the service does from startup on
internal static class IdentifierHashTestKey
{
#pragma warning disable CA2255
	[ModuleInitializer]
#pragma warning restore CA2255
	internal static void Use() => DeviceHasher.Initialize(Path.Combine(Path.GetTempPath(), "gn_probe_device_hmac.key"));
}

// One account per person (2026-11-19): one mailbox written another way is the same mailbox; a deleted banned account keeps
// its mailbox and provider ids only as keyed hashes while the ban lasts
public class IdentifierHashesTests
{
	private sealed class Factory : IDbContextFactory<AppDbContext>
	{
		private readonly ProbeDatabase m_probe;
		public Factory(ProbeDatabase probe) => m_probe = probe;
		public AppDbContext CreateDbContext() => m_probe.Open();
	}

	private static UserManager<User> Users(ProbeDatabase probe)
	{
		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton<IDbContextFactory<AppDbContext>>(new Factory(probe));
		var values = new Dictionary<string, string?> { ["Accounts:ephemeral_keys"] = "true", ["Accounts:password:enabled"] = "true" };
		services.AddGenOnlineIdentity(AccountsSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build()), development: true);
		return services.BuildServiceProvider().GetRequiredService<UserManager<User>>();
	}

	private static Task<IdentityResult> SignUp(UserManager<User> users, string email) =>
		users.CreateAsync(new User { Username = "Player", Email = email, EmailNormalized = NameNormalizer.Normalize(email) }, "correct horse battery");

	[Theory]
	[InlineData("Jane.Doe+games@Gmail.com", "janedoe@gmail.com")]
	[InlineData("jane.doe@googlemail.com", "janedoe@gmail.com")]
	[InlineData("j.a.n.e@gmail.com", "jane@gmail.com")]
	[InlineData("jane.doe+x@outlook.com", "jane.doe@outlook.com")]
	[InlineData("Jane.Doe@Example.org", "jane.doe@example.org")]
	[InlineData("+tag@example.org", null)]
	[InlineData("not-an-address", null)]
	public void EmailsHaveOneCanonicalForm(string email, string? canonical)
	{
		Assert.Equal(canonical, IdentifierHashes.CanonicalEmail(email));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task OneMailboxHoldsOneAccount(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		UserManager<User> users = Users(probe);

		Assert.True((await SignUp(users, "jane.doe@gmail.com")).Succeeded);
		foreach (string variant in new[] { "jane.doe+smurf@gmail.com", "JaneDoe@googlemail.com", "j.a.n.e.d.o.e@gmail.com" })
		{
			IdentityResult refused = await SignUp(users, variant);
			Assert.False(refused.Succeeded, variant);
			Assert.Equal(GoUserValidator.DuplicateEmail, Assert.Single(refused.Errors).Code);
		}
		// another mailbox is free; outside Gmail the dots matter
		Assert.True((await SignUp(users, "jane.doe@outlook.com")).Succeeded);
		Assert.True((await SignUp(users, "janedoe@outlook.com")).Succeeded);
		Assert.False((await SignUp(users, "jane.doe+x@outlook.com")).Succeeded);

		// changing the address moves the claim: the old mailbox is free again, the new one taken
		User jane = (await users.FindByEmailAsync("jane.doe@gmail.com"))!;
		Assert.True((await users.SetEmailAsync(jane, "jane.new@example.org")).Succeeded);
		Assert.True((await SignUp(users, "jane.doe+back@gmail.com")).Succeeded);
		Assert.False((await SignUp(users, "jane.new+2@example.org")).Succeeded);

		// only a hash is stored, never the address
		await using AppDbContext db = probe.Open();
		Assert.All(await db.IdentifierHashes.ToListAsync(), h => Assert.Equal(32, h.ValueHmac.Length));
	}

	// imported accounts get their mailbox hash at startup; each keeps working and holds its mailbox
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheBackfillHashesImportedAccounts(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		long imported;
		await using (AppDbContext db = probe.Open())
		{
			var a = new User { Username = "Old", UsernameNormalized = NameNormalizer.Normalize("Old"), NameTag = 1, Email = "old.player@gmail.com", EmailNormalized = NameNormalizer.Normalize("old.player@gmail.com"), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
			var c = new User { Username = "Steam", UsernameNormalized = NameNormalizer.Normalize("Steam"), NameTag = 3, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
			db.Users.AddRange(a, c);
			await db.SaveChangesAsync();
			imported = a.ID;

			Assert.Equal(1, await IdentifierHashes.Backfill(db));
			Assert.Equal(0, await IdentifierHashes.Backfill(db));
		}

		UserManager<User> users = Users(probe);
		Assert.False((await SignUp(users, "o.l.d.p.l.a.y.e.r+new@gmail.com")).Succeeded);
		// every Identity update validates the user again; the account that holds the mailbox keeps working
		User account = (await users.FindByIdAsync(imported.ToString()))!;
		Assert.True((await users.UpdateSecurityStampAsync(account)).Succeeded);
		Assert.True((await users.AccessFailedAsync(account)).Succeeded);
		Assert.True((await users.ResetAccessFailedCountAsync(account)).Succeeded);
	}

	// sign-ups with one mailbox at the same moment: exactly one account is made each time, the others leave nothing behind
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task SimultaneousSignUpsWithOneMailboxMakeOneAccount(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		for (int round = 0; round < 10; round++)
		{
			string[] variants = { $"race{round}@gmail.com", $"race{round}+x@gmail.com", $"r.a.c.e{round}@googlemail.com" };
			IdentityResult[] results = await Task.WhenAll(variants.Select(v => Task.Run(() => SignUp(Users(probe), v))));
			Assert.Single(results, r => r.Succeeded);
			Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(GoUserValidator.DuplicateEmail, Assert.Single(r.Errors).Code));
		}

		await using AppDbContext db = probe.Open();
		Assert.Equal(10, await db.Users.CountAsync());
		Assert.Equal(10, await db.IdentifierHashes.CountAsync(h => h.Kind == HashedIdentifierKinds.Email));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ADeletedBannedAccountKeepsOnlyHashesUntilTheBanEnds(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		UserManager<User> users = Users(probe);
		Assert.True((await SignUp(users, "cheater@gmail.com")).Succeeded);
		User cheater = (await users.FindByEmailAsync("cheater@gmail.com"))!;
		DateTime now = DateTime.UtcNow;
		await using (AppDbContext db = probe.Open())
		{
			db.Identities.Add(new UserIdentity { UserID = cheater.ID, Provider = "steam", ExternUid = "76561190000000009", CreatedAt = now });
			db.Identities.Add(new UserIdentity { UserID = cheater.ID, Provider = "twitch", ExternUid = "abc123", CreatedAt = now });
			db.Devices.Add(new Device { UserID = cheater.ID, Kind = "mac", ValueHmac = Enumerable.Repeat((byte)4, Device.ValueHmacLength).ToArray(), ValueRaw = "AA-BB-CC", FirstSeenAt = now, LastSeenAt = now, LastIp = "192.0.2.9" });
			db.Sanctions.Add(new Sanction { UserID = cheater.ID, Type = SanctionTypes.Ban, Reason = "cheating", CreatedAt = now });
			await db.SaveChangesAsync();

			var op = new Database.ModerationActor { Kind = AuditActorKinds.Cli, Name = "cli:operator", Source = ModerationSources.Cli };
			Assert.True((await AccountErasure.Erase(db, cheater.ID, op, "request", live: false)).BanHeld);
		}

		await using (AppDbContext db = probe.Open())
		{
			// no raw identifier is left: links gone, the device without its raw value and address
			Assert.False(await db.Identities.AnyAsync(i => i.UserID == cheater.ID));
			Device device = await db.Devices.SingleAsync(d => d.UserID == cheater.ID);
			Assert.Null(device.ValueRaw);
			Assert.Null(device.LastIp);
			Assert.Equal(3, await db.IdentifierHashes.CountAsync(h => h.UserID == cheater.ID && h.SanctionID != null));

			// the same Steam account, a provider added later and the mailbox are all refused while the ban lasts
			Assert.True(await IdentifierHashes.ProviderHeld(db, "steam", "76561190000000009"));
			Assert.True(await IdentifierHashes.ProviderHeld(db, "Twitch", "abc123"));
			Assert.False(await IdentifierHashes.ProviderHeld(db, "steam", "76561190000000010"));
			Assert.False(await IdentifierHashes.ProviderHeld(db, "discord", "76561190000000009"));
		}
		Assert.False((await SignUp(users, "cheater+again@gmail.com")).Succeeded);

		await using (AppDbContext db = probe.Open())
		{
			Assert.Equal(0, await IdentifierHashes.PurgeEndedHolds(db, DateTime.UtcNow));
			await db.Sanctions.Where(s => s.UserID == cheater.ID).ExecuteUpdateAsync(s => s.SetProperty(x => x.LiftedAt, DateTime.UtcNow));
			Assert.Equal(3, await IdentifierHashes.PurgeEndedHolds(db, DateTime.UtcNow));
			Assert.False(await IdentifierHashes.ProviderHeld(db, "steam", "76561190000000009"));
		}
		Assert.True((await SignUp(users, "cheater+again@gmail.com")).Succeeded);
	}

	// the same Discord account cannot sign up again while the ban of its deleted account lasts, and can once it ends
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ABannedProviderIdCannotSignUpAgainUntilTheBanEnds(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User banned = await ChosenNamesTests.ProviderSignUp(probe, "80351110224670001", "evader");
		await using (AppDbContext db = probe.Open())
		{
			db.Sanctions.Add(new Sanction { UserID = banned.ID, Type = SanctionTypes.Ban, Reason = "cheating", CreatedAt = DateTime.UtcNow });
			await db.SaveChangesAsync();
			var op = new Database.ModerationActor { Kind = AuditActorKinds.Cli, Name = "cli:operator", Source = ModerationSources.Cli };
			Assert.True((await AccountErasure.Erase(db, banned.ID, op, "request", live: false)).BanHeld);
		}

		var accounts = new UpstreamAccounts(new ProbeFactory(probe));
		UpstreamSignInResult again = await accounts.SignIn(ChosenNamesTests.Discord(), ChosenNamesTests.DiscordUser("80351110224670001", "evader"));
		Assert.Equal(UpstreamAccounts.ErrorDeactivated, again.Error);
		await using (AppDbContext db = probe.Open())
		{
			Assert.Equal(1, await db.Users.CountAsync());
			await db.Sanctions.ExecuteUpdateAsync(s => s.SetProperty(x => x.LiftedAt, DateTime.UtcNow));
			await IdentifierHashes.PurgeEndedHolds(db, DateTime.UtcNow);
		}

		// another Discord account is not held, and the held one is free once the ban ended
		Assert.Null((await accounts.SignIn(ChosenNamesTests.Discord(), ChosenNamesTests.DiscordUser("80351110224670002", "someone"))).Error);
		Assert.Null((await accounts.SignIn(ChosenNamesTests.Discord(), ChosenNamesTests.DiscordUser("80351110224670001", "evader"))).Error);
	}
}
