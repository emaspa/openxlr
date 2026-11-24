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
// This step carries CLAP audio and latency. Parameters, editors and VST3
// come with later ones.
#define _GNU_SOURCE  // pipe2, realpath, SCHED_RESET_ON_FORK
#include "host.h"
#include "winhost/wire.h"

#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <poll.h>
#include <sched.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/prctl.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

_Static_assert((int)WIRE_MAX_FRAMES == (int)MAX_FRAMES,
               "wire.h carries the host's largest block");
_Static_assert((int)WIRE_MAX_CHANNELS == (int)MAX_CHANNELS,
               "wire.h carries the host's channels");

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

extern char **environ;

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

// Start Wine with the Windows host. It inherits this process's environment,
// which the daemon has already given the Wine prefix and the overrides it
// wants, less WAYLAND_DISPLAY: an editor has to be an X11 window, which is
// what the frame it will sit in is.
static bool wine_spawn(Wine *w, const char *directory) {
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
  char **environment = calloc(count + 1, sizeof(char *));
  if (!environment)
    return false;
  size_t kept = 0;
  for (size_t i = 0; i < count; ++i)
    if (strncmp(environ[i], "WAYLAND_DISPLAY=", 16) != 0)
      environment[kept++] = environ[i];
  char *arguments[] = {wine, winhost, (char *)directory, NULL};

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

static bool wine_load(Host *h, char **arguments) {
  Wine *w = calloc(1, sizeof(Wine));
  if (!w)
    return false;
  h->impl = w;
  w->h = h;
  w->wake_out = w->wake_in = w->request = w->reply = w->log = -1;
  if (strcmp(arguments[0], "clap") != 0) {
    fprintf(stderr, "the Windows plugin host carries CLAP only, not %s\n",
            arguments[0]);
    return false;
  }
  char module[PATH_MAX];
  if (!realpath(arguments[1], module)) {
    fprintf(stderr, "cannot find %s: %s\n", arguments[1], strerror(errno));
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
  WireRequest load = {.type = WIRE_LOAD, .format = WIRE_FORMAT_CLAP,
                      .channels = h->channels};
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
  free(w);
  h->impl = NULL;
}

// --- activation -------------------------------------------------------------

static bool wine_activate(Host *h) {
  Wine *w = h->impl;
  uint32_t most = host_max_block(h);
  WireRequest request = {.type = WIRE_ACTIVATE, .rate = h->rate,
                         .min_frames = h->own_block ? most : 1,
                         .max_frames = most};
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
// block can start any more and this thread may use the audio wake. The
// Windows audio thread is first asked to stop processing, which is where
// CLAP wants that call made, then the plugin is deactivated. A Windows host
// that answers neither is left for unload to end.
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
  char wake = WIRE_WAKE_STOP;
  if ((w->pending && !wine_await_done(w, 1000)) ||
      write(w->wake_out, &wake, 1) != 1 || !wine_await_done(w, 1000)) {
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

// One block: copy the input into the shared file, wake the Windows audio
// thread with one byte, wait for the byte that answers, copy the output
// back. The wait is bounded at twice the block's own length and at least
// WINE_BLOCK_MS, so a block that comes late by less than that is heard
// late, as a native plugin's would be, and one that takes longer plays as
// silence. Its answer is then still owed: until it arrives every block
// plays silence without asking for another, and the shared file is left
// alone, since the Windows side may still be reading it.
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

const Backend wine_backend = {
    .name = "Windows plugin",
    .argument_count = 3,  // FORMAT FILE ID
    .load = wine_load,
    .activate = wine_activate,
    .deactivate = wine_deactivate,
    .process = wine_process,
    .main_thread = wine_main_thread,
    .unload = wine_unload,
    .latency = wine_latency,
};
