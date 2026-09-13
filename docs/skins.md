# Skins

A skin changes how the OpenXLR window looks. It is a folder with a
`skin.json` in it and, optionally, a few images beside it. It sets named
appearance values; it carries no markup, no script and no code, and it
cannot reach a file outside its own folder.

OpenXLR ships two appearances:

- **Material**, the default, which is what the window has always looked
  like. It is compiled in and is what every unset value falls back to.
- **Deck**, which dresses the window in the visual language of the
  OpenDeck keys and the Wave interfaces: near-black faceplates, black keys
  whose lettering is backlit green when a control is on and red when
  something is muted, bypassed or destructive, console faders with a
  machined cap, indicator lamps in a bezel, and meters that run green to
  amber to red across the scale.

Pick one in Options under APPEARANCE. The choice is saved in
`~/.config/openxlr/ui.json` as `"skin": "<id>"` and nowhere else: it is not
part of the mixer layout, the daemon's preferences or a profile, and
changing it never touches audio. Windows that are already open repaint;
nothing is rebuilt and nothing is restarted.

If a skin ever makes something unreadable, start the window once with

```sh
OPENXLR_SKIN=default openxlr
```

which ignores the saved choice for that run and leaves it saved, so you
can pick another one from Options. Options says when a run was started
that way. Any id works there, not only `default`, which is the quickest
way to try a skin without selecting it.

## Getting started

A skin can be eight lines. Put this in
`~/.local/share/openxlr/skins/my-skin/skin.json`:

```json
{
  "schema": 1,
  "name": "My skin",
  "tokens": {
    "Ox.Window.Background": "#101418",
    "Ox.Text.Primary": "#eef2f6"
  }
}
```

Open Options, press Reload beside the picker, and choose "My skin
(installed for you)". Everything the file does not mention keeps the
default appearance, so you can start with two values and grow the file.
Anything OpenXLR could not use is listed under the picker, with the token
name and the reason.

Edit, press Reload, look. Reload rereads the folders, so a skin you have
just edited, added or removed is picked up without restarting the window.

### Start from a working skin

Two complete files are in the source repository. Neither is installed on
disk by a package, because the shipped skins are compiled into the
application, so copy the one you want from the repository with one of
these two:

```sh
mkdir -p ~/.local/share/openxlr/skins/my-skin
# a small starting point
curl -fLo ~/.local/share/openxlr/skins/my-skin/skin.json \
  https://raw.githubusercontent.com/emaspa/openxlr/main/docs/examples/skins/example/skin.json
# or the whole Deck appearance, which uses every feature of the format
curl -fLo ~/.local/share/openxlr/skins/my-skin/skin.json \
  https://raw.githubusercontent.com/emaspa/openxlr/main/src/OpenXLR.UI/Assets/Skins/opendeck/skin.json
```

Then change `name` (and `id`, if you keep one) so your copy is not
confused with the original, and edit from there. In a source checkout the
same two files are at
[docs/examples/skins/example/skin.json](examples/skins/example/skin.json)
and `src/OpenXLR.UI/Assets/Skins/opendeck/skin.json`.

The example skin is read by the test suite on every build, so it is always
a file this version accepts.

### Editor help

[skin.schema.json](skin.schema.json) is a JSON Schema for the format. An
editor that understands it will complete token names and flag a value of
the wrong shape as you type. Point at it from the file itself:

```json
{
  "$schema": "https://raw.githubusercontent.com/emaspa/openxlr/main/docs/skin.schema.json",
  "schema": 1,
  "name": "My skin",
  "tokens": {}
}
```

OpenXLR ignores `$schema`, and the schema is checked against the token
list and the limits in the code by the test suite, so the two cannot drift
apart. The schema is a writing aid: OpenXLR itself decides what it
accepts, and this document describes that.

## Where skins are found

