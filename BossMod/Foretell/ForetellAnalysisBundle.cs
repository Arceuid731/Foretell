using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private sealed record AnalysisBundleWork(string OutputPath, byte[] Analysis, string[] RawPaths,
        string? ReplayPath, string[] Warnings, uint TerritoryID, string Content, string SessionID,
        string SessionPluginVersion, string ExporterPluginVersion, ForetellCollisionSnapshot? Collision);
    private sealed record AnalysisBundleResult(string Path, int RawFiles, bool ReadableReplay, int Decisions,
        string[] Warnings, string Error = "");

    private Task<AnalysisBundleResult>? _analysisBundleTask;
    private string _analysisBundlePath = "";
    private string _analysisBundleStatus = "";

    private void StartAnalysisBundleExport(EncounterMemory encounter)
    {
        if (_analysisBundleTask is { IsCompleted: false }) return;

        var now = DateTime.UtcNow;
        var completed = _store.Sessions.Where(session => session.TerritoryID == encounter.TerritoryID)
            .OrderByDescending(session => session.Ended).FirstOrDefault();
        var liveSelected = encounter.TerritoryID == _territory && _session.Observations > 0
            && (completed == null || _session.Started > completed.Ended);
        var selected = liveSelected
            ? new SessionSummary
            {
                SessionID = _session.ID,
                PluginVersion = _session.PluginVersion,
                TerritoryID = _session.TerritoryID,
                Started = _session.Started,
                Ended = now,
                Pulls = _session.Pulls,
                Observations = _session.Observations,
                MechanicsFinalized = _session.MechanicsFinalized,
                NewMechanics = _session.NewMechanics,
                AmbiguousMechanics = _session.AmbiguousMechanics,
                ReplayFile = Path.GetFileName(_replayPath)
            }
            : completed;
        var warnings = new List<string>();
        if (selected == null)
            warnings.Add("No completed or active learning session was found for this content; learned encounter memory is included without a run-scoped decision trail.");
        else if (string.IsNullOrWhiteSpace(selected.PluginVersion))
            warnings.Add("This session predates per-session version provenance; its capture version is unknown and must not be inferred from the exporter version.");
        if (liveSelected)
            warnings.Add("This session is still active. The current raw/replay segments are intentionally excluded until the territory changes and their writers seal them.");

        var rawPaths = AnalysisRawJournals(encounter.TerritoryID, selected, warnings);
        var replayPath = AnalysisReadableReplay(encounter.TerritoryID, selected, warnings);
        var sessionID = selected?.SessionID ?? "";
        var decisions = string.IsNullOrEmpty(sessionID)
            ? Array.Empty<DecisionAuditEntry>()
            : _store.DecisionAudit.Where(entry => entry.SessionID == sessionID && entry.TerritoryID == encounter.TerritoryID)
                .OrderBy(entry => entry.At).ToArray();
        if (selected != null && decisions.Length == 0)
            warnings.Add("No semantic decision audit exists for this run. Decision-level capture is available only for sessions recorded by Foretell 0.8.8 or newer.");

        var activePredictions = encounter.TerritoryID == _territory
            ? _predictions.Select(pair => new
            {
                id = pair.Key,
                pair.Value.ActionID,
                pair.Value.SignalKey,
                pair.Value.Label,
                pair.Value.Kind,
                pair.Value.Geometry,
                pair.Value.Guidance,
                originX = pair.Value.Origin.X,
                originZ = pair.Value.Origin.Y,
                targetX = pair.Value.Target.X,
                targetZ = pair.Value.Target.Y,
                pair.Value.Rotation,
                pair.Value.P1,
                pair.Value.P2,
                pair.Value.Activation,
                pair.Value.Confidence,
                pair.Value.Anticipated,
                pair.Value.Evidence
            }).Cast<object>().ToArray()
            : [];
        var exporterPluginVersion = CurrentPluginVersion;
        var sessionPluginVersion = string.IsNullOrWhiteSpace(selected?.PluginVersion) ? "unknown" : selected.PluginVersion;
        var analysis = new
        {
            formatSchema = 1,
            storeSchema = _store.Schema,
            generatedAt = now,
            pluginVersion = sessionPluginVersion,
            sessionPluginVersion,
            exporterPluginVersion,
            content = EncounterDisplayName(encounter),
            encounter,
            selectedSession = selected,
            decisionAudit = decisions,
            activePredictions,
            configuration = new
            {
                _cfg.Mode,
                _cfg.EnableLearning,
                _cfg.EnableML,
                _cfg.WorldOverlay,
                _cfg.TextHints,
                _cfg.SafePositionSuggestions,
                _cfg.MiniRadar,
                _cfg.RadarShape,
                _cfg.RadarZoom,
                _cfg.RadarAutoMinimumRadius,
                _cfg.RadarAutoMaximumRadius,
                _cfg.RadarTerrainStyle,
                _cfg.RadarTerrainColor,
                _cfg.RadarWorldRadius,
                _cfg.VisualConfidence,
                _cfg.WarningConfidence,
                _cfg.SafeConfidence,
                _cfg.MaxRenderedMechanics,
                _cfg.RecordReplay
            },
            runtimeAtExport = new
            {
                currentTerritory = _territory,
                currentSession = _session.ID,
                inPull = _inPull,
                performanceThrottled = PerformanceThrottled,
                updateFailures = _updateFailures,
                updateOverruns = _updateOverruns,
                updateLastMilliseconds = _lastUpdateMilliseconds,
                updateMeanMilliseconds = _meanUpdateMilliseconds,
                updatePeakMilliseconds = _peakUpdateMilliseconds,
                topologySuspended = TopologySuspended,
                topologyRays = _topologyRays,
                topologySweeps = _topologySweeps,
                topologyChanges = _topologyChanges,
                topologyInvalidations = _topologyInvalidations,
                topologyFloorSamples = _topologyFloorSamples,
                topologyEdgeSamples = _topologyEdgeSamples,
                topologyFailures = _topologyFailures,
                topologyOverruns = _topologyOverruns,
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
                topologyPassableCells = _topologyAnalysis?.PassableCells ?? 0,
                topologyUnknownCells = _topologyAnalysis?.UnknownCells ?? 0,
                arenaBoundaryRays = _arenaBoundaryRays,
                arenaBoundarySweeps = _arenaBoundarySweeps,
                arenaBoundaryAccepted = CurrentArenaBoundary != null,
                activeDynamicTerrainWarnings = ActiveDynamicTerrainWarnings().Count(),
                semanticObservationsRejected = _semanticObservationsRejected,
                semanticBudgetTrips = _semanticBudgetTrips,
                drawFailures = _drawFailures,
                episodeRejections = _episodeRejections,
                learningEvictions = _learningEvictions,
                rawPendingItems = _raw.PendingItems,
                rawPendingBytes = _raw.PendingBytes,
                rawWrittenItems = _raw.WrittenItems,
                rawWrittenBytes = _raw.WrittenBytes,
                rawRejectedItems = _raw.RejectedItems,
                rawFailure = _raw.Failure,
                coverage = new
                {
                    _store.Coverage.Discovered,
                    _store.Coverage.Ingested,
                    _store.Coverage.Used,
                    _store.Coverage.Excluded,
                    _store.Coverage.Unaccounted
                },
                lastEvidence = _lastEvidence
            }
        };
        var analysisBytes = JsonSerializer.SerializeToUtf8Bytes(analysis, _diagnosticJson);
        var outputPath = Path.Combine(_replayDir, $"foretell-analysis-T{encounter.TerritoryID}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var work = new AnalysisBundleWork(outputPath, analysisBytes, rawPaths, replayPath, warnings.ToArray(),
            encounter.TerritoryID, EncounterDisplayName(encounter), sessionID, sessionPluginVersion, exporterPluginVersion,
            liveSelected ? _lastCollisionSnapshot : null);
        _analysisBundleStatus = $"Packaging {rawPaths.Length} sealed raw journal(s) in the background...";
        _analysisBundleTask = Task.Run(() => CreateAnalysisBundle(work, decisions.Length));
    }

    private string[] AnalysisRawJournals(uint territoryID, SessionSummary? session, List<string> warnings)
    {
        if (session == null) return [];
        var writerPath = _raw.ActivePath;
        var candidates = Directory.GetFiles(_rawDir, $"foretell-T{territoryID}-*.ftraw.gz")
            .Where(path => !string.Equals(path, _rawPath, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(path, writerPath, StringComparison.OrdinalIgnoreCase))
            .Select(path => (Path: path,
                Parsed: TryParseJournalTime(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path)), out var at),
                At: at,
                Updated: File.GetLastWriteTimeUtc(path)))
            .Where(candidate => candidate.Parsed
                && candidate.At <= session.Ended.AddMinutes(1)
                && (candidate.At >= session.Started.AddMinutes(-1) || candidate.Updated >= session.Started.AddMinutes(-1)))
            .OrderBy(candidate => candidate.At)
            .ToArray();
        if (candidates.Length > 24)
        {
            warnings.Add($"The run overlaps {candidates.Length} raw segments; only the first 24 are included as a safety bound.");
            candidates = candidates.Take(24).ToArray();
        }
        if (candidates.Length == 0)
        {
            var stillActive = new[] { _rawPath, writerPath }.Where(path => !string.IsNullOrEmpty(path))
                .Any(path => ParseRawTerritory(path) == territoryID);
            warnings.Add(stillActive
                ? "The matching raw segment is still active and was not included. Leave the duty, wait a few seconds, then export this bundle again."
                : "No sealed raw journal matching this session was found.");
        }
        return candidates.Select(candidate => candidate.Path).ToArray();
    }

    private string? AnalysisReadableReplay(uint territoryID, SessionSummary? session, List<string> warnings)
    {
        if (session == null || string.IsNullOrWhiteSpace(session.ReplayFile)) return null;
        var path = Path.Combine(_replayDir, Path.GetFileName(session.ReplayFile));
        if (!File.Exists(path) || ParseRawTerritory(path) != territoryID) return null;
        if (string.Equals(path, _replayPath, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("The optional readable replay is still active and was not included; the sealed raw journal remains the authoritative capture.");
            return null;
        }
        if (new FileInfo(path).Length > MaxReadableReplayBytes)
        {
            warnings.Add("The optional readable replay exceeds the 512 MiB safety limit and was not included.");
            return null;
        }
        return path;
    }

    private AnalysisBundleResult CreateAnalysisBundle(AnalysisBundleWork work, int decisions)
    {
        var temporary = work.OutputPath + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                WriteBundleBytes(archive, "foretell-analysis.json", work.Analysis);
                var manifest = new
                {
                    formatSchema = 1,
                    generatedAt = DateTime.UtcNow,
                    work.TerritoryID,
                    work.Content,
                    work.SessionID,
                    work.SessionPluginVersion,
                    work.ExporterPluginVersion,
                    contents = new
                    {
                        analysis = "foretell-analysis.json",
                        rawJournals = work.RawPaths.Select(path => $"raw/{Path.GetFileName(path)}").ToArray(),
                        readableReplay = work.ReplayPath == null ? null : $"replay/{Path.GetFileName(work.ReplayPath)}",
                        collisionSnapshot = work.Collision == null ? null : "terrain/collision.ftrc"
                    },
                    collisionSnapshotMeaning = "Latest completed local capture for the selected live session at export; not the historical terrain of a completed run. Replay with ForetellCoreTests --collision <analysis.zip>.",
                    semantics = "Raw journals contain exact transport/ActorControl input. foretell-analysis.json contains the learned encounter snapshot and the bounded Detected/Proposed/Classified/Verified decision trail.",
                    displayEligibleMeaning = "The confidence/mode/settings gate passed when the decision was made; it does not prove a pixel was rendered if the draw surface later failed.",
                    warnings = work.Warnings
                };
                WriteBundleBytes(archive, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, _diagnosticJson));
                foreach (var raw in work.RawPaths)
                    CopyBundleFile(archive, raw, $"raw/{Path.GetFileName(raw)}", CompressionLevel.NoCompression);
                if (work.ReplayPath != null)
                    CopyBundleFile(archive, work.ReplayPath, $"replay/{Path.GetFileName(work.ReplayPath)}", CompressionLevel.Fastest);
                if (work.Collision != null)
                {
                    using var output = archive.CreateEntry("terrain/collision.ftrc", CompressionLevel.Fastest).Open();
                    ForetellCollisionSnapshotIO.Write(output, work.Collision);
                }
            }
            File.Move(temporary, work.OutputPath, true);
            return new(work.OutputPath, work.RawPaths.Length, work.ReplayPath != null, decisions, work.Warnings);
        }
        catch (Exception e)
        {
            return new("", 0, false, decisions, work.Warnings, $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static void WriteBundleBytes(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var output = entry.Open();
        output.Write(bytes);
    }

    private static void CopyBundleFile(ZipArchive archive, string source, string name, CompressionLevel compression)
    {
        var entry = archive.CreateEntry(name, compression);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
        using var output = entry.Open();
        input.CopyTo(output, 65536);
    }

    private void PollAnalysisBundleExport()
    {
        if (_analysisBundleTask is not { IsCompleted: true } task) return;
        try
        {
            var result = task.GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(result.Error))
            {
                _analysisBundlePath = result.Path;
                _analysisBundleStatus = $"Ready: {result.RawFiles} raw journal(s), {result.Decisions} decision records"
                    + (result.ReadableReplay ? ", readable replay included" : "");
                if (result.Warnings.Length != 0) _analysisBundleStatus += $" · {result.Warnings.Length} warning(s) in manifest";
                Service.ChatGui.Print($"Foretell analysis bundle: {result.Path}");
            }
            else
            {
                _analysisBundleStatus = $"Analysis bundle failed: {result.Error}";
                Service.ChatGui.PrintError(_analysisBundleStatus);
            }
        }
        catch (Exception e)
        {
            _analysisBundleStatus = $"Analysis bundle failed: {e.Message}";
            Service.ChatGui.PrintError(_analysisBundleStatus);
        }
        _analysisBundleTask = null;
    }
}
