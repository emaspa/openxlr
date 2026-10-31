// SPDX-License-Identifier: GPL-3.0-only
// Noise removal driven through the microphone stage with no audio server: a
// synthetic voice (a gliding harmonic series through three formants, spoken
// in syllables) in steady noise. For each engine the noise between the
// syllables has to drop, the syllables have to come through, and the delay
// the stage reports has to be the delay the output has. The frame
// bookkeeping is checked with blocks of every size, the switches with a
// steady tone, and the engine list against the build. DeepFilterNet is
// checked when this build carries it.
//
// `tests/denoise HOST` runs the checks; `tests/denoise HOST --bench` also
// prints each engine's processing time for one minute of audio.
#define main host_program_main
#include "../host.c"
#undef main
#include "../eq.c"
#include "../build-config.h"
#include <assert.h>
#include <time.h>

enum { RATE = 48000, BLOCK = 256, SECONDS = 8, COUNT = SECONDS * RATE };
static const double PI = 3.14159265358979323846;
static float voice[COUNT], noise[COUNT], input[COUNT], output[COUNT];

// --- signals -----------------------------------------------------------------

// Syllables of 250 ms with 150 ms between them.
static bool inside_syllable(size_t n) {
  double at = fmod((double)n / RATE, 0.4);
  return at > 0.05 && at < 0.20;
}
static bool between_syllables(size_t n) {
  double at = fmod((double)n / RATE, 0.4);
  return at > 0.27 && at < 0.38;
}

static uint64_t random_state = 0x9e3779b97f4a7c15u;
static double uniform(void) {
  random_state ^= random_state << 13, random_state ^= random_state >> 7, random_state ^= random_state << 17;
  return ((random_state >> 11) + 0.5) / 9007199254740992.0;
}

// A formant synthesiser: a glottal pulse train with a little jitter, through
// three resonators whose frequencies glide from one vowel to the next, with
// a breath of noise and a falling pitch in every syllable.
static void make_voice(void) {
  static const double vowels[5][3] = {
      {730, 1090, 2440}, {270, 2290, 3010}, {530, 1840, 2480}, {570, 840, 2410}, {300, 870, 2240}};
  static const double widths[3] = {90, 110, 170};
  double state[3][2] = {{0}}, open = 0, period = 0, glottal = 0;
  for (size_t n = 0; n < COUNT; n++) {
    double t = (double)n / RATE, at = fmod(t, 0.4);
    int syllable = (int)(t / 0.4);
    const double *from = vowels[syllable % 5], *to = vowels[(syllable + 2) % 5];
    double glide = fmin(1, at / 0.25);
    double f0 = (165 - 40 * glide) * (1 + 0.04 * sin(2 * PI * 0.37 * syllable));
    // One pulse per period: a rising half cosine, then a fast closure.
    if ((open += 1.0 / RATE) >= period) open = 0, period = (1 + 0.02 * (uniform() - 0.5)) / f0;
    double phase = open / period;
    double pulse = phase < 0.6 ? 0.5 - 0.5 * cos(PI * phase / 0.6) : cos(PI * (phase - 0.6) / 0.8);
    if (pulse < 0) pulse = 0;
    double source = pulse - glottal + 0.02 * (uniform() - 0.5);  // the flow's derivative
    glottal = pulse;
    double y = source;
    for (int f = 0; f < 3; f++) {
      double frequency = from[f] + (to[f] - from[f]) * glide;
      double r = exp(-PI * widths[f] / RATE), w = 2 * PI * frequency / RATE;
      double a1 = 2 * r * cos(w), a2 = -r * r, g = 1 - r;
      double out = g * y + a1 * state[f][0] + a2 * state[f][1];
      state[f][1] = state[f][0], state[f][0] = out;
      y = out;
    }
    double envelope = at < 0.25 ? 0.5 - 0.5 * cos(2 * PI * at / 0.25) : 0;
    voice[n] = (float)(160 * envelope * y);
  }
}

