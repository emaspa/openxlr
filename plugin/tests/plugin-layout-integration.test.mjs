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
    state.mixer.channels[0].mutedIn = [];
    state.mixer.channels[0].appearance = {icon:"♫",colour:"#1234AB",hidden:true};
    host.receive({event:"willAppear",context:"appearance-key",action:"com.emaspa.openxlr.toggle",payload:{settings:{target:"sendmute:system:monitor"}}});
    daemon.receive(state);
    const appearanceImage = host.messages.filter(m => m.event === "setImage" && m.context === "appearance-key").at(-1).payload.image;
    const appearanceSvg = Buffer.from(appearanceImage.split(",")[1], "base64").toString();
    assert.ok(appearanceSvg.includes("♫"));
    assert.ok(appearanceSvg.includes("#1234AB"));
    host.receive({event:"sendToPlugin",context:"qa",payload:{request:"layout"}});
    assert.ok(host.messages.at(-1).payload.levelGroups.flatMap(g => g.items)
      .some(item => item.target === "send:system:monitor2"));
    host.receive({event:"willAppear",context:"focus-key",action:"com.emaspa.openxlr.toggle",payload:{settings:{target:"focus:system"}}});
    host.receive({event:"keyDown",context:"focus-key"});
    const focus = daemon.messages.at(-1);
    assert.deepEqual({...focus, requestId:undefined}, {cmd:"routeFocusedApp",channel:"system",requestId:undefined});
    assert.equal(typeof focus.requestId, "string");
    daemon.receive({type:"commandResult",requestId:focus.requestId,error:"ambiguous application"});
    assert.ok(host.messages.some(m => m.event === "showAlert" && m.context === "focus-key"));
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
    state.mixer.mixes.push({id:"stream",name:"Stream",kind:"virtualMic"}, {id:"auxout",name:"Aux",kind:"auxPort"});
    for (const [current, next] of [["monitor+monitor2", "stream"], ["stream", "auxout"], ["auxout", "monitor"], ["deleted", "monitor"], ["", "monitor"]]) {
      state.mixer.monitorFeeds["qa-output"] = current;
      daemon.receive(state);
      host.receive({event:"keyDown",context:"feed-key"});
      assert.deepEqual(daemon.messages.at(-1), {cmd:"setMonitorFeed",device:"qa-output",mix:next});
    }
    host.receive({event:"propertyInspectorDidDisappear",context:"qa"});
    const count = host.messages.filter(m => m.event === "sendToPropertyInspector").length;
    state.mixer.channels[0].name = "Another name";
    daemon.receive(state);
    assert.equal(host.messages.filter(m => m.event === "sendToPropertyInspector").length, count);

    for (const [target, expected] of [
      ["outputup:", {cmd:"adjustOutputVolume",device:null,value:.05}],
      ["outputdown:qa-output", {cmd:"adjustOutputVolume",device:"qa-output",value:-.05}],
      ["outputmute:qa-output", {cmd:"toggleOutputMute",device:"qa-output"}],
      ["mainoutput:qa-output", {cmd:"setMainOutput",device:"qa-output"}],
      ["mainoutput:@monitor", {cmd:"setMainOutput",device:"@monitor"}],
    ]) {
      host.receive({event:"willAppear",context:"output-key",action:"com.emaspa.openxlr.toggle",payload:{settings:{target}}});
      host.receive({event:"keyDown",context:"output-key"});
      const {requestId, ...payload} = daemon.messages.at(-1);
      assert.deepEqual(payload, expected);
      daemon.receive({type:"commandResult",requestId});
      assert.ok(host.messages.some(m => m.event === "showOk" && m.context === "output-key"));
    }
    host.receive({event:"willAppear",context:"missing-output",action:"com.emaspa.openxlr.toggle",payload:{settings:{target:"outputmute:gone"}}});
    const beforeMissing = daemon.messages.length;
    host.receive({event:"keyDown",context:"missing-output"});
    assert.equal(daemon.messages.length, beforeMissing);
    assert.ok(host.messages.some(m => m.event === "showAlert" && m.context === "missing-output"));

    // A desktop boost must not jump back to 100% on the first dial tick.
    state.mixer.mixes = [
      {id:"monitor",name:"Monitor A",kind:"monitor",volume:1.5},
      {id:"monitor2",name:"Monitor B",kind:"monitor",volume:1.2},
      {id:"chat",name:"Chat",kind:"virtualMic",volume:1}
    ];
    state.mixer.outputVolume = 1.5;
    daemon.receive(state);
    for (const [target, volume, maximum] of [
      ["mixvol:monitor", 0, 1.5],
      ["mixvol:monitor", 1, 1.5],
      ["mixvol:monitor", 1.2, 1.5],
      ["mixvol:monitor", 1.5, 1.5],
      ["mixvol:monitor2", 1.2, 1.5],
      ["outputVolume", 1, 1.5],
      ["outputVolume", 1.2, 1.5],
      ["outputVolume", 1.5, 1.5],
      ["mixvol:chat", 0.5, 1],
      ["mixvol:chat", 1, 1],
      ["send:system:monitor", 1, 1],
    ]) {
      if (target.startsWith("mixvol:")) state.mixer.mixes.find(m => m.id === target.slice(7)).volume = volume;
      else if (target === "outputVolume") state.mixer.outputVolume = volume;
      daemon.receive(state);
      host.receive({event:"willAppear",context:"needle-dial",action:"com.emaspa.openxlr.dial",payload:{settings:{target}}});
      const feedback = host.messages.filter(m => m.event === "setFeedback" && m.context === "needle-dial").at(-1).payload;
      assert.equal(feedback.value, `${Math.round(volume * 100)}%`);
      const svg = Buffer.from(feedback.needle.split(",")[1], "base64").toString();
      const angle = Number(svg.match(/rotate\(([^,]+)/)[1]);
      assert.ok(Math.abs(angle - (volume / maximum * 100 - 50)) < 0.0001, target);
    }
    for (const [target, ticks, expected] of [
      ["mixvol:monitor", -1, {cmd:"setMixVolume",mix:"monitor",value:1.49}],
      ["mixvol:monitor", 1, {cmd:"setMixVolume",mix:"monitor",value:1.5}],
      ["mixvol:monitor2", -1, {cmd:"setMixVolume",mix:"monitor2",value:1.19}],
      ["mixvol:chat", 1, {cmd:"setMixVolume",mix:"chat",value:1}],
      ["outputVolume", -1, {cmd:"setOutputVolume",value:1.49}],
      ["outputVolume", 1, {cmd:"setOutputVolume",value:1.5}],
      ["outputVolume", -200, {cmd:"setOutputVolume",value:0}],
    ]) {
      host.receive({event:"willAppear",context:"volume-dial",action:"com.emaspa.openxlr.dial",payload:{settings:{target}}});
      host.receive({event:"dialRotate",context:"volume-dial",payload:{ticks}});
      await new Promise(resolve => setTimeout(resolve, 120));   // past the send window
      assert.deepEqual(daemon.messages.at(-1), expected);
    }

    // Every tick of a burst has to land. The dial steps from its own last
    // value, so ticks arriving before the daemon's echo are not lost, and the
    // echo cannot pull the strip back to where the turn started.
    await new Promise(resolve => setTimeout(resolve, 900));   // let the turns above cool
    state.mixer.outputVolume = 0.5;
    daemon.receive(state);
    host.receive({event:"willAppear",context:"burst-dial",action:"com.emaspa.openxlr.dial",payload:{settings:{target:"outputVolume"}}});
    for (let tick = 0; tick < 5; tick++)
      host.receive({event:"dialRotate",context:"burst-dial",payload:{ticks:1}});
    const shown = () => host.messages
      .filter(m => m.event === "setFeedback" && m.context === "burst-dial").at(-1).payload.value;
    assert.equal(shown(), "55%");
    await new Promise(resolve => setTimeout(resolve, 150));
    assert.deepEqual(daemon.messages.at(-1), {cmd:"setOutputVolume",value:0.55});
    daemon.receive(state);
    assert.equal(shown(), "55%");

    // Output mute keys read the sink's state: a mix sink is its mix, a monitor
    // output is the mixes feeding it or the sink itself, any other sink is the
    // server's flag, and the system default is the enforced sink when named.
    state.devices = [
      {kind:0,name:"qa-output",description:"QA speakers",volume:0.8,muted:false},
      {kind:0,name:"desk",description:"Desk speakers",volume:1.2,muted:true},
      {kind:0,name:"OpenXLR_mix_monitor",description:"Monitor A",isOwn:true,volume:1,muted:false},
      {kind:0,name:"legacy",description:"Old sink"},
    ];
    state.mixer.monitorFeeds = {"qa-output":"monitor+monitor2"};
    state.mixer.mixes[0].muted = true;
    state.mixer.mixes[1].muted = false;
    state.mixer.enforcedDefaultSink = "desk";
    const keyFace = (context) => {
      const image = host.messages.filter(m => m.event === "setImage" && m.context === context).at(-1).payload.image;
      const svg = Buffer.from(image.split(",")[1], "base64").toString();
      return svg.includes("#FF3C4E") ? "muted" : svg.includes("#4a4f5c") ? "unknown" : "off";
    };
    const keyTarget = (context, target) =>
      host.receive({event:"willAppear",context,action:"com.emaspa.openxlr.toggle",payload:{settings:{target}}});
    for (const [context, target] of [["mix-key","outputmute:OpenXLR_mix_monitor"], ["monitor-key","outputmute:qa-output"],
      ["desk-key","outputmute:desk"], ["legacy-key","outputmute:legacy"], ["default-key","outputmute:"]]) keyTarget(context, target);
    daemon.receive(state);
    assert.equal(keyFace("mix-key"), "muted");
    assert.equal(keyFace("monitor-key"), "off");
    assert.equal(keyFace("desk-key"), "muted");
    assert.equal(keyFace("legacy-key"), "unknown");
    assert.equal(keyFace("default-key"), "muted");
    state.mixer.mixes[1].muted = true;
    daemon.receive(state);
    assert.equal(keyFace("monitor-key"), "muted");
    state.mixer.mixes[0].muted = state.mixer.mixes[1].muted = false;
    state.devices[0].muted = true;
    daemon.receive(state);
    assert.equal(keyFace("monitor-key"), "muted");
    state.devices[0].muted = false;
    state.mixer.enforcedDefaultSink = "@monitor";
    daemon.receive(state);
    assert.equal(keyFace("monitor-key"), "off");
    assert.equal(keyFace("default-key"), "off");   // momentary while the default is not a named sink
    const beforeLegacy = daemon.messages.length;
    host.receive({event:"keyDown",context:"legacy-key"});
    assert.equal(daemon.messages.length, beforeLegacy);
    host.receive({event:"keyDown",context:"default-key"});
    assert.deepEqual({...daemon.messages.at(-1), requestId:undefined}, {cmd:"toggleOutputMute",device:null,requestId:undefined});
    state.mixer.enforcedDefaultSink = "desk";
    daemon.receive(state);

    // Output dials: the level and mute of a sink, the system default resolved
    // through the enforced sink, sent with the target's own device name.
    const dialTarget = (context, target) =>
      host.receive({event:"willAppear",context,action:"com.emaspa.openxlr.dial",payload:{settings:{target}}});
    const feedbackOf = (context) => host.messages.filter(m => m.event === "setFeedback" && m.context === context).at(-1).payload;
    for (const [target, value, rotate, expected] of [
      ["output:", "MUTED", 1, {cmd:"setOutputDeviceVolume",device:null,value:1.21}],
      ["output:qa-output", "80%", -1, {cmd:"setOutputDeviceVolume",device:"qa-output",value:0.79}],
      ["output:OpenXLR_mix_monitor", "150%", 1, {cmd:"setOutputDeviceVolume",device:"OpenXLR_mix_monitor",value:1.5}],
    ]) {
      dialTarget("output-dial", target);
      assert.equal(feedbackOf("output-dial").value, value, target);
      assert.equal(feedbackOf("output-dial").muteOverlay.enabled, value === "MUTED", target);
      host.receive({event:"dialRotate",context:"output-dial",payload:{ticks:rotate}});
      assert.deepEqual(daemon.messages.at(-1), expected);
      host.receive({event:"dialDown",context:"output-dial"});
      assert.deepEqual(daemon.messages.at(-1), {cmd:"toggleOutputMute",device:expected.device});
    }
    dialTarget("output-dial", "output:legacy");
    assert.equal(feedbackOf("output-dial").value, "set up");
    const beforeLegacyDial = daemon.messages.length;
    host.receive({event:"dialRotate",context:"output-dial",payload:{ticks:1}});
    assert.equal(daemon.messages.length, beforeLegacyDial);

    // The monitor dial's press mutes what the first monitor output hears.
    dialTarget("monitor-dial", "outputVolume");
    assert.equal(feedbackOf("monitor-dial").muteOverlay.enabled, false);
    host.receive({event:"dialDown",context:"monitor-dial"});
    assert.deepEqual(daemon.messages.slice(-2), [
      {cmd:"setMixMuted",mix:"monitor",value:true}, {cmd:"setMixMuted",mix:"monitor2",value:true}]);
    state.mixer.mixes[0].muted = state.mixer.mixes[1].muted = true;
    daemon.receive(state);
    assert.equal(feedbackOf("monitor-dial").muteOverlay.enabled, true);
    host.receive({event:"dialDown",context:"monitor-dial"});
    assert.deepEqual(daemon.messages.slice(-2), [
      {cmd:"setMixMuted",mix:"monitor",value:false}, {cmd:"setMixMuted",mix:"monitor2",value:false}]);
    state.mixer.monitorFeeds = {"qa-output":"monitor2"};
    daemon.receive(state);
    host.receive({event:"dialDown",context:"monitor-dial"});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"setMixMuted",mix:"monitor2",value:false});
  }
  finally {
    intervals.forEach(clearInterval);
    globalThis.setInterval = previousInterval;
    globalThis.WebSocket = previous;
  }
});
