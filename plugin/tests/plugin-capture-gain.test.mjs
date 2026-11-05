import assert from "node:assert/strict";
import test from "node:test";
import {controlFixture} from "./control-fixture.mjs";
import { layoutChoices } from "../com.emaspa.openxlr.sdPlugin/layout-choices.mjs";
import { captureGainOf, captureMuteOf, unitMeterKey } from "../com.emaspa.openxlr.sdPlugin/unit-inputs.mjs";

const gain = {gainDb: 36, minDb: 18, maxDb: 54, hardware: true};

test("a capture input's own gain is offered on a gain dial, and a unit's input keeps the unit's gain", () => {
  const mixer = {channels: [
    {id: "brio", name: "Brio", captureSource: "alsa_input.usb-webcam", captureGain: gain, levels: {monitor: 1}, mutedIn: []},
    {id: "dock", name: "Dock mic", captureSource: "alsa_input.usb-Elgato_dock", unitInput: {unit: "0fd9:0070", input: 1, primary: true}},
    {id: "music", name: "Music"},
  ], mixes: [{id: "monitor", name: "Monitor A", kind: "monitor"}]};
  const items = layoutChoices(mixer).levelGroups.find(g => g.id === "layout-microphone-gains").items;
  assert.deepEqual(items.map(i => i.target), ["chgain:brio", "chgain:dock"]);
  // The unit's switches stay with the unit's input alone.
  assert.deepEqual(layoutChoices(mixer).toggleGroups.find(g => g.id === "layout-microphone-controls").items
    .map(i => i.target.split(":")[1]).filter((v, i, a) => a.indexOf(v) === i), ["dock"]);

  const state = {mixer};
  assert.deepEqual(captureGainOf(state, "brio"), {value: 36, min: 18, max: 54, hardware: true, muted: null});
  // An older daemon reports no source mute, so no mute key is offered for it.
  assert.equal(captureMuteOf(state, "brio"), null);
  assert.equal(captureGainOf(state, "dock"), null);
  assert.equal(captureGainOf(state, "music"), null);
  // A gain beside a unit's input, a broken range or missing numbers are not a gain.
  for (const bad of [{...gain, maxDb: 18}, {...gain, gainDb: null}, "36", null])
    assert.equal(captureGainOf({mixer: {channels: [{id: "x", captureGain: bad}]}}, "x"), null);
  assert.equal(captureGainOf({mixer: {channels: [{id: "x", captureGain: gain, unitInput: {unit: "u", input: 1}}]}}, "x"), null);
  assert.equal(unitMeterKey(mixer, "chgain:brio"), "ch:brio");
  assert.equal(unitMeterKey(mixer, "chgain:music"), null);
});

test("a capture input's source mute is offered on the microphone mute key", () => {
  const mixer = {channels: [
    {id: "brio", name: "Brio", captureSource: "alsa_input.usb-webcam", captureGain: {...gain, muted: true}},
    {id: "dock", name: "Dock mic", captureSource: "alsa_input.usb-Elgato_dock", unitInput: {unit: "0fd9:0070", input: 1, primary: true},
     captureGain: {...gain, muted: false}},
    {id: "music", name: "Music"},
  ], mixes: [{id: "monitor", name: "Monitor A", kind: "monitor"}]};
  // The capture input gets the mute alone; the unit's input keeps all of its switches.
  assert.deepEqual(layoutChoices(mixer).toggleGroups.find(g => g.id === "layout-microphone-controls").items.map(i => i.target),
    ["chmute:brio", "chmute:dock", "chphantom:dock", "chlowcut:dock", "chclipguard:dock"]);
  const state = {mixer};
  assert.equal(captureGainOf(state, "brio").muted, true);
  assert.equal(captureMuteOf(state, "brio"), true);
  // A unit's input has the unit's mute, never the source's.
  assert.equal(captureMuteOf(state, "dock"), null);
  assert.equal(captureMuteOf(state, "music"), null);
  for (const bad of ["true", 1, null])
    assert.equal(captureMuteOf({mixer: {channels: [{id: "x", captureGain: {...gain, muted: bad}}]}}, "x"), null);
});

