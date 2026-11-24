// SPDX-License-Identifier: GPL-3.0-only
// Exercise the host-owned SDK objects directly, without a plugin or audio server.
#include "../vst3.cpp"
#include <cassert>
#include <limits>
#include <cerrno>
#include <fcntl.h>
#include <pthread.h>
#include <time.h>
#include <unistd.h>

extern "C" Host *test_host_new(void);
extern "C" void test_host_free(Host *h);
extern "C" Host *test_audio_host_new(unsigned channels);
extern "C" void test_audio_host_free(Host *h);
extern "C" bool test_host_audio_failed(Host *h);
extern "C" void test_host_set_own_block(Host *h, bool own_block);
extern "C" void test_host_open_editor(Host *h, const Backend *backend);
extern "C" void test_host_lose_display(void);
extern "C" const char *test_host_plugin_name(const Host *h);

class Watch final : public Linux::ITimerHandler, public Linux::IEventHandler {
 public:
  PlugFrame *frame = nullptr;
  bool timer = false, lose_display = false;
  unsigned refs = 1, calls = 0;
  tresult PLUGIN_API queryInterface(const TUID, void **obj) override {
    *obj = nullptr;
    return kNoInterface;
  }
  uint32 PLUGIN_API addRef() override { return ++refs; }
  uint32 PLUGIN_API release() override {
    assert(refs == 2);
    // A final release may re-enter the host. Its record is already detached.
    assert((timer ? frame->unregisterTimer(this)
                  : frame->unregisterEventHandler(this)) == kResultFalse);
    return --refs;
  }
  void PLUGIN_API onTimer() override {
    ++calls;
    if (lose_display) {
      lose_display = false;
      test_host_lose_display();
    }
  }
  void PLUGIN_API onFDIsSet(Linux::FileDescriptor) override { ++calls; }
};

class TeardownComponent final : public IComponent {
 public:
  PlugFrame *frame = nullptr;
  Watch *timer = nullptr, *fd = nullptr;
  bool remove_timer = false, remove_fd = false;
  unsigned terminated = 0;
  tresult PLUGIN_API queryInterface(const TUID, void **obj) override {
    *obj = nullptr;
    return kNoInterface;
  }
  HOST_OWNED_REFCOUNT()
  tresult PLUGIN_API initialize(FUnknown *) override { return kResultOk; }
  tresult PLUGIN_API terminate() override {
    ++terminated;
    if (remove_timer) assert(frame->unregisterTimer(timer) == kResultOk);
    if (remove_fd) assert(frame->unregisterEventHandler(fd) == kResultOk);
    // A plugin cannot create new watches while the host is unloading it.
    assert(frame->registerTimer(timer, 1) == kResultFalse);
    assert(frame->registerEventHandler(fd, -1) == kResultFalse);
    return kResultOk;
  }
  tresult PLUGIN_API getControllerClassId(TUID) override { return kNotImplemented; }
  tresult PLUGIN_API setIoMode(IoMode) override { return kResultOk; }
  int32 PLUGIN_API getBusCount(MediaType, BusDirection) override { return 0; }
  tresult PLUGIN_API getBusInfo(MediaType, BusDirection, int32, BusInfo &) override { return kNotImplemented; }
  tresult PLUGIN_API getRoutingInfo(RoutingInfo &, RoutingInfo &) override { return kNotImplemented; }
  tresult PLUGIN_API activateBus(MediaType, BusDirection, int32, TBool) override { return kResultOk; }
  tresult PLUGIN_API setActive(TBool) override { return kResultOk; }
  tresult PLUGIN_API setState(IBStream *) override { return kResultOk; }
  tresult PLUGIN_API getState(IBStream *) override { return kResultOk; }
};

class WatchView final : public IPlugView {
 public:
  Watch timer, fd;
  bool detached = false, removed_called = false;
  unsigned released = 0;
  tresult PLUGIN_API queryInterface(const TUID, void **obj) override {
    *obj = nullptr;
    return kNoInterface;
  }
  uint32 PLUGIN_API addRef() override { return 1; }
  uint32 PLUGIN_API release() override {
    // The view owns its handlers. Destroying it with registered watches
    // leaves the next loop dispatch calling a freed member object.
    assert(detached && timer.refs == 1 && fd.refs == 1);
    ++released;
    return 0;
  }
  tresult PLUGIN_API isPlatformTypeSupported(FIDString) override { return kResultTrue; }
  tresult PLUGIN_API attached(void *, FIDString) override { return kResultOk; }
  tresult PLUGIN_API removed() override { removed_called = true; return kResultOk; }
  tresult PLUGIN_API onWheel(float) override { return kResultFalse; }
  tresult PLUGIN_API onKeyDown(char16, int16, int16) override { return kResultFalse; }
  tresult PLUGIN_API onKeyUp(char16, int16, int16) override { return kResultFalse; }
  tresult PLUGIN_API getSize(ViewRect *) override { return kResultFalse; }
  tresult PLUGIN_API onSize(ViewRect *) override { return kResultOk; }
  tresult PLUGIN_API onFocus(TBool) override { return kResultOk; }
  tresult PLUGIN_API setFrame(IPlugFrame *frame) override { detached = !frame; return kResultOk; }
  tresult PLUGIN_API canResize() override { return kResultFalse; }
  tresult PLUGIN_API checkSizeConstraint(ViewRect *) override { return kResultOk; }
};

