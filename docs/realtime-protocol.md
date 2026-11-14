# Realtime protocol v1 (SignalR)

The game client, the launcher and the web chat talk to the service over one SignalR hub at `/-/realtime`. Every command gets a typed result, every server event carries its data, and the service never sends display text: clients render names, badges and messages themselves. The deploy is greenfield: this is the only realtime protocol, and nothing of the legacy `/ws` protocol stays on the wire.

Related designs: identity and sign-in (`docs/identity.md`, OpenIddict), permissions (`GenOnlineService/Authorization.cs`), schema v1 (`docs/schema.md`). Each environment is a separate instance, so users, friends, blocks, roles and bans in this protocol are always those of the environment the client is connected to.

Status: draft for review. Every message is specified; examples for each live in `docs/realtime-fixtures/`.

## Quick path

1. Sign in with the GenOnline login (OAuth authorization code with PKCE, see the identity design) and keep the access token.
2. Register the game session with `POST /api/v1/auth/session`; it returns the realtime URL.
3. Open `wss://<host>/-/realtime` with `Authorization: Bearer <access token>` and send the SignalR handshake.
4. Wait for `Ready`, then the snapshot events (`Friends`, `DirectMessages`, and `ChannelMembers` with `ChannelHistory` or `LobbyState` when rejoining).
5. Call hub methods for actions; each returns a `Result`.
6. Handle server events by name; ignore events and fields you do not know.

## Contents

