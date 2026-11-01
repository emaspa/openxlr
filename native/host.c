// SPDX-License-Identifier: GPL-3.0-only
// The process around one plugin: its PipeWire node, its command pipe, its
// editor window and its threads. The audio callback never allocates, logs,
// performs IPC or calls the editor.
#include "host.h"
#include "icon.h"
// Weak, so the tests that compile this file without measure.o still link.
#define LATENCY_METER_LINKAGE __attribute__((weak))
#include "measure.h"

#include <X11/Xatom.h>
#include <X11/Xutil.h>
#include <errno.h>
#include <fcntl.h>
#include <math.h>
#include <poll.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

sigjmp_buf x_escape;
volatile sig_atomic_t x_escape_armed;

static int report_x_error(Display *display, XErrorEvent *event) {
  char text[256];
  XGetErrorText(display, event->error_code, text, sizeof(text));
  fprintf(stderr, "editor X11 error: %s (request %u)\n", text,
          event->request_code);
  return 0;
}

static int lost_x_connection(Display *display) {
  if (x_escape_armed) {
    x_escape_armed = 0;
    siglongjmp(x_escape, 1);
  }
  // Xlib only calls this from inside one of its own functions, and every one
  // this host calls is armed. Leave rather than return: Xlib exits either way.
  _exit(1);
}

// --- what backends call ----------------------------------------------------

Control *host_add_control(Host *h, const char *symbol, float minimum,
                          float maximum, float initial, bool output,
                          void *backend) {
  if (h->control_count == MAX_CONTROLS)
    return NULL;
  Control *c = &h->controls[h->control_count++];
  c->symbol = symbol;
  c->minimum = isfinite(minimum) ? minimum : -1e10f;
  c->maximum = isfinite(maximum) ? maximum : 1e10f;
  c->output = output;
  c->reported = NAN;
  atomic_init(&c->desired, isfinite(initial) ? initial : 0);
  atomic_init(&c->observed, isfinite(initial) ? initial : 0);
  atomic_init(&c->moved, false);
  c->backend = backend;
  return c;
}

void host_control_moved(Host *h, Control *c, float value) {
  if (c->output || !isfinite(value))
    return;
  value = fminf(c->maximum, fmaxf(c->minimum, value));
  atomic_store(&c->desired, value);
  printf("control %s %.9g\n", c->symbol, value);
}

void host_control_moved_rt(Host *h, Control *c, float value) {
  if (!isfinite(value))
    return;
  value = fminf(c->maximum, fmaxf(c->minimum, value));
  atomic_store(&c->desired, value);
  atomic_store(&c->observed, value);
  atomic_store(&c->moved, true);
}

// Drop the editor without talking to X, for a connection that is already
// gone. What the plugin built inside the window cannot be cleaned up through
// a dead connection, so it stays until this process exits.
static void forget_ui(Host *h) {
  if (h->editor_source) {
    pw_loop_destroy_source(host_loop(h), h->editor_source);
    h->editor_source = NULL;
  }
  if (h->editor_open && h->backend->editor_lost)
    h->backend->editor_lost(h);
  h->editor_open = false;
  h->display = NULL;
  h->window = 0;
  h->child = 0;
  h->notified_width = h->notified_height = 0;
  h->self_width = h->self_height = 0;
  h->child_width = h->child_height = 0;
  h->settle_ticks = h->settle_width = h->settle_height = 0;
  h->frame_x = h->frame_y = 0;
  h->dragged_at = (struct timespec){0};
}

void host_editor_lost(Host *h) {
  forget_ui(h);
  fputs("editor X11 connection lost; audio continues without it\n", stderr);
}

// A backend's own callbacks (a plugin's timer, say) reach these outside any
// guard, so each one arms its own unless a caller already has.
static bool same_editor_size(Host *h, unsigned width, unsigned height);
static bool dragging(Host *h);
static void update_editor_hints(Host *h);
static void settle_editor_queue(Host *h);

void host_set_editor_resizable(Host *h, bool resizable) {
  h->editor_resizable = resizable;
}

void host_resize_editor(Host *h, unsigned width, unsigned height) {
  if (!h->display || !h->window || width < 1 || height < 1 ||
      width > 16384 || height > 16384 || dragging(h))
    return;
  h->self_width = width;
  h->self_height = height;
  if (x_escape_armed) {
    if (!h->editor_resizable)
      update_editor_hints(h);
    XResizeWindow(h->display, h->window, width, height);
    return;
  }
  if (sigsetjmp(x_escape, 0)) {
    x_escape_armed = 0;
    host_editor_lost(h);
    return;
  }
  x_escape_armed = 1;
  if (!h->editor_resizable)
    update_editor_hints(h);
  XResizeWindow(h->display, h->window, width, height);
  XFlush(h->display);
  x_escape_armed = 0;
  settle_editor_queue(h);
}

void host_show_editor(Host *h, bool show) {
  if (!h->display || !h->window)
    return;
  if (x_escape_armed) {
    if (show)
      XMapRaised(h->display, h->window);
    else
      XUnmapWindow(h->display, h->window);
    return;
  }
  if (sigsetjmp(x_escape, 0)) {
    x_escape_armed = 0;
    host_editor_lost(h);
    return;
  }
  x_escape_armed = 1;
  if (show)
    XMapRaised(h->display, h->window);
  else
    XUnmapWindow(h->display, h->window);
  XFlush(h->display);
  x_escape_armed = 0;
}

// Any plugin callback that may touch X runs inside the escape, so a lost
// display costs the editor and not the process. A caller already inside
// the escape is simply called through.
void host_run_guarded(Host *h, void (*call)(void *), void *argument) {
  if (x_escape_armed) {
    call(argument);
    return;
  }
  if (sigsetjmp(x_escape, 0)) {
    x_escape_armed = 0;
    host_editor_lost(h);
    return;
  }
  x_escape_armed = 1;
  call(argument);
  x_escape_armed = 0;
  settle_editor_queue(h);
}

uint32_t host_rate(const Host *h) { return h->rate; }
uint32_t host_max_block(const Host *h) {
  return h->own_block ? OWN_BLOCK_FRAMES : MAX_FRAMES;
}
unsigned host_channels(const Host *h) { return h->channels; }
const float *host_key(const Host *h) { return h->key; }
unsigned long host_window(const Host *h) { return h->window; }
void *host_impl(const Host *h) { return h->impl; }
void host_set_impl(Host *h, void *impl) { h->impl = impl; }
void host_set_has_editor(Host *h, bool has_editor) { h->has_editor = has_editor; }
void host_set_plugin_name(Host *h, const char *name) {
  // A scan has no host: it describes a bundle's plugins without running one.
  if (h && name && name[0])
    snprintf(h->plugin_name, sizeof(h->plugin_name), "%s", name);
}
uint32_t host_control_count(const Host *h) { return h->control_count; }
Control *host_control_at(Host *h, uint32_t index) {
  return index < h->control_count ? &h->controls[index] : NULL;
}
float control_desired(const Control *c) { return atomic_load(&c->desired); }
void control_set_desired(Control *c, float value) {
  atomic_store(&c->desired, fminf(c->maximum, fmaxf(c->minimum, value)));
}
void control_set_observed(Control *c, float value) {
  atomic_store(&c->observed, value);
}
bool control_is_output(const Control *c) { return c->output; }
void *control_backend(const Control *c) { return c->backend; }

void host_fail(Host *h, const char *why) {
  fprintf(stderr, "%s; restart the chain\n", why);
  atomic_store(&h->failure_explained, true);
  atomic_store(&h->audio_error, true);
}

// Plugins may refuse a block now and then (CLAP lets start_processing be
// retried on the next one), and a refused block costs only its own audio.
// Replacing the instance costs far more, seconds for a bridged plugin, and
// spends the restart budget, so only a run of refusals counts as failure.
void host_processing_result(Host *h, bool ok, uint32_t frames) {
  if (ok) {
    h->failed_frames = 0;
    return;
  }
  h->failed_frames += frames;
  if (h->failed_frames < h->rate / 2) return;
  atomic_store(&h->process_failed, true);
  atomic_store(&h->audio_error, true);
}

