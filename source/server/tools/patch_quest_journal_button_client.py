"""Make the Quest Journal's QUEST GUIDE button work in the isolated DAoC 1.127 client (owner 2026-10-08: the button
did nothing while typing /task opened the guide).

The Quest Journal window has its own click handler (0x5336FF). It forwards exactly one button event to the game,
RemoveQuest (event 0x21D); every other button event in that window is dropped, which is why the old BOUNTY MAP
button (ToggleMap) and the QUEST GUIDE button (Task) never did anything. The global Task event itself is fine: its
handler runs the slash command "task" exactly like typing /task ("&%s" -> the server's /task -> Quest Guide).

This patch redirects the RemoveQuest test into a few instructions in the spare space of the .bounty section, which
forward the Task event (0x68) down the same path as RemoveQuest. Every other journal click behaves exactly as
before. The button is the journal XML's QUEST GUIDE button (OnClickEvent Task). Research notes:
"CLIENT REVERSE ENGINEERING NOTES.md" in the CLAUDE VERSION folder.

Writes a new file and never replaces game.dll; deploy it with the client closed.
"""

import argparse
from pathlib import Path
import struct

IMAGE_BASE = 0x400000
HOOK = 0x53370B                 # mov eax, 21Dh / cmp [ecx+40h], eax / push edi / jne 53372Eh
HOOK_ORIGINAL = bytes.fromhex('b81d020000394140577518')
POST = 0x533716                 # push 0 / push 0 / push eax / push [ecx+38h] / push [esi+18h] / call 4E1719h
OTHER = 0x53372E                # the handler's other clicks (quest list, checkbox)
CAVE_OFFSET = 0x40              # inside .bounty, after the quest marker colour code
REMOVE_QUEST, TASK = 0x21D, 0x68


def u32(data, offset):
    return struct.unpack_from('<I', data, offset)[0]


def cave_code(address):
    code = bytearray()
    code += bytes([0x8B, 0x41, 0x40])                                 # mov eax, [ecx+40h]  (the button's event)
    code += bytes([0x3D]) + struct.pack('<I', REMOVE_QUEST)           # cmp eax, 21Dh
    code += bytes([0x74, 0x0B])                                       # je post
    code += bytes([0x83, 0xF8, TASK])                                 # cmp eax, 68h
    code += bytes([0x74, 0x06])                                       # je post
    code += bytes([0x57])                                             # push edi
    code += bytes([0xE9]) + struct.pack('<i', OTHER - (address + len(code) + 5))   # jmp other clicks
    assert len(code) == 0x15
    code += bytes([0x57])                                             # post: push edi
    code += bytes([0xE9]) + struct.pack('<i', POST - (address + len(code) + 5))    # jmp post (event in eax)
    return bytes(code)


def build_patch(original):
    data = bytearray(original)
    pe = u32(data, 0x3C)
    assert data[pe:pe + 4] == b'PE' + bytes(2), 'Not a PE file'
    count = struct.unpack_from('<H', data, pe + 6)[0]
    opt = pe + 24
    assert u32(data, opt + 28) == IMAGE_BASE, 'Unexpected image base'
    table = opt + struct.unpack_from('<H', data, pe + 20)[0]
    secs = [(table + i * 40, struct.unpack_from('<8sIIIIIIHHI', data, table + i * 40)) for i in range(count)]

    def offset(address):
        rva = address - IMAGE_BASE
        for _, s in secs:
            if s[2] <= rva < s[2] + max(s[1], s[3]):
                return s[4] + rva - s[2]
        raise ValueError(f'Address {address:#x} outside file')

    bounty = [(h, s) for h, s in secs if s[0].rstrip(b'\0') == b'.bounty']
    assert bounty, 'The .bounty section (patch_bounty_map_client.py) is missing'
    header, (name, vsize, rva, raw_size, raw, *_rest) = bounty[0]
    address = IMAGE_BASE + rva + CAVE_OFFSET
    code = cave_code(address)
    h = offset(HOOK)
    hook = bytes([0xE9]) + struct.pack('<i', address - (HOOK + 5))
    assert bytes(data[h:h + 5]) != hook, 'Already patched'
    assert bytes(data[h:h + len(HOOK_ORIGINAL)]) == HOOK_ORIGINAL, 'Unexpected journal click handler; refusing patch'
    assert vsize <= CAVE_OFFSET, 'Unexpected .bounty contents; refusing patch'
    assert not any(data[raw + CAVE_OFFSET:raw + CAVE_OFFSET + len(code)]), 'The .bounty cave is not empty; refusing patch'
    assert CAVE_OFFSET + len(code) <= raw_size, 'No room in .bounty'

    data[raw + CAVE_OFFSET:raw + CAVE_OFFSET + len(code)] = code
    struct.pack_into('<I', data, header + 8, CAVE_OFFSET + len(code))          # VirtualSize covers the cave
    data[h:h + 5] = hook                                                      # the rest of the old test is now dead code
    struct.pack_into('<I', data, opt + 64, 0)
    total = 0
    for i in range(0, len(data), 2):
        total += int.from_bytes(data[i:i + 2], 'little')
        total = (total & 0xffff) + (total >> 16)
    total = (total & 0xffff) + (total >> 16)
    struct.pack_into('<I', data, opt + 64, total + len(data))
    return bytes(data), address


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    assert args.source.resolve() != args.output.resolve(), 'Output must be a separate file'
    assert not args.output.exists(), 'Output already exists'
    patched, cave = build_patch(args.source.read_bytes())
    args.output.write_bytes(patched)
    print(f'Prepared the Quest Journal button patch (forwarding code at {cave:#x}): {args.output}')
