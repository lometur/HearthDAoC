using System.Globalization;
using CEM.Utils;
using OpenDAoC.Pathing;
using N = System.Numerics;
using O = OpenTK;

namespace CEM.World;

/// <summary>
/// Zone 249 candidate: follow the source climb surface in short steps and
/// attach its ends only to pass-1 collision-derived Detour floors.
/// </summary>
internal static class DarknessFallsClimbLinks
{
    private static readonly EDtPolyFlags[] Walk =
        [EDtPolyFlags.All ^ EDtPolyFlags.Disabled ^ EDtPolyFlags.Jump, 0];
    private static readonly float[] Along = [0, 40, -40, 80, -80, 120, -120, 160, -160, 200, -200, 240, -240];
    private static readonly float[] Away = [32, 48, 64, 80, 104, 128];
    private const float StepZ = 64;
    private const float SurfaceClearance = 56;
    private const float MaxFloorGap = 180;
    private const float MaxSegment = 190;
    private static readonly N.Vector3[][] EntranceTreads =
    [
        [new(33480, 27755, 22893.5f), new(33756, 27791, 22705.9f),
         new(33862, 27793, 22514), new(34269, 27678, 22381.5f)],
        [new(16182, 18540, 22893), new(15948, 18438, 22705.4f),
         new(15832, 18426, 22513.5f), new(15305, 18482, 22381)],
        [new(44343, 37995, 20845.5f), new(44109, 37892, 20657.9f),
         new(43993, 37881, 20466), new(43466, 37936, 20333.5f)],
    ];

    public static LadderLinkPlacer.PlacementResult Place(
        string navPath, string objPath, IReadOnlyList<LadderDefinition> ladders)
    {
        LadderLinkPlacer.PlacementResult result = new();
        using DetourNavMesh? mesh = DetourNavMesh.TryLoad(navPath);
        if (mesh == null)
            throw new InvalidOperationException($"Cannot load DF pass-1 mesh: {navPath}");
        using DetourNavMeshQuery query = mesh.CreateQuery();
        List<Triangle> collision = ReadNearbyCollision(objPath, ladders);

        foreach (LadderDefinition ladder in ladders)
        {
            bool placed = TryPlaceLadder(query, collision, ladder, result);
            Log.Normal($"DF climb {ladder.Name}: {(placed ? "segmented, collision-checked" : "UNRESOLVED")}");
        }

        // The three entrance wings have one-way fall ledges, not bidirectional
        // steps. Their directed drop geometry is staged separately with an
        // explicit over-lip/gravity waypoint. Never emit the old experimental
        // reverse-capable entrance OMCs from this generic ladder result.
        Log.Warn("DF entrance falls require directed, fall-aware handling; " +
                 "no entrance links emitted by the climb builder.");

        return result;
    }

    private static void PlaceEntranceSteps(DetourNavMeshQuery query, List<Triangle> collision,
                                           LadderLinkPlacer.PlacementResult result)
    {
        for (int realm = 0; realm < EntranceTreads.Length; realm++)
        {
            N.Vector3[] treads = EntranceTreads[realm];
            for (int i = 0; i < treads.Length - 1; i++)
            {
                N.Vector3 upper = treads[i], lower = treads[i + 1];
                if (!HasHorizontalCollision(upper, collision) || !HasHorizontalCollision(lower, collision))
                {
                    Log.Warn($"DF entrance realm {realm + 1} step {i}: source collision face absent");
                    continue;
                }
                List<N.Vector3> a = FindTreadEdge(query, upper, lower);
                List<N.Vector3> b = FindTreadEdge(query, lower, upper);
                var pair = (from x in a from y in b
                            where N.Vector3.Distance(x, y) <= 225 && LineClear(x, y, collision)
                            orderby N.Vector3.Distance(x, y)
                            select (x, y)).FirstOrDefault();
                if (pair == default)
                {
                    float minGap = (from x in a from y in b select N.Vector3.Distance(x, y))
                        .DefaultIfEmpty(float.PositiveInfinity).Min();
                    float clearGap = (from x in a from y in b
                                      where LineClear(x, y, collision)
                                      select N.Vector3.Distance(x, y))
                        .DefaultIfEmpty(float.PositiveInfinity).Min();
                    Log.Warn($"DF entrance realm {realm + 1} step {i}: no adjacent-tread connector " +
                             $"(upper samples {a.Count}, lower {b.Count}, nearest {minGap:F1}, " +
                             $"nearest collision-clear {clearGap:F1})");
                    continue;
                }
                result.Links.Add(new(ToOpen(pair.x), ToOpen(pair.y)));
                Log.Normal($"DF entrance realm {realm + 1} step {i}: " +
                           $"{pair.x.X:F0},{pair.x.Y:F0},{pair.x.Z:F0} -> " +
                           $"{pair.y.X:F0},{pair.y.Y:F0},{pair.y.Z:F0}");
            }
        }
    }

