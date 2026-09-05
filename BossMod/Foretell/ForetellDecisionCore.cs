using System.Numerics;

namespace BossMod.Foretell;

public enum HazardBinding { Fixed, Source, Target, LineEndpoints }
public sealed record HazardVertex(float X, float Z);
public sealed record HazardStage
{
    public float Delay { get; set; }
    public float Duration { get; set; } = 1.5f;
    public GeometryKind Geometry { get; set; }
    public float P1 { get; set; }
    public float P2 { get; set; }
    public float OffsetX { get; set; }
    public float OffsetZ { get; set; }
    public float RotationOffset { get; set; }
    public HazardVertex[] Polygon { get; set; } = [];
    public HazardBinding Binding { get; set; }
    public int Observations { get; set; }
    public double DelayM2 { get; set; }
    public int Hits { get; set; }
    public int Misses { get; set; }
    public uint EffectAction { get; set; }
    public float Reliability => ForetellInferenceCore.WilsonLowerBound(Hits, Hits + Misses);
}

public sealed record DecisionHazard(long ID, ActivePrediction Prediction, DateTime ActiveUntil,
    bool SpatiallyKnown, bool AdvisoryOnly, string Provenance);
public sealed record DecisionFrame(DateTime At, DecisionHazard[] Hazards, bool TerrainFresh, bool EvidenceComplete);
public sealed record RouteAssessment(bool Eligible, Vector2 Destination, float TravelSeconds, float Confidence, string Reason);

public static class ForetellDecisionCore
{
    // A mechanic can have many simultaneous casters. Apply the configured clutter budget to groups, then
    // preserve every footprint in the selected group (up to the independent 64-shape rendering safety bound).
    public static ActivePrediction[] SelectForDisplay(DecisionFrame frame, float threshold, int maxGroups)
    {
        var groups = new List<(string Key, uint Action, DateTime At)>();
        var result = new List<ActivePrediction>();
        foreach (var p in frame.Hazards.Select(h => h.Prediction).OrderBy(p => p.Activation))
        {
            if (!Valid(p) || p.Geometry == GeometryKind.Unknown && p.Guidance == GuidanceKind.None
                || p.Confidence < threshold && p.Provenance != "Terrain cue") continue;
            var known = groups.Any(g => g.Key == p.SignalKey && g.Action == p.ActionID && Math.Abs((g.At - p.Activation).TotalSeconds) <= .75);
            if (!known)
            {
                if (groups.Count >= Math.Max(1, maxGroups)) continue;
                groups.Add((p.SignalKey, p.ActionID, p.Activation));
            }
            if (result.Count == 64) break;
            result.Add(p);
        }
        return result.ToArray();
    }

    public static bool Valid(ActivePrediction p)
        => Enum.IsDefined(p.Geometry) && Enum.IsDefined(p.Kind) && Enum.IsDefined(p.Guidance) && Enum.IsDefined(p.Binding)
            && (p.Geometry is GeometryKind.Unknown or GeometryKind.Polygon || ForetellInferenceCore.GeometryParametersComplete(p.Geometry, p.P1, p.P2))
            && float.IsFinite(p.Origin.X) && float.IsFinite(p.Origin.Y) && float.IsFinite(p.Target.X) && float.IsFinite(p.Target.Y)
            && float.IsFinite(p.Rotation) && float.IsFinite(p.P1) && p.P1 is >= 0 and <= 200
            && float.IsFinite(p.P2) && p.P2 is >= 0 and <= 200 && float.IsFinite(p.Confidence) && p.Confidence is >= 0 and <= 1
            && float.IsFinite(p.LineMinimumLength) && p.LineMinimumLength is >= 0 and <= 200
            && float.IsFinite(p.Velocity.X) && float.IsFinite(p.Velocity.Y) && p.Velocity.LengthSquared() <= 10000
            && (p.Geometry != GeometryKind.Polygon || p.Polygon is { Count: >= 3 and <= 256 }
                && p.Polygon.All(v => float.IsFinite(v.X) && float.IsFinite(v.Z) && Math.Abs(v.X) <= 200 && Math.Abs(v.Z) <= 200));

    public static bool StageMatches(HazardStage expected, HazardStage observed)
        => expected.EffectAction == observed.EffectAction && expected.Geometry == observed.Geometry
            && Math.Abs(expected.Delay - observed.Delay) <= .5f
            && Math.Abs(expected.OffsetX - observed.OffsetX) <= 1 && Math.Abs(expected.OffsetZ - observed.OffsetZ) <= 1
            && Math.Abs(MathF.IEEERemainder(expected.RotationOffset - observed.RotationOffset, MathF.Tau)) <= .15f
            && ForetellInferenceCore.GeometryMatches(expected.Geometry, expected.P1, expected.P2, observed.Geometry, observed.P1, observed.P2);

