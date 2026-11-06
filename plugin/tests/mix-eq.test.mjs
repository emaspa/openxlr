import assert from "node:assert/strict";
import test from "node:test";
import {eqTarget, eqChoices, eqCommand, eqDial, eqLabel} from "../com.emaspa.openxlr.sdPlugin/microphone-eq.mjs";
import {eqProfileTarget, eqProfilePress, eqProfileLabel, eqProfileChoices} from "../com.emaspa.openxlr.sdPlugin/eq-profiles.mjs";
import {eqMixer} from "./microphone-eq-fixture.mjs";

// The default layout's Monitor A and Stream with an output EQ each, beside
// XLR 1's microphone EQ.
function mixer() {
  const m = eqMixer();
  const eq = structuredClone(m.channels[0].eq);
  m.mixes = [{id: "monitor", name: "Monitor A", kind: "monitor", eq: structuredClone(eq)},
    {id: "stream", name: "Stream", kind: "virtualMic", eq: structuredClone(eq)},
    {id: "chat", name: "Chat", kind: "virtualMic"}];
  return m;
}

const held = m => {
  m.hardwareMixing = {wanted: true, active: true,
    disabled: {"eq:mix:monitor": "The Wave XLR Pro builds Monitor A for its jacks, so its EQ is off while hardware mixing runs."}};
  return m;
};

test("a mix's EQ takes the microphone EQ's targets with mix before its id, and its commands name the mix", () => {
  const m = mixer();
  const on = eqTarget(m, "eq:mix:stream");
  assert.equal(on.channel.id, "stream");
  assert.equal(on.mix, true);
  assert.equal(on.meter, "mix:stream");
  assert.deepEqual(eqCommand(on, true), {cmd: "setEqEnabled", mix: "stream", value: true});
  assert.deepEqual(eqCommand(eqTarget(m, "eqband:mix:stream:2"), false), {cmd: "setEqBand", mix: "stream", band: 2, enabled: false});
  assert.deepEqual(eqCommand(eqTarget(m, "eqgain:mix:monitor:3"), -3), {cmd: "setEqBand", mix: "monitor", band: 3, gain: -3});
  assert.equal(eqLabel(on), "Stream\nEQ");
  assert.equal(eqDial(eqTarget(m, "eqgain:mix:stream:2"), 3).pin, "Stream");
  // A mix has no noise removal, and a mix without an EQ has no targets.
  assert.equal(eqTarget(m, "noise:mix:stream"), null);
  assert.equal(eqTarget(m, "eq:mix:chat"), null);
  assert.equal(eqTarget(m, "eq:mix:gone"), null);
  // A microphone's targets are as they were.
  assert.deepEqual(eqCommand(eqTarget(m, "eq:xlr1"), true), {cmd: "setEqEnabled", channel: "xlr1", value: true});
});

test("a channel called mix keeps its own targets beside the mixes'", () => {
  const m = mixer();
  m.channels.push({...structuredClone(m.channels[0]), id: "mix", name: "Mix mic"});
  assert.equal(eqTarget(m, "eq:mix").channel.name, "Mix mic");
  assert.equal(eqTarget(m, "eqgain:mix:2").channel.name, "Mix mic");
  assert.equal(eqTarget(m, "eqgain:mix:stream:2").channel.name, "Stream");
});

test("the inspector offers each mix's EQ under Output EQ", () => {
  const groups = eqChoices(mixer()).toggleGroups.map(g => g.label);
  assert.deepEqual(groups, ["Microphone EQ: Main mic", "Output EQ: Monitor A", "Output EQ: Stream"]);
  const dials = eqChoices(mixer()).levelGroups.find(g => g.label === "Output EQ: Stream");
  assert.ok(dials.items.some(item => item.target === "eqfreq:mix:stream:4"));
});

test("while hardware mixing holds a mix's EQ off its keys send nothing and say so", () => {
  const m = held(mixer());
  const monitor = eqTarget(m, "eq:mix:monitor");
  assert.equal(monitor.held, m.hardwareMixing.disabled["eq:mix:monitor"]);
  assert.equal(monitor.available, false);
  assert.equal(eqCommand(monitor, true), null);
  assert.equal(eqCommand(eqTarget(m, "eqgain:mix:monitor:2"), 3), null);
  assert.match(eqDial(eqTarget(m, "eqgain:mix:monitor:2"), 3).scroll, /held by the unit/);
  assert.deepEqual(eqCommand(eqTarget(m, "eq:mix:stream"), true), {cmd: "setEqEnabled", mix: "stream", value: true});
});

const profiles = [
  {id: "builtin:clear", name: "Clear", builtin: true, bands: mixer().channels[0].eq.bands},
  {id: "builtin:soft-treble", name: "Soft treble", builtin: true,
    bands: [{enabled: true, type: "highShelf", frequency: 4500, gain: -4, q: 0.7046930689671475}]},
];

test("an EQ profile key loads on a mix, the listening curves and the voice curves alike", () => {
  const m = mixer();
  m.eqLimits = {...m.eqLimits, minBands: 1, maxBands: 16};
  const state = {mixer: m, eqProfiles: profiles};
  const treble = eqProfileTarget(state, "eqprofile:mix:stream:builtin:soft-treble");
  assert.equal(treble.channel.id, "stream");
  assert.deepEqual(eqProfilePress(treble), {cmd: "loadEqProfile", mix: "stream", profile: "builtin:soft-treble"});
  assert.deepEqual(eqProfilePress(eqProfileTarget(state, "eqprofile:mix:monitor:builtin:clear")),
    {cmd: "loadEqProfile", mix: "monitor", profile: "builtin:clear"});
  assert.deepEqual(eqProfilePress(eqProfileTarget(state, "eqprofile:xlr1:builtin:soft-treble")),
    {cmd: "loadEqProfile", channel: "xlr1", profile: "builtin:soft-treble"});
  assert.equal(eqProfileLabel(treble), "Stream\nSoft treble");
  assert.equal(eqProfileLabel(null, "eqprofile:mix:stream:builtin:footsteps"), "EQ profile\nFootsteps");
  const groups = eqProfileChoices(state).toggleGroups;
  assert.deepEqual(groups.map(g => g.label), ["Load EQ profile: Main mic", "Load EQ profile: Monitor A", "Load EQ profile: Stream"]);
  assert.deepEqual(groups[2].items.map(i => i.target), ["eqprofile:mix:stream:builtin:clear", "eqprofile:mix:stream:builtin:soft-treble"]);
  // Held off by hardware mixing: the key sends nothing.
  assert.equal(eqProfilePress(eqProfileTarget({mixer: held(m), eqProfiles: profiles}, "eqprofile:mix:monitor:builtin:clear")), null);
});
