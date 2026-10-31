// SPDX-License-Identifier: GPL-3.0-only
// The voice changer: the last thing the microphone stage (eq.c) does to a
// sample, after noise removal and the EQ, so the EQ shapes the human voice
// and the character goes on last.
//
// A voice is a row of the table below over a few building blocks, run in
// this order:
//
//   pitch and formant shift  Signalsmith Stretch (signalsmith/, MIT) through
//                            voice-stretch.cpp, with a slow pitch wobble on
//                            top for a vibrato. A voice that shifts nothing
//                            goes through a plain delay of the same length,
//                            so every voice is equally late.
//   ring modulator           the voice times a sine, mixed with itself
//   comb                     a short delay with feedback: fixed it rings
//                            metallic, swept it flanges
//   saturation               tanh, unity for quiet sounds
//   filters                  up to four biquads from eq_coefficients
//   reverb                   Freeverb's eight combs and four all-passes, mono
//   gain                     brings the voice back near the input's level
//
// The shifter analyses 33 ms blocks (1600 samples at 48 kHz) every 5 ms, and
// its block is its delay: the stage reports it as latency while a voice runs
// and 0 while the dry voice plays. Stretch's own presets use 100 to 120 ms
// blocks, too late for live speech. 1600 rather than 1440 (30 ms): Stretch
// pads the 1600 to a 2048-point transform and the 1440 only to 1536. On
// recorded speech Deep's autocorrelation peak rose from 0.54 to 0.59 (dry
// 0.67), and blocks of 45 and 60 ms reached only 0.60 and 0.61.
//
// Switching. Two slots each hold a shifter and the blocks' state, so a voice
// can start in one while the other still plays. A new voice first hears
// one shifter delay of input, while what played keeps playing, then the two
// crossfade over 20 ms at equal power. Turning the changer off crossfades
// the voice to the dry input; once that is done the stage copies its input
// to its output and adds nothing. A second switch waits for the first to
// finish. Custom's pitch and formant follow their sliders with no fade: the
// shifter moves to a new figure at its next 5 ms step.
//
// A voice that puts out something other than a number is cut off: its slot's
// output is replaced by the dry input, the stage fades to it and says so on
// voice_status until the voice is asked for again.
//
// Who owns what. The daemon writes the controls; the audio thread reads them
// once a block and owns the slots. The main thread allocates everything at
// load and frees it at unload; nothing allocates or locks while audio runs.
#include "voice.h"
#include "eq.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

enum {
  VOICE_FILTERS = 4,
  REVERB_COMBS = 8,
  REVERB_ALLPASSES = 4,
  DRY = -1,   // the dry input, where a slot index would be
  NONE = -2,  // no switch under way
};

static const double VOICE_PI = 3.14159265358979323846;
static const double SHIFT_SECONDS = 1600.0 / 48000, STEP_SECONDS = 0.005, FADE_SECONDS = 0.020;
static const double COMB_LONGEST = 0.025;  // seconds, the longest comb delay a voice may ask for
static const double REVERB_LARGEST = 1.5;  // the largest room scale a voice may ask for
// The rates the shifter's block makes sense at.
static const uint32_t RATE_LOWEST = 16000, RATE_HIGHEST = 192000;
// Below this a recursive state is silence; flushing keeps the arithmetic out
// of subnormal numbers as a tail decays.
#define VOICE_FLUSH 1e-20f

// Freeverb's tuning at 44.1 kHz (Jezar at Dreampoint, public domain).
static const unsigned REVERB_COMB_TUNING[REVERB_COMBS] = {1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617};
static const unsigned REVERB_ALLPASS_TUNING[REVERB_ALLPASSES] = {556, 441, 341, 225};

typedef struct {
  int type;         // eq.h's shapes
  float frequency;  // Hz; 0 leaves the filter out
  float gain;       // dB, for peaks and shelves
  float q;
} VoiceFilter;

typedef struct {
  const char *name;
  bool shift;                         // runs the shifter
  float pitch, formant;               // semitones
  float vibrato_rate, vibrato_depth;  // Hz, semitones either side
  float ring_frequency, ring_mix;     // Hz, 0 to 1
  float comb_ms, comb_sweep_ms, comb_rate, comb_feedback, comb_mix;
  float drive, drive_mix;             // tanh(drive x) / drive, mixed in
  VoiceFilter filters[VOICE_FILTERS];
  float reverb_size, reverb_feedback, reverb_damping, reverb_mix;
  float gain;  // dB after everything
} VoicePreset;

