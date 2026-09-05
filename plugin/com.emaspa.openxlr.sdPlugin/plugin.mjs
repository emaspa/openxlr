// OpenXLR plugin for OpenDeck (OpenAction / Stream Deck SDK compatible).
// A thin bridge: one WebSocket to the OpenDeck host, one to the OpenXLR
// daemon. The daemon owns all state and broadcasts every change, so keys
// and dials stay in sync with the UI (and with the hardware) for free.

import process from "node:process";
import { channelName, layoutChoices, mixName, mixShortName } from "./layout-choices.mjs";

// ---------- launch arguments ----------
const arg = (name) => {
  const i = process.argv.indexOf(name);
  return i >= 0 ? process.argv[i + 1] : undefined;
};
const port = arg("-port");
const pluginUUID = arg("-pluginUUID");
const registerEvent = arg("-registerEvent");

// Toggle targets on the device state block, with short key labels.
const DEVICE_TOGGLES = {
  mute: "XLR 1\nMute", mute2: "XLR 2\nMute",
  phantom: "XLR 1\n48V", phantom2: "XLR 2\n48V",
  lowCut: "XLR 1\nLow Cut", lowCut2: "XLR 2\nLow Cut",
  expander: "XLR 1\nExpander", expander2: "XLR 2\nExpander",
  voiceTune: "XLR 1\nVoice Tune", voiceTune2: "XLR 2\nVoice Tune",
  clipGuard: "XLR 1\nClipGuard", clipGuard2: "XLR 2\nClipGuard",
  compressor: "XLR 1\nComp", compressor2: "XLR 2\nComp",
  lowImpedance: "Low Z", auxLevelLock: "Aux In\nLock",
  outHp1: "HP 1\nOut", outHp2: "HP 2\nOut", outLineOut: "Line\nOut",
  gainLocked: "Gain\nLock",
  softClipGuard: "Clip\nGuard",
};
// Targets whose ON state means "muted" (shown red instead of lit green).
const MUTE_LIKE = new Set(["mute", "mute2"]);

// ---------- daemon connection ----------
let daemon = null;
let daemonState = null;   // last full {"type":"state"} message
let daemonUp = false;
let meterLevels = null;   // last {"type":"meters"} levels, keyed ch:/mix:
let catalog = new Map();  // LV2 plugin URI -> PluginInfo (names and ranges of the params)
let reconnectTimer = null;
let reconnectDelayMs = 500;
let connectionGeneration = 0;

function scheduleDaemonReconnect(generation) {
  if (generation !== connectionGeneration || reconnectTimer) return;
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    connectDaemon();
  }, reconnectDelayMs);
  reconnectDelayMs = Math.min(reconnectDelayMs * 2, 10000);
}

