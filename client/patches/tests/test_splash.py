"""Splash tests: the MPK reader, the splash entry and its checks, the TGA and PNG helpers, the
MPK build (only when HDC_MPK_TOOL points to OfflineDaoc.Mpk.dll) and the re-lettered splash.png.
"""
import os
import shutil
import struct
import sys
import tempfile
import unittest
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
PATCHES = os.path.dirname(HERE)
REPO = os.path.dirname(os.path.dirname(PATCHES))
sys.path.insert(0, PATCHES)
import mpk  # noqa: E402
import splash_entry  # noqa: E402

# OfflineDAoC 0.34's pregame/splash.mpk. Upstream keeps a byte-identical copy in the repo.
UPSTREAM = os.path.join(REPO, "source", "tools", "OfflineDaoc.Launcher", "Assets",
                        "offline-daoc-client-splash.mpk")
BLACK_TGA = (splash_entry.TGA_HEADER + bytes(splash_entry.WIDTH * splash_entry.HEIGHT * 4)
             + splash_entry.TGA_FOOTER)
NAMES = [f"splash{i}.tga" for i in range(1, 9)]


def write_mpk(path, name, entries):
    """Write an MPK laid out the way OfflineDaoc.Mpk writes one (a test fixture)."""
    blobs = [zlib.compress(data) for _, data in entries]
    directory, offset, data_offset = b"", 0, 0
    for (entry, data), blob in zip(entries, blobs):
        directory += entry.encode().ljust(256, b"\0") + struct.pack(
            "<IiIIIII", 0, 4, offset, len(data), data_offset, len(blob), zlib.crc32(blob))
        offset, data_offset = offset + len(data), data_offset + len(blob)
    packed_dir, packed_name = zlib.compress(directory), zlib.compress(name.encode())
    head = struct.pack("<IIII", zlib.crc32(packed_dir), len(packed_dir), len(packed_name),
                       len(entries))
    with open(path, "wb") as f:
        f.write(b"MPAK\x02" + bytes(b ^ i for i, b in enumerate(head)) + packed_name
                + packed_dir + b"".join(blobs))


class MpkReaderTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.path = os.path.join(self.tmp.name, "test.mpk")

    def test_reads_the_upstream_splash(self):
        name, entries = mpk.read_mpk(UPSTREAM)
        self.assertEqual(name, "splash.mpk")
        self.assertEqual([entry for entry, _ in entries], NAMES)
        self.assertEqual({len(data) for _, data in entries}, {splash_entry.TGA_SIZE})

    def test_round_trip(self):
        entries = [("a.txt", b"alpha"), ("b.bin", bytes(range(256)) * 9)]
        write_mpk(self.path, "x.mpk", entries)
        self.assertEqual(mpk.read_mpk(self.path), ("x.mpk", entries))

    def test_corrupt_entry_is_refused(self):
        write_mpk(self.path, "x.mpk", [("a.txt", b"alpha" * 100)])
        with open(self.path, "r+b") as f:
            f.seek(-3, os.SEEK_END)
            f.write(b"\0\0\0")
        with self.assertRaisesRegex(mpk.MpkError, "a.txt: CRC mismatch"):
            mpk.read_mpk(self.path)

    def test_not_an_mpk_is_refused(self):
        with open(self.path, "wb") as f:
            f.write(b"PK\x03\x04 a zip file")
        with self.assertRaisesRegex(mpk.MpkError, "not an MPK archive"):
            mpk.read_mpk(self.path)


class SplashEntryTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.built = os.path.join(self.tmp.name, "splash.mpk")
        write_mpk(self.built, "splash.mpk", [(name, BLACK_TGA) for name in NAMES])
        self.client = os.path.join(self.tmp.name, "client")
        os.makedirs(os.path.join(self.client, "pregame"))
        shutil.copyfile(UPSTREAM, os.path.join(self.client, "pregame", "splash.mpk"))

    def test_entry(self):
        entry = splash_entry.splash_entry(self.client, self.built)
        self.assertEqual(entry, {"path": "pregame/splash.mpk",
                                 "before": splash_entry.STOCK_SPLASH_SHA256,
                                 "after": "source",
                                 "ops": [{"op": "file", "source": "splash.mpk"}]})
        self.assertEqual(list(entry), ["path", "before", "after", "ops"])

    def test_the_upstream_archive_passes_the_checks(self):
        splash_entry.check_splash_mpk(UPSTREAM)

    def test_wrong_internal_name_is_refused(self):
        write_mpk(self.built, "hearth-splash.mpk", [(name, BLACK_TGA) for name in NAMES])
        with self.assertRaisesRegex(splash_entry.SplashError, "internal name 'hearth-splash.mpk'"):
            splash_entry.splash_entry(self.client, self.built)

    def test_seven_images_are_refused(self):
        write_mpk(self.built, "splash.mpk", [(name, BLACK_TGA) for name in NAMES[:7]])
        with self.assertRaisesRegex(splash_entry.SplashError, "expected"):
            splash_entry.splash_entry(self.client, self.built)

    def test_rle_image_is_refused(self):
        rle = BLACK_TGA[:2] + b"\x0a" + BLACK_TGA[3:]
        write_mpk(self.built, "splash.mpk", [(name, rle) for name in NAMES])
        with self.assertRaisesRegex(splash_entry.SplashError, "splash1.tga: not an uncompressed"):
            splash_entry.splash_entry(self.client, self.built)

    def test_top_left_origin_is_refused(self):
        with self.assertRaisesRegex(splash_entry.SplashError, "not an uncompressed"):
            splash_entry.check_splash_tga("x.tga", BLACK_TGA[:17] + b"\x28" + BLACK_TGA[18:])

    def test_missing_footer_is_refused(self):
        with self.assertRaisesRegex(splash_entry.SplashError, "TGA 2.0 footer"):
            splash_entry.check_splash_tga("x.tga", BLACK_TGA[:-26])

    def test_unknown_client_splash_is_refused(self):
        with open(os.path.join(self.client, "pregame", "splash.mpk"), "ab") as f:
            f.write(b"\0")
        with self.assertRaisesRegex(splash_entry.SplashError, "isn't OfflineDAoC 0.34's splash"):
            splash_entry.splash_entry(self.client, self.built)