// The voices. The numbers were tuned on recorded speech so each voice comes
// out near the input's level; the maintainer tunes them further by ear.
static const VoicePreset PRESETS[VOICE_COUNT] = {
    [VOICE_CUSTOM] = {.name = "custom", .shift = true},
    [VOICE_DEEP] = {.name = "deep", .shift = true, .pitch = -4, .formant = -3,
                    .filters = {{LOW_SHELF, 180, 2, 0.7f}}, .gain = 0.5f},
    [VOICE_HIGH] = {.name = "high", .shift = true, .pitch = 5, .formant = 4,
                    .filters = {{LOW_CUT, 120, 0, 0.7f}}, .gain = 1.5f},
    [VOICE_ROBOT] = {.name = "robot", .ring_frequency = 55, .ring_mix = 1,
                     .comb_ms = 7.5f, .comb_feedback = 0.62f, .comb_mix = 0.7f,
                     .filters = {{LOW_CUT, 140, 0, 0.7f}, {PEAK, 1800, 4, 1.2f}},
                     .gain = 0.5f},
    [VOICE_RADIO] = {.name = "radio", .drive = 6, .drive_mix = 0.6f,
                     .filters = {{LOW_CUT, 300, 0, 0.8f}, {LOW_CUT, 300, 0, 0.8f},
                                 {HIGH_CUT, 3400, 0, 0.8f}, {PEAK, 1600, 5, 1.0f}},
                     .gain = 2.5f},
    [VOICE_ALIEN] = {.name = "alien", .shift = true, .pitch = 3, .formant = 5,
                     .ring_frequency = 420, .ring_mix = 0.35f,
                     .comb_ms = 2.5f, .comb_sweep_ms = 2, .comb_rate = 0.6f,
                     .comb_feedback = 0.55f, .comb_mix = 0.6f, .gain = 3},
    [VOICE_DRACULA] = {.name = "dracula", .shift = true, .pitch = -3, .formant = -2,
                       .filters = {{LOW_SHELF, 200, 2, 0.7f}},
                       .reverb_size = 1.4f, .reverb_feedback = 0.86f, .reverb_damping = 0.3f,
                       .reverb_mix = 0.35f, .gain = -0.5f},
    [VOICE_FRANKENSTEIN] = {.name = "frankenstein", .shift = true, .pitch = -6, .formant = -4,
                            .drive = 8, .drive_mix = 0.7f,
                            .filters = {{HIGH_CUT, 2800, 0, 0.7f}, {PEAK, 220, 3, 0.8f}},
                            .gain = 1.6f},
    [VOICE_MUMMY] = {.name = "mummy", .shift = true, .pitch = -2,
                     .drive = 10, .drive_mix = 0.6f,
                     .filters = {{LOW_CUT, 450, 0, 0.7f}, {HIGH_CUT, 3800, 0, 0.7f},
                                 {PEAK, 2400, 5, 1.5f}},
                     .reverb_size = 1.5f, .reverb_feedback = 0.9f, .reverb_damping = 0.5f,
                     .reverb_mix = 0.45f, .gain = 6},
    [VOICE_WEREWOLF] = {.name = "werewolf", .shift = true, .pitch = -5, .formant = -3,
                        .ring_frequency = 32, .ring_mix = 0.35f, .drive = 7, .drive_mix = 0.5f,
                        .filters = {{PEAK, 250, 3, 0.8f}, {HIGH_CUT, 5000, 0, 0.7f}},
                        .gain = 3.7f},
    [VOICE_WITCH] = {.name = "witch", .shift = true, .pitch = 4, .formant = 3,
                     .vibrato_rate = 6.5f, .vibrato_depth = 0.6f,
                     .filters = {{PEAK, 3000, 3, 1.0f}, {LOW_CUT, 150, 0, 0.7f}},
                     .reverb_size = 1.0f, .reverb_feedback = 0.8f, .reverb_damping = 0.4f,
                     .reverb_mix = 0.2f, .gain = 1},
};

const char *voice_name(int preset) {
  return preset >= 0 && preset < VOICE_COUNT ? PRESETS[preset].name : "off";
}

typedef struct {
  EqCoefficients c;
  double s1, s2;
} VoiceBiquad;

