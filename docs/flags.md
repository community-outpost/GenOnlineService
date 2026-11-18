# Game config flags

The settings the game reads from `GET /api/v1/config` (and the ones service code reads) are flags in the database. They replace `data/serviceconfig.json`: the first start seeds revision 1 from the values that file held, so the game gets the same answer. Staff change them on the staff pages (`/admin/config`) or over REST, with `flags.manage`.

## The model

- A flag is one field: a key (`client.connection.reconnect_timeout_ms`), a scope (`client` for the game, `service` for service code), a label, help text and a group for the page, a type (`bool`, `int`, `number`, `string`, `choice`) with its limits (`min`, `max`), unit and choices, a value, whether it is in the answer (`enabled`), and targeting rules.
- A targeting rule gives another value to some players: players with a role, game clients by client id, players from a country (two-letter code), or a percentage of players (1 to 99, always the last rule, the same players every time). The first rule that matches wins, everyone else gets the value. A flag may instead carry custom flagd targeting written by hand; the page shows it and leaves its editing to export and import.
- `client.<group>.<name>` keys build the grouped `service_config` of `GET /api/v1/config`: `client.input_delay.formula.repair_budget` is `input_delay.formula.repair_budget`. A flag that is not enabled is left out, and the game then keeps its built-in default.

## Draft, publish, rollback

- The `flags` table is the draft. Every change to it (create, edit, archive, restore, import, reset) is an audit row and changes nothing for players yet. Each flag has a version; a change sends it in `If-Match` and gets 409 `conflict.stale` when someone changed the flag meanwhile.
- Publishing writes the whole draft as the next revision in `flag_revisions`, a flagd document (https://flagd.dev/reference/flag-definitions/) with the staff fields in its metadata. Publish sends the `draft_etag` of the draft it showed in `If-Match`, so only the reviewed draft goes live; a draft without changes answers `flag.no_changes`.
- A rollback publishes an earlier revision again as a new revision (`rollback_of` names it); the draft is not changed. History is never rewritten.
- Archiving takes a flag out of the next revision; restoring brings it back. A flag service code reads cannot be archived (`flag.in_use`).
- Export is the document of a revision (`GET /api/v1/flags/revisions/:number`); import takes such a file, or any flagd file, as the new draft, and archives the fields it leaves out.

## Delivery

- The copy that publishes reloads at once and tells the other copies over Redis; every copy also checks the live revision every 5 seconds, so a missed message only delays it.
- The game reads its values from `GET /api/v1/config` as before, or with OFREP (OpenFeature Remote Evaluation Protocol): `POST /ofrep/v1/evaluate/flags` answers every enabled client flag evaluated for the caller, with an `ETag`; `If-None-Match` with that tag answers 304 while nothing changed. `POST /ofrep/v1/evaluate/flags/:key` evaluates one. The evaluation context is taken from the session (user id as `targetingKey`, roles, client id, country of the address, environment); a context in the request is ignored. The OFREP routes are outside `/api/v1`, so the OpenAPI document leaves them out.
- The service evaluates flags with its own flagd evaluator; the OpenFeature SDK is not used.