| Where | Path |
|---|---|
| yours | `$XDG_DATA_HOME/openxlr/skins/<id>/skin.json` (`~/.local/share` when unset) |
| this system | each of `$XDG_DATA_DIRS` plus `/openxlr/skins/<id>/skin.json` (`/usr/local/share:/usr/share` when unset) |
| shipped | compiled into the application |

The folder name is the skin's id. An id found in more than one place is
taken from the first row that has it, so your own copy shadows a system
one and a system one shadows a shipped one. Ids are lower-case letters,
digits, dot, dash and underscore, start with a letter or a digit, and are
at most 64 characters. At most 200 skins are read in one scan.

The id is what is stored; the name is what is read. `ui.json` holds the
id, so renaming a skin's `name` never moves the choice, and a folder you
rename is a different skin as far as the saved choice is concerned.
Material is always in the picker and always first; the rest follow by
name.

"Reload" in Options reads the folders again without restarting.

## The file

```json
{
  "schema": 1,
  "id": "my-skin",
  "name": "My skin",
  "description": "One line about it.",
  "author": "Someone",
  "controls": { "fader": "console" },
  "tokens": {
    "Ox.Card.Background": "#1d2027",
    "Ox.Text.Primary": "#e6e9f0",
    "Ox.Card.CornerRadius": 8
  }
}
```

