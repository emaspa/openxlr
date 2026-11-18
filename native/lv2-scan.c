// SPDX-License-Identifier: GPL-3.0-only
// The LV2 scanner: every installed plugin's description as JSON, for the
// daemon's catalogue. lilv parses every bundle's Turtle files here, in a
// process of its own, so a bundle whose data crashes or stalls the parser
// costs one scan and never the daemon. No plugin code is loaded.
//
// What is printed, and what is left out, follows the rules the daemon's
// own reading applied before this scanner existed (Lv2Catalog.cs keeps
// that reading for builds without the native host), so the catalogue
// reads the same either way. The daemon checks every value again.
//
// lilv's own loader is walked here bundle by bundle, so a phase marker names
// each bundle before its manifest is read, and a bundle the daemon has seen
// crash or stall the scan can be left out (`--skip`).
#define _GNU_SOURCE  // dl_iterate_phdr and memmem
#include "host.h"

#include <dirent.h>
#include <lilv/lilv.h>
#include <link.h>
#include <lv2/core/lv2.h>
#include <lv2/port-props/port-props.h>
#include <lv2/ui/ui.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

enum {
  // The daemon's limits (Lv2Catalog.cs): a plugin declaring more ports is
  // skipped, and the lists below stop at these counts.
  SCAN_MAX_PORTS = 4096,
  SCAN_MAX_CONTROLS = 512,
  SCAN_MAX_POINTS = 256,
  SCAN_MAX_FEATURES = 64,
  // Bytes of one string. The daemon clips names to far less; this only
  // bounds what a bundle can make the scanner print.
  SCAN_MAX_TEXT = 4096,
  // Bytes of a bundle path in a phase marker.
  SCAN_MAX_MARKER = 512,
};

// The length of the valid UTF-8 sequence at s (at most n bytes), or 0 when
// none starts there. Overlong forms, surrogates and values past U+10FFFF
// are invalid.
static size_t utf8_sequence(const unsigned char *s, size_t n) {
  if (s[0] < 0x80)
    return 1;
  size_t length;
  unsigned char low = 0x80, high = 0xbf;
  if (s[0] >= 0xc2 && s[0] <= 0xdf)
    length = 2;
  else if (s[0] >= 0xe0 && s[0] <= 0xef) {
    length = 3;
    if (s[0] == 0xe0)
      low = 0xa0;
    else if (s[0] == 0xed)
      high = 0x9f;
  } else if (s[0] >= 0xf0 && s[0] <= 0xf4) {
    length = 4;
    if (s[0] == 0xf0)
      low = 0x90;
    else if (s[0] == 0xf4)
      high = 0x8f;
  } else
    return 0;
  if (n < length)
    return 0;
  if (s[1] < low || s[1] > high)
    return 0;
  for (size_t i = 2; i < length; ++i)
    if (s[i] < 0x80 || s[i] > 0xbf)
      return 0;
  return length;
}

// The bytes of an ill-formed sequence that one replacement character
// stands for: the lead byte and the continuation bytes that could still
// have completed it, the way .NET decodes the same bytes.
static size_t utf8_invalid(const unsigned char *s, size_t n) {
  size_t length;
  unsigned char low = 0x80, high = 0xbf;
  if (s[0] >= 0xc2 && s[0] <= 0xdf)
    length = 2;
  else if (s[0] >= 0xe0 && s[0] <= 0xef) {
    length = 3;
    if (s[0] == 0xe0)
      low = 0xa0;
    else if (s[0] == 0xed)
      high = 0x9f;
  } else if (s[0] >= 0xf0 && s[0] <= 0xf4) {
    length = 4;
    if (s[0] == 0xf0)
      low = 0x90;
    else if (s[0] == 0xf4)
      high = 0x8f;
  } else
    return 1;
  size_t used = 1;
  if (used < n && used < length && s[1] >= low && s[1] <= high) {
    ++used;
    while (used < n && used < length && s[used] >= 0x80 && s[used] <= 0xbf)
      ++used;
  }
  return used;
}

