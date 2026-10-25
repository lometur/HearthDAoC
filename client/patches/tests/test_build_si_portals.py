"""Tests for the Shrouded Isles portal patch sets: their generator (build_si_portals.py) and the committed
si-portals.json and si-portal-art.json.

The generator runs on made-up archives with our own text. The committed sets are checked on their own, and, when
HDC_CLIENT_FILES holds the four client files (OfflineDAoC 0.36b's or 0.37b's, read only), against them: a rebuild
is the committed sets, the prototype's after hashes (~/tmp/hearthdaoc-period/scripts/repack_proto.py, 2026-10-22),
the six entries at their base sizes with no portal row left, and both appliers on a copy in a temporary folder
(apply, re-apply, restore, and the portal set retired by PORTALS_RETIRED putting the archives back). Only the two
pin lines of CommittedSetTests name the switches' shipped values (FORK.md, The Shrouded Isles portals).
"""
import contextlib
import hashlib
import io
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
import build_si_portals  # noqa: E402
import mpk  # noqa: E402
import patchset  # noqa: E402
from build import to_json  # noqa: E402
from tests.test_patchset import make_mpk, mpk_entry, parse_mpk  # noqa: E402

GENERATOR = os.path.join(PATCHES, "build_si_portals.py")
APPLY_PY = os.path.join(PATCHES, "apply_patches.py")
PS1 = os.path.join(os.path.dirname(PATCHES), "windows", "patch-client.ps1")
PWSH = os.environ.get("HDC_PWSH") or shutil.which("pwsh")
PORTALS = os.path.join(PATCHES, "si-portals.json")
ART = os.path.join(PATCHES, "si-portal-art.json")
CLIENT = os.environ.get("HDC_CLIENT_FILES")
ARCHIVES = ["zones/Zone000/csv000.mpk", "zones/zone100/csv100.mpk", "zones/zone200/csv200.mpk"]
MODEL = "Newtowns/zones/Nifs/bavteleporter.npk"
NEEDS_FILES = ("set HDC_CLIENT_FILES to a client folder with OfflineDAoC 0.36b's or 0.37b's zones/Zone000/csv000.mpk, "
               "zones/zone100/csv100.mpk, zones/zone200/csv200.mpk and Newtowns/zones/Nifs/bavteleporter.npk")
# The prototype's rebuilds of the player's own archives (si-design.md, 3.4.1): the generator and both appliers
# must give these bytes.
PROTOTYPE_AFTER = {
    "zones/Zone000/csv000.mpk": "41976bf7f186484337f7a471340a5ef95e75481956702145cc8bc5b5960d24ef",
    "zones/zone100/csv100.mpk": "d5e7552ad5c66b8e019f1c3830c30c8a9b7296bdc9750c96dd2dba4f3bd10abe",
    "zones/zone200/csv200.mpk": "7406b52d8a83d873123a14d68a8aee644cc4a252af7e9b01e0434d7369b82cc4",
}
# The fixtures and nifs entries without upstream's rows are the base client's: these sizes (si-design.md, 4.3).
BASE_SIZES = {
    "zones/Zone000/csv000.mpk": {"fixtures.csv": 82685, "nifs.csv": 4383},
    "zones/zone100/csv100.mpk": {"FIXTURES.CSV": 46233, "NIFS.CSV": 4506},
    "zones/zone200/csv200.mpk": {"fixtures.csv": 87013, "NIFS.CSV": 6029},
}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def read(path):
    with open(path, "rb") as f:
        return f.read()


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)


def our_row(zone, kind):
    """A made-up row of ours with the portal name, as upstream's tool appends it (the last line, CRLF)."""
    if kind == "fixtures":
        return f"9{zone},455,SI Portal Visual {zone},100,200,0,315,100,1,0,0,1,0,0,9\r\n"
    return f"455,SI Portal Visual {zone},ourportal.nif,0,0,255,0,1,1,0,0,100,100,0,0,0,0,0,0,0,0,0,0,\r\n"


