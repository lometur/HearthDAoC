# Classic Character Creation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Players on a HearthDAoC server create characters the classic way: they pick a base class (Fighter, Mage, Viking, Guardian…), place all 30 stat points themselves, and see a "Hearth DAoC" splash. This works on Linux and Windows without distributing any EA file.

**Architecture:** The fork ships a *patch set* (`client/patches/classic-creation.json`): for each client file, its expected SHA-256 before and after, plus the exact byte and text changes. It also ships our own new code section and the re-lettered `splash.mpk`. A generator (`build.py`) produces the patch set from:
- the real client files;
- the server's own class data;
- a nasm-built code cave that registers the base classes and hides the final classes and the later races;
- three byte patches for the classic stat flow;
- one pregame XML edit.

Two small appliers with identical rules install it into each player's own client: Python, run by `setup.sh` on Linux, and PowerShell on Windows. CI rebuilds the patch set from the pinned upstream release's files and checks it byte for byte.

**Tech Stack:** Python 3.10+ standard library (applier, generator, PE helpers); nasm; PowerShell 5.1+ (Windows applier); upstream's `OfflineDaoc.Mpk` (.NET 10) for `splash.mpk`; Pillow (dev-time splash re-lettering only); GitHub Actions.

**Spec:** `docs/fork/specs/2026-10-06-classic-character-creation-design.md` (branch `sub2-classic-creation`). The read-only investigation behind the patch sites is summarised there (section 3). Its addresses are re-verified by this plan's real-file tests.

## Global Constraints

- **No EA files are distributed.** Never commit or publish `game.dll`, pregame XML or stock MPKs. The repo and bundles hold only patch data (hashes plus byte or text edits), our own code bytes, and our `splash.mpk` (OfflineDAoC's art, re-lettered, credited).
- **One target build.** The target is OfflineDAoC 0.34 classic, `game.dll` SHA-256 `67dcf68a37b95a93…` (full value in `build.py`'s `GAME_DLL_SHA256`). Any other file is refused, and the client keeps working with the stock screen.
- **Dependencies.** The applier and generator use the Python 3.10+ standard library only. Pillow is used only by the dev-time `reletter_splash.py`. nasm builds the cave. .NET is used only for the MPK tool.
- **Applier rules, identical in Python and PowerShell:**
  - refuse unknown or missing files and change nothing (every file is checked before any write);
  - skip already-patched files;
  - back up each original once, as `<file>.hearthdaoc-orig`;
  - write atomically;
  - touch only the patch set's relative paths inside the client folder;
  - exit codes: 0 = done; 1 = a read or write failed; 2 = bad usage or invalid patch set; 3 = refused, nothing changed.
- **Base classes (15).** Albion: Fighter 14, Elementalist 15, Acolyte 16, Rogue 17, Mage 18, Disciple 20. Midgard: Viking 35, Mystic 36, Seer 37, Rogue 38. Hibernia: Magician 51, Guardian 52, Naturalist 53, Stalker 54, Forester 57.
- **Races per base class: no dead ends.** A base class offers the union of its enabled full classes' `EligibleRaces`, limited to the classic and Shrouded Isles races 1–15. "Enabled" means not in the world's `disabled_classes` after `world_fixes.py` removes 20 (PR #64, merged).
- **Line endings.** Keep each file's own: binary game files untouched apart from patches; pregame XML CRLF; `.bat` and `.ps1` CRLF.
- **Workflow.** Work on branch `sub2-classic-creation`, which already contains `main`. Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The owner merges the PR.

## Review Focus

1. **A client folder whose path has spaces and parentheses** (for example `C:\Program Files (x86)\Offline DAoC v0.34\…`): check, apply and restore must work with both appliers. Pinned by Task 2's cross-applier harness.
2. **Client files replaced by a newer upstream client after patching:** restore must not overwrite them with the old originals. It refuses, explains, keeps the backup and exits 3. Pinned in Task 1 (unit and CLI) and in Task 2's harness, for both appliers.
3. **Re-running `setup.sh` on an already-patched client:** it ends patched, with exactly one backup per patched file holding the original, and exits 0. Pinned in Task 7.
4. **Customising an existing, already-promoted character** from character select: the screen must not crash, even though full classes are hidden. This can only be checked in game (Task 9, check 6). The fallback is in spec section 6.
5. **Every race × base-class combination the client offers must be accepted by the server.** Pinned by Task 4 (each client race list is a subset of the server's base-class `EligibleRaces`, with no dead ends) and by Task 9 (one character per race, created as a normal player).

## Prerequisites

- Branch `sub2-classic-creation` contains `main`, including PR #64 (Disciple enabled, Saracen Disciple start) and PR #70 (merging is releasing). Since PR #70, `.github/workflows/server-image.yml` is the only workflow and publishes a release from the push to `main`, `release-pr.yml` is gone, `deploy/release_tag.py` only has `next`, and `deploy/build_bundles.sh` stamps the release tag into the bundle's `.env.example`. Tasks 7 and 8 edit those files as PR #70 left them. PR #70 is merged, and `main` was merged into the branch when this plan was committed; if `main` has moved since, merge it again before Task 1: `git fetch origin && git merge origin/main`. To check, run `git merge-base --is-ancestor origin/main HEAD && test ! -e .github/workflows/release-pr.yml && grep -x 'HEARTHDAOC_TAG=' deploy/.env.example`. It prints `HEARTHDAOC_TAG=`; Task 7 checks this again.
- Locally you need:
  - `nasm`;
  - the .NET 10 SDK, to build `source/tools/OfflineDaoc.Mpk`;
  - optionally PowerShell, for the Windows-applier cases;
  - the real client files for the real-file tests (on the owner's PC, `~/Games/HearthDAoC/client`) and the clean classic world (`~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db`).
- In CI, the workflow (Task 8) fetches the files it needs from the pinned upstream release, verified.

## File structure

| Path | Responsibility | Task |
|---|---|---|
| `client/patches/patchset.py`, `apply_patches.py` | Patch-set format, Python applier and CLI | 1 |
| `client/windows/patch-client.ps1`, `patch-client.bat` | Windows applier | 2 |
| `client/patches/pe.py` | PE parsing, checksum, section append, byte diff → ops | 3 |
| `client/patches/build.py` | Generator: stat flow, XML edit, cave, splash entry → `classic-creation.json` | 3, 5, 6 |
| `client/patches/classdata.py`, `src/base_classes.py` | Base-class table from the server's class sources and the world's `disabled_classes` | 4 |
| `client/patches/src/baseclass.asm` | Code cave (registration, hiding) | 5 |
| `client/patches/branding/*`, `mpk.py`, `splash_entry.py` | Splash re-lettering, MPK build, patch-set entry | 6 |
| `client/linux/setup.sh`, `deploy/build_bundles.sh` | Delivery to players and in release bundles | 7 |
| `.github/workflows/server-image.yml`, `deploy/release_tag.py`, `docs/fork/FORK.md`, `client/README.md` | CI checks, release build, what makes a release, docs | 8 |
| `docs/fork/verification/sub2-ingame.md` | In-game verification record | 9 |

---

### Task 1: Patch-set format and Python applier

**Files:**
- Create: `client/patches/patchset.py` (library: load, check, apply and restore a patch set)
- Create: `client/patches/apply_patches.py` (Linux command line; executable, mode 100755)
- Create: `client/patches/tests/__init__.py` (empty)
- Test: `client/patches/tests/test_patchset.py` (fixture files in a temp dir; no game file needed)

**Interfaces:**
- Consumes: nothing. This is the first task. Python 3.10+ standard library only.
- Produces, in `client/patches/patchset.py`:
  - `FORMAT = 1`; `BACKUP_SUFFIX = ".hearthdaoc-orig"`; `OPS = {"replace": ("offset", "from", "to"), "append": ("data",), "text-replace": ("find", "replace"), "file": ("source",)}`
  - `class PatchError(Exception)`
  - `load(path: str) -> dict`: validates format == 1, a non-empty `files` list, safe relative paths (no absolute path, no `..`, `.` or empty part, no backslash, no `:`), each path listed once (case-insensitive), lower-case SHA-256 `before`, `after` = lower-case SHA-256 or `"source"` (which needs exactly one `file` op), `before != after`, known ops with their fields, lower-case hex, same-length `replace`, Latin-1 `text-replace` text, safe `file` source. Any problem, including unreadable JSON, raises `PatchError`.
  - `sha256_file(path) -> str`
  - `transform(data: bytes, ops: list, bundle_dir: str | None) -> bytes`
  - `status(client_dir, patchset, bundle_dir) -> list[tuple[str, str]]`: states `"unpatched"` | `"patched"` | `"unknown"` | `"missing"`, in patch-set order.
  - `apply(client_dir, patchset, bundle_dir) -> list[tuple[str, str]]`: `"patched"` | `"already"` per file. It raises `PatchError("refused, nothing changed: game.dll (unknown), ...")` listing every unknown or missing file before anything is written. It builds and hash-checks every new content before the first write. It backs up once (an existing backup whose hash is `before` is kept; any other backup is replaced by the verified original). Every write is atomic: `tempfile.mkstemp` in the same folder, fsync, chmod to the original's mode, `os.replace`.
  - `restore_status(client_dir, patchset, bundle_dir) -> list[tuple[str, str]]`: the state of each file for a restore, in patch-set order: `"patched"` (the backup's hash is `before` and the file is the patched one, its hash is `after` or, for `"after": "source"`, the bundled file's), `"no-backup"`, `"original"` (the file already is the original), `"wrong-backup"` (the backup's hash isn't `before`), `"missing"` (the backup is fine, the file is gone) or `"changed"` (the backup is fine, the file is something else, for example a newer upstream client installed after patching).
  - `restore(client_dir, patchset, bundle_dir) -> list[tuple[str, str]]`: `"restored"` | `"not-patched"` per file. It checks every file first, like `apply`. A backup goes back only over the patched file: if any backup is wrong or any backed-up file is missing or changed, it raises `PatchError("not the original file: <path>.hearthdaoc-orig, ...; changed since it was patched: <path>, ...; missing: <path>, ...")` (only the parts that apply) and changes nothing. A file that already is the original is `"not-patched"` and keeps its backup, which the next patch run uses again. A restore moves the backup back with `os.replace`, so the backup is gone afterwards.
- Produces, `client/patches/apply_patches.py --client DIR [--patchset FILE] [--bundle DIR] [--restore | --check]`. The default patch set is `classic-creation.json` next to the script. The default bundle is the patch set's folder. The script sets `sys.dont_write_bytecode`, so it leaves no `__pycache__` in a bundle. Task 2's PowerShell applier must print the same lines and use the same exit codes:

  | Case | Output | Exit |
  |---|---|---|
  | file patched | stdout `Patched: <path> (original saved as <path>.hearthdaoc-orig)` | 0 |
  | file already patched | stdout `Already patched: <path>` | 0 |
  | `--restore`, backup put back over the patched file | stdout `Restored: <path>` | 0 |
  | `--restore`, no backup, or the file already is the original (its backup is kept) | stdout `Nothing to restore: <path>` | 0 |
  | `--check` | stdout `<path>: <state>` per file | 0, or 3 if any state is `unknown` or `missing` |
  | unknown file (one line per file, nothing changed) | stdout `Not patched: <path> is not the file this HearthDAoC release supports (for example the 0.34b edition or a newer upstream client). The client still works with the standard creation screen.` | 3 |
  | missing file (one line per file, nothing changed) | stdout `Not patched: <path> is missing from the client folder.` | 3 |
  | `--restore` with a wrong backup (one line for all of them, printed first; nothing changed) | stdout `Not restored: not the original file: <path>.hearthdaoc-orig. Nothing was changed.` | 3 |
  | `--restore`, a backed-up file has changed since it was patched (one line per file, nothing changed) | stdout `Not restored: <path> has changed since it was patched (for example a newer client was installed); the saved original is kept as <path>.hearthdaoc-orig.` | 3 |
  | `--restore`, a backed-up file is missing (one line per file, nothing changed) | stdout `Not restored: <path> is missing from the client folder; the saved original is kept as <path>.hearthdaoc-orig.` | 3 |
  | no `--client`, or `--restore` with `--check` | argparse usage error on stderr | 2 |
  | client folder not found | stderr `Error: client folder not found: <DIR>` | 2 |
  | invalid or unreadable patch set | stderr `Error: invalid patch set: <reason>` | 2 |
  | bundled file missing (`--restore` needs it too, to recognize a patched `"after": "source"` file), an op doesn't match, or the result has the wrong hash | stderr `Error: <reason>` | 2 |
  | a file couldn't be read or written | stderr `Error: <OS error>` | 1 |
- Produces, for later tasks' tests, these module-level helpers in `client/patches/tests/test_patchset.py`: `Fixture(root, data=None)` (with `.client`, `.bundle`, `.patchset_path`, `.patchset`, `.path(rel)`, `.backup(rel)` and `.snapshot()`), `fixture_patchset()`, `write_patchset(path, data)`, `sha`, `read`, `write`, and the constants `DLL`, `DLL_PATCHED`, `XML`, `XML_PATCHED`, `SPLASH`, `SPLASH_NEW`, `STRANGER`, `FILES`, `UNKNOWN_MESSAGE` and `CHANGED_MESSAGE`. Task 2 can `from tests.test_patchset import Fixture, ...` with the same `-t client/patches` top level.

All commands run from the repository root, on branch `sub2-classic-creation`.

- [ ] **Step 1: Write the failing test**

Create the test package and the library tests:

```bash
mkdir -p client/patches/tests
: > client/patches/tests/__init__.py
```

Create `client/patches/tests/test_patchset.py`:

```python
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -v`

Expected: the import fails, because `patchset.py` doesn't exist yet:
```
ERROR: tests.test_patchset (unittest.loader._FailedTest.tests.test_patchset)
...
ModuleNotFoundError: No module named 'patchset'
...
Ran 1 test in 0.000s

FAILED (errors=1)
```

- [ ] **Step 3: Write minimal implementation**

Create `client/patches/patchset.py`:

```python
"""Load, check, apply and restore a HearthDAoC client patch set (format 1).

A patch set is a JSON file. For each client file, given by its path relative to the client
folder, it holds the SHA-256 the file must have before and after patching and the operations
that turn one into the other. It carries only our patch data: the EA files stay on the
player's machine and are never distributed.

Operations, applied in order to an in-memory copy of the file:
- replace: at "offset" in the current bytes, "from" (hex) must match exactly; it becomes "to"
  (hex, the same length).
- append: "data" (hex) is added at the end.
- text-replace: on the bytes decoded as Latin-1, "find" must occur exactly once; it becomes
  "replace". Every other byte, CRLF line ends included, is kept.
- file: the whole file becomes <bundle folder>/<source>. "after": "source" means "equals that
  bundled file".

Rules (client/windows/patch-client.ps1 follows the same ones):
- A file whose SHA-256 is its "after" hash is already patched and is skipped.
- A file that is missing, or whose hash is neither "before" nor "after", is refused. Then
  nothing at all is changed: every file is checked, and every new content built and verified,
  before anything is written.
- The original is backed up once as <file>.hearthdaoc-orig. A backup that isn't the original
  is replaced by the verified original.
- A restore puts a backup back only over the patched file. A file that has changed since it was
  patched (for example a newer client) or is missing is refused, and then nothing is restored.
- Every write is atomic: a temporary file in the same folder, then os.replace.
- Only the patch set's own relative paths inside the client folder are touched.
"""
import hashlib
import json
import os
import re
import stat
import tempfile

FORMAT = 1
BACKUP_SUFFIX = ".hearthdaoc-orig"
OPS = {
    "replace": ("offset", "from", "to"),
    "append": ("data",),
    "text-replace": ("find", "replace"),
    "file": ("source",),
}
_SHA256 = re.compile(r"[0-9a-f]{64}")
_HEX = re.compile(r"(?:[0-9a-f]{2})+")


class PatchError(Exception):
    """An invalid patch set, a refused client file or a failed check; the message says which."""


def _check_path(rel):
    """Accept only a relative path with forward slashes that stays inside its folder."""
    if not isinstance(rel, str) or not rel or rel.startswith("/") or "\\" in rel or ":" in rel:
        raise PatchError(f"unsafe path: {rel!r}")
    if any(part in ("", ".", "..") for part in rel.split("/")):
        raise PatchError(f"unsafe path: {rel!r}")
    return rel


def _hex(value, where):
    if not isinstance(value, str) or not _HEX.fullmatch(value):
        raise PatchError(f"{where}: not lower-case hex bytes")
    return bytes.fromhex(value)


def _is_sha256(value):
    return isinstance(value, str) and _SHA256.fullmatch(value) is not None


def _check_op(op, where):
    if not isinstance(op, dict) or op.get("op") not in OPS:
        raise PatchError(f"{where}: unknown operation")
    kind = op["op"]
    for key in OPS[kind]:
        if key not in op:
            raise PatchError(f"{where}: {kind} needs {key!r}")
    if kind == "replace":
        if type(op["offset"]) is not int or op["offset"] < 0:
            raise PatchError(f"{where}: offset must be a whole number, 0 or more")
        if len(_hex(op["from"], where)) != len(_hex(op["to"], where)):
            raise PatchError(f"{where}: 'from' and 'to' must have the same length")
    elif kind == "append":
        _hex(op["data"], where)
    elif kind == "text-replace":
        for key in ("find", "replace"):
            if not isinstance(op[key], str):
                raise PatchError(f"{where}: {key!r} must be text")
            try:
                op[key].encode("latin-1")
            except UnicodeEncodeError:
                raise PatchError(f"{where}: {key!r} has characters outside Latin-1") from None
        if not op["find"]:
            raise PatchError(f"{where}: 'find' is empty")
    else:
        _check_path(op["source"])


def load(path):
    """Read and validate a patch set; raise PatchError if it can't be used."""
    try:
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
    except (OSError, ValueError) as e:
        raise PatchError(f"cannot read patch set {path}: {e}") from e
    if not isinstance(data, dict) or type(data.get("format")) is not int or data["format"] != FORMAT:
        raise PatchError(f"{path}: not a format {FORMAT} patch set")
    files = data.get("files")
    if not isinstance(files, list) or not files:
        raise PatchError(f"{path}: the patch set lists no files")
    seen = set()
    for n, entry in enumerate(files, 1):
        if not isinstance(entry, dict):
            raise PatchError(f"file {n}: not an object")
        rel = _check_path(entry.get("path"))
        if rel.lower() in seen:
            raise PatchError(f"{rel}: listed twice")
        seen.add(rel.lower())
        before, after, ops = entry.get("before"), entry.get("after"), entry.get("ops")
        if not _is_sha256(before):
            raise PatchError(f"{rel}: 'before' must be a lower-case SHA-256")
        if not (_is_sha256(after) or after == "source"):
            raise PatchError(f"{rel}: 'after' must be a lower-case SHA-256 or \"source\"")
        if before == after:
            raise PatchError(f"{rel}: 'before' and 'after' are the same")
        if not isinstance(ops, list) or not ops:
            raise PatchError(f"{rel}: no operations")
        for i, op in enumerate(ops, 1):
            _check_op(op, f"{rel} op {i}")
        if after == "source" and (len(ops) != 1 or ops[0]["op"] != "file"):
            raise PatchError(f"{rel}: \"after\": \"source\" needs exactly one file operation")
    return data


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 22), b""):
            h.update(block)
    return h.hexdigest()


def _target(client_dir, rel):
    return os.path.join(client_dir, *rel.split("/"))


def _bundled(bundle_dir, rel):
    path = os.path.join(bundle_dir or "", *rel.split("/"))
    if bundle_dir is None or not os.path.isfile(path):
        raise PatchError(f"bundled file missing: {path}")
    return path


def _after_hash(entry, bundle_dir):
    if entry["after"] == "source":
        return sha256_file(_bundled(bundle_dir, entry["ops"][0]["source"]))
    return entry["after"]


def transform(data, ops, bundle_dir):
    """Apply ops in order to a copy of data and return the new bytes."""
    out = bytearray(data)
    for i, op in enumerate(ops, 1):
        kind = op["op"]
        if kind == "replace":
            start, old, new = op["offset"], bytes.fromhex(op["from"]), bytes.fromhex(op["to"])
            if out[start:start + len(old)] != old:
                raise PatchError(f"op {i} (replace at {start:#x}): the bytes there are not the expected ones")
            out[start:start + len(old)] = new
        elif kind == "append":
            out += bytes.fromhex(op["data"])
        elif kind == "text-replace":
            text = out.decode("latin-1")
            count = text.count(op["find"])
            if count != 1:
                raise PatchError(f"op {i} (text-replace): found the text {count} times, not exactly once")
            out = bytearray(text.replace(op["find"], op["replace"]).encode("latin-1"))
        elif kind == "file":
            with open(_bundled(bundle_dir, op["source"]), "rb") as f:
                out = bytearray(f.read())
        else:
            raise PatchError(f"op {i}: unknown operation {kind!r}")
    return bytes(out)


def status(client_dir, patchset, bundle_dir):
    """[(path, state)] per file, state "unpatched", "patched", "unknown" or "missing"."""
    out = []
    for entry in patchset["files"]:
        target = _target(client_dir, entry["path"])
        if not os.path.isfile(target):
            out.append((entry["path"], "missing"))
            continue
        digest = sha256_file(target)
        if digest == _after_hash(entry, bundle_dir):
            out.append((entry["path"], "patched"))
        elif digest == entry["before"]:
            out.append((entry["path"], "unpatched"))
        else:
            out.append((entry["path"], "unknown"))
    return out


def _write_atomic(path, data, mode):
    """Write data to path through a temporary file in the same folder, then os.replace."""
    fd, tmp = tempfile.mkstemp(dir=os.path.dirname(path) or ".", prefix=".hearthdaoc-", suffix=".tmp")
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(data)
            f.flush()
            os.fsync(f.fileno())
        os.chmod(tmp, mode)
        os.replace(tmp, path)
    except BaseException:
        try:
            os.unlink(tmp)
        except FileNotFoundError:
            pass
        raise


def apply(client_dir, patchset, bundle_dir):
    """Patch every file that isn't patched yet; [(path, "patched" | "already")] per file.

    An unknown or missing file raises PatchError, listing every such file, and nothing is
    changed. Every new content is built and checked against its "after" hash before the first
    write.
    """
    states = status(client_dir, patchset, bundle_dir)
    refused = [f"{path} ({state})" for path, state in states if state in ("unknown", "missing")]
    if refused:
        raise PatchError("refused, nothing changed: " + ", ".join(refused))
    work = []
    for entry, (path, state) in zip(patchset["files"], states):
        if state == "patched":
            continue
        target = _target(client_dir, path)
        with open(target, "rb") as f:
            original = f.read()
        if hashlib.sha256(original).hexdigest() != entry["before"]:
            raise PatchError(f"{path} changed while it was being checked; nothing changed")
        new = transform(original, entry["ops"], bundle_dir)
        if hashlib.sha256(new).hexdigest() != _after_hash(entry, bundle_dir):
            raise PatchError(f"{path}: the patched file doesn't have the expected SHA-256; nothing changed")
        work.append((target, original, new, entry["before"]))
    for target, original, new, before in work:
        mode = stat.S_IMODE(os.stat(target).st_mode)
        backup = target + BACKUP_SUFFIX
        if not (os.path.isfile(backup) and sha256_file(backup) == before):
            _write_atomic(backup, original, mode)
        _write_atomic(target, new, mode)
    return [(path, "already" if state == "patched" else "patched") for path, state in states]


def restore_status(client_dir, patchset, bundle_dir):
    """[(path, state)] per file, in patch-set order, for a restore. The states:

    "patched" (the backup is the original and the file is the patched one: it can be restored),
    "no-backup", "original" (the file already is the original; its backup is kept),
    "wrong-backup" (the backup's hash isn't "before"), "missing" (the backup is fine but the file
    is gone) and "changed" (the backup is fine but the file is neither the original nor the
    patched one, for example a newer client installed after patching).
    """
    out = []
    for entry in patchset["files"]:
        target = _target(client_dir, entry["path"])
        backup = target + BACKUP_SUFFIX
        if not os.path.isfile(backup):
            state = "no-backup"
        elif sha256_file(backup) != entry["before"]:
            state = "wrong-backup"
        elif not os.path.isfile(target):
            state = "missing"
        else:
            digest = sha256_file(target)
            if digest == entry["before"]:
                state = "original"
            elif digest == _after_hash(entry, bundle_dir):
                state = "patched"
            else:
                state = "changed"
        out.append((entry["path"], state))
    return out


def restore(client_dir, patchset, bundle_dir):
    """Move each backup back over its patched file; [(path, "restored" | "not-patched")] per file.

    A backup goes back only over the patched file. A file that already is the original keeps its
    backup and counts as "not-patched", like a file without a backup. A wrong backup, or a file
    that is missing or has changed since it was patched, raises PatchError, listing every such
    file, and nothing is changed.
    """
    states = restore_status(client_dir, patchset, bundle_dir)
    problems = []
    for state, label, suffix in (("wrong-backup", "not the original file", BACKUP_SUFFIX),
                                 ("changed", "changed since it was patched", ""), ("missing", "missing", "")):
        paths = [path + suffix for path, s in states if s == state]
        if paths:
            problems.append(f"{label}: " + ", ".join(paths))
    if problems:
        raise PatchError("; ".join(problems))
    out = []
    for path, state in states:
        if state == "patched":
            target = _target(client_dir, path)
            os.replace(target + BACKUP_SUFFIX, target)
            out.append((path, "restored"))
        else:
            out.append((path, "not-patched"))
    return out
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -v`

Expected: all 27 library tests pass:
```
test_a_failed_write_leaves_the_file_intact (tests.test_patchset.ApplyTests.test_a_failed_write_leaves_the_file_intact) ... ok
...
test_text_replace_needs_exactly_one_match (tests.test_patchset.TransformTests.test_text_replace_needs_exactly_one_match) ... ok

----------------------------------------------------------------------
Ran 27 tests in 0.326s

OK
```

- [ ] **Step 5: Write the failing CLI tests**

Append this to the end of `client/patches/tests/test_patchset.py`, after two blank lines:

```python
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
```

- [ ] **Step 6: Run test to verify it fails**

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -v`

Expected: the 27 library tests still pass. All 7 CLI tests fail because the script doesn't exist yet. Python itself exits 2 for a missing script, so the exit-2 test fails on its message check:
```
ERROR: test_defaults_to_the_patch_set_and_bundle_next_to_the_script (tests.test_patchset.CliTests...)
FileNotFoundError: [Errno 2] No such file or directory: '<repo>/client/patches/apply_patches.py'
FAIL: test_a_file_changed_since_patching_is_not_restored_with_exit_3 ... AssertionError: 2 != 0
FAIL: test_bad_usage_and_invalid_patch_sets_exit_2 (tests.test_patchset.CliTests...)
AssertionError: 'the following arguments are required: --client' not found in "/usr/bin/python3: can't open file '<repo>/client/patches/apply_patches.py': [Errno 2] No such file or directory\n"
FAIL: test_check_apply_and_restore ... AssertionError: Tuples differ: (2, '') != (0, 'game.dll: unpatched\npregame/characte[62 chars]d\n')
FAIL: test_missing_file_is_refused_with_exit_3 ... AssertionError: Tuples differ: (2, '') != (3, 'Not patched: pregame/character_custom[47 chars].\n')
FAIL: test_unknown_file_is_refused_with_exit_3 ... AssertionError: Tuples differ: (2, '') != (3, 'Not patched: game.dll is not the file[150 chars].\n')
FAIL: test_wrong_backup_is_not_restored_with_exit_3 ... AssertionError: 2 != 0
...
Ran 34 tests in 0.436s

FAILED (failures=6, errors=1)
```

- [ ] **Step 7: Write the command-line applier**

Create `client/patches/apply_patches.py`:

```python
#!/usr/bin/env python3
"""Apply HearthDAoC's client patch set to a client folder (Windows: windows/patch-client.bat).

    apply_patches.py --client DIR [--patchset FILE] [--bundle DIR] [--restore | --check]

The patch set defaults to classic-creation.json next to this script. Bundled files, such as
splash.mpk, are looked up in the patch set's folder unless --bundle names another one.
--check only reports each file's state. --restore puts the backed-up originals back, but only
over files that are still the patched ones.

Exit codes:
  0  patched, already patched or restored (--check: every file is known)
  1  a file couldn't be read or written
  2  bad usage, or an invalid patch set or bundle
  3  refused: a client file is unknown or missing, or a backup isn't the original, or
     (--restore) a file has changed since it was patched; nothing was changed
"""
import argparse
import os
import sys

sys.dont_write_bytecode = True  # leave no __pycache__ in a player's client bundle
import patchset  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_PATCHSET = os.path.join(HERE, "classic-creation.json")
UNKNOWN = ("Not patched: {path} is not the file this HearthDAoC release supports (for example the 0.34b "
           "edition or a newer upstream client). The client still works with the standard creation screen.")
MISSING = "Not patched: {path} is missing from the client folder."
CHANGED = ("Not restored: {path} has changed since it was patched (for example a newer client was installed); "
           "the saved original is kept as {backup}.")
GONE = "Not restored: {path} is missing from the client folder; the saved original is kept as {backup}."


def run(args):
    try:
        pset = patchset.load(args.patchset)
    except patchset.PatchError as e:
        print(f"Error: invalid patch set: {e}", file=sys.stderr)
        return 2
    bundle = args.bundle or os.path.dirname(os.path.abspath(args.patchset))
    if args.restore:
        states = patchset.restore_status(args.client, pset, bundle)
        wrong = [path + patchset.BACKUP_SUFFIX for path, state in states if state == "wrong-backup"]
        if wrong:
            print(f"Not restored: not the original file: {', '.join(wrong)}. Nothing was changed.")
        for path, state in states:
            if state in ("changed", "missing"):
                message = CHANGED if state == "changed" else GONE
                print(message.format(path=path, backup=path + patchset.BACKUP_SUFFIX))
        if any(state in ("wrong-backup", "changed", "missing") for _path, state in states):
            return 3
        for path, result in patchset.restore(args.client, pset, bundle):
            print(f"Restored: {path}" if result == "restored" else f"Nothing to restore: {path}")
        return 0
    states = patchset.status(args.client, pset, bundle)
    refused = [(path, state) for path, state in states if state in ("unknown", "missing")]
    if args.check:
        for path, state in states:
            print(f"{path}: {state}")
        return 3 if refused else 0
    if refused:
        for path, state in refused:
            print((UNKNOWN if state == "unknown" else MISSING).format(path=path))
        return 3
    for path, result in patchset.apply(args.client, pset, bundle):
        if result == "patched":
            print(f"Patched: {path} (original saved as {path}{patchset.BACKUP_SUFFIX})")
        else:
            print(f"Already patched: {path}")
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description="Apply HearthDAoC's client patch set to a client folder.")
    parser.add_argument("--client", metavar="DIR", required=True, help="the client folder, the one with game.dll")
    parser.add_argument("--patchset", metavar="FILE", default=DEFAULT_PATCHSET,
                        help="patch set (default: classic-creation.json next to this script)")
    parser.add_argument("--bundle", metavar="DIR",
                        help="folder with the bundled files (default: the patch set's folder)")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--restore", action="store_true", help="put the original files back")
    mode.add_argument("--check", action="store_true", help="only report each file's state")
    args = parser.parse_args(argv)
    if not os.path.isdir(args.client):
        print(f"Error: client folder not found: {args.client}", file=sys.stderr)
        return 2
    try:
        return run(args)
    except patchset.PatchError as e:
        print(f"Error: {e}", file=sys.stderr)
        return 2
    except OSError as e:
        print(f"Error: {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
```

Make it executable:

```bash
chmod +x client/patches/apply_patches.py
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -v`

Expected:
```
test_a_file_changed_since_patching_is_not_restored_with_exit_3 (tests.test_patchset.CliTests.test_a_file_changed_since_patching_is_not_restored_with_exit_3) ... ok
test_bad_usage_and_invalid_patch_sets_exit_2 (tests.test_patchset.CliTests.test_bad_usage_and_invalid_patch_sets_exit_2) ... ok
test_check_apply_and_restore (tests.test_patchset.CliTests.test_check_apply_and_restore) ... ok
test_defaults_to_the_patch_set_and_bundle_next_to_the_script (tests.test_patchset.CliTests.test_defaults_to_the_patch_set_and_bundle_next_to_the_script) ... ok
test_missing_file_is_refused_with_exit_3 (tests.test_patchset.CliTests.test_missing_file_is_refused_with_exit_3) ... ok
test_unknown_file_is_refused_with_exit_3 (tests.test_patchset.CliTests.test_unknown_file_is_refused_with_exit_3) ... ok
test_wrong_backup_is_not_restored_with_exit_3 (tests.test_patchset.CliTests.test_wrong_backup_is_not_restored_with_exit_3) ... ok
...
----------------------------------------------------------------------
Ran 34 tests in 1.017s

OK
```

Then check the bad-usage paths by hand. `classic-creation.json` doesn't exist until Task 3, so the default patch set can't be read yet:

Run: `python3 client/patches/apply_patches.py; echo "rc=$?"; python3 client/patches/apply_patches.py --client /nonexistent; echo "rc=$?"; python3 client/patches/apply_patches.py --client client; echo "rc=$?"`

Expected:
```
usage: apply_patches.py [-h] --client DIR [--patchset FILE] [--bundle DIR]
                        [--restore | --check]
apply_patches.py: error: the following arguments are required: --client
rc=2
Error: client folder not found: /nonexistent
rc=2
Error: invalid patch set: cannot read patch set <repo>/client/patches/classic-creation.json: [Errno 2] No such file or directory: '<repo>/client/patches/classic-creation.json'
rc=2
```

Run: `git status --short --untracked-files=all`

Expected: only the four new files. `__pycache__` is ignored by `.gitignore` (`**/__pycache__/`).
```
?? client/patches/apply_patches.py
?? client/patches/patchset.py
?? client/patches/tests/__init__.py
?? client/patches/tests/test_patchset.py
```

- [ ] **Step 9: Commit**

```bash
git add client/patches/patchset.py client/patches/apply_patches.py client/patches/tests/__init__.py client/patches/tests/test_patchset.py
git commit -m "feat(client): patch-set format and Linux applier for client patches

patchset.py loads and validates format-1 patch sets (safe relative paths,
replace/append/text-replace/file operations) and checks, applies and restores
them: unknown or missing files are refused before anything is written, the
original is backed up once as <file>.hearthdaoc-orig, writes are atomic, and
a restore puts an original back only over the patched file (a file changed
since, such as a newer client, is refused). apply_patches.py is the command
line (exit 0 done, 2 bad usage or patch set, 3 refused with nothing changed).

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git ls-files -s client/patches/apply_patches.py
```

Expected: `100755 ... 0	client/patches/apply_patches.py`.

---

### Task 2: Windows applier and cross-applier tests

**Files:**
- Create: `client/windows/patch-client.ps1` (Windows applier; runs on Windows PowerShell 5.1; ASCII with CRLF line ends)
- Create: `client/windows/patch-client.bat` (double-click launcher; ASCII with CRLF line ends, like `connect-hearthdaoc.bat`)
- Test: `client/patches/tests/test_appliers.py` (the same command-line cases against `apply_patches.py` and `patch-client.ps1`, plus static checks of the two Windows files)

**Interfaces:**
- Consumes, from Task 1 (already committed on `sub2-classic-creation`):
  - `client/patches/apply_patches.py --client DIR [--patchset FILE] [--bundle DIR] [--restore | --check]`. It is the reference: its output lines and exit codes (Task 1's table) are what the PowerShell applier must reproduce.
  - From `client/patches/tests/test_patchset.py`, imported as `tests.test_patchset` with the top level `-t client/patches`: `Fixture`, `fixture_patchset`, `write_patchset`, `sha`, `read`, `write`, `DLL`, `DLL_PATCHED`, `XML`, `XML_PATCHED`, `SPLASH`, `SPLASH_NEW`, `STRANGER`, `FILES`, `UNKNOWN_MESSAGE`.
- Produces:
  - `client/windows/patch-client.ps1 [-Client DIR] [-PatchSet FILE] [-Bundle DIR] [-Restore | -Check] [-Help]`.
    - Defaults: `-Client` is the script's own folder (`$PSScriptRoot`, which is the `.bat`'s folder). `-PatchSet` is `patches\classic-creation.json` next to the script. `-Bundle` is the patch set's folder.
    - Option names are case-insensitive, and `--client` style is accepted too.
    - Same stdout lines, stderr lines and exit codes as Task 1's table (0 done, 1 file system error, 2 bad usage or invalid patch set or bundle, 3 refused with nothing changed), and the same validation messages after `Error: invalid patch set: `.
    - Usage errors (unknown argument, missing value, `-Restore` with `-Check`): stderr `Usage: patch-client.ps1 [-Client DIR] [-PatchSet FILE] [-Bundle DIR] [-Restore | -Check]` and `patch-client.ps1: error: <reason>`, exit 2. The script parses `$args` by hand (no `param()` block) because PowerShell's own binding error for a missing value exits 1, not 2.
  - `client/windows/patch-client.bat`: runs `powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1" %*`, pauses only when started from Explorer, and exits with the script's exit code.
  - Folder layout for Task 7 (client bundle) and Task 8 (README): `patch-client.bat`, `patch-client.ps1` and a `patches\` folder holding `classic-creation.json` and `splash.mpk` sit side by side, next to `connect-hearthdaoc.bat` in the install's `runtime\client-opendaoc\app` folder (the client folder, the one with `game.dll`).
  - `client/patches/tests/test_appliers.py`:
    - `ApplierCases`: a mixin with `FLAGS`, `command()` and `run_tool(*switches, client=None, patchset=None, bundle=None, cwd=None)`.
    - `PythonApplierTests` and `PowerShellApplierTests`. The PowerShell class is skipped unless `pwsh` is on `PATH` or `HDC_PWSH` names it.
    - `INSTALL`: the folders of a usual Windows install (`Program Files (x86)`, `Offline DAoC v0.34`, ...), with spaces and parentheses. Both appliers check, patch and restore a client there, and the PowerShell class also runs the script there the way `patch-client.bat` does.
    - `WindowsFilesTests` and `INVALID` (name: (change to the fixture patch set, expected reason)).
    - A later applier change adds its case to `ApplierCases` once, and both appliers run it.
    - Task 8's CI runs `python3 -m unittest discover -s client/patches/tests -t client/patches -v`. GitHub's Ubuntu runners have `pwsh`, which adds about 45 seconds.

PowerShell 5.1 pitfalls the script handles (keep them if you edit it):
- No `?:`, `??`, `&&` or `||`, and no `ConvertFrom-Json -AsHashtable`.
- PowerShell property names are case-insensitive, so `Get-Field` checks the JSON key's exact case.
- `$null` passed to a .NET `string` parameter arrives as `''`, so `File.Replace` gets `[System.Management.Automation.Language.NullString]::Value`.
- .NET's current directory doesn't follow PowerShell's location, so every path is made absolute with `GetUnresolvedProviderPathFromPSPath`.
- Functions return byte arrays as `return , $bytes`, because PowerShell would otherwise unroll them.
- Text search uses `[StringComparison]::Ordinal`.
- Regular expressions end in `\z`, because in .NET `$` also matches before a final newline.
- The file is ASCII only, because 5.1 reads a script without a BOM in the ANSI code page.

All commands run from the repository root, on branch `sub2-classic-creation`, with Task 1 committed.

- [ ] **Step 1: Write the failing test**

Create `client/patches/tests/test_appliers.py`:

```python
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

from tests.test_patchset import (CHANGED_MESSAGE, DLL, DLL_PATCHED, FILES, SPLASH, SPLASH_NEW, STRANGER,
                                 UNKNOWN_MESSAGE, XML, XML_PATCHED, Fixture, fixture_patchset, read, sha, write,
                                 write_patchset)

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


SHA_RULE = "game.dll: 'after' must be a lower-case SHA-256 or \"source\""
OFFSET_RULE = "game.dll op 1: offset must be a whole number, 0 or more"
HEX_RULE = "game.dll op 1: not lower-case hex bytes"
XML_OP = "pregame/character_customize_stats.xml op 1"

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
    "unsafe bundled source": (_op(2, source="../splash.mpk"), "unsafe path: '../splash.mpk'"),
    "source without a file op": (_file(0, after="source"),
                                 "game.dll: \"after\": \"source\" needs exactly one file operation"),
}


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

    def test_bat_runs_the_script_and_returns_its_exit_code(self):
        text = read(BAT).decode("ascii")
        self.assertIn('powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1" %*\r\n', text)
        self.assertIn('set "RC=%ERRORLEVEL%"\r\n', text)
        self.assertIn(" pause\r\n", text)  # only when double-clicked, so the player can read the result
        self.assertTrue(text.endswith("exit /b %RC%\r\n"))

    def test_ps1_avoids_powershell_7_only_syntax(self):
        text = read(PS1).decode("ascii")
        for token in ("??", "?.", "&&", "||", "-AsHashtable", "-NoEnumerate", "FromHexString", "::Latin1",
                      "$IsWindows", "$IsLinux", "-Parallel", "Join-Path -AdditionalChildPath"):
            self.assertNotIn(token, text)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run test to verify it fails**

The PowerShell cases need `pwsh` (PowerShell 7). If `command -v pwsh` prints nothing, install it as a .NET tool in a temporary folder. This changes nothing global:

```bash
dotnet tool install PowerShell --tool-path /tmp/hdc-pwsh
export HDC_PWSH=/tmp/hdc-pwsh/pwsh
```

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -v`

Expected:
- All 15 Python applier cases pass. They are the reference, and Task 1 already makes them pass.
- Every PowerShell case fails, because `pwsh` exits 64 when the script doesn't exist.
- The Windows file checks error.

```
test_a_backup_is_made_once_and_a_wrong_one_is_replaced (tests.test_appliers.PythonApplierTests.test_a_backup_is_made_once_and_a_wrong_one_is_replaced) ... ok
...
FAIL: test_apply_patches_every_file_and_backs_up_the_originals (tests.test_appliers.PowerShellApplierTests.test_apply_patches_every_file_and_backs_up_the_originals)
AssertionError: Tuples differ: (64, '\nUsage: pwsh[.exe] [-Login] [[-File[NNNN chars].\n") != (0, 'Patched: game.dll (original saved as [234 chars], '')
...
ERROR: test_defaults_to_its_own_folder_and_the_patches_folder_next_to_it (tests.test_appliers.PowerShellApplierTests.test_defaults_to_its_own_folder_and_the_patches_folder_next_to_it)
FileNotFoundError: [Errno 2] No such file or directory: '<repo>/client/windows/patch-client.ps1'
...
ERROR: test_the_bat_command_line_in_a_folder_with_spaces_and_parentheses (tests.test_appliers.PowerShellApplierTests.test_the_bat_command_line_in_a_folder_with_spaces_and_parentheses)
FileNotFoundError: [Errno 2] No such file or directory: '<repo>/client/windows/patch-client.ps1'
...
Ran 69 tests in 11.839s

FAILED (failures=48, errors=6)
```
(`NNNN` is a character count that depends on the clone's path.)

Without `pwsh` the 17 PowerShell cases are skipped (`skipped 'pwsh is not installed (HDC_PWSH may name it)'`). Then only the Windows file checks error: `FAILED (errors=4, skipped=17)`.

- [ ] **Step 3: Write minimal implementation**

Create `client/windows/patch-client.ps1`:

```powershell
<#
.SYNOPSIS
    Apply HearthDAoC's client patch set to an OfflineDAoC client folder (Windows).

.DESCRIPTION
    patch-client.ps1 [-Client DIR] [-PatchSet FILE] [-Bundle DIR] [-Restore | -Check]

    Players double-click patch-client.bat, which runs this script. Put both files and the
    patches folder next to connect-hearthdaoc.bat, in runtime\client-opendaoc\app of the
    OfflineDAoC install.

    -Client    the client folder, the one with game.dll (default: this script's folder)
    -PatchSet  the patch set (default: patches\classic-creation.json next to this script)
    -Bundle    the folder with the bundled files, such as splash.mpk (default: the patch set's folder)
    -Check     only report each file's state
    -Restore   put the original files back (only over files that are still the patched ones)

    The rules, messages and exit codes are the ones of the Linux applier,
    client/patches/apply_patches.py with client/patches/patchset.py:
    - A file whose SHA-256 is its "after" hash is already patched and is skipped.
    - A file that is missing, or that is neither the expected original nor the patched file,
      is refused. Then nothing at all is changed: every file is checked, and every new content
      built and verified, before anything is written.
    - The original is backed up once as <file>.hearthdaoc-orig. A backup that isn't the
      original is replaced by the verified original.
    - A restore puts a backup back only over the patched file. A file that has changed since
      it was patched (for example a newer client) or is missing is refused, and then nothing
      is restored.
    - Every write goes through a temporary file in the same folder and then replaces the file.
    - Only the patch set's own relative paths inside the client folder are touched.

    Exit codes:
      0  patched, already patched or restored (-Check: every file is known)
      1  a file couldn't be read or written
      2  bad usage, or an invalid patch set or bundle
      3  refused: a client file is unknown or missing, or a backup isn't the original, or
         (-Restore) a file has changed since it was patched; nothing was changed

    Players run Windows PowerShell 5.1: no PowerShell 7-only syntax or .NET Core-only APIs,
    and keep this file ASCII with CRLF line ends.
#>
$ErrorActionPreference = 'Stop'

$BackupSuffix = '.hearthdaoc-orig'
$OpNames = @('replace', 'append', 'text-replace', 'file')
$OpFields = @{
    'replace'      = @('offset', 'from', 'to')
    'append'       = @('data')
    'text-replace' = @('find', 'replace')
    'file'         = @('source')
}
$Latin1 = [Text.Encoding]::GetEncoding(28591)
$Usage = 'Usage: patch-client.ps1 [-Client DIR] [-PatchSet FILE] [-Bundle DIR] [-Restore | -Check]'
$UnknownMessage = 'Not patched: {0} is not the file this HearthDAoC release supports (for example the 0.34b ' +
    'edition or a newer upstream client). The client still works with the standard creation screen.'
$MissingMessage = 'Not patched: {0} is missing from the client folder.'
$ChangedMessage = 'Not restored: {0} has changed since it was patched (for example a newer client was installed); ' +
    'the saved original is kept as {0}{1}.'
$GoneMessage = 'Not restored: {0} is missing from the client folder; the saved original is kept as {0}{1}.'

function Write-Out([string]$Text) { [Console]::Out.WriteLine($Text) }

function Write-Err([string]$Text) { [Console]::Error.WriteLine($Text) }

function New-PatchError([string]$Message) {
    # Patch-set and check failures (PatchError in patchset.py) are ApplicationExceptions;
    # anything else is a file system error.
    New-Object System.ApplicationException $Message
}

function Get-Cause($ErrorRecord) {
    $e = $ErrorRecord.Exception
    while ($e -is [System.Management.Automation.MethodInvocationException] -and $null -ne $e.InnerException) {
        $e = $e.InnerException
    }
    $e
}

function Format-Value($Value) {
    # Python's repr() for the values a patch set can hold, as patchset.py prints them.
    if ($null -eq $Value) { return 'None' }
    if ($Value -is [string]) { return "'" + $Value + "'" }
    return [string]$Value
}

function Test-Object($Value) { $Value -is [System.Management.Automation.PSCustomObject] }

function Test-List($Value) { $Value -is [System.Collections.IList] }

function Test-Integer($Value) { ($Value -is [int]) -or ($Value -is [long]) }

function Test-Sha256($Value) { ($Value -is [string]) -and ($Value -cmatch '^[0-9a-f]{64}\z') }

function Test-Field($Object, [string]$Name) {
    # JSON keys are case-sensitive, PowerShell property names are not.
    $p = $Object.PSObject.Properties[$Name]
    ($null -ne $p) -and ($p.Name -ceq $Name)
}

function Get-Field($Object, [string]$Name) {
    if (Test-Field $Object $Name) { return , $Object.PSObject.Properties[$Name].Value }
    return $null
}

function Get-FullPath([string]$Path) {
    # Relative to PowerShell's current location, which .NET's current directory may not follow.
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

function Join-Rel([string]$Base, [string]$Rel) {
    $path = $Base
    foreach ($part in $Rel.Split('/')) { $path = [IO.Path]::Combine($path, $part) }
    $path
}

function Assert-SafePath($Rel) {
    # Only a relative path with forward slashes that stays inside its folder.
    $ok = ($Rel -is [string]) -and $Rel.Length -gt 0 -and -not $Rel.StartsWith('/', [StringComparison]::Ordinal) -and
        -not $Rel.Contains('\') -and -not $Rel.Contains(':')
    if ($ok) {
        foreach ($part in $Rel.Split('/')) {
            if ($part -eq '' -or $part -eq '.' -or $part -eq '..') { $ok = $false }
        }
    }
    if (-not $ok) { throw (New-PatchError ('unsafe path: ' + (Format-Value $Rel))) }
}

function ConvertFrom-Hex($Value, [string]$Where) {
    if (-not ($Value -is [string]) -or -not ($Value -cmatch '^(?:[0-9a-f]{2})+\z')) {
        throw (New-PatchError ('{0}: not lower-case hex bytes' -f $Where))
    }
    $bytes = New-Object byte[] ($Value.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) { $bytes[$i] = [Convert]::ToByte($Value.Substring(2 * $i, 2), 16) }
    return , $bytes
}

function Assert-Op($Op, [string]$Where) {
    $kind = $null
    if (Test-Object $Op) { $kind = Get-Field $Op 'op' }
    if (-not ($kind -is [string]) -or -not ($kind -cin $OpNames)) {
        throw (New-PatchError ('{0}: unknown operation' -f $Where))
    }
    foreach ($key in $OpFields[$kind]) {
        if (-not (Test-Field $Op $key)) { throw (New-PatchError ("{0}: {1} needs '{2}'" -f $Where, $kind, $key)) }
    }
    if ($kind -ceq 'replace') {
        $offset = Get-Field $Op 'offset'
        if (-not (Test-Integer $offset) -or $offset -lt 0) {
            throw (New-PatchError ('{0}: offset must be a whole number, 0 or more' -f $Where))
        }
        $from = ConvertFrom-Hex (Get-Field $Op 'from') $Where
        $to = ConvertFrom-Hex (Get-Field $Op 'to') $Where
        if ($from.Length -ne $to.Length) {
            throw (New-PatchError ("{0}: 'from' and 'to' must have the same length" -f $Where))
        }
    } elseif ($kind -ceq 'append') {
        $null = ConvertFrom-Hex (Get-Field $Op 'data') $Where
    } elseif ($kind -ceq 'text-replace') {
        foreach ($key in @('find', 'replace')) {
            $text = Get-Field $Op $key
            if (-not ($text -is [string])) { throw (New-PatchError ("{0}: '{1}' must be text" -f $Where, $key)) }
            if ($text -match '[^\x00-\xFF]') {
                throw (New-PatchError ("{0}: '{1}' has characters outside Latin-1" -f $Where, $key))
            }
        }
        if ((Get-Field $Op 'find').Length -eq 0) { throw (New-PatchError ("{0}: 'find' is empty" -f $Where)) }
    } else {
        Assert-SafePath (Get-Field $Op 'source')
    }
}

function Read-PatchSet([string]$Path, [string]$Shown) {
    # Read and validate a patch set like patchset.load; $Shown is the path as given, for messages.
    try {
        $data = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8))
    } catch {
        throw (New-PatchError ('cannot read patch set {0}: {1}' -f $Shown, (Get-Cause $_).Message))
    }
    $format = $null
    if (Test-Object $data) { $format = Get-Field $data 'format' }
    if (-not (Test-Integer $format) -or $format -ne 1) {
        throw (New-PatchError ('{0}: not a format 1 patch set' -f $Shown))
    }
    $files = Get-Field $data 'files'
    if (-not (Test-List $files) -or $files.Count -eq 0) {
        throw (New-PatchError ('{0}: the patch set lists no files' -f $Shown))
    }
    $seen = @{}
    $n = 0
    foreach ($entry in $files) {
        $n++
        if (-not (Test-Object $entry)) { throw (New-PatchError ('file {0}: not an object' -f $n)) }
        $rel = Get-Field $entry 'path'
        Assert-SafePath $rel
        $key = $rel.ToLowerInvariant()
        if ($seen.ContainsKey($key)) { throw (New-PatchError ('{0}: listed twice' -f $rel)) }
        $seen[$key] = $true
        $before = Get-Field $entry 'before'
        $after = Get-Field $entry 'after'
        $ops = Get-Field $entry 'ops'
        if (-not (Test-Sha256 $before)) {
            throw (New-PatchError ("{0}: 'before' must be a lower-case SHA-256" -f $rel))
        }
        if (-not ((Test-Sha256 $after) -or ($after -ceq 'source'))) {
            throw (New-PatchError ("{0}: 'after' must be a lower-case SHA-256 or `"source`"" -f $rel))
        }
        if ($before -ceq $after) { throw (New-PatchError ("{0}: 'before' and 'after' are the same" -f $rel)) }
        if (-not (Test-List $ops) -or $ops.Count -eq 0) { throw (New-PatchError ('{0}: no operations' -f $rel)) }
        $i = 0
        foreach ($op in $ops) {
            $i++
            Assert-Op $op ('{0} op {1}' -f $rel, $i)
        }
        if ($after -ceq 'source' -and ($ops.Count -ne 1 -or (Get-Field $ops[0] 'op') -cne 'file')) {
            throw (New-PatchError ('{0}: "after": "source" needs exactly one file operation' -f $rel))
        }
    }
    return $data
}

function Get-Sha256([byte[]]$Data) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash($Data) } finally { $sha.Dispose() }
    ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
}

function Get-FileSha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Bundled([string]$BundleDir, [string]$Rel) {
    $path = Join-Rel $BundleDir $Rel
    if (-not $BundleDir -or -not [IO.File]::Exists($path)) {
        throw (New-PatchError ('bundled file missing: {0}' -f $path))
    }
    $path
}

function Get-AfterHash($Entry, [string]$BundleDir) {
    $after = Get-Field $Entry 'after'
    if ($after -ceq 'source') {
        $ops = Get-Field $Entry 'ops'
        return Get-FileSha256 (Get-Bundled $BundleDir (Get-Field $ops[0] 'source'))
    }
    $after
}

function Invoke-Ops([byte[]]$Data, $Ops, [string]$BundleDir) {
    # Apply the ops in order to a copy of $Data and return the new bytes, like patchset.transform.
    $out = [byte[]]$Data.Clone()
    $i = 0
    foreach ($op in $Ops) {
        $i++
        $kind = Get-Field $op 'op'
        if ($kind -ceq 'replace') {
            $start = [long](Get-Field $op 'offset')
            $old = ConvertFrom-Hex (Get-Field $op 'from') 'from'
            $new = ConvertFrom-Hex (Get-Field $op 'to') 'to'
            $same = ($start + $old.Length) -le $out.Length
            for ($k = 0; $same -and $k -lt $old.Length; $k++) {
                if ($out[[int]$start + $k] -ne $old[$k]) { $same = $false }
            }
            if (-not $same) {
                $message = 'op {0} (replace at 0x{1:x}): the bytes there are not the expected ones' -f $i, $start
                throw (New-PatchError $message)
            }
            [Array]::Copy($new, 0, $out, [int]$start, $new.Length)
        } elseif ($kind -ceq 'append') {
            $tail = ConvertFrom-Hex (Get-Field $op 'data') 'data'
            $grown = New-Object byte[] ($out.Length + $tail.Length)
            [Array]::Copy($out, $grown, $out.Length)
            [Array]::Copy($tail, 0, $grown, $out.Length, $tail.Length)
            $out = $grown
        } elseif ($kind -ceq 'text-replace') {
            # Latin-1 maps every byte to one character and back, so CRLF and other bytes are kept.
            $find = Get-Field $op 'find'
            $text = $Latin1.GetString($out)
            $count = 0
            $at = $text.IndexOf($find, [StringComparison]::Ordinal)
            while ($at -ge 0) {
                $count++
                $at = $text.IndexOf($find, $at + $find.Length, [StringComparison]::Ordinal)
            }
            if ($count -ne 1) {
                $message = 'op {0} (text-replace): found the text {1} times, not exactly once' -f $i, $count
                throw (New-PatchError $message)
            }
            $out = $Latin1.GetBytes($text.Replace($find, (Get-Field $op 'replace')))
        } elseif ($kind -ceq 'file') {
            $out = [IO.File]::ReadAllBytes((Get-Bundled $BundleDir (Get-Field $op 'source')))
        } else {
            throw (New-PatchError ("op {0}: unknown operation '{1}'" -f $i, $kind))
        }
    }
    return , $out
}

function Get-States([string]$ClientDir, $Files, [string]$BundleDir) {
    # One object per file, in patch-set order: Path, State (unpatched, patched, unknown or missing),
    # Target and Entry.
    foreach ($entry in $Files) {
        $rel = Get-Field $entry 'path'
        $target = Join-Rel $ClientDir $rel
        if (-not [IO.File]::Exists($target)) {
            $state = 'missing'
        } else {
            $digest = Get-FileSha256 $target
            if ($digest -ceq (Get-AfterHash $entry $BundleDir)) {
                $state = 'patched'
            } elseif ($digest -ceq (Get-Field $entry 'before')) {
                $state = 'unpatched'
            } else {
                $state = 'unknown'
            }
        }
        [pscustomobject]@{ Path = $rel; State = $state; Target = $target; Entry = $entry }
    }
}

function Get-RestoreStates([string]$ClientDir, $Files, [string]$BundleDir) {
    # One object per file, in patch-set order, like patchset.restore_status: Path, State (patched,
    # no-backup, original, wrong-backup, missing or changed), Target and Backup.
    foreach ($entry in $Files) {
        $rel = Get-Field $entry 'path'
        $target = Join-Rel $ClientDir $rel
        $backup = $target + $BackupSuffix
        $before = Get-Field $entry 'before'
        if (-not [IO.File]::Exists($backup)) {
            $state = 'no-backup'
        } elseif ((Get-FileSha256 $backup) -cne $before) {
            $state = 'wrong-backup'
        } elseif (-not [IO.File]::Exists($target)) {
            $state = 'missing'
        } else {
            $digest = Get-FileSha256 $target
            if ($digest -ceq $before) {
                $state = 'original'
            } elseif ($digest -ceq (Get-AfterHash $entry $BundleDir)) {
                $state = 'patched'
            } else {
                $state = 'changed'
            }
        }
        [pscustomobject]@{ Path = $rel; State = $state; Target = $target; Backup = $backup }
    }
}

function Move-Over([string]$Source, [string]$Destination) {
    # Like Python's os.replace. File.Replace is ReplaceFile on Windows and rename on Unix; a
    # $null backup name would reach .NET as '', hence NullString.
    if ([IO.File]::Exists($Destination)) {
        [IO.File]::Replace($Source, $Destination, [System.Management.Automation.Language.NullString]::Value)
    } else {
        [IO.File]::Move($Source, $Destination)
    }
}

function Write-Atomic([string]$Path, [byte[]]$Data) {
    # Write through a temporary file in the same folder, flushed to disk, then replace $Path.
    $name = '.hearthdaoc-' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $tmp = [IO.Path]::Combine([IO.Path]::GetDirectoryName($Path), $name)
    try {
        $stream = New-Object IO.FileStream($tmp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
        try {
            $stream.Write($Data, 0, $Data.Length)
            $stream.Flush($true)
        } finally {
            $stream.Dispose()
        }
        Move-Over $tmp $Path
    } catch {
        if ([IO.File]::Exists($tmp)) { [IO.File]::Delete($tmp) }
        throw
    }
}

# Command line, parsed by hand so that every usage error exits with 2, as in apply_patches.py.
$Options = @{ Client = ''; PatchSet = ''; Bundle = ''; Restore = $false; Check = $false; Help = $false }
$UsageError = ''
$i = 0
while ($i -lt $args.Count -and -not $UsageError) {
    $arg = [string]$args[$i]
    $name = ''
    if ($arg -match '^--?([A-Za-z]+)$') { $name = $Matches[1] }
    if ($name -in @('Client', 'PatchSet', 'Bundle')) {
        if ($i + 1 -lt $args.Count) {
            $Options[$name] = [string]$args[$i + 1]
            $i++
        } else {
            $UsageError = $arg + ' needs a value'
        }
    } elseif ($name -in @('Restore', 'Check', 'Help')) {
        $Options[$name] = $true
    } else {
        $UsageError = 'unknown argument: ' + $arg
    }
    $i++
}
if ($Options.Help -and -not $UsageError) {
    Write-Out $Usage
    exit 0
}
if (-not $UsageError -and $Options.Restore -and $Options.Check) {
    $UsageError = '-Restore and -Check cannot be used together'
}
if ($UsageError) {
    Write-Err $Usage
    Write-Err ('patch-client.ps1: error: ' + $UsageError)
    exit 2
}
if (-not $Options.Client) { $Options.Client = $PSScriptRoot }
if (-not $Options.PatchSet) {
    $Options.PatchSet = [IO.Path]::Combine($PSScriptRoot, 'patches', 'classic-creation.json')
}

try {
    $ClientDir = Get-FullPath $Options.Client
    if (-not [IO.Directory]::Exists($ClientDir)) {
        Write-Err ('Error: client folder not found: {0}' -f $Options.Client)
        exit 2
    }
    $PatchSetPath = Get-FullPath $Options.PatchSet
    try {
        $Set = Read-PatchSet $PatchSetPath $Options.PatchSet
    } catch {
        Write-Err ('Error: invalid patch set: {0}' -f (Get-Cause $_).Message)
        exit 2
    }
    $Files = Get-Field $Set 'files'
    if ($Options.Bundle) {
        $BundleDir = Get-FullPath $Options.Bundle
    } else {
        $BundleDir = [IO.Path]::GetDirectoryName($PatchSetPath)
    }

    if ($Options.Restore) {
        # Check every file first: a backup goes back only over the patched file, and any problem
        # means nothing is restored.
        $Restore = @(Get-RestoreStates $ClientDir $Files $BundleDir)
        $wrong = @($Restore | Where-Object { $_.State -eq 'wrong-backup' } | ForEach-Object { $_.Path + $BackupSuffix })
        if ($wrong.Count -gt 0) {
            Write-Out ('Not restored: not the original file: {0}. Nothing was changed.' -f ($wrong -join ', '))
        }
        $refused = $wrong.Count
        foreach ($s in $Restore) {
            if ($s.State -eq 'changed') {
                Write-Out ($ChangedMessage -f $s.Path, $BackupSuffix)
                $refused++
            } elseif ($s.State -eq 'missing') {
                Write-Out ($GoneMessage -f $s.Path, $BackupSuffix)
                $refused++
            }
        }
        if ($refused -gt 0) { exit 3 }
        foreach ($s in $Restore) {
            if ($s.State -eq 'patched') { Move-Over $s.Backup $s.Target }
        }
        foreach ($s in $Restore) {
            if ($s.State -eq 'patched') {
                Write-Out ('Restored: {0}' -f $s.Path)
            } else {
                Write-Out ('Nothing to restore: {0}' -f $s.Path)
            }
        }
        exit 0
    }

    $States = @(Get-States $ClientDir $Files $BundleDir)
    $Refused = @($States | Where-Object { $_.State -eq 'unknown' -or $_.State -eq 'missing' })
    if ($Options.Check) {
        foreach ($s in $States) { Write-Out ('{0}: {1}' -f $s.Path, $s.State) }
        if ($Refused.Count -gt 0) { exit 3 }
        exit 0
    }
    if ($Refused.Count -gt 0) {
        foreach ($s in $Refused) {
            if ($s.State -eq 'unknown') {
                Write-Out ($UnknownMessage -f $s.Path)
            } else {
                Write-Out ($MissingMessage -f $s.Path)
            }
        }
        exit 3
    }

    # Build and check every new content before the first write.
    $Work = @()
    foreach ($s in $States) {
        if ($s.State -eq 'patched') { continue }
        $before = Get-Field $s.Entry 'before'
        $original = [IO.File]::ReadAllBytes($s.Target)
        if ((Get-Sha256 $original) -cne $before) {
            throw (New-PatchError ('{0} changed while it was being checked; nothing changed' -f $s.Path))
        }
        $new = Invoke-Ops $original (Get-Field $s.Entry 'ops') $BundleDir
        if ((Get-Sha256 $new) -cne (Get-AfterHash $s.Entry $BundleDir)) {
            $message = "{0}: the patched file doesn't have the expected SHA-256; nothing changed" -f $s.Path
            throw (New-PatchError $message)
        }
        $Work += [pscustomobject]@{ Target = $s.Target; Original = $original; New = $new; Before = $before }
    }
    foreach ($w in $Work) {
        $backup = $w.Target + $BackupSuffix
        if (-not ([IO.File]::Exists($backup) -and (Get-FileSha256 $backup) -ceq $w.Before)) {
            Write-Atomic $backup $w.Original
        }
        Write-Atomic $w.Target $w.New
    }
    foreach ($s in $States) {
        if ($s.State -eq 'patched') {
            Write-Out ('Already patched: {0}' -f $s.Path)
        } else {
            Write-Out ('Patched: {0} (original saved as {0}{1})' -f $s.Path, $BackupSuffix)
        }
    }
    exit 0
} catch {
    $cause = Get-Cause $_
    Write-Err ('Error: {0}' -f $cause.Message)
    if ($cause -is [System.ApplicationException]) { exit 2 }
    exit 1
}
```

Give it CRLF line ends. The conversion is idempotent:

```bash
python3 -c "import pathlib, sys; p = pathlib.Path(sys.argv[1]); p.write_bytes(p.read_bytes().replace(b'\r\n', b'\n').replace(b'\n', b'\r\n'))" client/windows/patch-client.ps1
file client/windows/patch-client.ps1
```

Expected: `client/windows/patch-client.ps1: ASCII text, with CRLF line terminators`

- [ ] **Step 4: Run the tests to verify the PowerShell cases pass**

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -v` (with `pwsh` on `PATH` or `HDC_PWSH` set)

Expected: every Python and PowerShell case passes. Only the `.bat` checks still error, because it doesn't exist yet:
```
ERROR: test_bat_runs_the_script_and_returns_its_exit_code (tests.test_appliers.WindowsFilesTests.test_bat_runs_the_script_and_returns_its_exit_code)
FileNotFoundError: [Errno 2] No such file or directory: '<repo>/client/windows/patch-client.bat'
ERROR: test_windows_files_are_ascii_with_crlf_line_ends (tests.test_appliers.WindowsFilesTests.test_windows_files_are_ascii_with_crlf_line_ends) [patch-client.bat]
FileNotFoundError: [Errno 2] No such file or directory: '<repo>/client/windows/patch-client.bat'
...
Ran 69 tests in 59.727s

FAILED (errors=2)
```

- [ ] **Step 5: Write the double-click launcher**

Create `client/windows/patch-client.bat`:

```bat
@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Give your OfflineDAoC client HearthDAoC's classic character creation screen.
rem Put this file, patch-client.ps1 and the patches folder next to connect-hearthdaoc.bat
rem (runtime\client-opendaoc\app of your OfflineDAoC install) and double-click it. Run it again
rem whenever something puts the original files back. Options go to patch-client.ps1:
rem -Check only reports, -Restore puts the original files back.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1" %*
set "RC=%ERRORLEVEL%"
rem Keep the window open when started from Explorer, whose command line names this file.
rem The quotes are removed first, so the comparison is safe for any folder name.
setlocal EnableDelayedExpansion
set "LINE=!CMDCMDLINE!"
if defined LINE set "LINE=!LINE:"=!"
if defined LINE if /i not "!LINE:%~nx0=!"=="!LINE!" pause
exit /b %RC%
```

About the pause check:
- It removes the quotes from `%CMDCMDLINE%` first, so a folder name with `&` or quotes can't break the comparison.
- `if defined LINE` skips the pause when the variable is empty. Wine's `cmd` leaves it empty.

Give it CRLF line ends:

```bash
python3 -c "import pathlib, sys; p = pathlib.Path(sys.argv[1]); p.write_bytes(p.read_bytes().replace(b'\r\n', b'\n').replace(b'\n', b'\r\n'))" client/windows/patch-client.bat
file client/windows/patch-client.bat
```

Expected: `client/windows/patch-client.bat: DOS batch file, ASCII text, with CRLF line terminators`

- [ ] **Step 6: Run the tests to verify they pass**

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -v` (with `pwsh`)

Expected:
```
test_a_backup_is_made_once_and_a_wrong_one_is_replaced (tests.test_appliers.PowerShellApplierTests.test_a_backup_is_made_once_and_a_wrong_one_is_replaced) ... ok
test_a_folder_that_cannot_be_written_exits_1_and_nothing_changes (tests.test_appliers.PowerShellApplierTests.test_a_folder_that_cannot_be_written_exits_1_and_nothing_changes) ... ok
...
test_defaults_to_its_own_folder_and_the_patches_folder_next_to_it (tests.test_appliers.PowerShellApplierTests.test_defaults_to_its_own_folder_and_the_patches_folder_next_to_it) ... ok
test_files_changed_since_patching_are_not_restored_with_exit_3_and_nothing_changed (tests.test_appliers.PowerShellApplierTests.test_files_changed_since_patching_are_not_restored_with_exit_3_and_nothing_changed) ... ok
test_invalid_patch_sets_exit_2_and_change_nothing (tests.test_appliers.PowerShellApplierTests.test_invalid_patch_sets_exit_2_and_change_nothing) ... ok
test_paths_with_spaces_and_parentheses (tests.test_appliers.PowerShellApplierTests.test_paths_with_spaces_and_parentheses) ... ok
...
test_restore_keeps_the_backup_of_a_file_that_already_is_the_original (tests.test_appliers.PowerShellApplierTests.test_restore_keeps_the_backup_of_a_file_that_already_is_the_original) ... ok
test_the_bat_command_line_in_a_folder_with_spaces_and_parentheses (tests.test_appliers.PowerShellApplierTests.test_the_bat_command_line_in_a_folder_with_spaces_and_parentheses) ... ok
...
test_unknown_and_missing_files_are_refused_with_exit_3_and_nothing_changed (tests.test_appliers.PythonApplierTests.test_unknown_and_missing_files_are_refused_with_exit_3_and_nothing_changed) ... ok
test_bat_runs_the_script_and_returns_its_exit_code (tests.test_appliers.WindowsFilesTests.test_bat_runs_the_script_and_returns_its_exit_code) ... ok
test_ps1_avoids_powershell_7_only_syntax (tests.test_appliers.WindowsFilesTests.test_ps1_avoids_powershell_7_only_syntax) ... ok
test_windows_files_are_ascii_with_crlf_line_ends (tests.test_appliers.WindowsFilesTests.test_windows_files_are_ascii_with_crlf_line_ends) ... ok
...
----------------------------------------------------------------------
Ran 69 tests in 59.357s

OK
```

Run `env -u HDC_PWSH python3 -m unittest discover -s client/patches/tests -t client/patches` on a machine without `pwsh`. Expected: `Ran 69 tests in 3.426s` and `OK (skipped=17)`. (The `test_a_folder_that_cannot_be_written...` case is also skipped when run as root.)

Then check the usage paths by hand. `classic-creation.json` doesn't exist until Task 3, so the default patch set can't be read yet:

Run: `PS="${HDC_PWSH:-pwsh} -NoProfile -NonInteractive -ExecutionPolicy Bypass -File client/windows/patch-client.ps1"; $PS -Restore -Check; echo "rc=$?"; $PS -Client; echo "rc=$?"; $PS; echo "rc=$?"`

Expected:
```
Usage: patch-client.ps1 [-Client DIR] [-PatchSet FILE] [-Bundle DIR] [-Restore | -Check]
patch-client.ps1: error: -Restore and -Check cannot be used together
rc=2
Usage: patch-client.ps1 [-Client DIR] [-PatchSet FILE] [-Bundle DIR] [-Restore | -Check]
patch-client.ps1: error: -Client needs a value
rc=2
Error: invalid patch set: cannot read patch set <repo>/client/windows/patches/classic-creation.json: Could not find a part of the path '<repo>/client/windows/patches/classic-creation.json'.
rc=2
```

Last, if Wine is installed, check that `patch-client.bat` passes a folder with spaces and parentheses on as one argument each. The tests can't run the `.bat`: they run the script the way it does. Under Wine, `cmd` runs the `.bat`, and a small Python stand-in named `powershell.exe` prints the arguments it gets (Wine starts a Unix program like a Windows one). The first run is a double-click (no options), the second passes `-Client` with the folder. This is a local check, not part of CI. It makes a throwaway Wine prefix (about 1.3 GB) in the temp folder and deletes it. The stand-in's exit code doesn't come back through Wine, so this checks only the arguments; the static test checks that the `.bat` returns the script's exit code.

Run:
```bash
T=$(mktemp -d); APP="$T/Program Files (x86)/Offline DAoC v0.34/runtime/client-opendaoc/app"
mkdir -p "$APP" && cp client/windows/patch-client.bat "$APP/"
printf '#!/usr/bin/env python3\nimport sys\nprint(" ".join("[%%s]" %% a for a in sys.argv[1:]))\n' > "$T/powershell.exe" && chmod +x "$T/powershell.exe"
win() { printf 'Z:%s' "$1" | tr / '\\'; }
printf '@call "%s\\patch-client.bat"\r\n' "$(win "$APP")" > "$T/double-click.cmd"
printf '@call "%s\\patch-client.bat" -Client "%s" -Check\r\n' "$(win "$APP")" "$(win "$APP")" > "$T/with-client.cmd"
for run in double-click with-client; do
  (cd "$T" && WINEPREFIX="$T/wine" WINEDEBUG=-all WINEDLLOVERRIDES="mscoree,mshtml=" wine cmd /c "$(win "$T/$run.cmd")" 2>/dev/null </dev/null)
done
rm -rf "$T"
```

Expected (each path arrives whole, quotes removed):
```
[-NoProfile] [-ExecutionPolicy] [Bypass] [-File] [Z:\tmp\tmp.XXXXXXXXXX\Program Files (x86)\Offline DAoC v0.34\runtime\client-opendaoc\app\patch-client.ps1]
[-NoProfile] [-ExecutionPolicy] [Bypass] [-File] [Z:\tmp\tmp.XXXXXXXXXX\Program Files (x86)\Offline DAoC v0.34\runtime\client-opendaoc\app\patch-client.ps1] [-Client] [Z:\tmp\tmp.XXXXXXXXXX\Program Files (x86)\Offline DAoC v0.34\runtime\client-opendaoc\app] [-Check]
```

- [ ] **Step 7: Check Windows PowerShell 5.1 compatibility**

The tests run PowerShell 7, but players run 5.1. PSScriptAnalyzer's compatibility rules check the script against the 5.1 profile it ships: syntax, commands, parameters and types. This is a local check: it needs `pwsh` and network access, and isn't part of CI. Run it after every edit of the script.

Run:
```bash
T=$(mktemp -d)
curl -sSL -o "$T/psa.nupkg" https://www.powershellgallery.com/api/v2/package/PSScriptAnalyzer/1.25.0
echo "14e634c828eb98efb9f40b2918ba90f139ed5eccdf663a2a747736d996995d60  $T/psa.nupkg" | LC_ALL=C sha256sum -c -
mkdir "$T/PSScriptAnalyzer" && python3 -I -c "import sys, zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])" "$T/psa.nupkg" "$T/PSScriptAnalyzer"
"${HDC_PWSH:-pwsh}" -NoProfile -NonInteractive -Command "Import-Module '$T/PSScriptAnalyzer/PSScriptAnalyzer.psd1'; \$p = 'win-48_x64_10.0.17763.0_5.1.17763.316_x64_4.0.30319.42000_framework'; \$s = @{ IncludeRules = @('PSUseCompatibleSyntax', 'PSUseCompatibleCommands', 'PSUseCompatibleTypes'); Rules = @{ PSUseCompatibleSyntax = @{ Enable = \$true; TargetVersions = @('5.1') }; PSUseCompatibleCommands = @{ Enable = \$true; TargetProfiles = @(\$p) }; PSUseCompatibleTypes = @{ Enable = \$true; TargetProfiles = @(\$p) } } }; \$r = @(Invoke-ScriptAnalyzer -Path client/windows/patch-client.ps1 -Settings \$s); \$r | Format-Table -AutoSize RuleName, Line, Message | Out-String -Width 200; 'findings: ' + \$r.Count"
rm -rf "$T"
```

Expected:
```
/tmp/tmp.XXXXXXXXXX/psa.nupkg: OK

findings: 0
```

These rules do find 5.1 problems. On a probe script, they reported `??`, the ternary operator and `ConvertFrom-Json -AsHashtable`.

- [ ] **Step 8: Commit**

```bash
git add client/windows/patch-client.ps1 client/windows/patch-client.bat client/patches/tests/test_appliers.py
git commit -m "feat(client): Windows applier for client patches, tested with the Linux one

patch-client.ps1 applies, checks and restores a patch set with the same
rules, messages and exit codes as apply_patches.py, in Windows PowerShell
5.1 (it defaults to its own folder and patches\\classic-creation.json).
patch-client.bat runs it with -ExecutionPolicy Bypass, passes the options
on, keeps the window open when double-clicked and returns its exit code.
test_appliers.py runs the same command-line cases against both appliers;
the PowerShell run needs pwsh (or HDC_PWSH) and is skipped without it.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git ls-files --eol client/windows/
```

Expected: both new files are stored with CRLF, like the existing launcher:
```
i/crlf  w/crlf  attr/                 	client/windows/connect-hearthdaoc.bat
i/crlf  w/crlf  attr/                 	client/windows/patch-client.bat
i/crlf  w/crlf  attr/                 	client/windows/patch-client.ps1
```

---

### Task 3: PE helpers, classic stat flow, Optimize removal and the patch-set generator

This task adds the first real patch data. `game.dll` gets the three stat-flow patches P1, P2 and P3 (spec section 3), and `pregame/character_customize_stats.xml` loses its Optimize button. A generator, `build.py`, reads the player's own client files and writes `client/patches/classic-creation.json`: hashes plus byte and text edits, never an EA file. Tasks 5 and 6 extend the same generator with the code cave and the splash.

Background for the engineer:
- **Addresses.** In this `game.dll`, `.text` starts at VA `0x401000` and file offset `0x1000`, so a code VA minus `0x400000` is its file offset (P1 `0x59C0B2` is offset `0x19C0B2`). The code still converts through `PE.offset()` and never hard-codes that rule. `Section.va` and every `va` in `pe.py` is an **absolute** VA (image base included), as in a disassembly.
- **Checksum.** The stock file's PE checksum (`0x5B1213`, at file offset `0x1B0`) is valid. `patch_game_dll` keeps it valid by recomputing it last, as upstream's `source/server/tools/patch_bot_map_client.py` does. So the `game.dll` entry has four replace ops: the checksum, P2, P1 and P3, in offset order.
- **XML.** The pregame XML uses CRLF line ends and tabs. The `find` text is the whole Optimize `ButtonDef` (lines 676–691 of the stock file), CRLF included. Reset (ControlId 1020, directly above it) stays.
- **Real-file tests** are skipped unless the environment points at real files:
  - `HDC_CLIENT_FILES`: an OfflineDAoC 0.34 classic client folder, for example `~/Games/HearthDAoC/client`.
  - `HDC_TEST_WORLD`: a clean classic world database, for example `~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db`. This is the repo's existing variable: `deploy/tests` uses it and CI already sets it. Only `test_build.py` needs it.
  - These files are only read; nothing in this task writes to a client folder.
- **Extension points.**
  - `patch_game_dll(original, cave=None)`: the line `if cave is not None: raise ValueError(...)` sits exactly where Task 5 appends the `.hdcc` section and writes the hook. That is after the stat-flow loop and before the final checksum.
  - `build_patchset(..., splash_mpk=None)`: the `if splash_mpk is not None: raise ValueError(...)` guard is where Task 6 adds the splash entry and the `--splash-mpk` flag.
  - `world_db` and `server_src` are accepted now, so the CLI stays stable. Task 5 uses them for the base-class data.

**Files:**
- Create: `client/patches/pe.py`
- Create: `client/patches/build.py`
- Create: `client/patches/classic-creation.json` (generated by `build.py` in Step 10; committed)
- Test: `client/patches/tests/test_pe.py` (create)
- Test: `client/patches/tests/test_build.py` (create)
- Uses, unchanged, from Task 1: `client/patches/patchset.py`, `client/patches/apply_patches.py`, `client/patches/tests/__init__.py`

**Interfaces:**
- Consumes (Task 1):
  - `patchset.transform(data: bytes, ops: list, bundle_dir: str) -> bytes`, in `test_build.py`.
  - The `apply_patches.py --client DIR [--check]` CLI, in Step 11 only.
- Produces, `client/patches/pe.py`:
  - `Section = namedtuple("Section", "name va vsize raw_offset raw_size characteristics")`. `va` is absolute.
  - `class PE(data: bytes)` with:
    - `.image_base`, `.sections` (`list[Section]`), `.file_alignment`, `.section_alignment`, `.size_of_image`, `.size_of_headers`
    - `.header_offsets`, a dict of file offsets with the keys `'num_sections'`, `'size_of_code'`, `'size_of_image'`, `'checksum'` and `'section_table'`
    - `.offset(va: int) -> int`, which raises `ValueError` when no file byte holds that VA
    - The constructor raises `ValueError` for anything that isn't a PE32 file.
  - `checksum(data: bytes, checksum_offset: int) -> int`
  - `append_section(data: bytes, name: str, payload: bytes, characteristics: int = 0x60000020) -> tuple[bytes, int]`. It returns the new bytes and the new section's absolute VA. It updates the section table, `NumberOfSections`, `SizeOfImage`, `SizeOfCode` (code sections only) and the checksum.
  - `diff_ops(old: bytes, new: bytes) -> list[dict]`
  - `align(value: int, alignment: int) -> int`
- Produces, `client/patches/build.py`:
  - `GAME_DLL_SHA256`
  - `STAT_FLOW: list[tuple[int, str, str]]`, as (VA, from hex, to hex)
  - `XML_EDITS: dict[str, list[tuple[str, str]]]`
  - `patch_game_dll(original: bytes, cave: bytes | None = None) -> bytes`
  - `edit_text(data: bytes, edits: list[tuple[str, str]]) -> bytes`
  - `build_patchset(client_dir: str, world_db: str, server_src: str, splash_mpk: str | None = None) -> dict`
  - `to_json(patchset: dict) -> str`
  - `main(argv=None) -> int`
  - CLI: `build.py --client DIR --world-db FILE --server-src DIR --out FILE`. It exits 1 with `build.py: <reason>` on an unknown or missing client file.

- [ ] **Step 1: Write the failing PE tests**

Create `client/patches/tests/test_pe.py`:

```python
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
```

- [ ] **Step 2: Run the PE tests to verify they fail**

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -p 'test_pe.py'`

Expected (shortened):
```
ERROR: tests.test_pe (unittest.loader._FailedTest.tests.test_pe)
ImportError: Failed to import test module: tests.test_pe
...
    import pe  # noqa: E402
ModuleNotFoundError: No module named 'pe'

Ran 1 test in 0.000s

FAILED (errors=1)
```

- [ ] **Step 3: Write `pe.py`**

Create `client/patches/pe.py`:

```python
"""Small PE32 helpers for HearthDAoC's client patches (Python standard library only).

Reads the headers HearthDAoC needs, computes the PE checksum, appends a section and turns
two versions of a file into patch-set operations. All addresses called `va` are absolute
virtual addresses (image base included), as in a disassembly of game.dll.
"""
import array
import struct
import sys
from collections import namedtuple

# va: absolute virtual address of the section (image base + the header's VirtualAddress).
Section = namedtuple("Section", "name va vsize raw_offset raw_size characteristics")

IMAGE_SCN_CNT_CODE = 0x20
SECTION_HEADER_SIZE = 40
MERGE_GAP = 16
BLOCK = 4096


def align(value, alignment):
    return (value + alignment - 1) // alignment * alignment


class PE:
    """The headers of a 32-bit PE file (PE32), read from its bytes."""

    def __init__(self, data: bytes):
        if len(data) < 0x40 or data[:2] != b"MZ":
            raise ValueError("not a PE file: no MZ header")
        e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
        if data[e_lfanew:e_lfanew + 4] != b"PE\0\0":
            raise ValueError("not a PE file: no PE signature")
        count, _, _, _, optional_size = struct.unpack_from("<HIIIH", data, e_lfanew + 6)
        opt = e_lfanew + 24
        if struct.unpack_from("<H", data, opt)[0] != 0x10B:
            raise ValueError("not a 32-bit PE (PE32) file")
        self.image_base = struct.unpack_from("<I", data, opt + 28)[0]
        self.section_alignment, self.file_alignment = struct.unpack_from("<II", data, opt + 32)
        self.size_of_image, self.size_of_headers = struct.unpack_from("<II", data, opt + 56)
        table = opt + optional_size
        self.header_offsets = {
            "num_sections": e_lfanew + 6,
            "size_of_code": opt + 4,
            "size_of_image": opt + 56,
            "checksum": opt + 64,
            "section_table": table,
        }
        self.sections = []
        for i in range(count):
            entry = table + SECTION_HEADER_SIZE * i
            name, vsize, rva, raw_size, raw_offset = struct.unpack_from("<8sIIII", data, entry)
            characteristics = struct.unpack_from("<I", data, entry + 36)[0]
            self.sections.append(Section(name.rstrip(b"\0").decode("latin-1"), self.image_base + rva,
                                         vsize, raw_offset, raw_size, characteristics))

    def offset(self, va: int) -> int:
        """File offset of the byte at virtual address `va`; ValueError if no file byte holds it."""
        for s in self.sections:
            if s.va <= va < s.va + s.raw_size:
                return s.raw_offset + va - s.va
        raise ValueError(f"VA {va:#x} is not stored in the file")


def checksum(data: bytes, checksum_offset: int) -> int:
    """The PE image checksum, with the 4 bytes at `checksum_offset` counted as zero."""
    buf = bytes(data[:checksum_offset]) + bytes(4) + bytes(data[checksum_offset + 4:])
    if len(buf) % 2:
        buf += b"\0"
    words = array.array("H")
    words.frombytes(buf)
    if sys.byteorder == "big":
        words.byteswap()
    total = sum(words)
    while total > 0xFFFF:
        total = (total & 0xFFFF) + (total >> 16)
    return (total + len(data)) & 0xFFFFFFFF


def append_section(data: bytes, name: str, payload: bytes,
                   characteristics: int = 0x60000020) -> tuple[bytes, int]:
    """Append `payload` as a new last section. Updates the section table, NumberOfSections,
    SizeOfImage, SizeOfCode (for code sections) and the checksum. Returns (new data, section VA)."""
    encoded = name.encode("ascii")
    if not 0 < len(encoded) <= 8:
        raise ValueError(f"section name {name!r} must be 1 to 8 ASCII characters")
    if not payload:
        raise ValueError("the new section needs a payload")
    pe = PE(data)
    if any(s.name == name for s in pe.sections):
        raise ValueError(f"the file already has a {name} section")
    offsets = pe.header_offsets
    entry = offsets["section_table"] + SECTION_HEADER_SIZE * len(pe.sections)
    first_raw = min([s.raw_offset for s in pe.sections if s.raw_size] + [pe.size_of_headers])
    if entry + SECTION_HEADER_SIZE > first_raw:
        raise ValueError("no room in the PE header for another section")
    if any(data[entry:entry + SECTION_HEADER_SIZE]):
        raise ValueError("the PE header slack after the section table is not empty")
    end = max(s.va + max(s.vsize, s.raw_size) for s in pe.sections) - pe.image_base
    rva = align(end, pe.section_alignment)
    raw = align(len(data), pe.file_alignment)
    raw_size = align(len(payload), pe.file_alignment)
    out = bytearray(data)
    out += bytes(raw - len(out))
    out += payload + bytes(raw_size - len(payload))
    struct.pack_into("<8sIIIIIIHHI", out, entry, encoded.ljust(8, b"\0"), len(payload), rva,
                     raw_size, raw, 0, 0, 0, 0, characteristics)
    struct.pack_into("<H", out, offsets["num_sections"], len(pe.sections) + 1)
    if characteristics & IMAGE_SCN_CNT_CODE:
        size_of_code = struct.unpack_from("<I", out, offsets["size_of_code"])[0]
        struct.pack_into("<I", out, offsets["size_of_code"], size_of_code + raw_size)
    struct.pack_into("<I", out, offsets["size_of_image"], align(rva + len(payload), pe.section_alignment))
    struct.pack_into("<I", out, offsets["checksum"], checksum(out, offsets["checksum"]))
    return bytes(out), pe.image_base + rva


def diff_ops(old: bytes, new: bytes) -> list[dict]:
    """Patch-set operations that turn `old` into `new`: one replace op per changed run inside
    `old` (runs less than 16 unchanged bytes apart are merged), then one append op for any tail."""
    if len(new) < len(old):
        raise ValueError("diff_ops cannot shrink a file")
    changed = []
    for start in range(0, len(old), BLOCK):
        a, b = old[start:start + BLOCK], new[start:start + BLOCK]
        if a != b:
            changed.extend(start + i for i in range(len(a)) if a[i] != b[i])
    runs = []
    for i in changed:
        if runs and i - runs[-1][1] < MERGE_GAP:
            runs[-1][1] = i + 1
        else:
            runs.append([i, i + 1])
    ops = [{"op": "replace", "offset": s, "from": old[s:e].hex(), "to": new[s:e].hex()} for s, e in runs]
    if len(new) > len(old):
        ops.append({"op": "append", "data": new[len(old):].hex()})
    return ops
```

- [ ] **Step 4: Run the PE tests to verify they pass**

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -p 'test_pe.py'`

Expected:
```
...................s
----------------------------------------------------------------------
Ran 20 tests in 0.014s

OK (skipped=1)
```

Run with the real client (the real-file test reads `game.dll`, checks the stock checksum `0x5B1213` and maps VA `0x59C0B2` to offset `0x19C0B2`): `HDC_CLIENT_FILES=~/Games/HearthDAoC/client python3 -m unittest discover -s client/patches/tests -t client/patches -p 'test_pe.py'`

Expected:
```
....................
----------------------------------------------------------------------
Ran 20 tests in 0.113s

OK
```

- [ ] **Step 5: Commit**

```bash
git add client/patches/pe.py client/patches/tests/test_pe.py
git commit -m "feat(client): PE helpers for the client patch set

Read PE32 headers, compute the PE checksum, append a section and turn
two file versions into patch-set replace/append operations.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 6: Write the failing generator tests**

First, append this class at the end of `client/patches/tests/test_pe.py`, with two blank lines before it. It checks the patch sites against the real client: the STAT_FLOW "from" bytes are in the real `game.dll`, the Optimize ButtonDef occurs exactly once, and the edited XML still parses.

```python
@unittest.skipUnless(CLIENT, NEEDS_CLIENT)
class RealPatchSiteTests(unittest.TestCase):
    def read(self, path):
        with open(os.path.join(CLIENT, *path.split("/")), "rb") as f:
            return f.read()

    def test_stat_flow_bytes_match_the_real_game_dll(self):
        from build import STAT_FLOW
        data = self.read("game.dll")
        p = pe.PE(data)
        for va, before, after in STAT_FLOW:
            start = p.offset(va)
            self.assertEqual(data[start:start + len(before) // 2].hex(), before, hex(va))

    def test_the_optimize_button_occurs_once_and_the_edited_xml_parses(self):
        from xml.etree import ElementTree
        from build import XML_EDITS, edit_text
        path = "pregame/character_customize_stats.xml"
        data = self.read(path)
        [(find, replace)] = XML_EDITS[path]
        self.assertEqual(data.decode("latin-1").count(find), 1)
        root = ElementTree.fromstring(edit_text(data, XML_EDITS[path]))
        control_ids = [e.text for e in root.iter("ControlId")]
        self.assertIn("1020", control_ids)
        self.assertNotIn("1021", control_ids)
        self.assertNotIn("Optimize", [e.text for e in root.iter("Label")])
```

Then create `client/patches/tests/test_build.py`:

```python
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
SERVER_SRC = os.path.join(REPO, "source", "server", "GameServer")
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

    def test_game_dll_changes_only_the_checksum_and_the_stat_flow(self):
        ops = self.patchset["files"][0]["ops"]
        self.assertEqual([(op["op"], op["offset"]) for op in ops],
                         [("replace", 0x1B0), ("replace", 0x19A853), ("replace", 0x19C0B2), ("replace", 0x19C574)])
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
```

- [ ] **Step 7: Run the generator tests to verify they fail**

Run: `HDC_CLIENT_FILES=~/Games/HearthDAoC/client HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -m unittest discover -s client/patches/tests -t client/patches -p 'test_pe.py'`

Expected (shortened):
```
ERROR: test_stat_flow_bytes_match_the_real_game_dll (tests.test_pe.RealPatchSiteTests.test_stat_flow_bytes_match_the_real_game_dll)
ModuleNotFoundError: No module named 'build'
ERROR: test_the_optimize_button_occurs_once_and_the_edited_xml_parses (tests.test_pe.RealPatchSiteTests.test_the_optimize_button_occurs_once_and_the_edited_xml_parses)
ModuleNotFoundError: No module named 'build'
Ran 22 tests in 0.088s
FAILED (errors=2)
```

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -p 'test_build.py'`

Expected (shortened):
```
ERROR: tests.test_build (unittest.loader._FailedTest.tests.test_build)
ImportError: Failed to import test module: tests.test_build
ModuleNotFoundError: No module named 'build'
Ran 1 test in 0.000s
FAILED (errors=1)
```

- [ ] **Step 8: Write `build.py`**

Create `client/patches/build.py` and make it executable (`chmod +x client/patches/build.py`):

```python
#!/usr/bin/env python3
"""Generate HearthDAoC's classic character creation patch set (classic-creation.json).

Reads an OfflineDAoC 0.34 classic client's own files, applies HearthDAoC's edits in memory and
writes only patch data: SHA-256 hashes and byte and text edits. No EA file is ever copied into
the patch set. Running it twice on the same inputs gives byte-identical output.

  python3 client/patches/build.py --client ~/Games/HearthDAoC/client \\
      --world-db clean-classic-0.34.db --server-src source/server/GameServer \\
      --out client/patches/classic-creation.json
"""
import argparse
import hashlib
import json
import os
import struct
import sys

import pe

FORMAT = 1
NAME = "classic-creation"
CLIENT = "OfflineDAoC 0.34 classic"
GAME_DLL = "game.dll"
GAME_DLL_SHA256 = "67dcf68a37b95a93946a943b99d5e19b4a03e08cd6469275e25c7b909de21e99"
CUSTOMIZE_STATS = "pregame/character_customize_stats.xml"

# Classic stat flow in game.dll: (VA, original bytes, new bytes), hex. From the 2026-10-06 investigation.
STAT_FLOW = [
    # P1: auto-assign (0x59C086) returns right after its reset: race base stats and 30 points to place.
    (0x59C0B2, "8b465c", "eb6890"),
    # P2: Continue on the customise screen checks unspent points for new characters too,
    # with the client's own "You must use all your points!" popup.
    (0x59A853, "0f859302000080bb28fa0000000f8478020000833dc8bb4502007437",
     "75f6833dc8bb450200751180bb28fa0000000f8473020000eb399090"),
    # P3: the attributes dialog starts visible.
    (0x59C574, "01", "00"),
]

# The attributes dialog's Optimize button (ControlId 1021), removed whole. The pregame XML uses CRLF.
OPTIMIZE_BUTTON = (
    "\t\t<ButtonDef>\r\n"
    "\t\t\t<TemplateName>button_small</TemplateName>\r\n"
    "\t\t\t<ControlId>1021</ControlId>\r\n"
    "\t\t\t<Label>Optimize</Label>\r\n"
    "\t\t\t<Alignment>\r\n"
    "\t\t\t\t<TopLeft>true</TopLeft>\r\n"
    "\t\t\t</Alignment>\r\n"
    "\t\t\t<Position>\r\n"
    "\t\t\t\t<X>364</X>\r\n"
    "\t\t\t\t<Y>224</Y>\r\n"
    "\t\t\t</Position>\r\n"
    "\t\t\t<LabelAlignment>\r\n"
    "\t\t\t\t<CenterVertically>true</CenterVertically>\r\n"
    "\t\t\t\t<CenterHorizontally>true</CenterHorizontally>\r\n"
    "\t\t\t</LabelAlignment>\r\n"
    "\t\t</ButtonDef>\r\n"
)
XML_EDITS = {CUSTOMIZE_STATS: [(OPTIMIZE_BUTTON, "")]}


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read_client_file(client_dir: str, path: str) -> bytes:
    with open(os.path.join(client_dir, *path.split("/")), "rb") as f:
        return f.read()


def patch_game_dll(original: bytes, cave: bytes | None = None) -> bytes:
    """The classic 0.34 game.dll with the stat-flow patches and a fresh PE checksum.
    `cave` is the slot for the base-class code cave; this version only accepts None."""
    if sha256(original) != GAME_DLL_SHA256:
        raise ValueError(f"{GAME_DLL} is not the OfflineDAoC 0.34 classic file (SHA-256 {GAME_DLL_SHA256})")
    image = pe.PE(original)
    data = bytearray(original)
    for va, before, after in STAT_FLOW:
        start, old, new = image.offset(va), bytes.fromhex(before), bytes.fromhex(after)
        if len(new) != len(old) or data[start:start + len(old)] != old:
            raise ValueError(f"{GAME_DLL}: unexpected bytes at VA {va:#x}")
        data[start:start + len(old)] = new
    if cave is not None:
        raise ValueError("this build.py cannot add the code cave yet")
    offset = image.header_offsets["checksum"]
    struct.pack_into("<I", data, offset, pe.checksum(data, offset))
    return bytes(data)


def edit_text(data: bytes, edits: list[tuple[str, str]]) -> bytes:
    """Apply (find, replace) pairs to `data` decoded as Latin-1; each find must occur exactly once."""
    text = data.decode("latin-1")
    for find, replace in edits:
        count = text.count(find)
        if count != 1:
            raise ValueError(f"expected the text once, found it {count} times: {find[:60]!r}")
        text = text.replace(find, replace)
    return text.encode("latin-1")


def file_entry(path: str, before: bytes, after: bytes, ops: list[dict]) -> dict:
    return {"path": path, "before": sha256(before), "after": sha256(after), "ops": ops}


def build_patchset(client_dir: str, world_db: str, server_src: str, splash_mpk: str | None = None) -> dict:
    """The classic-creation patch set for the client files in `client_dir`.
    `world_db` and `server_src` are the inputs of the base-class list and `splash_mpk` the slot
    for the splash entry; this version uses neither and only accepts splash_mpk=None."""
    if splash_mpk is not None:
        raise ValueError("this build.py cannot add the splash entry yet")
    original = read_client_file(client_dir, GAME_DLL)
    patched = patch_game_dll(original)
    files = [file_entry(GAME_DLL, original, patched, pe.diff_ops(original, patched))]
    for path, edits in XML_EDITS.items():
        data = read_client_file(client_dir, path)
        ops = [{"op": "text-replace", "find": find, "replace": replace} for find, replace in edits]
        files.append(file_entry(path, data, edit_text(data, edits), ops))
    return {"format": FORMAT, "name": NAME, "client": CLIENT, "files": files}


def to_json(patchset: dict) -> str:
    return json.dumps(patchset, indent=1, ensure_ascii=True, sort_keys=False) + "\n"


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Generate HearthDAoC's classic-creation patch set.")
    parser.add_argument("--client", required=True, help="OfflineDAoC 0.34 classic client folder (read only)")
    parser.add_argument("--world-db", required=True, help="the classic edition's clean world database (read only)")
    parser.add_argument("--server-src", required=True, help="the server sources, source/server/GameServer")
    parser.add_argument("--out", required=True, help="the patch set to write")
    args = parser.parse_args(argv)
    try:
        patchset = build_patchset(args.client, args.world_db, args.server_src)
    except (OSError, ValueError) as e:
        print(f"build.py: {e}", file=sys.stderr)
        return 1
    with open(args.out, "w", encoding="ascii", newline="\n") as f:
        f.write(to_json(patchset))
    print(f"Wrote {args.out}: {', '.join(entry['path'] for entry in patchset['files'])}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 9: Run the tests to verify they pass**

Run, without real files: `python3 -m unittest discover -s client/patches/tests -t client/patches -p 'test_pe.py'` and then `python3 -m unittest discover -s client/patches/tests -t client/patches -p 'test_build.py'`

Expected:
```
...................sss
----------------------------------------------------------------------
Ran 22 tests in 0.012s

OK (skipped=3)
.......ssssss
----------------------------------------------------------------------
Ran 13 tests in 0.000s

OK (skipped=6)
```

Run with real files: `HDC_CLIENT_FILES=~/Games/HearthDAoC/client HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -m unittest discover -s client/patches/tests -t client/patches -v`

Expected: all 35 tests of this task (22 in `test_pe`, 13 in `test_build`) report `ok`, none skipped, and the run ends with `OK` (`OK (skipped=17)` without pwsh: Task 2's PowerShell cases). The run also includes Task 1's `test_patchset` and Task 2's `test_appliers`. The `test_build` lines, for example:
```
test_keeps_latin1_bytes (EditTextTests) ... ok
test_refuses_a_missing_or_repeated_find (EditTextTests) ... ok
test_replaces_the_one_occurrence_and_keeps_crlf (EditTextTests) ... ok
test_json_layout (PatchDataTests) ... ok
test_refuses_a_game_dll_that_is_not_the_classic_034_file (PatchDataTests) ... ok
test_stat_flow_is_p1_p2_p3_with_same_length_edits (PatchDataTests) ... ok
test_the_xml_edit_removes_only_the_optimize_button (PatchDataTests) ... ok
test_cli_writes_the_same_json (RealBuildTests) ... ok
test_game_dll_before_hash_is_the_real_file (RealBuildTests) ... ok
test_game_dll_changes_only_the_checksum_and_the_stat_flow (RealBuildTests) ... ok
test_is_deterministic (RealBuildTests) ... ok
test_lists_game_dll_and_the_stats_xml (RealBuildTests) ... ok
test_transform_gives_the_after_hash (RealBuildTests) ... ok
```

- [ ] **Step 10: Generate `classic-creation.json` and check that it is reproducible**

Run:
```bash
python3 client/patches/build.py --client ~/Games/HearthDAoC/client --world-db ~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db --server-src source/server/GameServer --out client/patches/classic-creation.json
again="$(mktemp)" && python3 client/patches/build.py --client ~/Games/HearthDAoC/client --world-db ~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db --server-src source/server/GameServer --out "$again" && cmp client/patches/classic-creation.json "$again" && echo identical; rm -f "$again"
python3 -c 'import json; p = json.load(open("client/patches/classic-creation.json")); [print(f["path"], f["before"][:16], f["after"][:16], [(o["op"], o.get("offset")) for o in f["ops"]]) for f in p["files"]]'
```

Expected:
```
Wrote client/patches/classic-creation.json: game.dll, pregame/character_customize_stats.xml
Wrote /tmp/tmp.XXXXXXXXXX: game.dll, pregame/character_customize_stats.xml
identical
game.dll 67dcf68a37b95a93 cef4cbead1568256 [('replace', 432), ('replace', 1681491), ('replace', 1687730), ('replace', 1688948)]
pregame/character_customize_stats.xml 08e6f7c3b4fdb546 1d6be73e3e21f007 [('text-replace', None)]
```

The `game.dll` offsets are, in decimal: 432 = `0x1B0` (the PE checksum; only its low two bytes change, `1312` → `7abe`), 1681491 = `0x19A853` (P2), 1687730 = `0x19C0B2` (P1) and 1688948 = `0x19C574` (P3). The JSON holds only hashes, the changed bytes and the Optimize ButtonDef text being removed. Don't paste its content anywhere else.

- [ ] **Step 11: Apply the patch set to a scratch copy (never to the real client folder)**

Run:
```bash
scratch="$(mktemp -d)" && mkdir "$scratch/pregame" && cp ~/Games/HearthDAoC/client/game.dll "$scratch/" && cp ~/Games/HearthDAoC/client/pregame/character_customize_stats.xml "$scratch/pregame/"
python3 client/patches/apply_patches.py --client "$scratch"; echo "exit $?"
python3 client/patches/apply_patches.py --client "$scratch" --check; echo "exit $?"
rm -rf "$scratch"
```

Expected (the message wording comes from Task 1's applier; the exit codes and states are what matter):
```
Patched: game.dll (original saved as game.dll.hearthdaoc-orig)
Patched: pregame/character_customize_stats.xml (original saved as pregame/character_customize_stats.xml.hearthdaoc-orig)
exit 0
game.dll: patched
pregame/character_customize_stats.xml: patched
exit 0
```

- [ ] **Step 12: Commit**

```bash
git add client/patches/build.py client/patches/tests/test_build.py client/patches/tests/test_pe.py client/patches/classic-creation.json
git commit -m "feat(client): classic stat flow patch set and its generator

build.py reads the player's own 0.34 classic client and writes
classic-creation.json (hashes and edits only, no EA file): game.dll
P1-P3 (race base + 30 points to place, Continue needs all points,
attributes dialog open) with a fresh PE checksum, and the Optimize
button removed from character_customize_stats.xml.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Base-class data from the server sources

Derives the classic creation screen's base-class list (spec section 2 table) from the server's class files and the world's `disabled_classes`, and adds the hand-written descriptions and highlighted stats. Task 5's cave and data include consume `base_classes()`, `FINAL_CLASS_IDS` and `HIDE_RACES`.

Facts this task relies on (all checked against the real files on 2026-10-06):
- **Server class files.** Each `source/server/GameServer/playerclasses/**/Class*.cs` (63 files) has an attribute `[CharacterClass((int)eCharacterClass.Armsman, "Armsman", "Fighter", ...)]` (once spelled `[CharacterClassAttribute(...)]`, in ClassSavage.cs), a declaration `public class ClassArmsman : ClassFighter`, and one `EligibleRaces => new List<PlayerRace>() { PlayerRace.X, ... }` (once `=> new() { ... }`, in ClassBainshee.cs). Some race lists contain commented-out lines with more races, so comments are stripped before parsing.
- **Base class = the C# base type.** The server's `ScriptMgr.FindCharacterBaseClass` takes `charClass.GetType().BaseType` and requires that its attribute name equals the final class's base name (the attribute's third argument). The 15 base classes in `playerclasses/base/` derive from `CharacterClassBase`. Ids come from `GameServer/Enums/eCharacterClass.cs` and `GameServer/Enums/eRace.cs`. Realm and expansion per race come from `GameServer/gameobjects/PlayerRace.cs`: races 1-15 are Classic or ShroudedIsles, and 16-21 come later.
- **Sluaghbinder (63)** is a server full class deriving from `ClassAcolyte`, but the classic client never registers it (0.34b only; the classic world has `enable_sluaghbinder` = False). The 47 final classes the classic client registers (`FINAL_CLASS_IDS`, REPORT section 1.3) are exactly the server's full classes without 63.
- **World.** `ServerProperty` row `disabled_classes` in the clean classic world is `20;33;34;39;58-62`. HearthDAoC's `deploy/bin/world_fixes.py` (#63, PR #64) removes 20 (Disciple) at every start, so the generator treats 20 as enabled whatever the world says.
- **Client names.** The name string id and `.rdata` pointer of each base class come from the client's class-name table, the code at game.dll VA `0x44E769` (REPORT section 3). For example, `push 0x242; push 0x940C7C; call 0x7303FB; mov edx, 0x104B5A4` names class 14 "Fighter". The real-file test checks every pointer (the ASCII name is at that VA) and every name id against that code. For the VA-to-offset step it uses a small local helper, a stand-in for Task 3's `pe.PE(data).offset(va)`.
- **Stat ids** (client order, REPORT section 4.1): 0 STR, 1 CON, 2 DEX, 3 QUI, 4 INT, 5 PIE, 6 EMP, 7 CHA.
- **Descriptions** follow the spec's sample, including the article: `FLAVOR[id] + " At level 5 your trainer makes you " + ("an" | "a") + " " + "A, B or C" + "."`, for example "...makes you an Armsman, Mercenary, Paladin or Reaver."

**Files:**
- Create: `client/patches/src/base_classes.py`
- Create: `client/patches/classdata.py`
- Test: `client/patches/tests/test_classdata.py`

**Interfaces:**
- Consumes:
  - `client/patches/tests/__init__.py` (empty; created by Task 1), so that `unittest discover -t client/patches` imports the tests as package `tests`.
  - Server sources under `source/server/GameServer/` (`playerclasses/**/Class*.cs`, `Enums/eCharacterClass.cs`, `Enums/eRace.cs`, `gameobjects/PlayerRace.cs`).
  - Optional real files for the real-file tests: `HDC_CLIENT_FILES` (a classic client folder, as in Task 3; its `game.dll` must have SHA-256 `67dcf68a37b95a93946a943b99d5e19b4a03e08cd6469275e25c7b909de21e99`) and `HDC_TEST_WORLD` (a clean classic world DB, the existing `deploy/tests` convention). Both are only read.
- Produces (`client/patches/classdata.py`):
  - `@dataclass BaseClass(id: int, realm: int, name: str, name_id: int, name_ptr: int, finals: list[str], races: list[int], stats: list[int], description: str)`. `realm` is 1 Albion, 2 Midgard or 3 Hibernia; `finals` are the full-class names, sorted; `races` are race ids, sorted; `stats` are 3 client stat ids.
  - `parse_disabled(value: str) -> set[int]` splits on `;` and `,` and expands `a-b` ranges.
  - `read_disabled_classes(world_db) -> str` opens the DB read-only and returns `''` when the row is missing.
  - `base_classes(server_src: str, disabled: str) -> list[BaseClass]`. `server_src` is the folder that holds `GameServer/` (in this repo `source/server`). The list is sorted by id and applies the Disciple rule. It raises `ValueError` when an offered race isn't in the base class's own `EligibleRaces`, or when the sources can't be parsed.
  - `FINAL_CLASS_IDS` (47 ids, sorted; the cave's hide list), `HIDE_RACES = [16, 17, 18, 19, 20, 21]`.
  - Also `STAT_IDS`, `CLIENT_NAMES` (`{id: (name_id, name_ptr)}`), `FLAVOR` and `STATS` (re-exported from `src/base_classes.py`), `CLASSIC_EXPANSIONS`, `DISCIPLE`, `ServerClass`, `read_server_classes(server_src) -> dict[int, ServerClass]` and `read_player_races(server_src) -> dict[int, tuple[int, str]]`.
  - CLI: `python3 client/patches/classdata.py --server-src DIR (--world-db FILE | --disabled VALUE)` prints the table for review.
- Produces (`client/patches/src/base_classes.py`): `FLAVOR = {14: "...", ...}` (one ASCII sentence each) and `STATS = {14: ("STR", "CON", "DEX"), ...}`.

- [ ] **Step 1: Write the failing test**

Create `client/patches/tests/test_classdata.py`:

```python
"""Tests for classdata.py: the base classes of the classic creation screen.

The unit tests read this repo's server sources (source/server) and use the shipped classic world's
disabled_classes value. Real-file checks run only when the files are given:
- HDC_CLIENT_FILES: an OfflineDAoC 0.34 classic client folder (its game.dll is read, never changed);
- HDC_TEST_WORLD: a clean classic world database (opened read-only).
"""
import hashlib
import os
import shutil
import sqlite3
import struct
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
PATCHES = os.path.dirname(HERE)
REPO = os.path.abspath(os.path.join(PATCHES, "..", ".."))
SERVER_SRC = os.path.join(REPO, "source", "server")
sys.path.insert(0, PATCHES)
import classdata  # noqa: E402

SHIPPED_DISABLED = "20;33;34;39;58-62"  # clean classic 0.34 world, before world_fixes.py
CLIENT = os.environ.get("HDC_CLIENT_FILES")
WORLD = os.environ.get("HDC_TEST_WORLD")
GAME_DLL_SHA256 = "67dcf68a37b95a93946a943b99d5e19b4a03e08cd6469275e25c7b909de21e99"

BRI, AVA, HIG, SAR, NOR, TRO, DWA, KOB, CEL, FIR, ELF, LUR, INC, VAL, SYL = range(1, 16)
# docs/fork/specs/2026-10-06-classic-character-creation-design.md, section 2:
# id: (realm, name, becomes at level 5, races offered, highlighted stats)
SPEC = {
    14: (1, "Fighter", ["Armsman", "Mercenary", "Paladin", "Reaver"], [BRI, AVA, HIG, SAR, INC], ("STR", "CON", "DEX")),
    15: (1, "Elementalist", ["Theurgist", "Wizard"], [BRI, AVA], ("INT", "DEX", "QUI")),
    16: (1, "Acolyte", ["Cleric", "Friar"], [BRI, AVA, HIG], ("PIE", "CON", "DEX")),
    17: (1, "Rogue", ["Infiltrator", "Minstrel", "Scout"], [BRI, HIG, SAR, INC], ("DEX", "QUI", "STR")),
    18: (1, "Mage", ["Cabalist", "Sorcerer"], [BRI, AVA, SAR, INC], ("INT", "DEX", "QUI")),
    20: (1, "Disciple", ["Necromancer"], [BRI, SAR, INC], ("INT", "DEX", "QUI")),
    35: (2, "Viking", ["Berserker", "Savage", "Skald", "Thane", "Warrior"], [NOR, TRO, DWA, KOB, VAL],
         ("STR", "CON", "DEX")),
    36: (2, "Mystic", ["Bonedancer", "Runemaster", "Spiritmaster"], [NOR, TRO, DWA, KOB, VAL], ("PIE", "DEX", "QUI")),
    37: (2, "Seer", ["Healer", "Shaman"], [NOR, TRO, DWA, KOB], ("PIE", "CON", "DEX")),
    38: (2, "Rogue", ["Hunter", "Shadowblade"], [NOR, DWA, KOB, VAL], ("DEX", "QUI", "STR")),
    51: (3, "Magician", ["Eldritch", "Enchanter", "Mentalist"], [CEL, ELF, LUR], ("INT", "DEX", "QUI")),
    52: (3, "Guardian", ["Blademaster", "Champion", "Hero"], [CEL, FIR, ELF, LUR, SYL], ("STR", "CON", "DEX")),
    53: (3, "Naturalist", ["Bard", "Druid", "Warden"], [CEL, FIR, SYL], ("EMP", "DEX", "CON")),
    54: (3, "Stalker", ["Nightshade", "Ranger"], [CEL, ELF, LUR], ("DEX", "QUI", "STR")),
    57: (3, "Forester", ["Animist", "Valewalker"], [CEL, FIR, SYL], ("INT", "DEX", "CON")),
}
STAT_NAMES = {v: k for k, v in classdata.STAT_IDS.items()}


def copy_server(dst):
    """Copy just the server files classdata reads, so a test can edit them."""
    for rel in (("Enums", "eCharacterClass.cs"), ("Enums", "eRace.cs"), ("gameobjects", "PlayerRace.cs")):
        os.makedirs(os.path.join(dst, "GameServer", rel[0]), exist_ok=True)
        shutil.copy(os.path.join(SERVER_SRC, "GameServer", *rel), os.path.join(dst, "GameServer", *rel))
    shutil.copytree(os.path.join(SERVER_SRC, "GameServer", "playerclasses"),
                    os.path.join(dst, "GameServer", "playerclasses"))


def edit(path, old, new):
    with open(path, "rb") as f:
        data = f.read()
    assert data.count(old) == 1, (path, old)
    with open(path, "wb") as f:
        f.write(data.replace(old, new))


class ParseDisabledTests(unittest.TestCase):
    def test_shipped_value(self):
        self.assertEqual(classdata.parse_disabled(SHIPPED_DISABLED), {20, 33, 34, 39, 58, 59, 60, 61, 62})

    def test_empty_spaces_commas_reversed_ranges_and_junk(self):
        self.assertEqual(classdata.parse_disabled(""), set())
        self.assertEqual(classdata.parse_disabled(" 5 , 9-7;x;; 12 "), {5, 7, 8, 9, 12})


class ReadDisabledClassesTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "world.db")
        conn = sqlite3.connect(self.db)
        conn.execute("CREATE TABLE ServerProperty (`Key` TEXT PRIMARY KEY, Value TEXT)")
        conn.commit()
        conn.close()

    def tearDown(self):
        self.tmp.cleanup()

    def sha(self):
        with open(self.db, "rb") as f:
            return hashlib.sha256(f.read()).hexdigest()

    def test_reads_the_value_without_changing_the_file(self):
        conn = sqlite3.connect(self.db)
        conn.execute("INSERT INTO ServerProperty VALUES ('disabled_classes', ?)", (SHIPPED_DISABLED,))
        conn.commit()
        conn.close()
        before = self.sha()
        self.assertEqual(classdata.read_disabled_classes(self.db), SHIPPED_DISABLED)
        self.assertEqual(self.sha(), before)

    def test_missing_row_is_empty(self):
        self.assertEqual(classdata.read_disabled_classes(self.db), "")

    def test_missing_file_is_not_created(self):
        missing = os.path.join(self.tmp.name, "nope.db")
        with self.assertRaises(sqlite3.OperationalError):
            classdata.read_disabled_classes(missing)
        self.assertFalse(os.path.exists(missing))


class ServerSourceTests(unittest.TestCase):
    def test_base_class_is_the_csharp_base_type(self):
        classes = classdata.read_server_classes(SERVER_SRC)
        self.assertEqual((classes[2].type_name, classes[2].parent, classes[2].base_name),
                         ("ClassArmsman", "ClassFighter", "Fighter"))
        self.assertEqual(classes[12].parent, "ClassDisciple")
        self.assertEqual(classes[14].parent, "CharacterClassBase")
        # the commented-out list with Korazh and Half Ogre is ignored
        self.assertEqual(classes[2].races, [AVA, BRI, HIG, INC, SAR])

    def test_final_class_ids_are_the_servers_full_classes_but_sluaghbinder(self):
        classes = classdata.read_server_classes(SERVER_SRC)
        finals = {c.id for c in classes.values() if c.parent != "CharacterClassBase"}
        self.assertIn(63, finals)  # Sluaghbinder: 0.34b only, unknown to the classic client
        self.assertEqual(classdata.FINAL_CLASS_IDS, sorted(finals - {63}))
        self.assertEqual(len(classdata.FINAL_CLASS_IDS), 47)

    def test_hide_races_are_the_races_after_shrouded_isles(self):
        races = classdata.read_player_races(SERVER_SRC)
        later = sorted(r for r, (_, exp) in races.items() if exp not in classdata.CLASSIC_EXPANSIONS)
        self.assertEqual(classdata.HIDE_RACES, later)
        self.assertEqual(sorted(set(races) - set(later)), list(range(1, 16)))


class BaseClassTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bcs = classdata.base_classes(SERVER_SRC, SHIPPED_DISABLED)
        cls.server = classdata.read_server_classes(SERVER_SRC)

    def test_the_spec_table(self):
        got = {bc.id: (bc.realm, bc.name, bc.finals, bc.races, tuple(STAT_NAMES[s] for s in bc.stats))
               for bc in self.bcs}
        self.assertEqual(got, SPEC)
        self.assertEqual([bc.id for bc in self.bcs], sorted(SPEC))

    def test_no_dead_ends(self):
        off = classdata.parse_disabled(SHIPPED_DISABLED) - {20}
        for bc in self.bcs:
            parent = self.server[bc.id].type_name
            finals = [c for c in self.server.values() if c.parent == parent and c.name in bc.finals]
            self.assertEqual(len(finals), len(bc.finals), bc.name)
            self.assertTrue(finals, bc.name)
            self.assertFalse({c.id for c in finals} & off, bc.name)
            for race in bc.races:
                self.assertTrue(any(race in c.races for c in finals), (bc.name, race))

    def test_classic_races_the_server_accepts_for_the_base_class(self):
        for bc in self.bcs:
            self.assertTrue(set(bc.races) <= set(range(1, 16)), bc.name)
            self.assertTrue(set(bc.races) <= set(self.server[bc.id].races), bc.name)

    def test_three_stats_each(self):
        for bc in self.bcs:
            self.assertEqual(len(bc.stats), 3, bc.name)
            self.assertEqual(len(set(bc.stats)), 3, bc.name)
            self.assertTrue(set(bc.stats) <= set(range(8)), bc.name)

    def test_names_come_from_the_client_table(self):
        for bc in self.bcs:
            self.assertEqual((bc.name_id, bc.name_ptr), classdata.CLIENT_NAMES[bc.id])

    def test_descriptions(self):
        for bc in self.bcs:
            flavor = classdata.FLAVOR[bc.id]
            self.assertRegex(flavor, r"^[A-Z][^.!?]*\.$")  # one sentence
            article = "an" if bc.finals[0][0] in "AEIOU" else "a"
            finals = bc.finals[0] if len(bc.finals) == 1 else ", ".join(bc.finals[:-1]) + " or " + bc.finals[-1]
            self.assertEqual(bc.description, f"{flavor} At level 5 your trainer makes you {article} {finals}.")
            self.assertTrue(bc.description.isascii(), bc.name)
        fighter = next(bc for bc in self.bcs if bc.id == 14)
        self.assertEqual(fighter.description,
                         "Albion's soldiers, trained in heavy armour and every kind of weapon. "
                         "At level 5 your trainer makes you an Armsman, Mercenary, Paladin or Reaver.")
        self.assertTrue(next(bc for bc in self.bcs if bc.id == 20).description.endswith("makes you a Necromancer."))

    def test_text_data_covers_exactly_the_listed_classes(self):
        ids = {bc.id for bc in self.bcs}
        self.assertEqual(set(classdata.FLAVOR), ids)
        self.assertEqual(set(classdata.STATS), ids)
        self.assertEqual(set(classdata.CLIENT_NAMES), ids)


class DisabledClassesTests(unittest.TestCase):
    def ids(self, disabled):
        return [bc.id for bc in classdata.base_classes(SERVER_SRC, disabled)]

    def test_disciple_is_listed_although_the_world_disables_it(self):
        self.assertIn(20, self.ids("20"))

    def test_a_disabled_base_class_is_not_listed(self):
        self.assertNotIn(14, self.ids(SHIPPED_DISABLED + ";14"))

    def test_a_base_class_without_an_enabled_full_class_is_not_listed(self):
        self.assertNotIn(20, self.ids(SHIPPED_DISABLED + ";12"))

    def test_races_follow_the_enabled_full_classes(self):
        acolyte = next(bc for bc in classdata.base_classes(SERVER_SRC, SHIPPED_DISABLED + ";6") if bc.id == 16)
        self.assertEqual((acolyte.finals, acolyte.races), (["Friar"], [BRI]))


class ServerSourceErrorTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.src = self.tmp.name
        copy_server(self.src)
        self.classes = os.path.join(self.src, "GameServer", "playerclasses")

    def tearDown(self):
        self.tmp.cleanup()

    def test_a_race_the_base_class_refuses_is_an_error(self):
        edit(os.path.join(self.classes, "base", "ClassFighter.cs"), b" PlayerRace.Saracen,", b"")
        with self.assertRaisesRegex(ValueError, r"ClassFighter: races \[4\]"):
            classdata.base_classes(self.src, SHIPPED_DISABLED)

    def test_a_base_name_that_disagrees_with_the_csharp_base_type_is_an_error(self):
        edit(os.path.join(self.classes, "albion", "ClassArmsman.cs"), b'"Armsman", "Fighter"', b'"Armsman", "Viking"')
        with self.assertRaisesRegex(ValueError, "ClassArmsman derives from ClassFighter but names base class 'Viking'"):
            classdata.base_classes(self.src, SHIPPED_DISABLED)

    def test_an_unreadable_class_file_is_an_error(self):
        with open(os.path.join(self.classes, "base", "ClassBroken.cs"), "w") as f:
            f.write("public class ClassBroken : CharacterClassBase { }\n")
        with self.assertRaisesRegex(ValueError, "ClassBroken.cs"):
            classdata.read_server_classes(self.src)


def sections(data):
    """(name, rva, virtual size, raw offset, raw size) per PE section, and the image base."""
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    count, opt_size = struct.unpack_from("<H", data, pe + 6)[0], struct.unpack_from("<H", data, pe + 20)[0]
    image_base = struct.unpack_from("<I", data, pe + 24 + 28)[0]
    table = pe + 24 + opt_size
    out = []
    for i in range(count):
        name, vsize, rva, raw_size, raw = struct.unpack_from("<8sIIII", data, table + 40 * i)
        out.append((name.rstrip(b"\0").decode(), rva, vsize, raw, raw_size))
    return out, image_base


def va_to_offset(data, va):
    """File offset of a VA: a local stand-in for pe.PE(data).offset(va) (client/patches/pe.py, Task 3)."""
    secs, image_base = sections(data)
    for _, rva, vsize, raw, raw_size in secs:
        if rva <= va - image_base < rva + min(vsize, raw_size):
            return raw + va - image_base - rva
    raise ValueError(f"VA {va:#x} is not in the file")


@unittest.skipUnless(CLIENT, "needs HDC_CLIENT_FILES (an OfflineDAoC 0.34 classic client folder)")
class RealGameDllTests(unittest.TestCase):
    NAME_TABLE = (0x44E769, 0x44F800)  # code that fills the class-name table (0x104B400 + id * 0x1E)
    STRING_LOOKUP = 0x7303FB
    REGISTER_CLASS = 0x5B01A3

    @classmethod
    def setUpClass(cls):
        with open(os.path.join(CLIENT, "game.dll"), "rb") as f:
            cls.data = f.read()
        if hashlib.sha256(cls.data).hexdigest() != GAME_DLL_SHA256:
            raise AssertionError("HDC_CLIENT_FILES/game.dll is not the OfflineDAoC 0.34 classic game.dll")
        cls.bcs = classdata.base_classes(SERVER_SRC, SHIPPED_DISABLED)

    def test_each_name_pointer_holds_the_class_name(self):
        for bc in self.bcs:
            at = va_to_offset(self.data, bc.name_ptr)
            self.assertEqual(self.data[at:self.data.index(b"\0", at)], bc.name.encode("ascii"), bc.id)

    def test_name_ids_and_pointers_match_the_clients_class_name_table(self):
        start = va_to_offset(self.data, self.NAME_TABLE[0])
        code = self.data[start:va_to_offset(self.data, self.NAME_TABLE[1])]
        for bc in self.bcs:
            store = b"\xba" + struct.pack("<I", 0x104B400 + bc.id * 0x1E)  # mov edx, &table[id]
            self.assertEqual(code.count(store), 1, bc.id)
            call = code.index(store) - 5  # call STRING_LOOKUP(name pointer, name id)
            self.assertEqual(code[call], 0xE8, bc.id)
            rel = struct.unpack_from("<i", code, call + 1)[0]
            self.assertEqual(self.NAME_TABLE[0] + call + 5 + rel, self.STRING_LOOKUP, bc.id)
            before, push_id, ptr = code[:call], b"\x68" + struct.pack("<I", bc.name_id), struct.pack("<I", bc.name_ptr)
            self.assertTrue(before.endswith(push_id + b"\x68" + ptr)                # push id; push ptr
                            or before.endswith(push_id + b"\xbe" + ptr + b"\x56")    # push id; mov esi,ptr; push esi
                            or (before.endswith(push_id + b"\x56") and b"\xbe" + ptr in before), bc.id)

    def test_the_client_registers_47_final_classes(self):
        secs, image_base = sections(self.data)
        _, rva, _, raw, raw_size = next(s for s in secs if s[0] == ".text")
        calls, i = 0, self.data.find(b"\xe8", raw, raw + raw_size - 4)
        while i != -1:
            rel = struct.unpack_from("<i", self.data, i + 1)[0]
            if image_base + rva + (i - raw) + 5 + rel == self.REGISTER_CLASS:
                calls += 1
            i = self.data.find(b"\xe8", i + 1, raw + raw_size - 4)
        self.assertEqual(calls, len(classdata.FINAL_CLASS_IDS))


@unittest.skipUnless(WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class RealWorldTests(unittest.TestCase):
    def test_the_worlds_disabled_classes_give_the_spec_table(self):
        value = classdata.read_disabled_classes(WORLD)
        self.assertEqual(classdata.parse_disabled(value) - {20}, classdata.parse_disabled(SHIPPED_DISABLED) - {20})
        self.assertEqual([bc.id for bc in classdata.base_classes(SERVER_SRC, value)], sorted(SPEC))


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -p test_classdata.py -v`
Expected (the module doesn't exist yet):
```
    import classdata  # noqa: E402
ModuleNotFoundError: No module named 'classdata'
Ran 1 test in 0.000s
FAILED (errors=1)
```

- [ ] **Step 3: Write the hand-written text data**

Create `client/patches/src/base_classes.py`. These are the descriptions players read; the owner reviews them in the PR, and the Fighter sentence is the spec's own sample:

```python
"""Hand-written text for the classic creation screen's base classes (English only).

classdata.py adds the rest from the server's class files: which base classes are listed, the
full classes each leads to, and the races. Each description shown in game is
FLAVOR[id] + " At level 5 your trainer makes you a(n) <full classes>."
Keep every text plain ASCII and FLAVOR to one sentence.

STATS are the three highlighted (primary) stats per base class. With auto-assign gone they
only colour the stats on the attributes window; they assign nothing.
"""

FLAVOR = {
    # Albion
    14: "Albion's soldiers, trained in heavy armour and every kind of weapon.",
    15: "Albion's students of the elements, who learn to call down earth, ice, fire and air.",
    16: "Albion's faithful, who serve the Church with healing prayers and a sturdy staff.",
    17: "Albion's quick and quiet, who live by the hidden blade, the bow and the song.",
    18: "Albion's scholars of the arcane, who bend body, mind, matter and spirit to their will.",
    20: "Albion's servants of Arawn, lord of the underworld, who learn to command the dead.",
    # Midgard
    35: "Midgard's warriors, raised in the shield wall with axe, sword and hammer.",
    36: "Midgard's seekers of hidden lore, who call on runes, spirits and the bones of the dead.",
    37: "Midgard's faithful, blessed by the gods with healing and protective magic.",
    38: "Midgard's hunters and assassins, who strike from the shadows or from afar.",
    # Hibernia
    51: "Hibernia's spellcasters, schooled in the magic of light, mana and the mind.",
    52: "Hibernia's fighters, trained to hold the line with blade, hammer and shield.",
    53: "Hibernia's keepers of the land, who draw on nature's magic to heal and protect.",
    54: "Hibernia's hunters and assassins, who move unseen through forest and shadow.",
    57: "Hibernia's protectors of the deep forest, who draw power from living wood and growing things.",
}

STATS = {
    14: ("STR", "CON", "DEX"),
    15: ("INT", "DEX", "QUI"),
    16: ("PIE", "CON", "DEX"),
    17: ("DEX", "QUI", "STR"),
    18: ("INT", "DEX", "QUI"),
    20: ("INT", "DEX", "QUI"),
    35: ("STR", "CON", "DEX"),
    36: ("PIE", "DEX", "QUI"),
    37: ("PIE", "CON", "DEX"),
    38: ("DEX", "QUI", "STR"),
    51: ("INT", "DEX", "QUI"),
    52: ("STR", "CON", "DEX"),
    53: ("EMP", "DEX", "CON"),
    54: ("DEX", "QUI", "STR"),
    57: ("INT", "DEX", "CON"),
}
```

- [ ] **Step 4: Write minimal implementation**

Create `client/patches/classdata.py`:

```python
#!/usr/bin/env python3
"""Base-class data for the classic creation screen, derived from the server's class files.

Inputs:
- server_src: the server source folder that holds GameServer/ (in this repo: source/server).
  Read: GameServer/playerclasses/**/Class*.cs ([CharacterClass] attribute, C# base type,
  EligibleRaces), GameServer/Enums/eCharacterClass.cs, GameServer/Enums/eRace.cs and
  GameServer/gameobjects/PlayerRace.cs (realm and expansion of each race).
- disabled: the world's disabled_classes value (read_disabled_classes reads it from a world DB).
- src/base_classes.py: the hand-written FLAVOR sentences and highlighted STATS.

A full class's base class is its C# base type, as the server's ScriptMgr.FindCharacterBaseClass
resolves it (class ClassArmsman : ClassFighter); base classes derive from CharacterClassBase.
Standard library only.
"""
import argparse
import glob
import importlib.util
import os
import re
import sqlite3
import sys
from dataclasses import dataclass

HERE = os.path.dirname(os.path.abspath(__file__))

DISCIPLE = 20  # enabled by HearthDAoC's deploy/bin/world_fixes.py at every server start (#63, PR #64)
CLASSIC_EXPANSIONS = ("Classic", "ShroudedIsles")
HIDE_RACES = [16, 17, 18, 19, 20, 21]  # Half Ogre, Frostalf, Shar and the three Minotaurs
REALMS = {"Albion": 1, "Midgard": 2, "Hibernia": 3}
STAT_IDS = {"STR": 0, "CON": 1, "DEX": 2, "QUI": 3, "INT": 4, "PIE": 5, "EMP": 6, "CHA": 7}  # client stat order

# The 47 final classes that the classic client registers for its creation screen (16 Albion,
# 15 Midgard, 16 Hibernia; game.dll VA 0x5B0031). The cave hides all of them. They are the
# server's full classes except Sluaghbinder (63), which only the 0.34b edition knows.
FINAL_CLASS_IDS = [
    1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 19, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30,
    31, 32, 33, 34, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 55, 56, 58, 59, 60, 61, 62,
]

# Name string id and .rdata pointer of each base class's name, from the client's own class-name
# table (game.dll VA 0x44E769, OfflineDAoC 0.34 classic). The cave reuses them as they are.
CLIENT_NAMES = {
    14: (0x242, 0x940C7C),  # Fighter
    15: (0x243, 0x940C6C),  # Elementalist
    16: (0x244, 0x940C64),  # Acolyte
    17: (0x81D, 0x940C5C),  # Rogue (Albion)
    18: (0x246, 0x940C54),  # Mage
    20: (0x3D0, 0x940C40),  # Disciple
    35: (0x251, 0x940BAC),  # Viking
    36: (0x252, 0x940BA4),  # Mystic
    37: (0x253, 0x940B9C),  # Seer
    38: (0x245, 0x940C5C),  # Rogue (Midgard)
    51: (0x25F, 0x940B14),  # Magician
    52: (0x260, 0x940B08),  # Guardian
    53: (0x261, 0x940AFC),  # Naturalist
    54: (0x262, 0x940AF4),  # Stalker
    57: (0x42F, 0x940AD4),  # Forester
}


@dataclass
class ServerClass:
    id: int
    name: str         # [CharacterClass] name, e.g. "Armsman"
    base_name: str    # [CharacterClass] base name, e.g. "Fighter"
    type_name: str    # C# class, e.g. "ClassArmsman"
    parent: str       # C# base type, e.g. "ClassFighter" or "CharacterClassBase"
    races: list[int]  # EligibleRaces as race ids, in source order


@dataclass
class BaseClass:
    id: int
    realm: int
    name: str
    name_id: int
    name_ptr: int
    finals: list[str]
    races: list[int]
    stats: list[int]
    description: str


def _load_text():
    path = os.path.join(HERE, "src", "base_classes.py")
    spec = importlib.util.spec_from_file_location("hearthdaoc_base_classes", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


_TEXT = _load_text()
FLAVOR = _TEXT.FLAVOR
STATS = _TEXT.STATS


def parse_disabled(value: str) -> set[int]:
    """Class ids in a disabled_classes value such as '20;33;34;39;58-62'.

    Ranges are expanded as the property's description says (either order). Commas are accepted as
    well as semicolons, so nothing the owner may have typed is missed. Other tokens never match a
    class on the server either and are ignored.
    """
    ids = set()
    for token in re.split(r"[;,]", value or ""):
        token = token.strip()
        m = re.fullmatch(r"(\d+)\s*-\s*(\d+)", token)
        if m:
            lo, hi = sorted((int(m.group(1)), int(m.group(2))))
            ids.update(range(lo, hi + 1))
        elif token.isdigit():
            ids.add(int(token))
    return ids


def read_disabled_classes(world_db) -> str:
    """The world's disabled_classes server property ('' when the row is missing). Opens read-only."""
    conn = sqlite3.connect(f"file:{os.path.abspath(world_db)}?mode=ro", uri=True)
    try:
        row = conn.execute("SELECT Value FROM ServerProperty WHERE `Key`='disabled_classes'").fetchone()
    finally:
        conn.close()
    return row[0] if row else ""


def _read(path):
    with open(path, encoding="utf-8-sig") as f:
        text = f.read()
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def _enum(path, name):
    m = re.search(r"enum\s+" + name + r"\b[^{]*\{(.*?)\}", _read(path), re.S)
    if not m:
        raise ValueError(f"{path}: enum {name} not found")
    return {k: int(v) for k, v in re.findall(r"(\w+)\s*=\s*(\d+)", m.group(1))}


def read_player_races(server_src) -> dict[int, tuple[int, str]]:
    """Race id -> (realm id, expansion name), from GameServer/gameobjects/PlayerRace.cs."""
    gs = os.path.join(server_src, "GameServer")
    race_ids = _enum(os.path.join(gs, "Enums", "eRace.cs"), "eRace")
    found = re.findall(r"new\s+PlayerRace\(\s*eRace\.(\w+)\s*,\s*eRealm\.(\w+)\s*,\s*eDAoCExpansion\.(\w+)",
                       _read(os.path.join(gs, "gameobjects", "PlayerRace.cs")))
    if not found:
        raise ValueError("PlayerRace.cs: no races found")
    return {race_ids[race]: (REALMS[realm], expansion) for race, realm, expansion in found}


def read_server_classes(server_src) -> dict[int, ServerClass]:
    """Every player class in GameServer/playerclasses, by class id."""
    gs = os.path.join(server_src, "GameServer")
    class_ids = _enum(os.path.join(gs, "Enums", "eCharacterClass.cs"), "eCharacterClass")
    race_ids = _enum(os.path.join(gs, "Enums", "eRace.cs"), "eRace")
    files = sorted(glob.glob(os.path.join(gs, "playerclasses", "**", "Class*.cs"), recursive=True))
    if not files:
        raise ValueError(f"no Class*.cs under {os.path.join(gs, 'playerclasses')}")
    classes = {}
    for path in files:
        text = _read(path)
        attr = re.search(r'\[CharacterClass(?:Attribute)?\(\s*\(int\)\s*eCharacterClass\.(\w+)\s*,'
                         r'\s*"([^"]*)"\s*,\s*"([^"]*)"', text)
        decl = re.search(r"\bclass\s+(\w+)\s*:\s*(\w+)", text)
        races = re.search(r"EligibleRaces\s*=>\s*new\s*(?:List<PlayerRace>\s*)?\(\s*\)\s*\{(.*?)\}", text, re.S)
        if not (attr and decl and races):
            raise ValueError(f"{path}: [CharacterClass], class declaration or EligibleRaces not found")
        cls = ServerClass(id=class_ids[attr.group(1)], name=attr.group(2), base_name=attr.group(3),
                          type_name=decl.group(1), parent=decl.group(2),
                          races=[race_ids[r] for r in re.findall(r"PlayerRace\.(\w+)", races.group(1))])
        if cls.id in classes:
            raise ValueError(f"{path}: class id {cls.id} defined twice")
        classes[cls.id] = cls
    return classes


def _article(word):
    return "an" if word[0] in "AEIOU" else "a"


def _join(names):
    return names[0] if len(names) == 1 else ", ".join(names[:-1]) + " or " + names[-1]


def base_classes(server_src: str, disabled: str) -> list[BaseClass]:
    """The base classes the classic creation screen lists, sorted by id.

    A base class is listed when it isn't disabled and leads to at least one enabled full class the
    client knows (FINAL_CLASS_IDS). Disciple (20) counts as enabled whatever `disabled` says:
    world_fixes.py removes it at every server start. Its races are the union of those full classes'
    EligibleRaces, limited to the classic and Shrouded Isles races; they must also be in the base
    class's own EligibleRaces, which the server checks at creation (ValueError otherwise).
    """
    classes = read_server_classes(server_src)
    races = read_player_races(server_src)
    classic = {r for r, (_, expansion) in races.items() if expansion in CLASSIC_EXPANSIONS}
    off = parse_disabled(disabled) - {DISCIPLE}
    by_type = {c.type_name: c for c in classes.values()}
    out = []
    for base in sorted(classes.values(), key=lambda c: c.id):
        if base.parent != "CharacterClassBase" or base.id in off:
            continue
        finals = []
        for c in classes.values():
            if by_type.get(c.parent) is not base:
                continue
            if c.base_name != base.name:
                raise ValueError(f"{c.type_name} derives from {base.type_name} but names base class {c.base_name!r}")
            if c.id in FINAL_CLASS_IDS and c.id not in off:
                finals.append(c)
        if not finals:
            continue
        offered = sorted(set().union(*(c.races for c in finals)) & classic)
        missing = sorted(set(offered) - set(base.races))
        if missing:
            raise ValueError(f"{base.type_name}: races {missing} can become a full class but the server "
                             f"refuses them for the base class (not in its EligibleRaces)")
        realms = {races[r][0] for r in base.races}
        if len(realms) != 1:
            raise ValueError(f"{base.type_name}: EligibleRaces span realms {sorted(realms)}")
        if base.id not in FLAVOR or base.id not in STATS or base.id not in CLIENT_NAMES:
            raise ValueError(f"base class {base.id} ({base.name}) needs FLAVOR and STATS in src/base_classes.py "
                             f"and CLIENT_NAMES in classdata.py")
        names = sorted(c.name for c in finals)
        name_id, name_ptr = CLIENT_NAMES[base.id]
        out.append(BaseClass(
            id=base.id, realm=realms.pop(), name=base.name, name_id=name_id, name_ptr=name_ptr,
            finals=names, races=offered, stats=[STAT_IDS[s] for s in STATS[base.id]],
            description=f"{FLAVOR[base.id]} At level 5 your trainer makes you {_article(names[0])} {_join(names)}."))
    return out


def main(argv=None):
    ap = argparse.ArgumentParser(description="Print the base classes the classic creation screen lists.")
    ap.add_argument("--server-src", required=True, help="server source folder holding GameServer/ (source/server)")
    group = ap.add_mutually_exclusive_group(required=True)
    group.add_argument("--world-db", help="world database to read disabled_classes from (opened read-only)")
    group.add_argument("--disabled", help="a disabled_classes value, e.g. '20;33;34;39;58-62'")
    a = ap.parse_args(argv)
    disabled = a.disabled if a.disabled is not None else read_disabled_classes(a.world_db)
    names = {v: k for k, v in STAT_IDS.items()}
    for bc in base_classes(a.server_src, disabled):
        print(f"{bc.id:2} realm {bc.realm} {bc.name}: {', '.join(bc.finals)} | races {bc.races} | "
              f"{'/'.join(names[s] for s in bc.stats)}\n   {bc.description}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -p test_classdata.py -v`
Expected: the 22 unit tests pass, and the 4 real-file tests are skipped (`skipped 'needs HDC_CLIENT_FILES (an OfflineDAoC 0.34 classic client folder)'` / `skipped 'needs HDC_TEST_WORLD (a clean classic world database)'`):
```
Ran 26 tests in 0.093s
OK (skipped=4)
```

- [ ] **Step 6: Run the real-file checks**

These checks only read the files; nothing in the client or the world is changed. On the owner's machine:

Run: `HDC_CLIENT_FILES=$HOME/Games/HearthDAoC/client HDC_TEST_WORLD=$HOME/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -m unittest discover -s client/patches/tests -t client/patches -p test_classdata.py -v`
Expected:
```
test_each_name_pointer_holds_the_class_name (tests.test_classdata.RealGameDllTests.test_each_name_pointer_holds_the_class_name) ... ok
test_name_ids_and_pointers_match_the_clients_class_name_table (tests.test_classdata.RealGameDllTests.test_name_ids_and_pointers_match_the_clients_class_name_table) ... ok
test_the_client_registers_47_final_classes (tests.test_classdata.RealGameDllTests.test_the_client_registers_47_final_classes) ... ok
test_the_worlds_disabled_classes_give_the_spec_table (tests.test_classdata.RealWorldTests.test_the_worlds_disabled_classes_give_the_spec_table) ... ok
Ran 26 tests in 0.199s
OK
```
Elsewhere, point both variables at the copies CI fetches through `odaoc_fetch.py` (Task 8). If `game.dll` isn't the classic 0.34 build, the real-file class errors with "HDC_CLIENT_FILES/game.dll is not the OfflineDAoC 0.34 classic game.dll".

- [ ] **Step 7: Print the derived table and compare it with the spec**

Run: `python3 client/patches/classdata.py --server-src source/server --disabled '20;33;34;39;58-62'`
Expected (identical with `--world-db $HOME/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db`; it matches the spec section 2 table row for row):
```
14 realm 1 Fighter: Armsman, Mercenary, Paladin, Reaver | races [1, 2, 3, 4, 13] | STR/CON/DEX
   Albion's soldiers, trained in heavy armour and every kind of weapon. At level 5 your trainer makes you an Armsman, Mercenary, Paladin or Reaver.
15 realm 1 Elementalist: Theurgist, Wizard | races [1, 2] | INT/DEX/QUI
   Albion's students of the elements, who learn to call down earth, ice, fire and air. At level 5 your trainer makes you a Theurgist or Wizard.
16 realm 1 Acolyte: Cleric, Friar | races [1, 2, 3] | PIE/CON/DEX
   Albion's faithful, who serve the Church with healing prayers and a sturdy staff. At level 5 your trainer makes you a Cleric or Friar.
17 realm 1 Rogue: Infiltrator, Minstrel, Scout | races [1, 3, 4, 13] | DEX/QUI/STR
   Albion's quick and quiet, who live by the hidden blade, the bow and the song. At level 5 your trainer makes you an Infiltrator, Minstrel or Scout.
18 realm 1 Mage: Cabalist, Sorcerer | races [1, 2, 4, 13] | INT/DEX/QUI
   Albion's scholars of the arcane, who bend body, mind, matter and spirit to their will. At level 5 your trainer makes you a Cabalist or Sorcerer.
20 realm 1 Disciple: Necromancer | races [1, 4, 13] | INT/DEX/QUI
   Albion's servants of Arawn, lord of the underworld, who learn to command the dead. At level 5 your trainer makes you a Necromancer.
35 realm 2 Viking: Berserker, Savage, Skald, Thane, Warrior | races [5, 6, 7, 8, 14] | STR/CON/DEX
   Midgard's warriors, raised in the shield wall with axe, sword and hammer. At level 5 your trainer makes you a Berserker, Savage, Skald, Thane or Warrior.
36 realm 2 Mystic: Bonedancer, Runemaster, Spiritmaster | races [5, 6, 7, 8, 14] | PIE/DEX/QUI
   Midgard's seekers of hidden lore, who call on runes, spirits and the bones of the dead. At level 5 your trainer makes you a Bonedancer, Runemaster or Spiritmaster.
37 realm 2 Seer: Healer, Shaman | races [5, 6, 7, 8] | PIE/CON/DEX
   Midgard's faithful, blessed by the gods with healing and protective magic. At level 5 your trainer makes you a Healer or Shaman.
38 realm 2 Rogue: Hunter, Shadowblade | races [5, 7, 8, 14] | DEX/QUI/STR
   Midgard's hunters and assassins, who strike from the shadows or from afar. At level 5 your trainer makes you a Hunter or Shadowblade.
51 realm 3 Magician: Eldritch, Enchanter, Mentalist | races [9, 11, 12] | INT/DEX/QUI
   Hibernia's spellcasters, schooled in the magic of light, mana and the mind. At level 5 your trainer makes you an Eldritch, Enchanter or Mentalist.
52 realm 3 Guardian: Blademaster, Champion, Hero | races [9, 10, 11, 12, 15] | STR/CON/DEX
   Hibernia's fighters, trained to hold the line with blade, hammer and shield. At level 5 your trainer makes you a Blademaster, Champion or Hero.
53 realm 3 Naturalist: Bard, Druid, Warden | races [9, 10, 15] | EMP/DEX/CON
   Hibernia's keepers of the land, who draw on nature's magic to heal and protect. At level 5 your trainer makes you a Bard, Druid or Warden.
54 realm 3 Stalker: Nightshade, Ranger | races [9, 11, 12] | DEX/QUI/STR
   Hibernia's hunters and assassins, who move unseen through forest and shadow. At level 5 your trainer makes you a Nightshade or Ranger.
57 realm 3 Forester: Animist, Valewalker | races [9, 10, 15] | INT/DEX/CON
   Hibernia's protectors of the deep forest, who draw power from living wood and growing things. At level 5 your trainer makes you an Animist or Valewalker.
```

- [ ] **Step 8: Commit**

```bash
chmod +x client/patches/classdata.py
git add client/patches/classdata.py client/patches/src/base_classes.py client/patches/tests/test_classdata.py
git commit -m "feat(client): base-class data for classic creation, derived from the server sources" -m "classdata.py lists the base classes the classic creation screen shows: not disabled (Disciple counts as enabled, as world_fixes.py makes it), leading to at least one enabled full class, with the union of those classes' EligibleRaces limited to the classic and Shrouded Isles races and checked against the base class's own EligibleRaces. src/base_classes.py holds the hand-written descriptions and highlighted stats." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
Expected: `3 files changed, 618 insertions(+)`.

---

### Task 5: Base-class code cave in game.dll (new `.hdcc` section and hook)

This task makes the creation screen list the classic base classes. A small code cave, written in nasm, goes into a new last section `.hdcc` of `game.dll`, and a 5-byte hook calls it every time pregame opens. The cave hides the 47 final classes and the races after Shrouded Isles, then registers the 15 base classes from Task 4 with their descriptions, highlighted stats and races. `build.py` assembles the cave for the section's address, appends the section and writes the hook. Then `classic-creation.json` is regenerated.

Background for the engineer (facts checked on 2026-10-06 against the real `game.dll`, SHA-256 `67dcf68a…`, and the investigation's disassembly):
- **The hook.** `0x5B0031` builds the creation registry. The registry is the object at creation window + `0x84` (`0x599C9A: lea eax,[edi+0x84]; call 0x5B0031`). It runs the three realm functions, then `0x5B0050: push ebx` / `0x5B0051: call 0x5B438C` (`E8 36 43 00 00`). The hook turns that call into `call <cave>`. The registry is already on the stack, so the cave is stdcall with one argument and returns with `ret 4`. The cave's first job is the displaced `call 0x5B438C`. With the section at VA `0x248B000`, the hook bytes are `E8 AA AF ED 01`. `build.py` computes them; nothing hard-codes them except one test.
- **Where the tables live.** Both tables are `std::vector`s of object pointers, indexed by id, with the end pointer 4 bytes after the begin pointer.
  - **Races:** begin at registry + `0x28`, end at + `0x2C`. The evidence:
    - The lookup `0x5B485A` does `lea eax,[esi+0x24]; call 0x515724` (size = `([v+8]-[v+4])>>2`), then `cmp edi,eax; jae null; mov eax,[esi+0x28]; mov eax,[eax+edi*4]`.
    - The race functions store each race at its id: Briton at `0x5B03C3` (`mov eax,[ebp+8]; mov eax,[eax+0x28]; mov [eax+4],ecx`); the Minotaurs at `0x5B0C9F` (`add eax,0x4c`, race 19), `0x5B2210` (`add eax,0x50`, race 20) and `0x5B376D` (`add eax,0x54`, race 21).
    - The registry reset `0x5B005B` walks `[ebx+0x28]` up to `[ebx+0x2C]` and deletes each race.
    - The race button loops walk creation window + `0xAC`..`0xB0`, which is registry + `0x28`..`0x2C`: labels at `0x599DED`, the slot map at `0x599F27`. The random default race at `0x59F0E0` uses `lea edi,[ebp+0xa8]` with `[edi+4]`/`[edi+8]`.
  - **Classes:** begin at registry + `0x38`, end at + `0x3C`. The lookup `0x5B4842` uses vector + `0x34`. `REGISTER_CLASS` stores the new class at `0x5B0270`: `mov eax,[esi+0x38]; lea eax,[eax+ecx*4]; mov [eax],edi`, with no bounds check. The stock code registers id 62 there, so ids up to 62 fit.
- **Hiding.** The race object's availability mask is at `+0x54`. The constructor block at `0x5B0301` sets `mov dword [esi+0x54],7` for Briton. The race filter `0x59F729` requires realm (`+4`) = ctx+`0x48`, `+0x28` ≤ ctx+`0x4C`, and `[race+0x54] & [ctx+0x50]` ≠ 0. The class filter `0x59F774` does the same with the class mask at `+0x30`. All six callers of the race filter are creation-screen paths: the labels `0x599E0D`, the slot map `0x599F41`, the button updates `0x59EDCD` and `0x59EF31`, and the random default race `0x59F0FF`/`0x59F13B`. So a race with mask 0 gets no button and no label, and is never the default. The class side works the same way through `0x59F774`. The cave changes only masks, never the tables: it zeroes `+0x30` for `classdata.FINAL_CLASS_IDS` and `+0x54` for `classdata.HIDE_RACES` (16–21), bounds-checks every id against its table and skips missing objects.
- **Registering a base class** replays a stock block, for example Armsman's at `0x5B0D0F`–`0x5B0DE3`:
  - The frame layout is the same: the 0x20-byte entry at `ebp-0x50`, the race vector at `ebp-0x30`, the stat ids at `ebp-0x20`, the push-back value at `ebp-0x14` and the stat amount bytes at `ebp-0x10`.
  - The calls are `0x5B4A65` (push back), `0x520E28` (copy into the by-value argument), `0x5B01A3` (`REGISTER_CLASS`, `ret 0x28`) and `0x45BDC3` (free). `REGISTER_CLASS` reads the entry at `0x5B01E2`. These helpers keep `ebx`, `esi` and `edi`, and the stock code relies on that too.
  - Every base class gets expansion 0, trial 1, mask 7, gender 0 and description id 0, so the text is shown as written. Its stat amounts are 10/10/10, a legal 30-point recipe; P1 stops auto-assign anyway. The highlight `0x59E9C7` reads only the stat ids (class + `0x48`).
- **Data include.** `build.py` writes `baseclass_data.inc` from Task 4's `classdata.base_classes()`, using the `REC_*` layout in `baseclass.asm`. It holds one record per base class (`dd` id, realm, name id, name pointer, `desc_<id>`; `dd` 3 stat ids; `dd` race count, race ids), then `dd 0`, then the two 0-terminated byte hide lists and the descriptions. Descriptions must be printable ASCII without `"`, because nasm strings have no escapes.
- **Origin.** nasm needs the section's VA (`-D HDCC_ORG=…`) before the section exists. The VA doesn't depend on the payload: it is the next section-aligned address after the last section. `cave_origin(original)` gets it from `pe.append_section` with a 1-byte payload. `patch_game_dll` appends the real cave at the same place. A real-file test checks this: section bytes == `assemble_cave(section VA, …)`.
- **What `game.dll`'s ops become.** In offset order: NumberOfSections (`0x15E`), SizeOfCode (`0x175`), SizeOfImage + CheckSum merged (`0x1A9`), the new section header (`0x390`), P2, P1, P3, the hook's rel32 (`0x1B0052`), and one `append` of 4096 bytes. The append holds the cave (3,275 bytes) and zero padding to `FileAlignment`. Everything in it is our own code and text.
- **`--server-src` is `source/server`.** That is the folder that holds `GameServer/`, Task 4's `classdata` convention. Task 3 wrote `source/server/GameServer` when the argument was still unused, so this task corrects `build.py`, `test_build.py` and the regenerate command.
- **nasm** (2.x; tested with 2.16.01, `apt install nasm`) is needed by `build.py` and by `test_cave.py`. It is never needed by the appliers. Task 8 installs it in CI.
- Real-file tests read `HDC_CLIENT_FILES` (as in Task 3). `test_build.py` also reads `HDC_TEST_WORLD`. Nothing writes to a client folder.

**Files:**
- Create: `client/patches/src/baseclass.asm`
- Modify: `client/patches/build.py`
- Modify: `client/patches/classic-creation.json` (regenerated by `build.py` in Step 5)
- Test: `client/patches/tests/test_cave.py` (create)
- Test: `client/patches/tests/test_build.py` (modify: `SERVER_SRC`, and the `game.dll` ops test)

**Interfaces:**
- Consumes:
  - Task 1: `patchset.load(path) -> dict`, `patchset.transform(data, ops, bundle_dir) -> bytes`, and the `apply_patches.py --client DIR [--check]` CLI.
  - Task 3, `pe.py`: `PE(data)` (`.sections`, `.image_base`, `.file_alignment`, `.section_alignment`, `.size_of_image`, `.size_of_headers`, `.header_offsets`, `.offset(va)`), `Section`, `align(value, alignment)`, `checksum(data, checksum_offset)`, `append_section(data, name, payload, characteristics=0x60000020) -> tuple[bytes, int]`, `diff_ops(old, new)`.
  - Task 3, `build.py`: `STAT_FLOW`, `GAME_DLL_SHA256`, `patch_game_dll`, `build_patchset`, `to_json`, `main`.
  - Task 4, `classdata.py`: `base_classes(server_src: str, disabled: str) -> list[BaseClass]` (fields `id, realm, name, name_id, name_ptr, finals, races, stats, description`), `read_disabled_classes(world_db) -> str`, `FINAL_CLASS_IDS` (47 ids), `HIDE_RACES = [16, 17, 18, 19, 20, 21]`.
- Produces:
  - `client/patches/src/baseclass.asm`: `nasm -f bin -D HDCC_ORG=<VA>`. It includes `baseclass_data.inc`, which must define `base_table`, `hide_classes`, `hide_races` and `desc_<id>`. The entry point `hdc_post` is the first byte.
  - `client/patches/build.py`:
    - `HERE`, `CAVE_SOURCE`, `CAVE_SECTION = ".hdcc"`, `HOOK_VA = 0x5B0051`, `HOOK_FROM = "e836430000"`
    - `check_game_dll(original: bytes) -> None`
    - `cave_origin(original: bytes) -> int`
    - `cave_data_inc(classes: list, hide_classes: list[int], hide_races: list[int]) -> str`
    - `assemble_cave(org: int, data_inc: str) -> bytes` (ValueError when nasm is missing or fails)
    - `patch_game_dll(original: bytes, cave: bytes | None = None) -> bytes` now accepts the cave.
    - `build_patchset(client_dir, world_db, server_src, splash_mpk=None) -> dict` now always builds the cave. `server_src` is `source/server`.
    - The CLI is unchanged apart from that `--server-src` value. Task 6 still adds the splash entry at the `splash_mpk` guard.
  - `client/patches/classic-creation.json`: the `game.dll` after-hash becomes `1f6869d4eb5c495fc9018cfb565b711ac82df1471b85898bb2926cebcf3548ea`.

- [ ] **Step 1: Write the failing tests**

Create `client/patches/tests/test_cave.py`:

```python
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
```

In `client/patches/tests/test_build.py`, `SERVER_SRC` must be the folder that holds `GameServer/`. Replace the line

```python
SERVER_SRC = os.path.join(REPO, "source", "server", "GameServer")
```

with

```python
SERVER_SRC = os.path.join(REPO, "source", "server")
```

In the same file, the ops of `game.dll` now include the section and the hook. Replace

```python
    def test_game_dll_changes_only_the_checksum_and_the_stat_flow(self):
        ops = self.patchset["files"][0]["ops"]
        self.assertEqual([(op["op"], op["offset"]) for op in ops],
                         [("replace", 0x1B0), ("replace", 0x19A853), ("replace", 0x19C0B2), ("replace", 0x19C574)])
```

with

```python
    def test_game_dll_ops_are_the_headers_the_stat_flow_the_hook_and_the_cave(self):
        ops = self.patchset["files"][0]["ops"]
        # NumberOfSections, SizeOfCode, SizeOfImage + CheckSum, the .hdcc section header, P2, P1, P3, the hook
        self.assertEqual([(op["op"], op.get("offset")) for op in ops],
                         [("replace", 0x15E), ("replace", 0x175), ("replace", 0x1A9), ("replace", 0x390),
                          ("replace", 0x19A853), ("replace", 0x19C0B2), ("replace", 0x19C574),
                          ("replace", 0x1B0052), ("append", None)])
        self.assertEqual(len(ops[-1]["data"]), 2 * 0x1000)
```

The rest of that test (transform, then check the checksum) stays as it is.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -p 'test_cave.py'`

Expected (shortened):
```
ERROR: setUpClass (tests.test_cave.AssembleCaveTests)
AttributeError: module 'build' has no attribute 'assemble_cave'
ERROR: test_hide_lists (tests.test_cave.CaveDataIncTests.test_hide_lists)
AttributeError: module 'build' has no attribute 'cave_data_inc'
ERROR: test_one_record_and_one_description_per_base_class (tests.test_cave.CaveDataIncTests.test_one_record_and_one_description_per_base_class)
AttributeError: module 'build' has no attribute 'cave_data_inc'
ERROR: test_refuses_what_the_cave_cannot_hold (tests.test_cave.CaveDataIncTests.test_refuses_what_the_cave_cannot_hold)
AttributeError: module 'build' has no attribute 'cave_data_inc'
ERROR: test_the_fighter_record (tests.test_cave.CaveDataIncTests.test_the_fighter_record)
AttributeError: module 'build' has no attribute 'cave_data_inc'
Ran 10 tests in 0.010s
FAILED (errors=5, skipped=6)
```

Run: `HDC_CLIENT_FILES=~/Games/HearthDAoC/client HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -m unittest discover -s client/patches/tests -t client/patches -p 'test_build.py'`

Expected (shortened; the old generator has no section and no hook yet):
```
.........F...
FAIL: test_game_dll_ops_are_the_headers_the_stat_flow_the_hook_and_the_cave (tests.test_build.RealBuildTests.test_game_dll_ops_are_the_headers_the_stat_flow_the_hook_and_the_cave)
AssertionError: Lists differ: [('replace', 432), ('replace', 1681491), ('replace', 1[25 chars]948)] != [('replace', 350), ('replace', 373), ('replace', 425),[119 chars]one)]
First differing element 0:
('replace', 432)
('replace', 350)
Ran 13 tests in 0.399s
FAILED (failures=1)
```

- [ ] **Step 3: Write minimal implementation**

**3a. The cave source.** Check that nasm is installed: `nasm -v` prints a 2.x version (for example `NASM version 2.16.01`). If it doesn't, install it (`sudo apt install nasm`).

Create `client/patches/src/baseclass.asm`:

```nasm
; HearthDAoC classic character creation: the code cave in game.dll's .hdcc section.
;
; build.py assembles this file with nasm (-f bin -D HDCC_ORG=<VA of the .hdcc section>) next to
; baseclass_data.inc, which it generates from the server's class files and src/base_classes.py.
; Target: the OfflineDAoC 0.34 classic game.dll, whose character creation code is stock 1.127.
;
; The hook at 0x5B0051 turns "call 0x5B438C" in the creation-registry builder (0x5B0031) into
; "call hdc_post". The builder has just pushed the registry (ebx), so hdc_post is stdcall with
; that one argument. It runs every time pregame opens, after the three realms have registered
; their races and final classes, and:
;   1. makes the displaced call;
;   2. hides the final classes and the races after Shrouded Isles by zeroing their availability
;      masks. The creation screen's filters (classes 0x59F774, races 0x59F729) skip any object
;      whose mask has no bit in common with the server type (ctx+0x50), so hidden objects get
;      no button, no label and are never picked at random;
;   3. registers each base class the way a stock registration block does (Armsman's runs from
;      0x5B0D0F to 0x5B0DE3).

BITS 32
%ifndef HDCC_ORG
%error "assemble with -D HDCC_ORG=<VA of the .hdcc section>"
%endif
org HDCC_ORG

; game.dll functions
STATDESC_INIT   equ 0x5B438C  ; the displaced call: stdcall(registry)
REGISTER_CLASS  equ 0x5B01A3  ; ecx = &entry; stdcall(registry, id, realm, &stat ids, &stat amounts,
                              ;   race vector by value (16 bytes), stat count); ret 0x28
VEC_PUSH_BACK   equ 0x5B4A65  ; eax = &vector; stdcall(&value)
VEC_COPY        equ 0x520E28  ; ecx = &source vector; stdcall(&destination)
VEC_FREE        equ 0x45BDC3  ; esi = &vector

; The creation registry holds two tables of object pointers indexed by id. Each is the begin
; pointer of a std::vector, with the end pointer 4 bytes later.
REG_RACES       equ 0x28      ; race objects by race id (lookup 0x5B485A)
REG_CLASSES     equ 0x38      ; class objects by class id (lookup 0x5B4842)
RACE_MASK       equ 0x54      ; race object: availability mask (race filter 0x59F729)
CLASS_MASK      equ 0x30      ; class object: availability mask (class filter 0x59F774)

; What every base class registers with (the entry fields; REGISTER_CLASS copies them at 0x5B01E2)
EXPANSION       equ 0         ; entry +0x08: classic
TRIAL           equ 1         ; entry +0x0C: allowed on trial accounts
MASK            equ 7         ; entry +0x10: listed on every server type
GENDER          equ 0         ; entry +0x14: both genders
DESC_ID         equ 0         ; entry +0x18: no string id, so the text at +0x1C is shown as written
STAT_COUNT      equ 3
AMOUNT          equ 10        ; auto-assign recipe: +10 to each highlighted stat, 30 points. Patch P1
                              ; stops auto-assign; the stat ids only colour the stats.

; A base_table record in baseclass_data.inc, all dwords. The table ends with a 0 class id.
REC_ID          equ 0x00
REC_REALM       equ 0x04      ; 1 Albion, 2 Midgard, 3 Hibernia
REC_NAME_ID     equ 0x08      ; name string id
REC_NAME        equ 0x0C      ; name pointer (the client's own .rdata string)
REC_DESC        equ 0x10      ; description pointer
REC_STATS       equ 0x14      ; 3 stat ids (0 STR, 1 CON, 2 DEX, 3 QUI, 4 INT, 5 PIE, 6 EMP, 7 CHA)
REC_RACE_COUNT  equ 0x20
REC_RACES       equ 0x24      ; the race ids

; hdc_post's stack frame: the same layout as a stock registration block
ENTRY           equ -0x50     ; the 0x20-byte entry REGISTER_CLASS reads through ecx
RACE_VECTOR     equ -0x30     ; std::vector<int>: allocator, begin, end, end of storage
STAT_IDS        equ -0x20     ; 3 dwords
RACE_ID         equ -0x14     ; the value VEC_PUSH_BACK copies
STAT_AMOUNTS    equ -0x10     ; 3 bytes

hdc_post:
    push ebp
    mov ebp, esp
    sub esp, 0x50
    push ebx
    push esi
    push edi
    mov ebx, [ebp+8]                    ; the registry
    push ebx
    call STATDESC_INIT

    mov esi, hide_classes
    mov edi, REG_CLASSES
    mov edx, CLASS_MASK
    call hide
    mov esi, hide_races
    mov edi, REG_RACES
    mov edx, RACE_MASK
    call hide

    mov edi, base_table
.next_class:
    cmp dword [edi+REC_ID], 0
    je .done
    mov eax, [edi+REC_NAME_ID]
    mov [ebp+ENTRY+0x00], eax
    mov eax, [edi+REC_NAME]
    mov [ebp+ENTRY+0x04], eax
    mov dword [ebp+ENTRY+0x08], EXPANSION
    mov dword [ebp+ENTRY+0x0C], TRIAL
    mov dword [ebp+ENTRY+0x10], MASK
    mov dword [ebp+ENTRY+0x14], GENDER
    mov dword [ebp+ENTRY+0x18], DESC_ID
    mov eax, [edi+REC_DESC]
    mov [ebp+ENTRY+0x1C], eax
    mov eax, [edi+REC_STATS+0]
    mov [ebp+STAT_IDS+0], eax
    mov eax, [edi+REC_STATS+4]
    mov [ebp+STAT_IDS+4], eax
    mov eax, [edi+REC_STATS+8]
    mov [ebp+STAT_IDS+8], eax
    mov byte [ebp+STAT_AMOUNTS+0], AMOUNT
    mov byte [ebp+STAT_AMOUNTS+1], AMOUNT
    mov byte [ebp+STAT_AMOUNTS+2], AMOUNT
    xor eax, eax                        ; an empty race vector
    mov [ebp+RACE_VECTOR+4], eax
    mov [ebp+RACE_VECTOR+8], eax
    mov [ebp+RACE_VECTOR+12], eax
    mov ecx, [edi+REC_RACE_COUNT]
    lea esi, [edi+REC_RACES]
.next_race:
    test ecx, ecx
    jz .register
    push ecx
    mov eax, [esi]
    mov [ebp+RACE_ID], eax
    lea eax, [ebp+RACE_ID]
    push eax
    lea eax, [ebp+RACE_VECTOR]
    call VEC_PUSH_BACK
    pop ecx
    add esi, 4
    dec ecx
    jmp .next_race
.register:
    push STAT_COUNT
    sub esp, 0x10                       ; the race vector argument, passed by value
    mov eax, esp
    push eax
    lea ecx, [ebp+RACE_VECTOR]
    call VEC_COPY
    lea eax, [ebp+STAT_AMOUNTS]
    push eax
    lea eax, [ebp+STAT_IDS]
    push eax
    push dword [edi+REC_REALM]
    push dword [edi+REC_ID]
    push ebx
    lea ecx, [ebp+ENTRY]
    call REGISTER_CLASS                 ; frees the argument vector and pops 0x28 bytes
    lea esi, [ebp+RACE_VECTOR]
    call VEC_FREE
    mov eax, [edi+REC_RACE_COUNT]
    lea edi, [edi+REC_RACES+eax*4]      ; the next record
    jmp .next_class
.done:
    pop edi
    pop esi
    pop ebx
    leave
    ret 4

; Zero the availability mask of each listed object.
; In: ebx = registry, esi = 0-terminated byte list of ids, edi = REG_RACES or REG_CLASSES,
; edx = the mask's offset in the object. An id past the table's end or without an object is
; skipped. Changes eax, ecx and esi.
hide:
    movzx eax, byte [esi]
    inc esi
    test eax, eax
    jz .done
    mov ecx, [ebx+edi+4]
    sub ecx, [ebx+edi]
    shr ecx, 2                          ; the number of table entries
    cmp eax, ecx
    jae hide
    mov ecx, [ebx+edi]
    mov ecx, [ecx+eax*4]
    test ecx, ecx
    jz hide
    and dword [ecx+edx], 0
    jmp hide
.done:
    ret

align 4, db 0
; base_table (records, then dd 0), hide_classes and hide_races (bytes, then 0), descriptions
%include "baseclass_data.inc"
```

**3b. Extend `build.py`.** Replace the whole of `client/patches/build.py` with the version below. It keeps the executable bit. The changes from Task 3:
- the docstring and `--server-src` help say `source/server`;
- new imports `shutil`, `sqlite3`, `subprocess`, `tempfile` and `classdata`;
- new constants `HERE`, `CAVE_SOURCE`, `CAVE_SECTION`, `HOOK_VA` and `HOOK_FROM`;
- new functions `check_game_dll`, `cave_origin`, `cave_data_inc` and `assemble_cave`;
- `patch_game_dll` appends the cave and writes the hook where Task 3 raised "cannot add the code cave yet";
- `build_patchset` builds the cave from `server_src` and `world_db`;
- `main` also reports `sqlite3.Error`, for example a missing world file.

```python
#!/usr/bin/env python3
"""Generate HearthDAoC's classic character creation patch set (classic-creation.json).

Reads an OfflineDAoC 0.34 classic client's own files, applies HearthDAoC's edits in memory and
writes only patch data: SHA-256 hashes and byte and text edits, plus our own code cave. No EA file
is ever copied into the patch set. Running it twice on the same inputs gives byte-identical output.
Needs nasm (apt install nasm) for the cave, src/baseclass.asm.

  python3 client/patches/build.py --client ~/Games/HearthDAoC/client \\
      --world-db clean-classic-0.34.db --server-src source/server \\
      --out client/patches/classic-creation.json
"""
import argparse
import hashlib
import json
import os
import shutil
import sqlite3
import struct
import subprocess
import sys
import tempfile

import classdata
import pe

HERE = os.path.dirname(os.path.abspath(__file__))
FORMAT = 1
NAME = "classic-creation"
CLIENT = "OfflineDAoC 0.34 classic"
GAME_DLL = "game.dll"
GAME_DLL_SHA256 = "67dcf68a37b95a93946a943b99d5e19b4a03e08cd6469275e25c7b909de21e99"
CUSTOMIZE_STATS = "pregame/character_customize_stats.xml"

# Classic stat flow in game.dll: (VA, original bytes, new bytes), hex. From the 2026-10-06 investigation.
STAT_FLOW = [
    # P1: auto-assign (0x59C086) returns right after its reset: race base stats and 30 points to place.
    (0x59C0B2, "8b465c", "eb6890"),
    # P2: Continue on the customise screen checks unspent points for new characters too,
    # with the client's own "You must use all your points!" popup.
    (0x59A853, "0f859302000080bb28fa0000000f8478020000833dc8bb4502007437",
     "75f6833dc8bb450200751180bb28fa0000000f8473020000eb399090"),
    # P3: the attributes dialog starts visible.
    (0x59C574, "01", "00"),
]

# The base-class code cave: src/baseclass.asm, assembled into a new last section of game.dll.
CAVE_SOURCE = os.path.join(HERE, "src", "baseclass.asm")
CAVE_SECTION = ".hdcc"
# The hook: "call 0x5B438C" in the creation-registry builder (0x5B0031), right after the three
# realms register their races and classes, becomes "call <the cave>". The cave makes that call.
HOOK_VA = 0x5B0051
HOOK_FROM = "e836430000"

# The attributes dialog's Optimize button (ControlId 1021), removed whole. The pregame XML uses CRLF.
OPTIMIZE_BUTTON = (
    "\t\t<ButtonDef>\r\n"
    "\t\t\t<TemplateName>button_small</TemplateName>\r\n"
    "\t\t\t<ControlId>1021</ControlId>\r\n"
    "\t\t\t<Label>Optimize</Label>\r\n"
    "\t\t\t<Alignment>\r\n"
    "\t\t\t\t<TopLeft>true</TopLeft>\r\n"
    "\t\t\t</Alignment>\r\n"
    "\t\t\t<Position>\r\n"
    "\t\t\t\t<X>364</X>\r\n"
    "\t\t\t\t<Y>224</Y>\r\n"
    "\t\t\t</Position>\r\n"
    "\t\t\t<LabelAlignment>\r\n"
    "\t\t\t\t<CenterVertically>true</CenterVertically>\r\n"
    "\t\t\t\t<CenterHorizontally>true</CenterHorizontally>\r\n"
    "\t\t\t</LabelAlignment>\r\n"
    "\t\t</ButtonDef>\r\n"
)
XML_EDITS = {CUSTOMIZE_STATS: [(OPTIMIZE_BUTTON, "")]}


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read_client_file(client_dir: str, path: str) -> bytes:
    with open(os.path.join(client_dir, *path.split("/")), "rb") as f:
        return f.read()


def check_game_dll(original: bytes) -> None:
    """ValueError unless `original` is the OfflineDAoC 0.34 classic game.dll."""
    if sha256(original) != GAME_DLL_SHA256:
        raise ValueError(f"{GAME_DLL} is not the OfflineDAoC 0.34 classic file (SHA-256 {GAME_DLL_SHA256})")


def cave_origin(original: bytes) -> int:
    """The VA of the .hdcc section that patch_game_dll adds to `original`: the next section-aligned
    address after the last section. It doesn't depend on the cave's size."""
    check_game_dll(original)
    return pe.append_section(original, CAVE_SECTION, b"\xcc")[1]


def cave_data_inc(classes: list, hide_classes: list[int], hide_races: list[int]) -> str:
    """nasm source of baseclass_data.inc for src/baseclass.asm: one base_table record per class
    (classdata.BaseClass; the REC_* layout in baseclass.asm), the hide lists and the descriptions."""
    for ids in (hide_classes, hide_races):
        if not all(0 < i < 256 for i in ids):
            raise ValueError(f"hide list ids must be 1 to 255: {ids}")
    lines = [
        "; Generated by client/patches/build.py from the server's class files and",
        "; client/patches/src/base_classes.py. Do not edit. Layout: REC_* in baseclass.asm.",
        "",
        "base_table:",
    ]
    for bc in classes:
        if len(bc.stats) != 3 or not bc.races:
            raise ValueError(f"base class {bc.id} needs 3 stats and at least one race")
        if not (bc.description.isascii() and bc.description.isprintable()) or '"' in bc.description:
            raise ValueError(f"base class {bc.id}: the description must be printable ASCII without '\"'")
        lines += [
            f"    ; {bc.id} {bc.name}: {', '.join(bc.finals)}",
            f"    dd {bc.id}, {bc.realm}, {bc.name_id:#x}, {bc.name_ptr:#x}, desc_{bc.id}",
            f"    dd {', '.join(str(s) for s in bc.stats)}",
            f"    dd {len(bc.races)}, {', '.join(str(r) for r in bc.races)}",
        ]
    lines += [
        "    dd 0",
        "",
        "hide_classes:",
        "    db " + ", ".join([str(i) for i in hide_classes] + ["0"]),
        "hide_races:",
        "    db " + ", ".join([str(i) for i in hide_races] + ["0"]),
        "",
    ]
    for bc in classes:
        lines += [f"desc_{bc.id}:", f'    db "{bc.description}", 0']
    return "\n".join(lines) + "\n"


def assemble_cave(org: int, data_inc: str) -> bytes:
    """src/baseclass.asm assembled by nasm for a section at VA `org`, with `data_inc` as its
    baseclass_data.inc. nasm runs in a temporary folder that holds just those two files."""
    nasm = shutil.which("nasm")
    if nasm is None:
        raise ValueError("nasm not found: install it (for example apt install nasm) to build the cave")
    with tempfile.TemporaryDirectory(prefix="hdc-cave-") as tmp:
        shutil.copyfile(CAVE_SOURCE, os.path.join(tmp, "baseclass.asm"))
        with open(os.path.join(tmp, "baseclass_data.inc"), "w", encoding="ascii", newline="\n") as f:
            f.write(data_inc)
        run = subprocess.run([nasm, "-f", "bin", f"-DHDCC_ORG={org:#x}", "-o", "baseclass.bin", "baseclass.asm"],
                             cwd=tmp, capture_output=True, text=True)
        if run.returncode != 0:
            raise ValueError(f"nasm failed: {run.stderr.strip()}")
        with open(os.path.join(tmp, "baseclass.bin"), "rb") as f:
            return f.read()


def patch_game_dll(original: bytes, cave: bytes | None = None) -> bytes:
    """The classic 0.34 game.dll with the stat-flow patches and a fresh PE checksum. With `cave`
    (src/baseclass.asm assembled for cave_origin(original)), also the .hdcc section holding it and
    the hook at HOOK_VA that calls it."""
    check_game_dll(original)
    image = pe.PE(original)
    data = bytearray(original)
    for va, before, after in STAT_FLOW:
        start, old, new = image.offset(va), bytes.fromhex(before), bytes.fromhex(after)
        if len(new) != len(old) or data[start:start + len(old)] != old:
            raise ValueError(f"{GAME_DLL}: unexpected bytes at VA {va:#x}")
        data[start:start + len(old)] = new
    if cave is not None:
        hook = image.offset(HOOK_VA)
        if data[hook:hook + 5] != bytes.fromhex(HOOK_FROM):
            raise ValueError(f"{GAME_DLL}: unexpected bytes at VA {HOOK_VA:#x}")
        appended, va = pe.append_section(bytes(data), CAVE_SECTION, cave)
        data = bytearray(appended)
        data[hook:hook + 5] = b"\xe8" + struct.pack("<i", va - (HOOK_VA + 5))
    offset = image.header_offsets["checksum"]
    struct.pack_into("<I", data, offset, pe.checksum(data, offset))
    return bytes(data)


def edit_text(data: bytes, edits: list[tuple[str, str]]) -> bytes:
    """Apply (find, replace) pairs to `data` decoded as Latin-1; each find must occur exactly once."""
    text = data.decode("latin-1")
    for find, replace in edits:
        count = text.count(find)
        if count != 1:
            raise ValueError(f"expected the text once, found it {count} times: {find[:60]!r}")
        text = text.replace(find, replace)
    return text.encode("latin-1")


def file_entry(path: str, before: bytes, after: bytes, ops: list[dict]) -> dict:
    return {"path": path, "before": sha256(before), "after": sha256(after), "ops": ops}


def build_patchset(client_dir: str, world_db: str, server_src: str, splash_mpk: str | None = None) -> dict:
    """The classic-creation patch set for the client files in `client_dir`. The base classes come
    from `server_src` (the folder holding GameServer/, in this repo source/server) and the world's
    disabled_classes in `world_db`. `splash_mpk` is the slot for the splash entry; this version
    only accepts None."""
    if splash_mpk is not None:
        raise ValueError("this build.py cannot add the splash entry yet")
    original = read_client_file(client_dir, GAME_DLL)
    check_game_dll(original)
    classes = classdata.base_classes(server_src, classdata.read_disabled_classes(world_db))
    data_inc = cave_data_inc(classes, classdata.FINAL_CLASS_IDS, classdata.HIDE_RACES)
    patched = patch_game_dll(original, assemble_cave(cave_origin(original), data_inc))
    files = [file_entry(GAME_DLL, original, patched, pe.diff_ops(original, patched))]
    for path, edits in XML_EDITS.items():
        data = read_client_file(client_dir, path)
        ops = [{"op": "text-replace", "find": find, "replace": replace} for find, replace in edits]
        files.append(file_entry(path, data, edit_text(data, edits), ops))
    return {"format": FORMAT, "name": NAME, "client": CLIENT, "files": files}


def to_json(patchset: dict) -> str:
    return json.dumps(patchset, indent=1, ensure_ascii=True, sort_keys=False) + "\n"


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Generate HearthDAoC's classic-creation patch set.")
    parser.add_argument("--client", required=True, help="OfflineDAoC 0.34 classic client folder (read only)")
    parser.add_argument("--world-db", required=True, help="the classic edition's clean world database (read only)")
    parser.add_argument("--server-src", required=True, help="the server sources holding GameServer/, source/server")
    parser.add_argument("--out", required=True, help="the patch set to write")
    args = parser.parse_args(argv)
    try:
        patchset = build_patchset(args.client, args.world_db, args.server_src)
    except (OSError, ValueError, sqlite3.Error) as e:
        print(f"build.py: {e}", file=sys.stderr)
        return 1
    with open(args.out, "w", encoding="ascii", newline="\n") as f:
        f.write(to_json(patchset))
    print(f"Wrote {args.out}: {', '.join(entry['path'] for entry in patchset['files'])}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -p 'test_cave.py' -v`

Expected (the 6 real-file tests skip without `HDC_CLIENT_FILES`):
```
test_calls_land_on_the_game_dll_functions (tests.test_cave.AssembleCaveTests.test_calls_land_on_the_game_dll_functions) ... ok
test_each_record_matches_the_layout_in_baseclass_asm (tests.test_cave.AssembleCaveTests.test_each_record_matches_the_layout_in_baseclass_asm) ... ok
test_is_reproducible (tests.test_cave.AssembleCaveTests.test_is_reproducible) ... ok
test_reports_a_missing_nasm (tests.test_cave.AssembleCaveTests.test_reports_a_missing_nasm) ... ok
test_reports_a_nasm_error (tests.test_cave.AssembleCaveTests.test_reports_a_nasm_error) ... ok
test_starts_with_the_entry_point (tests.test_cave.AssembleCaveTests.test_starts_with_the_entry_point) ... ok
test_the_code_points_at_its_data (tests.test_cave.AssembleCaveTests.test_the_code_points_at_its_data) ... ok
test_the_origin_only_moves_addresses (tests.test_cave.AssembleCaveTests.test_the_origin_only_moves_addresses) ... ok
test_hide_lists (tests.test_cave.CaveDataIncTests.test_hide_lists) ... ok
test_one_record_and_one_description_per_base_class (tests.test_cave.CaveDataIncTests.test_one_record_and_one_description_per_base_class) ... ok
test_refuses_what_the_cave_cannot_hold (tests.test_cave.CaveDataIncTests.test_refuses_what_the_cave_cannot_hold) ... ok
test_the_fighter_record (tests.test_cave.CaveDataIncTests.test_the_fighter_record) ... ok
test_nothing_else_changes (tests.test_cave.RealGameDllCaveTests.test_nothing_else_changes) ... skipped 'set HDC_CLIENT_FILES to an OfflineDAoC 0.34 classic client folder'
test_the_checksum_is_valid (tests.test_cave.RealGameDllCaveTests.test_the_checksum_is_valid) ... skipped 'set HDC_CLIENT_FILES to an OfflineDAoC 0.34 classic client folder'
test_the_committed_patch_set_is_this_build (tests.test_cave.RealGameDllCaveTests.test_the_committed_patch_set_is_this_build) ... skipped 'set HDC_CLIENT_FILES to an OfflineDAoC 0.34 classic client folder'
test_the_hook_calls_the_cave_entry (tests.test_cave.RealGameDllCaveTests.test_the_hook_calls_the_cave_entry) ... skipped 'set HDC_CLIENT_FILES to an OfflineDAoC 0.34 classic client folder'
test_the_section_holds_the_cave (tests.test_cave.RealGameDllCaveTests.test_the_section_holds_the_cave) ... skipped 'set HDC_CLIENT_FILES to an OfflineDAoC 0.34 classic client folder'
test_the_section_table_is_valid (tests.test_cave.RealGameDllCaveTests.test_the_section_table_is_valid) ... skipped 'set HDC_CLIENT_FILES to an OfflineDAoC 0.34 classic client folder'
Ran 18 tests in 0.064s
OK (skipped=6)
```

Run with the real files: `HDC_CLIENT_FILES=~/Games/HearthDAoC/client HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -m unittest discover -s client/patches/tests -t client/patches`

Expected: everything passes except the check that the committed patch set is this build. It fails until Step 5 regenerates the patch set. The test count is 148 with Tasks 1 to 5 (Task 2's `test_appliers.py` included). Without `pwsh` the 17 PowerShell cases are skipped (the `s` marks); with `HDC_PWSH` set they run too and the run ends `FAILED (failures=1)`:
```
sssssssssssssssss.............................................F.....................................................................................
FAIL: test_the_committed_patch_set_is_this_build (tests.test_cave.RealGameDllCaveTests.test_the_committed_patch_set_is_this_build)
AssertionError: 'cef4cbead156825613f84a5a5fd1b26efc33a2e575a9b337c7d3a87a01c9cb63' != '1f6869d4eb5c495fc9018cfb565b711ac82df1471b85898bb2926cebcf3548ea'
 : classic-creation.json is out of date: run build.py again
Ran 148 tests in 4.928s
FAILED (failures=1, skipped=17)
```

- [ ] **Step 5: Regenerate `classic-creation.json` and check that it is reproducible**

Run:
```bash
python3 client/patches/build.py --client ~/Games/HearthDAoC/client --world-db ~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db --server-src source/server --out client/patches/classic-creation.json
again="$(mktemp)" && python3 client/patches/build.py --client ~/Games/HearthDAoC/client --world-db ~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db --server-src source/server --out "$again" && cmp client/patches/classic-creation.json "$again" && echo identical; rm -f "$again"
python3 -c 'import json; p = json.load(open("client/patches/classic-creation.json")); [print(f["path"], f["before"][:16], f["after"][:16], [(o["op"], o.get("offset")) for o in f["ops"]]) for f in p["files"]]'
```

Expected:
```
Wrote client/patches/classic-creation.json: game.dll, pregame/character_customize_stats.xml
Wrote /tmp/tmp.XXXXXXXXXX: game.dll, pregame/character_customize_stats.xml
identical
game.dll 67dcf68a37b95a93 1f6869d4eb5c495f [('replace', 350), ('replace', 373), ('replace', 425), ('replace', 912), ('replace', 1681491), ('replace', 1687730), ('replace', 1688948), ('replace', 1769554), ('append', None)]
pregame/character_customize_stats.xml 08e6f7c3b4fdb546 1d6be73e3e21f007 [('text-replace', None)]
```

In decimal: 350 = `0x15E`, 373 = `0x175`, 425 = `0x1A9`, 912 = `0x390`, 1769554 = `0x1B0052` (see the background). The JSON holds only hashes, the changed header and code bytes, and our own cave as hex. Don't paste its content anywhere else.

- [ ] **Step 6: Run the whole suite with the real files**

Run: `HDC_CLIENT_FILES=~/Games/HearthDAoC/client HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -m unittest discover -s client/patches/tests -t client/patches -v 2>&1 | grep -E 'RealGameDllCaveTests|ops_are|^Ran|^OK|FAIL|ERROR'`

Expected (148 tests, everything passes; nothing is skipped when HDC_PWSH points to PowerShell, as here, otherwise the 17 PowerShell cases are skipped and the run ends `OK (skipped=17)`):
```
test_game_dll_ops_are_the_headers_the_stat_flow_the_hook_and_the_cave (tests.test_build.RealBuildTests.test_game_dll_ops_are_the_headers_the_stat_flow_the_hook_and_the_cave) ... ok
test_nothing_else_changes (tests.test_cave.RealGameDllCaveTests.test_nothing_else_changes) ... ok
test_the_checksum_is_valid (tests.test_cave.RealGameDllCaveTests.test_the_checksum_is_valid) ... ok
test_the_committed_patch_set_is_this_build (tests.test_cave.RealGameDllCaveTests.test_the_committed_patch_set_is_this_build) ... ok
test_the_hook_calls_the_cave_entry (tests.test_cave.RealGameDllCaveTests.test_the_hook_calls_the_cave_entry) ... ok
test_the_section_holds_the_cave (tests.test_cave.RealGameDllCaveTests.test_the_section_holds_the_cave) ... ok
test_the_section_table_is_valid (tests.test_cave.RealGameDllCaveTests.test_the_section_table_is_valid) ... ok
Ran 148 tests in 60.055s
OK
```

Then run without real files: `python3 -m unittest discover -s client/patches/tests -t client/patches`

Expected:
```
----------------------------------------------------------------------
Ran 148 tests in 3.553s

OK (skipped=36)    (19 when HDC_PWSH is set)
```

- [ ] **Step 7: Apply the patch set to a scratch copy and look at the hook (never the real client folder)**

Run:
```bash
scratch="$(mktemp -d)" && mkdir "$scratch/pregame" && cp ~/Games/HearthDAoC/client/game.dll "$scratch/" && cp ~/Games/HearthDAoC/client/pregame/character_customize_stats.xml "$scratch/pregame/"
python3 client/patches/apply_patches.py --client "$scratch"; echo "exit $?"
python3 client/patches/apply_patches.py --client "$scratch" --check; echo "exit $?"
objdump -h "$scratch/game.dll" | grep -A1 '\.hdcc'
objdump -d -M intel --start-address=0x5b0050 --stop-address=0x5b0056 "$scratch/game.dll" | tail -n 2
objdump -d -M intel --start-address=0x248b000 --stop-address=0x248b012 "$scratch/game.dll" | tail -n 9
rm -rf "$scratch"
```

Expected (`objdump` comes from binutils. The last three commands are a human check of what `test_cave.py` already asserts, and can be skipped if binutils is missing):
```
Patched: game.dll (original saved as game.dll.hearthdaoc-orig)
Patched: pregame/character_customize_stats.xml (original saved as pregame/character_customize_stats.xml.hearthdaoc-orig)
exit 0
game.dll: patched
pregame/character_customize_stats.xml: patched
exit 0
  8 .hdcc         00000ccb  0248b000  0248b000  005b0000  2**2
                  CONTENTS, ALLOC, LOAD, READONLY, CODE
  5b0050:	53                   	push   ebx
  5b0051:	e8 aa af ed 01       	call   0x248b000
 248b000:	55                   	push   ebp
 248b001:	89 e5                	mov    ebp,esp
 248b003:	83 ec 50             	sub    esp,0x50
 248b006:	53                   	push   ebx
 248b007:	56                   	push   esi
 248b008:	57                   	push   edi
 248b009:	8b 5d 08             	mov    ebx,DWORD PTR [ebp+0x8]
 248b00c:	53                   	push   ebx
 248b00d:	e8 7a 93 12 fe       	call   0x5b438c
```

- [ ] **Step 8: Commit**

```bash
git add client/patches/src/baseclass.asm client/patches/build.py client/patches/tests/test_cave.py client/patches/tests/test_build.py client/patches/classic-creation.json
git commit -m "feat(client): base classes on the creation screen through a game.dll code cave

src/baseclass.asm runs after the client registers its classes (hook at
0x5B0051): it hides the 47 final classes and the races after Shrouded
Isles (availability masks), then registers the 15 classic base classes
with descriptions, highlighted stats and races from the server data.
build.py generates its data include, assembles it with nasm, appends it
as a new .hdcc section and writes the hook; classic-creation.json is
regenerated. --server-src is now source/server (the GameServer/ parent).

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

Expected: `5 files changed, 554 insertions(+), 21 deletions(-)`

---

### Task 6: Splash: re-lettered "HEARTH DAoC" art, splash.mpk build and the patch-set entry

The client shows 8 loading images from `pregame/splash.mpk` (an MPK archive whose internal name is `splash.mpk`, holding `splash1.tga` to `splash8.tga`). OfflineDAoC's images say "OFFLINE DAoC". This task re-letters that art to "HEARTH DAoC", keeps "CLASSIC + SHROUDED ISLES" and the rest of the art byte for byte, builds our own `splash.mpk` from it, and adds a `file` entry to the patch set so the appliers install it. Credit for the art (OfflineDAoC) goes into the docs in Task 8, not here.

Facts this task relies on (all checked on 2026-10-06):
- OfflineDAoC 0.34's `pregame/splash.mpk` has SHA-256 `f24460d2b064b86527b1800940d6d80c26b67ca3531d91b87c3b7a4d38455a9e`. The repo already holds a byte-identical copy (OfflineDAoC's own art, not an EA file): `source/tools/OfflineDaoc.Launcher/Assets/offline-daoc-client-splash.mpk`. The tests and the re-letter script read that copy, so they never need the client.
- Its 8 entries are identical TGAs of 3145772 bytes: an 18-byte header `000002000000000000000000000400032008` (type 2 = uncompressed true colour, 1024x768, 32 bits, descriptor 8 = 8 alpha bits + bottom-left origin), BGRA rows from the bottom up, and a 26-byte TGA 2.0 footer (8 zero bytes + `TRUEVISION-XFILE.` + NUL). This is exactly what upstream's `source/tools/OfflineDaoc.Mpk/Build-ClientSplash.ps1` writes. The client renders RLE TGAs black.
- Upstream's MPK tool: `dotnet OfflineDaoc.Mpk.dll pack <input-folder> <archive> splash.mpk` packs every file of the folder (names lower-cased, sorted). Each entry carries the packing time, so two builds of the same images differ byte-wise; the patch set's `"after": "source"` ("equals the bundled file") is made for that.
- Font: Cinzel (SIL Open Font License) from Google Fonts' GitHub, pinned to commit `45071f07c63e863a539442ef3562b71ab1f147a6` (the commit that last changed `ofl/cinzel/Cinzel[wght].ttf`), SHA-256 `f4d83d34d1f6c741193e4acf4b3dff9531e5a67b6aa65228d00a7db72a4e0f34`. It is a variable font; the script picks the named instance "Bold". In Cinzel, lower case draws as small capitals, so "DAoC" keeps the original's small "o".

Rules for this task: `reletter_splash.py` is the only file that uses Pillow (dev-time; its output `splash.png` is committed). Everything else is Python 3.10+ standard library, plus .NET for upstream's MPK tool. The tests must pass without Pillow and without .NET (they skip the MPK-tool tests unless `HDC_MPK_TOOL` points to `OfflineDaoc.Mpk.dll`, and the real-file tests unless `HDC_CLIENT_FILES` and `HDC_TEST_WORLD` are set, as in Task 3). The built `client/patches/splash.mpk` is never committed.

**Files:**
- Create: `client/patches/mpk.py` (MPK reader, standard library)
- Create: `client/patches/splash_entry.py` (the patch-set entry and the checks on our splash.mpk)
- Create: `client/patches/branding/build_splash_mpk.py` (PNG to 8 TGAs, packed by the MPK tool)
- Create: `client/patches/branding/reletter_splash.py` (Pillow, dev-time)
- Create: `client/patches/branding/splash.png` (generated by `reletter_splash.py`, committed)
- Modify: `client/patches/build.py` (import, `build_patchset`, `--splash-mpk`)
- Modify: `client/patches/classic-creation.json` (regenerated: adds the `pregame/splash.mpk` entry)
- Modify: `.gitignore` (`/client/patches/splash.mpk`)
- Test: `client/patches/tests/test_splash.py` (the package `client/patches/tests/__init__.py` exists since Task 1)

**Interfaces:**
- Consumes:
  - `patchset.sha256_file(path) -> str` (Task 1)
  - `build.build_patchset(client_dir: str, world_db: str, server_src: str, splash_mpk: str | None = None) -> dict` and `build.main(argv=None) -> int` (Task 3, extended by Task 5). Task 3 left this guard in `build_patchset` for this task: `if splash_mpk is not None: raise ValueError("this build.py cannot add the splash entry yet")`.
  - Upstream `source/tools/OfflineDaoc.Mpk` (`dotnet OfflineDaoc.Mpk.dll pack <indir> <archive> [internal-name]`).
- Produces:
  - `client/patches/mpk.py`: `read_mpk(path: str) -> tuple[str, list[tuple[str, bytes]]]` (internal name, entries in directory order); `class MpkError(ValueError)`.
  - `client/patches/splash_entry.py`: `splash_entry(client_dir, splash_mpk_path) -> dict` returning `{"path": "pregame/splash.mpk", "before": <sha256 of the client's file>, "after": "source", "ops": [{"op": "file", "source": "splash.mpk"}]}` (keys in that order; raises `SplashError` if the client's splash isn't OfflineDAoC 0.34's or the MPK fails the checks); `check_splash_mpk(path) -> None`; `check_splash_tga(name, data) -> None`; `class SplashError(ValueError)`; constants `SPLASH_PATH = "pregame/splash.mpk"`, `SPLASH_SOURCE = "splash.mpk"`, `SPLASH_NAME = "splash.mpk"`, `SPLASH_ENTRIES`, `STOCK_SPLASH_SHA256`, `WIDTH, HEIGHT = 1024, 768`, `TGA_HEADER`, `TGA_FOOTER`, `TGA_SIZE`.
  - `client/patches/branding/build_splash_mpk.py`: CLI `build_splash_mpk.py --mpk-tool <OfflineDaoc.Mpk.dll> [--png client/patches/branding/splash.png] [--out client/patches/splash.mpk] [--dotnet dotnet]`, exit 0 written / 1 failed; functions `read_png(path) -> tuple[int, int, bytes]` (RGBA, top row first), `tga_bytes(width, height, rgba) -> bytes`, `build(png, mpk_tool, out, dotnet="dotnet") -> None`.
  - `client/patches/branding/reletter_splash.py`: CLI `reletter_splash.py [--source <OfflineDAoC 0.34 splash.mpk>] [--out client/patches/branding/splash.png] [--font <local copy of the pinned Cinzel>]`; `--source` defaults to the repo's byte-identical copy and is refused unless its SHA-256 is `STOCK_SPLASH_SHA256`. Constants `TITLE_BOX = (180, 77, 886, 182)` (every changed pixel lies inside), `FONT_URL`, `FONT_SHA256`.
  - `client/patches/build.py`: `--splash-mpk FILE` (optional); with it, `build_patchset(..., splash_mpk)` appends the splash entry last. Without it the output is unchanged.

- [ ] **Step 1: Write the failing test (MPK reader and splash entry)**

Create `client/patches/tests/test_splash.py`:

```python
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -p test_splash.py`

Expected:
```
    import mpk  # noqa: E402
    ^^^^^^^^^^
ModuleNotFoundError: No module named 'mpk'
...
Ran 1 test in 0.000s

FAILED (errors=1)
```

- [ ] **Step 3: Write minimal implementation (MPK reader and splash entry)**

Create `client/patches/mpk.py`:

```python
"""Read a DAoC MPK archive (the format of pregame/splash.mpk), standard library only.

Layout, as written by upstream's OfflineDaoc.Mpk tool (source/server/CoreBase/MPK):
- "MPAK", then the byte 2.
- 16 bytes, each XORed with its index: CRC-32 of the compressed directory, the compressed
  directory size, the compressed name size and the number of files (4 little-endian uint32s).
- The archive's internal name, zlib-compressed. The client binds the archive by this name.
- The directory, zlib-compressed: one 284-byte entry per file (name[256], timestamp, 4,
  offset, uncompressed size, offset in the data area, compressed size, CRC-32 of the
  compressed bytes).
- The data area: each file's zlib stream.
"""
import struct
import zlib

MAGIC = b"MPAK\x02"
ENTRY_SIZE = 0x11C


class MpkError(ValueError):
    """The file isn't a readable MPK archive; the message says why."""


def _inflate(data, where):
    d = zlib.decompressobj()
    try:
        out = d.decompress(data) + d.flush()
    except zlib.error as e:
        raise MpkError(f"{where}: {e}") from None
    if not d.eof:
        raise MpkError(f"{where}: truncated zlib stream")
    return out


def read_mpk(path):
    """Return (internal name, [(entry name, data), ...] in directory order)."""
    with open(path, "rb") as f:
        data = f.read()
    if data[:5] != MAGIC:
        raise MpkError(f"{path}: not an MPK archive")
    head = bytes(b ^ i for i, b in enumerate(data[5:21]))
    if len(head) != 16:
        raise MpkError(f"{path}: truncated header")
    crc, size_dir, size_name, count = struct.unpack("<IIII", head)
    pos = 21
    name = _inflate(data[pos:pos + size_name], "archive name").split(b"\0")[0].decode("utf-8")
    pos += size_name
    packed_dir = data[pos:pos + size_dir]
    if zlib.crc32(packed_dir) != crc:
        raise MpkError(f"{path}: directory CRC mismatch")
    directory = _inflate(packed_dir, "directory")
    pos += size_dir
    if len(directory) != count * ENTRY_SIZE:
        raise MpkError(f"{path}: directory holds {len(directory)} bytes for {count} files")
    entries = []
    for k in range(count):
        raw = directory[k * ENTRY_SIZE:(k + 1) * ENTRY_SIZE]
        entry_name = raw[:256].split(b"\0")[0].decode("utf-8")
        _, _, _, size, data_offset, packed_size, entry_crc = struct.unpack("<IiIIIII", raw[256:])
        packed = data[pos + data_offset:pos + data_offset + packed_size]
        if len(packed) != packed_size or zlib.crc32(packed) != entry_crc:
            raise MpkError(f"{path}: {entry_name}: CRC mismatch")
        content = _inflate(packed, entry_name)
        if len(content) != size:
            raise MpkError(f"{path}: {entry_name}: {len(content)} bytes, directory says {size}")
        entries.append((entry_name, content))
    return name, entries
```

Create `client/patches/splash_entry.py`:

```python
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -p test_splash.py -v`

Expected (the 8-image fixtures make it take a second or two):
```
test_corrupt_entry_is_refused (tests.test_splash.MpkReaderTests.test_corrupt_entry_is_refused) ... ok
...
test_wrong_internal_name_is_refused (tests.test_splash.SplashEntryTests.test_wrong_internal_name_is_refused) ... ok

----------------------------------------------------------------------
Ran 12 tests in 1.648s

OK
```

- [ ] **Step 5: Write the failing test (TGA, PNG and the MPK build)**

Append to the end of `client/patches/tests/test_splash.py`, after two blank lines:

```python
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
```

- [ ] **Step 6: Run test to verify it fails**

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -p test_splash.py`

Expected:
```
ModuleNotFoundError: No module named 'build_splash_mpk'
...
Ran 1 test in 0.000s

FAILED (errors=1)
```

- [ ] **Step 7: Write minimal implementation (PNG reader, TGA writer, MPK build)**

Create `client/patches/branding/build_splash_mpk.py` (then `chmod +x client/patches/branding/build_splash_mpk.py`):

```python
#!/usr/bin/env python3
"""Build splash.mpk, the client's 8 loading images, from branding/splash.png.

Like upstream's Build-ClientSplash.ps1: one 1024x768 uncompressed 32-bit TGA with a
bottom-left origin, copied to splash1.tga ... splash8.tga and packed with upstream's
OfflineDaoc.Mpk tool under the internal name "splash.mpk". Standard library only, plus .NET
for the MPK tool. The packed archive is read back and checked before it is written.

    python3 client/patches/branding/build_splash_mpk.py \\
        --mpk-tool source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll

The output (default client/patches/splash.mpk) is a build product: never commit it. MPK
entries carry a timestamp, so two builds differ; the patch set's "after": "source" allows it.
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
```

- [ ] **Step 8: Run the tests to verify they pass, then with the MPK tool**

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -p test_splash.py`

Expected (the 3 MPK-tool tests skip without `HDC_MPK_TOOL`):
```
Ran 21 tests in 1.967s

OK (skipped=3)
```

Build upstream's MPK tool (needs the .NET 10 SDK; `bin/` and `obj/` are git-ignored) and run the tool tests:

Run: `dotnet build source/tools/OfflineDaoc.Mpk/OfflineDaoc.Mpk.csproj -c Release`

Expected:
```
  OfflineDaoc.Mpk -> .../source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll
Build succeeded.
    0 Error(s)
```

Run: `HDC_MPK_TOOL=source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll python3 -m unittest discover -s client/patches/tests -t client/patches -p test_splash.py -k MpkTool -v`

Expected:
```
test_cli (tests.test_splash.MpkToolTests.test_cli) ... ok
test_packs_eight_identical_images_under_the_client_name (tests.test_splash.MpkToolTests.test_packs_eight_identical_images_under_the_client_name) ... ok
test_tool_failure_is_reported (tests.test_splash.MpkToolTests.test_tool_failure_is_reported) ... ok

----------------------------------------------------------------------
Ran 3 tests in 6.164s

OK
```

`test_packs_eight_identical_images_under_the_client_name` packs a PNG made from OfflineDAoC's own pixels and checks that every packed entry equals OfflineDAoC's `splash1.tga` byte for byte, so the TGA format, row order and footer match upstream's exactly.

- [ ] **Step 9: Write the failing test (the re-lettered splash.png)**

Append to the end of `client/patches/tests/test_splash.py`, after two blank lines:

```python
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
```

- [ ] **Step 10: Run test to verify it fails**

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -p test_splash.py`

Expected:
```
ModuleNotFoundError: No module named 'reletter_splash'
...
Ran 1 test in 0.000s

FAILED (errors=1)
```

- [ ] **Step 11: Write the re-letter script and generate splash.png**

Create `client/patches/branding/reletter_splash.py` (then `chmod +x client/patches/branding/reletter_splash.py`):

```python
#!/usr/bin/env python3
"""Re-letter OfflineDAoC's loading splash to "HEARTH DAoC" (dev-time only; needs Pillow).

The art is OfflineDAoC's: splash1.tga of its 0.34 pregame/splash.mpk (the same bytes as
source/tools/OfflineDaoc.Launcher/Assets/offline-daoc-client-splash.mpk). Only the title
between the two gold rules changes: the old letters are painted over with sky (a smooth fill
from the pixels around them plus cloud texture sampled from the sky above), and "HEARTH DAoC"
is drawn in Cinzel Bold (SIL Open Font License, downloaded from a pinned Google Fonts commit
and checked) as gold with a dark outline and shadow. "CLASSIC + SHROUDED ISLES", the rules
and the rest of the art stay byte for byte.

    pip install Pillow
    python3 client/patches/branding/reletter_splash.py

writes client/patches/branding/splash.png (committed). build_splash_mpk.py turns it into the
client's splash.mpk.
"""
import argparse
import hashlib
import os
import random
import sys
import tempfile
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
PATCHES = os.path.dirname(HERE)
REPO = os.path.dirname(os.path.dirname(PATCHES))
sys.path.insert(0, PATCHES)
# Pillow is imported inside the functions, so the tests can read the constants without it.
from mpk import read_mpk  # noqa: E402
from splash_entry import HEIGHT, STOCK_SPLASH_SHA256, WIDTH  # noqa: E402

FONT_URL = ("https://raw.githubusercontent.com/google/fonts/"
            "45071f07c63e863a539442ef3562b71ab1f147a6/ofl/cinzel/Cinzel%5Bwght%5D.ttf")
FONT_SHA256 = "f4d83d34d1f6c741193e4acf4b3dff9531e5a67b6aa65228d00a7db72a4e0f34"
DEFAULT_SOURCE = os.path.join(REPO, "source", "tools", "OfflineDaoc.Launcher", "Assets",
                              "offline-daoc-client-splash.mpk")
DEFAULT_OUT = os.path.join(HERE, "splash.png")

# Every changed pixel lies in TITLE_BOX (left, top, right, bottom; right and bottom
# exclusive). Above it is the upper gold rule (rows 74-75), below it the lower one (row 182).
TITLE_BOX = (180, 77, 886, 182)
OLD_LETTERS = (204, 79, 864, 172)  # the "OFFLINE DAoC" letters
FILL_BOX = (148, 45, 918, 214)  # the art the smooth fill is computed from
GROW = 6  # pixels the old letters' mask grows by, to take in their outline and shadow
SKY_ROWS = (2, 73)  # sky above the upper rule: the cloud texture's source
ORNAMENT, ORNAMENT_SHIFT = (480, 546), 70  # the ornament's columns, replaced from further left
# A soft dark plate behind the new title: opacity, inset from TITLE_BOX (x, y), edge blur.
PLATE, PLATE_INSET, PLATE_BLUR = 0.4, (26, 12), 9

# The new title: (text, big capitals). Cinzel draws lower case as small capitals.
TITLE = [("H", True), ("EARTH", False), (" ", False), ("D", True), ("A", False), ("o", False),
         ("C", True)]
BIG_SIZE, SMALL_SIZE, CONDENSE = 106, 86, 0.9
BASELINE = 164  # the old title's baseline
CENTRE_X = 533  # the old title's and the subtitle's centre
GOLD = [(0.0, (250, 222, 150)), (0.3, (220, 170, 78)), (0.55, (160, 108, 38)),
        (0.75, (204, 150, 66)), (1.0, (110, 70, 24))]  # top to baseline


def fetch_font(folder):
    """Download the pinned Cinzel font into folder and return its path."""
    with urllib.request.urlopen(FONT_URL, timeout=60) as r:
        data = r.read()
    digest = hashlib.sha256(data).hexdigest()
    if digest != FONT_SHA256:
        raise SystemExit(f"Cinzel download has SHA-256 {digest}, expected {FONT_SHA256}")
    path = os.path.join(folder, "Cinzel-wght.ttf")
    with open(path, "wb") as f:
        f.write(data)
    return path


def load_art(source):
    """Return splash1.tga of OfflineDAoC 0.34's splash.mpk as an RGB image."""
    from PIL import Image
    with open(source, "rb") as f:
        digest = hashlib.sha256(f.read()).hexdigest()
    if digest != STOCK_SPLASH_SHA256:
        raise SystemExit(f"{source} has SHA-256 {digest}; this script re-letters OfflineDAoC "
                         f"0.34's splash.mpk ({STOCK_SPLASH_SHA256}) only")
    data = dict(read_mpk(source)[1])["splash1.tga"]
    pixels = data[18:18 + WIDTH * HEIGHT * 4]
    return Image.frombytes("RGBA", (WIDTH, HEIGHT), pixels, "raw", "BGRA", 0, -1).convert("RGB")


def gold_pixels(art, box):
    """Mask (as big as the art) of the gold pixels inside box."""
    from PIL import Image
    mask = Image.new("L", art.size, 0)
    src, dst = art.load(), mask.load()
    left, top, right, bottom = box
    for y in range(top, bottom):
        for x in range(left, right):
            r, g, b = src[x, y]
            # Bright, warm and yellow (green at least 0.69 of red): the sunset clouds are redder.
            if r * 299 + g * 587 + b * 114 > 60000 and r > b + 20 and g * 100 >= r * 69:
                dst[x, y] = 255
    return mask


def old_letter_mask(art):
    """Mask of the old title: its gold pixels, grown over outline and shadow, in TITLE_BOX."""
    from PIL import Image, ImageChops, ImageFilter
    grown = gold_pixels(art, OLD_LETTERS).filter(ImageFilter.MaxFilter(GROW * 2 + 1))
    box = Image.new("L", art.size, 0)
    box.paste(255, TITLE_BOX)
    return ImageChops.multiply(grown.filter(ImageFilter.GaussianBlur(1)), box)


def smooth_fill(img, hole):
    """Fill the hole (255) from the pixels around it, coarse to fine (a membrane fill)."""
    from PIL import Image, ImageFilter
    w, h = img.size
    cur = None
    for scale, rounds in ((16, 300), (8, 150), (4, 80), (2, 40), (1, 20)):
        size = (w // scale, h // scale)
        known = img.resize(size, Image.BOX)
        cells = hole.resize(size, Image.BOX).point(lambda v: 255 if v else 0)
        guess = known if cur is None else cur.resize(size, Image.BILINEAR)
        cur = Image.composite(guess, known, cells)
        for _ in range(rounds):
            cur = Image.composite(cur.filter(ImageFilter.BoxBlur(1)), known, cells)
    return cur


def sky_texture(art):
    """Cloud texture for TITLE_BOX as a high-pass image around 128.

    The sky above the upper rule is mirrored down over the title rows again and again, so the
    copies meet without seams; the ornament's columns come from further left.
    """
    from PIL import Image, ImageChops, ImageFilter, ImageOps
    left, top, right, bottom = TITLE_BOX
    band = art.crop((left, SKY_ROWS[0], right, SKY_ROWS[1]))
    o_left, o_right = ORNAMENT[0] - left, ORNAMENT[1] - left
    band.paste(band.crop((o_left - ORNAMENT_SHIFT, 0, o_right - ORNAMENT_SHIFT, band.height)),
               (o_left, 0))
    tiles = [ImageOps.flip(band), band]
    src = Image.new("RGB", (right - left, bottom - top))
    for k, y in enumerate(range(0, bottom - top, band.height)):
        src.paste(tiles[k % 2], (0, y))
    return ImageChops.subtract(src, src.filter(ImageFilter.GaussianBlur(3)), 1.0, 128)


def cover_old_title(art):
    """Return the art with the old title painted over with sky."""
    from PIL import Image, ImageChops, ImageFilter
    hole = old_letter_mask(art)
    # The rules, the ornament and the subtitle mustn't tint the fill: leave them out of it.
    decorations = gold_pixels(art, FILL_BOX).filter(ImageFilter.MaxFilter(5))
    unknown = ImageChops.lighter(hole, decorations)
    sky = art.copy()
    sky.paste(smooth_fill(art.crop(FILL_BOX), unknown.crop(FILL_BOX)), FILL_BOX[:2])
    sky.paste(ImageChops.add(sky.crop(TITLE_BOX), sky_texture(art), 1.0, -128), TITLE_BOX[:2])
    covered = Image.composite(sky, art, hole)
    plate = Image.new("L", art.size, 0)
    left, top, right, bottom = TITLE_BOX
    dx, dy = PLATE_INSET
    plate.paste(round(255 * PLATE), (left + dx, top + dy, right - dx, bottom - dy))
    plate = plate.filter(ImageFilter.GaussianBlur(PLATE_BLUR))
    return Image.composite(Image.new("RGB", art.size, (6, 5, 8)), covered, plate)


def title_mask(font_path, size):
    """Return (coverage mask of the new title, top row of its big capitals)."""
    from PIL import Image, ImageDraw, ImageFont

    def font(px):
        f = ImageFont.truetype(font_path, px)
        f.set_variation_by_name("Bold")
        return f

    big, small = font(BIG_SIZE), font(SMALL_SIZE)
    glyphs = [(ch, big if is_big else small) for text, is_big in TITLE for ch in text]
    width = sum(f.getlength(ch) for ch, f in glyphs)
    wide = Image.new("L", (round(size[0] / CONDENSE), size[1]), 0)
    draw = ImageDraw.Draw(wide)
    x = CENTRE_X / CONDENSE - width / 2
    for ch, f in glyphs:
        draw.text((x, BASELINE), ch, font=f, fill=255, anchor="ls")
        x += f.getlength(ch)
    top = BASELINE + big.getbbox("H", anchor="ls")[1]
    return wide.resize(size, Image.LANCZOS), top


def gold(mask, top):
    """Gold letters: a metal gradient, worn blotches, grain and a bevel lit from top left."""
    from PIL import Image, ImageChops, ImageFilter
    w, h = mask.size
    column = Image.new("RGB", (1, h))
    for y in range(h):
        t = min(1.0, max(0.0, (y - top) / float(BASELINE - top)))
        for (t0, c0), (t1, c1) in zip(GOLD, GOLD[1:]):
            if t <= t1:
                k = (t - t0) / (t1 - t0)
                column.putpixel((0, y), tuple(round(a + (b - a) * k) for a, b in zip(c0, c1)))
                break
    fill = column.resize((w, h))
    rng = random.Random(1)
    blots = Image.frombytes("L", (w // 6, h // 6), rng.randbytes((w // 6) * (h // 6)))
    blots = blots.resize((w, h), Image.BICUBIC).filter(ImageFilter.GaussianBlur(2))
    blots = blots.point(lambda v: 255 if v > 100 else 165 + v * 9 // 10)
    grain = Image.frombytes("L", (w, h), rng.randbytes(w * h)).point(lambda v: 220 + v // 8)
    fill = ImageChops.multiply(fill, Image.merge("RGB", [blots] * 3))
    fill = ImageChops.multiply(fill, Image.merge("RGB", [grain] * 3))
    soft = mask.filter(ImageFilter.GaussianBlur(1.5))
    moved = ImageChops.offset(soft, 2, 2)
    lit = ImageChops.subtract(soft, moved).point(lambda v: min(255, v * 2))
    shade = ImageChops.subtract(moved, soft).point(lambda v: min(255, v * 2))
    fill = ImageChops.screen(fill, Image.merge("RGB", [lit, lit, lit.point(lambda v: v * 3 // 4)]))
    return ImageChops.multiply(fill, Image.merge("RGB", [ImageChops.invert(shade)] * 3))


def reletter(art, font_path):
    """Return the re-lettered splash as a new RGB image; only TITLE_BOX changes."""
    from PIL import Image, ImageChops, ImageFilter
    work = cover_old_title(art)
    mask, cap_top = title_mask(font_path, art.size)

    def paint(colour, alpha):
        return Image.composite(Image.new("RGB", art.size, colour), work, alpha)

    halo = mask.filter(ImageFilter.MaxFilter(7)).filter(ImageFilter.GaussianBlur(5))
    work = paint((10, 8, 12), halo.point(lambda v: v * 55 // 100))
    shadow = ImageChops.offset(mask.filter(ImageFilter.GaussianBlur(2.5)), 2, 3)
    work = paint((0, 0, 0), shadow.point(lambda v: v * 85 // 100))
    outline = mask.filter(ImageFilter.MaxFilter(3)).filter(ImageFilter.GaussianBlur(0.6))
    work = paint((52, 32, 12), outline)
    work = Image.composite(gold(mask, cap_top), work, mask)
    # Blend into the art over the box's outer 2 pixels, so its edge leaves no seam.
    left, top, right, bottom = TITLE_BOX
    edge = Image.new("L", art.size, 0)
    edge.paste(255, (left + 2, top + 2, right - 2, bottom - 2))
    box = Image.new("L", art.size, 0)
    box.paste(255, TITLE_BOX)
    edge = ImageChops.multiply(edge.filter(ImageFilter.GaussianBlur(1)), box)
    return Image.composite(work, art, edge)


def main(argv=None):
    ap = argparse.ArgumentParser(description="Re-letter OfflineDAoC's splash to HEARTH DAoC.")
    ap.add_argument("--source", default=DEFAULT_SOURCE,
                    help="OfflineDAoC 0.34's pregame/splash.mpk (default: the repo's copy)")
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--font", help="a local copy of the pinned Cinzel font (default: download)")
    args = ap.parse_args(argv)
    art = load_art(args.source)
    with tempfile.TemporaryDirectory(prefix="hdc-font-") as tmp:
        font_path = args.font or fetch_font(tmp)
        with open(font_path, "rb") as f:
            if hashlib.sha256(f.read()).hexdigest() != FONT_SHA256:
                raise SystemExit(f"{font_path} isn't the pinned Cinzel font")
        result = reletter(art, font_path)
    result.save(args.out, optimize=True)
    print(f"Wrote {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

What it does, so the numbers make sense: the old letters are found as bright yellow-gold pixels inside `OLD_LETTERS` (the sunset clouds are redder, hence green >= 0.69 x red) and grown by 6 pixels to take in their outline and shadow. That hole is filled coarse to fine from the surrounding sky (with the rules, the ornament and the subtitle left out of the fill so their gold can't tint it), cloud texture is added from the sky above the upper rule (mirrored down; the ornament's columns taken from 70 pixels further left), and a soft dark plate sits behind the new title. "HEARTH DAoC" is drawn with big capitals for H, D and C and small capitals for the rest (sizes 106 and 86, condensed to 90 % width so it covers the old title's footprint, baseline 164, centred on x = 533 like the old title and the subtitle), with a dark halo, a drop shadow, a dark-brown outline and a gold fill (gradient, worn blotches, grain, bevel lit from the top left; the noise is seeded, so a rerun gives the same image). The result is blended into the art over the box's outer 2 pixels, so no seam shows, and nothing outside `TITLE_BOX` changes.

Run (Pillow is needed only here; `pip install Pillow` if `python3 -c "import PIL"` fails; the script downloads the pinned font into a temporary folder):

Run: `python3 client/patches/branding/reletter_splash.py`

Expected:
```
Wrote .../client/patches/branding/splash.png
```

With Pillow 10.2.0 (FreeType 2.13.2) the file was 1066015 bytes, SHA-256 `1d205416ce6f0dc9f148161aefe986c86a078333af91ca2c9006bc47e28827c1`; other Pillow or FreeType versions may differ in a few pixels, which is fine.

Now look at the image (open `client/patches/branding/splash.png` in an image viewer, at 100 % and zoomed to 200-300 % on the title). Check: the title reads "HEARTH DAoC" in gold with a dark outline and shadow, the same width and height as the old title, centred over "CLASSIC + SHROUDED ISLES"; no trace of the old "OFFLINE" letters; no hard edge or line around the title area (left over the sunset clouds, right against the trees, under the upper rule); the rules, the ornament, the subtitle, the castle and the trees are unchanged. If something looks wrong, adjust the constants at the top of the script, rerun and look again before going on.

- [ ] **Step 12: Run the tests to verify they pass**

Run: `python3 -m unittest discover -s client/patches/tests -t client/patches -p test_splash.py -v`

Expected:
```
...
test_font_is_pinned (tests.test_splash.SplashPngTests.test_font_is_pinned) ... ok
test_only_the_title_box_changes (tests.test_splash.SplashPngTests.test_only_the_title_box_changes) ... ok
test_size (tests.test_splash.SplashPngTests.test_size) ... ok
...
Ran 24 tests in 2.957s

OK (skipped=3)
```

Then check the tests need neither Pillow nor anything else outside the standard library (`-S` hides site-packages):

Run: `python3 -S -m unittest discover -s client/patches/tests -t client/patches -p test_splash.py`

Expected:
```
Ran 24 tests in 3.012s

OK (skipped=3)
```

- [ ] **Step 13: Ignore the built archive and commit**

Append these two lines to the end of `.gitignore`:

```
# Built by client/patches/branding/build_splash_mpk.py: bundles carry it, the repo does not.
/client/patches/splash.mpk
```

Run: `touch client/patches/splash.mpk && git check-ignore -v client/patches/splash.mpk && rm client/patches/splash.mpk`

Expected (the line number depends on what earlier tasks added to `.gitignore`):
```
.gitignore:40:/client/patches/splash.mpk	client/patches/splash.mpk
```

```bash
git add .gitignore client/patches/mpk.py client/patches/splash_entry.py \
  client/patches/branding/build_splash_mpk.py client/patches/branding/reletter_splash.py \
  client/patches/branding/splash.png client/patches/tests/test_splash.py
git commit -m "feat(client): re-lettered HEARTH DAoC splash and splash.mpk builder

OfflineDAoC's splash art with the title re-lettered in Cinzel Bold
(SIL OFL, pinned download) by branding/reletter_splash.py (Pillow,
dev-time only). branding/build_splash_mpk.py writes the 8 uncompressed
bottom-left 1024x768 TGAs and packs them with upstream's OfflineDaoc.Mpk
tool under the internal name splash.mpk; mpk.py reads MPKs with the
standard library and splash_entry.py checks the archive the client gets.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 14: Write the failing test (the splash entry in the generator)**

Append to the end of `client/patches/tests/test_splash.py`, after two blank lines:

```python
# --- The splash entry in the generator: build.py --splash-mpk (real client files) ---

import json  # noqa: E402
import subprocess  # noqa: E402

import build  # noqa: E402

CLIENT = os.environ.get("HDC_CLIENT_FILES")
WORLD_DB = os.environ.get("HDC_TEST_WORLD")
SERVER_SRC = os.path.join(REPO, "source", "server")


@unittest.skipUnless(CLIENT and WORLD_DB, "set HDC_CLIENT_FILES to an OfflineDAoC 0.34 classic "
                     "client folder and HDC_TEST_WORLD to a clean classic world database")
class BuildSplashTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.built = os.path.join(cls.tmp.name, "splash.mpk")
        write_mpk(cls.built, "splash.mpk", [(name, BLACK_TGA) for name in NAMES])
        cls.without = build.build_patchset(CLIENT, WORLD_DB, SERVER_SRC)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_the_splash_entry_comes_last(self):
        patchset = build.build_patchset(CLIENT, WORLD_DB, SERVER_SRC, self.built)
        self.assertEqual(patchset["files"][:-1], self.without["files"])
        self.assertEqual(patchset["files"][-1], splash_entry.splash_entry(CLIENT, self.built))

    def test_cli_flag(self):
        out = os.path.join(self.tmp.name, "classic-creation.json")
        subprocess.run([sys.executable, os.path.join(PATCHES, "build.py"), "--client", CLIENT,
                        "--world-db", WORLD_DB, "--server-src", SERVER_SRC,
                        "--splash-mpk", self.built, "--out", out], check=True, capture_output=True)
        with open(out, encoding="ascii") as f:
            written = json.load(f)
        paths = [entry["path"] for entry in self.without["files"]]
        self.assertEqual([entry["path"] for entry in written["files"]],
                         paths + ["pregame/splash.mpk"])

    def test_a_bad_splash_mpk_is_refused(self):
        bad = os.path.join(self.tmp.name, "bad.mpk")
        write_mpk(bad, "splash.mpk", [(name, BLACK_TGA) for name in NAMES[:7]])
        with self.assertRaises(splash_entry.SplashError):
            build.build_patchset(CLIENT, WORLD_DB, SERVER_SRC, bad)
```

- [ ] **Step 15: Run test to verify it fails**

These tests need the real client files and the clean world, like Task 3's real-file tests.

Run: `HDC_CLIENT_FILES=~/Games/HearthDAoC/client HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -m unittest discover -s client/patches/tests -t client/patches -p test_splash.py -k BuildSplash`

Expected:
```
ERROR: test_a_bad_splash_mpk_is_refused (tests.test_splash.BuildSplashTests.test_a_bad_splash_mpk_is_refused)
ValueError: this build.py cannot add the splash entry yet
ERROR: test_cli_flag (tests.test_splash.BuildSplashTests.test_cli_flag)
subprocess.CalledProcessError: Command '[... '--splash-mpk', '/tmp/.../splash.mpk', '--out', '/tmp/.../classic-creation.json']' returned non-zero exit status 2.
ERROR: test_the_splash_entry_comes_last (tests.test_splash.BuildSplashTests.test_the_splash_entry_comes_last)
ValueError: this build.py cannot add the splash entry yet
...
Ran 3 tests in 0.329s

FAILED (errors=3)
```

- [ ] **Step 16: Add the splash entry to build.py**

Make these edits in `client/patches/build.py`. The "before" text is Task 3's; if Task 5 changed lines around them, keep Task 5's changes and make the same edits.

1. Below `import pe`, add the import:

```python
import pe
from splash_entry import splash_entry
```

2. In `build_patchset`, delete Task 3's guard:

```python
    if splash_mpk is not None:
        raise ValueError("this build.py cannot add the splash entry yet")
```

and add the entry right before the `return` (it stays the last entry):

```python
    if splash_mpk is not None:
        files.append(splash_entry(client_dir, splash_mpk))
    return {"format": FORMAT, "name": NAME, "client": CLIENT, "files": files}
```

3. In the docstring of `build_patchset`, replace these two lines (as Task 5 left them):

```
    disabled_classes in `world_db`. `splash_mpk` is the slot for the splash entry; this version
    only accepts None."""
```

with these three:

```
    disabled_classes in `world_db`.
    `splash_mpk`, when given, is the built splash.mpk (branding/build_splash_mpk.py): it is
    checked, and pregame/splash.mpk gets a "file" entry, last, that installs it."""
```

4. In `main()`, add the option before `--out` and pass it on:

```python
    parser.add_argument("--splash-mpk", help="the built splash.mpk (branding/build_splash_mpk.py); "
                        "adds the pregame/splash.mpk entry")
    parser.add_argument("--out", required=True, help="the patch set to write")
```

```python
        patchset = build_patchset(args.client, args.world_db, args.server_src, args.splash_mpk)
```

5. In the module docstring's usage example, replace the line `      --out client/patches/classic-creation.json` with:

```
      --splash-mpk client/patches/splash.mpk --out client/patches/classic-creation.json
```

`SplashError` and `MpkError` are `ValueError`s, so `main()`'s existing `except (OSError, ValueError)` reports them with exit 1.

- [ ] **Step 17: Run the tests to verify they pass**

Run: `HDC_CLIENT_FILES=~/Games/HearthDAoC/client HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -m unittest discover -s client/patches/tests -t client/patches -p test_splash.py -k BuildSplash -v`

Expected:
```
test_a_bad_splash_mpk_is_refused (tests.test_splash.BuildSplashTests.test_a_bad_splash_mpk_is_refused) ... ok
test_cli_flag (tests.test_splash.BuildSplashTests.test_cli_flag) ... ok
test_the_splash_entry_comes_last (tests.test_splash.BuildSplashTests.test_the_splash_entry_comes_last) ... ok

----------------------------------------------------------------------
Ran 3 tests in 1.007s

OK
```

Task 3's tests (built without `--splash-mpk`) must be unaffected:

Run: `HDC_CLIENT_FILES=~/Games/HearthDAoC/client HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -m unittest discover -s client/patches/tests -t client/patches -p test_build.py`

Expected:
```
Ran 13 tests in 0.512s

OK
```
(The count is Task 3's; Task 5 may have added more. All must pass.)

- [ ] **Step 18: Build splash.mpk and regenerate the patch set**

Run: `python3 client/patches/branding/build_splash_mpk.py --mpk-tool source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll`

Expected (about 13.9 MB; `git status` doesn't list it):
```
Wrote .../client/patches/splash.mpk (8 x 1024x768 TGA)
```

Run: `python3 client/patches/build.py --client ~/Games/HearthDAoC/client --world-db ~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db --server-src source/server --splash-mpk client/patches/splash.mpk --out client/patches/classic-creation.json && git diff client/patches/classic-creation.json`

Expected: the only change is one new entry at the end of `"files"`:
```
Wrote client/patches/classic-creation.json: game.dll, pregame/character_customize_stats.xml, pregame/splash.mpk
...
+  },
+  {
+   "path": "pregame/splash.mpk",
+   "before": "f24460d2b064b86527b1800940d6d80c26b67ca3531d91b87c3b7a4d38455a9e",
+   "after": "source",
+   "ops": [
+    {
+     "op": "file",
+     "source": "splash.mpk"
+    }
+   ]
   }
  ]
 }
```

The entry doesn't depend on the bytes of the built `splash.mpk` (only on the client's stock file and the fixed name), so rebuilding the archive never changes `classic-creation.json`.

- [ ] **Step 19: Check the whole patch set on a scratch copy of the client**

The applier's default bundle folder is the patch set's folder, `client/patches/`, which now holds the built `splash.mpk`. Only the three patched files are copied; the real client folder is never touched.

Run:
```bash
T=$(mktemp -d) && mkdir -p "$T/pregame" && cp ~/Games/HearthDAoC/client/game.dll "$T/" \
  && cp ~/Games/HearthDAoC/client/pregame/character_customize_stats.xml ~/Games/HearthDAoC/client/pregame/splash.mpk "$T/pregame/" \
  && python3 client/patches/apply_patches.py --client "$T"; echo "exit $?"; \
  cmp "$T/pregame/splash.mpk" client/patches/splash.mpk && echo "splash installed"; \
  python3 client/patches/apply_patches.py --client "$T" --restore; echo "exit $?"; \
  sha256sum "$T/pregame/splash.mpk"; rm -rf "$T"
```

Expected (the wording of the applier's lines is Task 1's):
```
Patched: game.dll (original saved as game.dll.hearthdaoc-orig)
Patched: pregame/character_customize_stats.xml (original saved as pregame/character_customize_stats.xml.hearthdaoc-orig)
Patched: pregame/splash.mpk (original saved as pregame/splash.mpk.hearthdaoc-orig)
exit 0
splash installed
Restored: game.dll
Restored: pregame/character_customize_stats.xml
Restored: pregame/splash.mpk
exit 0
f24460d2b064b86527b1800940d6d80c26b67ca3531d91b87c3b7a4d38455a9e  /tmp/tmp.../pregame/splash.mpk
```

Then run the whole client/patches suite with everything available:

Run: `HDC_MPK_TOOL=source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll HDC_CLIENT_FILES=~/Games/HearthDAoC/client HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -m unittest discover -s client/patches/tests -t client/patches`

Expected: `Ran 175 tests ... OK (skipped=17)` with HDC_CLIENT_FILES, HDC_TEST_WORLD and HDC_MPK_TOOL set (the 17 skipped are the PowerShell cases; `OK` with nothing skipped when HDC_PWSH points to PowerShell too).

- [ ] **Step 20: Commit**

```bash
git add client/patches/build.py client/patches/classic-creation.json client/patches/tests/test_splash.py
git commit -m "feat(client): patch set replaces pregame/splash.mpk with the HEARTH DAoC splash

build.py --splash-mpk checks the built splash.mpk and adds a \"file\"
entry for pregame/splash.mpk (before: OfflineDAoC 0.34's splash,
after: the bundled file). classic-creation.json is regenerated with it.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

`client/patches/splash.mpk` stays untracked (ignored); `git status --short` must be empty after this commit.

**Notes for later tasks** (from this task's results):
- Task 7 (bundles): the client bundle's `patches/` must carry `splash.mpk` built by `build_splash_mpk.py`, because `classic-creation.json` now has the `pregame/splash.mpk` entry and the appliers read `<bundle>/splash.mpk`. `branding/` (with `splash.png` and the scripts) need not ship.
- Task 8 (CI): the real-file client folder must include `pregame/splash.mpk` (the splash entry hashes it), and any check that rebuilds `classic-creation.json` must pass `--splash-mpk` with a built `splash.mpk` (dotnet) to get the committed output. The release job builds the archive with `dotnet build source/tools/OfflineDaoc.Mpk/OfflineDaoc.Mpk.csproj -c Release` and then `build_splash_mpk.py --mpk-tool .../OfflineDaoc.Mpk.dll`. The docs credit OfflineDAoC for the splash art and name Cinzel (SIL Open Font License).

---

### Task 7: Client delivery: setup.sh applies the patch set; the bundles carry it, splash.mpk and the Windows applier

Players get the classic creation screen through the client bundle. On Linux, `setup.sh` runs the bundled `patches/apply_patches.py` on the client it has just built. On Windows, the bundle's `windows/` folder carries `patch-client.bat`, `patch-client.ps1` and `windows/patches/` (the same patch set and `splash.mpk`). `deploy/build_bundles.sh` builds `splash.mpk` from `client/patches/branding/splash.png` with upstream's MPK tool, so it needs `HDC_MPK_TOOL`. Only our patch data, our appliers and our `splash.mpk` are bundled, never an EA file.

Facts this task relies on (checked on 2026-10-06):
- `apply_patches.py` (Task 1) exits 0 when it patched or found the files already patched, 3 when it refused a client file and changed nothing (unknown or missing file, for example the b edition's `game.dll`), and 2 or 1 on errors. Its default patch set is `classic-creation.json` next to it, and its default bundle folder is the patch set's folder. So `patches/` must hold `apply_patches.py`, `patchset.py`, `classic-creation.json` and `splash.mpk` together. It sets `sys.dont_write_bytecode`, so it leaves no `__pycache__` in a bundle.
- Since Task 6, `classic-creation.json` has a `pregame/splash.mpk` entry with `"after": "source"`. Its status check reads `<bundle>/splash.mpk` as soon as the client has a `pregame/splash.mpk`. So a bundle without `splash.mpk` fails with exit 2 ("bundled file missing"), and so does `setup.sh` run from a checkout where `client/patches/splash.mpk` (git-ignored) hasn't been built.
- `deploy/tests/hdc_integration.sh` (run by CI's test job, which has no MPK tool) calls `build_bundles.sh` only for the deploy bundle. It gets a `--deploy-only` option, which needs no MPK tool.
- Since PR #70 (merging is releasing), the repository names no release. `deploy/build_bundles.sh` stamps the tag into the bundle's `.env.example`, where `./hdc update` reads it, and stops if that line is missing (its `grep -qxF "HEARTHDAOC_TAG=$tag"` guard). `deploy/tests/test_build_bundles.py` checks the stamped line exactly (`env.splitlines()`) and has `test_repo_leaves_the_tag_to_the_bundle` (the repository's `.env.example` has an empty `HEARTHDAOC_TAG=`, and `HANDOFF.md` names no release). This task edits both files around those lines and keeps them.
- CI runs the client tests with `python3 -m unittest discover -s client/tests -t client` and the deploy tests with `-s deploy/tests -t deploy`. Both test packages are named `tests`, like `client/patches/tests`, so these tests import the modules in `client/patches` directly (`splash_entry`, `mpk`, `build_splash_mpk`) and never `tests.test_*` of another folder.

**Before Step 1**, check the prerequisites. All commands run from the repository root, on branch `sub2-classic-creation`.

Run: `test ! -e .github/workflows/release-pr.yml && grep -x 'HEARTHDAOC_TAG=' deploy/.env.example && grep -cF 'grep -qxF "HEARTHDAOC_TAG=$tag"' deploy/build_bundles.sh`

Expected:
```
HEARTHDAOC_TAG=
1
```
This shows the branch contains main with PR #70: no release PR workflow, an empty `HEARTHDAOC_TAG=` in `deploy/.env.example`, and `build_bundles.sh` stamping the tag and guarding it. (It also has PR #62's relative-output fix, which came before.) If the command fails or prints anything else, merge main first, as in the Prerequisites: `git fetch origin && git merge origin/main`.

Run: `ls client/patches/{apply_patches.py,patchset.py,classic-creation.json,mpk.py,splash_entry.py} client/patches/branding/{build_splash_mpk.py,splash.png} client/windows/patch-client.{ps1,bat} && grep -c '"path"' client/patches/classic-creation.json`

Expected: the 9 paths are listed without an error, then `3` (game.dll, the pregame XML and the splash entry from Tasks 1, 2, 3, 5 and 6).

**Files:**
- Modify: `client/linux/setup.sh` (find `patches/apply_patches.py`, run it after the client files are fetched)
- Test: `client/tests/test_setup.py` (replace the whole file: adds a bundle-layout fixture with its own patch set)
- Modify: `deploy/build_bundles.sh` (three edits: `HDC_MPK_TOOL`, `splash.mpk`, `patches/`, Windows files, `--deploy-only`; main's tag stamp and its guard stay)
- Modify: `deploy/tests/hdc_integration.sh` (one line: `--deploy-only`)
- Test: `deploy/tests/test_build_bundles.py` (two edits: a fake `dotnet` packs the MPK, so no .NET is needed; main's `test_repo_leaves_the_tag_to_the_bundle` and exact-line check stay)

**Interfaces:**
- Consumes:
  - Task 1: `client/patches/apply_patches.py --client DIR` (default patch set `classic-creation.json` next to the script, default bundle the patch set's folder; exit 0 done, 3 refused and nothing changed, 2 bad patch set or bundle, 1 I/O error; stdout `Patched: <path> (original saved as <path>.hearthdaoc-orig)`, `Not patched: <path> is not the file this HearthDAoC release supports (...)`; stderr `Error: <reason>`), and `client/patches/patchset.py`.
  - Tasks 3, 5 and 6: the committed `client/patches/classic-creation.json`.
  - Task 6: `client/patches/branding/build_splash_mpk.py --mpk-tool <OfflineDaoc.Mpk.dll> --out <file>` (runs `dotnet <tool> pack <folder> <archive> splash.mpk` and checks the result), `build_splash_mpk.read_png(path) -> (width, height, rgba)`, `build_splash_mpk.tga_bytes(width, height, rgba) -> bytes`, `mpk.read_mpk(path) -> (name, [(entry, data)])`, `splash_entry.check_splash_mpk(path) -> None`, `client/patches/branding/splash.png`.
  - Task 2: `client/windows/patch-client.ps1` and `client/windows/patch-client.bat` (bundled byte for byte, CRLF kept; their `-PatchSet` default is `patches\classic-creation.json` next to the script, and the bundle folder defaults to the patch set's folder).
  - Existing: `tools/linux/tests/release_fixture.py` (`build(directory, files=None) -> (lock, files)`, `DEFAULT_FILES`, `RangeServer(directory)` with `.lock(lock)`).
- Produces:
  - `client/linux/setup.sh`: finds `patches/apply_patches.py` next to itself (client bundle), or else `client/patches/apply_patches.py` of the checkout. If neither exists, it stops before copying anything: stderr `Missing patches/apply_patches.py next to setup.sh; download the full client bundle.`, exit 1. After `odaoc_fetch.py` it prints `Applying HearthDAoC's client patches (classic character creation, loading splash) ...` and runs `python3 <apply_patches.py> --client "$DEST/client"`:
    - exit 0: setup goes on.
    - exit 3: stderr `Warning: the client was set up without HearthDAoC's patches (see the message above).`, setup goes on and exits 0.
    - any other exit N: stderr `Patching the client failed (apply_patches.py exit N, see the message above).`, exit 1, `play.sh` is not written.
  - `deploy/build_bundles.sh <tag> <output dir> [--deploy-only]`, with env `HDC_MPK_TOOL` = path to `OfflineDaoc.Mpk.dll` (relative paths work). Without `--deploy-only`, a missing `HDC_MPK_TOOL` stops it before anything is written: stderr `build_bundles.sh: set HDC_MPK_TOOL to upstream's OfflineDaoc.Mpk.dll (it packs the client's splash.mpk):` plus two lines on how to build and pass it, exit 2. A third argument other than `--deploy-only` prints `usage: deploy/build_bundles.sh <tag> <output dir> [--deploy-only]` and exits 2. `--deploy-only` writes only `hearthdaoc-deploy-<tag>.tar.gz` and prints `Built <out>/hearthdaoc-deploy-<tag>.tar.gz`.
  - Client bundle `hearthdaoc-client-<tag>.zip`, exactly these files under `hearthdaoc-client-<tag>/`: `README.md`, `setup.sh`, `play.sh.in`, `odaoc_fetch.py`, `upstream.lock`, `patches/classic-creation.json`, `patches/apply_patches.py`, `patches/patchset.py`, `patches/splash.mpk`, `windows/connect-hearthdaoc.bat`, `windows/patch-client.bat`, `windows/patch-client.ps1`, `windows/patches/classic-creation.json`, `windows/patches/splash.mpk`. `setup.sh` and `patches/apply_patches.py` keep mode 755.

- [ ] **Step 1: Write the failing test (setup.sh applies the patch set)**

Replace `client/tests/test_setup.py` with the following. The two existing tests stay as they were, with the base client moved into `make_base()` and the run into `run_setup()`. The new tests are:
- `test_repository_patch_set_refuses_a_foreign_client_and_setup_still_succeeds`: from a checkout, the real `client/patches` patch set refuses the fixture's random `game.dll`, so setup warns and succeeds.
- `BundlePatchTests`: an unpacked bundle layout with a small fixture patch set (`replace` + `append` on `game.dll`, `file` on `pregame/splash.mpk`) that matches the fixture release.
- `test_running_setup_again_ends_patched_with_one_verified_backup_per_file`: running `setup.sh` again on the client it patched exits 0 and ends patched, with exactly one `.hearthdaoc-orig` per patched file, each the original (setup rebuilds the client from the base with `rsync --delete`, so the applier backs up the fresh originals again).

```python
import hashlib
import json
import os
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(REPO, "tools", "linux", "tests"))
import release_fixture as fx  # noqa: E402

SETUP = os.path.join(REPO, "client", "linux", "setup.sh")
RELEASE_DLL = "editions/0.34-no-custom-class/runtime/client-opendaoc/app/game.dll"
RELEASE_SPLASH = "runtime/client-opendaoc/app/pregame/splash.mpk"
SPLASH = b"stock splash"
SPLASH_NEW = b"HEARTH DAoC splash"
UNKNOWN_DLL = ("Not patched: game.dll is not the file this HearthDAoC release supports (for example the "
               "0.34b edition or a newer upstream client). The client still works with the standard "
               "creation screen.\n")
WARNING = "Warning: the client was set up without HearthDAoC's patches (see the message above).\n"


def sha(data):
    return hashlib.sha256(data).hexdigest()


def read(path):
    with open(path, "rb") as f:
        return f.read()


def make_base(directory, files):
    """A fake 1.127 base client folder, as the player's OpenDAoC install would be."""
    base = os.path.join(directory, "base")
    os.makedirs(base)
    for name, data in (("connect.exe", files["runtime/client-opendaoc/app/connect.exe"]),
                       ("game1127.dll", b"scaling"), ("game.dll", b"stock"), ("paths.dat", b"[paths]\r\nsettings=Atlas1")):
        with open(os.path.join(base, name), "wb") as f:
            f.write(data)
    return base


def run_setup(script, directory, base, lock, *extra):
    """Run setup.sh with lock (part URLs pointing at a RangeServer); return (dest, result)."""
    lock_path = os.path.join(directory, "upstream.lock")
    with open(lock_path, "w") as f:
        json.dump(lock, f)
    dest = os.path.join(directory, "dest")
    args = ["bash", script, "--server", "192.168.1.64:10301", "--edition", "classic",
            "--base-client", base, "--dest", dest, "--lock", lock_path, *extra]
    return dest, subprocess.run(args, capture_output=True, text=True)


class SetupTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = self.tmp.name
        self.lock, self.files = fx.build(self.dir)
        self.base = make_base(self.dir, self.files)

    def tearDown(self):
        self.tmp.cleanup()

    def run_setup(self, srv, *extra):
        return run_setup(SETUP, self.dir, self.base, srv.lock(self.lock), *extra)

    def test_setup_builds_verified_client_and_play_script(self):
        with fx.RangeServer(self.dir) as srv:
            dest, r = self.run_setup(srv)
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        with open(os.path.join(dest, "client", "game.dll"), "rb") as f:
            self.assertEqual(f.read(), self.files["editions/0.34-no-custom-class/runtime/client-opendaoc/app/game.dll"])
        with open(os.path.join(dest, "client", "paths.dat"), "rb") as f:
            self.assertEqual(f.read(), self.files["runtime/client-opendaoc/app/paths.dat"])
        with open(os.path.join(self.base, "game.dll"), "rb") as f:
            self.assertEqual(f.read(), b"stock")  # base client untouched
        play = os.path.join(dest, "play.sh")
        self.assertTrue(os.stat(play).st_mode & stat.S_IXUSR)
        with open(play, encoding="utf-8") as f:
            text = f.read()
        self.assertIn('SERVER="${HEARTHDAOC_SERVER:-192.168.1.64:10301}"', text)
        self.assertNotIn("@SERVER@", text)
        self.assertEqual(subprocess.run(["bash", "-n", play]).returncode, 0)

    def test_repository_patch_set_refuses_a_foreign_client_and_setup_still_succeeds(self):
        # From a checkout, setup.sh uses client/patches/. The fixture's game.dll isn't OfflineDAoC
        # 0.34 classic's, so the applier refuses it (exit 3): a warning, and the client is unchanged.
        with fx.RangeServer(self.dir) as srv:
            dest, r = self.run_setup(srv)
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        self.assertIn(UNKNOWN_DLL, r.stdout)
        self.assertIn(WARNING, r.stderr)
        self.assertEqual(read(os.path.join(dest, "client", "game.dll")), self.files[RELEASE_DLL])
        self.assertEqual([n for n in os.listdir(os.path.join(dest, "client")) if n.endswith(".hearthdaoc-orig")], [])

    def test_rejects_bad_arguments(self):
        with fx.RangeServer(self.dir) as srv:
            for bad in (["--server", "no-port"], ["--edition", "gold"]):
                args = ["bash", SETUP, "--server", "h:1", "--edition", "classic", "--base-client", self.base, *bad]
                r = subprocess.run(args, capture_output=True, text=True)
                self.assertEqual(r.returncode, 2, bad)
            not_client = os.path.join(self.dir, "empty")
            os.makedirs(not_client)
            r = subprocess.run(["bash", SETUP, "--server", "h:1", "--edition", "classic", "--base-client", not_client],
                               capture_output=True, text=True)
            self.assertEqual(r.returncode, 2)
            self.assertIn("doesn't look like a 1.127 client", r.stderr)


class BundlePatchTests(unittest.TestCase):
    """setup.sh from an unpacked client bundle runs patches/apply_patches.py on the new client."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.dir = self.tmp.name
        files = dict(fx.DEFAULT_FILES)
        files[RELEASE_SPLASH] = SPLASH
        self.lock, self.files = fx.build(self.dir, files)
        self.base = make_base(self.dir, self.files)
        self.dll = self.files[RELEASE_DLL]
        self.dll_patched = b"HDC!" + self.dll[4:] + b"cave"
        # The bundle's layout (deploy/build_bundles.sh), two folders down so that setup.sh's
        # repository fallback (../../client/patches) finds nothing.
        self.bundle = os.path.join(self.dir, "download", "hearthdaoc-client-vtest")
        self.patches = os.path.join(self.bundle, "patches")
        os.makedirs(self.patches)
        for src in ("client/linux/setup.sh", "client/linux/play.sh.in", "tools/linux/odaoc_fetch.py"):
            shutil.copy(os.path.join(REPO, src), self.bundle)
        for name in ("apply_patches.py", "patchset.py"):
            shutil.copy(os.path.join(REPO, "client", "patches", name), self.patches)
        with open(os.path.join(self.patches, "splash.mpk"), "wb") as f:
            f.write(SPLASH_NEW)
        self.write_patchset(sha(self.dll))

    def write_patchset(self, dll_before):
        data = {
            "format": 1,
            "name": "classic-creation",
            "client": "test fixture",
            "files": [
                {"path": "game.dll", "before": dll_before, "after": sha(self.dll_patched),
                 "ops": [{"op": "replace", "offset": 0, "from": self.dll[:4].hex(), "to": b"HDC!".hex()},
                         {"op": "append", "data": b"cave".hex()}]},
                {"path": "pregame/splash.mpk", "before": sha(SPLASH), "after": "source",
                 "ops": [{"op": "file", "source": "splash.mpk"}]},
            ],
        }
        with open(os.path.join(self.patches, "classic-creation.json"), "w", encoding="utf-8") as f:
            json.dump(data, f, indent=1)

    def setup_sh(self):
        with fx.RangeServer(self.dir) as srv:
            return run_setup(os.path.join(self.bundle, "setup.sh"), self.dir, self.base, srv.lock(self.lock))

    def test_setup_patches_the_new_client(self):
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        client = os.path.join(dest, "client")
        self.assertIn("Patched: game.dll (original saved as game.dll.hearthdaoc-orig)\n"
                      "Patched: pregame/splash.mpk (original saved as pregame/splash.mpk.hearthdaoc-orig)\n",
                      r.stdout)
        self.assertNotIn("Warning", r.stderr)
        self.assertEqual(read(os.path.join(client, "game.dll")), self.dll_patched)
        self.assertEqual(read(os.path.join(client, "game.dll.hearthdaoc-orig")), self.dll)
        self.assertEqual(read(os.path.join(client, "pregame", "splash.mpk")), SPLASH_NEW)
        self.assertTrue(os.path.isfile(os.path.join(dest, "play.sh")))
        self.assertFalse(os.path.exists(os.path.join(self.patches, "__pycache__")))

    def test_running_setup_again_ends_patched_with_one_verified_backup_per_file(self):
        self.assertEqual(self.setup_sh()[1].returncode, 0)
        dest, r = self.setup_sh()  # on the client the first run patched
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        self.assertNotIn("Warning", r.stderr)
        client = os.path.join(dest, "client")
        self.assertEqual(read(os.path.join(client, "game.dll")), self.dll_patched)
        self.assertEqual(read(os.path.join(client, "pregame", "splash.mpk")), SPLASH_NEW)
        ours = sorted(os.path.relpath(os.path.join(folder, name), client).replace(os.sep, "/")
                      for folder, _dirs, names in os.walk(client) for name in names if "hearthdaoc" in name)
        self.assertEqual(ours, ["game.dll.hearthdaoc-orig", "pregame/splash.mpk.hearthdaoc-orig"])  # nothing else
        self.assertEqual(sha(read(os.path.join(client, "game.dll.hearthdaoc-orig"))), sha(self.dll))
        self.assertEqual(sha(read(os.path.join(client, "pregame", "splash.mpk.hearthdaoc-orig"))), sha(SPLASH))

    def test_a_refused_patch_set_warns_and_setup_still_succeeds(self):
        self.write_patchset(sha(b"the game.dll of another edition"))
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        self.assertIn(UNKNOWN_DLL, r.stdout)
        self.assertIn(WARNING, r.stderr)
        client = os.path.join(dest, "client")
        self.assertEqual(read(os.path.join(client, "game.dll")), self.dll)
        self.assertEqual(read(os.path.join(client, "pregame", "splash.mpk")), SPLASH)
        self.assertFalse(os.path.exists(os.path.join(client, "game.dll.hearthdaoc-orig")))
        self.assertTrue(os.path.isfile(os.path.join(dest, "play.sh")))

    def test_a_failed_patch_fails_setup(self):
        os.remove(os.path.join(self.patches, "splash.mpk"))  # the patch set can't be applied
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 1, r.stderr + r.stdout)
        self.assertIn("Error: bundled file missing: " + os.path.join(self.patches, "splash.mpk"), r.stderr)
        self.assertIn("Patching the client failed (apply_patches.py exit 2, see the message above).", r.stderr)
        self.assertEqual(read(os.path.join(dest, "client", "game.dll")), self.dll)
        self.assertFalse(os.path.exists(os.path.join(dest, "play.sh")))

    def test_a_bundle_without_patches_is_refused_before_anything_is_copied(self):
        shutil.rmtree(self.patches)
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 1)
        self.assertIn("Missing patches/apply_patches.py next to setup.sh; download the full client bundle.", r.stderr)
        self.assertFalse(os.path.exists(dest))


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python3 -m unittest discover -s client/tests -t client -p test_setup.py`

Expected: 6 failures, because `setup.sh` never runs the applier yet:
```
FFFFF.F.
FAIL: test_a_bundle_without_patches_is_refused_before_anything_is_copied (tests.test_setup.BundlePatchTests...)
AssertionError: 0 != 1
FAIL: test_a_failed_patch_fails_setup (tests.test_setup.BundlePatchTests...)
AssertionError: 0 != 1 : Copying your base client (read only) to /tmp/tmp.../dest/client ...
FAIL: test_a_refused_patch_set_warns_and_setup_still_succeeds (tests.test_setup.BundlePatchTests...)
AssertionError: 'Not patched: game.dll is not the file this HearthDAoC release supports (for example the 0.34b edition or a newer upstream client). The client still works with the standard creation screen.\n' not found in 'Copying ...
FAIL: test_running_setup_again_ends_patched_with_one_verified_backup_per_file (tests.test_setup.BundlePatchTests...)
AssertionError: b'\xfd?\xeb<\x92P\xb7\x97J\x9bR\x8bicc!\xa4[7207 chars]\x1f' != b'HDC!\x92P\xb7\x97J\x9bR\x8bicc!\xa4a\xb5^[7205 chars]cave'
FAIL: test_setup_patches_the_new_client (tests.test_setup.BundlePatchTests...)
AssertionError: 'Patched: game.dll (original saved as game.dll.hearthdaoc-orig)\nPatched: pregame/splash.mpk (original saved as pregame/splash.mpk.hearthdaoc-orig)\n' not found in 'Copying your base client ...
FAIL: test_repository_patch_set_refuses_a_foreign_client_and_setup_still_succeeds (tests.test_setup.SetupTests...)
AssertionError: 'Not patched: game.dll is not the file ...' not found in 'Copying ...
----------------------------------------------------------------------
Ran 8 tests in 5.838s

FAILED (failures=6)
```

- [ ] **Step 3: Write minimal implementation (setup.sh)**

In `client/linux/setup.sh`, make two edits.

1. Find the applier with the other bundled files, so an incomplete bundle stops before anything is copied. Replace:

```bash
TEMPLATE="$(find_file play.sh.in client/linux/play.sh.in)"
for t in python3 rsync; do command -v "$t" >/dev/null || { echo "Please install $t first." >&2; exit 1; }; done
```

with:

```bash
TEMPLATE="$(find_file play.sh.in client/linux/play.sh.in)"
PATCHER="$(find_file patches/apply_patches.py client/patches/apply_patches.py)"
for t in python3 rsync; do command -v "$t" >/dev/null || { echo "Please install $t first." >&2; exit 1; }; done
```

2. Run it right after the client files are fetched and verified, before `play.sh` is written. Replace:

```bash
python3 "$FETCH" --lock "$LOCK" client --edition "$EDITION" --client-dir "$DEST/client"
sed -e "s|@SERVER@|$SERVER|g" -e "s|@EDITION@|$EDITION|g" "$TEMPLATE" > "$DEST/play.sh"
```

with:

```bash
python3 "$FETCH" --lock "$LOCK" client --edition "$EDITION" --client-dir "$DEST/client"
# Classic character creation and the HearthDAoC splash (client/patches). Exit 3 means the applier
# refused a client file it doesn't know (e.g. the b edition) and changed nothing: the client works.
echo "Applying HearthDAoC's client patches (classic character creation, loading splash) ..."
rc=0; python3 "$PATCHER" --client "$DEST/client" || rc=$?
if [[ $rc -eq 3 ]]; then
    echo "Warning: the client was set up without HearthDAoC's patches (see the message above)." >&2
elif [[ $rc -ne 0 ]]; then
    echo "Patching the client failed (apply_patches.py exit $rc, see the message above)." >&2; exit 1
fi
sed -e "s|@SERVER@|$SERVER|g" -e "s|@EDITION@|$EDITION|g" "$TEMPLATE" > "$DEST/play.sh"
```

`find_file` already prints `Missing <name> next to setup.sh; download the full client bundle.` and exits 1. Because it runs inside `$(...)` in an assignment, `set -e` stops the script.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `python3 -m unittest discover -s client/tests -t client -v`

Expected:
```
...
test_a_bundle_without_patches_is_refused_before_anything_is_copied (tests.test_setup.BundlePatchTests.test_a_bundle_without_patches_is_refused_before_anything_is_copied) ... ok
test_a_failed_patch_fails_setup (tests.test_setup.BundlePatchTests.test_a_failed_patch_fails_setup) ... ok
test_a_refused_patch_set_warns_and_setup_still_succeeds (tests.test_setup.BundlePatchTests.test_a_refused_patch_set_warns_and_setup_still_succeeds) ... ok
test_running_setup_again_ends_patched_with_one_verified_backup_per_file (tests.test_setup.BundlePatchTests.test_running_setup_again_ends_patched_with_one_verified_backup_per_file) ... ok
test_setup_patches_the_new_client (tests.test_setup.BundlePatchTests.test_setup_patches_the_new_client) ... ok
test_rejects_bad_arguments (tests.test_setup.SetupTests.test_rejects_bad_arguments) ... ok
test_repository_patch_set_refuses_a_foreign_client_and_setup_still_succeeds (tests.test_setup.SetupTests.test_repository_patch_set_refuses_a_foreign_client_and_setup_still_succeeds) ... ok
test_setup_builds_verified_client_and_play_script (tests.test_setup.SetupTests.test_setup_builds_verified_client_and_play_script) ... ok

----------------------------------------------------------------------
Ran 11 tests in 5.873s

OK
```

Run: `bash -n client/linux/setup.sh && git status --short --untracked-files=all`

Expected (no `__pycache__` or other leftovers):
```
 M client/linux/setup.sh
 M client/tests/test_setup.py
```

- [ ] **Step 5: Commit**

```bash
git add client/linux/setup.sh client/tests/test_setup.py
git commit -m "feat(client): setup.sh applies the client patch set

After the verified client files are in place, setup.sh runs
patches/apply_patches.py (client/patches/ in a checkout) on the new
client. A refused client file (exit 3, e.g. the b edition) is a warning:
nothing was changed and the client works with the standard creation
screen. Any other failure stops setup.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 6: Write the failing test (the bundles carry the patches)**

Make two edits in `deploy/tests/test_build_bundles.py`. A fake `dotnet` on `PATH` stands in for upstream's MPK tool: it logs its arguments and packs the folder the way `OfflineDaoc.Mpk` does. So the real `build_splash_mpk.py` runs, from the real `splash.png`, without .NET. `test_with_the_real_mpk_tool` runs only when `HDC_MPK_TOOL` points to the real tool and `dotnet` is installed. Main's `import re`, its exact-line check of the stamped tag (`env.splitlines()`) and its `test_repo_leaves_the_tag_to_the_bundle` stay; the second edit stops just before that test.

1. The imports and module constants. Replace:

```python
import os
import re
import subprocess
import tarfile
import tempfile
import unittest
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.abspath(os.path.join(HERE, "..", "build_bundles.sh"))
```

with:

```python
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unittest
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.abspath(os.path.join(HERE, "..", "build_bundles.sh"))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
PATCHES = os.path.join(REPO, "client", "patches")
sys.path.insert(0, PATCHES)
sys.path.insert(0, os.path.join(PATCHES, "branding"))
import build_splash_mpk  # noqa: E402
import mpk  # noqa: E402
import splash_entry  # noqa: E402

TAG = "v0.34b-hearth.99"
CLIENT_FILES = {
    "README.md", "setup.sh", "play.sh.in", "odaoc_fetch.py", "upstream.lock",
    "patches/classic-creation.json", "patches/apply_patches.py", "patches/patchset.py", "patches/splash.mpk",
    "windows/connect-hearthdaoc.bat", "windows/patch-client.bat", "windows/patch-client.ps1",
    "windows/patches/classic-creation.json", "windows/patches/splash.mpk",
}
FROM_REPO = {  # bundled byte for byte (the .bat and .ps1 keep their CRLF line ends)
    "patches/classic-creation.json": "client/patches/classic-creation.json",
    "patches/apply_patches.py": "client/patches/apply_patches.py",
    "patches/patchset.py": "client/patches/patchset.py",
    "windows/patch-client.bat": "client/windows/patch-client.bat",
    "windows/patch-client.ps1": "client/windows/patch-client.ps1",
    "windows/patches/classic-creation.json": "client/patches/classic-creation.json",
}
EXECUTABLE = {"setup.sh", "patches/apply_patches.py"}

# Stands in for `dotnet OfflineDaoc.Mpk.dll pack <folder> <archive> <name>`: logs its arguments
# and packs the folder the way upstream's MPK tool does (the layout client/patches/mpk.py reads).
FAKE_DOTNET = r'''
import json, os, struct, sys, zlib
with open(os.environ["FAKE_DOTNET_LOG"], "w") as f:
    json.dump(sys.argv[1:], f)
_tool, _pack, folder, archive, name = sys.argv[1:]
names = sorted(os.listdir(folder))
blobs, directory, offset, data_offset = [], b"", 0, 0
for entry in names:
    with open(os.path.join(folder, entry), "rb") as f:
        data = f.read()
    blob = zlib.compress(data, 1)
    directory += entry.encode().ljust(256, b"\0") + struct.pack(
        "<IiIIIII", 0, 4, offset, len(data), data_offset, len(blob), zlib.crc32(blob))
    offset, data_offset = offset + len(data), data_offset + len(blob)
    blobs.append(blob)
packed_dir, packed_name = zlib.compress(directory), zlib.compress(name.encode())
head = struct.pack("<IIII", zlib.crc32(packed_dir), len(packed_dir), len(packed_name), len(names))
with open(archive, "wb") as f:
    f.write(b"MPAK\x02" + bytes(b ^ i for i, b in enumerate(head)) + packed_name + packed_dir + b"".join(blobs))
'''


def read(path):
    with open(path, "rb") as f:
        return f.read()
```

2. The tests before `test_repo_leaves_the_tag_to_the_bundle`. Replace:

```python
class BuildBundlesTests(unittest.TestCase):
    def build(self, out, cwd):
        r = subprocess.run(["bash", SCRIPT, "v0.34b-hearth.99", out], cwd=cwd, capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)

    def check(self, out):
        with tarfile.open(os.path.join(out, "hearthdaoc-deploy-v0.34b-hearth.99.tar.gz")) as t:
            names = {os.path.normpath(n) for n in t.getnames()}
            env = t.extractfile("./.env.example").read().decode()
        self.assertTrue({"compose.yml", ".env.example", "hdc", "HANDOFF.md", "upstream.lock"} <= names)
        self.assertIn("HEARTHDAOC_TAG=v0.34b-hearth.99", env.splitlines())
        with zipfile.ZipFile(os.path.join(out, "hearthdaoc-client-v0.34b-hearth.99.zip")) as z:
            self.assertIn("hearthdaoc-client-v0.34b-hearth.99/setup.sh", z.namelist())

    def test_relative_output_folder_like_ci(self):
        with tempfile.TemporaryDirectory() as cwd:
            self.build("dist", cwd)
            self.check(os.path.join(cwd, "dist"))

    def test_absolute_output_folder(self):
        with tempfile.TemporaryDirectory() as out:
            self.build(out, tempfile.gettempdir())
            self.check(out)
```

with:

```python
class BuildBundlesTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        bin_dir = os.path.join(self.tmp.name, "bin")
        os.makedirs(bin_dir)
        with open(os.path.join(bin_dir, "dotnet"), "w") as f:
            f.write(f"#!{sys.executable}\n{FAKE_DOTNET}")
        os.chmod(os.path.join(bin_dir, "dotnet"), 0o755)
        self.tool = os.path.join(self.tmp.name, "tool", "OfflineDaoc.Mpk.dll")
        os.makedirs(os.path.dirname(self.tool))
        open(self.tool, "wb").close()
        self.log = os.path.join(self.tmp.name, "dotnet.log")
        self.env = dict(os.environ, PATH=bin_dir + os.pathsep + os.environ["PATH"],
                        HDC_MPK_TOOL=self.tool, FAKE_DOTNET_LOG=self.log)

    def build(self, out, cwd, env=None):
        r = subprocess.run(["bash", SCRIPT, TAG, out], cwd=cwd, env=env or self.env, capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        return r

    def check_deploy(self, out):
        with tarfile.open(os.path.join(out, f"hearthdaoc-deploy-{TAG}.tar.gz")) as t:
            names = {os.path.normpath(n) for n in t.getnames()}
            env = t.extractfile("./.env.example").read().decode()
        self.assertTrue({"compose.yml", ".env.example", "hdc", "HANDOFF.md", "upstream.lock"} <= names)
        self.assertIn(f"HEARTHDAOC_TAG={TAG}", env.splitlines())

    def check(self, out):
        self.check_deploy(out)
        top = f"hearthdaoc-client-{TAG}/"
        with zipfile.ZipFile(os.path.join(out, f"hearthdaoc-client-{TAG}.zip")) as z:
            files = {i.filename[len(top):]: i for i in z.infolist() if not i.is_dir()}
            self.assertEqual(set(files), CLIENT_FILES)  # nothing else: no tests, branding or __pycache__
            for name in EXECUTABLE:
                self.assertTrue((files[name].external_attr >> 16) & 0o100, name)
            for name, src in FROM_REPO.items():
                self.assertEqual(z.read(top + name), read(os.path.join(REPO, src)), name)
            extracted = os.path.join(self.tmp.name, "unzipped")
            z.extractall(extracted)
        bundle = os.path.join(extracted, top)
        splash = os.path.join(bundle, "patches", "splash.mpk")
        self.assertEqual(read(splash), read(os.path.join(bundle, "windows", "patches", "splash.mpk")))
        return bundle, splash

    def check_splash_from_png(self, splash):
        """The bundled splash.mpk holds branding/splash.png as the client's 8 TGAs."""
        splash_entry.check_splash_mpk(splash)
        png = os.path.join(PATCHES, "branding", "splash.png")
        tga = build_splash_mpk.tga_bytes(*build_splash_mpk.read_png(png))
        _name, entries = mpk.read_mpk(splash)
        self.assertTrue(all(data == tga for _entry, data in entries))

    def test_relative_output_folder_like_ci(self):
        cwd = self.tmp.name
        env = dict(self.env, HDC_MPK_TOOL=os.path.join("tool", "OfflineDaoc.Mpk.dll"))
        self.build("dist", cwd, env)
        self.check(os.path.join(cwd, "dist"))
        with open(self.log) as f:
            args = json.load(f)
        self.assertEqual((os.path.realpath(args[0]), args[1], args[-1]),
                         (os.path.realpath(self.tool), "pack", "splash.mpk"))

    def test_absolute_output_folder(self):
        out = os.path.join(self.tmp.name, "out")
        self.build(out, tempfile.gettempdir())
        bundle, splash = self.check(out)
        self.check_splash_from_png(splash)
        # The bundled applier runs from the unpacked bundle with its own patch set.
        with open(os.path.join(PATCHES, "classic-creation.json"), encoding="utf-8") as f:
            paths = [entry["path"] for entry in json.load(f)["files"]]
        empty = os.path.join(self.tmp.name, "empty-client")
        os.makedirs(empty)
        r = subprocess.run([sys.executable, os.path.join(bundle, "patches", "apply_patches.py"),
                            "--client", empty, "--check"], capture_output=True, text=True)
        self.assertEqual((r.returncode, r.stdout), (3, "".join(f"{p}: missing\n" for p in paths)), r.stderr)
        self.assertFalse(os.path.exists(os.path.join(bundle, "patches", "__pycache__")))

    def test_without_the_mpk_tool_nothing_is_built(self):
        out = os.path.join(self.tmp.name, "out")
        for tool in (None, os.path.join(self.tmp.name, "missing.dll")):
            env = dict(self.env)
            env.pop("HDC_MPK_TOOL")
            if tool:
                env["HDC_MPK_TOOL"] = tool
            r = subprocess.run(["bash", SCRIPT, TAG, out], env=env, capture_output=True, text=True)
            self.assertEqual(r.returncode, 2, tool)
            self.assertIn("build_bundles.sh: set HDC_MPK_TOOL to upstream's OfflineDaoc.Mpk.dll", r.stderr)
            self.assertFalse(os.path.exists(out))

    def test_deploy_only_needs_no_mpk_tool(self):
        # deploy/tests/hdc_integration.sh needs only the deploy bundle; CI's test job has no MPK tool.
        out = os.path.join(self.tmp.name, "out")
        env = dict(self.env)
        env.pop("HDC_MPK_TOOL")
        r = subprocess.run(["bash", SCRIPT, TAG, out, "--deploy-only"], env=env, capture_output=True, text=True)
        self.assertEqual((r.returncode, r.stdout), (0, f"Built {out}/hearthdaoc-deploy-{TAG}.tar.gz\n"), r.stderr)
        self.assertEqual(os.listdir(out), [f"hearthdaoc-deploy-{TAG}.tar.gz"])
        self.check_deploy(out)
        self.assertFalse(os.path.exists(self.log))  # no splash.mpk was packed
        r = subprocess.run(["bash", SCRIPT, TAG, out, "--client-only"], env=env, capture_output=True, text=True)
        self.assertEqual(r.returncode, 2)
        self.assertIn("usage: deploy/build_bundles.sh <tag> <output dir> [--deploy-only]", r.stderr)

    @unittest.skipUnless(os.environ.get("HDC_MPK_TOOL") and shutil.which("dotnet"),
                         "set HDC_MPK_TOOL to OfflineDaoc.Mpk.dll (needs dotnet)")
    def test_with_the_real_mpk_tool(self):
        out = os.path.join(self.tmp.name, "out")
        self.build(out, REPO, dict(os.environ))
        _bundle, splash = self.check(out)
        self.check_splash_from_png(splash)
```

Run: `grep -nE '^import re$|env\.splitlines\(\)|def test_repo_leaves_the_tag_to_the_bundle' deploy/tests/test_build_bundles.py`

Expected (main's lines are still there):
```
3:import re
94:        self.assertIn(f"HEARTHDAOC_TAG={TAG}", env.splitlines())
180:    def test_repo_leaves_the_tag_to_the_bundle(self):
```

- [ ] **Step 7: Run test to verify it fails**

Run: `python3 -m unittest discover -s deploy/tests -t deploy -p test_build_bundles.py`

Expected: 4 failures, because the script bundles no patches yet and ignores `HDC_MPK_TOOL` and `--deploy-only`. `test_repo_leaves_the_tag_to_the_bundle` passes (the `.` in the progress line). The order of the set items can differ:
```
FFF.sF
FAIL: test_absolute_output_folder (tests.test_build_bundles.BuildBundlesTests.test_absolute_output_folder)
AssertionError: Items in the second set but not the first:
'patches/apply_patches.py'
'patches/splash.mpk'
'windows/patch-client.ps1'
'windows/patch-client.bat'
'patches/patchset.py'
'windows/patches/splash.mpk'
'windows/patches/classic-creation.json'
'patches/classic-creation.json'
FAIL: test_deploy_only_needs_no_mpk_tool (tests.test_build_bundles.BuildBundlesTests.test_deploy_only_needs_no_mpk_tool)
AssertionError: Tuples differ: (0, '[63 chars]ar.gz and /tmp/tmp.../out/hearthdaoc-clie[22 chars]p\n') != (0, '[63 chars]ar.gz\n')
FAIL: test_relative_output_folder_like_ci (tests.test_build_bundles.BuildBundlesTests.test_relative_output_folder_like_ci)
AssertionError: Items in the second set but not the first:
... (the same 8 files)
FAIL: test_without_the_mpk_tool_nothing_is_built (tests.test_build_bundles.BuildBundlesTests.test_without_the_mpk_tool_nothing_is_built)
AssertionError: 0 != 2
----------------------------------------------------------------------
Ran 6 tests in 0.074s

FAILED (failures=4, skipped=1)
```

- [ ] **Step 8: Write minimal implementation (build_bundles.sh and the integration test)**

Make three edits in `deploy/build_bundles.sh` (it stays executable, mode 100755). Main's tag stamp (the `sed` line with its comment) and its `grep -qxF` guard sit between edits 2 and 3 and stay as they are.

1. Usage, options and the MPK tool check. Replace:

```bash
# Usage: deploy/build_bundles.sh <tag> <output dir>   (run from the repository root; used by CI and tests)
set -euo pipefail
tag="${1:?release tag}"
```

with:

```bash
# Usage: HDC_MPK_TOOL=<OfflineDaoc.Mpk.dll> deploy/build_bundles.sh <tag> <output dir> [--deploy-only]
#   (run from the repository root; used by CI and tests). HDC_MPK_TOOL is upstream's MPK tool (it needs
#   dotnet); it packs the client bundle's splash.mpk from client/patches/branding/splash.png.
#   --deploy-only builds only the deploy bundle, without the MPK tool (deploy/tests/hdc_integration.sh).
set -euo pipefail
tag="${1:?release tag}"
case "${3:-}" in
    "") client=yes ;;
    --deploy-only) client=no ;;
    *) echo "usage: deploy/build_bundles.sh <tag> <output dir> [--deploy-only]" >&2; exit 2 ;;
esac
if [[ $client == yes && ! -f "${HDC_MPK_TOOL:-}" ]]; then
    echo "build_bundles.sh: set HDC_MPK_TOOL to upstream's OfflineDaoc.Mpk.dll (it packs the client's splash.mpk):" >&2
    echo "  dotnet build source/tools/OfflineDaoc.Mpk/OfflineDaoc.Mpk.csproj -c Release" >&2
    echo "  HDC_MPK_TOOL=source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll deploy/build_bundles.sh $tag ${2:-dist}" >&2
    exit 2
fi
```

2. Build the client's splash first. Replace:

```bash
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
mkdir -p "$work/deploy" "$work/client/hearthdaoc-client-$tag/windows"
```

with:

```bash
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
if [[ $client == yes ]]; then  # the client's loading splash first: if it can't be built, nothing is written
    python3 -B "$root/client/patches/branding/build_splash_mpk.py" --mpk-tool "$(realpath "$HDC_MPK_TOOL")" \
        --out "$work/splash.mpk" >/dev/null
fi
mkdir -p "$work/deploy"
```

3. Stop after the deploy bundle with `--deploy-only`; bundle the patches and the Windows applier. Replace:

```bash
tar czf "$out/hearthdaoc-deploy-$tag.tar.gz" -C "$work/deploy" .
c="$work/client/hearthdaoc-client-$tag"
cp "$root"/client/README.md "$root"/client/linux/setup.sh "$root"/client/linux/play.sh.in \
   "$root"/tools/linux/odaoc_fetch.py "$root"/deploy/upstream.lock "$c/"
cp "$root"/client/windows/connect-hearthdaoc.bat "$c/windows/"
```

with:

```bash
tar czf "$out/hearthdaoc-deploy-$tag.tar.gz" -C "$work/deploy" .
if [[ $client == no ]]; then echo "Built $out/hearthdaoc-deploy-$tag.tar.gz"; exit 0; fi
c="$work/client/hearthdaoc-client-$tag"
mkdir -p "$c/patches" "$c/windows/patches"
cp "$root"/client/README.md "$root"/client/linux/setup.sh "$root"/client/linux/play.sh.in \
   "$root"/tools/linux/odaoc_fetch.py "$root"/deploy/upstream.lock "$c/"
# Client patches (classic character creation, splash): our patch data, the appliers and our splash.mpk.
cp "$root"/client/patches/{classic-creation.json,apply_patches.py,patchset.py} "$work/splash.mpk" "$c/patches/"
cp "$root"/client/windows/{connect-hearthdaoc.bat,patch-client.bat,patch-client.ps1} "$c/windows/"
cp "$root"/client/patches/classic-creation.json "$work/splash.mpk" "$c/windows/patches/"
```

The splash is built first, into the temporary work folder, so a failing MPK tool leaves no half-written release. `python3 -B` keeps `build_splash_mpk.py`'s imports from writing `__pycache__` into the checkout, and its "Wrote ..." line (a temporary path) is dropped. `realpath` makes a relative `HDC_MPK_TOOL` absolute (see PR #62 for why paths are made absolute here).

In `deploy/tests/hdc_integration.sh`, the `make_bundle` function only uses the deploy bundle. Replace:

```bash
    "$HERE/../build_bundles.sh" "$1" "$W/b-$1" >/dev/null
```

with:

```bash
    "$HERE/../build_bundles.sh" "$1" "$W/b-$1" --deploy-only >/dev/null
```

Run: `git diff -U0 deploy/build_bundles.sh | grep '^-[^-]'`

Expected (only these three lines go; the tag stamp and its guard stay):
```
-# Usage: deploy/build_bundles.sh <tag> <output dir>   (run from the repository root; used by CI and tests)
-mkdir -p "$work/deploy" "$work/client/hearthdaoc-client-$tag/windows"
-cp "$root"/client/windows/connect-hearthdaoc.bat "$c/windows/"
```

- [ ] **Step 9: Run the tests to verify they pass, then with the real MPK tool**

Run: `python3 -m unittest discover -s deploy/tests -t deploy -p test_build_bundles.py -v`

Expected:
```
test_absolute_output_folder (tests.test_build_bundles.BuildBundlesTests.test_absolute_output_folder) ... ok
test_deploy_only_needs_no_mpk_tool (tests.test_build_bundles.BuildBundlesTests.test_deploy_only_needs_no_mpk_tool) ... ok
test_relative_output_folder_like_ci (tests.test_build_bundles.BuildBundlesTests.test_relative_output_folder_like_ci) ... ok
test_repo_leaves_the_tag_to_the_bundle (tests.test_build_bundles.BuildBundlesTests.test_repo_leaves_the_tag_to_the_bundle) ... ok
test_with_the_real_mpk_tool (tests.test_build_bundles.BuildBundlesTests.test_with_the_real_mpk_tool) ... skipped 'set HDC_MPK_TOOL to OfflineDaoc.Mpk.dll (needs dotnet)'
test_without_the_mpk_tool_nothing_is_built (tests.test_build_bundles.BuildBundlesTests.test_without_the_mpk_tool_nothing_is_built) ... ok

----------------------------------------------------------------------
Ran 6 tests in 6.143s

OK (skipped=1)
```

Build upstream's MPK tool if Task 6 hasn't already (needs the .NET 10 SDK; `bin/` and `obj/` are git-ignored):

Run: `dotnet build source/tools/OfflineDaoc.Mpk/OfflineDaoc.Mpk.csproj -c Release`

Expected: `Build succeeded.` and `0 Error(s)`.

Run: `HDC_MPK_TOOL=source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll python3 -m unittest discover -s deploy/tests -t deploy -p test_build_bundles.py -k real_mpk -v`

Expected:
```
test_with_the_real_mpk_tool (tests.test_build_bundles.BuildBundlesTests.test_with_the_real_mpk_tool) ... ok

----------------------------------------------------------------------
Ran 1 test in 4.789s

OK
```

Then check the script by hand, without and with the tool, into a temporary folder (so no `dist/` is left in the checkout):

Run:
```bash
T=$(mktemp -d); env -u HDC_MPK_TOOL deploy/build_bundles.sh v0.34b-hearth.99 "$T/dist"; echo "rc=$?"; ls "$T"
HDC_MPK_TOOL=source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll deploy/build_bundles.sh v0.34b-hearth.99 "$T/dist"; echo "rc=$?"
unzip -Z1 "$T/dist/hearthdaoc-client-v0.34b-hearth.99.zip" | grep -v '/$' | sort
unzip -Z "$T/dist/hearthdaoc-client-v0.34b-hearth.99.zip" | grep -E 'setup.sh|apply_patches'; rm -rf "$T"
```

Expected (`ls "$T"` prints nothing: nothing was written without the tool):
```
build_bundles.sh: set HDC_MPK_TOOL to upstream's OfflineDaoc.Mpk.dll (it packs the client's splash.mpk):
  dotnet build source/tools/OfflineDaoc.Mpk/OfflineDaoc.Mpk.csproj -c Release
  HDC_MPK_TOOL=source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll deploy/build_bundles.sh v0.34b-hearth.99 /tmp/tmp.../dist
rc=2
Built /tmp/tmp.../dist/hearthdaoc-deploy-v0.34b-hearth.99.tar.gz and /tmp/tmp.../dist/hearthdaoc-client-v0.34b-hearth.99.zip
rc=0
hearthdaoc-client-v0.34b-hearth.99/odaoc_fetch.py
hearthdaoc-client-v0.34b-hearth.99/patches/apply_patches.py
hearthdaoc-client-v0.34b-hearth.99/patches/classic-creation.json
hearthdaoc-client-v0.34b-hearth.99/patches/patchset.py
hearthdaoc-client-v0.34b-hearth.99/patches/splash.mpk
hearthdaoc-client-v0.34b-hearth.99/play.sh.in
hearthdaoc-client-v0.34b-hearth.99/README.md
hearthdaoc-client-v0.34b-hearth.99/setup.sh
hearthdaoc-client-v0.34b-hearth.99/upstream.lock
hearthdaoc-client-v0.34b-hearth.99/windows/connect-hearthdaoc.bat
hearthdaoc-client-v0.34b-hearth.99/windows/patch-client.bat
hearthdaoc-client-v0.34b-hearth.99/windows/patch-client.ps1
hearthdaoc-client-v0.34b-hearth.99/windows/patches/classic-creation.json
hearthdaoc-client-v0.34b-hearth.99/windows/patches/splash.mpk
-rwxrwxr-x  3.0 unx     4729 tx defN 26-Oct-06 13:44 hearthdaoc-client-v0.34b-hearth.99/patches/apply_patches.py
-rwxrwxr-x  3.0 unx     3470 tx defN 26-Oct-06 13:44 hearthdaoc-client-v0.34b-hearth.99/setup.sh
```
(Sizes and times can differ; what matters is the `-rwxrwxr-x` mode on the scripts.)
(The sizes depend on Tasks 1 to 6. Each `splash.mpk` is about 13.9 MB.)

The `make_bundle` steps of `hdc_integration.sh` without Docker, as CI's test job runs them (no `HDC_MPK_TOOL`):

Run: `W=$(mktemp -d) && env -u HDC_MPK_TOOL deploy/build_bundles.sh it-update "$W/b-it-update" --deploy-only && mkdir "$W/b-it-update/x" && tar xzf "$W/b-it-update/hearthdaoc-deploy-it-update.tar.gz" -C "$W/b-it-update/x" && ls -A "$W/b-it-update/x" && grep HEARTHDAOC_TAG "$W/b-it-update/x/.env.example"; rm -rf "$W"`

Expected:
```
Built /tmp/tmp.../b-it-update/hearthdaoc-deploy-it-update.tar.gz
compose.yml
.env.example
HANDOFF.md
hdc
upstream.lock
HEARTHDAOC_TAG=it-update
```

Last, if a local OfflineDAoC 0.34 classic client folder is at hand (here `~/Games/HearthDAoC/client`; it is only read), check the unpacked bundle's applier on a scratch copy of its three patched files:

Run:
```bash
C=~/Games/HearthDAoC/client; T=$(mktemp -d) \
  && HDC_MPK_TOOL=source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll deploy/build_bundles.sh v0.34b-hearth.99 "$T/dist" >/dev/null \
  && unzip -q "$T/dist/hearthdaoc-client-v0.34b-hearth.99.zip" -d "$T" && B="$T/hearthdaoc-client-v0.34b-hearth.99" \
  && mkdir -p "$T/client/pregame" && cp "$C/game.dll" "$T/client/" \
  && cp "$C/pregame/character_customize_stats.xml" "$C/pregame/splash.mpk" "$T/client/pregame/" \
  && python3 "$B/patches/apply_patches.py" --client "$T/client"; echo "exit $?"; \
  cmp "$T/client/pregame/splash.mpk" "$B/patches/splash.mpk" && echo "splash installed"; \
  python3 "$B/patches/apply_patches.py" --client "$T/client" --restore; echo "exit $?"; \
  ls "$B/patches"; rm -rf "$T"
```

Expected (the bundle's `patches/` stays free of `__pycache__`):
```
Patched: game.dll (original saved as game.dll.hearthdaoc-orig)
Patched: pregame/character_customize_stats.xml (original saved as pregame/character_customize_stats.xml.hearthdaoc-orig)
Patched: pregame/splash.mpk (original saved as pregame/splash.mpk.hearthdaoc-orig)
exit 0
splash installed
Restored: game.dll
Restored: pregame/character_customize_stats.xml
Restored: pregame/splash.mpk
exit 0
apply_patches.py
classic-creation.json
patchset.py
splash.mpk
```

- [ ] **Step 10: Run every suite that this task touches**

Run: `python3 -m unittest discover -s deploy/tests -t deploy && python3 -m unittest discover -s client/tests -t client && python3 -m unittest discover -s client/patches/tests -t client/patches && bash -n deploy/tests/hdc_integration.sh && git status --short --untracked-files=all`

Expected: three `OK` results. On this branch (main merged through PR #70) they are `Ran 109 tests` / `OK (skipped=18)`, `Ran 11 tests` / `OK` and `Ran 175 tests` / `OK (skipped=42)`; the counts depend on the other tasks and on main. Then only this task's files:
```
 M deploy/build_bundles.sh
 M deploy/tests/hdc_integration.sh
 M deploy/tests/test_build_bundles.py
```

- [ ] **Step 11: Commit**

```bash
git add deploy/build_bundles.sh deploy/tests/test_build_bundles.py deploy/tests/hdc_integration.sh
git commit -m "feat(release): client bundle carries the client patches and splash.mpk

build_bundles.sh packs client/patches/branding/splash.png into splash.mpk
with upstream's MPK tool (HDC_MPK_TOOL, needs dotnet; it stops with a
clear message, before writing anything, without it). The client bundle
gets patches/ (classic-creation.json, apply_patches.py, patchset.py,
splash.mpk) for setup.sh, and windows/patch-client.bat, patch-client.ps1
and windows/patches/ (the same patch set and splash.mpk). --deploy-only
builds just the deploy bundle; the hdc integration test uses it.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git status --short
```

Expected: `git status --short` prints nothing.

**Notes for later tasks** (from this task's results):
- **Task 8 (CI).** After this task, the `release-assets` job's "Build bundles (no EA files)" step (`deploy/build_bundles.sh "$TAG" dist`) fails with exit 2 until the job provides the MPK tool. Since PR #70 that job runs on the push to `main` that publishes a release, after the image is pushed, so Task 8 must land in the same PR. It needs `actions/setup-dotnet@v4` (`dotnet-version: "10.0.x"`), then `dotnet build source/tools/OfflineDaoc.Mpk/OfflineDaoc.Mpk.csproj -c Release`, then `HDC_MPK_TOOL: source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll` in the bundle step's `env`. The test job needs nothing new for `test_build_bundles.py`: the fake `dotnet` covers it. If the test job sets `HDC_MPK_TOOL` (with the tool built), `test_with_the_real_mpk_tool` runs too, from the repository root.
- **Task 8 (releases).** The client bundle's `splash.mpk` now depends on `source/tools/OfflineDaoc.Mpk/`, which `deploy/release_tag.py` doesn't count as shipped yet, so a change to it makes no release. Task 8 adds it to `RELEASE_PATHS`.
- **Task 8 (docs).**
  - `client/README.md`: `setup.sh` now patches the client and prints a warning when the client isn't the supported one. To undo: `python3 patches/apply_patches.py --client ~/Games/HearthDAoC/client --restore`, from the unpacked bundle. On Windows, copy `windows/patch-client.bat`, `windows/patch-client.ps1` and the `windows/patches` folder next to `connect-hearthdaoc.bat`, then double-click `patch-client.bat`.
  - Developer docs (`FORK.md`): `setup.sh` run from a checkout uses `client/patches/` and needs the git-ignored `client/patches/splash.mpk`. Without it, setup stops with `Error: bundled file missing: .../client/patches/splash.mpk`. Build it first with `client/patches/branding/build_splash_mpk.py --mpk-tool ...`. `deploy/build_bundles.sh` now needs `HDC_MPK_TOOL`, or `--deploy-only`.

---

### Task 8: CI and docs: client patch tests on the real classic files, splash.mpk in the release job, client-patch docs

CI starts testing the client patch set against the real OfflineDAoC 0.34 classic files, and the release job gets the MPK tool that `deploy/build_bundles.sh` needs since Task 7. `deploy/release_tag.py` learns that a change to that tool makes a release, and that the patch set's generator alone doesn't. The docs explain the patch set to the owner (`docs/fork/FORK.md`) and to players (`client/README.md`), and credit OfflineDAoC for the splash art.

Facts this task relies on (checked on 2026-10-06):
- CI's workflow is `.github/workflows/server-image.yml`, the only workflow since PR #70 (merging is releasing). It tests every PR and every push to `main`. On `main`, when `deploy/release_tag.py next` names a tag, the same run publishes that release. Its test job is `test-build-publish` (tests, image build, and the image push for a release). Its release job is `release-assets`, which runs only for a release: `actions/checkout@v4`, then "Build bundles (no EA files)" (`deploy/build_bundles.sh "$TAG" dist`), then "Create release (and its tag on this commit)" (`gh release create "$TAG" "dist/hearthdaoc-deploy-$TAG.tar.gz" "dist/hearthdaoc-client-$TAG.zip" ... --latest --generate-notes --notes "..."`). The `--notes` text is an inline string that gh puts before the notes it generates from the merged PRs. There is no notes file and no `release_tag.py notes`.
- The test job already has `actions/setup-dotnet@v4` with `dotnet-version: "10.0.x"`, fetches a clean classic world into `$RUNNER_TEMP/world` and passes it to the tests as `HDC_TEST_WORLD`. The release job has no .NET yet. So after Task 7 its bundle step stops with exit 2, and on `main` that happens after the image has been pushed. This task must land in the same PR as Task 7.
- `deploy/tests/test_workflows.py` (PR #70) holds `MergeIsReleaseTests`. They read the workflow as text and check, among other things, that the release gets exactly `"dist/hearthdaoc-deploy-$TAG.tar.gz"` and `"dist/hearthdaoc-client-$TAG.zip"` and that the bundles are built before `gh release create`. This task adds its own checks to the same file, as the class `ClientPatchWorkflowTests`.
- `deploy/release_tag.py` (PR #70) decides what makes a release. `release_worthy(paths)` is true for a path under `RELEASE_PATHS` (what `deploy/Dockerfile` copies and `deploy/build_bundles.sh` bundles: `source/server/`, two `source/tools` paths, `tools/linux/`, `deploy/`, `client/`, `.dockerignore`), unless it is under `NOT_RELEASE_PATHS`, in a `tests/` folder (any depth), or a `.md` file other than `deploy/HANDOFF.md` and `client/README.md`. So `client/patches/tests/` already makes no release; `deploy/tests/test_release_auto.py`'s `ReleaseWorthyTests` already lists `client/patches/tests/test_patchset.py`. Two of sub2's paths are classified wrongly. `source/tools/OfflineDaoc.Mpk/` makes no release, although after this task the release job builds the client bundle's `splash.mpk` with it (its project references `source/server/CoreBase`, which is shipped already). And `client/` counts as a whole, so the patch set's generator under `client/patches/` makes a release too, although nothing ships it.
- `tools/linux/odaoc_fetch.py --lock deploy/upstream.lock extract <archive path> <dest>` downloads one file of the pinned release with HTTP range requests, checks it against the release manifest (whose SHA-256 the lock pins) and creates the destination's folders. The archive paths come from the lock: the classic `game.dll` is `editions.classic.game_dll` (`editions/0.34-no-custom-class/runtime/client-opendaoc/app/game.dll`); the pregame files are under `client_prefix` (`runtime/client-opendaoc/app/`). The three files are about 20 MB (`splash.mpk` is 14 MB). Their SHA-256 values are the patch set's "before" hashes: `67dcf68a...`, `08e6f7c3...` and `f24460d2...`.
- CI's world (`init_world.py --edition classic`) is byte-identical to `~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db` (release manifest SHA-256 `afb8a3bedf88fdd6dbde3f3a7b54997dc52306241dbe9be498d024f5e42b645d`).
- The fetched files are EA files. They go to `$RUNNER_TEMP`, which is outside the checkout. So they are never in the Docker build context (`docker build ... .`), which `.dockerignore` limits anyway, and the workflow uploads nothing but the two bundles. GitHub's runners are deleted after the job.
- GitHub's `ubuntu-24.04` runners have `pwsh` (PowerShell 7) and `ruby` preinstalled; nasm comes from apt (2.16.01, the version the patch set was built with). Task 2's harness takes `HDC_PWSH` or else looks up `pwsh`; when `HDC_PWSH` names a missing program, the PowerShell cases fail instead of being skipped.
- Building the MPK tool in Release before the server unit tests and the Linux CLIs doesn't disturb them (checked: the CLI build, `dotnet test ... UT_CommandPrivLevelOverrides` and all suites pass after it).
- The workflow runs a step's `run` with `bash -e`, from the checkout.
- What sub2 adds under `client/` and what reaches a player:
  - Bundled as they are: `classic-creation.json`, `apply_patches.py`, `patchset.py` and `client/windows/patch-client.*`.
  - Packed into the bundled `splash.mpk` by `deploy/build_bundles.sh`: `branding/splash.png`, packed by `branding/build_splash_mpk.py`, which imports `splash_entry.py` (sizes, TGA header and footer) and `mpk.py`.
  - The generator: `build.py`, `pe.py`, `classdata.py`, `src/` and `branding/reletter_splash.py` (Pillow, run by hand). It only runs on a developer's machine and in CI's rebuild check. Its output, `classic-creation.json` and `splash.png`, is committed. A generator change that alters the output must come with the regenerated file, because the rebuild check fails otherwise, and that file then makes the release.

**Before Step 1**, check the prerequisites. All commands run from the repository root, on branch `sub2-classic-creation`.

Run: `git log --oneline -1 -- deploy/tests/test_build_bundles.py && grep -c '"path"' client/patches/classic-creation.json`

Expected: Task 7's commit, then `3`:
```
2186cba feat(release): client bundle carries the client patches and splash.mpk
3
```
(The hash differs on your branch.)

Run: `nasm -v && dotnet --list-sdks | grep '^10\.' && ruby -v && (command -v pwsh || echo "no pwsh: install it as in Task 2 Step 2")`

Expected (versions may be newer; ruby is only needed to read the workflow's YAML):
```
NASM version 2.16.01
10.0.112 [/usr/lib/dotnet/sdk]
ruby 3.2.3 (2024-01-18 revision 52bb2ac0a6) [x86_64-linux-gnu]
no pwsh: install it as in Task 2 Step 2
```
Without `pwsh`, install it as Task 2 did and put it on `PATH` for this task's checks (CI names it `pwsh`): `dotnet tool install PowerShell --tool-path /tmp/hdc-pwsh && export PATH="/tmp/hdc-pwsh:$PATH" HDC_PWSH=/tmp/hdc-pwsh/pwsh`.

Run: `dotnet build source/tools/OfflineDaoc.Mpk/OfflineDaoc.Mpk.csproj -c Release --nologo | grep -E 'Error\(s\)' && HDC_MPK_TOOL=$PWD/source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll HDC_CLIENT_FILES=~/Games/HearthDAoC/client HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -m unittest discover -s client/patches/tests -t client/patches 2>&1 | tail -3`

Expected (Tasks 1 to 7 together, nothing skipped):
```
    0 Error(s)
Ran 175 tests in 70.646s

OK
```
This reads `~/Games/HearthDAoC/client` (read-only), as Tasks 3 to 7 did. Its `game.dll` must still be the stock file (SHA-256 `67dcf68a...`), not one that `setup.sh` has patched.

**Files:**
- Modify: `.github/workflows/server-image.yml` (test job: nasm, fetch the classic files, build the MPK tool, run `client/patches/tests`, rebuild check; release job: .NET, MPK tool, `HDC_MPK_TOOL`; the `--notes` text credits the splash art)
- Test: `deploy/tests/test_workflows.py` (two edits: imports, and a new class `ClientPatchWorkflowTests` after `MergeIsReleaseTests`; it reads the workflow with ruby and runs its fetch step against a fake release)
- Modify: `deploy/release_tag.py` (`RELEASE_PATHS` gains `source/tools/OfflineDaoc.Mpk/`; the patch set's generator joins `NOT_RELEASE_PATHS`)
- Test: `deploy/tests/test_release_auto.py` (two `ReleaseWorthyTests` cases)
- Modify: `docs/fork/FORK.md` ("What the fork changes" row, new "Client patches" section, upstream-sync step, release assets)
- Modify: `client/README.md` (classic creation screen, Linux restore, Windows patch step, credits)

**Interfaces:**
- Consumes:
  - Tasks 1 and 2: the suite `python3 -m unittest discover -s client/patches/tests -t client/patches` (its PowerShell cases run when `HDC_PWSH` or `pwsh` is set or found); `apply_patches.py --client DIR [--restore]`; `client/windows/patch-client.bat` / `patch-client.ps1` (`-Client` defaults to the script's folder, `-PatchSet` to `patches\classic-creation.json` next to it, `-Restore`).
  - Tasks 3 to 6: `client/patches/build.py --client DIR --world-db FILE --server-src source/server [--splash-mpk FILE] --out FILE` (needs nasm; byte-identical output for the same inputs); real-file tests read `HDC_CLIENT_FILES` (a folder with `game.dll`, `pregame/character_customize_stats.xml`, `pregame/splash.mpk`) and `HDC_TEST_WORLD`; MPK cases read `HDC_MPK_TOOL`.
  - Task 6: `client/patches/branding/build_splash_mpk.py --mpk-tool <OfflineDaoc.Mpk.dll> [--out FILE]`; `client/patches/splash.mpk` is git-ignored.
  - Task 7: `HDC_MPK_TOOL=<OfflineDaoc.Mpk.dll> deploy/build_bundles.sh <tag> <out>`; the client bundle's `windows/` holds `connect-hearthdaoc.bat`, `patch-client.bat`, `patch-client.ps1` and `patches/` (`classic-creation.json`, `splash.mpk`); `patches/` holds the Linux applier.
  - Existing: `tools/linux/odaoc_fetch.py --lock LOCK extract REL DEST`; `deploy/upstream.lock` keys `client_prefix`, `editions.classic.game_dll`, `editions.classic.world_db`; `tools/linux/tests/release_fixture.py` (`build(directory, files=None) -> (lock, files)`, `DEFAULT_FILES`, `RangeServer(directory)` with `.lock(lock)`); `deploy/tests/test_workflows.py` (`ROOT`, `WORKFLOWS`, `read(name)`, `job(text, name)`, `MergeIsReleaseTests`); `deploy/release_tag.py` (`RELEASE_PATHS`, `NOT_RELEASE_PATHS`, `release_worthy(paths)`).
- Produces:
  - Test job steps, in this order: `Install test tools` (adds `nasm`), `Fetch a clean classic world ...` (unchanged), `Fetch the classic client files the patch set patches (tests only, never published)` (writes exactly `$RUNNER_TEMP/classic-client/{game.dll,pregame/character_customize_stats.xml,pregame/splash.mpk}`), `Build upstream's MPK tool (packs the client's splash.mpk)`, `Build Linux CLIs` (unchanged), `Unit tests` (also runs `client/patches/tests`; env `HDC_CLIENT_FILES=${{ runner.temp }}/classic-client`, `HDC_MPK_TOOL=${{ github.workspace }}/source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll`, `HDC_PWSH=pwsh`), `Client patch set matches a rebuild` (fails with `::error file=client/patches/classic-creation.json::out of date: ...` when the committed patch set differs from a rebuild).
  - Release job: `actions/setup-dotnet@v4`, `Build upstream's MPK tool (packs the client bundle's splash.mpk)`, then `Build bundles (no EA files)` with `HDC_MPK_TOOL: source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll`, then `Create release (and its tag on this commit)`, whose `--notes` string gains the line `The client's loading splash is OfflineDAoC's art, re-lettered HEARTH DAoC (...). Credit for the artwork goes to shadowofze/OfflineDAoC.`
  - `deploy/tests/test_workflows.py`: `ClientPatchWorkflowTests` (6 tests; skipped without ruby), next to `MergeIsReleaseTests`. CI runs it with the other `deploy/tests`.
  - `deploy/release_tag.py`: `release_worthy` is true for `source/tools/OfflineDaoc.Mpk/...`, and false for `client/patches/build.py`, `pe.py`, `classdata.py`, `src/...` and `branding/reletter_splash.py`.
  - `docs/fork/FORK.md` section `## Client patches` (table: what is patched, where, why; sources; splash credit; "Rebuilding the patch set" commands); `## Syncing with upstream` step 3 "Rebuild the client patch set", on the sync branch.
  - `client/README.md` sections `## Classic character creation` and `## Credits`; Windows steps 3 to 5.

- [ ] **Step 1: Write the failing tests**

Two test files get new cases.

`deploy/tests/test_workflows.py` gets the class `ClientPatchWorkflowTests`, next to PR #70's `MergeIsReleaseTests`. It reads the workflow as data (ruby's YAML to JSON, since Python's standard library has no YAML reader), finds steps by what they run, and runs the fetch step's script for real against a small fake release (`release_fixture`), with `RUNNER_TEMP` pointing at a temp folder. The files it expects are the paths in the committed `classic-creation.json`, so a patch set that gains a file also needs the fetch step to get it. Make two edits.

1. The imports. Replace:

```python
import os
import re
import unittest

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
WORKFLOWS = os.path.join(ROOT, ".github", "workflows")
```

with:

```python
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
WORKFLOWS = os.path.join(ROOT, ".github", "workflows")
sys.path.insert(0, os.path.join(ROOT, "tools", "linux", "tests"))
import release_fixture as fx  # noqa: E402
```

2. Add the class after `MergeIsReleaseTests`. Replace:

```python
        self.assertIn("contents: write", job(self.text, "release-assets"))


if __name__ == "__main__":
```

with:

```python
        self.assertIn("contents: write", job(self.text, "release-assets"))


# ClientPatchWorkflowTests read the workflow as data: ruby's YAML to JSON, since Python's standard library
# has no YAML reader. GitHub's Ubuntu runners have ruby; without it these tests are skipped.
RUBY = shutil.which("ruby")
YAML_TO_JSON = "require 'yaml'; require 'json'; puts JSON.generate(YAML.safe_load(File.read(ARGV[0])))"
PATCH_SET = os.path.join(ROOT, "client", "patches", "classic-creation.json")
TEST_JOB, RELEASE_JOB = "test-build-publish", "release-assets"
MPK_PROJECT = "source/tools/OfflineDaoc.Mpk/OfflineDaoc.Mpk.csproj"
MPK_TOOL = "source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll"
PATCH_TESTS = "python3 -m unittest discover -s client/patches/tests -t client/patches -v"
APP = "runtime/client-opendaoc/app/"


def read_bytes(path):
    with open(path, "rb") as f:
        return f.read()


def listing(folder):
    """Every file below folder, as sorted relative paths with forward slashes."""
    return sorted(os.path.relpath(os.path.join(d, n), folder).replace(os.sep, "/")
                  for d, _, names in os.walk(folder) for n in names)


@unittest.skipUnless(RUBY, "needs ruby to read the workflow's YAML")
class ClientPatchWorkflowTests(unittest.TestCase):
    """The client patch tests get the real classic client files, nasm, upstream's MPK tool and pwsh. The
    fetched EA files stay outside the checkout and are never published. The release job builds the MPK
    tool for the client bundle's splash.mpk, and its notes credit the splash art."""

    @classmethod
    def setUpClass(cls):
        run = subprocess.run([RUBY, "-e", YAML_TO_JSON, os.path.join(WORKFLOWS, "server-image.yml")],
                             capture_output=True, text=True, check=True)
        cls.jobs = json.loads(run.stdout)["jobs"]
        with open(PATCH_SET, encoding="utf-8") as f:
            cls.patched = sorted(entry["path"] for entry in json.load(f)["files"])

    def steps(self, job):
        return self.jobs[job]["steps"]

    def step(self, job, text):
        """(index, step) of the one step of `job` whose run script contains `text`."""
        found = [(i, s) for i, s in enumerate(self.steps(job)) if text in s.get("run", "")]
        self.assertEqual(len(found), 1, f"{job}: steps that run {text!r}")
        return found[0]

    def uses(self, job, action):
        """Index of the step of `job` that uses `action`."""
        return [s.get("uses") for s in self.steps(job)].index(action)

    def test_the_fetch_step_gets_exactly_the_patched_files_into_runner_temp(self):
        # Run the step's script the way GitHub does (bash -e, from the checkout) against a small
        # fake release that holds the patched files at the real release's paths.
        _, fetch = self.step(TEST_JOB, "odaoc_fetch.py")
        _, tests = self.step(TEST_JOB, PATCH_TESTS)
        with tempfile.TemporaryDirectory() as tmp:
            files = dict(fx.DEFAULT_FILES)
            for path in self.patched:
                if path != "game.dll":
                    files[APP + path] = path.encode() * 40
            lock, files = fx.build(tmp, files)
            checkout, runner_temp = os.path.join(tmp, "checkout"), os.path.join(tmp, "runner-temp")
            os.makedirs(os.path.join(checkout, "deploy"))
            os.makedirs(os.path.join(checkout, "tools", "linux"))
            os.makedirs(runner_temp)
            shutil.copy(os.path.join(ROOT, "tools", "linux", "odaoc_fetch.py"),
                        os.path.join(checkout, "tools", "linux"))
            with fx.RangeServer(tmp) as srv:
                with open(os.path.join(checkout, "deploy", "upstream.lock"), "w") as f:
                    json.dump(srv.lock(lock), f)
                run = subprocess.run(["bash", "-e", "-c", fetch["run"]], cwd=checkout, capture_output=True,
                                     text=True, env=dict(os.environ, RUNNER_TEMP=runner_temp))
            self.assertEqual(run.returncode, 0, run.stderr)
            client = tests["env"]["HDC_CLIENT_FILES"].replace("${{ runner.temp }}", runner_temp)
            # game.dll comes from the classic edition, the other files from the release's client folder.
            expected = {path: files[lock["editions"]["classic"]["game_dll"] if path == "game.dll" else APP + path]
                        for path in self.patched}
            self.assertEqual({path: read_bytes(os.path.join(client, path)) for path in listing(client)}, expected)
            self.assertEqual(listing(runner_temp),
                             [os.path.relpath(client, runner_temp) + "/" + path for path in self.patched])
            # Nothing lands in the checkout, so nothing fetched can reach the image's build context.
            self.assertEqual(listing(checkout), ["deploy/upstream.lock", "tools/linux/odaoc_fetch.py"])

    def test_the_client_patch_tests_get_nasm_the_mpk_tool_pwsh_and_the_world(self):
        i_apt, apt = self.step(TEST_JOB, "apt-get install")
        i_mpk, _ = self.step(TEST_JOB, f"dotnet build {MPK_PROJECT} -c Release")
        i_fetch, _ = self.step(TEST_JOB, "odaoc_fetch.py")
        i_world, _ = self.step(TEST_JOB, "init_world.py")
        i_tests, tests = self.step(TEST_JOB, PATCH_TESTS)
        self.assertIn("nasm", apt["run"].split())
        self.assertLess(self.uses(TEST_JOB, "actions/setup-dotnet@v4"), i_mpk)
        self.assertLess(max(i_apt, i_mpk, i_fetch, i_world), i_tests)
        env = tests["env"]
        self.assertEqual(env["HDC_MPK_TOOL"], "${{ github.workspace }}/" + MPK_TOOL)
        self.assertEqual(env["HDC_TEST_WORLD"], "${{ runner.temp }}/world/world/opendaoc.sqlite3.db")
        # Named, not looked up: without pwsh the PowerShell cases fail instead of being skipped.
        self.assertEqual(env["HDC_PWSH"], "pwsh")

    def test_a_rebuild_of_the_patch_set_is_compared_with_the_committed_one(self):
        i_tests, tests = self.step(TEST_JOB, PATCH_TESTS)
        i_rebuild, rebuild = self.step(TEST_JOB, "client/patches/build.py")
        self.assertGreater(i_rebuild, i_tests)
        script = rebuild["run"]
        # The splash entry needs a built splash.mpk, so the rebuild packs one with the MPK tool.
        for part in ('client/patches/branding/build_splash_mpk.py --mpk-tool "$HDC_MPK_TOOL"',
                     '--client "$HDC_CLIENT_FILES"', '--world-db "$HDC_TEST_WORLD"', "--server-src source/server",
                     "--splash-mpk", 'diff -u client/patches/classic-creation.json "$RUNNER_TEMP/'):
            self.assertIn(part, script)
        for name in ("HDC_CLIENT_FILES", "HDC_TEST_WORLD", "HDC_MPK_TOOL"):
            self.assertEqual(rebuild["env"][name], tests["env"][name], name)

    def test_nothing_fetched_is_published(self):
        for name, job_steps in self.jobs.items():
            for s in job_steps["steps"]:
                self.assertNotIn("upload-artifact", s.get("uses", ""), name)
        for s in self.steps(RELEASE_JOB):
            self.assertNotIn("odaoc_fetch", s.get("run", ""))
            self.assertNotIn("init_world", s.get("run", ""))
        # The release gets the two bundles and nothing else.
        _, create = self.step(RELEASE_JOB, "gh release create")
        assets = create["run"].split("gh release create", 1)[1].split("--target", 1)[0]
        self.assertEqual(assets.replace("\\\n", " ").split(),
                         ['"$TAG"', '"dist/hearthdaoc-deploy-$TAG.tar.gz"', '"dist/hearthdaoc-client-$TAG.zip"'])

    def test_the_release_job_builds_the_mpk_tool_for_the_client_bundle(self):
        i_mpk, _ = self.step(RELEASE_JOB, f"dotnet build {MPK_PROJECT} -c Release")
        i_bundles, bundles = self.step(RELEASE_JOB, "deploy/build_bundles.sh")
        self.assertLess(self.uses(RELEASE_JOB, "actions/setup-dotnet@v4"), i_mpk)
        self.assertLess(i_mpk, i_bundles)
        self.assertEqual(bundles["env"]["HDC_MPK_TOOL"], MPK_TOOL)

    def test_the_release_notes_credit_offlinedaocs_splash_art(self):
        # In the --notes string, which gh puts before the notes it generates from the merged PRs.
        _, create = self.step(RELEASE_JOB, "gh release create")
        notes = create["run"].split('--notes "', 1)[1].split('"', 1)[0]
        self.assertIn("The client's loading splash is OfflineDAoC's art", notes)


if __name__ == "__main__":
```

`deploy/tests/test_release_auto.py` gets two `ReleaseWorthyTests` cases: what the client bundle carries or is built from makes a release, including upstream's MPK tool; the generator alone does not. Replace:

```python
                     "source/development-tools/OpenDAoC-Core/x.cs", "source/tools/OfflineDaoc.Launcher/MainForm.cs"):
            with self.subTest(path=path):
                self.assertFalse(rt.release_worthy([path]))
```

with:

```python
                     "source/development-tools/OpenDAoC-Core/x.cs", "source/tools/OfflineDaoc.Launcher/MainForm.cs"):
            with self.subTest(path=path):
                self.assertFalse(rt.release_worthy([path]))

    def test_client_patches_ship_through_the_client_bundle(self):
        # build_bundles.sh bundles the patch set and both appliers, and packs splash.mpk from branding/splash.png
        # with build_splash_mpk.py (which imports splash_entry.py and mpk.py) and upstream's MPK tool.
        for path in ("client/patches/classic-creation.json", "client/patches/apply_patches.py",
                     "client/patches/patchset.py", "client/windows/patch-client.ps1",
                     "client/patches/branding/splash.png", "client/patches/branding/build_splash_mpk.py",
                     "client/patches/splash_entry.py", "client/patches/mpk.py",
                     "source/tools/OfflineDaoc.Mpk/Program.cs", "source/tools/OfflineDaoc.Mpk/OfflineDaoc.Mpk.csproj"):
            with self.subTest(path=path):
                self.assertTrue(rt.release_worthy([path]))

    def test_the_client_patch_set_generator_alone_does_not(self):
        # What it makes, classic-creation.json and branding/splash.png, is committed and ships; CI fails when
        # classic-creation.json differs from a rebuild.
        for path in ("client/patches/build.py", "client/patches/pe.py", "client/patches/classdata.py",
                     "client/patches/src/baseclass.asm", "client/patches/src/base_classes.py",
                     "client/patches/branding/reletter_splash.py"):
            with self.subTest(path=path):
                self.assertFalse(rt.release_worthy([path]))
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python3 -m unittest discover -s deploy/tests -t deploy -k ClientPatchWorkflowTests -k ReleaseWorthyTests`

Expected: 13 failures in 10 tests (each wrong path is its own subtest). `test_nothing_fetched_is_published` and the two existing `ReleaseWorthyTests` already pass:
```
FF..FFFFFFF.FFFF
FAIL: test_client_patches_ship_through_the_client_bundle (tests.test_release_auto.ReleaseWorthyTests.test_client_patches_ship_through_the_client_bundle) (path='source/tools/OfflineDaoc.Mpk/Program.cs')
AssertionError: False is not true
FAIL: test_client_patches_ship_through_the_client_bundle (tests.test_release_auto.ReleaseWorthyTests.test_client_patches_ship_through_the_client_bundle) (path='source/tools/OfflineDaoc.Mpk/OfflineDaoc.Mpk.csproj')
AssertionError: False is not true
FAIL: test_the_client_patch_set_generator_alone_does_not (tests.test_release_auto.ReleaseWorthyTests.test_the_client_patch_set_generator_alone_does_not) (path='client/patches/build.py')
AssertionError: True is not false
... (the same for client/patches/pe.py, classdata.py, src/baseclass.asm, src/base_classes.py and branding/reletter_splash.py)
FAIL: test_a_rebuild_of_the_patch_set_is_compared_with_the_committed_one (tests.test_workflows.ClientPatchWorkflowTests.test_a_rebuild_of_the_patch_set_is_compared_with_the_committed_one)
AssertionError: 0 != 1 : test-build-publish: steps that run 'python3 -m unittest discover -s client/patches/tests -t client/patches -v'
FAIL: test_the_client_patch_tests_get_nasm_the_mpk_tool_pwsh_and_the_world (tests.test_workflows.ClientPatchWorkflowTests.test_the_client_patch_tests_get_nasm_the_mpk_tool_pwsh_and_the_world)
AssertionError: 0 != 1 : test-build-publish: steps that run 'dotnet build source/tools/OfflineDaoc.Mpk/OfflineDaoc.Mpk.csproj -c Release'
FAIL: test_the_fetch_step_gets_exactly_the_patched_files_into_runner_temp (tests.test_workflows.ClientPatchWorkflowTests.test_the_fetch_step_gets_exactly_the_patched_files_into_runner_temp)
AssertionError: 0 != 1 : test-build-publish: steps that run 'odaoc_fetch.py'
FAIL: test_the_release_job_builds_the_mpk_tool_for_the_client_bundle (tests.test_workflows.ClientPatchWorkflowTests.test_the_release_job_builds_the_mpk_tool_for_the_client_bundle)
AssertionError: 0 != 1 : release-assets: steps that run 'dotnet build source/tools/OfflineDaoc.Mpk/OfflineDaoc.Mpk.csproj -c Release'
FAIL: test_the_release_notes_credit_offlinedaocs_splash_art (tests.test_workflows.ClientPatchWorkflowTests.test_the_release_notes_credit_offlinedaocs_splash_art)
AssertionError: "The client's loading splash is OfflineDAoC's art" not found in 'HearthDAoC, an unofficial fork of shadowofze/OfflineDAoC (upstream $upstream). Image: ghcr.io/lometur/hearthdaoc:$TAG.\nServer: \\`./hdc update\\` (or HANDOFF.md in the deploy bundle). Players: README.md in the client bundle.'
----------------------------------------------------------------------
Ran 10 tests in 0.070s

FAILED (failures=13)
```

- [ ] **Step 3: Write minimal implementation (the workflow and release_tag.py)**

Make three edits in `.github/workflows/server-image.yml`. Keep it ASCII.

1. In the test job, replace:

```yaml
      - name: Install test tools
        run: sudo apt-get update && sudo apt-get install -y --no-install-recommends sqlite3 bubblewrap

      - name: Fetch a clean classic world for tests (database only)
        run: python3 deploy/bin/init_world.py --lock deploy/upstream.lock --data "$RUNNER_TEMP/world" --edition classic --skip-navmesh

      - name: Build Linux CLIs
        run: tools/linux/build.sh "$RUNNER_TEMP/tools"

      - name: Unit tests
        env:
          HDC_TEST_WORLD: ${{ runner.temp }}/world/world/opendaoc.sqlite3.db
          HDC_TOOLS: ${{ runner.temp }}/tools
        run: |
          python3 -m unittest discover -s tools/linux/tests -t tools/linux -v
          python3 -m unittest discover -s deploy/tests -t deploy -v
          python3 -m unittest discover -s client/tests -t client -v
```

with:

```yaml
      # nasm assembles the client patch set's code cave (client/patches/src/baseclass.asm).
      - name: Install test tools
        run: sudo apt-get update && sudo apt-get install -y --no-install-recommends sqlite3 bubblewrap nasm

      - name: Fetch a clean classic world for tests (database only)
        run: python3 deploy/bin/init_world.py --lock deploy/upstream.lock --data "$RUNNER_TEMP/world" --edition classic --skip-navmesh

      # The files client/patches/classic-creation.json patches (the classic edition's game.dll and two
      # pregame files), from the pinned release and verified like every download, for the client patch
      # tests and the patch-set check. They are EA files: they stay in $RUNNER_TEMP, outside the checkout
      # (so never in the image's build context), for this run only, and no step uploads them.
      - name: Fetch the classic client files the patch set patches (tests only, never published)
        run: |
          lock=deploy/upstream.lock
          app="$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1]))["client_prefix"])' "$lock")"
          dll="$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1]))["editions"]["classic"]["game_dll"])' "$lock")"
          python3 tools/linux/odaoc_fetch.py --lock "$lock" extract "$dll" "$RUNNER_TEMP/classic-client/game.dll"
          for f in pregame/character_customize_stats.xml pregame/splash.mpk; do
            python3 tools/linux/odaoc_fetch.py --lock "$lock" extract "$app$f" "$RUNNER_TEMP/classic-client/$f"
          done

      - name: Build upstream's MPK tool (packs the client's splash.mpk)
        run: dotnet build source/tools/OfflineDaoc.Mpk/OfflineDaoc.Mpk.csproj -c Release --nologo

      - name: Build Linux CLIs
        run: tools/linux/build.sh "$RUNNER_TEMP/tools"

      - name: Unit tests
        env:
          HDC_TEST_WORLD: ${{ runner.temp }}/world/world/opendaoc.sqlite3.db
          HDC_TOOLS: ${{ runner.temp }}/tools
          HDC_CLIENT_FILES: ${{ runner.temp }}/classic-client
          HDC_MPK_TOOL: ${{ github.workspace }}/source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll
          HDC_PWSH: pwsh  # preinstalled on GitHub's Ubuntu runners; named, so the PowerShell cases can't skip
        run: |
          python3 -m unittest discover -s tools/linux/tests -t tools/linux -v
          python3 -m unittest discover -s deploy/tests -t deploy -v
          python3 -m unittest discover -s client/tests -t client -v
          python3 -m unittest discover -s client/patches/tests -t client/patches -v

      # classic-creation.json is generated by client/patches/build.py and committed. It must be exactly
      # what the generator makes from the pinned release's files and this commit's sources.
      - name: Client patch set matches a rebuild
        env:
          HDC_TEST_WORLD: ${{ runner.temp }}/world/world/opendaoc.sqlite3.db
          HDC_CLIENT_FILES: ${{ runner.temp }}/classic-client
          HDC_MPK_TOOL: ${{ github.workspace }}/source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll
        run: |
          python3 -B client/patches/branding/build_splash_mpk.py --mpk-tool "$HDC_MPK_TOOL" --out "$RUNNER_TEMP/splash.mpk"
          python3 -B client/patches/build.py --client "$HDC_CLIENT_FILES" --world-db "$HDC_TEST_WORLD" \
            --server-src source/server --splash-mpk "$RUNNER_TEMP/splash.mpk" --out "$RUNNER_TEMP/classic-creation.json"
          if ! diff -u client/patches/classic-creation.json "$RUNNER_TEMP/classic-creation.json"; then
            echo "::error file=client/patches/classic-creation.json::out of date: rebuild it with client/patches/build.py (docs/fork/FORK.md, Client patches)"
            exit 1
          fi
```

2. In the `release-assets` job, give the bundle step the MPK tool. Replace:

```yaml
      - uses: actions/checkout@v4
      - name: Build bundles (no EA files)
        run: deploy/build_bundles.sh "$TAG" dist
```

with:

```yaml
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"
      - name: Build upstream's MPK tool (packs the client bundle's splash.mpk)
        run: dotnet build source/tools/OfflineDaoc.Mpk/OfflineDaoc.Mpk.csproj -c Release --nologo
      - name: Build bundles (no EA files)
        env:
          HDC_MPK_TOOL: source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll
        run: deploy/build_bundles.sh "$TAG" dist
```

3. In the same job's "Create release (and its tag on this commit)" step, add the credit as the last line of the `--notes` string. Replace:

```yaml
          Server: \`./hdc update\` (or HANDOFF.md in the deploy bundle). Players: README.md in the client bundle."
```

with:

```yaml
          Server: \`./hdc update\` (or HANDOFF.md in the deploy bundle). Players: README.md in the client bundle.
          The client's loading splash is OfflineDAoC's art, re-lettered HEARTH DAoC (Cinzel font, SIL Open Font License). Credit for the artwork goes to shadowofze/OfflineDAoC."
```

Then, in `deploy/release_tag.py`, replace:

```python
# Files that end up in the image (deploy/Dockerfile's COPY lines) or the bundles (deploy/build_bundles.sh).
# Tests, docs, CI and this script don't need a release; the only shipped Markdown files are SHIPPED_DOCS.
RELEASE_PATHS = ("source/server/", "source/tools/OfflineDaoc.Launcher/BotCharacterGenerator.cs",
                 "source/tools/OfflineDaoc.ProgressImport/", "tools/linux/", "deploy/", "client/", ".dockerignore")
NOT_RELEASE_PATHS = ("source/server/docs/", "deploy/release_tag.py")
```

with:

```python
# Files that end up in the image (deploy/Dockerfile's COPY lines) or the bundles (deploy/build_bundles.sh, which
# also packs the client's splash.mpk with upstream's MPK tool, source/tools/OfflineDaoc.Mpk).
# Tests, docs, CI and this script don't need a release; the only shipped Markdown files are SHIPPED_DOCS. Nor
# does the client patch set's generator: what it makes (client/patches/classic-creation.json, branding/splash.png)
# is committed and ships, and CI fails when classic-creation.json differs from a rebuild.
RELEASE_PATHS = ("source/server/", "source/tools/OfflineDaoc.Launcher/BotCharacterGenerator.cs",
                 "source/tools/OfflineDaoc.ProgressImport/", "source/tools/OfflineDaoc.Mpk/", "tools/linux/",
                 "deploy/", "client/", ".dockerignore")
NOT_RELEASE_PATHS = ("source/server/docs/", "deploy/release_tag.py",
                     "client/patches/build.py", "client/patches/pe.py", "client/patches/classdata.py",
                     "client/patches/src/", "client/patches/branding/reletter_splash.py")
```

Why it looks like this:
- The fetch step reads the archive paths from `deploy/upstream.lock`, so a lock update with new edition paths needs no workflow change. It uses `$RUNNER_TEMP`, never `${{ runner.temp }}`, inside `run`, so `ClientPatchWorkflowTests` can run the script as it is.
- `HDC_MPK_TOOL` is absolute in the test job (`github.workspace`), because the tests run from different folders. `build_bundles.sh` makes a relative one absolute itself (Task 7).
- `python3 -B` keeps the rebuild from writing `__pycache__` into the checkout.
- The rebuild step's `diff -u` prints what differs; the `::error` line makes GitHub show the file in the run summary.
- The release job keeps PR #70's order: the bundles are built once, in "Build bundles (no EA files)", before "Create release" runs. Only that step gets `HDC_MPK_TOOL`. The credit is one more line inside the existing `--notes` string, which gh puts before the notes it generates. It has no `$`, backquote, backslash or double quote, so bash prints it as written.
- `release_tag.py`: `source/tools/OfflineDaoc.Mpk/` now affects what ships, so a change to it makes a release. Its project's reference, `source/server/CoreBase`, is already under `source/server/`. The generator files go to `NOT_RELEASE_PATHS`, for the reason given in the facts above. `mpk.py` and `splash_entry.py` stay release-worthy because `build_splash_mpk.py` imports them when the bundles are built. New generator files would count as shipped until they are listed here. That is the safe side: an extra release, never a missing one.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `ruby -ryaml -e 'YAML.load_file(ARGV[0])["jobs"].each { |j, s| puts "#{j}: #{s["steps"].map { |x| x["name"] || x["uses"] }.join(" | ")}" }' .github/workflows/server-image.yml`

Expected (the YAML parses; the steps in order):
```
test-build-publish: actions/checkout@v4 | Release to publish | actions/setup-dotnet@v4 | Install test tools | Fetch a clean classic world for tests (database only) | Fetch the classic client files the patch set patches (tests only, never published) | Build upstream's MPK tool (packs the client's splash.mpk) | Build Linux CLIs | Unit tests | Client patch set matches a rebuild | Server unit tests for the fork's server changes | CLI integration tests | Build image | Smoke test | Compose and hdc integration test | Publish image (releases only)
release-assets: actions/checkout@v4 | actions/setup-dotnet@v4 | Build upstream's MPK tool (packs the client bundle's splash.mpk) | Build bundles (no EA files) | Create release (and its tag on this commit)
```

Run: `python3 -m unittest discover -s deploy/tests -t deploy -k test_workflows -k test_release_auto -v`

Expected: all 32 tests of both files pass, PR #70's `MergeIsReleaseTests` included. These are the new ones; the other 24 lines are also `... ok` and are left out here:
```
...
test_client_patches_ship_through_the_client_bundle (tests.test_release_auto.ReleaseWorthyTests.test_client_patches_ship_through_the_client_bundle) ... ok
...
test_the_client_patch_set_generator_alone_does_not (tests.test_release_auto.ReleaseWorthyTests.test_the_client_patch_set_generator_alone_does_not) ... ok
test_a_rebuild_of_the_patch_set_is_compared_with_the_committed_one (tests.test_workflows.ClientPatchWorkflowTests.test_a_rebuild_of_the_patch_set_is_compared_with_the_committed_one) ... ok
test_nothing_fetched_is_published (tests.test_workflows.ClientPatchWorkflowTests.test_nothing_fetched_is_published) ... ok
test_the_client_patch_tests_get_nasm_the_mpk_tool_pwsh_and_the_world (tests.test_workflows.ClientPatchWorkflowTests.test_the_client_patch_tests_get_nasm_the_mpk_tool_pwsh_and_the_world) ... ok
test_the_fetch_step_gets_exactly_the_patched_files_into_runner_temp (tests.test_workflows.ClientPatchWorkflowTests.test_the_fetch_step_gets_exactly_the_patched_files_into_runner_temp) ... ok
test_the_release_job_builds_the_mpk_tool_for_the_client_bundle (tests.test_workflows.ClientPatchWorkflowTests.test_the_release_job_builds_the_mpk_tool_for_the_client_bundle) ... ok
test_the_release_notes_credit_offlinedaocs_splash_art (tests.test_workflows.ClientPatchWorkflowTests.test_the_release_notes_credit_offlinedaocs_splash_art) ... ok
...

----------------------------------------------------------------------
Ran 32 tests in 1.262s

OK
```

- [ ] **Step 5: Dry-run the new CI steps on this machine**

This runs the workflow's own scripts, taken from the YAML, the way CI does: `RUNNER_TEMP` is a temp folder, and the CI world is the local clean world (the same file). The fetch downloads about 20 MB from GitHub. `pwsh` must be on `PATH` (see "Before Step 1"), because CI names it `pwsh`. Run the three blocks in one shell: they share `$T` and `step`. The last block deletes `$T`, and with it the fetched files. Nothing here publishes anything. The "Create release" step is only read: the dry run prints its `--notes` argument with `printf` and never runs `gh`.

Run:
```bash
T=$(mktemp -d); W=.github/workflows/server-image.yml
step() {  # print step "$2" of job "$1" as a script: its env (runner.temp = $T) and its run
  ruby -ryaml -rshellwords -e 's = YAML.load_file(ARGV[0])["jobs"][ARGV[1]]["steps"].find { |x| x["name"] == ARGV[2] }
    (s["env"] || {}).each { |k, v| puts "export #{k}=#{Shellwords.escape(v.gsub("${{ runner.temp }}", ARGV[3]).gsub("${{ github.workspace }}", Dir.pwd))}" }
    puts s["run"]' "$W" "$1" "$2" "$T"
}
# CI's world step downloads this same database; link the local copy instead.
mkdir -p "$T/world/world" && ln -s ~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db "$T/world/world/opendaoc.sqlite3.db"
step test-build-publish "Fetch the classic client files the patch set patches (tests only, never published)" > "$T/fetch.sh"
step test-build-publish "Build upstream's MPK tool (packs the client's splash.mpk)" > "$T/mpk.sh"
{ step test-build-publish "Unit tests" | grep '^export '; echo "python3 -m unittest discover -s client/patches/tests -t client/patches"; } > "$T/patch-tests.sh"
step test-build-publish "Client patch set matches a rebuild" > "$T/rebuild.sh"
RUNNER_TEMP="$T" bash -e "$T/fetch.sh" && (cd "$T/classic-client" && sha256sum game.dll pregame/*)
RUNNER_TEMP="$T" bash -e "$T/mpk.sh" | grep -E 'Error\(s\)'
RUNNER_TEMP="$T" bash -e "$T/patch-tests.sh" 2>&1 | tail -3
RUNNER_TEMP="$T" bash -e "$T/rebuild.sh" | sed "s|$T|\$T|"; echo "rebuild exit ${PIPESTATUS[0]}"
git status --short
```

Expected (the patch tests take about a minute; the fetched files have the patch set's "before" hashes; `git status` lists only this task's four files):
```
ok editions/0.34-no-custom-class/runtime/client-opendaoc/app/game.dll
ok runtime/client-opendaoc/app/pregame/character_customize_stats.xml
ok runtime/client-opendaoc/app/pregame/splash.mpk
67dcf68a37b95a93946a943b99d5e19b4a03e08cd6469275e25c7b909de21e99  game.dll
08e6f7c3b4fdb546b203abd8a952c84b60105b5e8b189fb33277a40be0902364  pregame/character_customize_stats.xml
f24460d2b064b86527b1800940d6d80c26b67ca3531d91b87c3b7a4d38455a9e  pregame/splash.mpk
    0 Error(s)
Ran 175 tests in 71.829s

OK
Wrote $T/splash.mpk (8 x 1024x768 TGA)
Wrote $T/classic-creation.json: game.dll, pregame/character_customize_stats.xml, pregame/splash.mpk
rebuild exit 0
 M .github/workflows/server-image.yml
 M deploy/release_tag.py
 M deploy/tests/test_release_auto.py
 M deploy/tests/test_workflows.py
```

Now check that the rebuild step fails on a stale patch set, then put the file back:

Run:
```bash
sed -i 's/"name": "classic-creation"/"name": "stale"/' client/patches/classic-creation.json
RUNNER_TEMP="$T" bash -e "$T/rebuild.sh" 2>&1 | sed "s|$T|\$T|" | grep -v '^Wrote'; echo "rebuild exit ${PIPESTATUS[0]}"
LC_ALL=C git checkout client/patches/classic-creation.json
```

Expected:
```
--- client/patches/classic-creation.json	...
+++ $T/classic-creation.json	...
@@ -1,6 +1,6 @@
 {
  "format": 1,
- "name": "stale",
+ "name": "classic-creation",
  "client": "OfflineDAoC 0.34 classic",
  "files": [
   {
::error file=client/patches/classic-creation.json::out of date: rebuild it with client/patches/build.py (docs/fork/FORK.md, Client patches)
rebuild exit 1
Updated 1 path from the index
```

Then the release job's new steps and the release notes. `r-notes.sh` holds only the step's `upstream=` line and its `--notes` argument, given to `printf`; the `0` before the notes is `grep -c` confirming that it has no `gh release` line:

Run:
```bash
step release-assets "Build upstream's MPK tool (packs the client bundle's splash.mpk)" > "$T/r-mpk.sh"
step release-assets "Build bundles (no EA files)" > "$T/r-bundles.sh"
# The release notes as gh would get them, without running gh: the step's upstream= line and its --notes
# argument, printed. r-create.sh is only read, never run: it publishes a release.
step release-assets "Create release (and its tag on this commit)" > "$T/r-create.sh"
{ grep '^upstream=' "$T/r-create.sh"; sed -n -e 's/^ *--notes /printf "%s\\n" /' -e '/^printf/,$p' "$T/r-create.sh"; } > "$T/r-notes.sh"
export TAG=v0.34b-hearth.99
bash -e "$T/r-mpk.sh" | grep -E 'Error\(s\)'
bash -e "$T/r-bundles.sh" | sed "s|$PWD/||g"
unzip -Z1 "dist/hearthdaoc-client-$TAG.zip" | grep -v '/$' | wc -l
grep -c 'gh release' "$T/r-notes.sh"; bash -e "$T/r-notes.sh"
rm -rf dist "$T"; unset TAG; git status --short
```

Expected (14 files in the client bundle, as in Task 7; the notes' last line is the credit):
```
    0 Error(s)
Built dist/hearthdaoc-deploy-v0.34b-hearth.99.tar.gz and dist/hearthdaoc-client-v0.34b-hearth.99.zip
14
0
HearthDAoC, an unofficial fork of shadowofze/OfflineDAoC (upstream 0.34b). Image: ghcr.io/lometur/hearthdaoc:v0.34b-hearth.99.
Server: `./hdc update` (or HANDOFF.md in the deploy bundle). Players: README.md in the client bundle.
The client's loading splash is OfflineDAoC's art, re-lettered HEARTH DAoC (Cinzel font, SIL Open Font License). Credit for the artwork goes to shadowofze/OfflineDAoC.
 M .github/workflows/server-image.yml
 M deploy/release_tag.py
 M deploy/tests/test_release_auto.py
 M deploy/tests/test_workflows.py
```

- [ ] **Step 6: Commit**

```bash
git add .github/workflows/server-image.yml deploy/tests/test_workflows.py deploy/release_tag.py deploy/tests/test_release_auto.py
git commit -m "ci: client patch tests on the real classic files; release job builds splash.mpk

The test job installs nasm, fetches the three files the patch set
patches (the classic edition's game.dll, character_customize_stats.xml
and splash.mpk) from the pinned release with odaoc_fetch.py into the
runner's temp folder, builds upstream's MPK tool and runs
client/patches/tests with them and pwsh. Then it rebuilds
classic-creation.json and fails if the committed one differs. The
fetched EA files stay outside the checkout and are never uploaded.
The release job builds the MPK tool for build_bundles.sh, and the
release notes credit OfflineDAoC for the splash art.
ClientPatchWorkflowTests in deploy/tests/test_workflows.py check all of
this; they run the fetch step against a fake release.

release_tag.py: a change to upstream's MPK tool (it packs the client
bundle's splash.mpk) makes a release; the patch set's generator alone
does not, because what it makes is committed and ships.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 7: Document the client patches in `docs/fork/FORK.md`**

Make four edits in `docs/fork/FORK.md`. PR #70 rewrote "Syncing with upstream" (sync through a PR branch, never GitHub's **Sync fork** button, because merging is releasing) and "Releases". Keep both as they are, apart from the one sync step and the asset sentence below.

1. In the "What the fork changes" table, replace:

```markdown
| Player setup | `client/` | none |
```

with:

```markdown
| Player setup | `client/` | none |
| Classic character creation and splash (client patch set) | `client/patches/`, `client/windows/patch-client.*` | none: patches each player's own client files (see Client patches) |
```

2. Add the "Client patches" section before "Syncing with upstream". Replace:

```markdown
## Syncing with upstream
```

with:

````markdown
## Client patches

The patch set `client/patches/classic-creation.json` gives the OfflineDAoC 0.34 classic client a classic
character creation screen and the HearthDAoC loading splash (sub-project 2, see
[its spec](specs/2026-10-06-classic-character-creation-design.md)). It holds only SHA-256 hashes, byte and text edits
and our own code, never an EA file. On Linux `setup.sh` applies it (`client/patches/apply_patches.py`); on
Windows players run `patch-client.bat` (`client/windows/patch-client.ps1`, same rules). Both refuse any other
client file, such as the b edition's `game.dll`, and then change nothing. Both keep each original as
`<file>.hearthdaoc-orig` and put it back with `--restore` / `-Restore`, but only over the patched file: when a
file has changed since it was patched (for example a newer upstream client), the restore says so and changes
nothing.

| What is patched | Where | Why |
|---|---|---|
| Auto-assign stops after its reset: race base stats and 30 points to place (P1) | `game.dll`, VA `0x59C0B2` | Classic stat points (#39) |
| Continue checks unspent points for new characters too (P2) | `game.dll`, VA `0x59A853` (28 bytes) | Creation can't finish until all 30 are placed (#39) |
| The attributes window starts open (P3) | `game.dll`, VA `0x59C574` | The points are placed right away (#39) |
| A hook calls our code cave after class registration | `game.dll`, VA `0x5B0051`; a new last section `.hdcc` (with the section count, `SizeOfCode`, `SizeOfImage` and checksum) | The cave hides the full classes and the races after Shrouded Isles, and registers the 15 base classes with their descriptions (#55) |
| The Optimize button is removed | `pregame/character_customize_stats.xml` (ControlId 1021) | No auto-assign (#39) |
| The loading splash is replaced by our `splash.mpk` | `pregame/splash.mpk` | HEARTH DAoC lettering (#54) |

Sources, in `client/patches/`: `build.py` (the generator: stat-flow bytes, XML edit, cave section and hook),
`src/baseclass.asm` (the cave, nasm), `classdata.py` (base classes and races from the server's class files and
the world's `disabled_classes`), `src/base_classes.py` (descriptions and highlighted stats), `branding/` (the
splash) and the Linux applier (`apply_patches.py`, `patchset.py`).

The splash is OfflineDAoC's artwork (upstream keeps it as
`source/tools/OfflineDaoc.Launcher/Assets/offline-daoc-client-splash.mpk`), re-lettered "HEARTH DAoC" in Cinzel
(SIL Open Font License) by `branding/reletter_splash.py`. Credit for the art goes to OfflineDAoC. The bundles get
`splash.mpk` packed from `branding/splash.png` by upstream's MPK tool (`source/tools/OfflineDaoc.Mpk`, .NET), so
`deploy/build_bundles.sh` needs `HDC_MPK_TOOL` (or `--deploy-only` for the deploy bundle alone). A change to that
tool makes a release (`deploy/release_tag.py`), like a change to the files the bundles carry.

The base-class list is generated for the shipped classic world's `disabled_classes`, with Disciple enabled as
`deploy/bin/world_fixes.py` does. On a server that disables more classes, a base class whose full classes are
all disabled is still offered, and the server refuses it at creation. Rebuilding the patch set with that
world's database (`--world-db`) fixes it.

**Rebuilding the patch set.** `classic-creation.json` is generated, never edited by hand. Rebuild it after a
change to anything above, to the server's class files or to `deploy/upstream.lock`. CI rebuilds it on every run
from the pinned release's files and fails ("Client patch set matches a rebuild") when the committed file
differs. A change to the generator alone (`build.py`, `pe.py`, `classdata.py`, `src/`,
`branding/reletter_splash.py`) makes no release; the rebuilt `classic-creation.json` or `branding/splash.png`
does. You need nasm (`sudo apt install nasm`) and the .NET 10 SDK. From the repository root:

```bash
dotnet build source/tools/OfflineDaoc.Mpk/OfflineDaoc.Mpk.csproj -c Release
python3 client/patches/branding/build_splash_mpk.py --mpk-tool source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll
c="$(mktemp -d)"  # EA files from the pinned release, verified; never commit or share them
python3 tools/linux/odaoc_fetch.py --lock deploy/upstream.lock extract editions/0.34-no-custom-class/runtime/client-opendaoc/app/game.dll "$c/game.dll"
python3 tools/linux/odaoc_fetch.py --lock deploy/upstream.lock extract runtime/client-opendaoc/app/pregame/character_customize_stats.xml "$c/pregame/character_customize_stats.xml"
python3 tools/linux/odaoc_fetch.py --lock deploy/upstream.lock extract runtime/client-opendaoc/app/pregame/splash.mpk "$c/pregame/splash.mpk"
python3 tools/linux/odaoc_fetch.py --lock deploy/upstream.lock extract editions/0.34-no-custom-class/runtime/data/opendaoc.sqlite3.db "$c/world.db"
python3 client/patches/build.py --client "$c" --world-db "$c/world.db" --server-src source/server \
  --splash-mpk client/patches/splash.mpk --out client/patches/classic-creation.json
HDC_CLIENT_FILES="$c" HDC_TEST_WORLD="$c/world.db" python3 -m unittest discover -s client/patches/tests -t client/patches
rm -rf "$c"
```

The archive paths are the lock's `editions.classic` and `client_prefix` (the world database is about 90 MB).
The tests skip the real-file cases without `HDC_CLIENT_FILES` and `HDC_TEST_WORLD`, the MPK cases without
`HDC_MPK_TOOL` (the `OfflineDaoc.Mpk.dll` above) and the PowerShell cases without `pwsh` (or `HDC_PWSH`). CI
sets all of them; `ClientPatchWorkflowTests` in `deploy/tests/test_workflows.py` (needs ruby) keep the workflow
that way. `setup.sh` run from a checkout applies `client/patches/` and needs the git-ignored
`client/patches/splash.mpk` built above.

If the pinned release's classic `game.dll` changes, `build.py` refuses it until the patch sites in `build.py`,
`classdata.py` and `src/baseclass.asm` are found again in the new file. Until then CI fails and players with
the new client keep the standard creation screen.

## Syncing with upstream
````

3. In "Syncing with upstream", add the rebuild step on the sync branch, before the PR is opened. Replace:

```markdown
   and `PACKAGE MANIFEST.sha256`, on the same branch.
3. Push and open a PR; its CI builds and tests the image.
4. Merging it releases `v<upstream-version>-hearth.<n>` (a new upstream version restarts at `.1`).
5. On the server, back up, then follow `deploy/HANDOFF.md` → "Upgrading".
```

with:

```markdown
   and `PACKAGE MANIFEST.sha256`, on the same branch.
3. Rebuild the client patch set (see Client patches above) and commit it on the same branch if it changed.
4. Push and open a PR; its CI builds and tests the image.
5. Merging it releases `v<upstream-version>-hearth.<n>` (a new upstream version restarts at `.1`).
6. On the server, back up, then follow `deploy/HANDOFF.md` → "Upgrading".
```

4. At the end of "Releases", replace:

```markdown
attaches two assets: `hearthdaoc-deploy-<tag>.tar.gz` (compose file, `.env.example`, `hdc`, handoff) and
`hearthdaoc-client-<tag>.zip` (player scripts). Neither contains EA game files.
```

with:

```markdown
attaches two assets: `hearthdaoc-deploy-<tag>.tar.gz` (compose file, `.env.example`, `hdc`, handoff) and
`hearthdaoc-client-<tag>.zip` (player scripts, the client patch set with both appliers, and our `splash.mpk`).
Neither contains EA game files. The release notes credit OfflineDAoC for the splash art.
```

Run: `grep -n 'Sync fork\|Release it\|test_workflow\.py' docs/fork/FORK.md`

Expected (only PR #70's warning names the button; nothing names the old release step or a `test_workflow.py`):
```
102:1. Sync through a PR, not GitHub's **Sync fork** button: that commits straight to `main`, and merging is
```

- [ ] **Step 8: Run the documented rebuild commands as written**

The "Rebuilding the patch set" commands must work when pasted. Run them straight from `FORK.md`. They download about 110 MB (the world database is 87 MB) into a temp folder that the last command deletes.

Run:
```bash
S=$(mktemp) && awk '/^\*\*Rebuilding the patch set/ {f = 1} f && /^```bash$/ {g = 1; next} g && /^```$/ {exit} g' docs/fork/FORK.md > "$S"
HDC_MPK_TOOL=$PWD/source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll bash -e "$S" 2>&1 | grep -E '^(Build succeeded|Wrote|ok |Ran |OK|FAILED)' | sed "s|$PWD/||"
rm -f "$S"; git status --short
```

Expected (the rebuilt patch set equals the committed one, so `git status` shows only this task's edit; nothing is skipped, because the run sets `HDC_MPK_TOOL` and `pwsh` is on `PATH`):
```
Build succeeded.
Wrote client/patches/splash.mpk (8 x 1024x768 TGA)
ok editions/0.34-no-custom-class/runtime/client-opendaoc/app/game.dll
ok runtime/client-opendaoc/app/pregame/character_customize_stats.xml
ok runtime/client-opendaoc/app/pregame/splash.mpk
ok editions/0.34-no-custom-class/runtime/data/opendaoc.sqlite3.db
Wrote client/patches/classic-creation.json: game.dll, pregame/character_customize_stats.xml, pregame/splash.mpk
Ran 175 tests in 72.907s
OK
 M docs/fork/FORK.md
```

- [ ] **Step 9: Explain the classic creation screen in `client/README.md`**

Make four edits in `client/README.md`.

1. Add the "Classic character creation" section after the password paragraph. Replace:

```markdown
password appears on the game's command line.

## Linux (Steam / Proton)
```

with:

```markdown
password appears on the game's command line.

## Classic character creation

HearthDAoC patches your own copy of the client so that creating a character works as in classic Dark
Age of Camelot. This needs the `classic` edition (OfflineDAoC 0.34 classic).

- You pick a **base class**, such as Fighter, Mage, Viking or Guardian. Its description names the
  classes it leads to; your trainer makes you one of them at level 5.
- Each base class offers only the races that can become one of those classes. Half Ogre, Frostalf,
  Shar and the Minotaurs aren't offered.
- You start at your race's base stats with **30 points to place** yourself. The Optimize button is
  gone, and Continue says "You must use all your points!" until all 30 are placed.
- The loading screens say **HEARTH DAoC**.

The patch changes only `game.dll` and two files in `pregame`, and keeps each original as
`<file>.hearthdaoc-orig`. With any other client, such as the `b` edition, it changes nothing and
says so. The game then works with the standard creation screen.

## Linux (Steam / Proton)
```

2. In the Linux steps, replace:

```markdown
   This builds a separate client in `~/Games/HearthDAoC` and downloads about 45 MB of
   OfflineDAoC files, each checked against the official release.
```

with:

```markdown
   This builds a separate client in `~/Games/HearthDAoC`, downloads about 45 MB of OfflineDAoC
   files, each checked against the official release, and applies HearthDAoC's client patches (see
   Classic character creation). If it warns that the client was set up without HearthDAoC's patches,
   your client isn't the one this release supports (for example the `b` edition); the game still
   works, with the standard creation screen.
```

3. In the Windows steps, replace:

```markdown
   your first login creates the account.

## Windows

1. Install the **official** OfflineDAoC release for the server's edition (upstream's
   `DOWNLOAD-AND-PLAY-v0.34.cmd` for `classic`, `DOWNLOAD-AND-PLAY-v0.34b.cmd` for `b`).
2. Turn on the Windows feature **.NET Framework 3.5 (includes .NET 2.0 and 3.0)**; the game's
   `connect.exe` needs it.
3. Copy `connect-hearthdaoc.bat` into the install's `runtime\client-opendaoc\app` folder and run it.
   It asks for the server address, account and password once and saves them in `hearthdaoc.cfg`.
```

with:

```markdown
   your first login creates the account.

To go back to the standard creation screen, run
`python3 patches/apply_patches.py --client ~/Games/HearthDAoC/client --restore` in the
`hearthdaoc-client-<version>` folder. The same command without `--restore`, or running `setup.sh`
again, patches the client again.

## Windows

1. Install the **official** OfflineDAoC release for the server's edition (upstream's
   `DOWNLOAD-AND-PLAY-v0.34.cmd` for `classic`, `DOWNLOAD-AND-PLAY-v0.34b.cmd` for `b`).
2. Turn on the Windows feature **.NET Framework 3.5 (includes .NET 2.0 and 3.0)**; the game's
   `connect.exe` needs it.
3. Copy everything in the bundle's `windows` folder into the install's `runtime\client-opendaoc\app`
   folder: `connect-hearthdaoc.bat`, `patch-client.bat`, `patch-client.ps1` and the `patches` folder.
4. Double-click `patch-client.bat` for the classic character creation screen and the HEARTH DAoC
   loading screens (`classic` edition). It says what it patched and waits for a key. Run it again
   whenever something puts the original files back (OfflineDAoC's own launcher or a repair of the
   install may). To go back to the standard screen, open a Command Prompt in that folder and run
   `patch-client.bat -Restore`.
5. Run `connect-hearthdaoc.bat`.
   It asks for the server address, account and password once and saves them in `hearthdaoc.cfg`.
```

4. At the end of the file, replace:

```markdown
the Steam launch options of the game to `XMODIFIERS=@im=none %command%`. Tapping W once releases it.
```

with:

```markdown
the Steam launch options of the game to `XMODIFIERS=@im=none %command%`. Tapping W once releases it.

## Credits

The loading splash is OfflineDAoC's artwork ([shadowofze/OfflineDAoC](https://github.com/shadowofze/OfflineDAoC)),
re-lettered "HEARTH DAoC" in the Cinzel font (SIL Open Font License). Credit for the art goes to
OfflineDAoC.
```

- [ ] **Step 10: Check the README's patch steps on scratch copies**

The Linux commands run from an unpacked bundle; the Windows layout is `windows/` copied into a client folder and `patch-client.ps1` run without `-Client` (the `.bat` only adds a pause). Only copies of the three client files are touched.

Run:
```bash
C=~/Games/HearthDAoC/client; T=$(mktemp -d)
HDC_MPK_TOOL=source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll deploy/build_bundles.sh v0.34b-hearth.99 "$T/dist" >/dev/null \
  && unzip -q "$T/dist/hearthdaoc-client-v0.34b-hearth.99.zip" -d "$T" && B="$T/hearthdaoc-client-v0.34b-hearth.99"
# Linux: the README's commands, in the unpacked bundle folder
mkdir -p "$T/linux/pregame" && cp "$C/game.dll" "$T/linux/" && cp "$C/pregame/character_customize_stats.xml" "$C/pregame/splash.mpk" "$T/linux/pregame/"
(cd "$B" && python3 patches/apply_patches.py --client "$T/linux" && python3 patches/apply_patches.py --client "$T/linux" --restore); echo "linux exit $?"
# Windows: everything in windows/ copied into the client folder, then the script without -Client
mkdir -p "$T/app/pregame" && cp -r "$B/windows/." "$T/app/" && cp "$C/game.dll" "$T/app/" && cp "$C/pregame/character_customize_stats.xml" "$C/pregame/splash.mpk" "$T/app/pregame/"
PS="${HDC_PWSH:-pwsh} -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $T/app/patch-client.ps1"
$PS; echo "windows exit $?"; $PS -Restore; echo "windows restore exit $?"
ls "$T/linux" "$T/app" | sed "s|$T|\$T|"; sha256sum "$T/app/game.dll" | cut -c1-16; rm -rf "$T"
```

Expected (each `.hearthdaoc-orig` is gone after the restore, and `game.dll` is the stock file again):
```
Patched: game.dll (original saved as game.dll.hearthdaoc-orig)
Patched: pregame/character_customize_stats.xml (original saved as pregame/character_customize_stats.xml.hearthdaoc-orig)
Patched: pregame/splash.mpk (original saved as pregame/splash.mpk.hearthdaoc-orig)
Restored: game.dll
Restored: pregame/character_customize_stats.xml
Restored: pregame/splash.mpk
linux exit 0
Patched: game.dll (original saved as game.dll.hearthdaoc-orig)
Patched: pregame/character_customize_stats.xml (original saved as pregame/character_customize_stats.xml.hearthdaoc-orig)
Patched: pregame/splash.mpk (original saved as pregame/splash.mpk.hearthdaoc-orig)
windows exit 0
Restored: game.dll
Restored: pregame/character_customize_stats.xml
Restored: pregame/splash.mpk
windows restore exit 0
$T/app:
connect-hearthdaoc.bat
game.dll
patch-client.bat
patch-client.ps1
patches
pregame

$T/linux:
game.dll
pregame
67dcf68a37b95a93
```

- [ ] **Step 11: Run the suites this task touches**

`client/patches/tests` already ran with the real files in Step 5. Run the deploy suite (it now holds `ClientPatchWorkflowTests` and the new `ReleaseWorthyTests` cases; `HDC_MPK_TOOL` also runs Task 7's real-MPK bundle test) and the client suite.

Run:
```bash
export HDC_MPK_TOOL=$PWD/source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db
python3 -m unittest discover -s deploy/tests -t deploy 2>&1 | tail -3
python3 -m unittest discover -s client/tests -t client 2>&1 | tail -3
git status --short
```

Expected (the 4 skipped `test_world_admin` cases need `HDC_TOOLS` and `HDC_TEST_WORLD`, which CI sets), then only this task's docs edits:
```
Ran 117 tests in 29.852s

OK (skipped=4)
Ran 11 tests in 5.751s

OK
 M client/README.md
 M docs/fork/FORK.md
```
(The counts depend on the other tasks and on main.)

- [ ] **Step 12: Commit**

```bash
git add docs/fork/FORK.md client/README.md
git commit -m "docs: client patches in FORK.md, classic creation in the client README

FORK.md gets a Client patches section: what the patch set changes,
where and why, its sources, the splash credit, which changes make a
release and how to rebuild it (the same fetch and build commands as
CI). The upstream sync gains a rebuild-the-patch-set step on the sync
branch. client/README.md explains the classic creation screen, the
Linux restore command and the Windows step (copy windows/ into the
client folder, double-click patch-client.bat, run it again if the files
are put back, -Restore), and credits OfflineDAoC for the splash art.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git status --short
```

Expected: `git status --short` prints nothing.

**Notes for the PR and later tasks:**
- The first CI run of the PR is the real test of the workflow: check that the "Unit tests" log lists the `PowerShellApplierTests`, the real-file cases and `ClientPatchWorkflowTests` as `ok`, not `skipped`, and that "Client patch set matches a rebuild" passes.
- The release job only runs on `main`, so the PR's CI never runs it. Merging the PR is a release (`client/` changed), and it is the first run of the job's new steps. Afterwards, check that `release-assets` built the MPK tool, and that the release has both bundles and the credit line above the generated notes. If it failed after the image was pushed, fix it in a new PR, or use Run workflow on `main` (FORK.md, Releases).
- Task 9 (in-game checks): the release notes and `client/README.md` now carry the splash credit; the verification record can point to them.

---

### Task 9: In-game verification (owner) and record

**Files:**
- Create: `docs/fork/verification/sub2-ingame.md`

**Interfaces:**
- Consumes: the patched client from Task 7. On the owner's Linux PC, re-run `setup.sh` from the client bundle built by `deploy/build_bundles.sh`, or run `python3 client/patches/apply_patches.py --client ~/Games/HearthDAoC/client` from the branch. Also consumes a server running this branch's image, or the release that contains PR #64 (`v0.34b-hearth.5` or later).
- Produces: the verification record that gates merging the sub-project PR.

This task is done by the owner in the real client. The agent prepares the client and the record, then asks the owner to run the checks and report each one.

- [ ] **Step 1: Patch the owner's client and show its state**

Run: `python3 client/patches/apply_patches.py --client ~/Games/HearthDAoC/client && python3 client/patches/apply_patches.py --client ~/Games/HearthDAoC/client --check`
Expected: one `Patched: …` line per file, then each path followed by `: patched`. Exit code 0.

- [ ] **Step 2: Ask the owner to run the in-game checks** (spec section 5). Use a normal player account (plvl 1), because GMs skip some server checks. Report each check as pass or fail:

1. The splash says HEARTH DAoC. Each realm's class list shows only its base classes (Albion 6, Midgard 4, Hibernia 5), each with its description and "At level 5 your trainer makes you …".
2. Picking a class leaves only the expected race buttons active (spec section 2 table). Half Ogre, Frostalf, Shar and the Minotaurs are not shown.
3. Every pick starts at the race's base stats with 30 points to place, and the attributes window opens straight away. There is no Optimize button. Reset and the +/− buttons work. Continue shows "You must use all your points!" until all 30 are placed.
4. Create one character per race, using any base class allowed for it: 15 characters across the three realms. Each is accepted. On the server, `./hdc account list` and the database show the base class id and the placed stats. If one is refused, note which.
5. Level one character to 5 (for example with GM commands on a GM account after creating it as a player). Train it into a full class at its trainer.
6. From character select, customise an existing character that already has a full class (for example the promoted one from check 5). The screen must not crash. Note what it shows for the class.
7. Run `python3 client/patches/apply_patches.py --client ~/Games/HearthDAoC/client --restore`. The stock creation screen is back. Patch again afterwards.

- [ ] **Step 3: Record the results**

Create `docs/fork/verification/sub2-ingame.md` with one row per check (check, expected, actual, pass/fail), plus the client and server versions used. Any failure goes back to the owning task, or to spec section 6's fallback for checks 3 and 6, before the PR is merged.

- [ ] **Step 4: Commit**

```bash
git add docs/fork/verification/sub2-ingame.md
git commit -m "docs(fork): in-game verification of classic character creation

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
