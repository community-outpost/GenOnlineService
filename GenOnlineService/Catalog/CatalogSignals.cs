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
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Text.Json;

namespace GenOnlineService.Catalog
{
	public static class CatalogResources
	{
		public const string Flags = "flags";
		public const string Playlists = "playlists";
	}

	public sealed record CatalogChange(string Resource, long Version);

	// Tells every copy that a resource staff edited changed, so each reloads it from the database.
	// The copy that saved reloads at once; others hear it on the Redis backplane, and the reconcile tick catches anything missed.
	public static class CatalogSignals
	{
		private static readonly ILogger s_log = AppLog.For(typeof(CatalogSignals));
		private static readonly RedisChannel s_channel = RedisChannel.Literal("realtime.catalog");
		private static readonly JsonSerializerOptions s_json = RealtimeJson.Create();
		private static readonly ConcurrentDictionary<string, Func<AppDbContext, Task>> s_reloaders = new(StringComparer.Ordinal);

		private static ISubscriber? s_subscriber;

		public static void Register(string resource, Func<AppDbContext, Task> reload) => s_reloaders[resource] = reload;

		internal static async Task StartAsync(ISubscriber subscriber)
		{
			ChannelMessageQueue queue = await subscriber.SubscribeAsync(s_channel);
			queue.OnMessage(message => HandleAsync(message.Message));
			Volatile.Write(ref s_subscriber, subscriber);
		}

		// After a commit: reload here, then ask the other copies to reload
		public static async Task ChangedAsync(AppDbContext db, string resource, long version)
		{
			await ReloadAsync(db, resource);

			ISubscriber? subscriber = Volatile.Read(ref s_subscriber);
			if (subscriber == null)
			{
				return;
			}
			try
			{
				await subscriber.PublishAsync(s_channel, JsonSerializer.Serialize(new CatalogChange(resource, version), s_json));
			}
			catch (Exception ex)
			{
				// the reconcile tick reloads the other copies
				s_log.LogWarning(ex, "Announcing a {Resource} change to the other copies failed", resource);
			}
		}

		public static async Task ReloadAllAsync(AppDbContext db)
		{
			foreach (string resource in s_reloaders.Keys)
			{
				await ReloadAsync(db, resource);
			}
		}

		private static async Task ReloadAsync(AppDbContext db, string resource)
		{
			if (s_reloaders.TryGetValue(resource, out Func<AppDbContext, Task>? reload))
			{
				await reload(db);
			}
		}

		private static async Task HandleAsync(RedisValue payload)
		{
			try
			{
				CatalogChange? change = JsonSerializer.Deserialize<CatalogChange>(payload.ToString(), s_json);
				if (change == null || ServiceLocator.Services == null)
				{
					return;
				}
				IDbContextFactory<AppDbContext> factory = ServiceLocator.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
				await using AppDbContext db = await factory.CreateDbContextAsync();
				await ReloadAsync(db, change.Resource);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Reloading a catalog change from another copy failed");
			}
		}
	}
}
