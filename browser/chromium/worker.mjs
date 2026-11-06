import { HOST, MAX_TABS, deviceFor, errorText, notInvoked, siteOf, tabRecords } from './routing.mjs';
import { t } from './text.mjs';

const ASSIGNMENT_PENDING = t('assignmentPending');
const ASSIGNMENT_TIMEOUT = t('assignmentTimeout');
// How long an activation OpenXLR asked for waits for the user in the browser.
const REQUEST_TIMEOUT = 120000;

let native = null;
let state = null;
let bridgeError = null;
let creating = null;
let refreshing = null;
let refreshAgain = false;
let lastTabs = '';
let reportedTabs = new Map();
let badgeText;
let refreshTimer;
let bindingOperation = Promise.resolve();
let recovering = true;
let stopping = null;
const captures = new Map();
const sites = new Map();
const operations = new Map();
const starts = new Map();
const stops = new Map();
const assignments = new Map();
const requests = new Map();

function updateBadge() {
  const count = [...captures.values()].filter(capture => capture.capturing).length;
  const text = count ? String(count) : '';
  if (text === badgeText) return;
  badgeText = text;
  void chrome.action.setBadgeText({ text }).catch(() => { if (badgeText === text) badgeText = undefined; });
}

function activeRoutes() {
  return [...captures].flatMap(([id, capture]) => {
    const tab = reportedTabs.get(id);
    if (!capture.capturing || !tab) return [];
    const selected = state?.tabs.find(candidate => candidate.id === id && candidate.site === tab.site);
    const channel = state?.channels.find(candidate => candidate.id === (capture.channelId ?? selected?.channelId));
    return [{ id, site: tab.site, title: tab.title, channel: channel?.name ?? channel?.label ?? null }];
  });
}

function clearAssignment(id) {
  const pending = assignments.get(id);
  if (pending) clearTimeout(pending.timer);
  assignments.delete(id);
}

function clearAssignments() {
  for (const id of assignments.keys()) clearAssignment(id);
}

/** Forget an activation OpenXLR asked for. True when there was one. */
function clearRequest(id) {
  const request = requests.get(id);
  if (!request) return false;
  clearTimeout(request.timer);
  requests.delete(id);
  return true;
}

function clearRequests() {
  for (const id of [...requests.keys()]) clearRequest(id);
}

function connect() {
  if (recovering || native) return;
  try {
    const port = chrome.runtime.connectNative(HOST);
    native = port;
    bridgeError = null;
    lastTabs = '';
    port.onMessage.addListener(message => {
      if (native !== port) return;
      if (message?.type === 'activate') { void activateRequest(message).catch(error => { bridgeError = errorText(error); }); return; }
      if (message?.type === 'error') { clearAssignments(); bridgeError = errorText(message.error); return; }
      if (message?.type !== 'state' || !Array.isArray(message.channels) || !Array.isArray(message.tabs)) return;
      state = message;
      for (const [id, pending] of assignments) {
        const tab = state.tabs.find(candidate => candidate.id === id);
        if (tab?.site === pending.site && tab.channelId === pending.channel && (tab.remember === true) === pending.remember)
          clearAssignment(id);
      }
      bridgeError = null;
      void reconcile().catch(error => { bridgeError = errorText(error); });
    });
    port.onDisconnect.addListener(() => {
      if (native !== port) return;
      // Read lastError while its callback is active, but expose no native paths.
      const disconnected = chrome.runtime.lastError;
      bridgeError = disconnected ? t('bridgeUnavailable') : t('bridgeDisconnected');
      native = null;
      state = null;
      clearAssignments();
      clearRequests();
      void stopAll(bridgeError);
    });
    void refreshTabs().catch(error => { if (native === port) bridgeError = errorText(error); });
  } catch {
    native = null;
    clearAssignments();
    bridgeError = t('bridgeUnavailable');
  }
}

function post(message) {
  if (!native) throw new Error(bridgeError || t('statusDisconnected'));
  native.postMessage(message);
}

async function hasOffscreen() {
  const contexts = await chrome.runtime.getContexts({ contextTypes: ['OFFSCREEN_DOCUMENT'],
    documentUrls: [chrome.runtime.getURL('offscreen.html')] });
  return contexts.length !== 0;
}

