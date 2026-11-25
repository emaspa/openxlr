// SPDX-License-Identifier: GPL-3.0-only
// The Windows side of the disk identity (../disk-identity.h): the hook that
// answers a plugin's storage property query on \\.\PhysicalDrive0 with the
// descriptor the Linux side built.
#pragma once
#include "../disk-identity.h"

// Redirect the DeviceIoControl, CreateFileA, CreateFileW and CloseHandle
// imports of every module loaded from outside the Windows directory from now
// on. Once, on the main thread, before any plugin module is loaded. The
// descriptor is copied. One line on standard error when Wine lacks what the
// hook needs, and nothing is changed then.
void disk_hook_install(const unsigned char descriptor[DISK_IDENTITY_SIZE]);
