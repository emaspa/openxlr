import assert from "node:assert/strict";
import test from "node:test";
import {isEqProfileTarget, eqProfileTarget, eqProfilePress, eqProfileLabel, eqProfileName, eqProfileChoices, eqHolds}
  from "../com.emaspa.openxlr.sdPlugin/eq-profiles.mjs";
import {layoutChoices, targetAppearance} from "../com.emaspa.openxlr.sdPlugin/layout-choices.mjs";

const Q = 0.7046930689671475;
const band = (type, frequency, gain = 0, q = Q, enabled = true) => ({enabled, type, frequency, gain, q});
const clear = [band("lowCut", 80), band("peak", 300, -2, 1.0046157902783952), band("peak", 3500, 2, 1.0046157902783952), band("highShelf", 10000, 1)];
const mine = [band("peak", 250, -3.5, 1.4), band("highShelf", 9000, 2)];
const limits = {bands: 4, minBands: 1, maxBands: 16, types: ["lowCut", "lowShelf", "peak", "notch", "highShelf", "highCut"],
  frequency: {min: 20, max: 20000, step: 1}, gain: {min: -12, max: 12, step: 0.25}, q: {min: 0.1, max: 10, log10Step: 0.016}};
const eqOff = {enabled: false, bands: [band("lowCut", 36), band("peak", 310), band("peak", 2172), band("highCut", 15049, 0, Q, false)]};

const state = (more = {}) => ({
  eqProfiles: [
    {id: "builtin:clear", name: "Clear", builtin: true, bands: clear},
    {id: "builtin:warm", name: "Warm", builtin: true, bands: clear},
    {id: "builtin:loud", name: "Loud", builtin: true, bands: clear},
    {id: "saved:My voice", name: "My voice", builtin: false, bands: mine},
  ],
  mixer: {
    eqLimits: limits,
    mixes: [{id: "monitor", name: "Monitor A", kind: "monitor"}],
    channels: [
      {id: "xlr1", name: "Main mic", microphone: true, hardware: true, appearance: {icon: "mic", colour: "#12ab34"}, eq: eqOff},
      {id: "guest", name: "Guest", microphone: true, eq: eqOff},
      {id: "xlr2", name: "XLR 2", present: false, eq: eqOff},
      {id: "music", name: "Music", microphone: false},
    ],
  },
  ...more,
});

test("an EQ profile key names a microphone and the daemon's profile id", () => {
  const s = state();
  assert.ok(isEqProfileTarget("eqprofile:xlr1:builtin:clear"));
  assert.ok(!isEqProfileTarget("eq:xlr1"));
  const entry = eqProfileTarget(s, "eqprofile:xlr1:builtin:clear");
  assert.equal(entry.channel.id, "xlr1");
  assert.equal(entry.profile.id, "builtin:clear");
  assert.equal(entry.lit, false);
  assert.equal(eqProfileLabel(entry), "Main mic\nClear");
  // A saved profile is found by its name whatever its case, as the daemon finds it.
  assert.equal(eqProfileTarget(s, "eqprofile:guest:saved:my VOICE").profile.id, "saved:My voice");
  assert.equal(eqProfileLabel(eqProfileTarget(s, "eqprofile:guest:saved:My voice")), "Guest\nMy voice");
  // Malformed, absent, without an EQ or a profile the daemon does not list: nothing.
  for (const target of ["eqprofile:xlr1", "eqprofile:xlr1:clear", "eqprofile::builtin:clear", "eqprofile:a b:builtin:clear",
    "eqprofile:xlr2:builtin:clear", "eqprofile:music:builtin:clear", "eqprofile:xlr1:builtin:broadcast", "eqprofile:xlr1:saved:Gone",
    "eqprofile:xlr1:saved:"])
    assert.equal(eqProfileTarget(s, target), null, target);
  assert.equal(eqProfileLabel(null, "eqprofile:xlr1:builtin:warm"), "EQ profile\nWarm");
  assert.equal(eqProfileLabel(null, "eqprofile:xlr1:saved:Gone"), "EQ profile\nGone");
  assert.equal(eqProfileLabel(null, "nonsense"), "EQ profile");
  // A daemon without EQ profiles: nothing to load.
  assert.equal(eqProfileTarget(state({eqProfiles: undefined}), "eqprofile:xlr1:builtin:clear"), null);
  // The key wears its microphone's colour.
  assert.equal(targetAppearance(s.mixer, "eqprofile:xlr1:builtin:clear").colour, "#12ab34");
});

