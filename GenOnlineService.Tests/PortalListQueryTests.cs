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

using GenOnlineService.Portal.Client;
using Xunit;

namespace GenOnlineService.Tests;

// The player and match lists keep their view in the address (T17f): what the address holds is what the pages ask the host for
public class PortalListQueryTests
{
	[Fact]
	public void APlayerViewReadsFromTheAddressAndAsksTheHostTheSame()
	{
		PortalPlayerQuery query = PortalPlayerQuery.FromAddress(" Red#0001 ", "banned,nobody,staff,banned", "7d", "matches", "asc");
		Assert.Equal("Red#0001", query.Search);
		Assert.Equal(["banned", "staff"], query.Filters);
		Assert.Equal("query=Red%230001&filter=banned%2Cstaff&signed_in_within=7d&order_by=matches&sort=asc".Split('&').Order(), query.ToApiQuery().Split('&').Order());
		Assert.Equal("Red#0001", query.ToAddress()["q"]);
	}

	// A pasted address with a search and a direction but no order: the host refuses that pair, so the page leaves the direction out
	[Fact]
	public void ASearchWithoutAnOrderDropsTheDirection()
	{
		Assert.Null(PortalPlayerQuery.FromAddress("gen", null, null, null, "asc").Sort);
		Assert.Equal("asc", PortalPlayerQuery.FromAddress("gen", null, null, "name", "asc").Sort);
		Assert.Equal("asc", PortalPlayerQuery.FromAddress(null, null, null, null, "asc").Sort);
		Assert.DoesNotContain("sort=", (new PortalPlayerQuery { Search = "gen", Sort = "asc" }).ToApiQuery());
	}

	// The report queue keeps its status, search and order in the address; the open queue, oldest first, leaves it empty
	[Fact]
	public void AReportViewReadsFromTheAddressAndWritesItBack()
	{
		PortalReportQuery query = PortalReportQuery.FromAddress("dismissed,actioned", " hack ", "desc");
		Assert.Equal(new PortalReportQuery { Status = "actioned,dismissed", Search = "hack", NewestFirst = true }, query);
		Assert.Equal("actioned,dismissed", query.ToAddress()["status"]);
		Assert.Equal("hack", query.ToAddress()["q"]);
		Assert.Equal("desc", query.ToAddress()["sort"]);

		Assert.Equal("all", PortalReportQuery.FromAddress("open,all", null, null).Status);
		Assert.All(new PortalReportQuery().ToAddress().Values, Assert.Null);
	}

	// The audit log keeps players, the action and the days in the address and asks the host the same; an unknown action
	// or a day that is not yyyy-MM-dd is left out
	[Fact]
	public void AnAuditViewReadsFromTheAddressAndAsksTheHostTheSame()
	{
		PortalAuditQuery query = PortalAuditQuery.FromAddress(" Red#0001 ", "42", "ban", "2026-11-01", "2026-11-17");
		Assert.Equal("actor=Red%230001&target=42&action=ban&from=2026-11-01&to=2026-11-17", query.ToApiQuery());
		Assert.Equal("Red#0001", query.ToAddress()["actor"]);
		Assert.True(query.IsFiltered);

		PortalAuditQuery dropped = PortalAuditQuery.FromAddress(" ", null, "explode", "yesterday", "2026-13-40");
		Assert.Equal(new PortalAuditQuery(), dropped);
		Assert.False(dropped.IsFiltered);
		Assert.Equal("", dropped.ToApiQuery());
	}

	[Fact]
	public void EveryAuditActionHasWords()
	{
		Assert.All(PortalAuditActions.All, action => Assert.NotEqual(action, PortalAuditActions.LabelOf(action)));
	}

	[Fact]
	public void AnUnknownReportStatusIsTheOpenQueue()
	{
		Assert.Equal(new PortalReportQuery(), PortalReportQuery.FromAddress("closed", "  ", "up"));
		Assert.Equal(new PortalReportQuery(), PortalReportQuery.FromAddress(",", null, null));
	}

