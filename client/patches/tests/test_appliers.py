"""The same command-line cases against both client appliers.

apply_patches.py (Linux) and client/windows/patch-client.ps1 (Windows) must follow the same rules
and print the same lines with the same exit codes, so every case here runs against both. The
PowerShell run needs pwsh (PowerShell 7; GitHub's Ubuntu runners have it) and is skipped without
it. HDC_PWSH may name the pwsh executable. Players run the script with Windows PowerShell 5.1,
so WindowsFilesTests also checks the script for the usual PowerShell 7-only slips.
"""
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

from tests.test_layout import STOCK_INI, STOCK_INI_PATCHED, fixture_layout_set
from tests.test_patchset import (CHANGED_MESSAGE, DLL, DLL_PATCHED, FILES, FIXTURES_CSV, MODEL, MODEL_PATH, NIF_ROW,
                                 NIFS_CSV, PORTAL_ROW, SPLASH, SPLASH_NEW, STRANGER, UNKNOWN_MESSAGE, XML, XML_PATCHED,
                                 Fixture, fixture_patchset, make_mpk, mpk_entries, mpk_entry, mpk_op, read, rebuilt,
                                 remove_set, sha, with_header, write, write_patchset)

HERE = os.path.dirname(os.path.abspath(__file__))
PATCHES = os.path.dirname(HERE)
WINDOWS = os.path.join(os.path.dirname(PATCHES), "windows")
APPLY_PY = os.path.join(PATCHES, "apply_patches.py")
PS1 = os.path.join(WINDOWS, "patch-client.ps1")
BAT = os.path.join(WINDOWS, "patch-client.bat")
PWSH = os.environ.get("HDC_PWSH") or shutil.which("pwsh")
MISSING_MESSAGE = "Not patched: {path} is missing from the client folder.\n"
GONE_MESSAGE = ("Not restored: {path} is missing from the client folder; the saved original is kept as "
                "{path}.hearthdaoc-orig.\n")
PATCHED = "".join(f"Patched: {f} (original saved as {f}.hearthdaoc-orig)\n" for f in FILES)
RESTORED = "".join(f"Restored: {f}\n" for f in FILES)
# A usual Windows install folder, C:\Program Files (x86)\Offline DAoC v0.34, has spaces and parentheses.
INSTALL = ("Program Files (x86)", "Offline DAoC v0.34", "runtime", "client-opendaoc")


def _file(index, **changes):
    return lambda d: d["files"][index].update(changes)


def _op(index, **changes):
    return lambda d: d["files"][index]["ops"][0].update(changes)


SHA_RULE = "game.dll: 'after' must be a lower-case SHA-256, \"source\" or \"absent\""
OFFSET_RULE = "game.dll op 1: offset must be a whole number, 0 or more"
HEX_RULE = "game.dll op 1: not lower-case hex bytes"
XML_OP = "pregame/character_customize_stats.xml op 1"

# An MPK archive that mpk-text-replace edits twice, and a file that remove moves aside: the Shrouded Isles sets'
# shapes (si-portals.json, si-portal-art.json), with our own text.
ARCHIVE = "zones/zone000/csv000.mpk"
ARCHIVE_DATA = make_mpk(mpk_entries())
ARCHIVE_PATCHED = rebuilt(mpk_entries(), {"fixtures.csv": FIXTURES_CSV.replace(PORTAL_ROW.encode(), b""),
                                          "NIFS.CSV": NIFS_CSV.replace(NIF_ROW.encode(), b"")})
SI_FILES = [ARCHIVE, MODEL_PATH]
ENTRY_RULE = "'entry' must be an entry name (1 to 255 Latin-1 characters, no NUL)"


def archive_entry(data=ARCHIVE_DATA, after=ARCHIVE_PATCHED, ops=None):
    return {"path": ARCHIVE, "before": sha(data), "after": sha(after),
            "ops": ops or [mpk_op("fixtures.csv", PORTAL_ROW), mpk_op("NIFS.CSV", NIF_ROW)]}


def fixture_si_set(**changes):
    data = remove_set(name="si-test")
    data["files"].insert(0, archive_entry())
    return dict(data, **changes)


def _added(**op):
    """The fixture patch set with an archive entry whose operation is mpk_op's with these changes."""
    return lambda d: d["files"].append(archive_entry(ops=[dict(mpk_op("fixtures.csv", PORTAL_ROW), **op)]))


def _absent(**changes):
    return lambda d: d["files"].append(dict(remove_set()["files"][0], **changes))

