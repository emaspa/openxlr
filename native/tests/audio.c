// SPDX-License-Identifier: GPL-3.0-only
// Audio ports and the audio thread's safety rules, without a graph: a
// cycle the fixed buffers cannot carry is refused before anything is
// written, and a plugin stuck inside one process call is told apart from a
// node that simply has no work.
#include "../host.h"
#include <poll.h>
#include <stdio.h>
static float *test_buffers[MAX_CHANNELS * 2];
static void *test_dsp_buffer(void *port, uint32_t frames);
static Host *test_monitor_host;
static unsigned test_heartbeats;
static int test_monitor_poll(struct pollfd *fds, nfds_t count, int timeout);
static int test_host_puts(const char *text);
#define pw_filter_get_dsp_buffer test_dsp_buffer
#define poll test_monitor_poll
#define puts test_host_puts
#define main host_program_main
#include "../host.c"
#undef main
#undef pw_filter_get_dsp_buffer
#undef poll
#undef puts
#include <assert.h>

static int test_monitor_poll(struct pollfd *fds, nfds_t count, int timeout) {
  assert(test_monitor_host && count == 1 && timeout == 1000);
  atomic_store(&test_monitor_host->monitor_stop, true);
  return 0;
}

static int test_host_puts(const char *text) {
  if (!strcmp(text, "heartbeat")) ++test_heartbeats;
  return puts(text);
}

static void check_failed_monitor(void) {
  for (unsigned failed = 0; failed < 4; ++failed) {
    Host h = {0};
    atomic_store(&h.audio_error, failed != 0);
    if (failed == 1) atomic_store(&h.process_failed, true);
    if (failed == 2) atomic_store(&h.invalid_audio, AUDIO_INVALID_OUTPUT);
    if (failed == 3) atomic_store(&h.failure_explained, true);
    test_monitor_host = &h;
    test_heartbeats = 0;
    // No UI tick runs: an editor can block the main loop while the audio
    // callback has returned after reporting its failure.
    monitor_audio(&h);
    assert(test_heartbeats == (failed ? 0u : 1u));
    assert(atomic_load(&h.failure_explained) == (failed != 0));
  }
  test_monitor_host = NULL;
  puts("PASS: an idle host beats, but a failed host cannot hide behind a blocked editor");
}

// An output port as pw_filter keeps one (filter.c): two buffers, the free
// ones and the queued ones in order, and the io area PipeWire takes the
// buffer from. A cycle recycles the buffer in the io area if it was taken,
// the callback takes a free buffer and queues it, and the cycle ends with
// the oldest queued buffer put in the io area if that is free. Once its
// link is live PipeWire takes that buffer at the end of every cycle.
typedef struct {
  struct spa_io_buffers io;
  float data[2][32];
  uint32_t free[2], queued[2];
  unsigned free_count, queued_count;
  bool linked;
  float sent;  // the first sample of the buffer taken this cycle, or NAN
} FilterOutput;

static FilterOutput *filter_outputs;
static unsigned filter_output_count;

static uint32_t take_first(uint32_t *list, unsigned *count) {
  uint32_t first = list[0];
  for (unsigned i = 1; i < *count; ++i) list[i - 1] = list[i];
  --*count;
  return first;
}

static void filter_cycle(Host *h, struct spa_io_position *position) {
  for (unsigned i = 0; i < filter_output_count; ++i) {
    FilterOutput *p = &filter_outputs[i];
    if (p->io.buffer_id < 2 && p->io.status != SPA_STATUS_HAVE_DATA)
      p->free[p->free_count++] = p->io.buffer_id;
  }
  process_audio(h, position);
  for (unsigned i = 0; i < filter_output_count; ++i) {
    FilterOutput *p = &filter_outputs[i];
    if (p->io.status != SPA_STATUS_HAVE_DATA) {
      p->io.buffer_id = p->queued_count ? take_first(p->queued, &p->queued_count)
                                        : SPA_ID_INVALID;
      p->io.status = p->io.buffer_id != SPA_ID_INVALID ? SPA_STATUS_HAVE_DATA
                                                       : SPA_STATUS_NEED_DATA;
    }
    p->sent = NAN;
    if (p->linked) {
      if (p->io.status == SPA_STATUS_HAVE_DATA) p->sent = p->data[p->io.buffer_id][0];
      p->io.status = SPA_STATUS_NEED_DATA;
    }
  }
}

