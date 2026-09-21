// SPDX-License-Identifier: GPL-3.0-only
// Stereo gain fixture, used to measure channel insert routing.
#include <lv2/core/lv2.h>
#include <stdlib.h>
typedef struct { float *ports[5]; } Gain;
static LV2_Handle instantiate(const LV2_Descriptor *d, double rate,
    const char *bundle, const LV2_Feature *const *features) { return calloc(1, sizeof(Gain)); }
static void connect(LV2_Handle h, uint32_t port, void *data) { if (port < 5) ((Gain *)h)->ports[port] = data; }
static void run(LV2_Handle h, uint32_t frames) {
  Gain *g = h;
  for (uint32_t i = 0; i < frames; i++)
    for (uint32_t c = 0; c < 2; c++) g->ports[c + 2][i] = g->ports[c][i] * *g->ports[4];
}
static const LV2_Descriptor descriptor = {
  "urn:openxlr:test:gain", instantiate, connect, NULL, run, NULL, free, NULL
};
LV2_SYMBOL_EXPORT const LV2_Descriptor *lv2_descriptor(uint32_t index) { return index == 0 ? &descriptor : NULL; }
