"""Tests for classdata.py: the base classes of the classic creation screen.

The unit tests read this repo's server sources (source/server) and use the shipped classic world's
disabled_classes value. Real-file checks run only when the files are given:
- HDC_CLIENT_FILES: an OfflineDAoC 0.35 classic client folder (its game.dll is read, never changed);
- HDC_TEST_WORLD: a clean classic world database (opened read-only).
"""
import hashlib
import os
import shutil
import sqlite3
import struct
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
PATCHES = os.path.dirname(HERE)
REPO = os.path.abspath(os.path.join(PATCHES, "..", ".."))
SERVER_SRC = os.path.join(REPO, "source", "server")
sys.path.insert(0, PATCHES)
import classdata  # noqa: E402

SHIPPED_DISABLED = "20;33;34;39;58-62"  # clean classic 0.35 world, before world_fixes.py
CLIENT = os.environ.get("HDC_CLIENT_FILES")
WORLD = os.environ.get("HDC_TEST_WORLD")
GAME_DLL_SHA256 = "f55ed6b068e22ce8e1106871c2fad6ee10c18390bbb8b5dc772219ad8c8b83bb"

BRI, AVA, HIG, SAR, NOR, TRO, DWA, KOB, CEL, FIR, ELF, LUR, INC, VAL, SYL = range(1, 16)
# docs/fork/specs/2026-10-06-classic-character-creation-design.md, section 2:
# id: (realm, name, becomes at level 5, races offered, highlighted stats)
SPEC = {
    14: (1, "Fighter", ["Armsman", "Mercenary", "Paladin", "Reaver"], [BRI, AVA, HIG, SAR, INC], ("STR", "CON", "DEX")),
    15: (1, "Elementalist", ["Theurgist", "Wizard"], [BRI, AVA], ("INT", "DEX", "QUI")),
    16: (1, "Acolyte", ["Cleric", "Friar"], [BRI, AVA, HIG], ("PIE", "CON", "DEX")),
    17: (1, "Rogue", ["Infiltrator", "Minstrel", "Scout"], [BRI, HIG, SAR, INC], ("DEX", "QUI", "STR")),
    18: (1, "Mage", ["Cabalist", "Sorcerer"], [BRI, AVA, SAR, INC], ("INT", "DEX", "QUI")),
    20: (1, "Disciple", ["Necromancer"], [BRI, SAR, INC], ("INT", "DEX", "QUI")),
    35: (2, "Viking", ["Berserker", "Savage", "Skald", "Thane", "Warrior"], [NOR, TRO, DWA, KOB, VAL],
         ("STR", "CON", "DEX")),
    36: (2, "Mystic", ["Bonedancer", "Runemaster", "Spiritmaster"], [NOR, TRO, DWA, KOB, VAL], ("PIE", "DEX", "QUI")),
    37: (2, "Seer", ["Healer", "Shaman"], [NOR, TRO, DWA, KOB], ("PIE", "CON", "DEX")),
    38: (2, "Rogue", ["Hunter", "Shadowblade"], [NOR, DWA, KOB, VAL], ("DEX", "QUI", "STR")),
    51: (3, "Magician", ["Eldritch", "Enchanter", "Mentalist"], [CEL, ELF, LUR], ("INT", "DEX", "QUI")),
    52: (3, "Guardian", ["Blademaster", "Champion", "Hero"], [CEL, FIR, ELF, LUR, SYL], ("STR", "CON", "DEX")),
    53: (3, "Naturalist", ["Bard", "Druid", "Warden"], [CEL, FIR, SYL], ("EMP", "DEX", "CON")),
    54: (3, "Stalker", ["Nightshade", "Ranger"], [CEL, ELF, LUR], ("DEX", "QUI", "STR")),
    57: (3, "Forester", ["Animist", "Valewalker"], [CEL, FIR, SYL], ("INT", "DEX", "CON")),
}
STAT_NAMES = {v: k for k, v in classdata.STAT_IDS.items()}


def copy_server(dst):
    """Copy just the server files classdata reads, so a test can edit them."""
    for rel in (("Enums", "eCharacterClass.cs"), ("Enums", "eRace.cs"), ("gameobjects", "PlayerRace.cs")):
        os.makedirs(os.path.join(dst, "GameServer", rel[0]), exist_ok=True)
        shutil.copy(os.path.join(SERVER_SRC, "GameServer", *rel), os.path.join(dst, "GameServer", *rel))
    shutil.copytree(os.path.join(SERVER_SRC, "GameServer", "playerclasses"),
                    os.path.join(dst, "GameServer", "playerclasses"))


