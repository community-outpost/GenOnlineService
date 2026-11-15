# Realtime failover and graceful handover

Status: build order steps 1 to 3 are built on the hub branch (lease, fencing, readiness, `ServerSwitching` and `server.switching`; reconnect-safe lobby rejoins; live state in Redis and the hot standby, behind `Realtime:failover:enabled`); steps 4 and 5 are design. A planned handover still drops lobbies until step 4. Decided by the user on 2026-11-14: "active standby with graceful failover to start", "faster switchovers", and a brief active-active period to drain gracefully. Builds on `docs/realtime-scaling.md` (Redis backplane, close requests) and `docs/realtime-protocol.md` (the wire).

## Goals

- One active service copy per environment serves players. A hot standby on another node takes over.
- **Planned switch** (deploy, maintenance): every player reconnects once, for about a second, at a quiet moment for them. Lobbies, the matchmaking queue and running matches survive.
- **Unplanned failure** (crash, node lost, partition): takeover in about 3 to 5 seconds. Lobbies and the queue survive; changes from the last moment before the failure may be lost.
- No sticky routing: a reconnect may land on any copy. Cloudflare tunnel replicas pick a connector freely, so affinity cannot be relied on.
- Works for self-hosters without Kubernetes: two processes behind any proxy that health-checks.

Not goals:

- Several copies serving outside a handover (scale-out). The ownership model below is the base for it later.
- Keeping a connection open across a switch. That needs a proxy that holds the connections (the Pushpin model) and is the next step if a one-second reconnect is not good enough.
- SignalR's built-in stateful reconnect. Its buffer lives in one process, so it needs the same copy on reconnect, and it resumes through the negotiate step, which the protocol skips.

## Topology

| | Active | Standby |
|---|---|---|
| Process | running | running, connected to the database and Redis |
| Players | served | none, except during a handover |
| `/-/readiness` | ready | not ready, except as handover target |
| Lease | holds it | waits for it |
| Live state | owns it, writes every change to Redis | follows the changes, same state in memory |

Production: the active copy prefers `k8swk01`, the standby runs on `k8swk02` (one k3s cluster). Both share Postgres (CloudNativePG, synchronous replica on `k8swk02`) and Redis or Valkey (replica on `k8swk02`).

Two copies need Postgres as the shared database. With MySQL or MariaDB the service refuses to start when `Realtime:failover:enabled` is on or `Realtime:backplane` is `redis`. A single copy still runs on MySQL or MariaDB and needs no Redis backplane.

## Lease

- One row per environment in the service database, table `realtime_lease`: `holder` (copy id), `epoch` (increases on every takeover), `expires_at`. Expiry is compared with the database clock, never a copy's clock.
- The active copy renews every 500 ms with an expiry of 2.5 s, only while `holder` and `epoch` are still its own.
- A standby takes an expired lease and increments `epoch`.
- A copy that cannot renew before its own expiry, or that sees a newer epoch, closes all its connections at once and stops owning anything. A paused or cut-off copy therefore never acts next to a new active copy.
- `/-/readiness` reports ready only for the active copy and for the handover target. Envoy health-checks it actively every second (one failure marks it unhealthy, one success healthy), and the readiness probe runs every second.

## Live state and the hot standby

- In memory on the owner: lobbies (state, slots, countdown, mesh check bookkeeping, which peer pairs reported connected) and the matchmaking queue (tickets, widening).
- Every change is written by the owner to Redis: the current state of the entity with its owner, owner epoch and version, plus an entry in a capped change stream for the environment.
- A standby loads the current states on start, then follows the stream and applies each change. It reports itself in sync when it has applied the stream up to the latest entry.
- Writes are pipelined and not awaited on the player's command path. On an unplanned failure the changes not yet in Redis are lost.
- Presence (which connection of which user is on which copy, with session type) lives in Redis with an expiry, renewed by the copy holding the connection.
- Durable data (chat, friends, groups, matches, sanctions) already lives in the database and is unaffected.

## Ownership

- Every lobby and the matchmaking queue has one owner copy and an owner epoch, stored with its state in Redis.
- A copy only changes an entity it owns. The write is a compare on the stored epoch (a Redis script): when the stored epoch is newer, the write fails, and the copy drops that entity and closes the connections of its members.
- Outside a handover, the active copy owns everything.

## Planned handover from A to B

1. B runs as an in-sync hot standby.
2. A starts the handover when it receives the shutdown signal and a standby reports in sync, or on the operator command `GenOnlineService realtime handover`. It prefers a standby running the newer version. Without an in-sync standby, A closes every connection with `server.restarting`, as a restart does.
3. **Overlap begins.** B becomes ready, then A reports not ready, so new connections go to B while existing ones stay on A. The Redis backplane carries chat, presence, friends, direct messages and groups between both copies. New lobbies are created on B.
4. **The queue moves first.** A stops forming matches and passes the queue to B (owner epoch plus one); B starts forming matches. A then moves the players who are in the queue: their tickets stay in the queue while they reconnect.
5. **Lobbies move one by one, when quiet.** Quiet means `setup` with no countdown, mesh check or start in progress, or `in_game` (game traffic is peer to peer). For each lobby A writes its final state with owner B (owner epoch plus one), B takes it from its mirror, and A sends `ServerSwitching` and closes **only that lobby's members** with `server.switching`. The members reconnect to B together and get `LobbyState`, the lobby's `ChannelHistory`, and `StartSignalling` for every pair that has not reported connected. A lobby's members are never split across copies, so no command forwarding is needed.
6. **Everyone else moves in batches:** players in menus, channels and chat, about 5 percent every 200 ms, each with its own `retry_after_ms`, so B builds snapshots gradually instead of all at once.
7. A lobby that stays busy past the handover limit (default 120 s) is moved anyway.
8. When A holds no connections and owns nothing, it hands the lease to B (epoch plus one) and exits.