static void editor_watches() {
  Host *h = test_host_new();
  auto *v = new Vst3();
  v->h = h;
  v->frame = new PlugFrame(v);
  host_set_impl(h, v);
  int pipes[2];
  assert(pipe(pipes) == 0);
  for (unsigned cycle = 0; cycle < 3; ++cycle) {
    WatchView view;
    v->view = &view;
    view.timer.frame = view.fd.frame = v->frame;
    view.timer.timer = true;
    assert(v->frame->registerTimer(&view.timer, 1000) == kResultOk);
    assert(v->frame->registerEventHandler(&view.fd, pipes[0]) == kResultOk);
    if (cycle == 1) release_view(v);  // an attachment failed
    else vst3_editor_close(h);
    assert(!v->view && view.released == 1);
    assert(view.removed_called == (cycle != 1));
    assert(!v->stopping_run_loop);  // reopening can register new watches
    assert(write(pipes[1], "x", 1) == 1);
    pw_loop_enter(host_loop(h));
    assert(pw_loop_iterate(host_loop(h), 0) >= 0);
    pw_loop_leave(host_loop(h));
    assert(!view.timer.calls && !view.fd.calls);
    char byte;
    assert(read(pipes[0], &byte, 1) == 1);
  }
  vst3_unload(h);
  test_host_free(h);
  close(pipes[0]);
  close(pipes[1]);
}

static void iterate_until(Host *h, const unsigned &calls) {
  for (unsigned i = 0; i < 200 && !calls; ++i)
    pw_loop_iterate(host_loop(h), 10);
  assert(calls);
}

// The display goes while the view's timer runs. The view's sources stop at
// once, a hung-up descriptor included, and their handlers are released
// exactly once afterwards: by the plugin, by the next view's release, or
// at unload. A new view can register and run its own meanwhile.
static void lost_editor_watches(bool reopen) {
  Host *h = test_host_new();
  auto *v = new Vst3();
  v->h = h;
  v->frame = new PlugFrame(v);
  host_set_impl(h, v);
  int pipes[2];
  assert(pipe(pipes) == 0);
  WatchView lost;
  v->view = &lost;
  lost.timer.frame = lost.fd.frame = v->frame;
  lost.timer.timer = true;
  lost.timer.lose_display = true;
  assert(v->frame->registerTimer(&lost.timer, 1) == kResultOk);
  assert(v->frame->registerEventHandler(&lost.fd, pipes[0]) == kResultOk);
  test_host_open_editor(h, &vst3_backend);
  pw_loop_enter(host_loop(h));
  iterate_until(h, lost.timer.calls);
  assert(!v->view);
  // The plugin's connection hangs up and stays ready from now on.
  close(pipes[1]);
  for (unsigned i = 0; i < 10; ++i)
    assert(pw_loop_iterate(host_loop(h), 5) == 0);
  assert(lost.timer.calls == 1 && !lost.fd.calls);
  assert(lost.timer.refs == 2 && lost.fd.refs == 2);
  if (reopen) {
    // The plugin lets go of one itself; the next view's release takes the other.
    assert(v->frame->unregisterTimer(&lost.timer) == kResultOk);
    assert(lost.timer.refs == 1);
    assert(v->frame->unregisterTimer(&lost.timer) == kResultFalse);
    WatchView view;
    v->view = &view;
    view.timer.frame = view.fd.frame = v->frame;
    view.timer.timer = true;
    int next[2];
    assert(pipe(next) == 0);
    assert(v->frame->registerTimer(&view.timer, 1) == kResultOk);
    assert(v->frame->registerEventHandler(&view.fd, next[0]) == kResultOk);
    iterate_until(h, view.timer.calls);
    assert(write(next[1], "x", 1) == 1);
    iterate_until(h, view.fd.calls);
    vst3_editor_close(h);
    assert(view.released == 1 && lost.fd.refs == 1);
    close(next[0]);
    close(next[1]);
  }
  pw_loop_leave(host_loop(h));
  vst3_unload(h);
  assert(lost.timer.refs == 1 && lost.fd.refs == 1 && !lost.released);
  assert(lost.timer.calls == 1 && !lost.fd.calls);
  test_host_free(h);
  close(pipes[0]);
}

// One handler for several descriptors, which a plugin unregisters once.
class SharedHandler final : public Linux::IEventHandler {
 public:
  unsigned refs = 1, calls = 0;
  tresult PLUGIN_API queryInterface(const TUID, void **obj) override {
    *obj = nullptr;
    return kNoInterface;
  }
  uint32 PLUGIN_API addRef() override { return ++refs; }
  uint32 PLUGIN_API release() override { return --refs; }
  void PLUGIN_API onFDIsSet(Linux::FileDescriptor) override { ++calls; }
};

static void shared_event_handler() {
  Host *h = test_host_new();
  auto *v = new Vst3();
  v->h = h;
  v->frame = new PlugFrame(v);
  host_set_impl(h, v);
  SharedHandler handler;
  int pipes[3][2];
  for (auto &p : pipes) {
    assert(pipe(p) == 0);
    assert(v->frame->registerEventHandler(&handler, p[0]) == kResultOk);
  }
  assert(handler.refs == 4);
  assert(v->frame->unregisterEventHandler(&handler) == kResultOk);
  assert(handler.refs == 1);
  for (const Fd &f : v->fds)
    assert(!f.handler && !f.source);
  assert(v->frame->unregisterEventHandler(&handler) == kResultFalse);
  for (auto &p : pipes)
    assert(write(p[1], "x", 1) == 1);
  pw_loop_enter(host_loop(h));
  assert(pw_loop_iterate(host_loop(h), 0) == 0);
  pw_loop_leave(host_loop(h));
  assert(!handler.calls);
  vst3_unload(h);
  assert(handler.refs == 1);
  test_host_free(h);
  for (auto &p : pipes) {
    close(p[0]);
    close(p[1]);
  }
}