static void *test_dsp_buffer(void *port, uint32_t frames) {
  for (unsigned i = 0; i < filter_output_count; i++)
    if (port == &filter_outputs[i]) {
      FilterOutput *p = &filter_outputs[i];
      if (!p->free_count) return NULL;
      uint32_t id = take_first(p->free, &p->free_count);
      p->queued[p->queued_count++] = id;
      return p->data[id];
    }
  for (unsigned i = 0; i < MAX_CHANNELS * 2; i++)
    if (port == &test_buffers[i]) return test_buffers[i];
  return pw_filter_get_dsp_buffer(port, frames);
}

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

typedef struct {
  unsigned calls, channel;
  bool invalid_output;
  bool process_error;
  float invalid;
} AudioFixture;

static void gain_audio(Host *h, uint32_t frames, float *const *in,
                       float *const *out) {
  AudioFixture *fixture = h->impl;
  fixture->calls++;
  for (unsigned c = 0; c < h->channels; c++)
    for (uint32_t n = 0; n < frames; n++) out[c][n] = 2 * in[c][n];
  if (fixture->invalid_output)
    out[fixture->channel][frames - 1] = fixture->invalid;
  host_processing_result(h, !fixture->process_error, frames);
}

static void check_processing_failure(void) {
  const Backend gain = {.process = gain_audio};
  AudioFixture fixture = {.process_error = true};
  Host h = {.backend = &gain, .rate = 48000, .channels = 2, .impl = &fixture};
  for (unsigned c = 0; c < h.channels; ++c) {
    test_buffers[c] = guarded();
    test_buffers[MAX_CHANNELS + c] = guarded();
    h.in_ports[c] = &test_buffers[c];
    h.out_ports[c] = &test_buffers[MAX_CHANNELS + c];
  }
  struct spa_io_position position = {0};
  position.clock.duration = 32;
  position.clock.rate.denom = h.rate;
  // Refusals shorter than half a second are glitches, and a good block
  // starts the count again.
  const unsigned run = h.rate / 2 / 32;
  for (unsigned i = 1; i < run; ++i) process_audio(&h, &position);
  assert(!atomic_load(&h.audio_error));
  fixture.process_error = false;
  process_audio(&h, &position);
  fixture.process_error = true;
  for (unsigned i = 1; i < run; ++i) process_audio(&h, &position);
  assert(!atomic_load(&h.audio_error));
  process_audio(&h, &position);
  assert(atomic_load(&h.audio_error) && atomic_load(&h.process_failed));
  assert(!atomic_load(&h.invalid_audio) && fixture.calls == 2 * run);
  fixture.process_error = false;
  process_audio(&h, &position);
  assert(fixture.calls == 2 * run);
  for (unsigned i = 0; i < MAX_CHANNELS * 2; ++i) {
    if (i >= MAX_CHANNELS)
      for (unsigned n = 0; n < 32; ++n) assert(test_buffers[i][n] == 0);
    assert_canary(test_buffers[i]);
    free(test_buffers[i]);
    test_buffers[i] = NULL;
  }
  pw_init(NULL, NULL);
  h.loop = pw_main_loop_new(NULL);
  assert(h.loop);
  tick(&h, 1);
  assert(h.exit_code == 1);
  pw_main_loop_destroy(h.loop);
  pw_deinit();
  puts("PASS: half a second of processing errors silences every channel, stops callbacks and exits for recovery");
}

// outputs_unread is true only in a cycle where no main output had a buffer:
// one output with a buffer is enough for the node's outputs to be read.
static void check_outputs_unread(void) {
  const Backend gain = {.process = gain_audio};
  AudioFixture fixture = {0};
  Host h = {.backend = &gain, .rate = 48000, .channels = 2, .impl = &fixture};
  h.silence = guarded(), h.scratch = guarded();
  for (unsigned c = 0; c < h.channels; ++c) {
    test_buffers[c] = guarded();
    h.in_ports[c] = &test_buffers[c];
    h.out_ports[c] = &test_buffers[MAX_CHANNELS + c];
  }
  struct spa_io_position position = {0};
  position.clock.duration = 32;
  position.clock.rate.denom = h.rate;
  for (unsigned given = 0; given < 4; ++given) {
    for (unsigned c = 0; c < h.channels; ++c)
      test_buffers[MAX_CHANNELS + c] = (given >> c) & 1 ? guarded() : NULL;
    h.outputs_unread = given != 0;  // the opposite of what the cycle must say
    process_audio(&h, &position);
    assert(h.outputs_unread == (given == 0) && fixture.calls == given + 1);
    for (unsigned c = 0; c < h.channels; ++c) {
      free(test_buffers[MAX_CHANNELS + c]);
      test_buffers[MAX_CHANNELS + c] = NULL;
    }
  }
  for (unsigned c = 0; c < h.channels; ++c) {
    free(test_buffers[c]);
    test_buffers[c] = NULL;
  }
  assert_canary(h.silence), assert_canary(h.scratch);
  free(h.silence), free(h.scratch);
  puts("PASS: the main outputs count as unread only when none of them has a buffer");
}

