using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.GS;
using DOL.GS.Tests;
using NUnit.Framework;

namespace DOL.UnitTests;

// HearthDAoC: player-bot walks that stalled, replayed on the installed navmesh. Read-only; never starts the server.
// Set OFFLINE_DAOC_NAV_ROOT to a folder with navmesh/zone008.nav and lib/Detour (tools/linux/odaoc_fetch.py --lock
// deploy/upstream.lock extract runtime/server/navmesh/zone008.nav FOLDER/navmesh/zone008.nav).
[TestFixture, NonParallelizable, Explicit("Needs OFFLINE_DAOC_NAV_ROOT: a folder with navmesh/zone008.nav and lib/Detour")]
public class UT_PlayerBotWalkReplay
{
    // The quest bot stood 513 units short of Olorustos for 14 minutes (lv-r1-stall, 2026-10-25 04:46): its whole-unit
    // spot lay 0.6 units off the mesh, so each step only snapped it back on, and rounding put it back.
    [Test]
    public void TheBotWalksOnFromItsStallShortOfOlorustos()
    {
        string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
        Assert.That(root, Is.Not.Null.And.Not.Empty);
        string previous = Environment.CurrentDirectory;
        Zone zone = null;
        try
        {
            Environment.CurrentDirectory = root;
            // Campacorentin Forest (region 1, zone 8): the Zones row's OffsetX 59, OffsetY 71, Width 8, Height 8.
            var region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
            typeof(Region).GetField("m_regionData", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(region, new RegionData { Id = 1 });
            zone = new(region, 8, "Campacorentin Forest", 59 * 8192, 71 * 8192, 8 * 8192, 8 * 8192, 8, false, 0, false, 0, 0, 0, 0, 0);
            typeof(Region).GetField("m_zones", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(region, new List<Zone> { zone });
            LocalPathfindingMgr.LoadNavMesh(zone);
            var nav = PathfindingProvider.LocalPathfindingMgr;
            Assert.That(nav.HasNavmesh(zone), Is.True);

            Vector3 stall = new(497254, 587786, 2310), olorustos = new(496777, 587597, 2292);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, stall, olorustos, keepExact: false), Is.EqualTo(-1), "the stall");
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, stall, olorustos, keepExact: true), Is.InRange(1, 20));
        }
        finally
        {
            if (zone != null) LocalPathfindingMgr.UnloadNavMesh(zone);
            Environment.CurrentDirectory = previous;
        }
    }
}
