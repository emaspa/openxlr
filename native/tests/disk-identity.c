// SPDX-License-Identifier: GPL-3.0-only
// The disk identity's Linux side (disk-identity.c) on made-up disks. Each
// case builds a small tree that stands in for /proc/self/mountinfo, /sys and
// /dev, points disk_identity_test_root at it, and checks the descriptor byte
// for byte against the layout Windows' NVMe driver gives: NVMe disks with an
// NGUID, with an EUI-64 only, with a serial only and with no firmware
// revision, one behind a LUKS mapping, and every case where nothing is given
// (a disk that is not NVMe, a prefix on tmpfs, a model too long to fit, a
// prefix with no drive_c). No real disk is read, so this runs anywhere.
//
// disk_identity_test_root is the only way in, and only a test sets it; the
// product has no variable that points the rules at another tree.
//
// Usage: disk-identity
// Usage: disk-identity --write FILE
// The second form reads this machine's own disk for the prefix WINEPREFIX
// names, as the helper does, and writes the descriptor to FILE, for a
// comparison with what the yabridge companion builds. Exit code 1 when there
// is none.
#define _GNU_SOURCE
#include "../disk-identity.c"

#include "disk-fixture.h"

// The test's paths are a temporary directory and a few names under it.
#pragma GCC diagnostic ignored "-Wformat-truncation"

static char base[PATH_MAX];    // everything the test makes
static char tree[PATH_MAX];    // the stand-in root, under base
static char holder[PATH_MAX];  // the directory a mount holds, with a space in its name
static char prefix[PATH_MAX];  // a Wine prefix in it
static int failures;

static void mount_table(const char *source, const char *type, const char *later) {
  fixture_mounts(tree, holder, source, type, later);
}

// Run the rules with standard error kept, and say how many lines they wrote
// about the disk and whether one holds `reason`.
static bool run(unsigned char out[DISK_IDENTITY_SIZE], unsigned *lines, const char *reason,
                bool *said) {
  fflush(stderr);
  FILE *kept = tmpfile();
  int saved = dup(STDERR_FILENO);
  assert(kept && saved >= 0);
  dup2(fileno(kept), STDERR_FILENO);
  memset(out, 0xee, DISK_IDENTITY_SIZE);
  bool given = disk_identity_build(out);
  fflush(stderr);
  dup2(saved, STDERR_FILENO);
  close(saved);
  rewind(kept);
  char line[2048];
  *lines = 0;
  *said = false;
  while (fgets(line, sizeof(line), kept)) {
    fputs(line, stderr);
    if (!strncmp(line, "trace: disk identity: ", 22))
      ++*lines;
    if (reason && strstr(line, reason))
      *said = true;
  }
  fclose(kept);
  return given;
}

static void given(const char *what, const char *model, const char *revision,
                  const char *serial) {
  unsigned char got[DISK_IDENTITY_SIZE], want[DISK_IDENTITY_SIZE];
  unsigned lines;
  bool said;
  fixture_expected(want, model, revision, serial);
  if (!run(got, &lines, "PhysicalDrive0 reports", &said) || memcmp(got, want, sizeof(got)) ||
      lines != 1 || !said) {
    size_t at = 0;
    while (at < sizeof(got) && got[at] == want[at]) ++at;
    printf("FAIL: %s: %s (first difference at byte %zu, %u lines)\n", what,
           at < sizeof(got) ? "the descriptor differs" : "no line says what was given", at,
           lines);
    ++failures;
    return;
  }
  printf("PASS: %s\n", what);
}

static void declined(const char *what, const char *reason) {
  unsigned char got[DISK_IDENTITY_SIZE];
  unsigned lines;
  bool said;
  bool answered = run(got, &lines, reason, &said);
  if (answered || lines != (reason ? 1u : 0u) || (reason && !said)) {
    printf("FAIL: %s: %s, %u lines\n", what, answered ? "a descriptor was given" : "the wrong line",
           lines);
    ++failures;
    return;
  }
  printf("PASS: %s\n", what);
}

static void remove_tree(const char *path) {
  char command[PATH_MAX + 32];
  // Only ever the test's own directory, made by mkdtemp below.
  assert(strstr(path, "/openxlr-disk-identity."));
  snprintf(command, sizeof(command), "rm -rf -- '%s'", path);
  assert(system(command) == 0);
}

static int write_own(const char *file) {
  unsigned char descriptor[DISK_IDENTITY_SIZE];
  if (!disk_identity_build(descriptor))
    return 1;
  FILE *out = fopen(file, "wb");
  if (!out || fwrite(descriptor, 1, sizeof(descriptor), out) != sizeof(descriptor) ||
      fclose(out) != 0) {
    perror(file);
    return 1;
  }
  return 0;
}

