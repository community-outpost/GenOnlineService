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
using GenOnlineService.Portal.Client;
using GenOnlineService.Portal.Client.Rules;
using GenOnlineService.Rules;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

// The staff editor of the automated rules, S2 and S6, without a browser: the rule document to the canvas graph and back,
// the handles that decide which connections the canvas allows, the outline and its difference, and where a problem of
// the host lands in the editor
public class RuleEditorTests
{
	// the example of docs/automated-rules.md, as the host stores it
	private const string Example = """
	{
	  "nodes": [
	    { "id": "t", "kind": "trigger", "event": { "action": "sign_in" } },
	    { "id": "c", "kind": "condition", "query": {
	        "match": "all",
	        "rules": [
	          { "field": "host.id", "operator": "in_list", "value": ["AB12-CD34-YY"] },
	          { "match": "any", "rules": [
	            { "field": "user.age_days", "operator": "less_than", "value": 7 },
	            { "field": "source.ip", "operator": "cidr", "value": "203.0.113.0/24" }
	          ] }
	        ] } },
	    { "id": "a", "kind": "action", "action": "ban", "reason": "Ban evasion" }
	  ],
	  "edges": [
	    { "from": "t", "to": "c" },
	    { "from": "c", "to": "a", "output": "true" }
	  ]
	}
	""";

	private static PortalRuleDocument Parse(string json) => JsonSerializer.Deserialize<PortalRuleDocument>(json, PortalJson.Options)!;

	private static PortalRuleCatalog Catalog() => new PortalRuleCatalog
	{
		Fields = RuleFields.All.Select(f => new PortalRuleField { Key = f.Key, Type = f.Type, Label = f.Label, Triggers = f.Triggers, Operators = f.Operators, TrueLabel = f.TrueLabel, FalseLabel = f.FalseLabel }).ToList()
	};

	[Fact]
	public void TheGraphMapsBackToTheSameDocument()
	{
		PortalRuleDocument document = Parse(Example);
		(List<BlowNode> nodes, List<BlowEdge> edges) = RuleGraph.ToGraph(document);

		Assert.Equal(["t", "c", "a"], nodes.Select(n => n.Id));
		Assert.Equal(["trigger", "condition", "action"], nodes.Select(n => n.Type));
		Assert.False(nodes[0].Deletable);
		Assert.Equal([(null, "c"), ("true", "a")], edges.Select(e => (e.SourceHandle, e.Target)));

		PortalRuleDocument back = RuleGraph.ToDocument(nodes, edges);
		// the canvas adds the positions it placed the nodes at, by the column of their distance from the trigger
		Assert.Equal([(0d, 0d), (RuleGraph.ColumnWidth, 0d), (2 * RuleGraph.ColumnWidth, 0d)], back.Nodes.Select(n => (n.Position!.X, n.Position.Y)));
		foreach (PortalRuleNode node in back.Nodes)
		{
			node.Position = null;
		}
		Assert.Equal(JsonSerializer.Serialize(JsonDocument.Parse(Example), PortalJson.Options), JsonSerializer.Serialize(JsonDocument.Parse(back.ToJson()), PortalJson.Options));

		// the host takes the mapped document as its own format, positions included
		(RuleDocument? parsed, List<RuleProblem> problems) = RuleDocuments.Parse(RuleGraph.ToDocument(nodes, edges).ToJson());
		Assert.Empty(problems);
		Assert.Equal(RuleGraph.ColumnWidth, parsed!.Nodes[1].Position!.X);
	}

	[Fact]
	public void APlacedNodeKeepsItsPositionAndAMovedOneSavesTheNewOne()
	{
		PortalRuleDocument document = Parse(Example);
		document.Nodes[2].Position = new PortalRulePosition { X = 40, Y = 300 };
		(List<BlowNode> nodes, List<BlowEdge> edges) = RuleGraph.ToGraph(document);
		Assert.Equal((40d, 300d), (nodes[2].Position.X, nodes[2].Position.Y));

		nodes[1].Position = new BlowXYPosition(123.4, 56.6);
		PortalRuleDocument back = RuleGraph.ToDocument(nodes, edges);
		Assert.Equal((123d, 57d), (back.Nodes[1].Position!.X, back.Nodes[1].Position!.Y));
		Assert.Equal((40d, 300d), (back.Nodes[2].Position!.X, back.Nodes[2].Position!.Y));
	}

	[Fact]
	public void AnEditOfTheGraphNeverChangesTheDocumentItCameFrom()
	{
		PortalRuleDocument document = Parse(Example);
		(List<BlowNode> nodes, _) = RuleGraph.ToGraph(document);
		RuleGraph.RuleOf(nodes[2]).Reason = "changed";
		Assert.Equal("Ban evasion", document.Nodes[2].Reason);
	}

