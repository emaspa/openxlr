import assert from "node:assert/strict";
import test from "node:test";
import {readFileSync} from "node:fs";
import vm from "node:vm";

const html = readFileSync(new URL("../com.emaspa.openxlr.sdPlugin/propertyInspector/dial.html", import.meta.url), "utf8");
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];

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
