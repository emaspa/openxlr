import assert from "node:assert/strict";
import test from "node:test";
import fs from "node:fs";
import {controlFixture} from "./control-fixture.mjs";
import {eqMixer} from "./microphone-eq-fixture.mjs";

const PALETTE = JSON.parse(fs.readFileSync(new URL("../com.emaspa.openxlr.sdPlugin/skin-palettes.json", import.meta.url), "utf8")).default;

// Monitor A, Stream and the microphone with their EQ on and holding the
// profile's bands, and hardware mixing holding Monitor A's EQ off or not.
function state(heldByUnit) {
  const mixer = eqMixer();
  mixer.channels[0] = {...mixer.channels[0], levels: {monitor: 1}, mutedIn: []};
  const eq = {...structuredClone(mixer.channels[0].eq), enabled: true};
  mixer.channels[0].eq = structuredClone(eq);
  mixer.mixes = [{id: "monitor", name: "Monitor A", kind: "monitor", volume: 1, muted: false, eq: structuredClone(eq)},
    {id: "stream", name: "Stream", kind: "virtualMic", volume: 1, muted: false, eq: structuredClone(eq)}];
  Object.assign(mixer, {monitorOutputs: [], monitorFeeds: {}, inserts: {}, lowCutHz: 0,
    hardwareMixing: {wanted: true, active: heldByUnit, reason: null, note: null,
      disabled: {"eq:mix:monitor": "The Wave XLR Pro builds Monitor A for its jacks, so its EQ is off while hardware mixing runs."}}});
  return {type: "state", connected: true, profiles: [], devices: [], capabilities: {xlrInputs: 1}, state: {},
    eqProfiles: [{id: "builtin:clear", name: "Clear", builtin: true, bands: eq.bands}], mixer};
}

// While hardware mixing holds a mix's EQ off the state still reports the
// saved EQ, switched on. The mix's EQ and EQ profile keys grey as insert keys
// on that mix do, and a press only alerts. Another mix and the microphone
// stay as they are.
test("a mix's EQ keys grey while hardware mixing holds its EQ off", async () => {
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
    const push = async held => { daemon.receive(state(held)); await flush(); };
    await push(true);
    const face = context => {
      const image = host.messages.filter(m => m.event === "setImage" && m.context === context).at(-1).payload.image;
      return Buffer.from(image.split(",")[1], "base64").toString();
    };
    // The status lamp: off while the key is unavailable, the on colour while lit.
    const lamp = context => /<circle cx="120" cy="24" r="8" fill="([^"]+)"/.exec(face(context))[1];
    const alerts = context => host.messages.filter(m => m.event === "showAlert" && m.context === context).length;
    const commands = () => daemon.messages.filter(m => m.cmd).length;

    const held = ["eq:mix:monitor", "eqband:mix:monitor:1", "eqtype:mix:monitor:1", "eqprofile:mix:monitor:builtin:clear"];
    const free = ["eq:mix:stream", "eqband:mix:stream:1", "eqprofile:mix:stream:builtin:clear",
      "eq:xlr1", "eqband:xlr1:1", "eqprofile:xlr1:builtin:clear"];
    for (const target of [...held, ...free]) {
      host.receive({event: "willAppear", context: target, action: "com.emaspa.openxlr.toggle", payload: {settings: {target}}});
      host.receive({event: "titleParametersDidChange", context: target, payload: {title: ""}});
    }
    await flush();

    for (const target of held) assert.equal(lamp(target), PALETTE["Ox.Led.Off"], `${target} is grey`);
    for (const target of free.filter(t => !t.startsWith("eqtype:"))) assert.equal(lamp(target), PALETTE["Ox.Led.On"], `${target} is lit`);

    // A press on a greyed key alerts and sends nothing.
    const before = commands();
    for (const target of held) {
      host.receive({event: "keyDown", context: target, payload: {}});
      host.receive({event: "keyUp", context: target, payload: {}});
    }
    await flush();
    assert.equal(commands(), before);
    for (const target of held) assert.equal(alerts(target), 1, target);

    // Software mixing again: the keys show the saved EQ and act.
    await push(false);
    for (const target of held.filter(t => !t.startsWith("eqtype:"))) assert.equal(lamp(target), PALETTE["Ox.Led.On"], `${target} is lit again`);
    assert.equal(lamp("eqtype:mix:monitor:1"), PALETTE["Ox.Text.Muted"], "a type key is dark, not grey");
    host.receive({event: "keyDown", context: "eqprofile:mix:monitor:builtin:clear", payload: {}});
    await flush();
    assert.deepEqual(daemon.messages.at(-1).cmd, "loadEqProfile");
  } finally {
    globalThis.WebSocket = previous.socket;
    globalThis.setInterval = previous.interval;
    for (const interval of intervals) clearInterval(interval);
    if (previous.skin === undefined) delete process.env.OPENXLR_SKIN;
    else process.env.OPENXLR_SKIN = previous.skin;
    control.restore();
  }
});
