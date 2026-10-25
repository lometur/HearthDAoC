"""The Shrouded Isles portals back at their 1.65 spots (2026-10-22), the server side: the Avalon Marsh pier near
Adribard's Retreat, Gotar north-east of Nalliten and Shannon Estuary across the lake from Connla, where the portals stood
until patch 1.74 moved them "outside the main cities near Cotswold, Mularn, and Mag Mell" (2005).

The client's own jump lines at the 1.65 spots send the three entrance zone points (153, 165 and 167) themselves, so a
player needs nothing from the server there. deploy/bin/mob_fixes.json moves the zone points' source spots from upstream's
town portals to the client's lines, where bots, /travel and /stables go to cross, and ShroudedIslesPortals.cs has no
walk-on pads at the town spots any more (one marked edit).

The data and source tests need no world; the world tests run world_fixes on a copy of a clean classic world
(HDC_TEST_WORLD).
"""
import math
import os
import re
import shutil
import sqlite3
import sys
import tempfile
import unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
DEPLOY = os.path.dirname(HERE)
ROOT = os.path.dirname(DEPLOY)
sys.path.insert(0, os.path.join(DEPLOY, "bin"))

import mob_fixes as mf  # noqa: E402
import world_fixes as wf  # noqa: E402

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")
SERVER = os.path.join(ROOT, "source", "server")
GROUP = "the Shrouded Isles portals' 1.65 spots"
# Zone point Id: the client's jump line at the 1.65 portal (its zone's ZONEJUMP row: local x1, y1, x2, y2, and its Z
# band), the zone's offset in its region (zones.dat, x 8192), upstream 0.37b's source (region, x, y, z: its town
# portal) and the zone point back from the Isles, whose target is beside the 1.65 portal already.
PORTALS = {
    153: {"line": (44155, 18011, 44338, 18018), "band": (1713, 1906), "offset": (417792, 614400),  # dat009 row 1
          "upstream": (1, 564976, 509200, 2811), "back": 154},  # Cotswold
    165: {"line": (50532, 33020, 50685, 33111), "band": (4700, 4850), "offset": (720896, 802816),  # dat103 row 3
          "upstream": (100, 808830, 725039, 4882), "back": 166},  # Mularn
    167: {"line": (23103, 30483, 23183, 30374), "band": (4880, 5041), "offset": (286720, 614400),  # csv202 row 1
          "upstream": (200, 348198, 492819, 5240), "back": 168},  # Mag Mell
}
SOURCE = ("SourceRegion", "SourceX", "SourceY", "SourceZ")


def entries():
    """{zone point Id: its mob_fixes.json entry} of the file's ZonePoint entries."""
    return {entry["id"]: entry for entry in mf.load_data()["fixes"] if entry["table"] == "ZonePoint"}


class DataTests(unittest.TestCase):
    """The three entries of mob_fixes.json (no world needed)."""

    def test_the_three_entrances_are_one_group(self):
        found = entries()
        self.assertEqual(sorted(found), [153, 165, 167])
        self.assertEqual({entry.get("group") for entry in found.values()}, {GROUP})
        self.assertEqual([e["id"] for e in mf.load_data()["fixes"] if e.get("group") == GROUP], [153, 165, 167])

    def test_each_expects_upstreams_town_spot_and_keeps_its_region(self):
        for zp, entry in entries().items():
            with self.subTest(zp):
                self.assertEqual(entry["expect"], dict(zip(SOURCE, PORTALS[zp]["upstream"])))
                self.assertEqual(sorted(entry["set"]), ["SourceX", "SourceY", "SourceZ"])  # a portal never changes region

    def test_each_moves_to_the_middle_of_its_client_line(self):
        for zp, entry in entries().items():
            portal = PORTALS[zp]
            x1, y1, x2, y2 = portal["line"]
            middle = (portal["offset"][0] + (x1 + x2) / 2, portal["offset"][1] + (y1 + y2) / 2)
            with self.subTest(zp):
                self.assertLessEqual(math.dist((entry["set"]["SourceX"], entry["set"]["SourceY"]), middle), 1)
                # The floor there on the server's navmesh: in the line's Z band, or within 160 below it.
                low, high = portal["band"]
                self.assertTrue(low - 160 <= entry["set"]["SourceZ"] <= high, entry["set"]["SourceZ"])

    def test_each_says_why_with_the_1_74_notes_and_its_client_line(self):
        for zp, entry in entries().items():
            with self.subTest(zp):
                self.assertIn("1.74", entry["why"])
                self.assertIn("2005", entry["why"])
                self.assertIn("ZONEJUMP", entry["why"])
                self.assertIn("our choice", entry["why"].lower())


