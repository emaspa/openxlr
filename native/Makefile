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
# Track headers from each compilation, including SDKs passed with -isystem.
# Replace the dependency file only after a successful compile.
DEPFLAGS = -MD -MP -MF $@.d.tmp -MT $@
.DELETE_ON_ERROR:

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
DF_CARGO = $(CARGO)
ifneq ($(strip $(OPENXLR_DEEPFILTER_BUILD_CACHE)),)
DF_CARGO = python3 build-cache.py $(CARGO)
endif
DF_CARGO_FLAGS = --release --locked --target-dir target $(if $(wildcard deepfilter/vendor/),--offline \
                 --config 'source.crates-io.replace-with="vendored"' \
                 --config 'source.vendored.directory="vendor"')
ifeq ($(DEEPFILTER),1)
DEEPFILTER_LIB ?= deepfilter/target/release/libopenxlr_deepfilter.a
DF_DEPS = $(DEEPFILTER_LIB)
DF_LINK = -Wl,--gc-sections $(DEEPFILTER_LIB) -lgcc_s -lutil -lrt
endif

OBJECTS = host.o measure.o lv2.o clap.o vst3.o sound-check.o eq.o duck.o level.o player.o denoise.o \
          voice.o voice-stretch.o dynamics.o bridge.o $(RNN_OBJECTS)
TEST_OBJECTS = $(addprefix tests/,editor.o audio.o own-block.o measure.o clap.o lv2.o vst3.o host.o \
               lifecycle.o sound-check.o eq.o duck.o denoise.o denoise-handoff.o denoise-recovery.o \
               voice.o dynamics.o player.o)
DEP_TARGETS = $(OBJECTS) $(TEST_OBJECTS) tests/bridge tests/scan-plugin.so \
              tests/latency.lv2/latency.so tests/gain.lv2/gain.so
DEP_FILES = $(addsuffix .d,$(DEP_TARGETS))
# The stages and their tests link these beside the noise removal objects.
DENOISE_OBJECTS = denoise.o $(RNN_OBJECTS)
# The microphone stage (eq.c) runs noise removal first, the expander and the
# compressor around the EQ, and the voice changer last; its tests link them.
STAGE_OBJECTS = $(DENOISE_OBJECTS) voice.o voice-stretch.o dynamics.o

openxlr-lv2-host: $(OBJECTS) $(DF_DEPS)
	$(CXX) $(OBJECTS) -o $@ $(LDFLAGS) $(DF_LINK) $(LDLIBS)

%.o: %.c
	$(CC) $(CPPFLAGS) $(CFLAGS) $(DEPFLAGS) -c $< -o $@
	@mv $@.d.tmp $@.d

%.o: %.cpp
	$(CXX) $(CPPFLAGS) $(CXXFLAGS) $(DEPFLAGS) -c $< -o $@
	@mv $@.d.tmp $@.d

rnnoise/src/%.o: rnnoise/src/%.c
	$(CC) $(RNN_CPPFLAGS) $(RNN_CFLAGS) $(DEPFLAGS) -c $< -o $@
	@mv $@.d.tmp $@.d

rnnoise/src/x86/nnet_sse4_1.o: RNN_CFLAGS += -msse4.1
rnnoise/src/x86/nnet_avx2.o: RNN_CFLAGS += -mavx -mfma -mavx2

# Which optional engines denoise.c is compiled with; rewritten only when
# that changes, so switching DEEPFILTER rebuilds what depends on it.
build-config.h: FORCE
	@printf '#define OPENXLR_DEEPFILTER %s\n' '$(DEEPFILTER)' > $@.new
	@if cmp -s $@.new $@; then rm -f $@.new; else mv $@.new $@; fi

# The generated header must exist before the first compile can record it.
denoise.o tests/denoise-handoff.o tests/denoise-recovery.o: %.o: %.c build-config.h
	$(CC) $(CPPFLAGS) -Irnnoise/include $(CFLAGS) $(DEPFLAGS) -c $< -o $@
	@mv $@.d.tmp $@.d

tests/denoise.o: build-config.h

$(DF_MODEL):
	mkdir -p $(@D)
	curl -fsSL --retry 3 -o $@.part '$(DF_MODEL_URL)'
	mv $@.part $@

deepfilter/model/verified: $(DF_MODEL)
	echo '$(DF_MODEL_SHA256)  $(DF_MODEL)' | sha256sum -c --quiet -
	touch $@

