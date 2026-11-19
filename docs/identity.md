# Identity: every environment is its own identity provider (OpenIddict)

Status: **being replaced.** Keycloak is gone (the identity hub, decided 2026-11-15): the live environment's service is the identity provider, and the sections below on Keycloak, player ids, trusted claims, group sync and the plugin's change log no longer describe the service. A full rewrite follows. Earlier status: draft, topology decided by the user (separate environments tied together by Keycloak). Proposed to replace `docs/identity-keycloak.md` (branch `arctic/feat/keycloak-idp`). Nothing is built. Target location once agreed: `docs/identity.md`.

Each GenOnline environment owns its accounts and issues every token itself, like a GitLab instance. Upstream sign-in providers (Keycloak, Discord, Steam, Google, any OIDC) are linked to a local account, like GitLab's OmniAuth providers. The official deployment ties its environments together through one Keycloak; a self-hoster runs the same thing with or without one.

## 0. Decisions and assumptions

- **Greenfield deploy.** No old clients, no legacy login flow, no shared keys.
- **Separate environments.** Live, test and dev are complete, self-contained instances: own database, own bans, friends and blocks. Nothing is shared between them except the upstream login and, in the official deployment, the player id it carries (section 3.2).
- **The official deployment** uses Keycloak as the only upstream provider in every environment (Discord, Steam and Google are brokered inside Keycloak). Local passwords are off there.
- **Self-hosters** run one environment with local passwords and/or Discord and Steam directly, no Keycloak needed.
- **Later add-on, not now:** a one-way "follow live" sync (section 9).
- **Scale.** About 2,500 concurrent players at peak, about 4 sign-ins per second in a login burst.

## 1. Topology

```
                     Keycloak (official deployment only)
                 brokers Discord, Steam, Google; groups for staff
                   |                 |                 |
         +---------+------+  +-------+--------+  +-----+----------+
         | live           |  | test           |  | dev            |
         |  accounts      |  |  accounts      |  |  accounts      |
         |  application   |  |  application   |  |  application   |
         |  one database  |  |  one database  |  |  one database  |
         +----------------+  +----------------+  +----------------+

 self-hoster:  one box like the ones above, upstream = local passwords / Discord / Steam
```

- **`accounts`** (ASP.NET Core Identity + OpenIddict) and **`application`** (game API, realtime) are profiles of one binary. They run in one process by default; the split is configuration.
- One database per environment holds account tables and game tables.
- A compromise of dev or test cannot reach live: nothing connects them except Keycloak, which only signs people in.

## 2. Why this shape

| Need | How it is met |
|---|---|
| Small operators | One binary, one database, no external services |
| Same login everywhere (official) | Keycloak as upstream in every environment |
| Staff everywhere (official) | Keycloak groups mapped to roles at sign-in (section 5) |
| Isolation | Structural: environments share no data |
| Third-party "Sign in with GenOnline" | OAuth applications per environment (normally only live) |

Trade-offs accepted: friends, blocks and bans are per environment; the player id is the same everywhere in the official deployment (section 3.2). Dev and test data is throwaway; a player who matters belongs to live.

## 3. Accounts data (per environment)

GitLab shape on top of ASP.NET Core Identity. Full table definitions in `schema-v1-design.md`.

- `users`: account, display name with a 4-digit tag (`name#tag` unique, section 5a), email (optional), password hash (null without local passwords), `state` (active | blocked | deactivated), 2FA fields, sign-in timestamps.
- `identities`: linked sign-in methods `(provider, extern_uid)`; `provider` is `keycloak`, `discord`, `steam`, `google`, `gamereplays` or a configured OIDC name.
- `user_roles`: roles (`moderator`, `admin`, and the non-staff `streamer`) with a `source` (`local` or `upstream:<provider>`).
- `sanctions`: kicks, mutes and bans with expiry and lifting.
- OpenIddict tables, `api_tokens`, `service_accounts`, `audit_log`, `application_settings`.

### 3.1 Bans versus blocks

A ban removes someone from the game, not from their account, so a banned player can still sign in to the website to read the reason and appeal.

| | Sign-in (website, account pages) | Game API and realtime | Authorizations revoked |
|---|---|---|---|
| **Ban** (`sanctions`, type `ban`) | allowed | refused: 403 `moderation.banned`, realtime closed | game clients' only |
| **Mute** (`sanctions`, type `mute`) | allowed | allowed, chat refused | none |
| **Blocked** (`users.state = blocked`) | refused | refused | all |

`blocked` is only for abuse of the account itself (compromise, spam sign-ups, fraud). Several active bans may exist; the longest lasting decides what the player sees.

### 3.2 Player ids (decided 2026-11-14)

User: "i just want auto increment". One player id per person, the same in every environment of the official deployment.

- **Keycloak hands it out.** Every Keycloak account gets a user profile attribute `player_id` from one increasing counter. Imported players keep their old id; the counter starts above the highest imported id. Keycloak has no counter of its own, so the GenOnline Keycloak plugin (the same jar as the name tags, section 5a) assigns it when the account is created, inside the same transaction, from a sequence in its own table. It never changes.
- **Admin only.** The user profile marks `player_id` as viewable and editable by admins only; users can never set or change it. The import is the only other writer.
- **The claim.** `player_id` travels as a claim of the same name in the ID token Keycloak issues to each environment (user attribute mapper).
- **Environments use it as the user id.** On a Keycloak sign-in the environment finds the user with id `player_id`, or creates it with exactly that id. There is no separate link step and no matching by email: the id is the person. Player 42 is the same person in live, test and dev.
- **No local ids next to Keycloak.** An environment whose only provider is Keycloak never lets the database pick a user id, so it cannot take a number Keycloak gives out later. It refuses every other sign-in method (`only_provider`) and rejects a Keycloak sign-in without a numeric `player_id`.
- **Usernames and display names stay free.** The Keycloak username is only a login handle that environments ignore; players choose it. The display name and tag follow section 5a.
- **Self-hosters without Keycloak:** the database assigns user ids as usual; `player_id` does not exist there.
- **In the service:** the provider entry sets `player_ids: true` (claim name `player_id_claim`, default `player_id`). Startup refuses `player_ids` unless the provider is `trusted` and `only_provider` and no other provider is enabled. A sign-in without a positive integer claim is refused (`no_player_id`), and so is an identity whose user id differs from its claim, or a claim another identity of the provider already holds (`player_id_mismatch`). Without `player_ids` there is no claim-based linking at all.
- **Switching an existing environment to player ids** is only safe when its users came from the import (ids equal to the Keycloak `player_id`) or the users table is empty; a user the database created earlier would be taken for the Keycloak account with the same number.