BASE_FIXTURES = "1,10,Our Tr\xe9e,100,200,0,0,100,1,0,0,1,0,0,1\r\n2,11,Our Rock,300,400,0,0,100,1,0,0,1,0,0,2\r\n"
BASE_NIFS = "10,Our Tr\xe9e,ourtree.nif,0,0,255,0,1,1,0,0,100,100,0,0,0,0,0,0,0,0,0,0,\r\n"
# The archives' own entry names: the case differs between them, and the ops keep it.
NAMES = {"zones/Zone000/csv000.mpk": ("fixtures.csv", "nifs.csv"),
         "zones/zone100/csv100.mpk": ("FIXTURES.CSV", "NIFS.CSV"),
         "zones/zone200/csv200.mpk": ("fixtures.csv", "NIFS.CSV")}


def our_archive(path, fixtures=None, nifs=None, extra=()):
    """A made-up zone archive like the client's: zonejump.csv, the fixtures and nifs entries (default: ours, each
    ending in its portal row) and extra entries."""
    zone = path[-7:-4]
    names = NAMES[path]
    fixtures = BASE_FIXTURES + our_row(zone, "fixtures") if fixtures is None else fixtures
    nifs = BASE_NIFS + our_row(zone, "nifs") if nifs is None else nifs
    entries = [mpk_entry("zonejump.csv", b"1,Our Line,100,200,300,400,0,500,7,1\r\n"),
               mpk_entry(names[0], fixtures.encode("latin-1")), mpk_entry(names[1], nifs.encode("latin-1"))]
    return make_mpk(entries + [mpk_entry(name, content) for name, content in extra], name=f"csv{zone}.mpk".encode(),
                    cumulative=True)


class Client:
    """A made-up client folder with the three archives and the model."""

    def __init__(self, test, archives=None, model=b"our 2006 portal model"):
        tmp = tempfile.TemporaryDirectory()
        test.addCleanup(tmp.cleanup)
        self.root = tmp.name
        self.dir = os.path.join(self.root, "client")
        for path in ARCHIVES:
            write(self.path(path), (archives or {}).get(path) or our_archive(path))
        if model is not None:
            write(self.path(MODEL), model)

    def path(self, rel):
        return os.path.join(self.dir, *rel.split("/"))


def said(out, retired, look):
    """What the generator prints after writing both sets in `out` with these switches."""
    portals = " (retired: upstream's archives come back)" if retired else ""
    art = "retired: the 2006 look" if look == "2006" else "moved aside: the 2002 Albion portal look"
    return (f"Wrote {os.path.join(out, 'si-portals.json')}: {', '.join(ARCHIVES)}{portals}\n"
            f"Wrote {os.path.join(out, 'si-portal-art.json')}: {MODEL} ({art})\n")


def contents(data):
    """{entry name: content} of an MPK archive."""
    return {e["name_field"].split(b"\0")[0].decode("latin-1"): e["content"] for e in parse_mpk(data)[2]}


