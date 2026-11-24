// SPDX-License-Identifier: GPL-3.0-only
// Between the two halves of the Windows plugin host: winhost.c, which talks
// to the Linux side, and shim.c, which gives the backend the services of
// host.h. Nothing here crosses to Linux.
#pragma once
#include "../host.h"

// --- shim.c, for winhost.c --------------------------------------------------

// Once, on the main thread, before any backend is called.
bool shim_start(void);
// The event that brings the main thread's wait round when something was
// signalled for it, as a Windows handle.
void *shim_wake(void);
// Once, on the audio thread, before its first block.
void shim_enter_audio_thread(void);
// The events signalled since the last call, on the main thread.
void shim_run_events(void);
// The meters that changed since the last call, on the main thread.
void shim_publish_meters(Host *h);
// Around one block on the audio thread: whether the backend refused it.
void shim_begin_block(void);
bool shim_block_refused(void);

// --- winhost.c, for shim.c --------------------------------------------------

// A control's value changed on this side: the plugin moved it, or it is a
// meter with a new reading. Any thread; no system call.
void winhost_publish(uint32_t index, float value);
