// SPDX-License-Identifier: GPL-3.0-only
// The Windows side of the disk identity (disk-hook.h). This is the hook of
// packaging/yabridge/disk-identity.patch, the companion's, in C: the same
// loader notification, the same four imports redirected in the same modules,
// and the same one answer replaced, so a plugin is told what the companion
// tells it. The descriptor comes from the Linux side (../disk-identity.c)
// instead of being read here.
//
// One thing differs, on purpose. A plugin may ask once per audio block, so
// once Wine's empty answer has been replaced the repeats are answered from
// the copy, as the patch does, and here that path takes no lock either: the
// open PhysicalDrive0 handles are a fixed table of atomic slots rather than
// a set behind a mutex. A repeat costs a scan of the slots that were ever
// used and a copy, with no allocation and no system call.
#include "disk-hook.h"

#include <stdatomic.h>
#include <stdio.h>
#include <string.h>

#include <windows.h>
#include <winioctl.h>
#include <winternl.h>

// mingw's headers do not declare the loader notification.
typedef struct {
  ULONG Flags;
  const UNICODE_STRING *FullDllName;
  const UNICODE_STRING *BaseDllName;
  void *DllBase;
  ULONG SizeOfImage;
} LdrDllLoaded;
typedef union {
  LdrDllLoaded Loaded;
  LdrDllLoaded Unloaded;
} LdrDllNotificationData;
typedef void(CALLBACK *LdrDllNotification)(ULONG, const LdrDllNotificationData *, void *);
enum { LDR_DLL_NOTIFICATION_REASON_LOADED = 1 };

typedef BOOL(WINAPI *DeviceIoControlFn)(HANDLE, DWORD, LPVOID, DWORD, LPVOID,
                                        DWORD, LPDWORD, LPOVERLAPPED);
typedef HANDLE(WINAPI *CreateFileAFn)(LPCSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES,
                                      DWORD, DWORD, HANDLE);
typedef HANDLE(WINAPI *CreateFileWFn)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES,
                                      DWORD, DWORD, HANDLE);
typedef BOOL(WINAPI *CloseHandleFn)(HANDLE);
typedef NTSTATUS(WINAPI *LdrRegisterFn)(ULONG, LdrDllNotification, void *, void **);

static DeviceIoControlFn real_device_io_control;
static CreateFileAFn real_create_file_a;
static CreateFileWFn real_create_file_w;
static CloseHandleFn real_close_handle;

static unsigned char descriptor[DISK_IDENTITY_SIZE];
static _Atomic bool wine_answer_replaced;

// The PhysicalDrive0 handles the plugin has open. A slot holds a handle or
// NULL, which no open handle is. `used` is how many slots from the first have
// ever held one, so a lookup reads only those. A plugin with more open at once
// than the table holds has the rest answered by Wine, and is told so once.
enum { DRIVE_HANDLES = 256 };
static _Atomic(HANDLE) drive_handles[DRIVE_HANDLES];
static _Atomic unsigned drive_handles_used;
static _Atomic bool drive_handles_full;

static bool tracked(HANDLE handle) {
  if (!handle)
    return false;
  unsigned used = atomic_load(&drive_handles_used);
  for (unsigned i = 0; i < used; ++i)
    if (atomic_load(&drive_handles[i]) == handle)
      return true;
  return false;
}

static void remember(HANDLE handle) {
  if (handle == INVALID_HANDLE_VALUE || !handle)
    return;
  for (unsigned i = 0; i < DRIVE_HANDLES; ++i) {
    HANDLE none = NULL;
    if (!atomic_compare_exchange_strong(&drive_handles[i], &none, handle))
      continue;
    unsigned used = atomic_load(&drive_handles_used);
    while (used < i + 1 &&
           !atomic_compare_exchange_weak(&drive_handles_used, &used, i + 1)) {
    }
    return;
  }
  if (!atomic_exchange(&drive_handles_full, true))
    fprintf(stderr,
            "trace: disk identity: the plugin has more than %d PhysicalDrive0 "
            "handles open; Wine answers on the others\n",
            DRIVE_HANDLES);
}

static void forget(HANDLE handle) {
  if (!handle)
    return;
  unsigned used = atomic_load(&drive_handles_used);
  for (unsigned i = 0; i < used; ++i) {
    HANDLE held = handle;
    atomic_compare_exchange_strong(&drive_handles[i], &held, NULL);
  }
}

// std::tolower in the C locale: only A to Z change.
static char lower(char c) { return c >= 'A' && c <= 'Z' ? (char)(c - 'A' + 'a') : c; }

static bool same_lowered(const char *name, const char *lowered) {
  for (; *name && *lowered; ++name, ++lowered)
    if (lower(*name) != *lowered)
      return false;
  return !*name && !*lowered;
}

