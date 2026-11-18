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

using GenOnlineService.Catalog;
using GenOnlineService.Realtime;
using GenOnlineService.Rest;
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService.Rules
{
	// The staff changes of the rules. Each change and its audit row are one save, then every copy reloads the rules.
	public static class RuleStore
	{
		public static async Task<List<AutomatedRule>> ListAsync(AppDbContext db, bool archived) =>
			await db.AutomatedRules.AsNoTracking().Where(r => (r.ArchivedAt != null) == archived).OrderBy(r => r.Name).ThenBy(r => r.ID).ToListAsync();

		public static async Task<AutomatedRule?> GetAsync(AppDbContext db, Int64 id) => await db.AutomatedRules.AsNoTracking().FirstOrDefaultAsync(r => r.ID == id);

		// Newest first, without the documents
		public static async Task<List<AutomatedRuleVersion>> VersionsAsync(AppDbContext db, Int64 id) =>
			await db.AutomatedRuleVersions.AsNoTracking().Where(v => v.RuleID == id).OrderByDescending(v => v.Number)
				.Select(v => new AutomatedRuleVersion { RuleID = v.RuleID, Number = v.Number, CreatedBy = v.CreatedBy, Comment = v.Comment, CreatedAt = v.CreatedAt })
				.ToListAsync();

		public static async Task<AutomatedRuleVersion?> GetVersionAsync(AppDbContext db, Int64 id, int number) =>
			await db.AutomatedRuleVersions.AsNoTracking().FirstOrDefaultAsync(v => v.RuleID == id && v.Number == number);

		// A refused document names every problem, so the editor can mark each node and field
		public static Failure Refused(IEnumerable<RuleProblem> problems) =>
			Result.Fail(ErrorCodes.InvalidArgument, new { Field = "document", Problems = problems.Select(p => new { p.Path, p.Reason }).ToList() });

		public static async Task<Result<AutomatedRule>> CreateAsync(AppDbContext db, Database.ModerationActor actor, string? name, string? description, string? mode, bool? enabled, string? documentJson, string? comment)
		{
			if (CleanName(name) is not string cleanName)
			{
				return Result.Invalid("name");
			}
			if (description != null && description.Length > AutomatedRule.DescriptionMaxLength)
			{
				return Result.Invalid("description");
			}
			mode ??= RuleModes.Watch;
			if (!RuleModes.IsKnown(mode))
			{
				return Result.Invalid("mode");
			}
			if (comment != null && comment.Length > AutomatedRuleVersion.CommentMaxLength)
			{
				return Result.Invalid("comment");
			}
			(RuleDocument? document, List<RuleProblem> problems) = RuleDocuments.Parse(documentJson);
			if (document == null)
			{
				return Refused(problems);
			}

			DateTime now = DateTime.UtcNow;
			var rule = new AutomatedRule
			{
				Name = cleanName,
				Description = String.IsNullOrWhiteSpace(description) ? null : description.Trim(),
				Mode = mode,
				Enabled = enabled ?? false,
				CurrentVersion = 1,
				CreatedBy = actor.Name,
				CreatedAt = now,
				UpdatedBy = actor.Name,
				UpdatedAt = now
			};
			await using (var transaction = await db.Database.BeginTransactionAsync())
			{
				db.AutomatedRules.Add(rule);
				await db.SaveChangesAsync();
				db.AutomatedRuleVersions.Add(VersionOf(rule.ID, 1, document, actor, comment, now));
				await db.SaveChangesAsync();
				db.ChangeTracker.Clear();
				await Database.Moderation.AddAuditEntry(db, actor, RuleAudit.Create, RuleAudit.TargetKind, rule.ID, null, new { name = rule.Name, mode = rule.Mode, enabled = rule.Enabled, version = 1, comment });
				await transaction.CommitAsync();
			}
			await SignalAsync(db);
			return Result.Success(rule);
		}

		// Each save is the next immutable version. A base version that is not the current one means another change won.
		public static async Task<Result<AutomatedRuleVersion>> SaveVersionAsync(AppDbContext db, Database.ModerationActor actor, Int64 id, string? documentJson, string? comment, int? baseVersion)
		{
			AutomatedRule? rule = await db.AutomatedRules.AsTracking().FirstOrDefaultAsync(r => r.ID == id);
			if (rule == null)
			{
				return Result.NotFound("rule");
			}
			if (baseVersion != null && baseVersion != rule.CurrentVersion)
			{
				return Result.Fail(RestCodes.Conflict, new { What = "version", Current = rule.CurrentVersion });
			}
			if (comment != null && comment.Length > AutomatedRuleVersion.CommentMaxLength)
			{
				return Result.Invalid("comment");
			}
			(RuleDocument? document, List<RuleProblem> problems) = RuleDocuments.Parse(documentJson);
			if (document == null)
			{
				return Refused(problems);
			}

			DateTime now = DateTime.UtcNow;
			int number = rule.CurrentVersion + 1;
			AutomatedRuleVersion version = VersionOf(id, number, document, actor, comment, now);
			rule.CurrentVersion = number;
			Touch(rule, actor, now);
			db.AutomatedRuleVersions.Add(version);
			try
			{
				await using var transaction = await db.Database.BeginTransactionAsync();
				await db.SaveChangesAsync();
				db.ChangeTracker.Clear();
				await Database.Moderation.AddAuditEntry(db, actor, RuleAudit.Version, RuleAudit.TargetKind, id, null, new { version = number, comment });
				await transaction.CommitAsync();
			}
			catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex))
			{
				db.ChangeTracker.Clear();
				return Result.Fail(RestCodes.Conflict, new { What = "version" });
			}
			await SignalAsync(db);
			return Result.Success(version);
		}

		// An omitted field keeps its value
		public static async Task<Result<AutomatedRule>> UpdateAsync(AppDbContext db, Database.ModerationActor actor, Int64 id, string? name, string? description, string? mode, bool? enabled)
		{
			AutomatedRule? rule = await db.AutomatedRules.AsTracking().FirstOrDefaultAsync(r => r.ID == id);
			if (rule == null)
			{
				return Result.NotFound("rule");
			}
			object before = Details(rule);
			if (name != null)
			{
				if (CleanName(name) is not string cleanName)
				{
					return Result.Invalid("name");
				}
				rule.Name = cleanName;
			}
			if (description != null)
			{
				if (description.Length > AutomatedRule.DescriptionMaxLength)
				{
					return Result.Invalid("description");
				}
				rule.Description = String.IsNullOrWhiteSpace(description) ? null : description.Trim();
			}
			if (mode != null)
			{
				if (!RuleModes.IsKnown(mode))
				{
					return Result.Invalid("mode");
				}
				rule.Mode = mode;
			}
			if (enabled != null)
			{
				rule.Enabled = enabled.Value;
			}
			return await ChangeAsync(db, actor, rule, RuleAudit.Update, new { from = before, to = Details(rule) });
		}

		// An archived rule never runs and leaves the list, restoring brings it back as it was
		public static async Task<Result<AutomatedRule>> ArchiveAsync(AppDbContext db, Database.ModerationActor actor, Int64 id, bool archive)
		{
			AutomatedRule? rule = await db.AutomatedRules.AsTracking().FirstOrDefaultAsync(r => r.ID == id);
			if (rule == null)
			{
				return Result.NotFound("rule");
			}
			rule.ArchivedAt = archive ? DateTime.UtcNow : null;
			return await ChangeAsync(db, actor, rule, archive ? RuleAudit.Archive : RuleAudit.Restore, Details(rule));
		}

		// The circuit breaker: an Enforce rule that acted too often falls back to Flag, once, with an audit row
		public static async Task<bool> TripAsync(AppDbContext db, Database.ModerationActor actor, Int64 id, int actions, int limit)
		{
			DateTime now = DateTime.UtcNow;
			int changed = await db.AutomatedRules.Where(r => r.ID == id && r.Mode == RuleModes.Enforce)
				.ExecuteUpdateAsync(set => set.SetProperty(r => r.Mode, RuleModes.Flag).SetProperty(r => r.UpdatedBy, actor.Name).SetProperty(r => r.UpdatedAt, now));
			if (changed == 0)
			{
				return false;
			}
			await Database.Moderation.AddAuditEntry(db, actor, RuleAudit.Breaker, RuleAudit.TargetKind, id, "acted more than the hourly limit",
				new { from = RuleModes.Enforce, to = RuleModes.Flag, actions, limit });
			await SignalAsync(db);
			return true;
		}

		private static async Task<Result<AutomatedRule>> ChangeAsync(AppDbContext db, Database.ModerationActor actor, AutomatedRule rule, string action, object details)
		{
			Touch(rule, actor, DateTime.UtcNow);
			await using (var transaction = await db.Database.BeginTransactionAsync())
			{
				await db.SaveChangesAsync();
				db.ChangeTracker.Clear();
				await Database.Moderation.AddAuditEntry(db, actor, action, RuleAudit.TargetKind, rule.ID, null, details);
				await transaction.CommitAsync();
			}
			await SignalAsync(db);
			return Result.Success(rule);
		}

		public static Task SignalAsync(AppDbContext db) => CatalogSignals.ChangedAsync(db, CatalogResources.Rules, DateTime.UtcNow.Ticks);

		private static AutomatedRuleVersion VersionOf(Int64 id, int number, RuleDocument document, Database.ModerationActor actor, string? comment, DateTime now) => new AutomatedRuleVersion
		{
			RuleID = id,
			Number = number,
			Document = RuleDocuments.Serialize(document),
			CreatedBy = actor.Name,
			Comment = String.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
			CreatedAt = now
		};

		private static void Touch(AutomatedRule rule, Database.ModerationActor actor, DateTime now)
		{
			rule.UpdatedBy = actor.Name;
			rule.UpdatedAt = now;
		}

		private static object Details(AutomatedRule r) => new { name = r.Name, mode = r.Mode, enabled = r.Enabled, archived = r.ArchivedAt != null };

		private static string? CleanName(string? name)
		{
			string text = (name ?? String.Empty).Trim();
			return text.Length == 0 || text.Length > AutomatedRule.NameMaxLength || text.Any(Char.IsControl) ? null : text;
		}
	}
}