async function ensureOffscreen() {
  creating ??= (async () => {
    if (await hasOffscreen()) return;
    await chrome.offscreen.createDocument({ url: 'offscreen.html', reasons: ['USER_MEDIA'],
      justification: 'Route user-activated tab audio to its selected OpenXLR output without recording it.' });
  })();
  const task = creating;
  try { await task; } finally { if (creating === task) creating = null; }
}

async function audio(message, create = false, validate = () => true) {
  if (create) await ensureOffscreen();
  else if (!await hasOffscreen()) return null;
  if (validate() !== true) return null;
  const reply = await chrome.runtime.sendMessage({ ...message, target: 'offscreen' });
  if (reply?.error) throw new Error(reply.error);
  return reply?.value;
}

function serial(id, operation) {
  const previous = operations.get(id) ?? Promise.resolve();
  const next = previous.catch(() => {}).then(operation);
  operations.set(id, next);
  void next.finally(() => { if (operations.get(id) === next) operations.delete(id); }).catch(() => {});
  return next;
}

function cancelStart(id) {
  const pending = starts.get(id);
  if (pending) pending.cancelled = true;
  starts.delete(id);
}

function stopTab(id, error = null) {
  cancelStart(id);
  clearRequest(id);
  if (captures.has(id)) captures.set(id, { capturing: false, error });
  updateBadge();
  const task = (stops.get(id) ?? Promise.resolve()).catch(() => {}).then(() => audio({ type: 'stop', id, error }));
  stops.set(id, task);
  void task.finally(() => { if (stops.get(id) === task) stops.delete(id); }).catch(() => {});
  scheduleRefresh();
  return task;
}

function stopAll(error = null) {
  clearRequests();
  for (const pending of starts.values()) pending.cancelled = true;
  starts.clear();
  const task = (stopping ?? Promise.resolve()).then(async () => {
    try {
      // Include the initial document check so a late creation cannot outlive shutdown.
      await creating;
      await audio({ type: 'stopAll', error });
    }
    catch (failure) { bridgeError = errorText(failure); }
    finally {
      for (const [id] of captures) captures.set(id, { capturing: false, error });
      updateBadge();
      try { if (await hasOffscreen()) await chrome.offscreen.closeDocument(); }
      catch (failure) { bridgeError = errorText(failure); }
      scheduleRefresh();
    }
  });
  stopping = task;
  void task.finally(() => { if (stopping === task) stopping = null; }).catch(() => {});
  return task;
}

function scheduleRefresh() {
  clearTimeout(refreshTimer);
  refreshTimer = setTimeout(() => { void refreshTabs().catch(error => { bridgeError = errorText(error); }); }, 100);
}

async function refreshTabs() {
  if (refreshing) { refreshAgain = true; return refreshing; }
  refreshing = (async () => {
    do {
      refreshAgain = false;
      const tabs = await chrome.tabs.query({});
      const liveSites = new Map(tabs.map(tab => [tab.id, siteOf(tab.url)]));
      for (const id of captures.keys()) if (!liveSites.has(id)) captures.delete(id);
      for (const [id, pending] of assignments)
        if (liveSites.get(id) !== pending.site) clearAssignment(id);
      for (const [id, request] of requests)
        if (liveSites.get(id) !== request.site) clearRequest(id);
      for (const [id, previous] of sites) {
        if (liveSites.get(id) !== previous) {
          await stopTab(id);
          captures.delete(id);
          sites.delete(id);
        }
      }
      const records = tabRecords(tabs, captures, requests);
      reportedTabs = new Map(records.map(tab => [tab.id, tab]));
      for (const tab of records) sites.set(tab.id, tab.site);
      updateBadge();
      const encoded = JSON.stringify(records);
      if (native && encoded !== lastTabs) { post({ type: 'tabs', tabs: records }); lastTabs = encoded; }
    } while (refreshAgain);
  })();
  try { await refreshing; } finally { refreshing = null; }
}

