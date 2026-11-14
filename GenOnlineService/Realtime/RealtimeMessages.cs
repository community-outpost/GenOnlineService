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

using System.Text.Json.Serialization;

// Wire types of realtime protocol v1, see docs/realtime-protocol.md and docs/realtime-fixtures
namespace GenOnlineService.Realtime
{
	// Enums travel as snake_case strings

	public enum ClientType { Game, Launcher, Chat }
	public enum MessageType { Chat, Action, Announcement }
	public enum PresenceActivity { Offline, Menu, Channel, Lobby, Matchmaking, InGame }
	public enum ModerationAction { Kick, Ban, Mute }
	public enum SlotState { Open, Closed, Player, AiEasy, AiMedium, AiBrutal }
	public enum LobbyType { Custom, QuickMatch }
	public enum LobbyStage { Setup, Countdown, InGame, Complete }
	public enum LobbyVisibility { Public, Friends }
	public enum AnticheatKind { None, GoIntegrated, EasyAnticheat }
	public enum LobbyLeftReason { Left, Kicked, Closed }
	public enum CountdownCancelReason { PlayerNotReady, MeshIncomplete, PlayerLeft, SettingsChanged }
	public enum MatchRequeueReason { PlayerLeft, LobbyGone, MeshIncomplete, JoinTimeout, SetupFailed }

	// Shared objects

	public sealed record Empty
	{
		public static readonly Empty Instance = new Empty();
	}

