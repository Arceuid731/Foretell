using System.Numerics;
using BossMod.Foretell;

internal static class RadarTests
{
    public static void Run()
    {
        Vector2[] rectangle = [new(-14.5f, -19.5f), new(-14.5f, 19.5f), new(14.5f, 19.5f), new(14.5f, -19.5f)];
        for (var i = 0; i < 32; ++i)
        {
            var camera = i * MathF.Tau / 32;
            var view = ForetellRadarCore.Fit(rectangle, camera, 8, 65, 2);
            Check.That(view.Center == Vector2.Zero && view.Radius < 30, "Compact rectangle unnecessarily zoomed out");
            Check.That(rectangle.All(p => ForetellRadarCore.InView(p, view.Center, camera, view.Radius, true)), "Arena corner clipped in square viewport");
        }
        var small = ForetellRadarCore.Fit([new(-14.5f), new(14.5f)], 0, 8, 65, 2);
        Check.That(small.Radius == 16.5f, "Small arena inherited the 30-yalm open-world minimum");
        Check.That(ForetellRadarCore.Fit(rectangle, 0, 16, 10, 2).Radius == 10, "User maximum below combat minimum crashed or was ignored");
        Check.That(!ForetellRadarCore.InView(new(19, 19), Vector2.Zero, 0, 20, false)
            && ForetellRadarCore.InView(new(19, 19), Vector2.Zero, 0, 20, true), "Circle and square clipping do not differ at corners");

        var before = new RadarView(Vector2.Zero, 40, false);
        var target = new RadarView(new(12, 3), 16, true);
        var first = ForetellRadarCore.Smooth(before, target, 1f / 60);
        Check.That(first.Radius > 39 && first.Radius < 40 && first.Center.X is > 0 and < 2, "Combat transition snapped");
        var settled = first;
        for (var i = 0; i < 240; ++i) settled = ForetellRadarCore.Smooth(settled, target, 1f / 60);
        Check.That(Math.Abs(settled.Radius - target.Radius) < .5 && Vector2.Distance(settled.Center, target.Center) < .6, "View never converged");
        var jitter = ForetellRadarCore.Smooth(settled, settled with { Radius = settled.Radius + .1f, Center = settled.Center + new Vector2(.1f) }, 1f / 60);
        Check.That(jitter == settled, "Small observation noise made the view breathe");
        var moving = ForetellRadarCore.Smooth(settled, new(new(15, 9), 30, false), 1f / 60);
        Check.That(moving.Center == new Vector2(15, 9), "Open-world tracking lagged behind the player");
        Check.That(ForetellRadarCore.TerrainOpacity(.1) == ForetellRadarCore.TerrainOpacity(9), "Normal refresh interval changed fill opacity");
        Check.That(ForetellRadarCore.TerrainOpacity(12) < 1 && ForetellRadarCore.TerrainOpacity(12) > ForetellRadarCore.TerrainOpacity(15), "Stale map did not fade progressively");

        var grid = new ForetellTopologyGrid(); grid.Reset(Vector3.Zero, 30, 1);
        for (var i = 0; i < grid.CellCount; ++i)
        {
            var p = grid.CellCenter(i);
            grid.Set(i, Math.Abs(p.X) <= 12 && Math.Abs(p.Y) <= 10 ? TopologyCell.Passable : TopologyCell.Blocked, 0);
        }
        var bounds = ForetellRadarCore.ClosedTerrainBounds(grid, grid.Analyze(Vector2.Zero), 30);
        Check.That(bounds != null && ForetellRadarCore.Fit(bounds, 0, 8, 65, 2).Radius < 16, "Closed terrain was not fitted");
        for (var i = 0; i < grid.CellCount; ++i)
            if (grid.CellCenter(i).X > 6) grid.Set(i, TopologyCell.Void);
        var changed = ForetellRadarCore.ClosedTerrainBounds(grid, grid.Analyze(Vector2.Zero), 30);
        Check.That(changed != null && changed.Max(p => p.X) < bounds!.Max(p => p.X), "Removed floor persisted in the next framing input");
        for (var i = 0; i < grid.CellCount; ++i) grid.Set(i, TopologyCell.Passable, 0);
        Check.That(ForetellRadarCore.ClosedTerrainBounds(grid, grid.Analyze(Vector2.Zero), 30) == null, "Sampling edge masqueraded as a closed arena");
        Console.WriteLine("Radar framing, clipping, refresh and dynamic floor tests passed.");
    }
}