| Field | Required | What it is |
|---|---|---|
| `schema` | yes | the format version this file is written for, `1` |
| `id` | no | the id the author intended; the folder name wins when they disagree, and Options says so |
| `name` | no | what the picker shows; the id when it is missing |
| `description` | no | one line, shown under the picker |
| `author` | no | shown under the picker as "By ..." |
| `controls` | no | one of OpenXLR's own appearances per control, see [Controls](#controls) |
| `tokens` | yes | the appearance values, see [The tokens](#the-tokens) |

`name`, `description` and `author` are cut at 400 characters, and control
characters in them are dropped.

There is no `version` field and no `license` field: OpenXLR reads none, so
put a version in the `description` if you want one, and licensing in a
file beside `skin.json`. Files in the folder other than `skin.json` and
the images the tokens name are ignored by OpenXLR and are yours to use,
which is where a `LICENSE`, a `README` or your source artwork can live.

Nothing in a skin identifies the machine it runs on, and no part of it is
ever sent anywhere.

### How a skin is read

The whole file is refused, and the previous appearance stays on, when:

- the folder name is not a usable id;
- `skin.json` is larger than 256 KB, or is not valid JSON, or is not a
  JSON object;
- `schema` is missing, is not a number, or is below 1;
- `schema` is above the version this OpenXLR reads, which is `1`. The
  message says to update OpenXLR. A newer OpenXLR keeps reading schema 1
  files.

Anything else is a complaint about one value: it is reported, that value
is dropped, and the rest of the file still applies. A token name this
version does not have, a control or an appearance it does not have, a
value of the wrong type, a number outside its bounds, an unreadable or
oversized image: each of those costs you that one value and nothing else.
Every token the file does not mention keeps the default. A partial skin is
an overlay, not a half-painted window, which is also what makes a skin
written for a later OpenXLR usable here as far as this version
understands it.

Unknown fields at the top level of the file are ignored without comment,
which is what lets `$schema` sit there. Unknown names inside `tokens` and
`controls` are reported, since those are almost always a typo.

The complaints appear in Options under the picker. The mixer window never
shows them.

## Controls

A skin does not supply a template, a style or any markup. What it can do is
choose, for each control below, one of the appearances OpenXLR itself
draws, and then tint and size it with the tokens further down.

```json
"controls": {
  "button": "cap",
  "fader": "console",
  "meter": "segmented",
  "led": "lamp",
  "mute": "cap"
}
```

| Control | Appearances | What it is |
|---|---|---|
| `button` | `flat` (default), `cap` | `flat` is the framework's button. `cap` is the raised key: the control paints its own face and `Ox.Cap.Bevel` lays the gloss over it. One drawing serves the plain button, the toggle and the dropdown button, so a skin cannot raise one and leave the next one flat. Corner shape comes from `Ox.Control.CornerRadius`, so a skin can make every key square |
| `fader` | `flat` (default), `console` | `flat` is the framework's slider. `console` is OpenXLR's own: an inset groove, a filled section up to the value, and a machined cap with a grip line across it. It is drawn for horizontal faders, which is all the window has; a vertical one keeps the framework's |
| `meter` | `continuous` (default), `segmented` | `continuous` is one bar that grows. `segmented` is a ladder of cells that light one after another, as on a console |
| `led` | `flat` (default), `lamp` | `flat` is a filled dot. `lamp` puts the dot in a bezel ring and gives it a halo while it is lit |
| `mute` | `flat` (default), `cap` | `flat` is the framework's button. `cap` is the same raised key the `button` slot draws, so the mute matches the controls around it |

A control a skin does not mention keeps the appearance the window has
always had. The `button` slot covers the plain button, the toggle and the
dropdown button together on purpose: a skin picks a key face, and every
key wears it.

The console fader keeps the framework's own slider underneath. The
template hands the track, the two groove halves and the cap to the slider
under the names it looks for, so dragging, clicking the groove, the
keyboard, tick snapping and display scaling are unchanged. A skin cannot
change what a control does, only how it is drawn.

Some tokens are read only by one of these appearances and do nothing under
the others. They are marked in the tables below: the grip and track radius
under `fader: console`, the segment count and gap under `meter:
segmented`, the bezel, glow and core scale under `led: lamp`, and
`Ox.Cap.Bevel` and `Ox.Control.BorderThickness` under the `cap` faces.

A lamp draws entirely inside the box `Ox.Led.Size` gives it: the lit core
fills `Ox.Led.CoreScale` of it, the bezel is stroked outside the core, and
the halo fills what is left up to the edge. Nothing is drawn beyond the
box, so a lamp at the edge of a rounded card is never cut. Give the lamp a
larger `Ox.Led.Size` and a smaller `Ox.Led.CoreScale` for more halo, not a
bigger glow. The halo is drawn as soft rings rather than a real blur,
since the drawing context has none; at the size these lamps are drawn it
reads the same.

## Values

A token holds one of six kinds of value: a **brush**, a **colour** (a
brush that must stay flat), a **number**, a **radius**, a **thickness**, a
**family** or a **weight**. The kind is in the tables below, and a value
of the wrong kind is refused with a message naming the token.

**A brush** is a colour string, or an object:

```json
"Ox.Card.Background": "#1d2027",
"Ox.Badge.Background": "#cc1d2027",
"Ox.Divider": "slategray",
"Ox.Window.Background": {
  "type": "linear", "from": [0, 0], "to": [0, 1],
  "stops": [{ "offset": 0, "color": "#262626" }, { "offset": 1, "color": "#151515" }]
},
"Ox.Control.Background": {
  "type": "radial", "from": [0.5, 0.25], "to": [0.5, 0.5], "radius": 0.9,
  "stops": [{ "offset": 0, "color": "#4a4a4a" }, { "offset": 1, "color": "#333333" }]
},
"Ox.Tile.Background": {
  "type": "image", "source": "faceplate.png", "stretch": "uniformToFill", "opacity": 1
}
```

A colour string is `#rgb`, `#rrggbb`, `#aarrggbb` or a colour name.
`"type": "solid"` with a `color` is the same thing written as an object.

`from` and `to` are relative to the box being painted: `[0, 0]` its top
left corner, `[1, 1]` its bottom right, and each number is between -2 and
3 so a gradient may start outside the box. For a linear gradient `from`
defaults to `[0, 0]` and `to` to `[0, 1]`, which is top to bottom. For a
radial gradient `to` is the centre and `from` the highlight, both
defaulting to `[0.5, 0.5]`, and `radius` is between 0 and 4, 0.5 by
default. A gradient needs between 2 and 16 `stops`, each with a `color`
and an `offset` between 0 and 1.

An image is described under [Images](#images).

**A number** is a plain JSON number inside the bounds in the tables.

**A radius** is one number, applied to all four corners, or four: top
left, top right, bottom right, bottom left.

**A thickness** is one number, or two (left and right, then top and
bottom), or four (left, top, right, bottom).

**A family** is the name of a font already installed on the machine, up to
120 characters. A skin never loads a font file, so a family with a `#` or
a URL in it is refused. Name a family the machine is likely to have, or
one your skin's readme tells the user to install; an unknown family falls
back to the system default without an error.

**A weight** is `"Thin"`, `"ExtraLight"`, `"Light"`, `"Normal"`,
`"Medium"`, `"SemiBold"`, `"Bold"`, `"ExtraBold"`, `"Black"` and the other
names the toolkit knows, in any case, or a number from 1 to 1000.

## The tokens

Every value the window has is below. The **default** column is the
appearance OpenXLR ships with: a `#rrggbb` or `#aarrggbb` colour, a
number, or `framework`, which means OpenXLR sets nothing there and the
toolkit's own value stays. **Range** is the bounds a number, a radius or a
thickness is held to; a value outside them is refused and the default
stays.

A `framework` value is the toolkit's, and the toolkit follows the
desktop's light or dark setting. A skin that wants to look the same on
every desktop sets those tokens rather than leaving them; a skin that sets
only the surfaces and the text inherits whatever the desktop asked for
underneath.

The tables are checked against the code by the test suite, so a token
without a row here fails the build.

### Surfaces

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Window.Background` | brush | #16181d |  | the ground behind every window |
| `Ox.Card.Background` | brush | #1d2027 |  | a section card: INPUTS, HEADPHONES, an Options group |
| `Ox.Card.BorderBrush` | brush | #00000000 |  | that card's border |
| `Ox.Card.BorderThickness` | thickness | 0 | 0 to 16 | that border's width |
| `Ox.Card.CornerRadius` | radius | 8 | 0 to 48 | that card's corners |
| `Ox.Tile.Background` | brush | #23262f |  | a tile on a card: a channel strip, a mix master, an application chip |
| `Ox.Tile.BorderBrush` | brush | #00000000 |  | that tile's border |
| `Ox.Tile.BorderThickness` | thickness | 0 | 0 to 16 | that border's width |
| `Ox.Tile.CornerRadius` | radius | 6 | 0 to 48 | that tile's corners |
| `Ox.Dialog.Background` | brush | #1d2027 |  | the small confirmation and rename dialogs |
| `Ox.Danger.Background` | brush | #a03434 |  | a button that deletes something |
| `Ox.Danger.Foreground` | brush | #f6dede |  | that button's label |
| `Ox.Badge.Background` | brush | #2a2e38 |  | the small format badge beside a plugin name |
| `Ox.Divider` | brush | #2d313c |  | the rule under a plugin control group |

### Text

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Text.Primary` | brush | #e6e9f0 |  | names and readouts |
| `Ox.Text.Secondary` | brush | #8b93a7 |  | labels and section headings |
| `Ox.Text.Muted` | brush | #6b7285 |  | explanatory lines |
| `Ox.Text.Detail` | brush | #b9bfcc |  | paragraphs in About, release notes, plugin group headings |
| `Ox.Text.Warning` | brush | #e0a05a |  | something went wrong |
| `Ox.Text.Alert` | brush | #e0a030 |  | something needs attention |
| `Ox.Text.Info` | brush | #5ba8f5 |  | an update is available |
| `Ox.Link.Foreground` | brush | #5ba8f5 |  | a link |
| `Ox.Link.ForegroundPointerOver` | brush | #8cc4ff |  | a link under the pointer |

### Typography

| Token | Kind | Default | Range | What it sets |
|---|---|---|---|---|
| `Ox.Label.FontSize` | number | 12 | 8 to 24 | section headings and labels |
| `Ox.Label.FontWeight` | weight | SemiBold |  | those same headings and labels |
| `Ox.Title.FontSize` | number | 20 | 10 to 40 | the window's own title line |
| `Ox.Title.FontWeight` | weight | Bold |  | that title line |
| `Ox.Strip.FontWeight` | weight | SemiBold |  | the name over a channel strip or a mix |
| `Ox.Value.FontFamily` | family | monospace |  | the numeric readouts |
| `Ox.Font.Family` | family | framework |  | the font the buttons, labels and dropdowns use |

### Indicators

These six reach the window through code as well as through markup, so they
must be flat colours. A gradient or an image in one of them is refused
with a message saying why.

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Led.On` | colour | #3ecf7a |  | a connected device, a plugin that is processing |
| `Ox.Led.Off` | colour | #4a4f5c |  | a device that is not there |
| `Ox.Led.Alert` | colour | #ff3c4e |  | a plugin bypassed or failed |
| `Ox.Meter.Fill` | colour | #3ecf7a |  | the quiet end of the scale |
| `Ox.Meter.Warning` | colour | #3ecf7a |  | the middle of the scale |
| `Ox.Meter.Hot` | colour | #3ecf7a |  | the loud end of the scale |

The rest of an indicator's appearance is shape and size:

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Led.Size` | number | 9 | 4 to 40 | the box a lamp or a dot is drawn in, both appearances |
| `Ox.Led.Bezel` | brush | #00000000 |  | the ring around the core, `led: lamp` only |
| `Ox.Led.BezelThickness` | number | 0 | 0 to 6 | that ring's width, `led: lamp` only |
| `Ox.Led.Glow` | number | 0 | 0 to 1 | the halo while the lamp is lit, 0 for none, `led: lamp` only |
| `Ox.Led.CoreScale` | number | 1 | 0.1 to 1 | how much of the box the lit core fills, `led: lamp` only |
| `Ox.Meter.Track` | brush | #14161b |  | the groove a meter sits in, and an unlit cell |
| `Ox.Meter.CornerRadius` | radius | 2 | 0 to 48 | the meter's corners, and each cell's |
| `Ox.Meter.Height` | number | 4 | 2 to 24 | how tall a meter is |
| `Ox.Meter.Segments` | number | 16 | 2 to 64 | how many cells, `meter: segmented` only |
| `Ox.Meter.SegmentGap` | number | 2 | 0 to 8 | the gap between cells in pixels, `meter: segmented` only |

Cells are laid out on whole pixels, and a ladder too narrow to draw is
drawn as one continuous bar instead, so a segmented meter never comes out
as a smear.

### The meter's scale

A meter reads **RMS dBFS**: 0 is -60 dBFS and below, 1 is 0 dBFS, so a
reading of `x` is `60 * x - 60` decibels. It is an RMS meter, not a peak
meter, which is why the loud end starts lower than you might expect.

| Token | Kind | Default | Range | Where it sits |
|---|---|---|---|---|
| `Ox.Meter.WarningLevel` | number | 0.7 | 0 to 1 | -18 dBFS, the usual alignment level |
| `Ox.Meter.HotLevel` | number | 0.9 | 0 to 1 | -6 dBFS, where an RMS reading leaves no headroom for the peaks |

The colour belongs to the **place on the scale**, not to the reading. The
quiet end is drawn in `Ox.Meter.Fill` however loud the signal gets, and
the warning and top colours appear only on the parts of the bar the signal
has actually reached. A meter never repaints itself end to end. A cell of
a segmented meter is coloured by its own place on the scale for the same
reason. A hot level below the warning level is read as equal to it, so the
zones can never cross.

All three colours default to the same green, so the shipped appearance
draws no zones and looks exactly as it always has. A skin that wants a
hi-fi ladder sets `Ox.Meter.Warning` and `Ox.Meter.Hot` and gets the
default thresholds without having to state them.

### Mute buttons

A mute button is checked when the channel is muted, so its colours are
named for what the audio is doing rather than for the toggle, and they are
its own rather than the ordinary control's.

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Mute.Background` | brush | #1e5c3a |  | the face while audio passes |
| `Ox.Mute.Foreground` | brush | #d9efe2 |  | its label while audio passes |
| `Ox.Mute.BackgroundPointerOver` | brush | #27754b |  | the face under the pointer |
| `Ox.Mute.BackgroundChecked` | brush | #a03434 |  | the face when muted |
| `Ox.Mute.ForegroundChecked` | brush | #f6dede |  | its label when muted |
| `Ox.Mute.BorderBrush` | brush | #00000000 |  | its ring |
| `Ox.Mute.BorderThickness` | thickness | 0 | 0 to 16 | that ring's width |

### Faders

Under `fader: flat` these feed the framework's own slider; under
`fader: console` they feed OpenXLR's. Either way the slider's behaviour is
the framework's.

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Fader.Track` | brush | framework |  | the part of the groove above the value |
| `Ox.Fader.Fill` | brush | framework |  | the part below the value |
| `Ox.Fader.TrackHeight` | number | 2 | 1 to 24 | how thick the groove is |
| `Ox.Fader.TrackCornerRadius` | radius | 1 | 0 to 48 | the groove's corners, `fader: console` only |
| `Ox.Fader.Thumb.Background` | brush | framework |  | the cap |
| `Ox.Fader.Thumb.BorderBrush` | brush | #00000000 |  | the cap's ring |
| `Ox.Fader.Thumb.BorderThickness` | thickness | 0 | 0 to 16 | that ring's width |
| `Ox.Fader.Thumb.Width` | number | 22 | 4 to 64 | the cap across the groove |
| `Ox.Fader.Thumb.Height` | number | 10 | 4 to 64 | the cap along the groove |
| `Ox.Fader.Thumb.CornerRadius` | radius | 2 | 0 to 48 | the cap's corners |
| `Ox.Fader.Thumb.Grip` | brush | #00000000 |  | the line across the cap, `fader: console` only |
| `Ox.Fader.Thumb.GripLength` | number | 6 | 0 to 64 | how long that line is, `fader: console` only |

A vertical fader swaps the thumb's width and height, so one pair of values
sizes both.

### Buttons, toggles and dropdowns

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Control.Background` | brush | framework |  | a button, a toggle, a dropdown |
| `Ox.Control.BackgroundPointerOver` | brush | framework |  | one under the pointer |
| `Ox.Control.BackgroundPressed` | brush | framework |  | one held down |
| `Ox.Control.BackgroundChecked` | brush | framework |  | a toggle that is on |
| `Ox.Control.BackgroundDisabled` | brush | framework |  | one that cannot be used |
| `Ox.Control.Foreground` | brush | framework |  | the lettering on a button or a dropdown |
| `Ox.Control.ForegroundChecked` | brush | framework |  | a toggle's lettering when on, unless `Ox.Toggle.ForegroundChecked` is set |
| `Ox.Control.ForegroundDisabled` | brush | framework |  | the lettering when the control cannot be used |
| `Ox.Control.BorderBrush` | brush | framework |  | the ring |
| `Ox.Control.BorderBrushPointerOver` | brush | framework |  | the ring under the pointer |
| `Ox.Control.BorderBrushDisabled` | brush | framework |  | the ring when the control cannot be used |
| `Ox.Control.BorderThickness` | thickness | 1 | 0 to 16 | the ring's width on a raised key, `button: cap` and `mute: cap` only |
| `Ox.Control.CornerRadius` | radius | framework | 0 to 48 | the corners of every key, and of the boxes the framework rounds |
| `Ox.Control.DisabledOpacity` | number | 1 | 0.2 to 1 | how far a key fades when it cannot be used, 1 for no fade |
| `Ox.Ghost.Background` | brush | #00000000 |  | the face behind a button a view keeps out of the way: the header actions, the rows inside a flyout |
| `Ox.Cap.Bevel` | brush | #00000000 |  | the gloss over every raised key face, `button: cap` and `mute: cap` only; usually a gradient, and one brush for all of them so the keys cannot drift apart |

A toggle's lettering can say what the toggle is doing, which is how the
Deck skin works: the key stays black and the legend lights up. These take
precedence over `Ox.Control.Foreground` and `Ox.Control.ForegroundChecked`
for toggles, so a skin that sets only the older tokens behaves as it did.

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Toggle.Foreground` | brush | framework |  | a toggle that is off |
| `Ox.Toggle.ForegroundChecked` | brush | framework |  | a toggle that is on |
| `Ox.Toggle.ForegroundDisabled` | brush | framework |  | a toggle that cannot be used |
| `Ox.Toggle.BorderBrushChecked` | brush | framework |  | the ring of a toggle that is on |
| `Ox.Bypass.Foreground` | brush | #ffffff |  | the plugin bypass key while the plugin is processing |
| `Ox.Bypass.ForegroundBypassed` | brush | #ffffff |  | the same key while the plugin is bypassed |
| `Ox.Check.Foreground` | brush | #e6e9f0 |  | the label beside a checkbox, which is running text rather than a lit legend |

The bypass key reads the other way round from an ordinary toggle, because
its checked state is the one that stops the audio. Its two colours default
to the plain lettering the framework uses, so the key stays readable in an
appearance that does not colour by state.

### Text boxes, flyouts and the accent

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Input.Background` | brush | framework |  | a text box |
| `Ox.Input.Foreground` | brush | framework |  | what you type |
| `Ox.Input.BorderBrush` | brush | framework |  | its ring |
| `Ox.Flyout.Background` | brush | framework |  | a dropdown's list and a flyout |
| `Ox.Tooltip.Background` | brush | framework |  | a tooltip |
| `Ox.Tooltip.Foreground` | brush | framework |  | its text |
| `Ox.Tooltip.BorderBrush` | brush | framework |  | its border |
| `Ox.Accent` | colour | framework |  | the tick in a checkbox and the selection in a list; must be flat, since the theme reads it as a colour |

A checkbox, a repeat button inside a fader groove and the toggle that
opens a section header are not keys: they keep their own appearance
whichever button variant is chosen.

A control token a skin does not set keeps the framework's own value, so a
skin that only recolours the cards leaves buttons exactly as they are.

### Audio flow window

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Flow.Background` | brush | #242624 |  | the ground behind the graph |
| `Ox.Flow.Card` | brush | #333633 |  | a node |
| `Ox.Flow.CardSelected` | brush | #414541 |  | the node whose path is being traced |
| `Ox.Flow.Text` | brush | #f0f2ef |  | a node's name and the window's title |
| `Ox.Flow.TextDim` | brush | #a3aaa3 |  | a node's second line, the legend and the selected node's outline |
| `Ox.Flow.Input` | brush | #20c4d1 |  | an edge from an input to a channel |
| `Ox.Flow.Channel` | brush | #c958e5 |  | an edge from a channel to a mix |
| `Ox.Flow.Output` | brush | #20d49a |  | an edge from a mix to an output |
| `Ox.Flow.Muted` | brush | #757d75 |  | an edge that is muted or not in the traced path |
| `Ox.Flow.NodeHoverBackground` | brush | #414541 |  | a node under the pointer |
| `Ox.Flow.NodeHoverBorderBrush` | brush | #717971 |  | that node's outline |

## Images

An image comes from the skin's own folder:

```json
"Ox.Window.Background": {
  "type": "image", "source": "art/faceplate.png", "stretch": "uniformToFill", "opacity": 0.8
}
```

`stretch` is `fill`, `uniform`, `uniformToFill` (the default) or `none`,
and `opacity` is between 0 and 1, 1 by default.

The rules a `source` is held to:

- it is a relative path inside the skin's folder, with forward slashes, no
  leading slash, no `.` or `..` segment, no colon and no URL. Nothing is
  ever fetched over the network;
- a symbolic link whose target is outside the folder is not followed;
- the file is a PNG or a JPEG, at most 4 MB, at most 4096 pixels a side,
  and a skin's images together decode to at most 16 megapixels;
- a skin may name at most 32 images;
- a name is at most 256 characters.

The size is read from the file's header before anything is decoded, so a
small file that claims to be enormous is refused rather than unpacked. An
image that breaks one of these rules is reported in Options and that one
token keeps its default.

Images are paintable wherever a brush is: a faceplate behind the window, a
texture on a card, a metal face on a key. They are not available to the
six flat indicator colours.

## What a skin cannot do

- It cannot carry markup, a template, a style or code. It picks from the
  appearances listed under Controls and tints them. There is no way for a
  skin to add a control, move one, resize the window, or change what a
  control does.
- It cannot change the layout, the mixer, the routing or anything the
  daemon owns. It sets the values in this document and nothing else.
- It cannot reach outside its own folder, and it cannot make a network
  request or load a font file.
- It cannot skin a plugin's own editor. Those windows belong to the
  plugin, are drawn by the plugin's own toolkit, and OpenXLR only hosts
  them. The generated controls OpenXLR draws for a plugin do follow the
  skin.
- It cannot change the OpenDeck plugin's key art. The plugin draws its own
  images on the device; the Deck skin borrows that visual language for the
  window, not the other way round.
- It cannot hold a light and a dark version of itself, or follow the
  desktop's preference. A skin is one set of values. OpenXLR's own values
  are dark, and the toolkit values a skin leaves at `framework` are the
  ones that follow the desktop. A light appearance of OpenXLR's own is on
  the roadmap, not in this format.
- It cannot skin the tray icon, the desktop notification or any window
  another application draws.

## Colour, contrast and testing

The test suite holds both shipped appearances to a few rules, and a skin
you intend to share is worth checking against the same ones by eye:

- text stays readable on the surface it lands on, and an indicator stays
  distinguishable from the strip it sits on;
- state is never carried by hue alone. A key that cannot be used keeps its
  colours and fades instead, which is what `Ox.Control.DisabledOpacity` is
  for: if your skin letters an off toggle and an unavailable action in the
  same red, set it below 1 so the two are still told apart;
- a control is never made harder to hit than the shipped appearance makes
  it. Sizes are bounded, but a fader cap of 4 pixels or a 2 pixel meter is
  within the bounds and still a bad idea;
- the names a screen reader reads come from the window, not from the skin,
  and a focused control still shows its focus adorner. Nothing a skin can
  set changes either, but a face with no contrast against the focus ring
  hides it in practice.

Walk the windows before you share a skin: the mixer, Options, the audio
flow window, the plugin controls window, a confirmation dialog, a tooltip
and a dropdown list. The pointer-over, pressed, checked and disabled
states are separate tokens, and an appearance that only sets the resting
ones looks broken as soon as the pointer moves.

## Sharing and installing

There is no archive installer. A skin is a folder you copy into place:

```sh
unzip my-skin.zip -d ~/.local/share/openxlr/skins/
```

as long as the archive holds one folder with `skin.json` in it. The folder
name becomes the id, so pick one that is unlikely to collide and say in
your readme what it is. A system-wide install is the same folder under
`/usr/share/openxlr/skins/`, where every user on the machine sees it and
their own copy of the same id shadows it.

To stop using a skin, pick another one in Options. To remove it, delete
its folder and press Reload. A folder that is gone while its skin is the
chosen one leaves the window on Material, and `ui.json` keeps the name
until you choose something else, so putting the folder back brings it
straight back.

To reset the appearance completely, choose Material in Options, or close
the window and delete the `"skin"` line from
`~/.config/openxlr/ui.json`. That line is the whole of the skin's presence
on your machine, and nothing about the mixer, the daemon or your audio
devices is kept in that file at all, so neither route can disturb what is
playing.

## Adding a token

For the record, since the format is only as complete as the window: a
value the window hard-codes cannot be skinned. If something you want to
change has no token here, that is a missing token rather than a limit of
the format, and it is a small change to the source. See
[AGENTS.md](../AGENTS.md) and open an issue or a pull request.
