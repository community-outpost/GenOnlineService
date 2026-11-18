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

using GenOnlineService;
using GenOnlineService.Names;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage;
using System.Globalization;

// One name change: who, from what to what. Feeds MemberUpdate and the name.changed notice.
public sealed record NameChange(Int64 UserID, string OldName, int OldTag, string NewName, int NewTag)
{
	public bool NameChanged => !String.Equals(OldName, NewName, StringComparison.Ordinal);
}

// A name#tag a user has held, kept for that user under the look-alike key: no other user is given it, and the user gets
// the tag back when they take the name again. AppDbContext writes NameKey from Name on every save.
public class NameReservation
{
	public Int64 ID { get; set; }
	public Int64 UserID { get; set; }
	public User? User { get; set; }

	// The name as the user last held it
	public string Name { get; set; } = String.Empty;
	public string NameKey { get; set; } = String.Empty;
	public int NameTag { get; set; }

	// When the user last took the pair; the newest of a user's tags for one key comes back first
	public DateTime HeldAt { get; set; } = DateTime.UnixEpoch;
}

public class NameReservationConfiguration : IEntityTypeConfiguration<NameReservation>
{
	public void Configure(EntityTypeBuilder<NameReservation> builder)
	{
		builder.ToTable("name_reservations");

		builder.HasKey(e => e.ID);
		builder.HasIndex(e => new { e.NameKey, e.NameTag }).IsUnique().HasDatabaseName("ux_name_reservations_name_key_name_tag");
		builder.HasIndex(e => e.UserID).HasDatabaseName("ix_name_reservations_user_id");
		builder.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserID).OnDelete(DeleteBehavior.Cascade);

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(User.UsernameMaxLength);
		builder.Property(e => e.NameKey).HasColumnName("name_key").HasMaxLength(NameKey.MaxLength);
		builder.Property(e => e.NameTag).HasColumnName("name_tag");
		builder.Property(e => e.HeldAt).HasColumnName("held_at");
	}
}

// What a local rename did
public enum LocalRenameOutcome
{
	Renamed,
	Unchanged,
	NotFound,
	Taken
}

// Every one of the 9,999 tags of a name is in use
public sealed class NameTagsExhaustedException : Exception
{
	public NameTagsExhaustedException(string name) : base($"No free tag for the name {name}") { }
}

namespace Database
{
	// Xbox-style names: the name is not unique, name#tag is. The environment assigns the tag from the player's sequence for the
	// name (NameTags) on sign-up, rename, choice and reset, so the tag follows the name, and every pair a player has held stays
	// reserved for them (name_reservations). ApplyUpstream copies a name and tag another environment owns (the follow-prod
	// mirror, identity hub T8) and reserves nothing. Look-alikes count as one name everywhere: the plugin's key (NameKey) is
	// unique with the tag (ux_users_name_key_name_tag, ux_name_reservations_name_key_name_tag).
	public static class DisplayNames
	{
		public const int MinTag = 1;
		public const int MaxTag = 9999;

		// Candidates tried before a crowded name falls back to the next free tag after the first one
		private const int CandidateLimit = 64;

		private static readonly ILogger s_log = AppLog.For(typeof(DisplayNames));

		public static string FormatTag(int tag) => tag.ToString("D4", CultureInfo.InvariantCulture);

		// "Bob#0042": profiles, friend requests, moderation and staff tools
		public static string Format(string name, int tag) => $"{name}#{FormatTag(tag)}";

		// A copied name must fit the column; the rules on what players may choose live where names are chosen
		public static bool IsStorable(string? name)
		{
			return !String.IsNullOrWhiteSpace(name)
				&& !name.Any(Char.IsControl)
				&& String.Equals(TextSanitizer.Clean(name, User.UsernameMaxLength), name, StringComparison.Ordinal)
				&& NameNormalizer.Normalize(name).Length <= User.UsernameMaxLength
				&& NameKey.Of(name).Length <= NameKey.MaxLength;
		}

