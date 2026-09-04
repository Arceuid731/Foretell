using System.Numerics;

namespace BossMod.Foretell;

internal sealed record DynamicTerrainWarning(ulong ActorID, Vector2 Center, float OuterRadius, List<Vector2> Points,
    float ReferenceY, DateTime Expires, int Signals);

public sealed partial class ForetellEngine
{
    private readonly Dictionary<ulong, DynamicTerrainWarning> _dynamicTerrainWarnings = [];

    private void ObserveDynamicTerrainAnimation(Actor actor)
    {
        InvalidateTopology(immediate: true);
        if (!Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat]
            || actor.Type != ActorType.EventObj || actor.IsDestroyed)
            return;
        var position = V(actor.Position);
        if (!FiniteVector(position) || !TryDynamicTerrainCenter(position, out var center))
            return;
        var distance = Vector2.Distance(position, center);
        var peers = _ws.Actors.Where(candidate => candidate.Type == ActorType.EventObj && !candidate.IsDestroyed)
            .Select(candidate => V(candidate.Position)).Where(FiniteVector).ToArray();
        var points = ForetellDynamicTerrainCore.BuildRadialSector(center, position, peers,
            Math.Max(4, actor.HitboxRadius * 2.5f), out _);
        if (points.Count == 0)
            return;
        var now = ObservationNow();
        var signals = _dynamicTerrainWarnings.GetValueOrDefault(actor.InstanceID)?.Signals + 1 ?? 1;
        // A second structural animation for the same radial tile confirms the transition; retain the forbidden
        // sector until combat ends while the collision mesh independently observes the missing floor.
        var expires = signals >= 2 ? DateTime.MaxValue : now.AddSeconds(7);
        _dynamicTerrainWarnings[actor.InstanceID] = new(actor.InstanceID, center, Vector2.Distance(center, points[^1]),
            points, actor.PosRot.Y + .08f, expires, signals);
    }

    private bool TryDynamicTerrainCenter(Vector2 eventObject, out Vector2 center)
    {
        if (CurrentArenaBoundary is { ArenaLike: true } boundary)
        {
            center = boundary.Origin;
            return Vector2.Distance(center, eventObject) is >= 7 and <= 60;
        }
        var player = _ws.Party[PartyState.PlayerSlot];
        var playerHP = player?.HPMP.MaxHP ?? 0;
        var candidate = _ws.Actors.Where(actor => actor.Type == ActorType.Enemy && !actor.IsAlly && !actor.IsDeadOrDestroyed
                && Vector2.DistanceSquared(V(actor.Position), eventObject) <= 80 * 80)
            .Where(actor => actor.HitboxRadius >= 2.5f || playerHP != 0 && actor.HPMP.MaxHP >= playerHP * 1.25f)
            .OrderByDescending(actor => actor.HPMP.MaxHP).ThenByDescending(actor => actor.HitboxRadius).FirstOrDefault();
        center = candidate != null ? V(candidate.Position) : default;
        return candidate != null && FiniteVector(center) && Vector2.Distance(center, eventObject) is >= 7 and <= 60;
    }

    private void ClearDynamicTerrainWarnings() => _dynamicTerrainWarnings.Clear();
}
