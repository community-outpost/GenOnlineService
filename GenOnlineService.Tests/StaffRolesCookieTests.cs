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

// Staff roles and the audit log from the staff pages (T17c): one role granted or revoked through the sign-in cookie under
// the rank rule (the caller outranks the player and the role ranks below the caller), every change audited and applied
// to the role cache at once; the audit log read through the cookie, filtered and paged; bearer callers as before
[Collection(AdminAreaCollection.Name)]
public class StaffRolesCookieTests
{
	private static readonly object s_empty = new { };

	private static async Task<(AdminBrowser Browser, User User)> StaffAsync(ProbeDatabase probe, string name, string role)
	{
		var browser = new AdminBrowser(probe);
		string email = name.ToLowerInvariant() + "@example.org";
		await browser.SignUp(name, email);
		return (browser, await AdminArea.Grant(probe, email, role));
	}

	private static async Task<User> PlayerAsync(ProbeDatabase probe, string name)
	{
		var browser = new AdminBrowser(probe);
		string email = name.ToLowerInvariant() + "@example.org";
		await browser.SignUp(name, email);
		return (await PasswordPagesTests.UserByEmail(probe, email))!;
	}

	private static async Task<List<UserRole>> RolesOf(ProbeDatabase probe, long userID)
	{
		await using AppDbContext db = probe.Open();
		return await db.UserRoles.AsNoTracking().Where(r => r.UserID == userID).ToListAsync();
	}

	private static async Task<List<AuditLogEntry>> RoleAudit(ProbeDatabase probe, long target)
	{
		await using AppDbContext db = probe.Open();
		return await db.AuditLog.AsNoTracking()
			.Where(e => e.TargetKind == AuditTargetKinds.User && e.TargetID == target && (e.Action == ModerationActions.RoleGrant || e.Action == ModerationActions.RoleRevoke))
			.OrderBy(e => e.ID)
			.ToListAsync();
	}

	private static async Task AddUpstream(ProbeDatabase probe, long userID, string role)
	{
		await using AppDbContext db = probe.Open();
		db.UserRoles.Add(new UserRole { UserID = userID, Role = role, Source = AuthoritySources.Upstream("discord"), GrantedAt = DateTime.UtcNow, GrantedBy = "discord" });
		await db.SaveChangesAsync();
	}

