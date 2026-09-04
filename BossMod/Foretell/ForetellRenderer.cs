using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private bool _radarWasUnlocked;
    private bool _radarPositionDirty;
    private float _radarDisplayedWorldRadius;
    private DateTime _radarZoomUpdatedAt;
    private bool _textWasUnlocked;
    private bool _textPositionDirty;
    private long _drawFailures;
    private DateTime _lastDrawFailureLog;

    public void Draw()
    {
        DrawSafely(DrawInspector, "inspector");
        if (_cfg.MiniRadar)
            DrawSafely(() => DrawRadar(_cfg.Mode is ForetellMode.Hybrid or ForetellMode.Foretell), "radar");
        if (_cfg.Mode is ForetellMode.Legacy or ForetellMode.Observe) return;
        DrawSafely(() =>
        {
            if (_cfg.WorldOverlay) DrawDynamicTerrainWorld();
            var confidenceCut = _cfg.VisualConfidence / 100f;
            var displayed = 0;
            foreach (var p in _predictions.Values.Where(p => ValidPrediction(p) && HasSpatialPresentation(p)).OrderBy(p => p.Activation))
            {
                if (p.Confidence < confidenceCut || displayed++ >= _cfg.MaxRenderedMechanics) continue;
                if (_cfg.WorldOverlay) DrawWorld(p);
            }
        }, "world overlay");
        if (_cfg.TextHints) DrawSafely(DrawTextHints, "text hints");
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
                DrawWorldCircleClipped(cam, o, p.P1, color, thickness);
                break;
            case GeometryKind.Donut:
                DrawWorldCircleClipped(cam, o, p.P1, color, thickness);
                DrawWorldCircleClipped(cam, o, p.P2, color, thickness);
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
            case GuidanceKind.Marker:
                const float markerHalfSize = 1.25f;
                cam.DrawWorldLine(target + new Vector3(-markerHalfSize, 0, 0), target + new Vector3(markerHalfSize, 0, 0), color, thickness);
                cam.DrawWorldLine(target + new Vector3(0, 0, -markerHalfSize), target + new Vector3(0, 0, markerHalfSize), color, thickness);
                break;
        }
    }

    private void DrawCone(Camera cam, Vector3 o, float rot, float range, float half, uint color, float thickness)
    {
        const int n = 32;
        var prev = o;
        for (var i = 0; i <= n; ++i)
        {
            var a = rot - half + 2 * half * i / n;
            var q = o + new Vector3(MathF.Sin(a) * range, 0, MathF.Cos(a) * range);
            if (i == 0) DrawWorldLineClipped(cam, o, q, color, thickness);
            else DrawWorldLineClipped(cam, prev, q, color, thickness);
            if (i == n) DrawWorldLineClipped(cam, q, o, color, thickness);
            prev = q;
        }
    }

    private void DrawRect(Camera cam, Vector3 o, float rot, float length, float halfWidth, uint color, float thickness)
    {
        var f = new Vector3(MathF.Sin(rot), 0, MathF.Cos(rot));
        var r = new Vector3(MathF.Cos(rot), 0, -MathF.Sin(rot));
        var a = o + r * halfWidth;
        var b = o - r * halfWidth;
        var c = b + f * length;
        var d = a + f * length;
        DrawWorldLineClipped(cam, a, b, color, thickness);
        DrawWorldLineClipped(cam, b, c, color, thickness);
        DrawWorldLineClipped(cam, c, d, color, thickness);
        DrawWorldLineClipped(cam, d, a, color, thickness);
    }

    private void DrawWorldCircleClipped(Camera camera, Vector3 origin, float radius, uint color, float thickness)
    {
        const int segments = 48;
        var previous = origin + new Vector3(0, 0, radius);
        for (var i = 1; i <= segments; ++i)
        {
            var angle = MathF.Tau * i / segments;
            var current = origin + new Vector3(MathF.Sin(angle) * radius, 0, MathF.Cos(angle) * radius);
            DrawWorldLineClipped(camera, previous, current, color, thickness);
            previous = current;
        }
    }

    private void DrawWorldLineClipped(Camera camera, Vector3 from, Vector3 to, uint color, float thickness)
    {
        var horizontal = new Vector2(to.X - from.X, to.Z - from.Z).Length();
        var steps = Math.Clamp((int)MathF.Ceiling(horizontal / 2.5f), 1, 96);
        var previous = from;
        for (var step = 1; step <= steps; ++step)
        {
            var current = Vector3.Lerp(from, to, step / (float)steps);
            // Walkability cannot establish attack occlusion. Preserve the complete danger outline.
            camera.DrawWorldLine(ProjectWorldAlertToTopology(previous), ProjectWorldAlertToTopology(current), color, thickness);
            previous = current;
        }
    }

    private Vector3 ProjectWorldAlertToTopology(Vector3 point)
        => _topology.TryConnectedHeight(new(point.X, point.Z), _topologyAnalysis?.ConnectedCells, out var height)
            ? new(point.X, height + .05f, point.Z)
            : point;

    private void DrawTextHints()
    {
        var mainViewport = ImGui.GetMainViewport();
        var viewportOrigin = mainViewport.Pos;
        var viewport = mainViewport.Size;
        if (!FiniteViewport(viewport)) { viewportOrigin = default; viewport = new(1920, 1080); }
        var defaultPosition = viewportOrigin + new Vector2(viewport.X * .5f - 210, viewport.Y * .12f);
        var savedPosition = _cfg.TextPositionX >= 0 && _cfg.TextPositionY >= 0
            ? viewportOrigin + new Vector2(_cfg.TextPositionX * viewport.X, _cfg.TextPositionY * viewport.Y)
            : defaultPosition;
        savedPosition.X = Math.Clamp(savedPosition.X, viewportOrigin.X, viewportOrigin.X + Math.Max(0, viewport.X - 260));
        savedPosition.Y = Math.Clamp(savedPosition.Y, viewportOrigin.Y, viewportOrigin.Y + Math.Max(0, viewport.Y - 80));
        if (!_cfg.TextHintsUnlocked || !_textWasUnlocked)
            ImGui.SetNextWindowPos(savedPosition, ImGuiCond.Always);

        var flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (!_cfg.TextHintsUnlocked)
            flags |= ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoBackground
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing;
        if (!ImGui.Begin("Foretell guidance - drag to move###ForetellTextHintsWindow", flags))
        {
            ImGui.End();
            _textWasUnlocked = _cfg.TextHintsUnlocked;
            return;
        }

        try
        {
            if (_cfg.TextHintsUnlocked)
            {
                var position = ImGui.GetWindowPos();
                var normalizedX = Math.Clamp((position.X - viewportOrigin.X) / Math.Max(1, viewport.X), 0, 1);
                var normalizedY = Math.Clamp((position.Y - viewportOrigin.Y) / Math.Max(1, viewport.Y), 0, 1);
                if (Math.Abs(normalizedX - _cfg.TextPositionX) > .0001f || Math.Abs(normalizedY - _cfg.TextPositionY) > .0001f)
                {
                    _cfg.TextPositionX = normalizedX;
                    _cfg.TextPositionY = normalizedY;
                    _textPositionDirty = true;
                }
                if (_textPositionDirty && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    _cfg.Modified.Fire();
                    _textPositionDirty = false;
                }
            }
            _textWasUnlocked = _cfg.TextHintsUnlocked;

            var active = _predictions.Values.Where(p => ValidPrediction(p) && p.Confidence >= _cfg.VisualConfidence / 100f)
                .OrderBy(p => p.Activation).Take(Math.Min(3, _cfg.MaxRenderedMechanics)).ToArray();
            var terrainCue = _ws.Party[PartyState.PlayerSlot] is { } localPlayer
                && ActiveDynamicTerrainWarnings().Any(w => ForetellArenaBoundaryCore.Contains(w.Points, V(localPlayer.Position)));
            var hasActive = active.Length != 0 || terrainCue;
            for (var i = 0; i < active.Length; ++i)
            {
                var prediction = active[i];
                if (i != 0) ImGui.Separator();
                var remain = Math.Max(0, (prediction.Activation - _ws.CurrentTime).TotalSeconds);
                ImGui.TextColored(ConfidenceTextColor(prediction.Confidence), $"{GuidanceInstruction(prediction.Guidance, prediction.Kind, prediction.Geometry)} — {UserFacingPredictionLabel(prediction)}");
                ImGui.TextDisabled($"{remain:F1}s · confidence {prediction.Confidence:P0}{(prediction.Anticipated ? " · predicted ahead" : "")}");
            }
            if (terrainCue)
                ImGui.TextColored(new Vector4(.28f, .75f, 1, 1), "WATCH TERRAIN — possible floor change");
            var next = PredictNextContextual();
            if (next != null)
            {
                var encounter = _store.Encounters.GetValueOrDefault(_territory);
                if (encounter?.Mechanics.GetValueOrDefault(next.To) is { } mechanic)
                {
                    if (hasActive) ImGui.Separator();
                    ImGui.TextUnformatted($"NEXT — {UserFacingMechanicLabel(mechanic)}");
                    ImGui.TextDisabled($"about {next.MeanDelay:F1}s · learned {next.Count}× · timing {next.Stability:P0}");
                }
            }
            else if (!hasActive)
            {
                var legacyNext = PredictNext();
                var legacyName = legacyNext == null ? null : LookupActionName(legacyNext.To);
                if (legacyNext != null && !string.IsNullOrWhiteSpace(legacyName))
                {
                    ImGui.TextUnformatted($"NEXT — {legacyName}");
                    ImGui.TextDisabled($"about {legacyNext.MeanDelay:F1}s · learned {legacyNext.Count}×");
                }
                else
                {
                    var contextualCount = _store.Encounters.GetValueOrDefault(_territory)?.Mechanics.Count ?? 0;
                    ImGui.TextDisabled($"Foretell is learning · {contextualCount} candidates · no verified guidance yet");
                }
            }
        }
        finally { ImGui.End(); }
    }

    private Vector4 ConfidenceTextColor(float confidence)
    {
        var packed = ConfidenceColor(confidence);
        return new((packed & 0xFF) / 255f, ((packed >> 8) & 0xFF) / 255f, ((packed >> 16) & 0xFF) / 255f, 1);
    }

    private static string GuidanceInstruction(GuidanceKind guidance, MechanicKind kind, GeometryKind geometry) => guidance switch
    {
        GuidanceKind.Avoid => "AVOID",
        GuidanceKind.Stack => "STACK",
        GuidanceKind.Spread => "SPREAD",
        GuidanceKind.Soak => "SOAK TOWER",
        GuidanceKind.LookAway => "LOOK AWAY",
        GuidanceKind.Knockback => "KNOCKBACK",
        GuidanceKind.Tether => "CHECK TETHER",
        GuidanceKind.Raidwide => "RAIDWIDE",
        GuidanceKind.Tankbuster => "TANKBUSTER",
        GuidanceKind.Cleanse => "CLEANSE",
        GuidanceKind.Move => "MOVE",
        GuidanceKind.Marker => "MARKER",
        _ when geometry != GeometryKind.Unknown || kind is MechanicKind.GroundAOE or MechanicKind.TargetedAOE => "AVOID",
        _ => "WATCH"
    };

    private static string FriendlyMechanicLabel(MechanicKind kind, GeometryKind geometry) => kind switch
    {
        MechanicKind.GroundAOE => geometry == GeometryKind.Unknown ? "area attack" : $"{geometry.ToString().ToLowerInvariant()} area",
        MechanicKind.TargetedAOE => "targeted area attack",
        MechanicKind.Stack => "stack marker",
        MechanicKind.LineStack => "line stack",
        MechanicKind.Spread => "spread markers",
        MechanicKind.Tower => "tower",
        MechanicKind.Gaze => "gaze attack",
        MechanicKind.Knockback => "knockback",
        MechanicKind.ForcedMovement => "forced movement",
        MechanicKind.Tether => "tether mechanic",
        MechanicKind.Raidwide => "raid-wide damage",
        MechanicKind.Tankbuster => "tankbuster",
        MechanicKind.Debuff => "debuff",
        MechanicKind.Proximity => "proximity damage",
        MechanicKind.Environment => "arena change",
        MechanicKind.Transition => "phase transition",
        MechanicKind.Marker => "target marker",
        _ when geometry != GeometryKind.Unknown => $"{geometry.ToString().ToLowerInvariant()} area",
        _ => "learned mechanic"
    };

    private static string UserFacingMechanicLabel(ContextualMechanic mechanic)
    {
        if (mechanic.TriggerKind is ObservationKind.CastStart or ObservationKind.CastFinish or ObservationKind.ActionResolved or ObservationKind.AffectedTarget)
        {
            var actionName = LookupActionName(mechanic.TriggerID);
            if (!string.IsNullOrWhiteSpace(actionName)) return actionName;
        }
        return FriendlyMechanicLabel(mechanic.Kind, mechanic.Geometry);
    }

    private static string UserFacingPredictionLabel(ActivePrediction prediction)
    {
        var actionName = LookupActionName(prediction.ActionID);
        return !string.IsNullOrWhiteSpace(actionName) ? actionName : FriendlyMechanicLabel(prediction.Kind, prediction.Geometry);
    }

    private TimelineEdge? PredictNext()
    {
        if (_previousAction == 0) return null;
        return _store.Timeline.Values.Where(e => e.From == _previousAction && e.Count >= 2).OrderByDescending(e => e.Count).FirstOrDefault();
    }

    private SignalTimelineEdge? PredictNextContextual()
    {
        if (string.IsNullOrEmpty(_previousSignal) || !_store.Encounters.TryGetValue(_territory, out var encounter)) return null;
        var outgoing = encounter.Timeline.Values.Where(e => e.Phase == CurrentTimelinePhase && e.From == _previousSignal && e.Count >= 3
            && e.MeanDelay is >= .15 and <= 120 && e.Stability >= .45f && encounter.Mechanics.TryGetValue(e.To, out var mechanic)
            && (mechanic.Geometry != GeometryKind.Unknown || ForetellInferenceCore.GuidanceFor(mechanic.Kind) != GuidanceKind.None)).ToArray();
        return outgoing.Where(e => ForetellInferenceCore.TimelineProbability(e, outgoing) >= .55f)
            .OrderByDescending(e => ForetellInferenceCore.TimelineProbability(e, outgoing) * e.Stability)
            .ThenByDescending(e => e.Count).FirstOrDefault();
    }

    private void DrawRadar(bool showPredictions)
    {
        var player = _ws.Party[PartyState.PlayerSlot];
        if (player == null)
            return;
        var size = _cfg.RadarSize;
        var mainViewport = ImGui.GetMainViewport();
        var viewportOrigin = mainViewport.Pos;
        var viewport = mainViewport.Size;
        if (!FiniteViewport(viewport)) return;
        var playerPos = V(player.Position);
        if (!FiniteVector(playerPos)) return;
        var cameraAzimuth = float.IsFinite(_ws.Client.CameraAzimuth.Rad) ? _ws.Client.CameraAzimuth.Rad : 0;
        var windowSize = new Vector2(size + 24, size + ImGui.GetFontSize() * 3 + 48);
        var defaultPosition = viewportOrigin + new Vector2(Math.Max(8, viewport.X - windowSize.X - 22), 22);
        var savedPosition = _cfg.RadarPositionX >= 0 && _cfg.RadarPositionY >= 0
            ? viewportOrigin + new Vector2(_cfg.RadarPositionX * viewport.X, _cfg.RadarPositionY * viewport.Y)
            : defaultPosition;
        savedPosition.X = Math.Clamp(savedPosition.X, viewportOrigin.X, viewportOrigin.X + Math.Max(0, viewport.X - windowSize.X));
        savedPosition.Y = Math.Clamp(savedPosition.Y, viewportOrigin.Y, viewportOrigin.Y + Math.Max(0, viewport.Y - windowSize.Y));

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
                var normalizedX = Math.Clamp((position.X - viewportOrigin.X) / Math.Max(1, viewport.X), 0, 1);
                var normalizedY = Math.Clamp((position.Y - viewportOrigin.Y) / Math.Max(1, viewport.Y), 0, 1);
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
        var topologyAvailable = _cfg.RadarShape == ForetellRadarShape.Auto
            && _topologyAnalysis is { PassableCells: > 0 } topology
            && topology.ConnectedCells.Length == _topology.CellCount;
        var arenaBoundary = !topologyAvailable && _cfg.RadarShape == ForetellRadarShape.Auto ? CurrentArenaBoundary : null;
        var shape = _cfg.RadarShape == ForetellRadarShape.Auto ? ForetellRadarShape.Circle : _cfg.RadarShape;
        var shapeLabel = _cfg.RadarShape == ForetellRadarShape.Auto
            ? topologyAvailable ? HasFreshTopologyEvidence ? "terrain" : "refreshing" : arenaBoundary != null ? "boundary" : "surveying"
            : shape.ToString().ToLowerInvariant();
        var worldRadius = EffectiveRadarWorldRadius(playerPos);
        DrawRadarCaption(draw, canvas + new Vector2(4, 1), size, 0xFFE0E0E0u,
            $"Foretell · {shapeLabel} · {worldRadius:F0}y");
        var scale = radius / MathF.Max(1, worldRadius);
        if (arenaBoundary != null)
            DrawArenaBoundaryRadarFrame(draw, center, radius, playerPos, cameraAzimuth, scale, arenaBoundary);
        else if (topologyAvailable)
            DrawTopologyRadarFrame(draw, center, radius, playerPos, cameraAzimuth, scale);
        else
            DrawRadarFrame(draw, center, radius, shape);
        DrawRadarScale(draw, center, radius, scale);
        DrawRadarActors(draw, player, playerPos, cameraAzimuth, center, radius, scale);
        if (showPredictions)
            DrawDynamicTerrainRadar(draw, playerPos, cameraAzimuth, center, scale);

        if (showPredictions)
        {
            var displayed = 0;
            foreach (var p in _predictions.Values.Where(p => ValidPrediction(p) && HasSpatialPresentation(p)).OrderBy(p => p.Activation))
            {
                if (p.Confidence < _cfg.VisualConfidence / 100f || displayed++ >= _cfg.MaxRenderedMechanics)
                    continue;
                var col = ConfidenceColor(p.Confidence);
                var thickness = ConfidenceThickness(p.Confidence);
                DrawRadarGeometry(draw, p, playerPos, cameraAzimuth, center, scale, col, thickness);
                DrawRadarGuidance(draw, p, playerPos, cameraAzimuth, center, scale, col, thickness);
                var c = RadarPoint(p.Origin, playerPos, cameraAzimuth, center, scale);
                draw.AddText(c + new Vector2(5, -9), col, $"{p.Confidence:P0}");
            }
        }
        DrawRadarPlayer(draw, center);

        var legendY = center.Y + radius + 4;
        if (showPredictions)
        {
            var column = size / 3;
            DrawRadarCaption(draw, new(center.X - radius, legendY), column, ConfidenceColor(_cfg.VisualConfidence / 100f), $"{_cfg.VisualConfidence:F0}%");
            DrawRadarCaption(draw, new(center.X - radius + column, legendY), column, ConfidenceColor(_cfg.WarningConfidence / 100f), $"{_cfg.WarningConfidence:F0}%");
            DrawRadarCaption(draw, new(center.X - radius + 2 * column, legendY), column, ConfidenceColor(_cfg.SafeConfidence / 100f), $"{_cfg.SafeConfidence:F0}%");
            DrawRadarCaption(draw, new(center.X - radius, legendY + ImGui.GetFontSize()), size,
                Pack(165, 180, 195, 180), "prediction confidence");
        }
        else
            draw.AddText(new(center.X - radius, legendY), Pack(165, 180, 195, 180), "terrain only · guidance silent");
        }
        finally
        {
            ImGui.End();
        }
    }

    private static void DrawRadarCaption(ImDrawListPtr draw, Vector2 position, float width, uint color, string text)
    {
        var measured = ImGui.CalcTextSize(text).X;
        var fontSize = ImGui.GetFontSize() * Math.Min(1, Math.Max(1, width - 4) / Math.Max(1, measured));
        draw.AddText(ImGui.GetFont(), fontSize, position, color, text);
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

    private void DrawArenaBoundaryRadarFrame(ImDrawListPtr draw, Vector2 center, float radius, Vector2 player,
        float cameraAzimuth, float scale, ArenaBoundaryAnalysis boundary)
    {
        var min = center - new Vector2(radius);
        var max = center + new Vector2(radius);
        draw.AddRectFilled(min, max, Pack(8, 10, 16, 150), 8);
        draw.PushClipRect(min, max, true);
        try
        {
            var origin = RadarPoint(boundary.Origin, player, cameraAzimuth, center, scale);
            var color = RadarTerrainColor();
            var fill = RadarTerrainFillColor();
            for (var i = 0; i < boundary.Points.Count; ++i)
            {
                var a = RadarPoint(boundary.Points[i], player, cameraAzimuth, center, scale);
                var b = RadarPoint(boundary.Points[(i + 1) % boundary.Points.Count], player, cameraAzimuth, center, scale);
                if (_cfg.RadarTerrainStyle == ForetellRadarTerrainStyle.Filled)
                    draw.AddTriangleFilled(origin, a, b, fill);
                draw.AddLine(a, b, color, 2f);
            }
        }
        finally { draw.PopClipRect(); }
        draw.AddRect(min, max, Pack(110, 130, 150, 80), 8, ImDrawFlags.None, 1f);
    }

    private static void DrawRadarScale(ImDrawListPtr draw, Vector2 center, float radius, float scale)
    {
        var worldRadius = radius / Math.Max(.01f, scale);
        var step = worldRadius < 18 ? 5f : 10f;
        for (var distance = step; distance < worldRadius - 1; distance += step)
        {
            var screenRadius = distance * scale;
            draw.AddCircle(center, screenRadius, Pack(115, 135, 155, 65), 48, 1f);
            draw.AddText(center + new Vector2(3, -screenRadius + 2), Pack(165, 180, 195, 150), $"{distance:F0}y");
        }
        draw.AddText(center + new Vector2(-26, -radius + 4), Pack(210, 220, 235, 190), "↑ camera");
    }

    private void DrawRadarActors(ImDrawListPtr draw, Actor player, Vector2 playerPosition, float cameraAzimuth,
        Vector2 center, float radarRadius, float scale)
    {
        var worldRadius = radarRadius / Math.Max(.01f, scale);
        var maxDistanceSq = worldRadius * worldRadius;
        var boundary = CurrentArenaBoundary is { ArenaLike: true } learned ? learned : null;
        var arenaSummary = boundary != null ? ArenaEnemySummary(boundary) : default;

        var enemiesDrawn = 0;
        foreach (var actor in _ws.Actors)
        {
            if (enemiesDrawn >= 64 || actor.Type != ActorType.Enemy || actor.IsAlly || actor.IsDeadOrDestroyed || !actor.IsTargetable)
                continue;
            var world = V(actor.Position);
            if (!FiniteVector(world) || Vector2.DistanceSquared(world, playerPosition) > maxDistanceSq)
                continue;
            var position = RadarPoint(world, playerPosition, cameraAzimuth, center, scale);
            var boss = boundary != null && arenaSummary.HasBossCandidate && LiveArenaEnemy(actor, boundary)
                && IsBossCandidate(actor, arenaSummary.MaximumHP, arenaSummary.PlayerMaximumHP);
            var color = boss ? Pack(255, 190, 55, 235) : actor.CastInfo != null ? Pack(255, 95, 80, 235) : Pack(225, 80, 90, 215);
            var actorRadius = Math.Clamp(actor.HitboxRadius * scale, 2.5f, boss ? 12f : 9f);
            draw.AddCircleFilled(position, actorRadius, color, 20);
            draw.AddCircle(position, actorRadius + 1, Pack(10, 10, 15, 230), 20, 1.5f);
            if (actor.CastInfo != null)
            {
                draw.AddCircle(position, actorRadius + 3, Pack(255, 210, 90, 230), 24, 1.5f);
                if (boss)
                    draw.AddText(position + new Vector2(actorRadius + 5, -8), Pack(255, 210, 90, 230), "CAST");
            }
            ++enemiesDrawn;
        }

        for (var slot = 0; slot < PartyState.MaxAllies; ++slot)
        {
            var ally = _ws.Party[slot];
            if (ally == null || ally.InstanceID == player.InstanceID || ally.IsDeadOrDestroyed)
                continue;
            var world = V(ally.Position);
            if (!FiniteVector(world) || Vector2.DistanceSquared(world, playerPosition) > maxDistanceSq)
                continue;
            draw.AddCircleFilled(RadarPoint(world, playerPosition, cameraAzimuth, center, scale), 2.5f, Pack(75, 190, 245, 230), 16);
        }

        var companionID = _ws.Client.ActiveCompanion.InstanceID;
        if (companionID != 0 && _ws.Actors.Find(companionID) is { IsDeadOrDestroyed: false } companion)
        {
            var world = V(companion.Position);
            if (FiniteVector(world) && Vector2.DistanceSquared(world, playerPosition) <= maxDistanceSq)
                draw.AddCircleFilled(RadarPoint(world, playerPosition, cameraAzimuth, center, scale), 2.5f, Pack(95, 225, 135, 230), 16);
        }
    }

    private static void DrawRadarPlayer(ImDrawListPtr draw, Vector2 center)
    {
        draw.AddTriangleFilled(center + new Vector2(0, -7), center + new Vector2(-5, 5), center + new Vector2(5, 5), 0xFFFFFFFFu);
        draw.AddTriangle(center + new Vector2(0, -8), center + new Vector2(-6, 6), center + new Vector2(6, 6), Pack(10, 12, 18, 245), 2f);
    }

    private void DrawTopologyRadarFrame(ImDrawListPtr draw, Vector2 center, float radius, Vector2 player, float cameraAzimuth, float scale)
    {
        var min = center - new Vector2(radius);
        var max = center + new Vector2(radius);
        draw.AddCircleFilled(center, radius, Pack(8, 10, 16, 170), 64);
        draw.PushClipRect(min, max, true);
        try
        {
            if (_topologyAnalysis is { } topology && topology.ConnectedCells.Length == _topology.CellCount
                && topology.SampledCells.Length == _topology.CellCount && _topology.Width > 0 && _topology.Height > 0)
            {
                var worldRadius = radius / Math.Max(.01f, scale);
                var visibleRadiusSq = MathF.Max(0, worldRadius - _topology.Resolution * .75f);
                visibleRadiusSq *= visibleRadiusSq;
                var freshness = HasFreshTopologyEvidence ? 1f : .45f;
                var fill = RadarTerrainColor(.32f * freshness);
                if (_cfg.RadarTerrainStyle == ForetellRadarTerrainStyle.Filled)
                for (var z = 0; z < _topology.Height; ++z)
                {
                    var x = 0;
                    while (x < _topology.Width)
                    {
                        while (x < _topology.Width && !VisiblePassable(x, z)) ++x;
                        var start = x;
                        while (x < _topology.Width && VisiblePassable(x, z)) ++x;
                        if (start >= x) continue;
                        var a = RadarPoint(new(_topology.OriginX + start * _topology.Resolution, _topology.OriginZ + z * _topology.Resolution), player, cameraAzimuth, center, scale);
                        var b = RadarPoint(new(_topology.OriginX + x * _topology.Resolution, _topology.OriginZ + z * _topology.Resolution), player, cameraAzimuth, center, scale);
                        var c = RadarPoint(new(_topology.OriginX + x * _topology.Resolution, _topology.OriginZ + (z + 1) * _topology.Resolution), player, cameraAzimuth, center, scale);
                        var d = RadarPoint(new(_topology.OriginX + start * _topology.Resolution, _topology.OriginZ + (z + 1) * _topology.Resolution), player, cameraAzimuth, center, scale);
                        draw.AddTriangleFilled(a, b, c, fill);
                        draw.AddTriangleFilled(a, c, d, fill);
                    }
                }

                var outline = RadarTerrainColor(freshness);
                foreach (var contour in topology.Contours)
                    for (var i = 0; i < contour.Count; ++i)
                    {
                        var a = contour[i];
                        var b = contour[(i + 1) % contour.Count];
                        if (!ForetellTopologyWindow.TryClipSegmentToCircle(a, b, player, worldRadius,
                                out var clippedA, out var clippedB))
                            continue;
                        draw.AddLine(RadarPoint(clippedA, player, cameraAzimuth, center, scale),
                            RadarPoint(clippedB, player, cameraAzimuth, center, scale), outline, 2.1f);
                    }
                // A short wall can have reachable floor on both sides because a route goes around its end.
                // Such walls are interior edges and never appear in the outer component contour.
                foreach (var wall in topology.InteriorWalls ?? [])
                    if (ForetellTopologyWindow.TryClipSegmentToCircle(wall.A, wall.B, player, worldRadius, out var a, out var b))
                        draw.AddLine(RadarPoint(a, player, cameraAzimuth, center, scale),
                            RadarPoint(b, player, cameraAzimuth, center, scale), outline, 2.1f);

                bool VisiblePassable(int x, int z)
                {
                    var index = z * _topology.Width + x;
                    return topology.ConnectedCells[index] == (byte)TopologyCell.Passable
                        && Vector2.DistanceSquared(_topology.CellCenter(index), player) <= visibleRadiusSq;
                }

            }
        }
        finally { draw.PopClipRect(); }
        if (CurrentArenaBoundary is { ArenaLike: true } boundary)
            DrawArenaBoundaryOutline(draw, boundary, player, cameraAzimuth, center, scale);
        draw.AddCircle(center, radius, Pack(110, 130, 150, 80), 64, 1f);
        draw.AddLine(new(center.X - radius, center.Y), new(center.X + radius, center.Y), 0x354F5560u);
        draw.AddLine(new(center.X, center.Y - radius), new(center.X, center.Y + radius), 0x354F5560u);
    }

    private static Vector2 RadarPoint(Vector2 world, Vector2 player, float cameraAzimuth, Vector2 center, float scale)
        => center + ForetellInferenceCore.CameraRelativeRadarOffset(world - player, cameraAzimuth) * scale;

    private void DrawRadarGeometry(ImDrawListPtr draw, ActivePrediction p, Vector2 player, float cameraAzimuth, Vector2 center, float scale, uint color, float thickness)
    {
        switch (p.Geometry)
        {
            case GeometryKind.Circle:
                DrawRadarCircleClipped(draw, p.Origin, p.P1, player, cameraAzimuth, center, scale, color, thickness);
                break;
            case GeometryKind.Donut:
                DrawRadarCircleClipped(draw, p.Origin, p.P1, player, cameraAzimuth, center, scale, color, thickness);
                DrawRadarCircleClipped(draw, p.Origin, p.P2, player, cameraAzimuth, center, scale, color, thickness);
                break;
            case GeometryKind.Cone:
                DrawRadarCone(draw, p, player, cameraAzimuth, center, scale, color, thickness);
                break;
            case GeometryKind.Rectangle:
                DrawRadarRect(draw, p.Origin, p.Rotation, p.P1, p.P2, player, cameraAzimuth, center, scale, color, thickness);
                break;
            case GeometryKind.Cross:
                DrawRadarRect(draw, p.Origin, p.Rotation, p.P1, p.P2, player, cameraAzimuth, center, scale, color, thickness);
                DrawRadarRect(draw, p.Origin, p.Rotation + MathF.PI, p.P1, p.P2, player, cameraAzimuth, center, scale, color, thickness);
                DrawRadarRect(draw, p.Origin, p.Rotation + MathF.PI * .5f, p.P1, p.P2, player, cameraAzimuth, center, scale, color, thickness);
                DrawRadarRect(draw, p.Origin, p.Rotation - MathF.PI * .5f, p.P1, p.P2, player, cameraAzimuth, center, scale, color, thickness);
                break;
        }
    }

    private float EffectiveRadarWorldRadius(Vector2 player)
    {
        var target = _cfg.RadarWorldRadius;
        if (_cfg.RadarZoom == ForetellRadarZoom.Automatic)
        {
            target = _cfg.RadarAutoMinimumRadius;
            if (CurrentArenaBoundary is { ArenaLike: true } boundary && boundary.Points.Count >= 3)
                target = boundary.Points.Max(point => Vector2.Distance(point, player)) + 3;
            else if (TryClosedTopologyRadius(player, out var topologyRadius))
                target = topologyRadius + 3;
            target = Math.Clamp(target, _cfg.RadarAutoMinimumRadius, _cfg.RadarAutoMaximumRadius);
        }
        target = Math.Clamp(target, 5, 120);
        var now = DateTime.UtcNow;
        if (_radarDisplayedWorldRadius <= 0 || !float.IsFinite(_radarDisplayedWorldRadius))
            _radarDisplayedWorldRadius = target;
        else
        {
            var elapsed = _radarZoomUpdatedAt == default ? 0 : Math.Clamp((float)(now - _radarZoomUpdatedAt).TotalSeconds, 0, .1f);
            var speed = target > _radarDisplayedWorldRadius ? 36f : 12f;
            var delta = target - _radarDisplayedWorldRadius;
            _radarDisplayedWorldRadius += Math.Clamp(delta, -speed * elapsed, speed * elapsed);
        }
        _radarZoomUpdatedAt = now;
        return _radarDisplayedWorldRadius;
    }

    private bool TryClosedTopologyRadius(Vector2 player, out float radius)
    {
        radius = 0;
        if (!_topologyAnalysisComplete || _topologyAnalysis is not { PassableCells: > 0 } topology
            || topology.ConnectedCells.Length != _topology.CellCount || _topologySampleRadius <= 0)
            return false;
        for (var index = 0; index < topology.ConnectedCells.Length; ++index)
        {
            if (topology.ConnectedCells[index] != (byte)TopologyCell.Passable) continue;
            var distance = Vector2.Distance(_topology.CellCenter(index), player);
            radius = Math.Max(radius, distance);
            if (Vector2.Distance(_topology.CellCenter(index), TopologyCenter()) >= _topologySampleRadius - _topology.Resolution * 1.5f)
                return false;
        }
        return radius >= 6;
    }

    private void DrawArenaBoundaryOutline(ImDrawListPtr draw, ArenaBoundaryAnalysis boundary, Vector2 player,
        float cameraAzimuth, Vector2 center, float scale)
    {
        var color = RadarTerrainColor();
        for (var i = 0; i < boundary.Points.Count; ++i)
            draw.AddLine(RadarPoint(boundary.Points[i], player, cameraAzimuth, center, scale),
                RadarPoint(boundary.Points[(i + 1) % boundary.Points.Count], player, cameraAzimuth, center, scale), color, 2f);
    }

    private uint RadarTerrainColor(float alphaScale = 1)
    {
        var color = _cfg.RadarTerrainColor;
        var alpha = (byte)(color >> 24);
        return color & 0x00FFFFFFu | (uint)Math.Clamp((int)MathF.Round(alpha * alphaScale), 0, 255) << 24;
    }

    private uint RadarTerrainFillColor()
        => RadarTerrainColor(.32f);

    private IEnumerable<DynamicTerrainWarning> ActiveDynamicTerrainWarnings()
    {
        var now = ObservationNow();
        foreach (var warning in _dynamicTerrainWarnings.Values)
            if (warning.Expires > now)
                yield return warning;
    }

    private void DrawDynamicTerrainRadar(ImDrawListPtr draw, Vector2 player, float cameraAzimuth, Vector2 center, float scale)
    {
        var color = Pack(70, 190, 255, 210);
        var fill = Pack(70, 190, 255, 40);
        foreach (var warning in ActiveDynamicTerrainWarnings())
        {
            var arenaCenter = RadarPoint(warning.Center, player, cameraAzimuth, center, scale);
            draw.AddCircle(arenaCenter, warning.OuterRadius * scale, RadarTerrainColor(), 64, 2f);
            for (var i = 1; i + 1 < warning.Points.Count; ++i)
                draw.AddTriangleFilled(RadarPoint(warning.Points[0], player, cameraAzimuth, center, scale),
                    RadarPoint(warning.Points[i], player, cameraAzimuth, center, scale),
                    RadarPoint(warning.Points[i + 1], player, cameraAzimuth, center, scale), fill);
            for (var i = 0; i < warning.Points.Count; ++i)
                draw.AddLine(RadarPoint(warning.Points[i], player, cameraAzimuth, center, scale),
                    RadarPoint(warning.Points[(i + 1) % warning.Points.Count], player, cameraAzimuth, center, scale), color, 2.5f);
        }
    }

    private void DrawDynamicTerrainWorld()
    {
        var camera = Camera.Instance;
        if (camera == null) return;
        var color = Pack(70, 190, 255, 210);
        foreach (var warning in ActiveDynamicTerrainWarnings())
        {
            camera.DrawWorldCircle(new(warning.Center.X, warning.ReferenceY, warning.Center.Y), warning.OuterRadius,
                RadarTerrainColor(), 2f);
            for (var i = 0; i < warning.Points.Count; ++i)
            {
                var a = warning.Points[i];
                var b = warning.Points[(i + 1) % warning.Points.Count];
                camera.DrawWorldLine(new(a.X, warning.ReferenceY, a.Y), new(b.X, warning.ReferenceY, b.Y), color, 3f);
            }
        }
    }

    private void DrawRadarGuidance(ImDrawListPtr draw, ActivePrediction p, Vector2 player, float cameraAzimuth, Vector2 center, float scale, uint color, float thickness)
    {
        var targetActor = p.TargetID != 0 ? _ws.Actors.Find(p.TargetID) : null;
        var targetWorld = targetActor != null ? V(targetActor.Position) : p.Target;
        var target = RadarPoint(targetWorld, player, cameraAzimuth, center, scale);
        var origin = RadarPoint(p.Origin, player, cameraAzimuth, center, scale);
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
            case GuidanceKind.Marker:
                var markerSize = Math.Max(4, 1.25f * scale);
                draw.AddLine(target - new Vector2(markerSize, 0), target + new Vector2(markerSize, 0), color, thickness);
                draw.AddLine(target - new Vector2(0, markerSize), target + new Vector2(0, markerSize), color, thickness);
                draw.AddText(target + new Vector2(5, 5), color, "MARKER");
                break;
        }
    }

    private void DrawRadarCone(ImDrawListPtr draw, ActivePrediction p, Vector2 player, float cameraAzimuth, Vector2 center, float scale, uint color, float thickness)
    {
        const int n = 24;
        var previousWorld = p.Origin;
        for (var i = 0; i <= n; ++i)
        {
            var a = p.Rotation - p.P2 + 2 * p.P2 * i / n;
            var world = p.Origin + new Vector2(MathF.Sin(a), MathF.Cos(a)) * p.P1;
            if (i == 0) DrawRadarLineClipped(draw, p.Origin, world, player, cameraAzimuth, center, scale, color, thickness);
            else DrawRadarLineClipped(draw, previousWorld, world, player, cameraAzimuth, center, scale, color, thickness);
            if (i == n) DrawRadarLineClipped(draw, world, p.Origin, player, cameraAzimuth, center, scale, color, thickness);
            previousWorld = world;
        }
    }

    private void DrawRadarRect(ImDrawListPtr draw, Vector2 origin, float rot, float length, float halfWidth,
        Vector2 player, float cameraAzimuth, Vector2 center, float scale, uint color, float thickness)
    {
        var f = new Vector2(MathF.Sin(rot), MathF.Cos(rot));
        var r = new Vector2(MathF.Cos(rot), -MathF.Sin(rot));
        DrawRadarLineClipped(draw, origin + r * halfWidth, origin - r * halfWidth, player, cameraAzimuth, center, scale, color, thickness);
        DrawRadarLineClipped(draw, origin - r * halfWidth, origin - r * halfWidth + f * length, player, cameraAzimuth, center, scale, color, thickness);
        DrawRadarLineClipped(draw, origin - r * halfWidth + f * length, origin + r * halfWidth + f * length, player, cameraAzimuth, center, scale, color, thickness);
        DrawRadarLineClipped(draw, origin + r * halfWidth + f * length, origin + r * halfWidth, player, cameraAzimuth, center, scale, color, thickness);
    }

    private void DrawRadarCircleClipped(ImDrawListPtr draw, Vector2 origin, float radius, Vector2 player,
        float cameraAzimuth, Vector2 center, float scale, uint color, float thickness)
    {
        const int segments = 48;
        var previous = origin + new Vector2(0, radius);
        for (var i = 1; i <= segments; ++i)
        {
            var angle = MathF.Tau * i / segments;
            var current = origin + new Vector2(MathF.Sin(angle), MathF.Cos(angle)) * radius;
            DrawRadarLineClipped(draw, previous, current, player, cameraAzimuth, center, scale, color, thickness);
            previous = current;
        }
    }

    private void DrawRadarLineClipped(ImDrawListPtr draw, Vector2 from, Vector2 to, Vector2 player,
        float cameraAzimuth, Vector2 center, float scale, uint color, float thickness)
    {
        // Clip to the viewport only; collision walls do not prove that an AOE stops at them.
        var radius = _cfg.RadarSize * .5f / Math.Max(.01f, scale);
        if (ForetellTopologyWindow.TryClipSegmentToCircle(from, to, player, radius, out var a, out var b))
            draw.AddLine(RadarPoint(a, player, cameraAzimuth, center, scale),
                RadarPoint(b, player, cameraAzimuth, center, scale), color, thickness);
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
        if (!HasFreshTopologyEvidence) return;
        Vector2? best = null;
        var bestD = float.MaxValue;
        for (var ring = 2f; ring <= 25f; ring += 2f)
            for (var i = 0; i < 48; ++i)
            {
                var a = MathF.Tau * i / 48;
                var q = pp + new Vector2(MathF.Sin(a), MathF.Cos(a)) * ring;
                var topology = IsTopologyPassable(q);
                if (!Unsafe(q) && topology == true && ring < bestD
                    && _topology.CanTraverseSegment(pp, q, _topologyAnalysis?.ConnectedCells)
                    && !ActiveDynamicTerrainWarnings().Any(w => ForetellArenaBoundaryCore.Contains(w.Points, q)))
                { best = q; bestD = ring; }
            }
        if (best is not Vector2 b) return;
        var cam = Camera.Instance;
        if (cam == null) return;
        var y = player.PosRot.Y + .1f;
        if (!float.IsFinite(y)) return;
        DrawWorldLineClipped(cam, new(pp.X, y, pp.Y), new(b.X, y, b.Y), 0xFF40FF40u, 4f);
        cam.DrawWorldCircle(ProjectWorldAlertToTopology(new(b.X, y, b.Y)), 1f, 0xFF40FF40u, 3f);
    }

    private static bool ValidPrediction(ActivePrediction prediction)
        => float.IsFinite(prediction.Origin.X) && float.IsFinite(prediction.Origin.Y)
            && float.IsFinite(prediction.Target.X) && float.IsFinite(prediction.Target.Y)
            && float.IsFinite(prediction.Rotation) && float.IsFinite(prediction.P1) && prediction.P1 is >= 0 and <= 200
            && float.IsFinite(prediction.P2) && prediction.P2 is >= 0 and <= 200
            && float.IsFinite(prediction.Confidence) && prediction.Confidence is >= 0 and <= 1;

    private static bool HasSpatialPresentation(ActivePrediction prediction)
        => prediction.Geometry != GeometryKind.Unknown || prediction.Guidance != GuidanceKind.None;

    private static bool FiniteVector(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool FiniteViewport(Vector2 value)
        => FiniteVector(value) && value.X is >= 64 and <= 32768 && value.Y is >= 64 and <= 32768;
}
