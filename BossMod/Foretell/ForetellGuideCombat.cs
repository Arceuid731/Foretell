using System.Text.RegularExpressions;

namespace BossMod.Foretell;

internal sealed record GuideRule(GuidanceKind Guidance, string Evidence, bool Conditional)
{
    public bool Actionable => Guidance != GuidanceKind.None && !Conditional;
}
internal sealed record GuideStatusRule(string StatusName, GuidanceKind Guidance);

internal static class GuideRules
{
    private static readonly Regex Conditions = new(@"\b(if|when|once|unless|except|depending|instead|otherwise|either|after|before|then|followed|following|alternat(?:e|es|ing)|while|during|until|only|without|not|no|never|cannot|(?:can|don|doesn|isn|won|shouldn|mustn)['’]t|immune|immunity|avoid stacking)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly (GuidanceKind Kind, Regex Pattern)[] Patterns =
    [
        (GuidanceKind.Tankbuster, Pattern(@"\btank[ -]?buster\b")),
        (GuidanceKind.Raidwide, Pattern(@"\braid[ -]?wide\b|\bunavoidable (?:party|group)[ -]wide damage\b|^(?:party|group)[ -]wide damage[.!]?$")),
        (GuidanceKind.Stack, Pattern(@"\bstack marker\b|\bshare (?:the )?(?:damage|hit)\b|\bgroup stack\b")),
        (GuidanceKind.Spread, Pattern(@"\bspread out\b|\bspread markers?\b|\bspread aoes?\b")),
        (GuidanceKind.LookAway, Pattern(@"\bgaze (?:attack|mechanic)\b|\blook away\b|\bturn away\b")),
        (GuidanceKind.Knockback, Pattern(@"\bknock[ -]?back\b")),
        (GuidanceKind.Soak, Pattern(@"\bsoak (?:the )?towers?\b|\bstand in (?:the )?towers?\b")),
        (GuidanceKind.Cleanse, Pattern(@"\b(?:cleansed|removed|dispelled) (?:with|by|using) esuna\b")),
        (GuidanceKind.Avoid, Pattern(@"\b(?:circle|circular|cone|conal|line|donut|doughnut|rectangular|cross)[ -](?:shaped )?(?:aoe|attack|area of effect)\b|\b(?:cone|line|circle) that deals damage\b"))
    ];

    private static Regex Pattern(string value) => new(value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    public static bool HasConditions(string source) => Conditions.IsMatch(source);

    public static GuideStatusRule? PrepareStatus(string source)
    {
        var match = Regex.Match(source, @"^If (?:you|players|a player) (?:have|has|receive|receives|are afflicted with|is afflicted with|are affected by|is affected by) (?<status>[\p{L}\p{N}'’ -]{3,80}),\s*(?<response>[^\r\n]+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        if (!match.Success) return null;
        var rules = Prepare(match.Groups["response"].Value);
        return rules is [{ Actionable: true } rule] && rule.Guidance is GuidanceKind.Spread or GuidanceKind.LookAway or GuidanceKind.Cleanse
            ? new(match.Groups["status"].Value.Trim(), rule.Guidance) : null;
    }

    public static GuidanceKind StatusGuidance(GuideMechanic mechanic, GuidePhase phase, string statusName, GuideBoss? boss = null)
        => !phase.Conditional && !HasUnresolvedBossContext(boss) && mechanic.StatusRule is { } rule && GuideNames.Normalize(rule.StatusName) == GuideNames.Normalize(statusName)
            ? rule.Guidance : GuidanceKind.None;

    public static GuideRule[] Prepare(string source)
    {
        if (source.Length is 0 or > 24000) return [];
        var conditional = Conditions.IsMatch(source);
        var rules = Patterns.Where(pattern => pattern.Pattern.IsMatch(source)).Select(pattern => new GuideRule(pattern.Kind, source, conditional)).ToArray();
        if (rules.Length > 1)
            return rules.Select(rule => rule with { Conditional = true }).ToArray();
        return rules;
    }

    private static bool HasUnresolvedBossContext(GuideBoss? boss) => boss?.Phases.Any(phase => phase.Name.Length == 0 && phase.Conditional) == true;

    public static GuidanceKind LiveGuidance(GuideMechanic mechanic, GuidePhase phase, GuideBoss? boss = null)
    {
        var rules = mechanic.Rules;
        return rules.Length == 1 && rules[0].Actionable && !phase.Conditional && !HasUnresolvedBossContext(boss) ? rules[0].Guidance : GuidanceKind.None;
    }

    public static bool Mentions(string source, string name)
        => name.Length is >= 3 and <= 200 && Regex.IsMatch(source, @"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
}

internal enum GuideSignalKind { Cast, Status, Marker, Tether, Action }
internal sealed record GuideSignal(GuideBoss Boss, GuidePhase Phase, GuideMechanic Mechanic, GuideSignalKind Kind,
    ulong SourceID, uint SourceOID, uint SourceNameID, uint ID, ulong TargetID, DateTime Until, GuidanceKind Guidance, string Evidence)
{
    public ulong OwnerID { get; init; }
}
internal sealed record GuideCombatFrame(GuideBoss? Boss, bool Upcoming, bool Ambiguous, GuidePhase? Phase, GuideSignal[] Active, int CompletedBosses)
{
    public static readonly GuideCombatFrame Empty = new(null, true, false, null, [], 0);
    public GuidePhaseDefinition? KnownPhase { get; init; }
}
internal static class GuidePhaseSelection
{
    public static bool Visible(GuideCombatFrame frame, GuidePhase phase, GuideMechanic mechanic)
        => frame.Boss == null || frame.Upcoming || frame.Ambiguous || !frame.Boss.Phases.Contains(phase)
            || frame.Active.Any(signal => ReferenceEquals(signal.Boss, frame.Boss) && ReferenceEquals(signal.Mechanic, mechanic))
            || GuidePhases.Includes(frame.Boss, mechanic, frame.KnownPhase);
}
internal sealed record GuideActorState(ulong ID, uint OID, uint NameID, string EnglishName, bool Dead, bool Engaged, float Distance);

internal static class GuideSignalMatching
{
    public static Actor? Owner(Actor? source, Actor? owner)
    {
        if (source == null || source.IsDeadOrDestroyed || source.IsAlly || source.InstanceID == 0 || source.OID == 0 || source.NameID == 0
            || source.Type is not (ActorType.Enemy or ActorType.Helper)) return null;
        if (source.OwnerID == 0) return source.Type == ActorType.Enemy ? source : null;
        return owner is { IsDeadOrDestroyed: false, IsAlly: false, Type: ActorType.Enemy }
            && owner.InstanceID == source.OwnerID && owner.OID != 0 && owner.NameID != 0 ? owner : null;
    }

    public static GuideSignal? Status(GuideBoss boss, Actor player, ActorStatus status, Actor? source, Actor? owner, DateTime now, Func<string, uint, string> name)
    {
        if (player.IsDeadOrDestroyed || player.InstanceID == 0 || status.ID == 0 || status.ExpireAt <= now || source == null
            || status.SourceID != source.InstanceID || Owner(source, owner) is not { } bossActor
            || GuideNames.Boss(name("BNpcName", bossActor.NameID)) != GuideNames.Boss(boss.Name)) return null;
        var statusName = name("Status", status.ID);
        if (statusName.Length == 0) return null;
        var candidates = boss.Phases.SelectMany(phase => phase.Mechanics.Select(mechanic => (phase, mechanic)))
            .Where(entry => entry.mechanic.Advice is { } advice
                ? advice.TriggerKind == "status" && GuideNames.Normalize(advice.TriggerName) == GuideNames.Normalize(statusName)
                : GuideRules.Mentions(entry.mechanic.Text, statusName)).ToArray();
        if (candidates.Length != 1) return null;
        var candidate = candidates[0];
        return new(boss, candidate.phase, candidate.mechanic, GuideSignalKind.Status, source.InstanceID, source.OID, source.NameID,
            status.ID, player.InstanceID, status.ExpireAt, GuideRules.StatusGuidance(candidate.mechanic, candidate.phase, statusName, boss),
            "Status ID + exact name + current boss ownership") { OwnerID = source.OwnerID };
    }
}

internal sealed class GuideActionQueue
{
    private sealed record Pending(GuideDuty Duty, GuideBoss Boss, ulong SourceID, uint SourceOID, uint SourceNameID,
        ulong OwnerID, uint OwnerOID, uint OwnerNameID, uint ActionID, ulong TargetID, DateTime Until);
    private readonly List<Pending> _pending = [];
    public int Count => _pending.Count;

    public void Clear() => _pending.Clear();

    public void Enqueue(GuideDuty duty, GuideBoss boss, Actor source, Actor? owner, uint action, ulong target, DateTime now)
    {
        if (!duty.Valid || action == 0 || GuideSignalMatching.Owner(source, owner) is not { } bossActor) return;
        _pending.RemoveAll(pending => pending.Until <= now || pending.SourceID == source.InstanceID && pending.ActionID == action);
        if (_pending.Count >= 32) return;
        _pending.Add(new(duty, boss, source.InstanceID, source.OID, source.NameID, source.OwnerID, bossActor.OID, bossActor.NameID,
            action, target, now.AddSeconds(4)));
    }

    public GuideSignal[] Resolve(GuideDocument document, GuideDuty duty, GuideCombatFrame frame, DateTime now,
        Func<ulong, Actor?> actor, Func<string, uint, string> name)
    {
        if (document.Duty != duty || frame is not { Boss: { } boss, Upcoming: false, Ambiguous: false })
        {
            Clear();
            return [];
        }
        List<GuideSignal> resolved = [];
        foreach (var pending in _pending.ToArray())
        {
            var source = actor(pending.SourceID);
            var owner = GuideSignalMatching.Owner(source, pending.OwnerID == 0 ? null : actor(pending.OwnerID));
            if (pending.Until <= now || pending.Duty != duty || !ReferenceEquals(pending.Boss, boss)
                || source == null || source.InstanceID != pending.SourceID || source.OID != pending.SourceOID || source.NameID != pending.SourceNameID || source.OwnerID != pending.OwnerID
                || owner == null || owner.OID != pending.OwnerOID || owner.NameID != pending.OwnerNameID)
            {
                _pending.Remove(pending);
                continue;
            }
            var bossName = name("BNpcName", owner.NameID);
            if (bossName.Length == 0) continue;
            if (GuideNames.Boss(bossName) != GuideNames.Boss(boss.Name))
            {
                _pending.Remove(pending);
                continue;
            }
            var actionName = name("Action", pending.ActionID);
            if (actionName.Length == 0) continue;
            _pending.Remove(pending);
            var match = GuideSynchronization.Match(document, new(duty, source.InstanceID, source.OID, source.NameID, bossName,
                pending.ActionID, actionName, pending.Until), now);
            if (match == null || !ReferenceEquals(match.Boss, boss)) continue;
            resolved.Add(new(boss, match.Phase, match.Mechanic, GuideSignalKind.Action, source.InstanceID, source.OID, source.NameID,
                pending.ActionID, pending.TargetID, pending.Until, GuidanceKind.None, "Observed ActionEffect + current boss ownership + exact ability name")
                { OwnerID = pending.OwnerID });
        }
        return resolved.ToArray();
    }
}

internal static class GuideCentralPresentation
{
    public static GuideSignal[] Select(IEnumerable<GuideSignal> signals, ulong playerID)
        => playerID == 0 ? [] : signals.OrderByDescending(signal => signal.TargetID == playerID).ThenBy(signal => signal.Until).Take(2).ToArray();

    public static bool Owns(ActivePrediction prediction, IEnumerable<GuideSignal> displayed)
        => displayed.Any(signal => signal.Kind == GuideSignalKind.Cast && signal.SourceID == prediction.CasterID && signal.ID == prediction.ActionID
            && Math.Abs((signal.Until - prediction.Activation).TotalSeconds) < .4
            && (signal.Mechanic.Advice != null || prediction.Guidance is GuidanceKind.None or GuidanceKind.Marker || signal.Guidance == prediction.Guidance));
}

internal sealed class GuideEncounterTracker
{
    private readonly GuideEncounterProgress _progress = new();
    private GuideDuty? _duty;
    private readonly HashSet<string> _completed = [];
    private readonly HashSet<(string Boss, string Phase, string Mechanic)> _resolved = [];
    private readonly Dictionary<ulong, GuideActorState> _participants = [];
    private readonly Dictionary<(GuideSignalKind Kind, ulong SourceID, uint SourceOID, uint SourceNameID, ulong OwnerID, uint ID, ulong TargetID, string Mechanic), List<DateTime>> _phaseObservations = [];
    private GuidePhaseDefinition? _knownPhase;
    private GuideBoss? _boss;
    private bool _engaged;
    private string _documentHash = "";
    private bool _waitingForReset;
    public GuideCombatFrame Frame { get; private set; } = GuideCombatFrame.Empty;

    public void Reset()
    {
        _progress.Reset(); _duty = null;
        ResetDocument();
    }

    private void ResetDocument()
    {
        _completed.Clear(); _resolved.Clear(); _participants.Clear(); _boss = null; _engaged = false;
        Frame = GuideCombatFrame.Empty;
        _documentHash = "";
        _waitingForReset = false;
        ResetPhase();
    }

    public void Wipe()
    {
        _progress.Wipe();
        _resolved.Clear(); _participants.Clear(); _engaged = false;
        _waitingForReset = true;
        ResetPhase();
        Frame = new(_boss, true, false, null, [], _completed.Count);
    }

    public bool Resolved(GuideBoss boss, GuidePhase phase, GuideMechanic mechanic) => _resolved.Contains((boss.Name, phase.Name, mechanic.Name));
    public void Resolve(GuideSignal signal)
    {
        if (ReferenceEquals(signal.Boss, _boss)) _resolved.Add((signal.Boss.Name, signal.Phase.Name, signal.Mechanic.Name));
    }

    public void ObserveDeath(ulong actorID, uint oid, uint nameID)
    {
        _progress.ObserveDeath(actorID, oid, nameID);
        if (_participants.TryGetValue(actorID, out var actor) && actor.OID == oid && actor.NameID == nameID)
        {
            _participants[actorID] = actor with { Dead = true, Engaged = false };
            ResetPhase();
        }
    }

    private void ResetPhase()
    {
        _knownPhase = null;
        _phaseObservations.Clear();
        Frame = Frame with { KnownPhase = null };
    }

    public GuideCombatFrame Update(GuideDocument? document, IReadOnlyList<GuideActorState> actors, bool partyWiped)
    {
        if (document != null)
        {
            var identity = document.SourceHash + document.ModelRevision + document.AnalysisLanguage;
            if (_duty != null && _duty != document.Duty) Reset();
            if (_documentHash != identity)
            {
                var waitingForReset = _waitingForReset;
                ResetDocument(); _documentHash = identity; _waitingForReset = waitingForReset;
            }
            _duty = document.Duty;
        }
        if (_boss != null && document != null)
        {
            var current = document.Bosses.FirstOrDefault(boss => boss.Name == _boss.Name);
            if (!ReferenceEquals(current, _boss)) ResetPhase();
            _boss = current;
        }
        if (partyWiped) Wipe();
        if (_waitingForReset)
        {
            if (actors.Any(actor => actor.Engaged && !actor.Dead)) return Frame;
            _waitingForReset = false;
        }
        _progress.Observe(actors);
        if (document == null) return Frame = GuideCombatFrame.Empty;
        _completed.UnionWith(_progress.Completed(document));
        var identified = actors.Select(actor => (Actor: actor, Matches: document.Bosses.Where(boss => GuideNames.Boss(boss.Name) == GuideNames.Boss(actor.EnglishName)).ToArray()))
            .Where(entry => entry.Matches.Length == 1).Select(entry => (entry.Actor, Boss: entry.Matches[0])).ToArray();
        if (_boss != null && _engaged)
        {
            var present = identified.Where(entry => ReferenceEquals(entry.Boss, _boss) && !entry.Actor.Dead).Select(entry => entry.Actor).ToArray();
            if (present.Length > 0 && _participants.Count > 0
                && !present.Any(actor => _participants.TryGetValue(actor.ID, out var previous) && previous.OID == actor.OID && previous.NameID == actor.NameID && !previous.Dead))
            {
                _participants.Clear(); _resolved.Clear();
                ResetPhase();
            }
            foreach (var entry in identified.Where(entry => ReferenceEquals(entry.Boss, _boss)))
                _participants[entry.Actor.ID] = entry.Actor;
            if (_participants.Count != 0 && _participants.Values.All(actor => actor.Dead) && !partyWiped)
            {
                _completed.Add(_boss.Name); _participants.Clear(); _engaged = false; _boss = null; _resolved.Clear();
                ResetPhase();
            }
            else if (identified.Any(entry => ReferenceEquals(entry.Boss, _boss) && !entry.Actor.Dead)
                && !identified.Any(entry => ReferenceEquals(entry.Boss, _boss) && entry.Actor.Engaged && !entry.Actor.Dead))
            {
                _participants.Clear(); _engaged = false; _resolved.Clear();
                ResetPhase();
            }
        }
        var fighting = identified.Where(entry => !entry.Actor.Dead && entry.Actor.Engaged).Select(entry => entry.Boss).Distinct().ToArray();
        if (fighting.Length > 1)
        {
            _knownPhase = null;
            return Frame = new(null, false, true, null, [], _completed.Count);
        }
        var chosen = fighting.Length == 1 ? fighting[0] : _boss != null && _engaged ? _boss
            : identified.Where(entry => !entry.Actor.Dead && !_completed.Contains(entry.Boss.Name) && entry.Actor.Distance <= 80)
                .OrderBy(entry => entry.Actor.Distance).Select(entry => entry.Boss).FirstOrDefault()
                ?? document.Bosses.FirstOrDefault(boss => !_completed.Contains(boss.Name));
        if (!ReferenceEquals(chosen, _boss))
        {
            _boss = chosen; _participants.Clear(); _resolved.Clear(); _engaged = false;
            ResetPhase();
        }
        if (fighting.Length == 1 && !partyWiped)
        {
            _engaged = true;
            foreach (var entry in identified.Where(entry => ReferenceEquals(entry.Boss, _boss))) _participants[entry.Actor.ID] = entry.Actor;
        }
        return Frame = new(_boss, !_engaged, false, null, [], _completed.Count) { KnownPhase = _knownPhase };
    }

    public void Synchronize(IEnumerable<GuideSignal> signals)
    {
        var active = signals.Where(signal => ReferenceEquals(signal.Boss, _boss) && !Frame.Upcoming && !Frame.Ambiguous).Take(64).ToArray();
        var phases = active.Select(signal => signal.Phase).Distinct().ToArray();
        ObservePhases(active);
        Frame = Frame with { Active = active, Phase = phases.Length == 1 ? phases[0] : null, KnownPhase = _knownPhase };
    }

    private void ObservePhases(GuideSignal[] active)
    {
        if (_boss == null || _boss.PhaseDefinitions.Length == 0 || Frame.Upcoming || Frame.Ambiguous) return;
        var fresh = active.Where(signal => signal.Kind is GuideSignalKind.Cast or GuideSignalKind.Status or GuideSignalKind.Action
            && signal.Mechanic.Advice is { Conflict.Length: 0 } advice
            && (signal.Kind == GuideSignalKind.Status ? advice.TriggerKind == "status" : advice.TriggerKind == "cast")
            && signal.Mechanic.PhaseMemberships.Length > 0
            && _boss.Phases.Any(phase => phase.Mechanics.Contains(signal.Mechanic))
            && _participants.TryGetValue(signal.OwnerID == 0 ? signal.SourceID : signal.OwnerID, out var owner) && !owner.Dead
            && (signal.OwnerID != 0 || owner.OID == signal.SourceOID && owner.NameID == signal.SourceNameID)
            && NewPhaseObservation(signal))
            .Select(signal => signal.Mechanic.PhaseMemberships).Where(memberships => memberships.Length > 0).ToArray();
        var exclusive = fresh.Where(memberships => memberships.Length == 1).Select(memberships => memberships[0].PhaseID).Distinct().ToArray();
        if (exclusive.Length > 1 || fresh.Any(memberships => memberships.Any(membership => !_boss.PhaseDefinitions.Any(phase => phase.ID == membership.PhaseID))))
        {
            _knownPhase = null;
            return;
        }
        if (exclusive.Length == 1) _knownPhase = _boss.PhaseDefinitions.FirstOrDefault(phase => phase.ID == exclusive[0]);
        if (_knownPhase != null && fresh.Any(memberships => !memberships.Any(membership => membership.PhaseID == _knownPhase.ID)))
            _knownPhase = null;
    }

    private bool NewPhaseObservation(GuideSignal signal)
    {
        var key = (signal.Kind, signal.SourceID, signal.SourceOID, signal.SourceNameID, signal.OwnerID, signal.ID,
            signal.Kind == GuideSignalKind.Status ? signal.TargetID : 0, signal.Mechanic.Name);
        if (!_phaseObservations.TryGetValue(key, out var occurrences)) _phaseObservations[key] = occurrences = [];
        if (occurrences.Any(until => Math.Abs((until - signal.Until).TotalSeconds) < .4)) return false;
        occurrences.Add(signal.Until);
        return true;
    }
}

internal static class GuideDecisionBridge
{
    public static DecisionHazard Associate(DecisionHazard hazard, GuideSignal signal, string label, string sourceUrl)
    {
        var prediction = hazard.Prediction;
        if (signal.Kind != GuideSignalKind.Cast || prediction.CasterID != signal.SourceID || prediction.ActionID != signal.ID
            || Math.Abs((prediction.Activation - signal.Until).TotalSeconds) > .4) return hazard;
        var guidance = signal.Guidance;
        var compatible = prediction.Guidance is GuidanceKind.None or GuidanceKind.Avoid || prediction.Guidance == guidance;
        if (guidance is GuidanceKind.Stack or GuidanceKind.Spread or GuidanceKind.Soak)
            compatible = prediction.Guidance == guidance && prediction.TargetID != 0;
        var provenance = $"Guide + {prediction.Provenance} · {sourceUrl}";
        return hazard with
        {
            Prediction = prediction with { Label = label, Guidance = compatible && guidance != GuidanceKind.None ? guidance : prediction.Guidance,
                Provenance = provenance, Evidence = prediction.Evidence + " · " + signal.Evidence, GuideLinked = true },
            Provenance = provenance,
            AdvisoryOnly = hazard.AdvisoryOnly || guidance != GuidanceKind.None && compatible && prediction.Guidance != guidance
        };
    }

    public static DecisionHazard Annotation(GuideSignal signal, Vector2 source, string label, string sourceUrl, Vector2? statusTarget = null)
    {
        var targetedStatus = signal.Kind == GuideSignalKind.Status && signal.TargetID != 0 && statusTarget is { } position
            && float.IsFinite(position.X) && float.IsFinite(position.Y);
        var anchor = targetedStatus ? statusTarget!.Value : source;
        var targetID = targetedStatus ? signal.TargetID : signal.SourceID;
        var prediction = new ActivePrediction(signal.SourceID, signal.Kind == GuideSignalKind.Cast ? signal.ID : 0, GeometryKind.Unknown, MechanicKind.Marker,
            anchor, anchor, 0, 0, 0, signal.Until, 1, signal.Evidence, "guide-annotation:" + signal.Kind + ":" + signal.ID,
            targetID, GuidanceKind.Marker, Label: label)
        { GuideLinked = true, Provenance = "Guide signal annotation · " + sourceUrl };
        return new(unchecked(long.MinValue + (long)signal.SourceID + signal.ID), prediction, signal.Until, false, true, prediction.Provenance);
    }
}
