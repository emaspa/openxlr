#!/usr/bin/env python3
"""Exercise the installed Flatpak on private audio, config, network and bus state.

Run under Xvfb for CI. On a desktop, the same X11 calls exercise XWayland.
No host audio, USB settings, autostart entries or profiles are changed.
"""
import array
import ctypes as c
import json
import math
import os
import re
import shutil
from pathlib import Path
import subprocess
import sys
import time
import tempfile
import urllib.request

APP = 'io.github.emaspa.openxlr'
ROOT = Path(__file__).resolve().parent.parent


def wait(check, seconds=30):
    deadline = time.monotonic() + seconds
    last = None
    while time.monotonic() < deadline:
        try:
            value = check()
            if value:
                return value
        except (OSError, urllib.error.URLError) as error:
            last = error
        time.sleep(0.05)
    raise AssertionError(f'Deadline exceeded: {last or check}')


def run(*args):
    return subprocess.check_output(args, text=True, timeout=15).strip()


def stop_process(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


class Windows:
    """The small X11 surface also used by native plugin editors on Wayland."""
    class Data(c.Union):
        _fields_ = [('l', c.c_long * 5)]

    class Message(c.Structure):
        pass

    Message._fields_ = [('type', c.c_int), ('serial', c.c_ulong), ('send', c.c_int),
                        ('display', c.c_void_p), ('window', c.c_ulong), ('message', c.c_ulong),
                        ('format', c.c_int), ('data', Data)]

    class Attributes(c.Structure):
        _fields_ = [(name, c.c_int) for name in ('x', 'y', 'width', 'height', 'border', 'depth')] + [
            ('visual', c.c_void_p), ('root', c.c_ulong), ('kind', c.c_int), ('bit_gravity', c.c_int),
            ('win_gravity', c.c_int), ('backing_store', c.c_int), ('backing_planes', c.c_ulong),
            ('backing_pixel', c.c_ulong), ('save_under', c.c_int), ('colormap', c.c_ulong),
            ('map_installed', c.c_int), ('map_state', c.c_int), ('all_events', c.c_long),
            ('your_events', c.c_long), ('do_not_propagate', c.c_long), ('override_redirect', c.c_int),
            ('screen', c.c_void_p)]

    def attributes(self, window):
        value = self.Attributes()
        if not self.x.XGetWindowAttributes(self.display, window, c.byref(value)):
            raise OSError(f'X11 window {window} is no longer available')
        return value

    def __init__(self):
        self.x = c.CDLL('libX11.so.6')
        signatures = {
            'XOpenDisplay': (c.c_void_p, [c.c_char_p]),
            'XGetWindowAttributes': (c.c_int, [c.c_void_p, c.c_ulong, c.POINTER(self.Attributes)]),
            'XDefaultRootWindow': (c.c_ulong, [c.c_void_p]),
            'XQueryTree': (c.c_int, [c.c_void_p, c.c_ulong, c.POINTER(c.c_ulong), c.POINTER(c.c_ulong), c.POINTER(c.POINTER(c.c_ulong)), c.POINTER(c.c_uint)]),
            'XFetchName': (c.c_int, [c.c_void_p, c.c_ulong, c.POINTER(c.c_void_p)]),
            'XFree': (c.c_int, [c.c_void_p]),
            'XInternAtom': (c.c_ulong, [c.c_void_p, c.c_char_p, c.c_int]),
            'XSendEvent': (c.c_int, [c.c_void_p, c.c_ulong, c.c_int, c.c_long, c.c_void_p]),
            'XSync': (c.c_int, [c.c_void_p, c.c_int]),
            'XMoveResizeWindow': (c.c_int, [c.c_void_p, c.c_ulong, c.c_int, c.c_int, c.c_uint, c.c_uint]),
            'XCloseDisplay': (c.c_int, [c.c_void_p]),
        }
        for name, (result, args) in signatures.items():
            function = getattr(self.x, name)
            function.restype, function.argtypes = result, args
        handler = c.CFUNCTYPE(c.c_int, c.c_void_p, c.c_void_p)
        self.error_handler = handler(lambda display, event: 0)
        self.x.XSetErrorHandler.argtypes = [handler]
        self.x.XSetErrorHandler(self.error_handler)
        self.display = self.x.XOpenDisplay(None)
        if not self.display:
            raise RuntimeError('An X11 display or XWayland is required')

    def titles(self):
        pending = [self.x.XDefaultRootWindow(self.display)]
        found = {}
        for _ in range(2048):
            if not pending:
                return found
            window = pending.pop()
            name = c.c_void_p()
            if self.x.XFetchName(self.display, window, c.byref(name)) and name.value:
                found[window] = c.string_at(name).decode(errors='replace')
                self.x.XFree(name)
            root, parent, children, count = c.c_ulong(), c.c_ulong(), c.POINTER(c.c_ulong)(), c.c_uint()
            if self.x.XQueryTree(self.display, window, c.byref(root), c.byref(parent), c.byref(children), c.byref(count)):
                pending.extend(children[i] for i in range(min(count.value, 2048)))
                if children:
                    self.x.XFree(children)
        raise AssertionError('Unexpectedly large X11 window tree')

    def close_window(self, window):
        event = c.create_string_buffer(192)
        message = self.Message.from_buffer(event)
        message.type, message.send, message.display, message.window = 33, 1, self.display, window
        message.message = self.x.XInternAtom(self.display, b'WM_PROTOCOLS', 0)
        message.format, message.data.l[0] = 32, self.x.XInternAtom(self.display, b'WM_DELETE_WINDOW', 0)
        self.x.XSendEvent(self.display, window, 0, 0, event)
        self.x.XSync(self.display, 0)

    def resize(self, window, width, height):
        self.x.XMoveResizeWindow(self.display, window, 30, 30, width, height)
        self.x.XSync(self.display, 0)

    def close(self):
        self.x.XCloseDisplay(self.display)


def sandbox():
    runtime = Path(os.environ['OPENXLR_ACCEPTANCE_RUNTIME'])
    os.environ['XDG_CONFIG_HOME'] = str(runtime / 'flatpak/config')
    os.environ['XDG_DATA_HOME'] = str(runtime / 'flatpak/data')
    os.environ['XDG_STATE_HOME'] = str(runtime / 'flatpak/state')
    config = Path(os.environ['XDG_CONFIG_HOME'], 'openxlr')
    config.mkdir(parents=True, exist_ok=True)
    (config / 'daemon.json').write_text('{"submixer":true}')
    (config / 'mixer.json').write_text(json.dumps({'inputBindings': {'xlr1': {'source': 'acceptance_mic', 'pair': 0, 'monoChannel': 0}}}))
    (config / 'flatpak-notice').write_text('1\n')
    modules = [run('pactl', 'load-module', 'module-null-sink', 'sink_name=acceptance_output', 'channels=2'),
               run('pactl', 'load-module', 'module-null-sink', 'sink_name=acceptance_mic_sink', 'channels=2'),
               run('pactl', 'load-module', 'module-remap-source', 'master=acceptance_mic_sink.monitor', 'source_name=acceptance_mic', 'channels=1', 'master_channel_map=front-left', 'channel_map=mono')]
    token = Path(os.environ['XDG_RUNTIME_DIR'], 'openxlr', 'token')
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def api(path='state', command=None):
        headers = {'Authorization': 'Bearer ' + token.read_text().strip()}
        data = None
        if command is not None:
            path, data = 'commands', json.dumps(command).encode()
            headers['Content-Type'] = 'application/json'
        with opener.open(urllib.request.Request('http://127.0.0.1:37890/api/v1/' + path, data=data, headers=headers), timeout=10) as response:
            result = json.load(response)
            if isinstance(result, dict) and result.get('ok') is False:
                raise AssertionError(result)
            return result

    def command(name, **fields):
        return api(command={'cmd': name, **fields})

    window = Windows()
    existing_windows = set(window.titles())
    desktop = os.environ.get('OPENXLR_ACCEPTANCE_DESKTOP') == '1'
    def tray_items():
        reply = run('gdbus', 'call', '--session', '--dest', 'org.kde.StatusNotifierWatcher',
                    '--object-path', '/StatusNotifierWatcher', '--method', 'org.freedesktop.DBus.Properties.Get',
                    'org.kde.StatusNotifierWatcher', 'RegisteredStatusNotifierItems')
        return set(re.findall(r"'([^']+)'", reply))
    previous_tray = tray_items() if desktop else set()
    own_tray = set()
    imported = None
    with open(runtime / 'application.log', 'w') as log:
        app = subprocess.Popen(['openxlr'], stdout=log, stderr=log)
        try:
            wait(lambda: token.exists() and api().get('mixer'), 90)
            main = wait(lambda: next((w for w, title in window.titles().items() if w not in existing_windows and title == 'OpenXLR'), None))
            if desktop:
                own_tray = wait(lambda: tray_items() - previous_tray)
            window.resize(main, 360, 640)
            wait(lambda: window.attributes(main).width == 360)
            window.resize(main, 1400, 900)
            wait(lambda: window.attributes(main).width == 1400)
            window.close_window(main)
            # A close request must leave the daemon and GUI process alive.
            wait(lambda: window.attributes(main).map_state == 0)
            assert api().get('mixer')
            if desktop:
                assert own_tray <= tray_items(), 'The real tray item disappeared on close'
            assert app.poll() is None
            run('openxlr')  # single-instance restore, including an unmapped mixer
            wait(lambda: window.attributes(main).map_state == 2)
            assert app.poll() is None
            command('setMonitorOutputs', devices=['acceptance_output'])
            run('pactl', 'set-default-sink', 'acceptance_output')
            command('setMainOutput', device='acceptance_output')
            wait(lambda: run('pactl', 'get-default-sink') == 'acceptance_output')
            command('setLevel', channel='browser', mix='monitor', value=1.0)
            command('setMixVolume', mix='monitor', value=0.5)
            command('setMixMuted', mix='monitor', value=False)
            print('PASS: GUI startup, close, single-instance restore and resize requests', flush=True)

            def measure(expected=0.0125):
                signal = runtime / 'signal.raw'
                signal.write_bytes(array.array('f', [0.1] * (48000 * 2 * 4)).tobytes())
                capture = runtime / 'capture.raw'
                with open(capture, 'wb') as output:
                    record = subprocess.Popen(['parec', '--raw', '--format=float32le', '--rate=48000', '--channels=2', '--device=acceptance_output.monitor'], stdout=output, stderr=log)
                    play = subprocess.Popen(['paplay', '--raw', '--format=float32le', '--rate=48000', '--channels=2', '--device=OpenXLR_ch_browser', str(signal)], stdout=log, stderr=log)
                    try:
                        wait(lambda: capture.stat().st_size >= 48000 * 2 * 4 * 2, 15)
                    finally:
                        for process in (play, record):
                            stop_process(process)
                samples = array.array('f'); samples.frombytes(capture.read_bytes())
                assert samples and all(math.isfinite(value) for value in samples)
                peak = max(abs(value) for value in samples[-48000 * 2:])
                assert abs(peak - expected) < expected * 0.25, f'Expected settled amplitude {expected}, got {peak}'
                return peak

            print(f'PASS: sandbox audio, measured monitor gain {measure():.6f}', flush=True)
            plugins = api('plugins')
            plugin = 'http://lsp-plug.in/plugins/lv2/comp_delay_stereo'
            assert plugin in json.dumps(plugins), 'Install the LSP LinuxAudio extension before this test'
            source = Path(tempfile.mkdtemp(prefix='openxlr-acceptance-', suffix='.lv2', dir=runtime))
            shutil.copytree('/app/extensions/Plugins/lv2/lsp-plugins.lv2', source, dirs_exist_ok=True)
            imported = Path.home() / '.lv2' / source.name
            assert not imported.exists()
            command('installPlugin', path=str(source))
            assert (imported / 'manifest.ttl').is_file(), 'The sandbox could not import an LV2 bundle'
            setup = api('plugin-setup')['messages'][0]
            assert setup['lv2Directory'] == '~/.lv2'
            for folder in ('.lv2', '.clap', '.vst3'):
                directory = Path.home() / folder
                directory.mkdir(exist_ok=True)
                with tempfile.TemporaryFile(dir=directory) as probe:
                    probe.write(b'private plugin storage')
            discovery = api('plugin-diagnostics')['messages'][0]['discovery']
            assert str(Path.home() / '.lv2') in discovery['searchPaths']['lv2Override'].split(':')
            print('PASS: private LV2 import and writable private LV2/CLAP/VST3 folders', flush=True)
            command('setInserts', channel='mix:monitor', inserts=[{'id': 'acceptance', 'kind': 'lv2', 'plugin': plugin, 'nativeHost': True}])
            wait(lambda: any(item.get('nativeHostRunning') for item in api('inserts/mix:monitor')), 30)
            before = set(window.titles())
            command('showInsertUi', channel='mix:monitor', insertId='acceptance')
            editor = wait(lambda: next((w for w, title in window.titles().items()
                                        if w not in before and title.startswith('OpenXLR - ')), None), 20)
            window.resize(editor, 720, 480)
            window.close_window(editor)
            wait(lambda: editor not in window.titles() or window.attributes(editor).map_state == 0)
            command('showInsertUi', channel='mix:monitor', insertId='acceptance')
            wait(lambda: any(w not in before and title.startswith('OpenXLR - ') and window.attributes(w).map_state == 2
                             for w, title in window.titles().items()))
            command('setInsertParam', channel='mix:monitor', insertId='acceptance', symbol='g_out', value=0.5)
            print(f'PASS: live plugin parameter, measured amplitude {measure(0.00625):.6f}', flush=True)
            command('setInsertParam', channel='mix:monitor', insertId='acceptance', symbol='g_out', value=1.0)
            print(f'PASS: LinuxAudio plugin, native host and editor reopen; audio peak {measure():.6f}', flush=True)
            command('setInserts', channel='mix:monitor', inserts=[])
        except Exception:
            log.flush()
            for path in (runtime / 'application.log', Path(os.environ['XDG_STATE_HOME'], 'openxlr', 'daemon.log')):
                if path.exists():
                    with path.open('rb') as evidence:
                        evidence.seek(max(0, path.stat().st_size - 16384))
                        print(evidence.read().decode(errors='replace'), flush=True)
            raise
        finally:
            try:
                # Use the exported tray action so this reaches the real Quit
                # callback and waits for normal daemon cleanup.
                subprocess.run(['gdbus', 'call', '--session', '--dest', APP + '.Tray', '--object-path', '/MenuBar',
                                         '--method', 'com.canonical.dbusmenu.Event', 'int32 3', 'clicked', '<0>', 'uint32 0'],
                                        stdout=subprocess.DEVNULL, stderr=log, timeout=10)
                app.wait(timeout=50)
                assert app.returncode == 0, 'The window did not quit normally'
                def daemon_stopped():
                    try:
                        api()
                        return False
                    except urllib.error.URLError:
                        return True
                wait(daemon_stopped, 50)
                wait(lambda: 'OpenXLR_' not in run('pactl', 'list', 'short', 'sinks'), 15)
                if desktop:
                    wait(lambda: not (own_tray & tray_items()))
                    print('PASS: real desktop tray registration, close, restore and unregister', flush=True)
                print('PASS: GUI exit released its daemon and audio nodes', flush=True)
            finally:
                try:
                    stop_process(app)
                finally:
                    window.close()
                    if imported is not None and imported.is_dir() and not imported.is_symlink():
                        shutil.rmtree(imported)
                    for module in reversed(modules):
                        run('pactl', 'unload-module', module)



def private_host():
    runtime = Path(os.environ['XDG_RUNTIME_DIR'])
    private = runtime / 'flatpak'
    private.mkdir()
    command = ['flatpak', 'run', '--user', '--unshare=network', '--nodevice=usb',
               '--nofilesystem=xdg-config/wireplumber', '--nofilesystem=xdg-run/openxlr',
               '--nofilesystem=xdg-config/wireplumber/wireplumber.conf.d',
               '--filesystem=' + str(runtime), '--filesystem=' + str(ROOT) + ':ro',
               '--env=OPENXLR_ACCEPTANCE_RUNTIME=' + str(runtime),
               '--env=XDG_RUNTIME_DIR=' + str(private), '--env=XDG_CONFIG_HOME=' + str(private / 'config'),
               '--env=PIPEWIRE_RUNTIME_DIR=' + str(runtime), '--env=PIPEWIRE_REMOTE=pipewire-0',
               '--env=PULSE_SERVER=' + os.environ['PULSE_SERVER'],
               '--command=python3', APP, str(Path(__file__).resolve()), '--inside-sandbox']
    if os.environ.get('OPENXLR_ACCEPTANCE_DESKTOP_BUS'):
        command.insert(2, '--env=OPENXLR_ACCEPTANCE_DESKTOP=1')
    env = dict(os.environ, XDG_RUNTIME_DIR=f'/run/user/{os.getuid()}',
               XDG_DATA_HOME=os.environ['OPENXLR_FLATPAK_USER_DATA'],
               XDG_CONFIG_HOME=os.environ['OPENXLR_FLATPAK_USER_CONFIG'])
    if os.environ.get('OPENXLR_ACCEPTANCE_DESKTOP_BUS'):
        env['DBUS_SESSION_BUS_ADDRESS'] = os.environ['OPENXLR_ACCEPTANCE_DESKTOP_BUS']
    subprocess.run(command, check=True, timeout=240, env=env)


if __name__ == '__main__':
    if sys.argv[1:] == ['--help']:
        print(__doc__)
        raise SystemExit(0)
    if sys.argv[1:] == ['--inside-sandbox']:
        sandbox()
    elif sys.argv[1:] == ['--inside-host']:
        private_host()
    elif sys.argv[1:] in ([], ['--desktop']):
        subprocess.run([sys.executable, str(ROOT / 'tools/test-monitor-volume.py'), '--command',
                        sys.executable, str(Path(__file__).resolve()), '--inside-host'], check=True,
                       env=dict(os.environ,
                                OPENXLR_ACCEPTANCE_DESKTOP_BUS=os.environ.get('DBUS_SESSION_BUS_ADDRESS', '') if sys.argv[1:] == ['--desktop'] else '',
                                OPENXLR_FLATPAK_USER_DATA=os.environ.get('XDG_DATA_HOME', str(Path.home() / '.local/share')),
                                OPENXLR_FLATPAK_USER_CONFIG=os.environ.get('XDG_CONFIG_HOME', str(Path.home() / '.config'))))
    else:
        raise SystemExit(__doc__)
