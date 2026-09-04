using System.Diagnostics;
using System.IO;
using System.Numerics;

namespace BossMod.Foretell;

// Native collision memory is copied into these immutable managed values on the framework thread. Only this
// representation is allowed to cross to the raster worker; no game pointer can outlive the scene snapshot.
internal readonly record struct ForetellCollisionTriangle(Vector3 A, Vector3 B, Vector3 C, ulong Material = 0x4000);

// Match the native movement-ray layer/material contract, including per-object overrides of PCB materials.
internal static class ForetellCollisionRules
{
    public static ulong EffectiveMaterial(ulong primitive, ulong value, ulong mask)
        => mask == 0 ? primitive : (primitive & ~mask) | value;

    public static bool Participates(ulong layer, byte visibility) => (layer & 1) != 0 && (visibility & 1) != 0;
    public static bool BlocksMovement(ulong material) => (material & 0x4000) != 0;
    public static bool SupportsFloor(ulong material)
        => BlocksMovement(material) && (material & 0x2000000) == 0 && (material & 0x1F) != 0x11;
}

internal sealed record ForetellCollisionSnapshot(
    Vector3 Player,
    Vector2 Center,
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
    double RasterMilliseconds,
    float SampleRadius);

// Compact Recast-style stage for Foretell's 2D use case: project collision triangles into a heightfield, retain
// all candidate layers per cell, then flood only the layer reachable from the actor. Steep triangles become wall
// segments. This deliberately omits pathfinding and movement APIs; the output is only a bounded radar surface.
internal static class ForetellCollisionRasterizer
{
    private const int MaximumLayersPerCell = 8;
    private const float MinimumFloorNormalY = .55f;
    private const float MinimumWallHeight = .45f;
    private const float MaximumStepHeight = 1.5f;
    private const float MinimumHeadroom = 1.8f;
    private const int MaximumCandidateSamples = 8_000_000;
    private const int MaximumWallReferences = 8_000_000;

    private readonly record struct WallSegment(ForetellCollisionTriangle Triangle, Vector2 Min, Vector2 Max, float MinY, float MaxY);
    private readonly record struct ReachState(int Cell, int Layer);

