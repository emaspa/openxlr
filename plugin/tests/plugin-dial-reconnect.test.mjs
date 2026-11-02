import assert from "node:assert/strict";
import test from "node:test";
import {controlFixture} from "./control-fixture.mjs";

test("a daemon reconnect discards every dial's pending send and warm value", async t => {
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
    const old = control.children[0], host = sockets[0];
    old.onopen();
    const state = {type: "state", connected: true, profiles: [], devices: [],
      capabilities: {gain: true, gainMaxDb: 80, xlrInputs: 1, hpVolume: true},
      state: {gainDb: 40, hpVolumeDb: -12}, mixer: {
        channels: [{id: "music", name: "Music", levels: {monitor: .5}, mutedIn: []}],
        mixes: [{id: "monitor", name: "Monitor", kind: "monitor"}], inserts: {},
      }};
    old.receive(state);
    for (const target of ["gain", "hp", "send:music:monitor"])
      host.receive({event: "willAppear", context: target, action: "com.emaspa.openxlr.dial", payload: {settings: {target}}});
    const rotate = context => host.receive({event: "dialRotate", context, payload: {ticks: 1}});
    for (const target of ["gain", "hp", "send:music:monitor"]) { rotate(target); rotate(target); }
    const sentBeforeClose = old.messages.length;
    old.emit("close");
    // The reconnect happens while the old values would still be warm.
    t.mock.timers.tick(501);
    assert.equal(old.messages.length, sentBeforeClose);
    assert.equal(control.children.length, 2);
    const peer = control.children[1]; peer.onopen();
    state.state.gainDb = 10; state.state.hpVolumeDb = -30;
    state.mixer.channels[0].levels.monitor = .2;
    peer.receive(state); peer.messages.length = 0;
    for (const target of ["gain", "hp", "send:music:monitor"]) rotate(target);
    t.mock.timers.tick(81);
    assert.deepEqual(peer.messages, [
      {cmd: "set", control: "gain", value: 11},
      {cmd: "set", control: "hpVolumeDb", value: -29.4},
      {cmd: "setLevel", channel: "music", mix: "monitor", value: .21},
    ]);
  } finally {
    control.restore();
    for (const timer of intervals) clearInterval(timer);
    globalThis.WebSocket = previous.socket; globalThis.setInterval = previous.interval;
    if (previous.skin === undefined) delete process.env.OPENXLR_SKIN; else process.env.OPENXLR_SKIN = previous.skin;
  }
});
