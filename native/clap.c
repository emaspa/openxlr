// SPDX-License-Identifier: GPL-3.0-only
// The CLAP backend: the plugin, its parameters as controls, its X11 editor,
// and the host services a CLAP plugin expects: timers, file descriptors,
// thread checks and a log. Also the scanner that describes a bundle for the
// daemon's catalogue, in a process of its own so a bad plugin costs nothing.
#include "host.h"

#include <clap/clap.h>
#include <dlfcn.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { MAX_TIMERS = 32, MAX_FDS = 32, MAX_EVENTS = 1024 };

typedef struct Clap Clap;

typedef struct {
  clap_id id;
  void *cookie;
  float last_sent;  // audio thread only: what the plugin has been told
  char symbol[16];  // the id in decimal, which is how the daemon names it
  Control *ctl;
} Param;

typedef struct {
  Clap *c;
  clap_id id;
  struct spa_source *source;
} Timer;

typedef struct {
  Clap *c;
  int fd;
  struct spa_source *source;
} Fd;

struct Clap {
  Host *h;
  void *library;
  const clap_plugin_entry_t *entry;
  const clap_plugin_t *plugin;
  clap_host_t host;
  const clap_plugin_params_t *params;
  const clap_plugin_gui_t *gui;
  const clap_plugin_timer_support_t *timer_support;
  const clap_plugin_posix_fd_support_t *fd_support;
  Param *records;
  uint32_t record_count;
  bool activated, gui_created, gui_closed;
  _Atomic bool processing;  // start_processing has run, on the audio thread
  _Atomic bool callback_requested, flush_requested;
  Timer timers[MAX_TIMERS];
  Fd fds[MAX_FDS];
  int64_t steady;
  // The events of one process call. The plugin reads the inputs through
  // in_list and answers through out_list; neither allocates.
  clap_event_param_value_t in_events[MAX_EVENTS];
  uint32_t in_count;
  clap_input_events_t in_list;
  clap_output_events_t out_list;
};

static Clap *of(const clap_host_t *host) { return host->host_data; }

// --- host services ----------------------------------------------------------

static void host_log(const clap_host_t *host, clap_log_severity severity,
                     const char *message) {
  fprintf(stderr, "plugin: %s\n", message);
}

static const clap_host_log_t log_extension = {host_log};

static bool is_main_thread(const clap_host_t *host) {
  return host_on_main_thread(of(host)->h);
}

static bool is_audio_thread(const clap_host_t *host) {
  return host_on_audio_thread(of(host)->h);
}

static const clap_host_thread_check_t thread_check_extension = {
    is_main_thread, is_audio_thread};

static void params_rescan(const clap_host_t *host,
                          clap_param_rescan_flags flags) {
  // Values arrive through events; a change to the list itself would need
  // the daemon's catalogue to change too, which is a restart's job.
  if (flags & (CLAP_PARAM_RESCAN_ALL | CLAP_PARAM_RESCAN_INFO))
    fputs("plugin changed its parameter list; the catalogue will lag\n",
          stderr);
}

static void params_clear(const clap_host_t *host, clap_id id,
                         clap_param_clear_flags flags) {}

static void params_request_flush(const clap_host_t *host) {
  atomic_store(&of(host)->flush_requested, true);
}

static const clap_host_params_t params_extension = {
    params_rescan, params_clear, params_request_flush};

static void gui_resize_hints_changed(const clap_host_t *host) {}

static bool gui_request_resize(const clap_host_t *host, uint32_t width,
                               uint32_t height) {
  host_resize_editor(of(host)->h, width, height);
  return true;
}

static bool gui_request_show(const clap_host_t *host) {
  host_show_editor(of(host)->h, true);
  return true;
}

static bool gui_request_hide(const clap_host_t *host) {
  host_show_editor(of(host)->h, false);
  return true;
}

static void gui_closed(const clap_host_t *host, bool was_destroyed) {
  Clap *c = of(host);
  c->gui_closed = true;
  if (was_destroyed)
    c->gui_created = false;
}

