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

using GenOnlineService;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.Security.Cryptography;
using System.Text;

public static class DeviceKinds
{
	public const string MachineGuid = "machine_guid";
	public const string Mac = "mac";
	public const string VolumeSerial = "volume_serial";
}

public static class SessionEventKinds
{
	public const string Start = "start";
	public const string Refresh = "refresh";
	public const string End = "end";
	public const string Denied = "denied";
}

public static class SessionEventResults
{
	public const string Ok = "ok";
}

// A hardware identifier seen on a user's sign-in: the HMAC matches devices, the raw value is for staff review
public class Device
{
	public const int ValueHmacLength = 32;
	public const int ValueRawLength = 64;

	public Int64 ID { get; set; }
	public Int64 UserID { get; set; }
	public string Kind { get; set; } = String.Empty;
	public byte[] ValueHmac { get; set; } = Array.Empty<byte>();
	public string? ValueRaw { get; set; }
	public DateTime FirstSeenAt { get; set; } = DateTime.UnixEpoch;
	public DateTime LastSeenAt { get; set; } = DateTime.UnixEpoch;
	public string? LastIp { get; set; }
}

public class SessionEvent
{
	public Int64 ID { get; set; }
	public Int64 UserID { get; set; }
	public string Kind { get; set; } = String.Empty;
	public string Result { get; set; } = String.Empty;
	public string Client { get; set; } = String.Empty;
	public string ClientVersion { get; set; } = String.Empty;
	public string? ExeCrc { get; set; }
	public string Ip { get; set; } = String.Empty;
	public string? Country { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;
}

public class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
	public void Configure(EntityTypeBuilder<Device> builder)
	{
		builder.ToTable("devices");

		builder.HasKey(e => e.ID);
		builder.HasIndex(e => new { e.UserID, e.Kind, e.ValueHmac }).IsUnique().HasDatabaseName("ux_devices_user_kind_value");
		builder.HasIndex(e => new { e.Kind, e.ValueHmac }).HasDatabaseName("ix_devices_kind_value");
		builder.HasIndex(e => e.LastSeenAt).HasDatabaseName("ix_devices_last_seen_at");

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.Kind).HasColumnName("kind").HasMaxLength(16);
		builder.Property(e => e.ValueHmac).HasColumnName("value_hmac").HasMaxLength(Device.ValueHmacLength).IsFixedLength();
		builder.Property(e => e.ValueRaw).HasColumnName("value_raw").HasMaxLength(Device.ValueRawLength);
		builder.Property(e => e.FirstSeenAt).HasColumnName("first_seen_at");
		builder.Property(e => e.LastSeenAt).HasColumnName("last_seen_at");
		builder.Property(e => e.LastIp).HasColumnName("last_ip").HasMaxLength(45);
	}
}

public class SessionEventConfiguration : IEntityTypeConfiguration<SessionEvent>
{
	public void Configure(EntityTypeBuilder<SessionEvent> builder)
	{
		builder.ToTable("session_events");

		builder.HasKey(e => e.ID);
		builder.HasIndex(e => new { e.UserID, e.CreatedAt }).HasDatabaseName("ix_session_events_user_created");
		builder.HasIndex(e => e.CreatedAt).HasDatabaseName("ix_session_events_created_at");

		// Finds the other accounts seen on an address for the staff duplicate check
		builder.HasIndex(e => new { e.Ip, e.UserID }).HasDatabaseName("ix_session_events_ip_user");

		// Finds the accounts seen in a country for the staff investigation search
		builder.HasIndex(e => new { e.Country, e.UserID, e.CreatedAt }).HasDatabaseName("ix_session_events_country_user");

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.Kind).HasColumnName("kind").HasMaxLength(16);
		builder.Property(e => e.Result).HasColumnName("result").HasMaxLength(32);
		builder.Property(e => e.Client).HasColumnName("client").HasMaxLength(32);
		builder.Property(e => e.ClientVersion).HasColumnName("client_version").HasMaxLength(32);
		builder.Property(e => e.ExeCrc).HasColumnName("exe_crc").HasMaxLength(16);
		builder.Property(e => e.Ip).HasColumnName("ip").HasMaxLength(45);
		builder.Property(e => e.Country).HasColumnName("country").HasMaxLength(2).IsFixedLength();
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
	}
}

// Device values are HMAC-SHA-256 of their upper-case MD5 hex, the form every old row has, so imported and new rows compare equal
public static class DeviceHasher
{
	public const int KeyLength = 32;
	public const string DefaultKeyPath = "data/device_hmac.key";

