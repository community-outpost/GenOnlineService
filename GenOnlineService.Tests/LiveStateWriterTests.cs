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
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

// The Redis layout in memory with the rules of the Lua scripts: compare on the owner epoch, transfers, claims, delete markers and a stream that trims
internal sealed class FakeLiveStore : ILiveStateStore
{
	private readonly object m_lock = new();
	public readonly Dictionary<string, string> State = new(StringComparer.Ordinal);
	public readonly Dictionary<string, (long Epoch, string Owner, long DeletedAtMs)> Epochs = new(StringComparer.Ordinal);
	public readonly Dictionary<string, long> Seqs = new(StringComparer.Ordinal);
	public readonly List<LiveStreamEntry> Stream = new();
	public bool Down;
	public int FailNextWrites;
	public Action? DuringWrite;
	public long NowMs = 1_000_000;
	private long m_nextID;

	private void Check()
	{
		if (Down)
		{
			throw new InvalidOperationException("redis down");
		}
		if (FailNextWrites > 0)
		{
			--FailNextWrites;
			throw new InvalidOperationException("redis failed the batch");
		}
	}

	public Task<IReadOnlyList<LiveWriteResult>> WriteAsync(string copy, IReadOnlyList<LiveWrite> writes)
	{
		lock (m_lock)
		{
			Check();
			List<LiveWriteResult> results = new();
			foreach (LiveWrite write in writes)
			{
				if (Epochs.TryGetValue(write.Key, out (long Epoch, string Owner, long DeletedAtMs) current))
				{
					if (current.DeletedAtMs > 0 || current.Epoch != write.Epoch || current.Owner != copy)
					{
						results.Add(new LiveWriteResult(false, current.Epoch));
						continue;
					}
				}
				else if (write.Epoch != 1)
				{
					results.Add(new LiveWriteResult(false, 0));
					continue;
				}

				long epoch = write.Op == LiveOp.Transfer ? write.Epoch + 1 : write.Epoch;
				string owner = write.Op == LiveOp.Transfer ? write.Target! : copy;
				if (write.Op == LiveOp.Delete)
				{
					State.Remove(write.Key);
					Epochs[write.Key] = (epoch, owner, NowMs);
				}
				else
				{
					State[write.Key] = write.Body!;
					Epochs[write.Key] = (epoch, owner, 0);
				}
				long seq = Seqs[copy] = (Seqs.TryGetValue(copy, out long previous) ? previous : 0) + 1;
				Stream.Add(new LiveStreamEntry($"{++m_nextID}-0", write.Op, write.Key, copy, seq, epoch, owner, write.Version, write.Op == LiveOp.Delete ? null : write.Body));
				results.Add(new LiveWriteResult(true, epoch));
			}
			DuringWrite?.Invoke();
			return Task.FromResult<IReadOnlyList<LiveWriteResult>>(results);
		}
	}

	public Task<IReadOnlyDictionary<string, long>> ClaimAsync(string copy, LiveClaim mode, string? owner, IReadOnlyList<string> ensure)
	{
		lock (m_lock)
		{
			if (Down)
			{
				throw new InvalidOperationException("redis down");
			}
			Dictionary<string, long> claimed = new(StringComparer.Ordinal);
			foreach (string key in Epochs.Keys.ToList())
			{
				(long epoch, string current, long deleted) = Epochs[key];
				bool take = mode == LiveClaim.NotMine ? current != copy : current == owner;
				if (!take)
				{
					continue;
				}
				Epochs[key] = (epoch + 1, copy, deleted);
				if (deleted == 0)
				{
					claimed[key] = epoch + 1;
				}
			}
			foreach (string key in ensure)
			{
				if (!Epochs.ContainsKey(key))
				{
					Epochs[key] = (1, copy, 0);
					claimed[key] = 1;
				}
			}
			return Task.FromResult<IReadOnlyDictionary<string, long>>(claimed);
		}
	}