## 4. Flows

### 4.1 Game client login (native app, RFC 8252)

```
game client           system browser                 accounts (this environment)      application
   | open /oauth/authorize?client_id=genonline_zerohour&response_type=code
   |   &code_challenge=...&code_challenge_method=S256
   |   &redirect_uri=http://127.0.0.1:<port>/callback&scope=openid game
   |------------------------>|-------------------------------->|
   |                         |  sign in: upstream (Keycloak / Discord / Steam) or local password + 2FA
   |<---- loopback redirect ?code -----------------------------|
   | POST /oauth/token (code, code_verifier) ------------------>|
   |<---- access_token (JWT, 15 min), refresh_token (rotating), id_token
   | POST /api/v1/auth/session {device ids, crcs} with Bearer access_token ---------------->|
   |<---- session registered: display name, ws url, bans checked, device recorded ---------|
```

- The game API accepts this environment's access tokens directly; `POST /api/v1/auth/session` registers the game session (device ids, CRCs, ban check, session event), it does not mint tokens. The registration is stored (`game_sessions`), so every service copy and a restarted copy accept that client's realtime connection.
- Refresh tokens rotate on every use with reuse detection (OpenIddict rolling reference tokens). This replaces the service-side rotation from `cf5f0549`.
- Logout: `POST /oauth/revoke` plus `DELETE /api/v1/auth/session` (drops realtime).
- The client reads the environment's issuer from its configured base URL, so one client build works against any environment or self-hosted server.

### 4.2 Upstream sign-in (GitLab OmniAuth behaviour)

1. "Sign in with X" redirects to the provider; the callback returns `(provider, extern_uid, email?, name?, groups?)`.
2. Known identity: sign in that user.
3. Signed-in user on the account page: link X.
4. Unknown identity: create an account (if sign-up is allowed for that provider), or sign in to an existing account and link it. Never auto-link by email unless the operator enables it for that provider and the provider asserts a verified email.
5. Per provider settings: enabled, allow sign-up, auto-link by verified email, **only provider** (the official deployment sets Keycloak as the only one), **trusted claims** (section 5).

Steam is OpenID 2.0; its handler is built into accounts.

GameReplays offers OAuth 2.0 sign-in (no ID tokens): provider kind `gamereplays`, `base_url` `https://www.gamereplays.org/community/modules/oauth` (the folder of `authorize.php`, `token.php` and `resource.php`), scope `user_profile`, the client secret sent in the token request form. `resource.php` answers the member (or wraps it in `user`): `extern_uid` = member `id`, the name = `members_display_name`; `username` is ignored. Imported `gamereplays_id` links work on first sign-in.

Any provider except Steam may set `redirect_uri` when the return URL registered at the provider is another host's: the service sends that exact URL in the authorization and token requests, and that host must redirect the browser, query string intact, to `<service>/users/auth/<name>/callback`. That callback then counts as the registered URL for that provider only.

### 4.3 Website

Confidential client of the live environment, authorization code + PKCE, cookie session on the website. User actions use the user's access token; backend jobs use a `gopat-` token.

### 4.4 Third-party apps ("Sign in with GenOnline")

OAuth applications per environment (normally only live), consent screen, "authorized applications" page, delegated read scopes (`profile.read`, `matches.read`, `stats.read`), never staff or machine permissions.

Built so far: third-party clients (`first_party = false`) get a consent page on every sign-in (no remembered consent yet), `prompt=none` answers `consent_required`, and the page carries an antiforgery token bound to the signed-in user and refuses framing. The "authorized applications" page and the delegated read scopes are still to come.

### 4.5 Machines

`gopat-` tokens (`api-tokens-design.md`), per environment.

### 4.6 EOS

The EOS ID token (RS256, `sub` = user id, audience `genonline-eos`) is issued by each environment's accounts as the `eos` sign-in token of `POST /api/v1/me/linked_accounts/eos/sign_in_token`; each environment points at its own EOS deployment and JWKS.

### 4.7 Client registry

Every OAuth client is a row in `clients`, paired with OpenIddict's `oauth_applications` row of the same `client_id`. No code branches on a client id; behaviour follows the row:

| Field | Where | Meaning |
|---|---|---|
| `client_id` | both | `^[a-z0-9_]{3,32}$`, the same in every environment |
| display name, redirect URIs, type, secret, scopes | `oauth_applications` | type is `public` or `confidential`; OpenIddict stores only the secret's hash and checks redirects (loopback on any port) and scopes |
| `kind` | `clients` | `game`, `launcher`, `chat` or `app`; sets the session type (GameClient, GameLauncher, ChatClient, none). An app may not hold the `game` scope; the others need it |
| `game` | `clients` | `zerohour` or `generals` for a game client, `none` otherwise; the version check and the session registration (`GameSessionRegistration.Game`, which realtime build isolation keys on) use it |
| `first_party` | `clients` | first-party clients skip the consent page |
| `enabled` | `clients` | a disabled client loses its OpenIddict endpoint permissions, its tokens are refused at once, and its authorizations are revoked within one reload tick (or at the next start) |
| `min_version` | `clients` | lowest accepted `client_version` (`yyyy.mm.patch`) in the version check |
| accepted builds | `client_builds` | optional exe/INI CRC pairs; when listed, `POST /api/v1/auth/session` refuses other builds with `403 session.build_not_accepted` and the version check accepts only those executables (otherwise `crcfiles/<game>` decides) |

Security model: a public client's id is a label anyone can copy, so public clients hold player scopes only (`openid`, `profile`, `offline_access`, `game`); only confidential clients may hold more. Fair play stays with build identity plus anti-cheat, never with the client id.

`Accounts:clients` only seeds the first-party clients (always public) on first start, inserting a missing `client_id` and never updating one. Afterwards the CLI owns them; every change commits with its `audit_log` row (`target_kind = client`, `target_id = clients.id`):

