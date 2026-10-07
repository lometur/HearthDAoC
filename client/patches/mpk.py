"""Read a DAoC MPK archive (the format of pregame/splash.mpk), standard library only.

Layout, as written by upstream's OfflineDaoc.Mpk tool (source/server/CoreBase/MPK):
- "MPAK", then the byte 2.
- 16 bytes, each XORed with its index: CRC-32 of the compressed directory, the compressed
  directory size, the compressed name size and the number of files (4 little-endian uint32s).
- The archive's internal name, zlib-compressed. The client binds the archive by this name.
- The directory, zlib-compressed: one 284-byte entry per file (name[256], timestamp, 4,
  offset, uncompressed size, offset in the data area, compressed size, CRC-32 of the
  compressed bytes).
- The data area: each file's zlib stream.
"""
import struct
import zlib

MAGIC = b"MPAK\x02"
ENTRY_SIZE = 0x11C


class MpkError(ValueError):
    """The file isn't a readable MPK archive; the message says why."""


def _inflate(data, where):
    d = zlib.decompressobj()
    try:
        out = d.decompress(data) + d.flush()
    except zlib.error as e:
        raise MpkError(f"{where}: {e}") from None
    if not d.eof:
        raise MpkError(f"{where}: truncated zlib stream")
    return out


def read_mpk(path):
    """Return (internal name, [(entry name, data), ...] in directory order)."""
    with open(path, "rb") as f:
        data = f.read()
    if data[:5] != MAGIC:
        raise MpkError(f"{path}: not an MPK archive")
    head = bytes(b ^ i for i, b in enumerate(data[5:21]))
    if len(head) != 16:
        raise MpkError(f"{path}: truncated header")
    crc, size_dir, size_name, count = struct.unpack("<IIII", head)
    pos = 21
    name = _inflate(data[pos:pos + size_name], "archive name").split(b"\0")[0].decode("utf-8")
    pos += size_name
    packed_dir = data[pos:pos + size_dir]
    if zlib.crc32(packed_dir) != crc:
        raise MpkError(f"{path}: directory CRC mismatch")
    directory = _inflate(packed_dir, "directory")
    pos += size_dir
    if len(directory) != count * ENTRY_SIZE:
        raise MpkError(f"{path}: directory holds {len(directory)} bytes for {count} files")
    entries = []
    for k in range(count):
        raw = directory[k * ENTRY_SIZE:(k + 1) * ENTRY_SIZE]
        entry_name = raw[:256].split(b"\0")[0].decode("utf-8")
        _, _, _, size, data_offset, packed_size, entry_crc = struct.unpack("<IiIIIII", raw[256:])
        packed = data[pos + data_offset:pos + data_offset + packed_size]
        if len(packed) != packed_size or zlib.crc32(packed) != entry_crc:
            raise MpkError(f"{path}: {entry_name}: CRC mismatch")
        content = _inflate(packed, entry_name)
        if len(content) != size:
            raise MpkError(f"{path}: {entry_name}: {len(content)} bytes, directory says {size}")
        entries.append((entry_name, content))
    return name, entries
