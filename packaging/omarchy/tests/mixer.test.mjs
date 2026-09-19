import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const mixer = vm.createContext({});
vm.runInContext(readFileSync(new URL('../openxlr.mixer/Mixer.js', import.meta.url), 'utf8'), mixer);
const plain = value => JSON.parse(JSON.stringify(value));
const state = { type: 'state', connected: true, state: { mute: false }, mixer: {
  channels: [{ id: 'xlr1', name: 'XLR 1', hardware: true, levels: { monitor: 0.4 }, mutedIn: ['stream'] }],
  mixes: [{ id: 'monitor', name: 'Monitor A', kind: 'monitor', volume: 1.25 },
    { id: 'stream', name: 'Stream', kind: 'virtualMic', volume: 0.8 }],
  monitorOutputs: ['headset', 'speakers'], monitorFeeds: { speakers: 'monitor+stream' }
} };

function setup() {
  const events = [];
  const timers = {};
  const io = {};
  for (const method of ['readToken', 'connect', 'send', 'close', 'changed'])
    io[method] = value => events.push([method, value === undefined ? null : plain(value)]);
  for (const method of ['retry', 'deadline', 'meterDeadline', 'commandDeadline'])
    io[method] = value => { timers[method] = value; };
  io.cancelTimers = () => { for (const key of Object.keys(timers)) timers[key] = 0; };
  const session = new mixer.Session(io);
  const receive = message => session.receive(JSON.stringify(message));
  function ready() {
    session.start(); session.token('first-token\n'); session.opened('first-token'); receive(state);
  }
  return { session, events, timers, receive, ready };
}

test('token paths match the daemon, including a separate config home', () => {
  assert.equal(mixer.tokenPath('/run/user/123', '/config', '/home/me'), '/run/user/123/openxlr/token');
  assert.equal(mixer.tokenPath('', '/config', '/home/me'), '/config/openxlr/token');
  assert.equal(mixer.tokenPath('', '', '/home/me'), '/home/me/.config/openxlr/token');
});

test('bar names shorten only the leading Monitor label', () => {
  assert.equal(mixer.barName('Monitor A'), 'Mon A');
  assert.equal(mixer.barName('Monitor B'), 'Mon B');
  assert.equal(mixer.barName('Monitor Studio'), 'Mon Studio');
  assert.equal(mixer.barName('Stream'), 'Stream');
  assert.equal(mixer.barName('My Monitor A'), 'My Monitor A');
  assert.equal(mixer.barName('monitor A'), 'monitor A');
});

test('input visibility lasts ten seconds from the last above-floor frame', () => {
  const seen = { xlr1: 1000, xlr2: 5000, music: 9000 };
  assert.deepEqual(plain(mixer.shownInputs(seen, 10999)), ['xlr1', 'xlr2']);
  assert.deepEqual(plain(mixer.shownInputs(seen, 11000)), ['xlr2']);
  assert.deepEqual(plain(mixer.shownInputs(seen, 15000)), []);
  assert.deepEqual(plain(mixer.shownInputs({}, 1000)), []);
  assert.deepEqual(plain(mixer.shownInputs({ xlr1: NaN, xlr2: Infinity }, 1000)), []);
  assert.deepEqual(seen, { xlr1: 1000, xlr2: 5000, music: 9000 });
});

test('the feed meter uses the louder stereo side and loudest mix in a sum', () => {
  const levels = { 'mix:monitor': [0.2, 0.8], 'mix:stream': [0.9, 0.1] };
  assert.equal(mixer.feedLevel(levels, ['monitor']), 0.8);
  assert.equal(mixer.feedLevel(levels, ['monitor', 'stream']), 0.9);
  assert.equal(mixer.feedLevel(levels, ['missing']), 0);
  assert.equal(mixer.feedLevel(levels, []), 0);
});

