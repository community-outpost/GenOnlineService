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

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GenOnlineService.Names
{
	// The tag sequence of a player and a name: HMAC-SHA256 with the Names:tag_key secret over the user id, the look-alike key
	// and a block number, cut into 32-bit values and mapped into MinTag..MaxTag by rejection sampling, so every tag is equally
	// likely. The same player and name always give the same sequence, look-alikes share it, and other players get their own.
	public static class NameTags
	{
		public const string ConfigSection = "Names";
		public const string ConfigKey = "tag_key";
		public const int MinimumKeyLength = 32;

		// Only Development runs without a configured key; this fixed key makes its tags predictable
		private static readonly byte[] s_developmentKey = SHA256.HashData(Encoding.UTF8.GetBytes("genonline-development-name-tags"));

		private static readonly ILogger s_log = AppLog.For(typeof(NameTags));
		private static readonly object s_lock = new();
		private static byte[]? s_key;

		// The configured key; outside Development a missing or short key stops the caller with a message naming the setting
		public static byte[] Read(IConfiguration config, bool isDevelopment)
		{
			string? value = config.GetSection(ConfigSection).GetValue<string>(ConfigKey);
			if (String.IsNullOrWhiteSpace(value))
			{
				if (isDevelopment)
				{
					return s_developmentKey;
				}
				throw new InvalidOperationException($"{ConfigSection}:{ConfigKey} is not set. Set it to a base64 secret of at least {MinimumKeyLength} bytes: it derives every display name tag.");
			}

			byte[] key;
			try
			{
				key = Convert.FromBase64String(value.Trim());
			}
			catch (FormatException)
			{
				throw new InvalidOperationException($"{ConfigSection}:{ConfigKey} is not valid base64.");
			}
			if (key.Length < MinimumKeyLength)
			{
				throw new InvalidOperationException($"{ConfigSection}:{ConfigKey} must decode to at least {MinimumKeyLength} bytes.");
			}
			return key;
		}

		// Startup: reads the key once, so a missing one stops the service before it serves anything
		public static void Configure(IConfiguration config, IHostEnvironment environment)
		{
			byte[] key = Read(config, environment.IsDevelopment());
			if (ReferenceEquals(key, s_developmentKey))
			{
				s_log.LogWarning("{Section}:{Key} is not set; Development uses a fixed key, so its name tags are predictable", ConfigSection, ConfigKey);
			}
			UseKey(key);
		}

		// The tests and Configure set the key directly
		public static void UseKey(byte[] key)
		{
			lock (s_lock)
			{
				s_key = key.ToArray();
			}
		}

		// The command line tools never run the web host's startup, so they read the configuration on first use
		private static byte[] Key
		{
			get
			{
				lock (s_lock)
				{
					if (s_key == null)
					{
						WebApplicationBuilder builder = WebApplication.CreateBuilder(Array.Empty<string>());
						s_key = Read(builder.Configuration, builder.Environment.IsDevelopment());
					}
					return s_key;
				}
			}
		}

		// The tags to try for a name, in order and without repeats; endless, so the caller stops when it finds a free one
		public static IEnumerable<int> Candidates(Int64 userID, string nameKey)
		{
			const ulong range = Database.DisplayNames.MaxTag - Database.DisplayNames.MinTag + 1;
			// the largest multiple of range below 2^32: values from it upward would favour the low tags
			const ulong limit = (1UL << 32) / range * range;

			byte[] key = Key;
			HashSet<int> seen = new();
			for (Int64 block = 0; ; block++)
			{
				byte[] mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(String.Create(CultureInfo.InvariantCulture, $"{userID}\n{nameKey}\n{block}")));
				for (int offset = 0; offset < mac.Length; offset += sizeof(uint))
				{
					ulong value = BinaryPrimitives.ReadUInt32BigEndian(mac.AsSpan(offset));
					if (value >= limit)
					{
						continue;
					}

					int tag = Database.DisplayNames.MinTag + (int)(value % range);
					if (seen.Add(tag))
					{
						yield return tag;
					}
				}
			}
		}
	}
}