// A link is made a port at a time, and PipeWire can run a cycle on a port
// whose buffers and io area it has given before it takes from it. The
// buffer of that cycle stays in the io area, and a buffer the host takes
// the cycle after is queued behind it: from then on that channel would play
// a cycle late, for as long as the link lasts. Here the left output has its
// buffer left untaken for one cycle, the right one does not, and from the
// cycle after that the two carry the same cycle's audio, every cycle.
static void check_output_left_untaken(void) {
  const Backend gain = {.process = gain_audio};
  AudioFixture fixture = {0};
  Host h = {.backend = &gain, .rate = 48000, .channels = 2, .impl = &fixture};
  h.silence = guarded(), h.scratch = guarded();
  FilterOutput outputs[2];
  memset(outputs, 0, sizeof(outputs));
  filter_outputs = outputs, filter_output_count = 2;
  for (unsigned c = 0; c < h.channels; ++c) {
    test_buffers[c] = guarded();
    h.in_ports[c] = &test_buffers[c];
    h.out_ports[c] = &outputs[c];
    outputs[c] = (FilterOutput){.io = {SPA_STATUS_NEED_DATA, SPA_ID_INVALID},
                                .free = {0, 1}, .free_count = 2, .linked = c == 1};
    port_io_changed(&h, &outputs[c], SPA_IO_Buffers, &outputs[c].io, sizeof(outputs[c].io));
  }
  struct spa_io_position position = {0};
  position.clock.duration = 32;
  position.clock.rate.denom = h.rate;
  for (unsigned cycle = 1; cycle <= 64; ++cycle) {
    for (unsigned c = 0; c < h.channels; ++c)
      for (unsigned n = 0; n < 32; ++n) test_buffers[c][n] = (float)cycle;
    filter_cycle(&h, &position);
    outputs[0].linked = true;
    assert(outputs[1].sent == 2 * (float)cycle);
    if (cycle > 2) assert(outputs[0].sent == outputs[1].sent);
  }
  for (unsigned c = 0; c < h.channels; ++c) {
    port_io_changed(&h, &outputs[c], SPA_IO_Buffers, NULL, 0);
    assert(!atomic_load(&h.out_io[c]));
    free(test_buffers[c]);
    test_buffers[c] = NULL;
  }
  filter_outputs = NULL, filter_output_count = 0;
  assert_canary(h.silence), assert_canary(h.scratch);
  free(h.silence), free(h.scratch);
  puts("PASS: an output whose buffer was left untaken for a cycle does not fall a cycle behind the other");
}

