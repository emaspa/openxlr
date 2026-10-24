import { t } from './text.mjs';

export const HOST = 'com.emaspa.openxlr.browser';
export const MAX_TABS = 128;
export const MAX_CAPTURES = 8;

function boundedText(value, limit) {
  let text = String(value).toWellFormed().replace(/[\u0000-\u001f\u007f-\u009f]/g, ' ').slice(0, limit);
  if (/[\ud800-\udbff]$/.test(text)) text = text.slice(0, -1);
  return text;
}

export function siteOf(url) {
  try {
    const site = new URL(url);
    return (site.protocol === 'https:' || site.protocol === 'http:') && site.origin.length <= 512 ? site.origin : null;
  } catch { return null; }
}

/**
 * True for Chrome's refusal to capture a tab the user has not invoked the
 * extension on: a toolbar click, the extension's shortcut or a menu item.
 */
export function notInvoked(error) {
  return /has not been invoked|activeTab permission/i.test(String(error?.message ?? error ?? ''));
}

export function tabRecords(tabs, captures = new Map(), requests = new Map()) {
  return tabs.filter(tab => Number.isSafeInteger(tab.id) && tab.id >= 0 && tab.id <= 2147483647 && !tab.incognito && siteOf(tab.url))
    .sort((a, b) => Number(captures.get(b.id)?.capturing ?? false) - Number(captures.get(a.id)?.capturing ?? false)
      || Number(!!b.active) - Number(!!a.active) || Number(!!b.audible) - Number(!!a.audible) || a.id - b.id)
    .slice(0, MAX_TABS)
    .map(tab => ({
      id: tab.id, site: siteOf(tab.url),
      title: boundedText(tab.title || siteOf(tab.url), 160),
      capturing: captures.get(tab.id)?.capturing === true,
      error: captures.get(tab.id)?.error?.slice(0, 200) || null,
      activation: requests.get(tab.id)?.stage ?? null,
    }));
}

export function deviceFor(channel, devices, bindings = {}) {
  const outputs = devices.filter(device => device.kind === 'audiooutput'
    && device.deviceId && device.deviceId !== 'default' && device.deviceId !== 'communications');
  const bound = outputs.find(device => device.deviceId === bindings[channel.id]);
  if (bound) return bound.deviceId;
  const matches = outputs.filter(device => device.label === channel.label);
  if (matches.length !== 1)
    throw new Error(matches.length ? t('outputAmbiguous')
      : t('outputUnavailable'));
  return matches[0].deviceId;
}

export function errorText(error) {
  return boundedText(error?.message || error || t('routingFailed'), 200);
}

/** Native Web Audio does the playback. No audio samples enter JavaScript. */
export class TabAudio {
  constructor({ mediaDevices, createContext, status, timeoutMs = 10000 }) {
    this.mediaDevices = mediaDevices;
    this.createContext = createContext;
    this.status = status;
    this.timeoutMs = timeoutMs;
    this.sessions = new Map();
    this.deviceCheck = null;
  }

  async start(id, streamId, deviceId) {
    const previous = this.sessions.get(id);
    if (!previous && this.sessions.size >= MAX_CAPTURES) throw new Error(t('tooManyCaptures', MAX_CAPTURES));
    const session = { id, context: null, stream: null, source: null, closed: false, capturing: false, deviceId, routeVersion: 0 };
    this.sessions.set(id, session);
    let timer;
    try {
      if (previous) await this.close(previous);
      if (session.closed || this.sessions.get(id) !== session) return;
      session.context = this.createContext();
      if (typeof session.context.setSinkId !== 'function') throw new Error(t('noSinkSelection'));
      await session.context.setSinkId(deviceId);
      if (session.closed) return;
      const acquisition = this.mediaDevices.getUserMedia({
        audio: { mandatory: { chromeMediaSource: 'tab', chromeMediaSourceId: streamId } }, video: false,
      });
      // getUserMedia cannot be cancelled. A late result must release its tracks.
      acquisition.then(stream => { if (session.closed) stream.getTracks().forEach(track => track.stop()); }, () => {});
      session.stream = await Promise.race([acquisition, new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error(t('startTimeout'))), this.timeoutMs);
      })]);
      if (session.closed) { session.stream.getTracks().forEach(track => track.stop()); return; }
      if (!session.stream.getAudioTracks().length) throw new Error(t('noAudioTrack'));
      for (const track of session.stream.getTracks()) track.addEventListener('ended', () => {
        if (this.sessions.get(id) === session) void this.stop(id, t('routingEnded'));
      }, { once: true });
      if (session.stream.getAudioTracks().some(track => track.readyState === 'ended'))
        throw new Error(t('routingEnded'));
      session.source = session.context.createMediaStreamSource(session.stream);
      session.source.connect(session.context.destination);
      await session.context.resume();
      if (session.closed) return;
      session.capturing = true;
      this.status(id, true, null);
    } catch (error) {
      if (this.sessions.get(id) !== session) return;
      await this.stop(id, errorText(error));
      throw error;
    } finally { clearTimeout(timer); }
  }

  async route(id, deviceId) {
    const session = this.sessions.get(id);
    if (!session || session.closed || session.deviceId === deviceId) return;
    try {
      await session.context.setSinkId(deviceId);
      if (this.sessions.get(id) === session) { session.deviceId = deviceId; session.routeVersion++; }
    } catch (error) {
      if (this.sessions.get(id) === session) await this.stop(id, errorText(error));
      throw error;
    }
  }

  async stop(id, error = null) {
    const session = this.sessions.get(id);
    if (!session) return;
    this.sessions.delete(id);
    this.status(id, false, error);
    await this.close(session);
  }

  async close(session) {
    if (session.closed) return;
    session.closed = true;
    session.capturing = false;
    try { session.source?.disconnect(); } catch { /* A partially connected node is already released. */ }
    session.stream?.getTracks().forEach(track => track.stop());
    try { await session.context?.close(); } catch { /* Tracks have been released even if the context is gone. */ }
  }

  async stopAll(error = null) {
    await Promise.all([...this.sessions.keys()].map(id => this.stop(id, error)));
  }

  snapshot() {
    return [...this.sessions.values()].map(session => ({ id: session.id, capturing: session.capturing, deviceId: session.deviceId }));
  }

  async checkDevices() {
    const check = this.deviceCheck = Symbol();
    const checking = [...this.sessions].map(([id, session]) => ({ id, session, deviceId: session.deviceId, version: session.routeVersion }));
    let devices = [], error = null;
    try { devices = await this.mediaDevices.enumerateDevices(); }
    catch (failure) { error = errorText(failure); }
    if (this.deviceCheck !== check) return;
    const present = new Set(devices.filter(device => device.kind === 'audiooutput').map(device => device.deviceId));
    // Another check or reroute can also finish while a context closes below.
    for (const { id, session, deviceId, version } of checking)
      if (this.deviceCheck === check && this.sessions.get(id) === session && session.routeVersion === version && !present.has(deviceId))
        await this.stop(id, error ?? t('outputGone'));
  }
}