    public static ForetellCollisionRasterResult Build(ForetellCollisionSnapshot snapshot)
    {
        var started = Stopwatch.GetTimestamp();
        var grid = new ForetellTopologyGrid();
        grid.Reset(new(snapshot.Center.X, MathF.Round(snapshot.Player.Y / 8) * 8, snapshot.Center.Y), snapshot.Radius, snapshot.Resolution);
        var layers = new List<float>?[grid.CellCount];
        var surfaces = new List<float>?[grid.CellCount];
        var walls = new List<WallSegment>();
        var floorTriangles = 0;
        var wallTriangles = 0;
        var candidateSamples = 0;

        foreach (var triangle in snapshot.Triangles)
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 2)
                throw new InvalidDataException("Collision raster exceeded its worker time budget");
            if (!ForetellCollisionRules.BlocksMovement(triangle.Material)
                || !Finite(triangle.A) || !Finite(triangle.B) || !Finite(triangle.C))
                continue;
            var normal = Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A);
            var normalLength = normal.Length();
            if (!float.IsFinite(normalLength) || normalLength < 1e-5f)
                continue;
            var normalY = Math.Abs(normal.Y) / normalLength;
            if (normalY >= MinimumFloorNormalY)
            {
                // Downward-facing undersides are ceilings, not a second walkable floor under a bridge.
                RasterFloor(grid, surfaces, triangle, normal, ref candidateSamples);
                if (normal.Y > 0 && ForetellCollisionRules.SupportsFloor(triangle.Material))
                {
                    ++floorTriangles;
                    RasterFloor(grid, layers, triangle, normal, ref candidateSamples);
                }
            }
            else if (TryWallSegment(triangle, out var wall))
            {
                ++wallTriangles;
                walls.Add(wall);
            }
        }

        NormalizeLayers(layers, surfaces, grid.ReferenceY);
        var wallBins = BinWalls(grid, walls);
        var selected = ReachableLayers(grid, layers, walls, wallBins, new(snapshot.Player.X, snapshot.Player.Z), snapshot.Player.Y);
        MaterializeGrid(grid, layers, walls, wallBins, selected);
        var analysis = grid.Analyze(new(snapshot.Player.X, snapshot.Player.Z), MaximumStepHeight, requireKnownEdges: true);
        return new(grid, analysis, floorTriangles, wallTriangles, candidateSamples,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, snapshot.Radius);
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
                if (++candidateSamples > MaximumCandidateSamples)
                    throw new InvalidDataException("Collision heightfield exceeded its sample budget");
            }
    }

    private static void NormalizeLayers(List<float>?[] layers, List<float>?[] surfaces, float referenceY)
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
            if (surfaces[cell] is { } overhead)
                values.RemoveAll(floor => overhead.Any(height => height > floor + .18f && height < floor + MinimumHeadroom));
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
                if (nextLayers == null || selected[next] >= 0)
                    return;
                var order = Enumerable.Range(0, nextLayers.Count).OrderBy(layer => Math.Abs(nextLayers[layer] - currentHeight));
                foreach (var layer in order)
                {
                    var nextHeight = nextLayers[layer];
                    if (Math.Abs(nextHeight - currentHeight) > MaximumStepHeight
                        || IsWallBlocked(grid, walls, wallBins, current.Cell, next, currentHeight, nextHeight))
                        continue;
                    Mark(next, layer);
                    break;
                }
            }
        }
        return selected;

        void Mark(int cell, int layer)
        {
            if (selected[cell] >= 0)
                return;
            // Commit the reached sheet once. Flooding every layer then projecting the first one independently
            // per cell creates edges between incompatible heights and fragments the supposedly connected map.
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
        var references = 0;
        for (var wallIndex = 0; wallIndex < walls.Count; ++wallIndex)
        {
            var wall = walls[wallIndex];
            var minX = Math.Clamp((int)MathF.Floor((wall.Min.X - grid.OriginX) / grid.Resolution), 0, grid.Width - 1);
            var maxX = Math.Clamp((int)MathF.Floor((wall.Max.X - grid.OriginX) / grid.Resolution), 0, grid.Width - 1);
            var minZ = Math.Clamp((int)MathF.Floor((wall.Min.Y - grid.OriginZ) / grid.Resolution), 0, grid.Height - 1);
            var maxZ = Math.Clamp((int)MathF.Floor((wall.Max.Y - grid.OriginZ) / grid.Resolution), 0, grid.Height - 1);
            for (var z = minZ; z <= maxZ; ++z)
                for (var x = minX; x <= maxX; ++x)
                {
                    if (++references > MaximumWallReferences)
                        throw new InvalidDataException("Collision walls exceeded their spatial index budget");
                    (bins[z * grid.Width + x] ??= []).Add(wallIndex);
                }
        }
        return bins;
    }

    private static bool IsWallBlocked(ForetellTopologyGrid grid, List<WallSegment> walls, List<int>?[] bins,
        int from, int to, float fromY, float toY)
    {
        var a = grid.CellCenter(from);
        var b = grid.CellCenter(to);
        // The riser below the higher floor is a valid step, not a wall across the staircase.
        var minY = Math.Max(fromY, toY) + .15f;
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
                if (TriangleBlocksAtHeight(wall.Triangle, a, b, minY, maxY))
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
        var a = new Vector2(triangle.A.X, triangle.A.Z);
        var b = new Vector2(triangle.B.X, triangle.B.Z);
        var c = new Vector2(triangle.C.X, triangle.C.Z);
        var min = Vector2.Min(a, Vector2.Min(b, c));
        var max = Vector2.Max(a, Vector2.Max(b, c));
        var minY = Math.Min(triangle.A.Y, Math.Min(triangle.B.Y, triangle.C.Y));
        var maxY = Math.Max(triangle.A.Y, Math.Max(triangle.B.Y, triangle.C.Y));
        if (Vector2.DistanceSquared(min, max) < .01f || maxY - minY < MinimumWallHeight)
        {
            wall = default;
            return false;
        }
        wall = new(triangle, min, max, minY, maxY);
        return true;
    }

    // Clip the actual triangle to the actor's height band before projecting it. An arch's low corner must not
    // turn its entire high diagonal into a floor-to-ceiling curtain across the opening.
    private static bool TriangleBlocksAtHeight(ForetellCollisionTriangle triangle, Vector2 a, Vector2 b, float minY, float maxY)
    {
        Span<Vector3> input = stackalloc Vector3[8];
        Span<Vector3> output = stackalloc Vector3[8];
        input[0] = triangle.A; input[1] = triangle.B; input[2] = triangle.C;
        var count = ClipHeight(input[..3], output, minY, above: true);
        count = ClipHeight(output[..count], input, maxY, above: false);
        if (count < 2) return false;
        for (var i = 0; i < count; ++i)
        {
            var p = new Vector2(input[i].X, input[i].Z);
            var q = new Vector2(input[(i + 1) % count].X, input[(i + 1) % count].Z);
            if (SegmentsIntersect(a, b, p, q)) return true;
        }
        // Also handle a traversal wholly contained in the projected face of a sloping wall.
        for (var i = 1; i + 1 < count; ++i)
        {
            var p = new Vector2(input[0].X, input[0].Z);
            var q = new Vector2(input[i].X, input[i].Z);
            var r = new Vector2(input[i + 1].X, input[i + 1].Z);
            if (Math.Abs(Cross(q - p, r - p)) > 1e-6f
                && (DistanceSquaredToTriangle(a, p, q, r) < 1e-8f || DistanceSquaredToTriangle(b, p, q, r) < 1e-8f))
                return true;
        }
        return false;
    }

    private static int ClipHeight(ReadOnlySpan<Vector3> input, Span<Vector3> output, float height, bool above)
    {
        if (input.Length == 0) return 0;
        var count = 0;
        var previous = input[^1];
        var previousInside = above ? previous.Y >= height : previous.Y <= height;
        foreach (var current in input)
        {
            var inside = above ? current.Y >= height : current.Y <= height;
            if (inside != previousInside)
                output[count++] = Vector3.Lerp(previous, current, (height - previous.Y) / (current.Y - previous.Y));
            if (inside) output[count++] = current;
            previous = current;
            previousInside = inside;
        }
        return count;
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
