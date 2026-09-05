using System.Numerics;

namespace BossMod.Foretell;

public sealed class PreImpactClassEvidence
{
    public int Assessed { get; set; }
    public int Hits { get; set; }
    public int Abstained { get; set; }
    public double BrierSum { get; set; }
    public double LeadSecondsSum { get; set; }
    public float Reliability => ForetellInferenceCore.WilsonLowerBound(Hits, Assessed);
}

public sealed class PreImpactMemory
{
    public int FeatureSchema { get; set; } = 1;
    public MLState Model { get; set; } = new();
    public Dictionary<MechanicKind, PreImpactClassEvidence> Classes { get; set; } = [];
    public int MissingOutcomes { get; set; }
}

public readonly record struct PreImpactGuess(MechanicKind Kind, float Probability, float Reliability);

public sealed class ForetellPreImpactModel
{
    private readonly PreImpactMemory _memory;
    private readonly OnlineClassifier _model;
    private Dictionary<MechanicKind, float>? _frozenReliability;
    public ForetellPreImpactModel(PreImpactMemory memory)
    {
        _memory = memory;
        if (memory.FeatureSchema != 1) { memory.Model = new(); memory.Classes = []; memory.FeatureSchema = 1; }
        memory.Classes ??= [];
        memory.Classes = memory.Classes.Where(p => p.Value != null && Enum.IsDefined(p.Key)).Take(18).ToDictionary();
        foreach (var evidence in memory.Classes.Values)
        {
            evidence.Assessed = Math.Clamp(evidence.Assessed, 0, 1_000_000);
            evidence.Hits = Math.Clamp(evidence.Hits, 0, evidence.Assessed);
            evidence.Abstained = Math.Clamp(evidence.Abstained, 0, evidence.Assessed);
            evidence.BrierSum = double.IsFinite(evidence.BrierSum) ? Math.Max(0, evidence.BrierSum) : 0;
            evidence.LeadSecondsSum = double.IsFinite(evidence.LeadSecondsSum) ? Math.Max(0, evidence.LeadSecondsSum) : 0;
        }
        memory.Model ??= new(); memory.Classes ??= [];
        _model = new(memory.Model);
    }

    public PreImpactGuess Predict(double[] frozenFeatures)
    {
        var (kind, probability) = _model.Predict(frozenFeatures);
        return new(kind, probability, _frozenReliability?.GetValueOrDefault(kind) ?? _memory.Classes.GetValueOrDefault(kind)?.Reliability ?? 0);
    }
    public void FreezeCalibration() => _frozenReliability = _memory.Classes.ToDictionary(pair => pair.Key, pair => pair.Value.Reliability);

    // Evaluation happens before training, against a label obtained without this prediction. The frozen features
    // are exactly those used before impact; later hit/damage/status/position data can never enter the input.
    public void Resolve(double[] features, PreImpactGuess issued, MechanicKind independentLabel, double leadSeconds,
        bool complete, bool train = true)
    {
        if (!complete || !ForetellInferenceCore.CanTrainOutcome(independentLabel)) { ++_memory.MissingOutcomes; return; }
        if (!_memory.Classes.TryGetValue(issued.Kind, out var metrics)) _memory.Classes[issued.Kind] = metrics = new();
        ++metrics.Assessed;
        var hit = issued.Kind == independentLabel;
        if (hit) ++metrics.Hits;
        if (issued.Reliability < .75f || issued.Probability < .75f) ++metrics.Abstained;
        metrics.BrierSum += Math.Pow(Math.Clamp(issued.Probability, 0, 1) - (hit ? 1 : 0), 2);
        metrics.LeadSecondsSum += Math.Max(0, leadSeconds);
        if (train) _model.Train(features, independentLabel);
    }