    private static bool HasHorizontalCollision(N.Vector3 p, List<Triangle> triangles) =>
        triangles.Any(t =>
        {
            N.Vector3 center = (t.A + t.B + t.C) / 3;
            N.Vector3 n = N.Vector3.Cross(t.B - t.A, t.C - t.A);
            return DistanceXY(center, p) < 72 && Math.Abs(center.Z - p.Z) < 24 &&
                   n.LengthSquared() > 1 && Math.Abs(n.Z) / n.Length() > 0.85f;
        });

    private static List<N.Vector3> FindTreadEdge(DetourNavMeshQuery query, N.Vector3 from, N.Vector3 toward)
    {
        List<N.Vector3> points = [];
        N.Vector2 axis = Unit(new(toward.X - from.X, toward.Y - from.Y));
        N.Vector2 lateral = new(-axis.Y, axis.X);
        float distance = DistanceXY(from, toward);
        for (float along = 0; along <= distance + 64; along += 24)
        for (float side = -48; side <= 48; side += 48)
        {
            N.Vector3 probe = new(from.X + axis.X * along + lateral.X * side,
                                    from.Y + axis.Y * along + lateral.Y * side, from.Z);
            N.Vector3? hit = query.FindClosestPointInBox(probe, new(16, 16, 24), probe, Walk);
            if (!hit.HasValue || Math.Abs(hit.Value.Z - from.Z) > 24 ||
                DistanceXY(hit.Value, probe) > 32)
                continue;
            if (!points.Any(p => N.Vector3.Distance(p, hit.Value) < 16))
                points.Add(hit.Value);
        }
        return points.OrderByDescending(p => DistanceXY(p, from)).Take(24).ToList();
    }