deepfilter/target/release/libopenxlr_deepfilter.a: deepfilter/model/verified deepfilter/Cargo.toml \
		deepfilter/Cargo.lock deepfilter/src/lib.rs deepfilter/libdf/Cargo.toml $(wildcard deepfilter/libdf/src/*.rs)
	cd deepfilter && $(DF_CARGO) build $(DF_CARGO_FLAGS)
	touch $@

.PHONY: FORCE deepfilter
FORCE:
# The library alone, for a package that builds it in a step of its own.
deepfilter: deepfilter/target/release/libopenxlr_deepfilter.a

tests/editor: tests/editor.o lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

tests/audio: tests/audio.o lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

# The plugin's own block: the audio callback and its worker, without a graph.
tests/own-block: tests/own-block.o lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

# The measured delay: known delays, a noise remover, the own block, and
# outputs that must never give a figure, without a graph.
tests/measure: tests/measure.o measure.o lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

# This one compiles clap.c into itself to reach the layout reading, so it
# links without clap.o.
tests/clap: tests/clap.o lv2.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

# This one compiles lv2.c into itself, and loads the latency fixture.
tests/lv2: tests/lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

tests/latency.lv2/latency.so: tests/latency.lv2/latency.c
	$(CC) $(CPPFLAGS) $(CFLAGS) $(DEPFLAGS) -shared -fPIC $< -o $@ -lm
	@mv $@.d.tmp $@.d

tests/gain.lv2/gain.so: tests/gain.lv2/gain.c
	$(CC) $(CPPFLAGS) $(CFLAGS) $(DEPFLAGS) -shared -fPIC $< -o $@
	@mv $@.d.tmp $@.d

tests/host.o: tests/vst3-host.c
	$(CC) $(CPPFLAGS) $(CFLAGS) $(DEPFLAGS) -c $< -o $@
	@mv $@.d.tmp $@.d

# The C++ test includes vst3.cpp to exercise host-owned SDK objects.
tests/vst3: tests/vst3.o tests/host.o lv2.o clap.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

# A plugin lifecycle check against a real plugin, run by hand (see README).
tests/lifecycle: tests/lifecycle.o lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

.PHONY: test test-editor test-audio test-clap test-lv2 test-vst3 test-scan test-fixtures
# The editor tests need a display; the others need nothing.
test: test-audio test-clap test-lv2 test-vst3 test-scan test-editor

tests/scan-plugin.so: tests/scan-plugin.cpp
	$(CXX) $(CPPFLAGS) $(CXXFLAGS) $(DEPFLAGS) -std=c++20 -shared -fPIC $< -o $@
	@mv $@.d.tmp $@.d

test-scan: openxlr-lv2-host tests/scan-plugin.so
	python3 tests/scan.py

test-editor: tests/editor
	./tests/editor

test-audio: tests/audio tests/own-block tests/measure
	./tests/audio
	./tests/own-block
	./tests/measure

test-clap: tests/clap
	./tests/clap

# The LV2 bundles under tests/ that the private PipeWire tests load as inserts.
test-fixtures: tests/latency.lv2/latency.so tests/gain.lv2/gain.so
	@:

test-lv2: tests/lv2 test-fixtures
	./tests/lv2

test-vst3: tests/vst3
	./tests/vst3

.PHONY: clean clean-native clean-deepfilter
clean: clean-native clean-deepfilter

# Rebuild the C and C++ helper without discarding the Rust library.
clean-native:
	rm -f openxlr-lv2-host $(OBJECTS) tests/editor tests/editor.o tests/audio tests/audio.o tests/own-block tests/own-block.o tests/measure tests/measure.o tests/clap tests/clap.o tests/lifecycle tests/lifecycle.o tests/lv2 tests/lv2.o tests/latency.lv2/latency.so tests/gain.lv2/gain.so tests/vst3 tests/vst3.o tests/host.o tests/scan-plugin.so
	rm -f $(DEP_FILES) $(addsuffix .tmp,$(DEP_FILES))

# Sound Check: the loop backend and the level meter beside it, driven
# directly with deterministic samples and no audio server.
tests/sound-check: tests/sound-check.o lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

.PHONY: test-sound-check clean-sound-check
test: test-sound-check
test-sound-check: tests/sound-check
	./tests/sound-check

clean-native: clean-sound-check
clean-sound-check:
	rm -f tests/sound-check tests/sound-check.o

# The microphone EQ: the backend driven directly, its response measured
# against tools/eq-reference.py's table, with no audio server.
tests/eq: tests/eq.o $(STAGE_OBJECTS) lv2.o clap.o vst3.o $(DF_DEPS)
	$(CXX) $(filter %.o,$^) -o $@ $(LDFLAGS) $(DF_LINK) $(LDLIBS)

.PHONY: test-eq clean-eq
test: test-eq
test-eq: tests/eq
	./tests/eq tests/eq-reference.txt

clean-native: clean-eq
clean-eq:
	rm -f tests/eq tests/eq.o

# The duck stage: detector and gain timing with no audio server.
tests/duck: tests/duck.o lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

.PHONY: test-duck clean-duck
test: test-duck
test-duck: tests/duck
	./tests/duck

clean-native: clean-duck
clean-duck:
	rm -f tests/duck tests/duck.o

# Noise removal: both engines driven through the microphone stage with a
# synthetic voice in noise, with no audio server. DeepFilterNet is checked
# when this build carries it.
tests/denoise: tests/denoise.o $(STAGE_OBJECTS) lv2.o clap.o vst3.o $(DF_DEPS)
	$(CXX) $(filter %.o,$^) -o $@ $(LDFLAGS) $(DF_LINK) $(LDLIBS)

# The handoff test compiles denoise.c with a fake DeepFilterNet model.
tests/denoise-handoff: tests/denoise-handoff.o $(RNN_OBJECTS) lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

# Recovery from an engine that answers with NaN: RNNoise's own state, and a
# fake DeepFilterNet model, with denoise.c compiled in.
tests/denoise-recovery: tests/denoise-recovery.o $(RNN_OBJECTS) lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

.PHONY: test-denoise clean-denoise clean-denoise-objects
test: test-denoise
test-denoise: tests/denoise tests/denoise-handoff tests/denoise-recovery openxlr-lv2-host
	./tests/denoise ./openxlr-lv2-host
	./tests/denoise-handoff
	./tests/denoise-recovery

clean-native: clean-denoise-objects
clean-denoise: clean-denoise-objects clean-deepfilter
clean-denoise-objects:
	rm -f tests/denoise tests/denoise.o tests/denoise-handoff tests/denoise-handoff.o tests/denoise-recovery \
	      tests/denoise-recovery.o build-config.h $(RNN_OBJECTS) rnnoise/src/x86/*.o

clean-deepfilter:
	rm -rf deepfilter/target deepfilter/model/verified

# The voice changer: every voice driven through the microphone stage over a
# synthetic voice, with no audio server. `tests/voice --render IN.wav DIR`
# writes every voice over a recording for listening.
tests/voice: tests/voice.o $(STAGE_OBJECTS) lv2.o clap.o vst3.o $(DF_DEPS)
	$(CXX) $(filter %.o,$^) -o $@ $(LDFLAGS) $(DF_LINK) $(LDLIBS)

.PHONY: test-voice clean-voice
test: test-voice
test-voice: tests/voice
	./tests/voice

clean-native: clean-voice
clean-voice:
	rm -f tests/voice tests/voice.o

# The compressor and the expander: driven through the microphone stage with
# steady and stepped levels and with no audio server, checked against their
# static curves, their time constants and their meters.
tests/dynamics: tests/dynamics.o $(STAGE_OBJECTS) lv2.o clap.o vst3.o $(DF_DEPS)
	$(CXX) $(filter %.o,$^) -o $@ $(LDFLAGS) $(DF_LINK) $(LDLIBS)

.PHONY: test-dynamics clean-dynamics
test: test-dynamics
test-dynamics: tests/dynamics
	./tests/dynamics

clean-native: clean-dynamics
clean-dynamics:
	rm -f tests/dynamics tests/dynamics.o

# The soundboard player: decoding, resampling, press modes and ramps, driven
# directly with no audio server. It asks the helper for its `formats` and
# `probe` lines.
tests/player: tests/player.o lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

.PHONY: test-player clean-player
test: test-player
test-player: tests/player openxlr-lv2-host
	./tests/player ./openxlr-lv2-host

clean-native: clean-player
clean-player:
	rm -f tests/player tests/player.o

# The clock bridge: its ring, loop and resampler between two simulated
# clocks a known distance apart, with jitter, stalls and no audio server.
tests/bridge: tests/bridge.c
	$(CC) $(CPPFLAGS) $(CFLAGS) $(DEPFLAGS) $< -o $@ $(LDFLAGS) $(LDLIBS)
	@mv $@.d.tmp $@.d

.PHONY: test-bridge clean-bridge
test: test-bridge
test-bridge: tests/bridge
	./tests/bridge

clean-native: clean-bridge
clean-bridge:
	rm -f tests/bridge

# Objects from a previous checkout or a removed dependency file need one
# compile to recover their prerequisites. Keep this below the default goal.
EXISTING_DEP_FILES := $(wildcard $(DEP_FILES))
$(filter-out $(EXISTING_DEP_FILES:.d=),$(DEP_TARGETS)): FORCE
-include $(EXISTING_DEP_FILES)