static void teardown_sources(bool remove_timer, bool remove_fd, bool with_plugin) {
  Host *h = test_host_new();
  auto *v = new Vst3();
  v->h = h;
  v->frame = new PlugFrame(v);
  host_set_impl(h, v);
  Watch timers[2], fds[2];
  int pipes[2][2], timer_fds[2];
  for (unsigned i = 0; i < 2; ++i) {
    timers[i].frame = fds[i].frame = v->frame;
    timers[i].timer = true;
    assert(v->frame->registerTimer(&timers[i], 1000) == kResultOk);
    timer_fds[i] = v->timers[i].source->fd;
    assert(pipe(pipes[i]) == 0);
    assert(v->frame->registerEventHandler(&fds[i], pipes[i][0]) == kResultOk);
    assert(timers[i].refs == 2 && fds[i].refs == 2);
  }
  TeardownComponent component;
  component.frame = v->frame;
  component.timer = &timers[0];
  component.fd = &fds[0];
  component.remove_timer = remove_timer;
  component.remove_fd = remove_fd;
  if (with_plugin) v->component = &component;
  vst3_unload(h);
  assert(host_impl(h) == nullptr);
  assert(component.terminated == (with_plugin ? 1u : 0u));
  for (unsigned i = 0; i < 2; ++i) {
    assert(timers[i].refs == 1 && fds[i].refs == 1);
    assert(fcntl(timer_fds[i], F_GETFD) == -1 && errno == EBADF);
    assert(fcntl(pipes[i][0], F_GETFD) >= 0);
    assert(write(pipes[i][1], "x", 1) == 1);
  }
  // Dispatch after freeing the plugin and its frame must not reach a watch.
  test_host_free(h);
  for (unsigned i = 0; i < 2; ++i) {
    assert(!timers[i].calls && !fds[i].calls);
    close(pipes[i][0]);
    close(pipes[i][1]);
  }
}

static void teardown() {
  pw_init(nullptr, nullptr);
  for (unsigned i = 0; i < 4; ++i)
    teardown_sources(i & 1, i & 2, true);
  teardown_sources(false, false, false);
  editor_watches();
  lost_editor_watches(false);
  lost_editor_watches(true);
  shared_event_handler();
  pw_deinit();
  puts("PASS: VST3 teardown unregisters watches once and collects leftovers without closing plugin FDs");
  puts("PASS: a lost VST3 editor stops its watches at once and releases their handlers once");
  puts("PASS: unregistering a VST3 event handler removes every descriptor it was registered for");
}

static void parameters() {
  ParameterChanges changes;
  changes.reserve(2);
  assert(changes.getParameterData(-1) == nullptr);
  assert(changes.getParameterData(0) == nullptr);
  int32 index = -1;
  auto *queue = changes.addParameterData(7, index);
  assert(queue && index == 0);
  assert(changes.getParameterData(-1) == nullptr);
  assert(changes.getParameterData(0) == queue);
  assert(changes.addParameterData(7, index) == queue && index == 0);
  assert(changes.addParameterData(8, index) && index == 1);
  assert(changes.addParameterData(9, index) == nullptr);
  assert(changes.getParameterData(2) == nullptr);
  changes.clear();
  assert(changes.getParameterCount() == 0);
  assert(changes.getParameterData(0) == nullptr);
}

static void stream() {
  MemoryStream stream;
  int32 count = 123;
  char buffer[8] = {};
  assert(stream.read(buffer, -1, &count) != kResultOk && count == 0);
  assert(stream.write(buffer, -1, &count) != kResultOk && count == 0);
  assert(stream.read(nullptr, 1, &count) != kResultOk && count == 0);
  assert(stream.write(nullptr, 1, &count) != kResultOk && count == 0);
  assert(stream.read(nullptr, 0, &count) == kResultOk && count == 0);
  assert(stream.write(nullptr, 0, &count) == kResultOk && count == 0);
  assert(stream.write((void *)"abc", 3, &count) == kResultOk && count == 3);
  assert(stream.seek(0, IBStream::kIBSeekSet, nullptr) == kResultOk);
  assert(stream.read(buffer, 8, &count) == kResultOk && count == 3);
  assert(memcmp(buffer, "abc", 3) == 0);
  assert(stream.read(buffer, 8, &count) == kResultOk && count == 0);
  assert(stream.seek(5, IBStream::kIBSeekSet, nullptr) == kResultOk);
  assert(stream.read(buffer, 8, &count) == kResultOk && count == 0);
  assert(stream.write(nullptr, 0, &count) == kResultOk && stream.bytes.size() == 3);
  assert(stream.write((void *)"z", 1, &count) == kResultOk && count == 1);
  assert(stream.bytes == std::vector<uint8_t>({'a', 'b', 'c', 0, 0, 'z'}));
  int64 position = -1;
  assert(stream.seek(-1, IBStream::kIBSeekEnd, &position) == kResultOk && position == 5);
  assert(stream.seek(-6, IBStream::kIBSeekCur, nullptr) != kResultOk);
  assert(stream.seek(0, 42, nullptr) != kResultOk);
  assert(stream.seek(std::numeric_limits<int64>::min(), IBStream::kIBSeekCur, nullptr) != kResultOk);
  assert(stream.seek(std::numeric_limits<int64>::max(), IBStream::kIBSeekCur, nullptr) != kResultOk);
  assert(stream.tell(&position) == kResultOk && position == 5);
  // A sparse seek must not wrap the next write into a small allocation.
  auto maximum = static_cast<int64>(stream.bytes.max_size());
  assert(stream.seek(maximum, IBStream::kIBSeekSet, nullptr) == kResultOk);
  assert(stream.write(buffer, 1, &count) != kResultOk && count == 0);
  assert(stream.bytes.size() == 6);
  assert(stream.read(buffer, 1, &count) == kResultOk && count == 0);
}

