// SPDX-License-Identifier: GPL-3.0-only
// The Windows VST3 test plugin: a module built with mingw for
// tests/winhost.c to load through the Windows plugin host. Each class in it
// halves its input and has one way of behaving beside that, so a test picks
// the behaviour it wants by the class it loads. One object is the component,
// the processor and the controller. A scan describes every class in the
// module, so what goes wrong during one is chosen by
// OPENXLR_TEST_WINHOST_SCAN (see scan_trouble).
#include <cstdio>
#include <cstdlib>
#include <cstring>

#include <pluginterfaces/base/ipluginbase.h>
#include <pluginterfaces/gui/iplugview.h>
#include <pluginterfaces/gui/iplugviewcontentscalesupport.h>
#include <pluginterfaces/vst/ivstaudioprocessor.h>
#include <pluginterfaces/vst/ivstcomponent.h>
#include <pluginterfaces/vst/ivsteditcontroller.h>
#include <pluginterfaces/vst/ivstparameterchanges.h>
#include <pluginterfaces/vst/vstspeaker.h>

#include <windows.h>

#include "winhost-plugin-editor.h"

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace {

enum {
  KIND_GAIN,     // nothing else; it takes a mono chain when asked
  KIND_FIXED,    // stereo and nothing else, with a second input bus it adds in
  KIND_LATENCY,  // reports 480 frames
  KIND_RESTART,  // asks to be reloaded on block 8; reports 100 frames, then 300
  KIND_REFUSE,   // every block is refused
  KIND_PARAMS,   // five parameters and an editor; see PARAM_GAIN and the rest
  KINDS
};

const char *const names[KINDS] = {"Test VST3 Gain",    "Test VST3 Fixed",
                                  "Test VST3 Latency", "Test VST3 Restart",
                                  "Test VST3 Refuse",  "Test VST3 Params"};

// A class id as a Windows plugin holds it: the first eight bytes as COM
// orders them, three fields with the low byte first. A Linux host knows the
// same class by the digits 00112233445566778899AABBCCDDEE and the kind's
// two, which is the text the test loads it by. The bytes are written out
// here and not made by the SDK's macro, so the test holds the host to this
// order; the assertion below says the macro makes the same.
struct ClassId {
  TUID bytes;
};

constexpr ClassId class_id(int kind) {
  return {{0x33, 0x22, 0x11, 0x00, 0x55, 0x44, 0x77, 0x66, (char)0x88,
           (char)0x99, (char)0xAA, (char)0xBB, (char)0xCC, (char)0xDD,
           (char)0xEE, (char)kind}};
}

constexpr TUID id_by_macro =
    INLINE_UID(0x00112233, 0x44556677, 0x8899AABB, 0xCCDDEE00);

constexpr bool same_bytes(const TUID a, const ClassId &b) {
  for (int i = 0; i < 16; ++i)
    if (a[i] != b.bytes[i])
      return false;
  return true;
}

static_assert(same_bytes(id_by_macro, class_id(0)),
              "the SDK lays a Windows class id out in this order");

// KIND_PARAMS. The gain is the factor the audio is multiplied by, and only
// the processor's parameter queue changes it. Setting `move` to anything but
// 0 makes the controller move its own gain to that value and report the
// edit, on the main thread, as an editor would. The level is a meter the
// processor writes, twice the gain. The last two must never reach the host's
// control table: one is hidden, and the plugin's own bypass is left to the
// chain's. This class has the editor of winhost-plugin-editor.h, which
// moves the gain the same way. The other classes have no editor.
enum { PARAM_GAIN = 1, PARAM_MOVE, PARAM_LEVEL, PARAM_HIDDEN, PARAM_BYPASS, PARAMS = 5 };

enum { RESTART_BLOCK = 8 };

// The module was entered through InitDll, the name a Windows module has for
// it. Without that it has no factory to give.
bool entered;

bool is(const TUID iid, const TUID other) { return memcmp(iid, other, sizeof(TUID)) == 0; }

// What a scan of this module runs into, when the test asks for it: a fault,
// a call that never returns, or text on standard output, written through
// the C library and through Windows' own handle. Neither text ends its line.
// With no control character in it, text that landed inside one of the
// description's strings would leave it readable, so the test compares bytes.
void scan_trouble() {
  const char *what = getenv("OPENXLR_TEST_WINHOST_SCAN");
  if (!what)
    return;
  if (!strcmp(what, "crash"))
    *(volatile int *)nullptr = 1;
  if (!strcmp(what, "hang"))
    Sleep(INFINITE);
  if (!strcmp(what, "print")) {
    static const char raw[] = "[printed through the Windows handle]";
    DWORD written = 0;
    fputs("[printed through the C library]", stdout);
    fflush(stdout);
    WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), raw, sizeof(raw) - 1, &written,
              nullptr);
  }
}

