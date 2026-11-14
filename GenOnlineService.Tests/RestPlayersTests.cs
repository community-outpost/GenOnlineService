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
using Xunit;

namespace GenOnlineService.Tests
{
	// Argument parsing of the players, leaderboards and matches routes
	public class RestPlayersTests
	{
		private static IQueryCollection Query(string query)
		{
			DefaultHttpContext context = new DefaultHttpContext();
			context.Request.QueryString = new QueryString(query);
			return context.Request.Query;
		}

		private static string FieldOf(Failure? failure)
		{
			Assert.NotNull(failure);
			Assert.Equal(ErrorCodes.InvalidArgument, failure!.Value.Code);
			return (string)failure.Value.Params.GetType().GetProperty("Field")!.GetValue(failure.Value.Params)!;
		}

		[Fact]
		public void IdsKeepTheirOrderAndDropDuplicates()
		{
			(IReadOnlyList<long> ids, Failure? failure) = PlayerArgs.ReadIds("3, 1,3,2");
			Assert.Null(failure);
			Assert.Equal(new long[] { 3, 1, 2 }, ids);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("1,,2")]
		[InlineData("1,x")]
		[InlineData("0")]
		[InlineData("-4")]
		[InlineData("99999999999999999999")]
		public void BadIdsNameTheIdsField(string? text)
		{
			Assert.Equal("ids", FieldOf(PlayerArgs.ReadIds(text).Failure));
		}

		[Fact]
		public void AHundredIdsAreAllowedAndOneMoreIsNot()
		{
			string hundred = String.Join(",", Enumerable.Range(1, 100));
			Assert.Null(PlayerArgs.ReadIds(hundred).Failure);
			Assert.Equal("ids", FieldOf(PlayerArgs.ReadIds(hundred + ",101").Failure));
		}

		[Theory]
		[InlineData("?online=true", true, null)]
		[InlineData("?search=an", false, "an")]
		[InlineData("?online=true&search=%20Ann%20", true, "Ann")]
		[InlineData("?online=false&search=bob", false, "bob")]
		public void ListArgumentsAreRead(string query, bool online, string? search)
		{
			(PlayerListArgs args, Failure? failure) = PlayerArgs.ReadList(Query(query));
			Assert.Null(failure);
			Assert.Equal(online, args.Online);
			Assert.Equal(search, args.Search);
		}

		[Theory]
		[InlineData("")]
		[InlineData("?online=false")]
		[InlineData("?search=a")]
		[InlineData("?search=%20%20")]
		[InlineData("?online=true&search=a")]
		[InlineData("?online=yes")]
		public void AListWithoutAFilterOrWithAShortSearchIsRefused(string query)
		{
			(_, Failure? failure) = PlayerArgs.ReadList(Query(query));
			Assert.Equal(query.Contains("online=yes") ? "online" : "search", FieldOf(failure));
		}

		[Theory]
		[InlineData("elo", true)]
		[InlineData("ELO", false)]
		[InlineData("monthly", false)]
		public void OnlyTheEloLeaderboardExists(string id, bool known)
		{
			Assert.Equal(known, LeaderboardIds.IsKnown(id));
		}

		[Fact]
		public void MatchesDefaultToTheNewestTwenty()
		{
			(MatchListArgs args, Failure? failure) = MatchArgs.Read(Query(""));
			Assert.Null(failure);
			Assert.Equal(new MatchListArgs(null, null, 20), args);
			Assert.True(args.Newest);
		}

		[Fact]
		public void MatchArgumentsAreRead()
		{
			(MatchListArgs args, Failure? failure) = MatchArgs.Read(Query("?after_id=1200&since=2026-11-14T20:00:00Z&per_page=100"));
			Assert.Null(failure);
			Assert.Equal(1200, args.AfterID);
			Assert.Equal(new DateTime(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc), args.Since);
			Assert.Equal(DateTimeKind.Utc, args.Since!.Value.Kind);
			Assert.Equal(100, args.PerPage);
			Assert.False(args.Newest);
		}

		[Fact]
		public void SinceWithAnOffsetIsConvertedToUtc()
		{
			(MatchListArgs args, _) = MatchArgs.Read(Query("?since=2026-11-14T21:00:00%2B01:00"));
			Assert.Equal(new DateTime(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc), args.Since);
			Assert.False(args.Newest);
		}

		[Theory]
		[InlineData("?after_id=x", "after_id")]
		[InlineData("?after_id=-1", "after_id")]
		[InlineData("?since=yesterday", "since")]
		[InlineData("?since=2026-11-14", "since")]
		[InlineData("?per_page=0", "per_page")]
		[InlineData("?per_page=101", "per_page")]
		public void BadMatchArgumentsNameTheirField(string query, string field)
		{
			Assert.Equal(field, FieldOf(MatchArgs.Read(Query(query)).Failure));
		}
	}
}
