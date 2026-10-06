#!/usr/bin/env python3
"""Build splash.mpk, the client's 8 loading images, from branding/splash.png.

Like upstream's Build-ClientSplash.ps1: one 1024x768 uncompressed 32-bit TGA with a
bottom-left origin, copied to splash1.tga ... splash8.tga and packed with upstream's
OfflineDaoc.Mpk tool under the internal name "splash.mpk". Standard library only, plus .NET
for the MPK tool. The packed archive is read back and checked before it is written.

    python3 client/patches/branding/build_splash_mpk.py \\
        --mpk-tool source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll

The output (default client/patches/splash.mpk) is committed, and the patch set pins its
SHA-256. MPK entries carry a timestamp, so two builds differ: build it only when splash.png
changes, then rebuild classic-creation.json and commit splash.png, splash.mpk and the JSON
together.
"""
import argparse
import os
import struct
import subprocess
import sys
import tempfile
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
PATCHES = os.path.dirname(HERE)
sys.path.insert(0, PATCHES)
from mpk import read_mpk  # noqa: E402
from splash_entry import HEIGHT, SPLASH_ENTRIES, SPLASH_NAME, WIDTH  # noqa: E402
from splash_entry import TGA_FOOTER, TGA_HEADER, SplashError, check_splash_mpk  # noqa: E402

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def _unfilter(kind, line, prev, bpp):
    out = bytearray(line)
    n = len(out)
    if kind == 0:
        pass
    elif kind == 1:
        for i in range(bpp, n):
            out[i] = (out[i] + out[i - bpp]) & 0xFF
    elif kind == 2:
        out = bytearray((a + b) & 0xFF for a, b in zip(line, prev))
    elif kind == 3:
        for i in range(n):
            left = out[i - bpp] if i >= bpp else 0
            out[i] = (out[i] + ((left + prev[i]) >> 1)) & 0xFF
    elif kind == 4:
        for i in range(n):
            a = out[i - bpp] if i >= bpp else 0
            b = prev[i]
            c = prev[i - bpp] if i >= bpp else 0
            p = a + b - c
            pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
            pred = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
            out[i] = (out[i] + pred) & 0xFF
    else:
        raise ValueError(f"PNG filter type {kind} is invalid")
    return out


def read_png(path):
    """Return (width, height, RGBA bytes, top row first) of an 8-bit RGB or RGBA PNG."""
    with open(path, "rb") as f:
        data = f.read()
    if data[:8] != PNG_SIGNATURE:
        raise ValueError(f"{path}: not a PNG file")
    pos, header, idat = 8, None, []
    while pos < len(data):
        length, kind = struct.unpack(">I4s", data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + length]
        (crc,) = struct.unpack(">I", data[pos + 8 + length:pos + 12 + length])
        if zlib.crc32(kind + body) != crc:
            raise ValueError(f"{path}: bad CRC in chunk {kind!r}")
        if kind == b"IHDR":
            header = struct.unpack(">IIBBBBB", body)
        elif kind == b"IDAT":
            idat.append(body)
        elif kind == b"IEND":
            break
        pos += 12 + length
    if header is None:
        raise ValueError(f"{path}: no IHDR chunk")
    width, height, depth, colour, _, _, interlace = header
    if depth != 8 or colour not in (2, 6) or interlace != 0:
        raise ValueError(f"{path}: only 8-bit, non-interlaced RGB or RGBA PNGs are supported")
    bpp = 3 if colour == 2 else 4
    raw = zlib.decompress(b"".join(idat))
    stride = width * bpp
    if len(raw) != height * (stride + 1):
        raise ValueError(f"{path}: image data has the wrong size")
    rows, prev = [], bytearray(stride)
    for y in range(height):
        start = y * (stride + 1)
        prev = _unfilter(raw[start], raw[start + 1:start + 1 + stride], prev, bpp)
        rows.append(bytes(prev))
    pixels = b"".join(rows)
    if bpp == 3:
        rgba = bytearray(b"\xff" * (width * height * 4))
        for k in range(3):
            rgba[k::4] = pixels[k::3]
        pixels = bytes(rgba)
    return width, height, pixels


def tga_bytes(width, height, rgba):
    """Return the client's splash TGA: header, BGRA rows bottom-up, TGA 2.0 footer."""
    if (width, height) != (WIDTH, HEIGHT) or len(rgba) != width * height * 4:
        raise SplashError(f"the splash must be {WIDTH}x{HEIGHT}, got {width}x{height}")
    bgra = bytearray(rgba)
    bgra[0::4], bgra[2::4] = rgba[2::4], rgba[0::4]
    stride = width * 4
    rows = [bytes(bgra[y * stride:(y + 1) * stride]) for y in range(height - 1, -1, -1)]
    return TGA_HEADER + b"".join(rows) + TGA_FOOTER


def build(png, mpk_tool, out, dotnet="dotnet"):
    """Write out: splash1..8.tga from png, packed by the MPK tool and checked."""
    tga = tga_bytes(*read_png(png))
    out = os.path.abspath(out)
    with tempfile.TemporaryDirectory(prefix="hdc-splash-") as tmp:
        images = os.path.join(tmp, "images")
        os.mkdir(images)
        for name in SPLASH_ENTRIES:
            with open(os.path.join(images, name), "wb") as f:
                f.write(tga)
        packed = os.path.join(tmp, SPLASH_NAME)
        result = subprocess.run([dotnet, mpk_tool, "pack", images, packed, SPLASH_NAME],
                                capture_output=True, text=True)
        if result.returncode != 0:
            raise SplashError(f"the MPK tool failed (exit {result.returncode}): "
                              f"{result.stderr.strip() or result.stdout.strip()}")
        check_splash_mpk(packed)
        if any(data != tga for _, data in read_mpk(packed)[1]):
            raise SplashError("the packed archive doesn't hold the images that were written")
        os.makedirs(os.path.dirname(out), exist_ok=True)
        staged = out + ".tmp"
        with open(packed, "rb") as src, open(staged, "wb") as dst:
            dst.write(src.read())
        os.replace(staged, out)


def main(argv=None):
    ap = argparse.ArgumentParser(description="Build the client's splash.mpk from splash.png.")
    ap.add_argument("--png", default=os.path.join(HERE, "splash.png"))
    ap.add_argument("--mpk-tool", required=True, help="path to OfflineDaoc.Mpk.dll")
    ap.add_argument("--out", default=os.path.join(PATCHES, SPLASH_NAME))
    ap.add_argument("--dotnet", default="dotnet")
    args = ap.parse_args(argv)
    try:
        build(args.png, args.mpk_tool, args.out, args.dotnet)
    except (OSError, ValueError) as e:
        print(f"build_splash_mpk: {e}", file=sys.stderr)
        return 1
    print(f"Wrote {args.out} ({len(SPLASH_ENTRIES)} x {WIDTH}x{HEIGHT} TGA)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
