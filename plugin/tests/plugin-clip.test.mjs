import assert from "node:assert/strict";
import test from "node:test";
import {controlFixture} from "./control-fixture.mjs";

const clip = (id, name, more = {}) => ({id, name, seconds: 2.4, gain: 1, mode: "overlap", loop: false, icon: "",
  colour: null, loaded: true, error: null, playing: false, status: "ready", ...more});
const board = () => ({channel: "sfx", enabled: true, available: true, error: null, formats: ["wav", "flac"],
  clips: [clip("airhorn", "Airhorn", {colour: "#ff7a00"}), clip("drum-roll", "Drum roll and cymbal", {mode: "hold", icon: "♪"})],
  playing: []});
const state = () => ({type: "state", connected: true, profiles: [], devices: [], capabilities: {xlrInputs: 1}, state: {},
  mixer: {channels: [{id: "sfx", name: "SFX", levels: {monitor: 1}, mutedIn: [],
                      appearance: {icon: "", defaultIcon: "burst", colour: "#123456"}}],
          mixes: [{id: "monitor", name: "Monitor A", kind: "monitor", volume: 1, muted: false}],
          monitorOutputs: [], monitorFeeds: {}, inserts: {}, lowCutHz: 0, soundboard: board()}});

// The clip key against a fake OpenDeck host and a fake daemon: what a press
// sends, what the key draws from the state and the meters, and what the
// inspector lists.
test("a clip key plays, holds, stops all and draws the board's state and the clip's progress", async t => {
  t.mock.timers.enable({apis: ["setTimeout"]});
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
  const svg = image => Buffer.from(image.split(",")[1], "base64").toString();
  try {
    await import("../com.emaspa.openxlr.sdPlugin/plugin.mjs");
    const host = sockets[0];
    let daemon = control.children[0];
    daemon.onopen(); await flush();
    let current = state();
    const push = async () => { daemon.receive(current); await flush(); };
    const meters = async levels => { daemon.receive({type: "meters", levels}); await flush(); };
    await push();
    const appear = (context, settings, device = "deck-1") => {
      host.receive({event: "willAppear", context, device, action: "com.emaspa.openxlr.clip", payload: {settings}});
      host.receive({event: "titleParametersDidChange", context, payload: {title: ""}});
    };
    const images = context => host.messages.filter(m => m.event === "setImage" && m.context === context);
    const image = context => svg(images(context).at(-1).payload.image);
    const sent = () => daemon.messages.filter(m => /Clip/.test(m.cmd));
    const alerts = context => host.messages.filter(m => m.event === "showAlert" && m.context === context).length;
    const answer = async (error = null) => { daemon.receive({type: "commandResult", requestId: sent().at(-1).requestId, error}); await flush(); };
    const down = context => host.receive({event: "keyDown", context});
    const up = context => host.receive({event: "keyUp", context});

    appear("horn", {target: "clip:airhorn"});
    appear("roll", {target: "clip:drum-roll", name: "Drum roll and cymbal"});
    appear("forced", {target: "clip:airhorn", mode: "hold"});
    appear("stop", {target: "stopClips"});
    appear("blank", {});

    assert.ok(!image("horn").includes('<circle cx="120" cy="24" r="8" fill="#3ecf7a"'), "a silent clip's key is dark");
    // The face: the clip's name, its colour, SFX's icon where the clip has none.
    assert.match(image("horn"), />Airhorn<\/text>/);
    assert.ok(image("horn").includes("#ff7a00"));
    assert.ok(image("roll").includes(">Drum roll…</text>"), "a long name is shortened for the key");
    assert.ok(image("roll").includes(">♪</text>"), "the clip's own glyph");
    assert.ok(image("roll").includes("#123456"), "a clip without a colour takes SFX's");
    assert.match(image("stop"), />Stop all<\/text>/);
    assert.match(image("blank"), />set up<\/text>/);

    // An overlap press plays the clip; its success shows no check mark over the key.
    down("horn"); up("horn"); await flush();
    assert.deepEqual(sent().map(({requestId, ...rest}) => rest), [{cmd: "playClip", clip: "airhorn", mode: "overlap"}]);
    await answer();
    assert.equal(host.messages.filter(m => m.event === "showOk").length, 0);

    // The clip's own hold mode: the release ends it.
    down("roll"); await flush();
    assert.deepEqual(sent().at(-1), {cmd: "playClip", clip: "drum-roll", mode: "hold", requestId: sent().at(-1).requestId});
    up("roll"); await flush();
    assert.deepEqual(sent().at(-1), {cmd: "releaseClip", clip: "drum-roll"});
    await answer();

    // A key's own mode overrides the clip's, hold included.
    down("forced"); await flush();
    assert.equal(sent().at(-1).mode, "hold");
    await answer();
    up("forced"); await flush();
    assert.deepEqual(sent().at(-1), {cmd: "releaseClip", clip: "airhorn"});
    // A key that disappears while held lets go of its clip.
    down("forced"); await flush(); await answer();
    host.receive({event: "willDisappear", context: "forced"}); await flush();
    assert.deepEqual(sent().at(-1), {cmd: "releaseClip", clip: "airhorn"});
    const count = sent().length;
    up("forced"); await flush();
    assert.equal(sent().length, count, "a release is sent once");

    // Stop all.
    down("stop"); await flush();
    assert.equal(sent().at(-1).cmd, "stopClips");
    await answer();

    // A refused press alerts.
    down("horn"); await flush(); await answer("The soundboard is starting.");
    assert.equal(alerts("horn"), 1);

    // Playing: the state lights the key, the meters draw the bar, and a frame
    // that does not move the bar draws nothing.
    current.mixer.soundboard.clips[0].playing = true;
    current.mixer.soundboard.playing = ["airhorn"];
    await push();
    const lit = '<circle cx="120" cy="24" r="8" fill="#3ecf7a"';
    assert.ok(image("horn").includes(lit), "a playing clip lights its key");
    assert.ok(!image("roll").includes(lit), "another clip's key stays dark");
    assert.ok(image("stop").includes(lit), "stop all lights while a clip plays");
    await meters({"ch:sfx": [.5, .5], "clip:airhorn": [.5, 1]});
    assert.match(image("horn"), /<rect x="22" y="124" width="50" height="6"/);
    const drawn = images("horn").length;
    await meters({"ch:sfx": [.4, .4], "clip:airhorn": [.505, 1]});
    assert.equal(images("horn").length, drawn, "a frame within the same step is not redrawn");
    await meters({"ch:sfx": [.4, .4], "clip:airhorn": [.75, 2]});
    assert.match(image("horn"), /<rect x="22" y="124" width="75" height="6"/);
    // A title the user typed is drawn over the key: no bar under it, and no
    // redraw for a frame that only moves the bar.
    host.receive({event: "titleParametersDidChange", context: "horn", payload: {title: "Boom"}});
    assert.ok(!image("horn").includes('y="124"'));
    assert.ok(image("horn").includes('<circle cx="120" cy="24" r="8" fill="#3ecf7a"'), "still lit");
    const titled = images("horn").length;
    await meters({"ch:sfx": [.4, .4], "clip:airhorn": [.95, 2]});
    assert.equal(images("horn").length, titled);
    host.receive({event: "titleParametersDidChange", context: "horn", payload: {title: ""}});
    assert.match(image("horn"), /<rect x="22" y="124" width="95" height="6"/);
    // The clip ends: its key leaves the meters and the bar goes.
    await meters({"ch:sfx": [0, 0]});
    assert.ok(!image("horn").includes('y="124"'));

    // A clip that was deleted reads gone, under the name the key saved, and a press sends nothing.
    current.mixer.soundboard.clips.splice(1, 1);
    await push();
    assert.match(image("roll"), />Drum roll…<\/text>/);
    assert.match(image("roll"), />gone<\/text>/);
    const before = sent().length;
    down("roll"); await flush();
    assert.equal(sent().length, before);
    assert.equal(alerts("roll"), 1);

    // Loading and failed clips, by their status.
    Object.assign(current.mixer.soundboard.clips[0], {loaded: false, status: "loading"});
    await push();
    assert.match(image("horn"), />loading<\/text>/);
    Object.assign(current.mixer.soundboard.clips[0], {status: "error", error: "The clip could not be decoded."});
    await push();
    assert.match(image("horn"), />error<\/text>/);
    current.mixer.soundboard.clips[0] = clip("airhorn", "Airhorn");

    // The board cannot play: every clip is off and the board's sentence says
    // why. Each key reads one word for it, and a press alerts instead of sending.
    const cannot = async (fields, error) => {
      Object.assign(current.mixer.soundboard, fields, {error});
      for (const each of current.mixer.soundboard.clips)
        Object.assign(each, error === null ? {status: "ready", loaded: true} : {status: "off", loaded: false});
      await push();
    };
    await cannot({channel: null, enabled: false},
      "The soundboard is off because the SFX channel was deleted. Turn it on again to create SFX, muted in every mix.");
    assert.match(image("horn"), />no SFX<\/text>/);
    assert.match(image("stop"), />no SFX<\/text>/);
    down("stop"); await flush();
    assert.equal(alerts("stop"), 1);
    await cannot({channel: "sfx", enabled: false}, "The soundboard is off. Turn it on first.");
    assert.match(image("horn"), />off<\/text>/);
    await cannot({available: false}, "The soundboard needs the OpenXLR native plugin host, which this installation does not include.");
    assert.match(image("horn"), />unavailable<\/text>/);
    await cannot({available: true, enabled: true}, "The soundboard is starting.");
    assert.match(image("horn"), />stopped<\/text>/);
    assert.match(image("stop"), />stopped<\/text>/);
    await cannot({}, null);
    assert.match(image("horn"), />Airhorn<\/text>/);
    assert.ok(!image("horn").includes(">unavailable<"));

    // The inspector lists the clips by id with their names and the daemon's sentence.
    host.receive({event: "sendToPlugin", context: "horn", payload: {request: "soundboard"}});
    const listed = host.messages.at(-1).payload.soundboard;
    assert.deepEqual(listed.clips.map(c => [c.id, c.name]), [["airhorn", "Airhorn"]]);
    assert.equal(listed.notice, null);
    current.mixer.soundboard.error = "The soundboard player stopped three times in five minutes.";
    current.mixer.soundboard.clips.push(clip("cheer", "Cheer"));
    await push();
    const update = host.messages.filter(m => m.event === "sendToPropertyInspector").at(-1).payload.soundboard;
    assert.deepEqual(update.clips.map(c => c.id), ["airhorn", "cheer"]);
    assert.equal(update.notice, "The soundboard player stopped three times in five minutes.");
    host.receive({event: "propertyInspectorDidDisappear", context: "horn"});
    current.mixer.soundboard.error = null;

    // A daemon without a soundboard is older than the plugin.
    const older = state(); delete older.mixer.soundboard;
    daemon.receive(older); await flush();
    assert.match(image("horn"), />update<\/text>/);

    // Offline, and back after a reconnection.
    daemon.emit("close"); await flush();
    assert.match(image("horn"), />offline<\/text>/);
    t.mock.timers.tick(500);
    daemon = control.children.at(-1);
    daemon.onopen(); await flush();
    current = state();
    await push();
    assert.match(image("horn"), />Airhorn<\/text>/);
    assert.ok(!image("horn").includes(">offline<"));
    down("horn"); await flush();
    assert.equal(sent().at(-1).cmd, "playClip");
    await answer();
    const releases = () => sent().filter(m => m.cmd === "releaseClip");

    // Two keys holding one clip: the clip ends when the last of them lets go,
    // since releaseClip ends every hold voice of the clip on this connection.
    appear("hold-a", {target: "clip:drum-roll"});
    appear("hold-b", {target: "clip:airhorn", mode: "hold"});
    appear("hold-c", {target: "clip:airhorn", mode: "hold"});
    down("hold-b"); await flush(); await answer();
    down("hold-c"); await flush(); await answer();
    let released = releases().length;
    up("hold-b"); await flush();
    assert.equal(releases().length, released, "the other key still holds the clip");
    up("hold-c"); await flush();
    assert.deepEqual(releases().at(-1), {cmd: "releaseClip", clip: "airhorn"});
    assert.equal(releases().length, released + 1);

    // A device can disappear without sending a key up or willDisappear.
    appear("hold-remote", {target: "clip:airhorn", mode: "hold"}, "deck-2");
    down("hold-a"); await flush(); await answer();
    down("hold-b"); await flush(); await answer();
    down("hold-remote"); await flush(); await answer();
    released = releases().length;
    host.receive({event: "deviceDidDisconnect", device: "deck-1"}); await flush();
    assert.deepEqual(releases().slice(released), [{cmd: "releaseClip", clip: "drum-roll"}],
      "release the disconnected device's holds but leave a clip held by a different device playing");
    host.receive({event: "willDisappear", context: "hold-b"});
    up("hold-a"); await flush();
    assert.equal(releases().length, released + 1, "late releases from the disconnected deck do nothing");
    up("hold-remote"); await flush();
    assert.deepEqual(releases().slice(released + 1), [{cmd: "releaseClip", clip: "airhorn"}]);
    appear("hold-b", {target: "clip:airhorn", mode: "hold"});

    down("hold-a"); await flush(); await answer();
    released = releases().length;
    host.receive({event: "deviceDidDisconnect"}); await flush();
    assert.equal(releases().length, released, "a missing device id releases nothing");
    appear("hold-a", {target: "clip:drum-roll"});
    await flush();
    assert.deepEqual(releases().slice(released), [{cmd: "releaseClip", clip: "drum-roll"}],
      "a key that reappears also lets go of its previous hold");

    // Inside a multi-action OpenDeck sends the key up 100 ms after the key
    // down: a hold key plays as overlap there and its key up sends nothing.
    released = releases().length;
    host.receive({event: "keyDown", context: "hold-a", payload: {isInMultiAction: true}}); await flush();
    assert.equal(sent().at(-1).cmd, "playClip");
    assert.equal(sent().at(-1).mode, "overlap", "the clip's own hold mode is overridden");
    await answer();
    host.receive({event: "keyDown", context: "hold-b", payload: {isInMultiAction: true}}); await flush();
    assert.equal(sent().at(-1).mode, "overlap", "so is the key's own");
    await answer();
    host.receive({event: "keyDown", context: "horn", payload: {isInMultiAction: true}}); await flush();
    assert.equal(sent().at(-1).mode, "overlap", "a clip that does not hold plays as it is");
    await answer();
    up("hold-a"); up("hold-b"); await flush();
    assert.equal(releases().length, released);

    // Quick taps inside one round trip all play, and each hold key up follows its press.
    let plays = sent().filter(m => m.cmd === "playClip").length;
    down("horn"); up("horn"); down("horn"); up("horn"); await flush();
    down("hold-a"); up("hold-a"); down("hold-a"); up("hold-a"); await flush();
    assert.equal(sent().filter(m => m.cmd === "playClip").length, plays + 4, "no press is dropped");
    assert.deepEqual(sent().slice(-4).map(m => m.cmd), ["playClip", "releaseClip", "playClip", "releaseClip"]);
    for (const message of sent().filter(m => m.requestId))
      daemon.receive({type: "commandResult", requestId: message.requestId, error: null});
    await flush();

    // A press dropped at the outstanding limit holds nothing, so its key up sends no release.
    for (let i = 0; i < 64; i++) down("horn");
    await flush();
    plays = sent().length;
    released = releases().length;
    down("hold-a"); await flush();
    assert.equal(sent().length, plays, "the 65th unanswered press is dropped");
    assert.ok(alerts("hold-a") >= 1);
    up("hold-a"); await flush();
    assert.equal(releases().length, released);
  } finally {
    control.restore();
    for (const interval of intervals) clearInterval(interval);
    globalThis.setInterval = previous.interval;
    globalThis.WebSocket = previous.socket;
    if (previous.skin === undefined) delete process.env.OPENXLR_SKIN; else process.env.OPENXLR_SKIN = previous.skin;
  }
});