	[Fact]
	public void TheHandlesAllowOnlyTheConnectionsTheHostAccepts()
	{
		var document = new PortalRuleDocument
		{
			Nodes =
			[
				new PortalRuleNode { Id = "t", Kind = "trigger", Trigger = "sign_in" },
				new PortalRuleNode { Id = "c1", Kind = "condition", Query = PortalRuleQuery.Group("all") },
				new PortalRuleNode { Id = "c2", Kind = "condition", Query = PortalRuleQuery.Group("all") },
				new PortalRuleNode { Id = "a", Kind = "action", Action = "kick", Reason = "r" }
			],
			Edges = [new PortalRuleEdge { From = "t", To = "c1" }, new PortalRuleEdge { From = "c1", To = "c2", Output = "true" }]
		};
		(List<BlowNode> nodes, List<BlowEdge> edges) = RuleGraph.ToGraph(document);
		BlowConnectionRejection Check(string from, string? output, string to) => RuleGraph.Check(nodes, edges, from, output, to).Rejection;

		// trigger to a condition or an action, a condition output to a condition or an action
		Assert.Equal(BlowConnectionRejection.None, Check("t", null, "a"));
		Assert.Equal(BlowConnectionRejection.None, Check("c1", "false", "a"));
		Assert.Equal(BlowConnectionRejection.None, Check("c2", "true", "a"));
		Assert.Equal(BlowConnectionRejection.None, Check("c1", "false", "c2"));

		// an action ends its branch, nothing leads into the trigger, a condition names its output, the trigger names none
		Assert.NotEqual(BlowConnectionRejection.None, Check("a", null, "c1"));
		Assert.NotEqual(BlowConnectionRejection.None, Check("c1", "true", "t"));
		Assert.NotEqual(BlowConnectionRejection.None, Check("c1", null, "a"));
		Assert.NotEqual(BlowConnectionRejection.None, Check("t", "true", "a"));

		// no loop, no second edge between the same handles, no edge from a node to itself
		Assert.Equal(BlowConnectionRejection.Cycle, Check("c2", "false", "c1"));
		Assert.Equal(BlowConnectionRejection.Duplicate, Check("c1", "true", "c2"));
		Assert.NotEqual(BlowConnectionRejection.None, Check("c1", "true", "c1"));
	}

	[Fact]
	public void TheOutlineReadsTheGraphBranchByBranch()
	{
		PortalRuleDocument document = Parse(Example);
		document.Nodes.Add(new PortalRuleNode { Id = "m", Kind = "action", Action = "mute", DurationS = 86400, Reason = "Cool down" });
		document.Edges.Add(new PortalRuleEdge { From = "c", To = "m", Output = "false" });
		document.Edges.Add(new PortalRuleEdge { From = "t", To = "m" });
		document.Nodes.Add(new PortalRuleNode { Id = "lost", Kind = "action", Action = "flag", Reason = "Look" });

		List<RuleOutlineLine> lines = RuleOutline.Lines(document, Catalog());
		Assert.Equal("""
			on event.action == "sign_in"
			  if host.id in ["AB12-CD34-YY"] && (user.age_days < 7 || cidr("203.0.113.0/24").containsIP(source.ip))
			    then ban permanently, reason "Ban evasion"
			  else
			    then mute for 1d, reason "Cool down"
			  see above: then mute for 1d, reason "Cool down"
			unreached:
			  then flag, reason "Look"
			""".Replace("\t", "").Trim(), RuleOutline.Text(lines));
		// a line that starts a node names it, so the outline view selects that node on the canvas
		Assert.Equal(["t", "c", "a", "m", "m", "lost"], lines.Where(l => l.NodeId != null).Select(l => l.NodeId));
		Assert.Equal("nothing", RuleOutline.Lines(Parse("""{"nodes":[{"id":"t","kind":"trigger","event":{"action":"sweep"}}],"edges":[]}"""))[1].Text);

		// every line also reads in words, for screen readers
		Assert.Equal("When a player signs in", lines[0].Spoken);
		Assert.Equal("If Machine GUID is one of \"AB12-CD34-YY\" and either Account age in days is less than 7 or IP address is in range \"203.0.113.0/24\"", lines[1].Spoken);
		Assert.Equal("Ban permanently, reason: Ban evasion", lines[2].Spoken);
		Assert.Equal("Otherwise", lines[3].Spoken);
	}

