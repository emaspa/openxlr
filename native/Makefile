# The optional plugin host: one process per insert, LV2, CLAP and VST3, with
# the plugin's own editor. The CLAP and VST3 interface headers are vendored
# under clap/ and vst3/ (both MIT), and the voice changer's pitch shifter,
# Signalsmith Stretch, under signalsmith/ (MIT, header-only), so building
# needs nothing beyond the PipeWire, lilv, LV2, X11 and libsndfile
# development files and a C and C++ compiler.
PKGS = libpipewire-0.3 lilv-0 lv2 x11 sndfile
# Taken before the flags below are added: vendored code is built with the
# caller's flags but not with this project's warnings.
RNN_CFLAGS := -O2 -g $(CFLAGS) -w
CPPFLAGS += $(shell pkg-config --cflags $(PKGS)) -isystem clap/include -isystem vst3 -isystem signalsmith
WARN = -O2 -g -Wall -Wextra -Werror -Wno-unused-parameter
CFLAGS += -std=c11 $(WARN)
CXXFLAGS += -std=c++17 $(WARN) -fno-exceptions -fno-rtti
LDLIBS += $(shell pkg-config --libs $(PKGS)) -ldl -lm -pthread

# Noise removal (denoise.c). RNNoise 0.2 (Xiph, BSD-3-Clause) is vendored
# under rnnoise/ and always built in. DeepFilterNet 0.5.6 (MIT or
# Apache-2.0) is a Rust static library from deepfilter/: DEEPFILTER=1 builds
# it in and fails without cargo, DEEPFILTER=0 leaves it out, and auto, the
# default, builds it in when cargo is on the PATH. Every package sets 1.
# `openxlr-lv2-host engines` says which engines a build carries.
DEEPFILTER ?= auto
ifeq ($(DEEPFILTER),auto)
override DEEPFILTER := $(if $(shell command -v $(or $(CARGO),cargo) 2>/dev/null),1,0)
endif

RNN_SOURCES = $(addprefix rnnoise/src/,denoise.c rnn.c pitch.c kiss_fft.c celt_lpc.c nnet.c \
              nnet_default.c parse_lpcnet_weights.c rnnoise_data.c rnnoise_tables.c)
