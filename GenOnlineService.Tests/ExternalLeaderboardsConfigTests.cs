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


using Microsoft.Extensions.Configuration;
using Xunit;

namespace GenOnlineService.Tests;

// Without an ExternalLeaderboards section the service neither queues match results for publication nor asks the
// external API for ratings; a section that is present still has to be complete
public class ExternalLeaderboardsConfigTests
{
	private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

	[Fact]
	public void NoSectionMeansNotConfigured()
	{
		Assert.False(ExternalLeaderboardsClient.IsConfigured(Config(new())));
		Assert.False(ExternalLeaderboardsClient.IsConfigured(Config(new() { ["Other:key"] = "x" })));
		Assert.False(ExternalLeaderboardsClient.IsConfigured(null));
	}

	// a present section counts as configured even when incomplete, so a half-written one still fails loudly
	[Fact]
	public void AnySectionMeansConfigured()
	{
		Assert.True(ExternalLeaderboardsClient.IsConfigured(Config(new()
		{
			["ExternalLeaderboards:PostUrl"] = "https://example.org/ingest",
			["ExternalLeaderboards:PostToken"] = "t",
			["ExternalLeaderboards:GetUrl"] = "https://example.org/{playerId}",
			["ExternalLeaderboards:GetToken"] = "t"
		})));
		Assert.True(ExternalLeaderboardsClient.IsConfigured(Config(new() { ["ExternalLeaderboards:PostUrl"] = "https://example.org/ingest" })));
	}
}
