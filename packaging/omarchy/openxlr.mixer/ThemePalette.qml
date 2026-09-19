pragma ComponentBehavior: Bound

import QtQuick
import Quickshell
import Quickshell.Io
import "Mixer.js" as Mixer
import "Skins.js" as Skins

QtObject {
    id: root
    property string themeName: ""
    readonly property var palette: Mixer.themePalette(Skins.palettes, themeName)
    property FileView themeFile: FileView {
        path: Quickshell.env("HOME") + "/.local/state/omarchy/current/theme.name"
        printErrors: false
        watchChanges: true
        // Consume only the completed read. File changes reload asynchronously,
        // so the old text is never mistaken for the newly selected theme.
        onLoaded: root.themeName = root.themeFile.text()
        onFileChanged: root.themeFile.reload()
        onLoadFailed: root.themeName = ""
    }
}
