using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace BossMod.Foretell;

internal sealed record GuideCaptureFile(string File, string Sha256, long Bytes, string Kind, DateTime At, string SourceHash)
{
    public long ExpandedBytes { get; init; }
}
internal sealed record GuideAdaptedMechanic(string Name, string DisplayName, uint ActionID, string SummaryKey, string? Summary,
    GuidanceKind Guidance, string Instruction, bool Resolved, GuideRule[] Rules, GuideStatusRule? StatusRule)
{
    public GuideAdvice? Advice { get; init; }
}
internal sealed record GuideAdaptedPhase(string Name, string? Summary, bool Conditional, GuideAdaptedMechanic[] Mechanics);
internal sealed record GuideAdaptedBoss(string Name, string DisplayName, uint NameID, GuideAdaptedPhase[] Phases);
internal sealed record GuideCapturedSignal(string Boss, string Phase, string Mechanic, GuideSignalKind Kind, ulong SourceID, uint SourceOID,
    uint SourceNameID, uint ID, ulong OwnerID, ulong TargetID, DateTime Until, GuidanceKind Guidance, string Instruction, string Evidence)
{
    public GuideTrigger? Trigger { get; init; }
    public string? BindingKey { get; init; }
}
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
    public bool PauseAnalysisInCombat { get; init; }
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
    public GuidePresentationCapture? Presentation { get; init; }
    public GuideBindingAudit[] BindingAudits { get; init; } = [];
    public long BindingAuditsDropped { get; init; }
    public int BindingAuditsPending { get; init; }
    public string BindingMemoryState { get; init; } = "";
    public GuideIdResolutionInfo? IdResolution { get; init; }
    public long EstimatedBytes => 4096 + (Document?.Bosses.Sum(boss => boss.Phases.Sum(phase => phase.Context.Length + phase.Mechanics.Sum(mechanic => mechanic.Text.Length + mechanic.Name.Length))) ?? 0) * 8L
        + ((Document?.Page?.Text.Length ?? 0) + (Document?.Page?.Html.Length ?? 0)) * 6L
        + (Document?.Sources.Sum(source => (long)source.Text.Length + source.Original.Length) ?? 0) * 6L
        + Adapted.Sum(boss => boss.Phases.Sum(phase => (phase.Summary?.Length ?? 0) + phase.Mechanics.Sum(mechanic => (mechanic.Summary?.Length ?? 0) + mechanic.DisplayName.Length))) * 4L
        + Signals.Sum(signal => 256L + (signal.Boss.Length + signal.Phase.Length + signal.Mechanic.Length + signal.Instruction.Length + signal.Evidence.Length) * 2L)
        + BindingAudits.Sum(audit => 1024L + audit.CandidateIDs.Length * 12L + (audit.Instruction.Length + audit.Name.Length + audit.Candidates.Sum(name => name.Length)) * 2L)
        + (IdResolution?.Triggers.Sum(trigger => 512L + trigger.IDs.Length * 12L + (trigger.Boss.Length + trigger.Mechanic.Length + trigger.Name.Length) * 4L) ?? 0);
}

