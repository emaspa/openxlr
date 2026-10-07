import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';

test('live palettes repaint key art, dial text, needles and stationary meters without audio commands', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'openxlr-deck-art-'));
  const oldEnv = {...process.env};
  process.env.XDG_CONFIG_HOME = root;
  process.env.XDG_DATA_HOME = path.join(root, 'data');
  process.env.XDG_DATA_DIRS = path.join(root, 'system');
  delete process.env.OPENXLR_SKIN;
  const directory = path.join(root, 'openxlr'); fs.mkdirSync(directory);
  fs.writeFileSync(path.join(directory, 'ui.json'), '{"skin":"default"}');
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
  try {
    await import('../com.emaspa.openxlr.sdPlugin/plugin.mjs');
    const daemon = sockets.find(s => s.url.includes(':37890/')), host = sockets.find(s => s !== daemon);
    daemon.onopen();
    daemon.receive({type:'state', profiles:[], devices:[], mixer:{channels:[], mixes:[{id:'monitor',name:'Monitor A',kind:'monitor',volume:.5,muted:false}],monitorOutputs:[],inserts:{}}});
    host.receive({event:'willAppear',context:'key',action:'com.emaspa.openxlr.toggle',payload:{settings:{target:'mixmute:monitor'}}});
    host.receive({event:'willAppear',context:'dial',action:'com.emaspa.openxlr.dial',payload:{settings:{target:'mixvol:monitor'}}});
    const meters = {type:'meters',levels:{'mix:monitor':[.902,.902]}};
    daemon.receive(meters);
    const images = () => host.messages.filter(m => m.event === 'setImage');
    const meterImages = () => host.messages.filter(m => m.event === 'setFeedback' && m.payload.meter);
    const commandCount = daemon.messages.length;
    const before = images().length;
    const palette = {'Ox.Card.Background':'#fafafa','Ox.Text.Primary':'#010203','Ox.Led.Alert':'#ff0000',
      'Ox.Meter.Fill':'#11aa22', 'Ox.Meter.Warning':'#aabb22','Ox.Meter.Hot':'#ee0011', 'Ox.Meter.HotLevel':.905};
    const save = () => { fs.writeFileSync(path.join(directory, 'pending'), JSON.stringify({schema:1,pid:process.pid,skin:'default',tokens:palette})); fs.renameSync(path.join(directory, 'pending'),path.join(directory, 'deck-palette.json')); };
    save();
    await until(() => images().length > before);
    const art = svg(images().at(-1).payload.image);
    assert.ok(art.includes('#fafafa')); assert.ok(art.includes('#010203')); assert.ok(!art.includes('skinPalette'));
    assert.ok(svg(meterImages().at(-1).payload.meter).includes('#aabb22'));
    const feedback = host.messages.filter(m => m.event === 'setFeedback' && m.payload.needle).at(-1).payload;
    for (const field of ['title', 'icon', 'needle']) assert.ok(svg(feedback[field]).includes('#010203'), field);
    // Two readings in the same width bucket still have different colour zones.
    daemon.receive({type:'meters',levels:{'mix:monitor':[.906,.906]}});
    assert.ok(svg(meterImages().at(-1).payload.meter).includes('#ee0011'));
    host.receive({event:'willDisappear',context:'dial'});
    host.receive({event:'willAppear',context:'dial',action:'com.emaspa.openxlr.dial',payload:{settings:{target:'mixvol:monitor'}}});
    const oldMeters = meterImages().length;
    daemon.receive({type:'meters',levels:{'mix:monitor':[.906,.906]}});
    assert.equal(meterImages().length, oldMeters + 1, 'a reused context needs its first meter');
    const previousOpen = fs.openSync;
    let opens = 0;
    fs.openSync = (...args) => { opens++; return previousOpen(...args); };
    const settledMeters = meterImages().length;
    try {
      for (let i = 0; i < 100; i++) daemon.receive({type:'meters',levels:{'mix:monitor':[.906,.906]}});
    } finally { fs.openSync = previousOpen; }
    assert.equal(opens, 0, 'meter ticks must not open files');
    assert.equal(meterImages().length, settledMeters, 'unchanged meters must not redraw');
    assert.equal(daemon.messages.length, commandCount);
    const decoded = host.messages.flatMap(m => m.event === 'setImage' ? [svg(m.payload.image)] :
      m.event === 'setFeedback' ? ['title','icon','needle','meter'].filter(k => typeof m.payload[k] === 'string' && m.payload[k].startsWith('data:')).map(k => svg(m.payload[k])) : []);
    const parsed = spawnSync('python3', ['-c','import json,sys,xml.etree.ElementTree as E; [E.fromstring(s) for s in json.load(sys.stdin)]'], {input:JSON.stringify(decoded)});
    assert.equal(parsed.status, 0, parsed.stderr.toString());
  } finally {
    for (const i of intervals) clearInterval(i);
    for (const w of watchers) w.close();
    fs.watch = previousWatch; globalThis.setInterval = previousInterval; globalThis.WebSocket = previous;
    for (const key of ['XDG_CONFIG_HOME','XDG_DATA_HOME','XDG_DATA_DIRS','OPENXLR_SKIN']) {
      if (oldEnv[key] === undefined) delete process.env[key]; else process.env[key] = oldEnv[key];
    }
    fs.rmSync(root,{recursive:true,force:true});
  }
});
