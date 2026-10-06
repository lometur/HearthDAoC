import hashlib
import os
import struct
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
PATCHES = os.path.dirname(HERE)
sys.path.insert(0, PATCHES)
import build  # noqa: E402
import patchset  # noqa: E402
import pe  # noqa: E402

REPO = os.path.dirname(os.path.dirname(PATCHES))
SERVER_SRC = os.path.join(REPO, "source", "server")
CLIENT = os.environ.get("HDC_CLIENT_FILES")
WORLD_DB = os.environ.get("HDC_TEST_WORLD")
NEEDS_FILES = ("set HDC_CLIENT_FILES to an OfflineDAoC 0.34 classic client folder and "
               "HDC_TEST_WORLD to a clean classic world database")


def sha256(data):
    return hashlib.sha256(data).hexdigest()


class EditTextTests(unittest.TestCase):
    def test_replaces_the_one_occurrence_and_keeps_crlf(self):
        self.assertEqual(build.edit_text(b"a\r\nB\r\nc\r\n", [("B\r\n", "")]), b"a\r\nc\r\n")

    def test_refuses_a_missing_or_repeated_find(self):
        for data in (b"abc", b"xBxB"):
            with self.assertRaises(ValueError):
                build.edit_text(data, [("B", "")])

    def test_keeps_latin1_bytes(self):
        self.assertEqual(build.edit_text(b"\xe9X\xff", [("X", "Y")]), b"\xe9Y\xff")


class PatchDataTests(unittest.TestCase):
    def test_stat_flow_is_p1_p2_p3_with_same_length_edits(self):
        self.assertEqual([va for va, _, _ in build.STAT_FLOW], [0x59C0B2, 0x59A853, 0x59C574])
        self.assertEqual([len(bytes.fromhex(before)) for _, before, _ in build.STAT_FLOW], [3, 28, 1])
        for va, before, after in build.STAT_FLOW:
            self.assertEqual(len(bytes.fromhex(after)), len(bytes.fromhex(before)), hex(va))

    def test_the_xml_edit_removes_only_the_optimize_button(self):
        [(find, replace)] = build.XML_EDITS["pregame/character_customize_stats.xml"]
        self.assertTrue(find.startswith("\t\t<ButtonDef>\r\n"))
        self.assertTrue(find.endswith("\t\t</ButtonDef>\r\n"))
        self.assertEqual(find.count("<ButtonDef>"), 1)
        self.assertIn("\t\t\t<ControlId>1021</ControlId>\r\n\t\t\t<Label>Optimize</Label>\r\n", find)
        self.assertEqual(replace, "")

    def test_refuses_a_game_dll_that_is_not_the_classic_034_file(self):
        with self.assertRaises(ValueError):
            build.patch_game_dll(b"MZ" + bytes(0x200))

    def test_json_layout(self):
        self.assertEqual(build.to_json({"format": 1, "files": [{"find": "\té\r\n"}]}),
                         '{\n "format": 1,\n "files": [\n  {\n   "find": "\\t\\u00e9\\r\\n"\n  }\n ]\n}\n')


@unittest.skipUnless(CLIENT and WORLD_DB, NEEDS_FILES)
class RealBuildTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.patchset = build.build_patchset(CLIENT, WORLD_DB, SERVER_SRC)

    def read(self, path):
        with open(os.path.join(CLIENT, *path.split("/")), "rb") as f:
            return f.read()

    def test_lists_game_dll_and_the_stats_xml(self):
        self.assertEqual([self.patchset[key] for key in ("format", "name", "client")],
                         [1, "classic-creation", "OfflineDAoC 0.34 classic"])
        self.assertEqual([entry["path"] for entry in self.patchset["files"]],
                         ["game.dll", "pregame/character_customize_stats.xml"])

    def test_is_deterministic(self):
        again = build.build_patchset(CLIENT, WORLD_DB, SERVER_SRC)
        self.assertEqual(build.to_json(again), build.to_json(self.patchset))

    def test_game_dll_before_hash_is_the_real_file(self):
        entry = self.patchset["files"][0]
        self.assertEqual(entry["before"], sha256(self.read("game.dll")))
        self.assertEqual(entry["before"], build.GAME_DLL_SHA256)

    def test_transform_gives_the_after_hash(self):
        for entry in self.patchset["files"]:
            out = patchset.transform(self.read(entry["path"]), entry["ops"], PATCHES)
            self.assertEqual(sha256(out), entry["after"], entry["path"])

    def test_game_dll_ops_are_the_headers_the_stat_flow_the_hook_and_the_cave(self):
        ops = self.patchset["files"][0]["ops"]
        # NumberOfSections, SizeOfCode, SizeOfImage + CheckSum, the .hdcc section header, P2, P1, P3, the hook
        self.assertEqual([(op["op"], op.get("offset")) for op in ops],
                         [("replace", 0x15E), ("replace", 0x175), ("replace", 0x1A9), ("replace", 0x390),
                          ("replace", 0x19A853), ("replace", 0x19C0B2), ("replace", 0x19C574),
                          ("replace", 0x1B0052), ("append", None)])
        self.assertEqual(len(ops[-1]["data"]), 2 * 0x1000)
        patched = patchset.transform(self.read("game.dll"), ops, PATCHES)
        offset = pe.PE(patched).header_offsets["checksum"]
        self.assertEqual(struct.unpack_from("<I", patched, offset)[0], pe.checksum(patched, offset))

    def test_cli_writes_the_same_json(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "classic-creation.json")
            subprocess.run([sys.executable, os.path.join(PATCHES, "build.py"), "--client", CLIENT,
                            "--world-db", WORLD_DB, "--server-src", SERVER_SRC, "--out", out],
                           check=True, capture_output=True)
            with open(out, "rb") as f:
                written = f.read()
        self.assertEqual(written, build.to_json(self.patchset).encode("ascii"))
