// SPDX-License-Identifier: GPL-3.0-only
// The Windows backend: a Windows plugin that runs under Wine in a process of
// its own, openxlr-winhost.exe (winhost/winhost.c), while this side keeps
// the PipeWire node. The cut is at this file's Backend: load, activate, one
// block of audio, the latency, deactivate. Everything the plugin's own
// interface needs stays next to the plugin, on the Windows side. wire.h
// describes what crosses.
//
// The Windows side is another program running plugin code, so nothing here
// waits for it without a limit, and nothing it writes is used as a size or
// an index. If it dies, the audio thread hears it at once as the end of a
// pipe; if it stalls, the audio thread gives up on the block after a bounded
// wait. Either way the cycle plays silence and the main thread reports it,
// as it would for a native plugin that refuses its blocks.
//
// The plugin's controls are registered here under the names the Windows
// side lists, so the daemon sets and reads them as a native plugin's. Their
// values cross in the shared file: what this side sets goes with the next
// block, and what the plugin moves is read on the tick.
//
// A scan is the other thing this file starts the Windows host for
// (wine_scan): one module described and the process gone again, with none of
// the above between the two sides.
//
// This step carries CLAP and VST3 audio, parameters, latency and scanning.
// Editors come with a later one.
#define _GNU_SOURCE  // pipe2, realpath, SCHED_RESET_ON_FORK
#include "host.h"
#include "winhost/wire.h"

#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <langinfo.h>
#include <limits.h>
#include <locale.h>
#include <math.h>
#include <poll.h>
#include <sched.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/mman.h>
#include <sys/prctl.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

_Static_assert((int)WIRE_MAX_FRAMES == (int)MAX_FRAMES,
               "wire.h carries the host's largest block");
_Static_assert((int)WIRE_MAX_CHANNELS == (int)MAX_CHANNELS,
               "wire.h carries the host's channels");
_Static_assert((int)WIRE_MAX_CONTROLS == (int)MAX_CONTROLS,
               "wire.h carries the host's control table");

// How long the main thread waits for an answer on the control channel. Wine
// may be creating or updating its prefix before the first one, and a plugin
// may read a licence or unpack its presets while it loads. A Windows host
// that has died ends any of these waits at once.
enum {
  WINE_HELLO_MS = 120000,
  WINE_LOAD_MS = 120000,
  WINE_CONTROL_MS = 30000,
  WINE_QUIT_MS = 2000,
  // How long the report of a death holds on for the way the process ended.
  WINE_ENDING_MS = 200,
  // The least time a block gets before its cycle gives up on it.
  WINE_BLOCK_MS = 20
};

// How long one block may stay out before the instance is given up, the same
// patience the host has with a native plugin inside one callback.
#ifndef WINE_STALL_MS
#define WINE_STALL_MS 6000
#endif

// How long a scan may take from start to end: what Wine may need to start,
// as before a load, and the minute the daemon gives the scan of one native
// bundle. The daemon's own deadline may well come first. A helper it kills
// takes the Windows host with it.
#ifndef WINE_SCAN_MS
#define WINE_SCAN_MS (WINE_HELLO_MS + 60000)
#endif

extern char **environ;

// One of the plugin's controls on this side.
typedef struct {
  Control *control;
  char symbol[WIRE_SYMBOL];  // the name the host's table points at
  // The value the main thread last took from the plugin, with its count
  // (wire_value). Written by the main thread, read by the audio thread.
  _Atomic uint64_t taken;
  // Audio thread: the count of `taken` it has caught up with, and the value
  // the Windows side holds as far as this side knows.
  uint32_t caught_up;
  float sent;
} WineControl;

typedef struct {
  Host *h;
  pid_t child;      // what was started: wine, which becomes the Windows host
  pid_t process;    // the process the audio thread was found in
  pid_t audio_tid;  // that thread, or 0 when it was not found
  bool reaped;      // the child was waited for
  int wait_status;
  // The audio wake: a pipe each way, the Windows host's standard input and
  // output. Both ends here never block.
  int wake_out, wake_in;
  int request, reply;  // the control FIFOs
  int log;             // the Windows host's standard error
  WireShared *shared;
  WineControl *controls;
  uint32_t control_count;
  uint32_t published;  // main thread: the shared count as last looked at
  struct spa_source *log_source;
  char log_line[1024];
  size_t log_size;
  char thread_name[WIRE_THREAD_NAME];
  bool greeted;    // the Windows host answered the hello
  bool activated;
  bool failed;      // main thread: the failure has been reported
  int64_t dead_at;  // main thread: when it first saw the Windows host dead
  bool scheduled;   // main thread: the audio thread's scheduling is settled
  // Audio thread only: a block went out and its answer has not come back.
  bool pending;
  // The Windows host is gone, or broke the protocol. Either thread may find
  // out first.
  _Atomic bool dead;
  // When the pending block was given up, in milliseconds on the monotonic
  // clock, or 0 while none is out.
  _Atomic int64_t stalled_since;
  // The scheduling of the thread that waits for the Windows audio thread,
  // recorded by that thread on its first block.
  _Atomic int waiter_policy, waiter_priority;
  _Atomic bool waiter_known;
} Wine;

static int64_t wine_now_ms(void) {
  struct timespec now;
  clock_gettime(CLOCK_MONOTONIC, &now);
  return (int64_t)now.tv_sec * 1000 + now.tv_nsec / 1000000;
}

// --- the Windows host's standard error --------------------------------------

// Pass on what the Windows side writes, a line at a time: the plugin's log,
// trace lines, and whatever Wine itself has to say. A line longer than the
// buffer goes out in pieces.
static void wine_drain_log(Wine *w) {
  if (w->log < 0)
    return;
  for (;;) {
    ssize_t count = read(w->log, w->log_line + w->log_size,
                         sizeof(w->log_line) - 1 - w->log_size);
    if (count <= 0)
      break;
    w->log_size += (size_t)count;
    w->log_line[w->log_size] = 0;
    char *start = w->log_line, *end;
    while ((end = memchr(start, '\n', w->log_size - (size_t)(start - w->log_line)))) {
      *end = 0;
      fprintf(stderr, "%s\n", start);
      start = end + 1;
    }
    w->log_size -= (size_t)(start - w->log_line);
    memmove(w->log_line, start, w->log_size);
    if (w->log_size == sizeof(w->log_line) - 1) {
      fprintf(stderr, "%s\n", w->log_line);
      w->log_size = 0;
    }
  }
}

