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
using System.Reflection;
using Xunit;

namespace GenOnlineService.Tests;

// MentionCreate (section 6.2): a channel message that mentions a user none of whose connections is in the channel
public class MentionNoticesTests
{
	private const long Author = 7;
	private const long Channel = 3;

	private sealed class ManualClock : TimeProvider
	{
		private DateTimeOffset m_now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
		public override DateTimeOffset GetUtcNow() => m_now;
		public void Advance(TimeSpan by) => m_now += by;
	}

	// Records every event sent through one user's client
	public class Recorder : DispatchProxy
	{
		public long UserID;
		public List<(long UserID, string Event, object? Payload)>? Sent;

		protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
		{
			Sent!.Add((UserID, targetMethod!.Name, args?.FirstOrDefault()));
			return Task.CompletedTask;
		}
	}

	private static Func<long, MentionPresence> Presence(Dictionary<long, MentionPresence> online) =>
		id => online.TryGetValue(id, out MentionPresence presence) ? presence : MentionPresence.Offline;

	private static long[] Recipients(IReadOnlyList<long> mentions, Dictionary<long, MentionPresence> online, MentionThrottle throttle, long channel = Channel, params long[] blocked) =>
		MentionNotices.Recipients(Author, channel, mentions, Presence(online), blocked.ToHashSet(), throttle);

	[Fact]
	public void OnlyOnlineUsersWithNoConnectionInTheChannelAreNotified()
	{
		Dictionary<long, MentionPresence> online = new() { [10] = MentionPresence.Outside, [11] = MentionPresence.InChannel, [Author] = MentionPresence.InChannel };

		// 12 is offline: listed in mentions, never notified and nothing is kept for later
		long[] recipients = Recipients([10, 11, 12, Author], online, new MentionThrottle(new ManualClock()));

		Assert.Equal(new long[] { 10 }, recipients);
	}

	[Fact]
	public void UsersBlockedEitherWayAreNeverNotified()
	{
		Dictionary<long, MentionPresence> online = new() { [10] = MentionPresence.Outside, [11] = MentionPresence.Outside, [12] = MentionPresence.Outside };

		Assert.Equal(new long[] { 12 }, Recipients([10, 11, 12], online, new MentionThrottle(new ManualClock()), Channel, 10, 11));
	}

	[Fact]
	public async Task MentionCreateGoesThroughTheUserGroupOfEachRecipient()
	{
		List<(long UserID, string Event, object? Payload)> sent = new();
		Func<long, IRealtimeClient> userOf = id =>
		{
			IRealtimeClient client = DispatchProxy.Create<IRealtimeClient, Recorder>();
			((Recorder)(object)client).UserID = id;
			((Recorder)(object)client).Sent = sent;
			return client;
		};
		Message message = new Message { Id = "8813", ChannelId = "channel:3", Type = MessageType.Chat, Content = "@Bob#0042 we need a fourth", Mentions = [42], CreatedAt = DateTime.UtcNow };

		await MentionNotices.SendAsync(message, [42, 43], userOf);

		Assert.Equal(new[] { 42L, 43L }, sent.Select(s => s.UserID));
		Assert.All(sent, s => Assert.Equal(nameof(IRealtimeClient.MentionCreate), s.Event));
		MentionCreateEvent e = Assert.IsType<MentionCreateEvent>(sent[0].Payload);
		Assert.Equal("channel:3", e.ChannelId);
		Assert.Same(message, e.Message);
	}

	[Fact]
	public void AUserIsNotifiedAtMostOncePerChannelPer30Seconds()
	{
		ManualClock clock = new ManualClock();
		MentionThrottle throttle = new MentionThrottle(clock);
		Dictionary<long, MentionPresence> online = new() { [10] = MentionPresence.Outside };

		Assert.Equal(new long[] { 10 }, Recipients([10], online, throttle));
		clock.Advance(TimeSpan.FromSeconds(29));
		Assert.Empty(Recipients([10], online, throttle));
		// another channel counts apart
		Assert.Equal(new long[] { 10 }, Recipients([10], online, throttle, channel: 4));
		clock.Advance(TimeSpan.FromSeconds(1));
		Assert.Equal(new long[] { 10 }, Recipients([10], online, throttle));
	}

	[Fact]
	public void AnAuthorNotifiesAtMostFiveOutsidersPerMinute()
	{
		ManualClock clock = new ManualClock();
		MentionThrottle throttle = new MentionThrottle(clock);
		Dictionary<long, MentionPresence> online = Enumerable.Range(10, 8).ToDictionary(i => (long)i, _ => MentionPresence.Outside);

		Assert.Equal(new long[] { 10, 11, 12, 13, 14 }, Recipients([10, 11, 12, 13, 14, 15], online, throttle));
		clock.Advance(TimeSpan.FromSeconds(59));
		Assert.Empty(Recipients([16], online, throttle));
		clock.Advance(TimeSpan.FromSeconds(1));
		Assert.Equal(new long[] { 16, 17 }, Recipients([16, 17], online, throttle));
	}

	[Fact]
	public void ARecipientStillWithinItsWindowDoesNotSpendTheAuthorsBudget()
	{
		ManualClock clock = new ManualClock();
		MentionThrottle throttle = new MentionThrottle(clock);
		Dictionary<long, MentionPresence> online = Enumerable.Range(10, 6).ToDictionary(i => (long)i, _ => MentionPresence.Outside);

		Assert.Equal(new long[] { 10 }, Recipients([10], online, throttle));
		// 10 is still within its window and costs nothing, so four more fill the author's five per minute and 15 is left out
		Assert.Equal(new long[] { 11, 12, 13, 14 }, Recipients([10, 11, 12, 13, 14, 15], online, throttle));
	}

	private static async Task<long> AddUser(ProbeDatabase probe, string name)
	{
		await using AppDbContext db = probe.Open();
		User user = new User { Username = name, UsernameNormalized = NameNormalizer.Normalize(name), NameTag = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user.ID;
	}

	// The blocks come from BlockedEitherWayAsync, the database read the channel delivery uses
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ABlockInEitherDirectionStopsTheNotice(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		long author = await AddUser(probe, "Author");
		long blockedByAuthor = await AddUser(probe, "Blocked");
		long blockerOfAuthor = await AddUser(probe, "Blocker");
		long bystander = await AddUser(probe, "Bystander");

		HashSet<long> blocked;
		await using (AppDbContext db = probe.Open())
		{
			await Database.Social.AddBlock(db, author, blockedByAuthor);
			await Database.Social.AddBlock(db, blockerOfAuthor, author);
			blocked = await RealtimeHub.BlockedEitherWayAsync(db, author);
		}

		long[] recipients = MentionNotices.Recipients(author, Channel, [blockedByAuthor, blockerOfAuthor, bystander], _ => MentionPresence.Outside, blocked, new MentionThrottle(new ManualClock()));

		Assert.Equal(new[] { bystander }, recipients);
	}
}
