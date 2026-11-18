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

using GenOnlineService.Flags;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GenOnlineService.Tests
{
	public class FlagTests
	{
		private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

		private static FlagDefinition Timeout(string value = "30000") => new()
		{
			Key = "client.connection.reconnect_timeout_ms",
			Label = "Reconnect timeout",
			Group = "Connection",
			Type = FlagTypes.Int,
			Unit = "ms",
			Min = 1000,
			Max = 120000,
			Value = J(value)
		};

		private static JsonObject Context(long user, string[]? roles = null, string? country = "NL", string? client = "generals-online") =>
			FlagContext.Create(user, roles ?? [], client, null, country, "test");

		[Fact]
		public void ACompleteFieldPasses()
		{
			Assert.Null(FlagRules.Check(Timeout()));
		}

		[Theory]
		[InlineData("\"30000\"")]
		[InlineData("500")]
		[InlineData("120001")]
		[InlineData("1.5")]
		[InlineData("true")]
		public void AValueOfTheWrongTypeOrOutOfBoundsNamesTheValueField(string value)
		{
			Assert.Equal("value", FlagRules.Check(Timeout(value)));
		}

		[Theory]
		[InlineData("Client.Connection")]
		[InlineData("client..timeout")]
		[InlineData("client.connection.")]
		[InlineData("draft")]
		[InlineData("revisions")]
		public void BadKeysAreRefused(string key)
		{
			Assert.Equal("key", FlagRules.Check(Timeout() with { Key = key }));
		}

		[Fact]
		public void BlankLabelAndGroupAreRefused()
		{
			Assert.Equal("label", FlagRules.Check(Timeout() with { Label = " " }));
			Assert.Equal("group", FlagRules.Check(Timeout() with { Group = "" }));
		}

		[Fact]
		public void MinAboveMaxNamesMax()
		{
			Assert.Equal("max", FlagRules.Check(Timeout() with { Min = 5, Max = 4, Value = J("5") }));
		}

		[Fact]
		public void BoundsOnlyFitNumbersAndText()
		{
			var toggle = new FlagDefinition { Key = "client.lobby.host_migration", Label = "Host migration", Group = "Lobby", Type = FlagTypes.Bool, Value = J("false"), Min = 0 };
			Assert.Equal("min", FlagRules.Check(toggle));
			Assert.Null(FlagRules.Check(toggle with { Min = null }));
		}

		[Fact]
		public void TextLongerThanMaxIsRefused()
		{
			var text = new FlagDefinition { Key = "client.send.mode", Label = "Mode", Group = "Send", Type = FlagTypes.String, Max = 4, Value = J("\"datagram\"") };
			Assert.Equal("value", FlagRules.Check(text));
			Assert.Null(FlagRules.Check(text with { Max = 8 }));
		}

		[Fact]
		public void AChoiceValueMustBeOneOfTheChoices()
		{
			var mode = new FlagDefinition
			{
				Key = "client.send.mode",
				Label = "Send mode",
				Group = "Send",
				Type = FlagTypes.Choice,
				Choices = [new FlagChoice { Value = J("\"datagram\""), Label = "Datagram" }, new FlagChoice { Value = J("\"stream\""), Label = "Stream" }],
				Value = J("\"stream\"")
			};
			Assert.Null(FlagRules.Check(mode));
			Assert.Equal("value", FlagRules.Check(mode with { Value = J("\"carrier pigeon\"") }));
			Assert.Equal("choices", FlagRules.Check(mode with { Choices = [] }));
			Assert.Equal("choices", FlagRules.Check(mode with { Choices = [mode.Choices![0], mode.Choices[0]] }));
			Assert.Equal("choices", FlagRules.Check(Timeout() with { Choices = mode.Choices }));
		}

		[Fact]
		public void APercentageRuleMustBeLastAndBetween1And99()
		{
			FlagRule percent = new() { Kind = FlagRuleKinds.Percentage, Percent = 10, Value = J("45000") };
			FlagRule role = new() { Kind = FlagRuleKinds.Role, Values = ["streamer"], Value = J("60000") };
			Assert.Null(FlagRules.Check(Timeout() with { Rules = [role, percent] }));
			Assert.Equal("rules", FlagRules.Check(Timeout() with { Rules = [percent, role] }));
			Assert.Equal("rules", FlagRules.Check(Timeout() with { Rules = [percent with { Percent = 100 }] }));
			Assert.Equal("rules", FlagRules.Check(Timeout() with { Rules = [percent with { Percent = 0 }] }));
		}

		[Fact]
		public void RuleValuesAreChecked()
		{
			Assert.Equal("rules", FlagRules.Check(Timeout() with { Rules = [new FlagRule { Kind = FlagRuleKinds.Role, Values = ["nobody"], Value = J("45000") }] }));
			Assert.Equal("rules", FlagRules.Check(Timeout() with { Rules = [new FlagRule { Kind = FlagRuleKinds.Country, Values = ["nl"], Value = J("45000") }] }));
			Assert.Equal("rules", FlagRules.Check(Timeout() with { Rules = [new FlagRule { Kind = FlagRuleKinds.Country, Values = ["NL"], Value = J("1") }] }));
			Assert.Equal("rules", FlagRules.Check(Timeout() with { Rules = [new FlagRule { Kind = FlagRuleKinds.Client, Values = [], Value = J("45000") }] }));
		}

		[Theory]
		[InlineData("reconnect_timeout_ms", "Reconnect timeout", "ms")]
		[InlineData("slack_percent", "Slack", "%")]
		[InlineData("screenshot_upload_bytes_per_sec", "Screenshot upload bytes per", "seconds")]
		[InlineData("relay_only", "Relay only", null)]
		[InlineData("ms", "Ms", null)]
		public void LabelsComeFromKeyWords(string word, string label, string? unit)
		{
			Assert.Equal((label, unit), FlagRules.LabelOf(word));
		}

		[Fact]
		public void FriendlyRulesRoundTripThroughFlagd()
		{
			FlagDefinition flag = Timeout() with
			{
				Description = "How long a dropped player may take to come back",
				Rules =
				[
					new FlagRule { Kind = FlagRuleKinds.Role, Values = ["streamer", "admin"], Value = J("60000") },
					new FlagRule { Kind = FlagRuleKinds.Country, Values = ["NL", "BE"], Value = J("40000") },
					new FlagRule { Kind = FlagRuleKinds.Percentage, Percent = 25, Value = J("45000") }
				]
			};

			JsonObject flagd = Flagd.ToFlag(flag);
			(FlagDefinition? back, string? field) = Flagd.FromFlag(flag.Key, flagd);

			Assert.Null(field);
			Assert.Equal(FlagStore.Serialize(flag), FlagStore.Serialize(back!));
			Assert.Equal(["default", "rule1", "rule2", "rule3"], ((JsonObject)flagd["variants"]!).Select(v => v.Key).ToArray());
			Assert.Equal("ENABLED", flagd["state"]!.GetValue<string>());
		}

		[Fact]
		public void CustomTargetingRoundTripsAndAPlainFlagHasNoTargeting()
		{
			FlagDefinition custom = Timeout() with
			{
				Custom = new FlagCustom
				{
					Variants = new JsonObject { ["low"] = 30000, ["high"] = 90000 },
					DefaultVariant = "low",
					Targeting = JsonNode.Parse("""{"if":[{"sem_ver":[{"var":"client_version"},">=","2.0.0"]},"high",null]}""")
				}
			};
			Assert.Null(FlagRules.Check(custom));
			(FlagDefinition? back, _) = Flagd.FromFlag(custom.Key, Flagd.ToFlag(custom));
			Assert.Equal(FlagStore.Serialize(custom), FlagStore.Serialize(back!));

			Assert.False(Flagd.ToFlag(Timeout()).ContainsKey("targeting"));
		}

		[Fact]
		public void CustomVariantsAreTypeChecked()
		{
			FlagDefinition custom = Timeout() with
			{
				Custom = new FlagCustom { Variants = new JsonObject { ["low"] = 30000, ["bad"] = "x" }, DefaultVariant = "low" }
			};
			Assert.Equal("custom", FlagRules.Check(custom));
		}

		[Fact]
		public void AHandWrittenFlagWithoutMetadataGetsAGuessedSchema()
		{
			JsonNode node = JsonNode.Parse("""{"state":"ENABLED","variants":{"on":true,"off":false},"defaultVariant":"off"}""")!;
			(FlagDefinition? flag, _) = Flagd.FromFlag("client.lobby.host_migration", node);
			Assert.Equal(FlagTypes.Bool, flag!.Type);
			Assert.Equal("Host migration", flag.Label);
			Assert.Equal("lobby", flag.Group);
			Assert.NotNull(flag.Custom);
			Assert.Null(FlagRules.Check(flag));
		}

		[Fact]
		public void RoleRuleMatchesOnlyPlayersWithTheRole()
		{
			JsonObject flagd = Flagd.ToFlag(Timeout() with { Rules = [new FlagRule { Kind = FlagRuleKinds.Role, Values = ["streamer"], Value = J("60000") }] });

			FlagResult streamer = FlagEvaluator.Evaluate("k", flagd, Context(1, ["streamer"]));
			FlagResult player = FlagEvaluator.Evaluate("k", flagd, Context(2));

			Assert.Equal(60000, streamer.Value!.GetValue<int>());
			Assert.Equal(("rule1", FlagReasons.TargetingMatch), (streamer.Variant, streamer.Reason));
			Assert.Equal(30000, player.Value!.GetValue<int>());
			Assert.Equal("default", player.Variant);
		}

		[Fact]
		public void CountryAndClientRulesReadTheContext()
		{
			JsonObject flagd = Flagd.ToFlag(Timeout() with
			{
				Rules =
				[
					new FlagRule { Kind = FlagRuleKinds.Client, Values = ["test-client"], Value = J("50000") },
					new FlagRule { Kind = FlagRuleKinds.Country, Values = ["DE"], Value = J("40000") }
				]
			});

			Assert.Equal(50000, FlagEvaluator.Evaluate("k", flagd, Context(1, client: "test-client", country: "DE")).Value!.GetValue<int>());
			Assert.Equal(40000, FlagEvaluator.Evaluate("k", flagd, Context(1, country: "DE")).Value!.GetValue<int>());
			Assert.Equal(30000, FlagEvaluator.Evaluate("k", flagd, Context(1, country: null)).Value!.GetValue<int>());
		}

		[Fact]
		public void APercentageRuleSplitsPlayersStablyNearTheShare()
		{
			JsonObject flagd = Flagd.ToFlag(Timeout() with { Rules = [new FlagRule { Kind = FlagRuleKinds.Percentage, Percent = 20, Value = J("45000") }] });

			int chosen = 0;
			for (long user = 1; user <= 5000; user++)
			{
				FlagResult first = FlagEvaluator.Evaluate("client.connection.reconnect_timeout_ms", flagd, Context(user));
				FlagResult again = FlagEvaluator.Evaluate("client.connection.reconnect_timeout_ms", flagd, Context(user));
				Assert.Equal(first.Variant, again.Variant);
				Assert.Equal(FlagReasons.Split, first.Reason);
				if (first.Variant == "rule1") chosen++;
			}
			Assert.InRange(chosen, 850, 1150);
		}

		[Theory]
		[InlineData("", 0x00000000u)]
		[InlineData("hello", 0x248bfa47u)]
		[InlineData("The quick brown fox jumps over the lazy dog", 0x2e4ff723u)]
		public void Murmur3MatchesTheReferenceVectors(string text, uint hash)
		{
			Assert.Equal(hash, FlagEvaluator.Murmur3(Encoding.UTF8.GetBytes(text)));
		}

		[Theory]
		[InlineData("2.1.0", ">=", "2.0.0", true)]
		[InlineData("v1.9.9", ">=", "2.0.0", false)]
		[InlineData("2.4.1", "^", "2.0.0", true)]
		[InlineData("2.4.1", "~", "2.3.0", false)]
		[InlineData("2026.11.1", "=", "2026.11.1", true)]
		public void SemVerComparesVersions(string left, string op, string right, bool expected)
		{
			var flag = new JsonObject
			{
				["variants"] = new JsonObject { ["yes"] = true, ["no"] = false },
				["defaultVariant"] = "no",
				["targeting"] = new JsonObject { ["if"] = new JsonArray(new JsonObject { ["sem_ver"] = new JsonArray(left, op, right) }, "yes", "no") }
			};
			Assert.Equal(expected, FlagEvaluator.Evaluate("k", flag, new JsonObject()).Value!.GetValue<bool>());
		}

		[Fact]
		public void AnUnknownOperatorFallsBackToTheDefaultWithAnError()
		{
			var flag = new JsonObject
			{
				["variants"] = new JsonObject { ["a"] = 1, ["b"] = 2 },
				["defaultVariant"] = "a",
				["targeting"] = new JsonObject { ["nope"] = new JsonArray() }
			};
			FlagResult result = FlagEvaluator.Evaluate("k", flag, new JsonObject());
			Assert.Equal(("a", FlagReasons.Error), (result.Variant, result.Reason));
		}

		[Fact]
		public void TheSeedGivesTheGameExactlyTheOldServiceConfig()
		{
			List<FlagDefinition> flags = FlagSeed.FromGroupedConfig((JsonObject)JsonNode.Parse(FlagSeed.ServiceConfig)!);
			Assert.All(flags, f => Assert.Null(FlagRules.Check(f)));
			Assert.Equal(39, flags.Count);
			Assert.Contains(flags, f => f.Key == "client.input_delay.formula.repair_budget" && f.Group == "Input delay / Formula" && f.Type == FlagTypes.Bool);
			Assert.Contains(flags, f => f.Key == "client.connection.reconnect_timeout_ms" && f.Label == "Reconnect timeout" && f.Unit == "ms" && f.Type == FlagTypes.Int);

			FlagSnapshot snapshot = FlagSnapshot.Parse(1, Flagd.ToDocument(flags).ToJsonString());
			JsonObject grouped = snapshot.ServiceConfigFor(Context(7));

			Assert.True(JsonNode.DeepEquals(JsonNode.Parse(FlagSeed.ServiceConfig), grouped), grouped.ToJsonString());
		}

		[Fact]
		public void DisabledAndServiceFlagsStayOutOfTheClientAnswer()
		{
			FlagDefinition service = Timeout() with { Key = "service.matchmaking.tick_ms", Scope = FlagScopes.Service };
			FlagDefinition off = Timeout() with { Key = "client.connection.off", Enabled = false };
			FlagSnapshot snapshot = FlagSnapshot.Parse(3, Flagd.ToDocument([Timeout(), service, off]).ToJsonString());

			Assert.Equal(["client.connection.reconnect_timeout_ms"], snapshot.EvaluateClient(Context(1)).Select(r => r.Key).ToArray());
		}

		[Fact]
		public void TheETagFollowsRevisionAndContext()
		{
			FlagSnapshot one = FlagSnapshot.Parse(1, Flagd.ToDocument([Timeout()]).ToJsonString());
			FlagSnapshot two = FlagSnapshot.Parse(2, Flagd.ToDocument([Timeout()]).ToJsonString());

			Assert.Equal(one.ETagFor(Context(1)), one.ETagFor(Context(1)));
			Assert.NotEqual(one.ETagFor(Context(1)), one.ETagFor(Context(2)));
			Assert.NotEqual(one.ETagFor(Context(1)), one.ETagFor(Context(1, ["streamer"])));
			Assert.NotEqual(one.ETagFor(Context(1)), two.ETagFor(Context(1)));
		}

		[Fact]
		public void DiffNamesAddedRemovedAndChangedFields()
		{
			FlagDefinition kept = Timeout();
			FlagDefinition gone = Timeout() with { Key = "client.send.direct", Type = FlagTypes.Bool, Min = null, Max = null, Unit = null, Value = J("true") };
			JsonObject before = Flagd.ToDocument([kept, gone]);
			JsonObject after = Flagd.ToDocument([kept with { Value = J("45000"), Label = "Reconnect wait" }, kept with { Key = "client.send.fresh" }]);

			List<FlagChange> changes = FlagStore.Diff(before, after);

			Assert.Equal([("client.connection.reconnect_timeout_ms", "changed"), ("client.send.direct", "removed"), ("client.send.fresh", "added")],
				changes.Select(c => (c.Key, c.Change)).ToArray());
			Assert.Equal(["label", "value"], changes[0].Fields);
			Assert.Empty(FlagStore.Diff(before, before));
		}

		[Fact]
		public void ADocumentWithABadFlagNamesIt()
		{
			JsonObject document = Flagd.ToDocument([Timeout()]);
			document["flags"]!["client.connection.reconnect_timeout_ms"]!["variants"]!["default"] = "slow";
			(List<FlagDefinition>? flags, Realtime.Failure? failure) = FlagStore.ReadDocument(document);
			Assert.Null(flags);
			Assert.Equal(Realtime.Result.Invalid("flags.client.connection.reconnect_timeout_ms.value"), failure);
			Assert.Equal(Realtime.Result.Invalid("flags"), FlagStore.ReadDocument(JsonNode.Parse("[]")).Failure);
		}
	}
}
