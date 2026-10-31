// SPDX-License-Identifier: GPL-3.0-only
// The voice changer driven through the microphone stage with no audio
// server, on a synthetic voice (the formant synthesiser tests/denoise.c uses)
// and on a steady buzz. Nothing the stage does on the audio thread allocates.
// Off, the stage is the input to the bit. Every voice
// stays finite and below full scale, comes out near the input's level, dies
// away after the voice stops, and turns silence into silence. The delay the
// stage reports is the delay the output has. Switching between voices, on
// and off, leaves no step in the output and ends bit-exact when it ends dry.
// It prints each voice's latency and its CPU at 48 kHz in blocks of 256.
//
// `tests/voice` runs the checks. `tests/voice --render IN.wav DIR` runs every
// voice over a recording and writes DIR/NN-name.wav beside DIR/00-dry.wav,
// for tuning the table in voice.c by ear.
#define main host_program_main
#include "../host.c"
#undef main
#include "../eq.c"
#include <assert.h>
#include <sndfile.h>
#include <time.h>

enum { RATE = 48000, BLOCK = 256, SECONDS = 8, COUNT = SECONDS * RATE, SHIFT_BLOCK = 1600 };
static const double PI = 3.14159265358979323846;
static float voice[COUNT], buzz[COUNT], output[COUNT];

// Every allocation while the stage processes a block is counted: the audio
// thread may not allocate. C++'s operator new comes here through malloc.
extern void *__libc_malloc(size_t size);
extern void *__libc_calloc(size_t count, size_t size);
extern void *__libc_realloc(void *at, size_t size);
static _Atomic bool counting;
static _Atomic unsigned allocations;
void *malloc(size_t size) {
  if (atomic_load(&counting)) atomic_fetch_add(&allocations, 1);
  return __libc_malloc(size);
}
void *calloc(size_t count, size_t size) {
  if (atomic_load(&counting)) atomic_fetch_add(&allocations, 1);
  return __libc_calloc(count, size);
}
void *realloc(void *at, size_t size) {
  if (atomic_load(&counting)) atomic_fetch_add(&allocations, 1);
  return __libc_realloc(at, size);
}

// --- signals -----------------------------------------------------------------

// Syllables of 250 ms with 150 ms between them.
static bool inside_syllable(size_t n) {
  double at = fmod((double)n / RATE, 0.4);
  return at > 0.05 && at < 0.20;
}

static uint64_t random_state = 0x9e3779b97f4a7c15u;
static double uniform(void) {
  random_state ^= random_state << 13, random_state ^= random_state >> 7, random_state ^= random_state << 17;
  return ((random_state >> 11) + 0.5) / 9007199254740992.0;
}

// A glottal pulse train through three gliding formants, in syllables, as in
// tests/denoise.c.
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
    if ((open += 1.0 / RATE) >= period) open = 0, period = (1 + 0.02 * (uniform() - 0.5)) / f0;
    double phase = open / period;
    double pulse = phase < 0.6 ? 0.5 - 0.5 * cos(PI * phase / 0.6) : cos(PI * (phase - 0.6) / 0.8);
    if (pulse < 0) pulse = 0;
    double source = pulse - glottal + 0.02 * (uniform() - 0.5);
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
    // Plus zero: the EQ turns -0 into +0 even while off, which memcmp sees.
    voice[n] = (float)(160 * envelope * y) + 0.0f;
  }
}

// A steady vowel-like buzz: the first twelve harmonics of 140 Hz, falling
// off, at about -16 dBFS.
static void make_buzz(void) {
  for (size_t n = 0; n < COUNT; n++) {
    double y = 0;
    for (int k = 1; k <= 12; k++) y += sin(2 * PI * 140 * k * n / RATE + k) / k;
    buzz[n] = (float)(0.1 * y);
  }
}

static double rms_db(const float *x, size_t start, size_t end, size_t shift) {
  double sum = 0;
  size_t count = 0;
  for (size_t n = start; n + shift < end; n++)
    if (inside_syllable(n)) sum += (double)x[n + shift] * x[n + shift], count++;
  assert(count > 0);
  return 10 * log10(sum / count + 1e-30);
}

static float peak(const float *x, size_t start, size_t end) {
  float top = 0;
  for (size_t n = start; n < end; n++) top = fmaxf(top, fabsf(x[n]));
  return top;
}

// --- the stage -----------------------------------------------------------------

