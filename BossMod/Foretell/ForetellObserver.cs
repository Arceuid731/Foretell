using System.Collections;
using System.Reflection;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private void OnActorAdded(Actor actor)
        => ProcessObservation(Observation(ObservationKind.ActorAdded, actor, detail: actor.Type.ToString()));

    private void OnActorRemoved(Actor actor)
        => ProcessObservation(Observation(ObservationKind.ActorRemoved, actor, detail: actor.Type.ToString()));

    private void OnCastStarted(Actor actor)
    {
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
        var action = ReadActionID(ev);
        if (action == 0) return;
        var targets = ExtractTargetIDs(ev);
        ProcessRichObservation(Observation(ObservationKind.ActionResolved, actor, action, value1: targets.Count), ev);
        foreach (var target in targets)
            ProcessObservation(Observation(ObservationKind.AffectedTarget, actor, action, target: target));
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
        var target = ToULong(Member(actor.Tether, "Target")) ?? ToULong(Member(actor.Tether, "TargetID")) ?? 0;
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
        foreach (var ev in events.Take(32))
            ProcessRichObservation(Observation(ObservationKind.ActionTimelineSync, actor, ev.Item2, detail: ev.Item1.ToString("X")), events);
    }

    private void OnNpcYell(Actor actor, ushort id)
        => ProcessObservation(Observation(ObservationKind.NpcYell, actor, id));

    private void OnMapEffect(WorldState.OpMapEffect op)
        => ProcessRichObservation(Observation(ObservationKind.MapEffect, primary: ToUInt(op.Index) ?? 0, secondary: ToUInt(op.State) ?? 0), op);

    private void OnLegacyMapEffect(WorldState.OpLegacyMapEffect op)
        => ProcessRichObservation(Observation(ObservationKind.LegacyMapEffect,
            primary: ToUInt(op.Sequence) ?? 0,
            secondary: ToUInt(op.Param) ?? 0,
            value1: ToUInt(op.Data) ?? 0), op);

    private void OnDirectorUpdate(WorldState.OpDirectorUpdate op)
        => ProcessRichObservation(Observation(ObservationKind.DirectorUpdate,
            primary: ToUInt(op.UpdateID) ?? 0,
            secondary: ToUInt(op.Param1) ?? 0,
            value1: ToUInt(op.Param2) ?? 0,
            value2: ToUInt(op.Param3) ?? 0,
            detail: (ToUInt(op.Param4) ?? 0).ToString("X")), op);

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

    private void OnSystemLog(WorldState.OpSystemLogMessage op)
        => ProcessRichObservation(Observation(ObservationKind.SystemLog, primary: op.MessageID), op);

    private void OnRawServerIPC(NetworkState.RawServerIPC packet)
    {
        var actor = packet.SourceServerActor != 0 ? _ws.Actors.Find(packet.SourceServerActor) : null;
        var obs = Observation(ObservationKind.ServerIPC, actor, packet.Opcode, ToUInt(packet.ID) ?? 0, packet.TargetServerActor, detail: packet.ID.ToString());
        if (actor == null && packet.SourceServerActor != 0)
        {
            obs.ActorID = packet.SourceServerActor;
            obs.SourceKind = SourceKind.Unknown;
        }
        ProcessRichObservation(obs, packet);
    }

    private void OnRawClientIPC(NetworkState.RawClientIPC packet)
    {
        var obs = Observation(ObservationKind.ClientIPC, primary: packet.Opcode, detail: "client->server");
        ProcessRichObservation(obs, packet);
    }

    private void SamplePartyPositions()
    {
        foreach (var (_, player) in _ws.Party.WithSlot())
        {
            var role = Member(player, "ClassCategory")?.ToString() ?? "";
            var obs = Observation(ObservationKind.PositionSample, player, secondary: ToUInt(Member(player, "ClassCategory")) ?? 0, detail: role);
            ProcessObservation(obs);
        }
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