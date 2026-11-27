// SPDX-License-Identifier: GPL-3.0-only
// A stand-in for RTKit on a private bus, for the Windows backend's RTKit
// check (winhost-rtkit.py). It owns org.freedesktop.RealtimeKit1 on the bus
// at ADDRESS, never the system's, and answers what the helper asks:
//
// - the properties MaxRealtimePriority, MinNiceLevel and RTTimeUSecMax, the
//   first and last as given on the command line;
// - MakeThreadRealtimeWithPID(pid, tid, priority), checked as RTKit checks
//   it: the thread belongs to the process, the priority is within the
//   highest, and the process's hard real-time CPU limit is set and within
//   RTTimeUSecMax. Each call is written to RECORD as one line,
//
//     PID TID PRIORITY RTTIME-HARD
//
//   before it is answered. With MODE grant a call that passes the checks is
//   answered with success; with MODE refuse every call is refused, as polkit
//   refuses an inactive session.
//
// It changes no thread's scheduling. The kernel weighs a change against the
// target process's own real-time limit, which the check sets to 0, so only a
// privileged RTKit could. It prints `ready` once it owns the name and runs
// until it is killed or the bus goes away.
// Usage: winhost-rtkit ADDRESS MODE RECORD MAX-PRIORITY RTTIME-USEC-MAX
#define _GNU_SOURCE
#include <dbus/dbus.h>
#include <limits.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

#define NAME "org.freedesktop.RealtimeKit1"
#define PATH "/org/freedesktop/RealtimeKit1"

static const char *record;
static bool refuse;
static dbus_int32_t highest;
static dbus_int64_t rttime;

static void reply_error(DBusConnection *bus, DBusMessage *call, const char *name,
                        const char *text) {
  DBusMessage *error = dbus_message_new_error(call, name, text);
  if (error) {
    dbus_connection_send(bus, error, NULL);
    dbus_message_unref(error);
  }
}

static void reply_variant(DBusConnection *bus, DBusMessage *call, int type, const void *value) {
  DBusMessage *reply = dbus_message_new_method_return(call);
  DBusMessageIter outer, inner;
  char signature[2] = {(char)type, 0};
  dbus_message_iter_init_append(reply, &outer);
  dbus_message_iter_open_container(&outer, DBUS_TYPE_VARIANT, signature, &inner);
  dbus_message_iter_append_basic(&inner, type, value);
  dbus_message_iter_close_container(&outer, &inner);
  dbus_connection_send(bus, reply, NULL);
  dbus_message_unref(reply);
}

static void get_property(DBusConnection *bus, DBusMessage *call) {
  const char *interface = NULL, *property = NULL;
  dbus_int32_t nice = -15;
  if (!dbus_message_get_args(call, NULL, DBUS_TYPE_STRING, &interface, DBUS_TYPE_STRING,
                             &property, DBUS_TYPE_INVALID) ||
      strcmp(interface, NAME))
    reply_error(bus, call, DBUS_ERROR_INVALID_ARGS, "no such interface");
  else if (!strcmp(property, "MaxRealtimePriority"))
    reply_variant(bus, call, DBUS_TYPE_INT32, &highest);
  else if (!strcmp(property, "MinNiceLevel"))
    reply_variant(bus, call, DBUS_TYPE_INT32, &nice);
  else if (!strcmp(property, "RTTimeUSecMax"))
    reply_variant(bus, call, DBUS_TYPE_INT64, &rttime);
  else
    reply_error(bus, call, DBUS_ERROR_UNKNOWN_PROPERTY, "no such property");
}

// The hard real-time CPU limit of `pid` in microseconds, -1 when unlimited
// and -2 when it cannot be read.
static long long rttime_hard(unsigned long long pid) {
  char path[64], line[256];
  snprintf(path, sizeof(path), "/proc/%llu/limits", pid);
  FILE *file = fopen(path, "r");
  if (!file)
    return -2;
  long long found = -2;
  while (fgets(line, sizeof(line), file)) {
    char soft[32], hard[32];
    if (strncmp(line, "Max realtime timeout", 20) ||
        sscanf(line + 20, "%31s %31s", soft, hard) != 2)
      continue;
    found = strcmp(hard, "unlimited") ? strtoll(hard, NULL, 10) : -1;
  }
  fclose(file);
  return found;
}

