// SPDX-License-Identifier: GPL-3.0-only
// The VST3 backend: a component and its controller, the host objects a
// plugin expects from us, its parameters as controls, and its X11 editor
// through a run loop of ours. Also the scanner that describes a bundle for
// the daemon's catalogue, in a process of its own.
//
// VST3 speaks a COM-like ABI: every object is a table of virtual methods
// behind FUnknown, found by a 16-byte id. The interface headers are
// vendored under vst3/ (MIT); only their inline parts are used, so nothing
// of the SDK is compiled here.
#include "host.h"

#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <dirent.h>
#include <dlfcn.h>
#include <map>
#include <string>
#include <sys/stat.h>
#include <vector>

#include <pluginterfaces/base/ibstream.h>
#include <pluginterfaces/base/ipluginbase.h>
#include <pluginterfaces/gui/iplugview.h>
#include <pluginterfaces/vst/ivstattributes.h>
#include <pluginterfaces/vst/ivstaudioprocessor.h>
#include <pluginterfaces/vst/ivstcomponent.h>
#include <pluginterfaces/vst/ivsteditcontroller.h>
#include <pluginterfaces/vst/ivsthostapplication.h>
#include <pluginterfaces/vst/ivstmessage.h>
#include <pluginterfaces/vst/ivstparameterchanges.h>
#include <pluginterfaces/vst/ivstpluginterfacesupport.h>
#include <pluginterfaces/vst/vstspeaker.h>

