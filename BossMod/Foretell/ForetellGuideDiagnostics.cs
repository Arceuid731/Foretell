namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private string _guideDiagnosticSignature = "";
    private DateTime _guideDiagnosticSampleAt;

    private void CaptureGuideDiagnostics(bool force = false)
    {
        try { CaptureGuideDiagnosticsCore(force); }
        catch (Exception error)
        {
            if (_captureSession == null) return;
            System.Threading.Interlocked.Increment(ref _captureSession.GuideRejected);
            _captureSession.GuideError = $"Guide snapshot preparation failed: {error.GetType().Name}: {error.Message}";
        }
    }

    private void CaptureGuideDiagnosticsCore(bool force)
    {
        if (_capture == null || _captureSession == null || _isReplay || _ws.CurrentCFCID == 0) return;
        var now = DateTime.UtcNow;
        if (!force && (now - _guideDiagnosticSampleAt).TotalMilliseconds < 250) return;
        _guideDiagnosticSampleAt = now;
        var snapshot = _guides?.Snapshot;
        var document = _liveGuide?.Duty == _guideDuty ? _liveGuide : null;
        var summary = _guideSummaries?.Snapshot;
        var runtime = _guideSummaries?.Runtime;
        if (summary?.SourceHash != document?.SourceHash || summary?.Language != GuideClientLanguage) summary = null;
        var state = snapshot?.Duty == _guideDuty ? snapshot : null;
        var signals = LiveGuideSignals().ToArray();
        var options = new GuideCaptureOptions(_cfg.Mode, _cfg.EnableGuides, _cfg.GuideSidebar, _cfg.GuideEntryPopup, _guideEntryDismissed,
            _cfg.GuideCentralAlerts, _cfg.TextHints, _cfg.GuideLocalSummaries, _cfg.GuideSummaryGpu, _cfg.GuideScale, _cfg.GuideAlertScale)
        {
            ContextTokens = _cfg.GuideContextTokens, MemoryGiB = _cfg.GuideMemoryGiB,
            WorldOverlay = _cfg.WorldOverlay, MiniRadar = _cfg.MiniRadar, VisualThreshold = _cfg.VisualConfidence, WarningThreshold = _cfg.WarningConfidence,
            Layout = new(_cfg.GuideChecklistUnlocked, _cfg.GuidePositionX, _cfg.GuidePositionY, _cfg.GuideWidth, _cfg.GuideHeight,
                _cfg.GuideTextColor, _cfg.GuideActiveColor, _cfg.GuideResolvedColor, _cfg.GuideUnresolvedColor, _cfg.TextPositionX, _cfg.TextPositionY)
        };
        var signature = string.Join('|', _captureSession.ID, _guideDuty?.Key, document?.SourceHash, GuideClientLanguage, state?.State, state?.Error,
            summary?.Stage, summary?.Completed, summary?.Summaries.Count, summary?.LastIssue, runtime?.Stage, runtime?.ProcessID,
            _guideFrame.Boss?.Name, _guideFrame.Upcoming, _guideFrame.Ambiguous, _guideCombat,
            _guideBossNames.Count, _guideActionNames.Count,
            _guideBossNames.Values.Count(id => _guideNames.ContainsKey(("BNpcName", id, true))),
            _guideActionNames.Values.Count(id => _guideNames.ContainsKey(("Action", id, true))), options,
            string.Join(';', signals.Select(signal => $"{signal.SourceID}:{signal.ID}:{signal.Kind}:{signal.TargetID}:{signal.Until.Ticks / TimeSpan.TicksPerSecond}")));
        if (!force && signature == _guideDiagnosticSignature) return;
        var adapted = document?.Bosses.Select(boss => new GuideAdaptedBoss(boss.Name, GuideBossName(boss), _guideBossNames.GetValueOrDefault(boss.Name),
            boss.Phases.Select(phase => new GuideAdaptedPhase(phase.Name, GuideContextSummary(boss, phase), phase.Conditional,
                phase.Mechanics.Select(mechanic => new GuideAdaptedMechanic(mechanic.Name, GuideMechanicName(boss, mechanic),
                    _guideActionNames.GetValueOrDefault((boss.Name, mechanic.Name)), ForetellGuideSummaries.Key(boss, phase, mechanic), GuideSummaryFor(boss, phase, mechanic),
                    GuideRules.LiveGuidance(mechanic, phase, boss), GuideChecklistInstruction(boss, phase, mechanic), _guideEncounter.Resolved(boss, phase, mechanic),
                    mechanic.Rules, mechanic.StatusRule)).ToArray())).ToArray())).ToArray() ?? [];
        var live = signals.Select(signal => new GuideCapturedSignal(signal.Boss.Name, signal.Phase.Name, signal.Mechanic.Name, signal.Kind,
            signal.SourceID, signal.SourceOID, signal.SourceNameID, signal.ID, signal.OwnerID, signal.TargetID, signal.Until, signal.Guidance,
            GuideChecklistInstruction(signal.Boss, signal.Phase, signal.Mechanic, signal), signal.Evidence)).ToArray();
        var input = new GuideCaptureInput(now, _captureSession.ID, _captureSession.Territory, _guideDuty, GuideClientLanguage, document,
            new(state?.State.ToString() ?? (_cfg.EnableGuides ? "Unavailable" : "Disabled"), state?.Error ?? "", state?.FromCache ?? false, state?.ElapsedSeconds ?? 0,
                summary?.Stage ?? "Unavailable", summary?.Completed ?? 0, summary?.Total ?? 0, _guideFrame.Boss?.Name, _guideFrame.Upcoming, _guideFrame.Ambiguous, _guideCombat)
            { ModelRuntime = runtime, SummaryIssue = summary?.LastIssue },
            options, adapted, live);
        if (_capture.EnqueueGuide(_captureSession, input)) _guideDiagnosticSignature = signature;
    }
}
