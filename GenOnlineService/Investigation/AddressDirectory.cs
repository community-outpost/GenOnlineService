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

using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Responses;
using System.Collections.Concurrent;
using System.Net;

namespace GenOnlineService.Investigation
{
	// What the GeoIP databases know about an address, each field is null when unknown
	public sealed record AddressFacts(string? Country, string? City, long? Asn, string? NetworkOwner);

	// Looks up addresses for the staff pages only, players never see these facts
	public interface IAddressDirectory
	{
		// Null when no database knows the address
		AddressFacts? Lookup(string ipAddress);
	}

	// MaxMind GeoLite2: the City database the sign-in lookup opens (GeoLocation), and the ASN database when it is deployed
	public sealed class MaxMindAddressDirectory : IAddressDirectory, IDisposable
	{
		// The file GeoLocation reads at sign-in
		public const string CityPath = "data/GeoLite2-City.mmdb";
		// GeoLite2-ASN, from the same geoipupdate run once its edition id is configured
		public const string AsnPath = "data/GeoLite2-ASN.mmdb";

		private const int MaximumCached = 10000;

		private static readonly ILogger s_log = AppLog.For(typeof(MaxMindAddressDirectory));
		private static readonly Lazy<MaxMindAddressDirectory> s_default = new(() => new MaxMindAddressDirectory(CityPath, AsnPath));

		private readonly DatabaseReader? m_city;
		private readonly DatabaseReader? m_asn;
		private readonly ConcurrentDictionary<string, AddressFacts?> m_cache = new(StringComparer.Ordinal);

		public MaxMindAddressDirectory(string cityPath, string asnPath)
		{
			m_city = TryOpen(cityPath);
			m_asn = TryOpen(asnPath);
		}

		// The directory of the service, opened once like the sign-in lookup
		public static MaxMindAddressDirectory Default => s_default.Value;

		public bool HasCity => m_city != null;

		public bool HasAsn => m_asn != null;

		private static DatabaseReader? TryOpen(string path)
		{
			if (!File.Exists(path))
			{
				return null;
			}

			try
			{
				return new DatabaseReader(path);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GeoIP database {Path} could not be opened; staff pages show no facts from it", path);
				return null;
			}
		}

		public AddressFacts? Lookup(string ipAddress)
		{
			if (!IPAddress.TryParse(ipAddress, out IPAddress? address))
			{
				return null;
			}

			if (m_cache.TryGetValue(ipAddress, out AddressFacts? cached))
			{
				return cached;
			}

			string? country = null;
			string? city = null;
			long? asn = null;
			string? owner = null;
			try
			{
				if (m_city != null && m_city.TryCity(address, out CityResponse? place) && place != null)
				{
					country = place.Country.IsoCode;
					city = place.City.Name;
				}
				if (m_asn != null && m_asn.TryAsn(address, out AsnResponse? network) && network != null)
				{
					asn = network.AutonomousSystemNumber;
					owner = network.AutonomousSystemOrganization;
				}
			}
			catch (Exception ex)
			{
				s_log.LogWarning(ex, "GeoIP lookup failed");
			}

			AddressFacts? facts = country == null && city == null && asn == null && owner == null ? null : new AddressFacts(country, city, asn, owner);

			// a full cache starts over rather than growing without bound
			if (m_cache.Count >= MaximumCached)
			{
				m_cache.Clear();
			}
			m_cache[ipAddress] = facts;
			return facts;
		}

		public void Dispose()
		{
			m_city?.Dispose();
			m_asn?.Dispose();
		}
	}
}
