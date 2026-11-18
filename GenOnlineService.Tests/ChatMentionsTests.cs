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

using Database;
using GenOnlineService.Realtime;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

// Message.mentions (section 4): @name and @name#tag resolved against the conversation's members, and @name#tag against anyone in a channel
public class ChatMentionsTests
{
	private static readonly ChatMentions.Named Bob = new(1, "Bob", 42);
	private static readonly ChatMentions.Named BobSmith = new(2, "Bob Smith", 7);
	private static readonly ChatMentions.Named Ann = new(3, "Ann", 1);
	private static readonly ChatMentions.Named Zoe = new(4, "Zoë", 9);

	private static long[] Members(string content, params ChatMentions.Named[] members) => ChatMentions.Resolve(content, members, []);

	[Fact]
	public void TheLongestMatchingNameWinsAndNamesMayContainSpaces()
	{
		Assert.Equal(new long[] { 2 }, Members("@Bob Smith we need a fourth", Bob, BobSmith));
		Assert.Equal(new long[] { 1 }, Members("@Bob Smithers is here", Bob, BobSmith));
		Assert.Equal(new long[] { 1 }, Members("@Bob hi", Bob, BobSmith));
	}

	[Theory]
	[InlineData("@Bob", true)]
	[InlineData("@Bob hi", true)]
	[InlineData("@Bob, hi", true)]
	[InlineData("@Bob!", true)]
	[InlineData("(@Bob)", true)]
	[InlineData("@Bob#", true)]
	[InlineData("@Bob#hey", true)]
	[InlineData("@Bobby", false)]
	[InlineData("@Bob2", false)]
	[InlineData("@ Bob", false)]
	[InlineData("Bob", false)]
	public void TheNameMustEndAtTheEndWhitespacePunctuationOrHash(string content, bool mentioned)
	{
		Assert.Equal(mentioned ? new long[] { 1 } : Array.Empty<long>(), Members(content, Bob));
	}

	[Fact]
	public void NamesCompareCaseInsensitively()
	{
		Assert.Equal(new long[] { 1 }, Members("@bOB look", Bob));
		Assert.Equal(new long[] { 2 }, Members("@BOB SMITH look", Bob, BobSmith));
	}

	[Fact]
	public void NamesCompareAfterNfcSoComposedAndDecomposedFormsMatch()
	{
		// the stored name is composed, the text decomposed, and the other way round
		Assert.Equal(new long[] { 4 }, Members("@Zoë gg", Zoe));
		Assert.Equal(new long[] { 5 }, Members("@Zoë gg", new ChatMentions.Named(5, "Zoë", 3)));
	}

	[Theory]
	[InlineData("@B0b hi")]
	[InlineData("@Ｂｏｂ hi")]
	[InlineData("@Bо b")]
	[InlineData("@Вob hi")]
	public void ALookAlikeNameDoesNotMatch(string content)
	{
		// zero for o, fullwidth letters that NFKC would fold, Cyrillic letters: the look-alike key is never used
		Assert.Empty(Members(content, Bob));
	}

	[Theory]
	[InlineData("@Bob#42 hi", true)]
	[InlineData("@Bob#0042 hi", true)]
	[InlineData("@Bob#000042", true)]
	[InlineData("@Bob#0042, hi", true)]
	[InlineData("@Bob#0007 hi", false)]
	[InlineData("@Bob#7", false)]
	public void TheTagMatchesWithOrWithoutLeadingZerosAndAWrongTagMentionsNobody(string content, bool mentioned)
	{
		Assert.Equal(mentioned ? new long[] { 1 } : Array.Empty<long>(), Members(content, Bob));
	}

	[Fact]
	public void TwoMembersSharingANameAreSeparatedByTheTag()
	{
		ChatMentions.Named otherBob = new(9, "bob", 5);
		Assert.Equal(new long[] { 9 }, Members("@Bob#0005 hi", Bob, otherBob));
		Assert.Equal(new long[] { 1 }, Members("@Bob#42 hi", Bob, otherBob));
		// a bare name both share mentions both, in member order
		Assert.Equal(new long[] { 1, 9 }, Members("@Bob hi", Bob, otherBob));
	}

