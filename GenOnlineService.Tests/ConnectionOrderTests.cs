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
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests
{
	// The per pair connection order of realtime-protocol.md section 6.6
	public class ConnectionOrderTests
	{
		private static readonly JsonSerializerOptions s_options = RealtimeJson.Create();
		private static readonly string[] s_eos = [ConnectionOrder.Eos];
		private static readonly string[] s_none = [];

		[Fact]
		public void FirstStartSignallingIsDirectGnsForSixSeconds()
		{
			PeerPairs pairs = new PeerPairs();
			PeerPairState state = pairs.Current(7, 3);

			Assert.Equal(new PeerPairState(1, 1), state);
			Assert.Equal(new PeerStepValues(1, "gns", false, 6), ConnectionOrder.ValuesFor(state, s_eos, s_eos));
			Assert.Equal(state, pairs.Current(3, 7));
		}

		[Fact]
		public void AReportForAPairWithoutStateChangesNothing()
		{
			PeerPairs pairs = new PeerPairs();
			Assert.Null(pairs.Request(3, 7, 1, true));
			Assert.Equal(new PeerPairState(1, 1), pairs.Request(3, 7, null, true));
		}

		[Fact]
		public void APairThatFellBackToTurnStaysThereWhenEosReturns()
		{
			PeerPairs pairs = new PeerPairs();
			pairs.Current(3, 7);
			pairs.Request(3, 7, 1, true);
			Assert.Equal(new PeerPairState(2, ConnectionOrder.RelayStep), pairs.Settle(3, 7, false));
			Assert.Equal(new PeerPairState(3, ConnectionOrder.RelayStep), pairs.Restart(7, 3, false, true));
		}

		[Fact]
		public void AnEosStepSentWithoutEosValuesKeepsItsStoredStep()
		{
			PeerPairs pairs = new PeerPairs();
			pairs.Current(3, 7);
			PeerPairState state = pairs.Request(3, 7, 1, true)!.Value;

			// a send without both middleware ids uses the turn values once
			Assert.Equal(new PeerStepValues(2, "gns", true, 0), ConnectionOrder.ValuesOf(state, false));
			Assert.Equal(ConnectionOrder.EosStep, pairs.Current(3, 7).Step);
			Assert.Equal(new PeerStepValues(3, "eos", false, 10), ConnectionOrder.ValuesOf(pairs.Restart(3, 7, false, true), true));
		}

		[Fact]
		public void FailedAttemptsWalkGnsThenEosThenTurn()
		{
			PeerPairs pairs = new PeerPairs();
			pairs.Current(3, 7);

			PeerPairState? second = pairs.Request(3, 7, 1, true);
			Assert.Equal(new PeerStepValues(2, "eos", false, 10), ConnectionOrder.ValuesFor(second!.Value, s_eos, s_eos));

			PeerPairState? third = pairs.Request(7, 3, 2, true);
			Assert.Equal(new PeerStepValues(3, "gns", true, 0), ConnectionOrder.ValuesFor(third!.Value, s_eos, s_eos));
		}

		[Fact]
		public void StepTwoIsSkippedUnlessBothMembersListEos()
		{
			PeerPairs pairs = new PeerPairs();
			pairs.Current(3, 7);

			PeerPairState? next = pairs.Request(3, 7, 1, ConnectionOrder.BothListEos(s_eos, s_none));
			Assert.Equal(new PeerPairState(2, 3), next);
			Assert.Equal(new PeerStepValues(2, "gns", true, 0), ConnectionOrder.ValuesFor(next!.Value, s_eos, s_none));
		}

		[Fact]
		public void EmptyTransportListStillSendsTheStepFields()
		{
			Assert.False(ConnectionOrder.BothListEos(s_none, s_none));
			Assert.Equal(new PeerStepValues(1, "gns", false, 6), ConnectionOrder.ValuesFor(ConnectionOrder.First, s_none, s_none));
		}

		[Fact]
		public void StepThreeIsSticky()
		{
			PeerPairs pairs = new PeerPairs();
			pairs.Current(3, 7);
			pairs.Request(3, 7, 1, true);
			pairs.Request(3, 7, 2, true);

			Assert.Equal(new PeerPairState(4, 3), pairs.Request(3, 7, 3, true));
			Assert.Equal(new PeerPairState(5, 3), pairs.Restart(3, 7, true, true));
			Assert.Equal(new PeerPairState(6, 3), pairs.Request(3, 7, null, true));
		}

		[Fact]
		public void StaleFailedAttemptChangesNothing()
		{
			PeerPairs pairs = new PeerPairs();
			pairs.Current(3, 7);
			pairs.Request(3, 7, 1, true);

			// the other peer reports the same failed attempt after the pair already advanced
			Assert.Null(pairs.Request(7, 3, 1, true));
			Assert.Equal(new PeerPairState(2, 2), pairs.Current(3, 7));
		}

		[Fact]
		public void HigherFailedAttemptChangesNothing()
		{
			PeerPairs pairs = new PeerPairs();
			pairs.Current(3, 7);

			Assert.Null(pairs.Request(3, 7, 5, true));
			Assert.Equal(new PeerPairState(1, 1), pairs.Current(3, 7));
		}

		[Fact]
		public void MissingFailedAttemptRestartsTheSameStep()
		{
			PeerPairs pairs = new PeerPairs();
			pairs.Current(3, 7);

			Assert.Equal(new PeerPairState(2, 1), pairs.Request(3, 7, null, true));
			pairs.Request(3, 7, 2, true);
			Assert.Equal(new PeerPairState(4, 2), pairs.Request(7, 3, null, true));
		}

		[Fact]
		public void MeshCheckRestartAdvancesThePair()
		{
			PeerPairs pairs = new PeerPairs();
			pairs.Current(3, 7);

			Assert.Equal(new PeerPairState(2, 2), pairs.Restart(3, 7, true, true));
			Assert.Equal(new PeerPairState(3, 3), pairs.Restart(7, 3, true, true));
		}

		[Fact]
		public void ReconnectRestartKeepsTheStep()
		{
			PeerPairs pairs = new PeerPairs();
			pairs.Current(3, 7);

			Assert.Equal(new PeerPairState(2, 1), pairs.Restart(3, 7, false, true));
		}

		[Fact]
		public void RejoinStartsThePairOver()
		{
			PeerPairs pairs = new PeerPairs();
			pairs.Current(3, 7);
			pairs.Current(3, 9);
			pairs.Request(3, 7, 1, true);
			pairs.Request(3, 9, 1, true);
			pairs.Current(7, 9);
			pairs.Request(7, 9, 1, true);

			pairs.Forget(3);

			Assert.Equal(new PeerPairState(1, 1), pairs.Current(7, 3));
			Assert.Equal(new PeerPairState(1, 1), pairs.Current(9, 3));
			Assert.Equal(new PeerPairState(2, 2), pairs.Current(7, 9));
		}

		[Fact]
		public void OldShapeWhenEitherMemberNeverSent()
		{
			Assert.Null(ConnectionOrder.ValuesFor(ConnectionOrder.First, null, s_eos));
			Assert.Null(ConnectionOrder.ValuesFor(ConnectionOrder.First, s_eos, null));
			Assert.Null(ConnectionOrder.ValuesFor(ConnectionOrder.First, null, null));
			Assert.False(ConnectionOrder.BothListEos(null, s_eos));
		}

		[Fact]
		public void OldShapeLeavesTheFourFieldsOut()
		{
			StartSignallingEvent signal = new StartSignallingEvent { LobbyId = 991, UserId = 7, PreferredPort = 8088, MiddlewareId = "m" };
			Assert.Equal("{\"lobby_id\":991,\"user_id\":7,\"preferred_port\":8088,\"middleware_id\":\"m\"}", JsonSerializer.Serialize(signal, s_options));
		}

		[Fact]
		public void RequestWithoutFailedAttemptRoundTrips()
		{
			RequestSignallingRequest request = JsonSerializer.Deserialize<RequestSignallingRequest>("{\"target_user_id\":7}", s_options)!;
			Assert.Null(request.FailedAttempt);
			Assert.Equal("{\"target_user_id\":7}", JsonSerializer.Serialize(request, s_options));
		}

		[Fact]
		public void SessionTransportsAreReplacedWholeAndIgnoreUnknownNames()
		{
			UserSession session = new UserSession(7, EUserSessionType.None, "", "", "", 0, 0);
			Assert.Null(session.PeerTransports);

			session.SetPeerTransports(["eos", "quic", "eos"]);
			Assert.Equal(s_eos, session.PeerTransports);

			session.SetPeerTransports([]);
			Assert.Empty(session.PeerTransports!);

			session.SetPeerTransports(null);
			Assert.Empty(session.PeerTransports!);
		}

		[Fact]
		public void AnticheatRelayIsGone()
		{
			Assert.Null(typeof(RealtimeHub).GetMethod("AnticheatMessage", BindingFlags.Public | BindingFlags.Instance));
			Assert.Null(typeof(IRealtimeClient).GetMethod("AnticheatMessage"));
			Assert.NotNull(typeof(IRealtimeClient).GetMethod("AnticheatRegister"));
			Assert.NotNull(typeof(IRealtimeClient).GetMethod("AnticheatDeregister"));
		}
	}
}