// Some plugins put out NaN or infinity on pure digital silence, a denoiser
// whose noise estimate decays to zero for one, and recover once sound comes
// back (Alt Denoiser, issue 577, within a tenth of a second). A muted
// interface sends exactly that silence for minutes. Retiring the instance
// there spends the restart budget on audio nobody hears and leaves the chain
// off when the microphone is unmuted, so invalid output on silent input does
// not count, and on sound only a run of half a second does, as with refused
// blocks. The block itself is always silenced.
void host_output_result(Host *h, bool finite, bool silent_input, uint32_t frames) {
  if (finite) {
    h->invalid_frames = 0;
    return;
  }
  if (silent_input) return;
  h->invalid_frames += frames;
  if (h->invalid_frames < h->rate / 2) return;
  atomic_store(&h->invalid_audio, AUDIO_INVALID_OUTPUT);
  atomic_store(&h->audio_error, true);
}

struct pw_loop *host_loop(Host *h) { return pw_main_loop_get_loop(h->loop); }

bool host_on_main_thread(Host *h) {
  return pthread_equal(pthread_self(), h->main_thread);
}

bool host_on_audio_thread(Host *h) {
  return atomic_load(&h->audio_thread_known) &&
         pthread_equal(pthread_self(), h->audio_thread);
}

// --- audio ------------------------------------------------------------------

bool host_quantum_supported(const Host *h, uint32_t frames, uint32_t rate_denom) {
  return frames <= MAX_FRAMES && rate_denom == h->rate;
}

float *host_fallback(float *buffer, uint32_t frames, bool clear) {
  if (clear)
    memset(buffer, 0,
           sizeof(float) * (frames > MAX_FRAMES ? MAX_FRAMES : frames));
  return buffer;
}

bool host_audio_stuck(uint64_t entered, uint64_t left, uint64_t *last,
                      unsigned *outstanding, unsigned window) {
  if (entered == left) {  // nothing in the plugin right now
    *last = entered;
    *outstanding = 0;
    return false;
  }
  if (entered != *last) {  // a different call, so the previous one returned
    *last = entered;
    *outstanding = 1;
    return false;
  }
  if (*outstanding < window)
    ++*outstanding;
  return *outstanding >= window;
}

static bool finite_audio(float *const *buffers, unsigned channels,
                          uint32_t frames) {
  for (unsigned c = 0; c < channels; ++c)
    for (uint32_t n = 0; n < frames; ++n)
      if (!isfinite(buffers[c][n])) return false;
  return true;
}

static bool silent_audio(float *const *buffers, unsigned channels,
                         uint32_t frames) {
  for (unsigned c = 0; c < channels; ++c)
    for (uint32_t n = 0; n < frames; ++n)
      if (buffers[c][n] != 0) return false;
  return true;
}

// --- the plugin's own block ---------------------------------------------------

// A plugin that cannot finish a short block in time, typically one bridged
// from Windows whose cost is mostly per call, runs on a block of its own:
// OWN_BLOCK_FRAMES at a time on a worker thread, while the audio callback
// only moves samples. The callback writes the input into one ring, wakes the
// worker each time a block is complete, and reads the other ring a fixed
// delay behind what it wrote. It never waits for the worker. A block the
// worker has not answered by the time it is due plays as silence, and since
// the answer for input frame n is output frame n, a late block costs its own
// frames and never shifts the rest.
//
// The delay is the block plus the time the worker has to answer it. A block
// is complete only at the end of a cycle and is due to start playing when
// the read position reaches it, so with cycles of q frames that dividing the
// block the worker has delay - block + q frames of time: 768 frames, 16 ms
// at 48 kHz, for a delay of 1536 under cycles of 256. Larger cycles get a
// delay of the block plus one cycle, so a block that completes in one cycle
// is never due in the same one.
static uint32_t own_block_delay(uint32_t frames) {
  return OWN_BLOCK_FRAMES + (frames > OWN_BLOCK_SLACK ? frames : OWN_BLOCK_SLACK);
}

static bool own_block_allowed(const Backend *backend) {
  return backend == &lv2_backend || backend == &clap_backend ||
         backend == &vst3_backend;
}

static bool own_block_init(Host *h) {
  OwnBlock *b = &h->block;
  for (unsigned c = 0; c < h->channels; ++c)
    if (!(b->in[c] = calloc(OWN_BLOCK_RING, sizeof(float))) ||
        !(b->out[c] = calloc(OWN_BLOCK_RING, sizeof(float))) ||
        !(b->block_in[c] = calloc(OWN_BLOCK_FRAMES, sizeof(float))) ||
        !(b->block_out[c] = calloc(OWN_BLOCK_FRAMES, sizeof(float))))
      return false;
  if (sem_init(&b->wake, 0, 0) != 0) return false;
  b->wake_ready = true;
  return true;
}

static void own_block_free(Host *h) {
  OwnBlock *b = &h->block;
  for (unsigned c = 0; c < MAX_CHANNELS; ++c) {
    free(b->in[c]), free(b->out[c]), free(b->block_in[c]), free(b->block_out[c]);
    b->in[c] = b->out[c] = b->block_in[c] = b->block_out[c] = NULL;
  }
  if (b->wake_ready) sem_destroy(&b->wake);
  b->wake_ready = false;
}

static bool finite_audio(float *const *buffers, unsigned channels,
                         uint32_t frames);
static bool silent_audio(float *const *buffers, unsigned channels,
                         uint32_t frames);

// The audio thread's whole part: take this cycle's input, and play what the
// worker answered for the frames `delay` before it. It allocates nothing,
// takes no lock and waits for nothing; waking the worker is a semaphore post.
static void own_block_cycle(Host *h, uint32_t frames, float *const *in,
                            float *const *out) {
  OwnBlock *b = &h->block;
  const uint64_t mask = OWN_BLOCK_RING - 1;
  uint64_t written = atomic_load_explicit(&b->written, memory_order_relaxed);
  uint64_t done = atomic_load_explicit(&b->done, memory_order_acquire);
  // The worker may still be reading the oldest unanswered block, so a ring
  // with no room for this cycle is left alone and the cycle marked lost.
  // That takes a worker stuck for most of a second.
  if (written + frames - done <= OWN_BLOCK_RING) {
    for (unsigned c = 0; c < h->channels; ++c)
      for (uint32_t n = 0; n < frames; ++n)
        b->in[c][(written + n) & mask] = in[c][n];
  } else
    atomic_store_explicit(&b->lost, written + frames, memory_order_relaxed);
  atomic_store_explicit(&b->written, written + frames, memory_order_release);

  // A larger cycle needs a longer delay at once; a smaller one shortens it
  // only once it has held for two seconds, so a quantum that changes back
  // and forth does not keep moving the audio.
  uint32_t want = own_block_delay(frames);
  if (want >= b->current) {
    b->current = want;
    b->smaller_for = 0;
  } else if ((b->smaller_for += frames) >= 2 * h->rate) {
    b->current = want;
    b->smaller_for = 0;
  }
  atomic_store_explicit(&b->delay, b->current, memory_order_relaxed);

  // Output frame `written + n` is input frame `written + n - delay`. Before
  // the first input, and where a longer delay would play again what was
  // already heard, the output is silence; a frame the worker has not
  // answered yet is silence too, and that is a short cycle.
  int64_t start = (int64_t)written - (int64_t)b->current;
  uint64_t heard = atomic_load_explicit(&b->heard, memory_order_relaxed);
  done = atomic_load_explicit(&b->done, memory_order_acquire);
  bool short_cycle = false;
  for (uint32_t n = 0; n < frames; ++n) {
    int64_t at = start + (int64_t)n;
    bool play = at >= 0 && (uint64_t)at >= heard;
    if (play && (uint64_t)at >= done) {
      play = false;
      short_cycle = true;
    }
    for (unsigned c = 0; c < h->channels; ++c)
      out[c][n] = play ? b->out[c][(uint64_t)at & mask] : 0;
  }
  if (start + (int64_t)frames > (int64_t)heard)
    atomic_store_explicit(&b->heard, (uint64_t)(start + (int64_t)frames),
                          memory_order_release);
  if (short_cycle)
    atomic_fetch_add_explicit(&b->short_cycles, 1, memory_order_relaxed);
  if ((written + frames) / OWN_BLOCK_FRAMES != written / OWN_BLOCK_FRAMES)
    sem_post(&b->wake);
}