static Host *stage(uint32_t rate, int preset) {
  Host *h = calloc(1, sizeof(*h));
  assert(h);
  h->backend = &eq_backend;
  h->rate = rate;
  h->channels = 1;
  assert(h->backend->load(h, NULL));
  assert(set_control(h, "voice_enabled", preset != VOICE_OFF));
  if (preset != VOICE_OFF) assert(set_control(h, "voice_preset", (float)preset));
  assert(h->backend->activate(h));
  return h;
}

// The stage with noise removal on as well, RNNoise, for the split of its delay.
static Host *stage_with_noise(int preset) {
  Host *h = calloc(1, sizeof(*h));
  assert(h);
  h->backend = &eq_backend;
  h->rate = RATE;
  h->channels = 1;
  assert(h->backend->load(h, NULL));
  assert(set_control(h, "denoise_enabled", 1) && set_control(h, "denoise_engine", 0));
  assert(set_control(h, "voice_enabled", 1) && set_control(h, "voice_preset", (float)preset));
  assert(h->backend->activate(h));
  return h;
}

static void release(Host *h) {
  h->backend->deactivate(h);
  h->backend->unload(h);
  assert(h->impl == NULL);
  free(h);
}

static float meter(Host *h, const char *symbol) {
  for (uint32_t i = 0; i < h->control_count; i++)
    if (h->controls[i].output && !strcmp(h->controls[i].symbol, symbol))
      return atomic_load(&h->controls[i].observed);
  assert(!"no such meter");
  return NAN;
}

static void block(Host *h, const float *in, float *out, uint32_t frames) {
  float *ins[] = {(float *)in}, *outs[] = {out};
  atomic_store(&counting, true);
  h->backend->process(h, frames, ins, outs);
  atomic_store(&counting, false);
  assert(atomic_load(&allocations) == 0);
  for (uint32_t n = 0; n < frames; n++) assert(isfinite(out[n]));
  h->backend->main_thread(h);
}

static void run(Host *h, const float *in, float *out, size_t count, uint32_t frames) {
  for (size_t at = 0; at < count; at += frames) {
    uint32_t size = (uint32_t)(count - at < frames ? count - at : frames);
    block(h, in + at, out + at, size);
  }
}

// --- checks --------------------------------------------------------------------

static void check_controls(void) {
  Host *h = stage(RATE, VOICE_OFF);
  const struct { const char *name; float minimum, maximum, initial; bool output; } controls[] = {
      {"voice_enabled", 0, 1, 0, false}, {"voice_preset", 0, 10, 1, false},
      {"voice_pitch", -12, 12, 0, false}, {"voice_formant", -12, 12, 0, false},
      {"voice_active", -1, 10, -1, true}, {"voice_status", 0, 2, 0, true},
      {"voice_latency", 0, 1e9f, 0, true},
  };
  for (size_t i = 0; i < sizeof(controls) / sizeof(controls[0]); i++) {
    const Control *found = NULL;
    for (uint32_t c = 0; c < h->control_count; c++)
      if (!strcmp(h->controls[c].symbol, controls[i].name)) found = &h->controls[c];
    assert(found && found->minimum == controls[i].minimum && found->maximum == controls[i].maximum);
    assert(found->output == controls[i].output);
    float initial = controls[i].output ? atomic_load(&found->observed) : atomic_load(&found->desired);
    assert(initial == controls[i].initial);
  }
  static const char *names[VOICE_COUNT] = {"custom", "deep", "high", "robot", "radio", "alien",
                                           "dracula", "frankenstein", "mummy", "werewolf", "witch"};
  for (int i = 0; i < VOICE_COUNT; i++) assert(!strcmp(voice_name(i), names[i]));
  // Off, the stage passes its input untouched and adds no delay.
  run(h, voice, output, 2 * RATE, BLOCK);
  assert(!memcmp(voice, output, 2 * RATE * sizeof(float)));
  assert(eq_latency(h) == 0 && meter(h, "voice_active") == VOICE_OFF && meter(h, "voice_status") == VOICE_OK);
  release(h);
  puts("controls: as named in the plan; off, the input comes through to the bit with no delay");
}

static double cpu_seconds(void) {
  struct timespec t;
  clock_gettime(CLOCK_PROCESS_CPUTIME_ID, &t);
  return t.tv_sec + t.tv_nsec / 1e9;
}

