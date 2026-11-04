import {controlFixture} from "./control-fixture.mjs";
import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import vm from "node:vm";

// A toggle inspector opened while the daemon is still building its graph
// gets no outputs. The plugin sends them once they exist, and again when
// they change, as it does the layout groups.
test("toggle inspectors get the output devices once the graph has them", async () => {
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
  const control = controlFixture();
  try {
    await import("../com.emaspa.openxlr.sdPlugin/plugin.mjs");
    const daemon = control.children[0];
    const host = sockets[0];
    daemon.onopen();
    const outputsSent = () => host.messages.filter(m => m.event === "sendToPropertyInspector"
      && m.context === "pi" && Array.isArray(m.payload?.outputs)).map(m => m.payload.outputs);
    // Before the graph is built the daemon reports no devices at all.
    daemon.receive({type: "state", profiles: [], devices: null, mixer: null});
    host.receive({event: "sendToPlugin", context: "pi", payload: {request: "outputs"}});
    assert.deepEqual(outputsSent(), [[]]);

    const state = {type: "state", profiles: [], devices: [
      {kind: 0, name: "alsa_output.speakers", description: "Speakers"},
      {kind: 0, name: "OpenXLR_mix_monitor", description: "OpenXLR Monitor A", isOwn: true},
      {kind: 1, name: "alsa_input.mic", description: "Mic"},
    ], mixer: {channels: [], mixes: [], monitorOutputs: [], monitorFeeds: {}, inserts: {}}};
    daemon.receive(state);
    assert.deepEqual(outputsSent().at(-1), [{name: "alsa_output.speakers", description: "Speakers"}]);
    // The same outputs again are not sent again.
    daemon.receive(state);
    assert.equal(outputsSent().length, 2);
    state.devices.push({kind: 0, name: "alsa_output.headset", description: "Headset"});
    daemon.receive(state);
    assert.equal(outputsSent().at(-1).length, 2);
    // A closed inspector is sent nothing more.
    host.receive({event: "propertyInspectorDidDisappear", context: "pi"});
    state.devices.pop();
    daemon.receive(state);
    assert.equal(outputsSent().length, 3);
  } finally {
    for (const interval of intervals) clearInterval(interval);
    globalThis.setInterval = previousInterval;
    globalThis.WebSocket = previous;
    control.restore();
  }
});

// Minimal selector DOM, as in layout-options.test.mjs.
class Element {
  constructor(tag) { this.tag = tag; this.children = []; this.dataset = {}; this._value = ""; }
  appendChild(child) { child.parent = this; this.children.push(child); }
  remove() { this.parent.children = this.parent.children.filter(child => child !== this); }
  get options() { return this.children.flatMap(child => child.tag === "option" ? [child] : child.options); }
  get value() { return this.tag === "select" ? (this.options.some(o => o.value === this._value) ? this._value : "") : this._value; }
  set value(value) { this._value = value; }
}

test("the inspector refills an empty monitor group and keeps the saved key", () => {
  const select = new Element("select");
  select.id = "target";
  const all = node => [node, ...node.children.flatMap(all)];
  const document = {createElement: tag => new Element(tag), getElementById: id => all(select).find(node => node.id === id)};
  const context = vm.createContext({document, window: {}});
  vm.runInContext(readFileSync(new URL("../com.emaspa.openxlr.sdPlugin/propertyInspector/pi.js", import.meta.url), "utf8"), context);
  const wanted = "monitor:alsa_output.headset";
  context.fillMonitors([], wanted);
  assert.deepEqual(document.getElementById("monitor-group").children, []);
  context.fillMonitors([{name: "alsa_output.speakers", description: "Speakers"},
    {name: "alsa_output.headset", description: "Headset"}], wanted);
  assert.deepEqual(document.getElementById("monitor-group").children.map(o => o.value),
    ["monitor:alsa_output.speakers", "monitor:alsa_output.headset"]);
  assert.deepEqual(document.getElementById("feed-group").children.map(o => o.value),
    ["feed:alsa_output.speakers", "feed:alsa_output.headset"]);
  assert.equal(select.value, wanted);
  // One group of each, however often the outputs arrive.
  context.fillMonitors([{name: "alsa_output.headset", description: "Headset"}], wanted);
  assert.equal(select.children.filter(child => child.id === "monitor-group").length, 1);
  assert.equal(select.children.filter(child => child.id === "feed-group").length, 1);
  assert.equal(select.value, wanted);
});
