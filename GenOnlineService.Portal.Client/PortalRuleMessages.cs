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
using System.Text.Json.Serialization;

namespace GenOnlineService.Portal.Client
{
	// The words of the automated rules as the host names them, see docs/automated-rules.md
	public static class PortalRuleWords
	{
		public const string Trigger = "trigger";
		public const string Condition = "condition";
		public const string Action = "action";

		public const string OutputTrue = "true";
		public const string OutputFalse = "false";

		public const string All = "all";
		public const string Any = "any";

		public const string Watch = "watch";
		public const string Flag = "flag";
		public const string Enforce = "enforce";

		public const string Kick = "kick";
		public const string Mute = "mute";
		public const string Ban = "ban";

		public const string IsEmpty = "is_empty";
		public const string InList = "in_list";
		public const string Between = "between";

		public static readonly IReadOnlyList<string> Triggers = ["sign_in", "game_session", "lobby_join", "match_result", "anticheat_report", "sweep"];
		public static readonly IReadOnlyList<string> Modes = [Watch, Flag, Enforce];
		public static readonly IReadOnlyList<string> Actions = [Flag, Kick, Mute, Ban];

		public static string TriggerLabel(string? trigger) => trigger switch
		{
			"sign_in" => "Sign-in",
			"game_session" => "Game session",
			"lobby_join" => "Lobby join",
			"match_result" => "Match result",
			"anticheat_report" => "Anti-cheat report",
			"sweep" => "Manual sweep",
			null or "" => "No trigger",
			_ => trigger
		};

		// The event in a sentence, as the outline opens with it
		public static string TriggerSentence(string? trigger) => trigger switch
		{
			"sign_in" => "a player signs in",
			"game_session" => "a game session starts",
			"lobby_join" => "a player joins a lobby",
			"match_result" => "a player reports a match result",
			"anticheat_report" => "an anti-cheat review row is added",
			"sweep" => "a manual sweep runs",
			_ => "an unknown event happens"
		};

		public static string ModeLabel(string? mode) => mode switch
		{
			Watch => "Watch",
			Flag => "Flag",
			Enforce => "Enforce",
			_ => mode ?? ""
		};

		public static string ModeDescription(string? mode) => mode switch
		{
			Watch => "Records each match only.",
			Flag => "Records each match in the review queue.",
			Enforce => "Takes the actions and records what it did.",
			_ => ""
		};

		public static string ActionLabel(string? action) => action switch
		{
			Flag => "Flag for review",
			Kick => "Kick",
			Mute => "Mute",
			Ban => "Ban",
			_ => action ?? ""
		};

		public static string OperatorLabel(string? op) => op switch
		{
			"equals" => "is",
			"not_equals" => "is not",
			InList => "is one of",
			"contains" => "contains",
			"starts_with" => "starts with",
			"greater_than" => "is more than",
			"less_than" => "is less than",
			Between => "is between",
			IsEmpty => "is empty",
			"cidr" => "is in range",
			_ => op ?? ""
		};

		// The short form of an operator on a condition row, the words of OperatorLabel stay with it for screen readers
		public static string OperatorSymbol(string? op) => op switch
		{
			"equals" => "=",
			"not_equals" => "\u2260",
			InList => "in",
			"contains" => "contains",
			"starts_with" => "starts with",
			"greater_than" => ">",
			"less_than" => "<",
			Between => "between",
			IsEmpty => "is empty",
			"cidr" => "in range",
			_ => op ?? ""
		};

		public static string OutcomeLabel(string? outcome) => outcome switch
		{
			"watched" => "Watched",
			"flagged" => "Flagged",
			"enforced" => "Enforced",
			"staff_exempt" => "Staff, exempt",
			"nothing_to_do" => "Nothing to do",
			_ => outcome ?? ""
		};

		public static string SweepStateLabel(string? state) => state switch
		{
			"running" => "Running",
			"completed" => "Completed",
			"cancelled" => "Cancelled",
			"interrupted" => "Interrupted",
			"failed" => "Failed",
			_ => state ?? ""
		};
	}

	// The graph document of one rule version, the persisted format the host validates and compiles
	public sealed class PortalRuleDocument
	{
		public List<PortalRuleNode> Nodes { get; set; } = [];
		public List<PortalRuleEdge> Edges { get; set; } = [];

		// A deep copy through the wire format, so an edit never changes a document someone else holds
		public PortalRuleDocument Clone() => JsonSerializer.Deserialize<PortalRuleDocument>(ToJson(), PortalJson.Options) ?? new PortalRuleDocument();

