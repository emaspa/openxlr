#!/usr/bin/env python3
"""Write, or with --check compare, the microphone EQ reference magnitudes.

The table holds the magnitude response of OpenXLR's four-band microphone
EQ for a set of band settings at 48 kHz and 44.1 kHz, at 64 log-spaced
frequencies from 20 Hz to 20 kHz. It is computed here, in double precision,
straight from the formulas of Robert Bristow-Johnson's Audio EQ Cookbook
(https://www.w3.org/TR/audio-eq-cookbook/), so the C# curve maths
(EqResponse) and the native helper's `eq` backend are both held to a source
that is neither of them. Standard library only.

    python3 tools/eq-reference.py          # rewrite native/tests/eq-reference.txt
    python3 tools/eq-reference.py --check  # fail when the file differs
"""
import cmath
import math
import sys
from pathlib import Path

OUT = Path(__file__).resolve().parent.parent / "native" / "tests" / "eq-reference.txt"
RATES = (48000, 44100)
POINTS = 64


def snap_q(q):
    """Q on the grid the daemon snaps to: log10(Q) = -1 + 0.016 k."""
    k = math.floor((math.log10(q) + 1) / 0.016 + 0.5)
    return 10 ** (-1 + 0.016 * k)


def coefficients(kind, rate, frequency, gain, q):
    """Normalised (b0, b1, b2, a1, a2) of one band, as the cookbook writes them."""
    frequency = min(frequency, 0.49 * rate)
    if kind in ("lowShelf", "highShelf", "peak"):
        frequency = max(frequency, 2.0)
    w0 = 2 * math.pi * frequency / rate
    cos, sin = math.cos(w0), math.sin(w0)
    alpha = sin / (2 * q)
    a = 10 ** (gain / 40)
    if kind == "lowCut":
        b = ((1 + cos) / 2, -(1 + cos), (1 + cos) / 2)
        d = (1 + alpha, -2 * cos, 1 - alpha)
    elif kind == "highCut":
        b = ((1 - cos) / 2, 1 - cos, (1 - cos) / 2)
        d = (1 + alpha, -2 * cos, 1 - alpha)
    elif kind == "notch":
        b = (1, -2 * cos, 1)
        d = (1 + alpha, -2 * cos, 1 - alpha)
    elif kind == "peak":
        b = (1 + alpha * a, -2 * cos, 1 - alpha * a)
        d = (1 + alpha / a, -2 * cos, 1 - alpha / a)
    elif kind == "lowShelf":
        root = 2 * math.sqrt(a) * alpha
        b = (a * ((a + 1) - (a - 1) * cos + root), 2 * a * ((a - 1) - (a + 1) * cos),
             a * ((a + 1) - (a - 1) * cos - root))
        d = ((a + 1) + (a - 1) * cos + root, -2 * ((a - 1) + (a + 1) * cos), (a + 1) + (a - 1) * cos - root)
    elif kind == "highShelf":
        root = 2 * math.sqrt(a) * alpha
        b = (a * ((a + 1) + (a - 1) * cos + root), -2 * a * ((a - 1) + (a + 1) * cos),
             a * ((a + 1) + (a - 1) * cos - root))
        d = ((a + 1) - (a - 1) * cos + root, 2 * ((a - 1) - (a + 1) * cos), (a + 1) - (a - 1) * cos - root)
    else:
        raise ValueError(kind)
    return b[0] / d[0], b[1] / d[0], b[2] / d[0], d[1] / d[0], d[2] / d[0]


def magnitude_db(bands, rate, frequency):
    total = 0.0
    for enabled, kind, f0, gain, q in bands:
        if not enabled:
            continue
        b0, b1, b2, a1, a2 = coefficients(kind, rate, f0, gain, q)
        z = cmath.exp(-1j * 2 * math.pi * frequency / rate)
        h = (b0 + b1 * z + b2 * z * z) / (1 + a1 * z + a2 * z * z)
        total += 20 * math.log10(abs(h))
    return total


OFF = (0, "peak", 1000, 0.0, snap_q(0.705))


def single(kind, frequency, gain, q):
    return [(1, kind, frequency, gain, snap_q(q)), OFF, OFF, OFF]


