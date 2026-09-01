from pathlib import Path
import re


def load(path):
    return Path(path).read_text(encoding='utf-8-sig')


def save(path, text):
    Path(path).write_text(text, encoding='utf-8')


def replace_once(path, old, new):
    s = load(path)
    if old not in s:
        raise RuntimeError(f'anchor not found in {path}: {old[:120]!r}')
    save(path, s.replace(old, new, 1))


def regex_once(path, pattern, repl, flags=0):
    s = load(path)
    s2, n = re.subn(pattern, repl, s, count=1, flags=flags)
    if n != 1:
        raise RuntimeError(f'regex anchor not found in {path}: {pattern[:120]!r}')
    save(path, s2)


DATA_FABRIC = r'''using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace BossMod.Foretell;

// Generic, encounter-agnostic data ingestion. This deliberately refuses hand-authored BossModule/state-machine/
// encounter-component knowledge: Foretell may consume the same raw game state, but never their authored answers.
public sealed partial class ForetellEngine
{
    private const int MaxFabricEntriesPerObject = 768;
    private DateTime _lastFabricSample;
    private readonly Dictionary<ulong, string> _actorFabricFingerprint = [];
    private readonly Dictionary<ulong, FabricActorTrack> _fabricActorTracks = [];
    private Dictionary<string, double> _runtimeNumeric = [];
    private Dictionary<string, string> _runtimeText = [];

    private sealed class FabricActorTrack
    {
        public DateTime At;
        public Vector2 Position;
        public float Rotation;
    }

    private void ResetDataFabric()
    {
        _actorFabricFingerprint.Clear();
        _fabricActorTracks.Clear();
        _runtimeNumeric.Clear();
        _runtimeText.Clear();
        _lastFabricSample = default;
    }

    private void SampleDataFabric(bool force = false)
    {
        if (!force && (_ws.CurrentTime - _lastFabricSample).TotalMilliseconds < 500)
            return;
        _lastFabricSample = _ws.CurrentTime;
        RefreshRuntimeContext();

        foreach (var actor in _ws.Actors)
        {
            var obs = Observation(ObservationKind.ActorSnapshot, actor, detail: actor.Type.ToString());
            EnrichObservation(obs, actor);
            var pos = new Vector2(actor.Position.X, actor.Position.Z);
            if (_fabricActorTracks.TryGetValue(actor.InstanceID, out var previous))
            {
                var dt = Math.Max(.001, (_ws.CurrentTime - previous.At).TotalSeconds);
                obs.Numeric["derived.actor.speed"] = Vector2.Distance(previous.Position, pos) / dt;
                obs.Numeric["derived.actor.angularSpeed"] = Math.Abs(NormalizeAngle(actor.Rotation.Rad - previous.Rotation)) / dt;
            }
            _fabricActorTracks[actor.InstanceID] = new() { At = _ws.CurrentTime, Position = pos, Rotation = actor.Rotation.Rad };

            var fingerprint = Fingerprint(obs, "actor.") + Fingerprint(obs, "derived.actor.");
            if (_actorFabricFingerprint.GetValueOrDefault(actor.InstanceID) == fingerprint)
                continue;
            _actorFabricFingerprint[actor.InstanceID] = fingerprint;
            ProcessObservation(obs);
        }

        foreach (var dead in _fabricActorTracks.Keys.Where(id => _ws.Actors.Find(id) == null).ToArray())
        {
            _fabricActorTracks.Remove(dead);
            _actorFabricFingerprint.Remove(dead);
        }
    }

    private static float NormalizeAngle(float angle)
    {
        while (angle > MathF.PI) angle -= MathF.Tau;
        while (angle < -MathF.PI) angle += MathF.Tau;
        return angle;
    }

    private void ProcessRichObservation(ForetellObservation observation, object? payload)
    {
        EnrichObservation(observation, payload);
        ProcessObservation(observation);
    }

    private void EnrichObservation(ForetellObservation observation, object? payload = null)
    {
        observation.Numeric ??= [];
        observation.Text ??= [];

        foreach (var (k, v) in _runtimeNumeric)
            observation.Numeric.TryAdd(k, v);
        foreach (var (k, v) in _runtimeText)
            observation.Text.TryAdd(k, v);

        var actor = observation.ActorID != 0 ? _ws.Actors.Find(observation.ActorID) : null;
        var target = observation.TargetID != 0 ? _ws.Actors.Find(observation.TargetID) : null;
        if (actor != null)
            FlattenRoot(actor, "actor", observation, 4);
        if (target != null && target.InstanceID != actor?.InstanceID)
            FlattenRoot(target, "target", observation, 3);
        if (payload != null && !ReferenceEquals(payload, actor))
            FlattenRoot(payload, "event", observation, 5);

        if (observation.PrimaryID != 0 && observation.Kind is ObservationKind.CastStart or ObservationKind.CastFinish or ObservationKind.ActionResolved or ObservationKind.AffectedTarget)
            TryFlattenRow<Lumina.Excel.Sheets.Action>(observation.PrimaryID, "static.action", observation);
        if (observation.PrimaryID != 0 && observation.Kind is ObservationKind.StatusGain or ObservationKind.StatusLose)
            TryFlattenRow<Lumina.Excel.Sheets.Status>(observation.PrimaryID, "static.status", observation);
        if (observation.ActorOID != 0 && observation.SourceKind == SourceKind.Enemy)
            TryFlattenRow<Lumina.Excel.Sheets.BNpcBase>(observation.ActorOID, "static.bnpcBase", observation);
        if (actor != null)
        {
            var nameID = ToUInt(Member(actor, "NameID")) ?? ToUInt(Member(actor, "NameId")) ?? 0;
            if (nameID != 0)
                TryFlattenRow<Lumina.Excel.Sheets.BNpcName>(nameID, "static.bnpcName", observation);
        }
    }

    private void RefreshRuntimeContext()
    {
        var obs = new ForetellObservation();
        FlattenRoot(_ws, "runtime.worldState", obs, 3);
        FlattenRoot(Service.ClientState, "runtime.clientState", obs, 2);
        FlattenRoot(Service.PlayerState, "runtime.playerState", obs, 2);
        FlattenRoot(Service.TargetManager, "runtime.targetManager", obs, 2);
        FlattenRoot(Service.Condition, "runtime.condition", obs, 2);
        FlattenRoot(Service.KeyState, "runtime.keyState", obs, 1);
        FlattenRoot(Service.GameGui, "runtime.gameGui", obs, 1);
        FlattenRoot(Service.GameConfig, "runtime.gameConfig", obs, 1);
        FlattenRoot(Service.ObjectTable, "runtime.objectTable", obs, 1);
        FlattenEnumIndexers(Service.Condition, "runtime.condition", obs);
        FlattenEnumIndexers(Service.KeyState, "runtime.keyState", obs);
        TryFlattenRow<Lumina.Excel.Sheets.TerritoryType>(_territory, "static.territory", obs);
        _runtimeNumeric = obs.Numeric;
        _runtimeText = obs.Text;
    }

    private void TryFlattenRow<T>(uint rowID, string prefix, ForetellObservation observation) where T : struct, Lumina.Excel.IExcelRow<T>
    {
        if (rowID == 0) return;
        try
        {
            var sheet = Service.LuminaSheet<T>();
            if (sheet == null)
            {
                RegisterCapability($"{prefix}.__sheet", typeof(T), "sheet", false, true, "sheet unavailable");
                return;
            }
            object row = sheet.GetRow(rowID);
            FlattenRoot(row, prefix, observation, 5);
        }
        catch (Exception e)
        {
            RegisterCapability($"{prefix}.__row", typeof(T), "row", false, true, $"row unavailable: {e.GetType().Name}");
        }
    }

    private void FlattenRoot(object? value, string prefix, ForetellObservation observation, int maxDepth)
    {
        if (value == null) return;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var budget = MaxFabricEntriesPerObject;
        FlattenObject(value, prefix, observation, 0, maxDepth, visited, ref budget);
    }

    private void FlattenObject(object? value, string path, ForetellObservation observation, int depth, int maxDepth,
        HashSet<object> visited, ref int budget)
    {
        if (value == null || budget <= 0) return;
        var type = value.GetType();
        if (TryStoreScalar(value, type, path, observation))
        {
            --budget;
            return;
        }
        if (IsEncounterAuthored(type))
        {
            RegisterCapability(path, type, path, false, true, "encounter-authored knowledge explicitly forbidden");
            return;
        }
        if (IsOperationalType(type))
        {
            RegisterCapability(path, type, path, false, true, "operational/API plumbing; not game-state evidence");
            return;
        }
        if (depth >= maxDepth)
        {
            RegisterCapability(path, type, path, false, true, "bounded traversal reached; parent identity already captured");
            return;
        }
        if (!type.IsValueType && !visited.Add(value))
        {
            RegisterCapability(path, type, path, false, true, "reference cycle");
            return;
        }

        if (value is IEnumerable enumerable and not string)
        {
            var n = 0;
            foreach (var item in enumerable)
            {
                if (n >= 32 || budget <= 0) break;
                FlattenObject(item, $"{path}[{n}]", observation, depth + 1, maxDepth, visited, ref budget);
                ++n;
            }
            observation.Numeric[$"{path}.__sampledCount"] = n;
            RegisterCapability($"{path}.__sampledCount", type, "IEnumerable", true, false, n >= 32 ? "collection sampled; dedicated sources cover high-cardinality actors" : "");
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var p in type.GetProperties(flags).OrderBy(p => p.Name))
        {
            if (budget <= 0) break;
            var memberPath = $"{path}.{p.Name}";
            if (p.GetIndexParameters().Length != 0)
            {
                RegisterCapability(memberPath, type, p.Name, false, true, "indexer handled separately when enum-addressable");
                continue;
            }
            if (p.GetMethod == null || p.GetMethod.IsStatic)
            {
                RegisterCapability(memberPath, type, p.Name, false, true, "not readable instance data");
                continue;
            }
            try
            {
                FlattenObject(p.GetValue(value), memberPath, observation, depth + 1, maxDepth, visited, ref budget);
            }
            catch (Exception e)
            {
                RegisterCapability(memberPath, type, p.Name, false, true, $"getter rejected: {e.GetType().Name}");
            }
        }
        foreach (var f in type.GetFields(flags).OrderBy(f => f.Name))
        {
            if (budget <= 0 || f.IsStatic) break;
            var memberPath = $"{path}.{f.Name}";
            try
            {
                FlattenObject(f.GetValue(value), memberPath, observation, depth + 1, maxDepth, visited, ref budget);
            }
            catch (Exception e)
            {
                RegisterCapability(memberPath, type, f.Name, false, true, $"field rejected: {e.GetType().Name}");
            }
        }
    }

    private bool TryStoreScalar(object value, Type type, string path, ForetellObservation observation)
    {
        if (type.IsEnum)
        {
            observation.Numeric[path] = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            observation.Text[path + ".name"] = value.ToString() ?? "";
            RegisterCapability(path, type, path, true, false, "enum numeric+name");
            return true;
        }
        if (value is bool b)
        {
            observation.Numeric[path] = b ? 1 : 0;
            RegisterCapability(path, type, path, true, false, "bool");
            return true;
        }
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            try { observation.Numeric[path] = Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { observation.Text[path] = value.ToString() ?? ""; }
            RegisterCapability(path, type, path, true, false, "numeric");
            return true;
        }
        if (value is string s)
        {
            observation.Text[path] = s.Length <= 160 ? s : s[..160];
            RegisterCapability(path, type, path, true, false, "text");
            return true;
        }
        if (value is DateTime dt)
        {
            observation.Numeric[path] = dt.Ticks;
            RegisterCapability(path, type, path, true, false, "time");
            return true;
        }
        if (value is TimeSpan ts)
        {
            observation.Numeric[path] = ts.TotalSeconds;
            RegisterCapability(path, type, path, true, false, "duration");
            return true;
        }
        if (value is Guid guid)
        {
            observation.Text[path] = guid.ToString("N");
            RegisterCapability(path, type, path, true, false, "guid");
            return true;
        }
        if (type.IsPointer || value is IntPtr or UIntPtr)
        {
            RegisterCapability(path, type, path, false, true, "pointer/address deliberately not treated as evidence");
            return true;
        }
        return false;
    }

    private void FlattenEnumIndexers(object? source, string prefix, ForetellObservation observation)
    {
        if (source == null) return;
        var type = source.GetType();
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var pars = p.GetIndexParameters();
            if (pars.Length != 1 || !pars[0].ParameterType.IsEnum || p.GetMethod == null) continue;
            foreach (var key in Enum.GetValues(pars[0].ParameterType).Cast<object>().Take(512))
            {
                var path = $"{prefix}.{p.Name}[{key}]";
                try
                {
                    var v = p.GetValue(source, [key]);
                    if (v != null) TryStoreScalar(v, v.GetType(), path, observation);
                }
                catch (Exception e)
                {
                    RegisterCapability(path, type, p.Name, false, true, $"indexer rejected: {e.GetType().Name}");
                }
            }
        }
    }

    private static bool IsEncounterAuthored(Type type)
    {
        var n = type.FullName ?? type.Name;
        return n.Contains("BossMod.Modules.", StringComparison.Ordinal)
            || n.Contains("BossModule", StringComparison.Ordinal)
            || n.Contains("StateMachine", StringComparison.Ordinal)
            || n.Contains("BossComponent", StringComparison.Ordinal);
    }

    private static bool IsOperationalType(Type type)
    {
        if (typeof(Delegate).IsAssignableFrom(type)) return true;
        var n = type.FullName ?? type.Name;
        return n.Contains("ImGui", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Window", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Logger", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Texture", StringComparison.OrdinalIgnoreCase)
            || n.Contains("CommandManager", StringComparison.OrdinalIgnoreCase)
            || n.Contains("SigScanner", StringComparison.OrdinalIgnoreCase)
            || n.Contains("InteropProvider", StringComparison.OrdinalIgnoreCase);
    }

    private void RegisterCapability(string key, Type sourceType, string member, bool ingested, bool excluded, string reason)
    {
        var coverage = _store.Coverage ??= new();
        if (!coverage.Items.TryGetValue(key, out var cap))
        {
            cap = new()
            {
                Key = key,
                Category = key.Split('.', 2)[0],
                SourceType = sourceType.FullName ?? sourceType.Name,
                Member = member
            };
            coverage.Items[key] = cap;
        }
        ++cap.Seen;
        cap.Ingested |= ingested;
        cap.Excluded |= excluded;
        if (!string.IsNullOrEmpty(reason)) cap.Reason = reason;
    }

    private void RegisterRecordedFeatures(ForetellObservation observation)
    {
        observation.Numeric ??= [];
        observation.Text ??= [];
        foreach (var key in observation.Numeric.Keys)
            RegisterCapability(key, typeof(ForetellObservation), key, true, false, "replayed recorded feature");
        foreach (var key in observation.Text.Keys)
            RegisterCapability(key, typeof(ForetellObservation), key, true, false, "replayed recorded feature");
    }

    private void AccumulateDataFeatures(ForetellObservation observation)
    {
        MechanicEpisode? episode = _episodes.GetValueOrDefault(observation.Sequence);
        episode ??= BestEpisode(observation);
        if (episode == null) return;
        if (observation.Kind == ObservationKind.ActorSnapshot && observation.ActorID != episode.Trigger.ActorID && !episode.ParticipantPositions.ContainsKey(observation.ActorID))
            return;
        episode.AccumulateFeatures(observation);
    }

    private double[] ExtendFeatureVector(double[] core, MechanicEpisode episode)
    {
        var result = new double[OnlineClassifier.FeatureCount];
        Array.Copy(core, result, Math.Min(core.Length, OnlineClassifier.BaseFeatureCount));
        foreach (var (key, sum) in episode.FeatureSums)
        {
            var count = Math.Max(1, episode.FeatureCounts.GetValueOrDefault(key));
            var value = key.StartsWith("@text:", StringComparison.Ordinal) ? Math.Min(1d, sum / count) : NormalizeFeatureNumber(sum / count);
            var canonical = key.StartsWith("@text:", StringComparison.Ordinal) ? key[6..] : key;
            var hash = StableHash(canonical);
            var slot = OnlineClassifier.BaseFeatureCount + (int)(hash % OnlineClassifier.FabricFeatureCount);
            var sign = (hash & 0x80000000u) == 0 ? 1d : -1d;
            result[slot] = Math.Clamp(result[slot] + sign * value, -4, 4);
            MarkCapabilityUsed(canonical);
        }
        return result;
    }

    private static double NormalizeFeatureNumber(double value)
    {
        if (!double.IsFinite(value)) return 0;
        var sign = Math.Sign(value);
        return sign * Math.Tanh(Math.Log10(1 + Math.Abs(value)) / 3.5);
    }

    private static uint StableHash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in value)
            {
                hash ^= c;
                hash *= 16777619u;
            }
            return hash;
        }
    }

    private void MarkCapabilityUsed(string feature)
    {
        var key = feature;
        var eq = key.IndexOf('=');
        if (eq > 0) key = key[..eq];
        if (_store.Coverage.Items.TryGetValue(key, out var cap))
        {
            cap.Used = true;
            ++cap.UsedCount;
        }
    }

    private static string Fingerprint(ForetellObservation observation, string prefix)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in observation.Numeric.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(kv => kv.Key))
            sb.Append(k).Append('=').Append(v.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        foreach (var (k, v) in observation.Text.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(kv => kv.Key))
            sb.Append(k).Append('=').Append(v).Append(';');
        return StableHash(sb.ToString()).ToString("X8");
    }
}
'''