static void wine_log_ready(void *data, int fd, uint32_t mask) {
  Wine *w = data;
  wine_drain_log(w);
  // Every writer is gone: stop listening, or the loop would spin on the
  // hang-up. The source itself goes at unload.
  if ((mask & (SPA_IO_HUP | SPA_IO_ERR)) && !(mask & SPA_IO_IN))
    pw_loop_update_io(host_loop(w->h), w->log_source, 0);
}

// --- the child --------------------------------------------------------------

static bool wine_reap(Wine *w) {
  if (w->reaped || w->child <= 0)
    return true;
  int status = 0;
  if (waitpid(w->child, &status, WNOHANG) != w->child)
    return false;
  w->reaped = true;
  w->wait_status = status;
  return true;
}

// Whether the Windows host has exited. Where Wine put another process
// between what was started and the Windows host, the end of the first says
// nothing about the second, whose pipes will tell.
static bool wine_gone(Wine *w) {
  return wine_reap(w) && (w->process <= 0 || w->process == w->child);
}

// What became of the Windows host, for the line that reports it. A dying
// process closes its pipes a little before it can be waited for, so a
// caller that has just seen them close allows it `wait_ms` to finish.
static const char *wine_ending(Wine *w, char *text, size_t size, int wait_ms) {
  for (int64_t until = wine_now_ms() + wait_ms;
       !wine_reap(w) && wine_now_ms() < until;)
    usleep(2000);
  if (!wine_reap(w))
    snprintf(text, size, "the Windows plugin host stopped answering");
  else if (WIFSIGNALED(w->wait_status))
    snprintf(text, size, "the Windows plugin host was ended by signal %d",
             WTERMSIG(w->wait_status));
  else
    snprintf(text, size, "the Windows plugin host exited with code %d",
             WEXITSTATUS(w->wait_status));
  return text;
}

// A program by name on PATH, or as given when the name holds a slash.
static bool wine_find_program(const char *name, char *out, size_t size) {
  if (strchr(name, '/')) {
    snprintf(out, size, "%s", name);
    return access(out, X_OK) == 0;
  }
  const char *path = getenv("PATH");
  if (!path || !path[0])
    path = "/usr/local/bin:/usr/bin:/bin";
  while (*path) {
    size_t length = strcspn(path, ":");
    if (length && snprintf(out, size, "%.*s/%s", (int)length, path, name) < (int)size &&
        access(out, X_OK) == 0)
      return true;
    path += length;
    if (*path == ':')
      ++path;
  }
  return false;
}

// The Windows host is one file beside this helper, or wherever
// OPENXLR_WINHOST says.
static bool wine_find_winhost(char *out, size_t size) {
  const char *named = getenv("OPENXLR_WINHOST");
  if (named && named[0]) {
    snprintf(out, size, "%s", named);
    return access(out, R_OK) == 0;
  }
  char self[PATH_MAX];
  ssize_t length = readlink("/proc/self/exe", self, sizeof(self) - 1);
  if (length <= 0)
    return false;
  self[length] = 0;
  char *slash = strrchr(self, '/');
  if (!slash)
    return false;
  *slash = 0;
  return snprintf(out, size, "%s/openxlr-winhost.exe", self) < (int)size &&
         access(out, R_OK) == 0;
}

// Whether a locale of this system, by the name an environment variable
// holds, reads text as UTF-8. A name the system has no locale for reads as
// the C locale does, which is how a program started under it ends up.
static bool wine_locale_reads_utf8(const char *name) {
  locale_t locale = newlocale(LC_CTYPE_MASK, name, (locale_t)0);
  if (!locale)
    return false;
  const char *codeset = nl_langinfo_l(CODESET, locale);
  bool utf8 = codeset && (!strcasecmp(codeset, "UTF-8") || !strcasecmp(codeset, "UTF8"));
  freelocale(locale);
  return utf8;
}

// Wine reads this system's file names in the character set of its locale.
// The daemon hands paths over as UTF-8 whatever its own locale is. It runs
// its scans in the C locale, and a user service may have no locale at all,
// and there Wine cannot find a plugin whose path has a character outside
// ASCII. So the Windows host is given a locale that
// reads UTF-8 when the inherited one does not. It is the inherited locale
// with UTF-8 as its character set where this system has that one, so the
// language Wine takes from it stays, and C.UTF-8 otherwise.
//
// Only the variable that decides the character set changes: LC_ALL when it
// is set, since it overrides the rest, else LC_CTYPE. The entry to put in
// place of it is written to `entry` as NAME=VALUE, and the length of NAME=
// comes back. 0 means the environment is right as it is. This process's own
// locale is not touched.
static size_t wine_utf8_locale(char *entry, size_t size) {
  const char *variable = "LC_ALL";
  const char *name = getenv("LC_ALL");
  if (!name || !name[0]) {
    variable = "LC_CTYPE";
    name = getenv("LC_CTYPE");
  }
  if (!name || !name[0])
    name = getenv("LANG");
  if (!name || !name[0])
    name = "C";
  if (wine_locale_reads_utf8(name))
    return 0;
  char same[96];
  size_t stem = strcspn(name, ".@");
  const char *modifier = strchr(name, '@');
  const char *chosen = "C.UTF-8";
  if (stem && snprintf(same, sizeof(same), "%.*s.UTF-8%s", (int)stem, name,
                       modifier ? modifier : "") < (int)sizeof(same) &&
      wine_locale_reads_utf8(same))
    chosen = same;
  else if (!wine_locale_reads_utf8(chosen))
    return 0;  // this system has no UTF-8 locale to give
  if (snprintf(entry, size, "%s=%s", variable, chosen) >= (int)size)
    return 0;
  return strlen(variable) + 1;
}

