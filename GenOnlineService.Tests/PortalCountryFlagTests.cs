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

// The flag beside a country code on the staff pages: a regional-indicator emoji computed from the code, so the pages
// load nothing from another site, and nothing for a code that is null, malformed or no country
public class PortalCountryFlagTests
{
	[Theory]
	[InlineData("NL", "🇳🇱")]
	[InlineData("nl", "🇳🇱")]
	[InlineData("US", "🇺🇸")]
	[InlineData("DE", "🇩🇪")]
	[InlineData("XK", "🇽🇰")]
	public void AKnownCodeShowsItsFlag(string code, string flag)
	{
		Assert.Equal(flag, PortalText.CountryFlag(code));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("N")]
	[InlineData("NLD")]
	[InlineData("1A")]
	[InlineData("EU")]
	[InlineData("AP")]
	[InlineData("XX")]
	[InlineData("ZZ")]
	[InlineData("ÄÄ")]
	public void AnUnknownOrNullCodeShowsNoFlag(string? code)
	{
		Assert.Null(PortalText.CountryFlag(code));
	}
}
