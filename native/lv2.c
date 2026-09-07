// SPDX-License-Identifier: GPL-3.0-only
// The LV2 backend: lilv for loading, the worker extension on a thread of its
// own, and the plugin's X11 editor on the instance that processes audio.
#include "host.h"

#include <dlfcn.h>
#include <lilv/lilv.h>
#include <lv2/atom/atom.h>
#include <lv2/atom/util.h>
#include <lv2/buf-size/buf-size.h>
#include <lv2/instance-access/instance-access.h>
#include <lv2/options/options.h>
#include <lv2/parameters/parameters.h>
#include <lv2/ui/ui.h>
#include <lv2/urid/urid.h>
#include <lv2/worker/worker.h>
#include <math.h>
#include <semaphore.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum {
  MAX_PORTS = 4096,
  ATOM_CAPACITY = 65536,
  MAX_URIS = 4096,
  // Worker traffic. The ring is a power of two so the counters can wrap.
  WORK_RING = 65536,
  WORK_FRAME_MAX = 8192,
  NO_CHANNEL = 0xffffffff
};

// One direction of worker traffic between the audio callback and the worker
// thread: one writer, one reader, no locks and no allocation. The counters
// count bytes ever written and read, so the difference is what is queued.
typedef struct {
  uint8_t data[WORK_RING];
  _Atomic uint32_t written, read;
} Ring;

static void ring_copy_in(Ring *r, uint32_t at, const void *src, uint32_t size) {
  uint32_t start = at & (WORK_RING - 1);
  uint32_t first = size < WORK_RING - start ? size : WORK_RING - start;
  memcpy(r->data + start, src, first);
  memcpy(r->data, (const uint8_t *)src + first, size - first);
}

static void ring_copy_out(const Ring *r, uint32_t at, void *dst, uint32_t size) {
  uint32_t start = at & (WORK_RING - 1);
  uint32_t first = size < WORK_RING - start ? size : WORK_RING - start;
  memcpy(dst, r->data + start, first);
  memcpy((uint8_t *)dst + first, r->data, size - first);
}

static bool ring_push(Ring *r, const void *data, uint32_t size) {
  uint32_t written = atomic_load_explicit(&r->written, memory_order_relaxed);
  uint32_t read = atomic_load_explicit(&r->read, memory_order_acquire);
  uint32_t need = size + (uint32_t)sizeof(uint32_t);
  if (need > WORK_RING - (written - read))
    return false;
  ring_copy_in(r, written, &size, sizeof(size));
  ring_copy_in(r, written + (uint32_t)sizeof(size), data, size);
  atomic_store_explicit(&r->written, written + need, memory_order_release);
  return true;
}

static bool ring_pop(Ring *r, void *dst, uint32_t *size_out) {
  uint32_t read = atomic_load_explicit(&r->read, memory_order_relaxed);
  uint32_t written = atomic_load_explicit(&r->written, memory_order_acquire);
  if (written - read < sizeof(uint32_t))
    return false;
  uint32_t size;
  ring_copy_out(r, read, &size, sizeof(size));
  if (size > WORK_FRAME_MAX || written - read < sizeof(size) + size)
    return false;
  ring_copy_out(r, read + (uint32_t)sizeof(size), dst, size);
  atomic_store_explicit(&r->read, read + (uint32_t)sizeof(size) + size,
                        memory_order_release);
  *size_out = size;
  return true;
}

typedef struct {
  bool input, audio, control, atom;
  const char *symbol;
  float value;                 // connected for a control port
  float *samples;              // connected for an audio port
  LV2_Atom_Sequence *sequence; // connected for an atom port
  Control *ctl;                // the core's record of a control port
  unsigned channel;            // which PipeWire channel an audio port carries
} Port;

typedef struct {
  Host *h;
  LilvWorld *world;
  const LilvPlugin *plugin;
  LilvInstance *instance;
  bool activated;
  Port *ports;
  uint32_t count;
  char *uris[MAX_URIS];
  _Atomic uint32_t uri_count;
  LV2_URID_Map map;
  LV2_URID_Unmap unmap;
  LV2_URID sequence_type;
  // Worker: the plugin hands the audio thread's work to a thread that may
  // block. Its answers come back before the next run.
  const LV2_Worker_Interface *worker;
  LV2_Worker_Schedule schedule;
  pthread_t worker_thread;
  sem_t work_ready;
  bool work_ready_valid, worker_started;
  _Atomic bool worker_stop;
  Ring requests, responses;
  uint8_t work_in[WORK_FRAME_MAX];   // worker thread only
  uint8_t work_out[WORK_FRAME_MAX];  // audio thread only
  // Options the plugin reads at instantiation. They outlive that call
  // because plugins are permitted to keep the pointer.
  int32_t min_block, max_block, sequence_size;
  float rate_hz;
  LV2_Options_Option options[5];
  // The editor
  void *ui_library;
  const LV2UI_Descriptor *ui_descriptor;
  const LV2UI_Idle_Interface *idle;
  LV2UI_Handle ui;
  LV2UI_Resize resize;
} Lv2;

