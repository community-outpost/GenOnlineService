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
using GenOnlineService.Identity;
using GenOnlineService.Names;
using GenOnlineService.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net;
using Xunit;

namespace GenOnlineService.Tests;

// The Keycloak change log against a real database (GENONLINE_PROBE_POSTGRES / GENONLINE_PROBE_MARIADB): the rename
// answer racing the push of the same seq, and the log lines of a push, a pull and an outage
public class ChangeLogProbeTests
{
	private sealed class Factory : IDbContextFactory<AppDbContext>
	{
		private readonly ProbeDatabase m_probe;

		public Factory(ProbeDatabase probe)
		{
			m_probe = probe;
		}

		public AppDbContext CreateDbContext() => m_probe.Open();
	}

	internal static UpstreamProviderSettings Provider() => new()
	{
		Name = "keycloak",
		Enabled = true,
		Trusted = true,
		PlayerIDs = true,
		Issuer = "http://kc.test/realms/genonline",
		ChangeLog = new ChangeLogSettings { Enabled = true, ClientID = "genonline-sync", ClientSecret = "secret", PushSecret = new string('p', 32) }
	};

	private static async Task<User> AddUser(ProbeDatabase probe, string name, int tag)
	{
		await using AppDbContext db = probe.Open();
		var user = new User { Username = name, UsernameNormalized = NameNormalizer.Normalize(name), NameTag = tag, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user;
	}

	private static async Task SetLastSeq(ProbeDatabase probe, KeycloakChangeLog log, long seq)
	{
		await using AppDbContext db = probe.Open();
		await Database.Upstream.SetSetting(db, log.LastSeqKey, seq.ToString(System.Globalization.CultureInfo.InvariantCulture), "test");
	}

	// The plugin answers seq N to PATCH while its push of seq N arrives: exactly one history row, and last_seq reaches N
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task RenameAnswerAndPushOfTheSameSeqWriteOneHistoryRow(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Factory factory = new(probe);
		KeycloakChangeLog log = new(Provider(), factory);
		AccountsSettings accounts = RenameTests.Accounts(playerIDs: true, rename: true);

		for (int round = 1; round <= 40; round++)
		{
			User user = await AddUser(probe, "Alice" + round, 7);
			string renamed = "AliceRenamed" + round;
			await SetLastSeq(probe, log, round - 1);

			TaskCompletionSource answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
			FakeRenamePlugin plugin = new()
			{
				Rename = (_, _) =>
				{
					answered.TrySetResult();
					return Task.FromResult(FakeRenamePlugin.Json(200, $"{{\"player_id\":{user.ID},\"name\":\"{renamed}\",\"tag\":\"0007\",\"seq\":{round}}}"));
				}
			};
			PlayerNames names = new(factory, accounts, new KeycloakRenameClient(plugin, accounts), log);

			Task<Result<Empty>> patch = Task.Run(() => names.RenameSelfAsync(user.ID, renamed));
			Task push = Task.Run(async () =>
			{
				await answered.Task;
				await log.Receive(new ChangeLogEntry(round, user.ID, KeycloakChangeLog.KindName, renamed, "0007", null));
			});
			await Task.WhenAll(patch, push);

			Assert.True(patch.Result.Ok, patch.Result.Code);
			await using AppDbContext db = probe.Open();
			User row = await db.Users.SingleAsync(u => u.ID == user.ID);
			Assert.Equal((renamed, 7), (row.Username, row.NameTag));
			Assert.Equal(1, await db.UserUsernameHistory.CountAsync(h => h.UserID == user.ID));
			Assert.Equal(round, await log.ReadLastSeq());
		}
	}

	// The immediate apply never moves last_seq, and a second apply of the same answer writes nothing
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ImmediateApplyLeavesLastSeqAndRepeatsAsNothing(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Factory factory = new(probe);
		KeycloakChangeLog log = new(Provider(), factory);
		User user = await AddUser(probe, "Before", 7);
		await SetLastSeq(probe, log, 4);

		Assert.Single(await log.ApplyAnswer(user.ID, "After", "0007", "user:1"));
		Assert.Empty(await log.ApplyAnswer(user.ID, "After", "0007", "user:1"));
		Assert.Equal(4, await log.ReadLastSeq());
		await using AppDbContext db = probe.Open();
		Assert.Equal(1, await db.UserUsernameHistory.CountAsync(h => h.UserID == user.ID));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AppliedPushAndPullLogOneInformationLineEach(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User user = await AddUser(probe, "Before", 7);
		FakeChangeLogServer server = new()
		{
			Latest = "{\"latest_seq\":2,\"oldest_seq\":1}",
			Changes = $"{{\"entries\":[{{\"seq\":2,\"player_id\":{user.ID},\"kind\":\"name\",\"payload\":{{\"name\":\"Pulled\",\"tag\":\"0007\"}}}}],\"more\":false}}"
		};
		ListLogger logger = new();
		KeycloakChangeLog log = new(Provider(), new Factory(probe), server, logger);

		await log.Receive(new ChangeLogEntry(1, user.ID, KeycloakChangeLog.KindName, "Pushed", "0007", null));
		await log.Sync();

		Assert.Equal(2, await log.ReadLastSeq());
		Assert.Equal(new[]
		{
			(LogLevel.Information, $"Applied change log push seq 1 for player {user.ID}"),
			(LogLevel.Information, $"Applied change log pull seq 2 for player {user.ID}")
		}, logger.Entries.Select(e => (e.Level, e.Message)).ToArray());
		Assert.All(logger.Entries, e => Assert.Null(e.Exception));

		// an already applied push logs nothing
		await log.Receive(new ChangeLogEntry(2, user.ID, KeycloakChangeLog.KindName, "Pulled", "0007", null));
		Assert.Equal(2, logger.Entries.Count);
	}

	// Keycloak down: the pull throws the typed outage, the job logs one Warning without the exception, and nothing changes
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task PullWhileKeycloakIsDownLogsOneWarning(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		FakeChangeLogServer server = new() { Down = true };
		KeycloakChangeLog log = new(Provider(), new Factory(probe), server);
		ListLogger logger = new();

		await Program.RunObservedBackgroundJobAsync("keycloak_change_log", logger, () => log.Sync());

		var entry = Assert.Single(logger.Entries);
		Assert.Equal(LogLevel.Warning, entry.Level);
		Assert.Null(entry.Exception);
		Assert.StartsWith("Background job keycloak_change_log could not reach its upstream: ", entry.Message);
		Assert.Contains("ResponseEnded", entry.Message);
		Assert.Equal(0, await log.ReadLastSeq());

		// it keeps retrying: the next run asks again
		await Program.RunObservedBackgroundJobAsync("keycloak_change_log", logger, () => log.Sync());
		Assert.Equal(2, logger.Entries.Count);
		Assert.Equal(2, server.TokenRequests);
	}
}

// A fake Keycloak change log: a token, then the latest and changes pages the test sets, or a dropped connection
internal sealed class FakeChangeLogServer : HttpMessageHandler
{
	public bool Down { get; set; }
	public string Latest { get; set; } = "{\"latest_seq\":0,\"oldest_seq\":0}";
	public string Changes { get; set; } = "{\"entries\":[],\"more\":false}";
	public int TokenRequests;

	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		string url = request.RequestUri!.ToString();
		if (url.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal))
		{
			Interlocked.Increment(ref TokenRequests);
		}

		if (Down)
		{
			throw new HttpRequestException(HttpRequestError.ResponseEnded, "An error occurred while sending the request.", new IOException("The response ended prematurely. (ResponseEnded)"));
		}

		string body = url.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal) ? "{\"access_token\":\"token-1\",\"expires_in\":300}"
			: url.Contains("/changes/latest", StringComparison.Ordinal) ? Latest
			: Changes;
		return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
	}
}

// Records every log line with its level, rendered message and exception
internal sealed class ListLogger : ILogger
{
	public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		lock (Entries)
		{
			Entries.Add((logLevel, formatter(state, exception), exception));
		}
	}
}
