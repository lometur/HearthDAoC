"""The splash entry of the classic-creation patch set, and the checks on our splash.mpk.

pregame/splash.mpk holds the 8 loading images. The patch set replaces the whole file with
the bundled splash.mpk ("file" op, "after": "source"), which the bundle step builds from
branding/splash.png with branding/build_splash_mpk.py. Only that build is ever bundled:
OfflineDAoC's own splash.mpk stays on the player's machine.
"""
import os
import struct

from mpk import read_mpk
from patchset import sha256_file

SPLASH_PATH = "pregame/splash.mpk"  # in the client folder
SPLASH_SOURCE = "splash.mpk"  # in the bundle folder (client/patches/splash.mpk when built locally)
SPLASH_NAME = "splash.mpk"  # the archive's internal name; the client binds the archive by it
SPLASH_ENTRIES = [f"splash{i}.tga" for i in range(1, 9)]
STOCK_SPLASH_SHA256 = "f24460d2b064b86527b1800940d6d80c26b67ca3531d91b87c3b7a4d38455a9e"
WIDTH, HEIGHT = 1024, 768
# Uncompressed true-colour (type 2), 32 bits, descriptor 8 = 8 alpha bits and a bottom-left
# origin. The client renders RLE-compressed TGAs black.
TGA_HEADER = struct.pack("<BBB5sHHHHBB", 0, 0, 2, bytes(5), 0, 0, WIDTH, HEIGHT, 32, 8)
TGA_FOOTER = bytes(8) + b"TRUEVISION-XFILE.\0"
TGA_SIZE = len(TGA_HEADER) + WIDTH * HEIGHT * 4 + len(TGA_FOOTER)


class SplashError(ValueError):
    """A splash file the client can't use, or a client splash this release doesn't know."""


def check_splash_tga(name, data):
    """Raise SplashError unless data is a 1024x768 TGA the client renders."""
    if data[:len(TGA_HEADER)] != TGA_HEADER:
        raise SplashError(f"{name}: not an uncompressed 1024x768 32-bit bottom-left TGA "
                          f"(header {data[:18].hex()})")
    if len(data) != TGA_SIZE or not data.endswith(TGA_FOOTER):
        raise SplashError(f"{name}: {len(data)} bytes, expected {TGA_SIZE} with a TGA 2.0 footer")


def check_splash_mpk(path):
    """Raise SplashError (or mpk.MpkError) unless path is a splash.mpk the client can load."""
    name, entries = read_mpk(path)
    if name != SPLASH_NAME:
        raise SplashError(f"{path}: internal name {name!r}, the client expects {SPLASH_NAME!r}")
    names = sorted(entry for entry, _ in entries)
    if names != SPLASH_ENTRIES:
        raise SplashError(f"{path}: holds {names}, expected {SPLASH_ENTRIES}")
    for entry, data in entries:
        check_splash_tga(entry, data)


def splash_entry(client_dir, splash_mpk_path):
    """Return the patch-set entry that replaces the client's splash.mpk with ours."""
    check_splash_mpk(splash_mpk_path)
    before = sha256_file(os.path.join(client_dir, *SPLASH_PATH.split("/")))
    if before != STOCK_SPLASH_SHA256:
        raise SplashError(f"{SPLASH_PATH} in {client_dir} isn't OfflineDAoC 0.34's splash "
                          f"(SHA-256 {before})")
    return {"path": SPLASH_PATH, "before": before, "after": "source",
            "ops": [{"op": "file", "source": SPLASH_SOURCE}]}