// Steady noise, a little darker than white, at about -40 dBFS.
static void make_noise(void) {
  double held = 0;
  for (size_t n = 0; n < COUNT; n++) {
    double g = sqrt(-2 * log(uniform())) * cos(2 * PI * uniform());
    held = 0.6 * held + 0.4 * g;
    noise[n] = (float)(0.014 * held);
  }
}

static double rms_db(const float *x, size_t start, size_t end, bool (*keep)(size_t), size_t shift) {
  double sum = 0;
  size_t count = 0;
  for (size_t n = start; n + shift < end; n++)
    if (keep(n)) sum += (double)x[n + shift] * x[n + shift], count++;
  assert(count > 0);
  return 10 * log10(sum / count + 1e-30);
}

// --- the stage -----------------------------------------------------------------

static Host *stage(uint32_t rate, int engine, double strength) {
  Host *h = calloc(1, sizeof(*h));
  assert(h);
  h->backend = &eq_backend;
  h->rate = rate;
  h->channels = 1;
  assert(h->backend->load(h, NULL));
  assert(set_control(h, "denoise_enabled", engine == DENOISE_NONE ? 0 : 1));
  assert(set_control(h, "denoise_engine", engine == DENOISE_DEEPFILTER ? 1 : 0));
  assert(set_control(h, "denoise_strength", (float)strength));
  assert(h->backend->activate(h));
  return h;
}

static void release(Host *h) {
  h->backend->deactivate(h);
  h->backend->unload(h);
  assert(h->impl == NULL);
  free(h);
}

static Denoise *denoise_of(Host *h) { return ((Eq *)h->impl)->denoise; }

static float meter(Host *h, const char *symbol) {
  for (uint32_t i = 0; i < h->control_count; i++)
    if (h->controls[i].output && !strcmp(h->controls[i].symbol, symbol))
      return atomic_load(&h->controls[i].observed);
  assert(!"no such meter");
  return NAN;
}

// One block as the audio thread would see it, then the main thread's tick.
// DeepFilterNet's worker is given the time real audio would give it.
static void block(Host *h, const float *in, float *out, uint32_t frames) {
  float *ins[] = {(float *)in}, *outs[] = {out};
  h->backend->process(h, frames, ins, outs);
  for (uint32_t n = 0; n < frames; n++) assert(isfinite(out[n]));
  denoise_wait_idle(denoise_of(h));
  h->backend->main_thread(h);
}

static void run(Host *h, const float *in, float *out, size_t count, uint32_t frames) {
  for (size_t at = 0; at < count; at += frames) {
    uint32_t size = (uint32_t)(count - at < frames ? count - at : frames);
    block(h, in + at, out + at, size);
  }
}

// The lag at which the output lines up best with the clean voice.
static size_t best_lag(const float *out, size_t from, size_t to, size_t most) {
  size_t best = 0;
  double top = -1;
  for (size_t lag = 0; lag <= most; lag++) {
    double sum = 0;
    for (size_t n = from; n < to; n++) sum += (double)out[n + lag] * voice[n];
    if (sum > top) top = sum, best = lag;
  }
  return best;
}

// --- checks --------------------------------------------------------------------

static void check_engines(const char *host) {
  assert(denoise_engine_built(DENOISE_RNNOISE));
  assert(denoise_engine_built(DENOISE_DEEPFILTER) == (OPENXLR_DEEPFILTER == 1));
  assert(!denoise_engine_built(DENOISE_NONE));
  char command[1024], line[256] = "";
  snprintf(command, sizeof(command), "'%s' engines", host);
  FILE *p = popen(command, "r");
  assert(p);
  assert(fgets(line, sizeof(line), p));
  assert(pclose(p) == 0);
  const char *expected = OPENXLR_DEEPFILTER ? "{\"denoise\":[\"rnnoise\",\"deepfilternet\"],\"rate\":48000}\n"
                                            : "{\"denoise\":[\"rnnoise\"],\"rate\":48000}\n";
  assert(!strcmp(line, expected));
  printf("engines: %s", line);
}

