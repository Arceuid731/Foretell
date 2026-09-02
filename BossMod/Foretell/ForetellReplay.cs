using System.IO;
using System.Text.Json;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    public ReplayReport ReplayLatest()
    {
        _replay?.Drain(TimeSpan.FromSeconds(2));
        var latest = Directory.GetFiles(_replayDir, "foretell-*.jsonl")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (latest == null)
            return _lastReplayReport = new() { Status = "No replay file found" };
        return ReplayFile(latest);
    }

    private ReplayReport ReplayFile(string path)
    {
        var report = new ReplayReport { File = Path.GetFileName(path), Status = "Parsing" };
        List<ForetellObservation> observations = [];
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                ++report.Lines;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var observation = JsonSerializer.Deserialize<ForetellObservation>(line, _replayJson);
                    if (observation == null || observation.Kind == ObservationKind.Unknown || observation.At == default)
                    {
                        ++report.Rejected;
                        continue;
                    }
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

        report.Territories = observations.Select(o => o.TerritoryID).Distinct().Count();

        // Swap the complete learning state for an isolated sandbox. The same ProcessObservation/FinalizeEpisode
        // pipeline used live is executed here, but nothing is persisted and no replay is written recursively.
        var liveStore = _store;
        var liveClassifier = _classifier;
        var liveSession = _session;
        var liveEpisodes = _episodes;
        var liveTracks = _tracks;
        var livePredictions = _predictions;
        var liveRecent = _recentSignals;
        var liveTerritory = _territory;
        var liveSequence = _sequence;
        var livePreviousAction = _previousAction;
        var livePreviousActionTime = _previousActionTime;
        var livePreviousSignal = _previousSignal;
        var livePreviousSignalTime = _previousSignalTime;
        var liveInPull = _inPull;
        var liveLastCombat = _lastCombatSignal;
        var liveEvidence = _lastEvidence;
        var liveLearning = _cfg.EnableLearning;

        try
        {
            _cfg.EnableLearning = true; // sandbox must exercise learning even if live learning is disabled
            _store = new();
            _classifier = new(_store.ML);
            _episodes = [];
            _tracks = [];
            _predictions = [];
            _recentSignals = new();
            _territory = observations[0].TerritoryID;
            _session = NewSession(_territory);
            _sequence = 0;
            _previousAction = 0;
            _previousActionTime = default;
            _previousSignal = "";
            _previousSignalTime = default;
            _inPull = false;
            _lastCombatSignal = default;
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
                    FinalizeDue(DateTime.MaxValue);
                    _territory = observation.TerritoryID;
                    _session = NewSession(_territory);
                    _episodes.Clear();
                    _tracks.Clear();
                    _previousAction = 0;
                    _previousSignal = "";
                    _inPull = false;
                    StartEncounterSession(_territory);
                }
                ProcessObservation(observation, replaying: true);
            }
            FinalizeDue(DateTime.MaxValue);

            report.RediscoveredMechanics = _store.Encounters.Values.Sum(e => e.Mechanics.Count);
            report.AmbiguousMechanics = _store.Encounters.Values.Sum(e => e.Mechanics.Values.Sum(m => m.AmbiguousSamples));
            report.Status = $"OK - sandbox reprocessed {semanticObservations} semantic observations ({report.Parsed - semanticObservations} raw transport records retained) and rediscovered {report.RediscoveredMechanics} mechanics";
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
            _tracks = liveTracks;
            _predictions = livePredictions;
            _recentSignals = liveRecent;
            _territory = liveTerritory;
            _sequence = liveSequence;
            _previousAction = livePreviousAction;
            _previousActionTime = livePreviousActionTime;
            _previousSignal = livePreviousSignal;
            _previousSignalTime = livePreviousSignalTime;
            _inPull = liveInPull;
            _lastCombatSignal = liveLastCombat;
            _lastEvidence = liveEvidence;
        }

        return _lastReplayReport = report;
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
                rawFailure = _raw.Failure,
                nativeHookCaptured = _nativeHookCaptured,
                nativeHookProcessed = _nativeHookProcessed,
                nativeHookPending = _nativeHookPending,
                nativeHookFailures = _nativeHookFailures,
                typedSnapshotFailures = _typedSnapshotFailures,
                nativeSnapshotFailures = _nativeSnapshotFailures,
                coverageUnaccounted = _store.Coverage.Unaccounted
            },
            replay = _lastReplayReport,
            lastEvidence = _lastEvidence
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, _json));
        return path;
    }
}
