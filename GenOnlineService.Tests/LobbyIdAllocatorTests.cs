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
	// Lobby ids come from reserved blocks and are never handed out twice
	public class LobbyIdAllocatorTests
	{
		// A counter row in memory, each reservation bumps it atomically like the database UPDATE
		private sealed class FakeSource : ILobbyIdSource
		{
			private long m_next;
			public int Reservations;
			public bool Fail;
			public int DelayMs;

			public FakeSource(long next) => m_next = next;

			public async Task<long> ReserveBlockAsync(int size, CancellationToken cancellationToken)
			{
				if (DelayMs > 0)
				{
					await Task.Delay(DelayMs, cancellationToken);
				}
				else
				{
					await Task.Yield();
				}

				if (Fail)
				{
					throw new InvalidOperationException("database down");
				}

				Interlocked.Increment(ref Reservations);
				return Interlocked.Add(ref m_next, size) - size;
			}
		}

		[Fact]
		public async Task IdsStartAtTheCounterAndRefillWithTheNextBlock()
		{
			FakeSource source = new FakeSource(1);
			LobbyIdAllocator allocator = new LobbyIdAllocator(source, blockSize: 5);

			List<long> ids = new List<long>();
			for (int i = 0; i < 12; i++)
			{
				ids.Add(await allocator.NextAsync());
			}

			Assert.Equal(Enumerable.Range(1, 12).Select(i => (long)i), ids.Order());
			Assert.All(ids, id => Assert.True(id > 0));
			Assert.Equal(12, ids.Distinct().Count());
		}

		[Fact]
		public async Task ARestartSkipsTheUnusedRestOfABlock()
		{
			FakeSource source = new FakeSource(1);
			long first = await new LobbyIdAllocator(source, blockSize: 100).NextAsync();
			long afterRestart = await new LobbyIdAllocator(source, blockSize: 100).NextAsync();

			Assert.Equal(1, first);
			Assert.True(afterRestart > first);
		}

		[Fact]
		public async Task ConcurrentCallersAndCopiesNeverGetTheSameId()
		{
			FakeSource source = new FakeSource(1) { DelayMs = 1 };
			LobbyIdAllocator copyA = new LobbyIdAllocator(source, blockSize: 7);
			LobbyIdAllocator copyB = new LobbyIdAllocator(source, blockSize: 7);

			Task<long>[] calls = Enumerable.Range(0, 2000)
				.Select(i => Task.Run(() => (i % 2 == 0 ? copyA : copyB).NextAsync()))
				.ToArray();
			long[] ids = await Task.WhenAll(calls);

			Assert.Equal(ids.Length, ids.Distinct().Count());
			Assert.All(ids, id => Assert.True(id > 0));
		}

		[Fact]
		public async Task AFailedReservationFailsTheCallAndHandsOutNothing()
		{
			FakeSource source = new FakeSource(1) { Fail = true };
			LobbyIdAllocator allocator = new LobbyIdAllocator(source, blockSize: 3);

			await Assert.ThrowsAsync<InvalidOperationException>(() => allocator.NextAsync());

			source.Fail = false;
			Assert.Equal(1, await allocator.NextAsync());
		}

		[Fact]
		public async Task ABlockStartingAtZeroIsRefused()
		{
			LobbyIdAllocator allocator = new LobbyIdAllocator(new FakeSource(0), blockSize: 3);

			await Assert.ThrowsAsync<InvalidOperationException>(() => allocator.NextAsync());
		}
	}
}
