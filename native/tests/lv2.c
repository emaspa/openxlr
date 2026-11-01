// SPDX-License-Identifier: GPL-3.0-only
// The LV2 latency report, against a fixture whose latency port follows its
// own delay control, and the mono and stereo gain fixtures the capture chain
// tests load. No PipeWire: the process calls are made here, on plain
// buffers, and lilv finds the fixtures through LV2_PATH.
//
// host.c is included under a renamed main, as the other tests do, and lv2.c
// after it to reach the backend's own functions.
// realpath, mkdtemp and symlink, which strict C11 leaves out.
#define _DEFAULT_SOURCE
#define main host_program_main
#include "../host.c"
#undef main
#include "../lv2.c"
#include <assert.h>
#include <limits.h>
#include <unistd.h>

static Control *control_named(Host *h, const char *symbol) {
  for (uint32_t i = 0; i < h->control_count; ++i)
    if (!strcmp(h->controls[i].symbol, symbol))
      return &h->controls[i];
  return NULL;
}

static void cycle(Host *h) {
  static float in_l[256], in_r[256], out_l[256], out_r[256];
  float *in[] = {in_l, in_r}, *out[] = {out_l, out_r};
  lv2_process(h, 256, in, out);
  atomic_fetch_add(&h->audio_left, 1);
}

static void gain_fixture(uint32_t channels, char *uri) {
  Host h = {.rate = 48000, .channels = channels, .backend = &lv2_backend};
  char *arguments[] = {uri};
  assert(lv2_load(&h, arguments));
  assert(lv2_activate(&h));
  Control *gain = control_named(&h, "gain");
  assert(gain);
  atomic_store(&gain->desired, 0.25f);
  float left[] = {0.1f, -0.2f}, right[] = {0.3f, -0.4f};
  float out_left[2], out_right[2];
  float *in[] = {left, right}, *out[] = {out_left, out_right};
  lv2_process(&h, 2, in, out);
  for (uint32_t channel = 0; channel < channels; channel++)
    for (uint32_t frame = 0; frame < 2; frame++)
      assert(fabsf(out[channel][frame] - in[channel][frame] * 0.25f) < 1e-6f);
  lv2_deactivate(&h);
  lv2_unload(&h);
}

static int32_t option_value(Lv2 *l, const char *uri) {
  LV2_URID key = map_uri(l, uri);
  for (const LV2_Options_Option *o = l->options; o->key; ++o)
    if (o->key == key) return *(const int32_t *)o->value;
  return -1;
}

// On its own block the plugin is told the block is exactly 1024 frames:
// minimum, maximum and nominal. Otherwise it is told the bounds the audio
// callback keeps, and no nominal length. The real fixture then runs through
// the block's worker side and its output is its gain on the input, exactly
// the block's delay late.
static void own_block_fixture(char *uri) {
  for (int own = 0; own < 2; ++own) {
    // The main thread is named so the plugin's URIs map to real keys.
    Host h = {.rate = 48000, .channels = 1, .backend = &lv2_backend,
              .own_block = own, .main_thread = pthread_self()};
    char *arguments[] = {uri};
    assert(lv2_load(&h, arguments));
    Lv2 *l = h.impl;
    assert(option_value(l, LV2_BUF_SIZE__minBlockLength) == (own ? 1024 : 1));
    assert(option_value(l, LV2_BUF_SIZE__maxBlockLength) == (own ? 1024 : MAX_FRAMES));
    assert(option_value(l, LV2_BUF_SIZE__nominalBlockLength) == (own ? 1024 : -1));
    assert(lv2_activate(&h));
    if (own) {
      Control *gain = control_named(&h, "gain");
      assert(gain && own_block_init(&h));
      atomic_store(&gain->desired, 0.5f);
      static float input[256], output[256], heard[256 * 64];
      float *in[] = {input}, *out[] = {output};
      for (unsigned i = 0; i < 64; ++i) {
        for (unsigned n = 0; n < 256; ++n) input[n] = (float)(i * 256 + n + 1) / 65536.0f;
        own_block_cycle(&h, 256, in, out);
        own_block_drain(&h);
        memcpy(heard + i * 256, output, sizeof(output));
      }
      for (unsigned n = 0; n < 256 * 64; ++n)
        assert(heard[n] == (n < 1536 ? 0.0f : (float)(n - 1536 + 1) / 65536.0f * 0.5f));
      assert(report_latency(&h) && h.reported_latency == 1536);
      own_block_free(&h);
    }
    lv2_deactivate(&h);
    lv2_unload(&h);
  }
  puts("PASS: an LV2 plugin on its own block is told 1024 frames and runs exactly 1536 frames late");
}

int main(void) {
  // The fixtures' bundles are the only ones this test may see: LV2_PATH
  // names a fresh directory holding a link to each and nothing else.
  char bundle[PATH_MAX], directory[] = "/tmp/openxlr-lv2-XXXXXX", link[PATH_MAX], gain_link[PATH_MAX];
  assert(realpath("tests/latency.lv2", bundle));
  assert(mkdtemp(directory));
  snprintf(link, sizeof(link), "%s/latency.lv2", directory);
  assert(symlink(bundle, link) == 0);
  assert(realpath("tests/gain.lv2", bundle));
  snprintf(gain_link, sizeof(gain_link), "%s/gain.lv2", directory);
  assert(symlink(bundle, gain_link) == 0);
  setenv("LV2_PATH", directory, 1);
  Host h = {.rate = 48000, .channels = 2, .backend = &lv2_backend};
  char *arguments[] = {"urn:openxlr:test:latency"};
  assert(lv2_load(&h, arguments));
  assert(((Lv2 *)h.impl)->reports_latency);
  assert(lv2_activate(&h));

  // Nothing is said before the plugin has run once.
  assert(!report_latency(&h));
  cycle(&h);
  assert(lv2_latency(&h) == 480);
  assert(report_latency(&h) && h.reported_latency == 480);
  cycle(&h);
  assert(!report_latency(&h));
  puts("PASS: the latency port is reported once audio runs");

  Control *delay = control_named(&h, "delay");
  assert(delay);
  atomic_store(&delay->desired, 960.0f);
  cycle(&h);
  assert(report_latency(&h) && h.reported_latency == 960);
  assert(!report_latency(&h));
  puts("PASS: a latency the plugin changes while running is reported again");

  lv2_deactivate(&h);
  lv2_unload(&h);
  gain_fixture(1, "urn:openxlr:test:gain:mono");
  gain_fixture(2, "urn:openxlr:test:gain");
  puts("PASS: mono and stereo gain fixtures scale each audio channel");
  own_block_fixture("urn:openxlr:test:gain:mono");
  unlink(gain_link);
  unlink(link);
  rmdir(directory);
  return 0;
}
