import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import test from 'node:test';
import { HOST, MAX_TABS, deviceFor, errorText, notInvoked, siteOf, tabRecords } from '../chromium/routing.mjs';
import { t } from '../chromium/text.mjs';

const workerSource = (await readFile(new URL('../chromium/worker.mjs', import.meta.url), 'utf8'))
  .replace(/^import[^\n]+\n/gm, '');
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
};
const tick = () => new Promise(resolve => setImmediate(resolve));
const settle = async () => { for (let i = 0; i < 20; i++) await tick(); };
// What Chrome says when the user has not invoked the extension on the tab.
const NOT_INVOKED = 'Extension has not been invoked for the current page (see activeTab permission). Chrome pages cannot be captured.';
const event = () => ({ listeners: [], addListener(listener) { this.listeners.push(listener); },
  emit(...args) { return this.listeners.map(listener => listener(...args)); } });

function fixture({ existing = [], recovery } = {}) {
  const connected = deferred();
  const f = {
    tabs: [1, 2].map(id => ({ id, windowId: id * 10, active: id === 1, url: `https://site${id}.example/watch`, title: `Tab ${id}` })),
    active: new Map(existing.map(session => [session.id, session])), document: existing.length > 0,
    calls: [], tokens: [], ports: [], posts: [], timers: new Map(), timerDelays: new Map(), bindings: {}, startGates: [],
    focused: [], popups: [],
    state: { type: 'state', preserveBrowserOutputs: true,
      channels: [{ id: 'music', label: 'OpenXLR Music' }, { id: 'chat', label: 'OpenXLR Chat' }],
      tabs: [1, 2].map(id => ({ id, site: `https://site${id}.example`, channelId: id === 1 ? 'chat' : 'music', remember: false })) },
  };
  const listeners = event();
  const extensionId = 'openxlr-test';
  const url = path => `chrome-extension://${extensionId}/${path}`;
  f.request = (type, data = {}, sender = { id: extensionId, url: url('popup.html') }) => new Promise(resolve => {
    listeners.emit({ target: 'worker', type, ...data }, sender, resolve);
  });
  f.status = (id, capturing, error = null) => f.request('audioStatus', { id, capturing, error }, { id: extensionId, url: url('offscreen.html') });
  const chrome = {
    runtime: { id: extensionId, getURL: url, onMessage: listeners, onStartup: event(), onInstalled: event(),
      connectNative(host) {
        assert.equal(host, HOST);
        const port = { onMessage: event(), onDisconnect: event(), postMessage(message) { f.posts.push(message); } };
        f.ports.push(port); connected.resolve(); return port;
      },
      async sendMessage(message) {
        assert.equal(message.target, 'offscreen');
        f.calls.push(message);
        switch (message.type) {
          case 'snapshot': if (recovery) await recovery.promise; return { value: [...f.active.values()] };
          case 'devices':
            if (f.devicesGate) await f.devicesGate.promise;
            return { value: [{ kind: 'audiooutput', label: 'OpenXLR Music', deviceId: 'music-device' },
              { kind: 'audiooutput', label: 'OpenXLR Chat', deviceId: 'chat-device' }] };
          case 'start': {
            f.active.set(message.id, { id: message.id, capturing: true, deviceId: message.deviceId });
            await f.status(message.id, true);
            const gate = f.startGates.shift();
            if (gate) await gate.promise;
            return { value: null };
          }
          case 'route':
            if (f.active.has(message.id)) f.active.get(message.id).deviceId = message.deviceId;
            return { value: null };
          case 'stop':
            f.active.delete(message.id); await f.status(message.id, false, message.error);
            if (f.stopGate) await f.stopGate.promise;
            return { value: null };
          case 'stopAll':
            if (f.stopAllError) throw new Error(f.stopAllError);
            for (const id of [...f.active.keys()]) { f.active.delete(id); await f.status(id, false, message.error); }
            if (f.stopAllGate) await f.stopAllGate.promise;
            return { value: null };
          default: throw new Error(`Unexpected offscreen message ${message.type}`);
        }
      } },
    tabs: { onCreated: event(), onUpdated: event(), onRemoved: event(),
      async create(options) { (f.opened ??= []).push(options.url); },
      async get(id) { const tab = f.tabs.find(item => item.id === id); if (!tab) throw new Error(`No tab with id: ${id}.`); return { ...tab }; },
      async update(id, changes) { if (changes.active) f.activate(id); return { ...f.tabs.find(item => item.id === id) }; },
      async query(query) { return query.active ? f.tabs.filter(tab => tab.active) : [...f.tabs]; } },
    windows: { async update(id, changes) { if (changes.focused) f.focused.push(id); return { id }; } },
    action: { async openPopup(options) { if (f.popupError) throw new Error(f.popupError); f.popups.push(options?.windowId); } },
    offscreen: { async hasDocument() { return f.document; },
      async createDocument() { f.document = true; f.created = (f.created ?? 0) + 1; },
      async closeDocument() { f.document = false; f.active.clear(); f.closed = (f.closed ?? 0) + 1; } },
    storage: { local: { async get() { return { deviceBindings: f.bindings }; }, async set(value) { f.bindings = value.deviceBindings; } } },
    tabCapture: { async getMediaStreamId(options) {
      f.tokens.push(options.targetTabId);
      if (f.permissionError) throw new Error(f.permissionError);
      return `single-use-stream-${f.tokens.length}`;
    } },
  };
  f.chrome = chrome;
  f.ready = connected.promise.then(tick);
  f.publish = async (changes = {}) => {
    f.state = { ...f.state, ...changes }; f.ports.at(-1).onMessage.emit(f.state); await tick();
  };
  f.activate = id => { for (const tab of f.tabs) tab.active = tab.id === id; };
  f.update = (id, nextUrl) => {
    const tab = f.tabs.find(item => item.id === id); tab.url = nextUrl;
    chrome.tabs.onUpdated.emit(id, { url: nextUrl }, tab);
  };
  let timer = 0;
  f.native = async message => { f.ports.at(-1).onMessage.emit(message); await settle(); };
  f.refresh = async () => {
    for (const [id, callback] of [...f.timers]) if (f.timerDelays.get(id) === 100) { f.timers.delete(id); f.timerDelays.delete(id); callback(); }
    await settle();
    return f.posts.filter(message => message.type === 'tabs').at(-1)?.tabs;
  };
  vm.runInNewContext(workerSource, { chrome, HOST, MAX_TABS, deviceFor, errorText, notInvoked, siteOf, tabRecords, t,
    setTimeout(callback, delay) { f.timers.set(++timer, callback); f.timerDelays.set(timer, delay); return timer; },
    clearTimeout(id) { f.timers.delete(id); f.timerDelays.delete(id); } }, { filename: 'worker.mjs' });
  return f;
}

