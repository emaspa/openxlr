import assert from "node:assert/strict";
import test from "node:test";
import {readFileSync} from "node:fs";
import vm from "node:vm";

const html = readFileSync(new URL("../com.emaspa.openxlr.sdPlugin/propertyInspector/dial.html", import.meta.url), "utf8");
const scriptTag = "<script>";
const scriptStart = html.indexOf(scriptTag);
const scriptEnd = html.indexOf("</script>", scriptStart);
assert.ok(scriptStart >= 0 && scriptEnd > scriptStart, "The inspector must contain its inline script.");
const script = html.slice(scriptStart + scriptTag.length, scriptEnd);

// Run the shipped inspector, including its host messages, without changing
// real OpenDeck settings. The target option list is supplied by the fixture.
function inspector(settings = {}) {
  class Element {
    listeners = new Map(); children = []; value = "";
    addEventListener(event, callback) { this.listeners.set(event, callback); }
    append(...children) { this.children.push(...children); }
    appendChild(child) { this.append(child); }
    set innerHTML(_) { this.children = []; }
    change(value) { this.value = value; this.listeners.get("change")(); }
  }
  const nodes = Object.fromEntries(["target", "add", "stack", "stepSize"].map(id => [id, new Element()]));
  nodes.target.options = [{value: "gain", textContent: "Input gain"}, {value: "hp", textContent: "Headphones"}];
  const radios = [new Element(), new Element()]; radios[0].value = "tap"; radios[1].value = "push";
  const document = {getElementById: id => nodes[id], createElement: () => new Element(),
    querySelectorAll: () => radios,
    querySelector: selector => selector.endsWith(":checked") ? radios.find(r => r.checked)
      : radios.find(r => selector.includes(`value="${r.value}"`))};
  let socket;
  class Socket {
    messages = [];
    constructor() { socket = this; }
    send(text) { this.messages.push(JSON.parse(text)); }
  }
  const context = vm.createContext({document, WebSocket: Socket, window: {}, replaceLayoutOptions() {}});
  vm.runInContext(script, context);
  context.window.connectOpenActionSocket(1234, "inspector", "registerPropertyInspector", "{}",
    JSON.stringify({context: "dial", payload: {settings}}));
  socket.onopen();
  return {nodes, socket, settings: () => socket.messages.filter(m => m.event === "setSettings").at(-1)?.payload};
}

