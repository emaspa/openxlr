// Microphone EQ targets use stable channel ids and the daemon's reported
// ranges. There is no second EQ, preset table or hardware backend here.
// A capture input on a stereo pair has the same EQ targets, with no noise
// removal beside them.
// A microphone's noise removal switch (noise:<id>) rides along: it lives on
// the same channels and acts like the EQ's own switch.
// A mix's output EQ takes the same targets with "mix" before the mix's id
// (eq:mix:<id>, eqgain:mix:<id>:<band>, ...), which the part count keeps
// apart from a channel called "mix"; its commands name the mix. While
// hardware mixing on the Wave XLR Pro holds a mix's EQ off, the keys and
// dials show it and send nothing.
const FIELDS = {eq: "enabled", noise: "enabled", eqband: "enabled", eqtype: "type", eqfreq: "frequency", eqgain: "gain", eqq: "q"};
const TYPE_NAMES = {lowCut: "High pass", lowShelf: "Low shelf", peak: "Bell", notch: "Notch", highShelf: "High shelf", highCut: "Low pass"};
const typeName = (type, compact = false) => compact && type === "highShelf" ? "Hi shelf"
  : Object.hasOwn(TYPE_NAMES, type) ? TYPE_NAMES[type] : type;
const channels = mixer => Array.isArray(mixer?.channels) ? mixer.channels : [];
const mixes = mixer => Array.isArray(mixer?.mixes) ? mixer.mixes : [];
const nameOf = channel => typeof channel.name === "string" && channel.name ? channel.name : channel.id;
const usesGain = type => ["lowShelf", "peak", "highShelf"].includes(type);
const finite = value => typeof value === "number" && Number.isFinite(value);
const clamp = (value, min, max) => Math.min(max, Math.max(min, value));
const round = value => Math.sign(value) * Math.round(Math.abs(value));

// A channel the EQ targets may name: a microphone, or any capture input,
// whose EQ the daemon reports on a stereo pair too.
export function carriesEq(channel) {
  return channel?.microphone === true || typeof channel?.captureSource === "string" && channel.captureSource !== ""
    || channel?.microphone !== false && (channel?.id === "xlr1" || channel?.id === "xlr2");
}

export function isEqTarget(target) {
  return typeof target === "string" && /^(eq|noise|eqband|eqtype|eqfreq|eqgain|eqq):/.test(target);
}

function validRange(range, logarithmic = false, step = "step") {
  return range && finite(range.min) && finite(range.max) && finite(range.max - range.min) && range.min < range.max
    && (!logarithmic || range.min > 0 && finite(Math.log(range.max / range.min)))
    && finite(range[step]) && range[step] > 0;
}

// A target's parts: a mix's when "mix" comes second and the count is a mix
// target's, else a channel's.
function parts(target) {
  const [kind, ...rest] = target.split(":");
  const banded = !(kind === "eq" || kind === "noise");
  const mix = rest[0] === "mix" && rest.length === (banded ? 3 : 2);
  const [id, number, ...extra] = mix ? rest.slice(1) : rest;
  return {kind, mix, id, number, extra};
}

// The daemon's sentence while hardware mixing holds a mix's EQ off, or null.
export function eqHeldByUnit(mixer, id) {
  const mode = mixer?.hardwareMixing;
  const sentence = mode?.active === true ? mode.disabled?.[`eq:mix:${id}`] : undefined;
  return typeof sentence === "string" ? sentence : null;
}