async function readyFixture(options) {
  const f = fixture(options); await f.ready; await f.publish(); return f;
}

test('independent tabs share one offscreen document and keep separate output selections', { timeout: 2000 }, async () => {
  const f = await readyFixture();
  assert.equal((await f.request('start', { id: 1 })).error, undefined);
  f.activate(2); assert.equal((await f.request('start', { id: 2 })).error, undefined);
  assert.equal(f.created, 1);
  assert.equal(f.active.get(1).deviceId, 'chat-device');
  assert.equal(f.active.get(2).deviceId, 'music-device');
  await f.request('stop', { id: 1 }); assert.deepEqual([...f.active.keys()], [2]);
});

test('refused browser capture permission leaves playback alone and explicit retry gets a new token', { timeout: 2000 }, async () => {
  const f = await readyFixture(); f.permissionError = 'Permission denied';
  assert.match((await f.request('start', { id: 1 })).error, /Permission denied/);
  assert.equal(f.calls.filter(call => call.type === 'start').length, 0);
  assert.equal((await f.request('snapshot')).value.startingTabs.length, 0);
  f.permissionError = null;
  assert.equal((await f.request('start', { id: 1 })).error, undefined);
  assert.deepEqual(f.tokens, [1, 1]); assert.equal(f.active.size, 1);
});

test('activation waits for authoritative acknowledgement of a new assignment', { timeout: 2000 }, async () => {
  const f = await readyFixture();
  await f.request('assign', { id: 1, site: 'https://site1.example', channel: 'music', remember: true });
  assert.deepEqual(Array.from((await f.request('snapshot')).value.pendingTabs), [1]);
  assert.equal((await f.request('start', { id: 1 })).error, "Waiting for OpenXLR to confirm this tab's channel assignment.");
  assert.equal(f.tokens.length, 0); assert.equal(f.calls.length, 0);
  await f.publish({ tabs: f.state.tabs.map(tab => tab.id === 1 ? { ...tab, channelId: 'music', remember: true } : tab) });
  assert.equal((await f.request('snapshot')).value.pendingTabs.length, 0);
  assert.equal((await f.request('start', { id: 1 })).error, undefined);
  assert.equal(f.active.get(1).deviceId, 'music-device');
});