// One voice over the synthetic speech: bounded, near the input's level, the
// reported delay, a tail that dies away, and silence for silence.
static void check_voice(int preset) {
  static float silence[COUNT], tail[COUNT];
  Host *h = stage(RATE, preset);
  assert(meter(h, "voice_active") == preset && meter(h, "voice_status") == VOICE_OK);
  double start = cpu_seconds();
  run(h, voice, output, COUNT, BLOCK);
  double used = cpu_seconds() - start;
  size_t latency = eq_latency(h);
  assert(latency == (size_t)SHIFT_BLOCK && meter(h, "voice_latency") == (float)latency);
  double in_db = rms_db(voice, RATE, COUNT, 0), out_db = rms_db(output, RATE, COUNT, latency);
  float top = peak(output, 0, COUNT);
  // Then the voice stops: whatever rings on has to die away.
  run(h, silence, tail, 6 * RATE, BLOCK);
  float early = peak(tail, 0, RATE / 2), late = peak(tail, 5 * RATE, 6 * RATE);
  printf("%-12s latency %zu (%.1f ms), %5.2f%% of one core; level %5.1f dB from the input, peak %.2f; "
         "after the voice stops %.1f dBFS, 5 s later %.0f dBFS\n",
         voice_name(preset), latency, latency * 1000.0 / RATE, 100 * used / SECONDS, out_db - in_db, top,
         20 * log10(early + 1e-30), 20 * log10(late + 1e-30));
  assert(top <= 1.0f);
  assert(fabs(out_db - in_db) <= 4);
  assert(late < 1e-5f);
  release(h);

  h = stage(RATE, preset);
  run(h, silence, output, 2 * RATE, BLOCK);
  assert(peak(output, 0, 2 * RATE) == 0);
  release(h);
}

// Custom at no shift is the input late by the reported delay.
static void check_latency(void) {
  Host *h = stage(RATE, VOICE_CUSTOM);
  run(h, voice, output, 2 * RATE, BLOCK);
  size_t latency = eq_latency(h), best = 0;
  double top = -1;
  for (size_t lag = 0; lag <= 4 * latency; lag++) {
    double sum = 0;
    for (size_t n = RATE / 2; n < RATE + RATE / 2; n++) sum += (double)output[n + lag] * voice[n];
    if (sum > top) top = sum, best = lag;
  }
  printf("custom at 0 and 0: the voice lines up at %zu, latency %zu\n", best, latency);
  assert(best + 8 >= latency && best <= latency + 8);
  release(h);
}

// Every switch on a steady buzz: the largest step from one sample to the next
// around the switch is no larger than either voice's own, and turning off
// ends on the input to the bit with no delay.
static void check_switches(void) {
  static const int steps[] = {VOICE_DEEP,  VOICE_ROBOT,   VOICE_OFF, VOICE_WITCH, VOICE_MUMMY,
                              VOICE_RADIO, VOICE_CUSTOM,  VOICE_HIGH, VOICE_OFF,  VOICE_FRANKENSTEIN,
                              VOICE_ALIEN, VOICE_WEREWOLF, VOICE_DRACULA, VOICE_OFF};
  const size_t every = RATE / 2;  // half a second per voice
  Host *h = stage(RATE, VOICE_OFF);
  assert(set_control(h, "voice_pitch", 7));
  assert(set_control(h, "voice_formant", 2));
  size_t at = 0;
  for (size_t i = 0; i <= sizeof(steps) / sizeof(steps[0]); i++) {
    if (i > 0) {
      int preset = steps[i - 1];
      set_control(h, "voice_enabled", preset != VOICE_OFF);
      if (preset != VOICE_OFF) set_control(h, "voice_preset", (float)preset);
    }
    for (size_t end = at + every; at < end; at += BLOCK) block(h, buzz + at, output + at, BLOCK);
  }
  // The switch takes the shifter's delay and the fade.
  const size_t settle = SHIFT_BLOCK + RATE / 50 + BLOCK;
  double worst = 0;
  for (size_t i = 1; i <= sizeof(steps) / sizeof(steps[0]); i++) {
    size_t from = i * every;
    double before = 0, during = 0, after = 0;
    for (size_t n = from - every / 2; n < from; n++) before = fmax(before, fabs(output[n] - output[n - 1]));
    for (size_t n = from; n < from + settle; n++) during = fmax(during, fabs(output[n] - output[n - 1]));
    for (size_t n = from + settle; n < from + every; n++) after = fmax(after, fabs(output[n] - output[n - 1]));
    double ratio = during / fmax(before, after);
    worst = fmax(worst, ratio);
    printf("switch to %-12s largest step %.4f, %.2f of the larger steady one\n", voice_name(steps[i - 1]), during,
           ratio);
    assert(ratio <= 1.1);
  }
  // It ended off: the last voice faded out and the input comes back exact.
  size_t end = (sizeof(steps) / sizeof(steps[0]) + 1) * every;
  size_t back = end - every + settle;
  assert(!memcmp(buzz + back, output + back, (end - back) * sizeof(float)));
  assert(eq_latency(h) == 0 && meter(h, "voice_active") == VOICE_OFF);
  release(h);
  printf("switches: no step larger than %.2f of the voices' own; off is exact again\n", worst);
}

