# Automated rules

Staff write rules over player data that flag, kick, mute or ban players. A rule is data, not code: a versioned graph
document that the service validates on save and compiles into delegates, so an evaluation runs no parsing, reflection
or expression text. Text rules for names, lobby names and chat stay on their own engine for now (`docs/api.md`,
`/api/v1/staff/text_rules`).

## The rule document

A rule document is a graph of nodes and edges, in JSON with snake case keys:

```json
{
  "nodes": [
    { "id": "t", "kind": "trigger", "trigger": "sign_in" },
    { "id": "c", "kind": "condition", "query": {
        "match": "all",
        "rules": [
          { "field": "device.machine_guid", "operator": "in_list", "value": ["AB12-CD34-YY"] },
          { "match": "any", "rules": [
            { "field": "account.age_days", "operator": "less_than", "value": 7 },
            { "field": "network.ip", "operator": "cidr", "value": "203.0.113.0/24" }
          ] }
        ] } },
    { "id": "a", "kind": "action", "action": "ban", "duration_s": null, "reason": "Ban evasion" }
  ],
  "edges": [
    { "from": "t", "to": "c" },
    { "from": "c", "to": "a", "output": "true" }
  ]
}
```

- `trigger`: exactly one, the event the rule is checked on (see Triggers).
- `condition`: a query tree. A group is `{match: all | any, rules: [...]}`, a condition is `{field, operator, value}`;
  groups nest. The node has two outputs, `true` and `false`.
- `action`: `flag`, `kick`, `mute` or `ban` with a `reason` (1 to 512 characters, written to the audit log). A mute
  needs `duration_s`, a ban without one is permanent; lengths are whole minutes from one minute up to ten years, as for
  staff sanctions. A kick and a flag take no length.
- Edges go from the trigger, from a condition with `output` `true` or `false`, and never out of an action. Several
  edges from one output run every node they reach.
- Each node may carry `position {x, y}` for the editor; the service keeps it as given.
- `step` is reserved for later step nodes and is refused for now.
- A property the document does not know, such as a misspelt `duration` for `duration_s`, is refused with its path.

### Validation on save

A document is checked before it is stored, and every problem comes back with its path inside the document, as
`invalid_argument {field: document, problems: [{path, reason}]}`:

- exactly one trigger, known node kinds and unique ids of 1 to 64 characters;
- at most 100 nodes, 300 edges and 64 KB of JSON; at most 50 conditions in one node, groups at most 6 deep, lists
  of at most 100 values;
- edges that name existing nodes, with the outputs above, no loop, every node reached from the trigger and at least
  one action reached;
- known fields that the rule's trigger provides, an operator of the field's type, and a value of that type.

`POST /api/v1/staff/rules/validate` runs the same checks without saving.

## Fields

Every field reads as a list of values: a player has several devices and sign-in providers, and an unknown fact is an
empty list. `GET /api/v1/staff/rules/fields` lists the catalogue with each field's type, label, triggers and operators.

| Field | Type | Source |
|---|---|---|
| `device.machine_guid`, `device.mac`, `device.volume_serial` | device | every device id the player signed in with (`devices`) |
| `mac.vendor` | text | the IEEE OUI vendor of each MAC address, or `locally administered / virtual` |
| `mac.locally_administered` | bool | true when any MAC address of the player is locally administered |
| `network.ip` | ip | the address of the event, else of the last session event |
| `network.country`, `network.city`, `network.asn`, `network.owner` | text, number | GeoIP of that address; the country falls back to the one the sign-in stored |
| `account.age_days` | number | whole days since the account was created |
| `account.email_domain`, `account.email_confirmed` | text, bool | the email address |
| `account.providers` | text | `password` and every linked sign-in provider |
| `account.name`, `account.name_key` | text | the display name and its look-alike key |
| `account.two_factor` | bool | two-factor sign-in on |
| `sanctions.count`, `sanctions.active_ban`, `sanctions.active_mute` | number, bool | every sanction ever, and the active ban and mute |
| `anticheat.new_accounts`, `anticheat.modules`, `anticheat.probes`, `anticheat.devices`, `anticheat.quickmatch_rules` | number | unreviewed rows of each anti-cheat review list |
| `reports.open` | number | open player reports against the player |
| `stats.elo`, `stats.games_played` | number | the all time rating once rated, and the matches played |

