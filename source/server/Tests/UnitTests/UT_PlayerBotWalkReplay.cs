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
// Set OFFLINE_DAOC_NAV_ROOT to a folder with navmesh/zone008.nav, zone012.nav, zone120.nav, zone126.nav, zone152.nav,
// zone209.nav, zone250.nav and lib/Detour
// (tools/linux/odaoc_fetch.py --lock deploy/upstream.lock extract runtime/server/navmesh/zone008.nav
// FOLDER/navmesh/zone008.nav, and the same for the others). One test per zone: loading a zone's navmesh twice in one
// process crashes the test host.
[TestFixture, NonParallelizable, Explicit("Needs OFFLINE_DAOC_NAV_ROOT: a folder with navmesh/zone008.nav, zone012.nav, zone120.nav, zone126.nav, zone152.nav, zone209.nav, zone250.nav and lib/Detour")]
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
    [Test]
    public void TheBotWalksOutOfThePocketBelowARidge() =>
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
        });

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
