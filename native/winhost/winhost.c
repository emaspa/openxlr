// SPDX-License-Identifier: GPL-3.0-only
// The Windows plugin host: one Windows plugin, loaded under Wine, for the
// Linux plugin host that owns its PipeWire node (wine.c). Built with mingw
// and linked statically, so it is one file beside the Linux helper.
//
// It keeps everything a plugin's own interface needs on this side, next to
// the plugin: the CLAP host structure, the port layout, the restart a
// plugin asks for. Only what the Linux side's backend interface carries
// crosses, in the form wire.h describes: load, activate, one block of
// audio, the latency, deactivate, quit.
//
// Three threads. The main thread serves the control requests and the
// plugin's main-thread callbacks, and pumps the Windows messages a plugin
// may post to itself. A reader thread waits on the request FIFO and hands
// each request to the main thread. The audio thread waits on standard
// input, one byte per block, and calls the plugin's audio functions and
// nothing else: under Wine that read and the write that answers it are
// plain system calls in this thread, with no call to wineserver.
//
// Usage: openxlr-winhost.exe DIRECTORY
// DIRECTORY is a Unix path. It holds the shared audio file and the two
// control FIFOs under the names wire.h gives them.
#include <clap/clap.h>
#include <stdatomic.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include <windows.h>
#include <ole2.h>
#include <shellapi.h>

#include "wire.h"

enum {
  // A plugin may declare more audio ports than the one pair a chain insert
  // uses. Every declared port still has to be handed a buffer, so these
  // bound what can be carried, as in the Linux CLAP backend.
  MAX_PORTS = 8,
  MAX_PORT_CHANNELS = 8,
  // How often the main thread looks at what the plugin asked for, the same
  // as the Linux host's own tick.
  TICK_MS = 33
};

// Where an in-place restart stands: the main thread moves NONE to HOLD and
// HELD to NONE, the audio thread HOLD to HELD.
enum { RESTART_NONE, RESTART_HOLD, RESTART_HELD };

static WireShared *shared;
static HANDLE wake_in, wake_out;      // the audio thread's pipe each way
static HANDLE request_in, reply_out;  // the control FIFOs
static DWORD main_thread_id, audio_thread_id;
static char audio_thread_name[WIRE_THREAD_NAME];
static bool greeted;  // main thread: the hello was answered with a yes
static HANDLE audio_named;  // the audio thread has taken its name
static HANDLE tick_now;     // the plugin asked for its main-thread callback
// What stopped the start, said in the reply to the hello.
static char start_failure[WIRE_TEXT];

// The request the reader thread holds out to the main thread.
static WireRequest request;
static HANDLE request_ready, request_taken;
static _Atomic bool channel_closed;

// The plugin. Everything here is the main thread's unless it is atomic.
static const clap_plugin_entry_t *entry;
static const clap_plugin_t *plugin;
static const clap_plugin_latency_t *plugin_latency;
static uint32_t chain_channels;
static bool loaded;  // the plugin initialised and its ports fit the chain
static bool activated;
static uint32_t active_rate, active_min, active_max;
// What the audio thread reads: whether the plugin may be called at all, and
// the largest block it was activated for.
static _Atomic bool active;
static _Atomic uint32_t block_limit;
static _Atomic bool processing;  // start_processing has run, on the audio thread
static _Atomic bool callback_requested, restart_requested, latency_changed;
static _Atomic int restart;
static _Atomic unsigned dropped_logs;
static int64_t steady;  // audio thread

// Every audio port the plugin declares. The main ports are the shared
// file's own buffers, which never move, so the arrays are laid out once at
// load. The rest read silence and write into a buffer nothing listens to.
static clap_audio_buffer_t in_buses[MAX_PORTS], out_buses[MAX_PORTS];
static float *in_channels[MAX_PORTS][MAX_PORT_CHANNELS];
static float *out_channels[MAX_PORTS][MAX_PORT_CHANNELS];
static uint32_t in_bus_count, out_bus_count;
static float quiet[WIRE_MAX_FRAMES], discard[WIRE_MAX_FRAMES];

