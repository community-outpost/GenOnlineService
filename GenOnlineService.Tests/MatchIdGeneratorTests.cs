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

using Xunit;

namespace GenOnlineService.Tests
{
	// Snowflake match ids: layout, order, the per-copy worker slot and its reservation, and never handing out an id twice
	public class MatchIdGeneratorTests
	{
		private static readonly DateTimeOffset s_start = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

		private sealed class FakeClock : TimeProvider
		{
			public DateTimeOffset Now;
			public FakeClock(DateTimeOffset now) => Now = now;
			public override DateTimeOffset GetUtcNow() => Now;
			public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
		}

		// The match_id_workers rows in memory, each change conditioned on the generation like the database UPDATE statements
		private sealed class FakeStore : IMatchIdWorkerStore
		{
			public readonly long[] Generation;
			public readonly long[] ReservedUntil;
			public readonly string[] Holder;
			public int Acquisitions;
			public int Extensions;
			public bool Fail;

			public FakeStore(int slots = MatchIdLayout.WorkerCount)
			{
				Generation = new long[slots];
				ReservedUntil = new long[slots];
				Holder = Enumerable.Repeat(String.Empty, slots).ToArray();
			}

			public Task<MatchIdSlot?> AcquireAsync(string holder, long nowSecond, int windowSeconds, CancellationToken cancellationToken)
			{
				if (Fail)
				{
					throw new InvalidOperationException("database down");
				}

				lock (this)
				{
					int? free = Enumerable.Range(0, Generation.Length)
						.Where(w => ReservedUntil[w] < nowSecond)
						.OrderBy(w => ReservedUntil[w]).ThenBy(w => w)
						.Select(w => (int?)w)
						.FirstOrDefault();
					if (free == null)
					{
						return Task.FromResult<MatchIdSlot?>(null);
					}

					int worker = free.Value;
					long first = Math.Max(nowSecond, ReservedUntil[worker] + 1);
					Generation[worker]++;
					ReservedUntil[worker] = first + windowSeconds;
					Holder[worker] = holder;
					Acquisitions++;
					return Task.FromResult<MatchIdSlot?>(new MatchIdSlot(worker, Generation[worker], first, ReservedUntil[worker]));
				}
			}

			public Task<bool> ExtendAsync(MatchIdSlot slot, long reservedUntil, CancellationToken cancellationToken)
			{
				if (Fail)
				{
					throw new InvalidOperationException("database down");
				}

				lock (this)
				{
					if (Generation[slot.WorkerID] != slot.Generation || ReservedUntil[slot.WorkerID] > reservedUntil)
					{
						return Task.FromResult(false);
					}
					ReservedUntil[slot.WorkerID] = reservedUntil;
					Extensions++;
					return Task.FromResult(true);
				}
			}

			// Another copy takes the slot, as after this copy's reservation ran out
			public void TakeOver(int worker, long reservedUntil)
			{
				lock (this)
				{
					Generation[worker]++;
					ReservedUntil[worker] = reservedUntil;
					Holder[worker] = "other";
				}
			}
		}

		private static long Second(DateTimeOffset time) => MatchIdLayout.SecondAt(time);

		[Fact]
		public void LayoutPacksSecondWorkerAndSequence()
		{
			long id = MatchIdLayout.Compose(123_456, 17, 2_000);

			Assert.Equal(123_456, MatchIdLayout.SecondOf(id));
			Assert.Equal(17, MatchIdLayout.WorkerOf(id));
			Assert.Equal(2_000, MatchIdLayout.SequenceOf(id));
			Assert.Equal(MatchIdLayout.Epoch.AddSeconds(123_456), MatchIdLayout.MadeAt(id));

			// the largest id is a 48 bit value, so a JSON number stays exact for every client
			long largest = MatchIdLayout.Compose(MatchIdLayout.MaximumSecond, MatchIdLayout.WorkerCount - 1, MatchIdLayout.SequenceCount - 1);
			Assert.Equal((1L << 48) - 1, largest);
			Assert.True(largest < (1L << 53));
		}

