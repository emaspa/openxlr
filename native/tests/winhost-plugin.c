// SPDX-License-Identifier: GPL-3.0-only
// The Windows test plugin: a CLAP module built with mingw for
// tests/winhost.c to load through the Windows plugin host. Each plugin in
// it halves its input and has one way of behaving beside that, chosen by
// its id, so a test picks the behaviour it wants by the plugin it loads.
// A scan describes every plugin in the module, so what goes wrong during
// one is chosen by OPENXLR_TEST_WINHOST_SCAN (see scan_trouble). With
// OPENXLR_TEST_WINHOST_PRINT set, every plugin prints on standard output
// while it is made, activated and run (see print_line).
#include "disk-probe.h"

#include <clap/clap.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <windows.h>
#include <winioctl.h>

#include "winhost-plugin-editor.h"

#define ID "org.openxlr.test.winhost."

enum {
  KIND_GAIN,       // nothing else; it says it has an editor and cannot make one
  KIND_MONO,       // one channel wide
  KIND_LATENCY,    // reports 480 frames
  KIND_RESTART,    // asks for a restart on block 8; reports 100 frames, then 300
  KIND_CALLBACK,   // asks for its main-thread callback on block 4, then quarters
  KIND_SIDECHAIN,  // a second input port, which it adds to the output
  KIND_REFUSE,     // every block is a processing error
  KIND_SLOW,       // block 16 takes 60 ms
  KIND_STALL,      // block 16 never returns
  KIND_CRASH,      // block 16 writes through a null pointer
  KIND_PARAMS,     // five parameters and an editor; see PARAM_GAIN and the rest
  KIND_MANY,       // more parameters than a host carries; see MANY_PARAMS
  KIND_TIMER,      // asks for a timer when it is made, quarters after three calls
  KIND_DISK,       // asks for the disk's identity; see probe_disk and ask_per_block
  KINDS
};

// KIND_PARAMS. The gain is the factor the audio is multiplied by, and the
// plugin answers every gain it is sent with the same value. Setting `move`
// to anything but 0 makes the plugin move its own gain to that value and
// say so, as an editor would. The level is a meter, twice the gain. The last two must never reach the host's control table: one is
// hidden, and the plugin's own bypass is left to the chain's. This plugin
// has the editor of winhost-plugin-editor.h, which moves the gain too.
enum { PARAM_GAIN = 1, PARAM_MOVE, PARAM_LEVEL, PARAM_HIDDEN, PARAM_BYPASS, PARAMS = 5 };

// KIND_MANY. Parameters 1 to MANY_PARAMS, each 0 or 1. The audio is
// quartered while exactly MANY_QUARTER of them are 1 and halved otherwise.
enum { MANY_PARAMS = 5000, MANY_QUARTER = 1000 };

// KIND_TIMER.
enum { TIMER_MS = 20, TIMER_CALLS = 3 };

// Which block the timed behaviours act on.
enum { RESTART_BLOCK = 8, CALLBACK_BLOCK = 4, TROUBLE_BLOCK = 16 };

static const char *const features[] = {CLAP_PLUGIN_FEATURE_AUDIO_EFFECT, NULL};

#define DESCRIPTOR(suffix, title) \
  {.clap_version = CLAP_VERSION_INIT, .id = ID suffix, .name = title, \
   .vendor = "OpenXLR", .url = "", .manual_url = "", .support_url = "", \
   .version = "1", .description = "", .features = features}

static const clap_plugin_descriptor_t descriptors[KINDS] = {
    [KIND_GAIN] = DESCRIPTOR("gain", "Test Gain"),
    [KIND_MONO] = DESCRIPTOR("mono", "Test Mono"),
    [KIND_LATENCY] = DESCRIPTOR("latency", "Test Latency"),
    [KIND_RESTART] = DESCRIPTOR("restart", "Test Restart"),
    [KIND_CALLBACK] = DESCRIPTOR("callback", "Test Callback"),
    [KIND_SIDECHAIN] = DESCRIPTOR("sidechain", "Test Sidechain"),
    [KIND_REFUSE] = DESCRIPTOR("refuse", "Test Refuse"),
    [KIND_SLOW] = DESCRIPTOR("slow", "Test Slow"),
    [KIND_STALL] = DESCRIPTOR("stall", "Test Stall"),
    [KIND_CRASH] = DESCRIPTOR("crash", "Test Crash"),
    [KIND_PARAMS] = DESCRIPTOR("params", "Test Params"),
    [KIND_MANY] = DESCRIPTOR("many", "Test Many"),
    [KIND_TIMER] = DESCRIPTOR("timer", "Test Timer"),
    [KIND_DISK] = DESCRIPTOR("disk", "Test Disk"),
};