static void attributes() {
  AttributeList list;
  int64 integer = 0;
  double real = 0;
  const void *data = nullptr;
  uint32 size = 0;
  TChar text[3] = {9, 9, 9};
  assert(list.setInt(nullptr, 1) != kResultOk);
  assert(list.getInt(nullptr, integer) != kResultOk);
  assert(list.setFloat(nullptr, 1) != kResultOk);
  assert(list.getFloat(nullptr, real) != kResultOk);
  assert(list.setString(nullptr, u"a") != kResultOk);
  assert(list.getString(nullptr, text, sizeof(text)) != kResultOk);
  assert(list.setString("text", u"abc") == kResultOk);
  assert(list.getString("text", nullptr, 4) != kResultOk);
  assert(list.getString("text", text, 1) != kResultOk && text[0] == 9);
  assert(list.getString("text", text, 5) == kResultOk);
  assert(text[0] == 'a' && text[1] == 0 && text[2] == 9);
  assert(list.setBinary(nullptr, "a", 1) != kResultOk);
  assert(list.getBinary(nullptr, data, size) != kResultOk);
  assert(list.setBinary("bytes", nullptr, 1) != kResultOk);
  assert(list.setBinary("bytes", nullptr, 0) == kResultOk);
  assert(list.getBinary("bytes", data, size) == kResultOk && size == 0);
  assert(list.setBinary("bytes", "abc", 3) == kResultOk);
  assert(list.getBinary("bytes", data, size) == kResultOk && size == 3);
  assert(memcmp(data, "abc", size) == 0);
}

// A plugin fills its strings into fixed arrays, and the SDK's own copy
// leaves no terminator in a string that fills its array. A read stops at
// the array's end.
class NameComponent final : public IComponent {
 public:
  tresult PLUGIN_API queryInterface(const TUID, void **obj) override {
    *obj = nullptr;
    return kNoInterface;
  }
  HOST_OWNED_REFCOUNT()
  tresult PLUGIN_API initialize(FUnknown *) override { return kResultOk; }
  tresult PLUGIN_API terminate() override { return kResultOk; }
  tresult PLUGIN_API getControllerClassId(TUID) override { return kNotImplemented; }
  tresult PLUGIN_API setIoMode(IoMode) override { return kResultOk; }
  int32 PLUGIN_API getBusCount(MediaType, BusDirection) override { return 0; }
  tresult PLUGIN_API getBusInfo(MediaType, BusDirection, int32, BusInfo &) override { return kNotImplemented; }
  tresult PLUGIN_API getRoutingInfo(RoutingInfo &, RoutingInfo &) override { return kNotImplemented; }
  tresult PLUGIN_API activateBus(MediaType, BusDirection, int32, TBool) override { return kResultOk; }
  tresult PLUGIN_API setActive(TBool) override { return kResultOk; }
  tresult PLUGIN_API setState(IBStream *) override { return kResultOk; }
  tresult PLUGIN_API getState(IBStream *) override { return kResultOk; }
};

class NameFactory final : public IPluginFactory {
 public:
  NameComponent component;
  tresult PLUGIN_API queryInterface(const TUID, void **obj) override {
    *obj = nullptr;
    return kNoInterface;
  }
  HOST_OWNED_REFCOUNT()
  tresult PLUGIN_API getFactoryInfo(PFactoryInfo *) override { return kNotImplemented; }
  int32 PLUGIN_API countClasses() override { return 1; }
  tresult PLUGIN_API getClassInfo(int32, PClassInfo *info) override {
    *info = PClassInfo();
    strcpy(info->category, kVstAudioEffectClass);
    memset(info->name, 'N', sizeof(info->name));
    return kResultOk;
  }
  tresult PLUGIN_API createInstance(FIDString, FIDString, void **obj) override {
    *obj = static_cast<IComponent *>(&component);
    return kResultOk;
  }
};

// Leaves nonzero bytes where the next call's frame will be, so a read past
// a local array finds them rather than a lucky zero.
__attribute__((noinline)) static void dirty_stack() {
  volatile char bytes[8192];
  for (size_t i = 0; i < sizeof(bytes); ++i)
    bytes[i] = 'Z';
}

static void strings() {
  Host *h = test_audio_host_new(2);
  NameFactory factory;
  Vst3 v;
  v.h = h;
  v.factory = &factory;
  TUID cid = {};
  dirty_stack();
  assert(!instantiate(&v, cid));  // the fixture does not process audio
  assert(std::string(test_host_plugin_name(h)) == std::string(64, 'N'));
  release_plugin(&v);
  test_audio_host_free(h);

  // Each String128 is followed in ParameterInfo by text of its own, which a
  // read past the title or the units would take in.
  ParameterInfo info = {};
  for (auto &c : info.title) c = u'T';
  for (auto &c : info.shortTitle) c = u'S';
  for (auto &c : info.units) c = u'U';
  info.stepCount = 'X';
  assert(utf8(info.title) == std::string(128, 'T'));
  assert(utf8(info.units) == std::string(128, 'U'));
  // A high surrogate in the last element has no low half inside the array.
  info.title[127] = 0xD800;
  info.shortTitle[0] = 0xDC00;
  assert(utf8(info.title) == std::string(127, 'T') + "\xED\xA0\x80");
  info.title[3] = 0;
  assert(utf8(info.title) == "TTT");
  puts("PASS: VST3 class names and parameter strings stop at the end of their arrays");
}

