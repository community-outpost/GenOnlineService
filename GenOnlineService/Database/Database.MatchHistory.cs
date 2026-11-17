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
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

public static class MatchResults
{
	public const string Win = "win";
	public const string Loss = "loss";
}

public class Match
{
	// History columns are narrower than the live lobby limits (255/255/512); stored values are clamped (see TextSanitizer)
	public const int NameMaxLength = 64;
	public const int MapNameMaxLength = 128;
	public const int MapPathMaxLength = 256;

	public long Id { get; set; }

	// Null when the owner was not imported
	public long? OwnerUserId { get; set; }

	public string Name { get; set; } = string.Empty;
	public ELobbyType? LobbyType { get; set; }
	public string RosterType { get; set; } = string.Empty;
	public string MapName { get; set; } = string.Empty;
	public string? MapPath { get; set; }
	public bool IsOfficialMap { get; set; }
	public bool VanillaTeams { get; set; }
	public uint StartingCash { get; set; }
	public bool LimitSuperweapons { get; set; }
	public bool TrackStats { get; set; }
	public bool AllowObservers { get; set; }
	public ushort MaxCamHeight { get; set; }
	public uint ExeCrc { get; set; }
	public uint IniCrc { get; set; }
	public DateTime StartedAt { get; set; }
	public DateTime? FinishedAt { get; set; }

	public List<MatchPlayer> Players { get; set; } = new();
}

// One occupied lobby slot of a match; user_id is null for AI slots and users that were not imported
public class MatchPlayer
{
	public long MatchId { get; set; }
	public short Slot { get; set; }
	public long? UserId { get; set; }
	public short? Team { get; set; }
	public int? Side { get; set; }
	public int? Color { get; set; }

	// Null until the outcome is known
	public string? Result { get; set; }

	// MatchPlayerDetails as JSON
	public string? Details { get; set; }
}

// The per-slot values that are not columns
public sealed class MatchPlayerDetails
{
	public string display_name { get; set; } = string.Empty;
	public EPlayerType slot_state { get; set; } = EPlayerType.SLOT_CLOSED;
	public int startpos { get; set; } = -1;
	public int buildings_built { get; set; }
	public int buildings_killed { get; set; }
	public int buildings_lost { get; set; }
	public int units_built { get; set; }
	public int units_killed { get; set; }
	public int units_lost { get; set; }
	public int total_money { get; set; }
	public bool desynced { get; set; }
	public uint? duration { get; set; }
	public List<MemberMetadataModel> metadata { get; set; } = new();
}

public class MatchPublication
{
	public long MatchId { get; set; }
	public DateTime? NextAttemptAt { get; set; }
	public DateTime? PublishedAt { get; set; }
	public int Attempts { get; set; }
	public string? LastError { get; set; }
}

// Lobby types are stored as short lowercase strings
public sealed class LobbyTypeConverter : ValueConverter<ELobbyType, string>
{
	public LobbyTypeConverter()
		: base(v => ToStore(v), v => FromStore(v))
	{
	}

	public static string ToStore(ELobbyType value) => value switch
	{
		ELobbyType.CustomGame => "custom_game",
		ELobbyType.QuickMatch => "quick_match",
		_ => throw new ArgumentOutOfRangeException(nameof(value))
	};

	public static ELobbyType FromStore(string value) => value switch
	{
		"custom_game" => ELobbyType.CustomGame,
		"quick_match" => ELobbyType.QuickMatch,
		_ => throw new InvalidOperationException($"Unknown lobby type \"{value}\".")
	};
}

public class MatchConfiguration : IEntityTypeConfiguration<Match>
{
	public void Configure(EntityTypeBuilder<Match> entity)
	{
		entity.ToTable("matches");

		entity.HasKey(e => e.Id);

		// Serves the match feed: finished matches since a point in time, ordered by finish time
		entity.HasIndex(e => new { e.FinishedAt, e.Id }).HasDatabaseName("ix_matches_finished_at");
		// Serves the staff match list's start date filter
		entity.HasIndex(e => e.StartedAt).HasDatabaseName("ix_matches_started_at");

		entity.HasMany(e => e.Players).WithOne().HasForeignKey(p => p.MatchId).OnDelete(DeleteBehavior.Cascade);

		entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
		entity.Property(e => e.OwnerUserId).HasColumnName("owner_user_id");
		entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(Match.NameMaxLength);
		entity.Property(e => e.LobbyType).HasColumnName("lobby_type").HasMaxLength(16).HasConversion(new LobbyTypeConverter());
		entity.Property(e => e.RosterType).HasColumnName("roster_type").HasMaxLength(32);
		entity.Property(e => e.MapName).HasColumnName("map_name").HasMaxLength(Match.MapNameMaxLength);
		entity.Property(e => e.MapPath).HasColumnName("map_path").HasMaxLength(Match.MapPathMaxLength);
		entity.Property(e => e.IsOfficialMap).HasColumnName("is_official_map");
		entity.Property(e => e.VanillaTeams).HasColumnName("vanilla_teams");
		entity.Property(e => e.StartingCash).HasColumnName("starting_cash");
		entity.Property(e => e.LimitSuperweapons).HasColumnName("limit_superweapons");
		entity.Property(e => e.TrackStats).HasColumnName("track_stats");
		entity.Property(e => e.AllowObservers).HasColumnName("allow_observers");
		entity.Property(e => e.MaxCamHeight).HasColumnName("max_cam_height");
		entity.Property(e => e.ExeCrc).HasColumnName("exe_crc");
		entity.Property(e => e.IniCrc).HasColumnName("ini_crc");
		entity.Property(e => e.StartedAt).HasColumnName("started_at");
		entity.Property(e => e.FinishedAt).HasColumnName("finished_at");
	}
}