typedef struct {
  clap_plugin_t plugin;
  const clap_host_t *host;
  int kind;
  unsigned blocks;       // audio thread
  unsigned activations;  // main thread
  _Atomic int called_back;
  // KIND_PARAMS: written by whichever thread is handed the events, read by
  // the main thread when the host asks for a value.
  _Atomic float gain, move;
  // KIND_MANY: which parameters are 1, and how many.
  unsigned char *many;
  unsigned many_set;
  // KIND_TIMER
  clap_id timer;
  _Atomic int timer_calls;
  // KIND_DISK: the handle the blocks ask on, the answer it gave when the
  // plugin was activated, and the latest answer, which a block writes.
  HANDLE drive;
  ProbeRecord activated, block;
  // The editor, and a gain it has moved that the host has not been told.
  TestEditor editor;
  _Atomic float edit;
  _Atomic bool edited;
  // OPENXLR_TEST_WINHOST_PRINT, read once when the plugin is made.
  bool prints;
} Instance;

static Instance *of(const clap_plugin_t *plugin) { return plugin->plugin_data; }

// --- KIND_DISK ----------------------------------------------------------------

// One storage property query, made from this module, so that it goes through
// whatever its imports were pointed at.
static void ask(HANDLE drive, int property, int type, DWORD size, ProbeRecord *record) {
  STORAGE_PROPERTY_QUERY query = {property, type, {0}};
  memset(record, 0, sizeof(*record));
  DWORD returned = 0;
  BOOL ok = DeviceIoControl(drive, IOCTL_STORAGE_QUERY_PROPERTY, &query, sizeof(query),
                            record->bytes, size, &returned, NULL);
  record->ok = ok != 0;
  record->returned = returned;
  record->error = ok ? 0 : GetLastError();
}

static HANDLE open_drive(const char *name) {
  return CreateFileA(name, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
}

static HANDLE open_wide(const WCHAR *name, DWORD access) {
  return CreateFileW(name, access, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0,
                     NULL);
}

// The questions disk-probe.h lists, asked when the plugin is made, in a load
// and in a scan alike, and written to the file OPENXLR_TEST_WINHOST_DISK
// names as a Linux path. Nothing is asked without it.
static void probe_disk(void) {
  const char *named = getenv("OPENXLR_TEST_WINHOST_DISK");
  typedef WCHAR *(CDECL * ToDos)(const char *);
  ToDos to_dos = (ToDos)(void *)GetProcAddress(GetModuleHandleW(L"kernel32.dll"),
                                               "wine_get_dos_file_name");
  WCHAR *path = named && to_dos ? to_dos(named) : NULL;
  if (!path)
    return;
  static ProbeRecord records[PROBE_RECORDS];
  const int device = StorageDeviceProperty, standard = PropertyStandardQuery;
  HANDLE drive = open_drive("\\\\.\\PhysicalDrive0");
  ask(drive, device, standard, PROBE_BUFFER, &records[PROBE_FIRST]);
  ask(drive, device, standard, PROBE_BUFFER, &records[PROBE_REPEAT]);
  ask(drive, device, standard, sizeof(STORAGE_DESCRIPTOR_HEADER), &records[PROBE_HEADER]);
  ask(drive, StorageAdapterProperty, standard, PROBE_BUFFER, &records[PROBE_ADAPTER]);
  ask(drive, device, PropertyExistsQuery, PROBE_BUFFER, &records[PROBE_EXISTS]);
  HANDLE volume = open_wide(L"\\\\.\\C:", 0);
  ask(volume, device, standard, PROBE_BUFFER, &records[PROBE_VOLUME]);
  CloseHandle(volume);
  CloseHandle(drive);
  ask(drive, device, standard, PROBE_BUFFER, &records[PROBE_CLOSED]);
  HANDLE again = open_wide(L"\\\\?\\PHYSICALDRIVE0", 0);
  ask(again, device, standard, PROBE_BUFFER, &records[PROBE_REOPENED]);
  CloseHandle(again);
  HANDLE plain = open_wide(L"C:\\windows\\win.ini", GENERIC_READ);
  ask(plain, device, standard, PROBE_BUFFER, &records[PROBE_REUSED]);
  records[PROBE_REUSED].same = plain == again;
  CloseHandle(plain);
  HANDLE out = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
  DWORD written = 0;
  WriteFile(out, records, sizeof(records), &written, NULL);
  CloseHandle(out);
  HeapFree(GetProcessHeap(), 0, path);
}

// On the audio thread, once per block, as a plugin that checks its licence
// as it goes: the answer has to be the one the plugin was given when it was
// activated. A different one, or none, is a processing error.
static bool ask_per_block(Instance *p) {
  ask(p->drive, StorageDeviceProperty, PropertyStandardQuery, PROBE_BUFFER, &p->block);
  return p->block.ok && p->block.returned == p->activated.returned &&
         !memcmp(p->block.bytes, p->activated.bytes, p->block.returned);
}

// A line on standard output through the C library this module was built
// with and one through Windows, as a plugin that prints does. Each names
// where it was printed from.
static void print_line(Instance *p, const char *where) {
  if (!p->prints)
    return;
  char line[96];
  int length = snprintf(line, sizeof(line), "[printed through the C library %s]\n", where);
  fputs(line, stdout);
  fflush(stdout);
  length = snprintf(line, sizeof(line), "[printed through the Windows handle %s]\n", where);
  DWORD written = 0;
  if (length > 0)
    WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), line, (DWORD)length, &written, NULL);
}

