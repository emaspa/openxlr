import QtQuick

Item {
    required property Item anchorItem
    required property QtObject bar
    property var owner
    property bool open: false
    property int padding: 8
    property var borderSpec
    property Item focusTarget
    property int contentWidth
    property int contentHeight
    function fittedContentWidth(value: int): int {
        return value;
    }
    function fittedContentHeight(value: int, cap: int): int {
        return Math.min(value, cap);
    }
}