class GeneratorTests(unittest.TestCase):
    def test_the_portal_set_takes_the_portal_row_out_of_each_fixtures_and_nifs_entry(self):
        client = Client(self)
        data = build_si_portals.build_portals_set(client.dir, retired=False)
        self.assertEqual(list(data), ["format", "name", "client", "refused", "files"])
        self.assertEqual([data[key] for key in ("format", "name", "client", "refused")],
                         [1, "si-portals", build_si_portals.CLIENT, build_si_portals.PORTALS_REFUSED])
        self.assertEqual([entry["path"] for entry in data["files"]], ARCHIVES)
        for entry in data["files"]:
            path, zone = entry["path"], entry["path"][-7:-4]
            with self.subTest(path):
                original = read(client.path(path))
                self.assertEqual(entry["ops"], [
                    {"op": "mpk-text-replace", "entry": NAMES[path][0], "find": our_row(zone, "fixtures"), "replace": ""},
                    {"op": "mpk-text-replace", "entry": NAMES[path][1], "find": our_row(zone, "nifs"), "replace": ""}])
                after = patchset.transform(original, entry["ops"], None)
                self.assertEqual((entry["before"], entry["after"]), (sha(original), sha(after)))
                self.assertEqual(contents(after), {"zonejump.csv": b"1,Our Line,100,200,300,400,0,500,7,1\r\n",
                                                   NAMES[path][0]: BASE_FIXTURES.encode("latin-1"),
                                                   NAMES[path][1]: BASE_NIFS.encode("latin-1")})

    def test_the_art_set_moves_the_2006_model_aside(self):
        client = Client(self)
        self.assertEqual(build_si_portals.build_art_set(client.dir, "2002"), {
            "format": 1, "name": "si-portal-art", "client": build_si_portals.CLIENT,
            "refused": build_si_portals.ART_REFUSED,
            "files": [{"path": MODEL, "before": sha(b"our 2006 portal model"), "after": "absent", "ops": [{"op": "remove"}]}]})

    def test_the_2006_look_writes_the_art_set_retired_so_the_appliers_put_the_model_back(self):
        client = Client(self)
        shipped = build_si_portals.build_art_set(client.dir, "2002")
        retired = build_si_portals.build_art_set(client.dir, "2006")
        self.assertEqual(list(retired), ["format", "name", "client", "refused", "retired", "files"])
        self.assertEqual(retired, dict(shipped, retired=True))
        with self.assertRaisesRegex(ValueError, "the Albion portal look must be 2002 or 2006, not '2004'"):
            build_si_portals.build_art_set(client.dir, "2004")

    def test_retiring_the_portal_set_writes_it_retired_so_the_appliers_put_the_archives_back(self):
        # The way back if a client can't read the rebuilt archives (FORK.md, The Shrouded Isles portals): a set that
        # has shipped is never dropped, since nothing would run it on a Windows client.
        client = Client(self)
        shipped = build_si_portals.build_portals_set(client.dir, retired=False)
        retired = build_si_portals.build_portals_set(client.dir, retired=True)
        self.assertEqual(list(retired), ["format", "name", "client", "refused", "retired", "files"])
        self.assertEqual(retired, dict(shipped, retired=True))
        originals = {path: read(client.path(path)) for path in ARCHIVES}
        appliers = [([sys.executable, APPLY_PY], ("--client", "--patchset"))]
        if PWSH:
            appliers.append(([PWSH, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", PS1],
                             ("-Client", "-PatchSet")))
        for command, flags in appliers:
            with self.subTest(command[0]):
                for data, said in ((shipped, "Patched: "), (retired, "Restored: ")):
                    path = os.path.join(client.root, "si-portals.json")
                    write(path, to_json(data).encode("ascii"))
                    r = subprocess.run(command + [flags[0], client.dir, flags[1], path], capture_output=True, text=True)
                    self.assertEqual((r.returncode, r.stderr), (0, ""), r.stdout)
                    self.assertEqual([line.split(" (")[0] for line in r.stdout.splitlines()],
                                     [said + archive for archive in ARCHIVES])
                for archive in ARCHIVES:
                    self.assertEqual(read(client.path(archive)), originals[archive])
                    self.assertFalse(os.path.exists(client.path(archive) + ".hearthdaoc-orig"))

    def test_the_cli_writes_the_sets_as_the_switches_say(self):
        # PORTALS_RETIRED and ALBION_LOOK both ways, so a release that flips one changes only the switch, the line of
        # CommittedSetTests that pins it, and the committed JSON (FORK.md, The Shrouded Isles portals).
        for retired, look in ((False, "2002"), (True, "2006")):
            with self.subTest(retired=retired, look=look):
                client = Client(self)
                out = os.path.join(client.root, "out")
                os.makedirs(out)
                printed = io.StringIO()
                with mock.patch.object(build_si_portals, "PORTALS_RETIRED", retired), \
                        mock.patch.object(build_si_portals, "ALBION_LOOK", look), contextlib.redirect_stdout(printed):
                    self.assertEqual(build_si_portals.main(["--client", client.dir, "--out-dir", out]), 0)
                self.assertEqual(read(os.path.join(out, "si-portals.json")),
                                 to_json(build_si_portals.build_portals_set(client.dir, retired)).encode("ascii"))
                self.assertEqual(read(os.path.join(out, "si-portal-art.json")),
                                 to_json(build_si_portals.build_art_set(client.dir, look)).encode("ascii"))
                self.assertEqual(printed.getvalue(), said(out, retired, look))

    def test_the_sets_are_valid_and_apply_with_patchset(self):
        client = Client(self)
        for name, data in (("si-portals.json", build_si_portals.build_portals_set(client.dir, retired=False)),
                           ("si-portal-art.json", build_si_portals.build_art_set(client.dir, "2002"))):
            with self.subTest(name):
                path = os.path.join(client.root, name)
                write(path, to_json(data).encode("ascii"))
                pset = patchset.load(path)
                self.assertTrue(all(result == "patched" for _path, result in patchset.apply(client.dir, pset, None)))
                self.assertTrue(all(state == "patched" for _path, state in patchset.status(client.dir, pset, None)))
        self.assertFalse(os.path.exists(client.path(MODEL)))

    def test_refuses_an_archive_whose_portal_row_is_not_the_one_last_line(self):
        path = ARCHIVES[1]
        row = our_row("100", "fixtures")
        cases = {
            "zones/zone100/csv100.mpk: FIXTURES.CSV: 0 lines name 'SI Portal Visual', not exactly one":
                our_archive(path, fixtures=BASE_FIXTURES),
            "zones/zone100/csv100.mpk: FIXTURES.CSV: 2 lines name 'SI Portal Visual', not exactly one":
                our_archive(path, fixtures=BASE_FIXTURES + row + row.replace("9100", "9101")),
            "zones/zone100/csv100.mpk: FIXTURES.CSV: the 'SI Portal Visual' line is not the last one":
                our_archive(path, fixtures=row + BASE_FIXTURES),
            "zones/zone100/csv100.mpk: FIXTURES.CSV: does not end in CRLF":
                our_archive(path, fixtures=BASE_FIXTURES + row[:-2]),
            "zones/zone100/csv100.mpk: NIFS.CSV: 0 lines name 'SI Portal Visual', not exactly one":
                our_archive(path, nifs=BASE_NIFS),
            "zones/zone100/csv100.mpk: 2 entries named fixtures.csv (in any case), not exactly one":
                our_archive(path, extra=[("Fixtures.csv", b"")]),
        }
        for reason, archive in cases.items():
            with self.subTest(reason):
                client = Client(self, archives={path: archive})
                with self.assertRaisesRegex(ValueError, "^" + reason.replace("(", r"\(").replace(")", r"\)") + "$"):
                    build_si_portals.build_portals_set(client.dir)

    def test_refuses_an_archive_without_its_fixtures_entry(self):
        archive = make_mpk([mpk_entry("nifs.csv", (BASE_NIFS + our_row("000", "nifs")).encode("latin-1"))])
        client = Client(self, archives={ARCHIVES[0]: archive})
        with self.assertRaisesRegex(ValueError, "^zones/Zone000/csv000.mpk: 0 entries named fixtures.csv"):
            build_si_portals.build_portals_set(client.dir)

    def test_the_cli_writes_both_sets_the_same_every_time(self):
        client = Client(self)
        out = os.path.join(client.root, "out")
        os.makedirs(out)
        for _run in range(2):
            r = subprocess.run([sys.executable, "-B", GENERATOR, "--client", client.dir, "--out-dir", out],
                               capture_output=True, text=True)
            self.assertEqual((r.returncode, r.stderr), (0, ""))
            self.assertEqual(r.stdout, said(out, build_si_portals.PORTALS_RETIRED, build_si_portals.ALBION_LOOK))
            self.assertEqual(read(os.path.join(out, "si-portals.json")), to_json(build_si_portals.build_portals_set(
                client.dir, build_si_portals.PORTALS_RETIRED)).encode("ascii"))
            self.assertEqual(read(os.path.join(out, "si-portal-art.json")), to_json(build_si_portals.build_art_set(
                client.dir, build_si_portals.ALBION_LOOK)).encode("ascii"))
        self.assertEqual(sorted(os.listdir(out)), ["si-portal-art.json", "si-portals.json"])

    def test_the_cli_writes_nothing_when_a_file_is_missing(self):
        client = Client(self, model=None)
        out = os.path.join(client.root, "out")
        os.makedirs(out)
        r = subprocess.run([sys.executable, "-B", GENERATOR, "--client", client.dir, "--out-dir", out],
                           capture_output=True, text=True)
        self.assertEqual((r.returncode, r.stdout), (1, ""))
        self.assertRegex(r.stderr, r"^build_si_portals.py: .*bavteleporter.npk")
        self.assertEqual(os.listdir(out), [])


class CommittedSetTests(unittest.TestCase):
    """si-portals.json and si-portal-art.json as they ship: upstream's six rows and four hashes, no EA bytes."""

    def test_the_portal_set_removes_upstreams_two_rows_from_each_archive(self):
        self.assertIs(build_si_portals.PORTALS_RETIRED, False)  # this release ships the fix (FORK.md)
        data = patchset.load(PORTALS)
        self.assertEqual([data[key] for key in ("format", "name", "client", "refused")],
                         [1, "si-portals", build_si_portals.CLIENT, build_si_portals.PORTALS_REFUSED])
        self.assertIs(data.get("retired", False), build_si_portals.PORTALS_RETIRED)
        self.assertEqual([entry["path"] for entry in data["files"]], ARCHIVES)
        for entry in data["files"]:
            with self.subTest(entry["path"]):
                self.assertEqual(entry["after"], PROTOTYPE_AFTER[entry["path"]])
                self.assertEqual([(op["op"], op["entry"], op["replace"]) for op in entry["ops"]],
                                 [("mpk-text-replace", name, "") for name in BASE_SIZES[entry["path"]]])
                for op in entry["ops"]:
                    # One CSV row of upstream's tool (tools/claude-version/si_portal_visuals.py), with its CRLF.
                    self.assertIn(f",SI Portal Visual {entry['path'][-7:-4]},", op["find"])
                    self.assertTrue(op["find"].endswith("\r\n"))
                    self.assertEqual(op["find"].count("\n"), 1)
                    self.assertLess(len(op["find"]), 120)

    def test_the_art_set_moves_the_2006_model_aside_retired_only_for_the_2006_look(self):
        self.assertEqual(build_si_portals.ALBION_LOOK, "2002")  # the look this release ships (FORK.md)
        data = patchset.load(ART)
        self.assertEqual([data[key] for key in ("format", "name", "client", "refused")],
                         [1, "si-portal-art", build_si_portals.CLIENT, build_si_portals.ART_REFUSED])
        self.assertIs(data.get("retired", False), build_si_portals.ALBION_LOOK == "2006")
        [entry] = data["files"]
        self.assertEqual((entry["path"], entry["after"], entry["ops"]), (MODEL, "absent", [{"op": "remove"}]))
        self.assertEqual(entry["before"], "dfb6feaffcfefbac45e81377d3d1f4968f978b67cfe0d884cf6dc0a75e31f519")

    def test_the_files_are_the_generator_s_json(self):
        for path in (PORTALS, ART):
            with self.subTest(os.path.basename(path)):
                with open(path, encoding="ascii") as f:
                    self.assertEqual(read(path), to_json(json.load(f)).encode("ascii"))


@unittest.skipUnless(CLIENT and all(os.path.isfile(os.path.join(CLIENT, *p.split("/"))) for p in ARCHIVES + [MODEL]),
                     NEEDS_FILES)
class RealClientTests(unittest.TestCase):
    """The four files of the player's client (or CI's, from the pinned release), read only: copies are patched."""

    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.client = os.path.join(tmp.name, "client")
        self.originals = {}
        for rel in ARCHIVES + [MODEL]:
            self.originals[rel] = read(os.path.join(CLIENT, *rel.split("/")))
            write(self.path(rel), self.originals[rel])

    def path(self, rel):
        return os.path.join(self.client, *rel.split("/"))

    def test_a_rebuild_is_the_committed_sets(self):
        self.assertEqual(to_json(build_si_portals.build_portals_set(CLIENT, build_si_portals.PORTALS_RETIRED))
                         .encode("ascii"), read(PORTALS))
        self.assertEqual(to_json(build_si_portals.build_art_set(CLIENT, build_si_portals.ALBION_LOOK)).encode("ascii"),
                         read(ART))

    def test_the_rebuilt_archives_are_the_prototype_s_and_hold_the_base_entries(self):
        for entry in patchset.load(PORTALS)["files"]:
            with self.subTest(entry["path"]):
                original = self.originals[entry["path"]]
                self.assertEqual(sha(original), entry["before"])
                after = patchset.transform(original, entry["ops"], None)
                self.assertEqual(sha(after), PROTOTYPE_AFTER[entry["path"]])
                write(self.path(entry["path"]), after)
                name, entries = mpk.read_mpk(self.path(entry["path"]))  # read with mpk.py, not patchset.py
                old_name, old_entries = mpk.read_mpk(os.path.join(CLIENT, *entry["path"].split("/")))
                self.assertEqual(name, old_name)
                self.assertEqual([n for n, _c in entries], [n for n, _c in old_entries])
                for (entry_name, content), (_n, old) in zip(entries, old_entries):
                    if entry_name in BASE_SIZES[entry["path"]]:
                        self.assertNotIn(b"SI Portal Visual", content)
                        self.assertEqual(len(content), BASE_SIZES[entry["path"]][entry_name])
                        self.assertEqual(content + next(op["find"] for op in entry["ops"]
                                                        if op["entry"] == entry_name).encode("latin-1"), old)
                    else:
                        self.assertEqual(content, old)

    def run_applier(self, command, flags, pset, *switches):
        return subprocess.run(command + [flags[0], self.client, flags[1], pset, *switches],
                              capture_output=True, text=True)

    def applied(self, pset):
        """A copy of the committed set without "retired" (which a release that takes it back sets): its path."""
        with open(pset, encoding="ascii") as f:
            data = json.load(f)
        data.pop("retired", None)
        path = os.path.join(os.path.dirname(self.client), "applied-" + os.path.basename(pset))
        write(path, to_json(data).encode("ascii"))
        return path

    def check_applier(self, command, flags, restore):
        sets = [self.applied(pset) for pset in (PORTALS, ART)]
        for pset in sets:
            r = self.run_applier(command, flags, pset)
            self.assertEqual((r.returncode, r.stderr), (0, ""), r.stdout)
            self.assertTrue(r.stdout.startswith("Patched: "), r.stdout)
        for rel, after in PROTOTYPE_AFTER.items():
            self.assertEqual(sha(read(self.path(rel))), after)
        self.assertFalse(os.path.exists(self.path(MODEL)))
        for pset in sets:
            r = self.run_applier(command, flags, pset)
            self.assertEqual((r.returncode, r.stderr), (0, ""))
            self.assertTrue(all(line.startswith("Already patched: ") for line in r.stdout.splitlines()), r.stdout)
            r = self.run_applier(command, flags, pset, restore)
            self.assertEqual((r.returncode, r.stderr), (0, ""))
            self.assertTrue(all(line.startswith("Restored: ") for line in r.stdout.splitlines()), r.stdout)
        for rel, original in self.originals.items():
            self.assertEqual(read(self.path(rel)), original)
            self.assertFalse(os.path.exists(self.path(rel) + ".hearthdaoc-orig"))

    def check_retired_portals(self, command, flags):
        """The shipped portal set, then the same set retired (PORTALS_RETIRED), with a plain run: the originals."""
        retired = os.path.join(os.path.dirname(self.client), "si-portals-retired.json")
        write(retired, to_json(build_si_portals.build_portals_set(CLIENT, retired=True)).encode("ascii"))
        for pset, said in ((self.applied(PORTALS), "Patched: "), (retired, "Restored: ")):
            r = self.run_applier(command, flags, pset)
            self.assertEqual((r.returncode, r.stderr), (0, ""), r.stdout)
            self.assertEqual([line.split(" (")[0] for line in r.stdout.splitlines()], [said + rel for rel in ARCHIVES])
        for rel in ARCHIVES:
            self.assertEqual(read(self.path(rel)), self.originals[rel])
            self.assertFalse(os.path.exists(self.path(rel) + ".hearthdaoc-orig"))

    def test_the_python_applier_patches_and_restores_the_real_files(self):
        self.check_applier([sys.executable, APPLY_PY], ("--client", "--patchset"), "--restore")

    def test_the_python_applier_puts_the_real_archives_back_from_the_retired_portal_set(self):
        self.check_retired_portals([sys.executable, APPLY_PY], ("--client", "--patchset"))

    @unittest.skipUnless(PWSH, "pwsh is not installed (HDC_PWSH may name it)")
    def test_the_powershell_applier_gives_the_same_bytes(self):
        self.check_applier([PWSH, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", PS1],
                           ("-Client", "-PatchSet"), "-Restore")

    @unittest.skipUnless(PWSH, "pwsh is not installed (HDC_PWSH may name it)")
    def test_the_powershell_applier_puts_the_real_archives_back_from_the_retired_portal_set(self):
        self.check_retired_portals([PWSH, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", PS1],
                                   ("-Client", "-PatchSet"))


if __name__ == "__main__":
    unittest.main()
