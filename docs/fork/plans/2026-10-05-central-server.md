# Central Multiplayer Server (Sub-project 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run the fork's OfflineDAoC server as a Docker container on the owner's server machine for LAN multiplayer, with admin tooling, player client setup for Linux and Windows, CI, and a handoff for the Claude session on that machine.

**Architecture:** A thin, additive fork. Upstream server code is unchanged; new top-level folders add a container build (`deploy/`), Linux admin CLIs that wrap upstream code (`tools/linux/`), player scripts (`client/`) and CI. World data (clean world database, navmeshes, three config files) is fetched from the pinned upstream release with HTTP range requests and verified against a SHA-256 pinned in git. All state lives in one Docker volume.

**Tech Stack:** .NET 10 (server, C# CLIs), Python 3 standard library only (fetch, init, config, accounts, backups, world admin; tests with `unittest`), Bash (entrypoint, `odc`, client scripts, integration tests), Docker + Compose v2, GitHub Actions + GHCR, Proton (Linux client), Windows batch.

**Spec:** `docs/fork/specs/2026-10-05-central-server-design.md`

## Global Constraints

- Upstream pin: release `v0.34b`, commit `169c9b79066d8818f3ddb0ba8b49551801a3319e`, `PACKAGE MANIFEST.sha256` SHA-256 `af2b2efa21af82a2625b72aacd45edbf9cdf4c591cfdb11901c38650e1f01611`.
- Default ports: **10301/tcp** and **10401/udp**. Never default to 10300/10400 (OpenDAoC uses them on the server).
- Editions: exactly `classic` (0.34, no custom class) and `b` (0.34b, Sluaghbinder). Fixed per world.
- Names: compose project `offlinedaoc`; container `offlinedaoc-server`; volume `offlinedaoc-data`; image `ghcr.io/lometur/offlinedaoc`; release tags `v0.34b-fork.N`.
- Images: build `mcr.microsoft.com/dotnet/sdk:10.0`; runtime `mcr.microsoft.com/dotnet/aspnet:10.0` (Debian). Never Alpine (the bundled `SQLite.Interop.dll` needs glibc).
- Runtime env `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0` (the server crashes in invariant mode).
- Container: UID/GID default `1000:1000`; `cap_drop: [ALL]`; `no-new-privileges:true`; `mem_limit` default `10g`; `cpus` default `4`; `stop_grace_period: 120s`; daily backups, keep default `7`.
- Server config: never `serverconfig.example.xml`; `EnableUPnP` and `DetectRegionIP` always `False`.
- Native pathfinding is built from `source/server/Pathing/Detour` and must export all 14 functions; never `source/development-tools/.../Detour`.
- No EA client files are committed to the fork or attached to its releases.
- The only upstream file edited in this sub-project is the root `README.md` (banner).
- Python code uses the standard library only; tests run with `python3 -m unittest`.
- Every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Pushes go to `origin` (`lometur/OfflineDAoC`) only. On this PC git has no stored credentials, so push with: `git -c credential.helper= -c credential.helper='!gh auth git-credential' push origin main`.

## Review Focus

1. **An interrupted first-start download** (container killed or network drop mid-download) must resume on the next start and never start the server on a partial world. Test: Task 3 `test_interrupted_navmesh_download_resumes`.
2. **A volume the container user cannot write** (UID changed in `.env`, or a root-owned volume) must stop with a message that gives the exact `chown` fix, not a crash loop of stack traces. Test: Task 8 `smoke.sh` "unwritable volume" check.
3. **A port already in use** (e.g. `OFFLINEDAOC_PORT=10300` while OpenDAoC runs) must stop with a clear "port in use" message before the server starts. Test: Task 8 `smoke.sh` "port in use" check.
4. **Changing `OFFLINEDAOC_EDITION` for an existing world** must refuse to start, explaining both ways out. Tests: Task 3 `test_edition_mismatch_refuses` and Task 9 `odc_integration.sh`.
5. **A failed backup** (locked or corrupt source, full disk) must keep all previous backups and leave no partial file. Test: Task 7 `test_failed_backup_keeps_old_backups`.

---

## File Structure

| Path | Responsibility |
|---|---|
| `README.md` (modify) | Banner: unofficial fork, link to upstream |
| `.github/README.md` | Fork README shown on the repo page |
| `docs/fork/FORK.md` | What the fork changes, sync and deploy procedures |
| `tools/linux/odaoc_fetch.py` | Library + CLI: verified range-request extraction from the upstream release |
| `tools/linux/tests/release_fixture.py` | Test helper: builds a small fake release and serves it with HTTP range support |
| `tools/linux/tests/test_odaoc_fetch.py` | Tests for the fetch library |
| `tools/linux/accounts/accounts.py` | Account create/list/plvl with the server's password hashing |
| `tools/linux/tests/test_accounts.py` | Tests for accounts |
| `tools/linux/offline-bots/` | C# CLI: the launcher's bot creation (verbatim functions + linked generator) |
| `tools/linux/bot-goals/` | C# CLI: the launcher's bot-goals tab (linked `BotGoalSettings.cs`, configurable port) |
| `tools/linux/progress-import/` | C# CLI: upstream import engine (linked `ImportEngine.cs`) |
| `tools/linux/build.sh` | Builds the three C# CLIs into an output folder |
| `tools/linux/tests/test_cli_tools.sh` | Integration tests of the C# CLIs against a real clean world |
| `deploy/upstream.lock` | Pinned upstream release metadata (JSON) |
| `deploy/bin/init_world.py` | Create/check the world in `/data` |
| `deploy/bin/gen_config.py` | Generate `serverconfig.xml` from environment |
| `deploy/bin/backup.py` | Database snapshots: create, rotate, daily loop |
| `deploy/bin/world_admin.py` | status, restore, new-world, upgrade-world |
| `deploy/bin/add-bots.sh` | In-container wrapper: backup then add bots |
| `deploy/bin/healthcheck.sh` | Container health check |
| `deploy/serverconfig.build.xml` | Placeholder config used only to build |
| `deploy/entrypoint.sh` | Container start: checks, init, config, links, server with console pipe, graceful stop |
| `deploy/Dockerfile` | Image build |
| `.dockerignore` | Build context filter |
| `deploy/compose.yml`, `deploy/.env.example` | Deployment definition |
| `deploy/odc` | Host-side admin command |
| `deploy/tests/test_*.py` | Unit tests for `deploy/bin` |
| `deploy/tests/smoke.sh` | Container smoke test |
| `deploy/tests/odc_integration.sh` | Compose + `odc` integration test |
| `deploy/HANDOFF.md` | Instructions for the Claude session on the server machine |
| `.github/workflows/server-image.yml` | CI: tests, image build, smoke test, publish, release assets |
| `client/linux/setup.sh`, `client/linux/play.sh.in` | Linux client setup and launcher template |
| `client/windows/connect-central.bat` | Windows launcher |
| `client/README.md` | Player instructions |
| `client/tests/test_setup.py`, `client/tests/test_windows_bat.sh` | Client tests |
| `docs/fork/verification/sub1-local.md` | Record of the full local verification (Task 13) |

All commands below run from the repository root, `~/Games/OfflineDAoC/src/OfflineDAoC` on the owner's PC, unless stated otherwise.

---

### Task 1: Fork identity (banners and FORK.md)

**Files:**
- Create: `.github/README.md`
- Modify: `README.md:1` (insert banner above the existing first line)
- Create: `docs/fork/FORK.md`

**Interfaces:**
- Consumes: nothing.
- Produces: the banner wording reused in `client/README.md` (Task 12).

- [ ] **Step 1: Write the check that fails now**

```bash
grep -q "unofficial fork" README.md && grep -q "unofficial fork" .github/README.md && echo BANNERS-OK
```
Expected now: no output (fails).

- [ ] **Step 2: Create `.github/README.md`**

```markdown
> [!IMPORTANT]
> **This is `lometur/OfflineDAoC`, an unofficial fork of [shadowofze/OfflineDAoC](https://github.com/shadowofze/OfflineDAoC).**
> It is **not** the original OfflineDAoC project. For the official game, releases and support, go to
> [shadowofze/OfflineDAoC](https://github.com/shadowofze/OfflineDAoC).

# OfflineDAoC — central server fork

This fork runs OfflineDAoC (Dark Age of Camelot with autonomous bot players, built on OpenDAoC)
as a **central multiplayer server** in Docker, so several people can play in one world together.
Upstream provides the game; this fork adds only deployment, admin tooling and player setup:

| Folder | What it adds |
|---|---|
| [`deploy/`](../deploy) | Docker image, compose file, `odc` admin command, server handoff guide |
| [`tools/linux/`](../tools/linux) | Linux command-line versions of the launcher's bot, bot-goals and progress-import tools |
| [`client/`](../client) | Player setup for Linux (Steam/Proton) and Windows |
| [`docs/fork/`](../docs/fork) | What the fork changes, how it syncs with upstream, design specs and plans |

Everything else is upstream OfflineDAoC, unchanged; see the [upstream README](../README.md).
License: GPL-3.0, like upstream.
```

- [ ] **Step 3: Insert the banner at the top of the root `README.md`**

Insert these lines, followed by one blank line, before the current first line of `README.md`:

```markdown
> [!IMPORTANT]
> **This is `lometur/OfflineDAoC`, an unofficial fork of [shadowofze/OfflineDAoC](https://github.com/shadowofze/OfflineDAoC).**
> It is **not** the original OfflineDAoC project. For the official game, releases and support, go to
> [shadowofze/OfflineDAoC](https://github.com/shadowofze/OfflineDAoC). Fork details: [docs/fork/FORK.md](docs/fork/FORK.md).
```

- [ ] **Step 4: Create `docs/fork/FORK.md`**

```markdown
# About this fork

`lometur/OfflineDAoC` is an unofficial fork of [shadowofze/OfflineDAoC](https://github.com/shadowofze/OfflineDAoC).
It runs OfflineDAoC as a central multiplayer server and keeps its own changes small and additive,
so upstream updates merge cleanly.

## What the fork changes

| Change | Paths | Upstream files touched |
|---|---|---|
| Fork banner | `README.md` (top lines), `.github/README.md` | `README.md` |
| Container deployment | `deploy/`, `.dockerignore`, `.github/workflows/server-image.yml` | none |
| Linux admin CLIs | `tools/linux/` (link upstream sources; see each project file) | none |
| Player setup | `client/` | none |
| Design docs | `docs/fork/` | none |

Server code under `source/server` is unchanged in sub-project 1. Later sub-projects list every
server-code change in this table.

## Syncing with upstream

1. On GitHub, click **Sync fork** (or `git fetch upstream && git merge upstream/main`).
2. If upstream published a new release, update `deploy/upstream.lock` (version, tag, commit, part
   sizes and hashes, manifest SHA-256, edition paths) from the new release's `download-manifest.json`
   and `PACKAGE MANIFEST.sha256`.
3. Push; CI builds and smoke-tests the image.
4. Tag `v<upstream-version>-fork.<n>` to publish the image and release assets.
5. On the server, back up, then follow `deploy/HANDOFF.md` → "Upgrading".

## Releases

Tags `v0.34b-fork.N` publish `ghcr.io/lometur/offlinedaoc:v0.34b-fork.N` and attach two assets:
`offlinedaoc-deploy-<tag>.tar.gz` (compose file, `.env.example`, `odc`, handoff) and
`offlinedaoc-client-<tag>.zip` (player scripts). Neither contains EA game files.
```

- [ ] **Step 5: Run the check**

Run: `grep -q "unofficial fork" README.md && grep -q "unofficial fork" .github/README.md && echo BANNERS-OK`
Expected: `BANNERS-OK`. Also run `head -6 README.md` and confirm the banner precedes upstream's original first line.

- [ ] **Step 6: Commit**

```bash
git add README.md .github/README.md docs/fork/FORK.md
git commit -m "docs(fork): unofficial-fork banners and FORK.md

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Verified release fetch library (`tools/linux/odaoc_fetch.py`)

**Files:**
- Create: `tools/linux/odaoc_fetch.py`
- Create: `tools/linux/tests/__init__.py` (empty)
- Create: `tools/linux/tests/release_fixture.py`
- Test: `tools/linux/tests/test_odaoc_fetch.py`

**Interfaces:**
- Consumes: a lock dict with keys `version`, `root_folder`, `parts` (list of `{url, size}`), `manifest_path`, `manifest_sha256`, `client_prefix`, `navmesh_prefix`, `editions` (`{name: {world_db, game_dll}}`).
- Produces (used by Tasks 3, 7, 8, 11):
  - `class FetchError(Exception)`
  - `sha256_file(path) -> str`, `crc32_file(path) -> int`
  - `class Release(lock: dict, opener=None, retries=4, backoff=1.0)` with `Release.from_lock_file(path, **kw)`, attributes `version: str`, `lock: dict`; methods `entries() -> dict[str, dict]`, `manifest() -> dict[str, str]`, `expected_sha256(rel) -> str`, `extract(rel, dest) -> "ok" | "skip"`, `files_under(prefix) -> list[str]`, `edition(name) -> dict`
  - `fetch_client(release, edition, client_dir, log=print) -> int` (number of files placed)
  - CLI: `odaoc_fetch.py --lock LOCK list [--prefix P]` | `client --edition E --client-dir DIR` | `extract REL DEST`

- [ ] **Step 1: Write the test fixture `tools/linux/tests/release_fixture.py`**

```python
"""Test helper: build a small fake OfflineDAoC release (split ZIP + manifest) and serve it
over HTTP with Range support, so the fetch code can be tested without the network."""
import hashlib
import http.server
import io
import os
import random
import threading
import zipfile

ROOT = "OfflineDAoC-vtest/"


def _blob(seed, size):
    return random.Random(seed).randbytes(size)  # incompressible, so the archive spans several parts


DEFAULT_FILES = {
    "runtime/data/opendaoc.sqlite3.db": _blob(1, 3000),
    "editions/0.34-no-custom-class/runtime/data/opendaoc.sqlite3.db": _blob(2, 3500),
    "editions/0.34-no-custom-class/runtime/client-opendaoc/app/game.dll": _blob(3, 2500),
    "runtime/client-opendaoc/app/game.dll": _blob(4, 2600),
    "runtime/client-opendaoc/app/connect.exe": _blob(5, 900),
    "runtime/client-opendaoc/app/paths.dat": b"[paths]\r\nsettings=OfflineDAoC034",
    "runtime/client-opendaoc/app/ui/new_window.xml": b"<window/>" * 40,
    "runtime/server/navmesh/zone001.nav": _blob(6, 5000),
    "runtime/server/navmesh/zone002.nav": _blob(7, 7000),
    "runtime/server/config/logconfig.xml": b"<nlog/>",
}


def build(directory, files=None, part_size=4096, tamper=None, extra_names=()):
    """Write parts to directory/parts and return (lock_without_urls, files).

    tamper: {rel: wrong_sha} written into the manifest instead of the real hash.
    extra_names: raw archive names added as tiny entries (for path-safety tests).
    """
    files = dict(DEFAULT_FILES if files is None else files)
    tamper = tamper or {}
    manifest = "".join(f"{tamper.get(rel, hashlib.sha256(data).hexdigest())}  {rel}\n"
                       for rel, data in sorted(files.items())).encode()
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr(ROOT, b"")  # directory entry, must be ignored
        for rel, data in sorted(files.items()):
            z.writestr(ROOT + rel, data)
        z.writestr(ROOT + "PACKAGE MANIFEST.sha256", manifest)
        for name in extra_names:
            z.writestr(name, b"x")
    blob = buf.getvalue()
    parts_dir = os.path.join(directory, "parts")
    os.makedirs(parts_dir, exist_ok=True)
    parts = []
    for i in range(0, len(blob), part_size):
        name = f"release.zip.{len(parts) + 1:03d}"
        chunk = blob[i:i + part_size]
        with open(os.path.join(parts_dir, name), "wb") as f:
            f.write(chunk)
        parts.append({"name": name, "size": len(chunk)})
    lock = {
        "version": "test",
        "root_folder": ROOT,
        "parts": parts,
        "manifest_path": "PACKAGE MANIFEST.sha256",
        "manifest_sha256": hashlib.sha256(manifest).hexdigest(),
        "client_prefix": "runtime/client-opendaoc/app/",
        "navmesh_prefix": "runtime/server/navmesh/",
        "config_prefix": "runtime/server/config/",
        "editions": {
            "b": {"world_db": "runtime/data/opendaoc.sqlite3.db",
                  "game_dll": "runtime/client-opendaoc/app/game.dll"},
            "classic": {"world_db": "editions/0.34-no-custom-class/runtime/data/opendaoc.sqlite3.db",
                        "game_dll": "editions/0.34-no-custom-class/runtime/client-opendaoc/app/game.dll"},
        },
    }
    return lock, files


class RangeServer:
    """Serves directory/parts with HTTP Range support. Set .fail to a callable
    (path, range_header) -> bool to make matching requests fail with 503."""

    def __init__(self, directory):
        self.directory = os.path.join(directory, "parts")
        self.requests = []
        self.fail = None
        outer = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                rng = self.headers.get("Range")
                outer.requests.append((self.path, rng))
                if outer.fail and outer.fail(self.path, rng):
                    self.send_error(503)
                    return
                path = os.path.join(outer.directory, os.path.basename(self.path))
                if not os.path.isfile(path) or not rng or not rng.startswith("bytes="):
                    self.send_error(404 if not os.path.isfile(path) else 416)
                    return
                start, end = (int(x) for x in rng[6:].split("-"))
                with open(path, "rb") as f:
                    f.seek(start)
                    data = f.read(end - start + 1)
                self.send_response(206)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def log_message(self, *args):
                pass

        self.httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.httpd.serve_forever, daemon=True)

    @property
    def url(self):
        return f"http://127.0.0.1:{self.httpd.server_address[1]}"

    def lock(self, lock):
        """Return a copy of lock with part URLs pointing at this server."""
        out = dict(lock)
        out["parts"] = [{"url": f"{self.url}/{p['name']}", "size": p["size"]} for p in lock["parts"]]
        return out

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *exc):
        self.httpd.shutdown()
        self.httpd.server_close()
```

- [ ] **Step 2: Write the failing tests `tools/linux/tests/test_odaoc_fetch.py`**

```python
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
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `python3 -m unittest discover -s tools/linux/tests -t tools/linux -v`
Expected: errors with `ModuleNotFoundError: No module named 'odaoc_fetch'`.

- [ ] **Step 4: Implement `tools/linux/odaoc_fetch.py`**

