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
using System.Net;
using System.Text.Json;

namespace GenOnlineService.Rules
{
	// One thing wrong with a document: where it is and why
	public sealed record RuleProblem(string Path, string Reason);

	// The read and the checks of a rule document on save
	public static class RuleDocuments
	{
		public const int MaximumLength = 65536;
		public const int MaximumNodes = 100;
		public const int MaximumEdges = 300;
		public const int MaximumIdLength = 64;
		public const int MaximumDepth = 6;
		public const int MaximumConditions = 50;
		public const int MaximumListItems = 100;
		public const int MaximumTextLength = 256;

		private static readonly JsonSerializerOptions s_json = RealtimeJson.Create();

		public static string Serialize(RuleDocument document) => JsonSerializer.Serialize(document, s_json);

		public static RuleDocument Deserialize(string json) => JsonSerializer.Deserialize<RuleDocument>(json, s_json)!;

		// The document and every problem with it. A document with problems is never stored or compiled.
		public static (RuleDocument? Document, List<RuleProblem> Problems) Parse(string? json)
		{
			if (json == null)
			{
				return (null, [new RuleProblem("document", "missing")]);
			}
			if (json.Length > MaximumLength)
			{
				return (null, [new RuleProblem("document", $"longer than {MaximumLength} characters")]);
			}
			RuleDocument? document;
			try
			{
				document = JsonSerializer.Deserialize<RuleDocument>(json, s_json);
			}
			catch (JsonException ex)
			{
				return (null, [new RuleProblem(String.IsNullOrEmpty(ex.Path) || ex.Path == "$" ? "document" : ex.Path.TrimStart('$', '.'), "not the expected shape")]);
			}
			if (document == null)
			{
				return (null, [new RuleProblem("document", "missing")]);
			}
			List<RuleProblem> problems = Validate(document);
			return (problems.Count == 0 ? document : null, problems);
		}

		public static List<RuleProblem> Validate(RuleDocument document)
		{
			var problems = new List<RuleProblem>();
			IReadOnlyList<RuleNode> nodes = document.Nodes ?? [];
			IReadOnlyList<RuleEdge> edges = document.Edges ?? [];
			if (nodes.Count == 0 || nodes.Count > MaximumNodes)
			{
				problems.Add(new RuleProblem("nodes", $"between 1 and {MaximumNodes} nodes"));
				return problems;
			}
			if (edges.Count > MaximumEdges)
			{
				problems.Add(new RuleProblem("edges", $"at most {MaximumEdges} edges"));
				return problems;
			}

			var byID = new Dictionary<string, RuleNode>(StringComparer.Ordinal);
			for (int i = 0; i < nodes.Count; i++)
			{
				RuleNode node = nodes[i];
				string path = $"nodes[{i}]";
				if (String.IsNullOrEmpty(node.Id) || node.Id.Length > MaximumIdLength)
				{
					problems.Add(new RuleProblem(path + ".id", $"between 1 and {MaximumIdLength} characters"));
				}
				else if (!byID.TryAdd(node.Id, node))
				{
					problems.Add(new RuleProblem(path + ".id", "used by another node"));
				}
				ValidateNode(node, path, problems);
			}

			List<RuleNode> triggers = nodes.Where(n => n.Kind == RuleNodeKinds.Trigger).ToList();
			if (triggers.Count != 1)
			{
				problems.Add(new RuleProblem("nodes", "exactly one trigger node"));
			}
			string? trigger = triggers.Count == 1 ? triggers[0].Trigger : null;
			if (RuleTriggers.IsKnown(trigger))
			{
				for (int i = 0; i < nodes.Count; i++)
				{
					if (nodes[i].Kind == RuleNodeKinds.Condition && nodes[i].Query != null)
					{
						int conditions = 0;
						ValidateQuery(nodes[i].Query!, trigger!, $"nodes[{i}].query", 1, ref conditions, problems);
					}
				}
			}

			var seen = new HashSet<(string, string, string?)>();
			var next = new Dictionary<string, List<string>>(StringComparer.Ordinal);
			for (int i = 0; i < edges.Count; i++)
			{
				RuleEdge edge = edges[i];
				string path = $"edges[{i}]";
				if (!byID.TryGetValue(edge.From ?? "", out RuleNode? from))
				{
					problems.Add(new RuleProblem(path + ".from", "names no node"));
					continue;
				}
				if (!byID.TryGetValue(edge.To ?? "", out RuleNode? to))
				{
					problems.Add(new RuleProblem(path + ".to", "names no node"));
					continue;
				}
				if (to.Kind == RuleNodeKinds.Trigger)
				{
					problems.Add(new RuleProblem(path + ".to", "nothing leads into the trigger"));
				}
				if (from.Kind == RuleNodeKinds.Action)
				{
					problems.Add(new RuleProblem(path + ".from", "an action ends its branch"));
				}
				else if (from.Kind == RuleNodeKinds.Condition && edge.Output is not (RuleOutputs.True or RuleOutputs.False))
				{
					problems.Add(new RuleProblem(path + ".output", "true or false from a condition"));
				}
				else if (from.Kind == RuleNodeKinds.Trigger && edge.Output != null)
				{
					problems.Add(new RuleProblem(path + ".output", "none from the trigger"));
				}
				if (!seen.Add((edge.From!, edge.To!, edge.Output)))
				{
					problems.Add(new RuleProblem(path, "the same edge twice"));
				}
				if (!next.TryGetValue(edge.From!, out List<string>? targets))
				{
					next[edge.From!] = targets = new List<string>();
				}
				targets.Add(edge.To!);
			}

			if (problems.Count > 0)
			{
				return problems;
			}

			if (HasCycle(byID.Keys, next))
			{
				problems.Add(new RuleProblem("edges", "the edges form a loop"));
				return problems;
			}

			HashSet<string> reached = Reach(triggers[0].Id, next);
			for (int i = 0; i < nodes.Count; i++)
			{
				if (!reached.Contains(nodes[i].Id))
				{
					problems.Add(new RuleProblem($"nodes[{i}]", "not reached from the trigger"));
				}
			}
			if (!nodes.Any(n => n.Kind == RuleNodeKinds.Action && reached.Contains(n.Id)))
			{
				problems.Add(new RuleProblem("nodes", "no action is reached from the trigger"));
			}
			return problems;
		}

