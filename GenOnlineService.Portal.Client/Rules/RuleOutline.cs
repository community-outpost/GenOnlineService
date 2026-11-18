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
	// One line of the outline: how deep it sits, its expression, the same line in words for screen readers, and the node
	// it describes when it starts one
	public sealed record RuleOutlineLine(int Depth, string Text, string? NodeId, string? Spoken = null)
	{
		public string Words => Spoken ?? Text;
	}

	// One line of the difference of two outlines: ' ' in both, '-' only in the older one, '+' only in the newer one. The
	// words read the line to a screen reader when the outline gave them.
	public sealed record RuleOutlineChange(char Kind, string Line, string? Spoken = null);

	// Every rule graph as a readable outline for review, for the difference of two versions, and for staff who use a
	// keyboard or a screen reader instead of the canvas. It walks the graph from the trigger, branch by branch, and
	// writes each condition node as one CEL style expression over the field keys, such as
	// user.age_days < 7 && (!user.email_confirmed || host.mac_virtual). Each line also carries the same in words.
	public static class RuleOutline
	{
		public static List<RuleOutlineLine> Lines(PortalRuleDocument document, PortalRuleCatalog? catalog = null)
		{
			var lines = new List<RuleOutlineLine>();
			PortalRuleNode? trigger = document.Nodes.FirstOrDefault(n => n.Kind == PortalRuleWords.Trigger);
			if (trigger == null)
			{
				lines.Add(new RuleOutlineLine(0, "never", null, "No trigger: the rule never runs"));
				return lines;
			}

			var byID = document.Nodes.GroupBy(n => n.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
			var written = new HashSet<string>(StringComparer.Ordinal) { trigger.Id };
			lines.Add(new RuleOutlineLine(0, "on event.action == " + Quote(trigger.Trigger ?? ""), trigger.Id, "When " + PortalRuleWords.TriggerSentence(trigger.Trigger)));
			Branch(document, byID, catalog, Targets(document, trigger.Id, null), 1, written, lines);

			// what the trigger never reaches still shows, so nothing on the canvas is missing from the outline
			foreach (PortalRuleNode node in document.Nodes.Where(n => !written.Contains(n.Id)).ToList())
			{
				lines.Add(new RuleOutlineLine(0, "unreached:", null, "Not reached from the trigger:"));
				Node(document, byID, catalog, node, 1, written, lines);
			}
			return lines;
		}

		// The outline as plain text, two spaces a level
		public static string Text(IEnumerable<RuleOutlineLine> lines) =>
			String.Join("\n", lines.Select(l => Indent(l) + l.Text));

		private static string Indent(RuleOutlineLine line) => new string(' ', line.Depth * 2);

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

		// One condition in words, such as: Account age in days is less than 7, or Email is not confirmed
		public static string Condition(PortalRuleQuery condition, PortalRuleCatalog? catalog = null)
		{
			PortalRuleField? known = catalog?.Find(condition.Field);
			if (known is { TrueLabel: not null, FalseLabel: not null } && condition.Operator == "equals" && condition.Value is JsonElement state && state.ValueKind is JsonValueKind.True or JsonValueKind.False)
			{
				return state.ValueKind == JsonValueKind.True ? known.TrueLabel : known.FalseLabel;
			}
			string field = known?.Label ?? condition.Field ?? "?";
			string op = PortalRuleWords.OperatorLabel(condition.Operator);
			string value = ValueText(condition.Operator, condition.Value);
			return value.Length == 0 ? field + " " + op : field + " " + op + " " + value;
		}

		// A query in words on one line: the top group joined by and or or, a nested group as either or both
		public static string Words(PortalRuleQuery? query, PortalRuleCatalog? catalog = null) => WordsOf(query ?? PortalRuleQuery.Group(PortalRuleWords.All), catalog, true);

		private static string WordsOf(PortalRuleQuery query, PortalRuleCatalog? catalog, bool top)
		{
			if (!query.IsGroup)
			{
				return Condition(query, catalog);
			}
			List<string> parts = (query.Rules ?? []).Select(r => WordsOf(r, catalog, false)).ToList();
			bool any = query.Match == PortalRuleWords.Any;
			if (parts.Count == 0)
			{
				return any ? "nothing holds" : "always";
			}
			if (parts.Count == 1)
			{
				return parts[0];
			}
			string last = (any ? " or " : " and ") + parts[^1];
			string joined = String.Join(", ", parts.Take(parts.Count - 1)) + last;
			if (top)
			{
				return any ? String.Join(" or ", parts) : String.Join(" and ", parts);
			}
			return any ? "either " + joined : parts.Count == 2 ? "both " + joined : "all of " + joined;
		}

		// A query as one CEL style expression: && and || between the rules, a nested group in parentheses
		public static string Expression(PortalRuleQuery? query) => ExpressionOf(query ?? PortalRuleQuery.Group(PortalRuleWords.All), true);

		private static string ExpressionOf(PortalRuleQuery query, bool top)
		{
			if (!query.IsGroup)
			{
				return ConditionExpression(query);
			}
			List<PortalRuleQuery> rules = query.Rules ?? [];
			bool any = query.Match == PortalRuleWords.Any;
			if (rules.Count == 0)
			{
				return any ? "false" : "true";
			}
			if (rules.Count == 1)
			{
				return ExpressionOf(rules[0], top);
			}
			string joined = String.Join(any ? " || " : " && ", rules.Select(r => ExpressionOf(r, false)));
			return top ? joined : "(" + joined + ")";
		}

		// One condition as an expression, such as user.age_days < 7, !user.email_confirmed or host.id in ["AB12"]
		public static string ConditionExpression(PortalRuleQuery condition)
		{
			string field = String.IsNullOrEmpty(condition.Field) ? "?" : condition.Field;
			JsonElement? value = condition.Value is JsonElement v && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;
			switch (condition.Operator)
			{
				case "equals" when value?.ValueKind == JsonValueKind.True:
					return field;
				case "equals" when value?.ValueKind == JsonValueKind.False:
					return "!" + field;
				case "equals":
					return field + " == " + Literal(value);
				case "not_equals":
					return field + " != " + Literal(value);
				case "greater_than":
					return field + " > " + Literal(value);
				case "less_than":
					return field + " < " + Literal(value);
				case PortalRuleWords.InList:
					return field + " in " + Literal(value);
				case "contains":
					return field + ".contains(" + Literal(value) + ")";
				case "starts_with":
					return field + ".startsWith(" + Literal(value) + ")";
				case PortalRuleWords.Between when value?.ValueKind == JsonValueKind.Array && value.Value.GetArrayLength() == 2:
					return "(" + Literal(value.Value[0]) + " <= " + field + " && " + field + " <= " + Literal(value.Value[1]) + ")";
				case PortalRuleWords.IsEmpty:
					return "size(" + field + ") == 0";
				case "cidr":
					return "cidr(" + Literal(value) + ").containsIP(" + field + ")";
				default:
					return field + " " + (condition.Operator ?? "?") + " " + Literal(value);
			}
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

		// An action as the outline writes it: then ban for 7d, reason "Ban evasion"
		public static string ActionExpression(PortalRuleNode action)
		{
			string what = action.Action switch
			{
				PortalRuleWords.Mute => "mute for " + ShortLength(action.DurationS),
				PortalRuleWords.Ban => action.DurationS == null ? "ban permanently" : "ban for " + ShortLength(action.DurationS),
				null or "" => "do nothing",
				_ => action.Action
			};
			return "then " + (String.IsNullOrWhiteSpace(action.Reason) ? what : what + ", reason " + Quote(action.Reason));
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

		// A length in seconds in the largest whole unit, short: 7d, 3h, 30m, 90s
		public static string ShortLength(long? seconds)
		{
			if (seconds is not long s || s <= 0)
			{
				return "?";
			}
			(long size, string unit)[] units = [(86400, "d"), (3600, "h"), (60, "m"), (1, "s")];
			(long size, string unit) fit = units.First(u => s % u.size == 0);
			return (s / fit.size).ToString(CultureInfo.InvariantCulture) + fit.unit;
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
		public static List<RuleOutlineChange> Diff(IReadOnlyList<string> older, IReadOnlyList<string> newer) =>
			Diff(older.Select(l => (l, (string?)null)).ToList(), newer.Select(l => (l, (string?)null)).ToList());

		// The difference of two documents by the expressions of their outlines, each line with its words
		public static List<RuleOutlineChange> Diff(PortalRuleDocument older, PortalRuleDocument newer, PortalRuleCatalog? catalog = null)
		{
			static List<(string, string?)> Of(List<RuleOutlineLine> lines) => lines.Select(l => (Indent(l) + l.Text, (string?)l.Words)).ToList();
			return Diff(Of(Lines(older, catalog)), Of(Lines(newer, catalog)));
		}

		private static List<RuleOutlineChange> Diff(IReadOnlyList<(string Line, string? Spoken)> older, IReadOnlyList<(string Line, string? Spoken)> newer)
		{
			int[,] common = new int[older.Count + 1, newer.Count + 1];
			for (int i = older.Count - 1; i >= 0; i--)
			{
				for (int j = newer.Count - 1; j >= 0; j--)
				{
					common[i, j] = older[i].Line == newer[j].Line ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
				}
			}
			var changes = new List<RuleOutlineChange>();
			int a = 0, b = 0;
			while (a < older.Count && b < newer.Count)
			{
				if (older[a].Line == newer[b].Line)
				{
					changes.Add(new RuleOutlineChange(' ', older[a].Line, older[a].Spoken));
					a++;
					b++;
				}
				else if (common[a + 1, b] >= common[a, b + 1])
				{
					changes.Add(new RuleOutlineChange('-', older[a].Line, older[a].Spoken));
					a++;
				}
				else
				{
					changes.Add(new RuleOutlineChange('+', newer[b].Line, newer[b].Spoken));
					b++;
				}
			}
			while (a < older.Count)
			{
				changes.Add(new RuleOutlineChange('-', older[a].Line, older[a].Spoken));
				a++;
			}
			while (b < newer.Count)
			{
				changes.Add(new RuleOutlineChange('+', newer[b].Line, newer[b].Spoken));
				b++;
			}
			return changes;
		}

		private static void Branch(PortalRuleDocument document, Dictionary<string, PortalRuleNode> byID, PortalRuleCatalog? catalog, List<string> targets, int depth,
			HashSet<string> written, List<RuleOutlineLine> lines)
		{
			if (targets.Count == 0)
			{
				lines.Add(new RuleOutlineLine(depth, "nothing", null, "Nothing happens"));
				return;
			}
			foreach (string id in targets)
			{
				if (!byID.TryGetValue(id, out PortalRuleNode? node))
				{
					lines.Add(new RuleOutlineLine(depth, "missing " + Quote(id), null, "A missing node: " + id));
				}
				else if (!written.Add(id))
				{
					// a node reached a second time is written once, the later branch points at it
					lines.Add(new RuleOutlineLine(depth, "see above: " + Head(node), id, "Continue at " + Name(node, catalog) + ", described above"));
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
					lines.Add(new RuleOutlineLine(depth, Head(node), node.Id, "If " + Words(node.Query, catalog)));
					Branch(document, byID, catalog, Targets(document, node.Id, PortalRuleWords.OutputTrue), depth + 1, written, lines);
					lines.Add(new RuleOutlineLine(depth, "else", null, "Otherwise"));
					Branch(document, byID, catalog, Targets(document, node.Id, PortalRuleWords.OutputFalse), depth + 1, written, lines);
					break;
				case PortalRuleWords.Action:
					lines.Add(new RuleOutlineLine(depth, ActionExpression(node), node.Id, Action(node)));
					break;
				default:
					lines.Add(new RuleOutlineLine(depth, node.Kind + " " + Quote(node.Id), node.Id, "A " + node.Kind + " node"));
					break;
			}
		}

		// The first line of a node as the outline writes it
		private static string Head(PortalRuleNode node) => node.Kind switch
		{
			PortalRuleWords.Condition => "if " + Expression(node.Query),
			PortalRuleWords.Action => ActionExpression(node),
			_ => node.Kind + " " + Quote(node.Id)
		};

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

		// A text in double quotes with its quotes and backslashes escaped
		private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

		// A value as the expression writes it: a quoted text, a number, true or false, a list in brackets
		private static string Literal(JsonElement? value) => value is not JsonElement v ? "?" : v.ValueKind switch
		{
			JsonValueKind.String => Quote(v.GetString() ?? ""),
			JsonValueKind.Array => "[" + String.Join(", ", v.EnumerateArray().Select(i => Literal(i))) + "]",
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			JsonValueKind.Null or JsonValueKind.Undefined => "?",
			_ => v.GetRawText()
		};

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
