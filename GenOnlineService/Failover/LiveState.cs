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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenOnlineService.Failover
{
	// Realtime:failover:live_state, read only when failover and the Redis backplane are both on
	public sealed class LiveStateSettings
	{
		// Each lobby is written at least this often when nothing marked it changed, a write happens only when its state differs
		public int RefreshMs { get; init; } = 2000;

		// XADD MAXLEN ~ of the change stream
		public int StreamMaxLength { get; init; } = 10000;

		// The standby reads the stream this often while it is caught up
		public int PollIntervalMs { get; init; } = 100;

		// Zero by default: compare on write keeps a fenced copy's late write out, a positive value writes every entity again that often anyway
		public int RewriteMs { get; init; }

		public static LiveStateSettings Read(IConfiguration configuration)
		{
			IConfigurationSection section = configuration.GetSection("Realtime:failover:live_state");
			LiveStateSettings settings = new()
			{
				RefreshMs = section.GetValue("refresh_ms", 2000),
				StreamMaxLength = section.GetValue("stream_max_length", 10000),
				PollIntervalMs = section.GetValue("poll_interval_ms", 100),
				RewriteMs = section.GetValue("rewrite_ms", 0),
			};
			if (settings.RefreshMs < 100 || settings.StreamMaxLength < 100 || settings.PollIntervalMs < 10 || (settings.RewriteMs != 0 && settings.RewriteMs < settings.RefreshMs))
			{
				throw new InvalidOperationException("Realtime:failover:live_state needs refresh_ms of at least 100, stream_max_length of at least 100, poll_interval_ms of at least 10 and rewrite_ms of 0 or at least refresh_ms.");
			}
			return settings;
		}
	}

	// The entity keys of the state hash and the change stream
	public static class LiveKeys
	{
		public const string Queue = "queue";
		public const string LobbyPrefix = "lobby:";

		public static string Lobby(long lobbyID) => LobbyPrefix + lobbyID;

		public static bool IsLobby(string key) => key.StartsWith(LobbyPrefix, StringComparison.Ordinal);

		// A queue ticket a handover sends after the queue moved, one per player
		public const string TicketPrefix = "ticket:";

		public static string Ticket(long userID) => TicketPrefix + userID;

		public static bool IsTicket(string key) => key.StartsWith(TicketPrefix, StringComparison.Ordinal);

		// A leave from a lobby during its move, applied by the copy that takes the lobby
		public const string LeavePrefix = "leave:";

		public static string Leave(long lobbyID, long userID) => LeavePrefix + lobbyID + ":" + userID;

		public static bool IsLeave(string key) => key.StartsWith(LeavePrefix, StringComparison.Ordinal);

		// A lobby is written new at epoch 1, a ticket or a leave is only ever transferred new at epoch 1
		public static bool IsNew(string key, bool transfer) => transfer ? IsTicket(key) || IsLeave(key) : IsLobby(key);
	}

	// One stored entity with owner, owner epoch, the writing copy and an entity version per owner, formats change additively and readers ignore unknown fields
	public sealed record LiveEnvelope
	{
		public const int CurrentVersion = 1;

		// zero when the field is missing, which makes the entity unreadable
		public int V { get; init; }
		public string Key { get; init; } = String.Empty;
		public string Owner { get; init; } = String.Empty;
		public long Epoch { get; init; }
		public long Version { get; init; }
		public long WrittenAtMs { get; init; }

		// the copy that wrote it, the old owner while it hands the entity to a new one
		public string WrittenBy { get; init; } = String.Empty;
		public JsonElement Data { get; init; }
	}

	// One accepted change: a write by the owner, a delete, or a transfer that gives the entity to a new owner with the next owner epoch
	public enum LiveOp
	{
		Set,
		Delete,
		Transfer
	}

	// A stream entry: seq counts the accepted writes of each copy, epoch and owner are the entity's after the change
	public sealed record LiveStreamEntry(string ID, LiveOp Op, string Key, string Copy, long Seq, long Epoch, string Owner, long Version, string? Body);

	// The state of the environment as loaded by a standby: every stored entity, each copy's write count and the stream position, read in one transaction
	public sealed record LiveSnapshot(IReadOnlyDictionary<string, string> Entities, IReadOnlyDictionary<string, long> Seqs, string Position);

	public static class LiveJson
	{
		public static readonly JsonSerializerOptions Options = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
			DictionaryKeyPolicy = null,
			DefaultIgnoreCondition = JsonIgnoreCondition.Never,
			NumberHandling = JsonNumberHandling.AllowReadingFromString,
			WriteIndented = false
		};

		// The envelope around data that is already JSON, so a lobby is serialized once
		public static string Envelope(string key, string owner, long epoch, long version, string dataJson, string? writtenBy = null)
		{
			System.Buffers.ArrayBufferWriter<byte> buffer = new(dataJson.Length + 256);
			using (Utf8JsonWriter writer = new(buffer))
			{
				writer.WriteStartObject();
				writer.WriteNumber("v", LiveEnvelope.CurrentVersion);
				writer.WriteString("key", key);
				writer.WriteString("owner", owner);
				writer.WriteNumber("epoch", epoch);
				writer.WriteNumber("version", version);
				writer.WriteNumber("written_at_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
				writer.WriteString("written_by", writtenBy ?? owner);
				writer.WritePropertyName("data");
				writer.WriteRawValue(dataJson, skipInputValidation: true);
				writer.WriteEndObject();
			}
			return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
		}

		public static string Data(object data) => JsonSerializer.Serialize(data, data.GetType(), Options);

		// Null for anything unreadable: bad JSON, a missing or zero version, a missing key or no data
		public static LiveEnvelope? ReadEnvelope(string json)
		{
			try
			{
				LiveEnvelope? envelope = JsonSerializer.Deserialize<LiveEnvelope>(json, Options);
				if (envelope == null || envelope.V < 1 || envelope.Key.Length == 0 || envelope.Data.ValueKind != JsonValueKind.Object)
				{
					return null;
				}
				return envelope;
			}
			catch (JsonException)
			{
				return null;
			}
		}

		public static T? ReadData<T>(LiveEnvelope envelope) where T : class
		{
			try
			{
				return envelope.Data.Deserialize<T>(Options);
			}
			catch (JsonException)
			{
				return null;
			}
		}
	}

	// The build of a lobby or a queue ticket
	public sealed record LiveBuild
	{
		public string Game { get; init; } = String.Empty;
		public uint ExeCrc { get; init; }
		public uint IniCrc { get; init; }
		public int AnticheatId { get; init; } = -1;
	}

	// One lobby, version 1. Lobby chat lives in the database and is not part of it.
	public sealed record LobbyLive
	{
		public long Id { get; init; }
		public int Type { get; init; }
		public int State { get; init; }
		public long Owner { get; init; }
		public string Name { get; init; } = String.Empty;
		public string MapName { get; init; } = String.Empty;
		public string MapPath { get; init; } = String.Empty;
		public bool MapOfficial { get; init; }
		public ulong MatchId { get; init; }
		public DateTime CreatedAt { get; init; }
		public bool VanillaTeams { get; init; }
		public uint StartingCash { get; init; }
		public bool LimitSuperweapons { get; init; }
		public bool TrackStats { get; init; }
		public bool Passworded { get; init; }
		public string Password { get; init; } = String.Empty;
		public bool AllowObservers { get; init; }

		// absent in a capture of an older copy, which never limited the side
		public bool AllowArmySelection { get; init; } = true;
		public int PlaylistId { get; init; }
		public LiveBuild Build { get; init; } = new();
		public short NetworkRoomId { get; init; } = -1;
		public int RngSeed { get; init; }
		public string Region { get; init; } = String.Empty;
		public double Latitude { get; init; }
		public double Longitude { get; init; }
		public ushort MaxCameraHeight { get; init; }
		public int Joinability { get; init; }
		public List<LobbyMemberLive> Slots { get; init; } = new();

		// every player who was ever a member, connection reports may name a former member
		public List<long> EverMembers { get; init; } = new();
		public Dictionary<string, DateTime> MemberLeftAt { get; init; } = new();
		public List<long> MatchedUserIds { get; init; } = new();

		// none, mesh_check or countdown, with the countdown deadline as wall clock time
		public string StartPhase { get; init; } = "none";
		public long? CountdownEndsAtMs { get; init; }
		public LobbyStartLive? Start { get; init; }
		public LobbyMeshLive Mesh { get; init; } = new();

		// reporter and peer of each reported connection
		public List<long[]> ReportedConnections { get; init; } = new();
		public List<PeerPairLive> PeerPairs { get; init; } = new();
		public LobbyCountsLive? AtStart { get; init; }
		public Dictionary<string, DateTime> AbandonedInGame { get; init; } = new();
		public Dictionary<string, bool> ReportedOutcomes { get; init; } = new();
		public bool ProbesEnabled { get; init; }
	}

	public sealed record LobbyMemberLive
	{
		public int SlotIndex { get; init; }
		public int SlotState { get; init; }
		public long UserId { get; init; } = -1;
		public string DisplayName { get; init; } = String.Empty;
		public string NameNotDeduped { get; init; } = String.Empty;
		public int NameTag { get; init; }
		public bool Ready { get; init; }
		public ushort Port { get; init; }
		public int Side { get; init; }
		public int Team { get; init; } = -1;
		public int Color { get; init; }
		public int StartPosition { get; init; }
		public bool HasMap { get; init; }
		public string Region { get; init; } = String.Empty;
	}

	public sealed record LobbyStartLive
	{
		public long MatchId { get; init; }
		public int RngSeed { get; init; }
		public Dictionary<string, string?> ScreenshotUrls { get; init; } = new();
	}

	// The mesh check bookkeeping as it was, a restored lobby never accepts an answer to it
	public sealed record LobbyMeshLive
	{
		public long CheckId { get; init; }
		public int Attempt { get; init; }
		public bool? LastOutcome { get; init; }
		public string? LastFailureReason { get; init; }
		public bool Pending { get; init; }
	}

	public sealed record PeerPairLive
	{
		public long Low { get; init; }
		public long High { get; init; }
		public int Attempt { get; init; }
		public int Step { get; init; }
	}

	public sealed record LobbyCountsLive
	{
		public int Humans { get; init; }
		public int Ai { get; init; }
		public int Open { get; init; }
		public int Closed { get; init; }
	}

	// The matchmaking queue, version 1: every ticket, and the buckets with their widening and matched setups in progress
	public sealed record QueueLive
	{
		public List<QueueTicketLive> Tickets { get; init; } = new();
		public List<QueueBucketLive> Buckets { get; init; } = new();
	}

	public sealed record LobbyLeaveLive
	{
		public long LobbyId { get; init; }
		public long UserId { get; init; }
	}

	public sealed record QueueTicketLive
	{
		public long UserId { get; init; }
		public int PlaylistId { get; init; }
		public List<int> MapIndexes { get; init; } = new();
		public LiveBuild Build { get; init; } = new();
		public long QueuedAtMs { get; init; }

		// the index into buckets, -1 while the ticket waits to be sorted into one
		public int Bucket { get; init; } = -1;
	}

	public sealed record QueueBucketLive
	{
		public int Index { get; init; }
		public int PlaylistId { get; init; }
		public List<int> MapIndexes { get; init; } = new();
		public LiveBuild Build { get; init; } = new();
		public int EloExpansionIteration { get; init; } = 1;

		// searching, creating, joining, mesh_check, countdown or committed
		public string Phase { get; init; } = "searching";
		public long LobbyId { get; init; } = -1;
		public List<long> Members { get; init; } = new();
	}
}