// One block through the plugin, with the checks the audio callback makes
// around an ordinary call: entered and left for the stall monitor, and a
// block of invalid output silenced and counted the same way.
static void own_block_process(Host *h) {
  OwnBlock *b = &h->block;
  float *in[MAX_CHANNELS], *out[MAX_CHANNELS];
  for (unsigned c = 0; c < h->channels; ++c) {
    in[c] = b->block_in[c];
    out[c] = b->block_out[c];
  }
  bool invalid = false;
  if (!atomic_load(&h->audio_error)) {
    bool silent = silent_audio(in, h->channels, OWN_BLOCK_FRAMES);
    atomic_fetch_add(&h->audio_entered, 1);
    h->backend->process(h, OWN_BLOCK_FRAMES, in, out);
    atomic_fetch_add(&h->audio_left, 1);
    invalid = !finite_audio(out, h->channels, OWN_BLOCK_FRAMES);
    host_output_result(h, !invalid, silent, OWN_BLOCK_FRAMES);
  }
  if (invalid || atomic_load(&h->audio_error))
    for (unsigned c = 0; c < h->channels; ++c)
      memset(out[c], 0, sizeof(float) * OWN_BLOCK_FRAMES);
}

// Answer every complete block waiting, oldest first. A block whose frames
// have all been played already is dropped without a call: nobody would hear
// the answer, and skipping it puts the plugin back on time at once, which
// bounds the delay after a spike instead of letting it grow. The plugin
// hears the jump. A block whose input never reached the ring is answered
// with silence; everything below the newest such loss counts as lost, which
// after a stall long enough to fill the ring costs at most one more delay.
static void own_block_drain(Host *h) {
  OwnBlock *b = &h->block;
  const uint64_t mask = OWN_BLOCK_RING - 1;
  uint64_t at = atomic_load_explicit(&b->done, memory_order_relaxed);
  while (!atomic_load(&b->stop)) {
    uint64_t written = atomic_load_explicit(&b->written, memory_order_acquire);
    if (written - at < OWN_BLOCK_FRAMES) return;
    uint64_t heard = atomic_load_explicit(&b->heard, memory_order_acquire);
    if (at + OWN_BLOCK_FRAMES <= heard) {
      atomic_fetch_add_explicit(&b->dropped_blocks, 1, memory_order_relaxed);
    } else if (at < atomic_load_explicit(&b->lost, memory_order_relaxed)) {
      for (unsigned c = 0; c < h->channels; ++c)
        for (uint32_t n = 0; n < OWN_BLOCK_FRAMES; ++n)
          b->out[c][(at + n) & mask] = 0;
      atomic_fetch_add_explicit(&b->dropped_blocks, 1, memory_order_relaxed);
    } else {
      for (unsigned c = 0; c < h->channels; ++c)
        for (uint32_t n = 0; n < OWN_BLOCK_FRAMES; ++n)
          b->block_in[c][n] = b->in[c][(at + n) & mask];
      own_block_process(h);
      for (unsigned c = 0; c < h->channels; ++c)
        for (uint32_t n = 0; n < OWN_BLOCK_FRAMES; ++n)
          b->out[c][(at + n) & mask] = b->block_out[c][n];
    }
    at += OWN_BLOCK_FRAMES;
    atomic_store_explicit(&b->done, at, memory_order_release);
  }
}

static void *own_block_work(void *data) {
  Host *h = data;
  h->audio_thread = pthread_self();
  atomic_store(&h->audio_thread_known, true);
  while (!atomic_load(&h->block.stop)) {
    if (sem_wait(&h->block.wake) != 0 && errno != EINTR) break;
    own_block_drain(h);
  }
  return NULL;
}

// The worker is made real-time the way PipeWire makes its own data threads:
// through the thread utilities its context loaded, RTKit or the process's
// own limits, at the priority configured for them. Without them it still
// runs, at ordinary priority, and says so.
static bool own_block_start(Host *h, struct spa_thread_utils *utils) {
  OwnBlock *b = &h->block;
  if (!utils) utils = pw_thread_utils_get();
  struct spa_dict_item items[] = {
      SPA_DICT_ITEM_INIT(SPA_KEY_THREAD_NAME, "openxlr-block")};
  struct spa_dict props = SPA_DICT_INIT_ARRAY(items);
  b->utils = utils;
  b->thread = spa_thread_utils_create(utils, &props, own_block_work, h);
  if (!b->thread) return false;
  if (spa_thread_utils_acquire_rt(utils, b->thread, -1) < 0)
    fputs("note: the plugin's own block runs without real-time priority\n",
          stderr);
  return true;
}

static void own_block_stop(Host *h) {
  OwnBlock *b = &h->block;
  if (!b->thread) return;
  atomic_store(&b->stop, true);
  sem_post(&b->wake);
  spa_thread_utils_join(b->utils, b->thread, NULL);
  b->thread = NULL;
}

