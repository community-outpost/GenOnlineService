# Release notes

## Unreleased

### REST API moves to `/api/v1` (breaking)

The legacy `env/{environment}/contract/{contract_version}/...` routes and `/cloud/env:prod/VersionCheck` are gone. Every REST route lives under `/api/v1` (player routes use game-server names: `/me`, `/players`, `/stats`, `/leaderboards`, `/config`, `/compatibility`; operator routes use GitLab names) or under `/-/` for probes and operator checks. Every failure answers `{"error": "<code>", "params": {...}}` with the codes of section 7 of `docs/realtime-protocol.md`. Request and response examples are in `docs/api-fixtures/`; `docs/api.md` lists every route.

The deploy is greenfield: no client of the old routes is served. The game client and the launcher must ship builds that call `/api/v1` before this deploys.

| Legacy route | Now |
|---|---|
| `GET Users/Me`, `POST OID` | `GET /api/v1/me` |
| `DELETE User` (a logout) | `DELETE /api/v1/auth/session` |
| `POST EosToken` | `POST /api/v1/me/linked_accounts/eos/sign_in_token` (answers `{token, expires_in}`) |
| `POST ProvideMWToken` | `PUT /api/v1/me/linked_accounts/eos` (body `{proof}`) |
| `POST VersionCheck`, `POST /cloud/env:prod/VersionCheck` | `GET /api/v1/compatibility?client=&version=` |
| `GET VersionManifest` | Removed. The launcher reads executable CRCs from the CDN release manifest |
| `GET ServiceConfig`, `GET MOTD` | `GET /api/v1/config` |
| `GET AnticheatConfig` | removed: the service no longer forces an anti-cheat |
| `GET GlobalStats`, `GET Monitoring/BasicStats` | `GET /api/v1/stats/global` |
| `GET Users/Active`, `GET Monitoring/ActiveUsers` | `GET /api/v1/players?online=true` |
| `GET PlayerStats/{id}`, `POST PlayerStats/Batch` | `GET /api/v1/players/:id/stats`, `GET /api/v1/players/stats?ids=` |
| `PUT PlayerStats`, `MatchUpdate` | `PUT /api/v1/matches/:id/results/me` |
| `GET MatchHistory`, `MatchHistory/{id}`, `MatchHistory/since/{date}` | `GET /api/v1/matches`, `GET /api/v1/matches/:id` |
| `POST ConnectionOutcome` | `POST /api/v1/lobbies/:id/connection_reports` |
| `GET Rooms` | Removed: chat channels arrive in the hub's `Ready` |
| `GET Monitoring/Uptime`, `/healthz` | `GET /-/health` |
| `GET Monitoring/Database` | `GET /-/database` |
| `GET Monitoring/VersionCheck` | `GET /-/self_check/version` |

New operator routes: personal access tokens, service accounts, player roles, audit events, application settings and announcements (see `docs/api.md`). New permissions `audit.read` and `settings.manage` are held by admins.

### Match ids are snowflakes

New matches get 48 bit snowflake ids made by the service: 32 bits of seconds since 2026-01-01 UTC, 5 bits of worker slot, 11 bits of sequence (`docs/schema.md`, Match ids). They are JSON numbers below 2^53 like before, never 0, and larger than every older match id, so ordering by id and `GET /api/v1/matches?after_id=` keep working. Older matches keep their ids. Player and lobby ids do not change.

The migration `MatchIdWorkers` adds the `match_id_workers` table with 32 free worker slots. The migration `MatchIdsWithoutIdentity` then drops the identity / `AUTO_INCREMENT` of `matches.id`. Deploy it only once no copy from before the switch runs against the database (test and live both serve snowflake ids), since such a copy can no longer write a match. On MySQL it briefly drops and re-adds the foreign keys of `match_players` and `match_publications`.

### Deploy day: Epic portal

`POST OID` is removed, so EOS can no longer read the account through the UserInfo endpoint. On the deploy day, in the Epic developer portal, switch the existing OpenID identity provider entry of the product from **UserInfo** to **JWKS** mode:

- JWKS URL: `https://<service host>/.well-known/jwks.json`
- Account id: the `sub` claim

Do this when the service with this change goes live. Until the portal is switched, EOS logins with the new tokens fail.
