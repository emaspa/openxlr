// SPDX-License-Identifier: GPL-3.0-only
// The LV2 latency report, against a fixture whose latency port follows its
// own delay control. No PipeWire: the process calls are made here, on plain
// buffers, and lilv finds the fixture through LV2_PATH.
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

int main(void) {
  // The fixture's bundle is the only one this test may see: LV2_PATH names a
  // fresh directory holding a link to it and nothing else.
  char bundle[PATH_MAX], directory[] = "/tmp/openxlr-lv2-XXXXXX", link[PATH_MAX];
  assert(realpath("tests/latency.lv2", bundle));
  assert(mkdtemp(directory));
  snprintf(link, sizeof(link), "%s/latency.lv2", directory);
  assert(symlink(bundle, link) == 0);
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
  unlink(link);
  rmdir(directory);
  return 0;
}
