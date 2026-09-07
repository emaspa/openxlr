// SPDX-License-Identifier: GPL-3.0-only
// One isolated LV2 instance, its PipeWire ports and its optional X11 UI.
// The audio callback never allocates, logs, performs IPC, or calls the UI.
#define _POSIX_C_SOURCE 200809L
#include <X11/Xatom.h>
#include <X11/Xlib.h>
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <lilv/lilv.h>
#include <lv2/atom/atom.h>
#include <lv2/atom/util.h>
#include <lv2/instance-access/instance-access.h>
#include <lv2/ui/ui.h>
#include <lv2/urid/urid.h>
#include <math.h>
#include <pipewire/filter.h>
#include <pipewire/pipewire.h>
#include <pthread.h>
#include <signal.h>
#include <stdatomic.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <poll.h>
#include <time.h>
#include <unistd.h>

enum {
  MAX_PORTS = 2048,
  MAX_FRAMES = 8192,
  ATOM_CAPACITY = 65536,
  MAX_URIS = 4096
};
typedef struct {
  bool input, audio, control, atom;
  const char *symbol;
  float value, minimum, maximum, reported;
  _Atomic float desired, observed;
  void *pw_port;
  float *samples;
  float *dsp_buffer; // borrowed for one process callback; dequeue exactly once
  LV2_Atom_Sequence *sequence;
} Port;

typedef struct {
  LilvWorld *world;
  const LilvPlugin *plugin;
  LilvInstance *instance;
  Port *ports;
  uint32_t count, rate;
  const char *node_name;
  struct pw_main_loop *loop;
  struct pw_filter *filter;
  struct spa_source *timer;
  _Atomic bool audio_error;
  _Atomic uint64_t completed_cycles;
  unsigned heartbeat_ticks;
  _Atomic bool streaming, monitor_stop;
  char *uris[MAX_URIS];
  _Atomic uint32_t uri_count;
  pthread_t main_thread;
  LV2_URID_Map map;
  LV2_URID_Unmap unmap;
  LV2_URID sequence_type;
  Display *display;
  Window window;
  Atom close_message;
  void *ui_library;
  const LV2UI_Descriptor *ui_descriptor;
  const LV2UI_Idle_Interface *idle;
  LV2UI_Handle ui;
  LV2UI_Resize resize;
  char input[16384];
  size_t input_size;
  int exit_code;
} Host;

// LV2 calls URI mapping during instantiation; these IDs remain stable until
// instance destruction. UIs use instance-access, not a second DSP instance.
static LV2_URID map_uri(LV2_URID_Map_Handle handle, const char *uri) {
  Host *h = handle;
  uint32_t count = atomic_load(&h->uri_count);
  for (uint32_t i = 0; i < count; ++i)
    if (!strcmp(h->uris[i], uri))
      return i + 1;
  if (count == MAX_URIS || !pthread_equal(pthread_self(), h->main_thread))
    return 0;
  char *copy = strdup(uri);
  if (!copy)
    return 0;
  h->uris[count] = copy;
  atomic_store(&h->uri_count, count + 1);
  return count + 1;
}

static const char *unmap_uri(LV2_URID_Unmap_Handle handle, LV2_URID id) {
  Host *h = handle;
  return id && id <= h->uri_count ? h->uris[id - 1] : NULL;
}

static bool required_features_supported(const LilvNodes *required, bool ui) {
  if (!required)
    return true;
  LILV_FOREACH(nodes, i, required) {
    const char *uri = lilv_node_as_uri(lilv_nodes_get(required, i));
    if (!uri)
      return false;
    if (!strcmp(uri, LV2_URID__map) || !strcmp(uri, LV2_URID__unmap))
      continue;
    if (ui && (!strcmp(uri, LV2_INSTANCE_ACCESS_URI) ||
               !strcmp(uri, LV2_UI__parent) || !strcmp(uri, LV2_UI__resize) ||
               !strcmp(uri, LV2_UI__idleInterface)))
      continue;
    fprintf(stderr, "unsupported required LV2 feature: %s\n", uri);
    return false;
  }
  return true;
}