static const clap_host_gui_t gui_extension = {
    gui_resize_hints_changed, gui_request_resize, gui_request_show,
    gui_request_hide, gui_closed};

static void fire_timer(void *p) {
  Timer *t = p;
  t->c->timer_support->on_timer(t->c->plugin, t->id);
}

static void timer_fired(void *data, uint64_t expirations) {
  Timer *t = data;
  if (t->c->timer_support)
    host_run_guarded(t->c->h, fire_timer, t);
}

static bool timer_register(const clap_host_t *host, uint32_t period_ms,
                           clap_id *id) {
  Clap *c = of(host);
  for (int i = 0; i < MAX_TIMERS; ++i) {
    Timer *t = &c->timers[i];
    if (t->source)
      continue;
    t->c = c;
    t->id = (clap_id)i + 1;
    t->source = pw_loop_add_timer(host_loop(c->h), timer_fired, t);
    if (!t->source)
      return false;
    if (period_ms == 0)
      period_ms = 1;
    struct timespec interval = {period_ms / 1000, (period_ms % 1000) * 1000000L};
    pw_loop_update_timer(host_loop(c->h), t->source, &interval, &interval,
                         false);
    *id = t->id;
    return true;
  }
  return false;
}

static bool timer_unregister(const clap_host_t *host, clap_id id) {
  Clap *c = of(host);
  if (id < 1 || id > MAX_TIMERS || !c->timers[id - 1].source)
    return false;
  pw_loop_destroy_source(host_loop(c->h), c->timers[id - 1].source);
  c->timers[id - 1].source = NULL;
  return true;
}

static const clap_host_timer_support_t timer_extension = {timer_register,
                                                          timer_unregister};

static uint32_t io_mask(clap_posix_fd_flags_t flags) {
  uint32_t mask = 0;
  if (flags & CLAP_POSIX_FD_READ)
    mask |= SPA_IO_IN;
  if (flags & CLAP_POSIX_FD_WRITE)
    mask |= SPA_IO_OUT;
  if (flags & CLAP_POSIX_FD_ERROR)
    mask |= SPA_IO_ERR | SPA_IO_HUP;
  return mask;
}

typedef struct {
  Fd *fd;
  clap_posix_fd_flags_t flags;
} FdCall;

static void fire_fd(void *p) {
  FdCall *a = p;
  a->fd->c->fd_support->on_fd(a->fd->c->plugin, a->fd->fd, a->flags);
}

static void fd_ready(void *data, int fd, uint32_t mask) {
  Fd *f = data;
  if (!f->c->fd_support)
    return;
  FdCall call = {f, 0};
  if (mask & SPA_IO_IN)
    call.flags |= CLAP_POSIX_FD_READ;
  if (mask & SPA_IO_OUT)
    call.flags |= CLAP_POSIX_FD_WRITE;
  if (mask & (SPA_IO_ERR | SPA_IO_HUP))
    call.flags |= CLAP_POSIX_FD_ERROR;
  host_run_guarded(f->c->h, fire_fd, &call);
}

static Fd *fd_slot(Clap *c, int fd) {
  for (int i = 0; i < MAX_FDS; ++i)
    if (c->fds[i].source && c->fds[i].fd == fd)
      return &c->fds[i];
  return NULL;
}

static bool fd_register(const clap_host_t *host, int fd,
                        clap_posix_fd_flags_t flags) {
  Clap *c = of(host);
  if (fd_slot(c, fd))
    return false;
  for (int i = 0; i < MAX_FDS; ++i) {
    Fd *f = &c->fds[i];
    if (f->source)
      continue;
    f->c = c;
    f->fd = fd;
    f->source = pw_loop_add_io(host_loop(c->h), fd, io_mask(flags), false,
                               fd_ready, f);
    return f->source != NULL;
  }
  return false;
}

