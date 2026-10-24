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
| `GET /api/v1/devices` | `connected`, `device`, `capabilities`, `detected` and `devices` from the state |
| `GET /api/v1/profiles` | `profiles`, `activeProfile` and `recallOnConnect` from the state |
| `GET /api/v1/mixer` | The state's `mixer` |
| `GET /api/v1/channels`, `/channels/{id}` | Every channel, or the one with that id |
| `GET /api/v1/mixes`, `/mixes/{id}` | Every mix, or the one with that id |
| `GET /api/v1/inserts`, `/inserts/{id}` | Every insert chain by key, or one chain |
| `GET /api/v1/plugins` | v1 result containing a plugins message |
| `GET /api/v1/plugin-setup` | v1 result containing a pluginSetup message |
| `GET /api/v1/plugin-diagnostics` | v1 result containing a pluginDiagnostics message |
| `GET /api/v1/diagnostics` | v1 result containing a diagnostics message |
| `GET /api/v1/editor-rules` | v1 result containing a nativeEditorRules message |
| `POST /api/v1/commands` | Execute one existing cmd object |
| `WS /api/v1/events` | Same authenticated protocol as /ws |

`/state` also carries `daemonBuildId`, the running daemon assembly's module
identity. It lets `openxlr doctor` compare the running build with the one
installed on disk without restarting it. See [installation.md](installation.md).

The device, profile and mixer resources are parts of the same snapshot
`/state` returns, with the same field names; two requests may see two
different moments. Channel and mix ids are exact and case-sensitive. Insert
chains are keyed as in the state: an XLR or capture input channel id, or `mix:<id>`, so
`/api/v1/inserts/mix:monitor` reads Monitor A's chain. While the mixer is off
every mixer resource answers 503; with the mixer running an unknown id
answers 404, and so does a channel without a chain. A chain the state holds
with no inserts in it reads as `[]`. A profile directory that cannot be read
leaves `profiles` empty and is reported in the state's `warning`.
The mixer reports `defaultMonitorMix` for outputs without an explicit feed.
Every mix reports `editable` and `deletable`; the final monitor remains
renameable and reorderable but cannot be deleted. The existing `createMix`,
`renameMix`, `deleteMix` and `setLayoutOrder` commands manage monitor mixes,
Monitor A/B included. These reads never build, rebuild or save anything.

`plugins`, `plugin-setup`, `plugin-diagnostics`, `diagnostics` and
`editor-rules` run the existing `listPlugins`, `getPluginSetup`,
`getPluginDiagnostics`, `getDiagnostics` and `getNativeEditorRules` commands
and return the same v1 result as `POST /api/v1/commands` would. Some of them
scan plugins or start helpers, so they share the one HTTP command slot: while
a command or another of these reads is in flight they answer 429.
Changes still go through `/commands`.

