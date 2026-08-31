using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    public void Draw()
    {
        if (_cfg.Mode is ForetellMode.Legacy or ForetellMode.Observe) return;
        var confidenceCut = _cfg.VisualConfidence / 100f;
        var displayed = 0;
        foreach (var p in _predictions.Values.OrderBy(p => p.Activation))
        {
            if (p.Confidence < confidenceCut || displayed++ >= _cfg.MaxRenderedMechanics) continue;
            if (_cfg.WorldOverlay) DrawWorld(p);
        }
        if (_cfg.TextHints) DrawTextHints();
        if (_cfg.MiniRadar) DrawRadar();
        if (_cfg.SafePositionSuggestions) DrawSafeSuggestion();
    }

    private static uint Danger(float confidence) => confidence >= .99f ? 0xFF3030FFu : confidence >= .95f ? 0xFF40A0FFu : 0xFF40D0FFu;

    private void DrawWorld(ActivePrediction p)
    {
        var cam = Camera.Instance;
        if (cam == null) return;
        var actor = _ws.Actors.Find(p.CasterID);
        var y = actor?.PosRot.Y ?? 0;
        var o = new Vector3(p.Origin.X, y + .05f, p.Origin.Y);
        var color = Danger(p.Confidence);
        switch (p.Geometry)
        {
            case GeometryKind.Circle:
                cam.DrawWorldCircle(o, p.P1, color, 2f);
                break;
            case GeometryKind.Donut:
                cam.DrawWorldCircle(o, p.P1, color, 2f);
                cam.DrawWorldCircle(o, p.P2, color, 2f);
                break;
            case GeometryKind.Cone:
                DrawCone(cam, o, p.Rotation, p.P1, p.P2, color);
                break;
            case GeometryKind.Rectangle:
                DrawRect(cam, o, p.Rotation, p.P1, p.P2, color);
                break;
        }
    }

    private static void DrawCone(Camera cam, Vector3 o, float rot, float range, float half, uint color)
    {
        const int n = 32;
        var prev = o;
        for (var i = 0; i <= n; ++i)
        {
            var a = rot - half + 2 * half * i / n;
            var q = o + new Vector3(MathF.Sin(a) * range, 0, MathF.Cos(a) * range);
            if (i == 0) cam.DrawWorldLine(o, q, color, 2f);
            else cam.DrawWorldLine(prev, q, color, 2f);
            if (i == n) cam.DrawWorldLine(q, o, color, 2f);
            prev = q;
        }
    }

    private static void DrawRect(Camera cam, Vector3 o, float rot, float length, float halfWidth, uint color)
    {
        var f = new Vector3(MathF.Sin(rot), 0, MathF.Cos(rot));
        var r = new Vector3(MathF.Cos(rot), 0, -MathF.Sin(rot));
        var a = o + r * halfWidth;
        var b = o - r * halfWidth;
        var c = b + f * length;
        var d = a + f * length;
        cam.DrawWorldLine(a, b, color, 2);
        cam.DrawWorldLine(b, c, color, 2);
        cam.DrawWorldLine(c, d, color, 2);
        cam.DrawWorldLine(d, a, color, 2);
    }

    private void DrawTextHints()
    {
        var draw = ImGui.GetForegroundDrawList();
        var viewport = Camera.Instance?.ViewportSize ?? new Vector2(1920, 1080);
        var y = viewport.Y * .18f;
        var active = _predictions.Values.OrderBy(p => p.Activation).FirstOrDefault();
        if (active.ActionID != 0)
        {
            var remain = Math.Max(0, (active.Activation - _ws.CurrentTime).TotalSeconds);
            draw.AddText(new(viewport.X * .5f - 170, y), Danger(active.Confidence), $"FORETELL  {active.Kind} / {active.Geometry}  {remain:F1}s  {active.Confidence:P0}");
            y += 22;
        }
        var next = PredictNext();
        if (next != null)
            draw.AddText(new(viewport.X * .5f - 170, y), 0xFFE0E0E0u, $"Likely next: AID {next.To}  ~{next.MeanDelay:F1}s  ({next.Count}x)");
        draw.AddText(new(20, viewport.Y - 35), 0xFFB0B0B0u, $"Foretell learned {_store.Mechanics.Count} actions | ML updates {_store.ML.Updates} | {_lastEvidence}");
    }

    private TimelineEdge? PredictNext()
    {
        if (_previousAction == 0) return null;
        return _store.Timeline.Values.Where(e => e.From == _previousAction && e.Count >= 2).OrderByDescending(e => e.Count).FirstOrDefault();
    }

    private void DrawRadar()
    {
        var player = _ws.Party[PartyState.PlayerSlot];
        if (player == null) return;
        var cam = Camera.Instance;
        if (cam == null) return;
        var size = _cfg.RadarSize;
        var margin = 22f;
        var center = new Vector2(cam.ViewportSize.X - margin - size / 2, margin + size / 2);
        var radius = size / 2;
        var draw = ImGui.GetForegroundDrawList();
        draw.AddCircle(center, radius, 0xAAE0E0E0u, 64, 1.5f);
        draw.AddCircleFilled(center, 4, 0xFFFFFFFFu);
        var scale = radius / Math.Max(1, _cfg.RadarWorldRadius);
        foreach (var p in _predictions.Values)
        {
            if (p.Confidence < _cfg.VisualConfidence / 100f) continue;
            var rel = p.Origin - V(player.Position);
            var c = center + rel * scale;
            var col = Danger(p.Confidence);
            if (p.Geometry == GeometryKind.Circle) draw.AddCircle(c, p.P1 * scale, col, 48, 2);
            else if (p.Geometry == GeometryKind.Donut)
            {
                draw.AddCircle(c, p.P1 * scale, col, 48, 2);
                draw.AddCircle(c, p.P2 * scale, col, 48, 2);
            }
            else draw.AddCircle(c, 5, col, 16, 2);
        }
        draw.AddText(center - new Vector2(radius, radius + 18), 0xFFE0E0E0u, "Foretell adaptive radar");
    }

    private void DrawSafeSuggestion()
    {
        var player = _ws.Party[PartyState.PlayerSlot];
        if (player == null) return;
        var dangers = _predictions.Values.Where(p => p.Confidence >= _cfg.SafeConfidence / 100f).ToArray();
        if (dangers.Length == 0) return;
        var pp = V(player.Position);
        bool Unsafe(Vector2 q) => dangers.Any(p => Contains(p, q));
        if (!Unsafe(pp)) return;
        Vector2? best = null;
        var bestD = float.MaxValue;
        for (var ring = 2f; ring <= 25f; ring += 2f)
            for (var i = 0; i < 48; ++i)
            {
                var a = MathF.Tau * i / 48;
                var q = pp + new Vector2(MathF.Sin(a), MathF.Cos(a)) * ring;
                if (!Unsafe(q) && ring < bestD) { best = q; bestD = ring; }
            }
        if (best is not Vector2 b) return;
        var cam = Camera.Instance;
        if (cam == null) return;
        var y = player.PosRot.Y + .1f;
        cam.DrawWorldLine(new(pp.X, y, pp.Y), new(b.X, y, b.Y), 0xFF40FF40u, 4f);
        cam.DrawWorldCircle(new(b.X, y, b.Y), 1f, 0xFF40FF40u, 3f);
    }
}