static void process_audio(void *data, struct spa_io_position *position) {
  Host *h = data;
  uint32_t frames = position->clock.duration;
  if (frames > MAX_FRAMES || position->clock.rate.denom != h->rate) {
    atomic_store(&h->audio_error, true);
    for (uint32_t i = 0; i < h->count; ++i) {
      Port *p = &h->ports[i];
      if (p->audio && !p->input && p->pw_port) {
        float *out = pw_filter_get_dsp_buffer(p->pw_port, frames);
        if (out)
          memset(out, 0, sizeof(float) * frames);
      }
    }
    return;
  }
  for (uint32_t i = 0; i < h->count; ++i) {
    Port *p = &h->ports[i];
    if (p->audio) {
      float *buffer =
          p->pw_port ? pw_filter_get_dsp_buffer(p->pw_port, frames) : NULL;
      p->dsp_buffer = buffer;
      if (p->input) {
        if (buffer)
          memcpy(p->samples, buffer, frames * sizeof(float));
        else
          memset(p->samples, 0, frames * sizeof(float));
      }
    } else if (p->control && p->input)
      p->value = atomic_load(&p->desired);
    else if (p->atom) {
      p->sequence->atom.type = h->sequence_type;
      p->sequence->atom.size = p->input ? sizeof(LV2_Atom_Sequence_Body)
                                        : ATOM_CAPACITY - sizeof(LV2_Atom);
      p->sequence->body.unit = 0;
      p->sequence->body.pad = 0;
    }
  }
  lilv_instance_run(h->instance, frames);
  for (uint32_t i = 0; i < h->count; ++i) {
    Port *p = &h->ports[i];
    if (p->audio && !p->input && p->pw_port) {
      float *buffer = p->dsp_buffer;
      if (buffer)
        memcpy(buffer, p->samples, frames * sizeof(float));
    } else if (p->control)
      atomic_store(&p->observed, p->value);
  }
  atomic_fetch_add(&h->completed_cycles, 1);
}

static void state_changed(void *data, enum pw_filter_state old,
                          enum pw_filter_state state, const char *error) {
  Host *h = data;
  h->streaming = state == PW_FILTER_STATE_STREAMING;
  if (state == PW_FILTER_STATE_ERROR) {
    fprintf(stderr, "PipeWire: %s\n", error ? error : "disconnected");
    h->exit_code = 1;
    pw_main_loop_quit(h->loop);
  } else if (state == PW_FILTER_STATE_PAUSED)
    puts("ready");
}

static const struct pw_filter_events filter_events = {
    PW_VERSION_FILTER_EVENTS, .state_changed = state_changed,
    .process = process_audio};

static void close_ui(Host *h) {
  if (h->ui)
    h->ui_descriptor->cleanup(h->ui);
  h->ui = NULL;
  h->idle = NULL;
  if (h->display) {
    if (h->window)
      XDestroyWindow(h->display, h->window);
    XCloseDisplay(h->display);
  }
  h->display = NULL;
  h->window = 0;
  // Some plugin toolkits retain process-global resources: keep their library
  // resident until process exit, as permitted by LV2 ui:makeSONameResident.
}

static void ui_write(LV2UI_Controller controller, uint32_t index, uint32_t size,
                     uint32_t format, const void *buffer) {
  Host *h = controller;
  if (index >= h->count || format != 0 || size != sizeof(float))
    return;
  Port *p = &h->ports[index];
  float value = *(const float *)buffer;
  if (!p->input || !p->control || !isfinite(value))
    return;
  value = fminf(p->maximum, fmaxf(p->minimum, value));
  atomic_store(&p->desired, value);
  printf("control %s %.9g\n", p->symbol, value);
}

static int ui_resize(LV2UI_Feature_Handle handle, int width, int height) {
  Host *h = handle;
  if (!h->display || !h->window || width < 1 || height < 1 || width > 16384 ||
      height > 16384)
    return 1;
  XResizeWindow(h->display, h->window, (unsigned)width, (unsigned)height);
  return 0;
}

