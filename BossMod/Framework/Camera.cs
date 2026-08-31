using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace BossMod;

[SkipLocalsInit]
sealed class Camera
{
    public static Camera? Instance;

    public Vector3 Origin;
    public Matrix4x4 View;
    public Matrix4x4 Proj;
    public Matrix4x4 ViewProj;
    public Vector4 NearPlane;
    public float CameraAzimuth; // facing north = 0, facing west = pi/4, facing south = +-pi/2, facing east = -pi/4
    public float CameraAltitude; // facing horizontally = 0, facing down = pi/4, facing up = -pi/4
    public Vector2 ViewportSize;

    private enum WorldPrimitiveRunKind : byte { Lines, Curves }

    private struct WorldPrimitiveRun
    {
        public WorldPrimitiveRunKind Kind;
        public int Start;
        public int Count;
        public int CurveLineCount;
    }

    private readonly List<Dx11ArenaRenderer.WorldLineInstance> _worldDrawLines = [];
    private readonly List<Dx11ArenaRenderer.WorldCurveInstance> _worldDrawCurves = [];
    private readonly List<WorldPrimitiveRun> _worldPrimitiveRuns = [];
    // Slot 0 is permanently reserved for identity. Keeping that invariant across frames removes an EnsureIdentity call from every single world-line submission and avoids re-adding the
    // identity transform after each batch
    private readonly List<Dx11ArenaRenderer.WorldLineTransform> _worldTransforms = [Dx11ArenaRenderer.WorldLineTransform.Identity];

    public unsafe void Update()
    {
        var controlCamera = CameraManager.Instance()->GetActiveCamera();
        var renderCamera = controlCamera != null ? controlCamera->SceneCamera.RenderCamera : null;
        if (renderCamera == null)
        {
            return;
        }

        Origin = renderCamera->Origin;
        View = renderCamera->ViewMatrix;
        View.M44 = 1f; // for whatever reason, game doesn't initialize it...
        Proj = renderCamera->ProjectionMatrix;
        ViewProj = View * Proj;

        // Game uses reverse-z. Keep the explicit world-space near plane used by the GPU line shader.
        NearPlane = new(View.M13, View.M23, View.M33, View.M43 + renderCamera->NearPlane);

        CameraAzimuth = MathF.Atan2(View.M13, View.M33);
        CameraAltitude = MathF.Asin(View.M23);
        var device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        ViewportSize = new(device->Width, device->Height);
    }

