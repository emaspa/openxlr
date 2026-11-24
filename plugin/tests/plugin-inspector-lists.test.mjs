import assert from "node:assert/strict";
import test from "node:test";
import {controlFixture} from "./control-fixture.mjs";

const comp = {id: "ins-3", kind: "lv2", plugin: "urn:comp", label: "Comp", bypass: false, params: {}};
const gate = {id: "ins-4", kind: "lv2", plugin: "urn:gate", label: "Gate", bypass: false, params: {}};
const state = (inserts = {}, profiles = []) => ({type: "state", connected: true, profiles, devices: [],
  capabilities: {xlrInputs: 1}, state: {},
  mixer: {channels: [{id: "xlr1", name: "XLR 1", levels: {monitor: 1}, mutedIn: []}],
          mixes: [{id: "monitor", name: "Monitor A", kind: "monitor", volume: 1, muted: false}],
          monitorOutputs: [], monitorFeeds: {}, inserts, lowCutHz: 0}});

// An inspector that asked for the inserts or the profiles is sent them again
// whenever they change, as the layout, outputs and clips are: one opened
// before the state or the plugin catalogue arrived does not stay empty.
test("insert and profile pickers are sent their lists again when they change", async () => {
  const previous = {socket: globalThis.WebSocket, interval: globalThis.setInterval, skin: process.env.OPENXLR_SKIN};
  process.env.OPENXLR_SKIN = "default";
  const intervals = [], sockets = [];
  globalThis.setInterval = (...args) => { const value = previous.interval(...args); intervals.push(value); return value; };
  class Socket {
    static OPEN = 1;
    readyState = 1;
    messages = [];
    constructor(url) { this.url = url; sockets.push(this); }
    send(text) { this.messages.push(JSON.parse(text)); }
    receive(message) { this.onmessage({data: JSON.stringify(message)}); }
  }
  globalThis.WebSocket = Socket;
  const control = controlFixture();
  const flush = () => new Promise(setImmediate);
  try {
    await import("../com.emaspa.openxlr.sdPlugin/plugin.mjs");
    const host = sockets[0];
    const daemon = control.children[0];
    daemon.onopen(); await flush();
    const push = async message => { daemon.receive(message); await flush(); };
    await push(state());
    const sentTo = (context, field) => host.messages.filter(m => m.event === "sendToPropertyInspector"
      && m.context === context && Array.isArray(m.payload?.[field])).map(m => m.payload);

    host.receive({event: "sendToPlugin", context: "key", payload: {request: "inserts"}});
    host.receive({event: "sendToPlugin", context: "key", payload: {request: "profiles"}});
    host.receive({event: "sendToPlugin", context: "dial", payload: {request: "inserts"}});
    assert.deepEqual(sentTo("key", "inserts").at(-1), {inserts: [], chains: [], params: []});
    assert.deepEqual(sentTo("key", "profiles").at(-1), {profiles: []});

    // An insert is added and a profile saved while the inspectors are open.
    await push(state({xlr1: [comp]}, ["Evening"]));
    assert.deepEqual(sentTo("key", "inserts").at(-1).inserts.map(i => i.target), ["insert|xlr1|ins-3"]);
    assert.deepEqual(sentTo("key", "inserts").at(-1).chains.map(i => i.target), ["inschain|xlr1"]);
    assert.deepEqual(sentTo("dial", "inserts").at(-1).inserts.map(i => i.target), ["insert|xlr1|ins-3"]);
    assert.deepEqual(sentTo("key", "profiles").at(-1), {profiles: [{target: "profile|Evening", label: "Evening"}]});

    // The plugin catalogue arrives: the dial is sent the insert's controls.
    await push({type: "plugins", plugins: [{plugin: "urn:comp", name: "Comp",
      params: [{symbol: "threshold", name: "Threshold", min: -60, max: 0, default: -20}]}]});
    assert.deepEqual(sentTo("dial", "params").at(-1).params.map(p => p.target), ["insparam|xlr1|ins-3|threshold"]);

    // A state that changes neither list sends neither again.
    const count = host.messages.length;
    await push(state({xlr1: [comp]}, ["Evening"]));
    assert.equal(host.messages.filter(m => m.event === "sendToPropertyInspector").length,
      host.messages.slice(0, count).filter(m => m.event === "sendToPropertyInspector").length);

    // A closed inspector is sent nothing more.
    host.receive({event: "propertyInspectorDidDisappear", context: "dial"});
    const dialSends = sentTo("dial", "inserts").length;
    await push(state({xlr1: [comp, gate]}, ["Evening", "Morning"]));
    assert.equal(sentTo("dial", "inserts").length, dialSends);
    assert.deepEqual(sentTo("key", "inserts").at(-1).inserts.map(i => i.target), ["insert|xlr1|ins-3", "insert|xlr1|ins-4"]);
    assert.deepEqual(sentTo("key", "profiles").at(-1).profiles.map(p => p.target), ["profile|Evening", "profile|Morning"]);
    host.receive({event: "propertyInspectorDidDisappear", context: "key"});
    const keySends = host.messages.length;
    await push(state());
    assert.equal(host.messages.slice(keySends).filter(m => m.event === "sendToPropertyInspector").length, 0);
  } finally {
    globalThis.WebSocket = previous.socket;
    globalThis.setInterval = previous.interval;
    for (const interval of intervals) clearInterval(interval);
    if (previous.skin === undefined) delete process.env.OPENXLR_SKIN;
    else process.env.OPENXLR_SKIN = previous.skin;
    control.restore();
  }
});
