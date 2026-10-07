"""Small PE32 helpers for HearthDAoC's client patches (Python standard library only).

Reads the headers HearthDAoC needs, computes the PE checksum, appends a section and turns
two versions of a file into patch-set operations. All addresses called `va` are absolute
virtual addresses (image base included), as in a disassembly of game.dll.
"""
import array
import struct
import sys
from collections import namedtuple

# va: absolute virtual address of the section (image base + the header's VirtualAddress).
Section = namedtuple("Section", "name va vsize raw_offset raw_size characteristics")

IMAGE_SCN_CNT_CODE = 0x20
SECTION_HEADER_SIZE = 40
MERGE_GAP = 16
BLOCK = 4096


def align(value, alignment):
    return (value + alignment - 1) // alignment * alignment


class PE:
    """The headers of a 32-bit PE file (PE32), read from its bytes."""

    def __init__(self, data: bytes):
        if len(data) < 0x40 or data[:2] != b"MZ":
            raise ValueError("not a PE file: no MZ header")
        e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
        if data[e_lfanew:e_lfanew + 4] != b"PE\0\0":
            raise ValueError("not a PE file: no PE signature")
        count, _, _, _, optional_size = struct.unpack_from("<HIIIH", data, e_lfanew + 6)
        opt = e_lfanew + 24
        if struct.unpack_from("<H", data, opt)[0] != 0x10B:
            raise ValueError("not a 32-bit PE (PE32) file")
        self.image_base = struct.unpack_from("<I", data, opt + 28)[0]
        self.section_alignment, self.file_alignment = struct.unpack_from("<II", data, opt + 32)
        self.size_of_image, self.size_of_headers = struct.unpack_from("<II", data, opt + 56)
        table = opt + optional_size
        self.header_offsets = {
            "num_sections": e_lfanew + 6,
            "size_of_code": opt + 4,
            "size_of_image": opt + 56,
            "checksum": opt + 64,
            "section_table": table,
        }
        self.sections = []
        for i in range(count):
            entry = table + SECTION_HEADER_SIZE * i
            name, vsize, rva, raw_size, raw_offset = struct.unpack_from("<8sIIII", data, entry)
            characteristics = struct.unpack_from("<I", data, entry + 36)[0]
            self.sections.append(Section(name.rstrip(b"\0").decode("latin-1"), self.image_base + rva,
                                         vsize, raw_offset, raw_size, characteristics))

    def offset(self, va: int) -> int:
        """File offset of the byte at virtual address `va`; ValueError if no file byte holds it."""
        for s in self.sections:
            if s.va <= va < s.va + s.raw_size:
                return s.raw_offset + va - s.va
        raise ValueError(f"VA {va:#x} is not stored in the file")


def checksum(data: bytes, checksum_offset: int) -> int:
    """The PE image checksum, with the 4 bytes at `checksum_offset` counted as zero."""
    buf = bytes(data[:checksum_offset]) + bytes(4) + bytes(data[checksum_offset + 4:])
    if len(buf) % 2:
        buf += b"\0"
    words = array.array("H")
    words.frombytes(buf)
    if sys.byteorder == "big":
        words.byteswap()
    total = sum(words)
    while total > 0xFFFF:
        total = (total & 0xFFFF) + (total >> 16)
    return (total + len(data)) & 0xFFFFFFFF


def append_section(data: bytes, name: str, payload: bytes,
                   characteristics: int = 0x60000020) -> tuple[bytes, int]:
    """Append `payload` as a new last section. Updates the section table, NumberOfSections,
    SizeOfImage, SizeOfCode (for code sections) and the checksum. Returns (new data, section VA)."""
    encoded = name.encode("ascii")
    if not 0 < len(encoded) <= 8:
        raise ValueError(f"section name {name!r} must be 1 to 8 ASCII characters")
    if not payload:
        raise ValueError("the new section needs a payload")
    pe = PE(data)
    if any(s.name == name for s in pe.sections):
        raise ValueError(f"the file already has a {name} section")
    offsets = pe.header_offsets
    entry = offsets["section_table"] + SECTION_HEADER_SIZE * len(pe.sections)
    first_raw = min([s.raw_offset for s in pe.sections if s.raw_size] + [pe.size_of_headers])
    if entry + SECTION_HEADER_SIZE > first_raw:
        raise ValueError("no room in the PE header for another section")
    if any(data[entry:entry + SECTION_HEADER_SIZE]):
        raise ValueError("the PE header slack after the section table is not empty")
    end = max(s.va + max(s.vsize, s.raw_size) for s in pe.sections) - pe.image_base
    rva = align(end, pe.section_alignment)
    raw = align(len(data), pe.file_alignment)
    raw_size = align(len(payload), pe.file_alignment)
    out = bytearray(data)
    out += bytes(raw - len(out))
    out += payload + bytes(raw_size - len(payload))
    struct.pack_into("<8sIIIIIIHHI", out, entry, encoded.ljust(8, b"\0"), len(payload), rva,
                     raw_size, raw, 0, 0, 0, 0, characteristics)
    struct.pack_into("<H", out, offsets["num_sections"], len(pe.sections) + 1)
    if characteristics & IMAGE_SCN_CNT_CODE:
        size_of_code = struct.unpack_from("<I", out, offsets["size_of_code"])[0]
        struct.pack_into("<I", out, offsets["size_of_code"], size_of_code + raw_size)
    struct.pack_into("<I", out, offsets["size_of_image"], align(rva + len(payload), pe.section_alignment))
    struct.pack_into("<I", out, offsets["checksum"], checksum(out, offsets["checksum"]))
    return bytes(out), pe.image_base + rva


def diff_ops(old: bytes, new: bytes) -> list[dict]:
    """Patch-set operations that turn `old` into `new`: one replace op per changed run inside
    `old` (runs less than 16 unchanged bytes apart are merged), then one append op for any tail."""
    if len(new) < len(old):
        raise ValueError("diff_ops cannot shrink a file")
    changed = []
    for start in range(0, len(old), BLOCK):
        a, b = old[start:start + BLOCK], new[start:start + BLOCK]
        if a != b:
            changed.extend(start + i for i in range(len(a)) if a[i] != b[i])
    runs = []
    for i in changed:
        if runs and i - runs[-1][1] < MERGE_GAP:
            runs[-1][1] = i + 1
        else:
            runs.append([i, i + 1])
    ops = [{"op": "replace", "offset": s, "from": old[s:e].hex(), "to": new[s:e].hex()} for s, e in runs]
    if len(new) > len(old):
        ops.append({"op": "append", "data": new[len(old):].hex()})
    return ops
