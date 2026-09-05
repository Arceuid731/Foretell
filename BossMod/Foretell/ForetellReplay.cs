using System.IO;
using System.Text.Json;
using System.Threading;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private const long MaxReadableReplayBytes = 512L * 1024 * 1024;

    private Task<ReplayReport>? _semanticReplayTask;
    private readonly CancellationTokenSource _semanticReplayCancellation = new();

    public ReplayReport ReplayLatest()
    {
        if (_semanticReplayTask is { IsCompleted: false }) return _lastReplayReport;
        var completed = _store.Sessions.OrderByDescending(s => s.Ended).FirstOrDefault(s => !string.IsNullOrEmpty(s.CaptureDirectory));
        var captureDirectory = completed == null ? _captureSession?.Directory
            : Path.Combine(Path.GetDirectoryName(_replayDir)!, "foretell-captures", Path.GetFileName(completed.CaptureDirectory));
        var activeDirectory = _captureSession?.Directory;
        var captureWriter = _capture;
        var captureTask = captureDirectory == null || _capture == null ? null : _capture.SnapshotAsync(captureDirectory);
        _lastReplayReport = new() { Status = "Detached evaluation running in background" };
        _semanticReplayTask = Task.Run(async () =>
        {
            using var capture = captureTask == null ? null : await captureTask;
            if (capture != null && capture.Parts.Length > 0) return EvaluateCapture(capture);
            using var active = captureWriter == null || activeDirectory == null || activeDirectory == captureDirectory
                ? null : await captureWriter.SnapshotAsync(activeDirectory);
            if (active != null && active.Parts.Length > 0) return EvaluateCapture(active);
            var latest = Directory.GetFiles(_replayDir, "foretell-*.jsonl")
                .Where(path => _replay == null || !string.Equals(path, _replayPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            return latest == null ? new ReplayReport { Status = "No sealed recording found" } : ReplayFile(latest);
        });
        return _lastReplayReport;
    }

    private ReplayReport EvaluateCapture(ForetellCapture.Snapshot capture)
    {
        var reader = new ForetellRecordingReader(Path.Combine(capture.Directory, "index.json"), capture.Index);
        reader.Inspect(_semanticReplayCancellation.Token);
        var result = EvaluateRecordedStream(reader.Read(), captureComplete: reader.Complete, cancellationToken: _semanticReplayCancellation.Token).Report;
        result.File = Path.GetFileName(capture.Directory);
        result.Rejected = (int)Math.Min(int.MaxValue, reader.Rejected);
        return result;
    }

    private void PollSemanticReplay()
    {
        if (_semanticReplayTask is not { IsCompleted: true } task) return;
        try { _lastReplayReport = task.GetAwaiter().GetResult(); }
        catch (Exception e) { _lastReplayReport = new() { Status = $"Detached evaluation failed: {e.Message}" }; }
        _semanticReplayTask = null;
    }

    private string? LatestRawJournal(bool includeCurrent = true)
        => Directory.GetFiles(_rawDir, "foretell-T*-*.ftraw.gz")
            .Where(path => includeCurrent || !string.Equals(path, _rawPath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

    private void StartRawAnalysis()
    {
        if (_rawAnalysisTask is { IsCompleted: false }) return;
        var path = LatestRawJournal();
        if (path == null)
        {
            _lastRawAnalysis = new() { Path = "", Errors = { "No raw journal found" } };
            return;
        }
        var territory = ParseRawTerritory(path);
        _rawAnalysisTask = Task.Run(() => ForetellRawFormat.Read(path, territory));
    }

    private void PollRawAnalysis()
    {
        if (_rawAnalysisTask is not { IsCompleted: true } task) return;
        try { _lastRawAnalysis = task.GetAwaiter().GetResult(); }
        catch (Exception e) { _lastRawAnalysis = new() { Errors = { $"Analysis failed: {e.Message}" } }; }
        _rawAnalysisTask = null;
    }

    private static uint ParseRawTerritory(string path)
    {
        var name = Path.GetFileName(path);
        var start = name.IndexOf("-T", StringComparison.Ordinal);
        if (start < 0) return 0;
        start += 2;
        var end = name.IndexOf('-', start);
        return end > start && uint.TryParse(name.AsSpan(start, end - start), out var territory) ? territory : 0;
    }

    private static bool TryParseJournalTime(string name, out DateTime timestamp)
    {
        timestamp = default;
        var secondDash = name.IndexOf('-', name.IndexOf("-T", StringComparison.Ordinal) + 2);
        if (secondDash < 0) return false;
        var value = name[(secondDash + 1)..];
        return DateTime.TryParseExact(value, ["yyyyMMdd-HHmmss", "yyyyMMdd-HHmmss-fff"],
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal | System.Globalization.DateTimeStyles.AdjustToUniversal, out timestamp);
    }

    private ReplayReport ReplayFile(string path)
    {
        try
        {
            var reader = new ForetellRecordingReader(path);
            reader.Inspect(_semanticReplayCancellation.Token);
            var result = EvaluateRecordedStream(reader.Read(), captureComplete: reader.Complete,
                cancellationToken: _semanticReplayCancellation.Token).Report;
            result.File = Path.GetFileName(path);
            result.Rejected = (int)Math.Min(int.MaxValue, reader.Rejected);
            return result;
        }
        catch (Exception e) { return new ReplayReport { File = Path.GetFileName(path), Status = $"Read/evaluation failed: {e.Message}" }; }
    }

    private ForetellObservation RawWindowObservation(ForetellRawFeatureWindow window, bool includeStructuralDetails = true)
    {
        var obs = new ForetellObservation
        {
            At = NormalizeObservationTime(window.At),
            TerritoryID = window.TerritoryID,
            Kind = ObservationKind.GenericFeature,
            SourceKind = SourceKind.Environment,
            Detail = "raw:feature-window"
        };
        obs.Numeric["raw.window.serverPackets"] = window.ServerPackets;
        obs.Numeric["raw.window.clientPackets"] = window.ClientPackets;
        obs.Numeric["raw.window.actorControls"] = window.ActorControls;
        obs.Numeric["raw.window.payloadBytes"] = window.PayloadBytes;
        foreach (var (opcode, count) in window.Opcodes) obs.Numeric[$"raw.window.opcode[{opcode:X8}]"] = count;
        for (var i = 0; i < window.BinaryBuckets.Length; ++i) obs.Numeric[$"raw.window.binaryBucket[{i}]"] = window.BinaryBuckets[i];
        if (includeStructuralDetails)
        {
            foreach (var (opcode, feature) in window.OpcodeFeatures)
            {
                var prefix = $"raw.window.structure[{opcode:X8}]";
                obs.Numeric[$"{prefix}.count"] = feature.Count;
                obs.Numeric[$"{prefix}.payloadBytes"] = feature.PayloadBytes;
                obs.Numeric[$"{prefix}.minLength"] = feature.MinLength;
                obs.Numeric[$"{prefix}.maxLength"] = feature.MaxLength;
                obs.Text[$"{prefix}.sequenceHash"] = feature.SequenceHash.ToString("X16");
                for (var i = 0; i < feature.ByteMeans.Length; ++i)
                {
                    obs.Numeric[$"{prefix}.byte[{i}].mean"] = feature.ByteMeans[i];
                    obs.Numeric[$"{prefix}.byte[{i}].variance"] = feature.ByteVariances[i];
                }
            }
            foreach (var (transition, count) in window.Transitions)
                obs.Numeric[$"raw.window.transition[{transition:X16}]"] = count;
        }
        return obs;
    }

    public string ExportDiagnostics()
    {
        var path = Path.Combine(_replayDir, $"foretell-diagnostics-T{_territory}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var encounter = _store.Encounters.GetValueOrDefault(_territory);
        var payload = new
        {
            generatedAt = DateTime.UtcNow,
            territory = _territory,
            session = new
            {
                _session.ID,
                _session.Started,
                _session.Pulls,
                _session.Phase,
                _session.Observations,
                _session.MechanicsFinalized,
                _session.NewMechanics,
                _session.AmbiguousMechanics,
                counts = _session.Counts
            },
            encounter,
            mlUpdates = _store.PreImpact.Model.Updates,
            activeEpisodes = _episodes.Values.Count(e => !e.Finalized),
            activePredictions = _predictions.Count,
            configuration = new
            {
                _cfg.Mode,
                _cfg.EnableLearning,
                _cfg.EnableML,
                _cfg.WorldOverlay,
                _cfg.TextHints,
                _cfg.TextHintsUnlocked,
                _cfg.TextPositionX,
                _cfg.TextPositionY,
                _cfg.SafePositionSuggestions,
                _cfg.VisualConfidence,
                _cfg.WarningConfidence,
                _cfg.SafeConfidence,
                _cfg.MaxRenderedMechanics,
                _cfg.MiniRadar,
                _cfg.RadarUnlocked,
                _cfg.RadarShape,
                _cfg.RadarZoom,
                _cfg.RadarAutoMinimumRadius,
                _cfg.RadarAutoMaximumRadius,
                _cfg.RadarTerrainStyle,
                _cfg.RadarTerrainColor,
                topologyAutomatic = true,
                _cfg.RadarSize,
                _cfg.RadarWorldRadius,
                _cfg.RecordReplay,
                _cfg.AutomaticStorageMaintenance
            },
            runtime = new
            {
                updateFailures = _updateFailures,
                updateOverruns = _updateOverruns,
                updateLastMilliseconds = _lastUpdateMilliseconds,
                updateMeanMilliseconds = _meanUpdateMilliseconds,
                updatePeakMilliseconds = _peakUpdateMilliseconds,
                semanticObservationsRejected = _semanticObservationsRejected,
                semanticBudgetTrips = _semanticBudgetTrips,
                semanticFrameMilliseconds = _semanticMillisecondsThisFrame,
                semanticPeakObservationMilliseconds = _semanticPeakMilliseconds,
                drawFailures = _drawFailures,
                performanceThrottled = PerformanceThrottled,
                typedSnapshotLastMilliseconds = _lastTypedSnapshotMilliseconds,
                typedSnapshotPeakMilliseconds = _peakTypedSnapshotMilliseconds,
                nativeActorLastMilliseconds = _lastNativeActorMilliseconds,
                nativeActorPeakMilliseconds = _peakNativeActorMilliseconds,
                rawFeatureDrainLastMilliseconds = _lastRawFeatureDrainMilliseconds,
                rawFeatureDrainPeakMilliseconds = _peakRawFeatureDrainMilliseconds,
                topologyLastMilliseconds = _lastTopologyMilliseconds,
                topologyPeakMilliseconds = _peakTopologyMilliseconds,
                topologySweepRequested = _topologySweepRequested,
                topologySweepInProgress = _topologySweepInProgress,
                topologyAnalysisComplete = _topologyAnalysisComplete,
                topologyFrontierPending = _topologyFrontier.Pending,
                topologyFrontierSampled = _topologyFrontier.Sampled,
                topologyFrontierReachable = _topologyFrontier.Reachable,
                topologyFirstSurfaceMilliseconds = _topologyFirstSurfaceMilliseconds,
                topologyLastSweepMilliseconds = _topologyLastSweepMilliseconds,
                topologyPeakSweepMilliseconds = _topologyPeakSweepMilliseconds,
                topologyMeshPrimary = _topologyMeshPrimary,
                topologyMeshCaptures = _topologyMeshCaptures,
                topologyMeshFallbacks = _topologyMeshFallbacks,
                topologyMeshTriangles = _topologyMeshTriangles,
                topologyMeshColliders = _topologyMeshColliders,
                topologyMeshFloorTriangles = _topologyMeshFloorTriangles,
                topologyMeshWallTriangles = _topologyMeshWallTriangles,
                topologyMeshCandidateSamples = _topologyMeshCandidateSamples,
                topologyMeshCaptureMilliseconds = _topologyMeshCaptureMilliseconds,
                topologyMeshRasterMilliseconds = _topologyMeshRasterMilliseconds,
                topologyMeshPeakCaptureMilliseconds = _topologyMeshPeakCaptureMilliseconds,
                topologyMeshPeakRasterMilliseconds = _topologyMeshPeakRasterMilliseconds,
                topologyMeshCaptureBudgetMilliseconds = _topologyMeshCaptureBudget,
                topologyMeshFastCaptureRetries = _topologyMeshFastCaptureRetries,
                topologyMeshCaptureTimeouts = _topologyMeshCaptureTimeouts,
                topologyAtomicSwaps = _topologyAtomicSwaps,
                topologyRetainedRebuilds = _topologyRetainedRebuilds,
                topologyWindowRecenters = _topologyWindowRecenters,
                topologyLastRefreshLatencyMilliseconds = _topologyLastRefreshLatencyMilliseconds,
                topologyPeakRefreshLatencyMilliseconds = _topologyPeakRefreshLatencyMilliseconds,
                topologyPublishedAgeMilliseconds = _topologyLastPublishedAt == default ? 0 : Math.Max(0, (DateTime.UtcNow - _topologyLastPublishedAt).TotalMilliseconds),
                topologySceneFingerprint = _topologySceneFingerprint.ToString("X16"),
                topologySceneFingerprintChanges = _topologySceneFingerprintChanges,
                topologySceneColliders = _topologySceneColliders,
                topologyFloorSamples = _topologyFloorSamples,
                topologyEdgeSamples = _topologyEdgeSamples,
                topologyPassableCells = _topologyAnalysis?.PassableCells ?? 0,
                topologyUnknownCells = _topologyAnalysis?.UnknownCells ?? 0,
                arenaBoundaryRays = _arenaBoundaryRays,
                arenaBoundarySweeps = _arenaBoundarySweeps,
                arenaBoundaryAccepted = CurrentArenaBoundary != null,
                activeDynamicTerrainWarnings = ActiveDynamicTerrainWarnings().Count()
            },
            dataComplete = new
            {
                rawJournal = _rawPath,
                rawJournalActive = true,
                rawPendingItems = _raw.PendingItems,
                rawPendingBytes = _raw.PendingBytes,
                rawWrittenItems = _raw.WrittenItems,
                rawWrittenBytes = _raw.WrittenBytes,
                rawRejectedItems = _raw.RejectedItems,
                actorControlSemanticRejected = Interlocked.Read(ref _ws.Network.RejectedActorControlSemantic),
                rawFeatureWindowsPending = _raw.PendingFeatureWindows,
                rawFeatureWindowsRejected = _raw.RejectedFeatureWindows,
                rawFeatureWindowsProcessed = _rawFeatureWindowsProcessed,
                rawFeatureDrainMilliseconds = _lastRawFeatureDrainMilliseconds,
                rawFeatureDrainPeakMilliseconds = _peakRawFeatureDrainMilliseconds,
                rawFailure = _raw.Failure,
                nativeHookCaptured = _nativeHookCaptured,
                nativeHookProcessed = _nativeHookProcessed,
                nativeHookPending = _nativeHookPending,
                nativeHookFailures = _nativeHookFailures,
                typedSnapshotFailures = _typedSnapshotFailures,
                nativeSnapshotFailures = _nativeSnapshotFailures,
                topologyRays = _topologyRays,
                topologySweeps = _topologySweeps,
                topologyChanges = _topologyChanges,
                topologyFailures = _topologyFailures,
                topologyOverruns = _topologyOverruns,
                topologySuspended = TopologySuspended,
                episodeRejections = _episodeRejections,
                semanticObservationsRejected = _semanticObservationsRejected,
                semanticBudgetTrips = _semanticBudgetTrips,
                learningEvictions = _learningEvictions,
                coverageUnaccounted = _store.Coverage.Unaccounted
            },
            replay = _lastReplayReport,
            lastEvidence = _lastEvidence
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, _diagnosticJson));
        return path;
    }
}
