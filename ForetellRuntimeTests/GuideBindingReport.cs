using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class GuideBindingReport
{
    internal const int ArtifactLimit = 24 * 1024 * 1024;
    private const int EntryLimit = 256;
    private const int RecordLimit = 32768;
    private static readonly Regex TimelinePath = new(@"\Aguides/guide-timeline-[0-9]{6}\.json\.gz\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    public static void Run(string zip, string outputDirectory)
    {
        var output = Path.Combine(Path.GetFullPath(outputDirectory), "report.json");
        if (string.Equals(Path.GetFullPath(zip), output, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Report output would overwrite the input archive.");
        var report = new Summary();
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            var entries = archive.Entries.Where(entry => entry.FullName == "guides/index.json" || TimelinePath.IsMatch(entry.FullName)).ToArray();
            var duplicates = entries.GroupBy(entry => entry.FullName, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
            foreach (var duplicate in duplicates) report.Warn("Duplicate ZIP path skipped: " + duplicate);
            entries = entries.Where(entry => !duplicates.Contains(entry.FullName))
                .OrderBy(entry => entry.FullName == "guides/index.json" ? 0 : 1).ThenBy(entry => entry.FullName, StringComparer.Ordinal).ToArray();
            if (entries.Length > EntryLimit) report.Warn("Eligible guide artifacts exceed the 256-entry read limit; remaining artifacts omitted.");
            entries = entries.Take(EntryLimit).ToArray();
            JsonElement latest = default;
            var session = "";
            if (entries.FirstOrDefault(entry => entry.FullName == "guides/index.json") is { } indexEntry)
            {
                using var index = Read(indexEntry, report);
                if (index != null)
                {
                    var root = index.RootElement;
                    session = Text(root, "SessionID");
                    foreach (var warning in Array(root, "warnings")) report.Warn(warning.ValueKind == JsonValueKind.String ? warning.GetString()! : "Malformed export warning.");
                    if (Property(root, "complete").ValueKind == JsonValueKind.False) report.Warn("Export marks guide capture incomplete.");
                    var capture = Property(root, "guideCapture");
                    report.Session(session).TimelineOmitted = Number(capture, "timelineOmitted");
                    if (Property(capture, "latest") is { ValueKind: JsonValueKind.Object } latestFrame) latest = latestFrame.Clone();
                }
            }
            else report.Warn("guides/index.json is unavailable.");
            foreach (var entry in entries.Where(entry => TimelinePath.IsMatch(entry.FullName)).OrderBy(entry => entry.FullName, StringComparer.Ordinal))
            {
                using var document = Read(entry, report);
                if (document == null) continue;
                if (Property(document.RootElement, "frames").ValueKind != JsonValueKind.Array)
                {
                    report.Warn("Timeline frames unavailable: " + entry.FullName);
                    continue;
                }
                foreach (var frame in Array(document.RootElement, "frames")) report.Frame(frame, session);
            }
            if (latest.ValueKind == JsonValueKind.Object)
            {
                report.Frame(latest, session);
                report.LatestPending(latest);
            }
        }
        catch (Exception error) when (ReadError(error)) { report.Warn("Archive unavailable: " + error.Message); }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(report.Export(), new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(output);
    }

    private static JsonDocument? Read(ZipArchiveEntry entry, Summary report)
    {
        try
        {
            if (entry.Length > ArtifactLimit) throw new InvalidDataException("Artifact exceeds the 24 MiB read limit.");
            using var input = entry.Open();
            var bytes = ReadLimited(input);
            if (entry.FullName.EndsWith(".gz", StringComparison.Ordinal))
            {
                using var compressed = new MemoryStream(bytes);
                using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                bytes = ReadLimited(gzip);
            }
            return JsonDocument.Parse(bytes);
        }
        catch (Exception error) when (ReadError(error))
        {
            report.Warn(entry.FullName + ": " + error.Message);
            return null;
        }
    }

    private static byte[] ReadLimited(Stream input)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = input.Read(buffer, 0, Math.Min(buffer.Length, ArtifactLimit + 1 - (int)output.Length))) > 0)
        {
            if (output.Length + count > ArtifactLimit) throw new InvalidDataException("Artifact exceeds the 24 MiB expanded read limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static bool ReadError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException;
    private static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;
    private static IEnumerable<JsonElement> Array(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];
    private static string Text(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.String } text ? Short(text.GetString()!) : "";
    private static long Number(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out var result) ? Math.Max(0, result) : 0;
    private static string Short(string value) => value.Length <= 512 ? value : value[..512];
    private sealed record Audit(string SessionID, long Sequence, string Scope, string Kind, long ID, string Name, string Boss, string Result, string[] Candidates, string Mapping, string Instruction)
    {
        public string CatalogHash { get; init; } = "";
        public long[] CandidateIDs { get; init; } = [];
        public long CandidateIDsOmitted { get; init; }
        public long CastType { get; init; }
        public string Phase { get; init; } = "";
    }
    private sealed class Gaps
    {
        public long BindingAuditsDropped { get; set; }
        public long TimelineOmitted { get; set; }
        public long CaptureRejected { get; set; }
        public long BindingAuditsOmitted { get; set; }
        public long MissingAuditSequences { get; set; }
        public long MissingFrameSequences { get; set; }
    }

    private sealed class Summary
    {
        private readonly Dictionary<(string Session, long Sequence), Audit> _audits = [];
        private readonly HashSet<(string Session, long Sequence)> _frames = [];
        private readonly Dictionary<string, Gaps> _sessions = new(StringComparer.Ordinal);
        private readonly HashSet<string> _warnings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _listStates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _centralStates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _rowOutcomes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _alertOutcomes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _bindingMemoryStates = new(StringComparer.Ordinal);
        private bool _available;
        private int _auditCopies;
        private int _presentationFrames;
        private int _guideSubmitted;
        private int _genericSubmitted;
        private int _clippedFrames;
        private int _hiddenFrames;
        private long? _latestPending;

        public void LatestPending(JsonElement latest)
        {
            if (Property(latest, "BindingAuditsPending") is not { ValueKind: JsonValueKind.Number } pending
                || !pending.TryGetInt64(out var count) || count < 0) return;
            _latestPending = count;
            if (count > 0) Warn($"Final index sample retains {count} pending binding audit(s); export audit coverage is incomplete.");
        }

        public void Warn(string warning)
        {
            if (_warnings.Count < 255) _warnings.Add(Short(warning));
            else _warnings.Add("Additional warnings omitted by the report limit.");
        }

        public Gaps Session(string session)
        {
            if (!_sessions.TryGetValue(session, out var gaps)) _sessions[session] = gaps = new();
            return gaps;
        }

        public void Frame(JsonElement frame, string fallbackSession)
        {
            var session = Text(frame, "SessionID");
            if (session.Length == 0) session = fallbackSession;
            var sequence = Number(frame, "sequence");
            if (session.Length == 0 || sequence == 0) { Warn("Sample omitted: missing session or frame sequence."); return; }
            if (_frames.Count >= RecordLimit && !_frames.Contains((session, sequence))) { Warn("Frame aggregation limit reached."); return; }
            var gaps = Session(session);
            gaps.BindingAuditsDropped = Math.Max(gaps.BindingAuditsDropped, Number(frame, "BindingAuditsDropped"));
            gaps.TimelineOmitted = Math.Max(gaps.TimelineOmitted, Number(frame, "timelineOmitted"));
            gaps.CaptureRejected = Math.Max(gaps.CaptureRejected, Number(frame, "rejected"));
            if (Text(frame, "gap") is { Length: > 0 } gap) Warn(gap);
            if (Property(frame, "BindingAudits").ValueKind == JsonValueKind.Array)
            {
                _available = true;
                foreach (var value in Array(frame, "BindingAudits"))
                {
                    var auditSequence = Number(value, "Sequence");
                    if (auditSequence == 0) { Warn("Binding audit omitted: missing sequence."); continue; }
                    var key = (session, auditSequence);
                    if (_audits.ContainsKey(key)) { ++_auditCopies; continue; }
                    if (_audits.Count >= RecordLimit) { Warn("Binding audit aggregation limit reached."); break; }
                    _audits.Add(key, new(session, auditSequence, Text(value, "Scope"), Text(value, "Kind"), Number(value, "ID"), Text(value, "Name"), Text(value, "Boss"),
                        Text(value, "Result"), Array(value, "Candidates").Where(candidate => candidate.ValueKind == JsonValueKind.String)
                            .Take(8).Select(candidate => Short(candidate.GetString()!)).ToArray(), Text(value, "Mapping"), Text(value, "Instruction"))
                    {
                        CatalogHash = Text(value, "CatalogHash"), CastType = Number(value, "CastType"), Phase = Text(value, "Phase"),
                        CandidateIDsOmitted = Number(value, "CandidateIDsOmitted"),
                        CandidateIDs = Array(value, "CandidateIDs").Where(candidate => candidate.ValueKind == JsonValueKind.Number && candidate.TryGetInt64(out _))
                            .Take(4096).Select(candidate => candidate.GetInt64()).ToArray()
                    });
                }
            }
            else Warn("Sample has no binding audit array; audit coverage is incomplete.");
            if (!_frames.Add((session, sequence))) return;
            Count(_bindingMemoryStates, Text(frame, "BindingMemoryState"));
            gaps.BindingAuditsOmitted += Math.Min(RecordLimit, Number(frame, "bindingAuditsOmitted"));
            var presentation = Property(frame, "Presentation");
            var listState = Text(presentation, "ListState");
            var centralState = Text(presentation, "CentralState");
            if (listState.Length == 0) listState = Text(frame, "listState");
            if (centralState.Length == 0) centralState = Text(frame, "centralState");
            Count(_listStates, listState);
            Count(_centralStates, centralState);
            if (Hidden(listState) || Hidden(centralState)) ++_hiddenFrames;
            if (presentation.ValueKind != JsonValueKind.Object) return;
            ++_presentationFrames;
            var clipped = false;
            var guide = false;
            var generic = false;
            foreach (var row in PresentationItems(presentation, "Rows"))
            {
                var outcome = Text(row, "Outcome");
                Count(_rowOutcomes, outcome);
                clipped |= outcome is "Clipped" or "PartiallyClipped";
            }
            foreach (var alert in PresentationItems(presentation, "Alerts"))
            {
                var outcome = Text(alert, "Outcome");
                Count(_alertOutcomes, outcome);
                clipped |= outcome is "Clipped" or "PartiallyClipped";
                if (outcome != "Submitted") continue;
                guide |= Property(alert, "FromGuide").ValueKind == JsonValueKind.True;
                generic |= Property(alert, "FromGuide").ValueKind == JsonValueKind.False;
            }
            if (guide) ++_guideSubmitted;
            if (generic) ++_genericSubmitted;
            if (clipped) ++_clippedFrames;
        }

        public object Export()
        {
            foreach (var group in _audits.Keys.GroupBy(key => key.Session))
                _sessions[group.Key].MissingAuditSequences = group.Max(key => key.Sequence) - group.Min(key => key.Sequence) - (group.Count() - 1);
            foreach (var group in _frames.GroupBy(key => key.Session))
                _sessions[group.Key].MissingFrameSequences = group.Max(key => key.Sequence) - group.Count();
            if (!_available) Warn("Binding audit unavailable: snapshot-only, older, missing or unreadable capture; event absence cannot be inferred.");
            var matched = _audits.Values.Count(value => value.Result.StartsWith("Matched", StringComparison.Ordinal));
            return new
            {
                schema = 1,
                semantics = new
                {
                    bindings = "Distinct captured decisions by (SessionID, Sequence). ObservedName and RememberedID describe mapping provenance, not semantic truth.",
                    presentation = "Sampled draw submissions, not alert occurrences or proof of visible pixels. Guide and generic frame counts may overlap.",
                    gaps = "Cumulative counters use session maxima. Audit sequence gaps count only between retained decisions because numbering spans the engine lifetime. Only final index pending audits indicate an undrained backlog. Gap and omission counters may overlap; do not sum them.",
                    text = "Text is limited to 512 characters and candidates to eight per audit. Snapshot-only exports do not provide event audit coverage."
                },
                limits = new { eligibleGuideArtifacts = EntryLimit, expandedBytesPerArtifact = ArtifactLimit, distinctFrames = RecordLimit, distinctAudits = RecordLimit, presentationItemsPerArray = 256, outcomeLabels = 256 },
                bindingAudits = new
                {
                    availability = _available ? "available" : "unavailable", distinctCount = _audits.Count,
                    matchedCount = _available ? (int?)matched : null, rejectedCount = _available ? (int?)(_audits.Count - matched) : null,
                    duplicateCopies = _auditCopies,
                    byResult = _audits.Values.GroupBy(value => value.Result).ToDictionary(group => group.Key, group => group.Count()),
                    byMapping = _audits.Values.GroupBy(value => value.Mapping).ToDictionary(group => group.Key, group => group.Count()),
                    entries = _audits.Values.OrderBy(value => value.SessionID, StringComparer.Ordinal).ThenBy(value => value.Sequence).ToArray()
                },
                presentation = new
                {
                    distinctSampledFrames = _frames.Count, framesWithPresentation = _presentationFrames,
                    guideCentralSubmittedFrames = _guideSubmitted, genericCentralSubmittedFrames = _genericSubmitted,
                    clippedFrames = _clippedFrames, hiddenFrames = _hiddenFrames,
                    hiddenDefinition = "At least one list/central state contains Hidden, Disabled or equals NotDrawn.",
                    listStates = _listStates, centralStates = _centralStates, rowOutcomes = _rowOutcomes, alertOutcomes = _alertOutcomes
                },
                gapsBySession = _sessions,
                bindingMemoryStates = _bindingMemoryStates,
                latestBindingAuditsPending = _latestPending,
                warnings = _warnings.Order(StringComparer.Ordinal).ToArray()
            };
        }

        private static bool Hidden(string state) => state.Contains("Hidden", StringComparison.Ordinal) || state.Contains("Disabled", StringComparison.Ordinal) || state == "NotDrawn";
        private IEnumerable<JsonElement> PresentationItems(JsonElement presentation, string name)
        {
            if (Property(presentation, name) is { ValueKind: JsonValueKind.Array } array && array.GetArrayLength() > 256)
                Warn("Presentation " + name + " exceeded the 256-item sample limit.");
            return Array(presentation, name).Take(256);
        }

        private void Count(Dictionary<string, int> counts, string value)
        {
            if (counts.Count >= 256 && !counts.ContainsKey(value)) { Warn("Presentation outcome label limit reached."); return; }
            if (value.Length != 0) counts[value] = counts.GetValueOrDefault(value) + 1;
        }
    }
}

internal static class GuideBindingReportTests
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "foretell-binding-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var zip = Path.Combine(directory, "fixture.zip");
            var output = Path.Combine(directory, "output");
            void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
            JsonDocument Report()
            {
                GuideBindingReport.Run(zip, output);
                Check(Directory.GetFiles(output).Select(Path.GetFileName).SequenceEqual(new[] { "report.json" }), "Report extracted extra files.");
                return JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "report.json")));
            }
            void Write(ZipArchive archive, string path, object value, bool gzip = false)
            {
                using var stream = archive.CreateEntry(path).Open();
                var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
                if (gzip) { using var compressed = new GZipStream(stream, CompressionLevel.Fastest); compressed.Write(bytes); }
                else stream.Write(bytes);
            }
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                Write(archive, "guides/index.json", new { schema = 1, SessionID = "session", complete = true });
                Write(archive, "guides/guide-snapshot-000001.json.gz", new { Adapted = new object[0] }, true);
            }
            using (var old = Report())
                Check(old.RootElement.GetProperty("bindingAudits").GetProperty("matchedCount").ValueKind == JsonValueKind.Null, "Legacy export claimed no matched events.");
            File.Delete(zip);
            object Audit(long sequence, string result, string mapping) => new { Sequence = sequence, Scope = "fixture-scope", Kind = "cast", ID = 42, Name = "Hammer", Boss = "Sentinel", Result = result, Candidates = new[] { "Hammer" }, Mapping = mapping, Instruction = "Mitigate",
                CatalogHash = "catalog-fixture", CandidateIDs = new[] { 42, 43 }, CandidateIDsOmitted = 7, CastType = 2, Phase = "second" };
            object Frame(string session, long sequence, object[] audits, int pending = 0) => new
            {
                SessionID = session, sequence, BindingAudits = audits, BindingAuditsDropped = 2, BindingAuditsPending = pending, timelineOmitted = 3,
                Presentation = new
                {
                    ListState = "WindowHidden", CentralState = "Drawn", Rows = new[] { new { Outcome = "Clipped" } },
                    Alerts = new[] { new { FromGuide = true, Outcome = "Submitted" }, new { FromGuide = true, Outcome = "Submitted" }, new { FromGuide = false, Outcome = "Submitted" } }
                }
            };
            var first = Frame("session", 1, [Audit(100, "MatchedGroundedTrigger", "ObservedName"), Audit(101, "OtherPhase", "RememberedID")], 5);
            var last = Frame("session", 2, [Audit(100, "MatchedGroundedTrigger", "ObservedName")]);
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                Write(archive, "guides/index.json", new { SessionID = "session", complete = false, warnings = new[] { "Fixture gap" }, guideCapture = new { timelineOmitted = 3, latest = last } });
                Write(archive, "guides/guide-timeline-000001.json.gz", new { frames = new[] { first, last, Frame("other", 1, [Audit(1, "MatchedLegacyName", "RememberedID")]) } }, true);
                Write(archive, "guides/../guide-timeline-000002.json.gz", new { frames = new[] { Frame("unsafe", 1, [Audit(1, "Matched", "ObservedName")]) } }, true);
            }
            using (var current = Report())
            {
                var audits = current.RootElement.GetProperty("bindingAudits");
                var presentation = current.RootElement.GetProperty("presentation");
                Check(audits.GetProperty("distinctCount").GetInt32() == 3 && audits.GetProperty("matchedCount").GetInt32() == 2 && audits.GetProperty("rejectedCount").GetInt32() == 1, "Session/sequence audit deduplication failed.");
                Check(audits.GetProperty("entries").EnumerateArray().All(audit => audit.GetProperty("Scope").GetString() == "fixture-scope"), "Audit scope was lost.");
                Check(audits.GetProperty("entries").EnumerateArray().All(audit => audit.GetProperty("CatalogHash").GetString() == "catalog-fixture"
                    && audit.GetProperty("CandidateIDs").GetArrayLength() == 2 && audit.GetProperty("CandidateIDsOmitted").GetInt32() == 7
                    && audit.GetProperty("CastType").GetInt32() == 2 && audit.GetProperty("Phase").GetString() == "second"), "Official ID audit provenance was lost.");
                Check(presentation.GetProperty("guideCentralSubmittedFrames").GetInt32() == 3 && presentation.GetProperty("genericCentralSubmittedFrames").GetInt32() == 3, "Submissions counted alert occurrences or duplicated latest.");
                Check(presentation.GetProperty("clippedFrames").GetInt32() == 3 && presentation.GetProperty("hiddenFrames").GetInt32() == 3, "Clipping/hide samples were lost.");
                Check(current.RootElement.GetProperty("gapsBySession").GetProperty("session").GetProperty("BindingAuditsDropped").GetInt64() == 2, "Cumulative drops were summed.");
                Check(current.RootElement.GetProperty("gapsBySession").GetProperty("session").GetProperty("MissingAuditSequences").GetInt64() == 0, "Engine-lifetime sequence prefix was reported as lost audit coverage.");
                Check(current.RootElement.GetProperty("latestBindingAuditsPending").GetInt64() == 0
                    && !current.RootElement.GetProperty("warnings").EnumerateArray().Any(warning => warning.GetString()!.Contains("pending binding")), "Drained historical backlog was reported as lost coverage.");
            }
            File.Delete(zip);
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                Write(archive, "guides/index.json", new { SessionID = "session", guideCapture = new { latest = last } });
            using (var latestOnly = Report())
                Check(latestOnly.RootElement.GetProperty("bindingAudits").GetProperty("matchedCount").GetInt32() == 1, "Index-only final sample was ignored.");
            File.Delete(zip);
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                Write(archive, "guides/index.json", new { SessionID = "session", guideCapture = new { latest = Frame("session", 3, [], 2) } });
            using (var pending = Report())
                Check(pending.RootElement.GetProperty("latestBindingAuditsPending").GetInt64() == 2
                    && pending.RootElement.GetProperty("warnings").EnumerateArray().Any(warning => warning.GetString()!.Contains("pending binding")), "Undrained final backlog lacked a coverage warning.");
            File.Delete(zip);
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                Write(archive, "guides/index.json", new { SessionID = "session" });
                using var stream = archive.CreateEntry("guides/guide-timeline-000001.json.gz").Open();
                using var gzip = new GZipStream(stream, CompressionLevel.Fastest);
                var chunk = new byte[8192];
                for (var count = 0; count <= GuideBindingReport.ArtifactLimit; count += chunk.Length) gzip.Write(chunk);
            }
            using (var bounded = Report())
                Check(bounded.RootElement.GetProperty("warnings").EnumerateArray().Any(warning => warning.GetString()!.Contains("24 MiB")), "Expanded artifact limit was not reported.");
            File.Delete(zip);
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                Write(archive, "guides/index.json", new { SessionID = "session", guideCapture = new { latest = last } });
                for (var index = 0; index < 300; ++index) archive.CreateEntry("capture/segment-" + index);
            }
            using (var full = Report())
                Check(full.RootElement.GetProperty("bindingAudits").GetProperty("matchedCount").GetInt32() == 1
                    && !full.RootElement.GetProperty("warnings").EnumerateArray().Any(warning => warning.GetString()!.Contains("256-entry")), "Raw ZIP segments consumed the guide artifact budget.");
            File.Delete(zip);
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                Write(archive, "guides/index.json", new { SessionID = "session", guideCapture = new { latest = last } });
                for (var index = 1; index <= 256; ++index)
                    Write(archive, $"guides/guide-timeline-{index:D6}.json.gz", new { frames = new object[0] }, true);
            }
            using (var bounded = Report())
                Check(bounded.RootElement.GetProperty("bindingAudits").GetProperty("matchedCount").GetInt32() == 1
                    && bounded.RootElement.GetProperty("warnings").EnumerateArray().Any(warning => warning.GetString()!.Contains("256-entry")), "Guide artifact limit lost the final index sample or lacked a warning.");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("Guide binding report: legacy/new exports, deduplication, presentation samples, safe paths and read limits passed.");
    }
}
