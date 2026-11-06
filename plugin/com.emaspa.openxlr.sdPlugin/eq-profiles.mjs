// The EQ profile key: a Toggle action target "eqprofile:<channel>:<profile>",
// or "eqprofile:mix:<mix>:<profile>" for a mix's output EQ, where the profile
// is the daemon's id, "builtin:" and a key or "saved:" and a name, read from
// the state with no I/O, so plugin.mjs stays small and this stays testable. A
// press is one loadEqProfile: the profile's bands on that microphone or mix
// with its EQ switched on. The key is lit while the EQ is on and holds the
// profile's bands, as the daemon reports them. Every built-in profile, the
// voice curves and the listening curves, is offered on every EQ.

import { eqHeldByUnit } from "./microphone-eq.mjs";

// Built-in profiles by key, in the plugin's language; a key it does not know
// keeps the daemon's English name.
const BUILTIN_NAMES = {
  clear: "Clear", warm: "Warm", broadcast: "Broadcast", podcast: "Podcast",
  "deep-voice": "Deep voice", "soft-sibilants": "Soft sibilants",
  "voices-forward": "Voices forward", footsteps: "Footsteps", "bass-boost": "Bass boost", "soft-treble": "Soft treble",
};
const PREFIX = "eqprofile:";

const channels = mixer => Array.isArray(mixer?.channels) ? mixer.channels : [];
const mixes = mixer => Array.isArray(mixer?.mixes) ? mixer.mixes : [];
const nameOf = channel => typeof channel.name === "string" && channel.name ? channel.name : channel.id;
const finite = value => typeof value === "number" && Number.isFinite(value);
const profileList = state => Array.isArray(state?.eqProfiles) ? state.eqProfiles : [];

export function isEqProfileTarget(target) {
  return typeof target === "string" && target.startsWith(PREFIX);
}

// A profile as the state lists it, or null when it is not one: an id with
// its prefix, a name, the built-in flag and 1 to 16 bands with every field.
function validProfile(profile) {
  if (!profile || typeof profile.id !== "string" || typeof profile.name !== "string" || typeof profile.builtin !== "boolean"
      || !profile.id.startsWith(profile.builtin ? "builtin:" : "saved:") || !Array.isArray(profile.bands)
      || profile.bands.length < 1 || profile.bands.length > 16) return false;
  return profile.bands.every(b => b && typeof b.enabled === "boolean" && typeof b.type === "string"
    && finite(b.frequency) && finite(b.gain) && finite(b.q) && b.q > 0);
}

// The name a key and the inspector show: a built-in profile's in the plugin's language.
export function eqProfileName(profile) {
  if (!profile?.builtin) return profile?.name ?? "";
  const key = profile.id.slice("builtin:".length);
  return Object.hasOwn(BUILTIN_NAMES, key) ? BUILTIN_NAMES[key] : profile.name;
}

// The target's parts, or null when it is malformed. "mix:" before an id that a
// profile id follows names a mix; a channel called "mix" is followed by the
// profile itself.
function parse(target) {
  if (!isEqProfileTarget(target) || target.length > 200) return null;
  let rest = target.slice(PREFIX.length);
  const mixed = /^mix:([a-zA-Z0-9_-]{1,64}):(builtin|saved):./.exec(rest);
  if (mixed) rest = rest.slice(4);
  const split = rest.indexOf(":");
  if (split < 1) return null;
  const channel = rest.slice(0, split), profile = rest.slice(split + 1);
  if (channel.length > 64 || !/^[a-zA-Z0-9_-]+$/.test(channel) || !/^(builtin|saved):./.test(profile)) return null;
  return {channel, profile, mix: !!mixed};
}

// The EQ's band count limits the daemon reports; four and four from a daemon that says none.
function bandLimits(mixer) {
  const limits = mixer?.eqLimits;
  const min = limits?.minBands ?? limits?.bands ?? 4, max = limits?.maxBands ?? limits?.bands ?? 4;
  return Number.isInteger(min) && Number.isInteger(max) && min >= 1 && max <= 16 && min <= max ? {min, max} : {min: 4, max: 4};
}