```
GenOnlineService clients list
GenOnlineService clients add <client_id> --kind game|launcher|chat|app --name <name> --redirect-uris <uri,uri> --third-party true|false [--game zerohour|generals|none] [--type public|confidential] [--scopes <s1,s2>] [--min-version <v>] [--builds <exe:ini,...>]
GenOnlineService clients update <client_id> [--name] [--redirect-uris] [--scopes] [--min-version <v>|none] [--builds <exe:ini,...>|none] [--third-party true|false] [--enabled true|false] [--rotate-secret true] [--reason <text>]
GenOnlineService clients disable <client_id> [--reason <text>]
```

`kind`, `game` and `type` are fixed once added. A confidential client's secret is printed once by `add` and by `--rotate-secret`. The version check takes an optional `client_id`; without it, any enabled game client of the `game` may accept the version and executable.

## 5. Staff roles and bans from Keycloak (official deployment)

Roles granted in Keycloak follow a person into every environment without sharing any data between environments:

- Keycloak groups `genonline-admin`, `genonline-moderator` and `genonline-streamer` are sent as a `groups` claim. `streamer` is not a staff role: it grants no permission and no rank and only switches on client features (`Features:roles`, see the README).
- The Keycloak provider entry in each environment maps groups to roles (`trusted claims`). On every sign-in through Keycloak, the user's `user_roles` rows with `source = upstream:keycloak` are replaced by the mapped set; `source = local` rows are untouched.
- Optional group `genonline-banned` maps to a `play` ban with `source = upstream:keycloak`, refreshed the same way. It must never disable the Keycloak user, so appeals keep working.

**When group changes take effect.** Refresh tokens live 30 days, so a person may not sign in again for weeks. Two levels:

- **Baseline (every deployment):** upstream group changes apply at the next sign-in. For immediate effect staff use the local tools: `roles revoke`, a local ban, or revoking the user's sessions.
- **Group sync (official deployment, recommended):** the Keycloak provider entry gets a `group_sync` setting. When on, the reconcile tick polls Keycloak admin events (group membership added or removed for the mapped groups) with a Keycloak service account client that has only `view-events` and `view-users`. A change rewrites that user's `upstream:keycloak` rows at once; a removal from a staff group or an addition to `genonline-banned` also revokes the user's game sessions in that environment. A removal from `genonline-streamer` keeps the sessions and only sends the new feature list (`FeaturesUpdate`). If Keycloak is unreachable the environment keeps its last state, logs and alerts, and catches up from the last event time; it never grants anything from stale data. Admin events must be enabled in the realm.
- Only providers marked trusted may assert roles or bans; a self-hoster's Discord sign-in never grants staff.

**Rank rule for role changes.** Staff ranks:

| Role | Rank | Permissions |
| --- | --- | --- |
| `owner` | 3 | the same as `admin` |
| `admin` | 2 | every staff permission |
| `moderator` | 1 | kick, mute, rename, chat moderation |
| `streamer`, any other role | 0 | none (client features only) |

A staff member with `roles.manage` (the staff pages, `PUT`/`DELETE /api/v1/players/:id/roles[/:role]`) may grant or revoke a role only when they outrank the player (as for every moderation action: never themself or a peer) and the role ranks strictly below their own highest role (otherwise `roles.rank`). A service account token acts with admin rank. So an owner manages `admin`, `moderator` and `streamer`; an admin manages `moderator` and `streamer` of players and moderators; nobody changes another owner; and `owner` is granted or revoked with the `roles` CLI only, which has no rank. The same ranks decide every other staff action (renames, kicks, mutes, bans, message deletion), so an owner outranks an admin there too. `owner` is local only: a sign-in provider's role mapping that names it is ignored (`Database.Upstream`). Only `local` rows change; a role from a sign-in provider follows its sign-ins. Each change writes a `role_grant` or `role_revoke` audit row and reloads `StaffRoles`.

Effective permissions in `application` = all `user_roles` rows (local and upstream) → `StaffRoles` → `PermissionHandler`. Client features = the same roles → `Features:roles` → `Ready.features`, and `FeaturesUpdate` when the role reload changes the list.

## 5a. Display names

Model: Xbox modern gamertag. The display name is not unique; every account also has an automatic 4-digit tag (`0001` to `9999`), and `name#tag` is unique (`users.name_tag`, unique indexes on `(username_normalized, name_tag)`, names compared NFKC and upper case, and on `(name_key, name_tag)`, the look-alike key below). One global name and tag per person across all environments of the official deployment.