// Start Wine with the Windows host and its one argument: the directory of
// the channel, or `scan`. It inherits this process's environment, which the
// daemon has already given the Wine prefix and the overrides it wants, with
// two changes. WAYLAND_DISPLAY is left out: an editor has to be an X11
// window, which is what the frame it will sit in is. And the locale reads
// UTF-8 (wine_utf8_locale).
static bool wine_spawn(Wine *w, const char *argument) {
  char wine[PATH_MAX], winhost[PATH_MAX];
  const char *named = getenv("OPENXLR_WINE");
  if (!wine_find_program(named && named[0] ? named : "wine", wine, sizeof(wine))) {
    fputs("Wine is not installed, so a Windows plugin cannot run\n", stderr);
    return false;
  }
  if (!wine_find_winhost(winhost, sizeof(winhost))) {
    fputs("openxlr-winhost.exe is missing beside the plugin host\n", stderr);
    return false;
  }
  size_t count = 0;
  while (environ[count])
    ++count;
  char **environment = calloc(count + 2, sizeof(char *));
  if (!environment)
    return false;
  char locale[128];
  size_t replaced = wine_utf8_locale(locale, sizeof(locale));
  size_t kept = 0;
  for (size_t i = 0; i < count; ++i)
    if (strncmp(environ[i], "WAYLAND_DISPLAY=", 16) != 0 &&
        !(replaced && !strncmp(environ[i], locale, replaced)))
      environment[kept++] = environ[i];
  if (replaced)
    environment[kept++] = locale;
  char *arguments[] = {wine, winhost, (char *)argument, NULL};

  int to[2] = {-1, -1}, from[2] = {-1, -1}, log[2] = {-1, -1};
  if (pipe2(to, O_CLOEXEC) != 0 || pipe2(from, O_CLOEXEC) != 0 ||
      pipe2(log, O_CLOEXEC) != 0) {
    perror("pipe");
    for (int i = 0; i < 2; ++i) {
      if (to[i] >= 0) close(to[i]);
      if (from[i] >= 0) close(from[i]);
      if (log[i] >= 0) close(log[i]);
    }
    free(environment);
    return false;
  }
  pid_t parent = getpid();
  w->child = fork();
  if (w->child == 0) {
    // Only what is safe between fork and exec in a process with threads.
    dup2(to[0], STDIN_FILENO);
    dup2(from[1], STDOUT_FILENO);
    dup2(log[1], STDERR_FILENO);
    sigset_t none;
    sigemptyset(&none);
    sigprocmask(SIG_SETMASK, &none, NULL);
    signal(SIGPIPE, SIG_DFL);
    // If this helper is killed, the Windows host goes with it even when it
    // is stuck inside a plugin and deaf to its closed pipes.
    prctl(PR_SET_PDEATHSIG, SIGKILL);
    if (getppid() != parent)
      _exit(127);
    execve(wine, arguments, environment);
    _exit(127);
  }
  free(environment);
  close(to[0]);
  close(from[1]);
  close(log[1]);
  if (w->child < 0) {
    perror("fork");
    close(to[1]);
    close(from[0]);
    close(log[0]);
    return false;
  }
  w->wake_out = to[1];
  w->wake_in = from[0];
  w->log = log[0];
  fcntl(w->wake_out, F_SETFL, O_NONBLOCK);
  fcntl(w->wake_in, F_SETFL, O_NONBLOCK);
  fcntl(w->log, F_SETFL, O_NONBLOCK);
  return true;
}

// --- the control channel ----------------------------------------------------

// The private directory the Windows host is pointed at: the shared audio
// file and the two FIFOs. It lives in memory, under the user's runtime
// directory where there is one, and is gone again as soon as the Windows
// host has opened what is in it.
static bool wine_make_channel(Wine *w, char *directory, size_t size) {
  const char *base = getenv("XDG_RUNTIME_DIR");
  bool made = false;
  if (base && base[0] == '/' &&
      snprintf(directory, size, "%s/openxlr-winhost.XXXXXX", base) < (int)size)
    made = mkdtemp(directory) != NULL;
  if (!made) {
    snprintf(directory, size, "/dev/shm/openxlr-winhost.XXXXXX");
    made = mkdtemp(directory) != NULL;
  }
  if (!made) {
    perror("cannot make a directory for the Windows plugin host");
    directory[0] = 0;
    return false;
  }
  char path[PATH_MAX];
  snprintf(path, sizeof(path), "%s/%s", directory, WIRE_AUDIO_FILE);
  int file = open(path, O_RDWR | O_CREAT | O_EXCL | O_CLOEXEC, 0600);
  if (file < 0 || ftruncate(file, sizeof(WireShared)) != 0) {
    perror("cannot make the shared audio file");
    if (file >= 0) close(file);
    return false;
  }
  void *mapped = mmap(NULL, sizeof(WireShared), PROT_READ | PROT_WRITE,
                      MAP_SHARED, file, 0);
  close(file);
  if (mapped == MAP_FAILED) {
    perror("cannot map the shared audio file");
    return false;
  }
  w->shared = mapped;
  // Audio memory that is paged out costs a block; failing to pin it is not
  // worth refusing over.
  mlock(w->shared, sizeof(WireShared));
  w->shared->magic = WIRE_MAGIC;
  w->shared->version = WIRE_VERSION;
  w->shared->size = sizeof(WireShared);
  // Opened for both directions, the request FIFO never waits for its
  // reader, and its reader sees the end of it the moment this process is
  // gone. The reply FIFO is only read, and never blocks.
  snprintf(path, sizeof(path), "%s/%s", directory, WIRE_REQUEST_FIFO);
  if (mkfifo(path, 0600) != 0 ||
      (w->request = open(path, O_RDWR | O_CLOEXEC)) < 0) {
    perror("cannot make the request FIFO");
    return false;
  }
  snprintf(path, sizeof(path), "%s/%s", directory, WIRE_REPLY_FIFO);
  if (mkfifo(path, 0600) != 0 ||
      (w->reply = open(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC)) < 0) {
    perror("cannot make the reply FIFO");
    return false;
  }
  return true;
}

