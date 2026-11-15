# API tokens (gopat-)

Status: **draft, greenfield**. Reviewed by the release session (owner of permissions) and a system design review. Nothing is built. Target location once agreed: `docs/api-tokens.md` in GenOnlineService.

## 0. Assumptions

- **Greenfield deploy.** No existing game clients, no existing consumers of the old keys. User accounts may be imported, credentials are not.
- **Humans** sign in through Keycloak only: native PKCE in the game client, then `POST /v1/auth/login-with-idp` for service session tokens (`docs/identity-keycloak.md`). There is no LoginCode/CheckLogin flow and no website login callback.
- **Machines** (website backend, Discord bot, partners, monitoring) use `gopat-` tokens, the only machine credential.
- **One `application` instance per environment**, which holds the in-memory websocket hub. The design notes what changes past one instance.
- **Volume:** tens of service accounts, at most a few hundred tokens, machine traffic in the low hundreds of requests per second. The full active token set fits in memory.

## 1. What does not exist

These never ship: `API:webserver_key`, `API:keys`, `Monitor:username/password`, `APIKeyHelpers`, `EApiKeyType`, `BasicAuthenticationHandler`, the `JwtOrBasic` policy scheme, the `MonitorOrApiKey` / `AnyClientOrMonitorOrApiKey` policies, `IsInRole("Monitor")` branches, `POST LoginCode`, `POST CheckLogin`, the `env/{environment}/contract/{contract_version}` route prefix, and the EOS UserInfo endpoint `POST OID`.

## 2. Principles

1. **One machine credential.** Every machine caller presents `Authorization: Bearer gopat-...`.
2. **Least privilege.** A token carries explicit scopes and never grants more than its owner holds.
3. **Mandatory expiry, monitored.** Every token expires; expiry is alerted before it causes an outage.
4. **Shown once, stored hashed.** Only a SHA-256 hash is kept.
5. **Permissions, not roles.** Scopes are permission strings from `Authorization.cs`. Endpoints check permissions only.
6. **Dangerous grants need the host.** Privileged permissions can only be granted, and their tokens minted, from the CLI on the host.
7. **Auditable.** Every mint, rotate, revoke and grant is written to the audit log with actor and token id, never the secret.

## 3. Components

```
                    +--------------------+
 game client ------>|  Keycloak (PKCE)   |
      |             +--------------------+
      | Keycloak access token
      v
 +-------------------------------------------------------------------+
 | application (per environment)                                     |
 |                                                                   |
 |  Authentication                                                   |
 |   Bearer gopat-...  -> ApiTokenHandler --+                        |
 |   Bearer <JWT>      -> JwtBearer (HS256) +--> ClaimsPrincipal     |
 |                                          |                        |
 |  Authorization: perm:<name> policies ----+--> PermissionHandler   |
 |       token principal -> permission claims                        |
 |       session principal -> StaffRoles.HasPermission(sub, name)    |
 |                                                                   |
 |  In-memory caches (reloaded on the 5 s reconcile tick)            |
 |   staff roles | service account grants | active tokens            |
 |   last-used buffer (flushed on the same tick)                     |
 +-------------------------------------------------------------------+
      ^                ^                 ^                ^
 website backend   discord bot      partners        monitoring
   (gopat-)         (gopat-)        (gopat-)         (gopat-)

 host shell: GenOnlineService service-accounts | tokens   (CLI, direct DB)
```

## 4. Token format

```
gopat-<40 chars base62>
```

- `gopat-` prefix, GitLab style (`glpat-`). Lets the auth handler route without parsing, and lets secret scanners match it: `\bgopat-[0-9A-Za-z]{40}\b`.
- 40 base62 characters from `RandomNumberGenerator`, about 238 bits.
- Nothing is encoded in the token; everything lives in the database row.
- SHA-256 without pepper is enough at this entropy: a database leak exposes no usable token.

## 5. Owners

| Owner kind | Example | Permissions come from |
|---|---|---|
| **User** (personal token) | a staff member's script | the user's staff roles (`user_roles`) |
| **Service account** | `website`, `discord`, `monitor`, `partner-replaybot` | `service_account_permissions` |

