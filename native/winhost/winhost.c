// SPDX-License-Identifier: GPL-3.0-only
// The Windows plugin host: one Windows plugin, loaded under Wine, for the
// Linux plugin host that owns its PipeWire node (wine.c). Built with mingw
// and linked statically, so it is one file beside the Linux helper.
//
// The plugin is run by the same backend the Linux helper uses for a native
// plugin of its format (clap.c), built here for Windows and called through
// its Backend table. shim.c gives that backend the services of host.h. This
// file is the other half: what crosses to the Linux side, in the form
// wire.h describes. That is the Backend's own surface and no more: load,
// activate, one block of audio, the controls, the latency, deactivate.
//
// Three threads. The main thread serves the control requests, runs the
// backend's tick and pumps the Windows messages that timers and plugins
// post. A reader thread waits on the request FIFO and hands each request to
// the main thread. The audio thread waits on standard input, one byte per
// block, and calls the backend's process and nothing else: under Wine that
// read and the write that answers it are plain system calls in this
// thread, with no call to wineserver.
//
// Usage: openxlr-winhost.exe DIRECTORY
// DIRECTORY is a Unix path. It holds the shared audio file and the two
// control FIFOs under the names wire.h gives them.
#include "winhost.h"
#include "wire.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

#include <windows.h>
#include <ole2.h>
#include <shellapi.h>

_Static_assert((int)WIRE_MAX_FRAMES == (int)MAX_FRAMES,
               "wire.h carries the host's largest block");
_Static_assert((int)WIRE_MAX_CHANNELS == (int)MAX_CHANNELS,
               "wire.h carries the host's channels");
_Static_assert((int)WIRE_MAX_CONTROLS == (int)MAX_CONTROLS,
               "wire.h carries the host's control table");

// How often the main thread runs the backend's tick, the same as the Linux
// host's own.
enum { TICK_MS = 33 };

static WireShared *shared;
static HANDLE wake_in, wake_out;      // the audio thread's pipe each way
static HANDLE request_in, reply_out;  // the control FIFOs
static char audio_thread_name[WIRE_THREAD_NAME];
static bool greeted;  // main thread: the hello was answered with a yes
static HANDLE audio_named;  // the audio thread has taken its name
// What stopped the start, said in the reply to the hello.
static char start_failure[WIRE_TEXT];

// The request the reader thread holds out to the main thread.
static WireRequest request;
static HANDLE request_ready, request_taken;
static _Atomic bool channel_closed;

// The plugin, as its backend sees it. Everything here is the main thread's
// unless it is atomic.
static Host host;  // static: the control table is large
static const Backend *backend;
static bool loaded;  // the backend loaded the plugin and its controls are listed
static bool activated;
// What the audio thread reads: whether the plugin may be called at all, and
// the largest block it was activated for.
static _Atomic bool active;
static _Atomic uint32_t block_limit;

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

// --- controls ---------------------------------------------------------------

// A value for the Linux side: the next count for this control with the
// value beside it, in one word. Either thread may be the one to write, so
// the count is taken from the word it replaces.
void winhost_publish(uint32_t index, float value) {
  if (!shared || index >= WIRE_MAX_CONTROLS)
    return;
  uint64_t old = atomic_load(&shared->values[index]), next;
  do
    next = wire_value(wire_value_count(old) + 1, value);
  while (!atomic_compare_exchange_weak(&shared->values[index], &old, next));
  atomic_fetch_add(&shared->published, 1);
}

// What the backend made of the plugin's parameters, for the Linux side to
// register under the same names.
static bool list_controls(char *text, size_t text_size) {
  for (uint32_t i = 0; i < host.control_count; ++i) {
    Control *c = &host.controls[i];
    WireControl *listed = &shared->controls[i];
    size_t length = strlen(c->symbol);
    if (length == 0 || length >= WIRE_SYMBOL) {
      snprintf(text, text_size,
               "the plugin's control %u has a name that cannot be carried",
               (unsigned)i);
      return false;
    }
    memset(listed, 0, sizeof(*listed));
    memcpy(listed->symbol, c->symbol, length);
    listed->minimum = c->minimum;
    listed->maximum = c->maximum;
    listed->initial = atomic_load(c->output ? &c->observed : &c->desired);
    listed->flags = c->output ? WIRE_CONTROL_OUTPUT : 0;
    atomic_store(&shared->values[i], wire_value(0, listed->initial));
    // The Linux side has this reading from the list, so a meter is
    // published again only once it changes.
    c->reported = listed->initial;
  }
  return true;
}