def edit(path, old, new):
    with open(path, "rb") as f:
        data = f.read()
    assert data.count(old) == 1, (path, old)
    with open(path, "wb") as f:
        f.write(data.replace(old, new))


class ParseDisabledTests(unittest.TestCase):
    def test_shipped_value(self):
        self.assertEqual(classdata.parse_disabled(SHIPPED_DISABLED), {20, 33, 34, 39, 58, 59, 60, 61, 62})

    def test_empty_spaces_commas_reversed_ranges_and_junk(self):
        self.assertEqual(classdata.parse_disabled(""), set())
        self.assertEqual(classdata.parse_disabled(" 5 , 9-7;x;; 12 "), {5, 7, 8, 9, 12})


class ReadDisabledClassesTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "world.db")
        conn = sqlite3.connect(self.db)
        conn.execute("CREATE TABLE ServerProperty (`Key` TEXT PRIMARY KEY, Value TEXT)")
        conn.commit()
        conn.close()

    def tearDown(self):
        self.tmp.cleanup()

    def sha(self):
        with open(self.db, "rb") as f:
            return hashlib.sha256(f.read()).hexdigest()

    def test_reads_the_value_without_changing_the_file(self):
        conn = sqlite3.connect(self.db)
        conn.execute("INSERT INTO ServerProperty VALUES ('disabled_classes', ?)", (SHIPPED_DISABLED,))
        conn.commit()
        conn.close()
        before = self.sha()
        self.assertEqual(classdata.read_disabled_classes(self.db), SHIPPED_DISABLED)
        self.assertEqual(self.sha(), before)

    def test_missing_row_is_empty(self):
        self.assertEqual(classdata.read_disabled_classes(self.db), "")

    def test_missing_file_is_not_created(self):
        missing = os.path.join(self.tmp.name, "nope.db")
        with self.assertRaises(sqlite3.OperationalError):
            classdata.read_disabled_classes(missing)
        self.assertFalse(os.path.exists(missing))


class ServerSourceTests(unittest.TestCase):
    def test_base_class_is_the_csharp_base_type(self):
        classes = classdata.read_server_classes(SERVER_SRC)
        self.assertEqual((classes[2].type_name, classes[2].parent, classes[2].base_name),
                         ("ClassArmsman", "ClassFighter", "Fighter"))
        self.assertEqual(classes[12].parent, "ClassDisciple")
        self.assertEqual(classes[14].parent, "CharacterClassBase")
        # the commented-out list with Korazh and Half Ogre is ignored
        self.assertEqual(classes[2].races, [AVA, BRI, HIG, INC, SAR])

    def test_final_class_ids_are_the_servers_full_classes_but_sluaghbinder(self):
        classes = classdata.read_server_classes(SERVER_SRC)
        finals = {c.id for c in classes.values() if c.parent != "CharacterClassBase"}
        self.assertIn(63, finals)  # Sluaghbinder: b edition only, unknown to the classic client
        self.assertEqual(classdata.FINAL_CLASS_IDS, sorted(finals - {63}))
        self.assertEqual(len(classdata.FINAL_CLASS_IDS), 47)

    def test_hide_races_are_the_races_after_shrouded_isles(self):
        races = classdata.read_player_races(SERVER_SRC)
        later = sorted(r for r, (_, exp) in races.items() if exp not in classdata.CLASSIC_EXPANSIONS)
        self.assertEqual(classdata.HIDE_RACES, later)
        self.assertEqual(sorted(set(races) - set(later)), list(range(1, 16)))


class BaseClassTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bcs = classdata.base_classes(SERVER_SRC, SHIPPED_DISABLED)
        cls.server = classdata.read_server_classes(SERVER_SRC)

    def test_the_spec_table(self):
        got = {bc.id: (bc.realm, bc.name, bc.finals, bc.races, tuple(STAT_NAMES[s] for s in bc.stats))
               for bc in self.bcs}
        self.assertEqual(got, SPEC)
        self.assertEqual([bc.id for bc in self.bcs], sorted(SPEC))

    def test_no_dead_ends(self):
        off = classdata.parse_disabled(SHIPPED_DISABLED) - {20}
        for bc in self.bcs:
            parent = self.server[bc.id].type_name
            finals = [c for c in self.server.values() if c.parent == parent and c.name in bc.finals]
            self.assertEqual(len(finals), len(bc.finals), bc.name)
            self.assertTrue(finals, bc.name)
            self.assertFalse({c.id for c in finals} & off, bc.name)
            for race in bc.races:
                self.assertTrue(any(race in c.races for c in finals), (bc.name, race))

    def test_classic_races_the_server_accepts_for_the_base_class(self):
        for bc in self.bcs:
            self.assertTrue(set(bc.races) <= set(range(1, 16)), bc.name)
            self.assertTrue(set(bc.races) <= set(self.server[bc.id].races), bc.name)

    def test_three_stats_each(self):
        for bc in self.bcs:
            self.assertEqual(len(bc.stats), 3, bc.name)
            self.assertEqual(len(set(bc.stats)), 3, bc.name)
            self.assertTrue(set(bc.stats) <= set(range(8)), bc.name)

    def test_names_come_from_the_client_table(self):
        for bc in self.bcs:
            self.assertEqual((bc.name_id, bc.name_ptr), classdata.CLIENT_NAMES[bc.id])

    def test_descriptions(self):
        for bc in self.bcs:
            flavor = classdata.FLAVOR[bc.id]
            self.assertRegex(flavor, r"^[A-Z][^.!?]*\.$")  # one sentence
            article = "an" if bc.finals[0][0] in "AEIOU" else "a"
            finals = bc.finals[0] if len(bc.finals) == 1 else ", ".join(bc.finals[:-1]) + " or " + bc.finals[-1]
            self.assertEqual(bc.description, f"{flavor} At level 5 your trainer makes you {article} {finals}.")
            self.assertTrue(bc.description.isascii(), bc.name)
        fighter = next(bc for bc in self.bcs if bc.id == 14)
        self.assertEqual(fighter.description,
                         "Albion's soldiers, trained in heavy armour and every kind of weapon. "
                         "At level 5 your trainer makes you an Armsman, Mercenary, Paladin or Reaver.")
        self.assertTrue(next(bc for bc in self.bcs if bc.id == 20).description.endswith("makes you a Necromancer."))

    def test_text_data_covers_exactly_the_listed_classes(self):
        ids = {bc.id for bc in self.bcs}
        self.assertEqual(set(classdata.FLAVOR), ids)
        self.assertEqual(set(classdata.STATS), ids)
        self.assertEqual(set(classdata.CLIENT_NAMES), ids)


class DisabledClassesTests(unittest.TestCase):
    def ids(self, disabled):
        return [bc.id for bc in classdata.base_classes(SERVER_SRC, disabled)]

    def test_disciple_is_listed_although_the_world_disables_it(self):
        self.assertIn(20, self.ids("20"))

    def test_a_disabled_base_class_is_not_listed(self):
        self.assertNotIn(14, self.ids(SHIPPED_DISABLED + ";14"))

    def test_a_base_class_without_an_enabled_full_class_is_not_listed(self):
        self.assertNotIn(20, self.ids(SHIPPED_DISABLED + ";12"))

    def test_races_follow_the_enabled_full_classes(self):
        acolyte = next(bc for bc in classdata.base_classes(SERVER_SRC, SHIPPED_DISABLED + ";6") if bc.id == 16)
        self.assertEqual((acolyte.finals, acolyte.races), (["Friar"], [BRI]))


class ServerSourceErrorTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.src = self.tmp.name
        copy_server(self.src)
        self.classes = os.path.join(self.src, "GameServer", "playerclasses")

    def tearDown(self):
        self.tmp.cleanup()

    def test_a_race_the_base_class_refuses_is_an_error(self):
        edit(os.path.join(self.classes, "base", "ClassFighter.cs"), b" PlayerRace.Saracen,", b"")
        with self.assertRaisesRegex(ValueError, r"ClassFighter: races \[4\]"):
            classdata.base_classes(self.src, SHIPPED_DISABLED)

    def test_a_base_name_that_disagrees_with_the_csharp_base_type_is_an_error(self):
        edit(os.path.join(self.classes, "albion", "ClassArmsman.cs"), b'"Armsman", "Fighter"', b'"Armsman", "Viking"')
        with self.assertRaisesRegex(ValueError, "ClassArmsman derives from ClassFighter but names base class 'Viking'"):
            classdata.base_classes(self.src, SHIPPED_DISABLED)

    def test_an_unreadable_class_file_is_an_error(self):
        with open(os.path.join(self.classes, "base", "ClassBroken.cs"), "w") as f:
            f.write("public class ClassBroken : CharacterClassBase { }\n")
        with self.assertRaisesRegex(ValueError, "ClassBroken.cs"):
            classdata.read_server_classes(self.src)


