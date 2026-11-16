import assert from "node:assert/strict";
import test from "node:test";
import {readFileSync} from "node:fs";
import vm from "node:vm";

// A selector DOM that behaves as a browser's single select does: setting a
// value no option holds leaves the first enabled option selected, and so does
// removing the selected option. That is what made the page read empty.
class Element {
  constructor(tag) {
    this.tag = tag; this.children = []; this.dataset = {}; this.listeners = new Map();
    this.disabled = false; this.hidden = false; this._value = ""; this.selected = null;
  }
  appendChild(child) { child.parent = this; this.children.push(child); this.root().settle(); }
  insertBefore(child, ref) {
    child.parent = this; this.children.splice(this.children.indexOf(ref), 0, child); this.root().settle();
  }
  remove() {
    const root = this.root();
    this.parent.children = this.parent.children.filter(child => child !== this);
    this.parent = null;
    root.settle();
  }
  root() { return this.parent ? this.parent.root() : this; }
  querySelector(selector) {
    return selector === "[data-layout-anchor]" ? this.children.find(child => "layoutAnchor" in child.dataset) ?? null : null;
  }
  querySelectorAll() { return this.children.filter(child => child.dataset.layoutGroup); }
  get options() { return this.children.flatMap(child => child.tag === "option" ? [child] : child.options); }
  get selectedOptions() { return this.selected ? [this.selected] : []; }
  get value() {
    if (this.tag === "select") return this.selected?.value ?? "";
    return this._value;
  }
  set value(value) {
    if (this.tag !== "select") { this._value = value; return; }
    this.selected = this.options.find(option => option.value === value) ?? null;
    this.settle();
  }
  settle() {
    if (this.tag !== "select") return;
    if (!this.options.includes(this.selected)) this.selected = this.options.find(option => !option.disabled) ?? null;
  }
  addEventListener(event, callback) { this.listeners.set(event, callback); }
  // A choice made by the user in the list.
  choose(value) { this.value = value; this.listeners.get("change")(); }
}

const option = (value, text = value) => {
  const element = new Element("option");
  element.value = value; element.textContent = text;
  return element;
};

function page(settings) {
  const target = new Element("select"); target.id = "target";
  target.appendChild(option("", "choose…"));
  const hardware = new Element("optgroup");
  hardware.appendChild(option("mute", "XLR 1 mute"));
  target.appendChild(hardware);
  const anchor = new Element("optgroup"); anchor.dataset.layoutAnchor = "";
  target.appendChild(anchor);
  const icon = new Element("select"); icon.id = "icon";
  for (const value of ["", "speaker", "knob"]) icon.appendChild(option(value));
  const voiceMode = new Element("select"); voiceMode.id = "voiceMode";
  for (const value of ["toggle", "hold"]) voiceMode.appendChild(option(value));
  const modeRow = new Element("div"); modeRow.id = "voice-mode-row";
  const roots = [target, icon, voiceMode, modeRow];
  const all = node => [node, ...node.children.flatMap(all)];
  const document = {createElement: tag => new Element(tag),
    getElementById: id => roots.flatMap(all).find(node => node.id === id)};
  let socket;
  class Socket {
    messages = [];
    constructor() { socket = this; }
    send(text) { this.messages.push(JSON.parse(text)); }
  }
  const context = vm.createContext({document, WebSocket: Socket, window: {}});
  for (const file of ["layout-options.js", "pi.js"])
    vm.runInContext(readFileSync(new URL("../com.emaspa.openxlr.sdPlugin/propertyInspector/" + file, import.meta.url), "utf8"), context);
  context.window.connectOpenActionSocket(1234, "inspector", "registerPropertyInspector", "{}",
    JSON.stringify({context: "key", payload: {settings}}));
  socket.onopen();
  const deliver = payload => socket.onmessage({data: JSON.stringify({event: "sendToPropertyInspector", payload})});
  const saved = () => socket.messages.filter(m => m.event === "setSettings").at(-1)?.payload;
  const unavailable = () => document.getElementById("unavailable-layout-target");
  return {target, icon, voiceMode, deliver, saved, unavailable};
}

