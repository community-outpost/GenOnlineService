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
using Xunit;

namespace GenOnlineService.Tests;

// The staff operations of the staff pages (T2): the settings (MOTD), announcements, name resets and message deletes
// through the sign-in cookie under their permissions and the antiforgery rule, audited as through a bearer token; the
// report queue shows staff the reported message
[Collection(AdminAreaCollection.Name)]
public class StaffPortalOpsTests
{
	private static async Task<(AdminBrowser Browser, User User)> StaffAsync(ProbeDatabase probe, string name, string role)
	{
		var browser = new AdminBrowser(probe);
		string email = name.ToLowerInvariant() + "@example.org";
		await browser.SignUp(name, email);
		return (browser, await AdminArea.Grant(probe, email, role));
	}

	private static async Task<(AdminBrowser Browser, User User)> PlayerAsync(ProbeDatabase probe, string name)
	{
		var browser = new AdminBrowser(probe);
		string email = name.ToLowerInvariant() + "@example.org";
		await browser.SignUp(name, email);
		return (browser, (await PasswordPagesTests.UserByEmail(probe, email))!);
	}

	// A signed-in account that held a staff role when it opened the staff pages and lost it since: the cookie and its
	// antiforgery token stay valid, the permission checks read the role cache
	private static async Task<(AdminBrowser Browser, Dictionary<string, string> Csrf)> FormerStaffAsync(ProbeDatabase probe, string name)
	{
		(AdminBrowser browser, User user) = await StaffAsync(probe, name, StaffRoles.Moderator);
		Dictionary<string, string> csrf = await browser.Csrf();
		await using AppDbContext db = probe.Open();
		await db.UserRoles.Where(r => r.UserID == user.ID).ExecuteDeleteAsync();
		await StaffRoles.Reload(db);
		return (browser, csrf);
	}

	private static async Task<List<AuditLogEntry>> AuditOf(ProbeDatabase probe, string action)
	{
		await using AppDbContext db = probe.Open();
		return await db.AuditLog.AsNoTracking().Where(e => e.Action == action).OrderBy(e => e.ID).ToListAsync();
	}

	private static async Task<string?> StoredMotd(ProbeDatabase probe)
	{
		await using AppDbContext db = probe.Open();
		return await Database.Upstream.GetSetting(db, Rest.SettingKeys.Motd);
	}

	private static async Task<ChatMessage> MessageAsync(ProbeDatabase probe, long senderID, string content)
	{
		await using AppDbContext db = probe.Open();
		var message = new ChatMessage { Kind = ChatMessageKinds.Channel, ChannelID = 1, SenderID = senderID, Type = ChatMessageTypes.Chat, Content = content, CreatedAt = DateTime.UtcNow };
		db.ChatMessages.Add(message);
		await db.SaveChangesAsync();
		return message;
	}

	private static async Task<ChatMessage?> StoredMessage(ProbeDatabase probe, long id)
	{
		await using AppDbContext db = probe.Open();
		return await db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(m => m.ID == id);
	}

