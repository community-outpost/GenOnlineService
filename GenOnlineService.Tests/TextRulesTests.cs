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
using GenOnlineService.TextRules;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

// The text checks (S7) on the seed lists and default rules: display names, lobby names and chat, with the disguises the
// folding reads through, the exceptions that stay allowed, the masking of chat and the rule workflows
public class TextRulesTests
{
	// Names go through the rename and sign-up rule, ProfileActions.ReservedWordIn
	[Theory]
	[InlineData("Fuckface", "fuck")]
	[InlineData("F.u.c.k", "fuck")]
	[InlineData("F u c k", "fuck")]
	[InlineData("Fuuuuuck", "fuck")]
	[InlineData("FU-CK-ER", "fuck")]
	[InlineData("Fu\u0441k", "fuck")]
	[InlineData("N1gger", "nigger")]
	[InlineData("Pu$$y", "pussy")]
	[InlineData("\u0421\u043E\u0441k", "cock")]
	[InlineData("Sharmoota", "sharmoota")]
	[InlineData("\u0634\u0631\u0645\u0648\u0637\u0629", "\u0634\u0631\u0645\u0648\u0637\u0629")]
	[InlineData("\u0634\u064E\u0631\u0652\u0645\u064F\u0648\u0637\u064E\u0629", "\u0634\u0631\u0645\u0648\u0637\u0629")]
	[InlineData("\u0634\u0631\u0645\u0640\u0640\u0640\u0648\u0637\u0647", "\u0634\u0631\u0645\u0648\u0637\u0629")]
	[InlineData("3ars", "\u0639\u0631\u0635")]
	[InlineData("m3ras", "\u0645\u0639\u0631\u0635")]
	[InlineData("Adm1n", "admin")]
	[InlineData("St4ff", "staff")]
	[InlineData("M.o.d.e.r.a.t.o.r", "moderator")]
	public void ADisguisedListedWordRefusesADisplayName(string name, string word)
	{
		Assert.Equal(word, ProfileActions.ReservedWordIn(name));
	}

	// exceptions, words a listed word only looks like, and the flag words a name may hold
	[Theory]
	[InlineData("Scunthorpe")]
	[InlineData("Cocktail")]
	[InlineData("Peacock")]
	[InlineData("Therapist")]
	[InlineData("Classic")]
	[InlineData("Bobby")]
	[InlineData("Modern")]
	[InlineData("Assassin")]
	[InlineData("Grapes")]
	[InlineData("Sussex")]
	[InlineData("\u0632\u0628\u0648\u0646")]
	[InlineData("\u0643\u0633\u0644\u0627\u0646")]
	public void AllowedNamesPass(string name)
	{
		Assert.Null(ProfileActions.ReservedWordIn(name));
	}

	// The plugin's reserved words were moved into the lists: every one still refuses the names the plugin refused
	[Fact]
	public void TheReservedWordsStillRefuseTheSameNames()
	{
		foreach (string word in ProfileActions.ReservedNameWords)
		{
			Assert.Equal(word, ProfileActions.ReservedWordIn(word));
			Assert.NotNull(ProfileActions.ReservedWordIn("xx" + word.ToUpperInvariant() + "yy"));
		}
		TextTermInfo hitler = Assert.Single(TextChecks.Current.Terms, t => t.Term == "hitler");
		Assert.Equal((TextCategories.Reserved, TextMatchModes.Anywhere), (hitler.Category, hitler.Match));
		Assert.Equal(TextMatchModes.Literal, Assert.Single(TextChecks.Current.Terms, t => t.Term == "mass^").Match);
		Assert.Equal(TextCategories.Impersonation, Assert.Single(TextChecks.Current.Terms, t => t.Term == "admin").Category);
	}

	[Theory]
	[InlineData("fuck noobs", "fuck")]
	[InlineData("n i g g e r s only", "nigger")]
	[InlineData("\u0643\u0633 \u0627\u0645\u0643", "\u0643\u0633 \u0627\u0645\u0643")]
	public void ABlockedWordRefusesALobbyName(string name, string word)
	{
		Failure failure = Assert.IsType<Failure>(TextChecks.LobbyNameRefusal(name));
		Assert.Equal(ErrorCodes.NameReserved, failure.Code);
		Assert.Equal(word, JsonSerializer.SerializeToElement(failure.Params).GetProperty("Word").GetString());
	}

