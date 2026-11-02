// OpenXLR plugin for OpenDeck (OpenAction / Stream Deck SDK compatible).
// A thin bridge: one WebSocket to the OpenDeck host, one to the OpenXLR
// daemon. The daemon owns all state and broadcasts every change, so keys
// and dials stay in sync with the UI (and with the hardware) for free.

import process from "node:process";
import {ControlSocket} from "./control-socket.mjs";
import { KeyCommands } from "./key-commands.mjs";
import { channelName, mixName, mixShortName, defaultMonitorMix, monitorFeed, layoutChoices, controllableOutputs, outputKey, targetAppearance, openGroupMember } from "./layout-choices.mjs";
import { SkinPalette } from "./skin-palette.mjs";
import { captureGainOf, channelUnitControl, channelUnitTarget, deviceTargetSupported, duckingEnabled, duckingToggle, unitDialLabel, unitKeyLabel, unitMeterKey } from "./unit-inputs.mjs";
import { isEqTarget, eqTarget, eqStep, eqCommand, eqLabel, eqDial } from "./microphone-eq.mjs";
import { isVoiceTarget, voiceTarget, voiceMode, voicePress, voiceRelease, voiceLabel } from "./voice-changer.mjs";
import { isEqProfileTarget, eqProfileTarget, eqProfilePress, eqProfileLabel } from "./eq-profiles.mjs";
import { CLIP_ACTION, clipKey, clipIdOf, clipPress, clipProgress, progressBucket, clipAppearance, clipChoices } from "./soundboard.mjs";

// ---------- launch arguments ----------
const arg = (name) => {
  const i = process.argv.indexOf(name);
  return i >= 0 ? process.argv[i + 1] : undefined;
};
const port = arg("-port");
const pluginUUID = arg("-pluginUUID");
const registerEvent = arg("-registerEvent");

// ---------- static naming ----------
// Toggle targets on the device state block, with short key labels. The
// labels of the two inputs' controls are the names an older plugin drew; the
// key face now names the channel that carries the input (unit-inputs.mjs).
const DEVICE_TOGGLES = {
  mute: "XLR 1\nMute", mute2: "XLR 2\nMute",
  phantom: "XLR 1\n48V", phantom2: "XLR 2\n48V",
  lowCut: "XLR 1\nLow Cut", lowCut2: "XLR 2\nLow Cut",
  expander: "XLR 1\nExpander", expander2: "XLR 2\nExpander",
  voiceTune: "XLR 1\nVoice Tune", voiceTune2: "XLR 2\nVoice Tune",
  clipGuard: "XLR 1\nClipGuard", clipGuard2: "XLR 2\nClipGuard",
  compressor: "XLR 1\nComp", compressor2: "XLR 2\nComp",
  lowImpedance: "Low Z", auxLevelLock: "Aux In\nLock", lineInLevelLock: "Line In\nLock",
  outHp1: "HP 1\nOut", outHp2: "HP 2\nOut", outLineOut: "Line\nOut",
  gainLocked: "Gain\nLock",
  softClipGuard: "Clip\nGuard",
  ducking: "Ducking",
};
// Targets whose ON state means "muted" (shown red instead of lit green).
const MUTE_LIKE = new Set(["mute", "mute2"]);
// Dials on the interface's own controls, which a turn or press of an
// unsupported one answers with an alert.
const DEVICE_DIALS = new Set(["gain", "gain2", "hp", "hp2", "auxLevel", "lineIn", "crossfade"]);

// ---------- daemon connection ----------
let daemon = null;
const layoutInspectors = new Set();
let lastLayout = "";
function publishLayout() {
  if (!layoutInspectors.size) return;
  const choices = layoutChoices(daemonState?.mixer, daemonState?.devices, daemonState?.eqProfiles);
  const serialized = JSON.stringify(choices);
  if (serialized === lastLayout) return;
  lastLayout = serialized;
  for (const context of layoutInspectors)
    send({ event: "sendToPropertyInspector", context, payload: choices });
}
// Clip key inspectors, sent the library again whenever it changes.
const clipInspectors = new Set();
let lastClips = "";
function publishClips() {
  if (!clipInspectors.size) return;
  const choices = clipChoices(daemonState?.mixer, daemonUp);
  const serialized = JSON.stringify(choices);
  if (serialized === lastClips) return;
  lastClips = serialized;
  for (const context of clipInspectors)
    send({ event: "sendToPropertyInspector", context, payload: { soundboard: choices } });
}
let daemonState = null;   // last full {"type":"state"} message
let daemonUp = false;
// A short word keys and dials show instead of "offline" when the adapter
// said why it could not connect: a daemon from before the update, or an
// OpenXLR installation older than this plugin.
let daemonNotice = null;
let daemonNoticeMessage = "";
let meterLevels = null;   // last {"type":"meters"} levels, keyed ch:/mix:
let catalog = new Map();  // LV2 plugin URI -> PluginInfo (names and ranges of the params)
let reconnectTimer = null;
let reconnectDelayMs = 500;
let connectionGeneration = 0;
const keyCommands = new KeyCommands(payload => cmd(payload), (context, failed) =>
  send({event: failed ? "showAlert" : "showOk", context}));

function scheduleDaemonReconnect(generation) {
  if (generation !== connectionGeneration || reconnectTimer) return;
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    connectDaemon();
  }, reconnectDelayMs);
  reconnectDelayMs = Math.min(reconnectDelayMs * 2, 10000);
}

function connectDaemon() {
  const socket = new ControlSocket();
  const generation = ++connectionGeneration;
  daemon = socket;
  socket.onopen = () => {
    if (daemon !== socket) return;
    daemonUp = true;
    daemonNotice = null; daemonNoticeMessage = "";
    reconnectDelayMs = 500;
    cmd({ cmd: "listPlugins" });
    refreshAll();
  };
  socket.onmessage = (e) => {
    if (daemon !== socket) return;
    let m;
    try { m = JSON.parse(e.data); } catch { return; }
    if (m.type === "state") { daemonState = m; pruneEqTurns(); refreshAll(); refreshMeters(); publishLayout(); publishClips(); }
    else if (m.type === "meters") { meterLevels = m.levels; refreshMeters(); }
    else if (m.type === "plugins") {
      catalog = new Map((m.plugins ?? []).map((p) => [p.plugin, p]));
      refreshAll();
    }
    else if (m.type === "commandResult") keyCommands.finish(m.requestId, !!m.error);
    else if (m.type === "error") console.error("OpenXLR daemon:", m.message);
  };
  socket.onclose = (e) => {
    if (daemon !== socket) return;
    if (e && e.code === 1008) console.error("OpenXLR daemon refused the plugin:", e.reason);
    if (socket.error) {
      daemonNotice = {olderDaemon: "restart", olderOpenXLR: "update"}[socket.error.reason] ?? null;
      if (socket.error.message !== daemonNoticeMessage) {
        daemonNoticeMessage = socket.error.message;
        console.error("OpenXLR:", daemonNoticeMessage);
      }
    } else daemonNotice = null;
    daemonUp = false; daemonState = null;
    clearDialTurns();
    meterLevels = {};
    keyCommands.disconnect();
    // The daemon releases this connection's held clips when it closes.
    heldClips.clear(); holdCount.clear();
    heldVoices.clear();
    refreshAll();
    refreshMeters();
    publishClips();
    scheduleDaemonReconnect(generation);
  };
}
const cmd = (o) => {
  if (!daemonUp || !daemon || daemon.readyState !== WebSocket.OPEN) return false;
  try {
    return daemon.send(JSON.stringify(o)) !== false;
  } catch {
    daemonUp = false;
    try { daemon.close(); } catch { /* onclose/retry handles the rest */ }
    return false;
  }
};

// ---------- OpenDeck host connection ----------
const host = new WebSocket(`ws://localhost:${port}`);
const send = (o) => host.send(JSON.stringify(o));
host.onopen = () => send({ event: registerEvent, uuid: pluginUUID });
host.onclose = () => process.exit(0);

// Visible action instances: context -> {action, settings, controller}
const instances = new Map();

// OpenDeck keeps one persisted title field per key, shared by the plugin's
// setTitle and the user's own edits. So the plugin fills in a default title
// only while the title is empty, and never overwrites one the user typed.
// Clearing the title restores the default. Everything the user needs to read
// lives in the key image (frame colour, glyph, and the low-cut frequency),
// not the title, so a custom name never hides the state.
const emptyTitle = new Set();   // contexts whose OpenDeck title is ""

// The default label for a target, drawn INSIDE the key image (so it uses our
// styling, not the host's title font). A user-typed title replaces it: the
// host draws that on top and the image drops its own label.
function defaultTitle(target) {
  if (target === "softLowCut") return "Low Cut";
  return toggleLabel(target);
}

// ---------- inserts ----------
// Insert targets use "|" as the separator because a mix chain's channel key
// ("mix:monitor") already contains a colon:
//   insert|<channel>|<insertId>            key: bypass one insert
//   inschain|<channel>                     key: bypass the whole chain
//   insparam|<channel>|<insertId>|<symbol> dial: one plugin control
const isInsertTarget = (t) =>
  !!t && (t.startsWith("insert|") || t.startsWith("inschain|") || t.startsWith("insparam|"));

// Chains the daemon exposes, in the order the UI shows them.
const chainName = (ch) =>
  ch.startsWith("mix:") ? `${mixName(mixer(), ch.slice(4))} mix` : channelName(mixer(), ch);
const chainShort = (ch) =>
  ch.startsWith("mix:") ? `${mixShortName(mixer(), ch.slice(4))} mix` : channelName(mixer(), ch);
const insertsOf = (ch) => (mixer()?.inserts?.[ch] ?? []).map((s) => s.insert ?? s);

// A short plugin name for a key face: drop the vendor prefix and the
// channel-count suffix ("LSP Compressor Mono" -> "Compressor").
function pluginShort(label, uri) {
  let s = label || catalog.get(uri)?.name || uri?.split("/").pop() || "Plugin";
  s = s.replace(/^LSP\s+/i, "").replace(/\s+(Mono|Stereo|MidSide|LR|MS|x\d)$/i, "");
  return s;
}

// Find an insert by id; when the id is gone (chain rebuilt by a profile
// recall) fall back to the same plugin in the same chain, preferring the
// slot the key was made for. meta = {plugin, index} saved by the PI.
function resolveInsert(ch, id, meta) {
  const list = insertsOf(ch);
  const byId = list.find((i) => i.id === id);
  if (byId) return byId;
  if (!meta?.plugin) return null;
  const same = list.filter((i) => i.plugin === meta.plugin);
  if (!same.length) return null;
  return list[meta.index]?.plugin === meta.plugin ? list[meta.index] : same[0];
}
const metaOf = (inst, t) => inst?.settings?.meta?.[t];

// The catalog's description of one control, or null before the catalog
// arrived (or when the plugin is not installed any more).
const paramInfo = (uri, symbol) => catalog.get(uri)?.params?.find((p) => p.symbol === symbol) ?? null;

