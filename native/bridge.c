// SPDX-License-Identifier: GPL-3.0-only
// The clock bridge (bridge.h): its ring, its loop and its resampler, then
// the two PipeWire nodes that drive them.
#include "bridge.h"

#include <errno.h>
#include <fcntl.h>
#include <math.h>
#include <pipewire/filter.h>
#include <pipewire/pipewire.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

// --- the kernel -------------------------------------------------------------

// A Kaiser-windowed sinc, BRIDGE_TAPS long, in BRIDGE_PHASES + 1 rows: row p
// is the kernel for an output frame p / BRIDGE_PHASES of the way from one
// input frame to the next, and the last row is the first moved by one
// frame, so a position between two rows interpolates between them. The
// cutoff sits below the Nyquist frequency so that the ratio's small moves
// never fold anything back into the audio; each row sums to one, so a
// steady level stays exactly where it was.
static float kernel[BRIDGE_PHASES + 1][BRIDGE_TAPS];
static atomic_bool kernel_built;

static double bessel_i0(double x) {
  double sum = 1, term = 1;
  for (int k = 1; k < 64; ++k) {
    term *= (x / (2 * k)) * (x / (2 * k));
    sum += term;
    if (term < sum * 1e-17) break;
  }
  return sum;
}

void bridge_kernel_init(void) {
  if (atomic_load(&kernel_built)) return;
  const double cutoff = 0.92, beta = 9.0, norm = bessel_i0(beta);
  for (int p = 0; p <= BRIDGE_PHASES; ++p) {
    double frac = (double)p / BRIDGE_PHASES, sum = 0;
    double row[BRIDGE_TAPS];
    for (int t = 0; t < BRIDGE_TAPS; ++t) {
      // The distance from the output frame to this tap's input frame.
      double d = frac + BRIDGE_HALF - 1 - t;
      double x = d / BRIDGE_HALF;
      double window = fabs(x) >= 1 ? 0 : bessel_i0(beta * sqrt(1 - x * x)) / norm;
      double arg = M_PI * cutoff * d;
      double sinc = fabs(arg) < 1e-12 ? 1 : sin(arg) / arg;
      row[t] = cutoff * sinc * window;
      sum += row[t];
    }
    for (int t = 0; t < BRIDGE_TAPS; ++t) kernel[p][t] = (float)(row[t] / sum);
  }
  atomic_store(&kernel_built, true);
}

// --- set-up -----------------------------------------------------------------

// The resampler's input holds the kernel's span and the most one cycle can
// pull at the largest ratio the bridge accepts.
enum { HIST_FRAMES = 4 * BRIDGE_MAX_QUANTUM + 4 * BRIDGE_TAPS };
#define MAX_NOMINAL 4.0

bool bridge_init(Bridge *b, unsigned channels, double quanta, double margin_ms) {
  memset(b, 0, sizeof(*b));
  if (channels < 1 || channels > BRIDGE_MAX_CHANNELS || !(quanta >= 0.5) ||
      quanta > 16 || !(margin_ms >= 0) || margin_ms > 500)
    return false;
  bridge_kernel_init();
  b->channels = channels;
  b->quanta = quanta;
  b->margin_ms = margin_ms;
  b->ring = calloc((size_t)channels * BRIDGE_RING, sizeof(float));
  for (unsigned c = 0; c < channels; ++c)
    b->hist[c] = calloc(HIST_FRAMES, sizeof(float));
  for (unsigned c = 0; c < channels; ++c)
    if (!b->hist[c]) {
      bridge_free(b);
      return false;
    }
  if (!b->ring) {
    bridge_free(b);
    return false;
  }
  b->priming = true;
  b->corr = 1;
  spa_dll_init(&b->dll);
  return true;
}

void bridge_free(Bridge *b) {
  free(b->ring);
  b->ring = NULL;
  for (unsigned c = 0; c < BRIDGE_MAX_CHANNELS; ++c) {
    free(b->hist[c]);
    b->hist[c] = NULL;
  }
}