static bool plugin_init(const clap_plugin_t *plugin) {
  Instance *p = of(plugin);
  p->prints = getenv("OPENXLR_TEST_WINHOST_PRINT") != NULL;
  print_line(p, "when made");
  if (p->kind == KIND_DISK)
    probe_disk();
  if (p->kind == KIND_TIMER) {
    const clap_host_timer_support_t *timers =
        p->host->get_extension(p->host, CLAP_EXT_TIMER_SUPPORT);
    return timers && timers->register_timer(p->host, TIMER_MS, &p->timer);
  }
  return true;
}

static void plugin_destroy(const clap_plugin_t *plugin) {
  free(of(plugin)->many);
  free(of(plugin));
}

static void plugin_on_timer(const clap_plugin_t *plugin, clap_id timer) {
  Instance *p = of(plugin);
  if (timer != p->timer)
    return;
  if (atomic_fetch_add(&p->timer_calls, 1) + 1 == TIMER_CALLS) {
    const clap_host_timer_support_t *timers =
        p->host->get_extension(p->host, CLAP_EXT_TIMER_SUPPORT);
    timers->unregister_timer(p->host, timer);
  }
}

static const clap_plugin_timer_support_t timer_support = {plugin_on_timer};

// The parameter values the host sends, in a block or in a flush.
static void take_events(Instance *p, const clap_input_events_t *in,
                        const clap_output_events_t *out) {
  // What the editor did since the last look comes first: the gain changes
  // and the host is told, as for a move.
  if (out && atomic_exchange(&p->edited, false)) {
    clap_event_param_value_t said = {
        .header = {sizeof(said), 0, CLAP_CORE_EVENT_SPACE_ID,
                   CLAP_EVENT_PARAM_VALUE, 0},
        .param_id = PARAM_GAIN,
        .note_id = -1,
        .port_index = -1,
        .channel = -1,
        .key = -1,
        .value = atomic_load(&p->edit),
    };
    atomic_store(&p->gain, (float)said.value);
    out->try_push(out, &said.header);
  }
  uint32_t count = in ? in->size(in) : 0;
  for (uint32_t i = 0; i < count; ++i) {
    const clap_event_header_t *header = in->get(in, i);
    if (!header || header->space_id != CLAP_CORE_EVENT_SPACE_ID ||
        header->type != CLAP_EVENT_PARAM_VALUE)
      continue;
    const clap_event_param_value_t *e = (const clap_event_param_value_t *)header;
    if (p->kind == KIND_MANY) {
      if (e->param_id < 1 || e->param_id > MANY_PARAMS)
        continue;
      unsigned char *held = &p->many[e->param_id - 1], now = e->value >= 0.5;
      p->many_set += now - *held;
      *held = now;
    } else if (p->kind == KIND_PARAMS &&
               (e->param_id == PARAM_GAIN || e->param_id == PARAM_MOVE)) {
      // A gain the host sends is answered with the same value, which a
      // plugin has no need to do and a host has to put up with. A move
      // that is not 0 changes the gain and says so.
      if (e->param_id == PARAM_MOVE)
        atomic_store(&p->move, (float)e->value);
      if ((e->param_id == PARAM_MOVE && e->value == 0) || !out)
        continue;
      atomic_store(&p->gain, (float)e->value);
      clap_event_param_value_t said = {
          .header = {sizeof(said), 0, CLAP_CORE_EVENT_SPACE_ID,
                     CLAP_EVENT_PARAM_VALUE, 0},
          .param_id = PARAM_GAIN,
          .note_id = -1,
          .port_index = -1,
          .channel = -1,
          .key = -1,
          .value = e->value,
      };
      out->try_push(out, &said.header);
    }
  }
}

