using System.Globalization;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private long _outcomeGapGeneration;
    private DateTime _lastOutcomeGapAt;

    private void NoteMissingOutcome(ForetellObservation observation)
    {
        if (ForetellInferenceCore.IsMechanicOutcomeEvidence(observation.Kind, observation.SourceKind))
        { ++_outcomeGapGeneration; _lastOutcomeGapAt = observation.At; }
    }

    private void ProcessObservation(ForetellObservation observation, bool replaying = false, bool enriched = false)
    {
        if (replaying)
        {
            ProcessObservationCore(observation, replaying: true, enriched: enriched);
            return;
        }
        if (!TryEnterSemanticBudget(ForetellInferenceCore.IsPrioritySemanticObservation(observation.Kind, observation.SourceKind)))
        {
            NoteMissingOutcome(observation);
            return;
        }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            ProcessObservationCore(observation, replaying: false, enriched: enriched);
        }
        finally
        {
            ChargeSemanticBudget(started);
        }
    }

    private void ProcessObservationCore(ForetellObservation observation, bool replaying, bool enriched)
    {
        // JSON replay files and future schema migrations are external inputs. Normalize nullable collections before
        // any learner or UI code can enumerate them.
        observation.Detail ??= "";
        observation.Numeric ??= [];
        observation.Text ??= [];
        observation.Binary ??= [];
        observation.At = NormalizeObservationTime(observation.At);
        var previousGap = _outcomeGapGeneration;
        PrepareDecisionContext(observation);
        if (observation.Numeric.TryGetValue("decision.outcomeGap", out var gap))
            _outcomeGapGeneration = Math.Max(_outcomeGapGeneration, (long)Math.Clamp(gap, 0, long.MaxValue));
        if (_outcomeGapGeneration != previousGap) _lastOutcomeGapAt = observation.At;
        observation.X = FiniteOrZero(observation.X);
        observation.Z = FiniteOrZero(observation.Z);
        observation.TargetX = FiniteOrZero(observation.TargetX);
        observation.TargetZ = FiniteOrZero(observation.TargetZ);
        observation.Rotation = FiniteOrZero(observation.Rotation);
        observation.Value1 = FiniteOrZero(observation.Value1);
        observation.Value2 = FiniteOrZero(observation.Value2);
        if (observation.Sequence == 0) observation.Sequence = ++_sequence;
        else _sequence = Math.Max(_sequence, observation.Sequence);
        if (observation.TerritoryID == 0) observation.TerritoryID = _territory;
        if (replaying) RegisterRecordedFeatures(observation); else if (!enriched) EnrichObservation(observation);
        if (!replaying && observation.Kind is ObservationKind.CastStart or ObservationKind.ActionResolved)
            observation.Prior = ReadActionGeometryPrior(observation);

        FinalizeDue(observation.At, exhaustive: replaying);
        _session.Observe(observation);
        LockCastGeometry(observation);
        if (observation.Kind == ObservationKind.DecisionFrame)
        {
            Record(observation, replaying);
            UpdateTriggerContextForecasts(observation.At);
            ExpireHazardContext(observation.At);
            ExpireTimelineForecasts(observation.At);
            if (_inPull && !DecisionCombat) EndCombatPull();
            foreach (var id in _predictions.Where(p => PredictionEnd(p.Value) < observation.At).Select(p => p.Key).ToArray())
                ExpirePrediction(id, "display lifetime ended");
            return;
        }
        var encounter = _store.Encounters.GetValueOrDefault(observation.TerritoryID);
        if (_cfg.EnableLearning)
        {
            encounter ??= Encounter(observation.TerritoryID);
            encounter.LastSeen = LearningNow;
            encounter.ObservationCounts[observation.Kind] = encounter.ObservationCounts.GetValueOrDefault(observation.Kind) + 1;
            UpdateSourceMemory(encounter, observation);
            if (observation.Detail is "raw:feature-window" or "raw:250ms-window")
                UpdateRawProtocolMemory(encounter, observation);
        }
        Record(observation, replaying);

        if (observation.Kind == ObservationKind.PositionSample)
        {
            HandlePositionSample(observation, replaying);
            return;
        }

        var excludedSignal = encounter != null && IsSignalExcluded(encounter, observation);
        if (!excludedSignal && IsCombatSignal(observation))
            TouchPull(encounter, observation);

        if (!excludedSignal)
            LearnPhaseBoundary(observation);

        var lifecycleSignal = observation.Kind is ObservationKind.DutyStarted or ObservationKind.DutyWiped or ObservationKind.DutyRecommenced or ObservationKind.DutyCompleted;
        var timelineContext = !excludedSignal && IsPredictiveTimelineSignal(observation)
            && (_inPull || lifecycleSignal || TouchOutOfCombatHazardContext(observation));
        if (timelineContext)
        {
            if (encounter != null)
                ResolveTimelineForecasts(encounter, observation);
            if (_inPull && encounter != null && !lifecycleSignal)
                ObserveSignalTriggerContext(encounter, observation);
            if (_cfg.EnableLearning && encounter != null)
                LearnSignalTimeline(encounter, observation);
            else
                TrackSignalState(observation);
            if (encounter != null && !lifecycleSignal)
            {
                ScheduleTimelineForecast(encounter, observation);
                ScheduleCompositeForecasts(encounter, observation);
            }
        }

        var correlated = excludedSignal ? null : CorrelateObservation(observation);
        var mayStartEpisode = !excludedSignal && IsEpisodeTrigger(observation);
        if (mayStartEpisode && (observation.Kind is ObservationKind.CastStart or ObservationKind.Icon || correlated == null))
            StartEpisode(observation, encounter);
        if (mayStartEpisode && observation.Kind == ObservationKind.CastStart)
            ApplyActionMetadataPrior(observation);
        AccumulateDataFeatures(observation, correlated);
        if (ForetellPreImpactModel.IsPrecursor(observation.Kind))
        {
            _precursorCues.Enqueue(observation);
            while (_precursorCues.Count > 32 || _precursorCues.TryPeek(out var cue) && (observation.At - cue.At).TotalSeconds > 3)
                _precursorCues.Dequeue();
        }

        if (observation.Kind == ObservationKind.DeathChanged && observation.Flag && observation.ActorID != 0)
            CancelPredictionsForCaster(observation.ActorID, "caster died");

    }

    private void UpdateSourceMemory(EncounterMemory encounter, ForetellObservation observation)
    {
        // Environment is intentionally represented as OID 0 so map/director effects remain inspectable as one source.
        // Player OIDs are also 0; never let a character name overwrite that synthetic environment bucket.
        if (observation.SourceKind is SourceKind.Player or SourceKind.Pet
            || (observation.ActorOID == 0 && observation.SourceKind != SourceKind.Environment))
            return;
        if (!encounter.Sources.TryGetValue(observation.ActorOID, out var source))
        {
            if (encounter.Sources.Count >= 8192)
            {
                var mechanicSources = encounter.Mechanics.Values.Select(mechanic => mechanic.SourceOID).ToHashSet();
                var oldest = encounter.Sources.Values.Where(item => item.OID != 0)
                    .OrderBy(item => mechanicSources.Contains(item.OID))
                    .ThenBy(item => item.LastSeen)
                    .FirstOrDefault();
                if (oldest != null)
                {
                    encounter.Sources.Remove(oldest.OID);
                    ++_learningEvictions;
                }
            }
            source = new()
            {
                OID = observation.ActorOID,
                Kind = observation.SourceKind,
                FirstSeen = LearningNow,
                LastSeen = LearningNow
            };
            encounter.Sources[observation.ActorOID] = source;
        }
        ++source.Observations;
        source.LastSeen = LearningNow;
        if (observation.Numeric.TryGetValue("actor.nameID", out var nameID) && nameID > 0 && nameID <= uint.MaxValue)
            source.NameID = (uint)nameID;
        if (observation.Text.TryGetValue("actor.name", out var name) && !string.IsNullOrWhiteSpace(name))
            source.Name = name;
        if (observation.ActorOID == 0)
        {
            source.Kind = SourceKind.Environment;
            source.NameID = 0;
            source.Name = "";
        }
        if (observation.Kind == ObservationKind.CastStart) ++source.Casts;
        if (observation.Kind is ObservationKind.Icon or ObservationKind.VFX or ObservationKind.TetherStart or ObservationKind.StatusGain or
            ObservationKind.EventObjectState or ObservationKind.EventObjectAnimation or ObservationKind.ActionTimelineEvent or
            ObservationKind.MapEffect or ObservationKind.LegacyMapEffect or ObservationKind.DirectorUpdate or ObservationKind.NpcYell or ObservationKind.ObjectEffect or
            ObservationKind.NativeVFXSpawn or ObservationKind.NativeVFXDestroy or ObservationKind.DutyStarted or ObservationKind.DutyWiped or
            ObservationKind.DutyRecommenced or ObservationKind.DutyCompleted or ObservationKind.FlyText or ObservationKind.DalamudLogMessage or
            ObservationKind.NormalToast or ObservationKind.QuestToast or ObservationKind.ErrorToast or ObservationKind.SystemLog)
            ++source.Signals;
        if (observation.Kind == ObservationKind.DeathChanged && observation.Flag) ++source.Deaths;
        RecordLearnedArenaSourceContext(observation, source);
    }

    private void UpdateRawProtocolMemory(EncounterMemory encounter, ForetellObservation observation)
    {
        const string prefix = "raw.window.structure[";
        foreach (var (key, countValue) in observation.Numeric)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal) || !key.EndsWith("].count", StringComparison.Ordinal))
                continue;
            var close = key.IndexOf(']', prefix.Length);
            if (close < 0 || !uint.TryParse(key.AsSpan(prefix.Length, close - prefix.Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var opcode))
                continue;
            var count = Math.Max(0, (long)Math.Round(countValue));
            if (count == 0) continue;
            var structural = key[..(close + 1)];
            var bytes = Math.Max(0, (long)Math.Round(observation.Numeric.GetValueOrDefault(structural + ".payloadBytes")));
            var min = Math.Clamp((int)Math.Round(observation.Numeric.GetValueOrDefault(structural + ".minLength")), 0, ForetellRawFormat.MaxPayloadBytes);
            var max = Math.Clamp((int)Math.Round(observation.Numeric.GetValueOrDefault(structural + ".maxLength")), min, ForetellRawFormat.MaxPayloadBytes);
            observation.Text.TryGetValue(structural + ".sequenceHash", out var hashText);
            _ = ulong.TryParse(hashText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var sequenceHash);

            if (!encounter.RawOpcodes.TryGetValue(opcode, out var memory))
            {
                if (encounter.RawOpcodes.Count >= 4096)
                {
                    encounter.RawOpcodes.Remove(encounter.RawOpcodes.MinBy(item => item.Value.Packets).Key);
                    ++_learningEvictions;
                }
                encounter.RawOpcodes[opcode] = memory = new() { OpcodeFamily = opcode };
            }
            ++memory.Windows;
            var previousPackets = memory.Packets;
            var windowMean = bytes / (double)count;
            memory.Packets += count;
            memory.PayloadBytes += bytes;
            var delta = windowMean - memory.MeanLength;
            memory.MeanLength += delta * count / Math.Max(1, memory.Packets);
            memory.LengthM2 += delta * delta * previousPackets * count / Math.Max(1, memory.Packets);
            memory.MinLength = memory.MinLength == int.MaxValue || memory.Packets == count ? min : Math.Min(memory.MinLength, min);
            memory.MaxLength = Math.Max(memory.MaxLength, max);
            if (memory.LastSequenceHash != 0 && sequenceHash != 0 && memory.LastSequenceHash != sequenceHash)
                ++memory.StructuralChanges;
            if (sequenceHash != 0) memory.LastSequenceHash = sequenceHash;
        }
    }

    private void TouchPull(EncounterMemory? encounter, ForetellObservation observation)
    {
        // Duty entry emits director/map/VFX traffic before the countdown or first engage. It is useful evidence,
        // but must not invent pulls and phases. Open world has no equivalent condition boundary, so signals retain
        // the inactivity-based heuristic there.
        var inDuty = _ws.CurrentCFCID != 0;
        if (inDuty && !DecisionCombat)
            return;
        if (!_inPull || (!inDuty && (observation.At - _lastCombatSignal).TotalSeconds > 30))
            BeginCombatPull(encounter, observation.At);
        _lastCombatSignal = observation.At;
    }

    private void BeginCombatPull(EncounterMemory? encounter, DateTime at)
    {
        _inPull = true;
        PromoteDynamicTerrainWarningsForPull();
        ResetHazardContext(cancelForecasts: true);
        _pullStartedAt = at;
        _phaseStartedAt = at;
        _lastCombatSignal = at;
        ++_session.Pulls;
        if (_cfg.EnableLearning && encounter != null)
            ++encounter.Pulls;
        _session.Phase = 0;
        _previousSignal = "";
        _previousSignalTime = default;
        _previousAction = 0;
        _previousActionTime = default;
        _lastPhaseBoundary = default;
        _lastPhaseBoundarySignal = "";
        _phaseBoundariesThisPull.Clear();
        _signalOccurrencesThisPull.Clear();
        _skippedTriggerContextsThisPull.Clear();
        _bossHealthTracks.Clear();
        _bossHealthSnapshots.Clear();
        RefreshTriggerForecastCandidates(encounter);
        _retryTriggerForecastCandidates = _triggerForecastCandidates.Count == 0;
    }

    private int CurrentTimelinePhase => ForetellInferenceCore.TimelinePhase(_inPull, _session.Phase);

    private int TimelinePhaseFor(ForetellObservation observation)
        => observation.Kind is ObservationKind.DutyStarted or ObservationKind.DutyWiped or ObservationKind.DutyRecommenced or ObservationKind.DutyCompleted
            ? 0
            : CurrentTimelinePhase;

    private void EndCombatPull()
    {
        if (!_inPull) return;
        _inPull = false;
        ClearDynamicTerrainWarnings();
        _pullStartedAt = default;
        _phaseStartedAt = default;
        _lastContextForecastSample = default;
        _lastCombatSignal = default;
        _session.Phase = 0;
        _previousSignal = "";
        _previousSignalTime = default;
        _previousAction = 0;
        _previousActionTime = default;
        _lastPhaseBoundary = default;
        _lastPhaseBoundarySignal = "";
        _untargetableSince.Clear();
        _phaseBoundariesThisPull.Clear();
        _signalOccurrencesThisPull.Clear();
        _skippedTriggerContextsThisPull.Clear();
        _triggerForecastCandidates.Clear();
        _retryTriggerForecastCandidates = false;
        _bossHealthTracks.Clear();
        _bossHealthSnapshots.Clear();
        foreach (var phase in _timelineForecasts.Values.Where(item => item.Phase >= 0).Select(item => item.Phase).Distinct().ToArray())
            CancelTimelineForecasts(phase);
        foreach (var id in _predictions.Keys.ToArray())
            ExpirePrediction(id, "pull ended");
    }

    private bool TouchOutOfCombatHazardContext(ForetellObservation observation)
    {
        if (_inPull || !ForetellInferenceCore.OpensOutOfCombatHazardContext(observation.Kind, observation.SourceKind, observation.ActorID, observation.TargetID))
            return false;
        if (_hazardContextUntil == default || observation.At > _hazardContextUntil)
        {
            _previousSignal = "";
            _previousSignalTime = default;
            CancelTimelineForecasts(ForetellInferenceCore.OutOfCombatHazardPhase);
        }
        _hazardContextUntil = observation.At.AddSeconds(30);
        return true;
    }

    private void ExpireHazardContext(DateTime now)
    {
        if (_inPull || _hazardContextUntil == default || now <= _hazardContextUntil)
            return;
        ResetHazardContext(cancelForecasts: true);
    }

    private void ResetHazardContext(bool cancelForecasts)
    {
        _hazardContextUntil = default;
        if (!_inPull)
        {
            _previousSignal = "";
            _previousSignalTime = default;
        }
        if (cancelForecasts)
            CancelTimelineForecasts(ForetellInferenceCore.OutOfCombatHazardPhase);
    }

    private void CancelTimelineForecasts(int phase)
    {
        foreach (var forecast in _timelineForecasts.Values.Where(item => item.Phase == phase).ToArray())
        {
            _timelineForecasts.Remove(forecast.ID);
            ExpirePrediction(forecast.ID, "forecast context ended");
        }
    }

    private void CancelPredictionsForCaster(ulong casterID, string reason)
    {
        foreach (var id in _predictions.Where(item => item.Value.CasterID == casterID).Select(item => item.Key).ToArray())
        {
            _timelineForecasts.Remove(id);
            ExpirePrediction(id, reason);
        }
    }

    private void ExpirePrediction(long id, string reason)
    {
        ++_predictionRevision;
        AuditPredictionOutcome(id, DecisionAuditStage.Expired, null, reason);
        _predictions.Remove(id);
    }

    private static bool IsSignalExcluded(EncounterMemory encounter, ForetellObservation observation)
        => encounter.ExcludedSignals.ContainsKey(SignalKey(observation));

    private static bool IsCombatSignal(ForetellObservation observation)
        => (observation.Kind != ObservationKind.NativeVFXSpawn || observation.ActorID != 0 || observation.TargetID != 0)
            && observation.SourceKind is SourceKind.Enemy or SourceKind.EventObject or SourceKind.Environment
            && observation.Kind is ObservationKind.CastStart or ObservationKind.ActionResolved or ObservationKind.Icon or ObservationKind.VFX
                or ObservationKind.TetherStart or ObservationKind.EventObjectState or ObservationKind.EventObjectAnimation
                or ObservationKind.ActionTimelineEvent or ObservationKind.MapEffect or ObservationKind.LegacyMapEffect or ObservationKind.DirectorUpdate
                or ObservationKind.ObjectEffect or ObservationKind.NativeVFXSpawn;

    private static bool IsTimelineSignal(ForetellObservation observation)
        => observation.Kind is ObservationKind.CastStart or ObservationKind.Icon or ObservationKind.VFX or ObservationKind.TetherStart or ObservationKind.TetherEnd
            or ObservationKind.StatusGain or ObservationKind.StatusLose or ObservationKind.ActorControlRaw
            or ObservationKind.EventObjectState or ObservationKind.EventObjectAnimation or ObservationKind.ActionTimelineEvent or ObservationKind.ActionTimelineSync
            or ObservationKind.NpcYell or ObservationKind.MapEffect or ObservationKind.LegacyMapEffect or ObservationKind.DirectorUpdate
            or ObservationKind.ObjectEffect or ObservationKind.NativeVFXSpawn or ObservationKind.DutyStarted or ObservationKind.DutyWiped
            or ObservationKind.DutyRecommenced or ObservationKind.DutyCompleted or ObservationKind.SystemLog
            or ObservationKind.TargetableChanged or ObservationKind.ModelStateChanged;

    private static bool IsPredictiveTimelineSignal(ForetellObservation observation)
        => IsTimelineSignal(observation)
            && observation.SourceKind is not SourceKind.Player and not SourceKind.Pet
            && (observation.ActorOID != 0 || observation.SourceKind == SourceKind.Environment);

    private void LearnPhaseBoundary(ForetellObservation observation)
    {
        if (observation.Kind is ObservationKind.DutyStarted or ObservationKind.DutyWiped or ObservationKind.DutyRecommenced or ObservationKind.DutyCompleted)
        {
            ClearDynamicTerrainWarnings();
            EndCombatPull();
            ResetHazardContext(cancelForecasts: true);
            return;
        }
        if (observation.Kind == ObservationKind.TargetableChanged && observation.ActorID != 0)
        {
            if (!_inPull) return;
            if (!observation.Flag)
                _untargetableSince[observation.ActorID] = observation.At;
            else if (_untargetableSince.Remove(observation.ActorID, out var since) && (observation.At - since).TotalSeconds >= 1)
                ObservePhaseBoundary(observation, $"targetable:{observation.ActorOID:X}");
            return;
        }
        if (!_inPull) return;
        var signature = observation.Kind switch
        {
            ObservationKind.DirectorUpdate => $"director:{observation.PrimaryID:X}:{observation.SecondaryID:X}",
            ObservationKind.ModelStateChanged when observation.SourceKind == SourceKind.Enemy => $"model:{observation.ActorOID:X}:{observation.PrimaryID:X}:{observation.SecondaryID:X}",
            // A rolling radar window or a changed visibility polygon is not an encounter phase transition.
            _ => ""
        };
        if (signature.Length != 0) ObservePhaseBoundary(observation, signature);
    }

    private static float FiniteOrZero(float value) => float.IsFinite(value) ? value : 0;

    private void ObservePhaseBoundary(ForetellObservation observation, string signature)
    {
        if (_phaseBoundariesThisPull.Contains(signature)) return;
        if (_pullStartedAt != default && (observation.At - _pullStartedAt).TotalSeconds < 3)
        {
            // Director/model noise emitted while a pull is being initialized is not an encounter phase.
            _phaseBoundariesThisPull.Add(signature);
            return;
        }
        var encounter = _store.Encounters.GetValueOrDefault(observation.TerritoryID);
        if (encounter == null)
        {
            if (!_cfg.EnableLearning) { _phaseBoundariesThisPull.Add(signature); return; }
            encounter = Encounter(observation.TerritoryID);
        }
        if (!_cfg.EnableLearning)
        {
            if (encounter.PhaseBoundaries.TryGetValue(signature, out var learned) && learned.Accepted)
                AdvanceLearnedPhase(observation, signature);
            else
                _phaseBoundariesThisPull.Add(signature);
            return;
        }
        if (!encounter.PhaseBoundaries.TryGetValue(signature, out var candidate))
        {
            if (encounter.PhaseBoundaries.Count >= 512)
            {
                var weakest = encounter.PhaseBoundaries.OrderBy(item => item.Value.Accepted)
                    .ThenBy(item => item.Value.PullsSeen).ThenBy(item => item.Value.Seen).ThenBy(item => item.Value.LastSeen).First();
                encounter.PhaseBoundaries.Remove(weakest.Key);
                ++_learningEvictions;
            }
            encounter.PhaseBoundaries[signature] = candidate = new() { Signature = signature, EvidenceKind = observation.Kind };
        }
        ++candidate.Seen;
        candidate.LastSeen = LearningNow;
        if (candidate.LastPull != encounter.Pulls)
        {
            candidate.LastPull = encounter.Pulls;
            ++candidate.PullsSeen;
        }
        // Structural changes only become phase boundaries after recurring in separate pulls. This avoids turning
        // one-off targetability, model or director noise into dozens of fictitious phases.
        candidate.Accepted |= candidate.PullsSeen >= 2;
        if (candidate.Accepted)
            AdvanceLearnedPhase(observation, signature);
        else
            _phaseBoundariesThisPull.Add(signature);
    }

    private void AdvanceLearnedPhase(ForetellObservation observation, string signature)
    {
        if (signature == _lastPhaseBoundarySignal && (observation.At - _lastPhaseBoundary).TotalSeconds < 10) return;
        if ((observation.At - _lastPhaseBoundary).TotalSeconds < 3) return;
        var previousPhase = _session.Phase;
        _lastPhaseBoundary = observation.At;
        _lastPhaseBoundarySignal = signature;
        _phaseBoundariesThisPull.Add(signature);
        ++_session.Phase;
        _phaseStartedAt = observation.At;
        _previousSignal = "";
        _previousSignalTime = default;
        CancelTimelineForecasts(previousPhase);
        _skippedTriggerContextsThisPull.Clear();
        RefreshTriggerForecastCandidates(_store.Encounters.GetValueOrDefault(observation.TerritoryID));
        _retryTriggerForecastCandidates = _triggerForecastCandidates.Count == 0;
    }

    private static bool IsEpisodeTrigger(ForetellObservation observation)
        => ForetellInferenceCore.CanStartMechanicEpisode(observation.Kind, observation.SourceKind,
            observation.ActorID, observation.ActorOID, observation.TargetID);

    private static string SignalKey(ForetellObservation observation)
        => $"{observation.ActorOID:X}:{observation.Kind}:{observation.PrimaryID:X}";

    private void LearnSignalTimeline(EncounterMemory encounter, ForetellObservation observation)
    {
        var signal = SignalKey(observation);
        var timelinePhase = TimelinePhaseFor(observation);
        if (!encounter.Phases.TryGetValue(timelinePhase, out var phase))
            encounter.Phases[timelinePhase] = phase = new() { Phase = timelinePhase };
        ++phase.Seen;
        if (!phase.Signals.ContainsKey(signal) && phase.Signals.Count >= 2048)
        {
            var weakestSignal = phase.Signals.MinBy(item => item.Value).Key;
            phase.Signals.Remove(weakestSignal);
            ++_learningEvictions;
        }
        phase.Signals[signal] = phase.Signals.GetValueOrDefault(signal) + 1;

        if (!string.IsNullOrEmpty(_previousSignal) && _previousSignal != signal)
        {
            var key = $"{timelinePhase}:{_previousSignal}>{signal}";
            var dt = Math.Max(0, (observation.At - _previousSignalTime).TotalSeconds);
            if (!encounter.Timeline.TryGetValue(key, out var edge))
            {
                if (encounter.Timeline.Count >= 8192)
                {
                    var weakestEdge = encounter.Timeline.MinBy(item => (item.Value.Count, item.Value.Stability));
                    encounter.Timeline.Remove(weakestEdge.Key);
                    ++_learningEvictions;
                }
                encounter.Timeline[key] = edge = new() { From = _previousSignal, To = signal, Phase = timelinePhase };
            }
            ++edge.Count;
            var delta = dt - edge.MeanDelay;
            edge.MeanDelay += delta / edge.Count;
            edge.M2 += delta * (dt - edge.MeanDelay);
        }
        _previousSignal = signal;
        _previousSignalTime = observation.At;

        if (observation.Kind == ObservationKind.CastStart && observation.PrimaryID != 0)
            LearnLegacyTimeline(observation.PrimaryID, observation.At);
    }

    private static string TriggerContextKey(uint contextOID, int phase, string signal, int occurrence)
        => $"{contextOID:X}:{phase}:{occurrence}:{signal}";

    private static string SignalOccurrenceKey(int phase, string signal)
        => $"{phase}:{signal}";

    private void ObserveSignalTriggerContext(EncounterMemory encounter, ForetellObservation observation)
    {
        var phase = TimelinePhaseFor(observation);
        if (phase < 0 || _phaseStartedAt == default)
            return;
        var signal = SignalKey(observation);
        var occurrenceKey = SignalOccurrenceKey(phase, signal);
        var occurrence = _signalOccurrencesThisPull.GetValueOrDefault(occurrenceKey) + 1;
        _signalOccurrencesThisPull[occurrenceKey] = occurrence;
        if (!_cfg.EnableLearning || occurrence > 32 || (!IsEpisodeTrigger(observation) && !encounter.Mechanics.ContainsKey(signal)))
            return;

        var hasBossHealth = TryBossHealth(observation.ActorOID, observation.ActorID, observation.At,
            out var boss, out var hpRatio, out _);
        var contextOID = hasBossHealth ? boss.OID : ResolvePullContextOID(observation);
        if (contextOID == 0)
            return;
        if (_retryTriggerForecastCandidates)
        {
            RefreshTriggerForecastCandidates(encounter);
            _retryTriggerForecastCandidates = false;
        }
        var key = TriggerContextKey(contextOID, phase, signal, occurrence);
        if (!encounter.TriggerContexts.TryGetValue(key, out var memory))
        {
            if (encounter.TriggerContexts.Count >= 4096)
            {
                var weakest = encounter.TriggerContexts.MinBy(item => (item.Value.Samples, item.Value.HealthSamples, item.Value.LastSeen));
                encounter.TriggerContexts.Remove(weakest.Key);
                ++_learningEvictions;
            }
            encounter.TriggerContexts[key] = memory = new()
            {
                Key = key,
                Signal = signal,
                Phase = phase,
                Occurrence = occurrence,
                ContextOID = contextOID
            };
        }
        // One occurrence bucket is sampled at most once per pull. This keeps a packet duplicate or replayed
        // callback from manufacturing confidence without independent encounter repetitions.
        if (memory.LastPull == encounter.Pulls)
            return;
        memory.LastPull = encounter.Pulls;
        memory.LastSeen = LearningNow;
        var seconds = Math.Clamp((observation.At - _phaseStartedAt).TotalSeconds, 0, 1800);
        ++memory.Samples;
        var timeDelta = seconds - memory.MeanPhaseSeconds;
        memory.MeanPhaseSeconds += timeDelta / memory.Samples;
        memory.PhaseSecondsM2 += timeDelta * (seconds - memory.MeanPhaseSeconds);

        if (hasBossHealth)
        {
            memory.BossOID = boss.OID;
            ++memory.HealthSamples;
            var healthDelta = hpRatio - memory.MeanBossHPRatio;
            memory.MeanBossHPRatio += healthDelta / memory.HealthSamples;
            memory.BossHPRatioM2 += healthDelta * (hpRatio - memory.MeanBossHPRatio);
        }
    }

    private void RefreshTriggerForecastCandidates(EncounterMemory? encounter)
    {
        _triggerForecastCandidates.Clear();
        if (encounter == null || !_inPull)
            return;
        _triggerForecastCandidates.AddRange(encounter.TriggerContexts.Values
            .Where(memory => memory.Phase == _session.Phase && memory.Occurrence is >= 1 and <= 32
                && (memory.Samples >= 3 || memory.HealthSamples >= 3)
                && encounter.Mechanics.ContainsKey(memory.Signal)
                && !encounter.ExcludedSignals.ContainsKey(memory.Signal)
                && TriggerContextIsActive(memory, encounter.Mechanics[memory.Signal]))
            .OrderByDescending(memory => Math.Max(memory.TimeStability, memory.HealthStability))
            .ThenByDescending(memory => Math.Max(memory.Samples, memory.HealthSamples))
            .Take(256));
    }

    private uint ResolvePullContextOID(ForetellObservation observation)
    {
        if (observation.SourceKind == SourceKind.Enemy && observation.ActorOID != 0)
            return observation.ActorOID;
        var player = _ws.Party[PartyState.PlayerSlot];
        Actor? best = null;
        foreach (var actor in _ws.Actors)
        {
            if (actor.Type != ActorType.Enemy || actor.IsAlly || actor.IsDeadOrDestroyed || !actor.IsTargetable)
                continue;
            if (player != null && Vector2.Distance(V(actor.Position), V(player.Position)) > 80)
                continue;
            if (!actor.InCombat && !actor.AggroPlayer && actor.TargetID == 0)
                continue;
            if (best == null || actor.HPMP.MaxHP > best.HPMP.MaxHP)
                best = actor;
        }
        return best?.OID ?? 0;
    }

    private bool TriggerContextIsActive(SignalTriggerMemory memory, ContextualMechanic mechanic)
    {
        var contextOID = memory.ContextOID != 0 ? memory.ContextOID : memory.BossOID != 0 ? memory.BossOID : mechanic.SourceOID;
        if (contextOID == 0)
            return false;
        var player = _ws.Party[PartyState.PlayerSlot];
        foreach (var actor in _ws.Actors)
        {
            if (actor.OID != contextOID || actor.Type != ActorType.Enemy || actor.IsAlly || actor.IsDeadOrDestroyed || !actor.IsTargetable)
                continue;
            if (player != null && Vector2.Distance(V(actor.Position), V(player.Position)) > 80)
                continue;
            if (actor.InCombat || actor.AggroPlayer || actor.TargetID != 0)
                return true;
        }
        return false;
    }

    private void UpdateTriggerContextForecasts(DateTime now)
    {
        if (!_inPull || _phaseStartedAt == default || now < _phaseStartedAt)
            return;
        if (_lastContextForecastSample != default && (now - _lastContextForecastSample).TotalMilliseconds < 200)
            return;
        _lastContextForecastSample = now;
        if (!_store.Encounters.TryGetValue(_territory, out var encounter))
            return;
        _bossHealthSnapshots.Clear();

        foreach (var memory in _triggerForecastCandidates)
        {
            if (_skippedTriggerContextsThisPull.Contains(memory.Key))
                continue;
            var occurrence = _signalOccurrencesThisPull.GetValueOrDefault(SignalOccurrenceKey(memory.Phase, memory.Signal));
            if (memory.Occurrence != occurrence + 1)
                continue;
            if (_timelineForecasts.Values.Any(forecast => forecast.Phase == memory.Phase
                && forecast.ExpectedSignal == memory.Signal && now <= forecast.Expires))
                continue;
            if (!encounter.Mechanics.TryGetValue(memory.Signal, out var mechanic))
                continue;

            if (memory.PreferHealth)
            {
                if (!_bossHealthSnapshots.TryGetValue(memory.BossOID, out var health))
                {
                    health = TryBossHealth(memory.BossOID, 0, now, out var boss, out var ratio, out var lossPerSecond)
                        ? new BossHealthSnapshot(boss, ratio, lossPerSecond)
                        : null;
                    _bossHealthSnapshots[memory.BossOID] = health;
                }
                if (health is { } snapshot)
                    TryScheduleHealthTriggerForecast(encounter, memory, mechanic, now, snapshot);
            }
            else
                TryScheduleTimeTriggerForecast(encounter, memory, mechanic, now);
        }
    }

    private void TryScheduleTimeTriggerForecast(EncounterMemory encounter, SignalTriggerMemory memory,
        ContextualMechanic mechanic, DateTime now)
    {
        var confidence = ForetellInferenceCore.TriggerForecastConfidence(memory.Samples, memory.TimeStability,
            memory.TimeHits, memory.TimeMisses);
        if (confidence <= 0)
            return;
        var expected = _phaseStartedAt.AddSeconds(memory.MeanPhaseSeconds);
        var tolerance = Math.Clamp(memory.PhaseSecondsStdDev * 2 + 1, 1.5, 10);
        if (now > expected.AddSeconds(tolerance))
        {
            _skippedTriggerContextsThisPull.Add(memory.Key);
            return;
        }
        if ((expected - now).TotalSeconds > 12)
            return;
        var activation = expected < now ? now : expected;
        ScheduleTriggerContextForecast(encounter, memory, mechanic, activation, expected.AddSeconds(tolerance),
            PredictiveTriggerBasis.PhaseClock, confidence, null, now,
            $"phase clock T+{memory.MeanPhaseSeconds:F1}s +/- {memory.PhaseSecondsStdDev:F1}s; {memory.Samples} pulls");
    }

    private void TryScheduleHealthTriggerForecast(EncounterMemory encounter, SignalTriggerMemory memory,
        ContextualMechanic mechanic, DateTime now, BossHealthSnapshot health)
    {
        var confidence = ForetellInferenceCore.TriggerForecastConfidence(memory.HealthSamples, memory.HealthStability,
            memory.HealthHits, memory.HealthMisses);
        if (confidence <= 0)
            return;
        var boss = health.Boss;
        var currentRatio = health.Ratio;
        var lossPerSecond = health.LossPerSecond;
        var threshold = Math.Clamp(memory.MeanBossHPRatio, 0, 1);
        var passedTolerance = Math.Max(.015, memory.BossHPRatioStdDev * 2);
        if (currentRatio < threshold - passedTolerance)
        {
            _skippedTriggerContextsThisPull.Add(memory.Key);
            return;
        }

        double eta;
        if (currentRatio <= threshold)
            eta = 0;
        else if (lossPerSecond > .00001)
            eta = (currentRatio - threshold) / lossPerSecond;
        else if (currentRatio - threshold <= .015)
            eta = 0;
        else
            return;
        if (!double.IsFinite(eta) || eta > 12)
            return;
        eta = Math.Clamp(eta, 0, 12);
        var activation = now.AddSeconds(eta);
        var hpTimingTolerance = lossPerSecond > .00001 ? memory.BossHPRatioStdDev / lossPerSecond : 2;
        var tolerance = Math.Clamp(hpTimingTolerance * 2 + 1, 2, 10);
        ScheduleTriggerContextForecast(encounter, memory, mechanic, activation, activation.AddSeconds(tolerance),
            PredictiveTriggerBasis.BossHealth, confidence, boss, now,
            $"boss HP {threshold:P1} +/- {memory.BossHPRatioStdDev:P1}; current {currentRatio:P1}; ETA {eta:F1}s; {memory.HealthSamples} pulls");
    }

    private void ScheduleTriggerContextForecast(EncounterMemory encounter, SignalTriggerMemory memory,
        ContextualMechanic mechanic, DateTime activation, DateTime expires, PredictiveTriggerBasis basis,
        float confidence, Actor? boss, DateTime now, string evidence)
    {
        var actor = boss ?? (mechanic.SourceOID == 0 ? null : _ws.Actors.FirstOrDefault(candidate => candidate.OID == mechanic.SourceOID));
        var trigger = new ForetellObservation
        {
            At = now,
            TerritoryID = encounter.TerritoryID,
            Kind = mechanic.TriggerKind,
            SourceKind = mechanic.SourceKind,
            ActorID = actor?.InstanceID ?? 0,
            ActorOID = mechanic.SourceOID,
            PrimaryID = mechanic.TriggerID,
            X = actor?.Position.X ?? 0,
            Z = actor?.Position.Z ?? 0,
            Rotation = actor?.Rotation.Rad ?? 0,
            Detail = mechanic.TriggerDetail
        };
        if (BuildMechanicPrediction(mechanic, trigger, activation, anticipated: true) is not ActivePrediction prediction)
            return;
        var id = _nextForecastID--;
        if (_nextForecastID == long.MinValue) _nextForecastID = -1;
        var forecastPrediction = prediction with
        {
            Confidence = Math.Min(prediction.Confidence, confidence),
            Evidence = $"{(basis == PredictiveTriggerBasis.BossHealth ? "HP-threshold" : "absolute-time")} forecast; {evidence}"
        };
        StorePrediction(id, forecastPrediction, trigger);
        _timelineForecasts[id] = new()
        {
            OutcomeGapGeneration = _outcomeGapGeneration,
            ID = id,
            TerritoryID = encounter.TerritoryID,
            Phase = memory.Phase,
            TriggerContextKey = memory.Key,
            TriggerBasis = basis,
            ExpectedSignal = memory.Signal,
            MechanicKey = mechanic.Key,
            Due = activation,
            Expires = expires
        };
        if (_cfg.EnableLearning)
        {
            if (basis == PredictiveTriggerBasis.BossHealth) ++memory.HealthForecasts;
            else ++memory.TimeForecasts;
        }
    }

    private bool TryBossHealth(uint preferredOID, ulong preferredInstanceID, DateTime now,
        out Actor boss, out double ratio, out double lossPerSecond)
    {
        boss = null!;
        ratio = 0;
        lossPerSecond = 0;
        if (_isReplay)
        {
            var recorded = _ws.Actors.Find(_decisionContext?.BossID ?? 0);
            if (recorded == null || recorded.HPMP.MaxHP == 0) return false;
            boss = recorded;
            ratio = recorded.HPMP.CurHP / (double)recorded.HPMP.MaxHP;
            if (!_bossHealthTracks.TryGetValue(recorded.InstanceID, out var recordedTrack))
                _bossHealthTracks[recorded.InstanceID] = recordedTrack = new();
            recordedTrack.Update(now, ratio);
            lossPerSecond = recordedTrack.LossPerSecond;
            return true;
        }
        // HP thresholds are boss-only evidence. A large trash mob in a corridor must never become a fake HP gate;
        // require the independently observed arena boundary and the same boss-candidate test used by the radar.
        if (CurrentArenaBoundary is not { ArenaLike: true } boundary)
            return false;
        var summary = ArenaEnemySummary(boundary);
        if (!summary.HasBossCandidate)
            return false;
        Actor? best = null;
        foreach (var actor in _ws.Actors)
        {
            if (!LiveArenaEnemy(actor, boundary))
                continue;
            if (!IsBossCandidate(actor, summary.MaximumHP, summary.PlayerMaximumHP))
                continue;
            var preferred = preferredInstanceID != 0 && actor.InstanceID == preferredInstanceID
                || preferredOID != 0 && actor.OID == preferredOID;
            if (preferred)
            {
                best = actor;
                break;
            }
            if (best == null || actor.HPMP.MaxHP > best.HPMP.MaxHP || actor.HPMP.MaxHP == best.HPMP.MaxHP && actor.HPMP.CurHP > best.HPMP.CurHP)
                best = actor;
        }
        if (best == null)
            return false;
        boss = best;
        ratio = Math.Clamp(best.HPMP.CurHP / (double)Math.Max(1u, best.HPMP.MaxHP), 0, 1);
        if (!_bossHealthTracks.TryGetValue(best.InstanceID, out var track))
            _bossHealthTracks[best.InstanceID] = track = new();
        track.Update(now, ratio);
        lossPerSecond = track.LossPerSecond;
        return true;
    }

    private void ScheduleTimelineForecast(EncounterMemory encounter, ForetellObservation observation)
    {
        var current = SignalKey(observation);
        var timelinePhase = TimelinePhaseFor(observation);
        var outgoing = encounter.Timeline.Values.Where(edge => edge.Phase == timelinePhase && edge.From == current && edge.Count >= 3
            && edge.MeanDelay is >= .15 and <= 120 && edge.Stability >= .45f).ToArray();
        if (outgoing.Length == 0) return;
        var edge = outgoing.OrderByDescending(candidate => ForetellInferenceCore.TimelineProbability(candidate, outgoing) * candidate.Stability)
            .ThenByDescending(candidate => candidate.Count).First();
        var probability = ForetellInferenceCore.TimelineProbability(edge, outgoing);
        if (probability < .55f) return; // branch is genuinely ambiguous: abstain instead of guessing
        if (!encounter.Mechanics.TryGetValue(edge.To, out var mechanic)) return;
        var due = observation.At.AddSeconds(edge.MeanDelay);
        if (_timelineForecasts.Values.Any(forecast => forecast.Phase == timelinePhase && forecast.ExpectedSignal == edge.To && Math.Abs((forecast.Due - due).TotalSeconds) < .5))
            return;

        var actor = mechanic.SourceOID == 0 ? null : _ws.Actors.FirstOrDefault(candidate => candidate.OID == mechanic.SourceOID);
        var trigger = new ForetellObservation
        {
            At = observation.At,
            TerritoryID = encounter.TerritoryID,
            Kind = mechanic.TriggerKind,
            SourceKind = mechanic.SourceKind,
            ActorID = actor?.InstanceID ?? 0,
            ActorOID = mechanic.SourceOID,
            PrimaryID = mechanic.TriggerID,
            X = actor?.Position.X ?? observation.X,
            Z = actor?.Position.Z ?? observation.Z,
            Rotation = actor?.Rotation.Rad ?? observation.Rotation,
            Detail = mechanic.TriggerDetail
        };
        var prediction = BuildMechanicPrediction(mechanic, trigger, due, anticipated: true);
        if (prediction == null) return;
        var id = _nextForecastID--;
        if (_nextForecastID == long.MinValue) _nextForecastID = -1;
        var transitionReliability = edge.Hits + edge.Misses >= 3
            ? edge.ForecastReliability
            : Math.Min(.94f, probability * edge.Stability);
        var forecastPrediction = prediction.Value with
        {
            Confidence = Math.Min(prediction.Value.Confidence, transitionReliability),
            Evidence = $"timeline {edge.From} -> {edge.To}; branch {probability:P0}; stability {edge.Stability:P0}; {edge.Count} observations"
        };
        StorePrediction(id, forecastPrediction, trigger);
        var tolerance = Math.Clamp(edge.StdDev * 2 + .75, 1, 8);
        var edgeKey = $"{edge.Phase}:{edge.From}>{edge.To}";
        _timelineForecasts[id] = new()
        {
            OutcomeGapGeneration = _outcomeGapGeneration,
            ID = id,
            TerritoryID = encounter.TerritoryID,
            Phase = timelinePhase,
            EdgeKey = edgeKey,
            ExpectedSignal = edge.To,
            MechanicKey = mechanic.Key,
            Due = due,
            Expires = due.AddSeconds(tolerance)
        };
        if (_cfg.EnableLearning) ++edge.Forecasts;
    }

    private void ResolveTimelineForecasts(EncounterMemory encounter, ForetellObservation observation)
    {
        var signal = SignalKey(observation);
        var timelinePhase = TimelinePhaseFor(observation);
        foreach (var forecast in _timelineForecasts.Values.Where(candidate => candidate.TerritoryID == encounter.TerritoryID
            && candidate.Phase == timelinePhase && candidate.ExpectedSignal == signal && observation.At <= candidate.Expires).ToArray())
        {
            var verified = ForetellOutcomeValidation.VerifyTiming(forecast.Due, forecast.Expires, observation.At,
                forecast.OutcomeGapGeneration == _outcomeGapGeneration);
            if (_cfg.EnableLearning && verified is bool success)
            {
                if (!string.IsNullOrEmpty(forecast.EdgeKey) && encounter.Timeline.TryGetValue(forecast.EdgeKey, out var edge))
                { if (success) ++edge.Hits; else ++edge.Misses; }
                if (!string.IsNullOrEmpty(forecast.CompositeKey) && encounter.Composites.TryGetValue(forecast.CompositeKey, out var composite))
                { if (success) ++composite.Hits; else ++composite.Misses; }
                if (!string.IsNullOrEmpty(forecast.TriggerContextKey) && encounter.TriggerContexts.TryGetValue(forecast.TriggerContextKey, out var trigger))
                {
                    if (forecast.TriggerBasis == PredictiveTriggerBasis.BossHealth) { if (success) ++trigger.HealthHits; else ++trigger.HealthMisses; }
                    else if (forecast.TriggerBasis == PredictiveTriggerBasis.PhaseClock) { if (success) ++trigger.TimeHits; else ++trigger.TimeMisses; }
                }
            }
            AuditPredictionOutcome(forecast.ID, DecisionAuditStage.Verified, verified, $"expected signal observed: {signal}; timing and capture checked");
            _timelineForecasts.Remove(forecast.ID);
            _predictions.Remove(forecast.ID);
        }
    }

    private void ExpireTimelineForecasts(DateTime now)
    {
        foreach (var forecast in _timelineForecasts.Values.Where(candidate => candidate.Expires < now).ToArray())
        {
            var complete = forecast.OutcomeGapGeneration == _outcomeGapGeneration;
            if (complete && _store.Encounters.TryGetValue(forecast.TerritoryID, out var encounter))
            {
                if (_cfg.EnableLearning && !string.IsNullOrEmpty(forecast.EdgeKey) && encounter.Timeline.TryGetValue(forecast.EdgeKey, out var edge)) ++edge.Misses;
                if (_cfg.EnableLearning && !string.IsNullOrEmpty(forecast.CompositeKey) && encounter.Composites.TryGetValue(forecast.CompositeKey, out var composite)) ++composite.Misses;
                if (_cfg.EnableLearning && !string.IsNullOrEmpty(forecast.TriggerContextKey) && encounter.TriggerContexts.TryGetValue(forecast.TriggerContextKey, out var trigger))
                {
                    if (forecast.TriggerBasis == PredictiveTriggerBasis.BossHealth) ++trigger.HealthMisses;
                    else if (forecast.TriggerBasis == PredictiveTriggerBasis.PhaseClock) ++trigger.TimeMisses;
                }
            }
            AuditPredictionOutcome(forecast.ID, DecisionAuditStage.Expired, complete ? false : null, $"expected signal not observed before {forecast.Expires:u}");
            _timelineForecasts.Remove(forecast.ID);
            _predictions.Remove(forecast.ID);
        }
    }

    private void ScheduleCompositeForecasts(EncounterMemory encounter, ForetellObservation observation)
    {
        var current = SignalKey(observation);
        var timelinePhase = TimelinePhaseFor(observation);
        foreach (var composite in encounter.Composites.Values.Where(candidate => candidate.Phase == timelinePhase
            && candidate.Count >= 3 && candidate.Stability >= .45f && candidate.Signals.Contains(current)).Take(8))
        {
            var baseReliability = composite.Hits + composite.Misses >= 3
                ? composite.ForecastReliability
                : Math.Min(.94f, (1f - MathF.Exp(-composite.Count / 4f)) * composite.Stability);
            if (baseReliability < .35f) continue;
            foreach (var expected in composite.Signals.Where(signal => signal != current).Take(8))
            {
                if (_timelineForecasts.Values.Any(pending => pending.Phase == timelinePhase && pending.ExpectedSignal == expected && observation.At <= pending.Expires)) continue;
                if (!encounter.Mechanics.TryGetValue(expected, out var mechanic)) continue;
                var actor = mechanic.SourceOID == 0 ? null : _ws.Actors.FirstOrDefault(candidate => candidate.OID == mechanic.SourceOID);
                var trigger = new ForetellObservation
                {
                    At = observation.At,
                    TerritoryID = encounter.TerritoryID,
                    Kind = mechanic.TriggerKind,
                    SourceKind = mechanic.SourceKind,
                    ActorID = actor?.InstanceID ?? 0,
                    ActorOID = mechanic.SourceOID,
                    PrimaryID = mechanic.TriggerID,
                    X = actor?.Position.X ?? observation.X,
                    Z = actor?.Position.Z ?? observation.Z,
                    Rotation = actor?.Rotation.Rad ?? observation.Rotation,
                    Detail = mechanic.TriggerDetail
                };
                var due = observation.At.AddSeconds(Math.Clamp(composite.MeanSkewSeconds * .5, .05, .75));
                if (BuildMechanicPrediction(mechanic, trigger, due, anticipated: true) is not ActivePrediction prediction) continue;
                var id = _nextForecastID--;
                if (_nextForecastID == long.MinValue) _nextForecastID = -1;
                var forecastPrediction = prediction with
                {
                    Confidence = Math.Min(prediction.Confidence, baseReliability),
                    Evidence = $"simultaneous pattern {composite.Key}; stability {composite.Stability:P0}; {composite.Count} observations"
                };
                StorePrediction(id, forecastPrediction, trigger);
                _timelineForecasts[id] = new()
                {
                    OutcomeGapGeneration = _outcomeGapGeneration,
                    ID = id,
                    TerritoryID = encounter.TerritoryID,
                    Phase = timelinePhase,
                    CompositeKey = composite.Key,
                    ExpectedSignal = expected,
                    MechanicKey = mechanic.Key,
                    Due = due,
                    Expires = due.AddSeconds(Math.Clamp(composite.StdDev * 2 + .75, 1, 3))
                };
                if (_cfg.EnableLearning) ++composite.Forecasts;
            }
        }
    }

    private void TrackSignalState(ForetellObservation observation)
    {
        _previousSignal = SignalKey(observation);
        _previousSignalTime = observation.At;
        if (observation.Kind == ObservationKind.CastStart && observation.PrimaryID != 0)
        {
            _previousAction = observation.PrimaryID;
            _previousActionTime = observation.At;
        }
    }

    private void LearnLegacyTimeline(uint action, DateTime now)
    {
        if (_previousAction != 0 && _previousAction != action)
        {
            var key = $"{_previousAction}>{action}";
            var dt = Math.Max(0, (now - _previousActionTime).TotalSeconds);
            if (!_store.Timeline.TryGetValue(key, out var edge))
            {
                if (_store.Timeline.Count >= 8192)
                {
                    _store.Timeline.Remove(_store.Timeline.MinBy(item => item.Value.Count).Key);
                    ++_learningEvictions;
                }
                _store.Timeline[key] = edge = new() { From = _previousAction, To = action };
            }
            ++edge.Count;
            var delta = dt - edge.MeanDelay;
            edge.MeanDelay += delta / edge.Count;
            edge.M2 += delta * (dt - edge.MeanDelay);
        }
        _previousAction = action;
        _previousActionTime = now;
    }

    private void StartEpisode(ForetellObservation trigger, EncounterMemory? encounter)
    {
        if (!IsEpisodeTrigger(trigger))
            return;
        // Correlation is intentionally linear over the bounded live set. A large combat pack used to grow this
        // to 512 and scan it twice for every target/effect callback, producing catastrophic frame loss.
        const int maxLiveEpisodes = 64;
        if (_episodes.Values.Any(e => !e.Finalized && e.Trigger.ActorID == trigger.ActorID && e.Trigger.Kind == trigger.Kind &&
            e.Trigger.PrimaryID == trigger.PrimaryID && Math.Abs((e.Trigger.At - trigger.At).TotalSeconds) < .6))
            return;
        if (_episodes.Count >= maxLiveEpisodes)
        {
            var disposable = _episodes.Values.Where(episode => episode.Finalized).MinBy(episode => episode.FinalizeAt);
            if (disposable != null) RemoveEpisode(disposable.ID);
        }
        if (_episodes.Count >= maxLiveEpisodes)
        {
            ++_episodeRejections;
            return;
        }

        ContextualMechanic? contextual = null;
        if (encounter != null)
            encounter.Mechanics.TryGetValue(SignalKey(trigger), out contextual);
        var learnedLead = contextual?.MeanLeadSeconds ?? 0;
        var lead = trigger.Kind == ObservationKind.CastStart && float.IsFinite(trigger.Value1)
            ? Math.Clamp(trigger.Value1, 0, 120)
            : trigger.Kind == ObservationKind.Icon && learnedLead <= .25
                ? 10
                : Math.Clamp(learnedLead, 0, 120);
        var episode = new MechanicEpisode
        {
            ID = trigger.Sequence,
            OutcomeGapGeneration = _outcomeGapGeneration,
            Trigger = trigger,
            LeadSeconds = lead,
            Activation = trigger.At.AddSeconds(lead),
            FinalizeAt = trigger.At.AddSeconds(lead + 12)
        };
        foreach (var (id, track) in _tracks)
        {
            if (Math.Abs((track.At - trigger.At).TotalSeconds) > 2)
                continue;
            var pose = track.Nearest(episode.Activation);
            episode.ParticipantPositions[id] = pose.Position;
            episode.ParticipantRotations[id] = pose.Rotation;
            episode.ParticipantRoles[id] = track.Role;
            episode.ParticipantRoleNames[id] = track.RoleName;
        }
        episode.AddEvidence(trigger.Kind);
        episode.PreImpactFeatures = ForetellPreImpactModel.Features(trigger, episode.ParticipantPositions.Values, _precursorCues);
        episode.PreImpactGuess = _preImpact.Predict(episode.PreImpactFeatures);
        _episodes[episode.ID] = episode;
        AddDecisionAudit(new()
        {
            At = trigger.At,
            Activation = episode.Activation,
            PredictionID = episode.ID,
            Stage = DecisionAuditStage.Detected,
            SignalKey = episode.SignalKey,
            TriggerKind = trigger.Kind,
            TriggerID = trigger.PrimaryID,
            TriggerDetail = trigger.Detail,
            SourceKind = trigger.SourceKind,
            SourceOID = trigger.ActorOID,
            OriginX = trigger.X,
            OriginZ = trigger.Z,
            TargetX = trigger.TargetX,
            TargetZ = trigger.TargetZ,
            Rotation = trigger.Rotation,
            Label = ObservationLabel(trigger.Kind),
            Evidence = "accepted mechanic trigger"
        });
        _episodeFinalization.Enqueue(episode.ID, episode.FinalizeAt.Ticks);
        if (_cfg.EnableLearning && encounter != null)
            LearnCompositeMechanics(encounter, episode);

        if (contextual != null && (contextual.Geometry != GeometryKind.Unknown || ForetellInferenceCore.GuidanceFor(contextual.Kind) != GuidanceKind.None))
        {
            IssueMechanicPrediction(episode, contextual, trigger, anticipated: false);
        }
        else if (trigger.Kind == ObservationKind.CastStart && _store.Mechanics.TryGetValue(trigger.PrimaryID, out var fallback)
            && fallback.Geometry != GeometryKind.Unknown)
        {
            var target = new Vector2(trigger.TargetX, trigger.TargetZ);
            var source = new Vector2(trigger.X, trigger.Z);
            var origin = fallback.Geometry is GeometryKind.Circle or GeometryKind.Donut ? target : source;
            var prediction = new ActivePrediction(trigger.ActorID, trigger.PrimaryID, fallback.Geometry, fallback.Kind, origin, target, trigger.Rotation,
                fallback.P1, fallback.P2, episode.Activation, Math.Min(fallback.Confidence, .94f), $"global fallback; {fallback.Observations} observations",
                episode.SignalKey, trigger.TargetID, GuidanceKind.Avoid, false, LookupActionName(trigger.PrimaryID) ?? $"Action 0x{trigger.PrimaryID:X}");
            StorePrediction(episode.ID, prediction, trigger);
            episode.ForecastIssued = true;
            episode.ForecastGeometry = fallback.Geometry;
            episode.ForecastKind = fallback.Kind;
            episode.ForecastP1 = fallback.P1;
            episode.ForecastP2 = fallback.P2;
            episode.ForecastConfidence = Math.Min(fallback.Confidence, .94f);
        }
        else if (trigger.Kind == ObservationKind.Icon && trigger.TargetID != 0)
        {
            var target = new Vector2(trigger.TargetX, trigger.TargetZ);
            var prediction = new ActivePrediction(0, trigger.PrimaryID, GeometryKind.Unknown, MechanicKind.Marker,
                target, target, 0, 0, 0, episode.Activation, .90f,
                "Observed target marker; stack/spread/targeted meaning remains unclaimed until outcomes disambiguate it",
                episode.SignalKey, trigger.TargetID, GuidanceKind.Marker, false, "Target marker");
            StorePrediction(episode.ID, prediction, trigger);
            episode.ForecastIssued = true;
            episode.ForecastKind = MechanicKind.Marker;
            episode.ForecastConfidence = .90f;
        }
        if (_cfg.EnableML && !_predictions.ContainsKey(episode.ID) && episode.LeadSeconds >= .35
            && episode.PreImpactGuess.Reliability >= _cfg.VisualConfidence / 100f && episode.PreImpactGuess.Probability >= .8f
            && ForetellInferenceCore.GuidanceFor(episode.PreImpactGuess.Kind) is var learnedGuidance && learnedGuidance != GuidanceKind.None)
        {
            var confidence = Math.Min(episode.PreImpactGuess.Reliability, episode.PreImpactGuess.Probability);
            var prediction = new ActivePrediction(trigger.ActorID, trigger.PrimaryID, GeometryKind.Unknown, episode.PreImpactGuess.Kind,
                new(trigger.X, trigger.Z), new(trigger.TargetX, trigger.TargetZ), trigger.Rotation, 0, 0, episode.Activation, confidence,
                "Pre-impact features; independently assessed chronological model", episode.SignalKey, trigger.TargetID, learnedGuidance, true,
                "Predicted mechanic") { Provenance = "Pre-impact model" };
            StorePrediction(episode.ID, prediction, trigger);
            episode.ForecastIssued = true;
            episode.ForecastKind = prediction.Kind;
            episode.ForecastConfidence = confidence;
        }
    }

    private void IssueMechanicPrediction(MechanicEpisode episode, ContextualMechanic mechanic, ForetellObservation trigger, bool anticipated)
    {
        var prediction = BuildMechanicPrediction(mechanic, trigger, episode.Activation, anticipated);
        if (prediction is not ActivePrediction value)
            return;
        StorePrediction(episode.ID, value, trigger);
        episode.ForecastIssued = true;
        episode.ForecastAnticipated = anticipated;
        episode.ForecastGeometry = mechanic.Geometry;
        episode.ForecastKind = mechanic.Kind;
        episode.ForecastP1 = mechanic.P1;
        episode.ForecastP2 = mechanic.P2;
        episode.ForecastConfidence = mechanic.GuidanceConfidence;
        episode.ForecastOrigin = value.Origin;
        episode.ForecastRotation = value.Rotation;
    }

    private ActivePrediction? BuildMechanicPrediction(ContextualMechanic mechanic, ForetellObservation trigger, DateTime activation, bool anticipated)
    {
        var guidance = ForetellInferenceCore.GuidanceFor(mechanic.Kind);
        if (mechanic.Geometry == GeometryKind.Unknown && guidance == GuidanceKind.None)
            return null;

        var source = new Vector2(trigger.X, trigger.Z);
        if ((!float.IsFinite(source.X) || !float.IsFinite(source.Y) || source == Vector2.Zero) && trigger.ActorID != 0 && _ws.Actors.Find(trigger.ActorID) is { } actor)
            source = V(actor.Position);
        var target = new Vector2(trigger.TargetX, trigger.TargetZ);
        if ((!float.IsFinite(target.X) || !float.IsFinite(target.Y) || target == Vector2.Zero) && trigger.TargetID != 0 && _ws.Actors.Find(trigger.TargetID) is { } targetActor)
            target = V(targetActor.Position);
        if (target == Vector2.Zero && mechanic.AnchorSamples > 0)
        {
            var f = new Vector2(MathF.Sin(trigger.Rotation), MathF.Cos(trigger.Rotation));
            var r = new Vector2(MathF.Cos(trigger.Rotation), -MathF.Sin(trigger.Rotation));
            target = source + f * (float)mechanic.MeanAnchorForward + r * (float)mechanic.MeanAnchorSide;
        }
        if (target == Vector2.Zero) target = source;
        var geometry = !mechanic.HasReliableActionPrior && mechanic.GeometryAmbiguous ? GeometryKind.Unknown : mechanic.Geometry;
        // A learned target-relative offset is only safe to draw ahead of the trigger once repeated samples agree.
        // The textual/non-spatial warning remains useful, but an unstable spatial guess is deliberately hidden.
        if (anticipated && mechanic.OriginKind == PredictionOriginKind.Target
            && (mechanic.AnchorSamples < 3 || mechanic.AnchorStdDev > 3))
            geometry = GeometryKind.Unknown;
        var origin = mechanic.OriginKind == PredictionOriginKind.Target ? target : source;
        var confidence = mechanic.GuidanceConfidence;
        if (!anticipated && trigger.Kind == ObservationKind.Icon && mechanic.Kind == MechanicKind.Marker)
            confidence = Math.Max(confidence, .90f);
        var evidence = $"{(anticipated ? "timeline forecast" : "observed trigger")}; {mechanic.Observations} outcomes; " +
            $"{mechanic.ForecastHits}/{mechanic.Forecasts} verified; evidence {mechanic.Confidence:P0}; guidance {confidence:P0}";
        return new(trigger.ActorID, trigger.PrimaryID, geometry, mechanic.Kind, origin, target, trigger.Rotation,
            mechanic.P1, mechanic.P2, activation, confidence, evidence, mechanic.Key, trigger.TargetID, guidance, anticipated,
            MechanicDisplayName(mechanic));
    }

    private void StorePrediction(long id, ActivePrediction prediction, ForetellObservation trigger)
    {
        ++_predictionRevision;
        var mechanic = _store.Encounters.GetValueOrDefault(trigger.TerritoryID)?.Mechanics.GetValueOrDefault(prediction.SignalKey);
        prediction = prediction with { CreatedAt = trigger.At,
            Binding = trigger.Prior?.CastType == 8 && trigger.TargetID != 0 ? HazardBinding.LineEndpoints : prediction.Binding,
            LineMinimumLength = trigger.Prior?.CastType == 8 ? Math.Max(0, trigger.Prior.Value.EffectRange) : prediction.LineMinimumLength,
            Stages = mechanic?.Stages.Select(stage => stage with { Polygon = stage.Polygon.ToArray() }).ToArray(),
            Provenance = prediction.Provenance == "Pre-impact model" ? prediction.Provenance
                : trigger.Prior is { Geometry: not GeometryKind.Unknown } ? "Client geometry"
                : trigger.Prior is { Kind: not MechanicKind.Unknown } ? "Client semantics"
                : prediction.Anticipated ? "Learned sequence" : trigger.Prior != null ? "Observed cast" : "Learned hypothesis" };
        if (_episodes.GetValueOrDefault(id) is { } forecastEpisode)
        { forecastEpisode.ForecastOrigin = prediction.Origin; forecastEpisode.ForecastRotation = prediction.Rotation; forecastEpisode.ForecastSnapshot = prediction; }
        _predictions[id] = prediction;
        AddDecisionAudit(new()
        {
            At = LearningNow,
            Activation = prediction.Activation,
            PredictionID = id,
            Stage = DecisionAuditStage.Proposed,
            SignalKey = prediction.SignalKey,
            TriggerKind = trigger.Kind,
            TriggerID = trigger.PrimaryID,
            TriggerDetail = trigger.Detail,
            SourceKind = trigger.SourceKind,
            SourceOID = trigger.ActorOID,
            Mechanic = prediction.Kind,
            Geometry = prediction.Geometry,
            Guidance = prediction.Guidance,
            P1 = prediction.P1,
            P2 = prediction.P2,
            OriginX = prediction.Origin.X,
            OriginZ = prediction.Origin.Y,
            TargetX = prediction.Target.X,
            TargetZ = prediction.Target.Y,
            Rotation = prediction.Rotation,
            Confidence = prediction.Confidence,
            Anticipated = prediction.Anticipated,
            DisplayEligible = _cfg.Mode is ForetellMode.Hybrid or ForetellMode.Foretell
                && prediction.Confidence >= _cfg.VisualConfidence / 100f
                && (_cfg.TextHints || HasSpatialPresentation(prediction) && (_cfg.WorldOverlay || _cfg.MiniRadar)),
            Label = prediction.Label,
            Evidence = prediction.Evidence
        });
    }

    private void AuditPredictionOutcome(long id, DecisionAuditStage stage, bool? verified, string evidence)
    {
        if (!_predictions.TryGetValue(id, out var prediction))
        {
            if (_episodes.GetValueOrDefault(id)?.ForecastSnapshot is not { } snapshot) return;
            prediction = snapshot;
        }
        AddDecisionAudit(new()
        {
            At = LearningNow,
            Activation = prediction.Activation,
            PredictionID = id,
            Stage = stage,
            Validation = _timelineForecasts.ContainsKey(id) ? PredictionValidationKind.TriggerTiming : PredictionValidationKind.Outcome,
            SignalKey = prediction.SignalKey,
            TriggerID = prediction.ActionID,
            Mechanic = prediction.Kind,
            Geometry = prediction.Geometry,
            Guidance = prediction.Guidance,
            P1 = prediction.P1,
            P2 = prediction.P2,
            OriginX = prediction.Origin.X,
            OriginZ = prediction.Origin.Y,
            TargetX = prediction.Target.X,
            TargetZ = prediction.Target.Y,
            Rotation = prediction.Rotation,
            Confidence = prediction.Confidence,
            Anticipated = prediction.Anticipated,
            DisplayEligible = _cfg.Mode is ForetellMode.Hybrid or ForetellMode.Foretell
                && prediction.Confidence >= _cfg.VisualConfidence / 100f
                && (_cfg.WorldOverlay || _cfg.TextHints || _cfg.MiniRadar),
            Verified = verified,
            Label = prediction.Label,
            Evidence = evidence
        });
    }

    private void AddDecisionAudit(DecisionAuditEntry entry)
    {
        entry.SessionID = _session.ID;
        entry.TerritoryID = entry.TerritoryID == 0 ? _session.TerritoryID : entry.TerritoryID;
        _evaluationAuditSink?.Invoke(entry);
        _store.DecisionAudit.Add(entry);
        // Trim in batches so the hot path never shifts the full bounded list for every new entry.
        if (_store.DecisionAudit.Count > 8448)
            _store.DecisionAudit.RemoveRange(0, _store.DecisionAudit.Count - 8192);
    }

    private void LearnCompositeMechanics(EncounterMemory encounter, MechanicEpisode episode)
    {
        var nearby = _episodes.Values
            .Where(other => !other.Finalized && Math.Abs((episode.Trigger.At - other.Trigger.At).TotalSeconds) <= .75)
            .Take(12)
            .Append(episode)
            .GroupBy(other => other.SignalKey)
            .Select(group => group.OrderBy(other => Math.Abs((episode.Trigger.At - other.Trigger.At).Ticks)).First())
            .OrderBy(other => other.SignalKey)
            .ToArray();
        if (nearby.Length < 2) return;
        var signals = nearby.Select(other => other.SignalKey).ToList();
        var skew = nearby.Max(other => other.Trigger.At).Subtract(nearby.Min(other => other.Trigger.At)).TotalSeconds;
        var timelinePhase = TimelinePhaseFor(episode.Trigger);
        var key = $"{timelinePhase}:{string.Join('+', signals)}";
        if (!encounter.Composites.TryGetValue(key, out var composite))
        {
            if (encounter.Composites.Count >= 2048)
            {
                encounter.Composites.Remove(encounter.Composites.MinBy(item => item.Value.Count).Key);
                ++_learningEvictions;
            }
            encounter.Composites[key] = composite = new() { Key = key, Phase = timelinePhase, Signals = signals };
        }
        ++composite.Count;
        var delta = skew - composite.MeanSkewSeconds;
        composite.MeanSkewSeconds += delta / composite.Count;
        composite.M2 += delta * (skew - composite.MeanSkewSeconds);
    }

    private MechanicEpisode? CorrelateObservation(ForetellObservation observation)
    {
        if (!ForetellInferenceCore.IsMechanicOutcomeEvidence(observation.Kind, observation.SourceKind))
            return null;

        var episode = BestEpisode(observation);
        if (episode == null) return null;
        RecordEpisodeComponent(episode, observation);
        episode.AddEvidence(observation.Kind);
        if (!episode.ResolutionObserved && episode.Trigger.Kind != ObservationKind.CastStart && IsResolutionEvidence(observation.Kind)
            && observation.At >= episode.Trigger.At)
        {
            episode.ResolutionObserved = true;
            episode.Activation = observation.At;
            episode.LeadSeconds = Math.Clamp((observation.At - episode.Trigger.At).TotalSeconds, 0, 120);
        }
        if (_cfg.EnableLearning && _store.Encounters.TryGetValue(observation.TerritoryID, out var causalEncounter) && IsCausalEvidence(observation.Kind))
            RecordCausalEdge(causalEncounter, episode, observation);

        switch (observation.Kind)
        {
            case ObservationKind.ActionResolved:
                if (observation.Numeric.TryGetValue("action.globalSequence", out var sequenceValue) && sequenceValue > 0 && sequenceValue <= uint.MaxValue)
                {
                    var sequence = (uint)sequenceValue;
                    _effectSequenceEpisodes[sequence] = episode.ID;
                    if (_effectSequenceEpisodes.Count > 4096)
                    {
                        foreach (var stale in _effectSequenceEpisodes.Where(kv => !_episodes.ContainsKey(kv.Value)).Select(kv => kv.Key).ToArray())
                            _effectSequenceEpisodes.Remove(stale);
                    }
                }
                break;
            case ObservationKind.AffectedTarget:
                if (!episode.ParticipantPositions.ContainsKey(observation.TargetID)
                    || episode.FirstResolvedAt != default && (observation.At - episode.FirstResolvedAt).TotalSeconds > .75) break;
                episode.TypedKnockback |= observation.Numeric.Any(kv => kv.Key.StartsWith("actionEffect.", StringComparison.Ordinal) && kv.Key.EndsWith(".type", StringComparison.Ordinal) && kv.Value == 31 && observation.Numeric.GetValueOrDefault(kv.Key[..^5] + ".atSource") == 0);
                episode.TypedAttract |= observation.Numeric.Any(kv => kv.Key.StartsWith("actionEffect.", StringComparison.Ordinal) && kv.Key.EndsWith(".type", StringComparison.Ordinal) && kv.Value is >= 32 and <= 36 && observation.Numeric.GetValueOrDefault(kv.Key[..^5] + ".atSource") == 0);
                if (observation.TargetID != 0 && ForetellInferenceCore.HasConfirmedHit(observation.Numeric))
                {
                    episode.AffectedTargets.Add(observation.TargetID);
                    CaptureResolutionPose(episode, observation.TargetID, observation.At);
                    var damage = observation.Numeric.Where(kv => kv.Key.EndsWith(".damageHealValue", StringComparison.Ordinal)
                            && ForetellInferenceCore.IsDamageEffect(observation.Numeric.GetValueOrDefault(kv.Key[..^16] + ".type")))
                        .Select(kv => kv.Value).Where(v => v > 0).Sum();
                    if (damage > 0) episode.DamageByTarget[observation.TargetID] = damage;
                }
                break;
            case ObservationKind.StatusGain:
                if (observation.TargetID != 0) { episode.StatusTargets.Add(observation.TargetID); CaptureResolutionPose(episode, observation.TargetID, observation.At); }
                break;
            case ObservationKind.TetherStart:
                if (observation.TargetID != 0) episode.TetherTargets.Add(observation.TargetID);
                break;
            case ObservationKind.Icon:
            case ObservationKind.VFX:
            case ObservationKind.NativeVFXSpawn:
                // A target marker or attached VFX identifies a cue, not a confirmed hit.
                break;
            case ObservationKind.Displacement:
                if (observation.ActorID != 0)
                {
                    episode.MovementTargets.Add(observation.ActorID);
                    episode.MovementDistances[observation.ActorID] = Math.Max(episode.MovementDistances.GetValueOrDefault(observation.ActorID), observation.Value1);
                    CaptureResolutionPose(episode, observation.ActorID, observation.At);
                }
                break;
            case ObservationKind.DeathChanged:
                if (observation.Flag && observation.ActorID != 0 && episode.ParticipantPositions.ContainsKey(observation.ActorID))
                    episode.DeathTargets.Add(observation.ActorID);
                break;
        }
        return episode;
    }

    private void CaptureResolutionPose(MechanicEpisode episode, ulong actorID, DateTime at)
    {
        if (!_tracks.TryGetValue(actorID, out var track)) return;
        var pose = track.Nearest(at);
        if (Math.Abs((pose.At - at).TotalSeconds) > .5) return;
        episode.ResolutionPositions[actorID] = pose.Position;
        episode.ResolutionRotations[actorID] = pose.Rotation;
    }

    private MechanicEpisode? BestEpisode(ForetellObservation observation)
    {
        if (observation.Kind == ObservationKind.AffectedTarget && observation.Numeric.TryGetValue("action.globalSequence", out var globalSequence)
            && globalSequence is > 0 and <= uint.MaxValue && _effectSequenceEpisodes.TryGetValue((uint)globalSequence, out var linked)
            && _episodes.TryGetValue(linked, out var linkedEpisode) && !linkedEpisode.Finalized)
            return linkedEpisode;
        if (observation.Kind == ObservationKind.EffectResult && observation.PrimaryID != 0
            && _effectSequenceEpisodes.TryGetValue(observation.PrimaryID, out var exactID)
            && _episodes.TryGetValue(exactID, out var exact) && !exact.Finalized)
            return exact;

        MechanicEpisode? best = null;
        var bestScore = double.MaxValue;
        var secondScore = double.MaxValue;
        var encounter = _store.Encounters.GetValueOrDefault(observation.TerritoryID);
        var effect = EffectSignalKey(observation);
        foreach (var episode in _episodes.Values)
        {
            if (episode.Finalized || observation.At < episode.Trigger.At.AddSeconds(-.25) || observation.At > episode.FinalizeAt.AddSeconds(.75))
                continue;
            var score = Math.Abs((observation.At - episode.Activation).TotalSeconds);
            if (observation.ActorID != 0 && observation.ActorID == episode.Trigger.ActorID) score -= 1.5;
            if (observation.PrimaryID != 0 && observation.PrimaryID == episode.Trigger.PrimaryID) score -= 1;
            if (observation.TargetID != 0 && episode.ParticipantPositions.ContainsKey(observation.TargetID)) score -= .2;
            if (encounter?.CausalEdges.GetValueOrDefault($"{episode.SignalKey}>{effect}") is { } causal && causal.Confidence > 0)
            {
                var observedDelay = Math.Max(0, (observation.At - episode.Trigger.At).TotalSeconds);
                var timingError = Math.Abs(observedDelay - causal.MeanDelay) / Math.Max(.25, causal.StdDev + .25);
                score += Math.Min(2, timingError) * (1 - causal.Confidence);
                score -= causal.Confidence * 2.5;
            }
            if (score < bestScore)
            {
                secondScore = bestScore;
                bestScore = score;
                best = episode;
            }
            else if (score < secondScore) secondScore = score;
        }
        return bestScore <= 3.5 && secondScore - bestScore >= .35 ? best : null;
    }

    private static bool IsResolutionEvidence(ObservationKind kind)
        => kind is ObservationKind.ActionResolved or ObservationKind.AffectedTarget or ObservationKind.EffectResult
            or ObservationKind.StatusGain or ObservationKind.Displacement or ObservationKind.DeathChanged;

    private static bool IsCausalEvidence(ObservationKind kind)
        => IsResolutionEvidence(kind) || kind is ObservationKind.StatusLose or ObservationKind.TetherStart or ObservationKind.TetherEnd
            or ObservationKind.TargetableChanged or ObservationKind.ModelStateChanged or ObservationKind.ObjectEffect;

    private static string EffectSignalKey(ForetellObservation observation)
        => $"{observation.Kind}:{observation.PrimaryID:X}:{observation.SecondaryID:X}";

    private void RecordCausalEdge(EncounterMemory encounter, MechanicEpisode episode, ForetellObservation effect)
    {
        var effectKey = EffectSignalKey(effect);
        var key = $"{episode.SignalKey}>{effectKey}";
        if (!encounter.CausalEdges.TryGetValue(key, out var edge))
        {
            const int maxEffectsPerCause = 64;
            var sameCause = encounter.CausalEdges.Where(item => item.Value.Cause == episode.SignalKey).ToArray();
            if (sameCause.Length >= maxEffectsPerCause)
            {
                var weakest = sameCause.MinBy(item => (item.Value.Confidence, item.Value.Count, item.Value.LastSeen));
                encounter.CausalEdges.Remove(weakest.Key);
                ++_learningEvictions;
            }
            if (encounter.CausalEdges.Count >= 8192)
            {
                encounter.CausalEdges.Remove(encounter.CausalEdges.MinBy(item => (item.Value.Confidence, item.Value.Count, item.Value.LastSeen)).Key);
                ++_learningEvictions;
            }
            encounter.CausalEdges[key] = edge = new() { Cause = episode.SignalKey, Effect = effectKey };
        }
        ++edge.Count;
        if (effect.Kind == ObservationKind.EffectResult && effect.PrimaryID != 0
            && _effectSequenceEpisodes.GetValueOrDefault(effect.PrimaryID) == episode.ID)
            ++edge.ExactLinks;
        var delay = Math.Clamp((effect.At - episode.Trigger.At).TotalSeconds, 0, 120);
        var delta = delay - edge.MeanDelay;
        edge.MeanDelay += delta / edge.Count;
        edge.M2 += delta * (delay - edge.MeanDelay);
        edge.LastSeen = LearningNow;
    }

    private void HandlePositionSample(ForetellObservation observation, bool replaying)
    {
        var current = new Vector2(observation.X, observation.Z);
        if (_tracks.TryGetValue(observation.ActorID, out var previous) && !replaying)
        {
            var dt = (observation.At - previous.At).TotalSeconds;
            var distance = Vector2.Distance(previous.Position, current);
            if (distance <= 35f && ForetellInferenceCore.IsAbruptDisplacement(distance, dt))
            {
                var delta = current - previous.Position;
                var displacement = new ForetellObservation
                {
                    Sequence = ++_sequence,
                    At = observation.At,
                    TerritoryID = observation.TerritoryID,
                    Kind = ObservationKind.Displacement,
                    SourceKind = observation.SourceKind,
                    ActorID = observation.ActorID,
                    PrimaryID = observation.PrimaryID,
                    SecondaryID = observation.SecondaryID,
                    X = current.X,
                    Z = current.Y,
                    TargetX = previous.Position.X,
                    TargetZ = previous.Position.Y,
                    Rotation = MathF.Atan2(delta.X, delta.Y),
                    Value1 = distance,
                    Value2 = (float)dt,
                    Detail = observation.Detail
                };
                ProcessObservation(displacement);
            }
        }
        if (!_tracks.TryGetValue(observation.ActorID, out var track))
            _tracks[observation.ActorID] = track = new();
        track.Add(observation.At, current, observation.Rotation, observation.SecondaryID, observation.Detail);
    }

    private void FinalizeDue(DateTime now, bool exhaustive = false)
    {
        if (!exhaustive)
        {
            var frameTicks = _ws.CurrentTime.Ticks;
            if (frameTicks != _finalizationBudgetFrameTicks)
            {
                _finalizationBudgetFrameTicks = frameTicks;
                _finalizationsThisFrame = 0;
            }
            if (_finalizationsThisFrame >= 2) return;
        }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var finalized = 0;
        while (_episodeFinalization.TryPeek(out var id, out var due) && due <= now.Ticks)
        {
            if (!exhaustive && (_finalizationsThisFrame >= 2 || finalized >= 2 || System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= .65))
                break;
            _episodeFinalization.Dequeue();
            if (_episodes.TryGetValue(id, out var episode) && !episode.Finalized && episode.FinalizeAt.Ticks == due)
            {
                FinalizeEpisode(episode);
                _episodeCleanup.Enqueue(id, episode.FinalizeAt.AddSeconds(20).Ticks);
                ++finalized;
                if (!exhaustive) ++_finalizationsThisFrame;
            }
        }
        var cleaned = 0;
        while (_episodeCleanup.TryPeek(out var cleanupID, out var cleanupDue) && cleanupDue <= now.Ticks && (exhaustive || cleaned++ < 32))
        {
            _episodeCleanup.Dequeue();
            if (_episodes.TryGetValue(cleanupID, out var cleanupEpisode) && cleanupEpisode.Finalized)
                RemoveEpisode(cleanupID);
        }
    }

    private void RemoveEpisode(long id)
    {
        _episodes.Remove(id);
        _predictions.Remove(id);
        foreach (var sequence in _effectSequenceEpisodes.Where(item => item.Value == id).Select(item => item.Key).ToArray())
            _effectSequenceEpisodes.Remove(sequence);
    }

    private void FinalizeEpisode(MechanicEpisode episode)
    {
        if (episode.Finalized) return;
        episode.Finalized = true;
        if (!IsEpisodeTrigger(episode.Trigger))
            return;
        ++_session.MechanicsFinalized;

        if (episode.OutcomeGapGeneration != _outcomeGapGeneration)
        {
            // Missing target callbacks must not turn party members into negative (safe) geometry samples.
            AuditPredictionOutcome(episode.ID, DecisionAuditStage.Verified, null, "outcome incomplete: semantic capture budget dropped evidence");
            if (_store.Encounters.GetValueOrDefault(episode.Trigger.TerritoryID)?.Mechanics.GetValueOrDefault(episode.SignalKey) is { } incomplete)
                ++incomplete.UnverifiableOutcomes;
            if (_cfg.EnableLearning) ++_store.PreImpact.MissingOutcomes;
            return;
        }

        if (!_cfg.EnableLearning && !_isReplay)
        {
            _lastEvidence = $"{episode.SignalKey}: outcome observed; adaptive learning is disabled";
            return;
        }

        var encounter = Encounter(episode.Trigger.TerritoryID);
        var key = episode.SignalKey;
        var isNew = !encounter.Mechanics.TryGetValue(key, out var mechanic);
        if (mechanic == null)
        {
            if (encounter.Mechanics.Count >= 2048)
            {
                var weakest = encounter.Mechanics.MinBy(item => (item.Value.Confirmations, item.Value.Observations, item.Value.LastSeen));
                PurgeMechanic(encounter.TerritoryID, weakest.Key);
                ++_learningEvictions;
            }
            mechanic = new()
            {
                Key = key,
                TerritoryID = episode.Trigger.TerritoryID,
                SourceOID = episode.Trigger.ActorOID,
                SourceKind = episode.Trigger.SourceKind,
                TriggerKind = episode.Trigger.Kind,
                TriggerID = episode.Trigger.PrimaryID,
                TriggerDetail = episode.Trigger.Detail,
                FirstSeen = LearningNow
            };
            if (_cfg.EnableLearning)
            { encounter.Mechanics[key] = mechanic; ++_session.NewMechanics; }
        }
        mechanic.Samples ??= [];

        var affected = new HashSet<ulong>(episode.AffectedTargets);
        // Status applications alone do not establish a damaging spatial footprint.
        // Resolve every participant against the sample nearest to impact. Affected-target callbacks update this
        // eagerly; this pass also moves non-affected controls to the same point in time for unbiased geometry fits.
        foreach (var id in episode.ParticipantPositions.Keys)
            if (!episode.ResolutionPositions.ContainsKey(id))
                CaptureResolutionPose(episode, id, episode.Activation);
        var outcomeSamples = new ContextualMechanic();
        AddGeometrySamples(outcomeSamples, episode, affected);
        var outcomeFit = FitNormalizedGeometry(outcomeSamples.Samples);
        var observedKind = ClassifyEpisode(episode, affected, outcomeFit);
        if (!_cfg.EnableLearning)
        {
            var evaluationProbe = new ContextualMechanic();
            var frozenVerified = episode.ForecastIssued ? ValidateMechanicForecast(evaluationProbe, episode, observedKind, outcomeFit) : null;
            AuditPredictionOutcome(episode.ID, DecisionAuditStage.Verified, frozenVerified, "frozen model evaluated against independent outcome");
            if (_cfg.EnableML) _preImpact.Resolve(episode.PreImpactFeatures, episode.PreImpactGuess, observedKind, episode.LeadSeconds, true, train: false);
            return;
        }
        ForetellReliability.ObserveHypotheses(mechanic, ForetellOutcomeHypotheses.Candidates(OutcomeCues(episode, affected, outcomeFit)));
        LearnEpisodeProgram(mechanic, episode);
        AddGeometrySamples(mechanic, episode, affected);
        var fit = FitNormalizedGeometry(mechanic.Samples);
        mechanic.GeometryAmbiguous = fit is { Ambiguous: true };
        var kind = observedKind;
        var score = EvidenceScore(kind, outcomeFit);
        var ml = (episode.PreImpactGuess.Kind, episode.PreImpactGuess.Probability);

        var previousKind = mechanic.Kind;
        var previousGeometry = mechanic.Geometry;
        ++mechanic.Observations;
        mechanic.AffectedSamples += episode.AffectedTargets.Count;
        mechanic.StatusSamples += episode.StatusTargets.Count;
        mechanic.MovementSamples += episode.MovementTargets.Count;
        mechanic.DeathSamples += episode.DeathTargets.Count;
        mechanic.MeanLeadSeconds += (episode.LeadSeconds - mechanic.MeanLeadSeconds) / mechanic.Observations;
        UpdateMechanicAnchor(mechanic, episode);
        mechanic.LastSeen = LearningNow;
        foreach (var (evidence, count) in episode.Evidence)
            mechanic.Evidence[evidence] = mechanic.Evidence.GetValueOrDefault(evidence) + count;

        var agrees = previousKind == MechanicKind.Unknown || kind == MechanicKind.Unknown || previousKind == kind;
        var geometryAgrees = previousGeometry == GeometryKind.Unknown || fit == null || previousGeometry == fit.Value.Geometry;
        if (agrees && geometryAgrees && (observedKind != MechanicKind.Unknown || outcomeFit != null))
            ++mechanic.Confirmations;
        else if (!agrees || !geometryAgrees)
        {
            ++mechanic.AmbiguousSamples;
            ++_session.AmbiguousMechanics;
        }

        if (kind != MechanicKind.Unknown && (mechanic.Kind == MechanicKind.Unknown || agrees || score > mechanic.Score + .12f))
            mechanic.Kind = kind;
        if (fit is FitResult geometry && (mechanic.Geometry == GeometryKind.Unknown || geometryAgrees || geometry.Score > mechanic.Score + .08f))
        {
            mechanic.Geometry = geometry.Geometry;
            mechanic.P1 = geometry.P1;
            mechanic.P2 = geometry.P2;
        }
        mechanic.Score = mechanic.Observations == 1 ? score : mechanic.Score * .72f + score * .28f;

        // Reliable Action-sheet semantics describe the action itself. Ambient statuses, movement and a player's
        // successful dodge are contextual outcomes and must not rewrite a known rectangle/circle into CLEANSE,
        // MOVE or KNOCKBACK. Reassert before training, validation and audit so all downstream consumers agree.
        ReassertReliableActionPrior(mechanic);
        var resolvedKind = mechanic.Kind;

        // Marker means only "a target symbol exists"; it is an abstention from stack/spread/targeted semantics,
        // not a behavioral class for the outcome classifier.
        // Never train on the classifier's own guess or a previously retained label. Only this episode's
        // independent outcome (or an authoritative client-data prior) may supply a training target.
        if (_cfg.EnableML)
            _preImpact.Resolve(episode.PreImpactFeatures, episode.PreImpactGuess, observedKind, episode.LeadSeconds, true);

        if (_cfg.EnableLearning && episode.Trigger.Kind == ObservationKind.CastStart && fit is FitResult globalFit
            && mechanic.PriorKind != MechanicKind.Gaze && (mechanic.HasReliableActionPrior || !globalFit.Ambiguous))
        {
            var canonicalFit = mechanic.HasReliableActionPrior
                ? new FitResult(mechanic.PriorGeometry, globalFit.Origin, globalFit.Rotation, mechanic.PriorP1, mechanic.PriorP2, mechanic.PriorConfidence)
                : globalFit;
            UpdateGlobalMechanic(episode, canonicalFit, resolvedKind);
        }

        bool? forecastVerified = null;
        if (episode.ForecastIssued)
            forecastVerified = ValidateMechanicForecast(mechanic, episode, observedKind, outcomeFit);
        if (episode.ForecastIssued && forecastVerified == null) ++mechanic.UnverifiableOutcomes;
        if (forecastVerified == false) ++mechanic.RecentContradictions;
        else if (forecastVerified == true) mechanic.RecentContradictions = Math.Max(0, mechanic.RecentContradictions - 1);

        AddDecisionAudit(new()
        {
            At = LearningNow,
            Activation = episode.Activation,
            PredictionID = episode.ID,
            Stage = DecisionAuditStage.Classified,
            SignalKey = episode.SignalKey,
            TriggerKind = episode.Trigger.Kind,
            TriggerID = episode.Trigger.PrimaryID,
            TriggerDetail = episode.Trigger.Detail,
            SourceKind = episode.Trigger.SourceKind,
            SourceOID = episode.Trigger.ActorOID,
            Mechanic = mechanic.Kind,
            Geometry = mechanic.Geometry,
            Guidance = ForetellInferenceCore.GuidanceFor(mechanic.Kind),
            P1 = mechanic.P1,
            P2 = mechanic.P2,
            OriginX = fit?.Origin.X ?? episode.Trigger.X,
            OriginZ = fit?.Origin.Y ?? episode.Trigger.Z,
            TargetX = episode.Trigger.TargetX,
            TargetZ = episode.Trigger.TargetZ,
            Rotation = fit?.Rotation ?? episode.Trigger.Rotation,
            Confidence = mechanic.GuidanceConfidence,
            Anticipated = episode.ForecastAnticipated,
            DisplayEligible = _cfg.Mode is ForetellMode.Hybrid or ForetellMode.Foretell
                && mechanic.GuidanceConfidence >= _cfg.VisualConfidence / 100f
                && (_cfg.WorldOverlay || _cfg.TextHints || _cfg.MiniRadar),
            Verified = forecastVerified,
            Label = MechanicDisplayName(mechanic),
            Evidence = $"{mechanic.Confirmations}/{mechanic.Observations} confirmations; affected {affected.Count}/{episode.ParticipantPositions.Count}; evidence {episode.Evidence.Count}; ML {ml.Item1} {ml.Item2:P0}"
        });
        if (episode.ForecastIssued)
            AuditPredictionOutcome(episode.ID, DecisionAuditStage.Verified, forecastVerified,
                forecastVerified == true ? "predicted mechanic matched the observed outcome" : forecastVerified == false
                    ? "predicted mechanic differed from the observed outcome" : "outcome does not independently resolve the forecast");

        _lastEvidence = $"{key}: {mechanic.Kind}/{mechanic.Geometry} {mechanic.Confidence:P0}; evidence {episode.Evidence.Count}; " +
            $"affected {affected.Count}/{episode.ParticipantPositions.Count}; move {episode.MovementTargets.Count}; ML {ml.Item1} {ml.Item2:P0}";
    }

    private static void UpdateMechanicAnchor(ContextualMechanic mechanic, MechanicEpisode episode)
    {
        var source = new Vector2(episode.Trigger.X, episode.Trigger.Z);
        var target = new Vector2(episode.Trigger.TargetX, episode.Trigger.TargetZ);
        var targetKnown = target != Vector2.Zero && float.IsFinite(target.X) && float.IsFinite(target.Y);
        mechanic.OriginKind = mechanic.Geometry is GeometryKind.Circle or GeometryKind.Donut && targetKnown
            ? PredictionOriginKind.Target
            : PredictionOriginKind.Source;
        if (!targetKnown || !float.IsFinite(source.X) || !float.IsFinite(source.Y)) return;
        var delta = target - source;
        var f = new Vector2(MathF.Sin(episode.Trigger.Rotation), MathF.Cos(episode.Trigger.Rotation));
        var r = new Vector2(MathF.Cos(episode.Trigger.Rotation), -MathF.Sin(episode.Trigger.Rotation));
        var forward = Vector2.Dot(delta, f);
        var side = Vector2.Dot(delta, r);
        ++mechanic.AnchorSamples;
        var forwardDelta = forward - mechanic.MeanAnchorForward;
        mechanic.MeanAnchorForward += forwardDelta / mechanic.AnchorSamples;
        mechanic.AnchorForwardM2 += forwardDelta * (forward - mechanic.MeanAnchorForward);
        var sideDelta = side - mechanic.MeanAnchorSide;
        mechanic.MeanAnchorSide += sideDelta / mechanic.AnchorSamples;
        mechanic.AnchorSideM2 += sideDelta * (side - mechanic.MeanAnchorSide);
    }

    private static bool? ValidateMechanicForecast(ContextualMechanic mechanic, MechanicEpisode episode, MechanicKind observedKind, FitResult? fit)
    {
        if (episode.ForecastSnapshot is not { } prediction) return null;
        var points = episode.ResolutionPositions.Where(p => episode.ParticipantPositions.ContainsKey(p.Key))
            .Select(p => new SpatialOutcomePoint(p.Value, episode.AffectedTargets.Contains(p.Key))).ToArray();
        var verdict = ForetellOutcomeValidation.Verify(prediction, observedKind, episode.FirstResolvedAt, points, complete: true);
        if (verdict is not bool success) return null;
        ++mechanic.Forecasts;
        if (success) ++mechanic.ForecastHits;
        else ++mechanic.ForecastMisses;
        var probability = Math.Clamp(episode.ForecastConfidence, 0, 1);
        var outcome = success ? 1d : 0d;
        mechanic.BrierScoreSum += (probability - outcome) * (probability - outcome);
        return success;
    }

    private static void AddGeometrySamples(ContextualMechanic mechanic, MechanicEpisode episode, HashSet<ulong> affected)
    {
        var source = new Vector2(episode.Trigger.X, episode.Trigger.Z);
        var target = new Vector2(episode.Trigger.TargetX, episode.Trigger.TargetZ);
        if (episode.Trigger.TargetID == 0 && episode.Trigger.Prior?.TargetArea != true) target = source;
        var s = MathF.Sin(episode.Trigger.Rotation);
        var c = MathF.Cos(episode.Trigger.Rotation);
        foreach (var (id, resolved) in episode.ResolutionPositions)
        {
            if (!episode.ParticipantPositions.ContainsKey(id)) continue;
            var d = resolved - source;
            mechanic.Samples.Add(new()
            {
                Side = d.X * c - d.Y * s,
                Forward = d.X * s + d.Y * c,
                TargetDX = resolved.X - target.X,
                TargetDZ = resolved.Y - target.Y,
                Affected = affected.Contains(id)
            });
        }
        while (mechanic.Samples.Count > 256)
            mechanic.Samples.RemoveAt(0);
    }

    private static FitResult? FitNormalizedGeometry(List<MechanicSamplePoint> samples)
    {
        if (samples.Count < 2 || !samples.Any(s => s.Affected) || !samples.Any(s => !s.Affected)) return null;
        FitResult best = new(GeometryKind.Unknown, Vector2.Zero, 0, 0, 0, 0);
        Dictionary<GeometryKind, float> alternatives = [];
        void Try(GeometryKind kind, float p1, float p2, Func<MechanicSamplePoint, bool> contains)
        {
            var score = NormalizedScore(samples, contains);
            alternatives[kind] = Math.Max(alternatives.GetValueOrDefault(kind), score);
            if (score > best.Score) best = new(kind, Vector2.Zero, 0, p1, p2, score);
        }

        for (var r = 2f; r <= 35f; r += 1f)
            Try(GeometryKind.Circle, r, 0, p => MathF.Sqrt(p.TargetDX * p.TargetDX + p.TargetDZ * p.TargetDZ) <= r);
        for (var inner = 2f; inner <= 18f; inner += 2f)
            for (var outer = inner + 4; outer <= Math.Min(40, inner + 24); outer += 3f)
                Try(GeometryKind.Donut, inner, outer, p =>
                {
                    var d = MathF.Sqrt(p.TargetDX * p.TargetDX + p.TargetDZ * p.TargetDZ);
                    return d >= inner && d <= outer;
                });
        for (var range = 8f; range <= 60f; range += 4f)
            foreach (var halfDeg in ForetellInferenceCore.ConeHalfAngleCandidatesDegrees())
            {
                var half = halfDeg * MathF.PI / 180f;
                Try(GeometryKind.Cone, range, half, p =>
                {
                    var len = MathF.Sqrt(p.Side * p.Side + p.Forward * p.Forward);
                    if (len > range || len < .01f) return false;
                    return MathF.Abs(MathF.Atan2(p.Side, p.Forward)) <= half;
                });
            }
        for (var length = 8f; length <= 50f; length += 4f)
            for (var halfWidth = 1.5f; halfWidth <= 12f; halfWidth += 1.5f)
            {
                Try(GeometryKind.Rectangle, length, halfWidth, p => p.Forward >= 0 && p.Forward <= length && MathF.Abs(p.Side) <= halfWidth);
                Try(GeometryKind.Cross, length, halfWidth, p =>
                    (MathF.Abs(p.Side) <= halfWidth && MathF.Abs(p.Forward) <= length) ||
                    (MathF.Abs(p.Forward) <= halfWidth && MathF.Abs(p.Side) <= length));
            }

        return best.Score >= .58f ? best with { AlternativeScore = alternatives.Where(p => p.Key != best.Geometry).Select(p => p.Value).DefaultIfEmpty().Max() } : null;
    }

    private static float NormalizedScore(List<MechanicSamplePoint> samples, Func<MechanicSamplePoint, bool> contains)
    {
        float tp = 0, tn = 0, fp = 0, fn = 0;
        foreach (var sample in samples)
        {
            var predicted = contains(sample);
            if (predicted && sample.Affected) ++tp;
            else if (!predicted && !sample.Affected) ++tn;
            else if (predicted) ++fp;
            else ++fn;
        }
        var tpr = tp / Math.Max(1, tp + fn);
        var tnr = tn / Math.Max(1, tn + fp);
        var precision = tp / Math.Max(1, tp + fp);
        return Math.Clamp((tpr + tnr) * .375f + precision * .25f, 0, 1);
    }

    private static OutcomeCueSummary OutcomeCues(MechanicEpisode episode, HashSet<ulong> affected, FitResult? fit)
        => new(episode.ParticipantPositions.Count, affected.Count, episode.StatusTargets.Count, episode.MovementTargets.Count,
            episode.Evidence.ContainsKey(ObservationKind.Icon), episode.TetherTargets.Count > 0,
            affected.Count > 0 && affected.All(id => episode.ParticipantRoleNames.GetValueOrDefault(id)?.Contains("Tank", StringComparison.OrdinalIgnoreCase) == true),
            LooksLikeGaze(episode, affected), LooksLikeProximity(episode), fit?.Geometry ?? GeometryKind.Unknown,
            fit?.Score ?? 0, episode.ResolutionPositions.Count);

    private static MechanicKind ClassifyEpisode(MechanicEpisode episode, HashSet<ulong> affected, FitResult? fit)
    {
        if (episode.TypedKnockback) return MechanicKind.Knockback;
        if (episode.TypedAttract) return MechanicKind.ForcedMovement;
        if (fit is { Ambiguous: true } && !(episode.Trigger.Prior is { } prior
            && ForetellInferenceCore.IsReliableSpatialActionPrior(prior.Kind, prior.Geometry, prior.Confidence, prior.P1, prior.P2)))
            return MechanicKind.Unknown;
        return ForetellOutcomeHypotheses.IndependentLabel(OutcomeCues(episode, affected, fit));
    }

    private static bool LooksLikeGaze(MechanicEpisode episode, HashSet<ulong> affected)
    {
        if (affected.Count == 0 || affected.Count == episode.ParticipantPositions.Count || episode.Trigger.X == 0 && episode.Trigger.Z == 0)
            return false;
        var source = new Vector2(episode.Trigger.X, episode.Trigger.Z);
        var hit = new List<float>();
        var safe = new List<float>();
        foreach (var id in episode.ParticipantPositions.Keys)
        {
            var position = episode.PositionFor(id);
            var toSource = source - position;
            if (toSource.LengthSquared() < 1) continue;
            toSource = Vector2.Normalize(toSource);
            var rotation = episode.RotationFor(id);
            var facing = new Vector2(MathF.Sin(rotation), MathF.Cos(rotation));
            (affected.Contains(id) ? hit : safe).Add(Vector2.Dot(facing, toSource));
        }
        return hit.Count > 0 && safe.Count > 0 && hit.Average() > .35f && hit.Average() - safe.Average() > .55f;
    }

    private static bool LooksLikeProximity(MechanicEpisode episode)
    {
        if (episode.DamageByTarget.Count < 3) return false;
        var origin = new Vector2(episode.Trigger.TargetX, episode.Trigger.TargetZ);
        if (origin == Vector2.Zero) origin = new(episode.Trigger.X, episode.Trigger.Z);
        var samples = episode.DamageByTarget
            .Where(kv => episode.ParticipantPositions.ContainsKey(kv.Key))
            .Select(kv => (Distance: (double)Vector2.Distance(episode.PositionFor(kv.Key), origin), Damage: kv.Value))
            .ToArray();
        if (samples.Length < 3 || samples.Max(s => s.Damage) < samples.Min(s => s.Damage) * 1.20) return false;
        var meanX = samples.Average(s => s.Distance);
        var meanY = samples.Average(s => s.Damage);
        double covariance = 0, varianceX = 0, varianceY = 0;
        foreach (var sample in samples)
        {
            var dx = sample.Distance - meanX;
            var dy = sample.Damage - meanY;
            covariance += dx * dy; varianceX += dx * dx; varianceY += dy * dy;
        }
        var denominator = Math.Sqrt(varianceX * varianceY);
        return denominator > 0 && covariance / denominator < -.65;
    }

    private static float EvidenceScore(MechanicKind kind, FitResult? fit) => kind switch
    {
        MechanicKind.Raidwide => .82f,
        MechanicKind.Tankbuster => .78f,
        MechanicKind.Tether => .78f,
        MechanicKind.Knockback => .78f,
        MechanicKind.ForcedMovement => .70f,
        MechanicKind.Gaze => .74f,
        MechanicKind.Proximity => .72f,
        MechanicKind.LineStack => .72f,
        MechanicKind.Tower => .48f,
        MechanicKind.Stack or MechanicKind.Spread => .74f,
        MechanicKind.Debuff => .68f,
        MechanicKind.TargetedAOE => .66f,
        MechanicKind.Marker => .90f,
        MechanicKind.Environment or MechanicKind.Transition => .58f,
        MechanicKind.GroundAOE => fit?.Score ?? .60f,
        _ => fit?.Score ?? .25f
    };

    private void UpdateGlobalMechanic(MechanicEpisode episode, FitResult fit, MechanicKind kind)
    {
        var action = episode.Trigger.PrimaryID;
        if (action == 0) return;
        if (!_store.Mechanics.TryGetValue(action, out var mechanic))
        {
            _store.Mechanics[action] = new()
            {
                ActionID = action,
                Geometry = fit.Geometry,
                Kind = kind,
                P1 = fit.P1,
                P2 = fit.P2,
                Score = fit.Score,
                Observations = 1,
                Confirmations = 1,
                MeanCastSeconds = episode.LeadSeconds,
                LastSeen = LearningNow
            };
            return;
        }
        ++mechanic.Observations;
        if (mechanic.Geometry == fit.Geometry)
        {
            ++mechanic.Confirmations;
            var alpha = Math.Clamp(1f / MathF.Sqrt(mechanic.Confirmations), .08f, .35f);
            mechanic.P1 = mechanic.P1 * (1 - alpha) + fit.P1 * alpha;
            mechanic.P2 = mechanic.P2 * (1 - alpha) + fit.P2 * alpha;
            mechanic.Score = mechanic.Score * (1 - alpha) + fit.Score * alpha;
        }
        else if (fit.Score > mechanic.Score + .08f)
        {
            mechanic.Geometry = fit.Geometry;
            mechanic.P1 = fit.P1;
            mechanic.P2 = fit.P2;
            mechanic.Score = fit.Score;
            mechanic.Confirmations = 1;
        }
        if (kind != MechanicKind.Unknown) mechanic.Kind = kind;
        mechanic.MeanCastSeconds += (episode.LeadSeconds - mechanic.MeanCastSeconds) / mechanic.Observations;
        mechanic.LastSeen = LearningNow;
    }

}
