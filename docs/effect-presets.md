# Effect presets

The window can copy effects between chains, compare two settings of a chain,
and save chains or single effects as named presets. All of it works on the
parameter values OpenXLR already shows. A preset never holds plugin-private
state, so an effect's own preset banks, loaded sample files and editor
layout stay out of it. The [manual](manual.md#effect-presets) explains the
buttons. This page describes the files.

## What a preset holds

A preset is a name and a chain. The chain is an ordered list of effects,
each written the way the window sends it to the daemon with
[`setInserts`](api.md). Every effect has its slot id, its format, the plugin identifier, the
label it shows, whether it is bypassed, whether it runs in the native host,
and the value of every control the window has set. A single-effect preset
is a chain with exactly one effect in it.

## Where they live

Saved presets are one file, `effect-chain-presets.json`, under
`~/.config/openxlr/` (or `$XDG_CONFIG_HOME/openxlr/`). The window writes it
whole through an atomic replace, with owner-only permissions. The daemon
never reads it. The file is a JSON array of presets in the format below.

Export writes one preset to a file the user picks, named
`*.openxlr-effects.json` by default. A local file is written through the
same atomic replace. A location the desktop's file chooser serves another
way, such as a portal or a network share, gets the bytes as a stream, and
that provider decides how safely an existing file is replaced. Import reads one such
file and adds it to the saved presets. It never applies a chain and never
installs a plugin.

## Format

```json
{
  "name": "Speech",
  "chain": {
    "version": 1,
    "channels": 1,
    "inserts": [
      {
        "id": "3f1c9a2e",
        "kind": "lv2",
        "plugin": "urn:example:gate",
        "label": "Speech gate",
        "bypass": false,
        "nativeHost": false,
        "params": { "threshold": -30, "attack": 5 }
      }
    ]
  }
}
```

| Field | Meaning |
|---|---|
| `name` | 1 to 80 characters, not blank, no control characters. Names are unique in the store, compared without regard to case |
| `chain.version` | always `1`; any other value is refused |
| `chain.channels` | the width of the chain the preset was taken from: `1` for an input, `2` for a mix. Loading does not enforce it; the daemon checks every plugin against the width of the chain it is loaded into |
| `chain.inserts` | 0 to 16 effects, in signal order |
| `id` | 1 to 64 letters, digits, `-` or `_`, unique within the chain |
| `kind` | `lv2`, `clap` or `vst3` |
| `plugin` | the LV2 URI, CLAP id or VST3 class id, up to 512 characters |
| `label` | optional, up to 256 characters |
| `bypass`, `nativeHost` | optional booleans |
| `params` | an object of up to 256 controls, each a symbol of 1 to 256 characters without spaces or control characters, and a finite number |

The window refuses a whole file that is over 8 MiB, repeats a property in
any object, holds a value of the wrong type or has a field outside these
bounds, and shows one error line. The store holds at most 64 presets and
is itself limited to 8 MiB. A store that fails these checks is reported
and left as it is. The window will not save over it until the file is
repaired or removed.

## Loading and ids

Loading a chain preset and pasting copied effects give every effect a new
slot id, so the same preset can be loaded on several chains. Loading a
single-effect preset replaces only its target effect and keeps that
effect's id and every other effect in the chain. Hearing an A/B slot puts
back the chain exactly as it was stored, ids included.

Stream Deck keys and dials follow an effect by its id. After a chain preset
is loaded or a chain is replaced by a paste, a key bound to one of the old
effects falls back the way it does after a profile recall, to the same
plugin in the same chain.

Every load goes through `setInserts` and its validation. The daemon refuses
a plugin that is not installed, a control the plugin does not declare, or a
plugin that cannot run at the chain's width, and the chain stays as it was.
Before it replaces the chain, the window sends any control values still
waiting in its queue, so an edit made a moment earlier cannot arrive after
the loaded values and overwrite them.
