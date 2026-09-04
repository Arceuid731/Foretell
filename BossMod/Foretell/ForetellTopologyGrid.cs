using System.Numerics;

namespace BossMod.Foretell;

internal enum TopologyCell : byte { Unknown, Passable, Blocked, Void }

internal enum TopologyProbeKind : byte { Floor, Edge }

internal readonly record struct TopologyProbe(TopologyProbeKind Kind, int From, int To);

internal static class ForetellTopologyProbeRules
{
    public static float FloorReferenceY(float parentHeight, float playerY)
        => float.IsFinite(parentHeight) ? parentHeight : playerY;

    public static bool IsFloorHit(float normalY, float hitY, float referenceY)
    {
        var delta = hitY - referenceY;
        return float.IsFinite(normalY) && float.IsFinite(delta) && normalY >= .35f && delta is >= -6f and <= 2.25f;
    }
}

[Flags]
internal enum TopologyEdge : byte
{
    None = 0,
    North = 1 << 0,
    East = 1 << 1,
    South = 1 << 2,
    West = 1 << 3
}

// Incremental collision-survey scheduler. Unlike a raster sweep of the complete enclosing disc, this grows from
// the player's floor component and stops at the first observed void, excessive step or blocking wall. This makes
// a closed room proportional to its reachable surface while keeping open-world work local and strictly bounded.
// The class owns no native pointers and is deterministic, so its latency/work invariants are covered by core tests.
internal sealed class ForetellTopologyFrontier
{
    private readonly PriorityQueue<TopologyProbe, long> _pending = new();
    private readonly HashSet<long> _queuedEdges = [];
    private bool[] _floorQueued = [];
    private bool[] _sampled = [];
    private bool[] _reachable = [];
    private bool[] _expanded = [];
    private Vector2 _seed;
    private Vector2 _center;
    private float _radius;
    private int _root = -1;

    public int Pending => _pending.Count;
    public int Sampled { get; private set; }
    public int Reachable { get; private set; }
    public bool Complete => _pending.Count == 0;

    public void Clear()
    {
        _pending.Clear();
        _queuedEdges.Clear();
        _floorQueued = _sampled = _reachable = _expanded = [];
        _seed = _center = default;
        _radius = 0;
        _root = -1;
        Sampled = Reachable = 0;
    }

    public void Start(ForetellTopologyGrid grid, Vector2 seed, Vector2 center, float radius)
    {
        Clear();
        _seed = seed;
        _center = center;
        _radius = radius;
        _floorQueued = new bool[grid.CellCount];
        _sampled = new bool[grid.CellCount];
        _reachable = new bool[grid.CellCount];
        _expanded = new bool[grid.CellCount];

        var seedX = Math.Clamp((int)((seed.X - grid.OriginX) / grid.Resolution), 0, grid.Width - 1);
        var seedZ = Math.Clamp((int)((seed.Y - grid.OriginZ) / grid.Resolution), 0, grid.Height - 1);
        // Cell centres can straddle a narrow path even while the actor itself is on valid floor. A tiny local
        // seed patch finds the nearest floor without allowing the survey to jump across distant geometry.
        for (var z = Math.Max(0, seedZ - 1); z <= Math.Min(grid.Height - 1, seedZ + 1); ++z)
            for (var x = Math.Max(0, seedX - 1); x <= Math.Min(grid.Width - 1, seedX + 1); ++x)
                QueueFloor(grid, z * grid.Width + x, -1);
    }

    public bool TryDequeue(ForetellTopologyGrid grid, out TopologyProbe probe)
    {
        // A cell can become reachable through one of several queued edges. Retire only a bounded number of the
        // now-stale alternatives per call so queue cleanup itself can never create a main-thread latency spike.
        var retired = 0;
        while (retired++ < 32 && _pending.TryDequeue(out probe, out _))
        {
            if (probe.Kind == TopologyProbeKind.Floor)
            {
                _floorQueued[probe.To] = false;
                if (!_sampled[probe.To])
                    return true;
            }
            else
            {
                _queuedEdges.Remove(EdgeKey(probe.From, probe.To));
                if (_reachable[probe.From] && !_reachable[probe.To]
                    && _sampled[probe.From] && _sampled[probe.To]
                    && grid.Cells[probe.From] == (byte)TopologyCell.Passable
                    && grid.Cells[probe.To] == (byte)TopologyCell.Passable
                    && !grid.IsEdgeKnown(probe.From, probe.To))
                    return true;
            }
        }
        probe = default;
        return false;
    }

