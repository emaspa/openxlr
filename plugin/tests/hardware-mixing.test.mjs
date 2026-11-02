import assert from "node:assert/strict";
import test from "node:test";
import {controlFixture} from "./control-fixture.mjs";

// While the Wave XLR Pro builds the mixes its jacks play, no insert runs on
// them there. The daemon names those chains in hardwareMixing.disabled, and
// the deck greys the keys and dial presses on them by the same map the UI
// and the terminal mixer use: a press alerts and sends nothing. A chain on a
// mix this computer still builds acts as before.
test("insert keys on a mix the unit builds are greyed by the daemon's map", async () => {
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
    const daemon = bridge.children[0];
    const host = sockets[0];
    daemon.onopen();
    const chain = (id) => [{id, kind:"lv2", plugin:"urn:test", label:"Comp", bypass:false, params:{}}];
    const state = (mode) => ({type:"state", connected:true, profiles:[],
      capabilities:{gain:true, mute:true, xlrInputs:2, hardwareMixing:true},
      state:{gainDb:40, mute:false},
      devices:[],
      mixer:{
        channels:[{id:"game",name:"Game",levels:{monitor:1,stream:1},mutedIn:[],volume:1}],
        mixes:[{id:"monitor",name:"Monitor A",kind:"monitor",volume:1,hardwareSection:mode.active ? 0 : null},
               {id:"stream",name:"Stream",kind:"virtualMic",volume:1}],
        monitorOutputs:[], monitorFeeds:{}, lowCutHz:0, auxPortEnabled:false,
        inserts:{"mix:monitor":chain("a"), "mix:stream":chain("b")},
        hardwareMixing:mode,
      }});
    const alerted = (context) => host.messages.filter(m => m.event === "showAlert" && m.context === context).length;
    const key = (context, target) => {
      host.receive({event:"willAppear",context,action:"com.emaspa.openxlr.toggle",payload:{settings:{target}}});
      host.receive({event:"titleParametersDidChange",context,payload:{title:""}});
    };

    const running = {wanted:true, active:true, reason:null, note:null,
      disabled:{"inserts:mix:monitor":"The Wave XLR Pro builds Monitor A for its jacks, so no insert runs on it there."}};
    daemon.receive(state(running));
    key("unit-chain", "inschain|mix:monitor");
    key("unit-insert", "insert|mix:monitor|a");
    key("computer-chain", "inschain|mix:stream");

    let sent = daemon.messages.length;
    host.receive({event:"keyDown",context:"unit-chain"});
    host.receive({event:"keyDown",context:"unit-insert"});
    assert.equal(daemon.messages.length, sent, "a greyed key sends nothing");
    assert.equal(alerted("unit-chain"), 1);
    assert.equal(alerted("unit-insert"), 1);

    host.receive({event:"keyDown",context:"computer-chain"});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"setInsertBypass",channel:"mix:stream",insertId:"b",value:true});

    // A dial's press on a plugin control of the unit's mix alerts too.
    host.receive({event:"willAppear",context:"unit-param",action:"com.emaspa.openxlr.dial",payload:{settings:{target:"insparam|mix:monitor|a|gain"}}});
    sent = daemon.messages.length;
    host.receive({event:"dialDown",context:"unit-param"});
    host.receive({event:"dialUp",context:"unit-param"});
    assert.equal(daemon.messages.length, sent);

    // Wanted and waiting, the map is not in force: the keys act again.
    daemon.receive(state({wanted:true, active:false, reason:"unitAway", note:"The Wave XLR Pro is not connected."}));
    host.receive({event:"keyDown",context:"unit-chain"});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"setInsertBypass",channel:"mix:monitor",insertId:"a",value:true});
  } finally {
    bridge.restore();
    for (const interval of intervals) clearInterval(interval);
    globalThis.setInterval = previousInterval;
    globalThis.WebSocket = previous;
    if (oldSkin === undefined) delete process.env.OPENXLR_SKIN;
    else process.env.OPENXLR_SKIN = oldSkin;
  }
});
