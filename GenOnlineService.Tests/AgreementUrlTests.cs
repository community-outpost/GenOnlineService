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
using Xunit;

namespace GenOnlineService.Tests;

// The document addresses become links on every page's footer, so only absolute http and https addresses are accepted
public class AgreementUrlTests
{
	private static AgreementsSettings Read(params (string Key, string Value)[] values)
		=> AgreementsSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build().GetSection("Agreements"));

	[Theory]
	[InlineData("javascript:alert(1)")]
	[InlineData("data:text/html,<b>x</b>")]
	[InlineData("/rules")]
	[InlineData("rules.html")]
	[InlineData("https://example.org/a b")]
	[InlineData("https://example.org/\"x")]
	[InlineData("https://example.org/'x")]
	[InlineData("ftp://example.org/rules")]
	[InlineData("https:///rules")]
	public void AnAddressThatIsNotAnAbsoluteHttpOrHttpsUrlStopsTheStart(string url)
	{
		foreach (string key in new[] { "Agreements:rules:url", "Agreements:terms:url", "Agreements:privacy:url" })
		{
			var error = Assert.Throws<InvalidOperationException>(() => Read((key, url)));
			Assert.Contains(key, error.Message);
		}
	}

	[Fact]
	public void HttpAndHttpsAddressesWork()
	{
		AgreementsSettings settings = Read(("Agreements:rules:url", "https://example.org/rules"), ("Agreements:terms:url", "http://example.org/terms"), ("Agreements:privacy:url", " https://example.org/privacy "));
		Assert.Equal("https://example.org/rules", settings.RulesDocument.Url);
		Assert.Equal("http://example.org/terms", settings.TermsDocument.Url);
		Assert.Equal("https://example.org/privacy", settings.PrivacyUrl);
	}
}