static bool plugin_activate(const clap_plugin_t *plugin, double rate,
                            uint32_t min, uint32_t max) {
  Instance *p = of(plugin);
  p->activations++;
  print_line(p, "when activated");
  if (p->kind == KIND_DISK) {
    p->drive = open_drive("\\\\.\\PhysicalDrive0");
    ask(p->drive, StorageDeviceProperty, PropertyStandardQuery, PROBE_BUFFER, &p->activated);
    return p->activated.ok;
  }
  return true;
}

static void plugin_deactivate(const clap_plugin_t *plugin) {
  Instance *p = of(plugin);
  if (p->kind == KIND_DISK && p->drive != INVALID_HANDLE_VALUE)
    CloseHandle(p->drive);
}
static bool plugin_start(const clap_plugin_t *plugin) { return true; }
static void plugin_stop(const clap_plugin_t *plugin) {}
static void plugin_reset(const clap_plugin_t *plugin) {}

static void plugin_on_main_thread(const clap_plugin_t *plugin) {
  atomic_store(&of(plugin)->called_back, 1);
}

static clap_process_status plugin_process(const clap_plugin_t *plugin,
                                          const clap_process_t *process) {
  Instance *p = of(plugin);
  unsigned block = ++p->blocks;
  uint32_t inputs = p->kind == KIND_SIDECHAIN ? 2 : 1;
  if (process->audio_inputs_count != inputs || process->audio_outputs_count != 1 ||
      p->kind == KIND_REFUSE || (p->kind == KIND_DISK && !ask_per_block(p)))
    return CLAP_PROCESS_ERROR;
  if (p->kind == KIND_RESTART && block == RESTART_BLOCK)
    p->host->request_restart(p->host);
  if (p->kind == KIND_CALLBACK && block == CALLBACK_BLOCK)
    p->host->request_callback(p->host);
  if (block <= 3 || block == TROUBLE_BLOCK)
    print_line(p, "in a block");
  if (block == TROUBLE_BLOCK) {
    if (p->kind == KIND_SLOW)
      Sleep(60);
    if (p->kind == KIND_STALL)
      Sleep(INFINITE);
    if (p->kind == KIND_CRASH)
      *(volatile int *)0 = 1;
  }
  take_events(p, process->in_events, process->out_events);
  const clap_audio_buffer_t *in = &process->audio_inputs[0];
  const clap_audio_buffer_t *out = &process->audio_outputs[0];
  float gain = 0.5f;
  if (p->kind == KIND_CALLBACK && atomic_load(&p->called_back))
    gain = 0.25f;
  if (p->kind == KIND_TIMER && atomic_load(&p->timer_calls) >= TIMER_CALLS)
    gain = 0.25f;
  if (p->kind == KIND_MANY && p->many_set == MANY_QUARTER)
    gain = 0.25f;
  if (p->kind == KIND_PARAMS)
    gain = atomic_load(&p->gain);
  for (uint32_t c = 0; c < out->channel_count; ++c)
    for (uint32_t n = 0; n < process->frames_count; ++n) {
      float sample = in->data32[c][n] * gain;
      // The host gives a port it has no use for silence, so this adds 0.
      if (p->kind == KIND_SIDECHAIN)
        sample += process->audio_inputs[1].data32[c][n];
      out->data32[c][n] = sample;
    }
  return CLAP_PROCESS_CONTINUE;
}