uint32_t bridge_target(const Bridge *b, uint32_t in_quantum, uint32_t in_rate,
                       uint32_t out_quantum, uint32_t out_rate) {
  // The device's cycle arrives at once, so the reader can find the ring a
  // whole device cycle lower than it was a moment before; the mixer's cycles
  // are what the reader takes at once, and the margin is for a device whose
  // cycles come late or bunched, which is the kind this bridge is for.
  double nominal = (double)in_rate / out_rate;
  double target = in_quantum + b->quanta * out_quantum * nominal + BRIDGE_HALF +
                  b->margin_ms * in_rate / 1000.0;
  if (target > BRIDGE_RING / 4) target = BRIDGE_RING / 4;
  return (uint32_t)ceil(target);
}

// --- the writer -------------------------------------------------------------

static void write_frames(Bridge *b, const float *const *in, uint32_t frames, uint64_t written);

void bridge_produce(Bridge *b, const float *const *in, uint32_t frames,
                    uint32_t rate, uint64_t nsec, uint64_t next_nsec) {
  if (frames == 0 || frames > BRIDGE_MAX_QUANTUM || rate == 0) return;
  uint64_t written = atomic_load_explicit(&b->written, memory_order_relaxed);
  uint64_t read = atomic_load_explicit(&b->read, memory_order_acquire);
  bool full = written - read + frames > BRIDGE_RING;
  if (full) {
    // The mixer side stopped taking: this cycle is lost. Counted once per
    // stretch, so a mixer that pauses for a minute is one overrun, not
    // thousands; the reader re-centres when it comes back. The cycle's time
    // is still published, so the reader counts only what the ring holds.
    if (!b->p_full) atomic_fetch_add(&b->overruns, 1);
    b->p_full = true;
  } else {
    b->p_full = false;
    write_frames(b, in, frames, written);
    written += frames;
    atomic_store_explicit(&b->written, written, memory_order_release);
  }
  uint32_t seq = atomic_load_explicit(&b->seq, memory_order_relaxed);
  atomic_store_explicit(&b->seq, seq + 1, memory_order_relaxed);
  atomic_thread_fence(memory_order_release);
  atomic_store_explicit(&b->p_written, written, memory_order_relaxed);
  atomic_store_explicit(&b->p_nsec, nsec, memory_order_relaxed);
  atomic_store_explicit(&b->p_next_nsec, next_nsec, memory_order_relaxed);
  atomic_store_explicit(&b->p_quantum, frames, memory_order_relaxed);
  atomic_store_explicit(&b->p_rate, rate, memory_order_relaxed);
  atomic_store_explicit(&b->seq, seq + 2, memory_order_release);
}

static void write_frames(Bridge *b, const float *const *in, uint32_t frames, uint64_t written) {
  uint32_t at = (uint32_t)(written & (BRIDGE_RING - 1));
  uint32_t first = frames < BRIDGE_RING - at ? frames : BRIDGE_RING - at;
  for (unsigned c = 0; c < b->channels; ++c) {
    float *ring = b->ring + (size_t)c * BRIDGE_RING;
    if (in && in[c]) {
      memcpy(ring + at, in[c], first * sizeof(float));
      memcpy(ring, in[c] + first, (frames - first) * sizeof(float));
    } else {
      memset(ring + at, 0, first * sizeof(float));
      memset(ring, 0, (frames - first) * sizeof(float));
    }
  }
}

// --- the reader -------------------------------------------------------------

typedef struct {
  uint64_t written, nsec, next_nsec;
  uint32_t quantum, rate;
} WriterCycle;

static bool writer_cycle(Bridge *b, WriterCycle *w) {
  for (int tries = 0; tries < 8; ++tries) {
    uint32_t before = atomic_load_explicit(&b->seq, memory_order_acquire);
    if (before & 1) continue;
    w->written = atomic_load_explicit(&b->p_written, memory_order_relaxed);
    w->nsec = atomic_load_explicit(&b->p_nsec, memory_order_relaxed);
    w->next_nsec = atomic_load_explicit(&b->p_next_nsec, memory_order_relaxed);
    w->quantum = atomic_load_explicit(&b->p_quantum, memory_order_relaxed);
    w->rate = atomic_load_explicit(&b->p_rate, memory_order_relaxed);
    atomic_thread_fence(memory_order_acquire);
    if (atomic_load_explicit(&b->seq, memory_order_relaxed) == before) return true;
  }
  return false;
}

