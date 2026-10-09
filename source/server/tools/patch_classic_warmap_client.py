"""Classic frontier war map for the isolated DAoC 1.127 client (owner 2026-10-08: "the keep icons on the realm war
map aren't drawn where the keeps actually are in the frontier").

The 1.127 client draws the realm war map (KEEPS button) for New Frontiers. Its window XML already puts the four
zone tiles where the classic frontier zones lie, but game.dll hardcodes every icon: for each realm page a block of
"mov dword ptr [esi+field], imm32" sets each keep's tile index and its pixel offset inside that tile (New Frontiers
positions), and one lookup maps a zone id to a tile (New Frontiers zone ids 163-178 only). This patch rewrites, in
place and without changing any instruction length:

  * every keep (7 per realm), relic keep (power, strength) and border keep icon of the three realm pages: the tile
    index (by picking the register that already holds that value) and the x/y offset (the immediate), from the
    database's classic coordinates (a 65536-unit zone is a 128-pixel tile: 512 units per pixel). Two Hibernia
    coordinates that the original code loads from one shared register are written by two of the unused milegate
    writes later in the same routine;
  * the zone id -> tile lookup (0x5355F4): the classic frontier zone ids, so the player's own location pip shows on
    the war map in the classic frontier;
  * the world -> pixel divisor of that pip (500 -> 512: classic zones are 65536 units wide).

Pair it with build_classic_warmap_textures.py (the tile images) and the war map XML change (milegate icons removed:
the classic frontier has none). Research notes: "CLIENT REVERSE ENGINEERING NOTES.md" in the CLAUDE VERSION folder.
Writes a new file and never replaces game.dll; deploy it with the client closed.
"""

import argparse
from pathlib import Path
import struct

IMAGE_BASE = 0x400000

