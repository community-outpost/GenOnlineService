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

using GenOnlineService.Realtime;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace GenOnlineService.Investigation
{
	// The identifiers staff search accounts by; the device kinds are the kinds of the devices table
	public static class IdentifierKinds
	{
		public const string Mac = DeviceKinds.Mac;
		public const string MachineGuid = DeviceKinds.MachineGuid;
		public const string VolumeSerial = DeviceKinds.VolumeSerial;
		public const string Ip = "ip";
		public const string IpRange = "ip_range";
		public const string Country = "country";

		public static readonly IReadOnlyList<string> All = [Mac, MachineGuid, VolumeSerial, Ip, IpRange, Country];

		public static bool IsDevice(string kind) => kind is Mac or MachineGuid or VolumeSerial;
	}

	// One search: the normalized value as staff see it, and what the database is asked for
	public sealed record IdentifierQuery
	{
		public string Kind { get; init; } = "";
		public string Value { get; init; } = "";
		// device kinds: the keyed hashes of every form the value may have been stored in
		public IReadOnlyList<byte[]> Hashes { get; init; } = [];
		// ip: the stored form of the address
		public string? Address { get; init; }
		// ip_range: the network and the text every stored address inside it starts with
		public IPNetwork? Range { get; init; }
		public string? StoredPrefix { get; init; }
		// country: the ISO code
		public string? Country { get; init; }
	}

	public static class IdentifierSearch
	{
		// The widest ranges a search takes: 65,536 IPv4 addresses, an IPv6 site
		public const int MinimumIPv4Prefix = 16;
		public const int MinimumIPv6Prefix = 48;
		// A country is paged up to this many accounts
		public const int MaximumCountryAccounts = 1000;
		public const int MaximumValueLength = 128;
		// A device key is the whole keyed hash in hex, as the inspection lists it (StaffInspection.KeyOf)
		public const int KeyLength = Device.ValueHmacLength * 2;

		private const string GuidMarker = "YY";

		// ?kind=&value= or, for the device kinds, ?kind=&key= with a device key of the inspection
		public static (IdentifierQuery? Query, Failure? Failure) Read(string? kind, string? value, string? key)
		{
			if (kind == null || !IdentifierKinds.All.Contains(kind))
			{
				return (null, Result.Invalid("kind"));
			}

			if (key != null)
			{
				if (value != null || !IdentifierKinds.IsDevice(kind))
				{
					return (null, Result.Invalid("key"));
				}
				byte[]? hash = HashOfKey(key);
				return hash == null ? (null, Result.Invalid("key")) : (new IdentifierQuery { Kind = kind, Value = key.ToLowerInvariant(), Hashes = [hash] }, null);
			}

			string text = value?.Trim() ?? "";
			if (text.Length == 0 || text.Length > MaximumValueLength)
			{
				return (null, Result.Invalid("value"));
			}

			return kind switch
			{
				IdentifierKinds.Mac => Mac(text),
				IdentifierKinds.MachineGuid => MachineGuid(text),
				IdentifierKinds.VolumeSerial => VolumeSerial(text),
				IdentifierKinds.Ip => Address(text),
				IdentifierKinds.IpRange => Range(text),
				_ => Country(text)
			};
		}

		private static byte[]? HashOfKey(string key)
		{
			if (key.Length != KeyLength || !key.All(Uri.IsHexDigit))
			{
				return null;
			}
			return Convert.FromHexString(key);
		}

		// The client sends the value as typed plus its marker; a value without the marker or as typed is matched too
		private static IReadOnlyList<byte[]> HashesOf(string typed, params string[] forms)
		{
			return forms.Append(typed).Distinct(StringComparer.Ordinal).Select(DeviceHasher.HashRaw).ToList();
		}

		private static (IdentifierQuery?, Failure?) Mac(string text)
		{
			string? digits = MacAddresses.DigitsOf(text);
			if (digits == null)
			{
				return (null, Result.Invalid("value"));
			}
			string formatted = MacAddresses.Format(digits);
			return (new IdentifierQuery { Kind = IdentifierKinds.Mac, Value = formatted, Hashes = HashesOf(text, formatted + MacAddresses.Marker, formatted) }, null);
		}

		// The registry form is lower case with dashes, the client adds YY
		private static (IdentifierQuery?, Failure?) MachineGuid(string text)
		{
			string bare = text.EndsWith(GuidMarker, StringComparison.OrdinalIgnoreCase) ? text.Substring(0, text.Length - GuidMarker.Length) : text;
			if (!Guid.TryParse(bare, out Guid guid))
			{
				return (null, Result.Invalid("value"));
			}
			string lower = guid.ToString("D");
			string upper = lower.ToUpperInvariant();
			return (new IdentifierQuery { Kind = IdentifierKinds.MachineGuid, Value = lower, Hashes = HashesOf(text, lower + GuidMarker, upper + GuidMarker, lower, upper) }, null);
		}

		// Eight upper-case hex digits as the client writes them, also typed as 1234-ABCD like the vol command shows it
		private static (IdentifierQuery?, Failure?) VolumeSerial(string text)
		{
			string value = text.ToUpperInvariant();
			if (value.EndsWith(MacAddresses.Marker, StringComparison.Ordinal))
			{
				value = value.Substring(0, value.Length - MacAddresses.Marker.Length);
			}
			value = value.Replace("-", "", StringComparison.Ordinal);
			if (value.Length == 0 || value.Length > 8 || !value.All(Uri.IsHexDigit))
			{
				return (null, Result.Invalid("value"));
			}
			string serial = value.PadLeft(8, '0');
			return (new IdentifierQuery { Kind = IdentifierKinds.VolumeSerial, Value = serial, Hashes = HashesOf(text, serial + MacAddresses.Marker, serial) }, null);
		}

		// The form session events store: IPHelpers.NormalizeIP of the connection's address
		private static (IdentifierQuery?, Failure?) Address(string text)
		{
			if (!TryParseAddress(text, out _))
			{
				return (null, Result.Invalid("value"));
			}
			string stored = IPHelpers.NormalizeIP(text);
			return (new IdentifierQuery { Kind = IdentifierKinds.Ip, Value = stored, Address = stored }, null);
		}

		private static (IdentifierQuery?, Failure?) Range(string text)
		{
			int slash = text.IndexOf('/');
			if (slash < 1 || !TryParseAddress(text.Substring(0, slash), out IPAddress? address)
				|| !Int32.TryParse(text.Substring(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int prefix))
			{
				return (null, Result.Invalid("value"));
			}
			if (address.IsIPv4MappedToIPv6)
			{
				address = address.MapToIPv4();
				prefix -= 96;
			}

			bool v4 = address.AddressFamily == AddressFamily.InterNetwork;
			int bits = v4 ? 32 : 128;
			int minimum = v4 ? MinimumIPv4Prefix : MinimumIPv6Prefix;
			if (prefix > bits)
			{
				return (null, Result.Invalid("value"));
			}
			if (prefix < minimum)
			{
				return (null, Result.Fail(ErrorCodes.InvalidArgument, new { Field = "value", MinimumPrefix = minimum }));
			}

			var network = new IPNetwork(Masked(address, prefix), prefix);
			string? stored = StoredPrefixOf(network);
			if (stored == null)
			{
				return (null, Result.Invalid("value"));
			}
			string shown = network.BaseAddress + "/" + prefix.ToString(CultureInfo.InvariantCulture);
			return (new IdentifierQuery { Kind = IdentifierKinds.IpRange, Value = shown, Range = network, StoredPrefix = stored }, null);
		}

		// IPv6, or IPv4 written with its four parts: IPAddress also reads 203.0.113 as 203.0.0.113
		private static bool TryParseAddress(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IPAddress? address)
		{
			address = null;
			return (text.Contains(':') || text.Split('.').Length == 4) && IPAddress.TryParse(text, out address);
		}

		private static IPAddress Masked(IPAddress address, int prefix)
		{
			byte[] bytes = address.GetAddressBytes();
			for (int i = 0; i < bytes.Length; i++)
			{
				int keep = Math.Clamp(prefix - i * 8, 0, 8);
				bytes[i] &= (byte)(0xFF << (8 - keep));
			}
			return new IPAddress(bytes);
		}

		// Every stored address of the range starts with this text: the whole octets of IPv4, the leading non-zero groups of IPv6
		// (a zero group may be written as :: and is left to the exact check)
		private static string? StoredPrefixOf(IPNetwork network)
		{
			byte[] bytes = network.BaseAddress.GetAddressBytes();
			if (network.BaseAddress.AddressFamily == AddressFamily.InterNetwork)
			{
				return String.Concat(bytes.Take(Math.Min(network.PrefixLength / 8, 3)).Select(b => b.ToString(CultureInfo.InvariantCulture) + "."));
			}

			var prefix = new System.Text.StringBuilder();
			for (int group = 0; group < Math.Min(network.PrefixLength / 16, 7); group++)
			{
				int value = (bytes[group * 2] << 8) | bytes[group * 2 + 1];
				if (value == 0)
				{
					break;
				}
				prefix.Append(value.ToString("x", CultureInfo.InvariantCulture)).Append(':');
			}
			return prefix.Length == 0 ? null : prefix.ToString();
		}

		private static (IdentifierQuery?, Failure?) Country(string text)
		{
			if (text.Length != 2 || !text.All(Char.IsAsciiLetter))
			{
				return (null, Result.Invalid("value"));
			}
			string code = text.ToUpperInvariant();
			return (new IdentifierQuery { Kind = IdentifierKinds.Country, Value = code, Country = code }, null);
		}
	}
}