// Value to 0..100 along the control's own scale.
function paramPct(p, v) {
  if (p.toggled) return v > 0 ? 100 : 0;
  if (p.logarithmic && p.min > 0 && p.max > p.min)
    return Math.round((Math.log(v / p.min) / Math.log(p.max / p.min)) * 100);
  if (p.max === p.min) return 0;
  return Math.round(((v - p.min) / (p.max - p.min)) * 100);
}
function paramText(p, v) {
  if (p.toggled) return v > 0 ? "ON" : "OFF";
  const sp = p.scalePoints?.find((s) => Math.abs(s.value - v) < 1e-6);
  if (sp) return sp.label;
  if (p.integer) return String(Math.round(v));
  const a = Math.abs(v);
  return v.toFixed(a >= 100 ? 0 : a >= 10 ? 1 : 2);
}
// One dial tick along the control's scale; enumerations step through
// their scale points, toggles flip on any movement.
function paramStep(p, v, ticks) {
  const clamp = (x) => Math.min(p.max, Math.max(p.min, x));
  if (p.toggled) return ticks > 0 ? 1 : 0;
  if (p.enumeration && p.scalePoints?.length) {
    const pts = [...p.scalePoints].sort((a, b) => a.value - b.value);
    let i = pts.findIndex((s) => Math.abs(s.value - v) < 1e-6);
    if (i < 0) i = 0;
    return pts[Math.min(pts.length - 1, Math.max(0, i + Math.sign(ticks)))].value;
  }
  if (p.integer) return clamp(Math.round(v) + ticks);
  if (p.logarithmic && p.min > 0 && p.max > p.min)
    return clamp(v * Math.pow(p.max / p.min, ticks / 100));
  return clamp(v + ((p.max - p.min) / 100) * ticks);
}

// Everything the property inspectors can offer, built from live state.
function insertChoices() {
  const inserts = [], chains = [], params = [];
  for (const ch of Object.keys(mixer()?.inserts ?? {})) {
    const list = insertsOf(ch);
    if (list.length)
      chains.push({ target: `inschain|${ch}`, label: `${chainName(ch)}: whole chain` });
    list.forEach((ins, index) => {
      const name = pluginShort(ins.label, ins.plugin);
      const meta = { plugin: ins.plugin, index };
      inserts.push({ target: `insert|${ch}|${ins.id}`, label: `${chainName(ch)}: ${name}`, meta });
      for (const p of catalog.get(ins.plugin)?.params ?? [])
        params.push({ target: `insparam|${ch}|${ins.id}|${p.symbol}`,
                      label: `${chainName(ch)}: ${name}: ${p.name}`, meta });
    });
  }
  return { inserts, chains, params };
}

// Earlier versions pushed default titles into the host's persisted title
// field; recognise and clear those once so the in-image label takes over.
function isLegacyDefaultTitle(inst, title) {
  if (title === "offline" || title === "OpenXLR") return true;
  const t = inst.settings.target ?? "";
  if (t === "softLowCut" && title.startsWith("Low Cut")) return true;
  return title === defaultTitle(t) || (Object.hasOwn(DEVICE_TOGGLES, t) && title === DEVICE_TOGGLES[t]);
}

// A dial can hold a stack of targets; long-pressing the strip cycles them.
const targetsOf = (inst) =>
  Array.isArray(inst.settings.targets) && inst.settings.targets.length
    ? inst.settings.targets
    : inst.settings.target ? [inst.settings.target] : [];
const activeTarget = (inst) => {
  const ts = targetsOf(inst);
  return ts.length ? ts[(inst.settings.activeIndex ?? 0) % ts.length] : undefined;
};
// Which gesture cycles the stack (the other keeps its mute role); only
// meaningful once the stack has at least two entries.
const cyclesOn = (inst, gesture) =>
  targetsOf(inst).length > 1 && (inst.settings.cycleGesture ?? "tap") === gesture;
function cycleStack(context, inst) {
  const ts = targetsOf(inst);
  if (ts.length < 2) return;
  inst.settings.activeIndex = ((inst.settings.activeIndex ?? 0) + 1) % ts.length;
  send({ event: "setSettings", context, payload: inst.settings });
  refresh(context);
  refreshEqMeter(context);
}

host.onmessage = (e) => {
  let m;
  try { m = JSON.parse(e.data); } catch { return; }
  const inst = instances.get(m.context);
  switch (m.event) {
    case "willAppear":
      keyCommands.clear(m.context);
      clearEqTurns(m.context);
      instances.set(m.context, {
        action: m.action,
        settings: m.payload?.settings ?? {},
        controller: m.payload?.controller ?? "Keypad",
      });
      refresh(m.context);
      refreshEqMeter(m.context);
      break;
    case "willDisappear":
      releaseHeldClip(m.context);
      releaseHeldVoice(m.context);
      keyCommands.clear(m.context);
      clearEqTurns(m.context);
      instances.delete(m.context);
      emptyTitle.delete(m.context);
      lastMeter.delete(m.context);
      marquee.delete(m.context);
      break;
    case "didReceiveSettings":
      releaseHeldClip(m.context);
      releaseHeldVoice(m.context);
      keyCommands.clear(m.context);
      if (inst) {
        const previous = eqBinding(inst);
        inst.settings = m.payload?.settings ?? {};
        if (eqBinding(inst) !== previous) clearEqTurns(m.context);
        refresh(m.context);
        refreshEqMeter(m.context);
      }
      break;
    case "keyDown":
      if (inst?.action === CLIP_ACTION) onClipDown(m.context, inst, m.payload?.isInMultiAction === true);
      else if (inst) onKeyDown(m.context, inst, m.payload?.isInMultiAction === true);
      break;
    case "keyUp":
      releaseHeldClip(m.context);
      releaseHeldVoice(m.context);
      break;
    case "dialRotate":
      if (inst) onDialRotate(m.context, inst, m.payload?.ticks ?? 0);
      break;
    case "dialDown":
      if (!inst) break;
      if (cyclesOn(inst, "push")) cycleStack(m.context, inst);
      else onDialPress(m.context, inst);
      break;
    case "touchTap":
      if (!inst) break;
      if (cyclesOn(inst, "tap")) cycleStack(m.context, inst);
      else onDialPress(m.context, inst);
      break;
    case "titleParametersDidChange": {
      const title = m.payload?.title ?? "";
      if (title === "") { emptyTitle.add(m.context); refresh(m.context); }
      else if (inst && isLegacyDefaultTitle(inst, title)) {
        send({ event: "setTitle", context: m.context, payload: { title: "" } });
      } else { emptyTitle.delete(m.context); refresh(m.context); }
      break;
    }
    case "sendToPlugin":
      if (m.payload?.request === "layout") {
        layoutInspectors.add(m.context);
        // Record what this inspector now shows, so the next state that
        // differs from it is published even if it matches an older one.
        const choices = layoutChoices(daemonState?.mixer, daemonState?.devices, daemonState?.eqProfiles);
        lastLayout = JSON.stringify(choices);
        send({ event: "sendToPropertyInspector", context: m.context, payload: choices });
      }
      else if (m.payload?.request === "outputs")
        send({ event: "sendToPropertyInspector", context: m.context,
               payload: { outputs: outputDevices() } });
      else if (m.payload?.request === "inserts")
        send({ event: "sendToPropertyInspector", context: m.context,
               payload: insertChoices() });
      else if (m.payload?.request === "profiles")
        send({ event: "sendToPropertyInspector", context: m.context,
               payload: { profiles: profileChoices() } });
      else if (m.payload?.request === "soundboard") {
        clipInspectors.add(m.context);
        const choices = clipChoices(daemonState?.mixer, daemonUp);
        lastClips = JSON.stringify(choices);
        send({ event: "sendToPropertyInspector", context: m.context, payload: { soundboard: choices } });
      }
      break;
    case "propertyInspectorDidDisappear":
      layoutInspectors.delete(m.context);
      clipInspectors.delete(m.context);
      break;
  }
};

// Saved profiles of the active device, for the PI's picker. A key made for
// one is looked up by name, so a profile saved again under the same name
// keeps its key.
//   profile|<name>   key: recall the profile; lit while it is the last recalled
function profileChoices() {
  return (daemonState?.profiles ?? []).map((name) => ({ target: `profile|${name}`, label: name }));
}
const isProfileTarget = (t) => !!t && t.startsWith("profile|");

// Physical output sinks the monitor mix can feed, for the PI's picker.
function outputDevices() {
  return (daemonState?.devices ?? [])
    .filter((d) => d.kind === 0 && !d.isOwn)
    .map((d) => ({ name: d.name, description: d.description }));
}

// ---------- state readers ----------
const dev = () => daemonState?.state ?? null;
const mixer = () => daemonState?.mixer ?? null;
const monitorMuted = () => mixer()?.monitorMuted === true;
const effectiveMixMuted = mix => mix?.kind === "monitor" && monitorMuted() ? true : mix?.muted ?? null;
const mixOf = (id) => mixer()?.mixes?.find((x) => x.id === id);
const chOf = (id) => mixer()?.channels?.find((x) => x.id === id);
// The device's highest gain (40 dB on the Wave:3); 80 from a daemon that does not say.
const gainMaxDb = () => {
  const max = daemonState?.capabilities?.gainMaxDb;
  return Number.isInteger(max) && max > 0 ? max : 80;
};
// The highest Line In gain the daemon writes (40 dB on the Wave XLR Pro); a
// daemon that does not say held it to the unit's gain range.
const lineInGainMaxDb = () => {
  const max = daemonState?.capabilities?.lineInGainMaxDb;
  return Number.isInteger(max) && max > 0 ? max : gainMaxDb();
};

// The connected interface has the target. A binding never changes this.
const supported = (target) => deviceTargetSupported(daemonState?.capabilities, target);