static void silence(Bridge *b, float *const *out, uint32_t from, uint32_t frames) {
  for (unsigned c = 0; c < b->channels; ++c)
    if (out[c] && frames > from) memset(out[c] + from, 0, (frames - from) * sizeof(float));
}

// Start over from the target: the resampler's input begins with half a
// kernel of silence, and the loop keeps what it learned about the drift.
static void restart(Bridge *b, uint32_t rate) {
  double drift = b->started ? b->dll.z3 : 0;
  spa_dll_init(&b->dll);
  b->dll.z3 = drift;
  b->dll_period = 0;
  b->bw = SPA_DLL_BW_MAX;
  b->locked_frames = 0;
  for (unsigned c = 0; c < b->channels; ++c)
    memset(b->hist[c], 0, BRIDGE_HALF * sizeof(float));
  b->have = BRIDGE_HALF;
  b->pos = BRIDGE_HALF;
  b->priming = false;
  b->started = true;
  (void)rate;
}

// The loop narrows after a start: wide for five seconds so it finds the
// drift quickly, then halving every five seconds down to PipeWire's own
// narrowest, where the ratio moves too slowly for any pitch to be heard.
static void tune_loop(Bridge *b, uint32_t frames, uint32_t rate) {
  uint64_t step = b->locked_frames / (5ull * rate);
  double bw = step >= 3 ? SPA_DLL_BW_MIN : SPA_DLL_BW_MAX / (double)(1u << step);
  if (bw < SPA_DLL_BW_MIN) bw = SPA_DLL_BW_MIN;
  if (bw != b->bw || frames != b->dll_period || rate != b->dll_rate) {
    spa_dll_set_bw(&b->dll, bw, frames, rate);
    b->bw = bw;
    b->dll_period = frames;
    b->dll_rate = rate;
  }
}

static void report(Bridge *b, double fill, double nominal, uint32_t frames,
                   uint32_t rate) {
  double alpha = (double)frames / rate;  // about a second's memory
  if (alpha > 1) alpha = 1;
  b->fill_avg = b->fill_avg <= 0 ? fill : b->fill_avg + alpha * (fill - b->fill_avg);
  atomic_store_explicit(&b->delay, b->fill_avg / nominal, memory_order_relaxed);
  atomic_store_explicit(&b->ppm, (b->corr - 1) * 1e6, memory_order_relaxed);
}