async function outputFor(channel) {
  const devices = await audio({ type: 'devices' }, true);
  const task = bindingOperation.catch(() => {}).then(async () => {
    const { deviceBindings: saved } = await chrome.storage.local.get('deviceBindings');
    const deviceBindings = saved && typeof saved === 'object' && !Array.isArray(saved) ? saved : {};
    if (!state?.channels.some(item => item.id === channel.id)) throw new Error(t('changedWhileStarting'));
    const id = deviceFor(channel, devices ?? [], deviceBindings);
    const alive = new Set(state?.channels.map(item => item.id) ?? []);
    const next = Object.fromEntries(Object.entries(deviceBindings).filter(([key, value]) => alive.has(key) && typeof value === 'string'));
    next[channel.id] = id;
    if (JSON.stringify(next) !== JSON.stringify(saved)) await chrome.storage.local.set({ deviceBindings: next });
    return id;
  });
  bindingOperation = task;
  return task;
}

async function reconcile() {
  if (!state?.preserveBrowserOutputs) { clearRequests(); await stopAll(); return; }
  for (const [id, request] of requests) {
    const tab = state.tabs.find(candidate => candidate.id === id);
    if (tab?.site !== request.site || !tab.channelId) { clearRequest(id); scheduleRefresh(); }
  }
  for (const [id, pending] of starts) {
    const tab = state.tabs.find(candidate => candidate.id === id);
    if (tab?.site !== pending.site || tab?.channelId !== pending.channel) {
      await stopTab(id);
    }
  }
  for (const [id, current] of captures) {
    if (!current.capturing || assignments.has(id)) continue;
    await serial(id, async () => {
      if (captures.get(id) !== current || !current.capturing || !state?.preserveBrowserOutputs || assignments.has(id)) return;
      const tab = state?.tabs.find(candidate => candidate.id === id);
      const channel = state?.channels.find(candidate => candidate.id === tab?.channelId);
      if (!channel) { await stopTab(id); return; }
      if (current.channelId === channel.id) return;
      const unchanged = () => captures.get(id) === current && state?.preserveBrowserOutputs === true
        && !assignments.has(id) && state.tabs.find(candidate => candidate.id === id)?.channelId === channel.id;
      try {
        const deviceId = await outputFor(channel);
        await audio({ type: 'route', id, deviceId }, false, unchanged);
        if (unchanged()) current.channelId = channel.id;
      }
      catch (error) { if (unchanged()) await stopTab(id, errorText(error)); }
    });
  }
}

/** Start routing a tab that is active in its window. Chrome decides whether it may be captured. */
async function startCapture(id, site, active = null) {
  if (assignments.has(id)) throw new Error(ASSIGNMENT_PENDING);
  if (!state?.preserveBrowserOutputs) throw new Error(t('enableOutputsFirst'));
  const selected = state.tabs.find(tab => tab.id === id);
  if (!selected || !site) throw new Error(t('activateFromTab'));
  if (selected.site !== site) throw new Error(t('siteChanged'));
  const channel = state.channels.find(channel => channel.id === selected.channelId);
  if (!channel) throw new Error(t('chooseChannelFirst'));
  if (starts.has(id)) throw new Error(t('alreadyStarting'));
  if (captures.get(id)?.capturing) return null;
  // The popup's tab lookup is part of the activation and must be cancellable.
  const pending = { site, channel: channel.id, cancelled: false };
  starts.set(id, pending);
  const check = () => {
    const latest = state?.tabs.find(candidate => candidate.id === id);
    if (assignments.has(id)) throw new Error(ASSIGNMENT_PENDING);
    if (pending.cancelled || !state?.preserveBrowserOutputs || latest?.site !== site || latest?.channelId !== pending.channel)
      throw new Error(t('changedWhileStarting'));
    return true;
  };
  try {
    active ??= (await chrome.tabs.query({ active: true, currentWindow: true }))[0];
    check();
    if (!active || active.id !== id || active.incognito || siteOf(active.url) !== site) throw new Error(t('activateFromTab'));
    await stopping;
    await stops.get(id);
    check();
    const deviceId = await outputFor(channel);
    check();
    // The browser grants this only for a user-activated tab. Never reuse ids.
    const streamId = await chrome.tabCapture.getMediaStreamId({ targetTabId: id });
    check();
    await audio({ type: 'start', id, streamId, deviceId }, true, check);
    if (!pending.cancelled && captures.get(id)?.capturing) captures.get(id).channelId = channel.id;
    return null;
  } finally { if (starts.get(id) === pending) starts.delete(id); }
}