- **Official deployment:** Keycloak owns both, as user profile attributes `display_name` and `name_tag`. The GenOnline Keycloak plugin (repository `genonline-keycloak`: player ids, tags, GameReplays and Steam) checks the name (3 to 16 characters, no `#`, no control characters, no outer whitespace, no reserved words), assigns a static tag at sign-up that every rename keeps, and keeps `name_tag` read-only for users. There is no rename limit. Its token mapper sends `name` (the display name) and `name_tag` in the ID token, access token and userinfo.
- **Copy at sign-in:** on every sign-in through a `trusted` provider the environment copies the `name` and `name_tag` claims (`UpstreamAccounts`). An untrusted provider never sets a name. A local user that still holds the copied `name#tag` has a stale copy (it renamed in Keycloak and has not been back); it moves to a free local tag until its own next sign-in. Every change writes a `user_username_history` row with the previous name and tag, so old matches show who someone was.
- **Live renames (group sync):** with `group_sync` on, the reconcile tick also reads Keycloak user events of type `UPDATE_PROFILE` (a player's own rename) and admin events `UPDATE` on users (a staff rename in the admin console), each from its own cursor (`group_sync.<provider>.names_cursor`), then reads the user's `display_name` and `name_tag` and copies them. Players online get `MemberUpdate` and the `name.changed` notice at once. The realm must save `UPDATE_PROFILE` user events and admin events; the sync client needs `view-events` and `view-users`.
- **Renaming:** players call `PATCH /api/v1/me {name}`; staff with `moderation.rename` call `PATCH /api/v1/players/:id {name, reason?}` (rank rule, `audit_log` with the full reason and the old and new `name#tag`), and the Discord `/rename` goes through the same code. Where Keycloak owns names (an enabled `trusted` provider with `player_ids`, `AccountsSettings.NameOwner`), live has the provider's `rename` client (realm role `genonline-rename`) and calls the plugin's `POST /realms/<realm>/genonline/players/{player_id}/name` with `actor` `player:<player_id>` or `discord:<id>`; a 200 applies the returned name and tag at once (`MemberUpdate`) and leaves `last_seq` to the change-log consumer. With the change log on, that apply holds the same `last_seq` row lock as the consumer, so the push of the same seq finds the name current and writes no second history row. Plugin rule failures map to the `name.*` codes, its `name.taken` to `name.taken`; a timeout, refused connection, 5xx, 401, 403, 404 or `request.invalid` answers 503 `name.unavailable` with nothing applied. Test and dev, without that client, answer `name.managed`.
- **Chosen names (S17):** no real names are kept. A provider sign-up starts with a placeholder (`Player` and six digits) and owes a name (`users.name_required_at`); `/users/sign_up/welcome` asks for it before `/oauth/authorize` issues anything, the provider's own name only prefilling the box from a short-lived encrypted cookie. Staff reset an inappropriate name with `POST /api/v1/players/:id/reset_name` or `users name-reset`: a placeholder, the name owed again, every sign-in revoked, audited as `name_reset`.
- **Self-hosters without Keycloak:** the environment owns name and tag with the same rules. Look-alikes are one name: the key is the plugin's (lower case, NFKC, UTS #39 confusable skeleton from the plugin's ICU table, lower case), stored in `users.name_key` and unique with the tag. Sign-up takes the provider's name. Tags follow the name: a player's tag for a name comes from a keyed hash (`Names:tag_key`, HMAC-SHA256) of the player id and the look-alike key of the name, so the same player always gets the same tag for a name and its look-alikes, and other players get their own. When another player holds or has reserved that tag, the next tag of the player's sequence is taken, so a rename never fails on a taken tag (only when all 9,999 tags of a name are held or reserved, `name.taken`). Every `name#tag` a player has held stays reserved for them (`name_reservations`, unique on `(name_key, name_tag)`), so switching back to an earlier name always brings back its tag and no other player is ever given it. A sign-up has no player id yet and takes the first free tag of the name's own sequence. Renames no longer keep the tag. Two look-alike sign-ups or renames that race onto one tag meet a unique index, and the second takes its next tag. Both endpoints work the same way as in live. Reserved words follow the plugin's rule: its default list (`NameRules.DEFAULT_RESERVED_WORDS`, kept identical in `ProfileActions.ReservedNameWords`), matched case-insensitively anywhere in the name, with `name.reserved {word}` naming the word as the list spells it. A realm that replaces the list through the `reserved-words` option of its `genonline-display-name` validator changes only Keycloak's rule; the service list is fixed in code.
- **Reservation backfill:** the `NameReservations` migration creates `name_reservations` empty, and `GenOnlineService migrate` reserves every current `name#tag` and every tagged `user_username_history` row right after, on every run, rekeying reservations with the key. Existing players keep their names and tags. It reads users in batches of 1,000 by id, with their history and reservations. A pair several players held stays with the player who holds it now, else the one who held it last (`held_at`, the time the player last held the pair: when they took it, or when they left it, which is the `changed_at` of that history row); the run prints how many claims lost. A pair the running service reserves while the step runs is skipped and counted instead of failing the run, and the next run settles it. During a rolling deploy the old version keeps renaming without reservations until the rollout ends, so run `migrate` once more after the rollout; a pair taken in that gap meanwhile only lacks its reservation, nobody else can hold it while its player does. The follow-live mirror (`ApplyUpstream`) copies the upstream name and tag even when another player has the pair reserved, and reserves nothing; that player then gets their next tag while the copy holds it.
- **Tag key:** `Names:tag_key` is a base64 secret of at least 32 bytes. Outside Development the service and the command line tools stop when it is missing or short; Development uses a fixed key. Changing the key changes the tags of names a player has never held, never a reserved or current pair.
- **Sign-up races:** a sign-up's first try is the first free tag of the name's sequence; each try after a lost race picks a random one of the next free candidates, and after four tries any free tag, up to eight tries, so many sign-ups of one name at once all succeed.
- **Reservations use the look-alike key only:** current holders are checked under both `name_key` and `username_normalized`, reservations under `name_key` alone. Two names with one normalized form but different keys (upper case NFKC and the lower case skeleton can disagree on a few characters) do not block each other's reserved pairs; when such a name holds a player's old pair, the player gets their next tag for the name instead.
- **Key migration:** SQL cannot compute the key, so the `NameKeys` migration gives each existing row a unique placeholder and `GenOnlineService migrate` computes every key right after, on every run (so a new confusables table rekeys too). When existing names collide under the key and tag, it still keys every other user, leaves the colliding users their names, tags and placeholders, prints each group (tag, key, user ids and names) and exits with code 2; rename all but one user of each group (`PATCH /api/v1/players/:id`) and run `migrate` again. Until then only the exact-name checks cover the colliding users.
- **Key parity with the plugin:** `NameKey` lower-cases like Java's `toLowerCase(Locale.ROOT)`, not .NET's invariant culture: capital I with dot above (U+0130) becomes `i` and a combining dot, and capital sigma becomes final sigma (U+03C2) at the end of a word, with words found by the JDK word break rules as the plugin's JDK applies them (`GenOnlineService.Tests/NameKeyVectors.txt` pins vectors generated from the plugin's own `NameRules.normalize`; across every assigned code point and six million random mixed strings the two agree, except as follows). What still differs: .NET takes case mappings and NFKC/NFD from the host's ICU (Windows or the Linux `libicu`) and the plugin from its JDK, so a character one Unicode version knows and the other does not keys differently. On JDK 25 against Windows ICU these are 47 code points new in Unicode 16.0 (for example U+A7CB, the Garay letters U+10D50 to U+10D65, Todhri, Tulu-Tigalari, Gurung Khema, Kirat Rai). The Keycloak 26 image runs JDK 21 (Unicode 15.0), so there any character newer than Unicode 15.0 that the host ICU knows can differ the same way.
- **Showing names:** `Name#1234` on profiles (`/users/me` returns `display_name` and `name_tag`), friend lists and requests, and moderation and staff tools (Discord `/whois` accepts and prints `Name#tag`). Lobbies and chat show the name only, with the tag on every member whose name another member there shares; the service computes each lobby slot's `name` that way (both get the tag when the second joins, and lose it when one leaves), and the realtime `Member` carries `tag` for chat. Leaderboards serve user ids only; clients that show a name there show `Name#tag`.