// A JSON string, or null. Bytes that are not UTF-8 become U+FFFD, so the
// output always parses; the text stops at SCAN_MAX_TEXT bytes, between
// characters.
static void json_text(const char *text) {
  if (!text) {
    fputs("null", stdout);
    return;
  }
  const unsigned char *s = (const unsigned char *)text;
  size_t n = strlen(text);
  putchar('"');
  for (size_t i = 0; i < n;) {
    size_t length = utf8_sequence(s + i, n - i);
    size_t step = length ? length : utf8_invalid(s + i, n - i);
    if (i + step > SCAN_MAX_TEXT)
      break;
    if (!length)
      fputs("\\ufffd", stdout);
    else if (s[i] == '"' || s[i] == '\\')
      printf("\\%c", s[i]);
    else if (s[i] < 0x20)
      printf("\\u%04x", s[i]);
    else
      fwrite(s + i, 1, length, stdout);
    i += step;
  }
  putchar('"');
}

// A bundle's text in a phase marker: the path its URI names, with every byte
// outside printable ASCII and every '%' written as %XX. A hostile name cannot
// forge a marker line of its own, the daemon reads the path back from it to
// remember the bundle, and hands the same text back with `--skip`. At most
// SCAN_MAX_MARKER bytes, never ending inside an escape.
static void bundle_text(const LilvNode *bundle, char text[SCAN_MAX_MARKER + 1]) {
  const char *uri = bundle ? lilv_node_as_uri(bundle) : NULL;
  char *path = uri ? lilv_file_uri_parse(uri, NULL) : NULL;
  const unsigned char *s = (const unsigned char *)(path ? path : uri ? uri : "");
  size_t n = 0;
  for (; *s; ++s) {
    bool plain = *s >= 0x20 && *s < 0x7f && *s != '%';
    if (n + (plain ? 1 : 3) > SCAN_MAX_MARKER)
      break;
    if (plain)
      text[n++] = (char)*s;
    else
      n += (size_t)snprintf(text + n, 4, "%%%02X", *s);
  }
  text[n] = '\0';
  lilv_free(path);
}

// The bundles named by `--skip`: argv[1], argv[3] and so on below count.
typedef struct {
  char *const *argv;
  int count;
} Skips;

static bool skipped(const Skips *skips, const char *text) {
  for (int i = 1; i < skips->count; i += 2)
    if (!strcmp(skips->argv[i], text))
      return true;
  return false;
}

// Phase markers: `trace: scan LV2 manifest N: bundle TEXT` before a bundle's
// manifest is read, `trace: scan LV2 plugin N: bundle TEXT` before a plugin's
// own data files are, and `trace: scan LV2: skip bundle TEXT` for a bundle
// left out, which names no bundle being read.
static void trace_bundle(const char *phase, unsigned index, const LilvNode *bundle) {
  char text[SCAN_MAX_MARKER + 1];
  bundle_text(bundle, text);
  fprintf(stderr, "trace: scan LV2 %s %u: bundle %s\n", phase, index, text);
}

// Floats keep nine significant digits, enough to read back the same float.
static void json_float(float value) { printf("%.9g", (double)value); }

typedef struct {
  LilvWorld *world;
  LilvNode *control, *audio, *input, *output, *toggled, *integer,
      *enumeration, *logarithmic, *not_on_gui, *x11, *required;
} Classes;

// The required features of a node set, as URIs, up to the limit.
static void json_features(const LilvNodes *nodes) {
  putchar('[');
  unsigned count = 0;
  LILV_FOREACH(nodes, i, nodes) {
    if (count >= SCAN_MAX_FEATURES)
      break;
    const char *uri = lilv_node_as_uri(lilv_nodes_get(nodes, i));
    if (!uri)
      continue;
    if (count++)
      putchar(',');
    json_text(uri);
  }
  putchar(']');
}

// The control's scale points: a label and a finite value each, as the
// daemon keeps them.
static void json_points(const LilvPlugin *plugin, const LilvPort *port) {
  putchar('[');
  LilvScalePoints *points = lilv_port_get_scale_points(plugin, port);
  unsigned count = 0;
  LILV_FOREACH(scale_points, i, points) {
    if (count >= SCAN_MAX_POINTS)
      break;
    const LilvScalePoint *point = lilv_scale_points_get(points, i);
    const LilvNode *label = lilv_scale_point_get_label(point);
    const char *text = label ? lilv_node_as_string(label) : NULL;
    const LilvNode *node = lilv_scale_point_get_value(point);
    float value = node && (lilv_node_is_float(node) || lilv_node_is_int(node))
                      ? lilv_node_as_float(node)
                      : 0;
    if (!text || !isfinite(value))
      continue;
    printf("%s{\"label\":", count++ ? "," : "");
    json_text(text);
    fputs(",\"value\":", stdout);
    json_float(value);
    putchar('}');
  }
  lilv_scale_points_free(points);
  putchar(']');
}