public class MatchPlayerConfiguration : IEntityTypeConfiguration<MatchPlayer>
{
	public void Configure(EntityTypeBuilder<MatchPlayer> entity)
	{
		entity.ToTable("match_players");

		entity.HasKey(e => new { e.MatchId, e.Slot });
		entity.HasIndex(e => e.UserId).HasDatabaseName("ix_match_players_user_id");

		entity.Property(e => e.MatchId).HasColumnName("match_id");
		entity.Property(e => e.Slot).HasColumnName("slot");
		entity.Property(e => e.UserId).HasColumnName("user_id");
		entity.Property(e => e.Team).HasColumnName("team");
		entity.Property(e => e.Side).HasColumnName("side");
		entity.Property(e => e.Color).HasColumnName("color");
		entity.Property(e => e.Result).HasColumnName("result").HasMaxLength(16);
		entity.Property(e => e.Details).HasColumnName("details");
	}
}

public class MatchPublicationConfiguration : IEntityTypeConfiguration<MatchPublication>
{
	public void Configure(EntityTypeBuilder<MatchPublication> entity)
	{
		entity.ToTable("match_publications");

		entity.HasKey(e => e.MatchId);
		entity.HasIndex(e => new { e.PublishedAt, e.NextAttemptAt }).HasDatabaseName("ix_match_publications_pending");
		entity.HasOne<Match>().WithOne().HasForeignKey<MatchPublication>(e => e.MatchId).OnDelete(DeleteBehavior.Cascade);

		entity.Property(e => e.MatchId).HasColumnName("match_id").ValueGeneratedNever();
		entity.Property(e => e.NextAttemptAt).HasColumnName("next_attempt_at");
		entity.Property(e => e.PublishedAt).HasColumnName("published_at");
		entity.Property(e => e.Attempts).HasColumnName("attempts");
		entity.Property(e => e.LastError).HasColumnName("last_error").HasMaxLength(512);
	}
}

// Matches and wins per playable side; index is side - 2
public class FactionStatsStructure
{
	public const int numSides = 12;
	public int[] matches { get; set; } = new int[numSides];
	public int[] wins { get; set; } = new int[numSides];
}

namespace GenOnlineService
{

	public enum EScreenshotType
	{
		NONE = -1,
		SCREENSHOT_TYPE_LOADSCREEN = 0,
		SCREENSHOT_TYPE_GAMEPLAY = 1,
		SCREENSHOT_TYPE_SCORESCREEN = 2
	}


	public enum EMetadataFileType
	{
		UNKNOWN = -1,
		FILE_TYPE_SCREENSHOT = 0,
		FILE_TYPE_REPLAY = 1
	};

	public struct MemberMetadataModel
	{
		public string file_name { get; set; }
		public EMetadataFileType file_type { get; set; }
	}

	// Handles JSON booleans stored as integers (0/1) by older versions of the service.
	public sealed class BoolFromIntConverter : JsonConverter<bool>
	{
		public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType == JsonTokenType.Number)
				return reader.GetInt32() != 0;
			return reader.GetBoolean();
		}

		public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
			=> writer.WriteBooleanValue(value);
	}

	public struct MatchdataMemberModel
	{
		public Int64 user_id { get; set; } = -1;            // bigint(20) NOT NULL
		public string display_name { get; set; } = String.Empty;    // varchar(32) NOT NULL
		public EPlayerType slot_state { get; set; } = EPlayerType.SLOT_CLOSED;        // smallint(6) unsigned NOT NULL
		public int side { get; set; } = -1;                 // int(2) NOT NULL
		public int color { get; set; } = -1;                // int(2) NOT NULL
		public int team { get; set; } = -1;                 // int(1) NOT NULL
		public int startpos { get; set; } = -1;             // int(1) NOT NULL
		public int buildings_built { get; set; } = 0;     // int(11) DEFAULT NULL
		public int buildings_killed { get; set; } = 0;     // int(11) DEFAULT NULL
		public int buildings_lost { get; set; } = 0;       // int(11) DEFAULT NULL
		public int units_built { get; set; } = 0;          // int(11) DEFAULT NULL
		public int units_killed { get; set; } = 0;         // int(11) DEFAULT NULL
		public int units_lost { get; set; } = 0;           // int(11) DEFAULT NULL
		public int total_money { get; set; } = 0;          // int(11) DEFAULT NULL

		[JsonConverter(typeof(BoolFromIntConverter))]
		public bool won { get; set; } = false;                // tinyint(4) DEFAULT NULL
		
        [JsonConverter(typeof(BoolFromIntConverter))]
		public bool desynced {get; set; } = false;

		public uint? duration { get; set; } = null;

		public List<MemberMetadataModel> metadata { get; set; } = new List<MemberMetadataModel>();

		public MatchdataMemberModel()
		{
		}
	}
}

namespace Database
{
	public readonly record struct ExternalPublicationWorkItem(long MatchId, int Attempt);

	public static class MatchHistory
	{
		private static readonly ILogger s_log = AppLog.For(typeof(MatchHistory));

		private const int SlotCount = 8;

