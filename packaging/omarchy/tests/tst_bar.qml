pragma ComponentBehavior: Bound

import QtQuick
import QtTest
import "../openxlr.mixer" as Plugin
import "../../../tools/omarchy-qml-imports/qs/Commons" as Commons

TestCase {
    id: testCase
    name: "Omarchy bar"
    when: windowShown
    visible: true
    width: 800
    height: 100
    property var bar: null

    QtObject {
        id: fakeLink
        property bool ready: true
        property var levels: ({})
        property var snapshot: null
    }
    Component {
        id: barComponent
        Plugin.BarContent {
            width: implicitWidth
            height: 26
            link: fakeLink
            foreground: Commons.Color.foreground
            background: Commons.Color.background
            fontFamily: Commons.Style.font.family
            pixelSize: Commons.Style.font.body
        }
    }
    function snapshotFor(muted: bool): var {
        return {
            state: {
                mute: muted,
                mute2: false
            },
            mixer: {
                channels: [
                    {
                        id: "xlr1"
                    },
                    {
                        id: "xlr2"
                    }
                ],
                mixes: [
                    {
                        id: "monitor",
                        name: "Monitor A",
                        kind: "monitor"
                    },
                    {
                        id: "monitor2",
                        name: "Monitor B",
                        kind: "monitor"
                    }
                ],
                monitorOutputs: ["headset"]
            }
        };
    }
    function init(): void {
        fakeLink.ready = true;
        fakeLink.levels = ({});
        fakeLink.snapshot = snapshotFor(false);
        bar = createTemporaryObject(barComponent, testCase);
        verify(bar !== null);
        tryCompare(bar, "implicitWidth", findChild(bar, "feed").width);
        failOnWarning(/TypeError|Cannot assign/);
    }
    function test_onlyInputsWithSignalOccupySpace(): void {
        var first = findChild(bar, "input-xlr1");
        var second = findChild(bar, "input-xlr2");
        compare(first.visible, false);
        compare(second.visible, false);
        var feedOnlyWidth = bar.implicitWidth;
        fakeLink.levels = {
            "ch:xlr2": [0.3, 0.3]
        };
        wait(0);
        compare(first.visible, false);
        compare(second.visible, true);
        compare(findChild(second, "label").text, "XLR 2");
        tryVerify(function () {
            return bar.implicitWidth > feedOnlyWidth;
        });
    }
    function test_labelsAndMetersShareTheirVerticalCentre(): void {
        fakeLink.levels = {
            "ch:xlr1": [0.4, 0.4],
            "mix:monitor": [0.2, 0.7]
        };
        wait(0);
        for (var name of ["input-xlr1", "feed"]) {
            var readout = findChild(bar, name);
            var label = findChild(readout, "label");
            var meter = findChild(readout, "meter");
            verify(Math.abs(label.y + label.height / 2 - meter.y - meter.height / 2) <= 1);
            compare(label.font.pixelSize, Commons.Style.font.body);
            compare(meter.pixelSize, label.font.pixelSize);
            compare(label.font.bold, false);
            compare(meter.meterPalette, null);
            for (var child of meter.children) {
                if (!("glyph" in child))
                    continue;
                compare(child.verticalAlignment, Text.AlignVCenter);
                compare(child.font.bold, false);
                verify(Math.abs(child.y + child.height / 2 - meter.height / 2) <= 1);
            }
        }
    }
    function test_muteDimsTheInputWithoutChangingItsWidth(): void {
        fakeLink.levels = {
            "ch:xlr1": [0.5, 0.5]
        };
        wait(0);
        tryCompare(bar, "implicitWidth", findChild(bar, "input-xlr1").width + 10 + findChild(bar, "feed").width);
        var width = bar.implicitWidth;
        fakeLink.snapshot = snapshotFor(true);
        fakeLink.levels = {
            "ch:xlr1": [0, 0]
        };
        wait(0);
        var input = findChild(bar, "input-xlr1");
        compare(input.visible, true);
        compare(input.opacity, 0.45);
        compare(findChild(input, "label").text, "XLR 1");
        compare(findChild(input, "label").font.bold, false);
        compare(bar.implicitWidth, width);
    }
    function test_signalOffExpiresAfterTenSeconds(): void {
        var input = findChild(bar, "input-xlr1");
        fakeLink.levels = {
            "ch:xlr1": [0.5, 0.5]
        };
        var firstSeen = input.lastActive.xlr1;
        wait(40);
        // Even an unchanged positive reading rearms the single release timer.
        fakeLink.levels = {
            "ch:xlr1": [0.5, 0.5]
        };
        verify(input.lastActive.xlr1 > firstSeen);
        var release = findChild(input, "release");
        compare(release.interval, 10000);
        compare(release.repeat, false);
        fakeLink.levels = ({});
        wait(9500);
        compare(input.visible, true);
        tryCompare(input, "visible", false, 1500);
        compare(release.running, false);
    }
    function test_monitorLabelAndMeterFollowTheSelectedFeed(): void {
        fakeLink.levels = {
            "mix:monitor": [0.2, 0.8],
            "mix:monitor2": [0.9, 0.1]
        };
        var feed = findChild(bar, "feed");
        compare(findChild(feed, "label").text, "Mon A");
        compare(findChild(feed, "meter").leftLevel, 0.8);
        var next = snapshotFor(false);
        next.mixer.monitorOutputs.push("speakers");
        next.mixer.monitorFeeds = {
            headset: "monitor2"
        };
        fakeLink.snapshot = next;
        compare(findChild(feed, "label").text, "Mon B +1");
        compare(findChild(feed, "meter").leftLevel, 0.9);
    }
    function test_disconnectClearsInputsAndToleratesANullSnapshot(): void {
        fakeLink.levels = {
            "ch:xlr1": [0.5, 0.5]
        };
        fakeLink.snapshot = null;
        fakeLink.ready = false;
        compare(findChild(bar, "input-xlr1").visible, false);
        compare(findChild(findChild(bar, "input-xlr1"), "release").running, false);
        fakeLink.ready = true;
        fakeLink.levels = ({});
        compare(findChild(bar, "input-xlr1").visible, false);
    }
}
