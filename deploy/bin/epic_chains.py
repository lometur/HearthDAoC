#!/usr/bin/env python3
"""The epic chains (sub-project 4) in the world data: upstream's classic epic quests, in order and complete.

Spec: docs/fork/specs/2026-10-09-epic-chains-design.md, section 3.4. world_fixes.py calls apply() inside its
transaction, after battlegrounds.apply(). The fix runs once per world: it records the marker epic-chains-v1 in the
fork table fork_world_fixes, and a world with the marker is left alone, so changes the owner makes later stay. Every
statement runs under one savepoint; if a step fails it is rolled back (no change, no marker, so the next start tries
again) and apply() returns only the "not applied" line, while world_fixes.py still commits its own fixes.

Each step changes a row only while it still holds upstream's 0.35b value, and inserts a row only where none with its
key exists, so a later upstream world that fixed something itself keeps its own fix, and the counts show only what
this fix did. The Guild of Shadows data (quest IDs, links, XP and coin, texts, items, the level-50 quests) is in
epic_chains_data.json, beside this file.
1. The Guild of Shadows links: QuestDependency of its 60 chain rows by quest ID (DataQuest's "#a/b": one of them is
   finished; "!#a/b": closed while one of them is active or finished).
2. Every other line: a classic quest's name dependency that is its own name, or that names quests at more than one
   level below it for its classes, is pinned to the IDs of those quests at the highest such level.
3. The other version of the Supply Run (Lady Aelawen's) is closed: it needs itself finished, so it is offered to no
   one, and a character already on it can finish it (DataQuest reads dependencies only when it offers a quest).
4. The chain's XP and coin, in the last stage's entry (the 11 SI also pays coin at stage 3).
5. Necromancer 11: one reward for each version.
6. Texts: the Reaver's level-40 list without the two weapons that don't exist; the level-30 speech's source tags.
7. Items: the missing rewards and level-40 weapons, inserted where missing; fixes to existing rows (the level-50
   armour, upstream's broken rewards, every Guild of Shadows reward locked to its class).
8. The five level-50 quests ("Lord of Deceit", given by Captain Rhodri after the class's 48), and Lord Elidyn's camp
   copied back from upstream's archive (offline_classic165_removed_mobs), Mob_IDs kept.
9. Old Shadows_50 progress: a finished one becomes the character's finished level-50 step (no second armour set),
   any other is removed; epic vests in inventories with no charges get the template's.
"""
import datetime
import json
import os
import re
import uuid

FIX_ID = "epic-chains-v1"
MARKER_TABLE = "fork_world_fixes"
SAVEPOINT = "epic_chains"
NEEDED_TABLES = ("DataQuest", "ItemTemplate", "Mob", "offline_classic165_removed_mobs", "Quest",
                 "CharacterXDataQuest", "DOLCharacters", "Inventory")
NOT_APPLIED = "Epic chains: not applied ({}); the quests stay as upstream ships them"
CLASSIC = "DOL.GS.Quests.ClassicQuestStep"
ARCHIVE = "offline_classic165_removed_mobs"
DATA_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "epic_chains_data.json")
TEXT_COLUMNS = ("StepText", "SourceText", "TargetText", "Description", "FinishText")


