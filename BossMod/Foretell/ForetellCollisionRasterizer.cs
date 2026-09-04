using System.Diagnostics;
using System.Numerics;

namespace BossMod.Foretell;

// Native collision memory is copied into these immutable managed values on the framework thread. Only this
// representation is allowed to cross to the raster worker; no game pointer can outlive the scene snapshot.
internal readonly record struct ForetellCollisionTriangle(Vector3 A, Vector3 B, Vector3 C);

internal sealed record ForetellCollisionSnapshot(
    Vector3 Player,
    float Radius,
    float Resolution,
    ForetellCollisionTriangle[] Triangles,
    int Colliders,
    int NativePrimitives,
    double CaptureMilliseconds);

internal sealed record ForetellCollisionRasterResult(
    ForetellTopologyGrid Grid,
    TopologyAnalysis Analysis,
    int FloorTriangles,
    int WallTriangles,
    int CandidateSamples,
    double RasterMilliseconds);

// Compact Recast-style stage for Foretell's 2D use case: project collision triangles into a heightfield, retain
// all candidate layers per cell, then flood only the layer reachable from the actor. Steep triangles become wall
// segments. This deliberately omits pathfinding and movement APIs; the output is only a bounded radar surface.
internal static class ForetellCollisionRasterizer
{
    private const int MaximumLayersPerCell = 8;
    private const float MinimumFloorNormalY = .35f;
    private const float MinimumWallHeight = .45f;
    private const float MaximumStepHeight = 1.5f;

    private readonly record struct WallSegment(Vector2 A, Vector2 B, float MinY, float MaxY);
    private readonly record struct ReachState(int Cell, int Layer);

    public static ForetellCollisionRasterResult Build(ForetellCollisionSnapshot snapshot)
    {
        var started = Stopwatch.GetTimestamp();
        var grid = new ForetellTopologyGrid();
        grid.Reset(snapshot.Player, snapshot.Radius, snapshot.Resolution);
        var layers = new List<float>?[grid.CellCount];
        var walls = new List<WallSegment>();
        var floorTriangles = 0;
        var wallTriangles = 0;
        var candidateSamples = 0;

        foreach (var triangle in snapshot.Triangles)
        {
            if (!Finite(triangle.A) || !Finite(triangle.B) || !Finite(triangle.C))
                continue;
            var normal = Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A);
            var normalLength = normal.Length();
            if (!float.IsFinite(normalLength) || normalLength < 1e-5f)
                continue;
            var normalY = Math.Abs(normal.Y) / normalLength;
            if (normalY >= MinimumFloorNormalY)
            {
                ++floorTriangles;
                RasterFloor(grid, layers, triangle, normal, ref candidateSamples);
            }
            else if (TryWallSegment(triangle, out var wall))
            {
                ++wallTriangles;
                walls.Add(wall);
            }
        }

