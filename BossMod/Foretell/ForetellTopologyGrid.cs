using System.Numerics;

namespace BossMod.Foretell;

internal enum TopologyCell : byte { Unknown, Passable, Blocked, Void }

internal sealed record TopologyAnalysis(
    string Fingerprint,
    byte[] ConnectedCells,
    short[] HeightCentimeters,
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

    public void Reset(Vector3 center, float radius, float resolution)
    {
        Resolution = Math.Clamp(resolution, .5f, 4f);
        var halfCells = Math.Max(4, (int)MathF.Ceiling(radius / Resolution));
        Width = Height = halfCells * 2 + 1;
        OriginX = MathF.Round(center.X / Resolution) * Resolution - halfCells * Resolution;
        OriginZ = MathF.Round(center.Z / Resolution) * Resolution - halfCells * Resolution;
        ReferenceY = center.Y;
        Cells = new byte[CellCount];
        Heights = new float[CellCount];
        Array.Fill(Heights, float.NaN);
        Cursor = 0;
    }

    public void Clear()
    {
        OriginX = OriginZ = ReferenceY = Resolution = 0;
        Width = Height = Cursor = 0;
        Cells = [];
        Heights = [];
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
            Heights = Heights.ToArray()
        };

    public bool Restore(float originX, float originZ, float referenceY, float resolution, int width, int height,
        byte[] connectedCells, short[] heightCentimeters)
    {
        var count = (long)width * height;
        if (!float.IsFinite(originX) || !float.IsFinite(originZ) || !float.IsFinite(referenceY)
            || !float.IsFinite(resolution) || resolution is < .5f or > 4f || width <= 0 || height <= 0
            || count > 1_000_000 || connectedCells.Length != count || heightCentimeters.Length != count)
            return false;
        OriginX = originX;
        OriginZ = originZ;
        ReferenceY = referenceY;
        Resolution = resolution;
        Width = width;
        Height = height;
        Cells = connectedCells.ToArray();
        Heights = heightCentimeters.Select(value => value == short.MinValue ? float.NaN : referenceY + value / 100f).ToArray();
        Cursor = CellCount;
        return true;
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

    public TopologyAnalysis Analyze(Vector2 seedWorld, float maxStepHeight = 1.75f)
    {
        var connected = new byte[CellCount];
        for (var i = 0; i < CellCount; ++i)
            connected[i] = Cells[i] == (byte)TopologyCell.Unknown ? (byte)TopologyCell.Unknown : (byte)TopologyCell.Blocked;

        var seedX = Math.Clamp((int)((seedWorld.X - OriginX) / Resolution), 0, Width - 1);
        var seedZ = Math.Clamp((int)((seedWorld.Y - OriginZ) / Resolution), 0, Height - 1);
        var seed = FindNearestPassable(seedX, seedZ);
        if (seed >= 0)
            Flood(seed, connected, maxStepHeight);

        var components = CountRawComponents(maxStepHeight);
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
        var fingerprint = Fingerprint(connected, packedHeights);
        return new(fingerprint, connected, packedHeights, contours, passable, blocked, unknown, components);
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

    private void Flood(int seed, byte[] connected, float maxStep)
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
            connected[next] = (byte)TopologyCell.Passable;
            queue.Enqueue(next);
        }
    }

    private int CountRawComponents(float maxStep)
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
                loops.Add(SimplifyClosed(loop));
        }
        return loops;

        Vector2 ToWorld((int X, int Z) p) => new(OriginX + p.X * Resolution, OriginZ + p.Z * Resolution);
        (int X, int Z) FromWorld(Vector2 p) => ((int)MathF.Round((p.X - OriginX) / Resolution), (int)MathF.Round((p.Y - OriginZ) / Resolution));
    }

    private static List<Vector2> SimplifyClosed(List<Vector2> source)
    {
        if (source.Count < 5) return source;
        var result = new List<Vector2>(source.Count);
        for (var i = 0; i < source.Count; ++i)
        {
            var a = source[(i + source.Count - 1) % source.Count];
            var b = source[i];
            var c = source[(i + 1) % source.Count];
            var ab = b - a;
            var bc = c - b;
            if (Math.Abs(ab.X * bc.Y - ab.Y * bc.X) > .0001f)
                result.Add(b);
        }
        return result.Count >= 3 ? result : source;
    }

    private static string Fingerprint(byte[] connected, short[] heights)
    {
        ulong hash = 14695981039346656037UL;
        for (var i = 0; i < connected.Length; ++i)
        {
            hash ^= connected[i]; hash *= 1099511628211UL;
            if (connected[i] == (byte)TopologyCell.Passable)
            {
                hash ^= (ushort)heights[i]; hash *= 1099511628211UL;
            }
        }
        return hash.ToString("X16");
    }
}