```python
#!/usr/bin/env python3
"""Verified, selective download of files from an OfflineDAoC release (a split ZIP on GitHub)
using HTTP range requests. Nothing downloaded is executed.

Trust chain: deploy/upstream.lock (in git) pins the SHA-256 of the release's
"PACKAGE MANIFEST.sha256"; that manifest gives the SHA-256 of every other file. Each file is
also checked against the CRC32 and size recorded in the ZIP central directory.

CLI:
    odaoc_fetch.py --lock LOCK list [--prefix P]
    odaoc_fetch.py --lock LOCK client --edition classic|b --client-dir DIR
    odaoc_fetch.py --lock LOCK extract REL DEST
"""
import argparse
import hashlib
import json
import os
import struct
import sys
import tempfile
import time
import urllib.error
import urllib.request
import zlib

USER_AGENT = "offlinedaoc-fork-fetch/1"


class FetchError(Exception):
    """A download, checksum or archive-format problem; the message names the file."""


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 22), b""):
            h.update(block)
    return h.hexdigest()


def crc32_file(path):
    crc = 0
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 22), b""):
            crc = zlib.crc32(block, crc)
    return crc & 0xFFFFFFFF


def safe_rel(rel):
    """Reject archive names that could escape a destination folder."""
    parts = rel.split("/")
    if not rel or rel.startswith("/") or "\\" in rel or ":" in rel or ".." in parts:
        raise FetchError(f"unsafe archive path: {rel!r}")
    return rel


class Release:
    def __init__(self, lock, opener=None, retries=4, backoff=1.0):
        self.lock = lock
        self.version = lock["version"]
        self.root = lock["root_folder"] if lock["root_folder"].endswith("/") else lock["root_folder"] + "/"
        self.urls = [p["url"] for p in lock["parts"]]
        self.sizes = [int(p["size"]) for p in lock["parts"]]
        self.starts = [sum(self.sizes[:i]) for i in range(len(self.sizes))]
        self.total = sum(self.sizes)
        self.manifest_path = lock.get("manifest_path", "PACKAGE MANIFEST.sha256")
        self.manifest_sha256 = lock["manifest_sha256"].lower()
        self._open = opener or urllib.request.urlopen
        self.retries = max(1, retries)
        self.backoff = backoff
        self._entries = None
        self._manifest = None

    @classmethod
    def from_lock_file(cls, path, **kwargs):
        with open(path, encoding="utf-8") as f:
            return cls(json.load(f), **kwargs)

    # ---- bytes across the split parts ----
    def _get(self, part, start, end_incl):
        want = end_incl - start + 1
        req = urllib.request.Request(self.urls[part], headers={
            "Range": f"bytes={start}-{end_incl}", "User-Agent": USER_AGENT})
        last = "no attempt"
        for attempt in range(self.retries):
            try:
                with self._open(req, timeout=60) as resp:
                    status = getattr(resp, "status", 206)
                    data = resp.read()
                if status == 206 and len(data) == want:
                    return data
                last = f"HTTP {status}, {len(data)} of {want} bytes"
            except (urllib.error.URLError, OSError) as e:
                last = str(e)
            if attempt + 1 < self.retries:
                time.sleep(self.backoff * (2 ** attempt))
        raise FetchError(f"range request failed for part {part + 1} bytes {start}-{end_incl}: {last}")

    def read(self, offset, length):
        if offset < 0 or length < 0 or offset + length > self.total:
            raise FetchError(f"read outside the archive: {offset}+{length} > {self.total}")
        out = bytearray()
        while length > 0:
            part = max(i for i, s in enumerate(self.starts) if s <= offset)
            local = offset - self.starts[part]
            take = min(length, self.sizes[part] - local)
            out += self._get(part, local, local + take - 1)
            offset += take
            length -= take
        return bytes(out)

    def _iter_read(self, offset, length, chunk=8 << 20):
        while length > 0:
            n = min(length, chunk)
            yield self.read(offset, n)
            offset += n
            length -= n

    # ---- archive index ----
    def entries(self):
        """{relative path: entry} from the ZIP or ZIP64 central directory."""
        if self._entries is not None:
            return self._entries
        tail_len = min(65536 + 22, self.total)
        tail = self.read(self.total - tail_len, tail_len)
        i64 = tail.rfind(b"PK\x06\x06")
        if i64 >= 0:
            (_, _, _, _, _, _, _, _, cd_size, cd_off) = struct.unpack_from("<IQHHIIQQQQ", tail, i64)
        else:
            i = tail.rfind(b"PK\x05\x06")
            if i < 0:
                raise FetchError("end of central directory not found; not a ZIP release")
            (_, _, _, _, _, cd_size, cd_off, _) = struct.unpack_from("<IHHHHIIH", tail, i)
        cd = self.read(cd_off, cd_size)
        entries, p = {}, 0
        while p < len(cd):
            (sig, _vm, _vn, flag, meth, _mt, _md, crc, csz, usz, nl, el, cl, _dn, _ia, _ea, off) = \
                struct.unpack_from("<IHHHHHHIIIHHHHHII", cd, p)
            if sig != 0x02014B50:
                raise FetchError("corrupt central directory")
            name = cd[p + 46:p + 46 + nl].decode("utf-8" if flag & 0x800 else "cp437")
            extra = cd[p + 46 + nl:p + 46 + nl + el]
            q = 0
            while q + 4 <= len(extra):
                hid, hl = struct.unpack_from("<HH", extra, q)
                body, b = extra[q + 4:q + 4 + hl], 0
                if hid == 1:
                    if usz == 0xFFFFFFFF:
                        usz = struct.unpack_from("<Q", body, b)[0]
                        b += 8
                    if csz == 0xFFFFFFFF:
                        csz = struct.unpack_from("<Q", body, b)[0]
                        b += 8
                    if off == 0xFFFFFFFF:
                        off = struct.unpack_from("<Q", body, b)[0]
                        b += 8
                q += 4 + hl
            p += 46 + nl + el + cl
            if name.endswith("/"):
                continue
            if not name.startswith(self.root):
                raise FetchError(f"entry outside the release root: {name!r}")
            rel = safe_rel(name[len(self.root):])
            entries[rel] = dict(flag=flag, meth=meth, crc=crc, csz=csz, usz=usz, off=off)
        if not entries:
            raise FetchError("the release archive lists no files")
        self._entries = entries
        return entries

    def manifest(self):
        """{relative path: sha256}, after checking the manifest against the pinned hash."""
        if self._manifest is not None:
            return self._manifest
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "manifest")
            self._extract_entry(self.manifest_path, path, None)
            got = sha256_file(path)
            if got != self.manifest_sha256:
                raise FetchError(f"{self.manifest_path} SHA-256 {got} does not match the pinned {self.manifest_sha256}")
            sums = {}
            with open(path, encoding="utf-8") as f:
                for line in f:
                    line = line.rstrip("\r\n")
                    if len(line) > 66 and line[64:66] == "  ":
                        sums[line[66:]] = line[:64].lower()
        self._manifest = sums
        return sums

    def _extract_entry(self, rel, dest, sha_expected):
        e = self.entries().get(rel)
        if e is None:
            raise FetchError(f"not in release {self.version}: {rel}")
        if e["flag"] & 1:
            raise FetchError(f"encrypted entry: {rel}")
        if e["meth"] not in (0, 8):
            raise FetchError(f"unsupported compression method {e['meth']}: {rel}")
        hdr = self.read(e["off"], 30)
        sig, _, _, _, _, _, _, _, _, nl, el = struct.unpack("<IHHHHHIIIHH", hdr)
        if sig != 0x04034B50:
            raise FetchError(f"bad local header: {rel}")
        os.makedirs(os.path.dirname(os.path.abspath(dest)), exist_ok=True)
        tmp = dest + ".part"
        dec = zlib.decompressobj(-15) if e["meth"] == 8 else None
        crc, size, h = 0, 0, hashlib.sha256()
        try:
            with open(tmp, "wb") as f:
                for chunk in self._iter_read(e["off"] + 30 + nl + el, e["csz"]):
                    data = dec.decompress(chunk) if dec else chunk
                    crc = zlib.crc32(data, crc)
                    size += len(data)
                    h.update(data)
                    f.write(data)
                if dec:
                    data = dec.flush()
                    crc = zlib.crc32(data, crc)
                    size += len(data)
                    h.update(data)
                    f.write(data)
            if size != e["usz"] or (crc & 0xFFFFFFFF) != e["crc"]:
                raise FetchError(f"CRC/size mismatch: {rel}")
            if sha_expected is not None and h.hexdigest() != sha_expected:
                raise FetchError(f"SHA-256 mismatch against the release manifest: {rel}")
            os.replace(tmp, dest)
        finally:
            if os.path.exists(tmp):
                os.remove(tmp)

    def expected_sha256(self, rel):
        sha = self.manifest().get(rel)
        if sha is None:
            raise FetchError(f"no SHA-256 in the release manifest for {rel}")
        return sha

    def extract(self, rel, dest):
        """Download rel to dest unless dest already holds it. Returns 'ok' or 'skip'."""
        sha = self.expected_sha256(safe_rel(rel))
        if os.path.isfile(dest) and sha256_file(dest) == sha:
            return "skip"
        self._extract_entry(rel, dest, sha)
        return "ok"

    def files_under(self, prefix):
        return sorted(r for r in self.entries() if r.startswith(prefix))

    def edition(self, name):
        editions = self.lock["editions"]
        if name not in editions:
            raise FetchError(f"unknown edition {name!r}; expected one of {sorted(editions)}")
        return editions[name]


def fetch_client(release, edition, client_dir, log=print):
    """Overlay the release's client files for `edition` onto a copy of the base 1.127 client.
    Only files that are missing or differ (size/CRC32) are downloaded; game.dll always comes
    from the edition."""
    prefix = release.lock["client_prefix"]
    game_dll_rel = release.edition(edition)["game_dll"]
    entries = release.entries()
    plan = []
    for rel in release.files_under(prefix):
        if rel == prefix + "game.dll":
            continue
        local = os.path.join(client_dir, rel[len(prefix):])
        e = entries[rel]
        if not os.path.isfile(local) or os.path.getsize(local) != e["usz"] or crc32_file(local) != e["crc"]:
            plan.append((rel, local))
    plan.append((game_dll_rel, os.path.join(client_dir, "game.dll")))
    for i, (rel, local) in enumerate(plan, 1):
        release.extract(rel, local)
        if i % 20 == 0 or i == len(plan):
            log(f"  {i}/{len(plan)} client files verified")
    return len(plan)


def main(argv=None):
    ap = argparse.ArgumentParser(description="Verified selective download from an OfflineDAoC release.")
    ap.add_argument("--lock", required=True, help="path to deploy/upstream.lock")
    sub = ap.add_subparsers(dest="cmd", required=True)
    ls = sub.add_parser("list")
    ls.add_argument("--prefix", default="")
    cl = sub.add_parser("client")
    cl.add_argument("--edition", required=True)
    cl.add_argument("--client-dir", required=True)
    ex = sub.add_parser("extract")
    ex.add_argument("rel")
    ex.add_argument("dest")
    a = ap.parse_args(argv)
    try:
        release = Release.from_lock_file(a.lock)
        if a.cmd == "list":
            for rel in release.files_under(a.prefix):
                print(f"{release.entries()[rel]['usz']:>12} {rel}")
        elif a.cmd == "client":
            n = fetch_client(release, a.edition, a.client_dir)
            print(f"Client ready: {n} files verified against release {release.version} ({a.edition}).")
        else:
            print(f"{release.extract(a.rel, a.dest)} {a.rel}")
    except FetchError as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `python3 -m unittest discover -s tools/linux/tests -t tools/linux -v`
Expected: `Ran 9 tests ... OK`.

- [ ] **Step 6: Commit**

```bash
chmod +x tools/linux/odaoc_fetch.py
git add tools/linux/odaoc_fetch.py tools/linux/tests/__init__.py tools/linux/tests/release_fixture.py tools/linux/tests/test_odaoc_fetch.py
git commit -m "feat(tools): verified range-request fetch from the upstream release

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Upstream lock and world initialisation

**Files:**
- Create: `deploy/upstream.lock`
- Create: `deploy/bin/init_world.py`
- Create: `deploy/tests/__init__.py` (empty)
- Test: `deploy/tests/test_init_world.py`

**Interfaces:**
- Consumes: Task 2 `Release`, `FetchError`, `sha256_file`.
- Produces (used by Tasks 7, 8, 9):
  - `init_world.world_paths(data) -> {"meta", "db", "navmesh"}`
  - `init_world.ensure_navmesh(release, navmesh_dir, seed=None, log=print) -> (total, fetched)`
  - `init_world.init(release, data, edition, skip_navmesh=False, seed_navmesh=None, log=print) -> int` (0 ok, 3 edition mismatch, 4 version mismatch; raises `FetchError` on download failure)
  - `init_world.write_meta(path, meta)`
  - `/data/world.json`: `{"version": str, "edition": "classic"|"b", "navmesh": bool, "created_utc": str}`
  - CLI exit codes: 0, 2 (download failed), 3, 4.

- [ ] **Step 1: Create `deploy/upstream.lock`**

```json
{
  "upstream_repo": "shadowofze/OfflineDAoC",
  "version": "0.34b",
  "tag": "v0.34b",
  "commit": "169c9b79066d8818f3ddb0ba8b49551801a3319e",
  "root_folder": "OfflineDAoC-v0.34b/",
  "parts": [
    {"url": "https://github.com/shadowofze/OfflineDAoC/releases/download/v0.34b/OfflineDAoC-v0.34b.zip.001", "size": 1900000000, "sha256": "5d1b8321ac7be484814ec28cea0f2d93c5658bf3dbbfa19e013e15fba9174f31"},
    {"url": "https://github.com/shadowofze/OfflineDAoC/releases/download/v0.34b/OfflineDAoC-v0.34b.zip.002", "size": 1900000000, "sha256": "449946e57d41080f8745fbf6a60d2575d89cfd9ee22116871ce00d8d703dffc5"},
    {"url": "https://github.com/shadowofze/OfflineDAoC/releases/download/v0.34b/OfflineDAoC-v0.34b.zip.003", "size": 1267511073, "sha256": "71429d76e3c19b45d60f2f87a81228b87aeec5fa04f483f6d1e515b7e8db1698"}
  ],
  "archive_sha256": "2589196f2d6d7207f38389d7b4f5591bd61e2d9acf5f9662a02e54f17f211a7a",
  "manifest_path": "PACKAGE MANIFEST.sha256",
  "manifest_sha256": "af2b2efa21af82a2625b72aacd45edbf9cdf4c591cfdb11901c38650e1f01611",
  "client_prefix": "runtime/client-opendaoc/app/",
  "navmesh_prefix": "runtime/server/navmesh/",
  "config_prefix": "runtime/server/config/",
  "config_files": ["logconfig.xml", "invalidnames.txt", "MailConfig.xml"],
  "editions": {
    "b": {
      "world_db": "runtime/data/opendaoc.sqlite3.db",
      "game_dll": "runtime/client-opendaoc/app/game.dll"
    },
    "classic": {
      "world_db": "editions/0.34-no-custom-class/runtime/data/opendaoc.sqlite3.db",
      "game_dll": "editions/0.34-no-custom-class/runtime/client-opendaoc/app/game.dll"
    }
  }
}
```

Verify it against the real release (network; about 7.5 MB of range requests):

Run: `python3 tools/linux/odaoc_fetch.py --lock deploy/upstream.lock list --prefix runtime/server/config/`
Expected: four lines including `logconfig.xml`, `invalidnames.txt`, `MailConfig.xml`, `serverconfig.xml`, and exit code 0 (this proves the pinned manifest hash matches).

- [ ] **Step 2: Write the failing tests `deploy/tests/test_init_world.py`**

```python
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
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `python3 -m unittest discover -s deploy/tests -t deploy -p 'test_init_world.py' -v`
Expected: `ModuleNotFoundError: No module named 'init_world'`.

- [ ] **Step 4: Implement `deploy/bin/init_world.py`**

```python
#!/usr/bin/env python3
"""Create or check the world in /data from the pinned upstream release.

First start: download the edition's clean world database and the navmeshes (all verified), then
write /data/world.json LAST, so an interrupted first start simply resumes on the next start.
Later starts: refuse if OFFLINEDAOC_EDITION or the image's upstream version differs from the world's.
"""
import argparse
import datetime
import json
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path[:0] = [HERE, os.path.join(HERE, "..", "..", "tools", "linux")]

from odaoc_fetch import FetchError, Release, sha256_file  # noqa: E402

EXIT_DOWNLOAD = 2
EXIT_EDITION = 3
EXIT_VERSION = 4


def world_paths(data):
    return {
        "meta": os.path.join(data, "world.json"),
        "db": os.path.join(data, "world", "opendaoc.sqlite3.db"),
        "navmesh": os.path.join(data, "navmesh"),
    }


def write_meta(path, meta):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(meta, f, indent=2)
        f.write("\n")
    os.replace(tmp, path)


def ensure_navmesh(release, navmesh_dir, seed=None, log=print):
    prefix = release.lock["navmesh_prefix"]
    files = release.files_under(prefix)
    os.makedirs(navmesh_dir, exist_ok=True)
    fetched = 0
    for i, rel in enumerate(files, 1):
        name = rel[len(prefix):]
        dest = os.path.join(navmesh_dir, name)
        if seed and not os.path.isfile(dest):
            src = os.path.join(seed, name)
            if os.path.isfile(src) and sha256_file(src) == release.expected_sha256(rel):
                shutil.copyfile(src, dest + ".part")
                os.replace(dest + ".part", dest)
        if release.extract(rel, dest) == "ok":
            fetched += 1
        if i % 10 == 0 or i == len(files):
            log(f"  navmesh {i}/{len(files)}")
    return len(files), fetched


def init(release, data, edition, skip_navmesh=False, seed_navmesh=None, log=print):
    p = world_paths(data)
    if os.path.isfile(p["meta"]):
        with open(p["meta"], encoding="utf-8") as f:
            meta = json.load(f)
        if meta["edition"] != edition:
            log(f"ERROR: this world was created as edition '{meta['edition']}', but OFFLINEDAOC_EDITION is "
                f"'{edition}'. Set OFFLINEDAOC_EDITION={meta['edition']} in .env, or start a new world on purpose "
                f"with: odc new-world --edition {edition}")
            return EXIT_EDITION
        if meta["version"] != release.version:
            log(f"ERROR: this world is from upstream {meta['version']}, but this image is for {release.version}. "
                "Deploy the matching image, or run: odc upgrade-world (it backs up first).")
            return EXIT_VERSION
        if not skip_navmesh and not meta.get("navmesh"):
            ensure_navmesh(release, p["navmesh"], seed_navmesh, log)
            meta["navmesh"] = True
            write_meta(p["meta"], meta)
        return 0
    log(f"Creating a new '{edition}' world from upstream {release.version}...")
    release.extract(release.edition(edition)["world_db"], p["db"])
    log("  world database ready")
    navmesh = False
    if not skip_navmesh:
        total, fetched = ensure_navmesh(release, p["navmesh"], seed_navmesh, log)
        navmesh = True
        log(f"  navmeshes ready ({total} files, {fetched} downloaded)")
    write_meta(p["meta"], {
        "version": release.version,
        "edition": edition,
        "navmesh": navmesh,
        "created_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
    })
    log("World ready.")
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description="Create or check the OfflineDAoC world in a data folder.")
    ap.add_argument("--lock", required=True)
    ap.add_argument("--data", required=True)
    ap.add_argument("--edition", required=True, choices=["classic", "b"])
    ap.add_argument("--skip-navmesh", action="store_true")
    ap.add_argument("--seed-navmesh", help="folder of already-downloaded zoneNNN.nav files to verify and reuse")
    a = ap.parse_args(argv)
    try:
        return init(Release.from_lock_file(a.lock), a.data, a.edition, a.skip_navmesh, a.seed_navmesh)
    except FetchError as e:
        print(f"ERROR: world download failed: {e}. The server was not started; the next start resumes.",
              file=sys.stderr)
        return EXIT_DOWNLOAD


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `python3 -m unittest discover -s deploy/tests -t deploy -p 'test_init_world.py' -v`
Expected: `Ran 7 tests ... OK`.

- [ ] **Step 6: Commit**

```bash
chmod +x deploy/bin/init_world.py
git add deploy/upstream.lock deploy/bin/init_world.py deploy/tests/__init__.py deploy/tests/test_init_world.py
git commit -m "feat(deploy): pinned upstream lock and verified world initialisation

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Server config generation

**Files:**
- Create: `deploy/bin/gen_config.py`
- Test: `deploy/tests/test_gen_config.py`

**Interfaces:**
- Consumes: environment variables `OFFLINEDAOC_LISTEN_IP`, `OFFLINEDAOC_PORT`, `OFFLINEDAOC_UDP_PORT`, `OFFLINEDAOC_AUTO_ACCOUNTS`, `OFFLINEDAOC_SERVER_NAME`.
- Produces (used by Task 8): `gen_config.settings(env) -> dict` (raises `ConfigError`), `gen_config.render(settings, data) -> str`; CLI `gen_config.py --data DIR --out FILE` (exit 0, or 2 with `ERROR: ...`).

- [ ] **Step 1: Write the failing tests `deploy/tests/test_gen_config.py`**

```python
import os
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
BIN = os.path.join(HERE, "..", "bin")
sys.path.insert(0, BIN)

import gen_config  # noqa: E402


def parse(xml):
    return {child.tag: (child.text or "") for child in ET.fromstring(xml).find("Server")}


class GenConfigTests(unittest.TestCase):
    def test_defaults(self):
        cfg = parse(gen_config.render(gen_config.settings({}), "/data"))
        self.assertEqual(cfg["Port"], "10301")
        self.assertEqual(cfg["UdpPort"], "10401")
        self.assertEqual(cfg["RegionPort"], "10401")
        self.assertEqual(cfg["IP"], "0.0.0.0")
        self.assertEqual(cfg["EnableUPnP"], "False")
        self.assertEqual(cfg["DetectRegionIP"], "False")
        self.assertEqual(cfg["AutoAccountCreation"], "True")
        self.assertEqual(cfg["DBType"], "SQLITE")
        self.assertIn("Data Source=/data/world/opendaoc.sqlite3.db;", cfg["DBConnectionString"])
        self.assertEqual(cfg["MetricsEnabled"], "false")

    def test_overrides_and_case_insensitive_bool(self):
        s = gen_config.settings({"OFFLINEDAOC_PORT": "10311", "OFFLINEDAOC_UDP_PORT": "10411",
                                 "OFFLINEDAOC_AUTO_ACCOUNTS": "false", "OFFLINEDAOC_LISTEN_IP": "192.168.1.64"})
        cfg = parse(gen_config.render(s, "/data"))
        self.assertEqual((cfg["Port"], cfg["UdpPort"], cfg["AutoAccountCreation"]), ("10311", "10411", "False"))
        self.assertEqual((cfg["IP"], cfg["RegionIP"], cfg["UdpIP"]), ("192.168.1.64",) * 3)

    def test_server_name_is_xml_escaped(self):
        cfg = parse(gen_config.render(gen_config.settings({"OFFLINEDAOC_SERVER_NAME": "Bob & <Friends>"}), "/data"))
        self.assertEqual(cfg["ServerName"], "Bob & <Friends>")

    def test_invalid_values(self):
        for env, msg in [({"OFFLINEDAOC_PORT": "70000"}, "OFFLINEDAOC_PORT"),
                         ({"OFFLINEDAOC_UDP_PORT": "abc"}, "OFFLINEDAOC_UDP_PORT"),
                         ({"OFFLINEDAOC_LISTEN_IP": "my-host"}, "OFFLINEDAOC_LISTEN_IP"),
                         ({"OFFLINEDAOC_AUTO_ACCOUNTS": "maybe"}, "OFFLINEDAOC_AUTO_ACCOUNTS"),
                         ({"OFFLINEDAOC_SERVER_NAME": "   "}, "OFFLINEDAOC_SERVER_NAME")]:
            with self.subTest(env=env), self.assertRaisesRegex(gen_config.ConfigError, msg):
                gen_config.settings(env)

    def test_cli_writes_file_and_reports_errors(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "serverconfig.xml")
            env = dict(os.environ, OFFLINEDAOC_PORT="10391")
            r = subprocess.run([sys.executable, os.path.join(BIN, "gen_config.py"), "--data", "/data", "--out", out],
                               env=env, capture_output=True, text=True)
            self.assertEqual(r.returncode, 0, r.stderr)
            with open(out, encoding="utf-8") as f:
                self.assertEqual(parse(f.read())["Port"], "10391")
            env["OFFLINEDAOC_PORT"] = "0"
            r = subprocess.run([sys.executable, os.path.join(BIN, "gen_config.py"), "--data", "/data", "--out", out],
                               env=env, capture_output=True, text=True)
            self.assertEqual(r.returncode, 2)
            self.assertIn("ERROR: OFFLINEDAOC_PORT", r.stderr)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `python3 -m unittest discover -s deploy/tests -t deploy -p 'test_gen_config.py' -v`
