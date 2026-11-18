import assert from "node:assert/strict";
import test from "node:test";
import {format} from "node:util";
import {controlFixture} from "./control-fixture.mjs";

// A message the plugin cannot use, from OpenDeck or from the daemon, is logged
// and dropped. Before, one of them ended the plugin and every key went blank.
test("malformed messages from OpenDeck and the daemon are dropped and the plugin keeps working", async t => {
  t.mock.timers.enable({apis: ["setTimeout"]});
  const oldSkin = process.env.OPENXLR_SKIN;
  process.env.OPENXLR_SKIN = "default";
  const previous = globalThis.WebSocket;
  const previousInterval = globalThis.setInterval;
  const previousError = console.error;
  const intervals = [];
  const logged = [];
  globalThis.setInterval = (...args) => {
    const interval = previousInterval(...args);
    intervals.push(interval);
    return interval;
  };
  console.error = (...args) => logged.push(format(...args));
  const sockets = [];
  class Socket {
    static OPEN = 1;
    readyState = 1;
    messages = [];
    constructor(url) { this.url = url; sockets.push(this); }
    send(text) { this.messages.push(JSON.parse(text)); }
    receive(message) { this.onmessage({data: typeof message === "string" ? message : JSON.stringify(message)}); }
  }
  globalThis.WebSocket = Socket;
  const control = controlFixture();
  const rejectionHandlers = process.listenerCount("unhandledRejection");
  const exceptionHandlers = process.listenerCount("uncaughtException");
  try {
    await import("../com.emaspa.openxlr.sdPlugin/plugin.mjs");
    // An error nothing caught is logged rather than ending the process.
    assert.equal(process.listenerCount("unhandledRejection"), rejectionHandlers + 1);
    assert.equal(process.listenerCount("uncaughtException"), exceptionHandlers + 1);

    const host = sockets[0];
    const daemon = control.children[0];
    daemon.onopen();
    const state = (mixer, extra = {}) => ({type: "state", connected: true, profiles: ["Live"], activeProfile: "Live",
      capabilities: {gain: true, mute: true, xlrInputs: 1, gainMaxDb: 80}, state: {gainDb: 40, mute: false}, devices: [],
      mixer: {channels: [], mixes: [], monitorOutputs: [], monitorFeeds: {}, inserts: {}, lowCutHz: 0, ...mixer}, ...extra});
    daemon.receive(state({}));
    const images = context => host.messages.filter(m => m.event === "setImage" && m.context === context);
    const face = context => host.messages.filter(m => m.event === "setFeedback" && m.context === context && m.payload.value !== undefined).at(-1)?.payload;

    // Not JSON, and JSON that is not an object.
    for (const text of ["{", "null", "5", "[]"]) assert.doesNotThrow(() => host.receive(text));
    assert.equal(logged.filter(line => line.includes("from OpenDeck that is not")).length, 4);

    // A key saved with a target that is not a string draws as an unset key.
    assert.doesNotThrow(() => host.receive({event: "willAppear", context: "number", action: "com.emaspa.openxlr.toggle",
      payload: {settings: {target: 5}}}));
    assert.doesNotThrow(() => host.receive({event: "willAppear", context: "object", action: "com.emaspa.openxlr.toggle",
      payload: {settings: {target: {a: 1}}}}));
    assert.doesNotThrow(() => host.receive({event: "willAppear", context: "settings", action: "com.emaspa.openxlr.toggle",
      payload: {settings: "mute"}}));
    for (const context of ["number", "object", "settings"]) assert.equal(images(context).length, 1);
    // A dial skips the entries of its stack that are not strings.
    host.receive({event: "willAppear", context: "dial", action: "com.emaspa.openxlr.dial",
      payload: {settings: {targets: [7, null, "gain"]}}});
    assert.equal(face("dial").value, "40");
    // A turn by a count that is not a number moves nothing.
    host.receive({event: "dialRotate", context: "dial", payload: {ticks: "lots"}});
    t.mock.timers.tick(200);
    assert.ok(daemon.messages.filter(m => m.cmd === "set").every(m => m.value === 40));
    assert.equal(face("dial").value, "40");
    // A title that is not a string is no title.
    assert.doesNotThrow(() => host.receive({event: "titleParametersDidChange", context: "number", payload: {title: 3}}));
    // An insert key whose saved plugin is not a string still names itself.
    host.receive({event: "willAppear", context: "insert", action: "com.emaspa.openxlr.toggle",
      payload: {settings: {target: "insert|xlr1|gone", meta: {"insert|xlr1|gone": {plugin: 9, index: 0}}}}});
    host.receive({event: "titleParametersDidChange", context: "insert", payload: {title: ""}});
    assert.equal(images("insert").length, 2);

    // A handler that throws drops its message and the next one is handled.
    const send = host.send;
    host.send = () => { throw new Error("socket not open"); };
    assert.doesNotThrow(() => host.receive({event: "willAppear", context: "late", action: "com.emaspa.openxlr.toggle",
      payload: {settings: {target: "mute"}}}));
    host.send = send;
    assert.ok(logged.some(line => line.includes("dropped a willAppear message from OpenDeck") && line.includes("socket not open")));
    host.receive({event: "willAppear", context: "late", action: "com.emaspa.openxlr.toggle", payload: {settings: {target: "mute"}}});
    assert.equal(images("late").length, 1);

    // From the daemon: lines that are not JSON or not objects.
    daemon.stdout.write("{\nnull\n");
    assert.equal(logged.filter(line => line.includes("from the daemon that is not")).length, 2);

    // A state whose lists hold the wrong kinds: the readers see only the usable entries.
    daemon.receive(state({channels: 5, mixes: [null, 3], monitorOutputs: [3, "speakers"], inserts: {xlr1: "x", xlr2: [null]},
      exclusiveGroups: "none", soundboard: 2}, {profiles: [1, "Live", null], devices: "none"}));
    host.receive({event: "sendToPlugin", context: "profiles", payload: {request: "profiles"}});
    host.receive({event: "sendToPlugin", context: "outputs", payload: {request: "outputs"}});
    host.receive({event: "sendToPlugin", context: "inserts", payload: {request: "inserts"}});
    const inspector = context => host.messages.filter(m => m.event === "sendToPropertyInspector" && m.context === context).at(-1)?.payload;
    assert.deepEqual(inspector("profiles"), {profiles: [{target: "profile|Live", label: "Live"}]});
    assert.deepEqual(inspector("outputs"), {outputs: []});
    assert.deepEqual(inspector("inserts"), {inserts: [], chains: [], params: []});
    host.receive({event: "willAppear", context: "profile", action: "com.emaspa.openxlr.toggle", payload: {settings: {target: "profile|Live"}}});
    assert.equal(images("profile").length, 1);
    // A whole block of the wrong kind reads as absent.
    daemon.receive(state({}, {mixer: [], state: "x", capabilities: 4}));
    daemon.receive({type: "plugins", plugins: {}});
    daemon.receive({type: "plugins", plugins: [null, {plugin: "urn:x", name: "X"}]});
    // Meter levels of the wrong kind draw nothing.
    daemon.receive({type: "meters", levels: 5});
    daemon.receive({type: "meters", levels: {"ch:xlr1": null}});

    // A message that still throws is logged and dropped, and the daemon's next one is handled.
    daemon.receive(state({channels: [{id: "music", mutedIn: 5}], mixes: [{id: "stream"}]}));
    host.receive({event: "willAppear", context: "sendmute", action: "com.emaspa.openxlr.toggle", payload: {settings: {target: "sendmute:music:stream"}}});
    daemon.receive(state({channels: [{id: "music", mutedIn: 5}], mixes: [{id: "stream"}]}));
    assert.ok(logged.some(line => line.includes("dropped a state message from the daemon")));
    daemon.receive(state({}, {state: {gainDb: 30, mute: false}}));
    assert.equal(face("dial").value, "30");
  } finally {
    control.restore();
    for (const interval of intervals) clearInterval(interval);
    globalThis.setInterval = previousInterval;
    globalThis.WebSocket = previous;
    console.error = previousError;
    if (oldSkin === undefined) delete process.env.OPENXLR_SKIN;
    else process.env.OPENXLR_SKIN = oldSkin;
  }
});
