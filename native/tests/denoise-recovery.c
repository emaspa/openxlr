// SPDX-License-Identifier: GPL-3.0-only
// Noise removal recovering from an engine that answers with something that
// is not a number. The stage keeps NaN and infinity out of its input
// (tests/eq.c and tests/denoise.c), so these checks put one inside the
// engine instead: into RNNoise's frame, and out of a fake DeepFilterNet
// model. RNNoise starts over on the audio thread and plays exactly as an
// engine started at that sample would; DeepFilterNet's model is replaced by
// a fresh one on the main thread, once in DF_RELOAD_SECONDS.
#define main host_program_main
#include "../host.c"
#undef main
#include "../denoise.c"
#include <assert.h>

enum { RATE = 48000, BLOCK = 256 };

static float signal_at(size_t n) {
  return (float)(0.2 * sin(2 * 3.14159265358979323846 * 220 * n / RATE)
                 + 0.01 * sin(2 * 3.14159265358979323846 * 3517 * n / RATE));
}

static Denoise *started(Host *h, int engine) {
  Denoise *d = denoise_load(h);
  assert(d);
  atomic_store(&d->enabled->desired, 1);
  atomic_store(&d->engine->desired, engine);
  assert(denoise_activate(d));
  return d;
}

static void all_finite(const float *x, uint32_t count) {
  for (uint32_t n = 0; n < count; n++) assert(isfinite(x[n]));
}

// A NaN in RNNoise's frame. It answers that frame with numbers and the next
// with NaN, since it keeps what it heard in its state and looks a frame
// ahead; the first frame that comes out non-finite is cleared, the engine
// starts over, and from there the output is exactly that of a second stage
// that switched to RNNoise at that sample. A third stage, run a sample at a
// time, finds the sample.
static void check_rnnoise(void) {
  Host h = {.rate = RATE, .channels = 1}, g = {.rate = RATE, .channels = 1}, p = {.rate = RATE, .channels = 1};
  Denoise *d = started(&h, DENOISE_RNNOISE), *e = started(&g, DENOISE_RNNOISE), *f = started(&p, DENOISE_RNNOISE);
  enum { BLOCKS = 4 * RATE / BLOCK, START = RATE / BLOCK * BLOCK };
  static float in[BLOCKS * BLOCK], out[BLOCKS * BLOCK], expected[BLOCKS * BLOCK], probe[BLOCKS * BLOCK];
  for (size_t n = 0; n < BLOCKS * BLOCK; n++) in[n] = signal_at(n);
  for (size_t at = 0; at < START; at += BLOCK) {
    denoise_process(d, in + at, out + at, BLOCK);
    denoise_process(e, in + at, expected + at, BLOCK);
    denoise_process(f, in + at, probe + at, BLOCK);
  }
  assert(d->rnn_at > 0);  // the frame under way holds its first sample
  d->rnn_in[0] = f->rnn_in[0] = NAN;
  size_t restart = 0;
  for (size_t n = START; n < BLOCKS * BLOCK && !restart; n++) {
    denoise_process(f, in + n, probe + n, 1);
    if (denoise_resets(f)) restart = n + 1;
  }
  size_t frame_end = START + RNN_FRAME - d->rnn_at;
  assert(restart >= frame_end && restart <= frame_end + RNN_FRAME);
  for (size_t at = START; at < BLOCKS * BLOCK; at += BLOCK) denoise_process(d, in + at, out + at, BLOCK);
  denoise_process(e, in + START, expected + START, (uint32_t)(restart - START));
  begin(e, DENOISE_RNNOISE);
  for (size_t at = restart; at < BLOCKS * BLOCK; at += BLOCK) {
    uint32_t frames = BLOCKS * BLOCK - at < BLOCK ? (uint32_t)(BLOCKS * BLOCK - at) : BLOCK;
    denoise_process(e, in + at, expected + at, frames);
  }
  all_finite(out, BLOCKS * BLOCK);
  // Up to the frame that took the NaN, nothing changed; the frame RNNoise
  // still answered with numbers plays; from the restart on, a fresh engine.
  assert(!memcmp(out, expected, frame_end * sizeof(float)));
  assert(!memcmp(out + restart, expected + restart, (BLOCKS * BLOCK - restart) * sizeof(float)));
  assert(denoise_resets(d) == 1 && d->active == DENOISE_RNNOISE);
  printf("recovery: RNNoise starts over %zu samples after the frame that took a NaN and plays as if "
         "switched on there\n", restart - frame_end);
  denoise_unload(d), denoise_unload(e), denoise_unload(f);
}

