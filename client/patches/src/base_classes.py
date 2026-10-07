"""Hand-written text for the classic creation screen's base classes (English only).

classdata.py adds the rest from the server's class files: which base classes are listed, the
full classes each leads to, and the races. Each description shown in game is
FLAVOR[id] + " At level 5 your trainer makes you a(n) <full classes>."
Keep every text plain ASCII and FLAVOR to one sentence.

STATS are the three highlighted (primary) stats per base class. With auto-assign gone they
only colour the stats on the attributes window; they assign nothing.
"""

FLAVOR = {
    # Albion
    14: "Albion's soldiers, trained in heavy armour and every kind of weapon.",
    15: "Albion's students of the elements, who learn to call down earth, ice, fire and air.",
    16: "Albion's faithful, who serve the Church with healing prayers and a sturdy staff.",
    17: "Albion's quick and quiet, who live by the hidden blade, the bow and the song.",
    18: "Albion's scholars of the arcane, who bend body, mind, matter and spirit to their will.",
    20: "Albion's servants of Arawn, lord of the underworld, who learn to command the dead.",
    # Midgard
    35: "Midgard's warriors, raised in the shield wall with axe, sword and hammer.",
    36: "Midgard's seekers of hidden lore, who call on runes, spirits and the bones of the dead.",
    37: "Midgard's faithful, blessed by the gods with healing and protective magic.",
    38: "Midgard's hunters and assassins, who strike from the shadows or from afar.",
    # Hibernia
    51: "Hibernia's spellcasters, schooled in the magic of light, mana and the mind.",
    52: "Hibernia's fighters, trained to hold the line with blade, hammer and shield.",
    53: "Hibernia's keepers of the land, who draw on nature's magic to heal and protect.",
    54: "Hibernia's hunters and assassins, who move unseen through forest and shadow.",
    57: "Hibernia's protectors of the deep forest, who draw power from living wood and growing things.",
}

STATS = {
    14: ("STR", "CON", "DEX"),
    15: ("INT", "DEX", "QUI"),
    16: ("PIE", "CON", "DEX"),
    17: ("DEX", "QUI", "STR"),
    18: ("INT", "DEX", "QUI"),
    20: ("INT", "DEX", "QUI"),
    35: ("STR", "CON", "DEX"),
    36: ("PIE", "DEX", "QUI"),
    37: ("PIE", "CON", "DEX"),
    38: ("DEX", "QUI", "STR"),
    51: ("INT", "DEX", "QUI"),
    52: ("STR", "CON", "DEX"),
    53: ("EMP", "DEX", "CON"),
    54: ("DEX", "QUI", "STR"),
    57: ("INT", "DEX", "CON"),
}
