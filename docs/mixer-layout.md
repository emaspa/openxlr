# Saved mixer layout

The daemon reads the layout from `mixer.json` before building its graph.
Stop the daemon before editing this file manually: while running, its normal
settings saves overwrite the file with the live configuration.

`userChannels` is an ordered list of application and capture channels and
`mixes` an ordered list of every editable mix, including Monitor A and B.
Each entry has a stable `id` and a display `name`; a mix also has a `kind`,
`virtualMic` (published as a capture device) or `monitor` (an output mix with
no capture device). A mix without `kind` is a virtual microphone. For example:

```json
{
  "userChannels": [{"id": "podcast", "name": "Interview"}],
  "mixes": [{"id": "headphones", "name": "Headphones", "kind": "monitor"},
            {"id": "recording", "name": "Recording"}]
}
```

These are fields in the existing settings object; retain its other fields when
editing. Missing or null `mixes` reads the older `userMixes` list, with Monitor A
and B kept ahead of it; a missing or null legacy list also keeps Stream and Chat.
New daemon saves write only `mixes`, so removing or renaming a built-in monitor
survives a restart. When both fields exist, `mixes` wins.

While monitoring is paused, adding a monitor keeps its master muted. Deleting
one removes only its id from the paused listening selection. The pause clears
if its last previously open monitor is deleted; remaining individual mutes stay
closed. If a paused output route refuses removal, deletion is refused before
saving, with the layout and pause retained. Renaming changes no id or mute. The pause, mutes and layout edits are
saved together and restored after a daemon restart. Clients resolve saved output
feeds by surviving mix ids, ignoring whitespace and removed ids. An empty or
entirely stale feed follows the default monitor, just as the daemon does.

A single invalid entry is dropped and logged with its path, and the rest of the
file still applies; a file that cannot be parsed at all is copied to
`mixer.json.corrupt` before the next save replaces it. An empty or malformed
explicit mix list with no monitor is repaired to one Monitor A with every send
muted, ignoring stale saved levels for that synthesized monitor. At the size
limit the last accepted virtual microphone is dropped to make room. An empty
application list falls back to System so incoming applications have a destination.

The settings must be a regular file. Pipes, devices and sockets are refused
without reading them or replacing an earlier `mixer.json.corrupt` backup.

Only hardware inputs and Aux (`auxout`) remain structural. Monitor A
(`monitor`) and Monitor B (`monitor2`) can be renamed, moved or removed;
at least one monitor mix must remain. The `ignore` application target is reserved.
Invalid or duplicate entries are ignored; ids compare without regard to case,
an entry that repeats a structural id is dropped, and so is a mix of any
other kind. IDs contain at most 36 lowercase ASCII letters, digits,
underscores or hyphens, beginning with a letter. Names contain 1 to 60
printable characters and are trimmed. At most 32 editable channels and 18
editable mixes, including Monitor A/B and both mix kinds, are restored.
This keeps the same maximum graph size as the older 16 user mixes plus two
fixed monitors. A virtual microphone cannot reuse the ids `monitor` or `monitor2`. A mix with no
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

