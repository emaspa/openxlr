# Saved and editable mixer layout

The daemon reads the layout from `mixer.json` before building its graph.
`userChannels` is an ordered list of application channels and `userMixes` an
ordered list of virtual microphones. Each entry has a stable `id` and a display
`name`. Hardware inputs, Monitor A (`monitor`), Monitor B (`monitor2`) and Aux
(`auxout`) remain structural. The `ignore` application target is reserved.

For example:

```json
{
  "userChannels": [{"id": "podcast", "name": "Interview"}],
  "userMixes": [{"id": "recording", "name": "Recording"}]
}
```

Missing or null lists keep the legacy defaults. An empty mix list removes the
editable virtual microphones. An empty application list heals to System so new
applications always have a safe destination. Invalid, duplicate or reserved
entries are ignored and graph growth is capped at 32 application channels and
16 virtual microphones.

Node names derive from stable IDs, not labels. Removed application destinations
fall back to the first remaining application channel, never a hardware input;
ignored applications stay ignored. Existing monitor-feed and profile semantics
are unchanged.

## Live commands

`createChannel {name}` adds an application channel while running without
rebuilding existing nodes. It succeeds only after the new layout is written to
`mixer.json`. A failed save removes only the new channel module and restores the
previous in-memory layout.

`renameChannel {channel,name}` and `renameMix {mix,name}` change only the saved
OpenXLR display name. Stable IDs, application assignments, profile cells,
OpenDeck targets and the existing PipeWire nodes remain untouched. OpenXLR
clients receive the new label immediately. Because PipeWire/Pulse does not
provide a supported in-place description mutation for these module-owned
endpoints, the desktop audio-device description adopts the new label on the
next normal daemon restart/graph rebuild instead of recreating a live endpoint
just to rename it.

`createMix {name}`, `deleteMix {mix}` and `deleteChannel {channel}` change a
matrix column or row. They therefore rebuild the OpenXLR-owned graph under the
mixer state lock. Applications assigned to a deleted channel move to the first
remaining application channel. Deleted mixes lose their master/send cells and
insert chain. If graph construction or the atomic settings write fails, the
previous configuration is rebuilt before the command returns an error. These
operations can briefly interrupt OpenXLR audio; the UI says so before delete.

`setLayoutOrder {channels,mixes}` changes only display order while running.
Supply every application-channel ID in `channels` and every virtual-microphone
ID in `mixes`, each exactly once. Structural nodes stay fixed. Duplicate,
missing, unknown, oversized and structural IDs are rejected. No PipeWire node
or link changes during reorder.

Layout mutations may include a bounded `requestId`. The daemon sends an
authoritative `state` followed by `commandResult` carrying the same ID and a
nullable `error`. The desktop editor uses this so it does not report success
until the new layout is durable; a dropped connection or timeout is treated as
an unknown result and the restored state should be inspected before retrying.

The desktop editor is available from **Applications → Manage → Channels &
outputs…**. Ordinary fader saves keep their existing debounced best-effort retry
behavior; the strict save-before-success rule above applies only to structural
layout edits.
