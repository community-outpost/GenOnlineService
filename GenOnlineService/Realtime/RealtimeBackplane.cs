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

using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Text.Json;

namespace GenOnlineService.Realtime
{
	// Realtime:backplane none (one copy, the default) or redis, which carries hub messages, chat fan-out and close requests between copies
	public sealed class RealtimeBackplaneSettings
	{
		public const string None = "none";
		public const string Redis = "redis";

		public string Backplane { get; private init; } = None;
		public string? RedisConnection { get; private init; }
		public string ChannelPrefix { get; private init; } = String.Empty;

		public bool UsesRedis => Backplane == Redis;

		// Refuses an unknown backplane, redis without a connection string, and redis on MySQL or MariaDB at startup
		public static RealtimeBackplaneSettings Read(IConfiguration configuration)
		{
			string backplane = (configuration["Realtime:backplane"] ?? String.Empty).Trim().ToLowerInvariant();
			if (backplane.Length == 0)
			{
				backplane = None;
			}
			if (backplane != None && backplane != Redis)
			{
				throw new InvalidOperationException($"Realtime:backplane is '{backplane}', expected '{None}' or '{Redis}'.");
			}

			// every multi copy feature needs PostgreSQL, MySQL and MariaDB run one copy with the none backplane
			if (backplane == Redis && !Program.UsesPostgres(configuration))
			{
				throw new InvalidOperationException("Realtime:backplane is 'redis', but more than one service copy is supported on PostgreSQL only. Set Database:db_provider to postgres or Realtime:backplane to 'none'.");
			}

			string? connection = configuration["Realtime:redis"];
			if (backplane == Redis && String.IsNullOrWhiteSpace(connection))
			{
				throw new InvalidOperationException("Realtime:backplane is 'redis' but Realtime:redis has no connection string.");
			}

			return new RealtimeBackplaneSettings
			{
				Backplane = backplane,
				RedisConnection = backplane == Redis ? connection : null,
				ChannelPrefix = configuration["Realtime:redis_channel_prefix"]?.Trim() ?? String.Empty
			};
		}

		// The Redis options both the SignalR backplane and the close requests connect with
		public ConfigurationOptions RedisOptions()
		{
			ConfigurationOptions options = ConfigurationOptions.Parse(RedisConnection!);
			if (ChannelPrefix.Length > 0)
			{
				options.ChannelPrefix = RedisChannel.Literal(ChannelPrefix);
			}
			return options;
		}

		public void ConfigureSignalR(RedisOptions options)
		{
			options.Configuration = RedisOptions();
			options.ConnectionFactory = writer => RealtimeRedis.ConnectAsync(this, writer);
		}

		// The delivery manager RealtimeLifetimeManager wraps, in memory for one copy and Redis for several
		public HubLifetimeManager<RealtimeHub> CreateLifetimeManager(IServiceProvider services)
		{
			if (!UsesRedis)
			{
				return new DefaultHubLifetimeManager<RealtimeHub>(services.GetRequiredService<ILogger<DefaultHubLifetimeManager<RealtimeHub>>>());
			}

			return new RedisHubLifetimeManager<RealtimeHub>(
				services.GetRequiredService<ILogger<RedisHubLifetimeManager<RealtimeHub>>>(),
				services.GetRequiredService<IOptions<RedisOptions>>(),
				services.GetRequiredService<IHubProtocolResolver>(),
				services.GetRequiredService<IOptions<HubOptions>>(),
				services.GetRequiredService<IOptions<HubOptions<RealtimeHub>>>());
		}
	}

	// The one Redis connection of this copy, shared by the SignalR backplane and RealtimeControl
	internal static class RealtimeRedis
	{
		private static readonly SemaphoreSlim s_lock = new(1, 1);
		private static IConnectionMultiplexer? s_connection;

		public static async Task<IConnectionMultiplexer> ConnectAsync(RealtimeBackplaneSettings settings, TextWriter? log = null)
		{
			IConnectionMultiplexer? connection = Volatile.Read(ref s_connection);
			if (connection != null)
			{
				return connection;
			}

			await s_lock.WaitAsync();
			try
			{
				// a failed attempt caches nothing, so the next caller tries again
				s_connection ??= await ConnectionMultiplexer.ConnectAsync(settings.RedisOptions(), log);
				return s_connection;
			}
			finally
			{
				s_lock.Release();
			}
		}
	}

	// Closes a user's or a sign-in's connections with a close code from section 7 and an optional moderation notice first
	public sealed record CloseRequest
	{
		public string Id { get; init; } = Guid.NewGuid().ToString("N");
		public string Origin { get; init; } = String.Empty;
		public long UserId { get; init; }
		public EUserSessionType? SessionType { get; init; }
		public string? AuthorizationId { get; init; }
		public string Code { get; init; } = String.Empty;
		public bool AllowReconnect { get; init; }
		public ModerationNoticeEvent? Notice { get; init; }
		public bool WantsAck { get; init; }