		[Fact]
		public void LayoutRefusesValuesOutsideTheirFields()
		{
			// second 0 would allow id 0, which every client reads as no match
			Assert.Throws<ArgumentOutOfRangeException>(() => MatchIdLayout.Compose(0, 0, 0));
			Assert.Throws<ArgumentOutOfRangeException>(() => MatchIdLayout.Compose(MatchIdLayout.MaximumSecond + 1, 0, 0));
			Assert.Throws<ArgumentOutOfRangeException>(() => MatchIdLayout.Compose(1, MatchIdLayout.WorkerCount, 0));
			Assert.Throws<ArgumentOutOfRangeException>(() => MatchIdLayout.Compose(1, 0, MatchIdLayout.SequenceCount));
		}

		[Fact]
		public async Task IdsOfOneSecondCountUpTheSequenceOfTheHeldSlot()
		{
			FakeStore store = new FakeStore();
			MatchIdGenerator generator = new MatchIdGenerator(store, new FakeClock(s_start), "copy-a");

			long first = await generator.NextAsync();
			long second = await generator.NextAsync();
			long third = await generator.NextAsync();

			Assert.Equal(new[] { 0, 1, 2 }, new[] { first, second, third }.Select(MatchIdLayout.SequenceOf));
			Assert.All(new[] { first, second, third }, id => Assert.Equal(Second(s_start), MatchIdLayout.SecondOf(id)));
			Assert.All(new[] { first, second, third }, id => Assert.Equal(0, MatchIdLayout.WorkerOf(id)));
			Assert.True(first < second && second < third);
			Assert.Equal(1, store.Acquisitions);
			Assert.Equal("copy-a", store.Holder[0]);
		}

		[Fact]
		public async Task NewIdsAreFarAboveSequentialMatchIds()
		{
			MatchIdGenerator generator = new MatchIdGenerator(new FakeStore(), new FakeClock(s_start), "copy-a");

			long id = await generator.NextAsync();

			// sequential ids are far below a trillion, so the newest-first order and the after_id cursor keep working
			Assert.True(id > 1_000_000_000_000);
			Assert.Equal(s_start, MatchIdLayout.MadeAt(id));
		}

		[Fact]
		public async Task ANewSecondStartsTheSequenceAgain()
		{
			FakeClock clock = new FakeClock(s_start);
			MatchIdGenerator generator = new MatchIdGenerator(new FakeStore(), clock, "copy-a");

			long before = await generator.NextAsync();
			await generator.NextAsync();
			clock.Advance(1);
			long after = await generator.NextAsync();

			Assert.Equal(Second(s_start) + 1, MatchIdLayout.SecondOf(after));
			Assert.Equal(0, MatchIdLayout.SequenceOf(after));
			Assert.True(after > before);
		}

		[Fact]
		public async Task AUsedUpSecondBorrowsTheNextSecondsUpToTheLimit()
		{
			FakeClock clock = new FakeClock(s_start);
			MatchIdGenerator generator = new MatchIdGenerator(new FakeStore(), clock, "copy-a");

			int allowed = MatchIdLayout.SequenceCount * (MatchIdGenerator.MaximumAheadSeconds + 1);
			HashSet<long> ids = new HashSet<long>();
			long previous = 0;
			for (int i = 0; i < allowed; i++)
			{
				long id = await generator.NextAsync();
				Assert.True(id > previous);
				Assert.True(ids.Add(id));
				previous = id;
			}

			Assert.Equal(Second(s_start) + MatchIdGenerator.MaximumAheadSeconds, MatchIdLayout.SecondOf(previous));
			await Assert.ThrowsAsync<InvalidOperationException>(() => generator.NextAsync());

			// once the clock catches up the generator continues after the borrowed seconds
			clock.Advance(MatchIdGenerator.MaximumAheadSeconds + 1);
			long next = await generator.NextAsync();
			Assert.True(next > previous);
		}

		[Fact]
		public async Task AClockGoingBackwardsKeepsIdsIncreasing()
		{
			FakeClock clock = new FakeClock(s_start);
			MatchIdGenerator generator = new MatchIdGenerator(new FakeStore(), clock, "copy-a");

			long before = await generator.NextAsync();
			clock.Advance(-30);
			long after = await generator.NextAsync();

			Assert.True(after > before);
			Assert.Equal(Second(s_start), MatchIdLayout.SecondOf(after));
		}

