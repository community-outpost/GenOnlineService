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
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace GenOnlineService.TextRules
{
	// The outcome of one text check: the decision, the rule that made it, the matches, the masked text when masking
	public sealed record TextCheck(string Kind, string Outcome, string? Rule, IReadOnlyList<TextHit> Hits, string? Masked, IReadOnlyList<TextReading> Readings, TextRuleFacts Facts);

	// One built word list with its rules; a reload builds a new one and swaps it in whole
	public sealed class TextRuleSet
	{
		private readonly TextMatcher m_names = new();
		private readonly TextMatcher m_text = new();
		private readonly TextMatcher m_arabizi = new();
		private readonly List<TextTermInfo> m_literals = [];

		public TextRuleSet(IEnumerable<TextTermInfo> terms, IEnumerable<TextTermInfo> exceptions, TextRuleWorkflows workflows, string revision)
		{
			Terms = terms.ToList();
			Exceptions = exceptions.ToList();
			Workflows = workflows;
			Revision = revision;

			foreach (TextTermInfo term in Terms)
			{
				if (term.Match == TextMatchModes.Literal)
				{
					m_literals.Add(term);
					continue;
				}
				bool wholeWord = term.Match == TextMatchModes.Word;
				List<FoldedChar> folded = TextFolding.Fold(term.Term);
				m_names.Add(term, TextFolding.Latin(TextFolding.Words(folded, true), false), wholeWord);
				m_text.Add(term, TextFolding.Latin(TextFolding.Words(folded, false), false), wholeWord);

				if (term.Language == TextLanguages.Arabic && TextFolding.IsArabicText(term.Term))
				{
					string skeleton = TextFolding.ArabicSkeleton(new string(folded.Select(c => c.C).Where(c => !Char.IsWhiteSpace(c)).ToArray()));
					if (skeleton.Length >= 2)
					{
						var runs = skeleton.Select((c, i) => new TextRun(c, 1, 0, 0, i == 0, i == skeleton.Length - 1)).ToList();
						m_arabizi.Add(term, new TextReading(TextFolding.ArabiziReading, runs), wholeWord || skeleton.Length < 3);
					}
				}
			}
			foreach (TextTermInfo exception in Exceptions)
			{
				List<FoldedChar> folded = TextFolding.Fold(exception.Term);
				m_names.Add(exception, TextFolding.Latin(TextFolding.Words(folded, true), false), false);
				m_text.Add(exception, TextFolding.Latin(TextFolding.Words(folded, false), false), false);
			}
			m_names.Build();
			m_text.Build();
			m_arabizi.Build();
		}

		public IReadOnlyList<TextTermInfo> Terms { get; }
		public IReadOnlyList<TextTermInfo> Exceptions { get; }
		public TextRuleWorkflows Workflows { get; }
		public string Revision { get; }

		public TextCheck Check(string kind, string text)
		{
			bool name = kind == TextKinds.DisplayName;
			List<List<FoldedChar>> words = TextFolding.Words(TextFolding.Fold(text), name);
			TextMatcher matcher = name ? m_names : m_text;

			var readings = new List<TextReading> { TextFolding.Latin(words, false) };
			if (TextFolding.NeedsAlternative(words))
			{
				readings.Add(TextFolding.Latin(words, true));
			}
			var hits = new List<TextHit>();
			string lower = Names.NameKey.LowerRoot(text);
			foreach (TextTermInfo literal in m_literals)
			{
				string word = Names.NameKey.LowerRoot(literal.Term);
				int at = word.Length == 0 ? -1 : lower.IndexOf(word, StringComparison.Ordinal);
				if (at >= 0)
				{
					// lower-casing keeps the length except for a dotted capital I, then the whole text is the span
					bool sameLength = lower.Length == text.Length;
					hits.Add(new TextHit(literal, "literal", sameLength ? at : 0, sameLength ? at + word.Length : text.Length));
				}
			}
			foreach (TextReading reading in readings)
			{
				hits.AddRange(matcher.Find(reading));
			}
			if (TextFolding.Arabizi(words) is TextReading arabizi)
			{
				readings.Add(arabizi);
				hits.AddRange(m_arabizi.Find(arabizi));
			}

			List<TextHit> distinct = hits.DistinctBy(h => (h.Term.ID, h.Term.Term, h.Term.Language, h.Start))
				// the longest match first, so a word and its longer form (sharmota, sharmoota) answer the one typed
				.OrderBy(h => h.Start).ThenByDescending(h => h.End).ThenByDescending(h => h.Term.Term.Length).ToList();
			TextRuleFacts facts = TextRuleFacts.Of(kind, distinct);
			TextRuleDecision decision = Workflows.Decide(facts);
			string? masked = decision.Outcome == TextOutcomes.Mask ? Mask(text, distinct) : null;
			return new TextCheck(kind, decision.Outcome, decision.Rule, distinct, masked, readings, facts);
		}

		// Every character of a rated match turns into a star, spaces stay
		private static string Mask(string text, IEnumerable<TextHit> hits)
		{
			char[] masked = text.ToCharArray();
			foreach (TextHit hit in hits.Where(h => !TextCategories.IsNameOnly(h.Term.Category)))
			{
				for (int i = hit.Start; i < hit.End && i < masked.Length; i++)
				{
					if (!Char.IsWhiteSpace(masked[i]))
					{
						masked[i] = '*';
					}
				}
			}
			return new string(masked);
		}
	}

	// The seed lists of data/textrules, embedded in the service, and the reserved name words of the plugin
	public static class TextRuleSeeds
	{
		private const string ResourcePrefix = "GenOnlineService.data.textrules.";

		// The seed version: 1 the service's own lists and the plugin's reserved words, 2 the LDNOOBW lists, their exceptions
		// and the stricter display name rules. An environment seeded with an older version gets what came after (TextChecks.SeedAsync).
		public const int Version = 2;

		// The seed words added after a seed version; a word in more than one list keeps its first entry, so the service's own wins
		public static IReadOnlyList<TextRuleTerm> Terms(int after = 0)
		{
			var terms = new List<(TextRuleTerm Term, int Since)>();
			foreach (string language in TextLanguages.All)
			{
				foreach (string[] fields in Lines(language + ".txt"))
				{
					terms.Add((new TextRuleTerm { Term = fields[0], Language = language, Category = fields[1], Severity = fields[2], Match = fields[3], Source = TextRuleSources.Seed }, 1));
				}
			}
			// the plugin's list: a word with anything but letters keeps the plugin's literal rule, as folding would read it otherwise
			foreach (string word in ProfileActions.ReservedNameWords)
			{
				terms.Add((new TextRuleTerm
				{
					Term = word,
					Language = TextLanguages.English,
					Category = word is "admin" or "staff" or "moderator" ? TextCategories.Impersonation : TextCategories.Reserved,
					Severity = TextSeverities.Block,
					Match = word.All(Char.IsLetter) ? TextMatchModes.Anywhere : TextMatchModes.Literal,
					Source = TextRuleSources.Reserved
				}, 1));
			}
			// the LDNOOBW lists (CC BY 4.0, LICENSE-THIRD-PARTY.md), as flag words
			foreach (string language in TextLanguages.All)
			{
				foreach (string[] fields in Lines("ldnoobw-" + language + ".txt"))
				{
					terms.Add((new TextRuleTerm { Term = fields[0], Language = language, Category = fields[1], Severity = fields[2], Match = fields[3], Source = TextRuleSources.Ldnoobw }, 2));
				}
			}
			return terms.DistinctBy(t => (t.Term.Language, t.Term.Term)).Where(t => t.Since > after).Select(t => t.Term).ToList();
		}

		// The seed exceptions added after a seed version; the third field is the version that added one, 1 when left out
		public static IReadOnlyList<TextRuleException> Exceptions(int after = 0) =>
			Lines("exceptions.txt")
				.Where(f => (f.Length > 2 ? Int32.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture) : 1) > after)
				.Select(f => new TextRuleException { Term = f[0], Language = f[1], Source = TextRuleSources.Seed }).ToList();

		private static IEnumerable<string[]> Lines(string file)
		{
			using Stream stream = typeof(TextRuleSeeds).Assembly.GetManifestResourceStream(ResourcePrefix + file)
				?? throw new InvalidOperationException("The text rules seed " + file + " is not embedded");
			using var reader = new StreamReader(stream, Encoding.UTF8);
			var lines = new List<string[]>();
			while (reader.ReadLine() is string line)
			{
				if (line.Length == 0 || line.StartsWith('#'))
				{
					continue;
				}
				lines.Add(line.Split('\t'));
			}
			return lines;
		}
	}

	// The text checks of display names, lobby names and chat (S7). The word lists and rules live in the database; every
	// service copy holds them built in memory and rebuilds them on the 5 s reload when text_rules.revision changes.
	// Before the first load the seed lists apply, so a copy never checks against nothing.
	public static class TextChecks
	{
		// The realtime code of a chat message the rules block (docs/realtime-protocol.md section 7)
		public const string ChatBlocked = "chat.blocked";

		// application_settings key whose value changes with every change of the lists or rules
		public const string RevisionKey = "text_rules.revision";

		public const string SeedVersionKey = "text_rules.seed_version";

		private static readonly ILogger s_log = AppLog.For(typeof(TextChecks));
		private static readonly Lazy<TextRuleSet> s_defaults = new(BuildDefaults);
		private static volatile TextRuleSet? s_current;
		private static readonly SemaphoreSlim s_reload = new(1, 1);

		public static TextRuleSet Current => s_current ?? s_defaults.Value;

		private static TextRuleSet BuildDefaults()
		{
			return new TextRuleSet(TextRuleSeeds.Terms().Select(Info), TextRuleSeeds.Exceptions().Select(Info), new TextRuleWorkflows(TextRuleWorkflows.Defaults), String.Empty);
		}

		public static TextTermInfo Info(TextRuleTerm t) => new TextTermInfo(t.ID, t.Term, t.Language, t.Category, t.Severity, t.Match);

		public static TextTermInfo Info(TextRuleException e) => new TextTermInfo(e.ID, e.Term, e.Language, String.Empty, String.Empty, TextMatchModes.Anywhere, true);

		public static TextCheck Check(string kind, string text) => Current.Check(kind, text);

		// The listed word a chosen display name is refused for, null when the rules allow the name. The name codes stay as
		// they were: the caller answers name.reserved with this word.
		public static string? RefusedNameWord(string name)
		{
			TextCheck check = Check(TextKinds.DisplayName, name);
			if (check.Outcome != TextOutcomes.Refuse)
			{
				return null;
			}
			return check.Hits.FirstOrDefault()?.Term.Term ?? check.Rule ?? TextOutcomes.Refuse;
		}

		// name.reserved with the listed word, as a refused display name answers
		public static Failure? LobbyNameRefusal(string name)
		{
			TextCheck check = Check(TextKinds.LobbyName, name);
			if (check.Outcome != TextOutcomes.Refuse)
			{
				return null;
			}
			return Result.Fail(ErrorCodes.NameReserved, new { Word = check.Hits.FirstOrDefault()?.Term.Term ?? check.Rule ?? TextOutcomes.Refuse });
		}

		// chat.blocked, or the content to store and deliver: the masked text when the rules mask it
		public static (Failure? Blocked, string Content) ChatContent(string content)
		{
			TextCheck check = Check(TextKinds.Chat, content);
			return check.Outcome switch
			{
				TextOutcomes.Block => (Result.Fail(ChatBlocked), content),
				TextOutcomes.Mask => (null, check.Masked ?? content),
				_ => (null, content)
			};
		}

		// The seed lists and default rules. A new database gets everything; one seeded with an older seed version gets only
		// what later versions added: the seed words and exceptions it does not hold (a seed word staff removed before stays
		// removed, as only entries of later versions are added) and a new default workflow where the stored one is still a
		// default of before (staff edits stay). One transaction writes it all: a second copy starting at the same time fails on
		// the key of the version row or of a word it adds too, and writes nothing.
		// Answers whether this call wrote anything.
		public static async Task<bool> SeedAsync(AppDbContext db)
		{
			bool seeded = await db.ApplicationSettings.AsNoTracking().AnyAsync(s => s.Key == RevisionKey);
			string? stored = await db.ApplicationSettings.AsNoTracking().Where(s => s.Key == SeedVersionKey).Select(s => s.Value).FirstOrDefaultAsync();
			// a database seeded before versions were kept holds version 1
			int from = !seeded ? 0 : stored == null ? 1 : Int32.Parse(stored, System.Globalization.CultureInfo.InvariantCulture);
			if (from >= TextRuleSeeds.Version)
			{
				return false;
			}

			DateTime now = DateTime.UtcNow;
			try
			{
				await using var transaction = await db.Database.BeginTransactionAsync();
				string version = TextRuleSeeds.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
				if (!seeded)
				{
					db.ApplicationSettings.Add(new ApplicationSetting { Key = RevisionKey, Value = NewRevision(), UpdatedBy = "startup", UpdatedAt = now });
				}
				if (stored == null)
				{
					db.ApplicationSettings.Add(new ApplicationSetting { Key = SeedVersionKey, Value = version, UpdatedBy = "startup", UpdatedAt = now });
					await db.SaveChangesAsync();
				}
				else
				{
					ApplicationSetting row = await db.ApplicationSettings.AsTracking().SingleAsync(s => s.Key == SeedVersionKey);
					row.Value = version;
					row.UpdatedAt = now;
				}

				HashSet<(string, string)> termsHeld = (await db.TextRuleTerms.AsNoTracking().Select(t => new { t.Language, t.Term }).ToListAsync()).Select(t => (t.Language, t.Term)).ToHashSet();
				HashSet<(string, string)> exceptionsHeld = (await db.TextRuleExceptions.AsNoTracking().Select(e => new { e.Language, e.Term }).ToListAsync()).Select(e => (e.Language, e.Term)).ToHashSet();
				List<TextRuleTerm> terms = TextRuleSeeds.Terms(from).Where(t => !termsHeld.Contains((t.Language, t.Term))).ToList();
				List<TextRuleException> exceptions = TextRuleSeeds.Exceptions(from).Where(e => !exceptionsHeld.Contains((e.Language, e.Term))).ToList();
				foreach (TextRuleTerm term in terms)
				{
					term.UpdatedBy = "startup";
					term.UpdatedAt = now;
					db.TextRuleTerms.Add(term);
				}
				foreach (TextRuleException exception in exceptions)
				{
					exception.UpdatedBy = "startup";
					exception.UpdatedAt = now;
					db.TextRuleExceptions.Add(exception);
				}

				var workflows = new List<string>();
				Dictionary<string, TextRuleWorkflow> rows = await db.TextRuleWorkflows.AsTracking().ToDictionaryAsync(w => w.Kind, StringComparer.Ordinal);
				foreach ((string kind, TextWorkflowDefinition definition) in TextRuleWorkflows.Defaults)
				{
					string current = TextRuleWorkflows.Serialize(definition);
					if (!rows.TryGetValue(kind, out TextRuleWorkflow? row))
					{
						db.TextRuleWorkflows.Add(new TextRuleWorkflow { Kind = kind, Definition = current, UpdatedBy = "startup", UpdatedAt = now });
						workflows.Add(kind);
					}
					else if (row.Definition != current && TextRuleWorkflows.PreviousDefaults.TryGetValue(kind, out IReadOnlyList<TextWorkflowDefinition>? previous)
						&& previous.Any(p => TextRuleWorkflows.Serialize(p) == row.Definition))
					{
						row.Definition = current;
						row.UpdatedBy = "startup";
						row.UpdatedAt = now;
						workflows.Add(kind);
					}
				}
				if (seeded)
				{
					await TouchAsync(db, "startup");
				}

				db.AuditLog.Add(new AuditLogEntry
				{
					CreatedAt = now,
					ActorKind = AuditActorKinds.Cli,
					Actor = "startup",
					Action = TextRuleAudit.Seed,
					TargetKind = TextRuleAudit.TargetKind,
					Source = "seed",
					Details = JsonSerializer.Serialize(new { from_version = from, to_version = TextRuleSeeds.Version, terms = terms.Count, exceptions = exceptions.Count, workflows })
				});
				await db.SaveChangesAsync();
				await transaction.CommitAsync();
				s_log.LogInformation("Seeded the text rules from version {FromVersion} to {ToVersion}: {TermCount} words, {ExceptionCount} exceptions, workflows {Workflows}",
					from, TextRuleSeeds.Version, terms.Count, exceptions.Count, String.Join(", ", workflows));
			}
			catch (Exception ex) when (DbHelpers.IsUniqueViolation(ex))
			{
				return false;
			}
			finally
			{
				db.ChangeTracker.Clear();
			}
			return true;
		}

		public static string NewRevision() => JsonSerializer.Serialize(Guid.NewGuid().ToString("N"));

		// Marks the lists or rules changed; the caller's SaveChanges writes it with the change
		public static async Task TouchAsync(AppDbContext db, string updatedBy)
		{
			ApplicationSetting? row = await db.ApplicationSettings.AsTracking().FirstOrDefaultAsync(s => s.Key == RevisionKey);
			if (row == null)
			{
				db.ApplicationSettings.Add(new ApplicationSetting { Key = RevisionKey, Value = NewRevision(), UpdatedBy = updatedBy, UpdatedAt = DateTime.UtcNow });
			}
			else
			{
				row.Value = NewRevision();
				row.UpdatedBy = updatedBy;
				row.UpdatedAt = DateTime.UtcNow;
			}
		}

		// Rebuilds the lists when the stored revision differs from the one held; an unseeded database keeps the seed lists
		public static async Task ReloadAsync(AppDbContext db, bool force = false)
		{
			await s_reload.WaitAsync();
			try
			{
				string? revision = await db.ApplicationSettings.AsNoTracking().Where(s => s.Key == RevisionKey).Select(s => s.Value).FirstOrDefaultAsync();
				if (revision == null)
				{
					s_current = null;
					return;
				}
				if (!force && s_current != null && s_current.Revision == revision)
				{
					return;
				}

				List<TextRuleTerm> terms = await db.TextRuleTerms.AsNoTracking().OrderBy(t => t.ID).ToListAsync();
				List<TextRuleException> exceptions = await db.TextRuleExceptions.AsNoTracking().OrderBy(e => e.ID).ToListAsync();
				List<TextRuleWorkflow> stored = await db.TextRuleWorkflows.AsNoTracking().ToListAsync();

				var definitions = new Dictionary<string, TextWorkflowDefinition>(TextRuleWorkflows.Defaults, StringComparer.Ordinal);
				foreach (TextRuleWorkflow row in stored)
				{
					(TextWorkflowDefinition? definition, string? error) = TextRuleWorkflows.Parse(row.Kind, row.Definition);
					if (definition == null)
					{
						s_log.LogError("The stored text rules of {Kind} are invalid ({Error}); the default rules apply", row.Kind, error);
						continue;
					}
					definitions[row.Kind] = definition;
				}

				s_current = new TextRuleSet(terms.Select(Info), exceptions.Select(Info), new TextRuleWorkflows(definitions), revision);
			}
			finally
			{
				s_reload.Release();
			}
		}

		// Back to the seed lists, for tests that changed the process-wide lists
		public static void UseDefaults() => s_current = null;
	}

	public static class TextRuleAudit
	{
		public const string TargetKind = "text_rules";
		public const string Seed = "text_rules_seed";
		public const string TermAdd = "text_term_add";
		public const string TermUpdate = "text_term_update";
		public const string TermRemove = "text_term_remove";
		public const string ExceptionAdd = "text_exception_add";
		public const string ExceptionRemove = "text_exception_remove";
		public const string WorkflowUpdate = "text_workflow_update";
	}
}
