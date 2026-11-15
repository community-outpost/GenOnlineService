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
using GenOnlineService.Names;
using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

// Channel and lobby chat with the Redis backplane: each copy filters its own recipients by the users blocked either way with the author
public class ChatFanoutTests
{
	private const long Author = 10;

	private static Message Line(int index) => new Message
	{
		Id = (1000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
		ChannelId = "channel:5",
		Type = MessageType.Chat,
		Author = new Member { Id = Author, Name = "Bob", Tag = "#0001", Badges = Array.Empty<string>() },
		Content = "line " + index,
		CreatedAt = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc).AddSeconds(index)
	};

	[Fact]
	public void RecipientsLeaveOutEveryConnectionOfASkippedUserAndKeepTheOthers()
	{
		LocalChatGroups groups = new();
		groups.Add("a", Author, "channel:5");
		groups.Add("b1", 11, "channel:5");
		groups.Add("b2", 11, "channel:5");
		groups.Add("c", 12, "channel:5");
		groups.Add("d", 13, "channel:5");

		// 11 blocked the author and the author blocked 12, the author still gets the echo
		string[] recipients = groups.Recipients("channel:5", new HashSet<long> { 11, 12 });

		Assert.Equal(new[] { "a", "d" }, recipients.OrderBy(id => id, StringComparer.Ordinal));
		Assert.Equal(new[] { "a", "b1", "b2", "c", "d" }, groups.Recipients("channel:5", new HashSet<long>()).OrderBy(id => id, StringComparer.Ordinal));
	}

	[Fact]
	public void OnlyChannelAndLobbyGroupsAreMirroredAndEachGroupStaysApart()
	{
		LocalChatGroups groups = new();
		groups.Add("a", 1, "user:1");
		groups.Add("a", 1, RealtimeGroups.LobbyList);
		groups.Add("a", 1, "lobby:7");
		groups.Add("b", 2, "channel:7");

		Assert.Empty(groups.Recipients("user:1", new HashSet<long>()));
		Assert.Empty(groups.Recipients(RealtimeGroups.LobbyList, new HashSet<long>()));
		Assert.Equal(new[] { "a" }, groups.Recipients("lobby:7", new HashSet<long>()));
		Assert.Equal(new[] { "b" }, groups.Recipients("channel:7", new HashSet<long>()));
	}

	[Fact]
	public void ALeftOrDisconnectedConnectionGetsNothingMore()
	{
		LocalChatGroups groups = new();
		groups.Add("a", 1, "channel:5");
		groups.Add("a", 1, "lobby:9");
		groups.Add("b", 2, "channel:5");

		groups.Remove("b", "channel:5");
		Assert.Equal(new[] { "a" }, groups.Recipients("channel:5", new HashSet<long>()));

		groups.RemoveConnection("a");
		Assert.Empty(groups.Recipients("channel:5", new HashSet<long>()));
		Assert.Empty(groups.Recipients("lobby:9", new HashSet<long>()));

		// a connection that joins again is a member again
		groups.Add("a", 1, "channel:5");
		Assert.Equal(new[] { "a" }, groups.Recipients("channel:5", new HashSet<long>()));
	}

	[Fact]
	public void TheEnvelopeCarriesTheMessageAndTheSkippedUsersUnchanged()
	{
		ChatFanoutEnvelope sent = new ChatFanoutEnvelope { Group = "lobby:3", Skipped = new long[] { 11, 12 }, Message = Line(1) with { ChannelId = "lobby:3", Type = MessageType.Announcement } };

		ChatFanoutEnvelope? received = ChatFanout.Deserialize(ChatFanout.Serialize(sent));

		Assert.NotNull(received);
		Assert.Equal("lobby:3", received!.Group);
		Assert.Equal(new long[] { 11, 12 }, received.Skipped);
		JsonSerializerOptions wire = RealtimeJson.Create();
		Assert.Equal(JsonSerializer.Serialize(sent.Message, wire), JsonSerializer.Serialize(received.Message, wire));
	}

