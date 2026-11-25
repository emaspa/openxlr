// SPDX-License-Identifier: GPL-3.0-only
// The Linux side of the disk identity (disk-identity.h): the descriptor
// Windows' NVMe driver gives for the disk that holds the Wine prefix.
//
// This is build_descriptor() of packaging/yabridge/disk-identity.patch, the
// companion's, in C. A changed machine id counts as a new device against a
// licence, so the rules are the patch's, step for step and byte for byte:
// which disk, which serial, the layout, and every case where it leaves Wine's
// answer alone. Where the patch runs inside the Wine process, this runs in the
// helper before Wine starts, with the same environment, so it finds the same
// prefix. A test checks the two give the same bytes.
#define _GNU_SOURCE  // realpath
#include "disk-identity.h"

#include <dirent.h>
#include <fcntl.h>
#include <limits.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

const char *disk_identity_test_root;

// The layout stornvme returns for StorageDeviceProperty, read from a Windows
// 11 system: no vendor string, then product, revision and serial at fixed
// offsets after the 40-byte STORAGE_DEVICE_DESCRIPTOR.
enum {
  PRODUCT_OFFSET = 57,
  REVISION_OFFSET = 122,
  SERIAL_OFFSET = 139,
  HEADER_SIZE = 40,
  BUS_TYPE_NVME = 17
};

// Bytes with their length: a file's contents may hold anything, and the
// patch's strings carry every byte.
typedef struct {
  char *data;
  size_t size;
  bool failed;  // out of memory: the whole answer is given up
} Text;

static void text_add(Text *t, const char *data, size_t size) {
  if (t->failed || !size)
    return;
  char *grown = realloc(t->data, t->size + size + 1);
  if (!grown) {
    t->failed = true;
    return;
  }
  memcpy(grown + t->size, data, size);
  t->data = grown;
  t->size += size;
  t->data[t->size] = 0;
}

static void text_free(Text *t) {
  free(t->data);
  *t = (Text){0};
}

static bool text_starts(const Text *t, const char *start) {
  size_t length = strlen(start);
  return t->size >= length && !memcmp(t->data, start, length);
}

static const char *root(void) {
  return disk_identity_test_root ? disk_identity_test_root : "";
}

// A path, or "" when it is longer than any path can be, which is where
// opening it would end too.
static void path_of(char out[PATH_MAX], const char *format, ...)
    __attribute__((format(printf, 2, 3)));
static void path_of(char out[PATH_MAX], const char *format, ...) {
  va_list arguments;
  va_start(arguments, format);
  int length = vsnprintf(out, PATH_MAX, format, arguments);
  va_end(arguments);
  if (length < 0 || length >= PATH_MAX)
    out[0] = 0;
}

// The whole file, or nothing when it cannot be read.
static Text read_file(const char *path) {
  Text t = {0};
  int file = path[0] ? open(path, O_RDONLY | O_CLOEXEC) : -1;
  if (file < 0)
    return t;
  char buffer[4096];
  ssize_t count;
  while ((count = read(file, buffer, sizeof(buffer))) > 0)
    text_add(&t, buffer, (size_t)count);
  close(file);
  return t;
}

static bool blank(char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; }

// read_trimmed: the file without the blanks at either end.
static Text read_trimmed(const char *path) {
  Text whole = read_file(path), trimmed = {.failed = whole.failed};
  size_t first = 0, last = whole.size;
  while (first < last && blank(whole.data[first]))
    ++first;
  while (last > first && blank(whole.data[last - 1]))
    --last;
  text_add(&trimmed, whole.data + first, last - first);
  text_free(&whole);
  return trimmed;
}

// What the C library's isxdigit, toupper and isalnum answer in the C locale,
// the one the companion's host runs its checks in.
static bool hex_digit(unsigned char c) {
  return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}

static bool alphanumeric(unsigned char c) {
  return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}

// stornvme reports a namespace identifier as groups of four hex digits joined
// by underscores, with a trailing period: the NGUID when it is set, otherwise
// the EUI-64, otherwise the controller's serial number.
static Text windows_nvme_serial(const char *block) {
  static const struct {
    const char *file;
    size_t length;
  } identifiers[] = {{"nguid", 32}, {"eui", 16}};
  char path[PATH_MAX];
  for (size_t i = 0; i < sizeof(identifiers) / sizeof(identifiers[0]); ++i) {
    path_of(path, "%s/sys/block/%s/%s", root(), block, identifiers[i].file);
    Text value = read_trimmed(path);
    if (value.failed)
      return value;
    char digits[64];
    size_t count = 0;
    bool any = false;
    for (size_t at = 0; at < value.size; ++at) {
      unsigned char c = (unsigned char)value.data[at];
      if (!hex_digit(c))
        continue;
      if (count < sizeof(digits))
        digits[count] = (char)(c >= 'a' && c <= 'f' ? c - 'a' + 'A' : c);
      ++count;
      any |= c != '0';
    }
    text_free(&value);
    if (count != identifiers[i].length || !any)
      continue;
    Text serial = {0};
    for (size_t at = 0; at < count; at += 4) {
      if (at > 0)
        text_add(&serial, "_", 1);
      text_add(&serial, digits + at, 4);
    }
    text_add(&serial, ".", 1);
    return serial;
  }
  path_of(path, "%s/sys/block/%s/device/serial", root(), block);
  return read_trimmed(path);
}

