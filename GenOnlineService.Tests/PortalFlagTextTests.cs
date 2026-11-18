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

using GenOnlineService.Api;
using GenOnlineService.Flags;
using GenOnlineService.Portal.Client;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GenOnlineService.Tests;

// The game config page (S10) reads the host's flag definitions, edits the typed value and sends the definition back
public class PortalFlagTextTests
{
	private static List<FlagDefinition> Seed() => FlagSeed.FromGroupedConfig((JsonObject)JsonNode.Parse(FlagSeed.ServiceConfig)!);

	private static PortalFlag ToPortal(FlagDefinition flag) => JsonSerializer.Deserialize<PortalFlag>(JsonSerializer.Serialize(flag, ApiResults.Json), PortalJson.Options)!;

	private static FlagDefinition ToHost(PortalFlag flag) => JsonSerializer.Deserialize<FlagDefinition>(JsonSerializer.Serialize(flag, PortalJson.Options), ApiResults.Json)!;

	// every seeded field survives the trip through the page unchanged, and an edited value is the only difference
	[Fact]
	public void TheSeedRoundTripsThroughThePageAndAnEditChangesTheValueOnly()
	{
		foreach (FlagDefinition seeded in Seed())
		{
			PortalFlag flag = ToPortal(seeded);
			Assert.True(PortalFlagText.TryParse(flag, PortalFlagText.TextOf(flag, flag.Value), out JsonElement same, out string? problem), seeded.Key + ": " + problem);
			FlagDefinition back = ToHost(flag with { Value = same });
			Assert.Null(FlagRules.Check(back));
			Assert.Equal(JsonSerializer.Serialize(seeded, ApiResults.Json), JsonSerializer.Serialize(back, ApiResults.Json));
		}

		PortalFlag timeout = ToPortal(Seed().Single(f => f.Key == "client.connection.reconnect_timeout_ms"));
		Assert.Equal("30000 ms", PortalFlagText.Display(timeout, timeout.Value));
		Assert.True(PortalFlagText.TryParse(timeout, "45000", out JsonElement longer, out _));
		var rule = new PortalFlagRule { Kind = "country", Values = ["NL"], Value = longer };
		FlagDefinition edited = ToHost(timeout with { Value = longer, Rules = [rule] });
		Assert.Null(FlagRules.Check(edited));
		Assert.Equal(45000, edited.Value.GetInt32());
		Assert.Equal("from NL → 45000 ms", PortalFlagText.RuleText(timeout, rule));
	}

	[Fact]
	public void TheFormRefusesWhatTheTypeCannotHold()
	{
		var count = new PortalFlag { Key = "client.x.count", Type = "int", Min = 0, Max = 64, Unit = "frames" };
		Assert.False(PortalFlagText.TryParse(count, "1.5", out _, out string? notWhole));
		Assert.Equal("Enter a whole number.", notWhole);
		Assert.False(PortalFlagText.TryParse(count, "65", out _, out string? tooHigh));
		Assert.Equal("The value is outside its limits: whole number from 0 to 64, in frames.", tooHigh);
		Assert.True(PortalFlagText.TryParse(count, "64", out JsonElement top, out _));
		Assert.Equal(64, top.GetInt32());

		var text = new PortalFlag { Key = "client.x.name", Type = "string" };
		Assert.True(PortalFlagText.TryParse(text, "a \"quoted\" name", out JsonElement name, out _));
		Assert.Equal("a \"quoted\" name", name.GetString());
	}

	[Fact]
	public void ARefusedFieldReadsInWords()
	{
		Assert.Equal("The value does not fit the type, the limits or the choices of this field.", PortalText.FlagError("invalid_argument", "value"));
		Assert.Equal("Someone changed this meanwhile. Reload the page and try again.", PortalText.FlagError("conflict.stale", null));
		Assert.Equal(PortalText.Error("forbidden"), PortalText.FlagError("forbidden", "value"));
	}
}
