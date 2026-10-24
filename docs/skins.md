# Skins

A skin changes how the OpenXLR UI looks. It is a folder with a
`skin.json` in it and, optionally, a few images beside it. It sets named
appearance values and, if it wants, where the header and the
UI's sections go ([Layout](#layout)); it carries no markup, no script and
no code, and it cannot reach a file outside its own folder.

OpenXLR ships sixteen appearances, all compiled into the application, so
every package has them and there is nothing to install:

- **Material**, the classic appearance, in a dark and a light palette (see
  [Material's mode](#materials-mode)). The dark palette is what the UI
  looked like before Broadcast became the default, and it is what every
  unset value falls back to.
- **Deck**, which dresses the UI in the visual language of the
  OpenDeck keys and the Wave interfaces: near-black faceplates, black keys
  whose lettering is backlit green when a control is on and red when
  something is muted, bypassed or destructive, console faders with a
  machined cap, indicator lamps in a bezel, and meters that run green to
  amber to red across the scale.
- Eleven built from the [Omarchy](https://omarchy.org) palettes, so the
  UI matches the rest of a themed desktop: Catppuccin, Catppuccin
  Latte, Everforest, Gruvbox, Kanagawa, Matte Black, Nord, Osaka Jade,
  Ristretto, Rose Pine and Tokyo Night. Rose Pine and Catppuccin Latte
  are the light ones.
- **Broadcast**, the default, a dark layout skin: the submixer as
  a mix matrix across the UI, a row per channel and a column per mix,
  with each channel's send into each mix where its row crosses the mix's
  column. HEADPHONES and MONITOR's toolbar share the row above it, INPUTS
  is hidden because the matrix's row heads carry the input gain, mute,
  microphone switches and plugins, and APPLICATIONS is under the matrix in columns of six, on a dark neutral
  palette. It is the reference for writing a layout skin; see
  [Layout](#layout).
- **Nyan Pink**, a dark plum palette with candy pink faders, cyan details
  and segmented green, amber and red meters. It keeps the classic mixer
  arrangement and the saved section order.
- **Wave**, Broadcast's arrangement in a palette of its own: dark charcoal
  cards with a thin border, grey faders and green meters, and a wider name
  column in the matrix. HEADPHONES and MONITOR's toolbar share the row
  above the matrix, INPUTS is hidden, and APPLICATIONS is under the matrix
  in columns of six. Its cards sit 8 pixels apart where Broadcast's sit 12.
  It uses the same routing and controls as the other skins; no Elgato
  artwork or additional software is needed.

Pick one in Options under APPEARANCE. A new install starts on Broadcast,
and so does an install whose user never picked a skin, so upgrading from a
version that started on Material moves the UI to Broadcast. A skin you
picked stays picked, Material included. To go back to Material, choose
**Material** under APPEARANCE in Options; `ui.json` then holds
`"skin": "default"`. Nothing is written until you choose, and a missing or
empty `"skin"` always means Broadcast. If Broadcast itself cannot be read,
because a copy of it in your skin folder is broken, the UI starts on
Material instead.

The terminal mixer reads the same
folders and the same saved choice, on its own Options tab, so an appearance
picked in one is the appearance in the other. A terminal has no gradients,
images or corner radii, so it takes a gradient at its first stop. It also
reads `controls`: console faders get a three-cell cap, cap keys get
brackets and lamp LEDs get a square face rather than a round one. The pixel sizes and segment counts
remain the UI's; the terminal sizes these drawings to its cells. A
meter is a solid bar in eighth blocks whichever appearance is named, and
its colour blends from `Ox.Meter.Fill` through `Ox.Meter.Warning` to
`Ox.Meter.Hot` along the scale, anchored at the warning and hot levels.
The terminal requires `schema: 1`, as do the UI and Deck. A missing,
invalid or newer schema leaves the whole terminal palette at its defaults.
Within a supported document, malformed terminal palette fields keep their
default while valid fields still apply. Names use the same bounded,
control-free text in the skin picker and the loaded palette.
The terminal checks the 256 KiB document limit each time it reads a file,
including after the picker has listed it. A file that exceeds the limit or
grows during that read keeps the default palette; repairing it allows the
next load to use it again.

The choice is saved in
`~/.config/openxlr/ui.json` as `"skin": "<id>"` and nowhere else: it is not
part of the mixer layout, the daemon's preferences or a profile, and
changing it never touches audio. Windows that are already open repaint;
nothing is rebuilt and nothing is restarted. A skin with a layout moves the
header and the sections that are already there; their controls are the same ones.
If the terminal cannot read `ui.json`, it uses the default appearance.
A malformed document, including duplicate keys or a value other than a
JSON object, is left untouched when saving a terminal skin choice. Repair
the file before saving a new choice; settings unrelated to the skin are
preserved in a valid object. The UI does the same. It wears the chosen
skin for that run, leaves a `ui.json` it cannot read untouched, and says on
the line at the bottom of Options why the choice was not saved.

If a skin ever makes something unreadable, start the UI once with

```sh
OPENXLR_SKIN=default openxlr
```

which ignores the saved choice for that run and leaves it saved, so you
can pick another one from Options. With `default` the UI wears dark
Material whatever the saved mode says. Options says when a run was started
that way, and choosing a skin there ends the override for the rest of the
run. Any id works there, not only `default`, which is the quickest
way to try a skin without selecting it.

## Material's mode

Material has a **Mode** beside the skin picker in Options: **System**,
**Light** or **Dark**. Light and Dark hold that palette. System, the
default, follows the desktop's light or dark preference as the
[settings portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.Settings.html)
reports it, and repaints the open windows when the desktop switches. A
desktop that states no preference, or no portal at all, gets the dark
palette.

On a desktop that prefers light, the UI on Material with no saved mode
therefore opens in the light palette. On any other skin the UI looks
as it did. Deck, Broadcast, the Omarchy palettes and installed skins are one set of
values each and keep them in every mode, and the Mode picker is greyed out
while one of them is chosen. The mode is kept for when Material is chosen
again.

The light palette is skin data in the same format as any skin, compiled
into the application and read the same way. It is not a skin of its own and
does not appear in the picker. Unlike the dark default it colours its meter
zones: the fill is green, the warning zone amber and the hot zone red.

The mode is saved in `~/.config/openxlr/ui.json` as
`"appearanceMode": "system"`, `"light"` or `"dark"`, beside the skin and,
like it, nowhere else. A missing value, an unknown name or a value of the
wrong type reads as System and leaves the rest of the file's preferences
alone. A choice goes on at once even when it cannot be saved, and Options
says why on its preference line.

The terminal mixer reads the same key and offers the same three modes as
**Material mode** on its Options tab, changed with Left and Right. In System
it reads the portal's `color-scheme` with `gdbus` and watches it for
changes, only while Material is the skin and System is the mode. Without a
session bus or without `gdbus` it stays dark. `--skin` and `OPENXLR_SKIN`
hold their skin, and dark Material, until a skin or mode is chosen in
Options.

## Stream Deck colours

The OpenDeck plugin uses the same saved skin for key surfaces, lettering,
indicators, dial icons and needles, and meter colours and thresholds. Built-in
palettes travel with the plugin; regenerate `skin-palettes.json` with
`python3 tools/deck-skins.py` after changing embedded skins or colour defaults.
CI checks the generated table and tests its defaults against `SkinTokens`.

The UI publishes its realised palette to the private
`~/.config/openxlr/deck-palette.json` when it applies a skin. This also carries
the effective light or dark colours without a second desktop-theme connection
in the plugin. The file is an atomic, schema-1 object containing `skin`, the UI process `pid` and
`tokens`, with only the twelve colour and meter values the keys need. It does
not change `ui.json` or audio profiles. A write failure appears in the skin's
errors and leaves the plugin's file-based fallback available.

Without a matching palette from a running UI, the plugin reads the saved `skin` from
`ui.json` and the selected package from the usual XDG skin roots, user first,
then system, then the embedded palette. No saved skin is Broadcast, as in the
UI. Unknown ids and invalid documents fall back to Material. `OPENXLR_SKIN` in the plugin's own environment overrides the
saved choice and the published palette for that run. Empty variables are unset.
File events follow atomic saves and newly created or removed skin folders;
unchanged colours cause no redraw. Meter updates perform no filesystem work.

The Deck preserves its owned glyphs and key geometry. Gradients on surfaces
use their first stop; images and control templates stay in the UI. Hex
RGB and alpha-first ARGB colours are accepted, as are black, white,
transparent, red, green, blue, yellow, gray and grey. Other named colours use
the token default unless the UI publishes their resolved colour. Only
entries declared in the named-colour table qualify; inherited JavaScript
properties such as `constructor` or `__proto__` are not colours, including
when used as gradient stops. Meter
and LED colours are flat; invalid or crossed meter thresholds use the defaults.
Documents are limited to 256 KiB and must be regular files. Up to 32 XDG data
roots are considered. No document can supply SVG, code or an asset URL.

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
just edited, added or removed is picked up without restarting the UI.

### Start from a working skin

The shipped appearances are compiled in, so a package installs none of
them on disk. Copy the one you want to start from out of the repository:

```sh
mkdir -p ~/.local/share/openxlr/skins/my-skin
# a small starting point
curl -fLo ~/.local/share/openxlr/skins/my-skin/skin.json \
  https://raw.githubusercontent.com/emaspa/openxlr/main/docs/examples/skins/example/skin.json
# or the whole Deck appearance, which uses every feature of the format
curl -fLo ~/.local/share/openxlr/skins/my-skin/skin.json \
  https://raw.githubusercontent.com/emaspa/openxlr/main/src/OpenXLR.UI/Assets/Skins/opendeck/skin.json
# or Broadcast, a palette with a complete layout
curl -fLo ~/.local/share/openxlr/skins/my-skin/skin.json \
  https://raw.githubusercontent.com/emaspa/openxlr/main/docs/examples/skins/broadcast/skin.json
```

Any of the other appearances works the same way. They are the `skin.json`
files under
[src/OpenXLR.UI/Assets/Skins](../src/OpenXLR.UI/Assets/Skins), one folder
per id. A palette skin sets colours and nothing else, so it is a shorter
read than Deck.

Then change `name` (and `id`, if you keep one) so your copy is not
confused with the original, and edit from there. In a source checkout the
starting points are under [docs/examples/skins](examples/skins/README.md):
[example/skin.json](examples/skins/example/skin.json), and
[broadcast/skin.json](examples/skins/broadcast/skin.json), a copy of the
shipped Broadcast skin.

The example skin and every shipped one are read by the test suite on every
build, so they are always files this version accepts.

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
| `layout` | no | where the header and the UI's sections go, see [Layout](#layout) |
| `icons` | no | the skin's own channel and mix icons as path data, see [Icons](#icons) |

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
oversized image, an icon whose path is not plain path data: each of those
costs you that one value and nothing else.
Every token the file does not mention keeps the default. A partial skin is
an overlay, not a half-painted UI, which is also what makes a skin
written for a later OpenXLR usable here as far as this version
understands it.

The `layout` block is the one exception: it is taken whole or not at all.
A fault anywhere in it drops the whole block, and only the block, as
[Layout](#layout) describes.

Unknown fields at the top level of the file are ignored without comment,
which is what lets `$schema` sit there. Unknown names inside `tokens` and
`controls` are reported, since those are almost always a typo.

The complaints appear in Options under the picker. The rest of the UI never
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
| `fader` | `flat` (default), `console` | `flat` is the framework's slider. `console` is OpenXLR's own: an inset groove, a filled section up to the value, and a machined cap with a grip line across it. It is drawn flat and upright, so the upright faders of a mix bank wear it too |
| `meter` | `continuous` (default), `segmented` | `continuous` is one bar that grows. `segmented` is a ladder of cells that light one after another, as on a console |
| `led` | `flat` (default), `lamp` | `flat` is a filled dot. `lamp` puts the dot in a bezel ring and gives it a halo while it is lit |
| `mute` | `flat` (default), `cap` | `flat` is the framework's button. `cap` is the same raised key the `button` slot draws, so the mute matches the controls around it |

A control a skin does not mention keeps the appearance the UI has
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

Every value the UI has is below. The **default** column is Material's
dark palette, which is what a value a skin leaves out falls back to: a `#rrggbb` or `#aarrggbb` colour, a
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
| `Ox.Dialog.Background` | brush | #1d2027 |  | the small dialogs: confirmations, rename and the input binding dialog |
| `Ox.Danger.Background` | brush | #a03434 |  | a button that deletes something |
| `Ox.Danger.Foreground` | brush | #f6dede |  | that button's label |
| `Ox.Badge.Background` | brush | #2a2e38 |  | a small badge: the format badge beside a plugin name, a strip's binding or capture source chip, and the hardware badge in the EQ editor and the Ducking window |
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
| `Ox.Text.Info` | brush | #5ba8f5 |  | informational lines: an available update, and the hardware badge's lettering in the EQ editor and the Ducking window |
| `Ox.Link.Foreground` | brush | #5ba8f5 |  | a link |
| `Ox.Link.ForegroundPointerOver` | brush | #8cc4ff |  | a link under the pointer |

A colour given to one channel or mix in the layout editor paints that
item's name and icon in place of `Ox.Text.Primary`, in the UI, the
terminal mixer and on Stream Deck keys, whatever the skin. It is part of
the mixer layout, not of a skin
([Saved mixer layout](mixer-layout.md#appearance)).

### Typography

| Token | Kind | Default | Range | What it sets |
|---|---|---|---|---|
| `Ox.Label.FontSize` | number | 12 | 8 to 24 | section headings and labels |
| `Ox.Label.FontWeight` | weight | SemiBold |  | those same headings and labels |
| `Ox.Title.FontSize` | number | 20 | 10 to 40 | the UI's own title line |
| `Ox.Title.FontWeight` | weight | Bold |  | that title line |
| `Ox.Strip.FontWeight` | weight | SemiBold |  | the name over a channel strip or a mix |
| `Ox.Value.FontFamily` | family | monospace |  | the numeric readouts |
| `Ox.Font.Family` | family | framework |  | the font the buttons, labels and dropdowns use |

### Indicators

These six reach the UI through code as well as through markup, so they
must be flat colours. A gradient or an image in one of them is refused
with a message saying why.

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Led.On` | colour | #3ecf7a |  | a connected device, a plugin that is processing, the button of a chain with one, a microphone's **EQ** button while its EQ runs, a matrix row's **Mic** button while one of its switches is on |
| `Ox.Led.Off` | colour | #4a4f5c |  | a device that is not there |
| `Ox.Led.Alert` | colour | #ff3c4e |  | a plugin bypassed or failed, the button of a chain with nothing processing, a microphone's **EQ** button while its EQ cannot run, the phantom power lamp on a matrix row's **Mic** button while phantom power is on |
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
| `Ox.Meter.VerticalWidth` | number | 4 | 2 to 24 | how thick an upright meter is, where a strip template stands its meter up; it is as long as what it stands beside |
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
has actually reached. A meter never repaints itself end to end. A
continuous meter draws the lit part as one bar: only its two ends are
rounded, and the zone edges sit on whole pixels, so where the colours are
the same the bar is unbroken. A cell of
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

### Application icons

Each application in APPLICATIONS shows its icon before its name, found in
the desktop's icon theme. An application the theme has no icon for gets a
badge of the same size with the first letter of its name, so the rows stay
lined up. In APPLICATIONS' columns the name has a width of its own as well,
the same in every tile.

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.AppIcon.Size` | number | 18 | 12 to 48 | the square an application's icon is drawn in |
| `Ox.AppIcon.Background` | brush | #353a47 |  | the badge drawn instead of an icon the desktop does not have |
| `Ox.AppIcon.Foreground` | brush | #b9bfcc |  | the application's initial on that badge |
| `Ox.AppIcon.CornerRadius` | radius | 4 | 0 to 48 | that badge's corners |
| `Ox.AppIcon.FontSize` | number | 11 | 6 to 32 | the size of that initial |
| `Ox.AppColumns.NameWidth` | number | 128 | 64 to 400 | the width of an application's name in a tile of APPLICATIONS' columns, the same in every tile; a longer name is clipped and scrolls to its end while the pointer is over its tile |

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

### Microphone EQ editor

The EQ window's response graph: the six regions shaded in turn and named
along the top, a grid every 6 dB and at the region edges, each band's own
curve thin in its colour, the sum of the four thick over a light fill to
0 dB, and a numbered handle per band, hollow while the band is off. The
graph draws in code, so its colours are flat, like the indicators. The
window's switches, type pickers, number fields and buttons are the
ordinary controls in the skin's appearance for them.

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Eq.Background` | colour | #14161b |  | the EQ graph's ground, and the number on a band's handle |
| `Ox.Eq.Grid` | colour | #2d313c |  | the graph's lines every 6 dB and at the region edges, and the readout's outline |
| `Ox.Eq.GridLabel` | colour | #8b93a7 |  | the axis labels, the readout and the 0 dB line |
| `Ox.Eq.ZoneA` | colour | #00000000 |  | the first, third and fifth regions, over the ground |
| `Ox.Eq.ZoneB` | colour | #0affffff |  | the second, fourth and sixth regions, over the ground |
| `Ox.Eq.ZoneLabel` | colour | #6b7285 |  | the regions' names along the top |
| `Ox.Eq.Curve` | colour | #e6e9f0 |  | the sum of the four bands while the EQ is on |
| `Ox.Eq.CurveFill` | colour | #335ba8f5 |  | the fill between that sum and 0 dB |
| `Ox.Eq.Band1` | colour | #5ba8f5 |  | band 1: its handle, its own curve and its number in the rows |
| `Ox.Eq.Band2` | colour | #3ecf7a |  | band 2, likewise |
| `Ox.Eq.Band3` | colour | #e0a05a |  | band 3, likewise |
| `Ox.Eq.Band4` | colour | #c958e5 |  | band 4, likewise |
| `Ox.Eq.HandleOff` | colour | #6b7285 |  | the number on the handle of a band that is off, and the sum while the EQ is off or compared |
| `Ox.Eq.HandleRing` | colour | #e6e9f0 |  | the ring around the selected band's handle |
| `Ox.Eq.CurveThickness` | number | 2.5 | 1 to 8 | the width of the sum's line |
| `Ox.Eq.BandCurveThickness` | number | 1.25 | 0.5 to 6 | the width of each band's own curve |
| `Ox.Eq.HandleSize` | number | 18 | 12 to 48 | the diameter of a band's handle; the pointer takes it within 20 units of its centre, or the compact control minimum under Touch sizing |
| `Ox.Eq.LabelFontSize` | number | 10 | 8 to 24 | the size of the axis labels, the region names and the readout, and of the hardware badge's text in the EQ editor and the Ducking window |
| `Ox.Eq.CornerRadius` | radius | 6 | 0 to 48 | the graph's corners, and the hardware badge's in the EQ editor and the Ducking window |

### Mixer control sizing

These size the main mixer's controls and strips. Options offers two
sizings, saved in `ui.json` as `"touchControls"`: Standard wears the values
below, and Touch raises the five control minimums to 44 units, a channel
strip to 180, a mix tile to 280, an upright strip to 96, and a mix
matrix's name column, cell and row to 132, 200 and 52. A skin value
larger than that is kept in both. While Touch is on, or while a skin sets
`Ox.Mixer.ControlMinSize`, a slider is also made at least as tall as the
control minimum and the `Ox.Fader.Thumb.Height` cap, so an enlarged cap
stays inside its row. The other windows and the plugins' own editors keep
their sizes.

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Mixer.ControlMinSize` | number | framework | 0 to 64 | the smallest width and height of a mixer button, dropdown, check box, text box, slider and fader cap |
| `Ox.Mixer.SliderMinHeight` | number | framework | 0 to 96 | the smallest height of a mixer slider |
| `Ox.Mixer.DeviceSliderHeight` | number | 30 | 30 to 96 | the height of a slider in the interface's control rows |
| `Ox.Mixer.SmallControlMinSize` | number | 0 | 0 to 64 | the smallest size of the compact controls: the interface rows' buttons and sliders, the tile headers and their buttons, the Arrange buttons and drag handles, the gain lock, the binding and capture source chips, the inserts buttons, the SUBMIXER header's buttons, a send's mute and the EQ graph's handle target |
| `Ox.Mixer.InsertControlMinSize` | number | 24 | 16 to 64 | the smallest width and height of an insert row's own process, bypass and controls buttons |
| `Ox.Mixer.ChannelWidth` | number | 132 | 132 to 400 | the width of a submixer channel strip |
| `Ox.Mixer.MixWidth` | number | 232 | 232 to 500 | the width of a mix master tile |
| `Ox.Mixer.StripWidth` | number | 76 | 56 to 200 | the width of an upright strip in a mix bank, the name column of a send list, and the unit an input rack and mix banks reckon their narrowest width in |
| `Ox.Mixer.VerticalFaderLength` | number | 160 | 80 to 480 | the length of an upright fader |
| `Ox.Mixer.BankSpacing` | number | 12 | 0 to 64 | the gap between mix banks |
| `Ox.Mixer.HiddenOpacity` | number | 0.5 | 0.2 to 1 | how far a channel or mix hidden in the layout editor fades while the submixer's Show hidden draws it, 1 for no fade |

### Mix matrix

These draw the `"mix-matrix"` template (see [The mix matrix](#the-mix-matrix)).
Every cell is the same size, so the columns line up down the matrix and the
rows across it; a row head is a name column and one cell's width of source
controls.

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Matrix.NameWidth` | number | 140 | 56 to 400 | the width of a row head's name column, which holds the name, the binding line under it, the **Mic** button and the gain under it |
| `Ox.Matrix.CellWidth` | number | 168 | 120 to 400 | the width of a cell, of a column head and of a row head's source controls |
| `Ox.Matrix.RowHeight` | number | 40 | 28 to 120 | the height of a row: its head and every cell in it; a row whose head stacks a gain over the channel volume, or the **EQ** button over the chain's, is as tall as the two need, never less |
| `Ox.Matrix.MutedBackground` | brush | #40a03434 | | the background of a cell whose send is muted, when the matrix tints muted cells. Translucent by default, so the cell's own surface shows through as a red tint |
| `Ox.Matrix.MutedForeground` | brush | #f6dede | | the lettering of a muted send's mute in a tinted cell |
| `Ox.Matrix.MutedOpacity` | number | 0.5 | 0.1 to 1 | how far a muted send's fader and level fade when the matrix dims muted cells; the mute keeps its own colour |

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

Images are paintable wherever a brush is: a faceplate behind the UI, a
texture on a card, a metal face on a key. They are not available to a
token of kind colour.

## Icons

Every channel and mix wears an icon from the set OpenXLR ships, chosen in
the layout editor ([mixer-layout.md](mixer-layout.md#appearance)). The
icon is layout data, so it stays the same whichever skin is worn, and each
template draws it where it shows the item's name. A skin can add icons of
its own and redraw a built-in one with an optional `icons` object:

```json
"icons": {
  "mic": { "path": "M12 2a3 3 0 0 1 3 3v6a3 3 0 0 1 -6 0v-6a3 3 0 0 1 3 -3z M7 20h10v2h-10z" },
  "on-air": { "path": "M2 6h20v12h-20z M5 9v6h3v-6z" }
}
```

A name is a lower-case letter followed by up to 31 lower-case letters,
digits or hyphens, and `none` is reserved for "no icon". A name from the
built-in set replaces that icon's drawing while the skin is worn; any other
name adds an icon, which the layout editor's picker lists after the
built-in set. The built-in names are `mic`, `microphone`, `headset`, `speaker`, `jack`,
`music`, `game`, `browser`, `chat`, `people`, `broadcast`, `camera`,
`bell`, `star`, `globe`, `cog`, `film`, `voice`, `podcast`, `record`,
`apps`, `burst` and `monitor`.

`path` is SVG path data in a 24 by 24 box with the origin at the top
left, filled with the text colour of the name beside it, or with the
item's own colour when the user chose one. It is filled under SVG's
default nonzero rule, so a hole is drawn in the opposite direction to the
shape around it. Only the commands M, L, H, V, C, S, Q, T, A and Z, in
either case, with plain numbers are accepted, and the data starts with a
move. There is no stroke, no fill of its own and no reference to anything
outside the string.

The rules an `icons` object is held to:

- a skin may define at most 64 icons; the rest are reported and ignored;
- a path is at most 4096 characters;
- an entry is an object with a `path` string. Other fields are reported
  and ignored;
- a name given twice is reported and neither entry is used.

A bad entry is reported in Options and that one icon is dropped; an item
that names it wears the built-in icon of that name, or its default. Skin
icons reach only the UI. The terminal mixer and the Stream Deck
plugin draw the built-in icon of the same name if there is one, and the
item's default otherwise, so a layout that names a skin's icon still reads
on every client.

## Layout

A skin can also say where the UI's parts go: the header and the
five sections, placed in a tree of stacks, grids and docks, with a few
options for each of them. That is the optional `layout` object, beside
`tokens` and `controls`. It moves what the application builds; it never
adds a control, a binding or a picture of its own.

The shipped Broadcast skin is the complete example: a palette, control
appearances and a layout that uses the templates below, in
[docs/examples/skins/broadcast](examples/skins/broadcast/skin.json). Its
arrangement is inspired by Wave Link's; Broadcast has no connection to
Elgato and is not endorsed by it. Its tree is a vertical
stack. First comes a grid of two columns, the second 360 pixels wide.
HEADPHONES fills its top left cell, MONITOR's `"output-toolbar"` sits in
its top right cell, and the submixer, a `"mix-matrix"`, spans both columns
under them.
Then come INPUTS, `"hidden"` since the matrix's row heads carry the input
gain, mute, microphone switches and plugins, and the applications as `"application-columns"`
of 6, each across the whole width. Nothing starts folded. The tree leaves the
header out, so the header stays across the top of the UI, with every
action in its row and none in the overflow menu. Showing INPUTS from
Arrange mode puts it under the matrix. HEADPHONES beside the toolbar asks
for about 1000 pixels, so a narrower window scrolls the tree sideways, as
[Sizes](#sizes) says.
Its row heads list every part, `binding` included, so a copy shows each
part there is to move or hide. The shipped Wave skin uses the same tree
and regions, with 8 pixel gaps in place of 12.

A complete skin that puts the header across the top, INPUTS beside the
submixer, the headphones and the monitor under INPUTS, and the
applications along the bottom, folded at first:

```json
{
  "schema": 1,
  "id": "console-grid",
  "name": "Console grid",
  "tokens": {
    "Ox.Card.Background": "#1d2027"
  },
  "layout": {
    "version": 1,
    "main": {
      "kind": "grid",
      "id": "desktop",
      "spacing": 12,
      "columns": [{ "size": 640 }, { "star": 1 }],
      "rows": ["auto", "auto", "auto", "auto"],
      "children": [
        { "kind": "region", "region": "header", "row": 0, "column": 0, "columnSpan": 2 },
        { "kind": "region", "region": "inputs", "row": 1, "column": 0 },
        { "kind": "region", "region": "submixer", "row": 1, "column": 1, "rowSpan": 2 },
        {
          "kind": "stack",
          "id": "outputs",
          "orientation": "vertical",
          "row": 2,
          "column": 0,
          "children": [
            { "kind": "region", "region": "headphones" },
            { "kind": "region", "region": "monitor" }
          ]
        },
        { "kind": "region", "region": "applications", "row": 3, "column": 0, "columnSpan": 2 }
      ]
    },
    "regions": {
      "applications": { "visibility": "collapsed" },
      "header": { "actions": ["flow", "profiles"], "overflow": ["restart-daemon", "about"] }
    }
  }
}
```

And one that docks the header at the bottom, INPUTS down the left side,
the headphones and the monitor in a row along the top and the submixer in
what is left, with the applications hidden until you show them:

```json
{
  "schema": 1,
  "id": "docked",
  "name": "Docked",
  "tokens": {
    "Ox.Card.Background": "#20232a"
  },
  "layout": {
    "version": 1,
    "main": {
      "kind": "dock",
      "id": "window",
      "spacing": 8,
      "children": [
        { "kind": "region", "region": "header", "dock": "bottom" },
        { "kind": "region", "region": "inputs", "dock": "left" },
        {
          "kind": "stack",
          "id": "outputs",
          "orientation": "horizontal",
          "dock": "top",
          "children": [
            { "kind": "region", "region": "headphones" },
            { "kind": "region", "region": "monitor" },
            { "kind": "region", "region": "applications" }
          ]
        },
        { "kind": "region", "region": "submixer", "dock": "fill" }
      ]
    },
    "regions": {
      "inputs": { "width": 680 },
      "applications": { "visibility": "hidden" }
    }
  }
}
```

The block has a version of its own, `"version": 1`, separate from the
file's `schema`. A newer layout can then come along without making the
rest of the skin unreadable to an older OpenXLR.

| Field | Required | What it is |
|---|---|---|
| `version` | yes | the layout format this block is written for, `1` |
| `main` | no | the UI's tree: a stack, a grid or a dock. Without it the sections keep the classic arrangement |
| `regions` | no | options for the header and the sections, by region id |

### The tree

`main` and every container inside it is a stack, a grid or a dock, and
every container has these fields:

| Field | Required | What it is |
|---|---|---|
| `kind` | yes | `"stack"`, `"grid"` or `"dock"` |
| `id` | yes | 1 to 32 lowercase letters, digits and hyphens, unique in the tree. Your own arrangement names its places by it |
| `spacing` | no | the gap between children in pixels, from 0 to 64; 8, the classic gap, when it is missing |
| `children` | yes | regions and containers; a container holds no more than 16 children |

A stack puts its children one after another:

| Field | Required | What it is |
|---|---|---|
| `orientation` | yes | `"vertical"` puts them one above the other, the classic arrangement. `"horizontal"` puts them side by side in one row |

A grid puts its children in cells:

| Field | Required | What it is |
|---|---|---|
| `rows` | yes | 1 to 8 tracks, top to bottom |
| `columns` | yes | 1 to 8 tracks, left to right |

A track is `"auto"`, as large as what it holds; `{ "star": n }`, a share of
what the other tracks leave, with `n` from 0.1 to 10; or `{ "size": n }`, a
fixed size in pixels from 40 to 2000. Each child of a grid says where it
goes:

| Field | Required | What it is |
|---|---|---|
| `row` | yes | its row, counted from 0 |
| `column` | yes | its column, counted from 0 |
| `rowSpan` | no | how many rows it covers, 1 when it is missing, and never past the last row |
| `columnSpan` | no | how many columns it covers, 1 when it is missing, and never past the last column |

Two children never cover the same cell. A cell can stay empty.

A dock puts its children against its edges, in order, and the last one
fills what is left. Each child of a dock says which edge:

| Field | Required | What it is |
|---|---|---|
| `dock` | yes | `"top"`, `"bottom"`, `"left"`, `"right"` or `"fill"`. Exactly one child fills, and it is the last |

A child that is not a container is a region:

| Field | Required | What it is |
|---|---|---|
| `kind` | yes | `"region"` |
| `region` | yes | which part of the UI goes here, by the ids below |

| Region | What it is |
|---|---|
| `header` | the header: Options, the interface status and the header actions |
| `inputs` | INPUTS: the gain, the input binding, the input switches, the EQ and the microphone inserts |
| `headphones` | HEADPHONES: the headphone levels and the direct monitor blend |
| `monitor` | MONITOR: the output picker and its volume |
| `applications` | APPLICATIONS: the running applications and their channels |
| `submixer` | SUBMIXER: the channel strips and the mix masters |

`main` places each of the five sections exactly once, and the header at
most once. A tree that leaves the header out sits under it. The header
stays at the top of the UI, across its whole width, and the tree goes
below it, as in the classic arrangement. A stack of the five sections without
the header therefore looks the way it always has. A tree that places the
header puts it where it says.

A tree holds no more than 64 nodes, containers and regions together.
Containers nest at most 4 deep, `main` included, and a grid has at most 8
rows and 8 columns. Options in the header is always shown, so the way back
is always reachable wherever the header goes. The notes the application
shows, such as an update or a daemon left running across an upgrade, stay
above the tree: under the header when the tree leaves it out, at the top
of the UI when the tree places it.

A section the UI sets aside, such as INPUTS, HEADPHONES and
APPLICATIONS in the mini view, or MONITOR without the software mixer,
stays hidden wherever the skin puts it. A skin can hide a section; it can
never bring back one the UI hides.

### Sizes

Wherever a container gives a region a width of its own, in a horizontal
stack, a grid cell or a dock's left or right side, the region is at least
600 pixels wide, the width every section is held to fit. An input rack and
mix banks need less, and say how much under [Templates](#templates). A
region in a vertical stack, or along a dock's top or bottom, or filling
it, takes the container's width. A `size` column smaller than what its region needs
grows to fit, and a column of `auto` is as wide as its widest region.

When the tree needs more width than the window has, it scrolls sideways
instead of squeezing a region. With the header above the tree the header
keeps the window's width; with the header inside the tree it scrolls with
the rest. A horizontal stack at the top of the tree shares the window's
width among its regions; one inside another container gives each region
the width it needs.

Heights follow the contents, since the page scrolls downwards. A `size`
row is the least it takes and grows with what it holds, and a `star` row
is as tall as its contents, like an `auto` one. A region lines up along
the top of its cell or side, so a collapsed section is just its header and
does not stretch to the height of the one beside it.

### Region options

`regions` holds an object for each region it sets options for, by region
id, with only the options that region has:

| Option | Region | What it is |
|---|---|---|
| `visibility` | the five sections | `"visible"`, the default. `"collapsed"` folds the section the first time the UI wears the skin, and after that the section stays as you leave it. `"hidden"` leaves it out of the UI and out of the tab order until you show it from Arrange mode |
| `width` | the five sections | a preferred width in pixels, from 120 to 4000 |
| `height` | the five sections | a preferred height in pixels, from 120 to 4000 |
| `actions` | `header` | header action ids in the order the header shows them |
| `overflow` | `header` | header action ids to put in the ⋯ menu at the end of the header instead |

A preferred width applies where the region has a width of its own: in a
horizontal stack, a grid cell and a dock's left or right side. It never
goes below the 600 pixels a section needs, and in a vertical stack, or
along a dock's top or bottom, it is ignored and the section takes the
container's width. In a grid the region is that wide at the start of its
cell, and its column is at least that wide. A preferred height is the
least the section takes; it grows past it with its contents and never
cuts them off.

The header actions:

| Action | What it is |
|---|---|
| `profiles` | Profiles and its menu |
| `arrange` | the Arrange toggle |
| `flow` | Flow |
| `restart-daemon` | Restart daemon |
| `version` | the version number |
| `about` | About |

An action appears at most once across `actions` and `overflow`. One left
out of both keeps its usual place after the listed ones. Options is not
an action a skin places. It is always first in the header and always
shown. The actions in the ⋯ menu are the same controls, with the same
names for a screen reader; Profiles opens its own menu from there.

### Templates

INPUTS, MONITOR, APPLICATIONS and the submixer can each be drawn with
another template, chosen by `template` in their entry under `regions`. A template is markup the
application owns: the skin names it, orders the parts it has and leaves
some out. It cannot add a control, bind one to something else or move one
out of its template, so a phantom power switch stays with its input and a
send fader stays a send fader.

| Option | Template | What it is |
|---|---|---|
| `template` | `inputs`, `monitor`, `applications`, `submixer` | which template draws the section. INPUTS takes `"hardware-panel"`, the INPUTS panel as it has always been and the default, or `"input-rack"`. MONITOR takes `"output-panel"`, the default, or `"output-toolbar"`. APPLICATIONS takes `"application-chips"`, the default, `"application-list"` or `"application-columns"`. The submixer takes `"channel-tiles"`, the channel tiles and mix masters as they have always been and the default, `"mix-banks"` or `"mix-matrix"` |
| `strip` | `"input-rack"`, `"mix-banks"` | how one strip of the rack or of a bank is drawn, below |
| `orientation` | `"mix-banks"` | `"horizontal"`, the default, puts the banks side by side; `"vertical"` one above the other |
| `visibleBanks` | `"mix-banks"` | how many banks show, from 1 to 4; 2 when it is missing |
| `selectionDefaults` | `"mix-banks"` | the mixes the banks start on, by the id `mixer.json` gives them, such as `"monitor"` and `"stream"`: at most 16 mix ids, each 1 to 64 lowercase letters, digits, hyphens and underscores |
| `master` | `"mix-banks"` | how each bank's master is drawn, below |
| `perColumn` | `"application-columns"` | how many applications one column holds before the next column starts, from 1 to 24; 6 when it is missing |
| `matrix` | `"mix-matrix"` | how the matrix's row heads, column heads and cells are drawn; see [The mix matrix](#the-mix-matrix) |
| `popOut` | `submixer`, any template | where the submixer starts out: `{ "default": "docked" }`, the default, or `{ "default": "detached" }`. See [The mixer's own window](#the-mixers-own-window) |

An option that belongs to another template, such as `strip` with
`"hardware-panel"`, `visibleBanks` without `"mix-banks"`, `matrix`
without `"mix-matrix"` or `perColumn` without `"application-columns"`, drops
the layout like any other fault. MONITOR takes `template` and the options
every section has, nothing else; APPLICATIONS takes those and `perColumn`;
HEADPHONES has no template.

MONITOR's toolbar is one compact row, meant for a footer or a slot beside
the header: the output picker, the volume and its readout. The panel's
labels go, and stay the controls' names for a screen reader and the
volume's tooltip. The picker opens the same list the panel's does, so every
output stays reachable, each with its tick and, while it is ticked, the
mix it hears. The 150% switch and the help stay in the section's header.
Below 560 pixels of room the toolbar stands in two rows, the picker above
the volume, exactly as the panel does below the same width. MONITOR has no
mute of its own in either template; an output's monitor mixes are muted
from their mix masters.

APPLICATIONS' list is one line per running application, meant for a narrow
column: its state lamp, its name, which shortens to fit and shows whole in
its tooltip, and its channel picker. The picker assigns the application to
a channel exactly as a chip's does. Levels and sends stay with the
channels; the list only assigns.

APPLICATIONS' columns are tiles of one width, in columns: the first
`perColumn` applications top to bottom, the next ones in a column to the
right, in the order the chips of the default template wrap in. A tile holds
what a chip holds: its state lamp, its icon, its name and its channel
picker. Each part is the same width in every tile, so the tiles of a column
line up and so do their pickers. The name is
`Ox.AppColumns.NameWidth` wide, 128 by default, which holds about sixteen
characters such as "Telegram Desktop". A longer name is clipped; while the
pointer is over its tile it glides once to its end and stays there, and
glides back when the pointer leaves. Nothing moves at rest, and the tooltip
shows the whole name. The glide does not yet follow a desktop setting to
reduce motion, since the UI toolkit does not report one. The picker is as wide as the longest channel it
lists. Columns that do not fit the section's width start a new row of
columns below, whole, so nothing scrolls sideways; a window narrower than
two columns shows them one under the other. The default template's chips
keep the width of their own name and channel.

The input rack draws one strip per channel, in the mixer's channel
order. The strip of an XLR input holds the parts the INPUTS panel shows for
it: its gain with the gain lock, its switches and its plugin chain. The
strip of Aux In holds its level and level lock. These are the INPUTS
panel's own controls, so a switch the interface does not have stays
hidden, the gain lock still disables both gains, a mute still waits while
phantom power settles, and XLR 2 shows only on an interface with two XLR
inputs. Every channel's `gain` part also holds the channel's own volume,
which scales every send it makes: under the gain or the level on XLR 1,
XLR 2 and Aux In, alone on a strip without one. An application or capture
channel has no hardware gain and no chain of its own, so its strip has its
name, its meter and that volume whatever the skin orders. A daemon that
reports no volume for the channel leaves the strip without it.

Mix banks draw the submixer by mix instead of by channel. Each bank
shows one mix: the send of every channel into that mix, then the mix's
master. Every bank has a picker at its top that lists every mix not hidden
in the layout editor, so Monitor B, Aux and the mixes you added stay
reachable with two banks. The skin
only says which mixes the banks start on:

- A bank starts on the mix you last picked for it under this skin. Without
  a pick of yours it takes the next mix in `selectionDefaults`, skipping an
  id that no mix has, and then the next mix in the mixer's order that no
  other bank shows.
- Picking a mix another bank shows trades the two banks' mixes, so two
  banks never show one mix. Your picks are kept in `ui.json` under
  `skinBankSelections`, one entry for each skin and layout version. The 32
  most recent skins are kept.
- When a bank's mix is deleted, or the view stops showing it, the bank
  moves on to the next mix in the mixer's order that no other bank shows.
  It never creates, deletes, renames, reorders or reroutes a mix.
- There are never more banks than mixes the view shows. In the mini view
  that is the one mix it shows; in the compact view each bank shows only
  the chosen channel.

A bank's pick changes nothing in the mixer. Channel and mix order stay the
layout's one order, set in the layout editor or with Arrange, and every
strip writes to the same send, mix and mute the channel tiles do.

A meter drawn beside a send is the channel's own meter, before its send.
It is named that way for a screen reader. No template shows the level of a
send after its fader.

#### Strips and their parts

| Strip option | What it is |
|---|---|
| `template` | the strip template. An input rack takes `"input-row"`. A bank takes `"vertical-strip"`, the default, which stacks a send's parts in a column and stands the strips side by side, or `"send-list"`, which puts them in a row and the rows one above the other |
| `partsOrder` | part names in the order the strip draws them. Parts it leaves out follow in their default order |
| `hidden` | part names the strip leaves out. A hidden part is not drawn and is out of the tab order |
| `label` | `{ "style": ... }`, how the strip names its channel, below |
| `meter` | `{ "orientation": ..., "placement": ... }`, below |
| `fader` | `{ "orientation": ..., "direction": ... }`, below. Bank strips only, since an input row's gain is the interface's and lies flat |

An input row's parts, in their default order, are `identity`, `binding`,
`meter`, `gain`, `hardware-controls` and `effects`. A bank strip's are
`identity`, `meter`, `fader`, `value` and `mute`.

| Part | What it is |
|---|---|
| `identity` | the channel's icon and name. It cannot be hidden |
| `binding` | XLR 1's or XLR 2's input binding: a chip naming the interface's jack or the capture source that feeds the strip, with a light that is on while the feed is linked and, under it, why the strip is silent. Clicking opens the binding dialog. A capture input has a source chip with the same light and warning, opening the dialog's capture form with mono ports and stereo pairs |
| `meter` | the channel's own level meter, left and right. A capture input on one microphone port draws one bar and leaves the second bar's room empty |
| `gain` | the input's hardware gain or Aux In's level, with the channel's own volume under it; on an application or capture channel, its own volume |
| `hardware-controls` | the input's switches, or Aux In's level lock |
| `effects` | the input's processing in software: the microphone's **EQ** button, which opens its EQ editor, then its plugin chain's button and the chain at a glance. A capture input has the chain without EQ, only when the daemon reports `chainWidth` |
| `fader` | the send's level into the bank's mix |
| `value` | that level as a number |
| `mute` | the send's mute in the bank's mix |

Each list holds no more names than the strip has parts, and a part
appears once across `partsOrder` and `hidden`. A part a channel does not
have is skipped, so ordering `hardware-controls` first changes nothing on an
application channel.

The interface's gain and switches are drawn on the strip that carries the
interface's microphone. That is XLR 1 or XLR 2 while the strip takes the
interface's jack. While the strip is bound to another source, it is a
capture channel on the interface's own node at that jack's port, whose
`gain` part then shows the interface's gain above the channel's own volume
and whose `hardware-controls` part shows the switches. A strip bound to
another source keeps only its own volume in `gain`, and XLR 1's
`hardware-controls` keeps only its software low cut and ClipGuard, unless
the source is an additional
interface the daemon drives: then the strip's `gain` part shows that
interface's gain and its `hardware-controls` part that interface's
switches, as a capture channel on that interface's node does. A matrix row
head draws such an interface's gain and mute in its `gain` and `mute`
parts. Hiding a part hides the additional interface's control with it.
Capture inputs also have `binding` and `effects`. When an input rack's
`strip.partsOrder` is explicit, these two parts appear on capture inputs
only if that list names them, and a strip without `binding` keeps its
connection line under the name. The listed order stays as written, and a
part named in `hidden` stays hidden. Without an explicit order, both parts
take their default places. On a capture input on one mono port, `effects`
holds the microphone EQ button before the chain, as on XLR 1. Against an
older daemon that does not report `chainWidth`, a capture input has no
`effects`.

`master` takes `template`, `label`, `meter` and `fader`. Its template is
`"master-card"`, the mix tile the channel tiles have always shown, under
the bank's strips, and the default; or `"master-strip"`, one more strip at
the end of the bank, on the right of upright strips or under a send list.
Both keep the master's level, mute, 150% range for a monitor mix, the
button that opens its plugin chain and the To USB Aux port switch. The
master card also shows the chain at a glance; the master strip shows one
lamp per plugin, lit in the alert colour when a plugin fails, and its
bypass switches are in the chain window. A master's parts stay in their
default order.

#### Labels, meters and faders

| Value | What it is |
|---|---|
| `label.style` | `"text"`, the name, the default; `"icon-and-text"`, the icon chosen in the layout editor and then the name; `"icon"`, the icon alone, or the name for a channel without an icon. A screen reader hears the whole name in every style |
| `meter.orientation` | `"horizontal"`, a flat meter that fills from the left, or `"vertical"`, an upright one that fills from the bottom. Both read the same scale with the same zones |
| `meter.placement` | `"below-label"`, a part of its own where `partsOrder` puts it, which by default is straight under the name; `"beside-label"`, beside the name in the name's place; `"beside-fader"`, beside the fader in the fader's place, or beside the gain on an input row. With that part hidden the meter is a part of its own again |
| `fader.orientation` | `"horizontal"` or `"vertical"` |
| `fader.direction` | `"normal"`, the maximum at the top or at the right, the default; `"reversed"`, the maximum at the bottom or at the left. The arrow keys and Shift-drag for fine steps follow the direction |

A `"vertical-strip"` and a `"master-strip"` stand the fader and the meter
up, with the meter beside the fader, unless the skin says otherwise. Every
other strip lays them flat with the meter under the name. An upright fader
is as long as `Ox.Mixer.VerticalFaderLength`, an upright meter as thick as
`Ox.Meter.VerticalWidth`, an upright strip as wide as
`Ox.Mixer.StripWidth`, and the banks stand `Ox.Mixer.BankSpacing` apart.
The console fader and every meter presentation draw upright as well as
flat.

#### Narrowest widths

Where a container gives the section a width of its own, the classic
templates keep their 600 pixels. The others need less:

- an input rack is three times `Ox.Mixer.StripWidth` wide, plus the card's
  padding: 252 pixels with the default tokens;
- an output toolbar and an application list need the same three strip
  widths and the padding, the toolbar in its two rows;
- mix banks need each bank's width side by side, with
  `Ox.Mixer.BankSpacing` between them, or one bank's width for banks one
  above the other. A bank of upright strips needs one channel strip and,
  with a master strip, the master beside it;
  a send list needs three strip widths; a master card needs
  `Ox.Mixer.MixWidth`. More strips than fit scroll sideways inside the
  bank;
- a mix matrix needs its row heads, `Ox.Matrix.NameWidth` and
  `Ox.Matrix.CellWidth`, and one column beside them, plus the card's
  padding: 504 pixels with the default tokens. More columns than fit scroll
  sideways inside the card.

None asks for more than the classic 600 pixels, and Touch sizing raises
`Ox.Mixer.StripWidth` to 96, which widens them all. A popped out mixer window
holding banks is as wide as the banks need, and one holding a matrix as
wide as the whole matrix, every column beside the row heads; either scrolls
sideways when the window is narrower.

#### A complete example

INPUTS as a rack in a 280 pixel column on the left, two banks of upright
strips on the right starting on Monitor A and Stream, and the other
sections below:

```json
{
  "schema": 1,
  "id": "rack-and-banks",
  "name": "Rack and banks",
  "tokens": {
    "Ox.Card.Background": "#22252b",
    "Ox.Mixer.BankSpacing": 16
  },
  "controls": { "fader": "console" },
  "layout": {
    "version": 1,
    "main": {
      "kind": "grid",
      "id": "desktop",
      "columns": [{ "size": 280 }, { "star": 1 }],
      "rows": ["auto", "auto", "auto"],
      "children": [
        { "kind": "region", "region": "header", "row": 0, "column": 0, "columnSpan": 2 },
        { "kind": "region", "region": "inputs", "row": 1, "column": 0 },
        { "kind": "region", "region": "submixer", "row": 1, "column": 1 },
        {
          "kind": "stack",
          "id": "lower",
          "orientation": "vertical",
          "row": 2,
          "column": 0,
          "columnSpan": 2,
          "children": [
            { "kind": "region", "region": "headphones" },
            { "kind": "region", "region": "monitor" },
            { "kind": "region", "region": "applications" }
          ]
        }
      ]
    },
    "regions": {
      "inputs": {
        "template": "input-rack",
        "strip": {
          "template": "input-row",
          "partsOrder": ["identity", "binding", "meter", "gain", "hardware-controls", "effects"],
          "label": { "style": "icon-and-text" },
          "meter": { "orientation": "horizontal", "placement": "below-label" }
        }
      },
      "submixer": {
        "template": "mix-banks",
        "orientation": "horizontal",
        "visibleBanks": 2,
        "selectionDefaults": ["monitor", "stream"],
        "strip": {
          "template": "vertical-strip",
          "partsOrder": ["identity", "meter", "fader", "value", "mute"],
          "fader": { "orientation": "vertical", "direction": "normal" },
          "meter": { "orientation": "vertical", "placement": "beside-fader" },
          "label": { "style": "text" }
        },
        "master": { "template": "master-strip" }
      },
      "applications": { "visibility": "collapsed" }
    }
  }
}
```

In Arrange mode a channel has one handle. It is on the input rack's strip
while the rack shows, otherwise on the first bank's strip, never once per
bank. A drop still sends the new channel order to the daemon and the
strips move when it has saved it, as the channel tiles do. Mixes are
reordered in the layout editor while the submixer shows banks.

#### The mix matrix

A `"mix-matrix"` draws the submixer as one table. Each row is a channel and
each column a mix, both in the mixer's order, and where a row crosses a
column is a cell holding that channel's send into that mix: its mute and a
short fader, the channel's meter running under the fader as its track.
There is nothing to pick. The matrix always shows every channel and every
mix the view shows, so the compact view has one row and the mini view one
row and one column. A channel or mix hidden in the layout editor has no
row or column, and the cells under a hidden mix go with it, so the columns
stay lined up; Show hidden draws them faded to `Ox.Mixer.HiddenOpacity`.
A channel with no send into a mix, or one the view does not draw, has an
empty cell.

Each row starts with the channel's head: its icon and name, then the same
controls the INPUTS panel shows for the channel, under the same rules. XLR 1
and XLR 2 have their hardware gain, with the meter under it, their mute,
which waits while phantom power settles, and the button that opens their
plugin chain. The gain lock still disables the gain. At the end of the name
column a **Mic** button opens a flyout with the switches INPUTS shows beside
the mute: low cut, expander, Voice Tune, phantom power, ClipGuard and the
compressor, each where the interface has it, with the Voice Tune strength
while Voice Tune is on and, on XLR 1, the software low cut and ClipGuard of
an interface without hardware ones. They are the INPUTS panel's own buttons,
bound to the same settings, so a click there does what it does in INPUTS.
The mute is not repeated in the flyout, since the head has its own. **Mic**
is lettered in `Ox.Led.On` while one of the switches is on, and carries the
interface's phantom power lamp, lit in `Ox.Led.Alert` while phantom power is
on or settling, so both show without opening it. A capture channel carrying
the interface's microphone has the button too, with that input's switches,
and a strip carrying an additional interface's input has that interface's
switches. Aux In has its level
and level lock. Every channel has its own volume, which scales every send
in its row. On XLR 1, XLR 2 and Aux In it is a second fader under the gain
or the level, the meter under the gain, and that row is as tall as the two
faders need, never less than `Ox.Matrix.RowHeight`; its cells follow it.
An application or capture channel, or a strip bound to another source,
has the volume alone, with its meter under it. XLR 2 has a row only on an
interface with two XLR inputs.

Each column has a head: the mix's icon and name, an outputs line, a thin
line in the mix's own colour when the layout editor gave it one, and a row
of controls with the mix's master level, its mute and the button that opens
its plugin chain. The outputs line says how many outputs ticked in MONITOR
hear the mix, alone or summed with the other monitor mix. When none does it
says what the mix is: a monitor mix with no output, a virtual microphone or
the USB Aux port.

A muted send's cell is tinted with `Ox.Matrix.MutedBackground` and its mute
lettered in `Ox.Matrix.MutedForeground`, or, with `"muted": "dim"`, its
fader and level number are faded to `Ox.Matrix.MutedOpacity` while its
mute keeps its `Ox.Mute.BackgroundChecked` colour, so the cell greys out
and the mute still shows. Broadcast and Wave dim. The thumb of every
fader keeps the `controls.fader` appearance. The meter is the channel's
own, before its send, and a screen reader hears it by the channel's
name; the fader is named for the channel and the mix.

`matrix` takes these, all optional:

| Matrix option | What it is |
|---|---|
| `rowHead` | `{ "parts": [...], "hidden": [...], "label": { "style": ... } }`, how a row head is drawn |
| `columnHead` | `{ "parts": [...], "hidden": [...], "label": { "style": ... } }`, how a column head is drawn |
| `cell` | `{ "parts": [...], "hidden": [...], "muted": ..., "valuePlacement": ... }`, how a cell is drawn. `"muted"` is `"tint"`, the default, or `"dim"`. `"valuePlacement"` is `"beside"`, the default, the `value` part beside the fader in its part order, or `"over-fader"`, the level at the top centre of the cell, on `Ox.Control.Background` like the buttons, only while the fader is being moved, so the fader keeps the cell's width; a hidden `value` part draws nothing in either place |
| `meter` | `"under-fader"`, the default, the channel's meter drawn as the track of every fader in its row; or `"none"`, plain faders. With `"none"` a row head's `meter` part still shows the meter unless it is hidden |

`parts` lists part names in the order they are drawn; parts it leaves out
follow in their default order. `hidden` lists parts left out, and a part
appears once across the two lists. A row head's parts, in their default
order, are `identity`, `binding`, `meter`, `gain`, `mute`, `processing` and
`effects`. A
column head's are `identity`, `master`, `mute`, `effects` and `outputs`. A
cell's are `mute`, `fader` and `value`.

| Matrix part | Where | What it is |
|---|---|---|
| `identity` | row head, column head | the channel's or the mix's icon and name. It cannot be hidden. In a row head it is the name column, on the left unless `parts` puts it last |
| `binding` | row head | XLR 1's or XLR 2's input binding as a line under the name in the name column, wherever `parts` puts it: a light that is on while the feed is linked and the interface's jack or the source's name. The name and this line are one target that opens the binding dialog, and its tooltip says why the strip is silent. A capture input draws the same line for its source and opens the dialog's capture form |
| `meter` | row head | the channel's meter as a part of its own. Under the faders it is the gain's track instead, so a channel with gain draws it there |
| `gain` | row head | the input's hardware gain, or Aux In's level and level lock, with the channel's own volume as a second fader under it; a channel without a gain shows its volume alone. A capture channel carrying the interface's microphone shows the interface's gain over its volume |
| `mute` | row head, column head, cell | the input's hardware mute, the mix's mute, or the send's mute in the column's mix. A capture channel carrying the interface's microphone has the interface's mute |
| `processing` | row head | the **Mic** button that opens the input's switches in a flyout. It is lettered in `Ox.Led.On` while a switch is on, and its lamp lights while phantom power is on. It stands at the end of the name column, wherever `parts` puts it, so the source controls keep their room. A row whose input has no switch, and a channel that carries no interface's input, does not draw it. A skin that lists every other part and not this one still gets it |
| `effects` | row head, column head | the button that opens the input's or the mix's plugin chain, including a capture input whose daemon reports `chainWidth`. On XLR 1 and XLR 2 the **EQ** button, which opens the microphone's EQ editor, stands over it in the same part, as the signal meets them, so the row grows to hold both and the source controls keep their width |
| `master` | column head | the mix's master level as a short fader |
| `outputs` | column head | the outputs line |
| `fader` | cell | the send's level as a short fader. It cannot be hidden |
| `value` | cell | that level as a number |

For capture row heads, an explicit `rowHead.parts` list shows `binding`
and `effects` only when it names them, and a row without `binding` keeps
its connection line under the name. With no list, both use their default
places. The source line opens the binding dialog. A capture input on one mono port has the microphone EQ button over its chain's, as XLR 1 has; a stereo pair has none.
A capture input on one microphone port draws one meter bar, including under
the source and send faders; a stereo capture input draws two.

A column head's `identity` and `outputs` are its lines, in their order; its
`master`, `mute` and `effects` share one row of controls, in their order,
under the lines, or above them when `parts` names one of them first.

In Arrange mode a channel's handle is on its row head while the input rack
is not shown, and a mix's on its column head, once each, since every mix
appears once. A drop sends the new order to the daemon and the rows or
columns move when it has saved it, as with the tiles. Focus runs through
the column heads, then row by row, each row's head first.

A complete skin with a matrix under the header and the other sections
below it:

```json
{
  "schema": 1,
  "id": "matrix-desk",
  "name": "Matrix desk",
  "tokens": {
    "Ox.Matrix.CellWidth": 180,
    "Ox.Matrix.MutedBackground": "#55c0392b"
  },
  "controls": { "fader": "console" },
  "layout": {
    "version": 1,
    "main": {
      "kind": "stack",
      "id": "desk",
      "orientation": "vertical",
      "children": [
        { "kind": "region", "region": "submixer" },
        { "kind": "region", "region": "monitor" },
        { "kind": "region", "region": "inputs" },
        { "kind": "region", "region": "headphones" },
        { "kind": "region", "region": "applications" }
      ]
    },
    "regions": {
      "submixer": {
        "template": "mix-matrix",
        "matrix": {
          "rowHead": { "parts": ["identity", "mute", "gain"], "label": { "style": "icon-and-text" } },
          "columnHead": { "parts": ["identity", "outputs", "mute", "master"], "hidden": ["effects"] },
          "cell": { "parts": ["mute", "fader", "value"], "muted": "tint" },
          "meter": "under-fader"
        }
      },
      "inputs": { "visibility": "collapsed" }
    }
  }
}
```

### The mixer's own window

**Pop out** moves the submixer into a window of its own and **Return mixer
to main window** brings it back. A skin's `popOut` says where the submixer
starts out under that skin, and it is a default only. Where the submixer
goes is decided in this order:

1. The UI's own reasons. With no mixer there is nothing to pop out, and
   a skin never brings back what the view sets aside.
2. Your choice. Pressing Pop out, or closing the mixer's window, is kept in
   `ui.json` as `mixerDocked`, `false` after popping it out and `true` after
   docking it. While it holds a choice, no skin's `popOut` applies.
3. The skin's `popOut`. With `"detached"`, and no choice of yours, putting
   the skin on opens the mixer's window once. That is recorded in `ui.json`
   under `skinPopOutsApplied`, one entry for each skin and layout version,
   so it does not open again. Put on while the UI is hidden in the
   tray, it waits until the UI shows.
4. Docked, as the mixer has always started.

Reading the skin again with **Reload**, or changing colours or Touch
sizing, never opens or closes the mixer's window, and no switch ever closes
it. A mixer that is popped out stays popped out through every switch, and
one that is docked stays docked unless a skin's `"detached"` default
applies as above. **Reset this skin's arrangement** in Options forgets your choice and
the record, without opening anything; the skin's default applies again the
next time you put it on.

The mixer's window keeps fitting the screen it opened on. When a switch
gives the submixer a template that needs more room than the one before and
more than the window has, the window widens to fit, never past that
screen; a template that needs less leaves the width as you set it.

### Switching skins

Putting on a skin that only changes colours or control appearances
repaints the UI and does nothing else. Putting on one whose layout
differs moves the regions and changes templates, and keeps to these rules:

- A drag in progress ends first: an Arrange drag, the swap menu, and a
  fader held down in the UI or in the mixer's own window. The fader
  keeps the value it had reached and sends nothing more until it is
  pressed again.
- The submixer changes template in whichever window holds it.
- Focus stays on the control that had it. When the new template draws that
  control again, focus goes to the new one for the same channel, mix and
  role: the send fader for Game into Stream stays focused as the send
  fader for Game into Stream, a mute as the same mute, a picker as the same
  picker.
- The page keeps its scroll, and so does every scroller that is still
  there. A new tree is drawn from its left.
- The sections' folds, the compact view's channel and mix, your Arrange
  moves and your bank picks for each skin are kept.
- Every channel, mix and send is the same object before and after. No
  level, mute, routing or layout order is sent to the daemon, and a plugin
  window stays open under the window that opened it. When the mixer's
  window closes, the plugin windows opened from it move back to the
  UI.

#### A complete example with a toolbar, a list and its own window

The application list and MONITOR's toolbar in a 280 pixel column on the
left, the other sections beside them, and the submixer opened in its own
window the first time the skin is put on:

```json
{
  "schema": 1,
  "id": "side-column",
  "name": "Side column",
  "tokens": {
    "Ox.Card.Background": "#24262c"
  },
  "layout": {
    "version": 1,
    "main": {
      "kind": "grid",
      "id": "desktop",
      "columns": [{ "size": 280 }, { "star": 1 }],
      "rows": ["auto", "auto"],
      "children": [
        { "kind": "region", "region": "header", "row": 0, "column": 0, "columnSpan": 2 },
        {
          "kind": "stack",
          "id": "side",
          "orientation": "vertical",
          "row": 1,
          "column": 0,
          "children": [
            { "kind": "region", "region": "applications" },
            { "kind": "region", "region": "monitor" }
          ]
        },
        {
          "kind": "stack",
          "id": "centre",
          "orientation": "vertical",
          "row": 1,
          "column": 1,
          "children": [
            { "kind": "region", "region": "inputs" },
            { "kind": "region", "region": "headphones" },
            { "kind": "region", "region": "submixer" }
          ]
        }
      ]
    },
    "regions": {
      "applications": { "template": "application-list" },
      "monitor": { "template": "output-toolbar" },
      "submixer": { "popOut": { "default": "detached" } }
    }
  }
}
```

### How a layout is read

A layout is taken whole or not at all. Any of these drops the whole
`layout` block:

- `version` is missing, is not a whole number, or is not `1`. A newer
  version says to update OpenXLR;
- a property the object does not have, or one it needs and lacks;
- the same key twice in one object;
- a `kind` other than `stack`, `grid`, `dock` and `region`, or a region as
  `main`;
- a container `id` that is missing, is not 1 to 32 lowercase letters,
  digits and hyphens, or is used twice;
- an `orientation` other than `vertical` and `horizontal`;
- a `spacing` that is not a finite number from 0 to 64;
- `rows` or `columns` that are not 1 to 8 tracks, or a track that is not
  `"auto"`, a `star` from 0.1 to 10 or a `size` from 40 to 2000;
- a grid child without its `row` and `column`, one outside the grid, a
  span past the last track, or two children over one cell;
- a dock child without its `dock` edge, an edge other than the five, or a
  dock that does not end with exactly one `fill` child;
- a region id this OpenXLR does not have, a region placed twice, or a
  section left out;
- more than 16 children in a container, more than 64 nodes in the tree,
  or containers more than 4 deep;
- in `regions`, a region id this OpenXLR does not have, an option the
  region does not have, a `visibility` other than the three, a `width` or
  `height` that is not a finite number from 120 to 4000, or a header
  action that does not exist, is `options`, or is listed twice;
- a `template`, strip template, master template, part, label style,
  orientation, placement or direction this OpenXLR does not have; an
  option that belongs to another template; `identity` in `hidden`; a part
  listed twice across `partsOrder` and `hidden`, or more names in either
  than the strip has parts; a `visibleBanks` that is not a whole number
  from 1 to 4; or `selectionDefaults` that is not an array of at most 16
  mix ids, each 1 to 64 lowercase letters, digits, hyphens and
  underscores, with none listed twice;
- in `matrix`, a property it does not have, a part a row head, column head
  or cell does not have, a part listed twice across `parts` and `hidden` or
  more names in either than the list has parts, `identity` hidden in a head
  or `fader` hidden in a cell, a label style other than the three, a
  `muted` other than `"tint"` and `"dim"`, or a `meter` other than
  `"under-fader"` and `"none"`;
- a `popOut` that is not an object, lacks its `default`, has another
  property, or has a `default` other than `"docked"` and `"detached"`.

The complaint names where in the file the fault is, for example
`layout.main.children[2].region`, and appears in Options under the picker
with the others. The skin's tokens and controls still apply; the UI
wears its colours in the classic arrangement.

Anything that reads skins only for their colours ignores `layout`: the
terminal mixer, the Omarchy bar plugin and the Stream Deck plugin. An older
OpenXLR UI ignores it as well, since it skips top-level fields it does
not know, and wears the skin in the classic arrangement.

### Your own arrangement

Arrange moves sections under any skin. Where it keeps your moves depends
on the skin:

- A skin without a layout, such as Material or Deck, uses the order you
  arranged, kept in `ui.json` as `sectionOrder`, exactly as
  before skins had layouts. A section dropped on another lands before or
  after it.
- A skin with a layout starts from its own tree. Each region in the tree
  is a place, named by its container's id and its index in a stack or a
  dock (`window:2`), or by its container's id, row and column in a grid
  (`desktop:1,0`). Your arrangement says which section sits in which
  place. Dropping a section on another trades their places, in the same
  container or across containers. In a stack, dropping a section on its
  neighbour still lands it before or after, which is the same as trading
  places with it; dropped on a section further away, the two trade places.
  The header stays in the place the skin gives it.
- Under a skin with a layout, your moves are kept in `ui.json` under
  `skinArrangements`, one entry for each skin and layout version, holding
  only the places you changed and the hidden sections you showed. The 32
  most recent skins are kept. They never change `sectionOrder`.
  **Reset sections** returns to the skin's own places and visibility and
  forgets only that skin's entry. **Reset this skin's arrangement** in
  Options does the same and also forgets your bank picks for that skin and
  where you put the mixer.
- Switching back to a skin without a layout brings your `sectionOrder`
  back, and switching to the layout skin again brings back what you
  arranged under it.

The tree, the gaps and the sizes always come from the skin. If a skin
update changes its tree so that your entry names a place or a section the
tree no longer has, the whole entry is dropped rather than half applied,
the UI says so once in the note under the header, and the skin's own
arrangement is used. OpenXLR never rewrites a skin. Collapsed sections stay
collapsed whichever skin is worn, as before; a skin's `"collapsed"` only
sets where a section starts the first time, recorded in `ui.json` under
`skinLayoutsSeen`.

## What a skin cannot do

- It cannot carry markup, a control template of its own, a style or
  code. It picks from the appearances listed under Controls and tints
  them. Apart from placing the header and the five sections under
  [Layout](#layout), ordering the header's actions, hiding a section the
  user can bring back, and choosing one of the application's templates
  with its parts in another order, there is no way for a skin to add a
  control, move one out of its template, resize the window, or change
  what a control does.
- It cannot change the mixer layout, the mixer, the routing or anything
  the daemon owns. It sets the values in this document and nothing else.
- It cannot reach outside its own folder, and it cannot make a network
  request or load a font file.
- It cannot choose which icon a channel or mix wears. That is the user's
  choice in the layout editor; a skin can only redraw an icon or add one
  to the choices, as path data, never as a file.
- It cannot skin a plugin's own editor. Those windows belong to the
  plugin, are drawn by the plugin's own toolkit, and OpenXLR only hosts
  them. The generated controls OpenXLR draws for a plugin do follow the
  skin.
- It cannot change the OpenDeck plugin's key art. The plugin draws its own
  images on the device; the Deck skin borrows that visual language for the
  UI, not the other way round.
- It cannot hold a light and a dark version of itself, or follow the
  desktop's preference. A skin is one set of values. Only Material has a
  light and a dark palette, chosen by its mode. The toolkit values a skin
  leaves at `framework` are the ones that follow the desktop.
- It cannot skin the tray icon, the desktop notification or any window
  another application draws.

## Colour, contrast and testing

The test suite holds every shipped appearance to a few rules, and a skin
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
- the names a screen reader reads come from the UI, not from the skin,
  and a focused control still shows its focus adorner. Nothing a skin can
  set changes either, but a face with no contrast against the focus ring
  hides it in practice.

Walk the windows before you share a skin: the mixer, Options, the audio
flow window, the plugin controls window, the mixer layout window, the EQ
editor, the Ducking window, the input binding dialog, the desktop keys
dialog, a confirmation dialog, a tooltip and a dropdown list. The pointer-over, pressed, checked and disabled
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
chosen one leaves the UI on Material, and `ui.json` keeps the name
until you choose something else, so putting the folder back brings it
straight back.

To return to the classic look, choose Material and System in Options. To
reset the appearance completely, close the UI and delete the `"skin"`
and `"appearanceMode"` lines from `~/.config/openxlr/ui.json`; the UI
then starts on Broadcast, the default. Those lines are
the whole of the appearance's presence on your machine, and nothing about
the mixer, the daemon or your audio devices is kept in that file at all, so
neither route can disturb what is playing.

## Adding a token

For the record, since the format is only as complete as the UI: a
value the UI hard-codes cannot be skinned. If something you want to
change has no token here, that is a missing token rather than a limit of
the format, and it is a small change to the source. See
[AGENTS.md](../AGENTS.md) and open an issue or a pull request.
