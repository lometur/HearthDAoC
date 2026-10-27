using System.Numerics;

namespace DOL.GS.HearthDAoC.PlayerBots;

// HearthDAoC player bots (#137): a player bot's LoS answer from the server's navmesh, the way GameBots get theirs
// (StandardMobBrain.cs:141-156): positions at the objects' feet, the default filters, and line of sight where the
// zone has no mesh. LocalPathfindingMgr answers false for a zone it has no mesh for (LocalPathfindingMgr.cs:539-541),
// so the mesh is checked first. Used for monsters' checks on the bot (HeadlessPacketLib) and the bot's own
// "target in view" (PlayerBotDriver.Engage), so both use one rule.
public static class HeadlessLos
{
    public static bool Answer(IPathfindingMgr nav, GameObject source, GameObject target)
    {
        if (source == null || target == null || source.CurrentRegionID != target.CurrentRegionID)
            return false;
        return Answer(nav, source.CurrentZone, new Vector3(source.X, source.Y, source.Z), new Vector3(target.X, target.Y, target.Z));
    }

    public static bool Answer(IPathfindingMgr nav, Zone zone, Vector3 from, Vector3 to) =>
        zone == null || nav == null || !nav.IsAvailable || !nav.HasNavmesh(zone)
        || nav.HasLineOfSight(zone, from, to, nav.DefaultFilters);
}
