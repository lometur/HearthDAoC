import hashlib
import json
import os
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
sys.path.insert(0, HERE)

import release_fixture as fx  # noqa: E402
from odaoc_fetch import FetchError, Release, fetch_client, sha256_file  # noqa: E402


class FetchTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = self.tmp.name

    def tearDown(self):
        self.tmp.cleanup()

    def release(self, server, lock, **kw):
        kw.setdefault("retries", 1)
        kw.setdefault("backoff", 0)
        return Release(server.lock(lock), **kw)

    def test_extract_verifies_and_then_skips(self):
        lock, files = fx.build(self.dir)
        with fx.RangeServer(self.dir) as srv:
            rel = self.release(srv, lock)
            dest = os.path.join(self.dir, "out", "nav1")
            self.assertEqual(rel.extract("runtime/server/navmesh/zone001.nav", dest), "ok")
            with open(dest, "rb") as f:
                self.assertEqual(f.read(), files["runtime/server/navmesh/zone001.nav"])
            self.assertEqual(rel.extract("runtime/server/navmesh/zone001.nav", dest), "skip")

    def test_files_spanning_part_boundaries(self):
        lock, files = fx.build(self.dir, part_size=777)
        self.assertGreater(len(lock["parts"]), 5)
        with fx.RangeServer(self.dir) as srv:
            rel = self.release(srv, lock)
            for name, data in files.items():
                dest = os.path.join(self.dir, "all", name)
                rel.extract(name, dest)
                with open(dest, "rb") as f:
                    self.assertEqual(f.read(), data, name)

    def test_manifest_pin_mismatch_is_refused(self):
        lock, _ = fx.build(self.dir)
        lock["manifest_sha256"] = "0" * 64
        with fx.RangeServer(self.dir) as srv:
            rel = self.release(srv, lock)
            with self.assertRaisesRegex(FetchError, "does not match the pinned"):
                rel.manifest()

    def test_file_not_matching_manifest_is_refused_and_not_left_behind(self):
        target = "runtime/server/navmesh/zone002.nav"
        lock, _ = fx.build(self.dir, tamper={target: "1" * 64})
        with fx.RangeServer(self.dir) as srv:
            rel = self.release(srv, lock)
            dest = os.path.join(self.dir, "out", "zone002.nav")
            with self.assertRaisesRegex(FetchError, "SHA-256 mismatch"):
                rel.extract(target, dest)
            self.assertFalse(os.path.exists(dest))
            self.assertFalse(os.path.exists(dest + ".part"))

    def test_network_failure_leaves_no_partial_file(self):
        lock, _ = fx.build(self.dir)
        with fx.RangeServer(self.dir) as srv:
            rel = self.release(srv, lock)
            rel.manifest()  # index and manifest succeed
            srv.fail = lambda path, rng: True
            dest = os.path.join(self.dir, "out", "world.db")
            with self.assertRaisesRegex(FetchError, "range request failed"):
                rel.extract("runtime/data/opendaoc.sqlite3.db", dest)
            self.assertFalse(os.path.exists(dest))
            self.assertFalse(os.path.exists(dest + ".part"))

    def test_truncated_transfer_is_retried_then_reported(self):
        lock, _ = fx.build(self.dir)
        with fx.RangeServer(self.dir) as srv:
            rel = self.release(srv, lock, retries=2)
            rel.manifest()
            srv.truncate = lambda path, rng: True
            dest = os.path.join(self.dir, "out", "world.db")
            with self.assertRaisesRegex(FetchError, "range request failed"):
                rel.extract("runtime/data/opendaoc.sqlite3.db", dest)
            self.assertFalse(os.path.exists(dest + ".part"))

    def test_one_truncated_transfer_is_retried(self):
        lock, files = fx.build(self.dir)
        with fx.RangeServer(self.dir) as srv:
            rel = self.release(srv, lock, retries=2)
            rel.manifest()
            seen = []
            srv.truncate = lambda path, rng: (seen.append(rng), len(seen) == 1)[1]
            dest = os.path.join(self.dir, "out", "world.db")
            self.assertEqual(rel.extract("runtime/data/opendaoc.sqlite3.db", dest), "ok")
            with open(dest, "rb") as f:
                self.assertEqual(f.read(), files["runtime/data/opendaoc.sqlite3.db"])

    def test_unsafe_archive_path_is_refused(self):
        lock, _ = fx.build(self.dir, extra_names=[fx.ROOT + "../evil.txt"])
        with fx.RangeServer(self.dir) as srv:
            with self.assertRaisesRegex(FetchError, "unsafe archive path"):
                self.release(srv, lock).entries()

    def test_unknown_edition_is_refused(self):
        lock, _ = fx.build(self.dir)
        with fx.RangeServer(self.dir) as srv:
            with self.assertRaisesRegex(FetchError, "unknown edition"):
                self.release(srv, lock).edition("deluxe")

    def test_fetch_client_overlays_only_differences_and_edition_game_dll(self):
        lock, files = fx.build(self.dir)
        client = os.path.join(self.dir, "client")
        os.makedirs(client)
        with open(os.path.join(client, "connect.exe"), "wb") as f:
            f.write(files["runtime/client-opendaoc/app/connect.exe"])  # identical: not fetched
        with open(os.path.join(client, "paths.dat"), "wb") as f:
            f.write(b"[paths]\r\nsettings=Atlas1")  # different: fetched
        with open(os.path.join(client, "game.dll"), "wb") as f:
            f.write(b"stock")  # replaced by the edition's game.dll
        with fx.RangeServer(self.dir) as srv:
            n = fetch_client(self.release(srv, lock), "classic", client, log=lambda *_: None)
        self.assertEqual(n, 3)  # paths.dat, ui/new_window.xml, game.dll
        with open(os.path.join(client, "game.dll"), "rb") as f:
            self.assertEqual(f.read(), files["editions/0.34-no-custom-class/runtime/client-opendaoc/app/game.dll"])
        with open(os.path.join(client, "paths.dat"), "rb") as f:
            self.assertEqual(f.read(), files["runtime/client-opendaoc/app/paths.dat"])
        self.assertTrue(os.path.isfile(os.path.join(client, "ui", "new_window.xml")))

    def test_cli_extract(self):
        lock, files = fx.build(self.dir)
        with fx.RangeServer(self.dir) as srv:
            lock_path = os.path.join(self.dir, "upstream.lock")
            with open(lock_path, "w") as f:
                json.dump(srv.lock(lock), f)
            dest = os.path.join(self.dir, "cli", "logconfig.xml")
            out = subprocess.run([sys.executable, os.path.join(os.path.dirname(HERE), "odaoc_fetch.py"),
                                  "--lock", lock_path, "extract", "runtime/server/config/logconfig.xml", dest],
                                 capture_output=True, text=True)
            self.assertEqual(out.returncode, 0, out.stderr)
            self.assertEqual(sha256_file(dest), hashlib.sha256(files["runtime/server/config/logconfig.xml"]).hexdigest())


if __name__ == "__main__":
    unittest.main()
