using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private bool _radarWasUnlocked;
    private bool _radarPositionDirty;
    private long _drawFailures;
    private DateTime _lastDrawFailureLog;

    public void Draw()
    {
        DrawSafely(DrawInspector, "inspector");
        if (_cfg.Mode is ForetellMode.Legacy or ForetellMode.Observe) return;
        DrawSafely(() =>
        {
            var confidenceCut = _cfg.VisualConfidence / 100f;
            var displayed = 0;
            foreach (var p in _predictions.Values.Where(ValidPrediction).OrderBy(p => p.Activation))
            {
                if (p.Confidence < confidenceCut || displayed++ >= _cfg.MaxRenderedMechanics) continue;
                if (_cfg.WorldOverlay) DrawWorld(p);
            }
        }, "world overlay");
        if (_cfg.TextHints) DrawSafely(DrawTextHints, "text hints");
        if (_cfg.MiniRadar) DrawSafely(DrawRadar, "radar");
        if (_cfg.SafePositionSuggestions) DrawSafely(DrawSafeSuggestion, "safe suggestion");
    }

    private void DrawSafely(Action draw, string surface)
    {
        try { draw(); }
        catch (Exception e)
        {
            ++_drawFailures;
            var now = DateTime.UtcNow;
            if ((now - _lastDrawFailureLog).TotalSeconds < 5) return;
            _lastDrawFailureLog = now;
            Service.Log($"[Foretell] {surface} draw rejected safely ({_drawFailures} total): {e.GetType().Name}: {e.Message}");
        }
    }

    // Confidence is encoded consistently everywhere Foretell draws learned geometry:
    // cyan/blue = early hypothesis, yellow = learned, orange = warning-grade, red = safe-guidance-grade.
    // Red means "high confidence this is danger", not "more damage".
    private uint ConfidenceColor(float confidence)
    {
        var visual = _cfg.VisualConfidence / 100f;
        var warning = _cfg.WarningConfidence / 100f;
        var safe = _cfg.SafeConfidence / 100f;
        if (confidence >= safe) return Pack(255, 60, 60);
        if (confidence >= warning)
        {
            var t = InverseLerp(warning, Math.Max(warning + .001f, safe), confidence);
            return LerpColor(Pack(255, 185, 55), Pack(255, 60, 60), t);
        }
        if (confidence >= visual)
        {
            var t = InverseLerp(visual, Math.Max(visual + .001f, warning), confidence);
            return LerpColor(Pack(70, 190, 255), Pack(255, 215, 60), t);
        }
        return Pack(130, 130, 180);
    }

    private float ConfidenceThickness(float confidence)
    {
        var visual = _cfg.VisualConfidence / 100f;
        var safe = _cfg.SafeConfidence / 100f;
        return 1.5f + 2.5f * InverseLerp(visual, Math.Max(visual + .001f, safe), confidence);
    }

    private static float InverseLerp(float a, float b, float v) => Math.Clamp((v - a) / Math.Max(.0001f, b - a), 0, 1);

    private static uint Pack(byte r, byte g, byte b, byte a = 255) => (uint)(a << 24 | b << 16 | g << 8 | r);

    private static uint LerpColor(uint a, uint b, float t)
    {
        static byte C(uint c, int shift) => (byte)((c >> shift) & 0xFF);
        static byte L(byte x, byte y, float t) => (byte)Math.Clamp((int)MathF.Round(x + (y - x) * t), 0, 255);
        return Pack(L(C(a, 0), C(b, 0), t), L(C(a, 8), C(b, 8), t), L(C(a, 16), C(b, 16), t), L(C(a, 24), C(b, 24), t));
    }

    private void DrawWorld(ActivePrediction p)
    {
        var cam = Camera.Instance;
        if (cam == null) return;
        var actor = _ws.Actors.Find(p.CasterID);
        var y = actor?.PosRot.Y ?? 0;
        if (!float.IsFinite(y)) y = 0;
        var o = new Vector3(p.Origin.X, y + .05f, p.Origin.Y);
        var color = ConfidenceColor(p.Confidence);
        var thickness = ConfidenceThickness(p.Confidence);
        switch (p.Geometry)
        {
            case GeometryKind.Circle:
                cam.DrawWorldCircle(o, p.P1, color, thickness);
                break;
            case GeometryKind.Donut:
                cam.DrawWorldCircle(o, p.P1, color, thickness);
                cam.DrawWorldCircle(o, p.P2, color, thickness);
                break;
            case GeometryKind.Cone:
                DrawCone(cam, o, p.Rotation, p.P1, p.P2, color, thickness);
                break;
            case GeometryKind.Rectangle:
                DrawRect(cam, o, p.Rotation, p.P1, p.P2, color, thickness);
                break;
            case GeometryKind.Cross:
                DrawRect(cam, o, p.Rotation, p.P1, p.P2, color, thickness);
                DrawRect(cam, o, p.Rotation + MathF.PI, p.P1, p.P2, color, thickness);
                DrawRect(cam, o, p.Rotation + MathF.PI * .5f, p.P1, p.P2, color, thickness);
                DrawRect(cam, o, p.Rotation - MathF.PI * .5f, p.P1, p.P2, color, thickness);
                break;
        }
        DrawWorldGuidance(cam, p, o, color, thickness);
    }

    private void DrawWorldGuidance(Camera cam, ActivePrediction p, Vector3 origin, uint color, float thickness)
    {
        var targetActor = p.TargetID != 0 ? _ws.Actors.Find(p.TargetID) : null;
        var target = targetActor != null
            ? new Vector3(targetActor.Position.X, targetActor.PosRot.Y + .08f, targetActor.Position.Z)
            : new Vector3(p.Target.X, origin.Y, p.Target.Y);
        switch (p.Guidance)
        {
            case GuidanceKind.Stack:
                cam.DrawWorldCircle(target, Math.Max(3, p.P1), color, thickness);
                break;
            case GuidanceKind.Spread:
                cam.DrawWorldCircle(target, Math.Max(5, p.P1), color, thickness);
                break;
            case GuidanceKind.Soak:
                cam.DrawWorldCircle(origin, Math.Max(2.5f, p.P1), color, thickness);
                break;
            case GuidanceKind.Tether when p.CasterID != 0 && (p.TargetID != 0 || p.Target != p.Origin):
                cam.DrawWorldLine(origin, target, color, thickness);
                break;
            case GuidanceKind.LookAway:
                cam.DrawWorldCircle(origin, 2, color, thickness);
                cam.DrawWorldLine(origin + new Vector3(-1.5f, 0, -1.5f), origin + new Vector3(1.5f, 0, 1.5f), color, thickness);
                cam.DrawWorldLine(origin + new Vector3(-1.5f, 0, 1.5f), origin + new Vector3(1.5f, 0, -1.5f), color, thickness);
                break;
            case GuidanceKind.Knockback:
                var player = _ws.Party[PartyState.PlayerSlot];
                if (player != null)
                    cam.DrawWorldLine(origin, new(player.Position.X, player.PosRot.Y + .08f, player.Position.Z), color, thickness);
                break;
        }
    }

    private static void DrawCone(Camera cam, Vector3 o, float rot, float range, float half, uint color, float thickness)
    {
        const int n = 32;
        var prev = o;
        for (var i = 0; i <= n; ++i)
        {
            var a = rot - half + 2 * half * i / n;
            var q = o + new Vector3(MathF.Sin(a) * range, 0, MathF.Cos(a) * range);
            if (i == 0) cam.DrawWorldLine(o, q, color, thickness);
            else cam.DrawWorldLine(prev, q, color, thickness);
            if (i == n) cam.DrawWorldLine(q, o, color, thickness);
            prev = q;
        }
    }

    private static void DrawRect(Camera cam, Vector3 o, float rot, float length, float halfWidth, uint color, float thickness)
    {
        var f = new Vector3(MathF.Sin(rot), 0, MathF.Cos(rot));
        var r = new Vector3(MathF.Cos(rot), 0, -MathF.Sin(rot));
        var a = o + r * halfWidth;
        var b = o - r * halfWidth;
        var c = b + f * length;
        var d = a + f * length;
        cam.DrawWorldLine(a, b, color, thickness);
        cam.DrawWorldLine(b, c, color, thickness);
        cam.DrawWorldLine(c, d, color, thickness);
        cam.DrawWorldLine(d, a, color, thickness);
    }

    private void DrawTextHints()
    {
        var draw = ImGui.GetForegroundDrawList();
        var viewport = Camera.Instance?.ViewportSize ?? new Vector2(1920, 1080);
        if (!FiniteViewport(viewport)) viewport = new(1920, 1080);
        var y = viewport.Y * .18f;
        var active = _predictions.Values.Where(p => ValidPrediction(p) && p.Confidence >= _cfg.VisualConfidence / 100f)
            .OrderBy(p => p.Activation).FirstOrDefault();
        if (!string.IsNullOrEmpty(active.SignalKey) || active.ActionID != 0 || active.Guidance != GuidanceKind.None)
        {
            var remain = Math.Max(0, (active.Activation - _ws.CurrentTime).TotalSeconds);
            var actionName = LookupActionName(active.ActionID);
            var label = !string.IsNullOrWhiteSpace(active.Label) ? active.Label
                : !string.IsNullOrWhiteSpace(actionName) ? actionName
                : active.Kind != MechanicKind.Unknown ? active.Kind.ToString() : "Learned signal";
            draw.AddText(new(viewport.X * .5f - 190, y), ConfidenceColor(active.Confidence),
                $"FORETELL  {label}  {remain:F1}s  verified {active.Confidence:P0}{(active.Anticipated ? " · anticipated" : "")}");
            y += 22;
        }
        var next = PredictNextContextual();
        if (next != null)
        {
            var encounter = _store.Encounters.GetValueOrDefault(_territory);
            var nextName = encounter == null ? next.To : SignalDisplayName(encounter, next.To);
            draw.AddText(new(viewport.X * .5f - 190, y), 0xFFE0E0E0u, $"Likely next: {nextName}  ~{next.MeanDelay:F1}s  ({next.Count}x, {next.Stability:P0} stable)");
        }
        else
        {
            var legacyNext = PredictNext();
            if (legacyNext != null)
                draw.AddText(new(viewport.X * .5f - 190, y), 0xFFE0E0E0u, $"Likely next: {LookupActionName(legacyNext.To) ?? $"Action 0x{legacyNext.To:X}"}  ~{legacyNext.MeanDelay:F1}s  ({legacyNext.Count}x)");
        }
        var contextualCount = _store.Encounters.GetValueOrDefault(_territory)?.Mechanics.Count ?? 0;
        draw.AddText(new(20, viewport.Y - 35), 0xFFB0B0B0u, $"Foretell · {EncounterName(_territory)} · {contextualCount} mechanics learned · {_session.Observations:N0} observations");
    }

    private TimelineEdge? PredictNext()
    {
        if (_previousAction == 0) return null;
        return _store.Timeline.Values.Where(e => e.From == _previousAction && e.Count >= 2).OrderByDescending(e => e.Count).FirstOrDefault();
    }

    private SignalTimelineEdge? PredictNextContextual()
    {
        if (string.IsNullOrEmpty(_previousSignal) || !_store.Encounters.TryGetValue(_territory, out var encounter)) return null;
        return encounter.Timeline.Values.Where(e => e.Phase == _session.Phase && e.From == _previousSignal && e.Count >= 2)
            .OrderByDescending(e => e.Stability).ThenByDescending(e => e.Count).FirstOrDefault();
    }

    private void DrawRadar()
    {
        var player = _ws.Party[PartyState.PlayerSlot];
        if (player == null)
            return;
        var cam = Camera.Instance;
        if (cam == null)
            return;

        var size = _cfg.RadarSize;
        var viewport = cam.ViewportSize;
        if (!FiniteViewport(viewport)) return;
        var playerPos = V(player.Position);
        if (!FiniteVector(playerPos)) return;
        var windowSize = new Vector2(size + 24, size + 62);
        var defaultPosition = new Vector2(Math.Max(8, viewport.X - windowSize.X - 22), 22);
        var savedPosition = _cfg.RadarPositionX >= 0 && _cfg.RadarPositionY >= 0
            ? new Vector2(_cfg.RadarPositionX * viewport.X, _cfg.RadarPositionY * viewport.Y)
            : defaultPosition;

        // Locked placement is applied every frame; unlocked placement is only seeded on transition so ImGui can
        // move the window normally. Position is stored normalized to survive resolution/viewport changes.
        if (!_cfg.RadarUnlocked || !_radarWasUnlocked)
            ImGui.SetNextWindowPos(savedPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(windowSize, ImGuiCond.Always);

        var flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings;
        if (!_cfg.RadarUnlocked)
            flags |= ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoBackground
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing;

        if (!ImGui.Begin("Foretell radar - drag to move###ForetellRadarWindow", flags))
        {
            ImGui.End();
            _radarWasUnlocked = _cfg.RadarUnlocked;
            return;
        }

        try
        {
            if (_cfg.RadarUnlocked)
            {
                var position = ImGui.GetWindowPos();
                var normalizedX = Math.Clamp(position.X / Math.Max(1, viewport.X), 0, 1);
                var normalizedY = Math.Clamp(position.Y / Math.Max(1, viewport.Y), 0, 1);
                if (Math.Abs(normalizedX - _cfg.RadarPositionX) > .0001f || Math.Abs(normalizedY - _cfg.RadarPositionY) > .0001f)
                {
                    _cfg.RadarPositionX = normalizedX;
                    _cfg.RadarPositionY = normalizedY;
                    _radarPositionDirty = true;
                }
                if (_radarPositionDirty && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    _cfg.Modified.Fire();
                    _radarPositionDirty = false;
                }
            }
            _radarWasUnlocked = _cfg.RadarUnlocked;

        var canvas = ImGui.GetCursorScreenPos();
        var center = canvas + new Vector2(size * .5f + 4, size * .5f + 22);
        var radius = size * .5f;
        var draw = ImGui.GetWindowDrawList();
        var topologyAvailable = _cfg.RadarShape == ForetellRadarShape.Auto && _topologyAnalysis is { PassableCells: > 0, UnknownCells: 0 };
        var shape = _cfg.RadarShape == ForetellRadarShape.Auto ? ForetellRadarShape.Circle : _cfg.RadarShape;
        var shapeLabel = _cfg.RadarShape == ForetellRadarShape.Auto
            ? topologyAvailable ? "auto / collision topology" : "auto / scanning"
            : shape.ToString().ToLowerInvariant();
        draw.AddText(canvas + new Vector2(4, 1), 0xFFE0E0E0u,
            _cfg.RadarUnlocked ? "Unlocked - drag the title bar, then lock in Settings" : $"Foretell radar · {shapeLabel} · {_cfg.RadarWorldRadius:F0}y");
        if (topologyAvailable)
            DrawTopologyRadarFrame(draw, center, radius, playerPos, radius / MathF.Max(1, _cfg.RadarWorldRadius));
        else
            DrawRadarFrame(draw, center, radius, shape);
        draw.AddCircleFilled(center, 4, 0xFFFFFFFFu);

        var scale = radius / MathF.Max(1, _cfg.RadarWorldRadius);
        var displayed = 0;
        foreach (var p in _predictions.Values.Where(ValidPrediction).OrderBy(p => p.Activation))
        {
            if (p.Confidence < _cfg.VisualConfidence / 100f || displayed++ >= _cfg.MaxRenderedMechanics)
                continue;
            var col = ConfidenceColor(p.Confidence);
            var thickness = ConfidenceThickness(p.Confidence);
            DrawRadarGeometry(draw, p, playerPos, center, scale, col, thickness);
            DrawRadarGuidance(draw, p, playerPos, center, scale, col, thickness);
            var c = RadarPoint(p.Origin, playerPos, center, scale);
            draw.AddText(c + new Vector2(5, -9), col, $"{p.Confidence:P0}");
        }

        var legendY = center.Y + radius + 4;
        draw.AddText(new(center.X - radius, legendY), ConfidenceColor(_cfg.VisualConfidence / 100f), $"{_cfg.VisualConfidence:F0}% learn");
        draw.AddText(new(center.X - 22, legendY), ConfidenceColor(_cfg.WarningConfidence / 100f), $"{_cfg.WarningConfidence:F0}% high");
        draw.AddText(new(center.X + radius - 58, legendY), ConfidenceColor(_cfg.SafeConfidence / 100f), $"{_cfg.SafeConfidence:F0}% safe");
        }
        finally
        {
            ImGui.End();
        }
    }

    private static void DrawRadarFrame(ImDrawListPtr draw, Vector2 center, float radius, ForetellRadarShape shape)
    {
        var min = center - new Vector2(radius, radius);
        var max = center + new Vector2(radius, radius);
        if (shape == ForetellRadarShape.Square)
        {
            draw.AddRectFilled(min, max, Pack(12, 14, 20, 150));
            draw.AddRect(min, max, 0xAAE0E0E0u, 0, ImDrawFlags.None, 1.5f);
            draw.AddLine(new(center.X - radius, center.Y), new(center.X + radius, center.Y), 0x354F5560u);
            draw.AddLine(new(center.X, center.Y - radius), new(center.X, center.Y + radius), 0x354F5560u);
            draw.AddRect(center - new Vector2(radius * .5f), center + new Vector2(radius * .5f), 0x354F5560u);
        }
        else
        {
            draw.AddCircleFilled(center, radius, Pack(12, 14, 20, 150), 64);
            draw.AddCircle(center, radius, 0xAAE0E0E0u, 64, 1.5f);
            draw.AddCircle(center, radius * .5f, 0x354F5560u, 48, 1f);
            draw.AddLine(new(center.X - radius, center.Y), new(center.X + radius, center.Y), 0x354F5560u);
            draw.AddLine(new(center.X, center.Y - radius), new(center.X, center.Y + radius), 0x354F5560u);
        }
    }

    private void DrawTopologyRadarFrame(ImDrawListPtr draw, Vector2 center, float radius, Vector2 player, float scale)
    {
        var min = center - new Vector2(radius);
        var max = center + new Vector2(radius);
        draw.AddRectFilled(min, max, Pack(12, 14, 20, 150), 8);
        draw.PushClipRect(min, max, true);
        try
        {
            if (_topologyAnalysis is { } topology)
            {
                foreach (var loop in topology.Contours)
                {
                    if (loop.Count < 2) continue;
                    for (var i = 0; i < loop.Count; ++i)
                    {
                        var a = RadarPoint(loop[i], player, center, scale);
                        var b = RadarPoint(loop[(i + 1) % loop.Count], player, center, scale);
                        draw.AddLine(a, b, Pack(80, 220, 175, 230), 2f);
                    }
                }
            }
        }
        finally { draw.PopClipRect(); }
        draw.AddRect(min, max, Pack(80, 220, 175, 180), 8, ImDrawFlags.None, 1.5f);
        draw.AddLine(new(center.X - radius, center.Y), new(center.X + radius, center.Y), 0x354F5560u);
        draw.AddLine(new(center.X, center.Y - radius), new(center.X, center.Y + radius), 0x354F5560u);
    }

    private static Vector2 RadarPoint(Vector2 world, Vector2 player, Vector2 center, float scale)
        => center + (world - player) * scale;

    private static void DrawRadarGeometry(ImDrawListPtr draw, ActivePrediction p, Vector2 player, Vector2 center, float scale, uint color, float thickness)
    {
        var o = RadarPoint(p.Origin, player, center, scale);
        switch (p.Geometry)
        {
            case GeometryKind.Circle:
                draw.AddCircle(o, p.P1 * scale, color, 48, thickness);
                break;
            case GeometryKind.Donut:
                draw.AddCircle(o, p.P1 * scale, color, 48, thickness);
                draw.AddCircle(o, p.P2 * scale, color, 48, thickness);
                break;
            case GeometryKind.Cone:
                DrawRadarCone(draw, p, player, center, scale, color, thickness);
                break;
            case GeometryKind.Rectangle:
                DrawRadarRect(draw, p.Origin, p.Rotation, p.P1, p.P2, player, center, scale, color, thickness);
                break;
            case GeometryKind.Cross:
                DrawRadarRect(draw, p.Origin, p.Rotation, p.P1, p.P2, player, center, scale, color, thickness);
                DrawRadarRect(draw, p.Origin, p.Rotation + MathF.PI, p.P1, p.P2, player, center, scale, color, thickness);
                DrawRadarRect(draw, p.Origin, p.Rotation + MathF.PI * .5f, p.P1, p.P2, player, center, scale, color, thickness);
                DrawRadarRect(draw, p.Origin, p.Rotation - MathF.PI * .5f, p.P1, p.P2, player, center, scale, color, thickness);
                break;
        }
    }

    private void DrawRadarGuidance(ImDrawListPtr draw, ActivePrediction p, Vector2 player, Vector2 center, float scale, uint color, float thickness)
    {
        var targetActor = p.TargetID != 0 ? _ws.Actors.Find(p.TargetID) : null;
        var targetWorld = targetActor != null ? V(targetActor.Position) : p.Target;
        var target = RadarPoint(targetWorld, player, center, scale);
        var origin = RadarPoint(p.Origin, player, center, scale);
        switch (p.Guidance)
        {
            case GuidanceKind.Stack:
                draw.AddCircle(target, Math.Max(3, p.P1) * scale, color, 32, thickness);
                draw.AddText(target + new Vector2(5, 5), color, "STACK");
                break;
            case GuidanceKind.Spread:
                draw.AddCircle(target, Math.Max(5, p.P1) * scale, color, 32, thickness);
                draw.AddText(target + new Vector2(5, 5), color, "SPREAD");
                break;
            case GuidanceKind.Soak:
                draw.AddCircle(origin, Math.Max(2.5f, p.P1) * scale, color, 32, thickness);
                draw.AddText(origin + new Vector2(5, 5), color, "SOAK");
                break;
            case GuidanceKind.Tether:
                draw.AddLine(origin, target, color, thickness);
                draw.AddText((origin + target) * .5f, color, "TETHER");
                break;
            case GuidanceKind.LookAway:
                draw.AddCircle(origin, 8, color, 20, thickness);
                draw.AddLine(origin - new Vector2(6), origin + new Vector2(6), color, thickness);
                draw.AddLine(origin + new Vector2(-6, 6), origin + new Vector2(6, -6), color, thickness);
                break;
            case GuidanceKind.Knockback:
                draw.AddLine(origin, center, color, thickness);
                draw.AddText((origin + center) * .5f, color, "KNOCKBACK");
                break;
            case GuidanceKind.Raidwide:
                draw.AddText(center + new Vector2(-35, -18), color, "RAIDWIDE");
                break;
            case GuidanceKind.Cleanse:
                draw.AddText(center + new Vector2(-25, -18), color, "CLEANSE");
                break;
            case GuidanceKind.Move:
                draw.AddText(center + new Vector2(-22, -18), color, "MOVE");
                break;
        }
    }

    private static void DrawRadarCone(ImDrawListPtr draw, ActivePrediction p, Vector2 player, Vector2 center, float scale, uint color, float thickness)
    {
        const int n = 24;
        var o = RadarPoint(p.Origin, player, center, scale);
        Vector2 prev = default;
        for (var i = 0; i <= n; ++i)
        {
            var a = p.Rotation - p.P2 + 2 * p.P2 * i / n;
            var world = p.Origin + new Vector2(MathF.Sin(a), MathF.Cos(a)) * p.P1;
            var q = RadarPoint(world, player, center, scale);
            if (i == 0) draw.AddLine(o, q, color, thickness);
            else draw.AddLine(prev, q, color, thickness);
            if (i == n) draw.AddLine(q, o, color, thickness);
            prev = q;
        }
    }

    private static void DrawRadarRect(ImDrawListPtr draw, Vector2 origin, float rot, float length, float halfWidth,
        Vector2 player, Vector2 center, float scale, uint color, float thickness)
    {
        var f = new Vector2(MathF.Sin(rot), MathF.Cos(rot));
        var r = new Vector2(MathF.Cos(rot), -MathF.Sin(rot));
        var a = RadarPoint(origin + r * halfWidth, player, center, scale);
        var b = RadarPoint(origin - r * halfWidth, player, center, scale);
        var c = RadarPoint(origin - r * halfWidth + f * length, player, center, scale);
        var d = RadarPoint(origin + r * halfWidth + f * length, player, center, scale);
        draw.AddLine(a, b, color, thickness);
        draw.AddLine(b, c, color, thickness);
        draw.AddLine(c, d, color, thickness);
        draw.AddLine(d, a, color, thickness);
    }

    private void DrawSafeSuggestion()
    {
        var player = _ws.Party[PartyState.PlayerSlot];
        if (player == null) return;
        var dangers = _predictions.Values.Where(p => ValidPrediction(p) && (p.Guidance is GuidanceKind.Avoid or GuidanceKind.None)
            && p.Geometry != GeometryKind.Unknown && p.Confidence >= _cfg.SafeConfidence / 100f).ToArray();
        if (dangers.Length == 0) return;
        var pp = V(player.Position);
        if (!FiniteVector(pp)) return;
        bool Unsafe(Vector2 q) => dangers.Any(p => Contains(p, q));
        if (!Unsafe(pp)) return;
        Vector2? best = null;
        var bestD = float.MaxValue;
        for (var ring = 2f; ring <= 25f; ring += 2f)
            for (var i = 0; i < 48; ++i)
            {
                var a = MathF.Tau * i / 48;
                var q = pp + new Vector2(MathF.Sin(a), MathF.Cos(a)) * ring;
                var topology = IsTopologyPassable(q);
                if (!Unsafe(q) && topology != false && ring < bestD) { best = q; bestD = ring; }
            }
        if (best is not Vector2 b) return;
        var cam = Camera.Instance;
        if (cam == null) return;
        var y = player.PosRot.Y + .1f;
        if (!float.IsFinite(y)) return;
        cam.DrawWorldLine(new(pp.X, y, pp.Y), new(b.X, y, b.Y), 0xFF40FF40u, 4f);
        cam.DrawWorldCircle(new(b.X, y, b.Y), 1f, 0xFF40FF40u, 3f);
    }

    private static bool ValidPrediction(ActivePrediction prediction)
        => float.IsFinite(prediction.Origin.X) && float.IsFinite(prediction.Origin.Y)
            && float.IsFinite(prediction.Target.X) && float.IsFinite(prediction.Target.Y)
            && float.IsFinite(prediction.Rotation) && float.IsFinite(prediction.P1) && prediction.P1 is >= 0 and <= 200
            && float.IsFinite(prediction.P2) && prediction.P2 is >= 0 and <= 200
            && float.IsFinite(prediction.Confidence) && prediction.Confidence is >= 0 and <= 1;

    private static bool FiniteVector(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool FiniteViewport(Vector2 value)
        => FiniteVector(value) && value.X is >= 64 and <= 32768 && value.Y is >= 64 and <= 32768;
}
