pragma Singleton
import QtQuick

QtObject {
    component Typography: QtObject {
        readonly property int body: 12
        readonly property string family: "monospace"
    }
    readonly property Typography font: Typography {}
}
