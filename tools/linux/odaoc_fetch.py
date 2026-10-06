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
import http.client
import json
import os
import struct
import sys
import tempfile
import time
import urllib.error
import urllib.request
import zlib

USER_AGENT = "hearthdaoc-fork-fetch/1"


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
            except (urllib.error.URLError, OSError, http.client.HTTPException) as e:  # incl. IncompleteRead
                last = f"{type(e).__name__}: {e}"
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
