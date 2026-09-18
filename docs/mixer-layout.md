# Saved mixer layout

The daemon reads the layout from `mixer.json` before building its graph.
Stop the daemon before editing this file manually: while running, its normal
settings saves overwrite the file with the live configuration.

`userChannels` is an ordered list of application and capture channels and `userMixes` an
ordered list of virtual microphones. Each entry has a stable `id` and a display
`name`. For example:

```json
{
  "userChannels": [{"id": "podcast", "name": "Interview"}],
  "userMixes": [{"id": "recording", "name": "Recording"}]
}
```

These are fields in the existing settings object; retain its other fields when
editing. Missing or null lists keep the legacy defaults. A single invalid entry
is dropped and logged with its path, and the rest of the file still applies; a
file that cannot be parsed at all is copied to `mixer.json.corrupt` before the
next save replaces it. An empty mix list
removes the editable virtual microphones. An empty application list falls back
to System so incoming applications have a destination.

Hardware inputs, Monitor A (`monitor`), Monitor B (`monitor2`) and Aux
(`auxout`) remain structural. The `ignore` application target is reserved.
Invalid or duplicate entries are ignored. IDs contain at most 36 lowercase
ASCII letters, digits, underscores or hyphens, beginning with a letter. Names
contain 1 to 60 printable characters. At most 32 editable channels and 16
virtual microphones are restored.

Node names derive from IDs, not labels. The list order survives a settings
save and restart. Removed application destinations fall back to the first
application channel, never a hardware input; ignored applications stay ignored.
Existing monitor-feed settings and profile semantics are unchanged.

## Editing while running

The window's Edit layout button, and the commands below over the API,
edit the running graph. Adding or reordering preserves existing routes;
renaming an application channel can cause a short gap on that channel,
and deleting a virtual microphone disconnects its recorders. Each one is saved before it is
acknowledged; a failed save restores the previous layout and reports an
error. Ordinary fader saves keep their debounced, retried behaviour.

- `createChannel {name}` adds an application channel. Only its sink is
  loaded; it starts muted in every mix. The sink's sends into the mixes
  must appear within three seconds, or the sink is unloaded again and
  the command fails: a send that showed up later would carry PipeWire's
  defaults, full level and unmuted, instead of the stored fader.
- `renameChannel {channel, name}` saves the name and reloads that channel's
  playback device under it, so desktop applets show the new name at once.
  PipeWire parks the streams that were playing into it on the default
  output for the moment the sink is away, and the daemon puts them back.
- `deleteChannel {channel}` moves the apps routed to it, remembered
  assignments included, to the first remaining application channel and
  unloads its sink. The last application channel stays.
- `createMix {name}` adds a virtual microphone. The channel sinks feed the
  mix sinks by name pattern, so every channel grows a send into the new mix
  by itself, muted before the capture device is published. If a channel's
  send has not appeared within three seconds the mix is removed again and
  the command fails, for the same reason.
- `renameMix {mix, name}` changes the name in OpenXLR only. Reloading the
  capture device would drop every app recording from it onto another
  source, so its PipeWire description keeps the old name until the daemon
  restarts; the state carries `renamedSinceStart` and the window shows a
  restart hint.
- `deleteMix {mix}` removes the virtual microphone, its sends, inserts and
  capture device. Anything recording from it loses the device.
  Outputs listening to that mix keep the other mixes in their feed, or return
  to the first monitor mix if none remain. Feed changes are part of the saved
  deletion and roll back with it when saving fails.
- `setLayoutOrder {channels, mixes}` reorders the editable ids. Supply every
  application-channel id and every virtual-microphone id exactly once;
  hardware inputs, Monitor A/B and Aux keep their positions. No node changes.
  Open windows apply the published order to channel tiles, mix controls and
  send rows while retaining the existing controls and their values.

Every added channel or mix costs pipewire-pulse a few dozen open files;
the daemon refuses an addition the server has no room for, and the packages
raise the server's limit ([manual: open-file limit](manual.md#open-files)).

Ids are generated from names (lowercase letters, digits and hyphens,
starting with a letter, unique with a numeric suffix) and never change
afterwards, so node names, profiles and Stream Deck keys survive a rename.

Other manual changes, including external PipeWire descriptions, take effect
at startup.

## Capture inputs

