// SPDX-License-Identifier: GPL-3.0-only
// What the Linux plugin host (wine.c) and the Windows plugin host
// (winhost.c) say to each other. The Linux side owns a plugin's PipeWire
// node; the Windows side runs under Wine and owns the plugin. Between them:
//
// - One shared file, mapped by both, with the audio of one cycle.
// - The audio wake: the Windows host's standard input and output, a pipe
//   each way, one byte per cycle each way. Under Wine a blocking read on an
//   inherited pipe is a plain read in the calling thread, so a cycle costs
//   no call to wineserver.
// - The control channel: two FIFOs, a fixed-size request one way and a
//   fixed-size reply the other, between the two main threads. Never used on
//   an audio thread.
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
  WIRE_VERSION = 1,
  WIRE_MAX_FRAMES = 8192,  // MAX_FRAMES in host.h
  WIRE_MAX_CHANNELS = 2,   // MAX_CHANNELS in host.h
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

// The audio wake, Linux to Windows: process the block in the shared file,
// or stop processing because the plugin is about to be deactivated. The
// Windows host answers either with WIRE_WAKE_DONE.
enum { WIRE_WAKE_PROCESS = 'P', WIRE_WAKE_STOP = 'S', WIRE_WAKE_DONE = 'D' };

// How one block went. Anything but WIRE_BLOCK_OK plays as silence.
enum { WIRE_BLOCK_OK = 1, WIRE_BLOCK_REFUSED = 2 };

typedef struct {
  // Written by the Linux side before the Windows host starts.
  uint32_t magic, version, size;
  // One cycle: the Linux side writes `frames` and the input and wakes the
  // Windows audio thread, which writes the output and `status` and answers.
  uint32_t frames;
  uint32_t status;
  // The plugin's latency in frames, written by the Windows main thread each
  // time the plugin is activated, read by the Linux main thread.
  _Atomic uint32_t latency;
  uint32_t reserved[10];
  float in[WIRE_MAX_CHANNELS][WIRE_MAX_FRAMES];
  float out[WIRE_MAX_CHANNELS][WIRE_MAX_FRAMES];
} WireShared;

enum { WIRE_HELLO = 1, WIRE_LOAD, WIRE_ACTIVATE, WIRE_DEACTIVATE, WIRE_QUIT };
enum { WIRE_FORMAT_CLAP = 1 };

typedef struct {
  uint32_t magic, type;
  uint32_t version;     // hello
  uint32_t format;      // load
  uint32_t channels;    // load: the width of the chain the plugin sits in
  uint32_t rate;        // activate
  uint32_t min_frames;  // activate
  uint32_t max_frames;  // activate
  char thread[WIRE_THREAD_NAME];  // hello: the name the audio thread takes
  char id[WIRE_ID];               // load: which plugin of the module
  char path[WIRE_PATH];           // load: the module, as a Unix path
} WireRequest;

typedef struct {
  uint32_t magic, type;  // the type of the request this answers
  uint32_t ok;
  uint32_t version;      // hello
  uint32_t shared_size;  // hello: sizeof(WireShared) as this side built it
  uint32_t latency;      // activate
  uint32_t reserved[2];
  // The plugin's name after a load, the reason after a refusal.
  char text[WIRE_TEXT];
} WireReply;

_Static_assert(sizeof(WireShared) ==
                   64 + 2 * WIRE_MAX_CHANNELS * WIRE_MAX_FRAMES * sizeof(float),
               "the shared file's layout");
_Static_assert(sizeof(WireRequest) ==
                   32 + WIRE_THREAD_NAME + WIRE_ID + WIRE_PATH,
               "a request's layout");
_Static_assert(sizeof(WireReply) == 32 + WIRE_TEXT, "a reply's layout");