test('a stale or missing displayed website cannot assign the replacement page', async () => {
  const f = await readyFixture();
  await f.publish({ tabs: f.state.tabs.map(tab => tab.id === 1 ? { ...tab, site: 'https://replacement.example' } : tab) });
  for (const site of ['https://site1.example', undefined]) {
    const reply = await f.request('assign', { id: 1, site, channel: 'music', remember: true });
    assert.match(reply.error, /website changed/i);
  }
  assert.equal(f.posts.filter(message => message.type === 'assign').length, 0);
  assert.equal((await f.request('snapshot')).value.pendingTabs.length, 0);
  assert.equal((await f.request('assign', { id: 1, site: 'https://replacement.example', channel: 'music', remember: true })).error, undefined);
  assert.equal(f.posts.filter(message => message.type === 'assign')[0].site, 'https://replacement.example');
});

test('rapid assignment replacement ignores old acknowledgements and old timeout callbacks', { timeout: 2000 }, async () => {
  const f = await readyFixture();
  await f.request('assign', { id: 1, site: 'https://site1.example', channel: 'music', remember: true });
  const oldTimer = [...f.timerDelays].find(([, delay]) => delay === 5000)?.[0];
  const oldTimeout = f.timers.get(oldTimer);
  await f.request('assign', { id: 1, site: 'https://site1.example', channel: 'chat', remember: false });
  assert.equal(f.timers.has(oldTimer), false); oldTimeout();
  await f.publish({ tabs: f.state.tabs.map(tab => tab.id === 1 ? { ...tab, channelId: 'music', remember: true } : tab) });
  assert.deepEqual(Array.from((await f.request('snapshot')).value.pendingTabs), [1]);
  assert.match((await f.request('start', { id: 1 })).error, /Waiting for OpenXLR/);
  assert.equal(f.tokens.length, 0);
  await f.publish({ tabs: f.state.tabs.map(tab => tab.id === 1 ? { ...tab, channelId: 'chat', remember: false } : tab) });
  assert.equal((await f.request('snapshot')).value.pendingTabs.length, 0);
  assert.equal((await f.request('start', { id: 1 })).error, undefined);
  assert.equal(f.active.get(1).deviceId, 'chat-device');
});

test('remember acknowledgement and forgetting a website use the authoritative remember state', { timeout: 2000 }, async () => {
  const f = await readyFixture();
  await f.request('assign', { id: 1, site: 'https://site1.example', channel: 'chat', remember: true });
  await f.publish(); assert.equal((await f.request('snapshot')).value.pendingTabs.length, 1);
  await f.publish({ tabs: f.state.tabs.map(tab => tab.id === 1 ? { ...tab, remember: true } : tab) });
  assert.equal((await f.request('snapshot')).value.pendingTabs.length, 0);
  await f.request('assign', { id: 1, site: 'https://site1.example', channel: null, remember: true });
  await f.publish({ tabs: f.state.tabs.map(tab => tab.id === 1 ? { ...tab, channelId: null, remember: true } : tab) });
  assert.equal((await f.request('snapshot')).value.pendingTabs.length, 1);
  await f.publish({ tabs: f.state.tabs.map(tab => tab.id === 1 ? { ...tab, remember: false } : tab) });
  assert.equal((await f.request('snapshot')).value.pendingTabs.length, 0);
});

test('live captures ignore intermediate state while a newer assignment is pending', { timeout: 2000 }, async () => {
  const f = await readyFixture(); await f.request('start', { id: 1 });
  await f.request('assign', { id: 1, site: 'https://site1.example', channel: 'music', remember: true });
  await f.request('assign', { id: 1, site: 'https://site1.example', channel: 'chat', remember: true });
  await f.publish({ tabs: f.state.tabs.map(tab => tab.id === 1 ? { ...tab, channelId: 'music', remember: true } : tab) });
  assert.equal(f.calls.filter(call => call.type === 'route').length, 0);
  assert.equal(f.active.get(1).deviceId, 'chat-device');
  await f.publish({ tabs: f.state.tabs.map(tab => tab.id === 1 ? { ...tab, channelId: 'chat', remember: true } : tab) });
  assert.equal((await f.request('snapshot')).value.pendingTabs.length, 0);
  assert.equal(f.calls.filter(call => call.type === 'route').length, 0);
});

test('assignment failure and disconnect clear pending expectations and their timers', { timeout: 2000 }, async () => {
  const f = await readyFixture();
  await f.request('assign', { id: 1, site: 'https://site1.example', channel: 'music', remember: true });
  f.ports[0].onMessage.emit({ type: 'error', error: 'Assignment refused' }); await tick();
  let snapshot = (await f.request('snapshot')).value;
  assert.equal(snapshot.pendingTabs.length, 0); assert.equal(snapshot.error, 'Assignment refused');
  assert.equal([...f.timerDelays.values()].includes(5000), false);
  await f.request('assign', { id: 1, site: 'https://site1.example', channel: 'music', remember: true });
  f.ports[0].onDisconnect.emit(); await tick();
  snapshot = (await f.request('snapshot')).value;
  assert.equal(snapshot.pendingTabs.length, 0); assert.equal(snapshot.state, null);
  assert.equal([...f.timerDelays.values()].includes(5000), false);
});

