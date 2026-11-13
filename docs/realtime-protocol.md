# Realtime protocol v1 (SignalR)

The game client, the launcher and the web chat talk to the service over one SignalR hub at `/v1/realtime`. Every command gets a typed result, every server event carries its data, and the service never sends display text: clients render names, badges and messages themselves. This replaces the legacy `/ws` protocol completely; there is no compatibility layer.

Status: draft for review. Field lists marked *open* are decided during implementation.

## Quick path

1. Log in over REST as today and keep the session token.
2. Open `wss://<host>/v1/realtime` with `Authorization: Bearer <session token>` and send the SignalR handshake.
3. Wait for `Ready`, then the snapshot events (`Friends`, and `RoomMembers` or `LobbyState` when rejoining).
4. Call hub methods for actions; each returns a `Result`.
5. Handle server events by name; ignore events and fields you do not know.

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
12. [Legacy mapping](#12-legacy-mapping)
13. [Open questions](#13-open-questions)

## 1. Transport

| Topic | Decision |
|---|---|
| Endpoint | `wss://<environment host>/v1/realtime`, TLS on port 443 only |
| Transport | WebSockets only. The client connects directly (`skipNegotiation`), no HTTP negotiate call, no long polling, no QUIC |
| HTTP version | The upgrade is plain HTTP/1.1, so the traffic looks like ordinary HTTPS on restrictive networks |
| Hub protocol | SignalR JSON hub protocol, version 1 ([spec](https://github.com/dotnet/aspnetcore/blob/main/src/SignalR/docs/specs/HubProtocol.md)) |
| Framing | Text frames. Every record is a JSON object followed by the record separator byte `0x1E` |
| Auth | `Authorization: Bearer <session token>` on the upgrade request. Browsers cannot set that header, so the web chat passes `access_token` in the query string; the service strips it from request logs |
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
{"type":1,"invocationId":"5","target":"SendMessage","arguments":[{"channel_id":"room:3","type":"chat","content":"hi"}]}
{"type":3,"invocationId":"5","result":{"ok":true,"data":{"message_id":"8812"}}}
{"type":1,"target":"MessageCreate","arguments":[{"id":"8812","channel_id":"room:3","type":"chat","author":{"id":42,"name":"Bob","badges":[]},"content":"hi"}]}
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
| Ids | User, lobby and room ids are JSON numbers (all fit in 53 bits). Message ids are strings |
| Channel ids | Typed strings: `room:<room_id>`, `lobby:<lobby_id>`, `dm:<user_id>` |
| Time | ISO 8601 UTC strings (`2026-11-13T20:00:00Z`); durations in milliseconds with an `_ms` suffix |
| Binary data | Base64 strings (signalling, anticheat payloads) |
| Text | The service sends codes and parameters, never display text. The only free text is user content (chat, reasons typed by staff) |
| Unknown data | Clients ignore unknown events and unknown fields. The service ignores unknown fields |
| Results | Every method that expects an answer returns a `Result` (section 4). Hub errors (`completion.error`) only happen for protocol bugs, never for normal failures |

## 3. Connection lifecycle

```
connect ──> handshake ──> Ready ──> snapshots ──> events / commands ──> close
                                                       │
                              connection lost ─────────┘──> reconnect (backoff) ──> Ready ──> snapshots
```

1. **Connect** with the session token. An invalid or revoked token fails the upgrade with HTTP 401; a banned user gets HTTP 423 with `{"code":"moderation.banned","params":{"reason":"...","until":"..."}}`.
2. **Ready** is always the first event. It carries the user, permissions, badge definitions and limits (section 6.1).
3. **Snapshots** follow immediately: `Friends` always; `RoomMembers` if the session was in a room; `LobbyState` if the user is in a lobby. After a reconnect these restore everything, so the client never re-fetches state over REST.
4. **Reconnect** on any lost connection: wait 0.5 to 3 s random, then 2 s doubling to a 30 s cap, forever while logged in. During a match the client keeps reconnecting without a time window.
5. **Close** (`type 7`) carries a code in `error` and `allowReconnect`:

| `error` | `allowReconnect` | Meaning | Client does |
|---|---|---|---|
| `session.superseded` | false | Same user and session type connected elsewhere | Stop, show "signed in elsewhere" |
| `auth.revoked` | false | Tokens revoked (logout, ban, role change that requires it) | Refresh the token once, otherwise return to login |
| `moderation.kicked` | true | Staff kicked the user | Show the `ModerationNotice` that came before, reconnect after 10 s |
| `moderation.banned` | false | Banned | Show the `ModerationNotice`, return to login |
| `server.restarting` | true | Planned restart | Reconnect with backoff |
| `protocol.error` | false | Malformed traffic | Log and return to login (client bug) |

Missed events while disconnected are not replayed in v1: state is restored by the snapshots, chat sent during the gap is lost. Stateful reconnect can be added later without a breaking change (section 13).

## 4. Shared objects

**Result** (returned by every method that expects an answer)

```json
{"ok":true,"data":{}}
{"ok":false,"code":"chat.rate_limited","params":{"retry_after_ms":4200}}
```

**Member** (a user as other users see them; used in chat, rooms, lobbies, friends)

```json
{"id":7,"name":"Ann","badges":["moderator"],"client":"game"}
```

- `badges`: badge ids; definitions arrive in `Ready` and `BadgesUpdate`.
- `client`: `game`, `launcher` or `chat`. Present in member lists, omitted in chat messages.

**Message**

```json
{"id":"8812","channel_id":"room:3","type":"chat","author":{"id":7,"name":"Ann","badges":["moderator"]},"content":"hello"}
```

`type`: `chat`, `action` (an emote, rendered as "Ann waves"), `announcement` (lobby host only, rendered prominently).

**Notice**

```json
{"code":"name.changed","params":{"user_id":7,"old_name":"Ann","new_name":"Anna"}}
```

**LobbySummary** (lobby list entry) and **LobbyState** (full state of the lobby the user is in): same fields as the current `GET Lobby/{id}` model in `snake_case`, with slots as `Member` plus slot data. *Open:* the exact field list is fixed during implementation from the current lobby model.

## 5. Hub methods (client to server)

"Result" means the method expects an answer (`invocationId` set). "Fire" means the client sends it without `invocationId` and gets no answer.

### 5.1 Rooms and chat

| Method | Argument | Answer | Notes |
|---|---|---|---|
| `JoinRoom` | `{room_id}` | Result | Then `RoomMembers` event. Leaving a room happens by joining another or `LeaveRoom` |
| `LeaveRoom` | `{}` | Result | |
| `SendMessage` | `{channel_id, type, content}` | Result `{message_id}` | `type` `chat` or `action`; `announcement` only for the host in `lobby:` channels. `dm:` requires mutual friendship. Content 1 to 512 characters |
| `SetDisplayName` | `{name}` | Result | 3 to 16 characters. On success everyone who can see the user gets `MemberUpdate` |

### 5.2 Lobbies

| Method | Argument | Answer | Notes |
|---|---|---|---|
| `WatchLobbyList` | `{}` | Result `{lobbies: [LobbySummary]}` | Subscribes to `LobbyListUpsert` and `LobbyListRemove` |
| `UnwatchLobbyList` | `{}` | Result | |
| `CreateLobby` | `{settings}` | Result `{lobby_id}` | Then `LobbyState`. *Open:* settings fields |
| `JoinLobby` | `{lobby_id, password?}` | Result | Then `LobbyState` |
| `LeaveLobby` | `{}` | Result | |
| `UpdateLobby` | `{settings}` | Result | Host only. Partial update, only the fields present change |
| `SetLobbyPassword` | `{password}` | Result | Host only. `null` removes it. The password is never sent back to anyone |
| `KickFromLobby` | `{user_id}` | Result | Host only |
| `SetReady` | `{ready}` | Result | Then `LobbyState` for everyone in the lobby |
| `StartGame` | `{}` | Result | Host only. The service runs the ready check and the countdown and answers `lobby.not_ready`, `lobby.mesh_incomplete` and similar codes when it cannot start |
| `ReportMatchOutcome` | `{match_id, outcome}` | Result | *Open:* outcome fields from the current `Lobby/Outcome` model |

### 5.3 Matchmaking

| Method | Argument | Answer | Notes |
|---|---|---|---|
| `GetPlaylists` | `{}` | Result `{playlists}` | |
| `JoinQueue` | `{playlist_id, ...}` | Result | *Open:* fields from the current `PUT Matchmaking` body |
| `LeaveQueue` | `{}` | Result | |
| `WidenSearch` | `{}` | Result | |

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

Checked against the caller's permissions (`moderation.kick`, `moderation.mute`, `moderation.ban`). Every action is written to the moderation log.

| Method | Argument | Answer |
|---|---|---|
| `Kick` | `{user_id, reason}` | Result |
| `Mute` | `{user_id, duration_ms, reason}` | Result |
| `Unmute` | `{user_id, reason}` | Result |
| `Ban` | `{user_id, duration_ms?, reason}` | Result (no `duration_ms` means permanent) |

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
| `Ready` | `{protocol_version, session_type, user: Member, permissions: [string], badges: {id: {text, color}}, limits: {chat_max_length, chat_rate: {messages, window_ms}, name_min_length, name_max_length}}` |
| `BadgesUpdate` | `{badges: {id: {text, color}}}` (full set) |
| `Notice` | `Notice` (section 4), for things that are not the answer to a call, such as `name.changed` |
| `ModerationNotice` | `{action: "kick"|"ban"|"mute", reason, until?}`; a close record follows for kick and ban |

### 6.2 Rooms and chat

| Event | Data |
|---|---|
| `RoomMembers` | `{room_id, members: [Member]}` (full list, on join and after reconnect) |
| `MemberAdd` | `{channel_id, member: Member}` |
| `MemberRemove` | `{channel_id, user_id}` |
| `MemberUpdate` | `{member: Member}` (rename, badge change; sent to everyone who can see the user) |
| `MessageCreate` | `Message` |

### 6.3 Lobbies

| Event | Data |
|---|---|
| `LobbyListUpsert` | `{lobby: LobbySummary}` (new or changed lobby, for lobby list watchers) |
| `LobbyListRemove` | `{lobby_id}` |
| `LobbyState` | `{lobby: LobbyState}` (full state after every change, to members of that lobby) |
| `LobbyLeft` | `{lobby_id, reason}` (`left`, `kicked`, `closed`) |
| `CountdownStarted` | `{lobby_id, duration_ms}` |
| `CountdownCancelled` | `{lobby_id, reason, missing: [user_id]?}` |
| `GameStart` | `{lobby_id, match_id, screenshot_url?}` |

### 6.4 Matchmaking

| Event | Data |
|---|---|
| `MatchmakingStatus` | `{code, params}` (for example `matchmaking.searching` with `{players, needed}`) |
| `MatchFound` | `{lobby_id, map}` |
| `MatchSetupProgress` | `{timeout_ms, countdown_ms}` |
| `MatchStart` | `{lobby_id}` |
| `MatchRequeue` | `{reason}` |

### 6.5 Social

| Event | Data |
|---|---|
| `Friends` | `{friends: [Member + online], incoming: [Member], outgoing: [Member], blocked: [Member]}` (snapshot after `Ready`) |
| `FriendRequestReceived` | `{from: Member}` |
| `FriendRequestAccepted` | `{by: Member}` |
| `FriendRemoved` | `{user_id}` |
| `FriendPresence` | `{user_id, online, clients: [string]}` (keyed by user id, never by name) |

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
| `invalid_argument` | `{field}` | Malformed or missing fields |
| `not_found` | `{what}` | Unknown user, lobby, room or channel |
| `rate_limited` | `{retry_after_ms}` | Any method over its rate limit |
| `chat.rate_limited` | `{retry_after_ms}` | `SendMessage` (3 messages per 9 s today) |
| `chat.too_long` | `{max}` | `SendMessage` |
| `chat.muted` | `{until?}` | `SendMessage` |
| `chat.not_friends` | | `SendMessage` to `dm:` |
| `chat.recipient_offline` | | `SendMessage` to `dm:` |
| `name.length` | `{min, max}` | `SetDisplayName` |
| `name.invalid_characters` | | `SetDisplayName` |
| `name.whitespace` | | `SetDisplayName` |
| `name.reserved` | `{word}` | `SetDisplayName` (staff words and badge texts) |
| `name.taken` | | `SetDisplayName` |
| `room.unavailable` | | `JoinRoom` |
| `lobby.full`, `lobby.wrong_password`, `lobby.not_host`, `lobby.not_member`, `lobby.already_started` | | Lobby methods |
| `lobby.not_ready` | `{missing: [user_id]}` | `StartGame` |
| `lobby.mesh_incomplete` | `{missing: [user_id]}` | `StartGame` |
| `matchmaking.not_queued`, `matchmaking.already_queued` | | Matchmaking methods |
| `social.friend_list_full` | `{max}` | `SendFriendRequest`, `AcceptFriendRequest` |
| `social.blocked` | | Social methods involving a block |
| `moderation.target_offline` | | `Kick` |
| `moderation.reason_too_long` | `{max}` | Moderation methods |
| `name.changed` | `{user_id, old_name, new_name}` | `Notice` |

New codes are added freely; clients show a generic message for codes they do not know.

## 8. Groups and delivery

| Group | Members | Used for |
|---|---|---|
| `user:<id>` | Every connection of that user | Social events, moderation notices, `Friends` |
| `user:<id>:game` | That user's game connection | Peer networking, `LobbyState`, matchmaking |
| `room:<id>` | Connections in that room | `RoomMembers`, `MemberAdd`/`Remove`, room chat |
| `lobby:<id>` | Game connections of lobby members | `LobbyState`, lobby chat, countdown events |
| `lobby-list` | Connections that called `WatchLobbyList` | `LobbyListUpsert`, `LobbyListRemove` |

Rules:

- One connection per user and session type. A new connection of the same type closes the old one with `session.superseded`.
- Group membership is per connection and rebuilt from the user's state on every connect, so reconnects never leave stale groups.
- Events about a user (`MemberUpdate`, `FriendPresence`) go to every group that can see them, deduplicated per connection.
- Delivery to one connection is in order. There is no ordering between different connections.

## 9. Session types

| | Game | Launcher | Chat |
|---|---|---|---|
| Rooms and chat | yes | yes | yes |
| Direct messages, social | yes | yes | yes |
| Lobby list (watch) | yes | yes | no |
| Join or create lobbies, matchmaking | yes | no | no |
| Peer networking, anticheat, probes | yes | no | no |
| Moderation methods (with permission) | yes | yes | yes |

Methods outside a session type's column return `forbidden`.

## 10. What stays on REST

Anything that does not need a live session stays on REST: login and token refresh, logout, the EOS token, MOTD and service config, version check, player stats, global stats, leaderboards and match history. Everything that acts on or notifies live sessions moves to the hub.

## 11. Contract fixtures and versioning

- `docs/realtime-fixtures/` holds one JSON file per method argument, result and event (`SendMessage.request.json`, `SendMessage.result.json`, `MessageCreate.event.json`, ...).
- A service test serializes the real C# types and compares them with the fixtures. A client test parses the same fixtures with the C++ code. Changing a message without updating the fixtures fails both.
- `Ready.protocol_version` is `1`. Additive changes (new methods, events, fields, codes) keep version 1. A breaking change ships as a new hub path (`/v2/realtime`) next to the old one.

## 12. Legacy mapping

Reference for the client team: where each legacy `/ws` message ends up. Nothing here is kept on the wire.

| Legacy | v1 |
|---|---|
| 1 / 2 room chat, 10 / 11 lobby chat, 30 / 31 friend chat | `SendMessage` and `MessageCreate` |
| 3 change room, 4 member list | `JoinRoom` / `LeaveRoom`, `RoomMembers`, `MemberAdd` / `Remove` / `Update` |
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

- [ ] Lobby settings, `LobbySummary`, `LobbyState`, match outcome and queue fields: derive from the current models during implementation.
- [ ] `MatchFound.map`: the current client hard-codes "Alpine Assault" for prearranged lobbies; the service should send the real map.
- [ ] Stateful reconnect (message replay after short drops): not in v1, can be added without a version bump.
- [ ] Friend presence across session types: v1 reports every connected client type in `clients`.
- [ ] Multiple service processes: groups work with a Redis backplane, but lobby and session state are in memory, so v1 assumes one `application` process per environment.