static void wine_remove_channel(const char *directory) {
  if (!directory[0])
    return;
  static const char *const names[] = {WIRE_AUDIO_FILE, WIRE_REQUEST_FIFO,
                                      WIRE_REPLY_FIFO};
  char path[PATH_MAX];
  for (size_t i = 0; i < sizeof(names) / sizeof(names[0]); ++i) {
    snprintf(path, sizeof(path), "%s/%s", directory, names[i]);
    unlink(path);
  }
  rmdir(directory);
}

// One request and its reply, on the main thread. False when the Windows
// host did not answer in time, went away, or answered something else; what
// it said no to comes back as a reply with `ok` clear.
static bool wine_exchange(Wine *w, WireRequest *request, WireReply *reply,
                          int timeout_ms) {
  if (atomic_load(&w->dead))
    return false;
  request->magic = WIRE_MAGIC;
  const char *out = (const char *)request;
  for (size_t done = 0; done < sizeof(*request);) {
    ssize_t count = write(w->request, out + done, sizeof(*request) - done);
    if (count < 0 && errno == EINTR)
      continue;
    if (count <= 0) {
      atomic_store(&w->dead, true);
      return false;
    }
    done += (size_t)count;
  }
  char *in = (char *)reply;
  size_t got = 0;
  int64_t deadline = wine_now_ms() + timeout_ms;
  while (got < sizeof(*reply)) {
    struct pollfd fds[2] = {{w->reply, POLLIN, 0}, {w->log, POLLIN, 0}};
    int ready = poll(fds, 2, 100);
    wine_drain_log(w);
    if (ready > 0 && (fds[0].revents & POLLIN)) {
      ssize_t count = read(w->reply, in + got, sizeof(*reply) - got);
      if (count > 0)
        got += (size_t)count;
      continue;
    }
    // Nothing more to read and nobody left to write it, a child that has
    // exited, or time up.
    if ((ready > 0 && (fds[0].revents & (POLLHUP | POLLERR))) ||
        wine_gone(w) || wine_now_ms() >= deadline) {
      atomic_store(&w->dead, true);
      return false;
    }
  }
  if (reply->magic != WIRE_MAGIC || reply->type != request->type) {
    atomic_store(&w->dead, true);
    return false;
  }
  // Text from the other side: ended, and nothing in it that a terminal or a
  // log would act on.
  reply->text[sizeof(reply->text) - 1] = 0;
  for (char *c = reply->text; *c; ++c)
    if ((unsigned char)*c < 0x20 || *c == 0x7f)
      *c = ' ';
  return true;
}

// The Windows audio thread, by the name it took. Its own process is looked
// in first; Wine may have put another process between what was started and
// the Windows host, so the user's other processes are looked through after.
static pid_t wine_find_thread_in(const char *name, const char *process) {
  char path[64];
  snprintf(path, sizeof(path), "/proc/%s/task", process);
  DIR *tasks = opendir(path);
  if (!tasks)
    return 0;
  pid_t found = 0;
  struct dirent *task;
  while (!found && (task = readdir(tasks))) {
    if (task->d_name[0] < '0' || task->d_name[0] > '9')
      continue;
    char comm_path[384], comm[32] = "";
    snprintf(comm_path, sizeof(comm_path), "/proc/%s/task/%s/comm", process,
             task->d_name);
    FILE *file = fopen(comm_path, "r");
    if (!file)
      continue;
    if (fgets(comm, sizeof(comm), file))
      comm[strcspn(comm, "\n")] = 0;
    fclose(file);
    if (!strcmp(comm, name))
      found = (pid_t)strtol(task->d_name, NULL, 10);
  }
  closedir(tasks);
  return found;
}

static pid_t wine_find_thread(Wine *w) {
  char process[16];
  snprintf(process, sizeof(process), "%d", (int)w->child);
  pid_t found = wine_find_thread_in(w->thread_name, process);
  if (found) {
    w->process = w->child;
    return found;
  }
  DIR *proc = opendir("/proc");
  if (!proc)
    return 0;
  uid_t uid = getuid();
  struct dirent *entry;
  while (!found && (entry = readdir(proc))) {
    if (entry->d_name[0] < '0' || entry->d_name[0] > '9')
      continue;
    char path[280];
    struct stat status;
    snprintf(path, sizeof(path), "/proc/%s", entry->d_name);
    if (stat(path, &status) != 0 || status.st_uid != uid)
      continue;
    found = wine_find_thread_in(w->thread_name, entry->d_name);
    if (found)
      w->process = (pid_t)strtol(entry->d_name, NULL, 10);
  }
  closedir(proc);
  return found;
}

// --- controls ---------------------------------------------------------------

// A control's name is printed in lines the daemon reads and matched against
// the ones it sends, so it has to be one plain word.
static bool wine_symbol_ok(const char *symbol) {
  if (!symbol[0])
    return false;
  for (const unsigned char *c = (const unsigned char *)symbol; *c; ++c)
    if (*c <= 0x20 || *c >= 0x7f)
      return false;
  return true;
}

// Register what the Windows side listed at load, `count` controls by its
// reply. Every entry is copied out of the shared file before it is looked
// at, and a list this side cannot carry is refused whole.
static bool wine_take_controls(Host *h, Wine *w, uint32_t count) {
  if (count > WIRE_MAX_CONTROLS) {
    fprintf(stderr,
            "the Windows plugin host lists %u controls; at most %d can be "
            "carried\n",
            count, WIRE_MAX_CONTROLS);
    return false;
  }
  if (!count)
    return true;
  w->controls = calloc(count, sizeof(WineControl));
  if (!w->controls)
    return false;
  for (uint32_t i = 0; i < count; ++i) {
    WireControl listed = w->shared->controls[i];
    listed.symbol[WIRE_SYMBOL - 1] = 0;
    if (!wine_symbol_ok(listed.symbol)) {
      fprintf(stderr,
              "the Windows plugin host gave control %u a name that cannot be "
              "used\n",
              i);
      return false;
    }
    WineControl *c = &w->controls[i];
    memcpy(c->symbol, listed.symbol, sizeof(c->symbol));
    c->control = host_add_control(h, c->symbol, listed.minimum, listed.maximum,
                                  listed.initial,
                                  (listed.flags & WIRE_CONTROL_OUTPUT) != 0, c);
    if (!c->control) {
      fputs("the plugin has more controls than this host carries\n", stderr);
      return false;
    }
    c->sent = atomic_load(&c->control->desired);
    atomic_init(&c->taken, wire_value(0, c->sent));
    w->control_count = i + 1;
  }
  return true;
}