// LV2 calls URI mapping during instantiation; these IDs remain stable until
// instance destruction. UIs use instance-access, not a second DSP instance.
static LV2_URID map_uri(LV2_URID_Map_Handle handle, const char *uri) {
  Lv2 *l = handle;
  uint32_t count = atomic_load(&l->uri_count);
  for (uint32_t i = 0; i < count; ++i)
    if (!strcmp(l->uris[i], uri))
      return i + 1;
  if (count == MAX_URIS || !host_on_main_thread(l->h))
    return 0;
  char *copy = strdup(uri);
  if (!copy)
    return 0;
  l->uris[count] = copy;
  atomic_store(&l->uri_count, count + 1);
  return count + 1;
}

static const char *unmap_uri(LV2_URID_Unmap_Handle handle, LV2_URID id) {
  Lv2 *l = handle;
  return id && id <= l->uri_count ? l->uris[id - 1] : NULL;
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
    if (!ui && (!strcmp(uri, LV2_WORKER__schedule) ||
                !strcmp(uri, LV2_OPTIONS__options) ||
                !strcmp(uri, LV2_BUF_SIZE__boundedBlockLength)))
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

// --- worker -----------------------------------------------------------------

// Called from the audio thread. Queue the work and wake the worker; never
// touch the plugin here.
static LV2_Worker_Status schedule_work(LV2_Worker_Schedule_Handle handle,
                                       uint32_t size, const void *data) {
  Lv2 *l = handle;
  if (size > WORK_FRAME_MAX || !ring_push(&l->requests, data, size))
    return LV2_WORKER_ERR_NO_SPACE;
  sem_post(&l->work_ready);
  return LV2_WORKER_SUCCESS;
}

// Called from the worker thread, inside work().
static LV2_Worker_Status worker_respond(LV2_Worker_Respond_Handle handle,
                                        uint32_t size, const void *data) {
  Lv2 *l = handle;
  return size <= WORK_FRAME_MAX && ring_push(&l->responses, data, size)
             ? LV2_WORKER_SUCCESS
             : LV2_WORKER_ERR_NO_SPACE;
}

static void *run_worker(void *data) {
  Lv2 *l = data;
  while (!atomic_load(&l->worker_stop)) {
    if (sem_wait(&l->work_ready) != 0)
      continue;  // interrupted
    if (atomic_load(&l->worker_stop))
      break;
    uint32_t size;
    while (ring_pop(&l->requests, l->work_in, &size))
      l->worker->work(lilv_instance_get_handle(l->instance), worker_respond, l,
                      size, l->work_in);
  }
  return NULL;
}

// Idempotent: the plugin must not be asked to work after it is deactivated,
// so this runs before that, and again on the paths that skip it.
static void stop_worker(Lv2 *l) {
  if (!l->worker_started)
    return;
  atomic_store(&l->worker_stop, true);
  sem_post(&l->work_ready);
  pthread_join(l->worker_thread, NULL);
  l->worker_started = false;
}

// --- loading ----------------------------------------------------------------

static bool lv2_load(Host *h, char **arguments) {
  Lv2 *l = calloc(1, sizeof(Lv2));
  if (!l)
    return false;
  h->impl = l;
  l->h = h;
  l->world = lilv_world_new();
  if (!l->world)
    return false;
  lilv_world_load_all(l->world);
  LilvNode *uri = lilv_new_uri(l->world, arguments[0]);
  l->plugin = lilv_plugins_get_by_uri(lilv_world_get_all_plugins(l->world), uri);
  lilv_node_free(uri);
  if (!l->plugin) {
    fputs("plugin is not installed\n", stderr);
    return false;
  }
  LilvNode *name = lilv_plugin_get_name(l->plugin);
  if (name) {
    host_set_plugin_name(h, lilv_node_as_string(name));
    lilv_node_free(name);
  }
  LilvNodes *required = lilv_plugin_get_required_features(l->plugin);
  bool supported = required_features_supported(required, false);
  lilv_nodes_free(required);
  if (!supported)
    return false;
  l->count = lilv_plugin_get_num_ports(l->plugin);
  if (!l->count || l->count > MAX_PORTS)
    return false;

  l->map = (LV2_URID_Map){l, map_uri};
  l->unmap = (LV2_URID_Unmap){l, unmap_uri};
  l->sequence_type = map_uri(l, LV2_ATOM__Sequence);
  l->schedule = (LV2_Worker_Schedule){l, schedule_work};
  if (sem_init(&l->work_ready, 0, 0) != 0)
    return false;
  l->work_ready_valid = true;
  // The block length is bounded by the check in the audio callback, so the
  // promise these options make is one this host keeps.
  l->min_block = 1;
  l->max_block = MAX_FRAMES;
  l->sequence_size = ATOM_CAPACITY;
  l->rate_hz = (float)h->rate;
  LV2_URID atom_int = map_uri(l, LV2_ATOM__Int),
           atom_float = map_uri(l, LV2_ATOM__Float);
  l->options[0] = (LV2_Options_Option){
      LV2_OPTIONS_INSTANCE, 0, map_uri(l, LV2_BUF_SIZE__minBlockLength),
      sizeof(int32_t), atom_int, &l->min_block};
  l->options[1] = (LV2_Options_Option){
      LV2_OPTIONS_INSTANCE, 0, map_uri(l, LV2_BUF_SIZE__maxBlockLength),
      sizeof(int32_t), atom_int, &l->max_block};
  l->options[2] = (LV2_Options_Option){
      LV2_OPTIONS_INSTANCE, 0, map_uri(l, LV2_BUF_SIZE__sequenceSize),
      sizeof(int32_t), atom_int, &l->sequence_size};
  l->options[3] = (LV2_Options_Option){
      LV2_OPTIONS_INSTANCE, 0, map_uri(l, LV2_PARAMETERS__sampleRate),
      sizeof(float), atom_float, &l->rate_hz};
  l->options[4] = (LV2_Options_Option){LV2_OPTIONS_BLANK, 0, 0, 0, 0, NULL};

  LV2_Feature map = {LV2_URID__map, &l->map},
              unmap = {LV2_URID__unmap, &l->unmap};
  LV2_Feature schedule = {LV2_WORKER__schedule, &l->schedule};
  LV2_Feature options = {LV2_OPTIONS__options, l->options};
  LV2_Feature bounded = {LV2_BUF_SIZE__boundedBlockLength, NULL};
  const LV2_Feature *features[] = {&map,     &unmap,   &schedule,
                                   &options, &bounded, NULL};
  l->instance = lilv_plugin_instantiate(l->plugin, h->rate, features);
  if (!l->instance) {
    fputs("LV2 instantiation failed\n", stderr);
    return false;
  }
  l->worker = lilv_instance_get_extension_data(l->instance, LV2_WORKER__interface);
  if (l->worker && !l->worker->work)
    l->worker = NULL;

  l->ports = calloc(l->count, sizeof(Port));
  float *ranges = calloc(l->count * 3, sizeof(float));
  if (!l->ports || !ranges) {
    free(ranges);
    return false;
  }
  lilv_plugin_get_port_ranges_float(l->plugin, ranges, ranges + l->count,
                                    ranges + 2 * l->count);
  LilvNode *input = lilv_new_uri(l->world, LV2_CORE__InputPort),
           *audio = lilv_new_uri(l->world, LV2_CORE__AudioPort);
  LilvNode *control = lilv_new_uri(l->world, LV2_CORE__ControlPort),
           *atom = lilv_new_uri(l->world, LV2_ATOM__AtomPort);
  LilvNode *optional = lilv_new_uri(l->world, LV2_CORE__connectionOptional);
  bool ok = true;
  unsigned ins = 0, outs = 0;
  for (uint32_t i = 0; i < l->count && ok; ++i) {
    Port *p = &l->ports[i];
    const LilvPort *port = lilv_plugin_get_port_by_index(l->plugin, i);
    p->symbol = lilv_node_as_string(lilv_port_get_symbol(l->plugin, port));
    p->input = lilv_port_is_a(l->plugin, port, input);
    p->audio = lilv_port_is_a(l->plugin, port, audio);
    p->control = lilv_port_is_a(l->plugin, port, control);
    p->atom = lilv_port_is_a(l->plugin, port, atom);
    p->channel = NO_CHANNEL;
    if (p->audio) {
      p->samples = calloc(MAX_FRAMES, sizeof(float));
      ok = p->samples != NULL;
      if (ok) {
        lilv_instance_connect_port(l->instance, i, p->samples);
        unsigned index = p->input ? ins++ : outs++;
        if (index < h->channels)
          p->channel = index;
      }
    } else if (p->control) {
      p->value = isfinite(ranges[i + 2 * l->count]) ? ranges[i + 2 * l->count] : 0;
      p->ctl = host_add_control(h, p->symbol, ranges[i], ranges[i + l->count],
                                p->value, !p->input, p);
      ok = p->ctl != NULL;
      lilv_instance_connect_port(l->instance, i, &p->value);
    } else if (p->atom) {
      p->sequence = calloc(1, ATOM_CAPACITY);
      ok = p->sequence != NULL;
      if (ok)
        lilv_instance_connect_port(l->instance, i, p->sequence);
    } else if (lilv_port_has_property(l->plugin, port, optional))
      lilv_instance_connect_port(l->instance, i, NULL);
    else {
      fprintf(stderr, "unsupported required LV2 port: %s\n", p->symbol);
      ok = false;
    }
  }
  lilv_node_free(input);
  lilv_node_free(audio);
  lilv_node_free(control);
  lilv_node_free(atom);
  lilv_node_free(optional);
  free(ranges);
  if (ok && (ins < h->channels || outs < h->channels)) {
    fputs("the plugin has fewer audio ports than the chain\n", stderr);
    ok = false;
  }
  // Whether an editor exists is decided when it is asked for; lilv can say
  // now whether there is an X11 one to try.
  if (ok) {
    LilvUIs *uis = lilv_plugin_get_uis(l->plugin);
    LilvNode *x11 = lilv_new_uri(l->world, LV2_UI__X11UI);
    LILV_FOREACH(uis, i, uis)
      if (lilv_ui_is_a(lilv_uis_get(uis, i), x11))
        h->has_editor = true;
    lilv_node_free(x11);
    lilv_uis_free(uis);
  }
  return ok;
}

static bool lv2_activate(Host *h) {
  Lv2 *l = h->impl;
  if (l->worker && pthread_create(&l->worker_thread, NULL, run_worker, l) == 0)
    l->worker_started = true;
  else if (l->worker) {
    fputs("could not start the plugin's worker thread\n", stderr);
    return false;
  }
  lilv_instance_activate(l->instance);
  l->activated = true;
  return true;
}

static void lv2_deactivate(Host *h) {
  Lv2 *l = h->impl;
  stop_worker(l);
  if (l->activated)
    lilv_instance_deactivate(l->instance);
  l->activated = false;
}

static void lv2_unload(Host *h) {
  Lv2 *l = h->impl;
  stop_worker(l);
  if (l->work_ready_valid)
    sem_destroy(&l->work_ready);
  if (l->instance)
    lilv_instance_free(l->instance);
  if (l->ports)
    for (uint32_t i = 0; i < l->count; ++i) {
      free(l->ports[i].samples);
      free(l->ports[i].sequence);
    }
  free(l->ports);
  for (uint32_t i = 0; i < l->uri_count; ++i)
    free(l->uris[i]);
  if (l->world)
    lilv_world_free(l->world);
  free(l);
  h->impl = NULL;
}

// --- audio ------------------------------------------------------------------

static void lv2_process(Host *h, uint32_t frames, float *const *in,
                        float *const *out) {
  Lv2 *l = h->impl;
  for (uint32_t i = 0; i < l->count; ++i) {
    Port *p = &l->ports[i];
    if (p->audio && p->input) {
      if (p->channel != NO_CHANNEL)
        memcpy(p->samples, in[p->channel], frames * sizeof(float));
      else
        memset(p->samples, 0, frames * sizeof(float));
    } else if (p->control && p->input)
      p->value = atomic_load(&p->ctl->desired);
    else if (p->atom) {
      p->sequence->atom.type = l->sequence_type;
      p->sequence->atom.size = p->input ? sizeof(LV2_Atom_Sequence_Body)
                                        : ATOM_CAPACITY - sizeof(LV2_Atom);
      p->sequence->body.unit = 0;
      p->sequence->body.pad = 0;
    }
  }
  if (l->worker) {
    uint32_t size;
    while (ring_pop(&l->responses, l->work_out, &size))
      l->worker->work_response(lilv_instance_get_handle(l->instance), size,
                               l->work_out);
  }
  lilv_instance_run(l->instance, frames);
  if (l->worker && l->worker->end_run)
    l->worker->end_run(lilv_instance_get_handle(l->instance));
  for (uint32_t i = 0; i < l->count; ++i) {
    Port *p = &l->ports[i];
    if (p->audio && !p->input && p->channel != NO_CHANNEL)
      memcpy(out[p->channel], p->samples, frames * sizeof(float));
    else if (p->control)
      atomic_store(&p->ctl->observed, p->value);
  }
}

// --- the editor -------------------------------------------------------------

static void ui_write(LV2UI_Controller controller, uint32_t index, uint32_t size,
                     uint32_t format, const void *buffer) {
  Lv2 *l = controller;
  if (index >= l->count || format != 0 || size != sizeof(float))
    return;
  Port *p = &l->ports[index];
  if (!p->input || !p->control)
    return;
  host_control_moved(l->h, p->ctl, *(const float *)buffer);
}

static int ui_resize(LV2UI_Feature_Handle handle, int width, int height) {
  Lv2 *l = handle;
  if (width < 1 || height < 1)
    return 1;
  host_resize_editor(l->h, (unsigned)width, (unsigned)height);
  return 0;
}

static bool lv2_editor_open(Host *h) {
  Lv2 *l = h->impl;
  LilvUIs *uis = lilv_plugin_get_uis(l->plugin);
  LilvNode *x11 = lilv_new_uri(l->world, LV2_UI__X11UI);
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
      lilv_new_uri(l->world, LV2_CORE__requiredFeature);
  LilvNodes *required = lilv_world_find_nodes(
      l->world, lilv_ui_get_uri(selected), required_predicate, NULL);
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
  // The library stays resident once loaded, which LV2 permits
  // (ui:makeSONameResident): toolkits keep process-wide state.
  if (!l->ui_library)
    l->ui_library = dlopen(binary, RTLD_NOW | RTLD_LOCAL);
  const LV2UI_Descriptor *(*descriptor)(uint32_t) =
      l->ui_library ? dlsym(l->ui_library, "lv2ui_descriptor") : NULL;
  l->ui_descriptor = NULL;
  if (descriptor)
    for (uint32_t i = 0;; ++i) {
      const LV2UI_Descriptor *d = descriptor(i);
      if (!d)
        break;
      if (!strcmp(d->URI, lilv_node_as_uri(lilv_ui_get_uri(selected)))) {
        l->ui_descriptor = d;
        break;
      }
    }
  if (l->ui_descriptor) {
    l->resize = (LV2UI_Resize){l, ui_resize};
    LV2_Feature parent = {LV2_UI__parent, (void *)(uintptr_t)h->window};
    LV2_Feature map = {LV2_URID__map, &l->map},
                unmap = {LV2_URID__unmap, &l->unmap};
    LV2_Feature access = {LV2_INSTANCE_ACCESS_URI,
                          lilv_instance_get_handle(l->instance)};
    LV2_Feature size = {LV2_UI__resize, &l->resize};
    LV2_Feature idle = {LV2_UI__idleInterface, NULL};
    const LV2_Feature *features[] = {&parent, &map,  &unmap, &access,
                                     &size,   &idle, NULL};
    LV2UI_Widget widget = NULL;
    l->ui = l->ui_descriptor->instantiate(
        l->ui_descriptor, lilv_node_as_uri(lilv_plugin_get_uri(l->plugin)),
        bundle, ui_write, l, &widget, features);
    if (l->ui)
      l->idle = l->ui_descriptor->extension_data
                    ? l->ui_descriptor->extension_data(LV2_UI__idleInterface)
                    : NULL;
  }
  lilv_free(binary);
  lilv_free(bundle);
  lilv_uis_free(uis);
  return l->ui != NULL;
}

static void lv2_editor_close(Host *h) {
  Lv2 *l = h->impl;
  if (l->ui)
    l->ui_descriptor->cleanup(l->ui);
  l->ui = NULL;
  l->idle = NULL;
}

static void lv2_editor_lost(Host *h) {
  Lv2 *l = h->impl;
  l->ui = NULL;
  l->idle = NULL;
}

static bool lv2_editor_idle(Host *h) {
  Lv2 *l = h->impl;
  if (!l->ui)
    return true;
  if (l->ui_descriptor->port_event)
    for (uint32_t i = 0; i < l->count; ++i) {
      Port *p = &l->ports[i];
      if (!p->control)
        continue;
      float value = p->input ? atomic_load(&p->ctl->desired)
                             : atomic_load(&p->ctl->observed);
      l->ui_descriptor->port_event(l->ui, i, sizeof(float), 0, &value);
    }
  return l->idle && l->idle->idle(l->ui);
}

const Backend lv2_backend = {
    .name = "LV2",
    .argument_count = 1,
    .load = lv2_load,
    .activate = lv2_activate,
    .deactivate = lv2_deactivate,
    .process = lv2_process,
    .editor_open = lv2_editor_open,
    .editor_close = lv2_editor_close,
    .editor_idle = lv2_editor_idle,
    .editor_lost = lv2_editor_lost,
    .editor_resized = NULL,
    .main_thread = NULL,
    .unload = lv2_unload,
};