    public static Vector2 OriginAt(ActivePrediction prediction, DateTime at)
    {
        if (prediction.MotionUntil == default || prediction.CreatedAt == default) return prediction.Origin;
        var elapsed = Math.Clamp((float)((at < prediction.MotionUntil ? at : prediction.MotionUntil) - prediction.CreatedAt).TotalSeconds, 0, 12);
        return prediction.Origin + prediction.Velocity * elapsed;
    }

    public static bool Contains(ActivePrediction p, Vector2 point, DateTime at, float margin = 0)
    {
        var delta = point - OriginAt(p, at);
        var sine = MathF.Sin(p.Rotation); var cosine = MathF.Cos(p.Rotation);
        var side = delta.X * cosine - delta.Y * sine;
        var forward = delta.X * sine + delta.Y * cosine;
        return p.Geometry switch
        {
            GeometryKind.Circle => delta.Length() <= p.P1 + margin,
            GeometryKind.Donut => delta.Length() >= Math.Max(0, p.P1 - margin) && delta.Length() <= p.P2 + margin,
            GeometryKind.Cone => delta.Length() <= p.P1 + margin && (delta.Length() <= margin
                || MathF.Abs(MathF.Atan2(side, forward)) <= p.P2 + MathF.Atan2(margin, Math.Max(.1f, delta.Length()))),
            GeometryKind.Rectangle => forward >= -margin && forward <= p.P1 + margin && MathF.Abs(side) <= p.P2 + margin,
            GeometryKind.Cross => MathF.Abs(forward) <= p.P1 + margin && MathF.Abs(side) <= p.P2 + margin
                || MathF.Abs(side) <= p.P1 + margin && MathF.Abs(forward) <= p.P2 + margin,
            GeometryKind.Polygon => PolygonContains(p.Polygon, new(side, forward), margin),
            _ => false
        };
    }