ONLINE = r'''namespace BossMod.Foretell;

public sealed class OnlineClassifier
{
    public const int BaseFeatureCount = 10;
    public const int FabricFeatureCount = 128;
    public const int FeatureCount = BaseFeatureCount + FabricFeatureCount;
    public const int ClassCount = 18;
    private readonly MLState _state;

    public OnlineClassifier(MLState state)
    {
        _state = state;
        var oldFeatureCount = Math.Max(0, state.FeatureCount);
        var validClasses = state.ClassCount == ClassCount && state.Weights?.Length == ClassCount;
        if (!validClasses || state.Weights.Any(w => w == null || w.Length != oldFeatureCount + 1) || oldFeatureCount != FeatureCount)
        {
            var old = state.Weights ?? [];
            var migrated = NewWeights();
            if (validClasses)
            {
                for (var c = 0; c < ClassCount; ++c)
                {
                    var row = old[c];
                    var copy = Math.Min(oldFeatureCount, FeatureCount);
                    Array.Copy(row, migrated[c], copy);
                    if (row.Length > oldFeatureCount)
                        migrated[c][FeatureCount] = row[oldFeatureCount];
                }
            }
            state.FeatureCount = FeatureCount;
            state.ClassCount = ClassCount;
            state.Weights = migrated;
        }
    }

    public static double[][] NewWeights() => Enumerable.Range(0, ClassCount).Select(_ => new double[FeatureCount + 1]).ToArray();

    public (MechanicKind Kind, float Confidence) Predict(ReadOnlySpan<double> x)
    {
        Span<double> logits = stackalloc double[ClassCount];
        double max = double.NegativeInfinity;
        for (var c = 0; c < ClassCount; ++c)
        {
            var w = _state.Weights[c];
            var z = w[FeatureCount];
            for (var i = 0; i < FeatureCount && i < x.Length; ++i) z += w[i] * x[i];
            logits[c] = z;
            max = Math.Max(max, z);
        }
        double sum = 0;
        for (var c = 0; c < ClassCount; ++c) sum += Math.Exp(logits[c] - max);
        var best = 0;
        double bestP = 0;
        for (var c = 0; c < ClassCount; ++c)
        {
            var p = Math.Exp(logits[c] - max) / sum;
            if (p > bestP) { bestP = p; best = c; }
        }
        return ((MechanicKind)best, (float)bestP);
    }

    public void Train(ReadOnlySpan<double> x, MechanicKind label, double learningRate = .018)
    {
        var y = Math.Clamp((int)label, 0, ClassCount - 1);
        Span<double> logits = stackalloc double[ClassCount];
        double max = double.NegativeInfinity;
        for (var c = 0; c < ClassCount; ++c)
        {
            var w = _state.Weights[c];
            var z = w[FeatureCount];
            for (var i = 0; i < FeatureCount && i < x.Length; ++i) z += w[i] * x[i];
            logits[c] = z;
            max = Math.Max(max, z);
        }
        double sum = 0;
        for (var c = 0; c < ClassCount; ++c) sum += Math.Exp(logits[c] - max);
        for (var c = 0; c < ClassCount; ++c)
        {
            var p = Math.Exp(logits[c] - max) / sum;
            var error = (c == y ? 1d : 0d) - p;
            var w = _state.Weights[c];
            for (var i = 0; i < FeatureCount && i < x.Length; ++i) w[i] += learningRate * error * x[i];
            w[FeatureCount] += learningRate * error;
        }
        ++_state.Updates;
    }
}
'''

