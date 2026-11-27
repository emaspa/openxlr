# Installation receipts and doctor

Run `openxlr doctor` in a terminal to check an installation. It does not
open the mixer, start or stop services, change audio routing, or repair
anything automatically. It reports each recorded path that is missing,
has different contents or permissions (including special permission bits), or has become a different symbolic
link. Each finding includes a repair or inspection command. Review that
command before running it, particularly if a file was edited on purpose.

The daemon check compares the installed daemon assembly's build identity
with the running daemon's authenticated state. It detects an old daemon
left running after an upgrade even when both builds have the same version.
Doctor names a daemon from before the control socket, still running after
an upgrade, and gives `systemctl --user restart openxlr-daemon.service` as
the repair. A daemon that is not running gets
`systemctl --user start openxlr-daemon.service`. Any other unreachable
daemon is reported as unverified, with the restart as the repair. An absent
receipt is reported as unknown ownership, not as a healthy installation.

When the running daemon has
[OpenXLR's Windows plugin host](manual.md#windows-plugin-host), doctor also
asks it about Windows plugins, with or without a receipt. It adds notes for
the folders the host looks in, each folder the daemon cannot see, and each
Windows plugin its last scan left out, with the reason. Before those come
the notes to read before removing the `openxlr-yabridge` companion: each
insert whose plugin is listed only through a yabridge wrapper, with the
wrapper and the reason, and each companion folder the daemon could not
copy into the Windows plugin folders
([Windows plugins without yabridge](manual.md#windows-plugin-folders)).
With or without the host, a note names the inserts with a Windows plugin
when the daemon finds no Wine. These are
information, not findings, so they do not change the exit code. At most 32
are printed; the last one counts the rest.

```sh
openxlr doctor
openxlr doctor --json
openxlr doctor --no-runtime
```

Exit codes are 0 when the requested checks pass, 1 for findings, and 2 for
invalid arguments or a maintenance command that could not be applied.
`--no-runtime` skips the daemon comparison. `--json` includes the receipts,
findings, suggested commands, informational notes and daemon result; it
does not include tokens or the contents of user files. Terminal messages
are in English.
An explicit companion receipt can be checked on its own with `--no-runtime`
or as a staged payload with `--root`; it does not require the main package.

## What is recorded

New Debian, RPM, Arch (AUR) and Nix packages include a receipt at
`<prefix>/share/openxlr/installations/openxlr.json`. The optional bridge
can use its own `openxlr-yabridge.json` in that directory. This inventory is
separate from the bridge's existing compatibility marker under
`lib/openxlr/yabridge`.

Each receipt records the package manager, package version, full source
commit when available, whether the source was modified, and the final
payload's exact paths. A regular file has a SHA-256 digest and permissions;
a symbolic link has its target. Absolute and relative links to the same
location are equivalent, including Nix links normalized after fixup. Shared directories are not owned
and are never removed by OpenXLR. The receipt lists its own path without
a self-hash. Package-manager databases remain responsible for verifying
the receipt itself; this is an inventory, not a signed attestation.

The inventory includes the packaged unit, udev rule, WirePlumber rules,
pipewire-pulse drop-in, desktop entry, binaries, plugin host and other
payload files under the prefix. Files outside it, such as the browser
companion's native messaging manifests under `/etc`, are not recorded. NixOS generates its user unit outside the package output;
that declarative configuration belongs to NixOS, not to the package's
receipt. Profiles, mixer settings, plugins installed separately by the
user, runtime sockets and caches are user data, not installation payload.

Nix receipts use the read-only permissions the store applies when a build
finishes. Package discovery follows profile links to the actual receipt,
including profiles that link its parent directories;
explicit receipt arguments refuse links.

The daemon saves package receipts under
`~/.config/openxlr/installation-history` when it starts. To record them
without starting the daemon:

```sh
openxlr installation remember
```

These private copies survive an upgrade or package removal. Doctor compares
old inventories with the current one and reports remaining paths that the
current package no longer claims. It suggests moving an unchanged leftover
under the package prefixes (the running installation's own, `/usr` and
`/usr/local`) to an explicit backup path, with an interactive overwrite
prompt. Modified paths, and paths anywhere else, get an inspection command
instead: a saved record proves its own integrity, not who wrote it. A
rejected current receipt disables historical
leftover suggestions until the current inventory can be read again, so active
files are not mistaken for retired paths. Unreadable receipt locations remain
in discovery and are reported rather than silently treated as missing.
An invalid historical receipt gets
a command to move that private copy aside, not to reinstall the package;
saving history skips it and keeps recording newer inventories. Nix store outputs are managed by Nix's
garbage collector and are not treated as stale installation leftovers.
Nix receipts are not archived. Rebuilds of the same package, prefix and
version share one record, retaining paths retired by those rebuilds.
History is bounded to 128 versions and doctor reads at most 16 MiB of
receipts per run; saving history has the same combined byte limit. Older records can be retained elsewhere and checked
individually with `--receipt`.

Ownership before receipts were introduced cannot be reconstructed safely.
An external package recipe must include the inventory hook below to supply
one. The AUR recipe, maintained outside this repository, runs the hook
from 0.3.3; another external recipe has to add it itself. Package receipts do not take ownership of `/etc` overrides or
files supplied by another installer.

## Startup ownership

Options records the exact desktop entry, source-build user unit and
`default.target.wants` link it manages in
`~/.config/openxlr/installation.json`. Replaced user files are saved with
their original hash and mode under `installation-backups`. Backups are
written before their receipt entry is published. Disabling startup removes
only unchanged owned files, restoring a recorded original where applicable.
It does not delete directories or infer ownership from a filename. Existing
directories at startup-file paths are rejected before recording a write.

On upgrade, startup files identical to the UI's generated templates or
the source guide's reference unit are adopted before removal. The templates
of every release since v0.1.0 are recognized: the launch path is read from
the file's own `Exec` or `ExecStart` line and the file must equal that
release's template for it byte for byte. The reference unit may differ only
in its `ExecStart` line. The `default.target.wants` link is adopted when it
points at the packaged unit or at the user unit path, whether or not that
unit still exists, since `systemctl --user enable` leaves it behind. Doctor
reports a recognized file that no receipt records yet as information, not
as a finding; it is adopted by the next startup change in Options or by
`installation repair-startup`. A file doctor does not recognize is a
finding with a command to move it aside. Modified and foreign files still
require inspection, and a refused startup change leaves every file as it
was. If restoration leaves an original user unit shadowing the packaged
one, enabling is refused and its owned enable link is removed, so the
original is not silently started at the next login. A restored foreign
desktop entry may still start the UI, so Options reports that startup
could not be disabled.

```sh
openxlr doctor
# Reapply only startup preferences that are already enabled.
openxlr installation repair-startup
# Migrate recognized old files, preflight all paths, remove owned files.
openxlr installation remove-startup
systemctl --user daemon-reload
```

Removal keeps receipts and originals for diagnosis and does not change the
saved preferences or stop a running daemon. Turn the options off when
removing startup integration permanently. If a startup receipt is malformed
or uses a future format, doctor names a user-file recovery command:

```sh
openxlr installation reset-startup-receipt
openxlr installation repair-startup
```

Reset preserves the invalid receipt as `installation.json.before-recovery-*`
and leaves every startup file and original backup untouched. It refuses to
reset a valid receipt. Reapplying can adopt recognized generated files;
foreign files still require an explicit choice. Keep the saved receipt if
returning to a newer application version.

Symbolic links at the startup file itself or in `autostart`, `systemd` or
`systemd/user` are refused. For Stow or dotfile-managed directories, manage
these entries in the dotfile repository instead of through Options. This
restriction prevents a redirected path from extending receipt ownership.

Uninstall packaged files with the package manager that installed them.
Doctor suggests `apt-get install --reinstall`, `dnf reinstall`, an AUR
rebuild with `paru`, or `nix-store --repair-path` for package damage. Use
your original Arch package source if it is not an AUR package. OpenXLR
does not run a privileged, directory-based uninstaller. After a full
uninstall, run doctor from another OpenXLR build and pass a saved receipt:

```sh
openxlr doctor --removed --receipt ~/.config/openxlr/installation-history/<sha256>.json
```

`--removed` checks for remaining paths rather than reporting expected
missing files. It requires an explicit receipt and skips the running daemon. Saved copies
must be named with the SHA-256 of their exact bytes followed by `.json`.
Live receipts must contain a self entry matching their actual path; copies
without a matching content-addressed name are refused.

## Packaging hook

Generate the receipt after stripping, wrapping and compression, and before
the package manager creates its database metadata. The staging root must
contain only this package's payload. Python is needed at build time only.

```sh
python3 packaging/install_receipt.py --root "$pkgdir" --prefix /usr \
  --manager arch --version "$pkgver-$pkgrel"

# Check a staged payload without inspecting the host's daemon or config.
openxlr doctor --root "$pkgdir" \
  --receipt "$pkgdir/usr/share/openxlr/installations/openxlr.json" --json
```

`--manager` accepts `debian`, `rpm`, `arch` and `nix`. The companion also
uses `manual` for its explicit package-free `/usr/local` installation. The prefix is the
absolute installed prefix, not the staging path. Pass a full `--commit`
when the builder already knows it. Otherwise the generator uses Git HEAD
and its tracked-file dirty status, or `packaging/source-commit`, which
`git archive` substitutes through `.gitattributes`. Unknown source
provenance and modified source trees are informational, not failing checks.

An Arch recipe generating its receipt in `package()` must use
`options=('!strip')` and include `python` in `makedepends`, because makepkg
otherwise strips binaries after `package()` and invalidates their hashes.
The packaged receipt generator must run after any manual stripping.

Receipt format 1 is JSON with `formatVersion`, `package`, `manager`,
`version`, `commit`, `dirty`, `prefix` and `files`. File entries carry
`path`, `kind` (`file`, `link` or `receipt`), and the applicable `sha256`,
`mode` or `linkTarget`. Documentation under `share/doc` and `share/licenses`
is marked `optional`: if present it is checked, but its absence is allowed
for package-manager documentation exclusions. User entries can also carry `replacedSha256`,
`replacedMode` and `removed`. Paths are absolute and normalized, confined
to the prefix and unique. SHA-256 digests use lowercase hexadecimal.
The package generator rejects unsupported paths and link targets before
producing a receipt. Duplicate JSON properties, invalid hashes,
linked receipt files, contradictory entry fields, special file types and
oversized records are refused. A staged check also refuses symbolic links
in the installation prefix inside the staging root.