// The editor's view. The plugin it belongs to outlives it.
class View final : public IPlugView, public IPlugViewContentScaleSupport {
 public:
  View(void *plugin, const TestEditorCalls *calls) {
    editor_.scale = 1;
    editor_.calls = calls;
    editor_.plugin = plugin;
  }

  tresult PLUGIN_API queryInterface(const TUID iid, void **obj) override {
    *obj = nullptr;
    if (is(iid, IPlugView_iid) || is(iid, FUnknown_iid))
      *obj = static_cast<IPlugView *>(this);
    else if (is(iid, IPlugViewContentScaleSupport_iid))
      *obj = static_cast<IPlugViewContentScaleSupport *>(this);
    if (!*obj)
      return kNoInterface;
    addRef();
    return kResultOk;
  }
  uint32 PLUGIN_API addRef() override { return ++refs_; }
  uint32 PLUGIN_API release() override {
    uint32 left = --refs_;
    if (left == 0)
      delete this;
    return left;
  }

  tresult PLUGIN_API isPlatformTypeSupported(FIDString type) override {
    return type && !strcmp(type, kPlatformTypeHWND) ? kResultTrue : kResultFalse;
  }
  tresult PLUGIN_API attached(void *parent, FIDString type) override {
    if (isPlatformTypeSupported(type) != kResultTrue)
      return kResultFalse;
    return test_editor_create(&editor_, (HWND)parent) ? kResultOk : kResultFalse;
  }
  tresult PLUGIN_API removed() override {
    test_editor_destroy(&editor_);
    return kResultOk;
  }
  tresult PLUGIN_API onWheel(float) override { return kResultFalse; }
  tresult PLUGIN_API onKeyDown(char16, int16, int16) override { return kResultFalse; }
  tresult PLUGIN_API onKeyUp(char16, int16, int16) override { return kResultFalse; }
  tresult PLUGIN_API getSize(ViewRect *size) override {
    int width, height;
    test_editor_wanted(&editor_, &width, &height);
    *size = ViewRect(0, 0, width, height);
    return kResultOk;
  }
  tresult PLUGIN_API onSize(ViewRect *size) override {
    test_editor_set_size(&editor_, size->getWidth(), size->getHeight());
    return kResultOk;
  }
  tresult PLUGIN_API onFocus(TBool) override { return kResultOk; }
  tresult PLUGIN_API setFrame(IPlugFrame *frame) override {
    frame_ = frame;
    return kResultOk;
  }
  tresult PLUGIN_API canResize() override {
    return test_editor_fixed() ? kResultFalse : kResultTrue;
  }
  tresult PLUGIN_API checkSizeConstraint(ViewRect *rect) override {
    int width = rect->getWidth(), height = rect->getHeight();
    test_editor_fit(&width, &height);
    rect->right = rect->left + width;
    rect->bottom = rect->top + height;
    return kResultOk;
  }
  tresult PLUGIN_API setContentScaleFactor(ScaleFactor factor) override {
    editor_.scale = factor;
    return kResultOk;
  }

  // The host resizes the view itself when it agrees.
  bool ask_size(int width, int height) {
    ViewRect wanted(0, 0, width, height);
    return frame_ && frame_->resizeView(this, &wanted) == kResultOk;
  }

 private:
  uint32 refs_ = 1;
  TestEditor editor_ = {};
  IPlugFrame *frame_ = nullptr;
};

class Plugin final : public IComponent, public IAudioProcessor, public IEditController {
 public:
  explicit Plugin(int kind) : kind_(kind) {
    values_[PARAM_GAIN] = 0.5;
    values_[PARAM_LEVEL] = 0.5;
  }

