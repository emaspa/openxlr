// SPDX-License-Identifier: GPL-3.0-only
// Text in a scanner's JSON. The LV2, CLAP and VST3 scanners write every
// string through json_text, and so does the Windows plugin host, which
// builds the CLAP and VST3 scanners for Windows (winhost/). Nothing here
// needs more than the C library.
#include "host.h"

#include <string.h>

// Bytes of one string. The daemon clips names to far less; this only bounds
// what a plugin can make a scanner print.
enum { SCAN_MAX_TEXT = 4096 };

// The length of the valid UTF-8 sequence at s (at most n bytes), or 0 when
// none starts there. Overlong forms, surrogates and values past U+10FFFF
// are invalid.
static size_t utf8_sequence(const unsigned char *s, size_t n) {
  if (s[0] < 0x80)
    return 1;
  size_t length;
  unsigned char low = 0x80, high = 0xbf;
  if (s[0] >= 0xc2 && s[0] <= 0xdf)
    length = 2;
  else if (s[0] >= 0xe0 && s[0] <= 0xef) {
    length = 3;
    if (s[0] == 0xe0)
      low = 0xa0;
    else if (s[0] == 0xed)
      high = 0x9f;
  } else if (s[0] >= 0xf0 && s[0] <= 0xf4) {
    length = 4;
    if (s[0] == 0xf0)
      low = 0x90;
    else if (s[0] == 0xf4)
      high = 0x8f;
  } else
    return 0;
  if (n < length)
    return 0;
  if (s[1] < low || s[1] > high)
    return 0;
  for (size_t i = 2; i < length; ++i)
    if (s[i] < 0x80 || s[i] > 0xbf)
      return 0;
  return length;
}

// The bytes of an ill-formed sequence that one replacement character
// stands for: the lead byte and the continuation bytes that could still
// have completed it, the way .NET decodes the same bytes.
static size_t utf8_invalid(const unsigned char *s, size_t n) {
  size_t length;
  unsigned char low = 0x80, high = 0xbf;
  if (s[0] >= 0xc2 && s[0] <= 0xdf)
    length = 2;
  else if (s[0] >= 0xe0 && s[0] <= 0xef) {
    length = 3;
    if (s[0] == 0xe0)
      low = 0xa0;
    else if (s[0] == 0xed)
      high = 0x9f;
  } else if (s[0] >= 0xf0 && s[0] <= 0xf4) {
    length = 4;
    if (s[0] == 0xf0)
      low = 0x90;
    else if (s[0] == 0xf4)
      high = 0x8f;
  } else
    return 1;
  size_t used = 1;
  if (used < n && used < length && s[1] >= low && s[1] <= high) {
    ++used;
    while (used < n && used < length && s[used] >= 0x80 && s[used] <= 0xbf)
      ++used;
  }
  return used;
}

// A JSON string, or null. Bytes that are not UTF-8 become U+FFFD, so the
// output always parses; the text stops at SCAN_MAX_TEXT bytes, between
// characters.
void json_text(FILE *out, const char *text) {
  if (!text) {
    fputs("null", out);
    return;
  }
  const unsigned char *s = (const unsigned char *)text;
  size_t n = strlen(text);
  fputc('"', out);
  for (size_t i = 0; i < n;) {
    size_t length = utf8_sequence(s + i, n - i);
    size_t step = length ? length : utf8_invalid(s + i, n - i);
    if (i + step > SCAN_MAX_TEXT)
      break;
    if (!length)
      fputs("\\ufffd", out);
    else if (s[i] == '"' || s[i] == '\\')
      fprintf(out, "\\%c", s[i]);
    else if (s[i] < 0x20)
      fprintf(out, "\\u%04x", s[i]);
    else
      fwrite(s + i, 1, length, out);
    i += step;
  }
  fputc('"', out);
}