	private static byte[]? s_key;

	public static bool IsInitialized => s_key != null;

	public static string GetKeyPath(IConfiguration config)
	{
		string? path = config.GetSection("Devices").GetValue<string>("hmac_key_path");
		return String.IsNullOrWhiteSpace(path) ? DefaultKeyPath : path;
	}

	// Loads the key, creating it on first use; returns true when the key was created
	public static bool Initialize(string path)
	{
		bool created = false;

		if (!File.Exists(path))
		{
			string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
			if (!String.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			byte[] fresh = RandomNumberGenerator.GetBytes(KeyLength);

			// CreateNew never overwrites a key another process wrote first
			try
			{
				using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
				byte[] text = Encoding.ASCII.GetBytes(Convert.ToBase64String(fresh));
				stream.Write(text);
				created = true;
			}
			catch (IOException) when (File.Exists(path))
			{
			}
		}

		byte[] key;
		try
		{
			key = Convert.FromBase64String(File.ReadAllText(path).Trim());
		}
		catch (FormatException)
		{
			throw new InvalidOperationException($"The device HMAC key at {path} is not base64.");
		}

		if (key.Length < KeyLength)
		{
			throw new InvalidOperationException($"The device HMAC key at {path} is {key.Length} bytes, at least {KeyLength} are required.");
		}

		s_key = key;
		return created;
	}

	public static byte[] HashRaw(string rawValue)
	{
		return HashLegacyMd5(Helpers.ComputeMD5Hash(rawValue));
	}

	public static byte[] HashLegacyMd5(string md5Hex)
	{
		byte[] key = s_key ?? throw new InvalidOperationException("DeviceHasher is not initialized.");
		return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(md5Hex.ToUpperInvariant()));
	}

	// Clients send NONE when they cannot read a value; it identifies nothing
	public static bool IsMeaningful(string? rawValue)
	{
		return !String.IsNullOrWhiteSpace(rawValue) && !rawValue.Equals("NONE", StringComparison.OrdinalIgnoreCase);
	}

	// The raw value as stored: upper case like old databases, null when it identifies nothing
	public static string? NormalizeRaw(string? rawValue)
	{
		if (!IsMeaningful(rawValue))
		{
			return null;
		}

		string cleaned = TextSanitizer.Clean(rawValue!.Trim().ToUpperInvariant(), Device.ValueRawLength);
		return cleaned.Length == 0 ? null : cleaned;
	}

	public static readonly string NoneMd5 = Helpers.ComputeMD5Hash("NONE").ToUpperInvariant();
}

namespace Database
{
	public static class UserDevices
	{
		private static readonly ILogger s_log = AppLog.For(typeof(UserDevices));

		public static async Task RegisterUserDevice(AppDbContext db, long userId, string hwid_0, string hwid_1, string hwid_2, string ipAddr)
		{
			try
			{
				// Genuine clients end these values with a marker suffix
				if (!hwid_0.ToUpper().EndsWith("YY"))
				{
					await Database.AntiCheat.FlagAccountForReview_SuspectProbes(db, userId, 0, "Logged in with potentially spoofed HWID 0");
				}

				if (!hwid_1.ToUpper().EndsWith("ZZ"))
				{
					await Database.AntiCheat.FlagAccountForReview_SuspectProbes(db, userId, 0, "Logged in with potentially spoofed HWID 1");
				}

				if (!hwid_2.ToUpper().EndsWith("ZZ"))
				{
					await Database.AntiCheat.FlagAccountForReview_SuspectProbes(db, userId, 0, "Logged in with potentially spoofed HWID 2");
				}

				var seen = new List<(string Kind, byte[] Value, string? Raw)>();
				if (DeviceHasher.IsMeaningful(hwid_0)) seen.Add((DeviceKinds.MachineGuid, DeviceHasher.HashRaw(hwid_0), DeviceHasher.NormalizeRaw(hwid_0)));
				if (DeviceHasher.IsMeaningful(hwid_1)) seen.Add((DeviceKinds.Mac, DeviceHasher.HashRaw(hwid_1), DeviceHasher.NormalizeRaw(hwid_1)));
				if (DeviceHasher.IsMeaningful(hwid_2)) seen.Add((DeviceKinds.VolumeSerial, DeviceHasher.HashRaw(hwid_2), DeviceHasher.NormalizeRaw(hwid_2)));

				foreach (var (kind, value, raw) in seen)
				{
					await Touch(db, userId, kind, value, raw, ipAddr);
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "RegisterUserDevice failed");
			}
		}

