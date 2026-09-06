# Saved mixer layout

The daemon reads the layout from `mixer.json` before building its graph.
Stop the daemon before editing this file manually: while running, its normal
settings saves overwrite the file with the live configuration.

`userChannels` is an ordered list of application channels and `userMixes` an
ordered list of virtual microphones. Each entry has a stable `id` and a display
`name`. For example:

```json
{
  "userChannels": [{"id": "podcast", "name": "Interview"}],
  "userMixes": [{"id": "recording", "name": "Recording"}]
}
```

These are fields in the existing settings object; retain its other fields when
editing. Missing or null lists keep the legacy defaults. An empty mix list
removes the editable virtual microphones. An empty application list falls back
to System so incoming applications have a destination.

Hardware inputs, Monitor A (`monitor`), Monitor B (`monitor2`) and Aux
(`auxout`) remain structural. The `ignore` application target is reserved.
Invalid or duplicate entries are ignored. IDs contain at most 36 lowercase
ASCII letters, digits, underscores or hyphens, beginning with a letter. Names
contain 1 to 60 printable characters. At most 32 application channels and 16
virtual microphones are restored.

Node names derive from IDs, not labels. The list order survives a settings
save and restart. Removed application destinations fall back to the first
application channel, never a hardware input; ignored applications stay ignored.
Existing monitor-feed settings and profile semantics are unchanged.

## Editing while running

The window's Edit layout button, and the commands below over the API,
change the layout without stopping audio. Each one is saved before it is
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
- `setLayoutOrder {channels, mixes}` reorders the editable ids. Supply every
  application-channel id and every virtual-microphone id exactly once;
  hardware inputs, Monitor A/B and Aux keep their positions. No node changes.

Every added channel or mix costs pipewire-pulse a few dozen open files;
the daemon refuses an addition the server has no room for, and the packages
raise the server's limit ([manual, section 5.8](manual.md#open-files)).

Ids are generated from names (lowercase letters, digits and hyphens,
starting with a letter, unique with a numeric suffix) and never change
afterwards, so node names, profiles and Stream Deck keys survive a rename.

Other manual changes, including external PipeWire descriptions, take effect
at startup.
