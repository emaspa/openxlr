pragma ComponentBehavior: Bound

import QtQuick
import "Mixer.js" as Mixer

Item {
    id: root
    required property var link
    required property color foreground
    required property color background
    required property string fontFamily
    required property int pixelSize
    readonly property var mixer: link.snapshot && link.snapshot.mixer ? link.snapshot.mixer : ({})
    readonly property var channels: Mixer.shownChannels(mixer.channels)
    readonly property var mixes: mixer.mixes || []
    readonly property var feeds: Mixer.monitorFeeds(mixer)
    readonly property var feed: feeds.length ? feeds[0] : null
    readonly property string feedLabel: {
        if (!link.ready)
            return "XLR off";
        if (!feed)
            return "No output";
        var names = feed.ids.map(function (id) {
            var mix = root.mixes.filter(function (entry) {
                return entry.id === id;
            })[0];
            return Mixer.barName(mix ? mix.name : id);
        });
        return (names.join(" + ") || feed.name) + (feeds.length > 1 ? " +" + (feeds.length - 1) : "");
    }
    implicitWidth: readouts.implicitWidth
    implicitHeight: metrics.height

    FontMetrics {
        id: metrics
        font.family: root.fontFamily
        font.pixelSize: root.pixelSize
    }
    component Readout: Item {
        id: readout
        required property string text
        property real level: 0
        property bool muted: false
        implicitWidth: Math.ceil(label.width + 6 + meter.implicitWidth)
        width: implicitWidth
        height: metrics.height
        // Match the bar's regular text. Mute dims both label and meter to 45%
        // instead of changing font weight or removing the input.
        opacity: muted ? 0.45 : 1
        Text {
            id: label
            objectName: "label"
            anchors.verticalCenter: parent.verticalCenter
            width: Math.min(metrics.advanceWidth(readout.text), root.pixelSize * 10)
            height: metrics.height
            text: readout.text
            textFormat: Text.PlainText
            font.family: root.fontFamily
            font.pixelSize: root.pixelSize
            font.bold: false
            color: root.foreground
            verticalAlignment: Text.AlignVCenter
            elide: Text.ElideRight
        }
        BlockMeter {
            id: meter
            objectName: "meter"
            x: label.width + 6
            anchors.verticalCenter: parent.verticalCenter
            foreground: root.foreground
            background: root.background
            fontFamily: root.fontFamily
            pixelSize: root.pixelSize
            leftLevel: readout.level
            emphasizeHot: false
        }
    }

    Row {
        id: readouts
        anchors.verticalCenter: parent.verticalCenter
        spacing: 10
        Repeater {
            model: ["xlr1", "xlr2"]
            Readout {
                id: input
                required property string modelData
                objectName: "input-" + modelData
                property var lastActive: ({})
                property bool held: false
                readonly property bool present: root.channels.some(function (channel) {
                    return channel.id === input.modelData;
                })
                visible: root.link.ready && present && held
                text: modelData === "xlr1" ? "XLR 1" : "XLR 2"
                level: Mixer.pair(root.link.levels, "ch:" + modelData, true)[0]
                muted: !!(root.link.snapshot && root.link.snapshot.state || {})[Mixer.muteControl(modelData)]

                function observe(): void {
                    if (!root.link.ready) {
                        release.stop();
                        lastActive = ({});
                        held = false;
                        return;
                    }
                    // Read the frame directly: a levelsChanged handler can run
                    // before this delegate's level binding has reevaluated.
                    if (Mixer.pair(root.link.levels, "ch:" + modelData, true)[0] > 0) {
                        var seen = {};
                        seen[modelData] = Date.now();
                        lastActive = seen;
                        release.interval = Mixer.inputHoldMs;
                        release.restart();
                    }
                    held = Mixer.shownInputs(lastActive, Date.now()).indexOf(modelData) >= 0;
                }
                Timer {
                    id: release
                    objectName: "release"
                    interval: Mixer.inputHoldMs
                    repeat: false
                    onTriggered: {
                        input.held = Mixer.shownInputs(input.lastActive, Date.now()).indexOf(input.modelData) >= 0;
                        // A timer delivered just before the deadline waits only
                        // the remaining time. No polling timer runs between frames.
                        if (input.held) {
                            interval = Math.max(1, input.lastActive[input.modelData] + Mixer.inputHoldMs - Date.now());
                            restart();
                        }
                    }
                }
                Connections {
                    target: root.link
                    function onLevelsChanged(): void {
                        input.observe();
                    }
                    function onReadyChanged(): void {
                        input.observe();
                    }
                }
                Component.onCompleted: observe()
            }
        }
        Readout {
            objectName: "feed"
            text: root.feedLabel
            // One lane shows the louder stereo channel. For a summed feed it
            // shows the loudest constituent mix, not a guessed sum of dB values.
            level: root.link.ready && root.feed ? Mixer.feedLevel(root.link.levels, root.feed.ids) : 0
            opacity: root.link.ready ? 1 : 0.45
        }
    }
}