	[Fact]
	public void TheOutlineWritesEveryConditionAsACelExpression()
	{
		PortalRuleCatalog catalog = Catalog();
		PortalRuleQuery Leaf(string field, string op, object? value = null) =>
			new PortalRuleQuery { Field = field, Operator = op, Value = value == null ? null : JsonSerializer.SerializeToElement(value) };

		// nested groups in parentheses, a yes or no field as itself or negated
		PortalRuleQuery query = PortalRuleQuery.Group("all", Leaf("user.age_days", "less_than", 7), PortalRuleQuery.Group("any", Leaf("user.email_confirmed", "equals", false), Leaf("host.mac_virtual", "equals", true)));
		Assert.Equal("user.age_days < 7 && (!user.email_confirmed || host.mac_virtual)", RuleOutline.Expression(query));
		Assert.Equal("If Account age in days is less than 7 and either Email is not confirmed or MAC is virtual", "If " + RuleOutline.Words(query, catalog));

		// every operator, quoted strings with their quotes escaped and lists in brackets
		Assert.Equal("user.name == \"Sneaky \\\"Pete\\\"\"", RuleOutline.Expression(Leaf("user.name", "equals", "Sneaky \"Pete\"")));
		Assert.Equal("user.name != \"x\"", RuleOutline.Expression(Leaf("user.name", "not_equals", "x")));
		Assert.Equal("source.geo.country_iso_code in [\"NL\", \"BE\"]", RuleOutline.Expression(Leaf("source.geo.country_iso_code", "in_list", new[] { "NL", "BE" })));
		Assert.Equal("user.email_domain.contains(\"spam\")", RuleOutline.Expression(Leaf("user.email_domain", "contains", "spam")));
		Assert.Equal("user.name.startsWith(\"x\")", RuleOutline.Expression(Leaf("user.name", "starts_with", "x")));
		Assert.Equal("user.stats.elo > 1500", RuleOutline.Expression(Leaf("user.stats.elo", "greater_than", 1500)));
		Assert.Equal("(3 <= user.age_days && user.age_days <= 10)", RuleOutline.Expression(Leaf("user.age_days", "between", new[] { 3, 10 })));
		Assert.Equal("size(user.stats.elo) == 0", RuleOutline.Expression(Leaf("user.stats.elo", "is_empty")));
		Assert.Equal("user.two_factor", RuleOutline.Expression(PortalRuleQuery.Group("any", Leaf("user.two_factor", "equals", true))));
		Assert.Equal("true", RuleOutline.Expression(PortalRuleQuery.Group("all")));
		Assert.Equal("false", RuleOutline.Expression(PortalRuleQuery.Group("any")));

		// the actions keep their reason and length readable
		Assert.Equal("then ban for 7d, reason \"Ban evasion\"", RuleOutline.ActionExpression(new PortalRuleNode { Kind = "action", Action = "ban", DurationS = 604800, Reason = "Ban evasion" }));
		Assert.Equal("then mute for 90m, reason \"Calm\"", RuleOutline.ActionExpression(new PortalRuleNode { Kind = "action", Action = "mute", DurationS = 5400, Reason = "Calm" }));
		Assert.Equal("then kick, reason \"Go\"", RuleOutline.ActionExpression(new PortalRuleNode { Kind = "action", Action = "kick", Reason = "Go" }));
		Assert.Equal(["7d", "3h", "90s", "1m", "?"], new long?[] { 604800, 10800, 90, 60, null }.Select(RuleOutline.ShortLength));
	}

	[Fact]
	public void AConditionNodeShowsItsQueryOnOneLine()
	{
		PortalRuleCatalog catalog = Catalog();
		PortalRuleDocument document = Parse(Example);
		Assert.Equal("All of 2: Machine GUID is one of \"AB12-CD34-YY\", …", RuleOutline.Summary(document.Nodes[1].Query, catalog));
		Assert.Equal("Account age in days is less than 7", RuleOutline.Summary(PortalRuleQuery.Group("any", document.Nodes[1].Query!.Rules![1].Rules![0]), catalog));
		Assert.Equal("Any of 2: All of 1, …", RuleOutline.Summary(PortalRuleQuery.Group("any", PortalRuleQuery.Group("all", new PortalRuleQuery()), new PortalRuleQuery()), catalog));
		Assert.Equal("No conditions", RuleOutline.Summary(PortalRuleQuery.Group("all")));
		Assert.Equal(["7 days", "3 hours", "90 seconds", "1 minute", "no length"], new long?[] { 604800, 10800, 90, 60, null }.Select(RuleOutline.Length));
	}

