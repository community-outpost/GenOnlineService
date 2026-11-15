using Database;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GenOnlineService.Tests;

public class DeviceRawValueTests
{
	[Theory]
	[InlineData("abc-123yy", "ABC-123YY")]
	[InlineData("  00:1a:2b:3c:4d:5ezz ", "00:1A:2B:3C:4D:5EZZ")]
	[InlineData("NONE", null)]
	[InlineData("none", null)]
	[InlineData("", null)]
	[InlineData(null, null)]
	public void RawValuesAreUpperCasedAndNoneIsDropped(string? sent, string? stored)
	{
		Assert.Equal(stored, DeviceHasher.NormalizeRaw(sent));
	}

	[Fact]
	public void RawValuesAreCutToTheColumnLength()
	{
		string? stored = DeviceHasher.NormalizeRaw(new string('a', 200));
		Assert.Equal(new string('A', Device.ValueRawLength), stored);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task SignInStoresTheRawValueAndFillsOlderRows(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		DeviceHasher.Initialize(Path.Combine(Path.GetTempPath(), "gn_probe_device_hmac.key"));

		long userID;
		await using (AppDbContext db = probe.Open())
		{
			userID = (await Users.Create(db, "Dev")).ID;

			// A row from before raw values were kept
			db.Devices.Add(new Device { UserID = userID, Kind = DeviceKinds.Mac, ValueHmac = DeviceHasher.HashRaw("aa:bbzz"), FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow });
			await db.SaveChangesAsync();
		}

		await using (AppDbContext db = probe.Open())
		{
			await UserDevices.RegisterUserDevice(db, userID, "guid-1yy", "aa:bbzz", "NONE", "203.0.113.7");
			await UserDevices.RegisterUserDevice(db, userID, "guid-1yy", "aa:bbzz", "NONE", "203.0.113.8");
		}

		await using (AppDbContext db = probe.Open())
		{
			List<Device> devices = await db.Devices.Where(d => d.UserID == userID).OrderBy(d => d.Kind).ToListAsync();
			Assert.Equal(2, devices.Count);
			Assert.Equal((DeviceKinds.Mac, "AA:BBZZ", "203.0.113.8"), (devices[0].Kind, devices[0].ValueRaw, devices[0].LastIp));
			Assert.Equal((DeviceKinds.MachineGuid, "GUID-1YY", "203.0.113.8"), (devices[1].Kind, devices[1].ValueRaw, devices[1].LastIp));
			Assert.Equal(DeviceHasher.HashRaw("guid-1yy"), devices[1].ValueHmac);
		}
	}
}
