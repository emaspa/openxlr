// SPDX-License-Identifier: GPL-3.0-only
// A C host for the C++ run-loop tests. The audio-thread atomics stay in C.
#define main host_program_main
#include "../host.c"
#undef main
#include <assert.h>

Host *test_host_new(void) {
  Host *h = calloc(1, sizeof(*h));
  assert(h);
  h->loop = pw_main_loop_new(NULL);
  assert(h->loop);
  return h;
}

void test_host_free(Host *h) {
  assert(!h->impl);
  pw_loop_enter(host_loop(h));
  assert(pw_loop_iterate(host_loop(h), 0) >= 0);
  pw_loop_leave(host_loop(h));
  pw_main_loop_destroy(h->loop);
  free(h);
}