	[Fact]
	public void AnEmptyOrUnknownPlayerAddressIsTheWholeList()
	{
		PortalPlayerQuery query = PortalPlayerQuery.FromAddress(null, "", "1y", "email", "up");
		Assert.Equal(new PortalPlayerQuery().ToApiQuery(), query.ToApiQuery());
		Assert.Equal("", query.ToApiQuery());
		Assert.False(query.IsFiltered);
	}

	[Fact]
	public void AMatchViewReadsFromTheAddressAndAsksTheHostTheSame()
	{
		PortalMatchQuery query = PortalMatchQuery.FromAddress("quick_match", " Desert ", "Bob#0042", "2026-11-01", "2026-11-17", "true");
		Assert.True(query.IsFiltered);
		Assert.Equal("type=quick_match&map=Desert&player=Bob%230042&from=2026-11-01&to=2026-11-17&flagged=true", query.ToApiQuery());

		PortalMatchQuery unknown = PortalMatchQuery.FromAddress("ranked", "", null, "yesterday", "2026-13-01", "yes");
		Assert.False(unknown.IsFiltered);
		Assert.Equal("", unknown.ToApiQuery());
	}

	// The player page's Matches tab (T17g) asks /api/v1/matches/search for the player by id, a page of 10 at a time, and
	// its "All matches" link opens the match list on the same view
	[Fact]
	public async Task ThePlayerMatchesTabAsksTheHostForThePlayerAndLinksToTheSameList()
	{
		var host = new RecordingHandler();
		var api = new PortalApi(new HttpClient(host) { BaseAddress = new Uri("http://localhost/") }, new TestNavigation(), new NoScripts());

		PortalAnswer<PortalMatchPage> answer = await api.ListMatchesAsync(PortalMatchQuery.OfPlayer(42), 10, 10, CancellationToken.None);

		Assert.True(answer.Ok);
		Assert.Equal("/api/v1/matches/search?player=42&page=2&per_page=10", host.Requested?.PathAndQuery);
		Assert.Equal(7, answer.Value!.Total);
		Assert.Equal("/admin/matches?player=42", PortalRoutes.MatchesOf(42));

		// the link's address reads back as the same view the tab asked for
		PortalMatchQuery linked = PortalMatchQuery.FromAddress(null, null, "42", null, null, null);
		Assert.Equal(PortalMatchQuery.OfPlayer(42).ToApiQuery(), linked.ToApiQuery());
	}

	[Fact]
	public void AMatchResultIsReadForOnePlayer()
	{
		var finished = new PortalMatchRow
		{
			FinishedAt = DateTime.UtcNow,
			Players = [new PortalMatchRowPlayer { UserId = 1, Won = true }, new PortalMatchRowPlayer { UserId = 2 }]
		};
		Assert.Equal("won", finished.ResultFor(1));
		Assert.Equal("lost", finished.ResultFor(2));
		Assert.Equal("no result", (finished with { Players = [new PortalMatchRowPlayer { UserId = 1 }, new PortalMatchRowPlayer { UserId = 2 }] }).ResultFor(1));
		Assert.Equal("in progress", (finished with { FinishedAt = null }).ResultFor(1));
	}

	// Answers every request with an empty page of 7 matches and keeps the address asked for
	private sealed class RecordingHandler : HttpMessageHandler
	{
		public Uri? Requested { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requested = request.RequestUri;
			var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"matches\":[]}", System.Text.Encoding.UTF8, "application/json") };
			response.Headers.Add("X-Total", "7");
			return Task.FromResult(response);
		}
	}

	private sealed class TestNavigation : Microsoft.AspNetCore.Components.NavigationManager
	{
		public TestNavigation() => Initialize("http://localhost/", "http://localhost/admin/players/42");

		protected override void NavigateToCore(string uri, bool forceLoad)
		{
		}
	}

	private sealed class NoScripts : Microsoft.JSInterop.IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("no scripts in a test");

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new InvalidOperationException("no scripts in a test");
	}
}
