# Running more than one realtime copy

The realtime hub (`/realtime`, see `realtime-protocol.md`) is built so an environment can later run more than one service copy, for deploys without downtime. Today one copy per environment is the supported setup.

## PostgreSQL only

Every multi copy feature (the Redis backplane, failover and handover) needs `Database:db_provider` `postgres`. With `mysql` or `mariadb` the service runs one copy: it refuses to start with `Realtime:backplane` `redis` or `Realtime:failover:enabled` `true`, and the error names the setting and PostgreSQL.

## One copy

Nothing extra. `Realtime:backplane` defaults to `none`: hub messages and close requests stay inside the process.

## What the code already does for several copies

- Every server event is addressed through SignalR groups, never through a connection id the service looked up: `user:<id>` (every connection of the user), `user:<id>:game`, `user:<id>:launcher`, `user:<id>:chat` (the one connection of that session type), `channel:<id>`, `lobby:<id>`. With a backplane these reach connections on every copy.
- SignalR users are named by the user id in the session principal (`RealtimeUserIdProvider`, the OpenIddict subject). Server events still go through `user:<id>`, which a connection joins just before `Ready`, so `Ready` stays the first event.
- Kicks, bans and revocations are close requests (`RealtimeControl.RequestClose`). The copy that issues one closes its own matching sessions first, then publishes it; every other copy closes only its own matching sessions with the same close record (`moderation.kicked` or `moderation.banned` after the `ModerationNotice`, `auth.revoked`). A sign-in revocation closes only the connections of that sign-in on every copy.
- A kick of a user this copy does not hold waits up to 2 seconds for another copy to confirm it closed a session, otherwise it answers `moderation.target_offline` as before.
- Lobby ids come from the `lobby_ids` counter row in the database. Each copy reserves a block of 100 ids with one atomic update and reserves the next block in the background when 20 are left, so no restart and no other copy ever hands out the same lobby id again. The ids left in a block when a copy stops are skipped. A database outage fails only the `CreateLobby` that needs a new block.

## Active and standby copies

`Realtime:failover:enabled` (default `false`) runs two copies as active and hot standby, see `realtime-failover.md`. Running more than one copy is supported on PostgreSQL only. Only the copy that holds the `realtime_lease` row serves players: `/-/readiness` is ready only there, and a standby refuses the hub upgrade and `/api` calls with a retryable 503. A planned switch (`GenOnlineService realtime handover` or the shutdown signal) hands the lease to the standby, then sends every connection `ServerSwitching` and closes it with `server.switching`, so the clients reconnect to the standby and rebuild their state from snapshots. Lobbies and the matchmaking queue are not carried over on a planned switch yet (build step 4): lobbies end as on a restart. Both copies still need Redis and the same `Accounts` issuer and certificates.

