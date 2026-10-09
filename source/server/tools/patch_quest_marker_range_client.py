"""Red map dots for every reserved quest marker ID in the isolated DAoC 1.127 client (owner 2026-10-07).

patch_bounty_map_client.py made only 0xFFFE0001 (bounty) use the red quest_waypoint dot; every other relic-marker ID
fell back to the green group_location dot, so reputation (0xFFFE0002), Sluaghbinder epic (0xFFFE0003) and classic
quest markers (0xFFFE0004 + quest id - 20000) were drawn green like the bot dots. This rewrites the .bounty hook so
the whole reserved range 0xFFFE0001..0xFFFEFFFF is red. Bot markers (0x80000000 | database id) stay green.

Writes a new file and never replaces game.dll; deploy it with the client closed.
"""

import argparse
from pathlib import Path
import struct

IMAGE_BASE = 0x400000
HOOK = 0x530D71
CONTINUE = 0x530D76
GREEN_TEMPLATE = 0x953570
FIRST_ID = 0xFFFE0001
LAST_ID = 0xFFFEFFFF
WAYPOINT_TEMPLATE = b'quest_waypoint\0'


def u32(data, offset):
    return struct.unpack_from('<I', data, offset)[0]


def hook_code(address):
    # push eax; lea eax,[ebx-FIRST_ID]; cmp eax,LAST_ID-FIRST_ID; pop eax (flags kept); ja default;
    # push red template; jmp continuation; default: push original green; jmp continuation; template string.
    code = bytearray()
    code += b'\x50'
    code += b'\x8d\x83' + struct.pack('<I', (-FIRST_ID) & 0xFFFFFFFF)
    code += b'\x3d' + struct.pack('<I', LAST_ID - FIRST_ID)
    code += b'\x58'
    code += b'\x77\x0a'
    red_push = len(code)
    code += b'\x68\0\0\0\0'
    code += b'\xe9' + struct.pack('<i', CONTINUE - (address + len(code) + 5))
    assert len(code) - red_push == 10
    code += b'\x68' + struct.pack('<I', GREEN_TEMPLATE)
    code += b'\xe9' + struct.pack('<i', CONTINUE - (address + len(code) + 5))
    template = address + len(code)
    code[red_push + 1:red_push + 5] = struct.pack('<I', template)
    code += WAYPOINT_TEMPLATE
    return bytes(code)


def red(marker_id):
    """What the patched hook decides, for the tests."""
    return ((marker_id - FIRST_ID) & 0xFFFFFFFF) <= LAST_ID - FIRST_ID


def build_patch(original):
    data = bytearray(original)
    pe = u32(data, 0x3C)
    assert data[pe:pe + 4] == b'PE\0\0', 'Not a PE file'
    count = struct.unpack_from('<H', data, pe + 6)[0]
    opt = pe + 24
    assert u32(data, opt + 28) == IMAGE_BASE, 'Unexpected image base'
    table = opt + struct.unpack_from('<H', data, pe + 20)[0]
    headers = {}
    for i in range(count):
        section = struct.unpack_from('<8sIIIIIIHHI', data, table + i * 40)
        headers[section[0].rstrip(b'\0')] = (table + i * 40, section)
    assert b'.bounty' in headers, 'Bounty marker patch missing; run patch_bounty_map_client.py first'
    header, (name, vsize, rva, raw_size, raw, *_rest) = headers[b'.bounty']
    address = IMAGE_BASE + rva
    old = bytes(data[raw:raw + vsize])
    assert old[:2] == b'\x81\xfb' and u32(old, 2) == 0xFFFE0001, 'Unexpected .bounty code; refusing patch'
    jump = data[0:0]
    for sname, (_, s) in headers.items():
        if s[2] <= HOOK - IMAGE_BASE < s[2] + s[3]:
            off = s[4] + HOOK - IMAGE_BASE - s[2]
            jump = data[off:off + 5]
    assert jump == b'\xe9' + struct.pack('<i', address - CONTINUE), 'Hook does not jump to .bounty; refusing patch'

    code = hook_code(address)
    assert len(code) <= raw_size, 'No room in .bounty'
    data[raw:raw + raw_size] = code + bytes(raw_size - len(code))
    struct.pack_into('<I', data, header + 8, len(code))
    section_alignment = u32(data, opt + 32)
    end = max(s[2] + max(s[1], s[3]) for _, s in headers.values())
    end = max(end, rva + len(code))
    struct.pack_into('<I', data, opt + 56, (end + section_alignment - 1) // section_alignment * section_alignment)
    struct.pack_into('<I', data, opt + 64, 0)
    checksum = 0
    for i in range(0, len(data), 2):
        checksum += int.from_bytes(data[i:i + 2], 'little')
        checksum = (checksum & 0xffff) + (checksum >> 16)
    checksum = (checksum & 0xffff) + (checksum >> 16)
    struct.pack_into('<I', data, opt + 64, checksum + len(data))
    return bytes(data), address


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    assert args.source.resolve() != args.output.resolve(), 'Output must be a separate file'
    assert not args.output.exists(), 'Output already exists'
    patched, hook_target = build_patch(args.source.read_bytes())
    args.output.write_bytes(patched)
    print(f'Prepared quest marker range hook at {hook_target:#x}: {args.output}')