# name: (change to the fixture patch set, reason printed after "Error: invalid patch set: ")
INVALID = {
    "format 2": (lambda d: d.update(format=2), "{patchset}: not a format 1 patch set"),
    "format as text": (lambda d: d.update(format="1"), "{patchset}: not a format 1 patch set"),
    "no files": (lambda d: d.update(files=[]), "{patchset}: the patch set lists no files"),
    "file entry not an object": (lambda d: d.update(files=[5]), "file 1: not an object"),
    "absolute path": (_file(0, path="/etc/passwd"), "unsafe path: '/etc/passwd'"),
    "path leaving the folder": (_file(0, path="pregame/../../game.dll"), "unsafe path: 'pregame/../../game.dll'"),
    "drive letter": (_file(0, path="C:/game.dll"), "unsafe path: 'C:/game.dll'"),
    "empty path part": (_file(0, path="pregame//splash.mpk"), "unsafe path: 'pregame//splash.mpk'"),
    "field name in capitals": (lambda d: d["files"][0].update(Path=d["files"][0].pop("path")), "unsafe path: None"),
    "duplicate path": (lambda d: d["files"].append(dict(d["files"][0], path="GAME.DLL")), "GAME.DLL: listed twice"),
    "bad before hash": (_file(0, before="1234"), "game.dll: 'before' must be a lower-case SHA-256"),
    "upper-case after hash": (lambda d: d["files"][0].update(after=d["files"][0]["after"].upper()), SHA_RULE),
    "before equals after": (lambda d: d["files"][0].update(after=d["files"][0]["before"]),
                            "game.dll: 'before' and 'after' are the same"),
    "no operations": (_file(0, ops=[]), "game.dll: no operations"),
    "unknown op": (_op(0, op="delete"), "game.dll op 1: unknown operation"),
    "op name in capitals": (_op(0, op="REPLACE"), "game.dll op 1: unknown operation"),
    "op not text": (_op(0, op=[]), "game.dll op 1: unknown operation"),
    "missing field": (lambda d: d["files"][0]["ops"][0].pop("from"), "game.dll op 1: replace needs 'from'"),
    "negative offset": (_op(0, offset=-1), OFFSET_RULE),
    "offset as text": (_op(0, offset="16"), OFFSET_RULE),
    "fractional offset": (_op(0, offset=1.5), OFFSET_RULE),
    "odd hex": (_op(0, to="eb6890900"), HEX_RULE),
    "upper-case hex": (_op(0, to="EB689090"), HEX_RULE),
    "length change": (_op(0, to="eb68"), "game.dll op 1: 'from' and 'to' must have the same length"),
    "bad append data": (lambda d: d["files"][0]["ops"][2].update(data="xyz0"),
                        "game.dll op 3: not lower-case hex bytes"),
    "empty find": (_op(1, find=""), f"{XML_OP}: 'find' is empty"),
    "find not text": (_op(1, find=5), f"{XML_OP}: 'find' must be text"),
    "text outside Latin-1": (_op(1, replace="\u2014"), f"{XML_OP}: 'replace' has characters outside Latin-1"),
    "text that case-folds into Latin-1": (_op(1, replace="\u0178"),
                                          f"{XML_OP}: 'replace' has characters outside Latin-1"),
    "unsafe bundled source": (_op(2, source="../splash.mpk"), "unsafe path: '../splash.mpk'"),
    "source without a file op": (_file(0, after="source"),
                                 "game.dll: \"after\": \"source\" needs exactly one file operation"),
    "refused not text": (lambda d: d.update(refused=["x"]), "{patchset}: 'refused' must be text"),
    "refused null": (lambda d: d.update(refused=None), "{patchset}: 'refused' must be text"),
    "mpk entry missing": (lambda d: d["files"].append(archive_entry(ops=[{"op": "mpk-text-replace", "find": "x",
                                                                          "replace": ""}])),
                          f"{ARCHIVE} op 1: mpk-text-replace needs 'entry'"),
    "mpk entry not text": (_added(entry=5), f"{ARCHIVE} op 1: {ENTRY_RULE}"),
    "mpk entry empty": (_added(entry=""), f"{ARCHIVE} op 1: {ENTRY_RULE}"),
    "mpk entry with a NUL": (_added(entry="a\0b"), f"{ARCHIVE} op 1: {ENTRY_RULE}"),
    "mpk entry too long": (_added(entry="a" * 256), f"{ARCHIVE} op 1: {ENTRY_RULE}"),
    "mpk entry outside Latin-1": (_added(entry="—.csv"), f"{ARCHIVE} op 1: {ENTRY_RULE}"),
    "mpk entry that case-folds into Latin-1": (_added(entry="Ÿ"), f"{ARCHIVE} op 1: {ENTRY_RULE}"),
    "mpk find empty": (_added(find=""), f"{ARCHIVE} op 1: 'find' is empty"),
    "mpk replace not text": (_added(replace=None), f"{ARCHIVE} op 1: 'replace' must be text"),
    "mpk text outside Latin-1": (_added(replace="—"), f"{ARCHIVE} op 1: 'replace' has characters outside Latin-1"),
    "absent with two operations": (_absent(ops=[{"op": "remove"}, {"op": "remove"}]),
                                   f"{MODEL_PATH}: \"after\": \"absent\" needs exactly one remove operation"),
    "absent with another operation": (_absent(ops=[{"op": "append", "data": "00"}]),
                                      f"{MODEL_PATH}: \"after\": \"absent\" needs exactly one remove operation"),
    "remove without absent": (_absent(after=sha(b"other")),
                              f"{MODEL_PATH}: a remove operation needs \"after\": \"absent\""),
    "absent in capitals": (_absent(after="ABSENT"),
                           f"{MODEL_PATH}: 'after' must be a lower-case SHA-256, \"source\" or \"absent\""),
    "retired as text": (lambda d: d.update(retired="true"), "{patchset}: 'retired' must be true or false"),
    "retired as a number": (lambda d: d.update(retired=1), "{patchset}: 'retired' must be true or false"),
    "retired null": (lambda d: d.update(retired=None), "{patchset}: 'retired' must be true or false"),
}
LAYOUT_PATCHED = "Patched: default.ini (original saved as default.ini.hearthdaoc-orig)\n"
LAYOUT_REFUSED = ("Not patched: default.ini is not the file this HearthDAoC release supports (for example one changed "
                  "by hand, or an older or newer upstream client). New characters start with the game's standard "
                  "window layout.\n")