Path('BossMod/Foretell/ForetellDataFabric.cs').write_text(DATA_FABRIC, encoding='utf-8')
Path('BossMod/Foretell/OnlineClassifier.cs').write_text(ONLINE, encoding='utf-8')

# Model: generic feature bags + auditable coverage persisted with the learner.
replace_once('BossMod/Foretell/ForetellModel.cs',
    '    PositionSample, Displacement,\n    ClientMetadata\n}',
    '    PositionSample, Displacement, ActorSnapshot,\n    ClientMetadata, GenericFeature\n}')
replace_once('BossMod/Foretell/ForetellModel.cs',
    'public enum SourceKind { Unknown, Player, Pet, Enemy, EventObject, Environment }\n',
    '''public enum SourceKind { Unknown, Player, Pet, Enemy, EventObject, Environment }\n\npublic sealed class DataCapability\n{\n    public string Key { get; set; } = "";\n    public string Category { get; set; } = "";\n    public string SourceType { get; set; } = "";\n    public string Member { get; set; } = "";\n    public long Seen { get; set; }\n    public bool Ingested { get; set; }\n    public bool Used { get; set; }\n    public long UsedCount { get; set; }\n    public bool Excluded { get; set; }\n    public string Reason { get; set; } = "";\n    [JsonIgnore] public bool Unaccounted => !Ingested && !Excluded;\n}\n\npublic sealed class DataCoverage\n{\n    public Dictionary<string, DataCapability> Items { get; set; } = [];\n    [JsonIgnore] public int Discovered => Items.Count;\n    [JsonIgnore] public int Ingested => Items.Values.Count(v => v.Ingested);\n    [JsonIgnore] public int Used => Items.Values.Count(v => v.Used);\n    [JsonIgnore] public int Excluded => Items.Values.Count(v => v.Excluded);\n    [JsonIgnore] public int Unaccounted => Items.Values.Count(v => v.Unaccounted);\n}\n''')
