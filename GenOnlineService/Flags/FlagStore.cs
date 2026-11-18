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
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// The draft: one row per field staff edit; publishing copies the live rows into a revision
public class FlagRow
{
	public string Key { get; set; } = String.Empty;

	// A FlagDefinition as JSON
	public string Definition { get; set; } = "{}";

	// Archived fields leave the next revision and can be restored
	public DateTime? ArchivedAt { get; set; }

	// Moves on every change; If-Match compares it
	public int Version { get; set; } = 1;

	public string UpdatedBy { get; set; } = String.Empty;
	public DateTime UpdatedAt { get; set; } = DateTime.UnixEpoch;
}

// An immutable published set of flags; the highest number is live
public class FlagRevision
{
	public int Number { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;
	public string CreatedBy { get; set; } = String.Empty;
	public string? Comment { get; set; }

	// The flagd document ({"$schema", "flags"}) with our schema in each flag's metadata
	public string Document { get; set; } = "{}";

	// Set when this revision brought back an older one
	public int? RollbackOf { get; set; }
}

public class FlagRowConfiguration : IEntityTypeConfiguration<FlagRow>
{
	public void Configure(EntityTypeBuilder<FlagRow> builder)
	{
		builder.ToTable("flags");
		builder.HasKey(e => e.Key);
		builder.Property(e => e.Key).HasColumnName("key").HasMaxLength(GenOnlineService.Flags.FlagRules.MaximumKeyLength);
		builder.Property(e => e.Definition).HasColumnName("definition");
		builder.Property(e => e.ArchivedAt).HasColumnName("archived_at");
		builder.Property(e => e.Version).HasColumnName("version").IsConcurrencyToken();
		builder.Property(e => e.UpdatedBy).HasColumnName("updated_by").HasMaxLength(64);
		builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");
	}
}

public class FlagRevisionConfiguration : IEntityTypeConfiguration<FlagRevision>
{
	public void Configure(EntityTypeBuilder<FlagRevision> builder)
	{
		builder.ToTable("flag_revisions");
		builder.HasKey(e => e.Number);
		builder.Property(e => e.Number).HasColumnName("number").ValueGeneratedNever();
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		builder.Property(e => e.CreatedBy).HasColumnName("created_by").HasMaxLength(64);
		builder.Property(e => e.Comment).HasColumnName("comment").HasMaxLength(500);
		builder.Property(e => e.Document).HasColumnName("document");
		builder.Property(e => e.RollbackOf).HasColumnName("rollback_of");
	}
}

namespace GenOnlineService.Flags
{
	public static class FlagCodes
	{
		public const string Stale = "conflict.stale";
		public const string Exists = "flag.exists";
		public const string InUse = "flag.in_use";
		public const string NoChanges = "flag.no_changes";
	}

	// Keys service code reads; staff cannot archive them
	public static class ServiceFlags
	{
		private static readonly HashSet<string> s_used = new(StringComparer.Ordinal);

		public static IReadOnlyCollection<string> Used
		{
			get
			{
				lock (s_used)
				{
					return s_used.ToArray();
				}
			}
		}

		public static void Register(string key)
		{
			lock (s_used)
			{
				s_used.Add(key);
			}
		}

		// For tests
		internal static void Unregister(string key)
		{
			lock (s_used)
			{
				s_used.Remove(key);
			}
		}

		public static bool IsUsed(string key)
		{
			lock (s_used)
			{
				return s_used.Contains(key);
			}
		}
	}

	public sealed record DraftFlag(FlagDefinition Flag, bool Archived, int Version, string UpdatedBy, DateTime UpdatedAt);

	// One field that differs between two documents; before/after are FlagDefinitions, null when added or removed
	public sealed record FlagChange
	{
		public string Key { get; init; } = String.Empty;
		public string Label { get; init; } = String.Empty;

		// added, removed or changed
		public string Change { get; init; } = String.Empty;

		// the properties that differ: value, rules, enabled, label, ...
		public IReadOnlyList<string> Fields { get; init; } = [];

		public FlagDefinition? Before { get; init; }
		public FlagDefinition? After { get; init; }
	}

	public static class FlagStore
	{
		public const int MaximumCommentLength = 500;

		private static readonly JsonSerializerOptions s_json = RealtimeJson.Create();

		public static string Serialize(FlagDefinition flag) => JsonSerializer.Serialize(flag, s_json);

		public static FlagDefinition Deserialize(string json) => JsonSerializer.Deserialize<FlagDefinition>(json, s_json)!;