static void check_invalid_audio(void) {
  const Backend gain = {.process = gain_audio};
  const float invalid[] = {NAN, INFINITY, -INFINITY};
  const uint32_t sizes[] = {1, 32, 480, 512, 1024, MAX_FRAMES};
  for (unsigned i = 0; i < MAX_CHANNELS * 2; i++)
    test_buffers[i] = guarded();
  for (unsigned channels = 1; channels <= MAX_CHANNELS; channels++)
    for (unsigned size = 0; size < sizeof(sizes) / sizeof(sizes[0]); size++)
      for (unsigned kind = 0; kind < sizeof(invalid) / sizeof(invalid[0]); kind++)
        for (unsigned channel = 0; channel < channels; channel++)
          for (unsigned output = 0; output < 2; output++) {
            AudioFixture fixture = {.channel = channel,
                                    .invalid = invalid[kind]};
            Host h = {.backend = &gain, .rate = 48000, .channels = channels,
                      .impl = &fixture};
            for (unsigned i = 0; i < MAX_CHANNELS * 2; i++)
              for (uint32_t n = 0; n < MAX_FRAMES; n++)
                test_buffers[i][n] = -1;
            for (unsigned c = 0; c < channels; c++) {
              h.in_ports[c] = &test_buffers[c];
              h.out_ports[c] = &test_buffers[MAX_CHANNELS + c];
              for (uint32_t n = 0; n < sizes[size]; n++)
                test_buffers[c][n] = .75f;
            }
            struct spa_io_position position = {0};
            position.clock.duration = sizes[size];
            position.clock.rate.denom = h.rate;
            // Finite headroom above unity is audio, not a fault.
            process_audio(&h, &position);
            assert(fixture.calls == 1 && !atomic_load(&h.audio_error));
            for (unsigned c = 0; c < channels; c++)
              for (uint32_t n = 0; n < sizes[size]; n++)
                assert(test_buffers[MAX_CHANNELS + c][n] == 1.5f);
            if (!output) {
              // Invalid input reaches the plugin as silence at that sample
              // only, the host keeps running however long it lasts, and the
              // input buffer itself is left as it came.
              test_buffers[channel][sizes[size] - 1] = invalid[kind];
              const unsigned second = (h.rate + sizes[size] - 1) / sizes[size];
              for (unsigned b = 0; b < second; b++) {
                process_audio(&h, &position);
                assert(!atomic_load(&h.audio_error) && !atomic_load(&h.invalid_audio));
                for (unsigned c = 0; c < channels; c++)
                  for (uint32_t n = 0; n < sizes[size]; n++)
                    assert(test_buffers[MAX_CHANNELS + c][n] ==
                           (c == channel && n == sizes[size] - 1 ? 0 : 1.5f));
                assert(!isfinite(test_buffers[channel][sizes[size] - 1]));
              }
              assert(fixture.calls == 1 + second);
              assert(atomic_load(&h.nonfinite_input) == second);
              test_buffers[channel][sizes[size] - 1] = .75f;
              process_audio(&h, &position);
              assert(fixture.calls == 2 + second && !atomic_load(&h.audio_error));
              assert(atomic_load(&h.nonfinite_input) == second);
              for (unsigned c = 0; c < channels; c++)
                for (uint32_t n = 0; n < sizes[size]; n++)
                  assert(test_buffers[MAX_CHANNELS + c][n] == 1.5f);
              for (unsigned i = 0; i < MAX_CHANNELS * 2; i++) {
                assert_canary(test_buffers[i]);
                for (uint32_t n = sizes[size]; n < MAX_FRAMES; n++)
                  assert(test_buffers[i][n] == -1);
              }
              continue;
            }
            // Invalid output on sound stops the host after half a second of
            // it in a row, each block silenced.
            fixture.invalid_output = true;
            unsigned blocks = (h.rate / 2 + sizes[size] - 1) / sizes[size];
            for (unsigned b = 0; b < blocks; b++) {
              for (unsigned c = 0; c < channels; c++)
                test_buffers[MAX_CHANNELS + c][0] = -1;
              process_audio(&h, &position);
              assert(atomic_load(&h.audio_error) == (b + 1 == blocks));
              // The whole block is silent, including the unaffected channel.
              for (unsigned c = 0; c < channels; c++)
                for (uint32_t n = 0; n < sizes[size]; n++)
                  assert(test_buffers[MAX_CHANNELS + c][n] == 0);
            }
            assert(atomic_load(&h.invalid_audio) == AUDIO_INVALID_OUTPUT);
            assert(fixture.calls == 1u + blocks);
            // Until the main-loop tick exits, do not feed the poisoned
            // instance again.
            process_audio(&h, &position);
            assert(fixture.calls == 1u + blocks);
            for (unsigned i = 0; i < MAX_CHANNELS * 2; i++) {
              assert_canary(test_buffers[i]);
              for (uint32_t n = sizes[size]; n < MAX_FRAMES; n++)
                assert(test_buffers[i][n] == -1);
            }
          }
  for (unsigned i = 0; i < MAX_CHANNELS * 2; i++) {
    free(test_buffers[i]);
    test_buffers[i] = NULL;
  }
  puts("PASS: invalid input reaches a plugin as silence and never stops the host; invalid output silences the block, and half a second of it on sound retires the instance");
}

