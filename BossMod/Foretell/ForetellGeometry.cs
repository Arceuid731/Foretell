namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private FitResult FitGeometry(CastSnapshot cast, List<Sample> samples)
    {
        FitResult best = new(GeometryKind.Unknown, cast.Target, cast.Rotation, 0, 0, 0);
        void Try(FitResult f) { if (f.Score > best.Score) best = f; }

        for (var r = 2f; r <= 35f; r += 1f)
            Try(new(GeometryKind.Circle, cast.Target, 0, r, 0, Score(samples, p => Vector2.Distance(p, cast.Target) <= r)));

        for (var inner = 2f; inner <= 18f; inner += 2f)
            for (var outer = inner + 4; outer <= Math.Min(40, inner + 24); outer += 3f)
                Try(new(GeometryKind.Donut, cast.Target, 0, inner, outer,
                    Score(samples, p => { var d = Vector2.Distance(p, cast.Target); return d >= inner && d <= outer; })));

        for (var range = 8f; range <= 50f; range += 4f)
            foreach (var halfDeg in new[] { 15f, 22.5f, 30f, 45f, 60f, 90f })
            {
                var half = halfDeg * MathF.PI / 180f;
                Try(new(GeometryKind.Cone, cast.Origin, cast.Rotation, range, half,
                    Score(samples, p => InCone(p, cast.Origin, cast.Rotation, range, half))));
            }

        for (var length = 8f; length <= 50f; length += 4f)
            for (var halfWidth = 1.5f; halfWidth <= 12f; halfWidth += 1.5f)
                Try(new(GeometryKind.Rectangle, cast.Origin, cast.Rotation, length, halfWidth,
                    Score(samples, p => InRect(p, cast.Origin, cast.Rotation, length, halfWidth))));
                Try(new(GeometryKind.Cross, cast.Origin, cast.Rotation, length, halfWidth,
                    Score(samples, p => InCross(p, cast.Origin, cast.Rotation, length, halfWidth))));
        return best;
    }

    private static float Score(List<Sample> samples, Func<Vector2, bool> contains)
    {
        if (samples.Count == 0) return 0;
        float tp = 0, tn = 0, fp = 0, fn = 0;
        foreach (var s in samples)
        {
            var pred = contains(s.Position);
            if (pred && s.Hit) ++tp;
            else if (!pred && !s.Hit) ++tn;
            else if (pred) ++fp;
            else ++fn;
        }
        var balanced = .5f * (tp / Math.Max(1, tp + fn) + tn / Math.Max(1, tn + fp));
        var precision = tp / Math.Max(1, tp + fp);
        return Math.Clamp(balanced * .75f + precision * .25f, 0, 1);
    }

    private static bool InCone(Vector2 p, Vector2 o, float rot, float range, float half)
    {
        var d = p - o;
        var len = d.Length();
        if (len > range || len < .01f) return false;
        var a = MathF.Atan2(d.X, d.Y);
        return MathF.Abs(Norm(a - rot)) <= half;
    }

    private static bool InRect(Vector2 p, Vector2 o, float rot, float length, float halfWidth)
    {
        var d = p - o;
        var s = MathF.Sin(rot);
        var c = MathF.Cos(rot);
        var forward = d.X * s + d.Y * c;
        var side = d.X * c - d.Y * s;
        return forward >= 0 && forward <= length && MathF.Abs(side) <= halfWidth;
    }

    private static bool InCross(Vector2 p, Vector2 o, float rot, float length, float halfWidth)
        => InRect(p, o, rot, length, halfWidth)
            || InRect(p, o, rot + MathF.PI, length, halfWidth)
            || InRect(p, o, rot + MathF.PI * .5f, length, halfWidth)
            || InRect(p, o, rot - MathF.PI * .5f, length, halfWidth);

    private static float Norm(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    private static bool Contains(ActivePrediction p, Vector2 q) => p.Geometry switch
    {
        GeometryKind.Circle => Vector2.Distance(q, p.Origin) <= p.P1,
        GeometryKind.Donut => Vector2.Distance(q, p.Origin) is var d && d >= p.P1 && d <= p.P2,
        GeometryKind.Cone => InCone(q, p.Origin, p.Rotation, p.P1, p.P2),
        GeometryKind.Rectangle => InRect(q, p.Origin, p.Rotation, p.P1, p.P2),
        GeometryKind.Cross => InCross(q, p.Origin, p.Rotation, p.P1, p.P2),
        _ => false
    };
}