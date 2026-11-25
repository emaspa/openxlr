// SPDX-License-Identifier: GPL-3.0-only
// The two ends of the Windows backend's graph check (winhost-graph.py): one
// node that plays a known stereo signal and one that records what comes back
// and says what it heard. The check links the helper's node between them by
// hand, so all three run in one cycle of the same driver and see the same
// clock position.
//
// Each played sample names its own position, and the right channel is the
// left one plus a fixed step, so one recorded frame alone says the gain it
// was played at (four times the step between the channels) and how many
// frames late it is. Every frame is checked exactly against that reading.
// The recorder prints a line each time what it hears changes:
//
//   state POSITION silence
//   state POSITION gain GAIN delay FRAMES
//   state POSITION garbage LEFT RIGHT
//
// and `alive POSITION CYCLES` once a second. POSITION is the clock frame
// the change starts at. It listens only once `listen` arrives on its
// standard input: links are made one port at a time, and until the last is
// there one channel can be heard without the other. It runs until its
// standard input closes or it is asked to stop.
// Usage: winhost-graph QUANTUM
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <math.h>
#include <pipewire/filter.h>
#include <pipewire/pipewire.h>
#include <signal.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#define RATE 48000
#define SPAN 0x100000u  // positions a channel can name before it wraps

// As in winhost.c: never zero, exact at a half, a quarter and three quarters.
static float signal_at(uint64_t at, unsigned channel) {
  return (float)((at & (SPAN - 1)) + 1 + (channel ? SPAN : 0)) / (float)(1 << 22);
}

enum { SILENCE, AUDIO, GARBAGE };

typedef struct {
  int kind;
  float gain;
  uint32_t delay;
} State;

typedef struct {
  uint64_t position;
  State state;
  float left, right;  // the frame itself, for a line about garbage
} Event;

// Written by the recorder's data thread, read by the main thread.
enum { RING = 4096 };
static Event ring[RING];
static _Atomic uint32_t written, read_out;
static _Atomic uint64_t cycles, last_position;
static _Atomic bool overflowed, listening;

static struct pw_main_loop *loop;
static void *play_ports[2], *record_ports[2];
static State current = {.kind = -1};

static bool same(State a, State b) {
  return a.kind == b.kind && (a.kind != AUDIO || (a.gain == b.gain && a.delay == b.delay));
}

// What one recorded frame is: silence, the played signal at a gain and a
// delay, or anything else.
static State reading(float left, float right, uint64_t at) {
  State s = {.kind = GARBAGE};
  if (left == 0 && right == 0) return (State){.kind = SILENCE};
  float gain = 4 * (right - left);
  if (!(gain > 0 && gain <= 4) || !isfinite(left)) return s;
  long named = lrintf(left / gain * (float)(1 << 22)) - 1;
  if (named < 0 || named >= (long)SPAN) return s;
  if (left != signal_at((uint64_t)named, 0) * gain ||
      right != signal_at((uint64_t)named, 1) * gain)
    return s;
  return (State){.kind = AUDIO, .gain = gain,
                 .delay = (uint32_t)((at - (uint64_t)named) & (SPAN - 1))};
}

static void play(void *data, struct spa_io_position *position) {
  uint32_t frames = position->clock.duration;
  uint64_t at = position->clock.position;
  for (unsigned c = 0; c < 2; ++c) {
    float *out = pw_filter_get_dsp_buffer(play_ports[c], frames);
    if (!out) continue;
    for (uint32_t n = 0; n < frames; ++n) out[n] = signal_at(at + n, c);
  }
}

static void record(void *data, struct spa_io_position *position) {
  uint32_t frames = position->clock.duration;
  uint64_t at = position->clock.position;
  atomic_fetch_add(&cycles, 1);
  atomic_store(&last_position, at + frames);
  // Taken every cycle, heard or not: a buffer left in the port would be
  // read first once listening starts, a cycle late.
  float *in[2];
  for (unsigned c = 0; c < 2; ++c) in[c] = pw_filter_get_dsp_buffer(record_ports[c], frames);
  if (!atomic_load(&listening)) return;
  for (uint32_t n = 0; n < frames; ++n) {
    State s = in[0] && in[1] ? reading(in[0][n], in[1][n], at + n) : (State){.kind = SILENCE};
    if (same(s, current)) continue;
    current = s;
    uint32_t w = atomic_load(&written);
    if (w - atomic_load(&read_out) >= RING) {
      atomic_store(&overflowed, true);
      continue;
    }
    ring[w % RING] = (Event){at + n, s, in[0] ? in[0][n] : 0, in[1] ? in[1][n] : 0};
    atomic_store(&written, w + 1);
  }
}

