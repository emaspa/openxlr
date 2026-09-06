# Local HTTP API v1

The daemon listens on `127.0.0.1:37890`. HTTP requests use the same per-session
token as the existing WebSocket clients, presented as `Authorization: Bearer
<token>`. Read it from `$XDG_RUNTIME_DIR/openxlr/token`, or from
`$XDG_CONFIG_HOME/openxlr/token` (`~/.config/openxlr/token`) when the runtime
directory is unset. The daemon creates the private token file at startup.
There is no second credential or change to the existing UI/OpenDeck login.

Foreign browser Origins are refused even with a valid token. JSON commands
require `Content-Type: application/json` with UTF-8 encoding. Credentials in
query strings are not accepted. Keep the token out of logs and bug reports.

| Endpoint | Response |
|---|---|
| `GET /healthz` | Unauthenticated process liveness only, not hardware readiness |
| `GET /api/v1` | Version and endpoint discovery |
| `GET /api/v1/state` | Combined state message |
| `GET /api/v1/plugins` | v1 result containing a plugins message |
| `POST /api/v1/commands` | Execute one existing cmd object |
| `WS /api/v1/events` | Same authenticated protocol as /ws |

Both WebSocket paths require the existing first-message authentication:
`{"cmd":"auth","token":"..."}`. They send no state before authentication.
HTTP's Bearer header does not replace that WebSocket exchange. Ping, close,
message deadlines and command budgets remain the same on both socket paths.

Commands use the names and fields in [the WebSocket API](api.md), including
current Monitor A/B feed commands. Both transports share the dispatcher,
validation and broadcasts. HTTP returns
`{"apiVersion":"1","ok":true,"messages":[]}` after a successful mutation.
Read replies are in `messages`; rejected commands return HTTP 400, `ok:false`
and an error message. Some rejected optimistic edits also include current state.
This reports execution, not a new durability guarantee: saving follows each
existing command's behavior. Never automatically retry a mutation after losing
the connection; it may already have executed.

Error status codes: 401 missing/wrong token, 403 foreign Origin, 408 body-read
deadline, 413 body over 64 KiB, 415 wrong Content-Type, 429 budget exhausted or
another HTTP mutation in flight. Chunked bodies have the same 64 KiB cap and
five-second deadline. One HTTP command runs at a time, with no waiting queue.
All authenticated HTTP responses use `Cache-Control: no-store`.

The [OpenAPI document](openapi-v1.json) describes the HTTP endpoints. Restarting
the daemon rotates its existing per-session token; clients must reread it.
