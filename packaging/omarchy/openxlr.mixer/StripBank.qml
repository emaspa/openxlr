pragma ComponentBehavior: Bound

import QtQuick

Column {
    id: root
    required property var entries
    required property string title
    required property string mix
    required property var link
    required property color foreground
    required property color face
    required property string family
    property bool master: false
    property var meterPalette: null
    property int page: 0
    readonly property int stripWidth: 104
    readonly property int capacity: Math.max(0, Math.floor(width / stripWidth))
    readonly property int pageCount: capacity > 0 ? Math.ceil(entries.length / capacity) : 0
    readonly property int first: page * capacity
    readonly property int shown: Math.min(capacity, Math.max(0, entries.length - first))
    readonly property int viewportWidth: capacity * stripWidth
    spacing: 4
    onPageCountChanged: page = Math.min(page, Math.max(0, pageCount - 1))

    Text {
        width: root.viewportWidth
        text: root.title
        elide: Text.ElideRight
        color: root.foreground
        font.family: root.family
        font.pixelSize: 11
    }
    Item {
        width: root.viewportWidth
        height: 286
        // Only this page is instantiated. There is no fractional scroll offset
        // or animation that could expose part of the next strip.
        Row {
            Repeater {
                model: root.shown
                MixerStrip {
                    required property int index
                    width: root.stripWidth
                    entry: root.entries[root.first + index]
                    mix: root.mix
                    master: root.master
                    meterPalette: root.meterPalette
                    link: root.link
                    foreground: root.foreground
                    face: root.face
                    family: root.family
                }
            }
        }
    }
    Row {
        width: root.viewportWidth
        visible: root.capacity > 0
        MixerButton {
            objectName: "previousPage"
            width: 28
            text: "<"
            foreground: root.foreground
            face: root.face
            family: root.family
            enabled: root.page > 0 && !root.link.pending
            Accessible.name: "Previous " + root.title + " page"
            onClicked: root.page--
        }
        Text {
            width: Math.max(0, root.viewportWidth - 56)
            height: 28
            text: (root.shown ? root.first + 1 : 0) + "-" + (root.first + root.shown) + "/" + root.entries.length
            color: root.foreground
            font.family: root.family
            font.pixelSize: 11
            horizontalAlignment: Text.AlignHCenter
            verticalAlignment: Text.AlignVCenter
        }
        MixerButton {
            objectName: "nextPage"
            width: 28
            text: ">"
            foreground: root.foreground
            face: root.face
            family: root.family
            enabled: root.page + 1 < root.pageCount && !root.link.pending
            Accessible.name: "Next " + root.title + " page"
            onClicked: root.page++
        }
    }
}
