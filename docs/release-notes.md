# Release notes

## Unreleased

### REST API moves to `/api/v1` (breaking)

The legacy `env/{environment}/contract/{contract_version}/...` routes and `/cloud/env:prod/VersionCheck` are gone. Every REST route lives under `/api/v1` (player routes use game-server names: `/me`, `/players`, `/stats`, `/leaderboards`, `/config`, `/compatibility`; operator routes use GitLab names) or under `/-/` for probes and operator checks. Every failure answers `{"error": "<code>", "params": {...}}` with the codes of section 7 of `docs/realtime-protocol.md`. Request and response examples are in `docs/api-fixtures/`; `docs/api.md` lists every route.

The deploy is greenfield: no client of the old routes is served. The game client and the launcher must ship builds that call `/api/v1` before this deploys.

| Legacy route | Now |
|---|---|
| `GET Users/Me`, `POST OID` | `GET /api/v1/me` |
| `DELETE User` (a logout) | `DELETE /api/v1/auth/session` |
| `POST EosToken` | `POST /api/v1/me/eos_token` |
| `POST ProvideMWToken` | `PUT /api/v1/me/linked_accounts/eos` (body `{id_token}`) |
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

### Deploy day: Epic portal

`POST OID` is removed, so EOS can no longer read the account through the UserInfo endpoint. On the deploy day, in the Epic developer portal, switch the existing OpenID identity provider entry of the product from **UserInfo** to **JWKS** mode:

- JWKS URL: `https://<service host>/.well-known/jwks.json`
- Account id: the `sub` claim

Do this when the service with this change goes live. Until the portal is switched, EOS logins with the new tokens fail.
