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

using Bit.Blow;

namespace GenOnlineService.Portal.Client.Rules
{
	// The one place the rule document meets the canvas library. The persisted format stays the host's rule document, the
	// canvas only draws it, so another library replaces this class and the node template alone.
	public static class RuleGraph
	{
		// Every handle carries the flow of a rule, typed so the canvas draws and checks them as one kind of port
		public const string FlowType = "rule-flow";

		// Where a node without a position goes: one column per step from the trigger, one row per node in the column
		public const double ColumnWidth = 280;
		public const double RowHeight = 110;

		public static (List<BlowNode> Nodes, List<BlowEdge> Edges) ToGraph(PortalRuleDocument document)
		{
			Dictionary<string, (int Column, int Row)> places = Places(document);
			var nodes = new List<BlowNode>();
			foreach (PortalRuleNode node in document.Nodes)
			{
				(int column, int row) = places.GetValueOrDefault(node.Id);
				BlowXYPosition position = node.Position is PortalRulePosition at ? new BlowXYPosition(at.X, at.Y) : new BlowXYPosition(column * ColumnWidth, row * RowHeight);
				nodes.Add(NodeOf(Copy(node), position));
			}
			return (nodes, document.Edges.Select(EdgeOf).ToList());
		}

		public static PortalRuleDocument ToDocument(IEnumerable<BlowNode> nodes, IEnumerable<BlowEdge> edges)
		{
			var document = new PortalRuleDocument();
			var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (BlowNode node in nodes)
			{
				PortalRuleNode rule = Copy(RuleOf(node));
				rule.Id = node.Id;
				rule.Position = new PortalRulePosition { X = Math.Round(node.Position.X), Y = Math.Round(node.Position.Y) };
				kinds[rule.Id] = rule.Kind;
				document.Nodes.Add(rule);
			}
			foreach (BlowEdge edge in edges)
			{
				// only a condition names its output, as the host requires
				string? output = kinds.GetValueOrDefault(edge.Source) == PortalRuleWords.Condition ? edge.SourceHandle : null;
				document.Edges.Add(new PortalRuleEdge { From = edge.Source, To = edge.Target, Output = output });
			}
			return document;
		}

		public static BlowNode NodeOf(PortalRuleNode rule, BlowXYPosition position) => new BlowNode
		{
			Id = rule.Id,
			Type = rule.Kind,
			Position = position,
			Data = rule,
			Ports = PortsOf(rule.Kind),
			SourcePosition = BlowPosition.Right,
			TargetPosition = BlowPosition.Left,
			// the rule needs its trigger, so only the panel's trigger choice changes it
			Deletable = rule.Kind != PortalRuleWords.Trigger
		};

		public static BlowEdge EdgeOf(PortalRuleEdge edge) => new BlowEdge
		{
			Id = EdgeId(edge.From, edge.Output, edge.To),
			Source = edge.From,
			Target = edge.To,
			SourceHandle = edge.Output,
			Label = edge.Output,
			Class = edge.Output == PortalRuleWords.OutputFalse ? "go-rule-edge-false" : null,
			MarkerEnd = true
		};

		public static string EdgeId(string from, string? output, string to) => from + ":" + (output ?? "out") + ">" + to;

		// The handles of each kind: a trigger only starts the flow, a condition takes it in and splits it into true and
		// false, an action ends its branch
		public static List<BlowPort> PortsOf(string kind) => kind switch
		{
			PortalRuleWords.Trigger => [new BlowPort { Type = BlowHandleType.Source, DataType = FlowType, Label = "Then" }],
			PortalRuleWords.Condition =>
			[
				new BlowPort { Type = BlowHandleType.Target, DataType = FlowType, Label = "In" },
				new BlowPort { Id = PortalRuleWords.OutputTrue, Type = BlowHandleType.Source, DataType = FlowType, Label = "True" },
				new BlowPort { Id = PortalRuleWords.OutputFalse, Type = BlowHandleType.Source, DataType = FlowType, Label = "False" }
			],
			PortalRuleWords.Action => [new BlowPort { Type = BlowHandleType.Target, DataType = FlowType, Label = "In" }],
			_ => []
		};