static void check_controls(void) {
  Host *h = stage(RATE, DENOISE_NONE, 100);
  const struct { const char *name; float minimum, maximum, initial; bool output; } controls[] = {
      {"denoise_enabled", 0, 1, 0, false},  {"denoise_engine", 0, 1, 0, false},
      {"denoise_strength", 0, 100, 100, false}, {"denoise_active", -1, 1, -1, true},
      {"denoise_status", 0, 4, 0, true},    {"denoise_late", 0, 1e9f, 0, true},
      {"denoise_latency", 0, 1e9f, 0, true},
  };
  for (size_t i = 0; i < sizeof(controls) / sizeof(controls[0]); i++) {
    const Control *found = NULL;
    for (uint32_t c = 0; c < h->control_count; c++)
      if (!strcmp(h->controls[c].symbol, controls[i].name)) found = &h->controls[c];
    assert(found && found->minimum == controls[i].minimum && found->maximum == controls[i].maximum);
    assert(found->output == controls[i].output);
  }
  // Off, the stage passes its input untouched and adds no delay.
  run(h, input, output, RATE, BLOCK);
  assert(!memcmp(input, output, RATE * sizeof(float)));
  assert(eq_latency(h) == 0 && meter(h, "denoise_active") == DENOISE_NONE);
  assert(meter(h, "denoise_latency") == 0);
  release(h);
}

// Noise between the syllables, the syllables themselves, and the lag.
static void check_engine(int engine, const char *name, double least_drop) {
  Host *h = stage(RATE, engine, 100);
  run(h, input, output, COUNT, BLOCK);
  assert(meter(h, "denoise_active") == engine && meter(h, "denoise_status") == DENOISE_OK);
  size_t latency = eq_latency(h);
  assert(latency > 0 && meter(h, "denoise_latency") == (float)latency);  // the voice changer is off
  size_t from = 2 * RATE;  // past the engine's first adjustment
  double noise_in = rms_db(input, from, COUNT, between_syllables, 0);
  double noise_out = rms_db(output, from, COUNT, between_syllables, latency);
  double voice_in = rms_db(voice, from, COUNT, inside_syllable, 0);
  double voice_out = rms_db(output, from, COUNT, inside_syllable, latency);
  size_t lag = best_lag(output, from, from + RATE, 4 * RATE / 50);
  printf("%s: latency %zu samples (%.1f ms), voice lines up at %zu; noise %.1f -> %.1f dBFS (%.1f dB "
         "down), voice %.1f -> %.1f dBFS, %.0f late blocks\n",
         name, latency, latency * 1000.0 / RATE, lag, noise_in, noise_out, noise_in - noise_out,
         voice_in, voice_out, meter(h, "denoise_late"));
  assert(lag + 8 >= latency && lag <= latency + 8);
  assert(noise_in - noise_out >= least_drop);
  assert(voice_out > voice_in - 6 && voice_out < voice_in + 3);
  release(h);
}

// Strength 0 gives the input back, late by the engine's delay; a limit
// keeps the noise from going further down than it says.
static void check_strength(int engine, const char *name) {
  Host *h = stage(RATE, engine, 0);
  run(h, input, output, COUNT, BLOCK);
  size_t latency = eq_latency(h);
  double worst = 0, sum = 0, level = 0;
  for (size_t n = RATE; n + latency < COUNT; n++) {
    double d = output[n + latency] - input[n];
    worst = fmax(worst, fabs(d));
    sum += d * d;
    level += (double)input[n] * input[n];
  }
  double error_db = 10 * log10(sum / level + 1e-30);
  printf("%s at strength 0: the delayed input, %.1f dB below it off, at most %.2g\n", name, -error_db, worst);
  // RNNoise mixes the input itself back in; DeepFilterNet resynthesises it
  // with its least limit, 0.01 dB.
  assert(engine == DENOISE_RNNOISE ? worst == 0 : error_db < -40);
  release(h);

  h = stage(RATE, engine, 12);
  run(h, input, output, COUNT, BLOCK);
  latency = eq_latency(h);
  double drop = rms_db(input, 2 * RATE, COUNT, between_syllables, 0)
      - rms_db(output, 2 * RATE, COUNT, between_syllables, latency);
  printf("%s at strength 12: noise %.1f dB down\n", name, drop);
  assert(drop > 6 && drop < 13);
  release(h);
}