void bridge_consume(Bridge *b, float *const *out, uint32_t frames,
                    uint32_t rate, uint64_t nsec) {
  atomic_store_explicit(&b->out_rate, rate, memory_order_relaxed);
  atomic_store_explicit(&b->out_quantum, frames, memory_order_relaxed);
  WriterCycle w;
  if (frames == 0 || frames > BRIDGE_MAX_QUANTUM || rate == 0 || !writer_cycle(b, &w) ||
      w.quantum == 0 || w.rate == 0) {
    silence(b, out, 0, frames);
    return;
  }
  double nominal = (double)w.rate / rate;
  if (nominal > MAX_NOMINAL || nominal < 1 / MAX_NOMINAL) {
    silence(b, out, 0, frames);
    atomic_store(&b->state, BRIDGE_WAITING);
    return;
  }
  atomic_store_explicit(&b->in_rate, w.rate, memory_order_relaxed);
  atomic_store_explicit(&b->in_quantum, w.quantum, memory_order_relaxed);
  uint64_t written = atomic_load_explicit(&b->written, memory_order_acquire);
  uint64_t read = atomic_load_explicit(&b->read, memory_order_relaxed);
  b->target = bridge_target(b, w.quantum, w.rate, frames, rate);
  atomic_store_explicit(&b->target_out, (uint32_t)(b->target / nominal), memory_order_relaxed);

  // The fill as if the device's frames came in one at a time: what it had
  // written at its last cycle, plus what it has captured since by its own
  // cycle's length, less what has been read. That takes away the sawtooth
  // a ring between two cycles shows, which would otherwise move the ratio
  // every time the two cycles slid past each other. A device cycle that
  // comes late has still captured its frames on time, so the count runs on
  // past one cycle: a late or bunched delivery is the headroom's to absorb,
  // not the loop's, and the ratio does not jump for it. A device that has
  // stopped runs the ring dry instead. Without the times, the middle of the
  // sawtooth.
  double elapsed = w.quantum * 0.5;
  if (nsec && w.nsec && w.next_nsec > w.nsec) {
    elapsed = (double)(int64_t)(nsec - w.nsec) * w.quantum / (double)(w.next_nsec - w.nsec);
    if (elapsed > b->target) elapsed = b->target;
    if (elapsed < -(double)w.quantum) elapsed = -(double)w.quantum;
  }
  double pending = b->priming ? 0 : b->have - b->pos;
  double fill = (double)(int64_t)(w.written - read) + elapsed + pending;
  uint32_t avail = (uint32_t)(written - read);

  if (b->priming) {
    // Wait for the target, then drop what is above it, so the delay starts
    // where it will stay.
    uint32_t want = (uint32_t)(frames * nominal) + BRIDGE_HALF + 2;
    if (fill < b->target || avail < want) {
      atomic_store(&b->state, BRIDGE_WAITING);
      silence(b, out, 0, frames);
      return;
    }
    double surplus = fill - b->target;
    uint32_t drop = surplus >= 1 ? (uint32_t)surplus : 0;
    if (drop > avail - want) drop = avail - want;
    read += drop;
    avail -= drop;
    fill -= drop;
    atomic_store_explicit(&b->read, read, memory_order_release);
    restart(b, rate);
  } else if (fill > 2.0 * b->target) {
    // Far above the target: the mixer side stalled, or the device sent a
    // burst the loop cannot take back in time. Drop back to the target.
    uint32_t drop = (uint32_t)(fill - b->target);
    if (drop > avail) drop = avail;
    read += drop;
    avail -= drop;
    fill -= drop;
    atomic_store_explicit(&b->read, read, memory_order_release);
    atomic_fetch_add(&b->overruns, 1);
    b->dll.z1 = b->dll.z2 = 0;
  }

  // The loop: below the target the ratio drops a little, so fewer device
  // frames go into each mixer cycle and the ring fills again; above it the
  // ratio rises. The error is limited, so one late cycle moves it no more
  // than a steady error of that size would.
  tune_loop(b, frames, rate);
  double limit = w.quantum > 256 ? w.quantum : 256;
  double err = b->target - fill;
  if (err > limit) err = limit;
  if (err < -limit) err = -limit;
  double corr = spa_dll_update(&b->dll, err);
  if (corr > 1 + BRIDGE_MAX_CORRECTION) corr = 1 + BRIDGE_MAX_CORRECTION;
  if (corr < 1 - BRIDGE_MAX_CORRECTION) corr = 1 - BRIDGE_MAX_CORRECTION;
  b->corr = corr;
  double ratio = nominal * corr;

  // Take what this cycle needs from the ring.
  double last = b->pos + (frames - 1) * ratio;
  uint32_t need = (uint32_t)last + BRIDGE_HALF + 1;
  uint32_t pull = need > b->have ? need - b->have : 0;
  if (pull > avail) pull = avail;
  if (b->have + pull > HIST_FRAMES) pull = HIST_FRAMES - b->have;
  uint32_t at = (uint32_t)(read & (BRIDGE_RING - 1));
  uint32_t first = pull < BRIDGE_RING - at ? pull : BRIDGE_RING - at;
  for (unsigned c = 0; c < b->channels; ++c) {
    const float *ring = b->ring + (size_t)c * BRIDGE_RING;
    memcpy(b->hist[c] + b->have, ring + at, first * sizeof(float));
    memcpy(b->hist[c] + b->have + first, ring, (pull - first) * sizeof(float));
  }
  b->have += pull;
  atomic_store_explicit(&b->read, read + pull, memory_order_release);

  // Each output frame from the kernel span around its position.
  uint32_t k = 0;
  for (; k < frames; ++k) {
    uint32_t i = (uint32_t)b->pos;
    if (i + BRIDGE_HALF >= b->have) break;
    double phase = (b->pos - i) * BRIDGE_PHASES;
    uint32_t p = (uint32_t)phase;
    if (p >= BRIDGE_PHASES) p = BRIDGE_PHASES - 1;
    float a = (float)(phase - p), keep = 1 - a;
    const float *k0 = kernel[p], *k1 = kernel[p + 1];
    for (unsigned c = 0; c < b->channels; ++c) {
      const float *x = b->hist[c] + i + 1 - BRIDGE_HALF;
      float sum = 0;
      for (int t = 0; t < BRIDGE_TAPS; ++t) sum += x[t] * (keep * k0[t] + a * k1[t]);
      if (out[c]) out[c][k] = sum;
    }
    b->pos += ratio;
  }
  if (k < frames) {
    // The ring ran dry: play what there was, silence for the rest, and wait
    // for the target again.
    silence(b, out, k, frames);
    atomic_fetch_add(&b->underruns, 1);
    b->priming = true;
    atomic_store(&b->state, BRIDGE_WAITING);
    report(b, fill, nominal, frames, rate);
    return;
  }

  // Keep only the input the next output frame's kernel still reaches.
  uint32_t i = (uint32_t)b->pos;
  if (i >= BRIDGE_HALF) {
    uint32_t drop = i + 1 - BRIDGE_HALF;
    if (drop > b->have) drop = b->have;
    for (unsigned c = 0; c < b->channels; ++c)
      memmove(b->hist[c], b->hist[c] + drop, (b->have - drop) * sizeof(float));
    b->have -= drop;
    b->pos -= drop;
  }
  b->locked_frames += frames;
  atomic_store(&b->state, BRIDGE_RUNNING);
  report(b, fill, nominal, frames, rate);
}

