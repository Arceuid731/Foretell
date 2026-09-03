using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
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
        TopologyLoadBudget();
        TopologyFuzzMaintainsInvariants();
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
        GeometryValidationAndGuidance();
        MechanicSourcesExcludePartyActions();
        OutOfCombatHazardContextIsScoped();
        StorageMaintenanceProtectsActiveFiles();
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

    private static void GeometryValidationAndGuidance()
    {
        Check.That(ForetellInferenceCore.GeometryMatches(GeometryKind.Circle, 10, 0, GeometryKind.Circle, 11, 0), "near geometry was rejected");
        Check.That(!ForetellInferenceCore.GeometryMatches(GeometryKind.Circle, 10, 0, GeometryKind.Circle, 15, 0), "large geometry drift was accepted");
        Check.That(!ForetellInferenceCore.GeometryMatches(GeometryKind.Circle, 10, 0, GeometryKind.Cone, 10, 0), "different geometry families matched");
        Check.That(ForetellInferenceCore.GuidanceFor(MechanicKind.Stack) == GuidanceKind.Stack, "stack guidance mapping missing");
        Check.That(ForetellInferenceCore.GuidanceFor(MechanicKind.Proximity) == GuidanceKind.Move, "proximity guidance mapping missing");
        var stable = new ContextualMechanic { AnchorSamples = 3, AnchorForwardM2 = 1, AnchorSideM2 = 1 };
        var unstable = new ContextualMechanic { AnchorSamples = 3, AnchorForwardM2 = 30, AnchorSideM2 = 30 };
        Check.That(stable.AnchorStdDev < 3 && unstable.AnchorStdDev > 3, "anchor stability gate is incorrect");
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
        Check.That(!ForetellInferenceCore.IsMechanicOutcomeEvidence(ObservationKind.ActionResolved, SourceKind.Player), "player action became outcome evidence");
        Check.That(ForetellInferenceCore.IsMechanicOutcomeEvidence(ObservationKind.Displacement, SourceKind.Player), "player displacement was lost as knockback evidence");
        Check.That(ForetellInferenceCore.IsMechanicOutcomeEvidence(ObservationKind.DeathChanged, SourceKind.Player), "player death was lost as lethal evidence");
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
}
