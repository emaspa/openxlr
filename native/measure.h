// SPDX-License-Identifier: GPL-3.0-only
// The delay a plugin's node really adds, measured from its audio rather than
// taken from what the plugin declares. The audio thread copies a mono mix of
// what enters the node and of what leaves it into two rings; the main thread
// lines the two up once a second and reports a figure only once it is sure
// of it. See measure.c for the method and its thresholds.
#pragma once
#include <stdbool.h>
#include <stdint.h>

typedef struct LatencyMeter LatencyMeter;

// What one analysis found, for tests and diagnostics. `lag` is in samples.
typedef struct {
  bool enough;     // enough signal above the level floor on both sides
  bool confident;  // every check passed
  int64_t lag;     // the refined lag, -1 when there was none
  float coarse;    // the envelopes' normalised correlation at its peak
  float fine;      // the waveforms' normalised correlation at its peak
  float side;      // the envelopes' best correlation away from the peak
  unsigned active; // hops with signal on both sides at the coarse peak
} LatencyAnalysis;

// The helper links measure.o; tests that compile host.c into themselves
// without it see these as weak references, NULL, and measure nothing.
#ifndef LATENCY_METER_LINKAGE
#define LATENCY_METER_LINKAGE
#endif

// Main thread. NULL when the rings cannot be allocated.
LatencyMeter *latency_meter_new(uint32_t rate) LATENCY_METER_LINKAGE;
void latency_meter_free(LatencyMeter *m) LATENCY_METER_LINKAGE;
// Audio thread, once per cycle each: the input as it enters, before the
// plugin can touch it, and the output as it leaves. They copy and nothing
// else: no allocation, no lock, no system call.
void latency_meter_input(LatencyMeter *m, float *const *in, unsigned channels,
                         uint32_t frames) LATENCY_METER_LINKAGE;
void latency_meter_output(LatencyMeter *m, float *const *out, unsigned channels,
                          uint32_t frames) LATENCY_METER_LINKAGE;
// Main thread, every tick: analyses when a second of new audio has arrived,
// and returns true when the reported figure changed.
bool latency_meter_update(LatencyMeter *m) LATENCY_METER_LINKAGE;
// The figure in samples, or -1 until a confident one has been found.
int64_t latency_meter_samples(const LatencyMeter *m) LATENCY_METER_LINKAGE;
// The last analysis, and how many have run.
const LatencyAnalysis *latency_meter_last(const LatencyMeter *m,
                                          unsigned *count) LATENCY_METER_LINKAGE;