// What a plugin may ask of the host after activation. A changed component
// or bus layout asks for a reload, done in place (see reload_in_place); a
// new latency figure does not need one.
static void restart() {
  Vst3 v;
  ComponentHandler handler(&v);
  assert(handler.restartComponent(RestartFlags::kLatencyChanged) == kResultOk);
  assert(!v.restart_requested && !v.values_changed);
  assert(handler.restartComponent(RestartFlags::kParamValuesChanged) == kResultOk);
  assert(!v.restart_requested && v.values_changed);
  assert(handler.restartComponent(RestartFlags::kLatencyChanged | RestartFlags::kIoChanged) == kResultOk);
  assert(v.restart_requested.exchange(false));
  assert(handler.restartComponent(RestartFlags::kReloadComponent) == kResultOk);
  assert(v.restart_requested);
}

// A processor with controllable audio results and latency, counting each
// time its latency is asked.
class LatencyProcessor final : public IAudioProcessor {
 public:
  uint32 samples = 0;
  int asked = 0;
  int32 max_block = 0;
  tresult process_result = kResultOk;
  tresult setup_result = kResultOk;
  // The points one parameter got in the last process call.
  ParamID watched = ~0u;
  int32 points = 0;
  ParamValue value = -1;
  tresult PLUGIN_API queryInterface(const TUID, void **obj) override {
    *obj = nullptr;
    return kNoInterface;
  }
  HOST_OWNED_REFCOUNT()
  tresult PLUGIN_API setBusArrangements(SpeakerArrangement *, int32,
                                        SpeakerArrangement *, int32) override {
    return kResultOk;
  }
  tresult PLUGIN_API getBusArrangement(BusDirection, int32,
                                       SpeakerArrangement &) override {
    return kResultOk;
  }
  tresult PLUGIN_API canProcessSampleSize(int32) override { return kResultOk; }
  uint32 PLUGIN_API getLatencySamples() override {
    ++asked;
    return samples;
  }
  tresult PLUGIN_API setupProcessing(ProcessSetup &setup) override {
    max_block = setup.maxSamplesPerBlock;
    return setup_result;
  }
  tresult PLUGIN_API setProcessing(TBool) override { return kResultOk; }
  tresult PLUGIN_API process(ProcessData &data) override {
    points = 0;
    IParameterChanges *changes = data.inputParameterChanges;
    for (int32 i = 0; changes && i < changes->getParameterCount(); ++i) {
      IParamValueQueue *queue = changes->getParameterData(i);
      if (queue->getParameterId() != watched)
        continue;
      int32 offset;
      points = queue->getPointCount();
      queue->getPoint(0, offset, value);
    }
    return process_result;
  }
  uint32 PLUGIN_API getTailSamples() override { return 0; }
};

static void processing_failure() {
  Host *h = test_audio_host_new(2);
  Vst3 v;
  LatencyProcessor processor;
  v.processor = &processor;
  v.main_in = v.main_out = 0;
  v.in_buses.resize(1);
  v.out_buses.resize(1);
  host_set_impl(h, &v);
  float left[32], right[32];
  float *buffers[] = {left, right};
  for (float *buffer : buffers)
    std::fill(buffer, buffer + 32, .25f);
  vst3_process(h, 32, buffers, buffers);
  assert(!test_host_audio_failed(h));
  // One refused block is silenced and the next one tried; half a second of
  // them in a row asks for a new host, and a good block starts over.
  const unsigned run = 48000 / 2 / 32;
  processor.process_result = kResultFalse;
  for (unsigned block = 1; block < run; ++block) {
    std::fill(left, left + 32, .25f);
    std::fill(right, right + 32, .25f);
    vst3_process(h, 32, buffers, buffers);
    for (float *buffer : buffers)
      for (unsigned n = 0; n < 32; ++n) assert(buffer[n] == 0);
  }
  assert(!test_host_audio_failed(h));
  processor.process_result = kResultOk;
  vst3_process(h, 32, buffers, buffers);
  processor.process_result = kResultFalse;
  for (unsigned block = 1; block < run; ++block)
    vst3_process(h, 32, buffers, buffers);
  assert(!test_host_audio_failed(h));
  vst3_process(h, 32, buffers, buffers);
  assert(test_host_audio_failed(h));
  host_set_impl(h, nullptr);
  test_audio_host_free(h);
  puts("PASS: VST3 processing errors silence audio, and a run of them requests host recovery");
}

// The tick asks for the latency every cycle once audio runs. The plugin is
// asked once at activation and again only after it signals kLatencyChanged.
static void latency() {
  Vst3 v;
  LatencyProcessor processor;
  ComponentHandler handler(&v);
  v.processor = &processor;
  assert(held_latency(&v) == std::numeric_limits<uint32_t>::max());
  assert(processor.asked == 0);

  processor.samples = 256;
  v.active = true;
  read_latency(&v);
  assert(processor.asked == 1);
  for (int tick = 0; tick < 100; ++tick)
    assert(held_latency(&v) == 256);
  assert(processor.asked == 1);

  // A new figure the plugin has not announced is not looked for.
  processor.samples = 512;
  assert(held_latency(&v) == 256 && processor.asked == 1);

  assert(handler.restartComponent(RestartFlags::kLatencyChanged) == kResultOk);
  assert(!v.restart_requested);
  assert(held_latency(&v) == 512 && processor.asked == 2);
  assert(held_latency(&v) == 512 && processor.asked == 2);

  // A notification during activation is answered by activation's own read.
  assert(handler.restartComponent(RestartFlags::kLatencyChanged) == kResultOk);
  processor.samples = 64;
  read_latency(&v);
  assert(held_latency(&v) == 64 && processor.asked == 3);
  puts("PASS: VST3 latency is read at activation and after kLatencyChanged only");
}