        NormalizeLayers(layers, grid.ReferenceY);
        var wallBins = BinWalls(grid, walls);
        var selected = ReachableLayers(grid, layers, walls, wallBins, new(snapshot.Player.X, snapshot.Player.Z), snapshot.Player.Y);
        MaterializeGrid(grid, layers, walls, wallBins, selected);
        var analysis = grid.Analyze(new(snapshot.Player.X, snapshot.Player.Z), MaximumStepHeight, requireKnownEdges: true);
        return new(grid, analysis, floorTriangles, wallTriangles, candidateSamples,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static void RasterFloor(ForetellTopologyGrid grid, List<float>?[] layers, ForetellCollisionTriangle triangle,
        Vector3 normal, ref int candidateSamples)
    {
        var a = new Vector2(triangle.A.X, triangle.A.Z);
        var b = new Vector2(triangle.B.X, triangle.B.Z);
        var c = new Vector2(triangle.C.X, triangle.C.Z);
        var minX = Math.Min(a.X, Math.Min(b.X, c.X));
        var maxX = Math.Max(a.X, Math.Max(b.X, c.X));
        var minZ = Math.Min(a.Y, Math.Min(b.Y, c.Y));
        var maxZ = Math.Max(a.Y, Math.Max(b.Y, c.Y));
        var padding = grid.Resolution * .36f;
        var firstX = Math.Clamp((int)MathF.Floor((minX - padding - grid.OriginX) / grid.Resolution), 0, grid.Width - 1);
        var lastX = Math.Clamp((int)MathF.Floor((maxX + padding - grid.OriginX) / grid.Resolution), 0, grid.Width - 1);
        var firstZ = Math.Clamp((int)MathF.Floor((minZ - padding - grid.OriginZ) / grid.Resolution), 0, grid.Height - 1);
        var lastZ = Math.Clamp((int)MathF.Floor((maxZ + padding - grid.OriginZ) / grid.Resolution), 0, grid.Height - 1);
        var maximumDistanceSquared = padding * padding;

        for (var z = firstZ; z <= lastZ; ++z)
            for (var x = firstX; x <= lastX; ++x)
            {
                var index = z * grid.Width + x;
                var point = grid.CellCenter(index);
                if (DistanceSquaredToTriangle(point, a, b, c) > maximumDistanceSquared)
                    continue;
                var height = triangle.A.Y - (normal.X * (point.X - triangle.A.X) + normal.Z * (point.Y - triangle.A.Z)) / normal.Y;
                if (!float.IsFinite(height) || Math.Abs(height - grid.ReferenceY) > 48)
                    continue;
                (layers[index] ??= []).Add(height);
                ++candidateSamples;
            }
    }

    private static void NormalizeLayers(List<float>?[] layers, float referenceY)
    {
        for (var cell = 0; cell < layers.Length; ++cell)
        {
            var values = layers[cell];
            if (values == null || values.Count == 0)
                continue;
            values.Sort();
            var write = 0;
            for (var read = 0; read < values.Count; ++read)
            {
                var value = values[read];
                if (write > 0 && Math.Abs(value - values[write - 1]) < .18f)
                {
                    values[write - 1] = (values[write - 1] + value) * .5f;
                    continue;
                }
                values[write++] = value;
            }
            if (write < values.Count)
                values.RemoveRange(write, values.Count - write);
            if (values.Count > MaximumLayersPerCell)
            {
                var nearest = values.OrderBy(value => Math.Abs(value - referenceY)).Take(MaximumLayersPerCell).Order().ToArray();
                values.Clear();
                values.AddRange(nearest);
            }
        }
    }

    private static int[] ReachableLayers(ForetellTopologyGrid grid, List<float>?[] layers, List<WallSegment> walls,
        List<int>?[] wallBins, Vector2 player, float playerY)
    {
        var selected = new int[grid.CellCount];
        Array.Fill(selected, -1);
        if (!TryFindSeed(grid, layers, player, playerY, out var seed))
            return selected;

        var visited = new bool[]?[grid.CellCount];
        var queue = new Queue<ReachState>();
        Mark(seed.Cell, seed.Layer);
        while (queue.TryDequeue(out var current))
        {
            var x = current.Cell % grid.Width;
            var z = current.Cell / grid.Width;
            Visit(x - 1, z);
            Visit(x + 1, z);
            Visit(x, z - 1);
            Visit(x, z + 1);

            void Visit(int nextX, int nextZ)
            {
                if ((uint)nextX >= (uint)grid.Width || (uint)nextZ >= (uint)grid.Height)
                    return;
                var next = nextZ * grid.Width + nextX;
                var nextLayers = layers[next];
                var currentHeight = layers[current.Cell]![current.Layer];
                if (nextLayers == null)
                    return;
                var order = Enumerable.Range(0, nextLayers.Count).OrderBy(layer => Math.Abs(nextLayers[layer] - currentHeight));
                foreach (var layer in order)
                {
                    var nextHeight = nextLayers[layer];
                    if (Math.Abs(nextHeight - currentHeight) > MaximumStepHeight
                        || IsWallBlocked(grid, walls, wallBins, current.Cell, next, currentHeight, nextHeight))
                        continue;
                    Mark(next, layer);
                }
            }
        }
        return selected;

        void Mark(int cell, int layer)
        {
            var cellVisited = visited[cell] ??= new bool[layers[cell]!.Count];
            if (cellVisited[layer])
                return;
            cellVisited[layer] = true;
            if (selected[cell] < 0)
                selected[cell] = layer;
            queue.Enqueue(new(cell, layer));
        }
    }

    private static bool TryFindSeed(ForetellTopologyGrid grid, List<float>?[] layers, Vector2 player, float playerY,
        out ReachState seed)
    {
        seed = default;
        var best = float.MaxValue;
        for (var cell = 0; cell < grid.CellCount; ++cell)
        {
            var values = layers[cell];
            if (values == null)
                continue;
            var horizontal = Vector2.DistanceSquared(grid.CellCenter(cell), player);
            if (horizontal > 6.25f)
                continue;
            for (var layer = 0; layer < values.Count; ++layer)
            {
                var deltaY = values[layer] - playerY;
                if (deltaY is < -6f or > 2.25f)
                    continue;
                var score = horizontal + deltaY * deltaY * 4;
                if (score >= best)
                    continue;
                best = score;
                seed = new(cell, layer);
            }
        }
        return best < float.MaxValue;
    }

    private static void MaterializeGrid(ForetellTopologyGrid grid, List<float>?[] layers, List<WallSegment> walls,
        List<int>?[] wallBins, int[] selected)
    {
        for (var cell = 0; cell < grid.CellCount; ++cell)
            if (selected[cell] >= 0)
                grid.Set(cell, TopologyCell.Passable, layers[cell]![selected[cell]]);
            else
                grid.Set(cell, TopologyCell.Void);

        for (var z = 0; z < grid.Height; ++z)
            for (var x = 0; x < grid.Width; ++x)
            {
                var cell = z * grid.Width + x;
                if (x + 1 < grid.Width)
                    SetEdge(cell, cell + 1);
                if (z + 1 < grid.Height)
                    SetEdge(cell, cell + grid.Width);

                void SetEdge(int from, int to)
                {
                    var passable = selected[from] >= 0 && selected[to] >= 0;
                    var blocked = false;
                    if (passable)
                    {
                        var fromY = grid.Heights[from];
                        var toY = grid.Heights[to];
                        blocked = Math.Abs(fromY - toY) > MaximumStepHeight
                            || IsWallBlocked(grid, walls, wallBins, from, to, fromY, toY);
                    }
                    else if (selected[from] >= 0 || selected[to] >= 0)
                    {
                        var floorY = selected[from] >= 0 ? grid.Heights[from] : grid.Heights[to];
                        blocked = IsWallBlocked(grid, walls, wallBins, from, to, floorY, floorY);
                    }
                    grid.SetEdge(from, to, blocked);
                }
            }
        grid.Cursor = grid.CellCount;
    }

    private static List<int>?[] BinWalls(ForetellTopologyGrid grid, List<WallSegment> walls)
    {
        var bins = new List<int>?[grid.CellCount];
        for (var wallIndex = 0; wallIndex < walls.Count; ++wallIndex)
        {
            var wall = walls[wallIndex];
            var minX = Math.Clamp((int)MathF.Floor((Math.Min(wall.A.X, wall.B.X) - grid.OriginX) / grid.Resolution), 0, grid.Width - 1);
            var maxX = Math.Clamp((int)MathF.Floor((Math.Max(wall.A.X, wall.B.X) - grid.OriginX) / grid.Resolution), 0, grid.Width - 1);
            var minZ = Math.Clamp((int)MathF.Floor((Math.Min(wall.A.Y, wall.B.Y) - grid.OriginZ) / grid.Resolution), 0, grid.Height - 1);
            var maxZ = Math.Clamp((int)MathF.Floor((Math.Max(wall.A.Y, wall.B.Y) - grid.OriginZ) / grid.Resolution), 0, grid.Height - 1);
            for (var z = minZ; z <= maxZ; ++z)
                for (var x = minX; x <= maxX; ++x)
                    (bins[z * grid.Width + x] ??= []).Add(wallIndex);
        }
        return bins;
    }

    private static bool IsWallBlocked(ForetellTopologyGrid grid, List<WallSegment> walls, List<int>?[] bins,
        int from, int to, float fromY, float toY)
    {
        var a = grid.CellCenter(from);
        var b = grid.CellCenter(to);
        var minY = Math.Min(fromY, toY) + .15f;
        var maxY = Math.Max(fromY, toY) + 1.85f;
        foreach (var bin in CandidateBins(from, to))
        {
            var candidates = bins[bin];
            if (candidates == null)
                continue;
            foreach (var wallIndex in candidates)
            {
                var wall = walls[wallIndex];
                if (wall.MaxY < minY || wall.MinY > maxY)
                    continue;
                if (SegmentsIntersect(a, b, wall.A, wall.B))
                    return true;
            }
        }
        return false;

        IEnumerable<int> CandidateBins(int first, int second)
        {
            yield return first;
            if (second != first)
                yield return second;
        }
    }

    private static bool TryWallSegment(ForetellCollisionTriangle triangle, out WallSegment wall)
    {
        var points = new[]
        {
            new Vector2(triangle.A.X, triangle.A.Z),
            new Vector2(triangle.B.X, triangle.B.Z),
            new Vector2(triangle.C.X, triangle.C.Z)
        };
        var first = 0;
        var second = 1;
        var longest = Vector2.DistanceSquared(points[0], points[1]);
        Compare(0, 2);
        Compare(1, 2);
        var minY = Math.Min(triangle.A.Y, Math.Min(triangle.B.Y, triangle.C.Y));
        var maxY = Math.Max(triangle.A.Y, Math.Max(triangle.B.Y, triangle.C.Y));
        if (longest < .01f || maxY - minY < MinimumWallHeight)
        {
            wall = default;
            return false;
        }
        wall = new(points[first], points[second], minY, maxY);
        return true;

        void Compare(int a, int b)
        {
            var length = Vector2.DistanceSquared(points[a], points[b]);
            if (length <= longest)
                return;
            first = a;
            second = b;
            longest = length;
        }
    }

    private static float DistanceSquaredToTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        var ab = b - a;
        var bc = c - b;
        var ca = a - c;
        var ap = p - a;
        var bp = p - b;
        var cp = p - c;
        var c1 = Cross(ab, ap);
        var c2 = Cross(bc, bp);
        var c3 = Cross(ca, cp);
        if (c1 >= 0 && c2 >= 0 && c3 >= 0 || c1 <= 0 && c2 <= 0 && c3 <= 0)
            return 0;
        return Math.Min(DistanceSquaredToSegment(p, a, b),
            Math.Min(DistanceSquaredToSegment(p, b, c), DistanceSquaredToSegment(p, c, a)));
    }

    private static float DistanceSquaredToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var delta = b - a;
        var lengthSquared = delta.LengthSquared();
        if (lengthSquared < 1e-8f)
            return Vector2.DistanceSquared(p, a);
        var t = Math.Clamp(Vector2.Dot(p - a, delta) / lengthSquared, 0, 1);
        return Vector2.DistanceSquared(p, a + delta * t);
    }

    private static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        const float epsilon = 1e-4f;
        var abC = Cross(b - a, c - a);
        var abD = Cross(b - a, d - a);
        var cdA = Cross(d - c, a - c);
        var cdB = Cross(d - c, b - c);
        if ((abC > epsilon && abD > epsilon) || (abC < -epsilon && abD < -epsilon)
            || (cdA > epsilon && cdB > epsilon) || (cdA < -epsilon && cdB < -epsilon))
            return false;
        return Math.Max(Math.Min(a.X, b.X), Math.Min(c.X, d.X)) <= Math.Min(Math.Max(a.X, b.X), Math.Max(c.X, d.X)) + epsilon
            && Math.Max(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)) <= Math.Min(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)) + epsilon;
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
