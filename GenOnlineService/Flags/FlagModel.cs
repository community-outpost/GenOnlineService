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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GenOnlineService.Flags
{
	public static class FlagTypes
	{
		public const string Bool = "bool";
		public const string Int = "int";
		public const string Number = "number";
		public const string String = "string";
		public const string Choice = "choice";

		public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Bool, Int, Number, String, Choice };
	}

	public static class FlagScopes
	{
		// sent to the game over OFREP
		public const string Client = "client";

		// read by service code only, never sent to the game
		public const string Service = "service";

		public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Client, Service };
	}

	public static class FlagRuleKinds
	{
		public const string Role = "role";
		public const string Client = "client";
		public const string Country = "country";
		public const string Percentage = "percentage";

		public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Role, Client, Country, Percentage };
	}

	// One entry of a choice flag: the value sent to the game and the words staff see
	public sealed record FlagChoice
	{
		public JsonElement Value { get; init; }
		public string Label { get; init; } = System.String.Empty;
	}

	// "Players with role streamer get X", "10% of players get X"; the first matching rule wins, else the flag's value
	public sealed record FlagRule
	{
		public string Kind { get; init; } = System.String.Empty;

		// role names, client ids or ISO country codes; unused for percentage
		public IReadOnlyList<string>? Values { get; init; }

		// 1 to 99, only for percentage
		public int? Percent { get; init; }

		public JsonElement Value { get; init; }
	}

	// Custom flagd targeting written by hand; replaces Value and Rules when set
	public sealed record FlagCustom
	{
		public JsonObject Variants { get; init; } = new();
		public string DefaultVariant { get; init; } = System.String.Empty;
		public JsonNode? Targeting { get; init; }
	}

	// A field as staff see it: the schema that renders the form, the value and the plain targeting rules
	public sealed record FlagDefinition
	{
		public string Key { get; init; } = System.String.Empty;
		public string Scope { get; init; } = FlagScopes.Client;
		public string Label { get; init; } = System.String.Empty;
		public string? Description { get; init; }
		public string Group { get; init; } = System.String.Empty;
		public string Type { get; init; } = FlagTypes.String;

		// shown next to the number box ("ms", "seconds", "%"); for display only
		public string? Unit { get; init; }

		// bounds of an int or number; the longest text of a string
		public double? Min { get; init; }
		public double? Max { get; init; }

		public IReadOnlyList<FlagChoice>? Choices { get; init; }

		// false keeps the flag out of every answer, the game then uses its built-in default
		public bool Enabled { get; init; } = true;

		public JsonElement Value { get; init; }
		public IReadOnlyList<FlagRule> Rules { get; init; } = [];
		public FlagCustom? Custom { get; init; }
	}

	public static class FlagRules
	{
		public const int MaximumKeyLength = 128;
		public const int MaximumLabelLength = 80;
		public const int MaximumDescriptionLength = 500;
		public const int MaximumGroupLength = 64;
		public const int MaximumUnitLength = 16;
		public const int MaximumChoices = 50;
		public const int MaximumRules = 20;
		public const int MaximumRuleValues = 50;
		public const int MaximumStringLength = 4000;

		// lowercase words joined by dots, e.g. client.connection.reconnect_timeout_ms
		private static readonly Regex s_key = new("^[a-z][a-z0-9_]*(\\.[a-z0-9_]+)*$", RegexOptions.CultureInvariant);
		private static readonly Regex s_country = new("^[A-Z]{2}$", RegexOptions.CultureInvariant);

		// draft and revisions name routes under /api/v1/flags
		public static bool IsValidKey(string? key) => key != null && key.Length <= MaximumKeyLength && s_key.IsMatch(key) && key != "draft" && key != "revisions";

		// The field whose value is wrong, null when the definition is complete and consistent
		public static string? Check(FlagDefinition flag)
		{
			if (!IsValidKey(flag.Key)) return "key";
			if (!FlagScopes.All.Contains(flag.Scope)) return "scope";
			if (String.IsNullOrWhiteSpace(flag.Label) || flag.Label.Length > MaximumLabelLength) return "label";
			if (flag.Description != null && flag.Description.Length > MaximumDescriptionLength) return "description";
			if (String.IsNullOrWhiteSpace(flag.Group) || flag.Group.Length > MaximumGroupLength) return "group";
			if (!FlagTypes.All.Contains(flag.Type)) return "type";
			if (flag.Unit != null && flag.Unit.Length > MaximumUnitLength) return "unit";

			string? bounds = CheckBounds(flag);
			if (bounds != null) return bounds;

			if (flag.Type == FlagTypes.Choice)
			{
				if (flag.Choices == null || flag.Choices.Count == 0 || flag.Choices.Count > MaximumChoices) return "choices";
				var seen = new HashSet<string>(StringComparer.Ordinal);
				foreach (FlagChoice choice in flag.Choices)
				{
					bool scalar = choice.Value.ValueKind == JsonValueKind.String || choice.Value.ValueKind == JsonValueKind.Number;
					if (!scalar || String.IsNullOrWhiteSpace(choice.Label) || choice.Label.Length > MaximumLabelLength || !seen.Add(choice.Value.GetRawText()))
					{
						return "choices";
					}
				}
			}
			else if (flag.Choices != null)
			{
				return "choices";
			}

			if (flag.Custom != null)
			{
				return CheckCustom(flag);
			}

			if (!IsValueOf(flag, flag.Value)) return "value";
			return CheckRules(flag);
		}

		private static string? CheckBounds(FlagDefinition flag)
		{
			bool numeric = flag.Type == FlagTypes.Int || flag.Type == FlagTypes.Number;
			if (!numeric && flag.Type != FlagTypes.String)
			{
				return flag.Min != null ? "min" : flag.Max != null ? "max" : null;
			}
			if (flag.Type == FlagTypes.String)
			{
				if (flag.Min != null) return "min";
				if (flag.Max != null && (flag.Max < 1 || flag.Max > MaximumStringLength || flag.Max != Math.Floor(flag.Max.Value))) return "max";
				return null;
			}
			if (flag.Min != null && !Double.IsFinite(flag.Min.Value)) return "min";
			if (flag.Max != null && !Double.IsFinite(flag.Max.Value)) return "max";
			if (flag.Min != null && flag.Max != null && flag.Min > flag.Max) return "max";
			return null;
		}

		private static string? CheckRules(FlagDefinition flag)
		{
			if (flag.Rules.Count > MaximumRules) return "rules";
			for (int i = 0; i < flag.Rules.Count; i++)
			{
				FlagRule rule = flag.Rules[i];
				if (!FlagRuleKinds.All.Contains(rule.Kind)) return "rules";
				if (rule.Kind == FlagRuleKinds.Percentage)
				{
					// fractional ends the targeting, so the percentage rule is the last one
					if (i != flag.Rules.Count - 1 || rule.Percent == null || rule.Percent < 1 || rule.Percent > 99 || rule.Values != null) return "rules";
				}
				else
				{
					if (rule.Percent != null || rule.Values == null || rule.Values.Count == 0 || rule.Values.Count > MaximumRuleValues) return "rules";
					foreach (string value in rule.Values)
					{
						bool ok = rule.Kind switch
						{
							FlagRuleKinds.Role => StaffRoles.IsKnown(value),
							FlagRuleKinds.Country => s_country.IsMatch(value),
							_ => !String.IsNullOrWhiteSpace(value) && value.Length <= 64
						};
						if (!ok) return "rules";
					}
				}
				if (!IsValueOf(flag, rule.Value)) return "rules";
			}
			return null;
		}

		private static string? CheckCustom(FlagDefinition flag)
		{
			FlagCustom custom = flag.Custom!;
			if (flag.Rules.Count > 0) return "rules";
			if (custom.Variants.Count == 0 || custom.Variants.Count > MaximumRules + 1 || !custom.Variants.ContainsKey(custom.DefaultVariant)) return "custom";
			foreach ((string name, JsonNode? value) in custom.Variants)
			{
				if (String.IsNullOrWhiteSpace(name) || name.Length > 64 || value == null) return "custom";
				using JsonDocument document = JsonDocument.Parse(value.ToJsonString());
				if (!IsValueOf(flag, document.RootElement)) return "custom";
			}
			if (custom.Targeting != null && custom.Targeting is not JsonObject) return "custom";
			if (!IsValueOf(flag, flag.Value)) return "value";
			if (!JsonNode.DeepEquals(JsonNode.Parse(flag.Value.GetRawText()), custom.Variants[custom.DefaultVariant])) return "value";
			return null;
		}

		// True when value fits the flag's type, bounds and choices
		public static bool IsValueOf(FlagDefinition flag, JsonElement value)
		{
			switch (flag.Type)
			{
				case FlagTypes.Bool:
					return value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False;
				case FlagTypes.Int:
					return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long whole) && InBounds(flag, whole);
				case FlagTypes.Number:
					return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && Double.IsFinite(number) && InBounds(flag, number);
				case FlagTypes.String:
					return value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= (flag.Max ?? MaximumStringLength);
				case FlagTypes.Choice:
					return flag.Choices != null && flag.Choices.Any(c => c.Value.ValueKind == value.ValueKind && c.Value.GetRawText() == value.GetRawText());
				default:
					return false;
			}
		}

		private static bool InBounds(FlagDefinition flag, double value) => (flag.Min == null || value >= flag.Min) && (flag.Max == null || value <= flag.Max);

		// Upper case first letter, words split on underscores; a trailing unit word (ms, sec, percent) moves to Unit
		public static (string Label, string? Unit) LabelOf(string word)
		{
			string[] parts = word.Split('_', StringSplitOptions.RemoveEmptyEntries);
			string? unit = null;
			if (parts.Length > 1)
			{
				string last = parts[^1];
				unit = last switch
				{
					"ms" => "ms",
					"sec" or "secs" or "seconds" => "seconds",
					"percent" => "%",
					"bytes" => "bytes",
					_ => null
				};
				if (unit != null)
				{
					parts = parts[..^1];
				}
			}
			string text = String.Join(' ', parts);
			return (text.Length == 0 ? word : Char.ToUpperInvariant(text[0]) + text[1..], unit);
		}
	}

	// Converts between staff definitions and flagd flag definitions (https://flagd.dev/reference/flag-definitions/)
	public static class Flagd
	{
		public const string Schema = "https://flagd.dev/schema/v0/flags.json";
		public const string DefaultVariant = "default";

		// Our schema rides in flagd metadata; flagd metadata values are primitives, so lists are JSON text
		private const string MetaScope = "scope";
		private const string MetaLabel = "label";
		private const string MetaDescription = "description";
		private const string MetaGroup = "group";
		private const string MetaType = "type";
		private const string MetaUnit = "unit";
		private const string MetaMin = "min";
		private const string MetaMax = "max";
		private const string MetaChoices = "choices";
		private const string MetaRules = "rules";

		private static readonly JsonSerializerOptions s_json = RealtimeJson.Create();

		public static JsonObject ToFlag(FlagDefinition flag)
		{
			var result = new JsonObject { ["state"] = flag.Enabled ? "ENABLED" : "DISABLED" };

			if (flag.Custom != null)
			{
				result["variants"] = flag.Custom.Variants.DeepClone();
				result["defaultVariant"] = flag.Custom.DefaultVariant;
				if (flag.Custom.Targeting != null)
				{
					result["targeting"] = flag.Custom.Targeting.DeepClone();
				}
			}
			else
			{
				var variants = new JsonObject { [DefaultVariant] = Node(flag.Value) };
				for (int i = 0; i < flag.Rules.Count; i++)
				{
					variants[RuleVariant(i)] = Node(flag.Rules[i].Value);
				}
				result["variants"] = variants;
				result["defaultVariant"] = DefaultVariant;
				JsonNode? targeting = TargetingOf(flag.Rules);
				if (targeting != null)
				{
					result["targeting"] = targeting;
				}
			}

			var metadata = new JsonObject
			{
				[MetaScope] = flag.Scope,
				[MetaLabel] = flag.Label,
				[MetaGroup] = flag.Group,
				[MetaType] = flag.Type
			};
			if (flag.Description != null) metadata[MetaDescription] = flag.Description;
			if (flag.Unit != null) metadata[MetaUnit] = flag.Unit;
			if (flag.Min != null) metadata[MetaMin] = flag.Min.Value;
			if (flag.Max != null) metadata[MetaMax] = flag.Max.Value;
			if (flag.Choices != null) metadata[MetaChoices] = JsonSerializer.Serialize(flag.Choices, s_json);
			if (flag.Custom == null && flag.Rules.Count > 0) metadata[MetaRules] = JsonSerializer.Serialize(flag.Rules, s_json);
			result["metadata"] = metadata;
			return result;
		}

		public static JsonObject ToDocument(IEnumerable<FlagDefinition> flags)
		{
			var map = new JsonObject();
			foreach (FlagDefinition flag in flags.OrderBy(f => f.Key, StringComparer.Ordinal))
			{
				map[flag.Key] = ToFlag(flag);
			}
			return new JsonObject { ["$schema"] = Schema, ["flags"] = map };
		}

		// Reads a flag written by ToFlag or by hand; a flag without our metadata gets a schema guessed from its default value.
		// Null with the failing field when the flag cannot be read.
		public static (FlagDefinition? Flag, string? Field) FromFlag(string key, JsonNode? node)
		{
			if (node is not JsonObject flag || flag["variants"] is not JsonObject variants || flag["defaultVariant"]?.GetValueKind() != JsonValueKind.String)
			{
				return (null, "flags." + key);
			}
			string defaultVariant = flag["defaultVariant"]!.GetValue<string>();
			if (!variants.ContainsKey(defaultVariant) || variants[defaultVariant] == null)
			{
				return (null, "flags." + key);
			}
			JsonObject metadata = flag["metadata"] as JsonObject ?? new JsonObject();
			JsonElement value = Element(variants[defaultVariant]!);

			string word = key.Contains('.') ? key[(key.LastIndexOf('.') + 1)..] : key;
			(string guessedLabel, string? guessedUnit) = FlagRules.LabelOf(word);

			try
			{
				string? rulesText = Text(metadata, MetaRules);
				string? choicesText = Text(metadata, MetaChoices);
				var definition = new FlagDefinition
				{
					Key = key,
					Scope = Text(metadata, MetaScope) ?? FlagScopes.Client,
					Label = Text(metadata, MetaLabel) ?? guessedLabel,
					Description = Text(metadata, MetaDescription),
					Group = Text(metadata, MetaGroup) ?? GroupOf(key),
					Type = Text(metadata, MetaType) ?? TypeOf(value),
					Unit = metadata.ContainsKey(MetaUnit) ? Text(metadata, MetaUnit) : guessedUnit,
					Min = Number(metadata, MetaMin),
					Max = Number(metadata, MetaMax),
					Choices = choicesText == null ? null : JsonSerializer.Deserialize<List<FlagChoice>>(choicesText, s_json),
					Enabled = flag["state"]?.GetValueKind() != JsonValueKind.String || flag["state"]!.GetValue<string>() != "DISABLED",
					Value = value
				};

				if (rulesText != null)
				{
					return (definition with { Rules = JsonSerializer.Deserialize<List<FlagRule>>(rulesText, s_json) ?? [] }, null);
				}

				JsonNode? targeting = flag["targeting"];
				bool plain = variants.Count == 1 && (targeting == null || (targeting is JsonObject empty && empty.Count == 0));
				if (plain)
				{
					return (definition, null);
				}
				return (definition with { Custom = new FlagCustom { Variants = (JsonObject)variants.DeepClone(), DefaultVariant = defaultVariant, Targeting = targeting?.DeepClone() } }, null);
			}
			catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
			{
				return (null, "flags." + key);
			}
		}

		public static string RuleVariant(int index) => "rule" + (index + 1).ToString(CultureInfo.InvariantCulture);

		// if/else over the rules; a last percentage rule becomes fractional, which picks the same players every time
		private static JsonNode? TargetingOf(IReadOnlyList<FlagRule> rules)
		{
			if (rules.Count == 0)
			{
				return null;
			}

			JsonNode tail = (JsonNode)DefaultVariant;
			int conditions = rules.Count;
			FlagRule last = rules[^1];
			if (last.Kind == FlagRuleKinds.Percentage)
			{
				tail = new JsonObject
				{
					["fractional"] = new JsonArray(
						new JsonArray(RuleVariant(rules.Count - 1), last.Percent!.Value),
						new JsonArray(DefaultVariant, 100 - last.Percent!.Value))
				};
				conditions--;
			}

			if (conditions == 0)
			{
				return tail;
			}

			var arguments = new JsonArray();
			for (int i = 0; i < conditions; i++)
			{
				arguments.Add(ConditionOf(rules[i]));
				arguments.Add(RuleVariant(i));
			}
			arguments.Add(tail);
			return new JsonObject { ["if"] = arguments };
		}

		private static JsonNode ConditionOf(FlagRule rule)
		{
			if (rule.Kind == FlagRuleKinds.Role)
			{
				// the context carries every role of the player
				JsonNode[] any = rule.Values!.Select(role => (JsonNode)new JsonObject { ["in"] = new JsonArray(role, new JsonObject { ["var"] = FlagContext.Roles }) }).ToArray();
				return any.Length == 1 ? any[0] : new JsonObject { ["or"] = new JsonArray(any) };
			}

			string variable = rule.Kind == FlagRuleKinds.Client ? FlagContext.Client : FlagContext.Country;
			return new JsonObject { ["in"] = new JsonArray(new JsonObject { ["var"] = variable }, new JsonArray(rule.Values!.Select(v => (JsonNode)v).ToArray())) };
		}

		private static string GroupOf(string key)
		{
			string[] parts = key.Split('.');
			if (parts.Length <= 2)
			{
				return parts[0];
			}
			return String.Join('.', parts[1..^1]);
		}

		private static string TypeOf(JsonElement value) => value.ValueKind switch
		{
			JsonValueKind.True or JsonValueKind.False => FlagTypes.Bool,
			JsonValueKind.Number => value.TryGetInt64(out _) ? FlagTypes.Int : FlagTypes.Number,
			_ => FlagTypes.String
		};

		private static string? Text(JsonObject metadata, string name) => metadata[name]?.GetValueKind() == JsonValueKind.String ? metadata[name]!.GetValue<string>() : null;

		private static double? Number(JsonObject metadata, string name) => metadata[name]?.GetValueKind() == JsonValueKind.Number ? Double.Parse(metadata[name]!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture) : null;

		public static JsonNode? Node(JsonElement value) => JsonNode.Parse(value.GetRawText());

		public static JsonElement Element(JsonNode node)
		{
			using JsonDocument document = JsonDocument.Parse(node.ToJsonString());
			return document.RootElement.Clone();
		}
	}
}