test('theme slugs select a shipped palette and unknown or unreadable themes fall back', () => {
  const skins = vm.createContext({});
  vm.runInContext(readFileSync(new URL('../openxlr.mixer/Skins.js', import.meta.url), 'utf8'), skins);
  assert.equal(mixer.themePalette(skins.palettes, 'tokyo-night\n'), skins.palettes['tokyo-night']);
  assert.equal(mixer.themePalette(skins.palettes, 'catppuccin'), skins.palettes.catppuccin);
  assert.equal(mixer.themePalette(skins.palettes, 'unmatched-theme'), null);
  assert.equal(mixer.themePalette(skins.palettes, ''), null);
  assert.equal(mixer.themePalette(skins.palettes, null), null);
  assert.equal(mixer.themePalette(skins.palettes, '__proto__'), null);
  assert.equal(skins.palettes['matte-black'].warningLevel, 0.7);
  assert.equal(skins.palettes.gruvbox.hotLevel, 0.9);
});

test('auth is first, getState follows, and only state enables commands', () => {
  const f = setup();
  f.session.start();
  assert.equal(f.timers.deadline, 5000);
  f.session.token(' secret\n');
  f.session.opened('secret');
  assert.deepEqual(f.events.filter(e => e[0] === 'send').map(e => e[1]), [
    { cmd: 'auth', token: 'secret' }, { cmd: 'getState' }
  ]);
  assert.equal(f.session.send({ cmd: 'setMixMuted', mix: 'monitor', value: true }), false);
  f.receive(state);
  assert.equal(f.session.ready, true);
  assert.equal(f.timers.deadline, 0);
});

test('an absent token backs off once, even when close also signals failure', () => {
  const f = setup();
  for (const delay of [1000, 2000, 4000, 8000, 10000, 10000]) {
    f.session.start(); f.session.token(''); f.session.lost();
    assert.equal(f.timers.retry, delay);
  }
  assert.equal(f.events.filter(e => e[0] === 'connect').length, 0);
  assert.equal(f.events.filter(e => e[0] === 'readToken').length, 6);
});

test('reconnect rereads the rotated token and never replays an uncertain edit', () => {
  const f = setup(); f.ready();
  f.session.send({ cmd: 'setLevel', channel: 'xlr1', mix: 'monitor', value: 0.6 });
  f.session.lost();
  assert.equal(f.session.ready, false);
  assert.equal(f.session.snapshot, null);
  assert.equal(f.session.pending, '');
  f.receive(state);
  assert.equal(f.session.snapshot, null);
  f.session.start(); f.session.token('rotated'); f.session.opened('rotated'); f.receive(state);
  const sent = f.events.filter(e => e[0] === 'send').map(e => e[1]);
  assert.equal(sent.filter(e => e.cmd === 'setLevel').length, 1);
  assert.deepEqual(sent.at(-2), { cmd: 'auth', token: 'rotated' });
  assert.equal(f.session.backoff, 1000);
});

test('one pending command waits for its own result and retains daemon errors', () => {
  const f = setup(); f.ready();
  assert.equal(f.session.send({ cmd: 'setMixMuted', mix: 'stream', value: true }), true);
  const id = f.session.pending;
  assert.equal(f.session.send({ cmd: 'setMixMuted', mix: 'stream', value: false }), false);
  f.receive({ type: 'commandResult', requestId: 'elsewhere', error: null });
  assert.equal(f.session.pending, id);
  f.receive({ type: 'commandResult', requestId: id, error: 'The mix no longer exists' });
  assert.equal(f.session.pending, '');
  assert.equal(f.session.error, 'The mix no longer exists');
  assert.equal(f.timers.commandDeadline, 0);
});

test('an unanswered command reconnects and teardown cancels all timers', () => {
  const f = setup(); f.ready();
  f.session.send({ cmd: 'setMixVolume', mix: 'monitor', value: 0.7 });
  f.session.commandExpired();
  assert.equal(f.session.ready, false);
  assert.match(f.session.error, /may have reached/);
  f.session.stop(); f.session.lost(); f.session.start();
  assert.equal(f.session.phase, 'stopped');
  assert.ok(Object.values(f.timers).every(value => value === 0));
});