static void process_audio(void *data, struct spa_io_position *position) {
  Host *h = data;
  // On its own block the plugin is called from the worker, which is the
  // thread a plugin asking about its audio thread has to hear about.
  if (!h->own_block && !atomic_load(&h->audio_thread_known)) {
    h->audio_thread = pthread_self();
    atomic_store(&h->audio_thread_known, true);
  }
  uint32_t frames = position->clock.duration;
  // Refuse the cycle before anything is written. The fallback buffers hold
  // MAX_FRAMES frames, so a larger quantum has to be turned away here: the
  // silence and scratch writes below would run past their end, and the
  // check that used to sit after them came too late to prevent that.
  // The output ports' own buffers are sized for this quantum, so those are
  // the only ones it is safe to clear on the way out.
  if (!host_quantum_supported(h, frames, position->clock.rate.denom)) {
    atomic_store(&h->audio_error, true);
    for (unsigned i = 0; i < h->channels; ++i) {
      float *buffer = pw_filter_get_dsp_buffer(h->out_ports[i], frames);
      if (buffer)
        memset(buffer, 0, sizeof(float) * frames);
    }
    for (unsigned t = 0; t < h->tap_count; ++t) {
      float *buffer = pw_filter_get_dsp_buffer(h->tap_ports[t], frames);
      if (buffer)
        memset(buffer, 0, sizeof(float) * frames);
    }
    return;
  }
  float *in[MAX_CHANNELS], *out[MAX_CHANNELS];
  h->outputs_unread = true;
  for (unsigned i = 0; i < h->channels; ++i) {
    float *buffer = pw_filter_get_dsp_buffer(h->in_ports[i], frames);
    if (!buffer)
      buffer = host_fallback(h->silence, frames, true);
    in[i] = buffer;
    out[i] = pw_filter_get_dsp_buffer(h->out_ports[i], frames);
    if (out[i])
      h->outputs_unread = false;
    else
      out[i] = host_fallback(h->scratch, frames, false);
  }
  h->key = h->silence;
  if (h->key_port) {
    const float *buffer = pw_filter_get_dsp_buffer(h->key_port, frames);
    h->key = buffer ? buffer : host_fallback(h->silence, frames, true);
  }
  for (unsigned t = 0; t < MAX_TAPS; ++t) {
    h->taps[t] = NULL;
    if (t >= h->tap_count) continue;
    float *buffer = pw_filter_get_dsp_buffer(h->tap_ports[t], frames);
    h->taps[t] = buffer ? buffer : host_fallback(h->tap_scratch[t], frames, false);
  }
  // The input as it enters the node, before a plugin can write over it.
  if (h->measure)
    latency_meter_input(h->measure, in, h->channels, frames);
  // One invalid sample can poison a stateful processor until it is replaced.
  // Keep it out of the plugin and of downstream nodes. Finite headroom above
  // unity is left alone. Reporting and replacement happen off this thread.
  if (!atomic_load(&h->audio_error) && !finite_audio(in, h->channels, frames)) {
    atomic_store(&h->invalid_audio, AUDIO_INVALID_INPUT);
    atomic_store(&h->audio_error, true);
  }
  bool invalid_output = false;
  if (h->own_block) {
    // The worker checks the plugin's output and brackets its calls; this
    // thread only moves samples.
    if (!atomic_load(&h->audio_error))
      own_block_cycle(h, frames, in, out);
  } else if (!atomic_load(&h->audio_error)) {
    // Read before the call: a plugin may process in place.
    bool silent = silent_audio(in, h->channels, frames);
    // Bracket the plugin's callback so the process monitor can tell a stuck
    // one from an idle node: entered and left differ only while a call is
    // outstanding, and a stuck call keeps the same entered value.
    atomic_fetch_add(&h->audio_entered, 1);
    h->backend->process(h, frames, in, out);
    atomic_fetch_add(&h->audio_left, 1);
    invalid_output = !finite_audio(out, h->channels, frames);
    host_output_result(h, !invalid_output, silent, frames);
  }
  // A failed instance stays silent until the next main-loop tick stops it,
  // and an invalid block never reaches another node.
  if (invalid_output || atomic_load(&h->audio_error)) {
    for (unsigned c = 0; c < h->channels; ++c)
      memset(out[c], 0, sizeof(float) * frames);
    for (unsigned t = 0; t < h->tap_count; ++t)
      memset(h->taps[t], 0, sizeof(float) * frames);
  }
  // The output as it leaves, so the measured delay holds all the node adds.
  if (h->measure)
    latency_meter_output(h->measure, out, h->channels, frames);
}

static void state_changed(void *data, enum pw_filter_state old,
                          enum pw_filter_state state, const char *error) {
  Host *h = data;
  if (state == PW_FILTER_STATE_ERROR) {
    fprintf(stderr, "PipeWire: %s\n", error ? error : "disconnected");
    h->exit_code = 1;
    pw_main_loop_quit(h->loop);
  } else if (state == PW_FILTER_STATE_PAUSED)
    puts("ready");
}

static const struct pw_filter_events filter_events = {
    PW_VERSION_FILTER_EVENTS, .state_changed = state_changed,
    .process = process_audio};

static bool add_audio_ports(Host *h) {
  for (unsigned i = 0; i < h->channels; ++i) {
    for (int output = 0; output < 2; ++output) {
      char name[32];
      snprintf(name, sizeof(name), "%s_%u", output ? "capture" : "playback",
               i);
      void *port = pw_filter_add_port(
          h->filter, output ? PW_DIRECTION_OUTPUT : PW_DIRECTION_INPUT,
          PW_FILTER_PORT_FLAG_MAP_BUFFERS, 1,
          pw_properties_new(PW_KEY_FORMAT_DSP, "32 bit float mono audio",
                            PW_KEY_PORT_NAME, name, PW_KEY_AUDIO_CHANNEL,
                            h->channels == 1 ? "MONO"
                            : i == 0         ? "FL"
                                             : "FR",
                            NULL),
          NULL, 0);
      if (!port) return false;
      if (output)
        h->out_ports[i] = port;
      else
        h->in_ports[i] = port;
    }
  }
  if (h->backend->key_inputs == 1) {
    h->key_port = pw_filter_add_port(
        h->filter, PW_DIRECTION_INPUT, PW_FILTER_PORT_FLAG_MAP_BUFFERS, 1,
        pw_properties_new(PW_KEY_FORMAT_DSP, "32 bit float mono audio",
                          PW_KEY_PORT_NAME, "key_0", PW_KEY_AUDIO_CHANNEL,
                          "MONO", NULL),
        NULL, 0);
    if (!h->key_port) return false;
  }
  h->tap_count = 0;
  for (const TapOutput *tap = h->backend->tap_outputs; tap && tap->port; ++tap) {
    if (h->tap_count == MAX_TAPS) return false;
    void *port = pw_filter_add_port(
        h->filter, PW_DIRECTION_OUTPUT, PW_FILTER_PORT_FLAG_MAP_BUFFERS, 1,
        pw_properties_new(PW_KEY_FORMAT_DSP, "32 bit float mono audio",
                          PW_KEY_PORT_NAME, tap->port, PW_KEY_AUDIO_CHANNEL,
                          tap->channel, NULL),
        NULL, 0);
    if (!port) return false;
    h->tap_ports[h->tap_count++] = port;
  }
  return true;
}

// --- the editor's window ----------------------------------------------------

// A window manager only reads the outer window's hints. Embedded editors
// such as LSP put their limits on the child, so copy the size bounds and
// also enforce them for resize requests that bypass the window manager.
static void update_editor_hints(Host *h) {
  XSizeHints child = {0}, hints = {0};
  long supplied;
  h->editor_min_width = h->editor_min_height = 1;
  h->editor_max_width = h->editor_max_height = 16384;
  if (h->child && XGetWMNormalHints(h->display, h->child, &child, &supplied)) {
    if ((child.flags & PMinSize) && child.min_width > 0 && child.min_height > 0 &&
        child.min_width <= 16384 && child.min_height <= 16384) {
      h->editor_min_width = (unsigned)child.min_width;
      h->editor_min_height = (unsigned)child.min_height;
    }
    if ((child.flags & PMaxSize) && child.max_width >= (int)h->editor_min_width &&
        child.max_height >= (int)h->editor_min_height) {
      if (child.max_width < 16384)
        h->editor_max_width = (unsigned)child.max_width;
      if (child.max_height < 16384)
        h->editor_max_height = (unsigned)child.max_height;
    }
  }
  if (!h->editor_resizable && h->self_width && h->self_height) {
    h->editor_min_width = h->editor_max_width = h->self_width;
    h->editor_min_height = h->editor_max_height = h->self_height;
  }
  hints.flags = PMinSize | PMaxSize;
  hints.min_width = (int)h->editor_min_width;
  hints.min_height = (int)h->editor_min_height;
  hints.max_width = (int)h->editor_max_width;
  hints.max_height = (int)h->editor_max_height;
  XSetWMNormalHints(h->display, h->window, &hints);
}

// A plugin builds its interface in a window of its own inside ours, at
// whatever size it wants, and many never call the host's resize. Take the
// size from that window, and watch it so later changes follow.
static void fit_to_child(Host *h) {
  Window root, parent, *children = NULL;
  unsigned count = 0;
  if (!XQueryTree(h->display, h->window, &root, &parent, &children, &count))
    return;
  if (count > 0) {
    h->child = children[count - 1];
    XSelectInput(h->display, h->child, StructureNotifyMask | PropertyChangeMask);
    XWindowAttributes attributes;
    if (XGetWindowAttributes(h->display, h->child, &attributes) &&
        attributes.width > 0 && attributes.height > 0) {
      h->self_width = (unsigned)attributes.width;
      h->self_height = (unsigned)attributes.height;
      update_editor_hints(h);
      XResizeWindow(h->display, h->window, h->self_width, h->self_height);
    }
  }
  if (children)
    XFree(children);
}