static bool is_physical_drive0(const char *name) {
  return same_lowered(name, "\\\\.\\physicaldrive0") ||
         same_lowered(name, "\\\\?\\physicaldrive0");
}

static HANDLE WINAPI hooked_create_file_a(LPCSTR name, DWORD access, DWORD share,
                                          LPSECURITY_ATTRIBUTES security,
                                          DWORD disposition, DWORD flags,
                                          HANDLE template_file) {
  HANDLE handle = real_create_file_a(name, access, share, security, disposition,
                                     flags, template_file);
  if (name && is_physical_drive0(name))
    remember(handle);
  return handle;
}

static HANDLE WINAPI hooked_create_file_w(LPCWSTR name, DWORD access, DWORD share,
                                          LPSECURITY_ATTRIBUTES security,
                                          DWORD disposition, DWORD flags,
                                          HANDLE template_file) {
  HANDLE handle = real_create_file_w(name, access, share, security, disposition,
                                     flags, template_file);
  if (name) {
    // The first 64 characters, anything outside ASCII as a question mark.
    char narrow[65];
    size_t size = 0;
    for (const WCHAR *c = name; *c && size < 64; ++c)
      narrow[size++] = *c < 128 ? (char)*c : '?';
    narrow[size] = 0;
    if (is_physical_drive0(narrow))
      remember(handle);
  }
  return handle;
}

static BOOL WINAPI hooked_close_handle(HANDLE handle) {
  forget(handle);
  return real_close_handle(handle);
}

static DWORD copy_descriptor(LPVOID out, DWORD out_size, LPDWORD returned) {
  DWORD copied = out_size < DISK_IDENTITY_SIZE ? out_size : DISK_IDENTITY_SIZE;
  memcpy(out, descriptor, copied);
  if (returned)
    *returned = copied;
  return copied;
}

static BOOL WINAPI hooked_device_io_control(HANDLE handle, DWORD code, LPVOID in,
                                            DWORD in_size, LPVOID out,
                                            DWORD out_size, LPDWORD returned,
                                            LPOVERLAPPED overlapped) {
  // Once Wine's empty answer has been seen and replaced, later queries on a
  // drive handle get the same answer without the round trip to Wine's
  // mount manager.
  if (atomic_load_explicit(&wine_answer_replaced, memory_order_acquire) &&
      code == IOCTL_STORAGE_QUERY_PROPERTY && !overlapped && in &&
      in_size >= sizeof(STORAGE_PROPERTY_QUERY) && out &&
      out_size >= sizeof(STORAGE_DESCRIPTOR_HEADER)) {
    const STORAGE_PROPERTY_QUERY *query = in;
    if (tracked(handle) && query->PropertyId == StorageDeviceProperty &&
        query->QueryType == PropertyStandardQuery) {
      copy_descriptor(out, out_size, returned);
      return TRUE;
    }
  }

  BOOL ok = real_device_io_control(handle, code, in, in_size, out, out_size,
                                   returned, overlapped);
  if (!ok || code != IOCTL_STORAGE_QUERY_PROPERTY || overlapped || !in ||
      in_size < sizeof(STORAGE_PROPERTY_QUERY) || !out ||
      out_size < sizeof(STORAGE_DESCRIPTOR_HEADER))
    return ok;
  const STORAGE_PROPERTY_QUERY *query = in;
  if (query->PropertyId != StorageDeviceProperty ||
      query->QueryType != PropertyStandardQuery || !tracked(handle))
    return ok;

  // Only replace Wine's empty placeholder: a descriptor with no strings
  const STORAGE_DESCRIPTOR_HEADER *header = out;
  if (header->Size != sizeof(STORAGE_DEVICE_DESCRIPTOR))
    return ok;
  if (out_size >= sizeof(STORAGE_DEVICE_DESCRIPTOR)) {
    const STORAGE_DEVICE_DESCRIPTOR *found = out;
    if (found->VendorIdOffset || found->ProductIdOffset || found->SerialNumberOffset)
      return ok;
  }
  copy_descriptor(out, out_size, returned);
  atomic_store_explicit(&wine_answer_replaced, true, memory_order_release);
  return TRUE;
}

// --- the imports ------------------------------------------------------------

static bool starts_lowered(const char *name, const char *lowered) {
  for (; *lowered; ++name, ++lowered)
    if (lower(*name) != *lowered)
      return false;
  return true;
}

static bool is_kernel_dll(const char *name) {
  return same_lowered(name, "kernel32.dll") || same_lowered(name, "kernelbase.dll") ||
         starts_lowered(name, "api-ms-win-core-io-") ||
         starts_lowered(name, "api-ms-win-core-file-") ||
         starts_lowered(name, "api-ms-win-core-handle-");
}

