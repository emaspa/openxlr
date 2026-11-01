// SPDX-License-Identifier: GPL-3.0-only
// The CLAP audio port layout, against fake plugins that declare more buses
// than the chain uses. A plugin's declared bus count is what its process
// call is allowed to index, so a host that hands it fewer sends it past the
// end of the array; and the scanner and the loader have to agree about which
// layouts can be carried, or the catalogue offers a plugin that is then
// refused. Teardown checks use a private event loop, without a PipeWire
// server or an audio device.
//
// host.c is included under a renamed main, as tests/editor.c does, and clap.c
// after the macro is undone, since clap_map_ports has a local called main.
#define main host_program_main
#include "../host.c"
#undef main
#include "../clap.c"
#include <assert.h>

// --- a fake plugin ----------------------------------------------------------

typedef struct {
  uint32_t count;
  uint32_t channels[MAX_PORTS];
  uint32_t main;      // which index carries CLAP_AUDIO_PORT_IS_MAIN
  bool flag_main;     // whether any port is flagged at all
} Side;

static Side in_side, out_side;
static bool touched_every_bus;
static uint32_t seen_in_count, seen_out_count;

static uint32_t ap_count(const clap_plugin_t *p, bool input) {
  return input ? in_side.count : out_side.count;
}

static bool ap_get(const clap_plugin_t *p, uint32_t index, bool input,
                   clap_audio_port_info_t *info) {
  Side *s = input ? &in_side : &out_side;
  if (index >= s->count)
    return false;
  memset(info, 0, sizeof(*info));
  info->id = index;
  info->channel_count = s->channels[index];
  info->flags = (s->flag_main && index == s->main) ? CLAP_AUDIO_PORT_IS_MAIN : 0;
  info->port_type = CLAP_PORT_STEREO;
  info->in_place_pair = CLAP_INVALID_ID;
  return true;
}

static const clap_plugin_audio_ports_t ap_ext = {ap_count, ap_get};

static bool fake_init(const clap_plugin_t *p) { return true; }
static void fake_destroy(const clap_plugin_t *p) {}
static uint32_t activated_min, activated_max;
static bool fake_activate(const clap_plugin_t *p, double r, uint32_t a,
                          uint32_t b) {
  activated_min = a;
  activated_max = b;
  return true;
}
static void fake_deactivate(const clap_plugin_t *p) {}
static bool fake_start(const clap_plugin_t *p) { return true; }
static void fake_stop(const clap_plugin_t *p) {}
static void fake_reset(const clap_plugin_t *p) {}
static const void *fake_ext(const clap_plugin_t *p, const char *id) {
  if (!strcmp(id, CLAP_EXT_AUDIO_PORTS))
    return &ap_ext;
  return NULL;
}
static void fake_on_main(const clap_plugin_t *p) {}

// What a real sidechain plugin does: read every declared input channel for the
// whole block and write every declared output channel for the whole block.
static clap_process_status fake_process(const clap_plugin_t *p,
                                        const clap_process_t *pr) {
  seen_in_count = pr->audio_inputs_count;
  seen_out_count = pr->audio_outputs_count;
  float sum = 0;
  for (uint32_t b = 0; b < pr->audio_inputs_count; ++b) {
    const clap_audio_buffer_t *buf = &pr->audio_inputs[b];
    assert(buf->data64 == NULL);  // a plugin preferring 64-bit must see none
    for (uint32_t ch = 0; ch < buf->channel_count; ++ch) {
      assert(buf->data32[ch] != NULL);
      for (uint32_t f = 0; f < pr->frames_count; ++f)
        sum += buf->data32[ch][f];
    }
  }
  for (uint32_t b = 0; b < pr->audio_outputs_count; ++b) {
    const clap_audio_buffer_t *buf = &pr->audio_outputs[b];
    assert(buf->data64 == NULL);
    for (uint32_t ch = 0; ch < buf->channel_count; ++ch) {
      assert(buf->data32[ch] != NULL);
      for (uint32_t f = 0; f < pr->frames_count; ++f)
        buf->data32[ch][f] = sum + (float)f;
    }
  }
  touched_every_bus = true;
  return CLAP_PROCESS_CONTINUE;
}