static void close_ui(Host *h) {
  if (h->editor_source) {
    pw_loop_destroy_source(host_loop(h), h->editor_source);
    h->editor_source = NULL;
  }
  if (!h->display) {
    forget_ui(h);
    return;
  }
  if (sigsetjmp(x_escape, 0) == 0) {
    x_escape_armed = 1;
    if (h->editor_open)
      h->backend->editor_close(h);
    h->editor_open = false;
    if (h->window)
      XDestroyWindow(h->display, h->window);
    XCloseDisplay(h->display);
  }
  x_escape_armed = 0;
  forget_ui(h);
}

// Raise a window that is already up. A failure here is a lost connection.
static bool raise_ui(Host *h) {
  if (sigsetjmp(x_escape, 0)) {
    x_escape_armed = 0;
    forget_ui(h);
    return false;
  }
  x_escape_armed = 1;
  XMapRaised(h->display, h->window);
  XFlush(h->display);
  x_escape_armed = 0;
  return true;
}

// Open the window and hand it to the backend. Every X call of the editor's
// life happens here, in raise_ui, in the event pump or in a backend's guarded
// callback. Watch the X connection so a drag need not wait for the idle tick.
static void editor_events(void *data, int fd, uint32_t mask);
static void pump_editor(Host *h, bool idle);

static bool open_ui(Host *h) {
  if (h->editor_open)
    return raise_ui(h);
  if (!h->has_editor)
    return false;
  if (sigsetjmp(x_escape, 0)) {
    x_escape_armed = 0;
    forget_ui(h);
    return false;
  }
  x_escape_armed = 1;
  h->editor_resizable = true;
  h->display = XOpenDisplay(NULL);
  if (h->display) {
    h->window = XCreateSimpleWindow(h->display, DefaultRootWindow(h->display),
                                    0, 0, 900, 600, 0, 0, 0x16181d);
    char title[160];
    snprintf(title, sizeof(title), "OpenXLR - %s",
             h->plugin_name[0] ? h->plugin_name : h->backend->name);
    XStoreName(h->display, h->window, title);
    // The same class as the main window, so the desktop files it with the
    // application, and the application's icon, since no plugin format
    // carries one of its own.
    XClassHint class_hint = {(char *)"openxlr", (char *)"OpenXLR.UI"};
    XSetClassHint(h->display, h->window, &class_hint);
    XChangeProperty(h->display, h->window,
                    XInternAtom(h->display, "_NET_WM_ICON", False), XA_CARDINAL,
                    32, PropModeReplace, (const unsigned char *)openxlr_icon,
                    (int)openxlr_icon_length);
    XChangeProperty(h->display, h->window,
                    XInternAtom(h->display, "_OPENXLR_NODE", False), XA_STRING,
                    8, PropModeReplace, (const unsigned char *)h->node_name,
                    (int)strlen(h->node_name));
    h->close_message = XInternAtom(h->display, "WM_DELETE_WINDOW", False);
    XSetWMProtocols(h->display, h->window, &h->close_message, 1);
    XSelectInput(h->display, h->window,
                 StructureNotifyMask | SubstructureNotifyMask | FocusChangeMask);
    h->editor_open = h->backend->editor_open(h);
    if (h->editor_open) {
      fit_to_child(h);  // before mapping, so the frame never opens wrong
      XMapRaised(h->display, h->window);
      XFlush(h->display);
      h->editor_source = pw_loop_add_io(host_loop(h), ConnectionNumber(h->display),
                                        SPA_IO_IN | SPA_IO_HUP | SPA_IO_ERR,
                                        false, editor_events, h);
      if (h->backend->editor_coordinate_nudge)
        h->settle_ticks = 6;   // once it is on screen, teach it where that is
    }
  }
  x_escape_armed = 0;
  if (!h->editor_open)
    close_ui(h);
  else
    // Mapping the frame and measuring the plugin both wait for replies, so
    // the events that opening brought in may already be off the socket.
    pump_editor(h, false);
  return h->editor_open;
}

// Whether the user has the frame's corner right now: it changed size for a
// reason that was not the plugin's, moments ago. A plugin's request is not
// answered until they let go, so the window follows the hand holding it.
static bool dragging(Host *h) {
  if (h->dragged_at.tv_sec == 0 && h->dragged_at.tv_nsec == 0)
    return false;
  struct timespec now;
  clock_gettime(CLOCK_MONOTONIC, &now);
  long since = (now.tv_sec - h->dragged_at.tv_sec) * 1000 +
               (now.tv_nsec - h->dragged_at.tv_nsec) / 1000000;
  return since < 400;
}

// X reports moves and echoes as well as new sizes. Only skip the last size:
// returning to an earlier size is normal when the user reverses a drag.
static bool same_editor_size(Host *h, unsigned width, unsigned height) {
  bool seen = h->notified_width == width && h->notified_height == height;
  h->notified_width = width;
  h->notified_height = height;
  return seen;
}

