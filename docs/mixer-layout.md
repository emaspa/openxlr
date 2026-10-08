# Saved mixer layout

The daemon reads the layout from `mixer.json` before building its graph.
Stop the daemon before editing this file manually: while running, its normal
settings saves overwrite the file with the live configuration.

`userChannels` is an ordered list of application and capture channels and
`userMixes` an ordered list of user mixes. Each entry has a stable `id` and
a display `name`; a mix also has a `kind`, `virtualMic` (published as a
capture device) or `monitor` (an output mix with no capture device). A mix
without `kind` is a virtual microphone, which is what older files hold. For
example:

```json
{
  "userChannels": [{"id": "podcast", "name": "Interview"}],
  "userMixes": [{"id": "recording", "name": "Recording"},
                {"id": "headphones", "name": "Headphones", "kind": "monitor"}]
}
```

These are fields in the existing settings object; retain its other fields when
editing. Missing or null lists keep the legacy defaults. A single invalid entry
is dropped and logged with its path, and the rest of the file still applies; a
file that cannot be parsed at all is copied to `mixer.json.corrupt` before the
next save replaces it. An empty mix list removes every user mix. An empty
application list falls back to System so incoming applications have a
destination.

Hardware inputs, Monitor A (`monitor`), Monitor B (`monitor2`) and Aux
(`auxout`) remain structural. The `ignore` application target is reserved.
Invalid or duplicate entries are ignored; ids compare without regard to case,
an entry that repeats a structural id is dropped, and so is a mix of any
other kind. IDs contain at most 36 lowercase ASCII letters, digits,
underscores or hyphens, beginning with a letter. Names contain 1 to 60
printable characters and are trimmed. At most 32 editable channels and 16
user mixes, both kinds counted together, are restored. A user mix with no
saved send levels starts with every send muted, as a mix created while the
daemon runs does.

Node names derive from IDs, not labels. The list order survives a settings
save and restart. Clients draw the channels and mixes in this order, and the
order decides nothing else. An application whose channel no longer exists
goes to the fallback application channel: `system`, or when `system` has
been deleted, the application channel whose id sorts first. The fallback is
never a hardware input or a capture channel and never depends on the list
order, so reordering moves no application. Ignored applications stay
ignored. Existing monitor-feed settings and profile semantics are unchanged.

## Appearance

`appearance` maps `channel:<id>` or `mix:<id>` to how that item is drawn:

```json
{
  "appearance": {
    "channel:music": {"icon": "♫", "colour": "#2E9BFF", "hidden": false},
    "channel:sfx": {"icon": "", "hidden": true},
    "mix:stream": {"icon": "◆", "colour": "#FF7A00"}
  }
}
```

`icon` is empty or one of ● ♪ ♫ ✦ ◆ ▶ ◉. `colour` is `#RRGGBB`; absent or
null keeps the skin's colours. `hidden` leaves a channel's strip out of the
window's and the terminal mixer's full mixer; the channel keeps its sends,
meter, applications and audio, and stays in the layout editor, the
application pickers and the Stream Deck targets. A mix cannot be hidden.
An entry for an id the layout does not hold, a key of more than 44
characters, or a value outside these rules is dropped and logged. An entry
equal to the default is not written. Deleting a channel or a mix deletes
its entry. Appearance belongs to the layout, so profiles neither save nor
recall it.

## Other fields

The rest of the settings object is the live state the daemon writes back on
every save. Every field is optional on read; an absent one is empty or off.