// --- paths ------------------------------------------------------------------

// A Unix path as Windows names it. Wine's own conversion answers with a
// drive letter where one maps the path and with the \\?\unix form where
// none does; that form is built here when the conversion is missing.
static WCHAR *windows_path(const char *unix_path) {
  typedef WCHAR *(CDECL * ToDos)(const char *);
  ToDos to_dos = (ToDos)(void *)GetProcAddress(
      GetModuleHandleW(L"kernel32.dll"), "wine_get_dos_file_name");
  WCHAR *converted = to_dos ? to_dos(unix_path) : NULL;
  if (converted)
    return converted;
  static const WCHAR prefix[] = L"\\\\?\\unix";
  const int prefix_length = (int)(sizeof(prefix) / sizeof(WCHAR)) - 1;
  int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, unix_path,
                                   -1, NULL, 0);
  if (length <= 0)
    return NULL;
  WCHAR *path = HeapAlloc(GetProcessHeap(), 0,
                          (size_t)(prefix_length + length) * sizeof(WCHAR));
  if (!path)
    return NULL;
  memcpy(path, prefix, (size_t)prefix_length * sizeof(WCHAR));
  MultiByteToWideChar(CP_UTF8, 0, unix_path, -1, path + prefix_length, length);
  for (WCHAR *p = path + prefix_length; *p; ++p)
    if (*p == L'/')
      *p = L'\\';
  return path;
}

static HANDLE open_in(const char *directory, const char *name, DWORD access) {
  char unix_path[WIRE_PATH];
  int length = snprintf(unix_path, sizeof(unix_path), "%s/%s", directory, name);
  if (length < 0 || (size_t)length >= sizeof(unix_path))
    return INVALID_HANDLE_VALUE;
  WCHAR *path = windows_path(unix_path);
  if (!path)
    return INVALID_HANDLE_VALUE;
  HANDLE file = CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE,
                            NULL, OPEN_EXISTING, 0, NULL);
  HeapFree(GetProcessHeap(), 0, path);
  return file;
}

// --- the pipes --------------------------------------------------------------

static bool read_all(HANDLE from, void *buffer, DWORD size) {
  DWORD done = 0;
  while (done < size) {
    DWORD count = 0;
    if (!ReadFile(from, (char *)buffer + done, size - done, &count, NULL) ||
        count == 0)
      return false;
    done += count;
  }
  return true;
}

static bool write_all(HANDLE to, const void *buffer, DWORD size) {
  DWORD done = 0;
  while (done < size) {
    DWORD count = 0;
    if (!WriteFile(to, (const char *)buffer + done, size - done, &count,
                   NULL) ||
        count == 0)
      return false;
    done += count;
  }
  return true;
}

// --- what the plugin may ask of its host ------------------------------------

static bool on_audio_thread(void) {
  return GetCurrentThreadId() == audio_thread_id;
}

// A log line goes to standard error, which the Linux side passes on. That
// is a write to a pipe, so a line from the audio thread is counted and
// dropped instead; the main thread says how many were.
static void host_log(const clap_host_t *host, clap_log_severity severity,
                     const char *message) {
  if (on_audio_thread()) {
    atomic_fetch_add(&dropped_logs, 1);
    return;
  }
  fprintf(stderr, "plugin: %s\n", message ? message : "");
}

static const clap_host_log_t log_extension = {host_log};

static bool is_main_thread(const clap_host_t *host) {
  return GetCurrentThreadId() == main_thread_id;
}

static bool is_audio_thread(const clap_host_t *host) {
  return on_audio_thread();
}

static const clap_host_thread_check_t thread_check_extension = {
    is_main_thread, is_audio_thread};

// CLAP lets a plugin change its latency only while it is being activated;
// the figure is read again on the next tick, not inside that call.
static void host_latency_changed(const clap_host_t *host) {
  atomic_store(&latency_changed, true);
}