typedef struct {
  float *buffer;
  uint32_t length, at;
  float store;  // the comb's damping filter
} ReverbLine;

typedef struct {
  int voice;
  const VoicePreset *p;
  VoiceShifter *shifter;
  float pitch_now, formant_now;  // what the shifter was last given
  double vibrato_phase, ring_phase, sweep_phase;
  float *delay;  // stands in for the shifter in a voice that shifts nothing
  uint32_t delay_at;
  float *comb;
  uint32_t comb_at;
  VoiceBiquad filters[VOICE_FILTERS];
  ReverbLine combs[REVERB_COMBS], allpasses[REVERB_ALLPASSES];
  float *reverb_pool;
  size_t reverb_pool_size;
  float gain;
  float out[MAX_FRAMES];
} VoiceSlot;

struct Voice {
  Host *h;
  Control *enabled, *preset, *pitch, *formant;
  Control *active_meter, *status_meter, *latency_meter;
  bool ready;  // both slots and their shifters are there
  uint32_t shift_delay, delay_mask, comb_mask;
  uint32_t fade;
  VoiceSlot *slots[2];

  // The audio thread's own.
  int playing;    // the slot that plays, or DRY
  int incoming;   // the slot or DRY a switch goes to, or NONE
  uint32_t wait;  // samples before the incoming one fades in
  uint32_t faded; // samples of the fade done

  // Between the threads.
  _Atomic int broken;  // 1 + the voice that failed, 0 when none has
  _Atomic uint32_t latency;
};

static bool on(const Control *c) { return atomic_load(&c->desired) >= 0.5f; }

static int preset_of(const Control *c) {
  long preset = lrintf(atomic_load(&c->desired));
  return preset < 0 ? 0 : preset >= VOICE_COUNT ? VOICE_COUNT - 1 : (int)preset;
}

static float semitones(const Control *c) {
  float value = atomic_load(&c->desired);
  return isfinite(value) ? fminf(fmaxf(value, -12), 12) : 0;
}

static uint32_t power_of_two_above(uint32_t n) {
  uint32_t size = 1;
  while (size <= n) size <<= 1;
  return size;
}

// --- loading -------------------------------------------------------------------

static void slot_free(VoiceSlot *s) {
  if (!s) return;
  if (s->shifter) voice_shifter_free(s->shifter);
  free(s->delay);
  free(s->comb);
  free(s->reverb_pool);
  free(s);
}

static VoiceSlot *slot_new(Voice *v, uint32_t rate) {
  VoiceSlot *s = calloc(1, sizeof(*s));
  if (!s) return NULL;
  s->voice = VOICE_OFF;
  int block = (int)lrint(rate * SHIFT_SECONDS), interval = (int)lrint(rate * STEP_SECONDS);
  s->shifter = voice_shifter_create(block, interval);
  s->delay = calloc(v->delay_mask + 1, sizeof(float));
  s->comb = calloc(v->comb_mask + 1, sizeof(float));
  // Room for every line at the largest room a voice may ask for.
  double scale = rate / 44100.0 * REVERB_LARGEST;
  for (int i = 0; i < REVERB_COMBS; i++) s->reverb_pool_size += (size_t)ceil(REVERB_COMB_TUNING[i] * scale) + 1;
  for (int i = 0; i < REVERB_ALLPASSES; i++) s->reverb_pool_size += (size_t)ceil(REVERB_ALLPASS_TUNING[i] * scale) + 1;
  s->reverb_pool = calloc(s->reverb_pool_size, sizeof(float));
  if (!s->shifter || !s->delay || !s->comb || !s->reverb_pool) {
    slot_free(s);
    return NULL;
  }
  return s;
}

