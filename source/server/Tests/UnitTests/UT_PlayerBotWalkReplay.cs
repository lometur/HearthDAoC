using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.GS;
using DOL.GS.HearthDAoC.PlayerBots;
using DOL.GS.Tests;
using NUnit.Framework;

namespace DOL.UnitTests;

// HearthDAoC: player-bot walks that stalled, replayed on the installed navmesh. Read-only; never starts the server.
// Set OFFLINE_DAOC_NAV_ROOT to a folder with navmesh/zone008.nav, zone012.nav, zone014.nav, zone120.nav, zone126.nav,
// zone152.nav, zone209.nav, zone250.nav and lib/Detour
// (tools/linux/odaoc_fetch.py --lock deploy/upstream.lock extract runtime/server/navmesh/zone008.nav
// FOLDER/navmesh/zone008.nav, and the same for the others). One test per zone: loading a zone's navmesh twice in one
// process crashes the test host. lib/Detour must be built from this tree (source/server/Pathing/Detour, as
// deploy/Dockerfile builds it): the player bots' layer-aware queries (#166) are not in upstream's release library.
[TestFixture, NonParallelizable, Explicit("Needs OFFLINE_DAOC_NAV_ROOT: a folder with navmesh/zone008.nav, zone012.nav, zone014.nav, zone120.nav, zone126.nav, zone152.nav, zone209.nav, zone250.nav and lib/Detour")]
public class UT_PlayerBotWalkReplay
{
    // The quest bot stood 513 units short of Olorustos for 14 minutes (lv-r1-stall, 2026-10-25 04:46): its whole-unit
    // spot lay 0.6 units off the mesh, so each step only snapped it back on, and rounding put it back. (Since the corner
    // rule of #166 the rounded walk here gets out too, by its near corner; SliverMesh in UT_PlayerBotDriver keeps the
    // rounding case.)
    [Test]
    public void TheBotWalksOnFromItsStallShortOfOlorustos() =>
        // Campacorentin Forest (region 1, zone 8): the Zones row's OffsetX 59, OffsetY 71, Width 8, Height 8.
        OnZone(8, "Campacorentin Forest", 59, 71, (nav, region, zone) =>
        {
            Vector3 stall = new(497254, 587786, 2310), olorustos = new(496777, 587597, 2292);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, stall, olorustos, keepExact: true), Is.InRange(1, 20));
        });

    // st-smoke (2026-10-27, #166): Pbotaaaao stood for the rest of the run in Snowdonia, 0.4 units beside its corridor's
    // first corner and 70 units above it, whatever its goal. The corner is passed now.
    [Test]
    public void TheBotWalksOnFromBesideACornerFarBelowIt() =>
        // Snowdonia (region 1, zone 12): OffsetX 61, OffsetY 37, Width 8, Height 8.
        OnZone(12, "Snowdonia", 61, 37, (nav, region, zone) =>
        {
            Vector3 stall = new(535840.4f, 349184, 5387.15f), goal = new(539712, 353104, 4300);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, stall, goal, keepExact: true), Is.InRange(1, 200));

            // st-fix3: Pbotaaadv stood 88 units under the floor, beyond the surface moves' reach: it steps up onto it.
            Vector3 sunk = new(521313, 317795, 4713), target = new(521067, 316426, 4267);
            Vector3? floor = PlayerBotPathMover.FloorStep(nav, zone, sunk);
            Assert.That(floor, Is.EqualTo(new Vector3(521313, 317795, 4801.47f)).Using<Vector3>((a, b) => Vector3.Distance(a, b) < 1));
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, floor!.Value, target, keepExact: true), Is.InRange(1, 200));

            // st-replan2: Pbotaaabq stood 161 units over the floor, off the mesh, for minutes. Each step stood still
            // without failing, so only a stall ends its walks: then it steps down onto the floor and walks on.
            Vector3 over = new(515046.1f, 358050.7f, 3161.16f), camp = new(513749, 357535, 2823);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, over, camp, keepExact: true, blockEnds: true), Is.EqualTo(-2), "the stall");
            Vector3? down = PlayerBotPathMover.FloorStep(nav, zone, over);
            Assert.That(down, Is.EqualTo(new Vector3(515046.1f, 358050.7f, 3000.47f)).Using<Vector3>((a, b) => Vector3.Distance(a, b) < 1));
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, down!.Value, camp, keepExact: true), Is.InRange(1, 200));

            // st-final2: Pbotaaack stood for minutes on a polygon edge, where 117 of 507 starts within 0.06 units of its
            // spot don't move (this one among them). Down the mountain it then met the corridor line's float (19 over
            // the floor) and a slope whose surface move returns the start though the ray is clear. 10,000 units on, it
            // gets there.
            Vector3 edge = new(536300.6f, 352870.4f, 6563.43f), worryWort = new(546122, 353692, 4510);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, edge, worryWort, keepExact: true, ticks: 400, blockEnds: true),
                Is.InRange(1, 400));

            // st-layers2: roam goals 391 and 210 units under the bot, on floors no corridor leads down to. The corridor ends
            // where the bot stands, and each walk stood 3 seconds and ended stalled; now it ends at the first step.
            foreach ((Vector3 from, Vector3 under) in new[] {
                (new Vector3(545789.90f, 356891.30f, 3416.63f), new Vector3(545790, 356889, 3026)),     // Pbotaaabk
                (new Vector3(547655.20f, 353792.10f, 4363.42f), new Vector3(547628, 353675, 4153)) })   // Pbotaaada
            {
                Assert.That(PlayerBotPathMover.TryComputeNext(nav, region, zone, new(), from, under, 0, 47.75f, 0, out _, out string failure),
                    Is.False, $"from {from}");
                Assert.That(failure, Is.EqualTo(PlayerBotPathMover.OffTheCorridor));
            }
        });

    // st-smoke: in Caledon Pbothaade's first corner lay 2.2 units beside it and 123 above it. A surface step that only
    // dropped it back onto the floor counted as a step, so it bounced there for the rest of the run.
    [Test]
    public void TheBotWalksOnUnderACornerFarAboveIt() =>
        // Caledon (region 250, zone 250): OffsetX 1, OffsetY 1, Width 8, Height 8.
        OnZone(250, "Caledon", 1, 1, (nav, region, zone) =>
        {
            Vector3 stall = new(18475, 18946, 4325), goal = new(34053, 13870, 4508);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, stall, goal, keepExact: true, ticks: 600), Is.InRange(1, 600));
        });

    // st-smoke: in the Gripklosa Mountains Pbotmaaae, 267 units from its target with a wall between, took the straight
    // surface move of the last 300 units and crept along the wall.
    [Test]
    public void TheBotWalksRoundAWallNearItsTarget() =>
        // Gripklosa Mountains (region 151, zone 152): OffsetX 34, OffsetY 30, Width 8, Height 8.
        OnZone(152, "Gripklosa Mountains", 34, 30, (nav, region, zone) =>
        {
            Vector3 stall = new(282187.1f, 280977.1f, 4229.36f), target = new(282046, 281204, 4239);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, stall, target, keepExact: true), Is.InRange(1, 40));
        });

    // st-replan: in Vendo Caverns Pbotmaaab's corridor turned round a wall by two corners 13 and 20 units from it. Both
    // counted as reached, so it walked at the next corner, 100 units off behind the wall, and every fresh corridor did
    // the same: it stood there for minutes.
    [Test]
    public void TheBotWalksRoundAWallByTheCornersNearIt() =>
        // Vendo Caverns (region 126, zone 126): OffsetX 1, OffsetY 1, Width 8, Height 8.
        OnZone(126, "Vendo Caverns", 1, 1, (nav, region, zone) =>
        {
            Vector3 stall = new(27716.25f, 31724.64f, 16070.8f), goal = new(28693, 33846, 15744);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, stall, goal, keepExact: true, ticks: 600, blockEnds: true),
                Is.InRange(1, 600));
        });

    // st-final4 (and six runs before it): in Jordheim a roaming bot's corridor crossed a ridge by a corner 35 units up. Its moves
    // toward the corner slid along the ridge's foot into a pocket, and every fresh corridor from there led out and back into it.
    // The pocket is one of eight small floors walled in under the city's ramps, which the mesh links only to the ramps 70-133
    // units above them, at tile seams: the surface moves fell through the ramps onto them, and the corridors out hid the
    // climb in one corner. With the layer-aware queries (#166) the bot gets out of them and walks over them.
    [Test]
    public void TheBotGetsOutOfAndKeepsOffTheFloorsUnderJordheimsRamps() =>
        // Jordheim (region 101, zone 120): OffsetX 1, OffsetY 1, Width 8, Height 8.
        OnZone(120, "Jordheim", 1, 1, (nav, region, zone) =>
        {
            Vector3 pocket = new(32225.28f, 26621.72f, 8762.75f), firstStall = new(32254.63f, 26615.85f, 8762.75f);
            foreach (Vector3 goal in new Vector3[] { new(32204, 28405, 8801), new(30494, 28040, 8766), new(31746, 27019, 8763) })
            {
                Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, pocket, goal, keepExact: true, ticks: 400, blockEnds: true),
                    Is.InRange(1, 400), $"from the pocket to {goal}");
                Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, firstStall, goal, keepExact: true, ticks: 400, blockEnds: true),
                    Is.InRange(1, 400), $"from the first stall to {goal}");
            }
            // Out of two others and the pocket floor's south end, toward goals bots had there: blocked after 29-40 steps
            // before.
            foreach ((Vector3 from, Vector3 goal) in new[] {
                (new Vector3(32169, 26518, 8766), new Vector3(32145, 26460, 8823)),                             // st-fix6
                (new Vector3(31257.7f, 26596.0f, 8818.8f), new Vector3(30859, 27162, 8818)),                    // lm-fsmoke
                (new Vector3(30990.1f, 26822.2f, 8847.4f), new Vector3(31024, 26697, 8891)) })                  // lm-ona
                Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, from, goal, keepExact: true, ticks: 400, blockEnds: true),
                    Is.InRange(1, 100), $"from {from} to {goal}");
            // Over each of them on the ramp, not down onto it: these walks timed out after 150 steps on the floor below, or
            // were blocked after 33 and 18.
            foreach ((Vector3 from, Vector3 goal) in new[] {
                (new Vector3(32315.82f, 26506.08f, 8796.79f), new Vector3(32091.76f, 26532.80f, 8781.00f)),
                (new Vector3(31378.30f, 26576.80f, 8864.81f), new Vector3(31235.76f, 26625.87f, 8816.79f)),
                (new Vector3(31026.96f, 26935.20f, 8804.27f), new Vector3(30992.94f, 26782.56f, 8917.07f)) })
                Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, from, goal, keepExact: true, ticks: 400, blockEnds: true),
                    Is.InRange(1, 20), $"from {from} to {goal}");
            // Without a stall (st-layers1 and the review): half way down a 49-unit link the bot passed its foot and took the
            // ramp it came from (38 steps, with a replan); a corner on the ramp at its vertices' height, 47 units over a floor
            // walled in under it and 50 under the ramp's own, took it onto that floor (28); and the top of the link out of a
            // fourth walled-in floor lay 72 units under the floor it leads onto (lost the walkable surface).
            foreach ((Vector3 from, Vector3 goal, int most) in new[] {
                (new Vector3(31976.0f, 27124.0f, 8829.8f), new Vector3(32364, 27658, 8766), 34),
                (new Vector3(30957.2f, 26791.2f, 8933.5f), new Vector3(31336.3f, 26464.8f, 8913.1f), 20),
                (new Vector3(34860, 32405.5f, 8353), new Vector3(34900, 32470, 8480), 10) })
                Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, from, goal, keepExact: true, ticks: 400, blockEnds: true),
                    Is.InRange(1, most), $"from {from} to {goal}");
            Assert.That(LayeredQueriesRan(), Is.True, "lib/Detour has the layer-aware queries");
        });

    // #166: where no tile seam joins two floors, the layer-aware corridor has the plain one's corners, each on its polygon's
    // floor. A first version took an off-mesh connection's link (Detour gives it edge 0xff) for a seam's: on corridors back
    // across a two-way jump link between two tiles it read past the polygon's vertices and put corners as far off as
    // (0, 0, z), in zones 8, 12, 14 and 250. Here the Pennine Mountains' 15 such links, both ways.
    [Test]
    public void TheLayeredCorridorOverAJumpLinkIsThePlainOne() =>
        // Pennine Mountains (region 1, zone 14): OffsetX 69, OffsetY 43, Width 8, Height 8.
        OnZone(14, "Pennine Mountains", 69, 43, (nav, region, zone) =>
        {
            var plain = new WrappedPathfindingNode[256];
            var layered = new WrappedPathfindingNode[256];
            foreach ((Vector3 start, Vector3 end) in PennineJumps)
                foreach ((Vector3 a, Vector3 b) in new[] { (start, end), (end, start) })
                {
                    PathfindingResult p = nav.GetPathStraight(zone, a, b, nav.DefaultFilters, plain);
                    PathfindingResult l = nav.GetPathStraightLayered(zone, a, b, nav.DefaultFilters, layered);
                    Assert.That(p.NodeCount, Is.GreaterThan(1), $"{a} -> {b}");
                    Assert.That(l.Status, Is.EqualTo(p.Status), $"{a} -> {b}");
                    Assert.That(Array.ConvertAll(layered[..l.NodeCount], n => (n.Position.X, n.Position.Y, n.Flags)),
                        Is.EqualTo(Array.ConvertAll(plain[..p.NodeCount], n => (n.Position.X, n.Position.Y, n.Flags))), $"{a} -> {b}");
                }
            Assert.That(LayeredQueriesRan(), Is.True, "lib/Detour has the layer-aware queries");
        });

    // Whether the layer-aware queries ran on lib/Detour, not on the plain ones it falls back to without them (one warning,
    // which the test host does not show): with those, the comparison above compares the plain corridor with itself.
    private static bool LayeredQueriesRan() =>
        (int)typeof(LocalPathfindingMgr).GetField("_noLayers", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)! == 0;

    // The two ends of the cross-tile two-way off-mesh connections in zone014.nav.
    private static readonly (Vector3, Vector3)[] PennineJumps =
    {
        (new(583219.2f, 365446.4f, 6345.6f), new(583180.8f, 365475.2f, 6345.6f)),
        (new(583158.4f, 365478.4f, 6233.6f), new(583206.4f, 365449.6f, 6246.4f)),
        (new(581616.0f, 366384.0f, 6345.6f), new(581606.4f, 366406.4f, 6345.6f)),
        (new(581619.2f, 366428.8f, 6249.6f), new(581625.6f, 366377.6f, 6249.6f)),
        (new(585212.8f, 396300.8f, 3984.0f), new(585232.0f, 396307.2f, 3984.0f)),
        (new(585174.4f, 396284.8f, 3792.0f), new(585257.6f, 396316.8f, 3795.2f)),
        (new(585232.0f, 396307.2f, 3984.0f), new(585212.8f, 396300.8f, 3984.0f)),
        (new(585232.0f, 396307.2f, 3984.0f), new(585200.0f, 396294.4f, 4070.4f)),
        (new(585257.6f, 396316.8f, 3795.2f), new(585174.4f, 396284.8f, 3792.0f)),
        (new(585257.6f, 396316.8f, 3795.2f), new(585164.8f, 396281.6f, 4044.8f)),
        (new(585484.8f, 396784.0f, 2841.6f), new(585523.2f, 396745.6f, 2841.6f)),
        (new(585523.2f, 396780.8f, 2710.4f), new(585497.6f, 396812.8f, 2710.4f)),
        (new(585523.2f, 396745.6f, 2841.6f), new(585484.8f, 396784.0f, 2841.6f)),
        (new(585497.6f, 396812.8f, 2710.4f), new(585484.8f, 396784.0f, 2841.6f)),
        (new(575993.6f, 398880.0f, 5552.0f), new(576016.0f, 398883.2f, 5552.0f)),
    };

    // st-fix2: in Tir na Nog Pbothaacw stood 84 units above the floor, off the mesh: every step lost the walkable
    // surface, for the rest of the run. It steps onto the nearest floor, 51 units away, and walks on from there.
    [Test]
    public void TheBotStepsOntoTheFloorFromOffTheMeshAndWalksOn() =>
        // Tir na Nog (region 201, zone 209): OffsetX 1, OffsetY 1, Width 8, Height 8.
        OnZone(209, "Tir na Nog", 1, 1, (nav, region, zone) =>
        {
            Vector3 lost = new(24125, 33752, 6576), goal = new(23045, 34295, 6400);
            Assert.That(PlayerBotPathMover.TryComputeNext(nav, region, zone, new(), lost, goal, 20, 47.75f, 0, out _, out string failure),
                Is.False);
            Assert.That(failure, Does.StartWith("Lost the walkable surface"));
            Vector3? floor = PlayerBotPathMover.FloorStep(nav, zone, lost);
            Assert.That(floor, Is.Not.Null);
            Assert.That(Vector3.Distance(lost, floor!.Value), Is.LessThan(PlayerBotPathMover.FloorStepRange));
            Assert.That(PlayerBotPathMover.FloorStep(nav, zone, floor.Value), Is.Null, "on floor with a way out: no step");
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, floor.Value, goal, keepExact: true), Is.InRange(1, 200));

            // st-fix3: the same bot stood 79 units under the floor: it steps up onto the floor straight above.
            Vector3 sunk = new(24140, 33873, 6582);
            Assert.That(PlayerBotPathMover.FloorStep(nav, zone, sunk),
                Is.EqualTo(new Vector3(24140, 33873, 6660.8f)).Using<Vector3>((a, b) => Vector3.Distance(a, b) < 1));

            // st-fix4: here the mesh joins the floor at 6494 to the one at 6661, 167 units up over 41, with no floor
            // between. Half way up, the surface moves found a middle floor (6574) 30 units aside, and the bot stood on its
            // edge for minutes, its next corner 87 units overhead. It goes up the link to the corner, as a GameBot does.
            Vector3 middle = new(24084.06f, 33671.36f, 6574.4f), upstairs = new(24298, 33669, 6661);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, middle, upstairs, keepExact: true), Is.InRange(1, 200));
            Vector3 below = new(24063.2f, 33648, 6494.4f);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, below, upstairs, keepExact: true), Is.InRange(1, 200));
        });

    private static void OnZone(ushort id, string name, int offsetX, int offsetY, Action<IPathfindingMgr, Region, Zone> walk)
    {
        string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
        Assert.That(root, Is.Not.Null.And.Not.Empty);
        string previous = Environment.CurrentDirectory;
        Zone zone = null;
        try
        {
            Environment.CurrentDirectory = root;
            var region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
            typeof(Region).GetField("m_regionData", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(region, new RegionData { Id = 1 });
            zone = new(region, id, name, offsetX * 8192, offsetY * 8192, 8 * 8192, 8 * 8192, id, false, 0, false, 0, 0, 0, 0, 0);
            typeof(Region).GetField("m_zones", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(region, new List<Zone> { zone });
            LocalPathfindingMgr.LoadNavMesh(zone);
            var nav = PathfindingProvider.LocalPathfindingMgr;
            Assert.That(nav.HasNavmesh(zone), Is.True);
            walk(nav, region, zone);
        }
        finally
        {
            if (zone != null) LocalPathfindingMgr.UnloadNavMesh(zone);
            Environment.CurrentDirectory = previous;
        }
    }
}
