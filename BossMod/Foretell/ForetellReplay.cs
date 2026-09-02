using System.IO;
using System.Text.Json;
using System.Threading;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private const long MaxReadableReplayBytes = 512L * 1024 * 1024;
    private const int MaxReadableReplayLines = 2_000_000;

    public ReplayReport ReplayLatest()
    {
        if (_inPull)
            return _lastReplayReport = new() { Status = "Replay is disabled during combat to protect frame time" };
        _replay?.Drain(TimeSpan.FromSeconds(2));
        var latest = Directory.GetFiles(_replayDir, "foretell-*.jsonl")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (latest == null)
            return _lastReplayReport = new() { Status = "No replay file found" };
        return ReplayFile(latest);
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
            return _lastReplayReport = report;
        }

        if (observations.Count == 0)
        {
            report.Status = report.Rejected > 0
                ? "No normalized V2 observations found (this can be an older replay)"
                : "Replay is empty";
            return _lastReplayReport = report;
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

        // Swap the complete learning state for an isolated sandbox. The same ProcessObservation/FinalizeEpisode
        // pipeline used live is executed here, but nothing is persisted and no replay is written recursively.
        var liveStore = _store;
        var liveClassifier = _classifier;
        var liveSession = _session;
        var liveEpisodes = _episodes;
        var liveEpisodeFinalization = _episodeFinalization;
        var liveEpisodeCleanup = _episodeCleanup;
        var liveTracks = _tracks;
        var livePredictions = _predictions;
        var liveTimelineForecasts = _timelineForecasts;
        var liveNextForecastID = _nextForecastID;
        var liveEffectSequenceEpisodes = _effectSequenceEpisodes;
        var liveEpisodeRejections = _episodeRejections;
        var liveLearningEvictions = _learningEvictions;
        var liveTerritory = _territory;
        var liveSequence = _sequence;
        var livePreviousAction = _previousAction;
        var livePreviousActionTime = _previousActionTime;
        var livePreviousSignal = _previousSignal;
        var livePreviousSignalTime = _previousSignalTime;
        var liveInPull = _inPull;
        var liveLastCombat = _lastCombatSignal;
        var livePullStartedAt = _pullStartedAt;
        var liveLastPhaseBoundary = _lastPhaseBoundary;
        var liveLastPhaseBoundarySignal = _lastPhaseBoundarySignal;
        var liveUntargetableSince = _untargetableSince.ToArray();
        var livePhaseBoundariesThisPull = _phaseBoundariesThisPull.ToArray();
        var livePhaseTopologyFingerprint = _phaseTopologyFingerprint;
        var liveEvidence = _lastEvidence;
        var liveLearning = _cfg.EnableLearning;

        try
        {
            _cfg.EnableLearning = true; // sandbox must exercise learning even if live learning is disabled
            _store = new();
            _classifier = new(_store.ML);
            _episodes = [];
            _episodeFinalization = new();
            _episodeCleanup = new();
            _tracks = [];
            _predictions = [];
            _timelineForecasts = [];
            _nextForecastID = -1;
            _effectSequenceEpisodes = [];
            _episodeRejections = 0;
            _learningEvictions = 0;
            _territory = observations[0].TerritoryID;
            _session = NewSession(_territory);
            _sequence = 0;
            _previousAction = 0;
            _previousActionTime = default;
            _previousSignal = "";
            _previousSignalTime = default;
            _inPull = false;
            _lastCombatSignal = default;
            _pullStartedAt = default;
            _lastPhaseBoundary = default;
            _lastPhaseBoundarySignal = "";
            _untargetableSince.Clear();
            _phaseBoundariesThisPull.Clear();
            _phaseTopologyFingerprint = "";
            StartEncounterSession(_territory);

            var semanticObservations = 0;
            foreach (var observation in observations.OrderBy(o => o.At).ThenBy(o => o.Sequence))
            {
                // Raw transport is retained losslessly for offline protocol work, but it must not be interpreted a
                // second time by the semantic mechanic learner during Replay Lab.
                if (observation.Detail.StartsWith("transport:", StringComparison.Ordinal))
                    continue;
                ++semanticObservations;
                if (observation.TerritoryID != _territory)
                {
                    FinalizeDue(DateTime.MaxValue, exhaustive: true);
                    _territory = observation.TerritoryID;
                    _session = NewSession(_territory);
                    _episodes.Clear();
                    _episodeFinalization.Clear();
                    _episodeCleanup.Clear();
                    _tracks.Clear();
                    _timelineForecasts.Clear();
                    _predictions.Clear();
                    _previousAction = 0;
                    _previousSignal = "";
                    _inPull = false;
                    _pullStartedAt = default;
                    _lastCombatSignal = default;
                    _lastPhaseBoundary = default;
                    _lastPhaseBoundarySignal = "";
                    _untargetableSince.Clear();
                    _phaseBoundariesThisPull.Clear();
                    _phaseTopologyFingerprint = "";
                    StartEncounterSession(_territory);
                }
                ProcessObservation(observation, replaying: true);
            }
            FinalizeDue(DateTime.MaxValue, exhaustive: true);

            report.RediscoveredMechanics = _store.Encounters.Values.Sum(e => e.Mechanics.Count);
            report.AmbiguousMechanics = _store.Encounters.Values.Sum(e => e.Mechanics.Values.Sum(m => m.AmbiguousSamples));
            report.Status = $"OK - sandbox reprocessed {semanticObservations} observations including {report.RawWindows} deterministic raw windows from {rawPaths.Count} journal(s) and rediscovered {report.RediscoveredMechanics} mechanics";
        }
        catch (Exception e)
        {
            report.Status = $"Replay pipeline failed: {e.GetType().Name}: {e.Message}";
        }
        finally
        {
            _cfg.EnableLearning = liveLearning;
            _store = liveStore;
            _classifier = liveClassifier;
            _session = liveSession;
            _episodes = liveEpisodes;
            _episodeFinalization = liveEpisodeFinalization;
            _episodeCleanup = liveEpisodeCleanup;
            _tracks = liveTracks;
            _predictions = livePredictions;
            _timelineForecasts = liveTimelineForecasts;
            _nextForecastID = liveNextForecastID;
            _effectSequenceEpisodes = liveEffectSequenceEpisodes;
            _episodeRejections = liveEpisodeRejections;
            _learningEvictions = liveLearningEvictions;
            _territory = liveTerritory;
            _sequence = liveSequence;
            _previousAction = livePreviousAction;
            _previousActionTime = livePreviousActionTime;
            _previousSignal = livePreviousSignal;
            _previousSignalTime = livePreviousSignalTime;
            _inPull = liveInPull;
            _lastCombatSignal = liveLastCombat;
            _pullStartedAt = livePullStartedAt;
            _lastPhaseBoundary = liveLastPhaseBoundary;
            _lastPhaseBoundarySignal = liveLastPhaseBoundarySignal;
            _untargetableSince.Clear();
            foreach (var (actorID, since) in liveUntargetableSince)
                _untargetableSince[actorID] = since;
            _phaseBoundariesThisPull.Clear();
            _phaseBoundariesThisPull.UnionWith(livePhaseBoundariesThisPull);
            _phaseTopologyFingerprint = livePhaseTopologyFingerprint;
            _lastEvidence = liveEvidence;
        }

        return _lastReplayReport = report;
    }

    private ForetellObservation RawWindowObservation(ForetellRawFeatureWindow window)
    {
        var obs = new ForetellObservation
        {
            At = NormalizeObservationTime(window.At),
            TerritoryID = window.TerritoryID,
            Kind = ObservationKind.GenericFeature,
            SourceKind = SourceKind.Environment,
            Detail = "raw:250ms-window"
        };
        obs.Numeric["raw.window.serverPackets"] = window.ServerPackets;
        obs.Numeric["raw.window.clientPackets"] = window.ClientPackets;
        obs.Numeric["raw.window.actorControls"] = window.ActorControls;
        obs.Numeric["raw.window.payloadBytes"] = window.PayloadBytes;
        foreach (var (opcode, count) in window.Opcodes) obs.Numeric[$"raw.window.opcode[{opcode:X8}]"] = count;
        for (var i = 0; i < window.BinaryBuckets.Length; ++i) obs.Numeric[$"raw.window.binaryBucket[{i}]"] = window.BinaryBuckets[i];
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
            mlUpdates = _store.ML.Updates,
            activeEpisodes = _episodes.Values.Count(e => !e.Finalized),
            activePredictions = _predictions.Count,
            dataComplete = new
            {
                rawJournal = _rawPath,
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
                learningEvictions = _learningEvictions,
                coverageUnaccounted = _store.Coverage.Unaccounted
            },
            replay = _lastReplayReport,
            lastEvidence = _lastEvidence
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, _json));
        return path;
    }
}