  tresult PLUGIN_API queryInterface(const TUID iid, void **obj) override {
    *obj = nullptr;
    if (is(iid, IComponent_iid) || is(iid, IPluginBase_iid) || is(iid, FUnknown_iid))
      *obj = static_cast<IComponent *>(this);
    else if (is(iid, IAudioProcessor_iid))
      *obj = static_cast<IAudioProcessor *>(this);
    else if (is(iid, IEditController_iid))
      *obj = static_cast<IEditController *>(this);
    if (!*obj)
      return kNoInterface;
    addRef();
    return kResultOk;
  }
  uint32 PLUGIN_API addRef() override { return ++refs_; }
  uint32 PLUGIN_API release() override {
    uint32 left = --refs_;
    if (left == 0)
      delete this;
    return left;
  }

  // The component.
  tresult PLUGIN_API initialize(FUnknown *) override { return kResultOk; }
  tresult PLUGIN_API terminate() override { return kResultOk; }
  tresult PLUGIN_API getControllerClassId(TUID) override { return kNotImplemented; }
  tresult PLUGIN_API setIoMode(IoMode) override { return kResultOk; }
  int32 PLUGIN_API getBusCount(MediaType type, BusDirection direction) override {
    if (type != kAudio)
      return 0;
    return direction == kInput && kind_ == KIND_FIXED ? 2 : 1;
  }
  tresult PLUGIN_API getBusInfo(MediaType type, BusDirection direction, int32 index,
                                BusInfo &info) override {
    if (index < 0 || index >= getBusCount(type, direction))
      return kInvalidArgument;
    info = {};
    info.mediaType = kAudio;
    info.direction = direction;
    info.channelCount = index == 0 ? width_ : 2;
    info.busType = index == 0 ? kMain : kAux;
    info.flags = index == 0 ? BusInfo::kDefaultActive : 0;
    return kResultOk;
  }
  tresult PLUGIN_API getRoutingInfo(RoutingInfo &, RoutingInfo &) override { return kNotImplemented; }
  tresult PLUGIN_API activateBus(MediaType, BusDirection, int32, TBool) override { return kResultOk; }
  tresult PLUGIN_API setActive(TBool state) override {
    if (state)
      ++activations_;
    return kResultOk;
  }
  tresult PLUGIN_API setState(IBStream *) override { return kResultOk; }
  tresult PLUGIN_API getState(IBStream *) override { return kNotImplemented; }

  // The processor.
  tresult PLUGIN_API setBusArrangements(SpeakerArrangement *inputs, int32 input_count,
                                        SpeakerArrangement *outputs,
                                        int32 output_count) override {
    if (kind_ != KIND_GAIN || input_count != 1 || output_count != 1 ||
        inputs[0] != outputs[0])
      return kResultFalse;
    if (inputs[0] != SpeakerArr::kMono && inputs[0] != SpeakerArr::kStereo)
      return kResultFalse;
    width_ = inputs[0] == SpeakerArr::kMono ? 1 : 2;
    return kResultOk;
  }
  tresult PLUGIN_API getBusArrangement(BusDirection, int32 index,
                                       SpeakerArrangement &arrangement) override {
    arrangement = index == 0 && width_ == 1 ? SpeakerArr::kMono : SpeakerArr::kStereo;
    return kResultOk;
  }
  tresult PLUGIN_API canProcessSampleSize(int32 size) override {
    return size == kSample32 ? kResultTrue : kResultFalse;
  }
  uint32 PLUGIN_API getLatencySamples() override {
    if (kind_ == KIND_LATENCY)
      return 480;
    if (kind_ == KIND_RESTART)
      return activations_ > 1 ? 300 : 100;
    return 0;
  }
  tresult PLUGIN_API setupProcessing(ProcessSetup &) override { return kResultOk; }
  tresult PLUGIN_API setProcessing(TBool) override { return kResultOk; }
  uint32 PLUGIN_API getTailSamples() override { return 0; }
  tresult PLUGIN_API process(ProcessData &data) override {
    unsigned block = ++blocks_;
    int32 inputs = kind_ == KIND_FIXED ? 2 : 1;
    if (kind_ == KIND_REFUSE || data.numInputs != inputs || data.numOutputs != 1 ||
        data.symbolicSampleSize != kSample32)
      return kResultFalse;
    if (kind_ == KIND_RESTART && block == RESTART_BLOCK && handler_)
      handler_->restartComponent(kReloadComponent);
    if (IParameterChanges *changes = data.inputParameterChanges)
      for (int32 i = 0; i < changes->getParameterCount(); ++i) {
        IParamValueQueue *queue = changes->getParameterData(i);
        int32 offset;
        ParamValue value;
        if (queue && queue->getParameterId() == PARAM_GAIN &&
            queue->getPoint(queue->getPointCount() - 1, offset, value) == kResultOk)
          gain_ = (float)value;
      }
    const AudioBusBuffers &in = data.inputs[0], &out = data.outputs[0];
    for (int32 c = 0; c < out.numChannels; ++c)
      for (int32 n = 0; n < data.numSamples; ++n) {
        float sample = in.channelBuffers32[c][n] * gain_;
        // The host gives a bus it has no use for silence, so this adds 0.
        if (kind_ == KIND_FIXED)
          sample += data.inputs[1].channelBuffers32[c][n];
        out.channelBuffers32[c][n] = sample;
      }
    if (kind_ == KIND_PARAMS && data.outputParameterChanges) {
      const ParamID level = PARAM_LEVEL;
      int32 index;
      IParamValueQueue *queue = data.outputParameterChanges->addParameterData(level, index);
      if (queue)
        queue->addPoint(0, gain_, index);
    }
    return kResultOk;
  }

