import {controlFixture} from "./control-fixture.mjs";
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';

// A segment skin draws keys and dial strips as a display; leaving it puts
// back the layout's own strip, and a move between two display skins repaints
// the strip's glass.
test('a segment skin draws keys and dial strips as a display, and leaving it restores the strip', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'openxlr-deck-vfd-'));
  const oldEnv = {...process.env};
  process.env.XDG_CONFIG_HOME = root;
  process.env.XDG_DATA_HOME = path.join(root, 'data');
  process.env.XDG_DATA_DIRS = path.join(root, 'system');
  delete process.env.OPENXLR_SKIN;
  const directory = path.join(root, 'openxlr'); fs.mkdirSync(directory);
  const choose = id => {
    fs.writeFileSync(path.join(directory, 'pending'), JSON.stringify({skin: id}));
    fs.renameSync(path.join(directory, 'pending'), path.join(directory, 'ui.json'));
  };
  choose('vfd-amber');
  const previous = globalThis.WebSocket, previousInterval = globalThis.setInterval, previousWatch = fs.watch;
  const intervals = [], watchers = [], sockets = [];
  globalThis.setInterval = (...args) => { const i = previousInterval(...args); intervals.push(i); return i; };
  fs.watch = (...args) => { const w = previousWatch(...args); watchers.push(w); return w; };
  class Socket {
    static OPEN = 1; readyState = 1; messages = [];
    constructor(url) { this.url = url; sockets.push(this); }
    send(text) { this.messages.push(JSON.parse(text)); }
    receive(message) { this.onmessage({data:JSON.stringify(message)}); }
  }
  globalThis.WebSocket = Socket;
  const svg = uri => Buffer.from(uri.split(',')[1], 'base64').toString();
  async function until(condition) {
    const deadline = Date.now() + 4000;
    while (!condition() && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 20));
    assert.ok(condition());
  }
  const control = controlFixture();
  try {
    await import('../com.emaspa.openxlr.sdPlugin/plugin.mjs');
    const daemon = control.children[0], host = sockets[0];
    daemon.onopen();
    daemon.receive({type:'state', profiles:[], devices:[], mixer:{channels:[], mixes:[
      {id:'monitor',name:'Monitor A',kind:'monitor',volume:.5,muted:true},
      {id:'stream',name:'Stream',kind:'stream',volume:.5,muted:false}], monitorOutputs:[]}});
    host.receive({event:'willAppear',context:'mute',action:'com.emaspa.openxlr.toggle',payload:{settings:{target:'mixmute:monitor'}}});
    host.receive({event:'willAppear',context:'dial',action:'com.emaspa.openxlr.dial',payload:{settings:{target:'mixvol:stream'}}});
    daemon.receive({type:'meters',levels:{'mix:stream':[.5,.5]}});
    const key = () => svg(host.messages.filter(m => m.event === 'setImage' && m.context === 'mute').at(-1).payload.image);
    const layouts = () => host.messages.filter(m => m.event === 'setFeedbackLayout' && m.context === 'dial').map(m => m.payload.layout);
    const feedback = field => host.messages.filter(m => m.event === 'setFeedback' && m.context === 'dial' && m.payload[field] !== undefined).at(-1)?.payload[field];
    const palettes = JSON.parse(fs.readFileSync(new URL('../com.emaspa.openxlr.sdPlugin/skin-palettes.json', import.meta.url), 'utf8'));
    const amber = palettes['vfd-amber'], blue = palettes['vfd-blue'];

    // The key: the display's glass and mesh, the mute lit in the alert colour.
    assert.match(key(), /<pattern id="mesh"/);
    assert.match(key(), /filter="url\(#halo\)"/);
    assert.ok(key().includes(amber['Ox.Led.Alert']));
    assert.ok(!key().includes('<circle cx="72"'), 'no round cap on a display key');

    // The strip: its own layout, the display's glass, a ladder of cells in
    // place of the knob, no needle and none of OpenDeck's own text.
    assert.deepEqual(layouts(), ['layouts/dial-display.json']);
    assert.ok(svg(feedback('backgroundDial')).includes(amber['Ox.Text.Primary']));
    assert.equal(feedback('value'), '');
    assert.equal(feedback('unit').enabled, false);
    assert.ok(!svg(feedback('needle')).includes('<rect'));
    const dial = svg(feedback('dial'));
    assert.match(dial, /width="168"/);
    assert.ok(dial.includes(amber['Ox.Text.Ghost']));
    const meter = svg(host.messages.filter(m => m.event === 'setFeedback' && m.context === 'dial' && m.payload.meter).at(-1).payload.meter);
    assert.match(meter, /width="168"/);
    assert.ok((meter.match(/<rect /g) || []).length > 30, 'the meter is a ladder of cells');

    // A move to another display skin keeps the layout and repaints the glass.
    const before = layouts().length;
    choose('vfd-blue');
    await until(() => feedback('backgroundDial') && svg(feedback('backgroundDial')).includes(blue['Ox.Text.Primary']));
    assert.equal(layouts().length, before, 'no layout change between two display skins');

    // Leaving the display puts back the knob's layout and the strip's own images.
    choose('broadcast');
    await until(() => layouts().at(-1) === 'layouts/dial.json');
    const strip = fs.readFileSync(new URL('../com.emaspa.openxlr.sdPlugin/imgs/dialBackground.svg', import.meta.url), 'utf8');
    await until(() => feedback('backgroundDial') && svg(feedback('backgroundDial')) === strip);
    assert.notEqual(feedback('value'), '');
    assert.ok(!key().includes('<pattern id="mesh"'));

    // Every image is well-formed SVG.
    const decoded = host.messages.flatMap(m => m.event === 'setImage' ? [svg(m.payload.image)] :
      m.event === 'setFeedback' ? Object.values(m.payload).filter(v => typeof v === 'string' && v.startsWith('data:')).map(svg) : []);
    const parsed = spawnSync('python3', ['-c','import json,sys,xml.etree.ElementTree as E; [E.fromstring(s) for s in json.load(sys.stdin)]'], {input:JSON.stringify(decoded)});
    assert.equal(parsed.status, 0, parsed.stderr.toString());
  } finally {
    control.restore();
    for (const i of intervals) clearInterval(i);
    for (const w of watchers) w.close();
    fs.watch = previousWatch; globalThis.setInterval = previousInterval; globalThis.WebSocket = previous;
    for (const key of ['XDG_CONFIG_HOME','XDG_DATA_HOME','XDG_DATA_DIRS','OPENXLR_SKIN']) {
      if (oldEnv[key] === undefined) delete process.env[key]; else process.env[key] = oldEnv[key];
    }
    fs.rmSync(root,{recursive:true,force:true});
  }
});
