namespace BossMod.Foretell;

internal sealed class MechanicEpisode
{
    public long ID { get; init; }
    public ForetellObservation Trigger { get; init; } = new();
    public DateTime Activation { get; set; }
    public DateTime FinalizeAt { get; set; }
    public double LeadSeconds { get; set; }
    public Dictionary<ulong, Vector2> ParticipantPositions { get; } = [];
    public Dictionary<ulong, float> ParticipantRotations { get; } = [];
    public Dictionary<ulong, Vector2> ResolutionPositions { get; } = [];
    public Dictionary<ulong, float> ResolutionRotations { get; } = [];
    public Dictionary<ulong, uint> ParticipantRoles { get; } = [];
    public Dictionary<ulong, string> ParticipantRoleNames { get; } = [];
    public HashSet<ulong> AffectedTargets { get; } = [];
    public HashSet<ulong> StatusTargets { get; } = [];
    public HashSet<ulong> TetherTargets { get; } = [];
    public HashSet<ulong> MovementTargets { get; } = [];
    public Dictionary<ulong, float> MovementDistances { get; } = [];
    public Dictionary<ulong, double> DamageByTarget { get; } = [];
    public HashSet<ulong> DeathTargets { get; } = [];
    public Dictionary<ObservationKind, int> Evidence { get; } = [];
    public Dictionary<string, double> FeatureSums { get; } = [];
    public Dictionary<string, int> FeatureCounts { get; } = [];
    public double[] BinaryBuckets { get; } = new double[OnlineClassifier.FabricFeatureCount];
    public HashSet<string> BinaryKeys { get; } = [];
    public long BinaryBytes { get; private set; }
    public bool ResolutionObserved { get; set; }
    public GeometryKind ForecastGeometry { get; set; }
    public MechanicKind ForecastKind { get; set; }
    public float ForecastP1 { get; set; }
    public float ForecastP2 { get; set; }
    public float ForecastConfidence { get; set; }
    public bool ForecastIssued { get; set; }
    public bool ForecastAnticipated { get; set; }
    public bool Finalized { get; set; }

    public void AccumulateFeatures(ForetellObservation observation)
    {
        foreach (var (key, value) in observation.Numeric)
        {
            if (!double.IsFinite(value)) continue;
            FeatureSums[key] = FeatureSums.GetValueOrDefault(key) + value;
            FeatureCounts[key] = FeatureCounts.GetValueOrDefault(key) + 1;
        }
        foreach (var (key, value) in observation.Text)
        {
            var token = $"@text:{key}={value}";
            FeatureSums[token] = FeatureSums.GetValueOrDefault(token) + 1;
            FeatureCounts[token] = FeatureCounts.GetValueOrDefault(token) + 1;
        }
        foreach (var (key, bytes) in observation.Binary)
        {
            BinaryKeys.Add(key);
            var lengthKey = $"binary.{key}.length";
            FeatureSums[lengthKey] = FeatureSums.GetValueOrDefault(lengthKey) + bytes.Length;
            FeatureCounts[lengthKey] = FeatureCounts.GetValueOrDefault(lengthKey) + 1;
            BinaryBytes += bytes.LongLength;

            // Signed feature hashing compresses an arbitrary-size opaque packet into the same fixed fabric space.
            // Every byte participates; the raw bytes are still retained losslessly in Foretell replay.
            var keyHash = StableBinaryHash(key);
            for (var i = 0; i < bytes.Length; ++i)
            {
                unchecked
                {
                    var h = keyHash;
                    h ^= (uint)i * 0x9E3779B9u;
                    h *= 16777619u;
                    h ^= bytes[i];
                    h *= 16777619u;
                    var slot = (int)(h % OnlineClassifier.FabricFeatureCount);
                    var sign = (h & 0x80000000u) == 0 ? 1d : -1d;
                    var centered = bytes[i] / 127.5d - 1d;
                    BinaryBuckets[slot] += sign * centered;
                }
            }
        }
    }