  // The controller.
  tresult PLUGIN_API setComponentState(IBStream *) override { return kResultOk; }
  int32 PLUGIN_API getParameterCount() override {
    if (kind_ == KIND_GAIN)
      scan_trouble();
    return kind_ == KIND_PARAMS ? PARAMS : 0;
  }
  tresult PLUGIN_API getParameterInfo(int32 index, ParameterInfo &info) override {
    if (index < 0 || index >= getParameterCount())
      return kInvalidArgument;
    static const char16 *const titles[PARAMS] = {u"Gain", u"Move", u"Level", u"Hidden",
                                                  u"Bypass"};
    info = {};
    info.id = (ParamID)index + 1;
    for (int i = 0; titles[index][i]; ++i)
      info.title[i] = titles[index][i];
    info.defaultNormalizedValue = info.id == PARAM_GAIN || info.id == PARAM_LEVEL ? 0.5 : 0;
    switch (info.id) {
      case PARAM_LEVEL: info.flags = ParameterInfo::kIsReadOnly; break;
      case PARAM_HIDDEN: info.flags = ParameterInfo::kIsHidden; break;
      case PARAM_BYPASS:
        info.stepCount = 1;
        info.flags = ParameterInfo::kCanAutomate | ParameterInfo::kIsBypass;
        break;
      default: info.flags = ParameterInfo::kCanAutomate;
    }
    return kResultOk;
  }
  tresult PLUGIN_API getParamStringByValue(ParamID, ParamValue, String128) override {
    return kNotImplemented;
  }
  tresult PLUGIN_API getParamValueByString(ParamID, TChar *, ParamValue &) override {
    return kNotImplemented;
  }
  ParamValue PLUGIN_API normalizedParamToPlain(ParamID id, ParamValue value) override {
    return id == PARAM_LEVEL ? value * 2 : value;
  }
  ParamValue PLUGIN_API plainParamToNormalized(ParamID id, ParamValue value) override {
    return id == PARAM_LEVEL ? value / 2 : value;
  }
  ParamValue PLUGIN_API getParamNormalized(ParamID id) override {
    return id >= 1 && id <= PARAMS ? values_[id] : 0;
  }
  tresult PLUGIN_API setParamNormalized(ParamID id, ParamValue value) override {
    if (id < 1 || id > PARAMS)
      return kInvalidArgument;
    values_[id] = value;
    if (id == PARAM_MOVE && value != 0 && handler_) {
      values_[PARAM_GAIN] = value;
      handler_->beginEdit(PARAM_GAIN);
      handler_->performEdit(PARAM_GAIN, value);
      handler_->endEdit(PARAM_GAIN);
    }
    return kResultOk;
  }
  tresult PLUGIN_API setComponentHandler(IComponentHandler *handler) override {
    if (handler_)
      handler_->release();
    handler_ = handler;
    if (handler_)
      handler_->addRef();
    return kResultOk;
  }
  IPlugView *PLUGIN_API createView(FIDString) override {
    static const TestEditorCalls calls = {edit_gain, ask_size, nullptr};
    if (kind_ != KIND_PARAMS)
      return nullptr;
    view_ = new View(this, &calls);
    return view_;
  }