This file owns the channels and mixes: which exist, their names, their
order, their routing and their per-item appearance. It says nothing about
how the desktop UI arranges its sections. Where the UI's header
and five sections go, whether a section starts folded or hidden, and the
order of the header's actions come from the skin's optional `layout`
([skins.md](skins.md#layout)), and the moves a user makes with Arrange,
including the hidden sections they show again, are UI preferences in
`ui.json`.
Choosing a skin never sends a layout command, and a channel or mix order
set here never moves a section. Under a skin that draws the submixer as
mix banks, the mix each bank shows is a UI preference in `ui.json`
too. Picking one never changes the order kept here, and the banks list
the channels in that order. Whether the submixer sits in the UI
or in a window of its own is a UI preference in `ui.json` as well, and so is the
record of which skins' pop-out defaults have applied; neither is part of
this file or of a profile.

The optional `inputBindings` field in `mixer.json` is machine wiring. It
maps a hardware strip to the source it is bound to:

```json
{
  "inputBindings": {
    "xlr1": {"source": "alsa_input.usb-046d_Logi_4K_Stream_Edition_C971DF30-03.analog-stereo", "pair": 0, "monoChannel": 1},
    "xlr2": {"source": "alsa_input.usb-headset", "pair": 0, "monoChannel": 0}
  }
}
```

`xlr1` and `xlr2` are the only keys; a strip left out, and Aux In always,
takes the interface's capture pair. `source` is the exact node name,
`pair` is 0 and `monoChannel` is the zero-based port fed to both sides of
the strip, 0 when absent. `unit` is present only for a source that belonged
to a supported Elgato interface when it was bound, as that unit's id
(`"unit": "0fd9:0070@1234567890abcdef"`, or the bare model id for a unit
that has none of its own); the daemon writes it, and while the exact source
is missing the strip follows that unit's renamed source. When the daemon
reads the file it drops an entry with another key, with a source or port the
command would refuse, or with a `unit` that is not such an id, and its log
names the entry. A binding does not change channel ids or order and is not
exported into profiles. A file written before this field existed holds
XLR 1's source as `"primaryInput": {"source": "...", "channel": 0}`; when
`inputBindings` is absent it is read as XLR 1's binding, and the next save
writes `inputBindings` only. `setInputBinding` changes the field live; see
[the API](api.md#input-bindings).

## Appearance

`appearance` maps `channel:<id>`, `mix:<id>`, `output:phones1` or
`output:phones2` to how that item is drawn:

```json
{
  "appearance": {
    "channel:music": {"icon": "music", "colour": "#2E9BFF", "hidden": false},
    "channel:sfx": {"icon": "", "hidden": true},
    "channel:game": {"icon": "none"},
    "mix:stream": {"icon": "◆", "colour": "#FF7A00"},
    "output:phones1": {"icon": "headset", "colour": "#2E9BFF"}
  }
}
```

`icon` is one of:

- empty or absent: the default for the item's kind, which the daemon
  publishes in the state as `defaultIcon` ([api.md](api.md)). XLR 1 and
  XLR 2 wear `mic` on a real XLR jack, `microphone` on a Wave:3's or
  Wave:1's capsule or another source, and `headset` on a Bluetooth
  headset's source (`bluez_input.`); Aux In wears `jack`, mono capture inputs `microphone`,
  `bluez_input.` capture sources `headset`, stereo capture pairs `camera`, the
  shipped application channels `game`, `music`, `browser`, `cog` (System),
  `chat` (Voice Chat) and `burst` (SFX), any other application channel
  `apps`, monitor mixes `headset`, Stream `broadcast`, Chat `people`, any
  other virtual microphone `podcast`, and the Aux port `jack`;
- `none`: no icon at all;
- one of the text glyphs ● ♪ ♫ ✦ ◆ ▶ ◉, drawn as text, as layouts from
  before the icon set keep them;
- an icon name: a lower-case letter followed by up to 31 lower-case
  letters, digits or hyphens. The built-in set is `mic`, `microphone`, `headset`,
  `speaker`, `jack`, `music`, `game`, `browser`, `chat`, `people`,
  `broadcast`, `camera`, `bell`, `star`, `globe`, `cog`, `film`, `voice`,
  `podcast`, `record`, `apps`, `burst` and `monitor`; a skin may add names
  of its own ([skins.md](skins.md#icons)).

The daemon checks a name's shape only, so a layout may keep the name of an
icon only one skin draws. A client that cannot draw a name draws the
default instead: the UI when it wears another skin, the terminal mixer
and the Stream Deck plugin always, unless the name is in the built-in set.
The terminal mixer draws each built-in icon as one text character.

`colour` is `#RRGGBB`; absent or null keeps the skin's colours. `hidden`
leaves a channel's strip out of the UI's and the terminal mixer's full
mixer; the channel keeps its sends, meter, applications and audio, and
stays in the layout editor, the application pickers and the Stream Deck
targets.

A mix takes `hidden` under the same rule, Monitor A, Monitor B, Stream,
Chat and Aux included. A hidden mix keeps its audio, its outputs and
monitor feeds, its sends, volume, mute and plugin chain; only its master
and its column leave the full mixer. The UI's classic strips drop its
master tile and the send rows into it. Mix banks leave it out of their
pickers. A mix matrix drops its column head and the cell under it in
every row, so the other columns stay lined up. The compact view draws
every mix, so it leaves a hidden mix out too. The mini view draws the one
mix chosen for it, hidden or not. The terminal mixer leaves
a hidden mix out of its Mixer section and keeps it in Matrix. A hidden mix
stays in the layout editor, in every output's feed picker and in the
Stream Deck targets. With every mix hidden the full mixer draws the
channels with no sends and the matrix has row heads and no columns. The
MONITOR and HEADPHONES cards are not mixes and never hide.

The UI's **Show hidden**, in the SUBMIXER header, draws hidden
channels and mixes in their places, faded to `Ox.Mixer.HiddenOpacity`
and fully usable. It is a UI preference in `ui.json`
(`showHiddenItems`); the layout and the profiles do not change.
The two headphone outputs are hardware, not channels: they take an icon
and a colour, drawn before their labels in the UI's HEADPHONES card,
default to `headset` and are never hidden.
A key of more than 44 characters or a value outside these rules is
dropped and logged; an entry for an id the layout does not hold is dropped
without a note. An entry
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
| `channelVolumes` | channel id to its own volume, 0 to 1, the hardware inputs included; an absent channel is at 100% |
| `channelMuted` | `channel\|mix` cells whose send is muted |
| `monitorOutputs` | the selected outputs, in order; the first one is what `@monitor` resolves to |
| `monitorOutput` | the older single selection, read only when `monitorOutputs` is empty |
| `monitorFeeds` | output to feed, see [Output feeds](#output-feeds) |
| `monitorMuted` | pause every selected monitor output without changing mix or channel mutes, levels or feeds; false when absent. Saved here only, and unchanged by profile recall |
| `monitorOutputMutes` | exact physical output name to its previous native mute flag while OpenXLR owns a monitor mute. Empty when absent; null and unselected entries are dropped. Saved here only, never in profiles, so pause/resume can restore native flags across daemon restarts |
| `auxPortEnabled` | whether the Aux mix reaches the USB Aux port; when absent, a saved `#usbaux` monitor selection turns it on once and is dropped from the selection |
| `enforcedDefaultSink` | the output held as the system default, or `@monitor`; `setEnforcedDefaults` and `setMainOutput` write it |
| `enforcedDefaultSource` | the source held as the system default |
| `appOverrides` | application identity to remembered channel |
| `knownApps` | `{identity, label, channelId}` for every application seen |
| `lowCutHz` | the software low cut, 0, 80 or 120 |
| `softClipGuard` | the software ClipGuard |
| `inserts` | insert chains by `xlr1`, `xlr2`, capture channel id or `mix:<id>` |
| `eq` | the [microphone EQ](api.md#microphone-eq) of `xlr1` and `xlr2`, by channel id |
| `eqProcessing` | channel id to `device` or `software`, where that microphone's EQ runs; machine wiring, never in a profile |
| `compensateMixLatency` | the latency compensation switch across mixes, see [api.md](api.md) |
| `exclusiveGroups` | exclusive channel groups, see [Exclusive groups](#exclusive-groups) |
| `ducking` | the [ducking](api.md#ducking) setup, `{enabled, key, channels, mixes, sensitivity, amount, speed, processing}`; absent takes the defaults, off, and the next save writes them. Unknown ids are dropped and an unknown preset or processing word is reset to its default, each with a note |

A null field or a null entry inside one is dropped and logged; so is a
non-finite number in `mixVolumes`, `levels`, `channelVolumes` or an insert's `params`. A
`monitorFeeds` entry for an output that is not selected is removed the next
time the selection is written.

## Editing while running

The UI's Edit layout button, and the commands below over the API,
edit the running graph. Adding or reordering preserves existing routes;
renaming an application channel can cause a short gap on that channel,
and deleting a virtual microphone disconnects its recorders. A command
whose ids, names or lists fail validation is refused before anything
changes. Each one is then saved before it is acknowledged, with the
whole settings object, so a fader move pending in the debounced save is
on disk with it and an earlier save error is cleared; a failed save
restores the previous layout and reports an error. A rename to the current
name also saves the pending settings, without changing any audio node.
Ordinary fader saves keep their
debounced, retried behaviour.

- `createChannel {name, icon?}` adds an application channel. Only its sink
  is loaded; it starts muted in every mix. `icon` takes the values listed
  under [Appearance](#appearance) and is saved with the channel in the same
  write; absent or empty leaves the default. The sink's sends into the mixes
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
  then its sink is unloaded, and a capture channel's links, insert hosts,
  EQ stage and session stop with it. Its insert key and its `eq` entry are removed from the saved settings. The last application channel stays.
- `createMix {name, kind?, icon?}` adds a user mix after the existing ones,
  ahead of Aux, with `icon` as `createChannel` takes it. Without `kind`, or with `kind: "virtualMic"`, it is a virtual
  microphone. With `kind: "monitor"` it is a monitor mix: one sink and no
  capture device, for an output to follow. Its master is the sink's own
  volume, so desktop volume controls move it and it goes to 150% like
  Monitor A and Monitor B. The channel sinks feed the mix sinks by name
  pattern, so every channel grows a send into the new mix by itself, muted
  before a virtual microphone's capture device is published. If a
  channel's send has not appeared within three seconds the mix is removed
  again and the command fails, for the same reason. Creating a monitor
  never changes an existing output's feed: an implicit feed is saved explicitly
  if the new monitor would become the default. A saved feed naming a removed
  mix keeps its surviving mixes or fallback when that id is created again.
  Removed built-in monitor ids
  remain reserved when generating a new mix id. A monitor created while
  monitoring is paused also starts with its master muted, so resuming keeps
  the listening selection from before the pause.
- `renameMix {mix, name}` changes the name of a user mix in OpenXLR only.
  Reloading its sink or capture device would drop every app using it onto
  another device, so the PipeWire description keeps the old name until the
  daemon restarts; the state carries `renamedSinceStart` and the UI
  shows a restart hint.
- `deleteMix {mix}` removes a user mix with its sends and inserts, and a
  virtual microphone's capture device with it; anything recording from that
  device loses it. Outputs listening to the mix keep the other mixes in
  their feed, or return to the default monitor mix if none remain. The last monitor mix
  cannot be deleted. An
  enforced default source that was this microphone, or an enforced default
  sink that was this mix, is cleared, and the desktop chooses the default.
  All of that is part of the saved deletion and rolls back with it when
  saving fails.
- `setExclusiveGroup {group?, name, channels}` creates an exclusive group
  when `group` is absent, or replaces the name and members of a known one.
  `deleteExclusiveGroup {group}` removes one and leaves every send's mute
  as it is. See [Exclusive groups](#exclusive-groups).
- `setLayoutOrder {channels, mixes}` reorders the editable ids. Supply every
  application and capture channel id and every monitor or virtual microphone
  mix id exactly once; only hardware inputs and Aux keep their positions. No node
  changes. Open windows apply the published order to channel tiles, mix
  controls and send rows while retaining the existing controls and their
  values. The layout editor's arrows, a tile dragged in the UI's
  Arrange mode and Ctrl+Left/Right in the terminal mixer all send this
  command with both complete lists; a drag places the item before or
  after the tile it was dropped on.
- `setLayoutAppearance {channel | mix | output, appearance}` replaces one
  item's `icon`, `colour` and `hidden` as described under
  [Appearance](#appearance), hardware inputs and Monitor A/B and Aux
  included; `output` is `phones1` or `phones2`. No node changes.

Every edit keeps the [ducking](api.md#ducking) setup in step in the same
save: a deleted channel leaves `ducking.channels`, a deleted mix leaves
`ducking.mixes`, and deleting the key channel turns ducking off and resets
the key to `xlr1`. A new channel or mix is not added to the setup; a new
channel starts muted in every mix anyway. A rename keeps the id, so the
setup is unchanged.

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

`createCaptureChannel {name, source, capturePair, captureMonoChannel}` adds
a channel from an external PipeWire capture source. `source` is its exact
`node.name`, at most 256 printable characters. `capturePair` is a zero-based
stereo pair from 0 to 31 and defaults to 0. Mono sources feed both sides.
Optional `captureMonoChannel`, 0 to 63 and only with pair 0, takes that one
port of a multichannel source and feeds it to both sides; the third port is
the Wave XLR Pro's second microphone. A missing pair or port stays silent.
The source must be present in the daemon's `devices` list when creating the
channel. After creating a source externally, wait for the daemon to list it;
separate PipeWire registry subscriptions can observe its arrival at different
times. OpenXLR's own devices and sink monitor sources are not capture inputs,
to avoid direct feedback: a name starting with `OpenXLR` or ending in
`.monitor` is refused.

Capture channels share the editable-channel limit of 32. They start muted in
all mixes. Their hidden combine sink supports the existing sends, faders,
meters and scene recall. They are excluded from application-routing choices.
At least one application channel must remain; restoring a layout containing
only capture inputs reserves a slot for a System application channel. Its
application identity is retained even if a discarded capture entry had the
same id and display name.

Bindings are stored in `userChannels`, for example
`{"id":"second-mic","name":"Second microphone","captureSource":"alsa_input.usb-headset","capturePair":0}`.
An entry may also carry `captureMonoChannel` and `captureUnit`.
`captureUnit` is null, an instance id such as `0fd9:0070@0123456789abcdef`,
or a bare model id such as `0fd9:0070`, validated by `SourceBinding.IsUnitId`.
The daemon writes it when creating a capture on a node marked with
`devices[].drivenUnit`. An existing capture gets it once, when its exact
source is first seen on an attached supported unit, and the layout is saved.
It is reported as the channel's `binding.unit`, not a separate state field.
An entry without `captureSource` is an application channel, as in older
files; one that carries a `capturePair` other than 0, `captureMonoChannel`
or `captureUnit` without a source is dropped.
Invalid capture bindings are discarded, not converted into application channels.
The binding belongs to the layout, not a profile. Rename, reorder and delete
use the existing channel commands. Renaming leaves the capture graph running.

`setCaptureSource {channel, source, capturePair?, captureMonoChannel?}` changes
that channel's source or ports in place. It accepts only a capture input id,
with the same source and port limits as creation, and may name an offline
source. An omitted pair is 0; an omitted mono port selects a stereo pair.
The channel keeps its id, name, send levels, channel volume, insert chain,
exclusive group and appearance. All its monitor-kind sends are muted; other
send mutes stay as they were. Sound Check on this channel ends with a reason.
A change between mono and stereo is refused while the chain holds any insert,
even a bypassed or unavailable one. Remove those inserts first. An empty
pair-0 chain on a one-channel source follows the existing mono migration.
Changing ports on the same source also rewires and mutes the monitor sends.
Submitting an unchanged binding only saves it, as does one whose only change
is a newly learned unit; neither mutes anything.

The command writes the existing `captureSource`, `capturePair`,
`captureMonoChannel` and `captureUnit` fields, without adding layout or state
fields. A selected source belonging to an attached supported unit records
that unit. A different source clears the previous unit when no new unit is
known; first sight can learn it later. Selecting the same saved source while
its unit is absent keeps the remembered unit. The state reports the result in
`binding`, `microphone`, `chainWidth` and the existing connection fields.
The binding and monitor mutes are saved before the graph changes. A failed
save restores both and leaves any Sound Check take running. Rebinding rebuilds
only this input's route and keeps the combine and its sends; a running plugin
editor may close when its chain restarts. Profiles never restore this wiring.

Disconnected sources retain their exact binding and faders. They reconnect
when that node and pair return. With `captureUnit`, a missing exact name
follows the one source on that unit's card with the selected port, through
profile changes and replugging. More than one source on the card is never
guessed at, nor are attached units sharing an indistinguishable model id.
The saved name, chain, sends and channel id stay unchanged.
While two nodes share the source's name the channel stays silent with a
warning, until the name is unique again.
Both sides of the channel must connect before the input reports connected.
If only one link succeeds, it is removed and the next sweep retries the
whole connection, including the two links used for a mono source.

`setWaveInterfaceEnabled {device, value: false, keepChannels: true}` leaves
the unit's capture channels and chains in the layout. Without `keepChannels`,
or with false, it deletes them through `deleteChannel`. The saved unit id
finds them even while the unit is unplugged; older entries without it use
unambiguous source fragments.

Multiple interfaces can therefore supply audio simultaneously, independently
of the interface selected for hardware controls. Hardware controls still
target their unit. The software low cut and ClipGuard stay on the XLR
strips.
Capture inputs host their own insert chains through the API. A binding with
`captureMonoChannel` has `microphone: true` and `chainWidth: 1`; a pair has
`microphone: false` and `chainWidth: 2`, even while disconnected. A
microphone capture input also has the microphone EQ, which runs in software
and is saved in `mixer.json` under `eq` by its channel id, like XLR 1's
([api.md](api.md#eq-on-capture-inputs)).

The daemon moves a pair-0 capture input with no mono port to
`captureMonoChannel: 0` when its source reports `audio.channels: 1` and its
saved insert list is empty. It logs the change and saves it in
`mixer.json`. Existing links stay in place, since a lone source port already
feeds both sides. Missing sources, unknown channel counts, other pairs and
nonempty chains, including bypassed inserts, keep their binding and width.

An active chain uses `OpenXLR_lc_<id>_<generation>_in/_out` before the
existing combine. Without active inserts, no node is added. The new route
is connected before the old one stops. Each chained channel owns and heals
its links independently; the direct-feed healer handles unchained channels.
A source going away stops its chain, and its return rebuilds it. Load errors
leave the direct feed playing. Three host failures within five minutes leave
that channel on its direct feed with the reason on its inserts.

The settings loader checks insert keys against the restored layout. It
keeps XLR 1, XLR 2, known mixes and capture channels, and drops and logs any
other key. An insert whose plugin does not fit the width stays saved and
reports an error. Profiles
include capture chains, with an empty list for every input that has none;
a profile missing a capture key leaves that chain alone. Deleting a capture
input stops its chain after the removal is saved. Saved profiles retain the
key, so recreating the same id can restore it on recall.

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
an empty or unknown one, hears the default monitor mix: `monitor` when it
exists, otherwise the monitor whose id sorts first. Display order never changes
this default, which the state reports as `defaultMonitorMix`. Creating a monitor
whose id sorts earlier keeps existing fallback outputs on their previous monitor
by saving that feed explicitly, including outputs with stale unknown feed ids.
Deleting a mix drops it from every feed. The jacks of one interface share a return bus, so they
are written with one feed.

`outputRoutes`, the per-route level list that 0.1.40 and 0.1.41 wrote, is
ignored on read, in this file and in profiles.

If a channel or mix deletion cannot be saved, its previous routing settings
and any pending volume or mute writes are restored together. The normal
reconciliation keeps retrying those writes when PipeWire becomes available.
