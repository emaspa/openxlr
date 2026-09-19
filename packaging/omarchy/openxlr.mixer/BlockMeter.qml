pragma ComponentBehavior: Bound

import QtQuick
import "Mixer.js" as Mixer

Item {
    id: root
    required property color foreground
    required property color background
    required property string fontFamily
    property real leftLevel: 0
    property real rightLevel: 0
    property bool mono: true
    property bool vertical: false
    property int cells: 6
    property int pixelSize: 12
    property var meterPalette: null
    property bool emphasizeHot: vertical
    readonly property real cellWidth: pixelSize * 0.62
    readonly property int lanes: mono ? 1 : 2
    readonly property real rowHeight: vertical ? pixelSize : metrics.height
    implicitWidth: (vertical ? (mono ? 2 : 3) : cells) * cellWidth
    implicitHeight: (vertical ? cells : lanes) * rowHeight

    FontMetrics {
        id: metrics
        font.family: root.fontFamily
        font.pixelSize: root.pixelSize
    }

    function shade(amount: real): color {
        return Qt.rgba(background.r + (foreground.r - background.r) * amount, background.g + (foreground.g - background.g) * amount, background.b + (foreground.b - background.b) * amount, 1);
    }

    // TUI anchors: warning at 0.7 (-18 dBFS), hot at 0.9 (-6 dBFS).
    // Blend the bar's background towards its foreground instead of borrowing
    // a skin palette when none is supplied. Empty tracks keep low contrast
    // on both light and dark bars; horizontal bar glyphs stay regular weight.
    function ink(position: real): color {
        if (meterPalette) {
            if (position >= meterPalette.hotLevel)
                return meterPalette.hot;
            if (position >= meterPalette.warningLevel)
                return meterPalette.warning;
            return meterPalette.fill;
        }
        if (position >= 0.9)
            return foreground;
        if (position >= 0.7)
            return shade(0.8 + (position - 0.7));
        if (position <= 0.35)
            return shade(0.55);
        return shade(0.55 + (position - 0.35) / 0.35 * 0.25);
    }

    Repeater {
        model: root.cells * root.lanes
        Text {
            required property int index
            readonly property int lane: Math.floor(index / root.cells)
            readonly property int cell: index % root.cells
            readonly property int row: root.vertical ? root.cells - cell - 1 : cell
            readonly property real level: lane === 0 ? root.leftLevel : root.rightLevel
            readonly property real position: (row + 1) / root.cells
            readonly property string glyph: root.vertical ? Mixer.verticalGlyph(level, row, root.cells) : Mixer.horizontalGlyph(level, row, root.cells)
            x: root.vertical ? lane * root.cellWidth * 2 : cell * root.cellWidth
            y: root.vertical ? cell * root.rowHeight : lane * root.rowHeight
            width: root.cellWidth * (root.vertical && root.mono ? 2 : 1)
            height: root.rowHeight
            verticalAlignment: Text.AlignVCenter
            text: root.vertical && root.mono ? glyph + glyph : glyph
            textFormat: Text.PlainText
            font.family: root.fontFamily
            font.pixelSize: root.pixelSize
            font.bold: root.emphasizeHot && position >= (root.meterPalette ? root.meterPalette.hotLevel : 0.9)
            color: glyph === "│" || glyph === "─" ? (root.meterPalette ? root.meterPalette.track : root.shade(0.18)) : root.ink(position)
        }
    }
}
