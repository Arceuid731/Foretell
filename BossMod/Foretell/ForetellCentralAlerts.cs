namespace BossMod.Foretell;

internal sealed record ForetellCentralAlert(string Cue, string Label, double Remaining, float Total, DateTime At, bool Personal, bool FromGuide);

internal static class ForetellCentralPresentation
{
    internal static bool Personal(ActivePrediction prediction, Vector2 position, ulong playerID)
        => playerID != 0 && (prediction.TargetID == playerID || ForetellDecisionCore.Contains(prediction, position, prediction.Activation, .5f));

    internal static ActivePrediction[] Predictions(IEnumerable<ActivePrediction> candidates)
    {
        List<ActivePrediction> result = [];
        foreach (var prediction in candidates)
        {
            if (prediction.GuideLinked && prediction.Geometry == GeometryKind.Unknown && prediction.Guidance == GuidanceKind.Marker) continue;
            if (result.Any(previous => SameOccurrence(previous, prediction))) continue;
            result.Add(prediction);
        }
        return result.ToArray();
    }

    private static bool SameOccurrence(ActivePrediction first, ActivePrediction second)
        => (first.ActionID != 0 ? first.ActionID == second.ActionID
                : second.ActionID == 0 && !string.IsNullOrEmpty(first.SignalKey) && first.SignalKey == second.SignalKey)
            && first.Guidance == second.Guidance && first.Kind == second.Kind
            && Math.Abs((first.Activation - second.Activation).TotalSeconds) <= .75;

    internal static ForetellCentralAlert[] Select(IEnumerable<ForetellCentralAlert> candidates, int maximum)
        => candidates.OrderByDescending(alert => alert.Personal).ThenBy(alert => alert.At).ThenByDescending(alert => alert.FromGuide)
            .Take(Math.Clamp(maximum, 1, 2)).ToArray();
}
