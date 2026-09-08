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
    private string _guideBindingPlanHash = "";

    internal static bool GuideEventSource(Actor source, Actor? owner, uint bossNameID)
        => bossNameID != 0 && source.InstanceID != 0 && source.OID != 0 && source.NameID != 0 && !source.IsDeadOrDestroyed && !source.IsAlly
            && source.Type is ActorType.Enemy or ActorType.Helper && (source.OwnerID == 0 ? source.NameID == bossNameID
                : owner is { IsDeadOrDestroyed: false, IsAlly: false, Type: ActorType.Enemy } && owner.InstanceID == source.OwnerID && owner.NameID == bossNameID);

    private void UpdateGuideEventBindings(DateTime now)
    {
        if (_liveGuide == null || _guideEncounter.Frame is not { Boss: { } boss, Upcoming: false, Ambiguous: false } frame) return;
        var plan = GuideIDs(_liveGuide);
        if (!ReferenceEquals(_guideBindingDocument, _liveGuide) || !ReferenceEquals(_guideBindingBoss, boss) || _guideBindingPlanHash != (plan?.Fingerprint ?? ""))
        {
            _guideBindingDocument = _liveGuide; _guideBindingBoss = boss;
            _guideBindingPlanHash = plan?.Fingerprint ?? "";
            _guideBindingScope = GuideNames.Hash(GuideEventMatching.Scope(_liveGuide, boss) + "|" + _guideBindingPlanHash);
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
        var plan = GuideIDs(_liveGuide);
        var row = plan?.Catalog.Row(kind, identifier);
        var name = plan == null ? GuideSheetName(kind == "cast" ? "Action" : "Status", identifier, false) : row?.EnglishName ?? "";
        var mapping = plan == null ? "ObservedName" : "OfficialGameID";
        var owner = _ws.Actors.Find(source.OwnerID);
        var belongs = plan?.OwnsSource(boss, source, owner) ?? GuideEventSource(source, owner, bossNameID);
        if (plan == null && belongs && name.Length == 0 && _guideBindings is { } memory)
        {
            var names = memory.Entries.Where(entry => entry.Scope == _guideBindingScope && entry.Kind == kind && entry.ID == identifier
                && entry.SourceOID == source.OID && entry.SourceNameID == source.NameID).Select(entry => entry.Name).Distinct().ToArray();
            if (names.Length == 1) { name = names[0]; mapping = "RememberedID"; }
        }
        var result = belongs ? plan?.Resolve(boss, frame.KnownPhase, kind, identifier, targetID, playerID, partyTarget, stacks)
                ?? GuideEventMatching.Resolve(boss, frame.KnownPhase, kind, name, targetID, playerID, partyTarget, stacks)
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
            var candidateIDs = plan?.Candidates(boss, kind, result.Mechanic) ?? [];
            var capturedIDs = candidateIDs.Length > 64 ? candidateIDs.Take(63).Append(identifier).Distinct().ToArray() : candidateIDs;
            _guideBindingPending.Enqueue(new(++_guideBindingSequence, DateTime.UtcNow, _guideBindingScope, boss.Name, frame.KnownPhase?.ID, kind,
                source.InstanceID, source.OID, source.NameID, identifier, name, targetID, stacks, result.Reason, result.Candidates,
                result.Mechanic?.Name, instruction, mapping, key)
            { CatalogHash = plan?.Catalog.Fingerprint ?? "", CandidateIDs = capturedIDs, CandidateIDsOmitted = candidateIDs.Length - capturedIDs.Length,
                CastType = row?.CastType ?? 0, EffectRange = row?.EffectRange ?? 0 });
            if (key != null && mapping is "ObservedName" or "OfficialGameID") _guideBindings?.Remember(new(key, _guideBindingScope, result.Mechanic!.Name, kind,
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