    public void DrawWorldPrimitives()
    {
        if (_worldPrimitiveRuns.Count == 0)
        {
            return;
        }

        var batchStarted = false;
        try
        {
            var viewport = ImGuiHelpers.MainViewport;
            batchStarted = Dx11ArenaRenderer.BeginWorldBatch(ImGui.GetBackgroundDrawList(), viewport.Pos, viewport.Size, ViewProj, NearPlane,
                CollectionsMarshal.AsSpan(_worldTransforms));
            if (!batchStarted)
            {
                return;
            }

            var lines = CollectionsMarshal.AsSpan(_worldDrawLines);
            var curves = CollectionsMarshal.AsSpan(_worldDrawCurves);
            var runs = CollectionsMarshal.AsSpan(_worldPrimitiveRuns);
            var len = runs.Length;
            for (var i = 0; i < len; ++i)
            {
                ref var run = ref runs[i];
                if (run.Kind == WorldPrimitiveRunKind.Lines)
                {
                    Dx11ArenaRenderer.AppendWorldLines(lines.Slice(run.Start, run.Count));
                }
                else
                {
                    Dx11ArenaRenderer.AppendWorldCurves(curves.Slice(run.Start, run.Count), run.CurveLineCount);
                }
            }
        }
        finally
        {
            if (batchStarted)
            {
                Dx11ArenaRenderer.EndScreenBatch();
            }
            _worldDrawLines.Clear();
            _worldDrawCurves.Clear();
            _worldPrimitiveRuns.Clear();
            if (_worldTransforms.Count > 1)
            {
                CollectionsMarshal.SetCount(_worldTransforms, 1);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DrawWorldLine(Vector3 start, Vector3 end, uint color, float thickness = 1f)
    {
        AppendWorldLineUnchecked(start, end, color, thickness);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendWorldLineUnchecked(Vector3 start, Vector3 end, uint color, float thickness)
    {
        var index = _worldDrawLines.Count;
        _worldDrawLines.Add(new(start, end, color, thickness, 0u));
        RecordWorldPrimitiveRun(WorldPrimitiveRunKind.Lines, index, 1, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendWorldCurveUnchecked(in Dx11ArenaRenderer.WorldCurveInstance curve, int lineCount)
    {
        var index = _worldDrawCurves.Count;
        _worldDrawCurves.Add(curve);
        RecordWorldPrimitiveRun(WorldPrimitiveRunKind.Curves, index, 1, lineCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordWorldPrimitiveRun(WorldPrimitiveRunKind kind, int start, int count, int curveLineCount)
    {
        var runCount = _worldPrimitiveRuns.Count;
        if (runCount != 0)
        {
            var runs = CollectionsMarshal.AsSpan(_worldPrimitiveRuns);
            ref var last = ref runs[runCount - 1];
            if (last.Kind == kind && last.CurveLineCount == curveLineCount && last.Start + last.Count == start)
            {
                last.Count += count;
                return;
            }
        }

        _worldPrimitiveRuns.Add(new WorldPrimitiveRun { Kind = kind, Start = start, Count = count, CurveLineCount = curveLineCount });
    }

    // Bulk indexed world-space submission
    // Edge keys encode the first vertex in the high 32 bits and the second in the low 32 bits
    public void DrawWorldIndexedLines(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<ulong> edges, uint color, float thickness = 1f)
    {
        var oldCount = _worldDrawLines.Count;
        var lenE = edges.Length;
        var reservedCount = oldCount + lenE;
        _worldDrawLines.EnsureCapacity(reservedCount);
        CollectionsMarshal.SetCount(_worldDrawLines, reservedCount);
        var destination = CollectionsMarshal.AsSpan(_worldDrawLines);
        var dst = oldCount;

        var lenV = vertices.Length;
        for (var i = 0; i < lenE; ++i)
        {
            var key = edges[i];
            var ia = (int)(key >> 32);
            var ib = (int)(uint)key;
            if ((uint)ia >= (uint)lenV || (uint)ib >= (uint)lenV || ia == ib)
            {
                continue;
            }
            destination[dst++] = new(vertices[ia], vertices[ib], color, thickness, 0u);
        }

        if (dst != reservedCount)
        {
            CollectionsMarshal.SetCount(_worldDrawLines, dst);
        }
        RecordWorldPrimitiveRun(WorldPrimitiveRunKind.Lines, oldCount, dst - oldCount, 0);
    }

    // High-throughput local-space submission. Cached PCB edges remain immutable in local space;
    // one transform-table index is attached to every edge and the GPU performs local->world->clip.
    // If the transform table is ever exhausted, this falls back to CPU-transforming only that call
    // so correctness is preserved without forcing a batch split.
    public void DrawLocalLines(ReadOnlySpan<Dx11ArenaRenderer.WorldLineLocalSegment> lines, ref Dx11ArenaRenderer.WorldLineTransform transform, uint color, float thickness = 1f)
    {
        var transformIndex = GetOrAddWorldTransform(ref transform);

        var oldCount = _worldDrawLines.Count;
        var len = lines.Length;
        var reservedCount = oldCount + len;
        _worldDrawLines.EnsureCapacity(reservedCount);
        CollectionsMarshal.SetCount(_worldDrawLines, reservedCount);
        var destination = CollectionsMarshal.AsSpan(_worldDrawLines);

        if (transformIndex != uint.MaxValue)
        {
            for (var i = 0; i < len; ++i)
            {
                ref readonly var line = ref lines[i];
                destination[oldCount + i] = new(line.From, line.To, color, thickness, transformIndex);
            }
        }
        else
        {
            // Extremely unlikely (>1023 distinct transforms in one overlay batch). Keep the frame correct instead of flushing/reordering the background draw list
            for (var i = 0; i < len; ++i)
            {
                ref readonly var line = ref lines[i];
                destination[oldCount + i] = new(transform.TransformPoint(line.From), transform.TransformPoint(line.To), color, thickness, 0u);
            }
        }
        RecordWorldPrimitiveRun(WorldPrimitiveRunKind.Lines, oldCount, len, 0);
    }

    private uint GetOrAddWorldTransform(ref Dx11ArenaRenderer.WorldLineTransform transform)
    {
        var last = _worldTransforms.Count - 1;
        if (_worldTransforms[last].Equals(transform))
        {
            return (uint)last;
        }

        if (_worldTransforms.Count >= Dx11ArenaRenderer.MaxWorldLineTransforms)
        {
            return uint.MaxValue;
        }

        _worldTransforms.Add(transform);
        return (uint)(_worldTransforms.Count - 1);
    }

    public void DrawWorldCone(Vector3 center, float radius, Angle direction, Angle halfWidth, uint color, float thickness = 1f)
    {
        const int segments = 256;
        const int segmentsP2 = segments + 2;

        var dir = direction.ToDirection();
        var half = halfWidth.ToDirection();
        var curve = Dx11ArenaRenderer.WorldCurveInstance.ArcSector(center, radius, new Vector2(dir.X, dir.Z), new Vector2(half.X, half.Z), color, thickness, segments);
        AppendWorldCurveUnchecked(curve, segmentsP2);
    }

    public void DrawWorldCircle(Vector3 center, float radius, uint color, float thickness = 1f)
    {
        const int segments = 256;
        var curve = Dx11ArenaRenderer.WorldCurveInstance.Circle(center, radius, color, thickness, segments);
        AppendWorldCurveUnchecked(curve, segments);
    }

    // Pass in both Vec 3 center and Shape rectangle so we have the Y coordinate as well.
    public void DrawWorldRectangle(Vector3 center, Rectangle rectangle, uint color, float thickness = 1)
    {
        var pos = rectangle.Center;
        // pull out the 4 vertices directly from Shape Rectangle.
        var dirs = rectangle.Contour(pos);
        const int numSides = 4; // rectangles have 4 sides

        _worldDrawLines.EnsureCapacity(_worldDrawLines.Count + numSides);
        // align each line with vec3 center so that it is drawn at the correct Y value.
        var prev = center + dirs[3].ToVec3();
        // If the list of dirs has values we start at dirs[0] for first line and loop through the
        // array until we end up back at dirs[0]
        for (var i = 0; i < numSides; ++i)
        {
            var curr = center + dirs[i].ToVec3();
            AppendWorldLineUnchecked(curr, prev, color, thickness);
            prev = curr;
        }
    }

    // Shape agnostic drawing logic. Every Shape in Shapes.cs has a contour that is a list of WDir. Just pull that information
    // and peg it to the vec3 center for arena height. Camera doesn't need to redo the logic, just use what Shapes delivers.
    public void DrawWorldShape(Vector3 center, Shape shape, uint color, float thickness = 1)
    {
        var pos = new WPos(center.X, center.Z);
        var dirs = shape.Contour(pos);
        var dirsCount = dirs.Count;

        _worldDrawLines.EnsureCapacity(_worldDrawLines.Count + dirsCount);
        // align each line with vec3 center so that it is drawn at the correct Y value.
        var prev = center + dirs[dirsCount - 1].ToVec3(); // Start from the last point so we can complete the outline of the shape
        // If the list of dirs has values we start at dirs[0] for first line and loop through the
        // array until we end up back at dirs[0]
        for (var i = 0; i < dirsCount; ++i)
        {
            var curr = center + dirs[i].ToVec3();
            AppendWorldLineUnchecked(curr, prev, color, thickness);
            prev = curr;
        }
    }

    // Draw a shape from a polygon such as customArenaBounds : adapted from MiniArena.cs
    // Curves and circles will not look clean because these guts were designed for tiny radar shapes in mind.
    // Import to radar and then determine if it looks right instead of being bothered by jaggy curves for now.
    public void DrawWorldPoly(Vector3 center, RelSimplifiedComplexPolygon poly, uint color, float thickness = 1)
    {
        var parts = CollectionsMarshal.AsSpan(poly.Parts);
        var len = parts.Length;

        for (var i = 0; i < len; ++i)
        {
            var part = parts[i];

            DrawContour(part.Exterior);
            var countH = part.HoleStarts.Count;
            for (var h = 0; h < countH; ++h)
            {
                DrawContour(part.Interior(h));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void DrawContour(ReadOnlySpan<WDir> contour)
        {
            var len = contour.Length;
            if (len == 0)
            {
                return;
            }

            _worldDrawLines.EnsureCapacity(_worldDrawLines.Count + len);
            var prev = center + contour[len - 1].ToVec3();
            for (var i = 0; i < len; ++i)
            {
                var curr = center + contour[i].ToVec3();
                AppendWorldLineUnchecked(curr, prev, color, thickness);
                prev = curr;
            }
        }
    }

    public void DrawWorldSphere(Vector3 center, float radius, uint color, float thickness = 1)
    {
        const int segments = 256;
        const int tripleSegments = 3 * segments;
        var curve = Dx11ArenaRenderer.WorldCurveInstance.Sphere(center, radius, color, thickness, segments);
        AppendWorldCurveUnchecked(curve, tripleSegments);
    }

    // Procedural local-space cylinder. Returns false only when the shared transform table is full,
    // allowing callers with cached explicit edges to preserve correctness as a very rare fallback
    public bool DrawLocalCylinder(ref Dx11ArenaRenderer.WorldLineTransform transform, uint color, float thickness = 1f, float radius = 1f, float halfHeight = 1f)
    {
        var transformIndex = GetOrAddWorldTransform(ref transform);
        if (transformIndex == uint.MaxValue)
        {
            return false;
        }
        const int segments = 256;
        const int tripleSegments = 3 * segments;

        var curve = Dx11ArenaRenderer.WorldCurveInstance.Cylinder(Vector3.Zero, radius, halfHeight, color, thickness, segments, transformIndex);
        AppendWorldCurveUnchecked(curve, tripleSegments);
        return true;
    }
}