function connectDaemon() {
  const socket = new WebSocket("ws://127.0.0.1:37890/ws");
  const generation = ++connectionGeneration;
  daemon = socket;
  socket.onopen = () => {
    if (daemon !== socket) return;
    daemonUp = true;
    reconnectDelayMs = 500;
    cmd({ cmd: "listPlugins" });
    refreshAll();
  };
  socket.onmessage = (e) => {
    if (daemon !== socket) return;
    let m;
    try { m = JSON.parse(e.data); } catch { return; }
    if (m.type === "state") { daemonState = m; refreshAll(); }
    else if (m.type === "meters") { meterLevels = m.levels; refreshMeters(); }
    else if (m.type === "plugins") {
      catalog = new Map((m.plugins ?? []).map((p) => [p.plugin, p]));
      refreshAll();
    }
    else if (m.type === "error") console.error("OpenXLR daemon:", m.message);
  };
  socket.onclose = () => {
    if (daemon !== socket) return;
    daemonUp = false; daemonState = null; refreshAll();
    scheduleDaemonReconnect(generation);
  };
  socket.onerror = () => { /* onclose follows */ };
}
const cmd = (o) => {
  if (!daemonUp || !daemon || daemon.readyState !== WebSocket.OPEN) return false;
  try {
    daemon.send(JSON.stringify(o));
    return true;
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
  return title === defaultTitle(t);
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
}

host.onmessage = (e) => {
  let m;
  try { m = JSON.parse(e.data); } catch { return; }
  const inst = instances.get(m.context);
  switch (m.event) {
    case "willAppear":
      instances.set(m.context, {
        action: m.action,
        settings: m.payload?.settings ?? {},
        controller: m.payload?.controller ?? "Keypad",
      });
      refresh(m.context);
      break;
    case "willDisappear":
      instances.delete(m.context);
      emptyTitle.delete(m.context);
      break;
    case "didReceiveSettings":
      if (inst) { inst.settings = m.payload?.settings ?? {}; refresh(m.context); }
      break;
    case "keyDown":
      if (inst) onKeyDown(m.context, inst);
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
      if (m.payload?.request === "outputs")
        send({ event: "sendToPropertyInspector", context: m.context,
               payload: { outputs: outputDevices() } });
      else if (m.payload?.request === "inserts")
        send({ event: "sendToPropertyInspector", context: m.context,
               payload: insertChoices() });
      else if (m.payload?.request === "profiles")
        send({ event: "sendToPropertyInspector", context: m.context,
               payload: { profiles: profileChoices() } });
      else if (m.payload?.request === "layout")
        send({ event: "sendToPropertyInspector", context: m.context,
               payload: layoutChoices(mixer()) });
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
const mixOf = (id) => mixer()?.mixes?.find((x) => x.id === id);
const chOf = (id) => mixer()?.channels?.find((x) => x.id === id);

function deviceTargetSupported(target) {
  const c = daemonState?.capabilities;
  if (!c) return false;
  const secondXlr = new Set([
    "gain2", "mute2", "phantom2", "lowCut2", "expander2",
    "voiceTune2", "clipGuard2", "compressor2",
  ]).has(target);
  if (secondXlr && (c.xlrInputs ?? 1) < 2) return false;
  if (target === "hp2" && (c.hpOutputs ?? 1) < 2) return false;
  const capability = {
    gain: "gain", gain2: "gain", mute: "mute", mute2: "mute",
    phantom: "phantom", phantom2: "phantom", lowCut: "lowCut", lowCut2: "lowCut",
    expander: "expander", expander2: "expander",
    voiceTune: "voiceTune", voiceTune2: "voiceTune",
    clipGuard: "clipGuard", clipGuard2: "clipGuard",
    compressor: "compressor", compressor2: "compressor",
    lowImpedance: "lowImpedance", hp: "hpVolume", hp2: "hpVolume",
    crossfade: "crossfade",
    outHp1: "outputRouting", outHp2: "outputRouting",
    outLineOut: "outputRouting", auxLevel: "auxInput", auxLevelLock: "auxInput",
  }[target];
  return capability ? c[capability] === true : target === "gainLocked";
}

// A toggle target's current boolean, or null when unknown. Insert targets
// need the key's saved meta for id fallback, so they take the instance.
function toggleValue(target, inst) {
  if (!target) return null;
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
  if (target.startsWith("monitor:")) {
    const outs = mixer()?.monitorOutputs;
    return outs ? outs.includes(target.slice(8)) : null;
  }
  if (target.startsWith("mixmute:")) return mixOf(target.slice(8))?.muted ?? null;
  if (target.startsWith("sendmute:")) {
    const [, ch, mix] = target.split(":");
    return chOf(ch)?.mutedIn?.includes(mix) ?? null;
  }
  if (Object.hasOwn(DEVICE_TOGGLES, target) && !deviceTargetSupported(target)) return null;
  return dev()?.[target] ?? null;
}

function toggleLabel(target, inst) {
  if (!target) return "OpenXLR";
  if (target.startsWith("insert|")) {
    const [, ch, id] = target.split("|");
    const ins = resolveInsert(ch, id, metaOf(inst, target));
    const name = ins ? pluginShort(ins.label, ins.plugin) : (metaOf(inst, target)?.plugin ? pluginShort(null, metaOf(inst, target).plugin) : "Insert");
    return `${chainShort(ch)}\n${name}`;
  }
  if (target.startsWith("inschain|")) return `${chainShort(target.slice(9))}\nInserts`;
  if (isProfileTarget(target)) return `Profile\n${target.slice(8)}`;
  if (target === "auxPort") return "Aux\nPort";
  if (target === "softLowCut") {
    const hz = mixer()?.lowCutHz ?? 0;
    return hz ? `Low Cut\n${hz} Hz` : "Low Cut\nOff";
  }
  if (target.startsWith("monitor:")) {
    const sink = target.slice(8);
    const d = daemonState?.devices?.find((x) => x.name === sink);
    const name = d?.description ?? sink.split(".").pop();
    return "Monitor\n" + name;
  }
  if (target.startsWith("mixmute:")) return `${mixName(mixer(), target.slice(8))}\nMute`;
  if (target.startsWith("sendmute:")) {
    const [, ch, mix] = target.split(":");
    return `${channelName(mixer(), ch)}\n· ${mixShortName(mixer(), mix)}`;
  }
  return DEVICE_TOGGLES[target] ?? target;
}

const isMuteLike = (t) =>
  MUTE_LIKE.has(t) || t?.startsWith("mixmute:") || t?.startsWith("sendmute:");

// A dial target as {label, pct 0..100, text, muted}, or null when unknown.
function dialValue(target, inst) {
  if (!daemonState || !target) return null;
  const pct = (v) => Math.round(v * 100);
  if (target.startsWith("insparam|")) {
    const [, ch, id, symbol] = target.split("|");
    const ins = resolveInsert(ch, id, metaOf(inst, target));
    const p = ins ? paramInfo(ins.plugin, symbol) : null;
    if (!ins || !p) return null;
    const v = ins.params?.[symbol] ?? p.default;
    return { pin: pluginShort(ins.label, ins.plugin), scroll: p.name,
             pct: paramPct(p, v), text: ins.bypass ? "BYPASS" : paramText(p, v), muted: !!ins.bypass };
  }
  if (target.startsWith("send:")) {
    const [, chId, mix] = target.split(":");
    const ch = chOf(chId);
    if (!ch) return null;
    const v = mix === "all" ? (ch.levels?.monitor ?? 0) : (ch.levels?.[mix] ?? 0);
    const muted = mix === "all"
      ? Object.keys(ch.levels ?? {}).every((m) => ch.mutedIn?.includes(m))
      : ch.mutedIn?.includes(mix) ?? false;
    return { pin: channelName(mixer(), chId),
             scroll: mix === "all" ? "All mixes" : mixName(mixer(), mix),
             pct: pct(v), text: muted ? "MUTED" : `${pct(v)}%`, muted };
  }
  if (target.startsWith("mixvol:")) {
    const mix = mixOf(target.slice(7));
    if (!mix) return null;
    return { label: `${mix.name} mix`, pct: pct(mix.volume),
             text: mix.muted ? "MUTED" : `${pct(mix.volume)}%`, muted: mix.muted };
  }
  const s = dev(), x = mixer();
  switch (target) {
    case "outputVolume": {
      const v = x?.outputVolume ?? 0;
      const muted = mixOf("monitor")?.muted ?? false;
      return { label: "Monitor", pct: pct(v), text: muted ? "MUTED" : `${pct(v)}%`, muted };
    }
    case "gain": case "gain2": {
      if (!deviceTargetSupported(target)) return null;
      const db = target === "gain" ? s?.gainDb : s?.gain2Db;
      const muted = target === "gain" ? s?.mute : s?.mute2;
      if (db == null) return null;
      return { label: target === "gain" ? "XLR 1 gain" : "XLR 2 gain",
               pct: Math.round((db / 80) * 100), text: muted ? "MUTED" : `${db} dB`, muted };
    }
    case "hp": case "hp2": {
      if (!deviceTargetSupported(target)) return null;
      const db = target === "hp" ? s?.hpVolumeDb : s?.hp2VolumeDb;
      if (db == null) return null;
      const p = Math.round(((60 + db) / 60) * 100);
      const jackOff = (target === "hp" ? s?.outHp1 : s?.outHp2) === false;
      return { label: target === "hp" ? "Phones 1" : "Phones 2", pct: p,
               text: jackOff ? "MUTED" : `${p}%`, muted: jackOff };
    }
    case "auxLevel": {
      if (!deviceTargetSupported(target)) return null;
      const db = s?.auxLevelDb;
      if (db == null) return null;
      const p = Math.round(((60 + db) / 60) * 100);
      return { label: "Aux In level", pct: p, text: `${p}%`, muted: false };
    }
    case "crossfade": {
      if (!deviceTargetSupported(target)) return null;
      const v = s?.crossfade;
      if (v == null) return null;
      const text = v === 100 ? "centre" : v < 100 ? `mic +${100 - v}` : `pc +${v - 100}`;
      return { label: "Mic ↔ PC", pct: Math.round(v / 2), text, muted: false };
    }
  }
  return null;
}

// ---------- input handlers ----------
function onKeyDown(context, inst) {
  const t = inst.settings.target;
  const cur = toggleValue(t, inst);
  if (cur === null) { send({ event: "showAlert", context }); return; }
  if (t.startsWith("insert|")) {
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
  else if (isProfileTarget(t)) cmd({ cmd: "loadProfile", name: t.slice(8) });   // re-applies when already lit
  else if (t === "softLowCut") {
    const hz = mixer()?.lowCutHz ?? 0;
    cmd({ cmd: "setLowCutHz", value: hz === 0 ? 80 : hz === 80 ? 120 : 0 });
  }
  else if (t === "softClipGuard") cmd({ cmd: "setSoftClipGuard", value: !cur });
  else if (t === "gainLocked") cmd({ cmd: "set", control: "gainLock", value: !cur });
  else if (t.startsWith("monitor:")) cmd({ cmd: "setMonitorOutputs", devices: [t.slice(8)] });
  else if (t.startsWith("mixmute:"))
    cmd({ cmd: "setMixMuted", mix: t.slice(8), value: !cur });
  else if (t.startsWith("sendmute:")) {
    const [, ch, mix] = t.split(":");
    cmd({ cmd: "setChannelMuted", channel: ch, mix, value: !cur });
  } else cmd({ cmd: "set", control: t, value: !cur });
}

function onDialRotate(context, inst, ticks) {
  const t = activeTarget(inst);
  if (!t || !daemonState) return;
  if (["gain", "gain2", "hp", "hp2", "auxLevel", "crossfade"].includes(t) &&
      !deviceTargetSupported(t)) {
    send({ event: "showAlert", context });
    return;
  }
  const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
  if (t.startsWith("insparam|")) {
    const [, ch, id, symbol] = t.split("|");
    const ins = resolveInsert(ch, id, metaOf(inst, t));
    const p = ins ? paramInfo(ins.plugin, symbol) : null;
    if (!ins || !p) return;
    const v = ins.params?.[symbol] ?? p.default;
    cmd({ cmd: "setInsertParam", channel: ch, insertId: ins.id, symbol, value: paramStep(p, v, ticks) });
  } else if (t.startsWith("send:")) {
    const [, ch, mix] = t.split(":");
    const levels = chOf(ch)?.levels;
    if (!levels) return;
    for (const m of mix === "all" ? Object.keys(levels) : [mix]) {
      if (levels[m] == null) continue;
      cmd({ cmd: "setLevel", channel: ch, mix: m, value: clamp(levels[m] + ticks * 0.01, 0, 1) });
    }
  } else if (t.startsWith("mixvol:")) {
    const mix = t.slice(7), v = mixOf(mix)?.volume;
    if (v == null) return;
    cmd({ cmd: "setMixVolume", mix, value: clamp(v + ticks * 0.01, 0, 1) });
  } else if (t === "outputVolume") {
    const v = mixer()?.outputVolume;
    if (v == null) return;
    cmd({ cmd: "setOutputVolume", value: clamp(v + ticks * 0.01, 0, 1) });
  } else if (t === "gain" || t === "gain2") {
    const db = t === "gain" ? dev()?.gainDb : dev()?.gain2Db;
    if (db == null) return;
    cmd({ cmd: "set", control: t === "gain" ? "gain" : "gain2",
          value: clamp(db + ticks, 0, 80) });
  } else if (t === "hp" || t === "hp2") {
    const db = t === "hp" ? dev()?.hpVolumeDb : dev()?.hp2VolumeDb;
    if (db == null) return;
    cmd({ cmd: "set", control: t === "hp" ? "hpVolumeDb" : "hp2VolumeDb",
          value: clamp(db + ticks * 0.6, -60, 0) });
  } else if (t === "auxLevel") {
    const db = dev()?.auxLevelDb;
    if (db == null) return;
    cmd({ cmd: "set", control: "auxLevelDb", value: clamp(db + ticks * 0.6, -60, 0) });
  } else if (t === "crossfade") {
    const v = dev()?.crossfade;
    if (v == null) return;
    cmd({ cmd: "set", control: "crossfade", value: clamp(v + ticks * 5, 0, 200) });
  }
}

function onDialPress(context, inst) {
  const t = activeTarget(inst);
  if (!t) return;
  if (t.startsWith("insparam|")) {
    // the press is the insert's bypass, like a dial's mute
    const [, ch, id] = t.split("|");
    const ins = resolveInsert(ch, id, metaOf(inst, t));
    if (ins) cmd({ cmd: "setInsertBypass", channel: ch, insertId: ins.id, value: !ins.bypass });
  } else if (t.startsWith("send:")) {
    const [, ch, mix] = t.split(":");
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
    const mix = t.slice(7), muted = mixOf(mix)?.muted;
    if (muted != null) cmd({ cmd: "setMixMuted", mix, value: !muted });
  } else if (t === "gain" || t === "gain2") {
    const control = t === "gain" ? "mute" : "mute2";
    const muted = dev()?.[control];
    if (muted != null) cmd({ cmd: "set", control, value: !muted });
  } else if (t === "outputVolume") {
    const muted = mixOf("monitor")?.muted;
    if (muted != null) cmd({ cmd: "setMixMuted", mix: "monitor", value: !muted });
  } else if (t === "hp" || t === "hp2") {
    // no per-jack mute register exists; the output selector is the mute
    const control = t === "hp" ? "outHp1" : "outHp2";
    const on = dev()?.[control];
    if (on != null) cmd({ cmd: "set", control, value: !on });
  } else if (t === "crossfade") {
    cmd({ cmd: "set", control: "crossfade", value: 100 });   // back to centre
  }
}

// ---------- rendering ----------
// Visual language borrowed from Wave Link's deck plugin (all artwork is
// ours): a full-bleed colored frame that reads state at a glance (red =
// muted, light = engaged), an inner dark card, and a white glyph. Words
// (48V, EXP, ...) ride the deck's own title renderer via setTitle.

// Centered glyphs in a 144x144 viewBox, drawn in white.
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
  // a stack of scene cards, for profile keys
  scene: `<rect x="44" y="52" width="56" height="40" rx="6" fill="none" stroke="currentColor" stroke-width="6"/>
        <path d="M52 44 h52 a6 6 0 0 1 6 6 v34" fill="none" stroke="currentColor" stroke-width="6" stroke-linecap="round"/>
        <path d="M56 78 l10 -12 l9 8 l8 -14 l11 18" fill="none" stroke="currentColor" stroke-width="5" stroke-linejoin="round" stroke-linecap="round"/>`,
};

// Which glyph a toggle target wears; unlisted targets are word keys
// (frame + LED + title only).
function glyphFor(t) {
  if (!t) return null;
  if (t === "mute" || t === "mute2") return "mic";
  if (t === "outHp1" || t === "outHp2" || t === "lowImpedance") return "headphones";
  if (t === "outLineOut") return "jack";
  if (t.startsWith("monitor:") || t.startsWith("mixmute:")) return "speaker";
  if (t.startsWith("sendmute:")) return "fader";
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

function keySvg(on, muteLike, known, glyphName, badge, label, offColor = null) {
  // The keys speak the touch strips' hardware language: the same faceplate
  // material (the strip tiles' #383838 with the side-lit gradient and #505050
  // border), a machined round button cap like the dial knob, a status LED,
  // and for the low cut an inset LED display window. offColor lights the
  // OFF state too (an insert's bypass shows red, like the UI's LED).
  const accent = !known ? null : on ? (muteLike ? "#FF3C4E" : "#3ecf7a") : offColor;
  const ink = !known ? "#6a7080" : accent ?? "#d2d6de";
  const lines = label ? label.split("\n").slice(0, 2) : [];

  // Button cap (glyph keys) or LED display window (badge keys) or lamp only.
  const capY = lines.length ? 52 : 66;
  let face;
  if (glyphName) {
    const glyph = GLYPHS[glyphName].replaceAll("currentColor", ink);
    face = `
      <circle cx="72" cy="${capY}" r="38" fill="none" stroke="#000" stroke-opacity="0.4" stroke-width="6"/>
      <circle cx="72" cy="${capY}" r="34" fill="url(#cap)" stroke="#5a5f68" stroke-width="4"/>
      ${accent ? `<circle cx="72" cy="${capY}" r="37" fill="none" stroke="${accent}" stroke-width="6" opacity="0.6" filter="url(#bloom)"/>` : ""}
      <g transform="translate(72 ${capY}) scale(0.62) translate(-72 -72)">${glyph}</g>`;
  } else if (badge) {
    face = `
      <rect x="24" y="${capY - 30}" width="96" height="60" rx="8" fill="#0c0e11" stroke="#000" stroke-opacity="0.5" stroke-width="5"/>
      ${accent ? `<g filter="url(#bloom)" opacity="0.65">${sevenSegText(badge, 72, capY - 19, 38, accent)}</g>` : ""}
      ${sevenSegText(badge, 72, capY - 19, 38, known ? (accent ?? "#7d8494") : "#4a4f5c")}`;
  } else {
    const lamp = !known ? "#4a4f5c" : accent ?? "#2c2f36";
    face = `
      <circle cx="72" cy="${capY}" r="38" fill="none" stroke="#000" stroke-opacity="0.4" stroke-width="6"/>
      <circle cx="72" cy="${capY}" r="34" fill="url(#cap)" stroke="#5a5f68" stroke-width="4"/>
      ${accent ? `<circle cx="72" cy="${capY}" r="15" fill="${lamp}" filter="url(#bloom)" opacity="0.8"/>` : ""}
      <circle cx="72" cy="${capY}" r="12" fill="${lamp}" stroke="#15161a" stroke-width="4"/>`;
  }

  const slash = muteLike && on
    ? `<line x1="${72 - 26}" y1="${capY + 26}" x2="${72 + 26}" y2="${capY - 26}" stroke="#FF3C4E" stroke-width="8" stroke-linecap="round"/>`
    : "";

  // Status LED lamp in the top-right corner, like a channel strip indicator.
  const led = glyphName || !badge ? "" : "";
  const lampDot = glyphName
    ? `<circle cx="120" cy="24" r="8" fill="${!known ? "#4a4f5c" : accent ?? "#2c2f36"}" stroke="#15161a" stroke-width="3"/>` +
      (accent ? `<circle cx="120" cy="24" r="11" fill="${accent}" opacity="0.5" filter="url(#soft)"/>` : "")
    : "";

  const labelSvg = lines.map((line, i) => {
    const size = line.length > 11 ? 19 : line.length > 8 ? 22 : 26;
    const y = lines.length === 1 ? 126 : 106 + i * 24;
    return `<text x="72" y="${y}" text-anchor="middle" fill="#e8ebf2" ` +
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
        <radialGradient id="cap" cx="0.5" cy="0.3" r="0.9">
          <stop offset="0" stop-color="#4a4a4a"/>
          <stop offset="0.7" stop-color="#404040"/>
          <stop offset="1" stop-color="#333333"/>
        </radialGradient>
        <filter id="soft" x="-40%" y="-40%" width="180%" height="180%">
          <feGaussianBlur stdDeviation="3"/>
        </filter>
        <filter id="bloom" x="-40%" y="-40%" width="180%" height="180%">
          <feGaussianBlur stdDeviation="4.5"/>
        </filter>
      </defs>
      <rect x="6" y="6" width="132" height="132" rx="14" fill="#383838"/>
      <rect x="6" y="6" width="132" height="132" rx="14" fill="url(#side)"/>
      <rect x="9" y="9" width="126" height="126" rx="12" fill="none" stroke="#50555e" stroke-width="4"/>
      ${face}${slash}${led}${lampDot}${labelSvg}
    </svg>`).toString("base64");
}

// 24x24 white icons for the dial layout's corner slot.
function dialIcon(t) {
  const inner = (name) => GLYPHS[name]
    ? `<g transform="scale(0.1667)">${GLYPHS[name].replaceAll("currentColor", "#ffffff")}</g>` : "";
  let name = "knob";
  if (t?.startsWith("send:")) name = "fader";
  else if (t?.startsWith("mixvol:")) name = "speaker";
  else if (t === "outputVolume") name = "speaker";
  else if (t === "gain" || t === "gain2") name = "mic";
  else if (t === "hp" || t === "hp2") name = "headphones";
  else if (t === "crossfade") name = "xfade";
  return "data:image/svg+xml;base64," + Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24">${inner(name)}</svg>`
  ).toString("base64");
}

// The rotating needle over the half-knob, Wave Link style: a tick rotated
// around a center below the visible strip. 0..100% sweeps -50°..+50°.
function needleSvg(pct) {
  const angle = (Math.max(0, Math.min(100, pct)) / 100) * 100 - 50;
  return "data:image/svg+xml;base64," + Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="130" height="52" viewBox="0 0 130 52">
      <g transform="rotate(${angle}, 65, 61)">
        <rect x="63.5" y="30" width="3" height="16" rx="1.5" fill="#fff"/>
      </g>
    </svg>`).toString("base64");
}

// The meter key feeding a dial target's level bar.
function meterKeyFor(t) {
  if (!t) return null;
  if (t.startsWith("send:")) return `ch:${t.split(":")[1]}`;
  if (t.startsWith("mixvol:")) return `mix:${t.slice(7)}`;
  if (t === "gain") return "ch:xlr1";
  if (t === "gain2") return "ch:xlr2";
  if (t === "auxLevel") return "ch:aux";
  if (t.startsWith("insparam|")) {
    const ch = t.split("|")[1];
    return ch.startsWith("mix:") ? ch : `ch:${ch}`;
  }
  return "mix:monitor";   // outputVolume, hp, hp2, crossfade
}

function meterSvg(level) {
  const w = Math.round(Math.max(0, Math.min(1, level)) * 130);
  const hot = level > 0.92;
  return "data:image/svg+xml;base64," + Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="130" height="6" viewBox="0 0 130 6">
      <rect width="130" height="6" rx="3" fill="#252525"/>
      ${w > 0 ? `<rect width="${w}" height="6" rx="3" fill="${hot ? "#FF3C4E" : "#3ecf7a"}"/>` : ""}
    </svg>`).toString("base64");
}

// Meters tick at 15 Hz; only redraw a dial's bar when its value moved
// visibly, so the strip is not re-rendered for noise.
const lastMeter = new Map();   // context -> rounded width last drawn
function refreshMeters() {
  if (!meterLevels) return;
  for (const [context, inst] of instances) {
    if (inst.action !== "com.emaspa.openxlr.dial") continue;
    const key = meterKeyFor(activeTarget(inst));
    if (!key || !(key in meterLevels)) continue;
    const lr = meterLevels[key];
    const level = Math.max(lr[0] ?? 0, lr[1] ?? 0);
    const bucket = Math.round(level * 65);
    if (lastMeter.get(context) === bucket) continue;
    lastMeter.set(context, bucket);
    send({ event: "setFeedback", context, payload: { meter: meterSvg(level) } });
  }
}

// The title is rendered as our own pixmap so the scroll is pixel-exact
// across the full strip width, the way Wave Link's plugin uses it. Text
// width is made deterministic with SVG textLength (approximated from a
// per-character average, then enforced by the renderer). A send dial
// pins the channel name and scrolls the mix name in the space that
// remains; other long titles scroll whole.
const TITLE_W = 158, TITLE_H = 24, CHAR_W = 8.1, GAP_PX = 20, STEP_PX = 7;
const escXml = (t) => t.replace(/&/g, "&amp;").replace(/</g, "&lt;");
const textW = (t) => Math.round(t.length * CHAR_W);

function titleSvg(pinText, scroll, offsetPx) {
  const attrs = 'y="17" font-family="sans-serif" font-size="14.5" font-weight="700" fill="#ffffff"';
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
      ? (daemonUp ? (isInsertTarget(t) || isProfileTarget(t) ? toggleLabel(t, inst) : defaultTitle(t)) : "offline") : "";
    // The user can pick a glyph per key (a monitor output may be headphones
    // rather than speakers); "auto" or unset keeps the target's default.
    const iconChoice = inst.settings.icon;
    const glyphName = iconChoice && GLYPHS[iconChoice] ? iconChoice : glyphFor(t);
    const offColor = isInsertTarget(t) ? "#FF3C4E" : null;   // bypassed = red, as in the UI
    send({ event: "setImage", context,
           payload: { image: keySvg(v === true, isMuteLike(t), v !== null && daemonUp, glyphName, badge, label, offColor) } });
  } else if (inst.action === "com.emaspa.openxlr.dial") {
    const d = dialValue(t, inst);
    const isDb = t === "gain" || t === "gain2";
    send({ event: "setFeedback", context, payload: d
      ? { title: marqueeTitle(context, d.pin ?? "", d.scroll ?? d.label),
          value: isDb && !d.muted ? d.text.replace(" dB", "") : d.text,
          unit: { enabled: isDb && !d.muted },
          icon: dialIcon(t),
          needle: needleSvg(d.pct),
          muteOverlay: { enabled: d.muted } }
      : { title: "OpenXLR", value: daemonUp ? "set up" : "offline",
          unit: { enabled: false }, icon: dialIcon(null),
          needle: needleSvg(0), muteOverlay: { enabled: false } } });
  }
}

function refreshAll() { for (const context of instances.keys()) refresh(context); }

connectDaemon();
