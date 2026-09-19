pragma ComponentBehavior: Bound

import QtQuick
import QtTest
import "../openxlr.mixer" as Plugin
import "../openxlr.mixer/Mixer.js" as Mixer
import "../openxlr.mixer/Skins.js" as Skins

TestCase {
    id: testCase
    name: "Omarchy meter palettes"
    when: windowShown
    visible: true
    width: 240
    height: 320
    property var strip: null
    property string themeName: "tokyo-night"
    readonly property var paletteForTheme: Mixer.themePalette(Skins.palettes, themeName)

    QtObject {
        id: fakeLink
        property bool ready: true
        property bool pending: false
        property var snapshot: null
        property var levels: ({
                "ch:music": [1, 1]
            })
    }
    Component {
        id: stripComponent
        Plugin.MixerStrip {
            entry: ({
                    id: "music",
                    name: "Music",
                    levels: {
                        monitor: 1
                    },
                    mutedIn: []
                })
            mix: "monitor"
            link: fakeLink
            foreground: "#eeeeee"
            face: "#111111"
            family: "monospace"
            meterPalette: testCase.paletteForTheme
        }
    }
    function init(): void {
        themeName = "tokyo-night";
        fakeLink.levels = {
            "ch:music": [1, 1]
        };
        strip = createTemporaryObject(stripComponent, testCase);
        verify(strip !== null);
    }
    function cells(meter: var): var {
        return meter.children.filter(function (child) {
            return "glyph" in child;
        });
    }
    function test_litCellsUseTheirOwnZoneAndUnlitCellsUseTrack(): void {
        var meter = findChild(strip, "levelMeter");
        var palette = Skins.palettes["tokyo-night"];
        var drawn = cells(meter);
        verify(drawn.length > 0);
        for (var cell of drawn) {
            var expected = cell.position >= palette.hotLevel ? palette.hot : cell.position >= palette.warningLevel ? palette.warning : palette.fill;
            compare(String(cell.color), expected);
        }
        // Reducing the reading changes which cells are lit, not their zones.
        fakeLink.levels = {
            "ch:music": [0.75, 0.25]
        };
        for (var cell of drawn) {
            var lit = cell.glyph !== "│";
            var expected = !lit ? palette.track : cell.position >= palette.hotLevel ? palette.hot : cell.position >= palette.warningLevel ? palette.warning : palette.fill;
            compare(String(cell.color), expected);
        }
    }
    function test_themeChangesRepaintAndUnknownThemesRestoreForegroundShades(): void {
        var meter = findChild(strip, "levelMeter");
        var hot = cells(meter)[0];
        compare(String(hot.color), Skins.palettes["tokyo-night"].hot);
        themeName = "catppuccin";
        compare(String(hot.color), Skins.palettes.catppuccin.hot);
        themeName = "unmatched-theme";
        compare(meter.meterPalette, null);
        compare(String(hot.color), "#eeeeee");
        themeName = "";
        compare(meter.meterPalette, null);
        fakeLink.levels = ({});
        compare(String(hot.color), String(meter.shade(0.18)));
    }
}