static void json_symbols(const char *const *symbols, unsigned count) {
  putchar('[');
  for (unsigned i = 0; i < count; ++i) {
    if (i)
      putchar(',');
    json_text(symbols[i]);
  }
  putchar(']');
}

// One plugin's object. False, with nothing printed, for a plugin the
// catalogue never lists whatever the daemon decides: no URI, or more ports
// than anything real declares.
static bool describe(const LilvPlugin *plugin, const Classes *k, bool first) {
  const char *uri = lilv_node_as_uri(lilv_plugin_get_uri(plugin));
  uint32_t n = lilv_plugin_get_num_ports(plugin);
  if (!uri || n > SCAN_MAX_PORTS)
    return false;
  float *ranges = n ? calloc((size_t)n * 3, sizeof(float)) : NULL;
  const char **ins = n ? calloc(n, sizeof(char *)) : NULL;
  const char **outs = n ? calloc(n, sizeof(char *)) : NULL;
  char(*fallbacks)[16] = n ? calloc(n, sizeof(*fallbacks)) : NULL;
  if (n && (!ranges || !ins || !outs || !fallbacks)) {
    free(ranges);
    free(ins);
    free(outs);
    free(fallbacks);
    return false;
  }
  if (n)
    lilv_plugin_get_port_ranges_float(plugin, ranges, ranges + n,
                                      ranges + 2 * n);

  printf("%s{\"uri\":", first ? "" : ",");
  json_text(uri);
  LilvNode *name = lilv_plugin_get_name(plugin);
  fputs(",\"name\":", stdout);
  json_text(name ? lilv_node_as_string(name) : NULL);
  lilv_node_free(name);
  const LilvPluginClass *cls = lilv_plugin_get_class(plugin);
  const LilvNode *label = cls ? lilv_plugin_class_get_label(cls) : NULL;
  fputs(",\"class\":", stdout);
  json_text(label ? lilv_node_as_string(label) : NULL);
  printf(",\"latency\":%s", lilv_plugin_has_latency(plugin) ? "true" : "false");

  // Audio ports are counted whole; their symbols stop at the control limit.
  unsigned audio_ins = 0, audio_outs = 0, in_count = 0, out_count = 0;
  unsigned controls = 0;
  fputs(",\"params\":[", stdout);
  for (uint32_t i = 0; i < n; ++i) {
    const LilvPort *port = lilv_plugin_get_port_by_index(plugin, i);
    if (!port)
      continue;
    bool input = lilv_port_is_a(plugin, port, k->input);
    const LilvNode *symbol_node = lilv_port_get_symbol(plugin, port);
    const char *symbol = symbol_node ? lilv_node_as_string(symbol_node) : NULL;
    if (!symbol) {
      snprintf(fallbacks[i], sizeof(fallbacks[i]), "port%u", i);
      symbol = fallbacks[i];
    }
    if (lilv_port_is_a(plugin, port, k->audio)) {
      if (input) {
        ++audio_ins;
        if (in_count < SCAN_MAX_CONTROLS)
          ins[in_count++] = symbol;
      } else if (lilv_port_is_a(plugin, port, k->output)) {
        ++audio_outs;
        if (out_count < SCAN_MAX_CONTROLS)
          outs[out_count++] = symbol;
      }
      continue;
    }
    if (!input || !lilv_port_is_a(plugin, port, k->control))
      continue;
    if (lilv_port_has_property(plugin, port, k->not_on_gui))
      continue;
    if (controls >= SCAN_MAX_CONTROLS)
      continue;
    // A missing bound reads as NaN: the defaults the daemon always used.
    // Infinite metadata cannot travel as a JSON number, and a range the
    // wrong way round is no range, so either drops the control.
    float min = isnan(ranges[i]) ? 0 : ranges[i];
    float max = isnan(ranges[i + n]) ? 1 : ranges[i + n];
    float def = isnan(ranges[i + 2 * n]) ? min : ranges[i + 2 * n];
    if (!isfinite(min) || !isfinite(max) || !isfinite(def) || min > max)
      continue;
    def = fminf(max, fmaxf(min, def));
    printf("%s{\"symbol\":", controls++ ? "," : "");
    json_text(symbol);
    LilvNode *port_name = lilv_port_get_name(plugin, port);
    fputs(",\"name\":", stdout);
    json_text(port_name ? lilv_node_as_string(port_name) : NULL);
    lilv_node_free(port_name);
    fputs(",\"min\":", stdout);
    json_float(min);
    fputs(",\"max\":", stdout);
    json_float(max);
    fputs(",\"default\":", stdout);
    json_float(def);
    printf(",\"toggled\":%s,\"integer\":%s,\"logarithmic\":%s,\"enum\":%s",
           lilv_port_has_property(plugin, port, k->toggled) ? "true" : "false",
           lilv_port_has_property(plugin, port, k->integer) ? "true" : "false",
           lilv_port_has_property(plugin, port, k->logarithmic) ? "true" : "false",
           lilv_port_has_property(plugin, port, k->enumeration) ? "true" : "false");
    fputs(",\"points\":", stdout);
    json_points(plugin, port);
    putchar('}');
  }
  printf("],\"audioIns\":%u,\"audioOuts\":%u,\"inputs\":", audio_ins,
         audio_outs);
  json_symbols(ins, in_count);
  fputs(",\"outputs\":", stdout);
  json_symbols(outs, out_count);

  fputs(",\"features\":", stdout);
  LilvNodes *required = lilv_plugin_get_required_features(plugin);
  json_features(required);
  lilv_nodes_free(required);

  // The X11 UI the native host would open: the first one, in lilv's order.
  LilvUIs *uis = lilv_plugin_get_uis(plugin);
  LILV_FOREACH(uis, i, uis) {
    const LilvUI *ui = lilv_uis_get(uis, i);
    if (!lilv_ui_is_a(ui, k->x11))
      continue;
    LilvNodes *ui_required =
        lilv_world_find_nodes(k->world, lilv_ui_get_uri(ui), k->required, NULL);
    fputs(",\"ui\":{\"features\":", stdout);
    json_features(ui_required);
    fputs("}", stdout);
    lilv_nodes_free(ui_required);
    break;
  }
  lilv_uis_free(uis);
  putchar('}');
  free(ranges);
  free(ins);
  free(outs);
  free(fallbacks);
  return true;
}

