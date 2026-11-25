// SPDX-License-Identifier: GPL-3.0-only
// Between the parts of the Windows plugin host: winhost.c, which talks to
// the Linux side, shim.c, which gives the backend the services of host.h,
// and editor.c, which has the editor's window. Nothing here crosses to
// Linux.
#pragma once
#include "../host.h"
#include "wire.h"

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

// --- winhost.c, for editor.c ------------------------------------------------

// Tell the Linux side something it did not ask about: one of the notes of
// wire.h, with a size where the note has one. Main thread.
void winhost_note(uint32_t type, uint32_t width, uint32_t height);

// --- editor.c, for winhost.c ------------------------------------------------

// The editor requests of wire.h, on the main thread. An open fills in the
// reply: the window, its size and the flags, or the reason it was refused.
bool winhost_editor_open(Host *h, const WireRequest *request, WireReply *reply);
bool winhost_editor_show(void);
// Both take a size and hand back the one the editor would take, or took.
void winhost_editor_fit(Host *h, uint32_t *width, uint32_t *height);
void winhost_editor_resize(Host *h, uint32_t *width, uint32_t *height);
// Also when no editor is open, which it then leaves alone.
void winhost_editor_close(Host *h);
// Each tick of the main thread.
void winhost_editor_tick(Host *h);
