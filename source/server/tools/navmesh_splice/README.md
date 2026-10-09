# Navmesh tile compare and splice

`navdiff.py` compares two Detour MSET `.nav` files tile by tile and can splice tiles from a
freshly built mesh into an installed one.

```
python navdiff.py <a.nav> <b.nav>        # tiles only in A/B, differing tiles with world bounds
```

```python
from navdiff import splice
splice("installed.nav", "built.nav", "out.nav", x, y, half)  # tiles touching the square
```

`splice` keeps the installed file's header, tile order, tile refs and padding bytes, and only
swaps the tile data of the picked tiles. Splicing nothing (`half=-1`) reproduces the installed
file byte for byte; check that before trusting a splice. Detour joins neighbouring tiles by
matching their shared border edges at load, so always verify the seams with a real-Detour
probe (for example `UT_SiPortalPlatformProbe` or `UT_FloorAtPointsProbe`).

Used on 2026-10-05 for the solid Shrouded Isles portal platforms in Camelot Hills (000),
Vale of Mularn (100) and Lough Derg (200): 49 tiles (about 900 units) around each portal from
an OpenDAoC-BuildNav build of the current client, spliced into the installed meshes.
Builder note: zone 200 rebuilds byte-identical to the installed mesh, zones 000/100 do not
(their installed meshes have raised "New Towns" plazas around the portals that this client
does not have).
