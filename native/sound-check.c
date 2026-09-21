// SPDX-License-Identifier: GPL-3.0-only
// A bounded dry-microphone loop upstream of the live insert chain. The audio
// thread owns all sample data; controls and progress cross threads atomically.
#include "host.h"
#include <stdlib.h>
#include <string.h>
#include <time.h>

enum { SOUND_CHECK_SECONDS = 10, LIVE = 0, RECORD = 1, LOOP = 2 };
typedef struct {
  float *samples;
  uint32_t capacity, length, cursor;
  int previous;
  bool timed_record;
  struct timespec record_started;
  Control *command, *frames, *mode;
} SoundCheck;

static bool sound_check_load(Host *h, char **arguments) {
  if (h->channels != 1) return false;
  SoundCheck *s = calloc(1, sizeof(*s));
  if (!s) return false;
  h->impl = s;
  s->capacity = h->rate * SOUND_CHECK_SECONDS;
  s->samples = calloc(s->capacity, sizeof(float));
  s->command = host_add_control(h, "command", LIVE, LOOP, LIVE, false, NULL);
  s->frames = host_add_control(h, "frames", 0, (float)s->capacity, 0, true, NULL);
  s->mode = host_add_control(h, "mode", LIVE, LOOP, LIVE, true, NULL);
  return s->samples && s->command && s->frames && s->mode;
}
static bool sound_check_activate(Host *h) { return true; }
static void sound_check_deactivate(Host *h) {}
static void sound_check_unload(Host *h) {
  SoundCheck *s = h->impl;
  if (!s) return;
  free(s->samples);
  free(s);
  h->impl = NULL;
}
static void sound_check_process(Host *h, uint32_t frames, float *const *in, float *const *out) {
  SoundCheck *s = h->impl;
  float requested = atomic_load(&s->command->desired);
  int mode = requested == RECORD ? RECORD : requested == LOOP ? LOOP : LIVE;
  if (mode != s->previous) {
    if (mode == RECORD) s->length = 0;
    if (mode == LOOP) s->cursor = 0;
    s->previous = mode;
  }
  if (mode == LOOP && s->length == 0) mode = LIVE;
  for (uint32_t i = 0; i < frames; i++) {
    if (mode == RECORD) {
      s->samples[s->length++] = in[0][i];
      if (s->length == s->capacity) {
        mode = LIVE;
        s->previous = LIVE;
        host_control_moved_rt(h, s->command, LIVE);
      }
    }
    if (mode == LOOP) {
      out[0][i] = s->samples[s->cursor++];
      if (s->cursor == s->length) s->cursor = 0;
    } else out[0][i] = in[0][i];
  }
  atomic_store(&s->frames->observed, (float)s->length);
  atomic_store(&s->mode->observed, (float)mode);
}
static void sound_check_tick(Host *h) {
  SoundCheck *s = h->impl;
  float requested = atomic_load(&s->command->desired);
  if (requested == RECORD) {
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    if (!s->timed_record) { s->record_started = now; s->timed_record = true; }
    double elapsed = (double)(now.tv_sec - s->record_started.tv_sec)
        + (now.tv_nsec - s->record_started.tv_nsec) / 1e9;
    // A suspended input must not start recording minutes after the click.
    if (elapsed >= SOUND_CHECK_SECONDS) {
      host_control_moved(h, s->command, LIVE);
      requested = LIVE;
    }
  } else s->timed_record = false;
  atomic_store(&s->mode->observed, requested);
}

const Backend sound_check_backend = {
  .name = "Sound Check", .argument_count = 0,
  .load = sound_check_load, .activate = sound_check_activate,
  .deactivate = sound_check_deactivate, .process = sound_check_process,
  .main_thread = sound_check_tick, .unload = sound_check_unload,
};