RNN_CPPFLAGS = -Irnnoise/include -Irnnoise/src -DRNNOISE_BUILD
# On x86 the network picks SSE4.1 or AVX2 code when the processor has it.
ifneq ($(filter x86_64 i386 i486 i586 i686,$(firstword $(subst -, ,$(shell $(CC) -dumpmachine)))),)
RNN_SOURCES += $(addprefix rnnoise/src/x86/,x86_dnn_map.c x86cpu.c nnet_sse4_1.c nnet_avx2.c)
RNN_CPPFLAGS += -DRNN_ENABLE_X86_RTCD -DCPU_INFO_BY_ASM
endif
RNN_OBJECTS = $(RNN_SOURCES:.c=.o)
RNN_HEADERS = $(wildcard rnnoise/include/*.h rnnoise/src/*.h rnnoise/src/x86/*.h)

# DeepFilterNet's model is fetched once and checked against its SHA-256; a
# package that builds offline puts the same file at DF_MODEL first. The
# crates are fetched by cargo, or read from deepfilter/vendor when a package
# vendored them there. DEEPFILTER_LIB names a library built elsewhere (Nix
# builds it as a derivation of its own) and skips cargo.
DF_VERSION = 0.5.6
DF_MODEL = deepfilter/model/DeepFilterNet3_onnx.tar.gz
DF_MODEL_URL = https://raw.githubusercontent.com/Rikorose/DeepFilterNet/v$(DF_VERSION)/models/DeepFilterNet3_onnx.tar.gz
DF_MODEL_SHA256 = c94d91f70911001c946e0fabb4aa9adc37045f45a03b56008cb0c8244cb63616
CARGO ?= cargo
DF_CARGO_FLAGS = --release --locked --target-dir target $(if $(wildcard deepfilter/vendor/),--offline \
                 --config 'source.crates-io.replace-with="vendored"' \
                 --config 'source.vendored.directory="vendor"')
ifeq ($(DEEPFILTER),1)
DEEPFILTER_LIB ?= deepfilter/target/release/libopenxlr_deepfilter.a
DF_DEPS = $(DEEPFILTER_LIB)
DF_LINK = -Wl,--gc-sections $(DEEPFILTER_LIB) -lgcc_s -lutil -lrt
endif

OBJECTS = host.o lv2.o clap.o vst3.o sound-check.o eq.o duck.o level.o player.o denoise.o \
          voice.o voice-stretch.o dynamics.o $(RNN_OBJECTS)
HEADERS = host.h icon.h denoise.h dynamics.h eq.h voice.h $(wildcard clap/include/clap/*.h clap/include/clap/*/*.h) \
          $(wildcard vst3/pluginterfaces/*/*.h)
# The stages and their tests link these beside the noise removal objects.
DENOISE_OBJECTS = denoise.o $(RNN_OBJECTS)
# The microphone stage (eq.c) runs noise removal first, the expander and the
# compressor around the EQ, and the voice changer last; its tests link them.
STAGE_OBJECTS = $(DENOISE_OBJECTS) voice.o voice-stretch.o dynamics.o
SIGNALSMITH_HEADERS = signalsmith/signalsmith-stretch.h $(wildcard signalsmith/signalsmith-linear/*.h)

openxlr-lv2-host: $(OBJECTS) $(DF_DEPS)
	$(CXX) $(OBJECTS) -o $@ $(LDFLAGS) $(DF_LINK) $(LDLIBS)

%.o: %.c $(HEADERS)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

%.o: %.cpp $(HEADERS)
	$(CXX) $(CPPFLAGS) $(CXXFLAGS) -c $< -o $@

rnnoise/src/%.o: rnnoise/src/%.c $(RNN_HEADERS)
	$(CC) $(RNN_CPPFLAGS) $(RNN_CFLAGS) -c $< -o $@

rnnoise/src/x86/nnet_sse4_1.o: RNN_CFLAGS += -msse4.1
rnnoise/src/x86/nnet_avx2.o: RNN_CFLAGS += -mavx -mfma -mavx2

# Which optional engines denoise.c is compiled with; rewritten only when
# that changes, so switching DEEPFILTER rebuilds what depends on it.
build-config.h: FORCE
	@printf '#define OPENXLR_DEEPFILTER %s\n' '$(DEEPFILTER)' > $@.new
	@if cmp -s $@.new $@; then rm -f $@.new; else mv $@.new $@; fi

denoise.o: denoise.c build-config.h rnnoise/include/rnnoise.h $(HEADERS)
	$(CC) $(CPPFLAGS) -Irnnoise/include $(CFLAGS) -c $< -o $@

voice-stretch.o: voice-stretch.cpp $(SIGNALSMITH_HEADERS) $(HEADERS)
	$(CXX) $(CPPFLAGS) $(CXXFLAGS) -c $< -o $@

$(DF_MODEL):
	mkdir -p $(@D)
	curl -fsSL --retry 3 -o $@.part '$(DF_MODEL_URL)'
	mv $@.part $@

deepfilter/model/verified: $(DF_MODEL)
	echo '$(DF_MODEL_SHA256)  $(DF_MODEL)' | sha256sum -c --quiet -
	touch $@

deepfilter/target/release/libopenxlr_deepfilter.a: deepfilter/model/verified deepfilter/Cargo.toml \
		deepfilter/Cargo.lock deepfilter/src/lib.rs deepfilter/libdf/Cargo.toml $(wildcard deepfilter/libdf/src/*.rs)
	cd deepfilter && $(CARGO) build $(DF_CARGO_FLAGS)
	touch $@

.PHONY: FORCE deepfilter
FORCE:
# The library alone, for a package that builds it in a step of its own.
deepfilter: deepfilter/target/release/libopenxlr_deepfilter.a

tests/editor.o: tests/editor.c host.c $(HEADERS)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

tests/editor: tests/editor.o lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

tests/audio.o: tests/audio.c host.c $(HEADERS)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

tests/audio: tests/audio.o lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

# The plugin's own block: the audio callback and its worker, without a graph.
tests/own-block.o: tests/own-block.c host.c $(HEADERS)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

tests/own-block: tests/own-block.o lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

# This one compiles clap.c into itself to reach the layout reading, so it
# links without clap.o.
tests/clap.o: tests/clap.c host.c clap.c $(HEADERS)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

tests/clap: tests/clap.o lv2.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

# This one compiles lv2.c into itself, and loads the latency fixture.
tests/lv2.o: tests/lv2.c host.c lv2.c $(HEADERS)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

tests/lv2: tests/lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

tests/latency.lv2/latency.so: tests/latency.lv2/latency.c
	$(CC) $(CPPFLAGS) $(CFLAGS) -shared -fPIC $< -o $@ -lm

tests/gain.lv2/gain.so: tests/gain.lv2/gain.c
	$(CC) $(CPPFLAGS) $(CFLAGS) -shared -fPIC $< -o $@

# The C++ test includes vst3.cpp to exercise host-owned SDK objects.
tests/vst3.o: tests/vst3.cpp vst3.cpp $(HEADERS)
	$(CXX) $(CPPFLAGS) $(CXXFLAGS) -c $< -o $@

tests/host.o: tests/vst3-host.c host.c $(HEADERS)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

tests/vst3: tests/vst3.o tests/host.o lv2.o clap.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

# A plugin lifecycle check against a real plugin, run by hand (see README).
tests/lifecycle.o: tests/lifecycle.c host.c $(HEADERS)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

tests/lifecycle: tests/lifecycle.o lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

.PHONY: test test-editor test-audio test-clap test-lv2 test-vst3 test-scan test-fixtures clean
# The editor tests need a display; the others need nothing.
test: test-audio test-clap test-lv2 test-vst3 test-scan test-editor

tests/scan-plugin.so: tests/scan-plugin.cpp $(HEADERS)
	$(CXX) $(CPPFLAGS) $(CXXFLAGS) -std=c++20 -shared -fPIC $< -o $@

test-scan: openxlr-lv2-host tests/scan-plugin.so
	python3 tests/scan.py

test-editor: tests/editor
	./tests/editor

test-audio: tests/audio tests/own-block
	./tests/audio
	./tests/own-block

test-clap: tests/clap
	./tests/clap

# The LV2 bundles under tests/ that the private PipeWire tests load as inserts.
test-fixtures: tests/latency.lv2/latency.so tests/gain.lv2/gain.so
	@:

test-lv2: tests/lv2 test-fixtures
	./tests/lv2

test-vst3: tests/vst3
	./tests/vst3

clean:
	rm -f openxlr-lv2-host $(OBJECTS) tests/editor tests/editor.o tests/audio tests/audio.o tests/own-block tests/own-block.o tests/clap tests/clap.o tests/lifecycle tests/lifecycle.o tests/lv2 tests/lv2.o tests/latency.lv2/latency.so tests/gain.lv2/gain.so tests/vst3 tests/vst3.o tests/host.o tests/scan-plugin.so

# Sound Check: the loop backend and the level meter beside it, driven
# directly with deterministic samples and no audio server.
tests/sound-check.o: tests/sound-check.c sound-check.c level.c host.c $(HEADERS)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

tests/sound-check: tests/sound-check.o lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

.PHONY: test-sound-check clean-sound-check
test: test-sound-check
test-sound-check: tests/sound-check
	./tests/sound-check

clean: clean-sound-check
clean-sound-check:
	rm -f tests/sound-check tests/sound-check.o

# The microphone EQ: the backend driven directly, its response measured
# against tools/eq-reference.py's table, with no audio server.
tests/eq.o: tests/eq.c eq.c host.c $(HEADERS)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

tests/eq: tests/eq.o $(STAGE_OBJECTS) lv2.o clap.o vst3.o $(DF_DEPS)
	$(CXX) $(filter %.o,$^) -o $@ $(LDFLAGS) $(DF_LINK) $(LDLIBS)

.PHONY: test-eq clean-eq
test: test-eq
test-eq: tests/eq
	./tests/eq tests/eq-reference.txt

clean: clean-eq
clean-eq:
	rm -f tests/eq tests/eq.o

# The duck stage: detector and gain timing with no audio server.
tests/duck.o: tests/duck.c duck.c host.c $(HEADERS)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

tests/duck: tests/duck.o lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

.PHONY: test-duck clean-duck
test: test-duck
test-duck: tests/duck
	./tests/duck

clean: clean-duck
clean-duck:
	rm -f tests/duck tests/duck.o

# Noise removal: both engines driven through the microphone stage with a
# synthetic voice in noise, with no audio server. DeepFilterNet is checked
# when this build carries it.
tests/denoise.o: tests/denoise.c eq.c host.c build-config.h $(HEADERS)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

tests/denoise: tests/denoise.o $(STAGE_OBJECTS) lv2.o clap.o vst3.o $(DF_DEPS)
	$(CXX) $(filter %.o,$^) -o $@ $(LDFLAGS) $(DF_LINK) $(LDLIBS)

# The handoff test compiles denoise.c with a fake DeepFilterNet model.
tests/denoise-handoff.o: tests/denoise-handoff.c denoise.c host.c build-config.h $(HEADERS)
	$(CC) $(CPPFLAGS) -Irnnoise/include $(CFLAGS) -c $< -o $@

tests/denoise-handoff: tests/denoise-handoff.o $(RNN_OBJECTS) lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

.PHONY: test-denoise clean-denoise
test: test-denoise
test-denoise: tests/denoise tests/denoise-handoff openxlr-lv2-host
	./tests/denoise ./openxlr-lv2-host
	./tests/denoise-handoff

clean: clean-denoise
clean-denoise:
	rm -f tests/denoise tests/denoise.o tests/denoise-handoff tests/denoise-handoff.o build-config.h $(RNN_OBJECTS) rnnoise/src/x86/*.o
	rm -rf deepfilter/target deepfilter/model/verified

# The voice changer: every voice driven through the microphone stage over a
# synthetic voice, with no audio server. `tests/voice --render IN.wav DIR`
# writes every voice over a recording for listening.
tests/voice.o: tests/voice.c eq.c host.c $(HEADERS)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

tests/voice: tests/voice.o $(STAGE_OBJECTS) lv2.o clap.o vst3.o $(DF_DEPS)
	$(CXX) $(filter %.o,$^) -o $@ $(LDFLAGS) $(DF_LINK) $(LDLIBS)

.PHONY: test-voice clean-voice
test: test-voice
test-voice: tests/voice
	./tests/voice

clean: clean-voice
clean-voice:
	rm -f tests/voice tests/voice.o

# The compressor and the expander: driven through the microphone stage with
# steady and stepped levels and with no audio server, checked against their
# static curves, their time constants and their meters.
tests/dynamics.o: tests/dynamics.c eq.c host.c $(HEADERS)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

tests/dynamics: tests/dynamics.o $(STAGE_OBJECTS) lv2.o clap.o vst3.o $(DF_DEPS)
	$(CXX) $(filter %.o,$^) -o $@ $(LDFLAGS) $(DF_LINK) $(LDLIBS)

.PHONY: test-dynamics clean-dynamics
test: test-dynamics
test-dynamics: tests/dynamics
	./tests/dynamics

clean: clean-dynamics
clean-dynamics:
	rm -f tests/dynamics tests/dynamics.o

# The soundboard player: decoding, resampling, press modes and ramps, driven
# directly with no audio server. It asks the helper for its `formats` and
# `probe` lines.
tests/player.o: tests/player.c player.c host.c $(HEADERS)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

tests/player: tests/player.o lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

.PHONY: test-player clean-player
test: test-player
test-player: tests/player openxlr-lv2-host
	./tests/player ./openxlr-lv2-host

clean: clean-player
clean-player:
	rm -f tests/player tests/player.o
