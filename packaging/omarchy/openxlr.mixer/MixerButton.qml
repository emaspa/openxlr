pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls

Button {
    id: root
    required property color foreground
    required property color face
    required property string family
    property bool marked: false
    implicitHeight: 28
    padding: 4
    focusPolicy: Qt.StrongFocus
    font.family: family
    font.pixelSize: 12
    Accessible.name: text
    contentItem: Text {
        text: root.text
        textFormat: Text.PlainText
        font: root.font
        color: root.marked ? root.face : root.foreground
        opacity: root.enabled ? 1 : 0.45
        horizontalAlignment: Text.AlignHCenter
        verticalAlignment: Text.AlignVCenter
        elide: Text.ElideRight
    }
    background: Rectangle {
        color: root.marked ? root.foreground : root.face
        border.width: root.activeFocus || root.hovered ? 1 : 0
        border.color: root.foreground
    }
}
