import assert from "node:assert/strict";
import test from "node:test";
import {controlFixture} from "./control-fixture.mjs";

// OpenDeck's icon editor hands itself a fresh copy of the action after every
// setImage or setFeedback, and each fresh copy is saved and sent back to the
// plugin as titleParametersDidChange. A plugin that redraws on every title
// change keeps the editor in a loop that swallows keystrokes (#834). The fake
// host here answers each redraw the way that editor does, up to a cap.
test("an open icon editor and the plugin's redraws do not feed each other", async () => {
  const oldSkin = process.env.OPENXLR_SKIN;
  process.env.OPENXLR_SKIN = "default";
  const previous = globalThis.WebSocket;
  const previousInterval = globalThis.setInterval;
  const intervals = [];
  globalThis.setInterval = (...args) => {
    const interval = previousInterval(...args);
    intervals.push(interval);
    return interval;
  };
  const bridge = controlFixture();
  const sockets = [];
  const titles = new Map();   // context -> the title the editor holds
  let editing = null, echoes = 0;
  class Socket {
    static OPEN = 1;
    readyState = 1;
    messages = [];
    constructor(url) { this.url = url; sockets.push(this); }
    send(text) {
      const m = JSON.parse(text);
      this.messages.push(m);
      if (m.context === editing && (m.event === "setImage" || m.event === "setFeedback") && echoes++ < 50)
        this.receive({event: "titleParametersDidChange", context: m.context, payload: {title: titles.get(m.context) ?? ""}});
    }
    receive(message) { this.onmessage({data: JSON.stringify(message)}); }
  }
  globalThis.WebSocket = Socket;
  try {
    await import("../com.emaspa.openxlr.sdPlugin/plugin.mjs");
    const daemon = bridge.children[0];
    const host = sockets[0];
    daemon.onopen();
    const state = {type: "state", connected: true, profiles: [], devices: [],
      capabilities: {gain: true, gainMaxDb: 75, xlrInputs: 1},
      state: {gainDb: 40, muted: false},
      mixer: {
        channels: [{id: "browser", name: "Browser", levels: {monitor: .5}, mutedIn: []}],
        mixes: [{id: "monitor", name: "Monitor", kind: "monitor", volume: 1}],
        inserts: {}, lowCutHz: 0,
      }};
    daemon.receive(state);
    host.receive({event: "willAppear", context: "dial", action: "com.emaspa.openxlr.dial",
      payload: {settings: {target: "send:browser:monitor"}, controller: "Encoder"}});
    host.receive({event: "willAppear", context: "key", action: "com.emaspa.openxlr.toggle",
      payload: {settings: {target: "mute:browser:monitor"}}});
    for (const context of ["dial", "key"])
      host.receive({event: "titleParametersDidChange", context, payload: {title: ""}});
    const sent = (context) => host.messages.filter(m => m.context === context &&
      (m.event === "setImage" || m.event === "setFeedback"));

    // Typing a label on the dial, one key at a time, and moving it to the top.
    editing = "dial"; echoes = 0;
    const before = sent("dial").length;
    for (const title of ["B", "Br", "Bro", "Browser"]) {
      titles.set("dial", title);
      host.receive({event: "titleParametersDidChange", context: "dial", payload: {title, titleAlignment: "middle"}});
    }
    host.receive({event: "titleParametersDidChange", context: "dial", payload: {title: "Browser", titleAlignment: "top"}});
    assert.equal(sent("dial").length, before, "a dial's strip does not show the OpenDeck title");

    // A key redraws once, when its title first appears, and the editor's echo stops there.
    editing = "key"; echoes = 0;
    const keyBefore = sent("key").length;
    for (const title of ["B", "Br", "Bro"]) {
      titles.set("key", title);
      host.receive({event: "titleParametersDidChange", context: "key", payload: {title}});
    }
    assert.equal(sent("key").length - keyBefore, 1);
    assert.ok(echoes < 5, `the editor echoed ${echoes} times`);

    // Clearing the title puts the key's own label back.
    titles.set("key", "");
    host.receive({event: "titleParametersDidChange", context: "key", payload: {title: ""}});
    assert.equal(sent("key").length - keyBefore, 2);

    // A daemon state that changes nothing on a dial or key sends nothing.
    editing = null;
    const all = host.messages.length;
    daemon.receive(state);
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(host.messages.slice(all).filter(m => m.event === "setImage" || m.event === "setFeedback"), []);

    // A change still reaches the dial.
    state.mixer.channels[0].levels.monitor = .25;
    daemon.receive(state);
    await new Promise(resolve => setImmediate(resolve));
    assert.ok(host.messages.slice(all).some(m => m.context === "dial" && m.event === "setFeedback"));
  } finally {
    bridge.restore();
    for (const interval of intervals) clearInterval(interval);
    globalThis.setInterval = previousInterval;
    globalThis.WebSocket = previous;
    if (oldSkin === undefined) delete process.env.OPENXLR_SKIN; else process.env.OPENXLR_SKIN = oldSkin;
  }
});
