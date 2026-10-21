using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using DOL.GS.Commands;

namespace DOL.GS.HearthDAoC.PlayerBots;

public enum PlayerBotStepResult { Arrived, Moving, Waiting, Blocked }

// HearthDAoC #137 spike: walks a GamePlayer along the navmesh with the existing PlayerTravelPath cursor
// (commands/playercommands/PlayerTravelPath.cs:9-168: corridor, zone seams, replans), writes X/Y/Z and calls
// GamePlayer.OnPositionUpdateFromPacket (GamePlayer.cs:869-872), so the player's own movement code broadcasts, checks
// speed and moves subzones as for a client packet. Steps are MaxSpeed x elapsed with no floor: the speed-hack monitor
// (PlayerMovementMonitor.cs:84-148) now sees them.
public sealed class PlayerBotPathMover
{
    public const int DirectStepRange = 300;            // closer than this: one surface step, no corridor
    public const int TeleportResetDistance = 256;      // a jump this big (teleport, release, speed-hack correction) drops the corridor
    public const int IdleBroadcastMilliseconds = HeadlessClient.HeartbeatMs; // (est.) < 5 s is proven by PMC:15
    public const int StallSteps = 12;                  // 3 s without progress = blocked

    private readonly GamePlayer _player;
    private readonly IPathfindingMgr _nav;             // null = PathfindingProvider.Instance (set at start-up, PathfindingProvider.cs:11-32)
    private readonly PlayerTravelPath _path = new();
    private Vector3 _lastApplied;
    private long _lastStepTick, _lastUpdateTick;
    private int _stalledSteps;
    private int _walkSteps;                            // WalkReport: steps and distance since its last call
    private double _walked;

    /// <summary>Why the last Blocked came (the corridor's failure text, "stalled" or "no zone"); for the logs.</summary>
    public string LastBlock { get; private set; } = string.Empty;

    /// <summary>For the errand's walk line: the steps and distance since the last call, and the live corridor cursor
    /// (the promotion check's walk to Captain Falron covered 6,000 of 35,000 units in 15 minutes, with no block).</summary>
    public string WalkReport()
    {
        string report = $"steps={_walkSteps} walked={_walked:0} avgStep={(_walkSteps == 0 ? 0 : _walked / _walkSteps):0.0} " +
                        $"live: {Cursor(_path)}";
        (_walkSteps, _walked) = (0, 0);
        return report;
    }

    public PlayerBotPathMover(GamePlayer player, IPathfindingMgr nav = null) { _player = player; _nav = nav; }
    private IPathfindingMgr Nav => _nav ?? PathfindingProvider.Instance;

    public static float StepLength(short maxSpeed, long elapsedMilliseconds)
    {
        if (maxSpeed <= 0) return 0;                                   // rooted, CC (MaxSpeedCalculator.cs:29-30): no floor
        if (elapsedMilliseconds <= 0 || elapsedMilliseconds > 2 * PlayerBotDriver.TickMilliseconds)
            elapsedMilliseconds = PlayerBotDriver.TickMilliseconds;    // first step after a stop or a stall
        return maxSpeed * elapsedMilliseconds / 1000f;
    }

    public static bool TryComputeNext(IPathfindingMgr nav, Region region, Zone zone, PlayerTravelPath path, Vector3 current,
        Vector3 goal, int stopDistance, float stepLength, long now, out Vector3 next, out string failure)
    {
        next = current;
        failure = string.Empty;
        float remaining = Vector3.Distance(current, goal) - stopDistance;
        if (remaining <= 0) return true;
        float step = Math.Min(stepLength, remaining);                  // never step inside the stop radius
        if (step < 1) return true;
        if (region == null || zone == null) { failure = "No zone here."; return false; }
        if (!nav.HasNavmesh(zone) || remaining <= DirectStepRange)
        {
            Vector3 desired = PlayerTravelPath.Advance(current, goal, step);                     // PTP:30-35
            next = nav.GetMoveAlongSurface(zone, current, desired, nav.DefaultFilters) ?? desired; // as APP:516-520
            return true;
        }
        return path.TryStep(region, zone, current, goal, step, now, nav, out next, out failure); // PTP:73-167
    }