		private static void ValidateNode(RuleNode node, string path, List<RuleProblem> problems)
		{
			switch (node.Kind)
			{
				case RuleNodeKinds.Trigger:
					if (!RuleTriggers.IsKnown(node.Trigger))
					{
						problems.Add(new RuleProblem(path + ".trigger", "one of " + String.Join(", ", RuleTriggers.All)));
					}
					break;
				case RuleNodeKinds.Condition:
					if (node.Query == null)
					{
						problems.Add(new RuleProblem(path + ".query", "missing"));
					}
					break;
				case RuleNodeKinds.Action:
					ValidateAction(node, path, problems);
					break;
				case RuleNodeKinds.Step:
					problems.Add(new RuleProblem(path + ".kind", "step nodes are not supported yet"));
					break;
				default:
					problems.Add(new RuleProblem(path + ".kind", "trigger, condition or action"));
					break;
			}
		}

		private static void ValidateAction(RuleNode node, string path, List<RuleProblem> problems)
		{
			if (!RuleActionKinds.All.Contains(node.Action ?? "", StringComparer.Ordinal))
			{
				problems.Add(new RuleProblem(path + ".action", "one of " + String.Join(", ", RuleActionKinds.All)));
				return;
			}
			if (String.IsNullOrWhiteSpace(node.Reason) || node.Reason.Length > ModerationManager.MaximumReasonLength)
			{
				problems.Add(new RuleProblem(path + ".reason", $"between 1 and {ModerationManager.MaximumReasonLength} characters"));
			}
			bool takesDuration = node.Action is RuleActionKinds.Mute or RuleActionKinds.Ban;
			if (!takesDuration && node.DurationS != null)
			{
				problems.Add(new RuleProblem(path + ".duration_s", "only a mute or a ban lasts"));
			}
			else if (node.Action == RuleActionKinds.Mute && node.DurationS == null)
			{
				problems.Add(new RuleProblem(path + ".duration_s", "a mute needs a length"));
			}
			else if (node.DurationS is long seconds && (seconds < SanctionActions.MinimumDurationS || seconds > SanctionActions.MaximumDurationS || seconds % 60 != 0))
			{
				problems.Add(new RuleProblem(path + ".duration_s", "whole minutes from one minute up to ten years"));
			}
		}

