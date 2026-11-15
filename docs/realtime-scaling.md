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

`Realtime:failover:enabled` (default `false`) runs two copies as active and hot standby, see `realtime-failover.md`. Running more than one copy is supported on PostgreSQL only. Only the copy that holds the `realtime_lease` row serves players: `/-/readiness` is ready only there, and a standby refuses the hub upgrade and `/api` calls with a retryable 503. A planned switch (`GenOnlineService realtime handover` or the shutdown signal) hands the lease to the standby, then sends every connection `ServerSwitching` and closes it with `server.switching`, so the clients reconnect to the standby and rebuild their state from snapshots. Lobbies, the matchmaking queue and per copy session state are not carried over yet: lobbies end as on a restart. Both copies still need Redis and the same `Accounts` issuer and certificates.

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