		// A random tag no other user holds with this name or a look-alike of it
		public static async Task<int> FreeTag(AppDbContext db, string name, Int64? exceptUserID = null, int? alsoAvoid = null)
		{
			string key = NameKey.Of(name);
			string normalized = NameNormalizer.Normalize(name);
			HashSet<int> used = (await db.Users
				.Where(u => (u.NameKey == key || u.UsernameNormalized == normalized) && (exceptUserID == null || u.ID != exceptUserID))
				.Select(u => u.NameTag)
				.ToListAsync()).ToHashSet();
			if (alsoAvoid != null)
			{
				used.Add(alsoAvoid.Value);
			}

			List<int> free = Enumerable.Range(MinTag, MaxTag - MinTag + 1).Where(t => !used.Contains(t)).ToList();
			if (free.Count == 0)
			{
				throw new NameTagsExhaustedException(name);
			}

			return free[Random.Shared.Next(free.Count)];
		}

		// The tag a user gets with a name: the one they hold now when the name is a look-alike of their current one, else their
		// own reserved tag for it, newest first, else the first of their candidates (NameTags) that no other user holds or has
		// reserved under the look-alike key. The same player and name always end at the same tag while it stays theirs.
		private static async Task<int> TagFor(AppDbContext db, Int64 userID, User? current, string name)
		{
			string key = NameKey.Of(name);
			string normalized = NameNormalizer.Normalize(name);
			HashSet<int> used = (await db.Users
				.Where(u => u.ID != userID && (u.NameKey == key || u.UsernameNormalized == normalized))
				.Select(u => u.NameTag)
				.ToListAsync()).ToHashSet();

			if (current != null && String.Equals(NameKey.Of(current.Username), key, StringComparison.Ordinal) && !used.Contains(current.NameTag))
			{
				return current.NameTag;
			}

			var reserved = await db.NameReservations
				.Where(r => r.NameKey == key)
				.Select(r => new { r.UserID, r.NameTag, r.HeldAt })
				.ToListAsync();
			foreach (var own in reserved.Where(r => r.UserID == userID).OrderByDescending(r => r.HeldAt))
			{
				// a name copied from an upstream can hold a reserved pair
				if (!used.Contains(own.NameTag))
				{
					return own.NameTag;
				}
			}

			used.UnionWith(reserved.Where(r => r.UserID != userID).Select(r => r.NameTag));
			const int range = MaxTag - MinTag + 1;
			if (used.Count >= range)
			{
				throw new NameTagsExhaustedException(name);
			}

			int first = 0;
			foreach (int tag in NameTags.Candidates(userID, key).Take(CandidateLimit))
			{
				if (!used.Contains(tag))
				{
					return tag;
				}
				if (first == 0)
				{
					first = tag;
				}
			}

			// a crowded name: the next free tag after the first candidate, still the same for the same player and name
			for (int step = 1; ; step++)
			{
				int tag = MinTag + (first - MinTag + step) % range;
				if (!used.Contains(tag))
				{
					return tag;
				}
			}
		}

		// Keeps name#tag for the user. With take the user now holds the pair, and a reservation of theirs is refreshed;
		// otherwise the pair is only added when nobody has it reserved, which covers a name copied from an upstream earlier.
		private static async Task Reserve(AppDbContext db, Int64 userID, string name, int tag, DateTime now, bool take)
		{
			string key = NameKey.Of(name);
			NameReservation? reservation = db.ChangeTracker.Entries<NameReservation>().Select(e => e.Entity).FirstOrDefault(r => r.NameKey == key && r.NameTag == tag)
				?? await db.NameReservations.AsTracking().FirstOrDefaultAsync(r => r.NameKey == key && r.NameTag == tag);
			if (reservation == null)
			{
				db.NameReservations.Add(new NameReservation { UserID = userID, Name = name, NameKey = key, NameTag = tag, HeldAt = now });
			}
			else if (take && reservation.UserID == userID)
			{
				reservation.Name = name;
				reservation.HeldAt = now;
			}
		}

