// SPDX-License-Identifier: GPL-3.0-only
// The system disk's identity for Windows plugins that read
// \\.\PhysicalDrive0, as the yabridge companion reports it
// (packaging/yabridge/disk-identity.patch).
//
// Wine answers the storage property query on its placeholder PhysicalDrive0
// with a descriptor that has no product, revision or serial, and a plugin that
// builds a machine id from that serial then sends an empty one. The Linux
// side (disk-identity.c) builds the descriptor Windows' NVMe driver gives for
// the NVMe disk that holds the Wine prefix, with the companion's rules, and
// leaves it in a file of the instance's private directory before the Windows
// program starts. The Windows program (winhost/disk-hook.c) reads the file
// and answers that one query with it, in place of Wine's empty answer only.
// Both sides include this file; nothing in it is either system's own.
#pragma once
#include <stdbool.h>

// The descriptor's size, the size of what stornvme returns on Windows 11.
#define DISK_IDENTITY_SIZE 396

// The file in the private directory that holds the descriptor. It is there
// only when the identity could be worked out.
#define DISK_IDENTITY_FILE "disk-identity"

#ifndef _WIN32
// The descriptor for the disk that holds the prefix the Windows program will
// run in: WINEPREFIX, else ~/.wine, as Wine and the companion take it. False,
// with one line on standard error, when there is none to give: the prefix is
// not on a block device, or the disk is not an NVMe drive, or its identity
// cannot be read. False with no line when the prefix has no drive_c, which
// is where the companion's host says nothing either.
bool disk_identity_build(unsigned char descriptor[DISK_IDENTITY_SIZE]);

// For the tests only: a directory put in front of /proc/self/mountinfo,
// /sys and /dev, so the rules can be run against a disk that is not this
// machine's. NULL, the product's value, reads the machine's own.
extern const char *disk_identity_test_root;
#endif