replace_once('BossMod/Foretell/ForetellModel.cs', '    public int Schema { get; set; } = 3;', '    public int Schema { get; set; } = 4;')
replace_once('BossMod/Foretell/ForetellModel.cs',
    '    public MLState ML { get; set; } = new();\n}',
    '    public MLState ML { get; set; } = new();\n    public DataCoverage Coverage { get; set; } = new();\n}')
replace_once('BossMod/Foretell/ForetellModel.cs',
    '    public string Detail { get; set; } = "";\n}',
    '    public string Detail { get; set; } = "";\n    public Dictionary<string, double> Numeric { get; set; } = [];\n    public Dictionary<string, string> Text { get; set; } = [];\n}')

# Episode-level aggregation; every recorded generic datum can reach the learner.
replace_once('BossMod/Foretell/ForetellRuntime.cs',
    '    public Dictionary<ObservationKind, int> Evidence { get; } = [];\n    public bool Finalized { get; set; }',
    '''    public Dictionary<ObservationKind, int> Evidence { get; } = [];\n    public Dictionary<string, double> FeatureSums { get; } = [];\n    public Dictionary<string, int> FeatureCounts { get; } = [];\n    public bool Finalized { get; set; }\n\n    public void AccumulateFeatures(ForetellObservation observation)\n    {\n        foreach (var (key, value) in observation.Numeric)\n        {\n            if (FeatureSums.Count >= 4096 && !FeatureSums.ContainsKey(key)) continue;\n            FeatureSums[key] = FeatureSums.GetValueOrDefault(key) + value;\n            FeatureCounts[key] = FeatureCounts.GetValueOrDefault(key) + 1;\n        }\n        foreach (var (key, value) in observation.Text)\n        {\n            var token = $"@text:{key}={value}";\n            if (FeatureSums.Count >= 4096 && !FeatureSums.ContainsKey(token)) continue;\n            FeatureSums[token] = FeatureSums.GetValueOrDefault(token) + 1;\n            FeatureCounts[token] = FeatureCounts.GetValueOrDefault(token) + 1;\n        }\n    }''')

