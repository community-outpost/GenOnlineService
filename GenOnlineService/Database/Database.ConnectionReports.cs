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
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.Text.Json;

public static class ConnectionReportOutcomes
{
	public const string ConnectedDirect = "connected_direct";
	public const string ConnectedRelay = "connected_relay";
	public const string Failed = "failed";
	public const string Disconnected = "disconnected";

	public static readonly string[] All = { ConnectedDirect, ConnectedRelay, Failed, Disconnected };
}

// The states an event may name: the outcomes plus the two in-progress states
public static class ConnectionReportStates
{
	public const string Connecting = "connecting";
	public const string FindingRoute = "finding_route";

	public static readonly string[] All = { Connecting, FindingRoute, ConnectionReportOutcomes.ConnectedDirect, ConnectionReportOutcomes.ConnectedRelay, ConnectionReportOutcomes.Failed, ConnectionReportOutcomes.Disconnected };
}

// The final result of one connection attempt from the reporter to one peer in one lobby, never with an IP address
public class ConnectionReport
{
	public Int64 ID { get; set; }
	public Int64 ReporterID { get; set; }
	public Int64 LobbyID { get; set; }
	public Int64 TargetUserID { get; set; }
	public int Attempt { get; set; } = 1;
	public string Outcome { get; set; } = String.Empty;
	public string Transport { get; set; } = String.Empty;
	public int IpVersion { get; set; }

	// [{state, t_ms}] oldest first, at most 32
	public string Events { get; set; } = "[]";

	public DateTime CreatedAt { get; set; } = DateTime.UnixEpoch;
	public DateTime UpdatedAt { get; set; } = DateTime.UnixEpoch;
}

public class ConnectionReportConfiguration : IEntityTypeConfiguration<ConnectionReport>
{
	public void Configure(EntityTypeBuilder<ConnectionReport> builder)
	{
		builder.ToTable("connection_reports");

		builder.HasKey(e => e.ID);

		// A repeated report of the same attempt replaces the earlier one, the reporter prefix serves reads by reporter
		builder.HasIndex(e => new { e.ReporterID, e.LobbyID, e.TargetUserID, e.Attempt }).IsUnique().HasDatabaseName("ux_connection_reports_reporter_lobby_target_attempt");
		builder.HasIndex(e => new { e.TargetUserID, e.UpdatedAt }).HasDatabaseName("ix_connection_reports_target_updated_at");
		builder.HasIndex(e => e.LobbyID).HasDatabaseName("ix_connection_reports_lobby_id");
		builder.HasIndex(e => e.UpdatedAt).HasDatabaseName("ix_connection_reports_updated_at");

		builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
		builder.Property(e => e.ReporterID).HasColumnName("reporter_id");
		builder.Property(e => e.LobbyID).HasColumnName("lobby_id");
		builder.Property(e => e.TargetUserID).HasColumnName("target_user_id");
		builder.Property(e => e.Attempt).HasColumnName("attempt");
		builder.Property(e => e.Outcome).HasColumnName("outcome").HasMaxLength(16);
		builder.Property(e => e.Transport).HasColumnName("transport").HasMaxLength(32);
		builder.Property(e => e.IpVersion).HasColumnName("ip_version");
		builder.Property(e => e.Events).HasColumnName("events");
		builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");
	}
}

namespace Database
{
	// Connection reports of section 10.3, staff read them by player or by lobby
	public static class ConnectionReports
	{
		public const int MaximumEvents = 32;
		public const long MaximumEventMilliseconds = 600000;
		public const int ListLimit = 500;

		// Every report is kept 30 days after its last update
		public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

		private static readonly JsonSerializerOptions s_eventsJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

		public sealed record Event(string State, long TMs);

		public static string SerializeEvents(IReadOnlyList<Event> events) => JsonSerializer.Serialize(events, s_eventsJson);

		// A stored timeline that does not parse reads as empty
		public static List<Event> ParseEvents(string events)
		{
			try
			{
				return JsonSerializer.Deserialize<List<Event>>(events, s_eventsJson) ?? new List<Event>();
			}
			catch (JsonException)
			{
				return new List<Event>();
			}
		}

