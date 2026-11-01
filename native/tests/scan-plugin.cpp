// SPDX-License-Identifier: GPL-3.0-only
// A module that can stop inside each scanner phase. Exiting without cleanup
// leaves stderr exactly as a crashed plugin would, so the test can check
// the last marker without relying on a timeout or a desktop session.
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <unistd.h>
#include <clap/clap.h>
#include <pluginterfaces/base/ipluginbase.h>
#include <pluginterfaces/vst/ivstcomponent.h>
#include <pluginterfaces/vst/ivsteditcontroller.h>
#include <pluginterfaces/vst/ivstaudioprocessor.h>

using namespace Steinberg;
using namespace Steinberg::Vst;

static void phase(const char *name) {
  const char *stop = getenv("OPENXLR_TEST_SCAN_STOP");
  if (stop && !strcmp(stop, name)) _exit(73);
}

static int plugin_count() {
  const char *count = getenv("OPENXLR_TEST_SCAN_COUNT");
  return count ? atoi(count) : 1;
}

class Plugin final : public IComponent, public IAudioProcessor, public IEditController {
 public:
  tresult PLUGIN_API queryInterface(const TUID iid, void **obj) override {
    *obj = nullptr;
    if (!memcmp(iid, IAudioProcessor_iid, 16)) *obj = static_cast<IAudioProcessor *>(this);
    if (!memcmp(iid, IEditController_iid, 16)) *obj = static_cast<IEditController *>(this);
    return *obj ? kResultOk : kNoInterface;
  }
  uint32 PLUGIN_API addRef() override { return 1; }
  uint32 PLUGIN_API release() override { return 1; }
  tresult PLUGIN_API initialize(FUnknown *) override { phase("initialize"); return kResultOk; }
  tresult PLUGIN_API terminate() override { phase("release"); return kResultOk; }
  tresult PLUGIN_API getControllerClassId(TUID) override { phase("controller"); return kNoInterface; }
  tresult PLUGIN_API setIoMode(IoMode) override { return kResultOk; }
  int32 PLUGIN_API getBusCount(MediaType, BusDirection) override { phase("buses"); return 1; }
  tresult PLUGIN_API getBusInfo(MediaType, BusDirection, int32, BusInfo &info) override {
    info = {}; info.channelCount = 2; info.busType = kMain; return kResultOk;
  }
  tresult PLUGIN_API getRoutingInfo(RoutingInfo &, RoutingInfo &) override { return kNotImplemented; }
  tresult PLUGIN_API activateBus(MediaType, BusDirection, int32, TBool) override { return kResultOk; }
  tresult PLUGIN_API setActive(TBool) override { return kResultOk; }
  tresult PLUGIN_API setState(IBStream *) override { return kResultOk; }
  tresult PLUGIN_API getState(IBStream *) override { return kNotImplemented; }
  tresult PLUGIN_API setComponentState(IBStream *) override { return kResultOk; }
  // 32 ordinary parameters and the plugin's own bypass, which is not listed.
  int32 PLUGIN_API getParameterCount() override { phase("parameters"); return 33; }
  tresult PLUGIN_API getParameterInfo(int32 i, ParameterInfo &info) override {
    info = {}; info.id = i; if (i == 32) info.flags = ParameterInfo::kIsBypass; return kResultOk;
  }
  tresult PLUGIN_API getParamStringByValue(ParamID, ParamValue, String128) override { return kNotImplemented; }
  tresult PLUGIN_API getParamValueByString(ParamID, TChar *, ParamValue &) override { return kNotImplemented; }
  ParamValue PLUGIN_API normalizedParamToPlain(ParamID, ParamValue v) override { return v; }
  ParamValue PLUGIN_API plainParamToNormalized(ParamID, ParamValue v) override { return v; }
  ParamValue PLUGIN_API getParamNormalized(ParamID) override { return 0; }
  tresult PLUGIN_API setParamNormalized(ParamID, ParamValue) override { return kResultOk; }
  tresult PLUGIN_API setComponentHandler(IComponentHandler *) override { return kResultOk; }
  IPlugView *PLUGIN_API createView(FIDString) override { return nullptr; }
  tresult PLUGIN_API setBusArrangements(SpeakerArrangement *, int32, SpeakerArrangement *, int32) override {
    phase("widths"); return kResultFalse;
  }
  tresult PLUGIN_API getBusArrangement(BusDirection, int32, SpeakerArrangement &) override { return kNotImplemented; }
  tresult PLUGIN_API canProcessSampleSize(int32) override { return kResultOk; }
  uint32 PLUGIN_API getLatencySamples() override { return 0; }
  tresult PLUGIN_API setupProcessing(ProcessSetup &) override { return kResultOk; }
  tresult PLUGIN_API setProcessing(TBool) override { return kResultOk; }
  tresult PLUGIN_API process(ProcessData &) override { return kResultOk; }
  uint32 PLUGIN_API getTailSamples() override { return 0; }
};

