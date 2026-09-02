namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
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
