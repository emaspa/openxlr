import assert from "node:assert/strict";
import test from "node:test";
import {eqTarget, eqChoices, eqCommand} from "../com.emaspa.openxlr.sdPlugin/microphone-eq.mjs";
import {eqProfileTarget, eqProfilePress, eqProfileChoices} from "../com.emaspa.openxlr.sdPlugin/eq-profiles.mjs";
import {eqMixer} from "./microphone-eq-fixture.mjs";

// XLR 1 beside a capture input on a stereo pair, which the daemon reports
// with an EQ and no noise removal, and an application channel with neither.
function mixer() {
  const m = eqMixer();
  const eq = structuredClone(m.channels[0].eq);
  m.channels.push({id: "camera", name: "Camera", microphone: false, chainWidth: 2, captureSource: "usb_camera", eq},
    {id: "music", name: "Music", microphone: false, captureSource: null});
  return m;
}

test("a stereo capture input's EQ takes the microphone EQ's targets and its commands name the channel", () => {
  const m = mixer();
  const on = eqTarget(m, "eq:camera");
  assert.equal(on.channel.id, "camera");
  assert.equal(on.mix, false);
  assert.equal(on.meter, "ch:camera");
  assert.deepEqual(eqCommand(on, true), {cmd: "setEqEnabled", channel: "camera", value: true});
  assert.deepEqual(eqCommand(eqTarget(m, "eqgain:camera:2"), 3), {cmd: "setEqBand", channel: "camera", band: 2, gain: 3});
  // It has no noise removal, and an application channel has no EQ.
  assert.equal(eqTarget(m, "noise:camera"), null);
  assert.equal(eqTarget(m, "eq:music"), null);
  // An absent capture input offers nothing until it returns.
  m.channels.find(c => c.id === "camera").present = false;
  assert.equal(eqTarget(m, "eq:camera"), null);
});

test("the inspector names a stereo capture input's EQ as an input EQ, with no noise switch", () => {
  const {toggleGroups, levelGroups} = eqChoices(mixer());
  const keys = toggleGroups.find(g => g.id === "layout-eq-keys-camera");
  assert.equal(keys.label, "Input EQ: Camera");
  assert.ok(!keys.items.some(item => item.target.startsWith("noise:")));
  assert.equal(levelGroups.find(g => g.id === "layout-eq-dials-camera").label, "Input EQ: Camera");
  assert.equal(toggleGroups.find(g => g.id === "layout-eq-keys-xlr1").label, "Microphone EQ: Main mic");
  assert.ok(!toggleGroups.some(g => g.id === "layout-eq-keys-music"));
});

test("an EQ profile key loads onto a stereo capture input", () => {
  const m = mixer();
  const bands = m.channels[0].eq.bands;
  const state = {mixer: m, eqProfiles: [{id: "builtin:clear", name: "Clear", builtin: true, bands}]};
  const entry = eqProfileTarget(state, "eqprofile:camera:builtin:clear");
  assert.equal(entry.channel.id, "camera");
  assert.deepEqual(eqProfilePress(entry), {cmd: "loadEqProfile", channel: "camera", profile: "builtin:clear"});
  assert.ok(eqProfileChoices(state).toggleGroups.some(g => g.id === "layout-eq-profiles-camera"));
  assert.equal(eqProfileTarget(state, "eqprofile:music:builtin:clear"), null);
});
