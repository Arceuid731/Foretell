using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface;

namespace BossMod;

// note on coordinate systems:
// - world coordinates - X points West to East, Z points North to South - so SE is corner with both maximal coords, NW is corner with both minimal coords
//                       rotation 0 corresponds to South, and increases counterclockwise (so East is +pi/2, North is pi, West is -pi/2)
// - camera azimuth 0 correpsonds to camera looking North and increases counterclockwise
// - screen coordinates - X points left to right, Y points top to bottom
[SkipLocalsInit]
public sealed class MiniArena(WPos center, ArenaBounds bounds)
{
    public static readonly BossModuleConfig Config = Service.Config.Get<BossModuleConfig>();
    private WPos _center = center;

    public WPos Center
    {
        get => _center;
        set
        {
            if (_center != value)
            {
                _center = value;
            }
        }
    }

    private ArenaBounds _bounds = bounds;
    public ArenaBounds Bounds
    {
        get => _bounds;
        set
        {
            if (!ReferenceEquals(_bounds, value))
            {
                _bounds = value;
                _bounds.ScreenHalfSize = ScreenHalfSize; // ensure arena bounds are fully initialized before doing anything else
            }
        }
    }

    public float ScreenHalfSize => 150f * Config.ArenaScale;
    public float ScreenMarginSize => 20f * Config.ArenaScale;

    // these are set at the beginning of each draw
    public Vector2 ScreenCenter;
    private Angle _cameraAzimuth;
    private float _cameraSinAzimuth;
    private float _cameraCosAzimuth = 1f;

    // Frame-constant rendering state, populated once by Begin().
    private float _scaledCos;
    private float _scaledSin;
    private float _frameArenaScale = 1f;
    private float _frameThicknessScale = 1f;
    private float _frameActorScale = 1f;
    private float _frameScreenHalfSize;
    private float _frameScreenMarginSize;
    private float _frameCardinalsFontSize = 17f;
    private bool _frameShowOutlinesAndShadows;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool InBounds(WPos position) => _bounds.Contains(position - _center);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WPos ClampToBounds(WPos position) => _center + _bounds.ClampToBounds(position - _center);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float IntersectRayBounds(WPos rayOrigin, in WDir rayDir) => _bounds.IntersectRay(rayOrigin - _center, rayDir);