// The microphone's EQ holds the profile: switched on, and every band the
// profile's within half a step of the daemon's grid, so values that came
// back through the daemon still count.
export function eqHolds(eq, profile, limits) {
  if (!eq || eq.enabled !== true || !Array.isArray(eq.bands) || eq.bands.length !== profile.bands.length) return false;
  const frequencyStep = limits?.frequency?.step ?? 1, gainStep = limits?.gain?.step ?? 0.25, qStep = limits?.q?.log10Step ?? 0.016;
  return profile.bands.every((want, i) => {
    const have = eq.bands[i];
    return have && have.enabled === want.enabled && have.type === want.type
      && finite(have.frequency) && Math.abs(have.frequency - want.frequency) < frequencyStep / 2
      && finite(have.gain) && Math.abs(have.gain - want.gain) < gainStep / 2
      && finite(have.q) && have.q > 0 && Math.abs(Math.log10(have.q) - Math.log10(want.q)) < qStep / 2;
  });
}

// What an EQ profile key reads, or null when the target is malformed, the
// microphone is gone, absent or has no EQ, or the daemon lists no such
// profile: {channel, profile, lit, fits}. A saved profile is found by its
// name whatever its case, as the daemon finds it.
export function eqProfileTarget(state, target) {
  const parts = parse(target);
  if (!parts) return null;
  const mixer = state?.mixer;
  const channel = parts.mix ? mixes(mixer).find(m => m?.id === parts.channel)
    : channels(mixer).find(c => c?.id === parts.channel && c.present !== false
      && (c.microphone === true || c.microphone !== false && (c.id === "xlr1" || c.id === "xlr2")));
  if (!channel || !channel.eq || typeof channel.eq.enabled !== "boolean") return null;
  const listed = profileList(state).filter(validProfile);
  const lower = parts.profile.toLowerCase();
  const profile = listed.find(p => p.id === parts.profile)
    ?? (parts.profile.startsWith("saved:") ? listed.find(p => !p.builtin && p.id.toLowerCase() === lower) : undefined);
  if (!profile) return null;
  const {min, max} = bandLimits(mixer);
  // While hardware mixing holds a mix's EQ off, its key shows and sends nothing.
  const held = parts.mix && eqHeldByUnit(mixer, channel.id) !== null;
  return {channel, profile, mix: parts.mix, lit: eqHolds(channel.eq, profile, mixer?.eqLimits),
    fits: !held && profile.bands.length >= min && profile.bands.length <= max};
}

// What a press sends, or null when it can do nothing: no such profile,
// microphone or mix, a profile with a band count this EQ does not take, or a
// mix whose EQ hardware mixing holds off.
export function eqProfilePress(entry) {
  if (!entry || !entry.fits) return null;
  return {cmd: "loadEqProfile", ...(entry.mix ? {mix: entry.channel.id} : {channel: entry.channel.id}), profile: entry.profile.id};
}

export function eqProfileLabel(entry, target) {
  if (entry) return `${nameOf(entry.channel)}\n${eqProfileName(entry.profile)}`;
  const parts = parse(target);
  if (!parts) return "EQ profile";
  const id = parts.profile;
  const name = id.startsWith("builtin:") ? BUILTIN_NAMES[id.slice(8)] ?? id.slice(8) : id.slice(6);
  return `EQ profile\n${name}`;
}

// The inspector's groups: one per microphone with an EQ, a key per profile,
// the built-in ones first and marked as built in.
export function eqProfileChoices(state) {
  const toggleGroups = [];
  const listed = profileList(state).filter(validProfile);
  if (!listed.length) return {toggleGroups};
  for (const channel of channels(state?.mixer)) {
    if (typeof channel?.id !== "string" || !channel.eq || channel.present === false
        || !(channel.microphone === true || channel.microphone !== false && (channel.id === "xlr1" || channel.id === "xlr2"))) continue;
    const name = nameOf(channel);
    toggleGroups.push({id: `layout-eq-profiles-${channel.id}`, label: `Load EQ profile: ${name}`,
      items: listed.map(p => ({target: `${PREFIX}${channel.id}:${p.id}`,
        label: `${name} EQ: ${eqProfileName(p)}${p.builtin ? " (built in)" : ""}`}))});
  }
  for (const mix of mixes(state?.mixer)) {
    if (typeof mix?.id !== "string" || !/^[a-zA-Z0-9_-]{1,64}$/.test(mix.id) || !mix.eq) continue;
    const name = nameOf(mix);
    toggleGroups.push({id: `layout-eq-profiles-mix-${mix.id}`, label: `Load EQ profile: ${name}`,
      items: listed.map(p => ({target: `${PREFIX}mix:${mix.id}:${p.id}`,
        label: `${name} EQ: ${eqProfileName(p)}${p.builtin ? " (built in)" : ""}`}))});
  }
  return {toggleGroups};
}