# --- TGA and PNG helpers and the MPK build: branding/build_splash_mpk.py ---

import contextlib  # noqa: E402
import io  # noqa: E402

sys.path.insert(0, os.path.join(PATCHES, "branding"))
import build_splash_mpk  # noqa: E402

MPK_TOOL = os.environ.get("HDC_MPK_TOOL")


def upstream_rgba():
    """splash1.tga of the upstream archive as RGBA bytes, top row first."""
    data = dict(mpk.read_mpk(UPSTREAM)[1])["splash1.tga"]
    stride, height = splash_entry.WIDTH * 4, splash_entry.HEIGHT
    rows = [data[18 + y * stride:18 + (y + 1) * stride] for y in range(height - 1, -1, -1)]
    bgra = b"".join(rows)
    rgba = bytearray(bgra)
    rgba[0::4], rgba[2::4] = bgra[2::4], bgra[0::4]
    return bytes(rgba)


def png_filter(kind, line, prev, bpp):
    """Filter one PNG scanline: the encoder's side, to test the decoder."""
    out = bytearray()
    for i, x in enumerate(line):
        a = line[i - bpp] if i >= bpp else 0
        b = prev[i]
        c = prev[i - bpp] if i >= bpp else 0
        p = a + b - c
        paeth = a if abs(p - a) <= abs(p - b) and abs(p - a) <= abs(p - c) else (
            b if abs(p - b) <= abs(p - c) else c)
        out.append((x - (0, a, b, (a + b) // 2, paeth)[kind]) & 0xFF)
    return bytes(out)


def write_png(path, width, height, pixels, colour, kinds=(0, 1, 2, 3, 4)):
    """Write an 8-bit PNG (colour 2 = RGB, 6 = RGBA), cycling the rows through kinds."""
    bpp = 3 if colour == 2 else 4
    stride = width * bpp
    raw, prev = [], bytes(stride)
    for y in range(height):
        line = pixels[y * stride:(y + 1) * stride]
        kind = kinds[y % len(kinds)]
        raw.append(bytes([kind]) + (line if kind == 0 else png_filter(kind, line, prev, bpp)))
        prev = line

    def chunk(kind, body):
        crc = struct.pack(">I", zlib.crc32(kind + body))
        return struct.pack(">I", len(body)) + kind + body + crc

    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n"
                + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, colour, 0, 0, 0))
                + chunk(b"IDAT", zlib.compress(b"".join(raw))) + chunk(b"IEND", b""))


class TgaTests(unittest.TestCase):
    def test_matches_offlinedaocs_own_splash_byte_for_byte(self):
        original = dict(mpk.read_mpk(UPSTREAM)[1])["splash1.tga"]
        self.assertEqual(build_splash_mpk.tga_bytes(1024, 768, upstream_rgba()), original)

    def test_header_and_bottom_left_row_order(self):
        w, h = 1024, 768
        rgba = bytearray(b"\x00\x00\x00\xff" * (w * h))
        rgba[:w * 4] = b"\xff\x00\x00\xff" * w  # top row red
        rgba[-w * 4:] = b"\x00\x00\xff\xff" * w  # bottom row blue
        tga = build_splash_mpk.tga_bytes(w, h, bytes(rgba))
        self.assertEqual((tga[2],) + struct.unpack("<HHBB", tga[12:18]), (2, 1024, 768, 32, 8))
        self.assertEqual(tga[18:22], b"\xff\x00\x00\xff")  # first stored row: the bottom, BGRA
        last_row = 18 + (h - 1) * w * 4
        self.assertEqual(tga[last_row:last_row + 4], b"\x00\x00\xff\xff")  # last: the top
        splash_entry.check_splash_tga("splash1.tga", tga)

    def test_wrong_size_is_refused(self):
        with self.assertRaisesRegex(splash_entry.SplashError, "1024x768"):
            build_splash_mpk.tga_bytes(800, 600, bytes(800 * 600 * 4))


class PngTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.path = os.path.join(self.tmp.name, "t.png")

    def test_rgb_with_every_filter_type(self):
        w, h = 7, 10
        pixels = bytes((x * 37 + y * 11 + k * 101) & 0xFF
                       for y in range(h) for x in range(w) for k in range(3))
        write_png(self.path, w, h, pixels, 2)
        expected = bytearray(b"\xff" * (w * h * 4))
        for k in range(3):
            expected[k::4] = pixels[k::3]
        self.assertEqual(build_splash_mpk.read_png(self.path), (w, h, bytes(expected)))

    def test_rgba(self):
        w, h = 5, 6
        pixels = bytes((x * 53 + y * 29 + k * 7) & 0xFF
                       for y in range(h) for x in range(w) for k in range(4))
        write_png(self.path, w, h, pixels, 6)
        self.assertEqual(build_splash_mpk.read_png(self.path), (w, h, pixels))

    def test_16_bit_is_refused(self):
        write_png(self.path, 2, 2, bytes(12), 2)
        with open(self.path, "r+b") as f:
            data = bytearray(f.read())
            data[24] = 16  # IHDR bit depth
            data[29:33] = struct.pack(">I", zlib.crc32(bytes(data[12:29])))
            f.seek(0)
            f.write(data)
        with self.assertRaisesRegex(ValueError, "only 8-bit"):
            build_splash_mpk.read_png(self.path)


@unittest.skipUnless(MPK_TOOL and shutil.which("dotnet"),
                     "set HDC_MPK_TOOL to OfflineDaoc.Mpk.dll (needs dotnet)")
class MpkToolTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.png = os.path.join(self.tmp.name, "art.png")
        write_png(self.png, 1024, 768, upstream_rgba(), 6, kinds=(0,))
        self.out = os.path.join(self.tmp.name, "out", "splash.mpk")

    def test_packs_eight_identical_images_under_the_client_name(self):
        build_splash_mpk.build(self.png, MPK_TOOL, self.out)
        name, entries = mpk.read_mpk(self.out)
        self.assertEqual(name, "splash.mpk")
        self.assertEqual([entry for entry, _ in entries], NAMES)
        original = dict(mpk.read_mpk(UPSTREAM)[1])["splash1.tga"]
        self.assertTrue(all(data == original for _, data in entries))
        splash_entry.check_splash_mpk(self.out)

    def test_cli(self):
        printed = io.StringIO()
        with contextlib.redirect_stdout(printed):
            code = build_splash_mpk.main(["--png", self.png, "--mpk-tool", MPK_TOOL,
                                          "--out", self.out])
        self.assertEqual((code, printed.getvalue()),
                         (0, f"Wrote {self.out} (8 x 1024x768 TGA)\n"))
        splash_entry.check_splash_mpk(self.out)

    def test_tool_failure_is_reported(self):
        missing = os.path.join(self.tmp.name, "missing.dll")
        with self.assertRaisesRegex(splash_entry.SplashError, "the MPK tool failed"):
            build_splash_mpk.build(self.png, missing, self.out)
        self.assertFalse(os.path.exists(self.out))


# --- The re-lettered art: branding/reletter_splash.py and branding/splash.png ---

import reletter_splash  # noqa: E402

SPLASH_PNG = os.path.join(PATCHES, "branding", "splash.png")


def rgb(rgba):
    out = bytearray(len(rgba) // 4 * 3)
    for k in range(3):
        out[k::3] = rgba[k::4]
    return bytes(out)


class SplashPngTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.width, cls.height, cls.png = build_splash_mpk.read_png(SPLASH_PNG)
        cls.art = upstream_rgba()

    def test_size(self):
        self.assertEqual((self.width, self.height), (1024, 768))

    def test_only_the_title_box_changes(self):
        left, top, right, bottom = reletter_splash.TITLE_BOX
        stride, changed = 1024 * 4, 0
        for y in range(768):
            new = self.png[y * stride:(y + 1) * stride]
            old = self.art[y * stride:(y + 1) * stride]
            # RGB only: upstream's border pixels carry alpha below 255, splash.png has none.
            if not top <= y < bottom:
                self.assertEqual(rgb(new), rgb(old), f"row {y}")
                continue
            self.assertEqual(rgb(new[:left * 4]), rgb(old[:left * 4]), f"row {y}")
            self.assertEqual(rgb(new[right * 4:]), rgb(old[right * 4:]), f"row {y}")
            changed += sum(new[x * 4:x * 4 + 3] != old[x * 4:x * 4 + 3]
                           for x in range(left, right))
        self.assertGreater(changed, 30000)

    def test_font_is_pinned(self):
        self.assertRegex(reletter_splash.FONT_URL, r"^https://raw\.githubusercontent\.com/"
                         r"google/fonts/[0-9a-f]{40}/ofl/cinzel/")
        self.assertRegex(reletter_splash.FONT_SHA256, r"^[0-9a-f]{64}$")