#if OPENXLR_DEEPFILTER
struct OpenxlrDf { int unused; };
static unsigned made, freed;
static _Atomic int poison;  // hops the fake model answers with NaN

OpenxlrDf *openxlr_df_create(float strength) {
  OpenxlrDf *model = calloc(1, sizeof(*model));
  assert(model);
  made++;
  return model;
}
size_t openxlr_df_hop(const OpenxlrDf *st) { return 480; }
size_t openxlr_df_rate(const OpenxlrDf *st) { return 48000; }
size_t openxlr_df_delay(const OpenxlrDf *st) { return 960; }
void openxlr_df_set_atten_lim(OpenxlrDf *st, float strength) {}
int32_t openxlr_df_process(OpenxlrDf *st, const float *in, float *out) {
  memcpy(out, in, 480 * sizeof(float));
  if (atomic_load(&poison) > 0) {
    atomic_fetch_sub(&poison, 1);
    out[17] = NAN;
  }
  return 0;
}
void openxlr_df_free(OpenxlrDf *st) { freed++; free(st); }

// Blocks as the audio thread and the main thread's tick would run them.
static void play(Denoise *d, size_t *at, size_t blocks) {
  static float in[BLOCK], out[BLOCK];
  for (size_t b = 0; b < blocks; b++, *at += BLOCK) {
    for (uint32_t n = 0; n < BLOCK; n++) in[n] = signal_at(*at + n);
    denoise_process(d, in, out, BLOCK);
    all_finite(out, BLOCK);
    denoise_wait_idle(d);
    denoise_main_thread(d);
  }
}

// A model that answers with NaN once is replaced by a fresh one and noise
// removal comes back on its own; a second failure inside the reload window
// holds, as any other DeepFilterNet failure does.
static void check_deepfilter(void) {
  Host h = {.rate = RATE, .channels = 1};
  Denoise *d = started(&h, DENOISE_DEEPFILTER);
  size_t at = 0;
  play(d, &at, RATE / BLOCK);
  assert(made == 1 && d->active == DENOISE_DEEPFILTER);
  atomic_store(&poison, 1);
  play(d, &at, RATE / BLOCK);
  assert(made == 2 && freed == 1);
  assert(d->active == DENOISE_DEEPFILTER && atomic_load(&d->status_meter->observed) == DENOISE_OK);
  assert(denoise_resets(d) == 1);

  atomic_store(&poison, 1);
  play(d, &at, RATE / BLOCK);
  assert(made == 2 && freed == 2);
  assert(d->active == DENOISE_NONE && atomic_load(&d->status_meter->observed) == DENOISE_FELL_BEHIND);
  assert(denoise_resets(d) == 1);

  // Asked for again, it starts afresh, as after any failure.
  atomic_store(&d->enabled->desired, 0);
  play(d, &at, 4);
  atomic_store(&d->enabled->desired, 1);
  play(d, &at, RATE / BLOCK);
  assert(made == 3 && d->active == DENOISE_DEEPFILTER && atomic_load(&d->status_meter->observed) == DENOISE_OK);
  denoise_unload(d);
  assert(made == freed);
  puts("recovery: a DeepFilterNet model that answers with NaN is reloaded once, a second failure holds");
}
#endif

int main(void) {
  check_rnnoise();
#if OPENXLR_DEEPFILTER
  check_deepfilter();
#else
  puts("recovery: DeepFilterNet is not built");
#endif
  puts("denoise recovery: all checks passed");
  return 0;
}
