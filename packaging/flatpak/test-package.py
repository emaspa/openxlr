#!/usr/bin/env python3
"""Check the package's dependency inventory, exports and sandbox permissions."""
import importlib.util
import json
from pathlib import Path
import base64
import tempfile
import unittest
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('nuget_sources', HERE / 'nuget-sources.py')
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)


class PackageTests(unittest.TestCase):
    def test_locked_packages_and_downloads_agree(self):
        expected = generator.sources()
        actual = json.loads((HERE / 'nuget-sources.json').read_text())
        self.assertEqual(expected, [{k: v for k, v in item.items() if k != 'sha512'} for item in actual])
        for item in actual:
            self.assertRegex(item['sha512'], r'^[a-f0-9]{128}$')
            self.assertTrue(item['url'].startswith('https://api.nuget.org/v3-flatcontainer/'))
            self.assertNotIn('/', item['dest-filename'])

    def test_conflicting_normalized_package_identities_are_refused(self):
        with tempfile.TemporaryDirectory() as root:
            for project in ('OpenXLR.Core', 'OpenXLR.Daemon', 'OpenXLR.UI', 'OpenXLR.Tui'):
                folder = Path(root, 'src', project)
                folder.mkdir(parents=True)
                digest = base64.b64encode(bytes([0 if project == 'OpenXLR.Core' else 1]) * 64).decode()
                (folder / 'packages.lock.json').write_text(json.dumps({'dependencies': {'net10.0': {
                    'Example': {'type': 'Direct', 'resolved': '1.0', 'contentHash': digest}}}}))
            with self.assertRaisesRegex(ValueError, "Conflicting package hash") :
                generator.sources(Path(root))

    def test_permissions_are_specific_and_cannot_spawn_host_programs(self):
        manifest = json.loads((HERE / 'io.github.emaspa.openxlr.json').read_text())
        permissions = manifest['finish-args']
        self.assertIn('--device=usb', permissions)
        self.assertIn('--filesystem=xdg-run/pipewire-0:ro', permissions)
        self.assertIn('--filesystem=xdg-config/wireplumber/wireplumber.conf.d:create', permissions)
        self.assertNotIn('--filesystem=xdg-config/wireplumber:create', permissions)
        for permission in permissions:
            self.assertNotIn(permission, ('--device=all', '--filesystem=host', '--filesystem=home', '--socket=session-bus', '--socket=system-bus', '--talk-name=org.kde.KWin'))
            self.assertNotIn('org.freedesktop.Flatpak', permission)
        extension = manifest['add-extensions']['org.freedesktop.LinuxAudio.Plugins']
        self.assertEqual('lv2', extension['merge-dirs'])
        self.assertEqual(manifest['runtime-version'], extension['version'])

    def test_exports_share_the_application_id(self):
        manifest = json.loads((HERE / 'io.github.emaspa.openxlr.json').read_text())
        app = manifest['app-id']
        metadata = ET.parse(HERE / (app + '.metainfo.xml')).getroot()
        self.assertEqual(app, metadata.findtext('id'))
        self.assertEqual(app + '.desktop', metadata.findtext('launchable'))

    def test_service_script_is_packaged(self):
        build = (HERE / 'build.sh').read_text()
        self.assertIn('install -Dm755 packaging/flatpak/openxlr-flatpak-service /app/share/openxlr/openxlr-flatpak-service', build)
        script = (HERE / 'openxlr-flatpak-service').read_text()
        self.assertIn('--command=/app/lib/openxlr/daemon/OpenXLR.Daemon', script)
        self.assertIn('Restart=on-failure', script)

    def test_imports_are_private_and_do_not_grant_host_plugin_folders(self):
        permissions = json.loads((HERE / 'io.github.emaspa.openxlr.json').read_text())['finish-args']
        for folder in ('.lv2', '.clap', '.vst3'):
            self.assertIn('--persist=' + folder, permissions)
            self.assertFalse(any(item.startswith('--filesystem=') and folder in item for item in permissions))

    def test_archives_are_pinned_and_no_git_source_floats(self):
        manifest = json.loads((HERE / 'io.github.emaspa.openxlr.json').read_text())
        for module in manifest['modules']:
            for source in module.get('sources', []):
                if not isinstance(source, dict):
                    continue
                if source['type'] == 'archive':
                    self.assertRegex(source['sha256'], r'^[a-f0-9]{64}$')
                    self.assertTrue(source['url'].startswith('https://'))
                if source['type'] == 'git':
                    self.assertRegex(source.get('commit', ''), r'^[a-f0-9]{40}$')


if __name__ == '__main__':
    unittest.main()
