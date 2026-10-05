import json
import os
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path[:0] = [os.path.join(REPO, "deploy", "bin"), os.path.join(REPO, "tools", "linux"),
                os.path.join(REPO, "tools", "linux", "tests")]

import release_fixture as fx  # noqa: E402
import init_world  # noqa: E402
from odaoc_fetch import FetchError, Release  # noqa: E402

QUIET = lambda *a, **k: None  # noqa: E731


class InitWorldTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = self.tmp.name
        self.data = os.path.join(self.dir, "data")
        self.lock, self.files = fx.build(self.dir)

    def tearDown(self):
        self.tmp.cleanup()

    def release(self, srv):
        return Release(srv.lock(self.lock), retries=1, backoff=0)

    def read(self, path):
        with open(path, "rb") as f:
            return f.read()

    def meta(self):
        with open(os.path.join(self.data, "world.json"), encoding="utf-8") as f:
            return json.load(f)

    def test_new_classic_world(self):
        with fx.RangeServer(self.dir) as srv:
            self.assertEqual(init_world.init(self.release(srv), self.data, "classic", log=QUIET), 0)
        p = init_world.world_paths(self.data)
        self.assertEqual(self.read(p["db"]), self.files["editions/0.34-no-custom-class/runtime/data/opendaoc.sqlite3.db"])
        self.assertEqual(self.read(os.path.join(p["navmesh"], "zone002.nav")), self.files["runtime/server/navmesh/zone002.nav"])
        meta = self.meta()
        self.assertEqual((meta["version"], meta["edition"], meta["navmesh"]), ("test", "classic", True))

    def test_second_start_downloads_nothing(self):
        with fx.RangeServer(self.dir) as srv:
            init_world.init(self.release(srv), self.data, "b", log=QUIET)
            before = len(srv.requests)
            self.assertEqual(init_world.init(self.release(srv), self.data, "b", log=QUIET), 0)
            self.assertEqual(len(srv.requests), before)

    def test_edition_mismatch_refuses(self):
        with fx.RangeServer(self.dir) as srv:
            init_world.init(self.release(srv), self.data, "classic", log=QUIET)
            messages = []
            rc = init_world.init(self.release(srv), self.data, "b", log=messages.append)
        self.assertEqual(rc, init_world.EXIT_EDITION)
        self.assertIn("odc new-world", " ".join(messages))
        self.assertEqual(self.meta()["edition"], "classic")

    def test_version_mismatch_refuses(self):
        with fx.RangeServer(self.dir) as srv:
            init_world.init(self.release(srv), self.data, "b", log=QUIET)
            meta = self.meta()
            meta["version"] = "0.33b"
            init_world.write_meta(os.path.join(self.data, "world.json"), meta)
            messages = []
            rc = init_world.init(self.release(srv), self.data, "b", log=messages.append)
        self.assertEqual(rc, init_world.EXIT_VERSION)
        self.assertIn("odc upgrade-world", " ".join(messages))

    def test_interrupted_navmesh_download_resumes(self):
        with fx.RangeServer(self.dir) as srv:
            rel = self.release(srv)
            rel.manifest()
            db_entry = rel.entries()["editions/0.34-no-custom-class/runtime/data/opendaoc.sqlite3.db"]
            nav_entry = rel.entries()["runtime/server/navmesh/zone002.nav"]
            db_end = db_entry["off"] + db_entry["csz"]
            # Fail every request that touches zone002.nav's bytes.
            starts = [sum(p["size"] for p in self.lock["parts"][:i]) for i in range(len(self.lock["parts"]))]

            def touches_zone002(path, rng):
                part = int(path.rsplit(".", 1)[1]) - 1
                a, b = (int(x) for x in rng[6:].split("-"))
                a, b = a + starts[part], b + starts[part]
                return a < nav_entry["off"] + nav_entry["csz"] + 200 and b >= nav_entry["off"] and a >= db_end

            srv.fail = touches_zone002
            with self.assertRaises(FetchError):
                init_world.init(rel, self.data, "classic", log=QUIET)
            self.assertFalse(os.path.exists(os.path.join(self.data, "world.json")))
            db = init_world.world_paths(self.data)["db"]
            db_mtime = os.path.getmtime(db)
            srv.fail = None
            self.assertEqual(init_world.init(self.release(srv), self.data, "classic", log=QUIET), 0)
            self.assertEqual(os.path.getmtime(db), db_mtime)  # the finished database was not downloaded again
            self.assertTrue(self.meta()["navmesh"])

    def test_skip_navmesh_then_complete_later(self):
        with fx.RangeServer(self.dir) as srv:
            init_world.init(self.release(srv), self.data, "b", skip_navmesh=True, log=QUIET)
            self.assertFalse(self.meta()["navmesh"])
            self.assertFalse(os.path.isdir(init_world.world_paths(self.data)["navmesh"]))
            init_world.init(self.release(srv), self.data, "b", log=QUIET)
        self.assertTrue(self.meta()["navmesh"])
        self.assertTrue(os.path.isfile(os.path.join(self.data, "navmesh", "zone001.nav")))

    def test_seed_navmesh_avoids_downloading_navmeshes(self):
        seed = os.path.join(self.dir, "seed")
        os.makedirs(seed)
        for name in ("zone001.nav", "zone002.nav"):
            with open(os.path.join(seed, name), "wb") as f:
                f.write(self.files["runtime/server/navmesh/" + name])
        with fx.RangeServer(self.dir) as srv:
            rel = self.release(srv)
            rel.manifest()
            nav_offsets = {rel.entries()[r]["off"] for r in rel.files_under("runtime/server/navmesh/")}
            before = len(srv.requests)
            init_world.init(rel, self.data, "b", seed_navmesh=seed, log=QUIET)
            starts = [sum(p["size"] for p in self.lock["parts"][:i]) for i in range(len(self.lock["parts"]))]
            for path, rng in srv.requests[before:]:
                part = int(path.rsplit(".", 1)[1]) - 1
                a = int(rng[6:].split("-")[0]) + starts[part]
                self.assertNotIn(a, nav_offsets, "a navmesh local header was downloaded despite the seed")
        self.assertTrue(self.meta()["navmesh"])


if __name__ == "__main__":
    unittest.main()