static clap_plugin_t fake = {.plugin_data = NULL,
                             .init = fake_init,
                             .destroy = fake_destroy,
                             .activate = fake_activate,
                             .deactivate = fake_deactivate,
                             .start_processing = fake_start,
                             .stop_processing = fake_stop,
                             .reset = fake_reset,
                             .process = fake_process,
                             .get_extension = fake_ext,
                             .on_main_thread = fake_on_main};

// --- the harness ------------------------------------------------------------

static Clap *fresh(Host *h) {
  Clap *c = calloc(1, sizeof(Clap));
  assert(c);
  c->h = h;
  c->plugin = &fake;
  c->quiet = calloc(MAX_FRAMES, sizeof(float));
  c->discard = calloc(MAX_FRAMES, sizeof(float));
  assert(c->quiet && c->discard);
  h->impl = c;
  return c;
}

static void release(Clap *c) {
  free(c->quiet);
  free(c->discard);
  free(c);
}

static void run(Host *h, Clap *c, uint32_t frames) {
  float *inbuf[MAX_CHANNELS], *outbuf[MAX_CHANNELS];
  for (unsigned i = 0; i < h->channels; ++i) {
    inbuf[i] = malloc(sizeof(float) * frames);
    outbuf[i] = malloc(sizeof(float) * frames);
    assert(inbuf[i] && outbuf[i]);
    for (uint32_t f = 0; f < frames; ++f)
      inbuf[i][f] = 0.25f;
  }
  clap_process_audio(h, frames, inbuf, outbuf);
  // The fake plugin writes sum + f into every declared output channel. The
  // chain's own output buffers must be the ones the main bus carried, so the
  // audio has to land here and nowhere else.
  float expect = 0.25f * (float)frames * (float)h->channels;
  for (unsigned i = 0; i < h->channels; ++i)
    for (uint32_t f = 0; f < frames; ++f)
      assert(outbuf[i][f] == expect + (float)f);
  for (unsigned i = 0; i < h->channels; ++i) {
    assert(inbuf[i][0] == 0.25f);  // the plugin must not have scribbled input
    free(inbuf[i]);
    free(outbuf[i]);
  }
}

static pthread_t parameter_thread;
static unsigned readonly_reads, writable_reads;
static bool parameter_wrong_thread;
static bool read_parameter(const clap_plugin_t *plugin, clap_id id, double *value) {
  parameter_wrong_thread |= !pthread_equal(pthread_self(), parameter_thread);
  if (id == 10) ++readonly_reads;
  else ++writable_reads;
  *value = 0.75;
  return true;
}
static const clap_plugin_params_t parameter_ext = {.get_value = read_parameter};

static void *run_parameter_audio(void *data) {
  Host *h = data;
  run(h, h->impl, 16);
  return NULL;
}

static void readonly_parameters_use_main_thread(void) {
  parameter_thread = pthread_self();
  readonly_reads = writable_reads = 0;
  parameter_wrong_thread = false;
  Host h = {.rate = 48000, .channels = 2, .main_thread = parameter_thread};
  Clap *c = fresh(&h);
  in_side = (Side){.count = 1, .channels = {2}, .main = 0, .flag_main = true};
  out_side = in_side;
  assert(clap_map_ports(c, &h, &ap_ext, true));
  assert(clap_map_ports(c, &h, &ap_ext, false));
  Param records[2] = {{.id = 10}, {.id = 11}};
  records[0].ctl = host_add_control(&h, "meter", 0, 1, 0.1f, true, &records[0]);
  records[1].ctl = host_add_control(&h, "gain", 0, 1, 0.5f, false, &records[1]);
  assert(records[0].ctl && records[1].ctl);
  c->records = records;
  c->record_count = 2;
  c->params = &parameter_ext;
  pthread_t audio;
  assert(pthread_create(&audio, NULL, run_parameter_audio, &h) == 0);
  assert(pthread_join(audio, NULL) == 0);
  assert(readonly_reads == 0 && writable_reads == 0);
  assert(!parameter_wrong_thread);
  assert(atomic_load(&records[0].ctl->observed) == 0.1f);
  clap_main_thread(&h);
  assert(readonly_reads == 1 && writable_reads == 0);
  assert(!parameter_wrong_thread);
  assert(atomic_load(&records[0].ctl->observed) == 0.75f);
  release(c);
  puts("PASS: CLAP read-only parameters are polled on the main thread only");
}