// A toggle target's current boolean, or null when unknown. Insert targets
// need the key's saved meta for id fallback, so they take the instance.
function toggleValue(target, inst) {
  if (!target) return null;
  if (isVoiceTarget(target)) return voiceTarget(mixer(), target)?.lit ?? null;
  // Lit while the microphone's EQ is on and holds the profile's bands.
  if (isEqProfileTarget(target)) return eqProfileTarget(daemonState, target)?.lit ?? null;
  if (isEqTarget(target)) {
    const eq = eqTarget(mixer(), target);
    if (!eq) return null;
    if (eq.field === "type") return false;
    if (eq.field !== "enabled") return null;
    const value = dialNow(target, eq.value);
    return !eq.band && !value && !eq.available ? null : value;
  }
  const channel = channelUnitTarget(target);
  if (channel) return channel.control === "gain" ? null : channelUnitControl(daemonState, channel.channel, channel.control)?.value ?? null;
  const output = outputKey(target);
  if (output) {
    if (!mixer()) return null;
    if (output.kind === "main" && output.device === "@monitor")
      return mixer().monitorOutputs?.length ? mixer().enforcedDefaultSink === "@monitor" : null;
    if (output.device && !controllableOutputs(mixer(), daemonState?.devices).some(d => d.name === output.device)) return null;
    if (output.kind === "main") return mixer().enforcedDefaultSink === output.device;
    if (output.kind !== "mute") return false;   // louder and quieter are momentary
    // The system default key stays momentary while the daemon cannot say
    // which sink it is, so the key still works.
    const name = outputDeviceName(output.device);
    return output.device ? outputMuted(name) : outputMuted(name) ?? false;
  }
  if (target.startsWith("insert|")) {
    const [, ch, id] = target.split("|");
    const ins = resolveInsert(ch, id, metaOf(inst, target));
    return ins ? !ins.bypass : null;        // ON = the plugin is in the path
  }
  if (target.startsWith("inschain|")) {
    const list = insertsOf(target.slice(9));
    return list.length ? list.some((i) => !i.bypass) : null;
  }
  if (target === "auxPort") return mixer()?.auxPortEnabled ?? null;
  if (isProfileTarget(target)) {
    // Unknown (grey) while the daemon is down or the profile was deleted.
    const name = target.slice(8);
    if (!daemonState?.profiles?.includes(name)) return null;
    return daemonState.activeProfile === name;
  }
  if (target === "softLowCut") {
    const hz = mixer()?.lowCutHz;
    return hz == null ? null : hz > 0;
  }
  // A missing LADSPA limiter is a disabled target, not an optimistic OFF
  // state. Pressing it would otherwise send a command that cannot be applied
  // and leave the deck face out of sync with the audible graph.
  if (target === "softClipGuard")
    return mixer()?.softClipGuardAvailable === true ? mixer()?.softClipGuard ?? false : null;
  if (target.startsWith("focus:")) {
    const channel = chOf(target.slice(6));
    return channel && !channel.hardware && !channel.captureSource ? false : null;
  }
  if (target.startsWith("monitor:")) {
    const outs = mixer()?.monitorOutputs;
    return outs ? outs.includes(target.slice(8)) : null;
  }
  // What feeds an output: dark = the default monitor, lit = another feed;
  // disabled while the output is not a monitor output at all.
  if (target.startsWith("feed:")) {
    const out = target.slice(5);
    const outs = mixer()?.monitorOutputs;
    if (!outs || !outs.includes(out)) return null;
    return feedOf(out) !== defaultMonitorMix(mixer());
  }
  if (target.startsWith("mixmute:")) return effectiveMixMuted(mixOf(target.slice(8)));
  if (target.startsWith("sendmute:")) {
    const [, ch, mix] = target.split(":");
    return chOf(ch)?.mutedIn?.includes(mix) ?? null;
  }
  // Lit while a member is heard; disabled once the group is deleted.
  if (target.startsWith("group:")) {
    const group = groupOf(target.slice(6));
    return group ? groupOpenMember(group) !== null : null;
  }
  // Ducking is the mixer's setup, present on any device, not a field of the state block.
  if (target === "ducking") return duckingEnabled(daemonState);
  if (Object.hasOwn(DEVICE_TOGGLES, target) && !supported(target)) return null;
  return dev()?.[target] ?? null;
}

// An exclusive group and the member that is unmuted in some mix, or null.
const groupOf = (id) => mixer()?.exclusiveGroups?.find((g) => g.id === id) ?? null;
const groupOpenMember = (group) => openGroupMember(mixer(), group);

// An output's feed as the daemon stores it: any mix id, or ids
// joined with '+' when the output hears them summed.
const feedOf = (sink) => monitorFeed(mixer(), sink);
const FEED_LETTER = { monitor: "A", monitor2: "B" };
const feedLetters = (feed) => feed.split("+").map((id) => FEED_LETTER[id] ?? mixName(mixer(), id)).join("+");
const feedLabel = (feed) => feed === "" ? "Silent" : feed.split("+").every(id => Object.hasOwn(FEED_LETTER, id) && mixName(mixer(), id) === `Monitor ${FEED_LETTER[id]}`)
  ? `Monitor ${feedLetters(feed)}` : feed.split("+").map(id => mixName(mixer(), id)).join(" + ");
// Keep A, B, A+B first, then include every other live mix.
const nextFeed = (feed) => {
  const mixes = mixer()?.mixes ?? [];
  const monitors = mixes.filter(m => Object.hasOwn(FEED_LETTER, m.id)).map(m => m.id);
  const choices = [...monitors, ...(monitors.length > 1 ? [monitors.join("+")] : []),
    ...mixes.filter(m => !Object.hasOwn(FEED_LETTER, m.id)).map(m => m.id)];
  return choices.length ? choices[(choices.indexOf(feed) + 1) % choices.length] : defaultMonitorMix(mixer());
};
// The first selected monitor output's mixes, for the dial's audible state.
const monitorDialMixes = () => {
  const first = mixer()?.monitorOutputs?.[0];
  return first ? feedOf(first).split("+") : [defaultMonitorMix(mixer())];
};

// ---------- outputs at the audio server ----------
// Output targets name a PipeWire sink; the empty name is the system default,
// which is a known sink only while the daemon enforces one by name.
const deviceOf = (name) => daemonState?.devices?.find((d) => d.name === name);
// What a sink is called: its description, else the description the daemon
// remembers for a selected output that is away, else null.
const deviceLabel = (name) => deviceOf(name)?.description || mixer()?.monitorOutputLabels?.[name] || null;
const outputDeviceName = (name) => {
  if (name) return name;
  const enforced = mixer()?.enforcedDefaultSink;
  return enforced && deviceOf(enforced) ? enforced : null;
};
// Pro jack selections retain their feed key; output controls name the raw sink.
const selectedMonitorOutputs = (name) => (mixer()?.monitorOutputs ?? [])
  .filter(output => typeof output === "string" && output.split("#", 1)[0] === name);
// Muted as toggleOutputMute sees it: a mix sink is its mix, a monitor output
// follows the global monitor pause, its feeds and the sink itself. Any other
// sink uses the server's own flag. null when unknown.
function outputMuted(name) {
  const mix = name?.startsWith("OpenXLR_mix_") ? mixOf(name.slice(12)) : null;
  if (mix) return effectiveMixMuted(mix);
  const own = deviceOf(name)?.muted ?? null;
  const selected = selectedMonitorOutputs(name);
  if (selected.length)
    return monitorMuted() || selected.every(output => feedOf(output).split("+").every(id => mixOf(id)?.muted === true)) || own === true;
  return own;
}
function outputVolume(name) {
  const mix = name?.startsWith("OpenXLR_mix_") ? mixOf(name.slice(12)) : null;
  return mix?.volume ?? deviceOf(name)?.volume ?? null;
}

function toggleLabel(target, inst) {
  if (!target) return "OpenXLR";
  if (isVoiceTarget(target)) return voiceLabel(voiceTarget(mixer(), target), target);
  if (isEqProfileTarget(target)) return eqProfileLabel(eqProfileTarget(daemonState, target), target);
  if (isEqTarget(target)) {
    const eq = eqTarget(mixer(), target);
    return eqLabel(eq, eq?.field === "type" ? dialNow(target, eq.value) : eq?.value);
  }
  const output = outputKey(target);
  if (output) {
    const name = output.device === "@monitor" ? "Follow monitor" : output.device
      ? deviceLabel(output.device) ?? output.device : "System default";
    return `${({up:"Louder",down:"Quieter",mute:"Toggle mute",main:"System out"})[output.kind]}\n${name}`;
  }
  if (target.startsWith("insert|")) {
    const [, ch, id] = target.split("|");
    const ins = resolveInsert(ch, id, metaOf(inst, target));
    const name = ins ? pluginShort(ins.label, ins.plugin) : (metaOf(inst, target)?.plugin ? pluginShort(null, metaOf(inst, target).plugin) : "Insert");
    return `${chainShort(ch)}\n${name}`;
  }
  if (target.startsWith("focus:")) return `Focus\n${channelName(mixer(), target.slice(6))}`;
  if (target.startsWith("inschain|")) return `${chainShort(target.slice(9))}\nInserts`;
  if (isProfileTarget(target)) return `Profile\n${target.slice(8)}`;
  if (target === "auxPort") return "Aux\nPort";
  if (target === "softLowCut") {
    const hz = mixer()?.lowCutHz ?? 0;
    return hz ? `Low Cut\n${hz} Hz` : "Low Cut\nOff";
  }
  if (target.startsWith("monitor:")) {
    const sink = target.slice(8);
    const name = deviceLabel(sink) ?? sink.split(".").pop();
    // The output follows the default monitor unless its feed was switched.
    return `${feedLabel(feedOf(sink))}\n${name}`;
  }
  if (target.startsWith("feed:")) {
    const sink = target.slice(5);
    const name = deviceLabel(sink) ?? sink.split(".").pop();
    return `${name}\n${feedLabel(feedOf(sink))}`;
  }
  if (target.startsWith("mixmute:")) return `${mixName(mixer(), target.slice(8))}\nMute`;
  if (target.startsWith("sendmute:")) {
    const [, ch, mix] = target.split(":");
    return `${channelName(mixer(), ch)}\n· ${mixShortName(mixer(), mix)}`;
  }
  if (target.startsWith("group:")) {
    const group = groupOf(target.slice(6));
    const open = group ? groupOpenMember(group) : null;
    return `${group?.name ?? "Group"}\n${open ? channelName(mixer(), open) : "None"}`;
  }
  return unitKeyLabel(daemonState, target) ?? DEVICE_TOGGLES[target] ?? target;
}

const isMuteLike = (t) =>
  MUTE_LIKE.has(t) || t?.startsWith("chmute:") || t?.startsWith("mixmute:") || t?.startsWith("sendmute:") || t?.startsWith("outputmute:");

// ---------- turning a dial ----------
// One turn fires many rotate events inside a single daemon round trip. Stepping
// from the value the last broadcast carried loses every tick that arrives before
// the echo (a quarter of them on a Stream Deck +, measured), and repainting only
// on the echo makes the strip jerk. The window's sliders have the same problem
// and solve it the same way (SliderSync): the turn leads locally. The local
// value is what the next tick steps from and what the strip shows, the daemon
// hears the first tick at once and a burst as one send per window, and its echo
// cannot pull the dial back while the turn is still warm.
const DIAL_STEP_SIZES = new Set([1, 3, 5, 10]);
const DIAL_SEND_MS = 80;    // trailing edge, as the window's sliders use
const DIAL_HOLD_MS = 800;   // how long the local value outranks the echo
const dialTurns = new Map();   // value key -> {value, at}
const dialSends = new Map();   // value key -> {send, timer, at}

function forgetEqTurn(target) {
  const entry = dialSends.get(target);
  if (entry?.timer) clearTimeout(entry.timer);
  dialSends.delete(target);
  dialTurns.delete(target);
}
function clearEqTurns(context = null) {
  for (const [target, entry] of dialSends)
    if (isEqTarget(target) && (context === null || entry.eqContext === context)) forgetEqTurn(target);
}
function pruneEqTurns() {
  for (const target of dialSends.keys())
    if (isEqTarget(target)) {
      const eq = eqTarget(mixer(), target);
      if (!eq || eq.eq.bands.length !== dialSends.get(target).eqBandCount) forgetEqTurn(target);
    }
}
const eqBinding = inst => inst.action === "com.emaspa.openxlr.dial"
  ? JSON.stringify(targetsOf(inst)) : inst.settings.target ?? "";

