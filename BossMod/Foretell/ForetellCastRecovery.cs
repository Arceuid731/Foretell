namespace BossMod.Foretell;

internal static class ForetellCastRecovery
{
    internal static void Recover(IEnumerable<Actor> actors, Dictionary<ulong, ActorCastInfo> accepted,
        Func<bool> budgetAvailable, Func<Actor, bool> observe)
    {
        foreach (var actor in actors)
        {
            if (actor.Type is not (ActorType.Enemy or ActorType.Helper or ActorType.Part) || actor.IsAlly
                || actor.CastInfo is not { } cast || cast.EventHappened || !cast.IsSpell() || cast.NPCRemainingTime <= .15f
                || accepted.GetValueOrDefault(actor.InstanceID) == cast) continue;
            if (!budgetAvailable()) break;
            if (observe(actor)) accepted[actor.InstanceID] = cast;
        }
    }
}
