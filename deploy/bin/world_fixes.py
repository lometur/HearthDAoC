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
4. Upstream's 0.35 world has two Mob rows at the same spot for two Albion townspeople, Ley Manton and Tria
   Ellowis: the merchant and a plain GameNPC copy of it, so players see two identical NPCs. Remove the plain
   copy (archived first in the fork table fork_removed_mobs), only where the merchant is still at its spot.
5. The classic battlegrounds (levels 15 to 35, as in the Shrouded Isles era): battlegrounds.py, once per
   world (the marker classic-battlegrounds-v2 in fork_world_fixes). It runs last, under its own
   savepoint: if it fails, it undoes only itself and prints why, and the fixes above are still saved.
6. The epic chains (sub-project 4): epic_chains.py, once per world (the marker epic-chains-v1), after the
   battlegrounds and under its own savepoint like them: upstream's Guild of Shadows chain in order and complete, the
   real level-50 "Lord of Deceit", and every guild line's steps in order.
7. The Guild of Shadows chain's dialogue (levels 7 to 50): quest_dialogue.py, right after the epic chains, under its
   own savepoint like them. It has no marker: at every start it rewrites the texts of a quest that still hold
   upstream's text or an earlier version of its own, and leaves the owner's own edits alone.

All of them only apply when needed and leave anything the owner set themselves alone.
"""
import argparse
import datetime
import re
import sqlite3
import sys

import battlegrounds
import epic_chains
import quest_dialogue

DISCIPLE, SARACEN, INCONNU = 20, 4, 13

# Step 4: the townspeople whose plain copy stands on their merchant, and the archive of the removed rows.
DUPLICATE_TOWNSPEOPLE = ("Ley Manton", "Tria Ellowis")
DUPLICATES_FIX_ID = "duplicate-townspeople"
ARCHIVE_TABLE = battlegrounds.ARCHIVE_TABLE

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


def remove_duplicate_townspeople(conn, now):
    """Remove the plain GameNPC copy of Ley Manton and Tria Ellowis where a GameMerchant of the same name stands at
    exactly its spot (Name, Region, X, Y, Z), keeping the copy in the archive table. Returns the names removed."""
    have = {name for _, name, *_ in conn.execute('PRAGMA table_info("Mob")')}
    if not {"Mob_ID", "Name", "ClassType", "ItemsListTemplateID", "Region", "X", "Y", "Z"} <= have:
        return []  # no Mob table, or not upstream's
    marks = ", ".join("?" * len(DUPLICATE_TOWNSPEOPLE))
    where = (f"Name IN ({marks}) AND ClassType='DOL.GS.GameNPC' AND COALESCE(ItemsListTemplateID, '') = '' "
             "AND EXISTS (SELECT 1 FROM Mob m WHERE m.ClassType='DOL.GS.GameMerchant' AND m.Name=Mob.Name "
             "AND m.Region=Mob.Region AND m.X=Mob.X AND m.Y=Mob.Y AND m.Z=Mob.Z)")
    names = [r[0] for r in conn.execute(f"SELECT Name FROM Mob WHERE {where} ORDER BY Name, Mob_ID", DUPLICATE_TOWNSPEOPLE)]
    if not names:
        return []
    # The same archive as battlegrounds.py's (the Mob columns, FixId, RemovedUtc); it may not exist yet.
    columns = ", ".join(f'"{name}" {declared}' for _, name, declared, *_ in conn.execute('PRAGMA table_info("Mob")'))
    conn.execute(f"CREATE TABLE IF NOT EXISTS {ARCHIVE_TABLE} ({columns}, FixId TEXT NOT NULL, RemovedUtc TEXT NOT NULL)")
    archive = {name for _, name, *_ in conn.execute(f'PRAGMA table_info("{ARCHIVE_TABLE}")')}
    cols = ", ".join(f'"{name}"' for _, name, *_ in conn.execute('PRAGMA table_info("Mob")') if name in archive)
    conn.execute(f"INSERT INTO {ARCHIVE_TABLE} ({cols}, FixId, RemovedUtc) SELECT {cols}, ?, ? FROM Mob WHERE {where}",
                 (DUPLICATES_FIX_ID, now, *DUPLICATE_TOWNSPEOPLE))
    conn.execute(f"DELETE FROM Mob WHERE {where}", DUPLICATE_TOWNSPEOPLE)
    return sorted(set(names))


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
            removed = remove_duplicate_townspeople(conn, _now())
            if removed:
                changes.append("Duplicate townspeople removed, archived in %s: %s" % (ARCHIVE_TABLE, ", ".join(removed)))
            changes.extend(battlegrounds.apply(conn, _now()))
            changes.extend(epic_chains.apply(conn, _now()))
            changes.extend(quest_dialogue.apply(conn, _now()))
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