	public Task<LiveSnapshot> LoadAsync()
	{
		lock (m_lock)
		{
			if (Down)
			{
				throw new InvalidOperationException("redis down");
			}
			return Task.FromResult(new LiveSnapshot(new Dictionary<string, string>(State), new Dictionary<string, long>(Seqs), Stream.LastOrDefault()?.ID ?? "0-0"));
		}
	}

	public Task<IReadOnlyList<LiveStreamEntry>> ReadAfterAsync(string position, int count)
	{
		lock (m_lock)
		{
			if (Down)
			{
				throw new InvalidOperationException("redis down");
			}
			long after = long.Parse(position.Split('-')[0]);
			return Task.FromResult<IReadOnlyList<LiveStreamEntry>>(Stream.Where(e => long.Parse(e.ID.Split('-')[0]) > after).Take(count).ToList());
		}
	}

	public Task<string?> ReadAsync(string key)
	{
		lock (m_lock)
		{
			return Task.FromResult(State.TryGetValue(key, out string? value) ? value : null);
		}
	}

	public Task PruneAsync(long olderThanMs)
	{
		lock (m_lock)
		{
			foreach (string key in Epochs.Where(entry => entry.Value.DeletedAtMs > 0 && NowMs - entry.Value.DeletedAtMs > olderThanMs).Select(entry => entry.Key).ToList())
			{
				Epochs.Remove(key);
			}
			return Task.CompletedTask;
		}
	}

	// XADD MAXLEN, keeps only the newest entries
	public void Trim(int keep)
	{
		lock (m_lock)
		{
			if (Stream.Count > keep)
			{
				Stream.RemoveRange(0, Stream.Count - keep);
			}
		}
	}
}

// Build steps 3 and 4: the writer with compare on write, transfers and claims, the mirror in per copy order and the follower
[Collection(LiveStateCollection.Name)]
public class LiveStateWriterTests
{
	internal static string Data(string value) => JsonSerializer.Serialize(new { value }, LiveJson.Options);

	internal static string? ValueOf(IReadOnlyDictionary<string, string> entities, string key) =>
		entities.TryGetValue(key, out string? json) ? LiveJson.ReadEnvelope(json)!.Data.GetProperty("value").GetString() : null;

	private static LiveStateWriter Writer(FakeLiveStore store, string copy, Func<bool>? canWrite = null) => new(store, copy, canWrite ?? (() => true));

	[Fact]
	public async Task TheMirrorAppliesTheStreamInOrder()
	{
		FakeLiveStore store = new();
		LiveStateWriter writer = Writer(store, "a");
		LiveStateFollower follower = new(store, "b", () => true, 100, 2500);

		writer.Set("lobby:1", Data("one"));
		writer.Set("lobby:2", Data("two"));
		await writer.FlushAsync();
		await follower.StepAsync();
		Assert.Equal(("one", "two"), (ValueOf(follower.Mirror.Entities, "lobby:1"), ValueOf(follower.Mirror.Entities, "lobby:2")));

		writer.Set("lobby:1", Data("one again"));
		writer.Set("lobby:1", Data("one last"));
		writer.Delete("lobby:2");
		await writer.FlushAsync();
		Assert.Equal(2, await follower.StepAsync());
		Assert.Equal(("one last", null), (ValueOf(follower.Mirror.Entities, "lobby:1"), ValueOf(follower.Mirror.Entities, "lobby:2")));

		// the entries carry the write count of the copy, the envelopes the owner, its epoch, the writer and a version per entity
		Assert.Equal(new[] { 1L, 2L, 3L, 4L }, store.Stream.Select(e => e.Seq).ToArray());
		LiveEnvelope envelope = LiveJson.ReadEnvelope(store.State["lobby:1"])!;
		Assert.Equal(("a", 1L, "a", 2L), (envelope.Owner, envelope.Epoch, envelope.WrittenBy, envelope.Version));
	}