export function eqTarget(mixer, target) {
  if (!isEqTarget(target) || target.length > 128) return null;
  const {kind, mix, id, number, extra} = parts(target);
  if (!id || id.length > 64 || !/^[a-zA-Z0-9_-]+$/.test(id) || extra.length || mix && kind === "noise"
      || (kind === "eq" || kind === "noise" ? number !== undefined : !/^[1-9][0-9]?$/.test(number ?? ""))) return null;
  const channel = mix ? mixes(mixer).find(m => m?.id === id) : channels(mixer).find(c => c?.id === id && c.present !== false
    && carriesEq(c));
  const eq = channel?.eq, limits = mixer?.eqLimits;
  const min = limits?.minBands ?? limits?.bands, max = limits?.maxBands ?? limits?.bands;
  if (!eq || typeof eq.enabled !== "boolean" || !limits || !Number.isInteger(limits.bands)
      || limits.bands < 1 || limits.bands > 16 || !Number.isInteger(min) || !Number.isInteger(max)
      || min < 1 || max > 16 || min > max || !Array.isArray(eq.bands) || eq.bands.length < min || eq.bands.length > max
      || !Array.isArray(limits.types) || !limits.types.length || limits.types.length > 16
      || limits.types.some(t => typeof t !== "string" || t.length > 64 || !/^[a-zA-Z]+$/.test(t))
      || new Set(limits.types).size !== limits.types.length
      || !validRange(limits.frequency, true) || !validRange(limits.gain) || !validRange(limits.q, true, "log10Step")) return null;
  for (const band of eq.bands) {
    if (!band || typeof band.enabled !== "boolean" || !limits.types.includes(band.type)
        || !["frequency", "gain", "q"].every(field => finite(band[field])
          && band[field] >= limits[field].min && band[field] <= limits[field].max)) return null;
  }
  const noise = mix ? undefined : channel.noise;
  if (kind === "noise" && (!noise || typeof noise.enabled !== "boolean")) return null;
  const band = kind === "eq" || kind === "noise" ? 0 : Number(number);
  if (band > eq.bands.length) return null;
  const field = FIELDS[kind], state = kind === "noise" ? noise : band ? eq.bands[band - 1] : eq;
  const value = field === "type" ? limits.types.indexOf(state.type) : state[field];
  // Whether the switch can be turned on: the EQ's, or noise removal's.
  const held = mix ? eqHeldByUnit(mixer, id) : null;
  const available = !held && (kind === "noise" ? noise.available : eq.available) !== false;
  // What the targets name it by, and the meter its dial shows.
  const key = mix ? `mix:${id}` : id, meter = mix ? `mix:${id}` : `ch:${id}`;
  return {kind, mix, key, meter, channel, eq, limits, band, field, value, available, held,
    usable: field !== "gain" || usesGain(state.type)};
}

export function eqChoices(mixer) {
  const toggleGroups = [], levelGroups = [];
  for (const channel of channels(mixer)) {
    if (typeof channel?.id !== "string") continue;
    const entry = eqTarget(mixer, `eq:${channel.id}`);
    if (!entry) continue;
    const name = nameOf(channel);
    const toggles = [{target: `eq:${channel.id}`, label: `${name} EQ on/off`}];
    if (eqTarget(mixer, `noise:${channel.id}`))
      toggles.push({target: `noise:${channel.id}`, label: `${name} noise removal on/off`});
    const levels = [];
    for (let band = 1; band <= entry.eq.bands.length; band++) {
      toggles.push({target: `eqband:${channel.id}:${band}`, label: `${name} EQ band ${band} on/off`},
        {target: `eqtype:${channel.id}:${band}`, label: `${name} EQ band ${band} next type`});
      for (const [prefix, label] of [["eqfreq", "frequency"], ["eqgain", "gain"], ["eqq", "Q"], ["eqtype", "type"]])
        levels.push({target: `${prefix}:${channel.id}:${band}`, label: `${name} EQ band ${band} ${label}`});
    }
    const group = channel.microphone === false ? "Input EQ" : "Microphone EQ";
    toggleGroups.push({id: `layout-eq-keys-${channel.id}`, label: `${group}: ${name}`, items: toggles});
    levelGroups.push({id: `layout-eq-dials-${channel.id}`, label: `${group}: ${name}`, items: levels});
  }
  for (const mix of mixes(mixer)) {
    if (typeof mix?.id !== "string") continue;
    const entry = eqTarget(mixer, `eq:mix:${mix.id}`);
    if (!entry) continue;
    const name = nameOf(mix), key = `mix:${mix.id}`;
    const toggles = [{target: `eq:${key}`, label: `${name} EQ on/off`}], levels = [];
    for (let band = 1; band <= entry.eq.bands.length; band++) {
      toggles.push({target: `eqband:${key}:${band}`, label: `${name} EQ band ${band} on/off`},
        {target: `eqtype:${key}:${band}`, label: `${name} EQ band ${band} next type`});
      for (const [prefix, label] of [["eqfreq", "frequency"], ["eqgain", "gain"], ["eqq", "Q"], ["eqtype", "type"]])
        levels.push({target: `${prefix}:${key}:${band}`, label: `${name} EQ band ${band} ${label}`});
    }
    toggleGroups.push({id: `layout-eq-keys-mix-${mix.id}`, label: `Output EQ: ${name}`, items: toggles});
    levelGroups.push({id: `layout-eq-dials-mix-${mix.id}`, label: `Output EQ: ${name}`, items: levels});
  }
  return {toggleGroups, levelGroups};
}