static const clap_host_latency_t latency_extension = {host_latency_changed};

static bool ports_rescan_supported(const clap_host_t *host, uint32_t flag) {
  return false;
}

static void ports_rescan(const clap_host_t *host, uint32_t flags) {
}

static const clap_host_audio_ports_t audio_ports_extension = {
    ports_rescan_supported, ports_rescan};

static const void *host_get_extension(const clap_host_t *host,
                                      const char *id) {
  if (!strcmp(id, CLAP_EXT_LATENCY))
    return &latency_extension;
  if (!strcmp(id, CLAP_EXT_LOG))
    return &log_extension;
  if (!strcmp(id, CLAP_EXT_THREAD_CHECK))
    return &thread_check_extension;
  if (!strcmp(id, CLAP_EXT_AUDIO_PORTS))
    return &audio_ports_extension;
  return NULL;
}

// A change only a fresh activation can make, a new latency most often. It
// is carried out in place by the main thread (restart_step), so the plugin
// keeps its state. This may be called from any thread.
static void host_request_restart(const clap_host_t *host) {
  atomic_store(&restart_requested, true);
}

static void host_request_process(const clap_host_t *host) {}

// The main thread looks on its next tick. A request from any thread but
// the audio thread wakes it at once; waking it from the audio thread would
// be a call to wineserver inside a block.
static void host_request_callback(const clap_host_t *host) {
  atomic_store(&callback_requested, true);
  if (!on_audio_thread())
    SetEvent(tick_now);
}

static const clap_host_t host = {
    .clap_version = CLAP_VERSION_INIT,
    .host_data = NULL,
    .name = "OpenXLR",
    .vendor = "OpenXLR",
    .url = "https://github.com/emaspa/openxlr",
    .version = "1",
    .get_extension = host_get_extension,
    .request_restart = host_request_restart,
    .request_process = host_request_process,
    .request_callback = host_request_callback,
};

// This step carries no parameters: the plugin is sent no events and what it
// sends back is taken and dropped.
static uint32_t events_size(const clap_input_events_t *list) {
  return 0;
}

static const clap_event_header_t *events_get(const clap_input_events_t *list,
                                             uint32_t index) {
  return NULL;
}

static bool events_push(const clap_output_events_t *list,
                        const clap_event_header_t *event) {
  return true;
}

static const clap_input_events_t no_events = {NULL, events_size, events_get};
static const clap_output_events_t dropped_events = {NULL, events_push};

// --- loading ----------------------------------------------------------------

// One direction's audio ports: every one is given real memory of the right
// length, the main one the shared file's buffers. The main port is the
// first one flagged as such, or port 0 when none is, and has to be as wide
// as the chain. The same rules and the same words as the Linux CLAP backend.
static bool map_ports(const clap_plugin_audio_ports_t *ports, bool input,
                      char *why, size_t why_size) {
  const char *side = input ? "input" : "output";
  clap_audio_buffer_t *buses = input ? in_buses : out_buses;
  float *(*channels)[MAX_PORT_CHANNELS] = input ? in_channels : out_channels;
  uint32_t count = ports ? ports->count(plugin, input) : 0;
  if (count == 0 || count > MAX_PORTS) {
    snprintf(why, why_size,
             "the plugin declares %u audio %s ports; this host carries 1 to %d",
             (unsigned)count, side, MAX_PORTS);
    return false;
  }
  uint32_t main_port = 0, main_channels = 0;
  bool flagged = false;
  for (uint32_t i = 0; i < count; ++i) {
    clap_audio_port_info_t info;
    if (!ports->get(plugin, i, input, &info)) {
      snprintf(why, why_size,
               "the plugin would not describe its audio %s port %u", side,
               (unsigned)i);
      return false;
    }
    if (info.channel_count > MAX_PORT_CHANNELS) {
      snprintf(why, why_size,
               "the plugin's audio %s port %u has %u channels; this host "
               "carries %d",
               side, (unsigned)i, (unsigned)info.channel_count,
               MAX_PORT_CHANNELS);
      return false;
    }
    for (uint32_t k = 0; k < info.channel_count; ++k)
      channels[i][k] = input ? quiet : discard;
    buses[i].data32 = channels[i];
    buses[i].channel_count = info.channel_count;
    if (!flagged && ((info.flags & CLAP_AUDIO_PORT_IS_MAIN) || i == 0)) {
      flagged = (info.flags & CLAP_AUDIO_PORT_IS_MAIN) != 0;
      main_port = i;
      main_channels = info.channel_count;
    }
  }
  if (main_channels != chain_channels) {
    snprintf(why, why_size,
             "the plugin's main audio %s port has %u channels, not the "
             "chain's %u",
             side, (unsigned)main_channels, (unsigned)chain_channels);
    return false;
  }
  for (uint32_t k = 0; k < chain_channels; ++k)
    channels[main_port][k] = input ? shared->in[k] : shared->out[k];
  if (input)
    in_bus_count = count;
  else
    out_bus_count = count;
  return true;
}

