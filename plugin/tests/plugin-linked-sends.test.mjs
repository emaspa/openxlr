import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";
import {controlFixture} from "./control-fixture.mjs";
import {layoutChoices} from "../com.emaspa.openxlr.sdPlugin/layout-choices.mjs";

const mixes = [{id: "monitor", name: "Monitor A"}, {id: "stream", name: "Stream"}];

test("the toggle offers a link key per channel only when the daemon reports the link", () => {
  const group = mixer => layoutChoices(mixer).toggleGroups.find(g => g.id === "layout-linked-sends");
  const music = {id: "music", name: "Music", linkedSends: false};
  const game = {id: "game", name: "Game", linkedSends: true};
  assert.deepEqual(group({channels: [music, game], mixes}).items.map(i => [i.target, i.label]),
    [["sendlink:music", "Music: link sends"], ["sendlink:game", "Game: link sends"]]);
  // An older daemon sends no linkedSends: no group at all.
  assert.equal(group({channels: [{id: "music", name: "Music"}], mixes}), undefined);
});

// Minimal selector DOM, as layout-options.test.mjs builds it.
class Element {
  constructor(tag) { this.tag = tag; this.children = []; this.dataset = {}; this._value = ""; }
  appendChild(child) { child.parent = this; this.children.push(child); }
  insertBefore(child, ref) { child.parent = this; this.children.splice(this.children.indexOf(ref), 0, child); }
  querySelector(sel) { return sel === "[data-layout-anchor]" ? this.children.find(child => "layoutAnchor" in child.dataset) ?? null : null; }
  remove() { this.parent.children = this.parent.children.filter(child => child !== this); }
  get options() { return this.children.flatMap(child => child.tag === "option" ? [child] : child.options); }
  get value() { return this.tag === "select" ? (this.options.some(o => o.value === this._value) ? this._value : "") : this._value; }
  set value(value) { this._value = value; }
  querySelectorAll() { return this.children.filter(child => child.dataset.layoutGroup); }
}

test("a link key whose channel is deleted stays selected as unavailable", () => {
  const select = new Element("select"); select.id = "target";
  const all = node => [node, ...node.children.flatMap(all)];
  const document = {createElement: tag => new Element(tag), getElementById: id => all(select).find(node => node.id === id)};
  const context = vm.createContext({document});
  vm.runInContext(readFileSync(new URL("../com.emaspa.openxlr.sdPlugin/propertyInspector/layout-options.js", import.meta.url), "utf8"), context);
  const xlr1 = {id: "xlr1", name: "XLR 1", hardware: true, linkedSends: false};
  context.replaceLayoutOptions(layoutChoices({channels: [xlr1, {id: "music", name: "Music", linkedSends: false}], mixes}).toggleGroups);
  select.value = "sendlink:music";
  assert.equal(select.value, "sendlink:music");
  context.replaceLayoutOptions(layoutChoices({channels: [xlr1], mixes}).toggleGroups);
  assert.equal(select.value, "sendlink:music");
  assert.equal(document.getElementById("unavailable-layout-target").disabled, true);
});

// The key against a fake OpenDeck host and a fake daemon: grey on a daemon
// without the link, lit while linked, a press flips it after any send turn
// still waiting, and a channel that is gone only alerts.
test("a link key lights with the channel's link and a press flips it", async t => {
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
    const music = {id: "music", name: "Music", volume: 1, levels: {monitor: .5, stream: .5}, mutedIn: []};
    const state = {type: "state", connected: true, profiles: [], activeProfile: null,
      capabilities: {xlrInputs: 1}, state: {}, devices: [],
      mixer: {channels: [music], mixes: [{id: "monitor", name: "Monitor A", kind: "monitor", volume: 1},
        {id: "stream", name: "Stream", kind: "virtualMic", volume: 1}], inserts: {}}};
    const appear = (context, action, target) => host.receive({event: "willAppear", context,
      action: `com.emaspa.openxlr.${action}`, payload: {settings: {target}}});
    const svg = context => {
      const image = host.messages.filter(m => m.event === "setImage" && m.context === context).at(-1).payload.image;
      return Buffer.from(image.split(",")[1], "base64").toString("utf8");
    };
    const alerts = context => host.messages.filter(m => m.event === "showAlert" && m.context === context).length;
    const sent = () => daemon.messages.filter(m => m.cmd !== "listPlugins").map(({requestId, ...m}) => m);
    const press = context => host.receive({event: "keyDown", context});
    const answer = () => daemon.receive({type: "commandResult", requestId: daemon.messages.at(-1).requestId, error: null});

    appear("link", "toggle", "sendlink:music");
    appear("gone", "toggle", "sendlink:gone");
    appear("send", "dial", "send:music:monitor");
    host.receive({event: "titleParametersDidChange", context: "link", payload: {title: ""}});

    // An older daemon reports no link: the key is grey and a press only alerts.
    daemon.receive(state);
    const grey = svg("link");
    press("link");
    assert.equal(alerts("link"), 1);
    assert.deepEqual(sent(), []);

    music.linkedSends = false;
    daemon.receive(state);
    const unlit = svg("link");
    assert.match(unlit, />Link sends<\/text>.*>Music<\/text>/s);
    assert.notEqual(unlit, grey);

    // A send turn still waiting reaches the daemon before the link.
    const rotate = () => host.receive({event: "dialRotate", context: "send", payload: {ticks: 1}});
    rotate(); rotate();
    press("link");
    const out = sent();
    assert.deepEqual(out.at(-1), {cmd: "setLinkedSends", channel: "music", value: true});
    assert.equal(out.filter(m => m.cmd !== "setLinkedSends").length, 2, "both send turns go before the link");
    answer();

    music.linkedSends = true;
    music.levels = {monitor: .52, stream: .52};
    daemon.receive(state);
    assert.notEqual(svg("link"), unlit, "a linked channel lights its key");
    press("link");
    assert.deepEqual(sent().at(-1), {cmd: "setLinkedSends", channel: "music", value: false});
    answer();

    // A channel the layout no longer has is unavailable.
    const before = sent().length;
    press("gone");
    assert.equal(alerts("gone"), 1);
    assert.equal(sent().length, before);
  } finally {
    control.restore();
    for (const timer of intervals) clearInterval(timer);
    globalThis.WebSocket = previous.socket;
    globalThis.setInterval = previous.interval;
    if (previous.skin === undefined) delete process.env.OPENXLR_SKIN;
    else process.env.OPENXLR_SKIN = previous.skin;
  }
});
