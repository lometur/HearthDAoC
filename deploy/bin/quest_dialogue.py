#!/usr/bin/env python3
"""The Guild of Shadows chain's dialogue (quests of levels 7 to 50) in the world data.

Upstream left most of the chain's text empty ("[Traveler's]") or filled with walkthrough notes. The texts are in
quest_dialogue.json, beside this file; world_fixes.py calls apply() inside its transaction, right after
epic_chains.apply() (the chain's rows, including the fork's own level-50 ones, must exist), under its own savepoint:
if it fails it is rolled back alone, apply() returns only the "not applied" line, and the other fixes and the start
go on.

Unlike the other world fixes this one has no marker. It runs at every start and changes a row only while the columns
it sets still hold a text the file's guard lists: upstream's (as epic_chains.py leaves them) or an earlier version of
this file's text. So the owner's own edits stay, and a later revision of the text reaches a world that had an earlier
one. A row that already holds the new text is left alone.

The file:
  {"quests": [{"step": "7", "set": {...}, "guard": {"21500": ["<digest>", ...]}}]}
- step: a key of epic_chains_data.json "steps"; its "ids" are the quest IDs of the classes, in that file's order
  ("classes"); a null ID is skipped.
- set: DataQuest columns to set (AcceptText, Description, SourceText, StepText, TargetText, AdvanceText, FinishText,
  StepItemTemplates). A value is a string (every class), an object with all five class names, or (for the per-stage
  columns SourceText, StepText, TargetText, AdvanceText, StepItemTemplates) a list with one entry, a string or a
  per-class object, for each stage of the quest row (its StepType split on "|"); the list is joined with "|". No
  value may contain "|".
- guard: for each quest ID, the digests of its `set` columns' values that may be replaced. digest() makes them;
  `quest_dialogue.py --digests WORLD.db` prints them for upstream's text (after epic_chains) of every entry.
"""
import argparse
import datetime
import hashlib
import json
import os
import shutil
import sqlite3
import sys
import tempfile

import epic_chains

SAVEPOINT = "quest_dialogue"
NOT_APPLIED = "Quest dialogue: not applied ({}); the quests keep the text they have"
DATA_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "quest_dialogue.json")
COLUMNS = ("AcceptText", "Description", "SourceText", "StepText", "TargetText", "AdvanceText", "FinishText",
           "StepItemTemplates")
PER_STAGE = ("SourceText", "StepText", "TargetText", "AdvanceText", "StepItemTemplates")


def load_data(path=DATA_FILE):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def digest(values):
    """The guard digest of a row's `set` columns: `values` in sorted column-name order (None counts as "")."""
    return hashlib.sha256(json.dumps([value or "" for value in values], ensure_ascii=False).encode("utf-8")).hexdigest()


def _per_class(value, classes, what):
    """A string for every class, or an object with all the class names: {class name: string}."""
    if isinstance(value, str):
        if "|" in value:
            raise ValueError(f"{what}: a value contains '|'")
        return {name: value for name in classes}
    if not isinstance(value, dict):
        raise ValueError(f"{what}: expected a string or a per-class object")
    missing = [name for name in classes if name not in value]
    unknown = [name for name in value if name not in classes]
    if missing or unknown:
        raise ValueError(f"{what}: " + ("missing class " + ", ".join(missing) if missing else "unknown class " + ", ".join(unknown)))
    return _per_class_strings(value, what)


def _per_class_strings(value, what):
    for text in value.values():
        if not isinstance(text, str):
            raise ValueError(f"{what}: a per-class value is not a string")
        if "|" in text:
            raise ValueError(f"{what}: a value contains '|'")
    return dict(value)


def resolve(column, value, classes, stages):
    """The text of `column` for each class: {class name: text}. `stages` is {class name: number of stages}."""
    what = column
    if column not in COLUMNS:
        raise ValueError(f"unknown column {column}")
    if not isinstance(value, list):
        return _per_class(value, classes, what)
    if column not in PER_STAGE:
        raise ValueError(f"{what}: a list is only for the per-stage columns")
    entries = [_per_class(entry, classes, what) for entry in value]
    out = {}
    for name in classes:
        if stages[name] is not None and len(value) != stages[name]:
            raise ValueError(f"{what}: {len(value)} entries for {stages[name]} stages ({name})")
        out[name] = "|".join(entry[name] for entry in entries)
    return out


def _rows(conn, ids):
    """{quest id: StepType} of the rows that exist."""
    marks = ", ".join("?" * len(ids))
    return {qid: stype for qid, stype in conn.execute(f"SELECT ID, IFNULL(StepType, '') FROM DataQuest WHERE ID IN ({marks})", ids)}


