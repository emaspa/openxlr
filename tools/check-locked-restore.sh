#!/bin/sh
# Every path that builds a package must restore NuGet packages in locked
# mode against the committed lock files. This fails CI when one of them
# loses that flag, since only a release would otherwise notice.
set -eu
cd "$(dirname "$0")/.."
status=0
check() { grep -q -- "$2" "$1" || { echo "$1: missing $2" >&2; status=1; }; }
check .github/workflows/ci.yml "dotnet restore src/OpenXLR.slnx --locked-mode"
check .github/workflows/release-deb.yml "dotnet restore src/OpenXLR.slnx --locked-mode"
check .github/workflows/release-rpm.yml "dotnet restore src/OpenXLR.slnx --locked-mode"
check debian/rules "-p:RestoreLockedMode=true"
check packaging/rpm/openxlr.spec "-p:RestoreLockedMode=true"
check packaging/ppa/make-source.sh "--locked-mode"
check src/Directory.Build.props "<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>"
check src/Directory.Build.props "<RuntimeIdentifiers>linux-x64</RuntimeIdentifiers>"
for p in Core Daemon UI; do [ -f "src/OpenXLR.$p/packages.lock.json" ] || { echo "src/OpenXLR.$p/packages.lock.json missing" >&2; status=1; }; done
[ $status -eq 0 ] && echo "locked restore enforced on every packaging path"
exit $status
