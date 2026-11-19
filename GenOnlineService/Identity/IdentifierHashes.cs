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

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenOnlineService.Identity
{
	// A keyed hash of something that identifies a person: the canonical email of an account (one account per person), or, for
	// an erased account under an active ban, its email and each sign-in provider id, so the same person cannot sign up again
	// while the ban lasts. Only the hash is stored; the key is the device HMAC key (DeviceHasher).
	public class IdentifierHash
	{
		public const int KindMaxLength = 48;

		public Int64 ID { get; set; }
		public Int64 UserID { get; set; }

		// HashedIdentifierKinds.Email, or HashedIdentifierKinds.Provider(name) for any sign-in provider, present or future
		public string Kind { get; set; } = String.Empty;

		public byte[] ValueHmac { get; set; } = Array.Empty<byte>();

		// Set on the hashes an erased account keeps for this ban; they go when no ban on the account is active
		public Int64? SanctionID { get; set; }

		public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;
	}

	public class IdentifierHashConfiguration : IEntityTypeConfiguration<IdentifierHash>
	{
		public void Configure(EntityTypeBuilder<IdentifierHash> builder)
		{
			builder.ToTable("identifier_hashes");
			builder.HasKey(e => e.ID);
			// one row per mailbox or provider id: the database itself keeps one account per person, also for sign-ups at the same moment
			builder.HasIndex(e => new { e.Kind, e.ValueHmac }).IsUnique().HasDatabaseName("ux_identifier_hashes_kind_value");
			builder.HasIndex(e => e.UserID).HasDatabaseName("ix_identifier_hashes_user");

			builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
			builder.Property(e => e.UserID).HasColumnName("user_id");
			builder.Property(e => e.Kind).HasColumnName("kind").HasMaxLength(IdentifierHash.KindMaxLength);
			builder.Property(e => e.ValueHmac).HasColumnName("value_hmac").HasMaxLength(32).IsFixedLength();
			builder.Property(e => e.SanctionID).HasColumnName("sanction_id");
			builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		}
	}

	public static class HashedIdentifierKinds
	{
		public const string Email = "email";
		public const string ProviderPrefix = "provider:";

		// Generic: a provider added later is hashed and checked by its configured name with no code change
		public static string Provider(string name) => ProviderPrefix + name.ToLowerInvariant();
	}

	public static class IdentifierHashes
	{
		private const string GmailDomain = "gmail.com";

		// One mailbox, one form: lower case, the +tag of the local part dropped (Gmail, Outlook, iCloud, Proton and Fastmail
		// all deliver it to the base address), and for Gmail the dots of the local part dropped and googlemail.com read as
		// gmail.com. Null for a value that is not an address.
		public static string? CanonicalEmail(string? email)
		{
			string trimmed = (email ?? String.Empty).Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
			int at = trimmed.LastIndexOf('@');
			if (at <= 0 || at == trimmed.Length - 1)
			{
				return null;
			}

			string local = trimmed.Substring(0, at);
			string domain = trimmed.Substring(at + 1);
			int plus = local.IndexOf('+');
			if (plus >= 0)
			{
				local = local.Substring(0, plus);
			}
			if (domain == "googlemail.com")
			{
				domain = GmailDomain;
			}
			if (domain == GmailDomain)
			{
				local = local.Replace(".", String.Empty);
			}
			return local.Length == 0 ? null : local + "@" + domain;
		}

		public static byte[] Hash(string kind, string value) => DeviceHasher.HashIdentifier(kind + "\n" + value);

		public static byte[]? EmailHash(string? email) => CanonicalEmail(email) is string canonical ? Hash(HashedIdentifierKinds.Email, canonical) : null;

		// Another account, live or erased under a ban, already holds this mailbox
		public static async Task<bool> EmailHeldByOther(AppDbContext db, string? email, Int64 userID, CancellationToken cancellationToken = default)
		{
			byte[]? hash = EmailHash(email);
			if (hash == null)
			{
				return false;
			}
			// an account that already holds this mailbox keeps it: Identity validates on every update (stamp, failed sign-ins),
			// and accounts from before the rule may share one
			if (await db.IdentifierHashes.AnyAsync(h => h.Kind == HashedIdentifierKinds.Email && h.ValueHmac == hash && h.UserID == userID && h.SanctionID == null, cancellationToken))
			{
				return false;
			}
			return await db.IdentifierHashes.AnyAsync(h => h.Kind == HashedIdentifierKinds.Email && h.ValueHmac == hash && h.UserID != userID, cancellationToken);
		}

		// The account's mailbox row follows its email: replaced when it changes, removed when it has none. A new account has no
		// row to replace, so it only inserts: a delete there would conflict with other sign-ups under MariaDB's snapshot isolation
		public static async Task SyncEmail(AppDbContext db, Int64 userID, string? email, CancellationToken cancellationToken = default, bool newAccount = false)
		{
			byte[]? hash = EmailHash(email);
			if (!newAccount)
			{
				await db.IdentifierHashes.Where(h => h.UserID == userID && h.Kind == HashedIdentifierKinds.Email && h.SanctionID == null).ExecuteDeleteAsync(cancellationToken);
			}
			if (hash != null)
			{
				db.IdentifierHashes.Add(new IdentifierHash { UserID = userID, Kind = HashedIdentifierKinds.Email, ValueHmac = hash, CreatedAt = DateTime.UtcNow });
				await db.SaveChangesAsync(cancellationToken);
				db.ChangeTracker.Clear();
			}
		}

		// A provider id an erased, banned account kept: signing in with it again is refused while the ban lasts
		public static Task<bool> ProviderHeld(AppDbContext db, string provider, string subject, CancellationToken cancellationToken = default)
		{
			string kind = HashedIdentifierKinds.Provider(provider);
			byte[] hash = Hash(kind, subject);
			return db.IdentifierHashes.AnyAsync(h => h.Kind == kind && h.ValueHmac == hash && h.SanctionID != null, cancellationToken);
		}

		// Mailbox rows for accounts that have an email and none yet: imported accounts, which the import already keeps to one
		// per mailbox; returns how many
		public static async Task<int> Backfill(AppDbContext db, CancellationToken cancellationToken = default)
		{
			int added = 0;
			while (true)
			{
				var missing = await db.Users.AsNoTracking()
					.Where(u => u.Email != null && !db.IdentifierHashes.Any(h => h.UserID == u.ID && h.Kind == HashedIdentifierKinds.Email))
					.OrderBy(u => u.ID)
					.Select(u => new { u.ID, u.Email })
					.Take(1000)
					.ToListAsync(cancellationToken);
				if (missing.Count == 0)
				{
					return added;
				}

				DateTime now = DateTime.UtcNow;
				foreach (var user in missing)
				{
					// an address with no canonical form still gets a row, of its plain lower case, so the backfill ends
					byte[] hash = EmailHash(user.Email) ?? Hash(HashedIdentifierKinds.Email, user.Email!.Trim().ToLowerInvariant());
					db.IdentifierHashes.Add(new IdentifierHash { UserID = user.ID, Kind = HashedIdentifierKinds.Email, ValueHmac = hash, CreatedAt = now });
				}
				await db.SaveChangesAsync(cancellationToken);
				db.ChangeTracker.Clear();
				added += missing.Count;
			}
		}

		// The hashes an erased account kept for a ban go once no ban on it is active (or the ban row is gone)
		public static Task<int> PurgeEndedHolds(AppDbContext db, DateTime utcNow, CancellationToken cancellationToken = default)
		{
			IQueryable<Int64> banned = Database.Moderation.ActiveSanctions(db, SanctionTypes.Ban, utcNow).Select(s => s.UserID);
			return db.IdentifierHashes.Where(h => h.SanctionID != null && !banned.Contains(h.UserID)).ExecuteDeleteAsync(cancellationToken);
		}
	}
}