`createCaptureChannel {name, source, capturePair}` adds a channel from an external
PipeWire capture source. `source` is its exact `node.name`, at most 256 printable
characters. `capturePair` is a zero-based stereo pair from 0 to 31 and defaults
to 0. Mono sources feed both sides. A missing pair stays silent. The source must
be present when creating the channel. OpenXLR's own devices and sink monitor
sources are not capture inputs, to avoid direct feedback.

Capture channels share the editable-channel limit of 32. They start muted in
all mixes. Their hidden combine sink supports the existing sends, faders,
meters and scene recall. They are excluded from application-routing choices.
At least one application channel must remain; restoring a layout containing
only capture inputs reserves a slot for a System application channel. Its
application identity is retained even if a discarded capture entry had the
same id and display name.

Bindings are stored in `userChannels`, for example
`{"id":"second-mic","name":"Second microphone","captureSource":"alsa_input.usb-headset","capturePair":0}`.
An entry without `captureSource` is an application channel, as in older files.
Invalid capture bindings are discarded, not converted into application channels.
The binding belongs to the layout, not a profile. Rename, reorder and delete
use the existing channel commands. Renaming leaves the capture graph running.
To use a different source or pair, create a new capture channel and remove the old one.

Disconnected sources retain their exact binding and faders. They reconnect
when that node and pair return, without falling back to another microphone.
Both sides of the channel must connect before the input reports connected.
If only one link succeeds, it is removed and the next sweep retries the
whole connection, including the two links used for a mono source.
Multiple interfaces can therefore supply audio simultaneously, independently
of the interface selected for hardware controls. Hardware controls and the
built-in input DSP still belong to the selected Wave interface. Capture inputs
can feed mix insert chains; per-input insert hosting remains limited to the
existing XLR channels.

## Output routes

`monitorFeeds` records the mixes included in each selected output. The output
matrix can store an empty string for a deliberately silent output; it stays
silent across recalls and unrelated mix deletion. Deleting its last included
mix retains the existing fallback to the primary monitor mix.

`outputRoutes` stores gain exceptions as `{device, mix, level}` entries. A
selected feed absent from this list uses 100%. Levels are positive and at
most 1; zero is represented by removing the mix from `monitorFeeds`.
The state, mixer settings and profile scenes carry this list. Shared Pro
jack routes use the canonical `device#bus` key. Removing an output or mix
removes its gains; a failed mix-deletion save restores them. A legacy scene
that recalls output selection or feeds without gains uses unity gains.

`setOutputRoute` changes one route. Fader saves retain the normal debounced
and retried persistence behaviour. A route below unity uses a hidden
PipeWire gain sink, created muted before it is connected. Unity routes use
direct links until they need a gain node. Existing gain nodes update in
place, and unrelated outputs keep their links. Gain-node creation checks
pipewire-pulse's open-file headroom and adds no helper process per route.

## Mixer presentation

Use **Edit layout**, **Appearance** to select an icon and an optional `#RRGGBB`
colour for any channel or mix. Clear the colour to follow the current skin.
The channel's **Hide** option removes its full-size strip, not its sends,
meters, application assignments or audio connections. Hidden channels remain
in Edit layout, application choices and Stream Deck actions.

The up and down buttons move any display item, including hardware channels,
Monitor A, Monitor B and Aux. Routing priority, the first default application
channel and stable IDs do not change. Icons and colours reach the corresponding
Stream Deck keys; an explicit icon chosen on a key takes precedence. Mute and
offline indicators retain their status colours.

**Compact** above the mixer shows one selected channel. Its selector includes
hidden channels, so they can still be adjusted. If a selected channel is removed
or unavailable on the active device, the window shows an available channel;
with no available channels it shows none. Turning Compact off restores the
full layout. The compact preference and selected channel are local window
preferences in `ui.json`.

Display metadata lives in the `appearance` map in `mixer.json`, keyed by
`channel:<id>` or `mix:<id>`, with `icon`, `colour`, `hidden` and optional `order`.
Missing entries retain the existing appearance and layout order. Equal or
missing positions use the original layout order. Deleted items lose their
metadata. This is global layout configuration, not part of an audio profile.
Saving a failed edit restores the previous presentation. No PipeWire nodes
are rebuilt by these edits. `setLayoutOrder` remains the legacy editable-only
layout command; `setDisplayOrder` overrides its visual order for all items.
