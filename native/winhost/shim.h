// SPDX-License-Identifier: GPL-3.0-only
// What host.h includes in place of PipeWire, X and the dynamic loader when a
// backend is built for the Windows plugin host. The backends are the same
// source on both systems; this gives them, under the names they already
// use, the few things of the Linux host's that they touch: the main loop's
// timers and events, the types of the window a frame holds, and dlopen.
// shim.c implements them on Windows, beside the host services of host.h.
//
// No Windows header is included here, so a backend sees none of their
// macros.
#pragma once
#include <setjmp.h>
#include <stdint.h>
#include <time.h>
#ifdef __cplusplus
extern "C" {
#else
#include <stdbool.h>
#endif

// host.h declares its escape from a lost X connection with this type. There
// is no such escape here and nothing uses the declaration.
typedef jmp_buf sigjmp_buf;

// The window an editor is shown in. Windows names a window by a handle
// whose significant bits fit this type.
typedef struct ShimDisplay Display;
typedef unsigned long Window;
typedef unsigned long Atom;

// --- the main loop ----------------------------------------------------------

// The Linux host's loop is PipeWire's. Here it is the Windows main thread's
// message loop: a timer is a Windows timer, an event is a flag that loop
// looks at, and both call back on the main thread. Sources are added and
// destroyed on the main thread; an event may be signalled from any thread.
struct pw_main_loop;
struct pw_filter;
struct pw_loop;
struct spa_source;
struct spa_thread_utils;
struct spa_thread;

enum { SPA_IO_IN = 1 << 0, SPA_IO_OUT = 1 << 2, SPA_IO_ERR = 1 << 3, SPA_IO_HUP = 1 << 4 };

typedef void (*spa_source_io_func_t)(void *data, int fd, uint32_t mask);
typedef void (*spa_source_timer_func_t)(void *data, uint64_t expirations);
typedef void (*spa_source_event_func_t)(void *data, uint64_t count);

// NULL when no source is free.
struct spa_source *pw_loop_add_timer(struct pw_loop *loop,
                                     spa_source_timer_func_t func, void *data);
// `value` is the time to the first call and `interval` the time between the
// later ones, both relative. No interval is a single call; no value stops
// the timer. Windows timers are no finer than about 10 ms.
int pw_loop_update_timer(struct pw_loop *loop, struct spa_source *source,
                         struct timespec *value, struct timespec *interval,
                         bool absolute);
struct spa_source *pw_loop_add_event(struct pw_loop *loop,
                                     spa_source_event_func_t func, void *data);
// From the audio thread this only raises the flag, which the main thread
// sees on its next tick: waking it at once would be a call to wineserver
// inside a block.
int pw_loop_signal_event(struct pw_loop *loop, struct spa_source *source);
// Windows has no file descriptors to watch, so this always answers NULL.
struct spa_source *pw_loop_add_io(struct pw_loop *loop, int fd, uint32_t mask,
                                  bool close, spa_source_io_func_t func,
                                  void *data);
int pw_loop_update_io(struct pw_loop *loop, struct spa_source *source,
                      uint32_t mask);
void pw_loop_destroy_source(struct pw_loop *loop, struct spa_source *source);

// --- the dynamic loader -----------------------------------------------------

enum { RTLD_LOCAL = 0, RTLD_NOW = 2 };

// `file` is a Windows path in UTF-8.
void *dlopen(const char *file, int mode);
void *dlsym(void *library, const char *name);
// The module stays loaded: the process ends soon after, and unloading a
// plugin's module is one more thing that can go wrong on the way out.
int dlclose(void *library);
const char *dlerror(void);

#ifdef __cplusplus
}
#endif