 private:
  // The editor moved the gain, on the main thread: an edit like any other.
  static void edit_gain(void *plugin, float gain) {
    auto *p = static_cast<Plugin *>(plugin);
    p->values_[PARAM_GAIN] = gain;
    if (!p->handler_)
      return;
    p->handler_->beginEdit(PARAM_GAIN);
    p->handler_->performEdit(PARAM_GAIN, gain);
    p->handler_->endEdit(PARAM_GAIN);
  }
  static bool ask_size(void *plugin, int width, int height) {
    auto *p = static_cast<Plugin *>(plugin);
    return p->view_ && p->view_->ask_size(width, height);
  }

  View *view_ = nullptr;  // the view last made, which only an open editor asks through

  const int kind_;
  uint32 refs_ = 1;
  int32 width_ = 2;
  unsigned activations_ = 0;  // main thread
  unsigned blocks_ = 0;       // audio thread
  float gain_ = 0.5f;         // audio thread
  ParamValue values_[PARAMS + 1] = {};  // main thread: the controller's own
  IComponentHandler *handler_ = nullptr;
};

class Factory final : public IPluginFactory2 {
 public:
  tresult PLUGIN_API queryInterface(const TUID iid, void **obj) override {
    *obj = is(iid, IPluginFactory2_iid) || is(iid, IPluginFactory_iid) ||
                   is(iid, FUnknown_iid)
               ? static_cast<IPluginFactory2 *>(this)
               : nullptr;
    return *obj ? kResultOk : kNoInterface;
  }
  uint32 PLUGIN_API addRef() override { return 1; }
  uint32 PLUGIN_API release() override { return 1; }
  tresult PLUGIN_API getFactoryInfo(PFactoryInfo *info) override {
    *info = {};
    strcpy(info->vendor, "OpenXLR");
    return kResultOk;
  }
  int32 PLUGIN_API countClasses() override { return KINDS; }
  tresult PLUGIN_API getClassInfo(int32 index, PClassInfo *info) override {
    if (index < 0 || index >= KINDS)
      return kInvalidArgument;
    *info = {};
    memcpy(info->cid, class_id(index).bytes, sizeof(TUID));
    info->cardinality = PClassInfo::kManyInstances;
    strcpy(info->category, kVstAudioEffectClass);
    strcpy(info->name, names[index]);
    return kResultOk;
  }
  // The vendor and the sub-categories a scan reads.
  tresult PLUGIN_API getClassInfo2(int32 index, PClassInfo2 *info) override {
    if (index < 0 || index >= KINDS)
      return kInvalidArgument;
    *info = {};
    memcpy(info->cid, class_id(index).bytes, sizeof(TUID));
    info->cardinality = PClassInfo::kManyInstances;
    strcpy(info->category, kVstAudioEffectClass);
    strcpy(info->name, names[index]);
    strcpy(info->subCategories, "Fx|Dynamics");
    strcpy(info->vendor, "OpenXLR");
    return kResultOk;
  }
  tresult PLUGIN_API createInstance(FIDString cid, FIDString iid, void **obj) override {
    *obj = nullptr;
    for (int kind = 0; kind < KINDS; ++kind) {
      if (!is(cid, class_id(kind).bytes))
        continue;
      auto *plugin = new Plugin(kind);
      tresult result = plugin->queryInterface(iid, obj);
      plugin->release();
      return result;
    }
    return kNoInterface;
  }
};

Factory factory;

}  // namespace

extern "C" {
__declspec(dllexport) bool InitDll() {
  entered = true;
  return true;
}
__declspec(dllexport) bool ExitDll() { return true; }
__declspec(dllexport) IPluginFactory *PLUGIN_API GetPluginFactory() {
  return entered ? &factory : nullptr;
}
}
