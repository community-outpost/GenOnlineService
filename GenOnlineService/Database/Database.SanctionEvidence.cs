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
using GenOnlineService.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// A screenshot a sanction relies on. Screenshots expire 90 days after upload (the filer TTL on screenshots/), so the one a
// sanction needs is copied to evidence/ under the same key, which has no TTL; the service deletes the copy 90 days after the
// sanction ended (Privacy Policy). Several sanctions may hold the same copy.
public class SanctionEvidence
{
	public const int ObjectKeyMaxLength = 256;

	public Int64 ID { get; set; }
	public Int64 SanctionID { get; set; }
	public Int64 UserID { get; set; }
	public Int64 MatchID { get; set; }

	// The key of the copy, under evidence/
	public string ObjectKey { get; set; } = String.Empty;

	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;
	public Int64? CreatedByID { get; set; }
}

public class SanctionEvidenceConfiguration : IEntityTypeConfiguration<SanctionEvidence>
{
	public void Configure(EntityTypeBuilder<SanctionEvidence> builder)
	{
		builder.ToTable("sanction_evidence");

		builder.HasKey(e => e.ID);
		builder.HasIndex(e => new { e.SanctionID, e.ObjectKey }).IsUnique().HasDatabaseName("ux_sanction_evidence_sanction_key");
		builder.HasIndex(e => e.ObjectKey).HasDatabaseName("ix_sanction_evidence_key");

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.SanctionID).HasColumnName("sanction_id");
		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.MatchID).HasColumnName("match_id");
		builder.Property(e => e.ObjectKey).HasColumnName("object_key").HasMaxLength(SanctionEvidence.ObjectKeyMaxLength);
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		builder.Property(e => e.CreatedByID).HasColumnName("created_by_id");
	}
}

public enum EvidenceOutcome
{
	Held,
	SanctionNotFound,
	MatchNotFound,
	// The sanctioned player did not play in the match
	NotInMatch,
	// The sanction ended more than 90 days ago, so its hold is already over
	HoldOver,
	StoreUnavailable,
}

public sealed record EvidenceResult(EvidenceOutcome Outcome, IReadOnlyList<string> Held, int Missing)
{
	public static EvidenceResult Failed(EvidenceOutcome outcome) => new(outcome, Array.Empty<string>(), 0);
}

namespace Database
{
	public static class SanctionEvidenceStore
	{
		public const string ScreenshotPrefix = "screenshots/";
		public const string EvidencePrefix = "evidence/";
		public static readonly TimeSpan HoldAfterEnd = TimeSpan.FromDays(90);

		// A sanction ended when it was lifted or expired, whichever came first; an active one never ended
		private static DateTime? EndedAt(Sanction s) =>
			s.LiftedAt != null && (s.ExpiresAt == null || s.LiftedAt < s.ExpiresAt) ? s.LiftedAt : s.ExpiresAt;

		// The folders a player's screenshots of a match can be in: the upload key carries the date the lobby was created, which
		// is not stored, so the match start date and the days before it are searched
		public static IEnumerable<string> ScreenshotFolders(Int64 matchID, Int64 userID, DateTime matchStartedAt)
		{
			string user = Helpers.ComputeMD5Hash($"{matchID}_{userID}");
			for (int daysBefore = -1; daysBefore <= 2; daysBefore++)
			{
				DateTime day = matchStartedAt.Date.AddDays(-daysBefore);
				yield return $"{ScreenshotPrefix}{day.Year}/{day.Month}/{day.Day}/match_{matchID}/user_{user}/";
			}
		}

		public static string EvidenceKeyOf(string screenshotKey) => EvidencePrefix + screenshotKey.Substring(ScreenshotPrefix.Length);

