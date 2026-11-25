// SPDX-License-Identifier: GPL-3.0-only
// The editor of the two Windows test plugins (winhost-plugin.c and
// winhost-plugin-vst3.cpp): a real window with nothing a person would want
// in it, and everything tests/winhost-editor.c needs to tell that it is
// where it should be and hears what it should. The numbers come first, for
// that test, which is a Linux program. The window itself is below them, for
// the plugins.
//
// The editor is one flat colour with two squares in it. All of it scales
// with the scale the host gives: the sizes here are those at scale 1.
//
// - A left click in the first square moves the plugin's gain, to
//   TEST_EDITOR_GAIN the first time and TEST_EDITOR_OTHER_GAIN the next, and
//   so on in turn.
// - A left click in the second square asks the host for the editor's other
//   size, the grown one first.
// - Any left click takes the keyboard, as a plugin's window does. The key K
//   then moves the gain to TEST_EDITOR_KEY_GAIN.
// - A right click puts up a small window of another colour with its corner
//   at the click, as a menu would be, and the next one takes it down.
// - A middle click tells the host the editor has closed, where the plugin's
//   format has a call for that.
// - The key M puts up a real menu at the pointer and does not return from
//   it until the menu is gone, as Windows has it. The editor then takes the
//   colour TEST_EDITOR_AFTER_MENU_COLOUR.
//
// OPENXLR_TEST_WINHOST_EDITOR=fixed in the environment makes the editor one
// that the host may not resize. Resized by the host otherwise, it takes
// sizes that are multiples of TEST_EDITOR_STEP.
#pragma once

enum {
  TEST_EDITOR_WIDTH = 320,
  TEST_EDITOR_HEIGHT = 200,
  TEST_EDITOR_GROWN_WIDTH = 400,
  TEST_EDITOR_GROWN_HEIGHT = 260,
  TEST_EDITOR_STEP = 20,
  // The squares, each TEST_EDITOR_SQUARE wide and high.
  TEST_EDITOR_SQUARE = 40,
  TEST_EDITOR_GAIN_X = 40,
  TEST_EDITOR_SIZE_X = 120,
  TEST_EDITOR_SQUARES_Y = 40,
  TEST_EDITOR_POPUP_WIDTH = 60,
  TEST_EDITOR_POPUP_HEIGHT = 40,
  // Colours as an X11 pixel of a 24-bit display has them: red, green, blue.
  TEST_EDITOR_COLOUR = 0x2060a0,
  TEST_EDITOR_GAIN_COLOUR = 0xe04020,
  TEST_EDITOR_SIZE_COLOUR = 0x20c040,
  TEST_EDITOR_POPUP_COLOUR = 0xf0d010,
  TEST_EDITOR_AFTER_MENU_COLOUR = 0x603090,
};

#define TEST_EDITOR_GAIN 0.25f
#define TEST_EDITOR_OTHER_GAIN 0.75f
#define TEST_EDITOR_KEY_GAIN 0.125f

#ifdef _WIN32
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>

#include <windows.h>

// What the editor asks of the plugin it belongs to.
typedef struct {
  void (*set_gain)(void *plugin, float gain);
  // Ask the host for this size. True when the host agreed, and the editor
  // then takes the size itself unless the host has already given it.
  bool (*ask_size)(void *plugin, int width, int height);
  void (*closed)(void *plugin);  // NULL where the format has no call for it
} TestEditorCalls;

typedef struct {
  HWND window, popup;
  double scale;
  bool grown, other_gain, menu_over;
  const TestEditorCalls *calls;
  void *plugin;
} TestEditor;

static COLORREF test_editor_rgb(unsigned colour) {
  return RGB(colour >> 16, (colour >> 8) & 0xff, colour & 0xff);
}

static int test_editor_scaled(const TestEditor *e, int length) {
  return (int)(length * (e->scale > 0 ? e->scale : 1.0) + 0.5);
}

static bool test_editor_fixed(void) {
  const char *kind = getenv("OPENXLR_TEST_WINHOST_EDITOR");
  return kind && !strcmp(kind, "fixed");
}

