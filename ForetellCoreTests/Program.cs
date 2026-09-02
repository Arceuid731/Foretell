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
        RawSchemaOneCompatibility();
        RawRejectsCorruption();
        RawRejectsTruncation();
        RawFuzzDoesNotEscape();
        ClassifierMigrationPreservesFeatureFamilies();
        ClassifierRejectsNonFiniteStateAndInput();
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
}