test('a synchronous native write failure also clears its pending assignment', { timeout: 2000 }, async () => {
  const f = await readyFixture();
  f.ports[0].postMessage = () => { throw new Error('Native port closed'); };
  assert.equal((await f.request('assign', { id: 1, site: 'https://site1.example', channel: 'music', remember: true })).error, 'Native port closed');
  assert.equal((await f.request('snapshot')).value.pendingTabs.length, 0);
  assert.equal([...f.timerDelays.values()].includes(5000), false);
});

test('an unacknowledged assignment expires after five seconds with a fixed error', { timeout: 2000 }, async () => {
  const f = await readyFixture(); await f.request('assign', { id: 1, site: 'https://site1.example', channel: 'music', remember: true });
  const timers = [...f.timerDelays].filter(([, delay]) => delay === 5000);
  assert.equal(timers.length, 1); f.timers.get(timers[0][0])();
  const snapshot = (await f.request('snapshot')).value;
  assert.equal(snapshot.pendingTabs.length, 0);
  assert.equal(snapshot.error, 'OpenXLR did not confirm the tab assignment in time. Choose the channel again.');
});

test('navigation and removal clear only their own pending assignment', { timeout: 2000 }, async () => {
  const f = await readyFixture();
  await f.request('assign', { id: 1, site: 'https://site1.example', channel: 'music', remember: true });
  await f.request('assign', { id: 2, site: 'https://site2.example', channel: 'chat', remember: true });
  f.update(1, 'https://other.example/');
  assert.deepEqual(Array.from((await f.request('snapshot')).value.pendingTabs), [2]);
  f.chrome.tabs.onRemoved.emit(2);
  assert.equal((await f.request('snapshot')).value.pendingTabs.length, 0);
  assert.equal([...f.timerDelays.values()].includes(5000), false);
});

test('assigning during output discovery cancels the older activation before capture', { timeout: 2000 }, async () => {
  const f = await readyFixture(); f.devicesGate = deferred();
  const start = f.request('start', { id: 1 }); await tick();
  await f.request('assign', { id: 1, site: 'https://site1.example', channel: 'music', remember: true }); f.devicesGate.resolve();
  assert.match((await start).error, /changed|Waiting for OpenXLR/);
  assert.equal(f.tokens.length, 0); assert.equal(f.calls.filter(call => call.type === 'start').length, 0);
});

test('pending assignments are capped at the registered tab limit', { timeout: 2000 }, async () => {
  const f = await readyFixture();
  await f.publish({ tabs: Array.from({ length: 129 }, (_, id) => ({ id, site: `https://site${id}.example`, channelId: 'chat', remember: false })) });
  for (let id = 0; id < 128; id++) assert.equal((await f.request('assign', { id, site: `https://site${id}.example`, channel: 'music', remember: true })).error, undefined);
  assert.equal((await f.request('snapshot')).value.pendingTabs.length, 128);
  assert.match((await f.request('assign', { id: 128, site: 'https://site128.example', channel: 'music', remember: true })).error, /Too many/);
  assert.equal((await f.request('assign', { id: 1, site: 'https://site1.example', channel: 'chat', remember: false })).error, undefined);
  assert.equal((await f.request('snapshot')).value.pendingTabs.length, 128);
});

test('a duplicate pending activation does not request a second capture token', { timeout: 2000 }, async () => {
  const f = await readyFixture(); f.devicesGate = deferred();
  const first = f.request('start', { id: 1 }); await tick();
  assert.match((await f.request('start', { id: 1 })).error, /already starting/);
  f.devicesGate.resolve(); assert.equal((await first).error, undefined);
  assert.deepEqual(f.tokens, [1]);
});

test('stop during output discovery cancels acquisition and permits a clean retry', { timeout: 2000 }, async () => {
  const f = await readyFixture(); f.devicesGate = deferred();
  const start = f.request('start', { id: 1 }); await tick();
  await f.request('stop', { id: 1 }); f.devicesGate.resolve();
  assert.match((await start).error, /changed/); assert.equal(f.tokens.length, 0);
  assert.equal((await f.request('start', { id: 1 })).error, undefined);
  assert.equal(f.active.size, 1);
});

