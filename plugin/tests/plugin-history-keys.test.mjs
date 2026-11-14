import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";
import {controlFixture} from "./control-fixture.mjs";
import {historyLabel, historyPress, historyStep, historyValue, isHistoryTarget, shortStep} from "../com.emaspa.openxlr.sdPlugin/history-keys.mjs";

const PALETTE = JSON.parse(fs.readFileSync(new URL("../com.emaspa.openxlr.sdPlugin/skin-palettes.json", import.meta.url), "utf8")).default;
const step = (id, label, kind = "sendLevel") => ({step: id, kind, channel: "music", mix: "monitor", group: null, count: 1, label});

test("the undo and redo targets name the step on their side and grey out without one", () => {
  assert.ok(isHistoryTarget("undo"));
  assert.ok(isHistoryTarget("redo"));
  assert.ok(!isHistoryTarget("markGlitch"));
  const state = {history: {undo: step(7, "Music level in Monitor"), redo: null, undoSteps: 3, redoSteps: 0, note: null}};
  assert.equal(historyStep(state, "undo").step, 7);
  assert.equal(historyStep(state, "redo"), null);
  assert.equal(historyValue(state, "undo"), false);
  assert.equal(historyValue(state, "redo"), null);
  // An older daemon keeps no history: both keys are grey.
  assert.equal(historyValue({}, "undo"), null);
  assert.equal(historyValue(null, "redo"), null);
});

test("a press carries the id of the step the key shows", () => {
  assert.deepEqual(historyPress(step(7, "x"), "undo"), {cmd: "undo", step: 7});
  assert.deepEqual(historyPress(step(12, "x"), "redo"), {cmd: "redo", step: 12});
  assert.deepEqual(historyPress({label: "x"}, "undo"), {cmd: "undo"});
  assert.deepEqual(historyPress({step: 0}, "undo"), {cmd: "undo"});
  assert.deepEqual(historyPress({step: "7"}, "undo"), {cmd: "undo"});
});

test("the label puts the verb over the first words of the step that fit a key line", () => {
  assert.equal(shortStep("Music volume"), "Music volume");
  assert.equal(shortStep("XLR 1 level in Monitor A"), "XLR 1 level…");
  assert.equal(shortStep("XLR 1 level in Monitor A and 4 more"), "XLR 1 level…");
  assert.equal(shortStep("Monitor A volume"), "Monitor A…");
  assert.equal(shortStep("Extraordinarily long name"), "Extraordina…");
  assert.equal(shortStep(""), "");
  assert.equal(historyLabel(step(7, "Game mute in Stream"), "undo"), "Undo\nGame mute…");
  assert.equal(historyLabel(step(8, "Mics switch"), "redo"), "Redo\nMics switch");
  assert.equal(historyLabel(null, "undo"), "Undo");
  assert.equal(historyLabel(null, "redo"), "Redo");
});

test("the toggle inspector offers both keys", () => {
  const html = fs.readFileSync(new URL("../com.emaspa.openxlr.sdPlugin/propertyInspector/toggle.html", import.meta.url), "utf8");
  assert.match(html, /<optgroup label="History">\s*<option value="undo">[^<]+<\/option>\s*<option value="redo">/);
});

