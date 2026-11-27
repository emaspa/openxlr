#!/usr/bin/env python3
"""Every package builds the Windows plugin host in, and none needs Wine to install.

The host is openxlr-winhost.exe beside the native helper, and the helper's
wine backend that starts it, both made only with WINHOST=1. A packaging path
that loses that flag still builds and installs, and every Windows plugin
quietly stays on yabridge, so nothing else would notice before a user did.
Wine is what runs the host, and is never more than a suggestion.

The Flatpak is left out: its SDK has no mingw extension, and the sandbox has
no Wine. The AUR recipe lives outside this repository. Standard library only.
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding='utf-8-sig')


REQUIRED = [
    # The packages run the native build without a target, so the default
    # goal of a WINHOST=1 build has to make the program.
    ('native/Makefile', '\nall: openxlr-lv2-host openxlr-winhost.exe\n'),
    ('src/OpenXLR.Core/OpenXLR.Core.csproj', '<None Include="../../native/openxlr-winhost.exe" Link="openxlr-winhost.exe"'),
    ('src/OpenXLR.Core/OpenXLR.Core.csproj', "('$(EnableNativeLv2Host)' == 'true' And '$(WINHOST)' == '1')"),
    ('debian/rules', '\nexport WINHOST = 1\n'),
    ('debian/rules', '\nexport MINGW_CC = x86_64-w64-mingw32-gcc-posix\n'),
    ('debian/rules', '\nexport MINGW_CXX = x86_64-w64-mingw32-g++-posix\n'),
    ('debian/control', ' gcc-mingw-w64-x86-64-posix, g++-mingw-w64-x86-64-posix, libdbus-1-dev\n'),
    # WINHOST=1 links the helper with libdbus, for RTKit (PKGS in native/Makefile).
    ('native/Makefile', '\nifeq ($(WINHOST),1)\nPKGS += dbus-1\nendif\n'),
    ('debian/control', '\n libdbus-1-3\n'),
    ('packaging/rpm/openxlr.spec', '\nexport WINHOST=1\n'),
    ('packaging/rpm/openxlr.spec', '\nBuildRequires:  mingw64-gcc\n'),
    ('packaging/rpm/openxlr.spec', '\nBuildRequires:  mingw64-gcc-c++\n'),
    ('packaging/rpm/openxlr.spec', '\nBuildRequires:  mingw64-winpthreads-static\n'),
    ('packaging/rpm/openxlr.spec', '\nSuggests:       wine\n'),
    ('packaging/rpm/openxlr.spec', '\nBuildRequires:  dbus-devel\n'),
    ('packaging/rpm/openxlr.spec', '\nRequires:       dbus-libs\n'),
    ('packaging/rpm/rpmlint.toml', '"explicit-lib-dependency dbus-libs"'),
    ('packaging/nix/package.nix', 'WINHOST = "1";'),
    ('packaging/nix/package.nix', '++ lib.optional winhost dbus;'),
    ('packaging/nix/package.nix', 'mingw = pkgsCross.mingwW64.buildPackages.gcc;'),
    ('packaging/nix/package.nix', 'MINGW_CXX = "${mingw}/bin/${mingw.targetPrefix}g++";'),
    ('.github/workflows/release-deb.yml', 'gcc-mingw-w64-x86-64-posix g++-mingw-w64-x86-64-posix libdbus-1-dev'),
    ('.github/workflows/release-deb.yml', 'usr/lib/openxlr/daemon/openxlr-winhost.exe'),
    ('.github/workflows/release-rpm.yml', 'mingw64-gcc mingw64-gcc-c++ mingw64-winpthreads-static dbus-devel'),
    ('.github/workflows/release-rpm.yml', '/usr/lib/openxlr/daemon/openxlr-winhost.exe'),
    ('.github/workflows/ci.yml', 'test-winhost'),
    # The host links the MinGW-w64 runtime and winpthreads statically, and
    # their licences ask for their notices to come with it.
    ('packaging/rpm/openxlr.spec', '\ninstall -Dm644 native/winhost/NOTICE %{buildroot}%{_licensedir}/openxlr/winhost/NOTICE\n'),
    ('packaging/rpm/openxlr.spec', '\n%license %{_licensedir}/openxlr/winhost/\n'),
    ('.github/workflows/release-rpm.yml', 'test -f /usr/share/licenses/openxlr/winhost/NOTICE'),
    ('debian/rules', '\tinstall -Dm644 native/winhost/NOTICE $(PKG)/usr/share/doc/openxlr/winhost/NOTICE\n'),
    ('debian/copyright', '\nFiles: native/winhost/NOTICE\n'),
    ('.github/workflows/release-deb.yml', 'usr/share/doc/openxlr/winhost/NOTICE'),
    ('packaging/nix/package.nix', 'install -Dm644 native/winhost/NOTICE $out/share/licenses/openxlr/winhost/NOTICE'),
]

errors = [f'{path}: missing {text.strip()!r}' for path, text in REQUIRED if text not in read(path)]


def field(text, name):
    """A Debian control field of the binary package, folded onto one line."""
    binary = text.split('\nPackage: ', 1)[1]
    match = re.search(rf'^{name}:(.*(?:\n .*)*)', binary, re.M)
    return match.group(1).replace('\n', ' ') if match else ''


# Wine is optional: a hard dependency would pull it onto every system that
# installs OpenXLR, and a weak one that installs by default nearly as much.
control = read('debian/control')
for name in ('Depends', 'Pre-Depends', 'Recommends'):
    if re.search(r'(^|[ ,|])wine(64|32)?\b', field(control, name)):
        errors.append(f'debian/control: wine in {name}, it is only suggested')
if not re.search(r'(^|[ ,])wine(,|$)', field(control, 'Suggests').strip()):
    errors.append("debian/control: missing 'wine' in Suggests")
spec = read('packaging/rpm/openxlr.spec')
for line in spec.splitlines():
    if re.match(r'(Requires|Recommends)(\([^)]*\))?:\s.*\bwine', line):
        errors.append(f'packaging/rpm/openxlr.spec: {line.strip()!r}, wine is only suggested')
if re.search(r'\bwine\b', read('packaging/nix/package.nix').split('runtimeDeps', 1)[1].split('postFixup', 1)[0]):
    errors.append('packaging/nix/package.nix: wine in the runtime dependencies or the wrapper PATH')

checks = len(REQUIRED) + 4
for error in errors:
    print(f'winhost: {error}', file=sys.stderr)
print(f'winhost: {checks} checks, ' + ('ok' if not errors else f'{len(errors)} problems'))
sys.exit(1 if errors else 0)