Voice *voice_load(Host *h) {
  if (h->channels != 1) return NULL;
  Voice *v = calloc(1, sizeof(*v));
  if (!v) return NULL;
  v->h = h;
  v->playing = DRY;
  v->incoming = NONE;
  atomic_init(&v->broken, 0);
  atomic_init(&v->latency, 0);
  v->enabled = host_add_control(h, "voice_enabled", 0, 1, 0, false, NULL);
  v->preset = host_add_control(h, "voice_preset", 0, VOICE_COUNT - 1, VOICE_DEEP, false, NULL);
  v->pitch = host_add_control(h, "voice_pitch", -12, 12, 0, false, NULL);
  v->formant = host_add_control(h, "voice_formant", -12, 12, 0, false, NULL);
  v->active_meter = host_add_control(h, "voice_active", VOICE_OFF, VOICE_COUNT - 1, VOICE_OFF, true, NULL);
  v->status_meter = host_add_control(h, "voice_status", 0, 2, VOICE_OK, true, NULL);
  v->latency_meter = host_add_control(h, "voice_latency", 0, 1e9f, 0, true, NULL);
  if (!v->enabled || !v->preset || !v->pitch || !v->formant || !v->active_meter || !v->status_meter
      || !v->latency_meter) {
    voice_unload(v);
    return NULL;
  }
  v->fade = (uint32_t)lrint(h->rate * FADE_SECONDS);
  if (v->fade == 0) v->fade = 1;
  if (h->rate < RATE_LOWEST || h->rate > RATE_HIGHEST) return v;
  v->shift_delay = (uint32_t)lrint(h->rate * SHIFT_SECONDS);
  v->delay_mask = power_of_two_above(v->shift_delay) - 1;
  v->comb_mask = power_of_two_above((uint32_t)ceil(h->rate * COMB_LONGEST) + 2) - 1;
  for (int i = 0; i < 2; i++) v->slots[i] = slot_new(v, h->rate);
  v->ready = v->slots[0] && v->slots[1];
  // The shifter's delay is its block; the stage reports what it says.
  if (v->ready) v->shift_delay = (uint32_t)voice_shifter_latency(v->slots[0]->shifter);
  if (v->ready && v->shift_delay > v->delay_mask) v->ready = false;
  return v;
}

void voice_unload(Voice *v) {
  if (!v) return;
  for (int i = 0; i < 2; i++) slot_free(v->slots[i]);
  free(v);
}

uint32_t voice_latency(const Voice *v) { return v ? atomic_load(&v->latency) : 0; }

// --- one voice in one slot -----------------------------------------------------

static void slot_follow(Voice *v, VoiceSlot *s, uint32_t frames) {
  const VoicePreset *p = s->p;
  if (!p->shift) return;
  float pitch = p->pitch, formant = p->formant;
  if (s->voice == VOICE_CUSTOM) pitch = semitones(v->pitch), formant = semitones(v->formant);
  if (p->vibrato_depth > 0) {
    pitch += p->vibrato_depth * (float)sin(s->vibrato_phase);
    s->vibrato_phase = fmod(s->vibrato_phase + 2 * VOICE_PI * p->vibrato_rate * frames / v->h->rate, 2 * VOICE_PI);
  }
  if (pitch != s->pitch_now || formant != s->formant_now) {
    voice_shifter_set(s->shifter, pitch, formant);
    s->pitch_now = pitch, s->formant_now = formant;
  }
}

// Make the slot run `voice` from silence, as if it had just been built.
static void slot_start(Voice *v, VoiceSlot *s, int voice) {
  uint32_t rate = v->h->rate;
  const VoicePreset *p = &PRESETS[voice];
  s->voice = voice;
  s->p = p;
  s->vibrato_phase = s->ring_phase = s->sweep_phase = 0;
  voice_shifter_reset(s->shifter);
  s->pitch_now = NAN;  // set on the first block
  slot_follow(v, s, 0);
  memset(s->delay, 0, sizeof(float) * (v->delay_mask + 1));
  s->delay_at = 0;
  memset(s->comb, 0, sizeof(float) * (v->comb_mask + 1));
  s->comb_at = 0;
  for (int i = 0; i < VOICE_FILTERS; i++) {
    const VoiceFilter *f = &p->filters[i];
    s->filters[i] = (VoiceBiquad){{1, 0, 0, 0, 0}, 0, 0};
    if (f->frequency > 0) eq_coefficients(f->type, rate, f->frequency, f->gain, f->q, &s->filters[i].c);
  }
  memset(s->reverb_pool, 0, sizeof(float) * s->reverb_pool_size);
  if (p->reverb_mix > 0) {
    double scale = rate / 44100.0 * fmin(fmax(p->reverb_size, 0.5), REVERB_LARGEST);
    float *at = s->reverb_pool;
    for (int i = 0; i < REVERB_COMBS; i++) {
      s->combs[i] = (ReverbLine){at, (uint32_t)lrint(REVERB_COMB_TUNING[i] * scale), 0, 0};
      at += (size_t)ceil(REVERB_COMB_TUNING[i] * rate / 44100.0 * REVERB_LARGEST) + 1;
    }
    for (int i = 0; i < REVERB_ALLPASSES; i++) {
      s->allpasses[i] = (ReverbLine){at, (uint32_t)lrint(REVERB_ALLPASS_TUNING[i] * scale), 0, 0};
      at += (size_t)ceil(REVERB_ALLPASS_TUNING[i] * rate / 44100.0 * REVERB_LARGEST) + 1;
    }
  }
  s->gain = powf(10, p->gain / 20);
}