// What a scan of this module runs into, when the test asks for it: a fault,
// a call that never returns, or text on standard output, written through
// the C library and through Windows' own handle. Neither text ends its line.
// With no control character in it, text that landed inside one of the
// description's strings would leave it readable, so the test compares bytes.
static void scan_trouble(void) {
  const char *what = getenv("OPENXLR_TEST_WINHOST_SCAN");
  if (!what)
    return;
  if (!strcmp(what, "crash"))
    *(volatile int *)0 = 1;
  if (!strcmp(what, "hang"))
    Sleep(INFINITE);
  if (!strcmp(what, "print")) {
    static const char raw[] = "[printed through the Windows handle]";
    DWORD written = 0;
    fputs("[printed through the C library]", stdout);
    fflush(stdout);
    WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), raw, sizeof(raw) - 1, &written,
              NULL);
  }
}

static uint32_t ports_count(const clap_plugin_t *plugin, bool is_input) {
  if (of(plugin)->kind == KIND_GAIN && is_input)
    scan_trouble();
  return is_input && of(plugin)->kind == KIND_SIDECHAIN ? 2 : 1;
}

static bool ports_get(const clap_plugin_t *plugin, uint32_t index,
                      bool is_input, clap_audio_port_info_t *info) {
  if (index >= ports_count(plugin, is_input))
    return false;
  memset(info, 0, sizeof(*info));
  info->id = (is_input ? 0 : 10) + index;
  strcpy(info->name, is_input ? (index ? "sidechain" : "in") : "out");
  info->flags = index == 0 ? CLAP_AUDIO_PORT_IS_MAIN : 0;
  info->channel_count = of(plugin)->kind == KIND_MONO ? 1 : 2;
  info->port_type = of(plugin)->kind == KIND_MONO ? CLAP_PORT_MONO : CLAP_PORT_STEREO;
  info->in_place_pair = CLAP_INVALID_ID;
  return true;
}

static const clap_plugin_audio_ports_t audio_ports = {ports_count, ports_get};

static uint32_t latency_get(const clap_plugin_t *plugin) {
  Instance *p = of(plugin);
  if (p->kind == KIND_LATENCY)
    return 480;
  return p->activations > 1 ? 300 : 100;
}

static const clap_plugin_latency_t latency = {latency_get};

static uint32_t params_count(const clap_plugin_t *plugin) {
  return of(plugin)->kind == KIND_MANY ? MANY_PARAMS : PARAMS;
}

static bool params_get_info(const clap_plugin_t *plugin, uint32_t index,
                            clap_param_info_t *info) {
  if (index >= params_count(plugin))
    return false;
  memset(info, 0, sizeof(*info));
  info->id = index + 1;
  info->max_value = 1;
  if (of(plugin)->kind == KIND_MANY) {
    snprintf(info->name, sizeof(info->name), "Switch %u", (unsigned)index + 1);
    info->flags = CLAP_PARAM_IS_STEPPED;
    return true;
  }
  static const char *const names[PARAMS] = {"Gain", "Move", "Level", "Hidden",
                                            "Bypass"};
  strcpy(info->name, names[index]);
  switch (info->id) {
    case PARAM_GAIN: info->default_value = 0.5; break;
    case PARAM_LEVEL:
      info->max_value = 2;
      info->default_value = 1;
      info->flags = CLAP_PARAM_IS_READONLY;
      break;
    case PARAM_HIDDEN: info->flags = CLAP_PARAM_IS_HIDDEN; break;
    case PARAM_BYPASS: info->flags = CLAP_PARAM_IS_BYPASS | CLAP_PARAM_IS_STEPPED; break;
  }
  return true;
}

