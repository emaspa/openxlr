// SPDX-License-Identifier: GPL-3.0-only
// The plugin's own block, without a graph: the audio callback is called here
// on plain buffers, and the worker's part is either run by hand between
// cycles, which decides exactly when each block is answered, or by the real
// worker thread. It checks that the output is the input delayed by exactly
// the delay the host reports, that a block answered within its time is never
// heard late, that a late block costs its own frames and nothing more, that a
// spike is bounded silence followed by the same exact alignment, and that the
// callback returns while the plugin is held inside a call.
#include "../host.h"
#include <stdio.h>
static float *test_buffers[MAX_CHANNELS * 2];
static void *test_dsp_buffer(void *port, uint32_t frames);
#define pw_filter_get_dsp_buffer test_dsp_buffer
#define main host_program_main
#include "../host.c"
#undef main
#undef pw_filter_get_dsp_buffer
#include <assert.h>

static void *test_dsp_buffer(void *port, uint32_t frames) {
  for (unsigned i = 0; i < MAX_CHANNELS * 2; i++)
    if (port == &test_buffers[i]) return test_buffers[i];
  return NULL;
}

// The plugin: a copy of its input, plus whatever the test asks for.
typedef struct {
  _Atomic unsigned calls;
  _Atomic bool hold;     // a call waits inside the plugin while this is set
  _Atomic bool holding;  // a call is waiting
  _Atomic uint32_t cost_us, spike_call, spike_us;
  _Atomic bool wrong_size, wrong_thread, check_thread;
  _Atomic unsigned invalid_call;  // this call puts out NaN
  uint32_t latency;
} Fixture;

static void sleep_us(uint32_t us) {
  struct timespec t = {us / 1000000, (long)(us % 1000000) * 1000};
  while (nanosleep(&t, &t) != 0) {}
}

static void copy_process(Host *h, uint32_t frames, float *const *in,
                         float *const *out) {
  Fixture *f = h->impl;
  if (frames != OWN_BLOCK_FRAMES) atomic_store(&f->wrong_size, true);
  if (atomic_load(&f->check_thread) && !host_on_audio_thread(h))
    atomic_store(&f->wrong_thread, true);
  unsigned call = atomic_fetch_add(&f->calls, 1) + 1;
  if (atomic_load(&f->hold)) {
    atomic_store(&f->holding, true);
    while (atomic_load(&f->hold)) sleep_us(200);
    atomic_store(&f->holding, false);
  }
  uint32_t cost = atomic_load(&f->cost_us);
  if (call == atomic_load(&f->spike_call)) cost = atomic_load(&f->spike_us);
  if (cost) sleep_us(cost);
  for (unsigned c = 0; c < h->channels; ++c)
    for (uint32_t n = 0; n < frames; ++n)
      out[c][n] = call == atomic_load(&f->invalid_call) ? NAN : in[c][n];
  host_processing_result(h, true, frames);
}

static uint32_t fixture_latency(Host *h) { return ((Fixture *)h->impl)->latency; }

static const Backend copy_backend = {.name = "copy", .process = copy_process,
                                     .latency = fixture_latency};

// A signal that is never zero and names its own position, exactly, for the
// length of any test: silence, a misplaced frame and a repeated one are all
// told apart from the right frame.
static float signal_at(uint64_t position, unsigned channel) {
  return (float)((position & 0xfffff) + 1 + (channel ? 0x100000 : 0)) /
         (float)(1 << 22);
}

static uint64_t position_of(float value) {
  return (uint64_t)(value * (float)(1 << 22)) - 1;
}

enum { RECORD = 48000 * 20 };

typedef struct {
  Host h;
  Fixture f;
  uint64_t position;
  float *record[MAX_CHANNELS];
  double slowest_cycle_ms;
} Rig;

static double now_ms(void) {
  struct timespec t;
  clock_gettime(CLOCK_MONOTONIC, &t);
  return t.tv_sec * 1e3 + t.tv_nsec / 1e6;
}

