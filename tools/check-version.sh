#!/bin/sh
# The release version lives in several files that different packagers read.
# They must all agree; CI runs this so a half-done bump fails before it is
# tagged. Prints every location and exits nonzero on a mismatch.
set -eu
cd "$(dirname "$0")/.."
props=$(sed -n 's/.*<Version>\([0-9.]*\)<\/Version>.*/\1/p' src/Directory.Build.props)
plugin=$(sed -n 's/.*"Version": *"\([0-9.]*\)".*/\1/p' plugin/com.emaspa.openxlr.sdPlugin/manifest.json)
nix=$(sed -n 's/.*version = "\([0-9.]*\)".*/\1/p' packaging/nix/package.nix)
spec=$(sed -n 's/^Version: *\([0-9.]*\).*/\1/p' packaging/rpm/openxlr.spec)
deb=$(sed -n '1s/^openxlr (\([0-9.]*\)).*/\1/p' debian/changelog)
printf '%-28s %s\n' src/Directory.Build.props "$props" plugin/manifest.json "$plugin" \
    packaging/nix/package.nix "$nix" packaging/rpm/openxlr.spec "$spec" debian/changelog "$deb"
status=0
for v in "$plugin" "$nix" "$spec" "$deb"; do
    [ "$v" = "$props" ] || status=1
done
[ -n "$props" ] || status=1
if [ $status -ne 0 ]; then echo "version mismatch: every file must say $props" >&2; fi
exit $status
