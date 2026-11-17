# REST API v1

Every REST route of the service. The realtime hub is `/realtime` (`docs/realtime-protocol.md`); the routes of its section 10 (social, groups, sanctions, messages, reports, `PATCH /me`, match results, playlists, connection reports) are listed there and not repeated here.

## Conventions

- Base path `/api/v1`; operator probes and checks under `/-/`; standards under `/.well-known/`. There is no other route prefix.
- `Authorization: Bearer <access token or gopat- token>`. "Session" below means an OpenIddict access token of a registered client; "game session" means one with the `game` scope and a `POST /api/v1/auth/session` registration; "web session" means a token of an `app` client.
- The sign-in cookie, GitLab style (T16): the routes marked "cookie" below (and the section 10 routes listed under [Staff pages](#staff-pages-admin)) also accept the website sign-in cookie of the environment's own sign-in pages (`go_session`), so the staff pages call `/api/v1` directly. Any `Authorization` header keeps the bearer path, and every other route answers a cookie alone with 401 `unauthorized` as before. A cookie request that changes something (`POST`, `PATCH`, `PUT`, `DELETE`) must carry the antiforgery token of the signed-in user in the `RequestVerificationToken` header (the staff pages read it from the `csrf-token` meta tag of their host page), or gets 400 `csrf`. A cookie request with an `Origin` header other than this host's own origin gets 403 `forbidden`, whatever `AllowedOrigins` (CORS) allows, so CORS never lends the cookie to another site. A staff account with a password and no second factor gets 403 `two_factor_required` on every cookie request. The permission, rank and audit rules are those of a bearer caller; the audit source of a cookie action is `api`. Bearer requests need no header and are unchanged.
- JSON is snake_case, timestamps are ISO 8601 UTC with whole seconds, absent values are `null`.
- Every failure is `{"error": "<code>", "params": {...}}` with the codes of section 7 of `docs/realtime-protocol.md`: 400 `invalid_argument {field}`, 401 `unauthorized`, 403 `forbidden {permission}` (or `{}` when no single permission applies), 404 `not_found {what}`, 409 conflicts, 422 other rule failures, 503 `service.unavailable {what}`. An unknown `/api` or `/-/` path answers 404 `not_found {what: "route"}`, a known path with another method 405 `method_not_allowed`.
- Offset pages: `?page=&per_page=` (default 20, at most 100) with `X-Total`, `X-Page`, `X-Per-Page`, `X-Next-Page` and `Link rel="next"`. Keyset pages (matches, audit events): `after_id` and a `Link rel="next"` when the page is full.
- Example bodies: `docs/api-fixtures/`, one file per request and response type, checked by `ApiFixtureTests`.

## The caller

| Route | Auth | Answer and errors |
|---|---|---|
| `GET /api/v1/me` | session of any client, the website included | `{user_id, display_name, tag, roles, permissions}` |
| `GET /api/v1/me/sessions` | session of any client | `{sessions: [{type: game\|launcher\|chat, client_id, registered_at}]}` |
| `POST /api/v1/me/linked_accounts/:provider/sign_in_token` | game session | `{token, expires_in}`; for `eos` the EOS ID token. `not_found {what: provider}` (404), `session.game_required` (403), `service.unavailable {what: linked_account, provider}` (503) |
| `PUT /api/v1/me/linked_accounts/:provider` | game session | body `{proof}` (for `eos` the Epic Connect ID token), 204; `not_found {what: provider}` (404), `invalid_argument {field: proof}` (400), `session.game_required` (403), `linked_account.invalid_proof` (422), `linked_account.mismatch` (403), `linked_account.in_use` (409), `linked_account.locked` (409), all four with `{provider}`, `service.unavailable {what: linked_account, provider}` (503) |

Account deletion (`DELETE /api/v1/me`) is not built: it needs a data-retention decision. Signing out is `DELETE /api/v1/auth/session`.

## Meta

| Route | Auth | Answer and errors |
|---|---|---|
| `GET /api/v1/compatibility?client=&version=` | anonymous | `{compatible, min_version, service_version, update: {patcher_name, patcher_path, patcher_size}\|null}`; `client` is a registry client id, `version` is `YYYY.MM.PATCH`. The executable check is part of `POST /api/v1/auth/session`; executable CRCs for the launcher are in the CDN release manifest |
| `GET /api/v1/config` | session | `{service_config, motd}`; `service_config` is `data/serviceconfig.json` as written, in the grouped sections the game reads (`connection`, `send`, `input_delay`, `disconnect`, `lobby`, `telemetry`), and a key left out keeps the game's built-in default; the MOTD is the `motd` application setting, else `data/motd.txt`, with `{0}` replaced by the players online; `service.unavailable {what: config}` when `data/serviceconfig.json` is missing or invalid |
| `GET /api/v1/stats/global` | anonymous | `{players, lobbies, factions: [{side, matches, wins}]}` |
| `GET /api/v1/leaderboards/:id` | anonymous | `elo` only: `{id, entries: [{rank, user_id, name, tag, rating, matches}]}`, offset paged |

`tag` is the stored name tag written like the hub's `Member.tag` (`"0042"`), so a client shows `Name#0042`; it is left out for an account without one.

## Players, matches and lobbies

| Route | Auth | Answer and errors |
|---|---|---|
| `GET /api/v1/players?online=true&search=` | session | `{players: [Member]}`, offset paged; `search` is a case-insensitive name prefix of at least 2 characters; one of the two is required (`invalid_argument {field: search}`) |
| `GET /api/v1/players/search?query=` | `moderation.kick` or higher; cookie | staff search over every account, blocked and deactivated ones included: `{players: [{id, name, tag, state, created_at, last_sign_in_at}]}`, offset paged; `query` is a part of a name in any case (2 to 32 characters) or a player id (`invalid_argument {field: query}`) |
| `GET /api/v1/players/:id` | session | public profile `{id, name, tag, badges, created_at, elo_rating, monthly_elo_rating, elo_matches}` |
| `GET /api/v1/players/:id/staff_view` | `moderation.kick` or higher; cookie | the staff player page: `{id, name, tag, state, created_at, last_sign_in_at, roles, online, lobby_id, sessions: [{kind: game\|launcher\|chat, client_id, registered_at}], elo_rating, monthly_elo_rating, elo_matches, sanctions: [{id, type, reason, created_at, expires_at, lifted_at, created_by: Member, active}], can_rename}`; `not_found {what: user}` |
| `GET /api/v1/players/:id/inspection` | `players.inspect`; cookie | `{last_ip, last_country, last_seen_at, identities: [{provider, external_id, linked_at, last_used_at, profile_url}], devices: [{kind, fingerprint, first_seen_at, last_seen_at, last_ip}], addresses: [{ip, country, first_seen_at, last_seen_at, events}], duplicates: [{id, name, tag, state, match: device\|ip, shared, last_seen_at}]}`: sign-ins, devices (fingerprints only), addresses and possible duplicate accounts; writes one `player_inspect` audit row per caller and player an hour, before reading; `not_found {what: user}` |
| `GET /api/v1/players/:id/stats` | session | the player's stats |
| `GET /api/v1/players/stats?ids=1,2,3` | session | `{stats: [...]}` in request order, unknown ids left out, at most 100 ids |
| `GET /api/v1/matches?after_id=&since=&per_page=` | `match_history.read` | finished matches; with `after_id` or `since` (finish time) ascending by id with a next link, otherwise the newest page |
| `GET /api/v1/matches/:id` | `match_history.read`; cookie: also any staff permission (`moderation.kick` or higher) | one match, running or finished; a slot's `name` is the one the player had in the match, `tag` is the account's current tag |
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
| `GET /api/v1/anticheat_reports/:kind?reviewed=&user_id=` | `anticheat.review`; cookie | `kind` is `new_accounts`, `modules`, `probes` or `devices` (`invalid_argument {field: kind}`); `{reports: [{id, kind, player: Member, match_id, module_name?, module_path?, module_size?, reason?, reviewed_at, reviewed_by}]}`, newest first, offset paged; `reviewed` is `false` (default), `true` or `all`; `match_id` is null for a report raised at sign-in |
| `PATCH /api/v1/anticheat_reports/:kind/:id` | `anticheat.review`; cookie | body `{reviewed: true}`, 204; the row stays and gets `reviewed_at` and `reviewed_by`, an audit row `anticheat_review` is written; a row reviewed before answers 204 and changes nothing |
| `POST /api/v1/announcements` | `announcements.send` | body `{message}`, answers `{delivered_to}`; connected clients get `Notice` `server.announcement` |

Sign-out everywhere for another user is `POST /api/v1/users/:id/revoke_sessions` (`users.revoke`).

## Staff pages (`/admin`)

Each environment serves its own staff pages while `Accounts:enabled` (S12): a WebAssembly app under `/admin` (`/admin/reports`, `/admin/players`, `/admin/players/:id`, `/admin/matches`, `/admin/matches/:id`, `/admin/anticheat/:kind`; `/admin` opens the reports). A page without the sign-in cookie redirects to `/users/sign_in?ReturnUrl=`; a signed-in account without `moderation.kick`, `moderation.mute` or `moderation.ban` gets a 403 page; a staff account with a password and no second factor is sent to `/profile/two_factor_auth` first. The page shell loads `/_framework/*` (the WebAssembly files) and `/_content/*`, which `MapStaticAssets` serves.

The host page carries who is signed in and the antiforgery token in meta tags: `go-portal-session` (`{user_id, display_name, tag, roles, permissions, staff}`), `csrf-header` and `csrf-token`. The pages then call `/api/v1` with the cookie (the cookie rule under Conventions): `GET /api/v1/players/search`, `GET /api/v1/players/:id/staff_view`, `GET /api/v1/players/:id/inspection`, `GET /api/v1/matches/:id`, `GET` and `PATCH /api/v1/anticheat_reports`, and of section 10 of `docs/realtime-protocol.md` `GET /api/v1/reports/search`, `PATCH /api/v1/reports/:id`, `PATCH /api/v1/players/:id`, `POST /api/v1/players/:id/sanctions` and `DELETE /api/v1/sanctions/:id`. Those section 10 routes otherwise need a game session; `GET /api/v1/reports/search?status=&search=&sort=` is the staff queue paged in the database (`status` one or more of `open`, `actioned`, `dismissed` comma separated, or `all`, default `open`; `sort` `asc`, the default, or `desc` by creation; offset paged; `{reports}` as `GET /api/v1/reports`).

## Probes and operator checks

| Route | Auth | Answer |
|---|---|---|
| `GET\|HEAD /-/health` | anonymous | `{status, version, started_at, uptime_s}` |
| `GET /-/liveness` | anonymous | `{status: "ok"}` |
| `GET /-/readiness` | anonymous | `{status: "ok"}`, or 503 `service.unavailable {what: database}`, or with failover on `{what: standby}` on a copy that is not serving |
| `GET /-/database` | `ops.database` | `{ok}` |
| `GET /-/self_check/version` | `server.stats` | `{ok, clients: [{client_id, ok}]}` |
| `GET /.well-known/jwks.json` | anonymous | the keys of the `eos` sign-in token |