static bool fd_modify(const clap_host_t *host, int fd,
                      clap_posix_fd_flags_t flags) {
  Clap *c = of(host);
  Fd *f = fd_slot(c, fd);
  return f && pw_loop_update_io(host_loop(c->h), f->source, io_mask(flags)) == 0;
}

static bool fd_unregister(const clap_host_t *host, int fd) {
  Clap *c = of(host);
  Fd *f = fd_slot(c, fd);
  if (!f)
    return false;
  pw_loop_destroy_source(host_loop(c->h), f->source);
  f->source = NULL;
  return true;
}

static const clap_host_posix_fd_support_t fd_extension = {
    fd_register, fd_modify, fd_unregister};

static void state_mark_dirty(const clap_host_t *host) {}

static const clap_host_state_t state_extension = {state_mark_dirty};

static bool ports_rescan_supported(const clap_host_t *host, uint32_t flag) {
  return false;
}

static void ports_rescan(const clap_host_t *host, uint32_t flags) {}

static const clap_host_audio_ports_t audio_ports_extension = {
    ports_rescan_supported, ports_rescan};

static const void *host_get_extension(const clap_host_t *host,
                                      const char *id) {
  if (!strcmp(id, CLAP_EXT_LOG))
    return &log_extension;
  if (!strcmp(id, CLAP_EXT_THREAD_CHECK))
    return &thread_check_extension;
  if (!strcmp(id, CLAP_EXT_PARAMS))
    return &params_extension;
  if (!strcmp(id, CLAP_EXT_GUI))
    return &gui_extension;
  if (!strcmp(id, CLAP_EXT_TIMER_SUPPORT))
    return &timer_extension;
  if (!strcmp(id, CLAP_EXT_POSIX_FD_SUPPORT))
    return &fd_extension;
  if (!strcmp(id, CLAP_EXT_STATE))
    return &state_extension;
  if (!strcmp(id, CLAP_EXT_AUDIO_PORTS))
    return &audio_ports_extension;
  return NULL;
}

static void host_request_restart(const clap_host_t *host) {
  host_fail(of(host)->h, "the plugin asked to be restarted");
}

static void host_request_process(const clap_host_t *host) {}

static void host_request_callback(const clap_host_t *host) {
  atomic_store(&of(host)->callback_requested, true);
}

// --- events -----------------------------------------------------------------

static uint32_t in_size(const clap_input_events_t *list) {
  return ((Clap *)list->ctx)->in_count;
}

static const clap_event_header_t *in_get(const clap_input_events_t *list,
                                         uint32_t index) {
  Clap *c = list->ctx;
  return index < c->in_count ? &c->in_events[index].header : NULL;
}

static Param *param_by_id(Clap *c, clap_id id) {
  for (uint32_t i = 0; i < c->record_count; ++i)
    if (c->records[i].id == id)
      return &c->records[i];
  return NULL;
}

// The plugin's answers: a parameter its editor moved comes back here, on
// the audio thread, and the core carries it to the daemon.
static bool out_push(const clap_output_events_t *list,
                     const clap_event_header_t *event) {
  Clap *c = list->ctx;
  if (event->space_id != CLAP_CORE_EVENT_SPACE_ID ||
      event->type != CLAP_EVENT_PARAM_VALUE)
    return true;
  const clap_event_param_value_t *e = (const clap_event_param_value_t *)event;
  Param *p = param_by_id(c, e->param_id);
  if (p) {
    p->last_sent = (float)e->value;
    host_control_moved_rt(c->h, p->ctl, (float)e->value);
  }
  return true;
}

// --- loading ----------------------------------------------------------------

static bool open_bundle(Clap *c, const char *file) {
  c->library = dlopen(file, RTLD_NOW | RTLD_LOCAL);
  if (!c->library) {
    fprintf(stderr, "cannot load %s: %s\n", file, dlerror());
    return false;
  }
  c->entry = dlsym(c->library, "clap_entry");
  if (!c->entry || c->entry->clap_version.major != CLAP_VERSION_MAJOR) {
    fputs("not a CLAP bundle this host understands\n", stderr);
    return false;
  }
  if (!c->entry->init(file)) {
    fputs("the bundle refused to initialise\n", stderr);
    c->entry = NULL;
    return false;
  }
  return true;
}

