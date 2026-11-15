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
	// POST /api/v1/reports allows 10 reports per user in any 60 s window, message and player reports together
	internal static class ReportRateLimiter
	{
		public const int Reports = 10;
		public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

		private static readonly TimeSpan s_pruneInterval = TimeSpan.FromMinutes(5);
		private static readonly ConcurrentDictionary<long, Queue<DateTime>> s_recent = new();
		private static long s_nextPruneTicks;

		public static bool TryConsume(long userID, out long retryAfterMs)
		{
			DateTime now = DateTime.UtcNow;
			Prune(now);
			Queue<DateTime> recent = s_recent.GetOrAdd(userID, _ => new Queue<DateTime>());
			lock (recent)
			{
				while (recent.Count > 0 && now - recent.Peek() >= Window)
				{
					recent.Dequeue();
				}

				if (recent.Count >= Reports)
				{
					retryAfterMs = Math.Max(1, (long)Math.Ceiling((recent.Peek() + Window - now).TotalMilliseconds));
					return false;
				}

				recent.Enqueue(now);
				retryAfterMs = 0;
				return true;
			}
		}

		// Forgets users whose last report left the window, so the map only holds recent reporters
		private static void Prune(DateTime now)
		{
			long next = Interlocked.Read(ref s_nextPruneTicks);
			if (now.Ticks < next || Interlocked.CompareExchange(ref s_nextPruneTicks, (now + s_pruneInterval).Ticks, next) != next)
			{
				return;
			}

			foreach ((long userID, Queue<DateTime> recent) in s_recent)
			{
				bool idle;
				lock (recent)
				{
					idle = recent.Count == 0 || now - recent.Last() >= Window;
				}
				if (idle)
				{
					s_recent.TryRemove(new KeyValuePair<long, Queue<DateTime>>(userID, recent));
				}
			}
		}
	}
}