// Load the module, make the plugin and read its layout. The reply's text is
// the plugin's name, or on refusal the reason.
static bool load(WireRequest *r, char *text, size_t text_size) {
  r->path[sizeof(r->path) - 1] = 0;
  r->id[sizeof(r->id) - 1] = 0;
  if (plugin || entry) {
    snprintf(text, text_size, "a plugin is already loaded");
    return false;
  }
  if (r->format != WIRE_FORMAT_CLAP) {
    snprintf(text, text_size, "this Windows plugin host carries CLAP only");
    return false;
  }
  if (r->channels < 1 || r->channels > WIRE_MAX_CHANNELS) {
    snprintf(text, text_size, "a chain of %u channels cannot be carried",
             (unsigned)r->channels);
    return false;
  }
  chain_channels = r->channels;
  WCHAR *path = windows_path(r->path);
  if (!path) {
    snprintf(text, text_size, "the module's path cannot be named on Windows");
    return false;
  }
  // The path the plugin is told is its Windows one, in UTF-8 as CLAP asks.
  static char told[WIRE_PATH * 3];
  if (!WideCharToMultiByte(CP_UTF8, 0, path, -1, told, sizeof(told), NULL,
                           NULL))
    told[0] = 0;
  HMODULE module = LoadLibraryW(path);
  DWORD error = GetLastError();
  HeapFree(GetProcessHeap(), 0, path);
  if (!module) {
    snprintf(text, text_size, "cannot load the module: Windows error %lu",
             (unsigned long)error);
    return false;
  }
  const clap_plugin_entry_t *found =
      (const clap_plugin_entry_t *)(void *)GetProcAddress(module, "clap_entry");
  if (!found || found->clap_version.major != CLAP_VERSION_MAJOR) {
    snprintf(text, text_size, "not a CLAP module this host understands");
    return false;
  }
  if (!found->init(told)) {
    snprintf(text, text_size, "the module refused to initialise");
    return false;
  }
  entry = found;
  const clap_plugin_factory_t *factory =
      entry->get_factory(CLAP_PLUGIN_FACTORY_ID);
  if (!factory) {
    snprintf(text, text_size, "the module has no plugin factory");
    return false;
  }
  plugin = factory->create_plugin(factory, &host, r->id);
  if (!plugin) {
    snprintf(text, text_size, "the module has no plugin %.200s", r->id);
    return false;
  }
  if (!plugin->init(plugin)) {
    snprintf(text, text_size, "the plugin refused to initialise");
    return false;
  }
  const clap_plugin_audio_ports_t *ports =
      plugin->get_extension(plugin, CLAP_EXT_AUDIO_PORTS);
  if (!map_ports(ports, true, text, text_size) ||
      !map_ports(ports, false, text, text_size))
    return false;
  plugin_latency = plugin->get_extension(plugin, CLAP_EXT_LATENCY);
  snprintf(text, text_size, "%s",
           plugin->desc && plugin->desc->name ? plugin->desc->name : "");
  loaded = true;
  return true;
}

