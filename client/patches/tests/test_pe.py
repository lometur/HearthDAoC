import os
import random
import struct
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
import pe  # noqa: E402

IMAGE_BASE = 0x400000
FILE_ALIGN = 0x200
SECTION_ALIGN = 0x1000
CLIENT = os.environ.get("HDC_CLIENT_FILES")
NEEDS_CLIENT = "set HDC_CLIENT_FILES to an OfflineDAoC 0.34 classic client folder"


def align(value, alignment):
    return (value + alignment - 1) // alignment * alignment


def make_pe(sections=((".text", b"\x90" * 0x30, 0x60000020), (".data", b"\x01\x02\x03", 0xC0000040))):
    """A tiny PE32 image: DOS stub, PE header at 0x40, a 0xE0-byte optional header,
    room for five section headers in 0x200 bytes of headers, then the sections."""
    e_lfanew = 0x40
    opt = e_lfanew + 24
    table = opt + 0xE0
    data = bytearray(0x200)
    data[0:2] = b"MZ"
    struct.pack_into("<I", data, 0x3C, e_lfanew)
    data[e_lfanew:e_lfanew + 4] = b"PE\0\0"
    struct.pack_into("<HHIIIHH", data, e_lfanew + 4, 0x14C, len(sections), 0, 0, 0, 0xE0, 0x2102)
    struct.pack_into("<H", data, opt, 0x10B)
    struct.pack_into("<I", data, opt + 28, IMAGE_BASE)
    struct.pack_into("<II", data, opt + 32, SECTION_ALIGN, FILE_ALIGN)
    rva = SECTION_ALIGN
    code_size = 0
    for i, (name, body, characteristics) in enumerate(sections):
        raw = len(data)
        raw_size = align(len(body), FILE_ALIGN)
        data += body + bytes(raw_size - len(body))
        struct.pack_into("<8sIIIIIIHHI", data, table + 40 * i, name.encode("ascii").ljust(8, b"\0"),
                         len(body), rva, raw_size, raw, 0, 0, 0, 0, characteristics)
        if characteristics & 0x20:
            code_size += raw_size
        rva += align(len(body), SECTION_ALIGN)
    struct.pack_into("<I", data, opt + 4, code_size)
    struct.pack_into("<I", data, opt + 56, rva)
    struct.pack_into("<I", data, opt + 60, 0x200)
    struct.pack_into("<I", data, opt + 64, pe.checksum(data, opt + 64))
    return bytes(data)


def apply_ops(data, ops):
    out = bytearray(data)
    for op in ops:
        if op["op"] == "replace":
            start, before = op["offset"], bytes.fromhex(op["from"])
            assert out[start:start + len(before)] == before
            out[start:start + len(before)] = bytes.fromhex(op["to"])
        else:
            out += bytes.fromhex(op["data"])
    return bytes(out)


class PEReaderTests(unittest.TestCase):
    def test_reads_headers_and_sections(self):
        p = pe.PE(make_pe())
        self.assertEqual(p.image_base, IMAGE_BASE)
        self.assertEqual((p.file_alignment, p.section_alignment), (FILE_ALIGN, SECTION_ALIGN))
        self.assertEqual(p.size_of_image, 0x3000)
        self.assertEqual(p.sections, [
            pe.Section(".text", 0x401000, 0x30, 0x200, 0x200, 0x60000020),
            pe.Section(".data", 0x402000, 0x3, 0x400, 0x200, 0xC0000040),
        ])
        self.assertEqual(p.header_offsets, {"num_sections": 0x46, "size_of_code": 0x5C,
                                            "size_of_image": 0x90, "checksum": 0x98,
                                            "section_table": 0x138})

    def test_offset_maps_a_va_to_its_file_offset(self):
        p = pe.PE(make_pe())
        self.assertEqual(p.offset(0x401010), 0x210)
        self.assertEqual(p.offset(0x402002), 0x402)
        for outside in (0x400010, 0x401200, 0x403000):
            with self.assertRaises(ValueError):
                p.offset(outside)

    def test_rejects_data_that_is_not_a_pe32_file(self):
        for data in (b"", b"hello world", b"MZ" + bytes(0x100)):
            with self.assertRaises(ValueError):
                pe.PE(data)


class ChecksumTests(unittest.TestCase):
    def test_sums_16_bit_words_and_adds_the_length(self):
        self.assertEqual(pe.checksum(b"\x01\x00\x02\x00\xff\xff\xff\xff", 4), 1 + 2 + 8)

    def test_folds_the_carry(self):
        self.assertEqual(pe.checksum(b"\xff\xff\x02\x00\x00\x00\x00\x00", 4), 2 + 8)

    def test_pads_an_odd_length_and_ignores_the_stored_checksum(self):
        self.assertEqual(pe.checksum(b"\x12\x34\x56\x78\x07", 0), 7 + 5)


