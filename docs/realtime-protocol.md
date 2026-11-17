# Realtime protocol v1 (SignalR)

The game client, the launcher and the web chat talk to the service over one SignalR hub at `/realtime`. Every command gets a typed result, every server event carries its data, and the service never sends display text: clients render names, badges and messages themselves. The deploy is greenfield: this is the only realtime protocol, and nothing of the legacy `/ws` protocol stays on the wire.

Related designs: identity and sign-in (`docs/identity.md`, OpenIddict), permissions (`GenOnlineService/Authorization.cs`), schema v1 (`docs/schema.md`), failover and handover (`docs/realtime-failover.md`). Each environment is a separate instance, so users, friends, blocks, roles and bans in this protocol are always those of the environment the client is connected to.

Status: draft for review. Every message is specified; examples for each live in `docs/realtime-fixtures/`.

## Quick path

1. Sign in with the GenOnline login (OAuth authorization code with PKCE, see the identity design) and keep the access token.
2. Register the game session with `POST /api/v1/auth/session`; it returns the realtime URL.
3. Open `wss://<host>/realtime` with `Authorization: Bearer <access token>` and send the SignalR handshake.
4. Wait for `Ready`, then the snapshot events (`Friends`, `Conversations`, and `ChannelMembers` with `ChannelHistory` or `LobbyState` when rejoining).
5. Call hub methods for live actions (lobbies, matchmaking, chat, signalling) and REST for the rest (section 10); both report failures with the same codes.
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
10. [REST endpoints](#10-rest-endpoints)
11. [Contract fixtures and versioning](#11-contract-fixtures-and-versioning)
12. [Legacy reference](#12-legacy-reference)
13. [Open questions](#13-open-questions)

## 1. Transport

| Topic | Decision |
|---|---|
| Endpoint | `wss://<host:port>/realtime` on the same origin (host and port) as the environment's REST API, so the bearer token only goes to an origin the client already trusts. `POST /api/v1/auth/session` returns it as `ws_url`. Production uses port 443, which matters on networks that only allow web traffic |
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
| Channel ids | Typed strings: `channel:<channel_id>`, `lobby:<lobby_id>`, `dm:<user_id>`, `group:<group_id>` |
| Durations and times | A duration a person sees or chooses is whole seconds with `_s`; a delay the service computes for a client to wait is milliseconds with `_ms`; a point in time is ISO 8601 UTC with `_at` (`2026-11-13T20:00:00Z`). The measured `latency_ms` stays in milliseconds |
| Binary data | Base64 strings (signalling payloads) |
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

1. **Connect** with the access token after registering the session. The service accepts every WebSocket upgrade and completes the handshake, then refuses a caller it does not serve by closing at once, before `Ready`: an invalid or revoked token closes with `unauthorized`; a game client without a registered session for this sign-in closes with `session.not_registered`; a user with an active play ban gets `ModerationNotice {action: "ban", reason, expires_at?}` and then the close `moderation.banned`; a copy that is not serving (a failover standby, `docs/realtime-failover.md`) sends `ServerSwitching {retry_after_ms}` and then the close `service.unavailable`. These refusals are close codes because an HTTP status on the upgrade never reaches the client behind the Cloudflare tunnel: cloudflared answers every upgrade the origin does not accept with `101` with Cloudflare's own `502 Bad Gateway`, whatever the origin said. A client therefore treats any failed upgrade, a bare 502 included, as a lost connection and reconnects with backoff. A request to `/realtime` that is not a WebSocket upgrade still gets the HTTP answers of section 7, and REST calls the ban refuses answer with HTTP 403 `{"error":"moderation.banned","params":{"reason":"...","expires_at":"..."}}`. A ban never blocks signing in to the website, so appeals keep working.
2. **Ready** is always the first event. It carries the user, permissions, badge definitions and limits (section 6.1).
3. **Snapshots** follow immediately: `Friends` always, then `Conversations`; `ChannelMembers` and `ChannelHistory` if the session was in a channel; `LobbyState`, then `ChannelHistory` for `lobby:<id>`, if the user is in a lobby. After a reconnect these restore everything, so the client never re-fetches state over REST.
4. **Reconnect** on any lost connection: wait 0.5 to 3 s random, then 2 s doubling to a 30 s cap, forever while logged in. During a match the client keeps reconnecting without a time window. After a `server.switching` close the client reconnects after the `retry_after_ms` of the `ServerSwitching` event that came before, without backoff, and shows no reconnect screen for up to 3 s.
5. **Close** (`type 7`) carries a code in `error` and `allowReconnect`:

| `error` | `allowReconnect` | Meaning | Client does |
|---|---|---|---|
| `session.superseded` | false | Same user and session type connected elsewhere | Stop, show "signed in elsewhere" |
| `auth.revoked` | false | Tokens or the session revoked (logout, session revoke, account blocked) | Refresh the access token once through `/oauth/token`, then register the session again, otherwise return to login |
| `unauthorized` | false | Refused at connect, before `Ready`: no valid access token for this environment and client | Refresh the access token once through `/oauth/token` and connect again, otherwise return to login |
| `session.not_registered` | false | Refused at connect, before `Ready`: a game client without a `POST /api/v1/auth/session` registration for this sign-in | Register the session with `POST /api/v1/auth/session`, then connect again; if that refuses again, return to login |
| `moderation.kicked` | true | Staff kicked the user | Show the `ModerationNotice` that came before, reconnect after 10 s |
| `moderation.banned` | false | Banned: when the ban starts, or at connect before `Ready` while it lasts | Show the `ModerationNotice` that came before (its `reason` and `expires_at`, absent for a permanent ban), return to login |
| `service.unavailable` | true | Refused at connect, before `Ready`: this copy is not serving (a failover standby); `ServerSwitching` came before | Reconnect after its `retry_after_ms`, without advancing the backoff or showing an error; after 10 s of these in a row, reconnect with backoff |
| `server.restarting` | true | Planned restart | Reconnect with backoff |
| `server.switching` | true | Planned handover to another service copy (`docs/realtime-failover.md`); `ServerSwitching` came before | Reconnect after its `retry_after_ms`, without backoff or a reconnect screen |
| `protocol.error` | false | Malformed traffic | Log and return to login (client bug) |

Missed events while disconnected are not replayed in v1: state is restored by the snapshots. Channel chat, direct messages and group chat sent during the gap come back through `ChannelHistory`, `Conversations` and `GetHistory` (section 6.2); lobby chat comes back through `ChannelHistory` for `lobby:<id>` after `LobbyState`. A reconnect during lobby setup is safe: once the reconnecting member's `SetPeerTransports` of the new session has arrived (section 5.6), the service sends `StartSignalling` again for every pair of members that has not reported connected (a pair counts as connected once both list each other as `connected` in a `MeshCheckResponse`; before any mesh check that is every pair of the reconnecting member; nothing is resent once the game runs), and a client treats a repeated `StartSignalling` for a peer as a restart of that pair. `LobbyState.start` carries the start data, so a client that missed `GameStart` still starts. Events are not replayed by sequence number in v1; that can be added later without a breaking change (section 13).

## 4. Shared objects

**Result** (returned by every method that expects an answer)

```json
{"ok":true,"data":{}}
{"ok":false,"code":"chat.rate_limited","params":{"retry_after_ms":4200}}
```

**Member** (a user as other users see them; used in chat, channels, lobbies, friends)

```json
{"id":7,"name":"Ann","tag":"0042","badges":["moderator"],"client":"game"}
```

- `name` and `tag`: the display name is not unique; `name#tag` is (identity section 5a). Show `Ann#0042` on profiles, friend lists and friend requests, and in staff tools. In channels, lobbies and chat show the name alone, and add `#tag` only when two members there share the name (names compare case-insensitively). `tag` is always four digits and absent only for a user whose tag is not known. In lobbies the service applies this rule itself: each player slot carries `name`, the name the lobby shows.
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

A tombstone never shows its author or content.

**Group** (a group chat)

```json
{"id":12,"name":"Clan practice","owner_id":42,"members":[{"id":42,"name":"Bob","badges":[]},{"id":7,"name":"Ann","badges":["moderator"]}],"created_at":"2026-11-14T19:00:00Z"}
```

`name` is `null` when the owner set none; clients then show the member names. `members` lists every member, the owner included, in the order they joined.

**Notice**

```json
{"code":"name.changed","params":{"user_id":7,"old_name":"Ann","new_name":"Anna"}}
```

**Lobby objects.** Enums are lowercase strings, never numbers. Every lobby has exactly 8 slots; a slot is open, closed, a player or an AI.

`Slot`

```json
{"index":1,"state":"player","member":{"id":7,"name":"Ann","badges":[]},"name":"Ann","ready":true,"has_map":true,"side":2,"color":4,"team":null,"start_position":3}
```

| Field | Values |
|---|---|
| `index` | 0 to 7; slot 0 is the host and is always ready |
| `state` | `open`, `closed`, `player`, `ai_easy`, `ai_medium`, `ai_brutal` |
| `member` | `Member` for `player` slots, absent otherwise |
| `name` | for `player` slots, the name this lobby shows: the member's name, or `Name#tag` for every member whose name another member of the lobby shares (case-insensitive). It changes for both when such a member joins and goes back when one leaves; clients show it as is. Absent otherwise |
| `ready`, `has_map` | booleans; AI slots are always ready |
| `side`, `color`, `start_position` | game ids, `-1` means random. `side` `-2` is the observer seat, as in the game (`PLAYERTEMPLATE_OBSERVER`): allowed when the lobby's `allow_observers` is true, otherwise `invalid_argument {field: side}` from `UpdateMySlot` and `{field: slots.side}` from `UpdateSlots`; never for an AI. A remembered observer side becomes random at join when the lobby does not allow observers |
| `team` | team number or `null` for no team |

`LobbySummary` (lobby list entry, as one viewer sees it)

```json
{"id":991,"name":"1v1 no rush","host":{"id":42,"name":"Bob","badges":[]},"state":"setup","map":{"name":"Tournament Desert","official":true,"path":"maps/tournament desert/tournament desert"},"players":1,"max_players":2,"has_password":false,"region":"eu","latency_ms":38,"anticheat":"easy_anticheat","settings":{"starting_cash":10000,"limit_superweapons":true,"vanilla_teams":false,"track_stats":true,"allow_observers":false},"build":{"exe_crc":1234567890,"ini_crc":987654321},"created_at":"2026-11-13T20:00:00Z"}
```

- `players` counts filled slots (players and AI), `max_players` counts slots that are not closed.
- `map.path` is the `map.path` of the lobby's `LobbyState`, so a joiner can tell `JoinLobby` whether it has the map. A client reads a summary without it as one from an older service.
- `latency_ms` is estimated per viewer, so each watcher receives its own value.
- The list only contains custom lobbies in `setup` or `countdown` that the viewer may join: lobbies of a different build (game, executable and INI CRC, anticheat), lobbies whose host blocked the viewer and friends-only lobbies of non-friends are left out. Players only ever meet players running the same build, in custom lobbies and in matchmaking alike, so a third-party client plays with its own kind.

`LobbyState` (the lobby the user is in)

```json
{"id":991,"name":"1v1 no rush","host_id":42,"type":"custom","state":"setup","visibility":"public","has_password":false,"map":{"name":"Tournament Desert","path":"maps/tournament desert/tournament desert","official":true},"region":"eu","anticheat":"easy_anticheat","settings":{"starting_cash":10000,"limit_superweapons":true,"vanilla_teams":false,"track_stats":true,"allow_observers":false,"max_camera_height":310},"match_id":null,"start":null,"created_at":"2026-11-13T20:00:00Z","slots":[{"index":0,"state":"player","member":{"id":42,"name":"Bob","badges":[]},"ready":true,"has_map":true,"side":-1,"color":-1,"team":null,"start_position":-1}]}
```

| Field | Values |
|---|---|
| `type` | `custom` or `quick_match` |
| `state` | `setup`, `countdown`, `in_game`, `complete` |
| `visibility` | `public` or `friends` |
| `anticheat` | `none`, `go_integrated`, `easy_anticheat` |
| `match_id` | `null` until the game starts |
| `start` | `null` until the game starts, then `{match_id, rng_seed, screenshot_url}` as in `GameStart`, so a client that reconnects after the start still starts. In a `quick_match` lobby a non-null `start` also stands for `MatchStart`: the service sends `MatchStart` before any `LobbyState` that carries `start` |
| `slots` | always 8 entries (the example shows one) |

Never sent to clients: the lobby password, the matchmaker's paired user ids, mesh check bookkeeping. The lobby name is the host's text only; the region and anticheat badges the legacy service baked into the name are separate fields.

**Playlist**

```json
{"id":0,"name":"1v1 (Random Armies)","min_players":2,"desired_players":2,"min_selected_maps":1,"allow_teams":false,"team_size":null,"allow_army_selection":false,"grace_period_s":0,"maps":[{"index":0,"name":"Tournament Desert","path":"maps/tournament desert/tournament desert","custom":false,"max_players":2}]}
```

**Build identity.** The executable and INI CRCs and the anticheat id are sent once, with `POST /api/v1/auth/session`. The service uses them for every lobby and queue call of that session, so no hub method carries them. The anticheat id is the number the platform plugin reports (9481 for Easy Anti-Cheat today); the client passes the plugin's value through unchanged and never interprets it.

## 5. Hub methods (client to server)

"Result" means the method expects an answer (`invocationId` set). "Fire" means the client sends it without `invocationId` and gets no answer.

### 5.1 Channels and chat

A session is in at most one channel at a time, like Battle.net classic: joining another channel leaves the current one. A user is listed only in the channel they are in, and friends see that channel in presence.

| Method | Argument | Answer | Notes |
|---|---|---|---|
| `JoinChannel` | `{channel_id}` | Result | Then `ChannelMembers`, then `ChannelHistory`. A parent channel resolves to its default child; `ChannelMembers.channel_id` is the channel actually joined. Leaving a channel happens by joining another or `LeaveChannel` |
| `LeaveChannel` | `{}` | Result | |
| `SendMessage` | `{channel_id, type, content}` | Result `{message_id}` | `type` `chat` or `action`; `announcement` only for the host in `lobby:` channels. `dm:` requires mutual friendship, except for staff messaging (section 6.2); a message to an offline recipient is stored and answers ok. `group:` requires membership. Content 1 to 512 characters |
| `GetHistory` | `{channel_id, before?: message_id, limit}` | Result `{messages: [Message], has_more}` | `channel:`, `dm:`, `group:` and `lobby:` (current members of that lobby) only; the `limit` messages just older than `before`, or the newest when it is absent, returned oldest first like `ChannelHistory`; `has_more` is true when older messages exist; `limit` 1 to 100 |
| `MarkRead` | `{channel_id}` | Result | Marks the whole conversation read, up to the newest message the service holds at that moment; `Conversations` then shows `unread` 0 for it. The user's other connections get `ReadMarker`. `dm:` and `group:` conversations the caller is part of, otherwise `not_found {what: channel}` or `group.not_member` |

Group chats are small private conversations, like group direct messages in Discord. Groups are created and managed over REST (section 10.1); their messages use `SendMessage` to `group:<id>`.

### 5.2 Lobbies

Game sessions only. Any change other than ready, has-map, force-ready and kick clears every player's ready flag and cancels a pending start.

| Method | Argument | Answer | Who |
|---|---|---|---|
| `WatchLobbyList` | `{}` | Result `{lobbies: [LobbySummary]}` | any game session; subscribes to `LobbyListUpsert` and `LobbyListRemove` |
| `UnwatchLobbyList` | `{}` | Result | |
| `CreateLobby` | `{name, map: {name, path, official}, max_players, visibility, password: string or null, preferred_port, settings: {starting_cash, limit_superweapons, vanilla_teams, track_stats, allow_observers, max_camera_height}}` | Result `{lobby_id, turn: {username, token}}` | any game session; name and map name up to 255 characters, map path up to 512, password up to 128 |
| `JoinLobby` | `{lobby_id, password: string or null, preferred_port, has_map}` | Result `{turn: {username, token}}` | any game session; quick match lobbies only for the matched players. Before the Result the joiner gets `LobbyState`, then `ChannelHistory` for `lobby:<id>` |
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

### 5.3 Matchmaking

| Method | Argument | Answer | Notes |
|---|---|---|---|
| `JoinQueue` | `{playlist_id, map_indexes: [int]}` | Result | `map_indexes` index into the playlist's `maps`; at least `min_selected_maps`. Status then arrives as `MatchmakingStatus` |
| `LeaveQueue` | `{}` | Result | |
| `WidenSearch` | `{}` | Result | selects every map of the playlist |

### 5.4 Social

Friend requests, friends, blocks and groups are changed over REST (section 10.1). The hub delivers the results as events (section 6.5 and `GroupCreate`, `GroupUpdate`, `GroupRemoved` in section 6.2).

### 5.5 Moderation

Staff actions are REST endpoints (section 10.2): kick, mute and ban as sanctions, lifting them, and deleting messages. The hub delivers their effects: `ModerationNotice` and the close record to the target, `MessageDelete` to everyone who received a deleted message. Players report messages and players over REST as well.

### 5.6 Peer networking (game sessions only)

| Method | Argument | Answer | Notes |
|---|---|---|---|
| `SetPeerTransports` | `{transports: [string]}` | Fire | The plugin transports this client can use for peers: the provider names its platform plugins report (today `eos`), never a name the client makes up. Required after every `Ready` of a game session, before any lobby or queue call, and sent again whenever the list changes, an empty list included. The service matches the names as opaque strings; a name must be 1 to 32 characters of `a-z`, `0-9` and `_`, `gns` is reserved, at most 8 names count, and anything else is ignored (section 6.6) |
| `RequestSignalling` | `{target_user_id, failed_attempt?}` | Fire | The service sends `StartSignalling` to both peers. With `failed_attempt` it reports that attempt as failed and the pair moves to the next step of its connection order (section 6.6) |
| `Signal` | `{to, payload}` | Fire | Relayed as `Signal` to a peer in the same lobby |
| `BeginMeshCheck` | `{}` | Result | Host only, not in quick match lobbies |
| `MeshCheckResponse` | `{mesh_check_id, attempt, connected: [user_id], connecting: [user_id]}` | Fire | |
| `ProbeResponse` | `{probe_id}` | Fire | Answer to `ProbeRequest` |
| `ModuleScanResponse` | `{scan_id, modules: [{path, size}]}` | Fire | Answer to `ModuleScanRequest` |

## 6. Events (server to client)

### 6.1 Session

| Event | Data |
|---|---|
| `Ready` | `{protocol_version, session_type, user: Member, permissions: [string], features: [string], badges: {id: {text, color}}, channels: [{id, name, parent_id: int or null, show_all_matches}], limits: {chat_max_length, chat_rate: {messages, window_s}, name_min_length, name_max_length, group_max_members}}`; a channel whose `parent_id` is null is top level; `chat_rate` applies to `channel:` chat only |
| `BadgesUpdate` | `{badges: {id: {text, color}}}` (full set) |
| `FeaturesUpdate` | `{features: [string]}` (the user's full new feature list, sent to every connection of the user whenever it changes, for example after a role change) |
| `Notice` | `Notice` (section 4), for things that are not the answer to a call, such as `name.changed` |
| `ModerationNotice` | `{action: "kick"|"ban"|"mute", reason?, expires_at?}`; `reason` is absent when staff gave none; a close record follows for kick and ban, also when a banned user connects (section 3) |
| `ServerSwitching` | `{retry_after_ms}` (a planned handover to another service copy; a `server.switching` close follows; the service spreads the values so clients do not all reconnect at once). A copy that is not serving sends it to a connecting client with a `service.unavailable` close (section 3) |

**Features.** `permissions` say what the service lets the user do. `features` say which client capabilities are switched on for this player: lowercase snake_case names with no prefix, for example `stats_exporter` (the game's local stats and casting tools: camera control, follow, replay control, observing, lobby control) and `observer` (observing other players' matches). The client only checks whether a name is in the list and never sees roles. The list is sorted by name. `FeaturesUpdate` reaches every connection of the user once, within about 5 seconds of a role change. The service computes the list per connection from a per-environment map of roles to features (the `streamer` role exists for players who get these tools without being staff); later rules such as an environment switch, a beta group or a rollout share can add to it without touching permissions. When the list changes the client switches features on or off at once; switching one off mid-match only closes client tools. No feature gates anything yet: `streamer` and the two feature names are placeholders, and observing follows the lobby's `allow_observers` setting as in the original game. Plugin transports are not gated by features.

### 6.2 Channels and chat

| Event | Data |
|---|---|
| `ChannelMembers` | `{channel_id, members: [Member]}` (full list, on join and after reconnect) |
| `ChannelHistory` | `{channel_id, messages: [Message], has_more}` (after `ChannelMembers`: the last messages of the channel, oldest first; split into several events when it would exceed 64 KB) |
| `MemberAdd` | `{channel_id, member: Member}` |
| `MemberRemove` | `{channel_id, user_id}` |
| `MemberUpdate` | `{member: Member}` (rename, new tag, badge change; sent to everyone who can see the user) |
| `MessageCreate` | `Message` |
| `MessageDelete` | `{channel_id, message_id}` (staff deleted the message; sent to everyone who received it) |
| `Conversations` | `{conversations: [{channel_id, unread, last: Message or null}], groups: [Group]}` (snapshot after `Friends`: every direct message conversation and group chat of the user, newest activity first; `channel_id` is `dm:<user_id>` or `group:<group_id>`; `last` is `null` for a group with no messages yet) |
| `ReadMarker` | `{channel_id}` (the user read that whole conversation on another connection; clear its unread count) |
| `GroupCreate` | `{group: Group}` (the user was put in a group, by creating it or being added) |
| `GroupUpdate` | `{group: Group}` (members, name or owner changed; sent to every member) |
| `GroupRemoved` | `{group_id, reason: "left"|"removed"}` (the user is no longer in the group) |

**History and storage.** The service stores chat and replays it, so a reconnect or a channel switch never loses messages.

| | Stored | Replayed |
|---|---|---|
| Channel | id, channel id, author id, type, content, `created_at`, `deleted_at` | the last N on join (N from configuration, default 50) |
| Lobby | the same plus lobby id and match id | on `JoinLobby` and on rejoin after a reconnect, as `ChannelHistory` for `lobby:<id>` after `LobbyState`: the newest 50 messages since the lobby was created, block filtered |
| Direct | the same with sender and recipient | unread and recent on connect, synced across the user's clients |
| Group | the same with group id instead of recipient | the same as direct messages |

- History respects blocks: messages from a user the viewer blocked, or who blocked the viewer, are left out.
- A message sent while a `JoinChannel` or a reconnect restore is in progress can arrive both as `MessageCreate` and inside the following `ChannelHistory`; clients drop duplicates by message id.
- No chat text in application logs or telemetry, only ids and counts. Account deletion removes or anonymises the user's messages. The privacy policy must say chat is stored, why, and for how long.
- Every stored message (channel, lobby and direct) is kept 30 days, then purged. A report keeps its snapshot until its case is closed.

**Direct messages.** Only mutual friends exchange direct messages, and a block stops delivery. A message to an offline recipient is stored and delivered on their next connect through `Conversations` and `GetHistory`.

**Staff messaging.** Staff holding `chat.message_anyone` (moderators and admins) can send a direct message to any user, friend or not, and a block by that user does not stop it. The user can reply to that staff member, without being friends, for as long as the conversation holds a stored message from that staff member (30 days) and neither has blocked the other. Every staff message that uses this bypass (the two are not mutual friends, or a block exists) is written to `audit_log` with the actor, the target and the message id, never the text. When either side holds `chat.message_anyone`, a block does not hide that conversation in `Conversations` or `GetHistory`, and its messages stay reportable, so a player who blocked a staff member can still read and report what staff sent. Clients recognise staff by the author's badges.

**Group chats.** Group messages are stored, replayed and purged like direct messages and reach every connection of every member. Friendship with the owner is checked only when a member is added; a later unfriend changes nothing. Two members who block each other stay in the group, and the service hides each one's messages from the other in delivery and history. A new member sees the whole stored history as unread. Leaving or being removed deletes that member's read position for the group. `MessageDelete` for a group message goes to the current members, and any other current member may report a group message.

Messages are stored in the service database as they are; the protocol and the service add no encryption beyond TLS on the connection. Protecting the stored data (disk or volume encryption, encrypted backups) is the operator's job. Staff read a direct message only through a report or an audited lookup, and every lookup is written to `audit_log`.

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
| `StartSignalling` | `{lobby_id, user_id, preferred_port, middleware_id, attempt, transport, relay, timeout_s}` |
| `DisconnectPeer` | `{lobby_id, user_id}` |
| `Signal` | `{from, payload}` |
| `MeshCheckRequest` | `{mesh_check_id, attempt}` |
| `MeshCheckResult` | `{mesh_check_id, attempt, complete, reason, missing: [{source_user_id, target_user_id}]}` |
| `AnticheatRegister` / `AnticheatDeregister` | `{user_id, middleware_id}` |
| `ProbeRequest` | `{probe_id, url}` |
| `ModuleScanRequest` | `{scan_id}` |

Anti-cheat messages travel between the two clients of a pair over their peer connection, never through the service. `AnticheatRegister` and `AnticheatDeregister` only tell a client which peers to register with its anticheat, and a client holds a peer's messages until the pair connects.

**Connection order.** The service connects each pair of members in steps, and every `StartSignalling` names the step. Both peers of a pair get the same `attempt`, `transport`, `relay` and `timeout_s`:

1. `transport` `gns`, `relay` false: GameNetworkingSockets direct only, no TURN server.
2. `transport` set to the shared plugin transport both members listed in `SetPeerTransports`, `relay` false. Only names the service supports as a linked-account provider (section 10.4) count, because the pair needs their account ids for that provider. When they share more than one such name, the service takes the first in ordinal order; when they share none, the step is skipped. The client hands the pair to the plugin that reported that provider name. The service also needs both members' `middleware_id` (the account id of their linked account for that provider, section 10.4); while one is still unknown, that `StartSignalling` carries the step 3 values but the pair stays on step 2, and once the id arrives the service restarts the pair on step 2 with `attempt` plus one to both peers.
3. `transport` `gns`, `relay` true: GameNetworkingSockets with the Cloudflare TURN servers. Every later restart of the pair stays on this step.

`attempt` numbers the `StartSignalling` of this pair in this lobby, starting at 1, and goes up by one with every restart, whatever the reason. `timeout_s` is how long the client tries the step before giving up: 6 for step 1, 10 for step 2, 0 (no limit beyond the client's own connection timeouts) for step 3. When an attempt fails or `timeout_s` passes without a connection, the client sends `RequestSignalling` with `failed_attempt` set to that attempt. The service moves the pair to the next step only when `failed_attempt` equals the pair's current attempt, so the second peer's report of the same failure changes nothing. A restart after a failed mesh check also moves the pair to the next step. A client ignores every connection event of an older attempt of the pair and reports `attempt` in `connection_reports`.

Every `StartSignalling` carries `attempt`, `transport`, `relay` and `timeout_s`. A member whose `SetPeerTransports` has not arrived in its current session counts as listing no plugin transport, so its pairs skip step 2.

## 7. Error codes

Codes are stable strings; clients localize them. Every failed `Result` and every `Notice` uses this catalogue.

| Code | Params | Raised by |
|---|---|---|
| `unauthorized` | | Any REST call without a valid token, session token or API token alike (HTTP 401), and the close code that refuses a realtime connection without one (section 3). The answer is the same for every reason, so a caller cannot tell an unknown token from a revoked or expired one |
| `forbidden` | `{permission}` | Any method the caller has no permission for |
| `csrf` | | a request with the website sign-in cookie that changes something (POST, PATCH, PUT, DELETE) on a route that accepts the cookie, without the signed-in user's antiforgery token in the RequestVerificationToken header (HTTP 400) |
| `two_factor_required` | | any request with the website sign-in cookie by a staff account with a password and no second factor (HTTP 403) |
| `session.build_not_accepted` | | `POST /api/v1/auth/session` when the client's build is not accepted for its app in the client registry (HTTP 403) |
| `session.not_registered` | | The close code that refuses a game client connecting without `POST /api/v1/auth/session` first (section 3; HTTP 409 on a `/realtime` request that is not a WebSocket upgrade) |
| `moderation.banned` | `{reason, expires_at?}` | REST calls while play banned (HTTP 403); on the realtime connection the close code when a ban starts and when a banned user connects, after a `ModerationNotice` that carries these params (section 3) |
| `invalid_argument` | `{field}` | Malformed or missing fields |
| `not_found` | `{what}` | Unknown user, lobby, channel or message; `{what: provider}` for a linked-account route whose `:provider` the service does not support |
| `rate_limited` | `{retry_after_ms}` | Any method over its rate limit |
| `chat.rate_limited` | `{retry_after_ms}` | `SendMessage` to a `channel:` channel only (3 messages per 9 s today); `lobby:`, `dm:` and `group:` are not limited, in a lobby the host can kick instead |
| `chat.too_long` | `{max}` | `SendMessage` |
| `chat.muted` | `{expires_at?}` | `SendMessage` |
| `report.duplicate` | | `POST /api/v1/reports` when the caller already reported that message, or has an open report on that player |
| `match.result_closed` | | `PUT /api/v1/matches/:id/results/me` more than 24 hours after the match finished (after it started when it has no end) |
| `chat.not_friends` | | `SendMessage` to `dm:`, `POST /api/v1/groups`, `POST /api/v1/groups/:id/members` |
| `group.full` | `{max}` | `POST /api/v1/groups`, `POST /api/v1/groups/:id/members` |
| `group.not_owner` | | Adding, removing another member, renaming |
| `group.not_member` | | Group endpoints, and `SendMessage` or `GetHistory` to a `group:` channel the caller is not in (also for unknown group ids) |
| `name.length` | `{min, max}` | `PATCH /api/v1/me` |
| `name.invalid_characters` | | `PATCH /api/v1/me` |
| `name.whitespace` | | `PATCH /api/v1/me` |
| `name.reserved` | `{word}` | `PATCH /api/v1/me`, `PATCH /api/v1/players/:id`: the name contains a word from the reserved list (case-insensitive, anywhere in the name), the same list the Keycloak plugin uses; `word` is the listed word in lowercase |
| `name.taken` | | `PATCH /api/v1/me`, `PATCH /api/v1/players/:id`: the new name together with the player's tag is already taken |
| `name.managed` | | `PATCH /api/v1/me`, `PATCH /api/v1/players/:id` in an environment where Keycloak owns names and this environment may not rename (test and dev of the official deployment) |
| `name.unavailable` | | `PATCH /api/v1/me`, `PATCH /api/v1/players/:id` when the service that owns names (the Keycloak plugin) cannot be reached or fails (HTTP 503). Nothing changed; the client may retry later |
| `channel.unavailable` | | `JoinChannel` |
| `lobby.full`, `lobby.wrong_password`, `lobby.not_host`, `lobby.not_member`, `lobby.already_started` | | Lobby methods; `lobby.not_member` also for `GetHistory` on a `lobby:` the caller is not in |
| `lobby.not_in_setup`, `lobby.already_member`, `lobby.blocked`, `lobby.friends_only`, `lobby.not_matched`, `lobby.anticheat_mismatch`, `lobby.build_mismatch` | | `JoinLobby` |
| `lobby.slot_occupied` | `{index}` | `SetSlotState` on a player slot |
| `lobby.not_ready` | `{missing: [user_id]}` | `StartGame` |
| `lobby.mesh_incomplete` | `{missing: [user_id]}` | `StartGame` |
| `lobby.army_selection_disabled` | | `UpdateMySlot` and `UpdateSlots` in a quick match lobby whose playlist has `allow_army_selection` false, when a side other than -1 (random) is asked for; nothing changes |
| `matchmaking.not_queued`, `matchmaking.already_queued` | | Matchmaking methods |
| `matchmaking.invalid_playlist` | `{playlist_id}` | `JoinQueue` |
| `matchmaking.too_few_maps` | `{min}` | `JoinQueue` |
| `matchmaking.stats_unavailable` | | `JoinQueue` (rating could not be loaded) |
| `social.friend_list_full` | `{max}` | Sending or accepting a friend request |
| `social.blocked` | | Social and group endpoints involving a block |
| `moderation.target_offline` | | A kick of a user with no connection |
| `moderation.target_outranks` | | Sanctions and message deletes: staff only act on users of lower rank (admin above moderator above everyone else). Staff acting on themselves get `invalid_argument {field: id}` instead |
| `moderation.reason_too_long` | `{max}` | Sanctions, lifting them, message deletes, reports |
| `name.changed` | `{user_id, old_name, new_name}` | `Notice` |
| `service.unavailable` | `{what}` | A REST route whose backing service is not configured or not reachable: `linked_account` (with a `provider` param naming the provider, for both linked-account routes of section 10.4), `config`, `database` (HTTP 503). With failover on, a copy that is not serving answers `{what: standby}` with `Retry-After: 1` on REST calls and on `/-/readiness`, and refuses a realtime connection with `ServerSwitching {retry_after_ms}` and the close `service.unavailable` (section 3); clients wait that long (1 second when they cannot read the header) and retry, without showing an error, signing the player out or advancing their reconnect backoff; after 10 seconds of standby answers in a row they treat it as a normal failure. During a planned handover the copy that is handing over also answers the hub methods `CreateLobby` and `JoinQueue` with `{what: standby}` and then sends the caller `ServerSwitching`; the client follows the `ServerSwitching`, or retries after 1 second when none arrives. A lobby that is being moved is frozen from its final capture until its members are switched, usually well under a second: `JoinLobby` to that lobby or by one of its members to any other lobby, `LeaveLobby`, `UpdateLobby`, `SetLobbyPassword`, `UpdateMySlot`, `SetHasMap`, `SetReady`, `SetSlotState`, `UpdateSlots`, `KickFromLobby`, `ForceReady`, `StartGame` and `BeginMeshCheck` answer `{what: standby}`, and its countdown resumes on the new copy from the stored deadline. A kick, ban or sign-out of a member during the move takes effect when the new copy takes the lobby, or on the old copy when the move is cancelled, so that member keeps no seat. A member repeats the call after the `ServerSwitching` reconnect, a joiner who is not a member retries after 1 second |
| `session.game_required` | | `POST /api/v1/me/linked_accounts/:provider/sign_in_token` and `PUT /api/v1/me/linked_accounts/:provider` from a session that is not a game session (HTTP 403) |
| `linked_account.invalid_proof` | `{provider}` | `PUT /api/v1/me/linked_accounts/:provider` with a `proof` that does not validate for that provider |
| `linked_account.mismatch` | `{provider}` | `PUT /api/v1/me/linked_accounts/:provider` when the provider account does not belong to the caller's binding (HTTP 403) |
| `linked_account.in_use` | `{provider}` | `PUT /api/v1/me/linked_accounts/:provider` when another user holds that provider account (HTTP 409) |
| `linked_account.locked` | `{provider}` | `PUT /api/v1/me/linked_accounts/:provider` while the caller is in a lobby with another account of that provider (HTTP 409) |
| `token.limit_reached` | `{max}` | Minting a token over the active-token limit of its owner |
| `method_not_allowed` | | A REST path that exists without that HTTP method (HTTP 405); an unknown path is `not_found {what: route}` |
| `conflict` | | Token rotation of a token that is revoked, expired or already rotated; a duplicate service account name (HTTP 409) |
| `server.announcement` | `{message}` | `Notice` to every connection from `POST /api/v1/announcements` and the Discord `/announce` command |

REST endpoints return the same codes as `{"error": "<code>", "params": {...}}` with an HTTP status: 400 for `invalid_argument`, 401 `unauthorized` for a missing, unknown, expired or revoked token, 403 for `forbidden`, `moderation.target_outranks`, `session.game_required` and `linked_account.mismatch`, 404 for `not_found`, 409 for `report.duplicate`, `linked_account.in_use`, `linked_account.locked` and `conflict`, 422 for every other rule failure, 429 for `rate_limited`, 405 for `method_not_allowed`, 503 for `service.unavailable`.

New codes are added freely; clients show a generic message for codes they do not know.

## 8. Groups and delivery

| Group | Members | Used for |
|---|---|---|
| `user:<id>` | Every connection of that user | Social events, moderation notices, `Friends`, direct messages, group chat, `Conversations`, `ReadMarker`, `GroupCreate`, `GroupUpdate`, `GroupRemoved`, `FeaturesUpdate` |
| `user:<id>:game` | That user's game connection | Peer networking, `LobbyState`, matchmaking |
| `channel:<id>` | Connections in that channel | `ChannelMembers`, `MemberAdd`/`Remove`, channel chat, `MessageDelete` |
| `lobby:<id>` | Game connections of lobby members | `LobbyState` until the game starts, lobby chat, countdown events. From the start `LobbyState` goes to each member through `user:<id>:game`, because `start.screenshot_url` is per member |
| `lobby-list` | Connections that called `WatchLobbyList` | `LobbyListUpsert`, `LobbyListRemove` |

Rules:

- One connection per user and session type. A new connection of the same type closes the old one with `session.superseded`.
- Group membership is per connection and rebuilt from the user's state on every connect, so reconnects never leave stale groups.
- Events about a user (`MemberUpdate`, `FriendPresence`) go to every group that can see them, deduplicated per connection.
- Blocks are enforced by the service: a `MessageCreate` is never delivered to a user who blocked its author, or by a user to someone who blocked them, in channels, lobbies, direct messages and group chats. History follows the same rule. Clients do not need to filter.
- A successful `JoinChannel` is always followed by `ChannelMembers`, then `ChannelHistory`, for that channel.
- Delivery to one connection is in order. There is no ordering between different connections.

## 9. Session types

| | Game | Launcher | Chat |
|---|---|---|---|
| Channels and chat | yes | yes | yes |
| Direct messages, group chats, social | yes | yes | yes |
| Lobby list (watch) | yes | yes | no |
| Join or create lobbies, matchmaking | yes | no | no |
| Peer networking, anticheat, probes | yes | no | no |

Methods outside a session type's column return `forbidden`. REST endpoints (section 10) do not depend on a session type: they need the access token and, for staff actions, the permission.

## 10. REST endpoints

The split: the hub carries what acts on or notifies live sessions and needs to be fast (lobbies, matchmaking, chat, signalling, presence). REST carries everything durable, everything callers without a hub session also need (the website, the Discord bot, partners, the CLI), and everything a client must be able to retry safely. When a REST call changes something live players see, the service pushes the change through the hub.

Conventions: base `/api/v1` on the same origin as `/realtime`, `Authorization: Bearer <access token>`, JSON bodies with the same field names as the hub, success `200` with the answer object or `204` without one, failures as in section 7. A request body on `DELETE` is optional and only carries a reason. Example bodies live in `docs/api-fixtures/`. The full REST route map (sign-in, profiles, stats, leaderboards, match history, config, tokens, admin) is in the API design; this section lists the endpoints that replace earlier hub methods.

The staff routes `PATCH /api/v1/players/:id`, `POST /api/v1/players/:id/sanctions`, `DELETE /api/v1/sanctions/:id`, `GET /api/v1/reports/search` and `PATCH /api/v1/reports/:id` also accept the website sign-in cookie of the environment's own sign-in pages. Any Authorization header keeps the bearer path; a cookie change needs the antiforgery header (`csrf`); an Origin other than the host's own gets 403 `forbidden`; staff password accounts without two-factor get `two_factor_required`. Permissions, rank rule and audit (source `api`) are those of a bearer caller.

### 10.1 Social and groups

| Method and path | Body | Answer | Notes |
|---|---|---|---|
| `GET /api/v1/me/friends` | | `{friends, incoming, outgoing}` as in `Friends` | read only |
| `GET /api/v1/me/blocks` | | `{blocked: [Member]}` | read only |
| `POST /api/v1/me/friend_requests` | `{user_id}` | 204 | the other user gets `FriendRequestReceived` |
| `POST /api/v1/me/friend_requests/:user_id/accept` | | 204 | both get the friendship events of section 6.5 |
| `DELETE /api/v1/me/friend_requests/:user_id` | | 204 | declines an incoming or cancels an outgoing request |
| `DELETE /api/v1/me/friends/:user_id` | | 204 | both get `FriendRemoved` |
| `PUT /api/v1/me/blocks/:user_id` | | 204 | blocks; a friendship between the two ends |
| `DELETE /api/v1/me/blocks/:user_id` | | 204 | unblocks |
| `POST /api/v1/groups` | `{user_ids: [user_id], name?}` | `{group_id}` | The caller becomes the owner. `user_ids` are mutual friends of the caller with no block either way (`chat.not_friends`, `social.blocked`); more members than the limit is `group.full`. `name` up to 32 characters. Every member gets `GroupCreate` |
| `POST /api/v1/groups/:id/members` | `{user_ids: [user_id]}` | 204 | Owner only (`group.not_owner`); the same friendship, block and size rules as `POST /api/v1/groups`. New members get `GroupCreate` and see the stored history of the group, the others get `GroupUpdate` |
| `DELETE /api/v1/groups/:id/members/:user_id` | | 204 | With another member's id: owner only, never the owner themselves. The removed user gets `GroupRemoved` with reason `removed` and loses the history; the others get `GroupUpdate`. With the caller's own id (leaving): any member. The leaver gets `GroupRemoved` with reason `left`, the others `GroupUpdate`. When the owner leaves, the member who joined earliest becomes owner. When the last member leaves, the group is deleted; its stored messages follow the normal 30-day retention |
| `PATCH /api/v1/groups/:id` | `{name: string or null}` | 204 | Owner only; `null` clears the name. Members get `GroupUpdate` |

A group has one owner and at most `limits.group_max_members` members, the owner included; the limit is configured per environment (2 to 50, default 10) and `Ready` reports the live value. Names are `null` or 1 to 32 characters without control characters. Creating or adding checks blocks first, then the size limit, then friendship; adding someone who is already a member succeeds without change; an empty `user_ids` is `invalid_argument {field: user_ids}`. Removing a user who is not a member is `not_found {what: user}`. Every group endpoint on a group the caller is not in, or that does not exist, returns `group.not_member`.

### 10.2 Moderation and reports

Each endpoint is a `perm:<permission>` policy, so the service decides from the caller's roles in this environment (local roles and roles synced from the upstream sign-in). Moderators hold `moderation.kick`, `moderation.mute`, `chat.delete_any` and `chat.message_anyone`; admins also hold `moderation.ban`. Staff only act on users of lower rank: a moderator cannot kick, mute or ban an admin or another moderator, or lift their sanctions. Kick, mute and ban are sanctions of one record type: a mute is a sanction of type `mute`, so the user keeps playing but cannot send messages. Every action is written to `audit_log` with the actor, the target and the reason; the reason is stored only there.

- Reasons are optional, except on a ban, where the reason is required and not blank (`invalid_argument {field: reason}`). Reasons are up to 512 characters.
- `duration_s` is whole seconds, at least 60 and a multiple of 60, at most 10 years (315360000); otherwise `invalid_argument {field: duration_s}`. Clients offer presets (10 minutes, 1 hour, 1 day, 7 days, 30 days, permanent for bans).
- Mutes are never permanent: `duration_s` is required for a mute. A ban without `duration_s` is permanent. A kick has no duration.

| Method and path | Body | Answer | Notes |
|---|---|---|---|
| `POST /api/v1/players/:id/sanctions` | `{type: "kick"|"mute"|"ban", duration_s?, reason?}` | `{sanction_id}` | permission by type: `moderation.kick`, `moderation.mute`, `moderation.ban`. A kick needs a connected target (`moderation.target_offline`). The target gets `ModerationNotice`; a kick or ban then closes their connections (section 3) |
| `GET /api/v1/players/:id/sanctions?active=` | | `{sanctions: [{id, type, reason, created_at, expires_at, lifted_at, created_by: Member}]}` | `moderation.kick` or higher |
| `DELETE /api/v1/sanctions/:id` | `{reason?}` | 204 | lifts a mute or ban; `moderation.mute` or `moderation.ban` by type |
| `PATCH /api/v1/players/:id` | `{name, reason?}` | 204 | `moderation.rename`; replaces an offensive name, keeps the static tag, follows the staff rank rule and writes `audit_log`. The Discord bot uses the same endpoint. Same environment rules as `PATCH /api/v1/me` |
| `POST /api/v1/players/:id/reset_name` | `{reason?}` | 204 | `moderation.rename`; for a name that cannot stay while staff have no replacement. Gives the player a placeholder name (`Player` and six digits) with a free tag, and marks a name as owed: on their next sign-in they choose one at `/users/sign_up/welcome`, and token refreshes answer `invalid_grant` until they do. Their sign-ins are revoked and their connections close with `auth.revoked`. Follows the staff rank rule and writes `audit_log` (`name_reset`, from and to) |
| `DELETE /api/v1/messages/:id` | `{reason?}` | 204 | `chat.delete_any`; any channel, lobby, direct or group message. Follows the staff rank rule (`moderation.target_outranks`): staff delete messages of lower-ranked users and their own. No player delete, no edits. Everyone who received the message gets `MessageDelete`; history then returns a tombstone (section 4). The stored message keeps `deleted_at` only; who deleted it and why are in `audit_log` |
| `POST /api/v1/reports` | `{message_id?, user_id?, reason?}` | `{report_id}` | exactly one of `message_id` and `user_id`. A message: never the author; a channel message by any user not blocked either way with its author, a lobby message by anyone who was a member of that lobby (until 24 h after it ends) or a player of its match, a direct message by its recipient, a group message by any other current member; one report per user per message (`report.duplicate`); the service snapshots the message, up to 10 messages before it and, before the message is purged, up to 10 messages after it in the same conversation, kept until the case is closed; reasons up to 512 characters. A player: any other user, one open report per reporter and player |
| `GET /api/v1/reports?status=` | | `{reports: [{id, reporter: Member, reported: Member, message_id, reason, status, created_at, resolved_at, resolution_note}]}` | the staff queue for message and player reports, `moderation.kick` or higher; `message_id` is `null` for a report of a player; `status` is `open`, `actioned` or `dismissed` |
| `GET /api/v1/reports/search?status=&search=&sort=&page=&per_page=` | | `{reports: [as GET /api/v1/reports]}`, offset paged | the staff queue paged in the database, `moderation.kick` or higher; `status` is one or more of `open`, `actioned`, `dismissed` comma separated, or `all` (default `open`); `search` matches the reason or either player's name; `sort` `asc` (default) or `desc` by creation |
| `PATCH /api/v1/reports/:id` | `{status: "actioned"|"dismissed", resolution_note?}` | 204 | closes a report, `moderation.kick` or higher |

### 10.3 Profile, matches and playlists

| Method and path | Body | Answer | Notes |
|---|---|---|---|
| `PATCH /api/v1/me` | `{name}` | 204 | 3 to 16 characters, no `#`. The tag is static: it never changes, and a rename whose `name#tag` is taken answers `name.taken`. On success everyone who can see the user gets `MemberUpdate`. In the official deployment prod performs the rename through the Keycloak plugin and the change reaches every environment (identity section 5b); test and dev answer `name.managed` |
| `PUT /api/v1/matches/:id/results/me` | the match result: `{won, desynced, side, duration_s, stats: {buildings_built, buildings_killed, buildings_lost, units_built, units_killed, units_lost, total_money}}` | `{screenshot_url, replay_url}` | a player of that match; the URLs are presigned upload targets. Idempotent: repeating the same body returns the same answer, so a client retries freely after a lost connection, until 24 hours after the match finished (`match.result_closed` after that) |
| `GET /api/v1/matchmaking/playlists` | | `{playlists: [Playlist]}` | cacheable with `ETag` |
| `POST /api/v1/lobbies/:id/connection_reports` | `{target_user_id, outcome, transport, ip_version, attempt, events?: [{state, t_ms}]}` | 204 | the final result of one connection attempt to one peer (replaces `ConnectionOutcome`), sent once when the attempt ends. `outcome` is `connected_direct`, `connected_relay`, `failed` or `disconnected`; `transport` is the `transport` of that attempt's `StartSignalling`: `gns` or a plugin transport name, with the same syntax as in `SetPeerTransports`; `ip_version` is 4 or 6. `attempt` (1 or more) is the `attempt` of that `StartSignalling` and numbers the attempts of this pair in this lobby; a connection that drops during the match is sent again with the same `attempt` and its events ending in `disconnected`, a reconnect after that is a new attempt. `events` lists every state change of the attempt, oldest first, at most 32: `state` is `connecting`, `finding_route`, `connected_direct`, `connected_relay`, `failed` or `disconnected`, `t_ms` the milliseconds since the attempt started, never decreasing, at most 600000; anything else is `invalid_argument`. The caller must be a member of that lobby (`lobby.not_member`), checked before the body is validated; `target_user_id` must be another player who is or was a member of that lobby, otherwise `invalid_argument {field: target_user_id}`. Stored per player for 30 days, keyed by reporter, lobby, target and attempt, so a repeated report replaces the earlier one; staff use it to troubleshoot a player's connections and to line up both sides of a pair. Never stores IP addresses. Account deletion removes them, and the privacy policy must name them with their purpose and retention. Metrics are counted by outcome, transport and IP version; a `transport` that is neither `gns` nor a supported provider counts as `other` |

### 10.4 Linked accounts

A platform plugin of the game client (EOS today) links the player's account at that platform and signs in to it with a token from the service. The game never names a platform: the plugin reports its provider name (today `eos`) and the game puts that string into these routes and into `SetPeerTransports`. `:provider` is matched as an opaque string; one the service does not support answers `not_found {what: provider}`. Both routes need a game session (`session.game_required`), and a provider whose configuration is missing or whose backing service cannot be reached answers `service.unavailable {what: linked_account, provider}`.

| Method and path | Body | Answer | Notes |
|---|---|---|---|
| `POST /api/v1/me/linked_accounts/:provider/sign_in_token` | | `{token, expires_in}` | a short-lived token the plugin hands to its platform to sign the player in; `expires_in` in seconds. For `eos`: an RS256 JWT with `sub` the user id, `aud` the configured audience (`genonline-eos`), the `display_name` claim and a lifetime of 300 s; its keys are published at `/.well-known/jwks.json` |
| `PUT /api/v1/me/linked_accounts/:provider` | `{proof}` | 204 | links the caller to their account at that provider, the account id becoming their `middleware_id` (section 6.6). `proof` is the provider's own proof of that account; for `eos` the Epic Connect ID token, validated against Epic's keys and the caller's account binding. Failures: `linked_account.invalid_proof`, `linked_account.mismatch`, `linked_account.in_use`, `linked_account.locked`, all with `{provider}` |

### 10.5 Other REST

Sign-in, token refresh and logout, `POST /api/v1/auth/session`, config and MOTD, the compatibility check, player profiles and stats, global stats, leaderboards and match history. They are listed in `docs/api.md`.

## 11. Contract fixtures and versioning

- `docs/realtime-fixtures/` holds one JSON file per method argument, result and event (`SendMessage.request.json`, `SendMessage.result.json`, `MessageCreate.event.json`, ...). Each file is the single argument object of the hub message, not the SignalR record around it; `docs/realtime-fixtures/README.md` lists the naming rules.
- A service test serializes the real C# types and compares them with the fixtures. A client test parses the same fixtures with the C++ code. Changing a message without updating the fixtures fails both.
- `Ready.protocol_version` is `1`. Additive changes (new methods, events, fields, codes) keep version 1. A breaking change ships as a new hub path (`/realtime/v2`) next to the old one.

## 12. Legacy reference

For porting client code only: where the behaviour of each legacy `/ws` message lives in v1. The legacy protocol is not served by the new deployment.

| Legacy | v1 |
|---|---|
| 1 / 2 room chat, 10 / 11 lobby chat, 30 / 31 friend chat | `SendMessage` and `MessageCreate` |
| 3 change room, 4 member list | `JoinChannel` / `LeaveChannel`, `ChannelMembers`, `MemberAdd` / `Remove` / `Update` |
| 5 mark ready | `SetReady` |
| 6 current lobby dirty | `LobbyState` with content |
| 7 lobby list dirty | `LobbyListUpsert` / `LobbyListRemove` |
| 8 anticheat | Dropped: anti-cheat messages travel over the peer connection |
| 9 name change, name-change chat text | `PATCH /api/v1/me`, `name.*` codes, `MemberUpdate`, `Notice name.changed` |
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
| 46 moderation notice, 47 / 48 moderation command | `ModerationNotice`, `POST /api/v1/players/:id/sanctions` |
| REST lobby and matchmaking mutations | Hub methods in section 5 |
| REST social mutations | REST in section 10.1, results as hub events |

## 13. Open questions

- [x] Lobby settings, `LobbySummary`, `LobbyState`, match outcome and queue fields: fixed in sections 4 and 5 from the current lobby, slot, playlist and outcome models.
- [x] `MatchFound.map`: the service sends the real map; the client no longer hard-codes one.
- [x] Reconnect: SignalR's built-in stateful reconnect is not used (it needs the same service copy and the negotiate step). Snapshots, lobby chat history, resent `StartSignalling` and `LobbyState.start` make a reconnect safe from any copy. A replay by sequence number can be added later without a version bump.
- [ ] Friend presence across session types: v1 reports every connected client type in `clients`.
- [ ] Multiple service processes: one active copy and a hot standby, with a brief overlap during a planned handover (`docs/realtime-failover.md`). Not built yet; until then v1 runs one copy per environment.