    private static uint StableBinaryHash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in value)
            {
                hash ^= c;
                hash *= 16777619u;
            }
            return hash;
        }
    }

    public string SignalKey => $"{Trigger.ActorOID:X}:{Trigger.Kind}:{Trigger.PrimaryID:X}";

    public void AddEvidence(ObservationKind kind)
        => Evidence[kind] = Evidence.GetValueOrDefault(kind) + 1;

    public Vector2 PositionFor(ulong id)
        => ResolutionPositions.GetValueOrDefault(id, ParticipantPositions.GetValueOrDefault(id));

    public float RotationFor(ulong id)
        => ResolutionRotations.GetValueOrDefault(id, ParticipantRotations.GetValueOrDefault(id));
}

internal sealed class ParticipantTrack
{
    public DateTime At { get; set; }
    public Vector2 Position { get; set; }
    public uint Role { get; set; }
    public string RoleName { get; set; } = "";
    public float Rotation { get; set; }
    public Queue<ParticipantPositionPoint> History { get; } = new();

    public void Add(DateTime at, Vector2 position, float rotation, uint role, string roleName)
    {
        At = at;
        Position = position;
        Rotation = rotation;
        Role = role;
        RoleName = roleName;
        History.Enqueue(new(at, position, rotation));
        var cutoff = at.AddSeconds(-15);
        while (History.TryPeek(out var point) && point.At < cutoff)
            History.Dequeue();
    }

    public ParticipantPositionPoint Nearest(DateTime at)
    {
        var best = new ParticipantPositionPoint(At, Position, Rotation);
        var distance = Math.Abs((best.At - at).Ticks);
        foreach (var point in History)
        {
            var candidate = Math.Abs((point.At - at).Ticks);
            if (candidate < distance) { best = point; distance = candidate; }
        }
        return best;
    }
}

internal readonly record struct ParticipantPositionPoint(DateTime At, Vector2 Position, float Rotation);

internal sealed class PendingTimelineForecast
{
    public long ID { get; init; }
    public uint TerritoryID { get; init; }
    public int Phase { get; init; }
    public string EdgeKey { get; set; } = "";
    public string CompositeKey { get; set; } = "";
    public string TriggerContextKey { get; set; } = "";
    public PredictiveTriggerBasis TriggerBasis { get; set; }
    public string ExpectedSignal { get; init; } = "";
    public string MechanicKey { get; init; } = "";
    public DateTime Due { get; init; }
    public DateTime Expires { get; init; }
}

internal enum PredictiveTriggerBasis
{
    None,
    PhaseClock,
    BossHealth
}

internal sealed class BossHealthTrack
{
    public DateTime At { get; private set; }
    public double Ratio { get; private set; }
    public double LossPerSecond { get; private set; }

    public void Update(DateTime at, double ratio)
    {
        ratio = double.IsFinite(ratio) ? Math.Clamp(ratio, 0, 1) : 0;
        if (At != default && at > At)
        {
            var seconds = (at - At).TotalSeconds;
            var loss = Ratio - ratio;
            if (seconds is >= .05 and <= 5 && loss is >= 0 and <= .25)
            {
                var instantaneous = loss / seconds;
                if (instantaneous > .00001)
                    LossPerSecond = LossPerSecond <= 0 ? instantaneous : LossPerSecond * .72 + instantaneous * .28;
            }
        }
        At = at;
        Ratio = ratio;
    }
}

internal readonly record struct BossHealthSnapshot(Actor Boss, double Ratio, double LossPerSecond);

internal sealed class LiveSessionStats
{
    public string ID { get; } = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    public DateTime Started { get; } = DateTime.UtcNow;
    public uint TerritoryID { get; set; }
    public int Pulls { get; set; }
    public int Phase { get; set; }
    public int Observations { get; set; }
    public int MechanicsFinalized { get; set; }
    public int NewMechanics { get; set; }
    public int AmbiguousMechanics { get; set; }
    public Dictionary<ObservationKind, int> Counts { get; } = [];
    public Queue<ForetellObservation> Recent { get; } = new();

    public void Observe(ForetellObservation observation)
    {
        ++Observations;
        Counts[observation.Kind] = Counts.GetValueOrDefault(observation.Kind) + 1;
        Recent.Enqueue(observation);
        while (Recent.Count > 100)
            Recent.Dequeue();
    }
}