def load_data(path=DATA_FILE):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def apply(conn, now=None, data=None):
    """Apply the epic chains once per world; returns the summary line, the "not applied" line, or nothing.

    Inside an open transaction (world_fixes.py's), nothing here commits; on a connection with none open, the
    savepoint is the transaction, and its release commits it."""
    tables = {name.lower() for (name,) in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    if any(table.lower() not in tables for table in NEEDED_TABLES):
        return []
    if MARKER_TABLE in tables and conn.execute(f"SELECT 1 FROM {MARKER_TABLE} WHERE FixId=?", (FIX_ID,)).fetchone():
        return []
    now = now or _now()
    data = data if data is not None else load_data()
    conn.execute(f"SAVEPOINT {SAVEPOINT}")
    try:
        conn.execute(f"CREATE TABLE IF NOT EXISTS {MARKER_TABLE} (FixId TEXT PRIMARY KEY, AppliedUtc TEXT NOT NULL)")
        counts = {}
        for step in STEPS:
            counts.update(step(conn, now, data))
        conn.execute(f"INSERT INTO {MARKER_TABLE} (FixId, AppliedUtc) VALUES (?, ?)", (FIX_ID, now))
    except Exception as e:  # any failure: undo the whole fix, and let the other fixes and the start go on
        conn.execute(f"ROLLBACK TO {SAVEPOINT}")
        conn.execute(f"RELEASE {SAVEPOINT}")
        return [NOT_APPLIED.format(str(e) or type(e).__name__)]
    conn.execute(f"RELEASE {SAVEPOINT}")
    return [summary(counts)]


def summary(counts):
    """The start log's line. A count is what this fix changed (0 where upstream already had it)."""
    c = lambda key: counts.get(key, 0)  # noqa: E731
    return (f"Epic chains: Guild of Shadows {c('links')} links, {c('pay')} XP and coin, {c('closed')} Supply Runs "
            f"closed, {c('rewards')} rewards and {c('texts')} texts fixed; {c('pinned')} other links; "
            f"{c('items')} items added, {c('fixes')} item fixes; {c('level50')} level-50 quests, "
            f"Lord Elidyn's camp {c('camp')} restored; Shadows_50: {c('carried')} finished carried, "
            f"{c('removed')} removed, {c('vests')} epic vests recharged")


def _classes(allowed):
    """The class IDs of an AllowedClasses value ("9", "22|31", "1;6"); empty means every class."""
    return {int(part) for part in re.split(r"[|;,]", allowed or "") if part.strip().isdigit()}


def _shares(mine, theirs):
    return not mine or not theirs or bool(mine & theirs)


def _entry(steps, link, col):
    """A link ("7/7closed/7si", "!11si") as the class's QuestDependency entry ("#21500/21324/20478", "!#20469")."""
    closes = link.startswith("!")
    ids = [steps[key]["ids"][col] for key in link.lstrip("!").split("/")]
    return ("!#" if closes else "#") + "/".join(str(i) for i in ids if i is not None)


def _gos_links(conn, now, data):
    steps, changed = data["steps"], 0
    for step in steps.values():
        if step.get("new") or "links" not in step:
            continue
        for col, qid in enumerate(step["ids"]):
            if qid is None:
                continue
            value = "|".join(_entry(steps, link, col) for link in step["links"])
            changed += conn.execute(
                "UPDATE DataQuest SET QuestDependency=?, LastTimeRowUpdated=? WHERE ID=? AND IFNULL(QuestDependency,'')=?",
                (value, now, qid, step["upstream"]["QuestDependency"])).rowcount
    return {"links": changed}


def _pin_names(conn, now, data):
    gos = {qid for step in data["steps"].values() for qid in step["ids"] if qid is not None}
    quests = [r for r in conn.execute("SELECT ID, Name, MinLevel, AllowedClasses, IFNULL(QuestDependency,''), ClassType "
                                      "FROM DataQuest") if CLASSIC in (r[5] or "")]
    by_name = {}
    for quest in quests:
        by_name.setdefault(quest[1].lower(), []).append(quest)
    changed = 0
    for qid, name, level, allowed, dependency, _class_type in quests:
        entries = [e for e in dependency.split("|") if e]
        if qid in gos or not entries or any(e.strip().startswith(("#", "!#")) for e in entries):
            continue
        mine, pinned, any_pinned = _classes(allowed), [], False
        for entry in entries:
            before = [q for q in by_name.get(entry.lower(), [])
                      if q[0] != qid and q[2] < level and _shares(mine, _classes(q[3]))]
            levels = {q[2] for q in before}
            if before and (entry.lower() == name.lower() or len(levels) > 1):
                top = max(levels)
                pinned.append("#" + "/".join(str(q[0]) for q in sorted(before) if q[2] == top))
                any_pinned = True
            else:
                pinned.append(entry)
        if any_pinned:
            changed += conn.execute(
                "UPDATE DataQuest SET QuestDependency=?, LastTimeRowUpdated=? WHERE ID=? AND IFNULL(QuestDependency,'')=?",
                ("|".join(pinned), now, qid, dependency)).rowcount
    return {"pinned": changed}


def _close_supply_runs(conn, now, data):
    closed = data["steps"]["7closed"]
    changed = 0
    for qid in closed["ids"]:
        if qid is not None:
            changed += conn.execute(
                "UPDATE DataQuest SET QuestDependency=?, LastTimeRowUpdated=? WHERE ID=? AND IFNULL(QuestDependency,'')=?",
                (f"#{qid}", now, qid, closed["upstream"]["QuestDependency"])).rowcount
    return {"closed": changed}


def pay_lists(stages, step):
    """RewardXP and RewardMoney of a step with <stages> stages: its amounts in the last stage's entry, plus any
    "money_at" coin at its stage."""
    xp, money = [0] * stages, [0] * stages
    xp[-1], money[-1] = step["xp"], step["money"]
    for stage, amount in step.get("money_at", {}).items():
        money[int(stage) - 1] = amount
    return "|".join(map(str, xp)), "|".join(map(str, money))


def _pay(conn, now, data):
    changed = 0
    for step in data["steps"].values():
        if step.get("new") or "xp" not in step:
            continue
        upstream = step["upstream"]
        xp, money = pay_lists(len(upstream["RewardXP"].split("|")), step)
        for qid in step["ids"]:
            if qid is not None:
                changed += conn.execute(
                    "UPDATE DataQuest SET RewardXP=?, RewardMoney=?, LastTimeRowUpdated=? "
                    "WHERE ID=? AND IFNULL(RewardXP,'')=? AND IFNULL(RewardMoney,'')=?",
                    (xp, money, now, qid, upstream["RewardXP"], upstream["RewardMoney"])).rowcount
    return {"pay": changed}


def _rewards(conn, now, data):
    changed = 0
    for fix in data["rewards"]:
        changed += conn.execute(
            "UPDATE DataQuest SET FinalRewardItemTemplates=?, LastTimeRowUpdated=? WHERE ID=? "
            "AND IFNULL(FinalRewardItemTemplates,'')=?", (fix["value"], now, fix["id"], fix["upstream"])).rowcount
    return {"rewards": changed}


def _texts(conn, now, data):
    changed = set()
    for fix in data["texts"]:
        column = fix["column"]
        if column not in TEXT_COLUMNS:
            raise ValueError(f"unknown DataQuest text column {column}")
        for qid in fix["ids"]:
            row = conn.execute(f"SELECT {column} FROM DataQuest WHERE ID=?", (qid,)).fetchone()
            if row is None or row[0] is None or not all(old in row[0] for old, _new in fix["replace"]):
                continue
            text = row[0]
            for old, new in fix["replace"]:
                text = text.replace(old, new)
            conn.execute(f"UPDATE DataQuest SET {column}=?, LastTimeRowUpdated=? WHERE ID=?", (text, now, qid))
            changed.add(qid)
    return {"texts": len(changed)}


def _item_columns(conn, names):
    known = {row[1] for row in conn.execute('PRAGMA table_info("ItemTemplate")')}
    unknown = set(names) - known
    if unknown:
        raise ValueError(f"not ItemTemplate columns: {', '.join(sorted(unknown))}")
    return names


def _items(conn, now, data):
    added = 0
    for item in data["items"]:
        if conn.execute("SELECT 1 FROM ItemTemplate WHERE Id_nb=?", (item["Id_nb"],)).fetchone():
            continue
        # A stable ItemTemplate_ID, so the same item has the same row ID in every world.
        row = dict(item, ItemTemplate_ID=str(uuid.uuid5(uuid.NAMESPACE_URL, "hearthdaoc:item:" + item["Id_nb"])),
                   LastTimeRowUpdated=now)
        columns = ", ".join(f'"{c}"' for c in _item_columns(conn, list(row)))
        conn.execute(f"INSERT INTO ItemTemplate ({columns}) VALUES ({', '.join('?' * len(row))})", tuple(row.values()))
        added += 1
    fixed = 0
    for fix in data["item_fixes"]:
        expect = _item_columns(conn, list(fix["expect"]))
        current = conn.execute(f"SELECT {', '.join(expect)} FROM ItemTemplate WHERE Id_nb=?", (fix["Id_nb"],)).fetchone()
        if current is None or [str(v) for v in current] != [str(fix["expect"][c]) for c in expect]:
            continue
        sets = ", ".join(f"{c}=?" for c in _item_columns(conn, list(fix["set"])))
        conn.execute(f"UPDATE ItemTemplate SET {sets}, LastTimeRowUpdated=? WHERE Id_nb=?",
                     (*fix["set"].values(), now, fix["Id_nb"]))
        fixed += 1
    return {"items": added, "fixes": fixed}


def _level50(conn, now, data):
    quest, steps = data["level50"], data["steps"]
    step = steps["50"]
    stages = len(quest["steps"])
    xp, money = pay_lists(stages, step)
    empty = "|".join([""] * stages)
    added = 0
    for col, (class_name, class_id) in enumerate(data["classes"].items()):
        qid = step["ids"][col]
        if conn.execute("SELECT 1 FROM DataQuest WHERE ID=?", (qid,)).fetchone():
            continue
        armour = "|".join(f"{class_name}Epic{piece}" for piece in quest["armour"])
        dependency = "|".join(_entry(steps, link, col) for link in step["links"])
        conn.execute(
            "INSERT INTO DataQuest (ID, Name, StartType, StartName, StartRegionID, AcceptText, Description, SourceName, "
            "SourceText, StepType, StepText, StepItemTemplates, AdvanceText, TargetName, TargetText, CollectItemTemplate, "
            "MaxCount, MinLevel, MaxLevel, RewardMoney, RewardXP, RewardCLXP, RewardRP, RewardBP, "
            "OptionalRewardItemTemplates, FinalRewardItemTemplates, FinishText, QuestDependency, AllowedClasses, "
            "ClassType, LastTimeRowUpdated) "
            "VALUES (?, ?, 0, ?, ?, ?, ?, '', ?, ?, ?, ?, ?, ?, ?, ?, 1, 50, 50, ?, ?, '', '', '', '', ?, ?, ?, ?, ?, ?)",
            (qid, quest["name"], quest["start_name"], quest["start_region"], quest["accept"], quest["description"], empty,
             "|".join(map(str, quest["step_types"])), "|".join(quest["steps"]), empty, empty, "|".join(quest["targets"]),
             empty, empty, money, xp, armour, quest["finish"], dependency, str(class_id), CLASSIC, now))
        added += 1
    return {"level50": added, "camp": _camp(conn, data["camp"])}


def _camp(conn, camp):
    """Copy the camp's archived rows back into Mob (kept in the archive); rows already in Mob are skipped."""
    columns = ", ".join(f'"{name}"' for _, name, *_ in conn.execute('PRAGMA table_info("Mob")'))
    names = [name.lower() for name in camp["names"]]
    x, y, radius = camp["x"], camp["y"], camp["radius"]
    return conn.execute(
        f"INSERT INTO Mob ({columns}) SELECT {columns} FROM {ARCHIVE} WHERE Region=? "
        f"AND (X-?)*(X-?) + (Y-?)*(Y-?) <= ? AND lower(Name) IN ({', '.join('?' * len(names))}) "
        f"AND Mob_ID NOT IN (SELECT Mob_ID FROM Mob)",
        (camp["region"], x, x, y, y, radius * radius, *names)).rowcount


def _old_shadows_50(conn, now, data):
    by_class = dict(zip(data["classes"].values(), data["steps"]["50"]["ids"]))
    carried = removed = 0
    for quest_id, character, step, class_id in conn.execute(
            "SELECT q.Quest_ID, q.Character_ID, q.Step, c.Class FROM Quest q "
            "LEFT JOIN DOLCharacters c ON c.DOLCharacters_ID = q.Character_ID WHERE q.Name=?",
            (data["old_quest"],)).fetchall():
        target = by_class.get(class_id)
        if step == -2 and target is not None:  # -2: finished (AbstractQuest.FinishQuest)
            if not conn.execute("SELECT 1 FROM CharacterXDataQuest WHERE Character_ID=? AND DataQuestID=?",
                                (character, target)).fetchone():
                conn.execute("INSERT INTO CharacterXDataQuest (Character_ID, DataQuestID, Step, Count, LastTimeRowUpdated) "
                             "VALUES (?, ?, 0, 1, ?)", (character, target, now))
            carried += 1
        else:
            removed += 1
        conn.execute("DELETE FROM Quest WHERE Quest_ID=?", (quest_id,))
    vests = data["vests"]
    recharged = conn.execute(
        f"UPDATE Inventory SET Charges=?, LastTimeRowUpdated=? "
        f"WHERE ITemplate_Id IN ({', '.join('?' * len(vests['ids']))}) AND Charges=0",
        (vests["Charges"], now, *vests["ids"])).rowcount
    return {"carried": carried, "removed": removed, "vests": recharged}


STEPS = (_gos_links, _pin_names, _close_supply_runs, _pay, _rewards, _texts, _items, _level50, _old_shadows_50)


def _now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
