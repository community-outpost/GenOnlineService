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

using GenOnlineService.Api;
using GenOnlineService.Realtime;
using System.Globalization;

namespace GenOnlineService.Rest
{
	public enum MatchFileType { Unknown, Screenshot, Replay }

	public sealed record MatchFile
	{
		public string Name { get; init; } = "";
		public MatchFileType Type { get; init; }
	}

	// One of the 8 slots of a match in slot order; user_id is null for open, closed and AI slots and for removed accounts
	public sealed record MatchSlot
	{
		public SlotState State { get; init; }
		public long? UserId { get; init; }
		public string Name { get; init; } = "";
		public int Side { get; init; }
		public int Color { get; init; }
		public int? Team { get; init; }
		public int StartPosition { get; init; }
		public bool Won { get; init; }
		public bool Desynced { get; init; }
		public uint? DurationS { get; init; }
		public int BuildingsBuilt { get; init; }
		public int BuildingsKilled { get; init; }
		public int BuildingsLost { get; init; }
		public int UnitsBuilt { get; init; }
		public int UnitsKilled { get; init; }
		public int UnitsLost { get; init; }
		public int TotalMoney { get; init; }
		public IReadOnlyList<MatchFile> Files { get; init; } = [];
	}

	// GET /api/v1/matches/:id; finished_at is null while the match runs
	[ApiFixture("get-matches-item.response")]
	public sealed record MatchResponse
	{
		public long Id { get; init; }
		public long? OwnerId { get; init; }
		public string Name { get; init; } = "";
		public LobbyType Type { get; init; }
		public string RosterType { get; init; } = "";
		public MapRef Map { get; init; } = new MapRef();
		public LobbySettings Settings { get; init; } = new LobbySettings();
		public BuildIdentity Build { get; init; } = new BuildIdentity();
		public DateTime StartedAt { get; init; }
		public DateTime? FinishedAt { get; init; }
		public IReadOnlyList<MatchSlot> Slots { get; init; } = [];

		public static MatchResponse Of(MatchHistory_Entry entry)
		{
			return new MatchResponse
			{
				Id = entry.match_id,
				OwnerId = entry.owner_user_id > 0 ? entry.owner_user_id : null,
				Name = entry.lobby_name,
				Type = entry.lobby_type == ELobbyType.QuickMatch ? LobbyType.QuickMatch : LobbyType.Custom,
				RosterType = entry.match_roster_type,
				Map = new MapRef { Name = entry.map_name, Path = entry.map_path, Official = entry.is_official_map },
				Settings = new LobbySettings
				{
					StartingCash = (int)entry.starting_cash,
					LimitSuperweapons = entry.is_limit_superweapons,
					VanillaTeams = entry.vanilla_teams_only,
					TrackStats = entry.is_tracking_stats,
					AllowObservers = entry.allow_observers,
					MaxCameraHeight = entry.max_camera_height
				},
				Build = new BuildIdentity { ExeCrc = entry.exe_crc, IniCrc = entry.ini_crc },
				StartedAt = ParseTime(entry.time_started),
				FinishedAt = entry.is_finished ? ParseTime(entry.time_finished) : null,
				Slots = entry.members.Where(member => member != null).Select(member => SlotOf(member!.Value)).ToArray()
			};
		}

		private static DateTime ParseTime(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

		private static MatchSlot SlotOf(MatchdataMemberModel member)
		{
			return new MatchSlot
			{
				State = RealtimeLobbies.SlotStateOf(member.slot_state),
				UserId = member.user_id > 0 ? member.user_id : null,
				Name = member.display_name,
				Side = member.side,
				Color = member.color,
				Team = member.team < 0 ? null : member.team,
				StartPosition = member.startpos,
				Won = member.won,
				Desynced = member.desynced,
				DurationS = member.duration,
				BuildingsBuilt = member.buildings_built,
				BuildingsKilled = member.buildings_killed,
				BuildingsLost = member.buildings_lost,
				UnitsBuilt = member.units_built,
				UnitsKilled = member.units_killed,
				UnitsLost = member.units_lost,
				TotalMoney = member.total_money,
				Files = (member.metadata ?? new List<MemberMetadataModel>()).Select(file => new MatchFile
				{
					Name = file.file_name ?? "",
					Type = file.file_type switch
					{
						EMetadataFileType.FILE_TYPE_SCREENSHOT => MatchFileType.Screenshot,
						EMetadataFileType.FILE_TYPE_REPLAY => MatchFileType.Replay,
						_ => MatchFileType.Unknown
					}
				}).ToArray()
			};
		}
	}

	// GET /api/v1/matches
	[ApiFixture("get-matches.response")]
	public sealed record MatchListResponse
	{
		public IReadOnlyList<MatchResponse> Matches { get; init; } = [];
	}
}
