"""Adds each zone's main walkable area (largest connected component, navcomp ids) to navmesh/seams.json
as {"main": {region: {zone: area}}}. Same ids as seamgraph.py for the same mesh build."""
import json, os, sys, collections
from navcomp import NavAreas
seams_path, navdir = sys.argv[1], sys.argv[2]
d = json.load(open(seams_path))
main = {}
for region, edges in d['regions'].items():
    zones = sorted({e['from'] for e in edges} | {e['to'] for e in edges})
    main[region] = {}
    for z in zones:
        p = os.path.join(navdir, f"zone{z:03d}.nav")
        if not os.path.exists(p): continue
        m = NavAreas(p)
        comp = collections.Counter(m._find(i) for i in range(len(m.polys)) if not m.polys[i][4])
        main[region][str(z)] = comp.most_common(1)[0][0]
    print(region, main[region])
d['main'] = main
json.dump(d, open(seams_path, 'w'), separators=(',', ':'))
