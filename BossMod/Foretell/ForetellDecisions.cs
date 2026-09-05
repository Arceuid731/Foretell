using System.Diagnostics;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private DecisionFrame? _presentationFrame;
    private long _predictionRevision;
    private Task<(RouteAssessment Assessment, long Revision, DateTime At, Vector2 Start)>? _routeTask;
    private (RouteAssessment Assessment, long Revision, DateTime At, Vector2 Start)? _routeResult;
    private DateTime _lastRouteAttempt;

    private DecisionFrame PresentationFrame => _presentationFrame ??= BuildDecisionFrame(ObservationNow());

    private DecisionFrame BuildDecisionFrame(DateTime now)
    {
        List<DecisionHazard> hazards = [];
        var invalidGeometry = false;
        foreach (var (id, stored) in _predictions.OrderBy(p => p.Key))
        {
            if (!ValidPrediction(stored)) { invalidGeometry = true; continue; }
            var prediction = stored;
            if (prediction.Binding == HazardBinding.LineEndpoints && now < prediction.Activation)
                prediction = ResolveLineEndpoints(prediction);
            if (prediction.Binding != HazardBinding.Fixed && now < prediction.Activation)
            {
                var actor = _ws.Actors.Find(prediction.Binding == HazardBinding.Target ? prediction.TargetID : prediction.CasterID);
                if (actor != null) prediction = prediction with { Origin = V(actor.Position) };
            }
            var hasShape = prediction.Geometry != GeometryKind.Unknown;
            hazards.Add(new(id, prediction, prediction.Activation.AddSeconds(1.5), hasShape, false, prediction.Provenance));
            var stages = prediction.Stages ?? [];
            for (var index = 0; index < stages.Count; ++index)
            {
                var stage = stages[index];
                if (stage.Observations < 3 || stage.Delay < 0 || stage.Delay > 12 || stage.Misses >= stage.Hits + 2) continue;
                var reliability = stage.Hits + stage.Misses >= 3 ? stage.Reliability : .78f;
                var sine = MathF.Sin(prediction.Rotation); var cosine = MathF.Cos(prediction.Rotation);
                var origin = prediction.Origin + new Vector2(stage.OffsetX * cosine + stage.OffsetZ * sine,
                    -stage.OffsetX * sine + stage.OffsetZ * cosine);
                var component = prediction with
                {
                    Geometry = stage.Geometry, Origin = origin, Rotation = prediction.Rotation + stage.RotationOffset,
                    P1 = stage.P1, P2 = stage.P2, Polygon = stage.Polygon,
                    Activation = prediction.Activation.AddSeconds(stage.Delay), Confidence = Math.Min(prediction.Confidence, reliability),
                    Stages = null, Binding = stage.Binding, Provenance = $"Sequence hypothesis · stage {index + 2}",
                    Label = prediction.Label + $" · {index + 2}"
                };
                hazards.Add(new(unchecked(id * 17 + index + 1), component, component.Activation.AddSeconds(stage.Duration),
                    component.Geometry != GeometryKind.Unknown, true, component.Provenance));
            }
        }
        foreach (var warning in ActiveDynamicTerrainWarnings())
        {
            var vertices = warning.Points.Select(p => new HazardVertex(p.X - warning.Center.X, p.Y - warning.Center.Y)).ToArray();
            var prediction = new ActivePrediction(0, 0, GeometryKind.Polygon, MechanicKind.Environment, warning.Center,
                warning.Center, 0, 0, 0, now, .7f, "Structural animation; missing floor not yet established", Guidance: GuidanceKind.None,
                Label: "Possible floor change") { Polygon = vertices, Provenance = "Terrain cue" };
            hazards.Add(new(unchecked((long)warning.ActorID), prediction, warning.Expires, true, true, prediction.Provenance));
        }
        var complete = !invalidGeometry && hazards.Count <= 128 && (now - _lastOutcomeGapAt).TotalSeconds > 12 && !PerformanceThrottled && _semanticBudgetFrameTicks != _semanticBudgetTrippedFrameTicks;
        return new(now, hazards.Where(h => h.ActiveUntil >= now).Take(128).ToArray(), HasFreshTopologyEvidence, complete);
    }

    private ActivePrediction ResolveLineEndpoints(ActivePrediction prediction)
    {
        var source = _ws.Actors.Find(prediction.CasterID);
        var target = _ws.Actors.Find(prediction.TargetID);
        if (source == null || target == null) return prediction;
        var delta = V(target.Position) - V(source.Position);
        return prediction with { Origin = V(source.Position), Target = V(target.Position),
            Rotation = MathF.Atan2(delta.X, delta.Y), P1 = Math.Max(prediction.LineMinimumLength, delta.Length()) };
    }

    private void LockCastGeometry(ForetellObservation observation)
    {
        if (observation.Kind != ObservationKind.CastFinish) return;
        foreach (var id in _predictions.Where(pair => pair.Value.CasterID == observation.ActorID
            && pair.Value.ActionID == observation.PrimaryID && pair.Value.Binding == HazardBinding.LineEndpoints).Select(pair => pair.Key).ToArray())
        {
            _predictions[id] = ResolveLineEndpoints(_predictions[id]) with { Binding = HazardBinding.Fixed };
            if (_episodes.GetValueOrDefault(id) is { } episode)
            { episode.ForecastSnapshot = _predictions[id]; episode.ForecastOrigin = _predictions[id].Origin; episode.ForecastRotation = _predictions[id].Rotation; }
            ++_predictionRevision;
        }
    }

    private static DateTime PredictionEnd(ActivePrediction prediction)
        => prediction.Activation.AddSeconds(Math.Max(1.5, prediction.Stages?.Max(stage => (double?)stage.Delay + stage.Duration) ?? 0));

    private void PollRouteRecommendation(DecisionFrame frame, Vector2 player)
    {
        if (_routeTask is { IsCompleted: true } completed)
        {
            try { _routeResult = completed.GetAwaiter().GetResult(); }
            catch { _routeResult = null; }
            _routeTask = null;
        }
        if (_routeTask != null || (frame.At - _lastRouteAttempt).TotalSeconds < .25 || !frame.TerrainFresh) return;
        _lastRouteAttempt = frame.At;
        if (!frame.Hazards.Any(h => h.SpatiallyKnown && ForetellDecisionCore.Contains(h.Prediction, player, h.Prediction.Activation)))
        { _routeResult = null; return; }
        var grid = new ForetellTopologyGrid();
        grid.ReplaceWith(_topology);
        var connected = _topologyAnalysis?.ConnectedCells.ToArray();
        var arena = CurrentArenaBoundary?.Points.ToArray();
        var revision = _predictionRevision;
        _routeTask = Task.Run(() =>
        {
            var result = new RouteAssessment(false, player, 0, 0, "No assessable direct route found.");
            var started = Stopwatch.GetTimestamp();
            for (var ring = 2f; ring <= 24; ring += 2)
                for (var i = 0; i < 32; ++i)
                {
                    if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > 25) return (result, revision, frame.At, player);
                    var angle = MathF.Tau * i / 32;
                    var target = player + ring * new Vector2(MathF.Sin(angle), MathF.Cos(angle));
                    result = ForetellDecisionCore.AssessRoute(frame, player, target, (a, b) => grid.CanTraverseSegment(a, b, connected) && RouteInsideArena(a, b, arena));
                    if (result.Eligible) return (result, revision, frame.At, player);
                }
            return (result, revision, frame.At, player);
        });
    }

    private static bool RouteInsideArena(Vector2 from, Vector2 to, IReadOnlyList<Vector2>? arena)
    {
        if (arena == null) return true;
        var steps = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(from, to) / .25f));
        for (var i = 0; i <= steps; ++i)
            if (!ForetellArenaBoundaryCore.Contains(arena, Vector2.Lerp(from, to, i / (float)steps))) return false;
        return true;
    }

    private void RecordEpisodeComponent(MechanicEpisode episode, ForetellObservation observation)
    {
        if (observation.Kind != ObservationKind.ActionResolved) return;
        if (episode.FirstResolvedAt == default) { episode.FirstResolvedAt = observation.At; return; }
        if (observation.Prior is not { Geometry: not GeometryKind.Unknown } prior || episode.Components.Count >= 8) return;
        var delay = (float)(observation.At - episode.FirstResolvedAt).TotalSeconds;
        if (delay < .1f || delay > 12) return;
        var source = episode.ForecastSnapshot?.Origin ?? new Vector2(episode.Trigger.X, episode.Trigger.Z);
        var origin = prior.Geometry is GeometryKind.Circle or GeometryKind.Donut
            ? new Vector2(observation.TargetX, observation.TargetZ) : new Vector2(observation.X, observation.Z);
        if (observation.TargetID == 0 && !prior.TargetArea && prior.Geometry is GeometryKind.Circle or GeometryKind.Donut)
            origin = new(observation.X, observation.Z);
        var delta = origin - source;
        var rotation = episode.ForecastSnapshot?.Rotation ?? episode.Trigger.Rotation;
        var sine = MathF.Sin(rotation); var cosine = MathF.Cos(rotation);
        episode.Components.Add(new()
        {
            Delay = delay, Geometry = prior.Geometry, P1 = prior.P1, P2 = prior.P2, EffectAction = observation.PrimaryID,
            OffsetX = delta.X * cosine - delta.Y * sine, OffsetZ = delta.X * sine + delta.Y * cosine,
            RotationOffset = observation.Rotation - rotation
        });
    }

    private static void LearnEpisodeProgram(ContextualMechanic mechanic, MechanicEpisode episode)
    {
        var issued = episode.ForecastSnapshot?.Stages;
        for (var i = 0; i < Math.Max(mechanic.Stages.Count, episode.Components.Count); ++i)
        {
            var learned = i < mechanic.Stages.Count ? mechanic.Stages[i] : null;
            var observed = i < episode.Components.Count ? episode.Components[i] : null;
            if (learned != null && episode.FirstResolvedAt != default && issued != null && i < issued.Count && issued[i].Observations >= 3
                && episode.ForecastSnapshot!.Value.CreatedAt < episode.FirstResolvedAt.AddSeconds(-.2))
            {
                if (observed != null && ForetellDecisionCore.StageMatches(issued[i], observed)) ++learned.Hits;
                else ++learned.Misses;
            }
            if (observed == null) continue;
            if (learned == null) { mechanic.Stages.Add(observed with { Observations = 1 }); continue; }
            if (!ForetellDecisionCore.StageMatches(learned, observed)) continue;
            ++learned.Observations;
            var delta = observed.Delay - learned.Delay;
            learned.Delay += delta / learned.Observations;
            learned.DelayM2 += delta * (observed.Delay - learned.Delay);
            learned.OffsetX += (observed.OffsetX - learned.OffsetX) / learned.Observations;
            learned.OffsetZ += (observed.OffsetZ - learned.OffsetZ) / learned.Observations;
        }
    }
}
