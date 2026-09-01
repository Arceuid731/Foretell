using System.Collections;
using System.Diagnostics;
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
    private const double MaxFabricTraversalMilliseconds = 1.0;
    private const double SlowFabricGetterMilliseconds = 2.0;
    private const int RuntimeRootCount = 21;
    private const int NativeActorSlices = 2;
    private DateTime _lastFabricSample;
    private DateTime _lastNativeFabricSample;
    private int _runtimeRootCursor;
    private int _actorFabricCursor;
    private int _nativeActorSliceCursor;
    private readonly Dictionary<ulong, string> _actorFabricFingerprint = [];
    private readonly Dictionary<ulong, string> _nativeActorFabricFingerprint = [];
    private readonly Dictionary<ulong, FabricActorTrack> _fabricActorTracks = [];
    private readonly Dictionary<string, int> _fabricCollectionOffsets = [];
    private readonly HashSet<string> _slowFabricMembers = [];
    private readonly Dictionary<Type, PropertyInfo[]> _fabricPropertyCache = [];
    private readonly Dictionary<Type, FieldInfo[]> _fabricFieldCache = [];
    private long _fabricDeferredTraversals;
    private long _fabricQuarantinedGetters;

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
        _fabricCollectionOffsets.Clear();
        _runtimeRootCursor = 0;
        _actorFabricCursor = 0;
        _nativeActorSliceCursor = 0;
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
            SampleNativeActorSlice(now);

            foreach (var dead in _fabricActorTracks.Keys.Where(id => _ws.Actors.Find(id) == null).ToArray())
            {
                _fabricActorTracks.Remove(dead);
                _actorFabricFingerprint.Remove(dead);
                _nativeActorFabricFingerprint.Remove(dead);
            }
        }

        // Environment and camera retain their original 2 Hz cadence. Character direct reads are partitioned into
        // two alternating 250 ms slices above, preserving 2 Hz per actor without an all-actors frame spike.
        if (!force && (now - _lastNativeFabricSample).TotalMilliseconds < 500)
            return;
        _lastNativeFabricSample = now;
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
        EnrichActorCollections(obs, actor);
        var fingerprint = Fingerprint(obs, "actor.") + Fingerprint(obs, "static.");
        if (_actorFabricFingerprint.GetValueOrDefault(actor.InstanceID) == fingerprint)
            return;
        _actorFabricFingerprint[actor.InstanceID] = fingerprint;
        ProcessObservation(obs, enriched: true);
    }

    private void SampleNativeActorSlice(DateTime now)
    {
        var actors = _ws.Actors.ToArray();
        var slice = _nativeActorSliceCursor++ % NativeActorSlices;
        for (var i = slice; i < actors.Length; i += NativeActorSlices)
        {
            var actor = actors[i];
            if (!HasNativeCharacterLayout(actor.Type))
                continue;
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
            EnrichActorCore(observation, actor, "actor");
        if (target != null && target.InstanceID != actor?.InstanceID)
            EnrichActorCore(observation, target, "target");
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
            if (actor.NameID != 0)
                TryFlattenRow<Lumina.Excel.Sheets.BNpcName>(actor.NameID, "static.bnpcName", observation);
        }
    }

    private void EnrichActorCore(ForetellObservation observation, Actor actor, string prefix)
    {
        StoreFabric(observation, $"{prefix}.instanceID", actor.InstanceID);
        StoreFabric(observation, $"{prefix}.oid", actor.OID);
        StoreFabric(observation, $"{prefix}.spawnIndex", actor.SpawnIndex);
        StoreFabric(observation, $"{prefix}.layoutID", actor.LayoutID);
        StoreFabric(observation, $"{prefix}.fateID", actor.FateID);
        StoreFabric(observation, $"{prefix}.name", actor.Name);
        StoreFabric(observation, $"{prefix}.nameID", actor.NameID);
        StoreFabric(observation, $"{prefix}.type", actor.Type);
        StoreFabric(observation, $"{prefix}.class", actor.Class);
        StoreFabric(observation, $"{prefix}.role", actor.Role);
        StoreFabric(observation, $"{prefix}.classCategory", actor.ClassCategory);
        StoreFabric(observation, $"{prefix}.level", actor.Level);
        StoreFabric(observation, $"{prefix}.position.x", actor.PosRot.X);
        StoreFabric(observation, $"{prefix}.position.y", actor.PosRot.Y);
        StoreFabric(observation, $"{prefix}.position.z", actor.PosRot.Z);
        StoreFabric(observation, $"{prefix}.rotation", actor.PosRot.W);
        StoreFabric(observation, $"{prefix}.previousPosition.x", actor.PrevPosRot.X);
        StoreFabric(observation, $"{prefix}.previousPosition.y", actor.PrevPosRot.Y);
        StoreFabric(observation, $"{prefix}.previousPosition.z", actor.PrevPosRot.Z);
        StoreFabric(observation, $"{prefix}.previousRotation", actor.PrevPosRot.W);
        StoreFabric(observation, $"{prefix}.hitboxRadius", actor.HitboxRadius);
        StoreFabric(observation, $"{prefix}.hp.current", actor.HPMP.CurHP);
        StoreFabric(observation, $"{prefix}.hp.maximum", actor.HPMP.MaxHP);
        StoreFabric(observation, $"{prefix}.hp.shield", actor.HPMP.Shield);
        StoreFabric(observation, $"{prefix}.mp.current", actor.HPMP.CurMP);
        StoreFabric(observation, $"{prefix}.mp.maximum", actor.HPMP.MaxMP);
        StoreFabric(observation, $"{prefix}.isDestroyed", actor.IsDestroyed);
        StoreFabric(observation, $"{prefix}.isTargetable", actor.IsTargetable);
        StoreFabric(observation, $"{prefix}.isAlly", actor.IsAlly);
        StoreFabric(observation, $"{prefix}.visibility", actor.Visibility);
        StoreFabric(observation, $"{prefix}.isDead", actor.IsDead);
        StoreFabric(observation, $"{prefix}.inCombat", actor.InCombat);
        StoreFabric(observation, $"{prefix}.aggroPlayer", actor.AggroPlayer);
        StoreFabric(observation, $"{prefix}.isOpenTreasure", actor.IsOpenTreasure);
        StoreFabric(observation, $"{prefix}.modelState.model", actor.ModelState.ModelState);
        StoreFabric(observation, $"{prefix}.modelState.animation1", actor.ModelState.AnimState1);
        StoreFabric(observation, $"{prefix}.modelState.animation2", actor.ModelState.AnimState2);
        StoreFabric(observation, $"{prefix}.foray.level", actor.ForayInfo.Level);
        StoreFabric(observation, $"{prefix}.foray.element", actor.ForayInfo.Element);
        StoreFabric(observation, $"{prefix}.eventState", actor.EventState);
        StoreFabric(observation, $"{prefix}.ownerID", actor.OwnerID);
        StoreFabric(observation, $"{prefix}.targetID", actor.TargetID);
        StoreFabric(observation, $"{prefix}.mountID", actor.MountId);
        StoreFabric(observation, $"{prefix}.renderFlags", actor.Renderflags);
        StoreFabric(observation, $"{prefix}.omnidirectional", actor.Omnidirectional);
        StoreFabric(observation, $"{prefix}.tether.id", actor.Tether.ID);
        StoreFabric(observation, $"{prefix}.tether.target", actor.Tether.Target);

        if (actor.CastInfo is { } cast)
        {
            StoreFabric(observation, $"{prefix}.cast.actionType", cast.Action.Type);
            StoreFabric(observation, $"{prefix}.cast.actionID", cast.Action.ID);
            StoreFabric(observation, $"{prefix}.cast.targetID", cast.TargetID);
            StoreFabric(observation, $"{prefix}.cast.rotation", cast.Rotation.Rad);
            StoreFabric(observation, $"{prefix}.cast.location.x", cast.Location.X);
            StoreFabric(observation, $"{prefix}.cast.location.y", cast.Location.Y);
            StoreFabric(observation, $"{prefix}.cast.location.z", cast.Location.Z);
            StoreFabric(observation, $"{prefix}.cast.elapsed", cast.ElapsedTime);
            StoreFabric(observation, $"{prefix}.cast.total", cast.TotalTime);
            StoreFabric(observation, $"{prefix}.cast.interruptible", cast.Interruptible);
            StoreFabric(observation, $"{prefix}.cast.eventHappened", cast.EventHappened);
        }
        else
        {
            StoreFabric(observation, $"{prefix}.cast.active", false);
        }
    }

    private void EnrichActorCollections(ForetellObservation observation, Actor actor)
    {
        const string prefix = "actor";
        StoreFabric(observation, $"{prefix}.statuses.capacity", actor.Statuses.Length);
        var activeStatuses = 0;
        for (var i = 0; i < actor.Statuses.Length; ++i)
        {
            ref var status = ref actor.Statuses[i];
            if (status.ID == 0)
                continue;
            var p = $"{prefix}.statuses[{i}]";
            StoreFabric(observation, $"{p}.id", status.ID);
            StoreFabric(observation, $"{p}.extra", status.Extra);
            StoreFabric(observation, $"{p}.expires", status.ExpireAt);
            StoreFabric(observation, $"{p}.sourceID", status.SourceID);
            ++activeStatuses;
        }
        StoreFabric(observation, $"{prefix}.statuses.activeCount", activeStatuses);

        var activeIncoming = 0;
        for (var i = 0; i < actor.IncomingEffects.Length; ++i)
        {
            ref var incoming = ref actor.IncomingEffects[i];
            if (incoming.GlobalSequence == 0)
                continue;
            var p = $"{prefix}.incomingEffects[{i}]";
            StoreFabric(observation, $"{p}.globalSequence", incoming.GlobalSequence);
            StoreFabric(observation, $"{p}.targetIndex", incoming.TargetIndex);
            StoreFabric(observation, $"{p}.sourceInstanceID", incoming.SourceInstanceID);
            StoreFabric(observation, $"{p}.actionType", incoming.Action.Type);
            StoreFabric(observation, $"{p}.actionID", incoming.Action.ID);
            for (var e = 0; e < ActionEffects.MaxCount; ++e)
                StoreFabric(observation, $"{p}.raw[{e}]", incoming.Effects[e]);
            ++activeIncoming;
        }
        StoreFabric(observation, $"{prefix}.incomingEffects.capacity", actor.IncomingEffects.Length);
        StoreFabric(observation, $"{prefix}.incomingEffects.activeCount", activeIncoming);

        StorePendingDeltas(observation, $"{prefix}.pendingHP", actor.PendingHPDifferences);
        StorePendingDeltas(observation, $"{prefix}.pendingMP", actor.PendingMPDifferences);
        StoreFabric(observation, $"{prefix}.pendingStatuses.count", actor.PendingStatuses.Count);
        for (var i = 0; i < actor.PendingStatuses.Count; ++i)
        {
            var item = actor.PendingStatuses[i];
            StorePendingEffect(observation, $"{prefix}.pendingStatuses[{i}].effect", item.Effect);
            StoreFabric(observation, $"{prefix}.pendingStatuses[{i}].statusID", item.StatusId);
            StoreFabric(observation, $"{prefix}.pendingStatuses[{i}].extraLow", item.ExtraLo);
        }
        StoreFabric(observation, $"{prefix}.pendingDispels.count", actor.PendingDispels.Count);
        for (var i = 0; i < actor.PendingDispels.Count; ++i)
        {
            var item = actor.PendingDispels[i];
            StorePendingEffect(observation, $"{prefix}.pendingDispels[{i}].effect", item.Effect);
            StoreFabric(observation, $"{prefix}.pendingDispels[{i}].statusID", item.StatusId);
        }
        StoreFabric(observation, $"{prefix}.pendingKnockbacks.count", actor.PendingKnockbacks.Count);
        for (var i = 0; i < actor.PendingKnockbacks.Count; ++i)
            StorePendingEffect(observation, $"{prefix}.pendingKnockbacks[{i}]", actor.PendingKnockbacks[i]);
    }

    private void StorePendingDeltas(ForetellObservation observation, string prefix, List<PendingEffectDelta> values)
    {
        StoreFabric(observation, $"{prefix}.count", values.Count);
        for (var i = 0; i < values.Count; ++i)
        {
            StorePendingEffect(observation, $"{prefix}[{i}].effect", values[i].Effect);
            StoreFabric(observation, $"{prefix}[{i}].value", values[i].Value);
        }
    }

    private void StorePendingEffect(ForetellObservation observation, string prefix, PendingEffect effect)
    {
        StoreFabric(observation, $"{prefix}.globalSequence", effect.GlobalSequence);
        StoreFabric(observation, $"{prefix}.targetIndex", effect.TargetIndex);
        StoreFabric(observation, $"{prefix}.sourceInstanceID", effect.SourceInstanceID);
        StoreFabric(observation, $"{prefix}.expiration", effect.Expiration);
        StoreFabric(observation, $"{prefix}.requiresEffectResult", effect.RequiresEffectResult);
    }

    private void StoreFabric<T>(ForetellObservation observation, string key, T value) where T : notnull
        => TryStoreScalar(value, value.GetType(), key, observation);

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
        if (value == null)
            return;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var budget = MaxFabricEntriesPerRoot;
        var deadline = Stopwatch.GetTimestamp() + Math.Max(1, (long)(Stopwatch.Frequency * MaxFabricTraversalMilliseconds / 1000));
        var deferred = false;
        FlattenObject(value, prefix, observation, 0, maxDepth, visited, ref budget, deadline, ref deferred);
        if (deferred)
        {
            ++_fabricDeferredTraversals;
            RegisterCapability($"{prefix}.__deferred", value.GetType(), prefix, false, false,
                $"live traversal yielded after {MaxFabricTraversalMilliseconds:F1} ms; the rolling scanner will revisit this source");
        }
    }

    private void FlattenObject(object? value, string path, ForetellObservation observation, int depth, int maxDepth,
        HashSet<object> visited, ref int budget, long deadline, ref bool deferred)
    {
        if (value == null || budget <= 0)
            return;
        if (Stopwatch.GetTimestamp() >= deadline)
        {
            deferred = true;
            return;
        }

        // The safety budget counts every visited node, not only scalar leaves. Complex collections therefore cannot
        // bypass the guard by yielding thousands of non-scalar/depth-limited objects.
        --budget;
        var type = value.GetType();
        if (TryStoreScalar(value, type, path, observation))
            return;
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
            var offset = _fabricCollectionOffsets.GetValueOrDefault(path);
            var index = 0;
            var sampled = 0;
            var reachedEnd = true;
            foreach (var item in enumerable)
            {
                if (Stopwatch.GetTimestamp() >= deadline || budget <= 0)
                {
                    deferred = Stopwatch.GetTimestamp() >= deadline;
                    reachedEnd = false;
                    break;
                }
                if (index++ < offset)
                    continue;
                FlattenObject(item, $"{path}[{index - 1}]", observation, depth + 1, maxDepth, visited, ref budget, deadline, ref deferred);
                ++sampled;
                if (deferred)
                {
                    reachedEnd = false;
                    break;
                }
            }
            _fabricCollectionOffsets[path] = reachedEnd || sampled == 0 ? 0 : offset + sampled;
            observation.Numeric[$"{path}.__scanOffset"] = offset;
            observation.Numeric[$"{path}.__sampledCount"] = sampled;
            observation.Numeric[$"{path}.__complete"] = reachedEnd ? 1 : 0;
            RegisterCapability($"{path}.__sampledCount", type, "IEnumerable", true, false,
                reachedEnd ? "complete collection sweep" : "time-sliced collection sweep; continuation retained");
            if (budget <= 0)
                RegisterCapability($"{path}.__truncated", type, "IEnumerable", false, false, "root node safety budget exhausted");
            return; // collection members are the evidence; reflecting collection implementation duplicates plumbing
        }

        foreach (var p in FabricProperties(type))
        {
            if (budget <= 0)
                break;
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                deferred = true;
                break;
            }
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

            var memberKey = $"{type.AssemblyQualifiedName}|P|{p.Name}";
            if (_slowFabricMembers.Contains(memberKey))
            {
                RegisterCapability(memberPath, type, p.Name, false, false, "slow live getter quarantined; dedicated typed ingestion required");
                continue;
            }
            try
            {
                var getterStarted = Stopwatch.GetTimestamp();
                var memberValue = p.GetValue(value);
                var getterMilliseconds = Stopwatch.GetElapsedTime(getterStarted).TotalMilliseconds;
                if (getterMilliseconds > SlowFabricGetterMilliseconds)
                {
                    _slowFabricMembers.Add(memberKey);
                    ++_fabricQuarantinedGetters;
                    RegisterCapability(memberPath, type, p.Name, false, false,
                        $"live getter quarantined after {getterMilliseconds:F2} ms; dedicated typed ingestion required");
                    Service.Log($"[Foretell] Quarantined slow Data Fabric getter {type.FullName}.{p.Name} ({getterMilliseconds:F2} ms)");
                    if (memberValue != null)
                        TryStoreScalar(memberValue, memberValue.GetType(), memberPath, observation);
                    continue;
                }
                FlattenObject(memberValue, memberPath, observation, depth + 1, maxDepth, visited, ref budget, deadline, ref deferred);
            }
            catch (Exception e)
            {
                RegisterCapability(memberPath, type, p.Name, false, false, $"getter rejected: {e.GetType().Name}");
            }
        }

        foreach (var f in FabricFields(type))
        {
            if (budget <= 0)
                break;
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                deferred = true;
                break;
            }
            if (f.IsStatic)
                continue;
            var memberPath = $"{path}.{f.Name}";
            if (RejectNonBoxableMember(f.FieldType, memberPath, type, f.Name))
                continue;
            try
            {
                FlattenObject(f.GetValue(value), memberPath, observation, depth + 1, maxDepth, visited, ref budget, deadline, ref deferred);
            }
            catch (Exception e)
            {
                RegisterCapability(memberPath, type, f.Name, false, false, $"field rejected: {e.GetType().Name}");
            }
        }
    }

    private PropertyInfo[] FabricProperties(Type type)
    {
        if (!_fabricPropertyCache.TryGetValue(type, out var result))
            _fabricPropertyCache[type] = result = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).OrderBy(p => p.Name).ToArray();
        return result;
    }

    private FieldInfo[] FabricFields(Type type)
    {
        if (!_fabricFieldCache.TryGetValue(type, out var result))
            _fabricFieldCache[type] = result = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).OrderBy(f => f.Name).ToArray();
        return result;
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