		public string ToJson() => JsonSerializer.Serialize(this, PortalJson.Options);

		[JsonIgnore]
		public string? Trigger => Nodes.FirstOrDefault(n => n.Kind == PortalRuleWords.Trigger)?.Trigger;
	}

	// One node, whose fields apply by its kind
	public sealed class PortalRuleNode
	{
		public string Id { get; set; } = "";
		public string Kind { get; set; } = "";

		// trigger nodes: the event the rule is checked on, as event.action
		public PortalRuleNodeEvent? Event { get; set; }
		public PortalRuleQuery? Query { get; set; }
		public string? Action { get; set; }
		public long? DurationS { get; set; }
		public string? Reason { get; set; }
		public PortalRulePosition? Position { get; set; }

		// The event.action of a trigger node, null when the node has none
		[JsonIgnore]
		public string? Trigger
		{
			get => Event?.Action;
			set => Event = value == null ? null : new PortalRuleNodeEvent { Action = value };
		}
	}

	public sealed class PortalRuleNodeEvent
	{
		public string? Action { get; set; }
	}

	public sealed class PortalRulePosition
	{
		public double X { get; set; }
		public double Y { get; set; }
	}

	// A group with match and rules, or a condition with field, operator and value
	public sealed class PortalRuleQuery
	{
		public string? Match { get; set; }
		public List<PortalRuleQuery>? Rules { get; set; }
		public string? Field { get; set; }
		public string? Operator { get; set; }
		public JsonElement? Value { get; set; }

		[JsonIgnore]
		public bool IsGroup => Field == null;

		public static PortalRuleQuery Group(string match, params PortalRuleQuery[] rules) => new PortalRuleQuery { Match = match, Rules = rules.ToList() };
	}

	public sealed class PortalRuleEdge
	{
		public string From { get; set; } = "";
		public string To { get; set; } = "";
		public string? Output { get; set; }
	}

	// A rule as the list and the editor show it
	public sealed record PortalRule
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public string? Description { get; init; }
		public string Mode { get; init; } = "";
		public bool Enabled { get; init; }
		public int CurrentVersion { get; init; }
		public bool Archived { get; init; }
		public string Trigger { get; init; } = "";
		public string CreatedBy { get; init; } = "";
		public DateTime CreatedAt { get; init; }
		public string UpdatedBy { get; init; } = "";
		public DateTime UpdatedAt { get; init; }
		public DateTime? LastMatchAt { get; init; }

		// set while the circuit breaker holds an Enforce rule in Flag, until staff change its mode
		public DateTime? BreakerTrippedAt { get; init; }

