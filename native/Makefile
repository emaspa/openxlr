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

OBJECTS = host.o lv2.o clap.o vst3.o
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

.PHONY: test test-editor test-audio test-clap clean
# The editor tests need a display; the others need nothing.
test: test-audio test-clap test-editor

test-editor: tests/editor
	./tests/editor

test-audio: tests/audio
	./tests/audio

test-clap: tests/clap
	./tests/clap

clean:
	rm -f openxlr-lv2-host $(OBJECTS) tests/editor tests/editor.o tests/audio tests/audio.o tests/clap tests/clap.o
