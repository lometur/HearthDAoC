"""Tests for the patch-set library (patchset.py) and the Linux applier (apply_patches.py).

Every test builds its own fixture client folder, bundle folder and patch set in a temporary
directory, so no game file is needed. The MPK archives are of our own text, written and read here
independently of patchset.py.
"""
import hashlib
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
import zlib
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
PATCHES = os.path.dirname(HERE)
sys.path.insert(0, PATCHES)
import patchset  # noqa: E402

APPLY = os.path.join(PATCHES, "apply_patches.py")

DLL = bytes(range(256)) * 4
DLL_PATCHED = DLL[:0x10] + bytes.fromhex("eb689090") + DLL[0x14:0x1FF] + b"\xc3" + DLL[0x200:] + b"HDCC"
XML = (b'<Root>\r\n'
       b'  <ButtonDef ControlId="1021" Name="Optimize"/>\r\n'
       b'  <ButtonDef ControlId="1022" Name="Reset"/>\r\n'
       b'</Root>\r\n')
XML_PATCHED = (b'<Root>\r\n'
               b'  <ButtonDef ControlId="1022" Name="Reset"/>\r\n'
               b'</Root>\r\n')
SPLASH = b"stock splash"
SPLASH_NEW = b"HEARTH DAoC splash"
STRANGER = b"a file from another edition"
FILES = ["game.dll", "pregame/character_customize_stats.xml", "pregame/splash.mpk"]


def sha(data):
    return hashlib.sha256(data).hexdigest()


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)


def read(path):
    with open(path, "rb") as f:
        return f.read()


def fixture_patchset():
    return {
        "format": 1,
        "name": "classic-creation",
        "client": "test fixture",
        "files": [
            {"path": "game.dll", "before": sha(DLL), "after": sha(DLL_PATCHED),
             "ops": [{"op": "replace", "offset": 0x10, "from": "10111213", "to": "eb689090"},
                     {"op": "replace", "offset": 0x1FF, "from": "ff", "to": "c3"},
                     {"op": "append", "data": b"HDCC".hex()}]},
            {"path": "pregame/character_customize_stats.xml", "before": sha(XML), "after": sha(XML_PATCHED),
             "ops": [{"op": "text-replace", "find": '  <ButtonDef ControlId="1021" Name="Optimize"/>\r\n',
                      "replace": ""}]},
            {"path": "pregame/splash.mpk", "before": sha(SPLASH), "after": "source",
             "ops": [{"op": "file", "source": "splash.mpk"}]},
        ],
    }


def write_patchset(path, data):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1)
        f.write("\n")


# MPK archives (client/patches/mpk.py has the layout) of our own text, for mpk-text-replace.
def zlib_stored(data):
    """data as a zlib stream of stored blocks: 78 01, blocks of at most 65535 bytes (BFINAL on the last), Adler-32."""
    out = bytearray(b"\x78\x01")
    for start in range(0, len(data), 0xFFFF) if data else [0]:
        block = data[start:start + 0xFFFF]
        out += bytes([start + 0xFFFF >= len(data)]) + struct.pack("<HH", len(block), 0xFFFF - len(block)) + block
    return bytes(out + struct.pack(">I", zlib.adler32(data)))


def mpk_entry(name, content, ts=1191871001, flags=4, packed=None, name_field=None):
    """One file of an archive; its zlib stream defaults to level 9, as upstream's packer writes it."""
    return {"name_field": name_field or name.encode("latin-1").ljust(256, b"\0"), "ts": ts, "flags": flags,
            "content": content, "packed": zlib.compress(content, 9) if packed is None else packed}


def make_mpk(entries, name=b"test.mpk", cumulative=False, packed_dir=None):
    """An MPK archive. Each entry's mem field is 0, as in the client's own archives, or with cumulative the running
    total of the earlier entries' sizes, as upstream's packer writes it. packed_dir packs the directory (default:
    zlib level 9). An entry's "size" or "crc" overrides what its directory record says."""
    directory, area, total = bytearray(), bytearray(), 0
    for e in entries:
        directory += e["name_field"] + struct.pack("<IiIIIII", e["ts"], e["flags"], total if cumulative else 0,
                                                   e.get("size", len(e["content"])), len(area), len(e["packed"]),
                                                   e.get("crc", zlib.crc32(e["packed"])))
        total += len(e["content"])
        area += e["packed"]
    packed = (packed_dir or (lambda d: zlib.compress(d, 9)))(bytes(directory))
    stream = zlib.compress(name, 9)
    head = struct.pack("<IIII", zlib.crc32(packed), len(packed), len(stream), len(entries))
    return b"MPAK\x02" + bytes(b ^ i for i, b in enumerate(head)) + stream + packed + bytes(area)


def with_header(data, **values):
    """An archive with some of its header's values (crc, size_dir, size_name, count) replaced."""
    names = ("crc", "size_dir", "size_name", "count")
    head = dict(zip(names, struct.unpack("<IIII", bytes(b ^ i for i, b in enumerate(data[5:21])))), **values)
    return data[:5] + bytes(b ^ i for i, b in enumerate(struct.pack("<IIII", *(head[n] for n in names)))) + data[21:]


def parse_mpk(data):
    """(name stream, packed directory, [entry]) of an MPK archive; fails unless every CRC and size is right."""
    assert data[:5] == b"MPAK\x02", data[:5]
    crc, size_dir, size_name, count = struct.unpack("<IIII", bytes(b ^ i for i, b in enumerate(data[5:21])))
    stream, packed_dir = data[21:21 + size_name], data[21 + size_name:21 + size_name + size_dir]
    assert zlib.crc32(packed_dir) == crc
    directory, area = zlib.decompress(packed_dir), data[21 + size_name + size_dir:]
    assert len(directory) == 284 * count
    entries = []
    for k in range(count):
        raw = directory[284 * k:284 * (k + 1)]
        ts, flags, mem, size, offset, packed_size, entry_crc = struct.unpack("<IiIIIII", raw[256:])
        packed = area[offset:offset + packed_size]
        assert len(packed) == packed_size and zlib.crc32(packed) == entry_crc
        content = zlib.decompress(packed)
        assert len(content) == size
        entries.append({"name_field": raw[:256], "ts": ts, "flags": flags, "mem": mem, "offset": offset,
                        "packed": packed, "content": content})
    return stream, packed_dir, entries