	[Theory]
	[InlineData("1v1 no rush")]
	[InlineData("admin game")]
	[InlineData("shit map, 4v4")]
	[InlineData("classic cocktail party")]
	public void OtherLobbyNamesPass(string name)
	{
		Assert.Null(TextChecks.LobbyNameRefusal(name));
	}

	[Theory]
	[InlineData("you are a nigger")]
	[InlineData("f u c k you")]
	[InlineData("FUUUCK this")]
	[InlineData("enta 3ars")]
	[InlineData("\u064A\u0627 \u0634\u0631\u0645\u0648\u0637\u0629")]
	[InlineData("kill yourself")]
	public void ABlockedWordBlocksAChatMessage(string content)
	{
		(Failure? blocked, _) = TextChecks.ChatContent(content);
		Assert.Equal(TextChecks.ChatBlocked, blocked?.Code);
	}

	[Theory]
	[InlineData("you idiot", "you *****")]
	[InlineData("shit happens", "**** happens")]
	[InlineData("nice a$$ build", "nice *** build")]
	[InlineData("sh1t, gg", "****, gg")]
	[InlineData("\u064A\u0627 \u0643\u0644\u0628 gg", "** *** gg")]
	public void AFlaggedWordIsMaskedInChat(string content, string masked)
	{
		(Failure? blocked, string delivered) = TextChecks.ChatContent(content);
		Assert.Null(blocked);
		Assert.Equal(masked, delivered);
	}

	// a message with nothing listed is delivered as it was sent
	[Theory]
	[InlineData("gl hf, see you in the classic map")]
	[InlineData("this hit was lucky")]
	[InlineData("the admin said hi")]
	[InlineData("\u0645\u0631\u062D\u0628\u0627 \u0643\u064A\u0641 \u062D\u0627\u0644\u0643")]
	[InlineData("Scunthorpe united")]
	[InlineData("thumping the enemy base")]
	[InlineData("trimming the build order")]
	[InlineData("bisexuality and heterosexuality")]
	public void OtherChatIsDeliveredUnchanged(string content)
	{
		Assert.Equal(((Failure?)null, content), TextChecks.ChatContent(content));
	}

	// The imported LDNOOBW lists (flag words): matched in display names, masked in chat
	[Theory]
	[InlineData("Bukkake", "bukkake")]
	[InlineData("XxCamwh0rexX", "camwhore")]
	[InlineData("B.u.t.t.h.o.l.e", "butthole")]
	[InlineData("\u0627\u063A\u062A\u0635\u0627\u0628", "\u0627\u063A\u062A\u0635\u0627\u0628")]
	public void AnImportedWordRefusesADisplayName(string name, string word)
	{
		Assert.Equal(word, ProfileActions.ReservedWordIn(name));
		TextTermInfo term = TextChecks.Current.Terms.First(t => t.Term == word);
		Assert.Equal((TextSeverities.Flag, TextCategories.Profanity), (term.Severity, term.Category));
	}

	[Theory]
	[InlineData("nice camel toe", "nice ***** ***")]
	[InlineData("total bukkake gg", "total ******* gg")]
	[InlineData("\u064A\u0627 \u0644\u0628\u0648\u0629 gg", "\u064A\u0627 **** gg")]
	public void AnImportedWordIsMaskedInChat(string content, string masked)
	{
		Assert.Equal(((Failure?)null, masked), TextChecks.ChatContent(content));
	}

	// Display names are stricter than chat: a flag word that chat only masks refuses the name
	[Theory]
	[InlineData("Idiot", "idiot")]
	[InlineData("Shitlord", "shit")]
	[InlineData("DumbassKing", "dumbass")]
	public void AFlagWordRefusesADisplayName(string name, string word)
	{
		Assert.Equal(word, ProfileActions.ReservedWordIn(name));
		Assert.Equal(TextOutcomes.Mask, TextChecks.Check(TextKinds.Chat, name).Outcome);
	}

