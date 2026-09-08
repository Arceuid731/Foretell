namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private readonly ForetellMovingHazards _movingHazards = new();
    private readonly GuidePulseLinks _guidePulseLinks = new();
    private DateTime _movingFrameAt;
    private long _movingFrameRevision = -1;
    private DecisionHazard[] _movingFrame = [];

    private DecisionHazard[] MovingHazards(DateTime now)
    {
        if (_movingFrameAt == now && _movingFrameRevision == _movingHazards.Revision) return _movingFrame;
        _movingFrame = _movingHazards.Snapshot(now).Where(hazard => _isReplay || _ws.Actors.Find(hazard.Prediction.CasterID)
            is { IsDeadOrDestroyed: false, IsAlly: false, IsTargetable: false }).ToArray();
        _movingFrameAt = now; _movingFrameRevision = _movingHazards.Revision;
        return _movingFrame;
    }

    private void UpdateGuidePulseSignals(DateTime now)
    {
        if (_liveGuide == null || GuideIDs(_liveGuide) is not { } plan) { _guidePulseLinks.Clear(); return; }
        var frame = _guideEncounter.Frame;
        _guidePulseLinks.Update(_liveGuide, frame, _guideSignals, now);
        foreach (var hazard in MovingHazards(now))
        {
            if (_ws.Actors.Find(hazard.Prediction.CasterID) is not { } source) continue;
            var signal = _guidePulseLinks.Resolve(plan, frame, source, id => _ws.Actors.Find(id), hazard.Prediction.ActionID, hazard.ActiveUntil, now);
            if (signal != null) _guideSignals.Add(signal);
        }
    }

    private bool GuidePulseIsCurrent(GuideSignal signal)
        => _liveGuide != null && GuideIDs(_liveGuide) is { } plan
            && _ws.Actors.Find(signal.RelatedBossID) is { IsDeadOrDestroyed: false, IsAlly: false } boss
            && ReferenceEquals(plan.Boss(boss.NameID), signal.Boss)
            && MovingHazards(ObservationNow()).Any(hazard => hazard.Prediction.CasterID == signal.SourceID && hazard.Prediction.ActionID == signal.ID);
}
