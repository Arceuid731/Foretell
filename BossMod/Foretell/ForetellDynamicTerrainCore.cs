using System.Numerics;

namespace BossMod.Foretell;

// Event-object animations are the only early signal for some destructible platforms. This detector uses only the
// live spatial arrangement: a ring of peer objects around a credible combat centre defines angular sector bisectors.
// No territory, action, object ID or animation-state table enters the decision.
internal static class ForetellDynamicTerrainCore
{
    public static List<Vector2> BuildRadialSector(Vector2 center, Vector2 trigger, IReadOnlyList<Vector2> peers,
        float outerPadding, out float angularWidth)
    {
        angularWidth = 0;
        var radial = trigger - center;
        var radius = radial.Length();
        if (!Finite(center) || !Finite(trigger) || !float.IsFinite(outerPadding) || radius is < 7 or > 60)
            return [];
        var triggerAngle = MathF.Atan2(radial.X, radial.Y);
        var peerAngles = peers.Where(Finite).Select(point => point - center)
            .Where(offset => Math.Abs(offset.Length() - radius) <= Math.Max(3, radius * .25f))
            .Select(offset => MathF.Atan2(offset.X, offset.Y)).ToArray();
        if (peerAngles.Length < 3)
            return [];

        var clockwise = float.MaxValue;
        var counterClockwise = float.MaxValue;
        foreach (var angle in peerAngles)
        {
            var delta = PositiveAngle(angle - triggerAngle);
            if (delta is > .02f and < MathF.Tau - .02f)
            {
                clockwise = Math.Min(clockwise, delta);
                counterClockwise = Math.Min(counterClockwise, MathF.Tau - delta);
            }
        }
        if (!float.IsFinite(clockwise) || !float.IsFinite(counterClockwise))
            return [];
        clockwise = Math.Clamp(clockwise, 30f * MathF.PI / 180f, 150f * MathF.PI / 180f);
        counterClockwise = Math.Clamp(counterClockwise, 30f * MathF.PI / 180f, 150f * MathF.PI / 180f);
        angularWidth = (clockwise + counterClockwise) * .5f;
        var start = triggerAngle - counterClockwise * .5f;
        var end = triggerAngle + clockwise * .5f;
        var outer = Math.Clamp(radius + Math.Max(3, outerPadding), 10, 90);
        var points = new List<Vector2> { center };
        const int arcSegments = 12;
        for (var i = 0; i <= arcSegments; ++i)
        {
            var angle = start + (end - start) * i / arcSegments;
            points.Add(center + new Vector2(MathF.Sin(angle), MathF.Cos(angle)) * outer);
        }
        return points;
    }

    private static float PositiveAngle(float angle)
    {
        angle %= MathF.Tau;
        return angle < 0 ? angle + MathF.Tau : angle;
    }

    private static bool Finite(Vector2 point) => float.IsFinite(point.X) && float.IsFinite(point.Y);
}
