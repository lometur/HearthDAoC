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
   It also has two fighter trainers called Master Graent in Prydwen Keep, 770 units apart: remove the one among
   the merchants outside (archived the same way), only while that row is still upstream's, at its spot, and the
   one in the keep is still in the world. (The realm-0 "commander" its bestiary import put on the friendly Albion
   Commander's spot by Camelot's north gate went the same way until upstream 0.36 removed it itself.)
5. The classic battlegrounds (levels 15 to 35, as in the Shrouded Isles era): battlegrounds.py, once per
   world (the marker classic-battlegrounds-v2 in fork_world_fixes). It runs last, under its own
   savepoint: if it fails, it undoes only itself and prints why, and the fixes above are still saved.
6. The epic chains (sub-project 4): epic_chains.py, once per world (the marker epic-chains-v1), after the
   battlegrounds and under its own savepoint like them: upstream's Guild of Shadows chain in order and complete, the
   real level-50 "Lord of Deceit", and every guild line's steps in order.
7. Quest texts: the Guild of Shadows chain's dialogue (levels 7 to 50), and the classic quests' period lines, names
   and class words (the line that offers a quest's first keyword, the names and words upstream wrote as <Player>, and
   the guides' authors' class, which upstream kept, as <Class>), without the guides' leftovers (their placeholders
   for the player's name and race, as <Player> and <Race>; no scraped web page or editors' notes):
   quest_dialogue.py, right after the epic chains, under its own savepoint like them. It has no marker: at every
   start it rewrites the texts of a quest that still hold upstream's text or an earlier version of its own, and
   leaves the owner's own edits alone.
