// SPDX-License-Identifier: GPL-3.0-only
// What the Linux plugin host (wine.c) and the Windows plugin host
// (winhost.c) say to each other. The Linux side owns a plugin's PipeWire
// node; the Windows side runs under Wine and owns the plugin. Between them:
//
// - One shared file, mapped by both, with the audio of one cycle and the
//   plugin's controls.
// - The audio wake: the Windows host's standard input and output, a pipe
//   each way, one byte per cycle each way. Under Wine a blocking read on an
//   inherited pipe is a plain read in the calling thread, so a cycle costs
//   no call to wineserver.
// - The control channel: two FIFOs, a fixed-size request one way and a
//   fixed-size reply the other, between the two main threads. Never used on
//   an audio thread.
//
// Controls cross in the shared file, so a value costs no system call:
//
// - At load the Windows side lists what the plugin has in `controls`, and
//   the reply to the load says how many. The Linux side registers the same
//   controls with its own host, so the daemon sees them as a native
//   plugin's.
// - A value the Linux side sets travels with a cycle, in `sets`: the Linux
//   audio thread writes them before the wake and the Windows audio thread
//   applies them before the block.
// - A value the plugin moves, and every reading of a meter, is published in
//   `values`, one word per control that either Windows thread may write at
//   any time. The Linux main thread reads them on its tick.
// - Each published value has a count. A set names the count the Linux side
//   had seen when it was made, and the Windows side drops a set whose count
//   is behind: the plugin has moved that control since, and its newer value
//   is already on its way.
//
// A scan uses none of this. The Windows host started with the one argument
// `scan` reads one WireScan from its standard input, writes the module's
// description to its standard output and leaves. The Linux side holds that
// input pipe open while it waits, and the Windows host ends itself when the
// pipe closes, so giving up on a scan is closing it.
//
// Every structure has a fixed size and both builds are x86-64, so the two
// compilers lay them out alike; the assertions below hold them to it.
// Neither side trusts a count or an index it reads from the other: each one
// bounds what it uses.
#pragma once
#include <stdatomic.h>
#include <stdint.h>

enum {
  WIRE_MAGIC = 0x4857584f,  // "OXWH"
  WIRE_VERSION = 2,
  WIRE_MAX_FRAMES = 8192,    // MAX_FRAMES in host.h
  WIRE_MAX_CHANNELS = 2,     // MAX_CHANNELS in host.h
  WIRE_MAX_CONTROLS = 4096,  // MAX_CONTROLS in host.h
  // The values one cycle can hand over. More wait for the next cycle.
  WIRE_MAX_SETS = 256,
  // A control's name with its ending zero. The formats carried name a
  // control by its number in decimal, ten digits at most.
  WIRE_SYMBOL = 32,
  WIRE_PATH = 4096,
  WIRE_ID = 256,
  WIRE_TEXT = 256,
  // What Linux keeps of a thread's name, the ending zero included.
  WIRE_THREAD_NAME = 16
};

// The files in the private directory the Windows host is started with.
#define WIRE_AUDIO_FILE "audio"
#define WIRE_REQUEST_FIFO "request"
#define WIRE_REPLY_FIFO "reply"

// The audio wake, Linux to Windows: process the block in the shared file.
// The Windows host answers with WIRE_WAKE_DONE.
enum { WIRE_WAKE_PROCESS = 'P', WIRE_WAKE_DONE = 'D' };

// How one block went. Anything but WIRE_BLOCK_OK plays as silence.
enum { WIRE_BLOCK_OK = 1, WIRE_BLOCK_REFUSED = 2 };

// One control as the Windows side lists it at load.
enum { WIRE_CONTROL_OUTPUT = 1 };  // a meter: the plugin writes it
typedef struct {
  char symbol[WIRE_SYMBOL];
  float minimum, maximum, initial;
  uint32_t flags;
} WireControl;

// One value from the Linux side, for the control at `index`. `based_on` is
// the count of that control's published value as the Linux side last saw it.
typedef struct {
  uint32_t index;
  uint32_t based_on;
  float value;
  uint32_t reserved;
} WireSet;

// A published value: the count in the high half, the float's bits in the
// low half, so one atomic word carries both.
static inline uint64_t wire_value(uint32_t count, float value) {
  union {
    float f;
    uint32_t u;
  } bits = {.f = value};
  return (uint64_t)count << 32 | bits.u;
}
static inline uint32_t wire_value_count(uint64_t word) {
  return (uint32_t)(word >> 32);
}
static inline float wire_value_float(uint64_t word) {
  union {
    uint32_t u;
    float f;
  } bits = {.u = (uint32_t)word};
  return bits.f;
}