// An application that keeps sending NaN into a duck stage or an insert,
// here for three seconds of whole blocks on an in-place node: the plugin is
// called on every block and hears silence, nothing it writes is lost, the
// tick neither fails nor exits, and the trace says how many samples were
// replaced, once, not once a tick.
static void check_sustained_invalid_input(void) {
  const Backend gain = {.process = gain_audio};
  AudioFixture fixture = {0};
  Host h = {.backend = &gain, .rate = 48000, .channels = 2, .impl = &fixture};
  for (unsigned c = 0; c < h.channels; c++) {
    test_buffers[c] = guarded();
    h.in_ports[c] = h.out_ports[c] = &test_buffers[c];
  }
  struct spa_io_position position = {0};
  position.clock.duration = 256;
  position.clock.rate.denom = h.rate;
  const unsigned blocks = 3 * h.rate / 256;
  for (unsigned b = 0; b < blocks; b++) {
    for (unsigned c = 0; c < h.channels; c++)
      for (unsigned n = 0; n < 256; n++) test_buffers[c][n] = (b + n) & 1 ? NAN : -INFINITY;
    process_audio(&h, &position);
    for (unsigned c = 0; c < h.channels; c++)
      for (unsigned n = 0; n < 256; n++) assert(test_buffers[c][n] == 0);
    if (b % 30 == 0) {
      tick(&h, 1);
      assert(h.exit_code == 0);
    }
  }
  assert(fixture.calls == blocks && !atomic_load(&h.audio_error));
  assert(!atomic_load(&h.invalid_audio));
  assert(atomic_load(&h.nonfinite_input) == (uint64_t)blocks * 256 * h.channels);
  // The first block was traced at once; the rest wait for ten seconds.
  assert(h.traced_nonfinite == 256 * h.channels);
  assert(!trace_nonfinite_input(&h));
  h.traced_at.tv_sec -= 10;
  assert(trace_nonfinite_input(&h) && h.traced_nonfinite == atomic_load(&h.nonfinite_input));
  assert(!trace_nonfinite_input(&h));
  // Ordinary audio afterwards plays as it would have.
  for (unsigned c = 0; c < h.channels; c++)
    for (unsigned n = 0; n < 256; n++) test_buffers[c][n] = .25f;
  process_audio(&h, &position);
  for (unsigned c = 0; c < h.channels; c++) {
    for (unsigned n = 0; n < 256; n++) assert(test_buffers[c][n] == .5f);
    assert_canary(test_buffers[c]);
    free(test_buffers[c]);
    test_buffers[c] = NULL;
  }
  puts("PASS: three seconds of non-finite input play as silence, and the host keeps running and traces the count");
}

// The duck stage on a mix, the stage that used to end its helper on one NaN
// an application sent. It hears silence in its place, ducking or not, and
// goes on with the same gain it had.
static void check_duck_invalid_input(void) {
  assert(&duck_backend && !duck_backend.guards_input);
  Host h = {.backend = &duck_backend, .rate = 48000, .channels = 2};
  h.silence = guarded();
  assert(duck_backend.load(&h, NULL) && duck_backend.activate(&h));
  for (unsigned c = 0; c < h.channels; c++) {
    test_buffers[c] = guarded();
    test_buffers[MAX_CHANNELS + c] = guarded();
    h.in_ports[c] = &test_buffers[c];
    h.out_ports[c] = &test_buffers[MAX_CHANNELS + c];
  }
  struct spa_io_position position = {0};
  position.clock.duration = 256;
  position.clock.rate.denom = h.rate;
  const float invalid[] = {NAN, INFINITY, -INFINITY};
  for (unsigned b = 0; b < h.rate / 256; b++) {
    for (unsigned c = 0; c < h.channels; c++)
      for (unsigned n = 0; n < 256; n++) test_buffers[c][n] = c ? -.5f : .5f;
    test_buffers[b & 1][b % 256] = invalid[b % 3];
    process_audio(&h, &position);
    assert(!atomic_load(&h.audio_error));
    for (unsigned c = 0; c < h.channels; c++)
      for (unsigned n = 0; n < 256; n++)
        assert(test_buffers[MAX_CHANNELS + c][n] ==
               (c == (b & 1) && n == b % 256 ? 0 : c ? -.5f : .5f));
  }
  tick(&h, 1);
  assert(h.exit_code == 0 && atomic_load(&h.nonfinite_input) == h.rate / 256);
  duck_backend.deactivate(&h);
  duck_backend.unload(&h);
  for (unsigned i = 0; i < MAX_CHANNELS * 2; i++) {
    assert_canary(test_buffers[i]);
    free(test_buffers[i]);
    test_buffers[i] = NULL;
  }
  assert_canary(h.silence);
  free(h.silence);
  puts("PASS: the duck stage hears a non-finite input sample as silence and keeps running");
}

// A backend that zeroes non-finite input itself, as the microphone stage
// does, gets the block and keeps running; what it puts out is still checked.
static void zeroing_gain(Host *h, uint32_t frames, float *const *in, float *const *out) {
  AudioFixture *fixture = h->impl;
  fixture->calls++;
  for (unsigned c = 0; c < h->channels; c++)
    for (uint32_t n = 0; n < frames; n++) out[c][n] = isfinite(in[c][n]) ? 2 * in[c][n] : 0;
  if (fixture->invalid_output) out[fixture->channel][frames - 1] = fixture->invalid;
}