		public static PortalRuleNode RuleOf(BlowNode node) => node.Data as PortalRuleNode ?? new PortalRuleNode { Id = node.Id, Kind = node.Type ?? "" };

		// The rules of a connection, for the canvas while staff drag and for the panel's connection lists: strict source to
		// target, no duplicate, no loop, and the kinds of the two nodes
		public static BlowConnectionPolicy PolicyFor(IReadOnlyList<BlowNode> nodes) => new BlowConnectionPolicy
		{
			Mode = BlowConnectionMode.Strict,
			PreventDuplicates = true,
			PreventCycles = true,
			IsValidConnection = connection => IsAllowed(nodes, connection)
		};

		// The kinds a connection may join: from the trigger or a condition output, to a condition or an action
		public static bool IsAllowed(IReadOnlyList<BlowNode> nodes, BlowConnection connection)
		{
			BlowNode? source = nodes.FirstOrDefault(n => n.Id == connection.Source);
			BlowNode? target = nodes.FirstOrDefault(n => n.Id == connection.Target);
			if (source == null || target == null || source.Id == target.Id)
			{
				return false;
			}
			bool fromOk = source.Type switch
			{
				PortalRuleWords.Trigger => String.IsNullOrEmpty(connection.SourceHandle),
				PortalRuleWords.Condition => connection.SourceHandle is PortalRuleWords.OutputTrue or PortalRuleWords.OutputFalse,
				_ => false
			};
			return fromOk && target.Type is PortalRuleWords.Condition or PortalRuleWords.Action && String.IsNullOrEmpty(connection.TargetHandle);
		}

		public static BlowConnectionCheck Check(IReadOnlyList<BlowNode> nodes, IReadOnlyList<BlowEdge> edges, string from, string? output, string to) =>
			BlowConnectionRules.Check(nodes, edges, new BlowConnection(from, to, output, null), PolicyFor(nodes));

		// A node id no other node uses, such as condition-2
		public static string NewId(IEnumerable<string> taken, string kind)
		{
			var used = new HashSet<string>(taken, StringComparer.Ordinal);
			for (int n = 1; ; n++)
			{
				string id = kind + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
				if (!used.Contains(id))
				{
					return id;
				}
			}
		}

		private static PortalRuleNode Copy(PortalRuleNode node) =>
			System.Text.Json.JsonSerializer.Deserialize<PortalRuleNode>(System.Text.Json.JsonSerializer.Serialize(node, PortalJson.Options), PortalJson.Options)!;

		// Columns by the longest path from the trigger, rows in document order within a column
		private static Dictionary<string, (int Column, int Row)> Places(PortalRuleDocument document)
		{
			var depth = new Dictionary<string, int>(StringComparer.Ordinal);
			string? trigger = document.Nodes.FirstOrDefault(n => n.Kind == PortalRuleWords.Trigger)?.Id;
			if (trigger != null)
			{
				depth[trigger] = 0;
				var queue = new Queue<string>([trigger]);
				int guard = document.Nodes.Count * Math.Max(1, document.Edges.Count) + 1;
				while (queue.Count > 0 && guard-- > 0)
				{
					string id = queue.Dequeue();
					foreach (PortalRuleEdge edge in document.Edges.Where(e => e.From == id))
					{
						int next = depth[id] + 1;
						if (next <= document.Nodes.Count && (!depth.TryGetValue(edge.To, out int known) || known < next))
						{
							depth[edge.To] = next;
							queue.Enqueue(edge.To);
						}
					}
				}
			}
			var rows = new Dictionary<int, int>();
			var places = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
			int last = depth.Count == 0 ? 0 : depth.Values.Max() + 1;
			foreach (PortalRuleNode node in document.Nodes)
			{
				int column = depth.TryGetValue(node.Id, out int d) ? d : last;
				int row = rows.GetValueOrDefault(column);
				rows[column] = row + 1;
				places[node.Id] = (column, row);
			}
			return places;
		}
	}
}
