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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Globalization;

// One name change: who, from what to what. Feeds MemberUpdate and the name.changed notice.
public sealed record NameChange(Int64 UserID, string OldName, int OldTag, string NewName, int NewTag)
{
	public bool NameChanged => !String.Equals(OldName, NewName, StringComparison.Ordinal);
}

// Every one of the 9,999 tags of a name is in use
public sealed class NameTagsExhaustedException : Exception
{
	public NameTagsExhaustedException(string normalized) : base($"No free tag for the name {normalized}") { }
}

namespace Database
{
	// Xbox-style names: the name is not unique, name#tag is. Keycloak owns both in the official deployment and the
	// environment copies them (ApplyUpstream); elsewhere the environment picks the tag itself (Rename, NewUserTag).
	public static class DisplayNames
	{
		public const int MinTag = 1;
		public const int MaxTag = 9999;

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
				&& NameNormalizer.Normalize(name).Length <= User.UsernameMaxLength;
		}

		// A random tag no other user holds with this name
		public static async Task<int> FreeTag(AppDbContext db, string normalized, Int64? exceptUserID = null, int? alsoAvoid = null)
		{
			HashSet<int> used = (await db.Users
				.Where(u => u.UsernameNormalized == normalized && (exceptUserID == null || u.ID != exceptUserID))
				.Select(u => u.NameTag)
				.ToListAsync()).ToHashSet();
			if (alsoAvoid != null)
			{
				used.Add(alsoAvoid.Value);
			}

			List<int> free = Enumerable.Range(MinTag, MaxTag - MinTag + 1).Where(t => !used.Contains(t)).ToList();
			if (free.Count == 0)
			{
				throw new NameTagsExhaustedException(normalized);
			}

			return free[Random.Shared.Next(free.Count)];
		}

		// Sets a new, unsaved user's normalized name and a free tag
		public static async Task AssignNewUser(AppDbContext db, User user)
		{
			user.UsernameNormalized = NameNormalizer.Normalize(user.Username);
			user.NameTag = await FreeTag(db, user.UsernameNormalized);
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
			if (await db.Users.AnyAsync(u => u.UsernameNormalized == normalized && u.NameTag == tag))
			{
				return false;
			}

			user.Username = name!;
			user.UsernameNormalized = normalized;
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

		// ApplyUpstream inside the caller's transaction (the Keycloak change log); a unique violation is the caller's to retry
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
			User? holder = await db.Users.AsTracking().FirstOrDefaultAsync(u => u.ID != userID && u.UsernameNormalized == normalized && u.NameTag == tag);
			if (holder != null)
			{
				int moved = await FreeTag(db, normalized, holder.ID, tag);
				changes.Add(Record(db, holder, holder.Username, moved, "upstream:stale"));

				// the holder leaves the tag before the user takes it, so the unique index never sees both
				await db.SaveChangesAsync();
				s_log.LogInformation("User {HolderId} held {Name} with a stale tag; moved to {Tag} for user {UserId}", holder.ID, Format(holder.Username, tag), FormatTag(moved), userID);
			}

			changes.Add(Record(db, user, name, tag, changedBy));
			await db.SaveChangesAsync();
			return changes;
		}

		// A rename where the environment owns names: a change of case or width keeps the tag, any other name gets a free one.
		// Null when nothing changed or the user does not exist.
		public static async Task<NameChange?> Rename(AppDbContext db, Int64 userID, string name, string changedBy)
		{
			for (int attempt = 1; ; attempt++)
			{
				try
				{
					return await RenameOnce(db, userID, name, changedBy);
				}
				catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex) && attempt < 3)
				{
					// two renames picked the same free tag; pick again
					db.ChangeTracker.Clear();
				}
			}
		}

		private static async Task<NameChange?> RenameOnce(AppDbContext db, Int64 userID, string name, string changedBy)
		{
			User? user = await db.Users.AsTracking().FirstOrDefaultAsync(u => u.ID == userID);
			if (user == null || String.Equals(user.Username, name, StringComparison.Ordinal))
			{
				return null;
			}

			string normalized = NameNormalizer.Normalize(name);
			int tag = normalized == user.UsernameNormalized ? user.NameTag : await FreeTag(db, normalized, userID);
			NameChange change = Record(db, user, name, tag, changedBy);
			await db.SaveChangesAsync();
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
			user.NameTag = tag;
			user.RenamedAt = now;
			user.UpdatedAt = now;
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