// On its own block the processor is set up for that block; otherwise for the
// host's largest cycle. The refused setup ends activation before anything
// else is touched.
static void own_block_setup() {
  for (bool own : {false, true}) {
    Host *h = test_audio_host_new(2);
    test_host_set_own_block(h, own);
    Vst3 v;
    LatencyProcessor processor;
    processor.setup_result = kResultFalse;
    v.processor = &processor;
    host_set_impl(h, &v);
    assert(!vst3_activate(h));
    assert(processor.max_block == (own ? (int32)OWN_BLOCK_FRAMES : (int32)MAX_FRAMES));
    host_set_impl(h, nullptr);
    test_audio_host_free(h);
  }
  puts("PASS: a VST3 plugin on its own block is set up for at most 1024 frames");
}

// The plugin's own bypass parameter is not a control. The first block tells
// the processor "processing", and after that only the plugin's own editor
// moves it, one change per edit.
static void bypass_parameter() {
  Host *h = test_audio_host_new(2);
  Vst3 v;
  LatencyProcessor processor;
  ComponentHandler handler(&v);
  v.processor = &processor;
  v.in_buses.resize(1);
  v.out_buses.resize(1);
  v.in_changes.reserve(1);
  v.data.inputParameterChanges = &v.in_changes;
  v.has_bypass = true;
  v.bypass_id = 77;
  processor.watched = 77;
  host_set_impl(h, &v);
  float left[32] = {}, right[32] = {};
  float *buffers[] = {left, right};
  vst3_process(h, 32, buffers, buffers);
  assert(processor.points == 1 && processor.value == 0);
  vst3_process(h, 32, buffers, buffers);
  assert(processor.points == 0);
  assert(handler.performEdit(77, 1.0) == kResultOk);
  assert(v.records.empty());
  vst3_process(h, 32, buffers, buffers);
  assert(processor.points == 1 && processor.value == 1);
  vst3_process(h, 32, buffers, buffers);
  assert(processor.points == 0);
  host_set_impl(h, nullptr);
  test_audio_host_free(h);
  puts("PASS: a VST3 bypass parameter starts at processing and follows only the plugin's editor");
}

// A plugin that is its own component and processor, takes a mono chain
// although it starts out stereo, and asks to be reloaded from its process
// call or from inside its own activation. Its checks fail if the host ever
// calls it in an order VST3 does not allow: set up, arranged or given buses
// while active, deactivated while processing, processed while inactive or
// stopped. The states and counts are atomics because the threaded part of
// the test calls it from two threads, as the host does.
class ReloadPlugin final : public IComponent, public IAudioProcessor {
 public:
  IComponentHandler *handler = nullptr;
  std::atomic<bool> active{false}, processing{false}, aux{false};
  std::atomic<int32> width{2}, ask_in_process{0}, ask_in_activate{0};
  std::atomic<unsigned> activations{0}, deactivations{0}, setups{0},
      arranged{0}, starts{0}, stops{0}, processed{0}, asked{0};
  std::atomic<uint32> latency{256};
  tresult activate_result = kResultOk;
  int32 max_block = 0;
  double rate = 0;
  // The points one parameter got in the last process call.
  ParamID watched = 5;
  int32 points = 0;
  ParamValue value = -1;

  tresult PLUGIN_API queryInterface(const TUID, void **obj) override {
    *obj = nullptr;
    return kNoInterface;
  }
  HOST_OWNED_REFCOUNT()
  tresult PLUGIN_API initialize(FUnknown *) override { return kResultOk; }
  tresult PLUGIN_API terminate() override { return kResultOk; }
  tresult PLUGIN_API getControllerClassId(TUID) override { return kNotImplemented; }
  tresult PLUGIN_API setIoMode(IoMode) override { return kResultOk; }
  int32 PLUGIN_API getBusCount(MediaType type, BusDirection direction) override {
    if (type != kAudio) return 0;
    return direction == kInput && aux ? 2 : 1;
  }
  tresult PLUGIN_API getBusInfo(MediaType type, BusDirection direction,
                                int32 index, BusInfo &info) override {
    if (type != kAudio || index < 0 || index >= getBusCount(type, direction))
      return kInvalidArgument;
    info = BusInfo();
    info.mediaType = kAudio;
    info.direction = direction;
    info.channelCount = index == 0 ? width.load() : 1;
    info.busType = index == 0 ? kMain : kAux;
    return kResultOk;
  }
  tresult PLUGIN_API getRoutingInfo(RoutingInfo &, RoutingInfo &) override { return kNotImplemented; }
  tresult PLUGIN_API activateBus(MediaType, BusDirection, int32, TBool) override {
    assert(!active);
    return kResultOk;
  }
  tresult PLUGIN_API setActive(TBool state) override {
    assert(!processing);
    if (!state) {
      assert(active);
      active = false;
      ++deactivations;
      return kResultOk;
    }
    assert(!active);
    if (activate_result != kResultOk) return activate_result;
    active = true;
    ++activations;
    if (int32 flags = ask_in_activate.load()) handler->restartComponent(flags);
    return kResultOk;
  }
  tresult PLUGIN_API setState(IBStream *) override { return kResultOk; }
  tresult PLUGIN_API getState(IBStream *) override { return kResultOk; }

