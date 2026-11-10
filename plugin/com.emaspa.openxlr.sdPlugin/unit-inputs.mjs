// The driven interface's microphone inputs as the deck names them. Gain,
// mute, phantom power and the hardware effects belong to the interface's
// input stage, not to a strip: they stay unit controls (`gain`, `mute2` and
// the like), and their labels name the channel that carries that input,
// which the daemon reports in each channel's `unitInput`. XLR 1 carries
// input 1 until it is bound to another microphone; then a capture channel on
// the interface's own node may carry it, or nothing does.

// Unit controls on one of the two inputs, with the word under the channel's
// name on a key face.
const UNIT_CONTROLS = {
  mute: [1, "Mute"], mute2: [2, "Mute"],
  phantom: [1, "48V"], phantom2: [2, "48V"],
  lowCut: [1, "Low Cut"], lowCut2: [2, "Low Cut"],
  expander: [1, "Expander"], expander2: [2, "Expander"],
  voiceTune: [1, "Voice Tune"], voiceTune2: [2, "Voice Tune"],
  clipGuard: [1, "ClipGuard"], clipGuard2: [2, "ClipGuard"],
  compressor: [1, "Comp"], compressor2: [2, "Comp"],
};
const UNIT_DIALS = { gain: 1, gain2: 2 };

const CHANNEL_CONTROLS = { chgain: "gain", chmute: "mute", chphantom: "phantom", chlowcut: "lowCut", chclipguard: "clipGuard" };

export function channelUnitTarget(target) {
  if (typeof target !== "string") return null;
  const [prefix, channel] = target.split(":");
  return channel && Object.hasOwn(CHANNEL_CONTROLS, prefix) ? { channel, control: CHANNEL_CONTROLS[prefix] } : null;
}

// Resolve on each press or turn. A saved channel target follows its current
// carrier, and becomes unavailable when that unit leaves or stops being driven.
export function channelUnitControl(state, channel, control) {
  if (!Object.values(CHANNEL_CONTROLS).includes(control)) return null;
  const input = state?.mixer?.channels?.find(c => c.id === channel)?.unitInput;
  if (!input?.unit || ![1, 2].includes(input.input)) return null;
  const holder = input.primary === true ? state : state?.waveInterfaces?.find(unit =>
    unit.id === input.unit && unit.active !== true && unit.enabled === true);
  const name = control + (input.input === 2 ? "2" : "");
  if (holder?.connected !== true || !deviceTargetSupported(holder.capabilities, name)) return null;
  const value = holder.state?.[control === "gain" ? `${name}Db` : name];
  if (control === "gain" ? !Number.isFinite(value) : typeof value !== "boolean") return null;
  const max = holder.capabilities.gainMaxDb;
  return { device: input.unit, control: name, value,
    max: control === "gain" ? Number.isInteger(max) && max > 0 ? max : 80 : null };
}

// A capture input's own gain, its source's volume, as {value, min, max,
// hardware, muted}, or null. The daemon reports captureGain only on a capture
// input whose source no Wave unit drives; a channel that carries a unit's
// input has the unit's gain instead, through channelUnitControl. muted is the
// source's own mute, null from a daemon that does not report it.
export function captureGainOf(state, channel) {
  const c = state?.mixer?.channels?.find(ch => ch.id === channel);
  const g = c?.captureGain;
  if (!c || c.unitInput || !g || typeof g !== "object") return null;
  if (![g.gainDb, g.minDb, g.maxDb].every(Number.isFinite) || g.maxDb <= g.minDb) return null;
  return { value: g.gainDb, min: g.minDb, max: g.maxDb, hardware: g.hardware === true,
    muted: typeof g.muted === "boolean" ? g.muted : null };
}

// A capture input's source mute, true or false, or null where the channel
// has none: it carries a unit's input, whose mute is the unit's, or the
// daemon reports no mute beside the gain.
export function captureMuteOf(state, channel) {
  return captureGainOf(state, channel)?.muted ?? null;
}

// The input a unit control acts on, 1 or 2, or null for any other target.
export function unitInputOf(target) {
  if (Object.hasOwn(UNIT_DIALS, target)) return UNIT_DIALS[target];
  return Object.hasOwn(UNIT_CONTROLS, target) ? UNIT_CONTROLS[target][0] : null;
}

// The channel that carries the primary unit's input 1 or 2, or null. The
// daemon names it in unitInput. While no channel names one, which is the case
// with no unit connected and with a daemon that predates input bindings, a
// strip still on the interface stands for its input, as it always has.
export function unitCarrier(mixer, input) {
  const channels = Array.isArray(mixer?.channels) ? mixer.channels : [];
  const named = channels.find((c) => c?.unitInput?.primary === true && c.unitInput.input === input);
  if (named) return named;
  // The daemon names the other input but not this one: no channel carries it.
  if (channels.some((c) => c?.unitInput?.primary === true)) return null;
  const strip = channels.find((c) => c?.hardware === true && c.id === (input === 2 ? "xlr2" : "xlr1"));
  return strip && (!strip.binding || strip.binding.kind === "unit") ? strip : null;
}

// What the deck calls input 1 or 2: the carrying channel's name, "Input 1"
// while the input is not in the mixer, and with no mixer to look in the name
// the interface gives its strips (Mic 1 on a Wave:3's capsule).
export function unitInputName(state, input) {
  const carrier = unitCarrier(state?.mixer, input);
  if (typeof carrier?.name === "string" && carrier.name.trim()) return carrier.name;
  if ((state?.mixer?.channels ?? []).length) return `Input ${input}`;
  return `${state?.capabilities?.xlrJacks === false ? "Mic" : "XLR"} ${input}`;
}

