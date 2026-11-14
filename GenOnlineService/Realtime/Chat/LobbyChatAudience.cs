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
	// Users who were members of a lobby when its chat was sent, so a member who left can still report a lobby message
	// Each lobby is kept 24 h after its last message and pruned when the map is touched
	internal static class LobbyChatAudience
	{
		public static readonly TimeSpan Retention = TimeSpan.FromHours(24);
		private static readonly TimeSpan s_pruneInterval = TimeSpan.FromMinutes(10);

		private sealed class Entry
		{
			public readonly HashSet<long> Members = new();
			public DateTime LastSeen;
		}

		private static readonly ConcurrentDictionary<long, Entry> s_lobbies = new();
		private static long s_nextPruneTicks;

		public static void Record(long lobbyID, IEnumerable<long> members)
		{
			DateTime now = DateTime.UtcNow;
			Entry entry = s_lobbies.GetOrAdd(lobbyID, _ => new Entry());
			lock (entry)
			{
				entry.Members.UnionWith(members);
				entry.LastSeen = now;
			}
			Prune(now);
		}

		public static bool WasMember(long lobbyID, long userID)
		{
			Prune(DateTime.UtcNow);
			if (!s_lobbies.TryGetValue(lobbyID, out Entry? entry))
			{
				return false;
			}

			lock (entry)
			{
				return entry.Members.Contains(userID);
			}
		}

		private static void Prune(DateTime now)
		{
			long next = Interlocked.Read(ref s_nextPruneTicks);
			if (now.Ticks < next || Interlocked.CompareExchange(ref s_nextPruneTicks, (now + s_pruneInterval).Ticks, next) != next)
			{
				return;
			}

			foreach ((long lobbyID, Entry entry) in s_lobbies)
			{
				bool expired;
				lock (entry)
				{
					expired = now - entry.LastSeen > Retention;
				}
				if (expired)
				{
					s_lobbies.TryRemove(lobbyID, out _);
				}
			}
		}
	}
}
