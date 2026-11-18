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

// The seeding of the staff enrichment and investigation tests: game sign-ins with their device values and addresses, and
// staff browsers with a stub address directory and a small OUI registry
internal static class InvestigationProbe
{
	public const string SharedMac = "A8:BB:CC:00:11:22ZZ";
	public const string OtherMac = "00:1A:2B:3C:4D:5EZZ";
	public const string VirtualMac = "02:00:4C:4F:4F:50ZZ";

	public static readonly OuiRegistry Vendors = OuiRegistry.Parse(["# test registry", "A8BBCC\tExample Networks", "001A2B\tExample Large", "001A2B3\tExample Medium"]);

	public sealed class StubDirectory : IAddressDirectory
	{
		public List<string> Asked { get; } = new();

		public AddressFacts? Lookup(string ipAddress)
		{
			lock (Asked)
			{
				Asked.Add(ipAddress);
			}
			return ipAddress.StartsWith("203.0.113.", StringComparison.Ordinal) ? new AddressFacts("NL", "Amsterdam", 64500, "Example Transit") : null;
		}
	}

	public static async Task<(AdminBrowser Browser, User User)> StaffAsync(ProbeDatabase probe, string name, string role, StubDirectory? directory = null)
	{
		var browser = new AdminBrowser(probe, services =>
		{
			services.AddSingleton<IAddressDirectory>(directory ?? new StubDirectory());
			services.AddSingleton(Vendors);
		});
		string email = name.ToLowerInvariant() + "@example.org";
		await browser.SignUp(name, email);
		return (browser, await AdminArea.Grant(probe, email, role));
	}

	public static async Task<long> AccountAsync(ProbeDatabase probe, string name)
	{
		await using AppDbContext db = probe.Open();
		return (await Database.Users.Create(db, name)).ID;
	}

	// a game sign-in: the three device values as the client sends them, and a session event with its address
	public static async Task SignInAsync(ProbeDatabase probe, long userID, string guid, string mac, string serial, string ip, string? country)
	{
		DeviceHasher.Initialize(Path.Combine(Path.GetTempPath(), "gn_probe_device_hmac.key"));
		await using AppDbContext db = probe.Open();
		await Database.UserDevices.RegisterUserDevice(db, userID, guid, mac, serial, ip);
		await Database.SessionEvents.Record(db, userID, SessionEventKinds.Start, SessionEventResults.Ok, "genonline", ip, country);
	}

	public static JsonElement Json(PageResponse response) => JsonDocument.Parse(response.Body).RootElement;
}

// The GeoIP facts on the addresses and the MAC vendors on the devices of the staff inspection (S6), from the OUI registry
// and an address directory the tests stub; nothing of it reaches a player route
[Collection(AdminAreaCollection.Name)]
public class EnrichmentTests
{
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheInspectionCarriesGeoIpFactsAndMacVendors(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var directory = new StubDirectory();
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin, directory);
		long player = await AccountAsync(probe, "Echo");
		await SignInAsync(probe, player, "6f1c2d3e-0000-4a4b-9c9d-112233445566YY", SharedMac, "1234ABCDZZ", "203.0.113.5", null);
		await SignInAsync(probe, player, "6f1c2d3e-0000-4a4b-9c9d-112233445566YY", VirtualMac, "1234ABCDZZ", "198.51.100.9", "DE");

		PageResponse response = await admin.Get($"/api/v1/players/{player}/inspection");
		Assert.Equal(200, response.Status);
		JsonElement body = Json(response);

		// the answer keeps every field it had
		foreach (string field in new[] { "last_ip", "last_country", "last_seen_at", "identities", "devices", "addresses", "duplicates" })
		{
			Assert.True(body.TryGetProperty(field, out _), field);
		}
		JsonElement[] devices = body.GetProperty("devices").EnumerateArray().ToArray();
		foreach (JsonElement device in devices)
		{
			foreach (string field in new[] { "kind", "fingerprint", "first_seen_at", "last_seen_at", "last_ip", "key" })
			{
				Assert.True(device.TryGetProperty(field, out _), field);
			}
		}
		List<string?> vendors = devices.Where(d => d.GetProperty("kind").GetString() == "mac").Select(d => d.GetProperty("vendor").GetString()).Order().ToList();
		Assert.Equal(new[] { "Example Networks", OuiRegistry.LocallyAdministered }.Order(), vendors);
		Assert.All(devices.Where(d => d.GetProperty("kind").GetString() != "mac"), d => Assert.Equal(JsonValueKind.Null, d.GetProperty("vendor").ValueKind));