typedef struct {
  // Written by the Linux side before the Windows host starts.
  uint32_t magic, version, size;
  // One cycle: the Linux side writes `frames`, the input, `set_count` and
  // the sets, and wakes the Windows audio thread, which writes the output
  // and `status` and answers.
  uint32_t frames;
  uint32_t status;
  // The plugin's latency in frames, written by the Windows main thread each
  // time the plugin is activated and when the plugin changes it, read by
  // the Linux main thread.
  _Atomic uint32_t latency;
  uint32_t set_count;
  // Counts every change to `values`, so the Linux side looks through them
  // only after one.
  _Atomic uint32_t published;
  uint32_t reserved[8];
  float in[WIRE_MAX_CHANNELS][WIRE_MAX_FRAMES];
  float out[WIRE_MAX_CHANNELS][WIRE_MAX_FRAMES];
  WireSet sets[WIRE_MAX_SETS];
  _Atomic uint64_t values[WIRE_MAX_CONTROLS];
  WireControl controls[WIRE_MAX_CONTROLS];
} WireShared;

enum { WIRE_HELLO = 1, WIRE_LOAD, WIRE_ACTIVATE, WIRE_DEACTIVATE, WIRE_QUIT };
// The plugin formats a load can name. A Windows host that does not know one
// refuses the load and says so.
enum { WIRE_FORMAT_CLAP = 1, WIRE_FORMAT_VST3 };

typedef struct {
  uint32_t magic, type;
  uint32_t version;    // hello
  uint32_t format;     // load
  uint32_t channels;   // load: the width of the chain the plugin sits in
  uint32_t rate;       // load
  uint32_t own_block;  // load: the plugin is called with OWN_BLOCK_FRAMES at a time
  uint32_t reserved;
  char thread[WIRE_THREAD_NAME];  // hello: the name the audio thread takes
  // load: which plugin of the module. A CLAP id, or a VST3 class id as the
  // 32 hex digits a Linux host knows the plugin by.
  char id[WIRE_ID];
  // load: the module, as a Unix path. For VST3 it is the module inside the
  // bundle, which the Linux side has found.
  char path[WIRE_PATH];
} WireRequest;

typedef struct {
  uint32_t magic, type;  // the type of the request this answers
  uint32_t ok;
  uint32_t version;      // hello
  uint32_t shared_size;  // hello: sizeof(WireShared) as this side built it
  uint32_t latency;      // activate
  uint32_t controls;     // load: how many entries of `controls` are filled
  uint32_t reserved;
  // The plugin's name after a load, the reason after a refusal.
  char text[WIRE_TEXT];
} WireReply;

// What a scan is asked to describe. Both paths are the bytes of a Unix path
// and cross in this structure, never on the command line, which Wine would
// convert through the locale's character set.
typedef struct {
  uint32_t magic, version;
  uint32_t format;  // WIRE_FORMAT_CLAP or WIRE_FORMAT_VST3
  uint32_t reserved;
  // The module to open. For VST3 it is the module inside the bundle, which
  // the Linux side has found.
  char module[WIRE_PATH];
  // The path the description names the plugin by: the one the helper was
  // given, as it was given.
  char name[WIRE_PATH];
} WireScan;

_Static_assert(sizeof(WireControl) == WIRE_SYMBOL + 16, "a control's layout");
_Static_assert(sizeof(WireSet) == 16, "a set's layout");
_Static_assert(sizeof(_Atomic uint64_t) == 8 && sizeof(_Atomic uint32_t) == 4,
               "atomic words are plain words");
_Static_assert(sizeof(WireShared) ==
                   64 + 2 * WIRE_MAX_CHANNELS * WIRE_MAX_FRAMES * sizeof(float) +
                       WIRE_MAX_SETS * sizeof(WireSet) +
                       WIRE_MAX_CONTROLS * (8 + sizeof(WireControl)),
               "the shared file's layout");
_Static_assert(sizeof(WireRequest) ==
                   32 + WIRE_THREAD_NAME + WIRE_ID + WIRE_PATH,
               "a request's layout");
_Static_assert(sizeof(WireReply) == 32 + WIRE_TEXT, "a reply's layout");
_Static_assert(sizeof(WireScan) == 16 + 2 * WIRE_PATH, "a scan's layout");