// EQ switches and dial fields share the dial's bounded trailing edge. The
// final turn cannot be replayed into a new session or a rebound action.
function turnEq(context, inst, target, value) {
  if (!daemonUp || !eqCommand(eqTarget(mixer(), target), value)) {
    send({event: "showAlert", context}); return;
  }
  const generation = connectionGeneration, binding = eqBinding(inst), bandCount = eqTarget(mixer(), target).eq.bands.length;
  dialTurn(target, value, () => {
    if (generation !== connectionGeneration || !daemonUp || instances.get(context) !== inst || eqBinding(inst) !== binding) return;
    const eq = eqTarget(mixer(), target);
    if (eq?.eq.bands.length !== bandCount) { forgetEqTurn(target); return; }
    const payload = eqCommand(eq, value);
    if (!payload) { forgetEqTurn(target); return; }
    if (!cmd(payload)) { forgetEqTurn(target); send({event: "showAlert", context}); }
  });
  const pending = dialSends.get(target);
  if (pending) { pending.eqContext = context; pending.eqBandCount = bandCount; }
  refresh(context);
}

// The value a dial steps from and shows: the local one while the turn is warm,
// the daemon's own once it has settled.
function dialNow(key, remote) {
  const turn = dialTurns.get(key);
  if (turn && Date.now() - turn.at < DIAL_HOLD_MS) return turn.value;
  dialTurns.delete(key);
  return remote;
}

// Take the turn's new value locally, and get it to the daemon: the first tick
// goes out at once, the rest of a burst collapse into one send per window.
function dialTurn(key, value, send) {
  dialTurns.set(key, { value, at: Date.now() });
  const pending = dialSends.get(key);
  if (pending?.timer) { pending.send = send; return; }   // the edge sends the latest
  if (pending && Date.now() - pending.at < DIAL_SEND_MS) {
    const timer = setTimeout(() => {
      const entry = dialSends.get(key);
      if (!entry) return;
      entry.timer = null;
      entry.at = Date.now();
      entry.send();
    }, DIAL_SEND_MS);
    timer.unref?.();
    dialSends.set(key, { send, timer, at: pending.at });
    return;
  }
  dialSends.set(key, { send, timer: null, at: Date.now() });
  send();
}

function clearDialTurns() {
  for (const entry of dialSends.values()) if (entry.timer) clearTimeout(entry.timer);
  dialSends.clear();
  dialTurns.clear();
}

// A recalled profile must follow earlier turns on this connection, and its
// values must replace the dial's warm local value as soon as state arrives.
function flushDialTurns() {
  const pending = [...dialSends.values()].filter(entry => entry.timer);
  clearDialTurns();
  for (const entry of pending) entry.send();
}

// A capture input's own gain on a gain dial: its source's volume in dB over
// the range the daemon reports. The source has no mute OpenXLR writes, so the
// dial shows the channel muted when every one of its sends is, as Line In's does.
function captureGainDial(id) {
  const gain = captureGainOf(daemonState, id);
  if (!gain) return null;
  const db = dialNow(`capgain:${id}`, gain.value);
  const ch = chOf(id);
  const levels = Object.keys(ch?.levels ?? {});
  const muted = levels.length > 0 && levels.every((m) => ch.mutedIn?.includes(m));
  return { label: unitDialLabel(daemonState, `chgain:${id}`), pct: Math.round(((db - gain.min) / (gain.max - gain.min)) * 100),
           text: muted ? "MUTED" : `${Math.round(db * 10) / 10} dB`, muted };
}

// A dial target as {label, pct, maxPct, text, muted}, or null when unknown.
function dialValue(target, inst) {
  if (!daemonState || !target) return null;
  if (isEqTarget(target)) {
    const eq = eqTarget(mixer(), target);
    return eq ? eqDial(eq, dialNow(target, eq.value)) : null;
  }
  if (target.startsWith("chgain:")) {
    const gain = channelUnitControl(daemonState, target.slice(7), "gain");
    if (!gain) return captureGainDial(target.slice(7));
    const db = dialNow(`unit:${gain.device}:${gain.control}`, gain.value);
    const muted = channelUnitControl(daemonState, target.slice(7), "mute")?.value === true;
    return { label: unitDialLabel(daemonState, target), pct: Math.round(db / gain.max * 100),
             text: muted ? "MUTED" : `${db} dB`, muted };
  }
  const pct = (v) => Math.round(v * 100);
  if (target.startsWith("insparam|")) {
    const [, ch, id, symbol] = target.split("|");
    const ins = resolveInsert(ch, id, metaOf(inst, target));
    const p = ins ? paramInfo(ins.plugin, symbol) : null;
    if (!ins || !p) return null;
    const v = dialNow(`insparam|${ch}|${ins.id}|${symbol}`, ins.params?.[symbol] ?? p.default);
    return { pin: pluginShort(ins.label, ins.plugin), scroll: p.name,
             pct: paramPct(p, v), text: ins.bypass ? "BYPASS" : paramText(p, v), muted: !!ins.bypass };
  }
  if (target.startsWith("send:")) {
    const [, chId, mix] = target.split(":");
    const ch = chOf(chId);
    if (!ch) return null;
    const shown = mix === "all" ? defaultMonitorMix(mixer()) : mix;   // the whole-channel dial reads the default monitor
    const v = dialNow(`lvl:${chId}:${shown}`, ch.levels?.[shown] ?? 0);
    const muted = mix === "all"
      ? Object.keys(ch.levels ?? {}).every((m) => ch.mutedIn?.includes(m))
      : ch.mutedIn?.includes(mix) ?? false;
    return { pin: channelName(mixer(), chId),
             scroll: mix === "all" ? "All mixes" : mixName(mixer(), mix),
             pct: pct(v), text: muted ? "MUTED" : `${pct(v)}%`, muted };
  }
  if (target.startsWith("chvol:")) {
    const chId = target.slice(6), ch = chOf(chId);
    if (typeof ch?.volume !== "number") return null;
    const v = dialNow(target, ch.volume);
    // The channel is silent when every one of its sends is muted.
    const levels = Object.keys(ch.levels ?? {});
    const muted = levels.length > 0 && levels.every((m) => ch.mutedIn?.includes(m));
    return { label: `${channelName(mixer(), chId)} volume`, pct: pct(v),
             text: muted ? "MUTED" : `${pct(v)}%`, muted };
  }
  if (target.startsWith("mixvol:")) {
    const id = target.slice(7), mix = mixOf(id);
    if (!mix) return null;
    const v = dialNow(`mixvol:${id}`, mix.volume);
    return { label: `${mix.name} mix`, pct: pct(v), maxPct: mix.kind === "monitor" ? 150 : 100,
             text: effectiveMixMuted(mix) ? "MUTED" : `${pct(v)}%`, muted: effectiveMixMuted(mix) };
  }
  if (target.startsWith("output:")) {
    const name = outputDeviceName(target.slice(7));
    const volume = outputVolume(name);
    if (volume == null) return null;
    const v = dialNow(target, volume);
    const muted = outputMuted(name) === true;
    const label = target.length > 7 ? deviceLabel(name) || name : "System default";
    return { label, pct: pct(v), maxPct: 150, text: muted ? "MUTED" : `${pct(v)}%`, muted };
  }
  const s = dev(), x = mixer();
  switch (target) {
    case "outputVolume": {
      const v = dialNow(target, x?.outputVolume ?? 0);
      const muted = monitorMuted() || monitorDialMixes().every((id) => mixOf(id)?.muted === true);
      return { label: "Monitor", pct: pct(v), maxPct: 150, text: muted ? "MUTED" : `${pct(v)}%`, muted };
    }
    case "gain": case "gain2": {
      if (!supported(target)) return null;
      const reported = target === "gain" ? s?.gainDb : s?.gain2Db;
      const muted = target === "gain" ? s?.mute : s?.mute2;
      if (reported == null) return null;
      const db = dialNow(target, reported);
      return { label: unitDialLabel(daemonState, target),
               pct: Math.round((db / gainMaxDb()) * 100), text: muted ? "MUTED" : `${db} dB`, muted };
    }
    case "hp": case "hp2": {
      if (!supported(target)) return null;
      const reported = target === "hp" ? s?.hpVolumeDb : s?.hp2VolumeDb;
      if (reported == null) return null;
      const db = dialNow(target, reported);
      const p = Math.round(((60 + db) / 60) * 100);
      const selector = target === "hp" ? "outHp1" : "outHp2";
      const jackOff = supported(selector) && s?.[selector] === false;
      return { label: target === "hp" ? "Phones 1" : "Phones 2", pct: p,
               text: jackOff ? "MUTED" : `${p}%`, muted: jackOff };
    }
    case "auxLevel": {
      if (!supported(target)) return null;
      if (s?.auxLevelDb == null) return null;
      const db = dialNow(target, s.auxLevelDb);
      const p = Math.round(((60 + db) / 60) * 100);
      return { label: "Aux In level", pct: p, text: `${p}%`, muted: false };
    }
    case "lineIn": {
      // The Pro's Line In gain; the input has no hardware mute, so the dial
      // shows the channel muted when every one of its sends is.
      if (!supported(target)) return null;
      if (s?.lineInGainDb == null) return null;
      const db = dialNow(target, s.lineInGainDb);
      const ch = chOf("line");
      const levels = Object.keys(ch?.levels ?? {});
      const muted = levels.length > 0 && levels.every((m) => ch.mutedIn?.includes(m));
      // A unit set above the cap elsewhere shows its own gain on a full bar.
      return { label: `${channelName(mixer(), "line")} gain`,
               pct: Math.min(100, Math.round((db / lineInGainMaxDb()) * 100)), text: muted ? "MUTED" : `${db} dB`, muted };
    }
    case "crossfade": {
      if (!supported(target)) return null;
      if (s?.crossfade == null) return null;
      const v = dialNow(target, s.crossfade);
      const text = v === 100 ? "centre" : v < 100 ? `mic +${100 - v}` : `pc +${v - 100}`;
      return { label: "Mic ↔ PC", pct: Math.round(v / 2), text, muted: false };
    }
  }
  return null;
}