// A mount point as mountinfo writes it, with a space, a tab, a line feed or
// a backslash as a backslash and three octal digits. The patch reads the
// three with std::stoi, which is strtol: false where that finds no number,
// as the patch's exception gives up the whole answer.
static bool unescape_mount_path(const char *path, size_t size, Text *out) {
  for (size_t i = 0; i < size; ++i) {
    if (path[i] == '\\' && i + 3 < size) {
      char digits[4] = {path[i + 1], path[i + 2], path[i + 3], 0};
      char *end;
      long value = strtol(digits, &end, 8);
      if (end == digits)
        return false;
      char c = (char)value;
      text_add(out, &c, 1);
      i += 3;
    } else
      text_add(out, path + i, 1);
  }
  return true;
}

// The next field of a line as `>>` reads one: what stands between blanks.
static size_t next_field(const char **at, const char *end, const char **field) {
  const char *p = *at;
  while (p < end && (*p == ' ' || (*p >= '\t' && *p <= '\r')))
    ++p;
  *field = p;
  while (p < end && !(*p == ' ' || (*p >= '\t' && *p <= '\r')))
    ++p;
  *at = p;
  return (size_t)(p - *field);
}

// The block device behind the mount that holds `path`: of every mount whose
// point is the path or above it, the longest, and of equal ones the last.
// False when the mount table cannot be read the way the patch reads it.
static bool mount_source(const char *path, Text *best_source) {
  char table[PATH_MAX];
  path_of(table, "%s/proc/self/mountinfo", root());
  Text all = read_file(table);
  if (all.failed)
    return false;
  size_t path_length = strlen(path), best_mount = 0;
  bool ok = true;
  for (const char *line = all.data, *stop = all.data + all.size; ok && line && line < stop;) {
    const char *end = memchr(line, '\n', (size_t)(stop - line));
    if (!end)
      end = stop;
    const char *at = line, *field = NULL;
    for (int skip = 0; skip < 4; ++skip)
      next_field(&at, end, &field);
    size_t raw_length = next_field(&at, end, &field);
    Text mount_point = {0};
    ok = unescape_mount_path(field, raw_length, &mount_point) && !mount_point.failed;
    const char *separator = memmem(line, (size_t)(end - line), " - ", 3);
    if (ok && separator) {
      const char *tail = separator + 3, *source;
      next_field(&tail, end, &source);  // the file system's type
      size_t source_length = next_field(&tail, end, &source);
      const char *m = mount_point.data ? mount_point.data : "";
      size_t m_length = mount_point.size;
      bool contains =
          (m_length == 1 && m[0] == '/') ||
          (path_length == m_length && !memcmp(path, m, m_length)) ||
          (path_length > m_length && !memcmp(path, m, m_length) && path[m_length] == '/');
      if (contains && m_length >= best_mount) {
        best_mount = m_length;
        text_free(best_source);
        text_add(best_source, source, source_length);
        ok = !best_source->failed;
      }
    }
    text_free(&mount_point);
    line = end < stop ? end + 1 : NULL;
  }
  text_free(&all);
  return ok;
}

static const char *base_name(const char *path) {
  const char *slash = strrchr(path, '/');
  return slash ? slash + 1 : path;
}

// realpath, or "" for a path that does not resolve.
static void real_path(const char *path, char out[PATH_MAX]) {
  if (!path[0] || !realpath(path, out))
    out[0] = 0;
}

// The whole disk under a partition or a device-mapper device, or "".
static void disk_for(const char *start, char disk[PATH_MAX]) {
  char name[PATH_MAX], path[PATH_MAX], sys[PATH_MAX];
  snprintf(name, sizeof(name), "%s", start);
  disk[0] = 0;
  for (int depth = 0; depth < 4; ++depth) {
    path_of(path, "%s/sys/class/block/%s", root(), name);
    real_path(path, sys);
    if (!sys[0])
      return;
    char partition[PATH_MAX];
    path_of(partition, "%s/partition", sys);
    Text value = read_trimmed(partition);
    bool is_partition = value.size > 0;
    text_free(&value);
    if (is_partition) {
      *strrchr(sys, '/') = 0;
      snprintf(disk, PATH_MAX, "%s", base_name(sys));
      return;
    }
    if (strncmp(name, "dm-", 3) != 0) {
      snprintf(disk, PATH_MAX, "%s", name);
      return;
    }
    // Follow the first device a mapping (LUKS, LVM) sits on
    path_of(path, "%s/slaves", sys);
    DIR *slaves = path[0] ? opendir(path) : NULL;
    if (!slaves)
      return;
    char next[sizeof(name)] = "";
    const struct dirent *entry;
    while ((entry = readdir(slaves)))
      if (entry->d_name[0] != '.') {
        snprintf(next, sizeof(next), "%s", entry->d_name);
        break;
      }
    closedir(slaves);
    if (!next[0])
      return;
    memcpy(name, next, sizeof(name));
  }
}