// Main thread, each tick: the values the plugin moved and the meters that
// changed since the last look. A moved control is applied and told to the
// daemon as a native plugin's is. The control's own value is written before
// `taken`, which is the order wine_write_sets relies on.
static void wine_take_values(Host *h, Wine *w) {
  uint32_t published = atomic_load(&w->shared->published);
  if (published == w->published)
    return;
  w->published = published;
  for (uint32_t i = 0; i < w->control_count; ++i) {
    WineControl *c = &w->controls[i];
    uint64_t word = atomic_load(&w->shared->values[i]);
    uint32_t count = wire_value_count(word);
    if (count == wire_value_count(atomic_load(&c->taken)))
      continue;
    float value = wire_value_float(word);
    if (c->control->output) {
      if (isfinite(value))
        atomic_store(&c->control->observed, value);
    } else
      host_control_moved(h, c->control, value);
    atomic_store(&c->taken,
                 wire_value(count, atomic_load(&c->control->desired)));
  }
}

// Audio thread, before a block goes out: the values this side has set since
// the Windows side last heard. A value the main thread took from the plugin
// is not one of them. `taken` is read before the control's own value: read
// the other way round, a value just taken could be sent back as if it had
// been set here, with a count that makes the Windows side accept it. Read
// this way the worst is a value sent with the count before it, which the
// Windows side drops. More than one cycle's worth wait for the next.
static void wine_write_sets(Wine *w) {
  uint32_t count = 0;
  for (uint32_t i = 0; i < w->control_count && count < WIRE_MAX_SETS; ++i) {
    WineControl *c = &w->controls[i];
    if (c->control->output)
      continue;
    uint64_t taken = atomic_load(&c->taken);
    if (wire_value_count(taken) != c->caught_up) {
      c->caught_up = wire_value_count(taken);
      c->sent = wire_value_float(taken);
    }
    float value = atomic_load(&c->control->desired);
    if (value == c->sent)
      continue;
    w->shared->sets[count++] =
        (WireSet){.index = i, .based_on = c->caught_up, .value = value};
    c->sent = value;
  }
  w->shared->set_count = count;
}

// --- load and unload --------------------------------------------------------

// End the Windows host and wait for it. Asked to quit it takes the plugin
// down properly and exits by itself; a host that does not, because it is
// stuck inside the plugin or already gone wrong, is killed. True when it
// left by itself with a clean exit.
static bool wine_stop_child(Wine *w) {
  if (w->child <= 0)
    return false;
  if (!wine_reap(w)) {
    if (w->greeted && !atomic_load(&w->dead)) {
      WireRequest request = {.type = WIRE_QUIT};
      WireReply reply;
      wine_exchange(w, &request, &reply, WINE_QUIT_MS);
    }
    int64_t deadline = wine_now_ms() + WINE_QUIT_MS;
    while (!wine_reap(w) && !atomic_load(&w->dead) && wine_now_ms() < deadline) {
      struct pollfd log = {w->log, POLLIN, 0};
      poll(&log, 1, 10);
      wine_drain_log(w);
    }
  }
  if (!wine_reap(w)) {
    // The process the audio thread was found in, when that is not the one
    // that was started, and only while that thread is still there to say
    // the number has not passed to another process.
    if (w->process > 0 && w->process != w->child && w->audio_tid) {
      char process[16];
      snprintf(process, sizeof(process), "%d", (int)w->process);
      if (wine_find_thread_in(w->thread_name, process) == w->audio_tid)
        kill(w->process, SIGKILL);
    }
    kill(w->child, SIGKILL);
    int status = 0;
    while (waitpid(w->child, &status, 0) < 0 && errno == EINTR) {
    }
    w->reaped = true;
    w->wait_status = status;
  }
  wine_drain_log(w);
  return WIFEXITED(w->wait_status) && WEXITSTATUS(w->wait_status) == 0;
}

// A VST3 plugin is named by its bundle, as for the native backend: a
// directory with the Windows module under Contents/x86_64-win, or an older
// plugin that is the module itself. The module is found here, where the
// bundle is a directory of this system's, and the Windows side is handed the
// file. `path` is the bundle on the way in and the module on the way out.
static bool wine_vst3_module(char *path, size_t size) {
  struct stat status;
  if (stat(path, &status) != 0)
    return false;
  if (!S_ISDIR(status.st_mode))
    return true;
  char directory[PATH_MAX];
  if (snprintf(directory, sizeof(directory), "%s/Contents/x86_64-win", path) >=
      (int)sizeof(directory))
    return false;
  DIR *entries = opendir(directory);
  if (!entries)
    return false;
  bool found = false;
  struct dirent *entry;
  while (!found && (entry = readdir(entries))) {
    size_t length = strlen(entry->d_name);
    found = length > 5 && !strcasecmp(entry->d_name + length - 5, ".vst3") &&
            snprintf(path, size, "%s/%s", directory, entry->d_name) < (int)size;
  }
  closedir(entries);
  return found;
}