extern "C" {
#include <pipewire/pipewire.h>
#include <spa/utils/defs.h>
}

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace {

// OPENXLR_HOST_TRACE in the environment: describe buses and the first
// cycles on stderr. Read once, since the audio thread may not ask.
bool trace_enabled = getenv("OPENXLR_HOST_TRACE") != nullptr;

enum { MAX_TIMERS = 32, MAX_FDS = 32 };

bool same_iid(const TUID iid, const TUID other) {
  return FUnknownPrivate::iidEqual(iid, other);
}

// The interface ids, as the headers define them per translation unit.
#define IID(Interface) (Interface##_iid)

// --- strings ----------------------------------------------------------------

// String128 is UTF-16; the daemon and the catalogue speak UTF-8.
std::string utf8(const char16* s) {
  std::string out;
  for (; s && *s; ++s) {
    uint32_t c = *s;
    if (c >= 0xD800 && c <= 0xDBFF && s[1] >= 0xDC00 && s[1] <= 0xDFFF) {
      c = 0x10000 + ((c - 0xD800) << 10) + (s[1] - 0xDC00);
      ++s;
    }
    if (c < 0x80)
      out += (char)c;
    else if (c < 0x800) {
      out += (char)(0xC0 | (c >> 6));
      out += (char)(0x80 | (c & 0x3F));
    } else if (c < 0x10000) {
      out += (char)(0xE0 | (c >> 12));
      out += (char)(0x80 | ((c >> 6) & 0x3F));
      out += (char)(0x80 | (c & 0x3F));
    } else {
      out += (char)(0xF0 | (c >> 18));
      out += (char)(0x80 | ((c >> 12) & 0x3F));
      out += (char)(0x80 | ((c >> 6) & 0x3F));
      out += (char)(0x80 | (c & 0x3F));
    }
  }
  return out;
}

std::string hex_of(const TUID id) {
  static const char digits[] = "0123456789ABCDEF";
  std::string out;
  for (int i = 0; i < 16; ++i) {
    out += digits[((unsigned char)id[i]) >> 4];
    out += digits[((unsigned char)id[i]) & 15];
  }
  return out;
}

bool tuid_of(const char *hex, TUID out) {
  if (!hex || strlen(hex) != 32)
    return false;
  for (int i = 0; i < 16; ++i) {
    unsigned byte;
    if (sscanf(hex + 2 * i, "%2x", &byte) != 1)
      return false;
    out[i] = (char)byte;
  }
  return true;
}

void json_string(const char *s) {
  putchar('"');
  for (; s && *s; ++s) {
    unsigned char ch = (unsigned char)*s;
    if (ch == '"' || ch == '\\')
      printf("\\%c", ch);
    else if (ch < 0x20)
      printf("\\u%04x", ch);
    else
      putchar(ch);
  }
  putchar('"');
}

// --- host objects -------------------------------------------------------------

// Objects the host hands to plugins live as long as the process; their
// reference counts are decoration. Messages and attribute lists, which
// plugins create through us and release, are counted for real.
#define HOST_OWNED_REFCOUNT()                                                  \
  uint32 PLUGIN_API addRef() override { return 1; }                            \
  uint32 PLUGIN_API release() override { return 1; }

#define QUERY(Interface)                                                       \
  if (same_iid(iid, IID(Interface))) {                                         \
    *obj = static_cast<Interface *>(this);                                     \
    addRef();                                                                  \
    return kResultOk;                                                          \
  }

class AttributeList final : public IAttributeList {
 public:
  tresult PLUGIN_API queryInterface(const TUID iid, void **obj) override {
    QUERY(IAttributeList)
    if (same_iid(iid, IID(FUnknown))) {
      *obj = static_cast<FUnknown *>(this);
      addRef();
      return kResultOk;
    }
    *obj = nullptr;
    return kNoInterface;
  }
  uint32 PLUGIN_API addRef() override { return ++refs_; }
  uint32 PLUGIN_API release() override {
    uint32 left = --refs_;
    if (left == 0)
      delete this;
    return left;
  }
  tresult PLUGIN_API setInt(AttrID id, int64 value) override {
    values_[id] = Value{Value::Int, value, 0, {}, {}};
    return kResultOk;
  }
  tresult PLUGIN_API getInt(AttrID id, int64 &value) override {
    auto it = values_.find(id);
    if (it == values_.end() || it->second.kind != Value::Int)
      return kResultFalse;
    value = it->second.integer;
    return kResultOk;
  }
  tresult PLUGIN_API setFloat(AttrID id, double value) override {
    values_[id] = Value{Value::Float, 0, value, {}, {}};
    return kResultOk;
  }
  tresult PLUGIN_API getFloat(AttrID id, double &value) override {
    auto it = values_.find(id);
    if (it == values_.end() || it->second.kind != Value::Float)
      return kResultFalse;
    value = it->second.real;
    return kResultOk;
  }
  tresult PLUGIN_API setString(AttrID id, const TChar *string) override {
    std::u16string text;
    for (; string && *string; ++string)
      text += *string;
    values_[id] = Value{Value::String, 0, 0, text, {}};
    return kResultOk;
  }
  tresult PLUGIN_API getString(AttrID id, TChar *string,
                               uint32 sizeInBytes) override {
    auto it = values_.find(id);
    if (it == values_.end() || it->second.kind != Value::String)
      return kResultFalse;
    size_t room = sizeInBytes / sizeof(TChar);
    if (room == 0)
      return kResultFalse;
    size_t count = std::min(it->second.text.size(), room - 1);
    memcpy(string, it->second.text.data(), count * sizeof(TChar));
    string[count] = 0;
    return kResultOk;
  }
  tresult PLUGIN_API setBinary(AttrID id, const void *data,
                               uint32 sizeInBytes) override {
    const auto *bytes = static_cast<const uint8_t *>(data);
    values_[id] = Value{Value::Binary, 0, 0, {},
                        std::vector<uint8_t>(bytes, bytes + sizeInBytes)};
    return kResultOk;
  }
  tresult PLUGIN_API getBinary(AttrID id, const void *&data,
                               uint32 &sizeInBytes) override {
    auto it = values_.find(id);
    if (it == values_.end() || it->second.kind != Value::Binary)
      return kResultFalse;
    data = it->second.bytes.data();
    sizeInBytes = (uint32)it->second.bytes.size();
    return kResultOk;
  }

 private:
  struct Value {
    enum Kind { Int, Float, String, Binary } kind;
    int64 integer;
    double real;
    std::u16string text;
    std::vector<uint8_t> bytes;
  };
  std::map<std::string, Value> values_;
  std::atomic<uint32> refs_{1};
};

class Message final : public IMessage {
 public:
  tresult PLUGIN_API queryInterface(const TUID iid, void **obj) override {
    QUERY(IMessage)
    if (same_iid(iid, IID(FUnknown))) {
      *obj = static_cast<FUnknown *>(this);
      addRef();
      return kResultOk;
    }
    *obj = nullptr;
    return kNoInterface;
  }
  uint32 PLUGIN_API addRef() override { return ++refs_; }
  uint32 PLUGIN_API release() override {
    uint32 left = --refs_;
    if (left == 0)
      delete this;
    return left;
  }
  FIDString PLUGIN_API getMessageID() override { return id_.c_str(); }
  void PLUGIN_API setMessageID(FIDString id) override { id_ = id ? id : ""; }
  IAttributeList *PLUGIN_API getAttributes() override { return &attributes_; }

 private:
  std::string id_;
  AttributeList attributes_;  // lives and dies with the message
  std::atomic<uint32> refs_{1};
};

struct Vst3;

// What every plugin asks the host for first: a name, and the objects it
// sends between its two halves.
class HostApplication final : public IHostApplication,
                              public IPlugInterfaceSupport {
 public:
  tresult PLUGIN_API queryInterface(const TUID iid, void **obj) override {
    QUERY(IHostApplication)
    QUERY(IPlugInterfaceSupport)
    if (same_iid(iid, IID(FUnknown))) {
      *obj = static_cast<FUnknown *>(static_cast<IHostApplication *>(this));
      return kResultOk;
    }
    *obj = nullptr;
    return kNoInterface;
  }
  HOST_OWNED_REFCOUNT()
  tresult PLUGIN_API getName(String128 name) override {
    static const char16 text[] = u"OpenXLR";
    memcpy(name, text, sizeof(text));
    return kResultOk;
  }
  tresult PLUGIN_API createInstance(TUID cid, TUID iid, void **obj) override {
    if (same_iid(cid, IID(IMessage)) && same_iid(iid, IID(IMessage))) {
      *obj = static_cast<IMessage *>(new Message());
      return kResultOk;
    }
    if (same_iid(cid, IID(IAttributeList)) &&
        same_iid(iid, IID(IAttributeList))) {
      *obj = static_cast<IAttributeList *>(new AttributeList());
      return kResultOk;
    }
    *obj = nullptr;
    return kResultFalse;
  }
  tresult PLUGIN_API isPlugInterfaceSupported(const TUID iid) override {
    return same_iid(iid, IID(IComponent)) || same_iid(iid, IID(IEditController)) ||
                   same_iid(iid, IID(IAudioProcessor)) ||
                   same_iid(iid, IID(IConnectionPoint)) ||
                   same_iid(iid, IID(IPlugView))
               ? kResultTrue
               : kResultFalse;
  }
};

// A parameter change from the plugin's editor, or its answer to one of ours.
struct Record {
  ParamID id;
  Control *ctl;
  bool readonly;
  std::atomic<double> normalized_desired;  // main thread -> audio thread
  std::atomic<double> normalized_observed; // audio thread -> main thread
  double last_sent;   // audio thread: what the processor was last told
  float last_plain;   // main thread: what the daemon last asked for
  bool seen;          // main thread: last_plain has been set once
};

// One parameter's changes for one process call: at most one point, at
// sample zero, which is all a control change from outside needs.
class ValueQueue final : public IParamValueQueue {
 public:
  tresult PLUGIN_API queryInterface(const TUID iid, void **obj) override {
    QUERY(IParamValueQueue)
    *obj = nullptr;
    return kNoInterface;
  }
  HOST_OWNED_REFCOUNT()
  ParamID PLUGIN_API getParameterId() override { return id; }
  int32 PLUGIN_API getPointCount() override { return count; }
  tresult PLUGIN_API getPoint(int32 index, int32 &sampleOffset,
                              ParamValue &value_) override {
    if (index != 0 || count == 0)
      return kResultFalse;
    sampleOffset = 0;
    value_ = value;
    return kResultOk;
  }
  tresult PLUGIN_API addPoint(int32 sampleOffset, ParamValue value_,
                              int32 &index) override {
    value = value_;
    count = 1;
    index = 0;
    return kResultOk;
  }
  ParamID id = 0;
  ParamValue value = 0;
  int32 count = 0;
};

class ParameterChanges final : public IParameterChanges {
 public:
  tresult PLUGIN_API queryInterface(const TUID iid, void **obj) override {
    QUERY(IParameterChanges)
    *obj = nullptr;
    return kNoInterface;
  }
  HOST_OWNED_REFCOUNT()
  int32 PLUGIN_API getParameterCount() override { return used; }
  IParamValueQueue *PLUGIN_API getParameterData(int32 index) override {
    return index < used ? &queues[index] : nullptr;
  }
  IParamValueQueue *PLUGIN_API addParameterData(const ParamID &id,
                                                int32 &index) override {
    for (int32 i = 0; i < used; ++i)
      if (queues[i].id == id) {
        index = i;
        return &queues[i];
      }
    if (used == (int32)queues.size())
      return nullptr;
    ValueQueue &q = queues[used];
    q.id = id;
    q.count = 0;
    index = used++;
    return &q;
  }
  void clear() { used = 0; }
  void reserve(size_t n) { queues.resize(n); }
  std::vector<ValueQueue> queues;
  int32 used = 0;
};

// Memory behind getState and setComponentState.
class MemoryStream final : public IBStream {
 public:
  tresult PLUGIN_API queryInterface(const TUID iid, void **obj) override {
    QUERY(IBStream)
    *obj = nullptr;
    return kNoInterface;
  }
  HOST_OWNED_REFCOUNT()
  tresult PLUGIN_API read(void *buffer, int32 numBytes,
                          int32 *numBytesRead) override {
    int32 left = (int32)bytes.size() - (int32)position;
    int32 count = numBytes < left ? numBytes : (left > 0 ? left : 0);
    memcpy(buffer, bytes.data() + position, (size_t)count);
    position += (size_t)count;
    if (numBytesRead)
      *numBytesRead = count;
    return kResultOk;
  }
  tresult PLUGIN_API write(void *buffer, int32 numBytes,
                           int32 *numBytesWritten) override {
    if (numBytes < 0)
      return kResultFalse;
    if (position + (size_t)numBytes > bytes.size())
      bytes.resize(position + (size_t)numBytes);
    memcpy(bytes.data() + position, buffer, (size_t)numBytes);
    position += (size_t)numBytes;
    if (numBytesWritten)
      *numBytesWritten = numBytes;
    return kResultOk;
  }
  tresult PLUGIN_API seek(int64 pos, int32 mode, int64 *result) override {
    int64 base = mode == kIBSeekSet ? 0 : mode == kIBSeekCur ? (int64)position
                                                              : (int64)bytes.size();
    int64 target = base + pos;
    if (target < 0)
      return kResultFalse;
    position = (size_t)target;
    if (result)
      *result = target;
    return kResultOk;
  }
  tresult PLUGIN_API tell(int64 *pos) override {
    if (pos)
      *pos = (int64)position;
    return kResultOk;
  }
  std::vector<uint8_t> bytes;
  size_t position = 0;
};

struct Timer {
  Vst3 *v = nullptr;
  Linux::ITimerHandler *handler = nullptr;
  struct spa_source *source = nullptr;
};

struct Fd {
  Vst3 *v = nullptr;
  Linux::IEventHandler *handler = nullptr;
  int fd = -1;
  struct spa_source *source = nullptr;
};

class ComponentHandler;
class PlugFrame;

struct Vst3 {
  Host *h = nullptr;
  void *library = nullptr;
  bool (*module_exit)() = nullptr;
  IPluginFactory *factory = nullptr;
  IComponent *component = nullptr;
  IAudioProcessor *processor = nullptr;
  IEditController *controller = nullptr;
  bool controller_is_component = false;
  IConnectionPoint *component_point = nullptr, *controller_point = nullptr;
  HostApplication application;
  ComponentHandler *handler = nullptr;
  PlugFrame *frame = nullptr;
  std::vector<Record *> records;
  ParameterChanges in_changes, out_changes;
  // One entry per bus in each direction, active or not: a plugin indexes
  // its sidechain by bus number and must find something there.
  std::vector<AudioBusBuffers> in_buses, out_buses;
  int32 main_in = 0, main_out = 0;
  float *in_ptrs[MAX_CHANNELS] = {}, *out_ptrs[MAX_CHANNELS] = {};
  // The side buses are off, but a plugin may touch their buffers anyway, so
  // each gets real ones: silence to read, scratch to write.
  std::vector<std::vector<float>> side_buffers;
  std::vector<std::vector<float *>> side_ptrs;
  ProcessData data;
  bool active = false, processing = false;
  IPlugView *view = nullptr;
  Timer timers[MAX_TIMERS];
  Fd fds[MAX_FDS];
  std::atomic<bool> restart_requested{false}, values_changed{false};
};

// Where the plugin's editor reports its edits, on the main thread.
class ComponentHandler final : public IComponentHandler,
                               public IComponentHandler2 {
 public:
  explicit ComponentHandler(Vst3 *v) : v_(v) {}
  tresult PLUGIN_API queryInterface(const TUID iid, void **obj) override {
    QUERY(IComponentHandler)
    QUERY(IComponentHandler2)
    if (same_iid(iid, IID(FUnknown))) {
      *obj = static_cast<FUnknown *>(static_cast<IComponentHandler *>(this));
      return kResultOk;
    }
    *obj = nullptr;
    return kNoInterface;
  }
  HOST_OWNED_REFCOUNT()
  tresult PLUGIN_API beginEdit(ParamID) override { return kResultOk; }
  tresult PLUGIN_API performEdit(ParamID id, ParamValue normalized) override;
  tresult PLUGIN_API endEdit(ParamID) override { return kResultOk; }
  tresult PLUGIN_API restartComponent(int32 flags) override {
    if (flags & (RestartFlags::kReloadComponent | RestartFlags::kIoChanged |
                 RestartFlags::kLatencyChanged))
      v_->restart_requested = true;
    if (flags & RestartFlags::kParamValuesChanged)
      v_->values_changed = true;
    return kResultOk;
  }
  tresult PLUGIN_API setDirty(TBool) override { return kResultOk; }
  tresult PLUGIN_API requestOpenEditor(FIDString) override {
    return kNotImplemented;
  }
  tresult PLUGIN_API startGroupEdit() override { return kResultOk; }
  tresult PLUGIN_API finishGroupEdit() override { return kResultOk; }

 private:
  Vst3 *v_;
};

// The window the editor lives in, and the run loop it drives its file
// descriptors and timers from. Both sit on PipeWire's main loop.
class PlugFrame final : public IPlugFrame, public Linux::IRunLoop {
 public:
  explicit PlugFrame(Vst3 *v) : v_(v) {}
  tresult PLUGIN_API queryInterface(const TUID iid, void **obj) override {
    QUERY(IPlugFrame)
    QUERY(Linux::IRunLoop)
    if (same_iid(iid, IID(FUnknown))) {
      *obj = static_cast<FUnknown *>(static_cast<IPlugFrame *>(this));
      return kResultOk;
    }
    *obj = nullptr;
    return kNoInterface;
  }
  HOST_OWNED_REFCOUNT()
  tresult PLUGIN_API resizeView(IPlugView *view, ViewRect *size) override {
    if (!size || size->getWidth() < 1 || size->getHeight() < 1)
      return kResultFalse;
    host_resize_editor(v_->h, (unsigned)size->getWidth(),
                       (unsigned)size->getHeight());
    view->onSize(size);
    return kResultOk;
  }
  tresult PLUGIN_API registerEventHandler(Linux::IEventHandler *handler,
                                          Linux::FileDescriptor fd) override;
  tresult PLUGIN_API unregisterEventHandler(
      Linux::IEventHandler *handler) override;
  tresult PLUGIN_API registerTimer(Linux::ITimerHandler *handler,
                                   Linux::TimerInterval milliseconds) override;
  tresult PLUGIN_API unregisterTimer(Linux::ITimerHandler *handler) override;

 private:
  Vst3 *v_;
};

Vst3 *of(Host *h) { return static_cast<Vst3 *>(host_impl(h)); }

Record *record_for(Vst3 *v, ParamID id) {
  for (Record *r : v->records)
    if (r->id == id)
      return r;
  return nullptr;
}

tresult PLUGIN_API ComponentHandler::performEdit(ParamID id,
                                                 ParamValue normalized) {
  Record *r = record_for(v_, id);
  if (!r || r->readonly)
    return kResultOk;
  r->normalized_desired = normalized;
  float plain = (float)v_->controller->normalizedParamToPlain(id, normalized);
  r->last_plain = plain;
  r->seen = true;
  host_control_moved(v_->h, r->ctl, plain);
  return kResultOk;
}

// --- the run loop -------------------------------------------------------------

void fire_timer(void *p) {
  auto *t = static_cast<Timer *>(p);
  t->handler->onTimer();
}

void timer_fired(void *data, uint64_t) {
  auto *t = static_cast<Timer *>(data);
  if (t->handler)
    host_run_guarded(t->v->h, fire_timer, t);
}

void fire_fd(void *p) {
  auto *f = static_cast<Fd *>(p);
  f->handler->onFDIsSet(f->fd);
}

void fd_ready(void *data, int, uint32_t) {
  auto *f = static_cast<Fd *>(data);
  if (f->handler)
    host_run_guarded(f->v->h, fire_fd, f);
}

tresult PLUGIN_API PlugFrame::registerEventHandler(Linux::IEventHandler *handler,
                                                   Linux::FileDescriptor fd) {
  if (!handler)
    return kInvalidArgument;
  for (Fd &f : v_->fds) {
    if (f.source)
      continue;
    f.v = v_;
    f.handler = handler;
    f.fd = fd;
    f.source = pw_loop_add_io(host_loop(v_->h), fd, SPA_IO_IN | SPA_IO_ERR,
                              false, fd_ready, &f);
    if (!f.source)
      return kResultFalse;
    handler->addRef();
    return kResultOk;
  }
  return kResultFalse;
}

tresult PLUGIN_API PlugFrame::unregisterEventHandler(
    Linux::IEventHandler *handler) {
  for (Fd &f : v_->fds)
    if (f.source && f.handler == handler) {
      pw_loop_destroy_source(host_loop(v_->h), f.source);
      f.source = nullptr;
      f.handler->release();
      f.handler = nullptr;
      return kResultOk;
    }
  return kResultFalse;
}

tresult PLUGIN_API PlugFrame::registerTimer(Linux::ITimerHandler *handler,
                                            Linux::TimerInterval milliseconds) {
  if (!handler)
    return kInvalidArgument;
  for (Timer &t : v_->timers) {
    if (t.source)
      continue;
    t.v = v_;
    t.handler = handler;
    t.source = pw_loop_add_timer(host_loop(v_->h), timer_fired, &t);
    if (!t.source)
      return kResultFalse;
    if (milliseconds == 0)
      milliseconds = 1;
    struct timespec interval = {(time_t)(milliseconds / 1000),
                                (long)(milliseconds % 1000) * 1000000L};
    pw_loop_update_timer(host_loop(v_->h), t.source, &interval, &interval,
                         false);
    handler->addRef();
    return kResultOk;
  }
  return kResultFalse;
}

tresult PLUGIN_API PlugFrame::unregisterTimer(Linux::ITimerHandler *handler) {
  for (Timer &t : v_->timers)
    if (t.source && t.handler == handler) {
      pw_loop_destroy_source(host_loop(v_->h), t.source);
      t.source = nullptr;
      t.handler->release();
      t.handler = nullptr;
      return kResultOk;
    }
  return kResultFalse;
}

// --- the module ---------------------------------------------------------------

// A bundle is a directory with the shared object under Contents/<arch>; an
// older plugin may be the shared object itself.
std::string library_in(const std::string &path) {
  struct stat st;
  if (stat(path.c_str(), &st) != 0)
    return "";
  if (!S_ISDIR(st.st_mode))
    return path;
  std::string dir = path + "/Contents/x86_64-linux";
  DIR *d = opendir(dir.c_str());
  if (!d)
    return "";
  std::string found;
  while (dirent *e = readdir(d)) {
    std::string name = e->d_name;
    if (name.size() > 3 && name.compare(name.size() - 3, 3, ".so") == 0) {
      found = dir + "/" + name;
      break;
    }
  }
  closedir(d);
  return found;
}

bool open_module(Vst3 *v, const char *path) {
  std::string library = library_in(path);
  if (library.empty()) {
    fprintf(stderr, "no plugin library in %s\n", path);
    return false;
  }
  v->library = dlopen(library.c_str(), RTLD_NOW | RTLD_LOCAL);
  if (!v->library) {
    fprintf(stderr, "cannot load %s: %s\n", library.c_str(), dlerror());
    return false;
  }
  auto entry = (bool (*)(void *))dlsym(v->library, "ModuleEntry");
  v->module_exit = (bool (*)())dlsym(v->library, "ModuleExit");
  if (entry && !entry(v->library)) {
    fputs("the module refused to initialise\n", stderr);
    return false;
  }
  auto get_factory = (IPluginFactory * (*)()) dlsym(v->library, "GetPluginFactory");
  v->factory = get_factory ? get_factory() : nullptr;
  if (!v->factory) {
    fputs("the module has no plugin factory\n", stderr);
    return false;
  }
  IPluginFactory3 *factory3 = nullptr;
  if (v->factory->queryInterface(IID(IPluginFactory3), (void **)&factory3) ==
          kResultOk &&
      factory3) {
    factory3->setHostContext(static_cast<IHostApplication *>(&v->application));
    factory3->release();
  }
  return true;
}

// Create the component and its controller, wire them, and read what they
// are. Shared by loading and scanning.
bool instantiate(Vst3 *v, const TUID cid) {
  if (v->factory->createInstance(cid, IID(IComponent),
                                 (void **)&v->component) != kResultOk ||
      !v->component) {
    fputs("the plugin could not be created\n", stderr);
    return false;
  }
  for (int32 i = 0; i < v->factory->countClasses(); ++i) {
    PClassInfo info;
    if (v->factory->getClassInfo(i, &info) == kResultOk &&
        memcmp(info.cid, cid, sizeof(TUID)) == 0) {
      host_set_plugin_name(v->h, info.name);
      break;
    }
  }
  IHostApplication *context = &v->application;
  if (v->component->initialize(context) != kResultOk) {
    fputs("the plugin refused to initialise\n", stderr);
    return false;
  }
  if (v->component->queryInterface(IID(IAudioProcessor),
                                   (void **)&v->processor) != kResultOk ||
      !v->processor) {
    fputs("the plugin does not process audio\n", stderr);
    return false;
  }
  // The controller is a second object, or the same one wearing both hats.
  TUID controller_id;
  if (v->component->getControllerClassId(controller_id) == kResultOk &&
      v->factory->createInstance(controller_id, IID(IEditController),
                                 (void **)&v->controller) == kResultOk &&
      v->controller) {
    if (v->controller->initialize(context) != kResultOk) {
      fputs("the plugin's controller refused to initialise\n", stderr);
      return false;
    }
  } else if (v->component->queryInterface(IID(IEditController),
                                          (void **)&v->controller) == kResultOk &&
             v->controller) {
    v->controller_is_component = true;
  } else {
    fputs("the plugin has no controller\n", stderr);
    return false;
  }
  if (!v->controller_is_component) {
    v->component->queryInterface(IID(IConnectionPoint),
                                 (void **)&v->component_point);
    v->controller->queryInterface(IID(IConnectionPoint),
                                  (void **)&v->controller_point);
    if (v->component_point && v->controller_point) {
      v->component_point->connect(v->controller_point);
      v->controller_point->connect(v->component_point);
    }
    // The controller shows the component's state; start them in step.
    MemoryStream state;
    if (v->component->getState(&state) == kResultOk) {
      state.position = 0;
      v->controller->setComponentState(&state);
    }
  }
  v->handler = new ComponentHandler(v);
  v->controller->setComponentHandler(v->handler);
  return true;
}

// The main bus in one direction: the first marked main, else the first.
int32 main_bus(Vst3 *v, BusDirection direction) {
  int32 count = v->component->getBusCount(kAudio, direction);
  for (int32 i = 0; i < count; ++i) {
    BusInfo info;
    if (v->component->getBusInfo(kAudio, direction, i, info) == kResultOk &&
        info.busType == kMain)
      return i;
  }
  return 0;
}

int main_bus_channels(Vst3 *v, BusDirection direction) {
  BusInfo info;
  if (v->component->getBusCount(kAudio, direction) == 0 ||
      v->component->getBusInfo(kAudio, direction, main_bus(v, direction),
                               info) != kResultOk)
    return 0;
  return info.channelCount;
}

bool editor_available(Vst3 *v) {
  IPlugView *view = v->controller->createView(ViewType::kEditor);
  if (!view)
    return false;
  bool ok = view->isPlatformTypeSupported(kPlatformTypeX11EmbedWindowID) ==
            kResultTrue;
  view->release();
  return ok;
}

void release_plugin(Vst3 *v) {
  if (v->component_point && v->controller_point) {
    v->component_point->disconnect(v->controller_point);
    v->controller_point->disconnect(v->component_point);
  }
  if (v->controller_point)
    v->controller_point->release();
  if (v->component_point)
    v->component_point->release();
  if (v->controller) {
    v->controller->setComponentHandler(nullptr);
    if (!v->controller_is_component)
      v->controller->terminate();
    v->controller->release();
  }
  if (v->processor)
    v->processor->release();
  if (v->component) {
    v->component->terminate();
    v->component->release();
  }
  v->controller = nullptr;
  v->processor = nullptr;
  v->component = nullptr;
  v->component_point = v->controller_point = nullptr;
  delete v->handler;
  v->handler = nullptr;
}

// --- the backend --------------------------------------------------------------

bool vst3_load(Host *h, char **arguments) {
  auto *v = new Vst3();
  host_set_impl(h, v);
  v->h = h;
  TUID cid;
  if (!tuid_of(arguments[1], cid)) {
    fputs("the plugin id is not a class id\n", stderr);
    return false;
  }
  if (!open_module(v, arguments[0]) || !instantiate(v, cid))
    return false;
  unsigned channels = host_channels(h);
  if (main_bus_channels(v, kInput) != (int)channels ||
      main_bus_channels(v, kOutput) != (int)channels) {
    // Ask for the chain's width; a plugin that can take it says so.
    SpeakerArrangement wanted = channels == 1 ? SpeakerArr::kMono : SpeakerArr::kStereo;
    if (v->processor->setBusArrangements(&wanted, 1, &wanted, 1) != kResultOk ||
        main_bus_channels(v, kInput) != (int)channels ||
        main_bus_channels(v, kOutput) != (int)channels) {
      fputs("the plugin's main buses do not match the chain's channels\n",
            stderr);
      return false;
    }
  }
  if (v->processor->canProcessSampleSize(kSample32) != kResultTrue) {
    fputs("the plugin cannot process 32-bit audio\n", stderr);
    return false;
  }
  // Only the main buses carry audio; everything else stays off.
  v->main_in = main_bus(v, kInput);
  v->main_out = main_bus(v, kOutput);
  for (BusDirection direction : {kInput, kOutput})
    for (int32 i = 0; i < v->component->getBusCount(kAudio, direction); ++i)
      v->component->activateBus(kAudio, direction, i,
                                i == (direction == kInput ? v->main_in : v->main_out));
  for (BusDirection direction : {kInput, kOutput})
    for (int32 i = 0; i < v->component->getBusCount(kEvent, direction); ++i)
      v->component->activateBus(kEvent, direction, i, false);

  int32 count = v->controller->getParameterCount();
  v->in_changes.reserve((size_t)count);
  v->out_changes.reserve((size_t)count);
  for (int32 i = 0; i < count; ++i) {
    ParameterInfo info;
    if (v->controller->getParameterInfo(i, info) != kResultOk ||
        (info.flags & ParameterInfo::kIsHidden))
      continue;
    auto *r = new Record();
    r->id = info.id;
    r->readonly = (info.flags & ParameterInfo::kIsReadOnly) != 0;
    double normalized = v->controller->getParamNormalized(info.id);
    r->normalized_desired = normalized;
    r->normalized_observed = normalized;
    r->last_sent = normalized;
    float plain = (float)v->controller->normalizedParamToPlain(info.id, normalized);
    float minimum = (float)v->controller->normalizedParamToPlain(info.id, 0.0);
    float maximum = (float)v->controller->normalizedParamToPlain(info.id, 1.0);
    if (minimum > maximum)
      std::swap(minimum, maximum);
    char symbol[16];
    snprintf(symbol, sizeof(symbol), "%u", (unsigned)info.id);
    r->ctl = host_add_control(h, strdup(symbol), minimum, maximum, plain,
                              r->readonly, r);
    if (!r->ctl) {
      delete r;
      break;
    }
    r->last_plain = plain;
    r->seen = true;
    v->records.push_back(r);
  }
  v->frame = new PlugFrame(v);
  host_set_has_editor(h, editor_available(v));
  return true;
}

bool vst3_activate(Host *h) {
  Vst3 *v = of(h);
  ProcessSetup setup;
  setup.processMode = kRealtime;
  setup.symbolicSampleSize = kSample32;
  setup.maxSamplesPerBlock = MAX_FRAMES;
  setup.sampleRate = host_rate(h);
  if (v->processor->setupProcessing(setup) != kResultOk) {
    fputs("the plugin refused the processing setup\n", stderr);
    return false;
  }
  if (v->component->setActive(true) != kResultOk) {
    fputs("the plugin refused to activate\n", stderr);
    return false;
  }
  v->active = true;
  if (trace_enabled) {
    for (BusDirection direction : {kInput, kOutput})
      for (int32 i = 0; i < v->component->getBusCount(kAudio, direction); ++i) {
        BusInfo info;
        SpeakerArrangement arrangement = 0;
        v->component->getBusInfo(kAudio, direction, i, info);
        v->processor->getBusArrangement(direction, i, arrangement);
        fprintf(stderr, "trace: %s bus %d: %d channels, type %d, flags %u, arrangement %llx, main=%d\n",
                direction == kInput ? "in" : "out", i, info.channelCount, info.busType, info.flags,
                (unsigned long long)arrangement, i == (direction == kInput ? v->main_in : v->main_out));
      }
    fprintf(stderr, "trace: latency %u, tail %u\n", v->processor->getLatencySamples(), v->processor->getTailSamples());
  }
  // Processing is switched on here, on the main thread after activation, as
  // hosts do in practice: a plugin may allocate in it, and its answer is not
  // a verdict on whether it will process, so it is not treated as one.
  tresult processing = v->processor->setProcessing(true);
  if (trace_enabled)
    fprintf(stderr, "trace: setProcessing -> %d\n", (int)processing);
  v->processing = true;
  // Every bus gets an entry with buffers of its own width. The main ones
  // carry the chain; the rest read silence and write into scratch, since a
  // plugin does not always honour a bus being off before it indexes it.
  v->in_buses.assign((size_t)v->component->getBusCount(kAudio, kInput), AudioBusBuffers());
  v->out_buses.assign((size_t)v->component->getBusCount(kAudio, kOutput), AudioBusBuffers());
  v->side_buffers.clear();
  v->side_ptrs.clear();
  v->side_ptrs.reserve(v->in_buses.size() + v->out_buses.size());
  for (BusDirection direction : {kInput, kOutput}) {
    std::vector<AudioBusBuffers> &buses = direction == kInput ? v->in_buses : v->out_buses;
    int32 main = direction == kInput ? v->main_in : v->main_out;
    for (size_t i = 0; i < buses.size(); ++i) {
      if ((int32)i == main) {
        buses[i].numChannels = (int32)host_channels(h);
        buses[i].channelBuffers32 = direction == kInput ? v->in_ptrs : v->out_ptrs;
        continue;
      }
      BusInfo info;
      if (v->component->getBusInfo(kAudio, direction, (int32)i, info) != kResultOk ||
          info.channelCount <= 0)
        continue;
      std::vector<float *> ptrs;
      for (int32 c = 0; c < info.channelCount; ++c) {
        v->side_buffers.emplace_back(MAX_FRAMES, 0.0f);
        ptrs.push_back(v->side_buffers.back().data());
      }
      v->side_ptrs.push_back(std::move(ptrs));
      buses[i].numChannels = info.channelCount;
      buses[i].channelBuffers32 = v->side_ptrs.back().data();
      if (direction == kInput)
        buses[i].silenceFlags = ~0ULL;
    }
  }
  v->data.processMode = kRealtime;
  v->data.symbolicSampleSize = kSample32;
  v->data.numInputs = (int32)v->in_buses.size();
  v->data.numOutputs = (int32)v->out_buses.size();
  v->data.inputs = v->in_buses.data();
  v->data.outputs = v->out_buses.data();
  v->data.inputParameterChanges = &v->in_changes;
  v->data.outputParameterChanges = &v->out_changes;
  return true;
}

void vst3_deactivate(Host *h) {
  Vst3 *v = of(h);
  if (!v->active)
    return;
  if (v->processing) {
    v->processor->setProcessing(false);
    v->processing = false;
  }
  v->component->setActive(false);
  v->active = false;
}

void vst3_unload(Host *h) {
  Vst3 *v = of(h);
  for (Timer &t : v->timers)
    if (t.source) {
      pw_loop_destroy_source(host_loop(h), t.source);
      t.handler->release();
    }
  for (Fd &f : v->fds)
    if (f.source) {
      pw_loop_destroy_source(host_loop(h), f.source);
      f.handler->release();
    }
  release_plugin(v);
  delete v->frame;
  for (Record *r : v->records)
    delete r;
  if (v->factory)
    v->factory->release();
  if (v->module_exit)
    v->module_exit();
  if (v->library)
    dlclose(v->library);
  delete v;
  host_set_impl(h, nullptr);
}

// --- audio ------------------------------------------------------------------

void vst3_process(Host *h, uint32_t frames, float *const *in,
                  float *const *out) {
  Vst3 *v = of(h);
  unsigned channels = host_channels(h);
  for (unsigned i = 0; i < channels; ++i) {
    v->in_ptrs[i] = in[i];
    v->out_ptrs[i] = out[i];
  }
  v->in_changes.clear();
  v->out_changes.clear();
  for (Record *r : v->records) {
    if (r->readonly)
      continue;
    double wanted = r->normalized_desired.load();
    if (wanted == r->last_sent)
      continue;
    int32 index;
    IParamValueQueue *queue = v->in_changes.addParameterData(r->id, index);
    if (queue)
      queue->addPoint(0, wanted, index);
    r->last_sent = wanted;
  }
  v->data.numSamples = (int32)frames;
  v->in_buses[(size_t)v->main_in].silenceFlags = 0;  // the side inputs stay silent
  for (AudioBusBuffers &bus : v->out_buses)
    bus.silenceFlags = 0;
  tresult result = v->processor->process(v->data);
  if (result != kResultOk)
    for (unsigned i = 0; i < channels; ++i)
      memset(out[i], 0, frames * sizeof(float));
  static int traced = 0;
  if (traced < 4 && trace_enabled) {
    float in_peak = 0, out_peak = 0;
    for (uint32_t i = 0; i < frames; ++i) {
      in_peak = std::max(in_peak, std::abs(in[0][i]));
      out_peak = std::max(out_peak, std::abs(out[0][i]));
    }
    fprintf(stderr, "trace: process -> %d, frames %u, in peak %.3f, out peak %.3f, out silence flags %llx, changes in %d\n",
            (int)result, frames, in_peak, out_peak,
            (unsigned long long)v->out_buses[(size_t)v->main_out].silenceFlags, v->in_changes.getParameterCount());
    ++traced;
  }
  // What the processor changed on its own: meters, and a plugin that moves
  // its own parameters.
  for (int32 i = 0; i < v->out_changes.getParameterCount(); ++i) {
    ValueQueue &q = v->out_changes.queues[(size_t)i];
    if (q.count == 0)
      continue;
    Record *r = record_for(v, q.id);
    if (r)
      r->normalized_observed = q.value;
  }
}

// --- main thread --------------------------------------------------------------

void vst3_main_thread(Host *h) {
  Vst3 *v = of(h);
  if (v->restart_requested.exchange(false))
    host_fail(h, "the plugin asked to be reloaded");
  bool refresh = v->values_changed.exchange(false);
  for (Record *r : v->records) {
    // The daemon speaks plain values; the processor takes normalised ones.
    // Convert here, on the thread the controller is for.
    float plain = control_desired(r->ctl);
    if (!r->readonly && (!r->seen || plain != r->last_plain)) {
      double normalized = v->controller->plainParamToNormalized(r->id, plain);
      r->normalized_desired = normalized;
      v->controller->setParamNormalized(r->id, normalized);
      r->last_plain = plain;
      r->seen = true;
    }
    // And back the other way for what the processor reported.
    double observed = r->normalized_observed.load();
    if (r->readonly || refresh) {
      if (refresh)
        observed = v->controller->getParamNormalized(r->id);
      control_set_observed(r->ctl, (float)v->controller->normalizedParamToPlain(
                                       r->id, observed));
    }
  }
}

// --- the editor -------------------------------------------------------------

bool vst3_editor_open(Host *h) {
  Vst3 *v = of(h);
  v->view = v->controller->createView(ViewType::kEditor);
  if (!v->view)
    return false;
  if (v->view->isPlatformTypeSupported(kPlatformTypeX11EmbedWindowID) !=
      kResultTrue) {
    v->view->release();
    v->view = nullptr;
    return false;
  }
  v->view->setFrame(v->frame);
  ViewRect rect;
  if (v->view->getSize(&rect) == kResultOk && rect.getWidth() > 0 &&
      rect.getHeight() > 0)
    host_resize_editor(h, (unsigned)rect.getWidth(), (unsigned)rect.getHeight());
  if (v->view->attached((void *)(uintptr_t)host_window(h),
                        kPlatformTypeX11EmbedWindowID) != kResultOk) {
    v->view->setFrame(nullptr);
    v->view->release();
    v->view = nullptr;
    return false;
  }
  return true;
}

void vst3_editor_close(Host *h) {
  Vst3 *v = of(h);
  if (!v->view)
    return;
  v->view->removed();
  v->view->setFrame(nullptr);
  v->view->release();
  v->view = nullptr;
}

void vst3_editor_lost(Host *h) {
  // The view's window is inside a display that is gone; removing it would
  // go through that display. It stays until this process exits.
  of(h)->view = nullptr;
}

bool vst3_editor_idle(Host *) { return false; }

void vst3_editor_resized(Host *h, unsigned width, unsigned height) {
  Vst3 *v = of(h);
  if (!v->view)
    return;
  ViewRect rect(0, 0, (int32)width, (int32)height);
  v->view->onSize(&rect);
}

}  // namespace