static bool params_get_value(const clap_plugin_t *plugin, clap_id id,
                             double *value) {
  Instance *p = of(plugin);
  if (id < 1 || id > params_count(plugin))
    return false;
  if (p->kind == KIND_MANY)
    *value = p->many[id - 1];
  else
    *value = id == PARAM_GAIN    ? atomic_load(&p->gain)
             : id == PARAM_MOVE  ? atomic_load(&p->move)
             : id == PARAM_LEVEL ? atomic_load(&p->gain) * 2
                                 : 0;
  return true;
}

static bool params_value_to_text(const clap_plugin_t *plugin, clap_id id,
                                 double value, char *text, uint32_t size) {
  snprintf(text, size, "%g", value);
  return true;
}

static bool params_text_to_value(const clap_plugin_t *plugin, clap_id id,
                                 const char *text, double *value) {
  *value = atof(text);
  return true;
}

static void params_flush(const clap_plugin_t *plugin,
                         const clap_input_events_t *in,
                         const clap_output_events_t *out) {
  take_events(of(plugin), in, out);
}

static const clap_plugin_params_t params = {
    params_count, params_get_info, params_get_value, params_value_to_text,
    params_text_to_value, params_flush};

// The editor is in a window of Windows' own and in no other. KIND_PARAMS
// has one. KIND_GAIN says it has one too and then fails to make it.
static bool gui_is_api_supported(const clap_plugin_t *plugin, const char *api,
                                 bool is_floating) {
  return !is_floating && !strcmp(api, CLAP_WINDOW_API_WIN32);
}

// The editor moved the gain, on the main thread. The host hears of it in
// the events of the next block, or of the flush this asks for.
static void editor_set_gain(void *plugin, float gain) {
  Instance *p = plugin;
  const clap_host_params_t *host_params =
      p->host->get_extension(p->host, CLAP_EXT_PARAMS);
  atomic_store(&p->edit, gain);
  atomic_store(&p->edited, true);
  if (host_params)
    host_params->request_flush(p->host);
}

static bool editor_ask_size(void *plugin, int width, int height) {
  Instance *p = plugin;
  const clap_host_gui_t *host_gui = p->host->get_extension(p->host, CLAP_EXT_GUI);
  return host_gui &&
         host_gui->request_resize(p->host, (uint32_t)width, (uint32_t)height);
}

static void editor_closed(void *plugin) {
  Instance *p = plugin;
  const clap_host_gui_t *host_gui = p->host->get_extension(p->host, CLAP_EXT_GUI);
  if (host_gui)
    host_gui->closed(p->host, false);
}

static const TestEditorCalls editor_calls = {editor_set_gain, editor_ask_size,
                                             editor_closed};

static bool gui_get_preferred_api(const clap_plugin_t *plugin, const char **api,
                                  bool *is_floating) {
  *api = CLAP_WINDOW_API_WIN32;
  *is_floating = false;
  return true;
}

static bool gui_create(const clap_plugin_t *plugin, const char *api,
                       bool is_floating) {
  Instance *p = of(plugin);
  if (p->kind != KIND_PARAMS || !gui_is_api_supported(plugin, api, is_floating))
    return false;
  p->editor = (TestEditor){.scale = 1, .calls = &editor_calls, .plugin = p};
  return true;
}

static void gui_destroy(const clap_plugin_t *plugin) {
  test_editor_destroy(&of(plugin)->editor);
}

static bool gui_set_scale(const clap_plugin_t *plugin, double scale) {
  of(plugin)->editor.scale = scale;
  return true;
}

static bool gui_get_size(const clap_plugin_t *plugin, uint32_t *width,
                         uint32_t *height) {
  int wanted_width, wanted_height;
  test_editor_wanted(&of(plugin)->editor, &wanted_width, &wanted_height);
  *width = (uint32_t)wanted_width;
  *height = (uint32_t)wanted_height;
  return true;
}

static bool gui_can_resize(const clap_plugin_t *plugin) {
  return !test_editor_fixed();
}

static bool gui_get_resize_hints(const clap_plugin_t *plugin,
                                 clap_gui_resize_hints_t *hints) {
  return false;
}

