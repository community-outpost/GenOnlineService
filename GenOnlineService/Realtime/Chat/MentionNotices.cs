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

namespace GenOnlineService.Realtime
{
	internal enum MentionPresence { Offline, Outside, InChannel }

	// MentionCreate (section 6.2): a channel message that mentions an online user none of whose connections is in that channel
	internal static class MentionNotices
	{
		// In memory on the copy that serves the players, like ChatRateLimiter
		internal static MentionThrottle Throttle { get; } = new MentionThrottle(TimeProvider.System);

		// The mentioned users to notify, in mention order: online, outside the channel, not blocked either way with the author
		// and admitted by the throttle. Offline users get nothing, now or later.
		public static long[] Recipients(long author, long channelID, IReadOnlyList<long> mentions, Func<long, MentionPresence> presenceOf, IReadOnlySet<long> blocked, MentionThrottle throttle)
		{
			List<long> recipients = new();
			foreach (long userID in mentions)
			{
				if (userID == author || recipients.Contains(userID) || presenceOf(userID) != MentionPresence.Outside || blocked.Contains(userID))
				{
					continue;
				}
				if (throttle.TryAdmit(author, userID, channelID))
				{
					recipients.Add(userID);
				}
			}
			return recipients.ToArray();
		}

		// Through user:<id>, so every connection of each recipient gets it
		public static async Task SendAsync(Message message, IEnumerable<long> recipients, Func<long, IRealtimeClient> userOf)
		{
			MentionCreateEvent notice = new MentionCreateEvent { ChannelId = message.ChannelId, Message = message };
			foreach (long userID in recipients)
			{
				await userOf(userID).MentionCreate(notice);
			}
		}

		// From this copy's sessions, which hold every player because only the active copy serves players
		public static MentionPresence PresenceOf(long userID, long channelID)
		{
			MentionPresence presence = MentionPresence.Offline;
			foreach (var byUser in WebSocketManager.GetUserDataCache().Values)
			{
				if (!byUser.TryGetValue(userID, out UserSession? session) || RealtimeAudience.ConnectionOf(session) == null)
				{
					continue;
				}
				if (session.networkRoomID == channelID)
				{
					return MentionPresence.InChannel;
				}
				presence = MentionPresence.Outside;
			}
			return presence;
		}
	}

	// At most one MentionCreate per user per channel per 30 s and at most 5 per author per minute; a refused notice spends neither
	internal sealed class MentionThrottle
	{
		public static readonly TimeSpan RecipientWindow = TimeSpan.FromSeconds(30);
		public static readonly TimeSpan AuthorWindow = TimeSpan.FromMinutes(1);
		public const int AuthorLimit = 5;

		private readonly TimeProvider m_clock;
		private readonly object m_lock = new();
		private readonly Dictionary<(long UserID, long ChannelID), DateTimeOffset> m_lastNotice = new();
		private readonly Dictionary<long, Queue<DateTimeOffset>> m_byAuthor = new();
		private DateTimeOffset m_nextSweep = DateTimeOffset.MinValue;

		public MentionThrottle(TimeProvider clock)
		{
			m_clock = clock;
		}

		public bool TryAdmit(long author, long recipient, long channelID)
		{
			lock (m_lock)
			{
				DateTimeOffset now = m_clock.GetUtcNow();
				Sweep(now);

				if (m_lastNotice.TryGetValue((recipient, channelID), out DateTimeOffset last) && now - last < RecipientWindow)
				{
					return false;
				}

				if (!m_byAuthor.TryGetValue(author, out Queue<DateTimeOffset>? sent))
				{
					sent = new Queue<DateTimeOffset>();
					m_byAuthor[author] = sent;
				}
				Expire(sent, now);
				if (sent.Count >= AuthorLimit)
				{
					return false;
				}

				sent.Enqueue(now);
				m_lastNotice[(recipient, channelID)] = now;
				return true;
			}
		}

		private static void Expire(Queue<DateTimeOffset> sent, DateTimeOffset now)
		{
			while (sent.TryPeek(out DateTimeOffset first) && now - first >= AuthorWindow)
			{
				sent.Dequeue();
			}
		}

		// Drops what no longer limits anyone, at most once a minute
		private void Sweep(DateTimeOffset now)
		{
			if (now < m_nextSweep)
			{
				return;
			}
			m_nextSweep = now + AuthorWindow;

			foreach ((long, long) key in m_lastNotice.Where(entry => now - entry.Value >= RecipientWindow).Select(entry => entry.Key).ToArray())
			{
				m_lastNotice.Remove(key);
			}
			foreach (long author in m_byAuthor.Keys.ToArray())
			{
				Expire(m_byAuthor[author], now);
				if (m_byAuthor[author].Count == 0)
				{
					m_byAuthor.Remove(author);
				}
			}
		}
	}
}
