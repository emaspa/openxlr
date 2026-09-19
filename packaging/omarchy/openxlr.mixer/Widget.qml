pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls
import qs.Commons
import qs.Ui as Ui
import "Mixer.js" as Mixer

Item {
    id: root
    // Only PluginBarApi's presentation, tooltip and popout calls are used.
    property var bar: null
    property var registeredBar: null
    property string moduleName: "openxlr.mixer"
    property var settings: ({})
    property bool opened: false
    property string selectedMix: ""
    readonly property bool vertical: bar ? bar.vertical : false
    readonly property int barSize: bar ? bar.barSize : 26
    readonly property color foreground: bar ? bar.foreground : Color.foreground
    readonly property color background: bar ? bar.background : Color.background
    // A transparent bar still supplies RGB. The popout needs an opaque ground.
    readonly property color face: Qt.rgba(background.r, background.g, background.b, 1)
    readonly property string family: bar ? bar.fontFamily : Style.font.family
    readonly property var mixer: daemon.snapshot ? daemon.snapshot.mixer : ({})
    readonly property var channels: Mixer.shownChannels(mixer.channels)
    readonly property var mixes: mixer.mixes || []
    readonly property var inputs: channels.filter(function (channel) {
        return Mixer.mono(channel.id);
    })
    readonly property var feeds: Mixer.monitorFeeds(mixer)
    readonly property string mixId: mixes.some(function (mix) {
        return mix.id === root.selectedMix;
    }) ? selectedMix : feeds.length && feeds[0].ids.length ? feeds[0].ids[0] : mixes.length ? mixes[0].id : ""
    readonly property string mixName: {
        var mix = mixes.filter(function (entry) {
            return entry.id === root.mixId;
        })[0];
        return mix ? mix.name : "No mix";
    }
    implicitWidth: vertical ? barSize : barContent.implicitWidth
    implicitHeight: vertical ? barContent.implicitWidth : barSize
    clip: true
    Behavior on implicitWidth {
        NumberAnimation {
            duration: 180
            easing.type: Easing.OutCubic
        }
    }
    Behavior on implicitHeight {
        enabled: root.vertical
        NumberAnimation {
            duration: 180
            easing.type: Easing.OutCubic
        }
    }

    function close(): void {
        opened = false;
    }
    function closeForPopoutSwitch(): void {
        close();
    }
    function open(): void {
        opened = true;
    }
    function toggle(): void {
        if (bar)
            bar.hideTooltip(target);
        opened = !opened;
    }
    function registerTarget(): void {
        if (registeredBar)
            registeredBar.unregisterClickTarget(target);
        registeredBar = bar;
        if (registeredBar)
            registeredBar.registerClickTarget(target);
    }
    // The host injects bar after loading the widget, past Component.onCompleted.
    onBarChanged: registerTarget()
    Component.onCompleted: registerTarget()
    function moveMix(by: int): void {
        if (!mixes.length)
            return;
        var at = mixes.findIndex(function (mix) {
            return mix.id === root.mixId;
        });
        selectedMix = mixes[(at + by + mixes.length) % mixes.length].id;
    }
    function tooltip(): string {
        if (!daemon.ready)
            return "OpenXLR: daemon not reachable. Retrying in the background.";
        var lines = inputs.map(function (input) {
            var muted = !!((daemon.snapshot && daemon.snapshot.state) || {})[Mixer.muteControl(input.id)];
            var meter = Mixer.pair(daemon.levels, "ch:" + input.id, true);
            return input.name + ": " + (muted ? "muted" : Mixer.rms(meter[0]) + " dBFS RMS");
        });
        feeds.forEach(function (feed) {
            lines.push(Mixer.outputName(daemon.snapshot, feed.output) + ": " + feed.name);
        });
        return ["OpenXLR"].concat(lines, ["Click for sends and masters"]).join("\n");
    }

    DaemonLink {
        id: daemon
    }
    ThemePalette {
        id: theme
    }

    BarContent {
        id: barContent
        anchors.centerIn: parent
        link: daemon
        foreground: root.bar ? root.bar.barForeground : Color.foreground
        background: root.face
        fontFamily: root.family
        pixelSize: Style.font.body
        width: implicitWidth
        height: root.barSize
        rotation: root.vertical ? 90 : 0
    }
    MouseArea {
        id: target
        function triggerPress(button: int): void {
            root.toggle();
        }
        anchors.fill: parent
        hoverEnabled: true
        cursorShape: Qt.PointingHandCursor
        onClicked: root.toggle()
        onEntered: if (root.bar)
            root.bar.showTooltip(target, root.tooltip())
        onExited: if (root.bar)
            root.bar.hideTooltip(target)
    }

    // The first-party audio panel uses KeyboardPanel for reactive screen-edge
    // clamping, including bars on the bottom or either side of the screen.
    Ui.KeyboardPanel {
        id: popup
        anchorItem: target
        bar: root.bar
        owner: root
        open: root.opened
        padding: 8
        borderSpec: ({
                color: root.foreground,
                widths: {
                    top: 1,
                    right: 1,
                    bottom: 1,
                    left: 1
                },
                gradient: {
                    colors: [],
                    angle: 0,
                    enabled: false
                }
            })
        focusTarget: body
        contentWidth: popup.fittedContentWidth(800)
        contentHeight: popup.fittedContentHeight(desk.implicitHeight, 560)

        Rectangle {
            anchors.fill: parent
            anchors.margins: -popup.padding
            color: root.face
        }
        Flickable {
            id: body
            anchors.fill: parent
            clip: true
            contentHeight: desk.height
            contentWidth: width
            boundsBehavior: Flickable.StopAtBounds
            ScrollBar.vertical: ScrollBar {}
            focus: root.opened
            Keys.onEscapePressed: root.close()

            Column {
                id: desk
                width: body.width
                spacing: 6
                Row {
                    width: parent.width
                    Text {
                        width: parent.width - 64
                        height: 28
                        text: "OpenXLR / live RMS"
                        color: root.foreground
                        font.family: root.family
                        font.pixelSize: 13
                        font.bold: true
                        verticalAlignment: Text.AlignVCenter
                    }
                    MixerButton {
                        width: 64
                        foreground: root.foreground
                        face: root.face
                        family: root.family
                        text: "Close"
                        onClicked: root.close()
                    }
                }
                Text {
                    width: parent.width
                    height: 34
                    // The snapshot is read through a guard even under ready.
                    // Assigning ready wakes this binding before the snapshot
                    // assignment on the next line has landed, so there is a
                    // moment where ready is true and the snapshot is still null.
                    text: !daemon.ready ? "Daemon offline. Waiting for the local session token and socket." : daemon.error || (daemon.snapshot && daemon.snapshot.warning) || (root.feeds.length ? root.feeds.map(function (feed) {
                            return Mixer.outputName(daemon.snapshot, feed.output) + ": " + feed.name;
                        }).join("; ") : "No monitor output selected")
                    textFormat: Text.PlainText
                    color: root.foreground
                    font.family: root.family
                    font.pixelSize: 11
                    wrapMode: Text.NoWrap
                    maximumLineCount: 1
                    elide: Text.ElideRight
                }
                Row {
                    width: parent.width
                    spacing: 4
                    MixerButton {
                        width: 32
                        text: "<"
                        foreground: root.foreground
                        face: root.face
                        family: root.family
                        enabled: root.mixes.length > 1 && !daemon.pending
                        Accessible.name: "Previous send mix"
                        onClicked: root.moveMix(-1)
                    }
                    Text {
                        width: parent.width - 72
                        height: 28
                        text: "Sends to " + root.mixName
                        textFormat: Text.PlainText
                        color: root.foreground
                        font.family: root.family
                        font.pixelSize: 12
                        verticalAlignment: Text.AlignVCenter
                        elide: Text.ElideRight
                    }
                    MixerButton {
                        width: 32
                        text: ">"
                        foreground: root.foreground
                        face: root.face
                        family: root.family
                        enabled: root.mixes.length > 1 && !daemon.pending
                        Accessible.name: "Next send mix"
                        onClicked: root.moveMix(1)
                    }
                }
                Flow {
                    id: banks
                    width: parent.width
                    spacing: 8
                    readonly property bool stacked: width < channelBank.stripWidth * 2 + spacing
                    StripBank {
                        id: channelBank
                        width: Math.floor((banks.stacked ? banks.width : (banks.width - banks.spacing) * 0.62) / stripWidth) * stripWidth
                        title: "Channels / send mute"
                        entries: root.channels
                        meterPalette: theme.palette
                        mix: root.mixId
                        link: daemon
                        foreground: root.foreground
                        face: root.face
                        family: root.family
                    }
                    StripBank {
                        width: Math.floor((banks.width - (banks.stacked ? 0 : channelBank.width + banks.spacing)) / stripWidth) * stripWidth
                        title: "Masters"
                        entries: root.mixes
                        meterPalette: theme.palette
                        mix: root.mixId
                        master: true
                        link: daemon
                        foreground: root.foreground
                        face: root.face
                        family: root.family
                    }
                }
            }
        }
    }
    Component.onDestruction: {
        if (registeredBar) {
            registeredBar.hideTooltip(target);
            registeredBar.unregisterClickTarget(target);
        }
        if (bar && bar.activePopout === root)
            bar.releasePopout(root);
    }
}