# Engine hooks and schema migration.
replace_once('BossMod/Foretell/ForetellEngine.cs',
    '        SamplePartyPositions();\n    }',
    '        SamplePartyPositions();\n        SampleDataFabric(force: true);\n    }')
replace_once('BossMod/Foretell/ForetellEngine.cs',
    '            SamplePartyPositions();\n            _lastPositionSample = _ws.CurrentTime;',
    '            SamplePartyPositions();\n            SampleDataFabric();\n            _lastPositionSample = _ws.CurrentTime;')
replace_once('BossMod/Foretell/ForetellEngine.cs',
    '        _tracks.Clear();\n        _predictions.Clear();',
    '        _tracks.Clear();\n        ResetDataFabric();\n        _predictions.Clear();')
replace_once('BossMod/Foretell/ForetellEngine.cs', '        _store.Schema = Math.Max(_store.Schema, 3);', '        _store.Schema = Math.Max(_store.Schema, 4);')
replace_once('BossMod/Foretell/ForetellEngine.cs',
    '        _store.ML ??= new();\n        foreach (var encounter',
    '        _store.ML ??= new();\n        _store.Coverage ??= new();\n        _store.Coverage.Items ??= [];\n        foreach (var encounter')

# Central enrichment is replay-safe: live captures current generic data; replay consumes exactly what was recorded.
replace_once('BossMod/Foretell/ForetellLearning.cs',
    '        if (observation.TerritoryID == 0) observation.TerritoryID = _territory;\n\n        FinalizeDue(observation.At);',
    '        if (observation.TerritoryID == 0) observation.TerritoryID = _territory;\n        if (replaying) RegisterRecordedFeatures(observation); else EnrichObservation(observation);\n\n        FinalizeDue(observation.At);')
