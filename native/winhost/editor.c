// SPDX-License-Identifier: GPL-3.0-only
// The editor on the Windows side: the window a plugin's editor is put in,
// and what a backend asks of the frame around it (the editor part of
// host.h). The frame the user sees is the Linux side's. This window has no
// border and no title, and under Wine it is an X11 window, which the Linux
// side takes into that frame.
//
// It is made hidden and stays hidden until the Linux side has taken it and
// says so (wire.h). A window shown first is one a window manager has already
// placed, and Wine then keeps it where the manager put it.
//
// Everything here runs on the main thread, whose message loop the window's
// messages and the plugin's own come through (winhost.c).
#include "winhost.h"
#include "wire.h"

#include <stdio.h>

#include <windows.h>

static const WCHAR window_class[] = L"OpenXLREditor";

static HWND window;      // the editor's window, or NULL while none is open
static bool resizable;   // the plugin lets the frame be resized by hand
static bool opening;     // the backend is opening the editor
static bool sizing;      // the Linux side is setting the size
static bool closed_noted;

static LRESULT CALLBACK editor_proc(HWND hwnd, UINT message, WPARAM w,
                                    LPARAM l) {
  // Only the Linux side closes this window, when its frame closes.
  if (message == WM_CLOSE)
    return 0;
  return DefWindowProcW(hwnd, message, w, l);
}

static bool register_class(void) {
  static bool registered;
  if (registered)
    return true;
  WNDCLASSEXW description = {
      .cbSize = sizeof(description),
      .lpfnWndProc = editor_proc,
      .hInstance = GetModuleHandleW(NULL),
      .hCursor = LoadCursorW(NULL, (LPCWSTR)IDC_ARROW),
      // The frame's own colour, for what an editor leaves bare.
      .hbrBackground = CreateSolidBrush(RGB(0x16, 0x18, 0x1d)),
      .lpszClassName = window_class,
  };
  registered = RegisterClassExW(&description) != 0;
  return registered;
}

static bool size_ok(unsigned width, unsigned height) {
  return width >= 1 && height >= 1 && width <= 16384 && height <= 16384;
}

static void window_size(uint32_t *width, uint32_t *height) {
  RECT inside = {0};
  if (window)
    GetClientRect(window, &inside);
  *width = (uint32_t)inside.right;
  *height = (uint32_t)inside.bottom;
}

static void set_window_size(unsigned width, unsigned height) {
  SetWindowPos(window, NULL, 0, 0, (int)width, (int)height,
               SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
}

// --- what the backend asks of the frame ---------------------------------------

// A size from the plugin. The window takes it here. While the editor is
// being opened, or the Linux side is the one setting a size, the answer to
// that request carries it. At any other time it is the plugin's own wish and
// goes to the Linux side as a note, for the frame to follow.
void host_resize_editor(Host *h, unsigned width, unsigned height) {
  if (!window || !size_ok(width, height) || !host_on_main_thread(h))
    return;
  set_window_size(width, height);
  if (!opening && !sizing)
    winhost_note(WIRE_NOTE_EDITOR_SIZE, width, height);
}

void host_set_editor_resizable(Host *h, bool allowed) { resizable = allowed; }

double host_editor_scale(const Host *h) {
  return h->editor_scale > 0 ? h->editor_scale : 1.0;
}

// The frame is shown and hidden by the Linux side alone, so a plugin that
// asks for either is not followed.
void host_show_editor(Host *h, bool show) {}

// There is no display to lose on this side.
void host_editor_lost(Host *h) {}

// --- what the Linux side asks ---------------------------------------------------

void winhost_editor_close(Host *h) {
  if (!window)
    return;
  if (h->editor_open)
    h->backend->editor_close(h);
  h->editor_open = false;
  DestroyWindow(window);
  window = NULL;
  h->window = 0;
}

bool winhost_editor_open(Host *h, const WireRequest *request, WireReply *reply) {
  char *text = reply->text;
  const size_t text_size = sizeof(reply->text);
  if (window) {
    reply->flags |= WIRE_HAS_EDITOR;
    snprintf(text, text_size, "the editor is already open");
    return false;
  }
  if (!h->has_editor || !h->backend->editor_open) {
    snprintf(text, text_size, "the plugin has no editor this host can show");
    return false;
  }
  if (register_class())
    window = CreateWindowExW(0, window_class, L"OpenXLR",
                             WS_POPUP | WS_CLIPCHILDREN, 0, 0, 640, 480, NULL,
                             NULL, GetModuleHandleW(NULL), NULL);
  if (!window) {
    reply->flags |= WIRE_HAS_EDITOR;
    snprintf(text, text_size, "cannot make the editor's window: Windows error %lu",
             (unsigned long)GetLastError());
    return false;
  }
  h->window = (Window)(uintptr_t)window;
  // A scale that is no number fails both comparisons.
  h->editor_scale = request->scale >= 0.25f && request->scale <= 8.0f
                        ? request->scale
                        : 1.0;
  resizable = true;  // as the Linux frame starts out, until the backend says
  closed_noted = false;
  opening = true;
  h->editor_open = h->backend->editor_open(h);
  opening = false;
  if (!h->editor_open) {
    winhost_editor_close(h);
    snprintf(text, text_size, "the plugin did not open its editor");
    return false;
  }
  // Wine's X11 driver keeps the X11 window of a top-level window in a
  // property of that window. Without that driver, or without a display,
  // there is none and nothing to show the editor in.
  reply->window = (uint32_t)(uintptr_t)GetPropA(window, "__wine_x11_whole_window");
  if (!reply->window) {
    winhost_editor_close(h);
    reply->flags |= WIRE_HAS_EDITOR;
    snprintf(text, text_size,
             "Wine gave the editor no X11 window; it needs its X11 driver and "
             "a display");
    return false;
  }
  window_size(&reply->width, &reply->height);
  reply->flags |= WIRE_HAS_EDITOR | (resizable ? WIRE_EDITOR_RESIZABLE : 0);
  return true;
}

bool winhost_editor_show(void) {
  if (!window)
    return false;
  ShowWindow(window, SW_SHOWNORMAL);
  return true;
}

// The size nearest to the one asked that the editor would take, which for
// an editor of one size is the size it has.
void winhost_editor_fit(Host *h, uint32_t *width, uint32_t *height) {
  unsigned wanted_width = *width, wanted_height = *height;
  window_size(width, height);
  if (!window || !resizable || !size_ok(wanted_width, wanted_height))
    return;
  if (h->backend->editor_constrain)
    h->backend->editor_constrain(h, &wanted_width, &wanted_height);
  if (size_ok(wanted_width, wanted_height)) {
    *width = wanted_width;
    *height = wanted_height;
  }
}

// The frame has a new size. The window takes it and the plugin is told. A
// plugin that answers with another size gets that one, and the reply says
// what the window ended at.
void winhost_editor_resize(Host *h, uint32_t *width, uint32_t *height) {
  if (window && h->editor_open && size_ok(*width, *height)) {
    sizing = true;
    set_window_size(*width, *height);
    if (h->backend->editor_resized)
      h->backend->editor_resized(h, *width, *height);
    sizing = false;
  }
  window_size(width, height);
}

// Each tick: an editor the plugin closed by itself is told to the Linux
// side once, which then closes its frame and this window with it.
void winhost_editor_tick(Host *h) {
  if (!window || !h->editor_open || closed_noted || !h->backend->editor_idle ||
      !h->backend->editor_idle(h))
    return;
  closed_noted = true;
  winhost_note(WIRE_NOTE_EDITOR_CLOSED, 0, 0);
}