		public static MatchPlayerDetails ReadDetails(string? json)
		{
			if (string.IsNullOrEmpty(json))
				return new MatchPlayerDetails();

			return JsonSerializer.Deserialize<MatchPlayerDetails>(json) ?? new MatchPlayerDetails();
		}

		public static string WriteDetails(MatchPlayerDetails details)
		{
			return JsonSerializer.Serialize(details);
		}

		// The API shape of a slot; slots without a user report user_id -1
		public static MatchdataMemberModel ToMemberModel(MatchPlayer player)
		{
			MatchPlayerDetails details = ReadDetails(player.Details);

			return new MatchdataMemberModel
			{
				user_id = player.UserId ?? -1,
				display_name = details.display_name,
				slot_state = details.slot_state,
				side = player.Side ?? -1,
				color = player.Color ?? -1,
				team = player.Team ?? -1,
				startpos = details.startpos,
				buildings_built = details.buildings_built,
				buildings_killed = details.buildings_killed,
				buildings_lost = details.buildings_lost,
				units_built = details.units_built,
				units_killed = details.units_killed,
				units_lost = details.units_lost,
				total_money = details.total_money,
				won = player.Result == MatchResults.Win,
				desynced = details.desynced,
				duration = details.duration,
				metadata = details.metadata ?? new List<MemberMetadataModel>()
			};
		}

		private static MatchHistory_Entry ToEntry(Match match, IEnumerable<MatchPlayer> players)
		{
			var entry = new MatchHistory_Entry(
				match.Id,
				match.OwnerUserId ?? -1,
				match.Name,
				match.FinishedAt != null,
				match.StartedAt.ToString("O"),
				(match.FinishedAt ?? match.StartedAt).ToString("O"),
				match.MapName,
				match.MapPath ?? string.Empty,
				match.RosterType,
				match.LobbyType,
				match.IsOfficialMap,
				match.VanillaTeams,
				match.StartingCash,
				match.LimitSuperweapons,
				match.TrackStats,
				match.AllowObservers,
				match.MaxCamHeight,
				match.ExeCrc,
				match.IniCrc
			);

			foreach (MatchPlayer player in players.OrderBy(p => p.Slot))
			{
				entry.members.Add(ToMemberModel(player));
			}

			return entry;
		}

		private static async Task<List<MatchHistory_Entry>> ToEntries(AppDbContext db, List<Match> matches, CancellationToken cancellationToken = default)
		{
			List<MatchHistory_Entry> entries = new();

			if (matches.Count == 0)
				return entries;

			List<long> ids = matches.Select(m => m.Id).ToList();
			var players = (await db.MatchPlayers.AsNoTracking()
				.Where(p => ids.Contains(p.MatchId))
				.ToListAsync(cancellationToken))
				.ToLookup(p => p.MatchId);

			foreach (Match match in matches)
			{
				entries.Add(ToEntry(match, players[match.Id]));
			}

			return entries;
		}

		private static async Task<MatchPlayer?> GetPlayer(AppDbContext db, long matchId, int slotIndex, CancellationToken cancellationToken = default)
		{
			return await db.MatchPlayers.AsTracking()
				.FirstOrDefaultAsync(p => p.MatchId == matchId && p.Slot == (short)slotIndex, cancellationToken);
		}

		public static async Task CommitPlayerOutcome(
			AppDbContext db,
			int slotIndex,
			ulong matchId,
			int side,
			int buildingsBuilt,
			int buildingsKilled,
			int buildingsLost,
			int unitsBuilt,
			int unitsKilled,
			int unitsLost,
			int totalMoney,
			bool won,
			bool desynced,
			uint duration)
		{
			if (slotIndex < 0 || slotIndex >= SlotCount)
				return;

			try
			{
				MatchPlayer? player = await GetPlayer(db, (long)matchId, slotIndex);
				if (player == null)
					return;

				MatchPlayerDetails details = ReadDetails(player.Details);
				details.buildings_built = buildingsBuilt;
				details.buildings_killed = buildingsKilled;
				details.buildings_lost = buildingsLost;
				details.units_built = unitsBuilt;
				details.units_killed = unitsKilled;
				details.units_lost = unitsLost;
				details.total_money = totalMoney;
				details.desynced = desynced;
				details.duration = duration;

				player.Side = side;
				player.Result = won ? MatchResults.Win : MatchResults.Loss;
				player.Details = WriteDetails(details);

				await db.SaveChangesAsync();
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "CommitPlayerOutcome failed");
			}
			finally
			{
				db.ChangeTracker.Clear();
			}
		}

		private static string ComputeRosterType(Dictionary<int, int> playersPerTeam)
		{
			int noTeamCount = playersPerTeam.GetValueOrDefault(-1, 0);

			var groups = playersPerTeam
				.Where(kv => kv.Key != -1)
				.Select(kv => kv.Value)
				.Concat(Enumerable.Repeat(1, noTeamCount))
				.OrderBy(c => c)
				.ToList();

			int activePlayers = groups.Sum();

			if (activePlayers == 0 || groups.Count == 1)
			{
				return "Unknown";
			}

			if (activePlayers > 2 && groups.All(c => c == 1))
			{
				return $"{activePlayers} Player FFA";
			}

			return string.Join("v", groups);
		}