static void check_guarded_input(void) {
  const Backend guarded_backend = {.process = zeroing_gain, .guards_input = true};
  AudioFixture fixture = {.invalid = NAN};
  Host h = {.backend = &guarded_backend, .rate = 48000, .channels = 1, .impl = &fixture};
  test_buffers[0] = guarded();
  test_buffers[MAX_CHANNELS] = guarded();
  h.in_ports[0] = &test_buffers[0];
  h.out_ports[0] = &test_buffers[MAX_CHANNELS];
  struct spa_io_position position = {0};
  position.clock.duration = 32;
  position.clock.rate.denom = h.rate;
  const float invalid[] = {NAN, INFINITY, -INFINITY};
  for (unsigned kind = 0; kind < 3; kind++) {
    for (unsigned n = 0; n < 32; n++) test_buffers[0][n] = .25f;
    test_buffers[0][7] = invalid[kind];
    process_audio(&h, &position);
    assert(!atomic_load(&h.audio_error) && !atomic_load(&h.invalid_audio));
    assert(fixture.calls == kind + 1);
    for (unsigned n = 0; n < 32; n++) assert(test_buffers[MAX_CHANNELS][n] == (n == 7 ? 0 : .5f));
  }
  // Its output is held to the usual rule: non-finite output is silenced.
  fixture.invalid_output = true;
  process_audio(&h, &position);
  for (unsigned n = 0; n < 32; n++) assert(test_buffers[MAX_CHANNELS][n] == 0);
  assert_canary(test_buffers[0]);
  assert_canary(test_buffers[MAX_CHANNELS]);
  free(test_buffers[0]), free(test_buffers[MAX_CHANNELS]);
  test_buffers[0] = test_buffers[MAX_CHANNELS] = NULL;
  puts("PASS: a backend that guards its own input gets non-finite samples and keeps running; its output is still checked");
}

// Feed `blocks` blocks of 256 frames at `level`, the plugin adding a NaN to
// each when `nan`, and check what leaves the host.
static void feed(Host *h, float *const *out, float level, bool nan,
                 unsigned blocks) {
  AudioFixture *fixture = h->impl;
  struct spa_io_position position = {0};
  position.clock.duration = 256;
  position.clock.rate.denom = h->rate;
  fixture->invalid_output = nan;
  for (unsigned b = 0; b < blocks; b++) {
    for (unsigned c = 0; c < h->channels; c++)
      for (unsigned n = 0; n < 256; n++) test_buffers[c][n] = level;
    process_audio(h, &position);
    for (unsigned c = 0; c < h->channels; c++)
      for (unsigned n = 0; n < 256; n++)
        assert(out[c][n] == (nan || atomic_load(&h->audio_error) ? 0 : 2 * level));
  }
}

// Issue 577: a denoiser that puts out NaN after a minute of a muted
// microphone's pure zeros, and recovers a tenth of a second after sound
// comes back, keeps running and keeps its restart budget.
static void check_invalid_output_on_silence(void) {
  const Backend gain = {.process = gain_audio};
  for (unsigned inplace = 0; inplace < 2; inplace++) {
    AudioFixture fixture = {.invalid = NAN};
    Host h = {.backend = &gain, .rate = 48000, .channels = 2, .impl = &fixture};
    for (unsigned c = 0; c < h.channels; c++) {
      test_buffers[c] = guarded();
      test_buffers[MAX_CHANNELS + c] = guarded();
      h.in_ports[c] = &test_buffers[c];
      h.out_ports[c] = inplace ? &test_buffers[c] : &test_buffers[MAX_CHANNELS + c];
    }
    float *const out[] = {*(float **)h.out_ports[0], *(float **)h.out_ports[1]};
    const unsigned second = 48000 / 256, half = second / 2;
    feed(&h, out, 0, false, 78 * second);  // muted: clean at first
    feed(&h, out, 0, true, 180 * second);  // then NaN for three minutes of silence
    assert(!atomic_load(&h.audio_error));
    feed(&h, out, .25f, true, 21);         // unmuted: a tenth of a second to recover
    feed(&h, out, .25f, false, second);
    assert(!atomic_load(&h.audio_error) && !atomic_load(&h.invalid_audio));
    // On sound the run counts; silence in the middle neither counts nor
    // clears it, and a clean block does.
    feed(&h, out, .25f, true, half - 2);
    feed(&h, out, 0, true, second);
    feed(&h, out, .25f, false, 1);
    feed(&h, out, .25f, true, half - 1);
    assert(!atomic_load(&h.audio_error));
    feed(&h, out, .25f, true, 1);
    assert(!atomic_load(&h.audio_error));
    feed(&h, out, 0, true, second);
    feed(&h, out, .25f, true, 1);
    assert(atomic_load(&h.audio_error));
    assert(atomic_load(&h.invalid_audio) == AUDIO_INVALID_OUTPUT);
    for (unsigned i = 0; i < MAX_CHANNELS * 2; i++) {
      if (test_buffers[i]) assert_canary(test_buffers[i]);
      free(test_buffers[i]);
      test_buffers[i] = NULL;
    }
  }
  puts("PASS: invalid output on pure silence is silenced without counting, so a plugin that recovers on sound keeps running");
}

