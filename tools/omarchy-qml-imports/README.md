# Omarchy QML lint imports

These declarations describe only the external types used by the plugin.
They are inputs to `check-omarchy.py`, never installed or loaded by the shell.
QtQuick, Controls and WebSockets still use the real Qt type information.

`Style.font` and `Color` declare the `qs.Commons` values used by the bar.
The bar tests import these declarations by path and pass their scalar values
to the real bar content, with a fake daemon. They load no shell or file watcher.

`KeyboardPanel` follows `shell/Ui/KeyboardPanel.qml` in Omarchy's `quattro` tree.
It is declared as an Item here so lint can check its content without the
Quickshell window types. Its real anchoring, focus, sizing and popout
coordination need an Omarchy session to test. The bar is a dynamic object;
the plugin uses only the public `PluginBarApi.qml` members.

`FileView` follows the [Quickshell FileView contract](https://quickshell.org/docs/v0.2.0/types/Quickshell.Io/FileView/).
`Quickshell.env` reads the session environment in the real shell. These
declarations do no I/O and cannot test either operation.

Update these declarations when the plugin adopts another external member.
Also run qmllint against the installed shell on Omarchy. A clean check here
does not prove that an installed shell has these types or that they draw.