test('meter frames replace missing channels and become silence after a second', () => {
  const f = setup(); f.ready();
  f.receive({ type: 'meters', levels: { 'ch:xlr1': [0.4, 0.4], 'mix:monitor': [0.3, 0.9] } });
  assert.deepEqual(plain(mixer.pair(f.session.levels, 'mix:monitor', false)), [0.3, 0.9]);
  f.receive({ type: 'meters', levels: { 'ch:xlr1': [0.2] } });
  assert.deepEqual(plain(mixer.pair(f.session.levels, 'mix:monitor', false)), [0, 0]);
  assert.equal(f.timers.meterDeadline, 1000);
  f.session.metersExpired();
  assert.deepEqual(plain(f.session.levels), {});
  f.session.receive('{broken');
  f.session.receive('null');
  assert.equal(f.session.ready, true);
});

test('stereo stays apart, XLR is mono, and invalid levels do not poison the other side', () => {
  assert.deepEqual(plain(mixer.pair({ x: [0.2, 0.8] }, 'x', false)), [0.2, 0.8]);
  assert.deepEqual(plain(mixer.pair({ x: [0.2, 0.8] }, 'x', true)), [0.8, 0.8]);
  assert.deepEqual(plain(mixer.pair({ x: [NaN, 0.8] }, 'x', false)), [0, 0.8]);
  assert.deepEqual(plain(mixer.pair({ x: [Infinity, -1] }, 'x', false)), [0, 0]);
  assert.equal(mixer.mono('xlr2'), true);
  assert.equal(mixer.mono('aux'), false);
});

test('block glyphs use the TUI fractions and its dBFS scale', () => {
  assert.equal(mixer.horizontalGlyph(0.25, 0, 2), '▌');
  assert.equal(mixer.horizontalGlyph(0.25, 1, 2), '─');
  assert.equal(mixer.horizontalGlyph(1, 1, 2), '█');
  assert.equal(mixer.verticalGlyph(0.125, 0, 1), '▁');
  assert.equal(mixer.verticalGlyph(0.875, 0, 1), '▇');
  assert.equal(mixer.rms(0), '<-60');
  assert.equal(mixer.rms(0.7), '-18');
  assert.equal(mixer.rms(0.9), '-6');
});

test('monitor labels describe default feeds, sums, silent routes and custom names', () => {
  assert.deepEqual(plain(mixer.monitorFeeds(state.mixer)), [
    { output: 'headset', ids: ['monitor'], name: 'Monitor A' },
    { output: 'speakers', ids: ['monitor', 'stream'], name: 'Monitor A + Stream' }
  ]);
  assert.equal(mixer.monitorFeeds({ ...state.mixer, monitorFeeds: { headset: '' } })[0].name, 'Silent');
  assert.deepEqual(plain(mixer.monitorFeeds({ mixes: [] })), []);
});

test('output labels use the documented device description and tolerate state transitions', () => {
  const node = 'alsa_output.usb-Elgato_Systems_Elgato_XLR_Dock.serial.analog-stereo';
  const snapshot = { devices: [{ name: node, description: 'Elgato XLR Dock', kind: 0 }] };
  assert.equal(mixer.outputName(snapshot, node), 'Elgato XLR Dock');
  assert.equal(mixer.outputName(snapshot, 'missing'), 'missing');
  assert.equal(mixer.outputName(null, node), node);
  assert.equal(mixer.outputName({}, node), node);
  assert.equal(mixer.outputName({ devices: [{ name: node, description: ' ' }] }, node), node);
});

test('send edits address the selected mix and monitor masters alone reach 150 percent', () => {
  const channel = state.mixer.channels[0];
  assert.deepEqual(plain(mixer.volumeCommand(channel, 'stream', false, 1.3)),
    { cmd: 'setLevel', channel: 'xlr1', mix: 'stream', value: 1 });
  assert.deepEqual(plain(mixer.muteCommand(channel, 'stream', false)),
    { cmd: 'setChannelMuted', channel: 'xlr1', mix: 'stream', value: false });
  assert.deepEqual(plain(mixer.volumeCommand(state.mixer.mixes[0], '', true, 1.5)),
    { cmd: 'setMixVolume', mix: 'monitor', value: 1.5 });
  assert.equal(mixer.volumeCommand(state.mixer.mixes[1], '', true, 1.5).value, 1);
  assert.equal(mixer.muteCommand({ id: 'custom', muted: true }, '', true).value, false);
});