// A plugin's latency, and how often the host asks for it.
static uint32_t fake_latency_samples;
static unsigned latency_asked;
static uint32_t fake_latency(const clap_plugin_t *p) {
  ++latency_asked;
  return fake_latency_samples;
}
static const clap_plugin_latency_t latency_ext = {fake_latency};

// The tick asks for the latency every cycle once audio runs; the plugin is
// asked after activation and again only after it says the figure changed.
// What the daemon reads is one line at the start and one per change.
static void latency_reports(void) {
  Host h = {.rate = 48000, .channels = 2, .backend = &clap_backend};
  Clap *c = fresh(&h);
  c->host.host_data = c;
  assert(clap_latency(&h) == UINT32_MAX);
  assert(clap_activate(&h));
  assert(clap_latency(&h) == 0);
  clap_deactivate(&h);

  c->latency = &latency_ext;
  fake_latency_samples = 256;
  latency_asked = 0;
  assert(clap_activate(&h) && latency_asked == 1);
  assert(!report_latency(&h));  // nothing before the first audio cycle
  atomic_store(&h.audio_left, 1);
  assert(report_latency(&h) && h.reported_latency == 256);
  for (int tick = 0; tick < 100; ++tick)
    assert(!report_latency(&h));
  assert(latency_asked == 1);

  fake_latency_samples = 512;
  assert(!report_latency(&h) && latency_asked == 1);
  latency_changed(&c->host);
  assert(report_latency(&h) && h.reported_latency == 512 && latency_asked == 2);
  assert(!report_latency(&h) && latency_asked == 2);
  clap_deactivate(&h);
  assert(clap_latency(&h) == UINT32_MAX);
  assert(report_latency(&h) && h.reported_latency == UINT32_MAX);
  release(c);
  puts("PASS: CLAP latency is read at activation and reported once per change");
}

typedef struct {
  const clap_host_t *host;
  clap_id timer;
  int fd;
  bool unregister_timer, unregister_fd;
  unsigned destroyed;
} Teardown;

static void unregister_on_destroy(const clap_plugin_t *plugin) {
  Teardown *t = plugin->plugin_data;
  const clap_host_timer_support_t *timers =
      t->host->get_extension(t->host, CLAP_EXT_TIMER_SUPPORT);
  const clap_host_posix_fd_support_t *fds =
      t->host->get_extension(t->host, CLAP_EXT_POSIX_FD_SUPPORT);
  if (t->unregister_timer) {
    assert(timers->unregister_timer(t->host, t->timer));
    assert(!timers->unregister_timer(t->host, t->timer));
  }
  if (t->unregister_fd) {
    assert(fds->unregister_fd(t->host, t->fd));
    assert(!fds->unregister_fd(t->host, t->fd));
  }
  ++t->destroyed;
}