replace_once('BossMod/Foretell/ForetellLearning.cs',
    '        if (observation.Kind == ObservationKind.CastStart)\n            ApplyActionMetadataPrior(observation);\n\n        _recentSignals.Enqueue(observation);',
    '        if (observation.Kind == ObservationKind.CastStart)\n            ApplyActionMetadataPrior(observation);\n        AccumulateDataFeatures(observation);\n\n        _recentSignals.Enqueue(observation);')
replace_once('BossMod/Foretell/ForetellLearning.cs',
    '        var kind = ClassifyEpisode(episode, affected, fit);\n        var score = EvidenceScore(kind, fit);',
    '''        var kind = ClassifyEpisode(episode, affected, fit);\n        var score = EvidenceScore(kind, fit);\n        var features = ExtendFeatureVector(BuildEpisodeFeatures(episode, affected, fit), episode);\n        var ml = _cfg.EnableML ? _classifier.Predict(features) : (MechanicKind.Unknown, 0f);\n        if (kind == MechanicKind.Unknown && ml.Item1 != MechanicKind.Unknown && ml.Item2 >= .72f)\n        {\n            kind = ml.Item1;\n            score = Math.Max(score, ml.Item2 * .62f);\n            episode.AddEvidence(ObservationKind.GenericFeature);\n        }\n        else if (kind != MechanicKind.Unknown && ml.Item1 == kind && ml.Item2 >= .55f)\n        {\n            score = Math.Clamp(score + (ml.Item2 - .5f) * .12f, 0, 1);\n            episode.AddEvidence(ObservationKind.GenericFeature);\n        }''')
