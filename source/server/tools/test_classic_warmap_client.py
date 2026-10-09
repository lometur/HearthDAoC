"""Check a patched game.dll for the classic war map and Quest Journal button patches (no game needed).

    python test_classic_warmap_client.py <client app folder>

Checks: every icon patch of patch_classic_warmap_client.py is in place, the zone lookup answers each classic frontier
zone with its tile (read from the table the new lookup uses) and refuses the New Frontiers ids, the pip divisor is
512, the Quest Journal hook jumps to forwarding code that sends RemoveQuest and Task down the same path, and the
three war map textures carry the classic zone maps in the quadrants their tile controls read. With capstone
installed it also disassembles the new code.
"""
import importlib.util
import struct
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent


def load(name):
    spec = importlib.util.spec_from_file_location(name, HERE / f'{name}.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main(app):
    wm = load('patch_classic_warmap_client')
    qj = load('patch_quest_journal_button_client')
    tx = load('build_classic_warmap_textures')
    data = (app / 'game.dll').read_bytes()
    opt, secs = wm.sections(data)
    off = lambda a: wm.file_offset(secs, a)
    failures = []
    for address, old, new, why in wm.PATCHES:
        if data[off(address):off(address) + len(new) // 2].hex() != new:
            failures.append(f'icon patch missing at {address:#x} ({why})')
    if data[off(wm.LOOKUP):off(wm.LOOKUP_END)] != wm.lookup_code():
        failures.append('zone lookup not patched')
    table = off(wm.LOOKUP + 0x24)
    for page, zones in wm.TILE_ZONES.items():
        for tile, zone in enumerate(zones):
            got = struct.unpack_from('<H', data, table + page * 8 + tile * 2)[0]
            if got != zone:
                failures.append(f'page {page} tile {tile}: zone {got} != {zone}')
    for nf in range(164, 179):        # 163 is Agramon (its own page 3, kept)
        if any(struct.unpack_from('<H', data, table + i * 2)[0] == nf for i in range(12)):
            failures.append(f'New Frontiers zone {nf} still mapped on a realm page')
    if data[off(wm.DIVISOR):off(wm.DIVISOR) + 5] != bytes.fromhex('bb00020000'):
        failures.append('pip divisor not 512')
    hook = data[off(qj.HOOK):off(qj.HOOK) + 5]
    if hook[0] != 0xE9:
        failures.append('Quest Journal hook missing')
    else:
        cave = qj.HOOK + 5 + struct.unpack_from('<i', hook, 1)[0]
        if data[off(cave):off(cave) + 0x1B] != qj.cave_code(cave):
            failures.append('Quest Journal forwarding code differs')
    try:
        from PIL import Image
        for name, quads in tx.QUADRANTS.items():
            texture = Image.open(app / 'ui' / 'isles' / name).convert('RGB')
            for (x, y), zone in quads.items():
                tile = Image.open(app / 'ui' / 'maps' / f'z{zone:03d}.dds').convert('RGB').resize((128, 128), Image.LANCZOS)
                if texture.crop((x, y, x + 128, y + 128)).tobytes() != tile.tobytes():
                    failures.append(f'{name} quadrant {x},{y} is not zone {zone}')
    except ImportError:
        print('PIL missing: textures not checked')
    except FileNotFoundError as e:
        failures.append(f'texture or zone map missing: {e.filename}')
    try:
        import capstone
        md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
        for start, length in ((wm.LOOKUP, 0x21), (cave if hook[0] == 0xE9 else qj.HOOK, 0x1B)):
            for ins in md.disasm(data[off(start):off(start) + length], start):
                print(f'  {ins.address:08x} {ins.mnemonic} {ins.op_str}')
    except ImportError:
        pass
    for f in failures:
        print('FAIL', f)
    print('OK' if not failures else f'{len(failures)} failure(s)')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main(Path(sys.argv[1])))