// A directory of LV2_PATH as lilv reads it: `$NAME` is the variable's value
// (kept as written when it is unset) and `~` before a slash or the end is
// the home folder.
static char *expand(const char *s) {
  char *out = NULL;
  size_t size = 0;
  FILE *f = open_memstream(&out, &size);
  if (!f)
    return NULL;
  for (size_t i = 0; s[i];) {
    size_t name = i + 1, end = name;
    while ((s[end] >= '0' && s[end] <= '9') || (s[end] >= 'A' && s[end] <= 'Z') ||
           s[end] == '_')
      ++end;
    if (s[i] == '$' && end > name) {
      char *variable = strndup(s + name, end - name);
      const char *value = variable ? getenv(variable) : NULL;
      if (value)
        fputs(value, f);
      else
        fwrite(s + i, 1, end - i, f);
      free(variable);
      i = end;
    } else if (s[i] == '~' && (s[i + 1] == '/' || s[i + 1] == ':' || !s[i + 1])) {
      const char *home = getenv("HOME");
      fputs(home ? home : "$HOME", f);
      ++i;
    } else
      fputc(s[i++], f);
  }
  if (fclose(f) != 0) {
    free(out);
    return NULL;
  }
  return out;
}

// One directory of the path: every directory inside it is a bundle, in the
// order the directory lists them, as lilv loads them.
static void load_directory(LilvWorld *world, const char *entry,
                           const Skips *skips, unsigned *index) {
  char *dir = expand(entry);
  struct stat st;
  DIR *listing = dir && !stat(dir, &st) && S_ISDIR(st.st_mode) ? opendir(dir) : NULL;
  if (!listing) {
    free(dir);
    return;
  }
  size_t length = strlen(dir);
  const char *separator = length && dir[length - 1] == '/' ? "" : "/";
  for (struct dirent *e; (e = readdir(listing));) {
    if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, ".."))
      continue;
    size_t size = length + strlen(e->d_name) + 3;
    char *path = malloc(size);
    if (!path)
      continue;
    snprintf(path, size, "%s%s%s/", dir, separator, e->d_name);
    if (stat(path, &st) || !S_ISDIR(st.st_mode)) {
      free(path);
      continue;
    }
    LilvNode *bundle = lilv_new_file_uri(world, NULL, path);
    free(path);
    if (!bundle)
      continue;
    char text[SCAN_MAX_MARKER + 1];
    bundle_text(bundle, text);
    if (skipped(skips, text))
      fprintf(stderr, "trace: scan LV2: skip bundle %s\n", text);
    else {
      trace_bundle("manifest", (*index)++, bundle);
      lilv_world_load_bundle(world, bundle);
    }
    lilv_node_free(bundle);
  }
  closedir(listing);
  free(dir);
}