static void *replacement_for(const char *function) {
  if (!strcmp(function, "DeviceIoControl"))
    return (void *)&hooked_device_io_control;
  if (!strcmp(function, "CreateFileA"))
    return (void *)&hooked_create_file_a;
  if (!strcmp(function, "CreateFileW"))
    return (void *)&hooked_create_file_w;
  if (!strcmp(function, "CloseHandle"))
    return (void *)&hooked_close_handle;
  return NULL;
}

static void patch_imports(void *base) {
  unsigned char *image = base;
  const IMAGE_DOS_HEADER *dos = base;
  if (dos->e_magic != IMAGE_DOS_SIGNATURE)
    return;
  const IMAGE_NT_HEADERS *nt = (const IMAGE_NT_HEADERS *)(image + dos->e_lfanew);
  const IMAGE_DATA_DIRECTORY *directory =
      &nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
  if (!directory->VirtualAddress)
    return;
  for (const IMAGE_IMPORT_DESCRIPTOR *import =
           (const IMAGE_IMPORT_DESCRIPTOR *)(image + directory->VirtualAddress);
       import->Name; ++import) {
    if (!is_kernel_dll((const char *)(image + import->Name)) || !import->OriginalFirstThunk)
      continue;
    const IMAGE_THUNK_DATA *names =
        (const IMAGE_THUNK_DATA *)(image + import->OriginalFirstThunk);
    IMAGE_THUNK_DATA *addresses = (IMAGE_THUNK_DATA *)(image + import->FirstThunk);
    for (; names->u1.AddressOfData; ++names, ++addresses) {
      if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal))
        continue;
      const IMAGE_IMPORT_BY_NAME *by_name =
          (const IMAGE_IMPORT_BY_NAME *)(image + names->u1.AddressOfData);
      void *replacement = replacement_for((const char *)by_name->Name);
      if (!replacement)
        continue;
      DWORD old_protection;
      if (VirtualProtect(&addresses->u1.Function, sizeof(void *), PAGE_READWRITE,
                         &old_protection)) {
        addresses->u1.Function = (ULONG_PTR)replacement;
        VirtualProtect(&addresses->u1.Function, sizeof(void *), old_protection,
                       &old_protection);
      }
    }
  }
}

static bool in_windows_directory(const UNICODE_STRING *name) {
  static const WCHAR prefix[] = L"c:\\windows\\";
  const size_t prefix_length = sizeof(prefix) / sizeof(WCHAR) - 1;
  if (!name || !name->Buffer || name->Length / sizeof(WCHAR) < prefix_length)
    return false;
  for (size_t i = 0; i < prefix_length; ++i) {
    WCHAR c = name->Buffer[i];
    if (c >= L'A' && c <= L'Z')
      c = (WCHAR)(c - L'A' + L'a');
    if (c != prefix[i])
      return false;
  }
  return true;
}

static void CALLBACK on_dll_notification(ULONG reason, const LdrDllNotificationData *data,
                                         void *context) {
  (void)context;
  if (reason != LDR_DLL_NOTIFICATION_REASON_LOADED || !data ||
      in_windows_directory(data->Loaded.FullDllName))
    return;
  patch_imports(data->Loaded.DllBase);
}

void disk_hook_install(const unsigned char given[DISK_IDENTITY_SIZE]) {
  memcpy(descriptor, given, DISK_IDENTITY_SIZE);
  HMODULE kernel32 = GetModuleHandleW(L"kernel32.dll");
  HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
  real_device_io_control =
      (DeviceIoControlFn)(void *)GetProcAddress(kernel32, "DeviceIoControl");
  real_create_file_a = (CreateFileAFn)(void *)GetProcAddress(kernel32, "CreateFileA");
  real_create_file_w = (CreateFileWFn)(void *)GetProcAddress(kernel32, "CreateFileW");
  real_close_handle = (CloseHandleFn)(void *)GetProcAddress(kernel32, "CloseHandle");
  LdrRegisterFn register_notification =
      (LdrRegisterFn)(void *)GetProcAddress(ntdll, "LdrRegisterDllNotification");
  if (!real_device_io_control || !real_create_file_a || !real_create_file_w ||
      !real_close_handle || !register_notification) {
    fputs("trace: disk identity: missing a Wine entry point; leaving Wine's answer\n",
          stderr);
    return;
  }
  void *cookie = NULL;
  if (register_notification(0, on_dll_notification, NULL, &cookie) != 0)
    fputs("trace: disk identity: the loader refused the notification; leaving Wine's "
          "answer\n",
          stderr);
}