// The values the Linux side sent with this block. Each entry is copied out
// before it is looked at, and nothing in it is used unchecked. A set made
// before the Linux side had seen this control's latest published value is
// dropped: the plugin has moved the control since, and that value stands.
static void apply_sets(void) {
  uint32_t count = shared->set_count;
  if (count > WIRE_MAX_SETS)
    count = WIRE_MAX_SETS;
  for (uint32_t i = 0; i < count; ++i) {
    WireSet set = shared->sets[i];
    if (set.index >= host.control_count || !isfinite(set.value))
      continue;
    Control *c = &host.controls[set.index];
    if (c->output ||
        wire_value_count(atomic_load(&shared->values[set.index])) != set.based_on)
      continue;
    control_set_desired(c, set.value);
  }
}

// --- loading ----------------------------------------------------------------

// Set up the host the backend sees and have it load the plugin. The reply's
// text is the plugin's name, or on refusal the reason; what the backend
// itself has to say goes to standard error, as on Linux.
static bool load(WireRequest *r, WireReply *reply) {
  char *text = reply->text;
  const size_t text_size = sizeof(reply->text);
  r->path[sizeof(r->path) - 1] = 0;
  r->id[sizeof(r->id) - 1] = 0;
  if (backend) {
    snprintf(text, text_size, "a plugin is already loaded");
    return false;
  }
  if (r->format != WIRE_FORMAT_CLAP) {
    snprintf(text, text_size, "this Windows plugin host carries CLAP only");
    return false;
  }
  if (r->channels < 1 || r->channels > WIRE_MAX_CHANNELS || r->rate < 8000 ||
      r->rate > 384000) {
    snprintf(text, text_size,
             "a chain of %u channels at %u Hz cannot be carried",
             (unsigned)r->channels, (unsigned)r->rate);
    return false;
  }
  // The path the backend opens and the plugin is told is its Windows one,
  // in UTF-8 as CLAP asks.
  static char module[WIRE_PATH * 3];
  WCHAR *path = windows_path(r->path);
  bool named = path && WideCharToMultiByte(CP_UTF8, 0, path, -1, module,
                                           sizeof(module), NULL, NULL);
  if (path)
    HeapFree(GetProcessHeap(), 0, path);
  if (!named) {
    snprintf(text, text_size, "the module's path cannot be named on Windows");
    return false;
  }
  backend = &clap_backend;
  host.backend = backend;
  host.rate = r->rate;
  host.channels = r->channels;
  host.own_block = r->own_block != 0;
  char *arguments[] = {module, r->id};
  if (!backend->load(&host, arguments)) {
    snprintf(text, text_size, "the plugin did not load");
    return false;
  }
  if (!list_controls(text, text_size))
    return false;
  reply->controls = host.control_count;
  snprintf(text, text_size, "%s", host.plugin_name);
  loaded = true;
  return true;
}

// --- activation -------------------------------------------------------------

// The backend holds the figure and asks the plugin again only when the
// plugin said it changed.
static void publish_latency(void) {
  uint32_t latency = backend->latency ? backend->latency(&host) : 0;
  if (latency != UINT32_MAX)
    atomic_store(&shared->latency, latency);
}

static bool activate(char *text, size_t text_size) {
  if (!loaded) {
    snprintf(text, text_size, "no plugin is loaded");
    return false;
  }
  if (activated) {
    snprintf(text, text_size, "the plugin is already active");
    return false;
  }
  if (!backend->activate(&host)) {
    snprintf(text, text_size, "the plugin refused to activate");
    return false;
  }
  activated = true;
  publish_latency();
  atomic_store(&block_limit, host_max_block(&host));
  atomic_store(&active, true);
  return true;
}

// The Linux side sends this only once no block can be under way: its node
// has disconnected and a block that was still out has been answered. The
// audio thread is then waiting in its read, and the backend deactivates the
// plugin from this thread, as it does on Linux after the node's last cycle.
static void deactivate(void) {
  if (!activated)
    return;
  atomic_store(&active, false);
  backend->deactivate(&host);
  activated = false;
}

// The backend's tick: what the plugin asked for meanwhile, then what
// changed for the Linux side to read.
static void on_tick(void) {
  if (!loaded)
    return;
  if (backend->main_thread)
    backend->main_thread(&host);
  if (activated)
    publish_latency();
  shim_publish_meters(&host);
}

// --- the audio thread -------------------------------------------------------