	// Entries the folding cannot read and entries game chat uses innocently stay out of the lists
	[Fact]
	public void TheImportLeavesOutWhatItCannotReadOrWhatGamesSayInnocently()
	{
		Assert.DoesNotContain(TextChecks.Current.Terms, t => t.Term is "2g1c" or "s&m" or "escort" or "domination" or "hardcore" or "suck" or "sucks" or "tied up");
		Assert.Equal(((Failure?)null, "escort the convoy, domination mode, hardcore ai sucks"), TextChecks.ChatContent("escort the convoy, domination mode, hardcore ai sucks"));
		Assert.Null(TextChecks.LobbyNameRefusal("shit map hardcore"));
		Assert.Equal(TextRuleSources.Ldnoobw, TextRuleSeeds.Terms().First(t => t.Term == "bukkake").Source);
		// a word both lists hold keeps the service's own entry
		Assert.Equal((TextRuleSources.Seed, TextSeverities.Block), TextRuleSeeds.Terms().Where(t => t.Term == "fuck").Select(t => (t.Source, t.Severity)).Single());
	}

	// The test page shows how a text was read and what it matched
	[Fact]
	public void TheCheckShowsTheReadingsAndMatches()
	{
		TextCheck check = TextChecks.Check(TextKinds.Chat, "sh1t m3ras");
		Assert.Contains(check.Readings, r => r.Name == TextFolding.LatinReading && r.Text == "shit meras");
		Assert.Contains(check.Readings, r => r.Name == TextFolding.LatinAltReading && r.Text == "shlt meras");
		Assert.Contains(check.Readings, r => r.Name == TextFolding.ArabiziReading);
		Assert.Equal(["shit", "\u0645\u0639\u0631\u0635"], check.Hits.Select(h => h.Term.Term).ToArray());
		Assert.Equal((TextOutcomes.Block, "blocked words"), (check.Outcome, check.Rule));
		Assert.Equal((1, 1), (check.Facts.Block, check.Facts.Flag));
	}

	[Fact]
	public void WorkflowsAreValidatedBeforeTheyAreStored()
	{
		Assert.NotNull(TextRuleWorkflows.Parse(TextKinds.Chat, "not json").Error);
		Assert.NotNull(TextRuleWorkflows.Parse(TextKinds.Chat, "{\"rules\":[{\"name\":\"x\",\"expression\":\"input.Block > 0\",\"outcome\":\"block\",\"actions\":{}}]}").Error);
		Assert.Contains("not an input field", TextRuleWorkflows.Parse(TextKinds.Chat, "{\"rules\":[{\"name\":\"x\",\"expression\":\"System.IO.File.Exists(\\\"a\\\")\",\"outcome\":\"block\"}]}").Error);
		Assert.Contains("not an input field", TextRuleWorkflows.Parse(TextKinds.Chat, "{\"rules\":[{\"name\":\"x\",\"expression\":\"input.Kind.GetType() != null\",\"outcome\":\"block\"}]}").Error);
		Assert.Contains("outcome", TextRuleWorkflows.Parse(TextKinds.DisplayName, "{\"rules\":[{\"name\":\"x\",\"expression\":\"input.Block > 0\",\"outcome\":\"mask\"}]}").Error);
		Assert.Contains("rule x", TextRuleWorkflows.Parse(TextKinds.Chat, "{\"rules\":[{\"name\":\"x\",\"expression\":\"input.Block >\",\"outcome\":\"block\"}]}").Error);
		Assert.Contains("unique", TextRuleWorkflows.Parse(TextKinds.Chat, "{\"rules\":[{\"name\":\"x\",\"expression\":\"input.Block > 0\",\"outcome\":\"block\"},{\"name\":\"x\",\"expression\":\"input.Flag > 0\",\"outcome\":\"mask\"}]}").Error);
		Assert.NotNull(TextRuleWorkflows.Parse("profile", "{\"rules\":[]}").Error);

		(TextWorkflowDefinition? valid, string? error) = TextRuleWorkflows.Parse(TextKinds.Chat, "{\"rules\":[{\"name\":\"slurs\",\"expression\":\"input.Slur > 0 && input.Kind == \\\"chat\\\"\",\"outcome\":\"block\"},{\"name\":\"rest\",\"expression\":\"input.Matches > 0\",\"outcome\":\"mask\"}]}");
		Assert.Null(error);
		Assert.Equal(2, valid!.Rules.Count);
	}

