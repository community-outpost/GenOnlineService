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

using GenOnlineService.Failover;
using GenOnlineService.Realtime;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace GenOnlineService.Rules
{
	// One player a preview found, with the actions the rule would reach and the facts that decided it
	public sealed record RulePreviewHit(Int64 UserID, IReadOnlyList<string> Actions, string Facts);

	// Scanned counts the players read, Complete is true when every player was read within the limits
	public sealed record RulePreviewResult(int Scanned, int Matched, bool Complete, bool TimedOut, IReadOnlyList<RulePreviewHit> Players);

	// Who a rule would match now. It reads players in id order up to a row and a time limit, writes nothing and
	// takes no action, so staff see a rule's reach before they switch it on.
	public static class RulePreview
	{
		public const int BatchSize = 200;

		public static int MaximumPlayers { get; set; } = 5000;
		public static TimeSpan MaximumDuration { get; set; } = TimeSpan.FromSeconds(10);
		public static int PageSize { get; set; } = 25;

		public static async Task<RulePreviewResult> RunAsync(RuleEnvironment environment, RuleDocument document, CancellationToken cancellationToken = default)
		{
			CompiledRule rule = CompiledRule.Compile(0, 0, RuleModes.Watch, document);
			string trigger = rule.Trigger;
			var watch = Stopwatch.StartNew();
			var hits = new List<RulePreviewHit>();
			int scanned = 0;
			int matched = 0;
			long after = 0;
			await using AppDbContext db = await environment.Factory.CreateDbContextAsync(cancellationToken);
			DateTime now = DateTime.UtcNow;
			while (true)
			{
				List<long> ids = await db.Users.AsNoTracking().Where(u => u.ID > after).OrderBy(u => u.ID).Select(u => u.ID).Take(BatchSize).ToListAsync(cancellationToken);
				if (ids.Count == 0)
				{
					return new RulePreviewResult(scanned, matched, Complete: true, TimedOut: false, hits);
				}
				foreach (long id in ids)
				{
					if (watch.Elapsed >= MaximumDuration)
					{
						return new RulePreviewResult(scanned, matched, Complete: false, TimedOut: true, hits);
					}
					if (scanned >= MaximumPlayers)
					{
						return new RulePreviewResult(scanned, matched, Complete: false, TimedOut: false, hits);
					}
					var facts = new RuleFacts(db, environment, RuleEvent.Of(trigger, id), now);
					RuleResult result = await rule.RunAsync(facts);
					scanned++;
					if (result.Actions.Count > 0)
					{
						matched++;
						if (hits.Count < PageSize)
						{
							hits.Add(new RulePreviewHit(id, result.Actions.Select(a => a.Action!).Distinct().ToList(), RuleRunner.FactsJson(facts, result)));
						}
					}
				}
				after = ids[^1];
			}
		}
	}

	public static class RuleSweepStates
	{
		public const string Running = "running";
		public const string Completed = "completed";
		public const string Cancelled = "cancelled";

		// This copy lost the realtime lease during the sweep, which stopped there
		public const string Interrupted = "interrupted";
		public const string Failed = "failed";
	}

	// The progress of one sweep, as GET reads it
	public sealed class RuleSweepProgress
	{
		public Int64 RuleID { get; init; }
		public int RuleVersion { get; init; }
		public string State { get; set; } = RuleSweepStates.Running;
		public int Total { get; init; }
		public int Scanned { get; set; }
		public int Matched { get; set; }
		public string StartedBy { get; init; } = String.Empty;
		public DateTime StartedAt { get; init; }
		public DateTime? FinishedAt { get; set; }
		internal CancellationTokenSource Cancel { get; } = new();
	}

	// The manual run of one rule's current version over every player as trigger sweep. It runs only on the copy that
	// holds the realtime lease, in batches, and can be cancelled. Its progress lives on that copy.
	public static class RuleSweeps
	{
		public const int BatchSize = 100;

		private static readonly ILogger s_log = AppLog.For(typeof(RuleSweeps));
		private static readonly ConcurrentDictionary<Int64, RuleSweepProgress> s_sweeps = new();

		// The rules whose sweep is starting, claimed before any await so two starts never both pass
		private static readonly ConcurrentDictionary<Int64, byte> s_starting = new();

		// The lease check, which a test replaces
		internal static Func<bool> Serving { get; set; } = () => RealtimeFailover.Serving;

		// Runs before each batch, so a test can hold a sweep in progress
		internal static Func<int, Task>? BeforeBatch { get; set; }

		public static RuleSweepProgress? Get(Int64 ruleID) => s_sweeps.TryGetValue(ruleID, out RuleSweepProgress? progress) ? progress : null;

		public static async Task<Result<RuleSweepProgress>> StartAsync(RuleEnvironment environment, AppDbContext db, Database.ModerationActor actor, Int64 ruleID)
		{
			if (!Serving())
			{
				return Result.Fail(GenOnlineService.Rest.RestCodes.ServiceUnavailable);
			}
			if (!s_starting.TryAdd(ruleID, 0))
			{
				return Result.Fail(GenOnlineService.Rest.RestCodes.Conflict, new { What = "sweep" });
			}
			try
			{
				if (Get(ruleID) is RuleSweepProgress running && running.State == RuleSweepStates.Running)
				{
					return Result.Fail(GenOnlineService.Rest.RestCodes.Conflict, new { What = "sweep" });
				}
				return await StartClaimedAsync(environment, db, actor, ruleID);
			}
			finally
			{
				s_starting.TryRemove(ruleID, out _);
			}
		}

		private static async Task<Result<RuleSweepProgress>> StartClaimedAsync(RuleEnvironment environment, AppDbContext db, Database.ModerationActor actor, Int64 ruleID)
		{
			AutomatedRule? rule = await RuleStore.GetAsync(db, ruleID);
			if (rule == null || rule.ArchivedAt != null)
			{
				return Result.NotFound("rule");
			}
			AutomatedRuleVersion version = (await RuleStore.GetVersionAsync(db, ruleID, rule.CurrentVersion))!;
			CompiledRule compiled = CompiledRule.Compile(rule.ID, rule.CurrentVersion, rule.Mode, RuleDocuments.Deserialize(version.Document));
			var progress = new RuleSweepProgress
			{
				RuleID = ruleID,
				RuleVersion = rule.CurrentVersion,
				Total = await db.Users.CountAsync(),
				StartedBy = actor.Name,
				StartedAt = DateTime.UtcNow
			};
			await Database.Moderation.AddAuditEntry(db, actor, RuleAudit.SweepStart, RuleAudit.TargetKind, ruleID, null, new { version = rule.CurrentVersion, mode = rule.Mode, players = progress.Total });
			s_sweeps[ruleID] = progress;
			_ = Task.Run(() => RunAsync(environment, compiled, progress));
			return Result.Success(progress);
		}

		public static async Task<Result<RuleSweepProgress>> CancelAsync(AppDbContext db, Database.ModerationActor actor, Int64 ruleID)
		{
			if (Get(ruleID) is not RuleSweepProgress progress)
			{
				return Result.NotFound("sweep");
			}
			if (progress.State == RuleSweepStates.Running)
			{
				progress.Cancel.Cancel();
				await Database.Moderation.AddAuditEntry(db, actor, RuleAudit.SweepCancel, RuleAudit.TargetKind, ruleID, null, new { scanned = progress.Scanned, matched = progress.Matched });
			}
			return Result.Success(progress);
		}

		// Waits until the sweep of a rule left the running state, for tests
		internal static async Task WaitAsync(Int64 ruleID, TimeSpan timeout)
		{
			DateTime until = DateTime.UtcNow + timeout;
			while (Get(ruleID)?.State == RuleSweepStates.Running && DateTime.UtcNow < until)
			{
				await Task.Delay(10);
			}
		}

		private static async Task RunAsync(RuleEnvironment environment, CompiledRule rule, RuleSweepProgress progress)
		{
			CancellationToken cancel = progress.Cancel.Token;
			string final = RuleSweepStates.Completed;
			try
			{
				long after = 0;
				for (int batch = 0; ; batch++)
				{
					if (BeforeBatch != null)
					{
						await BeforeBatch(batch);
					}
					cancel.ThrowIfCancellationRequested();
					if (!Serving())
					{
						final = RuleSweepStates.Interrupted;
						return;
					}
					List<long> ids;
					await using (AppDbContext db = await environment.Factory.CreateDbContextAsync(cancel))
					{
						ids = await db.Users.AsNoTracking().Where(u => u.ID > after).OrderBy(u => u.ID).Select(u => u.ID).Take(BatchSize).ToListAsync(cancel);
					}
					if (ids.Count == 0)
					{
						return;
					}
					foreach (long id in ids)
					{
						cancel.ThrowIfCancellationRequested();
						progress.Matched += (await RuleRunner.ProcessAsync(environment, RuleEvent.Of(RuleTriggers.Sweep, id), [rule], cancel)).Count;
						progress.Scanned++;
					}
					after = ids[^1];
				}
			}
			catch (OperationCanceledException)
			{
				final = RuleSweepStates.Cancelled;
			}
			catch (Exception ex)
			{
				final = RuleSweepStates.Failed;
				s_log.LogError(ex, "The sweep of automated rule {RuleId} failed", rule.RuleID);
			}
			finally
			{
				progress.FinishedAt = DateTime.UtcNow;
				progress.State = final;
			}
		}
	}
}
