# Local HTTP API v1

The daemon listens on `127.0.0.1:37890`. HTTP requests use the same per-session
token as the existing WebSocket clients, presented as `Authorization: Bearer
<token>`. Read it from `$XDG_RUNTIME_DIR/openxlr/token`, or from
`$XDG_CONFIG_HOME/openxlr/token` (`~/.config/openxlr/token`) when the runtime
directory is unset. The daemon writes the private token file once it is
listening, and not before.
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
mix-to-output feed commands, which accept any existing mix or a sum of distinct
mix ids, and `setOutputRoute` for an individual route's level. For example,
`{"cmd":"setOutputRoute","device":"alsa_output.headset","mix":"chat","value":0.5}`
adds a 50% Chat feed to an already selected headset; `value:0` disconnects it.
Both transports share the dispatcher,
validation and broadcasts. HTTP returns
`{"apiVersion":"1","ok":true,"messages":[]}` after a successful mutation.
Read replies are in `messages`; rejected commands return HTTP 400, `ok:false`
and an error message. This includes failed plugin installation and Windows
plugin file operations: their typed reply remains in `messages`, followed by
the error or failed `commandResult` when a request id was supplied.
Some rejected optimistic edits also include current state.
This reports execution, not a new durability guarantee: saving follows each
existing command's behavior. Never automatically retry a mutation after losing
the connection; it may already have executed.

Error status codes: 400 a body that is not valid UTF-8, or a plain request
on the events route without a WebSocket upgrade; 401 missing/wrong token;
403 foreign Origin; 408 body-read deadline; 413 body over 64 KiB; 415 wrong
Content-Type; 429 budget exhausted or another HTTP mutation in flight. Chunked bodies have the same 64 KiB cap and
five-second deadline. One HTTP command runs at a time, with no waiting queue.
All authenticated HTTP responses use `Cache-Control: no-store`.

For a read-only check from a shell in the daemon's user session:

```sh
TOKEN=$(cat "${XDG_RUNTIME_DIR:-${XDG_CONFIG_HOME:-$HOME/.config}}/openxlr/token")
curl --fail --silent --show-error -H "Authorization: Bearer $TOKEN" \
  http://127.0.0.1:37890/api/v1/state
unset TOKEN
```

`getPluginSetup` is available through `POST /api/v1/commands` and reports
the effective plugin host, Wine and bridge provider. Its `memoryLockLimitBytes`
is the running daemon's soft memory-lock limit in bytes (-1 for unlimited,
null when unknown). `memoryLockHardLimitBytes` adds the hard limit with the
same units and sentinel values. `memoryLockNote` distinguishes a soft limit
with room to rise from a low hard limit that needs the user manager's
ceiling checked. Advice is null without Windows plugin support or when the
soft limit is at least 256 MiB or unlimited.
`skippedFailedCount` and `skippedFailedBundles` quietly expose bundles
skipped after a failed scan, including their reason and original failure
time. The list is capped at 128; the count includes omitted bundles.
`wineTrace` reports the running daemon's deep Wine trace setting.
`setPluginWineTrace` through `POST /api/v1/commands` requires a boolean
`value` and replies with `pluginSetup`. It changes only the daemon's process
environment, without a restart or a saved preference. Enable it, send
`rescanPlugins`, collect diagnostics after the scan, then disable it.
Failed bundles are retried; successful unchanged bundles stay cached.
`getPluginDiagnostics` reads bridge status and the latest completed native
scan evidence without syncing or changing inserts. It also reports both
memory-lock limits, the skipped bundles and `hostEnvironment`: the effective
`loaderEnvironment`, opt-in `cleanLaunch` flag, `removedLoaderEnvironment`,
`wineLoader`, resolved `wineRunner`, scanner-only `wineTrace` opt-in and
`scannerWineDebug` effective channels. Explicit `WINEDEBUG` takes precedence
over the trace preset. Loader values and Wine debug channels are bounded to 4096
characters each. The diagnostics archive redacts these paths with its
existing path redaction. Both reply shapes are
documented in [api.md](api.md); the transport does not select a bridge itself.

`addWindowsPluginFolder` and `removeWindowsPluginFolder` also use
`POST /api/v1/commands`, with an absolute `path`. They return a `pluginInstall`
message after refreshing the catalogue. Check that message's `ok` and
`message`, not just the HTTP status: registry or wrapper-cleanup failures are
reported there. Removal keeps the original files and refuses folders backing
current inserts. With the system bridge, folder registration is shared with
other applications. The [folder-management contract](api.md) covers partial
failures and path limits. `getWindowsPluginFiles` lists individual files in
one registered folder. `removeWindowsPluginInserts` takes `path` and removes
that file's plugin classes from all current chains, saving before it answers;
confirm with the user first because affected audio paths are rebuilt. It
keeps other inserts and saved profiles. `setWindowsPluginEnabled` takes `path` and a boolean
`value`; `deleteWindowsPlugin` takes `path` and permanently removes a standalone
file or bundle after the client has obtained confirmation. Both mutations
return `pluginInstall` and refresh the catalogue. Wine-installed and linked
files cannot be deleted through this API; use the Windows uninstaller in the
appropriate Wine prefix.

`getNativeEditorRules` and `setNativeEditorRule` use the same command endpoint.
The setter takes `kind`, `plugin`, optional `name`, and `blocked`: true forces
OpenXLR controls, false allows the native editor, and null/absent restores the
release default. It returns `nativeEditorRules` after saving. This changes only
editor availability, not audio processing or insert chains. `showInsertUi`
rejects blocked editors over HTTP just as it does over WebSocket.

The [OpenAPI document](openapi-v1.json) describes the HTTP endpoints. Restarting
the daemon rotates its per-session token once the new instance listens;
clients must reread it.

For `setEnforcedDefaults`, `sink: "@monitor"` follows the first selected
monitor output as the system playback device. The response state retains
that value; see [the command contract](api.md) for resolution and volume
synchronization.

Capture channels use the same command endpoint:
`{"cmd":"createCaptureChannel","name":"Headset mic","source":"alsa_input.usb-headset","capturePair":0}`.
The source must be present. Success is returned after the layout is saved.
State channel entries expose `captureSource`, `capturePair` and `captureConnected`;
a disconnected source retains its binding and reconnects when it returns.

`{"cmd":"routeFocusedApp","channel":"music"}` uses the same focused-application
routing as PC and OpenDeck keys. Enable **Desktop keys** in the running window.
KDE Plasma supplies the focused process; GLib's `gdbus` must be installed.
An unavailable desktop service, absent audio client or ambiguous identity
returns the normal command error response and does not select a guessed app.

Output keys use the same authenticated command endpoint:
`{"cmd":"adjustOutputVolume","value":-0.05}` lowers the current system output
by five percentage points; an optional `device` binds an exact output name.
`{"cmd":"setOutputDeviceVolume","value":1.0}` sets that output to 100%,
`value` from 0 to 1.5 on the desktop scale.
`{"cmd":"toggleOutputMute"}` toggles the current output's mute; on a selected
monitor output it toggles the mixes feeding that output, on a monitor mix
sink that mix.
`{"cmd":"setMainOutput","device":"alsa_output.usb-headset"}` selects and enforces
that output. `@monitor` follows the selected monitor output. Limits, rejected
targets and linked-monitor behavior match the [WebSocket contract](api.md).
These commands require the daemon but do not require a running UI or KDE.

The existing insert commands accept every current channel id, including
software, Aux and external-capture channels, as well as `mix:<id>`. Stereo
channels require a stereo-compatible plugin; XLR 1 and XLR 2 remain mono.
Validation and effect status are shared with the WebSocket transport.