test('a cancelled start finishing late cannot stop its replacement capture', { timeout: 2000 }, async () => {
  const f = await readyFixture(); const late = deferred(); f.startGates.push(late);
  const first = f.request('start', { id: 1 }); await tick();
  assert.equal(f.active.size, 1);
  await f.request('stop', { id: 1 });
  assert.equal((await f.request('start', { id: 1 })).error, undefined);
  late.resolve(); await first;
  assert.equal(f.active.size, 1); assert.equal(f.tokens.length, 2);
});

test('replacement activation waits until an earlier stop has finished', { timeout: 2000 }, async () => {
  const f = await readyFixture(); await f.request('start', { id: 1 });
  f.stopGate = deferred(); const stop = f.request('stop', { id: 1 }); await tick();
  const next = f.request('start', { id: 1 }); await tick();
  assert.equal(f.tokens.length, 1);
  f.stopGate.resolve(); await stop; assert.equal((await next).error, undefined);
  assert.equal(f.active.size, 1);
});

test('a delayed reroute cannot change the output of a replacement capture', { timeout: 2000 }, async () => {
  const f = await readyFixture(); await f.request('start', { id: 1 });
  const route = deferred(); let checks = 0;
  f.chrome.offscreen.hasDocument = async () => { if (++checks === 2) await route.promise; return f.document; };
  await f.publish({ tabs: f.state.tabs.map(tab => tab.id === 1 ? { ...tab, channelId: 'music' } : tab) });
  assert.equal(checks, 2);
  await f.request('stop', { id: 1 });
  await f.publish({ tabs: f.state.tabs.map(tab => tab.id === 1 ? { ...tab, channelId: 'chat' } : tab) });
  await f.request('start', { id: 1 }); route.resolve(); await tick();
  assert.equal(f.calls.filter(call => call.type === 'route').length, 0);
  assert.equal(f.active.get(1).deviceId, 'chat-device');
});

test('a delayed reroute is not dispatched after the same capture changes assignment', { timeout: 2000 }, async () => {
  const f = await readyFixture(); await f.request('start', { id: 1 });
  const route = deferred(); let checks = 0;
  f.chrome.offscreen.hasDocument = async () => { if (++checks === 2) await route.promise; return f.document; };
  await f.publish({ tabs: f.state.tabs.map(tab => tab.id === 1 ? { ...tab, channelId: 'music' } : tab) });
  assert.equal(checks, 2);
  await f.publish({ tabs: f.state.tabs.map(tab => tab.id === 1 ? { ...tab, channelId: 'chat' } : tab) });
  route.resolve(); await tick();
  assert.equal(f.calls.filter(call => call.type === 'route').length, 0);
  assert.equal(f.active.get(1).deviceId, 'chat-device');
});

test('reconnection cannot start capture in a document that shutdown will close', { timeout: 2000 }, async () => {
  const f = await readyFixture(); await f.request('start', { id: 1 });
  f.stopAllGate = deferred(); f.ports[0].onDisconnect.emit(); await tick();
  await f.request('retry'); await f.publish(); f.activate(2);
  const next = f.request('start', { id: 2 }); await tick();
  assert.deepEqual(f.tokens, [1]);
  f.stopAllGate.resolve(); assert.equal((await next).error, undefined);
  assert.equal(f.document, true); assert.deepEqual([...f.active.keys()], [2]);
});

test('shutdown closes the document even if its stop message fails', { timeout: 2000 }, async () => {
  const f = await readyFixture(); await f.request('start', { id: 1 });
  f.stopAllError = 'Offscreen response failed'; f.ports[0].onDisconnect.emit(); await tick();
  assert.equal(f.document, false); assert.equal(f.active.size, 0);
});

test('disabling routing waits for an in-flight document creation and closes its late result', { timeout: 2000 }, async () => {
  const f = await readyFixture(); const creation = deferred();
  f.chrome.offscreen.createDocument = async () => {
    f.created = (f.created ?? 0) + 1; await creation.promise; f.document = true;
  };
  const start = f.request('start', { id: 1 }); await tick();
  assert.equal(f.created, 1); assert.equal(f.document, false);
  await f.publish({ preserveBrowserOutputs: false }); creation.resolve();
  assert.match((await start).error, /changed/); await tick();
  assert.equal(f.document, false); assert.equal(f.closed, 1);
  assert.equal(f.tokens.length, 0); assert.equal(f.calls.filter(call => call.type === 'start').length, 0);
});

test('disconnecting during the initial document check also closes the later document', { timeout: 2000 }, async () => {
  const f = await readyFixture(); const check = deferred(); let checks = 0;
  f.chrome.offscreen.hasDocument = async () => { if (++checks === 1) await check.promise; return f.document; };
  const start = f.request('start', { id: 1 }); await tick();
  assert.equal(f.created, undefined);
  f.ports[0].onDisconnect.emit(); await tick(); check.resolve();
  assert.match((await start).error, /changed/); await tick();
  assert.equal(f.document, false); assert.equal(f.created, 1); assert.equal(f.closed, 1);
  assert.equal(f.tokens.length, 0); assert.equal(f.calls.filter(call => call.type === 'start').length, 0);
});