// One pass of the editor: its events and the backend's idle work. Losing
// the display here costs the editor and nothing else.
static void pump_editor_once(Host *h, bool idle) {
  if (sigsetjmp(x_escape, 0)) {
    x_escape_armed = 0;
    host_editor_lost(h);
    return;
  }
  x_escape_armed = 1;
  unsigned frame_width = 0, frame_height = 0, plugin_width = 0, plugin_height = 0;
  bool frame_resized = false, plugin_resized = false;
  while (XPending(h->display)) {
    XEvent event;
    XNextEvent(h->display, &event);
    if (event.type == ClientMessage &&
        (Atom)event.xclient.data.l[0] == h->close_message) {
      x_escape_armed = 0;
      close_ui(h);
      return;
    }
    // Whether the frame holds the keyboard focus. The plugin is told, since
    // one running under Wine will not take the mouse until it knows.
    if ((event.type == FocusIn || event.type == FocusOut) &&
        event.xfocus.window == h->window && h->backend->editor_focus)
      h->backend->editor_focus(h, event.type == FocusIn);
    // A plugin that builds its window after the editor opens is found when
    // that window appears.
    if (!h->child && (event.type == MapNotify || event.type == CreateNotify))
      fit_to_child(h);
    if (event.type == PropertyNotify && event.xproperty.window == h->child &&
        event.xproperty.atom == XA_WM_NORMAL_HINTS)
      update_editor_hints(h);
    // The window moved. Where a plugin thinks it is decides where it thinks
    // the pointer is, so it is taught the new place the only way it listens.
    if (event.type == ConfigureNotify && event.xconfigure.window == h->window &&
        (event.xconfigure.x != h->frame_x || event.xconfigure.y != h->frame_y)) {
      h->frame_x = event.xconfigure.x;
      h->frame_y = event.xconfigure.y;
      if (h->backend->editor_coordinate_nudge)
        h->settle_ticks = 9;
    }
    // Sizes are noted here and settled once the queue is empty. Dragging a
    // corner fills the queue with them, and telling a plugin about every
    // one, each a call across to its own process, leaves it a size behind
    // the hand. It hears the last one instead, which is the one that counts.
    if (event.type == ConfigureNotify && h->child) {
      const XConfigureEvent *c = &event.xconfigure;
      if (c->window == h->window && c->width > 0 && c->height > 0) {
        frame_width = (unsigned)c->width;
        frame_height = (unsigned)c->height;
        frame_resized = true;
      } else if (c->window == h->child && c->width > 0 && c->height > 0) {
        plugin_width = (unsigned)c->width;
        plugin_height = (unsigned)c->height;
        plugin_resized = true;
      }
    }
  }
  if (h->child) {
    XWindowAttributes attributes;
    // The frame follows the plugin only when the plugin resized its own
    // window: a size the frame gave it is an echo, and while the user has
    // the corner the frame belongs to the user.
    if (plugin_resized && !frame_resized && !dragging(h) &&
        (plugin_width != h->child_width || plugin_height != h->child_height) &&
        XGetWindowAttributes(h->display, h->window, &attributes) &&
        ((unsigned)attributes.width != plugin_width ||
         (unsigned)attributes.height != plugin_height)) {
      h->self_width = plugin_width;
      h->self_height = plugin_height;
      update_editor_hints(h);
      XResizeWindow(h->display, h->window, plugin_width, plugin_height);
    }
    if (frame_resized) {
      bool user_size = frame_width != h->self_width || frame_height != h->self_height;
      if (user_size && h->editor_resizable)
        clock_gettime(CLOCK_MONOTONIC, &h->dragged_at);
      if (user_size) {
        unsigned width = frame_width, height = frame_height;
        if (width < h->editor_min_width) width = h->editor_min_width;
        if (height < h->editor_min_height) height = h->editor_min_height;
        if (width > h->editor_max_width) width = h->editor_max_width;
        if (height > h->editor_max_height) height = h->editor_max_height;
        if (h->editor_resizable && h->backend->editor_constrain)
          h->backend->editor_constrain(h, &width, &height);
        if (width > 0 && height > 0 && width <= 16384 && height <= 16384 &&
            (width != frame_width || height != frame_height)) {
          frame_width = h->self_width = width;
          frame_height = h->self_height = height;
          XResizeWindow(h->display, h->window, width, height);
        }
      }
      // Let the plugin lay out before enlarging its embedding window, which
      // otherwise exposes pixels the plugin has not drawn yet.
      if (h->backend->editor_resized &&
          !same_editor_size(h, frame_width, frame_height))
        h->backend->editor_resized(h, frame_width, frame_height);
      if (XGetWindowAttributes(h->display, h->child, &attributes) &&
          ((unsigned)attributes.width != frame_width ||
           (unsigned)attributes.height != frame_height)) {
        XResizeWindow(h->display, h->child, frame_width, frame_height);
      }
      h->child_width = frame_width;
      h->child_height = frame_height;
    }
  }
  // A window that has just opened or moved has to change size once for a
  // plugin bridged from Windows to find out where it is; until it does, its
  // clicks land as far from the pointer as the window is from the corner of
  // the screen. A pixel smaller, then back a moment later, teaches it. The
  // two halves are ticks apart so the plugin has laid itself out in between,
  // and it costs a plugin that needed none of this one relayout.
  if (idle && h->settle_ticks > 0 && --h->settle_ticks == 0) {
    if (h->settle_height > 0) {
      h->self_width = h->settle_width;
      h->self_height = h->settle_height;
      if (!h->editor_resizable)
        update_editor_hints(h);
      XResizeWindow(h->display, h->window, h->settle_width, h->settle_height);
      XFlush(h->display);
      h->settle_height = 0;
    } else if (h->child) {
      XWindowAttributes attributes;
      if (XGetWindowAttributes(h->display, h->window, &attributes) &&
          attributes.width > 1 && attributes.height > 1) {
        h->settle_width = (unsigned)attributes.width;
        h->settle_height = (unsigned)attributes.height;
        h->self_width = h->settle_width;
        h->self_height = h->settle_height - 1;
        if (!h->editor_resizable)
          update_editor_hints(h);
        XResizeWindow(h->display, h->window, h->settle_width, h->settle_height - 1);
        XFlush(h->display);
        h->settle_ticks = 4;   // put it back once the plugin has caught up
      }
    }
  }
  bool finished = idle && h->backend->editor_idle && h->backend->editor_idle(h);
  XFlush(h->display);
  x_escape_armed = 0;
  if (finished)
    close_ui(h);
}

// Xlib reads the connection dry whenever it waits for a reply, so a round
// trip made after the queue was emptied can leave events in Xlib's own queue
// with nothing left on the socket. The editor's source only wakes on the
// socket, so those events would wait for an unrelated one or for the tick,
// and the plugin would hear its new size late or not at all. A pass ends
// with the queue empty instead.
static void pump_editor(Host *h, bool idle) {
  pump_editor_once(h, idle);
  for (unsigned pass = 0; pass < 8 && h->editor_open && h->display &&
                          XEventsQueued(h->display, QueuedAlready) > 0;
       ++pass)
    pump_editor_once(h, false);
}

// The same for X work done outside the pump: a plugin's resize request and a
// backend's own callbacks both wait for replies. A caller that is already
// inside the escape leaves the queue to the section that armed it.
static void settle_editor_queue(Host *h) {
  if (!x_escape_armed && h->editor_open && h->display &&
      XEventsQueued(h->display, QueuedAlready) > 0)
    pump_editor(h, false);
}

static void editor_events(void *data, int fd, uint32_t mask) {
  Host *h = data;
  if (mask & (SPA_IO_HUP | SPA_IO_ERR)) {
    host_editor_lost(h);
    return;
  }
  if (h->editor_open)
    pump_editor(h, false);
}

// --- the command pipe -------------------------------------------------------

static bool set_control(Host *h, const char *symbol, float value) {
  if (!isfinite(value))
    return false;
  for (uint32_t i = 0; i < h->control_count; ++i) {
    Control *c = &h->controls[i];
    if (!c->output && !strcmp(c->symbol, symbol)) {
      atomic_store(&c->desired, fminf(c->maximum, fmaxf(c->minimum, value)));
      return true;
    }
  }
  return false;
}

static void command(Host *h, char *line) {
  char symbol[MAX_SYMBOL + 1], extra;
  float value;
  if (!strcmp(line, "show")) {
    // Two different disappointments, and the user can act on only one. A
    // backend may learn only while opening that there is no editor, so the
    // answer is chosen after the attempt.
    bool opened = h->has_editor && open_ui(h);
    puts(opened          ? "ui opened"
         : !h->has_editor ? "ui unavailable: this plugin has no editor the "
                            "host can show"
                          : "ui unavailable: no X display; the daemon has "
                            "none from your desktop session");
  }
  else if (!strcmp(line, "hide"))
    close_ui(h);
  else if (!strcmp(line, "quit"))
    pw_main_loop_quit(h->loop);
  else if (sscanf(line, "set %255s %f %c", symbol, &value, &extra) == 2) {
    if (!set_control(h, symbol, value))
      fprintf(stderr, "invalid control: %s\n", symbol);
  } else if (h->backend->command)
    h->backend->command(h, line);
}

static void read_commands(void *data, int fd, uint32_t mask) {
  Host *h = data;
  ssize_t count =
      read(fd, h->input + h->input_size, sizeof(h->input) - h->input_size - 1);
  if (count == 0 || (count < 0 && errno != EAGAIN)) {
    pw_main_loop_quit(h->loop);
    return;
  }
  if (count < 0)
    return;
  h->input_size += (size_t)count;
  h->input[h->input_size] = 0;
  char *start = h->input, *end;
  while ((end = strchr(start, '\n'))) {
    *end = 0;
    command(h, start);
    start = end + 1;
  }
  h->input_size -= (size_t)(start - h->input);
  memmove(h->input, start, h->input_size);
  if (h->input_size == sizeof(h->input) - 1) {
    h->exit_code = 1;
    pw_main_loop_quit(h->loop);
  }
}

// --- the main-thread tick ---------------------------------------------------

// Tell the daemon the plugin's latency once audio runs, and again whenever
// the figure changes. A plugin may settle its latency on its first cycles,
// so nothing is said before one has completed.
static bool report_latency(Host *h) {
  if ((!h->backend->latency && !h->own_block) ||
      atomic_load(&h->audio_left) == 0)
    return false;
  uint32_t latency = h->backend->latency ? h->backend->latency(h) : UINT32_MAX;
  // On its own block the insert is late by its delay as well as by whatever
  // the plugin reports; a plugin with no figure counts as none.
  if (h->own_block) {
    uint32_t delay = atomic_load(&h->block.delay);
    if (latency == UINT32_MAX) latency = 0;
    latency = latency < UINT32_MAX - 1 - delay ? latency + delay : UINT32_MAX - 1;
  }
  if (h->latency_reported && latency == h->reported_latency)
    return false;
  printf("latency %u %u\n", latency, h->rate);
  h->reported_latency = latency;
  h->latency_reported = true;
  return true;
}

