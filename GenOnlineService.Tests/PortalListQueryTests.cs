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
}
