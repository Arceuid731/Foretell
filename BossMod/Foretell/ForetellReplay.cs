using System.IO;
using System.Text.Json;
using System.Threading;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private const long MaxReadableReplayBytes = 512L * 1024 * 1024;
    private const int MaxReadableReplayLines = 2_000_000;

    private Task<ReplayReport>? _semanticReplayTask;
    private readonly CancellationTokenSource _semanticReplayCancellation = new();

    public ReplayReport ReplayLatest()
    {
        if (_semanticReplayTask is { IsCompleted: false }) return _lastReplayReport;
        var writer = _replay;
        _lastReplayReport = new() { Status = "Detached evaluation running in background" };
        _semanticReplayTask = Task.Run(() =>
        {
            writer?.Drain(TimeSpan.FromSeconds(2));
            var latest = Directory.GetFiles(_replayDir, "foretell-*.jsonl")
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            return latest == null ? new ReplayReport { Status = "No replay file found" } : ReplayFile(latest);
        });
        return _lastReplayReport;
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

    private IReadOnlyList<string> RawJournalsForReplay(string replayPath, DateTime first, DateTime last)
    {
        var replayName = Path.GetFileNameWithoutExtension(replayPath);
        var territory = ParseRawTerritory(replayPath);
        if (!TryParseJournalTime(replayName, out var replayAt))
            return LatestRawJournal(includeCurrent: false) is { } latest ? [latest] : [];

        var candidates = Directory.GetFiles(_rawDir, $"foretell-T{territory}-*.ftraw.gz")
            .Select(path => (Path: path,
                Parsed: TryParseJournalTime(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path)), out var at),
                At: at, Updated: File.GetLastWriteTimeUtc(path)))
            .Where(candidate => candidate.Parsed)
            .ToArray();
        var overlap = candidates
            .Where(candidate => candidate.At <= last.AddMinutes(1) && candidate.Updated >= first.AddMinutes(-1))
            .OrderBy(candidate => candidate.At)
            .Select(candidate => candidate.Path)
            .ToArray();
        if (overlap.Length != 0) return overlap;
        var nearest = candidates.OrderBy(candidate => Math.Abs((candidate.At - replayAt).TotalMilliseconds)).FirstOrDefault();
        return nearest.Path == null || Math.Abs((nearest.At - replayAt).TotalMinutes) > 10 ? [] : [nearest.Path];
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
        var report = new ReplayReport { File = Path.GetFileName(path), Status = "Parsing" };
        List<ForetellObservation> observations = [];
        try
        {
            var length = new FileInfo(path).Length;
            if (length > MaxReadableReplayBytes)
                throw new InvalidDataException($"readable replay is {length / (1024d * 1024):F0} MiB and exceeds the {MaxReadableReplayBytes / (1024 * 1024)} MiB in-memory safety limit");
            foreach (var line in File.ReadLines(path))
            {
                _semanticReplayCancellation.Token.ThrowIfCancellationRequested();
                ++report.Lines;
                if (report.Lines > MaxReadableReplayLines)
                    throw new InvalidDataException($"readable replay exceeds the {MaxReadableReplayLines:N0}-line in-memory safety limit");
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var observation = JsonSerializer.Deserialize<ForetellObservation>(line, _replayJson);
                    if (observation == null || observation.Kind == ObservationKind.Unknown || observation.At == default)
                    {
                        ++report.Rejected;
                        continue;
                    }
                    observation.At = NormalizeObservationTime(observation.At);
                    observations.Add(observation);
                    ++report.Parsed;
                    report.Counts[observation.Kind] = report.Counts.GetValueOrDefault(observation.Kind) + 1;
                    if (report.First == default || observation.At < report.First) report.First = observation.At;
                    if (report.Last == default || observation.At > report.Last) report.Last = observation.At;
                }
                catch { ++report.Rejected; }
            }
        }
        catch (Exception e)
        {
            report.Status = $"Read failed: {e.Message}";
            return report;
        }

        if (observations.Count == 0)
        {
            report.Status = report.Rejected > 0
                ? "No normalized V2 observations found (this can be an older replay)"
                : "Replay is empty";
            return report;
        }

        // Replay every rotated raw journal overlapping this exact territory session. Feeding unrelated zones into
        // a pull would create plausible-looking but causally false correlations.
        var rawPaths = RawJournalsForReplay(path, report.First, report.Last);
        foreach (var rawPath in rawPaths)
        {
            var raw = ForetellRawFormat.Read(rawPath, ParseRawTerritory(rawPath));
            report.RawRecords += raw.Records;
            report.RawWindows += raw.Windows.Count;
            report.RawErrors += raw.Errors.Count;
            foreach (var window in raw.Windows)
            {
                var rawObservation = RawWindowObservation(window);
                observations.Add(rawObservation);
                report.Counts[ObservationKind.GenericFeature] = report.Counts.GetValueOrDefault(ObservationKind.GenericFeature) + 1;
            }
        }
        report.Territories = observations.Select(o => o.TerritoryID).Distinct().Count();

        try
        {
            var evaluation = EvaluateRecordedObservations(observations, captureComplete: report.Rejected == 0 && report.RawErrors == 0, cancellationToken: _semanticReplayCancellation.Token);
            evaluation.Report.File = report.File;
            evaluation.Report.Lines = report.Lines;
            evaluation.Report.Rejected = report.Rejected;
            evaluation.Report.RawRecords = report.RawRecords;
            evaluation.Report.RawWindows = report.RawWindows;
            evaluation.Report.RawErrors = report.RawErrors;
            report = evaluation.Report;
        }
        catch (Exception e)
        {
            report.Status = $"Detached replay failed: {e.GetType().Name}: {e.Message}";
        }

        return report;
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
