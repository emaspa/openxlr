# Browser tab routing

The optional Chromium companion gives each HTTP or HTTPS tab a separate
entry in OpenXLR's Browser tabs window. Choose Music for YouTube and Voice
Chat for a Teams tab or PWA, for example. It supports native Linux installs
of Chrome, Chromium, Brave and Edge, starting with Chromium 116. Firefox and
the Flatpak build are not supported by this companion. Snap-confined
Chromium cannot launch a native host outside the snap; use a native browser
package for this companion.

PipeWire often calls every browser stream `Playback`. It can also combine
several tabs into one stream. OpenXLR therefore does not guess a tab from a
stream title or process id. The companion takes over audio from a tab that
you activate and plays it into the selected OpenXLR application channel.
It does not record an audio file. A call still uses the microphone chosen
in the browser or website. Check a real call's echo cancellation and delay
with your headset before relying on this for meetings.

## Install the companion

Install the companion from the
[Chrome Web Store](https://chromewebstore.google.com/detail/beeohanoffjpcofbjiiamkdammhcihaf)
in Chrome, Chromium, Brave or Edge, then pin its action if useful. Its id
is `beeohanoffjpcofbjiiamkdammhcihaf`, the only extension the native host
accepts. What it reads and where that goes is in
the [privacy policy](browser-privacy.md).

The Debian, RPM and Nix packages offer the Store listing to Chrome and
Chromium themselves. They install `beeohanoffjpcofbjiiamkdammhcihaf.json`
in `share/chromium/extensions` and `share/google-chrome/extensions` under
their prefix, naming the Chrome Web Store as the place to fetch it from.
On its next start the browser discovers those files and requests the
companion from the Store. Check `chrome://extensions` to confirm that it is
installed and enabled. Do not rely on an approval prompt: Chrome documents
[automatic external installation on Linux](https://developer.chrome.com/docs/extensions/how-to/distribute/install-extensions#linux).
On NixOS, the browser does not read those package directories.
Set `services.openxlr.chromiumExtension = true` together with
`programs.chromium.enable = true` to install it through the browser's
forced install policy, which the user cannot remove, or install it from
the Store page. The module refuses `chromiumExtension` without
`programs.chromium.enable`, and keeps your other
`programs.chromium.extensions` entries.

If you removed the companion, an upgrade does not
offer it again; install it from the Store page. The policy install shows
under `ExtensionInstallForcelist` in `chrome://policy`. Brave and Edge
need the native host copied by hand, as described below.

### Native messaging host

The packages also install the native messaging host the companion talks
to. Debian and RPM packages install `com.emaspa.openxlr.browser.json` in
`/etc/chromium/native-messaging-hosts` and
`/etc/opt/chrome/native-messaging-hosts`, pointing at `/usr/bin/openxlr`.
The Nix package generates both manifests with its store path, and the
OpenXLR NixOS module exposes them in those system directories. Restart
the browser after installing or updating the package.

For other browsers or source installs, copy `browser/native-host.json`
into the directory below as `com.emaspa.openxlr.browser.json`. OpenXLR
does not write a per-user manifest at runtime. A per-user manifest takes
precedence over the system one, so remove a stale copy or update its path.

| Browser | Directory under `~/.config` |
| --- | --- |
| Chrome | `google-chrome/NativeMessagingHosts` |
| Chrome Beta | `google-chrome-beta/NativeMessagingHosts` |
| Chrome Unstable | `google-chrome-unstable/NativeMessagingHosts` |
| Chromium | `chromium/NativeMessagingHosts` |
| Brave | `BraveSoftware/Brave-Browser/NativeMessagingHosts` |
| Edge | `microsoft-edge/NativeMessagingHosts` |

For example, for Chromium:

```sh
mkdir -p ~/.config/chromium/NativeMessagingHosts
cp browser/native-host.json ~/.config/chromium/NativeMessagingHosts/com.emaspa.openxlr.browser.json
```

Custom browser profiles and managed installations can use another
directory. The manifest's `path` must be the absolute path to your
installed `openxlr` executable. Its default is `/usr/bin/openxlr`.
Keep `allowed_origins` restricted to the extension id above. Restart the
browser after installing the host, or press Reconnect in the popup.

With Nix outside the NixOS module, copy the generated manifest from
`<package>/etc/chromium/native-messaging-hosts/` into the per-user directory.
Its path names that package's `bin/openxlr`; update the copy when changing
package versions.

The AUR `PKGBUILD` is maintained outside this repository. Its `package()`
function can install the same files for the `/usr/bin/openxlr` wrapper:

```sh
install -Dm644 browser/native-host.json \
    "$pkgdir/etc/chromium/native-messaging-hosts/com.emaspa.openxlr.browser.json"
install -Dm644 browser/native-host.json \
    "$pkgdir/etc/opt/chrome/native-messaging-hosts/com.emaspa.openxlr.browser.json"
install -Dm644 browser/external-extension.json \
    "$pkgdir/usr/share/chromium/extensions/beeohanoffjpcofbjiiamkdammhcihaf.json"
install -Dm644 browser/external-extension.json \
    "$pkgdir/usr/share/google-chrome/extensions/beeohanoffjpcofbjiiamkdammhcihaf.json"
```

### Load it unpacked

Use the companion of the same version as your OpenXLR installation.
Remove the Store copy first: both use the same id. Each release carries
it as `openxlr-browser-<version>.tar.gz`. Extract it to a folder you keep,
since the browser loads the extension from there:

```sh
rm -rf ~/.local/share/openxlr/browser
mkdir -p ~/.local/share/openxlr/browser
tar -xzf openxlr-browser-<version>.tar.gz --strip-components=1 \
    -C ~/.local/share/openxlr/browser
```

1. Open `chrome://extensions`, `brave://extensions` or `edge://extensions`.
   Enable Developer mode, choose Load unpacked and select
   `~/.local/share/openxlr/browser`. From a checkout, select
   `browser/chromium` instead.
2. Check that its id is `beeohanoffjpcofbjiiamkdammhcihaf`. The public key in
   the manifest keeps this id stable across copies. No private signing
   key is shipped.

After an OpenXLR upgrade, run the same commands with the new release's
archive and press Reload on the extension's card.

`python3 -I tools/browser-extension.py <directory> --unpacked` writes that
archive. It is a tar.gz, not a zip, because OpenDeck offers every zip on
the latest release as a Stream Deck plugin. Without `--unpacked` the tool
writes `openxlr-browser-<version>.zip`, the package uploaded to the Store.
That zip leaves out the manifest's public key, which the Store refuses, so
an unpacked copy of it gets another id and the native host refuses it.

### Languages

The companion follows the browser's language. It ships in English, German,
Spanish, Italian, Filipino (Chrome's name for Tagalog) and Serbian (Cyrillic).
Chrome has no Cebuano, so a browser set to it shows English.

## Choose and activate a tab

1. In OpenXLR's APPLICATIONS card, press Browser tabs beside Manage. The
   button shows once the daemon supports browser routing. The window it
   opens holds the option and one row per tab, and says so when the
   companion has reported no tab yet. Enable individual browser tab
   routing at the top of the window. This
   suspends generic application routing for recognized Chromium browsers:
   OpenXLR stops moving their audio. Existing browser-wide assignments
   stay saved and resume when you turn the option off. Browser audio that
   has not been taken over plays where the browser or desktop puts it.
   That is often the Linux default, but not always: a browser already
   playing into an OpenXLR channel stays there, and the desktop can send
   a new browser stream back to the channel the browser last used. The
   browser keeps its assigned channel in the application list. The
   picker's tooltip says the assignment is on hold and names the channel
   the browser plays into now, refreshed on each stream sweep, including
   moves made in pavucontrol. Choosing a channel for the whole browser,
   from that list, an OpenDeck key or a desktop key, saves it for later
   and moves nothing while the option is on.
2. Allow output access once per browser profile. The companion opens the
   Output access page by itself right after it is installed, and until
   access is granted its popup says so, offers the page, and keeps Activate
   unavailable. Chromium requires microphone permission to reveal
   non-default output devices. Press the setup button to grant it; the
   companion immediately stops the temporary microphone stream and neither
   records nor forwards it.
3. Pick a channel in the tab's row in the Browser tabs window or in the
   popup. Check Remember website to save that origin's default. A
   tab-only choice leaves the
   site's existing default for other tabs intact. Forget website removes
   the saved default and returns that tab to Browser default.
4. Press Activate in the tab's row in the Browser tabs window, or return
   to the audio tab, open the companion and press Activate this tab. A PWA
   without a toolbar can use the extension action shortcut, initially
   Ctrl+Shift+9. Change it in your browser's extension shortcuts page if
   another application owns it. Choosing a channel alone never starts
   capture.

   The browser captures a tab only after you invoked the companion on it,
   with its toolbar button or its shortcut, and forgets that when the tab
   navigates. Activate in OpenXLR is not such a gesture, so it brings the
   tab's window and the tab forward and tries. If the browser still holds
   the grant from an earlier click, routing starts and the row's light
   turns on. Otherwise the companion opens its popup on that tab, which
   Chrome 127 and later allow, and the popup says OpenXLR asked to route
   the tab; press Activate this tab there. If the browser refuses that
   press too, or cannot open the popup, the popup or the row says to click
   the OpenXLR icon on the tab or press the shortcut, then Activate this
   tab. The row reads Starting in the browser while the companion works,
   then names the step left for you, and clears when routing starts, when
   the companion reports an error, or after two minutes.
   An older companion ignores the request; the row then returns to its
   usual text after ten seconds.
5. While active, channel changes in either place reroute that tab. Other
   active tabs keep their own channel. Stop, closing the tab, navigating
   to another origin, disabling browser output selection or losing the
   daemon connection releases the takeover. Normal browser playback can
   then resume. Reconnection restores the list, not capture permission.

Control requests already waiting when the daemon connection is lost are
discarded, rather than replayed after reconnection. An old error cannot
replace the new connection's status. The latest tab metadata is kept and
registered again so the list remains current; retry a refused channel
choice after the connection returns.

A same-origin page change keeps the tab's selection. A different origin
uses its own saved default. A late assignment from the previous website is
refused instead of changing the new website's routing. After restarting the
browser, website defaults remain, but each tab needs activation again.
Deleting a channel moves its remembered website defaults to the fallback
application channel, just like application assignments. Tab-only choices
for the deleted channel return to Browser default.
OpenXLR supports up to eight
active tab takeovers and 128 listed tabs per browser connection, with up
to eight connections and 128 saved website defaults. Private browsing,
browser settings pages and non-HTTP(S) URLs are excluded. A tab whose
host name holds a character such as `*`, `!` or `&` is left out of the
list too, because OpenXLR cannot store its address in the form the browser
uses. The other tabs stay listed and routable.
The daemon also limits each command to 64 KiB. Long tab metadata can reach
that limit before 128 tabs; the popup reports it instead of sending a
partial list. Close some tabs and reconnect to publish a smaller list.

If outputs cannot be enumerated, a channel is missing or an output label
is ambiguous, activation is refused without taking over audio. Renaming
an already matched channel preserves its output-device binding. A removed
output, a failed reroute or a failed output lookup stops only the tabs it
affects. A damaged saved output choice is reset the next time that channel
picks an output. The companion does not keep retrying a failed capture or
native host in the background; correct the problem and activate or
reconnect explicitly.

## Privacy and removal

The companion sends the current tab list to the local native host: tab id,
bounded title, HTTP(S) origin and routing status. The URL field is reduced
to its origin, excluding its path, query and credentials. Page titles come
from the website and are not redacted on their way to the daemon; a title
can itself include a name or link. Shared daemon state includes a title
only while the tab is capturing or has an assigned channel. Other titles
are empty, and the UI labels those tabs by origin. Origins remain available
so every eligible tab can be selected and assignments can reject stale
navigation. All authenticated clients, including HTTP clients, receive
this reduced list. Title-only changes are coalesced to the latest title on
a one-second timer; identical lists do not trigger state broadcasts.
Navigation, tab additions and removals, capture and error changes are
published immediately. The daemon keeps live tab ids and titles
in memory and saves only remembered origin-to-channel defaults in machine
settings, outside profiles. A manually collected diagnostic archive
includes the current tab list without its titles. It keeps the origin of
each tab routed to a channel and masks the origin of tabs left at the
browser default. Its copy of `mixer.json` includes the remembered website
defaults. Review it before sharing.
No tab audio is sent through the control API or stored by the companion.

The host accepts only tab registration, channel assignment and the browser
output preference. Towards the browser it passes the daemon's state and
OpenXLR's requests to activate a tab, each for a tab id and origin the
companion itself registered. It authenticates using the existing local daemon client
and does not expose the API token or unrelated daemon state to the browser.
Web pages cannot call this host through the extension.

To remove the companion, stop its active tabs, remove it in the browser's
extension page and delete only the native host manifest you installed.
Turn off browser output selection in OpenXLR to resume generic browser
routing. Remembered website defaults can be forgotten from their tab
entries before removing the companion.