		// Sets a new, unsaved user's normalized name, key and tag (TagFor), and reserves the pair for the user. The id is only
		// known after the insert, so a sign-up uses the candidates of user id 0 unless the caller gave an id. Two look-alike
		// sign-ups that pick the same tag at once meet a unique index, and the caller retries with the next tag.
		public static async Task AssignNewUser(AppDbContext db, User user)
		{
			// a retry detaches the user after a unique violation; the reservation of that attempt goes with it
			foreach (EntityEntry<NameReservation> stale in db.ChangeTracker.Entries<NameReservation>().Where(e => ReferenceEquals(e.Entity.User, user)).ToList())
			{
				stale.State = EntityState.Detached;
			}

			user.UsernameNormalized = NameNormalizer.Normalize(user.Username);
			user.NameKey = NameKey.Of(user.Username);
			user.NameTag = await TagFor(db, user.ID, null, user.Username);
			db.NameReservations.Add(new NameReservation { User = user, Name = user.Username, NameKey = user.NameKey, NameTag = user.NameTag, HeldAt = DateTime.UtcNow });
		}

		// A new user takes a trusted upstream's name and tag as they are when no local row holds them; true when it did.
		// Otherwise the caller assigns a local tag and ApplyUpstream sorts out the stale holder after the user exists.
		public static async Task<bool> TryAssignUpstream(AppDbContext db, User user, string? name, string? tagText)
		{
			if (!IsStorable(name) || !Int32.TryParse(tagText, NumberStyles.None, CultureInfo.InvariantCulture, out int tag) || tag < MinTag || tag > MaxTag)
			{
				return false;
			}

			string normalized = NameNormalizer.Normalize(name!);
			string key = NameKey.Of(name!);
			if (await db.Users.AnyAsync(u => u.NameTag == tag && (u.NameKey == key || u.UsernameNormalized == normalized)))
			{
				return false;
			}

			user.Username = name!;
			user.UsernameNormalized = normalized;
			user.NameKey = key;
			user.NameTag = tag;
			return true;
		}

