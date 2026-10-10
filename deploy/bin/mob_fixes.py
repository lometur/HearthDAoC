"""Period corrections to upstream's monsters in the world data: levels and spots, from mob_fixes.json beside this file.

The period quest archives turn up corrections like "Frund and Agisthil are level 10, at the red dwarf camp" (owner
2026-10-10); each is one entry of the file. world_fixes.py calls apply() inside its transaction, after its other
steps, under its own savepoint: if it fails it is rolled back alone, apply() returns only the "not applied" line, and
the other fixes and the start go on.

Like quest_dialogue.py it has no marker: it runs at every start and changes a row only while the row still holds every
value the entry expects (upstream's), so the owner's own changes stay. A row that already holds the entry's values is
up to date. Any other row is left alone and named in the start log at every start, so a row the owner or an upstream
upgrade changed is seen. An entry whose row (or table) is not in the world is skipped.

The file:
  {"fixes": [{"name": "Frund", "table": "Mob", "id": "3ce2...", "expect": {"X": 491936, ...},
              "set": {"X": 537351, ...}, "why": "..."}]}
- table and id: Mob (the row whose Mob_ID is id) or NpcTemplate (the rows whose TemplateId is id). One entry per row.
- expect: the values the row must still hold, upstream's, column by column; it has a value for every column of set.
- set: the values to set (never the row's id). Every column of expect and set must be one of the table's columns, or
  the fix is not applied. Values are text, numbers or null, and compare as SQLite compares them with the column (12
  and "12" are the same in an integer column).
- name: what the start log calls the row; why: the correction and its source.
"""
import datetime
import json
import os

SAVEPOINT = "mob_fixes"
NOT_APPLIED = "Mob fixes: not applied ({}); the monsters keep the values they have"
DATA_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "mob_fixes.json")
KEYS = {"Mob": "Mob_ID", "NpcTemplate": "TemplateId"}
SHOWN = 10


def load_data(path=DATA_FILE):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def _is_value(value):
    return value is None or (isinstance(value, (str, int, float)) and not isinstance(value, bool))


def _check(entry, n):
    """The entry's table, after checking its fields; a bad entry raises ValueError."""
    what = f"fix {n}" + (f" ({entry['name']})" if isinstance(entry.get("name"), str) else "")
    for field in ("name", "why"):
        if not isinstance(entry.get(field), str) or not entry[field].strip():
            raise ValueError(f"{what}: no {field}")
    table = entry.get("table")
    if table not in KEYS:
        raise ValueError(f"{what}: unknown table {table}")
    if not isinstance(entry.get("id"), (str, int)) or isinstance(entry["id"], bool):
        raise ValueError(f"{what}: no id")
    expect, sets = entry.get("expect"), entry.get("set")
    if not isinstance(expect, dict) or not isinstance(sets, dict) or not sets:
        raise ValueError(f"{what}: expect and set must be objects, set not empty")
    missing = sorted(set(sets) - set(expect))
    if missing:
        raise ValueError(f"{what}: expect has no value for {', '.join(missing)}")
    if KEYS[table].lower() in {column.lower() for column in sets}:
        raise ValueError(f"{what}: set changes the row's {KEYS[table]}")
    if not all(_is_value(value) for value in (*expect.values(), *sets.values())):
        raise ValueError(f"{what}: a value is not text, a number or null")
    return what, table


def _plan(conn, data):
    """[(entry, table, key column)] of the entries whose table is in the world, after checking the whole file."""
    tables = {name.lower() for (name,) in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    plan, seen = [], set()
    for n, entry in enumerate(data.get("fixes", []), 1):
        what, table = _check(entry, n)
        if (table, entry["id"]) in seen:
            raise ValueError(f"{what}: its row has another entry")
        seen.add((table, entry["id"]))
        if table.lower() not in tables:
            continue  # a world without the table: nothing to correct
        have = {name.lower() for _, name, *_ in conn.execute(f'PRAGMA table_info("{table}")')}
        unknown = sorted({column for column in (*entry["expect"], *entry["set"], KEYS[table]) if column.lower() not in have})
        if unknown:
            raise ValueError(f"{what}: {table} has no column {', '.join(unknown)}")
        plan.append((entry, table, KEYS[table]))
    return plan


def _holds(values):
    """SQL that is true where a row holds all of `values` ({column: value}), and its parameters."""
    return " AND ".join(f'"{column}" IS ?' for column in values), list(values.values())


def apply(conn, now=None, data=None):
    """Correct the rows that still hold the values their entries expect; returns the summary line, the "not applied"
    line, or nothing.

    Inside an open transaction (world_fixes.py's), nothing here commits; on a connection with none open, the
    savepoint is the transaction, and its release commits it."""
    now = now or _now()
    conn.execute(f"SAVEPOINT {SAVEPOINT}")
    try:
        data = data if data is not None else load_data()
        changed, kept = [], []
        for entry, table, key in _plan(conn, data):
            label = f"{entry['name']} ({table} {entry['id']})"
            at_set, set_args = _holds(entry["set"])
            upstream, expect_args = _holds(entry["expect"])
            rows = conn.execute(f'SELECT {at_set}, {upstream} FROM "{table}" WHERE "{key}"=?',
                                (*set_args, *expect_args, entry["id"])).fetchall()
            if any(not done and not ours for done, ours in rows):
                kept.append(label)
            if any(ours and not done for done, ours in rows):
                assign = ", ".join(f'"{column}"=?' for column in entry["set"])
                stamp = ""
                if "lasttimerowupdated" in {name.lower() for _, name, *_ in conn.execute(f'PRAGMA table_info("{table}")')}:
                    stamp = ", LastTimeRowUpdated=?"
                conn.execute(f'UPDATE "{table}" SET {assign}{stamp} WHERE "{key}"=? AND {upstream} AND NOT ({at_set})',
                             (*set_args, *([now] if stamp else []), entry["id"], *expect_args, *set_args))
                changed.append(label)
    except Exception as e:  # any failure: undo this fix only, and let the other fixes and the start go on
        conn.execute(f"ROLLBACK TO {SAVEPOINT}")
        conn.execute(f"RELEASE {SAVEPOINT}")
        return [NOT_APPLIED.format(str(e) or type(e).__name__)]
    conn.execute(f"RELEASE {SAVEPOINT}")
    return summary(changed, kept)


def _names(labels):
    more = f" and {len(labels) - SHOWN} more" if len(labels) > SHOWN else ""
    return ", ".join(labels[:SHOWN]) + more


def summary(changed, kept):
    """The start log's line: the rows corrected, and every start the rows left as they are, so a row the owner or an
    upstream upgrade changed is seen. Nothing when all are up to date."""
    parts = []
    if changed:
        parts.append(f"{len(changed)} corrected: {_names(changed)}")
    if kept:
        parts.append(f"{len(kept)} left as they are (they hold neither upstream's values nor this file's): {_names(kept)}")
    return ["Mob fixes: " + "; ".join(parts)] if parts else []


def _now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