static void rig_open(Rig *r, unsigned channels) {
  memset(r, 0, sizeof(*r));
  r->h.backend = &copy_backend;
  r->h.impl = &r->f;
  r->h.rate = 48000;
  r->h.channels = channels;
  r->h.own_block = true;
  r->f.latency = UINT32_MAX;
  r->h.silence = calloc(MAX_FRAMES, sizeof(float));
  r->h.scratch = calloc(MAX_FRAMES, sizeof(float));
  assert(r->h.silence && r->h.scratch && own_block_init(&r->h));
  for (unsigned c = 0; c < channels; ++c) {
    test_buffers[c] = calloc(MAX_FRAMES, sizeof(float));
    test_buffers[MAX_CHANNELS + c] = calloc(MAX_FRAMES, sizeof(float));
    r->record[c] = calloc(RECORD, sizeof(float));
    assert(test_buffers[c] && test_buffers[MAX_CHANNELS + c] && r->record[c]);
    r->h.in_ports[c] = &test_buffers[c];
    r->h.out_ports[c] = &test_buffers[MAX_CHANNELS + c];
  }
}

static void rig_close(Rig *r) {
  own_block_stop(&r->h);
  own_block_free(&r->h);
  for (unsigned c = 0; c < r->h.channels; ++c) {
    free(test_buffers[c]), free(test_buffers[MAX_CHANNELS + c]), free(r->record[c]);
    test_buffers[c] = test_buffers[MAX_CHANNELS + c] = NULL;
  }
  free(r->h.silence), free(r->h.scratch);
}

// One PipeWire cycle: this cycle's input in, the callback, its output kept.
static void cycle(Rig *r, uint32_t frames) {
  assert(r->position + frames <= RECORD);
  for (unsigned c = 0; c < r->h.channels; ++c)
    for (uint32_t n = 0; n < frames; ++n)
      test_buffers[c][n] = signal_at(r->position + n, c);
  struct spa_io_position position = {0};
  position.clock.duration = frames;
  position.clock.rate.denom = r->h.rate;
  double started = now_ms();
  process_audio(&r->h, &position);
  double took = now_ms() - started;
  if (took > r->slowest_cycle_ms) r->slowest_cycle_ms = took;
  for (unsigned c = 0; c < r->h.channels; ++c)
    memcpy(r->record[c] + r->position, test_buffers[MAX_CHANNELS + c],
           sizeof(float) * frames);
  r->position += frames;
}

// What the output from `from` to `to` holds against the input `delay` late:
// frames that are exactly the input, frames that are silence, and anything
// else, which must never happen.
typedef struct {
  uint64_t exact, silent, wrong, longest_silence;
} Tally;

static Tally tally(const Rig *r, uint64_t from, uint64_t to, uint32_t delay) {
  Tally t = {0};
  uint64_t run = 0;
  for (uint64_t at = from; at < to; ++at) {
    bool exact = at >= delay, silent = true;
    for (unsigned c = 0; c < r->h.channels; ++c) {
      float got = r->record[c][at];
      if (got != 0) silent = false;
      if (at < delay || got != signal_at(at - delay, c)) exact = false;
    }
    if (exact) t.exact++;
    else if (silent) t.silent++;
    else t.wrong++;
    run = silent && !exact ? run + 1 : 0;
    if (run > t.longest_silence) t.longest_silence = run;
  }
  return t;
}

static void check_exact(void) {
  for (unsigned channels = 1; channels <= MAX_CHANNELS; ++channels) {
    Rig r;
    rig_open(&r, channels);
    for (unsigned i = 0; i < 2000; ++i) {
      cycle(&r, 256);
      own_block_drain(&r.h);  // a worker that answers at once
    }
    const uint32_t delay = 1536;
    assert(atomic_load(&r.h.block.delay) == delay);
    Tally before = tally(&r, 0, delay, delay);
    assert(before.silent == delay);
    Tally after = tally(&r, delay, r.position, delay);
    assert(after.exact == r.position - delay && after.wrong == 0);
    assert(atomic_load(&r.h.block.short_cycles) == 0);
    assert(atomic_load(&r.h.block.dropped_blocks) == 0);
    assert(!atomic_load(&r.f.wrong_size));
    assert(atomic_load(&r.f.calls) == r.position / OWN_BLOCK_FRAMES);
    assert(atomic_load(&r.h.audio_entered) == atomic_load(&r.h.audio_left));
    rig_close(&r);
  }
  puts("PASS: the output is the input 1536 frames late, sample for sample, "
       "and the plugin hears only whole blocks of 1024");
}

