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

using System.Collections.Concurrent;

namespace GenOnlineService.Realtime
{
	// Lobby list watchers of section 6.3, each sees its own filtered list and latency so sends go per session
	internal static class LobbyListPublisher
	{
		private sealed class Watcher
		{
			public Watcher(string connectionID, UserSession session)
			{
				ConnectionID = connectionID;
				Session = session;
			}

			public string ConnectionID { get; }
			public UserSession Session { get; }
			public HashSet<Int64> Visible { get; } = new();
			public object Lock { get; } = new();
		}

		private static readonly ConcurrentDictionary<string, Watcher> s_watchers = new(StringComparer.Ordinal);
		private static readonly ConcurrentDictionary<Int64, byte> s_dirtyLobbies = new();

		// Changes coalesce between flushes, so a busy lobby costs one send per watcher per interval
		private const long FlushIntervalMs = 250;
		private static long s_lastFlushMs = 0;

		public static IReadOnlyList<LobbySummary> Watch(LobbyManager lobbyManager, string connectionID, UserSession viewer)
		{
			Watcher watcher = new Watcher(connectionID, viewer);
			lock (watcher.Lock)
			{
				// registered before the snapshot so a change racing it is flushed afterwards
				s_watchers[connectionID] = watcher;
				List<LobbySummary> lobbies = new();
				foreach (Lobby lobby in lobbyManager.GetLobbies())
				{
					if (RealtimeLobbies.IsListedFor(lobby, viewer))
					{
						lobbies.Add(RealtimeLobbies.SummaryOf(lobby, viewer));
						watcher.Visible.Add(lobby.LobbyID);
					}
				}
				return lobbies.OrderBy(lobby => lobby.Id).ToArray();
			}
		}

		public static void Unwatch(string connectionID)
		{
			s_watchers.TryRemove(connectionID, out _);
		}

		public static void MarkDirty(Int64 lobbyID)
		{
			s_dirtyLobbies[lobbyID] = 0;
		}

		public static void Flush(LobbyManager lobbyManager)
		{
			long nowMs = Environment.TickCount64;
			if (nowMs - Volatile.Read(ref s_lastFlushMs) < FlushIntervalMs || s_dirtyLobbies.IsEmpty)
			{
				return;
			}
			Volatile.Write(ref s_lastFlushMs, nowMs);

			List<Int64> dirty = new();
			foreach (Int64 lobbyID in s_dirtyLobbies.Keys)
			{
				if (s_dirtyLobbies.TryRemove(lobbyID, out _))
				{
					dirty.Add(lobbyID);
				}
			}

			foreach (Watcher watcher in s_watchers.Values)
			{
				// a watcher ends with its connection, the hub only learns of it here
				if (RealtimeLobbies.ConnectionOf(watcher.Session) != watcher.ConnectionID)
				{
					s_watchers.TryRemove(new KeyValuePair<string, Watcher>(watcher.ConnectionID, watcher));
					continue;
				}

				// the watcher's connection is the session's current one, reached through its session group
				IRealtimeClient client = RealtimeOut.Session(watcher.Session);
				lock (watcher.Lock)
				{
					foreach (Int64 lobbyID in dirty)
					{
						Lobby? lobby = lobbyManager.GetLobby(lobbyID);
						if (lobby != null && RealtimeLobbies.IsListedFor(lobby, watcher.Session))
						{
							watcher.Visible.Add(lobbyID);
							RealtimeOut.Fire(client.LobbyListUpsert(new LobbyListUpsertEvent { Lobby = RealtimeLobbies.SummaryOf(lobby, watcher.Session) }));
						}
						else if (watcher.Visible.Remove(lobbyID))
						{
							RealtimeOut.Fire(client.LobbyListRemove(new LobbyIdEvent { LobbyId = lobbyID }));
						}
					}
				}
			}
		}
	}
}