		public static async Task<ulong> CreatePlaceholderMatchHistory(
			AppDbContext db,
			GenOnlineService.Lobby lobby)
		{
			if (lobby == null)
				return 0;

			try
			{
				DateTime now = DateTime.UtcNow;
				var match = new Match
				{
					StartedAt = now,
					OwnerUserId = lobby.Owner > 0 ? lobby.Owner : null,
					Name = TextSanitizer.Clean(lobby.Name, Match.NameMaxLength),
					MapName = TextSanitizer.Clean(lobby.MapName, Match.MapNameMaxLength),
					MapPath = TextSanitizer.Clean(lobby.MapPath, Match.MapPathMaxLength),
					IsOfficialMap = lobby.IsMapOfficial,
					LobbyType = lobby.LobbyType,
					VanillaTeams = lobby.IsVanillaTeamsOnly,
					StartingCash = lobby.StartingCash,
					LimitSuperweapons = lobby.IsLimitSuperweapons,
					TrackStats = lobby.IsTrackingStats,
					AllowObservers = lobby.AllowObservers,
					MaxCamHeight = lobby.MaximumCameraHeight,
					ExeCrc = lobby.ExeCRC,
					IniCrc = lobby.IniCRC
				};

				Dictionary<int, int> playersPerTeam = new();

				foreach (var member in lobby.Members)
				{
					if (member.SlotState == EPlayerType.SLOT_OPEN ||
						member.SlotState == EPlayerType.SLOT_CLOSED)
						continue;

					var details = new MatchPlayerDetails
					{
						display_name = member.DisplayName,
						slot_state = member.SlotState,
						startpos = member.StartingPosition
					};

					match.Players.Add(new MatchPlayer
					{
						Slot = (short)member.SlotIndex,
						UserId = member.UserID > 0 ? member.UserID : null,
						Team = (short)member.Team,
						Side = member.Side,
						Color = member.Color,
						Details = WriteDetails(details)
					});

					// Observers still have PLAYERTEMPLATE_RANDOM (-2) at match start and are not active players
					if (member.Side != -2)
					{
						playersPerTeam[member.Team] = playersPerTeam.GetValueOrDefault(member.Team, 0) + 1;
					}
				}

				match.RosterType = ComputeRosterType(playersPerTeam);

				db.Matches.Add(match);
				await db.SaveChangesAsync();

				ulong id = (ulong)match.Id;
				lobby.SetMatchID(id);

				return id;
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "CreatePlaceholderMatchHistory failed");
				return 0;
			}
			finally
			{
				db.ChangeTracker.Clear();
			}
		}

		/// <summary>
		/// The slots allowed to win the timestamp fallback below. A player who reported won=false has
		/// conceded, so when they disconnected says nothing about the result - drop them and let the
		/// remaining player take it. Only a reported LOSS drops a player: a reported win that never
		/// reached the row is why we are in this fallback at all, and must not count against them.
		///
		/// 1v1 only. In teams or FFA a concession does not identify a winner, so those keep comparing
		/// every active slot as before. Same if every player conceded, or if none did - there is either
		/// nothing left to choose between or nothing to go on.
		/// </summary>
		public static List<int> BuildFallbackCandidates(
			IReadOnlyDictionary<int, MatchdataMemberModel> members,
			string strMatchRosterType,
			IReadOnlyDictionary<Int64, bool> reportedOutcomes)
		{
			// Observers and AI/placeholder slots (user_id <= 0) never quit the game and must not be
			// selected as "last to leave = winner" - the same filter the fallback loop applies.
			List<int> lstActiveSlots = new();
			List<int> lstCandidateSlots = new();

			foreach (var member in members)
			{
				if (member.Value.side == Constants.OBSERVER_SIDE_VALUE || member.Value.user_id <= 0)
					continue;

				lstActiveSlots.Add(member.Key);

				if (reportedOutcomes.TryGetValue(member.Value.user_id, out bool bReportedWon) && !bReportedWon)
					continue;

				lstCandidateSlots.Add(member.Key);
			}

			// Roster type was computed back at match start, so leaving early cannot change it. The slot
			// count is checked too, in case the stored value and the actual slots disagree.
			if (strMatchRosterType != "1v1" || lstActiveSlots.Count != 2)
				return lstActiveSlots;

			return lstCandidateSlots.Count > 0 ? lstCandidateSlots : lstActiveSlots;
		}

		private static async Task<Dictionary<int, MatchdataMemberModel>> LoadMembersBySlot(AppDbContext db, long matchId, CancellationToken cancellationToken = default)
		{
			var players = await db.MatchPlayers.AsNoTracking()
				.Where(p => p.MatchId == matchId)
				.ToListAsync(cancellationToken);

			return players.ToDictionary(p => (int)p.Slot, ToMemberModel);
		}

		public static async Task DetermineLobbyWinnerIfNotPresent(
			AppDbContext db,
			GenOnlineService.Lobby lobby,
			CancellationToken cancellationToken = default)
		{
			if (lobby == null || lobby.MatchID == 0)
				return;

			try
			{
				Dictionary<int, MatchdataMemberModel> members = await LoadMembersBySlot(db, (long)lobby.MatchID, cancellationToken);
				if (members.Count == 0)
					return;

				// A reported desync is a No Result: every non-observer loses, which the external leaderboard reads as such
				bool anyDesync = members.Values.Any(m => m.desynced);
				if (anyDesync)
				{
					foreach (var kv in members)
					{
						if (kv.Value.side == Constants.OBSERVER_SIDE_VALUE)
							continue;

						await UpdateMatchHistorySetWinFlag(db, lobby.MatchID, kv.Key, false, cancellationToken);
					}
					return;
				}

				// Winner groups from active, non-observer members; teamless players get synthetic keys
				Dictionary<int, List<int>> winGroups = new();
				foreach (var kv in members)
				{
					var model = kv.Value;
					if (model.side == Constants.OBSERVER_SIDE_VALUE || !model.won)
						continue;

					int groupKey = model.team != -1 ? model.team : -1000 - kv.Key;
					if (!winGroups.TryGetValue(groupKey, out var slotList))
					{
						slotList = new List<int>();
						winGroups[groupKey] = slotList;
					}
					slotList.Add(kv.Key);
				}

				// A unique largest winner group wins; ties or no winner groups fall back to abandon timestamps
				int? conclusiveWinningTeam = null;
				int conclusiveWinningSlot = -1;
				if (winGroups.Count > 0)
				{
					int maxCount = winGroups.Values.Max(g => g.Count);
					var topGroups = winGroups.Where(g => g.Value.Count == maxCount).ToList();
					if (topGroups.Count == 1)
					{
						var winningGroup = topGroups[0];
						int teamKey = winningGroup.Key;
						if (teamKey > -1000)
						{
							conclusiveWinningTeam = teamKey;
						}
						else
						{
							conclusiveWinningSlot = winningGroup.Value[0];
						}
					}
				}

				if (conclusiveWinningTeam != null || conclusiveWinningSlot != -1)
				{
					foreach (var kv in members)
					{
						bool isWinner = conclusiveWinningTeam != null
							? kv.Value.team == conclusiveWinningTeam.Value
							: kv.Key == conclusiveWinningSlot;

						await UpdateMatchHistorySetWinFlag(db, lobby.MatchID, kv.Key, isWinner, cancellationToken);
					}

					return;
				}

				// No winner: who quit last wins, by in-game abandon time first and lobby removal time second
				s_log.LogInformation("Match {MatchId} lobby {LobbyId}: no winner in DB, falling back to abandon timestamps. IngameAbandon={IngameAbandonCount} MemberLeft={MemberLeftCount}",
					lobby.MatchID, lobby.LobbyID, lobby.TimePlayerAbandonedIngame.Count, lobby.TimeMemberLeft.Count);
				foreach (var _kv in lobby.TimePlayerAbandonedIngame)
					s_log.LogDebug("Match {MatchId}: IngameAbandon user={UserId} at={AbandonTime}", lobby.MatchID, _kv.Key, _kv.Value);
				foreach (var _kv in lobby.TimeMemberLeft)
					s_log.LogDebug("Match {MatchId}: MemberLeft user={UserId} at={LeftTime}", lobby.MatchID, _kv.Key, _kv.Value);

				// In a 1v1, anyone who reported a loss is dropped - see BuildFallbackCandidates
				string strRosterType = await db.Matches
					.Where(m => m.Id == (long)lobby.MatchID)
					.Select(m => m.RosterType)
					.FirstOrDefaultAsync(cancellationToken) ?? String.Empty;

				List<int> lstCandidateSlots = BuildFallbackCandidates(members, strRosterType, lobby.ReportedOutcomes);
				s_log.LogInformation("Match {MatchId}: rosterType={RosterType} reported={ReportedOutcomeCount} candidates=[{CandidateSlots}]",
					lobby.MatchID, strRosterType, lobby.ReportedOutcomes.Count, String.Join(",", lstCandidateSlots));

				DateTime latestLeave = DateTime.MinValue;
				MatchdataMemberModel? lastPlayerNullable = null;
				int lastSlot = -1;

				foreach (var kv in members)
				{
					var model = kv.Value;

					// Observers, AI/placeholder slots and players who conceded can never be "last to leave = winner"
					if (!lstCandidateSlots.Contains(kv.Key))
						continue;

					DateTime abandonTime = DateTime.MinValue;
					string abandonSource = "none";

					if (lobby.TimePlayerAbandonedIngame.TryGetValue(model.user_id, out DateTime ingameAbandon))
					{
						abandonTime = ingameAbandon;
						abandonSource = "IngameAbandon";
					}
					else if (lobby.TimeMemberLeft.TryGetValue(model.user_id, out DateTime leftAt) && leftAt > DateTime.UnixEpoch)
					{
						abandonTime = leftAt;
						abandonSource = "MemberLeft";
					}

					s_log.LogDebug("Match {MatchId}: slot={Slot} user={UserId} side={Side} team={Team} time={AbandonTime} src={AbandonSource}",
						lobby.MatchID, kv.Key, model.user_id, model.side, model.team, abandonTime, abandonSource);
					if (abandonTime > latestLeave)
					{
						latestLeave = abandonTime;
						lastPlayerNullable = model;
						lastSlot = kv.Key;
					}
				}

				// A cleanly exited player can have no timestamp; if conceding left exactly one candidate they win regardless
				if (lastPlayerNullable == null && lstCandidateSlots.Count == 1)
				{
					lastSlot = lstCandidateSlots[0];
					lastPlayerNullable = members[lastSlot];
					s_log.LogInformation("Match {MatchId}: sole remaining candidate slot={Slot} user={UserId} has no abandon timestamp, awarding anyway",
						lobby.MatchID, lastSlot, lastPlayerNullable.Value.user_id);
				}

				if (lastPlayerNullable == null)
				{
					s_log.LogWarning("Match {MatchId}: no valid abandon timestamps found, fully inconclusive, clearing won flags", lobby.MatchID);
					foreach (var kv in members)
					{
						if (kv.Value.side == Constants.OBSERVER_SIDE_VALUE)
							continue;

						await UpdateMatchHistorySetWinFlag(db, lobby.MatchID, kv.Key, false, cancellationToken);
					}
					return;
				}

				MatchdataMemberModel lastPlayer = lastPlayerNullable.Value;
				int winningTeam = lastPlayer.team;
				s_log.LogInformation("Match {MatchId}: winner selected, user={UserId} slot={Slot} team={Team} at={LeaveTime}",
					lobby.MatchID, lastPlayer.user_id, lastSlot, winningTeam, latestLeave);

				// The last player and their teammates win, everyone else loses
				foreach (var kv in members)
				{
					var model = kv.Value;
					if (model.side == Constants.OBSERVER_SIDE_VALUE)
						continue;

					bool isWinner = model.user_id == lastPlayer.user_id ||
						(winningTeam != -1 && model.team == winningTeam);

					s_log.LogDebug("Match {MatchId}: marking slot={Slot} user={UserId} as {Result}",
						lobby.MatchID, kv.Key, model.user_id, isWinner ? "WINNER" : "loser");
					await UpdateMatchHistorySetWinFlag(db, lobby.MatchID, kv.Key, isWinner, cancellationToken);
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "DetermineLobbyWinnerIfNotPresent failed");
				throw;
			}
		}

		public static async Task UpdateMatchHistorySetWinFlag(
			AppDbContext db,
			ulong matchId,
			int slotIndex,
			bool won,
			CancellationToken cancellationToken = default)
		{
			if (matchId == 0 || slotIndex < 0 || slotIndex >= SlotCount)
				return;

			try
			{
				string result = won ? MatchResults.Win : MatchResults.Loss;

				int updated = await db.MatchPlayers
					.Where(p => p.MatchId == (long)matchId && p.Slot == (short)slotIndex)
					.ExecuteUpdateAsync(s => s.SetProperty(p => p.Result, result), cancellationToken);

				if (updated != 1)
					throw new InvalidOperationException($"Match {matchId} slot {slotIndex} disappeared while its winner was being finalized.");
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "UpdateMatchHistorySetWinFlag failed");
				throw;
			}
		}

		// Finished matches by id ascending after afterID, finished at or after since (a poller never misses a match that started before its cursor); the newest first when neither is given
		public static async Task<List<MatchHistory_Entry>> GetMatchesPage(AppDbContext db, long? afterID, DateTime? since, int take, CancellationToken cancellationToken = default)
		{
			IQueryable<Match> query = db.Matches.AsNoTracking().Where(m => m.FinishedAt != null);
			if (afterID != null)
			{
				long after = afterID.Value;
				query = query.Where(m => m.Id > after);
			}
			if (since != null)
			{
				DateTime from = DateTime.SpecifyKind(since.Value, DateTimeKind.Utc);
				query = query.Where(m => m.FinishedAt >= from);
			}
			query = afterID == null && since == null ? query.OrderByDescending(m => m.Id) : query.OrderBy(m => m.Id);

			List<Match> matches = await query.Take(take).ToListAsync(cancellationToken);
			return await ToEntries(db, matches, cancellationToken);
		}

		// Called when a lobby is deleted, the true end of a match
		public static async Task FinalizeAndScheduleExternalPublication(
			AppDbContext db,
			GenOnlineService.Lobby lobby,
			CancellationToken cancellationToken = default)
		{
			if (lobby.MatchID == 0)
				return;

			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

			await FinishMatch(db, lobby, cancellationToken);
			await DetermineLobbyWinnerIfNotPresent(db, lobby, cancellationToken);
			await ScheduleExternalPublication(db, lobby.MatchID, cancellationToken);

			await transaction.CommitAsync(cancellationToken);
		}

		private static async Task FinishMatch(
			AppDbContext db,
			GenOnlineService.Lobby lobby,
			CancellationToken cancellationToken)
		{
			if (lobby.MatchID == 0)
				return;

			DateTime now = DateTime.UtcNow;

			await db.Matches
				.Where(m => m.Id == (long)lobby.MatchID && m.FinishedAt == null)
				.ExecuteUpdateAsync(s => s.SetProperty(m => m.FinishedAt, now), cancellationToken);

			bool matchExists = await db.Matches
				.AnyAsync(m => m.Id == (long)lobby.MatchID && m.FinishedAt != null, cancellationToken);

			if (!matchExists)
				throw new InvalidOperationException($"Finished match {lobby.MatchID} was not found.");
		}

		private static async Task ScheduleExternalPublication(
			AppDbContext db,
			ulong matchId,
			CancellationToken cancellationToken)
		{
			if (matchId == 0)
				return;

			bool exists = await db.MatchPublications.AnyAsync(p => p.MatchId == (long)matchId, cancellationToken);
			if (!exists)
			{
				db.MatchPublications.Add(new MatchPublication
				{
					MatchId = (long)matchId,
					NextAttemptAt = DateTime.UtcNow
				});
				await db.SaveChangesAsync(cancellationToken);
				db.ChangeTracker.Clear();
			}

			bool publicationIsDurable = await db.Matches.AnyAsync(
				m => m.Id == (long)matchId && m.FinishedAt != null,
				cancellationToken);
			publicationIsDurable &= await db.MatchPublications.AnyAsync(
				p => p.MatchId == (long)matchId,
				cancellationToken);

			if (!publicationIsDurable)
				throw new InvalidOperationException($"Match {matchId} could not be scheduled for external publication.");
		}

		public static async Task<List<ExternalPublicationWorkItem>> GetPendingExternalPublications(
			AppDbContext db,
			DateTime utcNow,
			int maxCount,
			CancellationToken cancellationToken)
		{
			List<MatchPublication> pending = await db.MatchPublications
				.Where(p => p.PublishedAt == null && p.NextAttemptAt != null && p.NextAttemptAt <= utcNow)
				.OrderBy(p => p.NextAttemptAt)
				.ThenBy(p => p.MatchId)
				.Take(maxCount)
				.ToListAsync(cancellationToken);

			return pending
				.Select(item => new ExternalPublicationWorkItem(item.MatchId, item.Attempts + 1))
				.ToList();
		}

		public static async Task<DateTime?> GetOldestUnpublishedMatchTime(
			AppDbContext db,
			CancellationToken cancellationToken)
		{
			return await (
				from publication in db.MatchPublications.AsNoTracking()
				join match in db.Matches.AsNoTracking() on publication.MatchId equals match.Id
				where publication.PublishedAt == null && match.FinishedAt != null
				select match.FinishedAt)
				.MinAsync(cancellationToken);
		}

		public static async Task MarkExternalPublicationSucceeded(
			AppDbContext db,
			ulong matchId,
			int attempt,
			CancellationToken cancellationToken)
		{
			if (matchId == 0)
				throw new ArgumentOutOfRangeException(nameof(matchId));

			int updated = await db.MatchPublications
				.Where(p => p.MatchId == (long)matchId && p.PublishedAt == null)
				.ExecuteUpdateAsync(s => s
					.SetProperty(p => p.PublishedAt, DateTime.UtcNow)
					.SetProperty(p => p.NextAttemptAt, (DateTime?)null)
					.SetProperty(p => p.Attempts, attempt)
					.SetProperty(p => p.LastError, (string?)null), cancellationToken);

			if (updated != 1)
				throw new InvalidOperationException($"Match {matchId} could not be acknowledged after external publication.");
		}

		public static async Task MarkExternalPublicationFailed(
			AppDbContext db,
			ulong matchId,
			int attempt,
			DateTime? nextAttemptAt,
			string? errorMessage,
			CancellationToken cancellationToken)
		{
			if (matchId == 0)
				throw new ArgumentOutOfRangeException(nameof(matchId));

			string? truncatedError = errorMessage;
			if (!string.IsNullOrEmpty(truncatedError) && truncatedError.Length > 512)
				truncatedError = truncatedError[..512];

			int updated = await db.MatchPublications
				.Where(p => p.MatchId == (long)matchId && p.PublishedAt == null)
				.ExecuteUpdateAsync(s => s
					.SetProperty(p => p.NextAttemptAt, nextAttemptAt)
					.SetProperty(p => p.Attempts, attempt)
					.SetProperty(p => p.LastError, truncatedError), cancellationToken);

			if (updated != 1)
				throw new InvalidOperationException($"Match {matchId} could not be rescheduled after publication failure.");
		}

		internal static async Task<MatchHistory_Entry?> LoadMatchHistoryEntryAsync(
			AppDbContext db,
			long matchId,
			CancellationToken cancellationToken = default)
		{
			Match? match = await db.Matches.AsNoTracking().FirstOrDefaultAsync(m => m.Id == matchId, cancellationToken);
			if (match == null)
				return null;

			List<MatchPlayer> players = await db.MatchPlayers.AsNoTracking()
				.Where(p => p.MatchId == matchId)
				.ToListAsync(cancellationToken);

			return ToEntry(match, players);
		}

		public static async Task AttachMatchHistoryMetadata(
			AppDbContext db,
			ulong matchId,
			int slotIndex,
			string fileName,
			EMetadataFileType fileType)
		{
			if (matchId == 0 || slotIndex < 0 || slotIndex >= SlotCount)
				return;

			try
			{
				MatchPlayer? player = await GetPlayer(db, (long)matchId, slotIndex);
				if (player == null)
					return;

				MatchPlayerDetails details = ReadDetails(player.Details);
				details.metadata ??= new List<MemberMetadataModel>();
				details.metadata.Add(new MemberMetadataModel
				{
					file_name = fileName,
					file_type = fileType
				});

				player.Details = WriteDetails(details);
				await db.SaveChangesAsync();
			}
			finally
			{
				db.ChangeTracker.Clear();
			}
		}

		public static async Task UpdateLeaderboardAndElo(AppDbContext db, GenOnlineService.Lobby lobby)
		{
			if (lobby.LobbyType != ELobbyType.QuickMatch)
				return;

			try
			{
				var members = (await LoadMembersBySlot(db, (long)lobby.MatchID)).Values
					.Where(m => m.side != Constants.OBSERVER_SIDE_VALUE && m.user_id > 0)
					.ToList();

				if (members.Count == 0)
					return;

				await UpdateCurrentEloAsync(db, members);
				await UpdatePeriodEloAndLeaderboardsAsync(db, members, lobby.TimeCreated.ToUniversalTime());
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "UpdateLeaderboardAndElo failed");
			}
		}

		private static async Task UpdateCurrentEloAsync(AppDbContext db, List<MatchdataMemberModel> members)
		{
			var userIds = members.Select(m => (long)m.user_id).ToList();
			var dictElo = await Database.Users.GetBulkELOData(db, userIds);

			foreach (var a in members)
			{
				foreach (var b in members)
				{
					if (a.user_id == b.user_id)
						continue;

					if (b.team == a.team && a.team != -1)
						continue;

					if (a.user_id >= b.user_id)
						continue;

					ref EloData A = ref CollectionsMarshal.GetValueRefOrAddDefault(dictElo, a.user_id, out _);
					ref EloData B = ref CollectionsMarshal.GetValueRefOrAddDefault(dictElo, b.user_id, out _);
					Elo.ApplyResult(ref A, ref B, a.won ? MatchResult.PlayerAWins : MatchResult.PlayerBWins);
				}
			}

			foreach (var m in members)
			{
				ref EloData data = ref CollectionsMarshal.GetValueRefOrAddDefault(dictElo, m.user_id, out _);
				data.NumMatches++;
			}

			foreach (var pair in dictElo)
			{
				long userId = pair.Key;
				EloData data = pair.Value;

				var shared = GenOnlineService.WebSocketManager.GetSharedDataForUser(userId);
				if (shared != null)
				{
					shared.GameStats.EloRating = data.Rating;
					shared.GameStats.EloMatches = data.NumMatches;
				}

				await Database.Users.SaveELOData(db, userId, data);
			}
		}

		private static async Task UpdatePeriodEloAndLeaderboardsAsync(AppDbContext db, List<MatchdataMemberModel> members, DateTime matchTimeUtc)
		{
			var userIds = members.Select(m => (long)m.user_id).ToList();
			var bulk = await Database.Leaderboards.GetBulkLeaderboardData(db, userIds, matchTimeUtc);

			var daily = new Dictionary<long, EloData>();
			var monthly = new Dictionary<long, EloData>();
			var yearly = new Dictionary<long, EloData>();

			foreach (var m in members)
			{
				var lb = bulk[m.user_id];
				daily[m.user_id] = new EloData(lb.daily, lb.daily_matches);
				monthly[m.user_id] = new EloData(lb.monthly, lb.monthly_matches);
				yearly[m.user_id] = new EloData(lb.yearly, lb.yearly_matches);
			}

			foreach (var a in members)
			{
				foreach (var b in members)
				{
					if (a.user_id == b.user_id)
						continue;

					if (b.team == a.team && a.team != -1)
						continue;

					if (a.user_id >= b.user_id)
						continue;

					MatchResult result = a.won ? MatchResult.PlayerAWins : MatchResult.PlayerBWins;

					foreach (var period in new[] { daily, monthly, yearly })
					{
						ref EloData A = ref CollectionsMarshal.GetValueRefOrAddDefault(period, a.user_id, out _);
						ref EloData B = ref CollectionsMarshal.GetValueRefOrAddDefault(period, b.user_id, out _);
						Elo.ApplyResult(ref A, ref B, result);
					}
				}
			}

			DateOnly day = LeaderboardPeriods.DayStart(matchTimeUtc);
			DateOnly month = LeaderboardPeriods.MonthStart(matchTimeUtc);
			DateOnly year = LeaderboardPeriods.YearStart(matchTimeUtc);

			foreach (var m in members)
			{
				long userId = m.user_id;
				int wins = m.won ? 1 : 0;
				int losses = m.won ? 0 : 1;

				foreach (var (period, start, points) in new[]
				{
					(LeaderboardPeriods.Day, day, daily[userId].Rating),
					(LeaderboardPeriods.Month, month, monthly[userId].Rating),
					(LeaderboardPeriods.Year, year, yearly[userId].Rating)
				})
				{
					await db.LeaderboardEntries
						.Where(x => x.UserId == userId && x.Period == period && x.PeriodStart == start)
						.ExecuteUpdateAsync(s => s
							.SetProperty(x => x.Points, points)
							.SetProperty(x => x.Wins, x => x.Wins + wins)
							.SetProperty(x => x.Losses, x => x.Losses + losses));
				}
			}
		}
	}

	// Faction win rates of the current UTC day, derived from finished matches and cached for the GlobalStats endpoint
	public static class FactionStats
	{
		private static readonly ILogger s_log = AppLog.For(typeof(FactionStats));

		// Playable sides are 2..13, the array index is side - 2
		public const int FirstSide = 2;

		private static FactionStatsStructure s_current = new();

		public static FactionStatsStructure Current => Volatile.Read(ref s_current);

		public static async Task Refresh(AppDbContext db, DateTime utcNow, CancellationToken cancellationToken = default)
		{
			try
			{
				Volatile.Write(ref s_current, await Compute(db, utcNow, cancellationToken));
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "FactionStats.Refresh failed");
				throw;
			}
		}

		public static async Task<FactionStatsStructure> Compute(AppDbContext db, DateTime utcNow, CancellationToken cancellationToken = default)
		{
			DateTime dayStart = DateTime.SpecifyKind(utcNow.Date, DateTimeKind.Utc);
			int lastSide = FirstSide + FactionStatsStructure.numSides - 1;

			var rows = await (
				from player in db.MatchPlayers.AsNoTracking()
				join match in db.Matches.AsNoTracking() on player.MatchId equals match.Id
				where match.FinishedAt != null && match.FinishedAt >= dayStart
					&& player.UserId != null && player.Result != null
					&& player.Side >= FirstSide && player.Side <= lastSide
				group player by player.Side into bySide
				select new
				{
					Side = bySide.Key!.Value,
					Matches = bySide.Count(),
					Wins = bySide.Count(p => p.Result == MatchResults.Win)
				})
				.ToListAsync(cancellationToken);

			var stats = new FactionStatsStructure();
			foreach (var row in rows)
			{
				stats.matches[row.Side - FirstSide] = row.Matches;
				stats.wins[row.Side - FirstSide] = row.Wins;
			}

			return stats;
		}
	}
}
