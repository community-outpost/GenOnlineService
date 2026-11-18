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
using GenOnlineService.Rest;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace GenOnlineService.TextRules
{
	// The staff changes of the word lists, exceptions and rules (textrules.manage). Each change, its audit row and the new
	// revision are one save; this copy rebuilds at once, the other copies on their next reload.
	public static class TextRulesAdmin
	{
		public const int ListLimit = 1000;
		public const int TestTextMaxLength = 512;

		// A listed word as stored: trimmed, lower case, composed, one space between words
		public static string? CleanTerm(string? term)
		{
			string text = String.Join(' ', (term ?? String.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
			text = Names.NameKey.LowerRoot(text).Normalize(NormalizationForm.FormC);
			if (text.Length == 0 || text.Length > TextRuleTerm.TermMaxLength || text.Any(Char.IsControl))
			{
				return null;
			}
			return text;
		}

		public static async Task<List<TextRuleTerm>> Terms(AppDbContext db, string? query, string? language, string? category, string? severity)
		{
			IQueryable<TextRuleTerm> rows = db.TextRuleTerms.AsNoTracking();
			if (!String.IsNullOrWhiteSpace(language))
			{
				rows = rows.Where(t => t.Language == language);
			}
			if (!String.IsNullOrWhiteSpace(category))
			{
				rows = rows.Where(t => t.Category == category);
			}
			if (!String.IsNullOrWhiteSpace(severity))
			{
				rows = rows.Where(t => t.Severity == severity);
			}
			List<TextRuleTerm> terms = await rows.OrderBy(t => t.Language).ThenBy(t => t.Term).ToListAsync();
			string? needle = CleanTerm(query);
			return needle == null ? terms : terms.Where(t => t.Term.Contains(needle, StringComparison.Ordinal)).ToList();
		}

		public static async Task<Result<TextRuleTerm>> AddTerm(AppDbContext db, Database.ModerationActor actor, TextTermRequest request)
		{
			var term = new TextRuleTerm { Source = TextRuleSources.Staff };
			if (Apply(term, request, true) is Failure invalid)
			{
				return invalid;
			}
			term.UpdatedBy = actor.Name;
			term.UpdatedAt = DateTime.UtcNow;
			db.TextRuleTerms.Add(term);
			return await SaveAsync(db, actor, TextRuleAudit.TermAdd, () => Details(term), term);
		}

		public static async Task<Result<TextRuleTerm>> UpdateTerm(AppDbContext db, Database.ModerationActor actor, Int64 id, TextTermRequest request)
		{
			TextRuleTerm? term = await db.TextRuleTerms.AsTracking().FirstOrDefaultAsync(t => t.ID == id);
			if (term == null)
			{
				return Result.NotFound("term");
			}
			object before = Details(term);
			if (Apply(term, request, false) is Failure invalid)
			{
				return invalid;
			}
			term.UpdatedBy = actor.Name;
			term.UpdatedAt = DateTime.UtcNow;
			return await SaveAsync(db, actor, TextRuleAudit.TermUpdate, () => new { from = before, to = Details(term) }, term);
		}

		public static async Task<Result<Empty>> RemoveTerm(AppDbContext db, Database.ModerationActor actor, Int64 id)
		{
			TextRuleTerm? term = await db.TextRuleTerms.AsTracking().FirstOrDefaultAsync(t => t.ID == id);
			if (term == null)
			{
				return Result.NotFound("term");
			}
			db.TextRuleTerms.Remove(term);
			Result<TextRuleTerm> saved = await SaveAsync(db, actor, TextRuleAudit.TermRemove, () => Details(term), term);
			return saved.Ok ? Result.Success() : new Failure(saved.Code!, saved.Params!);
		}

		public static async Task<List<TextRuleException>> Exceptions(AppDbContext db) =>
			await db.TextRuleExceptions.AsNoTracking().OrderBy(e => e.Language).ThenBy(e => e.Term).ToListAsync();

		public static async Task<Result<TextRuleException>> AddException(AppDbContext db, Database.ModerationActor actor, TextExceptionRequest request)
		{
			string? term = CleanTerm(request.Term);
			if (term == null)
			{
				return Result.Invalid("term");
			}
			if (!TextLanguages.All.Contains(request.Language ?? String.Empty, StringComparer.Ordinal))
			{
				return Result.Invalid("language");
			}
			var exception = new TextRuleException { Term = term, Language = request.Language!, Source = TextRuleSources.Staff, UpdatedBy = actor.Name, UpdatedAt = DateTime.UtcNow };
			db.TextRuleExceptions.Add(exception);
			return await SaveAsync(db, actor, TextRuleAudit.ExceptionAdd, () => new { id = exception.ID, term = exception.Term, language = exception.Language }, exception);
		}

		public static async Task<Result<Empty>> RemoveException(AppDbContext db, Database.ModerationActor actor, Int64 id)
		{
			TextRuleException? exception = await db.TextRuleExceptions.AsTracking().FirstOrDefaultAsync(e => e.ID == id);
			if (exception == null)
			{
				return Result.NotFound("exception");
			}
			db.TextRuleExceptions.Remove(exception);
			Result<TextRuleException> saved = await SaveAsync(db, actor, TextRuleAudit.ExceptionRemove, () => new { id = exception.ID, term = exception.Term, language = exception.Language }, exception);
			return saved.Ok ? Result.Success() : new Failure(saved.Code!, saved.Params!);
		}

		// The stored workflows, with the default of a kind that has none
		public static async Task<List<TextRuleWorkflow>> Workflows(AppDbContext db)
		{
			Dictionary<string, TextRuleWorkflow> stored = await db.TextRuleWorkflows.AsNoTracking().ToDictionaryAsync(w => w.Kind, StringComparer.Ordinal);
			return TextKinds.All.Select(kind => stored.TryGetValue(kind, out TextRuleWorkflow? row)
				? row
				: new TextRuleWorkflow { Kind = kind, Definition = TextRuleWorkflows.Serialize(TextRuleWorkflows.Defaults[kind]), UpdatedBy = "default" }).ToList();
		}

		// The workflow is validated (it compiles and runs) before it is stored; the reason of a refusal is in params.reason
		public static async Task<Result<TextRuleWorkflow>> SaveWorkflow(AppDbContext db, Database.ModerationActor actor, string kind, string json)
		{
			if (!TextKinds.IsKnown(kind))
			{
				return Result.NotFound("workflow");
			}
			(TextWorkflowDefinition? definition, string? error) = TextRuleWorkflows.Parse(kind, json);
			if (definition == null)
			{
				return Result.Fail(ErrorCodes.InvalidArgument, new { Field = "rules", Reason = error });
			}

			string stored = TextRuleWorkflows.Serialize(definition);
			TextRuleWorkflow? row = await db.TextRuleWorkflows.AsTracking().FirstOrDefaultAsync(w => w.Kind == kind);
			if (row == null)
			{
				row = new TextRuleWorkflow { Kind = kind };
				db.TextRuleWorkflows.Add(row);
			}
			string previous = row.Definition;
			row.Definition = stored;
			row.UpdatedBy = actor.Name;
			row.UpdatedAt = DateTime.UtcNow;
			return await SaveAsync(db, actor, TextRuleAudit.WorkflowUpdate, () => new { kind, from = previous, to = stored }, row);
		}

		private static object Details(TextRuleTerm t) => new { id = t.ID, term = t.Term, language = t.Language, category = t.Category, severity = t.Severity, match = t.Match };

		// An omitted field keeps its value; a new word needs every field
		private static Failure? Apply(TextRuleTerm term, TextTermRequest request, bool create)
		{
			if (request.Term != null || create)
			{
				string? cleaned = CleanTerm(request.Term);
				if (cleaned == null)
				{
					return Result.Invalid("term");
				}
				term.Term = cleaned;
			}
			if (Pick(request.Language, term.Language, create, TextLanguages.All) is not string language)
			{
				return Result.Invalid("language");
			}
			if (Pick(request.Category, term.Category, create, TextCategories.All) is not string category)
			{
				return Result.Invalid("category");
			}
			if (Pick(request.Severity, term.Severity, create, TextSeverities.All) is not string severity)
			{
				return Result.Invalid("severity");
			}
			if (Pick(request.Match, term.Match, create, TextMatchModes.All) is not string match)
			{
				return Result.Invalid("match");
			}
			(term.Language, term.Category, term.Severity, term.Match) = (language, category, severity, match);
			return null;
		}

		private static string? Pick(string? requested, string current, bool create, IReadOnlyList<string> allowed)
		{
			if (requested == null)
			{
				return create ? null : current;
			}
			return allowed.Contains(requested, StringComparer.Ordinal) ? requested : null;
		}

		// The change, the revision and the audit row in one save, then this copy rebuilds; a taken word is a conflict
		private static async Task<Result<T>> SaveAsync<T>(AppDbContext db, Database.ModerationActor actor, string action, Func<object> details, T value) where T : class
		{
			await TextChecks.TouchAsync(db, actor.Name);
			try
			{
				// the audit row needs the id the first save gives a new word, a failed audit write undoes the change
				await using var transaction = await db.Database.BeginTransactionAsync();
				await db.SaveChangesAsync();
				await Database.Moderation.AddAuditEntry(db, actor, action, TextRuleAudit.TargetKind, null, null, details());
				await transaction.CommitAsync();
			}
			catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex))
			{
				db.ChangeTracker.Clear();
				return Result.Fail(RestCodes.Conflict, new { What = "term" });
			}
			await TextChecks.ReloadAsync(db);
			return Result.Success(value);
		}
	}
}