// Either non-audio thread can report a failure. Keep the first explanation,
// including one a backend already supplied through host_fail.
static void explain_audio_failure(Host *h) {
  if (atomic_exchange(&h->failure_explained, true)) return;
  unsigned invalid = atomic_load(&h->invalid_audio);
  const char *reason = atomic_load(&h->process_failed)
      ? "the plugin could not process audio"
      : invalid == AUDIO_INVALID_INPUT
      ? "non-finite audio at the plugin input"
      : invalid == AUDIO_INVALID_OUTPUT
      ? "non-finite audio from the plugin output"
      : "unsupported audio quantum or sample-rate change";
  fprintf(stderr, "%s; restart the chain\n", reason);
}

// With OPENXLR_HOST_TRACE set, the own block says once a second, when either
// count moved, how many cycles played silence for a block that came late and
// how many blocks the worker dropped as too late to be heard.
static void trace_own_block(Host *h) {
  OwnBlock *b = &h->block;
  uint64_t shorts = atomic_load(&b->short_cycles);
  uint64_t dropped = atomic_load(&b->dropped_blocks);
  if (shorts == b->reported_short && dropped == b->reported_dropped) return;
  b->reported_short = shorts;
  b->reported_dropped = dropped;
  if (getenv("OPENXLR_HOST_TRACE"))
    fprintf(stderr,
            "trace: own block: %llu short cycles, %llu dropped blocks, "
            "delay %u frames\n",
            (unsigned long long)shorts, (unsigned long long)dropped,
            atomic_load(&b->delay));
}

// --- the measured delay -----------------------------------------------------

// Every plugin's node measures the delay it really adds (measure.c), beside
// the figure the plugin declares. The built-in stages report their own.
static bool measures_latency(const Backend *backend) {
  return backend == &lv2_backend || backend == &clap_backend ||
         backend == &vst3_backend;
}

// The figure goes out as a meter of the host's own. Its name has a dot,
// which no LV2 symbol can, and CLAP and VST3 parameters are named by number,
// so it cannot be mistaken for one of the plugin's controls. It says nothing
// until there is a figure, and a build that cannot measure leaves it out.
static void measure_attach(Host *h) {
  if (!latency_meter_new) return;
  h->measured = host_add_control(h, "host.measured_latency", 0, 1e9f, 0, true, NULL);
  if (!h->measured) return;
  atomic_store(&h->measured->observed, NAN);
  h->measure = latency_meter_new(h->rate);
}

static void measure_start(Host *h) {
  if (measures_latency(h->backend)) measure_attach(h);
}

static void measure_tick(Host *h) {
  if (h->measure && latency_meter_update(h->measure))
    atomic_store(&h->measured->observed,
                 (float)latency_meter_samples(h->measure));
}

static void measure_free(Host *h) {
  if (h->measure) latency_meter_free(h->measure);
  h->measure = NULL;
}

static void tick(void *data, uint64_t expirations) {
  Host *h = data;
  // Editor progress is separate from audio progress. A blocked editor must
  // not cause the supervisor to tear down a working audio instance.
  if (++h->heartbeat_ticks == 30) {
    puts("ui-heartbeat");
    h->heartbeat_ticks = 0;
  }
  if (atomic_load(&h->audio_error)) {
    explain_audio_failure(h);
    h->exit_code = 1;
    pw_main_loop_quit(h->loop);
    return;
  }
  measure_tick(h);
  for (uint32_t i = 0; i < h->control_count; ++i) {
    Control *c = &h->controls[i];
    if (c->output) {
      float value = atomic_load(&c->observed);
      if (isfinite(value) && value != c->reported) {
        printf("meter %s %.9g\n", c->symbol, value);
        c->reported = value;
      }
    } else if (atomic_exchange(&c->moved, false))
      printf("control %s %.9g\n", c->symbol, atomic_load(&c->desired));
  }
  if (h->backend->main_thread)
    h->backend->main_thread(h);
  report_latency(h);
  if (h->own_block && h->heartbeat_ticks == 0)
    trace_own_block(h);
  if (h->editor_open)
    pump_editor(h, true);
}

static void stop(void *data, int signal) {
  pw_main_loop_quit(((Host *)data)->loop);
}

// How many consecutive polls one plugin callback may span before it counts
// as stuck. At a poll a second that is several seconds inside the plugin,
// on top of which the daemon waits out its own patience before it replaces
// the process: long enough that no working plugin is ever mistaken for one.
enum { AUDIO_STALL_POLLS = 6 };

static void *monitor_audio(void *data) {
  Host *h = data;
  uint64_t last_entered = 0;
  unsigned outstanding = 0;
  bool said = false;
  while (!atomic_load(&h->monitor_stop)) {
    // The redirected input pipe belongs to the daemon process, unlike
    // PDEATHSIG, which follows the short-lived .NET thread that spawned us.
    // Observe HUP without consuming commands; also works with a stuck UI.
    struct pollfd input = {STDIN_FILENO, POLLHUP | POLLERR, 0};
    if (poll(&input, 1, 1000) > 0 && (input.revents & (POLLHUP | POLLERR)))
      _exit(0);
    // A failed callback has returned, but its instance is silent. Do not
    // keep it looking healthy if an editor blocks the tick that exits it.
    if (atomic_load(&h->audio_error)) {
      explain_audio_failure(h);
      continue;
    }
    // The beat says the audio is moving, not merely that the process is
    // alive. A plugin that blocks inside its own process callback leaves
    // this thread and the editor loop untouched, so beating regardless
    // would leave the daemon carrying an instance that passes no audio and
    // never gets replaced. A node with no call outstanding is idle or
    // suspended on purpose and keeps beating.
    if (host_audio_stuck(atomic_load(&h->audio_entered),
                         atomic_load(&h->audio_left), &last_entered,
                         &outstanding, AUDIO_STALL_POLLS)) {
      if (!said) {
        fprintf(stderr,
                "the plugin has been inside one audio callback for %d s; "
                "restart the chain\n",
                AUDIO_STALL_POLLS);
        said = true;
      }
      continue;
    }
    said = false;
    puts("heartbeat");
  }
  return NULL;
}

// --- main -------------------------------------------------------------------

// The built-in stages live in their own objects, which only the helper
// links. Tests that compile this file into themselves leave them out, and
// the weak references read as unknown backends instead of failing the link.
extern const Backend sound_check_backend __attribute__((weak));
extern const Backend eq_backend __attribute__((weak));
extern const Backend eq_bands_backend __attribute__((weak));
extern const Backend duck_backend __attribute__((weak));
extern const Backend level_backend __attribute__((weak));
extern const Backend player_backend __attribute__((weak));
// The noise removal engines this build carries (denoise.c), likewise absent
// from the tests that leave the stages out.
extern int denoise_print_engines(void) __attribute__((weak));
// The soundboard's decoders and its one-shot probe (player.c).
extern int player_print_formats(void) __attribute__((weak));
extern int player_probe_main(const char *path) __attribute__((weak));

static const Backend *backend_named(const char *name) {
  if (!strcmp(name, "soundcheck"))
    return &sound_check_backend;
  if (!strcmp(name, "eq"))
    return &eq_backend;
  if (!strcmp(name, "eq-bands"))
    return &eq_bands_backend;
  if (!strcmp(name, "duck"))
    return &duck_backend;
  if (!strcmp(name, "level"))
    return &level_backend;
  if (!strcmp(name, "player"))
    return &player_backend;
  if (!strcmp(name, "lv2"))
    return &lv2_backend;
  if (!strcmp(name, "clap"))
    return &clap_backend;
  if (!strcmp(name, "vst3"))
    return &vst3_backend;
  return NULL;
}

static bool configure_editor_environment(void) {
  // LSP's OpenGL editor can stop repainting after continuous large resizes.
  // Its software renderer stays responsive. Only LSP reads this setting;
  // keep an explicit choice supplied by the user.
  return setenv("LSP_WS_LIB_GLXSURFACE", "0", 0) == 0;
}

