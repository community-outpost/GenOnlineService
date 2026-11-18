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
using Microsoft.EntityFrameworkCore.Storage;

namespace GenOnlineService.Names
{
	// Brings name_reservations in line with users and user_username_history: every name#tag a user holds or has held is
	// reserved for them under NameKey.Of(name). SQL cannot compute the key, so the NameReservations migration creates the
	// table empty and "GenOnlineService migrate" runs this afterwards on every run, which also rekeys after a NameKey change.
	// Names and tags of users never change here. A pair several users held stays with the user who holds it now, else with
	// the one who held it last, else with the lowest id; the run counts those pairs.
	public static class NameReservationBackfill
	{
		// Added and Removed count rows; Contested counts pairs more than one user held
		public sealed record Outcome(int Added, int Removed, int Contested);

		private sealed record Claim(Int64 UserID, string Name, bool Current, DateTime HeldAt);

		public static async Task<Outcome> Run(AppDbContext db, CancellationToken cancellationToken = default)
		{
			DateTime now = DateTime.UtcNow;
			var users = await db.Users.AsNoTracking().Select(u => new { u.ID, u.Username, u.NameTag }).ToListAsync(cancellationToken);
			HashSet<Int64> userIDs = users.Select(u => u.ID).ToHashSet();
			var history = await db.UserUsernameHistory.AsNoTracking()
				.Where(h => h.NameTag != null)
				.Select(h => new { h.UserID, h.Username, h.NameTag, h.ChangedAt })
				.ToListAsync(cancellationToken);
			List<NameReservation> existing = await db.NameReservations.AsNoTracking().ToListAsync(cancellationToken);

			// every claim on a pair: the reservation rows themselves, the current names and the history of existing users
			List<(string Key, int Tag, Claim Claim)> claims = new();
			claims.AddRange(existing.Where(r => userIDs.Contains(r.UserID)).Select(r => (NameKey.Of(r.Name), r.NameTag, new Claim(r.UserID, r.Name, false, r.HeldAt))));
			claims.AddRange(history.Where(h => userIDs.Contains(h.UserID)).Select(h => (NameKey.Of(h.Username), h.NameTag!.Value, new Claim(h.UserID, h.Username, false, h.ChangedAt))));
			claims.AddRange(users.Select(u => (NameKey.Of(u.Username), u.NameTag, new Claim(u.ID, u.Username, true, now))));

			Dictionary<(string Key, int Tag), NameReservation> wanted = new();
			int contested = 0;
			foreach (var pair in claims.GroupBy(c => (c.Key, c.Tag)))
			{
				// one claim per user: current when they hold it now, held last at the newest time, with the name they held then;
				// a current pair keeps the time its row already has, so a second run changes nothing
				List<Claim> perUser = pair.GroupBy(c => c.Claim.UserID).Select(g =>
				{
					List<Claim> held = g.Select(c => c.Claim).ToList();
					Claim? current = held.FirstOrDefault(c => c.Current);
					Claim latest = held.Where(c => !c.Current).OrderByDescending(c => c.HeldAt).FirstOrDefault() ?? current!;
					return current == null
						? latest
						: current with { HeldAt = held.Any(c => !c.Current) ? held.Where(c => !c.Current).Max(c => c.HeldAt) : current.HeldAt };
				}).ToList();
				if (perUser.Count > 1)
				{
					contested++;
				}

				Claim winner = perUser.OrderByDescending(c => c.Current).ThenByDescending(c => c.HeldAt).ThenBy(c => c.UserID).First();
				wanted[pair.Key] = new NameReservation { UserID = winner.UserID, Name = winner.Name, NameKey = pair.Key.Key, NameTag = pair.Key.Tag, HeldAt = winner.HeldAt };
			}

			// a row stays when it already says what is wanted; every other row is replaced
			List<Int64> stale = new();
			foreach (NameReservation row in existing)
			{
				if (wanted.TryGetValue((row.NameKey, row.NameTag), out NameReservation? want)
					&& want.ID == 0
					&& want.UserID == row.UserID
					&& String.Equals(want.Name, row.Name, StringComparison.Ordinal)
					&& want.HeldAt == row.HeldAt)
				{
					want.ID = row.ID;
				}
				else
				{
					stale.Add(row.ID);
				}
			}
			List<NameReservation> added = wanted.Values.Where(r => r.ID == 0).ToList();
			if (stale.Count == 0 && added.Count == 0)
			{
				return new Outcome(0, 0, contested);
			}

			await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(cancellationToken);
			// stale rows go first, so a rekeyed pair never meets its old row in the unique index
			foreach (Int64[] chunk in stale.Chunk(1000))
			{
				await db.NameReservations.Where(r => chunk.Contains(r.ID)).ExecuteDeleteAsync(cancellationToken);
			}
			db.NameReservations.AddRange(added);
			await db.SaveChangesAsync(cancellationToken);
			await tx.CommitAsync(cancellationToken);
			db.ChangeTracker.Clear();
			return new Outcome(added.Count, stale.Count, contested);
		}

		// The migrate command's step after the name keys; it never stops the run
		public static async Task RunCommand(AppDbContext db, TextWriter output, CancellationToken cancellationToken = default)
		{
			Outcome outcome = await Run(db, cancellationToken);
			output.WriteLine(outcome.Added == 0 && outcome.Removed == 0
				? "Name reservations are up to date."
				: $"Reserved {outcome.Added} name#tag pair(s) and removed {outcome.Removed} outdated reservation(s).");
			if (outcome.Contested > 0)
			{
				output.WriteLine($"{outcome.Contested} name#tag pair(s) were held by more than one user; each stays reserved for the user who holds it now, else the one who held it last.");
			}
		}
	}
}
