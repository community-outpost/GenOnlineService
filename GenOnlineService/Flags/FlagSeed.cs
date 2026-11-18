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

using GenOnlineService.Realtime;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenOnlineService.Flags
{
	// Revision 1: the game's network tuning that data/serviceconfig.json held, as client.<group>.<name> flags.
	// Runs once, on a database with no flags and no revisions.
	public static class FlagSeed
	{
		private static readonly ILogger s_log = AppLog.For(typeof(FlagSeed));

		public const string Actor = "system";
		public const string Comment = "Seeded from data/serviceconfig.json";

		public const string ServiceConfig = """
		{
			"connection": {
				"relay_only": false,
				"ice_engine": "webrtc",
				"use_mapped_port": true,
				"retry_signalling": true,
				"reconnect_timeout_ms": 30000,
				"anticheat_transport": false,
				"probe_lobby_interval_ms": 250,
				"probe_match_interval_ms": 1000,
				"wifi_low_latency": true
			},
			"send": {
				"mode": "datagram",
				"direct": true,
				"frames_per_packet": 2,
				"frames_per_packet_lossy": 1,
				"flush_every_frame": true,
				"redundancy": true,
				"redundancy_window_ms": 0,
				"redundancy_burst_ms": 150,
				"fast_resend": true
			},
			"input_delay": {
				"controller": true,
				"margin_frames": 1,
				"min_frames": 0,
				"max_frames": 64,
				"fast_recovery": true,
				"decision_interval_ms": 1000,
				"fps_history_samples": 30,
				"catch_up_frames": 1,
				"slack_percent": 30,
				"formula": {
					"latency_window_ms": 0,
					"decrease_after_decisions": 20,
					"repair_budget": true
				}
			},
			"disconnect": {
				"freeze_before_screen_ms": 5000,
				"remove_after_ms": 60000
			},
			"lobby": {
				"host_migration": false
			},
			"telemetry": {
				"screenshot_probes": true,
				"replay_upload": true,
				"screenshot_width": 1440,
				"screenshot_height": 700,
				"screenshot_upload_bytes_per_sec": 131072,
				"screenshot_slot_offset_ms": 2000
			}
		}
		""";

		// One flag per leaf of a grouped config object: key client.<path>, group from the path, type from the value
		public static List<FlagDefinition> FromGroupedConfig(JsonObject config)
		{
			var flags = new List<FlagDefinition>();
			Walk(config, [], flags);
			return flags;
		}

		private static void Walk(JsonObject node, List<string> path, List<FlagDefinition> flags)
		{
			foreach ((string name, JsonNode? child) in node)
			{
				if (child is JsonObject group)
				{
					Walk(group, [.. path, name], flags);
					continue;
				}
				if (child == null)
				{
					continue;
				}

				JsonElement value = Flagd.Element(child);
				(string label, string? unit) = FlagRules.LabelOf(name);
				flags.Add(new FlagDefinition
				{
					Key = "client." + String.Join('.', path.Append(name)),
					Scope = FlagScopes.Client,
					Label = label,
					Group = path.Count == 0 ? "General" : String.Join(" / ", path.Select(p => FlagRules.LabelOf(p).Label)),
					Type = value.ValueKind switch
					{
						JsonValueKind.True or JsonValueKind.False => FlagTypes.Bool,
						JsonValueKind.Number => value.TryGetInt64(out _) ? FlagTypes.Int : FlagTypes.Number,
						_ => FlagTypes.String
					},
					Unit = unit,
					Value = value
				});
			}
		}

		public static async Task EnsureAsync(AppDbContext db)
		{
			if (await db.Flags.AnyAsync() || await db.FlagRevisions.AnyAsync())
			{
				return;
			}

			var actor = new Database.ModerationActor { Kind = AuditActorKinds.Cli, Name = Actor, Source = ModerationSources.Cli };
			List<FlagDefinition> flags = FromGroupedConfig((JsonObject)JsonNode.Parse(ServiceConfig)!);
			JsonObject document = Flagd.ToDocument(flags);
			try
			{
				(_, Failure? failure) = await FlagStore.Import(db, document, actor);
				if (failure == null)
				{
					(_, failure) = await FlagStore.Publish(db, Comment, null, actor);
				}
				if (failure != null)
				{
					s_log.LogError("Seeding the flags failed: {Code}", failure.Value.Code);
				}
			}
			catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex))
			{
				// another copy seeded at the same moment
			}
		}
	}
}