// The output does not depend on how the audio arrives in blocks.
static void check_blocks(void) {
  static float other[COUNT];
  static const uint32_t sizes[] = {1, 37, 480, 128, 1000, 2048, 333, 64};
  Host *h = stage(RATE, DENOISE_RNNOISE, 30);
  run(h, input, output, 3 * RATE, BLOCK);
  release(h);
  h = stage(RATE, DENOISE_RNNOISE, 30);
  size_t at = 0;
  for (unsigned i = 0; at < 3 * RATE; i++) {
    uint32_t size = sizes[i % (sizeof(sizes) / sizeof(sizes[0]))];
    if (at + size > 3 * RATE) size = (uint32_t)(3 * RATE - at);
    block(h, input + at, other + at, size);
    at += size;
  }
  assert(!memcmp(output, other, 3 * RATE * sizeof(float)));
  release(h);
  printf("blocks: RNNoise gives the same output for blocks of 1 to 2048 frames\n");
}

// Switching on, off and between engines on a steady tone at strength 0,
// where every engine passes the tone: the only dip is the switch's own.
static void check_switches(void) {
  static float tone[COUNT];
  for (size_t n = 0; n < COUNT; n++) tone[n] = (float)(0.25 * sin(2 * PI * 440.0 * n / RATE));
  Host *h = stage(RATE, DENOISE_NONE, 0);
  struct { double at; int engine; } steps[] = {
      {0.5, DENOISE_RNNOISE}, {1.5, DENOISE_NONE}, {2.5, DENOISE_DEEPFILTER},
      {3.5, DENOISE_RNNOISE}, {4.5, DENOISE_DEEPFILTER}, {5.5, DENOISE_NONE},
  };
  size_t at = 0, quiet = 0, longest = 0;
  unsigned step = 0;
  while (at < COUNT) {
    if (step < sizeof(steps) / sizeof(steps[0]) && at >= steps[step].at * RATE) {
      int engine = steps[step].engine;
      if (engine == DENOISE_DEEPFILTER && !OPENXLR_DEEPFILTER) engine = DENOISE_RNNOISE;
      set_control(h, "denoise_enabled", engine != DENOISE_NONE);
      if (engine != DENOISE_NONE) set_control(h, "denoise_engine", engine);
      step++;
    }
    block(h, tone + at, output + at, BLOCK);
    at += BLOCK;
  }
  // The envelope: the largest sample in each 1 ms. Below half the tone's
  // level counts as the dip.
  for (size_t n = RATE / 4; n + 48 <= COUNT; n += 48) {
    float top = 0;
    for (size_t i = n; i < n + 48; i++) top = fmaxf(top, fabsf(output[i]));
    if (top < 0.125f) quiet++, longest = quiet > longest ? quiet : longest;
    else quiet = 0;
  }
  printf("switches: the longest dip is %zu ms\n", longest);
  assert(longest <= 6);
  release(h);
}

static void check_rate(void) {
  Host *h = stage(44100, DENOISE_RNNOISE, 100);
  run(h, input, output, RATE, BLOCK);
  assert(!memcmp(input, output, RATE * sizeof(float)));
  assert(meter(h, "denoise_status") == DENOISE_RATE_UNSUPPORTED && eq_latency(h) == 0);
  release(h);
  printf("44.1 kHz: passed through untouched, status says why\n");
}

#if OPENXLR_DEEPFILTER
// What DeepFilterNet's worker lag should be behind blocks of `block`: the hop
// it collects, then whole blocks making up at least a hop for its answer.
static size_t worker_lag(uint32_t block, uint32_t hop) { return hop + (hop + block - 1) / block * block; }

