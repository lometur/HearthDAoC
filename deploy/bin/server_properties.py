#!/usr/bin/env python3
"""Write HearthDAoC's settings into the world's ServerProperty table before the server starts.

HEARTHDAOC_GM_ONLY_COMMANDS ("/tele;/tc") becomes the command_plvl_overrides property
("/tele=2;/tc=2"), which the server applies when it loads commands: the listed single-player
shortcuts then need GM rights. Only writes when the value differs.
"""
import argparse
import datetime
import re
import sqlite3
import sys
import uuid

OVERRIDES_KEY = "command_plvl_overrides"
OVERRIDES_DESCRIPTION = ("Privilege level a command needs, overriding its default, separated by semi-colon, "
                         "example /tele=2;/tc=2 (1 player, 2 GM, 3 admin)")
GM = 2
COMMAND = re.compile(r"^/[A-Za-z0-9_]+$")


class PropertyError(Exception):
    pass


def gm_only_overrides(spec):
    """'/tele;/tc' -> '/tele=2;/tc=2'; '' or 'none' -> ''."""
    if spec.strip().lower() in ("", "none"):
        return ""
    commands = [c.strip() for c in spec.split(";") if c.strip()]
    bad = [c for c in commands if not COMMAND.match(c)]
    if bad:
        raise PropertyError(f"HEARTHDAOC_GM_ONLY_COMMANDS must be commands like /tele;/tc (or none), got {bad[0]!r}")
    return ";".join(f"{c.lower()}={GM}" for c in commands)


def set_property(db, key, value, category="server", description=OVERRIDES_DESCRIPTION):
    """Create or update a server property. Returns True when something changed."""
    conn = sqlite3.connect(db, timeout=30)
    try:
        row = conn.execute("SELECT Value FROM ServerProperty WHERE `Key`=?", (key,)).fetchone()
        if row is not None and row[0] == value:
            return False
        now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
        with conn:
            if row is None:
                conn.execute("INSERT INTO ServerProperty (Category, `Key`, Description, DefaultValue, Value, "
                             "LastTimeRowUpdated, ServerProperty_ID) VALUES (?, ?, ?, '', ?, ?, ?)",
                             (category, key, description, value, now, str(uuid.uuid4())))
            else:
                conn.execute("UPDATE ServerProperty SET Value=?, LastTimeRowUpdated=? WHERE `Key`=?", (value, now, key))
        return True
    finally:
        conn.close()


def main(argv=None):
    ap = argparse.ArgumentParser(description="Apply HearthDAoC settings to the world's server properties.")
    ap.add_argument("--db", required=True)
    ap.add_argument("--gm-only-commands", required=True, help='e.g. "/tele;/tc", or "none"')
    a = ap.parse_args(argv)
    try:
        overrides = gm_only_overrides(a.gm_only_commands)
    except PropertyError as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 2
    if set_property(a.db, OVERRIDES_KEY, overrides):
        print(f"GM-only commands: {a.gm_only_commands.strip() or 'none'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
