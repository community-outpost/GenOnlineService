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

using GenOnlineService.Api;
using GenOnlineService.Realtime;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Threading.Channels;

namespace GenOnlineService.Rules
{
	// Runs the rules after the events. A hook only queues its event, so a sign-in, a session, a join or a result never
	// waits for the rules or fails because of them, and a failed evaluation is logged and dropped.
	public static class RuleRunner
	{
		public const int QueueLength = 10000;

		private static readonly ILogger s_log = AppLog.For(typeof(RuleRunner));
		private static readonly Channel<RuleEvent> s_queue = Channel.CreateBounded<RuleEvent>(new BoundedChannelOptions(QueueLength) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
		private static readonly object s_startLock = new();

		private static RuleEnvironment? s_environment;
		private static Task? s_consumer;
		private static int s_pending;

		// A second match of a rule, player and trigger inside this window records nothing and acts on nothing
		public static TimeSpan DuplicateWindow { get; set; } = TimeSpan.FromMinutes(10);

		// An Enforce rule that already acted this many times in the last hour falls back to Flag
		public static int BreakerLimitPerHour { get; set; } = 20;

		public static RuleEnvironment? Environment => Volatile.Read(ref s_environment);

		public static void Configure(RuleEnvironment? environment) => Volatile.Write(ref s_environment, environment);

		// Called by the hooks. It never throws and never waits for the evaluation.
		public static void Raise(RuleEvent ev)
		{
			try
			{
				if (Environment == null || ev.UserID <= 0 || !RuleEngine.Current.Has(ev.Trigger))
				{
					return;
				}
				Interlocked.Increment(ref s_pending);
				if (!s_queue.Writer.TryWrite(ev))
				{
					Interlocked.Decrement(ref s_pending);
					s_log.LogWarning("The automated rules queue is full, a {Trigger} event of user {UserId} was dropped", ev.Trigger, ev.UserID);
					return;
				}
				EnsureConsumer();
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Queueing a {Trigger} event for the automated rules failed", ev.Trigger);
			}
		}

		// Waits until every queued event ran, for tests
		internal static async Task DrainAsync(TimeSpan timeout)
		{
			DateTime until = DateTime.UtcNow + timeout;
			while (Volatile.Read(ref s_pending) > 0 && DateTime.UtcNow < until)
			{
				await Task.Delay(10);
			}
		}

		private static void EnsureConsumer()
		{
			if (s_consumer != null)
			{
				return;
			}
			lock (s_startLock)
			{
				s_consumer ??= Task.Run(ConsumeAsync);
			}
		}

		private static async Task ConsumeAsync()
		{
			await foreach (RuleEvent ev in s_queue.Reader.ReadAllAsync())
			{
				try
				{
					RuleEnvironment? environment = Environment;
					if (environment != null)
					{
						await ProcessAsync(environment, ev, RuleEngine.Current.For(ev.Trigger));
					}
				}
				catch (Exception ex)
				{
					s_log.LogError(ex, "The automated rules failed on a {Trigger} event of user {UserId}", ev.Trigger, ev.UserID);
				}
				finally
				{
					Interlocked.Decrement(ref s_pending);
				}
			}
		}

		// Runs every rule on one event of one player and returns the matches recorded. A rule that fails is logged and the others still run.
		public static async Task<List<AutomatedRuleMatch>> ProcessAsync(RuleEnvironment environment, RuleEvent ev, IReadOnlyList<CompiledRule> rules, CancellationToken cancellationToken = default)
		{
			var recorded = new List<AutomatedRuleMatch>();
			if (rules.Count == 0)
			{
				return recorded;
			}
			await using AppDbContext db = await environment.Factory.CreateDbContextAsync(cancellationToken);
			var facts = new RuleFacts(db, environment, ev, DateTime.UtcNow);
			foreach (CompiledRule rule in rules)
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					RuleResult result = await rule.RunAsync(facts);
					if (result.Actions.Count == 0)
					{
						continue;
					}
					AutomatedRuleMatch? match = await ActAsync(environment, db, rule, ev, facts, result);
					if (match != null)
					{
						recorded.Add(match);
					}
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					s_log.LogError(ex, "Automated rule {RuleId} version {Version} failed on a {Trigger} event of user {UserId}", rule.RuleID, rule.Version, ev.Trigger, ev.UserID);
				}
			}
			return recorded;
		}

		// The facts a match keeps: every field the rule read, device ids as fingerprints
		public static string FactsJson(RuleFacts facts, RuleResult result)
		{
			var values = new SortedDictionary<string, object?[]>(StringComparer.Ordinal);
			foreach (string key in result.FieldsRead)
			{
				RuleField field = RuleFields.Find(key)!;
				values[key] = facts.Values.TryGetValue(key, out object?[]? read) ? read.Select(v => RuleFacts.Describe(field, v)).ToArray() : [];
			}
			return JsonSerializer.Serialize(values);
		}

