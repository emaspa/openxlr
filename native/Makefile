# The optional plugin host: one process per insert, LV2, CLAP and VST3, with
# the plugin's own editor. The CLAP and VST3 interface headers are vendored
# under clap/ and vst3/ (both MIT), so building needs nothing beyond the
# PipeWire, lilv, LV2 and X11 development files and a C and C++ compiler.
PKGS = libpipewire-0.3 lilv-0 lv2 x11
CPPFLAGS += $(shell pkg-config --cflags $(PKGS)) -isystem clap/include -isystem vst3
WARN = -O2 -g -Wall -Wextra -Werror -Wno-unused-parameter
CFLAGS += -std=c11 $(WARN)
CXXFLAGS += -std=c++17 $(WARN) -fno-exceptions -fno-rtti
LDLIBS += $(shell pkg-config --libs $(PKGS)) -ldl -lm -pthread

OBJECTS = host.o lv2.o clap.o vst3.o sound-check.o eq.o duck.o
HEADERS = host.h icon.h $(wildcard clap/include/clap/*.h clap/include/clap/*/*.h) \
          $(wildcard vst3/pluginterfaces/*/*.h)

openxlr-lv2-host: $(OBJECTS)
	$(CXX) $(OBJECTS) -o $@ $(LDFLAGS) $(LDLIBS)

%.o: %.c $(HEADERS)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

%.o: %.cpp $(HEADERS)
	$(CXX) $(CPPFLAGS) $(CXXFLAGS) -c $< -o $@

tests/editor.o: tests/editor.c host.c $(HEADERS)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

tests/editor: tests/editor.o lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

tests/audio.o: tests/audio.c host.c $(HEADERS)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

tests/audio: tests/audio.o lv2.o clap.o vst3.o
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

test-audio: tests/audio
	./tests/audio

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
	rm -f openxlr-lv2-host $(OBJECTS) tests/editor tests/editor.o tests/audio tests/audio.o tests/clap tests/clap.o tests/lifecycle tests/lifecycle.o tests/lv2 tests/lv2.o tests/latency.lv2/latency.so tests/gain.lv2/gain.so tests/vst3 tests/vst3.o tests/host.o tests/scan-plugin.so

# Sound Check: the loop backend, driven directly with deterministic samples
# and no audio server.
tests/sound-check.o: tests/sound-check.c sound-check.c host.c $(HEADERS)
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

tests/eq: tests/eq.o lv2.o clap.o vst3.o
	$(CXX) $^ -o $@ $(LDFLAGS) $(LDLIBS)

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