	[Fact]
	public void MentionsFollowTheOrderOfAppearanceWithoutDuplicates()
	{
		Assert.Equal(new long[] { 3, 1 }, Members("@Ann and @Bob", Bob, Ann));
		Assert.Equal(new long[] { 1, 3 }, Members("@Bob and @Ann", Bob, Ann));
		Assert.Equal(new long[] { 1, 3 }, Members("@Bob @bob @Bob#0042 @Ann @BOB", Bob, Ann));
	}

	[Fact]
	public void AtMostMentionMaxUsersAreMentionedAndTheRestStayPlainText()
	{
		ChatMentions.Named[] members = Enumerable.Range(1, 12).Select(i => new ChatMentions.Named(100 + i, $"Player{i:00}", i)).ToArray();
		string content = String.Join(" ", members.Select(m => "@" + m.Name));

		long[] mentioned = Members(content, members);

		Assert.Equal(10, ChatMentions.Max);
		Assert.Equal(Enumerable.Range(101, 10).Select(i => (long)i), mentioned);
	}

	[Fact]
	public void AnOutsiderResolvesOnlyByAnExactNameAndTag()
	{
		ChatMentions.Named carl = new(20, "Carl", 7);

		Assert.Equal(new long[] { 20 }, ChatMentions.Resolve("@Carl#0007 we need a fourth", [Bob], [carl]));
		Assert.Equal(new long[] { 20 }, ChatMentions.Resolve("@carl#7", [Bob], [carl]));
		Assert.Empty(ChatMentions.Resolve("@Carl we need a fourth", [Bob], [carl]));
		Assert.Empty(ChatMentions.Resolve("@Carl#0008", [Bob], [carl]));
		// an outsider's longer name wins over a member's shorter one
		ChatMentions.Named bobSmith = new(21, "Bob Smith", 3);
		Assert.Equal(new long[] { 21 }, ChatMentions.Resolve("@Bob Smith#0003", [Bob], [bobSmith]));
	}

	[Fact]
	public void TheOutsiderLookupNamesTheTextBeforeEachTag()
	{
		string[] names = ChatMentions.OutsiderNames("hi @Carl#0007 and @Bob Smith#3, @Dan#x");
		Assert.Contains("Carl", names);
		Assert.Contains("Bob Smith", names);
		// a '#' without a number is no tag
		Assert.DoesNotContain(names, name => name.StartsWith("Dan", StringComparison.Ordinal));
		Assert.Empty(ChatMentions.OutsiderNames("no mentions # here"));
	}

	[Fact]
	public void TextWithoutMentionsResolvesToNothing()
	{
		Assert.Empty(Members("hello", Bob, Ann));
		Assert.Empty(Members("mail me at bob@", Bob));
	}

	// A message without mentions is on the wire exactly as before: the field is absent, never an empty list
	[Fact]
	public void AMessageWithoutMentionsSerializesExactlyAsBefore()
	{
		Message message = new Message
		{
			Id = "8812",
			ChannelId = "channel:3",
			Type = MessageType.Chat,
			Author = new Member { Id = 7, Name = "Ann", Badges = ["moderator"] },
			Content = "hello",
			CreatedAt = new DateTime(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc),
			Mentions = ChatMentions.OrNull([])
		};

		Assert.Equal("{\"id\":\"8812\",\"channel_id\":\"channel:3\",\"type\":\"chat\",\"author\":{\"id\":7,\"name\":\"Ann\",\"badges\":[\"moderator\"]},\"content\":\"hello\",\"created_at\":\"2026-11-14T20:00:00Z\"}",
			JsonSerializer.Serialize(message, RealtimeJson.Create()));
		Assert.Contains("\"mentions\":[42]", JsonSerializer.Serialize(message with { Mentions = ChatMentions.OrNull([42]) }, RealtimeJson.Create()));
	}