static void silence(uint32_t frames) {
  for (uint32_t c = 0; c < WIRE_MAX_CHANNELS; ++c)
    memset(shared->out[c], 0, frames * sizeof(float));
}

// One block. The frame count is read once and checked against the fixed
// buffers and against what the plugin was activated for before anything
// uses it. The backend reads and writes the shared file's own buffers.
static uint32_t process_block(void) {
  uint32_t frames = shared->frames;
  uint32_t bounded = frames <= WIRE_MAX_FRAMES ? frames : WIRE_MAX_FRAMES;
  if (!atomic_load(&active) || frames == 0 ||
      frames > atomic_load(&block_limit)) {
    silence(bounded);
    return WIRE_BLOCK_REFUSED;
  }
  apply_sets();
  float *in[WIRE_MAX_CHANNELS], *out[WIRE_MAX_CHANNELS];
  for (uint32_t c = 0; c < WIRE_MAX_CHANNELS; ++c) {
    in[c] = shared->in[c];
    out[c] = shared->out[c];
  }
  shim_begin_block();
  backend->process(&host, frames, in, out);
  return shim_block_refused() ? WIRE_BLOCK_REFUSED : WIRE_BLOCK_OK;
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
  shim_enter_audio_thread();
  take_name();
  SetEvent(audio_named);
  for (;;) {
    char wake = 0;
    DWORD count = 0;
    if (!ReadFile(wake_in, &wake, 1, &count, NULL) || count != 1)
      break;
    if (wake != WIRE_WAKE_PROCESS)
      break;
    shared->status = process_block();
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
    reply.ok = load(r, &reply);
  else if (r->type == WIRE_ACTIVATE) {
    reply.ok = activate(reply.text, sizeof(reply.text));
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
  // A backend writes what it has to say as it goes; it should be read
  // before the reply that follows from it.
  fflush(stderr);
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
  WireShared *mapped = NULL;
  if (file == INVALID_HANDLE_VALUE || !GetFileSizeEx(file, &size) ||
      size.QuadPart != (LONGLONG)sizeof(WireShared))
    snprintf(start_failure, sizeof(start_failure),
             "the shared audio file is missing or not %u bytes long",
             (unsigned)sizeof(WireShared));
  else if (!(mapping = CreateFileMappingW(file, NULL, PAGE_READWRITE, 0, 0,
                                          NULL)) ||
           !(mapped = MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0,
                                    sizeof(WireShared))))
    snprintf(start_failure, sizeof(start_failure),
             "cannot map the shared audio file: Windows error %lu",
             (unsigned long)GetLastError());
  else if (mapped->magic != WIRE_MAGIC || mapped->version != WIRE_VERSION ||
           mapped->size != sizeof(WireShared))
    snprintf(start_failure, sizeof(start_failure),
             "the shared audio file is version %u, this side speaks %d",
             (unsigned)mapped->version, WIRE_VERSION);
  else {
    // Audio memory that is paged out costs a block; failing to pin it is
    // not worth refusing over.
    VirtualLock(mapped, sizeof(WireShared));
    shared = mapped;
  }
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
  // Windows plugins expect their host to have done this on the thread that
  // creates them.
  OleInitialize(NULL);
  request_ready = CreateEventW(NULL, FALSE, FALSE, NULL);
  request_taken = CreateEventW(NULL, FALSE, FALSE, NULL);
  audio_named = CreateEventW(NULL, TRUE, FALSE, NULL);
  if (!request_ready || !request_taken || !audio_named || !shim_start())
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
  ULONGLONG ticked = GetTickCount64();
  for (;;) {
    ULONGLONG since = GetTickCount64() - ticked;
    HANDLE events[] = {request_ready, shim_wake()};
    DWORD woke = MsgWaitForMultipleObjects(
        2, events, FALSE, since >= TICK_MS ? 0 : (DWORD)(TICK_MS - since),
        QS_ALLINPUT);
    // Timers call back from here, and so does whatever a plugin posts to
    // windows of its own.
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
    shim_run_events();
    if (GetTickCount64() - ticked >= TICK_MS) {
      ticked = GetTickCount64();
      on_tick();
    }
  }
  // Asked to quit, the Linux side has stopped the audio first, so the
  // plugin can be taken down properly. A channel that just closed says
  // nothing about the audio thread: leave without touching the plugin.
  if (!orderly)
    TerminateProcess(GetCurrentProcess(), 0);
  deactivate();
  if (host.impl)
    backend->unload(&host);
  return 0;
}
