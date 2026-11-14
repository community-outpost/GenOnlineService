# REST fixtures

Example bodies for the REST endpoints in section 10 of `docs/realtime-protocol.md`, the ones that replace earlier hub methods. The full REST route map ships its own fixtures with the `/api/v1` route move.

| File | Content |
|---|---|
| `<method>-<path>.request.json` | the request body; path parameters become plain words (`post-players-sanctions` is `POST /api/v1/players/:id/sanctions`) |
| `<method>-<path>.response.json` | the success body |
| `error.response.json` | a failure body: the code and params of section 7 |

Field names match the hub, so the client reuses its types.
