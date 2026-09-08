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
        var document = new GuideDocument(1, new(42, session.Territory, "Fixture"), "Fixture", 123, DateTime.UtcNow, GuideNames.Hash("fixture-source"), [boss])
        {
            Page = new("Fixture provider", "https://example.org/fixture", "Complete source with all conditions.", "<p>Complete source with all conditions.</p>"),
            Sources = [new("Community Workbook", "https://docs.google.com/spreadsheets/d/fixture/edit", "Full worksheet text", "Full original worksheet", "xlsx")],
            Providers = [new("Community Workbook", "Ready", "https://docs.google.com/spreadsheets/d/fixture/edit")],
            ModelRevision = "fixture-model-revision"
        };
        return new(DateTime.UtcNow, session.ID, session.Territory, document.Duty, GuideLanguage.French, document,
            new("Ready", "", true, .2, "Ready", 1, 1, boss.Name, false, false, true)
            { ModelRuntime = new(GuideModelStage.Unloaded, null, "CPU", 16384, 8000, true), SummaryIssue = "Earlier excerpt exceeded context" },
            new(ForetellMode.Hybrid, true, true, true, false, true, true, true, false, 1, 1.4f) { ContextTokens = 16384, MemoryGiB = 6 },
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
            Check(original.Guides.Length == 3, "Source, adaptation and timeline were not captured");
            using (var captureIndex = JsonDocument.Parse(original.Index))
            {
                var guideCapture = captureIndex.RootElement.GetProperty("guideCapture");
                Check(!guideCapture.TryGetProperty("timelineOmitted", out var omitted) || omitted.GetInt64() == 0,
                    "Initial timeline unexpectedly omitted samples");
            }
            var zip = Export(root, "with-guides.zip", original, first.ID);
            using (var archive = ZipFile.OpenRead(zip))
            {
                using var index = Read(archive, "guides/index.json");
                Check(index.RootElement.GetProperty("availability").GetString() == "captured" && index.RootElement.GetProperty("complete").GetBoolean(), "Guide availability is wrong");
                using var source = Read(archive, "guides/" + original.Guides.Single(file => file.Kind == "source").File);
                Check(source.RootElement.GetProperty("Revision").GetInt32() == 123, "Source revision missing");
                Check(source.RootElement.GetProperty("Sources")[0].GetProperty("Original").GetString() == "Full original worksheet"
                    && source.RootElement.GetProperty("Providers")[0].GetProperty("Status").GetString() == "Ready", "Aggregated originals or provider state missing from export");
                Check(source.RootElement.GetProperty("Page").GetProperty("Text").GetString() == "Complete source with all conditions."
                    && source.RootElement.GetProperty("Page").GetProperty("Html").GetString()!.StartsWith("<p>"), "Full guide content not exported");
                using var adapted = Read(archive, "guides/" + original.Guides.Single(file => file.Kind == "adapted").File);
                Check(adapted.RootElement.GetProperty("modelRevision").GetString() == "fixture-model-revision", "Adaptation exported the default model instead of the actual one");
                var row = adapted.RootElement.GetProperty("Adapted")[0].GetProperty("Phases")[0].GetProperty("Mechanics")[0];
                Check(row.GetProperty("Summary").GetString() == "Écarte-toi si marqué." && row.GetProperty("ActionID").GetUInt32() == 51
                    && row.GetProperty("Instruction").GetString() == "À VÉRIFIER", "Adapted text, contextual ID or abstention missing");
                Check(adapted.RootElement.GetProperty("Signals")[0].GetProperty("SourceID").GetUInt64() == 100, "Live association missing");
                var state = adapted.RootElement.GetProperty("State");
                Check(state.GetProperty("ModelRuntime").GetProperty("ProcessID").ValueKind == JsonValueKind.Null
                    && state.GetProperty("ModelRuntime").GetProperty("PromptTokens").GetInt32() == 8000
                    && state.GetProperty("SummaryIssue").GetString() == "Earlier excerpt exceeded context", "Session-time model activity/context failure missing");
                Check(adapted.RootElement.GetProperty("Options").GetProperty("ContextTokens").GetInt32() == 16384
                    && adapted.RootElement.GetProperty("Options").GetProperty("MemoryGiB").GetInt32() == 6, "Session resource settings missing");
            }
            var reader = new ForetellRecordingReader(zip); reader.Inspect();
            Check(reader.Parsed == events.Count && reader.Complete, "Guide files changed observation completeness");
            Check(ForetellEngine.EvaluateRecordedStream(reader.Read(), captureComplete: reader.Complete).Report.DecisionDigest == expectedDigest, "Guide prose contaminated replay learning");
            capture.EnqueueGuide(first, Input(first, "New summary"));
            using var updated = capture.SnapshotAsync(first.Directory).GetAwaiter().GetResult()!;
            Check(updated.Guides.Length == 5 && original.Guides.Length == 3, "Later adaptation mutated a sealed snapshot or duplicated source");
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
            Check(snapshot.Guides.Length == 5, "Historical guides lost after leaving/restarting");
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
        TestLongTimeline(root);
        TestChangingAdaptations(root);
        TestReservedQuotas(root, events[0]);
        TestTimelineBounds(root);
        TestOversizedSource(root);
        TestLegacyGuideIndex(root);
        Console.WriteLine("Guide export: reserved quotas, long timelines, change-only adaptations, final state/gaps, pin safety, legacy ZIPs and bounds passed.");
    }

    private static JsonElement[] Frames(ForetellCapture.Snapshot snapshot)
    {
        return snapshot.Guides.Where(file => file.Kind == "timeline").SelectMany(file =>
        {
            var bytes = ForetellEngine.ReadGuideArtifact(snapshot.Directory, file);
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var document = JsonDocument.Parse(gzip);
            Check(document.RootElement.ValueKind == JsonValueKind.Object, "Timeline was serialized as base64 instead of JSON");
            return document.RootElement.GetProperty("frames").EnumerateArray().Select(frame => frame.Clone()).ToArray();
        }).ToArray();
    }

    private static void TestLongTimeline(string root)
    {
        using var capture = new ForetellCapture(Path.Combine(root, "guide-long"));
        var session = capture.NewSession(1, "long", "capture-version");
        var initial = Input(session);
        var presentation = new GuidePresentationCapture(initial.At, session.ID, 1, 42, "Submitted", "Submitted",
            [new("Boss", "Phase 1", "Storm", 100, 51, true, "Submitted", new(10, 20, 200, 30), "Spread")], []);
        Check(capture.EnqueueGuide(session, initial with { Presentation = presentation }), "Initial presentation rejected");
        using var pinned = capture.SnapshotAsync(session.Directory).GetAwaiter().GetResult()!;
        var originalFiles = pinned.Guides.ToDictionary(file => file.File, file => File.ReadAllBytes(Path.Combine(pinned.Directory, file.File)));
        for (var count = 1; count < 600; ++count)
        {
            var at = initial.At.AddSeconds(count);
            var input = initial with { At = at, State = initial.State with { Boss = "Boss " + count },
                Presentation = presentation with { At = at, ListState = count == 599 ? "Clipped" : "Submitted" } };
            Check(capture.EnqueueGuide(session, input), "Long-run guide queue rejected a bounded batch");
            if (count % 24 == 0) capture.SnapshotAsync(session.Directory).GetAwaiter().GetResult()!.Dispose();
        }
        using var snapshot = capture.SnapshotAsync(session.Directory).GetAwaiter().GetResult()!;
        var frames = Frames(snapshot);
        Check(frames.Length == 600 && session.GuideRejected == 0, "Lightweight timeline stopped after 128 changes");
        Check(snapshot.Guides.Count(file => file.Kind == "source") == 1 && snapshot.Guides.Count(file => file.Kind == "adapted") == 1,
            "State/presentation transitions duplicated full source or adapted artifacts");
        Check(frames.Select(frame => frame.GetProperty("sequence").GetInt64()).SequenceEqual(Enumerable.Range(1, 600).Select(value => (long)value)), "Timeline order lost");
        Check(frames[^1].GetProperty("Presentation").GetProperty("ListState").GetString() == "Clipped", "Final presentation transition lost");
        Check(frames.All(frame => frame.GetProperty("sourceHash").GetString() == initial.Document!.SourceHash
            && frame.GetProperty("guideHash").GetString()!.Length == 64), "Timeline source/guide references missing");
        Check(!frames[0].TryGetProperty("Adapted", out _) && !frames[0].TryGetProperty("Document", out _), "Timeline contains repeated full artifacts");
        foreach (var file in originalFiles)
            Check(file.Value.SequenceEqual(File.ReadAllBytes(Path.Combine(pinned.Directory, file.Key))), "Pinned guide file changed while capture continued");
        Check(Frames(pinned).Length == 1, "Pinned snapshot acquired later frames");
        var zip = Export(root, "long-guides.zip", snapshot, session.ID);
        using var archive = ZipFile.OpenRead(zip);
        using var index = Read(archive, "guides/index.json");
        Check(index.RootElement.GetProperty("complete").GetBoolean()
            && index.RootElement.GetProperty("guideCapture").GetProperty("latest").GetProperty("State").GetProperty("Boss").GetString() == "Boss 599",
            "Export lost final guide state");
    }

    private static void TestChangingAdaptations(string root)
    {
        using var capture = new ForetellCapture(Path.Combine(root, "guide-adaptation-changes"));
        var session = capture.NewSession(1, "adaptations", "capture-version");
        for (var count = 0; count < 160; ++count)
        {
            Check(capture.EnqueueGuide(session, Input(session, "Summary " + count)), "Changed adaptation rejected at enqueue");
            if (count % 24 == 23) capture.SnapshotAsync(session.Directory).GetAwaiter().GetResult()!.Dispose();
        }
        using var snapshot = capture.SnapshotAsync(session.Directory).GetAwaiter().GetResult()!;
        Check(Frames(snapshot).Length == 160 && snapshot.Guides.Count(file => file.Kind == "adapted") == 160 && session.GuideRejected == 0,
            "Full adaptation changes still hit the old 128 snapshot limit");
        Check(snapshot.Index.Length <= 128 * 1024, "Guide metadata exceeds legacy recording-reader index bound");
    }

    private static void TestReservedQuotas(string root, ForetellObservation template)
    {
        foreach (var expandedLimit in new long[] { ForetellCapture.ExpandedSessionLimit, 64 * 1024 })
        {
            var directory = Path.Combine(root, "guide-reserved-" + expandedLimit);
            const int sessionLimit = 256 * 1024, cacheLimit = 512 * 1024;
            using var capture = new ForetellCapture(directory, sessionLimit, cacheLimit, segmentLimit: 16 * 1024, expandedLimit: expandedLimit);
            var session = capture.NewSession(1, "reserved", "capture-version");
            var random = new Random(73);
            for (var count = 0; count < 80; ++count)
            {
                var observation = template.CopyForRecording();
                var payload = new byte[4 * 1024]; random.NextBytes(payload);
                observation.Text["payload"] = Convert.ToBase64String(payload);
                capture.Enqueue(session, observation);
            }
            using var raw = capture.SnapshotAsync(session.Directory).GetAwaiter().GetResult()!;
            Check(session.Capped != 0 && session.Rejected > 0 && session.Written > 0, "Fixture did not exhaust the raw stream quota");
            var input = Input(session) with
            {
                BindingAudits = [new(1, DateTime.UtcNow, GuideNames.Hash("fixture"), "Final boss", null, "status", 1, 2, 3, 4,
                    "Charged", 5, 4, "MatchedGroundedTrigger", ["Bomb pattern"], "Bomb pattern", "Move away", "ObservedName", GuideNames.Hash("binding"))],
                BindingMemoryState = "Ready"
            };
            Check(capture.EnqueueGuide(session, input with { State = input.State with { Boss = "Final boss" } }), "Raw cap rejected later guide input");
            using var snapshot = capture.SnapshotAsync(session.Directory).GetAwaiter().GetResult()!;
            Check(session.GuideRejected == 0 && Frames(snapshot).Single().GetProperty("State").GetProperty("Boss").GetString() == "Final boss",
                "Capped raw stream starved the later boss guide");
            Check(snapshot.Guides.Any(file => file.Kind == "source") && snapshot.Guides.Any(file => file.Kind == "adapted"), "Later boss full artifacts missing");
            var audit = Frames(snapshot).Single().GetProperty("BindingAudits").EnumerateArray().Single();
            Check(audit.GetProperty("Instruction").GetString() == "Move away" && audit.GetProperty("Stacks").GetInt32() == 4,
                "Raw cap lost the selected event cue or status evidence");
            var zip = Export(root, "reserved-guides-" + expandedLimit + ".zip", snapshot, session.ID);
            using var archive = ZipFile.OpenRead(zip);
            using var index = Read(archive, "guides/index.json");
            Check(index.RootElement.GetProperty("complete").GetBoolean(), "Raw incompleteness incorrectly marked guide capture incomplete");
            Check(Directory.EnumerateFiles(session.Directory).Sum(path => new FileInfo(path).Length) <= sessionLimit, "Combined capture exceeded session quota");
            Check(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) <= cacheLimit, "Combined capture exceeded cache quota");
            Check(session.ExpandedBytes + snapshot.Guides.Sum(file => file.ExpandedBytes) <= expandedLimit, "Combined capture exceeded expanded quota");
            Check(capture.EnqueueGuide(session, input with { BindingAudits = [], BindingAuditsDropped = 3 }), "Dropped-audit counter rejected");
            using var dropped = capture.SnapshotAsync(session.Directory).GetAwaiter().GetResult()!;
            using var droppedArchive = ZipFile.OpenRead(Export(root, "dropped-bindings-" + expandedLimit + ".zip", dropped, session.ID));
            using var droppedIndex = Read(droppedArchive, "guides/index.json");
            Check(!droppedIndex.RootElement.GetProperty("complete").GetBoolean(), "Dropped binding evidence was silently marked complete");
        }
    }

    private static void TestTimelineBounds(string root)
    {
        var directory = Path.Combine(root, "guide-timeline-bounds");
        const int sessionLimit = 256 * 1024;
        using var capture = new ForetellCapture(directory, sessionLimit, 512 * 1024, segmentLimit: 16 * 1024, expandedLimit: 64 * 1024);
        var session = capture.NewSession(1, "bounds", "capture-version");
        var initial = Input(session);
        Check(capture.EnqueueGuide(session, initial), "Initial bounded guide rejected");
        using var pinned = capture.SnapshotAsync(session.Directory).GetAwaiter().GetResult()!;
        var pinnedBytes = pinned.Guides.ToDictionary(file => file.File, file => File.ReadAllBytes(Path.Combine(pinned.Directory, file.File)));
        for (var count = 1; count < 180; ++count)
        {
            Check(capture.EnqueueGuide(session, initial with { At = initial.At.AddSeconds(count), State = initial.State with { Boss = "Boss " + count } }), "Bounded guide input unexpectedly rejected");
            if (count % 16 == 0) capture.SnapshotAsync(session.Directory).GetAwaiter().GetResult()!.Dispose();
        }
        using var snapshot = capture.SnapshotAsync(session.Directory).GetAwaiter().GetResult()!;
        using var document = JsonDocument.Parse(snapshot.Index);
        var state = document.RootElement.GetProperty("guideCapture");
        Check(state.GetProperty("timelineOmitted").GetInt64() > 0 && session.GuideRejected > 0, "Timeline exhaustion did not expose gaps");
        Check(state.GetProperty("latest").GetProperty("State").GetProperty("Boss").GetString() == "Boss 179", "Quota exhaustion lost the final state");
        Check(Frames(snapshot).Length + state.GetProperty("timelineOmitted").GetInt64() == 180, "Stored/omitted timeline accounting does not cover every sample");
        Check(state.GetProperty("compressedBytes").GetInt64() <= state.GetProperty("compressedLimit").GetInt64()
            && state.GetProperty("expandedBytes").GetInt64() <= state.GetProperty("expandedLimit").GetInt64(), "Reserved guide bounds exceeded");
        Check(snapshot.Guides.Length <= 256 && snapshot.Index.Length <= 128 * 1024
            && Directory.EnumerateFiles(session.Directory).Sum(path => new FileInfo(path).Length) <= sessionLimit, "Guide files/index/session grew without bound");
        foreach (var file in pinnedBytes)
            Check(file.Value.SequenceEqual(File.ReadAllBytes(Path.Combine(pinned.Directory, file.Key))), "Storage pressure mutated a pinned artifact");
        var zip = Export(root, "bounded-guides.zip", snapshot, session.ID);
        using var archive = ZipFile.OpenRead(zip);
        using var index = Read(archive, "guides/index.json");
        Check(!index.RootElement.GetProperty("complete").GetBoolean() && index.RootElement.GetProperty("warnings").GetArrayLength() > 0,
            "Export concealed guide timeline gaps");
    }

    private static void TestLegacyGuideIndex(string root)
    {
        using var capture = new ForetellCapture(Path.Combine(root, "guide-legacy"));
        var session = capture.NewSession(1, "legacy", "capture-version");
        Check(capture.EnqueueGuide(session, Input(session)), "Legacy fixture rejected");
        using var snapshot = capture.SnapshotAsync(session.Directory).GetAwaiter().GetResult()!;
        var guides = snapshot.Guides.Where(file => file.Kind != "timeline").ToArray();
        var legacyIndex = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, sessionID = session.ID, territory = 1, complete = true, parts = Array.Empty<string>(), guides });
        using var legacy = new ForetellCapture.Snapshot(snapshot.Directory, legacyIndex, [], () => { }, guides);
        var zip = Export(root, "legacy-guides.zip", legacy, session.ID);
        using var archive = ZipFile.OpenRead(zip);
        using var index = Read(archive, "guides/index.json");
        Check(index.RootElement.GetProperty("schema").GetInt32() == 1 && index.RootElement.GetProperty("complete").GetBoolean()
            && index.RootElement.GetProperty("availability").GetString() == "captured", "Legacy snapshot-only guide index no longer exports");
    }

    private static void TestOversizedSource(string root)
    {
        using var capture = new ForetellCapture(Path.Combine(root, "guide-large-source"));
        var session = capture.NewSession(1, "large-source", "capture-version");
        var input = Input(session);
        input = input with { Document = input.Document! with { Page = new("Fixture", "https://example.org/fixture", new string('x', 1_500_000), "") },
            Presentation = new(input.At, session.ID, 1, 42, "Submitted", "Submitted", [], []) };
        Check(capture.EnqueueGuide(session, input), "Oversized source rejected essential guide state");
        using var snapshot = capture.SnapshotAsync(session.Directory).GetAwaiter().GetResult()!;
        var frame = Frames(snapshot).Single();
        Check(frame.GetProperty("State").GetProperty("Boss").GetString() == "Boss"
            && frame.GetProperty("Presentation").GetProperty("ListState").GetString() == "Submitted", "Source omission lost state/presentation");
        Check(!frame.GetProperty("sourceAvailable").GetBoolean() && frame.GetProperty("sourceHash").GetString() == input.Document!.SourceHash
            && frame.GetProperty("guideHash").GetString()!.Length == 64 && session.GuideRejected > 0, "Oversized source gap/hash not retained");
    }
}