static bool wine_load(Host *h, char **arguments) {
  Wine *w = calloc(1, sizeof(Wine));
  if (!w)
    return false;
  h->impl = w;
  w->h = h;
  w->wake_out = w->wake_in = w->request = w->reply = w->log = -1;
  uint32_t format = !strcmp(arguments[0], "clap")   ? WIRE_FORMAT_CLAP
                    : !strcmp(arguments[0], "vst3") ? WIRE_FORMAT_VST3
                                                    : 0;
  if (!format) {
    fprintf(stderr, "the Windows plugin host carries CLAP and VST3, not %s\n",
            arguments[0]);
    return false;
  }
  char module[PATH_MAX];
  if (!realpath(arguments[1], module)) {
    fprintf(stderr, "cannot find %s: %s\n", arguments[1], strerror(errno));
    return false;
  }
  if (format == WIRE_FORMAT_VST3 && !wine_vst3_module(module, sizeof(module))) {
    fprintf(stderr, "no 64-bit Windows plugin module in %s\n", arguments[1]);
    return false;
  }
  if (strlen(module) >= WIRE_PATH || strlen(arguments[2]) >= WIRE_ID) {
    fputs("the plugin's path or its name is too long to carry\n", stderr);
    return false;
  }
  // A write to a Windows host that has just died must come back as an
  // error, not end this process.
  signal(SIGPIPE, SIG_IGN);
  char directory[PATH_MAX] = "";
  bool started = wine_make_channel(w, directory, sizeof(directory)) &&
                 wine_spawn(w, directory);
  WireReply reply = {0};
  if (started) {
    // One process holds one plugin, so its number names the audio thread.
    snprintf(w->thread_name, sizeof(w->thread_name), "oxwh-%d", (int)getpid());
    WireRequest hello = {.type = WIRE_HELLO, .version = WIRE_VERSION};
    memcpy(hello.thread, w->thread_name, sizeof(hello.thread));
    if (!wine_exchange(w, &hello, &reply, WINE_HELLO_MS)) {
      char ending[96];
      fprintf(stderr, "the Windows plugin host did not start: %s\n",
              wine_ending(w, ending, sizeof(ending), WINE_ENDING_MS));
      started = false;
    } else if (!reply.ok || reply.version != WIRE_VERSION ||
               reply.shared_size != sizeof(WireShared)) {
      fprintf(stderr, "the Windows plugin host is not this build's: %s\n",
              reply.text[0] ? reply.text : "its version differs");
      started = false;
    } else
      w->greeted = true;
  }
  // The Windows host has opened what it needs, or never will.
  wine_remove_channel(directory);
  if (!started) {
    atomic_store(&w->dead, true);  // nothing to ask to quit: unload ends it
    return false;
  }
  for (int attempt = 0; attempt < 10 && !w->audio_tid; ++attempt) {
    if (attempt)
      usleep(10000);
    w->audio_tid = wine_find_thread(w);
  }
  WireRequest load = {.type = WIRE_LOAD, .format = format,
                      .channels = h->channels, .rate = h->rate,
                      .own_block = h->own_block};
  snprintf(load.path, sizeof(load.path), "%s", module);
  snprintf(load.id, sizeof(load.id), "%s", arguments[2]);
  if (!wine_exchange(w, &load, &reply, WINE_LOAD_MS)) {
    char ending[96];
    fprintf(stderr, "the plugin did not load: %s\n",
            wine_ending(w, ending, sizeof(ending), WINE_ENDING_MS));
    return false;
  }
  if (!reply.ok) {
    fprintf(stderr, "%s\n", reply.text[0] ? reply.text : "the plugin did not load");
    return false;
  }
  host_set_plugin_name(h, reply.text);
  if (!wine_take_controls(h, w, reply.controls))
    return false;
  w->log_source = pw_loop_add_io(host_loop(h), w->log, SPA_IO_IN | SPA_IO_HUP,
                                 false, wine_log_ready, w);
  return true;
}

static void wine_unload(Host *h) {
  Wine *w = h->impl;
  wine_stop_child(w);
  if (w->log_source)
    pw_loop_destroy_source(host_loop(h), w->log_source);
  if (w->log_size)
    fprintf(stderr, "%.*s\n", (int)w->log_size, w->log_line);
  int *fds[] = {&w->wake_out, &w->wake_in, &w->request, &w->reply, &w->log};
  for (size_t i = 0; i < sizeof(fds) / sizeof(fds[0]); ++i)
    if (*fds[i] >= 0)
      close(*fds[i]);
  if (w->shared)
    munmap(w->shared, sizeof(WireShared));
  free(w->controls);
  free(w);
  h->impl = NULL;
}

// --- activation -------------------------------------------------------------

static bool wine_activate(Host *h) {
  Wine *w = h->impl;
  WireRequest request = {.type = WIRE_ACTIVATE};
  WireReply reply;
  if (!wine_exchange(w, &request, &reply, WINE_CONTROL_MS)) {
    char ending[96];
    fprintf(stderr, "the plugin did not activate: %s\n",
            wine_ending(w, ending, sizeof(ending), WINE_ENDING_MS));
    return false;
  }
  if (!reply.ok) {
    fprintf(stderr, "%s\n", reply.text[0] ? reply.text : "the plugin refused to activate");
    return false;
  }
  w->activated = true;
  return true;
}

// Wait for the one byte that answers a wake, outside the audio cycle.
static bool wine_await_done(Wine *w, int timeout_ms) {
  struct pollfd fd = {w->wake_in, POLLIN, 0};
  char byte = 0;
  return poll(&fd, 1, timeout_ms) > 0 && read(w->wake_in, &byte, 1) == 1 &&
         byte == WIRE_WAKE_DONE;
}

// The node has disconnected and the own block's worker has ended, so no
// block can start any more and this thread may read the audio wake. A block
// that is still out is waited for, so the Windows audio thread is idle when
// the plugin is deactivated. A Windows host that does not answer is left
// for unload to end.
static void wine_deactivate(Host *h) {
  Wine *w = h->impl;
  if (!w->activated)
    return;
  w->activated = false;
  // Already given up on: there is nothing to wait for.
  if (w->failed)
    atomic_store(&w->dead, true);
  if (atomic_load(&w->dead))
    return;
  if (w->pending && !wine_await_done(w, 1000)) {
    atomic_store(&w->dead, true);
    return;
  }
  w->pending = false;
  WireRequest request = {.type = WIRE_DEACTIVATE};
  WireReply reply;
  wine_exchange(w, &request, &reply, WINE_CONTROL_MS);
}

// --- audio ------------------------------------------------------------------

