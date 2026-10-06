"""Tests for the base-class code cave: baseclass_data.inc, the nasm build and the patched game.dll.

The unit tests need nasm (apt install nasm) and read this repo's server sources. The real-file
tests also need HDC_CLIENT_FILES, an OfflineDAoC 0.34 classic client folder; its game.dll is only
read.
"""
import dataclasses
import hashlib
import os
import re
import struct
import sys
import unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
PATCHES = os.path.dirname(HERE)
sys.path.insert(0, PATCHES)
import build  # noqa: E402
import classdata  # noqa: E402
import patchset  # noqa: E402
import pe  # noqa: E402

REPO = os.path.dirname(os.path.dirname(PATCHES))
SERVER_SRC = os.path.join(REPO, "source", "server")
SHIPPED_DISABLED = "20;33;34;39;58-62"  # the clean classic 0.34 world's disabled_classes
CLIENT = os.environ.get("HDC_CLIENT_FILES")
NEEDS_CLIENT = "set HDC_CLIENT_FILES to an OfflineDAoC 0.34 classic client folder"
ORG = 0x248B000  # the VA the .hdcc section gets in the classic 0.34 game.dll
CALLED = {0x5B438C, 0x5B01A3, 0x5B4A65, 0x520E28, 0x45BDC3}  # game.dll functions the cave calls
FIGHTER = ("    ; 14 Fighter: Armsman, Mercenary, Paladin, Reaver\n"
           "    dd 14, 1, 0x242, 0x940c7c, desc_14\n"
           "    dd 0, 1, 2\n"
           "    dd 5, 1, 2, 3, 4, 13\n")


def base_classes():
    return classdata.base_classes(SERVER_SRC, SHIPPED_DISABLED)


def data_inc(classes=None):
    return build.cave_data_inc(base_classes() if classes is None else classes,
                               classdata.FINAL_CLASS_IDS, classdata.HIDE_RACES)


def call_targets(code, org):
    """The target of every E8 rel32 in `code` (data bytes can look like a call too)."""
    return {org + i + 5 + struct.unpack_from("<i", code, i + 1)[0] for i in range(len(code) - 4) if code[i] == 0xE8}


def changed_offsets(old, new):
    """Offsets below len(old) where `new` differs from `old`, compared 4 KiB at a time."""
    changed = set()
    for start in range(0, len(old), 4096):
        a, b = old[start:start + 4096], new[start:start + 4096]
        if a != b:
            changed.update(start + i for i in range(len(a)) if a[i] != b[i])
    return changed


class CaveDataIncTests(unittest.TestCase):
    def test_the_fighter_record(self):
        self.assertIn("base_table:\n" + FIGHTER, data_inc())

    def test_one_record_and_one_description_per_base_class(self):
        inc = data_inc()
        bcs = base_classes()
        records = re.findall(r"^    dd (\d+), ([123]), 0x[0-9a-f]+, 0x[0-9a-f]+, desc_\1$", inc, re.M)
        self.assertEqual(records, [(str(bc.id), str(bc.realm)) for bc in bcs])
        self.assertEqual(len(records), 15)
        self.assertIn("    dd 3, 9, 10, 15\n    dd 0\n\nhide_classes:\n", inc)  # Forester's races, end of table
        for bc in bcs:
            self.assertIn(f'desc_{bc.id}:\n    db "{bc.description}", 0\n', inc)

    def test_hide_lists(self):
        inc = data_inc()
        self.assertIn("hide_classes:\n    db 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 19, 21, 22, 23, 24, 25, 26, "
                      "27, 28, 29, 30, 31, 32, 33, 34, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 55, 56, 58, "
                      "59, 60, 61, 62, 0\n", inc)
        self.assertIn("hide_races:\n    db 16, 17, 18, 19, 20, 21, 0\n", inc)

    def test_refuses_what_the_cave_cannot_hold(self):
        fighter = base_classes()[0]
        for bad in (dataclasses.replace(fighter, description='Say "hi".'),
                    dataclasses.replace(fighter, description="Caf\xe9."),
                    dataclasses.replace(fighter, races=[]),
                    dataclasses.replace(fighter, stats=[0, 1])):
            with self.assertRaises(ValueError):
                data_inc([bad])
        with self.assertRaises(ValueError):
            build.cave_data_inc([fighter], [1, 300], [16])
        with self.assertRaises(ValueError):
            build.cave_data_inc([fighter], [1], [0])


class AssembleCaveTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cave = build.assemble_cave(ORG, data_inc())

    def test_is_reproducible(self):
        self.assertEqual(build.assemble_cave(ORG, data_inc()), self.cave)

    def test_starts_with_the_entry_point(self):
        self.assertEqual(self.cave[:6].hex(), "5589e583ec50")  # push ebp; mov ebp,esp; sub esp,0x50

    def test_calls_land_on_the_game_dll_functions(self):
        self.assertLessEqual(CALLED, call_targets(self.cave, ORG))

    def test_the_code_points_at_its_data(self):
        hide_classes = self.cave.index(bytes(classdata.FINAL_CLASS_IDS) + b"\0")
        hide_races = hide_classes + len(classdata.FINAL_CLASS_IDS) + 1
        self.assertEqual(self.cave[hide_races:hide_races + 7], bytes(classdata.HIDE_RACES) + b"\0")
        table = self.cave.index(struct.pack("<4I", 14, 1, 0x242, 0x940C7C))
        self.assertEqual(table % 4, 0)
        for opcode, at in ((b"\xbe", hide_classes), (b"\xbe", hide_races), (b"\xbf", table)):  # mov esi/edi, imm32
            self.assertEqual(self.cave.count(opcode + struct.pack("<I", ORG + at)), 1, hex(at))

    def test_each_record_matches_the_layout_in_baseclass_asm(self):
        at = self.cave.index(struct.pack("<4I", 14, 1, 0x242, 0x940C7C))
        for bc in base_classes():
            cid, realm, name_id, name_ptr, desc, s1, s2, s3, count = struct.unpack_from("<9I", self.cave, at)
            self.assertEqual((cid, realm, name_id, name_ptr), (bc.id, bc.realm, bc.name_id, bc.name_ptr))
            self.assertEqual([s1, s2, s3], bc.stats, bc.name)
            self.assertEqual(list(struct.unpack_from(f"<{count}I", self.cave, at + 0x24)), bc.races, bc.name)
            text = bc.description.encode("ascii") + b"\0"
            self.assertEqual(self.cave[desc - ORG:desc - ORG + len(text)], text, bc.name)
            at += 0x24 + 4 * count
        self.assertEqual(struct.unpack_from("<I", self.cave, at)[0], 0)

    def test_the_origin_only_moves_addresses(self):
        moved = build.assemble_cave(ORG + 0x10000, data_inc())
        self.assertEqual(len(moved), len(self.cave))
        self.assertLessEqual(CALLED, call_targets(moved, ORG + 0x10000))
        self.assertNotEqual(moved, self.cave)

    def test_reports_a_nasm_error(self):
        with self.assertRaisesRegex(ValueError, "nasm failed"):
            build.assemble_cave(ORG, "; no base_table\n")

    def test_reports_a_missing_nasm(self):
        with mock.patch.object(build.shutil, "which", return_value=None):
            with self.assertRaisesRegex(ValueError, "nasm not found"):
                build.assemble_cave(ORG, data_inc())