def _plan(conn, data, chains):
    """The work: [(quest id, columns in sorted order, new values, guard digests)], after checking the whole file."""
    classes = list(chains["classes"])
    plan, seen = [], set()
    for entry in data.get("quests", []):
        step = entry["step"]
        if step in seen:
            raise ValueError(f"step {step} appears twice")
        seen.add(step)
        if step not in chains["steps"]:
            raise ValueError(f"unknown step {step}")
        ids = chains["steps"][step]["ids"]
        by_class = {name: qid for name, qid in zip(classes, ids) if qid is not None}
        columns = sorted(entry["set"])
        stype = _rows(conn, list(by_class.values())) if by_class else {}
        stages = {name: (len(stype[qid].split("|")) if qid in stype else None) for name, qid in by_class.items()}
        for name in classes:
            stages.setdefault(name, None)
        resolved = {column: resolve(column, entry["set"][column], classes, stages) for column in columns}
        for name, qid in by_class.items():
            plan.append((qid, columns, [resolved[column][name] for column in columns],
                         set(entry.get("guard", {}).get(str(qid), []))))
    return plan


def apply(conn, now=None, data=None, chains=None):
    """Rewrite the chain's dialogue where it still holds a known earlier text; returns the summary line, the "not
    applied" line, or nothing. `chains` is epic_chains_data.json's contents.

    Inside an open transaction (world_fixes.py's), nothing here commits; on a connection with none open, the
    savepoint is the transaction, and its release commits it."""
    tables = {name.lower() for (name,) in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    if "dataquest" not in tables:
        return []
    now = now or _now()
    conn.execute(f"SAVEPOINT {SAVEPOINT}")
    try:
        data = data if data is not None else load_data()
        chains = chains if chains is not None else epic_chains.load_data()
        rewritten = kept = 0
        for qid, columns, values, guard in _plan(conn, data, chains):
            row = conn.execute(f"SELECT {', '.join(columns)} FROM DataQuest WHERE ID=?", (qid,)).fetchone()
            if row is None:
                continue
            held = [value or "" for value in row]
            if held == values:
                continue
            if digest(held) not in guard:
                kept += 1
                continue
            sets = ", ".join(f"{column}=?" for column in columns)
            conn.execute(f"UPDATE DataQuest SET {sets}, LastTimeRowUpdated=? WHERE ID=?", (*values, now, qid))
            rewritten += 1
    except Exception as e:  # any failure: undo this fix only, and let the other fixes and the start go on
        conn.execute(f"ROLLBACK TO {SAVEPOINT}")
        conn.execute(f"RELEASE {SAVEPOINT}")
        return [NOT_APPLIED.format(str(e) or type(e).__name__)]
    conn.execute(f"RELEASE {SAVEPOINT}")
    if not rewritten:
        return []
    line = f"Quest dialogue: {rewritten} quests rewritten"
    if kept:
        line += f"; {kept} left as they are (changed since upstream)"
    return [line]


def digests(world, data=None, chains=None):
    """{step: {quest id: digest}} of upstream's text of every entry's `set` columns, read from a copy of the world
    that has had epic_chains applied (when it lacks the marker); the world itself is not changed."""
    data = data if data is not None else load_data()
    chains = chains if chains is not None else epic_chains.load_data()
    out = {}
    with tempfile.TemporaryDirectory() as tmp:
        copy = os.path.join(tmp, "world.db")
        shutil.copyfile(world, copy)
        conn = sqlite3.connect(copy)
        try:
            with conn:
                for line in epic_chains.apply(conn):
                    if line.startswith("Epic chains: not applied"):
                        raise RuntimeError(line)
            for entry in data.get("quests", []):
                columns = sorted(entry["set"])
                ids = [qid for qid in chains["steps"][entry["step"]]["ids"] if qid is not None]
                out[entry["step"]] = {}
                for qid in ids:
                    row = conn.execute(f"SELECT {', '.join(columns)} FROM DataQuest WHERE ID=?", (qid,)).fetchone()
                    if row is not None:
                        out[entry["step"]][str(qid)] = digest(list(row))
        finally:
            conn.close()
    return out


def _now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S")


def main(argv=None):
    ap = argparse.ArgumentParser(description="The Guild of Shadows chain's dialogue: print the guard digests.")
    ap.add_argument("--digests", metavar="WORLD.db", required=True,
                    help="print {step: {quest id: digest}} of upstream's text of each entry's set columns")
    a = ap.parse_args(argv)
    print(json.dumps(digests(a.digests), indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
