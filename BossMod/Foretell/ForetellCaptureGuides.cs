using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace BossMod.Foretell;

internal sealed record GuideCaptureFile(string File, string Sha256, long Bytes, string Kind, DateTime At, string SourceHash);
internal sealed record GuideAdaptedMechanic(string Name, string DisplayName, uint ActionID, string SummaryKey, string? Summary,
    GuidanceKind Guidance, string Instruction, bool Resolved, GuideRule[] Rules, GuideStatusRule? StatusRule)
{
    public GuideAdvice? Advice { get; init; }
}
internal sealed record GuideAdaptedPhase(string Name, string? Summary, bool Conditional, GuideAdaptedMechanic[] Mechanics);
internal sealed record GuideAdaptedBoss(string Name, string DisplayName, uint NameID, GuideAdaptedPhase[] Phases);
internal sealed record GuideCapturedSignal(string Boss, string Phase, string Mechanic, GuideSignalKind Kind, ulong SourceID, uint SourceOID,
    uint SourceNameID, uint ID, ulong OwnerID, ulong TargetID, DateTime Until, GuidanceKind Guidance, string Instruction, string Evidence);
internal sealed record GuideCaptureOptions(ForetellMode Mode, bool Enabled, bool Checklist, bool EntryPopup, bool PopupDismissed,
    bool CentralAlerts, bool TextHints, bool LocalSummaries, bool Gpu, float ChecklistScale, float AlertScale)
{
    public bool WorldOverlay { get; init; }
    public bool MiniRadar { get; init; }
    public float VisualThreshold { get; init; }
    public float WarningThreshold { get; init; }
    public GuideCaptureLayout? Layout { get; init; }
    public int ContextTokens { get; init; }
    public int MemoryGiB { get; init; }
    public string ModelID { get; init; } = "";
    public bool CurrentPhaseOnly { get; init; }
}
internal sealed record GuideCaptureLayout(bool Unlocked, float PositionX, float PositionY, float Width, float Height,
    uint TextColor, uint ActiveColor, uint ResolvedColor, uint UnresolvedColor, float AlertPositionX, float AlertPositionY);
internal sealed record GuideCaptureState(string State, string Error, bool Cached, double ElapsedSeconds, string SummaryStage,
    int SummariesCompleted, int SummariesTotal, string? Boss, bool Upcoming, bool Ambiguous, bool Combat)
{
    public GuideModelRuntime? ModelRuntime { get; init; }
    public string? SummaryIssue { get; init; }
    public GuidePhaseDefinition? CurrentPhase { get; init; }
}
internal sealed record GuideCaptureInput(DateTime At, string SessionID, uint TerritoryID, GuideDuty? Duty, GuideLanguage Language,
    GuideDocument? Document, GuideCaptureState State, GuideCaptureOptions Options, GuideAdaptedBoss[] Adapted, GuideCapturedSignal[] Signals)
{
    public long EstimatedBytes => 4096 + (Document?.Bosses.Sum(boss => boss.Phases.Sum(phase => phase.Context.Length + phase.Mechanics.Sum(mechanic => mechanic.Text.Length + mechanic.Name.Length))) ?? 0) * 8L
        + ((Document?.Page?.Text.Length ?? 0) + (Document?.Page?.Html.Length ?? 0)) * 6L
        + (Document?.Sources.Sum(source => (long)source.Text.Length + source.Original.Length) ?? 0) * 6L
        + Adapted.Sum(boss => boss.Phases.Sum(phase => (phase.Summary?.Length ?? 0) + phase.Mechanics.Sum(mechanic => (mechanic.Summary?.Length ?? 0) + mechanic.DisplayName.Length))) * 4L;
}

