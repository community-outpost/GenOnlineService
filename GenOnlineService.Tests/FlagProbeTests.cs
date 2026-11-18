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

using Database;
using GenOnlineService.Flags;
using GenOnlineService.Realtime;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GenOnlineService.Tests
{
	// The flags draft, revisions and audit rows against a real database (GENONLINE_PROBE_POSTGRES / GENONLINE_PROBE_MARIADB)
	[Collection(FlagCatalogCollection.Name)]
	public class FlagProbeTests
	{
		private static readonly ModerationActor s_staff = new() { Kind = AuditActorKinds.User, UserID = 42, Name = "user:42", Source = ModerationSources.Api };

		private const string TimeoutKey = "client.connection.reconnect_timeout_ms";

		private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

		private static async Task<FlagDefinition> Draft(AppDbContext db, string key) => (await FlagStore.GetDraft(db, key))!.Flag;

		private static async Task<int> Version(AppDbContext db, string key) => (await FlagStore.GetDraft(db, key))!.Version;

		[ProbeTheory]
		[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
		public async Task TheSeedPublishesRevisionOneOnceAndTheDraftMatchesIt(string provider)
		{
			await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
			if (probe == null) return;
			await using AppDbContext db = probe.Open();

			await FlagSeed.EnsureAsync(db);
			await FlagSeed.EnsureAsync(db);

			FlagRevision current = (await FlagStore.Current(db))!;
			Assert.Equal(1, current.Number);
			Assert.Equal(FlagSeed.Actor, current.CreatedBy);
			Assert.Equal(1, await db.FlagRevisions.CountAsync());
			Assert.Equal(38, await db.Flags.CountAsync());

			// the database may reorder JSON keys; the draft still reads as unchanged
			Assert.Empty(FlagStore.Diff(JsonNode.Parse(current.Document), FlagStore.DocumentOf(await db.Flags.AsNoTracking().ToListAsync())));

			FlagCatalog.Install(FlagSnapshot.Empty);
			await FlagCatalog.Reload(db);
			Assert.Equal(1, FlagCatalog.Current.Revision);
			JsonObject grouped = FlagCatalog.Current.ServiceConfigFor(FlagContext.Create(7, [], "c", null, "NL", "test"));
			Assert.True(JsonNode.DeepEquals(JsonNode.Parse(FlagSeed.ServiceConfig), grouped));
			FlagCatalog.Install(FlagSnapshot.Empty);
		}

		[ProbeTheory]
		[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
		public async Task AnEditWithAStaleVersionIsRefusedAndAGoodOneIsAudited(string provider)
		{
			await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
			if (probe == null) return;
			await using AppDbContext db = probe.Open();
			await FlagSeed.EnsureAsync(db);

			FlagDefinition flag = await Draft(db, TimeoutKey);
			int version = await Version(db, TimeoutKey);

			(_, Failure? stale) = await FlagStore.Update(db, TimeoutKey, flag with { Value = J("45000") }, version + 1, s_staff);
			Assert.Equal(FlagCodes.Stale, stale!.Value.Code);

			(_, Failure? invalid) = await FlagStore.Update(db, TimeoutKey, flag with { Value = J("\"slow\"") }, version, s_staff);
			Assert.Equal(Result.Invalid("value"), invalid);

			(DraftFlag? updated, Failure? failure) = await FlagStore.Update(db, TimeoutKey, flag with { Value = J("45000") }, version, s_staff);
			Assert.Null(failure);
			Assert.Equal(version + 1, updated!.Version);
			Assert.Equal(45000, (await Draft(db, TimeoutKey)).Value.GetInt32());

			AuditLogEntry entry = await db.AuditLog.AsNoTracking().Where(e => e.Action == FlagActions.Update).SingleAsync();
			Assert.Equal("user:42", entry.Actor);
			Assert.Equal(AuditTargetKinds.Flag, entry.TargetKind);
			JsonNode details = JsonNode.Parse(entry.Details!)!;
			Assert.Equal(30000, details["before"]!["value"]!.GetValue<int>());
			Assert.Equal(45000, details["after"]!["value"]!.GetValue<int>());

			// the edit is a draft until published
			Assert.Equal(1, (await FlagStore.Current(db))!.Number);
		}

		[ProbeTheory]
		[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
		public async Task PublishRollbackAndTheirRefusals(string provider)
		{
			await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
			if (probe == null) return;
			await using AppDbContext db = probe.Open();
			await FlagSeed.EnsureAsync(db);

			(_, Failure? nothing) = await FlagStore.Publish(db, null, null, s_staff);
			Assert.Equal(FlagCodes.NoChanges, nothing!.Value.Code);

			string reviewed = await FlagStore.DraftETag(db);
			await FlagStore.Update(db, TimeoutKey, (await Draft(db, TimeoutKey)) with { Value = J("45000") }, null, s_staff);

			// the draft moved after the review
			(_, Failure? stale) = await FlagStore.Publish(db, "longer timeout", reviewed, s_staff);
			Assert.Equal(FlagCodes.Stale, stale!.Value.Code);

			(FlagRevision? two, Failure? published) = await FlagStore.Publish(db, "longer timeout", await FlagStore.DraftETag(db), s_staff);
			Assert.Null(published);
			Assert.Equal((2, "longer timeout", "user:42"), (two!.Number, two.Comment, two.CreatedBy));

			AuditLogEntry publishEntry = await db.AuditLog.AsNoTracking().Where(e => e.Action == FlagActions.Publish && e.TargetID == 2).SingleAsync();
			Assert.Equal(2, publishEntry.TargetID);
			Assert.Contains(TimeoutKey, publishEntry.Details);

			(FlagRevision? three, Failure? rolled) = await FlagStore.Rollback(db, 1, "back to the seed", s_staff);
			Assert.Null(rolled);
			Assert.Equal((3, (int?)1), (three!.Number, three.RollbackOf));

			FlagRevision one = (await FlagStore.GetRevision(db, 1))!;
			FlagRevision latest = (await FlagStore.Current(db))!;
			Assert.Empty(FlagStore.Diff(JsonNode.Parse(one.Document), JsonNode.Parse(latest.Document)));
			Assert.Equal(30000, (await Draft(db, TimeoutKey)).Value.GetInt32());

			(_, Failure? missing) = await FlagStore.Rollback(db, 99, null, s_staff);
			Assert.Equal(Result.NotFound("revision"), missing);

			(List<FlagRevision> page, int total) = await FlagStore.ListRevisions(db, 0, 2);
			Assert.Equal(3, total);
			Assert.Equal([3, 2], page.Select(r => r.Number).ToArray());
		}

		[ProbeTheory]
		[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
		public async Task ArchivedFieldsLeaveTheNextRevisionAndComeBack(string provider)
		{
			await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
			if (probe == null) return;
			await using AppDbContext db = probe.Open();
			await FlagSeed.EnsureAsync(db);

			(DraftFlag? archived, Failure? failure) = await FlagStore.Archive(db, TimeoutKey, null, s_staff);
			Assert.Null(failure);
			Assert.True(archived!.Archived);
			await FlagStore.Publish(db, null, null, s_staff);
			Assert.Null(JsonNode.Parse((await FlagStore.Current(db))!.Document)!["flags"]![TimeoutKey]);

			await FlagStore.Restore(db, TimeoutKey, null, s_staff);
			await FlagStore.Publish(db, null, null, s_staff);
			Assert.NotNull(JsonNode.Parse((await FlagStore.Current(db))!.Document)!["flags"]![TimeoutKey]);

			Assert.Equal(Result.NotFound("flag"), (await FlagStore.Archive(db, "client.nope", null, s_staff)).Failure);
		}

		[ProbeTheory]
		[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
		public async Task AServiceFlagInUseCannotBeArchivedOrImportedAway(string provider)
		{
			await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
			if (probe == null) return;
			await using AppDbContext db = probe.Open();

			const string key = "service.probe.in_use";
			var flag = new FlagDefinition { Key = key, Scope = FlagScopes.Service, Label = "In use", Group = "Probe", Type = FlagTypes.Int, Value = J("5") };
			Assert.Null((await FlagStore.Create(db, flag, s_staff)).Failure);
			Assert.Equal(FlagCodes.Exists, (await FlagStore.Create(db, flag, s_staff)).Failure!.Value.Code);

			// reading it from service code marks the key as used
			Assert.Equal(1, FlagCatalog.Get(key, 1));
			try
			{
				Assert.Equal(FlagCodes.InUse, (await FlagStore.Archive(db, key, null, s_staff)).Failure!.Value.Code);
				Assert.Equal(FlagCodes.InUse, (await FlagStore.Update(db, key, flag with { Scope = FlagScopes.Client }, null, s_staff)).Failure!.Value.Code);
				Assert.Equal(FlagCodes.InUse, (await FlagStore.Import(db, Flagd.ToDocument([]), s_staff)).Failure!.Value.Code);
			}
			finally
			{
				ServiceFlags.Unregister(key);
			}
		}

		[ProbeTheory]
		[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
		public async Task ImportReplacesTheDraftAndResetBringsBackTheLiveRevision(string provider)
		{
			await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
			if (probe == null) return;
			await using AppDbContext db = probe.Open();
			await FlagSeed.EnsureAsync(db);

			JsonObject other = JsonNode.Parse("""{"flags":{"client.lobby.host_migration":{"state":"ENABLED","variants":{"default":true},"defaultVariant":"default"}}}""")!.AsObject();
			(List<DraftFlag>? draft, Failure? failure) = await FlagStore.Import(db, other, s_staff);
			Assert.Null(failure);
			Assert.Single(draft!, f => !f.Archived);
			Assert.True((await Draft(db, "client.lobby.host_migration")).Value.GetBoolean());
			Assert.Equal(37, draft!.Count(f => f.Archived));

			(_, Failure? bad) = await FlagStore.Import(db, JsonNode.Parse("""{"flags":{"Bad Key":{}}}"""), s_staff);
			Assert.Equal(Result.Invalid("flags.Bad Key"), bad);

			(List<DraftFlag>? reset, _) = await FlagStore.ResetDraft(db, s_staff);
			Assert.Equal(38, reset!.Count(f => !f.Archived));
			Assert.False((await Draft(db, "client.lobby.host_migration")).Value.GetBoolean());
			Assert.Equal(1, (await FlagStore.Current(db))!.Number);
		}
	}

	// FlagCatalog and ServiceFlags are process wide
	[CollectionDefinition(FlagCatalogCollection.Name, DisableParallelization = true)]
	public sealed class FlagCatalogCollection
	{
		public const string Name = "flag catalog";
	}
}