Expected: `ModuleNotFoundError: No module named 'gen_config'`.

- [ ] **Step 3: Implement `deploy/bin/gen_config.py`**

```python
#!/usr/bin/env python3
"""Write serverconfig.xml from OFFLINEDAOC_* environment variables (see deploy/compose.yml).
UPnP and region-IP detection are always off; the database lives in the data volume."""
import argparse
import ipaddress
import os
import sys
from xml.sax.saxutils import escape

DEFAULTS = {
    "OFFLINEDAOC_LISTEN_IP": "0.0.0.0",
    "OFFLINEDAOC_PORT": "10301",
    "OFFLINEDAOC_UDP_PORT": "10401",
    "OFFLINEDAOC_AUTO_ACCOUNTS": "True",
    "OFFLINEDAOC_SERVER_NAME": "OfflineDAoC (lometur fork)",
}


class ConfigError(Exception):
    pass


def settings(env):
    s = {key: (env.get(key) or default) for key, default in DEFAULTS.items()}
    try:
        ipaddress.IPv4Address(s["OFFLINEDAOC_LISTEN_IP"])
    except ValueError:
        raise ConfigError(f"OFFLINEDAOC_LISTEN_IP must be an IPv4 address, got {s['OFFLINEDAOC_LISTEN_IP']!r}")
    for key in ("OFFLINEDAOC_PORT", "OFFLINEDAOC_UDP_PORT"):
        if not s[key].isdigit() or not 1 <= int(s[key]) <= 65535:
            raise ConfigError(f"{key} must be a port number from 1 to 65535, got {s[key]!r}")
    auto = s["OFFLINEDAOC_AUTO_ACCOUNTS"].strip().lower()
    if auto not in ("true", "false"):
        raise ConfigError(f"OFFLINEDAOC_AUTO_ACCOUNTS must be True or False, got {s['OFFLINEDAOC_AUTO_ACCOUNTS']!r}")
    s["OFFLINEDAOC_AUTO_ACCOUNTS"] = "True" if auto == "true" else "False"
    if not s["OFFLINEDAOC_SERVER_NAME"].strip():
        raise ConfigError("OFFLINEDAOC_SERVER_NAME must not be empty")
    return s


def render(s, data):
    ip = s["OFFLINEDAOC_LISTEN_IP"]
    db = escape(os.path.join(data, "world", "opendaoc.sqlite3.db"))
    name = escape(s["OFFLINEDAOC_SERVER_NAME"])
    return f"""<?xml version="1.0" encoding="utf-8"?>
<!-- Generated at container start by /app/bin/gen_config.py. Change deploy/.env instead of this file. -->
<root>
    <Server>
        <Port>{s['OFFLINEDAOC_PORT']}</Port>
        <IP>{ip}</IP>
        <RegionIP>{ip}</RegionIP>
        <RegionPort>{s['OFFLINEDAOC_UDP_PORT']}</RegionPort>
        <UdpIP>{ip}</UdpIP>
        <UdpPort>{s['OFFLINEDAOC_UDP_PORT']}</UdpPort>
        <EnableUPnP>False</EnableUPnP>
        <DetectRegionIP>False</DetectRegionIP>
        <ServerName>{name}</ServerName>
        <ServerNameShort>OfflineFork</ServerNameShort>
        <LogConfigFile>./config/logconfig.xml</LogConfigFile>
        <ScriptCompilationTarget>./lib/GameServerScripts.dll</ScriptCompilationTarget>
        <ScriptAssemblies> </ScriptAssemblies>
        <EnableCompilation>True</EnableCompilation>
        <AutoAccountCreation>{s['OFFLINEDAOC_AUTO_ACCOUNTS']}</AutoAccountCreation>
        <GameType>Normal</GameType>
        <CheatLoggerName>cheats</CheatLoggerName>
        <GMActionLoggerName>gmactions</GMActionLoggerName>
        <InvalidNamesFile>./config/invalidnames.txt</InvalidNamesFile>
        <DBType>SQLITE</DBType>
        <DBConnectionString>Data Source={db};Version=3;Pooling=True;Journal Mode=WAL;Synchronous=Normal;Foreign Keys=True;Default Timeout=60</DBConnectionString>
        <DBAutosave>True</DBAutosave>
        <DBAutosaveInterval>10</DBAutosaveInterval>
        <MetricsEnabled>false</MetricsEnabled>
        <MetricsInterval>60s</MetricsInterval>
        <OtlpEndpoint>http://127.0.0.1:4317</OtlpEndpoint>
    </Server>
</root>
"""


def main(argv=None):
    ap = argparse.ArgumentParser(description="Generate the server's serverconfig.xml from environment variables.")
    ap.add_argument("--data", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args(argv)
    try:
        xml = render(settings(os.environ), a.data)
    except ConfigError as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 2
    tmp = a.out + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(xml)
    os.replace(tmp, a.out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `python3 -m unittest discover -s deploy/tests -t deploy -p 'test_gen_config.py' -v`
Expected: `Ran 5 tests ... OK`.

- [ ] **Step 5: Commit**

```bash
chmod +x deploy/bin/gen_config.py
git add deploy/bin/gen_config.py deploy/tests/test_gen_config.py
git commit -m "feat(deploy): generate serverconfig.xml from environment

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Linux CLIs over upstream code (offline-bots, bot-goals, progress-import)

**Files:**
- Create: `tools/linux/offline-bots/offline-bots.csproj`, `tools/linux/offline-bots/Program.cs`
- Create: `tools/linux/bot-goals/bot-goals.csproj`, `tools/linux/bot-goals/Program.cs`
- Create: `tools/linux/progress-import/progress-import.csproj`, `tools/linux/progress-import/Program.cs`
- Create: `tools/linux/build.sh`
- Modify: `.gitignore` is not touched; add `tools/linux/.gitignore` with `bin/` and `obj/`
- Test: `tools/linux/tests/test_cli_tools.sh`

**Interfaces:**
- Consumes: upstream sources `source/tools/OfflineDaoc.Launcher/MainForm.cs` (copied functions), `source/tools/OfflineDaoc.Launcher/BotCharacterGenerator.cs` (linked), `source/server/GameServer/bots/autonomous/BotGoalSettings.cs` (linked), `source/tools/OfflineDaoc.ProgressImport/ImportEngine.cs` and `progress-policy.json` (linked).
- Produces (used by Tasks 7, 8, 9):
  - `build.sh OUT` → `OUT/offline-bots/offline-bots.dll`, `OUT/bot-goals/bot-goals.dll`, `OUT/progress-import/progress-import.dll`
  - `dotnet offline-bots.dll add <db> <realm 1|2|3> <count 1|10|100>[x<level 1|50>]`
  - `dotnet bot-goals.dll [--port N] <dir> show | reset | set <1-19|20-49|50> <solo> <group> <rvr> | import <file>` (writes `<dir>/bot-goals.json`; refuses writes while TCP port N, default 10301, is listening)
  - `dotnet progress-import.dll --import <old> <new> --replace-progress [report] [--leave-sluaghbinder-bots]` (exit 0/1, report file)

- [ ] **Step 1: Write the failing integration test `tools/linux/tests/test_cli_tools.sh`**

This test needs a clean classic world database. On the owner's PC that is
`~/Games/OfflineDAoC/editions/0.34-no-custom-class/runtime/data/opendaoc.sqlite3.db`; in CI, Task 10
downloads it with `init_world.py --skip-navmesh`.

```bash
#!/usr/bin/env bash
# Integration tests for the Linux CLIs. Usage: test_cli_tools.sh <tools-out-dir>
# Needs ODC_TEST_WORLD = path to a clean classic world database (opendaoc.sqlite3.db).
set -euo pipefail
OUT="${1:?usage: $0 <tools-out-dir>}"
: "${ODC_TEST_WORLD:?set ODC_TEST_WORLD to a clean classic world database}"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0
T="$(mktemp -d)"; trap 'rm -rf "$T"' EXIT
pass=0; fail() { echo "FAIL: $*" >&2; exit 1; }; ok() { pass=$((pass + 1)); echo "ok - $*"; }
q() { sqlite3 "$1" "$2"; }

# The upstream importer refuses while anything listens on TCP 10300 or a CoreServer process runs.
# Run it in private network and process namespaces when port 10300 is busy on this machine.
importer() {
    if ss -ltn 'sport = :10300' | grep -q 10300; then
        bwrap --ro-bind / / --dev /dev --proc /proc --bind "$T" "$T" --unshare-net --unshare-pid \
            dotnet "$OUT/progress-import/progress-import.dll" "$@"
    else
        dotnet "$OUT/progress-import/progress-import.dll" "$@"
    fi
}

# offline-bots
mkdir -p "$T/w"; cp "$ODC_TEST_WORLD" "$T/w/opendaoc.sqlite3.db"
dotnet "$OUT/offline-bots/offline-bots.dll" add "$T/w/opendaoc.sqlite3.db" 2 1x1 >/dev/null
[[ "$(q "$T/w/opendaoc.sqlite3.db" 'SELECT count(*) FROM offline_world_bots')" == 1 ]] || fail "offline-bots did not add a bot"
[[ "$(q "$T/w/opendaoc.sqlite3.db" "SELECT Value FROM offline_population_settings WHERE Key='ActiveTarget'")" == 1 ]] || fail "population target not updated"
ok "offline-bots adds a level-1 Midgard bot and updates the population target"
dotnet "$OUT/offline-bots/offline-bots.dll" add "$T/w/opendaoc.sqlite3.db" 1 1x50 >/dev/null
(( $(q "$T/w/opendaoc.sqlite3.db" "SELECT count(*) FROM Inventory WHERE OwnerID LIKE 'offlinebot:%'") >= 15 )) || fail "level-50 bot has no gear"
ok "offline-bots gives a level-50 bot its gear"

# bot-goals (port 9 is never listening, so writes are allowed)
dotnet "$OUT/bot-goals/bot-goals.dll" --port 9 "$T/w" set 50 10 30 60 >/dev/null
[[ -f "$T/w/bot-goals.json" ]] && dotnet "$OUT/bot-goals/bot-goals.dll" --port 9 "$T/w" show | grep -qE '^  50 .*60%$' \
    || fail "bot-goals set did not write"
ok "bot-goals set writes bot-goals.json"
if dotnet "$OUT/bot-goals/bot-goals.dll" --port 9 "$T/w" set 50 10 30 50 2>"$T/err"; then fail "bad total accepted"; fi
grep -q "add up to exactly 100" "$T/err" || fail "bad total message missing"
if dotnet "$OUT/bot-goals/bot-goals.dll" --port 9 "$T/w" set 1-19 50 40 10 2>"$T/err"; then fail "low-level RvR accepted"; fi
grep -q "cannot have RvR" "$T/err" || fail "low-level RvR message missing"
ok "bot-goals rejects invalid percentages with upstream's messages"
python3 -m http.server 10399 --bind 127.0.0.1 >/dev/null 2>&1 & srv=$!; sleep 1
if dotnet "$OUT/bot-goals/bot-goals.dll" --port 10399 "$T/w" reset 2>"$T/err"; then kill "$srv"; fail "write allowed while port busy"; fi
kill "$srv"; grep -q "Stop the server" "$T/err" || fail "busy-port message missing"
ok "bot-goals refuses to save while the server port is listening"

# progress-import round trip: old = world with bots and an account, new = clean world
mkdir -p "$T/old/runtime/data" "$T/new/runtime/data"
cp "$T/w/opendaoc.sqlite3.db" "$T/old/runtime/data/opendaoc.sqlite3.db"
sqlite3 "$T/old/runtime/data/opendaoc.sqlite3.db" "INSERT INTO Account (Name, Password, CreationDate, PrivLevel, Account_ID) VALUES ('tester', '##00', '2026-10-05 10:00:00.000000', 1, 'acc-1')"
cp "$ODC_TEST_WORLD" "$T/new/runtime/data/opendaoc.sqlite3.db"
importer --import "$T/old" "$T/new" --replace-progress "$T/report.txt" || { cat "$T/report.txt"; fail "import failed"; }
[[ "$(q "$T/new/runtime/data/opendaoc.sqlite3.db" 'SELECT count(*) FROM offline_world_bots')" == 2 ]] || fail "bots not imported"
[[ "$(q "$T/new/runtime/data/opendaoc.sqlite3.db" "SELECT count(*) FROM Account WHERE Name='tester'")" == 1 ]] || fail "account not imported"
ok "progress-import moves bots and accounts into a clean world"

echo "All $pass CLI checks passed."
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `ODC_TEST_WORLD=~/Games/OfflineDAoC/editions/0.34-no-custom-class/runtime/data/opendaoc.sqlite3.db bash tools/linux/tests/test_cli_tools.sh /tmp/odc-tools`
Expected: fails at the first `dotnet` call with `The application to execute does not exist` (nothing built yet).

- [ ] **Step 3: Create the three projects**

`tools/linux/offline-bots/offline-bots.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- Linux CLI for the Windows launcher's "+ Lv.1 / + Lv.50" bot buttons. Program.cs holds the
       launcher's bot-creation functions copied verbatim from MainForm.cs; the name generator is linked. -->
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>offline-bots</AssemblyName>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="System.Data.SQLite.Core" Version="1.0.119" /></ItemGroup>
  <ItemGroup>
    <Compile Include="../../../source/tools/OfflineDaoc.Launcher/BotCharacterGenerator.cs" Link="BotCharacterGenerator.cs" />
  </ItemGroup>
</Project>
```

`tools/linux/offline-bots/Program.cs`: the owner's PC already has a working, tested port (a 15-line
`Main` taking `add <db> <realm> <count>[x<level>]`, then `SluaghbinderEnabled`, `GenerateBotCharacters`,
the starting-location helpers and the `BotStartingLocation` record copied verbatim from `MainForm.cs`,
with `GenerateBotCharacters` changed from `private` to `internal`). Its `BotCharacterGenerator.cs` is
byte-identical to upstream's, which is why the project links upstream's file instead. Copy it:

```bash
cp ~/Games/OfflineDAoC/tools/offline-bots/Program.cs tools/linux/offline-bots/Program.cs
```

Then replace its first two comment lines with:
```csharp
// Linux CLI port of the OfflineDAoC launcher's "+ Lv.1 / + Lv.50" bot buttons.
// Code below the marker is copied verbatim from source/tools/OfflineDaoc.Launcher/MainForm.cs (GPL-3.0);
// when upstream changes those functions, re-copy them here.
```

`tools/linux/bot-goals/bot-goals.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- Linux CLI for the launcher's "Bot Goals Setting" tab. Links the same BotGoalSettings.cs the
       server and the Windows launcher compile, so validation, defaults and file format are identical. -->
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>bot-goals</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="../../../source/server/GameServer/bots/autonomous/BotGoalSettings.cs" Link="BotGoalSettings.cs" />
  </ItemGroup>
</Project>
```

`tools/linux/bot-goals/Program.cs`:
```csharp
using System.Net.NetworkInformation;
using OfflineDaoc.Configuration;

// bot-goals [--port N] <dir> show | reset | set <1-19|20-49|50> <solo> <group> <rvr> | import <file.json>
// The server reads <dir>/bot-goals.json once at startup. Like the launcher, saving requires the
// server to be stopped, detected as no TCP listener on --port (default 10301).
internal static class Program
{
    static int _port = 10301;

    static bool ServerStopped() =>
        !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p => p.Port == _port);

    static void Print(BotGoalSettings s, string path)
    {
        Console.WriteLine(File.Exists(path) ? $"Bot goals from {path}:" : "No bot-goals.json yet; the server uses the defaults:");
        Console.WriteLine("  Levels    Solo PvE  Group PvE  RvR");
        foreach (var (name, row) in new[] { ("1-19 ", s.Levels1To19), ("20-49", s.Levels20To49), ("50   ", s.Level50) })
            Console.WriteLine($"  {name}     {row.SoloPve,4}%     {row.GroupPve,4}%  {row.RvR,3}%");
    }

    static int Main(string[] argv)
    {
        var args = argv.ToList();
        int portAt = args.IndexOf("--port");
        if (portAt >= 0)
        {
            if (portAt + 1 >= args.Count || !int.TryParse(args[portAt + 1], out _port)) { Console.Error.WriteLine("--port needs a number"); return 2; }
            args.RemoveRange(portAt, 2);
        }
        if (args.Count < 2) { Console.Error.WriteLine("usage: bot-goals [--port N] <dir> show | reset | set <1-19|20-49|50> <solo> <group> <rvr> | import <file.json>"); return 2; }
        string path = Path.Combine(args[0], BotGoalSettings.FileName);
        try
        {
            BotGoalSettings current = BotGoalSettings.Load(path);
            switch (args[1])
            {
                case "show":
                    Print(current, path);
                    return 0;
                case "reset":
                    BotGoalSettings.Defaults.Save(path, ServerStopped);
                    break;
                case "set" when args.Count == 6:
                    var row = new BotGoalWeights(int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]));
                    current = args[2] switch
                    {
                        "1-19" => current with { Levels1To19 = row },
                        "20-49" => current with { Levels20To49 = row },
                        "50" => current with { Level50 = row },
                        _ => throw new ArgumentException("Level band must be 1-19, 20-49 or 50."),
                    };
                    current.Save(path, ServerStopped);
                    break;
                case "import" when args.Count == 3:
                    BotGoalSettings.Load(Path.GetFullPath(args[2])).Save(path, ServerStopped); // Load validates
                    break;
                default:
                    Console.Error.WriteLine("Unknown command.");
                    return 2;
            }
            Print(BotGoalSettings.Load(path), path);
            Console.WriteLine("Saved. The server applies it on its next start.");
            return 0;
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException or FormatException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine("Not saved: " + e.Message);
            return 1;
        }
    }
}
```

`tools/linux/progress-import/progress-import.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- Linux console build of the OfflineDAoC progress importer: upstream's ImportEngine.cs and
       progress-policy.json (linked, unchanged) plus a console entry point mirroring its import command. -->
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <AssemblyName>progress-import</AssemblyName><RootNamespace>OfflineDaoc.ProgressImport</RootNamespace>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="System.Data.SQLite.Core" Version="1.0.119" /></ItemGroup>
  <ItemGroup>
    <Compile Include="../../../source/tools/OfflineDaoc.ProgressImport/ImportEngine.cs" Link="ImportEngine.cs" />
    <None Include="../../../source/tools/OfflineDaoc.ProgressImport/progress-policy.json" Link="progress-policy.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

`tools/linux/progress-import/Program.cs`:
```csharp
namespace OfflineDaoc.ProgressImport;

// Same behaviour as the --import branch of upstream's Program.cs, without the Windows Forms window.
internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length >= 4 && args[0] == "--import" && args[3] == "--replace-progress")
        {
            bool leaveBots = args.Contains("--leave-sluaghbinder-bots");
            string report = args.Skip(4).FirstOrDefault(a => !a.StartsWith("--")) ?? Path.Combine(args[2], "import-test-result.txt");
            try { var lines = new List<string>(); string backup = ImportEngine.Import(args[1], args[2], lines.Add, leaveBots); lines.Add("SUCCESS " + backup); File.WriteAllLines(report, lines); return 0; }
            catch (Exception e) { File.WriteAllText(report, e.ToString()); return 1; }
        }
        Console.Error.WriteLine("usage: progress-import --import <old folder> <new folder> --replace-progress [report.txt] [--leave-sluaghbinder-bots]");
        return 2;
    }
}
```

`tools/linux/.gitignore`:
```
bin/
obj/
```

`tools/linux/build.sh`:
```bash
#!/usr/bin/env bash
# Build the Linux admin CLIs into OUT/<tool>/. Usage: tools/linux/build.sh OUT
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${1:?usage: $0 <output dir>}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
for tool in offline-bots bot-goals progress-import; do
    dotnet build "$here/$tool/$tool.csproj" -c Release -o "$out/$tool" --nologo -v quiet
done
echo "Built: $(ls "$out" | tr '\n' ' ')"
```