static void check_deepfilter_blocks(void) {
  static const uint32_t sizes[] = {64, 1024, 4096};
  size_t model = 0;  // the model's own delay, the same whatever the blocks
  for (size_t i = 0; i < sizeof(sizes) / sizeof(sizes[0]); i++) {
    Host *h = stage(RATE, DENOISE_DEEPFILTER, 0);
    run(h, input, output, 3 * RATE, sizes[i]);
    size_t latency = eq_latency(h);
    if (!model) model = latency - worker_lag(sizes[i], 480);
    assert(latency == worker_lag(sizes[i], 480) + model);
    size_t lag = best_lag(output, RATE, 2 * RATE, latency + 200);
    printf("DeepFilterNet in blocks of %u: latency %zu, lines up at %zu\n", sizes[i], latency, lag);
    assert(lag + 8 >= latency && lag <= latency + 8);
    release(h);
  }
}

// The lag follows the block size: up at once with larger blocks, down once
// smaller ones have held for DF_SETTLE, and the output lines up with the
// voice at each figure with no hop answered late.
static void check_deepfilter_follows(void) {
  static const struct { uint32_t block; double seconds; } parts[] = {{2048, 1.5}, {256, 3}, {1024, 1.5}};
  Host *h = stage(RATE, DENOISE_DEEPFILTER, 0);
  size_t model = 0, at = 0;
  for (size_t p = 0; p < sizeof(parts) / sizeof(parts[0]); p++) {
    size_t end = at + (size_t)(parts[p].seconds * RATE);
    uint32_t first = 0;
    while (at < end) {
      block(h, input + at, output + at, parts[p].block);
      at += parts[p].block;
      if (!first) first = eq_latency(h);
    }
    size_t latency = eq_latency(h);
    if (!model) model = latency - worker_lag(parts[p].block, 480);
    assert(latency == worker_lag(parts[p].block, 480) + model);
    // A larger block raises the lag from its first block on; a smaller one
    // lowers it only after DF_SETTLE.
    if (p > 0 && parts[p].block > parts[p - 1].block) assert(first == latency);
    if (p > 0 && parts[p].block < parts[p - 1].block) assert(first > latency);
    size_t from = end - RATE / 2 - latency, lag = best_lag(output, from, from + RATE / 4, latency + 200);
    printf("DeepFilterNet from blocks of %u: latency %zu (%.1f ms), lines up at %zu\n", parts[p].block, latency,
           latency * 1000.0 / RATE, lag);
    assert(lag + 8 >= latency && lag <= latency + 8);
  }
  for (size_t n = 0; n < at; n++) assert(fabsf(output[n]) < 1.0f);
  assert(meter(h, "denoise_late") == 0);
  release(h);
}

// A smaller quantum must settle two seconds after the last large block,
// independently of where the old fixed sampling window happened to start.
static void check_deepfilter_settle_deadline(void) {
  Host *h = stage(RATE, DENOISE_DEEPFILTER, 0);
  block(h, input, output, 2048);
  uint32_t large = eq_latency(h);
  for (unsigned n = 0; n < 374; n++) block(h, input, output, 256);
  assert(eq_latency(h) == large);
  block(h, input, output, 256);
  assert(eq_latency(h) == large - worker_lag(2048, 480) + worker_lag(256, 480));
  // One large block during a shorter run restarts the quiet interval.
  block(h, input, output, 2048);
  for (unsigned n = 0; n < 200; n++) block(h, input, output, 256);
  block(h, input, output, 2048);
  for (unsigned n = 0; n < 374; n++) block(h, input, output, 256);
  assert(eq_latency(h) == large);
  block(h, input, output, 256);
  assert(eq_latency(h) == large - worker_lag(2048, 480) + worker_lag(256, 480));
  release(h);
  puts("DeepFilterNet: smaller blocks settle after two seconds, with a large block restarting the wait");
}

