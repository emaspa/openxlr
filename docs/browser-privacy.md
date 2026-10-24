# Browser companion privacy policy

This policy covers the OpenXLR browser routing extension for Chromium
based browsers, id `beeohanoffjpcofbjiiamkdammhcihaf`, published in the
Chrome Web Store and kept in `browser/chromium` of this repository. The
extension lets you choose an OpenXLR audio channel for each browser tab.
It works only together with OpenXLR installed on the same computer.

## Summary

The extension sends nothing to any server. It talks only to the OpenXLR
program on your own computer, through the browser's native messaging. It
makes no network requests, has no analytics, no advertising and no
tracking, and it does not sell or share data with anyone.

## What the extension reads

- **Open tabs.** For each tab in a normal window whose address is an HTTP
  or HTTPS page, up to 128 tabs, it reads the browser's tab id, the
  address and the page title. The address is reduced to its origin, the
  scheme, host and port, so the path, query, fragment and any credentials
  in it are dropped. The title is cut to 160 characters. Whether a tab is
  active or playing sound is used only to order the list.
- **Tab audio, for tabs you activate.** When you press Activate this tab
  in the extension's popup, or use its keyboard shortcut, the browser
  hands the extension that tab's audio. The extension plays it through
  the browser's own audio engine into the OpenXLR output you chose, on
  the same computer. The audio samples are not read by the extension's
  code, recorded, stored or sent anywhere. Audio of tabs you have not
  activated is never captured.
- **Audio output devices.** The extension lists the browser's audio
  outputs (their kind, name and browser device id) to find the output
  that matches the OpenXLR channel you chose.
- **Microphone permission, once.** The browser shows the names of audio
  outputs other than the default only after it has been given microphone
  permission. The Output access page asks for that permission, opens the
  microphone and stops it immediately. No microphone audio is recorded,
  stored, sent to OpenXLR or routed.

## Where it goes

The extension sends messages only to the native messaging host
`com.emaspa.openxlr.browser`, which the browser starts from the OpenXLR
installation on your computer. Those messages are:

- the tab list: tab id, origin, title, whether the extension is playing
  the tab's audio, a short error text if routing that tab failed, and
  where an activation OpenXLR asked for stands;
- a channel choice: tab id, origin, the OpenXLR channel, and whether to
  remember it for that website;
- the setting that lets the browser choose its outputs.

OpenXLR answers with its list of channels and the tabs' assignments, and
passes on a request when you press Activate for a tab in OpenXLR. The
extension then brings that tab forward and may open its popup there; the
browser still captures the tab only after you invoked the extension on it. The
extension sends no tab audio over this connection. What OpenXLR keeps of
the tab list, including the website defaults it saves when you check
Remember for this website, is described in
[Browser tab routing](browser-routing.md#privacy-and-removal). OpenXLR
does not send it to any server either.

## What the extension stores

The extension keeps one entry in the browser's local extension storage:

| Key | Content |
| --- | --- |
| `deviceBindings` | For each OpenXLR channel the extension has routed to, the browser device id of the matching audio output. Entries for channels that no longer exist are removed. |

It stores no tab addresses, titles, history or audio.

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
