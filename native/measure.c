// SPDX-License-Identifier: GPL-3.0-only
// The delay a plugin's node really adds, measured at the edges of the audio
// callback, so it holds everything the node adds: the plugin's own delay,
// declared or not, and the host's own block when that is on.
//
// The audio thread writes a mono mix of the input and of the output into two
// rings, at the same positions. Once a second of new audio has arrived the
// main thread looks at the last three seconds of output and the four seconds
// of input that can explain them:
//
// 1. Coarse. Both sides become envelopes, the RMS of each millisecond
//    averaged over the 11 around it, which smooths away the ripple of the
//    voice's pitch without moving anything, and the output's envelope is
//    correlated with the input's at every lag from 0 to 1 s (Pearson's
//    coefficient, so level and offset do not count). Envelopes survive what
//    a noise remover does to speech, which changes the waveform little and
//    the level of the pauses a lot.
// 2. Fine. Around the coarse peak, seven milliseconds either side, which is
//    more than the smoothing leaves it uncertain by, the waveforms
//    themselves are correlated, sample by sample, over the 250 milliseconds
//    where both sides are loudest. The peak of that is the figure, to the
//    sample. With a narrower search, DeepFilterNet's first seconds put the
//    coarse peak 6 ms early and a voiced stretch matched one pitch period
//    away from the truth.
//
// A figure counts only when the match is clear:
// - at least 400 ms of the window carries signal above -50 dBFS on both
//   sides at the coarse lag. That floor sits above a microphone chain's own
//   noise and below quiet speech, so pauses and a muted input do not count;
// - the envelopes correlate at 0.6 or more. Unrelated noise reached 0.15 in
//   tests/measure.c, and RNNoise and DeepFilterNet over the same speech
//   0.98 or more;
// - no other peak more than 25 ms away, one that rises 0.05 above the dip
//   between it and the best, comes within 0.05 of the best, so a periodic
//   signal that matches at several lags does not pick one;
// - the waveforms correlate at 0.5 or more, either way up. Noise shaped
//   like the input's envelope passes the coarse step and fails here at
//   0.03; RNNoise and DeepFilterNet kept 0.96 or more;
// - the peak is inside both search ranges, not on their edge;
// - three analyses in a row agree to within two samples.
// Until then nothing is reported; afterwards the last confident figure stays
// until three agreeing analyses give another one.
#include "measure.h"

#include <math.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

enum {
  WINDOW_HOPS = 3000,  // output looked at: 3 s of 1 ms hops
  LAG_HOPS = 1000,     // lags searched: up to 1 s
  MIN_ACTIVE = 400,    // hops with signal on both sides
  PICKS = 250,         // hops the waveforms are compared over
  SIDE_HOPS = 25,      // how far another peak has to be to count as another
  SMOOTH_HOPS = 5,     // the envelopes are averaged this far either side
  AGREE = 3,           // analyses in a row that have to agree
  TOLERANCE = 2,       // samples they may differ by
  // The most frames the audio thread can write while an analysis reads: a
  // ring that wrapped further than this under the reader is not trusted.
  CYCLE_MARGIN = 16384
};
static const float FLOOR = 0.0031623f;  // -50 dBFS
static const double COARSE_MIN = 0.6, FINE_MIN = 0.5, DISTINCT = 0.05;
static const float PROMINENCE = 0.05f;  // what makes a bump another peak

typedef struct {
  float score;
  unsigned hop;
} Pick;

struct LatencyMeter {
  uint32_t rate, hop, reach;  // reach: how far the fine search goes either side
  uint64_t size, mask;
  float *in, *out;
  _Atomic uint64_t written;  // frames in both rings, written by the audio thread
  // Main thread only.
  uint64_t analysed;
  float *in_env, *out_env, *in_smooth, *out_smooth, *coarse;
  double *sum, *squares, *xy, *xx;
  float *xs, *ys;  // one picked hop's output, and the input it may match
  double *xs_squares;  // running sums of the squares of xs
  Pick *picks;
  int64_t candidate, published;
  unsigned streak, count;
  LatencyAnalysis last;
};

