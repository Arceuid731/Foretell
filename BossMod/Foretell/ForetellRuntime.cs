namespace BossMod.Foretell;

internal sealed class MechanicEpisode
{
    public long ID { get; init; }
    public ForetellObservation Trigger { get; init; } = new();
    public DateTime Activation { get; set; }
    public DateTime FinalizeAt { get; set; }
    public double LeadSeconds { get; set; }
    public Dictionary<ulong, Vector2> ParticipantPositions { get; } = [];
    public HashSet<ulong> AffectedTargets { get; } = [];
    public HashSet<ulong> StatusTargets { get; } = [];
    public HashSet<ulong> TetherTargets { get; } = [];
    public HashSet<ulong> MovementTargets { get; } = [];
    public HashSet<ulong> DeathTargets { get; } = [];
    public Dictionary<ObservationKind, int> Evidence { get; } = [];
    public bool Finalized { get; set; }

    public string SignalKey => $"{Trigger.ActorOID:X}:{Trigger.Kind}:{Trigger.PrimaryID:X}";

    public void AddEvidence(ObservationKind kind)
        => Evidence[kind] = Evidence.GetValueOrDefault(kind) + 1;
}

internal sealed class ParticipantTrack
{
    public DateTime At { get; set; }
    public Vector2 Position { get; set; }
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
