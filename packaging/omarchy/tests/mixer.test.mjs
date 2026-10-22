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
  const channels = [{ id: "xlr1" }, { id: "xlr2" }, { id: "music" }];
  const seen = { xlr1: 1000, xlr2: 5000, music: 9000 };
  assert.deepEqual(plain(mixer.shownInputs(channels, seen, 10999)), ['xlr1', 'xlr2']);
  assert.deepEqual(plain(mixer.shownInputs(channels, seen, 11000)), ['xlr2']);
  assert.deepEqual(plain(mixer.shownInputs(channels, seen, 15000)), []);
  assert.deepEqual(plain(mixer.shownInputs(channels, {}, 1000)), []);
  assert.deepEqual(plain(mixer.shownInputs(channels, { xlr1: NaN, xlr2: Infinity }, 1000)), []);
  assert.deepEqual(seen, { xlr1: 1000, xlr2: 5000, music: 9000 });
});

test('the bar holds at most four microphones in layout order and leaves stereo captures out', () => {
  const channels = [
    {id:'guest', microphone:true}, {id:'xlr2'}, {id:'line', microphone:false},
    {id:'usb', microphone:true}, {id:'xlr1'}, {id:'fifth', microphone:true},
    {id:'absent', microphone:true, present:false}, {id:'old-capture', captureSource:'usb'},
  ];
  const seen = Object.fromEntries(channels.map(c => [c.id, 1000]));
  assert.deepEqual(plain(mixer.shownInputs(channels, seen, 10999)), ['guest', 'xlr2', 'usb', 'xlr1']);
  seen.guest = 999;
  assert.deepEqual(plain(mixer.shownInputs(channels, seen, 10999)), ['xlr2', 'usb', 'xlr1', 'fifth']);
  assert.deepEqual(plain(mixer.shownInputs(channels, seen, 11000)), []);
  assert.equal(mixer.mono({id:'xlr1', microphone:false}), false);
  assert.equal(mixer.mono({id:'usb', microphone:true}), true);
  assert.equal(mixer.mono({id:'old-capture', captureMonoChannel:0}), false);
  assert.deepEqual(plain(mixer.pair({'ch:guest':[0.2,0.7]}, 'ch:guest', mixer.mono(channels[0]))), [0.7,0.7]);
});