async function openPopup(windowId) {
  // Chrome 127 opens an extension's popup on request; older versions cannot.
  if (typeof chrome.action?.openPopup !== 'function') return false;
  try { await chrome.action.openPopup({ windowId }); return true; } catch { return false; }
}

/**
 * OpenXLR's Activate for one tab: bring it forward and start it as the
 * popup's Activate would. Chrome captures only a tab the user invoked the
 * extension on, so without that grant the popup opens on the tab, or the
 * row in OpenXLR says what to do in the browser. The tab list reports the
 * stage as activation: starting, popup or browser.
 */
async function activateRequest(message) {
  const id = message.tabId;
  const listed = state?.tabs.find(tab => tab.id === id);
  // Only a tab this browser registered, on the origin OpenXLR showed.
  if (!Number.isSafeInteger(id) || !listed || listed.site !== message.site || !state.preserveBrowserOutputs || !listed.channelId) return;
  if (requests.has(id) || starts.has(id) || assignments.has(id) || captures.get(id)?.capturing) return;
  // Register before the first asynchronous tab lookup so a Stop can cancel
  // even a request that has not yet focused its tab.
  const request = { site: message.site, stage: 'starting', timer: null };
  requests.set(id, request);
  request.timer = setTimeout(() => { if (requests.get(id) === request && clearRequest(id)) scheduleRefresh(); }, REQUEST_TIMEOUT);
  // This attempt replaces an earlier failure in OpenXLR's row.
  if (captures.get(id)?.error) captures.set(id, { ...captures.get(id), error: null });
  scheduleRefresh();
  const current = () => requests.get(id) === request;
  let tab;
  try { tab = await chrome.tabs.get(id); } catch { /* The tab may have closed. */ }
  if (!current()) return;
  if (!tab || tab.incognito || siteOf(tab.url) !== request.site) {
    clearRequest(id);
    scheduleRefresh();
    return;
  }
  try {
    await chrome.windows.update(tab.windowId, { focused: true });
    if (!current()) return;
    await chrome.tabs.update(id, { active: true });
    const shown = await chrome.tabs.get(id);
    if (!current()) return;
    if (!shown?.active || shown.incognito || siteOf(shown.url) !== request.site) throw new Error(t('changedWhileStarting'));
    // The popup may have picked up this request while Chrome was focusing the tab.
    if (starts.has(id)) return;
    await startCapture(id, request.site, shown);
  } catch (error) {
    if (!current()) return;
    if (notInvoked(error)) {
      const opened = await openPopup(tab.windowId);
      if (current()) request.stage = opened ? 'popup' : 'browser';
    } else {
      clearRequest(id);
      if (!captures.get(id)?.capturing) captures.set(id, { capturing: false, error: errorText(error) });
    }
    scheduleRefresh();
  }
}

