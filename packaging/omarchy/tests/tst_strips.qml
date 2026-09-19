pragma ComponentBehavior: Bound

import QtQuick
import QtTest
import "../openxlr.mixer" as Plugin

TestCase {
    id: testCase
    name: "Omarchy strips"
    when: windowShown
    visible: true
    width: 200
    height: 320
    property var sent: []
    property var strip: null
    property var channel: ({
            id: "xlr1",
            name: "XLR 1",
            hardware: true,
            levels: {
                monitor: 0.4
            },
            mutedIn: []
        })

    QtObject {
        id: fakeLink
        property bool ready: true
        property bool pending: false
        property var levels: ({
                "ch:xlr1": [0.4, 0.4]
            })
        property var snapshot: ({
                connected: true,
                state: {
                    mute: false
                },
                capabilities: {
                    mute: true
                }
            })
        function send(command: var): void {
            testCase.sent.push(command);
        }
    }
    Component {
        id: stripComponent
        Plugin.MixerStrip {
            entry: testCase.channel
            mix: "monitor"
            link: fakeLink
            foreground: "#eeeeee"
            face: "#111111"
            family: "monospace"
        }
    }
    function init(): void {
        sent = [];
        fakeLink.ready = true;
        fakeLink.pending = false;
        strip = createTemporaryObject(stripComponent, testCase);
        verify(strip !== null);
    }
    function test_releaseSendsOneValueAndExternalStateRestoresTheFader(): void {
        var fader = findChild(strip, "volume");
        compare(fader.value, 0.4);
        mousePress(fader, 16, 95);
        mouseMove(fader, 16, 25, 20);
        compare(sent.length, 0);
        mouseRelease(fader, 16, 25);
        compare(sent.length, 1);
        compare(sent[0].cmd, "setLevel");
        compare(sent[0].channel, "xlr1");
        compare(sent[0].mix, "monitor");
        verify(sent[0].value > 0.6);
        strip.entry = {
            id: "xlr1",
            name: "XLR 1",
            levels: {
                monitor: 0.2
            },
            mutedIn: []
        };
        compare(fader.value, 0.2);
    }
    function test_keyboardChangesOnePercentagePoint(): void {
        var fader = findChild(strip, "volume");
        fader.forceActiveFocus();
        keyClick(Qt.Key_Up);
        compare(sent.length, 1);
        fuzzyCompare(sent[0].value, 0.41, 0.0001);
    }
    function test_sendMuteAndHardwareMuteAreSeparateCommands(): void {
        mouseClick(findChild(strip, "sendMute"));
        mouseClick(findChild(strip, "hardwareMute"));
        compare(sent.length, 2);
        compare(sent[0].cmd, "setChannelMuted");
        compare(sent[0].mix, "monitor");
        compare(sent[1].cmd, "set");
        compare(sent[1].control, "mute");
        compare(sent[1].value, true);
    }
    function test_offlineAndPendingStripsCannotSend(): void {
        fakeLink.ready = false;
        mouseClick(findChild(strip, "sendMute"));
        mouseClick(findChild(strip, "hardwareMute"));
        compare(sent.length, 0);
        fakeLink.ready = true;
        fakeLink.pending = true;
        mouseClick(findChild(strip, "sendMute"));
        compare(sent.length, 0);
    }
    function test_monitorMasterReaches150Percent(): void {
        strip.master = true;
        strip.entry = {
            id: "monitor",
            name: "Monitor A",
            kind: "monitor",
            volume: 1.25,
            muted: false
        };
        var fader = findChild(strip, "volume");
        compare(fader.to, 1.5);
        compare(fader.value, 1.25);
        mouseClick(findChild(strip, "sendMute"));
        compare(sent[0].cmd, "setMixMuted");
        compare(sent[0].mix, "monitor");
    }
    function test_layoutChangeDuringADragDoesNotEditTheReplacementChannel(): void {
        var fader = findChild(strip, "volume");
        mousePress(fader, 16, 95);
        mouseMove(fader, 16, 25, 20);
        strip.entry = {
            id: "music",
            name: "Music",
            levels: {
                monitor: 0.2
            },
            mutedIn: []
        };
        mouseRelease(fader, 16, 25);
        compare(sent.length, 0);
        compare(fader.value, 0.2);
    }
}