    public PlayerBotStepResult StepToward(Vector3 goal, int stopDistance, long now)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            Vector3 current = new(_player.X, _player.Y, _player.Z);
            if (Vector3.DistanceSquared(current, _lastApplied) > TeleportResetDistance * TeleportResetDistance) _path.Clear();
            if (Vector3.Distance(current, goal) <= stopDistance) { Stop(now); return PlayerBotStepResult.Arrived; }
            float step = StepLength(_player.MaxSpeed, now - _lastStepTick);
            _lastStepTick = now;
            if (step < 1) { Stop(now); return PlayerBotStepResult.Waiting; }
            if (!TryComputeNext(Nav, _player.CurrentRegion, _player.CurrentZone, _path, current, goal, stopDistance, step, now,
                    out Vector3 next, out string failure))
            {
                LastBlock = failure;
                Stop(now);
                PlayerBotDriverReport.Blocked();
                return PlayerBotStepResult.Blocked;
            }
            if (Vector3.DistanceSquared(next, current) < 1)
            {
                if (++_stalledSteps < StallSteps) return PlayerBotStepResult.Moving;
                _stalledSteps = 0;
                _path.Clear();
                LastBlock = "stalled";
                Stop(now);
                PlayerBotDriverReport.Blocked();
                return PlayerBotStepResult.Blocked;
            }
            _stalledSteps = 0;
            _walkSteps++;
            _walked += Vector3.Distance(current, next);
            Zone zone = _player.CurrentRegion?.GetZone((int)next.X, (int)next.Y);
            if (zone == null) { LastBlock = "no zone"; Stop(now); return PlayerBotStepResult.Blocked; } // as APP:531-536
            _player.Heading = _player.GetHeading((int)next.X, (int)next.Y);
            _player.CurrentSpeed = _player.MaxSpeed;
            _player.X = (int)Math.Round(next.X);
            _player.Y = (int)Math.Round(next.Y);
            _player.Z = (int)Math.Round(next.Z);
            UpdateZoneAndAreas(zone);                       // PPUH order: position, areas, then OnPositionUpdateFromPacket (PPUH:30-31)
            _player.OnPositionUpdateFromPacket();           // GP:869-872 -> PMC:104-143
            _lastApplied = new(_player.X, _player.Y, _player.Z);
            _lastUpdateTick = now;
            return PlayerBotStepResult.Moving;
        }
        finally
        {
            PlayerBotDriverReport.AddMover(Stopwatch.GetTimestamp() - started);
        }
    }

    /// <summary>For the logs, at a block: what the navmesh says here. The floor under the bot, the straight path's first
    /// corners toward the goal, and one step toward the first corner (the surface move, the floor under it and the line
    /// of sight to it, as PlayerTravelPath.TryStep checks them, PTP:143-160).</summary>
    public string Probe(Vector3 goal)
    {
        Zone zone = _player.CurrentZone;
        if (zone == null || !Nav.HasNavmesh(zone)) return "navmesh=none";
        IPathfindingMgr nav = Nav;
        Vector3 here = new(_player.X, _player.Y, _player.Z);
        static string P(Vector3? v) => v is Vector3 p ? $"({p.X:0},{p.Y:0},{p.Z:0})" : "none";
        Vector3? floor = nav.GetClosestPoint(zone, here, 16, 16, 64, nav.DefaultFilters);
        WrappedPathfindingNode[] nodes = new WrappedPathfindingNode[512];   // as TryBuildCorridor (PTP:52); 8 gave BufferTooSmall/52
        PathfindingResult result = nav.GetPathStraight(zone, here, goal, nav.DefaultFilters, nodes);
        int count = Math.Min(result.NodeCount, nodes.Length);
        string corners = string.Join("->", nodes.Take(Math.Min(count, 4)).Select(n => P(n.Position) + (n.Flags == EDtPolyFlags.Walk ? "" : $"[{n.Flags}]")));
        Vector3? corner = nodes.Take(count).Select(n => (Vector3?)n.Position).FirstOrDefault(c => Vector3.Distance(c.Value, here) > 16);
        if (corner == null) return $"floor={P(floor)} path={result.Status}/{result.NodeCount} corners={corners}";
        Vector3 desired = PlayerTravelPath.Advance(here, corner.Value, 47);
        Vector3? surface = nav.GetMoveAlongSurface(zone, here, desired, nav.DefaultFilters);
        Vector3? under = nav.GetClosestPoint(zone, desired, 16, 16, 64, nav.DefaultFilters);
        bool los = under.HasValue && nav.HasLineOfSight(zone, here, under.Value, nav.DefaultFilters);
        return $"floor={P(floor)} path={result.Status}/{result.NodeCount} corners={corners} step={P(desired)} " +
               $"surface={P(surface)} stepFloor={P(under)} los={los} goalZone={_player.CurrentRegion?.GetZone((int)goal.X, (int)goal.Y)?.ID} " +
               Replay(zone, here, goal);
    }

    // One step of a fresh corridor cursor from here, as StepToward takes it after its stall cleared the path.
    private string Replay(Zone zone, Vector3 here, Vector3 goal)
    {
        PlayerTravelPath fresh = new();
        bool ok = fresh.TryStep(_player.CurrentRegion, zone, here, goal, StepLength(_player.MaxSpeed, PlayerBotDriver.TickMilliseconds),
            GameLoop.GameLoopTime, Nav, out Vector3 next, out string failure);
        return $"replay: ok={ok} moved={Vector3.Distance(here, next):0.0} next=({next.X:0},{next.Y:0},{next.Z:0}) failure=\"{failure}\" " + Cursor(fresh);
    }

    // The corridor cursor's own state (PlayerTravelPath is upstream's; read only, by reflection, for this log line).
    private static string Cursor(PlayerTravelPath path)
    {
        const System.Reflection.BindingFlags Hidden = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        static string P(Vector3? v) => v is Vector3 p ? $"({p.X:0},{p.Y:0},{p.Z:0})" : "none";
        object Field(string name) => typeof(PlayerTravelPath).GetField(name, Hidden)?.GetValue(path);
        var queue = Field("_nodes") as Queue<WrappedPathfindingNode>;
        string next = queue == null ? "?" : string.Join("->", queue.Take(5).Select(n => P(n.Position)));
        return $"cursor: nodes={queue?.Count} next={next} seamExit={P(Field("_seamExit") as Vector3?)} " +
               $"crossing={Field("_crossingSeam")} zone={(Field("_zone") as Zone)?.ID} goal={P(Field("_goal") as Vector3?)} " +
               $"replans={Field("_replans")}";
    }

    public void Stop(long now)
    {
        _lastStepTick = now;
        if (_player.CurrentSpeed == 0) return;
        _player.CurrentSpeed = 0;
        _player.OnPositionUpdateFromPacket();               // one "stopped" update, like a client's speed-0 packet
        _lastUpdateTick = now;
    }

    public void Reset() { _path.Clear(); _stalledSteps = 0; }

    public void IdleBroadcast(long now)
    {
        if (_player.CurrentSpeed != 0 || now - _lastUpdateTick < IdleBroadcastMilliseconds) return;
        if (_player.Client?.ClientState is not GameClient.eClientState.Playing ||
            _player.ObjectState is not GameObject.eObjectState.Active) return;
        _player.OnPositionUpdateFromPacket();               // X/Y unchanged: no OnPlayerMove (a sitting bot stays seated), only a broadcast (PMC:111-119)
        _lastUpdateTick = now;
    }

    // A copy of PPUH:131-160 (no "entered" messages, no MaxLastZ) and :169-214 (no border-keep RealmTimer, :188-199).
    private void UpdateZoneAndAreas(Zone zone)
    {
        GamePlayer player = _player;
        if (zone != player.LastPositionUpdateZone) player.LastPositionUpdateZone = zone;
        Region region = player.CurrentRegion;
        if (region == null || region.Time <= player.AreaUpdateTick) return;
        IList<IArea> oldAreas = player.CurrentAreas;
        IList<IArea> newAreas = region.GetAreasOfZone(zone, player);       // GS/world/Region.cs:1353
        if (oldAreas != null)
            foreach (IArea area in oldAreas)
                if (!newAreas.Contains(area)) area.OnPlayerLeave(player);
        foreach (IArea area in newAreas)
            if (oldAreas == null || !oldAreas.Contains(area)) area.OnPlayerEnter(player);
        player.CurrentAreas = newAreas;
        player.AreaUpdateTick = region.Time + 2000;
    }
}