// --- activation -------------------------------------------------------------

static bool activate_plugin(void) {
  if (!plugin->activate(plugin, active_rate, active_min, active_max))
    return false;
  activated = true;
  atomic_store(&latency_changed, false);
  atomic_store(&shared->latency,
               plugin_latency ? plugin_latency->get(plugin) : 0);
  return true;
}

static bool activate(const WireRequest *r, char *text, size_t text_size) {
  if (!loaded) {
    snprintf(text, text_size, "no plugin is loaded");
    return false;
  }
  if (activated) {
    snprintf(text, text_size, "the plugin is already active");
    return false;
  }
  if (r->rate < 8000 || r->rate > 384000 || r->min_frames < 1 ||
      r->min_frames > r->max_frames || r->max_frames > WIRE_MAX_FRAMES) {
    snprintf(text, text_size, "a rate of %u with blocks of %u to %u frames "
             "cannot be carried",
             (unsigned)r->rate, (unsigned)r->min_frames,
             (unsigned)r->max_frames);
    return false;
  }
  active_rate = r->rate;
  active_min = r->min_frames;
  active_max = r->max_frames;
  if (!activate_plugin()) {
    snprintf(text, text_size, "the plugin refused to activate");
    return false;
  }
  atomic_store(&block_limit, active_max);
  atomic_store(&active, true);
  return true;
}

// The Linux side sends this only once no block can be under way, and has
// the audio thread stop processing first (WIRE_WAKE_STOP). If that did not
// happen, stop_processing is called here instead, after the audio thread's
// last block, which every plugin tested tolerates.
static void deactivate(void) {
  if (!activated)
    return;
  atomic_store(&active, false);
  if (atomic_exchange(&processing, false))
    plugin->stop_processing(plugin);
  plugin->deactivate(plugin);
  activated = false;
  atomic_store(&restart_requested, false);
  atomic_store(&restart, RESTART_NONE);
}

// The restart the plugin asked for, one step per tick. First the audio
// thread is asked to let go; on its next block it stops processing and
// answers with silence from then on. Once it has, the plugin is deactivated
// and activated again with the same rate and block sizes, and its latency is
// read anew. A plugin with no blocks coming holds the request until audio
// returns, since nothing can say the audio thread is not about to call it.
static void restart_step(void) {
  int state = atomic_load(&restart);
  if (state == RESTART_NONE) {
    if (atomic_exchange(&restart_requested, false) && activated)
      atomic_store(&restart, RESTART_HOLD);
    return;
  }
  if (state != RESTART_HELD)
    return;
  plugin->deactivate(plugin);
  activated = false;
  if (!activate_plugin()) {
    // Still held, so the audio thread never calls the inactive plugin.
    // Leaving is how the Linux side learns: it replaces the instance.
    fputs("the plugin did not activate again after asking for a restart\n",
          stderr);
    TerminateProcess(GetCurrentProcess(), 1);
  }
  atomic_store(&restart_requested, false);
  atomic_store(&restart, RESTART_NONE);
  fputs("trace: the plugin asked for a restart and was activated again\n",
        stderr);
}

static void on_tick(void) {
  if (!loaded)
    return;
  restart_step();
  if (atomic_exchange(&callback_requested, false))
    plugin->on_main_thread(plugin);
  if (activated && atomic_exchange(&latency_changed, false))
    atomic_store(&shared->latency,
                 plugin_latency ? plugin_latency->get(plugin) : 0);
  unsigned dropped = atomic_exchange(&dropped_logs, 0);
  if (dropped)
    fprintf(stderr,
            "trace: %u log lines from the plugin's audio thread were dropped\n",
            dropped);
}

// --- the audio thread -------------------------------------------------------