// The worker has until the block is due: a block completes at the end of
// cycle 4k+3 and starts playing in cycle 4k+6, three cycles of 256 later.
// Answered just before that cycle, nothing is lost; one cycle later, the
// late block's first cycle is silence and every other frame is still exact.
static void check_budget(void) {
  for (int late = 0; late < 2; ++late) {
    Rig r;
    rig_open(&r, 2);
    for (unsigned i = 0; i < 2000; ++i) {
      if (i % 4 == (late ? 3u : 2u)) own_block_drain(&r.h);
      cycle(&r, 256);
    }
    Tally t = tally(&r, 1536, r.position, 1536);
    assert(t.wrong == 0);
    if (!late) {
      assert(t.exact == r.position - 1536);
      assert(atomic_load(&r.h.block.short_cycles) == 0);
    } else {
      assert(atomic_load(&r.h.block.short_cycles) > 400);
      assert(t.longest_silence == 256 && t.exact > (r.position - 1536) * 7 / 10);
      assert(atomic_load(&r.h.block.dropped_blocks) == 0);
    }
    rig_close(&r);
  }
  puts("PASS: a block answered within 768 frames of completing is never heard "
       "late; one answered later costs its own late frames and nothing else");
}

// The worker stops answering for ten cycles, 53 ms, then catches up. The
// blocks whose time passed meanwhile are dropped rather than played late,
// the silence is no longer than the spike, and the alignment afterwards is
// the one before.
static void check_spike(void) {
  Rig r;
  rig_open(&r, 2);
  const unsigned spike_from = 1000, spike_cycles = 10;
  for (unsigned i = 0; i < 3000; ++i) {
    cycle(&r, 256);
    if (i < spike_from || i >= spike_from + spike_cycles) own_block_drain(&r.h);
  }
  Tally t = tally(&r, 1536, r.position, 1536);
  assert(t.wrong == 0);
  assert(t.longest_silence > 0 && t.longest_silence <= spike_cycles * 256);
  assert(t.silent == t.longest_silence);
  assert(atomic_load(&r.h.block.dropped_blocks) > 0);
  // Everything after the spike is exact again.
  uint64_t recovered = (uint64_t)(spike_from + spike_cycles + 8) * 256;
  Tally after = tally(&r, recovered, r.position, 1536);
  assert(after.exact == r.position - recovered);
  assert(atomic_load(&r.h.block.delay) == 1536);
  printf("PASS: a 53 ms stall is %llu frames of silence, %llu blocks dropped, "
         "then the same exact alignment\n",
         (unsigned long long)t.longest_silence,
         (unsigned long long)atomic_load(&r.h.block.dropped_blocks));
  rig_close(&r);
}

// A stall long enough to fill the input ring: the callback keeps going and
// what it could not keep is answered with silence, never with stale audio.
static void check_full_ring(void) {
  Rig r;
  rig_open(&r, 2);
  for (unsigned i = 0; i < 400; ++i) {
    cycle(&r, 256);
    own_block_drain(&r.h);
  }
  for (unsigned i = 0; i < 300; ++i) cycle(&r, 256);  // 1.6 s, no answers
  assert(atomic_load(&r.h.block.lost) > 0);
  for (unsigned i = 0; i < 400; ++i) {
    cycle(&r, 256);
    own_block_drain(&r.h);
  }
  Tally t = tally(&r, 1536, r.position, 1536);
  assert(t.wrong == 0);
  uint64_t recovered = 720 * 256;
  Tally after = tally(&r, recovered, r.position, 1536);
  assert(after.exact == r.position - recovered);
  rig_close(&r);
  puts("PASS: a stall that fills the ring is silence, never older audio, "
       "and the alignment comes back");
}

