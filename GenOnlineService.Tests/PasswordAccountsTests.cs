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
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace GenOnlineService.Tests;

// Email + password accounts (identity hub T3a): the Accounts:password settings, the Identity options they drive, and
// the user store's password, email, lockout, authenticator key and recovery code parts against a real database
public class PasswordAccountsTests
{
	private static AccountsSettings Read(Dictionary<string, string?> values) => AccountsSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

	// off unless the operator turns it on; sign-up open and the honeypot on once it is
	[Fact]
	public void PasswordSignInIsOffByDefault()
	{
		PasswordSignInSettings password = Read(new()).Password;
		Assert.False(password.Enabled);
		Assert.True(password.AllowSignUp);
		Assert.Equal(8, password.MinLength);
		Assert.True(password.Honeypot);
		Assert.Empty(password.EmailDomainDenyList);
	}

	[Fact]
	public void PasswordSettingsAreRead()
	{
		PasswordSignInSettings password = Read(new()
		{
			["Accounts:password:enabled"] = "true",
			["Accounts:password:allow_sign_up"] = "false",
			["Accounts:password:min_length"] = "12",
			["Accounts:password:honeypot"] = "false",
			["Accounts:password:email_domain_deny_list:0"] = "Mailinator.com",
			["Accounts:password:email_domain_deny_list:1"] = "example.org"
		}).Password;

		Assert.True(password.Enabled);
		Assert.False(password.AllowSignUp);
		Assert.Equal(12, password.MinLength);
		Assert.False(password.Honeypot);
		// domains compare without case
		Assert.Equal(new[] { "mailinator.com", "example.org" }, password.EmailDomainDenyList);
	}

