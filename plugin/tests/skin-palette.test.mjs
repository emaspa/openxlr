import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {execFileSync, spawnSync} from 'node:child_process';
import {SkinPalette, paletteFrom} from '../com.emaspa.openxlr.sdPlugin/skin-palette.mjs';

const CARD = 'Ox.Card.Background', INK = 'Ox.Text.Primary';
const defaults = paletteFrom({});
function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'openxlr-deck-'));
  t.after(() => fs.rmSync(root, {recursive:true, force:true}));
  const env = {XDG_CONFIG_HOME:path.join(root, 'config'), XDG_DATA_HOME:path.join(root, 'data'), XDG_DATA_DIRS:path.join(root, 'system')};
  const ui = path.join(env.XDG_CONFIG_HOME, 'openxlr/ui.json');
  const live = path.join(env.XDG_CONFIG_HOME, 'openxlr/deck-palette.json');
  function write(file, object) {
    fs.mkdirSync(path.dirname(file), {recursive:true});
    fs.writeFileSync(file + '.tmp', JSON.stringify(object));
    fs.renameSync(file + '.tmp', file);
  }
  const skin = (id, tokens, where = 'data') => {
    const file = path.join(root, where, 'openxlr/skins', id, 'skin.json');
    write(file, {schema:1, tokens});
    return file;
  };
  return {root, env, ui, live, write, skin};
}
async function until(condition) {
  const deadline = Date.now() + 4000;
  while (!condition() && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 20));
  assert.ok(condition(), 'file event did not produce the expected palette');
}

test('partial palettes validate flat colours, alpha, gradients and meter bounds', () => {
  assert.equal(paletteFrom({[CARD]:'#abc'})[CARD], '#aabbcc');
  assert.equal(paletteFrom({[CARD]:'#8abc'})[CARD], '#aabbcc88');
  assert.equal(paletteFrom({[CARD]:'#cc112233'})[CARD], '#112233cc');
  assert.equal(paletteFrom({[CARD]:'white'})[CARD], '#ffffff');
  const gradient = {type:'linear', stops:[{offset:0,color:'#abcdef'}, {offset:1,color:'#123456'}]};
  assert.equal(paletteFrom({[CARD]:gradient})[CARD], '#abcdef');
  assert.equal(paletteFrom({'Ox.Led.On':gradient})['Ox.Led.On'], defaults['Ox.Led.On']);
  for (const value of ['url(https://host)', '#fff"/><script/>', {type:'image',path:'../../token'}, {type:'linear',stops:[{offset:0,color:'#fff'}]}, {type:'radial',stops:[{offset:1,color:'#fff'}, {offset:0,color:'#000'}]}])
    assert.equal(paletteFrom({[CARD]:value})[CARD], defaults[CARD]);
  for (const value of [null, '0.7', NaN, Infinity, -1, 2])
    assert.equal(paletteFrom({'Ox.Meter.WarningLevel':value})['Ox.Meter.WarningLevel'], defaults['Ox.Meter.WarningLevel']);
  const crossed = paletteFrom({'Ox.Meter.WarningLevel':.95, 'Ox.Meter.HotLevel':.2});
  assert.equal(crossed['Ox.Meter.WarningLevel'], .7);
  assert.equal(crossed['Ox.Meter.HotLevel'], .9);
  assert.equal(paletteFrom({[CARD]:'#123456'})[INK], defaults[INK]);
});

test('inherited object names are not colour names or valid gradient stops', () => {
  for (const value of ['constructor', 'CONSTRUCTOR', '__proto__']) {
    for (const key of [CARD, INK, 'Ox.Led.On', 'Ox.Meter.Fill'])
      assert.equal(paletteFrom({[key]:value})[key], defaults[key], `${key}: ${value}`);
    for (const type of ['linear', 'radial']) {
      for (const badStop of [0, 1]) {
        const stops = [{offset:0, color:'#112233'}, {offset:1, color:'#abcdef'}];
        stops[badStop].color = value;
        assert.equal(paletteFrom({[CARD]:{type, stops}})[CARD], defaults[CARD], `${type}: ${badStop}: ${value}`);
      }
    }
  }
  assert.equal(paletteFrom({[CARD]:'WHITE'})[CARD], '#ffffff');
});

