using System.IO.Compression;
using System.Text.Json;
using BossMod.Foretell;

internal static class CaptureTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static void Run(List<ForetellObservation> events, string expectedDigest)
    {
        var root = Path.Combine(Path.GetTempPath(), "foretell-capture-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // Intentionally no Dalamud Service initialization and no RecordReplay option.
        using (var capture = new ForetellCapture(Path.Combine(root, "automatic")))
        {
            var session = capture.NewSession(1, "fixture", "test");
            var context = events.First().Context;
            foreach (var item in events)
            {
                var copy = item.CopyForRecording(); copy.Context = context;
                capture.Enqueue(session, copy);
                copy.X = 900; copy.Numeric.Clear(); // the background capture must own the original primitives
            }
            using var snapshot = capture.SnapshotAsync(session.Directory).GetAwaiter().GetResult()!;
            Check(snapshot != null && snapshot.Parts.Length == 1, "Live capture barrier did not seal its input");
            var zip = Package(root, "first.zip", snapshot!);
            var reader = new ForetellRecordingReader(zip); reader.Inspect();
            Check(reader.Complete && reader.Parsed == events.Count, "Automatic ZIP lost events with readable recording off");
            var result = ForetellEngine.EvaluateRecordedStream(reader.Read(), captureComplete: reader.Complete);
            Check(result.Report.DecisionDigest == expectedDigest && result.Report.Assessed == 1,
                "Compressed streaming replay changed real engine decisions");

            // Export while active, then continue in another independently decodable part.
            var continued = events.Last().CopyForRecording(); continued.At = continued.At.AddSeconds(30); continued.Context = context;
            capture.Enqueue(session, continued);
            using var second = capture.SnapshotAsync(session.Directory).GetAwaiter().GetResult()!;
            Check(second.Parts.Length == 2, "Recording did not continue after export");
            var secondPart = new ForetellRecordingReader(Path.Combine(second.Directory, second.Parts[1]));
            Check(secondPart.Read().First().Context != null, "New part did not repeat its referenced world context");
            reader.Inspect(); Check(reader.Parsed == events.Count, "A later capture changed the earlier ZIP");

            var oversized = continued.CopyForRecording(); oversized.Text["huge"] = new string('x', 600_000);
            capture.Enqueue(session, oversized);
            capture.Enqueue(session, continued);
            using var partial = capture.SnapshotAsync(session.Directory).GetAwaiter().GetResult()!;
            var partialReader = new ForetellRecordingReader(Package(root, "partial.zip", partial)); partialReader.Inspect();
            Check(!partialReader.Complete && partialReader.Rejected == 1, "A rejected event was silently reported as complete");
            var partialResult = ForetellEngine.EvaluateRecordedStream(partialReader.Read(), captureComplete: partialReader.Complete);
            Check(partialResult.Report.Assessed == 0 && partialResult.Report.MissingContexts > 0, "Incomplete recording validated outcomes");
            var corrupted = Path.Combine(partial.Directory, partial.Parts[0]);
            using (var file = new FileStream(corrupted, FileMode.Open, FileAccess.Write)) file.SetLength(new FileInfo(corrupted).Length - 4);
            try { new ForetellRecordingReader(partial.Directory).Inspect(); throw new Exception("Truncated gzip trailer was accepted"); }
            catch (InvalidDataException) { }
        }
        TestQuota(root, events[0]);
        TestReaderBounds(root);
        Console.WriteLine("Automatic capture tests passed: immutable live ZIP, replay parity, repeated context, missing evidence, quotas, retention and reader bounds.");
        // Only known temporary files created by this test; validate the absolute root before recursive cleanup.
        var full = Path.GetFullPath(root);
        Check(full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(full).StartsWith("foretell-capture-tests-", StringComparison.Ordinal), "Unsafe test cleanup target");
        Directory.Delete(full, recursive: true);
    }

    private static string Package(string root, string name, ForetellCapture.Snapshot snapshot)
    {
        var path = Path.Combine(root, name);
        var supplemental = Path.Combine(root, "large-supplement.ftraw.gz");
        using (var file = new FileStream(supplemental, FileMode.OpenOrCreate, FileAccess.Write)) file.SetLength(129L * 1024 * 1024);
        var work = new ForetellEngine.AnalysisBundleWork(path, "{}"u8.ToArray(), [supplemental], null,
            Task.FromResult<ForetellCapture.Snapshot?>(snapshot), [], 1, "fixture", "fixture", "test", "test", null);
        var result = ForetellEngine.CreateAnalysisBundle(work, 0);
        Check(result.Error.Length == 0 && File.Exists(path), "Real Analysis ZIP export failed: " + result.Error);
        using var archive = ZipFile.OpenRead(path);
        Check(archive.GetEntry("capture/index.json") != null && archive.GetEntry("raw/large-supplement.ftraw.gz") == null,
            "The real exporter omitted automatic capture or exceeded the budget with supplemental raw data");
        using var manifestStream = archive.GetEntry("manifest.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        Check(manifest.RootElement.GetProperty("warnings").GetArrayLength() > 0, "Supplemental omission was not disclosed");
        return path;
    }

    private static void TestQuota(string root, ForetellObservation template)
    {
        var cache = Path.Combine(root, "quota");
        const int sessionLimit = 256 * 1024, cacheLimit = 512 * 1024;
        using var capture = new ForetellCapture(cache, sessionLimit, cacheLimit, segmentLimit: 64 * 1024);
        var random = new Random(1);
        ForetellCapture.Snapshot? pinned = null;
        string firstDirectory = "";
        for (var run = 0; run < 6; ++run)
        {
            var session = capture.NewSession(1, "quota-" + run, "test");
            for (var n = 0; n < 32; ++n)
            {
                var item = template.CopyForRecording();
                var bytes = new byte[24 * 1024]; random.NextBytes(bytes);
                item.Text["random"] = Convert.ToBase64String(bytes);
                capture.Enqueue(session, item);
            }
            var snapshot = capture.SnapshotAsync(session.Directory).GetAwaiter().GetResult()!;
            Check(session.Capped != 0 && session.Rejected > 0, "Capture quota did not stop a full session");
            Check(Directory.EnumerateFiles(session.Directory).Sum(p => new FileInfo(p).Length) <= sessionLimit,
                "Session exceeded its reserved compressed budget");
            Check(Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length) <= cacheLimit,
                "Automatic cache exceeded its reserved budget");
            if (run == 0) { pinned = snapshot; firstDirectory = snapshot.Directory; }
            else snapshot.Dispose();
            Check(Directory.Exists(firstDirectory), "Cache retention deleted a capture leased by export/evaluation");
        }
        pinned!.Dispose();
        // A new session resumes after the old session cap, and expired unpinned data is reclaimed.
        Directory.SetLastWriteTimeUtc(firstDirectory, DateTime.UtcNow.AddDays(-20));
        var last = capture.NewSession(1, "after-limit", "test"); capture.Enqueue(last, template);
        using var final = capture.SnapshotAsync(last.Directory).GetAwaiter().GetResult();
        Check(last.Written == 1 && !Directory.Exists(firstDirectory), "Retention or new-session recovery failed");
    }

    private static void TestReaderBounds(string root)
    {
        var path = Path.Combine(root, "oversized.jsonl");
        File.WriteAllText(path, new string('x', 1024 * 1024 + 1));
        try { new ForetellRecordingReader(path).Inspect(); throw new Exception("Oversized line was accepted"); }
        catch (InvalidDataException) { }
        var missing = Path.Combine(root, "missing"); Directory.CreateDirectory(missing);
        File.WriteAllText(Path.Combine(missing, "index.json"), "{\"schema\":1,\"complete\":true,\"parts\":[\"missing.jsonl.gz\"],\"hashes\":{\"missing.jsonl.gz\":\"0000\"}}");
        try { new ForetellRecordingReader(missing).Inspect(); throw new Exception("Missing compressed part was accepted"); }
        catch (FileNotFoundException) { }
    }
}
