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

Choose the companion artifact for the distribution, then use its package
manager:

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

For NixOS, set `services.openxlr.yabridgePackage` to this flake's
`packages.x86_64-linux.openxlr-yabridge`. The module passes the store path
to the daemon. Build it separately with `nix build .#openxlr-yabridge`.

`OPENXLR_YABRIDGE=system` opts out and uses the system/user bridge. An
absolute path selects a companion installed elsewhere. Restart the daemon
after changing this setting. The system bridge needs its own synced
wrappers when opting out; OpenXLR's private files remain for switching back.

## Build and publish

Tools: Python 3, Git, Meson, Ninja, CMake, pkg-config, a C++ compiler,
winegcc/wineg++, Wine development files, XCB and D-Bus headers, Cargo and
Rust. Package creation also uses dpkg-dev, rpm, tar, xz and zstd.

```sh
python3 packaging/yabridge/build.py prepare --source /tmp/yabridge-source --output /tmp/yabridge-build
python3 packaging/yabridge/build.py build --source /tmp/yabridge-source --output /tmp/yabridge-build
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
