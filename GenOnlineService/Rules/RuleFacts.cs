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

using GenOnlineService.Investigation;
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService.Rules
{
	// What evaluation needs beside the event: the database, the GeoIP lookup and the MAC vendor registry
	public sealed record RuleEnvironment(IDbContextFactory<AppDbContext> Factory, IAddressDirectory Addresses, OuiRegistry Oui);

	// The facts of one player for one evaluation. A field loads on its first read and is kept for the rest of the
	// evaluation, so every rule of the event shares one read and a field no rule reads is never loaded.
	public sealed class RuleFacts
	{
		private readonly AppDbContext m_db;
		private readonly RuleEnvironment m_environment;
		private readonly RuleEvent m_event;
		private readonly DateTime m_now;
		private readonly Dictionary<string, object?[]> m_values = new(StringComparer.Ordinal);

		private Task<AccountFacts?>? m_account;
		private Task<List<Device>>? m_devices;
		private Task<string?>? m_ip;
		private Task<AddressFacts?>? m_address;
		private Task<SanctionFacts>? m_sanctions;

		public RuleFacts(AppDbContext db, RuleEnvironment environment, RuleEvent ev, DateTime now)
		{
			m_db = db;
			m_environment = environment;
			m_event = ev;
			m_now = now;
		}

		public Int64 UserID => m_event.UserID;

		// Every field read so far with its values
		public IReadOnlyDictionary<string, object?[]> Values => m_values;

		public async ValueTask<object?[]> GetAsync(RuleField field)
		{
			if (m_values.TryGetValue(field.Key, out object?[]? known))
			{
				return known;
			}
			object?[] values = (await field.Load(this)).Where(v => v != null).ToArray();
			m_values[field.Key] = values;
			return values;
		}

		public object?[] EventValue(string key) => m_event.Fields.TryGetValue(key, out object? value) && value != null ? [Normalize(value)] : [];

		private static object Normalize(object value) => value switch
		{
			int i => (double)i,
			long l => (double)l,
			ulong u => (double)u,
			float f => (double)f,
			_ => value
		};

		public async Task<object?[]> DeviceKeysAsync(string kind) =>
			(await DevicesAsync()).Where(d => d.Kind == kind).Select(d => (object?)Database.StaffInspection.KeyOf(d.ValueHmac)).Distinct().ToArray();

		public async Task<object?[]> MacVendorsAsync() =>
			(await DevicesAsync()).Where(d => d.Kind == DeviceKinds.Mac && d.ValueRaw != null).Select(d => (object?)m_environment.Oui.VendorOf(d.ValueRaw)).Where(v => v != null).Distinct().ToArray();

		public async Task<object?[]> MacLocallyAdministeredAsync()
		{
			List<string> digits = (await DevicesAsync()).Where(d => d.Kind == DeviceKinds.Mac).Select(d => MacAddresses.DigitsOf(d.ValueRaw)).OfType<string>().ToList();
			return digits.Count == 0 ? [] : [digits.Any(MacAddresses.IsLocallyAdministered)];
		}

		public async Task<object?[]> IpAsync() => await LastIpAsync() is string ip ? [ip] : [];

		public async Task<object?[]> AddressAsync(Func<AddressFacts, object?> pick) => await LookupAsync() is AddressFacts facts ? [pick(facts)] : [];

		public async Task<object?[]> AccountAsync(Func<AccountFacts, object?> pick) => await AccountFactsAsync() is AccountFacts facts ? [pick(facts)] : [];

		public async Task<object?[]> ProvidersAsync()
		{
			AccountFacts? account = await AccountFactsAsync();
			List<string> providers = await m_db.Identities.AsNoTracking().Where(i => i.UserID == UserID).Select(i => i.Provider).Distinct().ToListAsync();
			if (account?.Password == true)
			{
				providers.Add("password");
			}
			return providers.Select(p => (object?)p).ToArray();
		}

		public async Task<object?[]> SanctionsAsync(Func<SanctionFacts, object?> pick) => [pick(await (m_sanctions ??= LoadSanctionsAsync()))];

		public async Task<object?[]> OpenReportsAsync() =>
			[(double)await m_db.PlayerReports.AsNoTracking().CountAsync(r => r.ReportedUserID == UserID && r.Status == PlayerReportStatuses.Open)];

		public async Task<object?[]> EloAsync()
		{
			int? rating = await m_db.PlayerRatings.AsNoTracking()
				.Where(r => r.UserID == UserID && r.Kind == RatingKinds.AllTime && r.MatchesPlayed > 0)
				.Select(r => (int?)r.Rating)
				.FirstOrDefaultAsync();
			return rating == null ? [] : [(double)rating.Value];
		}

		public async Task<object?[]> GamesPlayedAsync() => [(double)await m_db.MatchPlayers.AsNoTracking().CountAsync(p => p.UserId == UserID)];

		// The unreviewed rows of one review list, the way the staff lists count them
		public async Task<object?[]> UnreviewedAsync(string kind)
		{
			int count = kind switch
			{
				AntiCheatReviewKinds.Modules => await m_db.AnticheatModuleReports.AsNoTracking().CountAsync(r => r.UserId == UserID && r.ReviewedAt == null),
				AntiCheatReviewKinds.NewAccounts => await m_db.AnticheatNewAccountGames.AsNoTracking().CountAsync(r => r.UserId == UserID && r.ReviewedAt == null),
				AntiCheatReviewKinds.Probes => await m_db.AnticheatProbeReports.AsNoTracking().CountAsync(r => r.UserId == UserID && r.ReviewedAt == null && r.MatchId != 0),
				AntiCheatReviewKinds.Devices => await m_db.AnticheatProbeReports.AsNoTracking().CountAsync(r => r.UserId == UserID && r.ReviewedAt == null && r.MatchId == 0),
				AntiCheatReviewKinds.QuickMatchRules => await m_db.AnticheatQuickMatchReports.AsNoTracking().CountAsync(r => r.UserId == UserID && r.ReviewedAt == null),
				_ => 0
			};
			return [(double)count];
		}

		private Task<List<Device>> DevicesAsync() => m_devices ??= m_db.Devices.AsNoTracking().Where(d => d.UserID == UserID).ToListAsync();

		private Task<AccountFacts?> AccountFactsAsync() => m_account ??= LoadAccountAsync();

		private async Task<AccountFacts?> LoadAccountAsync()
		{
			var user = await m_db.Users.AsNoTracking()
				.Where(u => u.ID == UserID)
				.Select(u => new { u.CreatedAt, u.Email, u.EmailConfirmedAt, u.TwoFactorEnabled, u.Username, u.NameKey, Password = u.PasswordHash != null })
				.FirstOrDefaultAsync();
			if (user == null)
			{
				return null;
			}
			int at = user.Email?.LastIndexOf('@') ?? -1;
			string? domain = at >= 0 ? user.Email!.Substring(at + 1).ToLowerInvariant() : null;
			return new AccountFacts(Math.Floor((m_now - user.CreatedAt).TotalDays), domain, user.EmailConfirmedAt != null, user.Username, user.NameKey, user.TwoFactorEnabled, user.Password);
		}

		private async Task<SanctionFacts> LoadSanctionsAsync()
		{
			var rows = await m_db.Sanctions.AsNoTracking().Where(s => s.UserID == UserID).Select(s => new { s.Type, s.LiftedAt, s.ExpiresAt }).ToListAsync();
			bool Active(string type) => rows.Any(s => s.Type == type && s.LiftedAt == null && (s.ExpiresAt == null || s.ExpiresAt > m_now));
			return new SanctionFacts(rows.Count, Active(SanctionTypes.Ban), Active(SanctionTypes.Mute));
		}

		private Task<string?> LastIpAsync() => m_ip ??= LoadIpAsync();

		private async Task<string?> LoadIpAsync()
		{
			if (!String.IsNullOrEmpty(m_event.Ip))
			{
				return m_event.Ip;
			}
			string? ip = await m_db.SessionEvents.AsNoTracking()
				.Where(e => e.UserID == UserID && e.Ip != "")
				.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.ID)
				.Select(e => e.Ip)
				.FirstOrDefaultAsync();
			return String.IsNullOrEmpty(ip) ? null : ip;
		}

		private Task<AddressFacts?> LookupAsync() => m_address ??= LoadAddressAsync();

		private async Task<AddressFacts?> LoadAddressAsync()
		{
			string? ip = await LastIpAsync();
			if (ip == null)
			{
				return null;
			}
			Investigation.AddressFacts? found = m_environment.Addresses.Lookup(ip);
			string? country = found?.Country;
			if (country == null)
			{
				// the sign-in lookup stored a country with the session event of this address
				country = await m_db.SessionEvents.AsNoTracking()
					.Where(e => e.UserID == UserID && e.Ip == ip && e.Country != null)
					.OrderByDescending(e => e.CreatedAt)
					.Select(e => e.Country)
					.FirstOrDefaultAsync();
			}
			return new AddressFacts(country?.Trim().ToUpperInvariant(), found?.City, found?.Asn is long asn ? asn : null, found?.NetworkOwner);
		}

		public sealed record AccountFacts(double AgeDays, string? EmailDomain, bool EmailConfirmed, string Name, string NameKey, bool TwoFactor, bool Password);

		public sealed record SanctionFacts(double Count, bool Banned, bool Muted);

		public sealed record AddressFacts(string? Country, string? City, double? Asn, string? Owner);

		// A recorded value as staff read it: a device id shows as the fingerprint of its hash, never a raw value
		public static object? Describe(RuleField field, object? value) => field.Type == RuleFieldTypes.Device && value is string key
			? key.Substring(0, Math.Min(key.Length, Database.StaffInspection.FingerprintBytes * 2))
			: value is double number && double.IsInteger(number) && Math.Abs(number) < 1e15 ? (long)number : value;
	}
}
