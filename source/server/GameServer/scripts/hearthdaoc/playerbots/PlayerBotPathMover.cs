using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using DOL.GS.Commands;
using DOL.Logging;

namespace DOL.GS.HearthDAoC.PlayerBots;

public enum PlayerBotStepResult { Arrived, Moving, Waiting, Blocked }

// HearthDAoC player bots (#137): walks a GamePlayer along the navmesh with the existing PlayerTravelPath cursor
// (commands/playercommands/PlayerTravelPath.cs:9-168: corridor, zone seams, replans), writes X/Y/Z and calls
// GamePlayer.OnPositionUpdateFromPacket (GamePlayer.cs:869-872), so the player's own movement code broadcasts, checks
// speed and moves subzones as for a client packet. Steps are MaxSpeed x elapsed with no floor: the speed-hack monitor
// (PlayerMovementMonitor.cs:84-148) now sees them.
public sealed class PlayerBotPathMover
{
    public const int DirectStepRange = 300;            // closer than this, in the open: one surface step, no corridor
    public const int FloorSag = 24;                    // the line to the goal this far under the floor takes the corridor (#166)
    public const int TeleportResetDistance = 256;      // a jump this big (teleport, release, speed-hack correction) drops the corridor
    public const int IdleBroadcastMilliseconds = HeadlessClient.HeartbeatMs; // (est.) < 5 s is proven by PMC:15
    public const int StallSteps = 12;                  // 3 s without progress = blocked
    public const int ReplanSteps = 4;                  // 1 s without progress: the corridor is planned again from here (#166)
    public const int ReplansPerWalk = 2;               // as the corridor's own progress check, blocked after two replans (PTP:126-139)
    public const int ChurnReplans = 2;                 // churning windows in a row that only replan; the next one blocks
    public const int FloorStepRange = 300;             // the GameBots' step onto the nearest floor (AWBC TryBeginLocalRouteRecovery)
    public const int FloorStepMilliseconds = 5_000;    // at most one such step per bot in this time

    private static readonly Logger Log = LoggerManager.Create(typeof(PlayerBotPathMover));

    private readonly GamePlayer _player;
    private readonly IPathfindingMgr _nav;             // null = PathfindingProvider.Instance (set at start-up, PathfindingProvider.cs:11-32)
    private readonly PlayerTravelPath _path = new();
    private Vector3 _lastApplied;
    private Vector3? _exact;                           // where the mover last put the bot, before GamePlayer rounded it
    private long _lastStepTick, _lastUpdateTick, _nextFloorStep;
    private readonly PlayerBotStallWatch _stall = new();
    private readonly PlayerBotChurnWatch _churn = new();
    private int _walkSteps;                            // WalkReport: steps and distance since its last call
    private double _walked;

    /// <summary>Why the last Blocked came (the corridor's failure text, "stalled" or "no zone"); for the logs.</summary>
    public string LastBlock { get; private set; } = string.Empty;

    /// <summary>For the blocked line (#166): the last stall's first and last steps, the step length, the bot's state
    /// and the corridor cursor. Hunting bots stood at one spot for the rest of a run, though a replay from that spot
    /// walks on.</summary>
    public string LastStall { get; private set; } = string.Empty;
    private string _stallFirst = string.Empty;

    /// <summary>For the errand's walk line: the steps and distance since the last call, and the live corridor cursor
    /// (the promotion check's walk to Captain Falron covered 6,000 of 35,000 units in 15 minutes, with no block).</summary>
    public string WalkReport()
    {
        string report = $"steps={_walkSteps} walked={_walked:0} avgStep={(_walkSteps == 0 ? 0 : _walked / _walkSteps):0.0} " +
                        $"live: {Cursor(_path)}";
        StartWalkReport();
        return report;
    }

    /// <summary>The next WalkReport counts from here (a walk starts or resumes; steps before it were other walks).</summary>
    public void StartWalkReport() => (_walkSteps, _walked) = (0, 0);

