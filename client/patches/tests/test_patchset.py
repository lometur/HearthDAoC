"""Tests for the patch-set library (patchset.py) and the Linux applier (apply_patches.py).

Every test builds its own fixture client folder, bundle folder and patch set in a temporary
directory, so no game file is needed.
"""
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
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
        }
        for name, change in cases.items():
            data = fixture_patchset()
            change(data)
            with self.subTest(name), self.assertRaises(patchset.PatchError):
                self.load(data)

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
                   "0.34b edition or a newer upstream client). The client still works with the standard "
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