replace_once('BossMod/Foretell/ForetellLearning.cs',
    '        var features = BuildEpisodeFeatures(episode, affected, fit);\n        if (_cfg.EnableLearning && _cfg.EnableML && kind != MechanicKind.Unknown)\n            _classifier.Train(features, kind);\n        var ml = _cfg.EnableML ? _classifier.Predict(features) : (MechanicKind.Unknown, 0f);',
    '        if (_cfg.EnableLearning && _cfg.EnableML && kind != MechanicKind.Unknown)\n            _classifier.Train(features, kind);')

# ActionEffect and status/map/director payloads are no longer discarded.
replace_once('BossMod/Foretell/ForetellObserver.cs',
    '        ProcessObservation(Observation(ObservationKind.ActionResolved, actor, action, value1: targets.Count));',
    '        ProcessRichObservation(Observation(ObservationKind.ActionResolved, actor, action, value1: targets.Count), ev);')
replace_once('BossMod/Foretell/ForetellObserver.cs',
    '        ProcessObservation(obs);\n    }\n\n    private void OnStatusLose',
    '        ProcessRichObservation(obs, status);\n    }\n\n    private void OnStatusLose')
replace_once('BossMod/Foretell/ForetellObserver.cs',
    '        ProcessObservation(obs);\n    }\n\n    private void OnIcon',
    '        ProcessRichObservation(obs, status);\n    }\n\n    private void OnIcon')
replace_once('BossMod/Foretell/ForetellObserver.cs',
    '            ProcessObservation(Observation(ObservationKind.ActionTimelineSync, actor));',
    '            ProcessRichObservation(Observation(ObservationKind.ActionTimelineSync, actor), events);')
replace_once('BossMod/Foretell/ForetellObserver.cs',
    '            ProcessObservation(Observation(ObservationKind.ActionTimelineSync, actor, ev.Item2, detail: ev.Item1.ToString("X")));',
    '            ProcessRichObservation(Observation(ObservationKind.ActionTimelineSync, actor, ev.Item2, detail: ev.Item1.ToString("X")), events);')
replace_once('BossMod/Foretell/ForetellObserver.cs',
    '    private void OnMapEffect(WorldState.OpMapEffect op)\n        => ProcessObservation(Observation(ObservationKind.MapEffect, primary: ToUInt(op.Index) ?? 0, secondary: ToUInt(op.State) ?? 0));',
    '    private void OnMapEffect(WorldState.OpMapEffect op)\n        => ProcessRichObservation(Observation(ObservationKind.MapEffect, primary: ToUInt(op.Index) ?? 0, secondary: ToUInt(op.State) ?? 0), op);')