// The same voice in quieter rooms. At -50 dBFS of noise the model rates the
// voice between its thresholds for gains alone and for deep filtering, where
// DeepFilterNet's library defaults cut into the syllables; at -70 dBFS the
// gaps fall below its quiet-hop level, where 0.5.6 zeroed lone hops and left
// the gaps louder than they came in. Every syllable has to come through and
// the noise between them has to drop.
static void check_deepfilter_quiet_rooms(void) {
  static float quiet[COUNT];
  static const double rooms[] = {0.3, 0.03};
  for (size_t r = 0; r < sizeof(rooms) / sizeof(rooms[0]); r++) {
    for (size_t n = 0; n < COUNT; n++) quiet[n] = voice[n] + (float)rooms[r] * noise[n];
    Host *h = stage(RATE, DENOISE_DEEPFILTER, 100);
    run(h, quiet, output, COUNT, BLOCK);
    size_t latency = eq_latency(h);
    double worst = 0;
    for (size_t at = 2 * RATE; at + RATE * 2 / 5 + latency <= COUNT; at += RATE * 2 / 5) {
      double cut = rms_db(voice, at, at + RATE * 2 / 5, inside_syllable, 0)
          - rms_db(output, at, at + RATE * 2 / 5 + latency, inside_syllable, latency);
      worst = fmax(worst, cut);
    }
    double drop = rms_db(quiet, 2 * RATE, COUNT, between_syllables, 0)
        - rms_db(output, 2 * RATE, COUNT, between_syllables, latency);
    printf("DeepFilterNet with noise %.0f dB lower: the most a syllable loses is %.1f dB, noise %.1f dB down\n",
           -20 * log10(rooms[r]), worst, drop);
    assert(worst < 8);
    assert(drop > 10);
    release(h);
  }
}
#endif

static double cpu_seconds(void) {
  struct timespec t;
  clock_gettime(CLOCK_PROCESS_CPUTIME_ID, &t);
  return t.tv_sec + t.tv_nsec / 1e9;
}

static void bench(int engine, const char *name) {
  Host *h = stage(RATE, engine, 100);
  double start = cpu_seconds();
  for (int round = 0; round < 60 / SECONDS; round++) run(h, input, output, COUNT, BLOCK);
  double used = cpu_seconds() - start;
  printf("bench %s: %.2f s of CPU for %d s of audio, %.2f%% of one core\n", name, used,
         60 / SECONDS * SECONDS, 100 * used / (60 / SECONDS * SECONDS));
  release(h);
}

int main(int argc, char **argv) {
  assert(argc >= 2);
  setvbuf(stdout, NULL, _IOLBF, 0);
  make_voice();
  make_noise();
  for (size_t n = 0; n < COUNT; n++) input[n] = voice[n] + noise[n];
#if OPENXLR_DEEPFILTER
  check_deepfilter_settle_deadline();
#endif
  check_engines(argv[1]);
  check_controls();
  check_engine(DENOISE_RNNOISE, "RNNoise", 10);
  check_strength(DENOISE_RNNOISE, "RNNoise");
  check_blocks();
  check_switches();
  check_rate();
#if OPENXLR_DEEPFILTER
  check_engine(DENOISE_DEEPFILTER, "DeepFilterNet", 15);
  check_strength(DENOISE_DEEPFILTER, "DeepFilterNet");
  check_deepfilter_blocks();
  check_deepfilter_follows();
  check_deepfilter_quiet_rooms();
#else
  // Asked for anyway, it passes the input through and says why.
  Host *h = stage(RATE, DENOISE_DEEPFILTER, 100);
  run(h, input, output, RATE, BLOCK);
  assert(!memcmp(input, output, RATE * sizeof(float)));
  assert(meter(h, "denoise_status") == DENOISE_NOT_BUILT && eq_latency(h) == 0);
  release(h);
  printf("DeepFilterNet: not in this build; asked for, it passes the input through\n");
#endif
  if (argc > 2 && !strcmp(argv[2], "--bench")) {
    bench(DENOISE_RNNOISE, "RNNoise");
    if (OPENXLR_DEEPFILTER) bench(DENOISE_DEEPFILTER, "DeepFilterNet");
  }
  puts("denoise: all checks passed");
  return 0;
}