// The keys against a fake OpenDeck host and a fake daemon: a press flushes
// the dial turn still waiting before the undo and then sends no step id,
// since that turn is the newest step; otherwise it carries the step it
// showed, and the dials then follow the restored values at once.
test("an undo key sends pending dial turns first and shows what the daemon answered", async t => {
  t.mock.timers.enable({apis: ["Date", "setTimeout"]});
  const previous = {socket: globalThis.WebSocket, interval: globalThis.setInterval, skin: process.env.OPENXLR_SKIN};
  const sockets = [], intervals = [];
  process.env.OPENXLR_SKIN = "default";
  globalThis.setInterval = (...args) => { const timer = previous.interval(...args); intervals.push(timer); return timer; };
  class Socket {
    static OPEN = 1;
    readyState = 1;
    messages = [];
    constructor() { sockets.push(this); }
    send(text) { this.messages.push(JSON.parse(text)); }
    receive(message) { this.onmessage({data: JSON.stringify(message)}); }
  }
  globalThis.WebSocket = Socket;
  const control = controlFixture();
  try {
    await import("../com.emaspa.openxlr.sdPlugin/plugin.mjs");
    const daemon = control.children[0], host = sockets[0];
    daemon.onopen();
    const state = {type: "state", connected: true, profiles: [], activeProfile: null,
      capabilities: {xlrInputs: 1}, state: {}, devices: [],
      mixer: {channels: [{id: "music", name: "Music", volume: .5, levels: {monitor: .5}, mutedIn: []}],
        mixes: [{id: "monitor", name: "Monitor", kind: "monitor", volume: 1}], inserts: {}}};
    const appear = (context, action, target) => host.receive({event: "willAppear", context,
      action: `com.emaspa.openxlr.${action}`, payload: {settings: {target}}});
    const svg = context => {
      const image = host.messages.filter(m => m.event === "setImage" && m.context === context).at(-1).payload.image;
      return Buffer.from(image.split(",")[1], "base64").toString("utf8");
    };
    const shown = (event, context) => host.messages.filter(m => m.event === event && m.context === context).length;
    const sent = () => daemon.messages.filter(m => m.cmd !== "listPlugins").map(({requestId, ...m}) => m);
    const press = context => host.receive({event: "keyDown", context});

    appear("undo", "toggle", "undo");
    appear("redo", "toggle", "redo");
    appear("channel", "dial", "chvol:music");
    for (const context of ["undo", "redo"])
      host.receive({event: "titleParametersDidChange", context, payload: {title: ""}});

    // An older daemon without a history: both keys grey, a press only alerts.
    daemon.receive(state);
    assert.match(svg("undo"), />Undo<\/text>/);
    assert.ok(svg("undo").includes(`stroke="${PALETTE["Ox.Text.Muted"]}"`), "grey glyph without a history");
    press("undo");
    assert.equal(shown("showAlert", "undo"), 1);
    assert.deepEqual(sent(), []);

    state.history = {undo: step(7, "Music level in Monitor"), redo: null, undoSteps: 1, redoSteps: 0, note: null};
    daemon.receive(state);
    const undoKey = svg("undo");
    assert.match(undoKey, />Undo<\/text>.*>Music level…<\/text>/s);
    assert.ok(undoKey.includes(`<path d="M45 57 h32 a22 22 0 0 1 0 44 h-28" fill="none" stroke="${PALETTE["Ox.Text.Primary"]}"`),
      "the undo glyph in the usable ink");
    assert.ok(svg("redo").includes(`<path d="M99 57 h-32 a22 22 0 0 0 0 44 h28" fill="none" stroke="${PALETTE["Ox.Text.Muted"]}"`),
      "an empty redo side is grey");
    press("redo");
    assert.equal(shown("showAlert", "redo"), 1);
    assert.deepEqual(sent(), []);

    // Two ticks: the first goes out at once, the second waits for its 80 ms.
    const rotate = () => host.receive({event: "dialRotate", context: "channel", payload: {ticks: 1}});
    rotate(); rotate();
    press("undo");
    t.mock.timers.tick(81);
    assert.deepEqual(sent(), [
      {cmd: "setChannelVolume", channel: "music", value: .51},
      {cmd: "setChannelVolume", channel: "music", value: .52},
      {cmd: "undo"},
    ], "the waiting turn reaches the daemon before the undo, once, and the undo takes it without the older step's id");
    const undo = daemon.messages.at(-1);
    assert.ok(undo.requestId, "the press waits for the daemon's answer");
    daemon.receive({type: "commandResult", requestId: undo.requestId, error: null});
    assert.equal(shown("showOk", "undo"), 1);

    // The undo's state moves the dial straight away: its local value is gone.
    state.mixer.channels[0].volume = .5;
    state.history = {undo: null, redo: step(7, "Music volume", "channelVolume"), undoSteps: 0, redoSteps: 1, note: null};
    daemon.receive(state);
    const feedback = host.messages.filter(m => m.event === "setFeedback" && m.context === "channel").at(-1).payload;
    assert.equal(feedback.value, "50%");
    assert.match(svg("redo"), />Redo<\/text>.*>Music volume<\/text>/s);
    assert.ok(svg("undo").includes(`stroke="${PALETTE["Ox.Text.Muted"]}"`), "an empty undo side is grey");

    // Redo carries its own step; a history changed by another client is refused and alerts.
    press("redo");
    const redo = daemon.messages.at(-1);
    assert.deepEqual({cmd: redo.cmd, step: redo.step}, {cmd: "redo", step: 7});
    daemon.receive({type: "commandResult", requestId: redo.requestId, error: "redo: the history changed"});
    assert.equal(shown("showAlert", "redo"), 2);
  } finally {
    control.restore();
    for (const timer of intervals) clearInterval(timer);
    globalThis.WebSocket = previous.socket;
    globalThis.setInterval = previous.interval;
    if (previous.skin === undefined) delete process.env.OPENXLR_SKIN;
    else process.env.OPENXLR_SKIN = previous.skin;
  }
});
