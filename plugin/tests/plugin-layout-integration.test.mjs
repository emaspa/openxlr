import assert from "node:assert/strict";
import test from "node:test";

test("plugin publishes layout updates and keeps monitor feed commands intact", async () => {
  const previous = globalThis.WebSocket;
  const previousInterval = globalThis.setInterval;
  const intervals = [];
  globalThis.setInterval = (...args) => {
    const interval = previousInterval(...args);
    intervals.push(interval);
    return interval;
  };
  const sockets = [];
  class Socket {
    static OPEN = 1;
    readyState = 1;
    messages = [];
    constructor(url) { this.url = url; sockets.push(this); }
    send(text) { this.messages.push(JSON.parse(text)); }
    receive(message) { this.onmessage({data: JSON.stringify(message)}); }
  }
  globalThis.WebSocket = Socket;
  try {
    await import("../com.emaspa.openxlr.sdPlugin/plugin.mjs");
    const daemon = sockets.find(socket => socket.url.includes(":37890/"));
    const host = sockets.find(socket => socket !== daemon);
    daemon.onopen();
    const state = {type:"state", profiles:[], devices:[{kind:0,name:"qa-output",description:"QA speakers"}], mixer:{
      channels:[{id:"system",name:"Desktop",levels:{monitor:1,monitor2:1}}],
      mixes:[{id:"monitor",name:"Monitor A"},{id:"monitor2",name:"Monitor B"}],
      monitorOutputs:["qa-output"], monitorFeeds:{}, inserts:{}
    }};
    daemon.receive(state);
    host.receive({event:"sendToPlugin",context:"qa",payload:{request:"layout"}});
    assert.ok(host.messages.at(-1).payload.levelGroups.flatMap(g => g.items)
      .some(item => item.target === "send:system:monitor2"));
    host.receive({event:"willAppear",context:"feed-key",action:"com.emaspa.openxlr.toggle",payload:{settings:{target:"feed:qa-output"}}});
    host.receive({event:"keyDown",context:"feed-key"});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"setMonitorFeed",device:"qa-output",mix:"monitor2"});
    state.mixer.monitorFeeds["qa-output"] = "monitor2";
    state.mixer.channels[0].name = "Renamed Desktop";
    daemon.receive(state);
    const update = host.messages.filter(message => message.event === "sendToPropertyInspector" && message.context === "qa").at(-1);
    assert.ok(update.payload.levelGroups.flatMap(g => g.items).some(item => item.label === "Renamed Desktop in Monitor B"));
    host.receive({event:"keyDown",context:"feed-key"});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"setMonitorFeed",device:"qa-output",mix:"monitor+monitor2"});
    host.receive({event:"propertyInspectorDidDisappear",context:"qa"});
    const count = host.messages.filter(m => m.event === "sendToPropertyInspector").length;
    state.mixer.channels[0].name = "Another name";
    daemon.receive(state);
    assert.equal(host.messages.filter(m => m.event === "sendToPropertyInspector").length, count);
  }
  finally {
    intervals.forEach(clearInterval);
    globalThis.setInterval = previousInterval;
    globalThis.WebSocket = previous;
  }
});