Service accounts are their own table, not rows in `users`: they never log in and never appear in leaderboards, friends, lobbies or name checks.

Personal tokens are only for users with at least one staff permission; a player holds no permissions, so a player token would grant nothing.

Effective permissions of a request = **token scopes ∩ owner permissions**, evaluated per request from the in-memory caches. Removing a role, a grant or a token narrows access within one reconcile tick (5 s).

## 6. Permissions

Two disjoint sets in one catalogue (`Authorization.cs`):

- **Human permissions:** granted by staff roles. Service accounts may also hold them (the Discord bot needs moderation).
- **Machine permissions:** granted only to service accounts. `StaffRoles.PermissionsFor` and `Reload` drop and log any machine permission found on a role.

Machine permissions are further marked **privileged** when they can act on any user's account. Privileged permissions can only be granted, and tokens carrying them minted, through the CLI on the host. The HTTP API refuses them even for admins, so the escalation needs host access, not an admin role.

Existing human permissions (moderation-roles): `moderation.kick`, `moderation.mute`, `moderation.rename`, `moderation.ban`, `chat.staff_badge`, `names.protected`, `social.unblockable`, `roles.manage`, `server.stats`, `announcements.send`.

Added by this design:

| Permission | Kind | Endpoints |
|---|---|---|
| `users.revoke` | machine, **privileged** | revoke a user's sessions (website account security page, sign out everywhere) |
| `ops.database` | machine, **privileged** | `GET /-/database` |
| `match_history.read` | machine | `GET /api/v1/matches`, `GET /api/v1/matches/:id` |
| `lobbies.read` | machine | `GET /api/v1/lobbies` for non-player callers |
| `server.stats` (existing) | human | `GET /-/self_check/version` |
| `tokens.manage` | human (admin) | the management API in section 9 (v2) |
| `audit.read` | human (admin) | `GET /api/v1/audit_events` |
| `settings.manage` | human (admin) | `GET` and `PUT /api/v1/application/settings` |

Monitoring self tests run on the internal ops port without a permission (section 11), so there is no `ops.self_test`.

Scope names are validated against the catalogue at mint time; an unknown scope is rejected, never dropped.

## 7. Storage

```
service_accounts
  id            bigint pk
  name          varchar(64) unique
  description   varchar(256)
  created_by    varchar(128)              -- user id, or "cli:<os user>"
  created_at    timestamp (UTC)
  disabled_at   timestamp null

service_account_permissions
  service_account_id  bigint fk
  permission          varchar(64)
  granted_by          varchar(128)
  granted_at          timestamp (UTC)
  primary key (service_account_id, permission)

api_tokens
  id                bigint pk
  owner_user_id     bigint null
  owner_service_id  bigint null
  name              varchar(64)
  token_hash        binary(32) unique     -- SHA-256 of the full token string
  token_last4       char(4)
  scopes            text                  -- space separated
  allowed_ips       text null             -- CIDR list
  created_by        varchar(128)
  created_at        timestamp (UTC)
  expires_at        timestamp (UTC)       -- required
  last_used_at      timestamp null
  last_used_ip      varchar(45) null
  revoked_at        timestamp null
  revoked_by        varchar(128) null
  check (exactly one of owner_user_id, owner_service_id is set)
  -- name is a label, not unique: rotation keeps the name, tokens are told apart by id (GitLab model)
```

Audit events go to `audit_log`, the generalized `moderation_log` from the moderation work (one append-only timeline for kicks, bans, role grants and token events). Columns: `actor_kind` (user, cli, discord, service_account, upstream; an upstream row has the provider name, such as `keycloak`, as `actor`), `actor_id` (nullable), `actor` (label), `action`, `target_kind` (user, service_account, token), `target_id` (nullable), `reason`, `source`, `scope`, `details`, `created_at`.

Token events:

| Action | target_kind | details |
|---|---|---|
| `token_mint` | token | name, owner, scopes, expires_at, allowed_ips |
| `token_rotate` | token | old token id, overlap minutes |
| `token_revoke` | token | reason |
| `sa_create`, `sa_disable` | service_account | description |
| `sa_grant`, `sa_revoke` | service_account | permission |

