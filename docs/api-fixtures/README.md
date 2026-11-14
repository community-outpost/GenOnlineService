# REST fixtures

Example bodies for every REST route: the ones in section 10 of `docs/realtime-protocol.md` and the rest of the `/api/v1` and `/-/` routes in `docs/api.md`.

| File | Content |
|---|---|
| `<method>-<path>.request.json` | the request body; path parameters are left out (`post-players-sanctions` is `POST /api/v1/players/:id/sanctions`), `/-/` is left out (`get-health` is `GET /-/health`). A route that ends in `:id` gets `-item` when the same method also lists that path (`get-players-item` is `GET /api/v1/players/:id`); a query that selects another answer adds its name (`get-players-stats-ids` is `GET /api/v1/players/stats?ids=`) |
| `<method>-<path>.response.json` | the success body |
| `error.response.json` | a failure body: the code and params of section 7 |

Field names match the hub, so the client reuses its types.
