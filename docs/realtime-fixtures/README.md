# Realtime protocol fixtures

One example per hub method argument, result and event of `docs/realtime-protocol.md`. Each file is the single argument object of the SignalR message, not the record around it.

| File | Content |
|---|---|
| `<Method>.request.json` | the argument a client passes to a hub method |
| `<Method>.result.json` | the `Result` a method returns when it carries data |
| `<Method>.error.json` | an example failed `Result` for that method |
| `Result.ok.json`, `Result.error.json` | the generic success and failure `Result` |
| `<Event>.event.json` | the argument of a server event |

Rules:

- The service test serializes the real C# types with the hub's JSON options and compares the result with these files, ignoring whitespace and key order.
- The client test parses every file with the C++ protocol code and re-serializes it the same way.
- A change to a message changes its fixture in the same commit. Both tests then fail until both sides follow.
