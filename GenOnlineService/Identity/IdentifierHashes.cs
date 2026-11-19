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
	// A keyed hash of something a ban holds: for an erased account under an active ban, its canonical mailbox (a +tag or Gmail's
	// dots do not make another one) and each sign-in provider id, so the same person cannot sign up again while the ban lasts.
	// Only the hash is stored; the key is the device HMAC key (DeviceHasher). A live account keeps no hash: its exact address
	// stays in users.email and is unique there (users.email_normalized); the mailbox form matters only to a ban.
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
			// a lookup path for the holds; not unique: several banned accounts may hold the same mailbox or provider id
			// (each ban keeps its own hold)
			builder.HasIndex(e => new { e.Kind, e.ValueHmac }).HasDatabaseName("ix_identifier_hashes_kind_value");
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

		// A mailbox a ban holds: an erased account's hash kept while its ban lasts, or a live account under an active ban. A
		// mailbox used by an unbanned live account is free, so more than one account may share it. `exceptUserID` lets an
		// account keep its own address while it changes another one.
		public static async Task<bool> EmailHeldByBan(AppDbContext db, string? email, Int64 exceptUserID, DateTime utcNow, CancellationToken cancellationToken = default)
		{
			string? canonical = CanonicalEmail(email);
			if (canonical == null)
			{
				return false;
			}

			IQueryable<Int64> banned = Database.Moderation.ActiveSanctions(db, SanctionTypes.Ban, utcNow).Select(s => s.UserID);
			byte[] hash = Hash(HashedIdentifierKinds.Email, canonical);

			// the hold an erased account kept while its ban lasts
			if (await db.IdentifierHashes.AnyAsync(h => h.Kind == HashedIdentifierKinds.Email && h.ValueHmac == hash && h.SanctionID != null
				&& h.UserID != exceptUserID && banned.Contains(h.UserID), cancellationToken))
			{
				return true;
			}

			// a live account under an active ban holds its mailbox in users.email; no hash is kept for it
			List<string?> addresses = await Database.Moderation.ActiveSanctions(db, SanctionTypes.Ban, utcNow)
				.Where(s => s.UserID != exceptUserID)
				.Join(db.Users.AsNoTracking().Where(u => u.Email != null && u.ID != exceptUserID), s => s.UserID, u => u.ID, (s, u) => u.Email)
				.Distinct()
				.ToListAsync(cancellationToken);
			return addresses.Any(address => String.Equals(CanonicalEmail(address), canonical, StringComparison.Ordinal));
		}

		// A provider id an erased, banned account kept: signing in with it again is refused while the ban lasts
		public static Task<bool> ProviderHeld(AppDbContext db, string provider, string subject, CancellationToken cancellationToken = default)
		{
			string kind = HashedIdentifierKinds.Provider(provider);
			byte[] hash = Hash(kind, subject);
			return db.IdentifierHashes.AnyAsync(h => h.Kind == kind && h.ValueHmac == hash && h.SanctionID != null, cancellationToken);
		}

		// A live account keeps no address hash (its address is held by address uniqueness), so the startup call has nothing
		// to do. Kept so older callers keep compiling.
		public static Task<int> Backfill(AppDbContext db, CancellationToken cancellationToken = default) => Task.FromResult(0);

		// The hashes an erased account kept for a ban go once no ban on it is active (or the ban row is gone)
		public static Task<int> PurgeEndedHolds(AppDbContext db, DateTime utcNow, CancellationToken cancellationToken = default)
		{
			IQueryable<Int64> banned = Database.Moderation.ActiveSanctions(db, SanctionTypes.Ban, utcNow).Select(s => s.UserID);
			return db.IdentifierHashes.Where(h => h.SanctionID != null && !banned.Contains(h.UserID)).ExecuteDeleteAsync(cancellationToken);
		}
	}
}
