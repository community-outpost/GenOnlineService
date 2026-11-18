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

using System.Text.Json;

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

	// One field of the catalogue: its type, the triggers that provide it and how its values load. A yes or no field also
	// names its two states as whole sentences, which the editor offers as one choice.
	public sealed record RuleField(string Key, string Type, string Label, string Description, IReadOnlyList<string> Triggers, Func<RuleFacts, Task<object?[]>> Load)
	{
		public IReadOnlyList<string> Operators => RuleFieldTypes.OperatorsOf(Type);

		public string? TrueLabel { get; init; }
		public string? FalseLabel { get; init; }

		public bool IsProvidedBy(string trigger) => Triggers.Contains(trigger, StringComparer.Ordinal);
	}

	// The fixed catalogue of player data a rule can read. Player fields come with every trigger, event fields only with
	// the triggers whose event carries them. The keys follow the Elastic Common Schema: host for the devices, source for
	// the address, user for the account, event for the event and game for the match and lobby of the event. Each field
	// also names its key before the rename, which documents saved earlier still use.
	public static class RuleFields
	{
		public static readonly IReadOnlyList<string> AllTriggers = RuleTriggers.All;

		public static readonly IReadOnlyList<RuleField> All;

		// The key of every field before the rename, to its current key
		public static readonly IReadOnlyDictionary<string, string> Renamed;

		private static readonly Dictionary<string, RuleField> s_byKey;

		static RuleFields()
		{
			var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
			All = Build(renamed);
			Renamed = renamed;
			s_byKey = All.ToDictionary(f => f.Key, StringComparer.Ordinal);
		}

		// A field by its current key or by its key before the rename
		public static RuleField? Find(string? key) => key != null && s_byKey.TryGetValue(CurrentKey(key), out RuleField? field) ? field : null;

		// The current key of a key, the same key when it was never renamed
		public static string CurrentKey(string key) => Renamed.TryGetValue(key, out string? current) ? current : key;

		private static RuleField Player(string key, string type, string label, string description, Func<RuleFacts, Task<object?[]>> load) =>
			new RuleField(key, type, label, description, AllTriggers, load);

		private static RuleField Event(string key, string type, string label, string description, IReadOnlyList<string> triggers) =>
			new RuleField(key, type, label, description, triggers, facts => Task.FromResult(facts.EventValue(key)));

		private static List<RuleField> Build(Dictionary<string, string> renamed)
		{
			var fields = new List<RuleField>();

			// The key before the rename, then the field under its current key
			void Add(string oldKey, RuleField field)
			{
				fields.Add(field);
				renamed.Add(oldKey, field.Key);
			}

			Add("device.machine_guid", Player("host.id", RuleFieldTypes.Device, "Machine GUID", "Any machine GUID the player signed in with.", f => f.DeviceKeysAsync(DeviceKinds.MachineGuid)));
			Add("device.mac", Player("host.mac", RuleFieldTypes.Device, "MAC address", "Any MAC address the player signed in with.", f => f.DeviceKeysAsync(DeviceKinds.Mac)));
			Add("device.volume_serial", Player("host.volume_serial", RuleFieldTypes.Device, "Volume serial", "Any volume serial the player signed in with.", f => f.DeviceKeysAsync(DeviceKinds.VolumeSerial)));
			Add("mac.vendor", Player("host.mac_vendor", RuleFieldTypes.Text, "MAC vendor", "The vendor of any MAC address of the player, from the IEEE registry.", f => f.MacVendorsAsync()));
			Add("mac.locally_administered", Player("host.mac_virtual", RuleFieldTypes.Bool, "Virtual MAC", "True when any MAC address of the player is locally administered, as virtual adapters are.", f => f.MacLocallyAdministeredAsync())
				with { TrueLabel = "MAC is virtual", FalseLabel = "MAC is not virtual" });
			Add("network.ip", Player("source.ip", RuleFieldTypes.Ip, "IP address", "The address of the event, else of the last session.", f => f.IpAsync()));
			Add("network.country", Player("source.geo.country_iso_code", RuleFieldTypes.Text, "Country", "The ISO country code of the address.", f => f.AddressAsync(a => a.Country)));
			Add("network.city", Player("source.geo.city_name", RuleFieldTypes.Text, "City", "The city of the address.", f => f.AddressAsync(a => a.City)));
			Add("network.asn", Player("source.as.number", RuleFieldTypes.Number, "ASN", "The autonomous system number of the address.", f => f.AddressAsync(a => a.Asn)));
			Add("network.owner", Player("source.as.organization.name", RuleFieldTypes.Text, "Network owner", "The organization of the autonomous system of the address.", f => f.AddressAsync(a => a.Owner)));
			Add("account.age_days", Player("user.age_days", RuleFieldTypes.Number, "Account age in days", "Whole days since the account was created.", f => f.AccountAsync(a => a.AgeDays)));
			Add("account.email_domain", Player("user.email_domain", RuleFieldTypes.Text, "Email domain", "The domain of the email address.", f => f.AccountAsync(a => a.EmailDomain)));
			Add("account.email_confirmed", Player("user.email_confirmed", RuleFieldTypes.Bool, "Email confirmed", "True when the email address is confirmed.", f => f.AccountAsync(a => a.EmailConfirmed))
				with { TrueLabel = "Email is confirmed", FalseLabel = "Email is not confirmed" });
			Add("account.providers", Player("user.providers", RuleFieldTypes.Text, "Sign-in providers", "Every way the player signs in: password or a provider such as discord.", f => f.ProvidersAsync()));
			Add("account.name", Player("user.name", RuleFieldTypes.Text, "Player name", "The display name without its tag.", f => f.AccountAsync(a => a.Name)));
			Add("account.name_key", Player("user.name_key", RuleFieldTypes.Text, "Name key", "The look-alike key of the name, the same for names that read alike.", f => f.AccountAsync(a => a.NameKey)));
			Add("account.two_factor", Player("user.two_factor", RuleFieldTypes.Bool, "Two-factor on", "True when two-factor sign-in is on.", f => f.AccountAsync(a => a.TwoFactor))
				with { TrueLabel = "Two-factor is on", FalseLabel = "Two-factor is off" });
			Add("sanctions.count", Player("user.sanctions.count", RuleFieldTypes.Number, "Prior sanctions", "Every kick, mute and ban the player ever had, lifted ones included.", f => f.SanctionsAsync(s => s.Count)));
			Add("sanctions.active_ban", Player("user.sanctions.active_ban", RuleFieldTypes.Bool, "Banned now", "True while a ban is active.", f => f.SanctionsAsync(s => s.Banned))
				with { TrueLabel = "Player is banned now", FalseLabel = "Player is not banned now" });
			Add("sanctions.active_mute", Player("user.sanctions.active_mute", RuleFieldTypes.Bool, "Muted now", "True while a mute is active.", f => f.SanctionsAsync(s => s.Muted))
				with { TrueLabel = "Player is muted now", FalseLabel = "Player is not muted now" });
			Add("reports.open", Player("user.reports.open", RuleFieldTypes.Number, "Open reports received", "Player reports against the player that staff have not closed.", f => f.OpenReportsAsync()));
			Add("stats.elo", Player("user.stats.elo", RuleFieldTypes.Number, "Elo", "The all time rating once the player has a rated match.", f => f.EloAsync()));
			Add("stats.games_played", Player("user.stats.games_played", RuleFieldTypes.Number, "Games played", "Matches the player took a slot in.", f => f.GamesPlayedAsync()));
			foreach (string kind in AntiCheatReviewKinds.All)
			{
				string key = kind;
				Add("anticheat." + kind, Player("user.anticheat." + kind, RuleFieldTypes.Number, "Unreviewed anti-cheat rows: " + kind.Replace('_', ' '), "Rows of this anti-cheat review list that staff have not reviewed.", f => f.UnreviewedAsync(key)));
			}

			// The trigger itself, which every event carries, is new with the rename
			fields.Add(new RuleField(RuleEventFields.Action, RuleFieldTypes.Text, "Event", "The event the rule is checked on: " + String.Join(", ", RuleTriggers.All) + ".", AllTriggers, facts => Task.FromResult(facts.ActionValue())));
			Add("event.provider", Event(RuleEventFields.Provider, RuleFieldTypes.Text, "Sign-in method", "How the player signed in: password or the provider.", [RuleTriggers.SignIn]));
			Add("event.client", Event(RuleEventFields.Client, RuleFieldTypes.Text, "Client", "The client id of the game session.", [RuleTriggers.GameSession]));
			Add("event.lobby_id", Event(RuleEventFields.LobbyId, RuleFieldTypes.Number, "Lobby id", "The lobby the player joined.", [RuleTriggers.LobbyJoin]));
			Add("event.match_id", Event(RuleEventFields.MatchId, RuleFieldTypes.Number, "Match id", "The match of the result or anti-cheat row.", [RuleTriggers.MatchResult, RuleTriggers.AnticheatReport]));
			Add("event.won", Event(RuleEventFields.Won, RuleFieldTypes.Bool, "Won", "True when the player reported a win.", [RuleTriggers.MatchResult])
				with { TrueLabel = "Player won", FalseLabel = "Player did not win" });
			Add("event.desynced", Event(RuleEventFields.Desynced, RuleFieldTypes.Bool, "Desynced", "True when the player reported a desync.", [RuleTriggers.MatchResult])
				with { TrueLabel = "Match desynced", FalseLabel = "Match did not desync" });
			Add("event.duration_s", Event(RuleEventFields.DurationS, RuleFieldTypes.Number, "Match length in seconds", "The length the player reported.", [RuleTriggers.MatchResult]));
			Add("event.anticheat_kind", Event(RuleEventFields.AnticheatKind, RuleFieldTypes.Text, "Anti-cheat list", "The review list of the new anti-cheat row.", [RuleTriggers.AnticheatReport]));
			return fields;
		}

		// A JSON object of field to values with every key read under its current key, for match records saved earlier
		public static string CurrentFactsJson(string json)
		{
			using JsonDocument document = JsonDocument.Parse(json);
			if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.EnumerateObject().Any(p => Renamed.ContainsKey(p.Name)))
			{
				return json;
			}
			var facts = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
			foreach (JsonProperty property in document.RootElement.EnumerateObject())
			{
				facts[CurrentKey(property.Name)] = property.Value.Clone();
			}
			return JsonSerializer.Serialize(facts);
		}
	}

	// The keys of the event fields that the hooks fill
	public static class RuleEventFields
	{
		// The trigger, which the event itself fills
		public const string Action = "event.action";

		public const string Provider = "event.sign_in_provider";
		public const string Client = "game.client.id";
		public const string LobbyId = "game.lobby.id";
		public const string MatchId = "game.match.id";
		public const string Won = "game.match.won";
		public const string Desynced = "game.match.desynced";
		public const string DurationS = "game.match.duration_s";
		public const string AnticheatKind = "game.anticheat.kind";
	}

	// One event a rule is checked on: the trigger, the player, the address when the event knows it and the event fields
	public sealed record RuleEvent(string Trigger, Int64 UserID, string? Ip, IReadOnlyDictionary<string, object?> Fields)
	{
		public static RuleEvent Of(string trigger, Int64 userID, string? ip = null, params (string Key, object? Value)[] fields) =>
			new RuleEvent(trigger, userID, ip, fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal));
	}
}