    private static bool TryPlaceLadder(
        DetourNavMeshQuery query, List<Triangle> collision,
        LadderDefinition ladder, LadderLinkPlacer.PlacementResult result)
    {
        if (ladder.SurfaceTriangles.Length == 0 || ladder.SurfaceVertices.Length == 0)
            return false;
        N.Vector2 normal = Unit(new(ladder.ThinAxis.X, ladder.ThinAxis.Y));
        N.Vector2 tangent = new(-normal.Y, normal.X);
        bool broadTopLanding = ladder.Name.StartsWith("climb001", StringComparison.Ordinal);
        float landingGap = broadTopLanding ? 240 : MaxFloorGap;

        foreach (N.Vector2 side in new[] { normal, -normal })
        {
            N.Vector3? bottom = FindFloor(query, ToNum(ladder.Bottom), side, tangent, landingGap);
            if (ladder.Name.StartsWith("climb001", StringComparison.Ordinal))
                Log.Normal($"DF climb001 probe side ({side.X:F2},{side.Y:F2}): bottom {bottom}");
            if (!bottom.HasValue) continue;

            // The geometric top often is a decorative climb tip 120-150 GU
            // above the actual platform. Search the exported climb vertices at
            // their real heights, highest first, for a collision-backed floor.
            foreach (O.Vector3 sourceTop in ladder.SurfaceVertices
                         .Where(v => v.Z > bottom.Value.Z + 200)
                         .OrderByDescending(v => v.Z))
            {
                N.Vector3? top = FindFloor(query, ToNum(sourceTop), side, tangent, landingGap);
                if (ladder.Name.StartsWith("climb001", StringComparison.Ordinal) && top.HasValue)
                    Log.Normal($"DF climb001 probe source Z {sourceTop.Z:F1}: top {top}");
                if (!top.HasValue || top.Value.Z <= bottom.Value.Z + 200)
                    continue;
                if (Math.Abs(top.Value.Z - sourceTop.Z) > 32 ||
                    DistanceXY(top.Value, ToNum(sourceTop)) > landingGap)
                    continue;

                List<N.Vector3> centers = [];
                int intervals = (int)Math.Ceiling((top.Value.Z - bottom.Value.Z) / StepZ);
                for (int i = 0; i <= intervals; i++)
                {
                    float z = bottom.Value.Z + (top.Value.Z - bottom.Value.Z) * i / intervals;
                    if (!TrySurfaceCenter(ladder.SurfaceTriangles, z, out N.Vector2 surface))
                    {
                        centers.Clear();
                        break;
                    }
                    centers.Add(new(surface.X + side.X * SurfaceClearance,
                                    surface.Y + side.Y * SurfaceClearance, z));
                }
                if (centers.Count == 0)
                {
                    if (ladder.Name.StartsWith("climb001", StringComparison.Ordinal))
                        Log.Normal($"DF climb001 source Z {sourceTop.Z:F1}: no continuous surface section");
                    continue;
                }

                List<N.Vector3> chain = [bottom.Value, .. centers, top.Value];
                int rejected = Enumerable.Range(1, chain.Count - 1).FirstOrDefault(i =>
                    N.Vector3.Distance(chain[i - 1], chain[i]) > (broadTopLanding ? 240 : MaxSegment) ||
                    !LineClear(chain[i - 1], chain[i], collision));
                if (rejected != 0)
                {
                    if (ladder.Name.StartsWith("climb001", StringComparison.Ordinal))
                        Log.Normal($"DF climb001 source Z {sourceTop.Z:F1}: segment {rejected} rejected " +
                                   $"{chain[rejected - 1]} -> {chain[rejected]}, " +
                                   $"length {N.Vector3.Distance(chain[rejected - 1], chain[rejected]):F1}, " +
                                   $"collision clear {LineClear(chain[rejected - 1], chain[rejected], collision)}");
                    continue;
                }

                foreach (N.Vector3 point in centers)
                    result.Pads.Add(new(ToOpen(point), ToOpen(tangent), ToOpen(side), 64, 64));
                for (int i = 1; i < chain.Count; i++)
                    result.Links.Add(new(ToOpen(chain[i - 1]), ToOpen(chain[i])));
                Log.Normal($"DF climb {ladder.Name}: {chain.Count - 1} links, floor Z {bottom.Value.Z:F1}->{top.Value.Z:F1}, " +
                           $"top source Z {sourceTop.Z:F1}, side ({side.X:F2},{side.Y:F2})");
                return true;
            }
        }
        return false;
    }

    private static N.Vector3? FindFloor(DetourNavMeshQuery query, N.Vector3 source,
                                        N.Vector2 side, N.Vector2 tangent, float maxGap)
    {
        N.Vector3? best = null;
        float bestScore = float.MaxValue;
        foreach (float along in Along)
        foreach (float away in Away)
        {
            N.Vector3 probe = new(source.X + side.X * away + tangent.X * along,
                                    source.Y + side.Y * away + tangent.Y * along, source.Z);
            N.Vector3? hit = query.FindClosestPointInBox(probe, new(16, 16, 32), probe, Walk);
            if (!hit.HasValue || Math.Abs(hit.Value.Z - source.Z) > 32)
                continue;
            float sideDistance = (hit.Value.X - source.X) * side.X + (hit.Value.Y - source.Y) * side.Y;
            float gap = DistanceXY(hit.Value, source);
            if (sideDistance < 8 || gap > maxGap)
                continue;
            float score = gap + Math.Abs(hit.Value.Z - source.Z);
            if (score < bestScore)
            {
                best = hit.Value;
                bestScore = score;
            }
        }
        return best;
    }

    private static bool TrySurfaceCenter(O.Vector3[][] triangles, float z, out N.Vector2 center)
    {
        float minZ = triangles.Min(t => t.Min(v => v.Z));
        float maxZ = triangles.Max(t => t.Max(v => v.Z));
        if (z < minZ - 32 || z > maxZ + 32)
        {
            center = default;
            return false;
        }
        z = Math.Clamp(z, minZ + 1, maxZ - 1);
        List<N.Vector2> points = [];
        foreach (O.Vector3[] tri in triangles)
        {
            for (int i = 0; i < 3; i++)
            {
                O.Vector3 a = tri[i], b = tri[(i + 1) % 3];
                if (z < Math.Min(a.Z, b.Z) - 2 || z > Math.Max(a.Z, b.Z) + 2)
                    continue;
                float span = b.Z - a.Z;
                if (Math.Abs(span) < 0.01f) continue;
                float t = Math.Clamp((z - a.Z) / span, 0, 1);
                N.Vector2 p = new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
                if (!points.Any(q => N.Vector2.DistanceSquared(q, p) < 4))
                    points.Add(p);
            }
        }
        if (points.Count == 0)
        {
            center = default;
            return false;
        }
        center = new(points.Average(p => p.X), points.Average(p => p.Y));
        return true;
    }