	public sealed record Member
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public IReadOnlyList<string> Badges { get; init; } = [];
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public ClientType? Client { get; init; }
	}

	// A chat message as MessageCreate and history carry it, a tombstone has only id, channel_id, created_at and deleted
	public sealed record Message
	{
		public string Id { get; init; } = "";
		public string ChannelId { get; init; } = "";
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public MessageType? Type { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public Member? Author { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Content { get; init; }
		public DateTime CreatedAt { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
		public bool Deleted { get; init; }
	}

	public sealed record FriendMember
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public IReadOnlyList<string> Badges { get; init; } = [];
		public bool Online { get; init; }
		public PresenceActivity Activity { get; init; }
		public long? ChannelId { get; init; }
	}

	public sealed record BadgeDefinition
	{
		public string Text { get; init; } = "";
		public string Color { get; init; } = "";
	}

	public sealed record TurnCredentials
	{
		public string Username { get; init; } = "";
		public string Token { get; init; } = "";
	}

	public sealed record MapRef
	{
		public string Name { get; init; } = "";
		public string Path { get; init; } = "";
		public bool Official { get; init; }
	}

	public sealed record LobbySettings
	{
		public int StartingCash { get; init; }
		public bool LimitSuperweapons { get; init; }
		public bool VanillaTeams { get; init; }
		public bool TrackStats { get; init; }
		public bool AllowObservers { get; init; }
		public int MaxCameraHeight { get; init; }
	}

	public sealed record Slot
	{
		public int Index { get; init; }
		public SlotState State { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public Member? Member { get; init; }
		public bool Ready { get; init; }
		public bool HasMap { get; init; }
		public int Side { get; init; }
		public int Color { get; init; }
		public int? Team { get; init; }
		public int StartPosition { get; init; }
	}

	public sealed record LobbySummaryMap
	{
		public string Name { get; init; } = "";
		public bool Official { get; init; }
	}

	public sealed record LobbySummarySettings
	{
		public int StartingCash { get; init; }
		public bool LimitSuperweapons { get; init; }
		public bool VanillaTeams { get; init; }
		public bool TrackStats { get; init; }
		public bool AllowObservers { get; init; }
	}

	public sealed record BuildIdentity
	{
		public uint ExeCrc { get; init; }
		public uint IniCrc { get; init; }
	}

	public sealed record LobbySummary
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public Member Host { get; init; } = new Member();
		public LobbyStage State { get; init; }
		public LobbySummaryMap Map { get; init; } = new LobbySummaryMap();
		public int Players { get; init; }
		public int MaxPlayers { get; init; }
		public bool HasPassword { get; init; }
		public string Region { get; init; } = "";
		public int LatencyMs { get; init; }
		public AnticheatKind Anticheat { get; init; }
		public LobbySummarySettings Settings { get; init; } = new LobbySummarySettings();
		public BuildIdentity Build { get; init; } = new BuildIdentity();
		public DateTime CreatedAt { get; init; }
	}

	public sealed record LobbyInfo
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public long HostId { get; init; }
		public LobbyType Type { get; init; }
		public LobbyStage State { get; init; }
		public LobbyVisibility Visibility { get; init; }
		public bool HasPassword { get; init; }
		public MapRef Map { get; init; } = new MapRef();
		public string Region { get; init; } = "";
		public AnticheatKind Anticheat { get; init; }
		public LobbySettings Settings { get; init; } = new LobbySettings();
		public long? MatchId { get; init; }
		public DateTime CreatedAt { get; init; }
		public IReadOnlyList<Slot> Slots { get; init; } = [];
	}

	public sealed record PlaylistMap
	{
		public int Index { get; init; }
		public string Name { get; init; } = "";
		public string Path { get; init; } = "";
		public bool Custom { get; init; }
		public int MaxPlayers { get; init; }
	}

	public sealed record Playlist
	{
		public int Id { get; init; }
		public string Name { get; init; } = "";
		public int MinPlayers { get; init; }
		public int DesiredPlayers { get; init; }
		public int MinSelectedMaps { get; init; }
		public bool AllowTeams { get; init; }
		public int? TeamSize { get; init; }
		public bool AllowArmySelection { get; init; }
		public long GracePeriodS { get; init; }
		public IReadOnlyList<PlaylistMap> Maps { get; init; } = [];
	}

	public sealed record ChannelInfo
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public long? ParentId { get; init; }
		public bool ShowAllMatches { get; init; }
	}

	public sealed record ChatRate
	{
		public int Messages { get; init; }
		public long WindowS { get; init; }
	}

	public sealed record Limits
	{
		public int ChatMaxLength { get; init; }
		public ChatRate ChatRate { get; init; } = new ChatRate();
		public int NameMinLength { get; init; }
		public int NameMaxLength { get; init; }
	}

	public sealed record MeshPair
	{
		public long SourceUserId { get; init; }
		public long TargetUserId { get; init; }
	}

	// Requests (one argument object per hub method)

	public sealed record UserRequest
	{
		public long UserId { get; init; }
	}

	public sealed record JoinChannelRequest
	{
		public long ChannelId { get; init; }
	}

	public sealed record SendMessageRequest
	{
		public string ChannelId { get; init; } = "";
		public MessageType Type { get; init; }
		public string Content { get; init; } = "";
	}

	public sealed record GetHistoryRequest
	{
		public string ChannelId { get; init; } = "";
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Before { get; init; }
		public int Limit { get; init; }
	}

	public sealed record MarkReadRequest
	{
		public string ChannelId { get; init; } = "";
		public string MessageId { get; init; } = "";
	}

	// DeleteMessage and ReportMessage
	public sealed record MessageActionRequest
	{
		public string MessageId { get; init; } = "";
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Reason { get; init; }
	}

	public sealed record SetDisplayNameRequest
	{
		public string Name { get; init; } = "";
	}

	public sealed record CreateLobbyRequest
	{
		public string Name { get; init; } = "";
		public MapRef Map { get; init; } = new MapRef();
		public int MaxPlayers { get; init; }
		public LobbyVisibility Visibility { get; init; }
		public string? Password { get; init; }
		public int PreferredPort { get; init; }
		public LobbySettings Settings { get; init; } = new LobbySettings();
	}

	public sealed record JoinLobbyRequest
	{
		public long LobbyId { get; init; }
		public string? Password { get; init; }
		public int PreferredPort { get; init; }
		public bool HasMap { get; init; }
	}

	public sealed record UpdateLobbyMap
	{
		public string Name { get; init; } = "";
		public string Path { get; init; } = "";
		public bool Official { get; init; }
		public int MaxPlayers { get; init; }
	}

	public sealed record UpdateLobbySettings
	{
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public int? StartingCash { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public bool? LimitSuperweapons { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public int? MaxCameraHeight { get; init; }
	}

	public sealed record UpdateLobbyRequest
	{
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public UpdateLobbyMap? Map { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public UpdateLobbySettings? Settings { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public LobbyVisibility? Visibility { get; init; }
	}

	public sealed record SetLobbyPasswordRequest
	{
		public string? Password { get; init; }
	}

	public sealed record UpdateMySlotRequest
	{
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public int? Side { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public int? Color { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
		public Optional<int?> Team { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public int? StartPosition { get; init; }
	}

	public sealed record SetHasMapRequest
	{
		public bool HasMap { get; init; }
	}

	public sealed record SetReadyRequest
	{
		public bool Ready { get; init; }
	}

	public sealed record SetSlotStateRequest
	{
		public int Index { get; init; }
		public SlotState State { get; init; }
	}

	public sealed record SlotPatch
	{
		public int Index { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public int? Side { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public int? Color { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
		public Optional<int?> Team { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public int? StartPosition { get; init; }
	}

	public sealed record UpdateSlotsRequest
	{
		public IReadOnlyList<SlotPatch> Slots { get; init; } = [];
	}

	public sealed record MatchStats
	{
		public int BuildingsBuilt { get; init; }
		public int BuildingsKilled { get; init; }
		public int BuildingsLost { get; init; }
		public int UnitsBuilt { get; init; }
		public int UnitsKilled { get; init; }
		public int UnitsLost { get; init; }
		public long TotalMoney { get; init; }
	}

	public sealed record ReportMatchOutcomeRequest
	{
		public long MatchId { get; init; }
		public bool Won { get; init; }
		public bool Desynced { get; init; }
		public int Side { get; init; }
		public int DurationS { get; init; }
		public MatchStats Stats { get; init; } = new MatchStats();
	}

	public sealed record JoinQueueRequest
	{
		public int PlaylistId { get; init; }
		public IReadOnlyList<int> MapIndexes { get; init; } = [];
	}

	public sealed record ModerationRequest
	{
		public long UserId { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Reason { get; init; }
	}

	public sealed record MuteRequest
	{
		public long UserId { get; init; }
		public long? DurationS { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Reason { get; init; }
	}

	public sealed record BanRequest
	{
		public long UserId { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public long? DurationS { get; init; }
		public string? Reason { get; init; }
	}

	public sealed record RequestSignallingRequest
	{
		public long TargetUserId { get; init; }
	}

	public sealed record RelayRequest
	{
		public long To { get; init; }
		public byte[] Payload { get; init; } = [];
	}

	public sealed record MeshCheckResponseRequest
	{
		public long MeshCheckId { get; init; }
		public int Attempt { get; init; }
		public IReadOnlyList<long> Connected { get; init; } = [];
		public IReadOnlyList<long> Connecting { get; init; } = [];
	}

	public sealed record ProbeResponseRequest
	{
		public string ProbeId { get; init; } = "";
	}

	public sealed record ModuleInfo
	{
		public string Path { get; init; } = "";
		public long Size { get; init; }
	}

	public sealed record ModuleScanResponseRequest
	{
		public string ScanId { get; init; } = "";
		public IReadOnlyList<ModuleInfo> Modules { get; init; } = [];
	}

	// Result data

	public sealed record SendMessageData
	{
		public string MessageId { get; init; } = "";
	}

	public sealed record HistoryData
	{
		public IReadOnlyList<Message> Messages { get; init; } = [];
		public bool HasMore { get; init; }
	}

	public sealed record WatchLobbyListData
	{
		public IReadOnlyList<LobbySummary> Lobbies { get; init; } = [];
	}

	public sealed record CreateLobbyData
	{
		public long LobbyId { get; init; }
		public TurnCredentials Turn { get; init; } = new TurnCredentials();
	}

	public sealed record JoinLobbyData
	{
		public TurnCredentials Turn { get; init; } = new TurnCredentials();
	}

	public sealed record ReportMatchOutcomeData
	{
		public string ScreenshotUrl { get; init; } = "";
		public string ReplayUrl { get; init; } = "";
	}

	public sealed record GetPlaylistsData
	{
		public IReadOnlyList<Playlist> Playlists { get; init; } = [];
	}

	// Events (one argument object per event)

	public sealed record ReadyEvent
	{
		public int ProtocolVersion { get; init; }
		public ClientType SessionType { get; init; }
		public Member User { get; init; } = new Member();
		public IReadOnlyList<string> Permissions { get; init; } = [];
		public IReadOnlyDictionary<string, BadgeDefinition> Badges { get; init; } = new Dictionary<string, BadgeDefinition>();
		public IReadOnlyList<ChannelInfo> Channels { get; init; } = [];
		public Limits Limits { get; init; } = new Limits();
	}

	public sealed record BadgesUpdateEvent
	{
		public IReadOnlyDictionary<string, BadgeDefinition> Badges { get; init; } = new Dictionary<string, BadgeDefinition>();
	}

	public sealed record NoticeEvent
	{
		public string Code { get; init; } = "";
		public object Params { get; init; } = Empty.Instance;
	}

	public sealed record ModerationNoticeEvent
	{
		public ModerationAction Action { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Reason { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public DateTime? ExpiresAt { get; init; }
	}

	public sealed record ChannelMembersEvent
	{
		public long ChannelId { get; init; }
		public IReadOnlyList<Member> Members { get; init; } = [];
	}

	public sealed record MemberAddEvent
	{
		public string ChannelId { get; init; } = "";
		public Member Member { get; init; } = new Member();
	}

	public sealed record MemberRemoveEvent
	{
		public string ChannelId { get; init; } = "";
		public long UserId { get; init; }
	}

	public sealed record MemberUpdateEvent
	{
		public Member Member { get; init; } = new Member();
	}

	public sealed record ChannelHistoryEvent
	{
		public string ChannelId { get; init; } = "";
		public IReadOnlyList<Message> Messages { get; init; } = [];
		public bool HasMore { get; init; }
	}

	// MessageDelete and ReadMarker
	public sealed record MessageRefEvent
	{
		public string ChannelId { get; init; } = "";
		public string MessageId { get; init; } = "";
	}

	public sealed record DirectConversationInfo
	{
		public long UserId { get; init; }
		public int Unread { get; init; }
		public Message Last { get; init; } = new Message();
	}

	public sealed record DirectMessagesEvent
	{
		public IReadOnlyList<DirectConversationInfo> Conversations { get; init; } = [];
	}

	public sealed record LobbyListUpsertEvent
	{
		public LobbySummary Lobby { get; init; } = new LobbySummary();
	}

	public sealed record LobbyIdEvent
	{
		public long LobbyId { get; init; }
	}

	public sealed record LobbyStateEvent
	{
		public LobbyInfo Lobby { get; init; } = new LobbyInfo();
	}

	public sealed record LobbyLeftEvent
	{
		public long LobbyId { get; init; }
		public LobbyLeftReason Reason { get; init; }
	}

	public sealed record CountdownStartedEvent
	{
		public long LobbyId { get; init; }
		public long DurationS { get; init; }
	}

	public sealed record CountdownCancelledEvent
	{
		public long LobbyId { get; init; }
		public CountdownCancelReason Reason { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public IReadOnlyList<long>? Missing { get; init; }
	}

	public sealed record GameStartEvent
	{
		public long LobbyId { get; init; }
		public long MatchId { get; init; }
		public int RngSeed { get; init; }
		public string? ScreenshotUrl { get; init; }
	}

	public sealed record MatchmakingStatusEvent
	{
		public string Code { get; init; } = "";
		public object Params { get; init; } = Empty.Instance;
	}

	public sealed record MatchFoundEvent
	{
		public long LobbyId { get; init; }
		public MapRef Map { get; init; } = new MapRef();
		public int Players { get; init; }
	}

	public sealed record MatchSetupProgressEvent
	{
		public long TimeoutS { get; init; }
		public long CountdownS { get; init; }
	}

	public sealed record MatchRequeueEvent
	{
		public MatchRequeueReason Reason { get; init; }
	}

	public sealed record FriendsEvent
	{
		public IReadOnlyList<FriendMember> Friends { get; init; } = [];
		public IReadOnlyList<Member> Incoming { get; init; } = [];
		public IReadOnlyList<Member> Outgoing { get; init; } = [];
		public IReadOnlyList<Member> Blocked { get; init; } = [];
	}

	public sealed record FriendRequestReceivedEvent
	{
		public Member From { get; init; } = new Member();
	}

	public sealed record FriendRequestAcceptedEvent
	{
		public Member By { get; init; } = new Member();
	}

	public sealed record UserIdEvent
	{
		public long UserId { get; init; }
	}

	public sealed record FriendPresenceEvent
	{
		public long UserId { get; init; }
		public bool Online { get; init; }
		public IReadOnlyList<ClientType> Clients { get; init; } = [];
		public PresenceActivity Activity { get; init; }
		public long? ChannelId { get; init; }
	}

	public sealed record StartSignallingEvent
	{
		public long LobbyId { get; init; }
		public long UserId { get; init; }
		public int PreferredPort { get; init; }
		public string MiddlewareId { get; init; } = "";
	}

	public sealed record DisconnectPeerEvent
	{
		public long LobbyId { get; init; }
		public long UserId { get; init; }
	}

	public sealed record RelayEvent
	{
		public long From { get; init; }
		public byte[] Payload { get; init; } = [];
	}

	public sealed record MeshCheckRequestEvent
	{
		public long MeshCheckId { get; init; }
		public int Attempt { get; init; }
	}

	public sealed record MeshCheckResultEvent
	{
		public long MeshCheckId { get; init; }
		public int Attempt { get; init; }
		public bool Complete { get; init; }
		public string Reason { get; init; } = "";
		public IReadOnlyList<MeshPair> Missing { get; init; } = [];
	}

	public sealed record AnticheatPeerEvent
	{
		public long UserId { get; init; }
		public string MiddlewareId { get; init; } = "";
	}

	public sealed record ProbeRequestEvent
	{
		public string ProbeId { get; init; } = "";
		public string Url { get; init; } = "";
	}

	public sealed record ModuleScanRequestEvent
	{
		public string ScanId { get; init; } = "";
	}
}
