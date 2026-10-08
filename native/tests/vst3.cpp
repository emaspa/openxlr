// SPDX-License-Identifier: GPL-3.0-only
// Exercise the host-owned SDK objects directly, without a plugin or audio server.
#include "../vst3.cpp"
#include <cassert>
#include <limits>

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
  puts("VST3 host bounds passed");
}
