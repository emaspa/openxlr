import QtQuick

QtObject {
    property string path
    property bool preload: true
    property bool printErrors: true
    property bool watchChanges: false
    signal loaded
    signal loadFailed(int error)
    signal fileChanged()
    function text(): string {
        return "";
    }
    function reload(): void {
    }
}