    public PlayerBotPathMover(GamePlayer player, IPathfindingMgr nav = null) { _player = player; _nav = nav; }
    private IPathfindingMgr Nav => _nav ?? PathfindingProvider.Instance;

    public static float StepLength(short maxSpeed, long elapsedMilliseconds)
    {
        if (maxSpeed <= 0) return 0;                                   // rooted, CC (MaxSpeedCalculator.cs:29-30): no floor
        if (elapsedMilliseconds <= 0 || elapsedMilliseconds > 2 * PlayerBotDriver.TickMilliseconds)
            elapsedMilliseconds = PlayerBotDriver.TickMilliseconds;    // first step after a stop or a stall
        return maxSpeed * elapsedMilliseconds / 1000f;
    }

    /// <summary>Where the next step starts: the spot the mover last put the bot on, while the bot still stands on it
    /// (GamePlayer keeps whole units, so it stands up to 0.87 units away), else the bot's own position (a teleport, a
    /// release or a speed-hack correction moved it). By Olorustos (lv-r1-stall, 2026-10-25) the whole-unit spot lay
    /// 0.6 units off the mesh: each step only snapped the bot back onto it, rounding put it back, and it stood there
    /// for 14 minutes. From the exact spot the same walk arrives in 12 steps (UT_PlayerBotWalkReplay).</summary>
    public static Vector3 StepStart(Vector3 player, Vector3? exact) =>
        exact is Vector3 spot && Vector3.DistanceSquared(player, spot) <= 1 ? spot : player;

    /// <summary>Where a bot whose walk failed steps to, as a GameBot does (AutonomousWorldBotController
    /// .TryBeginLocalRouteRecovery): standing off the walkable mesh, no step or route can start, so it steps onto the
    /// nearest real floor within 300 units and plans again from there. Null on floor with a way out (the goal was the
    /// trouble), or with no floor near. In st-fix2 (#166) six bots stood for the rest of the run where every step lost
    /// the walkable surface; in Tir na Nog one stood 84 units above the floor, which lay 51 units away. The nearest
    /// floor search counts a floor up to 128 units overhead as underfoot, but the surface moves reach only 64: in st-fix3
    /// bots stood 79-88 units under the floor for the rest of the run. Out of the moves' reach, it steps onto that floor.</summary>
    public static Vector3? FloorStep(IPathfindingMgr nav, Zone zone, Vector3 here)
    {
        if (AutonomousNavigationSurface.TryNearestWalkableFloor(nav, zone, here, FloorStepRange, out Vector3 floor)) return floor;
        return !nav.GetClosestPoint(zone, here, 32, 32, 64, nav.DefaultFilters).HasValue &&
               AutonomousNavigationSurface.TryFloor(nav, zone, here, out floor) ? floor : null;
    }

    /// <summary>Whether the bot stands within the stop radius as the driver judges range: GetDistanceTo cuts the
    /// whole-unit distance to an int (Point3D.cs:78-87), so anything under stopDistance + 1 is in.</summary>
    public static bool Arrived(Vector3 here, Vector3 goal, int stopDistance) => Vector3.Distance(here, goal) < stopDistance + 1;

