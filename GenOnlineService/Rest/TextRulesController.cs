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
using GenOnlineService.OpenApi;
using GenOnlineService.Realtime;
using GenOnlineService.TextRules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace GenOnlineService.Rest
{
	// A new word needs every field; an edit sends only what changes
	public sealed record TextTermRequest
	{
		public string? Term { get; init; }
		public string? Language { get; init; }
		public string? Category { get; init; }
		public string? Severity { get; init; }
		public string? Match { get; init; }
	}

	public sealed record TextTermView
	{
		public long Id { get; init; }
		public string Term { get; init; } = "";
		public string Language { get; init; } = "";
		public string Category { get; init; } = "";
		public string Severity { get; init; } = "";
		public string Match { get; init; } = "";
		public string Source { get; init; } = "";
		public string UpdatedBy { get; init; } = "";
		public DateTime UpdatedAt { get; init; }

		public static TextTermView Of(TextRuleTerm t) => new TextTermView
		{
			Id = t.ID, Term = t.Term, Language = t.Language, Category = t.Category, Severity = t.Severity, Match = t.Match, Source = t.Source, UpdatedBy = t.UpdatedBy, UpdatedAt = t.UpdatedAt
		};
	}

	// total counts every match of the filters, terms holds at most the first 1000
	public sealed record TextTermListResponse
	{
		public int Total { get; init; }
		public IReadOnlyList<TextTermView> Terms { get; init; } = [];
	}

	public sealed record TextExceptionRequest
	{
		public string? Term { get; init; }
		public string? Language { get; init; }
	}

	public sealed record TextExceptionView
	{
		public long Id { get; init; }
		public string Term { get; init; } = "";
		public string Language { get; init; } = "";
		public string Source { get; init; } = "";
		public string UpdatedBy { get; init; } = "";
		public DateTime UpdatedAt { get; init; }

		public static TextExceptionView Of(TextRuleException e) => new TextExceptionView
		{
			Id = e.ID, Term = e.Term, Language = e.Language, Source = e.Source, UpdatedBy = e.UpdatedBy, UpdatedAt = e.UpdatedAt
		};
	}

	public sealed record TextExceptionListResponse
	{
		public IReadOnlyList<TextExceptionView> Exceptions { get; init; } = [];
	}

	// The rules of one input kind, the outcomes they may decide and the input fields their expressions may read
	public sealed record TextWorkflowView
	{
		public string Kind { get; init; } = "";
		public TextWorkflowDefinition Definition { get; init; } = new TextWorkflowDefinition();
		public IReadOnlyList<string> Outcomes { get; init; } = [];
		public IReadOnlyList<string> Fields { get; init; } = [];
		public string UpdatedBy { get; init; } = "";
		public DateTime? UpdatedAt { get; init; }
	}

	public sealed record TextWorkflowListResponse
	{
		public IReadOnlyList<TextWorkflowView> Workflows { get; init; } = [];
	}

	public sealed record TextTestRequest
	{
		public string? Kind { get; init; }
		public string? Text { get; init; }
	}

	public sealed record TextTestReading
	{
		public string Name { get; init; } = "";
		public string Text { get; init; } = "";
	}

	public sealed record TextTestHit
	{
		public string Term { get; init; } = "";
		public string Language { get; init; } = "";
		public string Category { get; init; } = "";
		public string Severity { get; init; } = "";
		public string Match { get; init; } = "";
		public string Reading { get; init; } = "";
		public int Start { get; init; }
		public int End { get; init; }
		public string Text { get; init; } = "";
	}

	// What the current rules of this copy decide for a text, how it was read and what it matched
	public sealed record TextTestResponse
	{
		public string Kind { get; init; } = "";
		public string Outcome { get; init; } = "";
		public string? Rule { get; init; }
		public string? Masked { get; init; }
		public IReadOnlyList<TextTestReading> Readings { get; init; } = [];
		public IReadOnlyList<TextTestHit> Hits { get; init; } = [];
		public TextRuleFacts Facts { get; init; } = new TextRuleFacts();
	}

	// The word lists, exceptions and rules of the text checks (S7) for the staff pages, textrules.manage only (admins).
	// Every change is audited and applies to this copy at once and to every other copy within the 5 s reload.
	[ApiController]
	[AllowSignInCookie]
	[Authorize(Policy = PermissionPolicies.Prefix + Permissions.TextRulesManage)]
	public sealed class TextRulesController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public TextRulesController(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		[EndpointSummary("Lists the words of the text rules, filtered by text, language, category and severity.")]
		[ProducesResponseType<TextTermListResponse>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/staff/text_rules/terms")]
		public async Task<IActionResult> Terms([FromQuery] string? query, [FromQuery] string? language, [FromQuery] string? category, [FromQuery] string? severity)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			List<TextRuleTerm> terms = await TextRulesAdmin.Terms(db, query, language, category, severity);
			return RestResults.Ok(new TextTermListResponse { Total = terms.Count, Terms = terms.Take(TextRulesAdmin.ListLimit).Select(TextTermView.Of).ToList() });
		}

		[EndpointSummary("Adds a word to the text rules.")]
		[ProducesResponseType<TextTermView>(StatusCodes.Status201Created)]
		[HttpPost("~/api/v1/staff/text_rules/terms")]
		public async Task<IActionResult> AddTerm([FromBody] TextTermRequest? body)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			Result<TextRuleTerm> added = await TextRulesAdmin.AddTerm(db, CallerActors.Of(User), body ?? new TextTermRequest());
			return added.Ok ? RestResults.Created(TextTermView.Of(added.Data!)) : RestResults.Fail(new Failure(added.Code!, added.Params!));
		}

		[EndpointSummary("Changes the text, language, category, severity or match of a word.")]
		[ProducesResponseType<TextTermView>(StatusCodes.Status200OK)]
		[HttpPatch("~/api/v1/staff/text_rules/terms/{id:long}")]
		public async Task<IActionResult> UpdateTerm(long id, [FromBody] TextTermRequest? body)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			Result<TextRuleTerm> updated = await TextRulesAdmin.UpdateTerm(db, CallerActors.Of(User), id, body ?? new TextTermRequest());
			return updated.Ok ? RestResults.Ok(TextTermView.Of(updated.Data!)) : RestResults.Fail(new Failure(updated.Code!, updated.Params!));
		}

		[EndpointSummary("Removes a word from the text rules.")]
		[ProducesResponseType(StatusCodes.Status204NoContent)]
		[HttpDelete("~/api/v1/staff/text_rules/terms/{id:long}")]
		public async Task<IActionResult> RemoveTerm(long id)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			Result<Empty> removed = await TextRulesAdmin.RemoveTerm(db, CallerActors.Of(User), id);
			return removed.Ok ? NoContent() : RestResults.Fail(new Failure(removed.Code!, removed.Params!));
		}

		[EndpointSummary("Lists the exception words that stay allowed although they contain a listed word.")]
		[ProducesResponseType<TextExceptionListResponse>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/staff/text_rules/exceptions")]
		public async Task<IActionResult> Exceptions()
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			return RestResults.Ok(new TextExceptionListResponse { Exceptions = (await TextRulesAdmin.Exceptions(db)).Select(TextExceptionView.Of).ToList() });
		}

		[EndpointSummary("Adds an exception word.")]
		[ProducesResponseType<TextExceptionView>(StatusCodes.Status201Created)]
		[HttpPost("~/api/v1/staff/text_rules/exceptions")]
		public async Task<IActionResult> AddException([FromBody] TextExceptionRequest? body)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			Result<TextRuleException> added = await TextRulesAdmin.AddException(db, CallerActors.Of(User), body ?? new TextExceptionRequest());
			return added.Ok ? RestResults.Created(TextExceptionView.Of(added.Data!)) : RestResults.Fail(new Failure(added.Code!, added.Params!));
		}

		[EndpointSummary("Removes an exception word.")]
		[ProducesResponseType(StatusCodes.Status204NoContent)]
		[HttpDelete("~/api/v1/staff/text_rules/exceptions/{id:long}")]
		public async Task<IActionResult> RemoveException(long id)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			Result<Empty> removed = await TextRulesAdmin.RemoveException(db, CallerActors.Of(User), id);
			return removed.Ok ? NoContent() : RestResults.Fail(new Failure(removed.Code!, removed.Params!));
		}

		[EndpointSummary("Lists the rule workflows of display names, lobby names and chat.")]
		[ProducesResponseType<TextWorkflowListResponse>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/staff/text_rules/workflows")]
		public async Task<IActionResult> Workflows()
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			return RestResults.Ok(new TextWorkflowListResponse { Workflows = (await TextRulesAdmin.Workflows(db)).Select(ViewOf).ToList() });
		}

		[EndpointSummary("Replaces the rule workflow of one input kind after it compiles and runs.")]
		[ProducesResponseType<TextWorkflowView>(StatusCodes.Status200OK)]
		[ApiRequestBody(typeof(TextWorkflowDefinition))]
		[HttpPut("~/api/v1/staff/text_rules/workflows/{kind}")]
		public async Task<IActionResult> SaveWorkflow(string kind, [FromBody] JsonElement body)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			Result<TextRuleWorkflow> saved = await TextRulesAdmin.SaveWorkflow(db, CallerActors.Of(User), kind, body.GetRawText());
			return saved.Ok ? RestResults.Ok(ViewOf(saved.Data!)) : RestResults.Fail(new Failure(saved.Code!, saved.Params!));
		}

		[EndpointSummary("Checks a text against the current rules: how it reads, what it matches and the outcome.")]
		[ProducesResponseType<TextTestResponse>(StatusCodes.Status200OK)]
		[HttpPost("~/api/v1/staff/text_rules/test")]
		public IActionResult Test([FromBody] TextTestRequest? body)
		{
			if (!TextKinds.IsKnown(body?.Kind))
			{
				return RestResults.Invalid("kind");
			}
			string text = body!.Text ?? String.Empty;
			if (text.Length == 0 || text.Length > TextRulesAdmin.TestTextMaxLength)
			{
				return RestResults.Invalid("text");
			}

			TextCheck check = TextChecks.Check(body.Kind!, text);
			return RestResults.Ok(new TextTestResponse
			{
				Kind = check.Kind,
				Outcome = check.Outcome,
				Rule = check.Rule,
				Masked = check.Masked,
				Readings = check.Readings.Select(r => new TextTestReading { Name = r.Name, Text = r.Text }).ToList(),
				Hits = check.Hits.Select(h => new TextTestHit
				{
					Term = h.Term.Term,
					Language = h.Term.Language,
					Category = h.Term.Category,
					Severity = h.Term.Severity,
					Match = h.Term.Match,
					Reading = h.Reading,
					Start = h.Start,
					End = h.End,
					Text = text.Substring(h.Start, Math.Min(h.End, text.Length) - h.Start)
				}).ToList(),
				Facts = check.Facts
			});
		}

		private static TextWorkflowView ViewOf(TextRuleWorkflow row)
		{
			(TextWorkflowDefinition? definition, _) = TextRuleWorkflows.Parse(row.Kind, row.Definition);
			return new TextWorkflowView
			{
				Kind = row.Kind,
				Definition = definition ?? TextRuleWorkflows.Defaults[row.Kind],
				Outcomes = TextOutcomes.For(row.Kind),
				Fields = TextRuleWorkflows.Fields,
				UpdatedBy = row.UpdatedBy,
				UpdatedAt = row.UpdatedAt == DateTime.UnixEpoch ? null : row.UpdatedAt
			};
		}
	}
}