    public void CommitFloor(ForetellTopologyGrid grid, int index)
    {
        if (_sampled[index]) return;
        _sampled[index] = true;
        ++Sampled;
        if (grid.Cells[index] != (byte)TopologyCell.Passable)
            return;

        if (_root < 0)
        {
            _root = index;
            MarkReachable(grid, index);
            return;
        }

        VisitNeighbors(grid, index, neighbor =>
        {
            if (_reachable[neighbor]) QueueEdge(grid, neighbor, index);
        });
    }

    public void CommitEdge(ForetellTopologyGrid grid, int from, int to, bool blocked)
    {
        grid.SetEdge(from, to, blocked);
        if (blocked) return;
        if (_reachable[from] && !_reachable[to]) MarkReachable(grid, to);
        else if (_reachable[to] && !_reachable[from]) MarkReachable(grid, from);
    }

    public bool WasSampled(int index) => (uint)index < (uint)_sampled.Length && _sampled[index];
    public bool IsReachable(int index) => (uint)index < (uint)_reachable.Length && _reachable[index];

    private void MarkReachable(ForetellTopologyGrid grid, int index)
    {
        if (_reachable[index]) return;
        _reachable[index] = true;
        ++Reachable;
        Expand(grid, index);
    }

    private void Expand(ForetellTopologyGrid grid, int index)
    {
        if (_expanded[index]) return;
        _expanded[index] = true;
        VisitNeighbors(grid, index, neighbor =>
        {
            if (!_sampled[neighbor]) QueueFloor(grid, neighbor, index);
            else if (!_reachable[neighbor] && grid.Cells[neighbor] == (byte)TopologyCell.Passable)
                QueueEdge(grid, index, neighbor);
        });
    }

    private void QueueFloor(ForetellTopologyGrid grid, int index, int from)
    {
        if (_sampled[index] || _floorQueued[index] || !InsideSurvey(grid, index)) return;
        _floorQueued[index] = true;
        _pending.Enqueue(new(TopologyProbeKind.Floor, from, index), Priority(grid, index, 0));
    }

    private void QueueEdge(ForetellTopologyGrid grid, int from, int to)
    {
        if (_reachable[to] || grid.IsEdgeKnown(from, to)) return;
        var key = EdgeKey(from, to);
        if (!_queuedEdges.Add(key)) return;
        _pending.Enqueue(new(TopologyProbeKind.Edge, from, to), Priority(grid, to, 1));
    }

    private void VisitNeighbors(ForetellTopologyGrid grid, int index, Action<int> visitor)
    {
        var x = index % grid.Width;
        var z = index / grid.Width;
        if (x > 0) visitor(index - 1);
        if (x + 1 < grid.Width) visitor(index + 1);
        if (z > 0) visitor(index - grid.Width);
        if (z + 1 < grid.Height) visitor(index + grid.Width);
    }

    private bool InsideSurvey(ForetellTopologyGrid grid, int index)
    {
        var allowance = _radius + grid.Resolution * .35f;
        return Vector2.DistanceSquared(grid.CellCenter(index), _center) <= allowance * allowance;
    }

    private long Priority(ForetellTopologyGrid grid, int index, int kind)
    {
        var delta = grid.CellCenter(index) - _seed;
        // Integer quantization gives deterministic near-first rings while reserving the low bit for floor-before-edge.
        return (long)MathF.Round(delta.LengthSquared() * 1024) * 2 + kind;
    }

    private static long EdgeKey(int a, int b)
    {
        var lower = Math.Min(a, b);
        var upper = Math.Max(a, b);
        return ((long)lower << 32) | (uint)upper;
    }
}

