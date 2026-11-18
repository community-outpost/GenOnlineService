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
using Xunit;

namespace GenOnlineService.Tests;

// The hourly retention purge of devices and session events (Privacy Policy: device ids 90 days after last seen, or for as
// long as a ban on the account lasts; connection records 90 days)
public class RetentionPurgeTests
{
	private static Device NewDevice(long userID, byte marker, DateTime lastSeen) => new()
	{
		UserID = userID,
		Kind = "machine_id",
		ValueHmac = Enumerable.Repeat(marker, Device.ValueHmacLength).ToArray(),
		FirstSeenAt = lastSeen.AddDays(-1),
		LastSeenAt = lastSeen,
	};

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task DevicesGoNinetyDaysAfterLastSeenUnlessTheAccountIsBanned(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		DateTime now = DateTime.UtcNow;
		const long plain = 1, permanentBan = 2, timedBan = 3, endedBan = 4, liftedBan = 5, muted = 6;

		await using (AppDbContext db = probe.Open())
		{
			db.Devices.AddRange(
				NewDevice(plain, 1, now.AddDays(-91)),        // past 90 days: goes
				NewDevice(plain, 2, now.AddDays(-89)),        // within 90 days: stays
				NewDevice(permanentBan, 3, now.AddDays(-400)), // banned for good: stays
				NewDevice(timedBan, 4, now.AddDays(-200)),    // ban still running: stays
				NewDevice(endedBan, 5, now.AddDays(-91)),     // ban expired: goes
				NewDevice(liftedBan, 6, now.AddDays(-91)),    // ban lifted: goes
				NewDevice(muted, 7, now.AddDays(-91)));       // a mute holds nothing: goes
			db.Sanctions.AddRange(
				new Sanction { UserID = permanentBan, Type = SanctionTypes.Ban, CreatedAt = now.AddDays(-500) },
				new Sanction { UserID = timedBan, Type = SanctionTypes.Ban, CreatedAt = now.AddDays(-10), ExpiresAt = now.AddDays(10) },
				new Sanction { UserID = endedBan, Type = SanctionTypes.Ban, CreatedAt = now.AddDays(-30), ExpiresAt = now.AddDays(-1) },
				new Sanction { UserID = liftedBan, Type = SanctionTypes.Ban, CreatedAt = now.AddDays(-30), LiftedAt = now.AddDays(-2) },
				new Sanction { UserID = muted, Type = SanctionTypes.Mute, CreatedAt = now.AddDays(-1), ExpiresAt = now.AddDays(10) });
			db.SessionEvents.AddRange(
				new SessionEvent { UserID = plain, Kind = "sign_in", Result = SessionEventResults.Ok, Ip = "192.0.2.1", CreatedAt = now.AddDays(-91) },
				new SessionEvent { UserID = plain, Kind = "sign_in", Result = SessionEventResults.Ok, Ip = "192.0.2.1", CreatedAt = now.AddDays(-89) });
			await db.SaveChangesAsync();
		}

		await using (AppDbContext db = probe.Open())
		{
			var (devices, sessionEvents) = await Database.Retention.Purge(db, now);
			Assert.Equal(4, devices);
			Assert.Equal(1, sessionEvents);
		}

		await using (AppDbContext db = probe.Open())
		{
			List<byte> left = (await db.Devices.Select(d => d.ValueHmac).ToListAsync()).Select(h => h[0]).OrderBy(b => b).ToList();
			Assert.Equal(new byte[] { 2, 3, 4 }, left);
			Assert.Equal(1, await db.SessionEvents.CountAsync());
			// the purge never touches sanctions
			Assert.Equal(5, await db.Sanctions.CountAsync());
		}

		// nothing left to remove
		await using (AppDbContext db = probe.Open())
		{
			Assert.Equal((0, 0), await Database.Retention.Purge(db, now));
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task SanctionsGoTwoYearsAfterTheyEndAndTheStaffLogAfterTwoYears(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		DateTime now = DateTime.UtcNow;
		DateTime longAgo = now.AddYears(-5);
		DateTime cutoff = now.AddYears(-2);

		await using (AppDbContext db = probe.Open())
		{
			db.Sanctions.AddRange(
				new Sanction { UserID = 1, Reason = "permanent", CreatedAt = longAgo },                                                      // permanent and active: stays
				new Sanction { UserID = 1, Reason = "expired long ago", Type = SanctionTypes.Mute, CreatedAt = longAgo, ExpiresAt = cutoff.AddDays(-1) }, // goes
				new Sanction { UserID = 1, Reason = "expired recently", Type = SanctionTypes.Ban, CreatedAt = longAgo, ExpiresAt = cutoff.AddDays(1) },  // stays
				new Sanction { UserID = 1, Reason = "lifted long ago", CreatedAt = longAgo, LiftedAt = cutoff.AddDays(-1) },                 // permanent ban lifted: goes
				new Sanction { UserID = 1, Reason = "lifted recently", CreatedAt = longAgo, LiftedAt = cutoff.AddDays(1) },                  // stays
				new Sanction { UserID = 1, Reason = "lifted early", CreatedAt = longAgo, ExpiresAt = now.AddYears(1), LiftedAt = cutoff.AddDays(-1) }, // ended when lifted: goes
				new Sanction { UserID = 1, Reason = "old kick", Type = SanctionTypes.Kick, CreatedAt = cutoff.AddDays(-1), ExpiresAt = cutoff.AddDays(-1) }, // goes
				new Sanction { UserID = 1, Reason = "new kick", Type = SanctionTypes.Kick, CreatedAt = cutoff.AddDays(1), ExpiresAt = cutoff.AddDays(1) });   // stays
			db.AuditLog.AddRange(
				new AuditLogEntry { CreatedAt = cutoff.AddDays(-1), ActorKind = "cli", Actor = "cli", Action = "ban", TargetKind = "user", TargetID = 1, Source = "local" }, // goes
				new AuditLogEntry { CreatedAt = cutoff.AddDays(1), ActorKind = "cli", Actor = "cli", Action = "lift", TargetKind = "user", TargetID = 1, Source = "local" }); // stays
			await db.SaveChangesAsync();
		}

		await using (AppDbContext db = probe.Open())
		{
			var (sanctions, auditLog) = await Database.Moderation.PurgeExpired(db, now);
			Assert.Equal(4, sanctions);
			Assert.Equal(1, auditLog);
		}

		await using (AppDbContext db = probe.Open())
		{
			List<string?> left = await db.Sanctions.OrderBy(s => s.ID).Select(s => s.Reason).ToListAsync();
			Assert.Equal(new[] { "permanent", "expired recently", "lifted recently", "new kick" }, left);
			Assert.Equal("lift", Assert.Single(await db.AuditLog.ToListAsync()).Action);
		}

		await using (AppDbContext db = probe.Open())
		{
			Assert.Equal((0, 0), await Database.Moderation.PurgeExpired(db, now));
		}
	}
}