class ApplierCases:
    """The cases every applier must pass. A subclass sets FLAGS and command()."""

    FLAGS = {}  # neutral name -> this applier's option

    def command(self):
        raise NotImplementedError

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.fx = Fixture(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def run_tool(self, *switches, client=None, patchset=None, bundle=None, cwd=None):
        """Run the applier on the fixture; switches are "restore", "check" or "unknown"."""
        args = [self.FLAGS["client"], client or self.fx.client,
                self.FLAGS["patchset"], patchset or self.fx.patchset_path]
        if bundle:
            args += [self.FLAGS["bundle"], bundle]
        args += [self.FLAGS[name] for name in switches]
        return subprocess.run(self.command() + args, capture_output=True, text=True, cwd=cwd, timeout=120)

    def test_apply_patches_every_file_and_backs_up_the_originals(self):
        r = self.run_tool()
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, PATCHED, ""))
        for rel, old, new in zip(FILES, (DLL, XML, SPLASH), (DLL_PATCHED, XML_PATCHED, SPLASH_NEW)):
            self.assertEqual(read(self.fx.path(rel)), new, rel)
            self.assertEqual(read(self.fx.backup(rel)), old, rel)
        expected = sorted(FILES + [f + ".hearthdaoc-orig" for f in FILES])
        self.assertEqual(sorted(p.replace(os.sep, "/") for p in self.fx.snapshot()), expected)  # no temporary file left

    def test_already_patched_files_are_skipped(self):
        write(self.fx.path("game.dll"), DLL_PATCHED)
        r = self.run_tool()
        expected = "Already patched: game.dll\n" + "".join(PATCHED.splitlines(True)[1:])
        self.assertEqual((r.returncode, r.stdout), (0, expected), r.stderr)
        self.assertFalse(os.path.exists(self.fx.backup("game.dll")))
        before = self.fx.snapshot()
        r = self.run_tool()
        self.assertEqual((r.returncode, r.stdout), (0, "".join(f"Already patched: {f}\n" for f in FILES)), r.stderr)
        self.assertEqual(self.fx.snapshot(), before)

    def test_a_backup_is_made_once_and_a_wrong_one_is_replaced(self):
        self.assertEqual(self.run_tool().returncode, 0)
        first = os.stat(self.fx.backup("game.dll"))
        write(self.fx.path("game.dll"), DLL)  # e.g. a launcher put the stock file back
        write(self.fx.path(FILES[1]), XML)
        write(self.fx.backup(FILES[1]), STRANGER)
        r = self.run_tool()
        self.assertEqual((r.returncode, r.stdout), (0, "".join(PATCHED.splitlines(True)[:2])
                                                    + f"Already patched: {FILES[2]}\n"), r.stderr)
        again = os.stat(self.fx.backup("game.dll"))
        self.assertEqual((again.st_ino, again.st_mtime_ns), (first.st_ino, first.st_mtime_ns))
        self.assertEqual(read(self.fx.backup("game.dll")), DLL)
        self.assertEqual(read(self.fx.backup(FILES[1])), XML)

    def test_unknown_and_missing_files_are_refused_with_exit_3_and_nothing_changed(self):
        write(self.fx.path("game.dll"), STRANGER)
        os.remove(self.fx.path(FILES[1]))
        before = self.fx.snapshot()
        r = self.run_tool()
        self.assertEqual((r.returncode, r.stdout, r.stderr), (3, UNKNOWN_MESSAGE.format(path="game.dll")
                                                              + MISSING_MESSAGE.format(path=FILES[1]), ""))
        self.assertEqual(self.fx.snapshot(), before)

    def test_a_patch_set_s_refused_text_follows_the_unknown_file(self):
        data = fixture_patchset()
        data["refused"] = "(for example one changed by hand). New characters start with the standard layout."
        write_patchset(self.fx.patchset_path, data)
        write(self.fx.path("game.dll"), STRANGER)
        os.remove(self.fx.path(FILES[1]))
        before = self.fx.snapshot()
        r = self.run_tool()
        self.assertEqual((r.returncode, r.stdout, r.stderr),
                         (3, "Not patched: game.dll is not the file this HearthDAoC release supports (for example one "
                             "changed by hand). New characters start with the standard layout.\n"
                             + MISSING_MESSAGE.format(path=FILES[1]), ""))
        self.assertEqual(self.fx.snapshot(), before)

    def test_two_patch_sets_on_one_client_each_apply_and_restore_on_their_own(self):
        # classic-creation.json and default-layout.json are applied by separate runs: one refused never stops the
        # other, and each restores only its own files.
        layout = os.path.join(self.fx.bundle, "default-layout.json")
        write_patchset(layout, fixture_layout_set())
        write(self.fx.path("default.ini"), STOCK_INI)
        write(self.fx.path("game.dll"), STRANGER)  # e.g. the b edition's: classic creation is refused
        self.assertEqual(self.run_tool().returncode, 3)
        r = self.run_tool(patchset=layout)
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, LAYOUT_PATCHED, ""))
        self.assertEqual(read(self.fx.path("default.ini")), STOCK_INI_PATCHED)
        r = self.run_tool(patchset=layout)
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "Already patched: default.ini\n", ""))
        write(self.fx.path("game.dll"), DLL)
        r = self.run_tool()
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, PATCHED, ""))
        self.assertEqual(read(self.fx.path("default.ini")), STOCK_INI_PATCHED)
        r = self.run_tool("restore", patchset=layout)
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "Restored: default.ini\n", ""))
        self.assertEqual(read(self.fx.path("default.ini")), STOCK_INI)
        self.assertEqual(read(self.fx.path("game.dll")), DLL_PATCHED)
        # The other way round: a default.ini changed by hand is refused, with the layout's own text.
        write(self.fx.path("default.ini"), STRANGER)
        r = self.run_tool(patchset=layout)
        self.assertEqual((r.returncode, r.stdout, r.stderr), (3, LAYOUT_REFUSED, ""))
        self.assertEqual(read(self.fx.path("default.ini")), STRANGER)
        r = self.run_tool()
        self.assertEqual((r.returncode, r.stdout), (0, "".join(f"Already patched: {f}\n" for f in FILES)), r.stderr)

    def test_restore_puts_the_originals_back(self):
        self.assertEqual(self.run_tool().returncode, 0)
        r = self.run_tool("restore")
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, RESTORED, ""))
        originals = {os.path.join(*f.split("/")): c for f, c in zip(FILES, (DLL, XML, SPLASH))}
        self.assertEqual(self.fx.snapshot(), originals)
        r = self.run_tool("restore")
        self.assertEqual((r.returncode, r.stdout), (0, "".join(f"Nothing to restore: {f}\n" for f in FILES)), r.stderr)

    def test_a_wrong_backup_is_not_restored_with_exit_3(self):
        self.assertEqual(self.run_tool().returncode, 0)
        write(self.fx.backup("game.dll"), STRANGER)
        write(self.fx.backup(FILES[2]), STRANGER)
        before = self.fx.snapshot()
        r = self.run_tool("restore")
        self.assertEqual((r.returncode, r.stdout), (3, "Not restored: not the original file: game.dll.hearthdaoc-orig, "
                                                       "pregame/splash.mpk.hearthdaoc-orig. Nothing was changed.\n"))
        self.assertEqual(self.fx.snapshot(), before)

    def test_files_changed_since_patching_are_not_restored_with_exit_3_and_nothing_changed(self):
        self.assertEqual(self.run_tool().returncode, 0)
        write(self.fx.path("game.dll"), STRANGER)  # e.g. a newer client installed over the patched one
        write(self.fx.path(FILES[2]), STRANGER)  # "after": "source": not the bundled splash.mpk either
        before = self.fx.snapshot()
        r = self.run_tool("restore")
        self.assertEqual((r.returncode, r.stdout, r.stderr), (3, CHANGED_MESSAGE.format(path="game.dll")
                                                              + CHANGED_MESSAGE.format(path=FILES[2]), ""))
        self.assertEqual(self.fx.snapshot(), before)  # the XML, still the patched one, isn't restored either
        os.remove(self.fx.path("game.dll"))
        write(self.fx.path(FILES[2]), SPLASH_NEW)
        before = self.fx.snapshot()
        r = self.run_tool("restore")
        self.assertEqual((r.returncode, r.stdout, r.stderr), (3, GONE_MESSAGE.format(path="game.dll"), ""))
        self.assertEqual(self.fx.snapshot(), before)

    def test_restore_keeps_the_backup_of_a_file_that_already_is_the_original(self):
        self.assertEqual(self.run_tool().returncode, 0)
        write(self.fx.path("game.dll"), DLL)  # e.g. a launcher put the stock file back
        r = self.run_tool("restore")
        self.assertEqual((r.returncode, r.stdout, r.stderr),
                         (0, "Nothing to restore: game.dll\n" + "".join(RESTORED.splitlines(True)[1:]), ""))
        originals = {os.path.join(*f.split("/")): c for f, c in zip(FILES, (DLL, XML, SPLASH))}
        self.assertEqual(self.fx.snapshot(), dict(originals, **{"game.dll.hearthdaoc-orig": DLL}))

    def test_check_reports_each_state(self):
        r = self.run_tool("check")
        self.assertEqual((r.returncode, r.stdout), (0, "".join(f"{f}: unpatched\n" for f in FILES)), r.stderr)
        write(self.fx.path("game.dll"), STRANGER)
        os.remove(self.fx.path(FILES[1]))
        write(self.fx.path(FILES[2]), SPLASH_NEW)
        before = self.fx.snapshot()
        r = self.run_tool("check")
        states = f"game.dll: unknown\n{FILES[1]}: missing\n{FILES[2]}: patched\n"
        self.assertEqual((r.returncode, r.stdout), (3, states), r.stderr)
        self.assertEqual(self.fx.snapshot(), before)

    def test_invalid_patch_sets_exit_2_and_change_nothing(self):
        path = self.fx.patchset_path
        before = self.fx.snapshot()
        for name, (change, reason) in INVALID.items():
            data = fixture_patchset()
            change(data)
            write_patchset(path, data)
            with self.subTest(name):
                r = self.run_tool()
                self.assertEqual((r.returncode, r.stdout, r.stderr),
                                 (2, "", f"Error: invalid patch set: {reason.format(patchset=path)}\n"))
                self.assertEqual(self.fx.snapshot(), before)
        for name, text in (("not JSON", "{ not json"), ("not an object", "[]\n")):
            with open(path, "w", encoding="utf-8") as f:
                f.write(text)
            with self.subTest(name):
                r = self.run_tool()
                self.assertEqual(r.returncode, 2)
                self.assertTrue(r.stderr.startswith("Error: invalid patch set: "), r.stderr)
        r = self.run_tool(patchset=os.path.join(self.fx.root, "missing.json"))
        self.assertEqual(r.returncode, 2)
        self.assertTrue(r.stderr.startswith("Error: invalid patch set: cannot read patch set "), r.stderr)
        self.assertEqual(self.fx.snapshot(), before)

    def test_bad_usage_exits_2(self):
        before = self.fx.snapshot()
        for switches in (("restore", "check"), ("unknown",)):
            with self.subTest(switches=switches):
                r = self.run_tool(*switches)
                self.assertEqual((r.returncode, r.stdout), (2, ""))
                self.assertIn("usage: ", r.stderr.lower())
        missing = os.path.join(self.fx.root, "nowhere")
        r = self.run_tool(client=missing)
        self.assertEqual((r.returncode, r.stderr), (2, f"Error: client folder not found: {missing}\n"))
        self.assertEqual(self.fx.snapshot(), before)

    def test_a_missing_bundle_or_a_failed_check_exits_2_and_changes_nothing(self):
        empty = os.path.join(self.fx.root, "empty")
        os.makedirs(empty)
        before = self.fx.snapshot()
        for switches in ((), ("check",)):
            r = self.run_tool(*switches, bundle=empty)
            self.assertEqual((r.returncode, r.stdout, r.stderr),
                             (2, "", f"Error: bundled file missing: {os.path.join(empty, 'splash.mpk')}\n"))
        cases = {
            "op 1 (replace at 0x10): the bytes there are not the expected ones": _op(0, **{"from": "00000000"}),
            "op 1 (text-replace): found the text 0 times, not exactly once": _op(1, find="<Nothing/>"),
            f"{FILES[1]}: the patched file doesn't have the expected SHA-256; nothing changed":
                _file(1, after=sha(b"something else")),
        }
        for reason, change in cases.items():
            data = fixture_patchset()
            change(data)
            write_patchset(self.fx.patchset_path, data)
            with self.subTest(reason):
                r = self.run_tool()
                self.assertEqual((r.returncode, r.stdout, r.stderr), (2, "", f"Error: {reason}\n"))
                self.assertEqual(self.fx.snapshot(), before)
        # A restore needs the bundled splash.mpk too, to tell whether the client's one is the patched one.
        write_patchset(self.fx.patchset_path, fixture_patchset())
        self.assertEqual(self.run_tool().returncode, 0)
        before = self.fx.snapshot()
        r = self.run_tool("restore", bundle=empty)
        self.assertEqual((r.returncode, r.stdout, r.stderr),
                         (2, "", f"Error: bundled file missing: {os.path.join(empty, 'splash.mpk')}\n"))
        self.assertEqual(self.fx.snapshot(), before)

    @unittest.skipIf(hasattr(os, "geteuid") and os.geteuid() == 0, "root can write to read-only folders")
    def test_a_folder_that_cannot_be_written_exits_1_and_nothing_changes(self):
        folders = [self.fx.client, os.path.join(self.fx.client, "pregame")]
        before = self.fx.snapshot()
        for folder in folders:
            os.chmod(folder, 0o555)
        try:
            r = self.run_tool()
        finally:
            for folder in folders:
                os.chmod(folder, 0o755)
        self.assertEqual((r.returncode, r.stdout), (1, ""))
        self.assertTrue(r.stderr.startswith("Error: "), r.stderr)
        self.assertEqual(self.fx.snapshot(), before)

    def test_paths_with_spaces_and_parentheses(self):
        self.fx = Fixture(os.path.join(self.tmp.name, *INSTALL))
        r = self.run_tool("check")
        self.assertEqual((r.returncode, r.stdout), (0, "".join(f"{f}: unpatched\n" for f in FILES)), r.stderr)
        r = self.run_tool()
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, PATCHED, ""))
        self.assertEqual(read(self.fx.path(FILES[2])), SPLASH_NEW)
        r = self.run_tool("restore")
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, RESTORED, ""))
        self.assertEqual(read(self.fx.path("game.dll")), DLL)

    def test_relative_paths_are_resolved_from_the_current_folder(self):
        r = self.run_tool(client="client", patchset=os.path.join("bundle", "classic-creation.json"), cwd=self.fx.root)
        self.assertEqual((r.returncode, r.stdout), (0, PATCHED), r.stderr)
        self.assertEqual(read(self.fx.path(FILES[2])), SPLASH_NEW)

    # mpk-text-replace, remove ("after": "absent") and retired sets.

    def write_si(self, data=None, archive=ARCHIVE_DATA, model=MODEL, backup=None):
        """The archive, the model and its backup (None: no file) in the fixture client, and data (default: the
        Shrouded Isles test set) as a patch set; its path."""
        write(self.fx.path(ARCHIVE), archive)
        for path, content in ((self.fx.path(MODEL_PATH), model), (self.fx.backup(MODEL_PATH), backup)):
            if content is not None:
                write(path, content)
            elif os.path.exists(path):
                os.remove(path)
        path = os.path.join(self.fx.bundle, "si-test.json")
        write_patchset(path, data or fixture_si_set())
        return path

    def si_state(self):
        """(archive, its backup, model, its backup): the bytes of each, None for no file."""
        paths = (self.fx.path(ARCHIVE), self.fx.backup(ARCHIVE), self.fx.path(MODEL_PATH), self.fx.backup(MODEL_PATH))
        return tuple(read(p) if os.path.exists(p) else None for p in paths)

    def test_mpk_text_replace_and_remove_check_patch_and_restore(self):
        si = self.write_si()
        r = self.run_tool("check", patchset=si)
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "".join(f"{f}: unpatched\n" for f in SI_FILES), ""))
        inode = os.stat(self.fx.path(MODEL_PATH)).st_ino
        r = self.run_tool(patchset=si)
        self.assertEqual((r.returncode, r.stdout, r.stderr),
                         (0, "".join(f"Patched: {f} (original saved as {f}.hearthdaoc-orig)\n" for f in SI_FILES), ""))
        self.assertEqual(self.si_state(), (ARCHIVE_PATCHED, ARCHIVE_DATA, None, MODEL))  # the same bytes as Python's
        self.assertEqual(os.stat(self.fx.backup(MODEL_PATH)).st_ino, inode)  # renamed, never written
        r = self.run_tool("check", patchset=si)
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "".join(f"{f}: patched\n" for f in SI_FILES), ""))
        before = self.fx.snapshot()
        r = self.run_tool(patchset=si)
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "".join(f"Already patched: {f}\n" for f in SI_FILES), ""))
        self.assertEqual(self.fx.snapshot(), before)
        r = self.run_tool("restore", patchset=si)
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "".join(f"Restored: {f}\n" for f in SI_FILES), ""))
        self.assertEqual(self.si_state(), (ARCHIVE_DATA, None, MODEL, None))

    def test_a_client_without_the_file_to_remove_counts_as_patched(self):
        si = self.write_si(model=None)
        r = self.run_tool("check", patchset=si)
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, f"{ARCHIVE}: unpatched\n{MODEL_PATH}: patched\n", ""))
        r = self.run_tool(patchset=si)
        self.assertEqual((r.returncode, r.stdout, r.stderr),
                         (0, f"Patched: {ARCHIVE} (original saved as {ARCHIVE}.hearthdaoc-orig)\n"
                             f"Already patched: {MODEL_PATH}\n", ""))
        self.assertEqual(self.si_state(), (ARCHIVE_PATCHED, ARCHIVE_DATA, None, None))

    def test_a_wrong_backup_of_the_file_to_remove_is_replaced_by_the_verified_original(self):
        si = self.write_si(backup=STRANGER)
        r = self.run_tool(patchset=si)
        self.assertEqual((r.returncode, r.stderr), (0, ""), r.stdout)
        self.assertEqual(self.si_state(), (ARCHIVE_PATCHED, ARCHIVE_DATA, None, MODEL))

    def test_an_unknown_file_to_remove_is_refused_with_exit_3_and_nothing_changed(self):
        si = self.write_si(data=fixture_si_set(refused="(our test's). The portal keeps its look."), model=STRANGER)
        before = self.fx.snapshot()
        r = self.run_tool(patchset=si)
        self.assertEqual((r.returncode, r.stdout, r.stderr),
                         (3, f"Not patched: {MODEL_PATH} is not the file this HearthDAoC release supports (our test's). "
                             "The portal keeps its look.\n", ""))
        self.assertEqual(self.fx.snapshot(), before)  # the archive, which is fine, isn't patched either

    def test_the_restore_states_of_a_removed_file(self):
        wrong = f"Not restored: not the original file: {MODEL_PATH}.hearthdaoc-orig. Nothing was changed.\n"
        nothing = f"Nothing to restore: {MODEL_PATH}\n"
        cases = [((None, MODEL), (0, f"Restored: {MODEL_PATH}\n"), (MODEL, None)), ((None, None), (0, nothing), None),
                 ((MODEL, MODEL), (0, nothing), None), ((MODEL, None), (0, nothing), None),
                 ((STRANGER, None), (0, nothing), None), ((STRANGER, MODEL), (3, CHANGED_MESSAGE.format(path=MODEL_PATH)),
                                                         None),
                 ((None, STRANGER), (3, wrong), None), ((MODEL, STRANGER), (3, wrong), None),
                 ((STRANGER, STRANGER), (3, wrong), None)]
        for (model, backup), result, after in cases:
            with self.subTest(model=model, backup=backup):
                si = self.write_si(data=remove_set(), model=model, backup=backup)
                r = self.run_tool("restore", patchset=si)
                self.assertEqual((r.returncode, r.stdout, r.stderr), result + ("",))
                self.assertEqual(self.si_state()[2:], after or (model, backup))

    def test_mpk_failures_exit_2_and_change_nothing(self):
        damaged_entry = make_mpk([dict(e, crc=12345) if n == 1 else e for n, e in enumerate(mpk_entries())])
        op = mpk_op("fixtures.csv", PORTAL_ROW)
        cases = {
            "op 1 (mpk-text-replace): not an MPK archive": (b"not an archive", None),
            "op 1 (mpk-text-replace): the MPK archive is damaged": (with_header(ARCHIVE_DATA, count=5), None),
            "op 1 (mpk-text-replace): fixtures.csv in the archive is damaged": (damaged_entry, None),
            "op 1 (mpk-text-replace): the archive has 0 entries named FIXTURES.CSV, not exactly one":
                (ARCHIVE_DATA, [mpk_op("FIXTURES.CSV", PORTAL_ROW)]),
            "op 2 (mpk-text-replace): found the text 0 times in fixtures.csv, not exactly once": (ARCHIVE_DATA, [op, op]),
            f"{ARCHIVE}: the patched file doesn't have the expected SHA-256; nothing changed": (ARCHIVE_DATA, [op]),
        }
        for reason, (archive, ops) in cases.items():
            with self.subTest(reason):
                si = self.write_si(data=fixture_si_set(files=[archive_entry(data=archive, ops=ops)]), archive=archive)
                before = self.fx.snapshot()
                r = self.run_tool(patchset=si)
                self.assertEqual((r.returncode, r.stdout, r.stderr), (2, "", f"Error: {reason}\n"))
                self.assertEqual(self.fx.snapshot(), before)

    def test_stored_blocks_at_the_64_kib_boundaries_give_the_same_bytes(self):
        # Contents of 0, 1, 65535, 65536 and 131071 bytes after the edit, a name field with bytes after its NUL and
        # Latin-1 text: six operations on one archive, each rebuilding it.
        tail = "the line to remove\r\n"
        sizes = (0, 1, 65535, 65536, 2 * 65535 + 1)
        entries = [mpk_entry(f"e{n}.csv", bytes(i * 7 % 256 for i in range(size)) + tail.encode())
                   for n, size in enumerate(sizes)]
        entries.append(mpk_entry("", b"Our Tr\xe9e\r\n", name_field=b"named.csv\0old".ljust(256, b"\0")))
        archive = make_mpk(entries, cumulative=True)
        changed = {f"e{n}.csv": e["content"][:-len(tail)] for n, e in enumerate(entries[:-1])}
        expected = rebuilt(entries, dict(changed, **{"named.csv": b"Our Tr\xe8e\r\n"}))
        ops = [mpk_op(name, tail) for name in changed] + [mpk_op("named.csv", "Tr\xe9e", "Tr\xe8e")]
        si = self.write_si(data=fixture_si_set(files=[archive_entry(data=archive, after=expected, ops=ops)]),
                           archive=archive)
        r = self.run_tool(patchset=si)
        self.assertEqual((r.returncode, r.stderr), (0, ""), r.stdout)
        self.assertEqual(read(self.fx.path(ARCHIVE)), expected)

    def test_a_retired_set_is_restored_by_a_run_without_restore(self):
        si = self.write_si()
        self.assertEqual(self.run_tool(patchset=si).returncode, 0)
        write_patchset(si, fixture_si_set(retired=True))  # a later release takes the set back
        r = self.run_tool(patchset=si)
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "".join(f"Restored: {f}\n" for f in SI_FILES), ""))
        self.assertEqual(self.si_state(), (ARCHIVE_DATA, None, MODEL, None))
        for switches in ((), ("restore",)):
            r = self.run_tool(*switches, patchset=si)
            self.assertEqual((r.returncode, r.stdout, r.stderr),
                             (0, "".join(f"Nothing to restore: {f}\n" for f in SI_FILES), ""))
        r = self.run_tool("check", patchset=si)  # --check still reports the patch states
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "".join(f"{f}: unpatched\n" for f in SI_FILES), ""))
        write(self.fx.path(MODEL_PATH), STRANGER)
        write(self.fx.backup(MODEL_PATH), MODEL)
        before = self.fx.snapshot()
        r = self.run_tool(patchset=si)
        self.assertEqual((r.returncode, r.stdout, r.stderr), (3, CHANGED_MESSAGE.format(path=MODEL_PATH), ""))
        self.assertEqual(self.fx.snapshot(), before)

    def test_a_set_that_is_not_retired_is_applied(self):
        si = self.write_si(data=fixture_si_set(retired=False))
        r = self.run_tool(patchset=si)
        self.assertEqual((r.returncode, r.stdout, r.stderr),
                         (0, "".join(f"Patched: {f} (original saved as {f}.hearthdaoc-orig)\n" for f in SI_FILES), ""))


