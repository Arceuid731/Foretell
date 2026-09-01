namespace BossMod.Foretell;

public sealed class OnlineClassifier
{
    public const int BaseFeatureCount = 10;
    public const int FabricFeatureCount = 128;
    public const int FeatureCount = BaseFeatureCount + FabricFeatureCount;
    public const int ClassCount = 18;
    private readonly MLState _state;

    public OnlineClassifier(MLState state)
    {
        _state = state;
        var oldFeatureCount = Math.Max(0, state.FeatureCount);
        var old = state.Weights ?? [];
        var validClasses = state.ClassCount == ClassCount && old.Length == ClassCount;
        var validShape = validClasses && old.All(w => w != null && w.Length == oldFeatureCount + 1);
        if (!validShape || oldFeatureCount != FeatureCount)
        {
            var migrated = NewWeights();
            if (validShape)
            {
                for (var c = 0; c < ClassCount; ++c)
                {
                    var row = old[c];
                    var copy = Math.Min(oldFeatureCount, FeatureCount);
                    Array.Copy(row, migrated[c], copy);
                    if (row.Length > oldFeatureCount)
                        migrated[c][FeatureCount] = row[oldFeatureCount];
                }
            }
            state.FeatureCount = FeatureCount;
            state.ClassCount = ClassCount;
            state.Weights = migrated;
        }
    }

    public static double[][] NewWeights() => Enumerable.Range(0, ClassCount).Select(_ => new double[FeatureCount + 1]).ToArray();

    public (MechanicKind Kind, float Confidence) Predict(ReadOnlySpan<double> x)
    {
        Span<double> logits = stackalloc double[ClassCount];
        double max = double.NegativeInfinity;
        for (var c = 0; c < ClassCount; ++c)
        {
            var w = _state.Weights[c];
            var z = w[FeatureCount];
            for (var i = 0; i < FeatureCount && i < x.Length; ++i) z += w[i] * x[i];
            logits[c] = z;
            max = Math.Max(max, z);
        }
        double sum = 0;
        for (var c = 0; c < ClassCount; ++c) sum += Math.Exp(logits[c] - max);
        var best = 0;
        double bestP = 0;
        for (var c = 0; c < ClassCount; ++c)
        {
            var p = Math.Exp(logits[c] - max) / sum;
            if (p > bestP) { bestP = p; best = c; }
        }
        return ((MechanicKind)best, (float)bestP);
    }

    public void Train(ReadOnlySpan<double> x, MechanicKind label, double learningRate = .018)
    {
        var y = Math.Clamp((int)label, 0, ClassCount - 1);
        Span<double> logits = stackalloc double[ClassCount];
        double max = double.NegativeInfinity;
        for (var c = 0; c < ClassCount; ++c)
        {
            var w = _state.Weights[c];
            var z = w[FeatureCount];
            for (var i = 0; i < FeatureCount && i < x.Length; ++i) z += w[i] * x[i];
            logits[c] = z;
            max = Math.Max(max, z);
        }
        double sum = 0;
        for (var c = 0; c < ClassCount; ++c) sum += Math.Exp(logits[c] - max);
        for (var c = 0; c < ClassCount; ++c)
        {
            var p = Math.Exp(logits[c] - max) / sum;
            var error = (c == y ? 1d : 0d) - p;
            var w = _state.Weights[c];
            for (var i = 0; i < FeatureCount && i < x.Length; ++i) w[i] += learningRate * error * x[i];
            w[FeatureCount] += learningRate * error;
        }
        ++_state.Updates;
    }
}
