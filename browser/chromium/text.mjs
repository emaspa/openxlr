// The English text, kept equal to _locales/en/messages.json by the tests.
// Chrome reads the translations; this table answers where chrome.i18n is
// missing, which is the offscreen document and the tests.
export const ENGLISH = {
  extensionName: 'OpenXLR browser routing',
  extensionDescription: 'Choose an OpenXLR channel for each browser tab. Requires the OpenXLR native application.',
  actionTitle: 'Route this tab with OpenXLR',
  commandOpen: 'Open the channel picker for this tab',
  popupTitle: 'OpenXLR tab routing',
  popupConnecting: 'Connecting to OpenXLR…',
  popupChannel: 'Channel',
  popupRemember: 'Remember for this website',
  popupEnable: 'Let the browser choose its outputs',
  popupStart: 'Activate this tab',
  popupStop: 'Stop routing',
  popupDevices: 'Output access',
  popupRetry: 'Reconnect',
  popupForget: 'Forget website default',
  popupAccessNeeded: 'Allow output access once for this browser profile. Until then Chrome hides the OpenXLR outputs and no tab can be routed.',
  popupAccessGrant: 'Allow output access',
  popupHelp: 'Choose channels here or in OpenXLR. Activate each tab in the browser first. Stopping restores normal browser playback.',
  popupRequested: 'OpenXLR asked to route this tab. Press Activate this tab to start.',
  popupInvokeShortcut: 'The browser starts tab audio only after you open OpenXLR on this tab yourself. Click the OpenXLR icon in the toolbar or press $1, then press Activate this tab.',
  popupInvokeNoShortcut: "The browser starts tab audio only after you open OpenXLR on this tab yourself. Click the OpenXLR icon in the toolbar, then press Activate this tab. No keyboard shortcut is set; you can choose one on the browser's extension shortcuts page.",
  statusDisconnected: 'OpenXLR is not connected.',
  statusUnavailable: 'This tab is not available for routing.',
  statusRouted: 'Tab audio is routed through OpenXLR.',
  statusChoose: 'Choose a channel, then activate this tab.',
  channelDefault: 'Browser default',
  currentTab: 'Current tab',
  devicesTitle: 'OpenXLR output access',
  devicesHeading: 'Allow output device discovery',
  devicesExplain: 'Chrome requires microphone permission before it reveals nondefault output devices. This page opens the microphone only to obtain that permission and immediately stops it. No microphone audio is recorded, sent to OpenXLR or routed.',
  devicesAllow: 'Allow device discovery',
  devicesNote: 'Keep OpenXLR channel names unique so the extension can find the matching outputs. Permission and output identifiers belong to this browser profile.',
  devicesFound: 'Found $1 OpenXLR outputs. Return to your audio tab and activate routing.',
  devicesNone: 'Access was granted, but no OpenXLR outputs were found. Check that the daemon is running.',
  devicesDenied: 'Output access was not granted. Tab audio has not been changed. You can retry when ready.',
  assignmentPending: "Waiting for OpenXLR to confirm this tab's channel assignment.",
  assignmentTimeout: 'OpenXLR did not confirm the tab assignment in time. Choose the channel again.',
  bridgeUnavailable: 'OpenXLR browser integration is unavailable. Check the native host setup.',
  bridgeDisconnected: 'OpenXLR browser integration disconnected.',
  tabUnregistered: 'This tab is no longer registered with OpenXLR.',
  siteChanged: "This tab's website changed. Reopen the channel picker before assigning it.",
  channelUnavailable: 'This OpenXLR channel is unavailable.',
  tooManyAssignments: 'Too many browser assignments are waiting for OpenXLR. Try again after a pending assignment finishes.',
  activateFromTab: 'Activate routing from the tab you want to listen to.',
  enableOutputsFirst: 'Enable browser output selection in OpenXLR first.',
  chooseChannelFirst: 'Choose an OpenXLR channel and wait for the assignment to appear first.',
  alreadyStarting: 'Routing is already starting for this tab.',
  changedWhileStarting: 'The tab or channel changed while routing was starting. Activate the tab again.',
  outputAmbiguous: 'Several outputs have this name. Give the OpenXLR channel a unique name.',
  outputUnavailable: 'This OpenXLR output is unavailable. Check output access and the channel in OpenXLR.',
  routingFailed: 'Browser audio routing failed.',
  tooManyCaptures: 'At most $1 tabs can be routed at once.',
  noSinkSelection: 'This browser cannot select a Web Audio output.',
  startTimeout: 'The browser did not start tab audio in time. Try activating this tab again.',
  noAudioTrack: 'The tab supplied no audio track.',
  routingEnded: 'The browser ended tab audio routing.',
  outputGone: 'The selected OpenXLR output disappeared. Normal tab playback has resumed.',
};

const fill = (text, values) => text.replace(/\$([1-9])/g, (_, n) => values[n - 1] ?? '');

/** The message in the browser's language, English where Chrome has none. */
export function t(key, ...values) {
  const strings = values.map(String);
  return globalThis.chrome?.i18n?.getMessage?.(key, strings) || fill(ENGLISH[key], strings);
}

const patterns = Object.entries(ENGLISH).map(([key, text]) =>
  [key, new RegExp('^' + text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&').replace(/\\\$[1-9]/g, '(.+?)') + '$')]);

/** An error the offscreen document raised in English, in the browser's language. */
export function localized(text) {
  if (typeof text !== 'string') return text;
  for (const [key, pattern] of patterns) {
    const match = pattern.exec(text);
    if (match) return t(key, ...match.slice(1));
  }
  return text;
}

/** Fills every element marked data-i18n and sets the page language. */
export function translatePage(document) {
  for (const element of document.querySelectorAll('[data-i18n]')) element.textContent = t(element.dataset.i18n);
  const language = globalThis.chrome?.i18n?.getUILanguage?.();
  if (language) document.documentElement.lang = language;
}
