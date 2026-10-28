"""No starter guilds (owner 2026-10-28, #176: "bots will autoquit or never join the beginner guilds. in fact we can do
away with them if that's easy").

server_properties.py writes starting_guild False before every start (test_server_properties.py), so the server makes no
starter guild and puts no new character in one. starter_guilds.py, a world fix (world_fixes.py, every start, under its
own savepoint), takes every character and gamebot out of the ones a world already has and removes them with their
ranks. A player bot never joins one even with the property back on: a marked guard in StartupGuilds.cs, checked here
(its predicate, PlayerBotPlan.IsBotAccount, is unit-tested in UT_GuildForming).
"""
import os
import re
import shutil
import sqlite3
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
BIN = os.path.join(HERE, "..", "bin")
sys.path.insert(0, BIN)

import starter_guilds as sg  # noqa: E402
import world_fixes as wf  # noqa: E402
from tests import endings  # noqa: E402
from tests.test_world_fixes import SCHEMA as WORLD_FIXES_SCHEMA  # noqa: E402

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")
ROOT = os.path.dirname(os.path.dirname(HERE))
SERVER = os.path.join(ROOT, "source", "server")
STARTUP_GUILDS = os.path.join(SERVER, "GameServer", "scripts", "gameevents", "StartupGuilds.cs")

# The columns of upstream's tables that the fix reads or writes, as the server makes them.
SCHEMA = [
    "CREATE TABLE Guild (GuildID VARCHAR(255) DEFAULT NULL COLLATE NOCASE, GuildName VARCHAR(255) DEFAULT NULL "
    "COLLATE NOCASE, Realm UNSIGNED TINYINT(3) NOT NULL DEFAULT 0, IsStartingGuild TINYINT(1) NOT NULL DEFAULT 0, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', Guild_ID VARCHAR(255) NOT NULL DEFAULT '' "
    "COLLATE NOCASE, PRIMARY KEY (Guild_ID))",
    "CREATE TABLE GuildRank (GuildID VARCHAR(255) DEFAULT NULL COLLATE NOCASE, Title TEXT DEFAULT NULL, "
    "RankLevel UNSIGNED TINYINT(3) NOT NULL DEFAULT 0, GuildRank_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, "
    "PRIMARY KEY (GuildRank_ID))",
    "CREATE TABLE DOLCharacters (AccountName VARCHAR(255) NOT NULL DEFAULT '', Name VARCHAR(255) NOT NULL DEFAULT '', "
    "GuildID VARCHAR(255) DEFAULT NULL COLLATE NOCASE, GuildRank UNSIGNED SMALLINT(5) NOT NULL DEFAULT 0, "
    "GuildNote TEXT DEFAULT NULL COLLATE NOCASE, LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
    "DOLCharacters_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, PRIMARY KEY (DOLCharacters_ID))",
    "CREATE TABLE offline_world_bot_guild (BotId BIGINT(20) NOT NULL DEFAULT 0, GuildID VARCHAR(255) NOT NULL DEFAULT '' "
    "COLLATE NOCASE, BotName TEXT NOT NULL DEFAULT '' COLLATE NOCASE, PRIMARY KEY (BotId))",
]
STARTERS = [("s-hib", "Tir na Nog Adventurers", 3), ("s-alb", "Clan Cotswold", 1), ("s-mid", "Mularn Protectors", 2)]
PLAYER_GUILD = ("p-1", "Knights of the Hearth", 1)
LINE = ("Starter guilds: removed Clan Cotswold, Mularn Protectors, Tir na Nog Adventurers and their ranks; "
        "3 characters and 1 gamebot left them")


