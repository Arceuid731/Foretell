using System.Collections;
using System.Reflection;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private readonly HashSet<Type> _substitutedWorldOperations = [];
    private readonly Dictionary<(uint Source, uint Command), ActorControlGate> _actorControlGates = [];
    private readonly record struct ActorControlGate(ulong Fingerprint, DateTime At);

    private void OnActorAdded(Actor actor)
    {
        PrioritizeNativeActor(actor.InstanceID);
        ProcessObservation(Observation(ObservationKind.ActorAdded, actor, detail: actor.Type.ToString()));
    }

    private void OnActorRemoved(Actor actor)
        => ProcessObservation(Observation(ObservationKind.ActorRemoved, actor, detail: actor.Type.ToString()));

    private void OnCastStarted(Actor actor)
    {
        PrioritizeNativeActor(actor.InstanceID);
        var spell = actor.CastInfo;
        if (spell == null || !spell.IsSpell()) return;
        var castSeconds = (float)Math.Max(0, spell.NPCRemainingTime);
        var obs = Observation(ObservationKind.CastStart, actor, spell.Action.ID, value1: castSeconds);
        obs.TargetX = spell.LocXZ.X;
        obs.TargetZ = spell.LocXZ.Z;
        obs.Rotation = spell.Rotation.Rad;
        ProcessObservation(obs);
    }

    private void OnCastFinished(Actor actor)
    {
        var spell = actor.CastInfo;
        if (spell == null || !spell.IsSpell()) return;
        ProcessObservation(Observation(ObservationKind.CastFinish, actor, spell.Action.ID));
    }

    private void OnCastEvent(Actor actor, ActorCastEvent ev)
    {
        var action = ev.Action.ID;
        if (action == 0) return;
        var resolved = Observation(ObservationKind.ActionResolved, actor, action, value1: ev.Targets.Count);
        resolved.Numeric["action.globalSequence"] = ev.GlobalSequence;
        resolved.Numeric["action.sourceSequence"] = ev.SourceSequence;
        resolved.Numeric["action.maxTargets"] = ev.MaxTargets;
        resolved.Numeric["action.animationLock"] = ev.AnimationLockTime;
        resolved.Numeric["action.targetY"] = ev.TargetPos.Y;
        ProcessRichObservation(resolved, ev);

        var seen = new HashSet<ulong>();
        foreach (var target in ev.Targets)
        {
            seen.Add(target.ID);
            var affected = Observation(ObservationKind.AffectedTarget, actor, action, target: target.ID);
            affected.Numeric["action.globalSequence"] = ev.GlobalSequence;
            var effects = target.Effects.ValidEffects();
            affected.Numeric["actionEffect.count"] = effects.Length;
            for (var i = 0; i < effects.Length; ++i)
            {
                ref readonly var effect = ref effects[i];
                var prefix = $"actionEffect.{i}";
                affected.Numeric[$"{prefix}.type"] = (byte)effect.Type;
                affected.Numeric[$"{prefix}.param0"] = effect.Param0;
                affected.Numeric[$"{prefix}.param1"] = effect.Param1;
                affected.Numeric[$"{prefix}.param2"] = effect.Param2;
                affected.Numeric[$"{prefix}.param3"] = effect.Param3;
                affected.Numeric[$"{prefix}.param4"] = effect.Param4;
                affected.Numeric[$"{prefix}.value"] = effect.Value;
                affected.Numeric[$"{prefix}.fromTarget"] = effect.FromTarget ? 1 : 0;
                affected.Numeric[$"{prefix}.atSource"] = effect.AtSource ? 1 : 0;
                affected.Numeric[$"{prefix}.damageType"] = (int)effect.DamageType;
                affected.Numeric[$"{prefix}.damageElement"] = (int)effect.DamageElement;
                affected.Numeric[$"{prefix}.damageHealValue"] = effect.DamageHealValue;
                affected.Text[$"{prefix}.typeName"] = effect.Type.ToString();
                affected.Binary[$"{prefix}.raw"] =
                [
                    (byte)effect.Type, effect.Param0, effect.Param1, effect.Param2, effect.Param3, effect.Param4,
                    (byte)(effect.Value & 0xFF), (byte)(effect.Value >> 8)
                ];
            }
            ProcessObservation(affected);
        }

        if (ev.MainTargetID != 0 && !seen.Contains(ev.MainTargetID))
        {
            var affected = Observation(ObservationKind.AffectedTarget, actor, action, target: ev.MainTargetID, detail: "main-target-only");
            affected.Numeric["action.globalSequence"] = ev.GlobalSequence;
            ProcessObservation(affected);
        }
    }

    private void OnEffectResult(Actor target, uint sequence, int targetIndex)
    {
        var obs = Observation(ObservationKind.EffectResult, primary: sequence, secondary: (uint)Math.Max(0, targetIndex), target: target.InstanceID, detail: "effect-result");
        obs.Numeric["effectResult.sequence"] = sequence;
        obs.Numeric["effectResult.targetIndex"] = targetIndex;
        ProcessObservation(obs);
    }

    private void OnTargetableChanged(Actor actor)
        => ProcessObservation(Observation(ObservationKind.TargetableChanged, actor, flag: actor.IsTargetable));

    private void OnDeathChanged(Actor actor)
        => ProcessObservation(Observation(ObservationKind.DeathChanged, actor, flag: actor.IsDead));

    private void OnRenderFlagsChanged(Actor actor)
        => ProcessObservation(Observation(ObservationKind.RenderFlagsChanged, actor, detail: actor.Renderflags.ToString()));

    private void OnEventStateChanged(Actor actor)
        => ProcessObservation(Observation(ObservationKind.EventStateChanged, actor, primary: ToUInt(actor.EventState) ?? 0));

    private void OnModelStateChanged(Actor actor)
    {
        ref readonly var state = ref actor.ModelState;
        ProcessObservation(Observation(ObservationKind.ModelStateChanged, actor,
            primary: ToUInt(state.ModelState) ?? 0,
            secondary: ToUInt(state.AnimState1) ?? 0,
            value1: ToUInt(state.AnimState2) ?? 0));
    }

    private void OnTether(Actor actor)
    {
        PrioritizeNativeActor(actor.InstanceID);
        var target = ToULong(Member(actor.Tether, "Target")) ?? ToULong(Member(actor.Tether, "TargetID")) ?? 0;
        PrioritizeNativeActor(target);
        ProcessObservation(Observation(ObservationKind.TetherStart, actor, actor.Tether.ID, target: target));
    }

    private void OnUntether(Actor actor)
    {
        var target = ToULong(Member(actor.Tether, "Target")) ?? ToULong(Member(actor.Tether, "TargetID")) ?? 0;
        ProcessObservation(Observation(ObservationKind.TetherEnd, actor, actor.Tether.ID, target: target));
    }

    private void OnStatusGain(Actor affected, int index)
    {
        ref var status = ref affected.Statuses[index];
        var source = _ws.Actors.Find(status.SourceID);
        var obs = Observation(ObservationKind.StatusGain, source, status.ID, target: affected.InstanceID);
        if (source == null)
        {
            obs.ActorID = status.SourceID;
            obs.SourceKind = SourceKind.Unknown;
            obs.TargetX = affected.Position.X;
            obs.TargetZ = affected.Position.Z;
        }
        ProcessRichObservation(obs, status);
    }

    private void OnStatusLose(Actor affected, int index)
    {
        ref var status = ref affected.Statuses[index];
        var source = _ws.Actors.Find(status.SourceID);
        var obs = Observation(ObservationKind.StatusLose, source, status.ID, target: affected.InstanceID);
        if (source == null)
        {
            obs.ActorID = status.SourceID;
            obs.SourceKind = SourceKind.Unknown;
            obs.TargetX = affected.Position.X;
            obs.TargetZ = affected.Position.Z;
        }
        ProcessRichObservation(obs, status);
    }

    private void OnIcon(Actor actor, uint icon, ulong target)
        => ProcessObservation(Observation(ObservationKind.Icon, actor, icon, target: target));

    private void OnVFX(Actor actor, uint vfx, ulong target)
        => ProcessObservation(Observation(ObservationKind.VFX, actor, vfx, target: target));

    private void OnEventObjectState(Actor actor, ushort state)
        => ProcessObservation(Observation(ObservationKind.EventObjectState, actor, state));

    private void OnEventObjectAnimation(Actor actor, ushort p1, ushort p2)
        => ProcessObservation(Observation(ObservationKind.EventObjectAnimation, actor, p1, p2));

    private void OnActionTimelineEvent(Actor actor, ushort id)
        => ProcessObservation(Observation(ObservationKind.ActionTimelineEvent, actor, id));

    private void OnActionTimelineSync(Actor actor, List<(ulong, ushort)> events)
    {
        if (events.Count == 0)
        {
            ProcessRichObservation(Observation(ObservationKind.ActionTimelineSync, actor), events);
            return;
        }
        foreach (var ev in events)
            ProcessRichObservation(Observation(ObservationKind.ActionTimelineSync, actor, ev.Item2, detail: ev.Item1.ToString("X")), events);
    }

    private void OnNpcYell(Actor actor, ushort id)
        => ProcessObservation(Observation(ObservationKind.NpcYell, actor, id));

    private void OnMapEffect(WorldState.OpMapEffect op)
    {
        InvalidateTopology();
        ProcessRichObservation(Observation(ObservationKind.MapEffect, primary: ToUInt(op.Index) ?? 0, secondary: ToUInt(op.State) ?? 0), op);
    }

    private void OnLegacyMapEffect(WorldState.OpLegacyMapEffect op)
    {
        InvalidateTopology();
        ProcessRichObservation(Observation(ObservationKind.LegacyMapEffect,
            primary: ToUInt(op.Sequence) ?? 0,
            secondary: ToUInt(op.Param) ?? 0,
            value1: ToUInt(op.Data) ?? 0), op);
    }

    private void OnDirectorUpdate(WorldState.OpDirectorUpdate op)
    {
        InvalidateTopology();
        ProcessRichObservation(Observation(ObservationKind.DirectorUpdate,
            primary: ToUInt(op.UpdateID) ?? 0,
            secondary: ToUInt(op.Param1) ?? 0,
            value1: ToUInt(op.Param2) ?? 0,
            value2: ToUInt(op.Param3) ?? 0,
            detail: (ToUInt(op.Param4) ?? 0).ToString("X")), op);
    }

    private void OnWorldOperation(WorldState.Operation op)
    {
        // FrameStart is deliberately represented by the sampled runtime fabric: recording it at render frequency
        // would multiply replay size/CPU without adding a distinct state transition. This substitution is audited.
        if (op is WorldState.OpFrameStart)
        {
            RegisterCapability("worldop.FrameStart", op.GetType(), "FrameStart", false, true, "represented by sampled runtime.worldState/frame/gauge/camera context");
            return;
        }
        // If BMR packet recording is also enabled, don't duplicate the independent Foretell raw transport tap.
        if (op is NetworkState.OpServerIPC)
        {
            RegisterCapability("worldop.ServerIPC", op.GetType(), "ServerIPC", false, true, "duplicate of Foretell unconditional raw server IPC tap");
            return;
        }
        if (op is ActorState.OpEffectResult)
        {
            RegisterCapability("worldop.EffectResult", op.GetType(), "EffectResult", false, true, "duplicate of Foretell semantic EffectResult stream");
            return;
        }

        // These values either update at render frequency or already have a lossless semantic/sampled Foretell
        // representation. Expanding the operation and its actor here as well used to allocate hundreds of fields
        // per actor movement, turning busy open-world zones into thousands of duplicate observations per minute.
        if (WorldOperationSubstitution(op) is { } substitution)
        {
            if (_substitutedWorldOperations.Add(op.GetType()))
                RegisterCapability($"worldop.{op.GetType().Name}", op.GetType(), op.GetType().Name, false, true, substitution);
            return;
        }

        var actorID = ToULong(Member(op, "InstanceID")) ?? ToULong(Member(op, "ActorID")) ?? ToULong(Member(op, "SourceID")) ?? ToULong(Member(op, "CasterID")) ?? 0;
        var actor = actorID != 0 ? _ws.Actors.Find(actorID) : null;
        var typeName = op.GetType().FullName ?? op.GetType().Name;
        var obs = Observation(ObservationKind.WorldOperation, actor, StableHash(typeName), detail: typeName);
        if (actor == null && actorID != 0)
        {
            obs.ActorID = actorID;
            obs.SourceKind = SourceKind.Unknown;
        }
        ProcessRichObservation(obs, op);
    }

    private static string? WorldOperationSubstitution(WorldState.Operation op) => op switch
    {
        ActorState.OpMove => "represented by 4 Hz PositionSample plus rotating generic/native actor snapshots",
        ActorState.OpCreate or ActorState.OpDestroy => "duplicate of the semantic actor lifecycle stream",
        ActorState.OpTargetable or ActorState.OpDead or ActorState.OpRenderflags or ActorState.OpEventState or ActorState.OpModelState => "duplicate of a semantic actor-state stream",
        ActorState.OpTether or ActorState.OpCastInfo or ActorState.OpCastEvent or ActorState.OpStatus or ActorState.OpIcon or ActorState.OpVFX => "duplicate of a lossless semantic combat stream",
        ActorState.OpEventObjectStateChange or ActorState.OpEventObjectAnimation or ActorState.OpPlayActionTimelineEvent or ActorState.OpPlayActionTimelineSync or ActorState.OpEventNpcYell => "duplicate of a semantic encounter-signal stream",
        ClientState.OpActiveCompanionChange or ClientState.OpActivePetChange or ClientState.OpActiveFateChange => "represented by the sampled runtime.client root; continuous timers are not discrete events",
        ClientState.OpAnimationLockChange or ClientState.OpComboChange or ClientState.OpCooldown or ClientState.OpProcTimersChange => "represented by typed action/effect events and the sampled runtime.client root",
        ClientState.OpHateChange => "represented by typed enmity state in the sampled runtime.client root",
        ClientState.OpForcedMovementDirectionChange => "represented by sampled movement and runtime.client state",
        _ => null
    };

    private void OnSystemLog(WorldState.OpSystemLogMessage op)
    {
        var obs = Observation(ObservationKind.SystemLog, primary: op.MessageID);
        obs.Numeric["systemLog.argCount"] = op.Args.Length;
        for (var i = 0; i < op.Args.Length; ++i)
            obs.Numeric[$"systemLog.arg.{i}"] = op.Args[i];
        ProcessRichObservation(obs, op);
    }

    private void OnRawServerIPC(NetworkState.RawServerIPC packet)
    {
        // Every payload enters the compact lossless journal. It deliberately does not re-enter semantic enrichment:
        // decoded WorldState events already feed the online learner, while raw bytes remain available for offline
        // feature discovery and future decoders without multiplying per-packet work on the framework thread.
        var context = _rawCaptureContext;
        if (context.Path.Length != 0)
            _raw.EnqueueServer(context.Path, context.TerritoryID, packet);
    }

    private void OnRawClientIPC(NetworkState.RawClientIPC packet)
    {
        var context = _rawCaptureContext;
        if (context.Path.Length != 0)
            _raw.EnqueueClient(context.Path, context.TerritoryID, packet);
    }

    private void OnRawActorControlCapture(NetworkState.RawActorControl control)
    {
        var context = _rawCaptureContext;
        if (context.Path.Length != 0)
            _raw.EnqueueActorControl(context.Path, context.TerritoryID, DateTime.UtcNow, control);
    }

    private void OnRawActorControl(NetworkState.RawActorControl control)
    {
        var captureAt = ObservationNow();

        // Preserve every command in the optional raw stream, but admit at most one semantic update per command
        // and source every 250 ms. This prevents self telemetry from monopolizing the learner in open-world zones.
        var now = captureAt;
        var key = (control.SourceID, control.Command);
        var fingerprint = ActorControlFingerprint(control);
        if (_actorControlGates.TryGetValue(key, out var previous))
        {
            var age = (now - previous.At).TotalMilliseconds;
            if (age < 250 || previous.Fingerprint == fingerprint && age < 2000)
                return;
        }
        _actorControlGates[key] = new(fingerprint, now);
        if (_actorControlGates.Count > 512)
        {
            foreach (var stale in _actorControlGates.Where(kv => (now - kv.Value.At).TotalSeconds > 10).Select(kv => kv.Key).ToArray())
                _actorControlGates.Remove(stale);
        }

        var actor = control.SourceID != 0 ? _ws.Actors.Find(control.SourceID) : null;
        var obs = Observation(ObservationKind.ActorControlRaw, actor, control.Command, target: control.TargetID, flag: control.Replaying != 0);
        if (actor == null && control.SourceID != 0)
        {
            obs.ActorID = control.SourceID;
            obs.SourceKind = SourceKind.Unknown;
        }
        StoreActorControlFields(obs, control);
        ProcessObservation(obs, enriched: true);
    }

    private void SamplePartyPositions()
    {
        for (var slot = 0; slot < PartyState.MaxAllies; ++slot)
        {
            var player = _ws.Party[slot];
            if (player == null || player.IsDead)
                continue;
            var category = player.ClassCategory;
            var obs = Observation(ObservationKind.PositionSample, player, secondary: (uint)category, detail: category.ToString());
            ProcessObservation(obs, enriched: true);
        }
    }

    private static void StoreActorControlFields(ForetellObservation obs, NetworkState.RawActorControl control)
    {
        obs.Numeric["actorControl.p1"] = control.P1; obs.Numeric["actorControl.p2"] = control.P2;
        obs.Numeric["actorControl.p3"] = control.P3; obs.Numeric["actorControl.p4"] = control.P4;
        obs.Numeric["actorControl.p5"] = control.P5; obs.Numeric["actorControl.p6"] = control.P6;
        obs.Numeric["actorControl.p7"] = control.P7; obs.Numeric["actorControl.p8"] = control.P8;
        obs.Numeric["actorControl.replaying"] = control.Replaying;
    }

    private static ulong ActorControlFingerprint(NetworkState.RawActorControl control)
    {
        var hash = 14695981039346656037UL;
        static void Add(ref ulong hash, ulong value)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
        Add(ref hash, control.P1); Add(ref hash, control.P2); Add(ref hash, control.P3); Add(ref hash, control.P4);
        Add(ref hash, control.P5); Add(ref hash, control.P6); Add(ref hash, control.P7); Add(ref hash, control.P8);
        Add(ref hash, control.TargetID); Add(ref hash, control.Replaying);
        return hash;
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
        {
            foreach (var target in targets)
            {
                if (target == null) continue;
                var id = ToULong(Member(target, "ID")) ?? ToULong(Member(target, "TargetID")) ?? ToULong(Member(target, "InstanceID"));
                if (id is > 0) ids.Add(id.Value);
            }
        }
        var main = ToULong(Member(ev, "MainTargetID")) ?? ToULong(Member(ev, "TargetID"));
        if (main is > 0) ids.Add(main.Value);
        return ids;
    }
}