def rebuilt(entries, changed, name=b"test.mpk"):
    """What mpk-text-replace must make of make_mpk(entries, name): the entries named in changed ({name: new
    content}) and the directory as stored blocks, every other entry's zlib stream as it was, mem the running
    total, and the name field, timestamp and flags of every entry kept."""
    out = []
    for e in entries:
        key = e["name_field"].split(b"\0")[0].decode("latin-1")
        out.append(dict(e, content=changed[key], packed=zlib_stored(changed[key])) if key in changed else e)
    return make_mpk(out, name, cumulative=True, packed_dir=zlib_stored)


PORTAL_ROW = "3,12,Our Portal Visual,500,600,0,90,100,1,0,0,1,0,0,3\r\n"
FIXTURES_CSV = ("1,10,Our Tr\xe9e,100,200,0,0,100,1,0,0,1,0,0,1\r\n"
                "2,11,Our Rock,300,400,0,0,100,1,0,0,1,0,0,2\r\n" + PORTAL_ROW).encode("latin-1")
NIF_ROW = "12,Our Portal Visual,ourportal.nif,0,0,255,0,1,1,0,0,100,100,0,0,0,0,0,0,0,0,0,0,\r\n"
NIFS_CSV = ("10,Our Tr\xe9e,ourtree.nif,0,0,255,0,1,1,0,0,100,100,0,0,0,0,0,0,0,0,0,0,\r\n" + NIF_ROW).encode("latin-1")
ZONEJUMP_CSV = b"1,Our Line,100,200,300,400,0,500,7,1\r\n"


def mpk_entries():
    return [mpk_entry("bound.csv", b"0,1,2,3\r\n" * 40), mpk_entry("fixtures.csv", FIXTURES_CSV),
            mpk_entry("NIFS.CSV", NIFS_CSV, ts=1059658602), mpk_entry("zonejump.csv", ZONEJUMP_CSV, ts=1191871052)]


def mpk_op(entry, find, replace=""):
    return {"op": "mpk-text-replace", "entry": entry, "find": find, "replace": replace}


# A file the "remove" operation moves aside ("after": "absent"), so the client loads another copy.
MODEL = b"the 2006 portal model"
MODEL_PATH = "Newtowns/zones/Nifs/model.npk"


def remove_set(**changes):
    entry = {"path": MODEL_PATH, "before": sha(MODEL), "after": "absent", "ops": [{"op": "remove"}]}
    return dict({"format": 1, "name": "portal-art", "client": "test fixture", "files": [entry]}, **changes)


class Fixture:
    """A stock client folder plus a bundle folder holding the patch set and splash.mpk."""

    def __init__(self, root, data=None):
        self.root = root
        self.client = os.path.join(root, "client")
        self.bundle = os.path.join(root, "bundle")
        self.patchset_path = os.path.join(self.bundle, "classic-creation.json")
        for rel, content in zip(FILES, (DLL, XML, SPLASH)):
            write(self.path(rel), content)
        write(os.path.join(self.bundle, "splash.mpk"), SPLASH_NEW)
        write_patchset(self.patchset_path, data or fixture_patchset())
        self.patchset = patchset.load(self.patchset_path)

    def path(self, rel):
        return os.path.join(self.client, *rel.split("/"))

    def backup(self, rel):
        return self.path(rel) + patchset.BACKUP_SUFFIX

    def snapshot(self):
        """{relative path: bytes} of everything under the client folder."""
        out = {}
        for folder, _dirs, names in os.walk(self.client):
            for name in names:
                full = os.path.join(folder, name)
                out[os.path.relpath(full, self.client)] = read(full)
        return out


class LoadTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.tmp.name, "set.json")

    def tearDown(self):
        self.tmp.cleanup()

    def load(self, data):
        write_patchset(self.path, data)
        return patchset.load(self.path)

    def test_loads_a_valid_patch_set(self):
        loaded = self.load(fixture_patchset())
        self.assertEqual([f["path"] for f in loaded["files"]], FILES)
        loaded = self.load(dict(fixture_patchset(), refused="(for example a file changed by hand)."))
        self.assertEqual(loaded["refused"], "(for example a file changed by hand).")

    def test_rejects_unsafe_paths(self):
        for bad in ("/etc/passwd", "../game.dll", "pregame/../../game.dll", "pregame\\splash.mpk",
                    "C:/game.dll", "pregame//splash.mpk", "./game.dll", "pregame/", ""):
            data = fixture_patchset()
            data["files"][0]["path"] = bad
            with self.subTest(path=bad), self.assertRaisesRegex(patchset.PatchError, "unsafe path"):
                self.load(data)

    def test_rejects_unsafe_bundled_source(self):
        for bad in ("../splash.mpk", "/tmp/splash.mpk"):
            data = fixture_patchset()
            data["files"][2]["ops"][0]["source"] = bad
            with self.subTest(source=bad), self.assertRaisesRegex(patchset.PatchError, "unsafe path"):
                self.load(data)

    def test_rejects_invalid_patch_sets(self):
        def first_op(d, **changes):
            d["files"][0]["ops"][0].update(changes)

        cases = {
            "format 2": lambda d: d.update(format=2),
            "no files": lambda d: d.update(files=[]),
            "unknown op": lambda d: first_op(d, op="delete"),
            "length change": lambda d: first_op(d, to="eb68"),
            "odd hex": lambda d: first_op(d, to="eb6890900"),
            "upper-case hex": lambda d: first_op(d, to="EB689090"),
            "negative offset": lambda d: first_op(d, offset=-1),
            "missing field": lambda d: d["files"][0]["ops"][0].pop("from"),
            "bad before hash": lambda d: d["files"][0].update(before="1234"),
            "before equals after": lambda d: d["files"][0].update(after=d["files"][0]["before"]),
            "source without a file op": lambda d: d["files"][0].update(after="source"),
            "duplicate path": lambda d: d["files"].append(dict(d["files"][0], path="GAME.DLL")),
            "empty find": lambda d: d["files"][1]["ops"][0].update(find=""),
            "text outside Latin-1": lambda d: d["files"][1]["ops"][0].update(replace="\u2014"),
            "refused not text": lambda d: d.update(refused=5),
            "refused null": lambda d: d.update(refused=None),
        }
        for name, change in cases.items():
            data = fixture_patchset()
            change(data)
            with self.subTest(name), self.assertRaises(patchset.PatchError):
                self.load(data)

    def test_loads_mpk_text_replace_remove_and_retired_sets(self):
        data = remove_set()
        data["files"].append({"path": "zones/zone000/csv000.mpk", "before": sha(b"a"), "after": sha(b"b"),
                              "ops": [mpk_op("fixtures.csv", PORTAL_ROW), mpk_op("NIFS.CSV", "\xe9", "e")]})
        loaded = self.load(data)
        self.assertEqual([f["after"] for f in loaded["files"]], ["absent", sha(b"b")])
        for retired in (True, False):
            self.assertIs(self.load(dict(data, retired=retired))["retired"], retired)

    def test_rejects_invalid_mpk_text_replace_remove_and_retired_sets(self):
        def mpk_set(**op):
            return {"format": 1, "files": [{"path": "csv000.mpk", "before": sha(b"a"), "after": sha(b"b"),
                                            "ops": [dict(mpk_op("fixtures.csv", PORTAL_ROW), **op)]}]}

        def absent(**entry):
            data = remove_set()
            data["files"][0].update(entry)
            return data

        cases = {
            "mpk entry missing": (lambda: {"format": 1, "files": [dict(mpk_set()["files"][0], ops=[
                {"op": "mpk-text-replace", "find": "x", "replace": ""}])]}, "csv000.mpk op 1: mpk-text-replace needs 'entry'"),
            "mpk entry not text": (lambda: mpk_set(entry=5), "csv000.mpk op 1: 'entry' must be an entry name"),
            "mpk entry empty": (lambda: mpk_set(entry=""), "csv000.mpk op 1: 'entry' must be an entry name"),
            "mpk entry with a NUL": (lambda: mpk_set(entry="a\0b"), "csv000.mpk op 1: 'entry' must be an entry name"),
            "mpk entry outside Latin-1": (lambda: mpk_set(entry="—.csv"), "'entry' must be an entry name"),
            "mpk entry too long": (lambda: mpk_set(entry="a" * 256), "'entry' must be an entry name"),
            "mpk find empty": (lambda: mpk_set(find=""), "csv000.mpk op 1: 'find' is empty"),
            "mpk replace not text": (lambda: mpk_set(replace=None), "csv000.mpk op 1: 'replace' must be text"),
            "mpk text outside Latin-1": (lambda: mpk_set(find="—"), "'find' has characters outside Latin-1"),
            "absent with two operations": (lambda: absent(ops=[{"op": "remove"}, {"op": "remove"}]),
                                           f"{MODEL_PATH}: \"after\": \"absent\" needs exactly one remove operation"),
            "absent with another operation": (lambda: absent(ops=[{"op": "append", "data": "00"}]),
                                              f"{MODEL_PATH}: \"after\": \"absent\" needs exactly one remove operation"),
            "remove without absent": (lambda: absent(after=sha(b"b")),
                                      f"{MODEL_PATH}: a remove operation needs \"after\": \"absent\""),
            "absent without a before hash": (lambda: absent(before="absent"),
                                             f"{MODEL_PATH}: 'before' must be a lower-case SHA-256"),
            "after in capitals": (lambda: absent(after="ABSENT"), "'after' must be a lower-case SHA-256, "
                                                                  "\"source\" or \"absent\""),
            "retired as text": (lambda: remove_set(retired="true"), "'retired' must be true or false"),
            "retired as a number": (lambda: remove_set(retired=1), "'retired' must be true or false"),
            "retired null": (lambda: remove_set(retired=None), "'retired' must be true or false"),
        }
        for name, (data, reason) in cases.items():
            with self.subTest(name), self.assertRaisesRegex(patchset.PatchError, re.escape(reason)):
                self.load(data())

    def test_rejects_unreadable_files(self):
        with open(self.path, "w", encoding="utf-8") as f:
            f.write("{ not json")
        with self.assertRaisesRegex(patchset.PatchError, "cannot read patch set"):
            patchset.load(self.path)
        with self.assertRaisesRegex(patchset.PatchError, "cannot read patch set"):
            patchset.load(os.path.join(self.tmp.name, "missing.json"))