static const clap_plugin_factory_t *factory_of(Clap *c) {
  return c->entry->get_factory(CLAP_PLUGIN_FACTORY_ID);
}

// The main audio port in one direction, or the first one: its channel count.
static uint32_t main_port_channels(const clap_plugin_t *plugin,
                                   const clap_plugin_audio_ports_t *ports,
                                   bool input) {
  if (!ports)
    return 0;
  uint32_t count = ports->count(plugin, input), fallback = 0;
  for (uint32_t i = 0; i < count; ++i) {
    clap_audio_port_info_t info;
    if (!ports->get(plugin, i, input, &info))
      continue;
    if (info.flags & CLAP_AUDIO_PORT_IS_MAIN)
      return info.channel_count;
    if (i == 0)
      fallback = info.channel_count;
  }
  return fallback;
}

static bool clap_load(Host *h, char **arguments) {
  Clap *c = calloc(1, sizeof(Clap));
  if (!c)
    return false;
  h->impl = c;
  c->h = h;
  c->host = (clap_host_t){
      .clap_version = CLAP_VERSION_INIT,
      .host_data = c,
      .name = "OpenXLR",
      .vendor = "OpenXLR",
      .url = "https://github.com/emaspa/openxlr",
      .version = "1",
      .get_extension = host_get_extension,
      .request_restart = host_request_restart,
      .request_process = host_request_process,
      .request_callback = host_request_callback,
  };
  c->in_list = (clap_input_events_t){c, in_size, in_get};
  c->out_list = (clap_output_events_t){c, out_push};
  if (!open_bundle(c, arguments[0]))
    return false;
  const clap_plugin_factory_t *factory = factory_of(c);
  if (!factory)
    return false;
  c->plugin = factory->create_plugin(factory, &c->host, arguments[1]);
  if (!c->plugin) {
    fprintf(stderr, "the bundle has no plugin %s\n", arguments[1]);
    return false;
  }
  if (c->plugin->desc && c->plugin->desc->name)
    host_set_plugin_name(h, c->plugin->desc->name);
  if (!c->plugin->init(c->plugin)) {
    fputs("the plugin refused to initialise\n", stderr);
    return false;
  }
  const clap_plugin_audio_ports_t *ports =
      c->plugin->get_extension(c->plugin, CLAP_EXT_AUDIO_PORTS);
  if (main_port_channels(c->plugin, ports, true) != h->channels ||
      main_port_channels(c->plugin, ports, false) != h->channels) {
    fputs("the plugin's main ports do not match the chain's channels\n",
          stderr);
    return false;
  }
  c->params = c->plugin->get_extension(c->plugin, CLAP_EXT_PARAMS);
  c->gui = c->plugin->get_extension(c->plugin, CLAP_EXT_GUI);
  c->timer_support =
      c->plugin->get_extension(c->plugin, CLAP_EXT_TIMER_SUPPORT);
  c->fd_support =
      c->plugin->get_extension(c->plugin, CLAP_EXT_POSIX_FD_SUPPORT);
  if (c->params) {
    uint32_t count = c->params->count(c->plugin);
    c->records = calloc(count ? count : 1, sizeof(Param));
    if (!c->records)
      return false;
    for (uint32_t i = 0; i < count; ++i) {
      clap_param_info_t info;
      if (!c->params->get_info(c->plugin, i, &info) ||
          (info.flags & CLAP_PARAM_IS_HIDDEN))
        continue;
      Param *p = &c->records[c->record_count];
      p->id = info.id;
      p->cookie = info.cookie;
      snprintf(p->symbol, sizeof(p->symbol), "%u", info.id);
      double value = info.default_value;
      c->params->get_value(c->plugin, info.id, &value);
      p->last_sent = (float)value;
      p->ctl = host_add_control(h, p->symbol, (float)info.min_value,
                                (float)info.max_value, (float)value,
                                (info.flags & CLAP_PARAM_IS_READONLY) != 0, p);
      if (!p->ctl)
        break;
      c->record_count++;
    }
  }
  h->has_editor = c->gui && c->gui->is_api_supported(c->plugin,
                                                    CLAP_WINDOW_API_X11, false);
  return true;
}

