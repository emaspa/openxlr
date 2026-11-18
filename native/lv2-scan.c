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
#include "host.h"

#include <lilv/lilv.h>
#include <lv2/core/lv2.h>
#include <lv2/port-props/port-props.h>
#include <lv2/ui/ui.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

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

// A bundle's location for a phase marker: printable ASCII and nothing else,
// so a hostile path cannot forge a marker line of its own.
static void trace_bundle(unsigned index, const LilvNode *bundle) {
  const char *uri = bundle ? lilv_node_as_uri(bundle) : NULL;
  char *path = uri ? lilv_file_uri_parse(uri, NULL) : NULL;
  const char *text = path ? path : uri ? uri : "";
  fprintf(stderr, "trace: scan LV2 plugin %u: bundle ", index);
  for (size_t i = 0; text[i] && i < SCAN_MAX_MARKER; ++i)
    fputc(text[i] >= 0x20 && text[i] < 0x7f ? text[i] : '?', stderr);
  fputc('\n', stderr);
  lilv_free(path);
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

// `scan-lv2 [LV2_PATH]`: every plugin lilv finds on the path given, or on
// LV2_PATH and lilv's default as any LV2 host reads them. Phase markers go
// to standard error before each step, so the log of a scan that crashed or
// stalled names the bundle it was reading: lilv reads every manifest
// first, then each plugin's own data files when the plugin is described.
int lv2_scan(const char *lv2_path) {
  fputs("trace: scan LV2: load bundles\n", stderr);
  LilvWorld *world = lilv_world_new();
  if (!world)
    return 1;
  if (lv2_path) {
    LilvNode *path = lilv_new_string(world, lv2_path);
    lilv_world_set_option(world, LILV_OPTION_LV2_PATH, path);
    lilv_node_free(path);
  }
  lilv_world_load_all(world);
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
    trace_bundle(index++, lilv_plugin_get_bundle_uri(plugin));
    if (describe(plugin, &k, first))
      first = false;
  }
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