// --- the two nodes ----------------------------------------------------------

typedef struct {
  Bridge core;
  struct pw_main_loop *loop;
  struct pw_context *context;
  struct pw_core *pw;
  struct pw_filter *in, *out;
  struct spa_hook in_hook, out_hook;
  struct spa_source *sources[4];
  void *in_ports[BRIDGE_MAX_CHANNELS];
  void *out_ports[BRIDGE_MAX_CHANNELS];
  float *scratch[BRIDGE_MAX_CHANNELS];
  bool in_paused, out_paused, said_ready;
  int exit_code;
  unsigned ticks;
  double reported[8];
} App;

static void device_cycle(void *data, struct spa_io_position *position) {
  App *a = data;
  uint32_t frames = position->clock.duration;
  const float *in[BRIDGE_MAX_CHANNELS] = {0};
  if (frames <= BRIDGE_MAX_QUANTUM)
    for (unsigned c = 0; c < a->core.channels; ++c)
      in[c] = pw_filter_get_dsp_buffer(a->in_ports[c], frames);
  bridge_produce(&a->core, in, frames, position->clock.rate.denom,
                 position->clock.nsec, position->clock.next_nsec);
}

static void mixer_cycle(void *data, struct spa_io_position *position) {
  App *a = data;
  uint32_t frames = position->clock.duration;
  float *out[BRIDGE_MAX_CHANNELS] = {0};
  for (unsigned c = 0; c < a->core.channels; ++c) {
    out[c] = pw_filter_get_dsp_buffer(a->out_ports[c], frames);
    // An output nobody reads still has to advance the ring.
    if (!out[c] && frames <= BRIDGE_MAX_QUANTUM) out[c] = a->scratch[c];
  }
  bridge_consume(&a->core, out, frames, position->clock.rate.denom,
                 position->clock.nsec);
}

static void say_ready(App *a) {
  if (a->in_paused && a->out_paused && !a->said_ready) {
    a->said_ready = true;
    puts("ready");
  }
}

static void changed(App *a, bool *paused, enum pw_filter_state state, const char *error) {
  if (state == PW_FILTER_STATE_ERROR) {
    fprintf(stderr, "PipeWire: %s\n", error ? error : "disconnected");
    a->exit_code = 1;
    pw_main_loop_quit(a->loop);
  } else if (state == PW_FILTER_STATE_PAUSED || state == PW_FILTER_STATE_STREAMING) {
    *paused = true;
    say_ready(a);
  }
}

static void device_state(void *data, enum pw_filter_state old, enum pw_filter_state state,
                         const char *error) {
  App *a = data;
  changed(a, &a->in_paused, state, error);
}

static void mixer_state(void *data, enum pw_filter_state old, enum pw_filter_state state,
                        const char *error) {
  App *a = data;
  changed(a, &a->out_paused, state, error);
}

static const struct pw_filter_events device_events = {
    PW_VERSION_FILTER_EVENTS, .state_changed = device_state, .process = device_cycle};
