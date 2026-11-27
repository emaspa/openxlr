# Browser companion privacy policy

This policy covers the OpenXLR browser routing extension for Chromium
based browsers, id `beeohanoffjpcofbjiiamkdammhcihaf`, kept in
`browser/chromium` of this repository. It is not yet in the Chrome Web
Store; each release carries it as `openxlr-browser-<version>.tar.gz` to
load unpacked. The
extension lets you choose an OpenXLR audio channel for each browser tab.
It works only together with OpenXLR installed on the same computer.

## Summary

The extension sends nothing to any server. It talks only to the OpenXLR
program on your own computer, through the browser's native messaging. It
makes no network requests, has no analytics, no advertising and no
tracking, and it does not sell or share data with anyone.

## What the extension reads

- **Open tabs.** The browser provides the open tab list. The extension
  uses it to report up to 128 HTTP or HTTPS tabs from normal windows. For
  those tabs, it includes the browser's tab id, the origin and the page
  title cut to 160 characters. It drops paths, query strings, fragments
  and credentials. Whether a tab is active or playing sound is used to
  order the list. The popup uses the same titles for its active routes,
  and the toolbar badge shows only their count.
- **Tab audio, for tabs you activate.** The toolbar button or keyboard
  shortcut opens the popup and gives the extension access to the current
  tab. When you choose Activate this tab, the browser hands the extension
  that tab's audio. The extension plays it through
  the browser's own audio engine into the OpenXLR output you chose, on
  the same computer. The audio samples are not read by the extension's
  code, recorded, stored or sent anywhere. The extension captures a tab's
  audio only after you pressed Activate this tab in the popup on that tab. Opening
  the popup alone does not start it, and a request from OpenXLR starts it
  only under the rule in [Where it goes](#where-it-goes).
- **Audio output devices.** The extension lists the browser's audio
  outputs (their kind, name and browser device id) to find the output
  that matches the OpenXLR channel you chose.
- **Microphone permission, once.** The browser shows the names of audio
  outputs other than the default only after it has been given microphone
  permission. The Output access page opens once after the extension is
  installed, and the popup offers it until access is granted; choose Allow
  device discovery there to request it. The extension opens the microphone and stops
  it immediately. No microphone audio is recorded,
  stored, sent to OpenXLR or routed.

## Where it goes

The extension sends messages only to the native messaging host
`com.emaspa.openxlr.browser`, which the browser starts from the OpenXLR
installation on your computer. Those messages are:

- the tab list: tab id, origin, title, whether the extension is playing
  the tab's audio, that this extension can set a tab's volume and raise it
  above 100%, a short error text if routing that tab failed, and where an
  activation OpenXLR asked for stands;
- a channel choice: tab id, origin, the OpenXLR channel, and whether to
  remember it for that website;
- the setting that lets the browser choose its outputs.

OpenXLR answers with its list of channels, the tabs' assignments and
playback volumes, and an error text when it refuses a message or cannot
reach its daemon. It also passes on a request when you press Activate for
a tab in OpenXLR. Any local program that controls OpenXLR can send that
request too. The extension handles it like this:

- If you pressed Activate this tab in the popup on that tab during this
  browser session, and the tab is still on the same website, the
  extension brings the tab forward and starts capture again.
- For any other tab, it brings the tab forward and opens its popup there,
  or the tab's row in OpenXLR says what to press. Nothing is captured until
  you press Activate this tab.

The first case ends for a tab when you close it, when it moves to another
website, when you press Stop routing for it or Stop all routing in the
popup, and when the browser closes. The extension sends no tab audio over
this connection. What OpenXLR keeps of
the tab list, including the website defaults it saves when you check
Remember for this website, is described in
[Browser tab routing](browser-routing.md#privacy-and-removal). OpenXLR
does not send it to any server either.

## What the extension stores

The extension keeps one entry in the browser's local extension storage
and one in its session storage, which the browser clears when it closes:

| Key | Storage | Content |
| --- | --- | --- |
| `deviceBindings` | local | For each OpenXLR channel the extension has routed to, the browser device id of the matching audio output. Entries for channels that no longer exist are removed. |
| `activatedTabs` | session | For each tab where you pressed Activate this tab, the browser's tab id and the tab's origin. An entry is removed when the tab closes, moves to another website or is stopped in the popup. |

It stores no page paths, titles, history or audio.

## Browser permissions

- `tabs` supplies the titles and origins for the channel picker in OpenXLR.
- `activeTab` and `tabCapture` let the companion route a tab after you
  invoke the action and choose to activate it.
- `offscreen` keeps the browser's audio playback running with the popup
  closed.
- `storage` remembers the output device bindings in this browser profile,
  and the tabs you activated until the browser closes.
- `nativeMessaging` connects the extension to the local OpenXLR host.

The extension uses the data exposed by these APIs for the routing and
controls described here. Its code is bundled with the extension.

## Private browsing

The extension is not allowed in incognito windows, and it leaves private
tabs out of the tab list and refuses to route them.

## Removal

Forget website default in the popup removes that website's saved channel
from OpenXLR. Remove the extension from the browser's extensions page to
uninstall it. The native host manifest belongs to the OpenXLR package and
goes with it.

## Changes and contact

Changes to this policy are made in this file, and its history is the
record of them. Questions go to the
[OpenXLR issue tracker](https://github.com/emaspa/openxlr/issues).
