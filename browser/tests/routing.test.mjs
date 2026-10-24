import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { deviceFor, errorText, MAX_CAPTURES, notInvoked, siteOf, tabRecords, TabAudio } from '../chromium/routing.mjs';

const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
};

function fixture({ acquire, sink, resume, close, timeoutMs } = {}) {
  const contexts = [], streams = [], statuses = [], acquisitions = [];
  const acquired = deferred();
  const makeStream = () => {
    const listeners = new Map();
    const stopped = deferred();
    const track = { stopped: false, stop() { this.stopped = true; stopped.resolve(); }, addEventListener(name, callback) { listeners.set(name, callback); } };
    const stream = { track, stopped: stopped.promise, end: () => listeners.get('ended')?.(), getTracks: () => [track], getAudioTracks: () => [track] };
    streams.push(stream);
    return stream;
  };
  const mediaDevices = {
    async getUserMedia(constraints) {
      acquisitions.push(constraints); acquired.resolve();
      return acquire ? acquire(makeStream) : makeStream();
    },
    async enumerateDevices() { return [{ kind: 'audiooutput', deviceId: 'music' }, { kind: 'audiooutput', deviceId: 'chat' }]; },
  };
  const audio = new TabAudio({ mediaDevices, timeoutMs, status: (...args) => statuses.push(args), createContext() {
    const context = { closed: false, destination: {}, async setSinkId(id) { if (sink) await sink(id); this.sink = id; },
      createMediaStreamSource() { return { connect() {}, disconnect() {} }; },
      async resume() { if (resume) await resume(); }, async close() { if (close) await close(); this.closed = true; } };
    contexts.push(context); return context;
  } });
  return { audio, contexts, streams, statuses, acquisitions, acquired, mediaDevices };
}

test('only canonical website origins leave the browser; paths and queries do not', () => {
  assert.equal(siteOf('https://teams.microsoft.com/v2/?secret=a#call'), 'https://teams.microsoft.com');
  assert.equal(siteOf('https://name:password@example.org/private'), 'https://example.org');
  assert.equal(siteOf('http://localhost:8080/player'), 'http://localhost:8080');
  for (const url of ['chrome://settings', 'file:///home/person/private', 'data:text/html,hello', 'about:blank', undefined, '-']) assert.equal(siteOf(url), null);
});

test('tab metadata excludes private/restricted tabs, bounds titles and keeps captures in a full list', () => {
  const tabs = Array.from({ length: 180 }, (_, id) => ({ id, title: 'a\n'.repeat(200), url: `https://site${id}.example/private?token=secret` }));
  tabs.push({ id: 200, incognito: true, url: 'https://private.example' }, { id: 201, url: 'chrome://settings' });
  const records = tabRecords(tabs, new Map([[179, { capturing: true }]]));
  assert.equal(records.length, 128);
  assert.equal(records[0].id, 179);
  assert(records.every(tab => tab.title.length <= 160 && !tab.title.includes('\n') && !tab.site.includes('secret')));
  assert(!records.some(tab => tab.id >= 200));
});

test('a tab record carries the stage of an activation OpenXLR asked for', () => {
  const tabs = [{ id: 1, url: 'https://one.example' }, { id: 2, url: 'https://two.example' }];
  const records = tabRecords(tabs, new Map(), new Map([[2, { stage: 'popup' }]]));
  assert.deepEqual(records.map(tab => [tab.id, tab.activation]), [[1, null], [2, 'popup']]);
  assert.deepEqual(Object.keys(records[0]), ['id', 'site', 'title', 'capturing', 'error', 'activation']);
});

test('only the refusal for a tab the user did not invoke the extension on counts as one', () => {
  assert(notInvoked(new Error('Extension has not been invoked for the current page (see activeTab permission). Chrome pages cannot be captured.')));
  assert(notInvoked('Extension has not been invoked for the current page (see activeTab permission).'));
  for (const other of [new Error('Permission denied'), 'Cannot capture a tab with an active stream.', undefined, null])
    assert.equal(notInvoked(other), false);
});