    public static bool TryComputeNext(IPathfindingMgr nav, Region region, Zone zone, PlayerTravelPath path, Vector3 current,
        Vector3 goal, int stopDistance, float stepLength, long now, out Vector3 next, out string failure)
    {
        next = current;
        failure = string.Empty;
        float remaining = Vector3.Distance(current, goal) - stopDistance;
        if (remaining <= 0) return true;
        // At most one unit inside the stop radius: the whole-unit spot then lies within it (Arrived), and a bot less than
        // a unit short still steps (it stood still, and 12 such steps blocked the walk).
        float step = Math.Min(stepLength, remaining + 1);
        if (step < 1) return true;
        if (region == null || zone == null) { failure = "No zone here."; return false; }
        // A wall between (no line of sight on the mesh) takes the corridor round it: in st-smoke (#166) a bot 267 units from
        // its target crept along a wall for the rest of the run. Within a corner's reach of the goal the corridor has
        // dropped its last corner, so the direct step goes on (the review of #168: the bot stood short of the goal).
        // Nor when the line to the goal runs under the floor (#166): in Jordheim the line from the top of a ramp to a goal
        // at its foot ran 25-40 units under the ramp, over a floor walled in below it, and the next step stood on that
        // floor. The corridor's grounded steps take the ramp.
        if (!nav.HasNavmesh(zone) || remaining <= DirectStepRange &&
            (PlayerTravelPath.HasReachedCorner(current, goal) || nav.HasLineOfSight(zone, current, goal, nav.DefaultFilters) &&
             !RunsUnderFloor(nav, zone, current, goal, step)))
        {
            Vector3 desired = PlayerTravelPath.Advance(current, goal, step);                     // PTP:30-35
            // The plain move keeps the height of the straight line to the goal inside a polygon, so the last 300 units
            // follow it, not the floor. The grounded move would fix that, but arrival is judged against the goal's own
            // height: a target hovering or swimming 80+ units above the floor would then stall the bot under it (review
            // of 2026-10-21). Kept as is: it needs both, grounded steps and a floor-based arrival (not done).
            // Not down a link to another floor (GetMoveAlongSurfaceLayered, as the corridor's moves, #166).
            next = nav.GetMoveAlongSurfaceLayered(zone, current, desired, nav.DefaultFilters, false) ?? desired; // as APP:516-520
            // As the corridor's step (#166): no step closer, and the mesh is open to the goal, so the step takes the line,
            // where the surface moves still find the mesh.
            if (PlayerTravelPath.NoStepCloser(current, next, goal) &&
                nav.GetClosestPoint(zone, desired, 32, 32, 64, nav.DefaultFilters).HasValue) next = desired;
            return true;
        }
        // With layers (#166): in Jordheim the mesh links small floors walled in under a ramp only to the ramp 45-115 units
        // above them. Bots fell through the ramp onto them, and their way out back up the link hid its height in one
        // corner: they stood there for good. With layers the moves do not take such a link, and the corridor shows it as
        // two corners, one on each floor, that the bot follows straight up or down, as a GameBot walks its path.
        return path.TryStep(region, zone, current, goal, step, now, nav, out next, out failure, layers: true); // PTP:73-167
    }

    /// <summary>Whether the straight line to the goal runs more than <see cref="FloorSag"/> under the floor the bot walks
    /// on, somewhere on the way: the floor is followed one step at a time (grounded, on its own floor), as the bot would
    /// walk under the line. The whole line, not the next step: judged a step at a time, the corridor and the line took
    /// turns, one step back to a corner and one forward to the goal, until the walk was blocked (from st-final4's pocket
    /// to (31746, 27019), #166).</summary>
    private static bool RunsUnderFloor(IPathfindingMgr nav, Zone zone, Vector3 current, Vector3 goal, float step)
    {
        float length = Vector3.Distance(current, goal);
        Vector3 on = current;
        for (float along = Math.Min(step, length); ; along = Math.Min(along + step, length))
        {
            Vector3 line = Vector3.Lerp(current, goal, along / length);
            if (nav.GetMoveAlongSurfaceLayered(zone, on, line, nav.DefaultFilters, true) is not Vector3 floor ||
                Vector2.Distance(new(floor.X, floor.Y), new(line.X, line.Y)) > 1) return false;    // a wall: the step's own concern
            if (line.Z < floor.Z - FloorSag) return true;
            if (along >= length) return false;
            on = floor;
        }
    }

