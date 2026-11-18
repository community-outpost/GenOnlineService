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
using GenOnlineService.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace GenOnlineService.Rules
{
	public sealed record RuleCreateRequest
	{
		public string? Name { get; init; }
		public string? Description { get; init; }

		// watch when omitted
		public string? Mode { get; init; }

		// false when omitted, so a new rule runs only once staff switch it on
		public bool? Enabled { get; init; }
		public RuleDocument? Document { get; init; }
		public string? Comment { get; init; }
	}

	public sealed record RuleVersionRequest
	{
		public RuleDocument? Document { get; init; }
		public string? Comment { get; init; }

		// the version the editor started from, so another save meanwhile is refused as a conflict
		public int? BaseVersion { get; init; }
	}

	// An omitted field keeps its value
	public sealed record RuleUpdateRequest
	{
		public string? Name { get; init; }
		public string? Description { get; init; }
		public string? Mode { get; init; }
		public bool? Enabled { get; init; }
	}

	public sealed record RuleValidateRequest
	{
		public RuleDocument? Document { get; init; }
	}

	public sealed record RuleProblemView
	{
		public string Path { get; init; } = "";
		public string Reason { get; init; } = "";
	}

	public sealed record RuleValidateResponse
	{
		public bool Valid { get; init; }
		public IReadOnlyList<RuleProblemView> Problems { get; init; } = [];
	}

	public sealed record RuleView
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public string? Description { get; init; }
		public string Mode { get; init; } = "";
		public bool Enabled { get; init; }
		public int CurrentVersion { get; init; }
		public bool Archived { get; init; }

		// the trigger of the current version
		public string Trigger { get; init; } = "";
		public string CreatedBy { get; init; } = "";
		public DateTime CreatedAt { get; init; }
		public string UpdatedBy { get; init; } = "";
		public DateTime UpdatedAt { get; init; }

		public static RuleView Of(AutomatedRule r, string trigger) => new RuleView
		{
			Id = r.ID, Name = r.Name, Description = r.Description, Mode = r.Mode, Enabled = r.Enabled, CurrentVersion = r.CurrentVersion, Archived = r.ArchivedAt != null,
			Trigger = trigger, CreatedBy = r.CreatedBy, CreatedAt = r.CreatedAt, UpdatedBy = r.UpdatedBy, UpdatedAt = r.UpdatedAt
		};
	}

	public sealed record RuleListResponse
	{
		public IReadOnlyList<RuleView> Rules { get; init; } = [];
	}

	public sealed record RuleVersionView
	{
		public int Number { get; init; }
		public string CreatedBy { get; init; } = "";
		public string? Comment { get; init; }
		public DateTime CreatedAt { get; init; }

		// only on the read of one version and of the rule's current version
		public RuleDocument? Document { get; init; }

		public static RuleVersionView Of(AutomatedRuleVersion v, bool document) => new RuleVersionView
		{
			Number = v.Number, CreatedBy = v.CreatedBy, Comment = v.Comment, CreatedAt = v.CreatedAt, Document = document ? RuleDocuments.Deserialize(v.Document) : null
		};
	}

	// A rule with the document of its current version and every version newest first
	public sealed record RuleDetailResponse
	{
		public RuleView Rule { get; init; } = new RuleView();
		public RuleDocument Document { get; init; } = new RuleDocument();
		public IReadOnlyList<RuleVersionView> Versions { get; init; } = [];
	}

	public sealed record RuleFieldView
	{
		public string Key { get; init; } = "";
		public string Type { get; init; } = "";
		public string Label { get; init; } = "";
		public string Description { get; init; } = "";
		public IReadOnlyList<string> Triggers { get; init; } = [];
		public IReadOnlyList<string> Operators { get; init; } = [];
	}

	// What a rule document may use: the fields with their operators and triggers, the actions and the modes
	public sealed record RuleCatalogResponse
	{
		public IReadOnlyList<RuleFieldView> Fields { get; init; } = [];
		public IReadOnlyList<string> Triggers { get; init; } = [];
		public IReadOnlyList<string> Actions { get; init; } = [];
		public IReadOnlyList<string> Modes { get; init; } = [];
	}

	// The automated rules for the staff pages, rules.manage only. Every change is audited, saves a new immutable
	// version when the logic changes, and applies to every copy at once through CatalogSignals.
	[ApiController]
	[AllowSignInCookie]
	[Authorize(Policy = PermissionPolicies.Prefix + Permissions.RulesManage)]
	public sealed class RulesController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public RulesController(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		[EndpointSummary("Lists the automated rules, or the archived ones.")]
		[ProducesResponseType<RuleListResponse>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/staff/rules")]
		public async Task<IActionResult> List([FromQuery] bool archived = false)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			List<AutomatedRule> rules = await RuleStore.ListAsync(db, archived);
			Dictionary<long, string> triggers = await TriggersOf(db, rules);
			return RestResults.Ok(new RuleListResponse { Rules = rules.Select(r => RuleView.Of(r, triggers.GetValueOrDefault(r.ID, ""))).ToList() });
		}

		[EndpointSummary("Reads a rule with the document of its current version and every version.")]
		[ProducesResponseType<RuleDetailResponse>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/staff/rules/{id:long}")]
		public async Task<IActionResult> Get(long id)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			return await DetailAsync(db, id) is RuleDetailResponse detail ? RestResults.Ok(detail) : RestResults.NotFound("rule");
		}

		[EndpointSummary("Reads one version of a rule with its document.")]
		[ProducesResponseType<RuleVersionView>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/staff/rules/{id:long}/versions/{number:int}")]
		public async Task<IActionResult> GetVersion(long id, int number)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			AutomatedRuleVersion? version = await RuleStore.GetVersionAsync(db, id, number);
			return version == null ? RestResults.NotFound("version") : RestResults.Ok(RuleVersionView.Of(version, document: true));
		}

		[EndpointSummary("Creates a rule with its first version after the document validates.")]
		[ProducesResponseType<RuleDetailResponse>(StatusCodes.Status201Created)]
		[ApiRequestBody(typeof(RuleCreateRequest))]
		[HttpPost("~/api/v1/staff/rules")]
		public async Task<IActionResult> Create([FromBody] JsonElement body)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			Result<AutomatedRule> created = await RuleStore.CreateAsync(db, CallerActors.Of(User), Text(body, "name"), Text(body, "description"), Text(body, "mode"), Flag(body, "enabled"), DocumentOf(body), Text(body, "comment"));
			if (!created.Ok)
			{
				return RestResults.Fail(new Failure(created.Code!, created.Params!));
			}
			return RestResults.Created((await DetailAsync(db, created.Data!.ID))!);
		}

		[EndpointSummary("Saves a new version of a rule after the document validates.")]
		[ProducesResponseType<RuleDetailResponse>(StatusCodes.Status201Created)]
		[ApiRequestBody(typeof(RuleVersionRequest))]
		[HttpPost("~/api/v1/staff/rules/{id:long}/versions")]
		public async Task<IActionResult> SaveVersion(long id, [FromBody] JsonElement body)
		{
			int? baseVersion = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("base_version", out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			Result<AutomatedRuleVersion> saved = await RuleStore.SaveVersionAsync(db, CallerActors.Of(User), id, DocumentOf(body), Text(body, "comment"), baseVersion);
			if (!saved.Ok)
			{
				return RestResults.Fail(new Failure(saved.Code!, saved.Params!));
			}
			return RestResults.Created((await DetailAsync(db, id))!);
		}

		[EndpointSummary("Changes the name, description, mode or enabled state of a rule.")]
		[ProducesResponseType<RuleView>(StatusCodes.Status200OK)]
		[HttpPatch("~/api/v1/staff/rules/{id:long}")]
		public async Task<IActionResult> Update(long id, [FromBody] RuleUpdateRequest? body)
		{
			body ??= new RuleUpdateRequest();
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			return ViewResult(db, await RuleStore.UpdateAsync(db, CallerActors.Of(User), id, body.Name, body.Description, body.Mode, body.Enabled));
		}

		[EndpointSummary("Archives a rule, so it never runs and leaves the list.")]
		[ProducesResponseType<RuleView>(StatusCodes.Status200OK)]
		[HttpPost("~/api/v1/staff/rules/{id:long}/archive")]
		public async Task<IActionResult> Archive(long id)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			return ViewResult(db, await RuleStore.ArchiveAsync(db, CallerActors.Of(User), id, archive: true));
		}

		[EndpointSummary("Restores an archived rule as it was.")]
		[ProducesResponseType<RuleView>(StatusCodes.Status200OK)]
		[HttpPost("~/api/v1/staff/rules/{id:long}/restore")]
		public async Task<IActionResult> Restore(long id)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			return ViewResult(db, await RuleStore.ArchiveAsync(db, CallerActors.Of(User), id, archive: false));
		}

		[EndpointSummary("Checks a rule document without saving it and lists every problem.")]
		[ProducesResponseType<RuleValidateResponse>(StatusCodes.Status200OK)]
		[ApiRequestBody(typeof(RuleValidateRequest))]
		[HttpPost("~/api/v1/staff/rules/validate")]
		public IActionResult Validate([FromBody] JsonElement body)
		{
			string? json = DocumentOf(body);
			List<RuleProblem> problems = RuleDocuments.Parse(json).Problems;
			return RestResults.Ok(new RuleValidateResponse { Valid = problems.Count == 0, Problems = problems.Select(p => new RuleProblemView { Path = p.Path, Reason = p.Reason }).ToList() });
		}

		[EndpointSummary("Lists the fields a rule can read with their operators and triggers, the actions and the modes.")]
		[ProducesResponseType<RuleCatalogResponse>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/staff/rules/fields")]
		public IActionResult Fields()
		{
			return RestResults.Ok(new RuleCatalogResponse
			{
				Fields = RuleFields.All.Select(f => new RuleFieldView { Key = f.Key, Type = f.Type, Label = f.Label, Description = f.Description, Triggers = f.Triggers, Operators = f.Operators }).ToList(),
				Triggers = RuleTriggers.All,
				Actions = RuleActionKinds.All,
				Modes = RuleModes.All
			});
		}

		private IActionResult ViewResult(AppDbContext db, Result<AutomatedRule> result)
		{
			if (!result.Ok)
			{
				return RestResults.Fail(new Failure(result.Code!, result.Params!));
			}
			return RestResults.Ok(RuleView.Of(result.Data!, TriggerOfVersion(db, result.Data!)));
		}

		private static string TriggerOfVersion(AppDbContext db, AutomatedRule rule)
		{
			string? json = db.AutomatedRuleVersions.AsNoTracking().Where(v => v.RuleID == rule.ID && v.Number == rule.CurrentVersion).Select(v => v.Document).FirstOrDefault();
			return json == null ? "" : RuleDocuments.TriggerOf(RuleDocuments.Deserialize(json));
		}

		private static async Task<Dictionary<long, string>> TriggersOf(AppDbContext db, List<AutomatedRule> rules)
		{
			List<long> ids = rules.Select(r => r.ID).ToList();
			var documents = await db.AutomatedRuleVersions.AsNoTracking()
				.Where(v => ids.Contains(v.RuleID))
				.Join(db.AutomatedRules.AsNoTracking(), v => new { v.RuleID, v.Number }, r => new { RuleID = r.ID, Number = r.CurrentVersion }, (v, r) => new { v.RuleID, v.Document })
				.ToListAsync();
			return documents.ToDictionary(d => d.RuleID, d => RuleDocuments.TriggerOf(RuleDocuments.Deserialize(d.Document)));
		}

		private static async Task<RuleDetailResponse?> DetailAsync(AppDbContext db, long id)
		{
			AutomatedRule? rule = await RuleStore.GetAsync(db, id);
			if (rule == null)
			{
				return null;
			}
			AutomatedRuleVersion current = (await RuleStore.GetVersionAsync(db, id, rule.CurrentVersion))!;
			RuleDocument document = RuleDocuments.Deserialize(current.Document);
			return new RuleDetailResponse
			{
				Rule = RuleView.Of(rule, RuleDocuments.TriggerOf(document)),
				Document = document,
				Versions = (await RuleStore.VersionsAsync(db, id)).Select(v => RuleVersionView.Of(v, document: false)).ToList()
			};
		}

		private static string? Text(JsonElement body, string name) =>
			body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

		private static bool? Flag(JsonElement body, string name) =>
			body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

		// The document as its own JSON text, so its problems carry paths inside the document
		private static string? DocumentOf(JsonElement body) =>
			body.ValueKind == JsonValueKind.Object && body.TryGetProperty("document", out JsonElement value) && value.ValueKind == JsonValueKind.Object ? value.GetRawText() : null;
	}
}