    private static bool PolygonContains(IReadOnlyList<HazardVertex>? vertices, Vector2 point, float margin)
    {
        if (vertices == null || vertices.Count < 3 || vertices.Count > 256) return false;
        var inside = false;
        for (var i = 0; i < vertices.Count; ++i)
        {
            var a = new Vector2(vertices[i].X, vertices[i].Z);
            var b = new Vector2(vertices[(i + 1) % vertices.Count].X, vertices[(i + 1) % vertices.Count].Z);
            var ab = b - a;
            var closest = a + ab * Math.Clamp(Vector2.Dot(point - a, ab) / Math.Max(.00001f, ab.LengthSquared()), 0, 1);
            if (margin > 0 && Vector2.DistanceSquared(point, closest) <= margin * margin) return true;
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    public static RouteAssessment AssessRoute(DecisionFrame frame, Vector2 start, Vector2 destination,
        Func<Vector2, Vector2, bool> traversable, float speed = 4, float margin = .8f)
    {
        RouteAssessment Reject(string reason) => new(false, destination, 0, 0, reason);
        if (frame.Hazards.Length > 128 || frame.Hazards.Any(h => !Valid(h.Prediction))) return Reject("Hazard geometry is incomplete or invalid.");
        if (!frame.EvidenceComplete) return Reject("Capture incomplete: a route cannot be assessed.");
        if (!frame.TerrainFresh) return Reject("Terrain is refreshing.");
        if (!float.IsFinite(margin) || margin < 0) return Reject("Movement clearance unavailable.");
        if (!float.IsFinite(speed) || speed <= 0 || !float.IsFinite(start.X) || !float.IsFinite(start.Y)
            || !float.IsFinite(destination.X) || !float.IsFinite(destination.Y)) return Reject("Movement context unavailable.");
        if (!traversable(start, destination)) return Reject("The direct route crosses an obstacle or unknown ground.");
        var relevant = frame.Hazards.Where(h => h.ActiveUntil >= frame.At && (h.Prediction.Activation - frame.At).TotalSeconds <= 12).ToArray();
        if (relevant.Any(h => !h.SpatiallyKnown && h.Prediction.Kind != MechanicKind.Gaze))
            return Reject("An active mechanic has unknown spatial or targeting requirements.");
        if (relevant.Any(h => h.Prediction.Guidance is GuidanceKind.Stack or GuidanceKind.Spread or GuidanceKind.Soak
            or GuidanceKind.Knockback or GuidanceKind.Tether or GuidanceKind.Tankbuster or GuidanceKind.LookAway or GuidanceKind.Move))
            return Reject("A personal mechanic requires a constraint that this route cannot prove.");
        var travel = Vector2.Distance(start, destination) / speed;
        if (travel > 10) return Reject("Destination takes too long to reach.");
        var horizon = Math.Min(12, Math.Max(travel, relevant.Length == 0 ? travel
            : relevant.Max(h => (float)(h.ActiveUntil - frame.At).TotalSeconds)));
        foreach (var hazard in relevant)
        {
            if (!hazard.SpatiallyKnown) continue;
            var begins = Math.Max(0, (float)(hazard.Prediction.Activation - frame.At).TotalSeconds - .3f);
            var ends = Math.Min(horizon, (float)(hazard.ActiveUntil - frame.At).TotalSeconds);
            if (begins > ends) continue;
            // Sample at <= .1s with half-step spatial inflation, including the activation and destination hold.
            var steps = Math.Max(1, (int)MathF.Ceiling((ends - begins) / .1f));
            for (var i = 0; i <= steps; ++i)
            {
                var t = begins + (ends - begins) * i / steps;
                var position = Vector2.Lerp(start, destination, Math.Clamp(t / Math.Max(.0001f, travel), 0, 1));
                if (Contains(hazard.Prediction, position, frame.At.AddSeconds(t), margin + (speed + hazard.Prediction.Velocity.Length()) * .05f))
                    return Reject("The route or its arrival overlaps a credible danger at its activation time.");
            }
        }
        var confidence = relevant.Length == 0 ? 0 : relevant.Min(h => h.Prediction.Confidence);
        return new(true, destination, travel, confidence, "Direct route assessed against known hazards; assumes normal walking speed.");
    }

    public static DecisionHazard[] Prioritize(DecisionFrame frame, Vector2 player, ulong playerID)
        => frame.Hazards.Where(h => h.ActiveUntil >= frame.At)
            .OrderByDescending(h => h.Prediction.TargetID == playerID && playerID != 0
                || h.SpatiallyKnown && Contains(h.Prediction, player, h.Prediction.Activation, .5f))
            .ThenBy(h => h.Prediction.Activation).ThenByDescending(h => h.Prediction.Confidence)
            .ThenBy(h => h.ID).ToArray();
}

public readonly record struct SpatialOutcomePoint(Vector2 Position, bool Hit);

public static class ForetellOutcomeValidation
{
    public static bool? VerifyTiming(DateTime due, DateTime expires, DateTime observed, bool complete)
        => !complete || expires < due ? null : Math.Abs((observed - due).TotalSeconds) <= (expires - due).TotalSeconds;

    // A trial tests the issued footprint at observed positions and impact time, not every point in the world.
    public static bool? Verify(ActivePrediction prediction, MechanicKind observedKind, DateTime impact,
        IReadOnlyList<SpatialOutcomePoint> points, bool complete)
    {
        if (!complete || !ForetellDecisionCore.Valid(prediction) || prediction.CreatedAt == default
            || impact == default || (impact - prediction.CreatedAt).TotalSeconds < .2) return null;
        if (prediction.Geometry == GeometryKind.Unknown)
        {
            if (prediction.Kind is MechanicKind.Unknown or MechanicKind.Marker || observedKind == MechanicKind.Unknown) return null;
            return prediction.Kind == observedKind && Math.Abs((impact - prediction.Activation).TotalSeconds) <= 1;
        }
        var informative = points.Where(p => float.IsFinite(p.Position.X) && float.IsFinite(p.Position.Y)).ToArray();
        if (informative.Count(p => p.Hit) < 2 || informative.Count(p => !p.Hit) < 2) return null;
        // Near-edge poses cannot resolve an outcome reliably at packet/frame precision.
        var clear = informative.Where(p => new[] { new Vector2(.5f, 0), new(-.5f, 0), new(0, .5f), new(0, -.5f) }
            .All(offset => ForetellDecisionCore.Contains(prediction, p.Position + offset, impact)
                == ForetellDecisionCore.Contains(prediction, p.Position, impact))).ToArray();
        if (clear.Count(p => p.Hit) < 2 || clear.Count(p => !p.Hit) < 2) return null;
        return Math.Abs((impact - prediction.Activation).TotalSeconds) <= 1
            && clear.All(p => ForetellDecisionCore.Contains(prediction, p.Position, impact) == p.Hit)
            && (observedKind == MechanicKind.Unknown || prediction.Kind == MechanicKind.Unknown || prediction.Kind == observedKind);
    }
}