// A larger cycle lengthens the delay at once, by inserting silence; a
// smaller one shortens it after two seconds, by skipping. No frame is ever
// played twice or out of order.
static void check_quantum_change(void) {
  Rig r;
  rig_open(&r, 1);
  for (unsigned i = 0; i < 400; ++i) {
    cycle(&r, 256);
    own_block_drain(&r.h);
  }
  assert(atomic_load(&r.h.block.delay) == 1536);
  for (unsigned i = 0; i < 100; ++i) {
    cycle(&r, 1024);
    own_block_drain(&r.h);
    assert(atomic_load(&r.h.block.delay) == 2048);
  }
  uint64_t big_end = r.position;
  for (unsigned i = 0; i < 300; ++i) {
    cycle(&r, 256);
    own_block_drain(&r.h);
  }
  assert(atomic_load(&r.h.block.delay) == 2048);  // 1.6 s is not yet 2 s
  for (unsigned i = 0; i < 200; ++i) {
    cycle(&r, 256);
    own_block_drain(&r.h);
  }
  assert(atomic_load(&r.h.block.delay) == 1536);
  for (unsigned i = 0; i < 50; ++i) {
    cycle(&r, 2048);
    own_block_drain(&r.h);
  }
  assert(atomic_load(&r.h.block.delay) == 1024 + 2048);
  // Every frame that plays is some input frame, each at most once and in
  // order; the gaps are where the delay grew.
  uint64_t last = 0;
  bool any = false;
  for (uint64_t at = 0; at < r.position; ++at) {
    float got = r.record[0][at];
    if (got == 0) continue;
    uint64_t found = position_of(got);
    assert(found <= at && signal_at(found, 0) == got);
    assert(!any || found > last);
    last = found;
    any = true;
  }
  Tally steady = tally(&r, big_end + 2 * 48000 + 4096, big_end + 500 * 256 - 1, 1536);
  assert(steady.wrong == 0 && steady.silent == 0);
  rig_close(&r);
  puts("PASS: the delay follows the cycle, longer at once and shorter after "
       "2 s, and no frame plays twice");
}

static void check_latency_report(void) {
  Rig r;
  rig_open(&r, 2);
  assert(!report_latency(&r.h));  // nothing before the plugin has run
  for (unsigned i = 0; i < 8; ++i) {
    cycle(&r, 256);
    own_block_drain(&r.h);
  }
  assert(report_latency(&r.h) && r.h.reported_latency == 1536);
  r.f.latency = 480;
  assert(report_latency(&r.h) && r.h.reported_latency == 1536 + 480);
  assert(!report_latency(&r.h));
  rig_close(&r);
  puts("PASS: the reported latency is the plugin's own plus the block's delay");
}

static void check_invalid_output(void) {
  Rig r;
  rig_open(&r, 2);
  atomic_store(&r.f.invalid_call, 5);
  for (unsigned i = 0; i < 100; ++i) {
    cycle(&r, 256);
    own_block_drain(&r.h);
  }
  Tally t = tally(&r, 1536, r.position, 1536);
  assert(t.wrong == 0 && t.silent == OWN_BLOCK_FRAMES);
  assert(!atomic_load(&r.h.audio_error));
  rig_close(&r);
  puts("PASS: a block of invalid output plays as silence and the next is heard");
}

static void check_setup(void) {
  Host h = {0};
  assert(host_max_block(&h) == MAX_FRAMES);
  h.own_block = true;
  assert(host_max_block(&h) == OWN_BLOCK_FRAMES);
  assert(own_block_allowed(&lv2_backend) && own_block_allowed(&clap_backend) &&
         own_block_allowed(&vst3_backend));
  assert(!own_block_allowed(&copy_backend) && !own_block_allowed(NULL));
  assert(own_block_delay(32) == 1536 && own_block_delay(256) == 1536 &&
         own_block_delay(512) == 1536 && own_block_delay(1024) == 2048 &&
         own_block_delay(MAX_FRAMES) == 1024 + MAX_FRAMES);
  assert(own_block_delay(MAX_FRAMES) + MAX_FRAMES + OWN_BLOCK_FRAMES < OWN_BLOCK_RING);
  puts("PASS: only plugin formats run on their own block, which sets them up "
       "for exactly 1024 frames");
}