	// A service account token holding the given permissions, the bearer caller of the operator routes
	private static async Task<Dictionary<string, string>> ServiceToken(ProbeDatabase probe, params string[] permissions)
	{
		string token = ApiTokenFormat.Generate();
		await using AppDbContext db = probe.Open();
		var account = new ServiceAccount { Name = "operator", CreatedBy = "test", CreatedAt = DateTime.UtcNow };
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

	private static List<string> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToList();

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnAdminGrantsAndRevokesALowerRoleThroughTheCookieAudited(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		User plain = await PlayerAsync(probe, "Plain");
		Dictionary<string, string> csrf = await admin.Csrf();

		PageResponse granted = await admin.SendJson("PUT", $"/api/v1/players/{plain.ID}/roles/Moderator", s_empty, csrf);
		Assert.Equal(200, granted.Status);
		Assert.Equal(["moderator"], Strings(JsonDocument.Parse(granted.Body).RootElement.GetProperty("roles")));
		UserRole row = Assert.Single(await RolesOf(probe, plain.ID));
		Assert.Equal((StaffRoles.Moderator, AuthoritySources.Local, $"user:{chief.ID}"), (row.Role, row.Source, row.GrantedBy));
		// the role cache follows at once, so the new moderator's permissions apply without a restart
		Assert.Contains(StaffRoles.Moderator, StaffRoles.RolesOf(plain.ID));
		Assert.True(StaffRoles.HasPermission(plain.ID, Permissions.ModerationKick));

		// granting a role held already changes nothing and writes no second audit row
		Assert.Equal(200, (await admin.SendJson("PUT", $"/api/v1/players/{plain.ID}/roles/moderator", s_empty, csrf)).Status);

		Assert.Equal(204, (await admin.SendJson("DELETE", $"/api/v1/players/{plain.ID}/roles/moderator", s_empty, csrf)).Status);
		Assert.Empty(await RolesOf(probe, plain.ID));
		Assert.DoesNotContain(StaffRoles.Moderator, StaffRoles.RolesOf(plain.ID));
		Assert.False(StaffRoles.HasPermission(plain.ID, Permissions.ModerationKick));

		List<AuditLogEntry> audit = await RoleAudit(probe, plain.ID);
		Assert.Equal([ModerationActions.RoleGrant, ModerationActions.RoleRevoke], audit.Select(e => e.Action));
		foreach (AuditLogEntry entry in audit)
		{
			Assert.Equal((AuditActorKinds.User, (long?)chief.ID, $"user:{chief.ID}", ModerationSources.Api), (entry.ActorKind, entry.ActorID, entry.Actor, entry.Source));
			JsonElement details = JsonDocument.Parse(entry.Details!).RootElement;
			Assert.Equal((StaffRoles.Moderator, AuthoritySources.Local), (details.GetProperty("role").GetString(), details.GetProperty("source").GetString()));
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheRankRuleRefusesRolesAtTheCallersRankPeersSelfAndProviderRows(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		(_, User peer) = await StaffAsync(probe, "Peer", StaffRoles.Admin);
		(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
		User plain = await PlayerAsync(probe, "Plain");
		Dictionary<string, string> csrf = await admin.Csrf();

		// a role of the caller's own rank, here admin, is never granted
		PageResponse above = await admin.SendJson("PUT", $"/api/v1/players/{plain.ID}/roles/admin", s_empty, csrf);
		Assert.Equal(403, above.Status);
		Assert.Equal("roles.rank", AdminArea.ErrorOf(above));

		// a peer of the same rank is out of reach, so is the caller themself
		PageResponse onPeer = await admin.SendJson("DELETE", $"/api/v1/players/{peer.ID}/roles/admin", s_empty, csrf);
		Assert.Equal(403, onPeer.Status);
		Assert.Equal("moderation.target_outranks", AdminArea.ErrorOf(onPeer));
		Assert.Equal(403, (await admin.SendJson("PUT", $"/api/v1/players/{peer.ID}/roles/streamer", s_empty, csrf)).Status);
		PageResponse onSelf = await admin.SendJson("DELETE", $"/api/v1/players/{chief.ID}/roles/admin", s_empty, csrf);
		Assert.Equal(400, onSelf.Status);
		Assert.Equal("invalid_argument", AdminArea.ErrorOf(onSelf));

		// a role from a sign-in provider follows its sign-ins
		await AddUpstream(probe, plain.ID, StaffRoles.Streamer);
		PageResponse upstream = await admin.SendJson("DELETE", $"/api/v1/players/{plain.ID}/roles/streamer", s_empty, csrf);
		Assert.Equal(409, upstream.Status);
		Assert.Equal("roles.upstream", AdminArea.ErrorOf(upstream));
		Assert.Equal(AuthoritySources.Upstream("discord"), Assert.Single(await RolesOf(probe, plain.ID)).Source);

		PageResponse none = await admin.SendJson("DELETE", $"/api/v1/players/{plain.ID}/roles/moderator", s_empty, csrf);
		Assert.Equal(404, none.Status);
		Assert.Equal("not_found", AdminArea.ErrorOf(none));
		Assert.Equal(400, (await admin.SendJson("PUT", $"/api/v1/players/{plain.ID}/roles/overlord", s_empty, csrf)).Status);

		// staff without roles.manage are refused whatever their rank over the player
		PageResponse withoutPermission = await moderator.SendJson("PUT", $"/api/v1/players/{plain.ID}/roles/streamer", s_empty, await moderator.Csrf());
		Assert.Equal(403, withoutPermission.Status);
		Assert.Equal("forbidden", AdminArea.ErrorOf(withoutPermission));

		Assert.Single(await RolesOf(probe, plain.ID));
		Assert.Equal([StaffRoles.Admin], (await RolesOf(probe, peer.ID)).Select(r => r.Role));
		Assert.Equal([StaffRoles.Admin], (await RolesOf(probe, chief.ID)).Select(r => r.Role));
		Assert.Empty(await RoleAudit(probe, plain.ID));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ACookieRoleChangeNeedsTheHeaderTokenAndOwnOrigin(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		(AdminBrowser other, _) = await StaffAsync(probe, "Other", StaffRoles.Admin);
		User plain = await PlayerAsync(probe, "Plain");
		string path = $"/api/v1/players/{plain.ID}/roles/moderator";

		PageResponse missing = await admin.SendJson("PUT", path, s_empty);
		Assert.Equal(400, missing.Status);
		Assert.Equal("csrf", AdminArea.ErrorOf(missing));
		Assert.Equal("csrf", AdminArea.ErrorOf(await admin.SendJson("PUT", path, s_empty, await other.Csrf())));
		Assert.Equal("csrf", AdminArea.ErrorOf(await admin.SendJson("DELETE", path, s_empty)));

		Dictionary<string, string> foreign = new(await admin.Csrf()) { ["Origin"] = "https://evil.example" };
		PageResponse fromElsewhere = await admin.SendJson("PUT", path, s_empty, foreign);
		Assert.Equal(403, fromElsewhere.Status);
		Assert.Equal("forbidden", AdminArea.ErrorOf(fromElsewhere));
		Assert.Empty(await RolesOf(probe, plain.ID));

		// the routes that set the whole list stay bearer only, a cookie alone is unauthorized as before
		Assert.Equal(401, (await admin.SendJson("PUT", $"/api/v1/players/{plain.ID}/roles", new { roles = new[] { "moderator" } }, await admin.Csrf())).Status);
		Assert.Equal(401, (await admin.Get($"/api/v1/players/{plain.ID}/roles")).Status);
		Assert.Equal(401, (await admin.SendJson("DELETE", $"/api/v1/players/{plain.ID}/roles", s_empty, await admin.Csrf())).Status);
		Assert.Empty(await RolesOf(probe, plain.ID));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ThePlayerPageListsRoleSourcesAndTheRolesTheCallerMayChange(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		(_, User peer) = await StaffAsync(probe, "Peer", StaffRoles.Admin);
		(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
		User plain = await PlayerAsync(probe, "Plain");
		await AddUpstream(probe, plain.ID, StaffRoles.Streamer);

		JsonElement view = JsonDocument.Parse((await admin.Get($"/api/v1/players/{plain.ID}/staff_view")).Body).RootElement;
		JsonElement grant = Assert.Single(view.GetProperty("role_grants").EnumerateArray());
		Assert.Equal(("streamer", "upstream:discord"), (grant.GetProperty("role").GetString(), grant.GetProperty("source").GetString()));
		Assert.Equal(["moderator", "streamer"], Strings(view.GetProperty("assignable_roles")));

		foreach (long outOfReach in new[] { peer.ID, chief.ID })
		{
			JsonElement other = JsonDocument.Parse((await admin.Get($"/api/v1/players/{outOfReach}/staff_view")).Body).RootElement;
			Assert.Empty(other.GetProperty("assignable_roles").EnumerateArray());
			Assert.Equal("local", other.GetProperty("role_grants")[0].GetProperty("source").GetString());
		}

		// a moderator sees the roles but may change none
		JsonElement asModerator = JsonDocument.Parse((await moderator.Get($"/api/v1/players/{plain.ID}/staff_view")).Body).RootElement;
		Assert.Single(asModerator.GetProperty("role_grants").EnumerateArray());
		Assert.Empty(asModerator.GetProperty("assignable_roles").EnumerateArray());
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task BearerRoleRoutesAnswerAsBeforeApartFromTheRankRule(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Dictionary<string, string> bearer = await ServiceToken(probe, Permissions.RolesManage);
		(_, User peer) = await StaffAsync(probe, "Peer", StaffRoles.Admin);
		User plain = await PlayerAsync(probe, "Plain");
		var caller = new AdminBrowser(probe);
		string roles = $"/api/v1/players/{plain.ID}/roles";

		// previous behaviour: GET, PUT of the whole list and DELETE, no antiforgery header needed
		PageResponse empty = await caller.Get(roles, bearer);
		Assert.Equal(200, empty.Status);
		Assert.Equal("{\"roles\":[]}", empty.Body);
		PageResponse put = await caller.SendJson("PUT", roles, new { roles = new[] { "Moderator", "streamer" } }, bearer);
		Assert.Equal(200, put.Status);
		Assert.Equal("{\"roles\":[\"moderator\",\"streamer\"]}", put.Body);
		Assert.Equal(400, (await caller.SendJson("PUT", roles, new { roles = new[] { "overlord" } }, bearer)).Status);
		Assert.Equal(204, (await caller.SendJson("DELETE", roles, s_empty, bearer)).Status);
		Assert.Empty(await RolesOf(probe, plain.ID));
		Assert.Equal(404, (await caller.Get("/api/v1/players/999999/roles", bearer)).Status);

		// a service account acts with admin standing, as for staff renames: admin itself and admins are out of reach
		PageResponse admin = await caller.SendJson("PUT", roles, new { roles = new[] { "admin" } }, bearer);
		Assert.Equal(403, admin.Status);
		Assert.Equal("roles.rank", AdminArea.ErrorOf(admin));
		PageResponse onPeer = await caller.SendJson("DELETE", $"/api/v1/players/{peer.ID}/roles", s_empty, bearer);
		Assert.Equal(403, onPeer.Status);
		Assert.Equal("moderation.target_outranks", AdminArea.ErrorOf(onPeer));
		// a request that changes nothing still answers as before
		Assert.Equal(200, (await caller.SendJson("PUT", $"/api/v1/players/{peer.ID}/roles", new { roles = new[] { "admin" } }, bearer)).Status);
		Assert.Equal([StaffRoles.Admin], (await RolesOf(probe, peer.ID)).Select(r => r.Role));

		// the one-role routes take the bearer token too, audited as the service account
		Assert.Equal(200, (await caller.SendJson("PUT", roles + "/streamer", s_empty, bearer)).Status);
		AuditLogEntry last = (await RoleAudit(probe, plain.ID)).Last();
		Assert.Equal((ModerationActions.RoleGrant, AuditActorKinds.ServiceAccount), (last.Action, last.ActorKind));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheAuditLogReadsThroughTheCookieFilteredAndPaged(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
		User plain = await PlayerAsync(probe, "Plain");
		Dictionary<string, string> csrf = await admin.Csrf();
		Assert.Equal(200, (await admin.SendJson("PUT", $"/api/v1/players/{plain.ID}/roles/moderator", s_empty, csrf)).Status);
		Assert.Equal(200, (await admin.SendJson("PUT", $"/api/v1/players/{plain.ID}/roles/streamer", s_empty, csrf)).Status);
		// a row of another kind with the player's id as its target id, which target_kind=user leaves out
		await using (AppDbContext db = probe.Open())
		{
			db.AuditLog.Add(new AuditLogEntry { CreatedAt = DateTime.UtcNow, ActorKind = AuditActorKinds.Cli, Actor = "cli", Action = "token_revoke", TargetKind = AuditTargetKinds.Token, TargetID = plain.ID, Source = ModerationSources.Cli });
			await db.SaveChangesAsync();
		}

		string forPlayer = $"/api/v1/audit_events?target_id={plain.ID}&target_kind=user";
		JsonElement first = JsonDocument.Parse((await admin.Get(forPlayer + "&per_page=1")).Body).RootElement.GetProperty("audit_events");
		JsonElement newest = Assert.Single(first.EnumerateArray());
		Assert.Equal(("role_grant", "streamer"), (newest.GetProperty("action").GetString(), newest.GetProperty("details").GetProperty("role").GetString()));
		Assert.Equal(chief.ID, newest.GetProperty("actor").GetProperty("id").GetInt64());
		JsonElement next = JsonDocument.Parse((await admin.Get(forPlayer + $"&per_page=1&after_id={newest.GetProperty("id").GetInt64()}")).Body).RootElement.GetProperty("audit_events");
		Assert.Equal("moderator", Assert.Single(next.EnumerateArray()).GetProperty("details").GetProperty("role").GetString());

		int anyKind = JsonDocument.Parse((await admin.Get($"/api/v1/audit_events?target_id={plain.ID}")).Body).RootElement.GetProperty("audit_events").GetArrayLength();
		int users = JsonDocument.Parse((await admin.Get(forPlayer)).Body).RootElement.GetProperty("audit_events").GetArrayLength();
		Assert.Equal(users + 1, anyKind);
		Assert.Equal(2, JsonDocument.Parse((await admin.Get($"/api/v1/audit_events?actor_id={chief.ID}&action=role_grant")).Body).RootElement.GetProperty("audit_events").GetArrayLength());
		Assert.Equal(0, JsonDocument.Parse((await admin.Get(forPlayer + "&action=role_revoke")).Body).RootElement.GetProperty("audit_events").GetArrayLength());
		PageResponse badKind = await admin.Get("/api/v1/audit_events?target_kind=");
		Assert.Equal(400, badKind.Status);
		Assert.Equal("invalid_argument", AdminArea.ErrorOf(badKind));

		// audit.read only, from the service's own origin
		PageResponse asModerator = await moderator.Get(forPlayer);
		Assert.Equal(403, asModerator.Status);
		Assert.Equal("forbidden", AdminArea.ErrorOf(asModerator));
		Assert.Equal(403, (await admin.Get(forPlayer, new() { ["Origin"] = "https://evil.example" })).Status);

		// previous behaviour: a bearer token with audit.read reads the same rows
		Dictionary<string, string> bearer = await ServiceToken(probe, Permissions.AuditRead);
		PageResponse viaToken = await new AdminBrowser(probe).Get($"/api/v1/audit_events?target_id={plain.ID}&action=role_grant", bearer);
		Assert.Equal(200, viaToken.Status);
		Assert.Equal(2, JsonDocument.Parse(viaToken.Body).RootElement.GetProperty("audit_events").GetArrayLength());
	}

	// The owner role (L69): rank 3 over admin, admin's permissions, granted with the CLI only, never by a provider
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnOwnerGrantsAndRevokesAdminButNeverOwner(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser owner, User boss) = await StaffAsync(probe, "Boss", "owner");
		(_, User other) = await StaffAsync(probe, "Other", "owner");
		User plain = await PlayerAsync(probe, "Plain");
		Dictionary<string, string> csrf = await owner.Csrf();

		JsonElement view = JsonDocument.Parse((await owner.Get($"/api/v1/players/{plain.ID}/staff_view")).Body).RootElement;
		Assert.Equal(["admin", "moderator", "streamer"], Strings(view.GetProperty("assignable_roles")));

		PageResponse granted = await owner.SendJson("PUT", $"/api/v1/players/{plain.ID}/roles/admin", s_empty, csrf);
		Assert.Equal(200, granted.Status);
		Assert.Equal(["admin"], Strings(JsonDocument.Parse(granted.Body).RootElement.GetProperty("roles")));
		Assert.True(StaffRoles.HasPermission(plain.ID, Permissions.RolesManage));
		Assert.Equal(204, (await owner.SendJson("DELETE", $"/api/v1/players/{plain.ID}/roles/admin", s_empty, csrf)).Status);
		Assert.Empty(await RolesOf(probe, plain.ID));
		Assert.Equal([ModerationActions.RoleGrant, ModerationActions.RoleRevoke], (await RoleAudit(probe, plain.ID)).Select(e => e.Action));

		// owner itself ranks at the caller's own rank, so nobody grants it through the API
		PageResponse ownerRole = await owner.SendJson("PUT", $"/api/v1/players/{plain.ID}/roles/owner", s_empty, csrf);
		Assert.Equal(403, ownerRole.Status);
		Assert.Equal("roles.rank", AdminArea.ErrorOf(ownerRole));

		// another owner is a peer, the owner themself is out of reach
		PageResponse onPeer = await owner.SendJson("PUT", $"/api/v1/players/{other.ID}/roles/streamer", s_empty, csrf);
		Assert.Equal(403, onPeer.Status);
		Assert.Equal("moderation.target_outranks", AdminArea.ErrorOf(onPeer));
		PageResponse onSelf = await owner.SendJson("DELETE", $"/api/v1/players/{boss.ID}/roles/owner", s_empty, csrf);
		Assert.Equal(400, onSelf.Status);
		Assert.Equal("invalid_argument", AdminArea.ErrorOf(onSelf));
		Assert.Empty(JsonDocument.Parse((await owner.Get($"/api/v1/players/{other.ID}/staff_view")).Body).RootElement.GetProperty("assignable_roles").EnumerateArray());
		Assert.Equal(["owner"], (await RolesOf(probe, other.ID)).Select(r => r.Role));
		Assert.Equal(["owner"], (await RolesOf(probe, boss.ID)).Select(r => r.Role));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task OwnerStaysOutOfReachOfAdminsAndServiceAccounts(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		(_, User peer) = await StaffAsync(probe, "Peer", StaffRoles.Admin);
		(_, User boss) = await StaffAsync(probe, "Boss", "owner");
		User plain = await PlayerAsync(probe, "Plain");
		Dictionary<string, string> csrf = await admin.Csrf();

		// previous behaviour: an admin grants moderator to a player, and is refused admin and on another admin
		Assert.Equal(200, (await admin.SendJson("PUT", $"/api/v1/players/{plain.ID}/roles/moderator", s_empty, csrf)).Status);
		Assert.Equal("roles.rank", AdminArea.ErrorOf(await admin.SendJson("PUT", $"/api/v1/players/{plain.ID}/roles/admin", s_empty, csrf)));
		Assert.Equal("moderation.target_outranks", AdminArea.ErrorOf(await admin.SendJson("DELETE", $"/api/v1/players/{peer.ID}/roles/admin", s_empty, csrf)));
		Assert.Equal(["moderator", "streamer"], Strings(JsonDocument.Parse((await admin.Get($"/api/v1/players/{plain.ID}/staff_view")).Body).RootElement.GetProperty("assignable_roles")));

		// an owner outranks an admin
		PageResponse onOwner = await admin.SendJson("PUT", $"/api/v1/players/{boss.ID}/roles/streamer", s_empty, csrf);
		Assert.Equal(403, onOwner.Status);
		Assert.Equal("moderation.target_outranks", AdminArea.ErrorOf(onOwner));
		Assert.Equal("roles.rank", AdminArea.ErrorOf(await admin.SendJson("PUT", $"/api/v1/players/{plain.ID}/roles/owner", s_empty, csrf)));

		// a service account keeps admin rank: owner is never assignable through the API
		Dictionary<string, string> bearer = await ServiceToken(probe, Permissions.RolesManage);
		PageResponse viaToken = await new AdminBrowser(probe).SendJson("PUT", $"/api/v1/players/{plain.ID}/roles", new { roles = new[] { "owner" } }, bearer);
		Assert.Equal(403, viaToken.Status);
		Assert.Equal("roles.rank", AdminArea.ErrorOf(viaToken));
		Assert.Equal(403, (await new AdminBrowser(probe).SendJson("DELETE", $"/api/v1/players/{boss.ID}/roles", s_empty, bearer)).Status);
		Assert.Equal(["owner"], (await RolesOf(probe, boss.ID)).Select(r => r.Role));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnOwnerRenamesAnAdminAsTheModerationRankRuleAllows(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser owner, _) = await StaffAsync(probe, "Boss", "owner");
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		(_, User other) = await StaffAsync(probe, "Other", "owner");
		// staff renames read the process-wide role cache
		await using (AppDbContext db = probe.Open())
		{
			await StaffRoles.Reload(db);
		}

		Assert.Equal(204, (await owner.Patch($"/api/v1/players/{chief.ID}", new { name = "Renamed", reason = "probe" }, await owner.Csrf())).Status);
		PageResponse onOwner = await admin.Patch($"/api/v1/players/{other.ID}", new { name = "Renamed", reason = "probe" }, await admin.Csrf());
		Assert.Equal(403, onOwner.Status);
		Assert.Equal("moderation.target_outranks", AdminArea.ErrorOf(onOwner));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AProviderSignInNeverGrantsOwner(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User plain = await PlayerAsync(probe, "Plain");
		string source = AuthoritySources.Upstream("discord");

		await using (AppDbContext db = probe.Open())
		{
			Database.Upstream.Changes changes = await Database.Upstream.Apply(db, plain.ID, source, ["owner", "moderator"], false, "");
			Assert.Equal(["moderator"], changes.RolesAdded);
		}
		Assert.Equal([("moderator", source)], (await RolesOf(probe, plain.ID)).Select(r => (r.Role, r.Source)));
		Assert.DoesNotContain(await RoleAudit(probe, plain.ID), e => e.Details!.Contains("owner"));
	}

	[Fact]
	public void AnOwnerHoldsEveryAdminPermissionAndTheTopRank()
	{
		Assert.True(StaffRoles.IsKnown(StaffRoles.Owner));
		Assert.True(StaffRoles.IsStaff("owner"));
		Assert.Equal(StaffRoles.PermissionsFor([StaffRoles.Admin]).Order(), StaffRoles.PermissionsFor(["owner"]).Order());
		Assert.Equal(3, StaffRoles.RankOf(["owner"]));
		Assert.Equal((2, 1, 0), (StaffRoles.RankOf([StaffRoles.Admin]), StaffRoles.RankOf([StaffRoles.Moderator]), StaffRoles.RankOf([StaffRoles.Streamer])));
		Assert.Equal(ClientFeatures.Defaults[StaffRoles.Admin], ClientFeatures.Defaults["owner"]);
		Assert.Equal((IReadOnlyList<string>?)["owner"], Rest.RoleRules.Normalize(["Owner"]).Roles);
	}
}
