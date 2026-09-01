using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace BossMod.Foretell;

// Generic, encounter-agnostic data ingestion. This deliberately refuses hand-authored BossModule/state-machine/
// encounter-component knowledge: Foretell may consume the same raw game state, but never their authored answers.
public sealed partial class ForetellEngine
{
    // A root owns its own traversal budget. Runtime state is split into independent roots below, so a large
    // party/actor collection can never starve camera, client, network or environment state. The budget is a
    // recursion/bug guard, not a collection sampling policy.
    private const int MaxFabricEntriesPerRoot = 4096;
    private const int RuntimeRootCount = 21;
    private DateTime _lastFabricSample;
    private DateTime _lastNativeFabricSample;
    private int _runtimeRootCursor;
    private int _actorFabricCursor;
    private readonly Dictionary<ulong, string> _actorFabricFingerprint = [];
    private readonly Dictionary<ulong, string> _nativeActorFabricFingerprint = [];
    private readonly Dictionary<ulong, FabricActorTrack> _fabricActorTracks = [];

    private sealed class FabricActorTrack
    {
        public DateTime At;
        public Vector2 Position;
        public float Rotation;
    }

    private void ResetDataFabric()
    {
        _actorFabricFingerprint.Clear();
        _nativeActorFabricFingerprint.Clear();
        _fabricActorTracks.Clear();
        _runtimeRootCursor = 0;
        _actorFabricCursor = 0;
        ResetNativeDataFabric();
        _lastFabricSample = default;
        _lastNativeFabricSample = default;
    }

    private void SampleDataFabric(bool force = false)
    {
        var now = ObservationNow();

        // Reflection remains complete over a sweep, but only one independent root and one generic actor are
        // traversed per slice. This keeps the game/framework thread free of the old half-second aggregate spike.
        if (force || (now - _lastFabricSample).TotalMilliseconds >= 250)
        {
            _lastFabricSample = now;
            RefreshRuntimeContextSlice();
            SampleGenericActorSlice();
        }

        // Native character fields are cheap direct reads and include time-sensitive tether/timeline progress, so
        // retain the original 2 Hz coverage without paying generic reflection for every actor in the same frame.
        if (!force && (now - _lastNativeFabricSample).TotalMilliseconds < 500)
            return;
        _lastNativeFabricSample = now;
        SampleNativeActorState(now);

        foreach (var dead in _fabricActorTracks.Keys.Where(id => _ws.Actors.Find(id) == null).ToArray())
        {
            _fabricActorTracks.Remove(dead);
            _actorFabricFingerprint.Remove(dead);
            _nativeActorFabricFingerprint.Remove(dead);
        }

        SampleNativeEnvironment();
        SampleNativeCamera();
    }

    private void SampleGenericActorSlice()
    {
        var actors = _ws.Actors.ToArray();
        if (actors.Length == 0)
            return;
        if (_actorFabricCursor >= actors.Length)
            _actorFabricCursor = 0;

        var actor = actors[_actorFabricCursor++];
        var obs = Observation(ObservationKind.ActorSnapshot, actor, detail: $"{actor.Type}:generic");
        EnrichObservation(obs, actor);
        var fingerprint = Fingerprint(obs, "actor.") + Fingerprint(obs, "static.");
        if (_actorFabricFingerprint.GetValueOrDefault(actor.InstanceID) == fingerprint)
            return;
        _actorFabricFingerprint[actor.InstanceID] = fingerprint;
        ProcessObservation(obs, enriched: true);
    }

