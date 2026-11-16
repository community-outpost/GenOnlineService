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

namespace GenOnlineService.Realtime
{
	// The attempt and step of one pair of lobby members, see realtime-protocol.md section 6.6
	public readonly record struct PeerPairState(int Attempt, int Step);

	// The values one StartSignalling carries for a pair
	public readonly record struct PeerStepValues(int Attempt, string Transport, bool Relay, int TimeoutS);

	// The connection order of a pair: direct GNS, then a plugin transport both members share, then GNS over TURN
	public static class ConnectionOrder
	{
		public const string Gns = "gns";

		public const int DirectStep = 1;
		public const int PluginStep = 2;
		public const int RelayStep = 3;

		// A transport name is 1 to 32 characters of a-z, 0-9 and _, and at most 8 plugin names of one list count
		public const int MaxTransportNameLength = 32;
		public const int MaxPluginTransports = 8;

		// The first StartSignalling of a pair in a lobby
		public static PeerPairState First => new PeerPairState(1, DirectStep);

		// The syntax every transport name follows, gns included
		public static bool IsTransportName(string? name)
		{
			if (String.IsNullOrEmpty(name) || name.Length > MaxTransportNameLength)
			{
				return false;
			}
			foreach (char c in name)
			{
				if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
				{
					return false;
				}
			}
			return true;
		}

		// A name a plugin reports, gns is reserved for the built-in transport
		public static bool IsPluginTransport(string? name) => name != Gns && IsTransportName(name);

		// Keeps the first distinct plugin names in the order sent, anything else is ignored
		public static string[] PluginTransports(IEnumerable<string?>? transports)
		{
			return (transports ?? []).Where(IsPluginTransport).Select(name => name!).Distinct(StringComparer.Ordinal).Take(MaxPluginTransports).ToArray();
		}

		// The plugin transport both members listed, the first in ordinal order when they share several, null when they share none.
		// A null list means the member has not sent SetPeerTransports in its current session and counts as listing none
		public static string? SharedTransport(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
		{
			if (a == null || b == null)
			{
				return null;
			}
			return a.Where(name => b.Contains(name, StringComparer.Ordinal)).Order(StringComparer.Ordinal).FirstOrDefault();
		}

		// Step 2 needs a shared plugin transport, otherwise the pair uses step 3
		public static int Settle(int step, bool shared) => step == PluginStep && !shared ? RelayStep : step;

		public static int Next(int step, bool shared) => step == DirectStep && shared ? PluginStep : RelayStep;

		// A restart counts one more attempt and moves to the next step only when it advances
		public static PeerPairState Restart(PeerPairState? current, bool advance, bool shared)
		{
			if (current is not PeerPairState state)
			{
				return First;
			}

			int step = Settle(state.Step, shared);
			return new PeerPairState(state.Attempt + 1, advance ? Next(step, shared) : step);
		}

		// A RequestSignalling advances on the current attempt, restarts the same step without one, and ignores any other attempt
		public static PeerPairState? OnRequest(PeerPairState? current, int? failedAttempt, bool shared)
		{
			// a report for a pair without state names no attempt the service handed out
			if (current == null && failedAttempt != null)
			{
				return null;
			}
			if (current is not PeerPairState state || failedAttempt == null)
			{
				return Restart(current, false, shared);
			}

			return failedAttempt.Value == state.Attempt ? Restart(state, true, shared) : null;
		}

		// The step values for a StartSignalling from both members' transport lists
		public static PeerStepValues ValuesFor(PeerPairState state, IReadOnlyList<string>? transportsA, IReadOnlyList<string>? transportsB)
		{
			return ValuesOf(state, SharedTransport(transportsA, transportsB));
		}

		// The step values with the shared plugin transport, null when the pair cannot use step 2 for this send
		public static PeerStepValues ValuesOf(PeerPairState state, string? shared)
		{
			return Settle(state.Step, shared != null) switch
			{
				DirectStep => new PeerStepValues(state.Attempt, Gns, false, 6),
				PluginStep => new PeerStepValues(state.Attempt, shared!, false, 10),
				_ => new PeerStepValues(state.Attempt, Gns, true, 0),
			};
		}
	}

	// The connection order of every pair of one lobby, keyed by the unordered pair and used only under the lobby gate
	public sealed class PeerPairs
	{
		private readonly Dictionary<(long Low, long High), PeerPairState> m_pairs = new();

		private static (long Low, long High) Key(long userA, long userB) => (Math.Min(userA, userB), Math.Max(userA, userB));

		// The pair starts at attempt 1 on step 1 with its first StartSignalling
		public PeerPairState Current(long userA, long userB)
		{
			(long Low, long High) key = Key(userA, userB);
			if (!m_pairs.TryGetValue(key, out PeerPairState state))
			{
				state = ConnectionOrder.First;
				m_pairs[key] = state;
			}
			return state;
		}

		public PeerPairState Restart(long userA, long userB, bool advance, bool shared)
		{
			(long Low, long High) key = Key(userA, userB);
			PeerPairState next = ConnectionOrder.Restart(m_pairs.TryGetValue(key, out PeerPairState state) ? state : null, advance, shared);
			m_pairs[key] = next;
			return next;
		}

		// Null when the report changes nothing
		public PeerPairState? Request(long userA, long userB, int? failedAttempt, bool shared)
		{
			(long Low, long High) key = Key(userA, userB);
			PeerPairState? next = ConnectionOrder.OnRequest(m_pairs.TryGetValue(key, out PeerPairState state) ? state : null, failedAttempt, shared);
			if (next is PeerPairState restarted)
			{
				m_pairs[key] = restarted;
			}
			return next;
		}

		// Stores the step the pair is sent on, so a pair that fell back to step 3 stays there
		public PeerPairState Settle(long userA, long userB, bool shared)
		{
			PeerPairState state = Current(userA, userB);
			int step = ConnectionOrder.Settle(state.Step, shared);
			if (step != state.Step)
			{
				state = state with { Step = step };
				m_pairs[Key(userA, userB)] = state;
			}
			return state;
		}

		// Every stored pair, the live state of the lobby carries them
		public IEnumerable<(long Low, long High, PeerPairState State)> All() => m_pairs.Select(entry => (entry.Key.Low, entry.Key.High, entry.Value)).ToList();

		// A pair restored from the live state of another copy
		public void Restore(long userA, long userB, PeerPairState state) => m_pairs[Key(userA, userB)] = state;

		// A member who leaves or joins starts each of its pairs over
		public void Forget(long userID)
		{
			foreach ((long Low, long High) key in m_pairs.Keys.Where(key => key.Low == userID || key.High == userID).ToList())
			{
				m_pairs.Remove(key);
			}
		}
	}
}