Event fields come only with the triggers whose event carries them: `event.provider` (sign_in), `event.client`
(game_session), `event.lobby_id` (lobby_join), `event.match_id` (match_result, anticheat_report), `event.won`,
`event.desynced` and `event.duration_s` (match_result), `event.anticheat_kind` (anticheat_report).

Operators by type, text compared without case:

| Type | Operators |
|---|---|
| text | `equals`, `not_equals`, `in_list`, `contains`, `starts_with`, `is_empty` |
| number | `equals`, `not_equals`, `in_list`, `greater_than`, `less_than`, `between` (two numbers, both ends included), `is_empty` |
| bool | `equals`, `is_empty` |
| ip | `equals`, `not_equals`, `in_list`, `starts_with`, `cidr`, `is_empty`; an IPv4 address mapped into IPv6 compares as IPv4 |
| device | `equals`, `not_equals`, `in_list`, `is_empty` |

`equals`, `in_list`, `contains`, `starts_with`, `greater_than`, `less_than` and `between` hold when any value of the
field does; `not_equals` holds when none does. Device ids are stored only as keyed hashes, so staff enter the raw value
the client sends (as the player page shows it, in any case) or the whole device key of the investigation page, and the
service compares their hashes.

Facts load lazily: a field loads on its first read in an evaluation and is kept for the rest of it, so every rule of
the event shares one read and a field no rule reads is never loaded.

## Triggers

The rules run after the event, on a queue of the copy that saw it, so the event never waits for them and never fails
because of them; a failed evaluation is logged and dropped, and one rule that fails leaves the others running. A
trigger without an enabled rule queues nothing.

| Trigger | When |
|---|---|
| `sign_in` | a password sign-in, a sign-in after the two-factor code, a sign-up, and a sign-in through a provider |
| `game_session` | `POST /api/v1/auth/session` registered a game session, after the device ids are stored |
| `lobby_join` | the player joined a lobby |
| `match_result` | the player reported a match result |
| `anticheat_report` | a new row of an anti-cheat review list for the player |
| `sweep` | a manual sweep over every player (see Preview and sweep) |

## Modes and actions

Mode and enabled live on the rule, not in its versions:

- **Watch** records the match only.
- **Flag** records it in the review queue.
- **Enforce** takes the actions and records what it did.

Safety rules hold in every mode:

- Rules never act on staff: a player whose roles in the database carry a staff rank gets a match with outcome
  `staff_exempt` and nothing else.
- A second match of the same rule, player and trigger inside the duplicate window (`AutomatedRules:duplicate_window_s`,
  600 by default) records nothing and acts on nothing.
- A player already banned is not banned again, one already muted is not muted again. The check and the new sanction
  run under a row lock on the player in the database, so two evaluations at once, on one copy or two, add one at most. A ban goes first, so a kick after
  it is not needed; a kick of a player who is offline does nothing.
- The circuit breaker: an Enforce rule that already acted `AutomatedRules:breaker_actions_per_hour` times in the last
  hour (20 by default) switches itself to Flag, with a `rule_breaker` audit row, and the match that tripped it goes to
  the review queue.

Sanctions go through the staff sanction code (`SanctionActions.ApplyAsync`): the same reason and length rules, the same
`sanctions` and `audit_log` rows, a ban that disconnects the player on every copy, and a mute the player is told about.
The actor is the rule: actor kind `automation`, actor `rule:<id>@v<version>`, source `automated_rules`, no actor id.

## Matches and the review queue

Every match is a row of `automated_rule_matches` with the rule id and the version that fired, the trigger, the mode it
ran in, the actions taken, the outcome (`watched`, `flagged`, `enforced`, `staff_exempt` or `nothing_to_do`) and the
facts that decided it: every field the rule read with its values, device ids as the fingerprint of their hash. A Flag
match, and an Enforce match with a flag action, waits in the review queue (`review_state` `pending`) until staff with
`anticheat.review` mark it `reviewed` or `dismissed` (audited as `rule_match_review`). A player with a pending match is
in the staff player list's Flagged filter for reviewers, as one with an unreviewed anti-cheat row is.

## Preview and sweep

A preview shows who a rule would match now, before staff switch it on: `POST /api/v1/staff/rules/preview` with a saved
`rule_id` or an unsaved `document` reads players in id order as the rule's trigger, with no event fields, and answers
the count and the first 25 matching players with the actions the rule would reach and the facts that decided it. It
stops after 5000 players or 10 seconds (`complete` false, `timed_out` true for the time limit), takes no action and
writes no match; only a `rule_preview` audit row with the trigger and the counts.