static bool open_ui(Host *h) {
  if (h->ui) {
    XMapRaised(h->display, h->window);
    return true;
  }
  LilvUIs *uis = lilv_plugin_get_uis(h->plugin);
  LilvNode *x11 = lilv_new_uri(h->world, LV2_UI__X11UI);
  const LilvUI *selected = NULL;
  LILV_FOREACH(uis, i, uis) {
    const LilvUI *candidate = lilv_uis_get(uis, i);
    if (lilv_ui_is_a(candidate, x11)) {
      selected = candidate;
      break;
    }
  }
  lilv_node_free(x11);
  if (!selected) {
    lilv_uis_free(uis);
    return false;
  }
  LilvNode *required_predicate =
      lilv_new_uri(h->world, LV2_CORE__requiredFeature);
  LilvNodes *required = lilv_world_find_nodes(
      h->world, lilv_ui_get_uri(selected), required_predicate, NULL);
  bool supported = required_features_supported(required, true);
  lilv_nodes_free(required);
  lilv_node_free(required_predicate);
  if (!supported) {
    lilv_uis_free(uis);
    return false;
  }
  char *binary = lilv_file_uri_parse(
      lilv_node_as_uri(lilv_ui_get_binary_uri(selected)), NULL);
  char *bundle = lilv_file_uri_parse(
      lilv_node_as_uri(lilv_ui_get_bundle_uri(selected)), NULL);
  if (!h->ui_library)
    h->ui_library = dlopen(binary, RTLD_NOW | RTLD_LOCAL);
  const LV2UI_Descriptor *(*descriptor)(uint32_t) =
      h->ui_library ? dlsym(h->ui_library, "lv2ui_descriptor") : NULL;
  h->ui_descriptor = NULL;
  if (descriptor)
    for (uint32_t i = 0;; ++i) {
      const LV2UI_Descriptor *d = descriptor(i);
      if (!d)
        break;
      if (!strcmp(d->URI, lilv_node_as_uri(lilv_ui_get_uri(selected)))) {
        h->ui_descriptor = d;
        break;
      }
    }
  h->display = h->ui_descriptor ? XOpenDisplay(NULL) : NULL;
  if (h->display) {
    h->window = XCreateSimpleWindow(h->display, DefaultRootWindow(h->display),
                                    0, 0, 900, 600, 0, 0, 0x16181d);
    XStoreName(h->display, h->window, "OpenXLR - Native LV2 controls");
    XChangeProperty(h->display, h->window,
                    XInternAtom(h->display, "_OPENXLR_NODE", False), XA_STRING,
                    8, PropModeReplace, (const unsigned char *)h->node_name,
                    (int)strlen(h->node_name));
    h->close_message = XInternAtom(h->display, "WM_DELETE_WINDOW", False);
    XSetWMProtocols(h->display, h->window, &h->close_message, 1);
    h->resize = (LV2UI_Resize){h, ui_resize};
    LV2_Feature parent = {LV2_UI__parent, (void *)(uintptr_t)h->window};
    LV2_Feature map = {LV2_URID__map, &h->map},
                unmap = {LV2_URID__unmap, &h->unmap};
    LV2_Feature access = {LV2_INSTANCE_ACCESS_URI,
                          lilv_instance_get_handle(h->instance)};
    LV2_Feature size = {LV2_UI__resize, &h->resize};
    LV2_Feature idle = {LV2_UI__idleInterface, NULL};
    const LV2_Feature *features[] = {&parent, &map,  &unmap, &access,
                                     &size,   &idle, NULL};
    LV2UI_Widget widget = NULL;
    h->ui = h->ui_descriptor->instantiate(
        h->ui_descriptor, lilv_node_as_uri(lilv_plugin_get_uri(h->plugin)),
        bundle, ui_write, h, &widget, features);
    if (h->ui) {
      h->idle = h->ui_descriptor->extension_data
                    ? h->ui_descriptor->extension_data(LV2_UI__idleInterface)
                    : NULL;
      XMapRaised(h->display, h->window);
      XFlush(h->display);
    }
  }
  lilv_free(binary);
  lilv_free(bundle);
  lilv_uis_free(uis);
  if (!h->ui)
    close_ui(h);
  return h->ui != NULL;
}

static bool set_control(Host *h, const char *symbol, float value) {
  if (!isfinite(value))
    return false;
  for (uint32_t i = 0; i < h->count; ++i) {
    Port *p = &h->ports[i];
    if (p->input && p->control && !strcmp(p->symbol, symbol)) {
      atomic_store(&p->desired, fminf(p->maximum, fmaxf(p->minimum, value)));
      return true;
    }
  }
  return false;
}

