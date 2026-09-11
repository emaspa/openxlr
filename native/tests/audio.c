// SPDX-License-Identifier: GPL-3.0-only
// The audio thread's two safety rules, without a graph to run them in: a
// cycle the fixed buffers cannot carry is refused before anything is
// written, and a plugin stuck inside one process call is told apart from a
// node that simply has no work.
#define main host_program_main
#include "../host.c"
#undef main
#include <assert.h>

// A canary past the end of a fallback buffer. Nothing may reach it.
enum { CANARY = 64 };

static float *guarded(void) {
  float *buffer = malloc(sizeof(float) * (MAX_FRAMES + CANARY));
  assert(buffer);
  for (unsigned i = 0; i < MAX_FRAMES + CANARY; ++i)
    buffer[i] = -1.0f;
  return buffer;
}

static void assert_canary(const float *buffer) {
  for (unsigned i = MAX_FRAMES; i < MAX_FRAMES + CANARY; ++i)
    assert(buffer[i] == -1.0f);
}

int main(void) {
  Host h = {.rate = 48000, .channels = 2};

  // The quantum the graph runs at decides whether the cycle can happen at
  // all. Anything longer than the fallback buffers, or a sample rate the
  // plugin was not activated for, has to be turned away.
  assert(host_quantum_supported(&h, 1024, 48000));
  assert(host_quantum_supported(&h, MAX_FRAMES, 48000));
  assert(!host_quantum_supported(&h, MAX_FRAMES + 1, 48000));
  assert(!host_quantum_supported(&h, 65536, 48000));
  assert(!host_quantum_supported(&h, 1024, 44100));
  puts("PASS: an oversized quantum or a changed rate refuses the cycle");

  // And the write itself is bounded, so the refusal above is not the only
  // thing standing between a large quantum and the end of the buffer.
  float *silence = guarded();
  assert(host_fallback(silence, 512, true) == silence);
  for (unsigned i = 0; i < 512; ++i)
    assert(silence[i] == 0.0f);
  assert(silence[512] == -1.0f);
  assert_canary(silence);
  assert(host_fallback(silence, MAX_FRAMES * 8, true) == silence);
  assert_canary(silence);
  free(silence);
  puts("PASS: the fallback buffers are never written past their end");

  // A plugin that blocks inside its own process call leaves the monitor
  // thread and the editor loop untouched, so the beat has to come from the
  // audio callback's own progress. Entered and left differ only while a
  // call is outstanding.
  uint64_t entered = 0, left = 0, last = 0;
  unsigned outstanding = 0;
  const unsigned window = 6;

  // A node with no work: nothing outstanding, never stuck, however long.
  for (unsigned poll = 0; poll < 100; ++poll)
    assert(!host_audio_stuck(entered, left, &last, &outstanding, window));
  puts("PASS: an idle or suspended node is never taken for a stuck one");

  // A node processing normally: every poll sees a different call, or none.
  for (unsigned poll = 0; poll < 100; ++poll) {
    ++entered;
    assert(!host_audio_stuck(entered, left, &last, &outstanding, window));
    ++left;
    assert(!host_audio_stuck(entered, left, &last, &outstanding, window));
  }
  puts("PASS: a callback that returns keeps the process healthy");

  // A slow but progressing callback: outstanding at every poll, but a
  // different one each time.
  for (unsigned poll = 0; poll < 100; ++poll) {
    ++entered;
    assert(!host_audio_stuck(entered, left, &last, &outstanding, window));
    ++left;
  }
  puts("PASS: a slow callback that keeps arriving is not a stall");

  // The stall: one call, outstanding across poll after poll.
  ++entered;
  for (unsigned poll = 1; poll < window; ++poll)
    assert(!host_audio_stuck(entered, left, &last, &outstanding, window));
  assert(host_audio_stuck(entered, left, &last, &outstanding, window));
  assert(host_audio_stuck(entered, left, &last, &outstanding, window));
  puts("PASS: one callback outstanding across the whole window reads as stuck");

  // And it recovers: the plugin returns, and the next call is a fresh one.
  ++left;
  assert(!host_audio_stuck(entered, left, &last, &outstanding, window));
  ++entered;
  assert(!host_audio_stuck(entered, left, &last, &outstanding, window));
  puts("PASS: a callback that comes back is healthy again");
  return 0;
}
