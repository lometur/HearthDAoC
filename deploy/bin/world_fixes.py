#!/usr/bin/env python3
"""Fixes to upstream's classic world data, applied before every start (only where still needed).

1. Disciple (class 20, the Necromancer's base class) is in upstream's disabled_classes, although
   Necromancer is a Shrouded Isles class and the setup tool's own label says it allows Classic and
   Shrouded Isles classes. Stock clients still create Necromancers (the server checks the final class,
   then converts it), but a client that sends the base class is refused. Remove 20 from the list.
2. Saracen Disciples have no starting location (only Briton and Inconnu do), so a Saracen Necromancer
   has nowhere to enter the world. Add one, at the Inconnu Disciples' spot in the Shrouded Isles.
3. The welcome messages players see (motd, starting_msg) name Offline DAoC and describe a world to play
   alone. Replace upstream's texts with HearthDAoC's.
4. The classic battlegrounds (levels 15 to 35, as in the Shrouded Isles era): battlegrounds.py, once per
   world (the marker classic-battlegrounds-v2 in fork_world_fixes). It runs last, under its own
   savepoint: if it fails, it undoes only itself and prints why, and the fixes above are still saved.

All of them only apply when needed and leave anything the owner set themselves alone.
"""
import argparse
import datetime
import re
import sqlite3
import sys

import battlegrounds

DISCIPLE, SARACEN, INCONNU = 20, 4, 13

# Upstream's texts (its ServerProperties.cs defaults, as stored in its worlds), and what replaces them.
UPSTREAM_WELCOME = {
    "motd": "Welcome back to Offline DAoC. Adventure alone or alongside autonomous player bots across the three realms.",
    "starting_msg": "Welcome to Offline DAoC, a private Classic and Shrouded Isles world built to play alone or with "
                    "player bot companions.",
}
HEARTHDAOC_WELCOME = {
    "motd": "Welcome back to HearthDAoC. Adventure with your friends and autonomous player bots across the three realms.",
    "starting_msg": "Welcome to HearthDAoC, a private Classic and Shrouded Isles world to share with your friends, "
                    "with player bot companions at your side.",
}


def without_classes(value, ids):
    """Remove class ids from a disabled_classes value ('20;33;58-62'), splitting ranges."""
    out = []
    for token in re.split(r"[;,]", value):
        token = token.strip()
        if not token:
            continue
        m = re.fullmatch(r"(\d+)-(\d+)", token)
        lo, hi = (int(m.group(1)), int(m.group(2))) if m else (int(token), int(token)) if token.isdigit() else (None, None)
        if lo is None:
            out.append(token)
            continue
        start = None
        for n in range(lo, hi + 2):
            if n <= hi and n not in ids:
                start = n if start is None else start
            elif start is not None:
                end = n - 1
                out.append(str(start) if start == end else f"{start}-{end}")
                start = None
    return ";".join(out)


def apply(db):
    """Apply the fixes that are still needed; returns a description of each change."""
    changes = []
    conn = sqlite3.connect(db, timeout=30)
    try:
        with conn:
            row = conn.execute("SELECT Value FROM ServerProperty WHERE `Key`='disabled_classes'").fetchone()
            if row is not None:
                fixed = without_classes(row[0], {DISCIPLE})
                if fixed != row[0]:
                    conn.execute("UPDATE ServerProperty SET Value=?, LastTimeRowUpdated=? WHERE `Key`='disabled_classes'",
                                 (fixed, _now()))
                    changes.append(f"Disciple (Necromancer's base class) enabled: disabled_classes {row[0]} -> {fixed}")
            has_saracen = conn.execute("SELECT 1 FROM StartupLocation WHERE ClassID=? AND RaceID=?", (DISCIPLE, SARACEN)).fetchone()
            inconnu = conn.execute("SELECT XPos, YPos, ZPos, Heading, Region, MinVersion, RealmID, ClientRegionID FROM StartupLocation "
                                   "WHERE ClassID=? AND RaceID=? ORDER BY StartupLoc_ID LIMIT 1", (DISCIPLE, INCONNU)).fetchone()
            if not has_saracen and inconnu:
                conn.execute("INSERT INTO StartupLocation (XPos, YPos, ZPos, Heading, Region, MinVersion, RealmID, RaceID, ClassID, "
                             "ClientRegionID, LastTimeRowUpdated) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                             (*inconnu[:6], inconnu[6], SARACEN, DISCIPLE, inconnu[7], _now()))
                changes.append("Saracen Disciples get a starting location (with the Inconnu Disciples, region %d)" % inconnu[4])
            renamed = []
            for key, text in UPSTREAM_WELCOME.items():
                cur = conn.execute("UPDATE ServerProperty SET Value=?, LastTimeRowUpdated=? WHERE `Key`=? AND Value=?",
                                   (HEARTHDAOC_WELCOME[key], _now(), key, text))
                if cur.rowcount:
                    renamed.append(key)
            if renamed:
                changes.append("Welcome messages now name HearthDAoC (%s)" % ", ".join(renamed))
            changes.extend(battlegrounds.apply(conn, _now()))
    finally:
        conn.close()
    return changes


def _now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S")


def main(argv=None):
    ap = argparse.ArgumentParser(description="Apply HearthDAoC's fixes to upstream's world data.")
    ap.add_argument("--db", required=True)
    a = ap.parse_args(argv)
    for change in apply(a.db):
        print(change)
    return 0


if __name__ == "__main__":
    sys.exit(main())