// ---------- input handlers ----------
// A clip key plays its clip on the press; in hold mode the release ends it.
// The press goes beside the ordered queue and alerts only when refused. The
// release is sent as it is, never dropped behind an unanswered press.
// releaseClip ends every hold voice of the clip on this connection, so with
// two keys holding one clip it is sent when the last of them lets go.
const heldClips = new Map();   // context -> clip id held down in hold mode
const holdCount = new Map();   // clip id -> keys holding it
function onClipDown(context, inst, inMultiAction) {
  const key = clipKey(mixer(), inst.settings);
  if (!daemonUp || key.word !== null) { send({ event: "showAlert", context }); return; }
  releaseHeldClip(context);
  const press = clipPress(key, inst.settings, inMultiAction);
  // A press that was not sent holds nothing, so its key up releases nothing.
  if (keyCommands.enqueue(context, press.payload) && press.hold) {
    heldClips.set(context, key.id);
    holdCount.set(key.id, (holdCount.get(key.id) ?? 0) + 1);
  }
}
// A voice key switches its voice on the press; in hold mode the release
// switches the voice changer off again. The release goes as it is, never
// dropped behind an unanswered press.
const heldVoices = new Map();   // context -> channel held in hold mode
function onVoiceDown(context, inst, target, inMultiAction) {
  const entry = voiceTarget(mixer(), target);
  const press = daemonUp ? voicePress(entry, voiceMode(inst.settings), inMultiAction) : null;
  if (!press) { send({ event: "showAlert", context }); return; }
  releaseHeldVoice(context);
  // A press that was not sent holds nothing, so its key up releases nothing.
  if (keyCommands.enqueue(context, press.payload) && press.hold) heldVoices.set(context, entry.channel.id);
}
function releaseHeldVoice(context) {
  const channel = heldVoices.get(context);
  if (channel === undefined) return;
  heldVoices.delete(context);
  cmd(voiceRelease(channel));
}
function releaseHeldClip(context) {
  const id = heldClips.get(context);
  if (id === undefined) return;
  heldClips.delete(context);
  const left = (holdCount.get(id) ?? 1) - 1;
  if (left > 0) { holdCount.set(id, left); return; }
  holdCount.delete(id);
  cmd({ cmd: "releaseClip", clip: id });
}

function onKeyDown(context, inst, inMultiAction = false) {
  const t = inst.settings.target;
  const cur = toggleValue(t, inst);
  if (cur === null) { send({ event: "showAlert", context }); return; }
  if (isVoiceTarget(t)) { onVoiceDown(context, inst, t, inMultiAction); return; }
  if (isEqProfileTarget(t)) {
    // One loadEqProfile, after any EQ dial turn still waiting, so the
    // profile is what the microphone ends up with. A profile the
    // microphone's EQ cannot take is refused here.
    const payload = eqProfilePress(eqProfileTarget(daemonState, t));
    if (!payload) { send({ event: "showAlert", context }); return; }
    flushDialTurns();
    keyCommands.enqueue(context, payload);
    return;
  }
  if (isEqTarget(t)) {
    const eq = eqTarget(mixer(), t);
    const value = eq.field === "type" ? eqStep(eq, dialNow(t, eq.value), 1) : !cur;
    turnEq(context, inst, t, value);
    return;
  }
  const channel = channelUnitTarget(t);
  if (channel) {
    const control = channelUnitControl(daemonState, channel.channel, channel.control);
    if (control) cmd({ cmd: "setWaveControl", device: control.device, control: control.control, value: !cur });
    return;
  }
  const output = outputKey(t);
  if (output) {
    const payload = output.kind === "main" ? {cmd:"setMainOutput",device:output.device}
      : output.kind === "mute" ? {cmd:"toggleOutputMute",device:output.device}
      : {cmd:"adjustOutputVolume",device:output.device,value:output.kind === "up" ? .05 : -.05};
    keyCommands.enqueue(context, payload);
  }
  else if (t.startsWith("focus:")) keyCommands.enqueue(context, { cmd: "routeFocusedApp", channel: t.slice(6) });
  else if (t.startsWith("group:")) keyCommands.enqueue(context, { cmd: "cycleExclusiveGroup", group: t.slice(6) });
  else if (t.startsWith("insert|")) {
    const [, ch, id] = t.split("|");
    const ins = resolveInsert(ch, id, metaOf(inst, t));
    cmd({ cmd: "setInsertBypass", channel: ch, insertId: ins.id, value: cur });   // cur = active, so bypass it
  }
  else if (t.startsWith("inschain|")) {
    const ch = t.slice(9);
    for (const ins of insertsOf(ch))
      cmd({ cmd: "setInsertBypass", channel: ch, insertId: ins.id, value: cur });
  }
  else if (t === "auxPort") cmd({ cmd: "setAuxPortEnabled", value: !cur });
  else if (isProfileTarget(t)) {
    flushDialTurns();
    cmd({ cmd: "loadProfile", name: t.slice(8) });   // re-applies when already lit
  }
  else if (t === "softLowCut") {
    const hz = mixer()?.lowCutHz ?? 0;
    cmd({ cmd: "setLowCutHz", value: hz === 0 ? 80 : hz === 80 ? 120 : 0 });
  }
  else if (t === "softClipGuard") cmd({ cmd: "setSoftClipGuard", value: !cur });
  else if (t === "gainLocked") cmd({ cmd: "set", control: "gainLock", value: !cur });
  else if (t === "ducking") cmd(duckingToggle(daemonState));
  else if (t.startsWith("monitor:")) cmd({ cmd: "setMonitorOutputs", devices: [t.slice(8)] });
  else if (t.startsWith("feed:")) cmd({ cmd: "setMonitorFeed", device: t.slice(5), mix: nextFeed(feedOf(t.slice(5))) });
  else if (t.startsWith("mixmute:"))
    cmd({ cmd: "setMixMuted", mix: t.slice(8), value: !cur });
  else if (t.startsWith("sendmute:")) {
    const [, ch, mix] = t.split(":");
    cmd({ cmd: "setChannelMuted", channel: ch, mix, value: !cur });
  } else cmd({ cmd: "set", control: t, value: !cur });
}

function onDialRotate(context, inst, ticks) {
  if (!Number.isSafeInteger(ticks) || ticks === 0) return;
  const t = activeTarget(inst);
  if (!t || !daemonState) return;
  if (isEqTarget(t)) {
    const eq = eqTarget(mixer(), t);
    const value = eq ? eqStep(eq, dialNow(t, eq.value), ticks) : null;
    if (value === null) { if (ticks !== 0) send({event: "showAlert", context}); return; }
    turnEq(context, inst, t, value);
    return;
  }
  if (DEVICE_DIALS.has(t) && !supported(t)) {
    send({ event: "showAlert", context });
    return;
  }
  const step = DIAL_STEP_SIZES.has(inst.settings.stepSize) ? inst.settings.stepSize : null;
  const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
  // A level steps in whole percent and a dB control in tenths, so a long turn
  // never accumulates float dust in the value the daemon is told.
  const level = (v, lo, hi) => Math.round(clamp(v + ticks * (step ?? 1) / 100, lo, hi) * 100) / 100;
  const decibels = v => Math.round(clamp(v + ticks * (step ?? 0.6), -60, 0) * 10) / 10;
  let turned = true;
  if (t.startsWith("chgain:") && !channelUnitControl(daemonState, t.slice(7), "gain") && captureGainOf(daemonState, t.slice(7))) {
    // A capture input's own gain never passes the range the daemon reports.
    const channel = t.slice(7), gain = captureGainOf(daemonState, channel);
    const key = `capgain:${channel}`;
    const value = Math.round(clamp(dialNow(key, gain.value) + ticks * (step ?? 1), gain.min, gain.max) * 10) / 10;
    dialTurn(key, value, () => {
      if (captureGainOf(daemonState, channel)) cmd({ cmd: "setCaptureGain", channel, value });
    });
  } else if (t.startsWith("chgain:")) {
    const channel = t.slice(7), gain = channelUnitControl(daemonState, channel, "gain");
    if (!gain) { send({ event: "showAlert", context }); return; }
    const key = `unit:${gain.device}:${gain.control}`;
    const value = clamp(dialNow(key, gain.value) + ticks * (step ?? 1), 0, gain.max);
    dialTurn(key, value, () => {
      const current = channelUnitControl(daemonState, channel, "gain");
      if (current?.device === gain.device && current.control === gain.control)
        cmd({ cmd: "setWaveControl", device: gain.device, control: gain.control, value });
    });
  } else if (t.startsWith("insparam|")) {
    const [, ch, id, symbol] = t.split("|");
    const ins = resolveInsert(ch, id, metaOf(inst, t));
    const p = ins ? paramInfo(ins.plugin, symbol) : null;
    if (!ins || !p) return;
    const key = `insparam|${ch}|${ins.id}|${symbol}`;
    const value = paramStep(p, dialNow(key, ins.params?.[symbol] ?? p.default), ticks);
    dialTurn(key, value, () =>
      cmd({ cmd: "setInsertParam", channel: ch, insertId: ins.id, symbol, value }));
  } else if (t.startsWith("send:")) {
    const [, ch, mix] = t.split(":");
    const levels = chOf(ch)?.levels;
    if (!levels) return;
    for (const m of mix === "all" ? Object.keys(levels) : [mix]) {
      if (levels[m] == null) continue;
      const key = `lvl:${ch}:${m}`;
      const value = level(dialNow(key, levels[m]), 0, 1);
      dialTurn(key, value, () => cmd({ cmd: "setLevel", channel: ch, mix: m, value }));
    }
  } else if (t.startsWith("chvol:")) {
    const ch = t.slice(6), volume = chOf(ch)?.volume;
    if (typeof volume !== "number") return;
    const value = level(dialNow(t, volume), 0, 1);
    dialTurn(t, value, () => cmd({ cmd: "setChannelVolume", channel: ch, value }));
  } else if (t.startsWith("mixvol:")) {
    const mix = t.slice(7), state = mixOf(mix);
    if (state?.volume == null) return;
    const key = `mixvol:${mix}`;
    const value = level(dialNow(key, state.volume), 0, state.kind === "monitor" ? 1.5 : 1);
    dialTurn(key, value, () => cmd({ cmd: "setMixVolume", mix, value }));
  } else if (t === "outputVolume") {
    if (mixer()?.outputVolume == null) return;
    const value = level(dialNow(t, mixer().outputVolume), 0, 1.5);
    dialTurn(t, value, () => cmd({ cmd: "setOutputVolume", value }));
  } else if (t.startsWith("output:")) {
    const name = t.slice(7), current = outputVolume(outputDeviceName(name));
    if (current == null) return;
    const value = level(dialNow(t, current), 0, 1.5);
    dialTurn(t, value, () => cmd({ cmd: "setOutputDeviceVolume", device: name || null, value }));
  } else if (t === "gain" || t === "gain2") {
    const db = t === "gain" ? dev()?.gainDb : dev()?.gain2Db;
    if (db == null) return;
    const value = clamp(dialNow(t, db) + ticks * (step ?? 1), 0, gainMaxDb());
    dialTurn(t, value, () => cmd({ cmd: "set", control: t, value }));
  } else if (t === "hp" || t === "hp2") {
    const db = t === "hp" ? dev()?.hpVolumeDb : dev()?.hp2VolumeDb;
    if (db == null) return;
    const control = t === "hp" ? "hpVolumeDb" : "hp2VolumeDb";
    const value = decibels(dialNow(t, db));
    dialTurn(t, value, () => cmd({ cmd: "set", control, value }));
  } else if (t === "lineIn") {
    const db = dev()?.lineInGainDb;
    if (db == null) return;
    const value = clamp(dialNow(t, db) + ticks * (step ?? 1), 0, lineInGainMaxDb());
    dialTurn(t, value, () => cmd({ cmd: "set", control: "lineInGain", value }));
  } else if (t === "auxLevel") {
    const db = dev()?.auxLevelDb;
    if (db == null) return;
    const value = decibels(dialNow(t, db));
    dialTurn(t, value, () => cmd({ cmd: "set", control: "auxLevelDb", value }));
  } else if (t === "crossfade") {
    const v = dev()?.crossfade;
    if (v == null) return;
    const value = clamp(dialNow(t, v) + ticks * (step ?? 5), 0, 200);
    dialTurn(t, value, () => cmd({ cmd: "set", control: "crossfade", value }));
  } else turned = false;
  // The strip follows the finger, not the echo.
  if (turned) refresh(context);
}

