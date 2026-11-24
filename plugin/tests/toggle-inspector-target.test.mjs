import assert from "node:assert/strict";
import test from "node:test";
import {readFileSync} from "node:fs";
import vm from "node:vm";
import {Element, option} from "./inspector-dom.mjs";

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

// The plugin sends the insert and profile lists again whenever they change.
test("a later insert or profile list replaces the earlier one and keeps the key's target", () => {
  const groupValues = (key, id) => key.target.children.find(child => child.id === id)?.children.map(o => o.value);
  // Opened offline: the lists came empty, and the key's insert is unavailable.
  const key = page({target: "insert|xlr1|ins-3"});
  key.deliver({inserts: [], chains: []});
  key.deliver({profiles: []});
  assert.equal(key.unavailable().disabled, true);
  // The daemon is back: the lists fill and the real option replaces the entry.
  key.deliver({inserts: [{target: "insert|xlr1|ins-3", label: "XLR 1: Gate", meta: {plugin: "gate", index: 0}}],
    chains: [{target: "inschain|xlr1", label: "XLR 1: whole chain"}]});
  key.deliver({profiles: [{target: "profile|Evening", label: "Evening"}]});
  assert.equal(key.unavailable(), undefined);
  assert.equal(key.target.value, "insert|xlr1|ins-3");
  assert.equal(key.target.selectedOptions[0].disabled, false);
  // An insert added and a profile saved while the page is open.
  key.deliver({inserts: [{target: "insert|xlr1|ins-3", label: "XLR 1: Gate"}, {target: "insert|xlr1|ins-4", label: "XLR 1: Comp"}],
    chains: [{target: "inschain|xlr1", label: "XLR 1: whole chain"}]});
  key.deliver({profiles: [{target: "profile|Evening", label: "Evening"}, {target: "profile|Morning", label: "Morning"}]});
  assert.deepEqual(groupValues(key, "insert-group"), ["insert|xlr1|ins-3", "insert|xlr1|ins-4"]);
  assert.deepEqual(groupValues(key, "profile-group"), ["profile|Evening", "profile|Morning"]);
  assert.equal(key.target.children.filter(child => child.id === "insert-group").length, 1, "the group is replaced, not added again");
  assert.equal(key.target.value, "insert|xlr1|ins-3");
  // The key's insert is deleted: it stays selected as unavailable, and a save keeps it.
  key.deliver({inserts: [{target: "insert|xlr1|ins-4", label: "XLR 1: Comp"}], chains: []});
  assert.equal(key.target.value, "insert|xlr1|ins-3");
  assert.equal(key.unavailable().disabled, true);
  assert.equal(groupValues(key, "chain-group"), undefined, "an empty list removes its group");
  key.icon.choose("knob");
  assert.equal(key.saved().target, "insert|xlr1|ins-3");
  // A profile key follows its profile the same way.
  const profile = page({target: "profile|Morning"});
  profile.deliver({profiles: [{target: "profile|Evening", label: "Evening"}]});
  assert.equal(profile.unavailable().disabled, true);
  profile.deliver({profiles: [{target: "profile|Evening", label: "Evening"}, {target: "profile|Morning", label: "Morning"}]});
  assert.equal(profile.unavailable(), undefined);
  assert.equal(profile.target.value, "profile|Morning");
});
