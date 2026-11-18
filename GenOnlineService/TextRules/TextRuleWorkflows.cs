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

using RulesEngine.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GenOnlineService.TextRules
{
	// What the rules of one check read: the matches by severity and category. Impersonation and reserved words count
	// only apart, never as block or flag.
	public sealed class TextRuleFacts
	{
		public string Kind { get; init; } = TextKinds.Chat;
		public int Matches { get; init; }
		public int Block { get; init; }
		public int Flag { get; init; }
		public int Slur { get; init; }
		public int Sexual { get; init; }
		public int Profanity { get; init; }
		public int Insult { get; init; }
		public int Impersonation { get; init; }
		public int Reserved { get; init; }

		public static TextRuleFacts Of(string kind, IReadOnlyCollection<TextHit> hits)
		{
			List<TextTermInfo> terms = hits.Select(h => h.Term).DistinctBy(t => (t.ID, t.Term, t.Language)).ToList();
			List<TextTermInfo> rated = terms.Where(t => !TextCategories.IsNameOnly(t.Category)).ToList();
			return new TextRuleFacts
			{
				Kind = kind,
				Matches = terms.Count,
				Block = rated.Count(t => t.Severity == TextSeverities.Block),
				Flag = rated.Count(t => t.Severity == TextSeverities.Flag),
				Slur = terms.Count(t => t.Category == TextCategories.Slur),
				Sexual = terms.Count(t => t.Category == TextCategories.Sexual),
				Profanity = terms.Count(t => t.Category == TextCategories.Profanity),
				Insult = terms.Count(t => t.Category == TextCategories.Insult),
				Impersonation = terms.Count(t => t.Category == TextCategories.Impersonation),
				Reserved = terms.Count(t => t.Category == TextCategories.Reserved)
			};
		}
	}

	// One rule of a workflow as staff write it: a condition over input (TextRuleFacts) and the outcome when it holds
	public sealed record TextRuleDefinition
	{
		public string Name { get; init; } = String.Empty;
		public string Expression { get; init; } = String.Empty;
		public string Outcome { get; init; } = TextOutcomes.Allow;
		public bool Enabled { get; init; } = true;
	}

	// The rules of one input kind, checked in order: the first enabled rule that holds decides, none means allow
	public sealed record TextWorkflowDefinition
	{
		public IReadOnlyList<TextRuleDefinition> Rules { get; init; } = [];
	}

	public sealed record TextRuleDecision(string Outcome, string? Rule);

	// The rule workflows of the text checks run on Microsoft RulesEngine. Staff write only a condition and an outcome per
	// rule: the condition may name the input fields, numbers, strings, comparisons and and/or/not, nothing else, so it can
	// never reach a type or a method. RulesEngine's own actions, nested rules and injected workflows cannot be written.
	public sealed class TextRuleWorkflows
	{
		public const int MaxRules = 32;
		public const int MaxNameLength = 64;
		public const int MaxExpressionLength = 500;
		public const string InputName = "input";

		private static readonly ILogger s_log = AppLog.For(typeof(TextRuleWorkflows));

		public static readonly JsonSerializerOptions Json = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
			UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
			WriteIndented = true
		};

		private static readonly HashSet<string> s_identifiers = new(StringComparer.Ordinal)
		{
			InputName, "true", "false", "null", "and", "or", "not", "AND", "OR", "NOT",
			nameof(TextRuleFacts.Kind), nameof(TextRuleFacts.Matches), nameof(TextRuleFacts.Block), nameof(TextRuleFacts.Flag),
			nameof(TextRuleFacts.Slur), nameof(TextRuleFacts.Sexual), nameof(TextRuleFacts.Profanity), nameof(TextRuleFacts.Insult),
			nameof(TextRuleFacts.Impersonation), nameof(TextRuleFacts.Reserved)
		};

		// The input fields an expression may read, as input.<field>
		public static readonly IReadOnlyList<string> Fields =
		[
			nameof(TextRuleFacts.Kind), nameof(TextRuleFacts.Matches), nameof(TextRuleFacts.Block), nameof(TextRuleFacts.Flag), nameof(TextRuleFacts.Slur),
			nameof(TextRuleFacts.Sexual), nameof(TextRuleFacts.Profanity), nameof(TextRuleFacts.Insult), nameof(TextRuleFacts.Impersonation), nameof(TextRuleFacts.Reserved)
		];

		private static readonly Regex s_strings = new("\"[^\"\\\\]*\"", RegexOptions.Compiled);
		private static readonly Regex s_identifier = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
		private static readonly Regex s_allowedCharacters = new(@"^[A-Za-z0-9_\s\.\(\)!=<>&|+\-""]*$", RegexOptions.Compiled);

		public static readonly IReadOnlyDictionary<string, TextWorkflowDefinition> Defaults = new Dictionary<string, TextWorkflowDefinition>(StringComparer.Ordinal)
		{
			[TextKinds.DisplayName] = new TextWorkflowDefinition
			{
				Rules =
				[
					new TextRuleDefinition { Name = "reserved words", Expression = "input.Impersonation > 0 || input.Reserved > 0", Outcome = TextOutcomes.Refuse },
					new TextRuleDefinition { Name = "blocked words", Expression = "input.Block > 0", Outcome = TextOutcomes.Refuse },
					// names are stricter than chat, which only masks these
					new TextRuleDefinition { Name = "flagged words", Expression = "input.Flag > 0", Outcome = TextOutcomes.Refuse }
				]
			},
			[TextKinds.LobbyName] = new TextWorkflowDefinition
			{
				Rules = [new TextRuleDefinition { Name = "blocked words", Expression = "input.Block > 0", Outcome = TextOutcomes.Refuse }]
			},
			[TextKinds.Chat] = new TextWorkflowDefinition
			{
				Rules =
				[
					new TextRuleDefinition { Name = "blocked words", Expression = "input.Block > 0", Outcome = TextOutcomes.Block },
					new TextRuleDefinition { Name = "flagged words", Expression = "input.Flag > 0", Outcome = TextOutcomes.Mask }
				]
			}
		};

		// The defaults of earlier seed versions: a stored workflow still equal to one of them was never changed by staff and
		// takes the current default on upgrade
		public static readonly IReadOnlyDictionary<string, IReadOnlyList<TextWorkflowDefinition>> PreviousDefaults = new Dictionary<string, IReadOnlyList<TextWorkflowDefinition>>(StringComparer.Ordinal)
		{
			[TextKinds.DisplayName] =
			[
				new TextWorkflowDefinition
				{
					Rules =
					[
						new TextRuleDefinition { Name = "reserved words", Expression = "input.Impersonation > 0 || input.Reserved > 0", Outcome = TextOutcomes.Refuse },
						new TextRuleDefinition { Name = "blocked words", Expression = "input.Block > 0", Outcome = TextOutcomes.Refuse }
					]
				}
			]
		};

		private readonly RulesEngine.RulesEngine m_engine;
		private readonly IReadOnlyDictionary<string, TextWorkflowDefinition> m_definitions;

		public TextRuleWorkflows(IReadOnlyDictionary<string, TextWorkflowDefinition> definitions)
		{
			m_definitions = definitions;
			m_engine = new RulesEngine.RulesEngine(definitions.Select(d => ToWorkflow(d.Key, d.Value)).ToArray(), Settings());
		}

		public IReadOnlyDictionary<string, TextWorkflowDefinition> Definitions => m_definitions;

		private static ReSettings Settings() => new ReSettings
		{
			CustomTypes = [],
			EnableExceptionAsErrorMessage = true,
			IgnoreException = false
		};

		private static Workflow ToWorkflow(string kind, TextWorkflowDefinition definition) => new Workflow
		{
			WorkflowName = kind,
			Rules = definition.Rules.Select(r => new Rule
			{
				RuleName = r.Name,
				Expression = r.Expression,
				SuccessEvent = r.Outcome,
				Enabled = r.Enabled,
				RuleExpressionType = RuleExpressionType.LambdaExpression
			}).ToList()
		};

		// The first enabled rule that holds; a rule that fails to evaluate is logged and skipped
		public TextRuleDecision Decide(TextRuleFacts facts)
		{
			if (!m_definitions.TryGetValue(facts.Kind, out TextWorkflowDefinition? definition) || definition.Rules.All(r => !r.Enabled))
			{
				return new TextRuleDecision(TextOutcomes.Allow, null);
			}

			// the rules hold no actions, so the engine completes without awaiting anything
			List<RuleResultTree> results = m_engine.ExecuteAllRulesAsync(facts.Kind, new RuleParameter(InputName, facts)).GetAwaiter().GetResult();
			foreach (RuleResultTree result in results)
			{
				if (!String.IsNullOrEmpty(result.ExceptionMessage))
				{
					s_log.LogWarning("Text rule {Rule} of {Kind} failed: {Error}", result.Rule.RuleName, facts.Kind, result.ExceptionMessage);
					continue;
				}
				if (result.IsSuccess)
				{
					return new TextRuleDecision(result.Rule.SuccessEvent, result.Rule.RuleName);
				}
			}
			return new TextRuleDecision(TextOutcomes.Allow, null);
		}

		// The stored JSON of a workflow, or null with the reason; a valid workflow compiles and runs on a sample input
		public static (TextWorkflowDefinition? Definition, string? Error) Parse(string kind, string? json)
		{
			if (!TextKinds.IsKnown(kind))
			{
				return (null, "unknown kind");
			}
			if (String.IsNullOrWhiteSpace(json))
			{
				return (null, "the workflow is empty");
			}

			TextWorkflowDefinition? definition;
			try
			{
				definition = JsonSerializer.Deserialize<TextWorkflowDefinition>(json, Json);
			}
			catch (JsonException ex)
			{
				return (null, "not a workflow: " + ex.Message);
			}
			if (definition == null)
			{
				return (null, "the workflow is empty");
			}
			return Validate(kind, definition) is string error ? (null, error) : (definition, null);
		}

		public static string? Validate(string kind, TextWorkflowDefinition definition)
		{
			if (definition.Rules == null || definition.Rules.Count > MaxRules)
			{
				return $"a workflow has at most {MaxRules} rules";
			}
			IReadOnlyList<string> outcomes = TextOutcomes.For(kind);
			var names = new HashSet<string>(StringComparer.Ordinal);
			foreach (TextRuleDefinition rule in definition.Rules)
			{
				if (rule == null || String.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > MaxNameLength)
				{
					return $"every rule needs a name of at most {MaxNameLength} characters";
				}
				if (!names.Add(rule.Name))
				{
					return $"rule names are unique: {rule.Name}";
				}
				if (!outcomes.Contains(rule.Outcome, StringComparer.Ordinal))
				{
					return $"rule {rule.Name}: the outcome of a {kind} rule is one of {String.Join(", ", outcomes)}";
				}
				if (ExpressionError(rule.Expression) is string expressionError)
				{
					return $"rule {rule.Name}: {expressionError}";
				}
			}

			// compiles every rule alone and runs it once, so a rule that cannot run is refused, by name, before it is stored
			foreach (TextRuleDefinition rule in definition.Rules)
			{
				try
				{
					var engine = new RulesEngine.RulesEngine([ToWorkflow(kind, new TextWorkflowDefinition { Rules = [rule with { Enabled = true }] })], Settings());
					List<RuleResultTree> results = engine.ExecuteAllRulesAsync(kind, new RuleParameter(InputName, new TextRuleFacts { Kind = kind, Matches = 1, Block = 1 })).GetAwaiter().GetResult();
					if (results.FirstOrDefault(r => !String.IsNullOrEmpty(r.ExceptionMessage)) is RuleResultTree failed)
					{
						return $"rule {rule.Name}: {failed.ExceptionMessage}";
					}
				}
				catch (Exception ex)
				{
					return $"rule {rule.Name}: {ex.Message}";
				}
			}
			return null;
		}

		// Only the input fields, literals, comparisons and boolean operators
		private static string? ExpressionError(string? expression)
		{
			if (String.IsNullOrWhiteSpace(expression) || expression.Length > MaxExpressionLength)
			{
				return $"every rule needs an expression of at most {MaxExpressionLength} characters";
			}
			if (!s_allowedCharacters.IsMatch(expression))
			{
				return "the expression may use letters, digits, quotes, ( ) . ! = < > & | + - only";
			}
			string code = s_strings.Replace(expression, "\"\"");
			if (code.Count(c => c == '"') % 2 != 0)
			{
				return "a string is not closed";
			}
			foreach (System.Text.RegularExpressions.Match identifier in s_identifier.Matches(code))
			{
				if (!s_identifiers.Contains(identifier.Value))
				{
					return $"{identifier.Value} is not an input field; use input.{String.Join(", input.", Fields)}";
				}
			}
			return null;
		}

		public static string Serialize(TextWorkflowDefinition definition) => JsonSerializer.Serialize(definition, Json);
	}
}