		public static DraftFlag ToDraft(FlagRow row) => new(Deserialize(row.Definition), row.ArchivedAt != null, row.Version, row.UpdatedBy, row.UpdatedAt);

		public static string ETagOf(int version) => "\"" + version.ToString(CultureInfo.InvariantCulture) + "\"";

		public static async Task<List<DraftFlag>> ListDraft(AppDbContext db)
		{
			List<FlagRow> rows = await db.Flags.AsNoTracking().OrderBy(f => f.Key).ToListAsync();
			return rows.Select(ToDraft).ToList();
		}

		public static async Task<DraftFlag?> GetDraft(AppDbContext db, string key)
		{
			FlagRow? row = await db.Flags.AsNoTracking().FirstOrDefaultAsync(f => f.Key == key);
			return row == null ? null : ToDraft(row);
		}

		// One tag over the whole draft, so publish only goes ahead on the draft the caller reviewed
		public static string DraftETag(IEnumerable<FlagRow> rows)
		{
			var text = new StringBuilder();
			foreach (FlagRow row in rows.OrderBy(r => r.Key, StringComparer.Ordinal))
			{
				text.Append(row.Key).Append(':').Append(row.Version).Append(row.ArchivedAt == null ? '+' : '-').Append('\n');
			}
			return "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())), 0, 16).ToLowerInvariant() + "\"";
		}

		public static async Task<string> DraftETag(AppDbContext db) => DraftETag(await db.Flags.AsNoTracking().ToListAsync());

		public static async Task<(DraftFlag? Flag, Failure? Failure)> Create(AppDbContext db, FlagDefinition flag, Database.ModerationActor actor)
		{
			string? field = FlagRules.Check(flag);
			if (field != null)
			{
				return (null, Result.Invalid(field));
			}

			await using var tx = await db.Database.BeginTransactionAsync();
			var row = new FlagRow { Key = flag.Key, Definition = Serialize(flag), Version = 1, UpdatedBy = actor.Name, UpdatedAt = DateTime.UtcNow };
			db.Flags.Add(row);
			try
			{
				await db.SaveChangesAsync();
			}
			catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex))
			{
				return (null, Result.Fail(FlagCodes.Exists, new { flag.Key }));
			}
			db.ChangeTracker.Clear();
			await Database.Moderation.AddAuditEntry(db, actor, FlagActions.Create, AuditTargetKinds.Flag, null, null, new { key = flag.Key, after = JsonNode.Parse(row.Definition) });
			await tx.CommitAsync();
			return (ToDraft(row), null);
		}

		public static async Task<(DraftFlag? Flag, Failure? Failure)> Update(AppDbContext db, string key, FlagDefinition flag, int? expectedVersion, Database.ModerationActor actor)
		{
			if (flag.Key != key)
			{
				return (null, Result.Invalid("key"));
			}
			string? field = FlagRules.Check(flag);
			if (field != null)
			{
				return (null, Result.Invalid(field));
			}

			return await Change(db, key, expectedVersion, actor, FlagActions.Update, row =>
			{
				FlagDefinition before = Deserialize(row.Definition);
				if (before.Scope == FlagScopes.Service && flag.Scope != FlagScopes.Service && ServiceFlags.IsUsed(key))
				{
					return Result.Fail(FlagCodes.InUse, new { Key = key });
				}
				row.Definition = Serialize(flag);
				return null;
			});
		}

		public static Task<(DraftFlag? Flag, Failure? Failure)> Archive(AppDbContext db, string key, int? expectedVersion, Database.ModerationActor actor)
		{
			return Change(db, key, expectedVersion, actor, FlagActions.Archive, row =>
			{
				if (ServiceFlags.IsUsed(key))
				{
					return Result.Fail(FlagCodes.InUse, new { Key = key });
				}
				row.ArchivedAt ??= DateTime.UtcNow;
				return null;
			});
		}

		public static Task<(DraftFlag? Flag, Failure? Failure)> Restore(AppDbContext db, string key, int? expectedVersion, Database.ModerationActor actor)
		{
			return Change(db, key, expectedVersion, actor, FlagActions.Restore, row =>
			{
				row.ArchivedAt = null;
				return null;
			});
		}

