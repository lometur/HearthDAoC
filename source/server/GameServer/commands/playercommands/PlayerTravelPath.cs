using System;
using System.Collections.Generic;
using System.Numerics;

namespace DOL.GS.Commands
{
    /// <summary>Private path cursor for an explicitly requested human journey.
    /// Uses shared mesh geometry only; never creates or controls a player bot.</summary>
    public sealed class PlayerTravelPath
    {
        private readonly Queue<WrappedPathfindingNode> _nodes = new();
        private Zone _zone;
        private Vector3 _goal;
        private Vector3? _seamExit;
        private bool _crossingSeam;
        private Vector3 _lastProgress;
        private long _lastProgressTick;
        private int _replans;
        private Vector3? _doorToOpen;
        public Vector3? DoorNode => _doorToOpen ?? (_nodes.TryPeek(out var node) && (node.Flags & EDtPolyFlags.AnyDoor) != 0
            ? node.Position : null);

        public void Clear()
        {
            _nodes.Clear(); _zone = null; _seamExit = null; _crossingSeam = false;
            _lastProgressTick = 0; _replans = 0;
            _doorToOpen = null;
        }

        public static Vector3 Advance(Vector3 current, Vector3 next, float distance)
        {
            Vector3 delta = next - current;
            float length = delta.Length();
            return length <= distance || length < 0.01f ? next : current + delta * (distance / length);
        }

        // HearthDAoC: 24 units of height, not 64. A player bot (#137) on switchback stairs counted the corners at their foot,
        // 37 units below it, as reached, and climbed back up toward the next corner, which lies under the stairs.
        // A corner within 2 units keeps 64: a step on the floor cannot change height on the spot, and a player bot stood
        // for good at the top of a descent whose next corner lay at its own spot, 24.4 units below its feet.
        public static bool HasReachedCorner(Vector3 current, Vector3 corner)
        {
            float flat = Vector2.DistanceSquared(new(current.X, current.Y), new(corner.X, corner.Y));
            float height = Math.Abs(current.Z - corner.Z);
            return flat <= 32 * 32 && height <= 24 || flat <= 2 * 2 && height <= 64;
        }

        // HearthDAoC: whether a step brought the walker less than a unit closer to a point. A step that only drops it
        // back onto the floor under the point brings it no closer. Player bots (#166).
        public static bool NoStepCloser(Vector3 from, Vector3 to, Vector3 toward) =>
            Vector3.Distance(to, toward) > Vector3.Distance(from, toward) - 1;

        public static bool TryBuildCorridor(IPathfindingMgr nav, Zone zone, Vector3 start, Vector3 goal,
            out List<WrappedPathfindingNode> route)
        {
            route = new();
            if (zone == null || !nav.HasNavmesh(zone)) return false;
            Vector3? origin = nav.GetClosestPoint(zone, start, 64, 64, 128, nav.DefaultFilters);
            Vector3? finish = nav.GetClosestPoint(zone, goal, 128, 128, 256, nav.DefaultFilters);
            if (!origin.HasValue || !finish.HasValue) return false;
            Vector3 cursor = origin.Value;
            route.Add(new(cursor, EDtPolyFlags.Walk));
            var buffer = new WrappedPathfindingNode[512];
            // Bounded continuation of long mesh corridors, never a raw straight-line fallback.
            for (int segment = 0; segment < 8; segment++)
            {
                PathfindingResult result = nav.GetPathStraight(zone, cursor, finish.Value, nav.DefaultFilters, buffer);
                if (result.NodeCount <= 0 || result.NodeCount > buffer.Length ||
                    result.Status is not (PathfindingStatus.PathFound or PathfindingStatus.PartialPathFound or PathfindingStatus.BufferTooSmall))
                    return false;
                for (int i = 0; i < result.NodeCount; i++)
                    if (Vector3.DistanceSquared(route[^1].Position, buffer[i].Position) > 0.01f)
                        route.Add(buffer[i]);
                Vector3 end = buffer[result.NodeCount - 1].Position;
                if (result.Status == PathfindingStatus.PathFound && Vector3.DistanceSquared(end, finish.Value) <= 48 * 48)
                    return true;
                if (Vector3.DistanceSquared(end, cursor) < 64 * 64 ||
                    Vector3.DistanceSquared(end, finish.Value) >= Vector3.DistanceSquared(cursor, finish.Value))
                    return false;
                cursor = end;
            }
            return false;
        }