static bool clap_activate(Host *h) {
  Clap *c = h->impl;
  if (!c->plugin->activate(c->plugin, h->rate, 1, MAX_FRAMES)) {
    fputs("the plugin refused to activate\n", stderr);
    return false;
  }
  c->activated = true;
  return true;
}

static void clap_deactivate(Host *h) {
  Clap *c = h->impl;
  if (!c->activated)
    return;
  // stop_processing belongs on the audio thread, which is gone once the
  // node has disconnected. Called here instead, after that thread's last
  // cycle, which every plugin tested tolerates.
  if (atomic_exchange(&c->processing, false))
    c->plugin->stop_processing(c->plugin);
  c->plugin->deactivate(c->plugin);
  c->activated = false;
}

static void clap_unload(Host *h) {
  Clap *c = h->impl;
  for (int i = 0; i < MAX_TIMERS; ++i)
    if (c->timers[i].source)
      pw_loop_destroy_source(host_loop(h), c->timers[i].source);
  for (int i = 0; i < MAX_FDS; ++i)
    if (c->fds[i].source)
      pw_loop_destroy_source(host_loop(h), c->fds[i].source);
  if (c->plugin)
    c->plugin->destroy(c->plugin);
  if (c->entry)
    c->entry->deinit();
  if (c->library)
    dlclose(c->library);
  free(c->records);
  free(c);
  h->impl = NULL;
}

// --- audio ------------------------------------------------------------------

static void clap_process_audio(Host *h, uint32_t frames, float *const *in,
                               float *const *out) {
  Clap *c = h->impl;
  float *inputs[MAX_CHANNELS], *outputs[MAX_CHANNELS];
  for (unsigned i = 0; i < h->channels; ++i) {
    inputs[i] = in[i];
    outputs[i] = out[i];
  }
  if (!atomic_load(&c->processing)) {
    if (!c->plugin->start_processing(c->plugin)) {
      for (unsigned i = 0; i < h->channels; ++i)
        memset(outputs[i], 0, frames * sizeof(float));
      return;
    }
    atomic_store(&c->processing, true);
  }
  c->in_count = 0;
  for (uint32_t i = 0; i < c->record_count && c->in_count < MAX_EVENTS; ++i) {
    Param *p = &c->records[i];
    if (p->ctl->output)
      continue;
    float desired = atomic_load(&p->ctl->desired);
    if (desired == p->last_sent)
      continue;
    clap_event_param_value_t *e = &c->in_events[c->in_count++];
    *e = (clap_event_param_value_t){
        .header = {sizeof(*e), 0, CLAP_CORE_EVENT_SPACE_ID,
                   CLAP_EVENT_PARAM_VALUE, 0},
        .param_id = p->id,
        .cookie = p->cookie,
        .note_id = -1,
        .port_index = -1,
        .channel = -1,
        .key = -1,
        .value = desired,
    };
    p->last_sent = desired;
  }
  clap_audio_buffer_t input = {.data32 = inputs, .channel_count = h->channels};
  clap_audio_buffer_t output = {.data32 = outputs, .channel_count = h->channels};
  clap_process_t process = {
      .steady_time = c->steady,
      .frames_count = frames,
      .transport = NULL,
      .audio_inputs = &input,
      .audio_outputs = &output,
      .audio_inputs_count = 1,
      .audio_outputs_count = 1,
      .in_events = &c->in_list,
      .out_events = &c->out_list,
  };
  clap_process_status status = c->plugin->process(c->plugin, &process);
  c->steady += frames;
  if (status == CLAP_PROCESS_ERROR)
    for (unsigned i = 0; i < h->channels; ++i)
      memset(outputs[i], 0, frames * sizeof(float));
  for (uint32_t i = 0; i < c->record_count; ++i)
    if (c->records[i].ctl->output) {
      double value;
      if (c->params->get_value(c->plugin, c->records[i].id, &value))
        atomic_store(&c->records[i].ctl->observed, (float)value);
    }
}