// The stage at another rate keeps its 33 ms; one the shifter cannot work at
// stays dry and says why.
static void check_rates(void) {
  Host *h = stage(44100, VOICE_DEEP);
  run(h, voice, output, RATE, BLOCK);
  assert(eq_latency(h) == 1470 && meter(h, "voice_status") == VOICE_OK);
  for (size_t n = 0; n < RATE; n++) assert(fabsf(output[n]) <= 1);
  release(h);
  h = stage(8000, VOICE_DEEP);
  run(h, voice, output, RATE, BLOCK);
  assert(!memcmp(voice, output, RATE * sizeof(float)));
  assert(eq_latency(h) == 0 && meter(h, "voice_status") == VOICE_NOT_STARTED);
  assert(meter(h, "voice_active") == VOICE_OFF);
  release(h);
  puts("rates: 44.1 kHz keeps 33 ms; 8 kHz stays dry and says the voice could not start");
}

// --- rendering -----------------------------------------------------------------

static int render(const char *path, const char *directory) {
  SF_INFO info = {0};
  SNDFILE *file = sf_open(path, SFM_READ, &info);
  if (!file) {
    fprintf(stderr, "%s: %s\n", path, sf_strerror(NULL));
    return 1;
  }
  size_t count = (size_t)info.frames;
  float *frames = malloc(sizeof(float) * count * info.channels);
  float *in = malloc(sizeof(float) * count), *out = malloc(sizeof(float) * count);
  assert(frames && in && out);
  assert(sf_readf_float(file, frames, info.frames) == info.frames);
  sf_close(file);
  for (size_t n = 0; n < count; n++) {
    double sum = 0;
    for (int c = 0; c < info.channels; c++) sum += frames[n * info.channels + c];
    in[n] = (float)(sum / info.channels);
  }
  for (int preset = -1; preset < VOICE_COUNT; preset++) {
    Host *h = stage((uint32_t)info.samplerate, preset);
    run(h, in, out, count, BLOCK);
    size_t latency = eq_latency(h);
    double si = 0, so = 0;
    float top = 0;
    for (size_t n = 0; n + latency < count; n++) si += (double)in[n] * in[n], so += (double)out[n + latency] * out[n + latency];
    for (size_t n = 0; n < count; n++) top = fmaxf(top, fabsf(out[n]));
    char name[4096];
    snprintf(name, sizeof(name), "%s/%02d-%s.wav", directory, preset + 1, preset < 0 ? "dry" : voice_name(preset));
    SF_INFO written = {.samplerate = info.samplerate, .channels = 1, .format = SF_FORMAT_WAV | SF_FORMAT_PCM_16};
    SNDFILE *o = sf_open(name, SFM_WRITE, &written);
    if (!o) {
      fprintf(stderr, "%s: %s\n", name, sf_strerror(NULL));
      return 1;
    }
    assert(sf_writef_float(o, out, (sf_count_t)count) == (sf_count_t)count);
    sf_close(o);
    printf("%s: level %+.1f dB from the input, peak %.2f\n", name, 10 * log10((so + 1e-30) / (si + 1e-30)), top);
    release(h);
  }
  free(frames), free(in), free(out);
  return 0;
}

// The stage's delay is noise removal's plus the voice changer's, and each
// part reports its own share, so the daemon can show them apart.
static void check_split(void) {
  Host *h = stage_with_noise(VOICE_CUSTOM);
  run(h, voice, output, RATE, BLOCK);
  float noise = meter(h, "denoise_latency"), shift = meter(h, "voice_latency");
  size_t total = eq_latency(h);
  assert(meter(h, "denoise_active") == 0 && meter(h, "voice_active") == VOICE_CUSTOM);
  assert(noise > 0 && shift == SHIFT_BLOCK && noise + shift == (float)total);
  release(h);
  printf("split: noise removal %.0f and the voice changer %.0f samples, the stage %zu\n", noise, shift, total);
}

int main(int argc, char **argv) {
  setvbuf(stdout, NULL, _IOLBF, 0);
  if (argc == 4 && !strcmp(argv[1], "--render")) return render(argv[2], argv[3]);
  make_voice();
  make_buzz();
  check_controls();
  check_latency();
  for (int preset = 0; preset < VOICE_COUNT; preset++) check_voice(preset);
  check_switches();
  check_rates();
  check_split();
  puts("voice: all checks passed");
  return 0;
}