test('output matching refuses guesses and never chooses default-device aliases', () => {
  const channel = { id: 'music', label: 'OpenXLR Music' };
  const output = { kind: 'audiooutput', label: channel.label, deviceId: 'real' };
  assert.equal(deviceFor(channel, [{ ...output, deviceId: 'default' }, output]), 'real');
  assert.throws(() => deviceFor(channel, [{ ...output, deviceId: 'default' }]), /unavailable/);
  assert.throws(() => deviceFor(channel, [output, { ...output, deviceId: 'other' }]), /Several/);
  assert.equal(deviceFor({ ...channel, label: 'Renamed' }, [output], { music: 'real' }), 'real');
  assert.throws(() => deviceFor(channel, [], { music: 'disappeared' }), /unavailable/);
});

test('two tabs have independent native output contexts and stop independently', async () => {
  const f = fixture();
  await f.audio.start(1, 'teams-stream', 'chat'); await f.audio.start(2, 'youtube-stream', 'music');
  assert.deepEqual(f.contexts.map(context => context.sink), ['chat', 'music']);
  assert.deepEqual(f.acquisitions.map(item => item.audio.mandatory.chromeMediaSourceId), ['teams-stream', 'youtube-stream']);
  assert(f.acquisitions.every(item => item.video === false && item.audio.mandatory.chromeMediaSource === 'tab'));
  await f.audio.stop(1);
  assert.equal(f.contexts[0].closed, true); assert.equal(f.contexts[1].closed, false);
  assert.equal(f.streams[0].track.stopped, true); assert.equal(f.streams[1].track.stopped, false);
  assert.deepEqual(f.audio.snapshot().map(item => item.id), [2]);
  await f.audio.stopAll(); assert(f.contexts.every(context => context.closed));
});

test('refused output permission never takes over tab audio', async () => {
  const f = fixture({ sink: async () => { throw new Error('NotAllowedError'); } });
  await assert.rejects(f.audio.start(1, 'stream', 'chat'), /NotAllowedError/);
  assert.equal(f.acquisitions.length, 0); assert.equal(f.audio.snapshot().length, 0);
  assert(f.contexts.every(context => context.closed));
});

test('failed playback resume releases the capture instead of leaving a silent tab', async () => {
  const f = fixture({ resume: async () => { throw new Error('resume refused'); } });
  await assert.rejects(f.audio.start(1, 'stream', 'chat'), /resume refused/);
  assert(f.streams.every(stream => stream.track.stopped)); assert(f.contexts.every(context => context.closed));
  assert.deepEqual(f.statuses.at(-1), [1, false, 'resume refused']);
});

test('stop during acquisition releases late tracks and never resumes a closed context', async () => {
  const late = deferred(); let make;
  const f = fixture({ acquire: factory => { make = factory; return late.promise; } });
  const start = f.audio.start(1, 'stream', 'chat'); await f.acquired.promise;
  await f.audio.stop(1); late.resolve(make()); await start;
  assert(f.streams.every(stream => stream.track.stopped)); assert.equal(f.audio.snapshot().length, 0);
  assert(!f.statuses.some(item => item[1]));
});