static bool gui_adjust_size(const clap_plugin_t *plugin, uint32_t *width,
                            uint32_t *height) {
  int fitted_width = (int)*width, fitted_height = (int)*height;
  test_editor_fit(&fitted_width, &fitted_height);
  *width = (uint32_t)fitted_width;
  *height = (uint32_t)fitted_height;
  return true;
}

static bool gui_set_size(const clap_plugin_t *plugin, uint32_t width,
                         uint32_t height) {
  test_editor_set_size(&of(plugin)->editor, (int)width, (int)height);
  return true;
}

static bool gui_set_parent(const clap_plugin_t *plugin,
                           const clap_window_t *window) {
  return test_editor_create(&of(plugin)->editor, (HWND)window->win32);
}

static bool gui_set_transient(const clap_plugin_t *plugin,
                              const clap_window_t *window) {
  return false;
}

static void gui_suggest_title(const clap_plugin_t *plugin, const char *title) {}
static bool gui_show(const clap_plugin_t *plugin) { return true; }
static bool gui_hide(const clap_plugin_t *plugin) { return true; }

static const clap_plugin_gui_t gui = {
    gui_is_api_supported, gui_get_preferred_api, gui_create, gui_destroy,
    gui_set_scale, gui_get_size, gui_can_resize, gui_get_resize_hints,
    gui_adjust_size, gui_set_size, gui_set_parent, gui_set_transient,
    gui_suggest_title, gui_show, gui_hide};

static const void *plugin_get_extension(const clap_plugin_t *plugin,
                                        const char *id) {
  int kind = of(plugin)->kind;
  if (!strcmp(id, CLAP_EXT_GUI) && (kind == KIND_GAIN || kind == KIND_PARAMS))
    return &gui;
  if (!strcmp(id, CLAP_EXT_PARAMS) && (kind == KIND_PARAMS || kind == KIND_MANY))
    return &params;
  if (!strcmp(id, CLAP_EXT_TIMER_SUPPORT) && kind == KIND_TIMER)
    return &timer_support;
  if (!strcmp(id, CLAP_EXT_AUDIO_PORTS))
    return &audio_ports;
  if (!strcmp(id, CLAP_EXT_LATENCY) && (kind == KIND_LATENCY || kind == KIND_RESTART))
    return &latency;
  return NULL;
}

static uint32_t factory_count(const clap_plugin_factory_t *factory) {
  return KINDS;
}

static const clap_plugin_descriptor_t *factory_descriptor(
    const clap_plugin_factory_t *factory, uint32_t index) {
  return index < KINDS ? &descriptors[index] : NULL;
}

static const clap_plugin_t *factory_create(const clap_plugin_factory_t *factory,
                                           const clap_host_t *host,
                                           const char *id) {
  for (int kind = 0; kind < KINDS; ++kind) {
    if (strcmp(id, descriptors[kind].id) != 0)
      continue;
    Instance *p = calloc(1, sizeof(Instance));
    if (!p)
      return NULL;
    p->host = host;
    p->kind = kind;
    atomic_init(&p->gain, 0.5f);
    if (kind == KIND_MANY && !(p->many = calloc(MANY_PARAMS, 1))) {
      free(p);
      return NULL;
    }
    p->plugin = (clap_plugin_t){
        .desc = &descriptors[kind],
        .plugin_data = p,
        .init = plugin_init,
        .destroy = plugin_destroy,
        .activate = plugin_activate,
        .deactivate = plugin_deactivate,
        .start_processing = plugin_start,
        .stop_processing = plugin_stop,
        .reset = plugin_reset,
        .process = plugin_process,
        .get_extension = plugin_get_extension,
        .on_main_thread = plugin_on_main_thread,
    };
    return &p->plugin;
  }
  return NULL;
}

static const clap_plugin_factory_t factory = {factory_count, factory_descriptor,
                                              factory_create};

static bool entry_init(const char *path) { return true; }
static void entry_deinit(void) {}

static const void *entry_get_factory(const char *id) {
  return strcmp(id, CLAP_PLUGIN_FACTORY_ID) == 0 ? &factory : NULL;
}

CLAP_EXPORT const clap_plugin_entry_t clap_entry = {
    CLAP_VERSION_INIT, entry_init, entry_deinit, entry_get_factory};
