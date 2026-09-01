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
        ApplyActionMetadataPrior(obs);
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
        ProcessObservation(Observation(ObservationKind.ActionResolved, actor, action, value1: targets.Count));
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
        ProcessObservation(obs);
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
        ProcessObservation(obs);
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
            ProcessObservation(Observation(ObservationKind.ActionTimelineSync, actor));
            return;
        }
        foreach (var ev in events.Take(32))
            ProcessObservation(Observation(ObservationKind.ActionTimelineSync, actor, ev.Item2, detail: ev.Item1.ToString("X")));
    }

    private void OnNpcYell(Actor actor, ushort id)
        => ProcessObservation(Observation(ObservationKind.NpcYell, actor, id));

    private void OnMapEffect(WorldState.OpMapEffect op)
        => ProcessObservation(Observation(ObservationKind.MapEffect, primary: ToUInt(op.Index) ?? 0, secondary: ToUInt(op.State) ?? 0));

    private void OnLegacyMapEffect(WorldState.OpLegacyMapEffect op)
        => ProcessObservation(Observation(ObservationKind.LegacyMapEffect,
            primary: ToUInt(op.Sequence) ?? 0,
            secondary: ToUInt(op.Param) ?? 0,
            value1: ToUInt(op.Data) ?? 0));

    private void OnDirectorUpdate(WorldState.OpDirectorUpdate op)
        => ProcessObservation(Observation(ObservationKind.DirectorUpdate,
            primary: ToUInt(op.UpdateID) ?? 0,
            secondary: ToUInt(op.Param1) ?? 0,
            value1: ToUInt(op.Param2) ?? 0,
            value2: ToUInt(op.Param3) ?? 0,
            detail: (ToUInt(op.Param4) ?? 0).ToString("X")));

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