		// Inserts the report or replaces the one with the same reporter, lobby, target and attempt, created_at stays from the first report
		public static async Task Upsert(AppDbContext db, ConnectionReport report, CancellationToken cancellationToken = default)
		{
			DateTime now = TruncateToMicroseconds(DateTime.UtcNow);

			for (int attempt = 0; attempt < 2; attempt++)
			{
				int updated = await db.ConnectionReports
					.Where(r => r.ReporterID == report.ReporterID && r.LobbyID == report.LobbyID && r.TargetUserID == report.TargetUserID && r.Attempt == report.Attempt)
					.ExecuteUpdateAsync(s => s
						.SetProperty(r => r.Outcome, report.Outcome)
						.SetProperty(r => r.Transport, report.Transport)
						.SetProperty(r => r.IpVersion, report.IpVersion)
						.SetProperty(r => r.Events, report.Events)
						.SetProperty(r => r.UpdatedAt, now), cancellationToken);

				if (updated > 0)
				{
					return;
				}

				db.ConnectionReports.Add(new ConnectionReport
				{
					ReporterID = report.ReporterID,
					LobbyID = report.LobbyID,
					TargetUserID = report.TargetUserID,
					Attempt = report.Attempt,
					Outcome = report.Outcome,
					Transport = report.Transport,
					IpVersion = report.IpVersion,
					Events = report.Events,
					CreatedAt = now,
					UpdatedAt = now
				});

				try
				{
					await db.SaveChangesAsync(cancellationToken);
					return;
				}
				catch (DbUpdateException ex) when (attempt == 0 && DbHelpers.IsUniqueViolation(ex))
				{
					// Another request created the row first, the update path runs again
				}
				finally
				{
					db.ChangeTracker.Clear();
				}
			}
		}

		// Reports the user sent or that name the user as target, newest first
		public static async Task<List<ConnectionReport>> GetByPlayer(AppDbContext db, long userId, int limit = ListLimit, CancellationToken cancellationToken = default)
		{
			return await db.ConnectionReports.AsNoTracking()
				.Where(r => r.ReporterID == userId || r.TargetUserID == userId)
				.OrderByDescending(r => r.UpdatedAt).ThenByDescending(r => r.ID)
				.Take(limit)
				.ToListAsync(cancellationToken);
		}

		public static async Task<List<ConnectionReport>> GetByLobby(AppDbContext db, long lobbyId, int limit = ListLimit, CancellationToken cancellationToken = default)
		{
			return await db.ConnectionReports.AsNoTracking()
				.Where(r => r.LobbyID == lobbyId)
				.OrderBy(r => r.ID)
				.Take(limit)
				.ToListAsync(cancellationToken);
		}

		public sealed record Pair(long Low, long High, IReadOnlyList<ConnectionReport> Reports);

		// The reports of a lobby pair by pair, the lower user id first: user a to b, then b to a, attempt by attempt
		public static List<Pair> InPairs(IEnumerable<ConnectionReport> rows)
		{
			return rows
				.GroupBy(r => (Low: Math.Min(r.ReporterID, r.TargetUserID), High: Math.Max(r.ReporterID, r.TargetUserID)))
				.OrderBy(g => g.Key.Low).ThenBy(g => g.Key.High)
				.Select(g => new Pair(g.Key.Low, g.Key.High, g.OrderBy(r => r.Attempt).ThenBy(r => r.ReporterID == g.Key.Low ? 0 : 1).ToList()))
				.ToList();
		}

		// One connection_lookup row per actor and target inside the window, written before the reports are read, true when a row was written
		public static async Task<bool> LogLookup(AppDbContext db, ModerationActor actor, string targetKind, long target, string by, DateTime now, TimeSpan window)
		{
			DateTime since = now - window;
			bool logged = await db.AuditLog.AsNoTracking().AnyAsync(e => e.TargetKind == targetKind && e.TargetID == target
				&& e.Action == ModerationActions.ConnectionLookup && e.ActorKind == actor.Kind && e.ActorID == actor.UserID && e.CreatedAt > since);
			if (logged)
			{
				return false;
			}

			await Moderation.AddAuditEntry(db, actor, ModerationActions.ConnectionLookup, targetKind, target, null, new { by });
			return true;
		}

		// Runs hourly with the chat purge
		public static async Task<int> PurgeExpired(AppDbContext db, DateTime utcNow, CancellationToken cancellationToken = default)
		{
			DateTime cutoff = utcNow - Retention;
			return await db.ConnectionReports.Where(r => r.UpdatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
		}

		// Account deletion removes the reports the user sent and the ones that name the user
		public static async Task<int> DeleteUserData(AppDbContext db, long userId, CancellationToken cancellationToken = default)
		{
			return await db.ConnectionReports.Where(r => r.ReporterID == userId || r.TargetUserID == userId).ExecuteDeleteAsync(cancellationToken);
		}

		// Both providers store microseconds, so the stored row matches what a later read returns
		private static DateTime TruncateToMicroseconds(DateTime value)
		{
			return new DateTime(value.Ticks - (value.Ticks % 10), DateTimeKind.Utc);
		}
	}
}
