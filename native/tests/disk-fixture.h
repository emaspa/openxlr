// SPDX-License-Identifier: GPL-3.0-only
// Made-up disks for the disk identity's tests (tests/disk-identity.c,
// tests/winhost.c): a small tree that stands in for /proc/self/mountinfo,
// /sys and /dev, for disk_identity_test_root to point at. Nothing here reads
// or names a real disk.
#pragma once
#include "../disk-identity.h"

#include <assert.h>
#include <errno.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

// The paths are a temporary directory and a few names under it.
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wformat-truncation"

static inline void fixture_directories(const char *path) {
  char partial[PATH_MAX];
  snprintf(partial, sizeof(partial), "%s", path);
  for (char *slash = partial + 1; (slash = strchr(slash, '/')); ++slash) {
    *slash = 0;
    if (mkdir(partial, 0700) != 0) assert(errno == EEXIST);
    *slash = '/';
  }
  if (mkdir(partial, 0700) != 0) assert(errno == EEXIST);
}

static inline void fixture_text(const char *path, const char *text) {
  char directory[PATH_MAX];
  snprintf(directory, sizeof(directory), "%s", path);
  *strrchr(directory, '/') = 0;
  fixture_directories(directory);
  FILE *file = fopen(path, "w");
  assert(file);
  fputs(text, file);
  assert(fclose(file) == 0);
}

static inline void fixture_link(const char *target, const char *path) {
  char directory[PATH_MAX];
  snprintf(directory, sizeof(directory), "%s", path);
  *strrchr(directory, '/') = 0;
  fixture_directories(directory);
  unlink(path);
  assert(symlink(target, path) == 0);
}

// One NVMe controller under `tree` with its namespace and a partition on it,
// nvmeNn1p2, laid out as sysfs has them: the namespace's `device` is its
// controller, which holds the model, the firmware revision and the serial;
// the namespace holds the NGUID and the EUI-64, each left out when NULL.
static inline void fixture_nvme(const char *tree, int number, const char *nguid, const char *eui,
                         const char *model, const char *firmware, const char *serial) {
  char controller[PATH_MAX], space[PATH_MAX], part[PATH_MAX], path[PATH_MAX + 64],
      name[32];
  snprintf(controller, sizeof(controller), "%s/sys/devices/pci0000:00/0000:00:0%d.1/nvme/nvme%d",
           tree, number, number);
  snprintf(name, sizeof(name), "nvme%dn1", number);
  snprintf(space, sizeof(space), "%s/%s", controller, name);
  snprintf(part, sizeof(part), "%s/%sp2", space, name);
  fixture_directories(part);
  snprintf(path, sizeof(path), "%s/model", controller);
  fixture_text(path, model);
  snprintf(path, sizeof(path), "%s/firmware_rev", controller);
  fixture_text(path, firmware);
  snprintf(path, sizeof(path), "%s/serial", controller);
  fixture_text(path, serial);
  if (nguid) {
    snprintf(path, sizeof(path), "%s/nguid", space);
    fixture_text(path, nguid);
  }
  if (eui) {
    snprintf(path, sizeof(path), "%s/eui", space);
    fixture_text(path, eui);
  }
  snprintf(path, sizeof(path), "%s/partition", part);
  fixture_text(path, "2\n");
  snprintf(path, sizeof(path), "%s/device", space);
  char relative[32];  // relative, as sysfs has it
  snprintf(relative, sizeof(relative), "../../nvme%d", number);
  fixture_link(relative, path);
  snprintf(path, sizeof(path), "%s/sys/block/%s", tree, name);
  fixture_link(space, path);
  snprintf(path, sizeof(path), "%s/sys/class/block/%s", tree, name);
  fixture_link(space, path);
  snprintf(path, sizeof(path), "%s/sys/class/block/%sp2", tree, name);
  fixture_link(part, path);
  snprintf(path, sizeof(path), "%s/dev/%sp2", tree, name);
  fixture_text(path, "");
}

