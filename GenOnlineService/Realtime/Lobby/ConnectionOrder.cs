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

	// The connection order of a pair: direct GNS, then EOS P2P, then GNS over TURN
	public static class ConnectionOrder
	{
		public const string Gns = "gns";
		public const string Eos = "eos";

		public const int DirectStep = 1;
		public const int EosStep = 2;
		public const int RelayStep = 3;

		// The first StartSignalling of a pair in a lobby
		public static PeerPairState First => new PeerPairState(1, DirectStep);

		// Keeps the known transport names only, so unknown names are ignored
		public static string[] KnownTransports(IEnumerable<string?>? transports)
		{
			return (transports ?? []).Where(name => name == Eos).Distinct(StringComparer.Ordinal).Select(name => name!).ToArray();
		}

		// Null lists mean the member has not sent SetPeerTransports in its current session
		public static bool BothListEos(IReadOnlyList<string>? a, IReadOnlyList<string>? b) => a != null && b != null && a.Contains(Eos) && b.Contains(Eos);

		// Step 2 needs eos on both members, otherwise the pair uses step 3
		public static int Settle(int step, bool bothEos) => step == EosStep && !bothEos ? RelayStep : step;

		public static int Next(int step, bool bothEos) => step == DirectStep && bothEos ? EosStep : RelayStep;

		// A restart counts one more attempt and moves to the next step only when it advances
		public static PeerPairState Restart(PeerPairState? current, bool advance, bool bothEos)
		{
			if (current is not PeerPairState state)
			{
				return First;
			}

			int step = Settle(state.Step, bothEos);
			return new PeerPairState(state.Attempt + 1, advance ? Next(step, bothEos) : step);
		}

		// A RequestSignalling advances on the current attempt, restarts the same step without one, and ignores any other attempt
		public static PeerPairState? OnRequest(PeerPairState? current, int? failedAttempt, bool bothEos)
		{
			// a report for a pair without state names no attempt the service handed out
			if (current == null && failedAttempt != null)
			{
				return null;
			}
			if (current is not PeerPairState state || failedAttempt == null)
			{
				return Restart(current, false, bothEos);
			}

			return failedAttempt.Value == state.Attempt ? Restart(state, true, bothEos) : null;
		}

		// The step values for a StartSignalling, null for the old shape when either member has not sent SetPeerTransports
		public static PeerStepValues? ValuesFor(PeerPairState state, IReadOnlyList<string>? transportsA, IReadOnlyList<string>? transportsB)
		{
			return transportsA == null || transportsB == null ? null : ValuesOf(state, BothListEos(transportsA, transportsB));
		}

		public static PeerStepValues ValuesOf(PeerPairState state, bool bothEos)
		{
			return Settle(state.Step, bothEos) switch
			{
				DirectStep => new PeerStepValues(state.Attempt, Gns, false, 6),
				EosStep => new PeerStepValues(state.Attempt, Eos, false, 10),
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

		public PeerPairState Restart(long userA, long userB, bool advance, bool bothEos)
		{
			(long Low, long High) key = Key(userA, userB);
			PeerPairState next = ConnectionOrder.Restart(m_pairs.TryGetValue(key, out PeerPairState state) ? state : null, advance, bothEos);
			m_pairs[key] = next;
			return next;
		}

		// Null when the report changes nothing
		public PeerPairState? Request(long userA, long userB, int? failedAttempt, bool bothEos)
		{
			(long Low, long High) key = Key(userA, userB);
			PeerPairState? next = ConnectionOrder.OnRequest(m_pairs.TryGetValue(key, out PeerPairState state) ? state : null, failedAttempt, bothEos);
			if (next is PeerPairState restarted)
			{
				m_pairs[key] = restarted;
			}
			return next;
		}

		// Stores the step the pair is sent on, so a pair that fell back to step 3 stays there
		public PeerPairState Settle(long userA, long userB, bool bothEos)
		{
			PeerPairState state = Current(userA, userB);
			int step = ConnectionOrder.Settle(state.Step, bothEos);
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
