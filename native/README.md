# Optional LV2 editor host

The default .NET build does not invoke make or require a C compiler.
Inserts use PipeWire filter-chain by default, even when the helper is installed.
Build and copy the optional helper with:

```sh
dotnet build src/OpenXLR.slnx -c Release -p:EnableNativeLv2Host=true
```

Native build dependencies are a C11 compiler, make, pkg-config, PipeWire
development headers, lilv development headers and X11 development headers.
The helper can also be built with `make -C native` and installed beside
the daemon separately. Distribution packages are not changed by this PR.

For a supported plugin exposing an X11 editor, enable **Native host** in its
controls window to run that insert in an isolated PipeWire filter process.
The choice is stored as `nativeHost: true` in the insert definition; old
settings and profiles without the field keep filter-chain. An explicitly
selected native host that is unavailable reports an error instead of silently
changing hosts. Disable the choice to return to filter-chain. Switching hosts
rebuilds the chain and can briefly interrupt audio.
Other inserts remain in filter-chain, also
inside mixed chains. The existing generated controls window gains a Plugin UI
button when the daemon advertises an available native editor. On Wayland the
editor requires XWayland. The catalog checks required features on both the DSP
and the selected X11 UI before advertising the editor. The live insert status
also reports whether its native host is running, so a failed or bypassed chain
cannot offer an editor action that will only fail. Unsupported requirements are
never silently accepted; the existing upstream API feature gate remains in place.

Control edits return through the helper pipe and are saved by the normal
daemon settings path. Plugin output-control meters are included in insert status.
Audio buffers never cross managed code or the command pipe.

## Desktop session environment with the packaged user service

The user service can start before the desktop publishes its X11 environment.
The helper inherits the daemon's environment; it does not search other sessions
or guess a display/cookie. From a terminal **inside the active graphical session**,
import its values before restarting the daemon:

```sh
systemctl --user import-environment DISPLAY XAUTHORITY
systemctl --user restart openxlr-daemon.service
```

The restart briefly interrupts audio. Repeat the import at graphical login (or
use the desktop's session-start integration), especially when XWayland assigns
a new display. Check that DISPLAY is set and that XAUTHORITY, when set, points
to a readable cookie file. GDM/SDDM often use a file under `/run/user/...`, not
`~/.Xauthority`; use the session's value, not a copied or guessed cookie. Never
use `xhost +` to bypass authentication.

The packaged unit also enables `PrivateTmp`. For a filesystem X11 socket that
is hidden by that setting, create a **user override** with
`systemctl --user edit openxlr-daemon.service`:

```ini
[Service]
PrivateTmp=false
```

Then run `systemctl --user daemon-reload` and restart after the import above.
This optional override reduces temporary-directory isolation; the shipped unit
is unchanged. Do not disable other hardening. On Wayland, XWayland must be
running. These steps describe setup, not acceptance on GDM/SDDM hardware.

The helper observes the daemon-owned stdin pipe for EOF/HUP instead of using
PDEATHSIG, whose Linux semantics tie it to the creating thread. A separate
monitor thread reports audio progress; the UI loop reports UI progress.
A stalled editor does not by itself make the DSP process unhealthy.

The host currently implements URID map/unmap for DSP and the X11 editor
features used by this implementation. Worker, state/preset, VST3 and CLAP
support are separate work. Changing chains still uses the existing rebuild
mechanism; seamless swapping is not claimed.