    public static double[] Features(ForetellObservation trigger, IEnumerable<Vector2> participantPositions,
        IEnumerable<ForetellObservation>? precedingCues = null)
    {
        var positions = participantPositions.Take(48).ToArray();
        var source = new Vector2(trigger.X, trigger.Z);
        var target = new Vector2(trigger.TargetX, trigger.TargetZ);
        var x = new double[OnlineClassifier.FeatureCount];
        x[0] = Math.Clamp(trigger.Value1 / 10d, 0, 1);
        x[1] = trigger.Kind == ObservationKind.CastStart ? 1 : 0;
        x[2] = trigger.Kind == ObservationKind.Icon ? 1 : 0;
        x[3] = trigger.Kind is ObservationKind.VFX or ObservationKind.NativeVFXSpawn ? 1 : 0;
        x[4] = trigger.Kind == ObservationKind.TetherStart ? 1 : 0;
        x[5] = trigger.SourceKind is SourceKind.Environment or SourceKind.EventObject ? 1 : 0;
        x[6] = trigger.TargetID != 0 ? 1 : 0;
        x[7] = Math.Clamp(Vector2.Distance(source, target) / 60d, 0, 1);
        x[8] = positions.Length / 48d;
        x[9] = positions.Length == 0 ? 0 : positions.Count(p => Vector2.Distance(p, target) <= 5) / (double)positions.Length;
        x[10] = positions.Length == 0 ? 0 : Math.Clamp(positions.Average(p => Vector2.Distance(p, source)) / 60d, 0, 1);
        x[11] = trigger.Prior is { } prior ? (int)prior.Geometry / 6d : 0;
        x[12] = trigger.Prior is { } range ? Math.Clamp(range.P1 / 60d, 0, 1) : 0;
        x[13] = trigger.Prior is { } width ? Math.Clamp(width.P2 / 30d, 0, 1) : 0;
        x[14] = trigger.Prior?.TargetArea == true ? 1 : 0;
        x[15] = trigger.Kind is ObservationKind.MapEffect or ObservationKind.ObjectEffect ? 1 : 0;
        foreach (var cue in (precedingCues ?? []).Where(c => c.At <= trigger.At && (trigger.At - c.At).TotalSeconds <= 3).TakeLast(32))
        {
            if (!IsPrecursor(cue.Kind) || cue.SourceKind is SourceKind.Player or SourceKind.Pet) continue;
            // No actor/action/territory IDs or raw payload bytes: these features describe reusable cue families.
            Add($"kind:{cue.Kind}", 1);
            Add($"source:{cue.SourceKind}", 1);
            Add($"delay:{cue.Kind}", 1 / (1 + (trigger.At - cue.At).TotalSeconds));
            foreach (var pair in cue.Numeric.OrderBy(p => p.Key, StringComparer.Ordinal).Take(256))
                if (double.IsFinite(pair.Value) && IsTransferableField(pair.Key)) Add(pair.Key, Math.Tanh(pair.Value / 10));
        }
        return x;

        void Add(string key, double value)
        {
            uint hash = 2166136261;
            foreach (var c in key) hash = unchecked((hash ^ c) * 16777619);
            var index = OnlineClassifier.BaseFeatureCount + (int)(hash % OnlineClassifier.FabricFeatureCount);
            x[index] = Math.Clamp(x[index] + ((hash & 0x80000000) == 0 ? value : -value), -4, 4);
        }
    }

    public static bool IsPrecursor(ObservationKind kind) => kind is ObservationKind.CastStart or ObservationKind.Icon
        or ObservationKind.VFX or ObservationKind.NativeVFXSpawn or ObservationKind.TetherStart
        or ObservationKind.EventObjectAnimation or ObservationKind.ActionTimelineEvent or ObservationKind.ModelStateChanged
        or ObservationKind.MapEffect or ObservationKind.ObjectEffect;
    private static bool IsTransferableField(string field)
        => !field.Contains("ID", StringComparison.OrdinalIgnoreCase) && !field.Contains("sequence", StringComparison.OrdinalIgnoreCase)
            && (field.Contains("progress", StringComparison.OrdinalIgnoreCase) || field.Contains("speed", StringComparison.OrdinalIgnoreCase)
                || field.Contains("remaining", StringComparison.OrdinalIgnoreCase) || field.Contains("duration", StringComparison.OrdinalIgnoreCase));
}