## 5b. The plugin's change log (environment side)

The GenOnline Keycloak plugin appends every rename to a change log of full-state entries. Each environment follows that log: a rename made anywhere reaches every environment without a sign-in, and a missed push is caught up. The wire contract is `keycloak-changes-contract.md` (agreed v1, 2026-11-14).

- **Push:** `POST /-/keycloak/changes` takes one entry, signed with `X-GenOnline-Timestamp` and `X-GenOnline-Signature: sha256=<hex HMAC-SHA256(push_secret, timestamp + "." + body)>`, within 300 s.
  - It answers 204 when the entry is applied, is a duplicate, or starts a gap pull.
  - A bad signature or timestamp gets a 401 with the uniform body; a malformed entry gets 400 `invalid_argument {field}`.
  - It is an operator path: the edge proxy must never route `/-/keycloak`. Only the plugin inside the cluster calls it.
- **Order:** `last_seq` lives in `application_settings` (`keycloak.<provider>.change_log.last_seq`).
  - An entry with `seq = last_seq + 1` is applied; `seq <= last_seq` is ignored.
  - A gap pulls `changes?after=last_seq`.
  - Every apply holds a row lock on `last_seq`, so the copies of an environment apply in order and never twice.
  - An unknown `kind` or an unknown player id still advances `last_seq`.
- **Interval check:** at startup and every `interval_seconds` (60), the service compares `last_seq` with `changes/latest` and pulls when behind. This catches a lost last push and any downtime.
- **Resync:** when the log has been pruned past `last_seq` (410 `changes.expired`, or `oldest_seq` beyond it), the service reads every player's name and tag from `players?names` page by page. It then sets `last_seq` to the first page's `as_of_seq` and pulls the changes after it.
- **Applying:** a rename copies Keycloak's name and tag the same way a sign-in copy does: the user row, a `user_username_history` row whose `changed_by` is `upstream:<provider>`, and the stale-holder move. After commit, online players get `MemberUpdate` and the `name.changed` notice. While the change log is on, the group sync's event-based name poll stands down.

Configuration, under the trusted provider (`Accounts:providers:keycloak:change_log`):

| Key | Meaning |
|---|---|
| `enabled` | follow the change log (one trusted provider at most) |
| `client_id`, `client_secret` | a confidential client, service account only, holding the realm role `genonline-sync`; every environment has its own |
| `push_secret` | this environment's HMAC secret (at least 32 characters), the same value the plugin lists for it |
| `base_url`, `realm` | Keycloak's base URL and realm; both default from the provider's issuer |
| `interval_seconds` | the catch-up check (default 60, minimum 5) |

Tags never travel through the log. The tag assigned at first sign-in, and the import's `assign-tags`, write no entry; environments take the tag from the `name_tag` claim at sign-in, as in section 5a.

Upgrading the plugin from `d08727a` changes its name key. Right after that upgrade, call `POST /realms/<realm>/genonline/players/assign-tags` once with a client holding `genonline-rename`, before players rename again.

Every applied push or pull entry logs one Information line with its seq and player id. A pull answered with anything other than 200 or 410 (for example 400 `request.invalid`) is logged with the plugin's error code. A pull while Keycloak is down (refused or dropped connection, timeout, 5xx) logs one Warning line without a stack trace. Nothing is applied, and the next check retries.

Only live's client also holds `genonline-rename`, the role for the plugin's rename endpoint: a compromised test or dev environment then cannot rename live players. Calling that endpoint from `PATCH /api/v1/me` and the staff rename is the hub session's part.

## 5d. The look of the pages (T19)

The sign-in and account pages follow the Generals Online design system (boards Main, ChooseName, TwoFactor, CheckEmail, Refused, PhoneArabic and Account): `wwwroot/portal/tokens.css` (the `--go-*` tokens, dark only), the `gp-*` classes in `wwwroot/portal/generals.css`, self-hosted Play, IBM Plex Sans and IBM Plex Sans Arabic. `Accounts:cover_images` is a list of public paths on this service; missing means the three faction images, an empty list means no image, and an entry that is not a path on this service (an address with a host, whitespace or a quote) stops the start. The footer links come from `Agreements:terms:url`, `Agreements:privacy:url` and `Agreements:rules:url`; a document without an address links its built-in page (`/legal/terms`, `/legal/privacy`, `/legal/rules`, section 5c), and there is no Help link until a help page exists. Each configured address must be an absolute http or https URL without spaces or quotes, or the start fails.

The staff area (`/admin`) follows the design's Admin board on the same tokens and fonts: `wwwroot/portal/theme.css` is a copy of the design's Blazor Blueprint theme (`dist/portal/theme.css`), and the staff rules of `generals.css` restyle Blueprint's components to the design's component specs (nav, button, badge, card, field). Its account menu holds Edit profile and Sign out only, as the staff pages are in English and have no language switch, and its Help item opens the API docs.

Two differences from the earlier pages are intended: the two-factor sign-in page shows one code at a time, with "Use a recovery code instead" at `?recovery=1` on the same route and the same POST, and the pages now carry Back links, a skip link and a footer with the configured Terms, Privacy and Rules links.

## 5c. Agreements (S19)

Each account agrees to the current version of two documents, the Rules of Engagement (`rules`) and the Terms of Service (`terms`); the Privacy Policy is only linked. Versions, days and links are configuration (`Agreements:rules:version|date|url`, `Agreements:terms:version|date|url`, `Agreements:privacy:url`). A document without a version is not required, and with neither version nothing is gated, so self-hosters and tests sign in as before. A date that is not `yyyy-MM-dd`, a version longer than 32 characters or a url that is not an absolute http or https address stops the start. A wording-only fix keeps the version; a new version asks every player again.