internal sealed record TopologyAnalysis(
    string Fingerprint,
    byte[] ConnectedCells,
    byte[] SampledCells,
    short[] HeightCentimeters,
    byte[] KnownEdges,
    byte[] BlockedEdges,
    List<List<Vector2>> Contours,
    int PassableCells,
    int BlockedCells,
    int UnknownCells,
    int Components);

// Pure managed topology core. Native collision probing is isolated in ForetellTopology.cs; this class is deterministic
// and can be exercised by the standalone core test harness without loading Dalamud or FFXIVClientStructs.
internal sealed class ForetellTopologyGrid
{
    public float OriginX { get; private set; }
    public float OriginZ { get; private set; }
    public float ReferenceY { get; private set; }
    public float Resolution { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Cursor { get; set; }
    public int CellCount => Width * Height;
    public byte[] Cells { get; private set; } = [];
    public float[] Heights { get; private set; } = [];
    public byte[] KnownEdges { get; private set; } = [];
    public byte[] BlockedEdges { get; private set; } = [];

    public void Reset(Vector3 center, float radius, float resolution)
    {
        Resolution = Math.Clamp(resolution, .5f, 4f);
        var halfCells = Math.Max(4, (int)MathF.Ceiling(radius / Resolution));
        Width = Height = halfCells * 2 + 1;
        // Origin is the lower cell boundary. Offset by half a cell so the middle cell centre is exactly the
        // requested world-aligned window centre; this prevents every refresh from drifting by half a sample.
        OriginX = MathF.Round(center.X / Resolution) * Resolution - (halfCells + .5f) * Resolution;
        OriginZ = MathF.Round(center.Z / Resolution) * Resolution - (halfCells + .5f) * Resolution;
        ReferenceY = center.Y;
        Cells = new byte[CellCount];
        Heights = new float[CellCount];
        KnownEdges = new byte[CellCount];
        BlockedEdges = new byte[CellCount];
        Array.Fill(Heights, float.NaN);
        Cursor = 0;
    }

    public void Clear()
    {
        OriginX = OriginZ = ReferenceY = Resolution = 0;
        Width = Height = Cursor = 0;
        Cells = [];
        Heights = [];
        KnownEdges = [];
        BlockedEdges = [];
    }

    public ForetellTopologyGrid Snapshot()
        => new()
        {
            OriginX = OriginX,
            OriginZ = OriginZ,
            ReferenceY = ReferenceY,
            Resolution = Resolution,
            Width = Width,
            Height = Height,
            Cursor = Cursor,
            Cells = Cells.ToArray(),
            Heights = Heights.ToArray(),
            KnownEdges = KnownEdges.ToArray(),
            BlockedEdges = BlockedEdges.ToArray()
        };

    public void ReplaceWith(ForetellTopologyGrid source)
    {
        OriginX = source.OriginX;
        OriginZ = source.OriginZ;
        ReferenceY = source.ReferenceY;
        Resolution = source.Resolution;
        Width = source.Width;
        Height = source.Height;
        Cursor = source.Cursor;
        Cells = source.Cells.ToArray();
        Heights = source.Heights.ToArray();
        KnownEdges = source.KnownEdges.ToArray();
        BlockedEdges = source.BlockedEdges.ToArray();
    }

    public bool Restore(float originX, float originZ, float referenceY, float resolution, int width, int height,
        byte[] connectedCells, short[] heightCentimeters, byte[] knownEdges, byte[] blockedEdges)
    {
        var count = (long)width * height;
        if (!float.IsFinite(originX) || !float.IsFinite(originZ) || !float.IsFinite(referenceY)
            || !float.IsFinite(resolution) || resolution is < .5f or > 4f || width <= 0 || height <= 0
            || count > 1_000_000 || connectedCells.Length != count || heightCentimeters.Length != count
            || knownEdges.Length != count || blockedEdges.Length != count)
            return false;
        for (var i = 0; i < connectedCells.Length; ++i)
            if ((knownEdges[i] & 0xF0) != 0 || (blockedEdges[i] & ~knownEdges[i]) != 0)
                return false;
        OriginX = originX;
        OriginZ = originZ;
        ReferenceY = referenceY;
        Resolution = resolution;
        Width = width;
        Height = height;
        Cells = connectedCells.ToArray();
        Heights = heightCentimeters.Select(value => value == short.MinValue ? float.NaN : referenceY + value / 100f).ToArray();
        KnownEdges = knownEdges.ToArray();
        BlockedEdges = blockedEdges.ToArray();
        Cursor = CellCount;
        if (EdgeMasksAreSymmetric()) return true;
        Clear();
        return false;
    }

    public Vector2 CellCenter(int index)
    {
        var x = index % Width;
        var z = index / Width;
        return new(OriginX + (x + .5f) * Resolution, OriginZ + (z + .5f) * Resolution);
    }

    public void Set(int index, TopologyCell cell, float height = float.NaN)
    {
        if ((uint)index >= (uint)CellCount) return;
        Cells[index] = (byte)cell;
        Heights[index] = height;
    }

    public void ClearEdges()
    {
        Array.Clear(KnownEdges);
        Array.Clear(BlockedEdges);
    }

    public bool IsEdgeKnown(int from, int to)
        => TryEdgeBits(from, to, out var fromBit, out _) && (KnownEdges[from] & (byte)fromBit) != 0;

    public bool IsEdgeBlocked(int from, int to)
        => TryEdgeBits(from, to, out var fromBit, out _) && (BlockedEdges[from] & (byte)fromBit) != 0;

    public void SetEdge(int from, int to, bool blocked)
    {
        if (!TryEdgeBits(from, to, out var fromBit, out var toBit))
            return;
        KnownEdges[from] |= (byte)fromBit;
        KnownEdges[to] |= (byte)toBit;
        if (blocked)
        {
            BlockedEdges[from] |= (byte)fromBit;
            BlockedEdges[to] |= (byte)toBit;
        }
        else
        {
            BlockedEdges[from] &= (byte)~(byte)fromBit;
            BlockedEdges[to] &= (byte)~(byte)toBit;
        }
    }

    public bool Contains(Vector2 world)
        => world.X >= OriginX && world.Y >= OriginZ && world.X < OriginX + Width * Resolution && world.Y < OriginZ + Height * Resolution;

    public bool? IsConnectedPassable(Vector2 world, byte[]? connected)
    {
        if (!Contains(world) || connected == null || connected.Length != CellCount) return null;
        var x = Math.Clamp((int)((world.X - OriginX) / Resolution), 0, Width - 1);
        var z = Math.Clamp((int)((world.Y - OriginZ) / Resolution), 0, Height - 1);
        var value = connected[z * Width + x];
        return value == (byte)TopologyCell.Unknown ? null : value == (byte)TopologyCell.Passable;
    }

    public bool TryConnectedHeight(Vector2 world, byte[]? connected, out float height)
    {
        height = 0;
        if (!Contains(world) || connected == null || connected.Length != CellCount)
            return false;
        var x = Math.Clamp((int)((world.X - OriginX) / Resolution), 0, Width - 1);
        var z = Math.Clamp((int)((world.Y - OriginZ) / Resolution), 0, Height - 1);
        var index = z * Width + x;
        if (connected[index] != (byte)TopologyCell.Passable || !float.IsFinite(Heights[index]))
            return false;
        height = Heights[index];
        return true;
    }

    public TopologyAnalysis Analyze(Vector2 seedWorld, float maxStepHeight = 1.75f, bool requireKnownEdges = false)
    {
        var connected = new byte[CellCount];
        for (var i = 0; i < CellCount; ++i)
            connected[i] = Cells[i] == (byte)TopologyCell.Unknown ? (byte)TopologyCell.Unknown : (byte)TopologyCell.Blocked;

        var seedX = Math.Clamp((int)((seedWorld.X - OriginX) / Resolution), 0, Width - 1);
        var seedZ = Math.Clamp((int)((seedWorld.Y - OriginZ) / Resolution), 0, Height - 1);
        var seed = FindNearestPassable(seedX, seedZ);
        if (seed >= 0)
            Flood(seed, connected, maxStepHeight, requireKnownEdges);

        var components = CountRawComponents(maxStepHeight, requireKnownEdges);
        var passable = 0;
        var blocked = 0;
        var unknown = 0;
        var packedHeights = new short[CellCount];
        for (var i = 0; i < CellCount; ++i)
        {
            if (connected[i] == (byte)TopologyCell.Passable) ++passable;
            else if (connected[i] == (byte)TopologyCell.Unknown) ++unknown;
            else ++blocked;
            packedHeights[i] = float.IsFinite(Heights[i])
                ? (short)Math.Clamp(MathF.Round((Heights[i] - ReferenceY) * 100), short.MinValue + 1, short.MaxValue)
                : short.MinValue;
        }

        var contours = BuildContours(connected);
        var fingerprint = Fingerprint(connected, packedHeights, KnownEdges, BlockedEdges);
        return new(fingerprint, connected, Cells.ToArray(), packedHeights, KnownEdges.ToArray(), BlockedEdges.ToArray(), contours, passable, blocked, unknown, components);
    }

    private int FindNearestPassable(int sx, int sz)
    {
        var max = Math.Max(Width, Height);
        for (var r = 0; r < max; ++r)
            for (var z = Math.Max(0, sz - r); z <= Math.Min(Height - 1, sz + r); ++z)
                for (var x = Math.Max(0, sx - r); x <= Math.Min(Width - 1, sx + r); ++x)
                    if ((x == sx - r || x == sx + r || z == sz - r || z == sz + r) && Cells[z * Width + x] == (byte)TopologyCell.Passable)
                        return z * Width + x;
        return -1;
    }

    private void Flood(int seed, byte[] connected, float maxStep, bool requireKnownEdges)
    {
        var queue = new Queue<int>();
        connected[seed] = (byte)TopologyCell.Passable;
        queue.Enqueue(seed);
        while (queue.TryDequeue(out var current))
        {
            var x = current % Width;
            var z = current / Width;
            Visit(x - 1, z, current);
            Visit(x + 1, z, current);
            Visit(x, z - 1, current);
            Visit(x, z + 1, current);
        }

        void Visit(int x, int z, int from)
        {
            if ((uint)x >= (uint)Width || (uint)z >= (uint)Height) return;
            var next = z * Width + x;
            if (connected[next] == (byte)TopologyCell.Passable || Cells[next] != (byte)TopologyCell.Passable) return;
            if (!float.IsFinite(Heights[from]) || !float.IsFinite(Heights[next]) || Math.Abs(Heights[from] - Heights[next]) > maxStep) return;
            if (!CanTraverse(from, next, requireKnownEdges)) return;
            connected[next] = (byte)TopologyCell.Passable;
            queue.Enqueue(next);
        }
    }

    private int CountRawComponents(float maxStep, bool requireKnownEdges)
    {
        var visited = new bool[CellCount];
        var queue = new Queue<int>();
        var count = 0;
        for (var start = 0; start < CellCount; ++start)
        {
            if (visited[start] || Cells[start] != (byte)TopologyCell.Passable) continue;
            ++count;
            visited[start] = true;
            queue.Enqueue(start);
            while (queue.TryDequeue(out var cur))
            {
                var x = cur % Width;
                var z = cur / Width;
                Visit(x - 1, z, cur); Visit(x + 1, z, cur); Visit(x, z - 1, cur); Visit(x, z + 1, cur);
            }
        }
        return count;

        void Visit(int x, int z, int from)
        {
            if ((uint)x >= (uint)Width || (uint)z >= (uint)Height) return;
            var next = z * Width + x;
            if (visited[next] || Cells[next] != (byte)TopologyCell.Passable) return;
            if (Math.Abs(Heights[from] - Heights[next]) > maxStep) return;
            if (!CanTraverse(from, next, requireKnownEdges)) return;
            visited[next] = true;
            queue.Enqueue(next);
        }
    }

    private List<List<Vector2>> BuildContours(byte[] connected)
    {
        var next = new Dictionary<(int X, int Z), List<(int X, int Z)>>();
        bool Pass(int x, int z) => (uint)x < (uint)Width && (uint)z < (uint)Height && connected[z * Width + x] == (byte)TopologyCell.Passable;
        void Edge((int X, int Z) a, (int X, int Z) b)
        {
            if (!next.TryGetValue(a, out var list)) next[a] = list = [];
            list.Add(b);
        }

        for (var z = 0; z < Height; ++z)
            for (var x = 0; x < Width; ++x)
            {
                if (!Pass(x, z)) continue;
                if (!Pass(x, z - 1)) Edge((x, z), (x + 1, z));
                if (!Pass(x + 1, z)) Edge((x + 1, z), (x + 1, z + 1));
                if (!Pass(x, z + 1)) Edge((x + 1, z + 1), (x, z + 1));
                if (!Pass(x - 1, z)) Edge((x, z + 1), (x, z));
            }

        var loops = new List<List<Vector2>>();
        while (next.Count > 0)
        {
            var start = next.First().Key;
            var current = start;
            var loop = new List<Vector2>();
            var guard = 0;
            do
            {
                loop.Add(ToWorld(current));
                if (!next.TryGetValue(current, out var candidates) || candidates.Count == 0) break;
                var index = candidates.Count - 1;
                current = candidates[index];
                candidates.RemoveAt(index);
                if (candidates.Count == 0) next.Remove(loop.Count == 1 ? start : FromWorld(loop[^1]));
            } while (current != start && ++guard <= CellCount * 4);
            if (current == start && loop.Count >= 4)
                loops.Add(SimplifyClosed(loop, Resolution * .80f));
        }
        return loops;

        Vector2 ToWorld((int X, int Z) p) => new(OriginX + p.X * Resolution, OriginZ + p.Z * Resolution);
        (int X, int Z) FromWorld(Vector2 p) => ((int)MathF.Round((p.X - OriginX) / Resolution), (int)MathF.Round((p.Y - OriginZ) / Resolution));
    }

    private static List<Vector2> SimplifyClosed(List<Vector2> source, float tolerance)
    {
        if (source.Count < 5) return source;
        var corners = new List<Vector2>(source.Count);
        for (var i = 0; i < source.Count; ++i)
        {
            var a = source[(i + source.Count - 1) % source.Count];
            var b = source[i];
            var c = source[(i + 1) % source.Count];
            var ab = b - a;
            var bc = c - b;
            if (Math.Abs(ab.X * bc.Y - ab.Y * bc.X) > .0001f)
                corners.Add(b);
        }
        if (corners.Count < 5)
            return corners.Count >= 3 ? corners : source;

        // Split the ring across a long chord and run Ramer-Douglas-Peucker on both sides. This removes the visible
        // one-cell staircase produced by rasterization while retaining real corridor corners and narrow obstacles.
        var split = 1;
        var farthest = 0f;
        for (var i = 1; i < corners.Count; ++i)
        {
            var distance = Vector2.DistanceSquared(corners[0], corners[i]);
            if (distance <= farthest) continue;
            farthest = distance;
            split = i;
        }
        if (split <= 1 || split >= corners.Count - 1)
            return corners;

        var first = SimplifyOpen(corners.GetRange(0, split + 1), tolerance);
        var secondSource = corners.GetRange(split, corners.Count - split);
        secondSource.Add(corners[0]);
        var second = SimplifyOpen(secondSource, tolerance);
        var result = new List<Vector2>(first.Count + second.Count - 2);
        result.AddRange(first.Take(first.Count - 1));
        result.AddRange(second.Take(second.Count - 1));
        return result.Count >= 3 ? result : corners;
    }

    private static List<Vector2> SimplifyOpen(List<Vector2> source, float tolerance)
    {
        if (source.Count <= 2)
            return source;
        var keep = new bool[source.Count];
        keep[0] = keep[^1] = true;
        var pending = new Stack<(int First, int Last)>();
        pending.Push((0, source.Count - 1));
        var toleranceSquared = tolerance * tolerance;
        while (pending.TryPop(out var range))
        {
            var farthest = toleranceSquared;
            var selected = -1;
            for (var i = range.First + 1; i < range.Last; ++i)
            {
                var distance = DistanceSquaredToSegment(source[i], source[range.First], source[range.Last]);
                if (distance <= farthest) continue;
                farthest = distance;
                selected = i;
            }
            if (selected < 0) continue;
            keep[selected] = true;
            pending.Push((range.First, selected));
            pending.Push((selected, range.Last));
        }
        var result = new List<Vector2>();
        for (var i = 0; i < source.Count; ++i)
            if (keep[i]) result.Add(source[i]);
        return result;
    }

    private static float DistanceSquaredToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        var delta = b - a;
        var lengthSquared = delta.LengthSquared();
        if (lengthSquared <= 1e-8f)
            return Vector2.DistanceSquared(point, a);
        var t = Math.Clamp(Vector2.Dot(point - a, delta) / lengthSquared, 0, 1);
        return Vector2.DistanceSquared(point, a + delta * t);
    }

