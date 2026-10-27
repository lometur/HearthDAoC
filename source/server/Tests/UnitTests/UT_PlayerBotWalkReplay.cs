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
// Set OFFLINE_DAOC_NAV_ROOT to a folder with navmesh/zone008.nav, zone012.nav, zone152.nav, zone209.nav, zone250.nav and
// lib/Detour
// (tools/linux/odaoc_fetch.py --lock deploy/upstream.lock extract runtime/server/navmesh/zone008.nav
// FOLDER/navmesh/zone008.nav, and the same for the others). One test per zone: loading a zone's navmesh twice in one
// process crashes the test host.
[TestFixture, NonParallelizable, Explicit("Needs OFFLINE_DAOC_NAV_ROOT: a folder with navmesh/zone008.nav, zone012.nav, zone152.nav, zone209.nav, zone250.nav and lib/Detour")]
public class UT_PlayerBotWalkReplay
{
    // The quest bot stood 513 units short of Olorustos for 14 minutes (lv-r1-stall, 2026-10-25 04:46): its whole-unit
    // spot lay 0.6 units off the mesh, so each step only snapped it back on, and rounding put it back.
    [Test]
    public void TheBotWalksOnFromItsStallShortOfOlorustos() =>
        // Campacorentin Forest (region 1, zone 8): the Zones row's OffsetX 59, OffsetY 71, Width 8, Height 8.
        OnZone(8, "Campacorentin Forest", 59, 71, (nav, region, zone) =>
        {
            Vector3 stall = new(497254, 587786, 2310), olorustos = new(496777, 587597, 2292);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, stall, olorustos, keepExact: false), Is.EqualTo(-1), "the stall");
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