- **Built-in documents:** a document without a url links this service's own page of it, `/legal/rules`, `/legal/terms` or `/legal/privacy`, everywhere it is linked: the footer, the cards' "Read the full rules", the Terms step, `/profile` and `GET /api/v1/me/agreements`. So a version without a url means "agree to the built-in page". The links are absolute on `Accounts:issuer` when it is set (the game client opens them), relative otherwise. The pages answer anyone, signed in or not, on the sign-in pages' design and footer; the text is English inside a left-to-right block whatever the page's language, with the configured version and day above it. The text lives in `GenOnlineService/Identity/Legal/{rules,terms,privacy}.html`, embedded HTML fragments (`Identity/LegalPages.cs`); HTML comments in them are notes for the operators, such as `TODO(operator)` facts the code cannot tell, and are stripped before serving. The rules have one section per card under the card's id, and each card's "Read the full rules" opens `/legal/rules#<id>`; a configured rules url is linked as it is. Changing a fragment's meaning is a new version (`Agreements:<doc>:version`).

- **Storage:** `user_agreements` (user_id, document, version, agreed_at; key user_id + document + version), add-only. A document is owed while its current version has no row for the account. Each new row writes an `agreement_accepted` audit row (actor and target the player, details `{document, version}`); agreeing again stores nothing.
- **First sign-in:** choose a name (5a) -> agree to the rule cards one by one -> tick the Terms -> start playing. `/oauth/authorize` checks the account in this order: email confirmed (section 7), staff two-factor set up, name chosen, agreements; each sends the player to its page and back (`interaction_required` with `prompt=none`). Provider and password accounts are treated alike.
- **The pages (`/users/agreements`):** six rule cards (ids `respect`, `chat`, `fair_play`, `match`, `accounts`, `service`, for later sanction work to name a section), each with its intro, its items, "Read the full rules" and Agree; then the Terms step with the Terms and Privacy links, a tick box and Start playing. Only owed documents are shown: with only the Terms owed the cards are skipped, with only the rules owed the Terms step is. A player who had agreed to an older version reads "The rules changed on <date>" before the first card. Every post carries the antiforgery token; nothing is recorded before the last step's post, which carries the rules version the cards were agreed under and the tick.
- **The refresh rule:** the game client's refresh grant (a client registered with a session type, `SessionRevocation.IsGameClient`) is refused with `invalid_grant` while an agreement is owed only when the player has no live game connection on this copy of the service (`WebSocketManager`, the check behind "online" on the staff player page). That is the client's login with a saved sign-in: on `invalid_grant` it deletes the saved sign-in and opens the browser, where authorize leads through the pages. Mid-session a refused refresh would log the player out ("Authentication with the Generals Online servers failed."), possibly in a lobby or a match, so a refresh while connected passes and the player agrees at the next login. Edge case: a player whose connection dropped and who refreshes while reconnecting is sent to the browser. The name-reset refresh rule (5a) is unchanged and applies whether or not the player is connected.
- **Website:** the sign-in cookie keeps working on `/api/v1/me/agreements`, so `/profile` shows each document's agreed version and day and links to the pages while one is owed. Staff see the agreed versions on the player page (`staff_view`).

## 6. Tokens

| Token | Issuer | Format | Lifetime | Notes |
|---|---|---|---|---|
| Access token | environment accounts | JWT, RS256, `typ: at+jwt`, `aud` = environment API | 15 min | `sub` = user id, `client_id`, `scope`; no roles |
| Refresh token | environment accounts | reference token (stored) | 30 days sliding, rotated each use | reuse revokes the authorization |
| ID token | environment accounts | JWT | 15 min | clients only, never accepted by the API |
| `gopat-` | environment | opaque | operator set, max 365 days | machines and staff scripts |
| EOS token | environment accounts | JWT, RS256 | 5 min | Epic only |

- Blocking a user revokes all authorizations; a ban revokes the game clients' authorizations only.
- `application` keeps user state, bans and roles in in-memory caches reloaded on the reconcile tick, so a block or ban cuts access within a tick even with a valid access token.
- Realtime connections authenticate once at connect and are closed on a change (`auth.revoked`, `moderation.banned`); no re-authentication on the open socket.
- The service-issued HS256 session tokens and `TokenRevocationManager` generations are retired.
- Keys: signing and encryption certificates from configuration, current plus next for rotation, published in the JWKS (section 6.1).

### 6.1 Keys shared by every copy

Every copy of one environment (the active copy and the standby in `realtime-failover.md`) must use the same keys. Otherwise a restart, a deploy or a handover signs everyone out and invalidates every token. Nothing is generated per start outside Development.

| Key | Protects | Kept in | Configuration |
|---|---|---|---|
| OpenIddict signing | access and ID tokens | certificates | `Accounts:signing_certificates` |
| OpenIddict encryption | authorization codes, stored refresh tokens, upstream sign-in state | certificates | `Accounts:encryption_certificates` |
| ASP.NET Core data protection | the `go_session` cookie, the consent page's antiforgery token | table `data_protection_keys` | `DataProtection:application_name`, `DataProtection:certificates` |

Configuration:

- `Accounts:signing_certificates`, `Accounts:encryption_certificates`: ordered lists. Each entry is one of:
  - a `.pfx` or `.p12` file path (password in `Accounts:certificate_password`);
  - a PEM file path holding the certificate and its unencrypted private key (for example `tls.crt` and `tls.key` concatenated);
  - the PEM text itself (for example from an environment variable).

  The first entry signs or encrypts. Every entry is published (signing keys in the JWKS) and accepted.
- `Accounts:issuer`: set it to the public URL. Every copy then issues and accepts the same `iss`; without it each copy uses the host of the request.
- `Accounts:ephemeral_keys`: in-memory keys, Development only.
- `DataProtection:application_name`: the same value on every copy of one environment and a different one per environment, for example `genonline-live`. It is required outside Development.
- `DataProtection:certificates` (optional) and `DataProtection:certificate_password`: the first certificate encrypts new ring keys at rest, and every listed certificate decrypts. The official deployment sets it: without it the ring rows are plain XML, and anyone who can read the database can forge sign-in cookies.
- Data protection keys rotate on their own: ASP.NET Core adds a ring key every 90 days to the same table, and every copy picks it up.

Startup refuses to run outside Development:

- when `Accounts:ephemeral_keys` is set;
- when the accounts profile has no signing or encryption certificate;
- when `DataProtection:application_name` is missing;
- when a certificate cannot be loaded or has no private key.