CLI actions use `actor_kind=cli` with the OS user as `actor`. The secret and its hash never appear in `details`.

Retention: a `jobs` task deletes token rows 90 days after `revoked_at` or `expires_at`. Only active tokens are loaded into memory.

## 8. Request authentication

`ApiTokenHandler` runs when the bearer value starts with `gopat-`; any other bearer value goes to the JwtBearer path (HS256 pinned).

1. Hash the token and look it up in the in-memory active token set.
2. Fail with **401** when the token is missing, revoked or expired, the owner is disabled, banned or deleted, or the client address is outside `allowed_ips`. Every failure gets the same 401 body, `{"error":"unauthorized","params":{}}`, also used for session tokens; the real reason goes to the log with `token_id`.
3. Build the principal: `auth_kind=token`, `token_id`, `owner_kind`, `owner_id`, one `permission` claim per effective permission.
4. Record `last_used_at` / `last_used_ip` in an in-memory buffer; the reconcile tick flushes it.

Authorization:

- An `IAuthorizationPolicyProvider` maps `perm:<name>` to a policy with a `PermissionRequirement`.
- `PermissionHandler`: a token principal passes when it carries the permission claim; a session principal passes when `StaffRoles.HasPermission(sub, name)` is true.
- Endpoints use `[Authorize(Policy = "perm:...")]` and never check how the caller authenticated.

HTTP rate limiting belongs to the proxy (Envoy with CrowdSec), which limits and blocks callers; the service does none.

`allowed_ips` relies on the real client address. The service logs an error at startup when any active token has an allow-list while `TrustedProxies` is empty and forwarded headers are present on requests, because every caller would then appear as the proxy.

## 9. Management

### v1: CLI only (host access)

```
GenOnlineService service-accounts create website --description "Website backend"
GenOnlineService service-accounts grant website users.revoke
GenOnlineService service-accounts revoke-grant website users.revoke
GenOnlineService service-accounts disable website
GenOnlineService tokens create --owner service:website --name website-prod --scopes users.revoke --expires 2026-05-01 --allowed-ips 203.0.113.4/32
GenOnlineService tokens list [--owner service:website]
GenOnlineService tokens rotate <id> [--overlap-minutes 60]
GenOnlineService tokens revoke <id>
```

The CLI writes the database directly and the running service picks changes up on the next reconcile tick. It is the only way to grant privileged permissions or mint tokens that carry them.

### v2: HTTP API

The CLI and the routes below share one implementation (`Rest/ApiTokenAdmin.cs`), so the rules, limits and audit entries are the same; REST actions are audited with `actor_kind=user`, the caller's id and `source=api`.

| Method and path | Caller | Answer |
|---|---|---|
| `GET /api/v1/personal_access_tokens` | staff web session | the caller's own tokens, newest first, paged |
| `POST /api/v1/personal_access_tokens` | staff web session | 201, the token and its `token` secret |
| `GET /api/v1/personal_access_tokens/self` | a `gopat-` token | the token making the request |
| `POST /api/v1/personal_access_tokens/:id/rotate?overlap_minutes=&expires_at=` | staff web session | 201, the new token and its secret |
| `DELETE /api/v1/personal_access_tokens/:id` | web session | 204 |
| `GET/POST /api/v1/service_accounts` | web session with `tokens.manage` | list (paged); 201 with the new account |
| `PATCH /api/v1/service_accounts/:id` | web session with `tokens.manage` | `{grants: {add, remove}, disabled}`, the updated account |
| `GET/POST /api/v1/service_accounts/:id/personal_access_tokens` | web session with `tokens.manage` | the account's tokens; 201 with a new token and its secret |