// The size the editor wants now.
static void test_editor_wanted(const TestEditor *e, int *width, int *height) {
  *width = test_editor_scaled(e, e->grown ? TEST_EDITOR_GROWN_WIDTH : TEST_EDITOR_WIDTH);
  *height = test_editor_scaled(e, e->grown ? TEST_EDITOR_GROWN_HEIGHT : TEST_EDITOR_HEIGHT);
}

// The size nearest to one the host asks about that the editor takes.
static void test_editor_fit(int *width, int *height) {
  *width -= *width % TEST_EDITOR_STEP;
  *height -= *height % TEST_EDITOR_STEP;
  if (*width < TEST_EDITOR_STEP)
    *width = TEST_EDITOR_STEP;
  if (*height < TEST_EDITOR_STEP)
    *height = TEST_EDITOR_STEP;
}

static void test_editor_set_size(TestEditor *e, int width, int height) {
  if (e->window)
    SetWindowPos(e->window, NULL, 0, 0, width, height,
                 SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
}

static bool test_editor_in_square(const TestEditor *e, int x, int y, int left) {
  return x >= test_editor_scaled(e, left) &&
         x < test_editor_scaled(e, left + TEST_EDITOR_SQUARE) &&
         y >= test_editor_scaled(e, TEST_EDITOR_SQUARES_Y) &&
         y < test_editor_scaled(e, TEST_EDITOR_SQUARES_Y + TEST_EDITOR_SQUARE);
}

static void test_editor_fill(HDC dc, const TestEditor *e, int left, unsigned colour) {
  RECT square = {test_editor_scaled(e, left),
                 test_editor_scaled(e, TEST_EDITOR_SQUARES_Y),
                 test_editor_scaled(e, left + TEST_EDITOR_SQUARE),
                 test_editor_scaled(e, TEST_EDITOR_SQUARES_Y + TEST_EDITOR_SQUARE)};
  HBRUSH brush = CreateSolidBrush(test_editor_rgb(colour));
  FillRect(dc, &square, brush);
  DeleteObject(brush);
}

static HINSTANCE test_editor_module(void);

static void test_editor_popup(TestEditor *e, int x, int y) {
  if (e->popup) {
    DestroyWindow(e->popup);
    e->popup = NULL;
    return;
  }
  POINT corner = {x, y};
  ClientToScreen(e->window, &corner);
  e->popup = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST, L"OpenXLRTestPopup",
                             L"", WS_POPUP, corner.x, corner.y,
                             test_editor_scaled(e, TEST_EDITOR_POPUP_WIDTH),
                             test_editor_scaled(e, TEST_EDITOR_POPUP_HEIGHT),
                             GetAncestor(e->window, GA_ROOT), NULL,
                             test_editor_module(), NULL);
  if (e->popup)
    ShowWindow(e->popup, SW_SHOWNOACTIVATE);
}

