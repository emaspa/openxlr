import assert from "node:assert/strict";
import test from "node:test";
import {controlFixture} from "./control-fixture.mjs";

// The Wave XLR Pro's Line In: a dial turns its gain, which is the unit's own
// control, presses mute the channel in every mix since the input has no
// hardware mute, and meters the Line In channel. A key toggles its level
// lock, apart from Aux In's. A unit without the input answers the dial with
// an alert and greys the key.
test("the Line In dial turns the Pro's Line In gain and mutes its channel", async () => {
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
  const svg = (uri) => Buffer.from(uri.split(",")[1], "base64").toString();
  const control = controlFixture();
  try {
    await import("../com.emaspa.openxlr.sdPlugin/plugin.mjs");
    const daemon = control.children[0];
    const host = sockets[0];
    daemon.onopen();
    const state = {type:"state", connected:true, profiles:[],
      capabilities:{gain:true, mute:true, auxInput:true, lineInput:true, xlrInputs:2, gainMaxDb:80},
      state:{gainDb:52, mute:false, auxLevelDb:-30, auxLevelLock:true, lineInGainDb:20, lineInLevelLock:false},
      devices:[],
      mixer:{
        channels:[
          {id:"xlr1",name:"XLR 1",hardware:true,levels:{monitor:1,stream:1},mutedIn:["monitor"]},
          {id:"aux",name:"Aux In",hardware:true,levels:{monitor:1,stream:1,auxout:0},mutedIn:["monitor","auxout"]},
          {id:"line",name:"Line In",hardware:true,levels:{monitor:1,stream:1,auxout:0},mutedIn:["monitor","auxout"],volume:1},
        ],
        mixes:[{id:"monitor",name:"Monitor A",kind:"monitor",volume:1},{id:"stream",name:"Stream",kind:"virtualMic",volume:1},
               {id:"auxout",name:"Aux",kind:"auxPort",volume:1}],
        monitorOutputs:[], monitorFeeds:{}, inserts:{}, lowCutHz:0, auxPortEnabled:true,
      }};
    daemon.receive(state);
    const feedback = (context) =>
      host.messages.filter(m => m.event === "setFeedback" && m.context === context && m.payload.title).at(-1).payload;
    const meter = (context) =>
      host.messages.filter(m => m.event === "setFeedback" && m.context === context && m.payload.meter).at(-1)?.payload.meter;
    const alerted = (context) => host.messages.filter(m => m.event === "showAlert" && m.context === context).length;

    host.receive({event:"willAppear",context:"line-dial",action:"com.emaspa.openxlr.dial",payload:{settings:{target:"lineIn"}}});
    const face = feedback("line-dial");
    assert.equal(face.value, "20 dB");
    assert.ok(svg(face.title).includes("Line In gain"));

    // A turn writes the Line In's own gain in whole dB.
    host.receive({event:"dialRotate",context:"line-dial",payload:{ticks:3}});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"set",control:"lineInGain",value:23});
    assert.equal(feedback("line-dial").value, "23 dB");

    // A press mutes the channel in every mix; there is no hardware Line In mute.
    const before = daemon.messages.length;
    host.receive({event:"dialDown",context:"line-dial"});
    const muted = daemon.messages.slice(before);
    assert.deepEqual(muted.map(m => m.cmd), ["setChannelMuted", "setChannelMuted", "setChannelMuted"]);
    assert.ok(muted.every(m => m.channel === "line" && m.value === true));
    assert.ok(!muted.some(m => m.control));

    // It meters the Line In channel.
    daemon.receive({type:"meters", levels:{"ch:aux":[0,0], "ch:line":[0.8,0.8]}});
    assert.match(svg(meter("line-dial")), /<rect width="104" height="6"/);

    // The level lock key reads and writes Line In's lock, not Aux In's.
    const keyImage = (context) =>
      svg(host.messages.filter(m => m.event === "setImage" && m.context === context).at(-1).payload.image);
    host.receive({event:"willAppear",context:"line-lock",action:"com.emaspa.openxlr.toggle",payload:{settings:{target:"lineInLevelLock"}}});
    host.receive({event:"titleParametersDidChange",context:"line-lock",payload:{title:""}});
    assert.ok(keyImage("line-lock").includes(">Line In</text>"));
    assert.ok(keyImage("line-lock").includes(">Lock</text>"));
    host.receive({event:"keyDown",context:"line-lock"});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"set",control:"lineInLevelLock",value:true});
    state.state.lineInLevelLock = true;
    daemon.receive(state);
    host.receive({event:"keyDown",context:"line-lock"});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"set",control:"lineInLevelLock",value:false});

    // Line In's raw capture key, on a unit that has raw capture.
    state.capabilities.rawCapture = true;
    state.state.lineInRawCapture = false;
    daemon.receive(state);
    host.receive({event:"willAppear",context:"line-raw",action:"com.emaspa.openxlr.toggle",payload:{settings:{target:"lineInRawCapture"}}});
    host.receive({event:"titleParametersDidChange",context:"line-raw",payload:{title:""}});
    assert.ok(keyImage("line-raw").includes(">Raw</text>"));
    host.receive({event:"keyDown",context:"line-raw"});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"set",control:"lineInRawCapture",value:true});

    // A unit without the input: the dial says so and sends nothing, and so does the key.
    state.capabilities.lineInput = false;
    daemon.receive(state);
    const sent = daemon.messages.length;
    host.receive({event:"dialRotate",context:"line-dial",payload:{ticks:1}});
    assert.equal(daemon.messages.length, sent);
    assert.equal(alerted("line-dial"), 1);
    host.receive({event:"keyDown",context:"line-lock"});
    assert.equal(daemon.messages.length, sent);
  } finally {
    control.restore();
    for (const interval of intervals) clearInterval(interval);
    globalThis.setInterval = previousInterval;
    globalThis.WebSocket = previous;
    if (oldSkin === undefined) delete process.env.OPENXLR_SKIN;
    else process.env.OPENXLR_SKIN = oldSkin;
  }
});