async function handle(message) {
  switch (message.type) {
    case 'snapshot': return { state, error: bridgeError, startingTabs: [...starts.keys()], pendingTabs: [...assignments.keys()],
      requestedTabs: [...requests.keys()], activeRoutes: activeRoutes() };
    case 'retry': connect(); await refreshTabs(); return { state, error: bridgeError };
    case 'enable': post({ type: 'enable', value: message.value === true }); return null;
    case 'assign': {
      const tab = state?.tabs.find(candidate => candidate.id === message.id);
      if (!Number.isSafeInteger(message.id) || !tab) throw new Error(t('tabUnregistered'));
      if (message.site !== tab.site) throw new Error(t('siteChanged'));
      if (message.channel !== null && !state.channels.some(channel => channel.id === message.channel)) throw new Error(t('channelUnavailable'));
      if (!assignments.has(message.id) && assignments.size >= MAX_TABS)
        throw new Error(t('tooManyAssignments'));
      clearAssignment(message.id);
      const pending = { site: message.site, channel: message.channel, remember: message.channel !== null && message.remember === true };
      assignments.set(message.id, pending);
      bridgeError = null;
      pending.timer = setTimeout(() => {
        if (assignments.get(message.id) !== pending) return;
        clearAssignment(message.id);
        bridgeError = ASSIGNMENT_TIMEOUT;
      }, 5000);
      if (starts.has(message.id)) void stopTab(message.id).catch(error => { bridgeError = errorText(error); });
      try { post({ type: 'assign', tabId: message.id, site: message.site, channel: message.channel, remember: message.remember === true }); }
      catch (error) { if (assignments.get(message.id) === pending) clearAssignment(message.id); throw error; }
      return null;
    }
    case 'start': {
      const id = message.id;
      const request = requests.get(id);
      try { return await startCapture(id, message.site); }
      catch (error) {
        // An activation OpenXLR asked for: tell its row what happened. A
        // popup still showing an earlier website leaves the request alone.
        if (request && request.site === message.site && requests.get(id) === request && !starts.has(id)) {
          if (notInvoked(error)) request.stage = 'browser';
          else {
            clearRequest(id);
            if (!captures.get(id)?.capturing) captures.set(id, { capturing: false, error: errorText(error) });
          }
          scheduleRefresh();
        }
        throw error;
      }
    }
    case 'stop':
      if (Number.isSafeInteger(message.id)) await stopTab(message.id);
      return null;
    case 'stopAll': await stopAll(); return null;
    case 'showTab': {
      const route = reportedTabs.get(message.id);
      if (!route || route.site !== message.site || !captures.get(message.id)?.capturing)
        throw new Error(t('tabUnregistered'));
      const tab = await chrome.tabs.get(route.id);
      if (tab.incognito || siteOf(tab.url) !== route.site || !captures.get(route.id)?.capturing)
        throw new Error(t('siteChanged'));
      await chrome.tabs.update(tab.id, { active: true });
      await chrome.windows.update(tab.windowId, { focused: true });
      return null;
    }
    case 'audioStatus':
      if (!Number.isSafeInteger(message.id)) return null;
      captures.set(message.id, { capturing: message.capturing === true, error: message.error ? errorText(message.error) : null });
      if (message.capturing === true) clearRequest(message.id);
      updateBadge();
      scheduleRefresh();
      return null;
    case 'devicesChanged': await reconcile(); return null;
    default: throw new Error('Unknown browser routing action.');
  }
}

chrome.runtime.onMessage.addListener((message, sender, reply) => {
  if (sender.id !== chrome.runtime.id || message?.target !== 'worker') return;
  if (message.type === 'audioStatus' && sender.url !== chrome.runtime.getURL('offscreen.html')) return;
  void handle(message).then(value => reply({ value }), error => reply({ error: errorText(error) }));
  return true;
});

chrome.tabs.onCreated.addListener(scheduleRefresh);
chrome.tabs.onActivated.addListener(scheduleRefresh);
chrome.tabs.onUpdated.addListener((id, changes) => {
  const previous = assignments.get(id)?.site ?? starts.get(id)?.site ?? requests.get(id)?.site ?? sites.get(id);
  if (changes.url && previous && siteOf(changes.url) !== previous) {
    clearAssignment(id);
    clearRequest(id);
    void stopTab(id).catch(error => { bridgeError = errorText(error); });
  }
  if ('url' in changes || 'title' in changes || 'audible' in changes) scheduleRefresh();
});
function removeTab(id) {
  clearAssignment(id);
  clearRequest(id);
  if (captures.get(id)?.capturing || starts.has(id))
    void stopTab(id).catch(error => { bridgeError = errorText(error); });
  scheduleRefresh();
}
chrome.tabs.onRemoved.addListener(removeTab);
chrome.tabs.onReplaced.addListener((_, removed) => removeTab(removed));
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(details => {
  connect();
  // Output access is the one step Chrome asks for once; offer it straight after installing.
  if (details?.reason === 'install') void chrome.tabs.create({ url: chrome.runtime.getURL('devices.html') }).catch(() => {});
});

// A service worker can sleep while its offscreen audio continues. Recover the
// active sessions before publishing a replacement provider to the daemon.
void (async () => {
  try {
    const existing = await audio({ type: 'snapshot' });
    for (const tab of existing ?? []) captures.set(tab.id, { capturing: tab.capturing, error: null });
  } catch (error) {
    await stopAll(errorText(error));
  } finally {
    recovering = false;
    updateBadge();
    connect();
  }
})().catch(error => { bridgeError = errorText(error); });
