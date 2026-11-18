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

namespace GenOnlineService.Rules
{
	// The value types of the fields. Every field reads as a list of values, and an unknown fact is an empty list.
	public static class RuleFieldTypes
	{
		public const string Text = "text";
		public const string Number = "number";
		public const string Bool = "bool";
		public const string Ip = "ip";

		// A device id: staff enter the raw value, it is compared through the device hash
		public const string Device = "device";

		public static IReadOnlyList<string> OperatorsOf(string type) => type switch
		{
			Text => [RuleOperators.Equals, RuleOperators.NotEquals, RuleOperators.InList, RuleOperators.Contains, RuleOperators.StartsWith, RuleOperators.IsEmpty],
			Number => [RuleOperators.Equals, RuleOperators.NotEquals, RuleOperators.InList, RuleOperators.GreaterThan, RuleOperators.LessThan, RuleOperators.Between, RuleOperators.IsEmpty],
			Bool => [RuleOperators.Equals, RuleOperators.IsEmpty],
			Ip => [RuleOperators.Equals, RuleOperators.NotEquals, RuleOperators.InList, RuleOperators.StartsWith, RuleOperators.Cidr, RuleOperators.IsEmpty],
			Device => [RuleOperators.Equals, RuleOperators.NotEquals, RuleOperators.InList, RuleOperators.IsEmpty],
			_ => []
		};
	}

	// One field of the catalogue: its type, the triggers that provide it and how its values load
	public sealed record RuleField(string Key, string Type, string Label, string Description, IReadOnlyList<string> Triggers, Func<RuleFacts, Task<object?[]>> Load)
	{
		public IReadOnlyList<string> Operators => RuleFieldTypes.OperatorsOf(Type);

		public bool IsProvidedBy(string trigger) => Triggers.Contains(trigger, StringComparer.Ordinal);
	}

	// The fixed catalogue of player data a rule can read. Player fields come with every trigger, event fields only with
	// the triggers whose event carries them.
	public static class RuleFields
	{
		public static readonly IReadOnlyList<string> AllTriggers = RuleTriggers.All;

		public static readonly IReadOnlyList<RuleField> All = Build();

		private static readonly Dictionary<string, RuleField> s_byKey = All.ToDictionary(f => f.Key, StringComparer.Ordinal);

		public static RuleField? Find(string? key) => key != null && s_byKey.TryGetValue(key, out RuleField? field) ? field : null;

		private static RuleField Player(string key, string type, string label, string description, Func<RuleFacts, Task<object?[]>> load) =>
			new RuleField(key, type, label, description, AllTriggers, load);

		private static RuleField Event(string key, string type, string label, string description, IReadOnlyList<string> triggers) =>
			new RuleField(key, type, label, description, triggers, facts => Task.FromResult(facts.EventValue(key)));