    private void SampleNativeActorState(DateTime now)
    {
        foreach (var actor in _ws.Actors)
        {
            var obs = Observation(ObservationKind.ActorSnapshot, actor, detail: $"{actor.Type}:native");
            EnrichNativeCharacter(obs, actor);
            var pos = new Vector2(actor.Position.X, actor.Position.Z);
            if (_fabricActorTracks.TryGetValue(actor.InstanceID, out var previous))
            {
                var dt = Math.Max(.001, (now - previous.At).TotalSeconds);
                obs.Numeric["derived.actor.speed"] = Vector2.Distance(previous.Position, pos) / dt;
                obs.Numeric["derived.actor.angularSpeed"] = Math.Abs(NormalizeAngle(actor.Rotation.Rad - previous.Rotation)) / dt;
            }
            _fabricActorTracks[actor.InstanceID] = new() { At = now, Position = pos, Rotation = actor.Rotation.Rad };

            var fingerprint = Fingerprint(obs, "native.character.") + Fingerprint(obs, "derived.actor.");
            if (_nativeActorFabricFingerprint.GetValueOrDefault(actor.InstanceID) == fingerprint)
                continue;
            _nativeActorFabricFingerprint[actor.InstanceID] = fingerprint;
            ProcessObservation(obs, enriched: true);
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
        ProcessObservation(observation, enriched: true);
    }

    private void EnrichObservation(ForetellObservation observation, object? payload = null)
    {
        observation.Numeric ??= [];
        observation.Text ??= [];
        observation.Binary ??= [];

        // Independent runtime roots are emitted as GenericFeature observations. Copying thousands of cached
        // values into every semantic event multiplied allocations and reflection cost without adding evidence.

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

    private void RefreshRuntimeContextSlice()
    {
        var slot = _runtimeRootCursor++ % RuntimeRootCount;
        var obs = Observation(ObservationKind.GenericFeature, detail: $"runtime slice {slot}");

        // Every source still owns the same independent 4096-entry traversal budget. A complete sweep now spans
        // several frames instead of synchronously flattening every service and collection in one framework tick.
        switch (slot)
        {
            case 0:
                obs.Detail = "runtime.worldState";
                TryStoreScalar(_ws.QPF, typeof(ulong), "runtime.worldState.qpf", obs);
                TryStoreScalar(_ws.GameVersion, typeof(string), "runtime.worldState.gameVersion", obs);
                TryStoreScalar(_ws.CurrentZone, typeof(ushort), "runtime.worldState.currentZone", obs);
                TryStoreScalar(_ws.CurrentCFCID, typeof(ushort), "runtime.worldState.currentCFCID", obs);
                TryStoreScalar(_ws.IsPvPArea, typeof(bool), "runtime.worldState.isPvPArea", obs);
                TryStoreScalar(_ws.RSVEntries.Count, typeof(int), "runtime.worldState.rsvCount", obs);
                break;
            case 1:
                obs.Detail = "runtime.frame";
                FlattenRoot(_ws.Frame, "runtime.frame", obs, 4);
                break;
            case 2:
                obs.Detail = "runtime.waymarks";
                FlattenRoot(_ws.Waymarks, "runtime.waymarks", obs, 4);
                break;
            case 3:
                obs.Detail = "runtime.party";
                FlattenRoot(_ws.Party, "runtime.party", obs, 5);
                break;
            case 4:
                obs.Detail = "runtime.client";
                FlattenRoot(_ws.Client, "runtime.client", obs, 5);
                break;
            case 5:
                obs.Detail = "runtime.deepDungeon";
                FlattenRoot(_ws.DeepDungeon, "runtime.deepDungeon", obs, 5);
                break;
            case 6:
                obs.Detail = "runtime.network";
                FlattenRoot(_ws.Network, "runtime.network", obs, 4);
                break;
            case 7:
                obs.Detail = "runtime.clientState";
                FlattenRoot(Service.ClientState, "runtime.clientState", obs, 2);
                break;
            case 8:
                obs.Detail = "runtime.playerState";
                FlattenRoot(Service.PlayerState, "runtime.playerState", obs, 2);
                break;
            case 9:
                obs.Detail = "runtime.targetManager";
                FlattenRoot(Service.TargetManager, "runtime.targetManager", obs, 2);
                break;
            case 10:
                obs.Detail = "runtime.condition";
                FlattenRoot(Service.Condition, "runtime.condition", obs, 2);
                FlattenEnumIndexers(Service.Condition, "runtime.condition", obs);
                break;
            case 11:
                obs.Detail = "runtime.keyState";
                FlattenRoot(Service.KeyState, "runtime.keyState", obs, 1);
                FlattenEnumIndexers(Service.KeyState, "runtime.keyState", obs);
                break;
            case 12:
                obs.Detail = "runtime.gameGui";
                FlattenRoot(Service.GameGui, "runtime.gameGui", obs, 1);
                break;
            case 13:
                obs.Detail = "runtime.gameConfig";
                FlattenRoot(Service.GameConfig, "runtime.gameConfig", obs, 1);
                break;
            case 14:
                obs.Detail = "runtime.partyList";
                FlattenRoot(Service.PartyList, "runtime.partyList", obs, 4);
                break;
            case 15:
                obs.Detail = "runtime.buddyList";
                FlattenRoot(Service.BuddyList, "runtime.buddyList", obs, 4);
                break;
            case 16:
                obs.Detail = "runtime.fateTable";
                FlattenRoot(Service.FateTable, "runtime.fateTable", obs, 4);
                break;
            case 17:
                obs.Detail = "runtime.dutyState";
                FlattenRoot(Service.DutyState, "runtime.dutyState", obs, 3);
                break;
            case 18:
                obs.Detail = "runtime.gamepadState";
                FlattenRoot(Service.GamepadState, "runtime.gamepadState", obs, 3);
                break;
            case 19:
                AuditDalamudPluginServices();
                return;
            case 20:
                obs.Detail = "static.territory";
                TryFlattenRow<Lumina.Excel.Sheets.TerritoryType>(_territory, "static.territory", obs);
                break;
        }

        if (obs.Numeric.Count != 0 || obs.Text.Count != 0 || obs.Binary.Count != 0)
            ProcessObservation(obs, enriched: true);
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
        var budget = MaxFabricEntriesPerRoot;
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
            RegisterCapability(path, type, path, false, false, "bounded traversal reached; dedicated ingestion is required if this is gameplay state");
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
                if (budget <= 0) break;
                FlattenObject(item, $"{path}[{n}]", observation, depth + 1, maxDepth, visited, ref budget);
                ++n;
            }
            observation.Numeric[$"{path}.__sampledCount"] = n;
            RegisterCapability($"{path}.__sampledCount", type, "IEnumerable", true, false,
                budget <= 0 ? "root safety budget exhausted; source must be split into a dedicated root" : "complete collection");
            if (budget <= 0)
                RegisterCapability($"{path}.__truncated", type, "IEnumerable", false, false, "root safety budget exhausted");
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var p in type.GetProperties(flags).OrderBy(p => p.Name))
        {
            if (budget <= 0) break;
            var memberPath = $"{path}.{p.Name}";
            if (p.GetIndexParameters().Length != 0)
            {
                var enumAddressable = p.GetIndexParameters().Length == 1 && p.GetIndexParameters()[0].ParameterType.IsEnum;
                RegisterCapability(memberPath, type, p.Name, false, enumAddressable,
                    enumAddressable ? "duplicate indexer handled by complete enum traversal" : "non-enum indexer requires dedicated ingestion");
                continue;
            }
            if (p.GetMethod == null || p.GetMethod.IsStatic)
            {
                RegisterCapability(memberPath, type, p.Name, false, true, "not readable instance data");
                continue;
            }
            if (RejectNonBoxableMember(p.PropertyType, memberPath, type, p.Name))
                continue;
            try
            {
                FlattenObject(p.GetValue(value), memberPath, observation, depth + 1, maxDepth, visited, ref budget);
            }
            catch (Exception e)
            {
                RegisterCapability(memberPath, type, p.Name, false, false, $"getter rejected: {e.GetType().Name}");
            }
        }
        foreach (var f in type.GetFields(flags).OrderBy(f => f.Name))
        {
            if (budget <= 0 || f.IsStatic) break;
            var memberPath = $"{path}.{f.Name}";
            if (RejectNonBoxableMember(f.FieldType, memberPath, type, f.Name))
                continue;
            try
            {
                FlattenObject(f.GetValue(value), memberPath, observation, depth + 1, maxDepth, visited, ref budget);
            }
            catch (Exception e)
            {
                RegisterCapability(memberPath, type, f.Name, false, false, $"field rejected: {e.GetType().Name}");
            }
        }
    }