- [ ] **Step 4: Build and run the test**

Run:
```bash
chmod +x tools/linux/build.sh tools/linux/tests/test_cli_tools.sh
tools/linux/build.sh /tmp/odc-tools
ODC_TEST_WORLD=~/Games/OfflineDAoC/editions/0.34-no-custom-class/runtime/data/opendaoc.sqlite3.db bash tools/linux/tests/test_cli_tools.sh /tmp/odc-tools
```
Expected: `Built: bot-goals offline-bots progress-import`, six `ok - ...` lines, `All 6 CLI checks passed.`

- [ ] **Step 5: Commit**

```bash
git add tools/linux/.gitignore tools/linux/build.sh tools/linux/offline-bots tools/linux/bot-goals tools/linux/progress-import tools/linux/tests/test_cli_tools.sh
git commit -m "feat(tools): Linux CLIs for bot creation, bot goals and progress import

All three wrap upstream code: launcher functions copied verbatim, the shared
BotGoalSettings.cs and ImportEngine.cs linked unchanged.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Accounts tool

**Files:**
- Create: `tools/linux/accounts/accounts.py`
- Test: `tools/linux/tests/test_accounts.py`

**Interfaces:**
- Consumes: the world database's `Account` and `DOLCharacters` tables.
- Produces (used by Tasks 8, 9): `accounts.hash_password(pw) -> str`, `connect(db)`, `create(conn, name, password, plvl=1)`, `list_accounts(conn) -> list[tuple(name, plvl, last_login, characters)]`, `possibly_online(conn, name) -> bool`, `set_plvl(conn, name, plvl, server_stopped=False)`, `class AccountError`; CLI `accounts.py --db DB create NAME PASSWORD [--plvl N] | list | plvl NAME N [--server-stopped]` (exit 0, or 1 with `ERROR: ...`).

- [ ] **Step 1: Write the failing tests `tools/linux/tests/test_accounts.py`**

```python
import os
import sqlite3
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
TOOL = os.path.join(os.path.dirname(HERE), "accounts", "accounts.py")
sys.path.insert(0, os.path.dirname(TOOL))

import accounts  # noqa: E402

SCHEMA = """
CREATE TABLE `Account` (`Name` VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, `Password` TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
`CreationDate` DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', `LastLogin` DATETIME DEFAULT NULL, `Realm` INT(11) NOT NULL DEFAULT 0,
`PrivLevel` UNSIGNED INT(10) NOT NULL DEFAULT 0, `Status` INT(11) NOT NULL DEFAULT 0, `Mail` TEXT DEFAULT NULL COLLATE NOCASE,
`LastLoginIP` VARCHAR(255) DEFAULT NULL COLLATE NOCASE, `LastClientVersion` TEXT DEFAULT NULL COLLATE NOCASE,
`Language` TEXT DEFAULT NULL COLLATE NOCASE, `IsMuted` TINYINT(1) NOT NULL DEFAULT 0, `IsWarned` TINYINT(1) NOT NULL DEFAULT 0,
`Notes` TEXT DEFAULT NULL COLLATE NOCASE, `IsTester` TINYINT(1) NOT NULL DEFAULT 0, `CharactersTraded` INT(11) NOT NULL DEFAULT 0,
`SoloCharactersTraded` INT(11) NOT NULL DEFAULT 0, `DiscordID` TEXT DEFAULT NULL COLLATE NOCASE, `Realm_Timer_Realm` INT(11) NOT NULL DEFAULT 0,
`Realm_Timer_Last_Combat` DATETIME DEFAULT NULL, `LastDisconnected` DATETIME DEFAULT NULL,
`LastTimeRowUpdated` DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', `Account_ID` VARCHAR(255) DEFAULT NULL COLLATE NOCASE,
PRIMARY KEY (`Name`));
CREATE UNIQUE INDEX `U_Account_Account_ID` ON `Account` (`Account_ID`);
CREATE TABLE `DOLCharacters` (`AccountName` VARCHAR(255) NOT NULL DEFAULT '', `Name` VARCHAR(255) NOT NULL DEFAULT '');
"""


class AccountsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "world.db")
        with sqlite3.connect(self.db) as c:
            c.executescript(SCHEMA)
        self.c = accounts.connect(self.db)

    def tearDown(self):
        self.c.close()
        self.tmp.cleanup()

    def test_hash_matches_the_servers_algorithm(self):
        # Server: "##" + MD5 over UTF-16 big-endian chars, each byte as hex WITHOUT zero padding.
        self.assertEqual(accounts.hash_password("abc"), "##FC48464A2559EE604BE7382C39C6687")  # 31 chars: no zero padding
        self.assertEqual(accounts.hash_password("a")[:2], "##")

    def test_create_and_list(self):
        accounts.create(self.c, "Alice1", "s3cret")
        self.c.execute("INSERT INTO DOLCharacters (AccountName, Name) VALUES ('Alice1', 'Lometur')")
        rows = accounts.list_accounts(self.c)
        self.assertEqual(rows[0][:2], ("Alice1", 1))
        self.assertEqual(rows[0][3], 1)
        row = self.c.execute("SELECT Password, Language, Realm, Account_ID FROM Account").fetchone()
        self.assertEqual(row[0], accounts.hash_password("s3cret"))
        self.assertEqual((row[1], row[2]), ("EN", 0))
        self.assertEqual(len(row[3]), 36)

    def test_create_validation(self):
        for name, pw, plvl, msg in [("bad name", "x", 1, "letters and digits"), ("", "x", 1, "letters and digits"),
                                    ("ok", "", 1, "no spaces"), ("ok", "has space", 1, "no spaces"),
                                    ("ok", "x", 5, "plvl")]:
            with self.subTest(name=name), self.assertRaisesRegex(accounts.AccountError, msg):
                accounts.create(self.c, name, pw, plvl)
        accounts.create(self.c, "Bob", "pw")
        with self.assertRaisesRegex(accounts.AccountError, "already exists"):
            accounts.create(self.c, "bob", "pw2")

    def test_plvl_refuses_while_possibly_online(self):
        accounts.create(self.c, "Carol", "pw")
        self.c.execute("UPDATE Account SET LastLogin='2026-10-05 10:00:00.0000000', LastDisconnected=NULL WHERE Name='Carol'")
        self.c.commit()
        with self.assertRaisesRegex(accounts.AccountError, "log out"):
            accounts.set_plvl(self.c, "Carol", 3)
        accounts.set_plvl(self.c, "Carol", 3, server_stopped=True)
        self.assertEqual(self.c.execute("SELECT PrivLevel FROM Account WHERE Name='Carol'").fetchone()[0], 3)
        self.c.execute("UPDATE Account SET LastDisconnected='2026-10-05 11:00:00.0000000' WHERE Name='Carol'")
        self.c.commit()
        accounts.set_plvl(self.c, "Carol", 1)
        self.assertEqual(self.c.execute("SELECT PrivLevel FROM Account WHERE Name='Carol'").fetchone()[0], 1)

    def test_plvl_unknown_account(self):
        with self.assertRaisesRegex(accounts.AccountError, "no account"):
            accounts.set_plvl(self.c, "Nobody", 2, server_stopped=True)

    def test_cli(self):
        run = lambda *a: subprocess.run([sys.executable, TOOL, "--db", self.db, *a], capture_output=True, text=True)  # noqa: E731
        self.assertEqual(run("create", "Dave", "pw", "--plvl", "2").returncode, 0)
        out = run("list")
        self.assertIn("Dave", out.stdout)
        bad = run("create", "Dave", "pw")
        self.assertEqual(bad.returncode, 1)
        self.assertIn("ERROR: account 'Dave' already exists", bad.stderr)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `python3 -m unittest discover -s tools/linux/tests -t tools/linux -p 'test_accounts.py' -v`
Expected: `ModuleNotFoundError: No module named 'accounts'`.

- [ ] **Step 3: Implement `tools/linux/accounts/accounts.py`**

```python
#!/usr/bin/env python3
"""Manage OfflineDAoC accounts in the world database: create, list, set privilege level.

Uses the server's own password hashing ("##" + MD5 of the UTF-16 big-endian characters, each byte
as hex without zero padding; LoginRequestHandler.CryptPassword), so accounts made here work exactly
like accounts created at first login. Safe while the server runs, except changing the privilege
level of a logged-in player: logging out saves the in-memory account over the change.
"""
import argparse
import datetime
import hashlib
import re
import sqlite3
import sys
import uuid

NAME_RE = re.compile(r"^[A-Za-z0-9]+$")


class AccountError(Exception):
    pass


def hash_password(password):
    data = b"".join(bytes(((ord(ch) >> 8) & 0xFF, ord(ch) & 0xFF)) for ch in password)
    return "##" + "".join(format(b, "X") for b in hashlib.md5(data).digest())


def connect(db):
    conn = sqlite3.connect(db, timeout=10)
    conn.execute("PRAGMA busy_timeout=10000")
    return conn


def _now_local():
    return datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S.%f")


def _now_utc():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S.%fZ")


def create(conn, name, password, plvl=1):
    if not NAME_RE.match(name or ""):
        raise AccountError("account names may only contain letters and digits")
    if not password or any(ch.isspace() for ch in password):
        raise AccountError("the password must be non-empty and contain no spaces")
    if plvl not in (1, 2, 3):
        raise AccountError("plvl must be 1 (player), 2 (GM) or 3 (admin)")
    if conn.execute("SELECT 1 FROM Account WHERE Name = ? COLLATE NOCASE", (name,)).fetchone():
        raise AccountError(f"account {name!r} already exists")
    with conn:
        conn.execute(
            "INSERT INTO Account (Name, Password, CreationDate, Realm, PrivLevel, Language, LastTimeRowUpdated, Account_ID) "
            "VALUES (?, ?, ?, 0, ?, 'EN', ?, ?)",
            (name, hash_password(password), _now_local(), plvl, _now_utc(), str(uuid.uuid4())))


def list_accounts(conn):
    return conn.execute(
        "SELECT a.Name, a.PrivLevel, a.LastLogin, "
        "(SELECT count(*) FROM DOLCharacters d WHERE d.AccountName = a.Name COLLATE NOCASE) "
        "FROM Account a ORDER BY a.Name COLLATE NOCASE").fetchall()


def possibly_online(conn, name):
    row = conn.execute("SELECT LastLogin, LastDisconnected FROM Account WHERE Name = ? COLLATE NOCASE", (name,)).fetchone()
    if row is None:
        raise AccountError(f"no account named {name!r}")
    last_login, last_disconnected = row
    return last_login is not None and (last_disconnected is None or str(last_disconnected) < str(last_login))


def set_plvl(conn, name, plvl, server_stopped=False):
    if plvl not in (1, 2, 3):
        raise AccountError("plvl must be 1 (player), 2 (GM) or 3 (admin)")
    if not server_stopped and possibly_online(conn, name):
        raise AccountError(f"{name} may be logged in, and logging out would overwrite the change. "
                           "Ask them to log out first, or stop the server.")
    with conn:
        changed = conn.execute("UPDATE Account SET PrivLevel = ?, LastTimeRowUpdated = ? WHERE Name = ? COLLATE NOCASE",
                               (plvl, _now_utc(), name)).rowcount
    if changed != 1:
        raise AccountError(f"no account named {name!r}")


def main(argv=None):
    ap = argparse.ArgumentParser(description="Manage OfflineDAoC accounts.")
    ap.add_argument("--db", required=True)
    sub = ap.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("create")
    c.add_argument("name")
    c.add_argument("password")
    c.add_argument("--plvl", type=int, default=1)
    sub.add_parser("list")
    p = sub.add_parser("plvl")
    p.add_argument("name")
    p.add_argument("level", type=int)
    p.add_argument("--server-stopped", action="store_true")
    a = ap.parse_args(argv)
    conn = connect(a.db)
    try:
        if a.cmd == "create":
            create(conn, a.name, a.password, a.plvl)
            print(f"Created account {a.name} (plvl {a.plvl}).")
        elif a.cmd == "list":
            print(f"{'Account':20} {'plvl':>4}  {'Last login':26} Characters")
            for name, plvl, last, chars in list_accounts(conn):
                print(f"{name:20} {plvl:>4}  {str(last or 'never'):26} {chars}")
        else:
            set_plvl(conn, a.name, a.level, a.server_stopped)
            print(f"{a.name} is now plvl {a.level}; it applies at their next login.")
    except AccountError as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 1
    finally:
        conn.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `python3 -m unittest discover -s tools/linux/tests -t tools/linux -p 'test_accounts.py' -v`
Expected: `Ran 6 tests ... OK`. Then cross-check the hash against the owner's real server-created
account (read-only):
```bash
python3 - <<'EOF'
import sqlite3, sys
sys.path.insert(0, "tools/linux/accounts"); import accounts
od = __import__("os").path.expanduser("~/Games/OfflineDAoC")
pw = [l.split(":", 1)[1].strip() for l in open(od + "/runtime/account.txt", newline="") if l.lower().startswith("password:")][0]
stored = sqlite3.connect(f"file:{od}/runtime/data/opendaoc.sqlite3.db?mode=ro", uri=True).execute("SELECT Password FROM Account WHERE Name='offline'").fetchone()[0]
print("server hash reproduced:", accounts.hash_password(pw) == stored)
EOF
```
Expected: `server hash reproduced: True`.

- [ ] **Step 5: Commit**

```bash
chmod +x tools/linux/accounts/accounts.py
git add tools/linux/accounts/accounts.py tools/linux/tests/test_accounts.py
git commit -m "feat(tools): accounts CLI using the server's password hashing

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Backups and world administration

**Files:**
- Create: `deploy/bin/backup.py`
- Create: `deploy/bin/world_admin.py`
- Create: `deploy/bin/add-bots.sh`
- Test: `deploy/tests/test_backup.py`, `deploy/tests/test_world_admin.py`

**Interfaces:**
- Consumes: Task 2 `Release`/`FetchError`; Task 3 `init_world.init`, `world_paths`, `write_meta`; Task 5 `progress-import.dll`; Task 6 `accounts`.
- Produces (used by Tasks 8, 9):
  - `backup.create(data, keep=None, label="backup") -> str` (path), `backup.rotate(data, keep)`, `backup.seconds_until_due(data, interval, now=None) -> float`, `backup.loop(data, keep, interval=86400)`; CLI `backup.py --data D create [--keep N] [--label L] | loop --keep N | list`
  - `world_admin.status(data) -> dict`, `restore(data, name) -> str`, `new_world(release, data, edition, skip_navmesh=False, log=print) -> str` (archive path), `fetch_clean(release, data, log=print) -> str` (path of `/data/upgrade-clean-world.db`), `upgrade_world(release, data, importer_cmd, clean_world=None, same_version_ok=False, log=print) -> str`, `class AdminError`
  - CLI `world_admin.py --data D --lock L status | restore NAME | new-world --edition E [--skip-navmesh] | fetch-clean | upgrade-world --importer "CMD" [--clean-world FILE] [--same-version]`
  - `add-bots.sh <alb|mid|hib> <1|10|100> <1|50>` (inside the container)
  - Backup file names: `/data/backups/world-YYYYmmdd-HHMMSS-ffffff-<label>.db`; rotation only removes `-backup.db` files.

- [ ] **Step 1: Write the failing tests `deploy/tests/test_backup.py`**

```python
import glob
import os
import sqlite3
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "bin"))

import backup  # noqa: E402


def make_world(data, rows=3):
    os.makedirs(os.path.join(data, "world"), exist_ok=True)
    c = sqlite3.connect(os.path.join(data, "world", "opendaoc.sqlite3.db"))
    c.execute("PRAGMA journal_mode=WAL")
    c.execute("CREATE TABLE t (v INTEGER)")
    c.executemany("INSERT INTO t VALUES (?)", [(i,) for i in range(rows)])
    c.commit()
    return c  # keep open so the WAL stays in use, like the running server


class BackupTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.data = self.tmp.name

    def tearDown(self):
        self.tmp.cleanup()

    def test_backup_is_consistent_while_writer_is_open(self):
        live = make_world(self.data, rows=5)
        live.execute("INSERT INTO t VALUES (99)")
        live.commit()  # committed into the WAL, not yet checkpointed
        path = backup.create(self.data)
        live.close()
        with sqlite3.connect(path) as c:
            self.assertEqual(c.execute("SELECT count(*) FROM t").fetchone()[0], 6)
        self.assertTrue(path.endswith("-backup.db"))

    def test_rotation_keeps_newest_and_ignores_other_labels(self):
        make_world(self.data).close()
        pre = backup.create(self.data, label="pre-restore")
        made = [backup.create(self.data, keep=3) for _ in range(5)]
        kept = sorted(glob.glob(os.path.join(self.data, "backups", "*-backup.db")))
        self.assertEqual(kept, sorted(made[-3:]))
        self.assertTrue(os.path.exists(pre))

    def test_failed_backup_keeps_old_backups(self):
        make_world(self.data).close()
        old = [backup.create(self.data, keep=7) for _ in range(2)]
        with open(os.path.join(self.data, "world", "opendaoc.sqlite3.db"), "wb") as f:
            f.write(b"this is not a database")
        for p in ("-wal", "-shm"):
            path = os.path.join(self.data, "world", "opendaoc.sqlite3.db" + p)
            if os.path.exists(path):
                os.remove(path)
        with self.assertRaises(Exception):
            backup.create(self.data, keep=1)
        self.assertTrue(all(os.path.exists(p) for p in old))
        self.assertEqual(glob.glob(os.path.join(self.data, "backups", "*.part")), [])

    def test_daily_backup_is_due_only_when_the_newest_is_old(self):
        # Restarting the container must not add a backup each time (rotation would drop older days).
        make_world(self.data).close()
        self.assertEqual(backup.seconds_until_due(self.data, 86400), 0)
        path = backup.create(self.data)
        self.assertGreater(backup.seconds_until_due(self.data, 86400), 86000)
        old = os.path.getmtime(path) - 90000
        os.utime(path, (old, old))
        self.assertEqual(backup.seconds_until_due(self.data, 86400), 0)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Write the failing tests `deploy/tests/test_world_admin.py`**

```python
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path[:0] = [os.path.join(REPO, "deploy", "bin"), os.path.join(REPO, "tools", "linux"),
                os.path.join(REPO, "tools", "linux", "accounts"), os.path.join(REPO, "tools", "linux", "tests")]

import accounts  # noqa: E402
import backup  # noqa: E402
import init_world  # noqa: E402
import release_fixture as fx  # noqa: E402
import world_admin  # noqa: E402
from odaoc_fetch import Release  # noqa: E402

QUIET = lambda *a, **k: None  # noqa: E731
TEST_WORLD = os.environ.get("ODC_TEST_WORLD")
TOOLS = os.environ.get("ODC_TOOLS")


class FakeRelease:
    """Stands in for Release in upgrade tests: 'downloads' the given clean world file."""

    def __init__(self, version, clean_world):
        self.version = version
        self.lock = {"editions": {"classic": {"world_db": "w"}, "b": {"world_db": "w"}}}
        self._clean = clean_world

    def edition(self, name):
        return self.lock["editions"][name]

    def extract(self, rel, dest):
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        shutil.copyfile(self._clean, dest)
        return "ok"


def importer_cmd(data):
    cmd = ["env", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0", "dotnet",
           os.path.join(TOOLS, "progress-import", "progress-import.dll")]
    busy = subprocess.run(["ss", "-ltn", "sport = :10300"], capture_output=True, text=True).stdout.count("10300")
    if busy:  # the upstream engine refuses while anything listens on 10300 or a CoreServer process runs
        cmd = ["bwrap", "--ro-bind", "/", "/", "--dev", "/dev", "--proc", "/proc",
               "--bind", data, data, "--unshare-net", "--unshare-pid"] + cmd
    return cmd


class WorldAdminTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = self.tmp.name
        self.data = os.path.join(self.dir, "data")

    def tearDown(self):
        self.tmp.cleanup()

    def make_sqlite_world(self, edition="classic", version="test"):
        db = init_world.world_paths(self.data)["db"]
        os.makedirs(os.path.dirname(db), exist_ok=True)
        with sqlite3.connect(db) as c:
            c.execute("CREATE TABLE t (v TEXT)")
            c.execute("INSERT INTO t VALUES ('original')")
        init_world.write_meta(init_world.world_paths(self.data)["meta"],
                              {"version": version, "edition": edition, "navmesh": False, "created_utc": "x"})
        return db

    def test_restore(self):
        db = self.make_sqlite_world()
        saved = backup.create(self.data)
        with sqlite3.connect(db) as c:
            c.execute("UPDATE t SET v='changed'")
        world_admin.restore(self.data, os.path.basename(saved))
        with sqlite3.connect(db) as c:
            self.assertEqual(c.execute("SELECT v FROM t").fetchone()[0], "original")
        self.assertEqual(len([f for f in os.listdir(os.path.join(self.data, "backups")) if f.endswith("-pre-restore.db")]), 1)

    def test_restore_rejects_a_corrupt_backup(self):
        self.make_sqlite_world()
        os.makedirs(os.path.join(self.data, "backups"), exist_ok=True)
        bad = os.path.join(self.data, "backups", "bad.db")
        with open(bad, "wb") as f:
            f.write(b"junk")
        with self.assertRaisesRegex(world_admin.AdminError, "integrity"):
            world_admin.restore(self.data, "bad.db")

    def test_new_world_archives_and_switches_edition(self):
        lock, files = fx.build(self.dir)
        with fx.RangeServer(self.dir) as srv:
            rel = Release(srv.lock(lock), retries=1, backoff=0)
            init_world.init(rel, self.data, "classic", skip_navmesh=True, log=QUIET)
            archive = world_admin.new_world(rel, self.data, "b", skip_navmesh=True, log=QUIET)
        with open(init_world.world_paths(self.data)["db"], "rb") as f:
            self.assertEqual(f.read(), files["runtime/data/opendaoc.sqlite3.db"])
        with open(init_world.world_paths(self.data)["meta"], encoding="utf-8") as f:
            self.assertEqual(json.load(f)["edition"], "b")
        self.assertTrue(os.path.isfile(os.path.join(archive, "world", "opendaoc.sqlite3.db")))

    def test_status(self):
        db = self.make_sqlite_world()
        st = world_admin.status(self.data)
        self.assertEqual((st["edition"], st["version"]), ("classic", "test"))
        self.assertIn("latest_backup", st)

    @unittest.skipUnless(TEST_WORLD and TOOLS, "needs ODC_TEST_WORLD (clean classic world) and ODC_TOOLS (built CLIs)")
    def test_upgrade_world_keeps_accounts_passwords_and_privilege(self):
        db = init_world.world_paths(self.data)["db"]
        os.makedirs(os.path.dirname(db))
        shutil.copyfile(TEST_WORLD, db)
        init_world.write_meta(init_world.world_paths(self.data)["meta"],
                              {"version": "0.34b", "edition": "classic", "navmesh": False, "created_utc": "x"})
        conn = accounts.connect(db)
        accounts.create(conn, "Admin1", "adminpw", plvl=3)
        accounts.create(conn, "Player2", "playerpw")
        before = dict(conn.execute("SELECT Name, Password FROM Account").fetchall())
        conn.close()
        archive = world_admin.upgrade_world(FakeRelease("0.34b", TEST_WORLD), self.data,
                                            importer_cmd(self.data), same_version_ok=True, log=QUIET)
        with sqlite3.connect(db) as c:
            after = dict(c.execute("SELECT Name, Password FROM Account").fetchall())
            plvl = dict(c.execute("SELECT Name, PrivLevel FROM Account").fetchall())
            self.assertEqual(c.execute("PRAGMA integrity_check").fetchone()[0], "ok")
        self.assertEqual(after, before)  # every account kept, passwords unchanged
        self.assertEqual(plvl, {"Admin1": 3, "Player2": 1})  # admin rights restored after the importer reset them
        self.assertTrue(os.path.isfile(os.path.join(archive, "world", "opendaoc.sqlite3.db")))

    def test_upgrade_world_refuses_same_version_by_default(self):
        self.make_sqlite_world(version="0.34b")
        with self.assertRaisesRegex(world_admin.AdminError, "already"):
            world_admin.upgrade_world(FakeRelease("0.34b", "/nonexistent"), self.data, ["true"], log=QUIET)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `python3 -m unittest discover -s deploy/tests -t deploy -p 'test_backup.py' -v; python3 -m unittest discover -s deploy/tests -t deploy -p 'test_world_admin.py' -v`
Expected: `ModuleNotFoundError: No module named 'backup'` and `... 'world_admin'`.

- [ ] **Step 4: Implement `deploy/bin/backup.py`**

```python
#!/usr/bin/env python3
"""Consistent snapshots of the world database (safe while the server runs), rotation, daily loop.
A failed backup never deletes older backups and leaves no partial file."""
import argparse
import datetime
import glob
import os
import sqlite3
import sys
import time


def db_path(data):
    return os.path.join(data, "world", "opendaoc.sqlite3.db")


def backups_dir(data):
    return os.path.join(data, "backups")


def create(data, keep=None, label="backup"):
    os.makedirs(backups_dir(data), exist_ok=True)
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S-%f")
    dest = os.path.join(backups_dir(data), f"world-{stamp}-{label}.db")
    tmp = dest + ".part"
    try:
        src = sqlite3.connect(f"file:{db_path(data)}?mode=ro", uri=True, timeout=30)
        try:
            dst = sqlite3.connect(tmp)
            try:
                src.backup(dst)
            finally:
                dst.close()
        finally:
            src.close()
        chk = sqlite3.connect(f"file:{tmp}?mode=ro", uri=True)
        try:
            if chk.execute("PRAGMA quick_check").fetchone()[0] != "ok":
                raise RuntimeError("the new backup failed its integrity check")
        finally:
            chk.close()
        os.replace(tmp, dest)
    finally:
        if os.path.exists(tmp):
            os.remove(tmp)
    if keep:
        rotate(data, keep)
    return dest


def rotate(data, keep):
    files = sorted(glob.glob(os.path.join(backups_dir(data), "world-*-backup.db")))
    for old in files[:-keep] if keep > 0 else []:
        os.remove(old)


def seconds_until_due(data, interval, now=None):
    """0 when the newest automatic backup is older than interval (or there is none)."""
    files = sorted(glob.glob(os.path.join(backups_dir(data), "world-*-backup.db")))
    if not files:
        return 0
    age = (time.time() if now is None else now) - os.path.getmtime(files[-1])
    return max(0, interval - age)


def loop(data, keep, interval=86400):
    while True:
        wait = seconds_until_due(data, interval)
        if wait > 0:
            time.sleep(wait)
            continue
        try:
            print(f"Backup written: {create(data, keep=keep)}", flush=True)
        except Exception as e:  # keep the server running; report and retry in an hour
            print(f"ERROR: daily backup failed: {e}. Older backups were kept.", file=sys.stderr, flush=True)
            time.sleep(3600)


def main(argv=None):
    ap = argparse.ArgumentParser(description="World database backups.")
    ap.add_argument("--data", required=True)
    sub = ap.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("create")
    c.add_argument("--keep", type=int)
    c.add_argument("--label", default="backup")
    lp = sub.add_parser("loop")
    lp.add_argument("--keep", type=int, default=7)
    sub.add_parser("list")
    a = ap.parse_args(argv)
    if a.cmd == "create":
        try:
            print(create(a.data, a.keep, a.label))
        except Exception as e:
            print(f"ERROR: backup failed: {e}. Older backups were kept.", file=sys.stderr)
            return 1
    elif a.cmd == "loop":
        loop(a.data, a.keep)
    else:
        for path in sorted(glob.glob(os.path.join(backups_dir(a.data), "world-*.db"))):
            print(f"{os.path.getsize(path) // 1048576:>6} MB  {os.path.basename(path)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 5: Implement `deploy/bin/world_admin.py`**

```python
#!/usr/bin/env python3
"""World lifecycle for the central server: status, restore a backup, start a new world, and upgrade
to a new upstream version with upstream's progress importer. Everything except status and fetch-clean
expects the server to be stopped (deploy/odc enforces that) and keeps the previous world first: restore
takes a backup, new-world and upgrade-world move the whole old world into /data/archive."""
import argparse
import datetime
import json
import os
import shlex
import shutil
import sqlite3
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path[:0] = [HERE, os.path.join(HERE, "..", "..", "tools", "linux")]

import backup  # noqa: E402
import init_world  # noqa: E402
from odaoc_fetch import FetchError, Release  # noqa: E402

CLEAN_WORLD = "upgrade-clean-world.db"


class AdminError(Exception):
    pass


def _ts():
    return datetime.datetime.now().strftime("%Y%m%d-%H%M%S")


def _meta(data):
    path = init_world.world_paths(data)["meta"]
    if not os.path.isfile(path):
        raise AdminError("there is no world yet; start the server once (odc up) to create it")
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def _integrity_ok(path):
    try:
        c = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
        try:
            return c.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
        finally:
            c.close()
    except sqlite3.DatabaseError:
        return False


def _remove_sidecars(db):
    for suffix in ("-wal", "-shm"):
        try:
            os.remove(db + suffix)
        except FileNotFoundError:
            pass


def status(data):
    meta = _meta(data)
    db = init_world.world_paths(data)["db"]
    c = sqlite3.connect(f"file:{db}?mode=ro", uri=True, timeout=10)
    try:
        def count(sql):
            try:
                return c.execute(sql).fetchone()[0]
            except sqlite3.OperationalError:
                return None
        st = {
            "version": meta["version"], "edition": meta["edition"], "navmesh": meta.get("navmesh", False),
            "accounts": count("SELECT count(*) FROM Account"),
            "characters": count("SELECT count(*) FROM DOLCharacters"),
            "players_possibly_online": count("SELECT count(*) FROM Account WHERE LastLogin IS NOT NULL AND "
                                             "(LastDisconnected IS NULL OR LastDisconnected < LastLogin)"),
            "bots_online": count("SELECT count(*) FROM offline_world_bots WHERE IsOnline=1"),
            "bots_total": count("SELECT count(*) FROM offline_world_bots WHERE IsRetired=0"),
        }
    finally:
        c.close()
    backups = sorted(f for f in os.listdir(backup.backups_dir(data))) if os.path.isdir(backup.backups_dir(data)) else []
    st["latest_backup"] = backups[-1] if backups else None
    return st


def restore(data, name):
    src = name if os.path.isabs(name) else os.path.join(backup.backups_dir(data), name)
    if not os.path.isfile(src):
        raise AdminError(f"backup not found: {src}")
    if not _integrity_ok(src):
        raise AdminError("that backup failed its integrity check; nothing was changed")
    pre = backup.create(data, label="pre-restore")
    db = init_world.world_paths(data)["db"]
    _remove_sidecars(db)
    shutil.copyfile(src, db + ".restore")
    os.replace(db + ".restore", db)
    return pre


def _archive_world(data, label):
    p = init_world.world_paths(data)
    dest = os.path.join(data, "archive", f"{label}-{_ts()}")
    os.makedirs(dest)
    shutil.move(os.path.dirname(p["db"]), os.path.join(dest, "world"))
    shutil.move(p["meta"], os.path.join(dest, "world.json"))
    return dest


def new_world(release, data, edition, skip_navmesh=False, log=print):
    _meta(data)
    archive = _archive_world(data, "world")  # moved intact, so this also works when the old world is damaged
    rc = init_world.init(release, data, edition, skip_navmesh=skip_navmesh, log=log)
    if rc != 0:
        raise AdminError(f"creating the new world failed (code {rc}); the old world is in {archive}")
    log(f"New '{edition}' world created; the old one is archived in {archive}.")
    return archive


def fetch_clean(release, data, log=print):
    """Download (verified, resumable) this release's clean world for the world's edition. Kept separate
    from upgrade_world so the download can run with the network and the import without it."""
    meta = _meta(data)
    dest = os.path.join(data, CLEAN_WORLD)
    release.extract(release.edition(meta["edition"])["world_db"], dest)
    log(f"Clean {meta['edition']} world for upstream {release.version} ready.")
    return dest


