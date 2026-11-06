// SPDX-License-Identifier: GPL-3.0-only
// The clock bridge: a capture device on a clock of its own carried into the
// mixer's graph. One node follows the device, one follows the mixer, and the
// two meet only in this ring. The device side writes what it captured; the
// mixer side reads it back through a resampler whose ratio a delay-locked
// loop keeps at whatever holds the ring at its target fill, so the two
// clocks may drift apart forever without the ring running dry or over.
//
// The core below has no audio server in it: bridge_produce and
// bridge_consume are what the two nodes call from their own cycles, with
// the frames, the rate and the cycle times PipeWire hands them. They run on
// two threads, one writer and one reader, and share only the ring's counts
// and the writer's last cycle, all through atomics.
#pragma once
#define _DEFAULT_SOURCE  // M_PI beside POSIX
#include <spa/utils/dll.h>
#include <stdatomic.h>
#include <stdbool.h>
#include <stdint.h>

enum {
  BRIDGE_MAX_CHANNELS = 2,
  // Frames per channel in the ring, a power of two far above any target.
  BRIDGE_RING = 1 << 16,
  // The resampler's half length in frames: it reads this many frames on
  // either side of each output frame, which is also the delay it adds.
  BRIDGE_HALF = 32,
  BRIDGE_TAPS = 2 * BRIDGE_HALF,
  // Kernel phases between two input frames; the kernel is interpolated
  // between neighbouring phases.
  BRIDGE_PHASES = 256,
  // The largest cycle either side may run, PipeWire's own limit.
  BRIDGE_MAX_QUANTUM = 8192,
};

// Defaults the helper takes when its arguments do not say otherwise.
#define BRIDGE_DEFAULT_QUANTA 2.0
#define BRIDGE_DEFAULT_MARGIN_MS 10.0
// How far the loop may move the ratio from the nominal one: 0.5 %, well
// past any two crystals and short of an audible jump if it ever got there.
#define BRIDGE_MAX_CORRECTION 0.005

typedef enum {
  BRIDGE_WAITING = 0,  // nothing from the device yet, or re-priming
  BRIDGE_RUNNING = 1,
} BridgeState;

typedef struct {
  unsigned channels;
  // The target fill: the device's cycle, this many of the mixer's cycles,
  // the resampler's half length and the margin.
  double quanta;
  double margin_ms;

  // The ring, channel after channel, and the two running totals of frames
  // written and read. Only the writer moves `written`, only the reader `read`.
  float *ring;
  _Atomic uint64_t written;
  _Atomic uint64_t read;

  // The writer's last cycle, published under a sequence count: the total
  // written after it, its time and the time of the cycle after it in
  // nanoseconds on the monotonic clock, its frames and its rate.
  _Atomic uint32_t seq;
  _Atomic uint64_t p_written;
  _Atomic uint64_t p_nsec;
  _Atomic uint64_t p_next_nsec;
  _Atomic uint32_t p_quantum;
  _Atomic uint32_t p_rate;
  bool p_full;  // the writer's own: the ring was full on its last cycle

  // The reader's own state.
  struct spa_dll dll;
  double corr;            // the loop's correction, 1 for none
  unsigned dll_period;    // the cycle the loop's bandwidth was set for
  uint32_t dll_rate;
  double bw;              // the bandwidth now; it narrows after a start
  uint64_t locked_frames; // output frames since the last start
  bool priming;
  bool started;           // primed at least once
  float *hist[BRIDGE_MAX_CHANNELS];  // the resampler's input
  unsigned have;          // frames in hist
  double pos;             // the next output frame's position in hist
  double fill_avg;        // the measured fill, smoothed, input frames
  uint32_t target;        // frames, at the device's rate

  // What the main thread reports, written by the reader.
  _Atomic uint64_t underruns;
  _Atomic uint64_t overruns;
  _Atomic int state;
  _Atomic double delay;   // the smoothed fill in the mixer's frames
  _Atomic double ppm;     // the ratio's distance from the nominal one
  _Atomic uint32_t target_out;  // the target in the mixer's frames
  _Atomic uint32_t in_rate, in_quantum, out_rate, out_quantum;
} Bridge;

// The kernel, built once; bridge_init builds it if nobody has.
void bridge_kernel_init(void);

bool bridge_init(Bridge *b, unsigned channels, double quanta, double margin_ms);
void bridge_free(Bridge *b);

// The device's cycle: `frames` frames per channel captured at `rate`, the
// cycle's time and the next cycle's time (0 when unknown). A null channel is
// silence. Real-time safe.
void bridge_produce(Bridge *b, const float *const *in, uint32_t frames,
                    uint32_t rate, uint64_t nsec, uint64_t next_nsec);

// The mixer's cycle: fill `frames` frames per channel at `rate`, the cycle
// started at `nsec` (0 when unknown). Real-time safe.
void bridge_consume(Bridge *b, float *const *out, uint32_t frames,
                    uint32_t rate, uint64_t nsec);

// The fill the reader steers to for these cycles, in device frames.
uint32_t bridge_target(const Bridge *b, uint32_t in_quantum, uint32_t in_rate,
                       uint32_t out_quantum, uint32_t out_rate);

// `openxlr-lv2-host bridge NODE CHANNELS RATE [quanta=N] [margin=MS]`.
int bridge_main(int argc, char **argv);
