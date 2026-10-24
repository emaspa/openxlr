import { notInvoked, siteOf } from './routing.mjs';
import { localized, t, translatePage } from './text.mjs';

translatePage(document);
const $ = id => document.getElementById(id);
let tab;
let shown = '';
let disposed = false;
let operationError = '';
// Chrome shows output devices only after a microphone grant; the devices page asks for it.
let access = 'granted';
const watchAccess = async () => {
  try {
    const status = await navigator.permissions.query({ name: 'microphone' });
    access = status.state;
    status.addEventListener('change', () => { access = status.state; });
  } catch { access = 'granted'; }
};
const send = async message => {
  const result = await chrome.runtime.sendMessage({ ...message, target: 'worker' });
  if (result?.error) throw new Error(result.error);
  return result?.value;
};
// Chrome lets the extension capture a tab only after the user invoked it on
// that tab. A popup Chrome opened on OpenXLR's request may not count, so say
// how to invoke it, with the shortcut the user actually has.
const invocationHint = async () => {
  try {
    const shortcut = (await chrome.commands.getAll()).find(command => command.name === '_execute_action')?.shortcut;
    if (shortcut) return t('popupInvokeShortcut', shortcut);
  } catch { /* The plain hint below still applies. */ }
  return t('popupInvokeNoShortcut');
};
const start = async () => {
  try { await send({ type: 'start', id: tab.id }); }
  catch (error) { throw notInvoked(error.message) ? new Error(await invocationHint()) : error; }
};
const act = operation => {
  operationError = '';
  void operation().catch(error => {
    operationError = error.message;
    $('error').textContent = localized(operationError);
  });
};

async function refresh() {
  const snapshot = await send({ type: 'snapshot' });
  const state = snapshot?.state;
  const selected = state?.tabs.find(item => item.id === tab?.id);
  $('error').textContent = localized(operationError || snapshot?.error || selected?.error || '');
  $('status').textContent = !state ? t('statusDisconnected')
    : !selected ? t('statusUnavailable')
    : selected.capturing ? t('statusRouted') : t('statusChoose');
  $('enable').hidden = !state || state.preserveBrowserOutputs;
  const starting = snapshot?.startingTabs?.includes(tab?.id);
  const assigning = snapshot?.pendingTabs?.includes(tab?.id);
  $('request').hidden = !snapshot?.requestedTabs?.includes(tab?.id) || !!selected?.capturing;
  $('access').hidden = access === 'granted';
  $('start').disabled = access !== 'granted' || !state?.preserveBrowserOutputs || !selected?.channelId || selected.capturing || starting || assigning;
  $('stop').disabled = !selected?.capturing && !starting;
  $('channel').disabled = !selected;
  $('remember').disabled = !selected;
  $('forget').disabled = !selected;
  const encoded = JSON.stringify([state?.channels, selected?.channelId, selected?.remember]);
  if (encoded !== shown) {
    shown = encoded;
    const choices = [new Option(t('channelDefault'), ''), ...(state?.channels ?? []).map(channel => new Option(channel.name, channel.id))];
    $('channel').replaceChildren(...choices);
    $('channel').value = selected?.channelId ?? '';
    $('remember').checked = selected?.remember !== false;
  }
}

const assign = () => send({ type: 'assign', id: tab.id, site: siteOf(tab.url), channel: $('channel').value || null, remember: $('remember').checked });
$('channel').addEventListener('change', () => act(assign));
$('remember').addEventListener('change', () => act(assign));
$('enable').addEventListener('click', () => act(() => send({ type: 'enable', value: true })));
$('start').addEventListener('click', () => act(start));
$('stop').addEventListener('click', () => act(() => send({ type: 'stop', id: tab.id })));
$('retry').addEventListener('click', () => act(() => send({ type: 'retry' })));
$('forget').addEventListener('click', () => act(() => send({ type: 'assign', id: tab.id, site: siteOf(tab.url), channel: null, remember: true })));
const openDevices = () => { void chrome.tabs.create({ url: chrome.runtime.getURL('devices.html') }); };
$('devices').addEventListener('click', openDevices);
$('grant').addEventListener('click', openDevices);

window.addEventListener('pagehide', () => { disposed = true; });
void (async () => {
  await watchAccess();
  [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  $('title').textContent = tab?.title || t('currentTab');
  while (!disposed) {
    try { await refresh(); } catch (error) { $('error').textContent = localized(error.message); }
    await new Promise(resolve => setTimeout(resolve, 350));
  }
})();