		[Fact]
		public async Task TheEndOfTheReservationExtendsTheSameSlot()
		{
			FakeClock clock = new FakeClock(s_start);
			FakeStore store = new FakeStore();
			MatchIdGenerator generator = new MatchIdGenerator(store, clock, "copy-a", windowSeconds: 10);

			await generator.NextAsync();
			clock.Advance(11);
			long id = await generator.NextAsync();

			Assert.Equal(0, MatchIdLayout.WorkerOf(id));
			Assert.Equal(1, store.Acquisitions);
			Assert.Equal(1, store.Extensions);
			Assert.Equal(Second(s_start) + 11 + 10, store.ReservedUntil[0]);
		}

		[Fact]
		public async Task ATakenSlotMovesTheCopyToAnotherSlot()
		{
			FakeClock clock = new FakeClock(s_start);
			FakeStore store = new FakeStore(slots: 2);
			MatchIdGenerator generator = new MatchIdGenerator(store, clock, "copy-a", windowSeconds: 10);

			long before = await generator.NextAsync();
			Assert.Equal(0, MatchIdLayout.WorkerOf(before));

			// the copy paused past its reservation, another copy took slot 0
			clock.Advance(20);
			store.TakeOver(0, Second(clock.Now) + 10);

			long after = await generator.NextAsync();
			Assert.Equal(1, MatchIdLayout.WorkerOf(after));
			Assert.Equal(Second(clock.Now), MatchIdLayout.SecondOf(after));
			Assert.Equal(2, store.Acquisitions);
		}

		[Fact]
		public async Task ACopyThatLostItsSlotAndFindsNoFreeOneHandsOutNothing()
		{
			FakeClock clock = new FakeClock(s_start);
			FakeStore store = new FakeStore(slots: 1);
			MatchIdGenerator copyA = new MatchIdGenerator(store, clock, "copy-a", windowSeconds: 10);
			MatchIdGenerator copyB = new MatchIdGenerator(store, clock, "copy-b", windowSeconds: 10);

			long lastOfA = await copyA.NextAsync();
			long reservedByA = store.ReservedUntil[0];

			// copy A stalls past its reservation, copy B takes the only slot and starts after A's last second
			clock.Advance(11);
			long firstOfB = await copyB.NextAsync();
			Assert.True(MatchIdLayout.SecondOf(firstOfB) > reservedByA);
			Assert.True(firstOfB > lastOfA);

			// copy A comes back: its slot is gone and no other is free
			await Assert.ThrowsAsync<InvalidOperationException>(() => copyA.NextAsync());
			Assert.Equal("copy-b", store.Holder[0]);
		}

		[Fact]
		public async Task AFailedStoreHandsOutNothingAndTheNextCallRecovers()
		{
			FakeStore store = new FakeStore { Fail = true };
			MatchIdGenerator generator = new MatchIdGenerator(store, new FakeClock(s_start), "copy-a");

			await Assert.ThrowsAsync<InvalidOperationException>(() => generator.NextAsync());

			store.Fail = false;
			long id = await generator.NextAsync();
			Assert.Equal(0, MatchIdLayout.SequenceOf(id));
			Assert.Equal(1, store.Acquisitions);
		}

		[Fact]
		public async Task CopiesSharingTheSlotsNeverHandOutTheSameId()
		{
			FakeClock clock = new FakeClock(s_start);
			FakeStore store = new FakeStore(slots: 4);
			MatchIdGenerator[] copies =
			{
				new MatchIdGenerator(store, clock, "copy-a", windowSeconds: 10),
				new MatchIdGenerator(store, clock, "copy-b", windowSeconds: 10),
			};

			HashSet<long> ids = new HashSet<long>();
			for (int round = 0; round < 400; round++)
			{
				foreach (MatchIdGenerator copy in copies)
				{
					Assert.True(ids.Add(await copy.NextAsync()));
				}

				clock.Advance(0.25);
				// now and then a third copy takes over whichever slot has run out, as a restart would
				if (round % 50 == 49)
				{
					MatchIdGenerator restarted = new MatchIdGenerator(store, clock, $"copy-r{round}", windowSeconds: 10);
					clock.Advance(11);
					Assert.True(ids.Add(await restarted.NextAsync()));
				}
			}

			// all of them under 2^53 and above 0
			Assert.All(ids, id => Assert.InRange(id, 1, (1L << 53) - 1));
		}
	}
}
