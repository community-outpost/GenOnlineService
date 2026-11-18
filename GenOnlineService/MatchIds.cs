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

// A 48 bit match id: 32 bits of seconds since the epoch, 5 bits of worker slot, 11 bits of sequence.
// Every id is above 0, fits a JSON safe integer and is larger than every match id made before the switch.
public static class MatchIdLayout
{
	public static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	public const int WorkerBits = 5;
	public const int SequenceBits = 11;
	public const int WorkerCount = 1 << WorkerBits;
	public const int SequenceCount = 1 << SequenceBits;
	public const long MaximumSecond = (1L << 32) - 1;

	public static long Compose(long second, int worker, int sequence)
	{
		if (second < 1 || second > MaximumSecond)
		{
			throw new ArgumentOutOfRangeException(nameof(second));
		}
		if (worker < 0 || worker >= WorkerCount)
		{
			throw new ArgumentOutOfRangeException(nameof(worker));
		}
		if (sequence < 0 || sequence >= SequenceCount)
		{
			throw new ArgumentOutOfRangeException(nameof(sequence));
		}
		return (second << (WorkerBits + SequenceBits)) | ((long)worker << SequenceBits) | (long)sequence;
	}

	public static long SecondOf(long id) => id >> (WorkerBits + SequenceBits);
	public static int WorkerOf(long id) => (int)((id >> SequenceBits) & (WorkerCount - 1));
	public static int SequenceOf(long id) => (int)(id & (SequenceCount - 1));

	// Whole seconds since the epoch, rounded down
	public static long SecondAt(DateTimeOffset time) => (long)Math.Floor((time - Epoch).TotalSeconds);

	// The time a snowflake match id was made, to the second
	public static DateTimeOffset MadeAt(long id) => Epoch.AddSeconds(SecondOf(id));
}

// A worker slot held by this service copy: ids use FirstSecond to ReservedUntil
public sealed record MatchIdSlot(int WorkerID, long Generation, long FirstSecond, long ReservedUntil);

public interface IMatchIdWorkerStore
{
	// A free slot, null when all are reserved
	Task<MatchIdSlot?> AcquireAsync(string holder, long nowSecond, int windowSeconds, CancellationToken cancellationToken);

	// False once another copy took the slot
	Task<bool> ExtendAsync(MatchIdSlot slot, long reservedUntil, CancellationToken cancellationToken);
}

public sealed class DatabaseMatchIdWorkerStore : IMatchIdWorkerStore
{
	private readonly IDbContextFactory<AppDbContext> m_dbFactory;

	public DatabaseMatchIdWorkerStore(IDbContextFactory<AppDbContext> dbFactory)
	{
		m_dbFactory = dbFactory;
	}

	public async Task<MatchIdSlot?> AcquireAsync(string holder, long nowSecond, int windowSeconds, CancellationToken cancellationToken)
	{
		await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(cancellationToken);
		return await Database.MatchIdWorkers.Acquire(db, holder, nowSecond, windowSeconds, cancellationToken);
	}

	public async Task<bool> ExtendAsync(MatchIdSlot slot, long reservedUntil, CancellationToken cancellationToken)
	{
		await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(cancellationToken);
		return await Database.MatchIdWorkers.Extend(db, slot, reservedUntil, cancellationToken);
	}
}

// Makes match ids without a database round trip per id. A copy holds one worker slot and writes ahead in the database the last
// second it may use, so a copy that takes the slot later (after a restart, a crash or on another node) starts after that second.
// Uniqueness never depends on the clocks of the copies agreeing; a clock that runs backwards only keeps the last second in use.
public sealed class MatchIdGenerator
{
	public const int DefaultWindowSeconds = 60;

	// How far ids may borrow seconds ahead of the clock once a second's sequence is used up. After the clock went backwards the
	// generator stays on its last second, and borrows only within this limit of the clock
	public const int MaximumAheadSeconds = 5;

	private static readonly ILogger s_log = AppLog.For(typeof(MatchIdGenerator));

	private readonly IMatchIdWorkerStore m_store;
	private readonly TimeProvider m_time;
	private readonly string m_holder;
	private readonly int m_windowSeconds;

	// one id at a time; the store is only called when the reservation runs out, about once per window
	private readonly SemaphoreSlim m_gate = new(1, 1);
	private MatchIdSlot? m_slot;
	private long m_second;
	private int m_sequence;

	public MatchIdGenerator(IMatchIdWorkerStore store, TimeProvider time, string holder, int windowSeconds = DefaultWindowSeconds)
	{
		if (windowSeconds < MaximumAheadSeconds + 1)
		{
			throw new ArgumentOutOfRangeException(nameof(windowSeconds));
		}

		m_store = store;
		m_time = time;
		m_holder = holder;
		m_windowSeconds = windowSeconds;
	}

	// The name a copy writes into the slot it holds
	public static string DefaultHolder() => $"{Environment.MachineName}:{Environment.ProcessId}";

	// Never returns an id twice across every copy that shares the database, a failure throws and hands out nothing
	public async Task<long> NextAsync(CancellationToken cancellationToken = default)
	{
		await m_gate.WaitAsync(cancellationToken);
		try
		{
			long now = MatchIdLayout.SecondAt(m_time.GetUtcNow());

			long second;
			int sequence;
			if (m_slot == null || now > m_second)
			{
				second = now;
				sequence = 0;
			}
			else if (m_sequence + 1 < MatchIdLayout.SequenceCount)
			{
				second = m_second;
				sequence = m_sequence + 1;
			}
			else
			{
				// the sequence of the last second is used up: borrow the next second, but never run far ahead of the clock
				second = m_second + 1;
				sequence = 0;
				if (second - now > MaximumAheadSeconds)
				{
					throw new InvalidOperationException($"Match ids would run more than {MaximumAheadSeconds} s ahead of the clock");
				}
			}

			if (m_slot == null || second > m_slot.ReservedUntil)
			{
				MatchIdSlot? held = m_slot;
				long until = second + m_windowSeconds;
				if (held != null && await m_store.ExtendAsync(held, until, cancellationToken))
				{
					m_slot = held with { ReservedUntil = until };
				}
				else
				{
					if (held != null)
					{
						s_log.LogWarning("Match id worker slot {Worker} was taken by another copy, taking a new slot", held.WorkerID);
					}

					// forget the old slot first, so a failure below does not leave ids of a slot that is no longer ours
					m_slot = null;
					MatchIdSlot slot = await m_store.AcquireAsync(m_holder, now, m_windowSeconds, cancellationToken)
						?? throw new InvalidOperationException("Every match id worker slot is reserved");
					if (slot.FirstSecond > slot.ReservedUntil || slot.WorkerID < 0 || slot.WorkerID >= MatchIdLayout.WorkerCount)
					{
						throw new InvalidOperationException($"Match id worker slot {slot.WorkerID} has an invalid reservation");
					}

					m_slot = slot;
					second = Math.Max(now, slot.FirstSecond);
					sequence = 0;
					s_log.LogInformation("Holding match id worker slot {Worker} from second {First}", slot.WorkerID, slot.FirstSecond);
				}
			}

			long id = MatchIdLayout.Compose(second, m_slot.WorkerID, sequence);
			m_second = second;
			m_sequence = sequence;
			return id;
		}
		finally
		{
			m_gate.Release();
		}
	}
}
