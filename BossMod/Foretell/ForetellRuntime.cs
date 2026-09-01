namespace BossMod.Foretell;

internal sealed class MechanicEpisode
{
    public long ID { get; init; }
    public ForetellObservation Trigger { get; init; } = new();
    public DateTime Activation { get; set; }
    public DateTime FinalizeAt { get; set; }
    public double LeadSeconds { get; set; }
    public Dictionary<ulong, Vector2> ParticipantPositions { get; } = [];
    public Dictionary<ulong, uint> ParticipantRoles { get; } = [];
    public Dictionary<ulong, string> ParticipantRoleNames { get; } = [];
    public HashSet<ulong> AffectedTargets { get; } = [];
    public HashSet<ulong> StatusTargets { get; } = [];
    public HashSet<ulong> TetherTargets { get; } = [];
    public HashSet<ulong> MovementTargets { get; } = [];
    public Dictionary<ulong, float> MovementDistances { get; } = [];
    public HashSet<ulong> DeathTargets { get; } = [];
    public Dictionary<ObservationKind, int> Evidence { get; } = [];
    public Dictionary<string, double> FeatureSums { get; } = [];
    public Dictionary<string, int> FeatureCounts { get; } = [];
    public double[] BinaryBuckets { get; } = new double[OnlineClassifier.FabricFeatureCount];
    public HashSet<string> BinaryKeys { get; } = [];
    public long BinaryBytes { get; private set; }
    public bool Finalized { get; set; }

    public void AccumulateFeatures(ForetellObservation observation)
    {
        foreach (var (key, value) in observation.Numeric)
        {
            if (FeatureSums.Count >= 4096 && !FeatureSums.ContainsKey(key)) continue;
            FeatureSums[key] = FeatureSums.GetValueOrDefault(key) + value;
            FeatureCounts[key] = FeatureCounts.GetValueOrDefault(key) + 1;
        }
        foreach (var (key, value) in observation.Text)
        {
            var token = $"@text:{key}={value}";
            if (FeatureSums.Count >= 4096 && !FeatureSums.ContainsKey(token)) continue;
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
}

internal sealed class ParticipantTrack
{
    public DateTime At { get; set; }
    public Vector2 Position { get; set; }
    public uint Role { get; set; }
    public string RoleName { get; set; } = "";
}

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
