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

using GenOnlineService.Investigation;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace Database
{
	// An account that used a searched identifier; Events counts its sessions on the addresses, null for devices
	public sealed record IdentifierAccount(long UserID, string Name, int NameTag, string State, DateTime FirstSeenAt, DateTime LastSeenAt, int? Events);

	// The reverse lookups of the staff investigation page, most recently seen first, a page of Take plus one to tell whether one follows
	public static class IdentifierAccounts
	{
		// An IP range search reads at most this many address and account pairs
		public const int MaximumRangeRows = 50000;

		private sealed record Seen(long UserID, DateTime First, DateTime Last, int? Events);

		// Null when the range holds more sign-ins than one search reads
		public static async Task<(List<IdentifierAccount> Accounts, bool HasNext)?> Find(AppDbContext db, IdentifierQuery query, int skip, int take, CancellationToken cancellationToken = default)
		{
			List<Seen> seen;
			if (IdentifierKinds.IsDevice(query.Kind))
			{
				List<byte[]> hashes = query.Hashes.ToList();
				seen = (await db.Devices.AsNoTracking()
					.Where(d => d.Kind == query.Kind && hashes.Contains(d.ValueHmac))
					.GroupBy(d => d.UserID)
					.Select(g => new { UserID = g.Key, First = g.Min(d => d.FirstSeenAt), Last = g.Max(d => d.LastSeenAt) })
					.OrderByDescending(s => s.Last).ThenBy(s => s.UserID)
					.Skip(skip).Take(take + 1)
					.ToListAsync(cancellationToken))
					.Select(s => new Seen(s.UserID, s.First, s.Last, null)).ToList();
			}
			else if (query.Kind == IdentifierKinds.IpRange)
			{
				List<Seen>? inRange = await InRange(db, query.Range!.Value, query.StoredPrefix!, cancellationToken);
				if (inRange == null)
				{
					return null;
				}
				seen = inRange.OrderByDescending(s => s.Last).ThenBy(s => s.UserID).Skip(skip).Take(take + 1).ToList();
			}
			else
			{
				IQueryable<SessionEvent> events = db.SessionEvents.AsNoTracking();
				events = query.Kind == IdentifierKinds.Ip ? events.Where(e => e.Ip == query.Address) : events.Where(e => e.Country == query.Country);
				seen = (await events
					.GroupBy(e => e.UserID)
					.Select(g => new { UserID = g.Key, First = g.Min(e => e.CreatedAt), Last = g.Max(e => e.CreatedAt), Events = g.Count() })
					.OrderByDescending(s => s.Last).ThenBy(s => s.UserID)
					.Skip(skip).Take(take + 1)
					.ToListAsync(cancellationToken))
					.Select(s => new Seen(s.UserID, s.First, s.Last, s.Events)).ToList();
			}

			bool hasNext = seen.Count > take;
			List<long> ids = seen.Take(take).Select(s => s.UserID).ToList();
			var users = await db.Users.AsNoTracking()
				.Where(u => ids.Contains(u.ID))
				.Select(u => new { u.ID, u.Username, u.NameTag, u.State })
				.ToDictionaryAsync(u => u.ID, cancellationToken);

			// a row of a removed account names nobody
			List<IdentifierAccount> accounts = seen.Take(take).Where(s => users.ContainsKey(s.UserID))
				.Select(s => new IdentifierAccount(s.UserID, users[s.UserID].Username, users[s.UserID].NameTag, users[s.UserID].State, s.First, s.Last, s.Events))
				.ToList();
			return (accounts, hasNext);
		}

		// Addresses are stored as text: the database narrows by the text prefix, the exact range check runs here
		private static async Task<List<Seen>?> InRange(AppDbContext db, IPNetwork range, string storedPrefix, CancellationToken cancellationToken)
		{
			var rows = await db.SessionEvents.AsNoTracking()
				.Where(e => e.Ip.StartsWith(storedPrefix))
				.GroupBy(e => new { e.Ip, e.UserID })
				.Select(g => new { g.Key.Ip, g.Key.UserID, First = g.Min(e => e.CreatedAt), Last = g.Max(e => e.CreatedAt), Events = g.Count() })
				.Take(MaximumRangeRows + 1)
				.ToListAsync(cancellationToken);
			if (rows.Count > MaximumRangeRows)
			{
				return null;
			}

			return rows.Where(r => IPAddress.TryParse(r.Ip, out IPAddress? address) && range.Contains(address))
				.GroupBy(r => r.UserID)
				.Select(g => new Seen(g.Key, g.Min(r => r.First), g.Max(r => r.Last), g.Sum(r => r.Events)))
				.ToList();
		}
	}
}
