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

using GenOnlineService.Identity;
using Microsoft.Extensions.Configuration;
using System.Text;
using Xunit;

namespace GenOnlineService.Tests;

// Accounts:cover_images: the faction art by default, a configured list in its place, an explicit empty list for none
public class PortalCoverSettingsTests
{
	private static AccountsSettings Read(params (string Key, string Value)[] values)
		=> AccountsSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build());

	[Fact]
	public void WithoutTheSettingTheFactionArtIsTheCover()
	{
		Assert.Equal(["/portal/faction-usa.webp", "/portal/faction-china.webp", "/portal/faction-gla.webp"], Read().CoverImages);
	}

	[Fact]
	public void AConfiguredListReplacesTheDefault()
	{
		AccountsSettings settings = Read(("Accounts:cover_images:0", "/art/one.webp"), ("Accounts:cover_images:1", " /art/two.webp "));
		Assert.Equal(["/art/one.webp", "/art/two.webp"], settings.CoverImages);
	}

	[Fact]
	public void AnEmptyValueMeansNoCoverImage()
	{
		Assert.Empty(Read(("Accounts:cover_images", "")).CoverImages);
	}

	[Fact]
	public void AnEmptyJsonArrayMeansNoCoverImage()
	{
		IConfiguration config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("{\"Accounts\":{\"cover_images\":[]}}"))).Build();
		Assert.Empty(AccountsSettings.Read(config).CoverImages);
	}

	// a page naming another host would hand every visitor's address to it
	[Theory]
	[InlineData("https://cdn.example.com/a.webp")]
	[InlineData("//cdn.example.com/a.webp")]
	[InlineData("art/a.webp")]
	[InlineData("/a b.webp")]
	[InlineData("/a\"onload=x.webp")]
	public void AnEntryThatIsNotAPathOnThisServiceStopsTheStart(string entry)
	{
		InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Read(("Accounts:cover_images:0", entry)));
		Assert.Contains("Accounts:cover_images", error.Message);
	}
}