static LRESULT CALLBACK test_editor_proc(HWND window, UINT message, WPARAM w,
                                         LPARAM l) {
  TestEditor *e = (TestEditor *)GetWindowLongPtrW(window, GWLP_USERDATA);
  if (!e)
    return DefWindowProcW(window, message, w, l);
  int x = (short)LOWORD(l), y = (short)HIWORD(l);
  switch (message) {
    case WM_ERASEBKGND:
      return 1;
    case WM_PAINT: {
      PAINTSTRUCT paint;
      HDC dc = BeginPaint(window, &paint);
      RECT all;
      GetClientRect(window, &all);
      HBRUSH brush = CreateSolidBrush(test_editor_rgb(
          e->menu_over ? TEST_EDITOR_AFTER_MENU_COLOUR : TEST_EDITOR_COLOUR));
      FillRect(dc, &all, brush);
      DeleteObject(brush);
      test_editor_fill(dc, e, TEST_EDITOR_GAIN_X, TEST_EDITOR_GAIN_COLOUR);
      test_editor_fill(dc, e, TEST_EDITOR_SIZE_X, TEST_EDITOR_SIZE_COLOUR);
      EndPaint(window, &paint);
      return 0;
    }
    case WM_SIZE:
      InvalidateRect(window, NULL, FALSE);
      return 0;
    case WM_LBUTTONDOWN:
      SetFocus(window);
      if (test_editor_in_square(e, x, y, TEST_EDITOR_GAIN_X)) {
        e->calls->set_gain(e->plugin,
                           e->other_gain ? TEST_EDITOR_OTHER_GAIN : TEST_EDITOR_GAIN);
        e->other_gain = !e->other_gain;
      } else if (test_editor_in_square(e, x, y, TEST_EDITOR_SIZE_X)) {
        int width, height;
        e->grown = !e->grown;
        test_editor_wanted(e, &width, &height);
        if (e->calls->ask_size(e->plugin, width, height))
          test_editor_set_size(e, width, height);
        else
          e->grown = !e->grown;
      }
      return 0;
    case WM_RBUTTONDOWN:
      test_editor_popup(e, x, y);
      return 0;
    case WM_MBUTTONDOWN:
      if (e->calls->closed)
        e->calls->closed(e->plugin);
      return 0;
    case WM_KEYDOWN:
      if (w == 'K')
        e->calls->set_gain(e->plugin, TEST_EDITOR_KEY_GAIN);
      if (w == 'M') {
        POINT at;
        HMENU menu = CreatePopupMenu();
        GetCursorPos(&at);
        AppendMenuW(menu, MF_STRING, 1, L"One");
        AppendMenuW(menu, MF_STRING, 2, L"Two");
        TrackPopupMenu(menu, TPM_RETURNCMD | TPM_NONOTIFY, at.x, at.y, 0, window, NULL);
        DestroyMenu(menu);
        e->menu_over = true;
        InvalidateRect(window, NULL, FALSE);
      }
      return 0;
  }
  return DefWindowProcW(window, message, w, l);
}

// This module, which the window classes belong to.
static HINSTANCE test_editor_module(void) {
  HMODULE module = NULL;
  GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                     (LPCWSTR)(void *)&test_editor_proc, &module);
  return module;
}

// Make the editor's window inside `parent`, at the size it wants.
static bool test_editor_create(TestEditor *e, HWND parent) {
  static bool registered;
  if (!registered) {
    // Filled in by name: this header is built as C and as C++.
    WNDCLASSEXW editor, popup;
    memset(&editor, 0, sizeof(editor));
    editor.cbSize = sizeof(editor);
    editor.lpfnWndProc = test_editor_proc;
    editor.hInstance = test_editor_module();
    editor.hCursor = LoadCursorW(NULL, (LPCWSTR)IDC_ARROW);
    editor.lpszClassName = L"OpenXLRTestEditor";
    popup = editor;
    popup.lpfnWndProc = DefWindowProcW;
    popup.hbrBackground = CreateSolidBrush(test_editor_rgb(TEST_EDITOR_POPUP_COLOUR));
    popup.lpszClassName = L"OpenXLRTestPopup";
    registered = RegisterClassExW(&editor) && RegisterClassExW(&popup);
  }
  int width, height;
  test_editor_wanted(e, &width, &height);
  e->window = registered ? CreateWindowExW(0, L"OpenXLRTestEditor", L"",
                                           WS_CHILD | WS_VISIBLE, 0, 0, width,
                                           height, parent, NULL,
                                           test_editor_module(), NULL)
                         : NULL;
  if (!e->window)
    return false;
  SetWindowLongPtrW(e->window, GWLP_USERDATA, (LONG_PTR)e);
  InvalidateRect(e->window, NULL, FALSE);
  return true;
}

static void test_editor_destroy(TestEditor *e) {
  if (e->popup)
    DestroyWindow(e->popup);
  if (e->window)
    DestroyWindow(e->window);
  e->window = e->popup = NULL;
  e->grown = e->other_gain = e->menu_over = false;
}
#endif
