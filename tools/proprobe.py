#!/usr/bin/env python3
"""Wave XLR Pro (0fd9:00b4) vendor-protocol probe.

Read/write the paged property-bank blocks over the vendor interface, matching
the transport observed in wavexlrpro.pcapng:
  read : bmRequestType=0xC1, bRequest=0x01, wValue=block, wIndex=0x0103
  write: bmRequestType=0x41, bRequest=0x01, wValue=block, wIndex=0x0103, data
Uses raw libusb via ctypes, exactly like the openwave fork's device.py.
"""
import ctypes, ctypes.util, sys

VID, PID = 0x0FD9, 0x00B4
VINDEX = 0x0103          # Pro uses 0x0103 (MK2 uses 0x0203); low byte 3 = interface 3
VREQ = 0x01
RT_READ, RT_WRITE = 0xC1, 0x41

# Block sizes observed in the capture (bytes)
BLOCKS = {0x0001: 108, 0x0002: 150, 0x0003: 12, 0x0004: 80,
          0x0005: 8, 0x0006: 29, 0x0008: 96}

_lib = ctypes.CDLL(ctypes.util.find_library("usb-1.0") or "libusb-1.0.so.0")
_lib.libusb_init.argtypes = [ctypes.POINTER(ctypes.c_void_p)]
_lib.libusb_open_device_with_vid_pid.argtypes = [ctypes.c_void_p, ctypes.c_uint16, ctypes.c_uint16]
_lib.libusb_open_device_with_vid_pid.restype = ctypes.c_void_p
_lib.libusb_close.argtypes = [ctypes.c_void_p]
_lib.libusb_control_transfer.argtypes = [
    ctypes.c_void_p, ctypes.c_uint8, ctypes.c_uint8, ctypes.c_uint16, ctypes.c_uint16,
    ctypes.POINTER(ctypes.c_ubyte), ctypes.c_uint16, ctypes.c_uint]
_lib.libusb_control_transfer.restype = ctypes.c_int
_lib.libusb_strerror.argtypes = [ctypes.c_int]
_lib.libusb_strerror.restype = ctypes.c_char_p

_ctx = ctypes.c_void_p()
_lib.libusb_init(ctypes.byref(_ctx))


def _open():
    h = _lib.libusb_open_device_with_vid_pid(_ctx, VID, PID)
    if not h:
        sys.exit("open failed: device not found or no permission (need udev rule / root)")
    return h


def vread(h, block, length):
    buf = (ctypes.c_ubyte * length)()
    n = _lib.libusb_control_transfer(h, RT_READ, VREQ, block, VINDEX, buf, length, 1000)
    if n < 0:
        raise RuntimeError(f"read block {block:#06x} failed: {_lib.libusb_strerror(n).decode()}")
    return bytearray(buf[:n])


def vwrite(h, block, data):
    data = bytes(data)
    buf = (ctypes.c_ubyte * len(data))(*data)
    n = _lib.libusb_control_transfer(h, RT_WRITE, VREQ, block, VINDEX, buf, len(data), 1000)
    if n < 0:
        raise RuntimeError(f"write block {block:#06x} failed: {_lib.libusb_strerror(n).decode()}")
    return n


def hexdump(b):
    return ' '.join(f"{x:02x}" for x in b)


def cmd_dump(h):
    for blk, ln in BLOCKS.items():
        try:
            d = vread(h, blk, ln)
            print(f"block {blk:#06x} ({len(d):3}B): {hexdump(d)}")
        except RuntimeError as e:
            print(f"block {blk:#06x}: {e}")


def cmd_read(h, blk):
    d = vread(h, blk, BLOCKS.get(blk, 128))
    print(f"block {blk:#06x} ({len(d)}B): {hexdump(d)}")


def cmd_setbyte(h, blk, off, val):
    d = vread(h, blk, BLOCKS[blk])
    old = d[off]
    d[off] = val
    vwrite(h, blk, d)
    back = vread(h, blk, BLOCKS[blk])
    print(f"block {blk:#06x} off{off}: {old:#04x} -> wrote {val:#04x} -> readback {back[off]:#04x}")


def cmd_setbit(h, blk, off, bit, on):
    d = vread(h, blk, BLOCKS[blk])
    old = d[off]
    if on:
        d[off] |= (1 << bit)
    else:
        d[off] &= ~(1 << bit)
    vwrite(h, blk, d)
    back = vread(h, blk, BLOCKS[blk])
    print(f"block {blk:#06x} off{off} bit{bit}={'1' if on else '0'}: "
          f"{old:#04x} -> {back[off]:#04x}")


def cmd_commit(h):
    # Block 0x0003 is write-only and stalls on a read, so setbyte cannot
    # reach it. Wave Link sends this twelve-byte payload after a config write;
    # without it the firmware rolls output selector and path writes back.
    commit = bytearray(BLOCKS[0x0003])
    commit[0] = 0x04
    vwrite(h, 0x0003, commit)
    print(f"block 0x0003 commit: {hexdump(commit)}")


def cmd_write(h, blk, text):
    # Writes a whole block from a hex dump, as `read` prints it, to restore a
    # saved state byte for byte.
    data = bytes.fromhex(text)
    if len(data) != BLOCKS[blk]:
        sys.exit(f"block {blk:#06x} is {BLOCKS[blk]} bytes, got {len(data)}")
    vwrite(h, blk, data)
    back = vread(h, blk, BLOCKS[blk])
    diff = [i for i in range(len(data)) if back[i] != data[i]]
    print(f"block {blk:#06x} written, readback "
          f"{'identical' if not diff else 'differs at ' + ', '.join(map(str, diff))}")


if __name__ == "__main__":
    h = _open()
    a = sys.argv[1:]
    if not a or a[0] == "dump":
        cmd_dump(h)
    elif a[0] == "read":
        cmd_read(h, int(a[1], 0))
    elif a[0] == "setbyte":
        cmd_setbyte(h, int(a[1], 0), int(a[2]), int(a[3], 0))
    elif a[0] == "setbit":
        cmd_setbit(h, int(a[1], 0), int(a[2]), int(a[3]), int(a[4]))
    elif a[0] == "commit":
        cmd_commit(h)
    elif a[0] == "write":
        cmd_write(h, int(a[1], 0), ''.join(a[2:]))
    else:
        print("usage: dump | read BLK | setbyte BLK OFF VAL | setbit BLK OFF BIT ON"
              " | commit | write BLK HEX")
    _lib.libusb_close(h)
