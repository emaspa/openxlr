import assert from "node:assert/strict";
import test from "node:test";
import {controlFixture} from "./control-fixture.mjs";

// The daemon refuses a volume change for an output whose volume is locked,
// and the shared Monitor volume while that output is the first one. A dial
// that kept sending would show a value the daemon never took.
test("a locked output volume stops the dials and the volume keys, and shows on both", async t => {
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
    const daemon = control.children[0], host = sockets[0];
    daemon.onopen();
    const makeState = lock => ({type: "state", connected: true, profiles: [], capabilities: {}, state: {},
      devices: [{name: "headphones", description: "Headphones", kind: 0, volume: .8, muted: false},
        {name: "speakers", description: "Speakers", kind: 0, volume: .3, muted: false}],
      mixer: {
        channels: [{id: "music", name: "Music", levels: {monitor: .5}, mutedIn: []}],
        mixes: [{id: "monitor", name: "Monitor", kind: "monitor", volume: 1}], inserts: {},
        monitorOutputs: ["headphones", "speakers"], outputVolume: .8, ...lock,
      }});
    const held = {outputVolumeLock: {device: "headphones", volume: .8}, monitorVolumeLocked: true};
    let alertsBefore = 0;
    const show = lock => {
      t.mock.timers.tick(1000);
      daemon.receive(makeState(lock)); daemon.messages.length = 0;
      alertsBefore = host.messages.length;
    };
    const rotate = (context, ticks = 1) => host.receive({event: "dialRotate", context, payload: {ticks}});
    // A key's command is answered, so the next press on that output is not queued behind it.
    const press = context => {
      const before = daemon.messages.length;
      host.receive({event: "keyDown", context});
      for (const {requestId} of daemon.messages.slice(before)) if (requestId) daemon.receive({type: "commandResult", requestId});
    };
    const sent = () => daemon.messages.map(({requestId, ...message}) => message);
    const alerts = context => host.messages.slice(alertsBefore).filter(m => m.event === "showAlert" && m.context === context).length;
    const dialText = context => host.messages.filter(m => m.event === "setFeedback" && m.context === context).at(-1).payload.value;
    const keyImage = context => host.messages.filter(m => m.event === "setImage" && m.context === context).at(-1).payload.image;

    show({});
    for (const target of ["outputVolume", "output:headphones", "output:speakers"])
      host.receive({event: "willAppear", context: target, action: "com.emaspa.openxlr.dial", payload: {settings: {target}}});
    for (const target of ["outputup:headphones", "outputdown:headphones", "outputup:speakers", "outputmute:headphones"])
      host.receive({event: "willAppear", context: target, action: "com.emaspa.openxlr.toggle", payload: {settings: {target}}});
    const free = Object.fromEntries(["outputup:headphones", "outputup:speakers"].map(key => [key, keyImage(key)]));

    await t.test("without a lock every control sends", () => {
      show({});
      rotate("outputVolume"); rotate("output:headphones"); press("outputup:headphones");
      assert.deepEqual(sent(), [{cmd: "setOutputVolume", value: .81},
        {cmd: "setOutputDeviceVolume", device: "headphones", value: .81},
        {cmd: "adjustOutputVolume", device: "headphones", value: .05}]);
      assert.equal(dialText("outputVolume"), "81%");
    });

    await t.test("the Monitor dial and the locked output's dial show the lock and send nothing", () => {
      show(held);
      assert.equal(dialText("outputVolume"), "LOCKED");
      assert.equal(dialText("output:headphones"), "LOCKED");
      assert.equal(dialText("output:speakers"), "30%");
      rotate("outputVolume"); rotate("outputVolume", -3); rotate("output:headphones");
      assert.deepEqual(sent(), []);
      assert.equal(alerts("outputVolume"), 2);
      assert.equal(alerts("output:headphones"), 1);
      // The turn left no value behind for the display to hold.
      assert.equal(dialText("outputVolume"), "LOCKED");

      rotate("output:speakers");
      assert.deepEqual(sent(), [{cmd: "setOutputDeviceVolume", device: "speakers", value: .31}]);
    });

    await t.test("louder and quieter on the locked output are unavailable; its mute key still works", () => {
      show(held);
      assert.notEqual(keyImage("outputup:headphones"), free["outputup:headphones"]);
      assert.equal(keyImage("outputup:speakers"), free["outputup:speakers"]);
      press("outputup:headphones"); press("outputdown:headphones");
      assert.deepEqual(sent(), []);
      assert.equal(alerts("outputup:headphones"), 1);
      assert.equal(alerts("outputdown:headphones"), 1);
      press("outputup:speakers"); press("outputmute:headphones");
      assert.deepEqual(sent(), [{cmd: "adjustOutputVolume", device: "speakers", value: .05},
        {cmd: "toggleOutputMute", device: "headphones"}]);
    });

    await t.test("a muted locked output still reads as muted", () => {
      show({...held, monitorMuted: true});
      assert.equal(dialText("outputVolume"), "MUTED");
    });

    await t.test("a lock that waits for the interface, or one on a later output, leaves the Monitor dial free", () => {
      show({outputVolumeLock: {device: "headphones", volume: .8}, outputVolumeLockSuspended: true, monitorVolumeLocked: false});
      assert.equal(dialText("outputVolume"), "80%");
      assert.equal(dialText("output:headphones"), "80%");
      rotate("outputVolume"); rotate("output:headphones"); press("outputup:headphones");
      assert.deepEqual(sent(), [{cmd: "setOutputVolume", value: .81},
        {cmd: "setOutputDeviceVolume", device: "headphones", value: .81},
        {cmd: "adjustOutputVolume", device: "headphones", value: .05}]);

      show({outputVolumeLock: {device: "speakers", volume: .3}, monitorVolumeLocked: false});
      assert.equal(dialText("outputVolume"), "80%");
      assert.equal(dialText("output:speakers"), "LOCKED");
      rotate("outputVolume"); rotate("output:speakers");
      assert.deepEqual(sent(), [{cmd: "setOutputVolume", value: .81}]);
    });
  } finally {
    control.restore();
    for (const timer of intervals) clearInterval(timer);
    globalThis.WebSocket = previous.socket; globalThis.setInterval = previous.interval;
    if (previous.skin === undefined) delete process.env.OPENXLR_SKIN; else process.env.OPENXLR_SKIN = previous.skin;
  }
});
