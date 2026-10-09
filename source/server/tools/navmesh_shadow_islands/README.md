# Navmesh shadow islands

`shadowfix.py` disables small walkable navmesh regions that nothing uses and that sit within
256 units above or below a used floor: crawlspaces under a floor, tops of blocks. Floor snapping
(vertical search up to 256) could land on one of them and strand a route. In Tuscaran Glacier
(Oct 3-4) the raid front landed on a 275 x 275 pad 96 units under the floor its parties stood
on, and every route probe from there failed for hours.

Kept as is:
- any region with an entrance, a zone point or a mob spawn;
- regions larger than 2,000,000 square units;
- anything within 600 units of a ladder/climb off-mesh link (climb pads sit near floors);
- door polygons and off-mesh link polygons.

Disabled polygons get the flags value `Disabled` only (0x10). The server's default filter
includes every flag except Disabled, so any other bit left set would still pass.

```
python shadowfix.py <in.nav> <out.nav> <region id> <path to opendaoc.sqlite3.db>
```

Applied on 2026-10-05:

| Zone | Input | Result |
| --- | --- | --- |
| 160 Tuscaran Glacier | the Sep 13 glacier-climb build (MD5 107519b9e4ef0b46c49a0be755d6fd89) | 1,325 polygons in 258 islands, MD5 a3b7ec1ee046e60ab2feafa3ef6fe862 |
| 191 Galladoria | OpenDAoC-BuildNav output (MD5 2b6913c731391d967f7460ca6fa1b558, same as before) | 1,589 polygons in 320 islands, MD5 ffd72fcda96d469c3507195fae70f820 |

Checked with the real Detour library before installing (`UT_NavGapProbe`,
`UT_RealmEventNavigation`): spawns reachable from the entrance were unchanged (Tuscaran
362/364, Galladoria 196/197), Glacier climb links were unchanged (1,058/1,178 reachable from
the entrance), and two-way reachable grid points went up slightly in both zones. From the
old Tuscaran trap point, the whole dungeon is reachable again.