    public PlayerBotStepResult StepToward(Vector3 goal, int stopDistance, long now)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            Vector3 here = new(_player.X, _player.Y, _player.Z);
            Vector3 current = StepStart(here, _exact);
            if (Vector3.DistanceSquared(current, _lastApplied) > TeleportResetDistance * TeleportResetDistance)
            {
                _path.Clear();
                _churn.Reset();
            }
            _stall.Toward(goal, now);
            if (Arrived(here, goal, stopDistance)) { _stall.Reset(); Stop(now); return PlayerBotStepResult.Arrived; }
            float step = StepLength(_player.MaxSpeed, now - _lastStepTick);
            _lastStepTick = now;
            if (step < 1) { Stop(now); return PlayerBotStepResult.Waiting; }
            if (!TryComputeNext(Nav, _player.CurrentRegion, _player.CurrentZone, _path, current, goal, stopDistance, step, now,
                    out Vector3 next, out string failure))
            {
                LastBlock = failure;
                _stall.Reset();                             // the walk ended (a stall's plan again can fail here): the next counts afresh
                Stop(now);
                TryFloorStep(current, failure, now);
                PlayerBotDriverReport.Blocked();
                return PlayerBotStepResult.Blocked;
            }
            _exact = next;                                  // a snap back onto the mesh counts, though the bot stays put
            if (Vector3.DistanceSquared(next, current) < 1)
            {
                if (_stall.Steps == 0) _stallFirst = $"first={Exact(current)}->{Exact(next)} {Cursor(_path)}";
                if (!_stall.Still(_path, out bool replanned))
                {
                    if (replanned && Log.IsInfoEnabled)
                        Log.Info($"PLAYER_BOT_REPLAN bot={_player.Name} at=({current.X:0},{current.Y:0},{current.Z:0}) " +
                                 $"goal=({goal.X:0},{goal.Y:0},{goal.Z:0}) replan={_stall.Replans}/{ReplansPerWalk} {_stallFirst}");
                    return PlayerBotStepResult.Moving;
                }
                LastStall = $"here={Exact(here)} last={Exact(current)}->{Exact(next)} step={step:0.0} " +
                            $"speed={_player.MaxSpeed} sitting={_player.IsSitting} stealthed={_player.IsStealthed} " +
                            $"combat={_player.InCombat} {_stallFirst} | last {Cursor(_path)} | {StallQueries(current, goal, step)}";
                _stall.Reset();
                _path.Clear();
                LastBlock = "stalled";
                Stop(now);
                TryFloorStep(current, "stalled", now);      // off the mesh, a step can stand still without failing (#166)
                PlayerBotDriverReport.Blocked();
                return PlayerBotStepResult.Blocked;
            }
            _stall.Moved();
            float moved = Vector3.Distance(current, next);
            _walkSteps++;
            _walked += moved;
            if (_churn.Observe(next, goal, moved, now))
            {
                _path.Clear();                              // plan again from here (the replay's fresh path from the stuck spot was fine)
                if (_churn.ChurningWindows > ChurnReplans)
                {
                    _churn.Reset();
                    _stall.Reset();
                    LastBlock = "churning";
                    Stop(now);
                    PlayerBotDriverReport.Blocked();
                    return PlayerBotStepResult.Blocked;
                }
            }
            Zone zone = _player.CurrentRegion?.GetZone((int)next.X, (int)next.Y);
            if (zone == null) { _stall.Reset(); LastBlock = "no zone"; Stop(now); return PlayerBotStepResult.Blocked; } // as APP:531-536
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

