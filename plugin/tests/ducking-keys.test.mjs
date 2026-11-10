import assert from "node:assert/strict";
import test from "node:test";
import { canKeyDucking, duckingKeyOn, duckingKeys, duckingKeyToggle, MAX_DUCKING_KEYS, withDucking }
  from "../com.emaspa.openxlr.sdPlugin/unit-inputs.mjs";
import { layoutChoices, targetAppearance } from "../com.emaspa.openxlr.sdPlugin/layout-choices.mjs";

// The mixer's channels as the daemon reports them: the unit's inputs, an
// application channel, a capture microphone and a stereo capture input.
const channels = [
  {id:"xlr1", name:"XLR 1", hardware:true, microphone:true},
  {id:"xlr2", name:"XLR 2", hardware:true, microphone:true},
  {id:"aux", name:"Aux In", hardware:true, microphone:false},
  {id:"line", name:"Line In", hardware:true, microphone:false},
  {id:"voicechat", name:"Voice Chat", microphone:false, appearance:{icon:"♫", colour:"#12abEF"}},
  {id:"music", name:"Music", microphone:false},
  {id:"cap1", name:"Guest mic", microphone:true, captureSource:"alsa_input.usb-Yeti", captureMonoChannel:0},
  {id:"cap2", name:"Console", microphone:false, captureSource:"alsa_input.usb-Console"},
];
const mixes = [{id:"monitor", name:"Monitor A"}];

const stateWith = (ducking) => ({mixer:{channels, mixes}, ducking:{enabled:true, channels:["music", "xlr2"], mixes:["monitor"], ...ducking}});

test("the keys come from the list, or from the one key of an older daemon", () => {
  assert.deepEqual(duckingKeys(stateWith({keys:["xlr1", "voicechat"], key:"xlr1"})), ["xlr1", "voicechat"]);
  assert.deepEqual(duckingKeys(stateWith({key:"cap1"})), ["cap1"]);
  assert.deepEqual(duckingKeys(stateWith({keys:[], key:"xlr2"})), ["xlr2"]);
  assert.deepEqual(duckingKeys(stateWith({})), ["xlr1"]);
  for (const state of [undefined, null, {}, {ducking:null}, {ducking:"on"}])
    assert.equal(duckingKeys(state), null);
});

test("microphones, the unit's inputs and application channels key ducking; a stereo capture does not", () => {
  assert.deepEqual(channels.filter(canKeyDucking).map(c => c.id), ["xlr1", "xlr2", "aux", "line", "voicechat", "music", "cap1"]);
  assert.equal(canKeyDucking(undefined), false);
  // An older daemon without the microphone field: the hardware strips still key.
  assert.equal(canKeyDucking({id:"xlr1", hardware:true}), true);
});

test("a key is lit while its channel is a key, and unavailable without ducking or once the channel is gone", () => {
  const state = stateWith({keys:["xlr1", "voicechat"], key:"xlr1"});
  assert.equal(duckingKeyOn(state, "xlr1"), true);
  assert.equal(duckingKeyOn(state, "voicechat"), true);
  assert.equal(duckingKeyOn(state, "aux"), false);
  assert.equal(duckingKeyOn(state, "cap2"), null);
  assert.equal(duckingKeyOn(state, "gone"), null);
  assert.equal(duckingKeyOn({mixer:{channels, mixes}}, "xlr1"), null);
});

test("a press adds the channel at the end, takes a ducked one out of the channels, and removes a key", () => {
  const state = stateWith({keys:["xlr1"], key:"xlr1"});
  // Aux In is not ducked: the keys alone.
  assert.deepEqual(duckingKeyToggle(state, "aux"), {cmd:"setDucking", value:{keys:["xlr1", "aux"]}});
  // XLR 2 is ducked, and a key is never ducked: it leaves the channels in the same command.
  assert.deepEqual(duckingKeyToggle(state, "xlr2"), {cmd:"setDucking", value:{keys:["xlr1", "xlr2"], channels:["music"]}});
  // The last key stays: nothing is sent.
  assert.equal(duckingKeyToggle(state, "xlr1"), null);
  // With two keys either can go.
  const two = stateWith({keys:["xlr1", "voicechat"], key:"xlr1"});
  assert.deepEqual(duckingKeyToggle(two, "xlr1"), {cmd:"setDucking", value:{keys:["voicechat"]}});
  // An older daemon's one key is the list the press starts from.
  assert.deepEqual(duckingKeyToggle(stateWith({key:"cap1"}), "xlr1"), {cmd:"setDucking", value:{keys:["cap1", "xlr1"]}});
  // A stereo capture input, a channel that is gone and no ducking send nothing.
  assert.equal(duckingKeyToggle(state, "cap2"), null);
  assert.equal(duckingKeyToggle(state, "gone"), null);
  assert.equal(duckingKeyToggle({mixer:{channels, mixes}}, "xlr1"), null);
});

test("two presses before the daemon answers keep both changes", () => {
  // A Multi Action, or two quick presses: the second builds on the first.
  let state = stateWith({keys:["xlr1"], key:"xlr1"});
  const first = duckingKeyToggle(state, "xlr2");
  state = withDucking(state, first.value);
  assert.deepEqual(duckingKeyToggle(state, "voicechat").value, {keys:["xlr1", "xlr2", "voicechat"]});
  assert.deepEqual(state.ducking.channels, ["music"]);
  // Taking the first key out leaves the list's head as the old single key.
  state = withDucking(state, {keys:["xlr2"]});
  assert.equal(state.ducking.key, "xlr2");
  assert.equal(duckingKeyOn(state, "xlr1"), false);
  // Without ducking in the state there is nothing to build on.
  assert.equal(withDucking(null, {keys:["xlr1"]}), null);
  const none = {mixer:{channels, mixes}};
  assert.equal(withDucking(none, {keys:["xlr1"]}), none);
});

test("a ninth key is not added", () => {
  assert.equal(MAX_DUCKING_KEYS, 8);
  const many = [...channels.filter(canKeyDucking).map(c => c.id), "a"];
  const state = {mixer:{channels:[...channels, {id:"a", name:"A"}, {id:"b", name:"B"}], mixes},
    ducking:{enabled:true, keys:many.slice(0, 8), key:"xlr1", channels:[], mixes:[]}};
  assert.equal(duckingKeyToggle(state, "b"), null);
  assert.deepEqual(duckingKeyToggle(state, "a").value.keys, many.slice(0, 7));
});

test("the inspector offers a ducking key per channel that can key, with the channel's look", () => {
  const group = layoutChoices({channels, mixes}).toggleGroups.find(g => g.id === "layout-ducking-keys");
  assert.equal(group.label, "Ducking keys");
  assert.deepEqual(group.items.map(item => item.target),
    ["duckkey:xlr1", "duckkey:xlr2", "duckkey:aux", "duckkey:line", "duckkey:voicechat", "duckkey:music", "duckkey:cap1"]);
  assert.equal(group.items.find(item => item.target === "duckkey:voicechat").label, "Voice Chat keys ducking");
  assert.deepEqual(targetAppearance({channels, mixes}, "duckkey:voicechat"), {icon:"♫", path:null, colour:"#12abEF"});
});