function onDialPress(context, inst) {
  const t = activeTarget(inst);
  if (!t) return;
  if (isEqTarget(t)) {
    const eq = eqTarget(mixer(), t);
    const target = eq?.band ? `eqband:${eq.channel.id}:${eq.band}` : t;
    const toggle = eqTarget(mixer(), target);
    if (!toggle || toggle.field !== "enabled") { send({event: "showAlert", context}); return; }
    turnEq(context, inst, target, !dialNow(target, toggle.value));
    return;
  }
  if (DEVICE_DIALS.has(t) && !supported(t)) { send({ event: "showAlert", context }); return; }
  // A capture input's own gain has no mute OpenXLR writes on the source: a
  // press mutes the channel in every mix, as the Line In dial's does.
  const ownGain = t.startsWith("chgain:") && !channelUnitControl(daemonState, t.slice(7), "gain")
    && captureGainOf(daemonState, t.slice(7)) ? t.slice(7) : null;
  if (t.startsWith("chgain:") && !ownGain) {
    const mute = channelUnitControl(daemonState, t.slice(7), "mute");
    if (!mute) { send({ event: "showAlert", context }); return; }
    cmd({ cmd: "setWaveControl", device: mute.device, control: mute.control, value: !mute.value });
  } else if (t.startsWith("insparam|")) {
    // the press is the insert's bypass, like a dial's mute
    const [, ch, id] = t.split("|");
    const ins = resolveInsert(ch, id, metaOf(inst, t));
    if (ins) cmd({ cmd: "setInsertBypass", channel: ch, insertId: ins.id, value: !ins.bypass });
  } else if (t.startsWith("send:") || t.startsWith("chvol:") || t === "lineIn" || ownGain) {
    // A channel volume dial mutes the channel the way an all-mixes send dial
    // does, and so do the Line In gain dial and a capture input's own gain
    // dial, whose inputs have no mute OpenXLR writes.
    const [, ch, sendMix] = t === "lineIn" ? ["", "line", "all"] : ownGain ? ["", ownGain, "all"] : t.split(":");
    const mix = t.startsWith("chvol:") || t === "lineIn" || ownGain ? "all" : sendMix;
    const c = chOf(ch);
    if (!c) return;
    if (mix === "all") {
      const allMuted = Object.keys(c.levels ?? {}).every((m) => c.mutedIn?.includes(m));
      for (const m of Object.keys(c.levels ?? {}))
        cmd({ cmd: "setChannelMuted", channel: ch, mix: m, value: !allMuted });
    } else {
      const muted = c.mutedIn?.includes(mix);
      if (muted != null) cmd({ cmd: "setChannelMuted", channel: ch, mix, value: !muted });
    }
  } else if (t.startsWith("mixvol:")) {
    const mix = t.slice(7), muted = effectiveMixMuted(mixOf(mix));
    if (muted != null) cmd({ cmd: "setMixMuted", mix, value: !muted });
  } else if (t === "gain" || t === "gain2") {
    const control = t === "gain" ? "mute" : "mute2";
    const muted = dev()?.[control];
    if (muted != null) cmd({ cmd: "set", control, value: !muted });
  } else if (t === "outputVolume") {
    if (mixer()) keyCommands.enqueue(context, { cmd: "toggleMonitorMuted" });
  } else if (t.startsWith("output:")) {
    keyCommands.enqueue(context, { cmd: "toggleOutputMute", device: t.slice(7) || null });
  } else if (t === "hp" || t === "hp2") {
    // Only units with an output selector can use it as headphone mute.
    const control = t === "hp" ? "outHp1" : "outHp2";
    const on = dev()?.[control];
    if (supported(t) && supported(control) && typeof on === "boolean")
      cmd({ cmd: "set", control, value: !on });
  } else if (t === "crossfade") {
    cmd({ cmd: "set", control: "crossfade", value: 100 });   // back to centre
  }
}

// ---------- rendering ----------
// Visual language borrowed from Wave Link's deck plugin (all artwork is
// ours): a full-bleed colored frame that reads state at a glance (red =
// muted, light = engaged), an inner dark card, and a white glyph. Words
// The skin supplies the surface, lettering, indicator and meter colours.

// Centered glyphs in a 144x144 viewBox, coloured when they are drawn.
const GLYPHS = {
  mic: `<rect x="58" y="30" width="28" height="48" rx="14" fill="currentColor"/>
        <path d="M46 62 a26 26 0 0 0 52 0" stroke="currentColor" stroke-width="7" fill="none" stroke-linecap="round"/>
        <line x1="72" y1="90" x2="72" y2="104" stroke="currentColor" stroke-width="7" stroke-linecap="round"/>
        <line x1="56" y1="104" x2="88" y2="104" stroke="currentColor" stroke-width="7" stroke-linecap="round"/>`,
  speaker: `<path d="M42 58 h16 l20 -18 v64 l-20 -18 h-16 z" fill="currentColor"/>
        <path d="M88 56 a22 22 0 0 1 0 32 M96 46 a34 34 0 0 1 0 52"
              stroke="currentColor" stroke-width="6" fill="none" stroke-linecap="round"/>`,
  headphones: `<path d="M39 81 v-13 a33 33 0 0 1 66 0 v13" stroke="currentColor" stroke-width="6" fill="none" stroke-linecap="round"/>
        <rect x="31" y="72" width="21" height="34" rx="10" fill="currentColor"/>
        <rect x="92" y="72" width="21" height="34" rx="10" fill="currentColor"/>`,
  fader: `<g stroke="currentColor" stroke-width="6" stroke-linecap="round">
          <line x1="50" y1="40" x2="50" y2="104"/><line x1="72" y1="40" x2="72" y2="104"/>
          <line x1="94" y1="40" x2="94" y2="104"/></g>
        <g fill="currentColor"><rect x="41" y="76" width="18" height="12" rx="4"/>
          <rect x="63" y="52" width="18" height="12" rx="4"/>
          <rect x="85" y="66" width="18" height="12" rx="4"/></g>`,
  knob: `<circle cx="72" cy="72" r="34" stroke="currentColor" stroke-width="7" fill="none"/>
        <line x1="72" y1="72" x2="52" y2="50" stroke="currentColor" stroke-width="8" stroke-linecap="round"/>`,
  xfade: `<path d="M40 56 h50 m0 0 l-12 -10 m12 10 l-12 10" stroke="currentColor" stroke-width="7" fill="none" stroke-linecap="round" stroke-linejoin="round"/>
        <path d="M104 88 h-50 m0 0 l12 -10 m-12 10 l12 10" stroke="currentColor" stroke-width="7" fill="none" stroke-linecap="round" stroke-linejoin="round"/>`,
  jack: `<circle cx="72" cy="72" r="28" stroke="currentColor" stroke-width="7" fill="none"/>
        <circle cx="72" cy="72" r="9" fill="currentColor"/>`,
  // a play triangle over a short wave, for clip keys, and a stop square
  play: `<path d="M56 40 l40 24 l-40 24 z" fill="currentColor" stroke="currentColor" stroke-width="6" stroke-linejoin="round"/>
        <path d="M38 102 q8 -10 16 0 t16 0 t16 0 t16 0 t16 0" fill="none" stroke="currentColor" stroke-width="6" stroke-linecap="round"/>`,
  stop: `<rect x="46" y="46" width="52" height="52" rx="8" fill="currentColor"/>`,
  // an EQ curve with its handle over a baseline, for EQ profile keys
  eq: `<path d="M32 92 C50 92 54 50 72 50 C90 50 94 92 112 92" fill="none" stroke="currentColor" stroke-width="7" stroke-linecap="round"/>
        <line x1="32" y1="106" x2="112" y2="106" stroke="currentColor" stroke-width="5" stroke-linecap="round" opacity="0.6"/>
        <circle cx="72" cy="50" r="9" fill="currentColor"/>`,
  // a stack of scene cards, for profile keys
  scene: `<rect x="44" y="52" width="56" height="40" rx="6" fill="none" stroke="currentColor" stroke-width="6"/>
        <path d="M52 44 h52 a6 6 0 0 1 6 6 v34" fill="none" stroke="currentColor" stroke-width="6" stroke-linecap="round"/>
        <path d="M56 78 l10 -12 l9 8 l8 -14 l11 18" fill="none" stroke="currentColor" stroke-width="5" stroke-linejoin="round" stroke-linecap="round"/>`,
};

// Which glyph a toggle target wears; unlisted targets are word keys
// (frame + LED + title only).
function glyphFor(t) {
  if (!t) return null;
  if (isEqTarget(t)) return "knob";
  if (isEqProfileTarget(t)) return "eq";
  if (t === "mute" || t === "mute2") return "mic";
  if (t === "outHp1" || t === "outHp2" || t === "lowImpedance") return "headphones";
  if (t === "outLineOut") return "jack";
  if (t.startsWith("monitor:") || t.startsWith("feed:") || t.startsWith("mixmute:")) return "speaker";
  if (t.startsWith("sendmute:") || t.startsWith("group:")) return "fader";
  if (isProfileTarget(t)) return "scene";
  return null;
}


// Badge text drawn as seven-segment figures (like the LED displays on rack
// gear), so it renders identically on every machine instead of through
// whatever font the host's SVG rasterizer finds. Segments: a top, b top
// right, c bottom right, d bottom, e bottom left, f top left, g middle.
const SEGMENTS = {
  "0": "abcdef", "1": "bc", "2": "abged", "3": "abgcd", "4": "fgbc",
  "5": "afgcd", "6": "afgedc", "7": "abc", "8": "abcdefg", "9": "abcfgd",
  "O": "abcdef", "F": "afge",
};
function sevenSegText(text, x, y, h, color) {
  const w = h * 0.58, t = h * 0.20, gap = w * 0.40;   // digit box + stroke
  const seg = {
    a: [t * 0.7, 0, w - 1.4 * t, t], b: [w - t, t * 0.6, t, h / 2 - t],
    c: [w - t, h / 2 + t * 0.4, t, h / 2 - t], d: [t * 0.7, h - t, w - 1.4 * t, t],
    e: [0, h / 2 + t * 0.4, t, h / 2 - t], f: [0, t * 0.6, t, h / 2 - t],
    g: [t * 0.7, (h - t) / 2, w - 1.4 * t, t],
  };
  const total = text.length * w + (text.length - 1) * gap;
  const draw = (cx, names, fill, opacity) => {
    let o = `<g fill="${fill}" opacity="${opacity}">`;
    for (const sName of names) {
      const [sx, sy, sw, sh] = seg[sName];
      o += `<rect x="${(cx + sx).toFixed(1)}" y="${(y + sy).toFixed(1)}" width="${sw.toFixed(1)}" height="${sh.toFixed(1)}" rx="${(t / 2).toFixed(1)}"/>`;
    }
    return o + "</g>";
  };
  let out = "";
  let cx = x - total / 2;
  for (const ch of text) {
    const lit = SEGMENTS[ch.toUpperCase()] ?? "";
    out += draw(cx, "abcdefg", color, 0.14);   // ghost of unlit segments
    out += draw(cx, lit, color, 1);
    cx += w + gap;
  }
  return out;
}