The error names the setting and entry index and never echoes the entry.

In the official deployment every key (signing, encryption, data protection and the device HMAC key) is created once by OpenTofu and kept in OpenBao. External Secrets delivers them to one Secret that every copy mounts read-only at `/keys`, so all copies share them and they survive the loss of the cluster. The keys come from outpost-infra `opentofu/genonline` (certificate then PKCS#1 key, RSA 3072, self-signed, 10 years); OpenTofu never replaces one on its own. A rotation step below is then a new key name in that root's `config/genonline.yaml` and in the chart's values, ordered as described here.

Rotation runbook (signing certificate). Every step is a Secret change followed by a rolling restart of every copy:

1. **Add.** Create the next certificate, for example `openssl req -x509 -newkey rsa:3072 -nodes -days 400 -subj "/CN=genonline-<env>-signing-<n>" -addext keyUsage=digitalSignature -keyout next.key -out next.crt`. List it second. The current certificate still signs, and both are published. Wait until every copy runs with it and any remote validator (an `application` profile with `Accounts:enabled` false) has reloaded the JWKS: restart it, or give it a day.
2. **Promote.** Swap the order so the next certificate is first. New tokens are signed with it, and tokens from the old certificate stay valid. A copy that is not yet restarted keeps signing with the old one, which every copy accepts.
3. **Retire.** After `refresh_token_days`, remove the old entry. The wait matters because OpenIddict signs refresh tokens too: the stored refresh token keeps its signature. A refresh token issued before the promotion and unused since is refused once its certificate is gone, which signs out that idle session. Active sessions are unaffected, since every refresh issues a new token signed with the new certificate.

The encryption certificate rotates the same way (use `keyUsage=keyEncipherment`). It also needs the `refresh_token_days` wait: stored refresh tokens and codes encrypted with it become unreadable once it is removed.

To rotate the data protection certificate, list the new certificate first and keep the old one second. Remove the old one after 90 days, once every ring key it protected has expired.

## 7. Account security

- Local passwords (operator toggle): policy, breached-password check, lockout, rate limits on sign-in, sign-up, reset and 2FA.
- HTTP rate limiting belongs to the proxy, not the service: flood protection, per-path limits (`/oauth/token`, `/oauth/authorize`, the sign-in pages, the API) and per-IP blocking belong to the reverse proxy in front of it (Envoy with CrowdSec in the official deployment); a self-hoster without such a proxy should put one in front. Set `TrustedProxies` to the proxy addresses only, so forwarded headers are trusted from the proxy alone and logs and `allowed_ips` see the real client address. Per-account limits (failed sign-ins lead to lockout) come with local passwords and TOTP in phase 3; ASP.NET Identity lockout (failed attempts lock the account) must be enabled for local password sign-in then, since a self-hoster may run without a proxy.
- Email confirmation and reset need mail (`Mail:delivery_method`: SMTP or the Brevo, Resend, Mailgun and Cloudflare APIs). Mail links are built on `Accounts:issuer` only, never on the request's `Host` header, so mail requires it and the service refuses to start without it. With mail, a password sign-up gets a confirmation link (`/users/confirmation?confirmation_token=`, 24 hours) and `/oauth/authorize` waits for a confirmed address (`/users/confirmation/new`, "check your email" with a resend; `interaction_required` with `prompt=none`); provider accounts never wait, and password accounts that existed before confirmation shipped count as confirmed. Self-service reset: `/users/password/new` answers the same whether or not the address has an account and mails `/users/password/edit?reset_password_token=` (1 hour, once); the new password revokes every sign-in, ends the lockout and is audited as `password_reset` by the player. Without mail nothing is sent, nothing waits and an operator resets passwords (`users password-reset`). Mail requests are limited per account and per client address in the service, since each costs a mail.
- Staff account support (no mail, so no reset link): an operator runs `users password-reset` or `users two-factor-reset` on the host, and an admin (`accounts.support`, never a moderator) does the same from the player page's Account menu through `POST /api/v1/players/:id/password_reset` and `POST /api/v1/players/:id/two_factor_reset`. Both paths call `UserResets`: a new random password of 20 characters (shown once, in the answer or on the console, never in the log or `audit_log`; there is no forced change at the next sign-in), or two-factor sign-in off with the key and the recovery codes deleted. Each ends the lockout, changes the security stamp (website sign-ins end within a minute) and revokes every sign-in of the account: authorizations and refresh tokens in the database and the stored game sessions, so a refresh answers `invalid_grant` and a session registration or realtime connect is refused on every copy. From the staff pages the service also drops its cached game sessions and closes the realtime connections with `auth.revoked` at once; after the CLI, which runs apart from the service, an open realtime connection lasts until it drops, and the next connect is refused. Every personal `gopat-` token the account owns is revoked too (a `token_revoke` row each), which matters for a taken-over staff account. The audit row carries `{sign_ins_revoked, tokens_revoked}`. The routes take the staff pages' sign-in cookie only (antiforgery, `Origin` and the staff two-factor rule), a reason is required, the staff rank rule applies (no self, peer, senior or owner), and `audit_log` keeps `password_reset` or `two_factor_reset` with the staff member, the target and the reason.
- Account deletion (Privacy Policy, "Your rights"): an operator runs `users delete <user_id> --reason <text> --yes` on the host when the owner asks (by email from the account's address, or through their sign-in provider). It cannot be undone. `AccountErasure` keeps the `users` row as a tombstone under the same id, so matches, ratings, statistics, leaderboards and sanctions keep pointing at it, records it in `erased_accounts`, state `deactivated`, with a neutral name (`Deleted` and six digits, a shape no live player may choose: the name rules refuse it and its look-alikes as reserved word `deleted`) and no email, password, two-factor key, lockout or sign-in times. It deletes the name history, name reservations, recovery codes, agreements, staff roles, friends, friend requests and blocks in both directions, preferences, network settings, session events, the user's chat (`Chat.DeleteUserData`: sent messages, read markers, closed reports they filed, connection reports, groups), and blanks `display_name` in their `match_players` rows (replay files are not edited). Sign-ins, game sessions and personal tokens are revoked as in a password reset. While a ban on the account is active, its mailbox and every sign-in provider id are kept only as keyed hashes in `identifier_hashes` (the provider links themselves are deleted), so the same mailbox (in any spelling) or Steam/Discord/other provider account cannot sign up again unbanned, and its device rows stay without their raw value and address for evasion matching; the hourly purge removes the hashes once no ban is active and the devices 90 days after they were last seen. Moderation records keep their own retention: sanctions, sanction evidence, `audit_log`, open reports and reports about the player, anti-cheat reports and automated rule matches. The `account_erase` audit row holds the reason and counts, never the name or email; two erasures of one account at the same time write one, as the `erased_accounts` row is claimed first. The running service ends the account's connections on its 5 second access reload. `GoUserStore.DeleteAsync` erases the same way with the owner as actor; no page calls it yet (self-service deletion is not offered).
- A mailbox is no identity: a mailbox may belong to more than one live account, and sign-up never refuses one because another account uses it. A password account still confirms its own address, because password reset and sign-in use it. The only refusal is a mailbox a ban holds: a keyed hash in `identifier_hashes` kept for an erased account while its ban lasts (the provider ids the same), or a live account under an active ban. Sign-up and email changes are refused for that mailbox in canonical form (lower case, the +tag dropped, for Gmail the dots dropped and googlemail.com read as gmail.com) while the ban lasts, and the check runs in the same transaction that writes the account or the new address. No hash is written for a live unbanned account, so `users.email` is its only copy; imported accounts keep their address the same way, and the import still gives a mailbox to one account and reports the others as duplicates. Account linking stays off (`auto_link_verified_email` false), so a provider account is never joined to another.
- TOTP 2FA with recovery codes, mandatory for staff when local passwords are on; with Keycloak, MFA is enforced in Keycloak.
- Sign-up modes: open, invite only, upstream only.
- Account page: sessions, authorized apps, linked identities; email notifications for security changes.
- `audit_log` for security changes; sign-ins as metrics and `session_events`.

## 8. Endpoints (accounts)

```
/users/sign_in, /users/sign_up, /users/sign_out, /users/password, /users/confirmation   (HTML)
/users/sign_up/welcome (choose a name), /users/agreements (rules and terms), /profile (the player page), /profile/two_factor_auth   (HTML)
/profile/friends (friends and requests; POST add, accept, decline, cancel, remove, block)                           (HTML)
/profile/blocked (blocked players; POST add, remove)                                                               (HTML)
/users/two_factor, /profile/applications, /profile/identities                            (HTML)
/users/auth/:provider, /users/auth/:provider/callback                                     (upstream sign-in)
/oauth/authorize, /oauth/token, /oauth/revoke, /oauth/introspect, /oauth/userinfo         (OpenIddict)
/.well-known/openid-configuration, /oauth/discovery/keys
/api/v1/admin/users, /api/v1/admin/users/:id/identities, /api/v1/admin/applications        (admin API)
```

## 9. Later add-on: follow live

Not built now. If a long-lived public test environment ever needs a player's live friends or roles:

- Live exposes `GET /api/v1/internal/changes?cursor=` (ordered changes: friend added or removed, role granted, ban created), read with a `gopat-` token scoped `sync.read`.
- The other environment pulls a whitelisted subset, matches people by their Keycloak identity, keeps its last state if live is down and catches up from the cursor.
- Data flows one way. Only blocks may flow back, and only to make things stricter.

## 10. Import

- The legacy database is imported into the **live** environment, keeping user ids. Test and dev start empty and provision just in time.
- `identities` come from the old columns: `steam_id` → steam, `discord_id` → discord, `google_id` → google, `gamereplays_*` → gamereplays.
- **Official deployment, matching people to Keycloak:** the import also produces a Keycloak user import file: one Keycloak user per GenOnline user, with federated identity links for Steam, Discord and Google, the attribute `player_id` set to the old user id, and the plugin's counter set above the highest id. A person's first Keycloak sign-in to live then lands on their existing user, because the id is the same (section 3.2). Without Keycloak, users claim their account by signing in with any linked provider.
- Duplicate identity links are reported before import, never merged.
- `Ghost` and `DevAccount` users are not imported; their match slots stay with no user (details in `schema-v1-design.md` section 4).
- Old `admin` and `permissionslevel` values are not mapped: the import prints every affected user, and staff are granted again by hand (`roles grant`) or through Keycloak groups.
- Old `banned` users become `play` bans (reason from `ban_reason`; `banned_by`, `ban_verified_by` and aliases go into an `audit_log` entry).

## 11. Effect on work already done

- `docs/identity-keycloak.md` and `arctic/feat/keycloak-idp`: replaced. `LoginWithIdp`, the Keycloak admin client and suspension polling go; display name rules and just-in-time provisioning carry over.
- Auth hardening (`arctic/fix/auth-hardening`): EOS binding (`924be111`) and EosToken (`03168398`) carry over (issuer moves to accounts). Refresh rotation (`cf5f0549`) and Logout (`9645b0e1`) are superseded once OpenIddict tokens are live. Rate limiting and key hardening (`d9e53b06`) carry over in shape.
- Client: login becomes PKCE against the environment's `/oauth/authorize`, then `POST /api/v1/auth/session`; the `linked_account` plugin keeps working.
- `api-v1-gitlab-model.md`: `/auth/login|refresh|logout` become the OAuth endpoints plus `/api/v1/auth/session`.

## 12. Phases

1. **Core:** users, identities, Identity, OpenIddict, one upstream provider (Keycloak, generic OIDC), game PKCE client, token validation in `application`, `POST /api/v1/auth/session`, bans versus blocks, import.
2. **More providers:** Discord, Steam, Google, GameReplays (OAuth 2.0); linking rules; trusted claims for roles.
3. **Account security:** local passwords, email, reset, TOTP, lockout, notifications.
4. **Third-party apps:** OAuth applications, consent, delegated scopes.
5. **Later:** WebAuthn, SAML upstream, follow-live sync.

## 13. Open questions

1. GameReplays: does its OAuth server issue OpenID Connect ID tokens? Decides whether Keycloak can broker it directly.
2. Who builds phase 1? Suggested: a fresh dedicated session with this doc as its brief, building on `arctic/feat/moderation-roles` (permissions, `audit_log`, bans).
