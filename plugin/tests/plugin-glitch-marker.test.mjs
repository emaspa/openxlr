import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";
import {controlFixture} from "./control-fixture.mjs";
import {GLITCH_TARGET, glitchPress, glitchValue, isGlitchTarget} from "../com.emaspa.openxlr.sdPlugin/glitch-marker.mjs";

const state = () => ({type: "state", connected: true, profiles: [], devices: [], capabilities: {xlrInputs: 1}, state: {},
  mixer: {channels: [{id: "xlr1", name: "XLR 1", microphone: true, hardware: true, levels: {monitor: 1}, mutedIn: []}],
          mixes: [{id: "monitor", name: "Monitor A", kind: "monitor", volume: 1, muted: false}],
          monitorOutputs: [], monitorFeeds: {}, inserts: {}, lowCutHz: 0}});

test("the glitch target is momentary, available only with a daemon, and sends one labelled command", () => {
  assert.equal(GLITCH_TARGET, "markGlitch");
  assert.ok(isGlitchTarget("markGlitch"));
  assert.ok(!isGlitchTarget("mute"));
  assert.deepEqual(glitchPress(), {cmd: "markGlitch", label: "Deck"});
  assert.equal(glitchValue(true), false);
  assert.equal(glitchValue(false), null);
});

test("the toggle inspector offers the glitch key", () => {
  const html = fs.readFileSync(new URL("../com.emaspa.openxlr.sdPlugin/propertyInspector/toggle.html", import.meta.url), "utf8");
  assert.match(html, /<optgroup label="Diagnostics">\s*<option value="markGlitch">/);
});

// The key against a fake OpenDeck host and a fake daemon: every press is one
// markGlitch with a request id, the daemon's answer is the host's check mark
// or alert, and a press while the daemon is away sends nothing.
test("a glitch key marks a glitch per press and shows whether the daemon took it", async () => {
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
    const marks = () => daemon.messages.filter(m => m.cmd === "markGlitch");
    const shown = (event) => host.messages.filter(m => m.event === event && m.context === "glitch").length;
    const press = async () => {
      host.receive({event: "keyDown", context: "glitch", payload: {}}); await flush();
      host.receive({event: "keyUp", context: "glitch", payload: {}}); await flush();
    };
    const answer = async (error = null) => {
      daemon.receive({type: "commandResult", requestId: marks().at(-1).requestId, error});
      await flush();
    };

    // Before the daemon connects the key is grey and a press only alerts.
    host.receive({event: "willAppear", context: "glitch", action: "com.emaspa.openxlr.toggle", payload: {settings: {target: "markGlitch"}}});
    host.receive({event: "titleParametersDidChange", context: "glitch", payload: {title: ""}});
    await press();
    assert.equal(marks().length, 0);
    assert.equal(shown("showAlert"), 1);

    daemon.onopen(); await flush();
    daemon.receive(state()); await flush();
    const image = host.messages.filter(m => m.event === "setImage" && m.context === "glitch").at(-1).payload.image;
    const svg = Buffer.from(image.split(",")[1], "base64").toString("utf8");
    assert.match(svg, />Mark<\/text>.*>Glitch<\/text>/s);

    await press();
    assert.equal(marks().length, 1);
    const {requestId, ...sent} = marks()[0];
    assert.deepEqual(sent, {cmd: "markGlitch", label: "Deck"});
    assert.ok(requestId);
    await answer();
    assert.equal(shown("showOk"), 1);

    // A second press is a second marker, not a repeat to swallow once the first was answered.
    await press();
    assert.equal(marks().length, 2);
    assert.notEqual(marks()[1].requestId, requestId);
    // An older daemon that does not know the command: the key alerts.
    await answer("unknown cmd 'markGlitch'");
    assert.equal(shown("showAlert"), 2);
  } finally {
    globalThis.WebSocket = previous.socket;
    globalThis.setInterval = previous.interval;
    for (const interval of intervals) clearInterval(interval);
    if (previous.skin === undefined) delete process.env.OPENXLR_SKIN;
    else process.env.OPENXLR_SKIN = previous.skin;
    control.restore();
  }
});
