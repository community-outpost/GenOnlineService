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

namespace GenOnlineService
{
	// A stored match with its slots, as the external leaderboard publication and the /api/v1/matches routes read it
	public class MatchHistory_Entry
	{
		public MatchHistory_Entry(
		Int64 match_id,
		Int64 owner_user_id,
		string lobby_name,
		bool is_finished,
		string time_started,
		string time_finished,
		string map_name,
		string map_path,
		string match_roster_type,
		ELobbyType? lobby_type,
		bool is_official_map,
		bool vanilla_teams_only,
		UInt32 starting_cash,
		bool is_limit_superweapons,
		bool is_tracking_stats,
		bool allow_observers,
		UInt16 max_camera_height,
		UInt32 exe_crc,
		UInt32 ini_crc
		)
		{
			this.match_id = match_id;
			this.owner_user_id = owner_user_id;
			this.lobby_name = lobby_name;
			this.is_finished = is_finished;
			this.time_started = time_started;
			this.time_finished = time_finished;
			this.map_name = map_name;
			this.map_path = map_path;
			this.match_roster_type = match_roster_type;
			this.lobby_type = lobby_type;
			this.is_official_map = is_official_map;
			this.vanilla_teams_only = vanilla_teams_only;
			this.starting_cash = starting_cash;
			this.is_limit_superweapons = is_limit_superweapons;
			this.is_tracking_stats = is_tracking_stats;
			this.allow_observers = allow_observers;
			this.max_camera_height = max_camera_height;
			this.exe_crc = exe_crc;
			this.ini_crc = ini_crc;
		}

		public Int64 match_id { get; set; } = -1;
		public Int64 owner_user_id { get; set; } = -1;
		public string lobby_name { get; set; } = String.Empty;
		public bool is_finished { get; set; } = false;
		public string time_started { get; set; } = String.Empty;
		public string time_finished { get; set; } = String.Empty;
		public string map_name { get; set; } = String.Empty;
		public string map_path { get; set; } = String.Empty;

		public string match_roster_type { get; set; } = String.Empty;
		public ELobbyType? lobby_type { get; set; } = null;
		public bool is_official_map { get; set; } = false;
		public bool vanilla_teams_only { get; set; } = false;
		public UInt32 starting_cash { get; set; } = 0;
		public bool is_limit_superweapons { get; set; } = false;
		public bool is_tracking_stats { get; set; } = false;
		public bool allow_observers { get; set; } = false;
		public UInt16 max_camera_height { get; set; } = 0;
		public UInt32 exe_crc { get; set; } = 0;
		public UInt32 ini_crc { get; set; } = 0;

		public List<MatchdataMemberModel?> members { get; private set; } = new();
	}
}