test("the gain dial turns a capture input's own gain within its range and a press mutes the channel", async () => {
  const oldSkin = process.env.OPENXLR_SKIN;
  process.env.OPENXLR_SKIN = "default";
  const previous = globalThis.WebSocket;
  const previousInterval = globalThis.setInterval;
  const intervals = [];
  globalThis.setInterval = (...args) => {
    const interval = previousInterval(...args);
    intervals.push(interval);
    return interval;
  };
  const sockets = [];
  class Socket {
    static OPEN = 1;
    readyState = 1;
    messages = [];
    constructor(url) { this.url = url; sockets.push(this); }
    send(text) { this.messages.push(JSON.parse(text)); }
    receive(message) { this.onmessage({data: JSON.stringify(message)}); }
  }
  globalThis.WebSocket = Socket;
  const svg = (uri) => Buffer.from(uri.split(",")[1], "base64").toString();
  const control = controlFixture();
  try {
    await import("../com.emaspa.openxlr.sdPlugin/plugin.mjs");
    const daemon = control.children[0];
    const host = sockets[0];
    daemon.onopen();
    const state = {type: "state", connected: true, profiles: [],
      capabilities: {gain: true, mute: true, xlrInputs: 1, gainMaxDb: 80},
      state: {gainDb: 40, mute: false},
      devices: [],
      mixer: {
        channels: [
          {id: "xlr1", name: "XLR 1", hardware: true, levels: {monitor: 1, stream: 1}, mutedIn: []},
          {id: "brio", name: "Brio", captureSource: "alsa_input.usb-webcam", captureGain: {...gain, gainDb: 53},
           levels: {monitor: 1, stream: 1}, mutedIn: ["monitor"], volume: 1},
        ],
        mixes: [{id: "monitor", name: "Monitor A", kind: "monitor", volume: 1}, {id: "stream", name: "Stream", kind: "virtualMic", volume: 1}],
        monitorOutputs: [], monitorFeeds: {}, inserts: {}, lowCutHz: 0,
      }};
    daemon.receive(state);
    const feedback = (context) =>
      host.messages.filter(m => m.event === "setFeedback" && m.context === context && m.payload.title).at(-1).payload;
    const meter = (context) =>
      host.messages.filter(m => m.event === "setFeedback" && m.context === context && m.payload.meter).at(-1)?.payload.meter;
    const alerted = (context) => host.messages.filter(m => m.event === "showAlert" && m.context === context).length;

    host.receive({event: "willAppear", context: "brio-dial", action: "com.emaspa.openxlr.dial", payload: {settings: {target: "chgain:brio"}}});
    const face = feedback("brio-dial");
    assert.equal(face.value, "53");
    assert.ok(svg(face.title).includes("Brio gain"));

    // A turn past the top stops at the device's highest gain, sent as the capture input's own gain.
    host.receive({event: "dialRotate", context: "brio-dial", payload: {ticks: 3}});
    assert.deepEqual(daemon.messages.at(-1), {cmd: "setCaptureGain", channel: "brio", value: 54});
    assert.equal(feedback("brio-dial").value, "54");
    assert.ok(!daemon.messages.some(m => m.cmd === "setWaveControl" || m.cmd === "set"));

    // A press mutes the channel in every mix; the source's own mute is left alone.
    const before = daemon.messages.length;
    host.receive({event: "dialDown", context: "brio-dial"});
    const muted = daemon.messages.slice(before);
    assert.deepEqual(muted.map(m => m.cmd), ["setChannelMuted", "setChannelMuted"]);
    assert.ok(muted.every(m => m.channel === "brio" && m.value === true));

    // It meters the capture input's channel.
    daemon.receive({type: "meters", levels: {"ch:xlr1": [0, 0], "ch:brio": [0.8, 0.8]}});
    assert.match(svg(meter("brio-dial")), /<rect width="104" height="6"/);

    // A daemon that reports the source's mute: the dial shows it and a press flips it.
    state.mixer.channels[1].captureGain = {...gain, gainDb: 53, muted: true};
    state.mixer.channels[1].mutedIn = [];
    daemon.receive(state);
    assert.equal(feedback("brio-dial").value, "MUTED");
    const pressed = daemon.messages.length;
    host.receive({event: "dialDown", context: "brio-dial"});
    assert.deepEqual(daemon.messages.slice(pressed), [{cmd: "toggleCaptureMuted", channel: "brio"}]);

    // The microphone mute key on the capture input: lit while the source is muted, a press sends one toggle.
    host.receive({event: "willAppear", context: "brio-key", action: "com.emaspa.openxlr.toggle", payload: {settings: {target: "chmute:brio"}}});
    const keyed = daemon.messages.length;
    host.receive({event: "keyDown", context: "brio-key"});
    const toggle = daemon.messages.slice(keyed);
    assert.equal(toggle.length, 1);
    assert.deepEqual({...toggle[0], requestId: undefined}, {cmd: "toggleCaptureMuted", channel: "brio", requestId: undefined});
    assert.equal(typeof toggle[0].requestId, "string");
    daemon.receive({type: "commandResult", requestId: toggle[0].requestId, error: null});
    // An older daemon again: the press falls back to the channel's sends, and the key has nothing to flip.
    state.mixer.channels[1].captureGain = {...gain, gainDb: 53};
    daemon.receive(state);
    const fallback = daemon.messages.length;
    host.receive({event: "dialDown", context: "brio-dial"});
    assert.deepEqual(daemon.messages.slice(fallback).map(m => m.cmd), ["setChannelMuted", "setChannelMuted"]);
    const idle = daemon.messages.length;
    host.receive({event: "keyDown", context: "brio-key"});
    assert.ok(!daemon.messages.slice(idle).some(m => m.cmd === "toggleCaptureMuted"));

    // A channel with no gain of its own: the dial says so and sends nothing.
    delete state.mixer.channels[1].captureGain;
    daemon.receive(state);
    const sent = daemon.messages.length;
    host.receive({event: "dialRotate", context: "brio-dial", payload: {ticks: 1}});
    assert.equal(daemon.messages.length, sent);
    assert.equal(alerted("brio-dial"), 1);
  } finally {
    control.restore();
    for (const interval of intervals) clearInterval(interval);
    globalThis.setInterval = previousInterval;
    globalThis.WebSocket = previous;
    if (oldSkin === undefined) delete process.env.OPENXLR_SKIN;
    else process.env.OPENXLR_SKIN = oldSkin;
  }
});
