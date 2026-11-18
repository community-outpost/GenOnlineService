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
using Xunit;

namespace GenOnlineService.Tests;

// The operator's resets (identity hub T3d, "GenOnlineService users ..."): a new password and the end of two-factor
// sign-in for an account whose owner lost them, each audited, without mail
public class UserResetsTests
{
	private static async Task<User> AddUser(ProbeDatabase probe, string? email, bool twoFactor = false)
	{
		await using AppDbContext db = probe.Open();
		var user = new User
		{
			Username = "Lost", UsernameNormalized = NameNormalizer.Normalize("Lost"), NameTag = 7,
			Email = email, EmailNormalized = email == null ? null : NameNormalizer.Normalize(email),
			PasswordHash = "old-hash", SecurityStamp = "old-stamp", TwoFactorEnabled = twoFactor, AuthenticatorKey = twoFactor ? "protected-key" : null,
			LockoutEndAt = DateTime.UtcNow.AddMinutes(10), FailedSignInCount = 5, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
		};
		db.Users.Add(user);
		await db.SaveChangesAsync();
		if (twoFactor)
		{
			db.UserRecoveryCodes.Add(new UserRecoveryCode { UserID = user.ID, CodeHash = new string('a', 64), CreatedAt = DateTime.UtcNow });
			await db.SaveChangesAsync();
		}
		return user;
	}

	// without a given password a new one is made and returned once; the lock ends and old sessions stop
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task PasswordResetSetsANewPasswordAndUnlocks(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User user = await AddUser(probe, "lost@example.org");

		string password;
		await using (AppDbContext db = probe.Open())
		{
			password = await UserResets.ResetPassword(db, user.ID, null, 8, "cli:operator", "forgot it");
		}

		Assert.True(password.Length >= 16);
		await using AppDbContext check = probe.Open();
		User row = await check.Users.SingleAsync(u => u.ID == user.ID);
		Assert.Equal(PasswordVerificationResult.Success, new PasswordHasher<User>().VerifyHashedPassword(row, row.PasswordHash!, password));
		Assert.Null(row.LockoutEndAt);
		Assert.Equal(0, row.FailedSignInCount);
		Assert.NotEqual("old-stamp", row.SecurityStamp);

		AuditLogEntry audit = await check.AuditLog.SingleAsync(a => a.TargetID == user.ID && a.Action == UserResets.PasswordResetAction);
		Assert.Equal(AuditActorKinds.Cli, audit.ActorKind);
		Assert.Equal("cli:operator", audit.Actor);
		Assert.Equal(AuditTargetKinds.User, audit.TargetKind);
		Assert.Equal("forgot it", audit.Reason);
		Assert.DoesNotContain(password, audit.Details ?? String.Empty);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AGivenPasswordIsUsedWhenLongEnough(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User user = await AddUser(probe, "given@example.org");

		await using AppDbContext db = probe.Open();
		Assert.Equal("chosen by the operator", await UserResets.ResetPassword(db, user.ID, "chosen by the operator", 8, "cli:operator", null));
		InvalidOperationException tooShort = await Assert.ThrowsAsync<InvalidOperationException>(() => UserResets.ResetPassword(db, user.ID, "short", 8, "cli:operator", null));
		Assert.Contains("at least 8 characters", tooShort.Message);
		await Assert.ThrowsAsync<InvalidOperationException>(() => UserResets.ResetPassword(db, user.ID, new string('p', 129), 8, "cli:operator", null));
	}

	// a password needs an email address to sign in with; an unknown id is an error
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task PasswordResetNeedsAnAccountWithAnEmail(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User providerOnly = await AddUser(probe, null);

		await using AppDbContext db = probe.Open();
		InvalidOperationException noEmail = await Assert.ThrowsAsync<InvalidOperationException>(() => UserResets.ResetPassword(db, providerOnly.ID, null, 8, "cli:operator", null));
		Assert.Contains("no email address", noEmail.Message);
		InvalidOperationException unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => UserResets.ResetPassword(db, 999_999, null, 8, "cli:operator", null));
		Assert.Contains("999999", unknown.Message);
		Assert.Equal(0, await db.AuditLog.CountAsync());
	}

	// two-factor sign-in ends: no flag, no key, no recovery codes, old sessions stop
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TwoFactorResetClearsEverything(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User user = await AddUser(probe, "phone@example.org", twoFactor: true);

		await using (AppDbContext db = probe.Open())
		{
			await UserResets.ResetTwoFactor(db, user.ID, "cli:operator", "lost phone");
		}

		await using AppDbContext check = probe.Open();
		User row = await check.Users.SingleAsync(u => u.ID == user.ID);
		Assert.False(row.TwoFactorEnabled);
		Assert.Null(row.AuthenticatorKey);
		Assert.NotEqual("old-stamp", row.SecurityStamp);
		Assert.Equal("old-hash", row.PasswordHash);
		Assert.Equal(0, await check.UserRecoveryCodes.CountAsync(c => c.UserID == user.ID));
		AuditLogEntry audit = await check.AuditLog.SingleAsync(a => a.TargetID == user.ID && a.Action == UserResets.TwoFactorResetAction);
		Assert.Equal("lost phone", audit.Reason);
	}

	// the CLI's rows stay as they were with the staff pages sharing the resets: a cli actor without an id, source cli
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task CliResetsKeepTheirAuditRows(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User user = await AddUser(probe, "rows@example.org", twoFactor: true);

		await using (AppDbContext db = probe.Open())
		{
			await UserResets.ResetPassword(db, user.ID, null, 8, "cli:operator", " forgot it ");
			await UserResets.ResetTwoFactor(db, user.ID, "cli:operator", null);
		}

		await using AppDbContext check = probe.Open();
		List<AuditLogEntry> rows = await check.AuditLog.Where(a => a.TargetID == user.ID).OrderBy(a => a.ID).ToListAsync();
		Assert.Equal(new[] { UserResets.PasswordResetAction, UserResets.TwoFactorResetAction }, rows.Select(r => r.Action));
		Assert.All(rows, r => Assert.Equal((AuditActorKinds.Cli, (long?)null, "cli:operator", "cli", "{}"), (r.ActorKind, r.ActorID, r.Actor, r.Source, r.Details)));
		Assert.Equal(new string?[] { "forgot it", null }, rows.Select(r => r.Reason));
	}

	// the command line: unknown commands and options print the usage and exit 2 before touching a database
	[Theory]
	[InlineData("users")]
	[InlineData("users", "rename", "1")]
	[InlineData("users", "password-reset")]
	[InlineData("users", "password-reset", "notanumber")]
	[InlineData("users", "password-reset", "1", "--password", "visible")]
	public async Task BadCommandLinesExitTwo(params string[] args)
	{
		Assert.Equal(2, await UsersCli.RunUsers(args));
	}
}