// One line of the helper's log about the disk, with nothing in it a terminal
// or a log would act on.
static void say(const char *format, ...) __attribute__((format(printf, 1, 2)));
static void say(const char *format, ...) {
  char line[1024];
  va_list arguments;
  va_start(arguments, format);
  vsnprintf(line, sizeof(line), format, arguments);
  va_end(arguments);
  for (char *c = line; *c; ++c)
    if ((unsigned char)*c < 0x20 || *c == 0x7f)
      *c = ' ';
  fprintf(stderr, "trace: disk identity: %s\n", line);
}

static void put32(unsigned char *at, uint32_t value) {
  for (int i = 0; i < 4; ++i)
    at[i] = (unsigned char)(value >> (8 * i));
}

bool disk_identity_build(unsigned char descriptor[DISK_IDENTITY_SIZE]) {
  const char *prefix_env = getenv("WINEPREFIX"), *home = getenv("HOME");
  char drive_c[PATH_MAX], path[PATH_MAX];
  if (prefix_env && prefix_env[0])
    path_of(path, "%s/drive_c", prefix_env);
  else
    path_of(path, "%s/.wine/drive_c", home ? home : "");
  real_path(path, drive_c);
  if (!drive_c[0])
    return false;

  Text source = {0};
  if (!mount_source(drive_c, &source)) {
    text_free(&source);
    say("cannot read the mount table; leaving Wine's answer");
    return false;
  }
  if (!text_starts(&source, "/dev/")) {
    say("%s is on '%.*s', not a block device; leaving Wine's answer", drive_c,
        (int)source.size, source.data ? source.data : "");
    text_free(&source);
    return false;
  }
  char device_path[PATH_MAX], device[PATH_MAX];
  path_of(path, "%s%s", root(), source.data);
  text_free(&source);
  real_path(path, device_path);
  snprintf(device, sizeof(device), "%s", base_name(device_path));
  char disk[PATH_MAX];
  disk_for(device, disk);
  if (strncmp(disk, "nvme", 4) != 0) {
    say("the prefix disk '%s' is not an NVMe drive; leaving Wine's answer", disk);
    return false;
  }

  path_of(path, "%s/sys/block/%s/device/model", root(), disk);
  Text product = read_trimmed(path);
  path_of(path, "%s/sys/block/%s/device/firmware_rev", root(), disk);
  Text revision = read_trimmed(path);
  Text serial = windows_nvme_serial(disk);
  bool readable = !product.failed && !revision.failed && !serial.failed &&
                  product.size && serial.size &&
                  product.size < REVISION_OFFSET - PRODUCT_OFFSET &&
                  revision.size < SERIAL_OFFSET - REVISION_OFFSET &&
                  serial.size < DISK_IDENTITY_SIZE - SERIAL_OFFSET;
  if (readable) {
    memset(descriptor, 0, DISK_IDENTITY_SIZE);
    put32(descriptor + 0, HEADER_SIZE);         // Version
    put32(descriptor + 4, DISK_IDENTITY_SIZE);  // Size
    descriptor[8] = 0;                          // DeviceType
    descriptor[9] = 0;                          // DeviceTypeModifier
    descriptor[10] = 0;                         // RemovableMedia
    descriptor[11] = 1;                         // CommandQueueing
    put32(descriptor + 12, 0);                  // VendorIdOffset
    put32(descriptor + 16, PRODUCT_OFFSET);
    put32(descriptor + 20, revision.size ? REVISION_OFFSET : 0);
    put32(descriptor + 24, SERIAL_OFFSET);
    put32(descriptor + 28, BUS_TYPE_NVME);  // BusType
    put32(descriptor + 32, 0);              // RawPropertiesLength
    memcpy(descriptor + PRODUCT_OFFSET, product.data, product.size);
    if (revision.size)
      memcpy(descriptor + REVISION_OFFSET, revision.data, revision.size);
    memcpy(descriptor + SERIAL_OFFSET, serial.data, serial.size);
    // The serial's shape only, in the log
    char shape[DISK_IDENTITY_SIZE];
    for (size_t i = 0; i < serial.size; ++i)
      shape[i] = alphanumeric((unsigned char)serial.data[i]) ? 'x' : serial.data[i];
    shape[serial.size] = 0;
    say("PhysicalDrive0 reports %s: '%.*s', revision '%.*s', serial %s", disk,
        (int)product.size, product.data, (int)revision.size,
        revision.size ? revision.data : "", shape);
  } else
    say("could not read the identity of '%s'; leaving Wine's answer", disk);
  text_free(&product);
  text_free(&revision);
  text_free(&serial);
  return readable;
}