static void silence(uint32_t frames) {
  for (uint32_t c = 0; c < WIRE_MAX_CHANNELS; ++c)
    memset(shared->out[c], 0, frames * sizeof(float));
}

// One block. The frame count is read once and checked against the fixed
// buffers and against what the plugin was activated for before anything
// uses it.
static uint32_t process_block(void) {
  uint32_t frames = shared->frames;
  uint32_t bounded = frames <= WIRE_MAX_FRAMES ? frames : WIRE_MAX_FRAMES;
  if (!atomic_load(&active) || frames == 0 ||
      frames > atomic_load(&block_limit)) {
    silence(bounded);
    return WIRE_BLOCK_REFUSED;
  }
  int state = atomic_load(&restart);
  if (state != RESTART_NONE) {
    if (state == RESTART_HOLD) {
      if (atomic_exchange(&processing, false))
        plugin->stop_processing(plugin);
      atomic_store(&restart, RESTART_HELD);
    }
    silence(frames);
    return WIRE_BLOCK_OK;
  }
  if (!atomic_load(&processing)) {
    if (!plugin->start_processing(plugin)) {
      silence(frames);
      return WIRE_BLOCK_REFUSED;
    }
    atomic_store(&processing, true);
  }
  clap_process_t process = {
      .steady_time = steady,
      .frames_count = frames,
      .transport = NULL,
      .audio_inputs = in_buses,
      .audio_outputs = out_buses,
      .audio_inputs_count = in_bus_count,
      .audio_outputs_count = out_bus_count,
      .in_events = &no_events,
      .out_events = &dropped_events,
  };
  clap_process_status status = plugin->process(plugin, &process);
  steady += frames;
  if (status == CLAP_PROCESS_ERROR) {
    silence(frames);
    return WIRE_BLOCK_REFUSED;
  }
  return WIRE_BLOCK_OK;
}

// Linux finds this thread by its name under /proc and gives it the
// scheduling of the thread that waits for it, which a Windows program
// cannot ask for itself.
static void take_name(void) {
  typedef HRESULT(WINAPI * Describe)(HANDLE, PCWSTR);
  Describe describe = (Describe)(void *)GetProcAddress(
      GetModuleHandleW(L"kernel32.dll"), "SetThreadDescription");
  WCHAR wide[WIRE_THREAD_NAME];
  if (describe && MultiByteToWideChar(CP_UTF8, 0, audio_thread_name, -1, wide,
                                      WIRE_THREAD_NAME))
    describe(GetCurrentThread(), wide);
}

static DWORD WINAPI audio_thread(void *unused) {
  audio_thread_id = GetCurrentThreadId();
  take_name();
  SetEvent(audio_named);
  for (;;) {
    char wake = 0;
    DWORD count = 0;
    if (!ReadFile(wake_in, &wake, 1, &count, NULL) || count != 1)
      break;
    if (wake == WIRE_WAKE_PROCESS)
      shared->status = process_block();
    else if (wake == WIRE_WAKE_STOP) {
      if (atomic_exchange(&processing, false))
        plugin->stop_processing(plugin);
    } else
      break;
    wake = WIRE_WAKE_DONE;
    if (!WriteFile(wake_out, &wake, 1, &count, NULL) || count != 1)
      break;
  }
  // The Linux side is gone, or said something this build does not know.
  // The main thread may be inside the plugin, so nothing is torn down.
  TerminateProcess(GetCurrentProcess(), 0);
  return 0;
}

// --- the control channel ----------------------------------------------------

static DWORD WINAPI read_requests(void *unused) {
  for (;;) {
    if (!read_all(request_in, &request, sizeof(request)))
      break;
    SetEvent(request_ready);
    WaitForSingleObject(request_taken, INFINITE);
  }
  atomic_store(&channel_closed, true);
  SetEvent(request_ready);
  return 0;
}