static const struct pw_filter_events mixer_events = {
    PW_VERSION_FILTER_EVENTS, .state_changed = mixer_state, .process = mixer_cycle};

static struct pw_filter *make_node(App *a, const char *name, const char *description,
                                   uint32_t rate, const struct pw_filter_events *events,
                                   struct spa_hook *hook) {
  char fraction[32];
  snprintf(fraction, sizeof(fraction), "1/%u", rate);
  // Two nodes of one client: PipeWire puts each in the graph of whatever it
  // is linked to, and the two are never linked to each other.
  struct pw_filter *filter = pw_filter_new(
      a->pw, name,
      pw_properties_new(PW_KEY_NODE_NAME, name, PW_KEY_NODE_DESCRIPTION, description,
                        PW_KEY_MEDIA_TYPE, "Audio", PW_KEY_MEDIA_CATEGORY, "Filter",
                        PW_KEY_MEDIA_ROLE, "DSP", PW_KEY_NODE_RATE, fraction,
                        "node.autoconnect", "false", NULL));
  if (filter) pw_filter_add_listener(filter, hook, events, a);
  return filter;
}

static bool add_ports(App *a, struct pw_filter *filter, bool output, void **ports) {
  for (unsigned c = 0; c < a->core.channels; ++c) {
    char name[32];
    snprintf(name, sizeof(name), "%s_%u", output ? "capture" : "playback", c);
    ports[c] = pw_filter_add_port(
        filter, output ? PW_DIRECTION_OUTPUT : PW_DIRECTION_INPUT,
        PW_FILTER_PORT_FLAG_MAP_BUFFERS, 1,
        pw_properties_new(PW_KEY_FORMAT_DSP, "32 bit float mono audio", PW_KEY_PORT_NAME,
                          name, PW_KEY_AUDIO_CHANNEL,
                          a->core.channels == 1 ? "MONO" : c == 0 ? "FL" : "FR", NULL),
        NULL, 0);
    if (!ports[c]) return false;
  }
  return true;
}

static void meter(App *a, unsigned slot, const char *name, double value) {
  if (!isfinite(value) || value == a->reported[slot]) return;
  a->reported[slot] = value;
  printf("meter %s %.9g\n", name, value);
}

// Once a second: the beat, and what the bridge measured, each only when it
// changed. The delay is rounded to whole frames and the drift to a tenth of
// a part per million, so a steady bridge says nothing between changes.
static void tick(void *data, uint64_t expirations) {
  App *a = data;
  Bridge *b = &a->core;
  puts("heartbeat");
  puts("ui-heartbeat");
  int state = atomic_load(&b->state);
  meter(a, 0, "running", state == BRIDGE_RUNNING);
  meter(a, 1, "delay", state == BRIDGE_RUNNING ? round(atomic_load(&b->delay)) : 0);
  meter(a, 2, "target", atomic_load(&b->target_out));
  meter(a, 3, "underruns", (double)atomic_load(&b->underruns));
  meter(a, 4, "overruns", (double)atomic_load(&b->overruns));
  meter(a, 5, "drift", round(atomic_load(&b->ppm) * 10) / 10);
  meter(a, 6, "in_rate", atomic_load(&b->in_rate));
  meter(a, 7, "in_quantum", atomic_load(&b->in_quantum));
}

static void quit(void *data, int signal) { pw_main_loop_quit(((App *)data)->loop); }

// The daemon holds the other end of stdin; when it goes, so does the bridge.
static void on_stdin(void *data, int fd, uint32_t mask) {
  App *a = data;
  char buffer[256];
  ssize_t n = 0;
  if (mask & SPA_IO_IN) n = read(fd, buffer, sizeof(buffer));
  if ((mask & (SPA_IO_HUP | SPA_IO_ERR)) || n == 0 || (n < 0 && errno != EAGAIN))
    pw_main_loop_quit(a->loop);
}

static bool parse_setting(const char *argument, double *quanta, double *margin) {
  const char *equals = strchr(argument, '=');
  if (!equals) return false;
  char *end;
  double value = strtod(equals + 1, &end);
  if (*end || equals[1] == 0 || !isfinite(value)) return false;
  size_t length = (size_t)(equals - argument);
  if (length == 6 && !strncmp(argument, "quanta", 6)) *quanta = value;
  else if (length == 6 && !strncmp(argument, "margin", 6)) *margin = value;
  else return false;
  return true;
}