int main(int argc, char **argv) {
  if (argc == 3 && !strcmp(argv[1], "scan-clap"))
    return clap_scan(argv[2]);
  if (argc == 3 && !strcmp(argv[1], "scan-vst3"))
    return vst3_scan(argv[2]);
  if (argc == 2 && !strcmp(argv[1], "engines") && denoise_print_engines)
    return denoise_print_engines();
  if (argc == 2 && !strcmp(argv[1], "formats") && player_print_formats)
    return player_print_formats();
  if (argc == 3 && !strcmp(argv[1], "probe") && player_probe_main)
    return player_probe_main(argv[2]);
  // `--own-block` before a plugin format runs the plugin on its own block.
  bool own_block = argc >= 2 && !strcmp(argv[1], "--own-block");
  if (own_block) {
    argv[1] = argv[0];
    ++argv;
    --argc;
  }
  const Backend *backend = argc >= 2 ? backend_named(argv[1]) : NULL;
  int first = backend ? 2 + backend->argument_count : 0;  // NODE
  if (!backend || argc < first + 3 || (own_block && !own_block_allowed(backend))) {
    fputs("usage: openxlr-lv2-host [--own-block] lv2 URI NODE CHANNELS RATE "
          "[SYMBOL=VALUE ...]\n"
          "       openxlr-lv2-host [--own-block] clap FILE ID NODE CHANNELS RATE "
          "[SYMBOL=VALUE ...]\n"
          "       openxlr-lv2-host [--own-block] vst3 BUNDLE CLASS-ID NODE "
          "CHANNELS RATE [SYMBOL=VALUE ...]\n"
          "       openxlr-lv2-host soundcheck NODE 1 RATE\n"
          "       openxlr-lv2-host eq NODE 1 RATE [SYMBOL=VALUE ...]\n"
          "       openxlr-lv2-host eq-bands COUNT NODE 1 RATE [SYMBOL=VALUE ...]\n"
          "       openxlr-lv2-host duck NODE 2 RATE [SYMBOL=VALUE ...]\n"
          "       openxlr-lv2-host level NODE 1 RATE\n"
          "       openxlr-lv2-host player NODE 2 RATE [SYMBOL=VALUE ...]\n"
          "       openxlr-lv2-host scan-clap FILE\n"
          "       openxlr-lv2-host scan-vst3 BUNDLE\n"
          "       openxlr-lv2-host engines\n"
          "       openxlr-lv2-host formats\n"
          "       openxlr-lv2-host probe PATH\n",
          stderr);
    return 2;
  }
  int channels = atoi(argv[first + 1]);
  static Host h;  // large: the control table lives here, not on the stack
  h.backend = backend;
  h.node_name = argv[first];
  h.rate = (uint32_t)strtoul(argv[first + 2], NULL, 10);
  h.channels = (unsigned)channels;
  h.main_thread = pthread_self();
  h.own_block = own_block;
  if (channels < 1 || channels > MAX_CHANNELS || h.rate < 8000 ||
      h.rate > 384000)
    return 2;
  // Set the renderer before loading plugin code or starting audio threads.
  if (!configure_editor_environment()) {
    perror("editor renderer environment");
    return 1;
  }
  setvbuf(stdout, NULL, _IOLBF, 0);
  XSetErrorHandler(report_x_error);
  XSetIOErrorHandler(lost_x_connection);
  h.silence = calloc(MAX_FRAMES, sizeof(float));
  h.scratch = calloc(MAX_FRAMES, sizeof(float));
  for (unsigned t = 0; t < MAX_TAPS; ++t)
    if (!(h.tap_scratch[t] = calloc(MAX_FRAMES, sizeof(float))))
      return 1;
  if (!h.silence || !h.scratch)
    return 1;
  if (h.own_block && !own_block_init(&h))
    return 1;

  pw_init(NULL, NULL);
  h.loop = pw_main_loop_new(NULL);
  if (!h.loop)
    return 1;
  char rate[32], description[64];
  snprintf(rate, sizeof(rate), "1/%u", h.rate);
  snprintf(description, sizeof(description), "OpenXLR %s", backend->name);
  h.filter = pw_filter_new_simple(
      pw_main_loop_get_loop(h.loop), h.node_name,
      pw_properties_new(PW_KEY_NODE_NAME, h.node_name, PW_KEY_NODE_DESCRIPTION,
                        description, PW_KEY_MEDIA_TYPE, "Audio",
                        PW_KEY_MEDIA_CATEGORY, "Filter", PW_KEY_MEDIA_ROLE,
                        "DSP", PW_KEY_NODE_RATE, rate, "node.lock-rate", "true",
                        "node.autoconnect", "false", NULL),
      &filter_events, &h);
  if (!h.filter) {
    h.exit_code = 1;
    goto cleanup;
  }
  if (!add_audio_ports(&h)) {
    h.exit_code = 1;
    goto cleanup;
  }

  if (!backend->load(&h, argv + 2)) {
    h.exit_code = 1;
    goto cleanup;
  }
  measure_start(&h);
  for (int i = first + 3; i < argc; ++i) {
    char *equals = strchr(argv[i], '=');
    if (!equals) {
      h.exit_code = 2;
      goto cleanup;
    }
    *equals = 0;
    char *end;
    float value = strtof(equals + 1, &end);
    if (*end || !set_control(&h, argv[i], value)) {
      h.exit_code = 2;
      goto cleanup;
    }
  }
  if (!backend->activate(&h)) {
    h.exit_code = 1;
    goto cleanup;
  }
  if (pw_filter_connect(h.filter, PW_FILTER_FLAG_RT_PROCESS, NULL, 0) < 0) {
    h.exit_code = 1;
    goto deactivate;
  }
  if (h.own_block) {
    struct pw_core *core = pw_filter_get_core(h.filter);
    struct pw_context *context = core ? pw_core_get_context(core) : NULL;
    if (!own_block_start(&h, context ? pw_context_get_object(
                                           context, SPA_TYPE_INTERFACE_ThreadUtils)
                                     : NULL)) {
      fputs("could not start the plugin's own block\n", stderr);
      h.exit_code = 1;
      goto disconnect;
    }
  }
  fcntl(STDIN_FILENO, F_SETFL, O_NONBLOCK);
  struct pw_loop *loop = pw_main_loop_get_loop(h.loop);
  pw_loop_add_io(loop, STDIN_FILENO, SPA_IO_IN | SPA_IO_HUP, false,
                 read_commands, &h);
  h.timer = pw_loop_add_timer(loop, tick, &h);
  struct timespec interval = {0, 33333333};
  pw_loop_update_timer(loop, h.timer, &interval, &interval, false);
  pw_loop_add_signal(loop, SIGTERM, stop, &h);
  pw_loop_add_signal(loop, SIGINT, stop, &h);
  pthread_t monitor;
  if (pthread_create(&monitor, NULL, monitor_audio, &h)) {
    h.exit_code = 1;
    goto deactivate;
  }
  pw_main_loop_run(h.loop);
  atomic_store(&h.monitor_stop, true);
  pthread_join(monitor, NULL);
disconnect:
  pw_filter_disconnect(h.filter);
deactivate:
  // The worker calls the plugin, so it ends before the plugin is switched off.
  own_block_stop(&h);
  backend->deactivate(&h);
cleanup:
  close_ui(&h);
  if (h.impl)
    backend->unload(&h);
  if (h.filter)
    pw_filter_destroy(h.filter);
  if (h.loop)
    pw_main_loop_destroy(h.loop);
  free(h.silence);
  free(h.scratch);
  for (unsigned t = 0; t < MAX_TAPS; ++t)
    free(h.tap_scratch[t]);
  own_block_free(&h);
  measure_free(&h);
  return h.exit_code;
}