function keySvg(on, muteLike, known, glyphName, badge, label, offColor = null, appearance = {}, progress = null) {
  // The skin colours the owned faceplate, cap and glyph. An insert's bypass
  // uses the alert colour, and a mute keeps its visible slash and status lamp.
  // A channel's or mix's own icon and colour from the layout editor replace
  // the glyph and tint the label and border while the daemon is reachable.
  const accent = !known ? null : on ? (muteLike ? skinPalette.colours["Ox.Led.Alert"] : skinPalette.colours["Ox.Led.On"]) : offColor;
  const ink = !known ? skinPalette.colours["Ox.Text.Muted"] : accent ?? skinPalette.colours["Ox.Text.Primary"];
  const lines = label ? label.split("\n").slice(0, 2) : [];

  // Button cap (glyph keys) or LED display window (badge keys) or lamp only.
  const capY = lines.length ? 52 : 66;
  const tint = known ? appearance.colour ?? null : null;
  let face;
  if (appearance.icon) {
    face = `<text x="72" y="${capY + 15}" text-anchor="middle" font-family="sans-serif" font-size="48" fill="${tint ?? ink}">${escXml(appearance.icon)}</text>`;
  } else if (appearance.path) {
    // A built-in icon sits in the button cap the target's own glyph would use.
    face = `
      <circle cx="72" cy="${capY}" r="38" fill="none" stroke="#000" stroke-opacity="0.4" stroke-width="6"/>
      <circle cx="72" cy="${capY}" r="34" fill="${skinPalette.colours["Ox.Card.Background"]}" stroke="${skinPalette.colours["Ox.Text.Muted"]}" stroke-width="4"/>
      ${accent ? `<circle cx="72" cy="${capY}" r="37" fill="none" stroke="${accent}" stroke-width="6" opacity="0.6" filter="url(#bloom)"/>` : ""}
      <g transform="translate(72 ${capY}) scale(2.1) translate(-12 -12)"><path d="${appearance.path}" fill="${tint ?? ink}"/></g>`;
  } else if (glyphName) {
    const glyph = GLYPHS[glyphName].replaceAll("currentColor", ink);
    face = `
      <circle cx="72" cy="${capY}" r="38" fill="none" stroke="#000" stroke-opacity="0.4" stroke-width="6"/>
      <circle cx="72" cy="${capY}" r="34" fill="${skinPalette.colours["Ox.Card.Background"]}" stroke="${skinPalette.colours["Ox.Text.Muted"]}" stroke-width="4"/>
      ${accent ? `<circle cx="72" cy="${capY}" r="37" fill="none" stroke="${accent}" stroke-width="6" opacity="0.6" filter="url(#bloom)"/>` : ""}
      <g transform="translate(72 ${capY}) scale(0.62) translate(-72 -72)">${glyph}</g>`;
  } else if (badge) {
    face = `
      <rect x="24" y="${capY - 30}" width="96" height="60" rx="8" fill="${skinPalette.colours["Ox.Meter.Track"]}" stroke="#000" stroke-opacity="0.5" stroke-width="5"/>
      ${accent ? `<g filter="url(#bloom)" opacity="0.65">${sevenSegText(badge, 72, capY - 19, 38, accent)}</g>` : ""}
      ${sevenSegText(badge, 72, capY - 19, 38, known ? (accent ?? skinPalette.colours["Ox.Text.Muted"]) : skinPalette.colours["Ox.Led.Off"])}`;
  } else {
    const lamp = !known ? skinPalette.colours["Ox.Led.Off"] : accent ?? skinPalette.colours["Ox.Text.Muted"];
    face = `
      <circle cx="72" cy="${capY}" r="38" fill="none" stroke="#000" stroke-opacity="0.4" stroke-width="6"/>
      <circle cx="72" cy="${capY}" r="34" fill="${skinPalette.colours["Ox.Card.Background"]}" stroke="${skinPalette.colours["Ox.Text.Muted"]}" stroke-width="4"/>
      ${accent ? `<circle cx="72" cy="${capY}" r="15" fill="${lamp}" filter="url(#bloom)" opacity="0.8"/>` : ""}
      <circle cx="72" cy="${capY}" r="12" fill="${lamp}" stroke="${skinPalette.colours["Ox.Card.Background"]}" stroke-width="4"/>`;
  }

  const slash = muteLike && on
    ? `<line x1="${72 - 26}" y1="${capY + 26}" x2="${72 + 26}" y2="${capY - 26}" stroke="${skinPalette.colours["Ox.Led.Alert"]}" stroke-width="8" stroke-linecap="round"/>`
    : "";

  // Status LED lamp in the top-right corner, like a channel strip indicator.
  const lampDot = glyphName
    ? `<circle cx="120" cy="24" r="8" fill="${!known ? skinPalette.colours["Ox.Led.Off"] : accent ?? skinPalette.colours["Ox.Text.Muted"]}" stroke="${skinPalette.colours["Ox.Card.Background"]}" stroke-width="3"/>` +
      (accent ? `<circle cx="120" cy="24" r="11" fill="${accent}" opacity="0.5" filter="url(#soft)"/>` : "")
    : "";

  // A playing clip's progress, a thin bar along the bottom; a one-line label
  // moves up to clear it.
  const bar = progress === null ? "" :
    `<rect x="22" y="124" width="100" height="6" rx="3" fill="${skinPalette.colours["Ox.Meter.Track"]}"/>` +
    (progress > 0 ? `<rect x="22" y="124" width="${Math.round(Math.min(1, progress) * 100)}" height="6" rx="3" fill="${skinPalette.colours["Ox.Meter.Fill"]}"/>` : "");

  const labelSvg = lines.map((line, i) => {
    const size = line.length > 11 ? 19 : line.length > 8 ? 22 : 26;
    const y = lines.length === 1 ? (progress === null ? 126 : 114) : 106 + i * 24;
    return `<text x="72" y="${y}" text-anchor="middle" fill="${tint ?? skinPalette.colours["Ox.Text.Primary"]}" ` +
      `stroke="#000" stroke-width="4" paint-order="stroke" stroke-linejoin="round" ` +
      `font-family="Inter, Noto Sans, DejaVu Sans, sans-serif" font-size="${size}" font-weight="700">` +
      escXml(line) + `</text>`;
  }).join("");

  return "data:image/svg+xml;base64," + Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="288" height="288" viewBox="0 0 144 144">
      <defs>
        <linearGradient id="side" x1="138" y1="72" x2="6" y2="72" gradientUnits="userSpaceOnUse">
          <stop stop-opacity="0"/><stop offset="1" stop-opacity="0.2"/>
        </linearGradient>
        <filter id="soft" x="-40%" y="-40%" width="180%" height="180%">
          <feGaussianBlur stdDeviation="3"/>
        </filter>
        <filter id="bloom" x="-40%" y="-40%" width="180%" height="180%">
          <feGaussianBlur stdDeviation="4.5"/>
        </filter>
      </defs>
      <rect x="6" y="6" width="132" height="132" rx="14" fill="${skinPalette.colours["Ox.Card.Background"]}"/>
      <rect x="6" y="6" width="132" height="132" rx="14" fill="url(#side)"/>
      <rect x="9" y="9" width="126" height="126" rx="12" fill="none" stroke="${tint ?? skinPalette.colours["Ox.Text.Muted"]}" stroke-width="4"/>
      ${face}${slash}${lampDot}${labelSvg}${bar}
    </svg>`).toString("base64");
}

// 24x24 icons for the dial layout's corner slot.
function dialIcon(t) {
  const appearance = targetAppearance(mixer(), t);
  const ink = appearance.colour ?? skinPalette.colours["Ox.Text.Primary"];
  const inner = (name) => GLYPHS[name]
    ? `<g transform="scale(0.1667)">${GLYPHS[name].replaceAll("currentColor", ink)}</g>` : "";
  let name = "knob";
  if (t?.startsWith("send:") || t?.startsWith("chvol:")) name = "fader";
  else if (t?.startsWith("mixvol:")) name = "speaker";
  else if (t === "outputVolume" || t?.startsWith("output:")) name = "speaker";
  else if (t === "gain" || t === "gain2" || t?.startsWith("chgain:")) name = "mic";
  else if (t === "hp" || t === "hp2") name = "headphones";
  else if (t === "crossfade") name = "xfade";
  return "data:image/svg+xml;base64," + Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24">${appearance.icon
      ? `<text x="12" y="19" text-anchor="middle" font-family="sans-serif" font-size="21" fill="${ink}">${escXml(appearance.icon)}</text>`
      : appearance.path ? `<path d="${appearance.path}" fill="${ink}"/>` : inner(name)}</svg>`
  ).toString("base64");
}

