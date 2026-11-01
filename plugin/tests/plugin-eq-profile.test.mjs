import assert from "node:assert/strict";
import test from "node:test";
import {controlFixture} from "./control-fixture.mjs";

const Q = 0.7046930689671475;
const band = (type, frequency, gain = 0, q = Q, enabled = true) => ({enabled, type, frequency, gain, q});
const warm = [band("lowCut", 60), band("lowShelf", 200, 2), band("peak", 3000, 1, 1.0046157902783952), band("highShelf", 8000, -2)];
const six = Array.from({length: 6}, (_, i) => band("peak", 200 * (i + 1), 1));
const limits = {bands: 4, minBands: 1, maxBands: 16, types: ["lowCut", "lowShelf", "peak", "notch", "highShelf", "highCut"],
  frequency: {min: 20, max: 20000, step: 1}, gain: {min: -12, max: 12, step: 0.25}, q: {min: 0.1, max: 10, log10Step: 0.016}};
const off = {enabled: false, bands: [band("lowCut", 36), band("peak", 310), band("peak", 2172), band("highCut", 15049, 0, Q, false)]};
const state = (eq = off, maxBands = 16) => ({type: "state", connected: true, profiles: [], devices: [], capabilities: {xlrInputs: 1}, state: {},
  eqProfiles: [{id: "builtin:warm", name: "Warm", builtin: true, bands: warm}, {id: "saved:Six", name: "Six", builtin: false, bands: six}],
  mixer: {channels: [{id: "xlr1", name: "Main mic", microphone: true, hardware: true, levels: {monitor: 1}, mutedIn: [],
                      appearance: {icon: "", defaultIcon: "mic", colour: "#12ab34"}, eq}],
          mixes: [{id: "monitor", name: "Monitor A", kind: "monitor", volume: 1, muted: false}],
          eqLimits: {...limits, maxBands}, monitorOutputs: [], monitorFeeds: {}, inserts: {}, lowCutHz: 0}});

// The EQ profile key against a fake OpenDeck host and a fake daemon: the
// inspector's choices, a press that loads, the key lit from the state alone,
// and a press refused for a band count the microphone does not take.
test("an EQ profile key loads its profile with one command and lights while the EQ holds it", async () => {
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
    const push = async (...args) => { daemon.receive(state(...args)); await flush(); };
    await push();
    const appear = (context, settings) => {
      host.receive({event: "willAppear", context, action: "com.emaspa.openxlr.toggle", payload: {settings}});
      host.receive({event: "titleParametersDidChange", context, payload: {title: ""}});
    };
    const sent = () => daemon.messages.filter(m => m.cmd === "loadEqProfile").map(({requestId, ...rest}) => rest);
    const answer = async () => {
      daemon.receive({type: "commandResult", requestId: daemon.messages.at(-1).requestId, error: null});
      await flush();
    };
    const face = context => {
      const image = host.messages.filter(m => m.event === "setImage" && m.context === context).at(-1).payload.image;
      return Buffer.from(image.split(",")[1], "base64").toString();
    };
    const lit = context => face(context).includes("url(#bloom)");
    const alerts = context => host.messages.filter(m => m.event === "showAlert" && m.context === context).length;
    const press = async context => {
      host.receive({event: "keyDown", context, payload: {}}); await flush();
      host.receive({event: "keyUp", context, payload: {}}); await flush();
    };

    // The inspector lists each profile for the microphone, built-in ones marked.
    host.receive({event: "sendToPlugin", context: "inspector", payload: {request: "layout"}});
    const layout = host.messages.filter(m => m.event === "sendToPropertyInspector" && m.context === "inspector").at(-1).payload;
    const group = layout.toggleGroups.find(g => g.id === "layout-eq-profiles-xlr1");
    assert.deepEqual(group.items, [
      {target: "eqprofile:xlr1:builtin:warm", label: "Main mic EQ: Warm (built in)"},
      {target: "eqprofile:xlr1:saved:Six", label: "Main mic EQ: Six"}]);

    appear("warm", {target: "eqprofile:xlr1:builtin:warm"});
    appear("six", {target: "eqprofile:xlr1:saved:Six"});
    appear("gone", {target: "eqprofile:xlr1:saved:Gone"});
    assert.equal(lit("warm"), false);
    assert.match(face("warm"), />Main mic</);
    assert.match(face("warm"), />Warm</);
    assert.match(face("warm"), /#12ab34/);
    assert.match(face("gone"), />EQ profile</);

    // A press: one loadEqProfile; the key lights when the state shows the EQ holding it.
    await press("warm");
    assert.deepEqual(sent(), [{cmd: "loadEqProfile", channel: "xlr1", profile: "builtin:warm"}]);
    await answer();
    assert.equal(alerts("warm"), 0);
    await push({enabled: true, bands: warm});
    assert.equal(lit("warm"), true);
    assert.equal(lit("six"), false);
    // A band moved elsewhere: no longer the profile.
    await push({enabled: true, bands: [...warm.slice(0, 3), band("highShelf", 8000, -1)]});
    assert.equal(lit("warm"), false);

    // A profile the microphone's EQ cannot take, or one the daemon no longer lists: alert, nothing sent.
    await push(off, 4);
    const count = sent().length;
    await press("six");
    await press("gone");
    assert.equal(sent().length, count);
    assert.equal(alerts("six"), 1);
    assert.equal(alerts("gone"), 1);
  } finally {
    globalThis.WebSocket = previous.socket;
    globalThis.setInterval = previous.interval;
    for (const interval of intervals) clearInterval(interval);
    if (previous.skin === undefined) delete process.env.OPENXLR_SKIN;
    else process.env.OPENXLR_SKIN = previous.skin;
    control.restore();
  }
});
