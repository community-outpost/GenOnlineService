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
using Microsoft.Extensions.Configuration;
using System.Security.Claims;
using Xunit;

namespace GenOnlineService.Tests;

// Players choose their own display name (identity hub T13, S17): a provider sign-up owes one until the player picks it
public class ChosenNamesTests
{
	internal static UpstreamProviderSettings Discord() => AccountsSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
	{
		["Accounts:ephemeral_keys"] = "true",
		["Accounts:providers:discord:enabled"] = "true",
		["Accounts:providers:discord:client_id"] = "d-id",
		["Accounts:providers:discord:client_secret"] = "d-secret"
	}).Build()).EnabledProviders.Single(p => p.Name == "discord");

	internal static ClaimsPrincipal DiscordUser(string id, string name) =>
		new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Name, name) }, "test"));

	internal static async Task<User> ProviderSignUp(ProbeDatabase probe, string id, string name)
	{
		UpstreamSignInResult result = await new UpstreamAccounts(new ProbeFactory(probe)).SignIn(Discord(), DiscordUser(id, name));
		Assert.Null(result.Error);
		await using AppDbContext db = probe.Open();
		return await db.Users.SingleAsync(u => u.ID == result.User!.ID);
	}

	// a new provider account owes a name; signing in again does not settle it
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AProviderSignUpOwesAName(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		User created = await ProviderSignUp(probe, "80351110224678912", "nelly");
		Assert.NotNull(created.NameRequiredAt);

		User again = await ProviderSignUp(probe, "80351110224678912", "nelly");
		Assert.Equal(created.ID, again.ID);
		Assert.Equal(created.NameRequiredAt, again.NameRequiredAt);
	}

	// a password sign-up types the name on the form, so it owes nothing
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task APasswordSignUpOwesNoName(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		Assert.Equal(302, (await PasswordPagesTests.SignUp(new AccountsBrowser(probe, PasswordPagesTests.Enabled()), "Typed", "typed@example.org", "a long enough password")).Status);
		Assert.Null((await PasswordPagesTests.UserByEmail(probe, "typed@example.org"))!.NameRequiredAt);
	}
}