int main(int argc, char **argv) {
  if (argc == 3 && !strcmp(argv[1], "--write"))
    return write_own(argv[2]);
  if (argc != 1) {
    fputs("usage: disk-identity [--write FILE]\n", stderr);
    return 2;
  }
  setvbuf(stdout, NULL, _IOLBF, 0);
  const char *temporary = getenv("TMPDIR");
  snprintf(base, sizeof(base), "%s/openxlr-disk-identity.XXXXXX",
           temporary && temporary[0] == '/' ? temporary : "/tmp");
  assert(mkdtemp(base));
  // The mount table names directories by their real paths.
  char real[PATH_MAX];
  assert(realpath(base, real));
  snprintf(base, sizeof(base), "%s", real);
  snprintf(tree, sizeof(tree), "%s/root", base);
  snprintf(holder, sizeof(holder), "%s/the home", base);
  snprintf(prefix, sizeof(prefix), "%s/prefix", holder);
  char drive_c[PATH_MAX + 16];
  snprintf(drive_c, sizeof(drive_c), "%s/drive_c", prefix);
  fixture_directories(drive_c);
  disk_identity_test_root = tree;
  setenv("WINEPREFIX", prefix, 1);

  // Models and serials padded with blanks, as sysfs gives them.
  fixture_nvme(tree, 0, "e8238fa6-bf53-0001-001b-448b4a6b5c2b\n", "00 1b 44 8b 4a 6b 5c 2b\n",
           "WD_BLACK SN7100 4TB                     \n", "7613H0WD\n",
           "24301A800123        \n");
  fixture_nvme(tree, 1, "00000000-0000-0000-0000-000000000000\n", "00 25 38 5b 21 40 6d 4a\n",
           "Samsung SSD 990 PRO 2TB                 \n", "4B2QJXD7\n",
           "S7KHNJ0X123456X     \n");
  fixture_nvme(tree, 2, NULL, NULL, "INTEL SSDPEKNW010T8\n", "002C\n", "  BTNH9123456X1P0B  \n");
  fixture_nvme(tree, 3, NULL, "0000000000000000\n", "Generic NVMe\n", "\n", "SN-0003\n");
  fixture_nvme(tree, 4, "0123456789abcdef0123456789abcdef01\n", "\n",
           "A model name that runs past the sixty-four bytes stornvme leaves it   \n",
           "1.0\n", "SERIAL4\n");
  fixture_nvme(tree, 5, "abcdefabcdefabcdefabcdefabcdefab\n", NULL, "Short Rev\n", "123456789ABCDEFGH\n",
           "S5\n");
  fixture_sata(tree);
  fixture_luks(tree);

  mount_table("/dev/nvme0n1p2", NULL, NULL);
  given("an NVMe disk with an NGUID is named by it, in groups of four with a period",
        "WD_BLACK SN7100 4TB", "7613H0WD", "E823_8FA6_BF53_0001_001B_448B_4A6B_5C2B.");
  mount_table("/dev/nvme1n1p2", NULL, NULL);
  given("an NVMe disk whose NGUID is zero is named by its EUI-64", "Samsung SSD 990 PRO 2TB",
        "4B2QJXD7", "0025_385B_2140_6D4A.");
  mount_table("/dev/nvme2n1p2", NULL, NULL);
  given("an NVMe disk with neither is named by its controller's serial",
        "INTEL SSDPEKNW010T8", "002C", "BTNH9123456X1P0B");
  mount_table("/dev/nvme3n1p2", NULL, NULL);
  given("an NVMe disk with no firmware revision has no revision in its descriptor",
        "Generic NVMe", "", "SN-0003");
  mount_table("/dev/mapper/luks-root", NULL, NULL);
  given("a prefix behind a LUKS mapping is on the disk under the mapping",
        "Samsung SSD 990 PRO 2TB", "4B2QJXD7", "0025_385B_2140_6D4A.");
  mount_table("/dev/sda1", "btrfs", "/dev/nvme2n1p2");
  given("of two mounts on the prefix's directory the later one counts",
        "INTEL SSDPEKNW010T8", "002C", "BTNH9123456X1P0B");

  mount_table("/dev/sda1", NULL, NULL);
  declined("a prefix on a disk that is not NVMe is left to Wine", "is not an NVMe drive");
  mount_table("/dev/nvme0n1p2", "tmpfs", "tmpfs");
  declined("a prefix on a file system with no block device is left to Wine",
           "not a block device");
  mount_table("/dev/nvme4n1p2", NULL, NULL);
  declined("a model too long for the layout is left to Wine", "could not read the identity");
  mount_table("/dev/nvme5n1p2", NULL, NULL);
  declined("a firmware revision too long for the layout is left to Wine",
           "could not read the identity");
  mount_table("/dev/nvme9n1p2", NULL, NULL);
  declined("a device that is not there is left to Wine", "is not an NVMe drive");
  // A mount point the patch cannot read gives nothing at all.
  char path[PATH_MAX + 64];
  snprintf(path, sizeof(path), "%s/proc/self/mountinfo", tree);
  fixture_text(path, "21 1 8:1 / /\\xyz rw - ext4 /dev/nvme0n1p2 rw\n");
  declined("a mount table the companion cannot read is left to Wine", "mount table");

  mount_table("/dev/nvme0n1p2", NULL, NULL);
  char empty[PATH_MAX + 16];
  snprintf(empty, sizeof(empty), "%s/empty", holder);
  fixture_directories(empty);
  setenv("WINEPREFIX", empty, 1);
  declined("a prefix with no drive_c is left to Wine without a word", NULL);
  // No WINEPREFIX: the default prefix under HOME, as Wine takes it.
  unsetenv("WINEPREFIX");
  snprintf(path, sizeof(path), "%s/.wine", holder);
  assert(rename(prefix, path) == 0);
  setenv("HOME", holder, 1);
  given("without WINEPREFIX the prefix is ~/.wine", "WD_BLACK SN7100 4TB", "7613H0WD",
        "E823_8FA6_BF53_0001_001B_448B_4A6B_5C2B.");

  remove_tree(base);
  if (failures) {
    printf("FAIL: %d disk identity cases\n", failures);
    return 1;
  }
  return 0;
}
