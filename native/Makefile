# The optional plugin host: one process per insert, LV2 and CLAP, with the
# plugin's own editor. The CLAP headers are vendored under clap/ (MIT).
PKGS = libpipewire-0.3 lilv-0 lv2 x11
CPPFLAGS += $(shell pkg-config --cflags $(PKGS)) -Iclap/include
CFLAGS += -std=c11 -O2 -g -Wall -Wextra -Werror -Wno-unused-parameter
LDLIBS += $(shell pkg-config --libs $(PKGS)) -ldl -lm -pthread

SOURCES = host.c lv2.c clap.c
HEADERS = host.h $(wildcard clap/include/clap/*.h clap/include/clap/*/*.h)

openxlr-lv2-host: $(SOURCES) $(HEADERS)
	$(CC) $(CPPFLAGS) $(CFLAGS) $(SOURCES) -o $@ $(LDFLAGS) $(LDLIBS)

.PHONY: clean
clean:
	rm -f openxlr-lv2-host
