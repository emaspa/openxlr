// SPDX-License-Identifier: GPL-3.0-only
// A stereo delay that reports its delay on an LV2 latency port. The delay
// control sets it, so a test can change the latency while audio runs.
#include <lv2/core/lv2.h>
#include <math.h>
#include <stdint.h>
#include <stdlib.h>

enum { RING = 4096, PORTS = 6 };

typedef struct {
  float *ports[PORTS];
  float ring[2][RING];
  uint32_t position;
} Delay;

static LV2_Handle instantiate(const LV2_Descriptor *descriptor, double rate,
                              const char *bundle,
                              const LV2_Feature *const *features) {
  return calloc(1, sizeof(Delay));
}

static void connect(LV2_Handle handle, uint32_t port, void *data) {
  if (port < PORTS)
    ((Delay *)handle)->ports[port] = data;
}

static void run(LV2_Handle handle, uint32_t frames) {
  Delay *d = handle;
  float requested = *d->ports[4];
  uint32_t delay = isfinite(requested) && requested >= 0 && requested <= 2048
                       ? (uint32_t)requested
                       : 0;
  *d->ports[5] = (float)delay;
  for (uint32_t f = 0; f < frames; f++) {
    for (uint32_t c = 0; c < 2; c++) {
      d->ring[c][d->position] = d->ports[c][f];
      d->ports[2 + c][f] = d->ring[c][(d->position + RING - delay) % RING];
    }
    d->position = (d->position + 1) % RING;
  }
}

static const LV2_Descriptor descriptor = {
    "urn:openxlr:test:latency", instantiate, connect, NULL, run, NULL, free,
    NULL};

LV2_SYMBOL_EXPORT const LV2_Descriptor *lv2_descriptor(uint32_t index) {
  return index == 0 ? &descriptor : NULL;
}
