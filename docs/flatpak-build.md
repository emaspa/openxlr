# Building the experimental Flatpak

The manifest targets x86_64, matching the committed .NET runtime graph. It
uses the Freedesktop 25.08 runtime and .NET 10 SDK extension, and requires
Flatpak 1.15.11 or newer for the USB-only device grant. CI checks the
manifest and its files with `python3 packaging/flatpak/test-package.py`,
the NuGet source list with `python3 packaging/flatpak/nuget-sources.py
--check` and the metainfo with `appstreamcli validate --no-net`.

```sh
flatpak install --user flathub org.freedesktop.Sdk//25.08 org.freedesktop.Platform//25.08 org.freedesktop.Sdk.Extension.dotnet10//25.08 org.flatpak.Builder
python3 packaging/flatpak/nuget-sources.py --check
flatpak run org.flatpak.Builder --user --force-clean --disable-rofiles-fuse --state-dir="$HOME/.cache/openxlr-flatpak/state" --repo="$HOME/.cache/openxlr-flatpak/repo" "$HOME/.cache/openxlr-flatpak/build" packaging/flatpak/io.github.emaspa.openxlr.json
flatpak --user remote-add --no-gpg-verify --if-not-exists openxlr-local "$HOME/.cache/openxlr-flatpak/repo"
flatpak install --user openxlr-local io.github.emaspa.openxlr
```

Use a build directory and state directory under the home directory, since
the builder's private `/tmp` is not shared with commands it starts outside
its own sandbox. The manifest's directory source builds the current tree.
OpenXLR is not published on Flathub, whose rules do not allow AI-assisted
development; the build is for installing yourself.

The native host and its LV2 dependencies are built in the SDK. PipeWire
client tools and filter modules, ALSA's `amixer`, and WirePlumber's `wpctl`
are included. They connect to the host audio servers; the package never
starts its own PipeWire server or session manager in normal use.

The manifest grants the audio sockets, USB device nodes, graphics and
specific tray/focus D-Bus names. Only the host WirePlumber `wireplumber.conf.d` folder
and OpenXLR runtime directory are writable outside private app storage.
Imported LV2, CLAP and VST3 bundles live in app-private persistent folders.
The UI, catalogue and native host see those folders under the usual
home-directory names, without access to the host's plugin folders.
Loopback API and Stream Deck clients require the shared network namespace.
Audio clients identify their media category as Manager so WirePlumber can
permit global mixer controls, including default-device selection.
It does not grant whole-home access or the host-spawn service. KWin scripting
is deliberately excluded: that permission can execute desktop scripts outside
the sandbox. Focused-application routing is therefore not enabled by default;
use a native package for that integration. Portal-based output shortcuts
remain available.

After a NuGet update, regenerate `packaging/flatpak/nuget-sources.json` with
`python3 packaging/flatpak/nuget-sources.py`. The generator records package
versions and content identities from the lock files, and separately pins
the downloaded ZIP bytes. NuGet still verifies its own content hashes in
locked mode during the offline build.

Install LV2 plugins separately, for example:

```sh
flatpak install --user flathub org.freedesktop.LinuxAudio.Plugins.LSP//25.08
```

Run the acceptance checks described in [flatpak-testing.md](flatpak-testing.md) before
using the build with a live audio setup. The ordinary packages remain the
recommended installation. A clean-desktop login, a physical-output hearing
check and USB controls on the other supported models remain acceptance
work.