def build(db, schema=SCHEMA):
    with sqlite3.connect(db) as c:
        for stmt in schema:
            c.execute(stmt)
        for guild_id, name, realm in STARTERS + [PLAYER_GUILD]:
            starting = guild_id.startswith("s-")
            c.execute("INSERT INTO Guild (GuildID, GuildName, Realm, IsStartingGuild, Guild_ID) VALUES (?, ?, ?, ?, ?)",
                      (guild_id, name, realm, int(starting), "row-" + guild_id))
            for level in range(10):
                c.execute("INSERT INTO GuildRank (GuildID, Title, RankLevel, GuildRank_ID) VALUES (?, ?, ?, ?)",
                          (guild_id, f"Rank {level}", level, f"{guild_id}-{level}"))
        for char_id, account, name, guild_id, rank, note in [
                ("c1", "lometur", "Charles", "s-alb", 8, "new here"),
                ("c2", "hdbot-alb-01", "Botwin", "S-ALB", 8, None),  # GuildID compares without case
                ("c3", "friend", "Hrolf", "s-mid", 8, None),
                ("c4", "lometur", "Martel", "p-1", 0, "leader"),
                ("c5", "friend", "Nobody", None, 0, None),
                ("c6", "friend", "Left", "", 9, "")]:
            c.execute("INSERT INTO DOLCharacters (DOLCharacters_ID, AccountName, Name, GuildID, GuildRank, GuildNote) "
                      "VALUES (?, ?, ?, ?, ?, ?)", (char_id, account, name, guild_id, rank, note))
        c.execute("INSERT INTO offline_world_bot_guild (BotId, GuildID, BotName) VALUES (101, 's-alb', 'Gamebotta')")
        c.execute("INSERT INTO offline_world_bot_guild (BotId, GuildID, BotName) VALUES (102, 'p-1', 'Guildbotta')")


class StarterGuildsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "world.db")
        build(self.db)

    def tearDown(self):
        self.tmp.cleanup()

    def q(self, sql):
        with sqlite3.connect(self.db) as c:
            return c.execute(sql).fetchall()

    def apply(self):
        """As world_fixes.py runs it: inside a transaction, committed after."""
        conn = sqlite3.connect(self.db)
        try:
            with conn:
                return sg.apply(conn, "2026-10-28 12:00:00")
        finally:
            conn.close()

    def test_the_starter_guilds_go_with_their_ranks_and_their_members_leave_them(self):
        self.assertEqual(self.apply(), [LINE])
        self.assertEqual(self.q("SELECT GuildID, GuildName FROM Guild"), [("p-1", "Knights of the Hearth")])
        self.assertEqual(self.q("SELECT DISTINCT GuildID FROM GuildRank"), [("p-1",)])
        self.assertEqual(self.q("SELECT COUNT(*) FROM GuildRank"), [(10,)])
        self.assertEqual(self.q("SELECT DOLCharacters_ID, GuildID, GuildRank, GuildNote, LastTimeRowUpdated "
                                "FROM DOLCharacters ORDER BY DOLCharacters_ID"), [
            ("c1", "", 8, "", "2026-10-28 12:00:00"),  # as /gc quit leaves it: the rank stays
            ("c2", "", 8, "", "2026-10-28 12:00:00"),
            ("c3", "", 8, "", "2026-10-28 12:00:00"),
            ("c4", "p-1", 0, "leader", "2000-01-01 00:00:00"),
            ("c5", None, 0, None, "2000-01-01 00:00:00"),
            ("c6", "", 9, "", "2000-01-01 00:00:00"),
        ])
        self.assertEqual(self.q("SELECT BotId, GuildID FROM offline_world_bot_guild"), [(102, "p-1")])

    def test_the_next_start_has_nothing_to_do(self):
        self.apply()
        before = self.q("SELECT * FROM DOLCharacters ORDER BY DOLCharacters_ID")
        self.assertEqual(self.apply(), [])
        self.assertEqual(self.q("SELECT * FROM DOLCharacters ORDER BY DOLCharacters_ID"), before)
        self.assertEqual(self.q("SELECT COUNT(*) FROM Guild"), [(1,)])

    def test_a_world_without_starter_guilds_is_left_alone(self):
        with sqlite3.connect(self.db) as c:
            c.execute("UPDATE Guild SET IsStartingGuild=0")
        self.assertEqual(self.apply(), [])
        self.assertEqual(self.q("SELECT COUNT(*) FROM Guild"), [(4,)])
        self.assertEqual(self.q("SELECT COUNT(*) FROM GuildRank"), [(40,)])
        self.assertEqual(self.q("SELECT COUNT(*) FROM DOLCharacters WHERE GuildID LIKE 's-%'"), [(3,)])
        self.assertEqual(self.q("SELECT COUNT(*) FROM offline_world_bot_guild"), [(2,)])

    def test_starter_guilds_with_no_members(self):
        with sqlite3.connect(self.db) as c:
            c.execute("DELETE FROM DOLCharacters")
            c.execute("DROP TABLE offline_world_bot_guild")  # a world from before gamebot guilds
        self.assertEqual(self.apply(), ["Starter guilds: removed Clan Cotswold, Mularn Protectors, Tir na Nog "
                                        "Adventurers and their ranks; they had no members"])
        self.assertEqual(self.q("SELECT COUNT(*) FROM GuildRank"), [(10,)])

    def test_one_member_is_counted_as_one(self):
        with sqlite3.connect(self.db) as c:
            c.execute("DELETE FROM DOLCharacters WHERE DOLCharacters_ID IN ('c2', 'c3')")
            c.execute("DELETE FROM offline_world_bot_guild WHERE BotId=101")
        self.assertEqual(self.apply(), ["Starter guilds: removed Clan Cotswold, Mularn Protectors, Tir na Nog "
                                        "Adventurers and their ranks; 1 character left them"])

    def test_a_world_without_a_guild_table_is_left_alone(self):
        with sqlite3.connect(self.db) as c:
            c.execute("DROP TABLE Guild")
        self.assertEqual(self.apply(), [])
        self.assertEqual(self.q("SELECT COUNT(*) FROM GuildRank"), [(40,)])

    def test_a_failure_undoes_only_this_fix(self):
        with sqlite3.connect(self.db) as c:
            c.execute("CREATE TRIGGER no_leaving BEFORE UPDATE ON DOLCharacters BEGIN SELECT RAISE(ABORT, 'locked'); END")
        before = {t: self.q(f"SELECT * FROM {t} ORDER BY 1, 2") for t in
                  ("Guild", "DOLCharacters", "offline_world_bot_guild")}
        conn = sqlite3.connect(self.db)
        try:
            with conn:
                # An earlier fix's change, in the same transaction: it stays.
                conn.execute("INSERT INTO GuildRank (GuildID, Title, RankLevel, GuildRank_ID) "
                             "VALUES ('p-1', 'Earlier', 10, 'earlier')")
                self.assertEqual(sg.apply(conn), ["Starter guilds: not removed (locked); they stay as they are"])
        finally:
            conn.close()
        self.assertEqual({t: self.q(f"SELECT * FROM {t} ORDER BY 1, 2") for t in before}, before)
        self.assertEqual(self.q("SELECT COUNT(*) FROM GuildRank"), [(41,)])
        self.assertEqual(self.q("SELECT Title FROM GuildRank WHERE GuildRank_ID='earlier'"), [("Earlier",)])


class WorldFixesTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "world.db")
        build(self.db, WORLD_FIXES_SCHEMA + SCHEMA)

    def tearDown(self):
        self.tmp.cleanup()

    def test_every_start_runs_it_last(self):
        self.assertEqual(wf.apply(self.db), [LINE])
        self.assertEqual(wf.apply(self.db), [])

    def test_the_world_fixes_list_it(self):
        self.assertIn("starter_guilds.py", wf.__doc__)


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class ShippedWorldTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "world.db")
        shutil.copyfile(TEST_WORLD, self.db)

    def tearDown(self):
        self.tmp.cleanup()

    def q(self, sql):
        with sqlite3.connect(self.db) as c:
            return c.execute(sql).fetchall()

    def test_the_clean_world_loses_its_three_starter_guilds(self):
        self.assertEqual(self.q("SELECT GuildName FROM Guild WHERE IsStartingGuild <> 0 ORDER BY Realm"),
                         [("Clan Cotswold",), ("Mularn Protectors",), ("Tir na Nog Adventurers",)])
        conn = sqlite3.connect(self.db)
        try:
            with conn:
                self.assertEqual(sg.apply(conn), ["Starter guilds: removed Clan Cotswold, Mularn Protectors, Tir na "
                                                  "Nog Adventurers and their ranks; they had no members"])
            with conn:
                self.assertEqual(sg.apply(conn), [])
        finally:
            conn.close()
        self.assertEqual(self.q("SELECT COUNT(*) FROM Guild WHERE IsStartingGuild <> 0"), [(0,)])
        self.assertEqual(self.q("SELECT COUNT(*) FROM GuildRank WHERE GuildID NOT IN (SELECT GuildID FROM Guild)"),
                         [(0,)])


def read(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read()


class ServerSourceTests(unittest.TestCase):
    def test_the_fix_names_the_servers_tables_and_columns(self):
        tables = os.path.join(SERVER, "CoreDatabase", "Tables")
        guild = read(os.path.join(tables, "DbGuild.cs"))
        self.assertIn('[DataTable(TableName="Guild")]', guild)
        for column in ("GuildID", "GuildName", "Realm", "IsStartingGuild"):
            self.assertRegex(guild, rf"public \w+ {column}\b")
        rank = read(os.path.join(tables, "DbGuildRank.cs"))
        self.assertIn('[DataTable(TableName="GuildRank")]', rank)
        self.assertRegex(rank, r"public string GuildID\b")
        character = read(os.path.join(tables, "DbCoreCharacter.cs"))
        self.assertRegex(character, r'\[DataTable\(TableName\s*=\s*"DOLCharacters"')
        for column in ("GuildID", "GuildNote"):
            self.assertRegex(character, rf"public string {column}\b")
        bots = read(os.path.join(SERVER, "GameServer", "bots", "autonomous", "AutonomousBotGuilds.cs"))
        self.assertIn(f'[DataTable(TableName = "{sg.BOT_TABLE}")]', bots)
        self.assertRegex(bots, r"public string GuildID \{ get; set; \}")

    def test_the_server_starts_the_starter_guilds_only_while_the_property_is_on(self):
        # What the property's False relies on: no starter guild made, no new character put in one.
        text = read(STARTUP_GUILDS)
        loaded = text[text.index("public static void OnScriptCompiled"):]
        self.assertLess(loaded.index("if (!STARTING_GUILD)"), loaded.index("CheckStartupGuilds();"))
        handler = text[text.index("public static void AddNewbieToStarterGuild"):]
        self.assertLess(handler.index("if (!STARTING_GUILD)"), handler.index("character.GuildID = guild.GuildID;"))

    def test_a_player_bot_never_joins_a_starter_guild(self):
        text = read(STARTUP_GUILDS)
        handler = text[text.index("public static void AddNewbieToStarterGuild"):]
        guard = handler.index("if (HearthDAoC.PlayerBots.PlayerBotPlan.IsBotAccount(character.AccountName))")
        self.assertLess(guard, handler.index("character.GuildID = guild.GuildID;"))
        self.assertIn("// HearthDAoC:", handler[:guard].rsplit("\n", 2)[-2])
        self.assertTrue(re.match(r"\s*return;", handler[guard:].split("\n", 1)[1]))

    def test_the_file_keeps_upstreams_endings(self):
        endings.check(self, STARTUP_GUILDS)


if __name__ == "__main__":
    unittest.main()