test('replacement activation waits for late creation cleanup before creating its own document', { timeout: 2000 }, async () => {
  const f = await readyFixture(); const creation = deferred();
  f.chrome.offscreen.createDocument = async () => {
    f.created = (f.created ?? 0) + 1;
    if (f.created === 1) await creation.promise;
    f.document = true;
  };
  const first = f.request('start', { id: 1 }); await tick();
  await f.publish({ preserveBrowserOutputs: false });
  await f.publish({ preserveBrowserOutputs: true }); f.activate(2);
  const replacement = f.request('start', { id: 2 }); await tick();
  assert.equal(f.tokens.length, 0); creation.resolve();
  assert.match((await first).error, /changed/); assert.equal((await replacement).error, undefined);
  assert.equal(f.created, 2); assert.equal(f.closed, 1); assert.equal(f.document, true);
  assert.deepEqual([...f.active.keys()], [2]); assert.deepEqual(f.tokens, [2]);
});

test('rejected creation during shutdown is handled and does not block a later activation', { timeout: 2000 }, async () => {
  const f = await readyFixture(); const creation = deferred();
  const create = f.chrome.offscreen.createDocument;
  f.chrome.offscreen.createDocument = async () => { await creation.promise; f.document = true; };
  const first = f.request('start', { id: 1 }); await tick();
  await f.publish({ preserveBrowserOutputs: false }); creation.reject(new Error('Document creation refused'));
  assert.match((await first).error, /Document creation refused/); await tick();
  assert.equal(f.document, false); assert.equal(f.tokens.length, 0);
  f.chrome.offscreen.createDocument = create; await f.publish({ preserveBrowserOutputs: true });
  assert.equal((await f.request('start', { id: 1 })).error, undefined);
  assert.equal(f.document, true); assert.equal(f.active.size, 1);
});

test('cross-origin navigation cancels pending capture before the debounced refresh', { timeout: 2000 }, async () => {
  const f = await readyFixture(); f.devicesGate = deferred();
  const start = f.request('start', { id: 1 }); await tick();
  f.update(1, 'https://other.example/private'); f.devicesGate.resolve();
  assert.match((await start).error, /changed/); assert.equal(f.tokens.length, 0);
});

test('cancellation during the final document check never dispatches a capture', { timeout: 2000 }, async () => {
  const f = await readyFixture(); const dispatch = deferred(); let checks = 0;
  f.chrome.tabCapture.getMediaStreamId = async ({ targetTabId }) => {
    f.tokens.push(targetTabId);
    f.chrome.offscreen.hasDocument = async () => { if (++checks === 1) await dispatch.promise; return f.document; };
    return 'unused-stream';
  };
  const start = f.request('start', { id: 1 }); await tick();
  await f.request('stop', { id: 1 }); dispatch.resolve();
  assert.match((await start).error, /changed/);
  assert.equal(f.calls.filter(call => call.type === 'start').length, 0);
});

test('cross-origin navigation and tab removal stop active audio immediately', { timeout: 2000 }, async () => {
  const f = await readyFixture(); await f.request('start', { id: 1 });
  f.update(1, 'https://other.example/private'); await tick();
  assert.equal(f.active.size, 0);
  f.activate(2); await f.request('start', { id: 2 });
  f.chrome.tabs.onRemoved.emit(2); await tick();
  assert.equal(f.active.size, 0);
});

test('same-origin navigation preserves an active capture', { timeout: 2000 }, async () => {
  const f = await readyFixture(); await f.request('start', { id: 1 });
  f.update(1, 'https://site1.example/next'); await tick();
  assert.equal(f.active.size, 1); assert.equal(f.calls.filter(call => call.type === 'stop').length, 0);
});

test('startup events wait for offscreen recovery before publishing native tab metadata', { timeout: 2000 }, async () => {
  const recovery = deferred();
  const f = fixture({ recovery, existing: [{ id: 1, capturing: true, deviceId: 'chat-device' }] });
  f.chrome.runtime.onStartup.emit(); await tick();
  assert.equal(f.ports.length, 0);
  recovery.resolve(); await f.ready;
  assert.equal(f.ports.length, 1); assert.equal(f.posts[0].tabs.find(tab => tab.id === 1).capturing, true);
});