8. The quest camps upstream archived as post-legacy spawns that the period's walkthroughs describe (Delfina's rangers,
   the level-45 courier's isolationist camp, the level-48 Ellyll Seer's village, Lieutenant Grimarth):
   quest_camps.py, from quest_camps.json, under its own savepoint like the steps before it. Each camp is copied once
   per world (marker quest-camp:<key> in fork_world_fixes), so rows the owner removes later stay removed. The same file
   adds NPCs the period had (Lieutenant Cian, made like Lieutenant Brude; Mulgrut Maggot's escort), archives rows it
   didn't (the generated Bline, the standing Martley) into fork_removed_mobs, and adds the walks the period gave quest
   monsters (paths: Mulgrut Maggot's, between the Camelot Hills cemetery and the tall stone).
9. Period corrections to monsters, items, quests and zone points (levels, spots, prices, rewards; level 11's thieves
   Frund and Agisthil first, then the ten classic quests that handed their reward twice, then the Guild of Shadows
   epic's givers, monsters and item icons, then the three Shrouded Isles entrances crossed at their 1.65 portals on the
   Avalon Marsh pier, in Gotar and in Shannon Estuary): mob_fixes.py, from mob_fixes.json, after the camps (it moves
   rows of them), under its own savepoint like the steps before it. No marker: at every start it changes a row only
   while it still holds the values upstream shipped, and names the rows that hold neither those nor the file's.
10. Points along the bridge decks of the horse routes (owner 2026-10-13: horses swam under the Magical Bridge arches
   north of Camelot and under PKB, where a route crosses on one bank-to-bank segment): route_fixes.py, from
   route_fixes.json, under its own savepoint like the steps before it. No marker: at every start it adds a crossing's
   points only while its two points still follow each other at upstream's values, and names the crossings whose route
   changed.
11. The starting locations that were wrong for the period (owner's decisions 2026-10-13: race and base class fixed the
   town, beside the base-class trainer; the Shrouded Isles base classes, and the Troll Mystic, which can only become a
   Bonedancer, started only in their SI city before patch 1.72): the Briton Mage, three Seers, the Elf Stalker, the
   Briton Disciple, Troll Mystic and Celt and Firbolg Foresters, and four final-class rows the bots start from (and a
   promoted player's broken-bind repair): start_towns.py, from start_towns.json, under its own savepoint
   like the steps before it. No marker: at every start it moves a row only while it is at upstream's spot, and names
   the rows at neither that spot nor the period's. New characters only; existing ones keep their location and bind.
12. The leveling spawns an earlier `hdc spawns restore` brought back next to a quest's kill target named like them but
   for case (level 20's "arawnite messenger" beside the "Arawnite Messenger"): spawns.remove_quest_twins, last, under
   its own savepoint. Only rows listed in fork_restored_mobs; they stay in upstream's archive, as `hdc spawns undo`
   leaves them, and the restore now leaves them out.

All of them only apply when needed and leave anything the owner set themselves alone.
"""
import argparse
import datetime
import re
import sqlite3
import sys

import battlegrounds
import epic_chains
import mob_fixes
import quest_camps
import quest_dialogue
import route_fixes
import spawns
import start_towns

DISCIPLE, SARACEN, INCONNU = 20, 4, 13

# Step 4: the townspeople whose plain copy stands on their merchant, and the archive of the removed rows.
DUPLICATE_TOWNSPEOPLE = ("Ley Manton", "Tria Ellowis")
# Other duplicates, each one row: (Mob_ID, Name, ClassType, Region, X, Y, Z) as upstream ships it, and the Mob_ID
# of the copy that stays. The row goes only while it holds those values and the copy that stays has its name (in any
# capitals) in its region.
DUPLICATE_ROWS = [
    # Prydwen Keep (Camelot Hills): the fighter trainer among the merchants just outside goes, the one in the
    # keep's middle structure, by the healer and the town crier, stays (#101, the owner met both 2026-10-11).
    (("1e0dc1a7-6b01-4a38-845d-f8ef417a09e1", "Master Graent", "DOL.GS.Trainer.FighterTrainer", 1, 574886, 530169,
      2896), "deecddf1-2306-4194-a3e8-bab803d450fa"),
    # Until the 0.37b sync this also listed the hostile "commander" by Camelot's north gate (Mob_ID
    # 4609f55b-83dd-5958-99dc-b24f969cabda): upstream 0.36 removed it ("Removed the hostile copies of friendly NPCs
    # that 0.35's classic bestiary spawns added", its CHANGELOG).
]
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
    exactly its spot (Name, Region, X, Y, Z), and each DUPLICATE_ROWS row that is still upstream's while its kept
    copy is in the world, keeping the rows in the archive table. Returns the names removed."""
    have = {name for _, name, *_ in conn.execute('PRAGMA table_info("Mob")')}
    if not {"Mob_ID", "Name", "ClassType", "ItemsListTemplateID", "Region", "X", "Y", "Z"} <= have:
        return []  # no Mob table, or not upstream's
    marks = ", ".join("?" * len(DUPLICATE_TOWNSPEOPLE))
    where = (f"(Name IN ({marks}) AND ClassType='DOL.GS.GameNPC' AND COALESCE(ItemsListTemplateID, '') = '' "
             "AND EXISTS (SELECT 1 FROM Mob m WHERE m.ClassType='DOL.GS.GameMerchant' AND m.Name=Mob.Name "
             "AND m.Region=Mob.Region AND m.X=Mob.X AND m.Y=Mob.Y AND m.Z=Mob.Z))")
    args = list(DUPLICATE_TOWNSPEOPLE)
    for row, kept in DUPLICATE_ROWS:
        where += (" OR (Mob_ID=? AND Name=? AND ClassType=? AND Region=? AND X=? AND Y=? AND Z=? "
                  "AND EXISTS (SELECT 1 FROM Mob k WHERE k.Mob_ID=? AND lower(k.Name)=lower(Mob.Name) "
                  "AND k.Region=Mob.Region))")
        args += [*row, kept]
    names = [r[0] for r in conn.execute(f"SELECT Name FROM Mob WHERE {where} ORDER BY Name, Mob_ID", args)]
    if not names:
        return []
    # The same archive as battlegrounds.py's (the Mob columns, FixId, RemovedUtc); it may not exist yet.
    columns = ", ".join(f'"{name}" {declared}' for _, name, declared, *_ in conn.execute('PRAGMA table_info("Mob")'))
    conn.execute(f"CREATE TABLE IF NOT EXISTS {ARCHIVE_TABLE} ({columns}, FixId TEXT NOT NULL, RemovedUtc TEXT NOT NULL)")
    archive = {name for _, name, *_ in conn.execute(f'PRAGMA table_info("{ARCHIVE_TABLE}")')}
    cols = ", ".join(f'"{name}"' for _, name, *_ in conn.execute('PRAGMA table_info("Mob")') if name in archive)
    conn.execute(f"INSERT INTO {ARCHIVE_TABLE} ({cols}, FixId, RemovedUtc) SELECT {cols}, ?, ? FROM Mob WHERE {where}",
                 (DUPLICATES_FIX_ID, now, *args))
    conn.execute(f"DELETE FROM Mob WHERE {where}", args)
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
            changes.extend(quest_camps.apply(conn, _now()))
            changes.extend(mob_fixes.apply(conn, _now()))
            changes.extend(route_fixes.apply(conn, _now()))
            changes.extend(start_towns.apply(conn, _now()))
            changes.extend(spawns.remove_quest_twins(conn))
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