class TransformTests(unittest.TestCase):
    def test_replace_swaps_the_expected_bytes(self):
        ops = [{"op": "replace", "offset": 1, "from": "0102", "to": "aabb"}]
        self.assertEqual(patchset.transform(b"\x00\x01\x02\x03", ops, None), b"\x00\xaa\xbb\x03")

    def test_replace_refuses_other_bytes(self):
        for offset in (0, 3):  # wrong bytes, and past the end
            ops = [{"op": "replace", "offset": offset, "from": "0102", "to": "aabb"}]
            with self.subTest(offset=offset), self.assertRaisesRegex(patchset.PatchError, "op 1"):
                patchset.transform(b"\x00\x01\x02\x03", ops, None)

    def test_append_adds_at_the_end(self):
        ops = [{"op": "append", "data": "cafe"}]
        self.assertEqual(patchset.transform(b"\x01", ops, None), b"\x01\xca\xfe")

    def test_ops_apply_in_order(self):
        ops = [{"op": "append", "data": "0000"}, {"op": "replace", "offset": 2, "from": "00", "to": "e9"}]
        self.assertEqual(patchset.transform(b"\x01\x02", ops, None), b"\x01\x02\xe9\x00")

    def test_text_replace_keeps_crlf_and_other_bytes(self):
        ops = fixture_patchset()["files"][1]["ops"]
        self.assertEqual(patchset.transform(XML, ops, None), XML_PATCHED)
        ops = [{"op": "text-replace", "find": "Optimize", "replace": "Reset"}]
        self.assertEqual(patchset.transform(b"caf\xe9 \xff\xfe Optimize\r\n", ops, None),
                         b"caf\xe9 \xff\xfe Reset\r\n")

    def test_text_replace_needs_exactly_one_match(self):
        ops = [{"op": "text-replace", "find": "Optimize", "replace": "Reset"}]
        for data in (b"Reset", b"Optimize Optimize"):
            with self.subTest(data=data), self.assertRaisesRegex(patchset.PatchError, "exactly once"):
                patchset.transform(data, ops, None)

    def test_file_takes_the_bundled_file(self):
        with tempfile.TemporaryDirectory() as bundle:
            write(os.path.join(bundle, "splash.mpk"), SPLASH_NEW)
            ops = [{"op": "file", "source": "splash.mpk"}]
            self.assertEqual(patchset.transform(SPLASH, ops, bundle), SPLASH_NEW)
            with self.assertRaisesRegex(patchset.PatchError, "bundled file missing"):
                patchset.transform(SPLASH, [{"op": "file", "source": "other.mpk"}], bundle)


class MpkTextReplaceTests(unittest.TestCase):
    """mpk-text-replace edits one file inside an MPK archive and rebuilds the archive the same way every time."""

    def setUp(self):
        self.entries = mpk_entries()
        self.data = make_mpk(self.entries)

    def transform(self, *ops, data=None):
        return patchset.transform(self.data if data is None else data, list(ops), None)

    def refused(self, reason, *ops, data=None):
        with self.assertRaises(patchset.PatchError) as cm:
            self.transform(*ops, data=data)
        self.assertEqual(str(cm.exception), reason)

    def test_removes_the_line_and_rebuilds_the_archive(self):
        out = self.transform(mpk_op("fixtures.csv", PORTAL_ROW))
        new = FIXTURES_CSV.replace(PORTAL_ROW.encode(), b"")
        self.assertEqual(out, rebuilt(self.entries, {"fixtures.csv": new}))
        stream, packed_dir, entries = parse_mpk(out)  # every CRC and size checks out
        self.assertEqual(stream, parse_mpk(self.data)[0])  # the archive's name, as it was
        self.assertEqual(packed_dir, zlib_stored(zlib.decompress(packed_dir)))
        self.assertEqual([e["content"] for e in entries], [self.entries[0]["content"], new, NIFS_CSV, ZONEJUMP_CSV])
        self.assertEqual([e["packed"] for e in entries],
                         [self.entries[0]["packed"], zlib_stored(new), self.entries[2]["packed"], self.entries[3]["packed"]])
        sizes = [len(e["content"]) for e in entries]
        self.assertEqual([e["mem"] for e in entries], [0, sizes[0], sum(sizes[:2]), sum(sizes[:3])])
        lengths = [len(e["packed"]) for e in entries]
        self.assertEqual([e["offset"] for e in entries], [0, lengths[0], sum(lengths[:2]), sum(lengths[:3])])
        self.assertEqual([(e["name_field"], e["ts"], e["flags"]) for e in entries],
                         [(e["name_field"], e["ts"], e["flags"]) for e in self.entries])

    def test_the_rebuild_is_the_same_every_time(self):
        op = mpk_op("fixtures.csv", PORTAL_ROW)
        self.assertEqual(self.transform(op), self.transform(op))
        # Whatever the archive's own packer wrote (here the running total in mem, and a level 1 directory).
        other = make_mpk(self.entries, cumulative=True, packed_dir=lambda d: zlib.compress(d, 1))
        self.assertEqual(self.transform(op, data=other), self.transform(op))

    def test_two_operations_give_the_bytes_of_one_rebuild(self):
        out = self.transform(mpk_op("fixtures.csv", PORTAL_ROW), mpk_op("NIFS.CSV", NIF_ROW))
        self.assertEqual(out, rebuilt(self.entries, {"fixtures.csv": FIXTURES_CSV.replace(PORTAL_ROW.encode(), b""),
                                                     "NIFS.CSV": NIFS_CSV.replace(NIF_ROW.encode(), b"")}))

    def test_stored_blocks_at_0_1_65535_and_65536_bytes(self):
        tail = "the line to remove\r\n"
        for size in (0, 1, 65535, 65536):
            with self.subTest(size=size):
                body = bytes(i * 7 % 256 for i in range(size))
                data = make_mpk([mpk_entry("big.csv", body + tail.encode())])
                packed = parse_mpk(self.transform(mpk_op("big.csv", tail), data=data))[2][0]["packed"]
                self.assertEqual(zlib.decompress(packed), body)
                self.assertEqual(packed, zlib_stored(body))
                self.assertEqual(packed[-4:], struct.pack(">I", zlib.adler32(body)))
                if size == 0:
                    self.assertEqual(packed, bytes.fromhex("7801" "010000ffff" "00000001"))
                elif size == 1:
                    self.assertEqual(packed, bytes.fromhex("7801" "010100feff" "00" "00010001"))
                elif size == 65535:
                    self.assertEqual(packed[:7], bytes.fromhex("7801" "01ffff0000"))
                    self.assertEqual(len(packed), 2 + 5 + 65535 + 4)
                else:
                    self.assertEqual(packed[:7], bytes.fromhex("7801" "00ffff0000"))
                    self.assertEqual(packed[7 + 65535:7 + 65535 + 5], bytes.fromhex("010100feff"))
                    self.assertEqual(len(packed), 2 + 5 + 65535 + 5 + 1 + 4)

    def test_the_entry_is_named_exactly_once(self):
        self.refused("op 1 (mpk-text-replace): the archive has 0 entries named FIXTURES.CSV, not exactly one",
                     mpk_op("FIXTURES.CSV", PORTAL_ROW))
        twice = make_mpk(self.entries + [mpk_entry("fixtures.csv", FIXTURES_CSV)])
        self.refused("op 1 (mpk-text-replace): the archive has 2 entries named fixtures.csv, not exactly one",
                     mpk_op("fixtures.csv", PORTAL_ROW), data=twice)

    def test_the_text_must_occur_exactly_once_in_that_entry(self):
        for find, count in (("Our Wall", 0), ("ourportal.nif", 0), ("Our Portal Visual", 1), (",0,0,1,0,0,", 3)):
            op = mpk_op("fixtures.csv", find)
            if count == 1:
                self.transform(op)
                continue
            with self.subTest(find=find):
                self.refused(f"op 1 (mpk-text-replace): found the text {count} times in fixtures.csv, not exactly once",
                             op)
        self.refused("op 2 (mpk-text-replace): found the text 0 times in fixtures.csv, not exactly once",
                     mpk_op("fixtures.csv", PORTAL_ROW), mpk_op("fixtures.csv", PORTAL_ROW))

    def test_refuses_a_file_that_is_not_an_mpk_archive(self):
        for data in (b"", b"PK\x03\x04 a zip file", b"MPAK\x02" + bytes(15), b"MPAK\x01" + self.data[5:]):
            with self.subTest(data=data[:8]):
                self.refused("op 1 (mpk-text-replace): not an MPK archive", mpk_op("fixtures.csv", PORTAL_ROW),
                             data=data)

    def test_refuses_a_damaged_archive(self):
        at = 21 + len(zlib.compress(b"test.mpk", 9)) + 3  # a byte of the packed directory
        damaged = {
            "a directory byte": self.data[:at] + bytes([self.data[at] ^ 1]) + self.data[at + 1:],
            "a cut file": self.data[:-3],
            "a wrong file count": with_header(self.data, count=5),
            "a wrong directory size": with_header(self.data, size_dir=40),
            "sizes past the end": with_header(self.data, size_name=1 << 30),
        }
        for name, data in damaged.items():
            with self.subTest(name):
                self.refused("op 1 (mpk-text-replace): the MPK archive is damaged", mpk_op("fixtures.csv", PORTAL_ROW),
                             data=data)

    def test_refuses_a_damaged_entry(self):
        damaged = {
            "its CRC-32": dict(self.entries[1], crc=12345),
            "its zlib stream": dict(self.entries[1], packed=b"\x78\x01\x07" + self.entries[1]["packed"][3:]),
            "its size": dict(self.entries[1], size=len(FIXTURES_CSV) + 1),
            "a cut stream": dict(self.entries[1], packed=self.entries[1]["packed"][:40]),
        }
        for name, entry in damaged.items():
            with self.subTest(name):
                data = make_mpk([self.entries[0], entry] + self.entries[2:])
                self.refused("op 1 (mpk-text-replace): fixtures.csv in the archive is damaged",
                             mpk_op("fixtures.csv", PORTAL_ROW), data=data)

    def test_keeps_the_name_field_after_its_nul_and_every_latin1_byte(self):
        field = b"fixtures.csv\0old bytes".ljust(256, b"\0")
        entries = [mpk_entry("", FIXTURES_CSV, name_field=field)]
        out = self.transform(mpk_op("fixtures.csv", "Our Tr\xe9e", "Our Tr\xe8e"), data=make_mpk(entries))
        [entry] = parse_mpk(out)[2]
        self.assertEqual(entry["name_field"], field)
        self.assertEqual(entry["content"], FIXTURES_CSV.replace(b"Our Tr\xe9e", b"Our Tr\xe8e"))