static void teardown_sources(bool unregister_timer, bool unregister_fd,
                             bool with_plugin) {
  Host h = {.loop = pw_main_loop_new(NULL)};
  assert(h.loop);
  Clap *c = fresh(&h);
  c->host = (clap_host_t){.host_data = c, .get_extension = host_get_extension};
  Teardown t = {.host = &c->host, .unregister_timer = unregister_timer,
                .unregister_fd = unregister_fd};
  clap_plugin_t plugin = fake;
  plugin.plugin_data = &t;
  plugin.destroy = unregister_on_destroy;
  c->plugin = with_plugin ? &plugin : NULL;
  clap_id ids[2];
  int timer_fds[2], pipes[2][2];
  for (unsigned i = 0; i < 2; ++i) {
    assert(timer_register(&c->host, 1000, &ids[i]));
    timer_fds[i] = c->timers[ids[i] - 1].source->fd;
    assert(pipe(pipes[i]) == 0);
    assert(fd_register(&c->host, pipes[i][0], CLAP_POSIX_FD_READ));
  }
  t.timer = ids[0];
  t.fd = pipes[0][0];
  // One source of each kind is always left for the host to collect.
  clap_unload(&h);
  assert(!h.impl && t.destroyed == (with_plugin ? 1u : 0u));
  for (unsigned i = 0; i < 2; ++i) {
    assert(fcntl(timer_fds[i], F_GETFD) == -1 && errno == EBADF);
    assert(fcntl(pipes[i][0], F_GETFD) >= 0);  // plugin owns the descriptor
    assert(write(pipes[i][1], "x", 1) == 1);
  }
  // A removed FD watch must not dispatch into the freed plugin or its host.
  pw_loop_enter(host_loop(&h));
  assert(pw_loop_iterate(host_loop(&h), 0) >= 0);
  pw_loop_leave(host_loop(&h));
  pw_main_loop_destroy(h.loop);
  for (unsigned i = 0; i < 2; ++i) {
    assert(fcntl(pipes[i][0], F_GETFD) >= 0);
    close(pipes[i][0]);
    close(pipes[i][1]);
  }
}

static bool refuse_start(const clap_plugin_t *plugin) { return false; }

static clap_process_status refuse_process(const clap_plugin_t *plugin,
                                           const clap_process_t *process) {
  return CLAP_PROCESS_ERROR;
}

static void processing_failure(void) {
  for (unsigned start = 0; start < 2; ++start) {
    Host h = {.rate = 48000, .channels = 2};
    Clap *c = fresh(&h);
    clap_plugin_t failing = fake;
    if (start) failing.start_processing = refuse_start;
    else failing.process = refuse_process;
    c->plugin = &failing;
    in_side = out_side = (Side){.count = 1, .channels = {2}, .main = 0,
                                .flag_main = true};
    assert(clap_map_ports(c, &h, &ap_ext, true));
    assert(clap_map_ports(c, &h, &ap_ext, false));
    float left[32], right[32];
    float *buffers[] = {left, right};
    // Each refused block is silenced; only half a second of them in a row
    // asks for a new host.
    const unsigned run = h.rate / 2 / 32;
    for (unsigned block = 1; block <= run; ++block) {
      for (unsigned n = 0; n < 32; ++n) left[n] = right[n] = .25f;
      clap_process_audio(&h, 32, buffers, buffers);
      for (unsigned n = 0; n < 32; ++n) assert(left[n] == 0 && right[n] == 0);
      assert(atomic_load(&h.audio_error) == (block == run));
    }
    release(c);
  }
  puts("PASS: CLAP start and processing errors silence audio, and a run of them requests host recovery");
}

static void sources_live_through_plugin_destruction(void) {
  pw_init(NULL, NULL);
  for (unsigned i = 0; i < 4; ++i)
    teardown_sources((i & 1) != 0, (i & 2) != 0, true);
  teardown_sources(false, false, false);
  pw_deinit();
  puts("PASS: plugin destruction can unregister timers and FDs, and the host collects leftovers");
}

// On its own block the plugin is activated for that block and no other size;
// otherwise for anything from one frame to the host's largest cycle.
static void own_block_activation(void) {
  for (int own = 0; own < 2; ++own) {
    Host h = {.rate = 48000, .channels = 2, .backend = &clap_backend,
              .own_block = own};
    Clap *c = fresh(&h);
    c->host.host_data = c;
    assert(clap_activate(&h));
    assert(activated_min == (own ? OWN_BLOCK_FRAMES : 1u));
    assert(activated_max == (own ? OWN_BLOCK_FRAMES : (uint32_t)MAX_FRAMES));
    clap_deactivate(&h);
    release(c);
  }
  puts("PASS: a CLAP plugin on its own block is activated for exactly 1024 frames");
}

