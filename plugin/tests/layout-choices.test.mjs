import assert from "node:assert/strict";
import test from "node:test";

import { channelName, layoutChoices, mixName, controllableOutputs, outputKey } from "../com.emaspa.openxlr.sdPlugin/layout-choices.mjs";

const mixer = {
  mixes: [
    { id: "monitor", name: "My Ears" },
    { id: "broadcast-vod", name: "Broadcast + VOD" },
  ],
  channels: [
    { id: "xlr1", name: "Host Mic" },
    { id: "alerts-new", name: "Alerts & SFX" },
  ],
};

test("editable names are paired with stable ids", () => {
  const choices = layoutChoices(mixer);
  assert.deepEqual(choices.toggleGroups[0].items, [
    { target: "mixmute:monitor", label: "My Ears mix mute" },
    { target: "mixmute:broadcast-vod", label: "Broadcast + VOD mix mute" },
  ]);
  assert.ok(choices.toggleGroups.some((group) => group.items.some((item) =>
    item.target === "sendmute:alerts-new:broadcast-vod" && item.label === "Alerts & SFX in Broadcast + VOD")));
  assert.ok(choices.levelGroups.some((group) => group.items.some((item) =>
    item.target === "send:alerts-new:broadcast-vod")));
  assert.equal(channelName(mixer, "alerts-new"), "Alerts & SFX");
  assert.equal(mixName(mixer, "broadcast-vod"), "Broadcast + VOD");
});

test("deleted layout entries disappear from new choices", () => {
  const afterDelete = {
    mixes: mixer.mixes.filter((entry) => entry.id !== "broadcast-vod"),
    channels: mixer.channels.filter((entry) => entry.id !== "alerts-new"),
  };
  assert.doesNotMatch(JSON.stringify(layoutChoices(afterDelete)), /broadcast-vod|alerts-new/);
});


test("focus keys only target application channels and follow renames", () => {
  const state = { ...mixer, channels: [
    {id:"mic", name:"Mic", hardware:true},
    {id:"capture", name:"Capture", captureSource:"external"},
    {id:"apps", name:"Renamed apps", hardware:false},
  ]};
  assert.deepEqual(layoutChoices(state).toggleGroups.find(g => g.id === "layout-focus").items,
    [{target:"focus:apps", label:"Focused app to Renamed apps"}]);
});


test("output choices reject internal sinks and ambiguous names, retaining monitor masters", () => {
  const state = {...mixer, mixes:[{id:"monitor", kind:"monitor"}, {id:"chat",kind:"virtualMic"}]};
  const devices = ["headset:analog", "OpenXLR_mix_monitor", "OpenXLR_mix_chat", "OpenXLR_ch_system",
    "123", "@DEFAULT_SINK@", "sink#hp1", "-sink", "bad\u0085name"].map(name => ({name,kind:0,isOwn:name.startsWith("OpenXLR")}));
  devices.push({name:"mic",kind:1});
  assert.deepEqual(controllableOutputs(state, devices).map(d => d.name), ["headset:analog","OpenXLR_mix_monitor"]);
  const groups = layoutChoices(state, devices).toggleGroups;
  assert.ok(groups.find(g => g.id === "layout-output-keys").items.some(i => i.target === "outputup:"));
  assert.ok(groups.find(g => g.id === "layout-main-output").items.some(i => i.target === "mainoutput:@monitor"));
  assert.deepEqual(outputKey("outputmute:headset:analog"), {kind:"mute",device:"headset:analog"});
  assert.deepEqual(outputKey("outputdown:"), {kind:"down",device:null});
  assert.equal(outputKey("feed:headset"), null);
});

test("output dials list the system default then every controllable sink, after the mix masters", () => {
  const state = {...mixer, mixes:[{id:"monitor", name:"My Ears", kind:"monitor"}]};
  const devices = [
    {name:"headset:analog", description:"Headset", kind:0},
    {name:"OpenXLR_mix_monitor", description:"", kind:0, isOwn:true},
    {name:"OpenXLR_ch_system", kind:0, isOwn:true},
    {name:"sink#hp1", kind:0},
    {name:"mic", kind:1},
  ];
  const groups = layoutChoices(state, devices).levelGroups;
  assert.equal(groups[0].id, "layout-mix-levels");
  assert.deepEqual(groups[1], {id:"layout-output-levels", label:"Outputs", items:[
    {target:"output:", label:"Current system default"},
    {target:"output:headset:analog", label:"Headset"},
    {target:"output:OpenXLR_mix_monitor", label:"OpenXLR_mix_monitor"},
  ]});
  assert.equal(groups[2].id, "layout-all-sends");
  assert.equal(outputKey("output:headset:analog"), null);
  assert.equal(outputKey("output:"), null);
});
