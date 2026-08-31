namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private void ProcessObservation(ForetellObservation observation, bool replaying = false)
    {
        if (observation.Sequence == 0) observation.Sequence = ++_sequence;
        else _sequence = Math.Max(_sequence, observation.Sequence);
        if (observation.TerritoryID == 0) observation.TerritoryID = _territory;

        FinalizeDue(observation.At);
        _session.Observe(observation);
        var encounter = Encounter(observation.TerritoryID);
        encounter.LastSeen = DateTime.UtcNow;
        encounter.ObservationCounts[observation.Kind] = encounter.ObservationCounts.GetValueOrDefault(observation.Kind) + 1;
        UpdateSourceMemory(encounter, observation);
        Record(observation, replaying);

        if (observation.Kind == ObservationKind.PositionSample)
        {
            HandlePositionSample(observation, replaying);
            return;
        }

        if (IsCombatSignal(observation))
            TouchPull(encounter, observation);

        if (IsTimelineSignal(observation))
            LearnSignalTimeline(encounter, observation);

        var correlated = CorrelateObservation(observation);
        if (observation.Kind == ObservationKind.CastStart || (IsEpisodeTrigger(observation) && !correlated))
            StartEpisode(observation, encounter);

        _recentSignals.Enqueue(observation);
        TrimRecentSignals(observation.At.AddSeconds(-8));
    }

    private void UpdateSourceMemory(EncounterMemory encounter, ForetellObservation observation)
    {
        // Environment is intentionally represented as OID 0 so map/director effects remain inspectable as one source.
        if (!encounter.Sources.TryGetValue(observation.ActorOID, out var source))
        {
            source = new()
            {
                OID = observation.ActorOID,
                Kind = observation.SourceKind,
                FirstSeen = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow
            };
            encounter.Sources[observation.ActorOID] = source;
        }
        ++source.Observations;
        source.LastSeen = DateTime.UtcNow;
        if (observation.Kind == ObservationKind.CastStart) ++source.Casts;
        if (observation.Kind is ObservationKind.Icon or ObservationKind.VFX or ObservationKind.TetherStart or ObservationKind.StatusGain or
            ObservationKind.EventObjectState or ObservationKind.EventObjectAnimation or ObservationKind.ActionTimelineEvent or
            ObservationKind.MapEffect or ObservationKind.LegacyMapEffect or ObservationKind.DirectorUpdate or ObservationKind.NpcYell)
            ++source.Signals;
        if (observation.Kind == ObservationKind.DeathChanged && observation.Flag) ++source.Deaths;
    }

    private void TouchPull(EncounterMemory encounter, ForetellObservation observation)
    {
        if (!_inPull || (observation.At - _lastCombatSignal).TotalSeconds > 30)
        {
            _inPull = true;
            ++_session.Pulls;
            ++encounter.Pulls;
            _session.Phase = 0;
            _previousSignal = "";
        }
        _lastCombatSignal = observation.At;
    }

    private static bool IsCombatSignal(ForetellObservation observation)
        => observation.SourceKind is SourceKind.Enemy or SourceKind.EventObject or SourceKind.Environment
            && observation.Kind is ObservationKind.CastStart or ObservationKind.ActionResolved or ObservationKind.Icon or ObservationKind.VFX
                or ObservationKind.TetherStart or ObservationKind.EventObjectState or ObservationKind.EventObjectAnimation
                or ObservationKind.ActionTimelineEvent or ObservationKind.MapEffect or ObservationKind.LegacyMapEffect or ObservationKind.DirectorUpdate;

    private static bool IsTimelineSignal(ForetellObservation observation)
        => observation.Kind is ObservationKind.CastStart or ObservationKind.Icon or ObservationKind.VFX or ObservationKind.TetherStart
            or ObservationKind.EventObjectState or ObservationKind.EventObjectAnimation or ObservationKind.ActionTimelineEvent
            or ObservationKind.NpcYell or ObservationKind.MapEffect or ObservationKind.LegacyMapEffect or ObservationKind.DirectorUpdate;

    private static bool IsEpisodeTrigger(ForetellObservation observation)
    {
        if (observation.SourceKind is SourceKind.Player or SourceKind.Pet) return false;
        return observation.Kind is ObservationKind.CastStart or ObservationKind.Icon or ObservationKind.VFX or ObservationKind.TetherStart
            or ObservationKind.EventObjectState or ObservationKind.EventObjectAnimation or ObservationKind.ActionTimelineEvent
            or ObservationKind.NpcYell or ObservationKind.MapEffect or ObservationKind.LegacyMapEffect or ObservationKind.DirectorUpdate;
    }

    private static string SignalKey(ForetellObservation observation)
        => $"{observation.ActorOID:X}:{observation.Kind}:{observation.PrimaryID:X}";

    private void LearnSignalTimeline(EncounterMemory encounter, ForetellObservation observation)
    {
        var signal = SignalKey(observation);
        if (!encounter.Phases.TryGetValue(_session.Phase, out var phase))
            encounter.Phases[_session.Phase] = phase = new() { Phase = _session.Phase };
        ++phase.Seen;
        phase.Signals[signal] = phase.Signals.GetValueOrDefault(signal) + 1;

        if (!string.IsNullOrEmpty(_previousSignal) && _previousSignal != signal)
        {
            var key = $"{_session.Phase}:{_previousSignal}>{signal}";
            var dt = Math.Max(0, (observation.At - _previousSignalTime).TotalSeconds);
            if (!encounter.Timeline.TryGetValue(key, out var edge))
                encounter.Timeline[key] = edge = new() { From = _previousSignal, To = signal, Phase = _session.Phase };
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

    private void LearnLegacyTimeline(uint action, DateTime now)
    {
        if (_previousAction != 0 && _previousAction != action)
        {
            var key = $"{_previousAction}>{action}";
            var dt = Math.Max(0, (now - _previousActionTime).TotalSeconds);
            if (!_store.Timeline.TryGetValue(key, out var edge))
                _store.Timeline[key] = edge = new() { From = _previousAction, To = action };
            ++edge.Count;
            var delta = dt - edge.MeanDelay;
            edge.MeanDelay += delta / edge.Count;
            edge.M2 += delta * (dt - edge.MeanDelay);
        }
        _previousAction = action;
        _previousActionTime = now;
    }

    private void StartEpisode(ForetellObservation trigger, EncounterMemory encounter)
    {
        if (_episodes.Values.Any(e => !e.Finalized && e.Trigger.ActorID == trigger.ActorID && e.Trigger.Kind == trigger.Kind &&
            e.Trigger.PrimaryID == trigger.PrimaryID && Math.Abs((e.Trigger.At - trigger.At).TotalSeconds) < .6))
            return;

        var lead = trigger.Kind == ObservationKind.CastStart ? Math.Max(0, trigger.Value1) : 0;
        var episode = new MechanicEpisode
        {
            ID = trigger.Sequence,
            Trigger = trigger,
            LeadSeconds = lead,
            Activation = trigger.At.AddSeconds(lead),
            FinalizeAt = trigger.At.AddSeconds(lead + 3)
        };
        foreach (var (id, track) in _tracks)
        {
            episode.ParticipantPositions[id] = track.Position;
            episode.ParticipantRoles[id] = track.Role;
            episode.ParticipantRoleNames[id] = track.RoleName;
        }
        episode.AddEvidence(trigger.Kind);
        _episodes[episode.ID] = episode;

        if (trigger.Kind == ObservationKind.CastStart && encounter.Mechanics.TryGetValue(episode.SignalKey, out var learned)
            && learned.Geometry != GeometryKind.Unknown)
        {
            var target = new Vector2(trigger.TargetX, trigger.TargetZ);
            var source = new Vector2(trigger.X, trigger.Z);
            var origin = learned.Geometry is GeometryKind.Circle or GeometryKind.Donut ? target : source;
            _predictions[episode.ID] = new(trigger.ActorID, trigger.PrimaryID, learned.Geometry, learned.Kind, origin, target, trigger.Rotation,
                learned.P1, learned.P2, episode.Activation, learned.Confidence,
                $"context {trigger.ActorOID:X}; {learned.Observations} observations; {learned.AmbiguousSamples} ambiguous");
        }
        else if (trigger.Kind == ObservationKind.CastStart && _store.Mechanics.TryGetValue(trigger.PrimaryID, out var fallback)
            && fallback.Geometry != GeometryKind.Unknown)
        {
            var target = new Vector2(trigger.TargetX, trigger.TargetZ);
            var source = new Vector2(trigger.X, trigger.Z);
            var origin = fallback.Geometry is GeometryKind.Circle or GeometryKind.Donut ? target : source;
            _predictions[episode.ID] = new(trigger.ActorID, trigger.PrimaryID, fallback.Geometry, fallback.Kind, origin, target, trigger.Rotation,
                fallback.P1, fallback.P2, episode.Activation, fallback.Confidence, $"global fallback; {fallback.Observations} observations");
        }
    }

    private bool CorrelateObservation(ForetellObservation observation)
    {
        if (observation.Kind is ObservationKind.ActorAdded or ObservationKind.ActorRemoved or ObservationKind.PositionSample
            or ObservationKind.CastStart or ObservationKind.CastFinish or ObservationKind.RenderFlagsChanged)
            return false;

        var episode = BestEpisode(observation);
        if (episode == null) return false;
        episode.AddEvidence(observation.Kind);

        switch (observation.Kind)
        {
            case ObservationKind.AffectedTarget:
                if (observation.TargetID != 0) episode.AffectedTargets.Add(observation.TargetID);
                break;
            case ObservationKind.StatusGain:
                if (observation.TargetID != 0) episode.StatusTargets.Add(observation.TargetID);
                break;
            case ObservationKind.TetherStart:
                if (observation.TargetID != 0) episode.TetherTargets.Add(observation.TargetID);
                break;
            case ObservationKind.Icon:
            case ObservationKind.VFX:
                if (observation.TargetID != 0 && episode.ParticipantPositions.ContainsKey(observation.TargetID))
                    episode.AffectedTargets.Add(observation.TargetID);
                break;
            case ObservationKind.Displacement:
                if (observation.ActorID != 0)
                {
                    episode.MovementTargets.Add(observation.ActorID);
                    episode.MovementDistances[observation.ActorID] = Math.Max(episode.MovementDistances.GetValueOrDefault(observation.ActorID), observation.Value1);
                }
                break;
            case ObservationKind.DeathChanged:
                if (observation.Flag && observation.ActorID != 0 && episode.ParticipantPositions.ContainsKey(observation.ActorID))
                    episode.DeathTargets.Add(observation.ActorID);
                break;
        }
        return true;
    }

    private MechanicEpisode? BestEpisode(ForetellObservation observation)
    {
        MechanicEpisode? best = null;
        var bestScore = double.MaxValue;
        foreach (var episode in _episodes.Values)
        {
            if (episode.Finalized || observation.At < episode.Trigger.At.AddSeconds(-.25) || observation.At > episode.FinalizeAt.AddSeconds(.75))
                continue;
            var score = Math.Abs((observation.At - episode.Activation).TotalSeconds);
            if (observation.ActorID != 0 && observation.ActorID == episode.Trigger.ActorID) score -= 1.5;
            if (observation.PrimaryID != 0 && observation.PrimaryID == episode.Trigger.PrimaryID) score -= 1;
            if (observation.TargetID != 0 && episode.ParticipantPositions.ContainsKey(observation.TargetID)) score -= .2;
            if (score < bestScore)
            {
                bestScore = score;
                best = episode;
            }
        }
        return best;
    }

    private void HandlePositionSample(ForetellObservation observation, bool replaying)
    {
        var current = new Vector2(observation.X, observation.Z);
        if (_tracks.TryGetValue(observation.ActorID, out var previous) && !replaying)
        {
            var dt = (observation.At - previous.At).TotalSeconds;
            var distance = Vector2.Distance(previous.Position, current);
            if (dt is >= .08 and <= .75 && distance is >= 2.6f and <= 35f)
            {
                var delta = current - previous.Position;
                var displacement = new ForetellObservation
                {
                    Sequence = ++_sequence,
                    At = observation.At,
                    TerritoryID = observation.TerritoryID,
                    Kind = ObservationKind.Displacement,
                    SourceKind = SourceKind.Player,
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
        _tracks[observation.ActorID] = new() { At = observation.At, Position = current, Role = observation.SecondaryID, RoleName = observation.Detail };
    }

    private void FinalizeDue(DateTime now)
    {
        foreach (var episode in _episodes.Values.Where(e => !e.Finalized && e.FinalizeAt <= now).ToArray())
            FinalizeEpisode(episode);
        foreach (var id in _episodes.Where(kv => kv.Value.Finalized && kv.Value.FinalizeAt.AddSeconds(20) < now).Select(kv => kv.Key).ToArray())
            _episodes.Remove(id);
    }

    private void FinalizeEpisode(MechanicEpisode episode)
    {
        if (episode.Finalized) return;
        episode.Finalized = true;
        ++_session.MechanicsFinalized;

        var encounter = Encounter(episode.Trigger.TerritoryID);
        var key = episode.SignalKey;
        var isNew = !encounter.Mechanics.TryGetValue(key, out var mechanic);
        if (mechanic == null)
        {
            mechanic = new()
            {
                Key = key,
                TerritoryID = episode.Trigger.TerritoryID,
                SourceOID = episode.Trigger.ActorOID,
                SourceKind = episode.Trigger.SourceKind,
                TriggerKind = episode.Trigger.Kind,
                TriggerID = episode.Trigger.PrimaryID,
                FirstSeen = DateTime.UtcNow
            };
            encounter.Mechanics[key] = mechanic;
            ++_session.NewMechanics;
        }
        mechanic.Samples ??= [];

        var affected = new HashSet<ulong>(episode.AffectedTargets);
        affected.UnionWith(episode.StatusTargets);
        AddGeometrySamples(mechanic, episode, affected);
        var fit = FitNormalizedGeometry(mechanic.Samples);
        var kind = ClassifyEpisode(episode, affected, fit);
        var score = EvidenceScore(kind, fit);

        var previousKind = mechanic.Kind;
        var previousGeometry = mechanic.Geometry;
        ++mechanic.Observations;
        mechanic.AffectedSamples += episode.AffectedTargets.Count;
        mechanic.StatusSamples += episode.StatusTargets.Count;
        mechanic.MovementSamples += episode.MovementTargets.Count;
        mechanic.DeathSamples += episode.DeathTargets.Count;
        mechanic.MeanLeadSeconds += (episode.LeadSeconds - mechanic.MeanLeadSeconds) / mechanic.Observations;
        mechanic.LastSeen = DateTime.UtcNow;
        foreach (var (evidence, count) in episode.Evidence)
            mechanic.Evidence[evidence] = mechanic.Evidence.GetValueOrDefault(evidence) + count;

        var agrees = previousKind == MechanicKind.Unknown || kind == MechanicKind.Unknown || previousKind == kind;
        var geometryAgrees = previousGeometry == GeometryKind.Unknown || fit == null || previousGeometry == fit.Value.Geometry;
        if (agrees && geometryAgrees && (kind != MechanicKind.Unknown || fit != null))
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

        var features = BuildEpisodeFeatures(episode, affected, fit);
        if (_cfg.EnableLearning && _cfg.EnableML && kind != MechanicKind.Unknown)
            _classifier.Train(features, kind);
        var ml = _cfg.EnableML ? _classifier.Predict(features) : (MechanicKind.Unknown, 0f);

        if (_cfg.EnableLearning && episode.Trigger.Kind == ObservationKind.CastStart && fit is FitResult globalFit)
            UpdateGlobalMechanic(episode, globalFit, kind);

        _lastEvidence = $"{key}: {mechanic.Kind}/{mechanic.Geometry} {mechanic.Confidence:P0}; evidence {episode.Evidence.Count}; " +
            $"affected {affected.Count}/{episode.ParticipantPositions.Count}; move {episode.MovementTargets.Count}; ML {ml.Item1} {ml.Item2:P0}";
    }

    private static void AddGeometrySamples(ContextualMechanic mechanic, MechanicEpisode episode, HashSet<ulong> affected)
    {
        var source = new Vector2(episode.Trigger.X, episode.Trigger.Z);
        var target = new Vector2(episode.Trigger.TargetX, episode.Trigger.TargetZ);
        var s = MathF.Sin(episode.Trigger.Rotation);
        var c = MathF.Cos(episode.Trigger.Rotation);
        foreach (var (id, position) in episode.ParticipantPositions)
        {
            var d = position - source;
            mechanic.Samples.Add(new()
            {
                Side = d.X * c - d.Y * s,
                Forward = d.X * s + d.Y * c,
                TargetDX = position.X - target.X,
                TargetDZ = position.Y - target.Y,
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
        void Try(GeometryKind kind, float p1, float p2, Func<MechanicSamplePoint, bool> contains)
        {
            var score = NormalizedScore(samples, contains);
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
        for (var range = 8f; range <= 50f; range += 4f)
            foreach (var halfDeg in new[] { 15f, 22.5f, 30f, 45f, 60f, 90f })
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
                Try(GeometryKind.Rectangle, length, halfWidth, p => p.Forward >= 0 && p.Forward <= length && MathF.Abs(p.Side) <= halfWidth);

        return best.Score >= .58f ? best : null;
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

    private static MechanicKind ClassifyEpisode(MechanicEpisode episode, HashSet<ulong> affected, FitResult? fit)
    {
        var participants = episode.ParticipantPositions.Count;
        if (episode.TetherTargets.Count > 0) return MechanicKind.Tether;
        if (episode.MovementTargets.Count > 0)
        {
            if (participants >= 2 && episode.MovementTargets.Count >= Math.Ceiling(participants * .5)) return MechanicKind.Knockback;
            if (episode.MovementTargets.Count >= 1 && episode.Evidence.ContainsKey(ObservationKind.StatusGain)) return MechanicKind.ForcedMovement;
        }
        if (episode.StatusTargets.Count > 0 && episode.AffectedTargets.Count == 0) return MechanicKind.Debuff;
        if (participants >= 3 && affected.Count >= Math.Ceiling(participants * .75)) return MechanicKind.Raidwide;

        if (affected.Count is > 0 and <= 2)
        {
            var allTanks = affected.All(id => episode.ParticipantRoleNames.GetValueOrDefault(id).Contains("Tank", StringComparison.OrdinalIgnoreCase));
            if (participants >= 4 && allTanks) return MechanicKind.Tankbuster;
        }

        if (episode.Evidence.ContainsKey(ObservationKind.Icon) && affected.Count > 1)
        {
            var positions = affected.Where(episode.ParticipantPositions.ContainsKey).Select(id => episode.ParticipantPositions[id]).ToArray();
            if (positions.Length > 1)
            {
                double average = 0;
                var pairs = 0;
                for (var i = 0; i < positions.Length; ++i)
                    for (var j = i + 1; j < positions.Length; ++j)
                    {
                        average += Vector2.Distance(positions[i], positions[j]);
                        ++pairs;
                    }
                average /= Math.Max(1, pairs);
                return average > 7 ? MechanicKind.Spread : MechanicKind.Stack;
            }
        }
        if (episode.Evidence.ContainsKey(ObservationKind.Icon) && affected.Count == 1) return MechanicKind.TargetedAOE;
        if (fit is FitResult f && f.Score >= .62f) return MechanicKind.GroundAOE;
        if (episode.Trigger.SourceKind is SourceKind.Environment or SourceKind.EventObject) return MechanicKind.Environment;
        if (episode.Trigger.Kind == ObservationKind.DirectorUpdate) return MechanicKind.Transition;
        return MechanicKind.Unknown;
    }

    private static float EvidenceScore(MechanicKind kind, FitResult? fit) => kind switch
    {
        MechanicKind.Raidwide => .82f,
        MechanicKind.Tankbuster => .78f,
        MechanicKind.Tether => .78f,
        MechanicKind.Knockback => .78f,
        MechanicKind.ForcedMovement => .70f,
        MechanicKind.Stack or MechanicKind.Spread => .74f,
        MechanicKind.Debuff => .68f,
        MechanicKind.TargetedAOE => .66f,
        MechanicKind.Environment or MechanicKind.Transition => .58f,
        MechanicKind.GroundAOE => fit?.Score ?? .60f,
        _ => fit?.Score ?? .25f
    };

    private static double[] BuildEpisodeFeatures(MechanicEpisode episode, HashSet<ulong> affected, FitResult? fit)
    {
        var n = Math.Max(1, episode.ParticipantPositions.Count);
        return
        [
            Math.Clamp(episode.LeadSeconds / 10d, 0, 1),
            Math.Clamp(affected.Count / (double)n, 0, 1),
            Math.Clamp(episode.StatusTargets.Count / (double)n, 0, 1),
            Math.Clamp(episode.MovementTargets.Count / (double)n, 0, 1),
            episode.TetherTargets.Count > 0 ? 1 : 0,
            episode.Evidence.ContainsKey(ObservationKind.Icon) ? 1 : 0,
            episode.Evidence.ContainsKey(ObservationKind.VFX) ? 1 : 0,
            fit?.Score ?? 0,
            episode.Trigger.SourceKind is SourceKind.Environment or SourceKind.EventObject ? 1 : 0,
            Math.Clamp(episode.DeathTargets.Count / (double)n, 0, 1)
        ];
    }

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
                LastSeen = DateTime.UtcNow
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
        mechanic.LastSeen = DateTime.UtcNow;
    }

    private void TrimRecentSignals(DateTime cutoff)
    {
        while (_recentSignals.TryPeek(out var observation) && observation.At < cutoff)
            _recentSignals.Dequeue();
    }
}