// --- main thread ------------------------------------------------------------

static void on_main_thread(void *p) {
  Clap *c = p;
  c->plugin->on_main_thread(c->plugin);
}

static void clap_main_thread(Host *h) {
  Clap *c = h->impl;
  if (atomic_exchange(&c->callback_requested, false))
    host_run_guarded(h, on_main_thread, c);
  // A plugin that is not being processed flushes parameter changes here
  // instead; once the node runs, every process call carries them.
  if (atomic_exchange(&c->flush_requested, false) && c->params &&
      !atomic_load(&c->processing)) {
    c->in_count = 0;
    c->params->flush(c->plugin, &c->in_list, &c->out_list);
  }
}

// --- the editor -------------------------------------------------------------

static bool clap_editor_open(Host *h) {
  Clap *c = h->impl;
  if (!h->has_editor)
    return false;
  if (!c->gui->create(c->plugin, CLAP_WINDOW_API_X11, false))
    return false;
  c->gui_created = true;
  c->gui_closed = false;
  c->gui->set_scale(c->plugin, 1.0);
  uint32_t width = 0, height = 0;
  if (c->gui->get_size(c->plugin, &width, &height) && width && height)
    host_resize_editor(h, width, height);
  clap_window_t window = {.api = CLAP_WINDOW_API_X11, .x11 = h->window};
  if (!c->gui->set_parent(c->plugin, &window)) {
    c->gui->destroy(c->plugin);
    c->gui_created = false;
    return false;
  }
  c->gui->show(c->plugin);
  return true;
}

static void clap_editor_close(Host *h) {
  Clap *c = h->impl;
  if (!c->gui_created)
    return;
  c->gui->hide(c->plugin);
  c->gui->destroy(c->plugin);
  c->gui_created = false;
}

static void clap_editor_lost(Host *h) {
  // The plugin's window is inside a display that is gone; destroying it
  // would go through that display. It stays until this process exits.
  ((Clap *)h->impl)->gui_created = false;
}

static bool clap_editor_idle(Host *h) { return ((Clap *)h->impl)->gui_closed; }

const Backend clap_backend = {
    .name = "CLAP",
    .argument_count = 2,
    .load = clap_load,
    .activate = clap_activate,
    .deactivate = clap_deactivate,
    .process = clap_process_audio,
    .editor_open = clap_editor_open,
    .editor_close = clap_editor_close,
    .editor_idle = clap_editor_idle,
    .editor_lost = clap_editor_lost,
    .editor_resized = NULL,
    .main_thread = clap_main_thread,
    .unload = clap_unload,
};

// --- the scanner ------------------------------------------------------------

static void json_string(const char *s) {
  putchar('"');
  for (; s && *s; ++s) {
    unsigned char ch = (unsigned char)*s;
    if (ch == '"' || ch == '\\')
      printf("\\%c", ch);
    else if (ch < 0x20)
      printf("\\u%04x", ch);
    else
      putchar(ch);
  }
  putchar('"');
}

static bool scan_main_thread(const clap_host_t *host) { return true; }
static bool scan_audio_thread(const clap_host_t *host) { return false; }
static const clap_host_thread_check_t scan_thread_check = {scan_main_thread,
                                                           scan_audio_thread};

static const void *scan_get_extension(const clap_host_t *host,
                                      const char *id) {
  if (!strcmp(id, CLAP_EXT_LOG))
    return &log_extension;
  if (!strcmp(id, CLAP_EXT_THREAD_CHECK))
    return &scan_thread_check;
  return NULL;
}