		private static async Task<(DraftFlag? Flag, Failure? Failure)> Change(AppDbContext db, string key, int? expectedVersion, Database.ModerationActor actor, string action, Func<FlagRow, Failure?> apply)
		{
			await using var tx = await db.Database.BeginTransactionAsync();
			FlagRow? row = await db.Flags.AsTracking().FirstOrDefaultAsync(f => f.Key == key);
			if (row == null)
			{
				return (null, Result.NotFound("flag"));
			}
			if (expectedVersion != null && row.Version != expectedVersion)
			{
				return (null, Result.Fail(FlagCodes.Stale, new { Version = row.Version }));
			}

			string beforeDefinition = row.Definition;
			bool beforeArchived = row.ArchivedAt != null;
			Failure? failure = apply(row);
			if (failure != null)
			{
				return (null, failure);
			}

			row.Version++;
			row.UpdatedBy = actor.Name;
			row.UpdatedAt = DateTime.UtcNow;
			try
			{
				await db.SaveChangesAsync();
			}
			catch (DbUpdateConcurrencyException)
			{
				return (null, Result.Fail(FlagCodes.Stale, new { Version = (int?)null }));
			}
			db.ChangeTracker.Clear();

			await Database.Moderation.AddAuditEntry(db, actor, action, AuditTargetKinds.Flag, null, null, new
			{
				key,
				before = JsonNode.Parse(beforeDefinition),
				after = JsonNode.Parse(row.Definition),
				archived_before = beforeArchived,
				archived_after = row.ArchivedAt != null
			});
			await tx.CommitAsync();
			return (ToDraft(row), null);
		}

		public static async Task<FlagRevision?> Current(AppDbContext db) => await db.FlagRevisions.AsNoTracking().OrderByDescending(r => r.Number).FirstOrDefaultAsync();

		public static async Task<FlagRevision?> GetRevision(AppDbContext db, int number) => await db.FlagRevisions.AsNoTracking().FirstOrDefaultAsync(r => r.Number == number);

		// Newest first, without the documents
		public static async Task<(List<FlagRevision> Page, int Total)> ListRevisions(AppDbContext db, int skip, int take)
		{
			int total = await db.FlagRevisions.CountAsync();
			List<FlagRevision> page = await db.FlagRevisions.AsNoTracking().OrderByDescending(r => r.Number).Skip(skip).Take(take)
				.Select(r => new FlagRevision { Number = r.Number, CreatedAt = r.CreatedAt, CreatedBy = r.CreatedBy, Comment = r.Comment, RollbackOf = r.RollbackOf })
				.ToListAsync();
			return (page, total);
		}

		// The live draft rows as one flagd document
		public static JsonObject DocumentOf(IEnumerable<FlagRow> rows) => Flagd.ToDocument(rows.Where(r => r.ArchivedAt == null).Select(r => Deserialize(r.Definition)));

		public static async Task<(FlagRevision? Revision, Failure? Failure)> Publish(AppDbContext db, string? comment, string? expectedDraftETag, Database.ModerationActor actor)
		{
			if (comment != null && comment.Length > MaximumCommentLength)
			{
				return (null, Result.Invalid("comment"));
			}

			await using var tx = await db.Database.BeginTransactionAsync();
			List<FlagRow> rows = await db.Flags.AsNoTracking().ToListAsync();
			if (expectedDraftETag != null && expectedDraftETag != DraftETag(rows))
			{
				return (null, Result.Fail(FlagCodes.Stale, new { Version = (int?)null }));
			}

			JsonObject document = DocumentOf(rows);
			FlagRevision? current = await Current(db);
			if (current != null && JsonNode.DeepEquals(JsonNode.Parse(current.Document), document))
			{
				return (null, Result.Fail(FlagCodes.NoChanges));
			}

			(FlagRevision? revision, Failure? failure) = await Insert(db, document, comment, null, current, actor, FlagActions.Publish);
			if (failure == null)
			{
				await tx.CommitAsync();
			}
			return (revision, failure);
		}

		// Publishes revision n again as the next revision; the draft becomes that revision
		public static async Task<(FlagRevision? Revision, Failure? Failure)> Rollback(AppDbContext db, int number, string? comment, Database.ModerationActor actor)
		{
			if (comment != null && comment.Length > MaximumCommentLength)
			{
				return (null, Result.Invalid("comment"));
			}

			await using var tx = await db.Database.BeginTransactionAsync();
			FlagRevision? target = await GetRevision(db, number);
			if (target == null)
			{
				return (null, Result.NotFound("revision"));
			}
			(List<FlagDefinition>? flags, Failure? read) = ReadDocument(JsonNode.Parse(target.Document));
			if (read != null)
			{
				return (null, read);
			}

			Failure? replaced = await ReplaceDraft(db, flags!, actor);
			if (replaced != null)
			{
				return (null, replaced);
			}

			FlagRevision? current = await Current(db);
			(FlagRevision? revision, Failure? failure) = await Insert(db, Flagd.ToDocument(flags!), comment, number, current, actor, FlagActions.Rollback);
			if (failure == null)
			{
				await tx.CommitAsync();
			}
			return (revision, failure);
		}

