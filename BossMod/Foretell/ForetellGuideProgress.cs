namespace BossMod.Foretell;

internal sealed class GuideEncounterProgress
{
    private readonly Dictionary<(ulong ID, uint OID, uint NameID), GuideActorState> _actors = [];

    public void Reset() => _actors.Clear();

    public void Wipe()
    {
        foreach (var entry in _actors.Where(entry => !entry.Value.Dead).ToArray()) _actors.Remove(entry.Key);
    }

    public void Observe(IEnumerable<GuideActorState> actors)
    {
        foreach (var actor in actors)
        {
            if (actor.ID == 0 || actor.OID == 0 || actor.NameID == 0 || actor.EnglishName.Length == 0) continue;
            var key = (actor.ID, actor.OID, actor.NameID);
            var known = _actors.ContainsKey(key);
            if (!known && (!actor.Engaged || actor.Dead)) continue;
            if (_actors.Count < 2048 || known) _actors[key] = actor;
        }
    }

    public void ObserveDeath(ulong actorID, uint oid, uint nameID)
    {
        var key = (actorID, oid, nameID);
        if (_actors.TryGetValue(key, out var actor))
            _actors[key] = actor with { Dead = true, Engaged = false };
    }

    public IEnumerable<string> Completed(GuideDocument document)
    {
        foreach (var group in _actors.Values.GroupBy(actor => GuideNames.Boss(actor.EnglishName)))
        {
            if (!group.All(actor => actor.Dead)) continue;
            var bosses = document.Bosses.Where(boss => GuideNames.Boss(boss.Name) == group.Key).ToArray();
            if (bosses.Length == 1) yield return bosses[0].Name;
        }
    }
}
