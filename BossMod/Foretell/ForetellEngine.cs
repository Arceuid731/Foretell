using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine : IDisposable
{
    // Emergency fail-closed tier: direct client-memory readers and hooks remain quarantined until every detour
    // only copies bounded primitives into a queue and all interpretation runs on the framework thread.
    private const bool NativeTelemetryEnabled = false;
    private readonly WorldState _ws;
    private readonly ForetellConfig _cfg;
    private readonly string _storePath;
    private readonly string _replayDir;
    private readonly EventSubscriptions _subscriptions;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly JsonSerializerOptions _replayJson = new() { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };

    private ForetellStore _store;
    private OnlineClassifier _classifier;
    private StreamWriter? _replay;
    private string _replayPath = "";
    private long _sequence;
    private DateTime _lastSave;
    private DateTime _lastPositionSample;
    private uint _territory;
    private LiveSessionStats _session;

    private Dictionary<long, MechanicEpisode> _episodes = [];
    private Dictionary<ulong, ParticipantTrack> _tracks = [];
    private Dictionary<long, ActivePrediction> _predictions = [];
    private Dictionary<uint, long> _effectSequenceEpisodes = [];
    private Queue<ForetellObservation> _recentSignals = new();

    private uint _previousAction;
    private DateTime _previousActionTime;
    private string _previousSignal = "";
    private DateTime _previousSignalTime;
    private string _lastEvidence = "Waiting for observations";
    private bool _inPull;
    private DateTime _lastCombatSignal;

    private bool _inspectorOpen;
    private ReplayReport _lastReplayReport = new();

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
        _storePath = Path.Combine(configDirectory, "foretell-memory.json");
        _replayDir = Path.Combine(configDirectory, "foretell-replays");
        Directory.CreateDirectory(_replayDir);

        _store = LoadStore();
        NormalizeStore();
        _classifier = new(_store.ML);
        _territory = CurrentTerritory();
        _session = NewSession(_territory);
        _subscriptions = new();
        StartEncounterSession(_territory);

        foreach (var actor in _ws.Actors)
            OnActorAdded(actor);
        SamplePartyPositions();
        SampleDataFabric(force: true);

        // Perform all fallible initial sampling before installing passive native hooks or event callbacks. If a
        // future sensor rejects startup, the constructor cannot leave Foretell-owned hooks behind.
        try
        {
            SyncReplayWriter();
            InstallForetellCommand();
            if (NativeTelemetryEnabled)
                InitializeNativeHooks();
            else
                ClassifyNativeTelemetryQuarantine();
            SubscribeToWorldState();
            InitializeDalamudSignals();
        }
        catch
        {
            _subscriptions.Dispose();
            DisposeNativeHooks();
            _replay?.Dispose();
            _replay = null;
            Service.CommandManager.RemoveHandler("/foretell");
            throw;
        }
    }

    private void SubscribeToWorldState()
    {
        _subscriptions.Add(_ws.Modified.Subscribe(OnWorldOperation));
        _subscriptions.Add(_ws.SystemLogMessage.Subscribe(OnSystemLog));
        _subscriptions.Add(_ws.Network.RawServerIPCReceived.Subscribe(OnRawServerIPC));
        _subscriptions.Add(_ws.Network.RawClientIPCSent.Subscribe(OnRawClientIPC));
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
        FinalizeDue(DateTime.MaxValue);
        CompleteSession();
        SaveStore();
        _replay?.Dispose();
        _subscriptions.Dispose();
        DisposeNativeHooks();
        Service.CommandManager.RemoveHandler("/foretell");
    }

    public void Update()
    {
        var now = ObservationNow();
        var territory = CurrentTerritory();
        if (territory != _territory)
            ChangeTerritory(territory);

        SyncReplayWriter();
        if ((now - _lastPositionSample).TotalMilliseconds >= 250)
        {
            SamplePartyPositions();
            SampleDataFabric();
            _lastPositionSample = now;
        }

        FinalizeDue(now);
        TrimRecentSignals(now.AddSeconds(-8));

        foreach (var key in _predictions.Where(p => p.Value.Activation.AddSeconds(1.5) < now).Select(p => p.Key).ToArray())
            _predictions.Remove(key);

        if (_inPull && (now - _lastCombatSignal).TotalSeconds > 30)
            _inPull = false;

        if ((DateTime.UtcNow - _lastSave).TotalSeconds > 30)
            SaveStore();
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
        FinalizeDue(DateTime.MaxValue);
        CompleteSession();
        _episodes.Clear();
        _tracks.Clear();
        ResetDataFabric();
        _predictions.Clear();
        _effectSequenceEpisodes.Clear();
        _recentSignals.Clear();
        _previousAction = 0;
        _previousSignal = "";
        _inPull = false;
        _territory = territory;
        _session = NewSession(territory);
        StartEncounterSession(territory);
        ReopenReplayWriter();
        _lastEvidence = $"Entered territory {territory}";
    }

    private LiveSessionStats NewSession(uint territory) => new() { TerritoryID = territory };

    private void StartEncounterSession(uint territory)
    {
        var encounter = Encounter(territory);
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

    private void SyncReplayWriter()
    {
        if (_cfg.RecordReplay && _replay == null)
            OpenReplayWriter();
        else if (!_cfg.RecordReplay && _replay != null)
        {
            _replay.Flush();
            _replay.Dispose();
            _replay = null;
        }
    }

    private void ReopenReplayWriter()
    {
        _replay?.Flush();
        _replay?.Dispose();
        _replay = null;
        if (_cfg.RecordReplay) OpenReplayWriter();
    }

    private void OpenReplayWriter()
    {
        _replayPath = Path.Combine(_replayDir, $"foretell-T{_territory}-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        _replay = new(_replayPath, append: true) { AutoFlush = false };
    }

    private ForetellStore LoadStore()
    {
        try
        {
            return File.Exists(_storePath)
                ? JsonSerializer.Deserialize<ForetellStore>(File.ReadAllText(_storePath), _json) ?? new()
                : new();
        }
        catch (Exception e)
        {
            Service.Log($"[Foretell] Failed to load memory: {e.Message}");
            return new();
        }
    }

    private void NormalizeStore()
    {
        _store.Schema = Math.Max(_store.Schema, 6);
        _store.Mechanics ??= [];
        _store.Timeline ??= [];
        _store.Encounters ??= [];
        _store.Sessions ??= [];
        _store.ML ??= new();
        _store.Coverage ??= new();
        _store.Coverage.Items ??= [];
        foreach (var encounter in _store.Encounters.Values)
        {
            encounter.ObservationCounts ??= [];
            encounter.Sources ??= [];
            encounter.Mechanics ??= [];
            encounter.Timeline ??= [];
            encounter.Phases ??= [];
            foreach (var mechanic in encounter.Mechanics.Values)
            {
                mechanic.Evidence ??= [];
                mechanic.Samples ??= [];
            }
        }
    }

    private void SaveStore()
    {
        try
        {
            var tmp = _storePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_store, _json));
            File.Move(tmp, _storePath, true);
            _replay?.Flush();
            _lastSave = DateTime.UtcNow;
        }
        catch (Exception e)
        {
            Service.Log($"[Foretell] Failed to save memory: {e.Message}");
        }
    }

    private void Record(ForetellObservation observation, bool replaying)
    {
        if (replaying || !_cfg.RecordReplay || _replay == null) return;
        try { _replay.WriteLine(JsonSerializer.Serialize(observation, _replayJson)); }
        catch (Exception e) { Service.LogVerbose($"[Foretell] Replay write failed: {e.Message}"); }
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
        => value.Ticks < TimeSpan.TicksPerDay || value.Ticks > DateTime.MaxValue.Ticks - TimeSpan.TicksPerDay ? DateTime.UtcNow : value;

    private static SourceKind ClassifySource(Actor actor)
    {
        if (actor.Type == ActorType.Player) return SourceKind.Player;
        if (actor.Type is ActorType.Pet or ActorType.Chocobo or ActorType.Buddy) return SourceKind.Pet;
        var type = actor.Type.ToString();
        if (type.Contains("Event", StringComparison.OrdinalIgnoreCase) || type.Contains("Object", StringComparison.OrdinalIgnoreCase)) return SourceKind.EventObject;
        return SourceKind.Enemy;
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