test('saved choice, root precedence and override use the same bounded ids', t => {
  const f = fixture(t);
  f.write(f.ui, {skin:'opendeck'});
  const p = new SkinPalette(null, {env:f.env}); t.after(() => p.close());
  assert.equal(p.id, 'opendeck');
  assert.equal(p.colours[CARD], '#131313');
  f.skin('opendeck', {[CARD]:'#111111'}, 'system');
  f.skin('opendeck', {[CARD]:'#222222'}); p.reload();
  assert.equal(p.colours[CARD], '#222222');
  f.write(f.ui, {skin:'../../outside'}); p.reload();
  assert.equal(p.id, 'default');
  assert.deepEqual(p.colours, defaults);
  f.write(f.ui, {skin:{id:'opendeck'}}); p.reload();
  assert.deepEqual(p.colours, defaults);
  const forced = new SkinPalette(null, {env:{...f.env, OPENXLR_SKIN:'opendeck'}}); t.after(() => forced.close());
  assert.equal(forced.colours[CARD], '#222222');
});

test('invalid, oversized and nonregular documents leave valid fallbacks', t => {
  const f = fixture(t);
  f.write(f.ui, {skin:'local'});
  const file = f.skin('local', {[CARD]:'#112233'});
  const p = new SkinPalette(null, {env:f.env}); t.after(() => p.close());
  for (const text of ['[]', '{', JSON.stringify({schema:2,tokens:{[CARD]:'#ffffff'}}), ' '.repeat(256*1024+1)]) {
    fs.writeFileSync(file, text); p.reload(); assert.deepEqual(p.colours, defaults);
  }
  fs.unlinkSync(file); fs.mkdirSync(file); p.reload(); assert.deepEqual(p.colours, defaults);
  fs.rmdirSync(file);
  if (process.platform === 'linux') {
    execFileSync('mkfifo', [file]);
    const child = spawnSync(process.execPath, ['--input-type=module', '-e',
      `import {SkinPalette} from ${JSON.stringify(new URL('../com.emaspa.openxlr.sdPlugin/skin-palette.mjs', import.meta.url).href)}; const p=new SkinPalette(null,{env:${JSON.stringify(f.env)}}); p.close();`], {timeout:2000});
    assert.ifError(child.error); assert.equal(child.status, 0, child.stderr.toString());
    fs.unlinkSync(file);
  }
  f.skin('local', {[CARD]:'#112233'}); p.reload(); assert.equal(p.colours[CARD], '#112233');
});

test('realised window palette follows matching skin, including Material light', t => {
  const f = fixture(t);
  f.write(f.ui, {skin:'default'});
  f.write(f.live, {schema:1,pid:process.pid,skin:'default',tokens:{[CARD]:'#fffaff', [INK]:'#ff010203'}});
  const p = new SkinPalette(null, {env:f.env}); t.after(() => p.close());
  assert.equal(p.colours[CARD], '#fffaff'); assert.equal(p.colours[INK], '#010203ff');
  f.write(f.live, {schema:1,pid:2147483647,skin:'default',tokens:{[CARD]:'#ffffff'}});
  p.reload(); assert.deepEqual(p.colours, defaults, 'an exited publisher must not hide skin edits');
  f.write(f.ui, {skin:'opendeck'}); p.reload(); assert.equal(p.colours[CARD], '#131313');
  const forced = new SkinPalette(null, {env:{...f.env,OPENXLR_SKIN:'default'}}); t.after(() => forced.close());
  assert.deepEqual(forced.colours, defaults);
});

test('events follow atomic saves, late directories and removal without polling', async t => {
  const f = fixture(t);
  let changes = 0;
  const p = new SkinPalette(() => changes++, {env:f.env}); t.after(() => p.close());
  f.write(f.ui, {skin:'newskin'});
  await until(() => p.id === 'newskin');
  const file = f.skin('newskin', {[CARD]:'#123456'});
  await until(() => p.colours[CARD] === '#123456');
  f.write(file, {schema:1,tokens:{[CARD]:'#654321'}});
  await until(() => p.colours[CARD] === '#654321');
  const before = changes;
  f.write(file, {schema:1,tokens:{[CARD]:'#654321'}});
  await new Promise(resolve => setTimeout(resolve, 150));
  assert.equal(changes, before, 'unchanged colours must not redraw');
  fs.rmSync(path.dirname(file), {recursive:true});
  await until(() => p.colours[CARD] === defaults[CARD]);
  f.skin('newskin', {[CARD]:'#abcdef'});
  await until(() => p.colours[CARD] === '#abcdef');
  f.write(f.live, {schema:1,pid:process.pid,skin:'newskin',tokens:{[CARD]:'#fafafa'}});
  await until(() => p.colours[CARD] === '#fafafa');
  p.close(); const closedChanges = changes;
  f.write(f.ui, {skin:'opendeck'});
  await new Promise(resolve => setTimeout(resolve, 150));
  assert.equal(changes, closedChanges); assert.equal(p.watchers.length, 0);
});