  tresult PLUGIN_API setBusArrangements(SpeakerArrangement *inputs, int32 ins,
                                        SpeakerArrangement *outputs,
                                        int32 outs) override {
    assert(!active && ins == 1 && outs == 1 && inputs[0] == outputs[0]);
    width = inputs[0] == SpeakerArr::kMono ? 1 : 2;
    ++arranged;
    return kResultOk;
  }
  tresult PLUGIN_API getBusArrangement(BusDirection, int32,
                                       SpeakerArrangement &) override {
    return kResultOk;
  }
  tresult PLUGIN_API canProcessSampleSize(int32) override { return kResultOk; }
  uint32 PLUGIN_API getLatencySamples() override {
    ++asked;
    return latency;
  }
  tresult PLUGIN_API setupProcessing(ProcessSetup &setup) override {
    assert(!active);
    max_block = setup.maxSamplesPerBlock;
    rate = setup.sampleRate;
    ++setups;
    return kResultOk;
  }
  tresult PLUGIN_API setProcessing(TBool state) override {
    assert(active && processing != (bool)state);
    processing = state;
    ++(state ? starts : stops);
    return kResultOk;
  }
  tresult PLUGIN_API process(ProcessData &data) override {
    assert(active && processing);
    ++processed;
    // Every bus it reports has an entry with buffers of its width.
    assert(data.numInputs == getBusCount(kAudio, kInput));
    for (int32 b = 0; b < data.numInputs; ++b) {
      assert(data.inputs[b].numChannels == (b == 0 ? width.load() : 1));
      for (int32 c = 0; c < data.inputs[b].numChannels; ++c)
        assert(data.inputs[b].channelBuffers32[c]);
    }
    AudioBusBuffers &in = data.inputs[0], &out = data.outputs[0];
    for (int32 c = 0; c < out.numChannels; ++c)
      for (int32 n = 0; n < data.numSamples; ++n)
        out.channelBuffers32[c][n] = in.channelBuffers32[c][n] * 2;
    points = 0;
    IParameterChanges *changes = data.inputParameterChanges;
    for (int32 i = 0; changes && i < changes->getParameterCount(); ++i) {
      IParamValueQueue *queue = changes->getParameterData(i);
      if (queue->getParameterId() != watched) continue;
      int32 offset;
      points = queue->getPointCount();
      queue->getPoint(0, offset, value);
    }
    if (int32 flags = ask_in_process.exchange(0)) {
      // A new bus layout: a sidechain appears and the main buses fall back
      // to stereo, so the host has to ask for its width again.
      if (flags & RestartFlags::kIoChanged) {
        aux = true;
        width = 2;
      }
      handler->restartComponent(flags);
    }
    return kResultOk;
  }
  uint32 PLUGIN_API getTailSamples() override { return 0; }
};

// One mono block of 0.25 in; true when the plugin's answer came out, false
// when the block was silence.
static bool reload_block(Host *h) {
  float in[64], out[64];
  float *ins[] = {in}, *outs[] = {out};
  for (unsigned n = 0; n < 64; ++n) in[n] = .25f, out[n] = -1;
  vst3_process(h, 64, ins, outs);
  bool silent = true, answered = true;
  for (unsigned n = 0; n < 64; ++n) {
    silent &= out[n] == 0;
    answered &= out[n] == .5f;
  }
  assert(silent != answered);
  return answered;
}

struct ReloadRun {
  Host *h;
  ReloadPlugin *plugin;
  unsigned until;  // run until the plugin has been activated this often
  std::atomic<bool> done{false};
  unsigned answered = 0;
};

static void *reload_audio(void *data) {
  auto *run = static_cast<ReloadRun *>(data);
  for (unsigned block = 0; run->plugin->activations < run->until; ++block) {
    if (block % 50 == 0) run->plugin->ask_in_process = RestartFlags::kReloadComponent;
    run->answered += reload_block(run->h);
  }
  run->done = true;
  return nullptr;
}

