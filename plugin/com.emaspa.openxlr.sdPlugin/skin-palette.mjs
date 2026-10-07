// The saved skin is shared with the window and terminal. Only bounded data
// is read here; images, SVG fragments and paths from the document are ignored.
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';

const palettes = JSON.parse(fs.readFileSync(new URL('./skin-palettes.json', import.meta.url), 'utf8'));
const defaults = palettes.default;
const MAX_BYTES = 256 * 1024;
const validId = id => typeof id === 'string' && /^[a-z0-9][a-z0-9._-]{0,63}$/.test(id);
const named = {black:'#000000', white:'#ffffff', transparent:'#00000000',
  red:'#ff0000', green:'#008000', blue:'#0000ff', yellow:'#ffff00', gray:'#808080', grey:'#808080'};

function colour(value) {
  if (value && typeof value === 'object' && ['linear', 'radial'].includes(value.type)) {
    if (!Array.isArray(value.stops) || value.stops.length < 2 || value.stops.length > 16) return null;
    let previous = -1;
    for (const stop of value.stops) {
      if (typeof stop?.offset !== 'number' || !Number.isFinite(stop.offset) ||
          stop.offset < previous || stop.offset < 0 || stop.offset > 1 ||
          typeof stop.color !== 'string' || colour(stop.color) === null) return null;
      previous = stop.offset;
    }
    value = value.stops[0].color;
  }
  if (typeof value !== 'string') return null;
  const name = value.toLowerCase();
  if (Object.hasOwn(named, name)) return named[name];
  if (!/^#(?:[\da-f]{3}|[\da-f]{4}|[\da-f]{6}|[\da-f]{8})$/i.test(value)) return null;
  let hex = value.slice(1);
  if (hex.length <= 4) hex = [...hex].map(c => c + c).join('');
  // Avalonia writes alpha first. SVG/CSS writes alpha last.
  return '#' + (hex.length === 8 ? hex.slice(2) + hex.slice(0, 2) : hex);
}

export function paletteFrom(tokens) {
  const result = {};
  for (const [key, fallback] of Object.entries(defaults)) {
    const value = tokens?.[key];
    result[key] = typeof fallback === 'number'
      ? typeof value === 'number' && Number.isFinite(value) && value >= 0 && value <= 1 ? value : fallback
      : colour(key.startsWith('Ox.Led.') || ['Ox.Meter.Fill', 'Ox.Meter.Warning', 'Ox.Meter.Hot'].includes(key)
        ? typeof value === 'string' ? value : null : value) ?? colour(fallback);
  }
  // Crossed thresholds are refused together, as in the window.
  if (result['Ox.Meter.WarningLevel'] > result['Ox.Meter.HotLevel']) {
    result['Ox.Meter.WarningLevel'] = defaults['Ox.Meter.WarningLevel'];
    result['Ox.Meter.HotLevel'] = defaults['Ox.Meter.HotLevel'];
  }
  return Object.freeze(result);
}

function readJson(file) {
  let fd;
  try {
    // Nonblocking open plus fstat refuses pipes without waiting for a writer.
    fd = fs.openSync(file, fs.constants.O_RDONLY | fs.constants.O_NONBLOCK);
    const info = fs.fstatSync(fd);
    if (!info.isFile() || info.size > MAX_BYTES) return null;
    const bytes = Buffer.alloc(Math.min(info.size + 1, MAX_BYTES + 1));
    const length = fs.readSync(fd, bytes, 0, bytes.length, 0);
    if (length > MAX_BYTES) return null;
    const json = JSON.parse(bytes.subarray(0, length).toString('utf8'));
    return json && typeof json === 'object' && !Array.isArray(json) ? json : null;
  } catch { return null; }
  finally { if (fd !== undefined) fs.closeSync(fd); }
}

function publisherAlive(pid) {
  if (!Number.isSafeInteger(pid) || pid < 1 || pid > 2147483647) return false;
  try { process.kill(pid, 0); return true; } catch { return false; }
}

export class SkinPalette {
  constructor(onChange, {env = process.env, home = os.homedir()} = {}) {
    this.onChange = onChange;
    this.preferences = path.join(env.XDG_CONFIG_HOME || path.join(home, '.config'), 'openxlr', 'ui.json');
    this.live = path.join(path.dirname(this.preferences), 'deck-palette.json');
    this.roots = [...new Set([env.XDG_DATA_HOME || path.join(home, '.local/share'),
      ...(env.XDG_DATA_DIRS || '/usr/local/share:/usr/share').split(':').filter(Boolean)])]
      .slice(0, 32).map(root => path.join(root, 'openxlr/skins'));
    this.override = env.OPENXLR_SKIN || undefined;
    this.watchers = [];
    this.timer = null;
    this.closed = false;
    this.reload(false);
  }

  reload(notify = true) {
    if (this.closed) return;
    let id = this.override ?? readJson(this.preferences)?.skin;
    if (!validId(id)) id = 'default';
    let tokens = palettes[id] ?? defaults;
    if (id !== 'default') {
      for (const root of this.roots) {
        const file = path.join(root, id, 'skin.json');
        // An existing local package wins over the embedded package even if
        // its document is invalid. The latter then uses Material's defaults.
        try {
          fs.lstatSync(file);
          const doc = readJson(file);
          tokens = doc?.schema === 1 ? doc.tokens : null;
          break;
        } catch { /* try the next root */ }
      }
    }
    const live = readJson(this.live);
    if (this.override === undefined && live?.schema === 1 && live.skin === id && publisherAlive(live.pid) &&
        live.tokens && typeof live.tokens === 'object' && !Array.isArray(live.tokens)) tokens = live.tokens;
    const next = paletteFrom(tokens);
    const changed = JSON.stringify(next) !== JSON.stringify(this.colours);
    this.colours = next;
    this.id = id;
    this.watch(id);
    if (notify && changed) this.onChange?.();
  }

  watch(id) {
    for (const watcher of this.watchers) watcher.close();
    this.watchers = [];
    const watched = new Map();
    for (const file of [this.preferences, this.live, ...(id === 'default' ? [] : this.roots.map(root => path.join(root, id, 'skin.json')))]) {
      let folder = path.dirname(file);
      while (!fs.existsSync(folder) && path.dirname(folder) !== folder) folder = path.dirname(folder);
      const nextPart = path.relative(folder, file).split(path.sep)[0];
      if (!watched.has(folder)) watched.set(folder, new Set());
      watched.get(folder).add(nextPart);
    }
    for (const [folder, parts] of watched) {
      try {
        const watcher = fs.watch(folder, {persistent:false}, (_event, name) => {
          if (name !== null && !parts.has(name.toString()) && name.toString() !== path.basename(folder)) return;
          if (this.timer) return;
          this.timer = setTimeout(() => { this.timer = null; this.reload(); }, 50);
          this.timer.unref();
        });
        watcher.on('error', () => { watcher.close(); });
        this.watchers.push(watcher);
      } catch { /* an inaccessible directory cannot be watched */ }
    }
  }

  close() {
    this.closed = true;
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    for (const watcher of this.watchers) watcher.close();
    this.watchers = [];
  }
}