static bool hello(WireRequest *r, WireReply *reply) {
  reply->version = WIRE_VERSION;
  reply->shared_size = sizeof(WireShared);
  if (start_failure[0]) {
    snprintf(reply->text, sizeof(reply->text), "%s", start_failure);
    return false;
  }
  if (r->version != WIRE_VERSION) {
    snprintf(reply->text, sizeof(reply->text),
             "the Linux side speaks version %u, this side %d",
             (unsigned)r->version, WIRE_VERSION);
    return false;
  }
  if (greeted) {
    snprintf(reply->text, sizeof(reply->text), "already greeted");
    return false;
  }
  r->thread[sizeof(r->thread) - 1] = 0;
  memcpy(audio_thread_name, r->thread, sizeof(audio_thread_name));
  HANDLE thread = CreateThread(NULL, 0, audio_thread, NULL, 0, NULL);
  if (!thread) {
    snprintf(reply->text, sizeof(reply->text),
             "cannot start the audio thread");
    return false;
  }
  CloseHandle(thread);
  WaitForSingleObject(audio_named, INFINITE);
  greeted = true;
  return true;
}

// What became of one request: answered, answered and the last one, or the
// reply could not be written because the Linux side is gone.
enum { SERVED, SERVED_QUIT, SERVED_NOBODY };

static int serve(WireRequest *r) {
  WireReply reply = {.magic = WIRE_MAGIC, .type = r->type};
  bool quit = false;
  if (r->magic != WIRE_MAGIC)
    snprintf(reply.text, sizeof(reply.text), "not a request");
  else if (r->type == WIRE_HELLO)
    reply.ok = hello(r, &reply);
  else if (!greeted)
    snprintf(reply.text, sizeof(reply.text), "not greeted yet");
  else if (r->type == WIRE_LOAD)
    reply.ok = load(r, reply.text, sizeof(reply.text));
  else if (r->type == WIRE_ACTIVATE) {
    reply.ok = activate(r, reply.text, sizeof(reply.text));
    reply.latency = atomic_load(&shared->latency);
  } else if (r->type == WIRE_DEACTIVATE) {
    deactivate();
    reply.ok = true;
  } else if (r->type == WIRE_QUIT) {
    reply.ok = true;
    quit = true;
  } else
    snprintf(reply.text, sizeof(reply.text), "unknown request %u",
             (unsigned)r->type);
  if (!write_all(reply_out, &reply, sizeof(reply)))
    return SERVED_NOBODY;
  return quit ? SERVED_QUIT : SERVED;
}

// --- main -------------------------------------------------------------------

// A fault nothing handled, in the plugin or in this host. Leave at once:
// the Linux side hears the pipes close and replaces the instance, and the
// one line says why. Waiting for Wine's own debugger would only keep the
// audio silent for longer.
static LONG WINAPI crashed(EXCEPTION_POINTERS *fault) {
  char line[128];
  int length = snprintf(line, sizeof(line),
                        "the Windows plugin crashed: exception 0x%08lx at %p\n",
                        (unsigned long)fault->ExceptionRecord->ExceptionCode,
                        fault->ExceptionRecord->ExceptionAddress);
  DWORD written = 0;
  if (length > 0)
    WriteFile(GetStdHandle(STD_ERROR_HANDLE), line, (DWORD)length, &written,
              NULL);
  TerminateProcess(GetCurrentProcess(), 3);
  return EXCEPTION_EXECUTE_HANDLER;
}