// lilv_world_load_all, one bundle at a time: the same directories in the
// same order, a path split on colons the way lilv splits it, then the
// specifications and plugin classes it reads after the bundles.
static void load_path(LilvWorld *world, const char *lv2_path, const Skips *skips) {
  unsigned index = 0;
  while (*lv2_path) {
    const char *colon = strchr(lv2_path, ':');
    size_t length = colon ? (size_t)(colon - lv2_path) : 0;
    if (!length) {
      load_directory(world, lv2_path, skips, &index);
      break;
    }
    char *dir = strndup(lv2_path, length);
    if (dir)
      load_directory(world, dir, skips, &index);
    free(dir);
    lv2_path += length + 1;
  }
  fputs("trace: scan LV2: specifications and plugin classes\n", stderr);
  lilv_world_load_specifications(world);
  lilv_world_load_plugin_classes(world);
}

// Whether a readable segment of a loaded library holds this string,
// terminator and all.
static bool image_holds(const struct dl_phdr_info *info, const char *text) {
  size_t length = strlen(text) + 1;
  for (int i = 0; i < info->dlpi_phnum; ++i) {
    const ElfW(Phdr) *segment = &info->dlpi_phdr[i];
    if (segment->p_type == PT_LOAD && (segment->p_flags & PF_R) &&
        memmem((const void *)(info->dlpi_addr + segment->p_vaddr),
               segment->p_filesz, text, length))
      return true;
  }
  return false;
}

typedef struct {
  char path[4096];
  bool found;
} DefaultPath;

// lilv's own default LV2_PATH. lilv has no call that returns it, and it is
// what every other LV2 host on the machine reads, so the walk has to read
// the same directories. Its build writes it as `~/.lv2:/usr/local/LIBDIR/lv2:
// /usr/LIBDIR/lv2` unless the distribution chose another; each library
// directory lilv itself could have been installed under is tried, and one
// counts only when that exact text is in lilv's loaded image. A default
// written any other way is not found, and the scan leaves loading to lilv.
static int find_default(struct dl_phdr_info *info, size_t size, void *data) {
  DefaultPath *out = data;
  const char *name = info->dlpi_name;
  const char *slash = name ? strrchr(name, '/') : NULL;
  if (!slash || strncmp(slash + 1, "liblilv-0.so", 12))
    return 0;
  char dir[2048];
  size_t length = (size_t)(slash - name);
  if (!length || length >= sizeof(dir))
    return 1;
  memcpy(dir, name, length);
  dir[length] = '\0';
  const char *libdirs[5];
  int count = 0;
  if (!strncmp(dir, "/usr/", 5))
    libdirs[count++] = dir + 5;
  const char *last = strrchr(dir, '/');
  if (last && last[1])
    libdirs[count++] = last + 1;
  if (last && last > dir) {
    const char *before = last - 1;
    while (before > dir && *before != '/')
      --before;
    if (*before == '/' && before + 1 < last)
      libdirs[count++] = before + 1;
  }
  libdirs[count++] = "lib";
  libdirs[count++] = "lib64";
  for (int i = 0; i < count && !out->found; ++i) {
    int n = snprintf(out->path, sizeof(out->path), "~/.lv2:/usr/local/%s/lv2:/usr/%s/lv2",
                     libdirs[i], libdirs[i]);
    out->found = n > 0 && (size_t)n < sizeof(out->path) && image_holds(info, out->path);
  }
  return 1;
}

