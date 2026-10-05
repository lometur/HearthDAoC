#!/usr/bin/env python3
"""Manage HearthDAoC (OfflineDAoC) accounts in the world database: create, list, set privilege level.

Uses the server's own password hashing ("##" + MD5 of the UTF-16 big-endian characters, each byte
as hex without zero padding; LoginRequestHandler.CryptPassword), so accounts made here work exactly
like accounts created at first login. Safe while the server runs, except changing the privilege
level of a logged-in player: logging out saves the in-memory account over the change.
"""
import argparse
import datetime
import hashlib
import re
import sqlite3
import sys
import uuid

NAME_RE = re.compile(r"^[A-Za-z0-9]+$")


class AccountError(Exception):
    pass


def hash_password(password):
    data = b"".join(bytes(((ord(ch) >> 8) & 0xFF, ord(ch) & 0xFF)) for ch in password)
    return "##" + "".join(format(b, "X") for b in hashlib.md5(data).digest())


def connect(db):
    conn = sqlite3.connect(db, timeout=10)
    conn.execute("PRAGMA busy_timeout=10000")
    return conn


def _now_local():
    return datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S.%f")


def _now_utc():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S.%fZ")


def create(conn, name, password, plvl=1):
    if not NAME_RE.match(name or ""):
        raise AccountError("account names may only contain letters and digits")
    if not password or any(ch.isspace() for ch in password):
        raise AccountError("the password must be non-empty and contain no spaces")
    if plvl not in (1, 2, 3):
        raise AccountError("plvl must be 1 (player), 2 (GM) or 3 (admin)")
    if conn.execute("SELECT 1 FROM Account WHERE Name = ? COLLATE NOCASE", (name,)).fetchone():
        raise AccountError(f"account {name!r} already exists")
    with conn:
        conn.execute(
            "INSERT INTO Account (Name, Password, CreationDate, Realm, PrivLevel, Language, LastTimeRowUpdated, Account_ID) "
            "VALUES (?, ?, ?, 0, ?, 'EN', ?, ?)",
            (name, hash_password(password), _now_local(), plvl, _now_utc(), str(uuid.uuid4())))


def list_accounts(conn):
    return conn.execute(
        "SELECT a.Name, a.PrivLevel, a.LastLogin, "
        "(SELECT count(*) FROM DOLCharacters d WHERE d.AccountName = a.Name COLLATE NOCASE) "
        "FROM Account a ORDER BY a.Name COLLATE NOCASE").fetchall()


def possibly_online(conn, name):
    row = conn.execute("SELECT LastLogin, LastDisconnected FROM Account WHERE Name = ? COLLATE NOCASE", (name,)).fetchone()
    if row is None:
        raise AccountError(f"no account named {name!r}")
    last_login, last_disconnected = row
    return last_login is not None and (last_disconnected is None or str(last_disconnected) < str(last_login))


def set_plvl(conn, name, plvl, server_stopped=False):
    if plvl not in (1, 2, 3):
        raise AccountError("plvl must be 1 (player), 2 (GM) or 3 (admin)")
    if not server_stopped and possibly_online(conn, name):
        raise AccountError(f"{name} may be logged in, and logging out would overwrite the change. "
                           "Ask them to log out first, or stop the server.")
    with conn:
        changed = conn.execute("UPDATE Account SET PrivLevel = ?, LastTimeRowUpdated = ? WHERE Name = ? COLLATE NOCASE",
                               (plvl, _now_utc(), name)).rowcount
    if changed != 1:
        raise AccountError(f"no account named {name!r}")


def main(argv=None):
    ap = argparse.ArgumentParser(description="Manage HearthDAoC accounts.")
    ap.add_argument("--db", required=True)
    sub = ap.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("create")
    c.add_argument("name")
    c.add_argument("password")
    c.add_argument("--plvl", type=int, default=1)
    sub.add_parser("list")
    p = sub.add_parser("plvl")
    p.add_argument("name")
    p.add_argument("level", type=int)
    p.add_argument("--server-stopped", action="store_true")
    a = ap.parse_args(argv)
    conn = connect(a.db)
    try:
        if a.cmd == "create":
            create(conn, a.name, a.password, a.plvl)
            print(f"Created account {a.name} (plvl {a.plvl}).")
        elif a.cmd == "list":
            print(f"{'Account':20} {'plvl':>4}  {'Last login':26} Characters")
            for name, plvl, last, chars in list_accounts(conn):
                print(f"{name:20} {plvl:>4}  {str(last or 'never'):26} {chars}")
        else:
            set_plvl(conn, a.name, a.level, a.server_stopped)
            print(f"{a.name} is now plvl {a.level}; it applies at their next login.")
    except AccountError as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 1
    finally:
        conn.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