	// Two copies on one Redis: the author and a user the author blocked on copy 1, a user who blocked the author, a bystander and the author's second connection on copy 2
	[ProbeRedisFact]
	public async Task EveryCopyDeliversInPublishOrderAndLeavesOutItsOwnBlockedMembers()
	{
		string connection = Environment.GetEnvironmentVariable(ProbeRedisFactAttribute.Variable)!;
		IConnectionMultiplexer copy1 = await ConnectionMultiplexer.ConnectAsync(connection);
		IConnectionMultiplexer copy2 = await ConnectionMultiplexer.ConnectAsync(connection);
		RedisChannel channel = RedisChannel.Literal("probe-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ":realtime.chat");
		try
		{
			LocalChatGroups groups1 = new();
			groups1.Add("author-1", Author, "channel:5");
			groups1.Add("blocked-by-author", 11, "channel:5");
			LocalChatGroups groups2 = new();
			groups2.Add("blocker", 12, "channel:5");
			groups2.Add("bystander", 13, "channel:5");
			groups2.Add("author-2", Author, "channel:5");
			groups2.Add("other-channel", 14, "channel:6");

			ConcurrentQueue<(string Connection, string Content)> written1 = new();
			ConcurrentQueue<(string Connection, string Content)> written2 = new();
			Func<ChatFanoutEnvelope, Task> Writer(LocalChatGroups groups, ConcurrentQueue<(string, string)> written) => envelope =>
			{
				foreach (string id in groups.Recipients(envelope.Group, envelope.Skipped).OrderBy(id => id, StringComparer.Ordinal))
				{
					written.Enqueue((id, envelope.Message.Content!));
				}
				return Task.CompletedTask;
			};

			ChatFanout fanout1 = await ChatFanout.StartAsync(copy1.GetSubscriber(), channel, Writer(groups1, written1));
			await ChatFanout.StartAsync(copy2.GetSubscriber(), channel, Writer(groups2, written2));

			const int count = 40;
			long[] skipped = { 11, 12 };
			for (int i = 0; i < count; i++)
			{
				await fanout1.PublishAsync(new ChatFanoutEnvelope { Group = "channel:5", Skipped = skipped, Message = Line(i) });
			}

			DateTime deadline = DateTime.UtcNow.AddSeconds(5);
			while ((written1.Count < count || written2.Count < 2 * count) && DateTime.UtcNow < deadline)
			{
				await Task.Delay(20);
			}

			string[] lines = Enumerable.Range(0, count).Select(i => "line " + i).ToArray();
			Assert.Equal(lines, written1.Select(w => w.Content));
			Assert.All(written1, w => Assert.Equal("author-1", w.Connection));
			Assert.Equal(lines, written2.Where(w => w.Connection == "bystander").Select(w => w.Content));
			Assert.Equal(lines, written2.Where(w => w.Connection == "author-2").Select(w => w.Content));
			Assert.Equal(2 * count, written2.Count);
		}
		finally
		{
			await copy1.CloseAsync();
			await copy2.CloseAsync();
		}
	}

	private static HubConnectionContext Connected(RealtimeLifetimeManager manager, long userID)
	{
		DefaultConnectionContext transport = new DefaultConnectionContext("f5-" + Guid.NewGuid().ToString("N"));
		HubConnectionContext connection = new HubConnectionContext(transport, new HubConnectionContextOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), ClientTimeoutInterval = TimeSpan.FromSeconds(30) }, NullLoggerFactory.Instance);
		connection.UserIdentifier = userID.ToString(System.Globalization.CultureInfo.InvariantCulture);
		manager.OnConnectedAsync(connection).GetAwaiter().GetResult();
		return connection;
	}

	private static RealtimeLifetimeManager Manager(LocalChatGroups groups, ConnectionOutboxes outboxes) =>
		new RealtimeLifetimeManager(new DefaultHubLifetimeManager<RealtimeHub>(NullLogger<DefaultHubLifetimeManager<RealtimeHub>>.Instance), groups, outboxes);

	private static async Task Until(Func<bool> done, int seconds = 3)
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
		while (!done() && DateTime.UtcNow < deadline)
		{
			await Task.Delay(10);
		}
	}

	[Fact]
	public async Task TheMirrorFollowsAddAndRemoveOfLocalConnectionsOnly()
	{
		LocalChatGroups groups = new();
		RealtimeLifetimeManager manager = Manager(groups, new ConnectionOutboxes(8));
		HubConnectionContext a = Connected(manager, 1);

		await manager.AddToGroupAsync(a.ConnectionId, "channel:5");
		await manager.AddToGroupAsync(a.ConnectionId, RealtimeGroups.LobbyList);
		await manager.AddToGroupAsync("f5-not-on-this-copy", "channel:5");
		Assert.Equal(new[] { a.ConnectionId }, groups.Recipients("channel:5", Array.Empty<long>()));
		Assert.Empty(groups.Recipients(RealtimeGroups.LobbyList, Array.Empty<long>()));

		await manager.RemoveFromGroupAsync(a.ConnectionId, "channel:5");
		Assert.Empty(groups.Recipients("channel:5", Array.Empty<long>()));
		await manager.OnDisconnectedAsync(a);
	}

	[Fact]
	public async Task ADisconnectClearsTheMirrorAndTheOutbox()
	{
		LocalChatGroups groups = new();
		ConnectionOutboxes outboxes = new(8);
		RealtimeLifetimeManager manager = Manager(groups, outboxes);
		HubConnectionContext a = Connected(manager, 1);
		await manager.AddToGroupAsync(a.ConnectionId, "channel:5");
		await manager.AddToGroupAsync(a.ConnectionId, "lobby:9");
		Assert.Equal(1, RealtimeLifetimeManager.WriteLocal(groups, outboxes, "lobby:9", "MessageCreate", Line(1), Array.Empty<long>(), (_, _) => ValueTask.CompletedTask));
		Assert.Equal(1, outboxes.Count);

		await manager.OnDisconnectedAsync(a);

		Assert.Empty(groups.Recipients("channel:5", Array.Empty<long>()));
		Assert.Empty(groups.Recipients("lobby:9", Array.Empty<long>()));
		Assert.Equal(0, outboxes.Count);
	}

	[Fact]
	public async Task WriteLocalSerializesOnceSkipsBlockedUsersAndDropsStaleEntries()
	{
		LocalChatGroups groups = new();
		ConnectionOutboxes outboxes = new(8);
		RealtimeLifetimeManager manager = Manager(groups, outboxes);
		HubConnectionContext a = Connected(manager, 1);
		HubConnectionContext b = Connected(manager, 2);
		HubConnectionContext blocked = Connected(manager, 3);
		foreach (HubConnectionContext connection in new[] { a, b, blocked })
		{
			await manager.AddToGroupAsync(connection.ConnectionId, "channel:9");
		}
		groups.Add("f5-stale", 4, "channel:9");

		ConcurrentQueue<(string Connection, SerializedHubMessage Message)> written = new();
		int queued = RealtimeLifetimeManager.WriteLocal(groups, outboxes, "channel:9", "MessageCreate", Line(1), new long[] { 3 }, (connection, message) =>
		{
			written.Enqueue((connection.ConnectionId, message));
			return ValueTask.CompletedTask;
		});
		await Until(() => written.Count >= 2);

		Assert.Equal(2, queued);
		Assert.Equal(new[] { a.ConnectionId, b.ConnectionId }.OrderBy(id => id, StringComparer.Ordinal), written.Select(w => w.Connection).OrderBy(id => id, StringComparer.Ordinal));
		Assert.Same(written.First().Message, written.Last().Message);
		Assert.DoesNotContain("f5-stale", groups.Recipients("channel:9", Array.Empty<long>()));
		foreach (HubConnectionContext connection in new[] { a, b, blocked })
		{
			await manager.OnDisconnectedAsync(connection);
		}
	}

	[Fact]
	public async Task AStalledConnectionHoldsUpNeitherTheOtherMembersNorOtherGroups()
	{
		LocalChatGroups groups = new();
		ConnectionOutboxes outboxes = new(RealtimeFanout.OutboxLimit);
		RealtimeLifetimeManager manager = Manager(groups, outboxes);
		HubConnectionContext slow = Connected(manager, 1);
		HubConnectionContext fast = Connected(manager, 2);
		HubConnectionContext other = Connected(manager, 3);
		await manager.AddToGroupAsync(slow.ConnectionId, "channel:1");
		await manager.AddToGroupAsync(fast.ConnectionId, "channel:1");
		await manager.AddToGroupAsync(slow.ConnectionId, "lobby:2");
		await manager.AddToGroupAsync(other.ConnectionId, "lobby:2");

		TaskCompletionSource stall = new(TaskCreationOptions.RunContinuationsAsynchronously);
		ConcurrentDictionary<string, ConcurrentQueue<string>> written = new();
		Func<HubConnectionContext, SerializedHubMessage, ValueTask> write = (connection, message) =>
		{
			if (connection == slow)
			{
				return new ValueTask(stall.Task);
			}
			written.GetOrAdd(connection.ConnectionId, _ => new()).Enqueue(((Message)((InvocationMessage)message.Message!).Arguments[0]!).Content!);
			return ValueTask.CompletedTask;
		};

		const int count = 30;
		for (int i = 0; i < count; i++)
		{
			RealtimeLifetimeManager.WriteLocal(groups, outboxes, "channel:1", "MessageCreate", Line(i), Array.Empty<long>(), write);
			RealtimeLifetimeManager.WriteLocal(groups, outboxes, "lobby:2", "MessageCreate", Line(100 + i), Array.Empty<long>(), write);
		}
		await Until(() => written.TryGetValue(fast.ConnectionId, out var f) && f.Count == count && written.TryGetValue(other.ConnectionId, out var o) && o.Count == count);

		Assert.Equal(Enumerable.Range(0, count).Select(i => "line " + i), written[fast.ConnectionId]);
		Assert.Equal(Enumerable.Range(100, count).Select(i => "line " + i), written[other.ConnectionId]);
		Assert.False(written.ContainsKey(slow.ConnectionId));

		stall.SetResult();
		foreach (HubConnectionContext connection in new[] { slow, fast, other })
		{
			await manager.OnDisconnectedAsync(connection);
		}
	}

	[Fact]
	public async Task EachConnectionGetsTheMessagesOfAGroupInPublishOrder()
	{
		ConnectionOutboxes outboxes = new(RealtimeFanout.OutboxLimit);
		ConcurrentDictionary<string, ConcurrentQueue<int>> written = new();
		string[] connections = { "f5-order-a", "f5-order-b", "f5-order-c" };
		const int count = 200;
		for (int i = 0; i < count; i++)
		{
			int index = i;
			foreach (string connection in connections)
			{
				// writes finish at uneven times, as flushes to real sockets do
				Assert.True(outboxes.Enqueue(connection, async () =>
				{
					if (index % 7 == 0) await Task.Delay(1);
					else await Task.Yield();
					written.GetOrAdd(connection, _ => new()).Enqueue(index);
				}, () => throw new InvalidOperationException("no connection falls behind here")));
			}
		}
		await Until(() => connections.All(c => written.TryGetValue(c, out var q) && q.Count == count), 10);

		foreach (string connection in connections)
		{
			Assert.Equal(Enumerable.Range(0, count), written[connection]);
			outboxes.Remove(connection);
		}
	}

	[Fact]
	public async Task AConnectionPastTheLimitIsAbortedOnceAndTakesNoMoreWrites()
	{
		ConnectionOutboxes outboxes = new(4);
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource stall = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int aborts = 0;
		Action abort = () => Interlocked.Increment(ref aborts);

		Assert.True(outboxes.Enqueue("f5-limit", () => { started.TrySetResult(); return new ValueTask(stall.Task); }, abort));
		await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
		for (int i = 0; i < 4; i++)
		{
			Assert.True(outboxes.Enqueue("f5-limit", () => ValueTask.CompletedTask, abort));
		}

		Assert.False(outboxes.Enqueue("f5-limit", () => ValueTask.CompletedTask, abort));
		Assert.False(outboxes.Enqueue("f5-limit", () => ValueTask.CompletedTask, abort));
		Assert.Equal(1, aborts);

		// another connection is not affected
		Assert.True(outboxes.Enqueue("f5-limit-other", () => ValueTask.CompletedTask, abort));
		stall.SetResult();
		outboxes.Remove("f5-limit");
		outboxes.Remove("f5-limit-other");
	}

	private sealed class Factory : IDbContextFactory<AppDbContext>
	{
		private readonly ProbeDatabase m_probe;

		public Factory(ProbeDatabase probe)
		{
			m_probe = probe;
		}

		public AppDbContext CreateDbContext() => m_probe.Open();
	}

	private static async Task<long> AddUser(ProbeDatabase probe, string name)
	{
		await using AppDbContext db = probe.Open();
		var user = new User { Username = name, UsernameNormalized = NameNormalizer.Normalize(name), NameTag = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user.ID;
	}

	private static async Task<long[]> SkippedFor(ProbeDatabase probe, long author)
	{
		await using AppDbContext db = probe.Open();
		return (await RealtimeHub.BlockedEitherWayAsync(db, author)).OrderBy(id => id).ToArray();
	}

	// The blocks run through SocialActions on a copy where none of the users is online, as a REST call that reaches another copy than the hub connection
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheSkippedUsersHoldBothDirectionsAndFollowABlockMadeOnAnotherCopy(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Factory factory = new Factory(probe);

		long author = await AddUser(probe, "FanAuthor");
		long blockedByAuthor = await AddUser(probe, "FanBlocked");
		long blocker = await AddUser(probe, "FanBlocker");
		long bystander = await AddUser(probe, "FanBystander");
		Assert.Null(SocialState.Of(author));

		Assert.Empty(await SkippedFor(probe, author));
		Assert.True((await SocialActions.BlockAsync(factory, author, blockedByAuthor)).Ok);
		Assert.True((await SocialActions.BlockAsync(factory, blocker, author)).Ok);

		Assert.Equal(new[] { blockedByAuthor, blocker }.OrderBy(id => id), await SkippedFor(probe, author));
		Assert.Equal(new[] { author }, await SkippedFor(probe, blocker));
		Assert.Empty(await SkippedFor(probe, bystander));

		// an unblock counts from the next message on
		Assert.True((await SocialActions.UnblockAsync(factory, blocker, author)).Ok);
		Assert.Equal(new[] { blockedByAuthor }, await SkippedFor(probe, author));
	}
}
