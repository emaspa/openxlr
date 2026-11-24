// SPDX-License-Identifier: GPL-3.0-only
// The Windows test plugin: a CLAP module built with mingw for
// tests/winhost.c to load through the Windows plugin host. Each plugin in
// it halves its input and has one way of behaving beside that, chosen by
// its id, so a test picks the behaviour it wants by the plugin it loads.
#include <clap/clap.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

#include <windows.h>

#define ID "org.openxlr.test.winhost."

enum {
  KIND_GAIN,       // nothing else
  KIND_MONO,       // one channel wide
  KIND_LATENCY,    // reports 480 frames
  KIND_RESTART,    // asks for a restart on block 8; reports 100 frames, then 300
  KIND_CALLBACK,   // asks for its main-thread callback on block 4, then quarters
  KIND_SIDECHAIN,  // a second input port, which it adds to the output
  KIND_REFUSE,     // every block is a processing error
  KIND_SLOW,       // block 16 takes 60 ms
  KIND_STALL,      // block 16 never returns
  KIND_CRASH,      // block 16 writes through a null pointer
  KINDS
};

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
};

typedef struct {
  clap_plugin_t plugin;
  const clap_host_t *host;
  int kind;
  unsigned blocks;       // audio thread
  unsigned activations;  // main thread
  _Atomic int called_back;
} Instance;

static Instance *of(const clap_plugin_t *plugin) { return plugin->plugin_data; }

static bool plugin_init(const clap_plugin_t *plugin) { return true; }
static void plugin_destroy(const clap_plugin_t *plugin) { free(of(plugin)); }

static bool plugin_activate(const clap_plugin_t *plugin, double rate,
                            uint32_t min, uint32_t max) {
  of(plugin)->activations++;
  return true;
}

static void plugin_deactivate(const clap_plugin_t *plugin) {}
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
      p->kind == KIND_REFUSE)
    return CLAP_PROCESS_ERROR;
  if (p->kind == KIND_RESTART && block == RESTART_BLOCK)
    p->host->request_restart(p->host);
  if (p->kind == KIND_CALLBACK && block == CALLBACK_BLOCK)
    p->host->request_callback(p->host);
  if (block == TROUBLE_BLOCK) {
    if (p->kind == KIND_SLOW)
      Sleep(60);
    if (p->kind == KIND_STALL)
      Sleep(INFINITE);
    if (p->kind == KIND_CRASH)
      *(volatile int *)0 = 1;
  }
  const clap_audio_buffer_t *in = &process->audio_inputs[0];
  const clap_audio_buffer_t *out = &process->audio_outputs[0];
  float gain = p->kind == KIND_CALLBACK && atomic_load(&p->called_back) ? 0.25f : 0.5f;
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

static uint32_t ports_count(const clap_plugin_t *plugin, bool is_input) {
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

static const void *plugin_get_extension(const clap_plugin_t *plugin,
                                        const char *id) {
  int kind = of(plugin)->kind;
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
