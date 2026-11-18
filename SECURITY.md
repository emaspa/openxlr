# Security

## Reporting a vulnerability

Report it privately through GitHub: on the repository's **Security** tab,
choose **Report a vulnerability**. Do not open a public issue or post it
on Discord. Say what is affected, the version, and the steps that show
it. The answer comes in that report.

Only the latest release is supported. Fixes are not backported.

## In scope

- The daemon, the UI, the terminal mixer, the native plugin host and the
  USB helper.
- The control socket and the HTTP and WebSocket APIs on it.
- The OpenDeck plugin, the browser companion and its native messaging
  host, the GNOME extension and the Omarchy bar plugin.
- The packaging in this repository: the Debian, RPM, PPA, Nix and Flatpak
  recipes, the udev rule, the WirePlumber rules and the systemd units.
- The release workflows and the release assets, including a way to
  replace an asset, or to get an attestation that the commands in
  [docs/releases.md](docs/releases.md) accept for a file the release
  workflows did not build.

Out of scope: bugs in PipeWire, WirePlumber, OpenDeck, Wine, yabridge or
an audio plugin, unless OpenXLR makes them reachable in a way they are not
on their own.

## What the control socket trusts

The control socket gives a client the user's own rights. Any process
running as the same user can connect to it, and through it change the
mixer, add a plugin folder and load an audio plugin, which is native code,
and read and write OpenXLR's files. That process could already do all of
this without OpenXLR, so a report that a program of the same user can do
these things through the socket is not a vulnerability.

A way in for anyone else is: another local user, a web page, a browser
extension other than the OpenXLR companion, or another computer reaching
the socket or acting through the daemon. [docs/api.md](docs/api.md)
describes how the socket checks its peer.

## Checking a download

[docs/releases.md](docs/releases.md) lists every release asset, its
checksum file, and the command that checks which workflow built it.