replace_once('BossMod/Foretell/ForetellObserver.cs',
    '    private void OnLegacyMapEffect(WorldState.OpLegacyMapEffect op)\n        => ProcessObservation(Observation(ObservationKind.LegacyMapEffect,',
    '    private void OnLegacyMapEffect(WorldState.OpLegacyMapEffect op)\n        => ProcessRichObservation(Observation(ObservationKind.LegacyMapEffect,')
replace_once('BossMod/Foretell/ForetellObserver.cs',
    '            value1: ToUInt(op.Data) ?? 0));\n\n    private void OnDirectorUpdate',
    '            value1: ToUInt(op.Data) ?? 0), op);\n\n    private void OnDirectorUpdate')
replace_once('BossMod/Foretell/ForetellObserver.cs',
    '    private void OnDirectorUpdate(WorldState.OpDirectorUpdate op)\n        => ProcessObservation(Observation(ObservationKind.DirectorUpdate,',
    '    private void OnDirectorUpdate(WorldState.OpDirectorUpdate op)\n        => ProcessRichObservation(Observation(ObservationKind.DirectorUpdate,')
replace_once('BossMod/Foretell/ForetellObserver.cs',
    '            detail: (ToUInt(op.Param4) ?? 0).ToString("X")));',
    '            detail: (ToUInt(op.Param4) ?? 0).ToString("X")), op);')

# Surface the audit in the cockpit: no more silent data gaps.
replace_once('BossMod/Foretell/ForetellInspector.cs',
    '        ImGui.TextUnformatted($"ML updates: {_store.ML.Updates:N0} | current predictions: {_predictions.Count} | active candidates awaiting outcome: {_episodes.Values.Count(e => !e.Finalized)}");',
    '        ImGui.TextUnformatted($"ML updates: {_store.ML.Updates:N0} | current predictions: {_predictions.Count} | active candidates awaiting outcome: {_episodes.Values.Count(e => !e.Finalized)}");\n        var coverage = _store.Coverage;\n        ImGui.TextUnformatted($"DATA FABRIC: {coverage.Discovered} discovered | {coverage.Ingested} ingested | {coverage.Used} used by learner | {coverage.Excluded} explicitly excluded | {coverage.Unaccounted} UNACCOUNTED");\n        if (coverage.Unaccounted != 0) ImGui.TextUnformatted("WARNING: Data Fabric discovered fields that are neither ingested nor explicitly excluded. Export diagnostics and treat this build as incomplete.");')
replace_once('BossMod/Foretell/ForetellInspector.cs',
    '        ImGui.TextUnformatted("For cast actions Foretell also reads local client Action metadata (CastType, EffectRange, XAxisModifier, TargetArea, Omen/VFX and actor hitbox) as a prior before outcome evidence is available.");',
    '        ImGui.TextUnformatted("For cast actions Foretell also reads local client Action metadata (CastType, EffectRange, XAxisModifier, TargetArea, Omen/VFX and actor hitbox) as a prior before outcome evidence is available.");\n        ImGui.TextWrapped("Data Fabric additionally flattens generic structured WorldState, actor/target state, full event payloads, runtime gameplay services and complete relevant Lumina rows into hashed learner features. Encounter-authored BossModule/state-machine/component knowledge is explicitly excluded.");\n        ImGui.TextUnformatted($"Coverage audit right now: {_store.Coverage.Discovered} discovered / {_store.Coverage.Ingested} ingested / {_store.Coverage.Used} used / {_store.Coverage.Excluded} excluded / {_store.Coverage.Unaccounted} unaccounted.");')

# Documentation explicitly defines the completeness boundary.
readme = load('BossMod/Foretell/README.md')
readme += '''\n## Data Fabric completeness contract\n\nForetell ingests generic structured evidence available through BMR WorldState, Dalamud runtime gameplay services, actor/target state, raw event payloads, and relevant Lumina rows. Reflection discovers scalar/enum/text fields recursively and feeds them through a stable hashed feature space; ActionEffect/status/map/director payload details are retained in replay instead of discarded.\n\nThe one deliberate boundary is encounter-authored knowledge: BossModule implementations, state machines, encounter components/layouts/presets and equivalent hand-written answers are excluded. Foretell learns from the raw game data instead. The in-game coverage audit reports discovered, ingested, learner-used, explicitly excluded and unaccounted fields; unaccounted data is treated as a defect rather than silently ignored.\n'''
save('BossMod/Foretell/README.md', readme)

print('Foretell v4 Data Fabric patch applied')
