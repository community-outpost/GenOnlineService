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
| `GET /api/v1/staff/overview` | `moderation.kick` or higher, the anti-cheat parts `anticheat.review`; cookie | the staff dashboard: `{players_online, lobbies_open, matches_today, open_reports, unreviewed_anticheat, anticheat: [{kind, unreviewed}], recent_reports, recent_sanctions}`. Players online and lobbies are this process's live counts; matches today are those finished since 00:00 UTC; open reports count the queue; `recent_reports` are the newest 5 reports in any state (the fields of `GET /api/v1/reports`), `recent_sanctions` the newest 5 sanctions `{id, type, player: Member, reason, created_at, expires_at, lifted_at, created_by: Member, active}`; `unreviewed_anticheat` and `anticheat` are null without `anticheat.review` |
| `GET /api/v1/players/search?query=&filter=&signed_in_within=&order_by=&sort=` | `moderation.kick` or higher; cookie | the staff list of every account, blocked and deactivated ones included: `{players: [{id, name, tag, state, created_at, last_sign_in_at, online, banned, muted, roles, flagged, matches, rating}]}`, offset paged; past 10,000 matching accounts the answer leaves out `X-Total` and still sets `X-Next-Page`. Every argument is optional, and one given twice takes its first value: without `query` it lists every account; `query` is a part of a name in any case (2 to 32 characters), `Name#tag` (the name part narrowed to that tag) or a player id (`invalid_argument {field: query}`); `filter` is one or more of `online`, `banned`, `muted`, `staff`, `flagged`, comma separated, every one must hold (`{field: filter}`); `signed_in_within` is `24h`, `7d` or `30d` (`{field: signed_in_within}`); `order_by` is `last_sign_in_at`, `created_at`, `name` or `matches` (`{field: order_by}`) and `sort` `asc` or `desc` (`{field: sort}`; with a `query`, `sort` needs `order_by`, as a search keeps its own order otherwise), by default name A to Z and the others newest or most first, ties by id. Without `order_by` a query keeps its order (the exact id first, then by name) and no query lists the most recent sign-in first; accounts that never signed in come last either way. `online` is a connection to this process; `banned` and `muted` an active sanction; `roles` the known roles; `flagged` an open report, or with `anticheat.review` also an unreviewed anti-cheat row; `matches` the recorded matches; `rating` the all-time rating once the player has a rated match, else null |
| `GET /api/v1/players/:id` | session | public profile `{id, name, tag, badges, created_at, elo_rating, monthly_elo_rating, elo_matches}` |
| `GET /api/v1/players/:id/staff_view` | `moderation.kick` or higher; cookie | the staff player page: `{id, name, tag, state, created_at, last_sign_in_at, roles, online, lobby_id, sessions: [{kind: game\|launcher\|chat, client_id, registered_at}], elo_rating, monthly_elo_rating, elo_matches, sanctions: [{id, type, reason, created_at, expires_at, lifted_at, created_by: Member, active}], role_grants: [{role, source}], assignable_roles, can_rename}`; `role_grants` lists every role row with its source (`local` or `upstream:<provider>`), `assignable_roles` the roles the caller may grant or revoke here (`roles.manage` and the rank rule of `PUT /api/v1/players/:id/roles/:role`, empty otherwise); `not_found {what: user}` |
| `GET /api/v1/players/:id/inspection` | `players.inspect`; cookie | `{last_ip, last_country, last_seen_at, identities: [{provider, external_id, linked_at, last_used_at, profile_url}], devices: [{kind, fingerprint, first_seen_at, last_seen_at, last_ip}], addresses: [{ip, country, first_seen_at, last_seen_at, events}], duplicates: [{id, name, tag, state, match: device\|ip, shared, last_seen_at}]}`: sign-ins, devices (fingerprints only), addresses and possible duplicate accounts; writes one `player_inspect` audit row per caller and player an hour, before reading; `not_found {what: user}` |
| `GET /api/v1/players/:id/stats` | session | the player's stats |
| `GET /api/v1/players/stats?ids=1,2,3` | session | `{stats: [...]}` in request order, unknown ids left out, at most 100 ids |
| `GET /api/v1/matches?after_id=&since=&per_page=` | `match_history.read` | finished matches; with `after_id` or `since` (finish time) ascending by id with a next link, otherwise the newest page |
| `GET /api/v1/matches/:id` | `match_history.read`; cookie: also any staff permission (`moderation.kick` or higher) | one match, running or finished; a slot's `name` is the one the player had in the match, `tag` is the account's current tag |
| `GET /api/v1/matches/search?type=&map=&player=&from=&to=&flagged=` | `moderation.kick` or higher, the `anticheat` flag `anticheat.review`; cookie | the staff list of matches, running and finished, newest (highest id) first: `{matches: [{id, name, type, map: {name, path, official}, started_at, finished_at, duration_s, players: [{user_id, name, tag, team, side, won, desynced}], flags}]}`, offset paged; past 10,000 matching rows the answer leaves out `X-Total` and still sets `X-Next-Page`. Every argument is optional, and one given twice takes its first value: `type` is `quick_match` or `custom` (`{field: type}`); `map` a part of the map name in any case (at most 128 characters); `player` a player id, `Name#tag` or a whole name with any tag (a name of nobody lists nothing); `from` and `to` a day (`yyyy-MM-dd`, UTC, `to` includes the day) or an ISO 8601 time, on the start time (`{field: from}`, `{field: to}`; `to=9999-12-31` has no end, a time outside the calendar once its offset is applied is refused); `flagged=true` keeps flagged matches. `type` is null for a match kept without one; `finished_at` and `duration_s` are null while it runs; `players` are the slots with an account, `name` as in the match and `tag` the account's current one; `flags` are `desync` (a player desynced), `no_result` (finished without a winner) and, with `anticheat.review`, `anticheat` (an unreviewed anti-cheat row) |
| `GET /api/v1/lobbies` | player session or `lobbies.read` | `{lobbies: [LobbySummary]}`; players get what `WatchLobbyList` shows them |