		// the players behind created_by and updated_by, null when the actor is no player
		public PortalMember? CreatedByPlayer { get; init; }
		public PortalMember? UpdatedByPlayer { get; init; }
	}

	public sealed record PortalRuleList
	{
		public IReadOnlyList<PortalRule> Rules { get; init; } = [];
	}

	public sealed record PortalRuleVersion
	{
		public int Number { get; init; }
		public string CreatedBy { get; init; } = "";
		public string? Comment { get; init; }
		public DateTime CreatedAt { get; init; }
		public PortalRuleDocument? Document { get; init; }

		// the player behind created_by, null when the actor is no player
		public PortalMember? CreatedByPlayer { get; init; }
	}

	public sealed record PortalRuleDetail
	{
		public PortalRule Rule { get; init; } = new PortalRule();
		public PortalRuleDocument Document { get; init; } = new PortalRuleDocument();
		public IReadOnlyList<PortalRuleVersion> Versions { get; init; } = [];
	}

	// One problem of a document, with its path inside the document such as nodes[2].query.rules[0].value
	public sealed record PortalRuleProblem
	{
		public string Path { get; init; } = "";
		public string Reason { get; init; } = "";
	}

	public sealed record PortalRuleValidation
	{
		public bool Valid { get; init; }
		public IReadOnlyList<PortalRuleProblem> Problems { get; init; } = [];
	}

	public sealed record PortalRuleField
	{
		public string Key { get; init; } = "";
		public string Type { get; init; } = "";
		public string Label { get; init; } = "";
		public string Description { get; init; } = "";
		public IReadOnlyList<string> Triggers { get; init; } = [];
		public IReadOnlyList<string> Operators { get; init; } = [];

		// a yes or no field only: its two states as whole sentences, such as Email is not confirmed
		public string? TrueLabel { get; init; }
		public string? FalseLabel { get; init; }
	}

	public sealed record PortalRuleCatalog
	{
		public IReadOnlyList<PortalRuleField> Fields { get; init; } = [];
		public IReadOnlyList<string> Triggers { get; init; } = [];
		public IReadOnlyList<string> Actions { get; init; } = [];
		public IReadOnlyList<string> Modes { get; init; } = [];

		public PortalRuleField? Find(string? key) => key == null ? null : Fields.FirstOrDefault(f => f.Key == key);
	}

	public sealed record PortalRulePreviewPlayer
	{
		public PortalMember Player { get; init; } = new PortalMember();
		public IReadOnlyList<string> Actions { get; init; } = [];
		public JsonElement Facts { get; init; }
	}

	public sealed record PortalRulePreview
	{
		public int Scanned { get; init; }
		public int Matched { get; init; }
		public bool Complete { get; init; }
		public bool TimedOut { get; init; }
		public IReadOnlyList<PortalRulePreviewPlayer> Players { get; init; } = [];
	}

	public sealed record PortalRuleSweep
	{
		public long RuleId { get; init; }
		public int RuleVersion { get; init; }
		public string State { get; init; } = "";
		public int Total { get; init; }
		public int Scanned { get; init; }
		public int Matched { get; init; }
		public string StartedBy { get; init; } = "";
		public DateTime StartedAt { get; init; }
		public DateTime? FinishedAt { get; init; }

		[JsonIgnore]
		public bool Running => State == "running";
	}

	public sealed record PortalRuleMatch
	{
		public long Id { get; init; }
		public long RuleId { get; init; }
		public string RuleName { get; init; } = "";
		public int RuleVersion { get; init; }
		public PortalMember Player { get; init; } = new PortalMember();
		public string Trigger { get; init; } = "";
		public DateTime MatchedAt { get; init; }
		public string Mode { get; init; } = "";
		public IReadOnlyList<string> Actions { get; init; } = [];
		public string Outcome { get; init; } = "";
		public JsonElement Facts { get; init; }
		public string ReviewState { get; init; } = "";
		public long? ReviewedBy { get; init; }
		public DateTime? ReviewedAt { get; init; }
	}

	public sealed record PortalRuleMatchList
	{
		public IReadOnlyList<PortalRuleMatch> Matches { get; init; } = [];
	}

	public sealed record PortalRuleMatchPage
	{
		public IReadOnlyList<PortalRuleMatch> Matches { get; init; } = [];
		public int Total { get; init; }
	}

	// The filters of the matches page, as the address holds them
	public sealed record PortalRuleMatchQuery
	{
		public string Review { get; init; } = "pending";
		public long? RuleId { get; init; }
		public string? Trigger { get; init; }
		public string? Outcome { get; init; }

		public static readonly IReadOnlyList<string> ReviewStates = ["pending", "reviewed", "dismissed", "none", "all"];
		public static readonly IReadOnlyList<string> Outcomes = ["watched", "flagged", "enforced", "staff_exempt", "nothing_to_do"];

		public static string ReviewLabel(string state) => state switch
		{
			"pending" => "Waiting for review",
			"reviewed" => "Reviewed",
			"dismissed" => "Dismissed",
			"none" => "Not queued",
			_ => "All"
		};
	}

	// The answer of a rule change: the value, or the refusal with its details such as the problems of the document
	public readonly record struct PortalRuleOutcome<T>(T? Value, PortalError? Failure) where T : class
	{
		public bool Ok => Failure == null && Value != null;

		public string? Error => Failure?.Error;

		// The problems of invalid_argument {field: document, problems}
		public IReadOnlyList<PortalRuleProblem> Problems =>
			Failure?.Params is JsonElement details && details.ValueKind == JsonValueKind.Object && details.TryGetProperty("problems", out JsonElement problems) && problems.ValueKind == JsonValueKind.Array
				? problems.Deserialize<List<PortalRuleProblem>>(PortalJson.Options) ?? []
				: [];

		// conflict {what}: version when another save came first, sweep when one already runs
		public string? ConflictWhat =>
			Failure?.Error == "conflict" && Failure.Params is JsonElement details && details.ValueKind == JsonValueKind.Object && details.TryGetProperty("what", out JsonElement what) && what.ValueKind == JsonValueKind.String
				? what.GetString()
				: null;

		public int? CurrentVersion =>
			Failure?.Params is JsonElement details && details.ValueKind == JsonValueKind.Object && details.TryGetProperty("current", out JsonElement current) && current.ValueKind == JsonValueKind.Number
				? current.GetInt32()
				: null;
	}
}
