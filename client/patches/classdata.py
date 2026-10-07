#!/usr/bin/env python3
"""Base-class data for the classic creation screen, derived from the server's class files.

Inputs:
- server_src: the server source folder that holds GameServer/ (in this repo: source/server).
  Read: GameServer/playerclasses/**/Class*.cs ([CharacterClass] attribute, C# base type,
  EligibleRaces), GameServer/Enums/eCharacterClass.cs, GameServer/Enums/eRace.cs and
  GameServer/gameobjects/PlayerRace.cs (realm and expansion of each race).
- disabled: the world's disabled_classes value (read_disabled_classes reads it from a world DB).
- src/base_classes.py: the hand-written FLAVOR sentences and highlighted STATS.

A full class's base class is its C# base type, as the server's ScriptMgr.FindCharacterBaseClass
resolves it (class ClassArmsman : ClassFighter); base classes derive from CharacterClassBase.
Standard library only.
"""
import argparse
import glob
import importlib.util
import os
import re
import sqlite3
import sys
from dataclasses import dataclass

HERE = os.path.dirname(os.path.abspath(__file__))

DISCIPLE = 20  # enabled by HearthDAoC's deploy/bin/world_fixes.py at every server start (#63, PR #64)
CLASSIC_EXPANSIONS = ("Classic", "ShroudedIsles")
HIDE_RACES = [16, 17, 18, 19, 20, 21]  # Half Ogre, Frostalf, Shar and the three Minotaurs
REALMS = {"Albion": 1, "Midgard": 2, "Hibernia": 3}
STAT_IDS = {"STR": 0, "CON": 1, "DEX": 2, "QUI": 3, "INT": 4, "PIE": 5, "EMP": 6, "CHA": 7}  # client stat order

# The 47 final classes that the classic client registers for its creation screen (16 Albion,
# 15 Midgard, 16 Hibernia; game.dll VA 0x5B0031). The cave hides all of them. They are the
# server's full classes except Sluaghbinder (63), which only the 0.34b edition knows.
FINAL_CLASS_IDS = [
    1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 19, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30,
    31, 32, 33, 34, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 55, 56, 58, 59, 60, 61, 62,
]

# Name string id and .rdata pointer of each base class's name, from the client's own class-name
# table (game.dll VA 0x44E769, OfflineDAoC 0.34 classic). The cave reuses them as they are.
CLIENT_NAMES = {
    14: (0x242, 0x940C7C),  # Fighter
    15: (0x243, 0x940C6C),  # Elementalist
    16: (0x244, 0x940C64),  # Acolyte
    17: (0x81D, 0x940C5C),  # Rogue (Albion)
    18: (0x246, 0x940C54),  # Mage
    20: (0x3D0, 0x940C40),  # Disciple
    35: (0x251, 0x940BAC),  # Viking
    36: (0x252, 0x940BA4),  # Mystic
    37: (0x253, 0x940B9C),  # Seer
    38: (0x245, 0x940C5C),  # Rogue (Midgard)
    51: (0x25F, 0x940B14),  # Magician
    52: (0x260, 0x940B08),  # Guardian
    53: (0x261, 0x940AFC),  # Naturalist
    54: (0x262, 0x940AF4),  # Stalker
    57: (0x42F, 0x940AD4),  # Forester
}


@dataclass
class ServerClass:
    id: int
    name: str         # [CharacterClass] name, e.g. "Armsman"
    base_name: str    # [CharacterClass] base name, e.g. "Fighter"
    type_name: str    # C# class, e.g. "ClassArmsman"
    parent: str       # C# base type, e.g. "ClassFighter" or "CharacterClassBase"
    races: list[int]  # EligibleRaces as race ids, in source order


@dataclass
class BaseClass:
    id: int
    realm: int
    name: str
    name_id: int
    name_ptr: int
    finals: list[str]
    races: list[int]
    stats: list[int]
    description: str


