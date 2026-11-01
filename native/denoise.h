// SPDX-License-Identifier: GPL-3.0-only
// Noise removal at the head of the microphone stage (eq.c): RNNoise, built
// in from rnnoise/, or DeepFilterNet, built in from deepfilter/ when the
// helper was built with it. See denoise.c.
#pragma once
#include "host.h"

typedef struct Denoise Denoise;

// The engines, as the denoise_engine control numbers them; NONE is the
// dry signal.
enum { DENOISE_NONE = -1, DENOISE_RNNOISE = 0, DENOISE_DEEPFILTER = 1 };

// Why noise removal that was asked for does not run, as the denoise_status
// meter reports it.
enum {
  DENOISE_OK = 0,
  DENOISE_RATE_UNSUPPORTED = 1,  // the graph does not run at 48 kHz
  DENOISE_NOT_BUILT = 2,         // this helper was built without the engine
  DENOISE_LOAD_FAILED = 3,       // the engine could not start
  DENOISE_FELL_BEHIND = 4,       // the engine failed or could not keep up
};

// Both engines run at this rate only.
#define DENOISE_SAMPLE_RATE 48000u

// Add the controls and meters to a mono stage. Main thread, at load.
Denoise *denoise_load(Host *h);
// Start the engine the controls ask for, with no fade. Main thread, before
// audio runs.
bool denoise_activate(Denoise *d);
// One block from in to out; the two may not overlap. Audio thread.
void denoise_process(Denoise *d, const float *in, float *out, uint32_t frames);
// Start and stop DeepFilterNet as asked and report the status. Main thread,
// every tick.
void denoise_main_thread(Denoise *d);
// The delay the running engine adds, in samples. Any thread.
uint32_t denoise_latency(const Denoise *d);
// How many times an engine's state was cleared after it answered with
// something that is not a number. Any thread.
uint32_t denoise_resets(const Denoise *d);
// Free everything; audio has stopped. Main thread.
void denoise_unload(Denoise *d);

// Whether this build carries an engine.
bool denoise_engine_built(int engine);
// `openxlr-lv2-host engines`: the built engines as one line of JSON.
int denoise_print_engines(void);
// For tests that feed blocks faster than real time: wait until
// DeepFilterNet has taken every whole hop it was given. Main thread.
void denoise_wait_idle(Denoise *d);
