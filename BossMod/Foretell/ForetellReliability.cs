using System.Numerics;

namespace BossMod.Foretell;

public enum EvidenceMaturity { Unobserved, Hypothesis, Supported, Validated, Conflicted }

public sealed class MechanicHypothesis
{
    public MechanicKind Kind { get; set; }
    public int Supports { get; set; }
    public int Contradictions { get; set; }
    public string Reason { get; set; } = "";
    public float Score => Supports == 0 ? 0 : Supports / (float)(Supports + Contradictions + 2);
}

public readonly record struct ReliabilitySummary(EvidenceMaturity Maturity, int Observations, int Verified,
    int Hits, int Misses, int Unverifiable, float LowerBound, bool ClientShape, int AdditionalForVisual,
    int AdditionalForWarning, string Reason);

public static class ForetellReliability
{
    // This is a best-case sample requirement, not a promise that observing N casts establishes the mechanic.
    // Only independently assessable predictions count; correlated party hits are one outcome, not N trials.
    public static int AdditionalSuccesses(int hits, int misses, float target)
    {
        if (!float.IsFinite(target) || target <= 0 || target >= 1) return -1;
        hits = Math.Max(0, hits); misses = Math.Max(0, misses);
        if (ForetellInferenceCore.WilsonLowerBound(hits, hits + misses) >= target) return 0;
        if (ForetellInferenceCore.WilsonLowerBound(hits + 10_000, hits + misses + 10_000) < target) return -1;
        var low = 1; var high = 10_000;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (ForetellInferenceCore.WilsonLowerBound(hits + mid, hits + misses + mid) >= target) high = mid;
            else low = mid + 1;
        }
        return low;
    }

    public static ReliabilitySummary Describe(ContextualMechanic mechanic, float visual = .75f, float warning = .95f)
    {
        var hits = Math.Max(0, mechanic.ForecastHits);
        var misses = Math.Max(0, mechanic.ForecastMisses);
        var verified = hits + misses;
        var lower = ForetellInferenceCore.WilsonLowerBound(hits, verified);
        var maturity = mechanic.RecentContradictions >= 2 ? EvidenceMaturity.Conflicted
            : verified >= 3 && lower >= warning ? EvidenceMaturity.Validated
            : verified >= 3 && lower >= visual ? EvidenceMaturity.Supported
            : mechanic.Observations > 0 || mechanic.HasReliableActionPrior ? EvidenceMaturity.Hypothesis : EvidenceMaturity.Unobserved;
        var reason = maturity == EvidenceMaturity.Conflicted ? "Recent outcomes disagree; strong guidance is suspended."
            : mechanic.HasReliableActionPrior ? "The client supplies this shape. Independent outcome reliability is measured separately."
            : mechanic.Hypotheses.Count > 1 && mechanic.Kind == MechanicKind.Unknown ? "Several explanations remain compatible with the observations."
            : verified == 0 ? "No independent outcome has yet tested a prediction. Repetition alone is not validation."
            : "Only assessable predictions count. Unobserved outcomes and successful dodges may remain unverifiable.";
        return new(maturity, mechanic.Observations, verified, hits, misses, mechanic.UnverifiableOutcomes,
            lower, mechanic.HasReliableActionPrior, AdditionalSuccesses(hits, misses, visual),
            AdditionalSuccesses(hits, misses, warning), reason);
    }

    public static void ObserveHypotheses(ContextualMechanic mechanic, IEnumerable<(MechanicKind Kind, string Reason)> candidates)
    {
        var alternatives = candidates.DistinctBy(candidate => candidate.Kind).Take(18).ToArray();
        foreach (var candidate in alternatives)
        {
            var hypothesis = mechanic.Hypotheses.FirstOrDefault(h => h.Kind == candidate.Kind);
            if (hypothesis == null) mechanic.Hypotheses.Add(hypothesis = new() { Kind = candidate.Kind });
            hypothesis.Supports = Math.Min(1_000_000, hypothesis.Supports + 1);
            hypothesis.Reason = candidate.Reason;
        }
        // Absence of a cue does not refute a hypothesis. Contradictions require an independently resolved claim.
    }
}

public readonly record struct OutcomeCueSummary(int Participants, int Affected, int Statuses, int Displaced,
    bool Marker, bool Tether, bool TankTargets, bool GazeCorrelation, bool ProximityCorrelation,
    GeometryKind Geometry, float FitScore, int SpatialSamples);

public static class ForetellOutcomeHypotheses
{
    public static List<(MechanicKind Kind, string Reason)> Candidates(OutcomeCueSummary evidence)
    {
        List<(MechanicKind, string)> result = [];
        if (evidence.Geometry != GeometryKind.Unknown)
            result.Add((MechanicKind.GroundAOE, "Observed hit positions are compatible with a spatial attack."));
        if (evidence.Participants >= 3 && evidence.Affected >= Math.Ceiling(evidence.Participants * .75))
        {
            result.Add((MechanicKind.Raidwide, "Most participants were affected."));
            if (result.All(h => h.Item1 != MechanicKind.GroundAOE))
                result.Add((MechanicKind.GroundAOE, "An avoidable attack hitting most of the party is also possible."));
        }
        if (evidence.TankTargets && evidence.Affected > 0) result.Add((MechanicKind.Tankbuster, "Affected targets were tanks; targeting semantics need corroboration."));
        if (evidence.Marker)
        {
            result.Add((MechanicKind.Stack, "A marker can identify a shared-damage target."));
            result.Add((MechanicKind.Spread, "A marker can also identify a personal danger."));
        }
        if (evidence.GazeCorrelation) result.Add((MechanicKind.Gaze, "Facing and hits correlate; other spatial explanations remain possible."));
        if (evidence.ProximityCorrelation) result.Add((MechanicKind.Proximity, "Distance and damage correlate; mitigation and role may confound it."));
        if (evidence.Displaced > 0) result.Add((MechanicKind.Knockback, "Displacement observed; its cause and required response remain unconfirmed."));
        if (evidence.Tether) result.Add((MechanicKind.Tether, "A tether is observed; break/keep/transfer semantics are unknown."));
        if (evidence.Statuses > 0) result.Add((MechanicKind.Debuff, "A status was applied; its required response is unknown."));
        return result;
    }

    public static MechanicKind IndependentLabel(OutcomeCueSummary evidence)
        => evidence.Geometry != GeometryKind.Unknown && evidence.FitScore >= .9f && evidence.SpatialSamples >= 4
            && evidence.Affected >= 2 && evidence.SpatialSamples - evidence.Affected >= 2
                ? MechanicKind.GroundAOE : MechanicKind.Unknown;
}
