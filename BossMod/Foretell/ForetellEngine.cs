using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine : IDisposable
{
    private const long MaxLearnedStoreBytes = 256L * 1024 * 1024;
    // Emergency fail-closed tier: direct client-memory readers and hooks remain quarantined until every detour
    // only copies bounded primitives into a queue and all interpretation runs on the framework thread.
    private static readonly bool NativeHookTelemetryEnabled = true;
    private static readonly bool NativeSnapshotTelemetryEnabled = true;
    private readonly WorldState _ws;
    private readonly ForetellConfig _cfg;
    private readonly string _storePath;
    private readonly string _replayDir;
    private readonly string _rawDir;
    private readonly string _signalFilterPath;
    private readonly EventSubscriptions _subscriptions;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = false, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, Converters = { new JsonStringEnumConverter() } };
    private readonly JsonSerializerOptions _diagnosticJson = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, Converters = { new JsonStringEnumConverter() } };
    private readonly JsonSerializerOptions _replayJson = new() { WriteIndented = false, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, Converters = { new JsonStringEnumConverter() } };

    private ForetellStore _store;
    private OnlineClassifier _classifier;
    private ForetellReplayWriter? _replay;
    private readonly ForetellRawWriter _raw;
    private string _replayPath = "";
    private string _rawPath = "";
    private DateTime _rawOpenedAt;
    private sealed record RawCaptureContext(string Path, uint TerritoryID);
    private volatile RawCaptureContext _rawCaptureContext = new("", 0);
    private bool _rawCaptureAttached;
    private long _sequence;
    private DateTime _lastSave;
    private DateTime _lastPositionSample;
    private uint _territory;
    private LiveSessionStats _session;

    private Dictionary<long, MechanicEpisode> _episodes = [];
    private PriorityQueue<long, long> _episodeFinalization = new();
    private PriorityQueue<long, long> _episodeCleanup = new();
    private Dictionary<ulong, ParticipantTrack> _tracks = [];
    private readonly HashSet<ulong> _activePositionTrackIDs = [];
    private Dictionary<long, ActivePrediction> _predictions = [];
    private Dictionary<long, PendingTimelineForecast> _timelineForecasts = [];
    private long _nextForecastID = -1;
    private Dictionary<uint, long> _effectSequenceEpisodes = [];
    private long _episodeRejections;
    private long _learningEvictions;
    // WorldState callbacks run outside Update(), so Update's watchdog cannot account for their cost. Keep a
    // separate hard budget keyed to the game frame; bursty multi-target packs may shed derived semantic work,
    // while the independently queued raw transport journal remains exact and replayable.
    private const int MaxSemanticObservationsPerFrame = 48;
    private const double MaxSemanticMillisecondsPerFrame = 0.85;
    private const int MaxPrioritySemanticObservationsPerFrame = 72;
    private const double MaxPrioritySemanticMillisecondsPerFrame = 1.5;
    private long _semanticBudgetFrameTicks;
    private long _semanticBudgetTrippedFrameTicks;
    private int _semanticObservationsThisFrame;
    private double _semanticMillisecondsThisFrame;
    private long _semanticObservationsRejected;
    private long _semanticBudgetTrips;
    private double _semanticPeakMilliseconds;
    private long _finalizationBudgetFrameTicks;
    private int _finalizationsThisFrame;

    private uint _previousAction;
    private DateTime _previousActionTime;
    private string _previousSignal = "";
    private DateTime _previousSignalTime;
    private string _lastEvidence = "Waiting for observations";
    private bool _inPull;
    private DateTime _lastCombatSignal;
    private DateTime _pullStartedAt;
    private DateTime _phaseStartedAt;
    private DateTime _lastContextForecastSample;
    private DateTime _hazardContextUntil;
    private DateTime _lastPhaseBoundary;
    private string _lastPhaseBoundarySignal = "";
    private readonly Dictionary<ulong, DateTime> _untargetableSince = [];
    private readonly HashSet<string> _phaseBoundariesThisPull = [];
    private string _phaseTopologyFingerprint = "";
    private readonly Dictionary<string, int> _signalOccurrencesThisPull = [];
    private readonly HashSet<string> _skippedTriggerContextsThisPull = [];
    private readonly List<SignalTriggerMemory> _triggerForecastCandidates = [];
    private bool _retryTriggerForecastCandidates;
    private readonly Dictionary<ulong, BossHealthTrack> _bossHealthTracks = [];
    private readonly Dictionary<uint, BossHealthSnapshot?> _bossHealthSnapshots = [];

    private bool _inspectorOpen;
    private ReplayReport _lastReplayReport = new();
    private Task<ForetellRawReadReport>? _rawAnalysisTask;
    private ForetellRawReadReport? _lastRawAnalysis;
    private long _updateFailures;
    private long _updateOverruns;
    private double _lastUpdateMilliseconds;
    private double _peakUpdateMilliseconds;
    private double _meanUpdateMilliseconds;
    private long _updateSamples;
    private int _consecutiveUpdateOverruns;
    private DateTime _adaptiveThrottleUntil;
    private DateTime _lastUpdateFailureLog;
    private DateTime _lastStorageMaintenance;
    private Task<ForetellStorageMaintenanceResult>? _storageMaintenanceTask;
    private ForetellStorageMaintenanceResult _lastStorageMaintenanceResult = new();
    private bool _disposed;
    internal bool PerformanceThrottled => DateTime.UtcNow < _adaptiveThrottleUntil;

    private bool SemanticBudgetAvailable(bool priority = false)
    {
        var frameTicks = _ws.CurrentTime.Ticks;
        if (frameTicks != _semanticBudgetFrameTicks)
        {
            _semanticBudgetFrameTicks = frameTicks;
            _semanticObservationsThisFrame = 0;
            _semanticMillisecondsThisFrame = 0;
        }
        var maxObservations = priority ? MaxPrioritySemanticObservationsPerFrame : MaxSemanticObservationsPerFrame;
        var maxMilliseconds = priority ? MaxPrioritySemanticMillisecondsPerFrame : MaxSemanticMillisecondsPerFrame;
        return _semanticObservationsThisFrame < maxObservations
            && _semanticMillisecondsThisFrame < maxMilliseconds;
    }

    private bool TryEnterSemanticBudget(bool priority = false)
    {
        if (!SemanticBudgetAvailable(priority))
        {
            ++_semanticObservationsRejected;
            return false;
        }
        ++_semanticObservationsThisFrame;
        return true;
    }

    private void ChargeSemanticBudget(long started)
    {
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _semanticMillisecondsThisFrame += elapsed;
        _semanticPeakMilliseconds = Math.Max(_semanticPeakMilliseconds, elapsed);
        if (_semanticMillisecondsThisFrame < MaxSemanticMillisecondsPerFrame
            || _semanticBudgetTrippedFrameTicks == _semanticBudgetFrameTicks)
            return;
        _semanticBudgetTrippedFrameTicks = _semanticBudgetFrameTicks;
        ++_semanticBudgetTrips;
        // The per-frame rejection gate already bounds callback cost. Cross-frame throttling is reserved for the
        // Update watchdog below, where three genuinely slow frames demonstrate sustained load; otherwise a normal
        // alliance-raid burst could keep optional VFX/topology ingestion suspended for the entire encounter.
    }

    internal ForetellStore Store => _store;
    internal LiveSessionStats Session => _session;
    internal ReplayReport LastReplayReport => _lastReplayReport;
    internal string ReplayPath => _replayPath;
    internal uint TerritoryID => _territory;
    internal IEnumerable<MechanicEpisode> Episodes => _episodes.Values;
    internal IEnumerable<ForetellObservation> RecentObservations => _session.Recent;

    public ForetellEngine(WorldState ws, string configDirectory)
    {
        _ws = ws;
        _cfg = Service.Config.Get<ForetellConfig>();
        ApplyPerformancePolicyMigration();
        _storePath = Path.Combine(configDirectory, "foretell-memory.json");
        _replayDir = Path.Combine(configDirectory, "foretell-replays");
        _rawDir = Path.Combine(configDirectory, "foretell-raw");
        _signalFilterPath = Path.Combine(configDirectory, "foretell-signal-filters.json");
        Directory.CreateDirectory(_replayDir);
        Directory.CreateDirectory(_rawDir);
        _store = LoadStore();
        _lastSave = DateTime.UtcNow;
        _subscriptions = new();
        _raw = new();

        // Everything after background-resource construction is one transactional startup region. Passive hooks
        // and subscriptions are installed last; any failure unwinds every Foretell-owned resource in reverse.
        try
        {
            NormalizeStore();
            _classifier = new(_store.ML);
            _territory = CurrentTerritory();
            _session = NewSession(_territory);
            StartEncounterSession(_territory);
            foreach (var actor in _ws.Actors)
                OnActorAdded(actor);
            SamplePartyPositions();
            // Startup only touches managed/typed WorldState. Native Character/environment/camera reads begin on
            // the first normal framework update, after plugin and game-scene initialization have completed.
            SampleDataFabric(force: true, includeNative: false);
            OpenRawJournal();
            AttachRawCapture();
            SyncReplayWriter();
            InstallForetellCommand();
            SubscribeToWorldState();
            InitializeDalamudSignals();
            // Native detours are the final startup action so no game callback can observe a partially subscribed
            // engine. An unavailable signature remains an explicit degraded capability, not a load failure.
            if (NativeHookTelemetryEnabled)
                InitializeNativeHooks();
            else
                ClassifyNativeTelemetryQuarantine();
        }
        catch
        {
            _ws.Network.CaptureRawTransport = false;
            DetachRawCapture();
            try { _subscriptions.Dispose(); } catch { }
            try { DisposeNativeHooks(); } catch { }
            _replay?.Dispose();
            _replay = null;
            _raw.Dispose();
            Service.CommandManager.RemoveHandler("/foretell");
            throw;
        }
    }

    private void SubscribeToWorldState()
    {
        _subscriptions.Add(_ws.Modified.Subscribe(OnWorldOperation));
        _subscriptions.Add(_ws.SystemLogMessage.Subscribe(OnSystemLog));
        _subscriptions.Add(_ws.Network.RawActorControlReceived.Subscribe(OnRawActorControl));
        _subscriptions.Add(_ws.Actors.Added.Subscribe(OnActorAdded));
        _subscriptions.Add(_ws.Actors.Removed.Subscribe(OnActorRemoved));
        _subscriptions.Add(_ws.Actors.CastStarted.Subscribe(OnCastStarted));
        _subscriptions.Add(_ws.Actors.CastFinished.Subscribe(OnCastFinished));
        _subscriptions.Add(_ws.Actors.IsTargetableChanged.Subscribe(OnTargetableChanged));
        _subscriptions.Add(_ws.Actors.IsDeadChanged.Subscribe(OnDeathChanged));
        _subscriptions.Add(_ws.Actors.RenderflagsChanged.Subscribe(OnRenderFlagsChanged));
        _subscriptions.Add(_ws.Actors.EventStateChanged.Subscribe(OnEventStateChanged));
        _subscriptions.Add(_ws.Actors.Tethered.Subscribe(OnTether));
        _subscriptions.Add(_ws.Actors.Untethered.Subscribe(OnUntether));
        _subscriptions.Add(_ws.Actors.StatusGain.Subscribe(OnStatusGain));
        _subscriptions.Add(_ws.Actors.StatusLose.Subscribe(OnStatusLose));
        _subscriptions.Add(_ws.Actors.IconAppeared.Subscribe(OnIcon));
        _subscriptions.Add(_ws.Actors.VFXAppeared.Subscribe(OnVFX));
        _subscriptions.Add(_ws.Actors.CastEvent.Subscribe(OnCastEvent));
        _subscriptions.Add(_ws.Actors.EffectResult.Subscribe(OnEffectResult));
        _subscriptions.Add(_ws.Actors.EventObjectStateChange.Subscribe(OnEventObjectState));
        _subscriptions.Add(_ws.Actors.EventObjectAnimation.Subscribe(OnEventObjectAnimation));
        _subscriptions.Add(_ws.Actors.PlayActionTimelineEvent.Subscribe(OnActionTimelineEvent));
        _subscriptions.Add(_ws.Actors.PlayActionTimelineSync.Subscribe(OnActionTimelineSync));
        _subscriptions.Add(_ws.Actors.EventNpcYell.Subscribe(OnNpcYell));
        _subscriptions.Add(_ws.Actors.ModelStateChanged.Subscribe(OnModelStateChanged));
        _subscriptions.Add(_ws.MapEffect.Subscribe(OnMapEffect));
        _subscriptions.Add(_ws.LegacyMapEffect.Subscribe(OnLegacyMapEffect));
        _subscriptions.Add(_ws.DirectorUpdate.Subscribe(OnDirectorUpdate));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { FinalizeDue(DateTime.MaxValue, exhaustive: true); CompleteSession(); SaveStore(); }
        catch (Exception e) { Service.Log($"[Foretell] Final save during dispose failed safely: {e.Message}"); }
        _ws.Network.CaptureRawTransport = false;
        DetachRawCapture();
        try { _subscriptions.Dispose(); } catch (Exception e) { Service.Log($"[Foretell] Subscription shutdown failed safely: {e.Message}"); }
        try { DisposeNativeHooks(); } catch (Exception e) { Service.Log($"[Foretell] Native hook shutdown failed safely: {e.Message}"); }
        try { _replay?.Dispose(); } catch (Exception e) { Service.Log($"[Foretell] Replay shutdown failed safely: {e.Message}"); }
        _replay = null;
        try { _raw.Dispose(); } catch (Exception e) { Service.Log($"[Foretell] Raw shutdown failed safely: {e.Message}"); }
        try { Service.CommandManager.RemoveHandler("/foretell"); } catch (Exception e) { Service.Log($"[Foretell] Command shutdown failed safely: {e.Message}"); }
    }

    public void Update()
    {
        if (_disposed) return;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { UpdateCore(); }
        catch (Exception e)
        {
            ++_updateFailures;
            var now = DateTime.UtcNow;
            if ((now - _lastUpdateFailureLog).TotalSeconds >= 5)
            {
                _lastUpdateFailureLog = now;
                Service.Log($"[Foretell] Frame update rejected safely ({_updateFailures} total): {e.GetType().Name}: {e.Message}");
            }
        }
        finally
        {
            _lastUpdateMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _peakUpdateMilliseconds = Math.Max(_peakUpdateMilliseconds, _lastUpdateMilliseconds);
            _meanUpdateMilliseconds += (_lastUpdateMilliseconds - _meanUpdateMilliseconds) / ++_updateSamples;
            if (_lastUpdateMilliseconds > 2)
            {
                ++_updateOverruns;
                if (++_consecutiveUpdateOverruns >= 3)
                {
                    _adaptiveThrottleUntil = DateTime.UtcNow.AddSeconds(15);
                    _topologySuspendedUntil = _adaptiveThrottleUntil;
                    _consecutiveUpdateOverruns = 0;
                }
            }
            else _consecutiveUpdateOverruns = 0;
        }
    }

    private void UpdateCore()
    {
        var now = ObservationNow();
        var gameInCombat = Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];
        var territory = CurrentTerritory();
        if (territory != _territory)
            ChangeTerritory(territory);
        else if (_store.Encounters.TryGetValue(_territory, out var currentEncounter) && _ws.CurrentCFCID != 0 && currentEncounter.ContentFinderConditionID != _ws.CurrentCFCID)
            RefreshEncounterIdentity(currentEncounter, _ws.CurrentCFCID);

        // The combat flag normally changes before the first cast packet. Starting the phase clock here lets an
        // already learned T+N mechanic be announced before its trigger in duties and in open-world encounters.
        if (!_inPull && gameInCombat)
            BeginCombatPull(_store.Encounters.GetValueOrDefault(_territory), now);

        SyncReplayWriter();
        if ((DateTime.UtcNow - _rawOpenedAt).TotalHours >= 1)
            OpenRawJournal();
        // During burst recovery, exact captures continue on their bounded background queues. Optional derived
        // drains and native topology wait so they cannot compete with the game for the same frame.
        if (!PerformanceThrottled)
        {
            DrainRawFeatureWindows();
            DrainNativeCaptures();
        }
        PollStorageMaintenance();
        PollAnalysisBundleExport();
        if (!PerformanceThrottled)
            SampleNativeTopology();
        RefreshLearnedArenaSourceContext();
        if ((now - _lastPositionSample).TotalMilliseconds >= 250)
        {
            SamplePartyPositions();
            SampleDataFabric();
            UpdateTriggerContextForecasts(now);
            _lastPositionSample = now;
        }

        FinalizeDue(now);
        ExpireHazardContext(now);
        ExpireTimelineForecasts(now);
        foreach (var key in _predictions.Where(p => p.Value.Activation.AddSeconds(1.5) < now).Select(p => p.Key).ToArray())
            ExpirePrediction(key, "display lifetime ended");

        // The game combat condition is available globally. Ending on its falling edge prevents stale predictions
        // and 3D drawings after an open-world enemy or an instanced boss dies.
        if (_inPull && !gameInCombat)
            EndCombatPull();

        // Persistence is deliberately kept off the active-combat path. Store serialization can grow with learned
        // history; saving after combat preserves it without creating a periodic gameplay hitch.
        if (!_inPull && !gameInCombat && (DateTime.UtcNow - _lastSave).TotalSeconds > 60)
            SaveStore();
        if (!_inPull && !gameInCombat && _cfg.AutomaticStorageMaintenance && _storageMaintenanceTask == null
            && (DateTime.UtcNow - _lastStorageMaintenance).TotalMinutes >= 10)
            StartStorageMaintenance();
    }

    public void ToggleInspector() => _inspectorOpen = !_inspectorOpen;
    public void OpenInspector() => _inspectorOpen = true;

    private void InstallForetellCommand()
    {
        try
        {
            Service.CommandManager.RemoveHandler("/foretell");
            Service.CommandManager.AddHandler("/foretell", new Dalamud.Game.Command.CommandInfo((_, args) =>
            {
                var split = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (!HandleCommand(split))
                    Service.ChatGui.Print("Foretell: /foretell [inspect|stats|replay|export|save]");
            }) { HelpMessage = "Open Foretell Inspector or run Foretell learning/replay commands" });
        }
        catch (Exception e)
        {
            Service.Log($"[Foretell] Failed to install /foretell command: {e.Message}");
        }
    }

    private void ChangeTerritory(uint territory)
    {
        FinalizeDue(DateTime.MaxValue, exhaustive: true);
        CompleteSession();
        _episodes.Clear();
        _episodeFinalization.Clear();
        _episodeCleanup.Clear();
        _tracks.Clear();
        _activePositionTrackIDs.Clear();
        ResetDataFabric();
        ResetTopology();
        _predictions.Clear();
        ClearDynamicTerrainWarnings();
        _timelineForecasts.Clear();
        _nextForecastID = -1;
        _effectSequenceEpisodes.Clear();
        _semanticBudgetFrameTicks = 0;
        _semanticBudgetTrippedFrameTicks = 0;
        _semanticObservationsThisFrame = 0;
        _semanticMillisecondsThisFrame = 0;
        _finalizationBudgetFrameTicks = 0;
        _finalizationsThisFrame = 0;
        _actorControlGates.Clear();
        _previousAction = 0;
        _previousSignal = "";
        _inPull = false;
        _pullStartedAt = default;
        _phaseStartedAt = default;
        _lastContextForecastSample = default;
        _hazardContextUntil = default;
        _lastPhaseBoundary = default;
        _lastPhaseBoundarySignal = "";
        _untargetableSince.Clear();
        _phaseBoundariesThisPull.Clear();
        _phaseTopologyFingerprint = "";
        _signalOccurrencesThisPull.Clear();
        _skippedTriggerContextsThisPull.Clear();
        _triggerForecastCandidates.Clear();
        _retryTriggerForecastCandidates = false;
        _bossHealthTracks.Clear();
        _bossHealthSnapshots.Clear();
        _territory = territory;
        _session = NewSession(territory);
        StartEncounterSession(territory);
        ReopenReplayWriter();
        OpenRawJournal();
        _lastEvidence = $"Entered territory {territory}";
    }

    private static string CurrentPluginVersion => typeof(ForetellEngine).Assembly.GetName().Version?.ToString() ?? "unknown";

    private LiveSessionStats NewSession(uint territory) => new() { TerritoryID = territory, PluginVersion = CurrentPluginVersion };

    private void StartEncounterSession(uint territory)
    {
        var encounter = Encounter(territory);
        RefreshEncounterIdentity(encounter, territory == _territory ? _ws.CurrentCFCID : encounter.ContentFinderConditionID);
        ++encounter.Sessions;
        if (encounter.FirstSeen == default) encounter.FirstSeen = DateTime.UtcNow;
        encounter.LastSeen = DateTime.UtcNow;
    }

    private void CompleteSession()
    {
        if (_session.Observations == 0) return;
        _store.Sessions.Add(new()
        {
            SessionID = _session.ID,
            PluginVersion = _session.PluginVersion,
            TerritoryID = _session.TerritoryID,
            Started = _session.Started,
            Ended = DateTime.UtcNow,
            Pulls = _session.Pulls,
            Observations = _session.Observations,
            MechanicsFinalized = _session.MechanicsFinalized,
            NewMechanics = _session.NewMechanics,
            AmbiguousMechanics = _session.AmbiguousMechanics,
            ReplayFile = Path.GetFileName(_replayPath)
        });
        while (_store.Sessions.Count > 100)
            _store.Sessions.RemoveAt(0);
    }

    private uint CurrentTerritory()
    {
        try { return Convert.ToUInt32(Service.ClientState.TerritoryType); }
        catch { return 0; }
    }

    private EncounterMemory Encounter(uint territory)
    {
        if (!_store.Encounters.TryGetValue(territory, out var encounter))
        {
            encounter = new() { TerritoryID = territory, FirstSeen = DateTime.UtcNow, LastSeen = DateTime.UtcNow };
            _store.Encounters[territory] = encounter;
        }
        return encounter;
    }

    private void ApplyPerformancePolicyMigration()
    {
        var changed = false;
        if (_cfg.ReplayPerformancePolicyVersion < 1)
        {
            // Older builds enabled synchronous JSON replay recording by default. Disable it once for both existing
            // and new configurations; users can explicitly opt into the new background writer afterwards.
            _cfg.RecordReplay = false;
            _cfg.ReplayPerformancePolicyVersion = 1;
            changed = true;
        }

        // Persisted config is user-editable and survives plugin upgrades. Never let NaN, infinity or an obsolete
        // enum value reach ImGui/DX11; invalid window geometry is capable of destabilising the graphics driver.
        // Numeric configs from the short-lived v0.8.1 Hybrid mode used value 3; both old presentation variants
        // intentionally converge on the new combined BMR + Foretell Hybrid mode.
        if ((int)_cfg.Mode == 3) { _cfg.Mode = ForetellMode.Hybrid; changed = true; }
        changed |= NormalizeEnum(ref _cfg.Mode, ForetellMode.Observe);
        changed |= NormalizeEnum(ref _cfg.RadarShape, ForetellRadarShape.Auto);
        changed |= NormalizeEnum(ref _cfg.RadarZoom, ForetellRadarZoom.Automatic);
        changed |= NormalizeEnum(ref _cfg.RadarTerrainStyle, ForetellRadarTerrainStyle.Outline);
        changed |= NormalizeFinite(ref _cfg.VisualConfidence, 75, 50, 100);
        changed |= NormalizeFinite(ref _cfg.WarningConfidence, 95, _cfg.VisualConfidence, 100);
        changed |= NormalizeFinite(ref _cfg.SafeConfidence, 99, _cfg.WarningConfidence, 100);
        changed |= NormalizeFinite(ref _cfg.RadarWorldRadius, 30, 5, 120);
        changed |= NormalizeFinite(ref _cfg.RadarAutoMinimumRadius, 30, 10, 60);
        changed |= NormalizeFinite(ref _cfg.RadarAutoMaximumRadius, 65, Math.Max(20, _cfg.RadarAutoMinimumRadius), 120);
        changed |= NormalizeFinite(ref _cfg.RadarSize, 220, 140, 600);
        var maxRendered = Math.Clamp(_cfg.MaxRenderedMechanics, 1, 32);
        if (maxRendered != _cfg.MaxRenderedMechanics) { _cfg.MaxRenderedMechanics = maxRendered; changed = true; }
        var retentionDays = Math.Clamp(_cfg.RecordingRetentionDays, 1, 365);
        if (retentionDays != _cfg.RecordingRetentionDays) { _cfg.RecordingRetentionDays = retentionDays; changed = true; }
        var storageGiB = Math.Clamp(_cfg.MaximumRecordingStorageGiB, 1, 100);
        if (storageGiB != _cfg.MaximumRecordingStorageGiB) { _cfg.MaximumRecordingStorageGiB = storageGiB; changed = true; }
        if (!float.IsFinite(_cfg.RadarPositionX) || !float.IsFinite(_cfg.RadarPositionY))
        {
            _cfg.RadarPositionX = _cfg.RadarPositionY = -1;
            changed = true;
        }
        else if (_cfg.RadarPositionX >= 0 && _cfg.RadarPositionY >= 0)
        {
            var x = Math.Clamp(_cfg.RadarPositionX, 0, 1);
            var y = Math.Clamp(_cfg.RadarPositionY, 0, 1);
            if (x != _cfg.RadarPositionX || y != _cfg.RadarPositionY)
            {
                _cfg.RadarPositionX = x;
                _cfg.RadarPositionY = y;
                changed = true;
            }
        }
        else if (_cfg.RadarPositionX != -1 || _cfg.RadarPositionY != -1)
        {
            _cfg.RadarPositionX = _cfg.RadarPositionY = -1;
            changed = true;
        }
        if (!float.IsFinite(_cfg.TextPositionX) || !float.IsFinite(_cfg.TextPositionY))
        {
            _cfg.TextPositionX = _cfg.TextPositionY = -1;
            changed = true;
        }
        else if (_cfg.TextPositionX >= 0 && _cfg.TextPositionY >= 0)
        {
            var x = Math.Clamp(_cfg.TextPositionX, 0, 1);
            var y = Math.Clamp(_cfg.TextPositionY, 0, 1);
            if (x != _cfg.TextPositionX || y != _cfg.TextPositionY)
            {
                _cfg.TextPositionX = x;
                _cfg.TextPositionY = y;
                changed = true;
            }
        }
        else if (_cfg.TextPositionX != -1 || _cfg.TextPositionY != -1)
        {
            _cfg.TextPositionX = _cfg.TextPositionY = -1;
            changed = true;
        }
        if (changed) _cfg.Modified.Fire();
    }

    private static bool NormalizeEnum<T>(ref T value, T fallback) where T : struct, Enum
    {
        if (Enum.IsDefined(value)) return false;
        value = fallback;
        return true;
    }

    private static bool NormalizeFinite(ref float value, float fallback, float minimum, float maximum)
    {
        var normalized = float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
        if (normalized == value) return false;
        value = normalized;
        return true;
    }

    private void SyncReplayWriter()
    {
        // Data-complete transport capture is always armed while Foretell is alive. Replay Lab is an optional
        // human-readable JSON mirror; the compact compressed raw journal is the lossless production archive.
        _ws.Network.CaptureRawTransport = true;
        if (_cfg.RecordReplay && _replay == null)
            OpenReplayWriter();
        else if (!_cfg.RecordReplay && _replay != null)
        {
            _replay.Dispose();
            _replay = null;
        }
    }

    private void ReopenReplayWriter()
    {
        _replay?.Dispose();
        _replay = null;
        if (_cfg.RecordReplay) OpenReplayWriter();
    }

    private void OpenReplayWriter()
    {
        _replayPath = Path.Combine(_replayDir, $"foretell-T{_territory}-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        _replay ??= new(_replayJson);
    }

    private void OpenRawJournal()
    {
        _rawPath = Path.Combine(_rawDir, $"foretell-T{_territory}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.ftraw.gz");
        _rawOpenedAt = DateTime.UtcNow;
        _rawCaptureContext = new(_rawPath, _territory);
    }

    private void AttachRawCapture()
    {
        _ws.Network.RawServerIPCCapture = OnRawServerIPC;
        _ws.Network.RawClientIPCCapture = OnRawClientIPC;
        _ws.Network.RawActorControlCapture = OnRawActorControlCapture;
        _rawCaptureAttached = true;
    }

    private void DetachRawCapture()
    {
        if (!_rawCaptureAttached) return;
        if (_ws.Network.RawServerIPCCapture == OnRawServerIPC) _ws.Network.RawServerIPCCapture = null;
        if (_ws.Network.RawClientIPCCapture == OnRawClientIPC) _ws.Network.RawClientIPCCapture = null;
        if (_ws.Network.RawActorControlCapture == OnRawActorControlCapture) _ws.Network.RawActorControlCapture = null;
        _rawCaptureAttached = false;
    }

    private ForetellStore LoadStore()
    {
        var temporaryPath = _storePath + ".tmp";
        foreach (var candidate in new[] { _storePath, temporaryPath })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var size = new FileInfo(candidate).Length;
                if (size > MaxLearnedStoreBytes)
                    throw new InvalidDataException($"learned memory is {size / (1024d * 1024):F0} MiB and exceeds the {MaxLearnedStoreBytes / (1024 * 1024)} MiB load safety limit");
                var store = JsonSerializer.Deserialize<ForetellStore>(File.ReadAllText(candidate), _json) ?? new();
                if (candidate == temporaryPath)
                {
                    File.Move(temporaryPath, _storePath, true);
                    Service.Log("[Foretell] Recovered learned memory from an interrupted atomic save.");
                }
                return store;
            }
            catch (Exception e)
            {
                Service.Log($"[Foretell] Failed to load {Path.GetFileName(candidate)}: {e.Message}");
                PreserveRejectedStore(candidate);
            }
        }
        return new();
    }

    private static void PreserveRejectedStore(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var backup = $"{path}.rejected-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
            File.Move(path, backup, false);
            Service.Log($"[Foretell] Rejected memory was preserved as {Path.GetFileName(backup)}");
        }
        catch (Exception backupError) { Service.Log($"[Foretell] Could not preserve rejected memory: {backupError.Message}"); }
    }

    private void NormalizeStore()
    {
        var loadedSchema = _store.Schema;
        // The old live reflection scanner generated hundreds of thousands of dynamic diagnostic paths. Coverage
        // is an audit index, not learned mechanic evidence, so compact it once without touching learned data.
        if (_store.Schema < 9)
            _store.Coverage = new();
        _store.Schema = Math.Max(_store.Schema, 22);
        _store.Mechanics ??= [];
        _store.Timeline ??= [];
        _store.Encounters ??= [];
        _store.Sessions ??= [];
        _store.DecisionAudit ??= [];
        _store.ML ??= new();
        _store.Coverage ??= new();
        _store.Coverage.Items ??= [];
        foreach (var key in _store.Mechanics.Where(item => item.Value == null).Select(item => item.Key).ToArray()) _store.Mechanics.Remove(key);
        foreach (var key in _store.Timeline.Where(item => item.Value == null).Select(item => item.Key).ToArray()) _store.Timeline.Remove(key);
        foreach (var key in _store.Encounters.Where(item => item.Value == null).Select(item => item.Key).ToArray()) _store.Encounters.Remove(key);
        foreach (var key in _store.Coverage.Items.Where(item => item.Value == null).Select(item => item.Key).ToArray()) _store.Coverage.Items.Remove(key);
        foreach (var mechanic in _store.Mechanics.Values) NormalizeLearnedMechanic(mechanic);
        foreach (var edge in _store.Timeline.Values) NormalizeTimelineEdge(edge);
        while (_store.Mechanics.Count > 8192) _store.Mechanics.Remove(_store.Mechanics.MinBy(item => item.Value.LastSeen).Key);
        while (_store.Timeline.Count > 8192) _store.Timeline.Remove(_store.Timeline.MinBy(item => item.Value.Count).Key);
        if (_store.Encounters.Count > 2048)
            foreach (var key in _store.Encounters.OrderByDescending(item => item.Value.LastSeen).Skip(2048).Select(item => item.Key).ToArray()) _store.Encounters.Remove(key);
        if (_store.Coverage.Items.Count > 65536)
            foreach (var key in _store.Coverage.Items.OrderByDescending(item => item.Value.Seen).Skip(65536).Select(item => item.Key).ToArray()) _store.Coverage.Items.Remove(key);
        _store.Sessions.RemoveAll(session => session == null);
        foreach (var session in _store.Sessions)
        {
            session.SessionID ??= "";
            session.PluginVersion ??= "";
            session.ReplayFile ??= "";
        }
        if (_store.Sessions.Count > 100) _store.Sessions = _store.Sessions.OrderByDescending(session => session.Started).Take(100).OrderBy(session => session.Started).ToList();
        _store.DecisionAudit.RemoveAll(entry => entry == null);
        foreach (var entry in _store.DecisionAudit)
        {
            entry.SessionID ??= "";
            entry.SignalKey ??= "";
            entry.TriggerDetail ??= "";
            entry.Label ??= "";
            entry.Evidence ??= "";
            entry.P1 = Finite(entry.P1, 0, 0, 10000);
            entry.P2 = Finite(entry.P2, 0, 0, 10000);
            entry.OriginX = Finite(entry.OriginX, 0, -100000, 100000);
            entry.OriginZ = Finite(entry.OriginZ, 0, -100000, 100000);
            entry.TargetX = Finite(entry.TargetX, 0, -100000, 100000);
            entry.TargetZ = Finite(entry.TargetZ, 0, -100000, 100000);
            entry.Rotation = Finite(entry.Rotation, 0, -1000, 1000);
            entry.Confidence = Finite(entry.Confidence, 0, 0, 1);
        }
        if (_store.DecisionAudit.Count > 8192)
            _store.DecisionAudit = _store.DecisionAudit.OrderByDescending(entry => entry.At).Take(8192).OrderBy(entry => entry.At).ToList();

        var migratedInvalidActions = new HashSet<uint>();
        var migratedInvalidMechanics = 0;
        var migratedUnsafeMetadata = 0;
        foreach (var encounterKey in _store.Encounters.Keys.ToArray())
        {
            try
            {
                var encounter = _store.Encounters[encounterKey];
                encounter.TerritoryID = encounterKey;
                RefreshEncounterIdentity(encounter, encounter.TerritoryID == _territory && _ws.CurrentCFCID != 0 ? _ws.CurrentCFCID : encounter.ContentFinderConditionID);
                encounter.ObservationCounts ??= [];
                encounter.Sources ??= [];
                encounter.Mechanics ??= [];
                encounter.Timeline ??= [];
                encounter.TriggerContexts ??= [];
                encounter.Phases ??= [];
                encounter.PhaseBoundaries ??= [];
                encounter.Composites ??= [];
                encounter.CausalEdges ??= [];
                encounter.RawOpcodes ??= [];
                encounter.Topologies ??= [];
                encounter.ArenaBoundaries ??= [];
                encounter.ExcludedSignals ??= [];
                RemoveNullValues(encounter.Sources);
                RemoveNullValues(encounter.Mechanics);
                RemoveNullValues(encounter.Timeline);
                RemoveNullValues(encounter.TriggerContexts);
                RemoveNullValues(encounter.Phases);
                RemoveNullValues(encounter.PhaseBoundaries);
                RemoveNullValues(encounter.Composites);
                RemoveNullValues(encounter.CausalEdges);
                RemoveNullValues(encounter.RawOpcodes);
                RemoveNullValues(encounter.Topologies);
                RemoveNullValues(encounter.ArenaBoundaries);
                RemoveNullValues(encounter.ExcludedSignals);
                if (loadedSchema < 14)
                    MigrateUnreliableV08DerivedMemory(encounter);
                if (loadedSchema < 17)
                    migratedInvalidMechanics += MigrateInvalidMechanicSources(encounter, migratedInvalidActions);
                if (loadedSchema < 20)
                    ResetPre20ForecastOutcomes(encounter);
                if (loadedSchema < 21)
                    migratedUnsafeMetadata += MigratePre21ActionMetadata(encounter);
                foreach (var mechanic in encounter.Mechanics.Values) NormalizeContextualMechanic(mechanic);
                foreach (var edge in encounter.Timeline.Values) NormalizeSignalTimelineEdge(edge);
                foreach (var trigger in encounter.TriggerContexts.Values) NormalizeSignalTriggerMemory(trigger);
                foreach (var edge in encounter.CausalEdges.Values)
                {
                    edge.Cause ??= "";
                    edge.Effect ??= "";
                    edge.Count = Math.Max(0, edge.Count);
                    edge.ExactLinks = Math.Clamp(edge.ExactLinks, 0, edge.Count);
                    edge.MeanDelay = Finite(edge.MeanDelay, 0, 0, 120);
                    edge.M2 = Finite(edge.M2, 0, 0, double.MaxValue);
                }
                foreach (var raw in encounter.RawOpcodes.Values)
                {
                    raw.Windows = Math.Max(0, raw.Windows);
                    raw.Packets = Math.Max(0, raw.Packets);
                    raw.PayloadBytes = Math.Max(0, raw.PayloadBytes);
                    raw.MeanLength = Finite(raw.MeanLength, 0, 0, ForetellRawFormat.MaxPayloadBytes);
                    raw.LengthM2 = Finite(raw.LengthM2, 0, 0, double.MaxValue);
                    raw.MinLength = raw.MinLength == int.MaxValue ? 0 : Math.Clamp(raw.MinLength, 0, ForetellRawFormat.MaxPayloadBytes);
                    raw.MaxLength = Math.Clamp(raw.MaxLength, raw.MinLength, ForetellRawFormat.MaxPayloadBytes);
                    raw.StructuralChanges = Math.Max(0, raw.StructuralChanges);
                }
                foreach (var source in encounter.Sources.Values)
                {
                    source.Name ??= "";
                    source.Observations = Math.Max(0, source.Observations);
                    source.Casts = Math.Max(0, source.Casts);
                    source.Signals = Math.Max(0, source.Signals);
                    source.Deaths = Math.Max(0, source.Deaths);
                    source.MaximumHitboxRadius = Finite(source.MaximumHitboxRadius, 0, 0, 100);
                    source.ArenaContextObservations = Math.Max(0, source.ArenaContextObservations);
                    source.BossCandidateObservations = Math.Clamp(source.BossCandidateObservations, 0, source.ArenaContextObservations);
                }
                foreach (var key in encounter.ExcludedSignals.Keys.ToArray())
                {
                    var exclusion = encounter.ExcludedSignals[key];
                    exclusion.Signal = string.IsNullOrWhiteSpace(exclusion.Signal) ? key : exclusion.Signal;
                    exclusion.Label ??= "";
                    if (exclusion.CreatedAt == default) exclusion.CreatedAt = DateTime.UtcNow;
                    if (string.IsNullOrWhiteSpace(key) || !string.Equals(key, exclusion.Signal, StringComparison.Ordinal))
                    {
                        encounter.ExcludedSignals.Remove(key);
                        if (!string.IsNullOrWhiteSpace(exclusion.Signal)) encounter.ExcludedSignals[exclusion.Signal] = exclusion;
                    }
                }
                if (encounter.ExcludedSignals.Count > 4096)
                    foreach (var key in encounter.ExcludedSignals.Values.OrderByDescending(item => item.CreatedAt).Skip(4096).Select(item => item.Signal).ToArray())
                        encounter.ExcludedSignals.Remove(key);
                foreach (var phase in encounter.Phases.Values)
                {
                    phase.Signals ??= [];
                    phase.Seen = Math.Max(0, phase.Seen);
                    foreach (var signal in phase.Signals.Where(item => item.Value < 0).Select(item => item.Key).ToArray()) phase.Signals.Remove(signal);
                    if (phase.Signals.Count > 2048)
                        foreach (var signal in phase.Signals.OrderByDescending(item => item.Value).Skip(2048).Select(item => item.Key).ToArray()) phase.Signals.Remove(signal);
                }
                foreach (var composite in encounter.Composites.Values)
                {
                    composite.Key ??= "";
                    composite.Signals ??= [];
                    composite.Signals = composite.Signals.Where(signal => !string.IsNullOrWhiteSpace(signal)).Distinct().Take(64).ToList();
                    composite.Count = Math.Max(0, composite.Count);
                    composite.MeanSkewSeconds = Finite(composite.MeanSkewSeconds, 0, 0, 120);
                    composite.M2 = Finite(composite.M2, 0, 0, double.MaxValue);
                    composite.Forecasts = Math.Max(0, composite.Forecasts);
                    composite.Hits = Math.Clamp(composite.Hits, 0, composite.Forecasts);
                    composite.Misses = Math.Clamp(composite.Misses, 0, composite.Forecasts - composite.Hits);
                }
                foreach (var topologyKey in encounter.Topologies.Keys.ToArray())
                    if (!NormalizeTopology(encounter.Topologies[topologyKey])) encounter.Topologies.Remove(topologyKey);
                foreach (var boundaryKey in encounter.ArenaBoundaries.Keys.ToArray())
                    if (!NormalizeArenaBoundary(encounter.ArenaBoundaries[boundaryKey])
                        || loadedSchema < 20 && !encounter.ArenaBoundaries[boundaryKey].ArenaLike)
                        encounter.ArenaBoundaries.Remove(boundaryKey);
                TrimEncounterCollections(encounter);
                encounter.Sessions = Math.Max(0, encounter.Sessions);
                encounter.Pulls = Math.Max(0, encounter.Pulls);
            }
            catch (Exception e)
            {
                _store.Encounters.Remove(encounterKey);
                Service.Log($"[Foretell] Rejected malformed learned encounter {encounterKey} safely: {e.Message}");
            }
        }
        if (loadedSchema < 17)
        {
            var validEnemyActions = _store.Encounters.Values.SelectMany(encounter => encounter.Mechanics.Values)
                .Where(mechanic => mechanic.TriggerKind == ObservationKind.CastStart && mechanic.SourceKind is not SourceKind.Player and not SourceKind.Pet)
                .Select(mechanic => mechanic.TriggerID)
                .ToHashSet();
            migratedInvalidActions.ExceptWith(validEnemyActions);
            foreach (var action in migratedInvalidActions)
                _store.Mechanics.Remove(action);
            foreach (var key in _store.Timeline.Where(item => migratedInvalidActions.Contains(item.Value.From) || migratedInvalidActions.Contains(item.Value.To)).Select(item => item.Key).ToArray())
                _store.Timeline.Remove(key);
            if (migratedInvalidMechanics > 0)
            {
                // The classifier was trained from the same contaminated episodes and has no source attribution in
                // its persisted weights, so retaining it would keep player rotations influencing future labels.
                _store.ML = new();
                Service.Log($"[Foretell] Removed {migratedInvalidMechanics} invalid player/pet/non-mechanic signal episodes and reset contaminated derived ML state.");
            }
        }
        if (loadedSchema < 21 && migratedUnsafeMetadata > 0)
        {
            // The online classifier has no per-session provenance. Rows whose metadata was misread as a spatial
            // circle or whose ambient outcomes became CLEANSE/MOVE labels can therefore contaminate future duties.
            _store.ML = new();
            Service.Log($"[Foretell] Repaired {migratedUnsafeMetadata} unsafe pre-21 Action metadata models and reset contaminated derived ML state.");
        }
    }

    private static void MigrateUnreliableV08DerivedMemory(EncounterMemory encounter)
    {
        // v0.8 mixed local WorldState time with UTC native-hook time. Keep expensive empirical mechanic samples,
        // but discard timing/forecast products whose delays and hit rates can therefore be off by a timezone.
        encounter.Timeline.Clear();
        encounter.TriggerContexts.Clear();
        encounter.Phases.Clear();
        encounter.PhaseBoundaries.Clear();
        encounter.Composites.Clear();
        encounter.CausalEdges.Clear();
        encounter.Pulls = 0;
        foreach (var mechanic in encounter.Mechanics.Values)
        {
            mechanic.Forecasts = 0;
            mechanic.ForecastHits = 0;
            mechanic.ForecastMisses = 0;
            mechanic.BrierScoreSum = 0;
        }
        if (encounter.Sources.TryGetValue(0, out var environment))
        {
            environment.Kind = SourceKind.Environment;
            environment.NameID = 0;
            environment.Name = "";
        }
    }

    private static void ResetPre20ForecastOutcomes(EncounterMemory encounter)
    {
        // Before schema 20, an avoided spatial telegraph (no affected/safe split) was recorded as a false miss,
        // and busy alliance frames could shed the expected signal itself. Those counters cannot be repaired from
        // aggregate memory, so retain the learned mechanics/timing while restarting only their validation history.
        foreach (var mechanic in encounter.Mechanics.Values)
        {
            mechanic.Forecasts = 0;
            mechanic.ForecastHits = 0;
            mechanic.ForecastMisses = 0;
            mechanic.BrierScoreSum = 0;
        }
        foreach (var edge in encounter.Timeline.Values)
        {
            edge.Forecasts = 0;
            edge.Hits = 0;
            edge.Misses = 0;
        }
        foreach (var trigger in encounter.TriggerContexts.Values)
        {
            trigger.TimeForecasts = 0;
            trigger.TimeHits = 0;
            trigger.TimeMisses = 0;
            trigger.HealthForecasts = 0;
            trigger.HealthHits = 0;
            trigger.HealthMisses = 0;
        }
        foreach (var composite in encounter.Composites.Values)
        {
            composite.Forecasts = 0;
            composite.Hits = 0;
            composite.Misses = 0;
        }
    }

    private static int MigratePre21ActionMetadata(EncounterMemory encounter)
    {
        var repaired = 0;
        foreach (var mechanic in encounter.Mechanics.Values.Where(item => item.TriggerKind == ObservationKind.CastStart))
        {
            mechanic.PriorEvidence ??= "";
            if (mechanic.PriorVFXID == 0)
                mechanic.PriorVFXID = PriorEvidenceNumber(mechanic.PriorEvidence, "VFX=");

            if (ForetellInferenceCore.IsGazeActionVFX(mechanic.PriorVFXID))
            {
                mechanic.PriorKind = MechanicKind.Gaze;
                mechanic.PriorGeometry = GeometryKind.Unknown;
                mechanic.PriorP1 = mechanic.PriorP2 = 0;
                mechanic.PriorConfidence = Math.Max(mechanic.PriorConfidence, .94f);
                ReassertReliableActionPrior(mechanic);
                ResetMechanicValidation(mechanic);
                ++repaired;
                continue;
            }

            if (ForetellInferenceCore.IsAmbiguousLargeCircleAction(mechanic.PriorCastType, mechanic.PriorEffectRange,
                mechanic.PriorTargetArea, mechanic.PriorOmenID))
            {
                mechanic.PriorKind = MechanicKind.Unknown;
                mechanic.PriorGeometry = GeometryKind.Unknown;
                mechanic.PriorP1 = mechanic.PriorEffectRange;
                mechanic.PriorP2 = 0;
                mechanic.PriorConfidence = .72f;
                ResetUnsafeMechanicClassification(mechanic);
                ++repaired;
                continue;
            }

            if (ForetellInferenceCore.IsReliableSpatialActionPrior(MechanicKind.GroundAOE, mechanic.PriorGeometry,
                mechanic.PriorConfidence, mechanic.PriorP1, mechanic.PriorP2))
            {
                mechanic.PriorKind = MechanicKind.GroundAOE;
                ReassertReliableActionPrior(mechanic);
                ResetMechanicValidation(mechanic);
                ++repaired;
                continue;
            }

            if (mechanic.PriorCastType == 1 && mechanic.PriorGeometry == GeometryKind.Unknown && mechanic.PriorKind == MechanicKind.Unknown
                && (mechanic.Kind != MechanicKind.Unknown || mechanic.Geometry != GeometryKind.Unknown))
            {
                ResetUnsafeMechanicClassification(mechanic);
                ++repaired;
            }
        }
        return repaired;
    }

    private static uint PriorEvidenceNumber(string evidence, string marker)
    {
        var start = evidence.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return 0;
        start += marker.Length;
        var end = start;
        while (end < evidence.Length && char.IsAsciiDigit(evidence[end])) ++end;
        return uint.TryParse(evidence.AsSpan(start, end - start), out var value) ? value : 0;
    }

    private static void ResetUnsafeMechanicClassification(ContextualMechanic mechanic)
    {
        mechanic.Kind = MechanicKind.Unknown;
        mechanic.Geometry = GeometryKind.Unknown;
        mechanic.P1 = mechanic.P2 = 0;
        mechanic.Score = 0;
        mechanic.Confirmations = 0;
        mechanic.AmbiguousSamples = 0;
        ResetMechanicValidation(mechanic);
    }

    private static void ResetMechanicValidation(ContextualMechanic mechanic)
    {
        mechanic.Forecasts = 0;
        mechanic.ForecastHits = 0;
        mechanic.ForecastMisses = 0;
        mechanic.BrierScoreSum = 0;
    }

    private static int MigrateInvalidMechanicSources(EncounterMemory encounter, HashSet<uint> invalidActions)
    {
        var invalid = encounter.Mechanics
            .Where(item => !ForetellInferenceCore.CanStartMechanicEpisode(item.Value.TriggerKind, item.Value.SourceKind,
                item.Value.SourceOID == 0 ? 0UL : 1UL, item.Value.SourceOID))
            .ToArray();
        if (invalid.Length == 0)
            return 0;

        var invalidSignals = invalid.Select(item => item.Key).ToHashSet();
        foreach (var item in invalid)
        {
            if (item.Value.TriggerKind == ObservationKind.CastStart && item.Value.TriggerID != 0)
                invalidActions.Add(item.Value.TriggerID);
            encounter.Mechanics.Remove(item.Key);
        }
        foreach (var key in encounter.Timeline.Where(item => invalidSignals.Contains(item.Value.From) || invalidSignals.Contains(item.Value.To)).Select(item => item.Key).ToArray())
            encounter.Timeline.Remove(key);
        foreach (var key in encounter.TriggerContexts.Where(item => invalidSignals.Contains(item.Value.Signal)).Select(item => item.Key).ToArray())
            encounter.TriggerContexts.Remove(key);
        foreach (var phase in encounter.Phases.Values)
        {
            phase.Signals ??= [];
            foreach (var signal in phase.Signals.Keys.Where(invalidSignals.Contains).ToArray())
                phase.Signals.Remove(signal);
        }
        foreach (var key in encounter.Composites.Where(item => item.Value.Signals?.Any(invalidSignals.Contains) == true).Select(item => item.Key).ToArray())
            encounter.Composites.Remove(key);
        foreach (var key in encounter.CausalEdges.Where(item => invalidSignals.Contains(item.Value.Cause)).Select(item => item.Key).ToArray())
            encounter.CausalEdges.Remove(key);
        foreach (var key in encounter.Sources.Where(item => item.Value.Kind is SourceKind.Player or SourceKind.Pet).Select(item => item.Key).ToArray())
            encounter.Sources.Remove(key);

        // These streams were previously admitted as generic evidence merely because they happened during the
        // episode window. Keep the underlying telemetry/raw files, but remove the spurious evidence counters.
        var ambientEvidence = new[]
        {
            ObservationKind.WorldOperation, ObservationKind.NativeVFXDestroy, ObservationKind.FlyText,
            ObservationKind.DalamudLogMessage, ObservationKind.NormalToast, ObservationKind.QuestToast,
            ObservationKind.ErrorToast, ObservationKind.ActorSnapshot, ObservationKind.EnvironmentSnapshot,
            ObservationKind.CameraSnapshot, ObservationKind.GenericFeature
        };
        foreach (var mechanic in encounter.Mechanics.Values)
        {
            mechanic.Evidence ??= [];
            foreach (var kind in ambientEvidence)
                mechanic.Evidence.Remove(kind);
        }
        return invalid.Length;
    }

    private static void RemoveNullValues<TKey, TValue>(Dictionary<TKey, TValue> dictionary) where TKey : notnull where TValue : class
    {
        foreach (var key in dictionary.Where(item => item.Value == null).Select(item => item.Key).ToArray()) dictionary.Remove(key);
    }

    private static void TrimEncounterCollections(EncounterMemory encounter)
    {
        if (encounter.Sources.Count > 8192)
            foreach (var key in encounter.Sources.OrderByDescending(item => item.Value.LastSeen).Skip(8192).Select(item => item.Key).ToArray()) encounter.Sources.Remove(key);
        if (encounter.Mechanics.Count > 2048)
            foreach (var key in encounter.Mechanics.OrderByDescending(item => item.Value.GuidanceConfidence).ThenByDescending(item => item.Value.LastSeen).Skip(2048).Select(item => item.Key).ToArray()) encounter.Mechanics.Remove(key);
        if (encounter.Timeline.Count > 8192)
            foreach (var key in encounter.Timeline.OrderByDescending(item => item.Value.Count).Skip(8192).Select(item => item.Key).ToArray()) encounter.Timeline.Remove(key);
        if (encounter.TriggerContexts.Count > 4096)
            foreach (var key in encounter.TriggerContexts.OrderByDescending(item => Math.Max(item.Value.Samples, item.Value.HealthSamples)).ThenByDescending(item => item.Value.LastSeen).Skip(4096).Select(item => item.Key).ToArray()) encounter.TriggerContexts.Remove(key);
        if (encounter.Phases.Count > 512)
            foreach (var key in encounter.Phases.OrderBy(item => item.Key).Skip(512).Select(item => item.Key).ToArray()) encounter.Phases.Remove(key);
        if (encounter.PhaseBoundaries.Count > 512)
            foreach (var key in encounter.PhaseBoundaries.OrderByDescending(item => item.Value.Accepted).ThenByDescending(item => item.Value.PullsSeen).Skip(512).Select(item => item.Key).ToArray()) encounter.PhaseBoundaries.Remove(key);
        if (encounter.Composites.Count > 2048)
            foreach (var key in encounter.Composites.OrderByDescending(item => item.Value.Count).Skip(2048).Select(item => item.Key).ToArray()) encounter.Composites.Remove(key);
        if (encounter.CausalEdges.Count > 8192)
            foreach (var key in encounter.CausalEdges.OrderByDescending(item => item.Value.Confidence).ThenByDescending(item => item.Value.Count).Skip(8192).Select(item => item.Key).ToArray()) encounter.CausalEdges.Remove(key);
        if (encounter.RawOpcodes.Count > 4096)
            foreach (var key in encounter.RawOpcodes.OrderByDescending(item => item.Value.Packets).Skip(4096).Select(item => item.Key).ToArray()) encounter.RawOpcodes.Remove(key);
        if (encounter.Topologies.Count > 8)
            foreach (var key in encounter.Topologies.OrderByDescending(item => item.Value.LastSeen).Skip(8).Select(item => item.Key).ToArray()) encounter.Topologies.Remove(key);
        if (encounter.ArenaBoundaries.Count > 16)
            foreach (var key in encounter.ArenaBoundaries.OrderByDescending(item => item.Value.LastSeen).Skip(16).Select(item => item.Key).ToArray()) encounter.ArenaBoundaries.Remove(key);
    }

    private static void NormalizeLearnedMechanic(LearnedMechanic mechanic)
    {
        mechanic.P1 = Finite(mechanic.P1, 0, 0, 200);
        mechanic.P2 = Finite(mechanic.P2, 0, 0, 200);
        mechanic.Score = Finite(mechanic.Score, 0, 0, 1);
        mechanic.MeanCastSeconds = Finite(mechanic.MeanCastSeconds, 0, 0, 120);
        mechanic.Observations = Math.Max(0, mechanic.Observations);
        mechanic.Confirmations = Math.Clamp(mechanic.Confirmations, 0, mechanic.Observations);
    }

    private static void NormalizeContextualMechanic(ContextualMechanic mechanic)
    {
        mechanic.Key ??= "";
        mechanic.TriggerDetail ??= "";
        mechanic.PriorOmen ??= "";
        mechanic.PriorEvidence ??= "";
        mechanic.Evidence ??= [];
        mechanic.Samples ??= [];
        mechanic.P1 = Finite(mechanic.P1, 0, 0, 200);
        mechanic.P2 = Finite(mechanic.P2, 0, 0, 200);
        mechanic.Score = Finite(mechanic.Score, 0, 0, 1);
        mechanic.PriorP1 = Finite(mechanic.PriorP1, 0, 0, 200);
        mechanic.PriorP2 = Finite(mechanic.PriorP2, 0, 0, 200);
        mechanic.PriorConfidence = Finite(mechanic.PriorConfidence, 0, 0, .98f);
        if (!Enum.IsDefined(mechanic.PriorKind)) mechanic.PriorKind = MechanicKind.Unknown;
        if (!Enum.IsDefined(mechanic.OriginKind)) mechanic.OriginKind = PredictionOriginKind.Source;
        if (mechanic.AnchorSamples == 0 && mechanic.Geometry is GeometryKind.Circle or GeometryKind.Donut)
            mechanic.OriginKind = PredictionOriginKind.Target;
        mechanic.MeanLeadSeconds = Finite(mechanic.MeanLeadSeconds, 0, 0, 120);
        mechanic.MeanAnchorForward = Finite(mechanic.MeanAnchorForward, 0, -200, 200);
        mechanic.MeanAnchorSide = Finite(mechanic.MeanAnchorSide, 0, -200, 200);
        mechanic.AnchorForwardM2 = Finite(mechanic.AnchorForwardM2, 0, 0, double.MaxValue);
        mechanic.AnchorSideM2 = Finite(mechanic.AnchorSideM2, 0, 0, double.MaxValue);
        mechanic.AnchorSamples = Math.Max(0, mechanic.AnchorSamples);
        mechanic.Forecasts = Math.Max(0, mechanic.Forecasts);
        mechanic.ForecastHits = Math.Clamp(mechanic.ForecastHits, 0, mechanic.Forecasts);
        mechanic.ForecastMisses = Math.Clamp(mechanic.ForecastMisses, 0, mechanic.Forecasts - mechanic.ForecastHits);
        mechanic.BrierScoreSum = Finite(mechanic.BrierScoreSum, 0, 0, double.MaxValue);
        mechanic.Observations = Math.Max(0, mechanic.Observations);
        mechanic.Confirmations = Math.Clamp(mechanic.Confirmations, 0, mechanic.Observations);
        mechanic.AffectedSamples = Math.Max(0, mechanic.AffectedSamples);
        mechanic.StatusSamples = Math.Max(0, mechanic.StatusSamples);
        mechanic.MovementSamples = Math.Max(0, mechanic.MovementSamples);
        mechanic.DeathSamples = Math.Max(0, mechanic.DeathSamples);
        mechanic.AmbiguousSamples = Math.Max(0, mechanic.AmbiguousSamples);
        mechanic.Samples.RemoveAll(sample => sample == null || !float.IsFinite(sample.Side) || !float.IsFinite(sample.Forward) || !float.IsFinite(sample.TargetDX) || !float.IsFinite(sample.TargetDZ));
        if (mechanic.Samples.Count > 256) mechanic.Samples.RemoveRange(0, mechanic.Samples.Count - 256);
    }

    private static void NormalizeTimelineEdge(TimelineEdge edge)
    {
        edge.Count = Math.Max(0, edge.Count);
        edge.MeanDelay = Finite(edge.MeanDelay, 0, 0, 600);
        edge.M2 = Finite(edge.M2, 0, 0, double.MaxValue);
    }

    private static void NormalizeSignalTimelineEdge(SignalTimelineEdge edge)
    {
        edge.From ??= "";
        edge.To ??= "";
        edge.Count = Math.Max(0, edge.Count);
        edge.MeanDelay = Finite(edge.MeanDelay, 0, 0, 600);
        edge.M2 = Finite(edge.M2, 0, 0, double.MaxValue);
        edge.Forecasts = Math.Max(0, edge.Forecasts);
        edge.Hits = Math.Clamp(edge.Hits, 0, edge.Forecasts);
        edge.Misses = Math.Clamp(edge.Misses, 0, edge.Forecasts - edge.Hits);
    }

    private static void NormalizeSignalTriggerMemory(SignalTriggerMemory trigger)
    {
        trigger.Key ??= "";
        trigger.Signal ??= "";
        trigger.Phase = Math.Clamp(trigger.Phase, 0, 511);
        trigger.Occurrence = Math.Clamp(trigger.Occurrence, 1, 32);
        if (trigger.ContextOID == 0) trigger.ContextOID = trigger.BossOID;
        trigger.Samples = Math.Max(0, trigger.Samples);
        trigger.LastPull = Math.Max(-1, trigger.LastPull);
        trigger.MeanPhaseSeconds = Finite(trigger.MeanPhaseSeconds, 0, 0, 1800);
        trigger.PhaseSecondsM2 = Finite(trigger.PhaseSecondsM2, 0, 0, double.MaxValue);
        trigger.HealthSamples = Math.Clamp(trigger.HealthSamples, 0, trigger.Samples);
        trigger.MeanBossHPRatio = Finite(trigger.MeanBossHPRatio, 0, 0, 1);
        trigger.BossHPRatioM2 = Finite(trigger.BossHPRatioM2, 0, 0, double.MaxValue);
        trigger.TimeForecasts = Math.Max(0, trigger.TimeForecasts);
        trigger.TimeHits = Math.Clamp(trigger.TimeHits, 0, trigger.TimeForecasts);
        trigger.TimeMisses = Math.Clamp(trigger.TimeMisses, 0, trigger.TimeForecasts - trigger.TimeHits);
        trigger.HealthForecasts = Math.Max(0, trigger.HealthForecasts);
        trigger.HealthHits = Math.Clamp(trigger.HealthHits, 0, trigger.HealthForecasts);
        trigger.HealthMisses = Math.Clamp(trigger.HealthMisses, 0, trigger.HealthForecasts - trigger.HealthHits);
    }

    private static bool NormalizeTopology(ArenaTopologyMemory topology)
    {
        topology.Fingerprint ??= "";
        topology.Cells ??= [];
        topology.HeightCentimeters ??= [];
        topology.KnownEdges ??= [];
        topology.BlockedEdges ??= [];
        topology.Contours ??= [];
        if (topology.Width is <= 0 or > 257 || topology.Height is <= 0 or > 257
            || topology.Width * topology.Height != topology.Cells.Length
            || topology.HeightCentimeters.Length != topology.Cells.Length
            || topology.KnownEdges.Length != topology.Cells.Length
            || topology.BlockedEdges.Length != topology.Cells.Length)
            return false;
        topology.OriginX = Finite(topology.OriginX, 0, -100000, 100000);
        topology.OriginZ = Finite(topology.OriginZ, 0, -100000, 100000);
        topology.ReferenceY = Finite(topology.ReferenceY, 0, -10000, 10000);
        topology.Resolution = Finite(topology.Resolution, 1, .1f, 10);
        topology.Contours.RemoveAll(contour => contour == null);
        if (topology.Contours.Count > 128) topology.Contours.RemoveRange(128, topology.Contours.Count - 128);
        foreach (var contour in topology.Contours)
        {
            contour.Points ??= [];
            contour.Points.RemoveAll(point => point == null || !float.IsFinite(point.X) || !float.IsFinite(point.Z));
            if (contour.Points.Count > 65536) contour.Points.RemoveRange(65536, contour.Points.Count - 65536);
        }
        topology.PassableCells = Math.Clamp(topology.PassableCells, 0, topology.Cells.Length);
        topology.BlockedCells = Math.Clamp(topology.BlockedCells, 0, topology.Cells.Length);
        topology.UnknownCells = Math.Clamp(topology.UnknownCells, 0, topology.Cells.Length);
        topology.Components = Math.Max(0, topology.Components);
        topology.Observations = Math.Max(0, topology.Observations);
        return true;
    }

    private static bool NormalizeArenaBoundary(ArenaBoundaryMemory boundary)
    {
        boundary.Fingerprint ??= "";
        boundary.Points ??= [];
        boundary.Points.RemoveAll(point => point == null || !float.IsFinite(point.X) || !float.IsFinite(point.Z));
        if (boundary.Points.Count is < 16 or > 512 || string.IsNullOrWhiteSpace(boundary.Fingerprint))
            return false;
        boundary.OriginX = Finite(boundary.OriginX, 0, -100000, 100000);
        boundary.OriginZ = Finite(boundary.OriginZ, 0, -100000, 100000);
        boundary.ReferenceY = Finite(boundary.ReferenceY, 0, -10000, 10000);
        boundary.Rays = Math.Clamp(boundary.Rays, boundary.Points.Count, 512);
        boundary.Hits = Math.Clamp(boundary.Hits, 0, boundary.Rays);
        boundary.Area = Finite(boundary.Area, 0, 0, 100000);
        boundary.Compactness = Finite(boundary.Compactness, 0, 0, 1);
        boundary.AspectRatio = Finite(boundary.AspectRatio, 100, 1, 100);
        boundary.Observations = Math.Max(0, boundary.Observations);
        return true;
    }

    private static float Finite(float value, float fallback, float minimum, float maximum)
        => float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static double Finite(double value, double fallback, double minimum, double maximum)
        => double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private void SaveStore()
    {
        try
        {
            var tmp = _storePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_store, _json));
            File.Move(tmp, _storePath, true);
            _lastSave = DateTime.UtcNow;
        }
        catch (Exception e)
        {
            // Back off after an I/O failure instead of retrying serialization on every framework frame.
            _lastSave = DateTime.UtcNow;
            Service.Log($"[Foretell] Failed to save memory: {e.Message}");
        }
    }

    private void Record(ForetellObservation observation, bool replaying)
    {
        if (replaying || !_cfg.RecordReplay || _replay == null) return;
        _replay.Enqueue(_replayPath, observation);
    }

    private ForetellObservation Observation(ObservationKind kind, Actor? actor = null, uint primary = 0, uint secondary = 0, ulong target = 0,
        float value1 = 0, float value2 = 0, bool flag = false, string detail = "")
    {
        var targetActor = target != 0 ? _ws.Actors.Find(target) : null;
        return new()
        {
            Sequence = ++_sequence,
            At = ObservationNow(),
            TerritoryID = _territory,
            Kind = kind,
            SourceKind = actor == null ? SourceKind.Environment : ClassifySource(actor),
            ActorID = actor?.InstanceID ?? 0,
            ActorOID = actor?.OID ?? 0,
            TargetID = target,
            PrimaryID = primary,
            SecondaryID = secondary,
            X = actor?.Position.X ?? 0,
            Z = actor?.Position.Z ?? 0,
            TargetX = targetActor?.Position.X ?? 0,
            TargetZ = targetActor?.Position.Z ?? 0,
            Rotation = actor?.Rotation.Rad ?? 0,
            Value1 = value1,
            Value2 = value2,
            Flag = flag,
            Detail = detail
        };
    }

    private DateTime ObservationNow() => NormalizeObservationTime(_ws.CurrentTime);

    private static DateTime NormalizeObservationTime(DateTime value)
    {
        // WorldState uses the game's local wall-clock time while hook capture queues use UTC. DateTime subtraction
        // ignores Kind, so normalize UTC captures before any expiry, episode or timeline arithmetic.
        if (value.Ticks < TimeSpan.TicksPerDay || value.Ticks > DateTime.MaxValue.Ticks - TimeSpan.TicksPerDay)
            return DateTime.Now;
        return value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
    }

    private static SourceKind ClassifySource(Actor actor)
    {
        return actor.Type switch
        {
            ActorType.Player => SourceKind.Player,
            ActorType.Pet or ActorType.Chocobo or ActorType.Buddy or ActorType.Companion => SourceKind.Pet,
            ActorType.Enemy when actor.IsAlly => SourceKind.Pet,
            ActorType.Enemy or ActorType.Part or ActorType.Helper => SourceKind.Enemy,
            ActorType.EventNpc or ActorType.EventObj or ActorType.Area => SourceKind.EventObject,
            _ => SourceKind.Unknown
        };
    }

    private static Vector2 V(WPos p) => new(p.X, p.Z);

    private static object? Member(object? o, string name)
    {
        if (o == null) return null;
        var t = o.GetType();
        return t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o)
            ?? t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);
    }

    private static uint? ToUInt(object? o) { try { return o == null ? null : Convert.ToUInt32(o); } catch { return null; } }
    private static ulong? ToULong(object? o) { try { return o == null ? null : Convert.ToUInt64(o); } catch { return null; } }
}