		// Copies a trusted upstream's name and tag onto the user. A local row that still holds the same name#tag is a copy
		// the upstream has since changed, so it moves to a free tag until its own next sign-in corrects it.
		// Returns every change made: the user's, and the stale holder's. Runs in its own transaction: call it with none open.
		public static async Task<IReadOnlyList<NameChange>> ApplyUpstream(AppDbContext db, Int64 userID, string? name, string? tagText, string changedBy)
		{
			if (!IsStorable(name) || !Int32.TryParse(tagText, NumberStyles.None, CultureInfo.InvariantCulture, out int tag) || tag < MinTag || tag > MaxTag)
			{
				if (name != null || tagText != null)
				{
					s_log.LogWarning("Ignoring an upstream name for user {UserId}: not a storable name and a tag from {Min} to {Max}", userID, MinTag, MaxTag);
				}
				return Array.Empty<NameChange>();
			}

			for (int attempt = 1; ; attempt++)
			{
				try
				{
					return await ApplyUpstreamOnce(db, userID, name!, tag, changedBy);
				}
				catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex) && attempt < 3)
				{
					// another sign-in moved a name at the same moment; read again
					db.ChangeTracker.Clear();
				}
			}
		}

		private static async Task<IReadOnlyList<NameChange>> ApplyUpstreamOnce(AppDbContext db, Int64 userID, string name, int tag, string changedBy)
		{
			await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync();
			IReadOnlyList<NameChange> changes = await ApplyUpstreamCore(db, userID, name, tag, changedBy);
			await tx.CommitAsync();
			return changes;
		}

		// ApplyUpstream inside the caller's transaction; a unique violation is the caller's to retry
		public static async Task<IReadOnlyList<NameChange>> ApplyUpstreamInTransaction(AppDbContext db, Int64 userID, string? name, string? tagText, string changedBy)
		{
			if (!IsStorable(name) || !Int32.TryParse(tagText, NumberStyles.None, CultureInfo.InvariantCulture, out int tag) || tag < MinTag || tag > MaxTag)
			{
				s_log.LogWarning("Ignoring an upstream name for user {UserId}: not a storable name and a tag from {Min} to {Max}", userID, MinTag, MaxTag);
				return Array.Empty<NameChange>();
			}

			return await ApplyUpstreamCore(db, userID, name!, tag, changedBy);
		}

		private static async Task<IReadOnlyList<NameChange>> ApplyUpstreamCore(AppDbContext db, Int64 userID, string name, int tag, string changedBy)
		{
			List<NameChange> changes = new();

			User? user = await db.Users.AsTracking().FirstOrDefaultAsync(u => u.ID == userID);
			if (user == null || (String.Equals(user.Username, name, StringComparison.Ordinal) && user.NameTag == tag))
			{
				return changes;
			}

			string normalized = NameNormalizer.Normalize(name);
			string key = NameKey.Of(name);
			User? holder = await db.Users.AsTracking().FirstOrDefaultAsync(u => u.ID != userID && u.NameTag == tag && (u.NameKey == key || u.UsernameNormalized == normalized));
			if (holder != null)
			{
				int moved = await FreeTag(db, holder.Username, holder.ID, tag);
				changes.Add(Record(db, holder, holder.Username, moved, "upstream:stale"));

				// the holder leaves the tag before the user takes it, so the unique index never sees both
				await db.SaveChangesAsync();
				s_log.LogInformation("User {HolderId} held {Name} with a stale tag; moved to {Tag} for user {UserId}", holder.ID, Format(holder.Username, tag), FormatTag(moved), userID);
			}

			changes.Add(Record(db, user, name, tag, changedBy));
			await db.SaveChangesAsync();
			return changes;
		}

		// The neutral name of an account that still owes a chosen one (S17): "Player" and six digits, so pending accounts
		// spread over many names and never run out of tags
		public static string Placeholder() => "Player" + Random.Shared.Next(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

		// "Player" and six digits in any case: the shape of Placeholder, which a player cannot choose as the name they owe
		public static bool IsPlaceholderShaped(string name)
			=> name.Length == 12 && name.StartsWith("Player", StringComparison.OrdinalIgnoreCase) && name.AsSpan(6).IndexOfAnyExceptInRange('0', '9') < 0;

		// The name a player owes, chosen: the tag follows the name (TagFor), so the choice never fails on a taken tag. Settles
		// name_required_at even when the name is unchanged. False when the user is gone.
		public static async Task<(bool Found, NameChange? Change)> Choose(AppDbContext db, Int64 userID, string name, string changedBy)
		{
			for (int attempt = 1; ; attempt++)
			{
				User? user = await db.Users.AsTracking().FirstOrDefaultAsync(u => u.ID == userID);
				if (user == null)
				{
					return (false, null);
				}

				NameChange? change = null;
				if (!String.Equals(user.Username, name, StringComparison.Ordinal))
				{
					change = await Assign(db, user, name, await TagFor(db, userID, user, name), changedBy);
				}
				user.NameRequiredAt = null;

				try
				{
					await db.SaveChangesAsync();
					return (true, change);
				}
				catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex) && attempt < 3)
				{
					// another player took the name with that tag at the same moment; draw again
					db.ChangeTracker.Clear();
				}
			}
		}

		// Staff reset an inappropriate name: a placeholder with its tag (TagFor), the player owes a new name, and the security
		// stamp changes so website sessions end. Null when the user is gone.
		public static async Task<NameChange?> ResetName(AppDbContext db, Int64 userID, string changedBy)
		{
			for (int attempt = 1; ; attempt++)
			{
				User? user = await db.Users.AsTracking().FirstOrDefaultAsync(u => u.ID == userID);
				if (user == null)
				{
					return null;
				}

				string placeholder = Placeholder();
				NameChange change = await Assign(db, user, placeholder, await TagFor(db, userID, user, placeholder), changedBy);
				user.NameRequiredAt = DateTime.UtcNow;
				user.SecurityStamp = Guid.NewGuid().ToString("N");

				try
				{
					await db.SaveChangesAsync();
					return change;
				}
				catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex) && attempt < 3)
				{
					db.ChangeTracker.Clear();
				}
			}
		}

		// A rename where the environment owns names (self-hosted): the tag follows the name (TagFor), so a look-alike holding a
		// tag only moves the rename on to the next one; Taken only when every tag of the name is held or reserved. The unique
		// indexes decide a race, and the loser draws again.
		public static async Task<(LocalRenameOutcome Outcome, NameChange? Change)> Rename(AppDbContext db, Int64 userID, string name, string changedBy)
		{
			for (int attempt = 1; ; attempt++)
			{
				User? user = await db.Users.AsTracking().FirstOrDefaultAsync(u => u.ID == userID);
				if (user == null)
				{
					return (LocalRenameOutcome.NotFound, null);
				}
				if (String.Equals(user.Username, name, StringComparison.Ordinal))
				{
					return (LocalRenameOutcome.Unchanged, null);
				}

				NameChange change;
				try
				{
					change = await Assign(db, user, name, await TagFor(db, userID, user, name), changedBy);
				}
				catch (NameTagsExhaustedException)
				{
					return (LocalRenameOutcome.Taken, null);
				}

				try
				{
					await db.SaveChangesAsync();
					return (LocalRenameOutcome.Renamed, change);
				}
				catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex))
				{
					// another rename took a look-alike name with this tag at the same moment
					db.ChangeTracker.Clear();
					if (attempt == 3)
					{
						return (LocalRenameOutcome.Taken, null);
					}
				}
			}
		}

		// A local name change: Record, and the old and new pairs reserved for the user (Reserve)
		private static async Task<NameChange> Assign(AppDbContext db, User user, string name, int tag, string changedBy)
		{
			DateTime now = DateTime.UtcNow;
			await Reserve(db, user.ID, user.Username, user.NameTag, now, take: false);
			NameChange change = Record(db, user, name, tag, changedBy);
			await Reserve(db, user.ID, name, tag, now, take: true);
			return change;
		}

		// The one writer of a name change: history row of the previous name and tag, then the new values
		private static NameChange Record(AppDbContext db, User user, string name, int tag, string changedBy)
		{
			DateTime now = DateTime.UtcNow;
			NameChange change = new(user.ID, user.Username, user.NameTag, name, tag);

			db.UserUsernameHistory.Add(new UserUsernameHistory
			{
				UserID = user.ID,
				Username = user.Username,
				NameTag = user.NameTag,
				ChangedAt = now,
				ChangedBy = changedBy
			});

			user.Username = name;
			user.UsernameNormalized = NameNormalizer.Normalize(name);
			user.NameKey = NameKey.Of(name);
			user.NameTag = tag;
			user.RenamedAt = now;
			user.UpdatedAt = now;
			// any new name settles one the player owed; a staff name reset owes it again afterwards
			user.NameRequiredAt = null;
			return change;
		}

		public static async Task<(string Name, int Tag)?> Get(AppDbContext db, Int64 userID)
		{
			var row = await db.Users.Where(u => u.ID == userID).Select(u => new { u.Username, u.NameTag }).FirstOrDefaultAsync();
			return row == null ? null : (row.Username, row.NameTag);
		}

		public static async Task<Dictionary<Int64, (string Name, int Tag)>> GetBulk(AppDbContext db, IReadOnlyCollection<Int64> userIDs)
		{
			return await db.Users
				.Where(u => userIDs.Contains(u.ID))
				.Select(u => new { u.ID, u.Username, u.NameTag })
				.ToDictionaryAsync(u => u.ID, u => (u.Username, u.NameTag));
		}
	}
}