		// The user's sessions, all or one session type
		public static CloseRequest ForUser(Int64 userID, string code, bool allowReconnect, ModerationNoticeEvent? notice = null, EUserSessionType? sessionType = null) =>
			new CloseRequest { UserId = userID, SessionType = sessionType, Code = code, AllowReconnect = allowReconnect, Notice = notice };

		// Only the sessions opened with that sign-in, which never reconnect
		public static CloseRequest ForSignIn(string authorizationID, string code) =>
			new CloseRequest { AuthorizationId = authorizationID, Code = code };
	}

	// Runs close requests on this copy and, with the Redis backplane, publishes them so every other copy closes its own matching sessions
	public static class RealtimeControl
	{
		private static readonly ILogger s_log = AppLog.For(typeof(RealtimeControl));
		private static readonly JsonSerializerOptions s_json = RealtimeJson.Create();
		private static readonly string s_copyID = GenOnlineService.Failover.CopyIdentity.ID;
		private static readonly RedisChannel s_closeChannel = RedisChannel.Literal("realtime.close");
		private static readonly RedisChannel s_ackChannel = RedisChannel.Literal("realtime.close.ack");
		private static readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> s_pendingAcks = new(StringComparer.Ordinal);

		// How long a request that needs a remote confirmation waits for another copy
		private static readonly TimeSpan s_ackTimeout = TimeSpan.FromSeconds(2);

		private static ISubscriber? s_subscriber;

		// True when other copies may hold sessions this copy does not know
		public static bool IsShared => Volatile.Read(ref s_subscriber) != null;

		public static async Task StartAsync(RealtimeBackplaneSettings settings)
		{
			if (!settings.UsesRedis)
			{
				return;
			}

			IConnectionMultiplexer connection;
			try
			{
				connection = await RealtimeRedis.ConnectAsync(settings);
			}
			catch (Exception ex)
			{
				throw new InvalidOperationException("Realtime:backplane is 'redis' but the Redis server in Realtime:redis could not be reached.", ex);
			}

			ISubscriber subscriber = connection.GetSubscriber();
			ChannelMessageQueue requests = await subscriber.SubscribeAsync(s_closeChannel);
			requests.OnMessage(message => HandleRequestAsync(message.Message));
			await subscriber.SubscribeAsync(s_ackChannel, (_, value) =>
			{
				if (s_pendingAcks.TryRemove(value.ToString(), out TaskCompletionSource<bool>? pending))
				{
					pending.TrySetResult(true);
				}
			});

			await RealtimeFanout.StartAsync(subscriber);
			await RealtimeBlocks.StartAsync(subscriber);
			await GenOnlineService.Catalog.CatalogSignals.StartAsync(subscriber);

			Volatile.Write(ref s_subscriber, subscriber);
			s_log.LogInformation("Realtime backplane redis started on copy {CopyId}", s_copyID);
		}

		// Closes the matching sessions here first, keeping the single copy order, then on every other copy; true when a copy had one
		public static async Task<bool> RequestClose(CloseRequest request, bool confirmRemote = false)
		{
			bool closedHere = await CloseLocalAsync(request) > 0;

			ISubscriber? subscriber = Volatile.Read(ref s_subscriber);
			if (subscriber == null)
			{
				return closedHere;
			}

			// only a request nobody here matched waits for another copy to confirm
			bool wait = confirmRemote && !closedHere;
			CloseRequest published = request with { Origin = s_copyID, WantsAck = wait };
			TaskCompletionSource<bool>? ack = null;
			if (wait)
			{
				ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				s_pendingAcks[published.Id] = ack;
			}

			try
			{
				await subscriber.PublishAsync(s_closeChannel, JsonSerializer.Serialize(published, s_json));
				if (ack == null)
				{
					return closedHere;
				}

				Task done = await Task.WhenAny(ack.Task, Task.Delay(s_ackTimeout));
				return done == ack.Task;
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Publishing a close request for user {UserId} failed, only this copy closed its sessions", request.UserId);
				return closedHere;
			}
			finally
			{
				s_pendingAcks.TryRemove(published.Id, out _);
			}
		}

		private static Task<int> CloseLocalAsync(CloseRequest request)
		{
			return request.AuthorizationId != null
				? WebSocketManager.DisconnectSignIn(request.AuthorizationId, request.Code)
				: WebSocketManager.DisconnectUser(request.UserId, request.Code, request.AllowReconnect, request.Notice, request.SessionType);
		}

		// Requests arrive in publish order and run one at a time
		private static async Task HandleRequestAsync(RedisValue payload)
		{
			try
			{
				CloseRequest? request = JsonSerializer.Deserialize<CloseRequest>(payload.ToString(), s_json);
				if (request == null || request.Origin == s_copyID)
				{
					return;
				}

				int closed = await CloseLocalAsync(request);
				ISubscriber? subscriber = Volatile.Read(ref s_subscriber);
				if (closed > 0 && request.WantsAck && subscriber != null)
				{
					await subscriber.PublishAsync(s_ackChannel, request.Id);
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Handling a close request from another copy failed");
			}
		}
	}
}
