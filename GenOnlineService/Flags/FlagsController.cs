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
using Microsoft.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenOnlineService.Flags
{
	public sealed record DraftFlagResponse
	{
		public FlagDefinition Flag { get; init; } = new();
		public bool Archived { get; init; }
		public int Version { get; init; }

		// service code reads this flag, so it cannot be archived or leave the service scope
		public bool UsedByService { get; init; }

		// unchanged, added, changed or archived compared with the live revision
		public string Status { get; init; } = String.Empty;

		public string UpdatedBy { get; init; } = String.Empty;
		public DateTime UpdatedAt { get; init; }
	}

	public sealed record DraftResponse
	{
		// the live revision, 0 before the first publish
		public int Revision { get; init; }

		// the If-Match value publish expects
		public string DraftEtag { get; init; } = String.Empty;

		public IReadOnlyList<DraftFlagResponse> Flags { get; init; } = [];
	}

	public sealed record FlagChangesResponse
	{
		public int? From { get; init; }
		public int? To { get; init; }
		public IReadOnlyList<FlagChange> Changes { get; init; } = [];
	}

	public sealed record PublishRequest
	{
		public string? Comment { get; init; }
	}

	public sealed record RevisionResponse
	{
		public int Number { get; init; }
		public DateTime CreatedAt { get; init; }
		public string CreatedBy { get; init; } = String.Empty;
		public string? Comment { get; init; }
		public int? RollbackOf { get; init; }
		public bool Live { get; init; }

		// the flagd document, only on GET /api/v1/flags/revisions/:number (the export)
		public JsonNode? Document { get; init; }
	}

	// Staff manage the client and service flags: edit the draft, publish revisions, roll back, export and import (GitLab style, flags.manage)
	// Every route also takes the staff sign-in cookie, so the game config page calls them under the cookie rules
	[ApiController]
	[AllowSignInCookie]
	[Authorize(Policy = PermissionPolicies.Prefix + Permissions.FlagsManage)]
	public sealed class FlagsController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public FlagsController(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		[EndpointSummary("Reads the draft flags with their status against the live revision.")]
		[ProducesResponseType<DraftResponse>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/flags")]
		public async Task<IActionResult> List()
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			return Ok(await Draft(db));
		}

		[EndpointSummary("Adds a flag to the draft.")]
		[ProducesResponseType<DraftFlagResponse>(StatusCodes.Status201Created)]
		[ApiRequestBody(typeof(FlagDefinition))]
		[HttpPost("~/api/v1/flags")]
		public async Task<IActionResult> Create()
		{
			(FlagDefinition? flag, Failure? bad) = await ReadAsync<FlagDefinition>();
			if (bad != null) return RestResults.Fail(bad.Value);

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			(DraftFlag? created, Failure? failure) = await FlagStore.Create(db, flag!, CallerActors.Of(User));
			if (failure != null) return RestResults.Fail(failure.Value);

			Response.Headers[HeaderNames.ETag] = FlagStore.ETagOf(created!.Version);
			return RestResults.Created(await Describe(db, created));
		}

		[EndpointSummary("Reads one draft flag; the ETag is its version.")]
		[ProducesResponseType<DraftFlagResponse>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/flags/{key}")]
		public async Task<IActionResult> Get(string key)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			DraftFlag? flag = await FlagStore.GetDraft(db, key);
			if (flag == null) return RestResults.NotFound("flag");

			Response.Headers[HeaderNames.ETag] = FlagStore.ETagOf(flag.Version);
			return Ok(await Describe(db, flag));
		}

		[EndpointSummary("Changes one draft flag; If-Match takes its version.")]
		[ProducesResponseType<DraftFlagResponse>(StatusCodes.Status200OK)]
		[ApiRequestBody(typeof(FlagDefinition))]
		[HttpPut("~/api/v1/flags/{key}")]
		public async Task<IActionResult> Update(string key)
		{
			(int? expected, Failure? badTag) = ReadIfMatchVersion();
			if (badTag != null) return RestResults.Fail(badTag.Value);
			(FlagDefinition? flag, Failure? bad) = await ReadAsync<FlagDefinition>();
			if (bad != null) return RestResults.Fail(bad.Value);

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			(DraftFlag? updated, Failure? failure) = await FlagStore.Update(db, key, flag!, expected, CallerActors.Of(User));
			return await Answer(db, updated, failure);
		}

		// Archives: the field leaves the next revision and can be restored
		[EndpointSummary("Archives a draft flag, so it leaves the next revision.")]
		[ProducesResponseType<DraftFlagResponse>(StatusCodes.Status200OK)]
		[HttpDelete("~/api/v1/flags/{key}")]
		public async Task<IActionResult> Archive(string key)
		{
			(int? expected, Failure? badTag) = ReadIfMatchVersion();
			if (badTag != null) return RestResults.Fail(badTag.Value);

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			(DraftFlag? archived, Failure? failure) = await FlagStore.Archive(db, key, expected, CallerActors.Of(User));
			return await Answer(db, archived, failure);
		}

		[EndpointSummary("Brings an archived draft flag back.")]
		[ProducesResponseType<DraftFlagResponse>(StatusCodes.Status200OK)]
		[HttpPost("~/api/v1/flags/{key}/restore")]
		public async Task<IActionResult> Restore(string key)
		{
			(int? expected, Failure? badTag) = ReadIfMatchVersion();
			if (badTag != null) return RestResults.Fail(badTag.Value);

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			(DraftFlag? restored, Failure? failure) = await FlagStore.Restore(db, key, expected, CallerActors.Of(User));
			return await Answer(db, restored, failure);
		}

		// What publishing the draft would change
		[EndpointSummary("Lists what publishing the draft would change.")]
		[ProducesResponseType<FlagChangesResponse>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/flags/draft/changes")]
		public async Task<IActionResult> DraftChanges()
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			FlagRevision? current = await FlagStore.Current(db);
			JsonObject draft = FlagStore.DocumentOf(await db.Flags.AsNoTracking().ToListAsync());
			return Ok(new FlagChangesResponse
			{
				From = current?.Number,
				To = null,
				Changes = FlagStore.Diff(current == null ? null : JsonNode.Parse(current.Document), draft)
			});
		}

		// If-Match takes draft_etag from GET /api/v1/flags, so only the reviewed draft goes live
		[EndpointSummary("Publishes the draft as the next live revision; If-Match takes draft_etag.")]
		[ProducesResponseType<RevisionResponse>(StatusCodes.Status201Created)]
		[ApiRequestBody(typeof(PublishRequest))]
		[HttpPost("~/api/v1/flags/draft/publish")]
		public async Task<IActionResult> Publish()
		{
			(PublishRequest? body, Failure? bad) = await ReadAsync<PublishRequest>(optional: true);
			if (bad != null) return RestResults.Fail(bad.Value);

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			(FlagRevision? revision, Failure? failure) = await FlagStore.Publish(db, body?.Comment, IfMatch(), CallerActors.Of(User));
			if (failure != null) return RestResults.Fail(failure.Value);

			await GenOnlineService.Catalog.CatalogSignals.ChangedAsync(db, GenOnlineService.Catalog.CatalogResources.Flags, revision!.Number);
			return RestResults.Created(Summary(revision!, true));
		}

		[EndpointSummary("Throws the draft away and starts again from the live revision.")]
		[ProducesResponseType<DraftResponse>(StatusCodes.Status200OK)]
		[HttpPost("~/api/v1/flags/draft/reset")]
		public async Task<IActionResult> Reset()
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			(_, Failure? failure) = await FlagStore.ResetDraft(db, CallerActors.Of(User));
			if (failure != null) return RestResults.Fail(failure.Value);
			return Ok(await Draft(db));
		}

		// Takes an export (GET /api/v1/flags/revisions/:number, its document) or any flagd file as the new draft
		[EndpointSummary("Replaces the draft with a flagd file, such as an exported revision.")]
		[ProducesResponseType<DraftResponse>(StatusCodes.Status200OK)]
		[ApiRequestBody(typeof(JsonObject))]
		[HttpPost("~/api/v1/flags/draft/import")]
		public async Task<IActionResult> Import([FromBody] JsonNode? document)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			(_, Failure? failure) = await FlagStore.Import(db, document, CallerActors.Of(User));
			if (failure != null) return RestResults.Fail(failure.Value);
			return Ok(await Draft(db));
		}

		[EndpointSummary("Lists the published revisions, newest first.")]
		[ProducesResponseType<List<RevisionResponse>>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/flags/revisions")]
		public async Task<IActionResult> Revisions()
		{
			(OffsetPage page, Failure? bad) = Paging.ReadOffset(Request);
			if (bad != null) return RestResults.Fail(bad.Value);

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			(List<FlagRevision> revisions, int total) = await FlagStore.ListRevisions(db, page.Skip, page.PerPage);
			int? live = (await FlagStore.Current(db))?.Number;
			Paging.WriteOffsetHeaders(HttpContext, page, total);
			return Ok(revisions.Select(r => Summary(r, r.Number == live)).ToList());
		}

		[EndpointSummary("Reads one revision with its flagd document, the export.")]
		[ProducesResponseType<RevisionResponse>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/flags/revisions/{number:int}")]
		public async Task<IActionResult> Revision(int number)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			FlagRevision? revision = await FlagStore.GetRevision(db, number);
			if (revision == null) return RestResults.NotFound("revision");
			int? live = (await FlagStore.Current(db))?.Number;
			return Ok(Summary(revision, revision.Number == live) with { Document = JsonNode.Parse(revision.Document) });
		}

		[EndpointSummary("Lists what one revision changed against the one before it.")]
		[ProducesResponseType<FlagChangesResponse>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/flags/revisions/{number:int}/changes")]
		public async Task<IActionResult> RevisionChanges(int number)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			FlagRevision? revision = await FlagStore.GetRevision(db, number);
			if (revision == null) return RestResults.NotFound("revision");
			FlagRevision? previous = number > 1 ? await FlagStore.GetRevision(db, number - 1) : null;
			return Ok(new FlagChangesResponse
			{
				From = previous?.Number,
				To = number,
				Changes = FlagStore.Diff(previous == null ? null : JsonNode.Parse(previous.Document), JsonNode.Parse(revision.Document))
			});
		}

		[EndpointSummary("Publishes an earlier revision again as the next live revision.")]
		[ProducesResponseType<RevisionResponse>(StatusCodes.Status201Created)]
		[ApiRequestBody(typeof(PublishRequest))]
		[HttpPost("~/api/v1/flags/revisions/{number:int}/rollback")]
		public async Task<IActionResult> Rollback(int number)
		{
			(PublishRequest? body, Failure? bad) = await ReadAsync<PublishRequest>(optional: true);
			if (bad != null) return RestResults.Fail(bad.Value);

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			(FlagRevision? revision, Failure? failure) = await FlagStore.Rollback(db, number, body?.Comment, CallerActors.Of(User));
			if (failure != null) return RestResults.Fail(failure.Value);

			await GenOnlineService.Catalog.CatalogSignals.ChangedAsync(db, GenOnlineService.Catalog.CatalogResources.Flags, revision!.Number);
			return RestResults.Created(Summary(revision!, true));
		}

		private new IActionResult Ok(object body)
		{
			return new JsonResult(body, ApiResults.Json) { StatusCode = StatusCodes.Status200OK };
		}

		private async Task<IActionResult> Answer(AppDbContext db, DraftFlag? flag, Failure? failure)
		{
			if (failure != null) return RestResults.Fail(failure.Value);
			Response.Headers[HeaderNames.ETag] = FlagStore.ETagOf(flag!.Version);
			return Ok(await Describe(db, flag));
		}

		private async Task<DraftResponse> Draft(AppDbContext db)
		{
			List<FlagRow> rows = await db.Flags.AsNoTracking().OrderBy(f => f.Key).ToListAsync();
			FlagRevision? current = await FlagStore.Current(db);
			JsonObject? live = current == null ? null : JsonNode.Parse(current.Document)?["flags"] as JsonObject;
			string etag = FlagStore.DraftETag(rows);
			Response.Headers[HeaderNames.ETag] = etag;
			return new DraftResponse
			{
				Revision = current?.Number ?? 0,
				DraftEtag = etag,
				Flags = rows.Select(r => Describe(FlagStore.ToDraft(r), live)).ToList()
			};
		}

		private static async Task<DraftFlagResponse> Describe(AppDbContext db, DraftFlag flag)
		{
			FlagRevision? current = await FlagStore.Current(db);
			return Describe(flag, current == null ? null : JsonNode.Parse(current.Document)?["flags"] as JsonObject);
		}

		private static DraftFlagResponse Describe(DraftFlag flag, JsonObject? live)
		{
			string status;
			JsonNode? published = live?[flag.Flag.Key];
			if (flag.Archived)
			{
				status = "archived";
			}
			else if (published == null)
			{
				status = "added";
			}
			else
			{
				status = JsonNode.DeepEquals(published, Flagd.ToFlag(flag.Flag)) ? "unchanged" : "changed";
			}

			return new DraftFlagResponse
			{
				Flag = flag.Flag,
				Archived = flag.Archived,
				Version = flag.Version,
				UsedByService = ServiceFlags.IsUsed(flag.Flag.Key),
				Status = status,
				UpdatedBy = flag.UpdatedBy,
				UpdatedAt = flag.UpdatedAt
			};
		}

		private static RevisionResponse Summary(FlagRevision revision, bool live) => new()
		{
			Number = revision.Number,
			CreatedAt = revision.CreatedAt,
			CreatedBy = revision.CreatedBy,
			Comment = revision.Comment,
			RollbackOf = revision.RollbackOf,
			Live = live
		};

		private string? IfMatch()
		{
			string? value = Request.Headers[HeaderNames.IfMatch].ToString();
			return String.IsNullOrWhiteSpace(value) || value.Trim() == "*" ? null : value.Trim();
		}

		// If-Match "<version>" for one flag; absent means no check
		private (int? Version, Failure? Failure) ReadIfMatchVersion()
		{
			string? value = IfMatch();
			if (value == null)
			{
				return (null, null);
			}
			string inner = value.StartsWith("W/", StringComparison.Ordinal) ? value[2..] : value;
			inner = inner.Trim('"');
			return Int32.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out int version) ? (version, null) : (null, Result.Invalid("If-Match"));
		}

		private async Task<(T? Body, Failure? Failure)> ReadAsync<T>(bool optional = false) where T : class
		{
			try
			{
				using var reader = new StreamReader(Request.Body);
				string text = await reader.ReadToEndAsync();
				if (String.IsNullOrWhiteSpace(text))
				{
					return optional ? (null, null) : (null, Result.Invalid("body"));
				}
				T? body = JsonSerializer.Deserialize<T>(text, ApiResults.Json);
				return body == null && !optional ? (null, Result.Invalid("body")) : (body, null);
			}
			catch (JsonException ex)
			{
				string field = ex.Path is string path && path.StartsWith("$.", StringComparison.Ordinal) ? path.Substring(2) : "body";
				return (null, Result.Invalid(field));
			}
		}
	}
}
