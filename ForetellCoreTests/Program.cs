using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using BossMod.Foretell;

static class Check
{
    public static void That(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

static class ForetellCoreTests
{
    public static void Main()
    {
        TopologyConnectedComponentAndHole();
        TopologyWindowPrefetchesBeforeVisibleEdge();
        TopologyRadarContourClipIsContinuous();
        TopologyContoursAreStitchedAndSimplified();
        TopologyFrontierFindsClosedRoomEarly();
        TopologyProbeFollowsReachedElevation();
        CollisionRasterSelectsReachableLayer();
        CollisionRasterStopsAtMeshWall();
        CollisionRasterBuildsCorridorAndRoom();
        TopologyBarrierClosesConnectedSurface();
        TopologyRequiresObservedConnections();
        TopologyLoadBudget();
        TopologyFuzzMaintainsInvariants();
        ArenaBoundaryInference();
        TopologyPresentationNeverHidesUnknownTerrain();
        DynamicTerrainSectorInference();
        RawRoundTrip();
        RawLiveReplayDeterminism();
        RawStructuralFeaturesAreDeterministic();
        RawSchemaOneCompatibility();
        RawRejectsCorruption();
        RawRejectsTruncation();
        RawFuzzDoesNotEscape();
        ClassifierMigrationPreservesFeatureFamilies();
        ClassifierRejectsNonFiniteStateAndInput();
        InferenceReliabilityAndAbstention();
        CausalAndTimelineConfidence();
        PhaseClockAndHealthTriggerSelection();
        GeometryValidationAndGuidance();
        ActionMetadataSafetyFamilies();
        MechanicSourcesExcludePartyActions();
        RadarIsCameraRelative();
        OutOfCombatHazardContextIsScoped();
        StorageMaintenanceProtectsActiveFiles();
        DecisionAuditRoundTrip();
        Console.WriteLine("Foretell core tests passed.");
    }

    private static void TopologyConnectedComponentAndHole()
    {
        var grid = new ForetellTopologyGrid();
        grid.Reset(Vector3.Zero, 10, 1);
        for (var z = 3; z <= 17; ++z)
            for (var x = 3; x <= 17; ++x)
                grid.Set(z * grid.Width + x, TopologyCell.Passable, 0);
        for (var z = 9; z <= 11; ++z)
            for (var x = 9; x <= 11; ++x)
                grid.Set(z * grid.Width + x, TopologyCell.Void);
        grid.Set(0, TopologyCell.Passable, 8); // disconnected platform must not enter the player's component

        var result = grid.Analyze(Vector2.Zero);
        Check.That(result.PassableCells == 15 * 15 - 9, $"unexpected connected area {result.PassableCells}");
        Check.That(result.Components == 2, $"unexpected component count {result.Components}");
        Check.That(result.Contours.Count >= 2, "outer boundary and hole were not reconstructed");
        Check.That(grid.IsConnectedPassable(Vector2.Zero, result.ConnectedCells) == false, "hole considered passable");
        Check.That(grid.IsConnectedPassable(new(-5, -5), result.ConnectedCells) == true, "reachable floor rejected");
    }

    private static void TopologyLoadBudget()
    {
        var grid = new ForetellTopologyGrid();
        grid.Reset(Vector3.Zero, 48, 1);
        for (var i = 0; i < grid.CellCount; ++i)
            grid.Set(i, i % 13 == 0 ? TopologyCell.Blocked : TopologyCell.Passable, MathF.Sin(i * .01f));
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 100; ++i) _ = grid.Analyze(Vector2.Zero);
        sw.Stop();
        Check.That(sw.Elapsed < TimeSpan.FromSeconds(5), $"topology analysis load regression: {sw.Elapsed}");
    }

    private static void TopologyFuzzMaintainsInvariants()
    {
        var random = new Random(0xA11E7A);
        for (var iteration = 0; iteration < 100; ++iteration)
        {
            var grid = new ForetellTopologyGrid();
            grid.Reset(new Vector3(random.Next(-100, 100), random.Next(-5, 5), random.Next(-100, 100)), random.Next(4, 24), random.Next(1, 4));
            for (var i = 0; i < grid.CellCount; ++i)
            {
                var cell = (TopologyCell)random.Next(0, 4);
                grid.Set(i, cell, cell == TopologyCell.Passable ? (float)(random.NextDouble() * 8 - 4) : float.NaN);
            }
            var result = grid.Analyze(grid.CellCenter(random.Next(grid.CellCount)));
            Check.That(result.ConnectedCells.Length == grid.CellCount && result.HeightCentimeters.Length == grid.CellCount, "topology result shape mismatch");
            Check.That(result.PassableCells + result.BlockedCells + result.UnknownCells == grid.CellCount, "topology cell accounting mismatch");
            Check.That(result.Contours.All(loop => loop.Count >= 3 && loop.All(point => float.IsFinite(point.X) && float.IsFinite(point.Y))), "invalid topology contour");
        }
    }

    private static void RawRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foretell-test-{Guid.NewGuid():N}.ftraw.gz");
        try
        {
            using (var file = File.Create(path))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(gzip))
            {
                ForetellRawFormat.WriteHeader(writer);
                ForetellRawFormat.Write(writer, new(ForetellRawRecordKind.ServerIPC, DateTime.UtcNow.Ticks, 777,
                    [12, 34, 56], 0x1234, 0x5678, 0, [1, 2, 3, 4]));
                ForetellRawFormat.Write(writer, new(ForetellRawRecordKind.ActorControl, DateTime.UtcNow.AddMilliseconds(300).Ticks, 777,
                    [99, 1, 2, 3, 4, 5, 6, 7, 8], 0x1234, 0, 1, []));
            }
            var report = ForetellRawFormat.Read(path);
            Check.That(report.Complete, string.Join(';', report.Errors));
            Check.That(report.Schema == ForetellRawFormat.CurrentSchema, "schema mismatch");
            Check.That(report.Records == 2 && report.ServerPackets == 1 && report.ActorControls == 1, "record counts mismatch");
            Check.That(report.PayloadBytes == 4 && report.Windows.Count >= 1, "payload/window mismatch");
            Check.That(report.Windows.All(w => w.TerritoryID == 777), "territory was not retained");
        }
        finally { File.Delete(path); }
    }

    private static void RawRejectsCorruption()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foretell-corrupt-{Guid.NewGuid():N}.ftraw.gz");
        try
        {
            using (var file = File.Create(path))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(gzip))
            {
                writer.Write(ForetellRawFormat.Magic);
                writer.Write(ForetellRawFormat.CurrentSchema);
                writer.Write((byte)ForetellRawRecordKind.ServerIPC);
                writer.Write(DateTime.UtcNow.Ticks);
                writer.Write((uint)1);
                for (var i = 0; i < ForetellRawFormat.ArgumentCount; ++i) writer.Write((uint)0);
                writer.Write((ulong)0); writer.Write((ulong)0); writer.Write((byte)0);
                writer.Write(ForetellRawFormat.MaxPayloadBytes + 1);
            }
            var report = ForetellRawFormat.Read(path);
            Check.That(!report.Complete && report.Errors.Any(e => e.Contains("payload length")), "oversized payload was accepted");
        }
        finally { File.Delete(path); }
    }

    private static void RawLiveReplayDeterminism()
    {
        var now = DateTime.UtcNow;
        var records = new[]
        {
            new ForetellRawRecord(ForetellRawRecordKind.ServerIPC, now.Ticks, 91, [12, 34], 1, 2, 3, [0, 1, 127, 255]),
            new ForetellRawRecord(ForetellRawRecordKind.ClientIPC, now.AddMilliseconds(10).Ticks, 91, [77], 0, 0, 0, [4, 5, 6]),
            new ForetellRawRecord(ForetellRawRecordKind.ActorControl, now.AddMilliseconds(20).Ticks, 91, [8, 7, 6, 5, 4, 3, 2, 1, 0], 3, 4, 1, [])
        };
        var live = new ForetellRawWindowAccumulator();
        foreach (var record in records) live.Add(record);
        var expected = live.Finish();
        var path = Path.Combine(Path.GetTempPath(), $"foretell-determinism-{Guid.NewGuid():N}.ftraw.gz");
        try
        {
            using (var file = File.Create(path))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(gzip))
            {
                ForetellRawFormat.WriteHeader(writer);
                foreach (var record in records) ForetellRawFormat.Write(writer, record);
            }
            var actual = ForetellRawFormat.Read(path).Windows.Single();
            Check.That(actual.Opcodes.OrderBy(kv => kv.Key).SequenceEqual(expected.Opcodes.OrderBy(kv => kv.Key)), "live/replay opcode vectors differ");
            Check.That(actual.BinaryBuckets.SequenceEqual(expected.BinaryBuckets), "live/replay binary vectors differ");
        }
        finally { File.Delete(path); }
    }

    private static void RawStructuralFeaturesAreDeterministic()
    {
        var now = DateTime.UtcNow;
        var records = new[]
        {
            new ForetellRawRecord(ForetellRawRecordKind.ServerIPC, now.Ticks, 42, [0x123], 1, 2, 0, [10, 20, 30]),
            new ForetellRawRecord(ForetellRawRecordKind.ServerIPC, now.AddMilliseconds(1).Ticks, 42, [0x123], 1, 2, 0, [10, 40, 30]),
            new ForetellRawRecord(ForetellRawRecordKind.ClientIPC, now.AddMilliseconds(2).Ticks, 42, [0x456], 2, 1, 1, [99])
        };
        ForetellRawFeatureWindow Build()
        {
            var accumulator = new ForetellRawWindowAccumulator();
            foreach (var record in records) accumulator.Add(record);
            return accumulator.Finish();
        }
        var first = Build();
        var second = Build();
        Check.That(first.Transitions.OrderBy(pair => pair.Key).SequenceEqual(second.Transitions.OrderBy(pair => pair.Key)), "raw transition graph is not deterministic");
        Check.That(first.OpcodeFeatures.Keys.Order().SequenceEqual(second.OpcodeFeatures.Keys.Order()), "raw structural opcode families differ");
        foreach (var opcode in first.OpcodeFeatures.Keys)
        {
            var a = first.OpcodeFeatures[opcode];
            var b = second.OpcodeFeatures[opcode];
            Check.That(a == b || (a.Count == b.Count && a.PayloadBytes == b.PayloadBytes && a.MinLength == b.MinLength
                && a.MaxLength == b.MaxLength && a.SequenceHash == b.SequenceHash
                && a.ByteMeans.SequenceEqual(b.ByteMeans) && a.ByteVariances.SequenceEqual(b.ByteVariances)), "raw structural feature mismatch");
        }
        var serverKey = ((uint)ForetellRawRecordKind.ServerIPC << 24) | 0x123u;
        Check.That(first.OpcodeFeatures[serverKey].ByteVariances[0] == 0, "stable payload byte reported variance");
        Check.That(first.OpcodeFeatures[serverKey].ByteVariances[1] > 0, "changing payload byte was not detected");
        Check.That(first.Transitions.Count == 2, "raw transition families were lost");
    }

    private static void RawSchemaOneCompatibility()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foretell-v1-{Guid.NewGuid():N}.ftraw.gz");
        try
        {
            using (var file = File.Create(path))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(gzip))
            {
                writer.Write(ForetellRawFormat.Magic);
                writer.Write(1);
                writer.Write((byte)ForetellRawRecordKind.ClientIPC);
                writer.Write(DateTime.UtcNow.Ticks);
                for (var i = 0; i < ForetellRawFormat.ArgumentCount; ++i) writer.Write((uint)i);
                writer.Write((ulong)0); writer.Write((ulong)0); writer.Write((byte)0);
                writer.Write(2); writer.Write(new byte[] { 9, 10 });
            }
            var report = ForetellRawFormat.Read(path, legacyTerritory: 456);
            Check.That(report.Complete && report.Schema == 1 && report.Records == 1, "schema 1 migration failed");
            Check.That(report.Windows.Single().TerritoryID == 456, "schema 1 territory fallback failed");
        }
        finally { File.Delete(path); }
    }

    private static void RawRejectsTruncation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foretell-truncated-{Guid.NewGuid():N}.ftraw.gz");
        try
        {
            using (var file = File.Create(path))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(gzip))
            {
                ForetellRawFormat.WriteHeader(writer);
                writer.Write((byte)ForetellRawRecordKind.ServerIPC);
                writer.Write(DateTime.UtcNow.Ticks); writer.Write((uint)1);
                for (var i = 0; i < ForetellRawFormat.ArgumentCount; ++i) writer.Write((uint)0);
                writer.Write((ulong)0); writer.Write((ulong)0); writer.Write((byte)0);
                writer.Write(20); writer.Write(new byte[] { 1, 2, 3 });
            }
            var report = ForetellRawFormat.Read(path);
            Check.That(!report.Complete && report.Errors.Any(e => e.Contains("truncated payload")), "truncated payload was accepted");
        }
        finally { File.Delete(path); }
    }

    private static void RawFuzzDoesNotEscape()
    {
        var random = new Random(0xF07E11);
        var stopwatch = Stopwatch.StartNew();
        for (var iteration = 0; iteration < 200; ++iteration)
        {
            var path = Path.Combine(Path.GetTempPath(), $"foretell-fuzz-{Guid.NewGuid():N}.ftraw.gz");
            try
            {
                var bytes = new byte[random.Next(0, 2048)];
                random.NextBytes(bytes);
                if ((iteration & 1) == 0)
                {
                    using var file = File.Create(path);
                    using var gzip = new GZipStream(file, CompressionLevel.Fastest);
                    gzip.Write(bytes);
                }
                else File.WriteAllBytes(path, bytes);
                var report = ForetellRawFormat.Read(path);
                Check.That(!report.Complete, "random fuzz unexpectedly formed a complete journal");
            }
            finally { File.Delete(path); }
        }
        Check.That(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"raw parser fuzz budget regression: {stopwatch.Elapsed}");
    }

    private static void ClassifierMigrationPreservesFeatureFamilies()
    {
        const int oldBase = 10;
        var oldFeatureCount = oldBase + OnlineClassifier.FabricFeatureCount;
        var weights = Enumerable.Range(0, OnlineClassifier.ClassCount).Select(_ => new double[oldFeatureCount + 1]).ToArray();
        weights[1][0] = 1.25;
        weights[1][oldBase] = 2.5;
        weights[1][oldFeatureCount] = 3.75;
        var state = new MLState { FeatureCount = oldFeatureCount, ClassCount = OnlineClassifier.ClassCount, Weights = weights };
        var classifier = new OnlineClassifier(state);
        Check.That(state.FeatureCount == OnlineClassifier.FeatureCount, "classifier feature schema was not migrated");
        Check.That(state.Weights[1][0] == 1.25, "semantic weight was lost during migration");
        Check.That(state.Weights[1][OnlineClassifier.BaseFeatureCount] == 2.5, "fabric weight was not shifted during migration");
        Check.That(state.Weights[1][OnlineClassifier.FeatureCount] == 3.75, "classifier bias was lost during migration");
        var features = new double[OnlineClassifier.FeatureCount];
        features[0] = 1; features[OnlineClassifier.BaseFeatureCount] = 1;
        classifier.Train(features, MechanicKind.GroundAOE);
        var prediction = classifier.Predict(features);
        Check.That(float.IsFinite(prediction.Confidence) && prediction.Confidence is >= 0 and <= 1, "classifier produced an invalid probability");
    }

    private static void ClassifierRejectsNonFiniteStateAndInput()
    {
        var state = new MLState { Updates = -4 };
        state.Weights[0][0] = double.NaN;
        state.Weights[1][OnlineClassifier.FeatureCount] = double.PositiveInfinity;
        var classifier = new OnlineClassifier(state);
        Check.That(state.Updates == 0 && state.Weights.SelectMany(row => row).All(double.IsFinite), "non-finite persisted classifier state survived migration");

        var features = new double[OnlineClassifier.FeatureCount];
        features[0] = double.NaN;
        features[1] = double.NegativeInfinity;
        features[2] = 1;
        classifier.Train(features, MechanicKind.Stack);
        var prediction = classifier.Predict(features);
        Check.That(float.IsFinite(prediction.Confidence) && prediction.Confidence is >= 0 and <= 1, "non-finite input escaped classifier guards");
        Check.That(state.Weights.SelectMany(row => row).All(double.IsFinite), "training introduced non-finite classifier state");
    }

    private static void InferenceReliabilityAndAbstention()
    {
        Check.That(Math.Abs(ForetellInferenceCore.GuidanceConfidence(.99f, 0, 0) - .94f) < .0001f, "unverified guidance crossed warning gate");
        var perfect = ForetellInferenceCore.GuidanceConfidence(.99f, 20, 0);
        var imperfect = ForetellInferenceCore.GuidanceConfidence(.99f, 19, 1);
        Check.That(perfect > .80f && perfect < .99f, "verified lower bound is implausible");
        Check.That(imperfect < perfect, "a forecast miss did not lower guidance confidence");
        Check.That(ForetellInferenceCore.GuidanceConfidence(float.NaN, 20, 0) == 0, "non-finite evidence escaped abstention");
    }

    private static void CausalAndTimelineConfidence()
    {
        var weak = ForetellInferenceCore.CausalConfidence(1, 0, 2, 2);
        var strong = ForetellInferenceCore.CausalConfidence(20, 20, 2, .05);
        Check.That(strong > weak && strong > .90f, "exact stable causal evidence was not preferred");
        var a = new SignalTimelineEdge { From = "A", To = "B", Phase = 1, Count = 7 };
        var b = new SignalTimelineEdge { From = "A", To = "C", Phase = 1, Count = 3 };
        Check.That(Math.Abs(ForetellInferenceCore.TimelineProbability(a, [a, b]) - .7f) < .0001f, "timeline branch probability is incorrect");
        Check.That(ForetellInferenceCore.WilsonLowerBound(10, 10) > ForetellInferenceCore.WilsonLowerBound(5, 10), "reliability ordering regressed");
    }

    private static void CollisionRasterSelectsReachableLayer()
    {
        var triangles = new List<ForetellCollisionTriangle>();
        AddQuad(triangles, -6, -6, 6, 6, 0);
        AddQuad(triangles, -6, -6, 6, 6, 7);
        var snapshot = new ForetellCollisionSnapshot(new(0, .1f, 0), Vector2.Zero, 7, 1, triangles.ToArray(), 1, triangles.Count, 0);
        var result = ForetellCollisionRasterizer.Build(snapshot);
        Check.That(result.Analysis.PassableCells > 100, "collision raster did not reconstruct the nearby floor");
        Check.That(result.Grid.TryConnectedHeight(Vector2.Zero, result.Analysis.ConnectedCells, out var floorHeight)
            && Math.Abs(floorHeight) < .25f, "connected floor height is not reusable by 3D overlays");
        for (var i = 0; i < result.Grid.CellCount; ++i)
            if (result.Analysis.ConnectedCells[i] == (byte)TopologyCell.Passable)
                Check.That(Math.Abs(result.Grid.Heights[i]) < .25f, "collision raster leaked onto a stacked floor");
    }

    private static void TopologyWindowPrefetchesBeforeVisibleEdge()
    {
        var player = new Vector2(137.4f, -82.1f);
        var plan = ForetellTopologyWindow.Plan(player, 65);
        Check.That(plan.SampleRadius >= plan.VisibleRadius + 35, "rolling topology lost its hidden prefetch margin");
        Check.That(Math.Abs(plan.Center.X / plan.Alignment - MathF.Round(plan.Center.X / plan.Alignment)) < .001f
            && Math.Abs(plan.Center.Y / plan.Alignment - MathF.Round(plan.Center.Y / plan.Alignment)) < .001f,
            "rolling topology window is not world aligned");
        var grid = new ForetellTopologyGrid();
        grid.Reset(new(plan.Center.X, 12, plan.Center.Y), plan.SampleRadius, plan.Resolution);
        Check.That(!ForetellTopologyWindow.NeedsReplacement(grid, plan.SampleRadius,
            new(plan.Center.X, 12, plan.Center.Y), plan), "fresh rolling window immediately requested replacement");
        var triggerPlayer = plan.Center + new Vector2(plan.RecenterDistance + .1f, 0);
        Check.That(ForetellTopologyWindow.CoversVisible(plan.Center, plan.SampleRadius, triggerPlayer,
            plan.VisibleRadius, plan.Resolution * 3), "recenter starts after the visible radar can expose an old edge");
        Check.That(ForetellTopologyWindow.NeedsReplacement(grid, plan.SampleRadius,
            new(triggerPlayer.X, 12, triggerPlayer.Y), plan), "movement did not schedule the hidden back buffer");
    }

    private static void TopologyRadarContourClipIsContinuous()
    {
        Check.That(ForetellTopologyWindow.TryClipSegmentToCircle(new(-20, 0), new(20, 0), Vector2.Zero, 10,
                out var a, out var b)
            && Vector2.Distance(a, new(-10, 0)) < .001f && Vector2.Distance(b, new(10, 0)) < .001f,
            "radar contour was not clipped continuously at both circle edges");
        Check.That(ForetellTopologyWindow.TryClipSegmentToCircle(new(-2, 3), new(4, 3), Vector2.Zero, 10,
                out a, out b)
            && Vector2.Distance(a, new(-2, 3)) < .001f && Vector2.Distance(b, new(4, 3)) < .001f,
            "inside radar contour was modified");
        Check.That(!ForetellTopologyWindow.TryClipSegmentToCircle(new(20, 20), new(30, 30), Vector2.Zero, 10,
            out _, out _), "off-radar contour leaked into the visible map");
    }

    private static void TopologyContoursAreStitchedAndSimplified()
    {
        var grid = new ForetellTopologyGrid();
        grid.Reset(Vector3.Zero, 20, 1);
        // Synthetic diagonal room edge creates a long one-cell raster staircase before contour simplification.
        for (var z = 5; z <= 34; ++z)
            for (var x = 5; x <= Math.Min(34, z); ++x)
                grid.Set(z * grid.Width + x, TopologyCell.Passable, 0);
        var result = grid.Analyze(new(-10, -10));
        Check.That(result.Contours.Count == 1, $"connected raster room produced {result.Contours.Count} contour fragments");
        Check.That(result.Contours[0].Count <= 8, $"raster staircase remained visible as {result.Contours[0].Count} contour vertices");
        Check.That(result.Contours[0].Distinct().Count() == result.Contours[0].Count,
            "stitched contour contains duplicate discontinuity vertices");
    }

    private static void CollisionRasterStopsAtMeshWall()
    {
        var triangles = new List<ForetellCollisionTriangle>();
        AddQuad(triangles, -7, -7, 7, 7, 0);
        triangles.Add(new(new(0, 0, -7), new(0, 3, -7), new(0, 3, 7)));
        triangles.Add(new(new(0, 0, -7), new(0, 3, 7), new(0, 0, 7)));
        var snapshot = new ForetellCollisionSnapshot(new(-3, .1f, 0), Vector2.Zero, 8, 1, triangles.ToArray(), 1, triangles.Count, 0);
        var result = ForetellCollisionRasterizer.Build(snapshot);
        Check.That(result.Analysis.PassableCells > 40, "wall test produced no reachable floor");
        Check.That(result.Grid.IsConnectedPassable(new(-2, 0), result.Analysis.ConnectedCells) == true, "seed side was rejected");
        Check.That(result.Grid.IsConnectedPassable(new(2, 0), result.Analysis.ConnectedCells) == false, "vertical mesh wall was crossed");
    }

    private static void CollisionRasterBuildsCorridorAndRoom()
    {
        var triangles = new List<ForetellCollisionTriangle>();
        AddQuad(triangles, -3, -18, 3, 2, 0);
        AddQuad(triangles, -10, 2, 10, 16, 0);
        var snapshot = new ForetellCollisionSnapshot(new(0, .1f, -12), Vector2.Zero, 24, 1, triangles.ToArray(), 1, triangles.Count, 0);
        var result = ForetellCollisionRasterizer.Build(snapshot);
        Check.That(result.Grid.IsConnectedPassable(new(0, -10), result.Analysis.ConnectedCells) == true, "corridor missing");
        Check.That(result.Grid.IsConnectedPassable(new(8, 10), result.Analysis.ConnectedCells) == true, "room widening missing");
        Check.That(result.Grid.IsConnectedPassable(new(8, -10), result.Analysis.ConnectedCells) == false, "raster invented floor beside corridor");
    }

    private static void TopologyPresentationNeverHidesUnknownTerrain()
    {
        Check.That(ForetellInferenceCore.ShouldPresentOnTopology(true), "reachable alert segment was hidden");
        Check.That(ForetellInferenceCore.ShouldPresentOnTopology(null), "unknown terrain hid an alert");
        Check.That(!ForetellInferenceCore.ShouldPresentOnTopology(false), "confirmed unreachable segment was not clipped");
    }

    private static void AddQuad(List<ForetellCollisionTriangle> triangles, float minX, float minZ, float maxX, float maxZ, float y)
    {
        var a = new Vector3(minX, y, minZ);
        var b = new Vector3(maxX, y, minZ);
        var c = new Vector3(maxX, y, maxZ);
        var d = new Vector3(minX, y, maxZ);
        triangles.Add(new(a, b, c));
        triangles.Add(new(a, c, d));
    }

    private static void TopologyFrontierFindsClosedRoomEarly()
    {
        var grid = new ForetellTopologyGrid();
        grid.Reset(Vector3.Zero, 30, 1);
        var frontier = new ForetellTopologyFrontier();
        frontier.Start(grid, Vector2.Zero, Vector2.Zero, 30);
        var probes = 0;
        while (frontier.TryDequeue(grid, out var probe))
        {
            if (probe.Kind == TopologyProbeKind.Floor)
            {
                // Both sides of the synthetic room wall contain valid floor: only the horizontal collision edge
                // can stop the scanner, which models a corridor wall or pull barrier rather than a simple drop.
                grid.Set(probe.To, TopologyCell.Passable, 0);
                frontier.CommitFloor(grid, probe.To);
            }
            else
            {
                var insideFrom = InsideRoom(grid.CellCenter(probe.From));
                var insideTo = InsideRoom(grid.CellCenter(probe.To));
                frontier.CommitEdge(grid, probe.From, probe.To, insideFrom != insideTo);
            }
            ++probes;
            if (probes == 64)
                Check.That(frontier.Reachable >= 20, $"frontier did not publish a useful nearby surface early: {frontier.Reachable}");
            Check.That(probes < 1_000, "closed-room frontier regressed toward an exhaustive enclosing-disc scan");
        }

        var analysis = grid.Analyze(Vector2.Zero, requireKnownEdges: true);
        Check.That(frontier.Complete, "closed-room frontier did not terminate");
        Check.That(frontier.Reachable == analysis.PassableCells && analysis.PassableCells > 150,
            $"frontier/analysis component mismatch: {frontier.Reachable}/{analysis.PassableCells}");
        Check.That(frontier.Sampled < grid.CellCount / 4,
            $"closed room sampled too much unreachable terrain: {frontier.Sampled}/{grid.CellCount}");

        static bool InsideRoom(Vector2 point) => Math.Abs(point.X) < 10 && Math.Abs(point.Y) < 6;
    }

    private static void TopologyProbeFollowsReachedElevation()
    {
        var reference = ForetellTopologyProbeRules.FloorReferenceY(100, 0);
        Check.That(reference == 100, "frontier floor probe fell back to the actor's old elevation");
        Check.That(ForetellTopologyProbeRules.IsFloorHit(.9f, 101.2f, reference), "ordinary ascending terrain was rejected ahead of the actor");
        Check.That(!ForetellTopologyProbeRules.IsFloorHit(.9f, 103f, reference), "an excessive upward step was accepted");
        Check.That(!ForetellTopologyProbeRules.IsFloorHit(.2f, 100, reference), "a wall-like collision normal became walkable floor");
    }

    private static void DynamicTerrainSectorInference()
    {
        var center = Vector2.Zero;
        var peers = new[] { new Vector2(0, 15), new Vector2(15, 0), new Vector2(0, -15), new Vector2(-15, 0) };
        var sector = ForetellDynamicTerrainCore.BuildRadialSector(center, peers[1], peers, 5, out var width);
        Check.That(sector.Count == 14, "radial terrain sector was not reconstructed");
        Check.That(Math.Abs(width - MathF.PI / 2) < .01f, $"unexpected radial terrain width {width}");
        Check.That(Math.Abs(Vector2.Distance(center, sector[^1]) - 20) < .01f, "radial terrain outer edge is incorrect");
        Check.That(ForetellDynamicTerrainCore.BuildRadialSector(center, peers[1], peers[..2], 5, out _).Count == 0,
            "sparse event objects invented a terrain sector");
    }

    private static void TopologyBarrierClosesConnectedSurface()
    {
        var grid = new ForetellTopologyGrid();
        grid.Reset(Vector3.Zero, 4, 1);
        for (var z = 2; z <= 6; ++z)
            for (var x = 2; x <= 6; ++x)
                grid.Set(z * grid.Width + x, TopologyCell.Passable, 0);
        for (var z = 2; z <= 6; ++z)
            grid.SetEdge(z * grid.Width + 4, z * grid.Width + 5, blocked: true);

        var result = grid.Analyze(new(-1, 0));
        Check.That(result.PassableCells == 15, $"closed barrier leaked into {result.PassableCells} cells");
        Check.That(grid.IsConnectedPassable(new(2, 0), result.ConnectedCells) == false, "floor behind a closed barrier stayed connected");
        Check.That(result.KnownEdges.SequenceEqual(grid.KnownEdges) && result.BlockedEdges.SequenceEqual(grid.BlockedEdges), "barrier evidence was not snapshotted");
    }

    private static void TopologyRequiresObservedConnections()
    {
        var grid = new ForetellTopologyGrid();
        grid.Reset(Vector3.Zero, 4, 1);
        for (var z = 2; z <= 6; ++z)
            for (var x = 2; x <= 6; ++x)
                grid.Set(z * grid.Width + x, TopologyCell.Passable, 0);

        var seed = 4 * grid.Width + 4;
        var isolated = grid.Analyze(grid.CellCenter(seed), requireKnownEdges: true);
        Check.That(isolated.PassableCells == 1, "unobserved edges were treated as traversable");
        grid.SetEdge(seed, seed + 1, blocked: false);
        grid.SetEdge(seed + 1, seed + 2, blocked: false);
        var corridor = grid.Analyze(grid.CellCenter(seed), requireKnownEdges: true);
        Check.That(corridor.PassableCells == 3, $"observed local corridor had {corridor.PassableCells} cells");
        var restored = new ForetellTopologyGrid();
        Check.That(restored.Restore(grid.OriginX, grid.OriginZ, grid.ReferenceY, grid.Resolution, grid.Width, grid.Height,
            corridor.ConnectedCells, corridor.HeightCentimeters, corridor.KnownEdges, corridor.BlockedEdges), "valid observed edges did not restore");
        var asymmetric = corridor.KnownEdges.ToArray();
        asymmetric[seed] ^= (byte)TopologyEdge.West;
        Check.That(!restored.Restore(grid.OriginX, grid.OriginZ, grid.ReferenceY, grid.Resolution, grid.Width, grid.Height,
            corridor.ConnectedCells, corridor.HeightCentimeters, asymmetric, corridor.BlockedEdges), "asymmetric persisted edge evidence was accepted");
    }

    private static void PhaseClockAndHealthTriggerSelection()
    {
        var exactClock = ForetellInferenceCore.PhaseClockStability(4, 30, .25);
        var driftingClock = ForetellInferenceCore.PhaseClockStability(4, 30, 5);
        var exactHealth = ForetellInferenceCore.BossHealthStability(4, .005);
        Check.That(exactClock > .9f && driftingClock < exactClock, "phase-clock stability is not ordered");
        Check.That(exactHealth > .9f, "stable boss HP threshold was rejected");
        Check.That(!ForetellInferenceCore.PreferBossHealthTrigger(4, 30, .25, 4, .005), "coincidental HP correlation displaced a stable phase clock");
        Check.That(ForetellInferenceCore.PreferBossHealthTrigger(4, 30, 5, 4, .005), "stable HP threshold did not beat a drifting phase clock");
        Check.That(!ForetellInferenceCore.PreferBossHealthTrigger(4, 30, 5, 2, .001), "two HP samples created a predictive threshold");
        Check.That(ForetellInferenceCore.TriggerForecastConfidence(3, .95f, 0, 0) >= .8f, "stable three-pull trigger remained hidden");
        Check.That(ForetellInferenceCore.TriggerForecastConfidence(2, 1, 0, 0) == 0, "two-pull trigger escaped abstention");
        Check.That(ForetellInferenceCore.TriggerForecastConfidence(6, 1, 2, 1) < .5f, "poor verified trigger retained excessive confidence");
        Check.That(!ForetellInferenceCore.IsAbruptDisplacement(3.6f, .6), "normal running became forced movement");
        Check.That(ForetellInferenceCore.IsAbruptDisplacement(5f, .25), "abrupt knockback was ignored");
        Check.That(ForetellInferenceCore.IsPrioritySemanticObservation(ObservationKind.CastStart, SourceKind.Enemy), "boss casts have no reserved semantic budget");
        Check.That(ForetellInferenceCore.IsPrioritySemanticObservation(ObservationKind.ActionResolved, SourceKind.Enemy), "enemy instant actions have no reserved semantic budget");
        Check.That(!ForetellInferenceCore.IsPrioritySemanticObservation(ObservationKind.CastStart, SourceKind.Player), "player casts can consume the boss reserve");
        Check.That(!ForetellInferenceCore.IsPrioritySemanticObservation(ObservationKind.ActionResolved, SourceKind.Pet), "pet actions can consume the boss reserve");
        Check.That(ForetellInferenceCore.IsPrioritySemanticObservation(ObservationKind.StatusGain, SourceKind.Enemy), "enemy status mechanics have no reserved semantic budget");
        Check.That(ForetellInferenceCore.IsPrioritySemanticObservation(ObservationKind.StatusGain, SourceKind.Unknown), "actorless encounter statuses have no reserved semantic budget");
        Check.That(!ForetellInferenceCore.IsPrioritySemanticObservation(ObservationKind.StatusGain, SourceKind.Player), "player buffs can consume the boss reserve");
        Check.That(ForetellInferenceCore.IsPrioritySemanticObservation(ObservationKind.VFX, SourceKind.Enemy), "sparse target VFX have no reserved semantic budget");
        Check.That(!ForetellInferenceCore.IsPrioritySemanticObservation(ObservationKind.PositionSample, SourceKind.Player), "position samples can consume the priority reserve");
        Check.That(ForetellInferenceCore.ShouldSurfaceUnshapedCast(2.5f) && !ForetellInferenceCore.ShouldSurfaceUnshapedCast(2.49f), "unshaped cast visibility threshold is incorrect");
    }

    private static void GeometryValidationAndGuidance()
    {
        Check.That(ForetellInferenceCore.GeometryMatches(GeometryKind.Circle, 10, 0, GeometryKind.Circle, 11, 0), "near geometry was rejected");
        Check.That(!ForetellInferenceCore.GeometryMatches(GeometryKind.Circle, 10, 0, GeometryKind.Circle, 15, 0), "large geometry drift was accepted");
        Check.That(!ForetellInferenceCore.GeometryMatches(GeometryKind.Circle, 10, 0, GeometryKind.Cone, 10, 0), "different geometry families matched");
        Check.That(ForetellInferenceCore.GuidanceFor(MechanicKind.Stack) == GuidanceKind.Stack, "stack guidance mapping missing");
        Check.That(ForetellInferenceCore.GuidanceFor(MechanicKind.Proximity) == GuidanceKind.Move, "proximity guidance mapping missing");
        Check.That(ForetellInferenceCore.GuidanceFor(MechanicKind.Marker) == GuidanceKind.Marker, "unclassified target marker mapping missing");
        var stable = new ContextualMechanic { AnchorSamples = 3, AnchorForwardM2 = 1, AnchorSideM2 = 1 };
        var unstable = new ContextualMechanic { AnchorSamples = 3, AnchorForwardM2 = 30, AnchorSideM2 = 30 };
        Check.That(stable.AnchorStdDev < 3 && unstable.AnchorStdDev > 3, "anchor stability gate is incorrect");
        Check.That(ForetellInferenceCore.ConeHalfAngleCandidatesDegrees().Contains(135f), "270-degree cone cannot be learned");
        Check.That(!ForetellInferenceCore.GeometryParametersComplete(GeometryKind.Cone, 50, 0), "angle-less cone became drawable geometry");
        Check.That(ForetellInferenceCore.GeometryParametersComplete(GeometryKind.Cone, 50, 135f * MathF.PI / 180), "wide cone was rejected");
    }

    private static void ActionMetadataSafetyFamilies()
    {
        Check.That(ForetellInferenceCore.IsGazeActionVFX(25), "client gaze VFX was not recognized");
        Check.That(!ForetellInferenceCore.IsGazeActionVFX(24), "non-gaze VFX became LOOK AWAY");
        Check.That(ForetellInferenceCore.IsAmbiguousLargeCircleAction(2, 50, false, 0), "arena-scale CastType 2 became a lethal circle");
        Check.That(ForetellInferenceCore.IsAmbiguousLargeCircleAction(5, 30, false, 0), "arena-scale CastType 5 became a lethal circle");
        Check.That(!ForetellInferenceCore.IsAmbiguousLargeCircleAction(2, 6, false, 0), "ordinary small circle was suppressed");
        Check.That(!ForetellInferenceCore.IsAmbiguousLargeCircleAction(2, 50, true, 0), "explicit target-area circle was suppressed");
        Check.That(!ForetellInferenceCore.IsAmbiguousLargeCircleAction(2, 50, false, 123), "Omen-backed circle was suppressed");
        Check.That(ForetellInferenceCore.IsReliableSpatialActionPrior(MechanicKind.GroundAOE, GeometryKind.Rectangle, .91f, 35, 1.5f),
            "complete Action rectangle was not protected");
        Check.That(!ForetellInferenceCore.IsReliableSpatialActionPrior(MechanicKind.GroundAOE, GeometryKind.Cone, .64f, 50, 0),
            "incomplete Action cone was treated as authoritative");

        var protectedPrior = new ContextualMechanic
        {
            Kind = MechanicKind.Debuff,
            Geometry = GeometryKind.Unknown,
            Observations = 10,
            AmbiguousSamples = 10,
            PriorKind = MechanicKind.GroundAOE,
            PriorGeometry = GeometryKind.Rectangle,
            PriorP1 = 35,
            PriorP2 = 1.5f,
            PriorConfidence = .91f,
            Forecasts = 10,
            ForecastMisses = 10
        };
        Check.That(protectedPrior.Confidence >= .91f, "ambient outcomes suppressed a reliable Action prior");
        Check.That(protectedPrior.GuidanceConfidence >= .91f, "contaminated forecast counters suppressed a reliable Action telegraph");
    }

    private static void ArenaBoundaryInference()
    {
        const int rays = 64;
        var circular = Enumerable.Repeat(20f, rays).ToArray();
        var hits = Enumerable.Repeat(true, rays).ToArray();
        var arena = ForetellArenaBoundaryCore.Analyze(new(100, 200), 5, circular, hits, 42);
        Check.That(arena.ArenaLike && arena.Points.Count == rays, "enclosed compact arena was not recognized");
        Check.That(ForetellArenaBoundaryCore.Contains(arena.Points, new(100, 200)), "arena center was outside learned boundary");
        Check.That(!ForetellArenaBoundaryCore.Contains(arena.Points, new(150, 200)), "outside point entered learned boundary");

        var corridor = new float[rays];
        for (var i = 0; i < rays; ++i)
        {
            var angle = MathF.Tau * i / rays;
            var x = Math.Abs(MathF.Sin(angle));
            var z = Math.Abs(MathF.Cos(angle));
            corridor[i] = Math.Min(x < .001f ? 42 : 4 / x, z < .001f ? 42 : 30 / z);
        }
        var hallway = ForetellArenaBoundaryCore.Analyze(Vector2.Zero, 0, corridor, hits, 42);
        Check.That(!hallway.ArenaLike && hallway.AspectRatio > 2.6f, "long corridor was classified as a boss arena");
        var partialDistances = Enumerable.Repeat(20f, rays).ToArray();
        var partialHits = Enumerable.Repeat(true, rays).ToArray();
        for (var i = 0; i < 12; ++i)
        {
            partialHits[i] = false;
            partialDistances[i] = 42;
        }
        var partial = ForetellArenaBoundaryCore.Analyze(Vector2.Zero, 0, partialDistances, partialHits, 42);
        Check.That(!partial.ArenaLike, "partial wall fan became an expanding arena boundary");
        Check.That(!ForetellArenaBoundaryCore.IsBossCandidate(2_200, 2_200, 2_000, 1), "ordinary trash was classified as a boss");
        Check.That(ForetellArenaBoundaryCore.IsBossCandidate(12_000, 12_000, 2_000, 2), "high-health boss was not recognized");
        Check.That(!ForetellArenaBoundaryCore.IsBossCandidate(2_000, 12_000, 2_000, 2), "boss add was classified as the boss");
    }

    private static void OutOfCombatHazardContextIsScoped()
    {
        Check.That(ForetellInferenceCore.TimelinePhase(false, 4) == ForetellInferenceCore.OutOfCombatHazardPhase, "out-of-combat signals leaked into a boss phase");
        Check.That(ForetellInferenceCore.TimelinePhase(true, 4) == 4, "combat phase was not retained");
        Check.That(ForetellInferenceCore.OpensOutOfCombatHazardContext(ObservationKind.CastStart, SourceKind.Enemy, 10, 0), "enemy cast did not open hazard context");
        Check.That(ForetellInferenceCore.OpensOutOfCombatHazardContext(ObservationKind.MapEffect, SourceKind.Environment, 0, 0), "environmental hazard did not open hazard context");
        Check.That(!ForetellInferenceCore.OpensOutOfCombatHazardContext(ObservationKind.CastStart, SourceKind.Player, 10, 0), "player cast opened hazard context");
        Check.That(!ForetellInferenceCore.OpensOutOfCombatHazardContext(ObservationKind.NativeVFXSpawn, SourceKind.Environment, 0, 0), "unbound native VFX opened hazard context");
        Check.That(!ForetellInferenceCore.OpensOutOfCombatHazardContext(ObservationKind.ActorControlRaw, SourceKind.Unknown, 0, 0), "unbound actor control opened hazard context");
    }

    private static void MechanicSourcesExcludePartyActions()
    {
        Check.That(!ForetellInferenceCore.CanStartMechanicEpisode(ObservationKind.CastStart, SourceKind.Player, 10, 0), "player cast became a mechanic episode");
        Check.That(!ForetellInferenceCore.CanStartMechanicEpisode(ObservationKind.CastStart, SourceKind.Pet, 11, 123), "pet cast became a mechanic episode");
        Check.That(!ForetellInferenceCore.CanStartMechanicEpisode(ObservationKind.CastStart, SourceKind.Environment, 0, 0), "actorless cast became an environment mechanic");
        Check.That(!ForetellInferenceCore.CanStartMechanicEpisode(ObservationKind.CastStart, SourceKind.Unknown, 30, 456), "unknown actor became a mechanic source");
        Check.That(ForetellInferenceCore.CanStartMechanicEpisode(ObservationKind.CastStart, SourceKind.Enemy, 30, 456), "enemy cast was rejected");
        Check.That(ForetellInferenceCore.CanStartMechanicEpisode(ObservationKind.MapEffect, SourceKind.Environment, 0, 0), "arena map effect was rejected");
        Check.That(ForetellInferenceCore.CanStartMechanicEpisode(ObservationKind.Icon, SourceKind.Environment, 0, 0, 99), "target-attached encounter marker was rejected");
        Check.That(!ForetellInferenceCore.CanStartMechanicEpisode(ObservationKind.Icon, SourceKind.Environment, 0, 0), "unbound environment icon became a mechanic");
        Check.That(!ForetellInferenceCore.CanStartMechanicEpisode(ObservationKind.DirectorUpdate, SourceKind.Environment, 0, 0), "duty state became a mechanic");
        Check.That(!ForetellInferenceCore.CanStartMechanicEpisode(ObservationKind.EventObjectState, SourceKind.EventObject, 40, 789), "door/key state became a mechanic");
        Check.That(!ForetellInferenceCore.CanStartMechanicEpisode(ObservationKind.ActorControlRaw, SourceKind.Enemy, 30, 456), "raw actor control became a mechanic");
        Check.That(ForetellInferenceCore.CanStartMechanicEpisode(ObservationKind.ObjectEffect, SourceKind.EventObject, 40, 789), "explicit event-object effect was rejected");
        Check.That(!ForetellInferenceCore.IsMechanicOutcomeEvidence(ObservationKind.ActionResolved, SourceKind.Player), "player action became outcome evidence");
        Check.That(ForetellInferenceCore.IsMechanicOutcomeEvidence(ObservationKind.Displacement, SourceKind.Player), "player displacement was lost as knockback evidence");
        Check.That(ForetellInferenceCore.IsMechanicOutcomeEvidence(ObservationKind.DeathChanged, SourceKind.Player), "player death was lost as lethal evidence");
        Check.That(!ForetellInferenceCore.ShouldUseFastArenaBoundary(false), "out-of-combat radial walls replaced the terrain mesh");
        Check.That(ForetellInferenceCore.ShouldUseFastArenaBoundary(true), "combat arena acceleration was disabled");
        Check.That(!ForetellInferenceCore.ShouldReplaceTopologyAnalysis(120, 8, false), "tiny progressive rescan replaced a useful topology");
        Check.That(ForetellInferenceCore.ShouldReplaceTopologyAnalysis(120, 132, false), "materially growing progressive topology was rejected");
        Check.That(ForetellInferenceCore.ShouldReplaceTopologyAnalysis(120, 40, true), "completed structural shrink was rejected");
        Check.That(!ForetellInferenceCore.ShouldReplaceTopologyAnalysis(120, 0, true), "empty completed scan erased a useful topology");
    }

    private static void RadarIsCameraRelative()
    {
        var northCamera = ForetellInferenceCore.CameraRelativeRadarOffset(new(0, -10), 0);
        Check.That(Vector2.Distance(northCamera, new(0, -10)) < .001f, "north-facing camera was not radar-up");
        var eastCamera = ForetellInferenceCore.CameraRelativeRadarOffset(new(10, 0), -MathF.PI * .5f);
        Check.That(Vector2.Distance(eastCamera, new(0, -10)) < .001f, "east-facing camera was not radar-up");
        var right = ForetellInferenceCore.CameraRelativeRadarOffset(new(10, 0), 0);
        Check.That(Vector2.Distance(right, new(10, 0)) < .001f, "camera-relative right was not radar-right");
    }

    private static void StorageMaintenanceProtectsActiveFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"foretell-storage-{Guid.NewGuid():N}");
        var raw = Path.Combine(root, "raw");
        var replay = Path.Combine(root, "replay");
        Directory.CreateDirectory(raw);
        Directory.CreateDirectory(replay);
        try
        {
            var old = Path.Combine(raw, "old.ftraw.gz");
            var active = Path.Combine(raw, "active.ftraw.gz");
            var recent = Path.Combine(replay, "recent.jsonl");
            File.WriteAllBytes(old, new byte[128]);
            File.WriteAllBytes(active, new byte[128]);
            File.WriteAllBytes(recent, new byte[128]);
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-60));
            File.SetLastWriteTimeUtc(active, DateTime.UtcNow.AddDays(-60));
            var result = ForetellStorageMaintenance.Run(raw, replay, [active], DateTime.UtcNow, 30, 1024 * 1024);
            Check.That(string.IsNullOrEmpty(result.Error) && result.Deleted == 1, "retention cleanup result is incorrect");
            Check.That(!File.Exists(old) && File.Exists(active) && File.Exists(recent), "storage cleanup deleted the wrong file");

            var quotaOld = Path.Combine(raw, "quota-old.ftraw.gz");
            var quotaNew = Path.Combine(replay, "quota-new.jsonl");
            File.WriteAllBytes(quotaOld, new byte[700 * 1024]);
            File.WriteAllBytes(quotaNew, new byte[700 * 1024]);
            File.SetLastWriteTimeUtc(quotaOld, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(quotaNew, DateTime.UtcNow.AddHours(-1));
            result = ForetellStorageMaintenance.Run(raw, replay, [active], DateTime.UtcNow, 30, 1024 * 1024);
            Check.That(result.Deleted == 1 && !File.Exists(quotaOld) && File.Exists(quotaNew), "quota cleanup did not remove the oldest inactive recording");
            Check.That(File.Exists(active), "quota cleanup deleted the protected active recording");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void DecisionAuditRoundTrip()
    {
        var store = new ForetellStore();
        store.Sessions.Add(new() { SessionID = "test-session", PluginVersion = "0.8.9.0", TerritoryID = 192 });
        store.DecisionAudit.Add(new()
        {
            At = DateTime.UtcNow,
            Activation = DateTime.UtcNow.AddSeconds(2),
            SessionID = "test-session",
            TerritoryID = 192,
            PredictionID = 42,
            Stage = DecisionAuditStage.Proposed,
            SignalKey = "CastStart:123",
            TriggerKind = ObservationKind.CastStart,
            SourceKind = SourceKind.Enemy,
            SourceOID = 0xBEEF,
            Mechanic = MechanicKind.GroundAOE,
            Geometry = GeometryKind.Cone,
            Guidance = GuidanceKind.Avoid,
            P1 = 20,
            P2 = .5f,
            Confidence = .91f,
            DisplayEligible = true,
            Label = "Test cone"
        });
        store.Encounters[192] = new() { TerritoryID = 192 };
        store.Encounters[192].TriggerContexts["BEEF:0:1:BEEF:CastStart:123"] = new()
        {
            Key = "BEEF:0:1:BEEF:CastStart:123",
            Signal = "BEEF:CastStart:123",
            Phase = 0,
            Occurrence = 1,
            ContextOID = 0xBEEF,
            BossOID = 0xBEEF,
            Samples = 4,
            MeanPhaseSeconds = 12.5,
            HealthSamples = 4,
            MeanBossHPRatio = .7
        };
        var copy = JsonSerializer.Deserialize<ForetellStore>(JsonSerializer.Serialize(store));
        Check.That(copy?.Schema == 22 && copy.DecisionAudit.Count == 1, "decision audit schema/list did not round-trip");
        Check.That(copy!.Sessions.Single().PluginVersion == "0.8.9.0", "session plugin-version provenance did not round-trip");
        var entry = copy!.DecisionAudit[0];
        Check.That(entry.Stage == DecisionAuditStage.Proposed && entry.Geometry == GeometryKind.Cone
            && entry.DisplayEligible && entry.SessionID == "test-session", "decision audit fields did not round-trip");
        var trigger = copy.Encounters[192].TriggerContexts.Single().Value;
        Check.That(trigger.Samples == 4 && trigger.HealthSamples == 4 && Math.Abs(trigger.MeanBossHPRatio - .7) < .0001,
            "time/HP trigger memory did not round-trip");
    }
}
