// SPDX-License-Identifier: GPL-3.0-only
// Exercise the host-owned SDK objects directly, without a plugin or audio server.
#include "../vst3.cpp"
#include <cassert>
#include <limits>
#include <cerrno>
#include <fcntl.h>
#include <unistd.h>

extern "C" Host *test_host_new(void);
extern "C" void test_host_free(Host *h);

class Watch final : public Linux::ITimerHandler, public Linux::IEventHandler {
 public:
  PlugFrame *frame = nullptr;
  bool timer = false;
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
  void PLUGIN_API onTimer() override { ++calls; }
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
  pw_deinit();
  puts("PASS: VST3 teardown unregisters watches once and collects leftovers without closing plugin FDs");
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

// What a plugin may ask of the host after activation. Only a changed
// component or bus layout costs the process; a new latency figure does not.
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

// A processor that only answers the latency question, counting each time it
// is asked.
class LatencyProcessor final : public IAudioProcessor {
 public:
  uint32 samples = 0;
  int asked = 0;
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
  tresult PLUGIN_API setupProcessing(ProcessSetup &) override { return kResultOk; }
  tresult PLUGIN_API setProcessing(TBool) override { return kResultOk; }
  tresult PLUGIN_API process(ProcessData &) override { return kResultOk; }
  uint32 PLUGIN_API getTailSamples() override { return 0; }
};

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

int main(int argc, char **argv) {
  if (argc == 1 || !strcmp(argv[1], "parameters")) parameters();
  if (argc == 1 || !strcmp(argv[1], "stream")) stream();
  if (argc == 1 || !strcmp(argv[1], "attributes")) attributes();
  if (argc == 1 || !strcmp(argv[1], "restart")) restart();
  if (argc == 1 || !strcmp(argv[1], "latency")) latency();
  if (argc == 1 || !strcmp(argv[1], "teardown")) teardown();
  puts("VST3 host bounds passed");
}