class PythonApplierTests(ApplierCases, unittest.TestCase):
    FLAGS = {"client": "--client", "patchset": "--patchset", "bundle": "--bundle",
             "restore": "--restore", "check": "--check", "unknown": "--frobnicate"}

    def command(self):
        return [sys.executable, APPLY_PY]


@unittest.skipUnless(PWSH, "pwsh is not installed (HDC_PWSH may name it)")
class PowerShellApplierTests(ApplierCases, unittest.TestCase):
    FLAGS = {"client": "-Client", "patchset": "-PatchSet", "bundle": "-Bundle",
             "restore": "-Restore", "check": "-Check", "unknown": "-Frobnicate"}

    def command(self, script=PS1):
        return [PWSH, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script]

    def test_defaults_to_its_own_folder_and_the_patches_folder_next_to_it(self):
        fx = self.fx
        os.makedirs(os.path.join(fx.client, "patches"))
        shutil.copy(PS1, fx.client)
        for name in ("classic-creation.json", "splash.mpk"):
            shutil.move(os.path.join(fx.bundle, name), os.path.join(fx.client, "patches", name))
        r = subprocess.run(self.command(os.path.join(fx.client, "patch-client.ps1")),
                           capture_output=True, text=True, cwd=fx.root, timeout=120)
        self.assertEqual((r.returncode, r.stdout), (0, PATCHED), r.stderr)
        self.assertEqual(read(fx.path("game.dll")), DLL_PATCHED)
        self.assertEqual(read(fx.path(FILES[2])), SPLASH_NEW)

    def test_the_bat_command_line_in_a_folder_with_spaces_and_parentheses(self):
        # patch-client.bat runs: powershell ... -File "%~dp0patch-client.ps1" %*, where %~dp0 is
        # the install's app folder, e.g. C:\Program Files (x86)\...\client-opendaoc\app\.
        root = os.path.join(self.tmp.name, *INSTALL)
        fx = Fixture(root)
        app = os.path.join(root, "app")
        os.rename(fx.client, app)
        fx.client = app
        shutil.copy(PS1, app)
        shutil.move(fx.bundle, os.path.join(app, "patches"))
        script = self.command(os.path.join(app, "patch-client.ps1"))
        r = subprocess.run(script + ["-Check"], capture_output=True, text=True, cwd=self.tmp.name, timeout=120)
        self.assertEqual((r.returncode, r.stdout), (0, "".join(f"{f}: unpatched\n" for f in FILES)), r.stderr)
        r = subprocess.run(script + ["-Client", app], capture_output=True, text=True, cwd=self.tmp.name, timeout=120)
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, PATCHED, ""))
        self.assertEqual(read(fx.path(FILES[2])), SPLASH_NEW)
        r = subprocess.run(script + ["-Client", app, "-Restore"], capture_output=True, text=True, cwd=self.tmp.name,
                           timeout=120)
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, RESTORED, ""))
        self.assertEqual(read(fx.path("game.dll")), DLL)


