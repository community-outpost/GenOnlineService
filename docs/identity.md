# Identity: every environment is its own identity provider (OpenIddict)

Status: **draft, topology decided by the user** (separate environments tied together by Keycloak). Proposed to replace `docs/identity-keycloak.md` (branch `arctic/feat/keycloak-idp`). Nothing is built. Target location once agreed: `docs/identity.md`.

Each GenOnline environment owns its accounts and issues every token itself, like a GitLab instance. Upstream sign-in providers (Keycloak, Discord, Steam, Google, any OIDC) are linked to a local account, like GitLab's OmniAuth providers. The official deployment ties its environments together through one Keycloak; a self-hoster runs the same thing with or without one.

## 0. Decisions and assumptions

- **Greenfield deploy.** No old clients, no legacy login flow, no shared keys.
- **Separate environments.** Prod, test and dev are complete, self-contained instances: own database, own users, own ids, own bans, friends and blocks. Nothing is shared between them except the upstream login.
- **The official deployment** uses Keycloak as the only upstream provider in every environment (Discord, Steam and Google are brokered inside Keycloak). Local passwords are off there.
- **Self-hosters** run one environment with local passwords and/or Discord and Steam directly, no Keycloak needed.
- **Later add-on, not now:** a one-way "follow prod" sync (section 9).
- **Scale.** About 2,500 concurrent players at peak, about 4 sign-ins per second in a login burst.

## 1. Topology

```
                     Keycloak (official deployment only)
                 brokers Discord, Steam, Google; groups for staff
                   |                 |                 |
         +---------+------+  +-------+--------+  +-----+----------+
         | prod           |  | test           |  | dev            |
         |  accounts      |  |  accounts      |  |  accounts      |
         |  application   |  |  application   |  |  application   |
         |  one database  |  |  one database  |  |  one database  |
         +----------------+  +----------------+  +----------------+

 self-hoster:  one box like the ones above, upstream = local passwords / Discord / Steam
```

- **`accounts`** (ASP.NET Core Identity + OpenIddict) and **`application`** (game API, realtime) are profiles of one binary. They run in one process by default; the split is configuration.
- One database per environment holds account tables and game tables.
- A compromise of dev or test cannot reach prod: nothing connects them except Keycloak, which only signs people in.

## 2. Why this shape

| Need | How it is met |
|---|---|
| Small operators | One binary, one database, no external services |
| Same login everywhere (official) | Keycloak as upstream in every environment |
| Staff everywhere (official) | Keycloak groups mapped to roles at sign-in (section 5) |
| Isolation | Structural: environments share no data |
| Third-party "Sign in with GenOnline" | OAuth applications per environment (normally only prod) |

Trade-offs accepted: user ids, friends, blocks and bans are per environment. Dev and test data is throwaway; a player who matters lives in prod.

## 3. Accounts data (per environment)

GitLab shape on top of ASP.NET Core Identity. Full table definitions in `schema-v1-design.md`.

- `users`: account, display name (unique), email (optional), password hash (null without local passwords), `state` (active | blocked | deactivated), 2FA fields, sign-in timestamps.
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

- The game API accepts this environment's access tokens directly; `POST /api/v1/auth/session` registers the game session (device ids, CRCs, ban check, session event), it does not mint tokens.
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

