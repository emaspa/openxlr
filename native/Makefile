PKGS = libpipewire-0.3 lilv-0 x11
CPPFLAGS += $(shell pkg-config --cflags $(PKGS))
CFLAGS += -std=c11 -O2 -g -Wall -Wextra -Werror -Wno-unused-parameter
LDLIBS += $(shell pkg-config --libs $(PKGS)) -ldl -lm -pthread

openxlr-lv2-host: lv2-host.c
	$(CC) $(CPPFLAGS) $(CFLAGS) $< -o $@ $(LDFLAGS) $(LDLIBS)

.PHONY: clean
clean:
	rm -f openxlr-lv2-host
