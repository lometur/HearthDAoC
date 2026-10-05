import os
import sqlite3
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
TOOL = os.path.join(os.path.dirname(HERE), "accounts", "accounts.py")
sys.path.insert(0, os.path.dirname(TOOL))

import accounts  # noqa: E402

SCHEMA = """
CREATE TABLE `Account` (`Name` VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, `Password` TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
`CreationDate` DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', `LastLogin` DATETIME DEFAULT NULL, `Realm` INT(11) NOT NULL DEFAULT 0,
`PrivLevel` UNSIGNED INT(10) NOT NULL DEFAULT 0, `Status` INT(11) NOT NULL DEFAULT 0, `Mail` TEXT DEFAULT NULL COLLATE NOCASE,
`LastLoginIP` VARCHAR(255) DEFAULT NULL COLLATE NOCASE, `LastClientVersion` TEXT DEFAULT NULL COLLATE NOCASE,
`Language` TEXT DEFAULT NULL COLLATE NOCASE, `IsMuted` TINYINT(1) NOT NULL DEFAULT 0, `IsWarned` TINYINT(1) NOT NULL DEFAULT 0,
`Notes` TEXT DEFAULT NULL COLLATE NOCASE, `IsTester` TINYINT(1) NOT NULL DEFAULT 0, `CharactersTraded` INT(11) NOT NULL DEFAULT 0,
`SoloCharactersTraded` INT(11) NOT NULL DEFAULT 0, `DiscordID` TEXT DEFAULT NULL COLLATE NOCASE, `Realm_Timer_Realm` INT(11) NOT NULL DEFAULT 0,
`Realm_Timer_Last_Combat` DATETIME DEFAULT NULL, `LastDisconnected` DATETIME DEFAULT NULL,
`LastTimeRowUpdated` DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', `Account_ID` VARCHAR(255) DEFAULT NULL COLLATE NOCASE,
PRIMARY KEY (`Name`));
CREATE UNIQUE INDEX `U_Account_Account_ID` ON `Account` (`Account_ID`);
CREATE TABLE `DOLCharacters` (`AccountName` VARCHAR(255) NOT NULL DEFAULT '', `Name` VARCHAR(255) NOT NULL DEFAULT '');
"""


class AccountsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "world.db")
        with sqlite3.connect(self.db) as c:
            c.executescript(SCHEMA)
        self.c = accounts.connect(self.db)

    def tearDown(self):
        self.c.close()
        self.tmp.cleanup()

    def test_hash_matches_the_servers_algorithm(self):
        # Server: "##" + MD5 over UTF-16 big-endian chars, each byte as hex WITHOUT zero padding.
        self.assertEqual(accounts.hash_password("abc"), "##FC48464A2559EE604BE7382C39C6687")  # 31 chars: no zero padding
        self.assertEqual(accounts.hash_password("a")[:2], "##")

    def test_create_and_list(self):
        accounts.create(self.c, "Alice1", "s3cret")
        self.c.execute("INSERT INTO DOLCharacters (AccountName, Name) VALUES ('Alice1', 'Lometur')")
        rows = accounts.list_accounts(self.c)
        self.assertEqual(rows[0][:2], ("Alice1", 1))
        self.assertEqual(rows[0][3], 1)
        row = self.c.execute("SELECT Password, Language, Realm, Account_ID FROM Account").fetchone()
        self.assertEqual(row[0], accounts.hash_password("s3cret"))
        self.assertEqual((row[1], row[2]), ("EN", 0))
        self.assertEqual(len(row[3]), 36)

    def test_create_validation(self):
        for name, pw, plvl, msg in [("bad name", "x", 1, "letters and digits"), ("", "x", 1, "letters and digits"),
                                    ("ok", "", 1, "no spaces"), ("ok", "has space", 1, "no spaces"),
                                    ("ok", "x", 5, "plvl")]:
            with self.subTest(name=name), self.assertRaisesRegex(accounts.AccountError, msg):
                accounts.create(self.c, name, pw, plvl)
        accounts.create(self.c, "Bob", "pw")
        with self.assertRaisesRegex(accounts.AccountError, "already exists"):
            accounts.create(self.c, "bob", "pw2")

    def test_plvl_refuses_while_possibly_online(self):
        accounts.create(self.c, "Carol", "pw")
        self.c.execute("UPDATE Account SET LastLogin='2026-10-05 10:00:00.0000000', LastDisconnected=NULL WHERE Name='Carol'")
        self.c.commit()
        with self.assertRaisesRegex(accounts.AccountError, "log out"):
            accounts.set_plvl(self.c, "Carol", 3)
        accounts.set_plvl(self.c, "Carol", 3, server_stopped=True)
        self.assertEqual(self.c.execute("SELECT PrivLevel FROM Account WHERE Name='Carol'").fetchone()[0], 3)
        self.c.execute("UPDATE Account SET LastDisconnected='2026-10-05 11:00:00.0000000' WHERE Name='Carol'")
        self.c.commit()
        accounts.set_plvl(self.c, "Carol", 1)
        self.assertEqual(self.c.execute("SELECT PrivLevel FROM Account WHERE Name='Carol'").fetchone()[0], 1)

    def test_plvl_unknown_account(self):
        with self.assertRaisesRegex(accounts.AccountError, "no account"):
            accounts.set_plvl(self.c, "Nobody", 2, server_stopped=True)

    def test_cli(self):
        run = lambda *a: subprocess.run([sys.executable, TOOL, "--db", self.db, *a], capture_output=True, text=True)  # noqa: E731
        self.assertEqual(run("create", "Dave", "pw", "--plvl", "2").returncode, 0)
        out = run("list")
        self.assertIn("Dave", out.stdout)
        bad = run("create", "Dave", "pw")
        self.assertEqual(bad.returncode, 1)
        self.assertIn("ERROR: account 'Dave' already exists", bad.stderr)


if __name__ == "__main__":
    unittest.main()
