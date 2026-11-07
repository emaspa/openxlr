#!/bin/sh
set -eu
/app/bin/dotnet --info
make -C native clean
# DeepFilterNet is built in, from the crates cargo-sources.json unpacks
# under native/deepfilter/vendor and the model the manifest fetched; the
# build fails instead of leaving it out.
export DEEPFILTER=1
export CARGO_HOME="$PWD/native/deepfilter/.cargo-home"
cargo --version
runtime=$(cat packaging/flatpak/runtime-version)
for project in OpenXLR.UI OpenXLR.Daemon OpenXLR.Tui; do
    case "$project" in
        OpenXLR.UI) folder=ui ;;
        OpenXLR.Daemon) folder=daemon ;;
        OpenXLR.Tui) folder=tui ;;
    esac
    dotnet restore "src/$project/$project.csproj" -r linux-x64 --locked-mode \
        --source ./nuget-sources -p:NuGetAudit=false -p:SelfContained=false -p:RuntimeFrameworkVersion="$runtime" -p:TargetLatestRuntimePatch=false
    dotnet publish "src/$project/$project.csproj" -c Release -r linux-x64 \
        --no-self-contained --no-restore -warnaserror -p:RuntimeFrameworkVersion="$runtime" -p:TargetLatestRuntimePatch=false -p:EnableNativeLv2Host=true -o "/app/lib/openxlr/$folder"
done
chmod 755 /app/lib/openxlr/daemon/openxlr-control
ln -s ../lib/openxlr/daemon/openxlr-control /app/bin/openxlr-control
install -Dm755 packaging/flatpak/openxlr /app/bin/openxlr
install -Dm644 packaging/flatpak/io.github.emaspa.openxlr.metainfo.xml /app/share/metainfo/io.github.emaspa.openxlr.metainfo.xml
install -Dm644 packaging/openxlr.desktop /app/share/applications/io.github.emaspa.openxlr.desktop
sed -i 's/^Icon=openxlr$/Icon=io.github.emaspa.openxlr/' /app/share/applications/io.github.emaspa.openxlr.desktop
for size in 16 32 48 64 128 256; do
    install -Dm644 "src/OpenXLR.UI/Assets/icon-$size.png" "/app/share/icons/hicolor/${size}x${size}/apps/io.github.emaspa.openxlr.png"
done
for rule in packaging/70-openxlr.rules packaging/5*-*.conf; do
    install -Dm644 "$rule" "/app/share/openxlr/$(basename "$rule")"
done
install -Dm755 packaging/flatpak/openxlr-flatpak-service /app/share/openxlr/openxlr-flatpak-service
install -Dm644 docs/flatpak-build.md /app/share/doc/openxlr/flatpak-build.md
if [ -f docs/flatpak.md ]; then install -Dm644 docs/flatpak.md /app/share/doc/openxlr/flatpak.md; fi
mkdir -p /app/extensions/Plugins

install -Dm644 LICENSE /app/share/licenses/openxlr/LICENSE
# The notices of the code the native helper carries.
install -Dm644 native/rnnoise/COPYING /app/share/licenses/openxlr/rnnoise/COPYING
install -Dm644 native/deepfilter/libdf/LICENSE-MIT /app/share/licenses/openxlr/deepfilter/LICENSE-MIT
install -Dm644 native/deepfilter/libdf/LICENSE-APACHE /app/share/licenses/openxlr/deepfilter/LICENSE-APACHE
install -Dm644 native/signalsmith/LICENSE.txt /app/share/licenses/openxlr/signalsmith/stretch/LICENSE.txt
install -Dm644 native/signalsmith/signalsmith-linear/LICENSE.txt /app/share/licenses/openxlr/signalsmith/linear/LICENSE.txt
install -Dm644 native/clap/LICENSE /app/share/licenses/openxlr/clap/LICENSE
install -Dm644 native/vst3/LICENSE /app/share/licenses/openxlr/vst3/LICENSE
# The starter sounds' sources and authors; the clips are installed beside the daemon.
install -Dm644 packaging/starter-sounds/CREDITS /app/share/licenses/openxlr/starter-sounds/CREDITS
install -Dm644 src/OpenXLR.UI/Assets/Fonts/OFL.txt /app/share/licenses/openxlr/fira-sans/OFL.txt
