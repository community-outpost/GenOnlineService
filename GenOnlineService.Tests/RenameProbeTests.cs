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

// Renames against a real database (GENONLINE_PROBE_POSTGRES / GENONLINE_PROBE_MARIADB): the prod path with a fake
// plugin, the managed answer, and the local path with its static tag and the plugin's key
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

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task PluginAnswerAppliesNameAndTagAtOnce(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User user = await AddUser(probe, "Before", 7);
		FakeRenamePlugin plugin = new() { Rename = (_, _) => Task.FromResult(FakeRenamePlugin.Json(200, "{\"player_id\":1,\"name\":\"After\",\"tag\":\"0007\",\"seq\":11}")) };
		AccountsSettings accounts = RenameTests.Accounts(playerIDs: true, rename: true);
		PlayerNames names = new(new Factory(probe), accounts, new KeycloakRenameClient(plugin, accounts));

		Result<Empty> own = await names.RenameSelfAsync(user.ID, "After");
		Assert.True(own.Ok, own.Code);
		Assert.Equal(("After", 7, 1, 0), await Read(probe, user.ID));
		Assert.Contains($"\"actor\":\"player:{user.ID}\"", plugin.RenameRequests[0].Body);

		// the bot: its actor and the full reason reach the plugin and audit_log
		plugin.Rename = (_, _) => Task.FromResult(FakeRenamePlugin.Json(200, "{\"player_id\":1,\"name\":\"Player\",\"tag\":\"0007\",\"seq\":12}"));
		string reason = new string('r', 500);
		Result<Empty> forced = await names.RenameByStaffAsync(s_bot, user.ID, "Player", reason);
		Assert.True(forced.Ok, forced.Code);
		Assert.Equal(("Player", 7, 2, 1), await Read(probe, user.ID));
		Assert.Contains("\"actor\":\"discord:99\"", plugin.RenameRequests[1].Body);
		Assert.Contains($"\"reason\":\"{reason}\"", plugin.RenameRequests[1].Body);
		await using (AppDbContext db = probe.Open())
		{
			AuditLogEntry entry = await db.AuditLog.SingleAsync(a => a.TargetID == user.ID && a.Action == ModerationActions.Rename);
			Assert.Equal(reason, entry.Reason);
			Assert.Equal("discord:99", entry.Actor);
			Assert.Contains("After#0007", entry.Details);
			Assert.Contains("Player#0007", entry.Details);
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task PluginRefusalsAndOutagesChangeNothing(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User user = await AddUser(probe, "Before", 7);
		FakeRenamePlugin plugin = new() { Rename = (_, _) => Task.FromResult(FakeRenamePlugin.Json(409, "{\"error\":\"name.taken\",\"params\":{}}")) };
		AccountsSettings accounts = RenameTests.Accounts(playerIDs: true, rename: true);
		PlayerNames names = new(new Factory(probe), accounts, new KeycloakRenameClient(plugin, accounts));

		Assert.Equal(ErrorCodes.NameTaken, (await names.RenameSelfAsync(user.ID, "Taken")).Code);

		plugin.Rename = (_, _) => throw new HttpRequestException("connection refused");
		Assert.Equal(ErrorCodes.NameUnavailable, (await names.RenameSelfAsync(user.ID, "Other")).Code);

		plugin.Rename = (_, _) => Task.FromResult(FakeRenamePlugin.Json(502, "bad gateway"));
		Assert.Equal(ErrorCodes.NameUnavailable, (await names.RenameSelfAsync(user.ID, "Other")).Code);

		Assert.Equal(("Before", 7, 0, 0), await Read(probe, user.ID));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task KeycloakWithoutTheRenameClientAnswersManaged(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User user = await AddUser(probe, "Before", 7);
		FakeRenamePlugin plugin = new();
		AccountsSettings accounts = RenameTests.Accounts(playerIDs: true, rename: false);
		PlayerNames names = new(new Factory(probe), accounts, new KeycloakRenameClient(plugin, accounts));

		Assert.Equal(ErrorCodes.NameManaged, (await names.RenameSelfAsync(user.ID, "After")).Code);
		Assert.Equal(ErrorCodes.NameManaged, (await names.RenameByStaffAsync(s_bot, user.ID, "After", null)).Code);
		Assert.Empty(plugin.RenameRequests);
		Assert.Equal(("Before", 7, 0, 0), await Read(probe, user.ID));
	}

	// Self-hosted: the tag never changes, and a look-alike of a name that holds the same tag is taken
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task LocalRenameKeepsTheTagAndUsesThePluginKey(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User user = await AddUser(probe, "Before", 7);
		await AddUser(probe, "Bob", 7);
		await AddUser(probe, "Rob", 8);
		FakeRenamePlugin plugin = new();
		AccountsSettings accounts = AccountsSettings.Read(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
		PlayerNames names = new(new Factory(probe), accounts, new KeycloakRenameClient(plugin, accounts));

		Assert.Equal(ErrorCodes.NameTaken, (await names.RenameSelfAsync(user.ID, "B0B")).Code);
		Assert.Equal(ErrorCodes.NameTaken, (await names.RenameSelfAsync(user.ID, "B\u043Eb")).Code);
		Assert.Equal(("Before", 7, 0, 0), await Read(probe, user.ID));

		// Rob holds tag 8, not 7
		Result<Empty> renamed = await names.RenameSelfAsync(user.ID, "Rob");
		Assert.True(renamed.Ok, renamed.Code);
		Assert.Equal(("Rob", 7, 1, 0), await Read(probe, user.ID));

		Assert.True((await names.RenameSelfAsync(user.ID, "Rob")).Ok);
		Assert.Equal(("Rob", 7, 1, 0), await Read(probe, user.ID));
		Assert.Empty(plugin.RenameRequests);
	}
}
