using System.Numerics;

namespace BossMod.Foretell;

// Plans a world-aligned rolling collision window. The visible radar always sits well inside the sampled window,
// so walking starts the next double-buffered build before the player can expose its outer edge.
internal readonly record struct ForetellTopologyWindowPlan(
    Vector2 Center,
    float VisibleRadius,
    float SampleRadius,
    float Resolution,
    float RecenterDistance,
    float Alignment);

internal static class ForetellTopologyWindow
{
    private const float MinimumSampleRadius = 56;
    private const float MaximumSampleRadius = 192;
    private const float MinimumResolution = .75f;
    private const float MaximumResolution = 1.5f;
    private const float MinimumPrefetchMargin = 36;
    private const float MaximumPrefetchMargin = 64;
    private const float TargetHalfCells = 96;
    private const int AlignmentCells = 16;

    public static ForetellTopologyWindowPlan Plan(Vector2 player, float visibleCapacity)
    {
        var visible = Math.Clamp(visibleCapacity, 5, 120);
        var margin = Math.Clamp(Math.Max(MinimumPrefetchMargin, visible * .55f), MinimumPrefetchMargin, MaximumPrefetchMargin);
        var sample = Math.Clamp(visible + margin, MinimumSampleRadius, MaximumSampleRadius);
        var resolution = Math.Clamp(sample / TargetHalfCells, MinimumResolution, MaximumResolution);
        var alignment = resolution * AlignmentCells;
        var center = new Vector2(
            MathF.Round(player.X / alignment) * alignment,
            MathF.Round(player.Y / alignment) * alignment);
        // Start rebuilding while at least ~22 yalms of hidden old coverage still remain at the maximum zoom.
        var recenter = Math.Max(resolution * 4, margin * .38f);
        return new(center, visible, sample, resolution, recenter, alignment);
    }

    public static bool NeedsReplacement(ForetellTopologyGrid grid, float sampleRadius, Vector3 player,
        ForetellTopologyWindowPlan plan)
    {
        if (grid.CellCount == 0 || sampleRadius <= 0)
            return true;
        var center = Center(grid);
        return Math.Abs(player.Y - grid.ReferenceY) > 6
            || Math.Abs(sampleRadius - plan.SampleRadius) > plan.Resolution * 1.5f
            || Math.Abs(grid.Resolution - plan.Resolution) > .05f
            || Vector2.Distance(new(player.X, player.Z), center) >= plan.RecenterDistance
            || !CoversVisible(center, sampleRadius, new(player.X, player.Z), plan.VisibleRadius, grid.Resolution * 3);
    }

    public static bool CoversVisible(Vector2 center, float sampleRadius, Vector2 player, float visibleRadius, float reserve)
        => Vector2.Distance(center, player) + visibleRadius + Math.Max(0, reserve) <= sampleRadius;

    public static Vector2 Center(ForetellTopologyGrid grid)
        => new(grid.OriginX + grid.Width * grid.Resolution * .5f,
            grid.OriginZ + grid.Height * grid.Resolution * .5f);

    public static bool TryClipSegmentToCircle(Vector2 a, Vector2 b, Vector2 center, float radius,
        out Vector2 clippedA, out Vector2 clippedB)
    {
        clippedA = clippedB = default;
        if (!float.IsFinite(radius) || radius <= 0)
            return false;
        var delta = b - a;
        var lengthSquared = delta.LengthSquared();
        if (lengthSquared <= 1e-8f)
        {
            if (Vector2.DistanceSquared(a, center) > radius * radius)
                return false;
            clippedA = clippedB = a;
            return true;
        }
        var offset = a - center;
        var linear = 2 * Vector2.Dot(offset, delta);
        var constant = offset.LengthSquared() - radius * radius;
        var discriminant = linear * linear - 4 * lengthSquared * constant;
        if (discriminant < 0)
            return false;
        var root = MathF.Sqrt(discriminant);
        var first = (-linear - root) / (2 * lengthSquared);
        var last = (-linear + root) / (2 * lengthSquared);
        var from = Math.Max(0, first);
        var to = Math.Min(1, last);
        if (from > to)
            return false;
        clippedA = a + delta * from;
        clippedB = a + delta * to;
        return true;
    }
}
