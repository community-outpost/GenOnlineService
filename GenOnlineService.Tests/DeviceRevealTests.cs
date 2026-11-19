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

using GenOnlineService.Portal.Client;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

using static GenOnlineService.Tests.InvestigationProbe;

// The raw device values of the staff pages: players.inspect, so admins and owners and never moderators, and a
// device_reveal audit row on every reveal, unlike the hourly player_inspect row
[Collection(AdminAreaCollection.Name)]
public class DeviceRevealTests
{
	private const string MachineId = "6f1c2d3e-0000-4a4b-9c9d-112233445566YY";

	private static async Task<int> RevealsAsync(ProbeDatabase probe)
	{
		await using AppDbContext db = probe.Open();
		return await db.AuditLog.CountAsync(a => a.Action == ModerationActions.DeviceReveal);
	}

	// an absent and a null property both mean no value was shown
	private static bool Hidden(JsonElement element, string name) => !element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null;

	private static JsonElement DeviceOf(PageResponse inspection, string kind) =>
		Json(inspection).GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("kind").GetString() == kind);

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AModeratorGetsNoRawValues(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
		long player = await AccountAsync(probe, "Alpha");
		await SignInAsync(probe, player, MachineId, SharedMac, "1234ABCDZZ", "203.0.113.5", "NL");
		string key = Database.StaffInspection.KeyOf(DeviceHasher.HashRaw(SharedMac));

		PageResponse inspection = await moderator.Get($"/api/v1/players/{player}/inspection?raw=true");
		Assert.Equal(403, inspection.Status);
		Assert.DoesNotContain("A8:BB:CC", inspection.Body);
		PageResponse investigation = await moderator.Get($"/api/v1/staff/investigate?kind=mac&key={key}&raw=true");
		Assert.Equal(403, investigation.Status);
		Assert.DoesNotContain("A8:BB:CC", investigation.Body);
		Assert.Equal(0, await RevealsAsync(probe));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnAdminSeesRawValuesOnlyWhenAskedAndEveryRevealIsAudited(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		long player = await AccountAsync(probe, "Alpha");
		await SignInAsync(probe, player, MachineId, SharedMac, "1234ABCDZZ", "203.0.113.5", "NL");

		// without raw: the fingerprint and whether a value was kept, never the value, and no reveal row
		PageResponse plain = await admin.Get($"/api/v1/players/{player}/inspection");
		Assert.Equal(200, plain.Status);
		JsonElement hidden = DeviceOf(plain, "mac");
		Assert.True(hidden.GetProperty("has_value").GetBoolean());
		Assert.True(Hidden(hidden, "value"));
		Assert.DoesNotContain("A8:BB:CC", plain.Body);
		Assert.Equal(0, await RevealsAsync(probe));

		// with raw: the values as stored, and a row on each call, inside the hour of the inspection row too
		for (int call = 1; call <= 2; ++call)
		{
			PageResponse revealed = await admin.Get($"/api/v1/players/{player}/inspection?raw=true");
			Assert.Equal(200, revealed.Status);
			Assert.Equal(SharedMac, DeviceOf(revealed, "mac").GetProperty("value").GetString());
			Assert.Equal(MachineId.ToUpperInvariant(), DeviceOf(revealed, "machine_guid").GetProperty("value").GetString());
			Assert.Equal(call, await RevealsAsync(probe));
		}

		await using AppDbContext db = probe.Open();
		AuditLogEntry row = await db.AuditLog.Where(a => a.Action == ModerationActions.DeviceReveal).OrderBy(a => a.ID).FirstAsync();
		Assert.Equal((AuditTargetKinds.User, (long?)player, (long?)chief.ID), (row.TargetKind, row.TargetID, row.ActorID));
		Assert.Equal(1, await db.AuditLog.CountAsync(a => a.Action == ModerationActions.PlayerInspect));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnOwnerRevealsTheValueOfADeviceKeyAndTheSearchIsAudited(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser owner, _) = await StaffAsync(probe, "Boss", StaffRoles.Owner);
		long player = await AccountAsync(probe, "Alpha");
		await SignInAsync(probe, player, MachineId, SharedMac, "1234ABCDZZ", "203.0.113.5", "NL");
		string key = DeviceOf(await owner.Get($"/api/v1/players/{player}/inspection"), "mac").GetProperty("key").GetString()!;

		PageResponse plain = await owner.Get($"/api/v1/staff/investigate?kind=mac&key={key}");
		Assert.Equal(200, plain.Status);
		Assert.True(Hidden(Json(plain), "raw_value"));
		Assert.Equal(0, await RevealsAsync(probe));

		PageResponse revealed = await owner.Get($"/api/v1/staff/investigate?kind=mac&key={key}&raw=true");
		Assert.Equal(200, revealed.Status);
		Assert.Equal(SharedMac, Json(revealed).GetProperty("raw_value").GetString());
		Assert.Equal(1, await RevealsAsync(probe));
		await using (AppDbContext db = probe.Open())
		{
			AuditLogEntry row = await db.AuditLog.SingleAsync(a => a.Action == ModerationActions.DeviceReveal);
			Assert.Equal(AuditTargetKinds.Investigation, row.TargetKind);
			Assert.Contains(key, row.Details);
		}

		// raw needs a device key and takes true or false only
		Assert.Equal(400, (await owner.Get($"/api/v1/staff/investigate?kind=mac&value=a8bbcc001122&raw=true")).Status);
		Assert.Equal(400, (await owner.Get($"/api/v1/staff/investigate?kind=mac&key={key}&raw=yes")).Status);
		Assert.Equal(1, await RevealsAsync(probe));
	}
}
