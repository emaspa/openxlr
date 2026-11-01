// SPDX-License-Identifier: GPL-3.0-only
// The compressor and the expander of the microphone stage (eq.c). The
// expander runs after noise removal and before the EQ, the compressor after
// the EQ and before the voice changer. See dynamics.c.
#pragma once
#include "host.h"

// The two processors, as dynamics_load takes them.
enum { DYNAMICS_EXPANDER = 0, DYNAMICS_COMPRESSOR = 1 };

// What the meters hold while nothing is measured: the level floor in dBFS.
#define DYNAMICS_FLOOR_DB (-120.0)

typedef struct Dynamics Dynamics;

// Add one processor's controls and meters to a mono stage: comp_* for the
// compressor, exp_* for the expander. Main thread, at load. NULL when the
// controls cannot be added or memory runs out.
Dynamics *dynamics_load(Host *h, int kind);
// Start as the controls ask, with no fade and no reduction. Main thread,
// before audio runs.
bool dynamics_activate(Dynamics *d);
// One block from in to out; in may be out. Audio thread.
void dynamics_process(Dynamics *d, const float *in, float *out, uint32_t frames);
// Publish the meters while they are asked for. Main thread, every tick.
void dynamics_main_thread(Dynamics *d);
// Free everything; audio has stopped. Main thread. NULL is fine.
void dynamics_unload(Dynamics *d);

// The static curve: the gain reduction in dB, 0 or more, for a steady level
// in dBFS. `shape` is the compressor's knee width or the expander's range,
// both in dB.
double dynamics_reduction_db(int kind, double level_db, double threshold, double ratio, double shape);
// The smoothed reduction the processor applies now, in dB. For tests.
double dynamics_reduction_now(const Dynamics *d);