LatencyMeter *latency_meter_new(uint32_t rate) {
  LatencyMeter *m = calloc(1, sizeof(*m));
  if (!m) return NULL;
  m->rate = rate;
  m->hop = rate >= 2000 ? rate / 1000 : 1;
  m->reach = (SMOOTH_HOPS + 2) * m->hop + 2;
  // Enough for the input an analysis reads, and a second more that the
  // audio thread can write meanwhile without reaching it.
  uint64_t need = (uint64_t)(WINDOW_HOPS + LAG_HOPS) * m->hop + m->reach + rate;
  m->size = 1;
  while (m->size < need) m->size <<= 1;
  m->mask = m->size - 1;
  m->in = calloc(m->size, sizeof(float));
  m->out = calloc(m->size, sizeof(float));
  m->in_env = calloc(WINDOW_HOPS + LAG_HOPS, sizeof(float));
  m->out_env = calloc(WINDOW_HOPS, sizeof(float));
  m->in_smooth = calloc(WINDOW_HOPS + LAG_HOPS, sizeof(float));
  m->out_smooth = calloc(WINDOW_HOPS, sizeof(float));
  m->coarse = calloc(LAG_HOPS + 1, sizeof(float));
  m->sum = calloc(WINDOW_HOPS + LAG_HOPS + 1, sizeof(double));
  m->squares = calloc(WINDOW_HOPS + LAG_HOPS + 1, sizeof(double));
  m->xy = calloc(2 * m->reach + 1, sizeof(double));
  m->xx = calloc(2 * m->reach + 1, sizeof(double));
  m->picks = calloc(WINDOW_HOPS, sizeof(Pick));
  m->xs = calloc(m->hop + 2 * m->reach + 1, sizeof(float));
  m->ys = calloc(m->hop, sizeof(float));
  m->xs_squares = calloc(m->hop + 2 * m->reach + 2, sizeof(double));
  m->candidate = m->published = -1;
  m->last.lag = -1;
  atomic_init(&m->written, 0);
  if (!m->in || !m->out || !m->in_env || !m->out_env || !m->in_smooth ||
      !m->out_smooth || !m->coarse ||
      !m->sum || !m->squares || !m->xy || !m->xx || !m->picks || !m->xs ||
      !m->ys || !m->xs_squares) {
    latency_meter_free(m);
    return NULL;
  }
  return m;
}

void latency_meter_free(LatencyMeter *m) {
  if (!m) return;
  free(m->in), free(m->out), free(m->in_env), free(m->out_env);
  free(m->in_smooth), free(m->out_smooth);
  free(m->coarse), free(m->sum), free(m->squares), free(m->xy), free(m->xx);
  free(m->picks), free(m->xs), free(m->ys), free(m->xs_squares);
  free(m);
}

// --- the audio thread ---------------------------------------------------------

static void mix_into(float *ring, uint64_t mask, uint64_t at,
                     float *const *buffers, unsigned channels, uint32_t frames) {
  if (channels == 1) {
    for (uint32_t n = 0; n < frames; ++n) ring[(at + n) & mask] = buffers[0][n];
    return;
  }
  float scale = 1.0f / (float)channels;
  for (uint32_t n = 0; n < frames; ++n) {
    float sum = 0;
    for (unsigned c = 0; c < channels; ++c) sum += buffers[c][n];
    ring[(at + n) & mask] = sum * scale;
  }
}

void latency_meter_input(LatencyMeter *m, float *const *in, unsigned channels,
                         uint32_t frames) {
  mix_into(m->in, m->mask, atomic_load_explicit(&m->written, memory_order_relaxed),
           in, channels, frames);
}

void latency_meter_output(LatencyMeter *m, float *const *out, unsigned channels,
                          uint32_t frames) {
  uint64_t at = atomic_load_explicit(&m->written, memory_order_relaxed);
  mix_into(m->out, m->mask, at, out, channels, frames);
  atomic_store_explicit(&m->written, at + frames, memory_order_release);
}

// --- the main thread ----------------------------------------------------------

static void envelope(const float *ring, uint64_t mask, uint64_t from,
                     uint32_t hop, unsigned hops, float *into) {
  for (unsigned k = 0; k < hops; ++k) {
    float sum = 0;
    uint64_t at = from + (uint64_t)k * hop;
    for (uint32_t n = 0; n < hop; ++n) {
      float x = ring[(at + n) & mask];
      sum += x * x;
    }
    into[k] = sqrtf(sum / (float)hop);
  }
}

