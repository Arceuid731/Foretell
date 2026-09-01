using System.Text.Json.Serialization;

namespace BossMod.Foretell;

public enum GeometryKind { Unknown, Circle, Donut, Cone, Rectangle, Cross }
public enum MechanicKind
{
    Unknown, GroundAOE, Raidwide, Tankbuster, Stack, Spread, Tower, Knockback, Gaze, Tether, Proximity,
    Debuff, TargetedAOE, LineStack, ForcedMovement, Environment, Transition
}

public enum ObservationKind
{
    Unknown,
    ActorAdded, ActorRemoved, TargetableChanged, DeathChanged, RenderFlagsChanged, EventStateChanged, ModelStateChanged,
    CastStart, CastFinish, ActionResolved, AffectedTarget, EffectResult,
    Icon, VFX, TetherStart, TetherEnd, StatusGain, StatusLose,
    EventObjectState, EventObjectAnimation, ActionTimelineEvent, ActionTimelineSync, NpcYell,
    MapEffect, LegacyMapEffect, DirectorUpdate, SystemLog, ObjectEffect,
    DutyStarted, DutyWiped, DutyRecommenced, DutyCompleted, FlyText, DalamudLogMessage, NormalToast, QuestToast, ErrorToast,
    WorldOperation, ServerIPC, ClientIPC, ActorControlRaw,
    PositionSample, Displacement, ActorSnapshot, EnvironmentSnapshot, CameraSnapshot,
    NativeVFXSpawn, NativeVFXDestroy,
    ClientMetadata, GenericFeature
}

public enum SourceKind { Unknown, Player, Pet, Enemy, EventObject, Environment }

