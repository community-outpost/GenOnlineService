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

using System.Globalization;
using System.Text.Json;

namespace GenOnlineService.Portal.Client.Rules
{
	// One line of the outline: how deep it sits, its words, and the node it describes when it starts one
	public sealed record RuleOutlineLine(int Depth, string Text, string? NodeId);

	// One line of the difference of two outlines: ' ' in both, '-' only in the older one, '+' only in the newer one
	public sealed record RuleOutlineChange(char Kind, string Line);

	// Every rule graph as a readable outline for review, for the difference of two versions, and for staff who use
	// a keyboard or a screen reader instead of the canvas. It walks the graph from the trigger, branch by branch.
	public static class RuleOutline
	{
		public static List<RuleOutlineLine> Lines(PortalRuleDocument document, PortalRuleCatalog? catalog = null)
		{
			var lines = new List<RuleOutlineLine>();
			PortalRuleNode? trigger = document.Nodes.FirstOrDefault(n => n.Kind == PortalRuleWords.Trigger);
			if (trigger == null)
			{
				lines.Add(new RuleOutlineLine(0, "No trigger: the rule never runs", null));
				return lines;
			}

			var byID = document.Nodes.GroupBy(n => n.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
			var written = new HashSet<string>(StringComparer.Ordinal) { trigger.Id };
			lines.Add(new RuleOutlineLine(0, "When " + PortalRuleWords.TriggerSentence(trigger.Trigger), trigger.Id));
			Branch(document, byID, catalog, Targets(document, trigger.Id, null), 1, written, lines);

			// what the trigger never reaches still shows, so nothing on the canvas is missing from the outline
			foreach (PortalRuleNode node in document.Nodes.Where(n => !written.Contains(n.Id)).ToList())
			{
				lines.Add(new RuleOutlineLine(0, "Not reached from the trigger:", null));
				Node(document, byID, catalog, node, 1, written, lines);
			}
			return lines;
		}

		// The outline as plain text, two spaces a level
		public static string Text(IEnumerable<RuleOutlineLine> lines) =>
			String.Join("\n", lines.Select(l => new string(' ', l.Depth * 2) + l.Text));

		// The query of a condition node on one line, short enough for the node on the canvas
		public static string Summary(PortalRuleQuery? query, PortalRuleCatalog? catalog = null)
		{
			if (query == null)
			{
				return "No query";
			}
			if (!query.IsGroup)
			{
				return Condition(query, catalog);
			}
			List<PortalRuleQuery> rules = query.Rules ?? [];
			if (rules.Count == 0)
			{
				return "No conditions";
			}
			if (rules.Count == 1)
			{
				return Summary(rules[0], catalog);
			}
			string first = rules[0].IsGroup ? GroupWord(rules[0].Match) + " of " + (rules[0].Rules?.Count ?? 0) : Condition(rules[0], catalog);
			return (query.Match == PortalRuleWords.Any ? "Any" : "All") + " of " + rules.Count + ": " + first + ", …";
		}

		// One condition in words, such as: Account age in days is less than 7
		public static string Condition(PortalRuleQuery condition, PortalRuleCatalog? catalog = null)
		{
			string field = catalog?.Find(condition.Field)?.Label ?? condition.Field ?? "?";
			string op = PortalRuleWords.OperatorLabel(condition.Operator);
			string value = ValueText(condition.Operator, condition.Value);
			return value.Length == 0 ? field + " " + op : field + " " + op + " " + value;
		}

		// An action in words with its length and reason
		public static string Action(PortalRuleNode action)
		{
			string what = action.Action switch
			{
				PortalRuleWords.Mute => "Mute for " + Length(action.DurationS),
				PortalRuleWords.Ban => action.DurationS == null ? "Ban permanently" : "Ban for " + Length(action.DurationS),
				_ => PortalRuleWords.ActionLabel(action.Action)
			};
			return String.IsNullOrWhiteSpace(action.Reason) ? what : what + ", reason: " + action.Reason;
		}

		// A length in seconds in the largest whole unit: 7 days, 3 hours, 30 minutes
		public static string Length(long? seconds)
		{
			if (seconds is not long s || s <= 0)
			{
				return "no length";
			}
			(long size, string unit)[] units = [(86400, "day"), (3600, "hour"), (60, "minute"), (1, "second")];
			foreach ((long size, string unit) in units)
			{
				if (s % size == 0)
				{
					long n = s / size;
					return n.ToString(CultureInfo.InvariantCulture) + " " + unit + (n == 1 ? "" : "s");
				}
			}
			return s.ToString(CultureInfo.InvariantCulture) + " seconds";
		}

		public static string ValueText(string? op, JsonElement? value)
		{
			if (op == PortalRuleWords.IsEmpty || value is not JsonElement v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
			{
				return "";
			}
			if (op == PortalRuleWords.Between && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 2)
			{
				return Scalar(v[0]) + " and " + Scalar(v[1]);
			}
			return v.ValueKind == JsonValueKind.Array ? String.Join(", ", v.EnumerateArray().Select(Scalar)) : Scalar(v);
		}

		// The lines of two outlines side by side as one list, by their longest common run of lines
		public static List<RuleOutlineChange> Diff(IReadOnlyList<string> older, IReadOnlyList<string> newer)
		{
			int[,] common = new int[older.Count + 1, newer.Count + 1];
			for (int i = older.Count - 1; i >= 0; i--)
			{
				for (int j = newer.Count - 1; j >= 0; j--)
				{
					common[i, j] = older[i] == newer[j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
				}
			}
			var changes = new List<RuleOutlineChange>();
			int a = 0, b = 0;
			while (a < older.Count && b < newer.Count)
			{
				if (older[a] == newer[b])
				{
					changes.Add(new RuleOutlineChange(' ', older[a]));
					a++;
					b++;
				}
				else if (common[a + 1, b] >= common[a, b + 1])
				{
					changes.Add(new RuleOutlineChange('-', older[a++]));
				}
				else
				{
					changes.Add(new RuleOutlineChange('+', newer[b++]));
				}
			}
			while (a < older.Count)
			{
				changes.Add(new RuleOutlineChange('-', older[a++]));
			}
			while (b < newer.Count)
			{
				changes.Add(new RuleOutlineChange('+', newer[b++]));
			}
			return changes;
		}

		public static List<RuleOutlineChange> Diff(PortalRuleDocument older, PortalRuleDocument newer, PortalRuleCatalog? catalog = null) =>
			Diff(Text(Lines(older, catalog)).Split('\n'), Text(Lines(newer, catalog)).Split('\n'));

		private static void Branch(PortalRuleDocument document, Dictionary<string, PortalRuleNode> byID, PortalRuleCatalog? catalog, List<string> targets, int depth,
			HashSet<string> written, List<RuleOutlineLine> lines)
		{
			if (targets.Count == 0)
			{
				lines.Add(new RuleOutlineLine(depth, "Nothing happens", null));
				return;
			}
			foreach (string id in targets)
			{
				if (!byID.TryGetValue(id, out PortalRuleNode? node))
				{
					lines.Add(new RuleOutlineLine(depth, "A missing node: " + id, null));
				}
				else if (!written.Add(id))
				{
					// a node reached a second time is written once, the later branch points at it
					lines.Add(new RuleOutlineLine(depth, "Continue at " + Name(node, catalog) + ", described above", id));
				}
				else
				{
					Node(document, byID, catalog, node, depth, written, lines);
				}
			}
		}

		private static void Node(PortalRuleDocument document, Dictionary<string, PortalRuleNode> byID, PortalRuleCatalog? catalog, PortalRuleNode node, int depth,
			HashSet<string> written, List<RuleOutlineLine> lines)
		{
			written.Add(node.Id);
			switch (node.Kind)
			{
				case PortalRuleWords.Condition:
					PortalRuleQuery query = node.Query ?? PortalRuleQuery.Group(PortalRuleWords.All);
					if (query.IsGroup)
					{
						lines.Add(new RuleOutlineLine(depth, "Check that " + (query.Match == PortalRuleWords.Any ? "any of these holds:" : "all of these hold:"), node.Id));
						Query(query.Rules ?? [], catalog, depth + 1, lines);
					}
					else
					{
						lines.Add(new RuleOutlineLine(depth, "Check that " + Condition(query, catalog), node.Id));
					}
					lines.Add(new RuleOutlineLine(depth, "If true:", null));
					Branch(document, byID, catalog, Targets(document, node.Id, PortalRuleWords.OutputTrue), depth + 1, written, lines);
					lines.Add(new RuleOutlineLine(depth, "If false:", null));
					Branch(document, byID, catalog, Targets(document, node.Id, PortalRuleWords.OutputFalse), depth + 1, written, lines);
					break;
				case PortalRuleWords.Action:
					lines.Add(new RuleOutlineLine(depth, Action(node), node.Id));
					break;
				default:
					lines.Add(new RuleOutlineLine(depth, "A " + node.Kind + " node", node.Id));
					break;
			}
		}

		private static void Query(List<PortalRuleQuery> rules, PortalRuleCatalog? catalog, int depth, List<RuleOutlineLine> lines)
		{
			foreach (PortalRuleQuery rule in rules)
			{
				if (rule.IsGroup)
				{
					lines.Add(new RuleOutlineLine(depth, GroupWord(rule.Match) + " of:", null));
					Query(rule.Rules ?? [], catalog, depth + 1, lines);
				}
				else
				{
					lines.Add(new RuleOutlineLine(depth, Condition(rule, catalog), null));
				}
			}
		}

		// The nodes an output leads to, in the order of the edges
		public static List<string> Targets(PortalRuleDocument document, string from, string? output) =>
			document.Edges.Where(e => e.From == from && (e.Output ?? "") == (output ?? "")).Select(e => e.To).ToList();

		private static string Name(PortalRuleNode node, PortalRuleCatalog? catalog) => node.Kind switch
		{
			PortalRuleWords.Condition => "the check that " + Summary(node.Query, catalog),
			PortalRuleWords.Action => Action(node),
			_ => node.Id
		};

		private static string GroupWord(string? match) => match == PortalRuleWords.Any ? "Any" : "All";

		private static string Scalar(JsonElement value) => value.ValueKind switch
		{
			JsonValueKind.String => "\"" + value.GetString() + "\"",
			JsonValueKind.Number => value.GetRawText(),
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			_ => value.GetRawText()
		};
	}
}