		private static void ValidateQuery(RuleQuery query, string trigger, string path, int depth, ref int conditions, List<RuleProblem> problems)
		{
			if (depth > MaximumDepth)
			{
				problems.Add(new RuleProblem(path, $"groups nest at most {MaximumDepth} deep"));
				return;
			}
			if (query.Field == null)
			{
				if (query.Match is not (RuleGroupMatches.All or RuleGroupMatches.Any))
				{
					problems.Add(new RuleProblem(path + ".match", "all or any"));
				}
				if (query.Rules == null || query.Rules.Count == 0)
				{
					problems.Add(new RuleProblem(path + ".rules", "at least one rule"));
					return;
				}
				for (int i = 0; i < query.Rules.Count; i++)
				{
					if (query.Rules[i] == null)
					{
						problems.Add(new RuleProblem($"{path}.rules[{i}]", "missing"));
						continue;
					}
					ValidateQuery(query.Rules[i], trigger, $"{path}.rules[{i}]", depth + 1, ref conditions, problems);
				}
				return;
			}

			if (++conditions > MaximumConditions)
			{
				if (conditions == MaximumConditions + 1)
				{
					problems.Add(new RuleProblem(path, $"at most {MaximumConditions} conditions in one node"));
				}
				return;
			}
			if (query.Rules != null || query.Match != null)
			{
				problems.Add(new RuleProblem(path, "a condition or a group, not both"));
			}
			RuleField? field = RuleFields.Find(query.Field);
			if (field == null)
			{
				problems.Add(new RuleProblem(path + ".field", "not a known field"));
				return;
			}
			if (!field.IsProvidedBy(trigger))
			{
				problems.Add(new RuleProblem(path + ".field", $"the {trigger} trigger does not provide it"));
				return;
			}
			if (!field.Operators.Contains(query.Operator ?? "", StringComparer.Ordinal))
			{
				problems.Add(new RuleProblem(path + ".operator", "one of " + String.Join(", ", field.Operators)));
				return;
			}
			if (ValueProblem(field.Type, query.Operator!, query.Value) is string reason)
			{
				problems.Add(new RuleProblem(path + ".value", reason));
			}
		}

		// Why the value does not fit the operator and type, null when it does
		private static string? ValueProblem(string type, string op, JsonElement? value)
		{
			bool missing = value == null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
			switch (op)
			{
				case RuleOperators.IsEmpty:
					return missing ? null : "none";
				case RuleOperators.InList:
					if (missing || value!.Value.ValueKind != JsonValueKind.Array || value.Value.GetArrayLength() == 0 || value.Value.GetArrayLength() > MaximumListItems)
					{
						return $"a list of 1 to {MaximumListItems} values";
					}
					return value.Value.EnumerateArray().Select(item => ScalarProblem(type, op, item)).FirstOrDefault(p => p != null);
				case RuleOperators.Between:
					if (missing || value!.Value.ValueKind != JsonValueKind.Array || value.Value.GetArrayLength() != 2
						|| value.Value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Number) || value.Value[0].GetDouble() > value.Value[1].GetDouble())
					{
						return "two numbers, the lower first";
					}
					return null;
				default:
					return missing ? "a value" : ScalarProblem(type, op, value!.Value);
			}
		}

		private static string? ScalarProblem(string type, string op, JsonElement value)
		{
			switch (type)
			{
				case RuleFieldTypes.Number:
					return value.ValueKind == JsonValueKind.Number ? null : "a number";
				case RuleFieldTypes.Bool:
					return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "true or false";
				default:
					if (value.ValueKind != JsonValueKind.String || value.GetString()!.Trim().Length == 0 || value.GetString()!.Length > MaximumTextLength)
					{
						return $"a text of 1 to {MaximumTextLength} characters";
					}
					string text = value.GetString()!.Trim();
					if (type == RuleFieldTypes.Ip && op == RuleOperators.Cidr && !IPNetwork.TryParse(text, out _))
					{
						return "a network such as 203.0.113.0/24";
					}
					if (type == RuleFieldTypes.Ip && op is RuleOperators.Equals or RuleOperators.NotEquals or RuleOperators.InList && !IPAddress.TryParse(text, out _))
					{
						return "an IP address";
					}
					return null;
			}
		}

		private static bool HasCycle(IEnumerable<string> ids, Dictionary<string, List<string>> next)
		{
			var state = new Dictionary<string, int>(StringComparer.Ordinal);
			bool Visit(string id)
			{
				state.TryGetValue(id, out int s);
				if (s == 1)
				{
					return true;
				}
				if (s == 2)
				{
					return false;
				}
				state[id] = 1;
				if (next.TryGetValue(id, out List<string>? targets) && targets.Any(Visit))
				{
					return true;
				}
				state[id] = 2;
				return false;
			}
			return ids.Any(Visit);
		}

		private static HashSet<string> Reach(string start, Dictionary<string, List<string>> next)
		{
			var reached = new HashSet<string>(StringComparer.Ordinal) { start };
			var pending = new Queue<string>([start]);
			while (pending.Count > 0)
			{
				foreach (string target in next.GetValueOrDefault(pending.Dequeue()) ?? [])
				{
					if (reached.Add(target))
					{
						pending.Enqueue(target);
					}
				}
			}
			return reached;
		}