test('a timed-out acquisition also releases its late result', async () => {
  const late = deferred(); let make;
  const f = fixture({ timeoutMs: 10, acquire: factory => { make = factory; return late.promise; } });
  await assert.rejects(f.audio.start(1, 'stream', 'music'), /in time/);
  const stream = make(); late.resolve(stream);
  let timer;
  try { await Promise.race([stream.stopped, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Late tracks were not released.')), 1000); })]); }
  finally { clearTimeout(timer); }
  assert(stream.track.stopped); assert(f.contexts.every(context => context.closed));
});

test('overlapping starts cannot leave an obsolete capture playing', async () => {
  const late = deferred(); let make; let count = 0;
  const f = fixture({ acquire: factory => { if (++count === 1) { make = factory; return late.promise; } return factory(); } });
  const first = f.audio.start(1, 'first', 'chat'); await f.acquired.promise;
  await f.audio.start(1, 'second', 'music'); late.resolve(make()); await first;
  assert.equal(f.audio.snapshot().length, 1); assert.equal(f.audio.snapshot()[0].deviceId, 'music');
  assert(f.contexts[0].closed); assert.equal(f.contexts[1].closed, false);
  assert.equal(f.streams.at(-1).track.stopped, true);
  await f.audio.stopAll();
});

test('a failed live reroute releases only its own tab', async () => {
  const f = fixture({ sink: async id => { if (id === 'missing') throw new Error('output lost'); } });
  await f.audio.start(1, 'one', 'chat'); await f.audio.start(2, 'two', 'music');
  await assert.rejects(f.audio.route(1, 'missing'), /output lost/);
  assert.deepEqual(f.audio.snapshot().map(item => item.id), [2]);
  await f.audio.stopAll();
});

test('device loss and browser-ended capture report truth and release their resources', async () => {
  const f = fixture();
  await f.audio.start(1, 'one', 'chat'); await f.audio.start(2, 'two', 'music');
  f.mediaDevices.enumerateDevices = async () => [{ kind: 'audiooutput', deviceId: 'music' }];
  await f.audio.checkDevices(); assert.deepEqual(f.audio.snapshot().map(item => item.id), [2]);
  f.streams[1].end(); assert.equal(f.audio.snapshot().length, 0);
  assert(f.streams.every(stream => stream.track.stopped));
});

test('capture ending during playback resume is never reported as active', async () => {
  const ready = deferred(), resume = deferred();
  const f = fixture({ resume: () => { ready.resolve(); return resume.promise; } });
  const starting = f.audio.start(1, 'one', 'chat');
  await ready.promise;
  f.streams[0].end();
  resume.resolve();
  await starting;
  assert.equal(f.audio.snapshot().length, 0);
  assert(!f.statuses.some(([, capturing]) => capturing));
  assert(f.streams[0].track.stopped);
});

test('a delayed device snapshot cannot stop a new or rerouted session', async () => {
  const devices = deferred();
  const f = fixture();
  await f.audio.start(1, 'one', 'chat');
  f.mediaDevices.enumerateDevices = () => devices.promise;
  const checking = f.audio.checkDevices();
  await f.audio.route(1, 'new-device');
  await f.audio.start(2, 'two', 'new-device');
  devices.resolve([{ kind: 'audiooutput', deviceId: 'chat' }]);
  await checking;
  assert.deepEqual(f.audio.snapshot().map(tab => tab.id), [1, 2]);
  await f.audio.stopAll();
});

test('capture count is bounded without touching the rejected tab', async () => {
  const f = fixture();
  for (let id = 0; id < MAX_CAPTURES; id++) await f.audio.start(id, String(id), 'chat');
  await assert.rejects(f.audio.start(999, 'overflow', 'chat'), /At most/);
  assert.equal(f.acquisitions.length, MAX_CAPTURES); await f.audio.stopAll();
});

test('diagnostic text is bounded and cannot inject multiline status', () => {
  assert.equal(errorText(new Error('first\nsecond')), 'first second');
  assert.equal(errorText('a'.repeat(900)).length, 200);
  assert.equal(errorText('a'.repeat(199) + '😀'), 'a'.repeat(199));
  assert.equal(errorText('a\u0085b'), 'a b');
  assert.equal(tabRecords([{ id: 1, url: 'https://example.com', title: 'a'.repeat(159) + '😀' }])[0].title, 'a'.repeat(159));
  assert.equal(tabRecords([{ id: 1, url: 'https://example.com', title: 'bad\ud800text\udc00' }])[0].title, 'bad\ufffdtext\ufffd');
  assert.equal(siteOf('https://' + 'a'.repeat(510) + '.example'), null);
  assert.equal(tabRecords([{ id: 2147483648, url: 'https://example.com' }]).length, 0);
});

test('native allowlist is fixed to the extension key and has no website access', async () => {
  const manifest = JSON.parse(await readFile(new URL('../chromium/manifest.json', import.meta.url), 'utf8'));
  const host = JSON.parse(await readFile(new URL('../native-host.json', import.meta.url), 'utf8'));
  const id = [...createHash('sha256').update(Buffer.from(manifest.key, 'base64')).digest().subarray(0, 16)]
    .flatMap(byte => [byte >> 4, byte & 15]).map(nibble => String.fromCharCode(97 + nibble)).join('');
  assert.deepEqual(host.allowed_origins, [`chrome-extension://${id}/`]);
  assert.equal(host.name, 'com.emaspa.openxlr.browser'); assert.equal(host.path, '/usr/bin/openxlr');
  assert.equal(manifest.incognito, 'not_allowed');
  assert.equal(manifest.host_permissions, undefined); assert.equal(manifest.content_scripts, undefined);
  assert.equal(manifest.externally_connectable, undefined);
  assert.deepEqual(manifest.permissions, ['activeTab', 'tabs', 'tabCapture', 'offscreen', 'storage', 'nativeMessaging']);
});

test('a failed old device query cannot stop a replacement or newly started capture', async () => {
  const devices = deferred();
  const f = fixture();
  await f.audio.start(1, 'old', 'chat');
  f.mediaDevices.enumerateDevices = () => devices.promise;
  const checking = f.audio.checkDevices().catch(error => f.audio.stopAll(errorText(error)));
  await f.audio.start(1, 'replacement', 'music');
  await f.audio.start(2, 'new', 'chat');
  devices.reject(new Error('enumeration failed'));
  await checking;
  assert.deepEqual(f.audio.snapshot().map(tab => tab.id), [1, 2]);
  assert(f.streams[0].track.stopped);
  assert(!f.streams[1].track.stopped && !f.streams[2].track.stopped);
  await f.audio.stopAll();
});

test('a failed device query releases only unchanged captures and reports its bounded error', async () => {
  const devices = deferred();
  const f = fixture();
  await f.audio.start(1, 'one', 'chat');
  await f.audio.start(2, 'two', 'chat');
  f.mediaDevices.enumerateDevices = () => devices.promise;
  const checking = f.audio.checkDevices().catch(error => f.audio.stopAll(errorText(error)));
  await f.audio.route(1, 'music');
  devices.reject(new Error('devices\nunavailable'));
  await checking;
  assert.deepEqual(f.audio.snapshot().map(tab => tab.id), [1]);
  assert.equal(f.audio.snapshot()[0].deviceId, 'music');
  assert(f.streams[1].track.stopped && f.contexts[1].closed);
  assert.deepEqual(f.statuses.at(-1), [2, false, 'devices unavailable']);
  await f.audio.stopAll();
});

for (const failed of [false, true]) {
  test(`an old ${failed ? 'failed' : 'missing-device'} query cannot undo an output round trip`, async () => {
    const devices = deferred();
    const f = fixture();
    await f.audio.start(1, 'one', 'chat');
    f.mediaDevices.enumerateDevices = () => devices.promise;
    const checking = f.audio.checkDevices();
    await f.audio.route(1, 'music');
    await f.audio.route(1, 'chat');
    if (failed) devices.reject(new Error('old failure')); else devices.resolve([]);
    await checking;
    assert.deepEqual(f.audio.snapshot().map(tab => [tab.id, tab.deviceId]), [[1, 'chat']]);
    await f.audio.stopAll();
  });

  test(`an older ${failed ? 'failed' : 'missing-device'} query cannot override a newer successful device snapshot`, async () => {
    const devices = deferred();
    const f = fixture();
    await f.audio.start(1, 'one', 'chat');
    const enumerate = f.mediaDevices.enumerateDevices;
    f.mediaDevices.enumerateDevices = () => devices.promise;
    const checking = f.audio.checkDevices();
    f.mediaDevices.enumerateDevices = enumerate;
    await f.audio.checkDevices();
    if (failed) devices.reject(new Error('old failure')); else devices.resolve([]);
    await checking;
    assert.deepEqual(f.audio.snapshot().map(tab => tab.id), [1]);
    await f.audio.stopAll();
  });
}

test('a superseded query cannot stop another tab after awaiting a context close', async () => {
  const closing = deferred(), release = deferred();
  let first = true;
  const f = fixture({ close: async () => {
    if (first) { first = false; closing.resolve(); await release.promise; }
  } });
  await f.audio.start(1, 'one', 'chat');
  await f.audio.start(2, 'two', 'music');
  const enumerate = f.mediaDevices.enumerateDevices;
  f.mediaDevices.enumerateDevices = async () => [];
  const checking = f.audio.checkDevices();
  await closing.promise;
  f.mediaDevices.enumerateDevices = enumerate;
  await f.audio.checkDevices();
  release.resolve();
  await checking;
  assert.deepEqual(f.audio.snapshot().map(tab => tab.id), [2]);
  await f.audio.stopAll();
});
