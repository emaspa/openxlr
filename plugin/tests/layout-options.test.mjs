import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import vm from "node:vm";
import { layoutChoices, mixShortName } from "../com.emaspa.openxlr.sdPlugin/layout-choices.mjs";

// Minimal selector DOM: exercise selection and group replacement without a
// running OpenDeck instance or changing real action settings.
class Element {
  constructor(tag) { this.tag = tag; this.children = []; this.dataset = {}; this._value = ""; }
  appendChild(child) { child.parent = this; this.children.push(child); }
  remove() { this.parent.children = this.parent.children.filter(child => child !== this); }
  get options() { return this.children.flatMap(child => child.tag === "option" ? [child] : child.options); }
  get value() { return this.tag === "select" ? (this.options.some(o => o.value === this._value) ? this._value : "") : this._value; }
  set value(value) { this._value = value; }
  querySelectorAll() { return this.children.filter(child => child.dataset.layoutGroup); }
}

test("state refresh preserves a selected feed and only replaces layout groups", () => {
  const select = new Element("select");
  select.id = "target";
  const feed = new Element("optgroup"); feed.id = "feed-group";
  const option = new Element("option"); option.value = "feed:alsa_output.test";
  feed.appendChild(option); select.appendChild(feed); select.value = option.value;
  const all = node => [node, ...node.children.flatMap(all)];
  const document = { createElement: tag => new Element(tag), getElementById: id => all(select).find(node => node.id === id) };
  const context = vm.createContext({ document });
  vm.runInContext(readFileSync(new URL("../com.emaspa.openxlr.sdPlugin/propertyInspector/layout-options.js", import.meta.url), "utf8"), context);
  const apply = groups => context.replaceLayoutOptions(groups);
  const state = { channels: [{id:"system",name:"System"}], mixes:[{id:"monitor",name:"Monitor A"},{id:"monitor2",name:"Monitor B"}] };
  apply(layoutChoices(state).toggleGroups);
  assert.equal(select.value, "feed:alsa_output.test");
  assert.equal(document.getElementById("feed-group"), feed);
  select.value = "mixmute:monitor2";
  state.mixes[1].name = "Headset chat";
  apply(layoutChoices(state).toggleGroups);
  assert.equal(select.value, "mixmute:monitor2");
  assert.equal(select.options.find(o => o.value === select.value).textContent, "Headset chat mix mute");
  state.mixes.pop();
  apply(layoutChoices(state).toggleGroups);
  assert.equal(select.value, "mixmute:monitor2");
  assert.equal(document.getElementById("unavailable-layout-target").disabled, true);
  state.mixes.push({id:"monitor2",name:"Monitor B"});
  apply(layoutChoices(state).toggleGroups);
  assert.equal(select.value, "mixmute:monitor2");
  assert.equal(document.getElementById("unavailable-layout-target"), undefined);
  assert.equal(select.options.filter(o => o.value === "mixmute:monitor2").length, 1);
});

test("both structural monitor mixes retain distinct dial labels and choices", () => {
  assert.equal(mixShortName(null, "monitor"), "MonA");
  assert.equal(mixShortName(null, "monitor2"), "MonB");
  const choices = layoutChoices({channels:[{id:"game",name:"Game"}], mixes:[
    {id:"monitor",name:"Monitor A"},{id:"monitor2",name:"Monitor B"},{id:"stream",name:"Stream"}]});
  assert.ok(choices.levelGroups.flatMap(g => g.items).some(i => i.target === "send:game:monitor2"));
  assert.ok(choices.toggleGroups.flatMap(g => g.items).some(i => i.target === "mixmute:monitor"));
});