		// Replaces the draft with a flagd document (an export of another environment or a hand-written file); nothing goes live until publish
		public static async Task<(List<DraftFlag>? Draft, Failure? Failure)> Import(AppDbContext db, JsonNode? document, Database.ModerationActor actor)
		{
			(List<FlagDefinition>? flags, Failure? read) = ReadDocument(document);
			if (read != null)
			{
				return (null, read);
			}

			await using var tx = await db.Database.BeginTransactionAsync();
			Failure? failure = await ReplaceDraft(db, flags!, actor);
			if (failure != null)
			{
				return (null, failure);
			}
			await Database.Moderation.AddAuditEntry(db, actor, FlagActions.Import, AuditTargetKinds.Flag, null, null, new { keys = flags!.Select(f => f.Key).ToArray() });
			await tx.CommitAsync();
			return (await ListDraft(db), null);
		}

		// Throws the draft away: it becomes the live revision again
		public static async Task<(List<DraftFlag>? Draft, Failure? Failure)> ResetDraft(AppDbContext db, Database.ModerationActor actor)
		{
			await using var tx = await db.Database.BeginTransactionAsync();
			FlagRevision? current = await Current(db);
			List<FlagDefinition> flags = [];
			if (current != null)
			{
				(List<FlagDefinition>? read, Failure? failure) = ReadDocument(JsonNode.Parse(current.Document));
				if (failure != null)
				{
					return (null, failure);
				}
				flags = read!;
			}
			Failure? replaced = await ReplaceDraft(db, flags, actor);
			if (replaced != null)
			{
				return (null, replaced);
			}
			await Database.Moderation.AddAuditEntry(db, actor, FlagActions.Reset, AuditTargetKinds.Flag, null, null, new { revision = current?.Number });
			await tx.CommitAsync();
			return (await ListDraft(db), null);
		}

		// Every flag of a document, checked; the failing field names the flag ("flags.<key>.<field>")
		public static (List<FlagDefinition>? Flags, Failure? Failure) ReadDocument(JsonNode? document)
		{
			if (document is not JsonObject root || root["flags"] is not JsonObject map)
			{
				return (null, Result.Invalid("flags"));
			}

			var flags = new List<FlagDefinition>();
			foreach ((string key, JsonNode? node) in map)
			{
				if (!FlagRules.IsValidKey(key))
				{
					return (null, Result.Invalid("flags." + key));
				}
				(FlagDefinition? flag, string? field) = Flagd.FromFlag(key, node);
				if (flag == null)
				{
					return (null, Result.Invalid(field!));
				}
				string? invalid = FlagRules.Check(flag);
				if (invalid != null)
				{
					return (null, Result.Invalid("flags." + key + "." + invalid));
				}
				flags.Add(flag);
			}
			return (flags, null);
		}

