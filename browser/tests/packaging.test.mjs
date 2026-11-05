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

for (const [name, recipe, expand, executable] of [
  ['Debian', 'debian/rules', text => text.replaceAll('$(PKG)', '"$out"'), '/usr/bin/openxlr'],
  ['RPM', 'packaging/rpm/openxlr.spec', text => text.replaceAll('%{buildroot}', '"$out"')
    .replaceAll('%{_sysconfdir}', '/etc').replaceAll('%{_bindir}', '/usr/bin'), '/usr/bin/openxlr'],
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
}

test('the NixOS module exposes both generated manifests under /etc', () => {
  const source = readFileSync(join(root, 'packaging/nix/module.nix'), 'utf8');
  for (const browser of directories) {
    assert.ok(source.includes(`environment.etc."${browser}/${manifest}".source =`));
    assert.ok(source.includes(`"\${cfg.package}/etc/${browser}/${manifest}"`));
  }
});

test('the companion stays MV3 with a strict packaged-code policy and unchanged permissions', () => {
  const manifest = JSON.parse(readFileSync(join(root, 'browser/chromium/manifest.json'), 'utf8'));
  assert.equal(manifest.manifest_version, 3);
  assert.deepEqual(manifest.permissions, ['activeTab', 'tabs', 'tabCapture', 'offscreen', 'storage', 'nativeMessaging']);
  assert.deepEqual(manifest.content_security_policy, { extension_pages: "script-src 'self'; object-src 'none'" });
  assert.equal(manifest.host_permissions, undefined);
  assert.equal(manifest.content_scripts, undefined);
  assert.equal(manifest.externally_connectable, undefined);
});