		public static string TriggerOf(RuleDocument document) => document.Nodes.First(n => n.Kind == RuleNodeKinds.Trigger).Trigger!;
	}

	// The fields read and the actions reached for one player
	public sealed record RuleResult(IReadOnlyList<RuleNode> Actions, IReadOnlyList<string> FieldsRead);

	// A rule version turned into delegates once, so an evaluation runs no parsing, reflection or expression text
	public sealed class CompiledRule
	{
		private readonly Dictionary<string, RuleNode> m_nodes;
		private readonly Dictionary<string, Func<RuleFacts, HashSet<string>, ValueTask<bool>>> m_conditions;
		private readonly Dictionary<string, List<RuleEdge>> m_edges;
		private readonly string m_trigger;

		private CompiledRule(Int64 ruleID, int version, string mode, RuleDocument document, Dictionary<string, Func<RuleFacts, HashSet<string>, ValueTask<bool>>> conditions)
		{
			RuleID = ruleID;
			Version = version;
			Mode = mode;
			Trigger = RuleDocuments.TriggerOf(document);
			m_nodes = document.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
			m_conditions = conditions;
			m_edges = document.Edges.GroupBy(e => e.From, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
			m_trigger = document.Nodes.First(n => n.Kind == RuleNodeKinds.Trigger).Id;
		}

		public Int64 RuleID { get; }
		public int Version { get; }
		public string Mode { get; }
		public string Trigger { get; }

		// The actor of every action this version takes
		public Database.ModerationActor Actor => new Database.ModerationActor
		{
			Kind = AuditActorKinds.Automation,
			UserID = null,
			Name = $"rule:{RuleID}@v{Version}",
			Source = ModerationSources.AutomatedRules
		};

		// A valid document only. The device values are hashed here, so DeviceHasher must be initialized.
		public static CompiledRule Compile(Int64 ruleID, int version, string mode, RuleDocument document)
		{
			var conditions = new Dictionary<string, Func<RuleFacts, HashSet<string>, ValueTask<bool>>>(StringComparer.Ordinal);
			foreach (RuleNode node in document.Nodes.Where(n => n.Kind == RuleNodeKinds.Condition))
			{
				conditions[node.Id] = RuleConditions.Compile(node.Query!);
			}
			return new CompiledRule(ruleID, version, mode, document, conditions);
		}

		// Runs from the trigger through every reached condition once and returns the actions at the end of the taken branches
		public async Task<RuleResult> RunAsync(RuleFacts facts)
		{
			var read = new HashSet<string>(StringComparer.Ordinal);
			var actions = new List<RuleNode>();
			var visited = new HashSet<string>(StringComparer.Ordinal) { m_trigger };
			var pending = new Queue<string>([m_trigger]);
			while (pending.Count > 0)
			{
				string id = pending.Dequeue();
				RuleNode node = m_nodes[id];
				string? output = null;
				if (node.Kind == RuleNodeKinds.Action)
				{
					actions.Add(node);
					continue;
				}
				if (node.Kind == RuleNodeKinds.Condition)
				{
					output = await m_conditions[id](facts, read) ? RuleOutputs.True : RuleOutputs.False;
				}
				foreach (RuleEdge edge in m_edges.GetValueOrDefault(id) ?? [])
				{
					if (edge.Output == output && visited.Add(edge.To))
					{
						pending.Enqueue(edge.To);
					}
				}
			}
			return new RuleResult(actions, read.ToList());
		}
	}

	// A query tree as nested delegates, every leaf test built with its parsed constants
	public static class RuleConditions
	{
		public static Func<RuleFacts, HashSet<string>, ValueTask<bool>> Compile(RuleQuery query)
		{
			if (query.Field == null)
			{
				Func<RuleFacts, HashSet<string>, ValueTask<bool>>[] parts = query.Rules!.Select(Compile).ToArray();
				bool all = query.Match == RuleGroupMatches.All;
				return async (facts, read) =>
				{
					foreach (Func<RuleFacts, HashSet<string>, ValueTask<bool>> part in parts)
					{
						if (await part(facts, read) != all)
						{
							return !all;
						}
					}
					return all;
				};
			}

			RuleField field = RuleFields.Find(query.Field)!;
			Func<object?[], bool> test = Test(field.Type, query.Operator!, query.Value);
			return async (facts, read) =>
			{
				read.Add(field.Key);
				return test(await facts.GetAsync(field));
			};
		}

		private static Func<object?[], bool> Test(string type, string op, JsonElement? value)
		{
			if (op == RuleOperators.IsEmpty)
			{
				return values => values.Length == 0;
			}
			return type switch
			{
				RuleFieldTypes.Number => NumberTest(op, value!.Value),
				RuleFieldTypes.Bool => BoolTest(value!.Value),
				RuleFieldTypes.Ip => IpTest(op, value!.Value),
				RuleFieldTypes.Device => DeviceTest(op, value!.Value),
				_ => TextTest(op, value!.Value)
			};
		}

		private static IEnumerable<JsonElement> Items(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [value];

		private static Func<object?[], bool> TextTest(string op, JsonElement value)
		{
			var set = new HashSet<string>(Items(value).Select(v => v.GetString()!.Trim()), StringComparer.OrdinalIgnoreCase);
			string one = set.First();
			IEnumerable<string> Texts(object?[] values) => values.OfType<string>();
			return op switch
			{
				RuleOperators.Equals or RuleOperators.InList => values => Texts(values).Any(set.Contains),
				RuleOperators.NotEquals => values => !Texts(values).Any(set.Contains),
				RuleOperators.Contains => values => Texts(values).Any(v => v.Contains(one, StringComparison.OrdinalIgnoreCase)),
				RuleOperators.StartsWith => values => Texts(values).Any(v => v.StartsWith(one, StringComparison.OrdinalIgnoreCase)),
				_ => throw new ArgumentOutOfRangeException(nameof(op), op, "not a text operator")
			};
		}

		private static Func<object?[], bool> NumberTest(string op, JsonElement value)
		{
			double[] numbers = Items(value).Select(v => v.GetDouble()).ToArray();
			IEnumerable<double> Numbers(object?[] values) => values.OfType<double>();
			return op switch
			{
				RuleOperators.Equals or RuleOperators.InList => values => Numbers(values).Any(n => numbers.Contains(n)),
				RuleOperators.NotEquals => values => !Numbers(values).Any(n => numbers.Contains(n)),
				RuleOperators.GreaterThan => values => Numbers(values).Any(n => n > numbers[0]),
				RuleOperators.LessThan => values => Numbers(values).Any(n => n < numbers[0]),
				RuleOperators.Between => values => Numbers(values).Any(n => n >= numbers[0] && n <= numbers[1]),
				_ => throw new ArgumentOutOfRangeException(nameof(op), op, "not a number operator")
			};
		}

		private static Func<object?[], bool> BoolTest(JsonElement value)
		{
			bool wanted = value.GetBoolean();
			return values => values.OfType<bool>().Any(b => b == wanted);
		}

		// An IPv4 address mapped into IPv6 compares as the IPv4 address
		private static IPAddress? Address(string? text)
		{
			if (!IPAddress.TryParse(text?.Trim(), out IPAddress? address))
			{
				return null;
			}
			return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
		}

		private static Func<object?[], bool> IpTest(string op, JsonElement value)
		{
			IEnumerable<IPAddress> Addresses(object?[] values) => values.OfType<string>().Select(Address).OfType<IPAddress>();
			switch (op)
			{
				case RuleOperators.Cidr:
					IPNetwork network = IPNetwork.Parse(value.GetString()!.Trim());
					return values => Addresses(values).Any(network.Contains);
				case RuleOperators.StartsWith:
					string prefix = value.GetString()!.Trim();
					return values => values.OfType<string>().Any(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
				default:
					HashSet<IPAddress> set = Items(value).Select(v => Address(v.GetString())).OfType<IPAddress>().ToHashSet();
					bool not = op == RuleOperators.NotEquals;
					return values => Addresses(values).Any(set.Contains) != not;
			}
		}

		// A raw value as staff copy it, in its own, upper and lower case, or a whole device key from the investigation page
		public static IEnumerable<string> DeviceKeysOf(string raw)
		{
			string text = raw.Trim();
			if (text.Length == Device.ValueHmacLength * 2 && text.All(Uri.IsHexDigit))
			{
				yield return text.ToLowerInvariant();
			}
			foreach (string form in new[] { text, text.ToUpperInvariant(), text.ToLowerInvariant() }.Distinct(StringComparer.Ordinal))
			{
				yield return Database.StaffInspection.KeyOf(DeviceHasher.HashRaw(form));
			}
		}

		private static Func<object?[], bool> DeviceTest(string op, JsonElement value)
		{
			var keys = new HashSet<string>(Items(value).SelectMany(v => DeviceKeysOf(v.GetString()!)), StringComparer.Ordinal);
			bool not = op == RuleOperators.NotEquals;
			return values => values.OfType<string>().Any(keys.Contains) != not;
		}
	}
}