// A name short enough for one line of a key face.
const keyLine = (name) => name.length <= 11 ? name : `${name.slice(0, 10)}…`;

// A unit control's key face, as two lines, or null for any other target.
export function unitKeyLabel(state, target) {
  const channel = channelUnitTarget(target);
  if (channel && channel.control !== "gain") {
    const name = state?.mixer?.channels?.find(c => c.id === channel.channel)?.name ?? channel.channel;
    return `${keyLine(name)}\n${UNIT_CONTROLS[channel.control][1]}`;
  }
  if (!Object.hasOwn(UNIT_CONTROLS, target)) return null;
  const [input, word] = UNIT_CONTROLS[target];
  return `${keyLine(unitInputName(state, input))}\n${word}`;
}

// A gain dial's title, or null for any other target.
export function unitDialLabel(state, target) {
  const channel = channelUnitTarget(target);
  if (channel?.control === "gain")
    return `${state?.mixer?.channels?.find(c => c.id === channel.channel)?.name ?? channel.channel} gain`;
  return Object.hasOwn(UNIT_DIALS, target) ? `${unitInputName(state, UNIT_DIALS[target])} gain` : null;
}

// The meter a gain dial shows: the channel the gain is heard on, or null
// when the input is not in the mixer.
export function unitMeterKey(mixer, target) {
  const channel = channelUnitTarget(target);
  if (channel?.control === "gain")
    return mixer?.channels?.some(c => c.id === channel.channel && (c.unitInput || c.captureGain)) ? `ch:${channel.channel}` : null;
  const carrier = Object.hasOwn(UNIT_DIALS, target) ? unitCarrier(mixer, UNIT_DIALS[target]) : null;
  return carrier ? `ch:${carrier.id}` : null;
}

// Ducking's switch, or null from a daemon that sends no ducking. It runs on
// any device, on the unit or in software, so no capability gates it.
export function duckingEnabled(state) {
  const ducking = state?.ducking;
  return ducking && typeof ducking === "object" ? ducking.enabled === true : null;
}

// What a press of the ducking key sends: the switch flipped from what the
// daemon reports, the setup otherwise left alone. Null without ducking, and
// the key then only flashes an alert.
export function duckingToggle(state) {
  const enabled = duckingEnabled(state);
  return enabled === null ? null : { cmd: "setDucking", value: { enabled: !enabled } };
}

// The most keys the daemon takes.
export const MAX_DUCKING_KEYS = 8;

// Ducking's keys in the order chosen, or null from a daemon that sends no
// ducking. A daemon from before several keys sends only `key`.
export function duckingKeys(state) {
  const ducking = state?.ducking;
  if (!ducking || typeof ducking !== "object") return null;
  const keys = Array.isArray(ducking.keys) ? ducking.keys.filter(key => typeof key === "string") : [];
  return keys.length ? keys : [typeof ducking.key === "string" ? ducking.key : "xlr1"];
}

// A channel that can key ducking: a microphone, one of the unit's own inputs
// or an application channel. A stereo capture input cannot.
export function canKeyDucking(channel) {
  return !!channel && (channel.microphone === true || channel.hardware === true || !channel.captureSource);
}

// Whether a channel is one of ducking's keys, or null without ducking or
// once the channel is gone or cannot key.
export function duckingKeyOn(state, id) {
  const keys = duckingKeys(state);
  const channel = state?.mixer?.channels?.find(c => c.id === id);
  return keys === null || !canKeyDucking(channel) ? null : keys.includes(id);
}

// What a press of a channel's ducking key sends: the keys with the channel
// added at the end or taken out. A channel that becomes a key leaves the
// ducked channels in the same command, since a key is never ducked. Null
// when nothing may be sent: the last key stays, a ninth is not added, and
// a channel that cannot key sends nothing.
export function duckingKeyToggle(state, id) {
  if (duckingKeyOn(state, id) === null) return null;
  const keys = duckingKeys(state);
  if (keys.includes(id))
    return keys.length > 1 ? { cmd: "setDucking", value: { keys: keys.filter(key => key !== id) } } : null;
  if (keys.length >= MAX_DUCKING_KEYS) return null;
  const value = { keys: [...keys, id] };
  const channels = state.ducking.channels;
  if (Array.isArray(channels) && channels.includes(id)) value.channels = channels.filter(channel => channel !== id);
  return { cmd: "setDucking", value };
}

// The state as it reads once the daemon takes a ducking command. A second
// press before the daemon's answer then builds on the first instead of
// sending a list that drops it; the daemon's next state replaces this one.
export function withDucking(state, value) {
  if (!state?.ducking || typeof state.ducking !== "object" || !value || typeof value !== "object") return state;
  const ducking = { ...state.ducking, ...value };
  if (Array.isArray(value.keys) && value.keys.length) ducking.key = value.keys[0];
  return { ...state, ducking };
}

// Whether the driven unit has a hardware target, by its capabilities alone.
// A binding never changes this: the unit stays driven whatever XLR 1 takes.
// With no unit connected the daemon sends no capabilities and every hardware
// target is unavailable.
export function deviceTargetSupported(capabilities, target) {
  const c = capabilities;
  if (!c || typeof c !== "object") return false;
  if (unitInputOf(target) === 2 && (c.xlrInputs ?? 1) < 2) return false;
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
    lineIn: "lineInput", lineInLevelLock: "lineInput",
  }[target];
  return capability ? c[capability] === true : target === "gainLocked";
}