# (address, original bytes, patched bytes, what) - generated from the original realm blocks by the research script
PATCHES = [
    (0x5362FA, 'c70326000000', 'c7036c000000', 'alb keep0 Caer Benowyc'),
    (0x536314, 'c786246c010015000000', 'c786246c010074000000', 'alb keep0 Caer Benowyc'),
    (0x536324, 'c786286c010078000000', 'c786286c010026000000', 'alb keep1 Caer Berkstead'),
    (0x53632E, 'c7862c6c010027000000', 'c7862c6c01004b000000', 'alb keep1 Caer Berkstead'),
    (0x536338, '898e5c6c0100', '89965c6c0100', 'alb keep1 Caer Berkstead tile 3 (edx)'),
    (0x536344, 'c786306c01001d000000', 'c786306c01006d000000', 'alb keep2 Caer Erasleigh'),
    (0x53634E, 'c786346c010071000000', 'c786346c010024000000', 'alb keep2 Caer Erasleigh'),
    (0x536358, '898e606c0100', '8996606c0100', 'alb keep2 Caer Erasleigh tile 3 (edx)'),
    (0x536364, 'c786386c010063000000', 'c786386c010072000000', 'alb keep3 Caer Boldiam'),
    (0x53636E, 'c7863c6c010025000000', 'c7863c6c01006a000000', 'alb keep3 Caer Boldiam'),
    (0x536378, '899e646c0100', '8996646c0100', 'alb keep3 Caer Boldiam tile 3 (edx)'),
    (0x536384, 'c786406c010032000000', 'c786406c010021000000', 'alb keep4 Caer Sursbrooke'),
    (0x53638E, 'c786446c010058000000', 'c786446c01001a000000', 'alb keep4 Caer Sursbrooke'),
    (0x536398, '899e686c0100', '8996686c0100', 'alb keep4 Caer Sursbrooke tile 3 (edx)'),
    (0x5363A4, 'c786486c010055000000', 'c786486c01005b000000', 'alb keep5 Caer Hurbury'),
    (0x5363AE, 'c7864c6c010038000000', 'c7864c6c010042000000', 'alb keep5 Caer Hurbury'),
    (0x5363B8, '89966c6c0100', '89866c6c0100', 'alb keep5 Caer Hurbury tile 2 (eax)'),
    (0x5363BE, 'c786506c010009000000', 'c786506c01001d000000', 'alb keep6 Caer Renaris'),
    (0x5363C8, 'c786546c010064000000', 'c786546c010017000000', 'alb keep6 Caer Renaris'),
    (0x5363D2, '8986706c0100', '899e706c0100', 'alb keep6 Caer Renaris tile 0 (ebx)'),
    (0x536440, 'c7860c6f01002a000000', 'c7860c6f01000e000000', 'alb relic0 Castle Myrddin'),
    (0x53644A, 'c786106f01006d000000', 'c786106f01000c000000', 'alb relic0 Castle Myrddin'),
    (0x536454, '89961c6f0100', '89861c6f0100', 'alb relic0 Castle Myrddin tile 2 (eax)'),
    (0x53645A, 'c786146f010066000000', 'c786146f010047000000', 'alb relic1 Castle Excalibur'),
    (0x536464, 'c786186f010042000000', 'c786186f010018000000', 'alb relic1 Castle Excalibur'),
    (0x53646E, '8986206f0100', '899e206f0100', 'alb relic1 Castle Excalibur tile 0 (ebx)'),
    (0x536474, 'c786246f010078000000', 'c786246f010038000000', 'alb border0 Snowdonia Fortress'),
    (0x53647E, 'c786286f010073000000', 'c786286f01006c000000', 'alb border0 Snowdonia Fortress'),
    (0x536488, '8996346f0100', '8986346f0100', 'alb border0 Snowdonia Fortress tile 2 (eax)'),
    (0x53648E, 'c7862c6f01003e000000', 'c7862c6f010027000000', 'alb border1 Castle Sauvage'),
    (0x536498, 'c786306f01007b000000', 'c786306f010075000000', 'alb border1 Castle Sauvage'),
    (0x5364A2, '8986386f0100', '899e386f0100', 'alb border1 Castle Sauvage tile 0 (ebx)'),
    (0x5368A4, 'c70312000000', 'c70373000000', 'mid keep0 Bledmeer Faste'),
    (0x5368BE, 'c786246c01005e000000', 'c786246c010014000000', 'mid keep0 Bledmeer Faste'),
    (0x5368C8, '899e586c0100', '8986586c0100', 'mid keep0 Bledmeer Faste tile 2 (eax)'),
    (0x5368D4, 'c786286c010015000000', 'c786286c01002a000000', 'mid keep1 Nottmoor Faste'),
    (0x5368DE, 'c7862c6c01000a000000', 'c7862c6c010029000000', 'mid keep1 Nottmoor Faste'),
    (0x5368E8, '899e5c6c0100', '89965c6c0100', 'mid keep1 Nottmoor Faste tile 3 (edx)'),
    (0x5368F4, 'c786306c01006a000000', 'c786306c01002e000000', 'mid keep2 Hlidskialf Faste'),
    (0x5368FE, 'c786346c010064000000', 'c786346c010060000000', 'mid keep2 Hlidskialf Faste'),
    (0x536908, '8986606c0100', '8996606c0100', 'mid keep2 Hlidskialf Faste tile 3 (edx)'),
    (0x536914, 'c786386c010016000000', 'c786386c01005a000000', 'mid keep3 Blendrake Faste'),
    (0x53691E, 'c7863c6c010060000000', 'c7863c6c01002c000000', 'mid keep3 Blendrake Faste'),
    (0x536928, '8986646c0100', '8996646c0100', 'mid keep3 Blendrake Faste tile 3 (edx)'),
    (0x536934, 'c786406c010027000000', 'c786406c010065000000', 'mid keep4 Glenlock Faste'),
    (0x53693E, 'c786446c01001f000000', 'c786446c010065000000', 'mid keep4 Glenlock Faste'),
    (0x536948, '8986686c0100', '8996686c0100', 'mid keep4 Glenlock Faste tile 3 (edx)'),
    (0x536954, 'c786486c010063000000', 'c786486c010025000000', 'mid keep5 Fensalir Faste'),
    (0x53695E, 'c7864c6c010065000000', 'c7864c6c010063000000', 'mid keep5 Fensalir Faste'),
    (0x53696E, 'c786506c01003c000000', 'c786506c010055000000', 'mid keep6 Arvakr Faste'),
    (0x536978, 'c786546c010039000000', 'c786546c010011000000', 'mid keep6 Arvakr Faste'),
    (0x536982, '8996706c0100', '899e706c0100', 'mid keep6 Arvakr Faste tile 0 (ebx)'),
    (0x5369F0, 'c7860c6f010073000000', 'c7860c6f01002b000000', 'mid relic0 Grallarhorn Faste'),
    (0x5369FA, 'c786106f010056000000', 'c786106f01004c000000', 'mid relic0 Grallarhorn Faste'),
    (0x536A04, '89961c6f0100', '899e1c6f0100', 'mid relic0 Grallarhorn Faste tile 0 (ebx)'),
    (0x536A0A, 'c786146f01002a000000', 'c786146f010064000000', 'mid relic1 Mjollner Faste'),
    (0x536A14, 'c786186f010010000000', 'c786186f010028000000', 'mid relic1 Mjollner Faste'),
    (0x536A24, 'c786246f010079000000', 'c786246f01005f000000', 'mid border0 Vindsaul Faste'),
    (0x536A2E, 'c786286f010006000000', 'c786286f01007c000000', 'mid border0 Vindsaul Faste'),
    (0x536A38, '8996346f0100', '899e346f0100', 'mid border0 Vindsaul Faste tile 0 (ebx)'),
    (0x536A3E, 'c7862c6f01007e000000', 'c7862c6f010059000000', 'mid border1 Svasud Faste'),
    (0x536A48, 'c786306f010025000000', 'c786306f01007b000000', 'mid border1 Svasud Faste'),
    (0x536E77, 'c786206c01005c000000', 'c786206c010010000000', 'hib keep0 Dun Crauchon'),
    (0x536E81, 'c786246c010013000000', 'c786246c01003e000000', 'hib keep0 Dun Crauchon'),
    (0x536E97, 'c786286c010009000000', 'c786286c010067000000', 'hib keep1 Dun Crimthain'),
    (0x536EAB, '898e5c6c0100', '89865c6c0100', 'hib keep1 Dun Crimthain tile 2 (eax)'),
    (0x536EB7, 'c786306c010063000000', 'c786306c010023000000', 'hib keep2 Dun Bolg'),
    (0x536F37, 'c786ac6c01002e000000', 'c786346c01001d000000', 'hib keep2 Dun Bolg (field 0x16c34 was set from ebp; reuses an unused milegate write)'),
    (0x536EC1, '898e606c0100', '8986606c0100', 'hib keep2 Dun Bolg tile 2 (eax)'),
    (0x536ECD, 'c786386c010020000000', 'c786386c010018000000', 'hib keep3 Dun nGed'),
    (0x536ED7, 'c7863c6c010027000000', 'c7863c6c01005d000000', 'hib keep3 Dun nGed'),
    (0x536F41, 'c786b06c010022000000', 'c786406c01005d000000', 'hib keep4 Dun Da Behnn (field 0x16c40 was set from ebp; reuses an unused milegate write)'),
    (0x536EED, 'c786446c01004e000000', 'c786446c010064000000', 'hib keep4 Dun Da Behnn'),
    (0x536F03, 'c786486c01002c000000', 'c786486c01003c000000', 'hib keep5 Dun Scathaig'),
    (0x536F0D, 'c7864c6c010057000000', 'c7864c6c010031000000', 'hib keep5 Dun Scathaig'),
    (0x536F1D, 'c786506c010066000000', 'c786506c010062000000', 'hib keep6 Dun Ailinne'),
    (0x536F27, 'c786546c010064000000', 'c786546c010056000000', 'hib keep6 Dun Ailinne'),
    (0x536F9F, 'c7860c6f010058000000', 'c7860c6f01001f000000', 'hib relic0 Dun Dagda'),
    (0x536FA9, 'c786106f01006e000000', 'c786106f01005c000000', 'hib relic0 Dun Dagda'),
    (0x536FB9, 'c786146f010018000000', 'c786146f010039000000', 'hib relic1 Dun Lamfhota'),
    (0x536FC3, 'c786186f010044000000', 'c786186f010029000000', 'hib relic1 Dun Lamfhota'),
    (0x536FD3, 'c786246f01000b000000', 'c786246f010047000000', 'hib border0 Druim Cain'),
    (0x536FDD, 'c786286f010075000000', 'c786286f01007c000000', 'hib border0 Druim Cain'),
    (0x536FED, 'c7862c6f010048000000', 'c7862c6f01001b000000', 'hib border1 Druim Ligen'),
    (0x536FF7, 'c786306f01007d000000', 'c786306f01007c000000', 'hib border1 Druim Ligen')
]

