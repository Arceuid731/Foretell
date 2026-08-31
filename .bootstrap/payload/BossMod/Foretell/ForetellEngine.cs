using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine : IDisposable
{
    private readonly WorldState _ws;
    private readonly ForetellConfig _cfg;
    private readonly string _storePath;
    private readonly string _replayPath;
    private readonly EventSubscriptions _subscriptions;
    private readonly Dictionary<(ulong, uint), CastSnapshot> _casts = [];
    private readonly Dictionary<uint, ActivePrediction> _predictions = [];
    private readonly Queue<(DateTime Time, uint ID)> _recentIcons = [];
    private readonly Queue<(DateTime Time, uint ID)> _recentVfx = [];
    private readonly Queue<DateTime> _recentTethers = [];
    private readonly Queue<DateTime> _recentStatuses = [];
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private ForetellStore _store;
    private OnlineClassifier _classifier;
    private StreamWriter? _replay;
    private uint _previousAction;
    private DateTime _previousActionTime;
    private DateTime _lastSave;
    private string _lastEvidence = "Waiting for observations";

    public ForetellEngine(WorldState ws, string configDirectory)
    {
        _ws = ws;
        _cfg = Service.Config.Get<ForetellConfig>();
        _storePath = Path.Combine(configDirectory, "foretell-memory.json");
        var replayDir = Path.Combine(configDirectory, "foretell-replays");
        Directory.CreateDirectory(replayDir);
        _replayPath = Path.Combine(replayDir, $"foretell-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        _store = LoadStore();
        _classifier = new(_store.ML);
        if (_cfg.RecordReplay) _replay = new(_replayPath, append: true) { AutoFlush = false };
        _subscriptions = new(
            _ws.Actors.CastStarted.Subscribe(OnCastStarted),
            _ws.Actors.CastFinished.Subscribe(OnCastFinished),
            _ws.Actors.CastEvent.Subscribe(OnCastEvent),
            _ws.Actors.IconAppeared.Subscribe(OnIcon),
            _ws.Actors.VFXAppeared.Subscribe(OnVFX),
            _ws.Actors.Tethered.Subscribe(OnTether),
            _ws.Actors.StatusGain.Subscribe(OnStatusGain));
    }

    public void Dispose()
    {
        SaveStore();
        _replay?.Dispose();
        _subscriptions.Dispose();
    }

    public void Update()
    {
        TrimSignals();
        var now = _ws.CurrentTime;
        foreach (var key in _predictions.Where(p => p.Value.Activation.AddSeconds(1.5) < now).Select(p => p.Key).ToArray())
            _predictions.Remove(key);
        if ((DateTime.Now - _lastSave).TotalSeconds > 30) SaveStore();
    }

    private void OnCastStarted(Actor actor)
    {
        var spell = actor.CastInfo;
        if (spell == null || !spell.IsSpell()) return;
        var id = spell.Action.ID;
        var now = _ws.CurrentTime;
        var castSeconds = Math.Max(0, spell.NPCRemainingTime);
        var snapshot = new CastSnapshot(actor.InstanceID, id, V(actor.Position), V(spell.LocXZ), spell.Rotation.Rad, now, _ws.FutureTime(castSeconds), castSeconds);
        _casts[(actor.InstanceID, id)] = snapshot;
        LearnTimeline(id, now);
        if (_store.Mechanics.TryGetValue(id, out var learned) && learned.Geometry != GeometryKind.Unknown)
        {
            var origin = learned.Geometry is GeometryKind.Circle or GeometryKind.Donut ? snapshot.Target : snapshot.Origin;
            _predictions[id] = new(actor.InstanceID, id, learned.Geometry, learned.Kind, origin, snapshot.Target, snapshot.Rotation,
                learned.P1, learned.P2, snapshot.Activation, learned.Confidence, $"{learned.Observations} observations; fit {learned.Score:P0}");
        }
        Record("cast-start", new { actor = actor.InstanceID, action = id, x = snapshot.Origin.X, z = snapshot.Origin.Y, tx = snapshot.Target.X, tz = snapshot.Target.Y, castSeconds });
    }

    private void OnCastFinished(Actor actor)
    {
        var spell = actor.CastInfo;
        if (spell != null) Record("cast-finish", new { actor = actor.InstanceID, action = spell.Action.ID });
    }

    private void OnCastEvent(Actor actor, ActorCastEvent ev)
    {
        var action = ReadActionID(ev);
        if (action == 0) return;
        var key = (actor.InstanceID, action);
        _casts.TryGetValue(key, out var snapshot);
        if (snapshot.ActionID == 0)
            snapshot = new(actor.InstanceID, action, V(actor.Position), V(actor.Position), actor.Rotation.Rad, _ws.CurrentTime, _ws.CurrentTime, 0);

        var hitIds = ExtractTargetIDs(ev);
        var samples = PartySamples(hitIds);
        FitResult? fit = samples.Count >= 2 && hitIds.Count != 0 ? FitGeometry(snapshot, samples) : null;
        var features = BuildFeatures(snapshot, samples, hitIds, fit);
        var deterministic = LabelFromObservation(samples, hitIds, fit);
        if (_cfg.EnableLearning)
        {
            if (fit is FitResult f && f.Score >= .55f) UpdateMechanic(action, snapshot, f, deterministic);
            else if (!_store.Mechanics.ContainsKey(action))
                _store.Mechanics[action] = new() { ActionID = action, Geometry = GeometryKind.Unknown, Kind = deterministic, Score = .2f, Observations = 1, LastSeen = DateTime.UtcNow };
            if (_cfg.EnableML && deterministic != MechanicKind.Unknown) _classifier.Train(features, deterministic);
        }
        var ml = _cfg.EnableML ? _classifier.Predict(features) : (MechanicKind.Unknown, 0f);
        _lastEvidence = fit is FitResult best
            ? $"AID {action}: {best.Geometry} fit {best.Score:P1}; hits {hitIds.Count}/{samples.Count}; ML {ml.Item1} {ml.Item2:P0}"
            : $"AID {action}: outcome observed; hits {hitIds.Count}/{samples.Count}; ML {ml.Item1} {ml.Item2:P0}";
        Record("cast-event", new { actor = actor.InstanceID, action, targets = hitIds.Count, partySamples = samples.Count, geometry = fit?.Geometry.ToString(), score = fit?.Score, deterministic = deterministic.ToString(), ml = ml.Item1.ToString(), mlConfidence = ml.Item2 });
        _casts.Remove(key);
    }

    private void OnIcon(Actor actor, uint icon, ulong target) { _recentIcons.Enqueue((_ws.CurrentTime, icon)); Record("icon", new { actor = actor.InstanceID, icon, target }); }
    private void OnVFX(Actor actor, uint vfx, ulong target) { _recentVfx.Enqueue((_ws.CurrentTime, vfx)); Record("vfx", new { actor = actor.InstanceID, vfx, target }); }
    private void OnTether(Actor actor) { _recentTethers.Enqueue(_ws.CurrentTime); Record("tether", new { actor = actor.InstanceID, id = actor.Tether.ID, target = actor.Tether.Target }); }
    private void OnStatusGain(Actor actor, int index) { _recentStatuses.Enqueue(_ws.CurrentTime); ref var s = ref actor.Statuses[index]; Record("status", new { actor = actor.InstanceID, id = s.ID, source = s.SourceID }); }

    private List<Sample> PartySamples(HashSet<ulong> hitIds)
    {
        List<Sample> result = [];
        foreach (var (_, player) in _ws.Party.WithSlot()) result.Add(new(V(player.Position), hitIds.Contains(player.InstanceID)));
        return result;
    }

    private static Vector2 V(WPos p) => new(p.X, p.Z);

    private MechanicKind LabelFromObservation(List<Sample> samples, HashSet<ulong> hits, FitResult? fit)
    {
        if (_recentTethers.Count != 0) return MechanicKind.Tether;
        if (fit is not null) return MechanicKind.GroundAOE;
        if (samples.Count >= 4 && hits.Count >= Math.Ceiling(samples.Count * .75)) return MechanicKind.Raidwide;
        if (_recentIcons.Count != 0 && hits.Count > 1)
        {
            var hp = samples.Where(s => s.Hit).Select(s => s.Position).ToArray();
            if (hp.Length > 1)
            {
                double avg = 0; var n = 0;
                for (var i = 0; i < hp.Length; ++i) for (var j = i + 1; j < hp.Length; ++j) { avg += Vector2.Distance(hp[i], hp[j]); ++n; }
                avg /= Math.Max(1, n);
                return avg > 7 ? MechanicKind.Spread : MechanicKind.Stack;
            }
        }
        return MechanicKind.Unknown;
    }

    private double[] BuildFeatures(CastSnapshot cast, List<Sample> samples, HashSet<ulong> hits, FitResult? fit)
    {
        var hitFraction = samples.Count == 0 ? 0 : hits.Count / (double)samples.Count;
        return [Math.Clamp(cast.CastSeconds / 10d, 0, 1), Math.Clamp(Vector2.Distance(cast.Origin, cast.Target) / 50d, 0, 1), hitFraction,
            Math.Clamp(hits.Count / 8d, 0, 1), _recentIcons.Count > 0 ? 1 : 0, _recentTethers.Count > 0 ? 1 : 0,
            _recentStatuses.Count > 0 ? 1 : 0, _recentVfx.Count > 0 ? 1 : 0, fit?.Score ?? 0, fit is null ? 0 : (int)fit.Value.Geometry / 4d];
    }

    private void UpdateMechanic(uint action, CastSnapshot cast, FitResult fit, MechanicKind kind)
    {
        if (!_store.Mechanics.TryGetValue(action, out var m))
        {
            _store.Mechanics[action] = new() { ActionID = action, Geometry = fit.Geometry, Kind = kind, P1 = fit.P1, P2 = fit.P2,
                Score = fit.Score, Observations = 1, Confirmations = 1, MeanCastSeconds = cast.CastSeconds, LastSeen = DateTime.UtcNow };
            return;
        }
        ++m.Observations;
        if (m.Geometry == fit.Geometry)
        {
            ++m.Confirmations;
            var alpha = Math.Clamp(1f / MathF.Sqrt(m.Confirmations), .08f, .35f);
            m.P1 = m.P1 * (1 - alpha) + fit.P1 * alpha; m.P2 = m.P2 * (1 - alpha) + fit.P2 * alpha; m.Score = m.Score * (1 - alpha) + fit.Score * alpha;
        }
        else if (fit.Score > m.Score + .08f)
        { m.Geometry = fit.Geometry; m.P1 = fit.P1; m.P2 = fit.P2; m.Score = fit.Score; m.Confirmations = 1; }
        if (kind != MechanicKind.Unknown) m.Kind = kind;
        m.MeanCastSeconds += (cast.CastSeconds - m.MeanCastSeconds) / m.Observations;
        m.LastSeen = DateTime.UtcNow;
    }

    private void LearnTimeline(uint action, DateTime now)
    {
        if (_previousAction != 0 && _previousAction != action)
        {
            var key = $"{_previousAction}>{action}"; var dt = Math.Max(0, (now - _previousActionTime).TotalSeconds);
            if (!_store.Timeline.TryGetValue(key, out var e)) _store.Timeline[key] = e = new() { From = _previousAction, To = action };
            ++e.Count; var delta = dt - e.MeanDelay; e.MeanDelay += delta / e.Count; e.M2 += delta * (dt - e.MeanDelay);
        }
        _previousAction = action; _previousActionTime = now;
    }

    private void TrimSignals()
    {
        var cutoff = _ws.CurrentTime.AddSeconds(-6);
        while (_recentIcons.TryPeek(out var x) && x.Time < cutoff) _recentIcons.Dequeue();
        while (_recentVfx.TryPeek(out var v) && v.Time < cutoff) _recentVfx.Dequeue();
        while (_recentTethers.TryPeek(out var t) && t < cutoff) _recentTethers.Dequeue();
        while (_recentStatuses.TryPeek(out var s) && s < cutoff) _recentStatuses.Dequeue();
    }

    private static uint ReadActionID(object ev)
    {
        var action = Member(ev, "Action");
        return ToUInt(Member(action, "ID")) ?? ToUInt(Member(ev, "ActionID")) ?? 0;
    }

    private static HashSet<ulong> ExtractTargetIDs(object ev)
    {
        HashSet<ulong> ids = [];
        if (Member(ev, "Targets") is IEnumerable targets)
            foreach (var t in targets)
            {
                if (t == null) continue;
                var id = ToULong(Member(t, "ID")) ?? ToULong(Member(t, "TargetID")) ?? ToULong(Member(t, "InstanceID"));
                if (id is > 0) ids.Add(id.Value);
            }
        var main = ToULong(Member(ev, "MainTargetID")) ?? ToULong(Member(ev, "TargetID"));
        if (main is > 0) ids.Add(main.Value);
        return ids;
    }

    private static object? Member(object? o, string name)
    {
        if (o == null) return null;
        var t = o.GetType();
        return t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o)
            ?? t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);
    }
    private static uint? ToUInt(object? o) { try { return o == null ? null : Convert.ToUInt32(o); } catch { return null; } }
    private static ulong? ToULong(object? o) { try { return o == null ? null : Convert.ToUInt64(o); } catch { return null; } }

    private ForetellStore LoadStore()
    {
        try { return File.Exists(_storePath) ? JsonSerializer.Deserialize<ForetellStore>(File.ReadAllText(_storePath), _json) ?? new() : new(); }
        catch (Exception e) { Service.Log($"[Foretell] Failed to load memory: {e.Message}"); return new(); }
    }

    private void SaveStore()
    {
        try
        {
            var tmp = _storePath + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(_store, _json)); File.Move(tmp, _storePath, true);
            _replay?.Flush(); _lastSave = DateTime.Now;
        }
        catch (Exception e) { Service.Log($"[Foretell] Failed to save memory: {e.Message}"); }
    }

    private void Record(string type, object payload)
    {
        if (!_cfg.RecordReplay || _replay == null) return;
        try { _replay.WriteLine(JsonSerializer.Serialize(new { at = _ws.CurrentTime, type, payload })); } catch { }
    }
}