	// A service account token holding the given permissions, the bearer caller of the operator routes
	private static async Task<Dictionary<string, string>> ServiceToken(ProbeDatabase probe, params string[] permissions)
	{
		string token = ApiTokenFormat.Generate();
		await using AppDbContext db = probe.Open();
		var account = new ServiceAccount { Name = "operator-" + Guid.NewGuid().ToString("N").Substring(0, 8), CreatedBy = "test", CreatedAt = DateTime.UtcNow };
		db.ServiceAccounts.Add(account);
		await db.SaveChangesAsync();
		foreach (string permission in permissions)
		{
			db.ServiceAccountPermissions.Add(new ServiceAccountPermission { ServiceAccountID = account.ID, Permission = permission, GrantedBy = "test", GrantedAt = DateTime.UtcNow });
		}
		db.ApiTokens.Add(new ApiToken
		{
			OwnerServiceID = account.ID,
			Name = "operator",
			TokenHash = ApiTokenFormat.Hash(token),
			TokenLast4 = ApiTokenFormat.Last4(token),
			Scopes = String.Join(" ", permissions),
			CreatedBy = "test",
			CreatedAt = DateTime.UtcNow,
			ExpiresAt = DateTime.UtcNow.AddDays(1)
		});
		await db.SaveChangesAsync();
		await ApiTokenCache.Reload(db);
		return new Dictionary<string, string> { ["Authorization"] = "Bearer " + token };
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnAdminReadsAndSetsTheMotdThroughTheCookieAudited(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
		const string path = "/api/v1/application/settings";

		PageResponse empty = await admin.Get(path);
		Assert.Equal(200, empty.Status);
		Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(empty.Body).RootElement.GetProperty("motd").ValueKind);

		PageResponse saved = await admin.SendJson("PUT", path, new { motd = "Welcome back, commanders" }, await admin.Csrf());
		Assert.Equal(200, saved.Status);
		Assert.Equal("Welcome back, commanders", JsonDocument.Parse(saved.Body).RootElement.GetProperty("motd").GetString());
		Assert.Equal("Welcome back, commanders", JsonDocument.Parse((await admin.Get(path)).Body).RootElement.GetProperty("motd").GetString());
		Assert.Equal("\"Welcome back, commanders\"", await StoredMotd(probe));

		AuditLogEntry entry = Assert.Single(await AuditOf(probe, ModerationActions.SettingUpdate));
		Assert.Equal((AuditActorKinds.User, (long?)chief.ID, AuditTargetKinds.Setting), (entry.ActorKind, entry.ActorID, entry.TargetKind));
		Assert.Equal("Welcome back, commanders", JsonDocument.Parse(entry.Details!).RootElement.GetProperty("value").GetString());

		// no antiforgery header, no change
		PageResponse forged = await admin.SendJson("PUT", path, new { motd = "forged" }, null);
		Assert.Equal(400, forged.Status);
		Assert.Equal("csrf", AdminArea.ErrorOf(forged));

		// settings.manage only: a moderator neither reads nor writes
		PageResponse read = await moderator.Get(path);
		Assert.Equal(403, read.Status);
		Assert.Equal("forbidden", AdminArea.ErrorOf(read));
		Assert.Equal(403, (await moderator.SendJson("PUT", path, new { motd = "mine" }, await moderator.Csrf())).Status);
		Assert.Equal("\"Welcome back, commanders\"", await StoredMotd(probe));
		Assert.Single(await AuditOf(probe, ModerationActions.SettingUpdate));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task BearerSettingsAnswerAsBefore(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Dictionary<string, string> bearer = await ServiceToken(probe, Permissions.SettingsManage);
		var caller = new AdminBrowser(probe);

		PageResponse saved = await caller.SendJson("PUT", "/api/v1/application/settings", new { motd = "From the operator" }, bearer);
		Assert.Equal(200, saved.Status);
		Assert.Equal("{\"motd\":\"From the operator\"}", saved.Body);
		Assert.Equal("{\"motd\":\"From the operator\"}", (await caller.Get("/api/v1/application/settings", bearer)).Body);
		Assert.Equal(AuditActorKinds.ServiceAccount, Assert.Single(await AuditOf(probe, ModerationActions.SettingUpdate)).ActorKind);
		Assert.Equal(403, (await caller.Get("/api/v1/application/settings", await ServiceToken(probe, Permissions.AuditRead))).Status);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnAdminSendsAnAnnouncementThroughTheCookieAudited(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
		const string path = "/api/v1/announcements";

		PageResponse sent = await admin.SendJson("POST", path, new { message = "  Servers restart in 10 minutes  " }, await admin.Csrf());
		Assert.Equal(200, sent.Status);
		Assert.Equal(0, JsonDocument.Parse(sent.Body).RootElement.GetProperty("delivered_to").GetInt32());
		AuditLogEntry entry = Assert.Single(await AuditOf(probe, ModerationActions.Announcement));
		Assert.Equal((AuditActorKinds.User, (long?)chief.ID, AuditTargetKinds.Announcement), (entry.ActorKind, entry.ActorID, entry.TargetKind));
		Assert.Equal("Servers restart in 10 minutes", JsonDocument.Parse(entry.Details!).RootElement.GetProperty("message").GetString());

		PageResponse empty = await admin.SendJson("POST", path, new { message = " " }, await admin.Csrf());
		Assert.Equal(400, empty.Status);
		Assert.Equal("invalid_argument", AdminArea.ErrorOf(empty));

		PageResponse forged = await admin.SendJson("POST", path, new { message = "forged" }, null);
		Assert.Equal(400, forged.Status);
		Assert.Equal("csrf", AdminArea.ErrorOf(forged));

		// announcements.send only
		PageResponse refused = await moderator.SendJson("POST", path, new { message = "mine" }, await moderator.Csrf());
		Assert.Equal(403, refused.Status);
		Assert.Equal("forbidden", AdminArea.ErrorOf(refused));
		Assert.Single(await AuditOf(probe, ModerationActions.Announcement));

		// the page lists the recent ones from the audit log
		JsonElement recent = JsonDocument.Parse((await admin.Get("/api/v1/audit_events?action=announcement")).Body).RootElement.GetProperty("audit_events");
		Assert.Equal("Servers restart in 10 minutes", Assert.Single(recent.EnumerateArray()).GetProperty("details").GetProperty("message").GetString());
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task BearerAnnouncementsAnswerAsBefore(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Dictionary<string, string> bearer = await ServiceToken(probe, Permissions.AnnouncementsSend);

		PageResponse sent = await new AdminBrowser(probe).SendJson("POST", "/api/v1/announcements", new { message = "From the operator" }, bearer);
		Assert.Equal(200, sent.Status);
		Assert.Equal("{\"delivered_to\":0}", sent.Body);
		Assert.Equal(AuditActorKinds.ServiceAccount, Assert.Single(await AuditOf(probe, ModerationActions.Announcement)).ActorKind);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AModeratorResetsANameThroughTheCookieAudited(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser moderator, User warden) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
		(_, User plain) = await PlayerAsync(probe, "Plain");
		(_, User other) = await PlayerAsync(probe, "Other");
		(AdminBrowser former, Dictionary<string, string> formerCsrf) = await FormerStaffAsync(probe, "Former");
		// the rank rule reads the process-wide role cache
		await using (AppDbContext db = probe.Open())
		{
			await StaffRoles.Reload(db);
		}
		string path = $"/api/v1/players/{plain.ID}/reset_name";

		PageResponse forged = await moderator.SendJson("POST", path, new { reason = "offensive" }, null);
		Assert.Equal(400, forged.Status);
		Assert.Equal("csrf", AdminArea.ErrorOf(forged));
		Assert.Equal("Plain", (await PasswordPagesTests.UserByEmail(probe, "plain@example.org"))!.Username);

		// moderation.rename only, a cookie without it is refused
		PageResponse refused = await former.SendJson("POST", $"/api/v1/players/{other.ID}/reset_name", new { reason = "x" }, formerCsrf);
		Assert.Equal(403, refused.Status);
		Assert.Equal("forbidden", AdminArea.ErrorOf(refused));
		Assert.Equal("Other", (await PasswordPagesTests.UserByEmail(probe, "other@example.org"))!.Username);

		Assert.Equal(204, (await moderator.SendJson("POST", path, new { reason = "offensive" }, await moderator.Csrf())).Status);
		User reset = (await PasswordPagesTests.UserByEmail(probe, "plain@example.org"))!;
		Assert.NotEqual("Plain", reset.Username);
		Assert.NotNull(reset.NameRequiredAt);
		AuditLogEntry entry = Assert.Single(await AuditOf(probe, ModerationActions.NameReset));
		Assert.Equal((AuditActorKinds.User, (long?)warden.ID, (long?)plain.ID, "offensive"), (entry.ActorKind, entry.ActorID, entry.TargetID, entry.Reason));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AModeratorDeletesAReportedMessageThroughTheCookieAudited(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser moderator, User warden) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
		(_, User plain) = await PlayerAsync(probe, "Plain");
		(AdminBrowser former, Dictionary<string, string> formerCsrf) = await FormerStaffAsync(probe, "Former");
		await using (AppDbContext db = probe.Open())
		{
			await StaffRoles.Reload(db);
		}
		ChatMessage message = await MessageAsync(probe, plain.ID, "something rude");
		string path = $"/api/v1/messages/{message.ID}";

		PageResponse forged = await moderator.SendJson("DELETE", path, new { reason = "abuse" }, null);
		Assert.Equal(400, forged.Status);
		Assert.Equal("csrf", AdminArea.ErrorOf(forged));

		// chat.delete_any only, a cookie without it is refused
		PageResponse refused = await former.SendJson("DELETE", path, new { reason = "abuse" }, formerCsrf);
		Assert.Equal(403, refused.Status);
		Assert.Equal("forbidden", AdminArea.ErrorOf(refused));
		Assert.Null((await StoredMessage(probe, message.ID))!.DeletedAt);

		Assert.Equal(204, (await moderator.SendJson("DELETE", path, new { reason = "abuse" }, await moderator.Csrf())).Status);
		Assert.NotNull((await StoredMessage(probe, message.ID))!.DeletedAt);
		AuditLogEntry entry = Assert.Single(await AuditOf(probe, ModerationActions.MessageDelete));
		Assert.Equal((AuditActorKinds.User, (long?)warden.ID, (long?)message.ID, "abuse"), (entry.ActorKind, entry.ActorID, entry.TargetID, entry.Reason));

		// deleting it again answers the same and writes no second row
		Assert.Equal(204, (await moderator.SendJson("DELETE", path, new { reason = "abuse" }, await moderator.Csrf())).Status);
		Assert.Single(await AuditOf(probe, ModerationActions.MessageDelete));
	}

	// a bearer value on a route that also takes the cookie stays a bearer call: an unknown token is unauthorized and
	// changes nothing, whatever cookie comes with it
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task BearerCallsOnTheModerationRoutesStayBearerCalls(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
		(_, User plain) = await PlayerAsync(probe, "Plain");
		ChatMessage message = await MessageAsync(probe, plain.ID, "something rude");
		var unknown = new Dictionary<string, string> { ["Authorization"] = "Bearer not-a-session" };

		Assert.Equal(401, (await moderator.SendJson("POST", $"/api/v1/players/{plain.ID}/reset_name", new { reason = "x" }, unknown)).Status);
		Assert.Equal(401, (await moderator.SendJson("DELETE", $"/api/v1/messages/{message.ID}", new { reason = "x" }, unknown)).Status);
		Assert.Equal(401, (await new AdminBrowser(probe).SendJson("DELETE", $"/api/v1/messages/{message.ID}", new { }, null)).Status);
		Assert.Equal("Plain", (await PasswordPagesTests.UserByEmail(probe, "plain@example.org"))!.Username);
		Assert.Null((await StoredMessage(probe, message.ID))!.DeletedAt);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheReportQueueShowsStaffTheReportedMessage(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser moderator, User warden) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
		(AdminBrowser player, User plain) = await PlayerAsync(probe, "Plain");
		ChatMessage message = await MessageAsync(probe, plain.ID, "something rude");
		await using (AppDbContext db = probe.Open())
		{
			Assert.True((await Database.Chat.CreateReport(db, message, warden.ID, "abuse")).ReportID > 0);
			db.PlayerReports.Add(new PlayerReport { ReporterID = warden.ID, ReportedUserID = plain.ID, Reason = "griefing", Evidence = "{\"messages\":[]}", Status = PlayerReportStatuses.Open, CreatedAt = DateTime.UtcNow });
			await db.SaveChangesAsync();
		}

		PageResponse queue = await moderator.Get("/api/v1/reports/search?sort=asc");
		Assert.Equal(200, queue.Status);
		JsonElement[] reports = JsonDocument.Parse(queue.Body).RootElement.GetProperty("reports").EnumerateArray().ToArray();
		Assert.Equal(2, reports.Length);
		JsonElement ofMessage = reports.Single(r => r.GetProperty("reason").GetString() == "abuse");
		Assert.Equal(message.ID.ToString(), ofMessage.GetProperty("message_id").GetString());
		Assert.True(ofMessage.TryGetProperty("message_content", out JsonElement content), queue.Body);
		Assert.Equal("something rude", content.GetString());
		// a report of a player names no message
		JsonElement ofPlayer = reports.Single(r => r.GetProperty("reason").GetString() == "griefing");
		Assert.False(ofPlayer.TryGetProperty("message_content", out _));

		// players never read the queue
		PageResponse refused = await player.Get("/api/v1/reports/search");
		Assert.Equal(403, refused.Status);
		Assert.DoesNotContain("something rude", refused.Body);
	}
}
