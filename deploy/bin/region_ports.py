#!/usr/bin/env python3
"""Make the world tell clients the server's real UDP port.

The server sends each region's Regions.Port to clients as the UDP port to use (it swaps a local
region IP for the server's address, but not the port). Upstream's world ships 10400 everywhere,
which on a shared host is OpenDAoC's port. Run at every start, before the server opens the
database; it only writes when a value differs, so new-world, upgrade-world and restore stay right.
"""
import argparse
import sqlite3
import sys


class RegionPortError(Exception):
    pass


def align(db, port):
    """Set Regions.Port to port where it differs; returns the number of regions changed."""
    conn = sqlite3.connect(db, timeout=30)
    try:
        if not conn.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='Regions'").fetchone():
            raise RegionPortError(f"{db} has no Regions table; is this a world database?")
        with conn:
            return conn.execute("UPDATE Regions SET Port = ? WHERE Port IS NOT ?", (port, port)).rowcount
    finally:
        conn.close()


def main(argv=None):
    ap = argparse.ArgumentParser(description="Set every region's UDP port in the world database.")
    ap.add_argument("--db", required=True)
    ap.add_argument("--port", required=True, type=int)
    a = ap.parse_args(argv)
    if not 1 <= a.port <= 65535:
        ap.error("--port must be between 1 and 65535")
    try:
        n = align(a.db, a.port)
    except RegionPortError as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 1
    if n:
        print(f"Set {n} regions to UDP port {a.port}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