Preview and sweep have no event, so the event fields (`event.*`) read as empty there: `not_equals` and `is_empty` on an
event field match every player, and every other operator on one matches nobody.

A sweep runs one rule's current version over every player as trigger `sweep`, in its mode, with every safety rule of a
normal match: `POST /api/v1/staff/rules/:id/sweep` starts it, `GET` reads its progress `{rule_id, rule_version, state,
total, scanned, matched, started_by, started_at, finished_at}` and `DELETE` cancels it. It runs only on the copy that
holds the realtime lease (a standby answers 503 `service.unavailable`), in batches of 100 players, stops as
`interrupted` when the copy loses the lease, and one rule runs one sweep at a time (409 `conflict {what: sweep}`). The
progress lives on that copy; `state` is `running`, `completed`, `cancelled`, `interrupted` or `failed`. Starting and
cancelling are audited as `rule_sweep_start` and `rule_sweep_cancel`. A rule whose trigger is `sweep` runs only this
way.

## Storage and versions

`automated_rules` holds the rule with its mode and enabled flag, `automated_rule_versions` every saved document.
Each save is the next immutable version and becomes the current one; an old version is never changed. Mode, enabled,
name and description change on the rule without a new version. A save that sends `base_version` is refused with 409
`conflict` when another save came first. Archiving takes a rule out of the list and out of every copy, restoring
brings it back as it was. Every change is audited (`rule_create`, `rule_version`, `rule_update`, `rule_archive`,
`rule_restore`, target kind `automated_rule`).

Every copy keeps the current versions of the enabled rules compiled in memory. The copy that saved reloads at once and
tells the others through `CatalogSignals` (resource `rules`); the reconcile tick reloads a copy that missed it. A copy
compiles again only when a rule's version, mode or enabled state changed.

## API

Every route takes the staff sign-in cookie and needs `rules.manage` (admins and owners); `docs/api.md` lists them.

| Route | Answer |
|---|---|
| `GET /api/v1/staff/rules?archived=` | `{rules: [{id, name, description, mode, enabled, current_version, archived, trigger, created_by, created_at, updated_by, updated_at}]}` |
| `POST /api/v1/staff/rules` | body `{name, description?, mode?, enabled?, document, comment?}` (mode `watch` and enabled false when omitted); 201 `{rule, document, versions}` |
| `GET /api/v1/staff/rules/:id` | `{rule, document, versions: [{number, created_by, comment, created_at}]}`, the document of the current version |
| `GET /api/v1/staff/rules/:id/versions/:number` | `{number, created_by, comment, created_at, document}` |
| `POST /api/v1/staff/rules/:id/versions` | body `{document, comment?, base_version?}`; 201 as the read of the rule |
| `PATCH /api/v1/staff/rules/:id` | body `{name?, description?, mode?, enabled?}`; the rule |
| `POST /api/v1/staff/rules/:id/archive`, `POST /api/v1/staff/rules/:id/restore` | the rule |
| `POST /api/v1/staff/rules/validate` | body `{document}`; `{valid, problems: [{path, reason}]}` |
| `GET /api/v1/staff/rules/fields` | `{fields: [{key, type, label, description, triggers, operators}], triggers, actions, modes}` |
| `POST /api/v1/staff/rules/preview` | body `{rule_id}` or `{document}`; `{scanned, matched, complete, timed_out, players: [{player, actions, facts}]}` |
| `POST /api/v1/staff/rules/:id/sweep`, `GET`, `DELETE` | 201, 200 and 200 `{rule_id, rule_version, state, total, scanned, matched, started_by, started_at, finished_at}` |

The matches need `anticheat.review`, as the other review lists:

| Route | Answer |
|---|---|
| `GET /api/v1/staff/rules/matches?review=&rule_id=&user_id=&trigger=&outcome=` | `{matches: [{id, rule_id, rule_name, rule_version, player, trigger, matched_at, mode, actions, outcome, facts, review_state, reviewed_by, reviewed_at}]}`, newest first, offset paged; `review` is `pending` (the default), `none`, `reviewed`, `dismissed` or `all` |
| `PATCH /api/v1/staff/rules/matches/:id` | body `{review: reviewed or dismissed}`; the match. Only a pending match changes, one reviewed before answers as it is |