static void drain(void) {
  uint32_t w = atomic_load(&written), r = atomic_load(&read_out);
  for (; r != w; ++r) {
    Event e = ring[r % RING];
    if (e.state.kind == SILENCE)
      printf("state %llu silence\n", (unsigned long long)e.position);
    else if (e.state.kind == AUDIO)
      printf("state %llu gain %.9g delay %u\n", (unsigned long long)e.position,
             e.state.gain, e.state.delay);
    else
      printf("state %llu garbage %.9g %.9g\n", (unsigned long long)e.position, e.left, e.right);
  }
  atomic_store(&read_out, r);
  if (atomic_exchange(&overflowed, false)) puts("overflow");
}

static void tick(void *data, uint64_t expirations) {
  static unsigned ticks;
  drain();
  if (++ticks % 50 == 0)
    printf("alive %llu %llu\n", (unsigned long long)atomic_load(&last_position),
           (unsigned long long)atomic_load(&cycles));
}

static void input(void *data, int fd, uint32_t mask) {
  char buffer[256];
  ssize_t count = read(fd, buffer, sizeof(buffer) - 1);
  if (count == 0 || (count < 0 && errno != EAGAIN && errno != EINTR)) pw_main_loop_quit(loop);
  if (count > 0) {
    buffer[count] = 0;
    if (strstr(buffer, "listen")) atomic_store(&listening, true);
  }
}

static void stop(void *data, int signal_number) { pw_main_loop_quit(loop); }

static const struct pw_filter_events play_events = {PW_VERSION_FILTER_EVENTS, .process = play};
static const struct pw_filter_events record_events = {PW_VERSION_FILTER_EVENTS, .process = record};

static struct pw_filter *make(const char *name, const struct pw_filter_events *events,
                              enum pw_direction direction, void **ports, const char *latency) {
  struct pw_filter *filter = pw_filter_new_simple(
      pw_main_loop_get_loop(loop), name,
      pw_properties_new(PW_KEY_NODE_NAME, name, PW_KEY_MEDIA_TYPE, "Audio",
                        PW_KEY_MEDIA_CATEGORY, "Filter", PW_KEY_MEDIA_ROLE, "DSP",
                        PW_KEY_NODE_LATENCY, latency, "node.autoconnect", "false",
                        "node.always-process", "true", NULL),
      events, NULL);
  if (!filter) return NULL;
  for (unsigned c = 0; c < 2; ++c) {
    char port[16];
    snprintf(port, sizeof(port), "%s_%u", direction == PW_DIRECTION_OUTPUT ? "out" : "in", c);
    ports[c] = pw_filter_add_port(
        filter, direction, PW_FILTER_PORT_FLAG_MAP_BUFFERS, 1,
        pw_properties_new(PW_KEY_FORMAT_DSP, "32 bit float mono audio", PW_KEY_PORT_NAME, port,
                          PW_KEY_AUDIO_CHANNEL, c ? "FR" : "FL", NULL),
        NULL, 0);
    if (!ports[c]) return NULL;
  }
  return pw_filter_connect(filter, PW_FILTER_FLAG_RT_PROCESS, NULL, 0) < 0 ? NULL : filter;
}

int main(int argc, char **argv) {
  unsigned quantum = argc == 2 ? (unsigned)strtoul(argv[1], NULL, 10) : 0;
  if (quantum < 16 || quantum > 8192) {
    fputs("usage: winhost-graph QUANTUM\n", stderr);
    return 2;
  }
  setvbuf(stdout, NULL, _IOLBF, 0);
  pw_init(&argc, &argv);
  loop = pw_main_loop_new(NULL);
  if (!loop) return 1;
  char latency[32];
  snprintf(latency, sizeof(latency), "%u/%u", quantum, RATE);
  struct pw_filter *player =
      make("openxlr-graph-play", &play_events, PW_DIRECTION_OUTPUT, play_ports, latency);
  struct pw_filter *recorder =
      make("openxlr-graph-record", &record_events, PW_DIRECTION_INPUT, record_ports, latency);
  if (!player || !recorder) {
    fputs("cannot make the graph check's nodes\n", stderr);
    return 1;
  }
  struct pw_loop *l = pw_main_loop_get_loop(loop);
  fcntl(STDIN_FILENO, F_SETFL, O_NONBLOCK);
  pw_loop_add_io(l, STDIN_FILENO, SPA_IO_IN | SPA_IO_HUP, false, input, NULL);
  struct spa_source *timer = pw_loop_add_timer(l, tick, NULL);
  struct timespec every = {0, 20000000};
  pw_loop_update_timer(l, timer, &every, &every, false);
  pw_loop_add_signal(l, SIGTERM, stop, NULL);
  pw_loop_add_signal(l, SIGINT, stop, NULL);
  puts("ready");
  pw_main_loop_run(loop);
  drain();
  pw_filter_destroy(player);
  pw_filter_destroy(recorder);
  pw_main_loop_destroy(loop);
  pw_deinit();
  return 0;
}
