using BossMod.Foretell;
using System.IO.Compression;
using System.Text.Json;

internal static class GuideCaptureTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static GuideCaptureInput Input(ForetellCapture.Session session, string summary = "Écarte-toi si marqué.")
    {
        var mechanic = new GuideMechanic("Storm", "If marked, spread out.", "Storm");
        var phase = new GuidePhase("Phase 1", "If protected, do not move.", [mechanic]);
        var boss = new GuideBoss("Boss", "Boss", [phase]);
        var document = new GuideDocument(1, new(42, session.Territory, "Fixture"), "Fixture", 123, DateTime.UtcNow, GuideNames.Hash("fixture-source"), [boss]);
        return new(DateTime.UtcNow, session.ID, session.Territory, document.Duty, GuideLanguage.French, document,
            new("Ready", "", true, .2, "Ready", 1, 1, boss.Name, false, false, true),
            new(ForetellMode.Hybrid, true, true, true, false, true, true, true, false, 1, 1.4f),
            [new(boss.Name, "Boss FR", 50, [new(phase.Name, "Si protégé, ne bouge pas.", true,
                [new(mechanic.Name, "Tempête", 51, ForetellGuideSummaries.Key(boss, phase, mechanic), summary, GuidanceKind.None, "À VÉRIFIER", false, mechanic.Rules, null)])])],
            [new(boss.Name, phase.Name, mechanic.Name, GuideSignalKind.Cast, 100, 200, 50, 51, 0, 300, DateTime.UtcNow.AddSeconds(3), GuidanceKind.None, "À VÉRIFIER", "fixture exact-name association")]);
    }

    private static string Export(string root, string name, ForetellCapture.Snapshot snapshot, string sessionID)
    {
        var path = Path.Combine(root, name);
        var work = new ForetellEngine.AnalysisBundleWork(path, "{}"u8.ToArray(), [], null, Task.FromResult<ForetellCapture.Snapshot?>(snapshot),
            [], 1, "fixture", sessionID, "capture-version", "export-version", null);
        var result = ForetellEngine.CreateAnalysisBundle(work, 0);
        Check(result.Error.Length == 0, "Guide bundle export failed: " + result.Error);
        return path;
    }

    private static JsonDocument Read(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        if (!name.EndsWith(".gz", StringComparison.Ordinal)) return JsonDocument.Parse(stream);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }

    public static void Run(string root, List<ForetellObservation> events, string expectedDigest)
    {
        var directory = Path.Combine(root, "guide-capture");
        string firstDirectory;
        using (var capture = new ForetellCapture(directory))
        {
            var first = capture.NewSession(1, "first", "capture-version"); firstDirectory = first.Directory;
            foreach (var item in events) capture.Enqueue(first, item);
            Check(capture.EnqueueGuide(first, Input(first)), "Initial guide snapshot rejected");
            using var original = capture.SnapshotAsync(first.Directory).GetAwaiter().GetResult()!;
            Check(original.Guides.Length == 2, "Source and adaptation were not captured");
            var zip = Export(root, "with-guides.zip", original, first.ID);
            using (var archive = ZipFile.OpenRead(zip))
            {
                using var index = Read(archive, "guides/index.json");
                Check(index.RootElement.GetProperty("availability").GetString() == "captured" && index.RootElement.GetProperty("complete").GetBoolean(), "Guide availability is wrong");
                using var source = Read(archive, "guides/" + original.Guides.Single(file => file.Kind == "source").File);
                Check(source.RootElement.GetProperty("Revision").GetInt32() == 123, "Source revision missing");
                using var adapted = Read(archive, "guides/" + original.Guides.Single(file => file.Kind == "adapted").File);
                var row = adapted.RootElement.GetProperty("Adapted")[0].GetProperty("Phases")[0].GetProperty("Mechanics")[0];
                Check(row.GetProperty("Summary").GetString() == "Écarte-toi si marqué." && row.GetProperty("ActionID").GetUInt32() == 51
                    && row.GetProperty("Instruction").GetString() == "À VÉRIFIER", "Adapted text, contextual ID or abstention missing");
                Check(adapted.RootElement.GetProperty("Signals")[0].GetProperty("SourceID").GetUInt64() == 100, "Live association missing");
            }
            var reader = new ForetellRecordingReader(zip); reader.Inspect();
            Check(reader.Parsed == events.Count && reader.Complete, "Guide files changed observation completeness");
            Check(ForetellEngine.EvaluateRecordedStream(reader.Read(), captureComplete: reader.Complete).Report.DecisionDigest == expectedDigest, "Guide prose contaminated replay learning");
            capture.EnqueueGuide(first, Input(first, "New summary"));
            using var updated = capture.SnapshotAsync(first.Directory).GetAwaiter().GetResult()!;
            Check(updated.Guides.Length == 3 && original.Guides.Length == 2, "Later adaptation mutated a sealed snapshot or duplicated source");
            var second = capture.NewSession(1, "second", "capture-version");
            capture.Enqueue(second, events[0]);
            using var other = capture.SnapshotAsync(second.Directory).GetAwaiter().GetResult()!;
            Check(other.Guides.Length == 0, "Another run of the same duty inherited historical guides");
            var missing = Export(root, "missing-guides.zip", other, second.ID);
            using (var archive = ZipFile.OpenRead(missing))
            using (var index = Read(archive, "guides/index.json"))
                Check(index.RootElement.GetProperty("availability").GetString() == "unavailable" && !index.RootElement.GetProperty("complete").GetBoolean(), "Old/missing guide evidence was hidden");
            Check(!capture.EnqueueGuide(second, Input(first)), "Foreign-session guide accepted");
            Check(!capture.EnqueueGuide(second, Input(second) with { TerritoryID = 2 }), "Foreign-territory guide accepted");
        }
        using (var reopened = new ForetellCapture(directory))
        {
            using var snapshot = reopened.SnapshotAsync(firstDirectory).GetAwaiter().GetResult()!;
            Check(snapshot.Guides.Length == 3, "Historical guides lost after leaving/restarting");
            var artifact = snapshot.Guides.First();
            var data = File.ReadAllBytes(Path.Combine(firstDirectory, artifact.File)); data[^1] ^= 1;
            File.WriteAllBytes(Path.Combine(firstDirectory, artifact.File), data);
            var zip = Export(root, "corrupt-guides.zip", snapshot, "first");
            using var archive = ZipFile.OpenRead(zip);
            using var index = Read(archive, "guides/index.json");
            Check(!index.RootElement.GetProperty("complete").GetBoolean() && archive.GetEntry("guides/" + artifact.File) == null, "Corrupted source was exported as valid");
            try { ForetellEngine.ReadGuideArtifact(firstDirectory, artifact with { File = "../escape.json.gz" }); throw new Exception("Traversal accepted"); }
            catch (InvalidDataException) { }
        }
        using (var capped = new ForetellCapture(Path.Combine(root, "guide-cap")))
        {
            var session = capped.NewSession(1, "bounded", "capture-version");
            for (var count = 0; count < 130; ++count) capped.EnqueueGuide(session, Input(session));
            using var snapshot = capped.SnapshotAsync(session.Directory).GetAwaiter().GetResult()!;
            Check(snapshot.Guides.Length <= 129 && session.GuideRejected > 0 && session.Rejected == 0, "Guide bound lost or broke decision capture");
        }
        Console.WriteLine("Session guide/source/adaptation export, historical isolation, immutable barriers, replay neutrality, hashes and quotas passed.");
    }
}