// A moving average centred on each hop, so it delays neither envelope.
static void smooth(const float *from, unsigned count, float *into) {
  double sum = 0;
  unsigned low = 0, high = 0;  // the hops summed: [low, high)
  for (unsigned k = 0; k < count; ++k) {
    while (high < count && high <= k + SMOOTH_HOPS) sum += from[high++];
    while (low + SMOOTH_HOPS < k) sum -= from[low++];
    into[k] = (float)(sum / (high - low));
  }
}

static int louder_first(const void *a, const void *b) {
  float x = ((const Pick *)a)->score, y = ((const Pick *)b)->score;
  return x < y ? 1 : x > y ? -1 : 0;
}

// The highest other peak of the coarse correlation: a local maximum more
// than SIDE_HOPS from the best lag that stands out from the dip between it
// and the best one. Speech's envelope stays alike for a good while, so the
// best peak has broad shoulders, and a bump on them is not another match.
static float rival(const float *r, unsigned last, unsigned best) {
  float found = 0;
  for (int way = -1; way <= 1; way += 2) {
    float dip = r[best];
    for (int lag = (int)best + way; lag >= 0 && lag <= (int)last; lag += way) {
      if (r[lag] < dip) dip = r[lag];
      bool peak = (lag == 0 || r[lag - 1] <= r[lag]) &&
                  (lag == (int)last || r[lag + 1] <= r[lag]);
      if (peak && abs(lag - (int)best) > SIDE_HOPS && r[lag] - dip >= PROMINENCE &&
          r[lag] > found)
        found = r[lag];
    }
  }
  return found;
}

