# Optional Windows plugin bridge

`openxlr-yabridge` supplies a tested 64-bit bridge. Wine remains a separate
dependency. The libraries, chainloaders, Windows host and controller use
the commit in `source.json`, including the Wine 9.22+ window-position fix.
`private-plugin-home.patch` adds an environment-controlled output directory
to yabridgectl. It is the only local patch to the pinned source.

The package installs in `/usr/lib/openxlr/yabridge` and provides the separate
`openxlr-yabridgectl` command. Its registry is under
`$XDG_CONFIG_HOME/openxlr/bridge/yabridgectl`, and its wrappers are under
`$XDG_DATA_HOME/openxlr/yabridge/{vst3,clap,vst2}`. Normal XDG defaults apply.
OpenXLR supports VST3 and CLAP; the private VST2 destination keeps a mixed
folder sync from writing into another DAW's bridge tree.

## Install

Most people install it from the same place they installed OpenXLR:

```sh
yay -S openxlr-yabridge          # Arch, from the AUR
sudo apt install openxlr-yabridge   # Ubuntu, from the PPA
sudo dnf install openxlr-yabridge   # Fedora 44, from the COPR repository
```

On NixOS, set `services.openxlr.yabridgePackage` to the flake's
`openxlr-yabridge` package.

Every release also carries the packages themselves, next to a
`SHA256SUMS-yabridge.txt` to check them against. The
[Optional Windows bridge workflow](https://github.com/emaspa/openxlr/actions/workflows/yabridge.yml)
builds the same set from any branch, for review.

To install a package by hand, choose the one for your distribution and run
only the matching command. The companion supports x86-64 Linux and 64-bit
Windows plugins; it does not bundle Wine or 32-bit plugin support:

```sh
sudo apt install ./openxlr-yabridge_*_amd64.deb wine
sudo dnf install ./openxlr-yabridge-*.x86_64.rpm wine
sudo pacman -U ./openxlr-yabridge-*-x86_64.pkg.tar.zst
```

Restart the daemon with `systemctl --user restart openxlr-daemon`, then use
Options, "Bridge Wine's plugins", or install a Windows plugin folder.
OpenXLR selects the companion's matching libraries and host through its
isolated helper's PATH. It can still read existing system wrappers; new
wrappers are private. Other DAWs keep their existing environment and files.

For NixOS, import the OpenXLR module as shown in the [README](../../README.md#install),
then add this to the service configuration (`openxlr` is the flake input):

```nix
services.openxlr.yabridgePackage = openxlr.packages.x86_64-linux.openxlr-yabridge;
```

The module passes the store path to the daemon. Build it separately with
`nix build .#openxlr-yabridge` from the repository root.

`OPENXLR_YABRIDGE=system` opts out and uses the system/user bridge. An
absolute path selects a companion installed elsewhere. Restart the daemon
after changing this setting. The selected directory must contain the complete
payload and its `openxlr-yabridge.json` receipt; an incomplete directory is
not selected. Options shows the effective provider, version and path.
The system bridge needs its own synced
wrappers when opting out; OpenXLR's private files remain for switching back.

## Build it yourself

The packages above are the easy route. Building takes a few minutes and
needs a compiler for Windows binaries, which is what Wine's development
files provide.

Install the build tools first:

```sh
# Arch
sudo pacman -S --needed python meson ninja cmake pkgconf rust wine

# Debian or Ubuntu
sudo apt install python3 g++ meson ninja-build cmake pkg-config \
    wine wine64-tools libwine-dev libxcb1-dev libdbus-1-dev cargo rustc

# Fedora
sudo dnf install python3 gcc-c++ meson ninja-build cmake pkgconf-pkg-config \
    wine wine-devel libxcb-devel dbus-devel cargo rust
```

Then fetch the pinned source and build it. `prepare` is the only step that
downloads anything; it clones yabridge at the commit in `source.json`,
checks out the VST3 SDK it needs and vendors the Rust crates, so `build`
works offline afterwards:

```sh
python3 packaging/yabridge/build.py prepare --source /tmp/yabridge-source --output /tmp/yabridge-build
python3 packaging/yabridge/build.py build   --source /tmp/yabridge-source --output /tmp/yabridge-build --jobs "$(nproc)"
```

To use the result without making a package, install it under
`/usr/local`, which OpenXLR looks in without being told:

```sh
sudo python3 packaging/yabridge/build.py stage --source /tmp/yabridge-source \
    --output /tmp/yabridge-build --destdir / --prefix /usr/local
systemctl --user restart openxlr-daemon
```

That writes `/usr/local/lib/openxlr/yabridge` and the
`openxlr-yabridgectl` command beside it. Options then shows it as the
selected bridge, with its version and path. For any other location, stage it with
`--prefix` set to that root and set `OPENXLR_YABRIDGE` to the bridge
directory: both the daemon and the `openxlr-yabridgectl` wrapper read it,
and the wrapper falls back to the prefix it was staged with.

Three things bite when building inside a package builder rather than by
hand, and the recipes in this directory already handle them. Ubuntu names
the winelib compilers `winegcc-stable` and `wineg++-stable`, so the plain
names have to be on PATH. Wine reports no version at all without a home
directory it can write, and an empty version makes the build refuse to
compile VST3 support. And the distributions' hardened link flags reject
winelib output, which fails at the link with relocations in a read-only
segment.

## Prepare publication

Package creation also uses dpkg-dev, rpm, tar, xz and zstd.

```sh
python3 packaging/yabridge/build.py package --source /tmp/yabridge-source --output /tmp/yabridge-build --format deb
python3 packaging/yabridge/test-package.py /tmp/yabridge-build/stage-deb
```

Repeat `package` with `--format rpm`, `arch`, `srpm` or `debian-source` for
the other formats. Outputs and `SHA256SUMS` are in `dist` below the output
directory. Source RPMs can be submitted to COPR; Debian sources can be
signed and uploaded to a PPA.

Only `prepare` downloads source. Meson wrap revisions and SDK submodules
are pinned; Cargo uses its lock file and vendored dependencies. `build`
disables downloads. The corresponding source archive includes dependencies,
licenses, patches and the build recipe. Publish it alongside the binaries.

Build on the oldest supported distribution: newer compilers and glibc can
produce binaries needing newer runtime libraries. Debian and Arch packages
record those floors; RPM records ELF dependencies. CI uses Ubuntu 24.04.
Wine compatibility and editor input still require desktop acceptance tests.

The "Optional Windows bridge" workflow builds reviewable package and source
artifacts. It does not publish releases or alter OpenXLR's release version.
When updating the pin, update the Nix source hash, run private-sync tests,
and test controls, resizing, moving and reopening editors. Retain the old
packages for rollback, then sync private wrappers with the restored version.