	[Fact]
	public void TheDifferenceOfTwoVersionsMarksTheChangedLines()
	{
		PortalRuleDocument older = Parse(Example);
		PortalRuleDocument newer = Parse(Example);
		newer.Nodes[1].Query!.Rules![1].Rules![0].Value = JsonSerializer.SerializeToElement(3);
		newer.Nodes[2].Reason = "Evading a ban";

		List<RuleOutlineChange> changes = RuleOutline.Diff(older, newer, Catalog());
		Assert.Equal(
			[
				('-', "  if host.id in [\"AB12-CD34-YY\"] && (user.age_days < 7 || cidr(\"203.0.113.0/24\").containsIP(source.ip))"),
				('-', "    then ban permanently, reason \"Ban evasion\""),
				('+', "  if host.id in [\"AB12-CD34-YY\"] && (user.age_days < 3 || cidr(\"203.0.113.0/24\").containsIP(source.ip))"),
				('+', "    then ban permanently, reason \"Evading a ban\"")
			],
			changes.Where(c => c.Kind != ' ').Select(c => (c.Kind, c.Line)));
		Assert.Equal(RuleOutline.Text(RuleOutline.Lines(older, Catalog())).Split('\n').Length - 2, changes.Count(c => c.Kind == ' '));
		Assert.All(RuleOutline.Diff(older, older), c => Assert.Equal(' ', c.Kind));
	}

	[Fact]
	public void AProblemOfTheHostLandsOnTheNodeAndConditionItsPathNames()
	{
		PortalRuleDocument sent = Parse(Example);
		List<RuleNodeProblem> placed = RuleProblems.Place(
			[
				new PortalRuleProblem { Path = "nodes[1].query.rules[1].rules[0].value", Reason = "a number" },
				new PortalRuleProblem { Path = "nodes[2].reason", Reason = "between 1 and 512 characters" },
				new PortalRuleProblem { Path = "edges[1].output", Reason = "true or false from a condition" },
				new PortalRuleProblem { Path = "nodes", Reason = "exactly one trigger node" }
			], sent);
		Assert.Equal(
			[("c", "query.rules[1].rules[0].value"), ("a", "reason"), ("c", "edge to a"), (null, "nodes")],
			placed.Select(p => (p.NodeId, p.Path)));
		Assert.Single(RuleProblems.At(placed, "c", "query.rules[1]"));
		Assert.Empty(RuleProblems.At(placed, "c", "query.rules[0]"));
		Assert.Empty(RuleProblems.At(placed, "c", "query.rules[1]", below: false));
	}

	[Fact]
	public void TheQueryBuilderTypesValuesAsTheHostChecksThem()
	{
		Assert.Equal("[7,30]", RuleValues.Parse("number", "in_list", "7\n\n 30 \n", out _)!.Value.GetRawText());
		Assert.Null(RuleValues.Parse("number", "in_list", "7\nseven", out string? problem));
		Assert.Equal("Type a number.", problem);
		Assert.Equal("\"spam.example\"", RuleValues.Parse("text", "equals", " spam.example ", out _)!.Value.GetRawText());
		Assert.Equal("true", RuleValues.Parse("bool", "equals", "true", out _)!.Value.GetRawText());
		Assert.Null(RuleValues.Parse("text", "is_empty", "anything", out problem));
		Assert.Null(problem);
		Assert.Equal("[1,5]", RuleValues.ParseRange("1", "5", out _)!.Value.GetRawText());
		Assert.Null(RuleValues.ParseRange("5", "1", out problem));
		Assert.Equal(("1", "5"), RuleValues.Range(JsonSerializer.SerializeToElement(new[] { 1, 5 })));
		Assert.Equal("a\nb", RuleValues.Text(JsonSerializer.SerializeToElement(new[] { "a", "b" })));
	}

	// The staff pages name the host's permissions, and the rule pages check the one their routes need
	[Fact]
	public void ThePagesAskForTheHostsPermissions()
	{
		Assert.Equal(Permissions.RulesManage, PortalPermissions.RulesManage);
		Assert.Equal(Permissions.AnticheatReview, PortalPermissions.AnticheatReview);
		string pages = Path.Combine(SolutionRoot(), "GenOnlineService.Portal.Client", "Pages");
		Assert.Contains("Api.Session!.Can(PortalPermissions.RulesManage)", File.ReadAllText(Path.Combine(pages, "Rules.razor")));
		Assert.Contains("Api.Session!.Can(PortalPermissions.RulesManage)", File.ReadAllText(Path.Combine(pages, "Rule.razor")));
		Assert.Contains("Api.Session!.Can(PortalPermissions.AnticheatReview)", File.ReadAllText(Path.Combine(pages, "RuleMatches.razor")));
	}

	private static string SolutionRoot()
	{
		DirectoryInfo root = new DirectoryInfo(AppContext.BaseDirectory);
		while (root.Parent != null && !File.Exists(Path.Combine(root.FullName, "GenServices.sln")))
		{
			root = root.Parent;
		}
		return root.FullName;
	}
}