static void wine_silence(Host *h, uint32_t frames, float *const *out) {
  for (unsigned c = 0; c < h->channels; ++c)
    memset(out[c], 0, frames * sizeof(float));
}

// The Windows host is gone: this block and every later one is refused,
// which the host counts, and the main thread says why on its next tick.
static void wine_lost(Host *h, Wine *w, uint32_t frames, float *const *out) {
  atomic_store(&w->dead, true);
  wine_silence(h, frames, out);
  host_processing_result(h, false, frames);
}

// One block: copy the input and the values set since the last one into the
// shared file, wake the Windows audio thread with one byte, wait for the
// byte that answers, copy the output back. The wait is bounded at twice
// the block's own length and at least WINE_BLOCK_MS, so a block that comes
// late by less than that is heard late, as a native plugin's would be, and
// one that takes longer plays as silence. Its answer is then still owed:
// until it arrives every block plays silence without asking for another,
// and the shared file is left alone, since the Windows side may still be
// reading it.
static void wine_process(Host *h, uint32_t frames, float *const *in,
                         float *const *out) {
  Wine *w = h->impl;
  // Once: what this thread runs at, for the main thread to hand on.
  if (!atomic_load_explicit(&w->waiter_known, memory_order_relaxed)) {
    struct sched_param param = {0};
    int policy = sched_getscheduler(0);
    sched_getparam(0, &param);
    atomic_store(&w->waiter_policy, policy);
    atomic_store(&w->waiter_priority, param.sched_priority);
    atomic_store(&w->waiter_known, true);
  }
  if (atomic_load(&w->dead) || frames > WIRE_MAX_FRAMES) {
    wine_lost(h, w, frames > WIRE_MAX_FRAMES ? WIRE_MAX_FRAMES : frames, out);
    return;
  }
  char byte = 0;
  if (w->pending) {
    ssize_t count = read(w->wake_in, &byte, 1);
    if (count == 0 || (count < 0 && errno != EAGAIN && errno != EINTR) ||
        (count == 1 && byte != WIRE_WAKE_DONE)) {
      wine_lost(h, w, frames, out);
      return;
    }
    if (count != 1) {
      wine_silence(h, frames, out);
      return;
    }
    // The late block is back; what it holds was due long ago.
    w->pending = false;
    atomic_store(&w->stalled_since, 0);
  }
  for (unsigned c = 0; c < h->channels; ++c)
    memcpy(w->shared->in[c], in[c], frames * sizeof(float));
  w->shared->frames = frames;
  w->shared->status = 0;
  wine_write_sets(w);
  byte = WIRE_WAKE_PROCESS;
  if (write(w->wake_out, &byte, 1) != 1) {
    // A full pipe would mean thousands of unread wakes, which the protocol
    // rules out; anything else is a Windows host that is gone.
    wine_lost(h, w, frames, out);
    return;
  }
  int wait_ms = (int)((uint64_t)frames * 2000 / h->rate);
  if (wait_ms < WINE_BLOCK_MS)
    wait_ms = WINE_BLOCK_MS;
  struct pollfd fd = {w->wake_in, POLLIN, 0};
  int ready;
  do
    ready = poll(&fd, 1, wait_ms);
  while (ready < 0 && errno == EINTR);
  ssize_t count = ready > 0 ? read(w->wake_in, &byte, 1) : -1;
  if (ready > 0 && (count == 0 || (count == 1 && byte != WIRE_WAKE_DONE) ||
                    (count < 0 && errno != EAGAIN && errno != EINTR))) {
    wine_lost(h, w, frames, out);
    return;
  }
  if (count != 1) {
    w->pending = true;
    atomic_store(&w->stalled_since, wine_now_ms());
    wine_silence(h, frames, out);
    return;
  }
  bool ok = w->shared->status == WIRE_BLOCK_OK;
  if (ok)
    for (unsigned c = 0; c < h->channels; ++c)
      memcpy(out[c], w->shared->out[c], frames * sizeof(float));
  else
    wine_silence(h, frames, out);
  host_processing_result(h, ok, frames);
}

// --- main thread ------------------------------------------------------------

// Give the Windows audio thread the scheduling of the thread that waits for
// it: PipeWire's data thread, or the worker of the plugin's own block. Lower
// and that thread, real-time itself, would wait on one that is not; higher
// and plugin code would outrank PipeWire's own threads. A Windows program
// cannot ask for this itself, so it is set from here, which the kernel
// allows between two processes of one user within the real-time limit they
// share.
static void wine_schedule(Wine *w) {
  w->scheduled = true;
  int policy = atomic_load(&w->waiter_policy);
  int kind = policy & ~SCHED_RESET_ON_FORK;
  if (kind != SCHED_FIFO && kind != SCHED_RR)
    return;
  struct sched_param param = {.sched_priority = atomic_load(&w->waiter_priority)};
  if (!w->audio_tid)
    fputs("note: the Windows plugin's audio thread was not found and runs "
          "without real-time priority\n",
          stderr);
  else if (sched_setscheduler(w->audio_tid, kind | SCHED_RESET_ON_FORK, &param) != 0)
    fprintf(stderr,
            "note: the Windows plugin's audio thread runs without real-time "
            "priority: %s\n",
            strerror(errno));
  else
    fprintf(stderr,
            "trace: the Windows plugin's audio thread runs at real-time "
            "priority %d\n",
            param.sched_priority);
}

static void wine_main_thread(Host *h) {
  Wine *w = h->impl;
  wine_drain_log(w);
  if (w->failed)
    return;
  if (!w->scheduled && atomic_load(&w->waiter_known))
    wine_schedule(w);
  wine_take_values(h, w);
  // A Windows host that died while no audio was running has no pipe to
  // close on anybody, so it is looked at here as well.
  if (atomic_load(&w->dead) || wine_gone(w)) {
    char ending[96];
    atomic_store(&w->dead, true);
    if (!w->dead_at)
      w->dead_at = wine_now_ms();
    if (!wine_reap(w) && wine_now_ms() - w->dead_at < WINE_ENDING_MS)
      return;
    w->failed = true;
    host_fail(h, wine_ending(w, ending, sizeof(ending), 0));
    return;
  }
  int64_t since = atomic_load(&w->stalled_since);
  if (since && wine_now_ms() - since >= WINE_STALL_MS) {
    char why[96];
    snprintf(why, sizeof(why),
             "the Windows plugin has been inside one audio callback for %d s",
             WINE_STALL_MS / 1000);
    w->failed = true;
    host_fail(h, why);
  }
}

