// SPDX-License-Identifier: GPL-3.0-only
// What the Windows test plugin's `disk` plugin (tests/winhost-plugin.c) asks
// about \\.\PhysicalDrive0 from its own module, the way a plugin that ties
// its licence to the disk does, and writes down for tests/winhost.c: one
// record per question, in this order, to the file OPENXLR_TEST_WINHOST_DISK
// names. Shared by the plugin, built with mingw, and the test.
#pragma once
#include <stdint.h>

enum {
  PROBE_FIRST,     // StorageDeviceProperty on a handle from CreateFileA
  PROBE_REPEAT,    // the same again, on the same handle
  PROBE_HEADER,    // the same into a buffer only a header long
  PROBE_ADAPTER,   // StorageAdapterProperty on that handle
  PROBE_EXISTS,    // StorageDeviceProperty, but asking whether it exists
  PROBE_VOLUME,    // StorageDeviceProperty on \\.\C:, another device
  PROBE_CLOSED,    // StorageDeviceProperty on the first handle once closed
  PROBE_REOPENED,  // on \\?\PHYSICALDRIVE0 from CreateFileW, after that
  PROBE_REUSED,    // on a plain file opened once that one is closed
  PROBE_RECORDS
};

enum { PROBE_BUFFER = 1024 };

typedef struct {
  uint32_t ok;        // what DeviceIoControl returned
  uint32_t returned;  // the bytes it said it wrote
  uint32_t error;     // GetLastError when it failed, else 0
  uint32_t same;      // PROBE_REUSED: the plain file has the closed handle's value
  unsigned char bytes[PROBE_BUFFER];
} ProbeRecord;
