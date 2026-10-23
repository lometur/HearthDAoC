using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.Logging;

namespace DOL.GS
{
    /// <summary>
    /// The Shrouded Isles portals in Cotswold, Mularn and Mag Mell are only map art: the game
    /// client sends no jump request when a player walks into them, so the paired zone points
    /// ("ALB SI ENTRANCE", "MID SI ENTRANCE", "SILVERMINE > DOMNANN") could only be used by
    /// bots, which take them server side. A small circle at each zone point's source spot now
    /// sends a player through that same zone point. Bots keep their own crossing (areas only
    /// react to players).
    /// </summary>
    public static class ShroudedIslesPortals
    {
        private static readonly Logger Log = LoggerManager.Create(typeof(ShroudedIslesPortals));

        /// <summary>
        /// The three entrance zone points by Id: 153 "ALB SI ENTRANCE" (Cotswold), 165 "MID SI
        /// ENTRANCE" (Mularn) and 167 "SILVERMINE > DOMNANN" (Mag Mell).
        /// </summary>
        public static readonly ushort[] EntranceIds = [153, 165, 167];
        public const long ReuseMilliseconds = 5_000;

        // HearthDAoC: no walk-on pads. The Shrouded Isles portals are the client's own 1.65 portals on the Avalon
        // Marsh pier, in Gotar and in Shannon Estuary, whose jump lines send these same zone points, and
        // deploy/bin/mob_fixes.json moves the zone points' source spots there (2026-10-22). Upstream's pads were at
        // the 1.74 town spots, on the navmesh floor UT_SiPortalPlatformProbe measured:
        // [153] = (564976, 509200, 2694) Cotswold, [165] = (808830, 725039, 4815) Mularn,
        // [167] = (348198, 492819, 5290) Mag Mell. With none, OnServerStarted adds no area and bots cross at the
        // zone point's source, as at every other zone point.
        public static readonly Dictionary<ushort, Vector3> Pads = new();
        public const int PadRadius = 96;
        public const int PadHeightTolerance = 60;

        public static bool TryGetPad(ushort zonePointId, out Vector3 pad) => Pads.TryGetValue(zonePointId, out pad);

        /// <summary>Pure rule: standing on the platform, close to the portal.</summary>
        public static bool IsOnPad(Vector3 pad, Vector3 position) =>
            Vector2.Distance(new(pad.X, pad.Y), new(position.X, position.Y)) <= PadRadius &&
            Math.Abs(position.Z - pad.Z) <= PadHeightTolerance;

        private static readonly ConcurrentDictionary<GamePlayer, long> LastUse = new();

        [GameServerStartedEvent]
        public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
        {
            foreach (DbZonePoint point in GameServer.Database.SelectAllObjects<DbZonePoint>()
                         .Where(p => EntranceIds.Contains(p.Id) && Pads.ContainsKey(p.Id)))
            {
                Region region = WorldMgr.GetRegion(point.SourceRegion);
                if (region == null || WorldMgr.GetRegion(point.TargetRegion) == null)
                {
                    Log.Warn($"SHROUDED_ISLES_PORTAL_SKIPPED id={point.Id} region={point.SourceRegion} target={point.TargetRegion}");
                    continue;
                }
                region.AddArea(new PortalArea(point));
                Vector3 pad = Pads[point.Id];
                Log.Info($"SHROUDED_ISLES_PORTAL id={point.Id} region={point.SourceRegion} " +
                         $"pad={pad.X:0},{pad.Y:0},{pad.Z:0} radius={PadRadius} -> " +
                         $"{point.TargetRegion}:{point.TargetX},{point.TargetY},{point.TargetZ}");
            }
        }

        /// <summary>Pure rule: a player may use a portal again only after a short pause.</summary>
        public static bool MayUse(long lastUse, long now) => lastUse == 0 || now - lastUse >= ReuseMilliseconds;

        private sealed class PortalArea : Area.Circle
        {
            private readonly DbZonePoint _point;
            private readonly Vector3 _pad;

            public PortalArea(DbZonePoint point)
                : base("Shrouded Isles portal", (int)Pads[point.Id].X, (int)Pads[point.Id].Y, (int)Pads[point.Id].Z, PadRadius)
            {
                _point = point;
                _pad = Pads[point.Id];
                DisplayMessage = false;
            }

            // On the platform only: not on the steps or the ground beside it.
            public override bool IsContaining(int x, int y, int z, bool checkZ) =>
                base.IsContaining(x, y, z, false) && (!checkZ || Math.Abs(z - _pad.Z) <= PadHeightTolerance);

            public override void OnPlayerEnter(GamePlayer player)
            {
                base.OnPlayerEnter(player);
                if (player == null || !player.IsAlive || player.ObjectState != GameObject.eObjectState.Active)
                    return;
                long now = GameLoop.GameLoopTime;
                if (!MayUse(LastUse.GetValueOrDefault(player), now))
                    return;
                if (player.InCombat || GameRelic.IsPlayerCarryingRelic(player))
                {
                    player.Out.SendMessage("The portal will not take you while you are fighting or carrying a relic.",
                        eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    return;
                }
                LastUse[player] = now;
                player.LeaveHouse();
                player.Out.SendMessage("You step through the portal to the Shrouded Isles.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                player.MoveTo(_point.TargetRegion, _point.TargetX, _point.TargetY, _point.TargetZ, _point.TargetHeading);
            }
        }
    }
}