	// Staff rules decide: with every match masked, a blocked word is masked instead of blocking the message
	[Fact]
	public void TheFirstRuleThatHoldsDecides()
	{
		var workflows = new Dictionary<string, TextWorkflowDefinition>(TextRuleWorkflows.Defaults)
		{
			[TextKinds.Chat] = new TextWorkflowDefinition
			{
				Rules =
				[
					new TextRuleDefinition { Name = "off", Expression = "input.Matches > 0", Outcome = TextOutcomes.Block, Enabled = false },
					new TextRuleDefinition { Name = "mask everything", Expression = "input.Matches > 0", Outcome = TextOutcomes.Mask }
				]
			}
		};
		var set = new TextRuleSet(TextRuleSeeds.Terms().Select(TextChecks.Info), TextRuleSeeds.Exceptions().Select(TextChecks.Info), new TextRuleWorkflows(workflows), "test");
		TextCheck check = set.Check(TextKinds.Chat, "fuck this");
		Assert.Equal((TextOutcomes.Mask, "mask everything", "**** this"), (check.Outcome, check.Rule, check.Masked));
		Assert.Equal(TextOutcomes.Allow, set.Check(TextKinds.Chat, "good game").Outcome);
	}

	// Chat checks stay cheap with a few thousand listed words: well under a millisecond per message on average
	[Fact]
	public void AChatCheckStaysUnderTheBudget()
	{
		var random = new Random(7);
		string Word(int min, int max)
		{
			var word = new StringBuilder();
			int length = random.Next(min, max + 1);
			for (int i = 0; i < length; i++)
			{
				word.Append((char)('a' + random.Next(26)));
			}
			return word.ToString();
		}

		List<TextTermInfo> terms = TextRuleSeeds.Terms().Select(TextChecks.Info).ToList();
		for (int i = 0; i < 4000; i++)
		{
			terms.Add(new TextTermInfo(100000 + i, Word(5, 9), i % 3 == 0 ? TextLanguages.Arabic : TextLanguages.English, TextCategories.Insult,
				i % 2 == 0 ? TextSeverities.Flag : TextSeverities.Block, i % 2 == 0 ? TextMatchModes.Word : TextMatchModes.Anywhere));
		}
		var stopwatch = Stopwatch.StartNew();
		var set = new TextRuleSet(terms, TextRuleSeeds.Exceptions().Select(TextChecks.Info), new TextRuleWorkflows(TextRuleWorkflows.Defaults), "perf");
		TimeSpan build = stopwatch.Elapsed;

		List<string> messages = Enumerable.Range(0, 2000).Select(i => String.Join(' ', Enumerable.Range(0, random.Next(4, 20)).Select(_ => Word(2, 8)))
			+ (i % 10 == 0 ? " sh1t" : "") + (i % 25 == 0 ? " 3ars" : "")).ToList();
		foreach (string message in messages.Take(50))
		{
			set.Check(TextKinds.Chat, message);
		}

		stopwatch.Restart();
		foreach (string message in messages)
		{
			set.Check(TextKinds.Chat, message);
		}
		double averageMs = stopwatch.Elapsed.TotalMilliseconds / messages.Count;
		Console.WriteLine($"text rules: {terms.Count} words built in {build.TotalMilliseconds:F0} ms, {averageMs * 1000:F1} us per chat message");
		Assert.True(averageMs < 1.0, $"a chat check took {averageMs:F3} ms on average");
		Assert.True(build < TimeSpan.FromSeconds(5), $"building took {build}");
	}
}
