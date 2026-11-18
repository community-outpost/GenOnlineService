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
	// Status codes, paging and the error body of the /api/v1 routes outside section 10
	public class RestSharedTests
	{
		[Theory]
		[InlineData(RestCodes.Unauthorized, 401)]
		[InlineData(RestCodes.ServiceUnavailable, 503)]
		[InlineData(RestCodes.SessionGameRequired, 403)]
		[InlineData(RestCodes.LinkedAccountMismatch, 403)]
		[InlineData(RestCodes.LinkedAccountInUse, 409)]
		[InlineData(RestCodes.LinkedAccountLocked, 409)]
		[InlineData(RestCodes.Conflict, 409)]
		[InlineData(RestCodes.MethodNotAllowed, 405)]
		[InlineData(RestCodes.LinkedAccountInvalidProof, 422)]
		[InlineData(ErrorCodes.NotFound, 404)]
		[InlineData(ErrorCodes.InvalidArgument, 400)]
		public void CodesMapToTheirStatus(string code, int status)
		{
			Assert.Equal(status, RestResults.StatusOf(code));
		}

		[Theory]
		[InlineData("?page=0", "page")]
		[InlineData("?page=x", "page")]
		[InlineData("?page=21474837", "page")]
		[InlineData("?per_page=0", "per_page")]
		[InlineData("?per_page=101", "per_page")]
		[InlineData("?per_page=-5", "per_page")]
		public void BadPageArgumentsNameTheirField(string query, string field)
		{
			DefaultHttpContext context = new DefaultHttpContext();
			context.Request.QueryString = new QueryString(query);
			(_, Failure? failure) = Paging.ReadOffset(context.Request);
			Assert.NotNull(failure);
			Assert.Equal(ErrorCodes.InvalidArgument, failure!.Value.Code);
			Assert.Equal(field, (string)failure.Value.Params.GetType().GetProperty("Field")!.GetValue(failure.Value.Params)!);
		}

		[Fact]
		public void OffsetDefaultsToTheFirstPageOfTwenty()
		{
			(OffsetPage page, Failure? failure) = Paging.ReadOffset(new DefaultHttpContext().Request);
			Assert.Null(failure);
			Assert.Equal(new OffsetPage(1, 20), page);
		}

		[Fact]
		public void OffsetHeadersLinkTheNextPageAndKeepOtherArguments()
		{
			DefaultHttpContext context = new DefaultHttpContext();
			context.Request.Path = "/api/v1/players";
			context.Request.QueryString = new QueryString("?online=true&page=1&per_page=2");
			Paging.WriteOffsetHeaders(context, new OffsetPage(1, 2), 5);

			Assert.Equal("5", context.Response.Headers["X-Total"]);
			Assert.Equal("2", context.Response.Headers["X-Next-Page"]);
			Assert.Equal("</api/v1/players?online=true&page=2&per_page=2>; rel=\"next\"", context.Response.Headers["Link"]);
		}

		[Fact]
		public void TheLastPageHasNoNextPage()
		{
			DefaultHttpContext context = new DefaultHttpContext();
			Paging.WriteOffsetHeaders(context, new OffsetPage(3, 2), 5);
			Assert.Equal("", context.Response.Headers["X-Next-Page"]);
			Assert.False(context.Response.Headers.ContainsKey("Link"));
		}

		[Fact]
		public void KeysetLinkContinuesAfterTheLastID()
		{
			DefaultHttpContext context = new DefaultHttpContext();
			context.Request.Path = "/api/v1/matches";
			context.Request.QueryString = new QueryString("?per_page=50");
			Paging.WriteKeysetNext(context, "after_id", 1234);
			Assert.Equal("</api/v1/matches?per_page=50&after_id=1234>; rel=\"next\"", context.Response.Headers["Link"]);
		}

		[Theory]
		[InlineData("/api/v1/me", true)]
		[InlineData("/-/health", true)]
		[InlineData("/realtime", false)]
		[InlineData("/connect/token", false)]
		public void RestPathsAreApiAndDash(string path, bool expected)
		{
			Assert.Equal(expected, RestResults.IsRestPath(path));
		}
	}
}