1. [Transport](#1-transport)
2. [Conventions](#2-conventions)
3. [Connection lifecycle](#3-connection-lifecycle)
4. [Shared objects](#4-shared-objects)
5. [Hub methods (client to server)](#5-hub-methods-client-to-server)
6. [Events (server to client)](#6-events-server-to-client)
7. [Error codes](#7-error-codes)
8. [Groups and delivery](#8-groups-and-delivery)
9. [Session types](#9-session-types)
10. [What stays on REST](#10-what-stays-on-rest)
11. [Contract fixtures and versioning](#11-contract-fixtures-and-versioning)
12. [Legacy reference](#12-legacy-reference)
13. [Open questions](#13-open-questions)

## 1. Transport

| Topic | Decision |
|---|---|
| Endpoint | `wss://<host:port>/-/realtime` on the same origin (host and port) as the environment's REST API, so the bearer token only goes to an origin the client already trusts. `POST /api/v1/auth/session` returns it as `ws_url`. Production uses port 443, which matters on networks that only allow web traffic |
| Transport | WebSockets only. The client connects directly (`skipNegotiation`), no HTTP negotiate call, no long polling, no QUIC |
| HTTP version | The upgrade is plain HTTP/1.1, so the traffic looks like ordinary HTTPS on restrictive networks |
| Hub protocol | SignalR JSON hub protocol, version 1 ([spec](https://github.com/dotnet/aspnetcore/blob/main/src/SignalR/docs/specs/HubProtocol.md)) |
| Framing | Text frames. Every record is a JSON object followed by the record separator byte `0x1E` |
| Auth | `Authorization: Bearer <access token>` (the GenOnline OAuth access token for this environment) on the upgrade request. Browsers cannot set that header, so the web chat passes `access_token` in the query string; the service strips it from request logs. The token is checked once at connect: the open connection is not re-authenticated and is closed instead when the user's state changes (`auth.revoked`, `moderation.banned`) |
| Keep-alive | Server `KeepAliveInterval` 15 s, server `ClientTimeoutInterval` 30 s. The client sends a ping record (`{"type":6}`) every 15 s and treats 30 s without any record as a dead connection |
| Size limit | 64 KB per record (`MaximumReceiveMessageSize`) |

The game client implements the hub protocol itself on its existing curl websocket. The official C++ SignalR client is alpha and has no working websocket, so it is not used.

### Wire examples

Handshake, sent once after the upgrade:

```json
{"protocol":"json","version":1}
```

The server answers `{}` (or `{"error":"..."}`). Every line below ends with `0x1E` on the wire.

```json
{"type":1,"invocationId":"5","target":"SendMessage","arguments":[{"channel_id":"channel:3","type":"chat","content":"hi"}]}
{"type":3,"invocationId":"5","result":{"ok":true,"data":{"message_id":"8812"}}}
{"type":1,"target":"MessageCreate","arguments":[{"id":"8812","channel_id":"channel:3","type":"chat","author":{"id":42,"name":"Bob","badges":[]},"content":"hi","created_at":"2026-11-14T20:00:00Z"}]}
{"type":6}
{"type":7,"error":"session.superseded","allowReconnect":false}
```

| `type` | Record | Direction |
|---|---|---|
| 1 | Invocation (with `invocationId` it expects a completion, without it is fire-and-forget or an event) | both |
| 3 | Completion, the answer to an invocation | server to client |
| 6 | Ping | both |
| 7 | Close | server to client |

## 2. Conventions

| Topic | Rule |
|---|---|
| Arguments | Every hub method and event takes exactly **one** argument: an object. Never positional parameters |
| Field names | `snake_case` (server uses `JsonNamingPolicy.SnakeCaseLower`) |
| Method and event names | `PascalCase` (`SendMessage`, `MessageCreate`) |
| Ids | User, lobby and channel ids are JSON numbers (all fit in 53 bits). Message ids are strings |
| Channel ids | Typed strings: `channel:<channel_id>`, `lobby:<lobby_id>`, `dm:<user_id>` |
| Durations and times | A duration a person sees or chooses is whole seconds with `_s`; a delay the service computes for a client to wait is milliseconds with `_ms`; a point in time is ISO 8601 UTC with `_at` (`2026-11-13T20:00:00Z`). The measured `latency_ms` stays in milliseconds |
| Binary data | Base64 strings (signalling, anticheat payloads) |
| Text | The service sends codes and parameters, never display text. The only free text is user content (chat, reasons typed by staff) |
| Optional and nullable | `field?` may be omitted. A field written as `field: T or null` (for example `password`, `screenshot_url`, `team`) is always sent and is `null` when it has no value |
| Patch methods | In `UpdateLobby`, `UpdateMySlot` and `UpdateSlots` an omitted field stays unchanged and `null` clears a nullable field (`"team": null` means no team) |
| Unknown data | Clients ignore unknown events and unknown fields. The service ignores unknown fields |
| Results | Every method that expects an answer returns a `Result` (section 4). Hub errors (`completion.error`) only happen for protocol bugs, never for normal failures |

## 3. Connection lifecycle

```
connect ──> handshake ──> Ready ──> snapshots ──> events / commands ──> close
                                                       │
                              connection lost ─────────┘──> reconnect (backoff) ──> Ready ──> snapshots
```

1. **Connect** with the access token after registering the session. An invalid or revoked token fails the upgrade with HTTP 401; a game client without a registered session gets HTTP 409 `{"code":"session.not_registered"}`; a user with an active play ban gets HTTP 403 `{"code":"moderation.banned","params":{"reason":"...","expires_at":"..."}}`. A ban never blocks signing in to the website, so appeals keep working.
2. **Ready** is always the first event. It carries the user, permissions, badge definitions and limits (section 6.1).
3. **Snapshots** follow immediately: `Friends` always, then `DirectMessages`; `ChannelMembers` and `ChannelHistory` if the session was in a channel; `LobbyState` if the user is in a lobby. After a reconnect these restore everything, so the client never re-fetches state over REST.
4. **Reconnect** on any lost connection: wait 0.5 to 3 s random, then 2 s doubling to a 30 s cap, forever while logged in. During a match the client keeps reconnecting without a time window.
5. **Close** (`type 7`) carries a code in `error` and `allowReconnect`:

| `error` | `allowReconnect` | Meaning | Client does |
|---|---|---|---|
| `session.superseded` | false | Same user and session type connected elsewhere | Stop, show "signed in elsewhere" |
| `auth.revoked` | false | Tokens or the session revoked (logout, session revoke, account blocked) | Refresh the access token once through `/oauth/token`, then register the session again, otherwise return to login |
| `moderation.kicked` | true | Staff kicked the user | Show the `ModerationNotice` that came before, reconnect after 10 s |
| `moderation.banned` | false | Banned | Show the `ModerationNotice`, return to login |
| `server.restarting` | true | Planned restart | Reconnect with backoff |
| `protocol.error` | false | Malformed traffic | Log and return to login (client bug) |

Missed events while disconnected are not replayed in v1: state is restored by the snapshots. Channel chat and direct messages sent during the gap come back through `ChannelHistory`, `DirectMessages` and `GetHistory` (section 6.2); lobby chat sent during the gap is lost. Stateful reconnect can be added later without a breaking change (section 13).

## 4. Shared objects

**Result** (returned by every method that expects an answer)

```json
{"ok":true,"data":{}}
{"ok":false,"code":"chat.rate_limited","params":{"retry_after_ms":4200}}
```

**Member** (a user as other users see them; used in chat, channels, lobbies, friends)

```json
{"id":7,"name":"Ann","badges":["moderator"],"client":"game"}
```

- `badges`: badge ids, highest priority first; definitions (text and color) arrive in `Ready` and `BadgesUpdate`. Clients render a user's name in the color of their first badge and never hard-code badge ids, because operators define badges at runtime.
- `client`: `game`, `launcher` or `chat`. Present in member lists, omitted in chat messages.

**Message**

```json
{"id":"8812","channel_id":"channel:3","type":"chat","author":{"id":7,"name":"Ann","badges":["moderator"]},"content":"hello","created_at":"2026-11-14T20:00:00Z"}
```

`type`: `chat`, `action` (an emote, rendered as "Ann waves"), `announcement` (lobby host only, rendered prominently). `created_at` is when the service stored the message.

A message deleted by staff appears in history as a tombstone with no author and no content:

```json
{"id":"8811","channel_id":"channel:3","created_at":"2026-11-14T19:59:00Z","deleted":true}
```

OPEN: whether a tombstone shows its author.

**Notice**

```json
{"code":"name.changed","params":{"user_id":7,"old_name":"Ann","new_name":"Anna"}}
```

**Lobby objects.** Enums are lowercase strings, never numbers. Every lobby has exactly 8 slots; a slot is open, closed, a player or an AI.

`Slot`

```json
{"index":1,"state":"player","member":{"id":7,"name":"Ann","badges":[]},"ready":true,"has_map":true,"side":2,"color":4,"team":null,"start_position":3}
```

| Field | Values |
|---|---|
| `index` | 0 to 7; slot 0 is the host and is always ready |
| `state` | `open`, `closed`, `player`, `ai_easy`, `ai_medium`, `ai_brutal` |
| `member` | `Member` for `player` slots, absent otherwise |
| `ready`, `has_map` | booleans; AI slots are always ready |
| `side`, `color`, `start_position` | game ids, `-1` means random |
| `team` | team number or `null` for no team |

`LobbySummary` (lobby list entry, as one viewer sees it)

```json
{"id":991,"name":"1v1 no rush","host":{"id":42,"name":"Bob","badges":[]},"state":"setup","map":{"name":"Tournament Desert","official":true},"players":1,"max_players":2,"has_password":false,"region":"eu","latency_ms":38,"anticheat":"easy_anticheat","settings":{"starting_cash":10000,"limit_superweapons":true,"vanilla_teams":false,"track_stats":true,"allow_observers":false},"build":{"exe_crc":1234567890,"ini_crc":987654321},"created_at":"2026-11-13T20:00:00Z"}
```

- `players` counts filled slots (players and AI), `max_players` counts slots that are not closed.
- `latency_ms` is estimated per viewer, so each watcher receives its own value.
- The list only contains custom lobbies in `setup` or `countdown` that the viewer may join: lobbies whose host blocked the viewer and friends-only lobbies of non-friends are left out.

`LobbyState` (the lobby the user is in)

```json
{"id":991,"name":"1v1 no rush","host_id":42,"type":"custom","state":"setup","visibility":"public","has_password":false,"map":{"name":"Tournament Desert","path":"maps/tournament desert/tournament desert","official":true},"region":"eu","anticheat":"easy_anticheat","settings":{"starting_cash":10000,"limit_superweapons":true,"vanilla_teams":false,"track_stats":true,"allow_observers":false,"max_camera_height":310},"match_id":null,"created_at":"2026-11-13T20:00:00Z","slots":[{"index":0,"state":"player","member":{"id":42,"name":"Bob","badges":[]},"ready":true,"has_map":true,"side":-1,"color":-1,"team":null,"start_position":-1}]}
```

| Field | Values |
|---|---|
| `type` | `custom` or `quick_match` |
| `state` | `setup`, `countdown`, `in_game`, `complete` |
| `visibility` | `public` or `friends` |
| `anticheat` | `none`, `go_integrated`, `easy_anticheat` |
| `match_id` | `null` until the game starts |
| `slots` | always 8 entries (the example shows one) |

Never sent to clients: the lobby password, the matchmaker's paired user ids, mesh check bookkeeping. The lobby name is the host's text only; the region and anticheat badges the legacy service baked into the name are separate fields.

**Playlist**

```json
{"id":0,"name":"1v1 (Random Armies)","min_players":2,"desired_players":2,"min_selected_maps":1,"allow_teams":false,"team_size":null,"allow_army_selection":false,"grace_period_s":0,"maps":[{"index":0,"name":"Tournament Desert","path":"maps/tournament desert/tournament desert","custom":false,"max_players":2}]}
```

**Build identity.** The executable and INI CRCs and the anticheat id are sent once, with `POST /api/v1/auth/session`. The service uses them for every lobby and queue call of that session, so no hub method carries them.

## 5. Hub methods (client to server)

"Result" means the method expects an answer (`invocationId` set). "Fire" means the client sends it without `invocationId` and gets no answer.

### 5.1 Channels and chat

A session is in at most one channel at a time, like Battle.net classic: joining another channel leaves the current one. A user is listed only in the channel they are in, and friends see that channel in presence.

| Method | Argument | Answer | Notes |
|---|---|---|---|
| `JoinChannel` | `{channel_id}` | Result | Then `ChannelMembers`, then `ChannelHistory`. A parent channel resolves to its default child; `ChannelMembers.channel_id` is the channel actually joined. Leaving a channel happens by joining another or `LeaveChannel` |
| `LeaveChannel` | `{}` | Result | |
| `SendMessage` | `{channel_id, type, content}` | Result `{message_id}` | `type` `chat` or `action`; `announcement` only for the host in `lobby:` channels. `dm:` requires mutual friendship; a message to an offline friend is stored and answers ok. Content 1 to 512 characters |
| `GetHistory` | `{channel_id, before?: message_id, limit}` | Result `{messages: [Message], has_more}` | `channel:` and `dm:` only; the `limit` messages just older than `before`, or the newest when it is absent, returned oldest first like `ChannelHistory`; `has_more` is true when older messages exist; `limit` 1 to 100 |
| `MarkRead` | `{channel_id, message_id}` | Result | Sets the read position; the user's other connections get `ReadMarker` |
| `SetDisplayName` | `{name}` | Result | 3 to 16 characters. On success everyone who can see the user gets `MemberUpdate` |

### 5.2 Lobbies

Game sessions only. Any change other than ready, has-map, force-ready and kick clears every player's ready flag and cancels a pending start.

| Method | Argument | Answer | Who |
|---|---|---|---|
| `WatchLobbyList` | `{}` | Result `{lobbies: [LobbySummary]}` | any game session; subscribes to `LobbyListUpsert` and `LobbyListRemove` |
| `UnwatchLobbyList` | `{}` | Result | |
| `CreateLobby` | `{name, map: {name, path, official}, max_players, visibility, password: string or null, preferred_port, settings: {starting_cash, limit_superweapons, vanilla_teams, track_stats, allow_observers, max_camera_height}}` | Result `{lobby_id, turn: {username, token}}` | any game session; name and map name up to 255 characters, map path up to 512, password up to 128 |
| `JoinLobby` | `{lobby_id, password: string or null, preferred_port, has_map}` | Result `{turn: {username, token}}` | any game session; quick match lobbies only for the matched players |
| `LeaveLobby` | `{}` | Result | member |
| `UpdateLobby` | `{map?: {name, path, official, max_players}, settings?: {starting_cash?, limit_superweapons?, max_camera_height?}, visibility?}` | Result | host; only the fields present change. A new `max_players` opens or closes slots |
| `SetLobbyPassword` | `{password}` | Result | host; `null` removes it. The password is never sent back to anyone |
| `UpdateMySlot` | `{side?, color?, team?: int or null, start_position?}` | Result | member, own slot; patch semantics (section 2) |
| `SetHasMap` | `{has_map}` | Result | member |
| `SetReady` | `{ready}` | Result | member |
| `SetSlotState` | `{index, state}` | Result | host; `open`, `closed` or an AI state; a player slot cannot be changed this way |
| `UpdateSlots` | `{slots: [{index, side?, color?, team?: int or null, start_position?}]}` | Result | host; any slot, players and AI; patch semantics (section 2) |
| `KickFromLobby` | `{user_id}` | Result | host |
| `ForceReady` | `{}` | Result | host; marks everyone ready |
| `StartGame` | `{}` | Result | host; the service runs the ready check, the mesh check and the countdown, and answers `lobby.not_ready`, `lobby.mesh_incomplete` and similar codes when it cannot start |
| `ReportMatchOutcome` | `{match_id, won, desynced, side, duration_s, stats: {buildings_built, buildings_killed, buildings_lost, units_built, units_killed, units_lost, total_money}}` | Result `{screenshot_url, replay_url}` | a player of that match; the URLs are presigned upload targets |

### 5.3 Matchmaking

| Method | Argument | Answer | Notes |
|---|---|---|---|
| `GetPlaylists` | `{}` | Result `{playlists: [Playlist]}` | |
| `JoinQueue` | `{playlist_id, map_indexes: [int]}` | Result | `map_indexes` index into the playlist's `maps`; at least `min_selected_maps`. Status then arrives as `MatchmakingStatus` |
| `LeaveQueue` | `{}` | Result | |
| `WidenSearch` | `{}` | Result | selects every map of the playlist |

### 5.4 Social

| Method | Argument | Answer |
|---|---|---|
| `SendFriendRequest` | `{user_id}` | Result |
| `AcceptFriendRequest` | `{user_id}` | Result |
| `DeclineFriendRequest` | `{user_id}` | Result |
| `CancelFriendRequest` | `{user_id}` | Result |
| `RemoveFriend` | `{user_id}` | Result |
| `BlockUser` | `{user_id}` | Result |
| `UnblockUser` | `{user_id}` | Result |

### 5.5 Moderation

Each method is a `perm:<permission>` policy, so the service decides from the caller's roles in this environment (local roles and roles synced from the upstream sign-in). Moderators hold `moderation.kick`, `moderation.mute` and `chat.delete_any`; admins also hold `moderation.ban`. Staff only act on users of lower rank: a moderator cannot kick, mute, unmute or ban an admin or another moderator. A mute is a ban with restriction `chat`: the user keeps playing but cannot send messages. Every action is written to `audit_log` with the actor, the target and the reason; the reason is stored only there.

- Reasons are optional, except on `Ban`, where the reason is required and not blank (`invalid_argument {field: reason}`). Reasons are up to 512 characters.
- `duration_s` is whole seconds, at least 60 and a multiple of 60, at most 10 years (315360000); otherwise `invalid_argument {field: duration_s}`. Clients offer presets (10 minutes, 1 hour, 1 day, 7 days, 30 days, permanent for bans).
- OPEN: permanent mutes. `duration_s` stays required on `Mute`.
- OPEN: message reports.

| Method | Argument | Answer | Permission |
|---|---|---|---|
| `Kick` | `{user_id, reason?}` | Result | `moderation.kick` |
| `Mute` | `{user_id, duration_s, reason?}` | Result | `moderation.mute` |
| `Unmute` | `{user_id, reason?}` | Result | `moderation.mute` |
| `Ban` | `{user_id, duration_s?, reason}` | Result (no `duration_s` means permanent) | `moderation.ban` |
| `Unban` | `{user_id, reason?}` | Result | `moderation.ban` |
| `DeleteMessage` | `{message_id, reason?}` | Result | `chat.delete_any`; any channel, lobby or direct message. No player delete, no edits. Everyone who received the message gets `MessageDelete`; history then returns a tombstone (section 4). The stored message keeps `deleted_at` only; who deleted it and why are in `audit_log` |

### 5.6 Peer networking (game sessions only)

| Method | Argument | Answer | Notes |
|---|---|---|---|
| `RequestSignalling` | `{target_user_id}` | Fire | The service sends `StartSignalling` to both peers |
| `Signal` | `{to, payload}` | Fire | Relayed as `Signal` to a peer in the same lobby |
| `BeginMeshCheck` | `{}` | Result | Host only, not in quick match lobbies |
| `MeshCheckResponse` | `{mesh_check_id, attempt, connected: [user_id], connecting: [user_id]}` | Fire | |
| `AnticheatMessage` | `{to, payload}` | Fire | Relayed as `AnticheatMessage` |
| `ProbeResponse` | `{probe_id}` | Fire | Answer to `ProbeRequest` |
| `ModuleScanResponse` | `{scan_id, modules: [{path, size}]}` | Fire | Answer to `ModuleScanRequest` |

## 6. Events (server to client)

### 6.1 Session

| Event | Data |
|---|---|
| `Ready` | `{protocol_version, session_type, user: Member, permissions: [string], badges: {id: {text, color}}, channels: [{id, name, parent_id: int or null, show_all_matches}], limits: {chat_max_length, chat_rate: {messages, window_s}, name_min_length, name_max_length}}`; a channel whose `parent_id` is null is top level; `chat_rate` applies to `channel:` chat only |
| `BadgesUpdate` | `{badges: {id: {text, color}}}` (full set) |
| `Notice` | `Notice` (section 4), for things that are not the answer to a call, such as `name.changed` |
| `ModerationNotice` | `{action: "kick"|"ban"|"mute", reason?, expires_at?}`; `reason` is absent when staff gave none; a close record follows for kick and ban |

### 6.2 Channels and chat

| Event | Data |
|---|---|
| `ChannelMembers` | `{channel_id, members: [Member]}` (full list, on join and after reconnect) |
| `ChannelHistory` | `{channel_id, messages: [Message], has_more}` (after `ChannelMembers`: the last messages of the channel, oldest first; split into several events when it would exceed 64 KB) |
| `MemberAdd` | `{channel_id, member: Member}` |
| `MemberRemove` | `{channel_id, user_id}` |
| `MemberUpdate` | `{member: Member}` (rename, badge change; sent to everyone who can see the user) |
| `MessageCreate` | `Message` |
| `MessageDelete` | `{channel_id, message_id}` (staff deleted the message; sent to everyone who received it) |
| `DirectMessages` | `{conversations: [{user_id, unread, last: Message}]}` (snapshot after `Friends`) |
| `ReadMarker` | `{channel_id, message_id}` (the user's read position changed on another connection) |

**History and storage.** The service stores chat and replays it, so a reconnect or a channel switch never loses messages.

| | Stored | Replayed |
|---|---|---|
| Channel | id, channel id, author id, type, content, `created_at`, `deleted_at` | the last N on join (N from configuration, default 50) |
| Lobby | the same plus lobby id and match id | never (lobbies are short) |
| Direct | the same with sender and recipient | unread and recent on connect, synced across the user's clients |

- History respects blocks: messages from a user the viewer blocked, or who blocked the viewer, are left out.
- No chat text in application logs or telemetry, only ids and counts. Account deletion removes or anonymises the user's messages. The privacy policy must say chat is stored, why, and for how long.
- OPEN: retention periods.

**Direct messages.** Only mutual friends exchange direct messages, and a block stops delivery. A message to an offline friend is stored and delivered on their next connect through `DirectMessages` and `GetHistory`.

Messages are stored in the service database as they are; the protocol and the service add no encryption beyond TLS on the connection.

### 6.3 Lobbies

| Event | Data |
|---|---|
| `LobbyListUpsert` | `{lobby: LobbySummary}` (new or changed lobby, for lobby list watchers) |
| `LobbyListRemove` | `{lobby_id}` |
| `LobbyState` | `{lobby: LobbyState}` (full state after every change, to members of that lobby) |
| `LobbyLeft` | `{lobby_id, reason}` (`left`, `kicked`, `closed`) |
| `CountdownStarted` | `{lobby_id, duration_s}` |
| `CountdownCancelled` | `{lobby_id, reason, missing: [user_id]?}`; `reason` is `player_not_ready`, `mesh_incomplete`, `player_left` or `settings_changed` |
| `GameStart` | `{lobby_id, match_id, rng_seed, screenshot_url: string or null}` |

### 6.4 Matchmaking

| Event | Data |
|---|---|
| `MatchmakingStatus` | `{code, params}` (codes below) |
| `MatchFound` | `{lobby_id, map: {name, path, official}, players}`; join with `JoinLobby` within 45 s |
| `MatchSetupProgress` | `{timeout_s, countdown_s}` (mesh check: `countdown_s` 0; start countdown: both set) |
| `MatchStart` | `{lobby_id}` |
| `MatchRequeue` | `{reason}`: `player_left`, `lobby_gone`, `mesh_incomplete`, `join_timeout`, `setup_failed` |

| `MatchmakingStatus` code | Params |
|---|---|
| `matchmaking.queued` | `{playlist_id}` |
| `matchmaking.searching` | `{players, desired, required}` |
| `matchmaking.expanding` | `{}` |
| `matchmaking.grace_countdown` | `{seconds}` |
| `matchmaking.grace_cancelled` | `{}` |
| `matchmaking.creating` | `{players, map}` |
| `matchmaking.preparing` | `{}` |
| `matchmaking.starting` | `{seconds}` |
| `matchmaking.left` | `{reason}`: the service took the player out of the queue and did not requeue them; `join_timeout`, `setup_failed`, `stats_unavailable`, `invalid_playlist`, more may follow (show a generic line for unknown reasons) |

### 6.5 Social

| Event | Data |
|---|---|
| `Friends` | `{friends: [Member + online + activity + channel_id], incoming: [Member], outgoing: [Member], blocked: [Member]}` (snapshot after `Ready`) |
| `FriendRequestReceived` | `{from: Member}` |
| `FriendRequestAccepted` | `{by: Member}` |
| `FriendRemoved` | `{user_id}` |
| `FriendPresence` | `{user_id, online, clients: [string], activity, channel_id: int or null}` (keyed by user id, never by name) |

`channel_id` is the channel the user is in, `null` when in none. `activity` is `offline`, `menu`, `channel`, `lobby`, `matchmaking` or `in_game`; the client renders and localizes it (for example "Online - In game"). It describes the user in this environment only.

### 6.6 Peer networking (game sessions only)

| Event | Data |
|---|---|
| `StartSignalling` | `{lobby_id, user_id, preferred_port, middleware_id}` |
| `DisconnectPeer` | `{lobby_id, user_id}` |
| `Signal` | `{from, payload}` |
| `MeshCheckRequest` | `{mesh_check_id, attempt}` |
| `MeshCheckResult` | `{mesh_check_id, attempt, complete, reason, missing: [{source_user_id, target_user_id}]}` |
| `AnticheatRegister` / `AnticheatDeregister` | `{user_id, middleware_id}` |
| `AnticheatMessage` | `{from, payload}` |
| `ProbeRequest` | `{probe_id, url}` |
| `ModuleScanRequest` | `{scan_id}` |

## 7. Error codes

Codes are stable strings; clients localize them. Every failed `Result` and every `Notice` uses this catalogue.

| Code | Params | Raised by |
|---|---|---|
| `forbidden` | `{permission}` | Any method the caller has no permission for |
| `session.not_registered` | | Connecting without `POST /api/v1/auth/session` first (HTTP 409) |
| `moderation.banned` | `{reason, expires_at?}` | Connecting while play banned (HTTP 403), and the close code when a ban starts |
| `invalid_argument` | `{field}` | Malformed or missing fields |
| `not_found` | `{what}` | Unknown user, lobby, channel or message |
| `rate_limited` | `{retry_after_ms}` | Any method over its rate limit |
| `chat.rate_limited` | `{retry_after_ms}` | `SendMessage` to a `channel:` channel only (3 messages per 9 s today); `lobby:` and `dm:` are not limited, in a lobby the host can kick instead |
| `chat.too_long` | `{max}` | `SendMessage` |
| `chat.muted` | `{expires_at?}` | `SendMessage` |
| `chat.not_friends` | | `SendMessage` to `dm:` |
| `name.length` | `{min, max}` | `SetDisplayName` |
| `name.invalid_characters` | | `SetDisplayName` |
| `name.whitespace` | | `SetDisplayName` |
| `name.reserved` | `{word}` | `SetDisplayName` (staff words and badge texts) |
| `name.taken` | | `SetDisplayName` |
| `channel.unavailable` | | `JoinChannel` |
| `lobby.full`, `lobby.wrong_password`, `lobby.not_host`, `lobby.not_member`, `lobby.already_started` | | Lobby methods |
| `lobby.not_in_setup`, `lobby.already_member`, `lobby.blocked`, `lobby.friends_only`, `lobby.not_matched`, `lobby.anticheat_mismatch`, `lobby.build_mismatch` | | `JoinLobby` |
| `lobby.slot_occupied` | `{index}` | `SetSlotState` on a player slot |
| `lobby.not_ready` | `{missing: [user_id]}` | `StartGame` |
| `lobby.mesh_incomplete` | `{missing: [user_id]}` | `StartGame` |
| `matchmaking.not_queued`, `matchmaking.already_queued` | | Matchmaking methods |
| `matchmaking.invalid_playlist` | `{playlist_id}` | `JoinQueue` |
| `matchmaking.too_few_maps` | `{min}` | `JoinQueue` |
| `matchmaking.stats_unavailable` | | `JoinQueue` (rating could not be loaded) |
| `social.friend_list_full` | `{max}` | `SendFriendRequest`, `AcceptFriendRequest` |
| `social.blocked` | | Social methods involving a block |
| `moderation.target_offline` | | `Kick` |
| `moderation.target_outranks` | | Moderation methods: staff only act on users of lower rank (admin above moderator above everyone else), never on themselves |
| `moderation.reason_too_long` | `{max}` | Moderation methods, `DeleteMessage` |
| `name.changed` | `{user_id, old_name, new_name}` | `Notice` |

New codes are added freely; clients show a generic message for codes they do not know.

## 8. Groups and delivery

| Group | Members | Used for |
|---|---|---|
| `user:<id>` | Every connection of that user | Social events, moderation notices, `Friends`, direct messages, `DirectMessages`, `ReadMarker` |
| `user:<id>:game` | That user's game connection | Peer networking, `LobbyState`, matchmaking |
| `channel:<id>` | Connections in that channel | `ChannelMembers`, `MemberAdd`/`Remove`, channel chat, `MessageDelete` |
| `lobby:<id>` | Game connections of lobby members | `LobbyState`, lobby chat, countdown events |
| `lobby-list` | Connections that called `WatchLobbyList` | `LobbyListUpsert`, `LobbyListRemove` |

Rules:

- One connection per user and session type. A new connection of the same type closes the old one with `session.superseded`.
- Group membership is per connection and rebuilt from the user's state on every connect, so reconnects never leave stale groups.
- Events about a user (`MemberUpdate`, `FriendPresence`) go to every group that can see them, deduplicated per connection.
- Blocks are enforced by the service: a `MessageCreate` is never delivered to a user who blocked its author, or by a user to someone who blocked them, in channels, lobbies and direct messages. History follows the same rule. Clients do not need to filter.
- A successful `JoinChannel` is always followed by `ChannelMembers`, then `ChannelHistory`, for that channel.
- Delivery to one connection is in order. There is no ordering between different connections.

## 9. Session types

| | Game | Launcher | Chat |
|---|---|---|---|
| Channels and chat | yes | yes | yes |
| Direct messages, social | yes | yes | yes |
| Lobby list (watch) | yes | yes | no |
| Join or create lobbies, matchmaking | yes | no | no |
| Peer networking, anticheat, probes | yes | no | no |
| Moderation methods (with permission) | yes | yes | yes |

Methods outside a session type's column return `forbidden`.

## 10. What stays on REST

Anything that does not need a live session stays on REST: login and token refresh, logout, the EOS token, MOTD and service config, version check, player stats, global stats, leaderboards and match history. Everything that acts on or notifies live sessions moves to the hub.

## 11. Contract fixtures and versioning

- `docs/realtime-fixtures/` holds one JSON file per method argument, result and event (`SendMessage.request.json`, `SendMessage.result.json`, `MessageCreate.event.json`, ...). Each file is the single argument object of the hub message, not the SignalR record around it; `docs/realtime-fixtures/README.md` lists the naming rules.
- A service test serializes the real C# types and compares them with the fixtures. A client test parses the same fixtures with the C++ code. Changing a message without updating the fixtures fails both.
- `Ready.protocol_version` is `1`. Additive changes (new methods, events, fields, codes) keep version 1. A breaking change ships as a new hub path (`/-/realtime/v2`) next to the old one.

## 12. Legacy reference

For porting client code only: where the behaviour of each legacy `/ws` message lives in v1. The legacy protocol is not served by the new deployment.

| Legacy | v1 |
|---|---|
| 1 / 2 room chat, 10 / 11 lobby chat, 30 / 31 friend chat | `SendMessage` and `MessageCreate` |
| 3 change room, 4 member list | `JoinChannel` / `LeaveChannel`, `ChannelMembers`, `MemberAdd` / `Remove` / `Update` |
| 5 mark ready | `SetReady` |
| 6 current lobby dirty | `LobbyState` with content |
| 7 lobby list dirty | `LobbyListUpsert` / `LobbyListRemove` |
| 8 anticheat | `AnticheatMessage` both ways |
| 9 name change, name-change chat text | `SetDisplayName`, `name.*` codes, `MemberUpdate`, `Notice name.changed` |
| 12 signal, 19 request signalling, 17 start signalling, 18 disconnect | `Signal`, `RequestSignalling`, `StartSignalling`, `DisconnectPeer` |
| 13 start game, 23 countdown started, 49 start rejected, 50 lobby event, 51 | `StartGame` result codes, `CountdownStarted` / `Cancelled`, `GameStart` |
| 14 / 15 ping, pong, 42 / 43 keepalive | SignalR ping; 42 / 43 were a module scan: `ModuleScanRequest` / `ModuleScanResponse` |
| 16 probe, 39 probe response | `ProbeRequest` / `ProbeResponse` |
| 20, 21, 22, 44, 45 matchmaking | `MatchFound`, `MatchStart`, `MatchmakingStatus`, `MatchRequeue`, `MatchSetupProgress` |
| 24 / 25 lobby password | `SetLobbyPassword` |
| 26 / 27 / 28 mesh check | `BeginMeshCheck`, `MeshCheckRequest`, `MeshCheckResponse`, `MeshCheckResult` |
| 29, 32, 35, 36, 37, 38 social | `FriendRequestReceived`, `FriendPresence`, `Friends`, `FriendRequestAccepted`, `social.friend_list_full` |
| 33 / 34 social subscribe | Removed: social events always go to the user |
| 40 / 41 anticheat register | `AnticheatRegister` / `AnticheatDeregister` |
| 46 moderation notice, 47 / 48 moderation command | `ModerationNotice`, `Kick` / `Mute` / `Ban` with `Result` |
| REST lobby, social and matchmaking mutations | Hub methods in section 5 |

## 13. Open questions

- [x] Lobby settings, `LobbySummary`, `LobbyState`, match outcome and queue fields: fixed in sections 4 and 5 from the current lobby, slot, playlist and outcome models.
- [x] `MatchFound.map`: the service sends the real map; the client no longer hard-codes one.
- [ ] Stateful reconnect (message replay after short drops): not in v1, can be added without a version bump.
- [ ] Friend presence across session types: v1 reports every connected client type in `clients`.
- [ ] Multiple service processes: groups work with a Redis backplane, but lobby and session state are in memory, so v1 assumes one `application` process per environment.