def sections(data):
    """(name, rva, virtual size, raw offset, raw size) per PE section, and the image base."""
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    count, opt_size = struct.unpack_from("<H", data, pe + 6)[0], struct.unpack_from("<H", data, pe + 20)[0]
    image_base = struct.unpack_from("<I", data, pe + 24 + 28)[0]
    table = pe + 24 + opt_size
    out = []
    for i in range(count):
        name, vsize, rva, raw_size, raw = struct.unpack_from("<8sIIII", data, table + 40 * i)
        out.append((name.rstrip(b"\0").decode(), rva, vsize, raw, raw_size))
    return out, image_base


def va_to_offset(data, va):
    """File offset of a VA: a local stand-in for pe.PE(data).offset(va) (client/patches/pe.py, Task 3)."""
    secs, image_base = sections(data)
    for _, rva, vsize, raw, raw_size in secs:
        if rva <= va - image_base < rva + min(vsize, raw_size):
            return raw + va - image_base - rva
    raise ValueError(f"VA {va:#x} is not in the file")


@unittest.skipUnless(CLIENT, "needs HDC_CLIENT_FILES (an OfflineDAoC 0.35 classic client folder)")
class RealGameDllTests(unittest.TestCase):
    NAME_TABLE = (0x44E769, 0x44F800)  # code that fills the class-name table (0x104B400 + id * 0x1E)
    STRING_LOOKUP = 0x7303FB
    REGISTER_CLASS = 0x5B01A3

    @classmethod
    def setUpClass(cls):
        with open(os.path.join(CLIENT, "game.dll"), "rb") as f:
            cls.data = f.read()
        if hashlib.sha256(cls.data).hexdigest() != GAME_DLL_SHA256:
            raise AssertionError("HDC_CLIENT_FILES/game.dll is not the OfflineDAoC 0.35 classic game.dll")
        cls.bcs = classdata.base_classes(SERVER_SRC, SHIPPED_DISABLED)

    def test_each_name_pointer_holds_the_class_name(self):
        for bc in self.bcs:
            at = va_to_offset(self.data, bc.name_ptr)
            self.assertEqual(self.data[at:self.data.index(b"\0", at)], bc.name.encode("ascii"), bc.id)

    def test_name_ids_and_pointers_match_the_clients_class_name_table(self):
        start = va_to_offset(self.data, self.NAME_TABLE[0])
        code = self.data[start:va_to_offset(self.data, self.NAME_TABLE[1])]
        for bc in self.bcs:
            store = b"\xba" + struct.pack("<I", 0x104B400 + bc.id * 0x1E)  # mov edx, &table[id]
            self.assertEqual(code.count(store), 1, bc.id)
            call = code.index(store) - 5  # call STRING_LOOKUP(name pointer, name id)
            self.assertEqual(code[call], 0xE8, bc.id)
            rel = struct.unpack_from("<i", code, call + 1)[0]
            self.assertEqual(self.NAME_TABLE[0] + call + 5 + rel, self.STRING_LOOKUP, bc.id)
            before, push_id, ptr = code[:call], b"\x68" + struct.pack("<I", bc.name_id), struct.pack("<I", bc.name_ptr)
            self.assertTrue(before.endswith(push_id + b"\x68" + ptr)                # push id; push ptr
                            or before.endswith(push_id + b"\xbe" + ptr + b"\x56")    # push id; mov esi,ptr; push esi
                            or (before.endswith(push_id + b"\x56") and b"\xbe" + ptr in before), bc.id)

    def test_the_client_registers_47_final_classes(self):
        secs, image_base = sections(self.data)
        _, rva, _, raw, raw_size = next(s for s in secs if s[0] == ".text")
        calls, i = 0, self.data.find(b"\xe8", raw, raw + raw_size - 4)
        while i != -1:
            rel = struct.unpack_from("<i", self.data, i + 1)[0]
            if image_base + rva + (i - raw) + 5 + rel == self.REGISTER_CLASS:
                calls += 1
            i = self.data.find(b"\xe8", i + 1, raw + raw_size - 4)
        self.assertEqual(calls, len(classdata.FINAL_CLASS_IDS))


@unittest.skipUnless(WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class RealWorldTests(unittest.TestCase):
    def test_the_worlds_disabled_classes_give_the_spec_table(self):
        value = classdata.read_disabled_classes(WORLD)
        self.assertEqual(classdata.parse_disabled(value) - {20}, classdata.parse_disabled(SHIPPED_DISABLED) - {20})
        self.assertEqual([bc.id for bc in classdata.base_classes(SERVER_SRC, value)], sorted(SPEC))


if __name__ == "__main__":
    unittest.main()
