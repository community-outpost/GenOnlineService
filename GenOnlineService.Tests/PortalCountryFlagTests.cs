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

// The flag beside a country code on the staff pages: an SVG the service serves itself, so the pages load nothing from
// another site, and nothing for a code that is null, malformed or no country
public class PortalCountryFlagTests
{
	[Theory]
	[InlineData("NL", "/portal/flags/nl.svg")]
	[InlineData("nl", "/portal/flags/nl.svg")]
	[InlineData("US", "/portal/flags/us.svg")]
	[InlineData("DE", "/portal/flags/de.svg")]
	[InlineData("XK", "/portal/flags/xk.svg")]
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

	// every code with a flag has its file in the portal's static files
	[Fact]
	public void EveryKnownCodeHasABundledFile()
	{
		var root = new DirectoryInfo(AppContext.BaseDirectory);
		while (root.Parent != null && !File.Exists(Path.Combine(root.FullName, "GenServices.sln")))
		{
			root = root.Parent;
		}
		string flags = Path.Combine(root.FullName, "GenOnlineService", "wwwroot", "portal", "flags");
		int checkedCodes = 0;
		for (char a = 'A'; a <= 'Z'; ++a)
		{
			for (char b = 'A'; b <= 'Z'; ++b)
			{
				if (PortalText.CountryFlag($"{a}{b}") is string path)
				{
					string file = Path.Combine(flags, Path.GetFileName(path));
					Assert.True(File.Exists(file), path);
					// a flag draws itself: no script and no reference to another site, the xmlns names aside
					string svg = File.ReadAllText(file);
					Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
					Assert.DoesNotContain("href=\"http", svg, StringComparison.OrdinalIgnoreCase);
					++checkedCodes;
				}
			}
		}
		Assert.Equal(250, checkedCodes);
	}
}
