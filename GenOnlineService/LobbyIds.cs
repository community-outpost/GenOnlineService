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

using Microsoft.EntityFrameworkCore;

namespace GenOnlineService;

// Reserves a block of lobby ids that no other caller or service copy ever gets, returns the first id of [first, first + size)
public interface ILobbyIdSource
{
	Task<long> ReserveBlockAsync(int size, CancellationToken cancellationToken);
}

// Reserves blocks from the lobby_ids counter row shared by every service copy
public sealed class DatabaseLobbyIdSource : ILobbyIdSource
{
	private readonly IDbContextFactory<AppDbContext> m_dbFactory;

	public DatabaseLobbyIdSource(IDbContextFactory<AppDbContext> dbFactory)
	{
		m_dbFactory = dbFactory;
	}

	public async Task<long> ReserveBlockAsync(int size, CancellationToken cancellationToken)
	{
		await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(cancellationToken);
		return await Database.LobbyIds.ReserveBlock(db, size, cancellationToken);
	}
}

// Hands out lobby ids from reserved blocks so most lobbies cost no database round trip, unused ids of a block are skipped after a restart
public sealed class LobbyIdAllocator
{
	public const int DefaultBlockSize = 100;

	private static readonly ILogger s_log = AppLog.For(typeof(LobbyIdAllocator));

	private readonly ILobbyIdSource m_source;
	private readonly int m_blockSize;
	private readonly int m_prefetchBelow;

	// guards the current block and the spare block, ids leave only under this lock
	private readonly object m_lock = new();
	private long m_next;
	private long m_end;
	private long m_spareFirst;
	private long m_spareEnd;

	// one reservation at a time, so a refill never overwrites a block that still has ids
	private readonly SemaphoreSlim m_reserve = new(1, 1);

	public LobbyIdAllocator(ILobbyIdSource source, int blockSize = DefaultBlockSize)
	{
		if (blockSize < 1)
		{
			throw new ArgumentOutOfRangeException(nameof(blockSize));
		}

		m_source = source;
		m_blockSize = blockSize;
		m_prefetchBelow = Math.Max(1, blockSize / 5);
	}

	// Never returns an id twice and never 0 or less, a failed reservation throws and hands out nothing
	public async Task<long> NextAsync(CancellationToken cancellationToken = default)
	{
		while (true)
		{
			if (TryTake(out long id))
			{
				return id;
			}

			await m_reserve.WaitAsync(cancellationToken);
			try
			{
				// another caller or the prefetch may have refilled while this one waited
				if (HasIds())
				{
					continue;
				}

				Install(await ReserveAsync(cancellationToken));
			}
			finally
			{
				m_reserve.Release();
			}
		}
	}

	private bool TryTake(out long id)
	{
		bool prefetch;
		lock (m_lock)
		{
			if (m_next >= m_end && m_spareEnd > m_spareFirst)
			{
				m_next = m_spareFirst;
				m_end = m_spareEnd;
				m_spareFirst = m_spareEnd = 0;
			}

			if (m_next >= m_end)
			{
				id = 0;
				return false;
			}

			id = m_next++;
			prefetch = m_end - m_next < m_prefetchBelow && m_spareEnd == m_spareFirst;
		}

		if (prefetch)
		{
			_ = PrefetchAsync();
		}
		return true;
	}

	private bool HasIds()
	{
		lock (m_lock)
		{
			return m_next < m_end || m_spareEnd > m_spareFirst;
		}
	}

	private async Task<long> ReserveAsync(CancellationToken cancellationToken)
	{
		long first = await m_source.ReserveBlockAsync(m_blockSize, cancellationToken);
		if (first < 1 || first > Database.LobbyIds.MaximumID - m_blockSize)
		{
			throw new InvalidOperationException($"Reserved lobby id block starts out of range at {first}");
		}
		return first;
	}

	// Only a holder of m_reserve installs, so the current block is empty or the spare slot is free
	private void Install(long first)
	{
		lock (m_lock)
		{
			if (m_next >= m_end)
			{
				m_next = first;
				m_end = first + m_blockSize;
			}
			else
			{
				m_spareFirst = first;
				m_spareEnd = first + m_blockSize;
			}
		}
	}

	// Reserves the next block in the background while the current one runs low, a failure leaves the refill to the next caller
	private async Task PrefetchAsync()
	{
		if (!await m_reserve.WaitAsync(0))
		{
			return;
		}

		try
		{
			lock (m_lock)
			{
				if (m_spareEnd > m_spareFirst)
				{
					return;
				}
			}

			Install(await ReserveAsync(CancellationToken.None));
		}
		catch (Exception ex)
		{
			s_log.LogWarning(ex, "Reserving the next lobby id block failed");
		}
		finally
		{
			m_reserve.Release();
		}
	}
}
