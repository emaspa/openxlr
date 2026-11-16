import assert from "node:assert/strict";
import test from "node:test";
import {controlFixture} from "./control-fixture.mjs";

// The daemon refuses a gain set while the gain lock is on and sends an error,
// with no state broadcast to correct the strip. The dial must go back to the
// daemon's value when its hold ends, or at once on the error.
test("a refused dial turn returns to the daemon's value", async t => {
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
  const errors = [];
  t.mock.method(console, "error", (...args) => errors.push(args.join(" ")));
  try {
    await import("../com.emaspa.openxlr.sdPlugin/plugin.mjs");
    const daemon = control.children[0];
    const host = sockets[0];
    daemon.onopen();
    daemon.receive({type: "state", connected: true, profiles: [],
      capabilities: {gain: true, gainMaxDb: 75, xlrInputs: 1},
      state: {gainDb: 40, gainLocked: true},
      mixer: {channels: [], mixes: []}});
    host.receive({event: "willAppear", context: "dial", action: "com.emaspa.openxlr.dial",
      payload: {settings: {target: "gain", stepSize: 5}}});
    const shown = () => Number.parseFloat(host.messages.filter(m => m.event === "setFeedback"
      && m.context === "dial" && m.payload.title).at(-1).payload.value);
    const rotate = ticks => host.receive({event: "dialRotate", context: "dial", payload: {ticks}});
    const gainSets = () => daemon.messages.filter(m => m.cmd === "set" && m.control === "gain").map(m => m.value);
    assert.equal(shown(), 40);

    await t.test("the hold ends and the strip shows the daemon's value again", () => {
      rotate(1);
      assert.deepEqual(gainSets(), [45]);
      assert.equal(shown(), 45, "the turn leads while it is warm");
      t.mock.timers.tick(799);
      assert.equal(shown(), 45);
      t.mock.timers.tick(1);
      assert.equal(shown(), 40, "no broadcast came, the hold's end redraws");
      // The next turn steps from the daemon's value, not the refused one.
      rotate(1);
      assert.deepEqual(gainSets(), [45, 45]);
      t.mock.timers.tick(1000);
      assert.equal(shown(), 40);
    });

    await t.test("an error drops the local value at once", () => {
      daemon.messages.length = 0;
      rotate(1);
      assert.equal(shown(), 45);
      daemon.receive({type: "error", message: "gain is locked"});
      assert.equal(shown(), 40);
      assert.ok(errors.some(line => line.includes("gain is locked")));
      rotate(1);
      // A second tick inside the send window goes out on its trailing edge.
      t.mock.timers.tick(80);
      assert.deepEqual(gainSets(), [45, 45], "the next turn steps from 40");
      daemon.receive({type: "error", message: "gain is locked"});
      assert.equal(shown(), 40);
      // The hold's own end, after the error, leaves the daemon's value.
      t.mock.timers.tick(1000);
      assert.equal(shown(), 40);
    });

    await t.test("an accepted turn keeps its value after the hold", () => {
      daemon.messages.length = 0;
      rotate(1);
      daemon.receive({type: "state", connected: true, profiles: [],
        capabilities: {gain: true, gainMaxDb: 75, xlrInputs: 1},
        state: {gainDb: 45, gainLocked: false},
        mixer: {channels: [], mixes: []}});
      t.mock.timers.tick(1000);
      assert.equal(shown(), 45);
    });
  } finally {
    for (const interval of intervals) clearInterval(interval);
    globalThis.setInterval = previous.interval;
    globalThis.WebSocket = previous.socket;
    if (previous.skin === undefined) delete process.env.OPENXLR_SKIN; else process.env.OPENXLR_SKIN = previous.skin;
    control.restore();
  }
});