static inline double biquad_run(VoiceBiquad *f, double x) {
  double y = f->c.b0 * x + f->s1;
  f->s1 = f->c.b1 * x - f->c.a1 * y + f->s2;
  f->s2 = f->c.b2 * x - f->c.a2 * y;
  if (fabs(f->s1) < VOICE_FLUSH && fabs(f->s2) < VOICE_FLUSH) f->s1 = f->s2 = 0;
  return y;
}

static inline float flush(float x) { return fabsf(x) < VOICE_FLUSH ? 0.0f : x; }

static float reverb_run(VoiceSlot *s, float x) {
  const VoicePreset *p = s->p;
  float in = x * 0.015f, wet = 0;  // Freeverb's fixed input gain
  float feedback = p->reverb_feedback, damping = p->reverb_damping;
  for (int i = 0; i < REVERB_COMBS; i++) {
    ReverbLine *c = &s->combs[i];
    float y = c->buffer[c->at];
    c->store = flush(y * (1 - damping) + c->store * damping);
    c->buffer[c->at] = flush(in + c->store * feedback);
    if (++c->at >= c->length) c->at = 0;
    wet += y;
  }
  for (int i = 0; i < REVERB_ALLPASSES; i++) {
    ReverbLine *a = &s->allpasses[i];
    float held = a->buffer[a->at];
    a->buffer[a->at] = flush(wet + held * 0.5f);
    if (++a->at >= a->length) a->at = 0;
    wet = held - wet;
  }
  return wet * 3;  // Freeverb's wet scale
}

// A soft ceiling: unity below 0.9, and never past 1.
static inline float ceiling(float x) {
  float a = fabsf(x);
  if (a <= 0.9f) return x;
  float y = 0.9f + 0.1f * tanhf((a - 0.9f) / 0.1f);
  return x < 0 ? -y : y;
}

// Run one block of `in` through the slot's voice into its out. False when
// the voice gave out something that is not a number; out then holds `in`.
static bool slot_run(Voice *v, VoiceSlot *s, const float *in, uint32_t frames) {
  const VoicePreset *p = s->p;
  double rate = v->h->rate;
  slot_follow(v, s, frames);
  if (p->shift) voice_shifter_process(s->shifter, in, s->out, (int)frames);
  else
    for (uint32_t n = 0; n < frames; n++) {
      s->delay[s->delay_at] = in[n];
      s->out[n] = s->delay[(s->delay_at - v->shift_delay) & v->delay_mask];
      s->delay_at = (s->delay_at + 1) & v->delay_mask;
    }
  double ring_step = 2 * VOICE_PI * p->ring_frequency / rate;
  double sweep_step = 2 * VOICE_PI * p->comb_rate / rate;
  bool ok = true;
  for (uint32_t n = 0; n < frames; n++) {
    double x = s->out[n];
    if (p->ring_mix > 0) {
      x *= 1 - p->ring_mix + p->ring_mix * sin(s->ring_phase);
      s->ring_phase += ring_step;
      if (s->ring_phase >= 2 * VOICE_PI) s->ring_phase -= 2 * VOICE_PI;
    }
    if (p->comb_mix > 0) {
      double ms = p->comb_ms + p->comb_sweep_ms * 0.5 * (1 + sin(s->sweep_phase));
      s->sweep_phase += sweep_step;
      if (s->sweep_phase >= 2 * VOICE_PI) s->sweep_phase -= 2 * VOICE_PI;
      double back = fmin(fmax(ms * rate / 1000, 1), v->comb_mask - 1);
      uint32_t whole = (uint32_t)back;
      double frac = back - whole;
      float a = s->comb[(s->comb_at - whole) & v->comb_mask];
      float b = s->comb[(s->comb_at - whole - 1) & v->comb_mask];
      double d = a + (b - a) * frac;
      s->comb[s->comb_at] = flush((float)(x + p->comb_feedback * d));
      s->comb_at = (s->comb_at + 1) & v->comb_mask;
      x += p->comb_mix * d;
    }
    if (p->drive_mix > 0) x += p->drive_mix * (tanh(p->drive * x) / p->drive - x);
    for (int i = 0; i < VOICE_FILTERS; i++)
      if (p->filters[i].frequency > 0) x = biquad_run(&s->filters[i], x);
    if (p->reverb_mix > 0) x += p->reverb_mix * reverb_run(s, (float)x);
    float y = ceiling((float)(x * s->gain));
    if (!isfinite(y)) ok = false;
    s->out[n] = y;
  }
  if (!ok) memcpy(s->out, in, sizeof(float) * frames);
  return ok;
}

