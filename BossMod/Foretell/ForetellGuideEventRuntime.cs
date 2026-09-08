namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private GuideBindingMemory? _guideBindings;
    private readonly Queue<GuideBindingAudit> _guideBindingPending = new();
    private readonly Dictionary<string, (DateTime Until, string Result)> _guideBindingSeen = [];
    private GuideDocument? _guideBindingDocument;
    private GuideBoss? _guideBindingBoss;
    private string _guideBindingScope = "";
    private long _guideBindingSequence;
    private long _guideBindingDropped;

    internal static bool GuideEventSource(Actor source, Actor? owner, uint bossNameID)
        => bossNameID != 0 && source.InstanceID != 0 && source.OID != 0 && source.NameID != 0 && !source.IsDeadOrDestroyed && !source.IsAlly
            && source.Type is ActorType.Enemy or ActorType.Helper && (source.OwnerID == 0 ? source.NameID == bossNameID
                : owner is { IsDeadOrDestroyed: false, IsAlly: false, Type: ActorType.Enemy } && owner.InstanceID == source.OwnerID && owner.NameID == bossNameID);

    private void UpdateGuideEventBindings(DateTime now)
    {
        if (_liveGuide == null || _guideEncounter.Frame is not { Boss: { } boss, Upcoming: false, Ambiguous: false } frame) return;
        if (!ReferenceEquals(_guideBindingDocument, _liveGuide) || !ReferenceEquals(_guideBindingBoss, boss))
        {
            _guideBindingDocument = _liveGuide; _guideBindingBoss = boss;
            _guideBindingScope = GuideEventMatching.Scope(_liveGuide, boss);
            _guideBindingSeen.Clear();
        }
        var bossNameID = _guideBossNames.GetValueOrDefault(boss.Name);
        var party = _ws.Party.WithoutSlot(excludeAlliance: true, excludeNPCs: true);
        var playerID = _ws.Party.Player()?.InstanceID ?? 0;
        foreach (var source in _ws.Actors.Where(actor => actor.Type is ActorType.Enemy or ActorType.Helper && !actor.IsAlly && !actor.IsDeadOrDestroyed))
        {
            if (source.CastInfo is not { EventHappened: false } cast || !cast.IsSpell() || !float.IsFinite(cast.NPCRemainingTime) || cast.NPCRemainingTime is <= 0 or > 120) continue;
            ResolveGuideEvent(boss, frame, source, bossNameID, "cast", cast.Action.ID, cast.TargetID, playerID,
                party.Any(member => member.InstanceID == cast.TargetID), 0, now.AddSeconds(cast.NPCRemainingTime), now);
        }
        foreach (var member in party)
        foreach (var status in member.Statuses.Where(status => status.ID != 0 && status.ExpireAt > now))
        {
            if (_ws.Actors.Find(status.SourceID) is not { } source || source.IsAlly || source.Type is not (ActorType.Enemy or ActorType.Helper)) continue;
            ResolveGuideEvent(boss, frame, source, bossNameID, "status", status.ID, member.InstanceID, playerID, true,
                status.Extra & 0xFF, status.ExpireAt, now);
        }
    }

    private void ResolveGuideEvent(GuideBoss boss, GuideCombatFrame frame, Actor source, uint bossNameID, string kind, uint identifier,
        ulong targetID, ulong playerID, bool partyTarget, int stacks, DateTime until, DateTime now)
    {
        var name = GuideSheetName(kind == "cast" ? "Action" : "Status", identifier, false);
        var mapping = "ObservedName";
        var belongs = GuideEventSource(source, _ws.Actors.Find(source.OwnerID), bossNameID);
        if (belongs && name.Length == 0 && _guideBindings is { } memory)
        {
            var names = memory.Entries.Where(entry => entry.Scope == _guideBindingScope && entry.Kind == kind && entry.ID == identifier
                && entry.SourceOID == source.OID && entry.SourceNameID == source.NameID).Select(entry => entry.Name).Distinct().ToArray();
            if (names.Length == 1) { name = names[0]; mapping = "RememberedID"; }
        }
        var result = belongs ? GuideEventMatching.Resolve(boss, frame.KnownPhase, kind, name, targetID, playerID, partyTarget, stacks)
            : new GuideEventResolution(null, null, null, "UnrelatedCaster", []);
        var instruction = result.Trigger?.Cue ?? (result.Mechanic?.Advice is { } advice ? GuideListFlow.Instruction(result.Mechanic, advice.Cue) : "");
        var key = result.Mechanic == null ? null : GuideNames.Hash(_guideBindingScope + "|" + result.Mechanic.Name + "|" + kind + "|" + identifier + "|" + name + "|" + source.OID + "|" + source.NameID);
        var occurrence = $"{source.InstanceID}:{source.OID}:{source.NameID}:{source.OwnerID}:{kind}:{identifier}:{targetID}";
        var outcome = result.Reason + "|" + result.Mechanic?.Name + "|" + instruction + "|" + frame.KnownPhase?.ID + "|" + mapping + "|" + stacks;
        var changed = !_guideBindingSeen.TryGetValue(occurrence, out var seen) || seen.Result != outcome || Math.Abs((seen.Until - until).TotalSeconds) > 1;
        if (changed)
        {
            if (_guideBindingSeen.Count >= 512) _guideBindingSeen.Clear();
            _guideBindingSeen[occurrence] = (until, outcome);
            if (_guideBindingPending.Count >= 64) { _guideBindingPending.Dequeue(); ++_guideBindingDropped; }
            _guideBindingPending.Enqueue(new(++_guideBindingSequence, DateTime.UtcNow, _guideBindingScope, boss.Name, frame.KnownPhase?.ID, kind,
                source.InstanceID, source.OID, source.NameID, identifier, name, targetID, stacks, result.Reason, result.Candidates,
                result.Mechanic?.Name, instruction, mapping, key));
            if (key != null && mapping == "ObservedName") _guideBindings?.Remember(new(key, _guideBindingScope, result.Mechanic!.Name, kind,
                identifier, name, source.OID, source.NameID, 1, DateTime.UtcNow));
        }
        if (result.Mechanic == null || result.Phase == null) return;
        var signal = new GuideSignal(boss, result.Phase, result.Mechanic, kind == "cast" ? GuideSignalKind.Cast : GuideSignalKind.Status,
            source.InstanceID, source.OID, source.NameID, identifier, targetID, until,
            kind == "cast" ? GuideRules.LiveGuidance(result.Mechanic, result.Phase, boss) : GuideRules.StatusGuidance(result.Mechanic, result.Phase, name, boss),
            result.Reason + "; " + mapping + "; current boss and duty")
        { OwnerID = source.OwnerID, Instruction = instruction, Trigger = result.Trigger, BindingKey = key };
        _guideSignals.Add(signal);
        if (kind == "cast") _guideActionNames[(boss.Name, result.Mechanic.Name)] = identifier;
    }
}
