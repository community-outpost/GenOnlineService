# End-to-end probes

Probes run the real service against throwaway databases and a throwaway Keycloak, and check what a client sees: HTTP status codes, response bodies, realtime close records and database rows. They cover what the unit and contract tests in `GenOnlineService.Tests` cannot reach: Keycloak sign-in, group sync, the realtime hub and the CLI against a running service.

| Suite | Probe | Covers | Databases |
|---|---|---|---|
| `identity` | `probe_identity.sh` | Native sign-in (PKCE, loopback), player ids, Keycloak name and tag copy, tokens and refresh rotation, session registration, bans, blocks, sign-out everywhere, group sync | Postgres, MariaDB, MySQL |
| `identity` | `probe_clients.sh` | Client registry: seeding, CLI, third-party consent, accepted builds, disabling, compatibility check, startup sweep | Postgres, MariaDB, MySQL |
| `realtime` | `probe_features.sh` | Client features from roles (CLI and Keycloak group sync), `FeaturesUpdate`, observer seats | Postgres, MariaDB |

## Running

Needs Docker, the .NET 10 SDK, `curl`, bash (Git Bash on Windows) and Python 3 with `requests`.

```bash
export PROBE_ROOT=D:/gb/probes        # scratch for the build, logs and realm copies; keep it outside the repository
bash probes/run_all.sh               # builds, starts the containers, runs every probe on every database
```

Or one probe at a time:

```bash
source probes/common.sh && build
bash probes/identity/rig_up.sh
PROVIDER=mariadb bash probes/identity/probe_identity.sh
```

Each probe prints `PASS` or `FAIL <name>: expected [...] got [...]` per check and exits non-zero on any failure. Service logs land in `$PROBE_ROOT/run/<suite>/service-<provider>.log`.

## Rig

- Containers are named `$PROBE_NAME-pg`, `-maria`, `-mysql`, `-kc` for the identity suite and `$PROBE_NAME-rt-...` for the realtime suite. `PROBE_NAME` defaults to `probe`; set another value to run two rigs on one host.
- Ports (all on 127.0.0.1) can be moved with `IDENTITY_SERVICE_PORT` (19041), `IDENTITY_KC_PORT` (18101), `IDENTITY_PG_PORT` (54401), `IDENTITY_MARIA_PORT` (33101), `IDENTITY_MYSQL_PORT` (33102), `REALTIME_SERVICE_PORT` (19051), `REALTIME_KC_PORT` (18111), `REALTIME_PG_PORT` (54411) and `REALTIME_MARIA_PORT` (33111). The service also takes the next port up for its HTTPS endpoint.
- `stop_service` stops only the service on the rig's own port, never other copies of the service on the host.
- `rig_up.sh` recreates the containers. Docker restarts drop them (`--rm`), so run it again after one.
- The identity realm stands in for the GenOnline Keycloak plugin: user attributes `player_id`, `display_name` and `name_tag`, sent as the `player_id`, `name` and `name_tag` claims, with the stock `full name` mapper removed (player ids and display names in `docs/identity.md`). The realtime realm issues no player ids, so that suite runs the service self-hosted.
- `build` writes `data/anticheatconfig.dat` (level 0) next to the build: `GET /api/v1/config` needs it and deployments provide it.

## Writing probes

- Wait on observable state, never a guessed sleep: poll the REST answer, the database row or the realtime event the check is about, with a deadline. The role cache, client registry and group sync tick every 5 s, so a fixed sleep fails under load.
- Realtime connections opened with `rt.Hub` send the protocol's keep-alive ping on their own; the server closes a client that stays silent for 30 s (`docs/realtime-protocol.md`, Keep-alive).
- A probe that changes Keycloak (groups, attributes) restores it at the end, so the next run starts from the imported realm.
