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
using GenOnlineService.Investigation;
using GenOnlineService.Portal.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

using static GenOnlineService.Tests.InvestigationProbe;

// The investigation search of the staff pages (S9): every account that used a device or network identifier, through
// the sign-in cookie, audited per search
[Collection(AdminAreaCollection.Name)]
public class InvestigationTests
{
	private sealed record Seeded(long A, long B, long C);

	// A and B share the MAC, machine id and volume serial and sign in from 203.0.113.0/24 in NL; C is elsewhere
	private static async Task<Seeded> SeedAsync(ProbeDatabase probe)
	{
		long a = await AccountAsync(probe, "Alpha");
		long b = await AccountAsync(probe, "Bravo");
		long c = await AccountAsync(probe, "Charlie");
		await SignInAsync(probe, a, "6f1c2d3e-0000-4a4b-9c9d-112233445566YY", SharedMac, "1234ABCDZZ", "203.0.113.5", "NL");
		await SignInAsync(probe, b, "6f1c2d3e-0000-4a4b-9c9d-112233445566YY", SharedMac, "1234ABCDZZ", "203.0.113.200", "NL");
		await SignInAsync(probe, b, "6f1c2d3e-0000-4a4b-9c9d-112233445566YY", SharedMac, "1234ABCDZZ", "2001:db8:1:2::7", "NL");
		await SignInAsync(probe, c, "0a0b0c0d-1111-4222-8333-444455556666YY", OtherMac, "99990000ZZ", "198.51.100.9", "DE");
		return new Seeded(a, b, c);
	}

