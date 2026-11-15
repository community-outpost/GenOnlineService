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

using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace GenOnlineService.Realtime
{
	// The social sets of lobby hosts without a session on this copy, as after a restore or a move, so the lobby list filter never depends on the host being connected here
	internal static class LobbyHosts
	{
		private static readonly ILogger s_log = AppLog.For(typeof(LobbyHosts));
		private static readonly ConcurrentDictionary<long, UserSocialContainer> s_hosts = new();
		private static readonly ConcurrentDictionary<long, byte> s_loading = new();

		public static UserSocialContainer? Of(long userID) => s_hosts.TryGetValue(userID, out UserSocialContainer? container) ? container : null;

		// The host's sets, null while those of a host without a session here load, after which the lobby is listed again
		public static UserSocialContainer? For(Lobby lobby)
		{
			UserSocialContainer? session = SocialState.Of(lobby.Owner);
			if (session != null)
			{
				// the session's sets follow every change, a kept copy of them would go stale
				s_hosts.TryRemove(lobby.Owner, out _);
				return session;
			}
			UserSocialContainer? container = Of(lobby.Owner);
			if (container == null)
			{
				Prefetch(lobby.Owner, lobby.LobbyID);
			}
			return container;
		}

		// Loads the sets of a host without a session here once, a lobby that is created, adopted or restored calls it for its host
		public static void Prefetch(long hostID, long lobbyID)
		{
			if (SocialState.Of(hostID) != null || s_hosts.ContainsKey(hostID) || !s_loading.TryAdd(hostID, 0))
			{
				return;
			}
			RealtimeOut.Fire(LoadAsync(hostID, lobbyID));
		}

		private static async Task LoadAsync(long hostID, long lobbyID)
		{
			try
			{
				IServiceProvider? services = ServiceLocator.Services;
				if (services == null)
				{
					return;
				}
				IDbContextFactory<AppDbContext> dbFactory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
				await LoadAsync(dbFactory, hostID);

				// lobbies whose host has a session again, or that left this copy, need no sets here
				HashSet<long> hosts = services.GetRequiredService<LobbyManager>().GetLobbies().Select(lobby => lobby.Owner).ToHashSet();
				foreach (long userID in s_hosts.Keys)
				{
					if (!hosts.Contains(userID) || SocialState.Of(userID) != null)
					{
						s_hosts.TryRemove(userID, out _);
					}
				}
				LobbyListPublisher.MarkDirty(lobbyID);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Loading the social sets of lobby host {UserId} failed", hostID);
				s_loading.TryRemove(hostID, out _);

				// the next list flush tries the load again, so the hidden lobby does not stay hidden
				LobbyListPublisher.MarkDirty(lobbyID);
			}
			finally
			{
				s_loading.TryRemove(hostID, out _);
			}
		}

		// Adds the sets only once read, a change that arrived during the read found no set to update, so the guarded reload reads again
		internal static async Task LoadAsync(IDbContextFactory<AppDbContext> dbFactory, long hostID)
		{
			long before = RealtimeBlocks.Local.Sequence;
			UserSocialContainer stored;
			await using (AppDbContext db = await dbFactory.CreateDbContextAsync())
			{
				stored = await BlockCaches.ReadAsync(db, hostID);
			}
			s_hosts[hostID] = stored;
			if (RealtimeBlocks.Local.Sequence != before)
			{
				await RealtimeBlocks.Local.ReloadAsync(dbFactory, hostID);
			}
		}

		internal static void Forget(long hostID) => s_hosts.TryRemove(hostID, out _);
	}
}