@unittest.skipUnless(CLIENT, NEEDS_CLIENT)
class RealGameDllCaveTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        with open(os.path.join(CLIENT, "game.dll"), "rb") as f:
            cls.original = f.read()
        cls.org = build.cave_origin(cls.original)
        cls.cave = build.assemble_cave(cls.org, data_inc())
        cls.patched = build.patch_game_dll(cls.original, cls.cave)
        cls.old, cls.new = pe.PE(cls.original), pe.PE(cls.patched)

    def test_the_section_table_is_valid(self):
        old, new = self.old, self.new
        self.assertEqual(self.org, ORG)
        self.assertEqual(len(new.sections), 9)
        self.assertEqual([s.name for s in new.sections], [s.name for s in old.sections] + [".hdcc"])
        self.assertEqual(new.sections[:-1], old.sections)
        raw_size = pe.align(len(self.cave), new.file_alignment)
        self.assertEqual(new.sections[-1], pe.Section(".hdcc", ORG, len(self.cave), len(self.original), raw_size,
                                                      0x60000020))
        self.assertEqual(len(self.patched), len(self.original) + raw_size)
        self.assertEqual(new.size_of_image, pe.align(ORG - new.image_base + len(self.cave), new.section_alignment))
        self.assertLessEqual(new.header_offsets["section_table"] + 40 * len(new.sections), new.size_of_headers)
        end = 0
        for s in new.sections:
            self.assertEqual((s.va - new.image_base) % new.section_alignment, 0, s.name)
            self.assertEqual((s.raw_offset % new.file_alignment, s.raw_size % new.file_alignment), (0, 0), s.name)
            self.assertGreaterEqual(s.va, end, s.name)
            end = s.va + max(s.vsize, s.raw_size)
        size_of_code = struct.unpack_from("<I", self.original, old.header_offsets["size_of_code"])[0]
        self.assertEqual(struct.unpack_from("<I", self.patched, new.header_offsets["size_of_code"])[0],
                         size_of_code + raw_size)

    def test_the_checksum_is_valid(self):
        offset = self.new.header_offsets["checksum"]
        self.assertEqual(struct.unpack_from("<I", self.patched, offset)[0], pe.checksum(self.patched, offset))

    def test_the_section_holds_the_cave(self):
        tail = self.patched[len(self.original):]
        self.assertEqual(tail[:len(self.cave)], self.cave)
        self.assertEqual(tail[len(self.cave):], bytes(len(tail) - len(self.cave)))

    def test_the_hook_calls_the_cave_entry(self):
        at = self.new.offset(build.HOOK_VA)
        self.assertEqual(self.original[at:at + 5].hex(), build.HOOK_FROM)
        self.assertEqual(self.patched[at:at + 5].hex(), "e8aaafed01")
        rel = struct.unpack_from("<i", self.patched, at + 1)[0]
        self.assertEqual(build.HOOK_VA + 5 + rel, self.new.sections[-1].va)

    def test_nothing_else_changes(self):
        # Inside the original's length only P1-P3, the hook and the header fields that describe the
        # new section change: the entry point, the imports and every other header stay as they were.
        allowed = set()
        for va, before, _ in build.STAT_FLOW:
            start = self.old.offset(va)
            allowed.update(range(start, start + len(before) // 2))
        hook = self.old.offset(build.HOOK_VA)
        allowed.update(range(hook, hook + 5))
        offsets = self.old.header_offsets
        for key, size in (("num_sections", 2), ("size_of_code", 4), ("size_of_image", 4), ("checksum", 4)):
            allowed.update(range(offsets[key], offsets[key] + size))
        entry = offsets["section_table"] + 40 * len(self.old.sections)
        allowed.update(range(entry, entry + 40))
        changed = changed_offsets(self.original, self.patched)
        self.assertLessEqual(changed, allowed)
        self.assertIn(hook + 1, changed)  # the call's rel32; its E8 opcode stays

    def test_the_committed_patch_set_is_this_build(self):
        ps = patchset.load(os.path.join(PATCHES, "classic-creation.json"))
        entry = next(e for e in ps["files"] if e["path"] == "game.dll")
        self.assertEqual(entry["before"], hashlib.sha256(self.original).hexdigest())
        out = patchset.transform(self.original, entry["ops"], PATCHES)
        self.assertEqual(hashlib.sha256(out).hexdigest(), entry["after"])
        self.assertEqual(entry["after"], hashlib.sha256(self.patched).hexdigest(),
                         "classic-creation.json is out of date: run build.py again")


if __name__ == "__main__":
    unittest.main()