test("built-in profiles are named in the plugin's language, an unknown one by the daemon", () => {
  const [builtin, warm, loud, saved] = state().eqProfiles;
  assert.equal(eqProfileName(builtin), "Clear");
  assert.equal(eqProfileName(warm), "Warm");
  assert.equal(eqProfileName(loud), "Loud");
  assert.equal(eqProfileName(saved), "My voice");
});

test("a press is one loadEqProfile, refused for a band count the microphone does not take", () => {
  const s = state();
  assert.deepEqual(eqProfilePress(eqProfileTarget(s, "eqprofile:xlr1:builtin:clear")),
    {cmd: "loadEqProfile", channel: "xlr1", profile: "builtin:clear"});
  assert.deepEqual(eqProfilePress(eqProfileTarget(s, "eqprofile:guest:saved:my voice")),
    {cmd: "loadEqProfile", channel: "guest", profile: "saved:My voice"});
  assert.equal(eqProfilePress(null), null);
  // An older daemon's EQ takes four bands and no other count.
  const fixed = state();
  fixed.mixer.eqLimits = {...limits, minBands: undefined, maxBands: undefined};
  assert.equal(eqProfileTarget(fixed, "eqprofile:xlr1:saved:My voice").fits, false);
  assert.equal(eqProfilePress(eqProfileTarget(fixed, "eqprofile:xlr1:saved:My voice")), null);
  assert.ok(eqProfilePress(eqProfileTarget(fixed, "eqprofile:xlr1:builtin:clear")));
});

test("the key is lit while the microphone's EQ is on and holds the profile's bands", () => {
  const s = state();
  const lit = () => eqProfileTarget(s, "eqprofile:xlr1:builtin:clear").lit;
  s.mixer.channels[0].eq = {enabled: true, bands: clear.map(b => ({...b}))};
  assert.equal(lit(), true);
  // Values as they came back through the daemon, within half a step.
  s.mixer.channels[0].eq.bands[1] = {...clear[1], frequency: 300.4, gain: -2.1, q: clear[1].q * 1.01};
  assert.equal(lit(), true);
  // Off, a band moved by a step, a band switched, another type or count: not lit.
  for (const change of [
    eq => { eq.enabled = false; },
    eq => { eq.bands[0] = {...eq.bands[0], frequency: 81}; },
    eq => { eq.bands[2] = {...eq.bands[2], gain: 2.25}; },
    eq => { eq.bands[3] = {...eq.bands[3], enabled: false}; },
    eq => { eq.bands[3] = {...eq.bands[3], type: "lowShelf"}; },
    eq => { eq.bands[1] = {...eq.bands[1], q: eq.bands[1].q * 1.04}; },
    eq => { eq.bands.pop(); },
  ]) {
    s.mixer.channels[0].eq = {enabled: true, bands: clear.map(b => ({...b}))};
    change(s.mixer.channels[0].eq);
    assert.equal(lit(), false);
  }
  assert.equal(eqHolds(null, {bands: clear}, limits), false);
});

test("the inspector offers each profile for each microphone with an EQ, built-in ones marked", () => {
  const s = state();
  const groups = eqProfileChoices(s).toggleGroups;
  assert.deepEqual(groups.map(g => g.id), ["layout-eq-profiles-xlr1", "layout-eq-profiles-guest"]);
  assert.equal(groups[0].label, "Load EQ profile: Main mic");
  assert.deepEqual(groups[0].items.map(i => i.target),
    ["eqprofile:xlr1:builtin:clear", "eqprofile:xlr1:builtin:warm", "eqprofile:xlr1:builtin:loud", "eqprofile:xlr1:saved:My voice"]);
  assert.deepEqual(groups[1].items.map(i => i.label),
    ["Guest EQ: Clear (built in)", "Guest EQ: Warm (built in)", "Guest EQ: Loud (built in)", "Guest EQ: My voice"]);
  assert.ok(layoutChoices(s.mixer, [], s.eqProfiles).toggleGroups.some(group => group.id === "layout-eq-profiles-guest"));
  // A daemon without EQ profiles offers none.
  assert.deepEqual(eqProfileChoices(state({eqProfiles: undefined})).toggleGroups, []);
  assert.ok(!layoutChoices(s.mixer).toggleGroups.some(group => group.id.startsWith("layout-eq-profiles-")));
  // A profile that is not one is left out.
  const odd = state();
  odd.eqProfiles.push({id: "saved:Broken", name: "Broken", builtin: false, bands: []}, {id: "Clear", name: "Clear", builtin: true, bands: clear});
  assert.equal(eqProfileChoices(odd).toggleGroups[0].items.length, 4);
});
