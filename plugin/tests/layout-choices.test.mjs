import assert from "node:assert/strict";
import test from "node:test";

import { channelName, layoutChoices, mixName, defaultMonitorMix, monitorFeed, controllableOutputs, outputKey, targetAppearance, iconPath } from "../com.emaspa.openxlr.sdPlugin/layout-choices.mjs";

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

test("microphone controls follow primary and additional carriers by channel id", () => {
  const state = {...mixer, channels:[
    {id:"primary", name:"Pro mic", captureSource:"old.primary.node", microphone:true, chainWidth:1,
      binding:{unit:"pro"}, unitInput:{unit:"pro", input:2, primary:true}},
    {id:"guest", name:"Guest", captureSource:"old.guest.node", microphone:true, chainWidth:1,
      binding:{unit:"wave3"}, unitInput:{unit:"wave3", input:1, primary:false}},
    {id:"usb", name:"USB mic", microphone:true, chainWidth:1},
  ]};
  const choices = layoutChoices(state);
  assert.deepEqual(choices.levelGroups.find(g => g.id === "layout-microphone-gains"), {
    id:"layout-microphone-gains", label:"Microphone controls", items:[
      {target:"chgain:primary", label:"Pro mic gain"}, {target:"chgain:guest", label:"Guest gain"},
    ]});
  assert.deepEqual(choices.toggleGroups.find(g => g.id === "layout-microphone-controls").items.map(i => i.target),
    ["chmute:primary", "chphantom:primary", "chlowcut:primary", "chclipguard:primary",
      "chmute:guest", "chphantom:guest", "chlowcut:guest", "chclipguard:guest"]);
  state.channels[1].name = "Visitor";
  state.channels[1].appearance = {icon:"●", colour:"#123456"};
  assert.deepEqual(layoutChoices(state).levelGroups.find(g => g.id === "layout-microphone-gains").items[1],
    {target:"chgain:guest", label:"Visitor gain"});
  for (const target of ["chgain:guest", "chmute:guest", "chphantom:guest", "chlowcut:guest", "chclipguard:guest"])
    assert.deepEqual(targetAppearance(state, target), {icon:"●", path:null, colour:"#123456"});
  state.channels.splice(1, 1);
  assert.doesNotMatch(JSON.stringify(layoutChoices(state)), /chgain:guest|chmute:guest/);
});