internal sealed partial class ForetellCapture
{
    internal const int GuideExpandedLimit = 24 * 1024 * 1024;
    internal const long GuideSessionLimit = 16L * 1024 * 1024;
    private static readonly JsonSerializerOptions GuideJson = new(Json) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };
    private sealed record GuideEvent(Session Session, GuideCaptureInput Input, long Bytes);
    private readonly List<GuideCaptureFile> _guideFiles = [];
    private int _guideSnapshotNumber;
    private long _guideBytes;

    internal static bool ValidGuideFilename(string name) => System.Text.RegularExpressions.Regex.IsMatch(name,
        @"^guide-(?:source-[A-F0-9]{64}|snapshot-[0-9]{6})\.json\.gz$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    public bool EnqueueGuide(Session session, GuideCaptureInput input)
    {
        var bytes = input.EstimatedBytes;
        if (input.SessionID != session.ID || input.TerritoryID != session.Territory
            || input.Duty != null && input.Duty.TerritoryID != session.Territory
            || input.Document != null && input.Document.Duty != input.Duty)
        { RejectGuide(session, "Guide snapshot identity does not match the capture session"); return false; }
        if (Volatile.Read(ref _disposed) != 0 || bytes > QueueLimit / 2 || PendingBytes + bytes > QueueLimit)
        { RejectGuide(session, "Guide snapshot queue unavailable or input too large"); return false; }
        Interlocked.Add(ref _queuedBytes, bytes);
        try { if (_queue.TryAdd(new GuideEvent(session, input, bytes))) return true; }
        catch (InvalidOperationException) { }
        Interlocked.Add(ref _queuedBytes, -bytes);
        RejectGuide(session, "Guide snapshot queue full");
        return false;
    }

    private static void RejectGuide(Session session, string reason)
    { Interlocked.Increment(ref session.GuideRejected); session.GuideError = reason; }

    private void SelectSession(Session session)
    {
        if (_active == session) return;
        ClosePart(); SaveIndex();
        _active = session; _part = 0; _parts.Clear(); _hashes.Clear(); _counts.Clear(); _first = _last = default;
        _guideFiles.Clear(); _guideSnapshotNumber = 0; _guideBytes = 0;
    }

    private void WriteGuide(GuideEvent item)
    {
        SelectSession(item.Session);
        if (_guideSnapshotNumber >= 128) { RejectGuide(item.Session, "Guide snapshot count limit reached (128)"); return; }
        var input = item.Input;
        var sourceFile = input.Document == null ? null : "guide-source-" + input.Document.SourceHash + ".json.gz";
        if (sourceFile != null && !_guideFiles.Any(file => file.File == sourceFile))
        {
            if (_guideFiles.Count(file => file.Kind == "source") >= 16) { RejectGuide(item.Session, "Guide source revision limit reached (16)"); return; }
            WriteGuideArtifact(sourceFile, "source", input.Document!, input);
        }
        WriteGuideArtifact($"guide-snapshot-{++_guideSnapshotNumber:D6}.json.gz", "adapted", new
        {
            schema = 1, input.At, input.SessionID, input.TerritoryID, input.Duty, input.Language,
            sourceFile, sourceHash = input.Document?.SourceHash, modelRevision = input.Document?.ModelRevision,
            sourceCoverage = input.Document?.Coverage,
            input.State, input.Options, input.Adapted, input.Signals,
            availableSummaries = input.Adapted.Sum(boss => boss.Phases.Sum(phase => (phase.Summary == null ? 0 : 1) + phase.Mechanics.Count(mechanic => mechanic.Summary != null))),
            semantics = "Session-time guide preparation and sampled matching state, not proof of rendered pixels. Source/conditions remain authoritative; automatic summaries are documentary. No guides are loaded into the learner by replay."
        }, input);
        SaveIndex();
    }

    private void WriteGuideArtifact(string filename, string kind, object payload, GuideCaptureInput input)
    {
        if (!ValidGuideFilename(filename)) throw new InvalidDataException("Invalid guide artifact filename");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, GuideJson);
        if (bytes.Length > GuideExpandedLimit) throw new IOException("Guide artifact exceeds expanded size limit");
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(bytes);
        var data = compressed.ToArray();
        var session = _active!;
        lock (_filesLock)
        {
            Directory.CreateDirectory(session.Directory);
            PruneCache(session.Directory);
            var used = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
            if (_guideBytes + data.Length > GuideSessionLimit || session.Bytes + data.Length + SegmentReserve > _sessionLimit
                || session.ExpandedBytes + bytes.Length + _segmentLimit > _expandedLimit || used + data.Length + SegmentReserve > _cacheLimit)
                throw new IOException("Guide artifact omitted to preserve guide/session/cache quotas");
            var final = Path.Combine(session.Directory, filename);
            File.WriteAllBytes(final + ".tmp", data);
            File.Move(final + ".tmp", final, overwrite: false);
            _guideFiles.Add(new(filename, Convert.ToHexString(SHA256.HashData(data)), data.Length, kind, input.At, input.Document?.SourceHash ?? ""));
            _guideBytes += data.Length; session.Bytes += data.Length; session.ExpandedBytes += bytes.Length;
        }
    }
}
