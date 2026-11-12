#!/usr/bin/env python3
"""Export the OpenXLR Segment 14 glyph outlines for the Deck client.

A segment skin's lettering reaches the keys as SVG paths, since the image a
key shows is rendered without the UI's bundled fonts. Reads the font with the
standard library only: cmap format 4, hmtx, loca and simple glyf outlines.
"""
import argparse
import json
from pathlib import Path
import struct
import sys

ROOT = Path(__file__).resolve().parent.parent
FONT = ROOT / 'src/OpenXLR.UI/Assets/Fonts/OpenXLRSegment14-Italic.ttf'
OUTPUT = ROOT / 'plugin/com.emaspa.openxlr.sdPlugin/segment-font.json'
# Printable ASCII, and the ellipsis cut lettering ends in.
CHARACTERS = [chr(c) for c in range(0x20, 0x7f)] + ['…']


class Font:
    def __init__(self, data):
        self.data = data
        count = struct.unpack_from('>H', data, 4)[0]
        self.tables = {}
        for i in range(count):
            tag, _, offset, length = struct.unpack_from('>4sIII', data, 12 + 16 * i)
            self.tables[tag.decode('latin-1')] = (offset, length)
        head = self.tables['head'][0]
        self.units = struct.unpack_from('>H', data, head + 18)[0]
        self.long_loca = struct.unpack_from('>h', data, head + 50)[0] == 1
        self.glyphs = struct.unpack_from('>H', data, self.tables['maxp'][0] + 4)[0]
        self.metrics = struct.unpack_from('>H', data, self.tables['hhea'][0] + 34)[0]
        self.map = self.cmap()

    def cmap(self):
        base = self.tables['cmap'][0]
        count = struct.unpack_from('>H', self.data, base + 2)[0]
        for i in range(count):
            platform, encoding, offset = struct.unpack_from('>HHI', self.data, base + 4 + 8 * i)
            table = base + offset
            if (platform, encoding) in ((3, 1), (0, 3)) and struct.unpack_from('>H', self.data, table)[0] == 4:
                return self.format4(table)
        raise ValueError('no Unicode cmap of format 4')

    def format4(self, table):
        segments = struct.unpack_from('>H', self.data, table + 6)[0] // 2
        ends = table + 14
        starts = ends + 2 * segments + 2
        deltas = starts + 2 * segments
        ranges = deltas + 2 * segments
        result = {}
        for s in range(segments):
            end = struct.unpack_from('>H', self.data, ends + 2 * s)[0]
            start = struct.unpack_from('>H', self.data, starts + 2 * s)[0]
            delta = struct.unpack_from('>h', self.data, deltas + 2 * s)[0]
            offset = struct.unpack_from('>H', self.data, ranges + 2 * s)[0]
            for code in range(start, end + 1):
                if code == 0xffff:
                    continue
                if offset == 0:
                    glyph = (code + delta) & 0xffff
                else:
                    at = ranges + 2 * s + offset + 2 * (code - start)
                    glyph = struct.unpack_from('>H', self.data, at)[0]
                    if glyph:
                        glyph = (glyph + delta) & 0xffff
                if glyph:
                    result[code] = glyph
        return result

    def advance(self, glyph):
        hmtx = self.tables['hmtx'][0]
        return struct.unpack_from('>H', self.data, hmtx + 4 * min(glyph, self.metrics - 1))[0]

    def outline(self, glyph):
        loca = self.tables['loca'][0]
        if self.long_loca:
            start, end = struct.unpack_from('>II', self.data, loca + 4 * glyph)
        else:
            start, end = (2 * v for v in struct.unpack_from('>HH', self.data, loca + 2 * glyph))
        if start == end:
            return ''
        at = self.tables['glyf'][0] + start
        contours = struct.unpack_from('>h', self.data, at)[0]
        if contours < 0:
            raise ValueError(f'glyph {glyph} is composite')
        ends = struct.unpack_from(f'>{contours}H', self.data, at + 10)
        points = ends[-1] + 1
        at += 10 + 2 * contours
        at += 2 + struct.unpack_from('>H', self.data, at)[0]
        flags = []
        while len(flags) < points:
            flag = self.data[at]
            at += 1
            repeat = 1
            if flag & 8:
                repeat += self.data[at]
                at += 1
            flags += [flag] * repeat
        xs, at = self.coordinates(flags, at, 2, 16)
        ys, at = self.coordinates(flags, at, 4, 32)
        path = []
        first = 0
        for last in ends:
            path.append(contour([(xs[i], ys[i], flags[i] & 1) for i in range(first, last + 1)]))
            first = last + 1
        return ''.join(path)

    def coordinates(self, flags, at, short, same):
        values, value = [], 0
        for flag in flags:
            if flag & short:
                step = self.data[at]
                at += 1
                value += step if flag & same else -step
            elif not flag & same:
                value += struct.unpack_from('>h', self.data, at)[0]
                at += 2
            values.append(value)
        return values, at


def contour(points):
    """One closed TrueType contour as SVG path commands, y pointing up as in the font."""
    if not any(on for _, _, on in points):
        # All off-curve: start at the midpoint of the first two, which lies
        # on the curve between them.
        (x0, y0, _), (x1, y1, _) = points[0], points[1]
        points = [((x0 + x1) / 2, (y0 + y1) / 2, 1)] + points[1:] + points[:1]
    start = next(i for i, p in enumerate(points) if p[2])
    points = points[start:] + points[:start]
    x, y, _ = points[0]
    out = [f'M{num(x)} {num(y)}']
    pending = None
    for px, py, on in points[1:] + [points[0]]:
        if on:
            out.append(f'L{num(px)} {num(py)}' if pending is None
                       else f'Q{num(pending[0])} {num(pending[1])} {num(px)} {num(py)}')
            pending = None
        elif pending is None:
            pending = (px, py)
        else:
            mx, my = (pending[0] + px) / 2, (pending[1] + py) / 2
            out.append(f'Q{num(pending[0])} {num(pending[1])} {num(mx)} {num(my)}')
            pending = (px, py)
    out.append('Z')
    return ''.join(out)


def num(value):
    return str(int(value)) if float(value).is_integer() else f'{value:.1f}'


def generate():
    font = Font(FONT.read_bytes())
    full = font.map[ord('~')]
    glyphs = {}
    for char in CHARACTERS:
        glyph = font.map.get(ord(char))
        if glyph is not None:
            glyphs[char] = {'advance': font.advance(glyph), 'path': font.outline(glyph)}
    document = {'unitsPerEm': font.units, 'cell': font.advance(full), 'full': '~', 'glyphs': glyphs}
    return json.dumps(document, indent=1, ensure_ascii=False) + '\n'


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    generated = generate()
    if args.check:
        if not OUTPUT.exists() or OUTPUT.read_text() != generated:
            sys.exit('Deck segment font is out of date; run python3 tools/deck-segments.py')
        print('Deck segment font matches the bundled face')
    else:
        OUTPUT.write_text(generated)


if __name__ == '__main__':
    main()