test("older state without capture fields keeps layout choices without microphone controls", () => {
  const choices = layoutChoices({...mixer, channels:[...mixer.channels, {id:"capture", name:"Capture", captureSource:"usb"}]});
  assert.equal(choices.toggleGroups.find(g => g.id === "layout-microphone-controls"), undefined);
  assert.equal(choices.levelGroups.find(g => g.id === "layout-microphone-gains"), undefined);
  assert.ok(choices.levelGroups.some(g => g.items.some(i => i.target === "send:capture:all")));
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

test("exclusive groups offer one next-member key each", () => {
  assert.equal(layoutChoices(mixer).toggleGroups.find(g => g.id === "layout-exclusive-groups"), undefined);
  const grouped = { ...mixer, exclusiveGroups: [{ id: "mics", name: "Microphones", channels: ["xlr1", "alerts-new"] }] };
  assert.deepEqual(layoutChoices(grouped).toggleGroups.find(g => g.id === "layout-exclusive-groups").items,
    [{ target: "group:mics", label: "Microphones: next member" }]);
});

test("a key's appearance follows its stable target and admits only listed icons and #RRGGBB colours", () => {
  const state = {
    channels: [{id: "music", appearance: {icon: "♫", colour: "#12abEF", hidden: true}}],
    mixes: [{id: "stream", appearance: {icon: "◆", colour: "#ABC123"}}],
  };
  for (const target of ["focus:music", "sendmute:music:stream", "send:music:stream", "insert|music|id"])
    assert.deepEqual(targetAppearance(state, target), {icon: "♫", path: null, colour: "#12abEF"});
  for (const target of ["mixmute:stream", "mixvol:stream", "inschain|mix:stream", "insparam|mix:stream|id|0"])
    assert.deepEqual(targetAppearance(state, target), {icon: "◆", path: null, colour: "#ABC123"});
  state.channels[0].appearance = {icon: '<image href="file:///etc/passwd"/>', colour: '#fff" onload="alert(1)'};
  assert.deepEqual(targetAppearance(state, "focus:music"), {icon: "", path: null, colour: null});
  assert.deepEqual(targetAppearance(state, "focus:gone"), {icon: "", path: null, colour: null});
  assert.deepEqual(targetAppearance(state, "gain"), {icon: "", path: null, colour: null});
  assert.deepEqual(targetAppearance(null, undefined), {icon: "", path: null, colour: null});

  // A group key wears the look of the member that is heard.
  state.channels = [
    {id: "mic", levels: {stream: 1}, mutedIn: ["stream"], appearance: {icon: "●", colour: "#111111"}},
    {id: "headset", levels: {stream: 1}, mutedIn: [], appearance: {icon: "▶", colour: "#222222"}},
  ];
  state.exclusiveGroups = [{id: "voices", name: "Voices", channels: ["mic", "headset"]}];
  assert.deepEqual(targetAppearance(state, "group:voices"), {icon: "▶", path: null, colour: "#222222"});
  state.channels[1].mutedIn = ["stream"];
  assert.deepEqual(targetAppearance(state, "group:voices"), {icon: "", path: null, colour: null});
  assert.deepEqual(targetAppearance(state, "group:gone"), {icon: "", path: null, colour: null});
});

test("a named icon draws the built-in path, an unknown or empty one the default, and none draws nothing", () => {
  const music = iconPath("music"), apps = iconPath("apps");
  assert.match(music, /^M[0-9MmLlHhVvCcSsQqTtAaZz .,-]+$/);
  const state = {channels: [{id: "c", appearance: {icon: "music", defaultIcon: "apps"}}]};
  assert.deepEqual(targetAppearance(state, "focus:c"), {icon: "", path: music, colour: null});
  state.channels[0].appearance = {icon: "", defaultIcon: "apps"};
  assert.equal(targetAppearance(state, "focus:c").path, apps);
  state.channels[0].appearance = {icon: "a-skin-only-icon", defaultIcon: "apps"};
  assert.equal(targetAppearance(state, "focus:c").path, apps);
  state.channels[0].appearance = {icon: "none", defaultIcon: "apps"};
  assert.deepEqual(targetAppearance(state, "focus:c"), {icon: "", path: null, colour: null});
  // Only the plugin's own table reaches the SVG, never a path from the state.
  state.channels[0].appearance = {icon: "", defaultIcon: '"/><script/>'};
  assert.equal(targetAppearance(state, "focus:c").path, null);
  assert.equal(iconPath("__proto__"), null);
});

test("default monitor follows the explicit daemon default or older daemon layout order", () => {
  const mixes = [{id:"stream",kind:"virtualMic"}, {id:"monitor2",kind:"monitor"}, {id:"aaa",kind:"monitor"}];
  assert.equal(defaultMonitorMix({mixes, defaultMonitorMix:"monitor2"}), "monitor2");
  assert.equal(defaultMonitorMix({mixes:[...mixes].reverse(), defaultMonitorMix:"monitor2"}), "monitor2");
  assert.equal(defaultMonitorMix({mixes}), "monitor2");
  assert.equal(defaultMonitorMix({mixes:[...mixes].reverse()}), "aaa");
  assert.equal(defaultMonitorMix({mixes:[...mixes, {id:"monitor",kind:"monitor"}]}), "monitor2");
  assert.equal(defaultMonitorMix({mixes, defaultMonitorMix:"stream"}), "monitor2");
  assert.equal(defaultMonitorMix({mixes:[{id:"monitor2"},{id:"monitor"}]}), "monitor2");
});


test("saved feed ids follow the daemon after whitespace, renames and deletions", () => {
  const state = { mixes: [{id:"monitor2", name:"Renamed", kind:"monitor"}, {id:"chat", kind:"virtualMic"}],
    defaultMonitorMix:"monitor2", monitorFeeds:{} };
  for (const [saved, expected] of [[undefined,"monitor2"], ["","monitor2"], ["gone","monitor2"],
    [" gone + chat ","chat"], [" chat + monitor2 + chat ","monitor2+chat"]]) {
    state.monitorFeeds.output = saved;
    assert.equal(monitorFeed(state, "output"), expected);
  }
  assert.equal(monitorFeed(undefined, "output"), "monitor");
  for (const output of ["constructor", "__proto__", "toString"]) {
    assert.equal(monitorFeed({monitorFeeds:{}}, output), "monitor");
  }
});