The mixer resource also reports `preserveBrowserOutputs` and `browserTabs`
as documented in [browser tabs](api.md#browser-tabs). A tab's `title` is empty
unless it is capturing or has an assigned channel; its origin remains
available for selection. Title-only updates are coalesced on a one-second
timer, and unchanged tab lists do not generate state events. Routing and
tab lifecycle changes are immediate. Use
`{"cmd":"setPreserveBrowserOutputs","value":true}` to preserve browser-selected
outputs. While it is on, `assignApp` and `assignStream` for a recognized
browser return success and save the assignment without moving its audio.
Non-browser streams sharing that identity are still moved. The held
browser's `outputChannelId` follows its current target on each stream sweep.
Then use `assignBrowserTab` with a connected tab's `session`, `tabId`,
and expected `site`, an application `channel` or `null`, and boolean `remember`.
The required `site` must still match the tab's current canonical origin;
a stale choice returns HTTP 400 before changing the tab or saving anything.
A remembered choice is written before success is returned. `channel:null, remember:true`
forgets the website default; `remember:false` keeps that default and changes
only the tab. `activateBrowserTab` with the same `session`, `tabId` and
`site` asks the companion to bring a tab with a channel forward and start
routing it. Success means the request reached the companion; the tab's
`capturing`, `activation` and `error` in later states say what the browser
did, since it may need the user to press Activate there. Companion registration and metadata publication,
`getBrowserSession` and `syncBrowserTabs`, require an authenticated WebSocket
because its lifetime owns the runtime tabs. They are refused over HTTP.

For example, selecting a Teams tab on Voice Chat while remembering that website:

```json
{"cmd":"assignBrowserTab","session":"e3b0c44298fc4c14b9afbf4c8996fb92","tabId":42,"site":"https://teams.microsoft.com","channel":"voicechat","remember":true}
```

Both WebSocket paths require the existing first-message authentication:
`{"cmd":"auth","token":"..."}`. They send no state before authentication.
HTTP's Bearer header does not replace that WebSocket exchange. Ping, close,
message deadlines and command budgets remain the same on both socket paths.

Commands use the names and fields in [the WebSocket API](api.md), including
mix-to-output feed commands, which accept any existing mix or a sum of distinct
mix ids. For example,
`{"cmd":"setMonitorFeed","device":"alsa_output.headset","mix":"monitor+chat"}`
makes an already selected headset hear Monitor A and Chat summed at unity.
An output is selected with `setMonitorOutputs` before setting its feed.
For example, `{"cmd":"setMonitorOutputs","devices":["alsa_output.headset","alsa_output.speakers"]}`
selects both devices. Give the speakers `monitor` and leave the headset on
`monitor+chat`: Monitor A now feeds both outputs, while Chat also feeds the
headset. An empty sum is invalid; remove the device from `devices` to
stop its monitor feed. Feeds are saved in mixer settings and profiles;
`monitorFeeds` in the mixer state holds per-output exceptions from the
default monitor mix. The Pro's own jacks share one feed even when listed
as separate selected outputs.
`{"cmd":"setChannelVolume","channel":"game","value":0.5}` sets the Game
channel's own volume, which scales every send of the channel at once, and
`GET /api/v1/channels/game` reads it back as `volume`. Every channel takes
one, `xlr1`, `xlr2` and `aux` included.

A capture channel reports `microphone` and `chainWidth` through
`GET /api/v1/channels/{id}`. For a mono capture channel named `brio`,
`{"cmd":"setInserts","channel":"brio","inserts":[{"id":"compressor","kind":"lv2","plugin":"http://lsp-plug.in/plugins/lv2/compressor_mono"}]}`
sent to `POST /api/v1/commands` installs an LSP mono compressor when that
plugin is installed. `GET /api/v1/inserts/brio` reads its chain back.
The binding fixes the width even when the source is unplugged; a stereo
capture input needs a plugin that fits two channels.

The [microphone EQ](api.md#microphone-eq) takes four commands.
`{"cmd":"setEqBand","channel":"xlr1","band":2,"frequency":250,"gain":-3.5,"q":1.4}`
moves XLR 1's second band, `{"cmd":"setEqEnabled","channel":"xlr1","value":true}`
switches its EQ on, and `setEq` replaces the whole EQ in one call, for a
preset:
`{"cmd":"setEq","channel":"xlr1","eq":{"enabled":true,"bands":[{"enabled":true,"type":"lowCut","frequency":80,"gain":0,"q":0.7047},{"enabled":true,"type":"peak","frequency":250,"gain":-3.5,"q":1.4},{"enabled":true,"type":"peak","frequency":3500,"gain":2.5,"q":1},{"enabled":false,"type":"highCut","frequency":15049,"gain":0,"q":0.7047}]}}`.
`GET /api/v1/channels/xlr1` reads the EQ back as `eq`, with every value as
the daemon snapped it, and `GET /api/v1/mixer` carries `eqLimits`. On a
Wave XLR MK.2 or XLR Dock MK.2 from firmware 1.0.15, and on a Wave XLR Pro
from firmware 1.0.14, the unit's own DSP runs the EQ, `eq.backend` reads `device` and `eq.device` names the unit;
`{"cmd":"setEqProcessing","channel":"xlr1","value":"software"}` moves XLR 1's
EQ to this computer and `"value":"device"` back to the unit (see
[where the EQ runs](api.md#where-the-eq-runs)).
[Ducking](api.md#ducking) is one setup on any device:
`{"cmd":"setDucking","value":{"enabled":true,"channels":["music","game"],"mixes":["monitor"],"amount":"high"}}`
lowers Music and Game in Monitor A by 24 dB while XLR 1 is loud. The fields
it leaves out keep their values. `GET /api/v1/state` reads the setup back as
`ducking`, with `backend` saying whether the unit or this computer runs it.
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

An unreadable profile directory or recall marker is reported in the state's
`warning`, not as an HTTP failure. Device and mixer state remain available;
unavailable profile metadata uses the empty/null values documented in
[the state contract](api.md).

Error status codes: 400 a body that is not valid UTF-8, or a plain request
on the events route without a WebSocket upgrade; 401 missing/wrong token;
403 foreign Origin; 404 unknown resource id; 408 body-read deadline; 413
body over 64 KiB; 415 wrong Content-Type; 429 budget exhausted, or another
HTTP command or command-backed read in flight; 503 mixer resource while the
mixer is off. Chunked bodies have the same 64 KiB cap and five-second
deadline. One HTTP command or command-backed read runs at a time, with no
waiting queue; snapshot reads answer while a command runs. Every
authenticated HTTP request, read or write, takes from one budget shared by
every HTTP caller, the size of a socket's own (bursts of 300, a sustained
100 per second).
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

`addPluginSearchPath` and `removePluginSearchPath` take `kind` and `path`
through the same `POST /api/v1/commands` and answer with `pluginInstall`;
check its `ok`, since a refused folder or a failed save is reported there.
The [search path contract](api.md#plugin-search-paths) has the limits.

`addWindowsPluginFolder` and `removeWindowsPluginFolder` also use
`POST /api/v1/commands`, with an absolute `path`. They return a `pluginInstall`
message after refreshing the catalogue. Check that message's `ok` and
`message`, not the HTTP status alone: registry or wrapper-cleanup failures are
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
rejects blocked editors over HTTP as it does over WebSocket.

The [OpenAPI document](openapi-v1.json) describes the HTTP endpoints. Restarting
the daemon rotates its per-session token once the new instance listens;
clients must reread it.

For `setEnforcedDefaults`, `sink: "@monitor"` follows the first selected
monitor output as the system playback device. The response state retains
that value; see [the command contract](api.md) for resolution and volume
synchronization.

XLR 1 and XLR 2 can each take one port of any external capture source:
`{"cmd":"setInputBinding","channel":"xlr2","source":"alsa_input.usb-headset","captureMonoChannel":0}`.
`{"cmd":"setInputBinding","channel":"xlr2","source":null}` returns the strip
to the interface. The reply follows the saved change. Read each channel's
`binding`, `bindingConnected`, `bindingWarning` and `unitInput` from
`/api/v1/mixer` or `/api/v1/state`. A binding never stops the daemon
driving the interface. A source of another supported Elgato
interface is accepted; enable that unit with `setWaveInterfaceEnabled` to
have the strip's `unitInput` name it. See [input bindings](api.md#input-bindings)
for the interface, missing-source and profile behaviour. While no interface
is connected, `GET /api/v1/profiles` lists the `generic` profiles, which hold
the mixer scene only.

Capture channels use the same command endpoint:
`{"cmd":"createCaptureChannel","name":"Headset mic","source":"alsa_input.usb-headset","capturePair":0}`.
The source must be present. Success is returned after the layout is saved.
`captureMonoChannel` (0 to 63, with `capturePair` 0) takes one port of the
source for both sides.
State channel entries expose `captureSource`, `capturePair`, `captureMonoChannel` and `captureConnected`;
a disconnected source retains its binding and reconnects when it returns.
`{"cmd":"setCaptureSource","channel":"headset-mic","source":"alsa_input.usb-replacement","captureMonoChannel":0}`
rebinds that capture input without replacing its sends, volume, inserts or
group. It mutes its monitor sends and ends Sound Check. A width change needs
an empty chain. The reply follows the saved binding; unlike creation, the
source may be offline. See [capture inputs](mixer-layout.md#capture-inputs).

`{"cmd":"routeFocusedApp","channel":"music"}` uses the same focused-application
routing as PC and OpenDeck keys. Enable **Desktop keys** in the running UI.
KDE Plasma supplies the focused process; GLib's `gdbus` must be installed.
An unavailable desktop service, absent audio client or ambiguous identity
returns the normal command error response and does not select a guessed app.

Output keys use the same authenticated command endpoint:
`{"cmd":"adjustOutputVolume","value":-0.05}` lowers the current system output
by five percentage points; an optional `device` binds an exact output name.
`{"cmd":"setOutputDeviceVolume","value":1.0}` sets that output to 100%,
`value` from 0 to 1.5 on the desktop scale.
`{"cmd":"toggleOutputMute"}` toggles the current output's mute; on a selected
physical monitor output it toggles the whole monitor selection's pause, on a
monitor mix sink that mix.
`{"cmd":"setMonitorMuted","value":true}` pauses all selected monitor outputs
without changing mix or channel mutes. Recording and streaming continue by
default.
Selected physical sinks mirror the mute; resume restores their previous native
flags. An explicit `{"cmd":"setMixMuted","mix":"monitor","value":false}`
during a pause opens only that monitor, commits the other monitor mixes as
muted, and unmutes its assigned Linux sinks. Any open member of a shared output
feed keeps that device open; the Pro's independent Aux return keeps its shared
sink open while the selected jack controls follow monitoring.
Use `false` to resume. The `/mixer` resource reports the current `monitorMuted`;
it is saved with mixer settings and left unchanged by profile recall.
`{"cmd":"toggleMonitorMuted"}` toggles that pause atomically, without a
`value`. Use it for keys and repeated presses so each command toggles the
current state without depending on an earlier state response.
`{"cmd":"setMonitorMuteMicrophones","value":true}` opts microphone sends into
the same pause. The XLR/Mic strips and mono capture microphones then stop
feeding OpenXLR's mixes while `monitorMuted` is true. Other application audio,
stereo capture and Aux remain unchanged. This does not mute applications
recording the physical microphone directly. The `/mixer` resource reports the
saved `monitorMuteMicrophones` option and `microphonePausePending`; a pending
transition is not confirmed silence. The option is machine state, outside
profiles. Disabling it while paused resumes microphone sends only. Sound Check
stops and discards its take when microphones pause, and cannot start a new
recording until they resume.
`{"cmd":"setMainOutput","device":"alsa_output.usb-headset"}` selects and enforces
that output. `@monitor` follows the selected monitor output. Limits, rejected
targets and linked-monitor behavior match the [WebSocket contract](api.md).
These commands require the daemon but do not require a running UI or KDE.