// Open the control FIFOs and map the shared file. Without the FIFOs there
// is nobody to tell; any later failure is kept for the reply to the hello.
static bool start(const char *directory) {
  request_in = open_in(directory, WIRE_REQUEST_FIFO, GENERIC_READ);
  reply_out = open_in(directory, WIRE_REPLY_FIFO, GENERIC_WRITE);
  if (request_in == INVALID_HANDLE_VALUE || reply_out == INVALID_HANDLE_VALUE)
    return false;
  wake_in = GetStdHandle(STD_INPUT_HANDLE);
  wake_out = GetStdHandle(STD_OUTPUT_HANDLE);
  HANDLE file = open_in(directory, WIRE_AUDIO_FILE,
                        GENERIC_READ | GENERIC_WRITE);
  LARGE_INTEGER size = {0};
  HANDLE mapping = NULL;
  if (file == INVALID_HANDLE_VALUE || !GetFileSizeEx(file, &size) ||
      size.QuadPart != (LONGLONG)sizeof(WireShared))
    snprintf(start_failure, sizeof(start_failure),
             "the shared audio file is missing or not %u bytes long",
             (unsigned)sizeof(WireShared));
  else if (!(mapping = CreateFileMappingW(file, NULL, PAGE_READWRITE, 0, 0,
                                          NULL)) ||
           !(shared = MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0,
                                    sizeof(WireShared))))
    snprintf(start_failure, sizeof(start_failure),
             "cannot map the shared audio file: Windows error %lu",
             (unsigned long)GetLastError());
  else if (shared->magic != WIRE_MAGIC || shared->version != WIRE_VERSION ||
           shared->size != sizeof(WireShared))
    snprintf(start_failure, sizeof(start_failure),
             "the shared audio file is version %u, this side speaks %d",
             (unsigned)shared->version, WIRE_VERSION);
  else
    // Audio memory that is paged out costs a block; failing to pin it is
    // not worth refusing over.
    VirtualLock(shared, sizeof(WireShared));
  return true;
}

int main(void) {
  // No dialog for a missing library or a fault: nobody is there to see it.
  SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX |
               SEM_NOOPENFILEERRORBOX);
  SetUnhandledExceptionFilter(crashed);
  int argc = 0;
  WCHAR **argv = CommandLineToArgvW(GetCommandLineW(), &argc);
  static char directory[WIRE_PATH];
  if (!argv || argc != 2 ||
      !WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, directory,
                           sizeof(directory), NULL, NULL)) {
    fputs("usage: openxlr-winhost.exe DIRECTORY\n", stderr);
    return 2;
  }
  main_thread_id = GetCurrentThreadId();
  // Windows plugins expect their host to have done this on the thread that
  // creates them.
  OleInitialize(NULL);
  request_ready = CreateEventW(NULL, FALSE, FALSE, NULL);
  request_taken = CreateEventW(NULL, FALSE, FALSE, NULL);
  audio_named = CreateEventW(NULL, TRUE, FALSE, NULL);
  tick_now = CreateEventW(NULL, FALSE, FALSE, NULL);
  if (!request_ready || !request_taken || !audio_named || !tick_now)
    return 1;
  if (!start(directory)) {
    fputs("openxlr-winhost: cannot open the control channel\n", stderr);
    return 1;
  }
  HANDLE reader = CreateThread(NULL, 0, read_requests, NULL, 0, NULL);
  if (!reader)
    return 1;
  CloseHandle(reader);

  bool orderly = false;
  for (;;) {
    HANDLE events[] = {request_ready, tick_now};
    DWORD woke = MsgWaitForMultipleObjects(2, events, FALSE, TICK_MS,
                                           QS_ALLINPUT);
    MSG message;
    while (PeekMessageW(&message, NULL, 0, 0, PM_REMOVE)) {
      TranslateMessage(&message);
      DispatchMessageW(&message);
    }
    if (woke == WAIT_OBJECT_0) {
      if (atomic_load(&channel_closed))
        break;
      int served = serve(&request);
      SetEvent(request_taken);
      orderly = served == SERVED_QUIT;
      if (served != SERVED)
        break;
    }
    on_tick();
  }
  // Asked to quit, the Linux side has stopped the audio first, so the
  // plugin can be taken down properly. A channel that just closed says
  // nothing about the audio thread: leave without touching the plugin.
  if (!orderly)
    TerminateProcess(GetCurrentProcess(), 0);
  deactivate();
  if (plugin)
    plugin->destroy(plugin);
  if (entry)
    entry->deinit();
  // The module stays loaded: unloading buys nothing in a process that is
  // about to end, and some plugins do not survive it.
  return 0;
}
