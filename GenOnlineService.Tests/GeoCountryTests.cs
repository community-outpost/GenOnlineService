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
using Xunit;

namespace GenOnlineService.Tests;

// A sign-in records the country only when GeoIP knows it: the lobby fallback (US, Washington) is never stored as the
// player's country (staff saw the right city with country US)
public class GeoCountryTests
{
	[Theory]
	[InlineData("192.0.2.1")]   // documentation range, in no GeoIP database
	[InlineData("10.0.0.1")]    // private
	[InlineData("not an address")]
	public void AnUnknownAddressHasNoCountry(string address)
	{
		Assert.Null(GeoLocation.KnownCountry(address));
	}

	[Fact]
	public void LobbiesStillGetTheFallbackLocation()
	{
		GeoLocation geo = GeoLocation.Lookup("192.0.2.1");
		Assert.Equal("US", geo.Country);
		Assert.Equal("NA", geo.Continent);
	}
}