extern "C" const Backend vst3_backend = {
    "VST3",
    2,
    vst3_load,
    vst3_activate,
    vst3_deactivate,
    vst3_process,
    vst3_editor_open,
    vst3_editor_close,
    vst3_editor_idle,
    vst3_editor_lost,
    vst3_editor_resized,
    vst3_main_thread,
    vst3_unload,
};

// --- the scanner ------------------------------------------------------------

// Describe every audio plugin in one bundle as JSON, for the daemon's
// catalogue. Each is created once, wired to its controller, and asked about
// its buses, parameters and editor, then let go.
extern "C" int vst3_scan(const char *path) {
  Vst3 v;
  if (!open_module(&v, path))
    return 1;
  printf("{\"file\":");
  json_string(path);
  printf(",\"plugins\":[");
  int32 count = v.factory->countClasses();
  IPluginFactory2 *factory2 = nullptr;
  v.factory->queryInterface(IID(IPluginFactory2), (void **)&factory2);
  bool first = true;
  for (int32 i = 0; i < count; ++i) {
    PClassInfo info;
    if (v.factory->getClassInfo(i, &info) != kResultOk ||
        strcmp(info.category, kVstAudioEffectClass) != 0)
      continue;
    std::string vendor, subcategories;
    if (factory2) {
      PClassInfo2 info2;
      if (factory2->getClassInfo2(i, &info2) == kResultOk) {
        vendor = info2.vendor;
        subcategories = info2.subCategories;
      }
    }
    if (!instantiate(&v, info.cid)) {
      release_plugin(&v);
      continue;
    }
    printf("%s{\"id\":", first ? "" : ",");
    first = false;
    json_string(hex_of(info.cid).c_str());
    printf(",\"name\":");
    json_string(info.name);
    printf(",\"vendor\":");
    json_string(vendor.c_str());
    // Sub-categories read "Fx|Reverb"; the daemon groups by them.
    printf(",\"features\":[");
    size_t start = 0;
    bool first_feature = true;
    while (start <= subcategories.size()) {
      size_t end = subcategories.find('|', start);
      if (end == std::string::npos)
        end = subcategories.size();
      if (end > start) {
        if (!first_feature)
          putchar(',');
        first_feature = false;
        json_string(subcategories.substr(start, end - start).c_str());
      }
      start = end + 1;
    }
    printf("],\"audioIns\":%d,\"audioOuts\":%d", main_bus_channels(&v, kInput),
           main_bus_channels(&v, kOutput));
    // Whether a VST3 plugin has an editor can only be learnt by creating
    // one, which for a module of two hundred plugins is most of a minute.
    // Nearly every VST3 plugin ships one, so the catalogue assumes it does;
    // the host finds out for certain when the editor is asked for.
    printf(",\"gui\":true");
    printf(",\"params\":[");
    int32 parameters = v.controller->getParameterCount();
    bool first_param = true;
    for (int32 p = 0; p < parameters; ++p) {
      ParameterInfo pinfo;
      if (v.controller->getParameterInfo(p, pinfo) != kResultOk ||
          (pinfo.flags & ParameterInfo::kIsHidden))
        continue;
      double minimum = v.controller->normalizedParamToPlain(pinfo.id, 0.0);
      double maximum = v.controller->normalizedParamToPlain(pinfo.id, 1.0);
      double fallback = v.controller->normalizedParamToPlain(
          pinfo.id, pinfo.defaultNormalizedValue);
      if (minimum > maximum)
        std::swap(minimum, maximum);
      printf("%s{\"id\":%u,\"name\":", first_param ? "" : ",",
             (unsigned)pinfo.id);
      first_param = false;
      json_string(utf8(pinfo.title).c_str());
      printf(",\"module\":");
      json_string(utf8(pinfo.units).c_str());
      printf(",\"min\":%.9g,\"max\":%.9g,\"default\":%.9g,\"readonly\":%s,"
             "\"stepped\":%s,\"enum\":%s}",
             minimum, maximum, fallback,
             (pinfo.flags & ParameterInfo::kIsReadOnly) ? "true" : "false",
             pinfo.stepCount > 0 ? "true" : "false",
             (pinfo.flags & ParameterInfo::kIsList) ? "true" : "false");
    }
    printf("]}");
    release_plugin(&v);
  }
  puts("]}");
  if (factory2)
    factory2->release();
  v.factory->release();
  if (v.module_exit)
    v.module_exit();
  dlclose(v.library);
  return 0;
}