	private static List<long> IdsOf(PageResponse response) => Json(response).GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("id").GetInt64()).Order().ToList();

	private static string Search(string kind, string value) => $"/api/v1/staff/investigate?kind={kind}&value={Uri.EscapeDataString(value)}";

	// Device searches

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AMacSearchFindsExactlyTheAccountsThatUsedIt(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		Seeded seeded = await SeedAsync(probe);

		foreach (string typed in new[] { "a8-bb-cc-00-11-22", "A8:BB:CC:00:11:22", "a8bbcc001122", " A8BB.CC00.1122 ", SharedMac })
		{
			PageResponse response = await admin.Get(Search("mac", typed));
			Assert.Equal(200, response.Status);
			Assert.Equal(new[] { seeded.A, seeded.B }.Order(), IdsOf(response));
			JsonElement body = Json(response);
			Assert.Equal("mac", body.GetProperty("kind").GetString());
			Assert.Equal("A8:BB:CC:00:11:22", body.GetProperty("value").GetString());
			Assert.Equal("Example Networks", body.GetProperty("vendor").GetString());
			JsonElement bravo = body.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("id").GetInt64() == seeded.B);
			Assert.Equal("Bravo", bravo.GetProperty("name").GetString());
			Assert.True(bravo.GetProperty("first_seen_at").GetDateTime() <= bravo.GetProperty("last_seen_at").GetDateTime());
		}

		Assert.Equal(new[] { seeded.C }, IdsOf(await admin.Get(Search("mac", "00-1a-2b-3c-4d-5e"))));
		Assert.Empty(IdsOf(await admin.Get(Search("mac", "A8:BB:CC:00:11:24"))));
		PageResponse invalid = await admin.Get(Search("mac", "A8:BB:CC"));
		Assert.Equal(400, invalid.Status);
		Assert.Equal("invalid_argument", AdminArea.ErrorOf(invalid));
		Assert.Equal(400, (await admin.Get(Search("serial_number", "x"))).Status);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task MachineIdAndVolumeSerialSearchesTakeTheirUsualForms(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		Seeded seeded = await SeedAsync(probe);

		foreach (string typed in new[] { "6f1c2d3e-0000-4a4b-9c9d-112233445566", "{6F1C2D3E-0000-4A4B-9C9D-112233445566}", "6f1c2d3e00004a4b9c9d112233445566" })
		{
			PageResponse response = await admin.Get(Search("machine_guid", typed));
			Assert.Equal(200, response.Status);
			Assert.Equal(new[] { seeded.A, seeded.B }.Order(), IdsOf(response));
			Assert.Equal("6f1c2d3e-0000-4a4b-9c9d-112233445566", Json(response).GetProperty("value").GetString());
		}
		foreach (string typed in new[] { "1234-ABCD", "1234abcd", "1234ABCDZZ" })
		{
			Assert.Equal(new[] { seeded.A, seeded.B }.Order(), IdsOf(await admin.Get(Search("volume_serial", typed))));
		}
		Assert.Equal(new[] { seeded.C }, IdsOf(await admin.Get(Search("volume_serial", "9999-0000"))));
		Assert.Equal(400, (await admin.Get(Search("machine_guid", "not-a-guid"))).Status);
		Assert.Equal(400, (await admin.Get(Search("volume_serial", "123456789"))).Status);
	}

	// The device key of the inspection pivots to the other accounts of the device without showing its value
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheInspectionDeviceKeyFindsTheOtherAccounts(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		Seeded seeded = await SeedAsync(probe);

		PageResponse inspection = await admin.Get($"/api/v1/players/{seeded.A}/inspection");
		Assert.Equal(200, inspection.Status);
		JsonElement mac = Json(inspection).GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("kind").GetString() == "mac");
		string key = mac.GetProperty("key").GetString()!;
		Assert.Equal(64, key.Length);
		Assert.StartsWith(mac.GetProperty("fingerprint").GetString()!, key);
		Assert.DoesNotContain("A8:BB:CC", inspection.Body);

		PageResponse found = await admin.Get($"/api/v1/staff/investigate?kind=mac&key={key}");
		Assert.Equal(200, found.Status);
		Assert.Equal(new[] { seeded.A, seeded.B }.Order(), IdsOf(found));
		Assert.Empty(IdsOf(await admin.Get($"/api/v1/staff/investigate?kind=volume_serial&key={key}")));
		Assert.Equal(400, (await admin.Get($"/api/v1/staff/investigate?kind=ip&key={key}")).Status);
		Assert.Equal(400, (await admin.Get($"/api/v1/staff/investigate?kind=mac&key=abc")).Status);
		Assert.Equal(400, (await admin.Get($"/api/v1/staff/investigate?kind=mac&key={key}&value=a8bbcc001122")).Status);
	}

	// Address searches

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnAddressSearchMatchesEveryWayOfWritingIt(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var directory = new StubDirectory();
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin, directory);
		Seeded seeded = await SeedAsync(probe);

		PageResponse v4 = await admin.Get(Search("ip", "203.0.113.5"));
		Assert.Equal(200, v4.Status);
		Assert.Equal(new[] { seeded.A }, IdsOf(v4));
		JsonElement network = Json(v4).GetProperty("network");
		Assert.Equal("Amsterdam", network.GetProperty("city").GetString());
		Assert.Equal(64500, network.GetProperty("asn").GetInt64());
		Assert.Equal("Example Transit", network.GetProperty("network_owner").GetString());
		Assert.Equal(1, Json(v4).GetProperty("accounts")[0].GetProperty("events").GetInt32());
		Assert.Equal(new[] { seeded.A }, IdsOf(await admin.Get(Search("ip", "::ffff:203.0.113.5"))));

		foreach (string typed in new[] { "2001:db8:1:2::7", "2001:0DB8:0001:0002:0000:0000:0000:0007", "2001:db8:1:2:0:0:0:7" })
		{
			PageResponse v6 = await admin.Get(Search("ip", typed));
			Assert.Equal(new[] { seeded.B }, IdsOf(v6));
			Assert.Equal("2001:db8:1:2::7", Json(v6).GetProperty("value").GetString());
		}
		Assert.Equal(400, (await admin.Get(Search("ip", "203.0.113"))).Status);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ARangeSearchStaysInsideItsBounds(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		Seeded seeded = await SeedAsync(probe);
		long d = await AccountAsync(probe, "Delta");
		await SignInAsync(probe, d, "11111111-2222-4333-8444-555555555555YY", "00:1A:2B:00:00:01ZZ", "00000001ZZ", "203.0.114.1", "NL");

		PageResponse slash24 = await admin.Get(Search("ip_range", "203.0.113.77/24"));
		Assert.Equal(200, slash24.Status);
		Assert.Equal(new[] { seeded.A, seeded.B }.Order(), IdsOf(slash24));
		Assert.Equal("203.0.113.0/24", Json(slash24).GetProperty("value").GetString());
		Assert.Equal(new[] { seeded.B }, IdsOf(await admin.Get(Search("ip_range", "203.0.113.128/25"))));
		Assert.Equal(new[] { seeded.A, seeded.B, d }.Order(), IdsOf(await admin.Get(Search("ip_range", "203.0.112.0/22"))));
		Assert.Equal(new[] { seeded.A, seeded.B, d }.Order(), IdsOf(await admin.Get(Search("ip_range", "203.0.0.0/16"))));
		Assert.Equal(new[] { seeded.B }, IdsOf(await admin.Get(Search("ip_range", "2001:db8:1::/48"))));
		Assert.Equal(new[] { seeded.B }, IdsOf(await admin.Get(Search("ip_range", "2001:db8:1:2::/64"))));
		Assert.Empty(IdsOf(await admin.Get(Search("ip_range", "2001:db8:2::/48"))));

		foreach ((string range, int minimum) in new[] { ("203.0.0.0/15", 16), ("0.0.0.0/0", 16), ("2001:db8::/47", 48) })
		{
			PageResponse wide = await admin.Get(Search("ip_range", range));
			Assert.Equal(400, wide.Status);
			Assert.Equal("invalid_argument", AdminArea.ErrorOf(wide));
			JsonElement parameters = Json(wide).GetProperty("params");
			Assert.Equal("value", parameters.GetProperty("field").GetString());
			Assert.Equal(minimum, parameters.GetProperty("minimum_prefix").GetInt32());
		}
		Assert.Equal(400, (await admin.Get(Search("ip_range", "203.0.113.0/33"))).Status);
		Assert.Equal(400, (await admin.Get(Search("ip_range", "203.0.113.0"))).Status);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ACountrySearchIsPagedAndCapped(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		Seeded seeded = await SeedAsync(probe);

		Assert.Equal(new[] { seeded.A, seeded.B }.Order(), IdsOf(await admin.Get(Search("country", "nl"))));
		Assert.Equal(new[] { seeded.C }, IdsOf(await admin.Get(Search("country", "DE"))));

		PageResponse first = await admin.Get(Search("country", "NL") + "&per_page=1");
		Assert.Single(IdsOf(first));
		Assert.Equal("2", first.Headers!["X-Next-Page"].ToString());
		PageResponse second = await admin.Get(Search("country", "NL") + "&per_page=1&page=2");
		Assert.Single(IdsOf(second));
		Assert.Equal("", second.Headers!["X-Next-Page"].ToString());
		Assert.NotEqual(IdsOf(first), IdsOf(second));

		Assert.Equal(200, (await admin.Get(Search("country", "NL") + "&per_page=100&page=10")).Status);
		PageResponse capped = await admin.Get(Search("country", "NL") + "&per_page=100&page=11");
		Assert.Equal(400, capped.Status);
		JsonElement parameters = Json(capped).GetProperty("params");
		Assert.Equal("page", parameters.GetProperty("field").GetString());
		Assert.Equal(IdentifierSearch.MaximumCountryAccounts, parameters.GetProperty("maximum_accounts").GetInt32());
		Assert.Equal(400, (await admin.Get(Search("country", "NLD"))).Status);
	}

	// Access and the audit log

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheSearchNeedsPlayersInspect(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
		await SeedAsync(probe);

		PageResponse refused = await moderator.Get(Search("mac", SharedMac));
		Assert.Equal(403, refused.Status);
		Assert.Equal("forbidden", AdminArea.ErrorOf(refused));
		Assert.Contains("players.inspect", refused.Body);
		Assert.Equal(401, (await new AdminBrowser(probe).Get(Search("mac", SharedMac))).Status);
		await using AppDbContext db = probe.Open();
		Assert.False(await db.AuditLog.AnyAsync(e => e.Action == ModerationActions.InvestigateSearch));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task EverySearchIsAuditedWithWhatWasSearched(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		await SeedAsync(probe);

		Assert.Equal(200, (await admin.Get(Search("mac", "a8-bb-cc-00-11-22"))).Status);
		Assert.Equal(200, (await admin.Get(Search("mac", "a8-bb-cc-00-11-22"))).Status);
		Assert.Equal(200, (await admin.Get(Search("ip", "198.51.100.9"))).Status);
		Assert.Equal(400, (await admin.Get(Search("ip", "nope"))).Status);

		await using AppDbContext db = probe.Open();
		List<AuditLogEntry> rows = await db.AuditLog.AsNoTracking().Where(e => e.Action == ModerationActions.InvestigateSearch).OrderBy(e => e.ID).ToListAsync();
		Assert.Equal(3, rows.Count);
		Assert.All(rows, row =>
		{
			Assert.Equal(AuditActorKinds.User, row.ActorKind);
			Assert.Equal(chief.ID, row.ActorID);
			Assert.Equal(AuditTargetKinds.Investigation, row.TargetKind);
			Assert.Null(row.TargetID);
			Assert.Equal(ModerationSources.Api, row.Source);
		});
		JsonElement mac = JsonDocument.Parse(rows[0].Details!).RootElement;
		Assert.Equal("mac", mac.GetProperty("kind").GetString());
		Assert.Equal("A8:BB:CC:00:11:22", mac.GetProperty("value").GetString());
		Assert.Equal(2, mac.GetProperty("results").GetInt32());
		JsonElement ip = JsonDocument.Parse(rows[2].Details!).RootElement;
		Assert.Equal(("ip", "198.51.100.9", 1), (ip.GetProperty("kind").GetString(), ip.GetProperty("value").GetString(), ip.GetProperty("results").GetInt32()));
	}
}