// The rotating needle over the half-knob, Wave Link style: a tick rotated
// around a center below the visible strip. The full range sweeps -50°..+50°.
function needleSvg(pct, maxPct = 100) {
  const angle = (Math.max(0, Math.min(maxPct, pct)) / maxPct) * 100 - 50;
  return "data:image/svg+xml;base64," + Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="130" height="52" viewBox="0 0 130 52">
      <g transform="rotate(${angle}, 65, 61)">
        <rect x="63.5" y="30" width="3" height="16" rx="1.5" fill="${skinPalette.colours["Ox.Text.Primary"]}"/>
      </g>
    </svg>`).toString("base64");
}

// The meter key feeding a dial target's level bar.
function meterKeyFor(t) {
  if (!t) return null;
  if (isEqTarget(t)) {
    const eq = eqTarget(mixer(), t);
    return eq ? `ch:${eq.channel.id}` : null;
  }
  if (t.startsWith("send:") || t.startsWith("chvol:")) return `ch:${t.split(":")[1]}`;
  if (t.startsWith("mixvol:")) return `mix:${t.slice(7)}`;
  // A gain shows the channel it is heard on, wherever the layout put it.
  if (t === "gain" || t === "gain2" || t.startsWith("chgain:")) return unitMeterKey(mixer(), t);
  if (t === "auxLevel") return "ch:aux";
  if (t === "lineIn") return "ch:line";
  if (t.startsWith("insparam|")) {
    const ch = t.split("|")[1];
    return ch.startsWith("mix:") ? ch : `ch:${ch}`;
  }
  // An output shows the mix it hears; a sink the mixer does not feed has no meter.
  if (t.startsWith("output:")) {
    const name = outputDeviceName(t.slice(7));
    if (name?.startsWith("OpenXLR_mix_")) return `mix:${name.slice(12)}`;
    const selected = selectedMonitorOutputs(name)[0];
    return selected ? `mix:${feedOf(selected).split("+")[0]}` : null;
  }
  return "mix:monitor";   // outputVolume, hp, hp2, crossfade
}

// The colours sit along the bar, as in the window: green up to the warning
// level, yellow up to the hot level, red above, blended over a few pixels at
// each edge. Only the bar's length follows the level, so the whole strip never
// switches colour from one tick to the next.
const METER_W = 130, METER_BLEND = 0.04;
function meterSvg(level) {
  const w = Math.round(Math.max(0, Math.min(1, level)) * METER_W);
  const c = skinPalette.colours;
  const warn = Math.max(0, Math.min(1, c["Ox.Meter.WarningLevel"]));
  const hot = Math.max(warn, Math.min(1, c["Ox.Meter.HotLevel"]));
  const stop = (at, colour) => `<stop offset="${Math.max(0, Math.min(1, at)).toFixed(3)}" stop-color="${colour}"/>`;
  const stops = [stop(0, c["Ox.Meter.Fill"]), stop(warn - METER_BLEND, c["Ox.Meter.Fill"]),
    stop(warn + METER_BLEND, c["Ox.Meter.Warning"]), stop(Math.max(warn + METER_BLEND, hot - METER_BLEND), c["Ox.Meter.Warning"]),
    stop(hot + METER_BLEND, c["Ox.Meter.Hot"]), stop(1, c["Ox.Meter.Hot"])].join("");
  return "data:image/svg+xml;base64," + Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="${METER_W}" height="6" viewBox="0 0 ${METER_W} 6">
      <defs><linearGradient id="level" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="${METER_W}" y2="0">${stops}</linearGradient></defs>
      <rect width="${METER_W}" height="6" rx="3" fill="${c["Ox.Meter.Track"]}"/>
      ${w > 0 ? `<rect width="${w}" height="6" rx="3" fill="url(#level)"/>` : ""}
    </svg>`).toString("base64");
}

// Meters tick at 15 Hz; only redraw a dial's bar when its value moved
// visibly, so the strip is not re-rendered for noise.
const lastMeter = new Map();   // context -> rounded width last drawn
function refreshEqMeter(context) {
  const inst = instances.get(context);
  if (inst && isEqTarget(activeTarget(inst))) refreshMeters(context);
}
function refreshMeters(contextOnly) {
  if (!meterLevels) return;
  for (const [context, inst] of instances) {
    if (contextOnly !== undefined && context !== contextOnly) continue;
    // A clip key redraws when its bar moves a step, not on every frame.
    if (inst.action === CLIP_ACTION) {
      if (lastMeter.get(context) !== clipBucket(context, clipProgress(meterLevels, clipIdOf(inst.settings.target)))) refresh(context);
      continue;
    }
    if (inst.action !== "com.emaspa.openxlr.dial") continue;
    const target = activeTarget(inst);
    const key = meterKeyFor(target);
    // A gain whose input is not in the mixer has nothing to meter: its bar
    // empties rather than keeping the level of a channel it no longer feeds.
    const silent = !key && (target === "gain" || target === "gain2" || target?.startsWith("chgain:"))
      || isEqTarget(target) && (!key || !(key in meterLevels));
    if (!silent && (!key || !(key in meterLevels))) continue;
    const lr = silent ? [0, 0] : meterLevels[key];
    const level = Math.max(lr[0] ?? 0, lr[1] ?? 0);
    const bucket = Math.round(level * 65);
    if (lastMeter.get(context) === bucket) continue;
    lastMeter.set(context, bucket);
    send({ event: "setFeedback", context, payload: { meter: meterSvg(level) } });
  }
}

// What a clip key's face shows of its progress: the bar's step, or under a
// title typed by the user, which hides the bar, only whether the clip plays.
const clipBucket = (context, progress) => !progress ? -1 : emptyTitle.has(context) ? progressBucket(progress) : 0;

// The title is rendered as our own pixmap so the scroll is pixel-exact
// across the full strip width, the way Wave Link's plugin uses it. Text
// width is made deterministic with SVG textLength (approximated from a
// per-character average, then enforced by the renderer). A send dial
// pins the channel name and scrolls the mix name in the space that
// remains; other long titles scroll whole.
const TITLE_W = 154, TITLE_H = 24, CHAR_W = 8.1, GAP_PX = 20, STEP_PX = 7;
const escXml = (t) => t.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
const textW = (t) => Math.round(t.length * CHAR_W);

function titleSvg(pinText, scroll, offsetPx) {
  const attrs = `y="17" font-family="sans-serif" font-size="14.5" font-weight="700" fill="${skinPalette.colours["Ox.Text.Primary"]}"`;
  const pinW = textW(pinText);
  const scrollW = textW(scroll);
  const avail = TITLE_W - pinW;
  const pinPart = pinText === "" ? "" :
    `<text x="0" ${attrs} textLength="${pinW - 4}" lengthAdjust="spacingAndGlyphs">${escXml(pinText)}</text>`;
  let body;
  if (scrollW <= avail) {
    body = `<text x="${pinW}" ${attrs}>${escXml(scroll)}</text>`;
  } else {
    const total = scrollW + GAP_PX;
    const o = offsetPx % total;
    const t = (x) =>
      `<text x="${x}" ${attrs} textLength="${scrollW}" lengthAdjust="spacingAndGlyphs">${escXml(scroll)}</text>`;
    body = `<svg x="${pinW}" y="0" width="${avail}" height="${TITLE_H}">${t(-o)}${t(-o + total)}</svg>`;
  }
  return "data:image/svg+xml;base64," + Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="${TITLE_W}" height="${TITLE_H}" viewBox="0 0 ${TITLE_W} ${TITLE_H}">${pinPart}${body}</svg>`
  ).toString("base64");
}

const marquee = new Map();   // context -> {pin, scroll, offset, hold, total}
function marqueeTitle(context, pin, scroll) {
  let pinText = pin === "" ? "" : `${pin} · `;
  // with no room left beside the pin, scroll the whole thing instead
  if (pinText !== "" && TITLE_W - textW(pinText) < 40) { scroll = pinText + scroll; pinText = ""; }
  if (textW(scroll) <= TITLE_W - textW(pinText)) { marquee.delete(context); return titleSvg(pinText, scroll, 0); }
  let m = marquee.get(context);
  if (!m || m.pinText !== pinText || m.scroll !== scroll)
    { m = { pinText, scroll, offset: 0, hold: 3, total: textW(scroll) + GAP_PX }; marquee.set(context, m); }
  return titleSvg(pinText, m.scroll, m.offset);
}
setInterval(() => {
  for (const [context, m] of marquee) {
    if (!instances.has(context)) { marquee.delete(context); continue; }
    if (m.hold > 0) { m.hold--; continue; }
    m.offset += STEP_PX;
    if (m.offset >= m.total) { m.offset = 0; m.hold = 3; }   // pause at each wrap
    send({ event: "setFeedback", context,
           payload: { title: titleSvg(m.pinText, m.scroll, m.offset) } });
  }
}, 350);

function refresh(context) {
  const inst = instances.get(context);
  if (!inst) return;
  const t = inst.action === "com.emaspa.openxlr.dial"
    ? activeTarget(inst) : inst.settings.target;
  if (inst.action === "com.emaspa.openxlr.toggle") {
    const v = toggleValue(t, inst);
    const badge = t === "softLowCut" ? (mixer()?.lowCutHz ? String(mixer().lowCutHz) : "OFF") : "";
    const label = emptyTitle.has(context)
      ? (!daemonUp ? daemonNotice ?? "offline"
        : isInsertTarget(t) || isProfileTarget(t) || isEqProfileTarget(t) ? toggleLabel(t, inst) : defaultTitle(t)) : "";
    // The user can pick a glyph per key (a monitor output may be headphones
    // rather than speakers); "auto" or unset keeps the target's default.
    const iconChoice = inst.settings.icon;
    const hasIcon = typeof iconChoice === "string" && Object.hasOwn(GLYPHS, iconChoice);
    const glyphName = hasIcon ? iconChoice : glyphFor(t);
    const offColor = isInsertTarget(t) ? skinPalette.colours["Ox.Led.Alert"] : null;   // bypassed = red, as in the UI
    // A glyph picked on the key itself wins over the channel's icon. An EQ
    // profile key wears the EQ glyph in its microphone's colour.
    const appearance = { ...targetAppearance(mixer(), t), ...(hasIcon || isEqProfileTarget(t) ? { icon: "", path: null } : {}) };
    send({ event: "setImage", context,
           payload: { image: keySvg(v === true, isMuteLike(t), v !== null && daemonUp, glyphName, badge, label, offColor, appearance) } });
  } else if (inst.action === CLIP_ACTION) {
    const key = clipKey(mixer(), inst.settings);
    const word = !daemonUp ? daemonNotice ?? "offline" : key.word;
    const known = word === null;
    const progress = clipProgress(meterLevels, key.id);
    lastMeter.set(context, clipBucket(context, progress));
    const own = emptyTitle.has(context);
    const label = own ? (word === null ? key.label : `${key.label}\n${word}`) : "";
    // Lit while the clip plays; the meters say so before the state does.
    const playing = known && (key.playing || !key.stop && progress !== null);
    send({ event: "setImage", context,
           payload: { image: keySvg(playing, false, known, key.stop ? "stop" : "play", "", label, null,
             key.stop ? {} : clipAppearance(mixer(), key.clip),
             // A title typed by the user is drawn over the key, where the bar would be.
             own && known && !key.stop && progress ? progress.position : null) } });
  } else if (inst.action === "com.emaspa.openxlr.dial") {
    const d = dialValue(t, inst);
    const isDb = t === "gain" || t === "gain2" || t?.startsWith("chgain:");
    send({ event: "setFeedback", context, payload: d
      ? { title: marqueeTitle(context, d.pin ?? "", d.scroll ?? d.label),
          value: isDb && !d.muted ? d.text.replace(" dB", "") : d.text,
          unit: { enabled: isDb && !d.muted },
          icon: dialIcon(t),
          needle: needleSvg(d.pct, d.maxPct),
          muteOverlay: { enabled: d.muted } }
      : { title: marqueeTitle(context, "", "OpenXLR"), value: daemonUp ? "set up" : daemonNotice ?? "offline",
          unit: { enabled: false }, icon: dialIcon(null),
          needle: needleSvg(0), muteOverlay: { enabled: false } } });
  }
}

function refreshAll() { for (const context of instances.keys()) refresh(context); }

const skinPalette = new SkinPalette(() => {
  lastMeter.clear();
  refreshAll();
  refreshMeters();
});
connectDaemon();