		private static async Task Touch(AppDbContext db, long userId, string kind, byte[] value, string? raw, string ipAddr)
		{
			DateTime now = DateTime.UtcNow;
			string? ip = String.IsNullOrEmpty(ipAddr) ? null : TextSanitizer.Clean(ipAddr, 45);

			int updated = await db.Devices
				.Where(d => d.UserID == userId && d.Kind == kind && d.ValueHmac == value)
				.ExecuteUpdateAsync(s => s
					.SetProperty(d => d.LastSeenAt, now)
					.SetProperty(d => d.LastIp, ip)
					// Fills rows stored before raw values were kept
					.SetProperty(d => d.ValueRaw, d => d.ValueRaw ?? raw));

			if (updated > 0)
			{
				return;
			}

			db.Devices.Add(new Device { UserID = userId, Kind = kind, ValueHmac = value, ValueRaw = raw, FirstSeenAt = now, LastSeenAt = now, LastIp = ip });

			try
			{
				await db.SaveChangesAsync();
			}
			catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex))
			{
				// A concurrent sign-in inserted the row first
			}
			finally
			{
				db.ChangeTracker.Clear();
			}
		}
	}

	public static class SessionEvents
	{
		public static async Task Record(AppDbContext db, long userId, string kind, string result, string client, string ipAddr, string? country)
		{
			db.SessionEvents.Add(new SessionEvent
			{
				UserID = userId,
				Kind = kind,
				Result = result,
				Client = TextSanitizer.Clean(client, 32),
				ClientVersion = String.Empty,
				Ip = TextSanitizer.Clean(ipAddr, 45),
				Country = country is { Length: 2 } ? country.ToUpperInvariant() : null,
				CreatedAt = DateTime.UtcNow
			});

			try
			{
				await db.SaveChangesAsync();
			}
			finally
			{
				db.ChangeTracker.Clear();
			}
		}
	}

	// A stored device as staff see it: a short fingerprint of the keyed hash, never a raw value
	// Key is the whole keyed hash for the investigation search, ValueRaw stays on the host to look up a MAC vendor
	public sealed record InspectedDevice(string Kind, string Fingerprint, DateTime FirstSeenAt, DateTime LastSeenAt, string? LastIp, string Key, string? ValueRaw);

	public sealed record InspectedAddress(string Ip, string? Country, DateTime FirstSeenAt, DateTime LastSeenAt, int Events);

	public sealed record LastSession(DateTime At, string Ip, string? Country, string Client);

	// Another account seen on the same device or only on the same address, Shared counts the devices or addresses in common
	public sealed record DuplicateAccount(long UserID, string Name, int NameTag, string State, string Match, int Shared, DateTime LastSeenAt);

	public static class DuplicateMatches
	{
		public const string Device = "device";
		public const string Ip = "ip";
	}

	// What staff with players.inspect read about one player, every list is capped in the database
	public static class StaffInspection
	{
		public const int MaximumRows = 100;
		public const int MaximumDuplicates = 50;
		public const int FingerprintBytes = 4;

		public static string FingerprintOf(byte[] valueHmac) => Convert.ToHexString(valueHmac, 0, Math.Min(FingerprintBytes, valueHmac.Length)).ToLowerInvariant();

		// The whole keyed hash in hex, the device key the investigation search takes; never the raw value
		public static string KeyOf(byte[] valueHmac) => Convert.ToHexString(valueHmac).ToLowerInvariant();

		public static async Task<List<InspectedDevice>> GetDevices(AppDbContext db, long userID, CancellationToken cancellationToken = default)
		{
			List<Device> rows = await db.Devices.AsNoTracking()
				.Where(d => d.UserID == userID)
				.OrderByDescending(d => d.LastSeenAt)
				.ThenBy(d => d.ID)
				.Take(MaximumRows)
				.ToListAsync(cancellationToken);
			return rows.Select(d => new InspectedDevice(d.Kind, FingerprintOf(d.ValueHmac), d.FirstSeenAt, d.LastSeenAt, d.LastIp, KeyOf(d.ValueHmac), d.ValueRaw)).ToList();
		}

		// The distinct addresses of the session events, most recent first
		public static async Task<List<InspectedAddress>> GetAddresses(AppDbContext db, long userID, CancellationToken cancellationToken = default)
		{
			var rows = await db.SessionEvents.AsNoTracking()
				.Where(e => e.UserID == userID && e.Ip != "")
				.GroupBy(e => e.Ip)
				.Select(g => new { Ip = g.Key, Country = g.Max(e => e.Country), First = g.Min(e => e.CreatedAt), Last = g.Max(e => e.CreatedAt), Events = g.Count() })
				.OrderByDescending(a => a.Last)
				.Take(MaximumRows)
				.ToListAsync(cancellationToken);
			return rows.Select(a => new InspectedAddress(a.Ip, a.Country, a.First, a.Last, a.Events)).ToList();
		}

		public static async Task<LastSession?> GetLastSession(AppDbContext db, long userID, CancellationToken cancellationToken = default)
		{
			SessionEvent? row = await db.SessionEvents.AsNoTracking()
				.Where(e => e.UserID == userID && e.Result == SessionEventResults.Ok)
				.OrderByDescending(e => e.CreatedAt)
				.ThenByDescending(e => e.ID)
				.FirstOrDefaultAsync(cancellationToken);
			return row == null ? null : new LastSession(row.CreatedAt, row.Ip, row.Country, row.Client);
		}

		// Device matches first, then accounts that only share an address, a hint for staff, never proof
		public static async Task<List<DuplicateAccount>> GetDuplicates(AppDbContext db, long userID, CancellationToken cancellationToken = default)
		{
			var byDevice = await (
				from mine in db.Devices.AsNoTracking()
				join other in db.Devices.AsNoTracking() on new { mine.Kind, mine.ValueHmac } equals new { other.Kind, other.ValueHmac }
				where mine.UserID == userID && other.UserID != userID
				group other by other.UserID into matches
				select new { UserID = matches.Key, Shared = matches.Count(), Last = matches.Max(o => o.LastSeenAt) })
				.OrderByDescending(m => m.Shared)
				.ThenByDescending(m => m.Last)
				.Take(MaximumDuplicates)
				.ToListAsync(cancellationToken);

			IQueryable<string> addresses = db.SessionEvents.AsNoTracking().Where(e => e.UserID == userID && e.Ip != "").Select(e => e.Ip).Distinct();
			var byAddress = await db.SessionEvents.AsNoTracking()
				.Where(e => e.UserID != userID && addresses.Contains(e.Ip))
				.GroupBy(e => e.UserID)
				.Select(g => new { UserID = g.Key, Shared = g.Select(e => e.Ip).Distinct().Count(), Last = g.Max(e => e.CreatedAt) })
				.OrderByDescending(m => m.Last)
				.Take(MaximumDuplicates)
				.ToListAsync(cancellationToken);

			HashSet<long> strong = byDevice.Select(m => m.UserID).ToHashSet();
			var matched = byDevice.Select(m => (m.UserID, Match: DuplicateMatches.Device, m.Shared, m.Last))
				.Concat(byAddress.Where(m => !strong.Contains(m.UserID)).Select(m => (m.UserID, Match: DuplicateMatches.Ip, m.Shared, m.Last)))
				.Take(MaximumDuplicates)
				.ToList();

			List<long> ids = matched.Select(m => m.UserID).ToList();
			var users = await db.Users.AsNoTracking()
				.Where(u => ids.Contains(u.ID))
				.Select(u => new { u.ID, u.Username, u.NameTag, u.State })
				.ToDictionaryAsync(u => u.ID, cancellationToken);

			// a row of a removed account names nobody
			return matched.Where(m => users.ContainsKey(m.UserID))
				.Select(m => new DuplicateAccount(m.UserID, users[m.UserID].Username, users[m.UserID].NameTag, users[m.UserID].State, m.Match, m.Shared, m.Last))
				.ToList();
		}
	}

	// Devices are kept 180 days after they were last seen, session events 90 days
	public static class Retention
	{
		public static readonly TimeSpan DeviceRetention = TimeSpan.FromDays(180);
		public static readonly TimeSpan SessionEventRetention = TimeSpan.FromDays(90);

		public static async Task<(int Devices, int SessionEvents)> Purge(AppDbContext db, DateTime utcNow, CancellationToken cancellationToken = default)
		{
			DateTime deviceCutoff = utcNow - DeviceRetention;
			DateTime eventCutoff = utcNow - SessionEventRetention;

			int devices = await db.Devices.Where(d => d.LastSeenAt < deviceCutoff).ExecuteDeleteAsync(cancellationToken);
			int events = await db.SessionEvents.Where(e => e.CreatedAt < eventCutoff).ExecuteDeleteAsync(cancellationToken);

			return (devices, events);
		}
	}
}
