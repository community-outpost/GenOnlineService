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
using GenOnlineService.Identity;
using GenOnlineService.Names;
using GenOnlineService.Realtime;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GenOnlineService.Tests;

// Renames against a real database (GENONLINE_PROBE_POSTGRES / GENONLINE_PROBE_MARIADB): staff renames with their audit
// row, and the static tag and the look-alike key of every rename
public class RenameProbeTests
{
	private sealed class Factory : IDbContextFactory<AppDbContext>
	{
		private readonly ProbeDatabase m_probe;

		public Factory(ProbeDatabase probe)
		{
			m_probe = probe;
		}

		public AppDbContext CreateDbContext() => m_probe.Open();
	}

	private static async Task<User> AddUser(ProbeDatabase probe, string name, int tag)
	{
		await using AppDbContext db = probe.Open();
		var user = new User { Username = name, UsernameNormalized = NameNormalizer.Normalize(name), NameTag = tag, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user;
	}

	private static async Task<(string Name, int Tag, int History, int Audit)> Read(ProbeDatabase probe, Int64 userID)
	{
		await using AppDbContext db = probe.Open();
		User user = await db.Users.SingleAsync(u => u.ID == userID);
		int history = await db.UserUsernameHistory.CountAsync(h => h.UserID == userID);
		int audit = await db.AuditLog.CountAsync(a => a.TargetID == userID && a.Action == ModerationActions.Rename);
		return (user.Username, user.NameTag, history, audit);
	}

	// a discord_admins entry: no account, admin standing
	private static readonly Database.ModerationActor s_bot = new() { Kind = AuditActorKinds.Discord, Name = "discord:99", Source = ModerationSources.Discord };

	// The bot (or any staff member) renames: the tag stays, and the full reason and both names reach audit_log
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task StaffRenameKeepsTheTagAndAuditsTheFullReason(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User user = await AddUser(probe, "Before", 7);
		PlayerNames names = new(new Factory(probe));

		string reason = new string('r', 500);
		Result<Empty> forced = await names.RenameByStaffAsync(s_bot, user.ID, "Player", reason);
		Assert.True(forced.Ok, forced.Code);
		Assert.Equal(("Player", 7, 1, 1), await Read(probe, user.ID));
		await using (AppDbContext db = probe.Open())
		{
			AuditLogEntry entry = await db.AuditLog.SingleAsync(a => a.TargetID == user.ID && a.Action == ModerationActions.Rename);
			Assert.Equal(reason, entry.Reason);
			Assert.Equal("discord:99", entry.Actor);
			Assert.Contains("Before#0007", entry.Details);
			Assert.Contains("Player#0007", entry.Details);
		}

		// renaming to the current name changes nothing and leaves no second audit row
		Assert.True((await names.RenameByStaffAsync(s_bot, user.ID, "Player", null)).Ok);
		Assert.Equal(("Player", 7, 1, 1), await Read(probe, user.ID));
	}

	// The tag never changes, and a look-alike of a name that holds the same tag is taken
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task RenameKeepsTheTagAndUsesTheLookAlikeKey(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User user = await AddUser(probe, "Before", 7);
		await AddUser(probe, "Bob", 7);
		await AddUser(probe, "Rob", 8);
		PlayerNames names = new(new Factory(probe));

		Assert.Equal(ErrorCodes.NameTaken, (await names.RenameSelfAsync(user.ID, "B0B")).Code);
		Assert.Equal(ErrorCodes.NameTaken, (await names.RenameSelfAsync(user.ID, "B\u043Eb")).Code);
		Assert.Equal(("Before", 7, 0, 0), await Read(probe, user.ID));

		// Rob holds tag 8, not 7
		Result<Empty> renamed = await names.RenameSelfAsync(user.ID, "Rob");
		Assert.True(renamed.Ok, renamed.Code);
		Assert.Equal(("Rob", 7, 1, 0), await Read(probe, user.ID));

		Assert.True((await names.RenameSelfAsync(user.ID, "Rob")).Ok);
		Assert.Equal(("Rob", 7, 1, 0), await Read(probe, user.ID));
	}

	// Two look-alike renames onto one tag at once: whether the check or the unique (name_key, name_tag) index stops the
	// second, exactly one wins and the other answers name.taken
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ConcurrentLookAlikeRenamesOnOneTagLetExactlyOneWin(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		PlayerNames names = new(new Factory(probe));

		for (int tag = 1; tag <= 10; tag++)
		{
			User first = await AddUser(probe, "Anna", tag);
			User second = await AddUser(probe, "Bert", tag);

			Result<Empty>[] results = await Task.WhenAll(
				Task.Run(() => names.RenameSelfAsync(first.ID, "Bob")),
				Task.Run(() => names.RenameSelfAsync(second.ID, "B0b")));

			Assert.Single(results, r => r.Ok);
			Assert.Single(results, r => !r.Ok && r.Code == ErrorCodes.NameTaken);
			await using AppDbContext db = probe.Open();
			Assert.Equal(1, await db.Users.CountAsync(u => u.NameTag == tag && u.NameKey == "bob"));
			Assert.Equal(1, await db.UserUsernameHistory.CountAsync(h => h.UserID == first.ID || h.UserID == second.ID));
		}
	}
}
