namespace BossMod.Foretell;

internal sealed class GuidePulseLinks
{
    private GuideDocument? _document;
    private GuideBoss? _boss;
    private readonly Dictionary<GuideMechanic, GuideSignal> _casts = [];
    private readonly Dictionary<(ulong Source, uint Action), GuideSignal> _linked = [];

    public void Clear()
    {
        _document = null; _boss = null; _casts.Clear(); _linked.Clear();
    }

    public void Update(GuideDocument document, GuideCombatFrame frame, IEnumerable<GuideSignal> signals, DateTime now)
    {
        if (!ReferenceEquals(document, _document) || !ReferenceEquals(frame.Boss, _boss) || frame.Upcoming || frame.Ambiguous) Clear();
        _document = document; _boss = frame.Boss;
        foreach (var key in _casts.Where(pair => pair.Value.Until.AddSeconds(12) < now).Select(pair => pair.Key).ToArray()) _casts.Remove(key);
        foreach (var key in _linked.Where(pair => pair.Value.Until < now).Select(pair => pair.Key).ToArray()) _linked.Remove(key);
        if (_boss == null || frame.Upcoming || frame.Ambiguous) return;
        foreach (var signal in signals.Where(signal => signal.Kind == GuideSignalKind.Cast && ReferenceEquals(signal.Boss, _boss)))
            if (_casts.Count < 64 || _casts.ContainsKey(signal.Mechanic)) _casts[signal.Mechanic] = signal;
    }

    public GuideSignal? Resolve(GuideIdPlan plan, GuideCombatFrame frame, Actor source, Func<ulong, Actor?> actor,
        uint actionID, DateTime until, DateTime now)
    {
        if (!ReferenceEquals(plan.Document, _document) || frame is not { Boss: { } boss, Upcoming: false, Ambiguous: false }
            || !ReferenceEquals(boss, _boss) || until <= now || until > now.AddSeconds(3)
            || source.IsDeadOrDestroyed || source.IsAlly || source.IsTargetable || source.OwnerID != 0
            || source.Type is not (ActorType.Enemy or ActorType.Helper) || !plan.AllowsInstant(boss, actionID)) return null;
        var match = plan.Resolve(boss, frame.KnownPhase, "cast", actionID, source.InstanceID, 0, false, 0);
        if (match.Mechanic == null || match.Phase == null || !GuidePhases.Includes(boss, match.Mechanic, frame.KnownPhase)) return null;
        var key = (source.InstanceID, actionID);
        _linked.TryGetValue(key, out var previous);
        if (previous != null && (previous.Until < now || previous.SourceOID != source.OID || previous.SourceNameID != source.NameID
            || !ReferenceEquals(previous.Mechanic, match.Mechanic))) previous = null;
        var seed = previous ?? _casts.GetValueOrDefault(match.Mechanic);
        if (seed == null || previous == null && seed.Until.AddSeconds(12) < now) return null;
        var bossID = previous?.RelatedBossID ?? (seed.OwnerID == 0 ? seed.SourceID : seed.OwnerID);
        if (actor(bossID) is not { IsDeadOrDestroyed: false, IsAlly: false, Type: ActorType.Enemy } owner
            || !ReferenceEquals(plan.Boss(owner.NameID), boss) || (source.Position - owner.Position).Length() > 60) return null;
        if (_linked.Count >= 128 && !_linked.ContainsKey(key)) return null;
        var signal = new GuideSignal(boss, match.Phase, match.Mechanic, GuideSignalKind.Pulse, source.InstanceID, source.OID, source.NameID,
            actionID, 0, until, GuidanceKind.None, "Repeated emitter + official action ID + recent matching boss cast; temporal association")
        {
            Instruction = GuideShortCue.Instruction(match.Mechanic, match.Mechanic.Advice?.Cue ?? "", match.Trigger),
            Trigger = match.Trigger, RelatedBossID = bossID, RelatedCastID = previous?.RelatedCastID ?? seed.ID
        };
        _linked[key] = signal;
        return signal;
    }
}