// The plugin asks to be reloaded while it runs. The host does it in place:
// the audio thread lets go and plays silence, the main thread stops,
// deactivates, arranges, sets up, activates and starts it again with the
// same instance, the new latency is read, and the next block is processed.
// The helper never fails, and the plugin's parameters are neither lost nor
// sent again.
static void reload_in_place() {
  Host *h = test_audio_host_new(1);
  Vst3 v;
  ReloadPlugin plugin;
  ComponentHandler handler(&v);
  plugin.handler = &handler;
  v.h = h;
  v.component = &plugin;
  v.processor = &plugin;
  v.in_changes.reserve(1);
  v.out_changes.reserve(1);
  host_set_impl(h, &v);
  // One control whose daemon value is already in step, so the tick leaves
  // the controller alone and the test needs none.
  Record record;
  record.id = plugin.watched;
  record.ctl = host_add_control(h, "5", 0, 1, .5f, false, &record);
  record.readonly = false;
  record.normalized_desired = .5;
  record.normalized_observed = .5;
  record.last_sent = .5;
  record.last_plain = .5f;
  record.seen = true;
  v.records.push_back(&record);

  // Loading: the stereo plugin is asked for a mono arrangement once.
  assert(arrange_buses(h, &v) && vst3_activate(h));
  assert(plugin.arranged == 1 && plugin.width == 1 && plugin.setups == 1);
  assert(plugin.max_block == (int32)MAX_FRAMES && plugin.rate == 48000);
  assert(reload_block(h) && plugin.processed == 1 && plugin.starts == 1);
  assert(held_latency(&v) == 256 && plugin.asked == 1);

  plugin.ask_in_process = RestartFlags::kReloadComponent;
  assert(reload_block(h));  // the block that asked is still answered
  plugin.latency = 512;
  vst3_main_thread(h);  // asks the audio thread to let go
  assert(v.restart == RESTART_HOLD && plugin.deactivations == 0 && plugin.stops == 0);
  // With no block run since, the audio thread may still be about to call
  // the plugin, so ticks wait for it however many go by.
  for (int tick = 0; tick < 5; ++tick) {
    vst3_main_thread(h);
    assert(v.restart == RESTART_HOLD && plugin.deactivations == 0 && plugin.stops == 0);
  }
  assert(!reload_block(h) && v.restart == RESTART_HELD);
  assert(!reload_block(h) && plugin.processed == 2);  // held: not called
  record.normalized_desired = .75;  // an edit while held waits for the plugin
  vst3_main_thread(h);  // stops, deactivates, sets up, activates, starts
  assert(v.restart == RESTART_NONE && plugin.active && plugin.processing);
  assert(plugin.stops == 1 && plugin.deactivations == 1);
  assert(plugin.setups == 2 && plugin.activations == 2 && plugin.starts == 2);
  assert(plugin.arranged == 1 && plugin.width == 1);  // still mono: not asked again
  assert(held_latency(&v) == 512 && plugin.asked == 2);
  assert(reload_block(h) && plugin.processed == 3);
  assert(plugin.points == 1 && plugin.value == .75);
  for (int tick = 0; tick < 10; ++tick) {
    vst3_main_thread(h);
    assert(reload_block(h) && plugin.points == 0);  // nothing sent again
  }
  assert(plugin.activations == 2 && plugin.asked == 2);
  assert(!test_host_audio_failed(h));

  // A new bus layout: the sidechain gets a bus entry of its own, and the
  // main buses are asked for the chain's width again.
  plugin.ask_in_process = RestartFlags::kIoChanged;
  assert(reload_block(h));
  vst3_main_thread(h);
  assert(!reload_block(h));
  vst3_main_thread(h);
  assert(plugin.activations == 3 && plugin.arranged == 2 && plugin.width == 1);
  assert(v.in_buses.size() == 2 && v.data.numInputs == 2);
  assert(reload_block(h) && !test_host_audio_failed(h));

  // A request made inside the activation that answers one is answered by
  // it, not by a reload after it, which would go round for ever.
  plugin.ask_in_process = plugin.ask_in_activate = RestartFlags::kReloadComponent;
  assert(reload_block(h));
  vst3_main_thread(h);
  assert(!reload_block(h));
  vst3_main_thread(h);
  assert(plugin.activations == 4 && plugin.deactivations == 3);
  for (int tick = 0; tick < 10; ++tick) {
    vst3_main_thread(h);
    assert(reload_block(h));
  }
  assert(plugin.activations == 4 && !test_host_audio_failed(h));
  plugin.ask_in_activate = 0;

  // The same with the audio on a thread of its own, as in the host: the
  // plugin asks every 50 blocks while the main thread ticks, for 500
  // reloads. Its own checks fail if it is ever called out of order.
  unsigned before = plugin.activations;
  ReloadRun run;
  run.h = h;
  run.plugin = &plugin;
  run.until = before + 500;
  pthread_t audio;
  assert(pthread_create(&audio, nullptr, reload_audio, &run) == 0);
  while (!run.done) {
    vst3_main_thread(h);
    struct timespec pause = {0, 100000};
    nanosleep(&pause, nullptr);
  }
  assert(pthread_join(audio, nullptr) == 0);
  // Finish a reload the last blocks asked for, with audio on this thread.
  for (int tick = 0; tick < 3; ++tick) {
    vst3_main_thread(h);
    reload_block(h);
  }
  assert(v.restart == RESTART_NONE && !v.restart_requested);
  assert(reload_block(h) && run.answered > 0);
  assert(plugin.activations >= before + 500 && !test_host_audio_failed(h));

  // A plugin that will not activate again ends the helper, and stays held
  // so the audio thread never calls it while inactive.
  plugin.activate_result = kResultFalse;
  plugin.ask_in_process = RestartFlags::kReloadComponent;
  assert(reload_block(h));
  vst3_main_thread(h);
  assert(!reload_block(h));
  unsigned processed = plugin.processed;
  vst3_main_thread(h);
  assert(test_host_audio_failed(h) && !plugin.active && !v.active);
  assert(!reload_block(h) && plugin.processed == processed);
  vst3_deactivate(h);  // shutdown has nothing left to switch off
  assert(plugin.deactivations == plugin.activations);
  host_set_impl(h, nullptr);
  test_audio_host_free(h);
  puts("PASS: a VST3 plugin that asks to be reloaded is reactivated in place and keeps processing");
}

int main(int argc, char **argv) {
  if (argc == 1 || !strcmp(argv[1], "processing")) processing_failure();
  if (argc == 1 || !strcmp(argv[1], "parameters")) parameters();
  if (argc == 1 || !strcmp(argv[1], "stream")) stream();
  if (argc == 1 || !strcmp(argv[1], "attributes")) attributes();
  if (argc == 1 || !strcmp(argv[1], "strings")) strings();
  if (argc == 1 || !strcmp(argv[1], "restart")) restart();
  if (argc == 1 || !strcmp(argv[1], "reload")) reload_in_place();
  if (argc == 1 || !strcmp(argv[1], "latency")) latency();
  if (argc == 1 || !strcmp(argv[1], "teardown")) teardown();
  if (argc == 1 || !strcmp(argv[1], "own-block")) own_block_setup();
  if (argc == 1 || !strcmp(argv[1], "bypass")) bypass_parameter();
  puts("VST3 host bounds passed");
}