		// Fields in the list are written and restored, other fields are archived; a service flag in use cannot be archived
		private static async Task<Failure?> ReplaceDraft(AppDbContext db, List<FlagDefinition> flags, Database.ModerationActor actor)
		{
			DateTime now = DateTime.UtcNow;
			Dictionary<string, FlagDefinition> wanted = flags.ToDictionary(f => f.Key, StringComparer.Ordinal);
			string? missing = ServiceFlags.Used.Where(k => !wanted.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
			if (missing != null)
			{
				return Result.Fail(FlagCodes.InUse, new { Key = missing });
			}

			List<FlagRow> rows = await db.Flags.AsTracking().ToListAsync();
			foreach (FlagRow row in rows)
			{
				if (wanted.Remove(row.Key, out FlagDefinition? flag))
				{
					string definition = Serialize(flag);
					if (row.Definition != definition || row.ArchivedAt != null)
					{
						row.Definition = definition;
						row.ArchivedAt = null;
						Touch(row, actor, now);
					}
				}
				else if (row.ArchivedAt == null)
				{
					row.ArchivedAt = now;
					Touch(row, actor, now);
				}
			}
			foreach (FlagDefinition flag in wanted.Values)
			{
				db.Flags.Add(new FlagRow { Key = flag.Key, Definition = Serialize(flag), Version = 1, UpdatedBy = actor.Name, UpdatedAt = now });
			}

			try
			{
				await db.SaveChangesAsync();
			}
			catch (DbUpdateConcurrencyException)
			{
				return Result.Fail(FlagCodes.Stale, new { Version = (int?)null });
			}
			finally
			{
				db.ChangeTracker.Clear();
			}
			return null;
		}

		private static void Touch(FlagRow row, Database.ModerationActor actor, DateTime now)
		{
			row.Version++;
			row.UpdatedBy = actor.Name;
			row.UpdatedAt = now;
		}

		private static async Task<(FlagRevision? Revision, Failure? Failure)> Insert(AppDbContext db, JsonObject document, string? comment, int? rollbackOf, FlagRevision? current, Database.ModerationActor actor, string action)
		{
			var revision = new FlagRevision
			{
				Number = (current?.Number ?? 0) + 1,
				CreatedAt = DateTime.UtcNow,
				CreatedBy = actor.Name,
				Comment = String.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
				Document = document.ToJsonString(),
				RollbackOf = rollbackOf
			};
			db.FlagRevisions.Add(revision);
			try
			{
				await db.SaveChangesAsync();
			}
			catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex))
			{
				// another publish took this number first
				return (null, Result.Fail(FlagCodes.Stale, new { Version = (int?)null }));
			}
			finally
			{
				db.ChangeTracker.Clear();
			}

			List<FlagChange> changes = Diff(current == null ? null : JsonNode.Parse(current.Document), document);
			await Database.Moderation.AddAuditEntry(db, actor, action, AuditTargetKinds.FlagRevision, revision.Number, revision.Comment, new
			{
				rollback_of = rollbackOf,
				changes = changes.Select(c => new { c.Key, c.Change, c.Fields }).ToArray()
			});
			return (revision, null);
		}

		// What a publish of after would change compared with before
		public static List<FlagChange> Diff(JsonNode? before, JsonNode? after)
		{
			Dictionary<string, FlagDefinition> old = Definitions(before);
			Dictionary<string, FlagDefinition> now = Definitions(after);
			var changes = new List<FlagChange>();
			foreach (string key in old.Keys.Union(now.Keys).OrderBy(k => k, StringComparer.Ordinal))
			{
				old.TryGetValue(key, out FlagDefinition? a);
				now.TryGetValue(key, out FlagDefinition? b);
				if (a == null)
				{
					changes.Add(new FlagChange { Key = key, Label = b!.Label, Change = "added", After = b });
				}
				else if (b == null)
				{
					changes.Add(new FlagChange { Key = key, Label = a.Label, Change = "removed", Before = a });
				}
				else
				{
					string[] fields = ChangedFields(a, b);
					if (fields.Length > 0)
					{
						changes.Add(new FlagChange { Key = key, Label = b.Label, Change = "changed", Fields = fields, Before = a, After = b });
					}
				}
			}
			return changes;
		}

		private static Dictionary<string, FlagDefinition> Definitions(JsonNode? document)
		{
			var result = new Dictionary<string, FlagDefinition>(StringComparer.Ordinal);
			if (document?["flags"] is JsonObject map)
			{
				foreach ((string key, JsonNode? node) in map)
				{
					(FlagDefinition? flag, _) = Flagd.FromFlag(key, node);
					if (flag != null)
					{
						result[key] = flag;
					}
				}
			}
			return result;
		}

		private static string[] ChangedFields(FlagDefinition a, FlagDefinition b)
		{
			JsonObject left = (JsonObject)JsonNode.Parse(Serialize(a))!;
			JsonObject right = (JsonObject)JsonNode.Parse(Serialize(b))!;
			return left.Select(p => p.Key).Union(right.Select(p => p.Key))
				.Where(name => !JsonNode.DeepEquals(left[name], right[name]))
				.OrderBy(name => name, StringComparer.Ordinal)
				.ToArray();
		}
	}

	public static class FlagActions
	{
		public const string Create = "flag_create";
		public const string Update = "flag_update";
		public const string Archive = "flag_archive";
		public const string Restore = "flag_restore";
		public const string Publish = "flags_publish";
		public const string Rollback = "flags_rollback";
		public const string Import = "flags_import";
		public const string Reset = "flags_reset";
	}
}