    private bool CanTraverse(int from, int to, bool requireKnownEdges)
    {
        if (!TryEdgeBits(from, to, out var bit, out _)) return false;
        var known = (KnownEdges[from] & (byte)bit) != 0;
        return (!requireKnownEdges || known) && (!known || (BlockedEdges[from] & (byte)bit) == 0);
    }

    private bool TryEdgeBits(int from, int to, out TopologyEdge fromBit, out TopologyEdge toBit)
    {
        fromBit = toBit = TopologyEdge.None;
        if ((uint)from >= (uint)CellCount || (uint)to >= (uint)CellCount)
            return false;
        var fromX = from % Width;
        var fromZ = from / Width;
        var toX = to % Width;
        var toZ = to / Width;
        (fromBit, toBit) = (toX - fromX, toZ - fromZ) switch
        {
            (0, -1) => (TopologyEdge.North, TopologyEdge.South),
            (1, 0) => (TopologyEdge.East, TopologyEdge.West),
            (0, 1) => (TopologyEdge.South, TopologyEdge.North),
            (-1, 0) => (TopologyEdge.West, TopologyEdge.East),
            _ => (TopologyEdge.None, TopologyEdge.None)
        };
        return fromBit != TopologyEdge.None;
    }

    private bool EdgeMasksAreSymmetric()
    {
        for (var z = 0; z < Height; ++z)
            for (var x = 0; x < Width; ++x)
            {
                var index = z * Width + x;
                if (x + 1 < Width && !Same(index, index + 1, TopologyEdge.East, TopologyEdge.West)) return false;
                if (z + 1 < Height && !Same(index, index + Width, TopologyEdge.South, TopologyEdge.North)) return false;
            }
        return true;

        bool Same(int first, int second, TopologyEdge firstBit, TopologyEdge secondBit)
            => ((KnownEdges[first] & (byte)firstBit) != 0) == ((KnownEdges[second] & (byte)secondBit) != 0)
                && ((BlockedEdges[first] & (byte)firstBit) != 0) == ((BlockedEdges[second] & (byte)secondBit) != 0);
    }

    private static string Fingerprint(byte[] connected, short[] heights, byte[] knownEdges, byte[] blockedEdges)
    {
        ulong hash = 14695981039346656037UL;
        for (var i = 0; i < connected.Length; ++i)
        {
            hash ^= connected[i]; hash *= 1099511628211UL;
            if (connected[i] == (byte)TopologyCell.Passable)
            {
                hash ^= (ushort)heights[i]; hash *= 1099511628211UL;
                hash ^= knownEdges[i]; hash *= 1099511628211UL;
                hash ^= blockedEdges[i]; hash *= 1099511628211UL;
            }
        }
        return hash.ToString("X16");
    }
}
