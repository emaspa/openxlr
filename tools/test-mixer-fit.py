#!/usr/bin/env python3
"""Check the detached mixer at multiple physical window sizes and display scales."""

import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess

ROOT = Path(__file__).resolve().parent.parent
PROJECT = "src/OpenXLR.Tests/OpenXLR.Tests.csproj"
SCALES = [100, 110, 125, 133, 150, 175, 200, 250]
SIZES = ["640x480", "800x960", "1024x600", "1273x500", "1366x768", "1920x720",
         "2560x1440", "3440x900", "3840x2160"]


def window_size(value):
    if not re.fullmatch(r"[1-9][0-9]{0,3}x[1-9][0-9]{0,3}", value):
        raise argparse.ArgumentTypeError("expected WIDTHxHEIGHT in physical pixels")
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scale", type=int, action="append", metavar="PERCENT",
                        help="display scaling percentage (repeat for multiple scales)")
    parser.add_argument("--size", type=window_size, action="append", metavar="WIDTHxHEIGHT",
                        help="physical window size (repeat for multiple sizes)")
    parser.add_argument("--no-build", action="store_true", help="reuse an existing Release build")
    parser.add_argument("--captures", type=Path, help="save screenshots by scale, skin and window size")
    args = parser.parse_args()
    scales = args.scale or SCALES
    sizes = args.size or SIZES
    if any(not 100 <= scale <= 300 for scale in scales):
        parser.error("--scale must be between 100 and 300")
    for command in ("dotnet", "xvfb-run", "xauth"):
        if not shutil.which(command):
            parser.error(f"{command} is required")

    if not args.no_build:
        subprocess.run(["dotnet", "restore", "src/OpenXLR.slnx", "--locked-mode"], check=True, cwd=ROOT)
        subprocess.run(["dotnet", "build", "src/OpenXLR.slnx", "-c", "Release", "--no-restore", "-warnaserror"],
                       check=True, cwd=ROOT)

    widths, heights = zip(*(map(int, size.split("x")) for size in sizes))
    failed = []
    for scale in scales:
        screen_width = max(max(widths) + 64, scale * 11)
        screen_height = max(max(heights) + 64, scale * 10)
        print(f"Display {scale}% · windows {', '.join(sizes)}", flush=True)
        env = dict(os.environ, AVALONIA_GLOBAL_SCALE_FACTOR=str(scale / 100),
                   OPENXLR_TEST_LAYOUT="1", OPENXLR_MIXER_FIT_SIZES=",".join(sizes))
        env.pop("OPENXLR_LAYOUT_ARTIFACTS", None)
        if args.captures:
            env["OPENXLR_LAYOUT_ARTIFACTS"] = str(args.captures.resolve() / str(scale))
        result = subprocess.run(
            ["xvfb-run", "-a", "-s", f"-screen 0 {screen_width}x{screen_height}x24",
             "dotnet", "test", PROJECT, "-c", "Release", "--no-build",
             "--filter", "FullyQualifiedName~WindowLayoutTests"],
            cwd=ROOT, env=env)
        if result.returncode:
            failed.append(scale)

    if failed:
        parser.exit(1, "Failed scales " + ", ".join(f"{scale}%" for scale in failed) + "\n")


if __name__ == "__main__":
    main()