If A dies during the handover, B takes the lease after expiry and takes ownership of everything, as in an unplanned failover. If B dies, A cancels the handover, takes back what B owned (owner epoch plus one) and reports ready again.

Must be fixed before the handover ships: block filtering in channel and lobby fan-out is exact only on the sending copy (`docs/realtime-scaling.md`). The receiving copy has to filter per recipient.

## Unplanned failover

1. A stops renewing (crash, node lost, partition).
2. After the expiry (2.5 s), B takes the lease (epoch plus one), takes ownership of every lobby and the queue (owner epoch plus one) and reports ready. Envoy notices within a second.
3. Clients reconnect with their normal backoff, because their connection simply broke, and get snapshots and lobby state from B's mirror.
4. If A is alive but cut off, it fails to renew, closes all its connections and stops owning anything.

## Timing targets

| | Planned | Unplanned |
|---|---|---|
| Detection | none, A starts it | lease expiry, 2.5 s |
| State on the new copy | already in memory | already in memory |
| Routing | B ready before A closes anything | Envoy health check, about 1 s |
| Player-visible reconnect | about 1 s, at a quiet moment, hidden by the client | about 1 to 3 s with backoff |
| Total | about 1 to 2 s per player | about 3 to 5 s |

## Compatibility between versions

During the overlap, version N and N+1 run together:

- Redis state, change stream and backplane message formats change additively only. A new required field ships one version after every reader accepts it.
- Database migrations follow expand and contract: the migrations of N+1 must be safe while N serves.
- The wire protocol is not affected by a handover; its own rule (additive within version 1) applies.

## Client behaviour (wire details in `docs/realtime-protocol.md`)

- `ServerSwitching {retry_after_ms}` comes before a `server.switching` close. The client reconnects after that delay without backoff and shows no reconnect screen for up to 3 s.
- Snapshots replace local state; history is deduplicated by message id.
- A repeated `StartSignalling` for a peer restarts that pair.
- `LobbyState.start` lets a client that missed `GameStart` still start the match.

## Infra

- Two pods with required anti-affinity across `k8swk01` and `k8swk02`. A rollout replaces the standby first, then the active, which hands over to the new in-sync standby. `terminationGracePeriodSeconds` covers the handover limit (150 s).
- Envoy, on the GenOnline route: active health check on `/-/readiness` every second, request timeout 0 and stream idle timeout 1 h for the websocket.
- Redis or Valkey with a replica on `k8swk02`.
- Self-hosters: two processes, one shared Postgres database and Redis, a proxy that health-checks `/-/readiness`.

## Build order

1. Lease, readiness, `ServerSwitching` and `server.switching`. A planned switch then means everyone reconnects to the standby with snapshots.
2. The reconnect-safe protocol pieces: `LobbyState.start`, resent `StartSignalling`, lobby chat history on rejoin.
3. Live state in Redis and the hot standby: an unplanned failover keeps lobbies and the queue.
4. Ownership epochs and the planned handover with queue move, quiet-lobby moves and batches.
5. Block filtering on the receiving copy, then the infra policies.

## Open

- Counters that restart per process (mesh check ids, probe ids) must not let a handed-over lobby accept a stale answer: start them at a random or time-based value, or reset the lobby's pending checks on handover.
- The lobby state handed over must include the former member set, which connection reports use to check their target, and each pair's connection order state (step, attempt, reported failures).
- Lobby ids come from a database block allocator (blocks of 100 per copy). The allocator relies on synchronous database replication: an asynchronous replica can lose the last reserved block on failover and hand out ids again. Production runs a synchronous CloudNativePG replica; keep it that way.
- Redis holds lobby passwords, host coordinates and match start data, so it must be trusted like the service: same network boundary, authentication and no shared tenants.
- A copy that loses the lease and takes it back within the reconnect grace can keep stale lobbies. Step 4's ownership epochs must close this.
- Handover limit and batch size: start with 120 s and 5 percent every 200 ms, then tune from metrics.
- Metrics to add: handover duration, connections moved per batch, lobbies moved, lobbies lost on an unplanned failover, reconnect time seen by clients.

## Implementation notes (step 1)

- `realtime_lease` holds one row (`id` 1) with `holder`, `epoch`, `expires_at`, plus `standby` and `standby_expires_at` (the standby renews its own entry each interval, so the holder knows whom to hand over to) and `handover_requested_at` (set by `GenOnlineService realtime handover`). Every change is one conditional `UPDATE` against `now()` or `UTC_TIMESTAMP(6)`.
- The holder counts its own deadline from before it sent the renewal, so it stops serving no later than the database expiry. Readiness compares with that deadline on every request, so a stalled lease loop stops serving too.
- A standby refuses the hub upgrade and `/api` calls with `503 service.unavailable {what: standby}` and `Retry-After: 1`; `/-/` routes and sign-in stay served.
- Without step 3 there is no in-sync check: a live standby entry is enough to hand over. Lobbies and the queue end on the old copy as on a restart, members reconnect to the new copy and get snapshots.
- If the target dies during the handover, the old copy does not take the lease back yet (step 4).