    // #166: the stalled step's own queries, from where it started, toward the cursor's next corner (Tir na Nog,
    // st-fix5: every step from one spot came back to it, while the same queries offline walk on).
    private string StallQueries(Vector3 current, Vector3 goal, float step)
    {
        Zone zone = _player.CurrentZone;
        IPathfindingMgr nav = Nav;
        if (zone == null || !nav.HasNavmesh(zone)) return "queries: navmesh=none";
        static string P(Vector3? v) => v is Vector3 p ? $"({p.X:0.0},{p.Y:0.0},{p.Z:0.0})" : "none";
        var nodes = typeof(PlayerTravelPath).GetField("_nodes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(_path) as Queue<WrappedPathfindingNode>;
        if (nodes == null || !nodes.TryPeek(out WrappedPathfindingNode node)) return "queries: corner=none " + Replay(zone, current, goal);
        Vector3 desired = PlayerTravelPath.Advance(current, node.Position, step);
        return $"queries: corner={P(node.Position)}[{node.Flags}] desired={P(desired)} " +
               $"ground={P(AutonomousNavigationSurface.MoveAlongGround(nav, zone, current, desired))} " +
               $"surface={P(nav.GetMoveAlongSurface(zone, current, desired, nav.DefaultFilters))} " +
               $"los={nav.HasLineOfSight(zone, current, desired, nav.DefaultFilters)} " +
               $"box={P(nav.GetClosestPoint(zone, desired, 32, 32, 64, nav.DefaultFilters))} " +
               $"underfoot={P(nav.GetClosestPoint(zone, current, 2, 2, 64, nav.DefaultFilters))} " + Replay(zone, current, goal);
    }

    // One step of a fresh corridor cursor from here, as StepToward takes it after its stall cleared the path.
    private string Replay(Zone zone, Vector3 here, Vector3 goal)
    {
        PlayerTravelPath fresh = new();
        bool ok = fresh.TryStep(_player.CurrentRegion, zone, here, goal, StepLength(_player.MaxSpeed, PlayerBotDriver.TickMilliseconds),
            GameLoop.GameLoopTime, Nav, out Vector3 next, out string failure, layers: true);
        return $"replay: ok={ok} moved={Vector3.Distance(here, next):0.0} next=({next.X:0},{next.Y:0},{next.Z:0}) failure=\"{failure}\" " + Cursor(fresh);
    }

    // The corridor cursor's own state (PlayerTravelPath is upstream's; read only, by reflection, for this log line).
    private static string Exact(Vector3 v) => $"({v.X:0.00},{v.Y:0.00},{v.Z:0.00})";

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

    public void Reset() { _path.Clear(); _stall.Reset(); _churn.Reset(); }

    // When a walk ends blocked: a step onto the nearest floor that leads on (FloorStep), at most one search per
    // FloorStepMilliseconds.
    private void TryFloorStep(Vector3 current, string why, long now)
    {
        if (now < _nextFloorStep) return;
        _nextFloorStep = now + FloorStepMilliseconds;       // one search in the time, found or not: it runs on the game loop (the review of #168)
        if (FloorStep(Nav, _player.CurrentZone, current) is not Vector3 floor) return;
        if (Log.IsInfoEnabled)
            Log.Info($"PLAYER_BOT_FLOOR_STEP bot={_player.Name} from=({current.X:0},{current.Y:0},{current.Z:0}) " +
                     $"to=({floor.X:0},{floor.Y:0},{floor.Z:0}) distance={Vector3.Distance(current, floor):0} why=\"{why}\"");
        _player.MoveTo(_player.CurrentRegionID, (int)Math.Round(floor.X), (int)Math.Round(floor.Y), (int)Math.Round(floor.Z),
            _player.Heading);
        Reset();
        _exact = null;
    }

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

/// <summary>Steps in a row that brought the bot less than a unit on (#166). At ReplanSteps the corridor is planned again
/// from where the bot stands, keeping the goal, at most ReplansPerWalk times a walk; at StallSteps the walk is blocked.
/// In st-fix6 most stalls left had the corridor's next corner on another level than the bot (a ramp above it), while a
/// fresh corridor from the same spot walked on; the walk ended blocked and the bot gave up its goal. A limit a walk: a
/// fresh corridor that leads to another stall would otherwise plan again at each one, and a bot walking to and fro
/// would never be blocked (Clear also restarts the corridor's own 15 s progress check). Once a goal was too few: in
/// st-replan2 bots stalled again where a fresh corridor walked on. A walk ends when it arrives or is blocked, or when the
/// steps toward it pause or its goal jumps, as the churn watch tells a new walk (the review of the replan: counts kept
/// from one walk denied the next its plans again).</summary>
public sealed class PlayerBotStallWatch
{
    private Vector3 _goal;
    private long _last;

    /// <summary>Still steps in a row.</summary>
    public int Steps { get; private set; }

    /// <summary>Times the corridor was planned again in this walk.</summary>
    public int Replans { get; private set; }

    /// <summary>Each step toward goal, before it is taken: a pause of more than PlayerBotChurnWatch.PauseMilliseconds since
    /// the last step starts a new walk, and a goal more than PlayerBotChurnWatch.GoalMoved from the last step's starts a
    /// new walk's replans. The still steps count on across a goal jump: a stalled bot whose target ran more than 64 units a
    /// step never ended blocked (the review).</summary>
    public void Toward(Vector3 goal, long now)
    {
        const float goalMoved = PlayerBotChurnWatch.GoalMoved;
        if (now - _last > PlayerBotChurnWatch.PauseMilliseconds) Reset();
        else if (Vector3.DistanceSquared(goal, _goal) > goalMoved * goalMoved) Replans = 0;
        (_goal, _last) = (goal, now);
    }

    /// <summary>One still step: true when the walk is now blocked; replanned when it planned the corridor again.</summary>
    public bool Still(PlayerTravelPath path, out bool replanned)
    {
        replanned = ++Steps == PlayerBotPathMover.ReplanSteps && Replans < PlayerBotPathMover.ReplansPerWalk;
        if (replanned)
        {
            Replans++;
            path.Clear();
        }
        return Steps >= PlayerBotPathMover.StallSteps;
    }

    /// <summary>A step that moved the bot: the next still step starts a new run, in the same walk.</summary>
    public void Moved() => Steps = 0;

    /// <summary>The walk ended: the next one counts afresh.</summary>
    public void Reset() => (Steps, Replans) = (0, 0);
}

/// <summary>Walking that gets nowhere: a 15 s window of one walk that walked over 300 units and ended less than a third
/// of that from where it began. The walk replay of 2026-10-21 took 120 steps of about 81 units every 30 s and got 45-50
/// units nearer its goal, bouncing between two levels below a corridor corner; the corridor's own check (16 units in
/// 15 s, PlayerTravelPath.cs:95-108) counted that as progress and never replanned. A new goal or a pause starts a new
/// walk: roaming between random points (a leg of about 1,300 units, 2-3 legs a window) often ends a window near
/// its start without churning.</summary>
public sealed class PlayerBotChurnWatch
{
    public const int WindowMilliseconds = 15_000;
    public const float MinimumWalked = 300;
    public const float GoalMoved = 64;                 // a goal this far from the window's: a new walk (as PTP:91)
    public const int PauseMilliseconds = 1_000;        // no step for this long (a fight, a rest, a talk): a new walk

    private Vector3 _start, _goal;
    private long _since = -1, _last;
    private float _walked;

    /// <summary>Churning windows in a row of one walk; a clean window or a new walk sets it back to 0.</summary>
    public int ChurningWindows { get; private set; }

    /// <summary>One applied step toward goal; true when it closes a churning window.</summary>
    public bool Observe(Vector3 position, Vector3 goal, float step, long now)
    {
        if (_since >= 0 && (now - _last > PauseMilliseconds || Vector3.DistanceSquared(goal, _goal) > GoalMoved * GoalMoved))
            Reset();
        _last = now;
        if (_since < 0) { (_start, _goal, _since, _walked) = (position, goal, now, 0); return false; }
        _walked += step;
        if (now - _since < WindowMilliseconds) return false;
        bool churning = _walked > MinimumWalked && Vector3.Distance(position, _start) * 3 < _walked;
        (_start, _since, _walked) = (position, now, 0);
        ChurningWindows = churning ? ChurningWindows + 1 : 0;
        return churning;
    }

    public void Reset() => (_since, ChurningWindows) = (-1, 0);
}