class RemoveTests(unittest.TestCase):
    """A file whose "after" is "absent" is patched by moving it over its backup, and restored by moving it back."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.client = os.path.join(self.tmp.name, "client")
        self.model = os.path.join(self.client, *MODEL_PATH.split("/"))
        self.backup = self.model + patchset.BACKUP_SUFFIX
        write(self.model, MODEL)
        path = os.path.join(self.tmp.name, "portal-art.json")
        write_patchset(path, remove_set())
        self.patchset = patchset.load(path)

    def place(self, model, backup):
        """Put the model and its backup in a state: bytes, or None for no file."""
        for path, data in ((self.model, model), (self.backup, backup)):
            if data is None:
                if os.path.exists(path):
                    os.remove(path)
            else:
                write(path, data)

    def snapshot(self):
        return {name: read(os.path.join(os.path.dirname(self.model), name))
                for name in sorted(os.listdir(os.path.dirname(self.model)))}

    def test_status(self):
        for model, state in ((MODEL, "unpatched"), (None, "patched"), (STRANGER, "unknown")):
            with self.subTest(state):
                self.place(model, None)
                self.assertEqual(patchset.status(self.client, self.patchset, None), [(MODEL_PATH, state)])

    def test_apply_moves_the_file_over_its_backup(self):
        inode = os.stat(self.model).st_ino
        self.assertEqual(patchset.apply(self.client, self.patchset, None), [(MODEL_PATH, "patched")])
        self.assertFalse(os.path.exists(self.model))
        self.assertEqual(read(self.backup), MODEL)
        self.assertEqual(os.stat(self.backup).st_ino, inode)  # renamed, never written
        self.assertEqual(patchset.apply(self.client, self.patchset, None), [(MODEL_PATH, "already")])
        self.assertEqual(self.snapshot(), {"model.npk.hearthdaoc-orig": MODEL})

    def test_a_client_without_the_file_counts_as_patched(self):
        self.place(None, None)
        self.assertEqual(patchset.apply(self.client, self.patchset, None), [(MODEL_PATH, "already")])
        self.assertEqual(self.snapshot(), {})

    def test_a_wrong_backup_is_replaced_by_the_verified_original(self):
        write(self.backup, STRANGER)
        inode = os.stat(self.model).st_ino
        self.assertEqual(patchset.apply(self.client, self.patchset, None), [(MODEL_PATH, "patched")])
        self.assertEqual(self.snapshot(), {"model.npk.hearthdaoc-orig": MODEL})
        self.assertEqual(os.stat(self.backup).st_ino, inode)

    def test_an_unknown_file_is_refused_and_nothing_changes(self):
        self.place(STRANGER, None)
        with self.assertRaisesRegex(patchset.PatchError, re.escape(f"refused, nothing changed: {MODEL_PATH} (unknown)")):
            patchset.apply(self.client, self.patchset, None)
        self.assertEqual(self.snapshot(), {"model.npk": STRANGER})

    def test_restore_status_reports_each_state(self):
        cases = [((None, MODEL), "patched"), ((None, None), "no-backup"), ((None, STRANGER), "wrong-backup"),
                 ((MODEL, MODEL), "original"), ((MODEL, None), "no-backup"), ((MODEL, STRANGER), "wrong-backup"),
                 ((STRANGER, MODEL), "changed"), ((STRANGER, None), "no-backup"), ((STRANGER, STRANGER), "wrong-backup")]
        for (model, backup), state in cases:
            with self.subTest(model=model, backup=backup):
                self.place(model, backup)
                self.assertEqual(patchset.restore_status(self.client, self.patchset, None), [(MODEL_PATH, state)])

    def test_restore_moves_the_backup_back(self):
        patchset.apply(self.client, self.patchset, None)
        inode = os.stat(self.backup).st_ino
        self.assertEqual(patchset.restore(self.client, self.patchset, None), [(MODEL_PATH, "restored")])
        self.assertEqual(self.snapshot(), {"model.npk": MODEL})
        self.assertEqual(os.stat(self.model).st_ino, inode)
        self.assertEqual(patchset.restore(self.client, self.patchset, None), [(MODEL_PATH, "not-patched")])

    def test_restore_keeps_the_backup_of_a_file_that_already_is_the_original(self):
        self.place(MODEL, MODEL)  # e.g. a repair of the install put the file back
        self.assertEqual(patchset.restore(self.client, self.patchset, None), [(MODEL_PATH, "not-patched")])
        self.assertEqual(self.snapshot(), {"model.npk": MODEL, "model.npk.hearthdaoc-orig": MODEL})

    def test_restore_refuses_a_changed_file_or_a_wrong_backup_and_changes_nothing(self):
        for (model, backup), reason in (((STRANGER, MODEL), f"changed since it was patched: {MODEL_PATH}"),
                                        ((None, STRANGER), f"not the original file: {MODEL_PATH}.hearthdaoc-orig")):
            with self.subTest(reason):
                self.place(model, backup)
                before = self.snapshot()
                with self.assertRaises(patchset.PatchError) as cm:
                    patchset.restore(self.client, self.patchset, None)
                self.assertEqual(str(cm.exception), reason)
                self.assertEqual(self.snapshot(), before)


class ApplyTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.fx = Fixture(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def apply(self):
        return patchset.apply(self.fx.client, self.fx.patchset, self.fx.bundle)

    def test_status_reports_each_state(self):
        fx = self.fx
        self.assertEqual(patchset.status(fx.client, fx.patchset, fx.bundle), [(f, "unpatched") for f in FILES])
        write(fx.path("game.dll"), STRANGER)
        os.remove(fx.path(FILES[1]))
        write(fx.path(FILES[2]), SPLASH_NEW)
        self.assertEqual(patchset.status(fx.client, fx.patchset, fx.bundle),
                         [(FILES[0], "unknown"), (FILES[1], "missing"), (FILES[2], "patched")])

    def test_apply_patches_every_file_and_backs_up_the_originals(self):
        self.assertEqual(self.apply(), [(f, "patched") for f in FILES])
        for rel, old, new in zip(FILES, (DLL, XML, SPLASH), (DLL_PATCHED, XML_PATCHED, SPLASH_NEW)):
            self.assertEqual(read(self.fx.path(rel)), new, rel)
            self.assertEqual(read(self.fx.backup(rel)), old, rel)

    def test_already_patched_files_are_skipped(self):
        self.apply()
        before = self.fx.snapshot()
        self.assertEqual(self.apply(), [(f, "already") for f in FILES])
        self.assertEqual(self.fx.snapshot(), before)

    def test_only_unpatched_files_are_written(self):
        write(self.fx.path("game.dll"), DLL_PATCHED)
        self.assertEqual(self.apply(), [(FILES[0], "already"), (FILES[1], "patched"), (FILES[2], "patched")])
        self.assertFalse(os.path.exists(self.fx.backup("game.dll")))

    def test_refuses_unknown_and_missing_files_and_changes_nothing(self):
        write(self.fx.path(FILES[2]), STRANGER)  # the last file, so game.dll would come first
        os.remove(self.fx.path(FILES[1]))
        before = self.fx.snapshot()
        with self.assertRaises(patchset.PatchError) as cm:
            self.apply()
        self.assertIn("pregame/character_customize_stats.xml (missing)", str(cm.exception))
        self.assertIn("pregame/splash.mpk (unknown)", str(cm.exception))
        self.assertEqual(self.fx.snapshot(), before)

    def test_wrong_result_changes_nothing(self):
        data = fixture_patchset()
        data["files"][1]["after"] = sha(b"something else")
        shutil.rmtree(self.fx.root)
        os.makedirs(self.fx.root)
        self.fx = Fixture(self.fx.root, data)
        before = self.fx.snapshot()
        with self.assertRaisesRegex(patchset.PatchError, "expected SHA-256"):
            self.apply()
        self.assertEqual(self.fx.snapshot(), before)

    def test_backup_is_made_once(self):
        self.apply()
        first = os.stat(self.fx.backup("game.dll"))
        write(self.fx.path("game.dll"), DLL)  # e.g. a launcher put the stock file back
        self.assertEqual(self.apply()[0], ("game.dll", "patched"))
        again = os.stat(self.fx.backup("game.dll"))
        self.assertEqual((again.st_ino, again.st_mtime_ns), (first.st_ino, first.st_mtime_ns))
        self.assertEqual(read(self.fx.backup("game.dll")), DLL)

    def test_a_stale_backup_is_replaced_by_the_verified_original(self):
        write(self.fx.backup("game.dll"), STRANGER)
        self.apply()
        self.assertEqual(read(self.fx.backup("game.dll")), DLL)

    def test_writes_go_through_a_temporary_file_and_os_replace(self):
        real_replace = os.replace
        calls = []

        def record(src, dst):
            calls.append((src, dst))
            return real_replace(src, dst)

        with mock.patch.object(patchset.os, "replace", side_effect=record):
            self.apply()
        targets = [dst for _src, dst in calls]
        for rel in FILES:
            self.assertIn(self.fx.path(rel), targets)
            self.assertIn(self.fx.backup(rel), targets)
        for src, dst in calls:
            self.assertEqual(os.path.dirname(src), os.path.dirname(dst))
            self.assertNotEqual(src, dst)
        expected = sorted(FILES + [f + patchset.BACKUP_SUFFIX for f in FILES])
        self.assertEqual(sorted(self.fx.snapshot()), expected)  # no temporary file is left behind

    def test_a_failed_write_leaves_the_file_intact(self):
        real_replace = os.replace
        target = self.fx.path("game.dll")

        def fail_on_target(src, dst):
            if dst == target:
                raise OSError("disk full")
            return real_replace(src, dst)

        with mock.patch.object(patchset.os, "replace", side_effect=fail_on_target):
            with self.assertRaisesRegex(OSError, "disk full"):
                self.apply()
        self.assertEqual(read(target), DLL)
        self.assertEqual(sorted(os.listdir(self.fx.client)), ["game.dll", "game.dll.hearthdaoc-orig", "pregame"])


class RestoreTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.fx = Fixture(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def restore(self):
        return patchset.restore(self.fx.client, self.fx.patchset, self.fx.bundle)

    def test_restore_puts_the_originals_back(self):
        fx = self.fx
        patchset.apply(fx.client, fx.patchset, fx.bundle)
        self.assertEqual(self.restore(), [(f, "restored") for f in FILES])
        for rel, old in zip(FILES, (DLL, XML, SPLASH)):
            self.assertEqual(read(fx.path(rel)), old, rel)
            self.assertFalse(os.path.exists(fx.backup(rel)), rel)
        self.assertEqual(self.restore(), [(f, "not-patched") for f in FILES])

    def test_restore_status_reports_each_state(self):
        fx = self.fx
        self.assertEqual(patchset.restore_status(fx.client, fx.patchset, fx.bundle), [(f, "no-backup") for f in FILES])
        patchset.apply(fx.client, fx.patchset, fx.bundle)
        self.assertEqual(patchset.restore_status(fx.client, fx.patchset, fx.bundle), [(f, "patched") for f in FILES])
        write(fx.path("game.dll"), DLL)
        os.remove(fx.path(FILES[1]))
        write(fx.path(FILES[2]), STRANGER)
        self.assertEqual(patchset.restore_status(fx.client, fx.patchset, fx.bundle),
                         [(FILES[0], "original"), (FILES[1], "missing"), (FILES[2], "changed")])
        write(fx.backup("game.dll"), STRANGER)
        self.assertEqual(patchset.restore_status(fx.client, fx.patchset, fx.bundle)[0], (FILES[0], "wrong-backup"))

    def test_restore_refuses_a_wrong_backup_and_changes_nothing(self):
        fx = self.fx
        patchset.apply(fx.client, fx.patchset, fx.bundle)
        write(fx.backup(FILES[2]), STRANGER)
        before = fx.snapshot()
        with self.assertRaisesRegex(patchset.PatchError, "pregame/splash.mpk.hearthdaoc-orig"):
            self.restore()
        self.assertEqual(fx.snapshot(), before)

    def test_restore_refuses_files_that_are_not_the_patched_ones_and_changes_nothing(self):
        fx = self.fx
        patchset.apply(fx.client, fx.patchset, fx.bundle)
        write(fx.path("game.dll"), STRANGER)  # e.g. a newer client installed over the patched one
        os.remove(fx.path(FILES[1]))
        before = fx.snapshot()
        with self.assertRaises(patchset.PatchError) as cm:
            self.restore()
        self.assertEqual(str(cm.exception), f"changed since it was patched: game.dll; missing: {FILES[1]}")
        self.assertEqual(fx.snapshot(), before)  # splash.mpk, still the patched one, isn't restored either

    def test_a_file_that_is_already_the_original_keeps_its_backup(self):
        fx = self.fx
        patchset.apply(fx.client, fx.patchset, fx.bundle)
        write(fx.path("game.dll"), DLL)  # e.g. a launcher put the stock file back
        self.assertEqual(self.restore(), [(FILES[0], "not-patched"), (FILES[1], "restored"), (FILES[2], "restored")])
        self.assertEqual(read(fx.path("game.dll")), DLL)
        self.assertEqual(read(fx.backup("game.dll")), DLL)  # kept: the next patch run uses it again


UNKNOWN_MESSAGE = ("Not patched: {path} is not the file this HearthDAoC release supports (for example the "
                   "b edition, or an older or newer upstream client). The client still works with the standard "
                   "creation screen.\n")
CHANGED_MESSAGE = ("Not restored: {path} has changed since it was patched (for example a newer client was "
                   "installed); the saved original is kept as {path}.hearthdaoc-orig.\n")


class CliTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.fx = Fixture(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def run_cli(self, *args, script=APPLY):
        return subprocess.run([sys.executable, script, *args], capture_output=True, text=True)

    def run_fx(self, *args):
        return self.run_cli("--client", self.fx.client, "--patchset", self.fx.patchset_path, *args)

    def test_check_apply_and_restore(self):
        r = self.run_fx("--check")
        self.assertEqual((r.returncode, r.stdout), (0, "".join(f"{f}: unpatched\n" for f in FILES)), r.stderr)
        r = self.run_fx()
        self.assertEqual((r.returncode, r.stdout), (0, "".join(
            f"Patched: {f} (original saved as {f}.hearthdaoc-orig)\n" for f in FILES)), r.stderr)
        self.assertEqual(read(self.fx.path("game.dll")), DLL_PATCHED)
        r = self.run_fx()
        self.assertEqual((r.returncode, r.stdout), (0, "".join(f"Already patched: {f}\n" for f in FILES)), r.stderr)
        r = self.run_fx("--check")
        self.assertEqual((r.returncode, r.stdout), (0, "".join(f"{f}: patched\n" for f in FILES)), r.stderr)
        r = self.run_fx("--restore")
        self.assertEqual((r.returncode, r.stdout), (0, "".join(f"Restored: {f}\n" for f in FILES)), r.stderr)
        self.assertEqual(read(self.fx.path("game.dll")), DLL)
        r = self.run_fx("--restore")
        self.assertEqual((r.returncode, r.stdout), (0, "".join(f"Nothing to restore: {f}\n" for f in FILES)), r.stderr)

    def test_unknown_file_is_refused_with_exit_3(self):
        write(self.fx.path("game.dll"), STRANGER)
        before = self.fx.snapshot()
        r = self.run_fx()
        self.assertEqual((r.returncode, r.stdout), (3, UNKNOWN_MESSAGE.format(path="game.dll")), r.stderr)
        self.assertEqual(self.fx.snapshot(), before)
        r = self.run_fx("--check")
        self.assertEqual(r.returncode, 3)
        self.assertIn("game.dll: unknown\n", r.stdout)

    def test_missing_file_is_refused_with_exit_3(self):
        os.remove(self.fx.path(FILES[1]))
        before = self.fx.snapshot()
        r = self.run_fx()
        self.assertEqual((r.returncode, r.stdout),
                         (3, f"Not patched: {FILES[1]} is missing from the client folder.\n"), r.stderr)
        self.assertEqual(self.fx.snapshot(), before)

    def test_wrong_backup_is_not_restored_with_exit_3(self):
        self.assertEqual(self.run_fx().returncode, 0)
        write(self.fx.backup("game.dll"), STRANGER)
        before = self.fx.snapshot()
        r = self.run_fx("--restore")
        self.assertEqual((r.returncode, r.stdout), (3, "Not restored: not the original file: "
                                                       "game.dll.hearthdaoc-orig. Nothing was changed.\n"))
        self.assertEqual(self.fx.snapshot(), before)

    def test_a_file_changed_since_patching_is_not_restored_with_exit_3(self):
        self.assertEqual(self.run_fx().returncode, 0)
        write(self.fx.path("game.dll"), STRANGER)  # e.g. a newer client installed over the patched one
        before = self.fx.snapshot()
        r = self.run_fx("--restore")
        self.assertEqual((r.returncode, r.stdout), (3, CHANGED_MESSAGE.format(path="game.dll")), r.stderr)
        self.assertEqual(self.fx.snapshot(), before)
        write(self.fx.path("game.dll"), DLL)  # the original again: nothing to restore, and its backup stays
        r = self.run_fx("--restore")
        self.assertEqual((r.returncode, r.stdout), (0, "Nothing to restore: game.dll\n" + "".join(
            f"Restored: {f}\n" for f in FILES[1:])), r.stderr)
        self.assertEqual(read(self.fx.backup("game.dll")), DLL)

    def test_bad_usage_and_invalid_patch_sets_exit_2(self):
        r = self.run_cli()
        self.assertEqual(r.returncode, 2)
        self.assertIn("the following arguments are required: --client", r.stderr)
        r = self.run_fx("--restore", "--check")
        self.assertEqual(r.returncode, 2)
        self.assertIn("not allowed with argument", r.stderr)
        missing = os.path.join(self.tmp.name, "nowhere")
        r = self.run_cli("--client", missing, "--patchset", self.fx.patchset_path)
        self.assertEqual((r.returncode, r.stderr), (2, f"Error: client folder not found: {missing}\n"))
        data = fixture_patchset()
        data["format"] = 2
        write_patchset(self.fx.patchset_path, data)
        r = self.run_fx()
        self.assertEqual((r.returncode, r.stderr),
                         (2, f"Error: invalid patch set: {self.fx.patchset_path}: not a format 1 patch set\n"))
        write_patchset(self.fx.patchset_path, fixture_patchset())
        empty = os.path.join(self.tmp.name, "empty")
        os.makedirs(empty)
        before = self.fx.snapshot()
        r = self.run_fx("--bundle", empty)
        self.assertEqual((r.returncode, r.stderr),
                         (2, f"Error: bundled file missing: {os.path.join(empty, 'splash.mpk')}\n"))
        self.assertEqual(self.fx.snapshot(), before)

    def test_defaults_to_the_patch_set_and_bundle_next_to_the_script(self):
        tool = os.path.join(self.tmp.name, "tool")
        os.makedirs(tool)
        for name in ("apply_patches.py", "patchset.py"):
            shutil.copy(os.path.join(PATCHES, name), tool)
        for name in ("classic-creation.json", "splash.mpk"):
            shutil.copy(os.path.join(self.fx.bundle, name), tool)
        os.remove(os.path.join(self.fx.bundle, "splash.mpk"))
        r = self.run_cli("--client", self.fx.client, script=os.path.join(tool, "apply_patches.py"))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(read(self.fx.path(FILES[2])), SPLASH_NEW)
        self.assertFalse(os.path.exists(os.path.join(tool, "__pycache__")))
