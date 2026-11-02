# Optional Windows plugin bridge

`openxlr-yabridge` supplies a tested 64-bit bridge. Wine remains a separate
dependency. The libraries, chainloaders, Windows host and controller use
the commit in `source.json`, including the Wine 9.22+ window-position fix.
Two local patches apply to the pinned source, and nothing else changes.
`private-plugin-home.patch` adds an environment-controlled output directory
to yabridgectl.

`disk-identity.patch` gives Windows plugins the system disk's identity. In
Wine, `\\.\PhysicalDrive0` is a placeholder that no drive backs. Its
answer to the storage property query has no model, firmware revision or
serial number. A plugin that ties its licence to a machine ID built from
that disk then sends an empty machine ID, and activation fails. With the
patch, the Windows host answers that one query with the model, firmware
revision and serial number of the NVMe drive that holds the Wine prefix,
laid out the way Windows' own NVMe driver reports them. It replaces only
Wine's empty answer. When the prefix is not on an NVMe drive, or the drive
cannot be read, the host leaves Wine's answer alone. The host's output
names the drive it found, with the serial masked.

The version in `source.json`, 5.1.1.54, is the companion's own label for
the pinned commit. Its last number is not a commit count. The commit is
yabridge 5.1.1 plus 42 commits, and `git describe` gives
`5.1.1-42-ge4bca2d3`.
The label stays, since 5.1.1.42 sorts below the published 5.1.1.54 and
every package manager would treat it as a downgrade. A move to a newer
commit takes a version above 5.1.1.54.

`source.json` also carries the package revision: the package version is
`<version>-<revision>`, and a rebuild of the same commit bumps the
revision. `build.py` strips any `-march`, `-mtune` or instruction-set flag
from the packager's `CFLAGS` and `CXXFLAGS` and compiles the host for
baseline x86-64: the bridge calls into Windows code through the Microsoft
calling convention, and GCC spills AVX registers around those calls to the
wrong stack slot, which faults on the first plugin factory call with
AVX-512 and reads garbage with AVX2. The Nix companion sets the same
target on the bridge derivation. The staged bridge directory carries
an `openxlr-yabridge.json` receipt with that version, the source commit,
the Wine input fix flag and the Windows architectures it bridges
(`x86_64`); the daemon reads the receipt before it selects a companion.

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
not selected. `stage` installs live only under `/usr/local`, so to put a
companion elsewhere, stage it with `--manager manual` into an empty
`--destdir`, copy its `usr/local/lib/openxlr/yabridge` directory to the
place you want and point the variable at the copy. Options shows the
effective provider, version and path.
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

Install through a package, or use the package-free local install:

```sh
python3 packaging/yabridge/build.py package --source /tmp/yabridge-source \
    --output /tmp/yabridge-build --format deb
# Install the matching .deb from /tmp/yabridge-build/dist with apt.
systemctl --user restart openxlr-daemon
```

For a package-free install, the explicit local prefix is still supported:

```sh
sudo python3 packaging/yabridge/build.py stage --source /tmp/yabridge-source \
    --output /tmp/yabridge-build --destdir / --prefix /usr/local --manager manual
systemctl --user restart openxlr-daemon
```

It builds a private staged inventory first, then publishes only those paths.
A path the previous local receipt owns is replaced whatever it holds now, so
the same command repairs a modified file that `openxlr doctor` reports. A
path the receipt does not own is left alone when it already equals the new
payload and refused otherwise, as are linked parents and regular files
blocking a parent directory; every refusal happens before the first file is
written and names all the conflicting paths at once. An install made by a
`stage` from before receipts has no receipt: its files are adopted when they
sit at the companion's own paths under `/usr/local` and its
`lib/openxlr/yabridge/openxlr-yabridge.json` is one that stage wrote, so the
upgrade replaces them and writes the receipt. With `--manager manual`, a
`--destdir` other than `/` runs this same install under that root, which lets
a packager or a check rehearse an upgrade against a copy of a tree.
No shared directory is inventoried or removed. Package-manager removal remains
preferred; a local install has no package-manager uninstall command. Inspect
its exact receipt with `openxlr doctor`, and move only verified owned paths
when removing it.

`stage` and `receipt` require `--manager`: `debian`, `rpm`, `arch`, `nix`,
or `manual` for the explicit local install. For an external AUR recipe:

```sh
# makedepends includes python; stage strips the companion before inventory.
options=('!strip')
# In package():
python3 openxlr-packaging/build.py stage --source . --output out \
    --destdir "$pkgdir" --manager arch
```

RPM retains the distribution's stripping and writes the receipt afterwards.
Debian source builds select the Debian manager when staging and regenerate
the receipt before collecting the final package checksums.
OpenXLR 0.5.0 carries the companion at 5.1.1.54 revision 3, the same
yabridge commit rebuilt with the disk identity patch. Options shows the
selected bridge with its version and path. `OPENXLR_YABRIDGE` selects an
installed bridge outside the standard locations in both the daemon and the
wrapper. See [installation receipts](../../docs/installation.md).

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
