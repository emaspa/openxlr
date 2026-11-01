// SPDX-License-Identifier: GPL-3.0-only
// The voice changer at the tail of the microphone stage (eq.c), after noise
// removal and the EQ. See voice.c.
#pragma once
#include "host.h"

#ifdef __cplusplus
extern "C" {
#endif

// The voices, as the voice_preset control numbers them. The daemon names
// them; only the numbers cross the command pipe, so they never move.
enum {
  VOICE_CUSTOM = 0,
  VOICE_DEEP = 1,
  VOICE_HIGH = 2,
  VOICE_ROBOT = 3,
  VOICE_RADIO = 4,
  VOICE_ALIEN = 5,
  VOICE_DRACULA = 6,
  VOICE_FRANKENSTEIN = 7,
  VOICE_MUMMY = 8,
  VOICE_WEREWOLF = 9,
  VOICE_WITCH = 10,
  VOICE_COUNT = 11,
  VOICE_OFF = -1,  // the voice_active meter while the dry voice plays
};

// Why a voice that was asked for does not run, as the voice_status meter
// reports it.
enum {
  VOICE_OK = 0,
  VOICE_NOT_STARTED = 1,  // the pitch shifter could not start: the rate, or memory
  VOICE_FELL_BEHIND = 2,  // a voice gave out something that is not a number twice within a second
};

// The voice's name as the daemon spells it, for tests and messages.
const char *voice_name(int preset);

#ifndef __cplusplus
typedef struct Voice Voice;

// Add the controls and meters to a mono stage and allocate everything the
// audio thread will use. Main thread, at load. NULL only when the controls
// cannot be added; a pitch shifter that cannot start leaves the stage dry
// and says so on voice_status.
Voice *voice_load(Host *h);
// Start as the controls ask, with no fade. Main thread, before audio runs.
bool voice_activate(Voice *v);
// One block from in to out; the two may not overlap. Audio thread.
void voice_process(Voice *v, const float *in, float *out, uint32_t frames);
// Report the status. Main thread, every tick.
void voice_main_thread(Voice *v);
// The delay the running voice adds, in samples: 0 while the dry voice plays.
// Any thread.
uint32_t voice_latency(const Voice *v);
// How many times a voice started over after answering with something that
// is not a number. Any thread.
uint32_t voice_resets(const Voice *v);
// Free everything; audio has stopped. Main thread.
void voice_unload(Voice *v);
#endif

// The pitch and formant shifter, Signalsmith Stretch (signalsmith/, MIT),
// behind a C interface (voice-stretch.cpp). Created and freed on the main
// thread; everything else runs on the audio thread and allocates nothing.
typedef struct VoiceShifter VoiceShifter;
// A shifter for mono audio that analyses `block` samples every `interval`.
VoiceShifter *voice_shifter_create(int block, int interval);
void voice_shifter_free(VoiceShifter *s);
// Clear what it has heard, for a new voice. Stretch keeps some figures from
// block to block that this leaves alone, so a shifter that put out something
// that is not a number is replaced instead (voice.c).
void voice_shifter_reset(VoiceShifter *s);
// Pitch and formant in semitones; the formant shift is relative to the
// input's own formants, so 0 keeps them where they were whatever the pitch.
void voice_shifter_set(VoiceShifter *s, float pitch, float formant);
void voice_shifter_process(VoiceShifter *s, const float *in, float *out, int frames);
// What the shifter adds, in samples: its block.
int voice_shifter_latency(const VoiceShifter *s);

#ifdef __cplusplus
}
#endif