static void command(Host *h, char *line) {
  char symbol[256], extra;
  float value;
  if (!strcmp(line, "show"))
    puts(open_ui(h)
             ? "ui opened"
             : "ui unavailable: X11 LV2 UI or display could not be opened");
  else if (!strcmp(line, "hide"))
    close_ui(h);
  else if (!strcmp(line, "quit"))
    pw_main_loop_quit(h->loop);
  else if (sscanf(line, "set %255s %f %c", symbol, &value, &extra) == 2) {
    if (!set_control(h, symbol, value))
      fprintf(stderr, "invalid control: %s\n", symbol);
  }
}

static void read_commands(void *data, int fd, uint32_t mask) {
  Host *h = data;
  ssize_t count =
      read(fd, h->input + h->input_size, sizeof(h->input) - h->input_size - 1);
  if (count == 0 || (count < 0 && errno != EAGAIN)) {
    pw_main_loop_quit(h->loop);
    return;
  }
  if (count < 0)
    return;
  h->input_size += (size_t)count;
  h->input[h->input_size] = 0;
  char *start = h->input, *end;
  while ((end = strchr(start, '\n'))) {
    *end = 0;
    command(h, start);
    start = end + 1;
  }
  h->input_size -= (size_t)(start - h->input);
  memmove(h->input, start, h->input_size);
  if (h->input_size == sizeof(h->input) - 1) {
    h->exit_code = 1;
    pw_main_loop_quit(h->loop);
  }
}

static void tick(void *data, uint64_t expirations) {
  Host *h = data;
  // Editor progress is separate from audio progress. A blocked editor must
  // not cause the supervisor to tear down a working audio instance.
  if (++h->heartbeat_ticks == 30) {
    puts("ui-heartbeat");
    h->heartbeat_ticks = 0;
  }
  if (atomic_load(&h->audio_error)) {
    fputs(
        "unsupported audio quantum or sample-rate change; restart the chain\n",
        stderr);
    h->exit_code = 1;
    pw_main_loop_quit(h->loop);
    return;
  }
  if (h->ui) {
    while (XPending(h->display)) {
      XEvent event;
      XNextEvent(h->display, &event);
      if (event.type == ClientMessage &&
          (Atom)event.xclient.data.l[0] == h->close_message) {
        close_ui(h);
        break;
      }
    }
  }
  for (uint32_t i = 0; i < h->count; ++i) {
    Port *p = &h->ports[i];
    if (!p->control)
      continue;
    float value =
        p->input ? atomic_load(&p->desired) : atomic_load(&p->observed);
    if (h->ui && h->ui_descriptor->port_event)
      h->ui_descriptor->port_event(h->ui, i, sizeof(float), 0, &value);
    if (!p->input && isfinite(value) && value != p->reported) {
      printf("meter %s %.9g\n", p->symbol, value);
      p->reported = value;
    }
  }
  if (h->ui && h->idle && h->idle->idle(h->ui))
    close_ui(h);
}

static void stop(void *data, int signal) {
  pw_main_loop_quit(((Host *)data)->loop);
}

static void *monitor_audio(void *data) {
  Host *h = data;
  uint64_t last_cycles = 0;
  while (!atomic_load(&h->monitor_stop)) {
    // The redirected input pipe belongs to the daemon process, unlike
    // PDEATHSIG, which follows the short-lived .NET thread that spawned us.
    // Observe HUP without consuming commands; also works with a stuck UI.
    struct pollfd input = {STDIN_FILENO, POLLHUP | POLLERR, 0};
    if (poll(&input, 1, 1000) > 0 && (input.revents & (POLLHUP | POLLERR)))
      _exit(0);
    uint64_t cycles = atomic_load(&h->completed_cycles);
    if (!atomic_load(&h->streaming) || cycles != last_cycles)
      puts("heartbeat");
    last_cycles = cycles;
  }
  return NULL;
}