	[Fact]
	public async Task TwoCopiesWritingInTurnStayInOrderForTheFollower()
	{
		FakeLiveStore store = new();
		LiveStateWriter a = Writer(store, "a");
		LiveStateWriter b = Writer(store, "b");
		LiveStateFollower follower = new(store, "c", () => true, 100, 2500);
		await follower.StepAsync();

		for (int i = 0; i < 3; i++)
		{
			a.Set("lobby:1", Data("a" + i));
			await a.FlushAsync();
			b.Set("lobby:2", Data("b" + i));
			await b.FlushAsync();
		}
		Assert.Equal(6, await follower.StepAsync());
		Assert.True(follower.InSync);
		Assert.Equal(("a2", "b2"), (ValueOf(follower.Mirror.Entities, "lobby:1"), ValueOf(follower.Mirror.Entities, "lobby:2")));
		Assert.Equal((3L, 3L), (follower.Mirror.SeqOf("a"), follower.Mirror.SeqOf("b")));
	}

	[Fact]
	public async Task AStreamTrimmedPastTheFollowerTriggersAFullReload()
	{
		FakeLiveStore store = new();
		LiveStateWriter writer = Writer(store, "a");
		LiveStateFollower follower = new(store, "b", () => true, 100, 2500);
		writer.Set("lobby:1", Data("one"));
		await writer.FlushAsync();
		await follower.StepAsync();

		for (int i = 0; i < 5; i++)
		{
			writer.Set("lobby:" + (10 + i), Data("n" + i));
			await writer.FlushAsync();
		}
		writer.Delete("lobby:1");
		await writer.FlushAsync();
		store.Trim(2);

		await follower.StepAsync();
		Assert.False(follower.Mirror.Loaded);
		Assert.False(follower.InSync);

		await follower.StepAsync();
		Assert.True(follower.InSync);
		Assert.Equal(new[] { "lobby:10", "lobby:11", "lobby:12", "lobby:13", "lobby:14" }, follower.Mirror.Entities.Keys.OrderBy(k => k).ToArray());
	}

	[Fact]
	public async Task AWriteBelowTheStoredOwnerEpochIsRefusedAndTheCopyDropsTheEntity()
	{
		FakeLiveStore store = new();
		LiveStateWriter a = Writer(store, "a");
		List<string> lost = new();
		a.Rejected += lost.Add;
		a.Set("lobby:1", Data("from a"));
		await a.FlushAsync();
		Assert.True(a.Owns("lobby:1"));

		// b takes over after a failure and owns lobby:1 at epoch 2
		LiveStateWriter b = Writer(store, "b");
		IReadOnlyDictionary<string, long> claimed = await ClaimNow(b, LiveClaim.NotMine, null);
		Assert.Equal(2L, claimed["lobby:1"]);

		// the fenced a writes once more, Redis refuses it and changes nothing
		int entries = store.Stream.Count;
		a.Set("lobby:1", Data("late"));
		await a.FlushAsync();
		Assert.Equal(new[] { "lobby:1" }, lost);
		Assert.False(a.Owns("lobby:1"));
		Assert.Equal("from a", ValueOf(store.State, "lobby:1"));
		Assert.Equal(entries, store.Stream.Count);

		// later writes of a for that lobby stay out, b writes at its epoch
		a.Set("lobby:1", Data("later"));
		await a.FlushAsync();
		b.Set("lobby:1", Data("from b"));
		await b.FlushAsync();
		Assert.Equal(("from b", (2L, "b", 0L)), (ValueOf(store.State, "lobby:1"), store.Epochs["lobby:1"]));
		Assert.Single(lost);
	}

	private static async Task<IReadOnlyDictionary<string, long>> ClaimNow(LiveStateWriter writer, LiveClaim mode, string? owner)
	{
		Task<IReadOnlyDictionary<string, long>> claim = writer.ClaimAsync(mode, owner, new[] { LiveKeys.Queue });
		await writer.FlushAsync();
		return await claim;
	}