test('failed offscreen recovery restores normal playback and still connects the provider', { timeout: 2000 }, async () => {
  const recovery = deferred();
  const f = fixture({ recovery, existing: [{ id: 1, capturing: true, deviceId: 'chat-device' }] });
  recovery.reject(new Error('Cannot read offscreen sessions')); await f.ready;
  assert.equal(f.document, false); assert.equal(f.active.size, 0);
  assert.equal(f.posts[0].tabs.find(tab => tab.id === 1).capturing, false);
});

test('only the offscreen document can report capture state', { timeout: 2000 }, async () => {
  const f = await readyFixture();
  const result = f.chrome.runtime.onMessage.emit({ target: 'worker', type: 'audioStatus', id: 1, capturing: true },
    { id: f.chrome.runtime.id, url: f.chrome.runtime.getURL('popup.html') }, () => assert.fail('Untrusted status received a reply'));
  assert.deepEqual(result, [undefined]);
  const timer = [...f.timers.values()].at(-1); if (timer) await timer();
  assert.equal(f.posts.at(-1).tabs.find(tab => tab.id === 1).capturing, false);
});

test('a fresh install opens the output access page and an update does not', { timeout: 2000 }, async () => {
  const f = fixture(); await f.ready;
  f.chrome.runtime.onInstalled.emit({ reason: 'update' }); await tick();
  assert.equal(f.opened, undefined);
  f.chrome.runtime.onInstalled.emit({ reason: 'install' }); await tick();
  assert.deepEqual(f.opened, ['chrome-extension://openxlr-test/devices.html']);
});

test('OpenXLR activation brings the tab forward and starts it while Chrome still holds the grant', { timeout: 2000 }, async () => {
  const f = await readyFixture();
  await f.native({ type: 'activate', tabId: 2, site: 'https://site2.example' });
  assert.deepEqual(f.focused, [20]);
  assert.equal(f.tabs.find(tab => tab.id === 2).active, true);
  assert.deepEqual(f.tokens, [2]); assert.equal(f.active.get(2).deviceId, 'music-device');
  assert.deepEqual(f.popups, []);
  assert.equal((await f.request('snapshot')).value.requestedTabs.length, 0);
  const listed = (await f.refresh()).find(tab => tab.id === 2);
  assert.equal(listed.capturing, true); assert.equal(listed.activation, null);
});

test('without the grant the popup opens on the tab, and a refused press there asks for the toolbar', { timeout: 2000 }, async () => {
  const f = await readyFixture(); f.permissionError = NOT_INVOKED;
  await f.native({ type: 'activate', tabId: 2, site: 'https://site2.example' });
  assert.deepEqual(f.popups, [20]); assert.equal(f.active.size, 0);
  assert.deepEqual(Array.from((await f.request('snapshot')).value.requestedTabs), [2]);
  let listed = (await f.refresh()).find(tab => tab.id === 2);
  assert.equal(listed.activation, 'popup'); assert.equal(listed.capturing, false); assert.equal(listed.error, null);
  // A press in a popup Chrome opened may not count as invoking the extension.
  assert.match((await f.request('start', { id: 2 })).error, /not been invoked/);
  assert.equal((await f.refresh()).find(tab => tab.id === 2).activation, 'browser');
  // After a toolbar click Chrome grants it, and the request ends with the capture.
  f.permissionError = null;
  assert.equal((await f.request('start', { id: 2 })).error, undefined);
  listed = (await f.refresh()).find(tab => tab.id === 2);
  assert.equal(listed.capturing, true); assert.equal(listed.activation, null);
  assert.equal((await f.request('snapshot')).value.requestedTabs.length, 0);
});

test('a browser that cannot open the popup leaves the activation to the user there', { timeout: 2000 }, async () => {
  for (const missing of [true, false]) {
    const f = await readyFixture(); f.permissionError = NOT_INVOKED;
    if (missing) delete f.chrome.action.openPopup; else f.popupError = 'Could not find an active browser window.';
    await f.native({ type: 'activate', tabId: 2, site: 'https://site2.example' });
    assert.equal((await f.refresh()).find(tab => tab.id === 2).activation, 'browser');
    assert.equal(f.active.size, 0);
  }
});

