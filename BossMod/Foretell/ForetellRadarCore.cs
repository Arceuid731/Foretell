using System.Numerics;

namespace BossMod.Foretell;

internal readonly record struct RadarView(Vector2 Center, float Radius, bool Focused);

internal static class ForetellRadarCore
{
    internal static RadarView Fit(IReadOnlyList<Vector2> points, float cameraAzimuth, float minimum, float maximum, float padding, bool square = true)
    {
        minimum = Math.Min(minimum, maximum);
        var min = points[0]; var max = min;
        foreach (var point in points) { min = Vector2.Min(min, point); max = Vector2.Max(max, point); }
        var center = (min + max) * .5f;
        var radius = minimum;
        foreach (var point in points)
        {
            var offset = ForetellInferenceCore.CameraRelativeRadarOffset(point - center, cameraAzimuth);
            radius = Math.Max(radius, (square ? Math.Max(Math.Abs(offset.X), Math.Abs(offset.Y)) : offset.Length()) + padding);
        }
        return new(center, Math.Clamp(radius, minimum, maximum), true);
    }

    internal static RadarView? FitArena(IReadOnlyList<Vector2> points, Vector2 player, Vector2? boss, float cameraAzimuth, float maximum, bool square)
    {
        if (points.Count < 3 || !ForetellArenaBoundaryCore.Contains(points, player)
            || boss is { } enemy && !ForetellArenaBoundaryCore.Contains(points, enemy)) return null;
        var min = points[0]; var max = min;
        foreach (var point in points) { min = Vector2.Min(min, point); max = Vector2.Max(max, point); }
        var size = max - min;
        // Judge enclosure size independently of party spread: a solo player beside the boss still needs
        // the full room. Reject corridors and oversized staging areas using the observed room itself.
        if (boss != null && (Math.Min(size.X, size.Y) < 8 || Math.Max(size.X, size.Y) > 2.6f * Math.Min(size.X, size.Y))) return null;
        var fitted = Fit(points, cameraAzimuth, 8, 240, 2, square);
        return fitted.Radius <= maximum ? fitted : null;
    }

    internal static RadarView Smooth(RadarView previous, RadarView target, float elapsed)
    {
        if (previous.Radius <= 0 || Vector2.Distance(previous.Center, target.Center) > 80) return target;
        var dt = Math.Clamp(elapsed, 0, .1f);
        // Open-world player tracking is exact. In combat the view is centred on the fighting space, with a
        // small dead band and smooth panning instead of doubling its radius when the player approaches an edge.
        var center = !target.Focused ? target.Center : Vector2.Distance(previous.Center, target.Center) < .6f
            ? previous.Center : Vector2.Lerp(previous.Center, target.Center, 1 - MathF.Exp(-5 * dt));
        var delta = target.Radius - previous.Radius;
        var radius = Math.Abs(delta) < .5f ? previous.Radius
            : previous.Radius + Math.Clamp(delta, -(target.Focused ? 25 : 12) * dt, 36 * dt);
        return new(center, radius, target.Focused);
    }

    // A refresh request invalidates routing evidence, not the pixels of the last complete terrain. Only a
    // genuinely old published map fades, progressively; collision changes still replace the grid immediately.
    internal static float TerrainOpacity(double publishedAgeSeconds)
        => 1 - .55f * Math.Clamp((float)(publishedAgeSeconds - 10) / 5, 0, 1);

    internal static bool InView(Vector2 world, Vector2 origin, float cameraAzimuth, float radius, bool square)
    {
        var offset = ForetellInferenceCore.CameraRelativeRadarOffset(world - origin, cameraAzimuth);
        return square ? Math.Abs(offset.X) <= radius && Math.Abs(offset.Y) <= radius : offset.LengthSquared() <= radius * radius;
    }

    internal static Vector2[]? ClosedTerrainBounds(ForetellTopologyGrid grid, TopologyAnalysis analysis, float sampleRadius)
    {
        if (grid.CellCount == 0 || analysis.ConnectedCells.Length != grid.CellCount || analysis.PassableCells == 0) return null;
        var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
        var sampleCenter = ForetellTopologyWindow.Center(grid);
        for (var index = 0; index < grid.CellCount; ++index)
        {
            if (analysis.ConnectedCells[index] != (byte)TopologyCell.Passable) continue;
            var p = grid.CellCenter(index);
            if (Vector2.Distance(p, sampleCenter) >= sampleRadius - grid.Resolution * 1.5f) return null;
            min = Vector2.Min(min, p - new Vector2(grid.Resolution * .5f));
            max = Vector2.Max(max, p + new Vector2(grid.Resolution * .5f));
        }
        if (Math.Min(max.X - min.X, max.Y - min.Y) < 8) return null;
        return [min, new(min.X, max.Y), max, new(max.X, min.Y)];
    }
}