    // Reflection cannot box these CLR types. In particular, invoking PropertyInfo.GetValue for a function-pointer
    // member makes MethodBaseInvoker ask the runtime for a managed call signature and can terminate CoreCLR with
    // 0x80131506 before managed exception handling is reached.
    private bool RejectNonBoxableMember(Type memberType, string path, Type sourceType, string member)
    {
        if (memberType.IsPointer || memberType.IsFunctionPointer)
        {
            RegisterCapability(path, sourceType, member, false, true, "native pointer/function pointer is operational layout, never learner evidence");
            return true;
        }
        if (memberType.IsByRef || memberType.IsByRefLike || memberType == typeof(TypedReference) || memberType == typeof(ArgIterator))
        {
            RegisterCapability(path, sourceType, member, false, false, "non-boxable CLR value requires dedicated typed ingestion");
            return true;
        }
        return false;
    }

    private bool TryStoreScalar(object value, Type type, string path, ForetellObservation observation)
    {
        if (value is byte[] bytes)
        {
            observation.Binary[path] = bytes.ToArray();
            RegisterCapability(path, type, path, true, false, $"binary {bytes.Length} bytes, lossless");
            return true;
        }
        if (value is ReadOnlyMemory<byte> rom)
        {
            observation.Binary[path] = rom.ToArray();
            RegisterCapability(path, type, path, true, false, $"binary {rom.Length} bytes, lossless");
            return true;
        }
        if (value is Memory<byte> mem)
        {
            observation.Binary[path] = mem.ToArray();
            RegisterCapability(path, type, path, true, false, $"binary {mem.Length} bytes, lossless");
            return true;
        }
        if (value is ArraySegment<byte> segment)
        {
            observation.Binary[path] = segment.ToArray();
            RegisterCapability(path, type, path, true, false, $"binary {segment.Count} bytes, lossless");
            return true;
        }
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
            observation.Text[path] = s;
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
            foreach (var key in Enum.GetValues(pars[0].ParameterType).Cast<object>())
            {
                var path = $"{prefix}.{p.Name}[{key}]";
                try
                {
                    var v = p.GetValue(source, [key]);
                    if (v != null) TryStoreScalar(v, v.GetType(), path, observation);
                }
                catch (Exception e)
                {
                    RegisterCapability(path, type, p.Name, false, false, $"indexer rejected: {e.GetType().Name}");
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
        if (typeof(Type).IsAssignableFrom(type) || typeof(MemberInfo).IsAssignableFrom(type)) return true;
        if (type == typeof(RuntimeTypeHandle) || type == typeof(RuntimeMethodHandle) || type == typeof(RuntimeFieldHandle) || type == typeof(RuntimeArgumentHandle)) return true;
        var n = type.FullName ?? type.Name;
        return (type.Namespace?.StartsWith("System.Reflection", StringComparison.Ordinal) ?? false)
            || n.Contains("ImGui", StringComparison.OrdinalIgnoreCase)
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
        observation.Binary ??= [];
        foreach (var key in observation.Numeric.Keys)
            RegisterCapability(key, typeof(ForetellObservation), key, true, false, "replayed recorded feature");
        foreach (var key in observation.Text.Keys)
            RegisterCapability(key, typeof(ForetellObservation), key, true, false, "replayed recorded feature");
        foreach (var key in observation.Binary.Keys)
            RegisterCapability(key, typeof(ForetellObservation), key, true, false, "replayed lossless binary feature");
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

        if (episode.BinaryBytes > 0)
        {
            var scale = 1d / Math.Sqrt(Math.Max(1, episode.BinaryBytes));
            for (var i = 0; i < episode.BinaryBuckets.Length; ++i)
            {
                var slot = OnlineClassifier.BaseFeatureCount + i;
                result[slot] = Math.Clamp(result[slot] + Math.Tanh(episode.BinaryBuckets[i] * scale), -4, 4);
            }
            foreach (var key in episode.BinaryKeys)
                MarkCapabilityUsed(key);
        }

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