## Tokens and service accounts

The management routes need a web session (never a game session or a `gopat-` token): the bearer session of a registered `app` client, or on the routes marked "cookie" the staff pages' sign-in cookie. See `docs/api-tokens.md` for the rules.

| Route | Auth | Answer |
|---|---|---|
| `GET /api/v1/personal_access_tokens`, `POST` | web session with a staff permission | the caller's tokens with their `allowed_ips` (empty: any address); create answers 201 with the `token` secret once |
| `GET /api/v1/personal_access_tokens/self` | `gopat-` token | the token making the request |
| `POST /api/v1/personal_access_tokens/:id/rotate?overlap_minutes=&expires_at=` | web session with a staff permission | 201 with the new secret |
| `DELETE /api/v1/personal_access_tokens/:id` | web session | 204 |
| `GET /api/v1/service_accounts`, `POST` | web session, `tokens.manage`; cookie | list, create (201); each account carries `active_tokens` (neither revoked nor expired, a rotated token inside its overlap included) and `last_token_used_at` |
| `GET /api/v1/service_accounts/:id` | web session, `tokens.manage`; cookie | one account with its permissions; `not_found {what: service_account}` |
| `PATCH /api/v1/service_accounts/:id` | web session, `tokens.manage`; cookie | `{description?, grants?: {add?, remove?}, disabled?}`; privileged grants stay CLI only; a changed description writes `sa_update {name, old_description, description}`, an empty one clears it |
| `GET /api/v1/service_accounts/:id/personal_access_tokens`, `POST` | web session, `tokens.manage`; cookie | the account's tokens; mint (201) |
| `POST /api/v1/service_accounts/:id/personal_access_tokens/:token_id/rotate?overlap_minutes=&expires_at=` | web session, `tokens.manage`; cookie | 201 with the new secret, as the personal token rotate; a token of another owner `not_found {what: token}` |
| `DELETE /api/v1/service_accounts/:id/personal_access_tokens/:token_id` | web session, `tokens.manage`; cookie | 204, `token_revoke` in the audit log; a token of another owner `not_found {what: token}` |

## Administration

