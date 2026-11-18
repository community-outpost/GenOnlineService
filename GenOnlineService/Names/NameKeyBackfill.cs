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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GenOnlineService.Names
{
	// Brings users.name_key in line with NameKey.Of(username). The NameKeys migration gives every existing row a unique
	// placeholder, because SQL cannot compute the key, and "GenOnlineService migrate" runs this afterwards on every run, so
	// it also rekeys after a confusables table or Unicode change. Users whose names collide under the key and tag are
	// reported and keep their placeholder, because the unique index would refuse them and merging them silently would
	// hide one player. Every other user is keyed, so a run that stops on collisions leaves only those rows unkeyed.
	public static class NameKeyBackfill
	{
		// The exit code of a migrate run that stops on colliding names
		public const int CollisionExitCode = 2;

		public sealed record Holder(Int64 UserID, string Name);

		public sealed record Collision(string Key, int Tag, IReadOnlyList<Holder> Holders);

		// Updated counts the users that got their computed key
		public sealed record Outcome(int Updated, IReadOnlyList<Collision> Collisions);

		// The key a row holds until the backfill computes it: a control character never appears in a storable name
		public static string Placeholder(Int64 userID) => "\u0001" + userID.ToString(System.Globalization.CultureInfo.InvariantCulture);

		public static async Task<Outcome> Run(AppDbContext db, CancellationToken cancellationToken = default)
		{
			var rows = await db.Users.AsNoTracking()
				.Select(u => new { u.ID, u.Username, u.NameTag, u.NameKey })
				.ToListAsync(cancellationToken);
			var keyed = rows.Select(r => new { r.ID, r.Username, r.NameTag, r.NameKey, Computed = NameKey.Of(r.Username) }).ToList();

			List<Collision> collisions = keyed
				.GroupBy(r => (r.Computed, r.NameTag))
				.Where(g => g.Count() > 1)
				.Select(g => new Collision(g.Key.Computed, g.Key.NameTag, g.OrderBy(r => r.ID).Select(r => new Holder(r.ID, r.Username)).ToList()))
				.OrderBy(c => c.Holders[0].UserID)
				.ToList();
			HashSet<Int64> colliding = collisions.SelectMany(c => c.Holders).Select(h => h.UserID).ToHashSet();

			// a colliding row keeps or gets its placeholder, every other row gets its computed key
			Dictionary<Int64, string> stale = keyed
				.Select(r => (r.ID, r.NameKey, Target: colliding.Contains(r.ID) ? Placeholder(r.ID) : r.Computed))
				.Where(r => !String.Equals(r.NameKey, r.Target, StringComparison.Ordinal))
				.ToDictionary(r => r.ID, r => r.Target);
			if (stale.Count == 0)
			{
				return new Outcome(0, collisions);
			}

			await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(cancellationToken);
			List<User> users = new(stale.Count);
			foreach (Int64[] chunk in stale.Keys.Chunk(1000))
			{
				users.AddRange(await db.Users.AsTracking().Where(u => chunk.Contains(u.ID)).ToListAsync(cancellationToken));
			}

			// placeholders first, so a new key never meets the old key of a row that has not moved yet
			foreach (User user in users)
			{
				user.NameKey = Placeholder(user.ID);
			}
			await db.SaveChangesAsync(cancellationToken);

			foreach (User user in users)
			{
				user.NameKey = stale[user.ID];
			}
			await db.SaveChangesAsync(cancellationToken);
			await tx.CommitAsync(cancellationToken);
			db.ChangeTracker.Clear();
			return new Outcome(users.Count(u => !colliding.Contains(u.ID)), collisions);
		}

		// The migrate command's step: 0 when every key is current, CollisionExitCode with a report when names collide
		public static async Task<int> RunCommand(AppDbContext db, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
		{
			Outcome outcome = await Run(db, cancellationToken);
			if (outcome.Collisions.Count == 0)
			{
				output.WriteLine(outcome.Updated == 0 ? "Name keys are up to date." : $"Computed the name key of {outcome.Updated} user(s).");
				return 0;
			}

			error.WriteLine($"Cannot key every display name: {outcome.Collisions.Count} group(s) of users hold look-alike names with one tag, which the unique (name_key, name_tag) index refuses.");
			error.WriteLine($"Computed the name key of {outcome.Updated} other user(s). The users below keep their names, tags and a placeholder key, so only exact names protect them until they are keyed.");
			error.WriteLine("Rename all but one user of each group (for example PATCH /api/v1/players/<id> with a staff account), then run migrate again:");
			foreach (Collision collision in outcome.Collisions)
			{
				error.WriteLine($"  tag {DisplayNames.FormatTag(collision.Tag)}, key \"{collision.Key}\": {String.Join(", ", collision.Holders.Select(h => $"user {h.UserID} \"{h.Name}\""))}");
			}
			return CollisionExitCode;
		}
	}
}
