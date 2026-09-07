# Optional LV2 editor host

The default .NET build does not invoke make or require a C compiler.
Plugins continue to use PipeWire filter-chain when the helper is absent.
Build and copy the optional helper with:

```sh
dotnet build src/OpenXLR.slnx -c Release -p:EnableNativeLv2Host=true
```

Native build dependencies are a C11 compiler, make, pkg-config, PipeWire
development headers, lilv development headers and X11 development headers.
The helper can also be built with `make -C native` and installed beside
the daemon separately. Distribution packages are not changed by this PR.

For a supported plugin exposing an X11 editor, the live instance runs in an
isolated PipeWire filter process. Other plugins remain in filter-chain, also
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

The helper observes the daemon-owned stdin pipe for EOF/HUP instead of using
PDEATHSIG, whose Linux semantics tie it to the creating thread. A separate
monitor thread reports audio progress; the UI loop reports UI progress.
A stalled editor does not by itself make the DSP process unhealthy.

The host currently implements URID map/unmap for DSP and the X11 editor
features used by this implementation. Worker, state/preset, VST3 and CLAP
support are separate work. Changing chains still uses the existing rebuild
mechanism; seamless swapping is not claimed.
