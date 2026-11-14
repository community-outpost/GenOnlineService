# REST API v1

Every REST route of the service. The realtime hub is `/realtime` (`docs/realtime-protocol.md`); the routes of its section 10 (social, groups, sanctions, messages, reports, `PATCH /me`, match results, playlists, connection reports) are listed there and not repeated here.

## Conventions

- Base path `/api/v1`; operator probes and checks under `/-/`; standards under `/.well-known/`. There is no other route prefix.
- `Authorization: Bearer <access token or gopat- token>`. "Session" below means an OpenIddict access token of a registered client; "game session" means one with the `game` scope and a `POST /api/v1/auth/session` registration; "web session" means a token of an `app` client.
- JSON is snake_case, timestamps are ISO 8601 UTC with whole seconds, absent values are `null`.
- Every failure is `{"error": "<code>", "params": {...}}` with the codes of section 7 of `docs/realtime-protocol.md`: 400 `invalid_argument {field}`, 401 `unauthorized`, 403 `forbidden {permission}` (or `{}` when no single permission applies), 404 `not_found {what}`, 409 conflicts, 422 other rule failures, 503 `service.unavailable {what}`. An unknown `/api` or `/-/` path answers 404 `not_found {what: "route"}`, a known path with another method 405 `method_not_allowed`.
- Offset pages: `?page=&per_page=` (default 20, at most 100) with `X-Total`, `X-Page`, `X-Per-Page`, `X-Next-Page` and `Link rel="next"`. Keyset pages (matches, audit events): `after_id` and a `Link rel="next"` when the page is full.
- Example bodies: `docs/api-fixtures/`, one file per request and response type, checked by `ApiFixtureTests`.

## The caller

| Route | Auth | Answer and errors |
|---|---|---|
| `GET /api/v1/me` | session of any client, the website included | `{user_id, display_name, tag, roles, permissions}` |
| `GET /api/v1/me/sessions` | session of any client | `{sessions: [{type: game\|launcher\|chat, client_id, registered_at}]}` |
| `POST /api/v1/me/eos_token` | game session | `{eos_token, expires_in}`; `session.game_required` (403), `service.unavailable {what: eos_token}` |
| `PUT /api/v1/me/linked_accounts/eos` | game session | body `{id_token}` (the Epic ID token), 204; `session.game_required`, `eos.invalid_token` (422), `eos.account_mismatch` (403), `eos.account_in_use` (409), `eos.account_locked` (409), `service.unavailable {what: eos}` |

Account deletion (`DELETE /api/v1/me`) is not built: it needs a data-retention decision. Signing out is `DELETE /api/v1/auth/session`.

## Meta

| Route | Auth | Answer and errors |
|---|---|---|
| `GET /api/v1/compatibility?client=&version=` | anonymous | `{compatible, min_version, service_version, update: {patcher_name, patcher_path, patcher_size}\|null}`; `client` is a registry client id, `version` is `YYYY.MM.PATCH`. The executable check is part of `POST /api/v1/auth/session`; executable CRCs for the launcher are in the CDN release manifest |
| `GET /api/v1/config` | session | `{service_config, anticheat_level, motd}`; the MOTD is the `motd` application setting, else `data/motd.txt`, with `{0}` replaced by the players online; `service.unavailable {what: config}` when a data file is missing |
| `GET /api/v1/stats/global` | anonymous | `{players, lobbies, factions: [{side, matches, wins}]}` |
| `GET /api/v1/leaderboards/:id` | anonymous | `elo` only: `{id, entries: [{rank, user_id, name, tag, rating, matches}]}`, offset paged |

`tag` is the stored name tag written like the hub's `Member.tag` (`"0042"`), so a client shows `Name#0042`; it is left out for an account without one.

## Players, matches and lobbies

| Route | Auth | Answer and errors |
|---|---|---|
| `GET /api/v1/players?online=true&search=` | session | `{players: [Member]}`, offset paged; `search` is a case-insensitive name prefix of at least 2 characters; one of the two is required (`invalid_argument {field: search}`) |
| `GET /api/v1/players/:id` | session | public profile `{id, name, tag, badges, created_at, elo_rating, monthly_elo_rating, elo_matches}` |
| `GET /api/v1/players/:id/stats` | session | the player's stats |
| `GET /api/v1/players/stats?ids=1,2,3` | session | `{stats: [...]}` in request order, unknown ids left out, at most 100 ids |
| `GET /api/v1/matches?after_id=&since=&per_page=` | `match_history.read` | finished matches; with `after_id` or `since` (finish time) ascending by id with a next link, otherwise the newest page |
| `GET /api/v1/matches/:id` | `match_history.read` | one match, running or finished; a slot's `name` is the one the player had in the match, `tag` is the account's current tag |
| `GET /api/v1/lobbies` | player session or `lobbies.read` | `{lobbies: [LobbySummary]}`; players get what `WatchLobbyList` shows them |

## Tokens and service accounts

The management routes need a web session (never a game session or a `gopat-` token). See `docs/api-tokens.md` for the rules.

| Route | Auth | Answer |
|---|---|---|
| `GET /api/v1/personal_access_tokens`, `POST` | web session with a staff permission | the caller's tokens; create answers 201 with the `token` secret once |
| `GET /api/v1/personal_access_tokens/self` | `gopat-` token | the token making the request |
| `POST /api/v1/personal_access_tokens/:id/rotate?overlap_minutes=&expires_at=` | web session with a staff permission | 201 with the new secret |
| `DELETE /api/v1/personal_access_tokens/:id` | web session | 204 |
| `GET /api/v1/service_accounts`, `POST` | web session, `tokens.manage` | list, create (201) |
| `PATCH /api/v1/service_accounts/:id` | web session, `tokens.manage` | `{grants?: {add?, remove?}, disabled?}`; privileged grants stay CLI only |
| `GET /api/v1/service_accounts/:id/personal_access_tokens`, `POST` | web session, `tokens.manage` | the account's tokens; mint (201) |

## Administration

| Route | Permission | Answer |
|---|---|---|
| `GET /api/v1/players/:id/roles`, `PUT` `{roles}`, `DELETE` | `roles.manage` | `{roles}`; PUT and DELETE change the locally granted roles, roles from the sign-in provider stay |
| `GET /api/v1/audit_events?actor_id=&target_id=&action=&after_id=&per_page=` | `audit.read` | `{audit_events}`, newest first, keyset paged |
| `GET /api/v1/application/settings`, `PUT` | `settings.manage` | `{motd}`; PUT is partial, `null` clears a key |
| `POST /api/v1/announcements` | `announcements.send` | body `{message}`, answers `{delivered_to}`; connected clients get `Notice` `server.announcement` |

Sign-out everywhere for another user is `POST /api/v1/users/:id/revoke_sessions` (`users.revoke`).

## Probes and operator checks

| Route | Auth | Answer |
|---|---|---|
| `GET\|HEAD /-/health` | anonymous | `{status, version, started_at, uptime_s}` |
| `GET /-/liveness` | anonymous | `{status: "ok"}` |
| `GET /-/readiness` | anonymous | `{status: "ok"}`, or 503 `service.unavailable {what: database}` |
| `GET /-/database` | `ops.database` | `{ok}` |
| `GET /-/self_check/version` | `server.stats` | `{ok, clients: [{client_id, ok}]}` |
| `GET /.well-known/jwks.json` | anonymous | the EOS token keys |