def _load_text():
    path = os.path.join(HERE, "src", "base_classes.py")
    spec = importlib.util.spec_from_file_location("hearthdaoc_base_classes", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


_TEXT = _load_text()
FLAVOR = _TEXT.FLAVOR
STATS = _TEXT.STATS


def parse_disabled(value: str) -> set[int]:
    """Class ids in a disabled_classes value such as '20;33;34;39;58-62'.

    Ranges are expanded as the property's description says (either order). Commas are accepted as
    well as semicolons, so nothing the owner may have typed is missed. Other tokens never match a
    class on the server either and are ignored.
    """
    ids = set()
    for token in re.split(r"[;,]", value or ""):
        token = token.strip()
        m = re.fullmatch(r"(\d+)\s*-\s*(\d+)", token)
        if m:
            lo, hi = sorted((int(m.group(1)), int(m.group(2))))
            ids.update(range(lo, hi + 1))
        elif token.isdigit():
            ids.add(int(token))
    return ids


def read_disabled_classes(world_db) -> str:
    """The world's disabled_classes server property ('' when the row is missing). Opens read-only."""
    conn = sqlite3.connect(f"file:{os.path.abspath(world_db)}?mode=ro", uri=True)
    try:
        row = conn.execute("SELECT Value FROM ServerProperty WHERE `Key`='disabled_classes'").fetchone()
    finally:
        conn.close()
    return row[0] if row else ""


def _read(path):
    with open(path, encoding="utf-8-sig") as f:
        text = f.read()
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def _enum(path, name):
    m = re.search(r"enum\s+" + name + r"\b[^{]*\{(.*?)\}", _read(path), re.S)
    if not m:
        raise ValueError(f"{path}: enum {name} not found")
    return {k: int(v) for k, v in re.findall(r"(\w+)\s*=\s*(\d+)", m.group(1))}


def read_player_races(server_src) -> dict[int, tuple[int, str]]:
    """Race id -> (realm id, expansion name), from GameServer/gameobjects/PlayerRace.cs."""
    gs = os.path.join(server_src, "GameServer")
    race_ids = _enum(os.path.join(gs, "Enums", "eRace.cs"), "eRace")
    found = re.findall(r"new\s+PlayerRace\(\s*eRace\.(\w+)\s*,\s*eRealm\.(\w+)\s*,\s*eDAoCExpansion\.(\w+)",
                       _read(os.path.join(gs, "gameobjects", "PlayerRace.cs")))
    if not found:
        raise ValueError("PlayerRace.cs: no races found")
    return {race_ids[race]: (REALMS[realm], expansion) for race, realm, expansion in found}


def read_server_classes(server_src) -> dict[int, ServerClass]:
    """Every player class in GameServer/playerclasses, by class id."""
    gs = os.path.join(server_src, "GameServer")
    class_ids = _enum(os.path.join(gs, "Enums", "eCharacterClass.cs"), "eCharacterClass")
    race_ids = _enum(os.path.join(gs, "Enums", "eRace.cs"), "eRace")
    files = sorted(glob.glob(os.path.join(gs, "playerclasses", "**", "Class*.cs"), recursive=True))
    if not files:
        raise ValueError(f"no Class*.cs under {os.path.join(gs, 'playerclasses')}")
    classes = {}
    for path in files:
        text = _read(path)
        attr = re.search(r'\[CharacterClass(?:Attribute)?\(\s*\(int\)\s*eCharacterClass\.(\w+)\s*,'
                         r'\s*"([^"]*)"\s*,\s*"([^"]*)"', text)
        decl = re.search(r"\bclass\s+(\w+)\s*:\s*(\w+)", text)
        races = re.search(r"EligibleRaces\s*=>\s*new\s*(?:List<PlayerRace>\s*)?\(\s*\)\s*\{(.*?)\}", text, re.S)
        if not (attr and decl and races):
            raise ValueError(f"{path}: [CharacterClass], class declaration or EligibleRaces not found")
        cls = ServerClass(id=class_ids[attr.group(1)], name=attr.group(2), base_name=attr.group(3),
                          type_name=decl.group(1), parent=decl.group(2),
                          races=[race_ids[r] for r in re.findall(r"PlayerRace\.(\w+)", races.group(1))])
        if cls.id in classes:
            raise ValueError(f"{path}: class id {cls.id} defined twice")
        classes[cls.id] = cls
    return classes


def _article(word):
    return "an" if word[0] in "AEIOU" else "a"


def _join(names):
    return names[0] if len(names) == 1 else ", ".join(names[:-1]) + " or " + names[-1]


def base_classes(server_src: str, disabled: str) -> list[BaseClass]:
    """The base classes the classic creation screen lists, sorted by id.

    A base class is listed when it isn't disabled and leads to at least one enabled full class the
    client knows (FINAL_CLASS_IDS). Disciple (20) counts as enabled whatever `disabled` says:
    world_fixes.py removes it at every server start. Its races are the union of those full classes'
    EligibleRaces, limited to the classic and Shrouded Isles races; they must also be in the base
    class's own EligibleRaces, which the server checks at creation (ValueError otherwise).
    """
    classes = read_server_classes(server_src)
    races = read_player_races(server_src)
    classic = {r for r, (_, expansion) in races.items() if expansion in CLASSIC_EXPANSIONS}
    off = parse_disabled(disabled) - {DISCIPLE}
    by_type = {c.type_name: c for c in classes.values()}
    out = []
    for base in sorted(classes.values(), key=lambda c: c.id):
        if base.parent != "CharacterClassBase" or base.id in off:
            continue
        finals = []
        for c in classes.values():
            if by_type.get(c.parent) is not base:
                continue
            if c.base_name != base.name:
                raise ValueError(f"{c.type_name} derives from {base.type_name} but names base class {c.base_name!r}")
            if c.id in FINAL_CLASS_IDS and c.id not in off:
                finals.append(c)
        if not finals:
            continue
        offered = sorted(set().union(*(c.races for c in finals)) & classic)
        missing = sorted(set(offered) - set(base.races))
        if missing:
            raise ValueError(f"{base.type_name}: races {missing} can become a full class but the server "
                             f"refuses them for the base class (not in its EligibleRaces)")
        realms = {races[r][0] for r in base.races}
        if len(realms) != 1:
            raise ValueError(f"{base.type_name}: EligibleRaces span realms {sorted(realms)}")
        if base.id not in FLAVOR or base.id not in STATS or base.id not in CLIENT_NAMES:
            raise ValueError(f"base class {base.id} ({base.name}) needs FLAVOR and STATS in src/base_classes.py "
                             f"and CLIENT_NAMES in classdata.py")
        names = sorted(c.name for c in finals)
        name_id, name_ptr = CLIENT_NAMES[base.id]
        out.append(BaseClass(
            id=base.id, realm=realms.pop(), name=base.name, name_id=name_id, name_ptr=name_ptr,
            finals=names, races=offered, stats=[STAT_IDS[s] for s in STATS[base.id]],
            description=f"{FLAVOR[base.id]} At level 5 your trainer makes you {_article(names[0])} {_join(names)}."))
    return out


def main(argv=None):
    ap = argparse.ArgumentParser(description="Print the base classes the classic creation screen lists.")
    ap.add_argument("--server-src", required=True, help="server source folder holding GameServer/ (source/server)")
    group = ap.add_mutually_exclusive_group(required=True)
    group.add_argument("--world-db", help="world database to read disabled_classes from (opened read-only)")
    group.add_argument("--disabled", help="a disabled_classes value, e.g. '20;33;34;39;58-62'")
    a = ap.parse_args(argv)
    disabled = a.disabled if a.disabled is not None else read_disabled_classes(a.world_db)
    names = {v: k for k, v in STAT_IDS.items()}
    for bc in base_classes(a.server_src, disabled):
        print(f"{bc.id:2} realm {bc.realm} {bc.name}: {', '.join(bc.finals)} | races {bc.races} | "
              f"{'/'.join(names[s] for s in bc.stats)}\n   {bc.description}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