// Without a walk, lilv loads every bundle in one step and only the plugin
// markers name one; a skipped bundle is unloaded again before its plugins
// are described.
static void unload_skipped(LilvWorld *world, const Skips *skips) {
  for (bool again = skips->count > 0; again;) {
    again = false;
    const LilvPlugins *plugins = lilv_world_get_all_plugins(world);
    LILV_FOREACH(plugins, i, plugins) {
      const LilvNode *bundle = lilv_plugin_get_bundle_uri(lilv_plugins_get(plugins, i));
      char text[SCAN_MAX_MARKER + 1];
      bundle_text(bundle, text);
      if (!skipped(skips, text))
        continue;
      fprintf(stderr, "trace: scan LV2: skip bundle %s\n", text);
      LilvNode *copy = lilv_node_duplicate(bundle);
      lilv_world_unload_bundle(world, copy);
      lilv_node_free(copy);
      again = true;
      break;
    }
  }
}

// `scan-lv2 [--skip BUNDLE]... [LV2_PATH]`: every plugin lilv finds on the
// path given, or on LV2_PATH and lilv's default as any LV2 host reads them,
// less the bundles skipped, each named by its marker text. Phase markers go
// to standard error before each step, so the log of a scan that crashed or
// stalled names the bundle it was reading: each bundle's manifest first,
// then each plugin's own data files when the plugin is described.
int lv2_scan(int argc, char **argv) {
  int options = 0;
  while (options + 1 < argc && !strcmp(argv[options], "--skip"))
    options += 2;
  if (argc - options > 1 || (options < argc && !strcmp(argv[options], "--skip"))) {
    fputs("usage: openxlr-lv2-host scan-lv2 [--skip BUNDLE]... [LV2_PATH]\n", stderr);
    return 2;
  }
  const Skips skips = {argv, options};
  const char *lv2_path = options < argc ? argv[options] : getenv("LV2_PATH");
  DefaultPath fallback = {.found = false};
  if (!lv2_path) {
    dl_iterate_phdr(find_default, &fallback);
    if (fallback.found)
      lv2_path = fallback.path;
  }

  fputs("trace: scan LV2: load bundles\n", stderr);
  LilvWorld *world = lilv_world_new();
  if (!world)
    return 1;
  if (lv2_path)
    load_path(world, lv2_path, &skips);
  else {
    lilv_world_load_all(world);
    unload_skipped(world, &skips);
  }
  Classes k = {
      .world = world,
      .control = lilv_new_uri(world, LV2_CORE__ControlPort),
      .audio = lilv_new_uri(world, LV2_CORE__AudioPort),
      .input = lilv_new_uri(world, LV2_CORE__InputPort),
      .output = lilv_new_uri(world, LV2_CORE__OutputPort),
      .toggled = lilv_new_uri(world, LV2_CORE__toggled),
      .integer = lilv_new_uri(world, LV2_CORE__integer),
      .enumeration = lilv_new_uri(world, LV2_CORE__enumeration),
      .logarithmic = lilv_new_uri(world, LV2_PORT_PROPS__logarithmic),
      .not_on_gui = lilv_new_uri(world, LV2_PORT_PROPS__notOnGUI),
      .x11 = lilv_new_uri(world, LV2_UI__X11UI),
      .required = lilv_new_uri(world, LV2_CORE__requiredFeature),
  };
  fputs("{\"plugins\":[", stdout);
  const LilvPlugins *plugins = lilv_world_get_all_plugins(world);
  unsigned index = 0;
  bool first = true;
  LILV_FOREACH(plugins, i, plugins) {
    const LilvPlugin *plugin = lilv_plugins_get(plugins, i);
    trace_bundle("plugin", index++, lilv_plugin_get_bundle_uri(plugin));
    if (describe(plugin, &k, first))
      first = false;
  }
  // Past the last plugin, so a failure from here on names no bundle.
  fputs("trace: scan LV2: finish\n", stderr);
  puts("]}");
  LilvNode *nodes[] = {k.control, k.audio, k.input, k.output, k.toggled,
                       k.integer, k.enumeration, k.logarithmic, k.not_on_gui,
                       k.x11, k.required};
  for (size_t i = 0; i < sizeof(nodes) / sizeof(nodes[0]); ++i)
    lilv_node_free(nodes[i]);
  lilv_world_free(world);
  if (fflush(stdout) != 0 || ferror(stdout))
    return 1;
  fputs("trace: scan LV2: complete\n", stderr);
  return 0;
}