LOOKUP = 0x5355F4          # zone id -> tile index (eax = realm page 0 Hib / 1 Mid / 2 Alb / 3 Agramon, ecx = zone id)
LOOKUP_END = 0x535652
LOOKUP_ORIGINAL = bytes.fromhex('83e800743f48742648740e48754881f9a3000000754033c0c38bc12daf00000074384874054874eeeb136a0258c38bc12da700000074df4874f048741d48751633c040c38bc12dab00000074f34874da4874c348740483c8ffc36a0358c3')
# tiles 0..3 of each page are the window XML controls 1000..1003 (positions unchanged): the classic zone shown at each
TILE_ZONES = {0: (211, 214, 212, 210),               # Hibernia: Cruachan Gorge, Emain Macha, Breifine, Mount Collory
              1: (112, 111, 115, 113),               # Midgard: Yggdra Forest, Uppland, Odin's Gate, Jamtland Mountains
              2: (11, 15, 12, 14),                   # Albion: Forest Sauvage, Hadrian's Wall, Snowdonia, Pennine Mountains
              3: (0xA3, 0xFFFE, 0xFFFE, 0xFFFE)}     # Agramon (as before)
DIVISOR = 0x538FE5         # mov ebx, 500 in the pip placement (realm pages; Agramon uses 250 and is left alone)