// One analysis of the audio up to `end`. False when the rings moved under
// it, in which case nothing it found is used.
static bool analyse(LatencyMeter *m, uint64_t end, LatencyAnalysis *a) {
  const unsigned W = WINDOW_HOPS, L = LAG_HOPS;
  const uint32_t hop = m->hop;
  *a = (LatencyAnalysis){.lag = -1};
  uint64_t out_from = end - (uint64_t)W * hop;  // the output window
  uint64_t in_from = out_from - (uint64_t)L * hop;  // the input that can explain it
  envelope(m->out, m->mask, out_from, hop, W, m->out_env);
  envelope(m->in, m->mask, in_from, hop, W + L, m->in_env);
  smooth(m->out_env, W, m->out_smooth);
  smooth(m->in_env, W + L, m->in_smooth);

  // Coarse: Pearson's coefficient between the output's envelope and the
  // input's, shifted by each lag. Output hop k lines up with input hop
  // k + L - lag.
  double sy = 0, syy = 0;
  for (unsigned k = 0; k < W; ++k)
    sy += m->out_smooth[k], syy += (double)m->out_smooth[k] * m->out_smooth[k];
  m->sum[0] = m->squares[0] = 0;
  for (unsigned j = 0; j < W + L; ++j) {
    m->sum[j + 1] = m->sum[j] + m->in_smooth[j];
    m->squares[j + 1] = m->squares[j] + (double)m->in_smooth[j] * m->in_smooth[j];
  }
  double vy = syy - sy * sy / W;
  unsigned best = 0;
  for (unsigned lag = 0; lag <= L; ++lag) {
    const float *x = m->in_smooth + (L - lag);
    double sxy = 0;
    for (unsigned k = 0; k < W; ++k) sxy += (double)x[k] * m->out_smooth[k];
    double sx = m->sum[L - lag + W] - m->sum[L - lag];
    double vx = m->squares[L - lag + W] - m->squares[L - lag] - sx * sx / W;
    double r = vx > 0 && vy > 0 ? (sxy - sx * sy / W) / sqrt(vx * vy) : 0;
    m->coarse[lag] = (float)r;
    if (m->coarse[lag] > m->coarse[best]) best = lag;
  }
  a->coarse = m->coarse[best];
  a->side = rival(m->coarse, L, best);

  // Where both sides carry signal at that lag, loudest first.
  unsigned picked = 0;
  for (unsigned k = 0; k < W; ++k) {
    float y = m->out_env[k], x = m->in_env[k + L - best];
    if (y >= FLOOR && x >= FLOOR)
      m->picks[picked++] = (Pick){x < y ? x : y, k};
  }
  a->active = picked;
  a->enough = picked >= MIN_ACTIVE;
  if (!a->enough || a->coarse < COARSE_MIN || best == L) goto checked;
  qsort(m->picks, picked, sizeof(Pick), louder_first);
  if (picked > PICKS) picked = PICKS;

  // Fine: the waveforms over the picked hops, at every lag within reach of
  // the coarse peak, which a parabola through its neighbours centres.
  double centre = best;
  if (best > 0 && best < L) {
    double l = m->coarse[best - 1], c = m->coarse[best], r = m->coarse[best + 1];
    double bend = l - 2 * c + r;
    if (bend < 0) centre += 0.5 * (l - r) / bend;
  }
  int64_t middle = llround(centre * hop);
  int64_t low = middle - m->reach, high = middle + m->reach;
  if (low < 0) low = 0;
  if (high > (int64_t)L * hop) high = (int64_t)L * hop;
  unsigned span = (unsigned)(high - low), lags = span + 1;
  memset(m->xy, 0, sizeof(double) * lags);
  memset(m->xx, 0, sizeof(double) * lags);
  double yy = 0;
  for (unsigned p = 0; p < picked; ++p) {
    // The hop's output, and the input from `high` before it to `low`
    // before its end: output sample i at lag low + d meets input i + span - d.
    uint64_t at = out_from + (uint64_t)m->picks[p].hop * hop;
    for (uint32_t i = 0; i < hop; ++i) {
      m->ys[i] = m->out[(at + i) & m->mask];
      yy += (double)m->ys[i] * m->ys[i];
    }
    m->xs_squares[0] = 0;
    for (unsigned j = 0; j < hop + span; ++j) {
      m->xs[j] = m->in[(at - (uint64_t)high + j) & m->mask];
      m->xs_squares[j + 1] = m->xs_squares[j] + (double)m->xs[j] * m->xs[j];
    }
    for (unsigned d = 0; d < lags; ++d) {
      const float *x = m->xs + (span - d);
      float sum = 0;
      for (uint32_t i = 0; i < hop; ++i) sum += x[i] * m->ys[i];
      m->xy[d] += sum;
      m->xx[d] += m->xs_squares[span - d + hop] - m->xs_squares[span - d];
    }
  }
  unsigned peak = 0;
  float strongest = 0;
  for (unsigned d = 0; d < lags; ++d) {
    double r = m->xx[d] > 0 && yy > 0 ? fabs(m->xy[d]) / sqrt(m->xx[d] * yy) : 0;
    if ((float)r > strongest) strongest = (float)r, peak = d;
  }
  a->fine = strongest;
  a->lag = low + peak;
  // A peak on the edge of the range may belong outside it. Lag zero is the
  // exception: no node answers before it is asked.
  bool edge = (peak == 0 && low > 0) || peak == lags - 1;
  a->confident = a->fine >= FINE_MIN && a->coarse - a->side >= DISTINCT && !edge;

checked:;
  // The oldest frame read was the input at in_from less the reach. If the
  // audio thread could have written over it meanwhile, discard the result.
  uint64_t now = atomic_load_explicit(&m->written, memory_order_acquire);
  return now + CYCLE_MARGIN - (in_from - m->reach) <= m->size;
}

bool latency_meter_update(LatencyMeter *m) {
  uint64_t end = atomic_load_explicit(&m->written, memory_order_acquire);
  uint64_t first = (uint64_t)(WINDOW_HOPS + LAG_HOPS) * m->hop + m->reach;
  if (end < first || end - m->analysed < m->rate) return false;
  m->analysed = end;
  LatencyAnalysis a;
  if (!analyse(m, end, &a)) return false;
  m->last = a;
  m->count++;
  if (!a.confident) {
    // Silence is no evidence either way; a clear mismatch is.
    if (a.enough) m->streak = 0;
    return false;
  }
  if (m->streak > 0 && llabs(a.lag - m->candidate) <= TOLERANCE)
    m->streak++;
  else
    m->streak = 1;
  m->candidate = a.lag;
  if (m->streak < AGREE) return false;
  if (m->published >= 0 && llabs(a.lag - m->published) <= TOLERANCE) return false;
  m->published = a.lag;
  return true;
}

int64_t latency_meter_samples(const LatencyMeter *m) { return m->published; }

const LatencyAnalysis *latency_meter_last(const LatencyMeter *m, unsigned *count) {
  if (count) *count = m->count;
  return &m->last;
}