// A SATA disk, sda with sda1 on it.
static inline void fixture_sata(const char *tree) {
  char disk[PATH_MAX], part[PATH_MAX], path[PATH_MAX + 64];
  snprintf(disk, sizeof(disk), "%s/sys/devices/pci0000:00/ata1/host0/block/sda", tree);
  snprintf(part, sizeof(part), "%s/sda1", disk);
  snprintf(path, sizeof(path), "%s/partition", part);
  fixture_text(path, "1\n");
  snprintf(path, sizeof(path), "%s/device/model", disk);
  fixture_text(path, "Samsung SSD 870\n");
  snprintf(path, sizeof(path), "%s/sys/block/sda", tree);
  fixture_link(disk, path);
  snprintf(path, sizeof(path), "%s/sys/class/block/sda", tree);
  fixture_link(disk, path);
  snprintf(path, sizeof(path), "%s/sys/class/block/sda1", tree);
  fixture_link(part, path);
  snprintf(path, sizeof(path), "%s/dev/sda1", tree);
  fixture_text(path, "");
}

// A LUKS mapping, dm-0, on nvme1n1p2, by the name /dev/mapper gives it.
static inline void fixture_luks(const char *tree) {
  char mapping[PATH_MAX], path[PATH_MAX + 64], partition[PATH_MAX];
  snprintf(mapping, sizeof(mapping), "%s/sys/devices/virtual/block/dm-0", tree);
  snprintf(path, sizeof(path), "%s/dm/name", mapping);
  fixture_text(path, "luks-root\n");
  snprintf(partition, sizeof(partition), "%s/sys/class/block/nvme1n1p2", tree);
  snprintf(path, sizeof(path), "%s/slaves/nvme1n1p2", mapping);
  fixture_link(partition, path);
  snprintf(path, sizeof(path), "%s/sys/class/block/dm-0", tree);
  fixture_link(mapping, path);
  snprintf(path, sizeof(path), "%s/dev/dm-0", tree);
  fixture_text(path, "");
  snprintf(path, sizeof(path), "%s/dev/mapper/luks-root", tree);
  fixture_link("../dm-0", path);
}

// The mount table, as mountinfo writes it: the root on the SATA disk, and
// `holder`, a real path, on `source`. A space in the holder's name is written
// as \040, as mountinfo does, and two mounts beside it must not be taken for
// it: one whose name only starts the same, and one that is longer. `later`,
// when set, mounts something of `type` on the holder afterwards, which is the
// one that counts.
static inline void fixture_mounts(const char *tree, const char *holder, const char *source,
                           const char *type, const char *later) {
  char escaped[PATH_MAX * 4], path[PATH_MAX + 64], *out = escaped;
  for (const char *c = holder; *c; ++c)
    out += *c == ' ' ? sprintf(out, "\\040") : sprintf(out, "%c", *c);
  *out = 0;
  char *table = NULL;
  size_t size = 0;
  FILE *f = open_memstream(&table, &size);
  assert(f);
  fprintf(f, "21 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw\n");
  fprintf(f, "22 21 0:21 / /proc rw,nosuid shared:5 - proc proc rw\n");
  fprintf(f, "30 21 259:2 /@home %s rw,relatime shared:3 - btrfs %s rw,ssd\n", escaped, source);
  fprintf(f, "31 21 259:3 / %sx rw shared:4 - ext4 /dev/sda1 rw\n", escaped);
  fprintf(f, "32 30 0:40 / %s/elsewhere rw shared:6 - tmpfs tmpfs rw\n", escaped);
  if (later)
    fprintf(f, "33 30 0:41 / %s rw shared:7 master:2 - %s %s rw\n", escaped, type, later);
  fclose(f);
  snprintf(path, sizeof(path), "%s/proc/self/mountinfo", tree);
  fixture_text(path, table);
  free(table);
}

// What stornvme gives for a disk with this model, firmware revision and
// serial, written out from the layout itself: a 40-byte
// STORAGE_DEVICE_DESCRIPTOR of 396 bytes in all, no vendor, the product at
// 57, the revision at 122 when there is one, the serial at 139, bus type 17.
static inline void fixture_expected(unsigned char out[DISK_IDENTITY_SIZE], const char *model,
                             const char *revision, const char *serial) {
  static const unsigned char header[40] = {
      40, 0, 0, 0,  0x8c, 1, 0, 0,  0, 0, 0, 1,  0, 0, 0, 0,  57, 0, 0, 0,
      122, 0, 0, 0,  139, 0, 0, 0,  17, 0, 0, 0,  0, 0, 0, 0,  0, 0, 0, 0};
  memset(out, 0, DISK_IDENTITY_SIZE);
  memcpy(out, header, sizeof(header));
  if (!revision[0])
    out[20] = 0;
  memcpy(out + 57, model, strlen(model));
  memcpy(out + 122, revision, strlen(revision));
  memcpy(out + 139, serial, strlen(serial));
}

#pragma GCC diagnostic pop
