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
using GenOnlineService.Rest;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace GenOnlineService.Tests
{
	// The rules of the staff tools that need no database: who holds the new permissions and what the list arguments accept
	public class StaffToolsTests
	{
		private static string FieldOf(Failure? failure)
		{
			Assert.NotNull(failure);
			Assert.Equal(ErrorCodes.InvalidArgument, failure!.Value.Code);
			return (string)failure.Value.Params.GetType().GetProperty("Field")!.GetValue(failure.Value.Params)!;
		}

		private static IQueryCollection Query(params (string Key, string Value)[] pairs)
		{
			return new QueryCollection(pairs.ToDictionary(pair => pair.Key, pair => new StringValues(pair.Value)));
		}

		[Fact]
		public void OnlyAdminHoldsAnticheatReviewAndPlayersInspect()
		{
			Assert.Equal("anticheat.review", Permissions.AnticheatReview);
			Assert.Equal("players.inspect", Permissions.PlayersInspect);

			HashSet<string> admin = StaffRoles.PermissionsFor([StaffRoles.Admin]);
			Assert.Contains(Permissions.AnticheatReview, admin);
			Assert.Contains(Permissions.PlayersInspect, admin);

			HashSet<string> moderator = StaffRoles.PermissionsFor([StaffRoles.Moderator]);
			Assert.DoesNotContain(Permissions.AnticheatReview, moderator);
			Assert.DoesNotContain(Permissions.PlayersInspect, moderator);

			Assert.Contains(Permissions.AnticheatReview, Permissions.Human);
			Assert.Contains(Permissions.PlayersInspect, Permissions.Human);
			Assert.False(Permissions.IsMachine(Permissions.AnticheatReview));
			Assert.False(Permissions.IsMachine(Permissions.PlayersInspect));
		}

		[Theory]
		[InlineData("ab", "ab")]
		[InlineData("  General  ", "General")]
		[InlineData("7", "7")]
		[InlineData("12345678901234567890123456789012", "12345678901234567890123456789012")]
		public void StaffSearchAcceptsANamePartOrAnId(string text, string expected)
		{
			(string? search, Failure? failure) = StaffPlayerActions.ReadSearch(text);
			Assert.Null(failure);
			Assert.Equal(expected, search);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("a")]
		[InlineData("0")]
		[InlineData(" % ")]
		[InlineData("123456789012345678901234567890123")]
		public void StaffSearchRefusesAShortOrOverlongTerm(string? text)
		{
			(_, Failure? failure) = StaffPlayerActions.ReadSearch(text);
			Assert.Equal("query", FieldOf(failure));
		}

		[Fact]
		public void ReviewListDefaultsToUnreviewedRowsOfEveryPlayer()
		{
			(AntiCheatListArgs args, Failure? failure) = AntiCheatActions.ReadList(Query());
			Assert.Null(failure);
			Assert.Equal(new AntiCheatListArgs(false, null), args);
		}

		[Theory]
		[InlineData("true", true)]
		[InlineData("false", false)]
		[InlineData("all", null)]
		public void ReviewListReadsTheReviewedFilter(string text, bool? expected)
		{
			(AntiCheatListArgs args, Failure? failure) = AntiCheatActions.ReadList(Query(("reviewed", text), ("user_id", "42")));
			Assert.Null(failure);
			Assert.Equal(new AntiCheatListArgs(expected, 42), args);
		}

		[Theory]
		[InlineData("reviewed", "yes")]
		[InlineData("reviewed", "")]
		[InlineData("user_id", "0")]
		[InlineData("user_id", "-4")]
		[InlineData("user_id", "abc")]
		public void ReviewListNamesTheBadArgument(string key, string value)
		{
			(_, Failure? failure) = AntiCheatActions.ReadList(Query((key, value)));
			Assert.Equal(key, FieldOf(failure));
		}

		[Fact]
		public void ReviewKindsAreTheFourLists()
		{
			Assert.Equal(["new_accounts", "modules", "probes", "devices"], AntiCheatReviewKinds.All);
			Assert.False(AntiCheatReviewKinds.IsKnown("crc"));
			Assert.False(AntiCheatReviewKinds.IsKnown(null));
		}

		[Fact]
		public void DeviceFingerprintIsAShortPrefixOfTheHash()
		{
			byte[] hash = Enumerable.Range(0, Device.ValueHmacLength).Select(i => (byte)(0xF0 + i)).ToArray();
			Assert.Equal("f0f1f2f3", Database.StaffInspection.FingerprintOf(hash));
		}
	}
}
