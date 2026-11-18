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
using GenOnlineService.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace GenOnlineService.Rules
{
	public sealed record RuleMatchView
	{
		public long Id { get; init; }
		public long RuleId { get; init; }
		public string RuleName { get; init; } = "";
		public int RuleVersion { get; init; }
		public Member Player { get; init; } = new Member();
		public string Trigger { get; init; } = "";
		public DateTime MatchedAt { get; init; }
		public string Mode { get; init; } = "";
		public IReadOnlyList<string> Actions { get; init; } = [];
		public string Outcome { get; init; } = "";

		// every field the rule read with its values, device ids as fingerprints
		public JsonElement Facts { get; init; }
		public string ReviewState { get; init; } = "";
		public long? ReviewedBy { get; init; }
		public DateTime? ReviewedAt { get; init; }
	}

	public sealed record RuleMatchListResponse
	{
		public IReadOnlyList<RuleMatchView> Matches { get; init; } = [];
	}

	public sealed record RuleMatchReviewRequest
	{
		// reviewed or dismissed
		public string? Review { get; init; }
	}

	// The matches of the automated rules and the review queue of the flagged ones, with anticheat.review as the other
	// review lists. Marking a match reviewed or dismissed is audited.
	[ApiController]
	[AllowSignInCookie]
	[Authorize(Policy = PermissionPolicies.Prefix + Permissions.AnticheatReview)]
	public sealed class RuleMatchesController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public RuleMatchesController(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		[EndpointSummary("Lists the matches of the automated rules, newest first, filtered by review state, rule, player, trigger and outcome.")]
		[ProducesResponseType<RuleMatchListResponse>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/staff/rules/matches")]
		public async Task<IActionResult> List([FromQuery] string? review, [FromQuery(Name = "rule_id")] long? ruleID, [FromQuery(Name = "user_id")] long? userID, [FromQuery] string? trigger, [FromQuery] string? outcome)
		{
			(OffsetPage page, Failure? invalid) = Paging.ReadOffset(Request);
			if (invalid != null)
			{
				return RestResults.Fail(invalid.Value);
			}
			review ??= RuleReviewStates.Pending;
			if (review != "all" && review is not (RuleReviewStates.None or RuleReviewStates.Pending or RuleReviewStates.Reviewed or RuleReviewStates.Dismissed))
			{
				return RestResults.Invalid("review");
			}
			if (trigger != null && !RuleTriggers.IsKnown(trigger))
			{
				return RestResults.Invalid("trigger");
			}

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			IQueryable<AutomatedRuleMatch> query = db.AutomatedRuleMatches.AsNoTracking();
			if (review != "all")
			{
				query = query.Where(m => m.ReviewState == review);
			}
			if (ruleID != null)
			{
				query = query.Where(m => m.RuleID == ruleID);
			}
			if (userID != null)
			{
				query = query.Where(m => m.UserID == userID);
			}
			if (trigger != null)
			{
				query = query.Where(m => m.Trigger == trigger);
			}
			if (outcome != null)
			{
				query = query.Where(m => m.Outcome == outcome);
			}
			int total = await query.CountAsync();
			List<AutomatedRuleMatch> rows = await query.OrderByDescending(m => m.ID).Skip(page.Skip).Take(page.PerPage).ToListAsync();
			Paging.WriteOffsetHeaders(HttpContext, page, total);
			return RestResults.Ok(new RuleMatchListResponse { Matches = await ViewsOf(db, rows) });
		}

		[EndpointSummary("Marks a flagged match reviewed or dismissed.")]
		[ProducesResponseType<RuleMatchView>(StatusCodes.Status200OK)]
		[HttpPatch("~/api/v1/staff/rules/matches/{id:long}")]
		public async Task<IActionResult> Review(long id, [FromBody] RuleMatchReviewRequest? body)
		{
			string? review = body?.Review;
			if (review is not (RuleReviewStates.Reviewed or RuleReviewStates.Dismissed))
			{
				return RestResults.Invalid("review");
			}
			Database.ModerationActor actor = CallerActors.Of(User);
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			DateTime now = DateTime.UtcNow;

			// only a pending match changes, one reviewed before answers as it is
			int changed = await db.AutomatedRuleMatches.Where(m => m.ID == id && m.ReviewState == RuleReviewStates.Pending)
				.ExecuteUpdateAsync(set => set.SetProperty(m => m.ReviewState, review).SetProperty(m => m.ReviewedBy, actor.UserID).SetProperty(m => m.ReviewedAt, now));
			if (changed > 0)
			{
				await Database.Moderation.AddAuditEntry(db, actor, RuleAudit.MatchReview, RuleAudit.MatchTargetKind, id, null, new { review });
			}
			AutomatedRuleMatch? match = await db.AutomatedRuleMatches.AsNoTracking().FirstOrDefaultAsync(m => m.ID == id);
			return match == null ? RestResults.NotFound("match") : RestResults.Ok((await ViewsOf(db, [match]))[0]);
		}

		private static async Task<List<RuleMatchView>> ViewsOf(AppDbContext db, List<AutomatedRuleMatch> rows)
		{
			List<long> ruleIDs = rows.Select(r => r.RuleID).Distinct().ToList();
			Dictionary<long, string> rules = await db.AutomatedRules.AsNoTracking().Where(r => ruleIDs.Contains(r.ID)).ToDictionaryAsync(r => r.ID, r => r.Name);
			Dictionary<long, (string Name, int Tag)> names = await RealtimeHub.NamesAsync(db, rows.Select(r => r.UserID));
			return rows.Select(m => new RuleMatchView
			{
				Id = m.ID,
				RuleId = m.RuleID,
				RuleName = rules.GetValueOrDefault(m.RuleID, ""),
				RuleVersion = m.RuleVersion,
				Player = RealtimeMembers.Of(m.UserID, names),
				Trigger = m.Trigger,
				MatchedAt = m.MatchedAt,
				Mode = m.Mode,
				Actions = m.Actions == null ? [] : m.Actions.Split(','),
				Outcome = m.Outcome,
				Facts = JsonDocument.Parse(RuleFields.CurrentFactsJson(m.Facts)).RootElement.Clone(),
				ReviewState = m.ReviewState,
				ReviewedBy = m.ReviewedBy,
				ReviewedAt = m.ReviewedAt
			}).ToList();
		}
	}
}