int main(void) {
  own_block_activation();
  processing_failure();
  sources_live_through_plugin_destruction();
  readonly_parameters_use_main_thread();
  latency_reports();
  Host h = {.rate = 48000, .channels = 2};

  // 1. main stereo in + stereo sidechain in, one stereo out.
  in_side = (Side){.count = 2, .channels = {2, 2}, .main = 0, .flag_main = true};
  out_side = (Side){.count = 1, .channels = {2}, .main = 0, .flag_main = true};
  Clap *c = fresh(&h);
  assert(clap_map_ports(c, &h, &ap_ext, true));
  assert(clap_map_ports(c, &h, &ap_ext, false));
  assert(c->in_bus_count == 2 && c->out_bus_count == 1);
  assert(c->main_in == 0 && c->main_out == 0);
  touched_every_bus = false;
  run(&h, c, MAX_FRAMES);
  assert(touched_every_bus && seen_in_count == 2 && seen_out_count == 1);
  release(c);
  puts("PASS: a sidechain input bus gets real silence for a full block");

  // 2. the main bus is not port 0: sidechain first, main second.
  in_side = (Side){.count = 2, .channels = {1, 2}, .main = 1, .flag_main = true};
  out_side = (Side){.count = 2, .channels = {2, 4}, .main = 0, .flag_main = true};
  c = fresh(&h);
  assert(clap_map_ports(c, &h, &ap_ext, true));
  assert(clap_map_ports(c, &h, &ap_ext, false));
  assert(c->main_in == 1 && c->main_out == 0);
  run(&h, c, 1024);
  assert(seen_in_count == 2 && seen_out_count == 2);
  release(c);
  puts("PASS: a main bus behind a spare one is found and carried");

  // 3. nothing flagged main: port 0 is it.
  in_side = (Side){.count = 2, .channels = {2, 2}, .flag_main = false};
  out_side = (Side){.count = 1, .channels = {2}, .flag_main = false};
  c = fresh(&h);
  assert(clap_map_ports(c, &h, &ap_ext, true));
  assert(c->main_in == 0);
  release(c);
  puts("PASS: with nothing flagged, port 0 is the main one");

  // 4. refusals.
  in_side = (Side){.count = 0};
  c = fresh(&h);
  assert(!clap_map_ports(c, &h, &ap_ext, true));
  in_side = (Side){.count = MAX_PORTS + 1, .channels = {2, 2, 2, 2, 2, 2, 2, 2},
                   .flag_main = false};
  assert(!clap_map_ports(c, &h, &ap_ext, true));
  in_side = (Side){.count = 2, .channels = {2, MAX_PORT_CHANNELS + 1},
                   .main = 0, .flag_main = true};
  assert(!clap_map_ports(c, &h, &ap_ext, true));
  in_side = (Side){.count = 1, .channels = {1}, .main = 0, .flag_main = true};
  assert(!clap_map_ports(c, &h, &ap_ext, true));
  assert(!clap_map_ports(c, &h, NULL, true));
  release(c);
  puts("PASS: the refusals hold");

  // 5. two cycles in a row: the main pointers move, the spare ones stay.
  in_side = (Side){.count = 2, .channels = {2, 2}, .main = 0, .flag_main = true};
  out_side = (Side){.count = 2, .channels = {2, 2}, .main = 1, .flag_main = true};
  c = fresh(&h);
  assert(clap_map_ports(c, &h, &ap_ext, true));
  assert(clap_map_ports(c, &h, &ap_ext, false));
  run(&h, c, 512);
  run(&h, c, 256);
  assert(c->in_channels[1][0] == c->quiet && c->in_channels[1][1] == c->quiet);
  assert(c->out_channels[0][0] == c->discard);
  release(c);
  puts("PASS: repeated cycles keep the spare buses pointed at their buffers");

  // 5b. the widest spare output bus this host carries, at a full block.
  in_side = (Side){.count = 1, .channels = {2}, .main = 0, .flag_main = true};
  out_side = (Side){.count = 2, .channels = {2, MAX_PORT_CHANNELS},
                    .main = 0, .flag_main = true};
  c = fresh(&h);
  assert(clap_map_ports(c, &h, &ap_ext, true));
  assert(clap_map_ports(c, &h, &ap_ext, false));
  run(&h, c, MAX_FRAMES);
  release(c);
  puts("PASS: eight spare output channels at a full block stay in bounds");

  // 6. a mono chain.
  Host mono = {.rate = 48000, .channels = 1};
  in_side = (Side){.count = 2, .channels = {1, 2}, .main = 0, .flag_main = true};
  out_side = (Side){.count = 1, .channels = {1}, .main = 0, .flag_main = true};
  c = fresh(&mono);
  assert(clap_map_ports(c, &mono, &ap_ext, true));
  assert(clap_map_ports(c, &mono, &ap_ext, false));
  run(&mono, c, MAX_FRAMES);
  release(c);
  puts("PASS: a mono chain carries a stereo sidechain");

  // 7. the scanner and the loader read the layout the same way. The scan
  // passes 0 for the chain's width, because it does not know what the plugin
  // would be inserted into; everything else about what can be carried has to
  // be the same verdict, in the same words, or the catalogue offers a plugin
  // the loader then turns away.
  static const Side refused[] = {
      {.count = 0},
      {.count = MAX_PORTS + 1, .channels = {2, 2, 2, 2, 2, 2, 2, 2}},
      {.count = 2, .channels = {2, MAX_PORT_CHANNELS + 1}, .main = 0, .flag_main = true},
  };
  for (unsigned i = 0; i < sizeof(refused) / sizeof(*refused); ++i) {
    in_side = refused[i];
    PortLayout scanned, loaded;
    char scan_why[256] = "", load_why[256] = "";
    assert(!clap_layout(&fake, &ap_ext, true, 0, &scanned, scan_why, sizeof(scan_why)));
    assert(!clap_layout(&fake, &ap_ext, true, 2, &loaded, load_why, sizeof(load_why)));
    assert(!strcmp(scan_why, load_why) && scan_why[0]);
  }
  // A layout both accept, with the width the catalogue would show.
  in_side = (Side){.count = 2, .channels = {2, 2}, .main = 0, .flag_main = true};
  PortLayout scanned, loaded;
  char scan_why[256] = "", load_why[256] = "";
  assert(clap_layout(&fake, &ap_ext, true, 0, &scanned, scan_why, sizeof(scan_why)));
  assert(clap_layout(&fake, &ap_ext, true, 2, &loaded, load_why, sizeof(load_why)));
  assert(scanned.count == loaded.count && scanned.main == loaded.main);
  assert(scanned.main_channels == 2 && loaded.main_channels == 2);
  // The one thing only the loader can judge: the chain's own width. A scan
  // still describes the plugin, and the catalogue matches widths itself.
  assert(clap_layout(&fake, &ap_ext, true, 0, &scanned, scan_why, sizeof(scan_why)));
  assert(!clap_layout(&fake, &ap_ext, true, 1, &loaded, load_why, sizeof(load_why)));
  puts("PASS: the scanner and the loader refuse the same layouts, in the same words");

  // 8. A refusal in one direction leaves the other one described. The scan
  // prints both widths, so a reading it skips is a reading of whatever the
  // stack held: skipping the output reading once a refused input had
  // short-circuited it put 0xFEFEFEFE into the catalogue's JSON as an
  // audioOuts of 4278124286, for four real LSP mixers. Both layouts are
  // poisoned first, so anything left unwritten is caught here.
  in_side = (Side){.count = MAX_PORTS + 1, .channels = {2, 2, 2, 2, 2, 2, 2, 2}};
  out_side = (Side){.count = 1, .channels = {2}, .main = 0, .flag_main = true};
  PortLayout poisoned_in, poisoned_out;
  memset(&poisoned_in, 0xFE, sizeof(poisoned_in));
  memset(&poisoned_out, 0xFE, sizeof(poisoned_out));
  char why_in[256] = "", why_out[256] = "";
  bool read_in = clap_layout(&fake, &ap_ext, true, 0, &poisoned_in, why_in, sizeof(why_in));
  bool read_out = clap_layout(&fake, &ap_ext, false, 0, &poisoned_out, why_out, sizeof(why_out));
  assert(!read_in && read_out);
  assert(poisoned_in.count == 0 && poisoned_in.main == 0 && poisoned_in.main_channels == 0);
  assert(poisoned_out.count == 1 && poisoned_out.main_channels == 2);
  assert(why_in[0] && !why_out[0]);
  puts("PASS: a refused direction writes its own layout and leaves the other described");
  return 0;
}