def upgrade_world(release, data, importer_cmd, clean_world=None, same_version_ok=False, log=print):
    meta = _meta(data)
    if meta["version"] == release.version and not same_version_ok:
        raise AdminError(f"the world is already on upstream {release.version}; nothing to upgrade")
    db = init_world.world_paths(data)["db"]
    backup.create(data, label="pre-upgrade")
    stage = os.path.join(data, "upgrade", _ts())
    old_rt, new_rt = os.path.join(stage, "old", "runtime"), os.path.join(stage, "new", "runtime")
    os.makedirs(os.path.join(old_rt, "data"))
    os.makedirs(os.path.join(new_rt, "data"))
    old_db = os.path.join(old_rt, "data", "opendaoc.sqlite3.db")
    new_db = os.path.join(new_rt, "data", "opendaoc.sqlite3.db")
    src = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    dst = sqlite3.connect(old_db)
    try:
        src.backup(dst)
        names = [r[0] for r in src.execute("SELECT Name FROM Account ORDER BY Name")]
        plvls = dict(src.execute("SELECT Name, PrivLevel FROM Account").fetchall())
        counts_old = {t: src.execute(f"SELECT count(*) FROM {t}").fetchone()[0]
                      for t in ("Account", "DOLCharacters", "offline_world_bots")}
    finally:
        dst.close()
        src.close()
    if names:
        # The importer needs runtime/account.txt to name an existing account when there are several.
        # Naming one keeps every account's password (ImportEngine.cs only rewrites a password when
        # account.txt is missing or invalid); the password value here is never used.
        with open(os.path.join(old_rt, "account.txt"), "w", encoding="utf-8", newline="") as f:
            f.write(f"Account: {names[0]}\r\nPassword: unused\r\n")
    if clean_world:
        shutil.copyfile(clean_world, new_db)
    else:
        log(f"Fetching the clean {meta['edition']} world for upstream {release.version}...")
        release.extract(release.edition(meta["edition"])["world_db"], new_db)
    report = os.path.join(stage, "import-report.txt")
    cmd = list(importer_cmd) + ["--import", os.path.join(stage, "old"), os.path.join(stage, "new"), "--replace-progress", report]
    log("Importing progress with upstream's import engine...")
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        detail = open(report, encoding="utf-8", errors="replace").read()[:2000] if os.path.exists(report) else r.stderr
        raise AdminError(f"the import failed; the current world is unchanged. Details:\n{detail}")
    c = sqlite3.connect(new_db)
    try:
        with c:  # the importer resets every account to plvl 1; restore GM/admin rights
            c.executemany("UPDATE Account SET PrivLevel=? WHERE Name=?", [(v, k) for k, v in plvls.items()])
        counts_new = {t: c.execute(f"SELECT count(*) FROM {t}").fetchone()[0] for t in counts_old}
        ok = c.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
    finally:
        c.close()
    if not ok or counts_new != counts_old:
        raise AdminError(f"verification failed (before {counts_old}, after {counts_new}); the current world is unchanged")
    archive = _archive_world(data, "world-pre-upgrade")
    os.makedirs(os.path.dirname(db))
    shutil.move(new_db, db)
    init_world.write_meta(init_world.world_paths(data)["meta"],
                          dict(meta, version=release.version, navmesh=False,
                               upgraded_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds")))
    shutil.rmtree(stage, ignore_errors=True)
    log(f"Upgraded to upstream {release.version}: {counts_new}. Previous world archived in {archive}. "
        "Navmeshes are re-verified on the next start.")
    return archive


def main(argv=None):
    ap = argparse.ArgumentParser(description="World administration (server must be stopped except for status).")
    ap.add_argument("--data", required=True)
    ap.add_argument("--lock", required=True)
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("status")
    r = sub.add_parser("restore")
    r.add_argument("name", help="file name in /data/backups, or an absolute path")
    n = sub.add_parser("new-world")
    n.add_argument("--edition", required=True, choices=["classic", "b"])
    n.add_argument("--skip-navmesh", action="store_true")
    sub.add_parser("fetch-clean", help="download the clean world that upgrade-world imports into")
    u = sub.add_parser("upgrade-world")
    u.add_argument("--importer", required=True, help='e.g. "dotnet /app/tools/progress-import/progress-import.dll"')
    u.add_argument("--clean-world", help="an already-downloaded clean world (from fetch-clean); removed on success")
    u.add_argument("--same-version", action="store_true", help="re-import into a fresh copy of the same version")
    a = ap.parse_args(argv)
    try:
        if a.cmd == "status":
            for k, v in status(a.data).items():
                print(f"{k:24} {v}")
        elif a.cmd == "restore":
            print(f"Restored {a.name}. The previous database was saved as {os.path.basename(restore(a.data, a.name))}.")
        elif a.cmd == "new-world":
            new_world(Release.from_lock_file(a.lock), a.data, a.edition, a.skip_navmesh)
        elif a.cmd == "fetch-clean":
            fetch_clean(Release.from_lock_file(a.lock), a.data)
        else:
            upgrade_world(Release.from_lock_file(a.lock), a.data, shlex.split(a.importer), a.clean_world, a.same_version)
            if a.clean_world and os.path.exists(a.clean_world):
                os.remove(a.clean_world)
    except (AdminError, FetchError) as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 6: Implement `deploy/bin/add-bots.sh`**

```bash
#!/usr/bin/env bash
# Inside the container: back up, then add autonomous bots like the launcher's buttons.
# Usage: add-bots.sh <alb|mid|hib> <1|10|100> <1|50>
set -euo pipefail
case "${1:-}" in
    alb|albion|1) realm=1 ;;
    mid|midgard|2) realm=2 ;;
    hib|hibernia|3) realm=3 ;;
    *) echo "usage: add-bots <alb|mid|hib> <1|10|100> <1|50>" >&2; exit 2 ;;
esac
count="${2:?count: 1, 10 or 100}"
level="${3:?level: 1 or 50}"
python3 /app/bin/backup.py --data /data create --label pre-bots >/dev/null
dotnet /app/tools/offline-bots/offline-bots.dll add /data/world/opendaoc.sqlite3.db "$realm" "${count}x${level}"
```

- [ ] **Step 7: Run all deploy tests**

Run:
```bash
chmod +x deploy/bin/backup.py deploy/bin/world_admin.py deploy/bin/add-bots.sh
ODC_TEST_WORLD=~/Games/OfflineDAoC/editions/0.34-no-custom-class/runtime/data/opendaoc.sqlite3.db ODC_TOOLS=/tmp/odc-tools \
  DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 python3 -m unittest discover -s deploy/tests -t deploy -v
```
Expected: all tests pass (`Ran 22 tests ... OK`), including `test_upgrade_world_keeps_accounts_passwords_and_privilege` (not skipped).

- [ ] **Step 8: Commit**

```bash
git add deploy/bin/backup.py deploy/bin/world_admin.py deploy/bin/add-bots.sh deploy/tests/test_backup.py deploy/tests/test_world_admin.py
git commit -m "feat(deploy): backups, restore, new world and upstream upgrade

Upgrade keeps every account and password by naming an existing account in a
staged account.txt, and restores GM/admin levels the importer resets.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Container image and entrypoint

**Files:**
- Create: `deploy/serverconfig.build.xml`
- Create: `deploy/entrypoint.sh`
- Create: `deploy/bin/healthcheck.sh`
- Create: `deploy/Dockerfile`
- Create: `.dockerignore`
- Test: `deploy/tests/smoke.sh`

**Interfaces:**
- Consumes: Tasks 2–7 files; environment variables listed in Task 4 plus `OFFLINEDAOC_EDITION`, `OFFLINEDAOC_SKIP_NAVMESH`, `OFFLINEDAOC_BACKUP_KEEP`.
- Produces (used by Tasks 9, 10, 13, 14):
  - Image layout: `/app/server` (server + `lib/Detour.so` + `config/{logconfig.xml,invalidnames.txt,MailConfig.xml}`), `/app/tools/{offline-bots,bot-goals,progress-import}`, `/app/tools/accounts/accounts.py`, `/app/bin/*`, `/app/upstream.lock`, volume `/data`, user `1000:1000`.
  - Entrypoint exit codes: 2 (download), 3 (edition), 4 (version), 64 (unwritable `/data`), 65 (port in use); otherwise the server's exit code.
  - Console pipe `/tmp/offlinedaoc-console` (only `exit` is useful).

- [ ] **Step 1: Write the failing smoke test `deploy/tests/smoke.sh`**

```bash
#!/usr/bin/env bash
# Container smoke test (world database only, test ports). Usage: smoke.sh <image>
set -euo pipefail
IMAGE="${1:?usage: $0 <image>}"
NAME="offlinedaoc-smoke-$$"; VOL="offlinedaoc-smoke-$$"; PORT="${SMOKE_PORT:-10391}"; UDP="${SMOKE_UDP:-10491}"
cleanup() { docker rm -f "$NAME" >/dev/null 2>&1 || true; docker volume rm -f "$VOL" "$VOL-ro" >/dev/null 2>&1 || true; }
trap cleanup EXIT
fail() { echo "FAIL: $*" >&2; docker logs "$NAME" 2>&1 | tail -40 >&2 || true; exit 1; }
run() { docker run -d --name "$NAME" --network host -v "$VOL":/data -e OFFLINEDAOC_EDITION="${1:-classic}" \
            -e OFFLINEDAOC_PORT="$PORT" -e OFFLINEDAOC_UDP_PORT="$UDP" -e OFFLINEDAOC_SKIP_NAVMESH=1 "$IMAGE" >/dev/null; }
wait_listen() {
    for _ in $(seq 1 300); do
        docker logs "$NAME" 2>&1 | grep -q "Server is now listening for incoming connections on 0.0.0.0:$PORT" && return 0
        [[ "$(docker inspect -f '{{.State.Running}}' "$NAME")" == true ]] || return 1
        sleep 2
    done
    return 1
}
db=/data/world/opendaoc.sqlite3.db

run; wait_listen || fail "server did not start"
echo "ok - server listens on $PORT"
docker exec "$NAME" grep -q "<Port>$PORT</Port>" /app/server/config/serverconfig.xml || fail "port not in config"
docker exec "$NAME" grep -q "<EnableUPnP>False</EnableUPnP>" /app/server/config/serverconfig.xml || fail "UPnP not off"
docker exec "$NAME" test -f /app/server/config/logconfig.xml || fail "logconfig.xml missing"
echo "ok - generated config and release config files present"
docker exec "$NAME" python3 /app/tools/accounts/accounts.py --db "$db" create smoketest Sm0keTest >/dev/null || fail "account create"
docker stop -t 120 "$NAME" >/dev/null
docker logs "$NAME" 2>&1 | grep -q "| DOL.GS.GameServer | Stopped" || fail "no clean save on docker stop"
echo "ok - docker stop saves and stops cleanly"
docker rm "$NAME" >/dev/null
run; wait_listen || fail "restart failed"
docker exec "$NAME" python3 /app/tools/accounts/accounts.py --db "$db" list | grep -q smoketest || fail "data lost on restart"
echo "ok - restart keeps the data"
docker rm -f "$NAME" >/dev/null

run b; sleep 5
[[ "$(docker inspect -f '{{.State.ExitCode}}' "$NAME")" == 3 ]] || fail "edition change not refused"
docker logs "$NAME" 2>&1 | grep -q "odc new-world" || fail "edition message missing"
echo "ok - changing the edition of an existing world is refused"
docker rm -f "$NAME" >/dev/null

python3 -c "import socket,time; s=socket.socket(); s.bind(('0.0.0.0', $PORT)); s.listen(); time.sleep(30)" & holder=$!
sleep 1; run; sleep 5; kill "$holder" 2>/dev/null || true
[[ "$(docker inspect -f '{{.State.ExitCode}}' "$NAME")" == 65 ]] || fail "busy port not detected"
docker logs "$NAME" 2>&1 | grep -q "already in use" || fail "port message missing"
echo "ok - a busy port is refused with a clear message"
docker rm -f "$NAME" >/dev/null

docker volume create "$VOL-ro" >/dev/null
docker run --rm -v "$VOL-ro":/data --user 0 --entrypoint sh "$IMAGE" -c 'chown 0:0 /data && chmod 755 /data'
docker run -d --name "$NAME" --network host -v "$VOL-ro":/data -e OFFLINEDAOC_PORT="$PORT" "$IMAGE" >/dev/null; sleep 3
[[ "$(docker inspect -f '{{.State.ExitCode}}' "$NAME")" == 64 ]] || fail "unwritable volume not detected"
docker logs "$NAME" 2>&1 | grep -q "chown" || fail "chown hint missing"
echo "ok - an unwritable volume is refused with the chown fix"
echo "SMOKE OK"
```

- [ ] **Step 2: Run it to verify it fails**

Run: `chmod +x deploy/tests/smoke.sh && deploy/tests/smoke.sh offlinedaoc:dev`
Expected: fails at `docker run` with `Unable to find image 'offlinedaoc:dev' locally` / pull access denied.

- [ ] **Step 3: Create `deploy/serverconfig.build.xml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<!-- Build-time placeholder only (the build requires a serverconfig.xml to exist).
     The container generates the real file at start with deploy/bin/gen_config.py. -->
<root>
    <Server>
        <Port>10301</Port>
        <IP>127.0.0.1</IP>
        <RegionIP>127.0.0.1</RegionIP>
        <RegionPort>10401</RegionPort>
        <UdpIP>127.0.0.1</UdpIP>
        <UdpPort>10401</UdpPort>
        <EnableUPnP>False</EnableUPnP>
        <DetectRegionIP>False</DetectRegionIP>
        <DBType>SQLITE</DBType>
    </Server>
</root>
```

- [ ] **Step 4: Create `deploy/bin/healthcheck.sh`**

```bash
#!/usr/bin/env bash
# Healthy when the game port accepts TCP connections.
ip="${OFFLINEDAOC_LISTEN_IP:-0.0.0.0}"
[[ "$ip" == 0.0.0.0 ]] && ip=127.0.0.1
exec bash -c "</dev/tcp/$ip/${OFFLINEDAOC_PORT:-10301}" 2>/dev/null
```

- [ ] **Step 5: Create `deploy/entrypoint.sh`**

```bash
#!/usr/bin/env bash
# Container entrypoint: check /data, create or check the world, generate the config, link the
# server's state files into /data, start the server with a console pipe, and turn docker stop
# (SIGTERM) into the server's own "exit" command so the world is saved.
set -euo pipefail

DATA=/data
SRV=/app/server
BIN=/app/bin
LOCK=/app/upstream.lock
EDITION="${OFFLINEDAOC_EDITION:-classic}"
LISTEN_IP="${OFFLINEDAOC_LISTEN_IP:-0.0.0.0}"
PORT="${OFFLINEDAOC_PORT:-10301}"

if ! touch "$DATA/.write-test" 2>/dev/null; then
    echo "ERROR: $DATA is not writable by uid $(id -u):$(id -g). Give the volume to that user, e.g.:" >&2
    echo "  docker run --rm --user 0 -v offlinedaoc-data:/data --entrypoint chown <image> -R $(id -u):$(id -g) /data" >&2
    exit 64
fi
rm -f "$DATA/.write-test"

init_args=(--lock "$LOCK" --data "$DATA" --edition "$EDITION")
[[ -n "${OFFLINEDAOC_SKIP_NAVMESH:-}" ]] && init_args+=(--skip-navmesh)
python3 "$BIN/init_world.py" "${init_args[@]}"

python3 "$BIN/gen_config.py" --data "$DATA" --out "$SRV/config/serverconfig.xml"

mkdir -p "$DATA/logs" "$DATA/state" "$DATA/backups" "$DATA/navmesh"
link() {  # link <path in /app/server> <target in /data>
    [[ -e "$1" && ! -L "$1" ]] && rm -rf "$1"
    ln -sfn "$2" "$1"
}
link "$SRV/navmesh" "$DATA/navmesh"
link "$SRV/logs" "$DATA/logs"
link "$SRV/bot-goals.json" "$DATA/bot-goals.json"
link "$SRV/realm-event-records.sqlite3" "$DATA/state/realm-event-records.sqlite3"

if ! python3 - "$LISTEN_IP" "$PORT" <<'EOF'
import socket, sys
s = socket.socket()
s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
try:
    s.bind((sys.argv[1], int(sys.argv[2])))
except OSError:
    sys.exit(1)
EOF
then
    echo "ERROR: TCP port $PORT is already in use on this host. Choose another OFFLINEDAOC_PORT in .env (OpenDAoC uses 10300)." >&2
    exit 65
fi

python3 "$BIN/backup.py" --data "$DATA" loop --keep "${OFFLINEDAOC_BACKUP_KEEP:-7}" &
backup_pid=$!

fifo=/tmp/offlinedaoc-console
rm -f "$fifo"
mkfifo "$fifo"
exec 3<>"$fifo"   # keep a writer open: the server's console loop busy-spins if stdin reaches EOF

cd "$SRV"
dotnet CoreServer.dll <&3 &
server_pid=$!

stopping=0
stop() {
    if (( ! stopping )); then
        stopping=1
        echo "Saving the world and stopping the server..."
        echo exit >&3
    fi
}
trap stop TERM INT

rc=0
while kill -0 "$server_pid" 2>/dev/null; do
    if wait "$server_pid"; then rc=0; else rc=$?; fi
done
kill "$backup_pid" 2>/dev/null || true
exit "$rc"
```

- [ ] **Step 6: Create `deploy/Dockerfile`**

```dockerfile
# syntax=docker/dockerfile:1
# OfflineDAoC central server (lometur fork). Build from the repository root:
#   docker build -f deploy/Dockerfile -t offlinedaoc:dev .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update \
 && apt-get install -y --no-install-recommends cmake g++ make binutils python3 \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY source/server ./source/server
COPY source/tools/OfflineDaoc.Launcher/BotCharacterGenerator.cs ./source/tools/OfflineDaoc.Launcher/BotCharacterGenerator.cs
COPY source/tools/OfflineDaoc.ProgressImport ./source/tools/OfflineDaoc.ProgressImport
COPY tools/linux ./tools/linux
COPY deploy ./deploy
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# Server (the build only needs some serverconfig.xml; never the example file, which listens on all interfaces with UPnP on)
RUN cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml \
 && dotnet build source/server/DOLLinux.sln -c Release
# Native pathfinding from the server's own Detour copy; it must export all 14 functions the server imports
RUN cmake -S source/server/Pathing/Detour -B /tmp/detour -DCMAKE_BUILD_TYPE=Release -DSOVERSION=1 -DLIB_VERSION=1.0.0 \
 && cmake --build /tmp/detour -j"$(nproc)" \
 && install -D -m 755 /tmp/detour/libDetour.so.1.0.0 source/server/Release/lib/Detour.so \
 && test "$(nm -D --defined-only source/server/Release/lib/Detour.so | grep -cE ' T (LoadNavMesh|FreeNavMesh|CreateNavMeshQuery|FreeNavMeshQuery|PathStraight|MoveAlongSurface|MoveAlongSurfaceGrounded|FindRandomPointAroundCircle|FindClosestPoint|FindClosestPointInBox|HasLineOfSight|UpdateFlags|GetPolyAt|GetPolysInBox)$')" = 14
# Upstream release config files the source tree lacks (verified against the pinned manifest)
RUN for f in logconfig.xml invalidnames.txt MailConfig.xml; do \
      python3 tools/linux/odaoc_fetch.py --lock deploy/upstream.lock extract "runtime/server/config/$f" "source/server/Release/config/$f"; \
    done
# Admin CLIs
RUN tools/linux/build.sh /out/tools

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 sqlite3 tzdata \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --uid 1000 --user-group --create-home offlinedaoc
COPY --from=build /src/source/server/Release /app/server
COPY --from=build /out/tools /app/tools
COPY tools/linux/accounts /app/tools/accounts
COPY tools/linux/odaoc_fetch.py /app/bin/odaoc_fetch.py
COPY deploy/bin /app/bin
COPY deploy/entrypoint.sh /app/entrypoint.sh
COPY deploy/upstream.lock /app/upstream.lock
# /app/server must be writable by whichever OFFLINEDAOC_UID runs it (script compilation, dashboards, links).
RUN mkdir -p /data \
 && chown 1000:1000 /data \
 && chmod -R a+rwX /app/server \
 && chmod +x /app/entrypoint.sh /app/bin/*.sh /app/bin/*.py /app/tools/accounts/accounts.py
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_CLI_HOME=/tmp \
    XDG_CONFIG_HOME=/tmp/xdg \
    OFFLINEDAOC_EDITION=classic
USER 1000:1000
VOLUME /data
HEALTHCHECK --interval=30s --timeout=5s --start-period=10m --retries=3 CMD ["/app/bin/healthcheck.sh"]
ENTRYPOINT ["/app/entrypoint.sh"]
```

- [ ] **Step 7: Create `.dockerignore`**

```
.git
source/**/bin
source/**/obj
tools/**/bin
tools/**/obj
source/server/Release
source/server/Debug
source/development-tools
source/reference
older-versions
package-files
docs
client
deploy/tests
tools/linux/tests
```

- [ ] **Step 8: Build the image and run the smoke test**

Run:
```bash
chmod +x deploy/entrypoint.sh deploy/bin/healthcheck.sh
docker build -f deploy/Dockerfile -t offlinedaoc:dev .
deploy/tests/smoke.sh offlinedaoc:dev
```
Expected: the build succeeds (the Detour export check and the three verified config downloads pass), then six `ok - ...` lines and `SMOKE OK`.

- [ ] **Step 9: Commit**

```bash
git add .dockerignore deploy/Dockerfile deploy/entrypoint.sh deploy/serverconfig.build.xml deploy/bin/healthcheck.sh deploy/tests/smoke.sh
git commit -m "feat(deploy): server image with graceful stop and smoke test

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Compose deployment and the `odc` admin command

**Files:**
- Create: `deploy/compose.yml`
- Create: `deploy/.env.example`
- Create: `deploy/odc`
- Test: `deploy/tests/odc_integration.sh`

**Interfaces:**
- Consumes: the image from Task 8; in-container tools from Tasks 5–7.
- Produces (used by Tasks 13, 14): `odc up|stop|down|logs|status|add-bots|account|bot-goals|backup|backups|restore|new-world|upgrade-world|init|auto-accounts|help`; `.env` variables `OFFLINEDAOC_TAG`, `OFFLINEDAOC_IMAGE`, `OFFLINEDAOC_EDITION`, `OFFLINEDAOC_LISTEN_IP`, `OFFLINEDAOC_PORT`, `OFFLINEDAOC_UDP_PORT`, `OFFLINEDAOC_AUTO_ACCOUNTS`, `OFFLINEDAOC_SERVER_NAME`, `OFFLINEDAOC_BACKUP_KEEP`, `OFFLINEDAOC_UID`, `OFFLINEDAOC_GID`, `OFFLINEDAOC_MEM_LIMIT`, `OFFLINEDAOC_CPUS`, `TZ`, plus test-only `OFFLINEDAOC_PROJECT`, `OFFLINEDAOC_CONTAINER`, `OFFLINEDAOC_VOLUME`, `OFFLINEDAOC_SKIP_NAVMESH`.

- [ ] **Step 1: Write the failing integration test `deploy/tests/odc_integration.sh`**

```bash
#!/usr/bin/env bash
# Compose + odc integration test on a separate project, volume and ports. Usage: odc_integration.sh <image>
set -euo pipefail
IMAGE="${1:?usage: $0 <image e.g. offlinedaoc:dev>}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
W="$(mktemp -d)"
cp "$HERE/../compose.yml" "$HERE/../odc" "$W/"
cat > "$W/.env" <<EOF
OFFLINEDAOC_IMAGE=${IMAGE%%:*}
OFFLINEDAOC_TAG=${IMAGE##*:}
OFFLINEDAOC_EDITION=classic
OFFLINEDAOC_PORT=10392
OFFLINEDAOC_UDP_PORT=10492
OFFLINEDAOC_PROJECT=offlinedaoc-it
OFFLINEDAOC_CONTAINER=offlinedaoc-it-server
OFFLINEDAOC_VOLUME=offlinedaoc-it-data
OFFLINEDAOC_SKIP_NAVMESH=1
OFFLINEDAOC_MEM_LIMIT=4g
OFFLINEDAOC_CPUS=2
EOF
odc() { "$W/odc" "$@"; }
cleanup() { odc down >/dev/null 2>&1 || true; docker volume rm -f offlinedaoc-it-data >/dev/null 2>&1 || true; rm -rf "$W"; }
trap cleanup EXIT
fail() { echo "FAIL: $*" >&2; docker logs offlinedaoc-it-server 2>&1 | tail -30 >&2 || true; exit 1; }
healthy() { for _ in $(seq 1 150); do [[ "$(docker inspect -f '{{.State.Health.Status}}' offlinedaoc-it-server 2>/dev/null)" == healthy ]] && return 0; sleep 2; done; return 1; }

odc up >/dev/null; healthy || fail "not healthy"
echo "ok - up and healthy"
[[ "$(docker inspect -f '{{.HostConfig.Memory}}' offlinedaoc-it-server)" == 4294967296 ]] || fail "memory limit not applied"
docker inspect -f '{{.HostConfig.CapDrop}}' offlinedaoc-it-server | grep -q ALL || fail "capabilities not dropped"
[[ "$(docker inspect -f '{{.HostConfig.NetworkMode}}' offlinedaoc-it-server)" == host ]] || fail "not host networking"
echo "ok - isolation settings applied"
odc status | grep -q "edition .* classic" || fail "status"
odc account create Tester1 pw1 >/dev/null || fail "account create"
odc account list | grep -q Tester1 || fail "account list"
odc add-bots hib 1 1 | grep -q . || fail "add-bots"
odc backup | grep -q "/data/backups/world-" || fail "backup"
echo "ok - status, accounts, add-bots and backup while running"
if odc bot-goals set 50 10 30 60 2>/dev/null; then fail "bot-goals write allowed while running"; fi
if odc restore x.db 2>/dev/null; then fail "restore allowed while running"; fi
echo "ok - stopped-only commands refuse while running"
odc stop >/dev/null
docker logs offlinedaoc-it-server 2>&1 | grep -q "| DOL.GS.GameServer | Stopped" || fail "no clean save"
odc bot-goals set 50 10 30 60 | grep -q "Saved" || fail "bot-goals set while stopped"
odc account plvl Tester1 3 | grep -q "plvl 3" || fail "plvl while stopped"
latest="$(odc backups | awk '/-backup.db/ {print $NF}' | tail -1)"
odc restore "$latest" | grep -q "Restored" || fail "restore"
echo "ok - stopped-only commands work when stopped"
sed -i 's/^OFFLINEDAOC_EDITION=classic/OFFLINEDAOC_EDITION=b/' "$W/.env"
odc up >/dev/null 2>&1 || true; sleep 6
[[ "$(docker inspect -f '{{.State.ExitCode}}' offlinedaoc-it-server)" == 3 ]] || fail "edition change not refused"
odc stop >/dev/null 2>&1 || true
sed -i 's/^OFFLINEDAOC_EDITION=b/OFFLINEDAOC_EDITION=classic/' "$W/.env"
echo "ok - edition change refused through compose"
odc auto-accounts off >/dev/null; healthy || fail "not healthy after auto-accounts off"
docker exec offlinedaoc-it-server grep -q "<AutoAccountCreation>False</AutoAccountCreation>" /app/server/config/serverconfig.xml || fail "auto-accounts not off"
echo "ok - auto-accounts off recreates the server with the new setting"
echo "ODC INTEGRATION OK"
```

- [ ] **Step 2: Run it to verify it fails**

Run: `chmod +x deploy/tests/odc_integration.sh && deploy/tests/odc_integration.sh offlinedaoc:dev`
Expected: `cp: cannot stat '.../deploy/compose.yml': No such file or directory`.

- [ ] **Step 3: Create `deploy/compose.yml`**

```yaml
# OfflineDAoC central server (lometur fork). Manage with ./odc; settings live in .env.
name: offlinedaoc

services:
  server:
    image: ${OFFLINEDAOC_IMAGE:-ghcr.io/lometur/offlinedaoc}:${OFFLINEDAOC_TAG:?set OFFLINEDAOC_TAG in .env}
    container_name: ${OFFLINEDAOC_CONTAINER:-offlinedaoc-server}
    # Host networking so the server tells clients its real address for the UDP channel
    # (inside a bridge network it would be an unreachable 172.x address). Own ports, see .env.
    network_mode: host
    init: true
    user: "${OFFLINEDAOC_UID:-1000}:${OFFLINEDAOC_GID:-1000}"
    environment:
      OFFLINEDAOC_EDITION: ${OFFLINEDAOC_EDITION:-classic}
      OFFLINEDAOC_LISTEN_IP: ${OFFLINEDAOC_LISTEN_IP:-0.0.0.0}
      OFFLINEDAOC_PORT: ${OFFLINEDAOC_PORT:-10301}
      OFFLINEDAOC_UDP_PORT: ${OFFLINEDAOC_UDP_PORT:-10401}
      OFFLINEDAOC_AUTO_ACCOUNTS: ${OFFLINEDAOC_AUTO_ACCOUNTS:-True}
      OFFLINEDAOC_SERVER_NAME: ${OFFLINEDAOC_SERVER_NAME:-OfflineDAoC (lometur fork)}
      OFFLINEDAOC_BACKUP_KEEP: ${OFFLINEDAOC_BACKUP_KEEP:-7}
      OFFLINEDAOC_SKIP_NAVMESH: ${OFFLINEDAOC_SKIP_NAVMESH:-}
      TZ: ${TZ:-UTC}
    volumes:
      - data:/data
    restart: unless-stopped
    stop_grace_period: 120s
    mem_limit: ${OFFLINEDAOC_MEM_LIMIT:-10g}
    cpus: ${OFFLINEDAOC_CPUS:-4}
    cap_drop: [ALL]
    security_opt: ["no-new-privileges:true"]

volumes:
  data:
    name: ${OFFLINEDAOC_VOLUME:-offlinedaoc-data}
```

- [ ] **Step 4: Create `deploy/.env.example`**

```bash
# Copy to .env and adjust. ./odc and docker compose read this file; put values with spaces in double quotes.
OFFLINEDAOC_TAG=v0.34b-fork.1
# classic = 0.34 (no custom class), b = 0.34b (Sluaghbinder). Fixed once the world exists.
OFFLINEDAOC_EDITION=classic
# Ports must differ from OpenDAoC's 10300/10400 on the same machine.
OFFLINEDAOC_PORT=10301
OFFLINEDAOC_UDP_PORT=10401
# 0.0.0.0 = all interfaces (limit access with the host firewall; see HANDOFF.md).
OFFLINEDAOC_LISTEN_IP=0.0.0.0
# First login creates the account. Turn off (./odc auto-accounts off) before remote players join.
OFFLINEDAOC_AUTO_ACCOUNTS=True
OFFLINEDAOC_SERVER_NAME="OfflineDAoC (lometur fork)"
OFFLINEDAOC_BACKUP_KEEP=7
OFFLINEDAOC_MEM_LIMIT=10g
OFFLINEDAOC_CPUS=4
OFFLINEDAOC_UID=1000
OFFLINEDAOC_GID=1000
TZ=America/Chicago
```

- [ ] **Step 5: Create `deploy/odc`**

```bash
#!/usr/bin/env bash
# odc: manage the OfflineDAoC central server deployment. Run it from the folder holding
# compose.yml and .env. See HANDOFF.md for setup.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[[ -f "$here/.env" ]] || { echo "Missing $here/.env (copy .env.example to .env and edit it)." >&2; exit 1; }
set -a; source "$here/.env"; set +a

PROJECT="${OFFLINEDAOC_PROJECT:-offlinedaoc}"
CONTAINER="${OFFLINEDAOC_CONTAINER:-offlinedaoc-server}"
VOLUME="${OFFLINEDAOC_VOLUME:-offlinedaoc-data}"
UIDGID="${OFFLINEDAOC_UID:-1000}:${OFFLINEDAOC_GID:-1000}"
PORT="${OFFLINEDAOC_PORT:-10301}"
KEEP="${OFFLINEDAOC_BACKUP_KEEP:-7}"
DB=/data/world/opendaoc.sqlite3.db
ADMIN=(python3 /app/bin/world_admin.py --data /data --lock /app/upstream.lock)

compose() { docker compose -p "$PROJECT" -f "$here/compose.yml" --env-file "$here/.env" "$@"; }
# Create the volume with compose's labels when odc needs it before the first "up", so compose adopts it.
ensure_volume() { docker volume inspect "$VOLUME" >/dev/null 2>&1 || docker volume create \
    --label com.docker.compose.project="$PROJECT" --label com.docker.compose.volume=data "$VOLUME" >/dev/null; }
image() { compose config --images | head -1; }
running() { [[ "$(docker inspect -f '{{.State.Running}}' "$CONTAINER" 2>/dev/null)" == true ]]; }
need_stopped() { if running; then echo "The server is running. Stop it first: ./odc stop" >&2; exit 1; fi; }
# Throwaway container on the world volume with no network: the upstream import engine and the
# bot-goals tool look for game servers on local ports, and host ports (OpenDAoC) must not count.
offline() { ensure_volume; docker run --rm -i --network none --user "$UIDGID" --cap-drop ALL \
              --security-opt no-new-privileges:true -v "$VOLUME":/data "$@"; }
# tool PROGRAM ARGS...: run inside the live server, or in a throwaway container when stopped.
tool() { if running; then docker exec -i "$CONTAINER" "$@"; else offline --entrypoint "$1" "$(image)" "${@:2}"; fi; }

usage() {
    cat <<'EOF'
odc - OfflineDAoC central server

  up | stop | down | logs        start, stop (saves first), remove containers (volume kept), follow logs
  status                         server state, world, accounts, players, bots, latest backup
  init [--seed-navmesh DIR]      create the world now (optionally reuse verified navmesh files from DIR)
  add-bots <alb|mid|hib> <1|10|100> <1|50>
  account create <name> <password> [--plvl 1-3] | list | plvl <name> <1-3>
  bot-goals show | set <1-19|20-49|50> <solo> <group> <rvr> | reset | import <file>   (changes need the server stopped)
  backup | backups               take a backup now | list backups
  restore <backup file name>     (server stopped)
  new-world [--edition classic|b]   archive the current world and start a fresh one (server stopped)
  upgrade-world [--same-version]    move progress into the clean world of this image's upstream version (server stopped)
  auto-accounts on|off           allow or stop account creation at first login (recreates the server)
EOF
}

cmd="${1:-help}"; shift || true
case "$cmd" in
    up|start) compose up -d ;;
    stop) compose stop ;;
    down) compose down ;;
    logs) compose logs -f --tail=200 server ;;
    status)
        if running; then
            echo "Server: running ($(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}no health check{{end}}' "$CONTAINER"))"
        else
            echo "Server: stopped"
        fi
        tool "${ADMIN[@]}" status ;;
    init)
        need_stopped
        args=(); mounts=()
        if [[ "${1:-}" == --seed-navmesh ]]; then
            mounts=(-v "$(realpath "${2:?folder}")":/seed:ro); args=(--seed-navmesh /seed)
        fi
        ensure_volume
        docker run --rm --network host --user "$UIDGID" -v "$VOLUME":/data "${mounts[@]}" --entrypoint python3 "$(image)" \
            /app/bin/init_world.py --lock /app/upstream.lock --data /data --edition "${OFFLINEDAOC_EDITION:-classic}" "${args[@]}" ;;
    add-bots)
        running || { echo "Start the server first (./odc up); new bots log in while it runs." >&2; exit 1; }
        docker exec -i "$CONTAINER" /app/bin/add-bots.sh "$@" ;;
    account)
        if [[ "${1:-}" == plvl ]] && ! running; then
            tool python3 /app/tools/accounts/accounts.py --db "$DB" "$@" --server-stopped
        else
            tool python3 /app/tools/accounts/accounts.py --db "$DB" "$@"
        fi ;;
    bot-goals)
        sub="${1:-show}"
        if [[ "$sub" == show ]]; then
            tool dotnet /app/tools/bot-goals/bot-goals.dll --port "$PORT" /data show
        else
            need_stopped
            if [[ "$sub" == import ]]; then
                f="$(realpath "${2:?file}")"
                offline -v "$f":/import/bot-goals.json:ro --entrypoint dotnet "$(image)" \
                    /app/tools/bot-goals/bot-goals.dll --port "$PORT" /data import /import/bot-goals.json
            else
                offline --entrypoint dotnet "$(image)" /app/tools/bot-goals/bot-goals.dll --port "$PORT" /data "$@"
            fi
        fi ;;
    backup) tool python3 /app/bin/backup.py --data /data create --keep "$KEEP" ;;
    backups) tool python3 /app/bin/backup.py --data /data list ;;
    restore) need_stopped; offline --entrypoint python3 "$(image)" "${ADMIN[@]:1}" restore "${1:?backup file name (see ./odc backups)}" ;;
    new-world)
        need_stopped
        edition="${OFFLINEDAOC_EDITION:-classic}"
        [[ "${1:-}" == --edition ]] && edition="${2:?classic or b}"
        ensure_volume
        docker run --rm --network host --user "$UIDGID" -v "$VOLUME":/data --entrypoint python3 "$(image)" \
            "${ADMIN[@]:1}" new-world --edition "$edition"
        if [[ "$edition" != "${OFFLINEDAOC_EDITION:-classic}" ]]; then
            sed -i "s/^OFFLINEDAOC_EDITION=.*/OFFLINEDAOC_EDITION=$edition/" "$here/.env"
            echo "Updated OFFLINEDAOC_EDITION=$edition in .env."
        fi ;;
    upgrade-world)
        need_stopped
        # Download with the network, then import in a container without one: upstream's import engine
        # refuses while anything listens on port 10300, and the host's OpenDAoC server must not count.
        ensure_volume
        docker run --rm --network host --user "$UIDGID" -v "$VOLUME":/data --entrypoint python3 "$(image)" \
            "${ADMIN[@]:1}" fetch-clean
        offline --entrypoint python3 "$(image)" "${ADMIN[@]:1}" upgrade-world --clean-world /data/upgrade-clean-world.db \
            --importer "dotnet /app/tools/progress-import/progress-import.dll" "$@" ;;
    auto-accounts)
        case "${1:-}" in on) v=True ;; off) v=False ;; *) echo "usage: odc auto-accounts on|off" >&2; exit 2 ;; esac
        if grep -q '^OFFLINEDAOC_AUTO_ACCOUNTS=' "$here/.env"; then
            sed -i "s/^OFFLINEDAOC_AUTO_ACCOUNTS=.*/OFFLINEDAOC_AUTO_ACCOUNTS=$v/" "$here/.env"
        else
            echo "OFFLINEDAOC_AUTO_ACCOUNTS=$v" >> "$here/.env"
        fi
        compose up -d --force-recreate
        echo "Account creation at first login is now $1." ;;
    help|-h|--help) usage ;;
    *) usage >&2; exit 2 ;;
esac
```

Note on `upgrade-world`: the clean world is fetched in a networked container, then the import runs
in a `--network none` container with its own process namespace, so the upstream engine's "port 10300
is listening" and "CoreServer is running" checks never see the host's OpenDAoC server.

- [ ] **Step 6: Run the integration test**

Run: `chmod +x deploy/odc && deploy/tests/odc_integration.sh offlinedaoc:dev`
Expected: seven `ok - ...` lines and `ODC INTEGRATION OK`.

- [ ] **Step 7: Commit**

```bash
git add deploy/compose.yml deploy/.env.example deploy/odc deploy/tests/odc_integration.sh
git commit -m "feat(deploy): compose definition and odc admin command

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: CI image build and publish

**Files:**
- Create: `.github/workflows/server-image.yml`

**Interfaces:**
- Consumes: all previous tasks' tests and the Dockerfile.
- Produces (used by Task 14): image `ghcr.io/lometur/offlinedaoc:<tag>` on tags `v*-fork.*`; a job named `release-assets` that later tasks extend.

- [ ] **Step 1: Create `.github/workflows/server-image.yml`**

```yaml
name: server-image

on:
  push:
    branches: [main]
    tags: ["v*-fork.*"]
  pull_request:

permissions:
  contents: read
  packages: write

jobs:
  test-build-publish:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"

      - name: Install test tools
        run: sudo apt-get update && sudo apt-get install -y --no-install-recommends sqlite3 bubblewrap

      - name: Fetch a clean classic world for tests (database only)
        run: python3 deploy/bin/init_world.py --lock deploy/upstream.lock --data "$RUNNER_TEMP/world" --edition classic --skip-navmesh

      - name: Build Linux CLIs
        run: tools/linux/build.sh "$RUNNER_TEMP/tools"

      - name: Unit tests
        env:
          ODC_TEST_WORLD: ${{ runner.temp }}/world/world/opendaoc.sqlite3.db
          ODC_TOOLS: ${{ runner.temp }}/tools
        run: |
          python3 -m unittest discover -s tools/linux/tests -t tools/linux -v
          python3 -m unittest discover -s deploy/tests -t deploy -v
          python3 -m unittest discover -s client/tests -t client -v

      - name: CLI integration tests
        env:
          ODC_TEST_WORLD: ${{ runner.temp }}/world/world/opendaoc.sqlite3.db
        run: tools/linux/tests/test_cli_tools.sh "$RUNNER_TEMP/tools"

      - name: Build image
        run: docker build -f deploy/Dockerfile -t offlinedaoc:ci .

      - name: Smoke test
        run: deploy/tests/smoke.sh offlinedaoc:ci

      - name: Compose and odc integration test
        run: deploy/tests/odc_integration.sh offlinedaoc:ci

      - name: Publish image (tags only)
        if: startsWith(github.ref, 'refs/tags/')
        run: |
          echo "${{ secrets.GITHUB_TOKEN }}" | docker login ghcr.io -u "${{ github.actor }}" --password-stdin
          img="ghcr.io/${{ github.repository_owner }}/offlinedaoc:${{ github.ref_name }}"
          docker tag offlinedaoc:ci "$img"
          docker push "$img"
```

Note: the `client/tests` discovery line needs Task 11; until Task 11 is merged, the step fails with
`Start directory is not importable: 'client/tests'`. Create an empty `client/tests/__init__.py` in
this task so discovery finds zero tests and passes.

- [ ] **Step 2: Create `client/tests/__init__.py`** (empty file).

- [ ] **Step 3: Ask the owner to enable Actions on the fork**

Tell the owner: on GitHub, open `lometur/OfflineDAoC` → **Actions** → "I understand my workflows, go
ahead and enable them". Forks start with Actions disabled. Wait for confirmation.

- [ ] **Step 4: Push and watch the run**

Run:
```bash
git add .github/workflows/server-image.yml client/tests/__init__.py
git commit -m "ci: test, build and publish the server image

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git -c credential.helper= -c credential.helper='!gh auth git-credential' push origin main
gh run watch --repo lometur/OfflineDAoC --exit-status "$(gh run list --repo lometur/OfflineDAoC --workflow server-image --limit 1 --json databaseId --jq '.[0].databaseId')"
```
Expected: the run finishes with every step green (the publish step is skipped on `main`).

---

### Task 11: Linux client setup

**Files:**
- Create: `client/linux/setup.sh`
- Create: `client/linux/play.sh.in`
- Test: `client/tests/test_setup.py`

**Interfaces:**
- Consumes: Task 2 `odaoc_fetch.py client`, `deploy/upstream.lock` (or `--lock` override).
- Produces (used by Tasks 13, 14): `setup.sh --server HOST:PORT --edition classic|b --base-client DIR [--dest DIR] [--lock FILE]` → `DEST/client/` (verified), `DEST/play.sh`; `play.sh` honours `OFFLINEDAOC_SERVER` and `DAOC_PROTON` overrides and stores credentials in `DEST/account.txt` (`Account:`/`Password:` lines, mode 600).

- [ ] **Step 1: Write the failing test `client/tests/test_setup.py`**

```python
import json
import os
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


class SetupTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = self.tmp.name
        self.lock, self.files = fx.build(self.dir)
        self.base = os.path.join(self.dir, "base")
        os.makedirs(self.base)
        for name, data in (("connect.exe", self.files["runtime/client-opendaoc/app/connect.exe"]),
                           ("game1127.dll", b"scaling"), ("game.dll", b"stock"), ("paths.dat", b"[paths]\r\nsettings=Atlas1")):
            with open(os.path.join(self.base, name), "wb") as f:
                f.write(data)

    def tearDown(self):
        self.tmp.cleanup()

    def run_setup(self, srv, *extra):
        lock_path = os.path.join(self.dir, "upstream.lock")
        with open(lock_path, "w") as f:
            json.dump(srv.lock(self.lock), f)
        dest = os.path.join(self.dir, "dest")
        args = ["bash", SETUP, "--server", "192.168.1.64:10301", "--edition", "classic",
                "--base-client", self.base, "--dest", dest, "--lock", lock_path, *extra]
        return dest, subprocess.run(args, capture_output=True, text=True)

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
        self.assertIn('SERVER="${OFFLINEDAOC_SERVER:-192.168.1.64:10301}"', text)
        self.assertNotIn("@SERVER@", text)
        self.assertEqual(subprocess.run(["bash", "-n", play]).returncode, 0)

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


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `python3 -m unittest discover -s client/tests -t client -v`
Expected: failures with `No such file or directory` for `setup.sh`.

- [ ] **Step 3: Create `client/linux/play.sh.in`**

```bash
#!/usr/bin/env bash
# Play OfflineDAoC on the central server @SERVER@ (edition @EDITION@) through Proton.
# Add this file to Steam as a non-Steam game WITHOUT forcing a compatibility tool;
# any launch options must end with %command%.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CL="$HERE/client"
SERVER="${OFFLINEDAOC_SERVER:-@SERVER@}"
CREDS="$HERE/account.txt"

if [[ ! -f "$CREDS" ]]; then
    if command -v zenity >/dev/null; then
        form="$(zenity --forms --title="OfflineDAoC login" \
            --text="Server: $SERVER\nUse a password you use nowhere else." \
            --add-entry="Account (letters and digits)" --add-password="Password" --separator=$'\n')" || exit 1
        acct="$(sed -n 1p <<<"$form")"; pass="$(sed -n 2p <<<"$form")"
    else
        read -rp "Account (letters and digits): " acct
        read -rsp "Password (use one you use nowhere else): " pass; echo
    fi
    [[ "$acct" =~ ^[A-Za-z0-9]+$ ]] || { echo "Account names may only contain letters and digits." >&2; exit 1; }
    [[ -n "$pass" && ! "$pass" =~ [[:space:]] ]] || { echo "The password must be non-empty with no spaces." >&2; exit 1; }
    (umask 077 && printf 'Account: %s\nPassword: %s\n' "$acct" "$pass" > "$CREDS")
fi
ACCT="$(sed -n 's/^Account:[[:space:]]*//p' "$CREDS" | tr -d '\r')"
PASS="$(sed -n 's/^Password:[[:space:]]*//p' "$CREDS" | tr -d '\r')"

STEAM_ROOT="$(readlink -f "$HOME/.steam/steam")"
PROTON_DIR="${DAOC_PROTON:-$STEAM_ROOT/steamapps/common/Proton - Experimental}"
[[ -x "$PROTON_DIR/proton" ]] || { echo "Proton not found at $PROTON_DIR (install Proton Experimental in Steam, or set DAOC_PROTON)." >&2; exit 1; }
tool_appid="$(sed -n 's/.*"require_tool_appid"[[:space:]]*"\([0-9]*\)".*/\1/p' "$PROTON_DIR/toolmanifest.vdf")"
runtime_dir="$(sed -n 's/.*"installdir"[[:space:]]*"\(.*\)".*/\1/p' "$STEAM_ROOT/steamapps/appmanifest_$tool_appid.acf" 2>/dev/null)"
RUNTIME="$STEAM_ROOT/steamapps/common/$runtime_dir"
[[ -n "$runtime_dir" && -x "$RUNTIME/_v2-entry-point" ]] || {
    echo "Steam Linux Runtime (app $tool_appid) needed by $(basename "$PROTON_DIR") is not installed; install it from Steam." >&2; exit 1; }

unset WINEPREFIX WINEDLLOVERRIDES
export STEAM_COMPAT_DATA_PATH="$HERE/proton"
export STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM_ROOT"
export STEAM_COMPAT_TOOL_PATHS="$PROTON_DIR:$RUNTIME"
# Keep Steam's ids when launched from Steam; Proton's steam.exe needs SteamGameId set to keep
# waiting on the game after connect.exe exits.
export SteamAppId="${SteamAppId:-0}" SteamGameId="${SteamGameId:-0}"
export PROTON_USE_XALIA="${PROTON_USE_XALIA:-0}"
mkdir -p "$STEAM_COMPAT_DATA_PATH"

run_proton() { "$RUNTIME/_v2-entry-point" --verb=waitforexitandrun -- "$PROTON_DIR/proton" waitforexitandrun "$@"; }

PFX="$STEAM_COMPAT_DATA_PATH/pfx"
if [[ ! -f "$PFX/user.reg" ]]; then
    echo "Creating the Proton prefix (first run only)..."
    run_proton cmd.exe /c exit || true
fi
# The client drops the last character of paths.dat's final line (no trailing newline), so
# "settings=OfflineDAoC034" means the profile folder "OfflineDAoC03".
profile_name="$(sed -n 's/^settings=//p' "$CL/paths.dat" | tr -d '\r')"
profile_name="${profile_name%?}"
PROFILE="$PFX/drive_c/users/steamuser/AppData/Roaming/Electronic Arts/Dark Age of Camelot/$profile_name"
if [[ ! -f "$PROFILE/user.dat" ]]; then
    mkdir -p "$PROFILE"
    printf '[main]\r\nfullscreen_windowed=1\r\n' > "$PROFILE/user.dat"
fi

cd "$CL"
# Proton doesn't search the working directory for the exe: pass a Linux path for connect.exe and
# the game DLL as a Windows path (Z: is /).
exec "$RUNTIME/_v2-entry-point" --verb=waitforexitandrun -- "$PROTON_DIR/proton" waitforexitandrun \
    "$CL/connect.exe" "Z:${CL//\//\\}\\game.dll" "$SERVER" "$ACCT" "$PASS"
```

- [ ] **Step 4: Create `client/linux/setup.sh`**

```bash
#!/usr/bin/env bash
# Set up an OfflineDAoC client for the central server (Linux, Steam/Proton).
set -euo pipefail

usage() {
    cat <<'EOF'
usage: setup.sh --server HOST:PORT --edition classic|b --base-client DIR [--dest DIR]

  --server       the central server, e.g. 192.168.1.64:10301 (ask the server owner)
  --edition      classic or b; must match the server (ask the server owner)
  --base-client  your 1.127 client folder (e.g. from the OpenDAoC installer); only read
  --dest         where to create the OfflineDAoC client (default ~/Games/OfflineDAoC-Central)
EOF
}

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVER="" EDITION="" BASE="" DEST="$HOME/Games/OfflineDAoC-Central" LOCK=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --server) SERVER="${2:-}"; shift 2 ;;
        --edition) EDITION="${2:-}"; shift 2 ;;
        --base-client) BASE="${2:-}"; shift 2 ;;
        --dest) DEST="${2:-}"; shift 2 ;;
        --lock) LOCK="${2:-}"; shift 2 ;;   # advanced/testing: alternative upstream.lock
        -h|--help) usage; exit 0 ;;
        *) usage >&2; exit 2 ;;
    esac
done
[[ -n "$SERVER" && -n "$EDITION" && -n "$BASE" ]] || { usage >&2; exit 2; }
[[ "$SERVER" =~ ^[A-Za-z0-9.-]+:[0-9]+$ ]] || { echo "--server must look like 192.168.1.64:10301" >&2; exit 2; }
[[ "$EDITION" == classic || "$EDITION" == b ]] || { echo "--edition must be classic or b" >&2; exit 2; }
[[ -f "$BASE/connect.exe" && -f "$BASE/game1127.dll" ]] || {
    echo "$BASE doesn't look like a 1.127 client (connect.exe and game1127.dll not found)." >&2; exit 2; }

find_file() {  # find_file <bundle name> <repo path>
    for p in "$here/$1" "$here/../../$2"; do [[ -f "$p" ]] && { echo "$p"; return; }; done
    echo "Missing $1 next to setup.sh; download the full client bundle." >&2; exit 1
}
FETCH="$(find_file odaoc_fetch.py tools/linux/odaoc_fetch.py)"
[[ -n "$LOCK" ]] || LOCK="$(find_file upstream.lock deploy/upstream.lock)"
TEMPLATE="$(find_file play.sh.in client/linux/play.sh.in)"
for t in python3 rsync; do command -v "$t" >/dev/null || { echo "Please install $t first." >&2; exit 1; }; done

mkdir -p "$DEST"
echo "Copying your base client (read only) to $DEST/client ..."
rsync -a --delete --exclude='*.dxvk-cache' --exclude='/logs/' --exclude='/login.log' "$BASE/" "$DEST/client/"
echo "Fetching the OfflineDAoC $EDITION client files (each verified) ..."
python3 "$FETCH" --lock "$LOCK" client --edition "$EDITION" --client-dir "$DEST/client"
sed -e "s|@SERVER@|$SERVER|g" -e "s|@EDITION@|$EDITION|g" "$TEMPLATE" > "$DEST/play.sh"
chmod +x "$DEST/play.sh"
cat <<EOF

Done. Play with: $DEST/play.sh
Add it to Steam: Games > Add a Non-Steam Game > Browse > $DEST/play.sh, and leave
"Force the use of a specific Steam Play compatibility tool" unchecked.
EOF
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `chmod +x client/linux/setup.sh && python3 -m unittest discover -s client/tests -t client -v`
Expected: `Ran 2 tests ... OK`.

- [ ] **Step 6: Commit**

```bash
git add client/linux/setup.sh client/linux/play.sh.in client/tests/test_setup.py
git commit -m "feat(client): Linux setup and Proton launcher for the central server

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Windows launcher and player README

**Files:**
- Create: `client/windows/connect-central.bat`
- Create: `client/README.md`
- Test: `client/tests/test_windows_bat.sh`

**Interfaces:**
- Consumes: nothing from earlier tasks (Windows players use upstream's official install).
- Produces (used by Task 14): `connect-central.bat` reading/writing `central-server.cfg` (`SERVER=`, `ACCOUNT=`, `PASSWORD=` lines) next to itself; `DRYRUN=1` prints the command instead of running it.

- [ ] **Step 1: Write the failing test `client/tests/test_windows_bat.sh`** (local only; needs Wine)

```bash
#!/usr/bin/env bash
# Runs connect-central.bat under Wine in a throwaway prefix with DRYRUN=1. Local only (needs wine).
set -euo pipefail
command -v wine >/dev/null || { echo "SKIP: wine not installed"; exit 0; }
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
T="$(mktemp -d)"; trap 'WINEPREFIX="$T/pfx" wineserver -k 2>/dev/null || true; rm -rf "$T"' EXIT
mkdir -p "$T/app"; cp "$HERE/../windows/connect-central.bat" "$T/app/"; : > "$T/app/connect.exe"
printf 'SERVER=192.168.1.64:10301\r\nACCOUNT=Tester1\r\nPASSWORD=pw1\r\n' > "$T/app/central-server.cfg"
export WINEPREFIX="$T/pfx" WINEDEBUG=-all WINEDLLOVERRIDES="mscoree,mshtml=;winemenubuilder.exe=d"
wineboot -i >/dev/null 2>&1
out="$(cd "$T/app" && DRYRUN=1 wine cmd /c connect-central.bat 2>/dev/null | tr -d '\r')"
grep -q "connect.exe game.dll 192.168.1.64:10301 Tester1 pw1" <<<"$out" || { echo "FAIL: got: $out" >&2; exit 1; }
echo "ok - connect-central.bat builds the connect.exe command from central-server.cfg"
```

- [ ] **Step 2: Run it to verify it fails**

Run: `chmod +x client/tests/test_windows_bat.sh && client/tests/test_windows_bat.sh`
Expected: `cp: cannot stat '.../client/windows/connect-central.bat'`.

- [ ] **Step 3: Create `client/windows/connect-central.bat`** (CRLF line endings)

```bat
@echo off
setlocal EnableExtensions
rem Connect this OfflineDAoC client to the central server.
rem Put this file in your official OfflineDAoC install's runtime\client-opendaoc\app folder
rem (next to connect.exe). Settings are saved in central-server.cfg next to this file.
cd /d "%~dp0"
if not exist connect.exe (
    echo connect.exe was not found. Put this file in runtime\client-opendaoc\app of your OfflineDAoC install.
    pause
    exit /b 1
)
set "CFG=%~dp0central-server.cfg"
if exist "%CFG%" goto load
set /p "SERVER=Server address (host:port, ask the server owner): "
set /p "ACCOUNT=Account name (letters and digits): "
set /p "PASSWORD=Password (use one you use nowhere else, no spaces): "
> "%CFG%" echo SERVER=%SERVER%
>> "%CFG%" echo ACCOUNT=%ACCOUNT%
>> "%CFG%" echo PASSWORD=%PASSWORD%
:load
for /f "usebackq tokens=1,* delims==" %%A in ("%CFG%") do set "%%A=%%B"
if defined DRYRUN (
    echo connect.exe game.dll %SERVER% %ACCOUNT% %PASSWORD%
    exit /b 0
)
start "" connect.exe game.dll %SERVER% %ACCOUNT% %PASSWORD%
```

Convert to CRLF: `sed -i 's/$/\r/' client/windows/connect-central.bat` (run once; check with `file client/windows/connect-central.bat` → `with CRLF line terminators`).

- [ ] **Step 4: Create `client/README.md`**

```markdown
> [!IMPORTANT]
> These scripts belong to **`lometur/OfflineDAoC`, an unofficial fork** of
> [shadowofze/OfflineDAoC](https://github.com/shadowofze/OfflineDAoC). They connect you to a private
> central OfflineDAoC server; for the official single-player game, use the upstream project.

# Playing on the central server

Ask the server owner for two things first: the **server address** (for example `192.168.1.64:10301`)
and the **edition** (`classic` or `b`). Your client must use the same edition as the server.

Use a password you use nowhere else: the server stores passwords with a weak, unsalted hash, and the
password appears on the game's command line.

## Linux (Steam / Proton)

You need Steam with **Proton Experimental**, plus `python3` and `rsync`, and a 1.127 client folder
from the free [OpenDAoC installer](https://www.opendaoc.com/docs/client/) (it is only read).

1. Download and unpack `offlinedaoc-client-<version>.zip` from the fork's releases.
2. Run:
   `./setup.sh --server 192.168.1.64:10301 --edition classic --base-client "/path/to/your/1.127 client"`
   This builds a separate client in `~/Games/OfflineDAoC-Central` and downloads about 45 MB of
   OfflineDAoC files, each checked against the official release.
3. Steam → Games → **Add a Non-Steam Game** → Browse → `~/Games/OfflineDAoC-Central/play.sh`.
   Leave "Force the use of a specific Steam Play compatibility tool" **unchecked**. Any launch
   options must end with `%command%`.
4. Press Play. The first time, it asks for an account name and password. While the server allows it,
   your first login creates the account.

## Windows

1. Install the **official** OfflineDAoC release for the server's edition (upstream's
   `DOWNLOAD-AND-PLAY-v0.34.cmd` for `classic`, `DOWNLOAD-AND-PLAY-v0.34b.cmd` for `b`).
2. Turn on the Windows feature **.NET Framework 3.5 (includes .NET 2.0 and 3.0)**; the game's
   `connect.exe` needs it.
3. Copy `connect-central.bat` into the install's `runtime\client-opendaoc\app` folder and run it.
   It asks for the server address, account and password once and saves them in `central-server.cfg`.
   Don't use OfflineDAoC's own launcher for this; its "Enter Realm" only connects to your own PC.

## Character keeps running on its own (Linux)

On Proton 10 and later, a movement key can get "stuck" after zoning or certain key presses.
Workarounds: remove the **Run Lock 2** key binding (NumLock) in the game's keyboard options, and set
the Steam launch options of the game to `XMODIFIERS=@im=none %command%`. Tapping W once releases it.
```

- [ ] **Step 5: Run the Windows test**

Run: `client/tests/test_windows_bat.sh`
Expected: `ok - connect-central.bat builds the connect.exe command from central-server.cfg`.

- [ ] **Step 6: Commit**

```bash
git add client/windows/connect-central.bat client/README.md client/tests/test_windows_bat.sh
git commit -m "feat(client): Windows launcher and player README

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 13: Full local verification on the owner's PC

**Files:**
- Create: `docs/fork/verification/sub1-local.md`

**Interfaces:**
- Consumes: everything above; the owner's PC (`~/Games/OfflineDAoC`, Steam with Proton Experimental, the base client at `~/Games/DAoC/prefix/drive_c/OpenDAoC`).
- Produces: a written record of each check's result (used by Task 14 to decide on release).

- [ ] **Step 1: Deploy the dev image locally with the real world**

```bash
mkdir -p ~/Games/OfflineDAoC-Server-Test && cd ~/Games/OfflineDAoC-Server-Test
cp ~/Games/OfflineDAoC/src/OfflineDAoC/deploy/{compose.yml,odc,.env.example} .
cp .env.example .env
sed -i -e 's/^OFFLINEDAOC_TAG=.*/OFFLINEDAOC_TAG=dev/' .env && echo 'OFFLINEDAOC_IMAGE=offlinedaoc' >> .env
./odc init --seed-navmesh ~/Games/OfflineDAoC/runtime/server/navmesh
./odc up && sleep 60 && ./odc status
```
Expected: `init` reports `navmeshes ready (99 files, 0 downloaded)` (all reused from the seed after
verification); `status` shows `running (healthy)`, `edition classic`, `navmesh True`.

- [ ] **Step 2: Check the server log**

Run: `docker exec offlinedaoc-server sh -c 'grep -c "Loading NavMesh successful" /data/logs/server.log; grep "Server is now listening" /data/logs/server.log | tail -1'`
Expected: `99` and `... listening for incoming connections on 0.0.0.0:10301`.

- [ ] **Step 3: Connect a real client**

```bash
bash ~/Games/OfflineDAoC/src/OfflineDAoC/client/linux/setup.sh --server 127.0.0.1:10301 --edition classic \
    --base-client ~/Games/DAoC/prefix/drive_c/OpenDAoC --dest ~/Games/OfflineDAoC-Central
```
Then ask the owner to run `~/Games/OfflineDAoC-Central/play.sh`, log in with a new throwaway account,
create a character and enter the world. Expected: the server log shows `New account created: <name>`,
the character enters the world, and `./odc status` shows `players_possibly_online 1`.

- [ ] **Step 4: Exercise the admin commands with the owner logged in, then out**

```bash
./odc add-bots alb 10 1 && ./odc add-bots mid 10 1 && ./odc add-bots hib 10 1
sleep 120 && ./odc status        # bots_online rises toward 30
./odc backup && ./odc backups
./odc account plvl <owner-account> 2   # expected: refused while the owner is logged in
./odc account create Friend1 Fr1endPass
```
Ask the owner to quit the game, delete `~/Games/OfflineDAoC-Central/account.txt`, start `play.sh`
again and log in as `Friend1` / `Fr1endPass` (proves accounts made by `odc` use the server's password
hashing), then quit again. Then:
```bash
./odc account plvl <owner-account> 2   # now accepted
./odc stop                             # expected: completes within 120 s
docker logs offlinedaoc-server 2>&1 | grep "| DOL.GS.GameServer | Stopped"
./odc up                               # the owner logs in again: character and bots are still there
```

- [ ] **Step 5: Exercise the stopped-only commands**

```bash
./odc stop
./odc bot-goals set 50 20 30 50 && ./odc bot-goals show
./odc upgrade-world --same-version     # expected: counts printed, previous world archived
./odc up && sleep 60 && ./odc status   # healthy, same accounts and bots
```

- [ ] **Step 6: Record the results and clean up**

Write `docs/fork/verification/sub1-local.md` with a table: check, command, expected, actual, pass/fail,
for steps 1–5, plus the time `./odc stop` took with 30 bots. Then:
```bash
./odc down && docker volume rm offlinedaoc-data && cd ~ && rm -rf ~/Games/OfflineDAoC-Server-Test
```
(Keep `~/Games/OfflineDAoC-Central`: it is the owner's client for the real server; re-run `setup.sh`
with the real server address in Task 14.)

- [ ] **Step 7: Commit**

```bash
git add docs/fork/verification/sub1-local.md
git commit -m "docs(fork): local verification of the central server

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 14: Handoff and first release

**Files:**
- Create: `deploy/HANDOFF.md`
- Modify: `.github/workflows/server-image.yml` (add the `release-assets` job)

**Interfaces:**
- Consumes: Tasks 8–13.
- Produces: release `v0.34b-fork.1` with image `ghcr.io/lometur/offlinedaoc:v0.34b-fork.1` and assets `offlinedaoc-deploy-v0.34b-fork.1.tar.gz`, `offlinedaoc-client-v0.34b-fork.1.zip`.

- [ ] **Step 1: Create `deploy/HANDOFF.md`**

````markdown
# Handoff: deploy the OfflineDAoC central server

For the Claude session on the server machine. This deploys `lometur/OfflineDAoC` (an unofficial
fork of shadowofze/OfflineDAoC) next to the existing OpenDAoC server.

**Do not modify, stop, restart or recreate any OpenDAoC container, volume, network or compose
project.** Everything here uses the compose project `offlinedaoc`, the container
`offlinedaoc-server`, the volume `offlinedaoc-data` and ports 10301/tcp + 10401/udp.

## 1. Pre-checks (report each result to the owner)

```bash
docker --version && docker compose version          # Compose v2 required
free -g                                              # need >= 10 GB available next to OpenDAoC
ss -ltnu | grep -E ':(10301|10401)\b' || echo free   # both ports must be free
docker ps --format '{{.Names}}' | grep -i opendaoc   # note OpenDAoC's containers; leave them alone
```
If less than 10 GB of RAM is available, lower `OFFLINEDAOC_MEM_LIMIT` in step 2 and tell the owner.

## 2. Install

```bash
mkdir -p ~/offlinedaoc && cd ~/offlinedaoc
gh release download v0.34b-fork.1 --repo lometur/OfflineDAoC --pattern 'offlinedaoc-deploy-*.tar.gz'
tar xzf offlinedaoc-deploy-v0.34b-fork.1.tar.gz
cp .env.example .env
```
Edit `.env`: set `TZ` to the owner's timezone; keep `OFFLINEDAOC_EDITION=classic` unless the owner
says otherwise. Then pull: `docker compose -p offlinedaoc -f compose.yml --env-file .env pull`.

## 3. World data (choose one)

- **Shortcut (preferred):** the owner copies already-verified navmeshes from their PC:
  `rsync -a <owner-pc>:~/Games/OfflineDAoC/runtime/server/navmesh/ ~/offlinedaoc/seed-navmesh/`
  then: `./odc init --seed-navmesh ~/offlinedaoc/seed-navmesh` (verifies every file; downloads only the
  world database, ~30 MB). Afterwards `rm -rf ~/offlinedaoc/seed-navmesh`.
- **Download:** `./odc init` (about 600 MB from GitHub, verified, resumable).

## 4. Start and firewall

```bash
./odc up
sudo ufw allow from <LAN subnet, e.g. 192.168.1.0/24> to any port 10301 proto tcp
sudo ufw allow from <LAN subnet> to any port 10401 proto udp
```
(If the machine uses another firewall, add the equivalent LAN-only rules and tell the owner.)

## 5. Verify (report each)

```bash
sleep 90 && ./odc status                       # running (healthy), edition classic, navmesh True
docker exec offlinedaoc-server grep -c "Loading NavMesh successful" /data/logs/server.log   # 99
docker exec offlinedaoc-server grep "Server is now listening" /data/logs/server.log | tail -1   # 0.0.0.0:10301
docker ps --format '{{.Names}} {{.Status}}' | grep -i opendaoc   # OpenDAoC still up, unchanged
```
Then ask the owner to connect from their PC (`~/Games/OfflineDAoC-Central/play.sh` after running
`setup.sh --server <this machine's LAN IP>:10301 ...`) and confirm the login works.

## 6. Day-to-day

`./odc help` lists everything: status, logs, add-bots, accounts, bot goals, backups, restore,
new-world, upgrade-world, auto-accounts. Backups run daily into the volume (keep 7).
Before remote players join: `./odc auto-accounts off` and create their accounts with
`./odc account create <name> <password>`.

## Rollback

`./odc down` stops and removes the container; the world stays in the `offlinedaoc-data` volume.
To remove everything: `./odc down && docker volume rm offlinedaoc-data` (deletes the world).

## Upgrading to a new fork release

```bash
./odc backup && ./odc stop
sed -i 's/^OFFLINEDAOC_TAG=.*/OFFLINEDAOC_TAG=<new tag>/' .env
docker compose -p offlinedaoc -f compose.yml --env-file .env pull
./odc upgrade-world        # only if the release notes say the upstream version changed
./odc up
```
````

- [ ] **Step 2: Add the `release-assets` job to `.github/workflows/server-image.yml`**

Append:
```yaml
  release-assets:
    needs: test-build-publish
    if: startsWith(github.ref, 'refs/tags/')
    runs-on: ubuntu-24.04
    permissions:
      contents: write
    steps:
      - uses: actions/checkout@v4
      - name: Build bundles (no EA files)
        run: |
          tag="${GITHUB_REF_NAME}"
          mkdir -p dist/deploy "dist/client/offlinedaoc-client-$tag/windows"
          cp deploy/compose.yml deploy/.env.example deploy/odc deploy/HANDOFF.md deploy/upstream.lock dist/deploy/
          sed -i "s/^OFFLINEDAOC_TAG=.*/OFFLINEDAOC_TAG=$tag/" dist/deploy/.env.example
          tar czf "dist/offlinedaoc-deploy-$tag.tar.gz" -C dist/deploy .
          c="dist/client/offlinedaoc-client-$tag"
          cp client/README.md client/linux/setup.sh client/linux/play.sh.in tools/linux/odaoc_fetch.py deploy/upstream.lock "$c/"
          cp client/windows/connect-central.bat "$c/windows/"
          (cd dist/client && zip -qr "../offlinedaoc-client-$tag.zip" "offlinedaoc-client-$tag")
      - name: Create release
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: |
          gh release create "$GITHUB_REF_NAME" dist/offlinedaoc-deploy-*.tar.gz dist/offlinedaoc-client-*.zip \
            --title "$GITHUB_REF_NAME" \
            --notes "Unofficial fork of shadowofze/OfflineDAoC. Image: ghcr.io/lometur/offlinedaoc:$GITHUB_REF_NAME. Deploy: see HANDOFF.md in the deploy bundle. Players: see README.md in the client bundle."
```

- [ ] **Step 3: Commit, tag and watch the release build**

```bash
git add deploy/HANDOFF.md .github/workflows/server-image.yml
git commit -m "docs(deploy): server handoff and release assets

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git tag -a v0.34b-fork.1 -m "First central-server release (upstream v0.34b)"
git -c credential.helper= -c credential.helper='!gh auth git-credential' push origin main v0.34b-fork.1
gh run watch --repo lometur/OfflineDAoC --exit-status "$(gh run list --repo lometur/OfflineDAoC --workflow server-image --limit 1 --json databaseId --jq '.[0].databaseId')"
gh release view v0.34b-fork.1 --repo lometur/OfflineDAoC --json assets --jq '.assets[].name'
```
Expected: run green; the release lists `offlinedaoc-deploy-v0.34b-fork.1.tar.gz` and
`offlinedaoc-client-v0.34b-fork.1.zip`.

- [ ] **Step 4: Make the image public**

Ask the owner to open GitHub → their profile → **Packages** → `offlinedaoc` → Package settings →
Change visibility → **Public** (GHCR packages start private, and the server pulls without logging in).
Verify: `docker pull ghcr.io/lometur/offlinedaoc:v0.34b-fork.1` succeeds after `docker logout ghcr.io`.

- [ ] **Step 5: Hand off**

Tell the owner to give the Claude session on the server machine this instruction:
"Follow https://github.com/lometur/OfflineDAoC/blob/main/deploy/HANDOFF.md exactly, reporting the
result of every check, and never touch the OpenDAoC containers." Done criteria (spec §10): two LAN
clients in the world at once, a restart keeps progress, backup and restore work, and OpenDAoC is
untouched. The two-client check needs a second LAN machine (or a friend's PC) set up with
`client/README.md`.

- [ ] **Step 6: Record the server results**

Add a "Server deployment" section to `docs/fork/verification/sub1-local.md` with each check the
server's Claude reported (pre-checks, navmesh count, listening line, OpenDAoC unchanged) and the
two-client test, then:

```bash
git add docs/fork/verification/sub1-local.md
git commit -m "docs(fork): record the server deployment checks

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git -c credential.helper= -c credential.helper='!gh auth git-credential' push origin main
```