int main(int argc, char **argv) {
  if (argc < 5) {
    fputs("usage: openxlr-lv2-host URI NODE CHANNELS RATE [SYMBOL=VALUE ...]\n",
          stderr);
    return 2;
  }
  int channels = atoi(argv[3]);
  Host h = {.rate = (uint32_t)strtoul(argv[4], NULL, 10),
            .node_name = argv[2],
            .main_thread = pthread_self()};
  if (channels < 1 || channels > 2 || h.rate < 8000 || h.rate > 384000)
    return 2;
  setvbuf(stdout, NULL, _IOLBF, 0);
  h.world = lilv_world_new();
  if (!h.world)
    return 1;
  lilv_world_load_all(h.world);
  LilvNode *uri = lilv_new_uri(h.world, argv[1]);
  h.plugin = lilv_plugins_get_by_uri(lilv_world_get_all_plugins(h.world), uri);
  lilv_node_free(uri);
  if (!h.plugin) {
    fputs("plugin is not installed\n", stderr);
    lilv_world_free(h.world);
    return 1;
  }
  LilvNodes *required = lilv_plugin_get_required_features(h.plugin);
  bool supported = required_features_supported(required, false);
  lilv_nodes_free(required);
  if (!supported) {
    lilv_world_free(h.world);
    return 1;
  }
  h.count = lilv_plugin_get_num_ports(h.plugin);
  if (!h.count || h.count > MAX_PORTS) {
    lilv_world_free(h.world);
    return 1;
  }
  h.map = (LV2_URID_Map){&h, map_uri};
  h.unmap = (LV2_URID_Unmap){&h, unmap_uri};
  LV2_Feature map = {LV2_URID__map, &h.map},
              unmap = {LV2_URID__unmap, &h.unmap};
  const LV2_Feature *features[] = {&map, &unmap, NULL};
  h.sequence_type = map_uri(&h, LV2_ATOM__Sequence);
  h.instance = lilv_plugin_instantiate(h.plugin, h.rate, features);
  if (!h.instance) {
    fputs("LV2 instantiation failed\n", stderr);
    h.exit_code = 1;
    goto cleanup;
  }
  h.ports = calloc(h.count, sizeof(Port));
  float *ranges = calloc(h.count * 3, sizeof(float));
  if (!h.ports || !ranges) {
    free(ranges);
    h.exit_code = 1;
    goto cleanup;
  }
  lilv_plugin_get_port_ranges_float(h.plugin, ranges, ranges + h.count,
                                    ranges + 2 * h.count);
  LilvNode *input = lilv_new_uri(h.world, LV2_CORE__InputPort),
           *audio = lilv_new_uri(h.world, LV2_CORE__AudioPort);
  LilvNode *control = lilv_new_uri(h.world, LV2_CORE__ControlPort),
           *atom = lilv_new_uri(h.world, LV2_ATOM__AtomPort);
  LilvNode *optional = lilv_new_uri(h.world, LV2_CORE__connectionOptional);
  pw_init(NULL, NULL);
  h.loop = pw_main_loop_new(NULL);
  if (!h.loop) {
    h.exit_code = 1;
    goto ports_done;
  }
  char rate[32];
  snprintf(rate, sizeof(rate), "1/%u", h.rate);
  h.filter = pw_filter_new_simple(
      pw_main_loop_get_loop(h.loop), argv[2],
      pw_properties_new(PW_KEY_NODE_NAME, argv[2], PW_KEY_NODE_DESCRIPTION,
                        "OpenXLR LV2", PW_KEY_MEDIA_TYPE, "Audio",
                        PW_KEY_MEDIA_CATEGORY, "Filter", PW_KEY_MEDIA_ROLE,
                        "DSP", PW_KEY_NODE_RATE, rate, "node.lock-rate", "true",
                        "node.autoconnect", "false", NULL),
      &filter_events, &h);
  if (!h.filter) {
    h.exit_code = 1;
    goto ports_done;
  }
  unsigned ins = 0, outs = 0;
  for (uint32_t i = 0; i < h.count; ++i) {
    Port *p = &h.ports[i];
    const LilvPort *port = lilv_plugin_get_port_by_index(h.plugin, i);
    p->symbol = lilv_node_as_string(lilv_port_get_symbol(h.plugin, port));
    p->input = lilv_port_is_a(h.plugin, port, input);
    p->audio = lilv_port_is_a(h.plugin, port, audio);
    p->control = lilv_port_is_a(h.plugin, port, control);
    p->atom = lilv_port_is_a(h.plugin, port, atom);
    if (p->audio) {
      p->samples = calloc(MAX_FRAMES, sizeof(float));
      if (!p->samples) {
        h.exit_code = 1;
        break;
      }
      lilv_instance_connect_port(h.instance, i, p->samples);
      unsigned index = p->input ? ins++ : outs++;
      if (index < (unsigned)channels) {
        char name[32];
        snprintf(name, sizeof(name), "%s_%u", p->input ? "playback" : "capture",
                 index);
        p->pw_port = pw_filter_add_port(
            h.filter, p->input ? PW_DIRECTION_INPUT : PW_DIRECTION_OUTPUT,
            PW_FILTER_PORT_FLAG_MAP_BUFFERS, 1,
            pw_properties_new(PW_KEY_FORMAT_DSP, "32 bit float mono audio",
                              PW_KEY_PORT_NAME, name, PW_KEY_AUDIO_CHANNEL,
                              channels == 1 ? "MONO"
                              : index == 0  ? "FL"
                                            : "FR",
                              NULL),
            NULL, 0);
        if (!p->pw_port) {
          h.exit_code = 1;
          break;
        }
      }
    } else if (p->control) {
      p->minimum = isfinite(ranges[i]) ? ranges[i] : -1e10f;
      p->maximum = isfinite(ranges[i + h.count]) ? ranges[i + h.count] : 1e10f;
      p->value =
          isfinite(ranges[i + 2 * h.count]) ? ranges[i + 2 * h.count] : 0;
      atomic_init(&p->desired, p->value);
      atomic_init(&p->observed, p->value);
      p->reported = NAN;
      lilv_instance_connect_port(h.instance, i, &p->value);
    } else if (p->atom) {
      p->sequence = calloc(1, ATOM_CAPACITY);
      if (!p->sequence) {
        h.exit_code = 1;
        break;
      }
      lilv_instance_connect_port(h.instance, i, p->sequence);
    } else if (lilv_port_has_property(h.plugin, port, optional))
      lilv_instance_connect_port(h.instance, i, NULL);
    else {
      fprintf(stderr, "unsupported required LV2 port: %s\n", p->symbol);
      h.exit_code = 1;
      break;
    }
  }
ports_done:
  lilv_node_free(input);
  lilv_node_free(audio);
  lilv_node_free(control);
  lilv_node_free(atom);
  lilv_node_free(optional);
  free(ranges);
  if (h.exit_code)
    goto cleanup;
  for (int i = 5; i < argc; ++i) {
    char *equals = strchr(argv[i], '=');
    if (!equals) {
      h.exit_code = 2;
      goto cleanup;
    }
    *equals = 0;
    char *end;
    float value = strtof(equals + 1, &end);
    if (*end || !set_control(&h, argv[i], value)) {
      h.exit_code = 2;
      goto cleanup;
    }
  }
  lilv_instance_activate(h.instance);
  if (pw_filter_connect(h.filter, PW_FILTER_FLAG_RT_PROCESS, NULL, 0) < 0) {
    h.exit_code = 1;
    goto deactivate;
  }
  fcntl(STDIN_FILENO, F_SETFL, O_NONBLOCK);
  struct pw_loop *loop = pw_main_loop_get_loop(h.loop);
  pw_loop_add_io(loop, STDIN_FILENO, SPA_IO_IN | SPA_IO_HUP, false,
                 read_commands, &h);
  h.timer = pw_loop_add_timer(loop, tick, &h);
  struct timespec interval = {0, 33333333};
  pw_loop_update_timer(loop, h.timer, &interval, &interval, false);
  pw_loop_add_signal(loop, SIGTERM, stop, &h);
  pw_loop_add_signal(loop, SIGINT, stop, &h);
  pthread_t monitor;
  if (pthread_create(&monitor, NULL, monitor_audio, &h)) {
    h.exit_code = 1;
    goto deactivate;
  }
  pw_main_loop_run(h.loop);
  atomic_store(&h.monitor_stop, true);
  pthread_join(monitor, NULL);
  pw_filter_disconnect(h.filter);
deactivate:
  lilv_instance_deactivate(h.instance);
cleanup:
  close_ui(&h);
  if (h.filter)
    pw_filter_destroy(h.filter);
  if (h.loop)
    pw_main_loop_destroy(h.loop);
  if (h.instance)
    lilv_instance_free(h.instance);
  if (h.ports)
    for (uint32_t i = 0; i < h.count; ++i) {
      free(h.ports[i].samples);
      free(h.ports[i].sequence);
    }
  free(h.ports);
  for (uint32_t i = 0; i < h.uri_count; ++i)
    free(h.uris[i]);
  lilv_world_free(h.world);
  return h.exit_code;
}
