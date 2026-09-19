// SPDX-License-Identifier: GPL-3.0-only
#define main host_program_main
#include "../host.c"
#undef main
#include "../sound-check.c"
#include <assert.h>

int main(void) {
  Host h = {.rate = 8000, .channels = 1};
  assert(sound_check_load(&h, NULL));
  SoundCheck *s = h.impl;
  float input[512], output[512];
  float *in[] = {input}, *out[] = {output};
  for (unsigned i = 0; i < 512; i++) input[i] = (float)i / 512;
  sound_check_process(&h, 512, in, out);
  assert(memcmp(input, output, sizeof(input)) == 0 && s->length == 0);
  atomic_store(&s->command->desired, RECORD);
  sound_check_process(&h, 128, in, out);
  assert(s->length == 128 && memcmp(input, output, 128 * sizeof(float)) == 0);
  atomic_store(&s->command->desired, LOOP);
  for (unsigned i = 0; i < 512; i++) input[i] = -1;
  sound_check_process(&h, 512, in, out);
  for (unsigned i = 0; i < 512; i++) assert(output[i] == (float)(i % 128) / 512);
  assert(s->length == 128);
  atomic_store(&s->command->desired, LIVE);
  sound_check_process(&h, 512, in, out);
  assert(memcmp(input, output, sizeof(input)) == 0 && s->length == 128);
  atomic_store(&s->command->desired, RECORD);
  for (unsigned i = 0; i < 200; i++) sound_check_process(&h, 512, in, out);
  assert(s->length == 80000 && atomic_load(&s->command->desired) == LIVE);
  assert(atomic_load(&s->mode->observed) == LIVE);
  atomic_store(&s->command->desired, RECORD);
  sound_check_process(&h, 10, in, out);
  assert(s->length == 10);
  atomic_store(&s->command->desired, NAN);
  sound_check_process(&h, 512, in, out);
  assert(memcmp(input, output, sizeof(input)) == 0);
  atomic_store(&s->command->desired, RECORD);
  sound_check_tick(&h);
  assert(s->timed_record);
  s->record_started.tv_sec -= 11;
  sound_check_tick(&h);
  assert(atomic_load(&s->command->desired) == LIVE);
  sound_check_unload(&h);
  assert(h.impl == NULL);
  puts("PASS: dry passthrough, replay, live restore, record replacement and ten-second bound");
  return 0;
}