| Route | Permission | Answer |
|---|---|---|
| `GET /api/v1/players/:id/roles`, `PUT` `{roles}`, `DELETE` | `roles.manage` | `{roles}`; PUT and DELETE change the locally granted roles, roles from the sign-in provider stay; the roles that change follow the rank rule below (a request that changes nothing answers as before) |
| `PUT /api/v1/players/:id/roles/:role`, `DELETE` | `roles.manage`; cookie | one local role: PUT grants it (200 `{roles}`, also when held already, which changes nothing), DELETE revokes it (204); an unknown role `invalid_argument {field: role}`; a role held only through a sign-in provider 409 `roles.upstream {role, source}`, it follows that provider's sign-ins; a role not held `not_found {what: role}`. Each change writes a `role_grant` or `role_revoke` audit row `{role, source: local}` and reloads the role cache, so it applies at once. Rank rule (`owner` 3, `admin` 2, `moderator` 1, any other role 0, `streamer` included): the caller outranks the player (`moderation.target_outranks`, 403; never themself, `invalid_argument {field: id}`) and the role ranks strictly below the caller's highest role (403 `roles.rank {role}`); a service account token acts with admin rank, as for staff renames. So an owner manages `admin`, `moderator` and `streamer` of everyone below owner; an admin manages `moderator` and `streamer` of players and moderators. `roles.rank` answers an admin or a service account granting or revoking `admin` or `owner`, and anyone granting or revoking `owner`: `owner` is granted with the `roles` CLI only, and never by a sign-in provider |
| `GET /api/v1/audit_events?actor_id=&target_id=&target_kind=&action=&after_id=&per_page=` | `audit.read`; cookie | `{audit_events}`, newest first, keyset paged; `target_kind` (such as `user`, `token`, `service_account`) narrows `target_id`, which alone matches every kind |
| `GET /api/v1/application/settings`, `PUT` | `settings.manage` | `{motd}`; PUT is partial, `null` clears a key |
| `GET /api/v1/anticheat_reports/:kind?reviewed=&user_id=` | `anticheat.review`; cookie | `kind` is `new_accounts`, `modules`, `probes`, `devices` or `quickmatch_rules` (`invalid_argument {field: kind}`); `{reports: [{id, kind, player: Member, match_id, module_name?, module_path?, module_size?, reason?, lobby_id?, playlist_id?, method?, sides?, reported_at?, reviewed_at, reviewed_by}]}`, newest first, offset paged; `reviewed` is `false` (default), `true` or `all`; `match_id` is null for a report raised at sign-in and for a `quickmatch_rules` report made before its match started. `quickmatch_rules` lists quick match requests a genuine client never sends (today `UpdateMySlot` and `UpdateSlots` asking for a side other than -1 in a playlist without army selection, refused with `lobby.army_selection_disabled`), kept once per player and lobby with the `lobby_id`, `playlist_id`, hub `method`, requested `sides` (array of ints) and `reported_at` |
| `PATCH /api/v1/anticheat_reports/:kind/:id` | `anticheat.review`; cookie | body `{reviewed: true}`, 204; the row stays and gets `reviewed_at` and `reviewed_by`, an audit row `anticheat_review` is written; a row reviewed before answers 204 and changes nothing |
| `POST /api/v1/announcements` | `announcements.send` | body `{message}`, answers `{delivered_to}`; connected clients get `Notice` `server.announcement` |

Sign-out everywhere for another user is `POST /api/v1/users/:id/revoke_sessions` (`users.revoke`).

## Staff pages (`/admin`)

Each environment serves its own staff pages while `Accounts:enabled` (S12): a WebAssembly app under `/admin` (`/admin/reports`, `/admin/players`, `/admin/players/:id`, `/admin/matches`, `/admin/matches/:id`, `/admin/anticheat/:kind`, `/admin/audit`, `/admin/partners`, `/admin/partners/:id`; `/admin` is the dashboard). A page without the sign-in cookie redirects to `/users/sign_in?ReturnUrl=`; a signed-in account without `moderation.kick`, `moderation.mute` or `moderation.ban` gets a 403 page; a staff account with a password and no second factor is sent to `/profile/two_factor_auth` first. The page shell loads `/_framework/*` (the WebAssembly files) and `/_content/*`, which `MapStaticAssets` serves.

The host page carries who is signed in and the antiforgery token in meta tags: `go-portal-session` (`{user_id, display_name, tag, roles, permissions, staff}`), `csrf-header` and `csrf-token`. The pages then call `/api/v1` with the cookie (the cookie rule under Conventions): `GET /api/v1/staff/overview`, `GET /api/v1/players/search`, `GET /api/v1/players/:id/staff_view`, `GET /api/v1/players/:id/inspection`, `GET /api/v1/matches/:id`, `GET /api/v1/matches/search`, `GET` and `PATCH /api/v1/anticheat_reports`, `PUT` and `DELETE /api/v1/players/:id/roles/:role`, `GET /api/v1/audit_events`, the service account routes under [Tokens and service accounts](#tokens-and-service-accounts), and of section 10 of `docs/realtime-protocol.md` `GET /api/v1/reports/search`, `PATCH /api/v1/reports/:id`, `PATCH /api/v1/players/:id`, `POST /api/v1/players/:id/sanctions` and `DELETE /api/v1/sanctions/:id`. Those section 10 routes otherwise need a game session; `GET /api/v1/reports/search?status=&search=&sort=` is the staff queue paged in the database (`status` one or more of `open`, `actioned`, `dismissed` comma separated, or `all`, default `open`; `sort` `asc`, the default, or `desc` by creation; offset paged; `{reports}` as `GET /api/v1/reports`).

## Probes and operator checks

| Route | Auth | Answer |
|---|---|---|
| `GET\|HEAD /-/health` | anonymous | `{status, version, started_at, uptime_s}` |
| `GET /-/liveness` | anonymous | `{status: "ok"}` |
| `GET /-/readiness` | anonymous | `{status: "ok"}`, or 503 `service.unavailable {what: database}`, or with failover on `{what: standby}` on a copy that is not serving |
| `GET /-/database` | `ops.database` | `{ok}` |
| `GET /-/self_check/version` | `server.stats` | `{ok, clients: [{client_id, ok}]}` |
| `GET /.well-known/jwks.json` | anonymous | the keys of the `eos` sign-in token |