def lookup_code():
    """Table-driven replacement of the lookup, same entry point and register contract (eax in/out, ecx kept)."""
    table = LOOKUP + 0x24
    code = bytearray()
    code += bytes([0x56])                                   # push esi
    code += bytes([0xBE]) + struct.pack('<I', table)        # mov esi, table
    code += bytes([0x83, 0xF8, 0x03])                       # cmp eax, 3
    code += bytes([0x77, 0x11])                             # ja not_found
    code += bytes([0x8D, 0x34, 0xC6])                       # lea esi, [esi+eax*8]
    code += bytes([0x33, 0xC0])                             # xor eax, eax
    code += bytes([0x66, 0x3B, 0x0C, 0x46])                 # next: cmp cx, word ptr [esi+eax*2]
    code += bytes([0x74, 0x09])                             # je found
    code += bytes([0x40])                                   # inc eax
    code += bytes([0x83, 0xF8, 0x04])                       # cmp eax, 4
    code += bytes([0x72, 0xF4])                             # jb next
    code += bytes([0x83, 0xC8, 0xFF])                       # not_found: or eax, -1
    code += bytes([0x5E])                                   # found: pop esi
    code += bytes([0xC3])                                   # ret
    code += bytes([0xCC]) * (0x24 - len(code))
    for page in range(4):
        code += struct.pack('<4H', *TILE_ZONES[page])
    code += bytes([0xCC]) * (LOOKUP_END - LOOKUP - len(code))
    assert len(code) == LOOKUP_END - LOOKUP
    return bytes(code)


def u32(data, offset):
    return struct.unpack_from('<I', data, offset)[0]


def sections(data):
    pe = u32(data, 0x3C)
    assert data[pe:pe + 4] == b'PE' + bytes(2), 'Not a PE file'
    count = struct.unpack_from('<H', data, pe + 6)[0]
    opt = pe + 24
    assert u32(data, opt + 28) == IMAGE_BASE, 'Unexpected image base'
    table = opt + struct.unpack_from('<H', data, pe + 20)[0]
    return opt, [struct.unpack_from('<8sIIIIIIHHI', data, table + i * 40) for i in range(count)]


def file_offset(secs, address):
    rva = address - IMAGE_BASE
    for s in secs:
        if s[2] <= rva < s[2] + max(s[1], s[3]):
            return s[4] + rva - s[2]
    raise ValueError(f'Address {address:#x} outside file')


def checksum(data, opt):
    struct.pack_into('<I', data, opt + 64, 0)
    total = 0
    for i in range(0, len(data), 2):
        total += int.from_bytes(data[i:i + 2], 'little')
        total = (total & 0xffff) + (total >> 16)
    total = (total & 0xffff) + (total >> 16)
    struct.pack_into('<I', data, opt + 64, total + len(data))


def build_patch(original):
    data = bytearray(original)
    opt, secs = sections(data)
    o = file_offset(secs, LOOKUP)
    assert bytes(data[o:o + LOOKUP_END - LOOKUP]) != lookup_code(), 'Already patched'
    assert bytes(data[o:o + LOOKUP_END - LOOKUP]) == LOOKUP_ORIGINAL, 'Unexpected zone lookup; refusing patch'
    d = file_offset(secs, DIVISOR)
    assert bytes(data[d:d + 5]) == bytes.fromhex('bbf4010000'), 'Unexpected pip divisor; refusing patch'
    for address, old, new, why in PATCHES:
        f = file_offset(secs, address)
        assert bytes(data[f:f + len(old) // 2]).hex() == old, f'Unexpected bytes at {address:#x} ({why}); refusing patch'
    for address, old, new, why in PATCHES:
        f = file_offset(secs, address)
        data[f:f + len(new) // 2] = bytes.fromhex(new)
    data[o:o + LOOKUP_END - LOOKUP] = lookup_code()
    data[d:d + 5] = bytes.fromhex('bb00020000')
    checksum(data, opt)
    return bytes(data)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    assert args.source.resolve() != args.output.resolve(), 'Output must be a separate file'
    assert not args.output.exists(), 'Output already exists'
    args.output.write_bytes(build_patch(args.source.read_bytes()))
    print(f'Prepared the classic war map client ({len(PATCHES)} icon patches, zone lookup, pip scale): {args.output}')