export function eqStep(entry, current, ticks) {
  if (!entry?.usable || !finite(current) || !Number.isSafeInteger(ticks) || ticks === 0 || entry.field === "enabled") return null;
  if (entry.field === "type") {
    const count = entry.limits.types.length;
    return ((current + ticks % count) % count + count) % count;
  }
  const range = entry.limits[entry.field];
  const next = entry.field === "frequency" ? current * Math.pow(2, ticks / 24)
    : entry.field === "q" ? current * Math.pow(10, ticks * range.log10Step)
    : current + ticks * range.step;
  const bounded = clamp(next, range.min, range.max);
  const snapped = entry.field === "q"
    ? Math.pow(10, Math.log10(range.min) + round((Math.log10(bounded) - Math.log10(range.min)) / range.log10Step) * range.log10Step)
    : round(bounded / range.step) * range.step;
  return finite(snapped) ? clamp(snapped, range.min, range.max) : null;
}

export function eqCommand(entry, value) {
  if (!entry || entry.held) return null;
  const {field, band, channel, limits} = entry;
  const where = entry.mix ? {mix: channel.id} : {channel: channel.id};
  if (field === "enabled") {
    if (typeof value !== "boolean" || !band && value && !entry.available) return null;
    if (entry.kind === "noise") return {cmd: "setNoiseRemoval", ...where, enabled: value};
    return band ? {cmd: "setEqBand", ...where, band, enabled: value}
      : {cmd: "setEqEnabled", ...where, value};
  }
  if (field === "type") {
    if (!Number.isInteger(value) || value < 0 || value >= limits.types.length) return null;
    value = limits.types[value];
  } else if (!entry.usable || !finite(value) || value < limits[field].min || value > limits[field].max) return null;
  return {cmd: "setEqBand", ...where, band, [field]: value};
}

export function eqLabel(entry, value = entry?.value) {
  if (!entry) return "Microphone EQ";
  const name = nameOf(entry.channel);
  if (entry.kind === "noise") return `${name}\nNoise`;
  return !entry.band ? `${name}\nEQ` : entry.field === "type"
    ? `${name}\n${entry.band}: ${typeName(entry.limits.types[value])}`
    : `${name}\nEQ band ${entry.band}`;
}

export function eqDial(entry, value) {
  if (!entry || entry.field === "enabled" || !finite(value)) return null;
  if (entry.field === "type" && (!Number.isInteger(value) || value < 0 || value >= entry.limits.types.length)) return null;
  const range = entry.limits[entry.field];
  if (range) value = clamp(value, range.min, range.max);
  const pct = entry.field === "type" ? value / Math.max(1, entry.limits.types.length - 1) * 100
    : entry.field === "gain" ? (value - range.min) / (range.max - range.min) * 100
    : Math.log(value / range.min) / Math.log(range.max / range.min) * 100;
  const text = !entry.usable ? "n/a" : entry.field === "type" ? typeName(entry.limits.types[value], true)
    : entry.field === "frequency" ? `${Math.round(value)} Hz` : entry.field === "gain" ? `${Number(value.toFixed(2))} dB`
    : `Q ${Number(value.toFixed(4))}`;
  const status = entry.held ? " (held by the unit)" : entry.eq.available === false ? " (unavailable)" : !entry.eq.enabled ? " (EQ off)"
    : !entry.eq.bands[entry.band - 1].enabled ? " (band off)" : "";
  return {pin: nameOf(entry.channel), scroll: `EQ ${entry.band} ${entry.field}${status}`, pct: clamp(pct, 0, 100), text, muted: false};
}