static Plugin plugin;
class Factory final : public IPluginFactory {
 public:
  tresult PLUGIN_API queryInterface(const TUID, void **obj) override { *obj = nullptr; return kNoInterface; }
  uint32 PLUGIN_API addRef() override { return 1; }
  uint32 PLUGIN_API release() override { return 1; }
  tresult PLUGIN_API getFactoryInfo(PFactoryInfo *) override { return kNotImplemented; }
  int32 PLUGIN_API countClasses() override { phase("factory"); return plugin_count(); }
  tresult PLUGIN_API getClassInfo(int32 i, PClassInfo *info) override {
    phase("descriptor");
    *info = {}; memcpy(info->cid, &i, sizeof(i));
    strcpy(info->category, kVstAudioEffectClass); strcpy(info->name, "Scan fixture");
    return kResultOk;
  }
  tresult PLUGIN_API createInstance(FIDString, FIDString, void **obj) override {
    phase("create"); *obj = static_cast<IComponent *>(&plugin); return kResultOk;
  }
};
static Factory factory;
extern "C" IPluginFactory *GetPluginFactory() { return &factory; }
extern "C" bool ModuleEntry(void *) { phase("module"); return true; }
extern "C" bool ModuleExit() { phase("module release"); return true; }

static bool clap_init(const clap_plugin_t *) { phase("initialize"); return true; }
static void clap_destroy(const clap_plugin_t *) { phase("release"); }
static uint32_t port_count(const clap_plugin_t *, bool) { phase("buses"); return 1; }
static bool port_info(const clap_plugin_t *, uint32_t, bool, clap_audio_port_info_t *info) {
  *info = {}; info->flags = CLAP_AUDIO_PORT_IS_MAIN; info->channel_count = 2; return true;
}
static const clap_plugin_audio_ports_t ports = {port_count, port_info};
static uint32_t param_count(const clap_plugin_t *) { phase("parameters"); return 33; }
static bool param_info(const clap_plugin_t *, uint32_t i, clap_param_info_t *info) {
  *info = {}; info->id = i; info->max_value = 1;
  if (i == 32) info->flags = CLAP_PARAM_IS_BYPASS | CLAP_PARAM_IS_STEPPED;
  return true;
}
static const clap_plugin_params_t params = {param_count, param_info, nullptr, nullptr, nullptr, nullptr};
static const void *extension(const clap_plugin_t *, const char *id) {
  if (!strcmp(id, CLAP_EXT_AUDIO_PORTS)) return &ports;
  if (!strcmp(id, CLAP_EXT_PARAMS)) return &params;
  return nullptr;
}
static const clap_plugin_t clap_plugin = {
  .desc = nullptr, .plugin_data = nullptr, .init = clap_init, .destroy = clap_destroy,
  .activate = nullptr, .deactivate = nullptr, .start_processing = nullptr,
  .stop_processing = nullptr, .reset = nullptr, .process = nullptr,
  .get_extension = extension, .on_main_thread = nullptr,
};
static const clap_plugin_descriptor_t descriptor = {
  .clap_version = CLAP_VERSION_INIT, .id = "org.openxlr.scan-test", .name = "Scan fixture",
  .vendor = "OpenXLR", .url = "", .manual_url = "", .support_url = "",
  .version = "1", .description = "", .features = nullptr,
};
static uint32_t clap_count(const clap_plugin_factory_t *) { phase("factory"); return plugin_count(); }
static const clap_plugin_descriptor_t *clap_descriptor(const clap_plugin_factory_t *, uint32_t) {
  phase("descriptor"); return &descriptor;
}
static const clap_plugin_t *clap_create(const clap_plugin_factory_t *, const clap_host_t *, const char *) {
  phase("create"); return &clap_plugin;
}
static const clap_plugin_factory_t clap_factory = {clap_count, clap_descriptor, clap_create};
static bool entry_init(const char *) { phase("module"); return true; }
static void entry_deinit() { phase("module release"); }
static const void *entry_factory(const char *) { return &clap_factory; }
extern "C" const clap_plugin_entry_t clap_entry = {CLAP_VERSION_INIT, entry_init, entry_deinit, entry_factory};