		private static async Task<AutomatedRuleMatch?> ActAsync(RuleEnvironment environment, AppDbContext db, CompiledRule rule, RuleEvent ev, RuleFacts facts, RuleResult result)
		{
			DateTime now = DateTime.UtcNow;
			DateTime since = now - DuplicateWindow;
			if (await db.AutomatedRuleMatches.AsNoTracking().AnyAsync(m => m.RuleID == rule.RuleID && m.UserID == ev.UserID && m.Trigger == ev.Trigger && m.MatchedAt > since))
			{
				return null;
			}

			var match = new AutomatedRuleMatch
			{
				RuleID = rule.RuleID,
				RuleVersion = rule.Version,
				UserID = ev.UserID,
				Trigger = ev.Trigger,
				MatchedAt = now,
				Mode = rule.Mode,
				Facts = FactsJson(facts, result),
				ReviewState = RuleReviewStates.None
			};

			// the roles in the database decide, so a role granted on another copy counts at once
			if (StaffRoles.RankOf(await Database.Moderation.GetRoleNames(db, ev.UserID)) > 0)
			{
				match.Outcome = RuleOutcomes.StaffExempt;
				return await SaveAsync(db, match);
			}

			if (match.Mode == RuleModes.Enforce)
			{
				DateTime hourAgo = now.AddHours(-1);
				int acted = await db.AutomatedRuleMatches.AsNoTracking().CountAsync(m => m.RuleID == rule.RuleID && m.Outcome == RuleOutcomes.Enforced && m.MatchedAt > hourAgo);
				if (acted >= BreakerLimitPerHour)
				{
					await RuleStore.TripAsync(db, rule.Actor, rule.RuleID, acted, BreakerLimitPerHour);
					match.Mode = RuleModes.Flag;
				}
			}

			switch (match.Mode)
			{
				case RuleModes.Watch:
					match.Outcome = RuleOutcomes.Watched;
					break;
				case RuleModes.Flag:
					match.Outcome = RuleOutcomes.Flagged;
					match.ReviewState = RuleReviewStates.Pending;
					break;
				default:
					await EnforceAsync(environment, db, rule, ev, result, match);
					break;
			}
			return await SaveAsync(db, match);
		}

		// A ban goes first, so a kick is not needed after it. A player already banned or muted keeps that sanction.
		private static async Task EnforceAsync(RuleEnvironment environment, AppDbContext db, CompiledRule rule, RuleEvent ev, RuleResult result, AutomatedRuleMatch match)
		{
			var taken = new List<string>();
			RuleNode? Pick(string kind) => result.Actions.FirstOrDefault(a => a.Action == kind);

			bool banned = await Database.Moderation.GetActivePlayBan(db, ev.UserID) != null;
			if (Pick(RuleActionKinds.Ban) is RuleNode ban && !banned)
			{
				if (await ApplyAsync(environment, rule, ev, SanctionType.Ban, ban))
				{
					taken.Add(RuleActionKinds.Ban);
					banned = true;
				}
			}
			if (Pick(RuleActionKinds.Mute) is RuleNode mute && await Database.Moderation.GetActiveMute(db, ev.UserID) == null)
			{
				if (await ApplyAsync(environment, rule, ev, SanctionType.Mute, mute))
				{
					taken.Add(RuleActionKinds.Mute);
				}
			}
			if (Pick(RuleActionKinds.Kick) is RuleNode kick && !banned)
			{
				if (await ApplyAsync(environment, rule, ev, SanctionType.Kick, kick))
				{
					taken.Add(RuleActionKinds.Kick);
				}
			}

			bool flagged = Pick(RuleActionKinds.Flag) != null;
			if (flagged)
			{
				taken.Add(RuleActionKinds.Flag);
				match.ReviewState = RuleReviewStates.Pending;
			}
			match.Actions = taken.Count == 0 ? null : String.Join(',', taken);
			match.Outcome = taken.Any(a => a != RuleActionKinds.Flag) ? RuleOutcomes.Enforced : flagged ? RuleOutcomes.Flagged : RuleOutcomes.NothingToDo;
		}

		private static async Task<bool> ApplyAsync(RuleEnvironment environment, CompiledRule rule, RuleEvent ev, SanctionType type, RuleNode action)
		{
			Result<CreateSanctionResponse> applied = await SanctionActions.ApplyAsync(environment.Factory, rule.Actor, ev.UserID, type, action.DurationS, action.Reason);
			if (!applied.Ok && applied.Code != ErrorCodes.ModerationTargetOffline)
			{
				s_log.LogWarning("Automated rule {RuleId} could not {Action} user {UserId}: {Code}", rule.RuleID, action.Action, ev.UserID, applied.Code);
			}
			return applied.Ok;
		}

		private static async Task<AutomatedRuleMatch> SaveAsync(AppDbContext db, AutomatedRuleMatch match)
		{
			db.AutomatedRuleMatches.Add(match);
			try
			{
				await db.SaveChangesAsync();
			}
			finally
			{
				db.ChangeTracker.Clear();
			}
			return match;
		}
	}
}
