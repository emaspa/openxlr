import assert from "node:assert/strict";
import test from "node:test";
import {controlFixture} from "./control-fixture.mjs";
import {eqMixer} from "./microphone-eq-fixture.mjs";

test("each dial applies its step in the control's units and keeps every tick", async t => {
  t.mock.timers.enable({apis: ["Date", "setTimeout"]});
  const previous = {socket: globalThis.WebSocket, interval: globalThis.setInterval, skin: process.env.OPENXLR_SKIN};
  const sockets = [], intervals = [];
  process.env.OPENXLR_SKIN = "default";
  globalThis.setInterval = (...args) => { const timer = previous.interval(...args); intervals.push(timer); return timer; };
  class Socket {
    static OPEN = 1;
    readyState = 1;
    messages = [];
    constructor() { sockets.push(this); }
    send(text) { this.messages.push(JSON.parse(text)); }
    receive(message) { this.onmessage({data: JSON.stringify(message)}); }
  }
  globalThis.WebSocket = Socket;
  const control = controlFixture();
  try {
    await import("../com.emaspa.openxlr.sdPlugin/plugin.mjs");
    const daemon = control.children[0];
    const host = sockets[0];
    daemon.onopen();
    const makeState = () => ({type: "state", connected: true, profiles: [],
      capabilities: {gain: true, gainMaxDb: 75, xlrInputs: 2, hpVolume: true, hpOutputs: 2,
        auxInput: true, lineInput: true, crossfade: true, lineInGainMaxDb: 40},
      state: {gainDb: 40, gain2Db: 30, hpVolumeDb: -12, hp2VolumeDb: -24, auxLevelDb: -15, lineInGainDb: 20, crossfade: 100},
      waveInterfaces: [{id: "extra", enabled: true, active: false, connected: true,
        capabilities: {gain: true, gainMaxDb: 40, xlrInputs: 2}, state: {gain2Db: 12}}],
      devices: [{name: "desk", description: "Desk speakers", kind: 0, volume: .5}],
      mixer: {...eqMixer(), outputVolume: .8,
        channels: [
          {...eqMixer().channels[0], unitInput: {unit: "primary", input: 1, primary: true}},
          {id: "guest", name: "Guest", unitInput: {unit: "extra", input: 2, primary: false}},
          {id: "capture", name: "Capture", captureGain: {gainDb: 5, minDb: -10, maxDb: 10}},
          {id: "music", name: "Music", volume: .5, levels: {monitor: .5, chat: .5}, mutedIn: []},
        ],
        mixes: [{id: "monitor", name: "Monitor A", kind: "monitor", volume: 1.2},
          {id: "chat", name: "Chat", kind: "virtualMic", volume: .5}],
      }});
    let state;
    const reset = () => {
      t.mock.timers.tick(1000);
      state = makeState(); daemon.receive(state); daemon.messages.length = 0;
    };
    const appear = (target, stepSize, context = "dial", more = {}) => host.receive({event: "willAppear", context,
      action: "com.emaspa.openxlr.dial", payload: {settings: {target, stepSize, ...more}}});
    const rotate = (ticks, context = "dial") => host.receive({event: "dialRotate", context, payload: {ticks}});
    const feedback = context => host.messages.filter(m => m.event === "setFeedback" && m.context === context && m.payload.title).at(-1).payload;
    const set = (control, value) => ({cmd: "set", control, value});

    await t.test("gain, hardware volumes, channel controls and faders use their native units", () => {
      for (const [target, step, ticks, expected] of [
        ["gain", 3, 1, set("gain", 43)],
        ["gain2", 5, -2, set("gain2", 20)],
        ["chgain:xlr1", 10, 2, {cmd: "setWaveControl", device: "primary", control: "gain", value: 60}],
        ["chgain:guest", 3, -1, {cmd: "setWaveControl", device: "extra", control: "gain2", value: 9}],
        ["chgain:capture", 3, -2, {cmd: "setCaptureGain", channel: "capture", value: -1}],
        ["hp", 3, 2, set("hpVolumeDb", -6)],
        ["hp2", 5, -1, set("hp2VolumeDb", -29)],
        ["auxLevel", 10, 1, set("auxLevelDb", -5)],
        ["lineIn", 5, 2, set("lineInGain", 30)],
        ["send:music:monitor", 3, 2, {cmd: "setLevel", channel: "music", mix: "monitor", value: .56}],
        ["chvol:music", 5, 3, {cmd: "setChannelVolume", channel: "music", value: .65}],
        ["mixvol:monitor", 10, 1, {cmd: "setMixVolume", mix: "monitor", value: 1.3}],
        ["mixvol:chat", 3, -1, {cmd: "setMixVolume", mix: "chat", value: .47}],
        ["outputVolume", 3, 1, {cmd: "setOutputVolume", value: .83}],
        ["output:desk", 10, -2, {cmd: "setOutputDeviceVolume", device: "desk", value: .3}],
        ["crossfade", 3, -2, set("crossfade", 94)],
      ]) {
        reset(); appear(target, step); rotate(ticks);
        assert.deepEqual(daemon.messages, [expected], target);
      }
      reset(); appear("send:music:all", 10); rotate(-2);
      assert.deepEqual(daemon.messages, ["monitor", "chat"].map(mix =>
        ({cmd: "setLevel", channel: "music", mix, value: .3})));
    });

    await t.test("old actions and malformed settings retain the original step", () => {
      for (const step of [undefined, null, "", 0, 2, -3, 1.5, "3", true, [], {}, NaN, Infinity])
        for (const [target, expected] of [
          ["gain", set("gain", 41)], ["hp", set("hpVolumeDb", -11.4)],
          ["crossfade", set("crossfade", 105)], ["chvol:music", {cmd: "setChannelVolume", channel: "music", value: .51}],
        ]) {
          reset(); appear(target, step); rotate(1);
          assert.deepEqual(daemon.messages, [expected], `${target}: ${JSON.stringify(step)}`);
        }
      reset(); appear("hp", 1); rotate(1);
      assert.deepEqual(daemon.messages, [set("hpVolumeDb", -11)]);
    });

    await t.test("large turns clamp to every reported range without numeric drift", () => {
      for (const [target, ticks, expected] of [
        ["gain", 10000, 75], ["gain", -10000, 0], ["lineIn", 10000, 40],
        ["chgain:guest", 10000, 40], ["chgain:capture", 10000, 10], ["chgain:capture", -10000, -10],
        ["hp", Number.MAX_SAFE_INTEGER, 0], ["hp", -Number.MAX_SAFE_INTEGER, -60],
        ["auxLevel", -10000, -60], ["crossfade", 10000, 200], ["crossfade", -10000, 0],
        ["mixvol:monitor", 10000, 1.5], ["mixvol:chat", 10000, 1], ["chvol:music", -10000, 0],
        ["outputVolume", 10000, 1.5], ["output:desk", -10000, 0],
      ]) {
        reset(); appear(target, 10); rotate(ticks);
        assert.equal(daemon.messages.at(-1)?.value, expected, target);
      }
      reset(); appear("hp", 3);
      for (let i = 0; i < 3; ++i) rotate(-1);
      assert.equal(feedback("dial").value, "65%");
      t.mock.timers.tick(81);
      assert.deepEqual(daemon.messages.at(-1), set("hpVolumeDb", -21));
    });

    await t.test("coalescing keeps fast ticks, settings changes and independent dial steps", () => {
      reset(); appear("gain", 3); rotate(1); rotate(1);
      host.receive({event: "didReceiveSettings", context: "dial", payload: {settings: {target: "gain", stepSize: 5}}});
      rotate(1);
      state.state.gainDb = 43; daemon.receive(state);
      assert.equal(Number.parseFloat(feedback("dial").value), 51, "a stale echo must not undo the turn");
      t.mock.timers.tick(81);
      assert.deepEqual(daemon.messages, [set("gain", 43), set("gain", 51)]);

      reset(); appear("gain", 3, "fine"); appear("gain", 10, "coarse");
      rotate(1, "fine"); rotate(1, "coarse"); rotate(1, "fine");
      t.mock.timers.tick(81);
      assert.deepEqual(daemon.messages, [set("gain", 43), set("gain", 56)]);
      host.receive({event: "willDisappear", context: "fine"});
      host.receive({event: "willDisappear", context: "coarse"});
    });

    await t.test("stack cycling applies the same saved step to the newly selected control", () => {
      reset(); appear("gain", 5, "stack", {targets: ["gain", "hp"], activeIndex: 0, cycleGesture: "tap"});
      rotate(1, "stack");
      host.receive({event: "touchTap", context: "stack"});
      const saved = host.messages.filter(m => m.event === "setSettings" && m.context === "stack").at(-1).payload;
      assert.equal(saved.stepSize, 5); assert.equal(saved.activeIndex, 1);
      rotate(1, "stack");
      assert.deepEqual(daemon.messages, [set("gain", 45), set("hpVolumeDb", -7)]);
      host.receive({event: "willDisappear", context: "stack"});
    });

    await t.test("EQ and insert parameters retain their own steps; invalid ticks send nothing", () => {
      reset(); appear("eqgain:xlr1:2", 10); rotate(1);
      assert.deepEqual(daemon.messages, [{cmd: "setEqBand", channel: "xlr1", band: 2, gain: .25}]);
      reset();
      state.mixer.inserts = {xlr1: [{id: "fx", plugin: "fixture", params: {drive: 2}}]};
      daemon.receive(state);
      daemon.receive({type: "plugins", plugins: [{plugin: "fixture", name: "Fixture",
        params: [{symbol: "drive", name: "Drive", min: 0, max: 10, default: 2}]}]});
      appear("insparam|xlr1|fx|drive", 10); rotate(1);
      assert.deepEqual(daemon.messages, [{cmd: "setInsertParam", channel: "xlr1", insertId: "fx", symbol: "drive", value: 2.1}]);
      for (const target of ["gain", "hp", "chvol:music", "eqgain:xlr1:2"])
        for (const ticks of [0, undefined, null, false, "2", .5, NaN, Infinity, -Infinity, Number.MAX_SAFE_INTEGER + 1]) {
          reset(); appear(target, 10); rotate(ticks);
          assert.deepEqual(daemon.messages, [], `${target}: ${JSON.stringify(ticks)}`);
        }
    });


  } finally {
    control.restore();
    for (const timer of intervals) clearInterval(timer);
    globalThis.WebSocket = previous.socket; globalThis.setInterval = previous.interval;
    if (previous.skin === undefined) delete process.env.OPENXLR_SKIN; else process.env.OPENXLR_SKIN = previous.skin;
  }
});