int bridge_main(int argc, char **argv) {
  if (argc < 4) {
    fputs("usage: openxlr-lv2-host bridge NODE CHANNELS RATE [quanta=N] [margin=MS]\n", stderr);
    return 2;
  }
  const char *node = argv[1];
  int channels = atoi(argv[2]);
  uint32_t rate = (uint32_t)strtoul(argv[3], NULL, 10);
  double quanta = BRIDGE_DEFAULT_QUANTA, margin = BRIDGE_DEFAULT_MARGIN_MS;
  for (int i = 4; i < argc; ++i)
    if (!parse_setting(argv[i], &quanta, &margin)) {
      fprintf(stderr, "bridge: unknown setting %s\n", argv[i]);
      return 2;
    }
  if (strlen(node) == 0 || strlen(node) > 200 || rate < 8000 || rate > 384000) return 2;
  static App a;
  if (!bridge_init(&a.core, (unsigned)channels, quanta, margin)) {
    fputs("bridge: needs 1 or 2 channels, quanta from 0.5 to 16 and a margin from 0 to 500 ms\n",
          stderr);
    return 2;
  }
  for (unsigned c = 0; c < a.core.channels; ++c)
    if (!(a.scratch[c] = calloc(BRIDGE_MAX_QUANTUM, sizeof(float)))) return 1;
  setvbuf(stdout, NULL, _IOLBF, 0);
  pw_init(NULL, NULL);
  a.loop = pw_main_loop_new(NULL);
  if (!a.loop) return 1;
  char input[256];
  snprintf(input, sizeof(input), "%s_in", node);
  a.context = pw_context_new(pw_main_loop_get_loop(a.loop), NULL, 0);
  a.pw = a.context ? pw_context_connect(a.context, NULL, 0) : NULL;
  if (!a.pw) {
    fputs("bridge: could not connect to PipeWire\n", stderr);
    a.exit_code = 1;
    goto done;
  }
  a.in = make_node(&a, input, "OpenXLR clock bridge input", rate, &device_events, &a.in_hook);
  a.out = make_node(&a, node, "OpenXLR clock bridge", rate, &mixer_events, &a.out_hook);
  if (!a.in || !a.out || !add_ports(&a, a.in, false, a.in_ports) ||
      !add_ports(&a, a.out, true, a.out_ports) ||
      pw_filter_connect(a.in, PW_FILTER_FLAG_RT_PROCESS, NULL, 0) < 0 ||
      pw_filter_connect(a.out, PW_FILTER_FLAG_RT_PROCESS, NULL, 0) < 0) {
    fputs("bridge: could not create its nodes\n", stderr);
    a.exit_code = 1;
    goto done;
  }
  struct pw_loop *loop = pw_main_loop_get_loop(a.loop);
  fcntl(STDIN_FILENO, F_SETFL, O_NONBLOCK);
  a.sources[0] = pw_loop_add_io(loop, STDIN_FILENO, SPA_IO_IN | SPA_IO_HUP | SPA_IO_ERR, false, on_stdin, &a);
  struct spa_source *timer = a.sources[1] = pw_loop_add_timer(loop, tick, &a);
  struct timespec second = {1, 0};
  pw_loop_update_timer(loop, timer, &second, &second, false);
  a.sources[2] = pw_loop_add_signal(loop, SIGTERM, quit, &a);
  a.sources[3] = pw_loop_add_signal(loop, SIGINT, quit, &a);
  for (unsigned i = 0; i < 8; ++i) a.reported[i] = NAN;
  pw_main_loop_run(a.loop);
done:
  for (unsigned i = 0; i < 4; ++i)
    if (a.sources[i]) pw_loop_destroy_source(pw_main_loop_get_loop(a.loop), a.sources[i]);
  if (a.in) pw_filter_destroy(a.in);
  if (a.out) pw_filter_destroy(a.out);
  if (a.pw) pw_core_disconnect(a.pw);
  if (a.context) pw_context_destroy(a.context);
  pw_main_loop_destroy(a.loop);
  bridge_free(&a.core);
  for (unsigned c = 0; c < BRIDGE_MAX_CHANNELS; ++c) free(a.scratch[c]);
  pw_deinit();
  return a.exit_code;
}