// Every kind of target a toggle key can hold, with the plugin's answer that
// lists it. The last is a kind no list knows, as a new one would be.
const kinds = [
  ["noise:xlr1", {toggleGroups: [{label: "Noise", items: [{target: "noise:xlr1", label: "XLR 1 noise removal"}]}]}],
  ["eqprofile:xlr1:Warm", {toggleGroups: [{label: "EQ", items: [{target: "eqprofile:xlr1:Warm", label: "Warm"}]}]}],
  ["mixmute:stream", {toggleGroups: [{label: "Mixes", items: [{target: "mixmute:stream", label: "Stream mute"}]}]}],
  ["profile|Evening", {profiles: [{target: "profile|Evening", label: "Evening"}]}],
  ["monitor:alsa_output.desk", {outputs: [{name: "alsa_output.desk", description: "Desk"}]}],
  ["feed:alsa_output.desk", {outputs: [{name: "alsa_output.desk", description: "Desk"}]}],
  ["insert|xlr1|ins-3", {inserts: [{target: "insert|xlr1|ins-3", label: "XLR 1: Gate", meta: {plugin: "gate", index: 0}}], chains: []}],
  ["inschain|xlr1", {inserts: [{target: "insert|xlr1|ins-3", label: "XLR 1: Gate"}], chains: [{target: "inschain|xlr1", label: "XLR 1: whole chain"}]}],
  ["future:kind", {}],
];

// The same answers with something else listed, as from a daemon whose
// channel, output, insert or profile is gone.
const without = {
  toggleGroups: [{label: "Mixes", items: [{target: "mixmute:chat", label: "Chat mute"}]}],
  profiles: [{target: "profile|Morning", label: "Morning"}],
  outputs: [{name: "alsa_output.headset", description: "Headset"}],
  inserts: [{target: "insert|xlr2|ins-9", label: "XLR 2: Comp"}],
  chains: [{target: "inschain|xlr2", label: "XLR 2: whole chain"}],
};

for (const [wanted, listed] of kinds)
  test("a key keeps its target while its option is missing: " + wanted, () => {
    const key = page({target: wanted, icon: "", custom: "keep"});
    // Offline: nothing is listed, and the page names the target as unavailable.
    assert.equal(key.target.value, wanted);
    assert.equal(key.unavailable().disabled, true);
    key.icon.choose("knob");
    assert.deepEqual(key.saved(), {target: wanted, icon: "knob", custom: "keep", voiceMode: "toggle"});

    // Online, with the option gone.
    const online = page({target: wanted, icon: ""});
    online.deliver(without);
    assert.equal(online.target.value, wanted);
    assert.equal(online.unavailable().disabled, true);
    online.icon.choose("speaker");
    assert.equal(online.saved().target, wanted);

    // Listed: the real option replaces the unavailable entry.
    const listedPage = page({target: wanted, icon: ""});
    listedPage.deliver(listed);
    assert.equal(listedPage.target.value, wanted);
    if (Object.keys(listed).length) {
      assert.equal(listedPage.unavailable(), undefined);
      assert.equal(listedPage.target.selectedOptions[0].disabled, false);
    }
    listedPage.icon.choose("knob");
    assert.equal(listedPage.saved().target, wanted);
  });

test("a choice in the list replaces the target, and the empty choice clears it", () => {
  const key = page({target: "profile|Evening"});
  key.deliver(without);
  key.target.choose("profile|Morning");
  assert.equal(key.saved().target, "profile|Morning");
  assert.equal(key.unavailable(), undefined);
  // A later list that lacks the new choice keeps it as well.
  key.deliver({outputs: []});
  key.voiceMode.choose("hold");
  assert.equal(key.saved().target, "profile|Morning");
  key.target.choose("");
  assert.equal(key.saved().target, "");
  key.icon.choose("knob");
  assert.equal(key.saved().target, "");
});

test("an insert's metadata is saved for the insert the key holds", () => {
  const key = page({target: "insert|xlr1|ins-3"});
  key.deliver({inserts: [{target: "insert|xlr1|ins-3", label: "XLR 1: Gate", meta: {plugin: "gate", index: 0}}], chains: []});
  key.icon.choose("knob");
  assert.deepEqual(key.saved().meta, {"insert|xlr1|ins-3": {plugin: "gate", index: 0}});
});
