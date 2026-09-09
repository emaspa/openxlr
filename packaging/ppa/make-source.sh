#!/bin/sh
# Signed Ubuntu source packages for the PPA (ppa:sparvoli/openxlr).
#
# Launchpad builds with no network, so the NuGet packages the publish
# needs are restored here and shipped inside the source tarball under
# vendor/nuget; debian/rules points the restore at that feed whenever
# the directory exists. The sources come from <git-ref>, the debian/
# directory from HEAD, and one source package is produced per series,
# versioned <upstream>~<series>1 (the source format is native).
#
# Usage:  packaging/ppa/make-source.sh <git-ref> <series>...
#         packaging/ppa/make-source.sh v0.1.22 noble resolute
#         dput ppa:sparvoli/openxlr dist/ppa/openxlr_*_source.changes
#
# The first argument is a git ref, so v0.1.22 and not 0.1.22.
#
# Launchpad refuses a version it has already seen, so a rebuild of the
# same upstream version needs a higher series suffix: PPA_SUFFIX=2 makes
# the packages X.Y.Z~noble2.
#
# Needs git, dotnet-sdk-10.0, devscripts, debhelper and the maintainer's
# GPG key (DEBSIGN_KEYID overrides the key picked from the changelog).
set -eu

ref=${1:?usage: $0 <git-ref> <series>...}
shift
[ $# -gt 0 ] || { echo "usage: $0 <git-ref> <series>..." >&2; exit 1; }

suffix=${PPA_SUFFIX:-1}

# Each series builds with its own dotnet SDK, and an SDK asks for the
# runtime packs of exactly the runtime it bundles. The SDK here is
# usually ahead of the one in an older series, so a feed vendored from
# this machine alone leaves that builder asking for packs it cannot
# reach (NU1102, no network on the builders). Vendor those versions too;
# add one whenever a series ships a runtime this list does not cover.
pack_versions=${PPA_RUNTIME_PACKS:-10.0.11 10.0.12}

repo=$(cd "$(dirname "$0")/../.." && pwd)
out=${OUT:-$repo/dist/ppa}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
       DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# Clean exports; nothing from the working tree goes in.
tree=$work/export
mkdir -p "$tree"
git -C "$repo" archive --format=tar "$ref" | tar -x -C "$tree"
rm -rf "$tree/debian"
git -C "$repo" archive --format=tar HEAD debian | tar -x -C "$tree"
version=$(dpkg-parsechangelog -l "$tree/debian/changelog" -S Version)
maint=$(dpkg-parsechangelog -l "$tree/debian/changelog" -S Maintainer)

# Restore exactly what debian/rules publishes into a private packages
# folder, then lay the .nupkg files out as a local feed.
for proj in OpenXLR.Daemon OpenXLR.UI; do
    dotnet restore "$tree/src/$proj" -r linux-x64 -p:SelfContained=false \
        --locked-mode --packages "$work/packages"
done

# The runtime packs for the other series, fetched without referencing
# them: PackageDownload puts them in the same folder the feed is laid
# out from.
mkdir -p "$work/packs"
{
    echo '<Project Sdk="Microsoft.NET.Sdk">'
    echo '  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>'
    echo '  <ItemGroup>'
    for v in $pack_versions; do
        for pack in Microsoft.NETCore.App.Runtime.linux-x64 \
                    Microsoft.NETCore.App.Host.linux-x64 \
                    Microsoft.AspNetCore.App.Runtime.linux-x64; do
            echo "    <PackageDownload Include=\"$pack\" Version=\"[$v]\" />"
        done
    done
    echo '  </ItemGroup>'
    echo '</Project>'
} > "$work/packs/packs.csproj"
dotnet restore "$work/packs/packs.csproj" --packages "$work/packages"

rm -rf "$tree"/src/*/obj
for dir in "$work"/packages/*/*/; do
    dest=$tree/vendor/nuget/${dir#"$work/packages/"}
    mkdir -p "$dest"
    cp "$dir"*.nupkg "$dir"*.nuspec "$dir"*.sha512 "$dest"
done

mkdir -p "$out"
for series in "$@"; do
    build=$work/openxlr-$series
    cp -a "$tree" "$build"
    (
        cd "$build"
        DEBEMAIL=$maint dch --newversion "$version~${series}$suffix" \
            --distribution "$series" --force-distribution \
            --force-bad-version \
            "Source package for the PPA, built for $series."
        # -d: the source builder needs no build dependencies; -z1: the
        # tarball is mostly .nupkg archives, which do not compress.
        dpkg-buildpackage -S -d -z1 ${DEBSIGN_KEYID:+--sign-key="$DEBSIGN_KEYID"}
    )
    mv "$work"/openxlr_"$version~${series}$suffix"* "$out/"
done

ls -l "$out"
echo "Upload with: dput ppa:sparvoli/openxlr $out/openxlr_<version>_source.changes"