| Field | Holds |
|---|---|
| `mixVolumes` | mix id to master level |
| `mixMuted` | mix ids whose master is muted |
| `levels` | `channel\|mix` to send level |
| `channelMuted` | `channel\|mix` cells whose send is muted |
| `monitorOutputs` | the selected outputs, in order; the first one is what `@monitor` resolves to |
| `monitorOutput` | the older single selection, read only when `monitorOutputs` is empty |
| `monitorFeeds` | output to feed, see [Output feeds](#output-feeds) |
| `auxPortEnabled` | whether the Aux mix reaches the USB Aux port; when absent, a saved `#usbaux` monitor selection turns it on once and is dropped from the selection |
| `enforcedDefaultSink` | the output held as the system default, or `@monitor`; `setEnforcedDefaults` and `setMainOutput` write it |
| `enforcedDefaultSource` | the source held as the system default |
| `appOverrides` | application identity to remembered channel |
| `knownApps` | `{identity, label, channelId}` for every application seen |
| `lowCutHz` | the software low cut, 0, 80 or 120 |
| `softClipGuard` | the software ClipGuard |
| `inserts` | insert chains by `xlr1`, `xlr2` or `mix:<id>` |
| `exclusiveGroups` | exclusive channel groups, see [Exclusive groups](#exclusive-groups) |

A null field or a null entry inside one is dropped and logged; so is a
non-finite number in `mixVolumes`, `levels` or an insert's `params`. A
`monitorFeeds` entry for an output that is not selected is removed the next
time the selection is written.

## Editing while running

The window's Edit layout button, and the commands below over the API,
edit the running graph. Adding or reordering preserves existing routes;
renaming an application channel can cause a short gap on that channel,
and deleting a virtual microphone disconnects its recorders. A command
whose ids, names or lists fail validation is refused before anything
changes. Each one is then saved before it is acknowledged, with the
whole settings object, so a fader move pending in the debounced save is
on disk with it and an earlier save error is cleared; a failed save
restores the previous layout and reports an error. A rename to the current
name is acknowledged without a save. Ordinary fader saves keep their
debounced, retried behaviour.

- `createChannel {name}` adds an application channel. Only its sink is
  loaded; it starts muted in every mix. The sink's sends into the mixes
  must appear within three seconds, or the sink is unloaded again and
  the command fails: a send that showed up later would carry PipeWire's
  defaults, full level and unmuted, instead of the stored fader.
- `renameChannel {channel, name}` saves the name and reloads that channel's
  playback device under it, so desktop applets show the new name at once.
  PipeWire parks the streams that were playing into it on the default
  output for the moment the sink is away, and the daemon puts them back.
  A capture channel's sink is hidden, so its name changes without a reload.
- `deleteChannel {channel}` removes an application or capture channel. Apps
  routed to it, remembered assignments included, and whatever is playing
  into its sink at that moment move to the fallback application channel
  (`system`, or the remaining application channel whose id sorts first);
  then its sink is unloaded, and a capture channel's link to its source
  with it. The last application channel stays.
- `createMix {name, kind?}` adds a user mix after the existing ones, ahead
  of Aux. Without `kind`, or with `kind: "virtualMic"`, it is a virtual
  microphone. With `kind: "monitor"` it is a monitor mix: one sink and no
  capture device, for an output to follow. Its master is the sink's own
  volume, so desktop volume controls move it and it goes to 150% like
  Monitor A and Monitor B. The channel sinks feed the mix sinks by name
  pattern, so every channel grows a send into the new mix by itself, muted
  before a virtual microphone's capture device is published. If a
  channel's send has not appeared within three seconds the mix is removed
  again and the command fails, for the same reason.
- `renameMix {mix, name}` changes the name of a user mix in OpenXLR only.
  Reloading its sink or capture device would drop every app using it onto
  another device, so the PipeWire description keeps the old name until the
  daemon restarts; the state carries `renamedSinceStart` and the window
  shows a restart hint.
- `deleteMix {mix}` removes a user mix with its sends and inserts, and a
  virtual microphone's capture device with it; anything recording from that
  device loses it. Outputs listening to the mix keep the other mixes in
  their feed, or return to the first monitor mix if none remain. An
  enforced default source that was this microphone, or an enforced default
  sink that was this mix, is cleared, and the desktop chooses the default.
  All of that is part of the saved deletion and rolls back with it when
  saving fails.
- `setExclusiveGroup {group?, name, channels}` creates an exclusive group
  when `group` is absent, or replaces the name and members of a known one.
  `deleteExclusiveGroup {group}` removes one and leaves every send's mute
  as it is. See [Exclusive groups](#exclusive-groups).
- `setLayoutOrder {channels, mixes}` reorders the editable ids. Supply every
  application and capture channel id and every user mix id exactly
  once; hardware inputs, Monitor A/B and Aux keep their positions. No node
  changes. Open windows apply the published order to channel tiles, mix
  controls and send rows while retaining the existing controls and their
  values. The layout editor's arrows, a tile dragged in the window's
  Arrange mode and Ctrl+Left/Right in the terminal mixer all send this
  command with both complete lists; a drag places the item before or
  after the tile it was dropped on.
- `setLayoutAppearance {channel | mix, appearance}` replaces one item's
  `icon`, `colour` and `hidden` as described under [Appearance](#appearance),
  hardware inputs and Monitor A/B and Aux included. No node changes.

Every added channel or mix costs pipewire-pulse a few dozen open files;
the daemon refuses an addition the server has no room for, and the packages
raise the server's limit ([manual: open-file limit](manual.md#open-files)).

Ids are generated from names: lowercase letters and digits with hyphens,
starting with a letter, at most 28 characters, made unique against the
existing ids and the reserved ones with a numeric suffix (`-2`, `-3`). A
name that leaves nothing usable becomes `channel` or `mix`, and one that
starts with a digit gets that word in front. An id never changes
afterwards, so node names, profiles and Stream Deck keys survive a rename.

Other manual changes, including external PipeWire descriptions, take effect
at startup.

## Capture inputs

`createCaptureChannel {name, source, capturePair}` adds a channel from an
external PipeWire capture source. `source` is its exact `node.name`, at most
256 printable characters. `capturePair` is a zero-based stereo pair from 0 to
31 and defaults to 0. Mono sources feed both sides. A missing pair stays
silent. The source must be present when creating the channel. OpenXLR's own
devices and sink monitor sources are not capture inputs, to avoid direct
feedback: a name starting with `OpenXLR` or ending in `.monitor` is refused.

Capture channels share the editable-channel limit of 32. They start muted in
all mixes. Their hidden combine sink supports the existing sends, faders,
meters and scene recall. They are excluded from application-routing choices.
At least one application channel must remain; restoring a layout containing
only capture inputs reserves a slot for a System application channel. Its
application identity is retained even if a discarded capture entry had the
same id and display name.

Bindings are stored in `userChannels`, for example
`{"id":"second-mic","name":"Second microphone","captureSource":"alsa_input.usb-headset","capturePair":0}`.
An entry without `captureSource` is an application channel, as in older files;
one that carries a `capturePair` other than 0 without a source is dropped.
Invalid capture bindings are discarded, not converted into application channels.
The binding belongs to the layout, not a profile. Rename, reorder and delete
use the existing channel commands. Renaming leaves the capture graph running.
To use a different source or pair, create a new capture channel and remove the
old one.

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

## Exclusive groups

An exclusive group is a named list of channels of which only one is heard.
`exclusiveGroups` holds them:

```json
"exclusiveGroups": [
  {"id": "microphones", "name": "Microphones", "channels": ["xlr1", "headset"]}
]
```

A member is open while any of its sends is unmuted. Unmuting a member's send
in any mix mutes every send of the other members, in every mix, before the
new send opens; send levels do not change. If one of those mutes cannot be
written yet, the new send stays silent until it has been. Muting the open
member leaves the whole group muted.

A group has 2 to 36 distinct members, any channels, hardware inputs
included, and a channel is in one group at most. At most 16 groups are
restored. The id follows the channel id rules and is generated from the
name the same way, with `group` as the fallback word; the member order is
the order `cycleExclusiveGroup` steps through. An entry that breaks these
rules is dropped and logged, a member whose channel no longer exists is
removed, and a group left with fewer than two members is removed too.
Deleting a channel takes it out of its group the same way.

The daemon never picks a member for you. If more than one member is open
when a group is saved, when the settings file is read or when a profile is
recalled, it mutes every member of that group. Profiles store the send
mutes and not the groups, so a profile recalls into the groups that exist.

On a Wave XLR Pro, XLR 1 in a group does not use the interface's
zero-latency path to the headphone jacks. That path bypasses the mixer, so
the group could not mute it; the microphone reaches the jacks through its
Monitor send instead, with the latency of the PipeWire graph.

## Output feeds

`monitorFeeds` records the mixes included in each selected output, as one mix
id or several joined with `+`. Every mix in a feed reaches the output at
unity, as a direct port link from the mix; a blend at other levels is a mix
of its own, with the sends set there. An output without an entry, or with
an empty or unknown one, hears the first monitor mix. Deleting a mix drops
it from every feed. The jacks of one interface share a return bus, so they
are written with one feed.

`outputRoutes`, the per-route level list that 0.1.40 and 0.1.41 wrote, is
ignored on read, in this file and in profiles.

If a channel or mix deletion cannot be saved, its previous routing settings
and any pending volume or mute writes are restored together. The normal
reconciliation keeps retrying those writes when PipeWire becomes available.