static void check_inplace_audio(void) {
  const Backend gain = {.process = gain_audio};
  AudioFixture fixture = {0};
  Host h = {.backend = &gain, .rate = 48000, .channels = 2,
            .impl = &fixture};
  for (unsigned c = 0; c < h.channels; c++) {
    test_buffers[c] = guarded();
    h.in_ports[c] = h.out_ports[c] = &test_buffers[c];
    for (unsigned n = 0; n < 512; n++) test_buffers[c][n] = .75f;
  }
  struct spa_io_position position = {0};
  position.clock.duration = 512;
  position.clock.rate.denom = h.rate;
  process_audio(&h, &position);
  assert(!atomic_load(&h.audio_error) && fixture.calls == 1);
  for (unsigned c = 0; c < h.channels; c++)
    for (unsigned n = 0; n < 512; n++) assert(test_buffers[c][n] == 1.5f);
  fixture.invalid_output = true;
  fixture.invalid = NAN;
  const unsigned run = h.rate / 2 / 512 + 1;
  for (unsigned i = 0; i < run; i++) {
    for (unsigned c = 0; c < h.channels; c++)
      for (unsigned n = 0; n < 512; n++) test_buffers[c][n] = .75f;
    process_audio(&h, &position);
  }
  assert(atomic_load(&h.audio_error) && fixture.calls == 1 + run);
  for (unsigned c = 0; c < h.channels; c++) {
    for (unsigned n = 0; n < 512; n++) assert(test_buffers[c][n] == 0);
    assert_canary(test_buffers[c]);
    free(test_buffers[c]);
    test_buffers[c] = NULL;
  }
  puts("PASS: in-place buffers keep finite audio and silence invalid output");
}

static void check_key_silence(Host *h, uint32_t frames, float *const *in,
                              float *const *out) {
  assert(host_key(h) == h->silence);
  for (uint32_t n = 0; n < frames; n++) assert(host_key(h)[n] == 0);
}

// A tap output PipeWire gave no buffer writes into the host's own spare for
// that tap, never into the main outputs' scratch or another tap's.
static void check_tap_fallback(Host *h, uint32_t frames, float *const *in,
                               float *const *out) {
  for (unsigned t = 0; t < MAX_TAPS; t++) {
    if (t >= h->tap_count) {
      assert(h->taps[t] == NULL);
      continue;
    }
    assert(h->taps[t] == h->tap_scratch[t] && h->taps[t] != h->scratch);
    for (uint32_t n = 0; n < frames; n++) h->taps[t][n] = 0.25f * (float)(t + 1);
  }
}

static const TapOutput one_tap[] = {{"tap_0", "MONO"}, {NULL, NULL}};
static const TapOutput stereo_taps[] = {{"audition_0", "FL"}, {"audition_1", "FR"}, {NULL, NULL}};
static const TapOutput too_many_taps[] = {{"a", "MONO"}, {"b", "MONO"}, {"c", "MONO"}, {NULL, NULL}};