        public bool TryStep(Region region, Zone zone, Vector3 current, Vector3 goal, float distance,
            long now, IPathfindingMgr nav, out Vector3 position, out string failure)
        {
            position = current; failure = string.Empty; _doorToOpen = null;
            if (zone == null || region == null || !nav.HasNavmesh(zone))
            {
                failure = "No navigation mesh is available at your current location.";
                return false;
            }
            // Finish the already validated 128-unit seam bridge before switching meshes.
            if (_crossingSeam && _seamExit.HasValue)
            {
                position = Advance(current, _seamExit.Value, distance);
                if (Vector3.DistanceSquared(position, _seamExit.Value) < 4) Clear();
                return true;
            }
            if (_zone != zone || Vector3.DistanceSquared(goal, _goal) > 64 * 64)
            {
                Clear(); _zone = zone; _goal = goal; _lastProgress = current; _lastProgressTick = now;
            }
            if (Vector3.DistanceSquared(current, _lastProgress) >= 16 * 16)
            {
                _lastProgress = current; _lastProgressTick = now;
            }
            else if (now - _lastProgressTick >= 15_000)
            {
                _lastProgressTick = now;
                _nodes.Clear(); _seamExit = null;
                if (++_replans > 2)
                {
                    failure = "The walking route is blocked after two replans; manual control restored.";
                    return false;
                }
            }
            if (_nodes.Count == 0)
            {
                Zone targetZone = region.GetZone((int)goal.X, (int)goal.Y);
                Vector3 leg = goal;
                if (targetZone != zone)
                {
                    if (!AutonomousZoneItinerary.TryNextStep(region, zone, targetZone, current, goal, nav, out var seam))
                    {
                        failure = "No connected, walkable zone border was found for this journey.";
                        return false;
                    }
                    leg = seam.Inside; _seamExit = seam.Outside;
                }
                if (!TryBuildCorridor(nav, zone, current, leg, out var route))
                {
                    failure = "No complete walkable path was found from here; try an open road or another destination.";
                    return false;
                }
                foreach (var node in route) _nodes.Enqueue(node);
            }
            // Detour's straight-path corners and surface projection can differ
            // by one mesh voxel (observed 13-24 units near Cotswold). Requiring
            // exact equality stalls at the polygon edge. Accept one voxel, then
            // constrain the next movement to the surface; never teleport across it.
            while (_nodes.TryPeek(out var reached) && HasReachedCorner(current, reached.Position))
            {
                if ((reached.Flags & EDtPolyFlags.AnyDoor) != 0) _doorToOpen = reached.Position;
                _nodes.Dequeue();
            }
            if (_nodes.Count == 0)
            {
                if (_seamExit.HasValue)
                {
                    _crossingSeam = true;
                    position = Advance(current, _seamExit.Value, distance);
                }
                return true;
            }
            Vector3 desired = Advance(current, _nodes.Peek().Position, distance);
            // HearthDAoC: on the real navmesh, first the surface move GameBots use, which takes the height of the polygon
            // it reached (AutonomousNavigationSurface.MoveAlongGround). The plain move is a 2D query: stopped at an edge
            // it returns the edge's vertex height, and a player bot (#137) by Cotswold bounced about 66 units a step
            // between two levels and crept at 2 units a second for minutes. The grounded move fails on a polygon edge,
            // where the plain move and the floor fallback below still apply.
            Vector3? snapped = (nav is LocalPathfindingMgr ? AutonomousNavigationSurface.MoveAlongGround(nav, zone, current, desired) : null)
                ?? nav.GetMoveAlongSurface(zone, current, desired, nav.DefaultFilters);
            // At certain polygon edges Detour's local surface mover returns the
            // origin even though its corridor and raycast agree the next step is
            // open (Cotswold stable approach). A normal-speed step is allowed only
            // if its endpoint is on mesh and the entire segment has mesh LOS.
            // This is not an offset/nudge or a direct-to-goal fallback.
            if (snapped.HasValue && Vector3.DistanceSquared(current, snapped.Value) < 1 &&
                Vector3.DistanceSquared(current, desired) >= 1)
            {
                Vector3? floor = nav.GetClosestPoint(zone, desired, 16, 16, 64, nav.DefaultFilters);
                // HearthDAoC: the floor may lie up to one voxel (24) above or below the step, as corners do (see above):
                // a player bot (#137) by Cotswold found it 5 units above the corner and stood there for good.
                if (floor.HasValue &&
                    Vector2.DistanceSquared(new(floor.Value.X, floor.Value.Y), new(desired.X, desired.Y)) <= 4 &&
                    Math.Abs(floor.Value.Z - desired.Z) <= 24 &&
                    Vector3.Distance(current, floor.Value) <= distance + 2 &&
                    nav.HasLineOfSight(zone, current, floor.Value, nav.DefaultFilters)) snapped = floor;
            }
            // HearthDAoC: no step closer to the corner, yet the mesh is open along the corridor: the step follows it, as a
            // GameBot walks its path. Detour's straight-path corners take the polygons' height, which on steep ground lies
            // far off the floor the surface moves keep: player bots (#166) stood for the rest of a run beside a corner 70
            // units below them, bounced under one 123 units above, or stood at a polygon edge no surface move crossed.
            // The step stays where the surface moves still find the mesh (their 32/64 box, LocalPathfindingMgr.cs:50):
            // from beyond it every step lost the walkable surface for good.
            if (snapped.HasValue && NoStepCloser(current, snapped.Value, _nodes.Peek().Position) &&
                Vector3.DistanceSquared(current, desired) >= 1 &&
                nav.HasLineOfSight(zone, current, desired, nav.DefaultFilters) &&
                nav.GetClosestPoint(zone, desired, 32, 32, 64, nav.DefaultFilters).HasValue) snapped = desired;
            if (!snapped.HasValue)
            {
                failure = "Lost the walkable surface; manual control restored.";
                return false;
            }
            position = snapped.Value;
            return true;
        }
    }
}
