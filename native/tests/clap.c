// SPDX-License-Identifier: GPL-3.0-only
// The CLAP audio port layout, against fake plugins that declare more buses
// than the chain uses. A plugin's declared bus count is what its process
// call is allowed to index, so a host that hands it fewer sends it past the
// end of the array; and the scanner and the loader have to agree about which
// layouts can be carried, or the catalogue offers a plugin that is then
// refused. No PipeWire and no audio device: the buffers are plain malloc.
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
static bool fake_activate(const clap_plugin_t *p, double r, uint32_t a,
                          uint32_t b) {
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

int main(void) {
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
  return 0;
}