	[Theory]
	[InlineData("7")]
	[InlineData("129")]
	[InlineData("0")]
	public void AMinimumLengthOutOfRangeStopsTheStart(string minLength)
	{
		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Read(new() { ["Accounts:password:min_length"] = minLength }));
		Assert.Contains("Accounts:password:min_length", ex.Message);
	}

	private static IServiceProvider Services(Dictionary<string, string?> values, IDbContextFactory<AppDbContext>? factory = null)
	{
		ServiceCollection services = new();
		services.AddLogging();
		if (factory != null)
		{
			services.AddSingleton(factory);
		}
		values = new Dictionary<string, string?>(values) { ["Accounts:ephemeral_keys"] = "true" };
		services.AddGenOnlineIdentity(Read(values), development: true);
		return services.BuildServiceProvider();
	}

	// lockout after 5 failures for 15 minutes, every account; a password needs only its length
	[Fact]
	public void IdentityOptionsFollowTheSettings()
	{
		IdentityOptions options = Services(new() { ["Accounts:password:min_length"] = "10" }).GetRequiredService<IOptions<IdentityOptions>>().Value;

		Assert.True(options.Lockout.AllowedForNewUsers);
		Assert.Equal(5, options.Lockout.MaxFailedAccessAttempts);
		Assert.Equal(TimeSpan.FromMinutes(15), options.Lockout.DefaultLockoutTimeSpan);
		Assert.Equal(10, options.Password.RequiredLength);
		Assert.False(options.Password.RequireDigit);
		Assert.False(options.Password.RequireLowercase);
		Assert.False(options.Password.RequireUppercase);
		Assert.False(options.Password.RequireNonAlphanumeric);
		Assert.Equal(1, options.Password.RequiredUniqueChars);
	}

	// a password longer than 128 characters is refused like a short one
	[Fact]
	public async Task OverlongPasswordsAreRefused()
	{
		IServiceProvider provider = Services(new());
		var validator = new MaximumLengthPasswordValidator();

		Assert.True((await validator.ValidateAsync(null!, new User(), new string('p', 128))).Succeeded);
		IdentityResult tooLong = await validator.ValidateAsync(null!, new User(), new string('p', 129));
		Assert.False(tooLong.Succeeded);
		Assert.Equal(MaximumLengthPasswordValidator.ErrorCode, Assert.Single(tooLong.Errors).Code);
		Assert.Contains(provider.GetServices<IPasswordValidator<User>>(), v => v is MaximumLengthPasswordValidator);
	}

	private sealed class Factory : IDbContextFactory<AppDbContext>
	{
		private readonly ProbeDatabase m_probe;

		public Factory(ProbeDatabase probe)
		{
			m_probe = probe;
		}

		public AppDbContext CreateDbContext() => m_probe.Open();
	}

	private static async Task<(IServiceProvider Provider, User User)> Account(ProbeDatabase probe, string email)
	{
		IServiceProvider provider = Services(new() { ["Accounts:password:enabled"] = "true" }, new Factory(probe));
		UserManager<User> users = provider.GetRequiredService<UserManager<User>>();
		var user = new User { Username = "Player" };
		IdentityResult created = await users.CreateAsync(user, "correct horse battery");
		Assert.True(created.Succeeded, String.Join(", ", created.Errors.Select(e => e.Description)));
		Assert.True((await users.SetEmailAsync(user, email)).Succeeded);
		return (provider, user);
	}

	// the hash is stored, never the password; the email finds the account whatever its case
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task PasswordAndEmailRoundTrip(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(IServiceProvider services, User user) = await Account(probe, "Player@Example.org");
		UserManager<User> users = services.GetRequiredService<UserManager<User>>();

		User? found = await users.FindByEmailAsync("player@EXAMPLE.org");
		Assert.NotNull(found);
		Assert.Equal(user.ID, found!.ID);
		Assert.Equal("Player@Example.org", found.Email);
		Assert.Null(found.EmailConfirmedAt);
		Assert.True(await users.CheckPasswordAsync(found, "correct horse battery"));
		Assert.False(await users.CheckPasswordAsync(found, "wrong horse battery"));

		await using AppDbContext db = probe.Open();
		User row = await db.Users.SingleAsync(u => u.ID == user.ID);
		Assert.NotNull(row.PasswordHash);
		Assert.DoesNotContain("correct horse battery", row.PasswordHash!);
		Assert.Equal(NameNormalizer.Normalize("Player@Example.org"), row.EmailNormalized);
	}

	// the fifth wrong password locks the account for 15 minutes, and the lock is in the database
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task FiveFailuresLockTheAccount(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(IServiceProvider services, User user) = await Account(probe, "lock@example.org");
		SignInManager<User> signIn = services.GetRequiredService<SignInManager<User>>();
		UserManager<User> users = services.GetRequiredService<UserManager<User>>();

		for (int attempt = 1; attempt <= 4; attempt++)
		{
			SignInResult failed = await signIn.CheckPasswordSignInAsync((await users.FindByIdAsync(user.ID.ToString()))!, "wrong", lockoutOnFailure: true);
			Assert.False(failed.Succeeded);
			Assert.False(failed.IsLockedOut);
		}

		SignInResult locked = await signIn.CheckPasswordSignInAsync((await users.FindByIdAsync(user.ID.ToString()))!, "wrong", lockoutOnFailure: true);
		Assert.True(locked.IsLockedOut);

		// even the right password is refused while locked
		Assert.True((await signIn.CheckPasswordSignInAsync((await users.FindByIdAsync(user.ID.ToString()))!, "correct horse battery", lockoutOnFailure: true)).IsLockedOut);

		await using AppDbContext db = probe.Open();
		User row = await db.Users.SingleAsync(u => u.ID == user.ID);
		Assert.NotNull(row.LockoutEndAt);
		Assert.InRange(row.LockoutEndAt!.Value, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(16));
	}

	// a right password before the lock clears the failure count
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ASuccessResetsTheFailures(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(IServiceProvider services, User user) = await Account(probe, "reset@example.org");
		SignInManager<User> signIn = services.GetRequiredService<SignInManager<User>>();
		UserManager<User> users = services.GetRequiredService<UserManager<User>>();

		await signIn.CheckPasswordSignInAsync((await users.FindByIdAsync(user.ID.ToString()))!, "wrong", lockoutOnFailure: true);
		Assert.True((await signIn.CheckPasswordSignInAsync((await users.FindByIdAsync(user.ID.ToString()))!, "correct horse battery", lockoutOnFailure: true)).Succeeded);

		await using AppDbContext db = probe.Open();
		Assert.Equal(0, (await db.Users.SingleAsync(u => u.ID == user.ID)).FailedSignInCount);
	}

	// the authenticator key is encrypted at rest and read back as it was set
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheAuthenticatorKeyIsEncryptedAtRest(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(IServiceProvider services, User user) = await Account(probe, "totp@example.org");
		UserManager<User> users = services.GetRequiredService<UserManager<User>>();

		Assert.Null(await users.GetAuthenticatorKeyAsync(user));
		Assert.True((await users.ResetAuthenticatorKeyAsync(user)).Succeeded);
		string? key = await users.GetAuthenticatorKeyAsync((await users.FindByIdAsync(user.ID.ToString()))!);
		Assert.False(String.IsNullOrEmpty(key));

		await using AppDbContext db = probe.Open();
		string? stored = (await db.Users.SingleAsync(u => u.ID == user.ID)).AuthenticatorKey;
		Assert.NotNull(stored);
		Assert.DoesNotContain(key!, stored!);
	}

	// recovery codes are stored hashed, each works once, and new codes replace the old ones
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task RecoveryCodesAreHashedAndSingleUse(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(IServiceProvider services, User user) = await Account(probe, "codes@example.org");
		UserManager<User> users = services.GetRequiredService<UserManager<User>>();

		List<string> codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!.ToList();
		Assert.Equal(10, codes.Count);
		Assert.Equal(10, await users.CountRecoveryCodesAsync(user));

		await using (AppDbContext db = probe.Open())
		{
			List<string> stored = await db.UserRecoveryCodes.Where(c => c.UserID == user.ID).Select(c => c.CodeHash).ToListAsync();
			Assert.Equal(10, stored.Count);
			Assert.Empty(stored.Intersect(codes));
		}

		Assert.True((await users.RedeemTwoFactorRecoveryCodeAsync(user, codes[0])).Succeeded);
		Assert.False((await users.RedeemTwoFactorRecoveryCodeAsync(user, codes[0])).Succeeded);
		Assert.False((await users.RedeemTwoFactorRecoveryCodeAsync(user, "not-a-code")).Succeeded);
		Assert.Equal(9, await users.CountRecoveryCodesAsync(user));

		List<string> fresh = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!.ToList();
		Assert.False((await users.RedeemTwoFactorRecoveryCodeAsync(user, codes[1])).Succeeded);
		Assert.True((await users.RedeemTwoFactorRecoveryCodeAsync(user, fresh[0])).Succeeded);
		Assert.Equal(9, await users.CountRecoveryCodesAsync(user));
	}

	// the flag and the lockout state live on the user row, so they survive a new manager
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TwoFactorFlagIsStored(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(IServiceProvider services, User user) = await Account(probe, "flag@example.org");
		UserManager<User> users = services.GetRequiredService<UserManager<User>>();

		Assert.False(await users.GetTwoFactorEnabledAsync(user));
		Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
		Assert.True(await users.GetTwoFactorEnabledAsync((await users.FindByIdAsync(user.ID.ToString()))!));
	}
}