// The figure the Windows side last wrote, which it does each time the
// plugin is activated there.
static uint32_t wine_latency(Host *h) {
  Wine *w = h->impl;
  if (!w->activated)
    return UINT32_MAX;
  return atomic_load(&w->shared->latency);
}

// --- scanning ---------------------------------------------------------------

// Pass on what the Windows host has written of its description so far.
// False once every writer has closed the pipe.
static bool wine_pass_description(int from) {
  char buffer[16384];
  for (;;) {
    ssize_t count = read(from, buffer, sizeof(buffer));
    if (count > 0)
      fwrite(buffer, 1, (size_t)count, stdout);
    else if (count == 0 || errno != EINTR)
      return count != 0;
  }
}

// `wine scan-clap FILE` and `wine scan-vst3 BUNDLE`: what `scan-clap` and
// `scan-vst3` are for a native plugin. The Windows host is started with
// `scan` and handed the module on its standard input. It describes the
// module with the scanner this helper has for a native plugin of the format,
// and what it writes is passed on as it comes: the description from its
// standard output, the phase lines and the plugin's own from its standard
// error. The description names the plugin by `path` as it was given.
//
// A scan runs plugin code, so it is waited for with a limit. The pipe the
// request went down stays open meanwhile, and the Windows host ends itself
// when it closes. That is how a scan is given up here, and it is what
// happens by itself when this process is killed. The result is 0 only when
// the Windows host finished and said the description is whole.
int wine_scan(const char *format, const char *path) {
  static Wine scan;
  static WireScan request;
  Wine *w = &scan;
  w->wake_out = w->wake_in = w->request = w->reply = w->log = -1;
  request.magic = WIRE_MAGIC;
  request.version = WIRE_VERSION;
  request.format = !strcmp(format, "clap") ? WIRE_FORMAT_CLAP : WIRE_FORMAT_VST3;
  char module[PATH_MAX];
  if (!realpath(path, module)) {
    fprintf(stderr, "cannot find %s: %s\n", path, strerror(errno));
    return 1;
  }
  if (request.format == WIRE_FORMAT_VST3 && !wine_vst3_module(module, sizeof(module))) {
    fprintf(stderr, "no 64-bit Windows plugin module in %s\n", path);
    return 1;
  }
  if (strlen(module) >= sizeof(request.module) || strlen(path) >= sizeof(request.name)) {
    fputs("the plugin's path is too long to carry\n", stderr);
    return 1;
  }
  strcpy(request.module, module);
  strcpy(request.name, path);
  // A write to a Windows host that has just died must come back as an
  // error, not end this process.
  signal(SIGPIPE, SIG_IGN);
  if (!wine_spawn(w, "scan"))
    return 1;
  // Readable once the child has exited, so its end is seen at once. On a
  // kernel without it the poll below runs to its timeout.
  int exited = (int)syscall(SYS_pidfd_open, w->child, 0);
  int description = w->wake_in, log = w->log;
  size_t sent = 0;
  int64_t deadline = wine_now_ms() + WINE_SCAN_MS;
  bool ended;
  while (!(ended = wine_reap(w)) && wine_now_ms() < deadline) {
    struct pollfd fds[4] = {{description, POLLIN, 0},
                            {log, POLLIN, 0},
                            {sent < sizeof(request) ? w->wake_out : -1, POLLOUT, 0},
                            {exited, POLLIN, 0}};
    if (poll(fds, 4, 100) <= 0)
      continue;
    if (fds[2].revents) {
      ssize_t count = write(w->wake_out, (const char *)&request + sent,
                            sizeof(request) - sent);
      if (count > 0)
        sent += (size_t)count;
      else if (errno != EAGAIN && errno != EINTR)
        sent = sizeof(request);  // nobody is reading: the child's end follows
    }
    // A pipe every writer has closed is no longer listened to, or the loop
    // would spin on its hang-up until the child can be waited for.
    if (fds[0].revents && !wine_pass_description(description))
      description = -1;
    wine_drain_log(w);
    if ((fds[1].revents & (POLLHUP | POLLERR)) && !(fds[1].revents & POLLIN))
      log = -1;
  }
  if (exited >= 0)
    close(exited);
  if (!ended) {
    // Time is up. Closing the pipe tells the Windows host to end, and one
    // that does not hear it is killed.
    close(w->wake_out);
    w->wake_out = -1;
    wine_stop_child(w);
  }
  // What it wrote before it ended.
  wine_pass_description(w->wake_in);
  wine_drain_log(w);
  if (w->log_size)
    fprintf(stderr, "%.*s\n", (int)w->log_size, w->log_line);
  int *fds[] = {&w->wake_out, &w->wake_in, &w->log};
  for (size_t i = 0; i < sizeof(fds) / sizeof(fds[0]); ++i)
    if (*fds[i] >= 0)
      close(*fds[i]);
  bool whole = ended && WIFEXITED(w->wait_status) && WEXITSTATUS(w->wait_status) == 0;
  char ending[96];
  if (!ended)
    fprintf(stderr, "the Windows plugin did not finish its scan in %d s\n",
            WINE_SCAN_MS / 1000);
  else if (!whole)
    fprintf(stderr, "the scan did not finish: %s\n",
            wine_ending(w, ending, sizeof(ending), 0));
  return fflush(stdout) == 0 && !ferror(stdout) && whole ? 0 : 1;
}

const Backend wine_backend = {
    .name = "Windows plugin",
    .argument_count = 3,  // FORMAT FILE ID, the last two as that format's own
    .load = wine_load,
    .activate = wine_activate,
    .deactivate = wine_deactivate,
    .process = wine_process,
    .main_thread = wine_main_thread,
    .unload = wine_unload,
    .latency = wine_latency,
};