test('capture carriers keep primary and additional mute targets after their unit source is renamed', () => {
  const primary = 'pro', additional = 'wave3';
  const snapshot = {connected:true, capabilities:{mute:true}, state:{mute2:true},
    waveInterfaces:[{id:additional, enabled:true, connected:true, capabilities:{mute:true}, state:{mute:false}}],
    devices:[{name:'renamed.primary', kind:1, drivenUnit:primary}, {name:'renamed.additional', kind:1, drivenUnit:additional}],
    mixer:{channels:[
      {id:'pro-mic', captureSource:'old.primary', microphone:true, binding:{unit:primary},
        unitInput:{unit:primary,input:2,primary:true}},
      {id:'guest', captureSource:'old.additional', microphone:true, binding:{unit:additional},
        unitInput:{unit:additional,input:1,primary:false}},
    ]}};
  assert.deepEqual(plain(mixer.muteTarget(snapshot, 'pro-mic')), {device:'',control:'mute2'});
  assert.deepEqual(plain(mixer.muteCommandFor(snapshot, 'pro-mic', false)), {cmd:'set',control:'mute2',value:false});
  assert.deepEqual(plain(mixer.muteCommandFor(snapshot, 'guest', true)),
    {cmd:'setWaveControl',device:additional,control:'mute',value:true});
  assert.equal(mixer.hardwareMuted(snapshot, 'pro-mic'), true);
  assert.equal(mixer.muteAvailable(snapshot, 'guest'), true);
  // An older state has neither the microphone flag nor followed unit binding.
  for (const channel of snapshot.mixer.channels) {
    delete channel.microphone;
    delete channel.binding.unit;
  }
  assert.equal(mixer.muteAvailable(snapshot, 'pro-mic'), true);
  assert.equal(mixer.muteAvailable(snapshot, 'guest'), true);
  delete snapshot.mixer.channels[0].unitInput;
  delete snapshot.mixer.channels[1].unitInput;
  assert.equal(mixer.muteTarget(snapshot, 'pro-mic'), null);
  assert.equal(mixer.muteTarget(snapshot, 'guest'), null);
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
  assert.equal(mixer.mono({id:'xlr2'}), true);
  assert.equal(mixer.mono({id:'aux'}), false);
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

test('monitor labels describe default feeds, sums and custom names', () => {
  assert.deepEqual(plain(mixer.monitorFeeds(state.mixer)), [
    { output: 'headset', ids: ['monitor'], name: 'Monitor A' },
    { output: 'speakers', ids: ['monitor', 'stream'], name: 'Monitor A + Stream' }
  ]);
  assert.equal(mixer.monitorFeeds({ ...state.mixer, monitorFeeds: { headset: '' } })[0].name, 'Monitor A');
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

test('a channel the device cannot feed is left out, and an older daemon keeps them all', () => {
  // Only the Wave XLR Pro has a second XLR jack and the aux input stage, so
  // the daemon marks those channels absent on every other model.
  const channels = [
    { id: 'xlr1', name: 'XLR 1', present: true },
    { id: 'xlr2', name: 'XLR 2', present: false },
    { id: 'aux', name: 'Aux In', present: false },
    { id: 'game', name: 'Game' },
  ];
  assert.deepEqual(plain(mixer.shownChannels(channels)).map(c => c.id), ['xlr1', 'game']);
  // A daemon that does not send the field says nothing, not absent.
  assert.deepEqual(plain(mixer.shownChannels([{ id: 'xlr2' }])).map(c => c.id), ['xlr2']);
  assert.deepEqual(plain(mixer.shownChannels(null)), []);
  assert.deepEqual(plain(mixer.shownChannels(undefined)), []);
});

test('the interface mute follows the channel that carries its input', () => {
  const unit = '0fd9:00b4@0123456789abcdef';
  const source = 'alsa_input.usb-Blue_Yeti_Nano';
  const snapshot = { connected: true, state: { mute: true, mute2: false },
    devices: [{ name: source, description: 'Yeti Nano', kind: 1 }],
    mixer: { ...state.mixer, channels: [
      { id: 'xlr1', name: 'Mic 1', hardware: true, binding: { kind: 'source', source, pair: 0, monoChannel: 1 },
        bindingConnected: true },
      { id: 'xlr2', name: 'XLR 2', hardware: true, binding: { kind: 'unit', pair: 1 },
        unitInput: { unit, input: 2, primary: true } },
      { id: 'cap1', name: 'Pro mic', captureSource: 'alsa_input.usb-Elgato_Wave_XLR_Pro', captureConnected: true,
        unitInput: { unit, input: 1, primary: true } },
    ] } };
  const [xlr1, xlr2, capture] = snapshot.mixer.channels;
  assert.equal(mixer.unitCarrier(snapshot.mixer, 1).id, 'cap1');
  assert.equal(mixer.unitCarrier(snapshot.mixer, 2).id, 'xlr2');
  assert.equal(mixer.muteControl(snapshot, 'xlr1'), '');
  assert.equal(mixer.muteControl(snapshot, 'cap1'), 'mute');
  assert.equal(mixer.muteControl(snapshot, 'xlr2'), 'mute2');
  assert.equal(mixer.hardwareMuted(snapshot, 'xlr1'), false);
  assert.equal(mixer.hardwareMuted(snapshot, 'cap1'), true);
  assert.equal(mixer.channelName(snapshot.mixer.channels, 'xlr1'), 'Mic 1');
  assert.equal(mixer.channelName(snapshot.mixer.channels, 'aux'), '');

  // What feeds a bound strip, on its strip and in the tooltip.
  assert.equal(mixer.kindLine(snapshot, xlr1), 'Yeti Nano');
  assert.equal(mixer.bindingNote(snapshot, xlr1), 'Yeti Nano, port 2');
  assert.equal(mixer.kindLine(snapshot, xlr2), 'INPUT');
  assert.equal(mixer.bindingNote(snapshot, xlr2), '');
  assert.equal(mixer.kindLine(snapshot, capture), 'CAPTURE');
  xlr1.bindingConnected = false;
  assert.equal(mixer.kindLine(snapshot, xlr1), 'Yeti Nano OFF');
  assert.equal(mixer.bindingNote(snapshot, xlr1), 'Yeti Nano, port 2, not linked');
  xlr1.bindingWarning = 'Yeti Nano is not connected.';
  assert.equal(mixer.bindingNote(snapshot, xlr1), 'Yeti Nano is not connected.');
  assert.equal(mixer.kindLine({}, xlr1), `${source} OFF`);

  // The daemon names input 2 but not input 1: nothing carries input 1.
  delete capture.unitInput;
  assert.equal(mixer.unitCarrier(snapshot.mixer, 1), null);
  assert.equal(mixer.muteControl(snapshot, 'cap1'), '');

  // An older daemon, or no unit connected: the strips on the interface stand in.
  const old = { state: { mute: true }, mixer: { channels: [{ id: 'xlr1', hardware: true }, { id: 'xlr2', hardware: true }] } };
  assert.equal(mixer.muteControl(old, 'xlr1'), 'mute');
  assert.equal(mixer.muteControl(old, 'xlr2'), 'mute2');
  assert.equal(mixer.hardwareMuted(old, 'xlr1'), true);
  assert.equal(mixer.muteControl(null, 'xlr1'), '');
  assert.equal(mixer.muteControl({ mixer: { channels: 'nonsense' } }, 'xlr1'), '');
  assert.equal(mixer.hardwareMuted({ mixer: old.mixer }, 'xlr1'), false);
});

test('a bound strip names its unit after a capture node rename without guessing between nodes', () => {
  const unit = '0fd9:0070@fedcba9876543210';
  const source = 'alsa_input.usb-Elgato_Wave_3.mono-fallback';
  const channel = { id: 'xlr1', hardware: true, bindingConnected: true,
    binding: { kind: 'source', source, monoChannel: 0, unit } };
  const renamed = { name: 'alsa_input.usb-Elgato_Wave_3.analog-stereo', description: 'Wave:3', kind: 1, drivenUnit: unit };
  const snapshot = { devices: [renamed,
    { name: 'alsa_output.usb-Elgato_Wave_3', description: 'Headphones', kind: 0, drivenUnit: unit },
    { name: 'OpenXLR_Chat', description: 'Chat', kind: 1, isOwn: true, drivenUnit: unit }] };
  assert.equal(mixer.kindLine(snapshot, channel), 'Wave:3');
  assert.equal(mixer.bindingNote(snapshot, channel), 'Wave:3, port 1');

  // The saved node wins while it still exists, even if the unit publishes another source.
  snapshot.devices.push({ name: source, description: 'Saved microphone', kind: 1, drivenUnit: unit });
  assert.equal(mixer.kindLine(snapshot, channel), 'Saved microphone');
  snapshot.devices.pop();

  // Two candidate sources are ambiguous; another unit is never a replacement.
  snapshot.devices.push({ ...renamed, name: 'alsa_input.usb-Elgato_Wave_3.pro-input-0' });
  assert.equal(mixer.kindLine(snapshot, channel), source);
  assert.equal(mixer.bindingNote(snapshot, channel), `${source}, port 1`);
  snapshot.devices = [{ ...renamed, drivenUnit: 'another-unit' }];
  assert.equal(mixer.kindLine(snapshot, channel), source);
  delete channel.binding.unit;
  snapshot.devices = [renamed];
  assert.equal(mixer.kindLine(snapshot, channel), source);
});

test('a strip on a driven additional unit carries that unit\'s mute', () => {
  const wave3 = '0fd9:0070@fedcba9876543210';
  const snapshot = { connected: true, state: { mute: false }, capabilities: { mute: true },
    waveInterfaces: [
      { id: '0fd9:00b4@0123456789abcdef', active: true, enabled: false, connected: true },
      { id: wave3, active: false, enabled: true, connected: true, capabilities: { mute: true }, state: { mute: true } },
    ],
    mixer: { ...state.mixer, channels: [
      { id: 'xlr1', name: 'XLR 1', hardware: true, binding: { kind: 'unit', pair: 0 },
        unitInput: { unit: '0fd9:00b4@0123456789abcdef', input: 1, primary: true } },
      { id: 'xlr2', name: 'Mic 2', hardware: true,
        binding: { kind: 'source', source: 'alsa_input.usb-Elgato_Wave_3', pair: 0, monoChannel: 0, unit: wave3 },
        unitInput: { unit: wave3, input: 1, primary: false } },
    ] } };
  assert.deepEqual(plain(mixer.muteTarget(snapshot, 'xlr2')), { device: wave3, control: 'mute' });
  assert.deepEqual(plain(mixer.muteTarget(snapshot, 'xlr1')), { device: '', control: 'mute' });
  assert.equal(mixer.hardwareMuted(snapshot, 'xlr2'), true);
  assert.equal(mixer.hardwareMuted(snapshot, 'xlr1'), false);
  assert.equal(mixer.muteAvailable(snapshot, 'xlr2'), true);
  assert.deepEqual(plain(mixer.muteCommandFor(snapshot, 'xlr2', false)),
    { cmd: 'setWaveControl', device: wave3, control: 'mute', value: false });
  assert.deepEqual(plain(mixer.muteCommandFor(snapshot, 'xlr1', true)), { cmd: 'set', control: 'mute', value: true });
  // The primary's own input 2 is not on this strip: the primary's mute2 never dims it.
  snapshot.state.mute2 = true;
  snapshot.waveInterfaces[1].state.mute = false;
  assert.equal(mixer.hardwareMuted(snapshot, 'xlr2'), false);
  // Unplugged: the mute is still the unit's, but cannot be written.
  snapshot.waveInterfaces[1].connected = false;
  assert.equal(mixer.muteAvailable(snapshot, 'xlr2'), false);
  // Not driven: no hardware mute on the strip at all.
  snapshot.waveInterfaces[1].enabled = false;
  assert.equal(mixer.muteTarget(snapshot, 'xlr2'), null);
  assert.equal(mixer.muteCommandFor(snapshot, 'xlr2', true), null);
});

test('implicit monitor uses the explicit daemon default or older daemon layout order', () => {
  const source = {monitorOutputs:['headset'], mixes:[
    {id:'stream', name:'Stream', kind:'virtualMic'},
    {id:'monitor2', name:'Studio', kind:'monitor'},
    {id:'aaa', name:'Speakers', kind:'monitor'}]};
  assert.equal(mixer.monitorFeeds({...source, defaultMonitorMix:'monitor2'})[0].name, 'Studio');
  assert.equal(mixer.monitorFeeds({...source, mixes:[...source.mixes].reverse(), defaultMonitorMix:'monitor2'})[0].name, 'Studio');
  assert.equal(mixer.monitorFeeds(source)[0].name, 'Studio');
  assert.equal(mixer.monitorFeeds({...source, defaultMonitorMix:'removed'})[0].name, 'Studio');
  assert.equal(mixer.monitorFeeds({...source, mixes:[...source.mixes].reverse()})[0].name, 'Speakers');
});

test('monitor master controls show the pause and explicitly open just that monitor', () => {
  const paused = {mixer:{monitorMuted:true}};
  const monitor = {id:'monitor',kind:'monitor',muted:false};
  const stream = {id:'stream',kind:'virtualMic',muted:false};
  assert.equal(mixer.masterMuted(monitor, paused), true);
  assert.deepEqual(plain(mixer.muteCommand(monitor, '', true, paused)), {cmd:'setMixMuted',mix:'monitor',value:false});
  assert.equal(mixer.masterMuted(stream, paused), false);
  assert.deepEqual(plain(mixer.muteCommand(stream, '', true, paused)), {cmd:'setMixMuted',mix:'stream',value:true});
  assert.equal(monitor.muted, false);
  assert.equal(mixer.masterMuted(monitor, {}), false);
});


test('saved monitor feeds discard deleted ids and whitespace like the daemon', () => {
  const source = { mixes:[{id:'monitor2',name:'Studio',kind:'monitor'},{id:'chat',name:'Chat',kind:'virtualMic'}],
    monitorOutputs:['headset'],defaultMonitorMix:'monitor2' };
  for (const [saved, ids] of [['', ['monitor2']], ['gone', ['monitor2']], [' gone + chat ', ['chat']],
    [' chat + monitor2 + chat ', ['monitor2','chat']]]) {
    assert.deepEqual(plain(mixer.monitorFeeds({...source,monitorFeeds:{headset:saved}}))[0].ids, ids);
  }
});