test("the step selector offers the default and supported unit steps with an accessible hint", () => {
  assert.match(html, /<label for="stepSize"[^>]*>Step per tick<\/label>/);
  const select = html.match(/<select id="stepSize"[^>]*>([\s\S]*?)<\/select>/);
  assert.match(select[0], /aria-describedby="stepHint"/);
  assert.deepEqual([...select[1].matchAll(/<option value="([^"]*)"/g)].map(m => m[1]), ["", "1", "3", "5", "10"]);
  assert.match(html, /id="stepHint"/);
});

test("changing the step preserves the active stack, metadata, gesture and unrelated settings", () => {
  const saved = {target: "gain", targets: ["gain", "hp"], activeIndex: 1, cycleGesture: "push",
    meta: {gain: {plugin: "untouched", index: 2}}, custom: "keep"};
  const page = inspector(saved);
  assert.equal(page.nodes.stepSize.value, "");
  assert.equal(page.settings(), undefined, "opening the inspector must not rewrite settings");
  for (const size of [1, 3, 5, 10]) {
    page.nodes.stepSize.change(String(size));
    assert.deepEqual(page.settings(), {...saved, stepSize: size});
    assert.equal(page.socket.messages.at(-1).context, "inspector");
    const reopened = inspector(page.settings());
    assert.equal(reopened.nodes.stepSize.value, String(size));
    assert.equal(reopened.settings(), undefined);
  }
  page.nodes.stepSize.change("");
  assert.deepEqual(page.settings(), {...saved, stepSize: null}, "Default must explicitly replace the previous step");
  assert.equal(inspector(page.settings()).nodes.stepSize.value, "");
});

test("invalid saved step sizes display Default without coercing or writing them", () => {
  for (const size of [null, "3", 0, -1, 2, 1.5, true, {}, []]) {
    const page = inspector({target: "gain", stepSize: size});
    assert.equal(page.nodes.stepSize.value, "", JSON.stringify(size));
    assert.equal(page.settings(), undefined);
  }
});

test("stack edits and host refreshes keep the saved step", () => {
  const page = inspector({targets: ["gain", "hp"], stepSize: 5, activeIndex: 1});
  page.socket.onmessage({data: JSON.stringify({event: "sendToPropertyInspector", payload: {levelGroups: []}})});
  assert.equal(page.nodes.stepSize.value, "5");
  page.nodes.stack.children[0].children[1].onclick();
  assert.deepEqual(page.settings(), {targets: ["hp"], stepSize: 5, activeIndex: 0, target: "hp"});
  page.nodes.target.value = "gain";
  page.nodes.target.selectedOptions = [{dataset: {meta: JSON.stringify({plugin: "fixture", index: 0})}}];
  page.nodes.add.onclick();
  assert.deepEqual(page.settings(), {targets: ["hp", "gain"], stepSize: 5, activeIndex: 0, target: "hp",
    meta: {gain: {plugin: "fixture", index: 0}}});
});


test("a step edit keeps stack changes received while the inspector is open", () => {
  const page = inspector({target: "gain", targets: ["gain", "hp"], activeIndex: 0, stepSize: 3});
  const current = {target: "gain", targets: ["gain", "hp"], activeIndex: 1, stepSize: 5,
    cycleGesture: "push", meta: {hp: {plugin: "keep"}}};
  page.socket.onmessage({data: JSON.stringify({event: "didReceiveSettings", context: "dial",
    payload: {settings: current}})});
  assert.equal(page.nodes.stepSize.value, "5");
  assert.equal(page.settings(), undefined, "receiving settings must not write them back");
  page.nodes.stepSize.change("10");
  assert.deepEqual(page.settings(), {...current, stepSize: 10});
});


test("host settings refresh legacy targets and ignore malformed or unrelated updates", () => {
  const page = inspector({target: "gain", stepSize: 3});
  for (const value of [undefined, null, [], "invalid", 10])
    page.socket.onmessage({data: JSON.stringify({event: "didReceiveSettings", context: "dial", payload: {settings: value}})});
  page.socket.onmessage({data: JSON.stringify({event: "didReceiveSettings", context: "other",
    payload: {settings: {target: "hp", stepSize: 10}}})});
  assert.equal(page.nodes.stepSize.value, "3");
  page.socket.onmessage({data: JSON.stringify({event: "didReceiveSettings", context: "dial",
    payload: {settings: {target: "hp", stepSize: null}}})});
  assert.equal(page.nodes.stepSize.value, "");
  assert.equal(page.nodes.stack.children.length, 1);
  assert.equal(page.settings(), undefined);
  page.nodes.stepSize.change("5");
  assert.deepEqual(page.settings(), {target: "hp", targets: ["hp"], stepSize: 5});
});

test("the add button skips an unavailable entry", () => {
  const page = inspector({targets: ["hp"]});
  page.nodes.target.value = "send:gone:monitor";
  page.nodes.target.selectedOptions = [{disabled: true, dataset: {}}];
  page.nodes.add.onclick();
  assert.equal(page.settings(), undefined);
});

// The insert controls the plugin sends again whenever the inserts or the
// plugin catalogue change: the group is rebuilt, the stack keeps its labels
// and the picker keeps its choice.
test("a later list of insert controls replaces the earlier one", async () => {
  const {Element, option} = await import("./inspector-dom.mjs");
  const target = new Element("select"); target.id = "target";
  target.appendChild(option("", "choose…"));
  const device = new Element("optgroup");
  device.appendChild(option("gain", "XLR 1 gain"));
  target.appendChild(device);
  const anchor = new Element("optgroup"); anchor.dataset.layoutAnchor = "";
  target.appendChild(anchor);
  const nodes = {target, add: new Element("button"), stack: new Element("ul"), stepSize: new Element("select")};
  for (const [id, node] of Object.entries(nodes)) node.id = id;
  const all = node => [node, ...node.children.filter(child => typeof child !== "string").flatMap(all)];
  const radios = [new Element("input"), new Element("input")]; radios[0].value = "tap"; radios[1].value = "push";
  const document = {createElement: tag => new Element(tag),
    getElementById: id => Object.values(nodes).flatMap(all).find(node => node.id === id),
    querySelectorAll: () => radios,
    querySelector: selector => selector.endsWith(":checked") ? radios.find(r => r.checked)
      : radios.find(r => selector.includes(`value="${r.value}"`))};
  let socket;
  class Socket {
    messages = [];
    constructor() { socket = this; }
    send(text) { this.messages.push(JSON.parse(text)); }
  }
  const context = vm.createContext({document, WebSocket: Socket, window: {}});
  vm.runInContext(readFileSync(new URL("../com.emaspa.openxlr.sdPlugin/propertyInspector/layout-options.js", import.meta.url), "utf8"), context);
  vm.runInContext(script, context);
  const comp = "insparam|xlr1|ins-3|threshold";
  context.window.connectOpenActionSocket(1234, "inspector", "registerPropertyInspector", "{}",
    JSON.stringify({context: "dial", payload: {settings: {targets: ["gain", comp]}}}));
  socket.onopen();
  const deliver = payload => socket.onmessage({data: JSON.stringify({event: "sendToPropertyInspector", payload})});
  const stack = () => nodes.stack.children.map(li => li.children[0].trim());
  const params = () => target.children.filter(child => child.id === "param-group").map(group => group.children.map(o => o.value));

  // Before the catalogue arrived the insert had no controls to list.
  deliver({inserts: [], chains: [], params: []});
  assert.deepEqual(params(), []);
  assert.deepEqual(stack(), ["XLR 1 gain", comp]);
  // The catalogue arrives: the controls are listed and the stack reads them.
  deliver({inserts: [], chains: [], params: [{target: comp, label: "XLR 1: Comp: Threshold", meta: {plugin: "comp", index: 0}}]});
  assert.deepEqual(params(), [[comp]]);
  assert.deepEqual(stack(), ["XLR 1 gain", "XLR 1: Comp: Threshold"]);
  target.value = comp;
  // A second insert is added while the page is open: one group, both inserts, the choice kept.
  const gate = "insparam|xlr1|ins-4|open";
  deliver({inserts: [], chains: [], params: [{target: comp, label: "XLR 1: Comp: Threshold", meta: {plugin: "comp", index: 0}},
    {target: gate, label: "XLR 1: Gate: Open", meta: {plugin: "gate", index: 1}}]});
  assert.deepEqual(params(), [[comp, gate]]);
  assert.equal(target.value, comp);
  nodes.add.onclick();
  assert.deepEqual(socket.messages.filter(m => m.event === "setSettings").at(-1).payload.meta, {[comp]: {plugin: "comp", index: 0}});
  // The compressor is removed: its control stays chosen as unavailable, and the add button skips it.
  deliver({inserts: [], chains: [], params: [{target: gate, label: "XLR 1: Gate: Open", meta: {plugin: "gate", index: 1}}]});
  assert.deepEqual(params(), [[gate]]);
  assert.equal(target.value, comp);
  assert.equal(target.selectedOptions[0].disabled, true);
  const saves = socket.messages.length;
  nodes.add.onclick();
  assert.equal(socket.messages.length, saves);
});