class WindowsFilesTests(unittest.TestCase):
    def test_windows_files_are_ascii_with_crlf_line_ends(self):
        for path in (PS1, BAT):
            with self.subTest(os.path.basename(path)):
                data = read(path)
                data.decode("ascii")  # Windows PowerShell 5.1 reads a script without a BOM as ANSI
                self.assertTrue(data.endswith(b"\r\n"))
                self.assertEqual(data.count(b"\n"), data.count(b"\r\n"))

    def test_bat_runs_the_script_for_each_patch_set_and_returns_the_exit_code(self):
        # Once for each patch set with the player's options, or once for the one -PatchSet names; how it
        # chooses runs under Wine in client/tests/test_windows_bat.sh.
        text = read(BAT).decode("ascii")
        for name in ("classic-creation.json", "default-layout.json"):
            self.assertIn('powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1" '
                          f'-PatchSet "%~dp0patches\\{name}" %*\r\n', text)
        self.assertIn('powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1" %*\r\n', text)
        self.assertIn('set "RC=%ERRORLEVEL%"\r\n', text)
        self.assertIn(" pause\r\n", text)  # only when double-clicked, so the player can read the result
        self.assertIn("\r\nexit /b %RC%\r\n", text)

    def test_ps1_avoids_powershell_7_only_syntax(self):
        text = read(PS1).decode("ascii")
        for token in ("??", "?.", "&&", "||", "-AsHashtable", "-NoEnumerate", "FromHexString", "::Latin1",
                      "$IsWindows", "$IsLinux", "-Parallel", "Join-Path -AdditionalChildPath"):
            self.assertNotIn(token, text)


if __name__ == "__main__":
    unittest.main()