		private static List<RuleField> Build()
		{
			var fields = new List<RuleField>
			{
				Player("device.machine_guid", RuleFieldTypes.Device, "Machine GUID", "Any machine GUID the player signed in with.", f => f.DeviceKeysAsync(DeviceKinds.MachineGuid)),
				Player("device.mac", RuleFieldTypes.Device, "MAC address", "Any MAC address the player signed in with.", f => f.DeviceKeysAsync(DeviceKinds.Mac)),
				Player("device.volume_serial", RuleFieldTypes.Device, "Volume serial", "Any volume serial the player signed in with.", f => f.DeviceKeysAsync(DeviceKinds.VolumeSerial)),
				Player("mac.vendor", RuleFieldTypes.Text, "MAC vendor", "The vendor of any MAC address of the player, from the IEEE registry.", f => f.MacVendorsAsync()),
				Player("mac.locally_administered", RuleFieldTypes.Bool, "Virtual MAC", "True when any MAC address of the player is locally administered, as virtual adapters are.", f => f.MacLocallyAdministeredAsync()),
				Player("network.ip", RuleFieldTypes.Ip, "IP address", "The address of the event, else of the last session.", f => f.IpAsync()),
				Player("network.country", RuleFieldTypes.Text, "Country", "The ISO country code of the address.", f => f.AddressAsync(a => a.Country)),
				Player("network.city", RuleFieldTypes.Text, "City", "The city of the address.", f => f.AddressAsync(a => a.City)),
				Player("network.asn", RuleFieldTypes.Number, "ASN", "The autonomous system number of the address.", f => f.AddressAsync(a => a.Asn)),
				Player("network.owner", RuleFieldTypes.Text, "Network owner", "The organization of the autonomous system of the address.", f => f.AddressAsync(a => a.Owner)),
				Player("account.age_days", RuleFieldTypes.Number, "Account age in days", "Whole days since the account was created.", f => f.AccountAsync(a => a.AgeDays)),
				Player("account.email_domain", RuleFieldTypes.Text, "Email domain", "The domain of the email address.", f => f.AccountAsync(a => a.EmailDomain)),
				Player("account.email_confirmed", RuleFieldTypes.Bool, "Email confirmed", "True when the email address is confirmed.", f => f.AccountAsync(a => a.EmailConfirmed)),
				Player("account.providers", RuleFieldTypes.Text, "Sign-in providers", "Every way the player signs in: password or a provider such as discord.", f => f.ProvidersAsync()),
				Player("account.name", RuleFieldTypes.Text, "Player name", "The display name without its tag.", f => f.AccountAsync(a => a.Name)),
				Player("account.name_key", RuleFieldTypes.Text, "Name key", "The look-alike key of the name, the same for names that read alike.", f => f.AccountAsync(a => a.NameKey)),
				Player("account.two_factor", RuleFieldTypes.Bool, "Two-factor on", "True when two-factor sign-in is on.", f => f.AccountAsync(a => a.TwoFactor)),
				Player("sanctions.count", RuleFieldTypes.Number, "Prior sanctions", "Every kick, mute and ban the player ever had, lifted ones included.", f => f.SanctionsAsync(s => s.Count)),
				Player("sanctions.active_ban", RuleFieldTypes.Bool, "Banned now", "True while a ban is active.", f => f.SanctionsAsync(s => s.Banned)),
				Player("sanctions.active_mute", RuleFieldTypes.Bool, "Muted now", "True while a mute is active.", f => f.SanctionsAsync(s => s.Muted)),
				Player("reports.open", RuleFieldTypes.Number, "Open reports received", "Player reports against the player that staff have not closed.", f => f.OpenReportsAsync()),
				Player("stats.elo", RuleFieldTypes.Number, "Elo", "The all time rating once the player has a rated match.", f => f.EloAsync()),
				Player("stats.games_played", RuleFieldTypes.Number, "Games played", "Matches the player took a slot in.", f => f.GamesPlayedAsync()),
			};
			foreach (string kind in AntiCheatReviewKinds.All)
			{
				string key = kind;
				fields.Add(Player("anticheat." + kind, RuleFieldTypes.Number, "Unreviewed anti-cheat rows: " + kind.Replace('_', ' '), "Rows of this anti-cheat review list that staff have not reviewed.", f => f.UnreviewedAsync(key)));
			}

			fields.Add(Event(RuleEventFields.Provider, RuleFieldTypes.Text, "Sign-in method", "How the player signed in: password or the provider.", [RuleTriggers.SignIn]));
			fields.Add(Event(RuleEventFields.Client, RuleFieldTypes.Text, "Client", "The client id of the game session.", [RuleTriggers.GameSession]));
			fields.Add(Event(RuleEventFields.LobbyId, RuleFieldTypes.Number, "Lobby id", "The lobby the player joined.", [RuleTriggers.LobbyJoin]));
			fields.Add(Event(RuleEventFields.MatchId, RuleFieldTypes.Number, "Match id", "The match of the result or anti-cheat row.", [RuleTriggers.MatchResult, RuleTriggers.AnticheatReport]));
			fields.Add(Event(RuleEventFields.Won, RuleFieldTypes.Bool, "Won", "True when the player reported a win.", [RuleTriggers.MatchResult]));
			fields.Add(Event(RuleEventFields.Desynced, RuleFieldTypes.Bool, "Desynced", "True when the player reported a desync.", [RuleTriggers.MatchResult]));
			fields.Add(Event(RuleEventFields.DurationS, RuleFieldTypes.Number, "Match length in seconds", "The length the player reported.", [RuleTriggers.MatchResult]));
			fields.Add(Event(RuleEventFields.AnticheatKind, RuleFieldTypes.Text, "Anti-cheat list", "The review list of the new anti-cheat row.", [RuleTriggers.AnticheatReport]));
			return fields;
		}
	}

	// The keys of the event fields that the hooks fill
	public static class RuleEventFields
	{
		public const string Provider = "event.provider";
		public const string Client = "event.client";
		public const string LobbyId = "event.lobby_id";
		public const string MatchId = "event.match_id";
		public const string Won = "event.won";
		public const string Desynced = "event.desynced";
		public const string DurationS = "event.duration_s";
		public const string AnticheatKind = "event.anticheat_kind";
	}

	// One event a rule is checked on: the trigger, the player, the address when the event knows it and the event fields
	public sealed record RuleEvent(string Trigger, Int64 UserID, string? Ip, IReadOnlyDictionary<string, object?> Fields)
	{
		public static RuleEvent Of(string trigger, Int64 userID, string? ip = null, params (string Key, object? Value)[] fields) =>
			new RuleEvent(trigger, userID, ip, fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal));
	}
}