	[Fact]
	public async Task ATransferGivesTheEntityToTheTargetAtTheNextEpochAndTheOldOwnerCannotWriteItAgain()
	{
		FakeLiveStore store = new();
		LiveStateWriter a = Writer(store, "a");
		LiveStateWriter b = Writer(store, "b");
		List<string> lost = new();
		a.Rejected += lost.Add;
		a.Set("lobby:7", Data("setup"));
		await a.FlushAsync();

		// a queued write before the transfer is replaced by it, one after it stays out
		a.Set("lobby:7", Data("changed"));
		Task<bool> transfer = a.TransferAsync("lobby:7", Data("final"), "b");
		a.Set("lobby:7", Data("after"));
		await a.FlushAsync();
		Assert.True(await transfer);
		Assert.False(a.Owns("lobby:7"));
		LiveEnvelope handed = LiveJson.ReadEnvelope(store.State["lobby:7"])!;
		Assert.Equal(("b", 2L, "a", "final"), (handed.Owner, handed.Epoch, handed.WrittenBy, handed.Data.GetProperty("value").GetString()));
		Assert.Equal(LiveOp.Transfer, store.Stream.Last().Op);

		// b takes it at the epoch of the transfer and writes it under its own name
		b.Adopt("lobby:7", 2);
		b.Set("lobby:7", Data("on b"));
		await b.FlushAsync();
		Assert.Equal(("b", "on b"), (LiveJson.ReadEnvelope(store.State["lobby:7"])!.WrittenBy, ValueOf(store.State, "lobby:7")));

		// a transfer of something a does not own fails without a write
		Assert.False(await TransferNow(a, "lobby:7"));
		Assert.Empty(lost);
	}

	private static async Task<bool> TransferNow(LiveStateWriter writer, string key)
	{
		Task<bool> transfer = writer.TransferAsync(key, Data("x"), "z");
		await writer.FlushAsync();
		return await transfer;
	}

	[Fact]
	public async Task ADeletedEntityIsNeverBroughtBackByALateWriteOfAnotherCopy()
	{
		FakeLiveStore store = new();
		LiveStateWriter a = Writer(store, "a");
		a.Set("lobby:3", Data("x"));
		await a.FlushAsync();

		LiveStateWriter b = Writer(store, "b");
		await ClaimNow(b, LiveClaim.NotMine, null);
		b.Delete("lobby:3");
		await b.FlushAsync();
		Assert.False(store.State.ContainsKey("lobby:3"));

		// a fenced copy that never learnt of the claim writes the lobby as new, the delete marker refuses it
		LiveStateWriter late = Writer(store, "a");
		late.Set("lobby:3", Data("ghost"));
		await late.FlushAsync();
		Assert.False(store.State.ContainsKey("lobby:3"));

		// a delete is final, also for the copy that wrote it
		Task<bool> transfer = b.TransferAsync("lobby:3", Data("x"), "a");
		b.Set("lobby:3", Data("again"));
		await b.FlushAsync();
		Assert.False(await transfer);
		Assert.False(store.State.ContainsKey("lobby:3"));

		// the marker goes after its age, which no fenced copy outlives
		store.NowMs += LiveStateWriter.TombstoneMs + 1;
		await store.PruneAsync(LiveStateWriter.TombstoneMs);
		Assert.False(store.Epochs.ContainsKey("lobby:3"));
	}

	[Fact]
	public async Task AClaimTakesWhatOtherCopiesOwnOrWhatOneCopyOwnsAndEnsuresTheQueue()
	{
		FakeLiveStore store = new();
		LiveStateWriter a = Writer(store, "a");
		LiveStateWriter b = Writer(store, "b");
		a.Set("lobby:1", Data("a"));
		await a.FlushAsync();
		b.Set("lobby:2", Data("b"));
		await b.FlushAsync();

		IReadOnlyDictionary<string, long> first = await ClaimNow(a, LiveClaim.OwnedBy, "b");
		Assert.Equal(new Dictionary<string, long> { ["lobby:2"] = 2, [LiveKeys.Queue] = 1 }, first);
		Assert.True(a.Owns(LiveKeys.Queue));

		LiveStateWriter c = Writer(store, "c");
		IReadOnlyDictionary<string, long> all = await ClaimNow(c, LiveClaim.NotMine, null);
		Assert.Equal(new Dictionary<string, long> { ["lobby:1"] = 2, ["lobby:2"] = 3, [LiveKeys.Queue] = 2 }, all);
		Assert.True(c.Owns("lobby:1") && c.Owns("lobby:2") && c.Owns(LiveKeys.Queue));
	}