public sealed class DataCapability
{
    public string Key { get; set; } = "";
    public string Category { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string Member { get; set; } = "";
    public long Seen { get; set; }
    public bool Ingested { get; set; }
    public bool Used { get; set; }
    public long UsedCount { get; set; }
    public bool Excluded { get; set; }
    public string Reason { get; set; } = "";
    [JsonIgnore] public bool Unaccounted => !Ingested && !Excluded;
}

public sealed class DataCoverage
{
    public Dictionary<string, DataCapability> Items { get; set; } = [];
    [JsonIgnore] public int Discovered => Items.Count;
    [JsonIgnore] public int Ingested => Items.Values.Count(v => v.Ingested);
    [JsonIgnore] public int Used => Items.Values.Count(v => v.Used);
    [JsonIgnore] public int Excluded => Items.Values.Count(v => v.Excluded);
    [JsonIgnore] public int Unaccounted => Items.Values.Count(v => v.Unaccounted);
}

public sealed class LearnedMechanic
{
    public uint ActionID { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public GeometryKind Geometry { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public MechanicKind Kind { get; set; }
    public float P1 { get; set; }
    public float P2 { get; set; }
    public float Score { get; set; }
    public int Observations { get; set; }
    public int Confirmations { get; set; }
    public double MeanCastSeconds { get; set; }
    public DateTime LastSeen { get; set; }
    [JsonIgnore] public float Confidence => Math.Clamp((Score * .72f) + (1f - MathF.Exp(-Observations / 4f)) * .28f, 0, 1);
}

public sealed class MechanicSamplePoint
{
    public float Side { get; set; }
    public float Forward { get; set; }
    public float TargetDX { get; set; }
    public float TargetDZ { get; set; }
    public bool Affected { get; set; }
}

public sealed class ContextualMechanic
{
    public string Key { get; set; } = "";
    public uint TerritoryID { get; set; }
    public uint SourceOID { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public SourceKind SourceKind { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public ObservationKind TriggerKind { get; set; }
    public uint TriggerID { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public GeometryKind Geometry { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public MechanicKind Kind { get; set; }
    public float P1 { get; set; }
    public float P2 { get; set; }
    public float Score { get; set; }
    public int Observations { get; set; }
    public int Confirmations { get; set; }
    public int AffectedSamples { get; set; }
    public int StatusSamples { get; set; }
    public int MovementSamples { get; set; }
    public int DeathSamples { get; set; }
    public int AmbiguousSamples { get; set; }
    public double MeanLeadSeconds { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public Dictionary<ObservationKind, int> Evidence { get; set; } = [];
    public List<MechanicSamplePoint> Samples { get; set; } = [];

    // Static client-data prior. This is deliberately kept separate from empirical score so outcome evidence can
    // confirm, refine or contradict it instead of silently treating the game sheet as ground truth.
    [JsonConverter(typeof(JsonStringEnumConverter))] public GeometryKind PriorGeometry { get; set; }
    public float PriorP1 { get; set; }
    public float PriorP2 { get; set; }
    public float PriorConfidence { get; set; }
    public int PriorCastType { get; set; }
    public int PriorEffectRange { get; set; }
    public int PriorXAxisModifier { get; set; }
    public bool PriorTargetArea { get; set; }
    public uint PriorOmenID { get; set; }
    public string PriorOmen { get; set; } = "";
    public string PriorEvidence { get; set; } = "";

    [JsonIgnore] public float EmpiricalConfidence
    {
        get
        {
            var repetition = 1f - MathF.Exp(-Observations / 4f);
            var agreement = Observations == 0 ? 0 : Confirmations / (float)Observations;
            var ambiguityPenalty = 1f / (1f + AmbiguousSamples * .12f);
            return Math.Clamp((Score * .48f + repetition * .30f + agreement * .22f) * ambiguityPenalty, 0, 1);
        }
    }

    [JsonIgnore] public float Confidence
    {
        get
        {
            var empirical = EmpiricalConfidence;
            if (PriorConfidence <= 0) return empirical;
            if (Observations == 0) return Math.Min(PriorConfidence, .98f);

            var effectivePrior = PriorConfidence;
            if (PriorGeometry != GeometryKind.Unknown && Geometry != GeometryKind.Unknown)
            {
                if (PriorGeometry != Geometry)
                {
                    // Observed geometry-family disagreement means this Action sheet row is not describing the
                    // correlated encounter effect accurately enough to dominate the learner.
                    effectivePrior *= .20f;
                }
                else if (PriorP1 > 0 && P1 > 0)
                {
                    var drift1 = MathF.Abs(P1 - PriorP1) / MathF.Max(1, PriorP1);
                    var drift2 = PriorP2 > 0 && P2 > 0 ? MathF.Abs(P2 - PriorP2) / MathF.Max(1, PriorP2) : 0;
                    var drift = MathF.Max(drift1, drift2);
                    if (drift > .15f)
                        effectivePrior *= Math.Clamp(1f - drift, .25f, .85f);
                }
            }

            // Client metadata can make ordinary telegraphs useful on the first cast. It can accelerate confidence,
            // but the 99% safe-guidance gate still requires corroborating empirical evidence.
            effectivePrior = Math.Min(effectivePrior, .98f);
            var fused = 1f - (1f - effectivePrior) * (1f - empirical);
            if (AmbiguousSamples > 0)
                fused *= 1f / (1f + AmbiguousSamples * .08f);
            return Math.Clamp(fused, 0, .999f);
        }
    }
}

public sealed class TimelineEdge
{
    public uint From { get; set; }
    public uint To { get; set; }
    public int Count { get; set; }
    public double MeanDelay { get; set; }
    public double M2 { get; set; }
    [JsonIgnore] public double StdDev => Count > 1 ? Math.Sqrt(M2 / (Count - 1)) : 0;
}

public sealed class SignalTimelineEdge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int Phase { get; set; }
    public int Count { get; set; }
    public double MeanDelay { get; set; }
    public double M2 { get; set; }
    [JsonIgnore] public double StdDev => Count > 1 ? Math.Sqrt(M2 / (Count - 1)) : 0;
    [JsonIgnore] public float Stability => Count < 2 ? 0 : Math.Clamp(1f - (float)(StdDev / Math.Max(.5, MeanDelay)), 0, 1);
}

public sealed class SourceMemory
{
    public uint OID { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public SourceKind Kind { get; set; }
    public int Observations { get; set; }
    public int Casts { get; set; }
    public int Signals { get; set; }
    public int Deaths { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
}

public sealed class PhaseMemory
{
    public int Phase { get; set; }
    public int Seen { get; set; }
    public Dictionary<string, int> Signals { get; set; } = [];
}

public sealed class SessionSummary
{
    public string SessionID { get; set; } = "";
    public uint TerritoryID { get; set; }
    public DateTime Started { get; set; }
    public DateTime Ended { get; set; }
    public int Pulls { get; set; }
    public int Observations { get; set; }
    public int MechanicsFinalized { get; set; }
    public int NewMechanics { get; set; }
    public int AmbiguousMechanics { get; set; }
    public string ReplayFile { get; set; } = "";
}

public sealed class EncounterMemory
{
    public uint TerritoryID { get; set; }
    public int Sessions { get; set; }
    public int Pulls { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public Dictionary<ObservationKind, long> ObservationCounts { get; set; } = [];
    public Dictionary<uint, SourceMemory> Sources { get; set; } = [];
    public Dictionary<string, ContextualMechanic> Mechanics { get; set; } = [];
    public Dictionary<string, SignalTimelineEdge> Timeline { get; set; } = [];
    public Dictionary<int, PhaseMemory> Phases { get; set; } = [];
}

public sealed class MLState
{
    public int FeatureCount { get; set; } = OnlineClassifier.FeatureCount;
    public int ClassCount { get; set; } = OnlineClassifier.ClassCount;
    public double[][] Weights { get; set; } = OnlineClassifier.NewWeights();
    public long Updates { get; set; }
}

public sealed class ForetellStore
{
    public int Schema { get; set; } = 6;
    public Dictionary<uint, LearnedMechanic> Mechanics { get; set; } = [];
    public Dictionary<string, TimelineEdge> Timeline { get; set; } = [];
    public Dictionary<uint, EncounterMemory> Encounters { get; set; } = [];
    public List<SessionSummary> Sessions { get; set; } = [];
    public MLState ML { get; set; } = new();
    public DataCoverage Coverage { get; set; } = new();
}

public sealed class ForetellObservation
{
    public long Sequence { get; set; }
    public DateTime At { get; set; }
    public uint TerritoryID { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public ObservationKind Kind { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public SourceKind SourceKind { get; set; }
    public ulong ActorID { get; set; }
    public uint ActorOID { get; set; }
    public ulong TargetID { get; set; }
    public uint PrimaryID { get; set; }
    public uint SecondaryID { get; set; }
    public float X { get; set; }
    public float Z { get; set; }
    public float TargetX { get; set; }
    public float TargetZ { get; set; }
    public float Rotation { get; set; }
    public float Value1 { get; set; }
    public float Value2 { get; set; }
    public bool Flag { get; set; }
    public string Detail { get; set; } = "";
    public Dictionary<string, double> Numeric { get; set; } = [];
    public Dictionary<string, string> Text { get; set; } = [];
    // Lossless opaque payloads (network packets and any future binary client structures). JSON serializes byte[] as base64.
    public Dictionary<string, byte[]> Binary { get; set; } = [];
}

public sealed class ReplayReport
{
    public string File { get; set; } = "";
    public int Lines { get; set; }
    public int Parsed { get; set; }
    public int Rejected { get; set; }
    public int Territories { get; set; }
    public int RediscoveredMechanics { get; set; }
    public int AmbiguousMechanics { get; set; }
    public Dictionary<ObservationKind, int> Counts { get; set; } = [];
    public DateTime First { get; set; }
    public DateTime Last { get; set; }
    public string Status { get; set; } = "Not run";
}

public readonly record struct ActivePrediction(
    ulong CasterID, uint ActionID, GeometryKind Geometry, MechanicKind Kind,
    Vector2 Origin, Vector2 Target, float Rotation, float P1, float P2,
    DateTime Activation, float Confidence, string Evidence);

internal readonly record struct ActionGeometryPrior(
    uint ActionID, GeometryKind Geometry, float P1, float P2, float Confidence,
    int CastType, int EffectRange, int XAxisModifier, bool TargetArea,
    uint OmenID, string Omen, string Evidence);

internal readonly record struct CastSnapshot(
    ulong CasterID, uint ActionID, Vector2 Origin, Vector2 Target, float Rotation,
    DateTime Started, DateTime Activation, double CastSeconds);

internal readonly record struct Sample(Vector2 Position, bool Hit);
internal readonly record struct FitResult(GeometryKind Geometry, Vector2 Origin, float Rotation, float P1, float P2, float Score);