- Caller must be a staff web session, a session of a registered `app` client (never a game, launcher or chat client session, never a `gopat-` token); any other caller gets `forbidden {permission: "tokens.manage"}`. A web session without any staff permission gets `forbidden {}` on the personal token routes, except `DELETE`, so a user who lost a role can still revoke their own tokens.
- A caller only sees and changes their own personal tokens; another owner's token id answers `not_found {what: "token"}`. `/self` answers `not_found {what: "token"}` to a session.
- The create body is `{name, scopes: [permission], expires_at, allowed_ips?: [cidr]}`; `expires_at` is an ISO 8601 date (midnight UTC) or a UTC timestamp.
- Privileged permissions are refused: grant and mint them through the CLI (`forbidden {permission}`). Removing a privileged grant is allowed.
- Granting a human permission to a service account requires the caller to hold it (`forbidden {permission}`).
- Personal tokens: scopes must be human permissions the caller holds (`forbidden {permission}`); an unknown scope is `invalid_argument {field: "scopes"}`.
- Failures: `invalid_argument {field}` (`name`, `description`, `scopes`, `grants`, `expires_at`, `allowed_ips`, `overlap_minutes`), `token.limit_reached {max}` (422), `conflict` (409: a service account name in use, or rotating a revoked, expired or already rotated token), `not_found {what}` (`token`, `service_account`).
- Tokens of a service account are rotated and revoked through the CLI only.

### Rules for both

- `expires_at` required, at most `ApiTokens:max_lifetime_days` (default 365) away.
- Tokens carrying a privileged permission must set `allowed_ips` and live at most `ApiTokens:privileged_max_lifetime_days` (default 30).
- At most `ApiTokens:max_active_per_owner` (default 20) active tokens per owner.
- Rotate mints a new secret with the same scopes and `allowed_ips`. `overlap_minutes` defaults to **0**: the old token stops at once. Planned rotations pass an explicit overlap (max 1440). Rotate needs the same rights as revoke.
- The token is shown once in the create and rotate output and never again.

## 10. Configuration

```json
"ApiTokens": {
  "max_lifetime_days": 365,
  "privileged_max_lifetime_days": 30,
  "max_active_per_owner": 20,
  "expiry_warning_days": 14
}
```

## 11. Monitoring and operations

- Metric `api_token_expires_in_seconds{token_id, name, owner}`; alerts at 14 and 3 days. A daily log warning lists tokens inside `expiry_warning_days`.
- Metrics `api_token_auth_failures_total{reason}` and `api_token_requests_total{owner}`.
- The operator endpoints live under `/-/` (`/-/database`, `/-/self_check/version`) on the service port; the edge blocks `/-/*` with one rule, except the public probes below. `/-/database` needs `ops.database` because it reads raw database state.
- `/-/health`, `/-/liveness`, `/-/readiness` and `/.well-known/jwks.json` stay public.

## 12. Security notes

- Lookup by hash of the full token needs no constant-time compare.
- Tokens are never logged; logs carry `token_id` and `token_last4`.
- Tokens are per environment: separate databases, so a prod token never works on dev or test.
- Banning or deleting a user disables their personal tokens within one tick.
- Rotation guidance: privileged tokens 30 days, website and Discord 90 days, partners at most 365 days.

## 13. Trade-offs

| Decision | Benefit | Cost |
|---|---|---|
| Long-lived minted tokens instead of Keycloak client credentials | No Keycloak call per machine request, familiar model | A leaked token works until revoked or expired; mitigated by expiry alerts, short privileged lifetimes and allow-lists |
| Full in-memory token set on a 5 s tick | No database read per request | Revocation takes up to 5 s; needs a push channel past one instance |
| Machine permissions disjoint from roles, privileged ones CLI-only | An admin account alone cannot impersonate users | Host access needed for a small set of operations |
| CLI first, HTTP API later | Smallest attack surface in v1 | Admins need shell access to provision until v2 |

## 14. Revisit as the system grows

- **More than one `application` instance:** revocation needs a push (database notify or pub/sub), not only the tick.
- **Partners asking for self service:** partner user accounts and the v2 API.
- **One credential across environments:** move service accounts and tokens to the `accounts` service.

## 15. Dependencies and ownership

- Builds on `arctic/feat/moderation-roles` (`Authorization.cs`, `user_roles`, the reconcile tick, the moderation and audit log).
- The `perm:` policy layer is built once in `Authorization.cs` and shared with staff sessions. Who builds it first is open.
- Routes live under `/api/v1` (`docs/api.md`); operator endpoints under `/-/`.