	[Fact]
	public async Task AClaimWaitsUntilTheCopyMayWriteAndComesBeforeItsWrites()
	{
		FakeLiveStore store = new();
		LiveStateWriter a = Writer(store, "a");
		a.Set("lobby:1", Data("a"));
		await a.FlushAsync();

		bool may = false;
		LiveStateWriter b = Writer(store, "b", () => may);
		Task<IReadOnlyDictionary<string, long>> claim = b.ClaimAsync(LiveClaim.NotMine, null, new[] { LiveKeys.Queue });
		await b.FlushAsync();
		Assert.False(claim.IsCompleted);

		may = true;
		b.Adopt("lobby:9", 1);
		b.Set("lobby:1", Data("b"));
		await b.FlushAsync();
		Assert.True(claim.IsCompletedSuccessfully);
		Assert.Equal("b", ValueOf(store.State, "lobby:1"));
	}

	[Fact]
	public async Task ACopyThatMayNotWriteSendsNothingAndWritesEverythingAgainLater()
	{
		FakeLiveStore store = new();
		bool may = false;
		LiveStateWriter writer = Writer(store, "a", () => may);
		int before = writer.Generation;

		writer.Set("lobby:1", Data("x"));
		await writer.FlushAsync();
		Assert.Empty(store.Stream);
		Assert.False(writer.Writing);
		Assert.NotEqual(before, writer.Generation);

		may = true;
		writer.Set("lobby:1", Data("x"));
		await writer.FlushAsync();
		Assert.Single(store.Stream);
	}

	[Fact]
	public async Task AWriteAcceptedAfterTheCopyReleasedEverythingDoesNotOwnTheEntityAgain()
	{
		FakeLiveStore store = new();
		LiveStateWriter writer = Writer(store, "a");
		store.DuringWrite = writer.ReleaseAll;
		writer.Set("lobby:1", Data("x"));
		await writer.FlushAsync();
		Assert.True(store.State.ContainsKey("lobby:1"));
		Assert.False(writer.Owns("lobby:1"));

		store.DuringWrite = null;
		writer.Set("lobby:1", Data("y"));
		await writer.FlushAsync();
		Assert.Equal("x", ValueOf(store.State, "lobby:1"));
	}

	[Fact]
	public async Task ADeleteLostInAFailedBatchIsSentAgain()
	{
		FakeLiveStore store = new();
		LiveStateWriter writer = Writer(store, "a");
		writer.Set("lobby:1", Data("one"));
		writer.Set("lobby:2", Data("two"));
		await writer.FlushAsync();

		int generation = writer.Generation;
		store.FailNextWrites = 1;
		writer.Delete("lobby:1");
		await writer.FlushAsync();
		Assert.True(store.State.ContainsKey("lobby:1"));
		Assert.NotEqual(generation, writer.Generation);

		await writer.FlushAsync();
		Assert.False(store.State.ContainsKey("lobby:1"));

		LiveStateFollower follower = new(store, "b", () => true, 100, 2500);
		await follower.StepAsync();
		Assert.Equal(new[] { "lobby:2" }, follower.Mirror.Entities.Keys.ToArray());
	}

	[Fact]
	public async Task TheFollowerReportsWritesThatNameItsCopyAsTheOwner()
	{
		FakeLiveStore store = new();
		LiveStateWriter a = Writer(store, "a");
		await ClaimNow(a, LiveClaim.NotMine, null);
		a.Set("lobby:5", Data("five"));
		await a.FlushAsync();

		LiveStateFollower follower = new(store, "b", () => true, 100, 2500);
		List<(string Key, long Epoch)> owned = new();
		follower.Owned += (key, body, epoch) => owned.Add((key, epoch));
		await follower.StepAsync();

		Assert.True(await TransferNow2(a, "lobby:5", "b"));
		Assert.True(await TransferNow2(a, LiveKeys.Queue, "b"));
		await follower.StepAsync();
		Assert.Equal(new[] { ("lobby:5", 2L), (LiveKeys.Queue, 2L) }, owned);
	}

