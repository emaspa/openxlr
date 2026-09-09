#!/bin/sh
# Run with --unshare=network --nodevice=all. All audio nodes stay on a private server.
set -eu
probe_dir=$(mktemp -d)
probe_pw= probe_pulse= probe_daemon= probe_filter=
cleanup() {
  for pid in "$probe_daemon" "$probe_filter" "$probe_pulse" "$probe_pw"; do
    if [ -n "$pid" ]; then kill -TERM "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true; fi
  done
  rm -rf "$probe_dir"
}
trap cleanup EXIT HUP INT TERM
export XDG_RUNTIME_DIR="$probe_dir/run" XDG_CONFIG_HOME="$probe_dir/config" XDG_DATA_HOME="$probe_dir/data"
export PULSE_SERVER="unix:$XDG_RUNTIME_DIR/pulse/native" PIPEWIRE_REMOTE=pipewire-0-manager
export LV2_PATH=/app/lib/lv2
mkdir -p "$XDG_RUNTIME_DIR" "$XDG_CONFIG_HOME" "$XDG_DATA_HOME"
chmod 700 "$XDG_RUNTIME_DIR"
cat > "$probe_dir/pipewire.conf" <<'CONF'
context.properties = { core.daemon = true core.name = pipewire-0 support.dbus = false }
context.spa-libs = { audio.convert.* = audioconvert/libspa-audioconvert support.* = support/libspa-support }
context.modules = [
 { name = libpipewire-module-protocol-native }
 { name = libpipewire-module-access args = { access.socket = { pipewire-0 = unrestricted pipewire-0-manager = unrestricted } } }
 { name = libpipewire-module-client-node }
 { name = libpipewire-module-spa-node-factory }
 { name = libpipewire-module-adapter }
 { name = libpipewire-module-metadata }
 { name = libpipewire-module-link-factory }
 { name = libpipewire-module-session-manager }
]
context.objects = [
 { factory = metadata args = { metadata.name = default } }
 { factory = spa-node-factory args = { factory.name = support.node.driver node.name = Dummy-Driver node.group = pipewire.dummy priority.driver = 20000 } }
]
CONF
pipewire -c "$probe_dir/pipewire.conf" > "$probe_dir/pipewire.log" 2>&1 & probe_pw=$!
pipewire-pulse > "$probe_dir/pulse.log" 2>&1 & probe_pulse=$!
probe_attempt=0
until timeout 2 pactl info >/dev/null 2>&1; do
  probe_attempt=$((probe_attempt + 1))
  if [ "$probe_attempt" -ge 100 ]; then cat "$probe_dir/pipewire.log" "$probe_dir/pulse.log"; exit 1; fi
  sleep 0.1
done
timeout 5 pactl load-module module-null-sink sink_name=flatpak_test_output >/dev/null
/app/lib/openxlr/daemon/OpenXLR.Daemon > "$probe_dir/daemon.log" 2>&1 & probe_daemon=$!
probe_attempt=0
until [ -s "$XDG_RUNTIME_DIR/openxlr/token" ]; do
  probe_attempt=$((probe_attempt + 1))
  if [ "$probe_attempt" -ge 200 ] || ! kill -0 "$probe_daemon" 2>/dev/null; then cat "$probe_dir/daemon.log"; exit 1; fi
  sleep 0.1
done
timeout 5 pw-dump > "$probe_dir/graph.json" || { cat "$probe_dir/pipewire.log" "$probe_dir/daemon.log"; exit 1; }
grep -q 'OpenXLR_ch_system' "$probe_dir/graph.json"
grep -q 'OpenXLR_stream' "$probe_dir/graph.json"
printf 'PASS: bundled daemon built application channels and virtual microphones on a private audio server\n'
pw-cli -m load-module libpipewire-module-filter-chain '{ node.description = "Flatpak LV2 probe" filter.graph = { nodes = [ { type = lv2 name = amp plugin = "http://lv2plug.in/plugins/eg-amp" control = { gain = -6.0 } } ] inputs = [ "amp:in" ] outputs = [ "amp:out" ] } capture.props = { node.name = flatpak_test_lv2_in audio.channels = 1 audio.position = [ MONO ] } playback.props = { node.name = flatpak_test_lv2_out audio.channels = 1 audio.position = [ MONO ] } }' > "$probe_dir/filter.log" 2>&1 & probe_filter=$!
probe_attempt=0
until timeout 2 pw-dump | grep -q 'flatpak_test_lv2_out'; do
  probe_attempt=$((probe_attempt + 1))
  if [ "$probe_attempt" -ge 50 ] || ! kill -0 "$probe_filter" 2>/dev/null; then cat "$probe_dir/filter.log"; exit 1; fi
  sleep 0.1
done
printf 'PASS: bundled PipeWire loaded an LV2 plugin and exported its nodes\n'
kill -TERM "$probe_daemon"
wait "$probe_daemon"
probe_daemon=
if timeout 5 pw-dump | grep -q 'OpenXLR_ch_system'; then echo 'Daemon left mixer nodes behind' >&2; exit 1; fi
printf 'PASS: daemon shutdown removed its mixer graph\n'
