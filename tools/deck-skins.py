#!/usr/bin/env python3
"""Export colour tokens for the Deck client; no image or control markup."""
import argparse
import json
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parent.parent
OUTPUT = ROOT / 'plugin/com.emaspa.openxlr.sdPlugin/skin-palettes.json'
TOKENS = ('Ox.Card.Background', 'Ox.Text.Primary', 'Ox.Text.Muted',
          'Ox.Led.On', 'Ox.Led.Off', 'Ox.Led.Alert', 'Ox.Meter.Track',
          'Ox.Meter.Fill', 'Ox.Meter.Warning', 'Ox.Meter.Hot',
          'Ox.Meter.WarningLevel', 'Ox.Meter.HotLevel')


def generate():
    source = (ROOT / 'src/OpenXLR.UI/Skinning/SkinTokens.cs').read_text()
    defaults = {}
    for token in TOKENS:
        match = re.search(r'(?:SolidBrush|Brush)\("' + re.escape(token) + r'", "(#[0-9a-fA-F]+)"', source)
        if match:
            defaults[token] = match[1]
    # SkinDocumentTests holds these two against the actual numeric defaults.
    defaults.update({'Ox.Meter.WarningLevel': .7, 'Ox.Meter.HotLevel': .9})
    if set(defaults) != set(TOKENS):
        raise ValueError('A Deck default is missing; check SkinTokens')
    palettes = {'default': defaults}
    for file in sorted((ROOT / 'src/OpenXLR.UI/Assets/Skins').glob('*/skin.json')):
        skin = json.loads(file.read_text())
        if skin['id'] != file.parent.name:
            raise ValueError(f'{file}: id differs from directory')
        values = {}
        for token in TOKENS:
            value = skin['tokens'].get(token)
            # A Deck image uses a representative colour for a surface.
            if isinstance(value, dict) and value.get('type') in ('linear', 'radial'):
                value = value['stops'][0]['color']
            if value is not None:
                values[token] = value
        palettes[skin['id']] = values
    return json.dumps(palettes, indent=2) + '\n'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    generated = generate()
    if args.check:
        if not OUTPUT.exists() or OUTPUT.read_text() != generated:
            sys.exit('Deck palettes are out of date; run python3 tools/deck-skins.py')
        print('Deck palettes match the embedded skins')
    else:
        OUTPUT.write_text(generated)


if __name__ == '__main__':
    main()