    // prepare for drawing - set up internal state, clip rect etc.
    public void Begin(Angle cameraAzimuth)
    {
        // Snapshot renderer-facing configuration once per arena frame. Most primitive methods are hot
        // and do not need to re-read the config object for values that cannot meaningfully change
        // halfway through one Begin/End pair.
        var arenaScale = Config.ArenaScale;
        _frameArenaScale = arenaScale;
        _frameThicknessScale = Config.ThicknessScale;
        _frameActorScale = Config.ActorScale;
        _frameShowOutlinesAndShadows = Config.ShowOutlinesAndShadows;
        _frameCardinalsFontSize = Config.CardinalsFontSize;
        var screenHalfSize = _frameScreenHalfSize = 150f * arenaScale;
        var screenMarginSize = _frameScreenMarginSize = 20f * arenaScale;

        var centerOffset = new Vector2(screenMarginSize + Config.SlackForRotations * screenHalfSize);
        var fullSize = 2f * centerOffset;
        var currentWindowSize = ImGui.GetWindowSize();
        var requiredWindowSize = Vector2.Max(fullSize, currentWindowSize);
        ImGui.SetWindowSize(requiredWindowSize);
        var cursor = ImGui.GetCursorScreenPos();
        ImGui.Dummy(fullSize);

        if (_bounds.ScreenHalfSize != screenHalfSize)
        {
            _bounds.ScreenHalfSize = screenHalfSize;
        }

        var screenCenter = cursor + centerOffset;
        ScreenCenter = screenCenter;

        _cameraAzimuth = cameraAzimuth;
        (_cameraSinAzimuth, _cameraCosAzimuth) = MathF.SinCos(cameraAzimuth.Rad);

        var screenScale = screenHalfSize * _bounds.InvRadius;
        var scaledCos = _cameraCosAzimuth * screenScale;
        var scaledSin = _cameraSinAzimuth * screenScale;
        var centerX = screenCenter.X;
        var centerY = screenCenter.Y;

        _scaledCos = scaledCos;
        _scaledSin = scaledSin;

        var drawList = ImGui.GetWindowDrawList();

        var wmin = ImGui.GetWindowPos();
        var wmax = wmin + ImGui.GetWindowSize();
        drawList.PushClipRect(Vector2.Max(cursor, wmin), Vector2.Min(cursor + fullSize, wmax));

        // Start our custom DX11 arena renderer. Arena background, border and stencil clipping all
        // share the cached arena SDF. Arena shapes are considered immutable, so object identity is the cache key.
        Dx11ArenaRenderer.BeginArena(drawList, _bounds.Shape, centerX, centerY, _scaledCos, _scaledSin, screenScale);

        if (Config.OpaqueArenaBackground)
        {
            Dx11ArenaRenderer.AppendArenaBackground(Colors.Background);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2 WorldPositionToScreenPosition(WPos p) => ScreenCenter + WorldOffsetToScreenOffset(p - _center);

    // this is useful for drawing on margins (TODO better api)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2 RotatedCoords(Vector2 coords)
    {
        var cx = coords.X;
        var cy = coords.Y;
        var x = cx * _cameraCosAzimuth - cy * _cameraSinAzimuth;
        var y = cy * _cameraCosAzimuth + cx * _cameraSinAzimuth;
        return new(x, y);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector2 WorldOffsetToScreenOffset(WDir worldOffset)
    {
        var wx = worldOffset.X;
        var wz = worldOffset.Z;
        return new(wx * _scaledCos - wz * _scaledSin, wz * _scaledCos + wx * _scaledSin);
    }

    // Unclipped primitive rendering that accepts world-space positions
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddLine(WPos a, WPos b, uint color = default, float thickness = 1f)
    {
        PrepareOutlineStyle(color, thickness, out var lineColor, out var lineThickness, out var shadowColor, out var shadowThickness);
        Span<WDir> points = [a - _center, b - _center];
        Dx11ArenaRenderer.AppendPolyline(points, false, lineColor, lineThickness, shadowColor, shadowThickness);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddCircleUnfilled(WPos center, float radius, uint color = default, float thickness = 1f)
    {
        PrepareOutlineStyle(color, thickness, out var lineColor, out var lineThickness, out var shadowColor, out var shadowThickness);
        Dx11ArenaRenderer.AppendCircleOutlineUnclipped(center - _center, radius, lineColor, lineThickness, shadowColor, shadowThickness);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddTriangle(WPos p1, WPos p2, WPos p3, uint color = default, float thickness = 1f)
    {
        Dx11ArenaRenderer.AppendPrimitiveTriangleStroke(p1 - _center, p2 - _center, p3 - _center, color != default ? color : Colors.Danger, thickness * _frameThicknessScale);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddTriangleFilled(WPos p1, WPos p2, WPos p3, uint color = default)
        => Dx11ArenaRenderer.AppendPrimitiveTriangle(p1 - _center, p2 - _center, p3 - _center, color != default ? color : Colors.Danger);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddQuad(WPos p1, WPos p2, WPos p3, WPos p4, uint color = default, float thickness = 1f)
    {
        Dx11ArenaRenderer.AppendQuadStroke(p1 - _center, p2 - _center, p3 - _center, p4 - _center, color != default ? color : Colors.Danger, thickness * _frameThicknessScale);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddRect(WPos origin, WDir direction, float lenFront, float lenBack, float halfWidth, uint color, float thickness = 1f)
    {
        thickness *= _frameThicknessScale;
        var side = halfWidth * direction.OrthoR();
        var front = origin + lenFront * direction;
        var back = origin - lenBack * direction;
        AddQuad(front + side, front - side, back - side, back + side, color, thickness);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddPolygon(ReadOnlySpan<WPos> vertices, uint color = default, float thickness = 1f)
    {
        var len = vertices.Length;
        Span<WDir> local = stackalloc WDir[len];
        for (var i = 0; i < len; ++i)
        {
            local[i] = vertices[i] - _center;
        }
        Dx11ArenaRenderer.AppendPolyline(local, true, color != default ? color : Colors.Danger, thickness * _frameThicknessScale);
    }

    public void AddComplexPolygon(RelSimplifiedComplexPolygon poly, uint color = default, float thickness = 1f)
    {
        var parts = CollectionsMarshal.AsSpan(poly.Parts);
        var len = parts.Length;
        PrepareOutlineStyle(color, thickness, out var lineColor, out var lineThickness, out var shadowColor, out var shadowThickness);

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
            => Dx11ArenaRenderer.AppendPolyline(contour, true, lineColor, lineThickness, shadowColor, shadowThickness);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PathLineTo(WPos p) => Dx11ArenaRenderer.PathLineTo(p - _center);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PathArcTo(WPos center, float radius, float amin, float amax) => Dx11ArenaRenderer.PathArcTo(center - _center, radius, amin, amax);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PathStroke(bool closed, uint color = default, float thickness = 1f)
        => Dx11ArenaRenderer.PathStroke(closed, color != default ? color : Colors.Danger, thickness * Config.ThicknessScale);

    // Filled zones:
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneCone(WPos center, float innerRadius, float outerRadius, Angle centerDirection, Angle halfAngle, uint color)
        => Dx11ArenaRenderer.AppendCone(center - _center, innerRadius, outerRadius, centerDirection.ToDirection(), halfAngle.Rad, color != default ? color : Colors.AOE);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneCircle(WPos center, float radius, uint color)
        => Dx11ArenaRenderer.AppendCircle(center - _center, radius, color != default ? color : Colors.AOE);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneDonut(WPos center, float innerRadius, float outerRadius, uint color)
        => Dx11ArenaRenderer.AppendDonut(center - _center, innerRadius, outerRadius, color != default ? color : Colors.AOE);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneTri(WPos a, WPos b, WPos c, uint color)
        => Dx11ArenaRenderer.AppendTriangle(a - _center, b - _center, c - _center, color != default ? color : Colors.AOE);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneIsoscelesTri(WPos apex, WDir height, WDir halfBase, uint color)
    {
        var a = apex - _center;
        Dx11ArenaRenderer.AppendTriangle(a, a + height + halfBase, a + height - halfBase, color != default ? color : Colors.AOE);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneIsoscelesTri(WPos apex, Angle direction, Angle halfAngle, float height, uint color)
    {
        var a = apex - _center;
        var dir = direction.ToDirection();
        var h = height * dir;
        var halfBase = height * halfAngle.Tan() * dir.OrthoL();
        Dx11ArenaRenderer.AppendTriangle(a, a + h + halfBase, a + h - halfBase, color != default ? color : Colors.AOE);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneRect(WPos origin, WDir direction, float lenFront, float lenBack, float halfWidth, uint color)
        => Dx11ArenaRenderer.AppendRect(origin - _center, direction, lenFront, lenBack, halfWidth, color != default ? color : Colors.AOE);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneRect(WPos origin, Angle direction, float lenFront, float lenBack, float halfWidth, uint color)
        => Dx11ArenaRenderer.AppendRect(origin - _center, direction.ToDirection(), lenFront, lenBack, halfWidth, color != default ? color : Colors.AOE);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneRect(WPos start, WPos end, float halfWidth, uint color)
    {
        var dir = end - start;
        var len = dir.Length();
        if (len > 0f)
        {
            Dx11ArenaRenderer.AppendRect(start - _center, dir / len, len, 0f, halfWidth, color != default ? color : Colors.AOE);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneCross(WPos origin, Angle rotation, float range, float halfWidth, uint color)
        => Dx11ArenaRenderer.AppendCross(origin - _center, rotation.ToDirection(), range, halfWidth, color != default ? color : Colors.AOE);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneRelPoly(RelSimplifiedComplexPolygon poly, uint color)
        => Dx11ArenaRenderer.AppendRelPoly(poly, color != default ? color : Colors.AOE);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneCapsule(WPos start, WDir direction, float radius, float length, uint color)
        => Dx11ArenaRenderer.AppendCapsule(start - _center, direction, radius, length, color != default ? color : Colors.AOE);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneArcCapsule(WPos start, WPos orbitCenter, Angle angularLength, float radius, uint color)
        => Dx11ArenaRenderer.AppendArcCapsule(start - _center, orbitCenter - start, angularLength.Rad, radius, color != default ? color : Colors.AOE);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PrepareOutlineStyle(uint color, float thickness, out uint lineColor, out float lineThickness, out uint shadowColor, out float shadowThickness)
    {
        lineColor = color != default ? color : Colors.Danger;
        lineThickness = thickness * _frameThicknessScale;
        if (_frameShowOutlinesAndShadows)
        {
            shadowColor = Colors.Shadows;
            shadowThickness = (thickness + 1f) * _frameThicknessScale;
        }
        else
        {
            shadowColor = 0u;
            shadowThickness = lineThickness;
        }
    }

    // draw zone outlines
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneConeOutline(WPos center, float innerRadius, float outerRadius, Angle centerDirection, Angle halfAngle, uint color = default, float thickness = 1f)
    {
        PrepareOutlineStyle(color, thickness, out var lineColor, out var lineThickness, out var shadowColor, out var shadowThickness);
        Dx11ArenaRenderer.AppendConeOutline(center - _center, innerRadius, outerRadius, centerDirection.ToDirection(), halfAngle.Rad, lineColor, lineThickness, shadowColor, shadowThickness);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneCircleOutline(WPos center, float radius, uint color = default, float thickness = 1f)
    {
        PrepareOutlineStyle(color, thickness, out var lineColor, out var lineThickness, out var shadowColor, out var shadowThickness);
        Dx11ArenaRenderer.AppendCircleOutline(center - _center, radius, lineColor, lineThickness, shadowColor, shadowThickness);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneDonutOutline(WPos center, float innerRadius, float outerRadius, uint color = default, float thickness = 1f)
    {
        PrepareOutlineStyle(color, thickness, out var lineColor, out var lineThickness, out var shadowColor, out var shadowThickness);
        Dx11ArenaRenderer.AppendDonutOutline(center - _center, innerRadius, outerRadius, lineColor, lineThickness, shadowColor, shadowThickness);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneTriOutline(WPos a, WPos b, WPos c, uint color = default, float thickness = 1f)
    {
        PrepareOutlineStyle(color, thickness, out var lineColor, out var lineThickness, out var shadowColor, out var shadowThickness);
        Dx11ArenaRenderer.AppendTriangleOutline(a - _center, b - _center, c - _center, lineColor, lineThickness, shadowColor, shadowThickness);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneIsoscelesTriOutline(WPos apex, WDir height, WDir halfBase, uint color = default, float thickness = 1f)
    {
        var a = apex - _center;
        PrepareOutlineStyle(color, thickness, out var lineColor, out var lineThickness, out var shadowColor, out var shadowThickness);
        Dx11ArenaRenderer.AppendTriangleOutline(a, a + height + halfBase, a + height - halfBase, lineColor, lineThickness, shadowColor, shadowThickness);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneRectOutline(WPos origin, WDir direction, float lenFront, float lenBack, float halfWidth, uint color = default, float thickness = 1f)
    {
        PrepareOutlineStyle(color, thickness, out var lineColor, out var lineThickness, out var shadowColor, out var shadowThickness);
        Dx11ArenaRenderer.AppendRectOutline(origin - _center, direction, lenFront, lenBack, halfWidth, lineColor, lineThickness, shadowColor, shadowThickness);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneRectOutline(WPos origin, Angle direction, float lenFront, float lenBack, float halfWidth, uint color = default, float thickness = 1f)
    {
        PrepareOutlineStyle(color, thickness, out var lineColor, out var lineThickness, out var shadowColor, out var shadowThickness);
        Dx11ArenaRenderer.AppendRectOutline(origin - _center, direction.ToDirection(), lenFront, lenBack, halfWidth, lineColor, lineThickness, shadowColor, shadowThickness);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneRectOutline(WPos start, WPos end, float halfWidth, uint color = default, float thickness = 1f)
    {
        var dir = end - start;
        var len = dir.Length();
        if (!(len > 0f))
        {
            return;
        }
        PrepareOutlineStyle(color, thickness, out var lineColor, out var lineThickness, out var shadowColor, out var shadowThickness);
        Dx11ArenaRenderer.AppendRectOutline(start - _center, dir / len, len, 0f, halfWidth, lineColor, lineThickness, shadowColor, shadowThickness);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneCrossOutline(WPos origin, Angle rotation, float range, float halfWidth, uint color = default, float thickness = 1f)
    {
        PrepareOutlineStyle(color, thickness, out var lineColor, out var lineThickness, out var shadowColor, out var shadowThickness);
        Dx11ArenaRenderer.AppendCrossOutline(origin - _center, rotation.ToDirection(), range, halfWidth, lineColor, lineThickness, shadowColor, shadowThickness);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneRelPolyOutline(RelSimplifiedComplexPolygon poly, uint color = default, float thickness = 1f)
    {
        PrepareOutlineStyle(color, thickness, out var lineColor, out var lineThickness, out var shadowColor, out var shadowThickness);
        Dx11ArenaRenderer.AppendCustomOutline(poly, lineColor, lineThickness, shadowColor, shadowThickness);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneCapsuleOutline(WPos start, WDir direction, float radius, float length, uint color = default, float thickness = 1f)
    {
        PrepareOutlineStyle(color, thickness, out var lineColor, out var lineThickness, out var shadowColor, out var shadowThickness);
        Dx11ArenaRenderer.AppendCapsuleOutline(start - _center, direction, radius, length, lineColor, lineThickness, shadowColor, shadowThickness);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ZoneArcCapsuleOutline(WPos start, WPos orbitCenter, Angle angularLength, float radius, uint color = default, float thickness = 1f)
    {
        PrepareOutlineStyle(color, thickness, out var lineColor, out var lineThickness, out var shadowColor, out var shadowThickness);
        Dx11ArenaRenderer.AppendArcCapsuleOutline(start - _center, orbitCenter - start, angularLength.Rad, radius, lineColor, lineThickness, shadowColor, shadowThickness);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SpriteScreen(Vector2 min, Vector2 max, IDalamudTextureWrap texture, uint color = 0xFFFFFFFFu)
        => Dx11ArenaRenderer.AppendSpriteScreen(min, max, texture, color);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TextScreen(Vector2 center, string text, uint color, float fontSize = 17f, uint outlineColor = 0u, float outlineWidth = 0f)
        => Dx11ArenaRenderer.AppendTextScreen(center, text, fontSize * _frameArenaScale, color, outlineColor, outlineWidth * _frameArenaScale);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TextWorld(WPos center, string text, uint color, float fontSize = 17f, uint outlineColor = 0u, float outlineWidth = 0f)
        => TextScreen(WorldPositionToScreenPosition(center), text, color, fontSize, outlineColor, outlineWidth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IconScreen(Vector2 center, FontAwesomeIcon icon, uint color, float fontSize = 17f)
    {
        var text = icon.ToIconString();
        Dx11ArenaRenderer.AppendIconScreen(center, text, fontSize, color);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IconWorld(WPos center, FontAwesomeIcon icon, uint color, float fontSize = 17f) => IconScreen(WorldPositionToScreenPosition(center), icon, color, fontSize);

    public void CardinalNames()
    {
        var center = ScreenCenter;
        var fontSetting = _frameCardinalsFontSize;
        var offCenterSizeOffset = (_frameScreenHalfSize + _frameScreenMarginSize * 0.5f) * _bounds.ScaleFactor + fontSetting - 17f;
        var offS = RotatedCoords(new(default, offCenterSizeOffset));
        var offE = RotatedCoords(new(offCenterSizeOffset, default));
        TextScreen(center - offS, "N", Colors.CardinalN, fontSetting);
        TextScreen(center + offS, "S", Colors.CardinalS, fontSetting);
        TextScreen(center + offE, "E", Colors.CardinalE, fontSetting);
        TextScreen(center - offE * 1.02f, "W", Colors.CardinalW, fontSetting); // w is slightly wider, so we are putting it 2% farther away than the E
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ActorInsideBounds(WPos position, Angle rotation, uint color)
    {
        var scale = _frameActorScale * _frameThicknessScale;
        var dir = rotation.ToDirection();
        var scale07 = scale * 0.7f * dir;
        var scale035 = scale * 0.35f * dir;
        var scale0433 = scale * 0.433f * dir.OrthoR();
        var positionscale07 = position + scale07;
        var positionscale035 = position - scale035;
        var positionscale035pscale0433 = positionscale035 + scale0433;
        var positionscale035mscale0433 = positionscale035 - scale0433;
        if (_frameShowOutlinesAndShadows)
        {
            AddTriangle(positionscale07, positionscale035pscale0433, positionscale035mscale0433, Colors.Shadows, 2f);
        }

        AddTriangleFilled(positionscale07, positionscale035pscale0433, positionscale035mscale0433, color);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ActorOutsideBounds(WPos position, Angle rotation, uint color)
    {
        var scale = _frameActorScale;
        var dir = rotation.ToDirection();
        var scale07 = scale * 0.7f * dir;
        var scale035 = scale * 0.35f * dir;
        var scale0433 = scale * 0.433f * dir.OrthoR();
        var positionscale035 = position - scale035;
        AddTriangle(position + scale07, positionscale035 + scale0433, positionscale035 - scale0433, color);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ActorProjected(WPos from, WPos to, Angle rotation, uint color)
    {
        if (InBounds(to))
        {
            // projected position is inside bounds
            ActorInsideBounds(to, rotation, color);
            return;
        }

        var dir = to - from;
        var l = dir.Length();

        if (l == default)
        {
            return; // can't determine projection direction
        }

        dir /= l;
        var t = IntersectRayBounds(from, dir);
        if (t <= l)
        {
            ActorOutsideBounds(from + t * dir, rotation, color);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Actor(WPos position, Angle rotation, uint color)
    {
        if (InBounds(position))
        {
            ActorInsideBounds(position, rotation, color);
        }
        else
        {
            ActorOutsideBounds(ClampToBounds(position), rotation, color);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Actor(Actor? actor, uint color = default, bool allowDeadAndUntargetable = false)
    {
        if (actor != null && !actor.IsDestroyed && (allowDeadAndUntargetable || actor.IsTargetable && !actor.IsDead))
        {
            Actor(actor.Position, actor.Rotation, color == default ? Colors.Enemy : color);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Actors(IEnumerable<Actor> actors, uint color = default, bool allowDeadAndUntargetable = false)
    {
        foreach (var a in actors)
        {
            Actor(a, color == default ? Colors.Enemy : color, allowDeadAndUntargetable);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Actors(List<Actor> actors, uint color = default, bool allowDeadAndUntargetable = false)
    {
        var count = actors.Count;
        for (var i = 0; i < count; ++i)
        {
            Actor(actors[i], color == default ? Colors.Enemy : color, allowDeadAndUntargetable);
        }
    }

    public void Actors(BossModule module, uint[] actors, uint color = default, bool allowDeadAndUntargetable = false)
    {
        var actors_ = actors;
        var len = actors_.Length;
        var color_ = color == default ? Colors.Enemy : color;
        for (var i = 0; i < len; ++i)
        {
            var enemies = module.Enemies(actors[i]);
            var count = enemies.Count;
            for (var j = 0; j < count; ++j)
            {
                var enemy = enemies[j];
                if (!enemy.IsDestroyed && (allowDeadAndUntargetable || enemy.IsTargetable && !enemy.IsDead))
                {
                    Actor(enemy.Position, enemy.Rotation, color_);
                }
            }
        }
    }

    public void ActorsInBounds(BossModule module, uint[] actors, uint color = default, bool allowDeadAndUntargetable = false)
    {
        var actors_ = actors;
        var len = actors_.Length;
        var center = _center;
        var radius = Bounds.Radius;
        var color_ = color == default ? Colors.Enemy : color;
        for (var i = 0; i < len; ++i)
        {
            var enemies = module.Enemies(actors[i]);
            var count = enemies.Count;
            for (var j = 0; j < count; ++j)
            {
                var enemy = enemies[j];
                if (!enemy.IsDestroyed && enemy.Position.AlmostEqual(center, radius) && (allowDeadAndUntargetable || enemy.IsTargetable && !enemy.IsDead))
                {
                    Actor(enemy.Position, enemy.Rotation, color_);
                }
            }
        }
    }

    public static void End()
    {
        // Flush the final contiguous run while the arena clip rect is still active
        Dx11ArenaRenderer.EndArena();
        ImGui.GetWindowDrawList().PopClipRect();
    }
}
