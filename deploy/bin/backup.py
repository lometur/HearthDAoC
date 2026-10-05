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


KEEP_OTHER = 3  # per label: pre-bots, pre-restore, pre-upgrade, ...


def db_path(data):
    return os.path.join(data, "world", "opendaoc.sqlite3.db")


def backups_dir(data):
    return os.path.join(data, "backups")


def create(data, keep=None, label="backup"):
    os.makedirs(backups_dir(data), exist_ok=True)
    # UTC, so names sort chronologically whichever container (with or without TZ) made them.
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d-%H%M%S-%f")
    dest = os.path.join(backups_dir(data), f"world-{stamp}-{label}.db")
    tmp = dest + ".part"
    try:
        src = sqlite3.connect(f"file:{db_path(data)}?mode=ro", uri=True, timeout=30)
        try:
            dst = sqlite3.connect(tmp)
            try:
                src.backup(dst)
                # The copy inherits the server's WAL mode; make it a standalone file with no -wal/-shm.
                dst.execute("PRAGMA journal_mode=DELETE")
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
        for leftover in (tmp, tmp + "-wal", tmp + "-shm"):
            if os.path.exists(leftover):
                os.remove(leftover)
    rotate(data, keep)
    return dest


def _label(path):
    # world-YYYYmmdd-HHMMSS-ffffff-<label>.db
    parts = os.path.basename(path)[:-len(".db")].split("-", 4)
    return parts[4] if len(parts) == 5 else ""


def rotate(data, keep=None):
    """Keep the newest `keep` daily backups and the newest KEEP_OTHER of every other label."""
    files = sorted(glob.glob(os.path.join(backups_dir(data), "world-*.db")))
    daily = [f for f in files if _label(f) == "backup"]
    for old in (daily[:-keep] if keep and keep > 0 else []):
        os.remove(old)
    by_label = {}
    for f in files:
        if _label(f) != "backup":
            by_label.setdefault(_label(f), []).append(f)
    for paths in by_label.values():
        for old in paths[:-KEEP_OTHER]:
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