		JsonElement known = body.GetProperty("addresses").EnumerateArray().Single(a => a.GetProperty("ip").GetString() == "203.0.113.5");
		Assert.Equal("NL", known.GetProperty("country").GetString());
		Assert.Equal("Amsterdam", known.GetProperty("city").GetString());
		Assert.Equal(64500, known.GetProperty("asn").GetInt64());
		Assert.Equal("Example Transit", known.GetProperty("network_owner").GetString());
		Assert.Equal(1, known.GetProperty("events").GetInt32());
		JsonElement unknown = body.GetProperty("addresses").EnumerateArray().Single(a => a.GetProperty("ip").GetString() == "198.51.100.9");
		Assert.Equal("DE", unknown.GetProperty("country").GetString());
		Assert.Equal(JsonValueKind.Null, unknown.GetProperty("city").ValueKind);
		Assert.Contains("203.0.113.5", directory.Asked);
	}

	[Fact]
	public void TheOuiRegistryTakesTheLongestBlockAndLabelsLocalAddresses()
	{
		Assert.Equal("Example Networks", InvestigationProbe.Vendors.VendorOf("A8:BB:CC:DD:EE:FFZZ"));
		Assert.Equal("Example Networks", InvestigationProbe.Vendors.VendorOf("a8-bb-cc-dd-ee-ff"));
		Assert.Equal("Example Medium", InvestigationProbe.Vendors.VendorOf("00:1A:2B:3C:4D:5EZZ"));
		Assert.Equal("Example Large", InvestigationProbe.Vendors.VendorOf("00:1A:2B:4C:4D:5EZZ"));
		Assert.Null(InvestigationProbe.Vendors.VendorOf("00:00:01:00:00:00ZZ"));
		// the U/L bit of the first octet: 02, 06, 0A and 0E start locally administered addresses
		Assert.Equal(OuiRegistry.LocallyAdministered, InvestigationProbe.Vendors.VendorOf("02:00:4C:4F:4F:50ZZ"));
		Assert.Equal(OuiRegistry.LocallyAdministered, InvestigationProbe.Vendors.VendorOf("AE:BB:CC:DD:EE:FF"));
		Assert.Null(InvestigationProbe.Vendors.VendorOf("6F1C2D3E-0000-4A4B-9C9D-112233445566YY"));
		Assert.Null(InvestigationProbe.Vendors.VendorOf(null));
		Assert.Equal(3, InvestigationProbe.Vendors.Count);
	}

	[Fact]
	public void TheOuiFileLoadsAndAMissingOneLeavesVendorsUnknown()
	{
		string directory = Path.Combine(Path.GetTempPath(), "gn_oui_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try
		{
			string path = Path.Combine(directory, "oui.tsv");
			File.WriteAllLines(path, ["# IEEE registry", "A8BBCC\tExample Networks", "bad line", "XYZ123\tNot hex"]);
			Assert.Equal(1, OuiRegistry.Load(path).Count);
			Assert.Equal("Example Networks", OuiRegistry.Load(path).VendorOf("A8:BB:CC:00:00:00ZZ"));

			OuiRegistry missing = OuiRegistry.Load(Path.Combine(directory, "absent.tsv"));
			Assert.Equal(0, missing.Count);
			Assert.Null(missing.VendorOf("A8:BB:CC:00:00:00ZZ"));
			Assert.Equal(OuiRegistry.LocallyAdministered, missing.VendorOf("02:00:00:00:00:00ZZ"));
		}
		finally
		{
			Directory.Delete(directory, true);
		}
	}

	// The staff directory opens the City database of the sign-in lookup and answers nothing without it, where the
	// sign-in lookup falls back to fixed coordinates for lobby regions
	[Fact]
	public void WithoutTheGeoIpDatabasesTheDirectoryKnowsNothing()
	{
		Assert.Equal("data/GeoLite2-City.mmdb", MaxMindAddressDirectory.CityPath);
		using var directory = new MaxMindAddressDirectory(Path.Combine(Path.GetTempPath(), "absent-city.mmdb"), Path.Combine(Path.GetTempPath(), "absent-asn.mmdb"));
		Assert.False(directory.HasCity);
		Assert.False(directory.HasAsn);
		Assert.Null(directory.Lookup("203.0.113.5"));
		Assert.Null(directory.Lookup("not an address"));
	}

	// Only players.inspect answers carry a vendor, a city or a network owner, no player route returns them
	[Fact]
	public void NoPlayerRouteAnswersWithEnrichment()
	{
		string[] enriched = ["Vendor", "City", "Asn", "NetworkOwner"];
		var offenders = new List<string>();
		foreach (Type controller in typeof(AccountsController).Assembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
		{
			foreach (MethodInfo action in controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
			{
				IEnumerable<Type> answers = action.GetCustomAttributes<ProducesResponseTypeAttribute>().Select(a => a.Type).Where(t => t != null && t != typeof(void));
				bool carries = answers.Any(t => Reaches(t, enriched, new HashSet<Type>()));
				bool inspect = action.GetCustomAttributes<AuthorizeAttribute>().Any(a => a.Policy == PermissionPolicies.Prefix + Permissions.PlayersInspect);
				if (carries && !inspect)
				{
					offenders.Add(controller.Name + "." + action.Name);
				}
			}
		}
		Assert.True(offenders.Count == 0, String.Join(", ", offenders));
		Assert.True(Reaches(typeof(PortalInspection), enriched, new HashSet<Type>()));
		Assert.True(Reaches(typeof(PortalInvestigation), enriched, new HashSet<Type>()));
	}

	private static bool Reaches(Type type, string[] names, HashSet<Type> seen)
	{
		Type element = type.IsArray ? type.GetElementType()! : type.IsGenericType ? type.GetGenericArguments().LastOrDefault() ?? type : type;
		if (element != type)
		{
			return Reaches(element, names, seen);
		}
		if (type.IsPrimitive || type == typeof(string) || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true || !seen.Add(type))
		{
			return false;
		}
		foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
		{
			if (names.Contains(property.Name) || Reaches(property.PropertyType, names, seen))
			{
				return true;
			}
		}
		return false;
	}
}