	[Fact]
	public void ReadyLimitsCarryMentionMax()
	{
		Assert.Equal(10, RealtimeHub.BuildLimits().MentionMax);
	}

	private static async Task<long> AddUser(ProbeDatabase probe, string name, int tag)
	{
		await using AppDbContext db = probe.Open();
		User user = new User { Username = name, UsernameNormalized = NameNormalizer.Normalize(name), NameTag = tag, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user.ID;
	}

	// Members resolve by their stored name in every conversation, an outsider only by an exact @name#tag and only in a channel
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task OutsidersResolveByNameAndTagOnlyInAChannel(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		long ann = await AddUser(probe, "Ann Lee", 11);
		long bob = await AddUser(probe, "Bob", 42);
		long carl = await AddUser(probe, "Carl", 7);
		await AddUser(probe, "Carl", 8);

		await using AppDbContext db = probe.Open();
		string content = "@Ann Lee @Carl#0007 @Carl @Bob";

		Assert.Equal(new[] { ann, carl, bob }, await ChatMentions.ResolveAsync(db, content, [ann, bob], outsidersByTag: true));
		Assert.Equal(new[] { ann, bob }, await ChatMentions.ResolveAsync(db, content, [ann, bob], outsidersByTag: false));
		Assert.Empty(await ChatMentions.ResolveAsync(db, "@Carl#0009 @Dan#0001", [ann, bob], outsidersByTag: true));
	}

	// mentions is stored with the message and every history read returns it; a message without any keeps the column null
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task StoredMentionsComeBackFromHistory(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		long ann = await AddUser(probe, "Ann", 1);
		long bob = await AddUser(probe, "Bob", 2);
		long[] mentions = [bob, ann];

		await using (AppDbContext db = probe.Open())
		{
			await Database.Chat.InsertChannelMessage(db, 3, ann, "chat", "@Bob @Ann", mentions);
			await Database.Chat.InsertChannelMessage(db, 3, ann, "chat", "hello");
			await Database.Chat.InsertLobbyMessage(db, 9, null, ann, "chat", "@Bob @Ann", mentions);
			await Database.Chat.InsertDirectMessage(db, ann, bob, "chat", "@Bob @Ann", mentions);
			Database.GroupInfo group = await Database.Chat.CreateGroup(db, ann, null, [bob]);
			await Database.Chat.InsertGroupMessage(db, group.Group.ID, ann, "chat", "@Bob @Ann", mentions);
		}

		await using (AppDbContext db = probe.Open())
		{
			Dictionary<long, (string Name, int Tag)> names = new();
			ChatPage channel = await Database.Chat.GetChannelHistory(db, 3, null, 10);
			Assert.Equal(mentions, RealtimeHub.ToWire(channel.Messages[0], ann, names).Mentions);
			Assert.Null(RealtimeHub.ToWire(channel.Messages[1], ann, names).Mentions);
			Assert.Null(channel.Messages[1].Mentions);

			ChatPage lobby = await Database.Chat.GetLobbyHistory(db, 9, DateTime.UnixEpoch, null, 10);
			Assert.Equal(mentions, RealtimeHub.ToWire(lobby.Messages.Single(), ann, names).Mentions);

			ChatPage direct = await Database.Chat.GetDirectHistory(db, bob, ann, null, 10);
			Assert.Equal(mentions, RealtimeHub.ToWire(direct.Messages.Single(), bob, names).Mentions);

			Database.GroupInfo group = (await Database.Chat.GetGroupsOf(db, bob)).Single();
			ChatPage groupPage = await Database.Chat.GetGroupHistory(db, group.Group.ID, null, 10);
			Assert.Equal(mentions, RealtimeHub.ToWire(groupPage.Messages.Single(), bob, names).Mentions);
		}
	}
}
