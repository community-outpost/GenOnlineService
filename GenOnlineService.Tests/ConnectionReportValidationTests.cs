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

using GenOnlineService.Api;
using GenOnlineService.Realtime;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests
{
	// The body checks of POST /api/v1/lobbies/:id/connection_reports
	public class ConnectionReportValidationTests
	{
		private const long Reporter = 5;
		private const long Lobby = 42;
		private const long Stranger = 99;
		private const long FormerMember = 8;

		// players 5 and 7 are members, 8 left the lobby, 99 never joined it
		private static bool IsMember(long userId) => userId is Reporter or 7;
		private static bool WasMember(long userId) => IsMember(userId) || userId == FormerMember;

		private static ConnectionReportRequest Valid() => new ConnectionReportRequest
		{
			TargetUserId = 7,
			Outcome = "connected_relay",
			Transport = "gns",
			IpVersion = 4,
			Attempt = 1
		};

		private static ConnectionReportEvent Event(string state, long ms) => new ConnectionReportEvent { State = state, TMs = ms };

		private static string? FieldOf(Result<ConnectionReport> result)
		{
			Assert.False(result.Ok);
			Assert.Equal(ErrorCodes.InvalidArgument, result.Code);
			return JsonSerializer.SerializeToElement(result.Params, ApiResults.Json).GetProperty("field").GetString();
		}

		[Fact]
		public void ValidReportBecomesTheStoredRow()
		{
			Result<ConnectionReport> result = ConnectionReportValidation.Validate(Valid() with { Events = [Event("connecting", 0), Event("connected_relay", 5100)] }, Reporter, Lobby, WasMember);

			Assert.True(result.Ok);
			ConnectionReport row = result.Data!;
			Assert.Equal(Reporter, row.ReporterID);
			Assert.Equal(Lobby, row.LobbyID);
			Assert.Equal(7, row.TargetUserID);
			Assert.Equal(1, row.Attempt);
			Assert.Equal("connected_relay", row.Outcome);
			Assert.Equal("gns", row.Transport);
			Assert.Equal(4, row.IpVersion);
			Assert.Equal("[{\"state\":\"connecting\",\"t_ms\":0},{\"state\":\"connected_relay\",\"t_ms\":5100}]", row.Events);
		}

		[Fact]
		public void MissingEventsStoreAnEmptyTimeline()
		{
			Assert.Equal("[]", ConnectionReportValidation.Validate(Valid() with { Attempt = 3 }, Reporter, Lobby, WasMember).Data!.Events);
		}

		[Theory]
		[InlineData(null, "target_user_id")]
		[InlineData(0L, "target_user_id")]
		[InlineData(-1L, "target_user_id")]
		[InlineData(Reporter, "target_user_id")]
		public void TargetMustBeAnotherPlayer(long? target, string field)
		{
			Assert.Equal(field, FieldOf(ConnectionReportValidation.Validate(Valid() with { TargetUserId = target }, Reporter, Lobby, WasMember)));
		}

		[Fact]
		public void TargetMustBeOrHaveBeenAMemberOfTheLobby()
		{
			Assert.Equal("target_user_id", FieldOf(ConnectionReportValidation.Validate(Valid() with { TargetUserId = Stranger }, Reporter, Lobby, WasMember)));
			Assert.True(ConnectionReportValidation.Validate(Valid() with { TargetUserId = FormerMember }, Reporter, Lobby, WasMember).Ok);
		}

		[Fact]
		public void MembershipIsCheckedBeforeTheBody()
		{
			ConnectionReportRequest bad = Valid() with { Outcome = "bogus", TargetUserId = Stranger };

			Result<ConnectionReport> notMember = ConnectionReportValidation.Check(true, IsMember, WasMember, bad, null, Stranger, Lobby);
			Assert.False(notMember.Ok);
			Assert.Equal(ErrorCodes.LobbyNotMember, notMember.Code);

			Result<ConnectionReport> malformed = ConnectionReportValidation.Check(true, IsMember, WasMember, null, Result.Invalid("body"), Stranger, Lobby);
			Assert.Equal(ErrorCodes.LobbyNotMember, malformed.Code);

			Result<ConnectionReport> noLobby = ConnectionReportValidation.Check(false, IsMember, WasMember, bad, null, Reporter, Lobby);
			Assert.Equal(ErrorCodes.NotFound, noLobby.Code);

			Assert.Equal("target_user_id", FieldOf(ConnectionReportValidation.Check(true, IsMember, WasMember, bad, null, Reporter, Lobby)));
			Assert.True(ConnectionReportValidation.Check(true, IsMember, WasMember, Valid(), null, Reporter, Lobby).Ok);
		}

		[Theory]
		[InlineData("connected_direct", true)]
		[InlineData("connected_relay", true)]
		[InlineData("failed", true)]
		[InlineData("disconnected", true)]
		[InlineData("connecting", false)]
		[InlineData("CONNECTED_DIRECT", false)]
		[InlineData(null, false)]
		public void OutcomeIsOneOfTheFour(string? outcome, bool ok)
		{
			Result<ConnectionReport> result = ConnectionReportValidation.Validate(Valid() with { Outcome = outcome }, Reporter, Lobby, WasMember);
			if (ok)
			{
				Assert.True(result.Ok);
			}
			else
			{
				Assert.Equal("outcome", FieldOf(result));
			}
		}

		[Theory]
		[InlineData("gns")]
		[InlineData("eos")]
		[InlineData("steam")]
		[InlineData("plugin_2")]
		public void TransportIsGnsOrAPluginTransportName(string transport)
		{
			Result<ConnectionReport> result = ConnectionReportValidation.Validate(Valid() with { Transport = transport }, Reporter, Lobby, WasMember);
			Assert.True(result.Ok);
			Assert.Equal(transport, result.Data!.Transport);
		}

		// storage keeps the reported name, the metric tag is gns, a supported provider or other
		[Theory]
		[InlineData("gns", "gns")]
		[InlineData("eos", "eos")]
		[InlineData("abc", "other")]
		[InlineData("steam", "other")]
		[InlineData("other", "other")]
		public void TheMetricTagIsGnsASupportedProviderOrOther(string transport, string tag)
		{
			Assert.Equal(tag, ConnectionReportValidation.MetricTransport(transport, ["eos"]));
		}

		[Theory]
		[InlineData("Bad!")]
		[InlineData("EOS")]
		[InlineData("")]
		[InlineData("abcdefghijklmnopqrstuvwxyz0123456")]
		[InlineData(null)]
		public void TransportWithOtherSyntaxIsInvalid(string? transport)
		{
			Assert.Equal("transport", FieldOf(ConnectionReportValidation.Validate(Valid() with { Transport = transport }, Reporter, Lobby, WasMember)));
		}

		[Theory]
		[InlineData(5)]
		[InlineData(0)]
		[InlineData(null)]
		public void IpVersionIsFourOrSix(int? version)
		{
			Assert.True(ConnectionReportValidation.Validate(Valid() with { IpVersion = 6 }, Reporter, Lobby, WasMember).Ok);
			Assert.Equal("ip_version", FieldOf(ConnectionReportValidation.Validate(Valid() with { IpVersion = version }, Reporter, Lobby, WasMember)));
		}

		[Theory]
		[InlineData(0)]
		[InlineData(-2)]
		[InlineData(null)]
		public void AttemptIsRequiredAndAtLeastOne(int? attempt)
		{
			Assert.Equal("attempt", FieldOf(ConnectionReportValidation.Validate(Valid() with { Attempt = attempt }, Reporter, Lobby, WasMember)));
		}

		[Fact]
		public void AtMost32Events()
		{
			ConnectionReportEvent[] events = Enumerable.Range(0, 33).Select(i => Event("connecting", i)).ToArray();
			Assert.True(ConnectionReportValidation.Validate(Valid() with { Events = events[..32] }, Reporter, Lobby, WasMember).Ok);
			Assert.Equal("events", FieldOf(ConnectionReportValidation.Validate(Valid() with { Events = events }, Reporter, Lobby, WasMember)));
		}

		[Fact]
		public void EventTimesNeverDecreaseAndStayWithinTenMinutes()
		{
			Assert.True(ConnectionReportValidation.Validate(Valid() with { Events = [Event("connecting", 0), Event("finding_route", 0), Event("failed", 600000)] }, Reporter, Lobby, WasMember).Ok);
			Assert.Equal("events", FieldOf(ConnectionReportValidation.Validate(Valid() with { Events = [Event("connecting", 10), Event("failed", 5)] }, Reporter, Lobby, WasMember)));
			Assert.Equal("events", FieldOf(ConnectionReportValidation.Validate(Valid() with { Events = [Event("failed", 600001)] }, Reporter, Lobby, WasMember)));
			Assert.Equal("events", FieldOf(ConnectionReportValidation.Validate(Valid() with { Events = [Event("failed", -1)] }, Reporter, Lobby, WasMember)));
		}

		[Fact]
		public void EventStateIsFromTheList()
		{
			Assert.Equal("events", FieldOf(ConnectionReportValidation.Validate(Valid() with { Events = [Event("relaying", 0)] }, Reporter, Lobby, WasMember)));
			Assert.Equal("events", FieldOf(ConnectionReportValidation.Validate(Valid() with { Events = [new ConnectionReportEvent { State = "connecting" }] }, Reporter, Lobby, WasMember)));
		}
	}
}