    private readonly record struct Triangle(N.Vector3 A, N.Vector3 B, N.Vector3 C,
                                             N.Vector3 Min, N.Vector3 Max);

    private static List<Triangle> ReadNearbyCollision(string path, IReadOnlyList<LadderDefinition> ladders)
    {
        List<N.Vector3> vertices = [default];
        List<Triangle> result = [];
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("v ", StringComparison.Ordinal))
            {
                string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 4) continue;
                vertices.Add(new(Parse(fields[1]) * 32, Parse(fields[3]) * 32, Parse(fields[2]) * 32));
            }
            else if (line.StartsWith("f ", StringComparison.Ordinal))
            {
                string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 4) continue;
                N.Vector3 a = vertices[int.Parse(fields[1].Split('/')[0], CultureInfo.InvariantCulture)];
                N.Vector3 b = vertices[int.Parse(fields[2].Split('/')[0], CultureInfo.InvariantCulture)];
                N.Vector3 c = vertices[int.Parse(fields[3].Split('/')[0], CultureInfo.InvariantCulture)];
                N.Vector3 min = N.Vector3.Min(a, N.Vector3.Min(b, c));
                N.Vector3 max = N.Vector3.Max(a, N.Vector3.Max(b, c));
                bool nearClimb = ladders.Any(l => min.X < l.CenterXY.X + 450 && max.X > l.CenterXY.X - 450 &&
                                     min.Y < l.CenterXY.Y + 450 && max.Y > l.CenterXY.Y - 450 &&
                                     min.Z < l.MaxZ + 150 && max.Z > l.MinZ - 150);
                bool nearEntrance = EntranceTreads.SelectMany(t => t).Any(p =>
                    min.X < p.X + 450 && max.X > p.X - 450 && min.Y < p.Y + 450 && max.Y > p.Y - 450 &&
                    min.Z < p.Z + 225 && max.Z > p.Z - 225);
                if (nearClimb || nearEntrance)
                    result.Add(new(a, b, c, min, max));
            }
        }
        Log.Normal($"DF climb collision check: {result.Count} nearby source triangles");
        return result;
    }

    private static bool LineClear(N.Vector3 a, N.Vector3 b, List<Triangle> triangles)
    {
        N.Vector3 d = b - a;
        N.Vector3 lo = N.Vector3.Min(a, b), hi = N.Vector3.Max(a, b);
        foreach (Triangle t in triangles)
        {
            if (t.Min.X > hi.X || t.Max.X < lo.X || t.Min.Y > hi.Y || t.Max.Y < lo.Y ||
                t.Min.Z > hi.Z || t.Max.Z < lo.Z) continue;
            N.Vector3 edge1 = t.B - t.A, edge2 = t.C - t.A;
            N.Vector3 h = N.Vector3.Cross(d, edge2);
            float det = N.Vector3.Dot(edge1, h);
            if (Math.Abs(det) < 0.001f) continue;
            float inv = 1 / det;
            N.Vector3 s = a - t.A;
            float u = inv * N.Vector3.Dot(s, h);
            if (u < 0 || u > 1) continue;
            N.Vector3 q = N.Vector3.Cross(s, edge1);
            float v = inv * N.Vector3.Dot(d, q);
            if (v < 0 || u + v > 1) continue;
            float along = inv * N.Vector3.Dot(edge2, q);
            if (along > 0.05f && along < 0.95f) return false;
        }
        return true;
    }

    private static float Parse(string value) => float.Parse(value, CultureInfo.InvariantCulture);
    private static N.Vector2 Unit(N.Vector2 v) => v.LengthSquared() < 0.001f ? N.Vector2.UnitX : N.Vector2.Normalize(v);
    private static float DistanceXY(N.Vector3 a, N.Vector3 b) => N.Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y));
    private static N.Vector3 ToNum(O.Vector3 v) => new(v.X, v.Y, v.Z);
    private static O.Vector3 ToOpen(N.Vector3 v) => new(v.X, v.Y, v.Z);
    private static O.Vector2 ToOpen(N.Vector2 v) => new(v.X, v.Y);
}
