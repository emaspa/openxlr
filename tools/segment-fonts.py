#!/usr/bin/env python3
"""OpenXLR Segment 7 and 14: DSEG7 and DSEG14 Classic Italic with a space as
wide as a cell, so words stay apart as on a real display, where a space is a
blank cell, a point with a width of its own, and an ellipsis made of the face's
own points for cut lettering.
Renamed as the SIL OFL requires of a modified DSEG (Reserved Font
Name "DSEG").  usage: tools/segment-fonts.py <dseg dir> [<out dir>]

<dseg dir> holds DSEG7Classic-Italic.ttf and DSEG14Classic-Italic.ttf from
https://github.com/keshikan/DSEG; the out dir defaults to the UI's fonts.
Needs fontTools. Run tools/deck-segments.py after it, so the Deck's outlines
follow."""
import os, sys
from fontTools.ttLib import TTFont
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.ttGlyphPen import TTGlyphPen

SRC = {'7': 'DSEG7Classic-Italic.ttf', '14': 'DSEG14Classic-Italic.ttf'}

def ellipsis(f, cell):
    """An ellipsis one cell wide: three of the face's own decimal points
    spread across the cell, so cut lettering ends in the face itself
    instead of a fallback font's glyph."""
    glyphs = f.getGlyphSet()
    cmap = f.getBestCmap()
    period = cmap[ord('.')]
    bounds = BoundsPen(glyphs)
    glyphs[period].draw(bounds)
    x0, _, x1, _ = bounds.bounds
    dot = x1 - x0
    left, right = 62, cell - 62
    gap = (right - left - 3 * dot) / 2
    pen = TTGlyphPen(glyphs)
    for i in range(3):
        glyphs[period].draw(TransformPen(pen, (1, 0, 0, 1, round(left + i * (dot + gap) - x0), 0)))
    name = 'ellipsis'
    glyph = pen.glyph()
    f['glyf'][name] = glyph
    order = f.getGlyphOrder()
    if name not in order:
        f.setGlyphOrder(order + [name])
    glyph.recalcBounds(f['glyf'])
    f['hmtx'][name] = (cell, glyph.xMin)
    for table in f['cmap'].tables:
        if table.isUnicode():
            table.cmap[0x2026] = name
    f['maxp'].numGlyphs = len(f.getGlyphOrder())

def point(f, cell):
    """A point with a width of its own, on the baseline. DSEG's point
    has no width and lands on the bottom right segment of the digit before
    it, where at small sizes it merges into that segment and cannot be seen."""
    glyphs = f.getGlyphSet()
    period = f.getBestCmap()[ord('.')]
    bounds = BoundsPen(glyphs)
    glyphs[period].draw(bounds)
    x0, _, x1, _ = bounds.bounds
    pen = TTGlyphPen(glyphs)
    # Narrow, so the point sits close to the digits beside it as on a
    # display, where it lights in the gap between two cells.
    side = 40
    glyphs[period].draw(TransformPen(pen, (1, 0, 0, 1, round(side - x0), 0)))
    glyph = pen.glyph()
    f['glyf'][period] = glyph
    glyph.recalcBounds(f['glyf'])
    f['hmtx'][period] = (round(x1 - x0) + 2 * side, glyph.xMin)

def build(src, out, digits):
    # The source's timestamps stay, so the same DSEG makes the same bytes.
    f = TTFont(src, recalcTimestamp=False)
    cmap = f.getBestCmap()
    hmtx = f['hmtx']
    cell = hmtx[cmap[ord('8')]][0]
    space = cmap[ord(' ')]
    hmtx[space] = (cell, hmtx[space][1])
    family = f'OpenXLR Segment {digits}'
    ps = f'OpenXLRSegment{digits}-Italic'
    for rec in f['name'].names:
        if rec.nameID in (1, 16):
            rec.string = family
        elif rec.nameID == 4:
            rec.string = family + ' Italic'
        elif rec.nameID == 6:
            rec.string = ps
        elif rec.nameID == 3:
            rec.string = ps + ';OpenXLR'
        elif rec.nameID == 5:
            rec.string = str(rec.toUnicode()) + '; space widened to one cell, ellipsis added, point with a width of its own'
    ellipsis(f, cell)
    point(f, cell)
    f['hhea'].advanceWidthMax = max(f['hhea'].advanceWidthMax, cell)
    f.save(out)
    print('wrote', out)

if __name__ == '__main__':
    if len(sys.argv) not in (2, 3):
        sys.exit(__doc__)
    out = sys.argv[2] if len(sys.argv) == 3 else os.path.join(
        os.path.dirname(os.path.abspath(__file__)), '..', 'src/OpenXLR.UI/Assets/Fonts')
    for d, name in SRC.items():
        build(os.path.join(sys.argv[1], name), os.path.join(out, f'OpenXLRSegment{d}-Italic.ttf'), d)
