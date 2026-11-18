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
using System.Text.RegularExpressions;

namespace GenOnlineService.Portal.Client.Rules
{
	// The value of a condition as the query builder types it, and back to the JSON value the host checks
	public static class RuleValues
	{
		// The text of a scalar value, or of a list with one value a line
		public static string Text(JsonElement? value)
		{
			if (value is not JsonElement v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
			{
				return "";
			}
			return v.ValueKind switch
			{
				JsonValueKind.Array => String.Join("\n", v.EnumerateArray().Select(Scalar)),
				_ => Scalar(v)
			};
		}

		// The two ends of a between value
		public static (string Low, string High) Range(JsonElement? value) =>
			value is JsonElement v && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 2 ? (Scalar(v[0]), Scalar(v[1])) : ("", "");

		// The JSON value of typed text for a field type and operator, or null with a problem when the text does not fit.
		// A list takes one value a line.
		public static JsonElement? Parse(string type, string op, string text, out string? problem)
		{
			problem = null;
			if (op == PortalRuleWords.IsEmpty)
			{
				return null;
			}
			if (op == PortalRuleWords.InList)
			{
				var items = new List<object>();
				foreach (string line in text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
				{
					if (!TryScalar(type, line, out object? item, out problem))
					{
						return null;
					}
					items.Add(item!);
				}
				if (items.Count == 0)
				{
					problem = "Type at least one value, one a line.";
					return null;
				}
				return JsonSerializer.SerializeToElement(items);
			}
			return TryScalar(type, text.Trim(), out object? value, out problem) ? JsonSerializer.SerializeToElement(value) : null;
		}

		public static JsonElement? ParseRange(string low, string high, out string? problem)
		{
			problem = null;
			if (!Double.TryParse(low.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double from) || !Double.TryParse(high.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double to))
			{
				problem = "Type two numbers.";
				return null;
			}
			if (from > to)
			{
				problem = "The first number is the lower one.";
				return null;
			}
			return JsonSerializer.SerializeToElement(new[] { from, to });
		}

		// What a new condition of a field starts with: its first operator and, for a yes or no field, true
		public static JsonElement? Default(string type, string op) =>
			type == "bool" && op == "equals" ? JsonSerializer.SerializeToElement(true) : null;

		private static bool TryScalar(string type, string text, out object? value, out string? problem)
		{
			value = null;
			problem = null;
			switch (type)
			{
				case "number":
					if (Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
					{
						value = number;
						return true;
					}
					problem = "Type a number.";
					return false;
				case "bool":
					if (text is "true" or "false")
					{
						value = text == "true";
						return true;
					}
					problem = "Choose true or false.";
					return false;
				default:
					if (text.Length == 0)
					{
						problem = "Type a value.";
						return false;
					}
					value = text;
					return true;
			}
		}

		private static string Scalar(JsonElement value) => value.ValueKind switch
		{
			JsonValueKind.String => value.GetString() ?? "",
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			_ => value.GetRawText()
		};
	}

	// A problem of a saved document as the editor places it: on the node its path names, at the path inside that node
	public sealed record RuleNodeProblem(string? NodeId, string Path, string Reason);

	public static class RuleProblems
	{
		private static readonly Regex s_nodePath = new Regex(@"^nodes\[(\d+)\](?:\.(.*))?$", RegexOptions.CultureInvariant);
		private static readonly Regex s_edgePath = new Regex(@"^edges\[(\d+)\](?:\.(.*))?$", RegexOptions.CultureInvariant);

		// nodes[2].query.rules[0].value names the third node of the document that was sent, at query.rules[0].value. An
		// edge's problem goes to the node it starts from, and anything else belongs to the whole rule
		public static List<RuleNodeProblem> Place(IEnumerable<PortalRuleProblem> problems, PortalRuleDocument sent)
		{
			var placed = new List<RuleNodeProblem>();
			foreach (PortalRuleProblem problem in problems)
			{
				Match node = s_nodePath.Match(problem.Path);
				Match edge = s_edgePath.Match(problem.Path);
				if (node.Success && Int32.TryParse(node.Groups[1].Value, out int n) && n < sent.Nodes.Count)
				{
					placed.Add(new RuleNodeProblem(sent.Nodes[n].Id, node.Groups[2].Value, problem.Reason));
				}
				else if (edge.Success && Int32.TryParse(edge.Groups[1].Value, out int e) && e < sent.Edges.Count)
				{
					placed.Add(new RuleNodeProblem(sent.Edges[e].From, "edge to " + sent.Edges[e].To, problem.Reason));
				}
				else
				{
					placed.Add(new RuleNodeProblem(null, problem.Path, problem.Reason));
				}
			}
			return placed;
		}

		// The problems at a path of a node's query and below it, such as query.rules[1] for that condition
		public static IEnumerable<RuleNodeProblem> At(IEnumerable<RuleNodeProblem> problems, string nodeId, string path, bool below = true) =>
			problems.Where(p => p.NodeId == nodeId && (p.Path == path || below && p.Path.StartsWith(path + ".", StringComparison.Ordinal)));

		// The words of a path inside a node, for a problem the panel shows under the node
		public static string Where(string path) => path switch
		{
			"" => "This node",
			"trigger" => "The trigger",
			"action" => "The action",
			"reason" => "The reason",
			"duration_s" => "The length",
			"query" => "The query",
			"id" => "The node id",
			"kind" => "The node kind",
			_ when path.StartsWith("edge to ", StringComparison.Ordinal) => "The connection " + path.Substring(5),
			_ when path.EndsWith(".value", StringComparison.Ordinal) => "A value",
			_ when path.EndsWith(".operator", StringComparison.Ordinal) => "An operator",
			_ when path.EndsWith(".field", StringComparison.Ordinal) => "A field",
			_ => "Part of the query"
		};
	}
}