		// Copies the sanctioned player's screenshots of a match to evidence/ and records that the sanction holds them. A
		// screenshot already gone (its TTL passed) is counted as missing; one the sanction already holds is held again.
		public static async Task<EvidenceResult> HoldMatchScreenshots(AppDbContext db, IMatchDataStore? store, Int64 sanctionID, Int64 matchID, ModerationActor actor, DateTime utcNow, CancellationToken cancellationToken = default)
		{
			Sanction? sanction = await db.Sanctions.AsNoTracking().FirstOrDefaultAsync(s => s.ID == sanctionID, cancellationToken);
			if (sanction == null)
			{
				return EvidenceResult.Failed(EvidenceOutcome.SanctionNotFound);
			}

			DateTime? ended = EndedAt(sanction);
			if (ended != null && ended < utcNow - HoldAfterEnd)
			{
				return EvidenceResult.Failed(EvidenceOutcome.HoldOver);
			}

			Match? match = await db.Matches.AsNoTracking().FirstOrDefaultAsync(m => m.Id == matchID, cancellationToken);
			if (match == null)
			{
				return EvidenceResult.Failed(EvidenceOutcome.MatchNotFound);
			}

			if (!await db.MatchPlayers.AsNoTracking().AnyAsync(p => p.MatchId == matchID && p.UserId == sanction.UserID, cancellationToken))
			{
				return EvidenceResult.Failed(EvidenceOutcome.NotInMatch);
			}

			if (store == null)
			{
				return EvidenceResult.Failed(EvidenceOutcome.StoreUnavailable);
			}

			HashSet<string> alreadyHeld = (await db.SanctionEvidence.AsNoTracking()
				.Where(e => e.SanctionID == sanctionID && e.MatchID == matchID)
				.Select(e => e.ObjectKey)
				.ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

			var held = new List<string>();
			int missing = 0;
			foreach (string folder in ScreenshotFolders(matchID, sanction.UserID, match.StartedAt))
			{
				// a copy another sanction already made is shared, even after the screenshot itself expired
				HashSet<string> copied = (await store.ListAsync(EvidenceKeyOf(folder), cancellationToken)).ToHashSet(StringComparer.Ordinal);
				IEnumerable<string> targets = (await store.ListAsync(folder, cancellationToken)).Select(EvidenceKeyOf).Union(copied, StringComparer.Ordinal);

				foreach (string target in targets)
				{
					if (target.Length > SanctionEvidence.ObjectKeyMaxLength)
					{
						missing++;
						continue;
					}

					if (alreadyHeld.Contains(target))
					{
						held.Add(target);
						continue;
					}

					StoredObject? screenshot = null;
					if (!copied.Contains(target))
					{
						screenshot = await store.GetAsync(ScreenshotPrefix + target.Substring(EvidencePrefix.Length), cancellationToken);
						if (screenshot == null)
						{
							missing++;
							continue;
						}
					}

					// the row comes first, so a copy that fails half way is still found and deleted by the purge
					var row = new SanctionEvidence { SanctionID = sanctionID, UserID = sanction.UserID, MatchID = matchID, ObjectKey = target, CreatedAt = utcNow, CreatedByID = actor.UserID };
					db.SanctionEvidence.Add(row);
					await db.SaveChangesAsync(cancellationToken);
					db.ChangeTracker.Clear();

					if (screenshot != null)
					{
						await store.PutAsync(target, screenshot, cancellationToken);
					}
					else if (await store.GetAsync(target, cancellationToken) == null)
					{
						// the purge deleted the shared copy between the listing and the row: nothing is left to hold
						await db.SanctionEvidence.Where(e => e.ID == row.ID).ExecuteDeleteAsync(cancellationToken);
						missing++;
						continue;
					}
					alreadyHeld.Add(target);
					held.Add(target);
				}
			}

			db.AuditLog.Add(Moderation.Entry(actor, ModerationActions.SanctionEvidence, AuditTargetKinds.User, sanction.UserID, null,
				new { sanction_id = sanctionID, match_id = matchID, files = held.Count, missing }, utcNow));
			await db.SaveChangesAsync(cancellationToken);

			return new EvidenceResult(EvidenceOutcome.Held, held, missing);
		}

		// Deletes the copies whose sanctions ended more than 90 days ago or no longer exist. A copy another sanction still
		// holds stays: that is checked right before each delete, so a hold recorded while the purge runs keeps its copy (and a
		// hold that finds its shared copy gone counts it missing). The object goes before its row, so a failed delete is
		// retried on the next run.
		public static async Task<int> PurgeExpired(AppDbContext db, IMatchDataStore? store, DateTime utcNow, CancellationToken cancellationToken = default)
		{
			DateTime cutoff = utcNow - HoldAfterEnd;
			IQueryable<Int64> holding = db.Sanctions.AsNoTracking()
				.Where(s => !((s.LiftedAt != null && s.LiftedAt < cutoff) || (s.ExpiresAt != null && s.ExpiresAt < cutoff)))
				.Select(s => s.ID);

			List<SanctionEvidence> expired = await db.SanctionEvidence.AsNoTracking()
				.Where(e => !holding.Contains(e.SanctionID))
				.OrderBy(e => e.ID)
				.Take(1000)
				.ToListAsync(cancellationToken);
			if (expired.Count == 0)
			{
				return 0;
			}

			// without the bucket nothing can be deleted, so the rows stay until it is back
			if (store == null)
			{
				return 0;
			}

			int removed = 0;
			foreach (IGrouping<string, SanctionEvidence> rows in expired.GroupBy(e => e.ObjectKey))
			{
				string key = rows.Key;
				if (!await db.SanctionEvidence.AsNoTracking().AnyAsync(e => e.ObjectKey == key && holding.Contains(e.SanctionID), cancellationToken))
				{
					await store.DeleteAsync(key, cancellationToken);
				}

				List<Int64> ids = rows.Select(r => r.ID).ToList();
				removed += await db.SanctionEvidence.Where(e => ids.Contains(e.ID)).ExecuteDeleteAsync(cancellationToken);
			}

			return removed;
		}
	}
}