GameReplays offers OAuth sign-in (Invision Community OAuth 2.0 server). It is a regular upstream provider: a generic OAuth 2.0 handler in accounts (authorization code, then the user from the Invision user-info API `/api/core/me`, `extern_uid` = GameReplays member id). Imported `gamereplays_id` links work on first sign-in. For the official deployment it is brokered in Keycloak like the others; if GameReplays issues no ID tokens, Keycloak's OIDC broker cannot use it directly and needs either a small identity provider extension or GameReplays configured as a provider in each environment's accounts next to Keycloak (to be verified against GameReplays' endpoints).

### 4.3 Website

Confidential client of the prod environment, authorization code + PKCE, cookie session on the website. User actions use the user's access token; backend jobs use a `gopat-` token.

### 4.4 Third-party apps ("Sign in with GenOnline")

OAuth applications per environment (normally only prod), consent screen, "authorized applications" page, delegated read scopes (`profile.read`, `matches.read`, `stats.read`), never staff or machine permissions.

Built so far: third-party clients (`first_party = false`) get a consent page on every sign-in (no remembered consent yet), `prompt=none` answers `consent_required`, and the page carries an antiforgery token bound to the signed-in user and refuses framing. The "authorized applications" page and the delegated read scopes are still to come.

### 4.5 Client registry

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

### 4.5 Machines

`gopat-` tokens (`api-tokens-design.md`), per environment.

### 4.6 EOS

The EOS ID token (RS256, `sub` = user id, audience `genonline-eos`) is issued by each environment's accounts; each environment points at its own EOS deployment and JWKS.

## 5. Staff roles and bans from Keycloak (official deployment)

Roles granted in Keycloak follow a person into every environment without sharing any data between environments:

- Keycloak groups `genonline-admin`, `genonline-moderator` and `genonline-streamer` are sent as a `groups` claim. `streamer` is not a staff role: it grants no permission and no rank and only switches on client features (`Features:roles`, see the README).
- The Keycloak provider entry in each environment maps groups to roles (`trusted claims`). On every sign-in through Keycloak, the user's `user_roles` rows with `source = upstream:keycloak` are replaced by the mapped set; `source = local` rows are untouched.
- Optional group `genonline-banned` maps to a `play` ban with `source = upstream:keycloak`, refreshed the same way. It must never disable the Keycloak user, so appeals keep working.

**When group changes take effect.** Refresh tokens live 30 days, so a person may not sign in again for weeks. Two levels:

- **Baseline (every deployment):** upstream group changes apply at the next sign-in. For immediate effect staff use the local tools: `roles revoke`, a local ban, or revoking the user's sessions.
- **Group sync (official deployment, recommended):** the Keycloak provider entry gets a `group_sync` setting. When on, the reconcile tick polls Keycloak admin events (group membership added or removed for the mapped groups) with a Keycloak service account client that has only `view-events` and `view-users`. A change rewrites that user's `upstream:keycloak` rows at once; a removal from a staff group or an addition to `genonline-banned` also revokes the user's game sessions in that environment. A removal from `genonline-streamer` keeps the sessions and only sends the new feature list (`FeaturesUpdate`). If Keycloak is unreachable the environment keeps its last state, logs and alerts, and catches up from the last event time; it never grants anything from stale data. Admin events must be enabled in the realm.
- Only providers marked trusted may assert roles or bans; a self-hoster's Discord sign-in never grants staff.

Effective permissions in `application` = all `user_roles` rows (local and upstream) → `StaffRoles` → `PermissionHandler`. Client features = the same roles → `Features:roles` → `Ready.features`, and `FeaturesUpdate` when the role reload changes the list.

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
- Keys: signing and encryption certificates from files or the OS store, two active for rotation, published in the JWKS.

## 7. Account security

- Local passwords (operator toggle): policy, breached-password check, lockout, rate limits on sign-in, sign-up, reset and 2FA.
- HTTP rate limiting belongs to the proxy, not the service: flood protection, per-path limits (`/oauth/token`, `/oauth/authorize`, the sign-in pages, the API) and per-IP blocking belong to the reverse proxy in front of it (Envoy with CrowdSec in the official deployment); a self-hoster without such a proxy should put one in front. Set `TrustedProxies` to the proxy addresses only, so forwarded headers are trusted from the proxy alone and logs and `allowed_ips` see the real client address. Per-account limits (failed sign-ins lead to lockout) come with local passwords and TOTP in phase 3; ASP.NET Identity lockout (failed attempts lock the account) must be enabled for local password sign-in then, since a self-hoster may run without a proxy.
- Email verification and reset need SMTP; without SMTP an operator runs upstream-only sign-in.
- TOTP 2FA with recovery codes, mandatory for staff when local passwords are on; with Keycloak, MFA is enforced in Keycloak.
- Sign-up modes: open, invite only, upstream only.
- Account page: sessions, authorized apps, linked identities; email notifications for security changes.
- `audit_log` for security changes; sign-ins as metrics and `session_events`.

## 8. Endpoints (accounts)

```
/users/sign_in, /users/sign_up, /users/sign_out, /users/password, /users/confirmation   (HTML)
/users/two_factor, /profile/account, /profile/applications, /profile/identities          (HTML)
/users/auth/:provider, /users/auth/:provider/callback                                     (upstream sign-in)
/oauth/authorize, /oauth/token, /oauth/revoke, /oauth/introspect, /oauth/userinfo         (OpenIddict)
/.well-known/openid-configuration, /oauth/discovery/keys
/api/v1/admin/users, /api/v1/admin/users/:id/identities, /api/v1/admin/applications        (admin API)
```

## 9. Later add-on: follow prod

Not built now. If a long-lived public test environment ever needs a player's prod friends or roles:

- Prod exposes `GET /api/v1/internal/changes?cursor=` (ordered changes: friend added or removed, role granted, ban created), read with a `gopat-` token scoped `sync.read`.
- The other environment pulls a whitelisted subset, matches people by their Keycloak identity, keeps its last state if prod is down and catches up from the cursor.
- Data flows one way. Only blocks may flow back, and only to make things stricter.

## 10. Import

- The old production database is imported into the **prod** environment, keeping user ids. Test and dev start empty and provision just in time.
- `identities` come from the old columns: `steam_id` → steam, `discord_id` → discord, `google_id` → google, `gamereplays_*` → gamereplays.
- **Official deployment, matching people to Keycloak:** the import also produces a Keycloak user import file (one Keycloak user per GenOnline user, with federated identity links for Steam, Discord and Google, and attribute `go_user_id`). On a person's first Keycloak sign-in to prod, the Keycloak provider (trusted) asserts `go_user_id`, and prod links that Keycloak identity to the existing user. Without Keycloak, users claim their account by signing in with any linked provider.
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
5. **Later:** WebAuthn, SAML upstream, follow-prod sync.

## 13. Open questions

1. GameReplays: does its OAuth server issue OpenID Connect ID tokens? Decides whether Keycloak can broker it directly.
2. Who builds phase 1? Suggested: a fresh dedicated session with this doc as its brief, building on `arctic/feat/moderation-roles` (permissions, `audit_log`, bans).