test('an activation request is ignored unless it names a registered tab with a channel on its origin', { timeout: 2000 }, async () => {
  const f = await readyFixture();
  await f.native({ type: 'activate', tabId: 9, site: 'https://site2.example' });
  await f.native({ type: 'activate', tabId: 2, site: 'https://site1.example' });
  await f.native({ type: 'activate', tabId: '2', site: 'https://site2.example' });
  await f.publish({ tabs: f.state.tabs.map(tab => tab.id === 2 ? { ...tab, channelId: null } : tab) });
  await f.native({ type: 'activate', tabId: 2, site: 'https://site2.example' });
  await f.publish({ tabs: f.state.tabs.map(tab => tab.id === 2 ? { ...tab, channelId: 'music' } : tab) });
  f.tabs.find(tab => tab.id === 2).incognito = true;
  await f.native({ type: 'activate', tabId: 2, site: 'https://site2.example' });
  f.tabs.find(tab => tab.id === 2).incognito = false;
  await f.publish({ preserveBrowserOutputs: false });
  await f.native({ type: 'activate', tabId: 2, site: 'https://site2.example' });
  assert.deepEqual(f.focused, []); assert.deepEqual(f.tokens, []); assert.deepEqual(f.popups, []);
  assert.equal((await f.request('snapshot')).value.requestedTabs.length, 0);
});

test('another failure ends the request and reaches OpenXLR as the tab error', { timeout: 2000 }, async () => {
  const f = await readyFixture(); f.permissionError = 'Permission denied';
  await f.native({ type: 'activate', tabId: 2, site: 'https://site2.example' });
  assert.deepEqual(f.popups, []);
  let listed = (await f.refresh()).find(tab => tab.id === 2);
  assert.equal(listed.activation, null); assert.equal(listed.error, 'Permission denied');
  // The next request replaces the old error.
  f.permissionError = NOT_INVOKED;
  await f.native({ type: 'activate', tabId: 2, site: 'https://site2.example' });
  listed = (await f.refresh()).find(tab => tab.id === 2);
  assert.equal(listed.error, null); assert.equal(listed.activation, 'popup');
});

test('a waiting request ends after its timeout, on navigation and when routing is turned off', { timeout: 2000 }, async () => {
  const f = await readyFixture(); f.permissionError = NOT_INVOKED;
  await f.native({ type: 'activate', tabId: 2, site: 'https://site2.example' });
  const timer = [...f.timerDelays].find(([, delay]) => delay === 120000)[0];
  f.timers.get(timer)();
  assert.equal((await f.refresh()).find(tab => tab.id === 2).activation, null);
  await f.native({ type: 'activate', tabId: 2, site: 'https://site2.example' });
  f.update(2, 'https://other.example/'); await settle();
  assert.equal((await f.request('snapshot')).value.requestedTabs.length, 0);
  await f.native({ type: 'activate', tabId: 1, site: 'https://site1.example' });
  assert.deepEqual(Array.from((await f.request('snapshot')).value.requestedTabs), [1]);
  await f.publish({ preserveBrowserOutputs: false }); await settle();
  assert.equal((await f.request('snapshot')).value.requestedTabs.length, 0);
  assert.equal([...f.timerDelays.values()].includes(120000), false);
});

test('a malformed saved output binding is repaired without blocking capture', { timeout: 2000 }, async () => {
  for (const invalid of [null, [], 'corrupt', 42, true]) {
    const f = await readyFixture();
    f.bindings = invalid;
    assert.equal((await f.request('start', { id: 1 })).error, undefined);
    assert.equal(f.active.get(1).deviceId, 'chat-device');
    assert.deepEqual(JSON.parse(JSON.stringify(f.bindings)), { chat: 'chat-device' });
  }
});

test('repair keeps other valid output bindings and drops malformed values', { timeout: 2000 }, async () => {
  const f = await readyFixture();
  await f.publish({ channels: [...f.state.channels, { id: 'other', label: 'OpenXLR Other' }] });
  f.bindings = { music: 'music-device', chat: null, other: { malformed: true }, removed: 'old-device' };
  assert.equal((await f.request('start', { id: 1 })).error, undefined);
  assert.deepEqual(JSON.parse(JSON.stringify(f.bindings)), { music: 'music-device', chat: 'chat-device' });
});

for (const change of ['disconnect', 'channel removal']) {
  test(`an output lookup interrupted by ${change} preserves saved bindings`, { timeout: 2000 }, async () => {
    const f = await readyFixture();
    f.bindings = { music: 'music-device' };
    const reading = deferred(), release = deferred();
    f.chrome.storage.local.get = async () => {
      reading.resolve(); await release.promise;
      return { deviceBindings: f.bindings };
    };
    const starting = f.request('start', { id: 1 });
    await reading.promise;
    if (change === 'disconnect') f.ports.at(-1).onDisconnect.emit();
    else await f.publish({ channels: f.state.channels.filter(channel => channel.id !== 'chat'),
      tabs: f.state.tabs.map(tab => tab.id === 1 ? { ...tab, channelId: null } : tab) });
    release.resolve();
    assert.ok((await starting).error);
    assert.deepEqual(JSON.parse(JSON.stringify(f.bindings)), { music: 'music-device' });
    assert.equal(f.tokens.length, 0);
    assert.equal(f.active.size, 0);
  });
}
