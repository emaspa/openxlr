import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, readFileSync, rmSync, statSync, copyFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const root = fileURLToPath(new URL('../../', import.meta.url));
const manifest = 'com.emaspa.openxlr.browser.json';
const directories = ['chromium/native-messaging-hosts', 'opt/chrome/native-messaging-hosts'];
const extensionId = 'beeohanoffjpcofbjiiamkdammhcihaf';
const storeUpdate = { external_update_url: 'https://clients2.google.com/service/update2/crx' };
const extensionDirectories = ['chromium/extensions', 'google-chrome/extensions'];

for (const [name, recipe, expand, executable] of [
  ['Debian', 'debian/rules', text => text.replaceAll('$(PKG)', '"$out"'), '/usr/bin/openxlr'],
  ['RPM', 'packaging/rpm/openxlr.spec', text => text.replaceAll('%{buildroot}', '"$out"')
    .replaceAll('%{_sysconfdir}', '/etc').replaceAll('%{_bindir}', '/usr/bin').replaceAll('%{_datadir}', '/usr/share'), '/usr/bin/openxlr'],
  ['Nix', 'packaging/nix/package.nix', text => text, null],
]) {
  test(`${name} installs both native host manifests with its executable path`, () => {
    const directory = mkdtempSync(join(tmpdir(), 'openxlr-browser-package-'));
    try {
      mkdirSync(join(directory, 'browser'));
      copyFileSync(join(root, 'browser/native-host.json'), join(directory, 'browser/native-host.json'));
      const source = readFileSync(join(root, recipe), 'utf8');
      const block = source.match(/# Native messaging for the separately installed browser companion\.\n([\s\S]*?)\n\s*\n/);
      assert.ok(block, 'native host install commands are present');
      const out = join(directory, 'package');
      execFileSync('sh', ['-eu', '-c', expand(block[1])], { cwd: directory, env: { ...process.env, out } });
      for (const browser of directories) {
        const path = join(out, 'etc', browser, manifest);
        const actual = JSON.parse(readFileSync(path, 'utf8'));
        assert.equal(actual.path, executable ?? `${out}/bin/openxlr`);
        assert.equal(actual.name, 'com.emaspa.openxlr.browser');
        assert.equal(actual.type, 'stdio');
        assert.deepEqual(actual.allowed_origins, ['chrome-extension://beeohanoffjpcofbjiiamkdammhcihaf/']);
        assert.equal(statSync(path).mode & 0o777, 0o644);
      }
    } finally { rmSync(directory, { recursive: true, force: true }); }
  });

  test(`${name} offers the Chrome Web Store companion to Chromium and Chrome`, () => {
    const directory = mkdtempSync(join(tmpdir(), 'openxlr-browser-package-'));
    try {
      mkdirSync(join(directory, 'browser'));
      copyFileSync(join(root, 'browser/external-extension.json'), join(directory, 'browser/external-extension.json'));
      const source = readFileSync(join(root, recipe), 'utf8');
      const block = source.match(/# Chromium and Chrome offer the companion from the Chrome Web Store\.\n([\s\S]*?)\n\s*\n/);
      assert.ok(block, 'external extension install commands are present');
      const out = join(directory, 'package');
      execFileSync('sh', ['-eu', '-c', expand(block[1])], { cwd: directory, env: { ...process.env, out } });
      for (const browser of extensionDirectories) {
        const path = join(out, executable ? 'usr/share' : 'share', browser, `${extensionId}.json`);
        assert.deepEqual(JSON.parse(readFileSync(path, 'utf8')), storeUpdate);
        assert.equal(statSync(path).mode & 0o777, 0o644);
      }
    } finally { rmSync(directory, { recursive: true, force: true }); }
  });
}

test('the RPM claims both external extension files', () => {
  const source = readFileSync(join(root, 'packaging/rpm/openxlr.spec'), 'utf8');
  const files = source.slice(source.indexOf('\n%files'));
  for (const browser of extensionDirectories)
    assert.ok(files.includes(`\n%{_datadir}/${browser}/${extensionId}.json\n`));
});

test('the external extension file names the Chrome Web Store update service only', () => {
  assert.deepEqual(JSON.parse(readFileSync(join(root, 'browser/external-extension.json'), 'utf8')), storeUpdate);
});

test('the NixOS module adds the Store id to the Chromium policy only when asked', () => {
  const source = readFileSync(join(root, 'packaging/nix/module.nix'), 'utf8');
  assert.match(source, /chromiumExtension = lib\.mkOption \{\n\s*type = lib\.types\.bool;\n\s*default = false;/);
  assert.ok(source.includes(`programs.chromium.extensions = lib.mkIf cfg.chromiumExtension [ "${extensionId}" ];`));
});

test('the NixOS module exposes both generated manifests under /etc', () => {
  const source = readFileSync(join(root, 'packaging/nix/module.nix'), 'utf8');
  for (const browser of directories) {
    assert.ok(source.includes(`environment.etc."${browser}/${manifest}".source =`));
    assert.ok(source.includes(`"\${cfg.package}/etc/${browser}/${manifest}"`));
  }
});