// The real worker thread, with the plugin held inside one call: the callback
// keeps returning at once, the stall monitor sees the call outstanding, and
// once the plugin lets go the output is exact again.
static void check_never_waits(void) {
  Rig r;
  rig_open(&r, 2);
  pw_init(NULL, NULL);
  atomic_store(&r.f.check_thread, true);
  assert(own_block_start(&r.h, NULL));
  for (unsigned i = 0; i < 100; ++i) {
    cycle(&r, 256);
    while (atomic_load(&r.h.block.done) + OWN_BLOCK_FRAMES <= r.position)
      sleep_us(100);
  }
  atomic_store(&r.f.hold, true);
  while (!atomic_load(&r.f.holding)) {
    cycle(&r, 256);
    sleep_us(1000);
  }
  r.slowest_cycle_ms = 0;
  for (unsigned i = 0; i < 2000; ++i) {  // 10.7 s of audio while it is held
    cycle(&r, 256);
    assert(atomic_load(&r.f.holding));
  }
  assert(atomic_load(&r.h.audio_entered) != atomic_load(&r.h.audio_left));
  printf("PASS: with the plugin held inside a call, 2000 callbacks returned, "
         "the slowest in %.3f ms\n", r.slowest_cycle_ms);
  assert(r.slowest_cycle_ms < 50);
  atomic_store(&r.f.hold, false);
  while (atomic_load(&r.h.audio_entered) != atomic_load(&r.h.audio_left) ||
         atomic_load(&r.h.block.done) + OWN_BLOCK_FRAMES <= r.position)
    sleep_us(100);
  uint64_t resumed = r.position;
  for (unsigned i = 0; i < 400; ++i) {
    cycle(&r, 256);
    while (atomic_load(&r.h.block.done) + OWN_BLOCK_FRAMES <= r.position)
      sleep_us(100);
  }
  Tally t = tally(&r, 1536, r.position, 1536);
  assert(t.wrong == 0);
  Tally after = tally(&r, resumed + 4096, r.position, 1536);
  assert(after.exact == r.position - resumed - 4096);
  assert(!atomic_load(&r.f.wrong_size) && !atomic_load(&r.f.wrong_thread));
  assert(!host_on_audio_thread(&r.h));  // this thread played PipeWire's part
  rig_close(&r);
  pw_deinit();
  puts("PASS: the worker thread calls the plugin and is the thread it is told "
       "is its audio thread; after the hold the output is exact again");
}

// In real time: 256-frame cycles every 5.33 ms against a plugin that takes
// 1 ms a call and once 53 ms, as a bridged Windows plugin does soon after it
// loads. The callback never takes long, every frame is exact or silent, and
// the silence is bounded by the spike.
static void check_real_time(void) {
  Rig r;
  rig_open(&r, 2);
  pw_init(NULL, NULL);
  atomic_store(&r.f.cost_us, 1000);
  atomic_store(&r.f.spike_call, 60);
  atomic_store(&r.f.spike_us, 53000);
  assert(own_block_start(&r.h, NULL));
  struct timespec next;
  clock_gettime(CLOCK_MONOTONIC, &next);
  for (unsigned i = 0; i < 750; ++i) {  // 4 s
    cycle(&r, 256);
    next.tv_nsec += 5333333;
    if (next.tv_nsec >= 1000000000) next.tv_nsec -= 1000000000, next.tv_sec++;
    while (clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME, &next, NULL) != 0) {}
  }
  Tally t = tally(&r, 1536, r.position, 1536);
  uint64_t spike_frames = 53 * 48 + OWN_BLOCK_FRAMES;
  printf("PASS: in real time the slowest callback took %.3f ms; a 53 ms spike "
         "gave %llu frames of silence in all, the longest run %llu\n",
         r.slowest_cycle_ms, (unsigned long long)t.silent,
         (unsigned long long)t.longest_silence);
  assert(t.wrong == 0);
  assert(t.longest_silence <= spike_frames);
  assert(r.slowest_cycle_ms < 20);
  // The last second is exact.
  Tally last = tally(&r, r.position - 48000, r.position, 1536);
  assert(last.exact == 48000);
  rig_close(&r);
  pw_deinit();
}

int main(void) {
  check_setup();
  check_exact();
  check_budget();
  check_spike();
  check_full_ring();
  check_quantum_change();
  check_latency_report();
  check_invalid_output();
  check_never_waits();
  check_real_time();
  return 0;
}
