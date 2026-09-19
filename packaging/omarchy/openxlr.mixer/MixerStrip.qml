pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls
import "Mixer.js" as Mixer

Rectangle {
    id: root
    required property var entry
    required property string mix
    required property var link
    required property color foreground
    required property color face
    required property string family
    property bool master: false
    property var meterPalette: null
    readonly property bool mono: !master && Mixer.mono(entry.id)
    readonly property bool muted: master ? !!entry.muted : Mixer.sendMuted(entry, mix)
    readonly property real volume: master ? Mixer.clamp(entry.volume, maximum) : Mixer.level(entry, mix)
    readonly property real maximum: master ? Mixer.ceiling(entry) : 1
    readonly property var meter: Mixer.pair(link.levels, (master ? "mix:" : "ch:") + entry.id, mono)
    readonly property bool available: link.ready && !link.pending
    readonly property bool hardwareMuted: mono && link.snapshot && !!(link.snapshot.state || {})[Mixer.muteControl(entry.id)]
    readonly property string kind: master ? (entry.kind === "monitor" ? "MON" : entry.kind === "virtualMic" ? "MIC" : "AUX") : entry.hardware ? "INPUT" : entry.captureSource ? "CAPTURE" : "APP"
    width: 104
    height: 286
    color: face
    border.width: 1
    border.color: Qt.rgba(foreground.r, foreground.g, foreground.b, 0.2)

    Text {
        x: 4
        y: 6
        width: parent.width - 8
        height: 32
        text: root.entry.name
        textFormat: Text.PlainText
        font.family: root.family
        font.pixelSize: 12
        font.bold: true
        color: root.foreground
        wrapMode: Text.Wrap
        elide: Text.ElideRight
        maximumLineCount: 2
        horizontalAlignment: Text.AlignHCenter
    }
    Text {
        x: 4
        y: 40
        width: parent.width - 8
        text: root.kind + (root.entry.captureSource && !root.entry.captureConnected ? " OFF" : "")
        textFormat: Text.PlainText
        font.family: root.family
        font.pixelSize: 10
        color: root.foreground
        opacity: 0.65
        horizontalAlignment: Text.AlignHCenter
    }
    Text {
        x: 4
        y: 57
        width: parent.width - 8
        text: Math.round((fader.pressed ? fader.value : root.volume) * 100) + "%"
        font.family: root.family
        font.pixelSize: 12
        color: root.foreground
        horizontalAlignment: Text.AlignHCenter
    }
    Slider {
        id: fader
        objectName: "volume"
        property string pressedEntry: ""
        property string pressedMix: ""
        x: 14
        y: 87
        width: 32
        height: 144
        orientation: Qt.Vertical
        from: 0
        to: root.maximum
        stepSize: 0.01
        live: false
        enabled: root.available
        Accessible.name: root.entry.name + (root.master ? " master volume" : " send volume")
        onMoved: if (!pressed)
            root.link.send(Mixer.volumeCommand(root.entry, root.mix, root.master, value))
        onPressedChanged: {
            if (pressed) {
                pressedEntry = root.entry.id;
                pressedMix = root.mix;
            } else if (root.available && pressedEntry === root.entry.id && pressedMix === root.mix) {
                // A layout change during a drag must not edit the replacement strip.
                root.link.send(Mixer.volumeCommand(root.entry, root.mix, root.master, value));
            }
        }
        background: Rectangle {
            x: (fader.width - width) / 2
            y: fader.topPadding
            width: 1
            height: fader.availableHeight
            color: root.foreground
            opacity: 0.3
        }
        handle: Text {
            x: (fader.width - width) / 2
            y: fader.topPadding + fader.visualPosition * (fader.availableHeight - height)
            text: "╞═╡"
            font.family: root.family
            font.pixelSize: 12
            color: root.foreground
            opacity: fader.enabled ? 1 : 0.45
        }
        Binding {
            target: fader
            property: "value"
            value: root.volume
            when: !fader.pressed
            // Keep the confirmed value when a keyboard press starts a move.
            // Restoring the Slider's original zero would make Up jump to 1%.
            restoreMode: Binding.RestoreNone
        }
    }
    Text {
        x: 60
        y: 75
        text: root.mono ? "RMS" : "L R"
        font.family: root.family
        font.pixelSize: 10
        color: root.foreground
        opacity: 0.65
    }
    BlockMeter {
        objectName: "levelMeter"
        x: 60
        y: 91
        vertical: true
        meterPalette: root.meterPalette
        cells: 11
        pixelSize: 12
        foreground: root.foreground
        background: root.face
        fontFamily: root.family
        mono: root.mono
        leftLevel: root.meter[0]
        rightLevel: root.meter[1]
    }
    MixerButton {
        objectName: "sendMute"
        x: 4
        y: 235
        width: parent.width - 8
        height: 24
        foreground: root.foreground
        face: root.face
        family: root.family
        text: root.muted ? "[MUTE]" : "[ ON ]"
        marked: root.muted
        enabled: root.available && (root.master || root.mix !== "")
        Accessible.name: root.entry.name + (root.master ? " master mute" : " send mute")
        onClicked: root.link.send(Mixer.muteCommand(root.entry, root.mix, root.master))
    }
    MixerButton {
        objectName: "hardwareMute"
        x: 4
        y: 261
        width: parent.width - 8
        height: 22
        visible: root.mono
        foreground: root.foreground
        face: root.face
        family: root.family
        text: root.hardwareMuted ? "Mic muted" : "Mic on"
        marked: root.hardwareMuted
        enabled: root.available && !!root.link.snapshot && !!root.link.snapshot.connected && !!(root.link.snapshot.capabilities || {}).mute
        Accessible.name: root.entry.name + " hardware mute"
        onClicked: root.link.send({
            cmd: "set",
            control: Mixer.muteControl(root.entry.id),
            value: !root.hardwareMuted
        })
    }
}