static void scan_nothing(const clap_host_t *host) {}

// Describe every plugin in one bundle as JSON, for the daemon's catalogue.
// Each plugin is created and destroyed once so its ports and parameters can
// be read; a plugin that crashes takes this process, and only this bundle,
// with it.
int clap_scan(const char *file) {
  Clap c = {0};
  if (!open_bundle(&c, file))
    return 1;
  const clap_plugin_factory_t *factory = factory_of(&c);
  if (!factory) {
    c.entry->deinit();
    return 1;
  }
  clap_host_t host = {
      .clap_version = CLAP_VERSION_INIT,
      .host_data = NULL,
      .name = "OpenXLR",
      .vendor = "OpenXLR",
      .url = "https://github.com/emaspa/openxlr",
      .version = "1",
      .get_extension = scan_get_extension,
      .request_restart = scan_nothing,
      .request_process = scan_nothing,
      .request_callback = scan_nothing,
  };
  printf("{\"file\":");
  json_string(file);
  printf(",\"plugins\":[");
  uint32_t count = factory->get_plugin_count(factory);
  bool first = true;
  for (uint32_t i = 0; i < count; ++i) {
    const clap_plugin_descriptor_t *d = factory->get_plugin_descriptor(factory, i);
    if (!d || !d->id)
      continue;
    const clap_plugin_t *plugin = factory->create_plugin(factory, &host, d->id);
    if (!plugin)
      continue;
    if (!plugin->init(plugin)) {
      plugin->destroy(plugin);
      continue;
    }
    printf("%s{\"id\":", first ? "" : ",");
    first = false;
    json_string(d->id);
    printf(",\"name\":");
    json_string(d->name ? d->name : d->id);
    printf(",\"vendor\":");
    json_string(d->vendor ? d->vendor : "");
    printf(",\"features\":[");
    for (const char *const *f = d->features; f && *f; ++f) {
      if (f != d->features)
        putchar(',');
      json_string(*f);
    }
    const clap_plugin_audio_ports_t *ports =
        plugin->get_extension(plugin, CLAP_EXT_AUDIO_PORTS);
    printf("],\"audioIns\":%u,\"audioOuts\":%u",
           main_port_channels(plugin, ports, true),
           main_port_channels(plugin, ports, false));
    const clap_plugin_gui_t *gui = plugin->get_extension(plugin, CLAP_EXT_GUI);
    printf(",\"gui\":%s",
           gui && gui->is_api_supported(plugin, CLAP_WINDOW_API_X11, false)
               ? "true"
               : "false");
    printf(",\"params\":[");
    const clap_plugin_params_t *params =
        plugin->get_extension(plugin, CLAP_EXT_PARAMS);
    uint32_t parameters = params ? params->count(plugin) : 0;
    bool first_param = true;
    for (uint32_t p = 0; p < parameters; ++p) {
      clap_param_info_t info;
      if (!params->get_info(plugin, p, &info) ||
          (info.flags & CLAP_PARAM_IS_HIDDEN))
        continue;
      printf("%s{\"id\":%u,\"name\":", first_param ? "" : ",", info.id);
      first_param = false;
      json_string(info.name);
      printf(",\"module\":");
      json_string(info.module);
      printf(",\"min\":%.9g,\"max\":%.9g,\"default\":%.9g,\"readonly\":%s,"
             "\"stepped\":%s,\"enum\":%s}",
             info.min_value, info.max_value, info.default_value,
             (info.flags & CLAP_PARAM_IS_READONLY) ? "true" : "false",
             (info.flags & CLAP_PARAM_IS_STEPPED) ? "true" : "false",
             (info.flags & CLAP_PARAM_IS_ENUM) ? "true" : "false");
    }
    printf("]}");
    plugin->destroy(plugin);
  }
  puts("]}");
  c.entry->deinit();
  dlclose(c.library);
  return 0;
}
