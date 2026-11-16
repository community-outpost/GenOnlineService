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
		private static readonly string[] s_eos = ["eos"];
		private static readonly string[] s_none = [];

		// the linked-account providers the service supports, as registered in Program.cs
		private static readonly string[] s_supported = ["eos"];

		[Fact]
		public void FirstStartSignallingIsDirectGnsForSixSeconds()
		{
			PeerPairs pairs = new PeerPairs();
			PeerPairState state = pairs.Current(7, 3);

			Assert.Equal(new PeerPairState(1, 1), state);
			Assert.Equal(new PeerStepValues(1, "gns", false, 6), ConnectionOrder.ValuesOf(state, ConnectionOrder.SharedTransport(s_eos, s_eos, s_supported)));
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
		public void APairThatFellBackToTurnStaysThereWhenTheSharedTransportReturns()
		{
			PeerPairs pairs = new PeerPairs();
			pairs.Current(3, 7);
			pairs.Request(3, 7, 1, true);
			Assert.Equal(new PeerPairState(2, ConnectionOrder.RelayStep), pairs.Settle(3, 7, false));
			Assert.Equal(new PeerPairState(3, ConnectionOrder.RelayStep), pairs.Restart(7, 3, false, true));
		}

		[Fact]
		public void APluginStepSentWithoutPluginValuesKeepsItsStoredStep()
		{
			PeerPairs pairs = new PeerPairs();
			pairs.Current(3, 7);
			PeerPairState state = pairs.Request(3, 7, 1, true)!.Value;

			// a send without both middleware ids uses the turn values once
			Assert.Equal(new PeerStepValues(2, "gns", true, 0), ConnectionOrder.ValuesOf(state, null));
			Assert.Equal(ConnectionOrder.PluginStep, pairs.Current(3, 7).Step);
			Assert.Equal(new PeerStepValues(3, "eos", false, 10), ConnectionOrder.ValuesOf(pairs.Restart(3, 7, false, true), "eos"));
		}

		[Fact]
		public void FailedAttemptsWalkGnsThenEosThenTurn()
		{
			PeerPairs pairs = new PeerPairs();
			pairs.Current(3, 7);

			PeerPairState? second = pairs.Request(3, 7, 1, true);
			Assert.Equal(new PeerStepValues(2, "eos", false, 10), ConnectionOrder.ValuesOf(second!.Value, ConnectionOrder.SharedTransport(s_eos, s_eos, s_supported)));

			PeerPairState? third = pairs.Request(7, 3, 2, true);
			Assert.Equal(new PeerStepValues(3, "gns", true, 0), ConnectionOrder.ValuesOf(third!.Value, ConnectionOrder.SharedTransport(s_eos, s_eos, s_supported)));
		}

		[Fact]
		public void StepTwoIsSkippedUnlessBothMembersShareAPluginTransport()
		{
			PeerPairs pairs = new PeerPairs();
			pairs.Current(3, 7);

			PeerPairState? next = pairs.Request(3, 7, 1, ConnectionOrder.SharedTransport(s_eos, s_none, s_supported) != null);
			Assert.Equal(new PeerPairState(2, 3), next);
			Assert.Equal(new PeerStepValues(2, "gns", true, 0), ConnectionOrder.ValuesOf(next!.Value, ConnectionOrder.SharedTransport(s_eos, s_none, s_supported)));
		}

		[Fact]
		public void EmptyTransportListStillSendsTheStepFields()
		{
			Assert.Null(ConnectionOrder.SharedTransport(s_none, s_none, s_supported));
			Assert.Equal(new PeerStepValues(1, "gns", false, 6), ConnectionOrder.ValuesOf(ConnectionOrder.First, ConnectionOrder.SharedTransport(s_none, s_none, s_supported)));
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
		public void AMemberWithoutSetPeerTransportsListsNoPluginTransport()
		{
			Assert.Null(ConnectionOrder.SharedTransport(null, s_eos, s_supported));
			Assert.Null(ConnectionOrder.SharedTransport(s_eos, null, s_supported));
			Assert.Null(ConnectionOrder.SharedTransport(null, null, s_supported));

			// every StartSignalling carries the step fields, a pair on step 2 without a shared transport falls to step 3
			Assert.Equal(new PeerStepValues(1, "gns", false, 6), ConnectionOrder.ValuesOf(ConnectionOrder.First, ConnectionOrder.SharedTransport(null, s_eos, s_supported)));
			Assert.Equal(new PeerStepValues(2, "gns", true, 0), ConnectionOrder.ValuesOf(new PeerPairState(2, ConnectionOrder.PluginStep), ConnectionOrder.SharedTransport(s_eos, null, s_supported)));
		}

		[Fact]
		public void StartSignallingAlwaysCarriesTheFourFields()
		{
			StartSignallingEvent signal = new StartSignallingEvent { LobbyId = 991, UserId = 7, PreferredPort = 8088, MiddlewareId = "m", Attempt = 1, Transport = "gns", Relay = false, TimeoutS = 6 };
			Assert.Equal("{\"lobby_id\":991,\"user_id\":7,\"preferred_port\":8088,\"middleware_id\":\"m\",\"attempt\":1,\"transport\":\"gns\",\"relay\":false,\"timeout_s\":6}", JsonSerializer.Serialize(signal, s_options));
		}

		[Fact]
		public void SharedTransportIsTheFirstCommonNameInOrdinalOrder()
		{
			string[] supported = ["eos", "steam", "eos_2", "eos_3", "zz"];
			Assert.Equal("eos", ConnectionOrder.SharedTransport(["steam", "eos"], ["eos", "steam"], supported));
			Assert.Equal("steam", ConnectionOrder.SharedTransport(["steam", "eos"], ["steam"], supported));
			Assert.Equal("eos_2", ConnectionOrder.SharedTransport(["zz", "eos_3", "eos_2"], ["eos_3", "eos_2"], supported));
			Assert.Null(ConnectionOrder.SharedTransport(["steam"], s_eos, supported));
		}

		[Fact]
		public void OnlySupportedProvidersCountForStepTwo()
		{
			// abc sorts before eos, but only eos is a supported provider
			Assert.Equal("eos", ConnectionOrder.SharedTransport(["eos", "abc"], ["abc", "eos"], s_supported));
			Assert.Null(ConnectionOrder.SharedTransport(["abc"], ["abc"], s_supported));
			Assert.Null(ConnectionOrder.SharedTransport(["eos"], ["eos"], s_none));

			PeerPairs pairs = new PeerPairs();
			pairs.Current(3, 7);
			string[] abc = ["abc"];
			PeerPairState next = pairs.Request(3, 7, 1, ConnectionOrder.SharedTransport(abc, abc, s_supported) != null)!.Value;
			Assert.Equal(new PeerPairState(2, ConnectionOrder.RelayStep), next);
			Assert.Equal(new PeerStepValues(2, "gns", true, 0), ConnectionOrder.ValuesOf(next, ConnectionOrder.SharedTransport(abc, abc, s_supported)));
		}

		[Fact]
		public void AnySupportedProviderNameDrivesStepTwo()
		{
			PeerPairs pairs = new PeerPairs();
			pairs.Current(3, 7);
			string[] steam = ["steam"];

			PeerPairState next = pairs.Request(3, 7, 1, ConnectionOrder.SharedTransport(steam, steam, steam) != null)!.Value;
			Assert.Equal(new PeerStepValues(2, "steam", false, 10), ConnectionOrder.ValuesOf(next, ConnectionOrder.SharedTransport(steam, steam, steam)));
		}

		[Fact]
		public void StepTwoIsSkippedWhenNoNameIsShared()
		{
			PeerPairs pairs = new PeerPairs();
			pairs.Current(3, 7);
			string[] steam = ["steam"];

			PeerPairState next = pairs.Request(3, 7, 1, ConnectionOrder.SharedTransport(steam, s_eos, s_supported) != null)!.Value;
			Assert.Equal(new PeerPairState(2, ConnectionOrder.RelayStep), next);
			Assert.Equal(new PeerStepValues(2, "gns", true, 0), ConnectionOrder.ValuesOf(next, ConnectionOrder.SharedTransport(steam, s_eos, s_supported)));
		}

		[Theory]
		[InlineData("eos", true)]
		[InlineData("steam", true)]
		[InlineData("a", true)]
		[InlineData("plugin_2", true)]
		[InlineData("abcdefghijklmnopqrstuvwxyz012345", true)]
		[InlineData("abcdefghijklmnopqrstuvwxyz0123456", false)]
		[InlineData("", false)]
		[InlineData(null, false)]
		[InlineData("EOS", false)]
		[InlineData("Bad!", false)]
		[InlineData("e-os", false)]
		[InlineData("e os", false)]
		public void TransportNameSyntax(string? name, bool valid)
		{
			Assert.Equal(valid, ConnectionOrder.IsTransportName(name));
			Assert.Equal(valid, ConnectionOrder.IsPluginTransport(name));
		}

		[Fact]
		public void GnsIsATransportNameButReservedForPlugins()
		{
			Assert.True(ConnectionOrder.IsTransportName("gns"));
			Assert.False(ConnectionOrder.IsPluginTransport("gns"));
		}

		[Fact]
		public void PluginTransportsKeepTheFirstEightValidDistinctNamesInOrder()
		{
			Assert.Equal(["steam", "eos"], ConnectionOrder.PluginTransports(["steam", "gns", "EOS", "eos", "steam", "Bad!", null, new string('a', 33)]));

			string[] many = ["p1", "p1", "gns", "p2", "p3", "p4", "p5", "p6", "p7", "p8", "p9"];
			Assert.Equal(["p1", "p2", "p3", "p4", "p5", "p6", "p7", "p8"], ConnectionOrder.PluginTransports(many));
		}

		[Fact]
		public void RequestWithoutFailedAttemptRoundTrips()
		{
			RequestSignallingRequest request = JsonSerializer.Deserialize<RequestSignallingRequest>("{\"target_user_id\":7}", s_options)!;
			Assert.Null(request.FailedAttempt);
			Assert.Equal("{\"target_user_id\":7}", JsonSerializer.Serialize(request, s_options));
		}

		[Fact]
		public void SessionTransportsAreReplacedWholeAndIgnoreInvalidNames()
		{
			UserSession session = new UserSession(7, EUserSessionType.None, "", "", "", 0, 0);
			object connection = new object();
			session.ResetPeerTransports(connection);
			Assert.Null(session.PeerTransports);

			Assert.True(session.SetPeerTransports(connection, ["eos", "Quic", "gns", "eos"]));
			Assert.Equal(s_eos, session.PeerTransports);

			session.SetPeerTransports(connection, ["steam", "eos"]);
			Assert.Equal(["steam", "eos"], session.PeerTransports);

			session.SetPeerTransports(connection, []);
			Assert.Empty(session.PeerTransports!);

			session.SetPeerTransports(connection, null);
			Assert.Empty(session.PeerTransports!);
		}

		[Fact]
		public void ReconnectResendWaitsForTheTransportsOfTheNewConnection()
		{
			UserSession session = new UserSession(7, EUserSessionType.None, "", "", "", 0, 0);
			object oldConnection = new object();
			session.ResetPeerTransports(oldConnection);
			session.SetPeerTransports(oldConnection, s_eos);

			// a new connection forgets the earlier list, so the resend waits for SetPeerTransports
			object newConnection = new object();
			session.ResetPeerTransports(newConnection);
			Assert.Null(session.PeerTransports);
			session.MarkSignallingResendPending();
			Assert.False(session.TakeSignallingResend());

			Assert.True(session.SetPeerTransports(newConnection, []));
			Assert.True(session.TakeSignallingResend());

			// the resend runs once, a later list change sends nothing again
			session.SetPeerTransports(newConnection, s_eos);
			Assert.False(session.TakeSignallingResend());
		}

		[Fact]
		public void ASupersededConnectionsTransportsDoNotCountForTheNewConnection()
		{
			UserSession session = new UserSession(7, EUserSessionType.None, "", "", "", 0, 0);
			object oldConnection = new object();
			session.ResetPeerTransports(oldConnection);

			// the old connection's SetPeerTransports lands after the new connection took the session over
			object newConnection = new object();
			session.ResetPeerTransports(newConnection);
			session.MarkSignallingResendPending();
			Assert.False(session.SetPeerTransports(oldConnection, s_eos));
			Assert.Null(session.PeerTransports);
			Assert.False(session.TakeSignallingResend());

			// the new connection's own list still runs the resend
			Assert.True(session.SetPeerTransports(newConnection, []));
			Assert.True(session.TakeSignallingResend());
		}

		[Fact]
		public void ANewConnectionDropsAResendTheOldOneStillOwed()
		{
			UserSession session = new UserSession(7, EUserSessionType.None, "", "", "", 0, 0);
			session.MarkSignallingResendPending();
			object connection = new object();
			session.ResetPeerTransports(connection);
			session.SetPeerTransports(connection, s_eos);
			Assert.False(session.TakeSignallingResend());
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