// --- switching -----------------------------------------------------------------

// What the controls ask to hear now, as far as it can run.
static int wanted(Voice *v) {
  if (!on(v->enabled) || !v->ready) {
    atomic_store(&v->broken, 0);
    return VOICE_OFF;
  }
  int preset = preset_of(v->preset);
  int broken = atomic_load(&v->broken);
  // A failure holds until another voice, or the same one anew, is asked for.
  if (broken && broken != preset + 1) atomic_store(&v->broken, broken = 0);
  return broken ? VOICE_OFF : preset;
}

static int voice_of(const Voice *v, int slot) { return slot == DRY ? VOICE_OFF : v->slots[slot]->voice; }

static void publish(Voice *v) {
  bool shifting = v->playing != DRY || (v->incoming != NONE && v->incoming != DRY);
  atomic_store(&v->latency, shifting ? v->shift_delay : 0);
  atomic_store(&v->latency_meter->observed, shifting ? (float)v->shift_delay : 0.0f);
  atomic_store(&v->active_meter->observed, (float)voice_of(v, v->playing));
}

bool voice_activate(Voice *v) {
  int voice = wanted(v);
  v->incoming = NONE;
  v->playing = DRY;
  if (voice != VOICE_OFF) {
    slot_start(v, v->slots[0], voice);
    v->playing = 0;
  }
  publish(v);
  voice_main_thread(v);
  return true;
}

void voice_main_thread(Voice *v) {
  int status = VOICE_OK;
  if (on(v->enabled)) {
    if (!v->ready) status = VOICE_NOT_STARTED;
    else if (atomic_load(&v->broken)) status = VOICE_FELL_BEHIND;
  }
  atomic_store(&v->status_meter->observed, (float)status);
}

void voice_process(Voice *v, const float *in, float *out, uint32_t frames) {
  int want = wanted(v);
  if (v->incoming == NONE && want != voice_of(v, v->playing)) {
    if (want == VOICE_OFF) {
      v->incoming = DRY;
      v->wait = 0;
    } else {
      v->incoming = v->playing == 0 ? 1 : 0;
      slot_start(v, v->slots[v->incoming], want);
      // The new voice hears one shifter delay of input before it fades in.
      v->wait = v->shift_delay;
    }
    v->faded = 0;
    publish(v);
  }
  if (v->playing == DRY && v->incoming == NONE) {
    memcpy(out, in, sizeof(float) * frames);
    return;
  }
  const float *now = in, *next = NULL;
  if (v->playing != DRY) {
    VoiceSlot *s = v->slots[v->playing];
    if (!slot_run(v, s, in, frames)) atomic_store(&v->broken, s->voice + 1);
    now = s->out;
  }
  if (v->incoming == DRY) next = in;
  else if (v->incoming != NONE) {
    VoiceSlot *s = v->slots[v->incoming];
    if (!slot_run(v, s, in, frames)) atomic_store(&v->broken, s->voice + 1);
    next = s->out;
  }
  for (uint32_t n = 0; n < frames; n++) {
    if (!next) {
      out[n] = now[n];
      continue;
    }
    if (v->wait) {
      v->wait--;
      out[n] = now[n];
      continue;
    }
    double t = (double)++v->faded / v->fade * (VOICE_PI / 2);
    out[n] = (float)(cos(t) * now[n] + sin(t) * next[n]);
    if (v->faded == v->fade) {
      // The rest of the block is the new one's alone.
      v->playing = v->incoming;
      v->incoming = NONE;
      now = next;
      next = NULL;
      publish(v);
    }
  }
}
