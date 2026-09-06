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

internal enum GuideSignalKind { Cast, Status, Marker, Tether }
internal sealed record GuideSignal(GuideBoss Boss, GuidePhase Phase, GuideMechanic Mechanic, GuideSignalKind Kind,
    ulong SourceID, uint SourceOID, uint SourceNameID, uint ID, ulong TargetID, DateTime Until, GuidanceKind Guidance, string Evidence)
{
    public ulong OwnerID { get; init; }
}
internal sealed record GuideCombatFrame(GuideBoss? Boss, bool Upcoming, bool Ambiguous, GuidePhase? Phase, GuideSignal[] Active, int CompletedBosses)
{
    public static readonly GuideCombatFrame Empty = new(null, true, false, null, [], 0);
}
internal sealed record GuideActorState(ulong ID, uint OID, uint NameID, string EnglishName, bool Dead, bool Engaged, float Distance);

internal sealed class GuideEncounterTracker
{
    private readonly HashSet<string> _completed = [];
    private readonly HashSet<(string Boss, string Phase, string Mechanic)> _resolved = [];
    private readonly Dictionary<ulong, GuideActorState> _participants = [];
    private GuideBoss? _boss;
    private bool _engaged;
    private string _documentHash = "";
    private bool _waitingForReset;
    public GuideCombatFrame Frame { get; private set; } = GuideCombatFrame.Empty;

    public void Reset()
    {
        _completed.Clear(); _resolved.Clear(); _participants.Clear(); _boss = null; _engaged = false;
        Frame = GuideCombatFrame.Empty;
        _documentHash = "";
        _waitingForReset = false;
    }

    public void Wipe()
    {
        _resolved.Clear(); _participants.Clear(); _engaged = false;
        _waitingForReset = true;
        Frame = new(_boss, true, false, null, [], _completed.Count);
    }

    public bool Resolved(GuideBoss boss, GuidePhase phase, GuideMechanic mechanic) => _resolved.Contains((boss.Name, phase.Name, mechanic.Name));
    public void Resolve(GuideSignal signal)
    {
        if (ReferenceEquals(signal.Boss, _boss)) _resolved.Add((signal.Boss.Name, signal.Phase.Name, signal.Mechanic.Name));
    }

    public void ObserveDeath(ulong actorID, uint oid, uint nameID)
    {
        if (_participants.TryGetValue(actorID, out var actor) && actor.OID == oid && actor.NameID == nameID)
            _participants[actorID] = actor with { Dead = true, Engaged = false };
    }

    public GuideCombatFrame Update(GuideDocument document, IReadOnlyList<GuideActorState> actors, bool partyWiped)
    {
        if (_documentHash != document.SourceHash) { Reset(); _documentHash = document.SourceHash; }
        if (partyWiped) Wipe();
        if (_waitingForReset)
        {
            if (actors.Any(actor => actor.Engaged && !actor.Dead)) return Frame;
            _waitingForReset = false;
        }
        var identified = actors.Select(actor => (Actor: actor, Matches: document.Bosses.Where(boss => GuideNames.Boss(boss.Name) == GuideNames.Boss(actor.EnglishName)).ToArray()))
            .Where(entry => entry.Matches.Length == 1).Select(entry => (entry.Actor, Boss: entry.Matches[0])).ToArray();
        if (_boss != null && _engaged)
        {
            foreach (var entry in identified.Where(entry => ReferenceEquals(entry.Boss, _boss)))
                _participants[entry.Actor.ID] = entry.Actor;
            if (_participants.Count != 0 && _participants.Values.All(actor => actor.Dead) && !partyWiped)
            {
                _completed.Add(_boss.Name); _participants.Clear(); _engaged = false; _boss = null; _resolved.Clear();
            }
        }
        var fighting = identified.Where(entry => !entry.Actor.Dead && entry.Actor.Engaged).Select(entry => entry.Boss).Distinct().ToArray();
        if (fighting.Length > 1)
            return Frame = new(null, false, true, null, [], _completed.Count);
        var chosen = fighting.Length == 1 ? fighting[0] : _boss != null && _engaged ? _boss
            : identified.Where(entry => !entry.Actor.Dead && !_completed.Contains(entry.Boss.Name) && entry.Actor.Distance <= 80)
                .OrderBy(entry => entry.Actor.Distance).Select(entry => entry.Boss).FirstOrDefault()
                ?? document.Bosses.FirstOrDefault(boss => !_completed.Contains(boss.Name));
        if (!ReferenceEquals(chosen, _boss))
        {
            _boss = chosen; _participants.Clear(); _resolved.Clear(); _engaged = false;
        }
        if (fighting.Length == 1 && !partyWiped)
        {
            _engaged = true;
            foreach (var entry in identified.Where(entry => ReferenceEquals(entry.Boss, _boss))) _participants[entry.Actor.ID] = entry.Actor;
        }
        return Frame = new(_boss, !_engaged, false, null, [], _completed.Count);
    }

    public void Synchronize(IEnumerable<GuideSignal> signals)
    {
        var active = signals.Where(signal => ReferenceEquals(signal.Boss, _boss) && !Frame.Upcoming && !Frame.Ambiguous).Take(64).ToArray();
        var phases = active.Select(signal => signal.Phase).Distinct().ToArray();
        Frame = Frame with { Active = active, Phase = phases.Length == 1 ? phases[0] : null };
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

    public static DecisionHazard Annotation(GuideSignal signal, Vector2 source, string label, string sourceUrl)
    {
        var prediction = new ActivePrediction(signal.SourceID, signal.Kind == GuideSignalKind.Cast ? signal.ID : 0, GeometryKind.Unknown, MechanicKind.Marker,
            source, source, 0, 0, 0, signal.Until, 1, signal.Evidence, "guide-annotation:" + signal.Kind + ":" + signal.ID,
            signal.SourceID, GuidanceKind.Marker, Label: label)
        { GuideLinked = true, Provenance = "Guide signal annotation · " + sourceUrl };
        return new(unchecked(long.MinValue + (long)signal.SourceID + signal.ID), prediction, signal.Until, false, true, prediction.Provenance);
    }
}