static void make_realtime(DBusConnection *bus, DBusMessage *call) {
  dbus_uint64_t pid = 0, tid = 0;
  dbus_uint32_t priority = 0;
  if (!dbus_message_get_args(call, NULL, DBUS_TYPE_UINT64, &pid, DBUS_TYPE_UINT64, &tid,
                             DBUS_TYPE_UINT32, &priority, DBUS_TYPE_INVALID)) {
    reply_error(bus, call, DBUS_ERROR_INVALID_ARGS, "expected (tt u)");
    return;
  }
  long long hard = rttime_hard(pid);
  FILE *file = fopen(record, "a");
  if (file) {
    fprintf(file, "%llu %llu %u %lld\n", (unsigned long long)pid, (unsigned long long)tid,
            priority, hard);
    fclose(file);
  }
  char task[96];
  struct stat status;
  snprintf(task, sizeof(task), "/proc/%llu/task/%llu", (unsigned long long)pid,
           (unsigned long long)tid);
  if (refuse)
    reply_error(bus, call, DBUS_ERROR_ACCESS_DENIED, "Operation not permitted");
  else if (stat(task, &status) != 0)
    reply_error(bus, call, DBUS_ERROR_INVALID_ARGS, "no such thread in that process");
  else if ((dbus_int32_t)priority > highest || priority < 1)
    reply_error(bus, call, DBUS_ERROR_INVALID_ARGS, "priority out of range");
  else if (hard < 0 || hard > rttime)
    reply_error(bus, call, DBUS_ERROR_ACCESS_DENIED, "the process's RLIMIT_RTTIME is not set");
  else {
    DBusMessage *reply = dbus_message_new_method_return(call);
    dbus_connection_send(bus, reply, NULL);
    dbus_message_unref(reply);
  }
}

int main(int argc, char **argv) {
  if (argc != 6 || (strcmp(argv[2], "grant") && strcmp(argv[2], "refuse"))) {
    fputs("usage: winhost-rtkit ADDRESS grant|refuse RECORD MAX-PRIORITY RTTIME-USEC-MAX\n",
          stderr);
    return 2;
  }
  refuse = !strcmp(argv[2], "refuse");
  record = argv[3];
  highest = (dbus_int32_t)strtol(argv[4], NULL, 10);
  rttime = strtoll(argv[5], NULL, 10);
  DBusError error;
  dbus_error_init(&error);
  // The private bus by its address, so nothing here can reach the system's.
  DBusConnection *bus = dbus_connection_open_private(argv[1], &error);
  if (!bus || !dbus_bus_register(bus, &error)) {
    fprintf(stderr, "winhost-rtkit: %s\n", error.message);
    return 1;
  }
  if (dbus_bus_request_name(bus, NAME, DBUS_NAME_FLAG_DO_NOT_QUEUE, &error) !=
      DBUS_REQUEST_NAME_REPLY_PRIMARY_OWNER) {
    fprintf(stderr, "winhost-rtkit: the name was not given: %s\n",
            dbus_error_is_set(&error) ? error.message : "taken");
    return 1;
  }
  puts("ready");
  fflush(stdout);
  while (dbus_connection_read_write(bus, -1)) {
    DBusMessage *call;
    while ((call = dbus_connection_pop_message(bus))) {
      if (dbus_message_get_type(call) == DBUS_MESSAGE_TYPE_METHOD_CALL) {
        const char *path = dbus_message_get_path(call);
        if (!path || strcmp(path, PATH))
          reply_error(bus, call, DBUS_ERROR_UNKNOWN_OBJECT, "no such object");
        else if (dbus_message_is_method_call(call, DBUS_INTERFACE_PROPERTIES, "Get"))
          get_property(bus, call);
        else if (dbus_message_is_method_call(call, NAME, "MakeThreadRealtimeWithPID"))
          make_realtime(bus, call);
        else
          reply_error(bus, call, DBUS_ERROR_UNKNOWN_METHOD, "no such method");
      }
      dbus_message_unref(call);
    }
  }
  return 0;
}