class AppendSectionTests(unittest.TestCase):
    def test_appends_a_section_and_updates_the_headers(self):
        old = make_pe()
        payload = bytes(range(256)) * 2 + b"\xc3" * 0x50
        new, va = pe.append_section(old, ".hdcc", payload)
        self.assertEqual(va, 0x403000)
        p = pe.PE(new)
        self.assertEqual(p.sections[-1], pe.Section(".hdcc", 0x403000, 0x250, 0x600, 0x400, 0x60000020))
        self.assertEqual(len(p.sections), 3)
        self.assertEqual(p.size_of_image, 0x4000)
        self.assertEqual(len(new), 0xA00)
        self.assertEqual(new[0x600:0x850], payload)
        self.assertEqual(new[0x850:], bytes(0x1B0))
        self.assertEqual(new[0x200:0x600], old[0x200:0x600])
        self.assertEqual(struct.unpack_from("<I", new, 0x5C)[0], 0x600)
        stored = struct.unpack_from("<I", new, p.header_offsets["checksum"])[0]
        self.assertEqual(stored, pe.checksum(new, p.header_offsets["checksum"]))

    def test_a_data_section_keeps_size_of_code(self):
        new, va = pe.append_section(make_pe(), ".hdcd", b"\x01", characteristics=0xC0000040)
        self.assertEqual(struct.unpack_from("<I", new, 0x5C)[0], 0x200)
        self.assertEqual(pe.PE(new).sections[-1].characteristics, 0xC0000040)

    def test_refuses_a_duplicate_name(self):
        with self.assertRaises(ValueError):
            pe.append_section(make_pe(), ".text", b"\x90")

    def test_refuses_a_bad_name_or_empty_payload(self):
        for name, payload in ((".toolongname", b"\x90"), ("", b"\x90"), (".hdcc", b"")):
            with self.assertRaises(ValueError):
                pe.append_section(make_pe(), name, payload)

    def test_refuses_when_the_header_has_no_room(self):
        full = make_pe(tuple((f".s{i}", b"\x90", 0x60000020) for i in range(5)))
        with self.assertRaises(ValueError):
            pe.append_section(full, ".hdcc", b"\x90")


class DiffOpsTests(unittest.TestCase):
    def test_identical_files_need_no_ops(self):
        self.assertEqual(pe.diff_ops(bytes(64), bytes(64)), [])

    def test_one_changed_byte(self):
        new = bytearray(64)
        new[10] = 1
        self.assertEqual(pe.diff_ops(bytes(64), bytes(new)),
                         [{"op": "replace", "offset": 10, "from": "00", "to": "01"}])

    def test_runs_less_than_16_bytes_apart_are_merged(self):
        new = bytearray(64)
        new[10] = new[26] = 1
        self.assertEqual(pe.diff_ops(bytes(64), bytes(new)),
                         [{"op": "replace", "offset": 10, "from": "00" * 17, "to": "01" + "00" * 15 + "01"}])

    def test_runs_16_bytes_apart_stay_separate(self):
        new = bytearray(64)
        new[10] = new[27] = 1
        self.assertEqual(pe.diff_ops(bytes(64), bytes(new)),
                         [{"op": "replace", "offset": 10, "from": "00", "to": "01"},
                          {"op": "replace", "offset": 27, "from": "00", "to": "01"}])

    def test_a_run_across_a_4k_block_boundary(self):
        new = bytearray(8192)
        new[4095] = new[4096] = 0xAB
        self.assertEqual(pe.diff_ops(bytes(8192), bytes(new)),
                         [{"op": "replace", "offset": 4095, "from": "0000", "to": "abab"}])

    def test_growth_becomes_one_append_op(self):
        new = bytearray(16) + b"\xaa\xbb"
        new[0] = 7
        self.assertEqual(pe.diff_ops(bytes(16), bytes(new)),
                         [{"op": "replace", "offset": 0, "from": "00", "to": "07"},
                          {"op": "append", "data": "aabb"}])

    def test_refuses_to_shrink(self):
        with self.assertRaises(ValueError):
            pe.diff_ops(bytes(16), bytes(15))

    def test_random_edits_round_trip(self):
        rng = random.Random(7)
        old = bytes(rng.randrange(256) for _ in range(20000))
        new = bytearray(old)
        for _ in range(300):
            new[rng.randrange(len(new))] = rng.randrange(256)
        new += bytes(rng.randrange(256) for _ in range(77))
        self.assertEqual(apply_ops(old, pe.diff_ops(old, bytes(new))), bytes(new))


@unittest.skipUnless(CLIENT, NEEDS_CLIENT)
class RealGameDllTests(unittest.TestCase):
    def test_reads_the_real_game_dll(self):
        with open(os.path.join(CLIENT, "game.dll"), "rb") as f:
            data = f.read()
        p = pe.PE(data)
        self.assertEqual(p.image_base, 0x400000)
        self.assertEqual([s.name for s in p.sections],
                         [".text", ".rdata", ".data", "Shared", ".rsrc", ".botmap", ".ofly", ".raid"])
        self.assertEqual(p.offset(0x59C0B2), 0x19C0B2)
        stored = struct.unpack_from("<I", data, p.header_offsets["checksum"])[0]
        self.assertEqual(pe.checksum(data, p.header_offsets["checksum"]), stored)