SETTINGS = [
    single("lowCut", 36, 0, 0.705),
    single("lowCut", 120, 0, 4),
    single("lowCut", 80, 0, 0.5),
    single("lowCut", 20, 0, 0.1),
    single("lowCut", 1000, 0, 10),
    single("lowCut", 500, 0, 1),
    single("lowShelf", 100, 6, 0.5),
    single("lowShelf", 250, -9.25, 0.705),
    single("lowShelf", 60, 12, 2),
    single("lowShelf", 2000, -3, 1),
    single("lowShelf", 80, 20, 0.705),
    single("lowShelf", 400, -20, 1),
    single("peak", 1000, 9.25, 2),
    single("peak", 310, -12, 0.705),
    single("peak", 2172, 3.5, 4),
    single("peak", 60, 12, 10),
    single("peak", 8000, -6, 0.3),
    single("peak", 20000, 12, 1),
    # The ends of the gain range, at the bell the Wave XLR MK.2 was measured with.
    single("peak", 703, 20, 0.6),
    single("peak", 703, -20, 0.6),
    single("notch", 3000, 0, 10),
    single("notch", 60, 0, 2),
    single("notch", 12000, 0, 0.705),
    single("notch", 500, 0, 0.1),
    single("highShelf", 5000, -6, 3),
    single("highShelf", 8000, 12, 0.705),
    single("highShelf", 12000, -12, 0.1),
    single("highShelf", 3000, 4.75, 1),
    single("highShelf", 8000, -20, 0.705),
    single("highShelf", 6000, 20, 2),
    single("highCut", 15049, 0, 0.705),
    single("highCut", 5000, 0, 0.5),
    single("highCut", 10000, 0, 10),
    single("highCut", 2000, 0, 2),
    single("highCut", 19000, 0, 0.1),
    # Wave Link's defaults: band 4 is off.
    [(1, "lowCut", 36, 0.0, snap_q(0.705)), (1, "peak", 310, 0.0, snap_q(0.705)),
     (1, "peak", 2172, 0.0, snap_q(0.705)), (0, "highCut", 15049, 0.0, snap_q(0.705))],
    # Every band on and doing something.
    [(1, "lowShelf", 130, 9.25, snap_q(0.705)), (1, "peak", 792, -9.25, snap_q(1.5)),
     (1, "peak", 3193, 9.25, snap_q(3)), (1, "highShelf", 6238, -9.25, snap_q(0.705))],
    [(1, "lowCut", 90, 0.0, snap_q(0.705)), (1, "notch", 1200, 0.0, snap_q(5)),
     (1, "peak", 4500, 5.5, snap_q(0.9)), (1, "highCut", 12000, 0.0, snap_q(0.705))],
]


def frequencies():
    return [20 * 10 ** (3 * i / (POINTS - 1)) for i in range(POINTS)]


def render():
    lines = [
        "# OpenXLR microphone EQ reference magnitudes, from the RBJ Audio EQ Cookbook.",
        "# Generated by tools/eq-reference.py; do not edit by hand.",
        "# f: the frequencies in Hz.",
        "# r: a sample rate, four bands as 'enabled type frequency gain q', then the",
        "#    cascade's magnitude in dB at each frequency.",
        "f " + " ".join(repr(f) for f in frequencies()),
    ]
    for rate in RATES:
        for bands in SETTINGS:
            fields = [str(rate)]
            for enabled, kind, f0, gain, q in bands:
                fields += [str(enabled), kind, str(f0), repr(float(gain)), repr(q)]
            fields += [f"{magnitude_db(bands, rate, f):.9f}" for f in frequencies()]
            lines.append("r " + " ".join(fields))
    return "\n".join(lines) + "\n"


def main():
    text = render()
    if sys.argv[1:] == ["--check"]:
        if not OUT.exists() or OUT.read_text() != text:
            sys.exit(f"{OUT} is out of date; run python3 tools/eq-reference.py")
        return
    if sys.argv[1:]:
        sys.exit("usage: eq-reference.py [--check]")
    OUT.write_text(text)


if __name__ == "__main__":
    main()