static void check_ports(void) {
  pw_init(NULL, NULL);
  struct pw_main_loop *loop = pw_main_loop_new(NULL);
  assert(loop);
  const Backend keyed = {.process = check_key_silence, .key_inputs = 1};
  const Backend tapped = {.process = check_tap_fallback, .tap_outputs = one_tap};
  const Backend paired = {.process = check_tap_fallback, .tap_outputs = stereo_taps};
  const Backend *backends[] = {&lv2_backend, &clap_backend, &vst3_backend, &keyed, &tapped, &paired};
  for (unsigned i = 0; i < sizeof(backends) / sizeof(backends[0]); i++) {
    Host h = {.backend = backends[i], .rate = 48000, .channels = 2};
    h.filter = pw_filter_new_simple(pw_main_loop_get_loop(loop), "port-test",
                                    NULL, &filter_events, &h);
    assert(h.filter && add_audio_ports(&h));
    for (unsigned c = 0; c < h.channels; c++) {
      assert(h.in_ports[c] && h.out_ports[c]);
      const struct pw_properties *in = pw_filter_get_properties(h.filter, h.in_ports[c]);
      const struct pw_properties *out = pw_filter_get_properties(h.filter, h.out_ports[c]);
      assert(!strcmp(pw_properties_get(in, PW_KEY_PORT_NAME), c ? "playback_1" : "playback_0"));
      assert(!strcmp(pw_properties_get(out, PW_KEY_PORT_NAME), c ? "capture_1" : "capture_0"));
    }
    if (h.backend != &tapped && h.backend != &paired) {
      assert(h.backend->tap_outputs == NULL && h.tap_count == 0 && h.tap_ports[0] == NULL);
    } else {
      // The taps in the order the backend declared them, each with its own
      // name and channel position, and each with a spare of its own.
      const TapOutput *declared = h.backend->tap_outputs;
      assert(h.tap_count == (h.backend == &paired ? 2u : 1u));
      for (unsigned t = 0; t < h.tap_count; t++) {
        assert(h.tap_ports[t]);
        const struct pw_properties *tap = pw_filter_get_properties(h.filter, h.tap_ports[t]);
        assert(!strcmp(pw_properties_get(tap, PW_KEY_PORT_NAME), declared[t].port));
        assert(!strcmp(pw_properties_get(tap, PW_KEY_AUDIO_CHANNEL), declared[t].channel));
        assert(!strcmp(pw_properties_get(tap, PW_KEY_FORMAT_DSP), "32 bit float mono audio"));
      }
      h.silence = guarded(), h.scratch = guarded();
      for (unsigned t = 0; t < MAX_TAPS; t++) h.tap_scratch[t] = guarded();
      struct spa_io_position position = {0};
      position.clock.duration = 256;
      position.clock.rate.denom = h.rate;
      process_audio(&h, &position);
      // Nothing is linked here, so no main output had a buffer to give.
      assert(atomic_load(&h.audio_left) == 1 && h.outputs_unread);
      for (unsigned t = 0; t < MAX_TAPS; t++)
        assert(h.tap_scratch[t][255] == (t < h.tap_count ? 0.25f * (float)(t + 1) : -1.0f));
      assert_canary(h.silence), assert_canary(h.scratch);
      for (unsigned t = 0; t < MAX_TAPS; t++) assert_canary(h.tap_scratch[t]), free(h.tap_scratch[t]);
      free(h.silence), free(h.scratch);
    }
    if (h.backend != &keyed) {
      assert(h.backend->key_inputs == 0 && h.key_ports[0] == NULL);
    } else {
      assert(h.key_ports[0] && !h.key_ports[1]);
      const struct pw_properties *key = pw_filter_get_properties(h.filter, h.key_ports[0]);
      assert(!strcmp(pw_properties_get(key, PW_KEY_PORT_NAME), "key_0"));
      assert(!strcmp(pw_properties_get(key, PW_KEY_AUDIO_CHANNEL), "MONO"));
      assert(!strcmp(pw_properties_get(key, PW_KEY_FORMAT_DSP), "32 bit float mono audio"));
      h.silence = guarded(), h.scratch = guarded();
      struct spa_io_position position = {0};
      position.clock.duration = 256;
      position.clock.rate.denom = h.rate;
      process_audio(&h, &position);
      assert(atomic_load(&h.audio_entered) == 1 && atomic_load(&h.audio_left) == 1);
      assert_canary(h.silence), assert_canary(h.scratch);
      free(h.silence), free(h.scratch);
    }
    pw_filter_destroy(h.filter);
  }
  // More taps than the host has room for refuse the node.
  const Backend crowded = {.process = check_tap_fallback, .tap_outputs = too_many_taps};
  Host h = {.backend = &crowded, .rate = 48000, .channels = 2};
  h.filter = pw_filter_new_simple(pw_main_loop_get_loop(loop), "port-test", NULL, &filter_events, &h);
  assert(h.filter && !add_audio_ports(&h) && h.tap_count == MAX_TAPS);
  pw_filter_destroy(h.filter);
  pw_main_loop_destroy(loop);
  pw_deinit();
  puts("PASS: existing backends get no key or tap port; a keyed backend gets mono key_0 and silence "
       "without a buffer, a tapped one mono tap_0, a paired one audition_0 and audition_1, each tap "
       "its own spare, and a backend past the tap room is refused");
}

int main(void) {
  check_ports();
  check_failed_monitor();
  check_processing_failure();
  check_outputs_unread();
  check_output_left_untaken();
  check_invalid_audio();
  check_sustained_invalid_input();
  check_duck_invalid_input();
  check_guarded_input();
  check_inplace_audio();
  check_invalid_output_on_silence();
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

  // A failure a backend explains is marked, so the exit path does not write
  // the audio thread's reason over it; the audio thread's own stays unmarked.
  assert(!atomic_load(&h.audio_error) && !atomic_load(&h.failure_explained));
  host_fail(&h, "the plugin asked to be reloaded");
  assert(atomic_load(&h.audio_error) && atomic_load(&h.failure_explained));
  puts("PASS: an explained failure keeps its own reason");
  return 0;
}