class SourceTests(unittest.TestCase):
    """The marked upstream edits: no walk-on pads at the town spots."""

    def read(self, *path):
        with open(os.path.join(SERVER, *path), encoding="utf-8") as f:
            return f.read()

    def test_the_pads_are_empty_and_marked(self):
        text = self.read("GameServer", "world", "ShroudedIslesPortals.cs")
        block = re.search(r"\n((?:        //[^\n]*\n)+)        public static readonly Dictionary<ushort, Vector3> Pads = new\(\);\r?\n",
                          text)
        self.assertIsNotNone(block, "Pads is not the marked empty dictionary")
        self.assertTrue(block.group(1).startswith("        // HearthDAoC: "), block.group(1))
        self.assertEqual(text.count("HearthDAoC"), 1)  # one marked block
        code = "\n".join(line for line in text.splitlines() if not line.lstrip().startswith("//"))
        self.assertNotRegex(code, r"\[\s*(153|165|167)\s*\]\s*=")  # upstream's pads stay only in the comment

    def test_upstreams_test_has_two_marked_edits(self):
        text = self.read("Tests", "UnitTests", "UT_ShroudedIslesPortalsOct5.cs")
        self.assertEqual(text.count("// HearthDAoC:"), 2)
        self.assertNotIn("TryGetPad(165, out Vector3 pad)", text)


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class WorldTests(unittest.TestCase):
    """The real data file on a copy of a clean classic world."""

    COLUMNS = "Id, TargetX, TargetY, TargetZ, TargetRegion, TargetHeading, SourceRegion, SourceX, SourceY, SourceZ, Realm, " \
              "ClassType, ZonePoint_ID"

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.db = os.path.join(cls.tmp.name, "world.db")
        shutil.copyfile(TEST_WORLD, cls.db)
        with sqlite3.connect(cls.db) as c:
            cls.before = {row[0]: row for row in c.execute(f"SELECT {cls.COLUMNS} FROM ZonePoint WHERE Id IN (153, 165, 167)")}
        wf.apply(cls.db)
        cls.conn = sqlite3.connect(cls.db)
        cls.after = {row[0]: row for row in cls.conn.execute(f"SELECT {cls.COLUMNS} FROM ZonePoint WHERE Id IN (153, 165, 167)")}

    @classmethod
    def tearDownClass(cls):
        cls.conn.close()
        cls.tmp.cleanup()

    def test_upstreams_world_has_them_at_the_town_spots(self):
        self.assertEqual({zp: row[6:10] for zp, row in self.before.items()},
                         {zp: portal["upstream"] for zp, portal in PORTALS.items()})

    def test_each_source_is_at_its_1_65_spot(self):
        for zp, entry in entries().items():
            with self.subTest(zp):
                self.assertEqual(self.after[zp][6:10], (PORTALS[zp]["upstream"][0], entry["set"]["SourceX"],
                                                        entry["set"]["SourceY"], entry["set"]["SourceZ"]))

    def test_only_the_source_spot_changes(self):
        for zp in PORTALS:
            with self.subTest(zp):
                self.assertEqual(self.after[zp][:6] + self.after[zp][10:], self.before[zp][:6] + self.before[zp][10:])

    def test_the_way_back_lands_beside_each_portal(self):
        # Zone points 154, 166 and 168 already lead to the 1.65 spots: the entrance and the arrival are one place.
        for zp, portal in PORTALS.items():
            region, x, y, _ = self.after[zp][6:10]
            back = self.conn.execute("SELECT TargetRegion, TargetX, TargetY FROM ZonePoint WHERE Id=?", (portal["back"],)).fetchall()
            with self.subTest(zp):
                self.assertEqual(len(back), 1)
                self.assertEqual(back[0][0], region)
                self.assertLess(math.dist(back[0][1:], (x, y)), 800)


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class EarlierWorldTests(unittest.TestCase):
    """A world an earlier release started, with every other fix in: the owner's world at this release's first start."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "world.db")
        shutil.copyfile(TEST_WORLD, self.db)
        data = mf.load_data()
        with mock.patch.object(mf, "load_data", return_value=dict(data, fixes=[e for e in data["fixes"]
                                                                               if e["table"] != "ZonePoint"])):
            self.assertNotIn("ZonePoint", " ".join(wf.apply(self.db)))

    def tearDown(self):
        self.tmp.cleanup()

    def spots(self):
        with sqlite3.connect(self.db) as c:
            return {zp: (region, x, y, z) for zp, region, x, y, z in c.execute(
                "SELECT Id, SourceRegion, SourceX, SourceY, SourceZ FROM ZonePoint WHERE Id IN (153, 165, 167)")}

    def test_its_next_start_moves_the_three_and_names_them(self):
        self.assertEqual(wf.apply(self.db), ["Mob fixes: 3 corrected: Albion Shrouded Isles portal (ZonePoint 153), "
                                             "Midgard Shrouded Isles portal (ZonePoint 165), Hibernia Shrouded Isles "
                                             "portal (ZonePoint 167)"])
        self.assertEqual(self.spots(), {zp: (PORTALS[zp]["upstream"][0], *(e["set"][c] for c in SOURCE[1:]))
                                        for zp, e in entries().items()})
        self.assertEqual(wf.apply(self.db), [])

    def test_an_entrance_the_owner_moved_keeps_all_three_where_they_are(self):
        with sqlite3.connect(self.db) as c:
            c.execute("UPDATE ZonePoint SET SourceX=808900 WHERE Id=165")
        held = ("Mob fixes: 1 group left as it is (an entry's row holds neither upstream's values nor this file's, or is "
                f"missing, so no row of the group changes): {GROUP} (Midgard Shrouded Isles portal (ZonePoint 165))")
        self.assertEqual(wf.apply(self.db), [held])
        self.assertEqual(self.spots(), {153: PORTALS[153]["upstream"], 165: (100, 808900, 725039, 4882),
                                        167: PORTALS[167]["upstream"]})
        self.assertEqual(wf.apply(self.db), [held])  # named at every start

if __name__ == "__main__":
    unittest.main()