	private static async Task<bool> TransferNow2(LiveStateWriter writer, string key, string target)
	{
		Task<bool> transfer = writer.TransferAsync(key, Data("final"), target);
		await writer.FlushAsync();
		return await transfer;
	}

	[Fact]
	public async Task TheFollowerIsInSyncOnlyWhenCaughtUpAndRecentlyRead()
	{
		FakeLiveStore store = new();
		long now = 1_000;
		bool follow = true;
		LiveStateFollower follower = new(store, "b", () => follow, 100, 2500, () => now);
		Assert.False(follower.InSync);

		await follower.StepAsync();
		Assert.True(follower.InSync);

		// a full batch means more entries wait
		LiveStateWriter writer = Writer(store, "a");
		for (int i = 0; i < LiveStateFollower.BatchSize + 10; i++)
		{
			writer.Set("lobby:" + i, Data("x"));
		}
		await writer.FlushAsync();
		Assert.Equal(LiveStateFollower.BatchSize, await follower.StepAsync());
		Assert.False(follower.InSync);
		await follower.StepAsync();
		Assert.True(follower.InSync);

		// no successful read for longer than a lease
		now += 2600;
		Assert.False(follower.InSync);
		now += 10;
		await follower.StepAsync();
		Assert.True(follower.InSync);

		store.Down = true;
		await follower.StepAsync();
		Assert.False(follower.InSync);
		store.Down = false;

		await follower.StepAsync();
		Assert.True(follower.InSync);
		follow = false;
		await follower.StepAsync();
		Assert.False(follower.InSync);
		Assert.False(follower.Mirror.Loaded);
	}

	[Fact]
	public async Task OnlyAMirrorInSyncWithinTwoLeasePeriodsIsFresh()
	{
		FakeLiveStore store = new();
		long now = 10_000;
		LiveStateFollower follower = new(store, "b", () => true, 100, 2500, () => now);
		Assert.False(follower.Detach().Fresh);

		await follower.StepAsync();
		now += 4_000;
		Assert.True(follower.Detach().Fresh);

		await follower.StepAsync();
		now += 5_001;
		Assert.False(follower.Detach().Fresh);
	}

	[Fact]
	public async Task DetachingTheMirrorHandsOverEveryEntityAndStopsTheSync()
	{
		FakeLiveStore store = new();
		LiveStateWriter writer = Writer(store, "a");
		await ClaimNow(writer, LiveClaim.NotMine, null);
		writer.Set("lobby:1", Data("one"));
		writer.Set(LiveKeys.Queue, Data("q"));
		await writer.FlushAsync();
		LiveStateFollower follower = new(store, "b", () => true, 100, 2500);
		await follower.StepAsync();

		IReadOnlyDictionary<string, string> mirror = follower.Detach().Entities;
		Assert.Equal(new[] { "lobby:1", "queue" }, mirror.Keys.OrderBy(k => k).ToArray());
		Assert.False(follower.InSync);
		Assert.False(follower.Mirror.Loaded);
	}

	[Fact]
	public void WithoutLiveStateEveryCallIsANoOpAndTheStandbyCountsAsInSync()
	{
		Assert.False(RealtimeLiveState.Enabled);
		Assert.False(RealtimeLiveState.Writing);
		Assert.True(RealtimeLiveState.StandbyInSync);
		Assert.True(RealtimeLiveState.Owns(LiveKeys.Queue));
		Assert.True(MatchmakingManager.FormsMatches);
		RealtimeLiveState.Set("lobby:1", "{}");
		RealtimeLiveState.Delete("lobby:1");
		Assert.Empty(RealtimeLiveState.DetachMirror().Entities);
		Assert.False(RealtimeLiveState.DetachMirror().Fresh);
	}
}