internal sealed partial class ForetellCapture
{
    internal const int GuideExpandedLimit = 24 * 1024 * 1024;
    internal const long GuideSessionLimit = 16L * 1024 * 1024;
    private long GuideBudget => Math.Min(GuideSessionLimit, _sessionLimit / 4);
    private long GuideExpandedBudget => Math.Min(128L * 1024 * 1024, _expandedLimit / 4);
    private int IndexReserve => (int)Math.Min(128 * 1024, _sessionLimit / 8);
    private int GuideFrameLimit => Math.Max(256, IndexReserve / 4);
    private int GuideBatchLimit => (int)Math.Min(256 * 1024, GuideExpandedBudget / 4);
    private static readonly JsonSerializerOptions GuideJson = new(Json) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };
    private sealed record GuideEvent(Session Session, GuideCaptureInput Input, long Bytes, string? SourceHash, string? ModelRevision, string? Gap, bool AdaptationOmitted);
    private readonly List<GuideCaptureFile> _guideFiles = [];
    private int _guideSnapshotNumber;
    private long _guideBytes;
    private long _guideExpandedBytes;
    private long _guideArtifactBytes;
    private long _guideArtifactExpandedBytes;
    private long _guideSequence;
    private long _guideTimelineOmitted;
    private int _guideTimelineNumber;
    private int _guideTimelineBytes;
    private readonly List<byte[]> _guideTimeline = [];
    private JsonElement? _guideLatest;
    private GuideCaptureInput? _guideLastInput;
    private DateTime _guideBatchStarted;
    private string? _guideAdaptationHash;
    private string? _guideAdaptationFile;
    private string? _guideAttemptedSource;
    private string? _guideAttemptedAdaptation;

    internal static bool ValidGuideFilename(string name) => System.Text.RegularExpressions.Regex.IsMatch(name,
        @"^guide-(?:source-[A-F0-9]{64}|snapshot-[0-9]{6}|timeline-[0-9]{6})\.json\.gz$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    public bool EnqueueGuide(Session session, GuideCaptureInput input)
    {
        var bytes = input.EstimatedBytes;
        var sourceHash = input.Document?.SourceHash;
        var modelRevision = input.Document?.ModelRevision;
        string? gap = null;
        var adaptationOmitted = false;
        if (input.SessionID != session.ID || input.TerritoryID != session.Territory
            || input.Duty != null && input.Duty.TerritoryID != session.Territory
            || input.Document != null && input.Document.Duty != input.Duty)
        { RejectGuide(session, "Guide snapshot identity does not match the capture session"); return false; }
        bytes += JsonSerializer.SerializeToUtf8Bytes(input.Presentation, GuideJson).Length * 2L;
        var available = QueueLimit / 2 - Interlocked.Read(ref _guideQueuedBytes);
        if (bytes > available)
        {
            input = input with { Document = null };
            gap = "Full guide source omitted from queue; essential timeline retained";
            bytes = input.EstimatedBytes + JsonSerializer.SerializeToUtf8Bytes(input.Presentation, GuideJson).Length * 2L;
            if (bytes > available)
            {
                input = input with { Adapted = [], IdResolution = input.IdResolution is { } ids ? ids with { Triggers = [] } : null };
                adaptationOmitted = true;
                gap = "Full guide source/adaptation omitted from queue; essential timeline retained";
                bytes = input.EstimatedBytes + JsonSerializer.SerializeToUtf8Bytes(input.Presentation, GuideJson).Length * 2L;
            }
        }
        if (Volatile.Read(ref _disposed) != 0 || bytes > QueueLimit / 2 || Interlocked.Read(ref _guideQueuedBytes) + bytes > QueueLimit / 2)
        { RejectGuide(session, "Guide snapshot queue unavailable or input too large"); return false; }
        Interlocked.Add(ref _guideQueuedBytes, bytes);
        try { if (_queue.TryAdd(new GuideEvent(session, input, bytes, sourceHash, modelRevision, gap, adaptationOmitted))) return true; }
        catch (InvalidOperationException) { }
        Interlocked.Add(ref _guideQueuedBytes, -bytes);
        RejectGuide(session, "Guide snapshot queue full");
        return false;
    }

    private static void RejectGuide(Session session, string reason)
    { Interlocked.Increment(ref session.GuideRejected); session.GuideError = reason; }

    private void SelectSession(Session session)
    {
        if (_active == session) return;
        ClosePart(); FlushGuideTimeline(); SaveIndex();
        _active = session; _part = 0; _parts.Clear(); _hashes.Clear(); _counts.Clear(); _first = _last = default;
        _guideFiles.Clear(); _guideSnapshotNumber = 0; _guideBytes = 0;
        _guideExpandedBytes = _guideArtifactBytes = _guideArtifactExpandedBytes = _guideSequence = _guideTimelineOmitted = 0;
        _guideTimelineNumber = _guideTimelineBytes = 0; _guideTimeline.Clear(); _guideLatest = null; _guideLastInput = null;
        _guideAdaptationHash = _guideAdaptationFile = _guideAttemptedSource = _guideAttemptedAdaptation = null;
    }

    private void WriteGuide(GuideEvent item)
    {
        SelectSession(item.Session);
        var input = item.Input;
        if (item.Gap != null) RejectGuide(item.Session, item.Gap);
        var sourceFile = item.SourceHash == null ? null : "guide-source-" + item.SourceHash + ".json.gz";
        if (input.Document != null && sourceFile != null && sourceFile != _guideAttemptedSource && !_guideFiles.Any(file => file.File == sourceFile))
        {
            _guideAttemptedSource = sourceFile;
            TryWriteGuideArtifact(sourceFile, "source", input.Document!, input);
        }
        string? guideHash = null;
        try
        {
            if (!item.AdaptationOmitted)
            {
                var adaptation = SerializeGuide(new
                {
                    input.Duty, input.Language, sourceHash = item.SourceHash,
                    modelRevision = item.ModelRevision, sourceCoverage = input.Document?.Coverage, input.Adapted, input.IdResolution
                }, GuideExpandedLimit);
                guideHash = Convert.ToHexString(SHA256.HashData(adaptation));
            }
        }
        catch (IOException error) { RejectGuide(item.Session, $"Guide adaptation omitted: {error.Message}"); }
        if (guideHash != null && guideHash != _guideAttemptedAdaptation)
        {
            _guideAttemptedAdaptation = guideHash;
            var filename = $"guide-snapshot-{++_guideSnapshotNumber:D6}.json.gz";
            if (TryWriteGuideArtifact(filename, "adapted", new
            {
                schema = 2, input.At, input.SessionID, input.TerritoryID, input.Duty, input.Language,
                sourceFile, sourceHash = item.SourceHash, guideHash, modelRevision = item.ModelRevision, gap = item.Gap,
                sourceCoverage = input.Document?.Coverage, input.State, input.Options, input.Adapted, input.Signals, input.Presentation,
                input.BindingAudits, input.BindingAuditsDropped, input.BindingAuditsPending, input.BindingMemoryState,
                input.IdResolution,
                availableSummaries = input.Adapted.Sum(boss => boss.Phases.Sum(phase => (phase.Summary == null ? 0 : 1) + phase.Mechanics.Count(mechanic => mechanic.Summary != null)))
            }, input))
            { _guideAdaptationHash = guideHash; _guideAdaptationFile = filename; }
        }
        var sourceAvailable = sourceFile == null || _guideFiles.Any(file => file.File == sourceFile);
        var adaptationAvailable = guideHash != null && guideHash == _guideAdaptationHash;
        var sequence = ++_guideSequence;
        byte[] frame;
        try
        {
            frame = SerializeGuide(new
            {
                sequence, input.At, input.SessionID, input.TerritoryID, input.Duty, input.Language,
                sourceHash = item.SourceHash, sourceFile = sourceAvailable ? sourceFile : null,
                guideHash, adaptedFile = adaptationAvailable ? _guideAdaptationFile : null,
                sourceAvailable, adaptationAvailable, input.State, input.Options, input.Signals, input.Presentation,
                input.BindingAudits, input.BindingAuditsDropped, input.BindingAuditsPending, input.BindingMemoryState,
                idResolutionState = input.IdResolution?.State, idPlanHash = input.IdResolution?.PlanHash, catalogHash = input.IdResolution?.CatalogHash,
                gap = item.Gap, rejected = Interlocked.Read(ref item.Session.GuideRejected), timelineOmitted = _guideTimelineOmitted
            }, GuideFrameLimit);
        }
        catch (IOException)
        {
            RejectGuide(item.Session, "Guide timeline sample exceeds its bounded frame size");
            ++_guideTimelineOmitted;
            frame = JsonSerializer.SerializeToUtf8Bytes(new { sequence, input.At, sourceHash = item.SourceHash, guideHash,
                state = input.State.State[..Math.Min(256, input.State.State.Length)],
                boss = input.State.Boss?[..Math.Min(256, input.State.Boss.Length)],
                listState = input.Presentation?.ListState[..Math.Min(256, input.Presentation.ListState.Length)],
                centralState = input.Presentation?.CentralState[..Math.Min(256, input.Presentation.CentralState.Length)],
                bindingAuditsOmitted = input.BindingAudits.Length, input.BindingAuditsDropped, input.BindingAuditsPending,
                gap = "Sample exceeds frame size limit; signals, binding audits, options and presentation rows omitted", rejected = item.Session.GuideRejected }, GuideJson);
        }
        using (var document = JsonDocument.Parse(frame)) _guideLatest = document.RootElement.Clone();
        if (_guideTimelineBytes + frame.Length + 1 > GuideBatchLimit || _guideTimeline.Count >= 256
            || _guideTimeline.Count > 0 && input.At - _guideBatchStarted >= TimeSpan.FromMinutes(1)) FlushGuideTimeline();
        if (_guideTimeline.Count == 0) _guideBatchStarted = input.At;
        _guideTimeline.Add(frame); _guideTimelineBytes += frame.Length + 1;
        _guideLastInput = input with { Document = null, Adapted = [], Signals = [], Presentation = null, BindingAudits = [],
            IdResolution = input.IdResolution is { } ids ? ids with { Triggers = [] } : null };
    }

    private void FlushGuideTimeline()
    {
        if (_guideTimeline.Count == 0) return;
        using var output = new MemoryStream();
        output.Write("{\"schema\":2,\"frames\":["u8);
        for (var index = 0; index < _guideTimeline.Count; ++index)
        {
            if (index != 0) output.WriteByte((byte)',');
            output.Write(_guideTimeline[index]);
        }
        output.Write("]}"u8);
        try { WriteGuideArtifact($"guide-timeline-{++_guideTimelineNumber:D6}.json.gz", "timeline", output.ToArray(), _guideLastInput!); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _guideTimelineOmitted += _guideTimeline.Count;
            RejectGuide(_active!, $"Guide timeline gap: {error.Message}");
        }
        _guideTimeline.Clear(); _guideTimelineBytes = 0; _guideLastInput = null;
        SaveIndex();
    }

    private bool TryWriteGuideArtifact(string filename, string kind, object payload, GuideCaptureInput input)
    {
        try { WriteGuideArtifact(filename, kind, SerializeGuide(payload, GuideExpandedLimit), input); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { RejectGuide(_active!, $"Guide {kind} omitted: {error.Message}"); return false; }
    }

    private sealed class GuideBuffer(int limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > limit) throw new IOException("Guide expanded size limit reached");
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > limit) throw new IOException("Guide expanded size limit reached");
            var bytes = buffer.ToArray();
            base.Write(bytes, 0, bytes.Length);
        }
    }

    private static byte[] SerializeGuide(object payload, int limit)
    {
        using var output = new GuideBuffer(limit);
        JsonSerializer.Serialize(output, payload, GuideJson);
        return output.ToArray();
    }

    private void WriteGuideArtifact(string filename, string kind, byte[] bytes, GuideCaptureInput input)
    {
        if (!ValidGuideFilename(filename)) throw new InvalidDataException("Invalid guide artifact filename");
        if (bytes.Length > GuideExpandedLimit) throw new IOException("Guide artifact exceeds expanded size limit");
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(bytes);
        var data = compressed.ToArray();
        var session = _active!;
        lock (_filesLock)
        {
            Directory.CreateDirectory(session.Directory);
            var reserve = (_file != null ? SegmentReserve : 0) + 2L * IndexReserve;
            PruneCache(session.Directory, reserve + data.Length);
            var used = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
            if (_guideBytes + data.Length > GuideBudget || _guideExpandedBytes + bytes.Length > GuideExpandedBudget
                || kind != "timeline" && (_guideArtifactBytes + data.Length > GuideBudget / 2 || _guideArtifactExpandedBytes + bytes.Length > GuideExpandedBudget / 2
                    || _guideFiles.Count(file => file.Kind != "timeline") >= 192
                    || Index().Length + GuideFrameLimit + IndexReserve / 8 + 1024 > IndexReserve)
                || used + data.Length + reserve > _cacheLimit || _guideFiles.Count >= 256
                || Index().Length + GuideFrameLimit + 1024 > IndexReserve)
                throw new IOException("Reserved guide storage or index limit reached");
            var final = Path.Combine(session.Directory, filename);
            File.WriteAllBytes(final + ".tmp", data);
            File.Move(final + ".tmp", final, overwrite: false);
            _guideFiles.Add(new(filename, Convert.ToHexString(SHA256.HashData(data)), data.Length, kind, input.At, input.Document?.SourceHash ?? "") { ExpandedBytes = bytes.Length });
            _guideBytes += data.Length; _guideExpandedBytes += bytes.Length;
            if (kind != "timeline") { _guideArtifactBytes += data.Length; _guideArtifactExpandedBytes += bytes.Length; }
        }
    }
}