With failover and the Redis backplane both on, the active copy keeps the live state in Redis (build step 3): each lobby (settings, slots, members and former members, host, state, countdown deadline, match id and start data, mesh check bookkeeping, the pairs that reported connected and each pair's connection order) and the matchmaking queue (tickets, buckets with their widening, matched setups in progress) as a versioned JSON document in the hash `genonline:live:state`, each change also as an entry in the capped stream `genonline:live:stream` (`XADD MAXLEN ~` `Realtime:failover:live_state:stream_max_length`, 10000), and the owner in `genonline:live:owner`, all behind `Realtime:redis_channel_prefix` and a colon when it is set. Lobby chat stays in the database. The documents hold lobby passwords, the host coordinates the latency estimates need and the match start data, so Redis is trusted like the service process. Every stored document carries `v` (1), the owner copy, its epoch and a version per entity that counts from 1 under each owner; formats change additively only, readers ignore unknown fields, and an unreadable document is skipped and counted. The lobby tick writes a lobby when it changed and at least every `refresh_ms` (2000) when its state differs from the last write, the matchmaking tick writes the queue, and every entity is written again every `rewrite_ms` (30000) even when unchanged, so a late write of a fenced copy heals until build step 4 compares epochs on write; a failed write batch makes the next batch start with a reset of the stored state; every write is queued and sent pipelined from its own loop, never on a player's command path and never under the lobby gate, so changes not yet in Redis are lost on a failure. A standby loads every document, then follows the stream every `poll_interval_ms` (100) into an in-memory mirror without running lobby logic; a missing sequence number (the stream was trimmed past it, or a write failed) makes it reload everything. It is in sync once it applied the stream up to the latest entry and read it within one lease period, and it announces itself as standby only while in sync, so a planned switch needs an in-sync standby. A standby that takes the expired lease of a holder it watched (an unplanned failure, not the first take of a cold start) turns the mirror into live lobbies and tickets before it reports ready, but only when the mirror was in sync within two lease periods (otherwise it logs a warning, counts `restore_skipped_stale` and starts empty as a cold start does); a user stored in two lobbies keeps the seat of the more recently written one, and unclaimed restored tickets stay in the written queue during the grace: a countdown continues to its stored deadline, a start still in its mesh check goes back to setup, pending mesh checks and probes are dropped and new ids start at a time based value, and finished lobbies and quick match setups not yet in game stay out (their players get their tickets back with `MatchRequeue setup_failed`). A reconnecting member takes its seat again and gets `LobbyState`, the lobby chat as `ChannelHistory` and `StartSignalling` for each pair that has not reported connected; a queued player gets `MatchmakingStatus matchmaking.queued` and keeps the queue time. Members and tickets not reclaimed within `Core:reconnect_grace_period_ms` are dropped. The new owner starts with a reset of the stored state and writes everything again under its own epoch. Presence needs nothing of this: every player reconnects to the copy that took over, which publishes presence on each connection as a single copy does. Metrics: `genonline.failover.live_state.events` (`loaded`, `stream_gap`, `write_failed`, `queue_lost`, `restore_skipped_stale`), `genonline.failover.mirror.lag`, `genonline.failover.lobbies.restored` and `genonline.failover.lobbies.lost`.

## The Redis backplane

| Key | Meaning |
|---|---|
| `Realtime:backplane` | `none` (default) or `redis`; `redis` needs PostgreSQL |
| `Realtime:redis` | StackExchange.Redis connection string, required with `redis`; the service refuses to start without it or when Redis cannot be reached |
| `Realtime:redis_channel_prefix` | optional prefix of every Redis channel, so environments sharing one Redis never mix |

Redis is the documented option because `Microsoft.AspNetCore.SignalR.StackExchangeRedis` is the only backplane Microsoft maintains. It is used with PostgreSQL only, see above. The community Postgres backplanes are deferred until they mature. One Redis connection per copy carries both the SignalR backplane and the close requests (`realtime.close`, `realtime.close.ack`). Redis pub/sub is not durable: a close request published while Redis is down is lost (the copy that handles the kick or revocation still closes its own connections), and a publish during an outage can stall that call until the StackExchange.Redis timeout (5 s by default) before it fails.

## Why two copies are not supported yet

Two copies need Redis, the same `Accounts:issuer` and the same `Accounts` signing and encryption certificates on both (so a token one copy issued is valid on the other), and sticky sessions in Envoy (hash on a cookie or the source address). The hub is websockets only, so a connection stays on its copy anyway; stickiness matters for reconnects and for the HTTP session calls that go with them.

Even then, this state is still kept per copy in memory and is not shared:

- sessions, reconnect grace and session takeover (`session.superseded` only happens when both connections reach the same copy)
- lobbies, the lobby list and matchmaking, including the connection order of each pair of lobby members (attempt and step, section 6.6 of `realtime-protocol.md`)
- the transports each session sent with `SetPeerTransports`: a lobby reads them from the member sessions on its own copy, and a pair whose member session is on another copy gets `StartSignalling` without the four connection order fields
- channel membership lists (`ChannelMembers`), presence of users on other copies, name change audiences beyond the user and their friends
- the block filter of channel and lobby chat: blocked users are left out by connection id, which works only for connections on the copy that sends
- game session registrations, the chat rate limit and the report rate limit

Moving that state to a shared store is later work. Until then two copies are not supported.

## What the backplane covers

Covered: every event sent to a user, a session type of a user, a channel or a lobby group, `All` notices, and close requests with their notice and close record.

Not covered: anything decided from the per copy state above, for example whether a user is online, who is in a lobby, or which connections the block filter leaves out.
