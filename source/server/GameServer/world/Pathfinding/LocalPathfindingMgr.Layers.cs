using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace DOL.GS
{
    // HearthDAoC: the player bots' layer-aware queries (#166), PathStraightLayered and MoveAlongSurfaceLayered in
    // Pathing/Detour/Source/DetourInterface.cpp. In Jordheim small floors walled in under a ramp are linked, at tile seams,
    // only to the ramp 70-152 units above them: player bots fell through the ramp onto them and stood there for good.
    // These queries treat such a link as a change of floor; only PlayerTravelPath with layers (player bots) calls them.
    // A lib/Detour built before them lacks the two exports: then the plain queries, with one log line.
    public sealed partial class LocalPathfindingMgr
    {
        private static int _noLayers;

        [LibraryImport("lib/Detour")]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static partial EDtStatus PathStraightLayered(IntPtr queryPtr, ReadOnlySpan<float> start, ReadOnlySpan<float> end,
            ReadOnlySpan<float> polyPickExt, ReadOnlySpan<EDtPolyFlags> queryFilter, EDtStraightPathOptions pathOptions,
            out int pointCount, Span<float> pointBuffer, Span<EDtPolyFlags> pointFlags);

        [LibraryImport("lib/Detour")]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static partial EDtStatus MoveAlongSurfaceLayered(IntPtr query, ReadOnlySpan<float> start, ReadOnlySpan<float> end,
            ReadOnlySpan<float> polyPickExt, ReadOnlySpan<EDtPolyFlags> filter, int grounded, Span<float> outputVector);

        /// <summary>GetPathStraight, with each layer link of the corridor (a tile-seam link whose floors lie more than
        /// walkableClimb apart at both ends) as two corners at one spot, the second, on the floor it enters, flagged
        /// <see cref="EDtPolyFlags.LayerLink"/>, and each corner on a floor: its polygon's, or where the floors on the two
        /// sides of its portal part, the one before it, which the walker reaches it on.</summary>
        public override PathfindingResult GetPathStraightLayered(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters,
            Span<WrappedPathfindingNode> destination)
        {
            if (Volatile.Read(ref _noLayers) == 0)
                try { return PathLayered(zone, start, end, filters, destination); }
                catch (EntryPointNotFoundException e) { NoLayers(e); }
            return GetPathStraight(zone, start, end, filters, destination);
        }

        /// <summary>GetMoveAlongSurfaceGrounded (grounded) or GetMoveAlongSurface, for which a layer link is a wall.</summary>
        public override Vector3? GetMoveAlongSurfaceLayered(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters, bool grounded)
        {
            if (Volatile.Read(ref _noLayers) == 0)
                try { return MoveLayered(zone, start, end, filters, grounded); }
                catch (EntryPointNotFoundException e) { NoLayers(e); }
            return grounded ? GetMoveAlongSurfaceGrounded(zone, start, end, filters) : GetMoveAlongSurface(zone, start, end, filters);
        }

        private static void NoLayers(EntryPointNotFoundException e)
        {
            if (Interlocked.Exchange(ref _noLayers, 1) == 0 && log.IsWarnEnabled)
                log.Warn("lib/Detour lacks the player bots' layer-aware queries (#166), so they use the plain ones: build it " +
                    "from source/server/Pathing/Detour, as deploy/Dockerfile does", e);
        }

        private PathfindingResult PathLayered(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters,
            Span<WrappedPathfindingNode> destination)
        {
            if (!TryGetQuery(zone, out NavMeshQuery query)) return new(PathfindingStatus.NoPathFound, 0);
            Span<float> a = stackalloc float[3], b = stackalloc float[3], points = stackalloc float[MAX_POLY * 3];
            Span<EDtPolyFlags> flags = stackalloc EDtPolyFlags[MAX_POLY];
            FillRecastFloats(start, a);
            FillRecastFloats(end, b);
            long profiled = NavQueryProfile.Start();
            EDtStatus status = PathStraightLayered(query, a, b, _defaultHalfExtents, filters,
                EDtStraightPathOptions.DT_STRAIGHTPATH_ALL_CROSSINGS, out int count, points, flags);
            NavQueryProfile.Stop(NavQueryProfile.Kind.PathStraight, profiled);
            if ((status & EDtStatus.DT_SUCCESS) == 0) return new(PathfindingStatus.NoPathFound, 0);
            if (destination.Length < count) return new(PathfindingStatus.BufferTooSmall, count);
            for (int i = 0; i < count; i++)
                destination[i] = new(new(points[i * 3] * INV_FACTOR, points[i * 3 + 2] * INV_FACTOR, points[i * 3 + 1] * INV_FACTOR), flags[i]);
            int kept = MergeCoincidentNodes(destination, count);
            return new((status & EDtStatus.DT_PARTIAL_RESULT) != 0 ? PathfindingStatus.PartialPathFound : PathfindingStatus.PathFound, kept);
        }

        private Vector3? MoveLayered(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters, bool grounded)
        {
            if (!TryGetQuery(zone, out NavMeshQuery query)) return null;
            Span<float> a = stackalloc float[3], b = stackalloc float[3], result = stackalloc float[3];
            FillRecastFloats(start, a);
            FillRecastFloats(end, b);
            long profiled = NavQueryProfile.Start();
            EDtStatus status = MoveAlongSurfaceLayered(query, a, b, _defaultHalfExtents, filters, grounded ? 1 : 0, result);
            NavQueryProfile.Stop(NavQueryProfile.Kind.OtherQuery, profiled);
            return (status & EDtStatus.DT_SUCCESS) == 0 ? null : new(result[0] * INV_FACTOR, result[2] * INV_FACTOR, result[1] * INV_FACTOR);
        }
    }
}
