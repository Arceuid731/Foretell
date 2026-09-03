using System.Text.Json.Serialization;
using System.Numerics;

namespace BossMod.Foretell;

public enum GeometryKind { Unknown, Circle, Donut, Cone, Rectangle, Cross }
public enum GuidanceKind { None, Avoid, Stack, Spread, Soak, LookAway, Knockback, Tether, Raidwide, Cleanse, Move }
public enum PredictionOriginKind { Source, Target }
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
    NativeVFXSpawn, NativeVFXDestroy, TopologySnapshot,
    ClientMetadata, GenericFeature
}

public enum SourceKind { Unknown, Player, Pet, Enemy, EventObject, Environment }

public enum DecisionAuditStage { Detected, Proposed, Classified, Verified, Expired }

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
    // Capability discovery is live diagnostic state, not learned encounter evidence. Persisting tens of thousands
    // of reflection paths made the minute autosave serialize a very large ledger on the framework thread.
    [JsonIgnore]
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
    public string TriggerDetail { get; set; } = "";
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
    public PredictionOriginKind OriginKind { get; set; }
    public int AnchorSamples { get; set; }
    public double MeanAnchorForward { get; set; }
    public double MeanAnchorSide { get; set; }
    public double AnchorForwardM2 { get; set; }
    public double AnchorSideM2 { get; set; }
    public int Forecasts { get; set; }
    public int ForecastHits { get; set; }
    public int ForecastMisses { get; set; }
    public double BrierScoreSum { get; set; }
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

    [JsonIgnore] public float GuidanceConfidence => ForetellInferenceCore.GuidanceConfidence(Confidence, ForecastHits, ForecastMisses);
    [JsonIgnore] public float ForecastAccuracy => Forecasts == 0 ? 0 : ForecastHits / (float)Math.Max(1, Forecasts);
    [JsonIgnore] public double AnchorStdDev => AnchorSamples > 1
        ? Math.Sqrt(Math.Max(0, AnchorForwardM2 + AnchorSideM2) / (AnchorSamples - 1))
        : 0;
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
    public int Forecasts { get; set; }
    public int Hits { get; set; }
    public int Misses { get; set; }
    [JsonIgnore] public double StdDev => Count > 1 ? Math.Sqrt(M2 / (Count - 1)) : 0;
    [JsonIgnore] public float Stability => Count < 2 ? 0 : Math.Clamp(1f - (float)(StdDev / Math.Max(.5, MeanDelay)), 0, 1);
    [JsonIgnore] public float ForecastReliability => ForetellInferenceCore.WilsonLowerBound(Hits, Hits + Misses);
}

// Occurrence-specific trigger context. Keeping the occurrence number prevents a repeating cast from collapsing
// several points in one phase into one meaningless average. Phase-clock and boss-HP evidence are learned in
// parallel; inference chooses the more stable explanation instead of assuming that every correlation is causal.
public sealed class SignalTriggerMemory
{
    public string Key { get; set; } = "";
    public string Signal { get; set; } = "";
    public int Phase { get; set; }
    public int Occurrence { get; set; }
    public uint ContextOID { get; set; }
    public uint BossOID { get; set; }
    public int Samples { get; set; }
    public int LastPull { get; set; } = -1;
    public double MeanPhaseSeconds { get; set; }
    public double PhaseSecondsM2 { get; set; }
    public int HealthSamples { get; set; }
    public double MeanBossHPRatio { get; set; }
    public double BossHPRatioM2 { get; set; }
    public int TimeForecasts { get; set; }
    public int TimeHits { get; set; }
    public int TimeMisses { get; set; }
    public int HealthForecasts { get; set; }
    public int HealthHits { get; set; }
    public int HealthMisses { get; set; }
    public DateTime LastSeen { get; set; }

    [JsonIgnore] public double PhaseSecondsStdDev => Samples > 1 ? Math.Sqrt(Math.Max(0, PhaseSecondsM2) / (Samples - 1)) : 0;
    [JsonIgnore] public double BossHPRatioStdDev => HealthSamples > 1 ? Math.Sqrt(Math.Max(0, BossHPRatioM2) / (HealthSamples - 1)) : 0;
    [JsonIgnore] public float TimeStability => ForetellInferenceCore.PhaseClockStability(Samples, MeanPhaseSeconds, PhaseSecondsStdDev);
    [JsonIgnore] public float HealthStability => ForetellInferenceCore.BossHealthStability(HealthSamples, BossHPRatioStdDev);
    [JsonIgnore] public float TimeForecastReliability => ForetellInferenceCore.WilsonLowerBound(TimeHits, TimeHits + TimeMisses);
    [JsonIgnore] public float HealthForecastReliability => ForetellInferenceCore.WilsonLowerBound(HealthHits, HealthHits + HealthMisses);
    [JsonIgnore] public bool PreferHealth => ForetellInferenceCore.PreferBossHealthTrigger(Samples, MeanPhaseSeconds, PhaseSecondsStdDev, HealthSamples, BossHPRatioStdDev);
}

public sealed class SignalExclusion
{
    public string Signal { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class SignalFilterExport
{
    public int Schema { get; set; } = 1;
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public Dictionary<uint, List<SignalExclusion>> Territories { get; set; } = [];
}

public sealed class CausalEdgeMemory
{
    public string Cause { get; set; } = "";
    public string Effect { get; set; } = "";
    public int Count { get; set; }
    public int ExactLinks { get; set; }
    public double MeanDelay { get; set; }
    public double M2 { get; set; }
    public DateTime LastSeen { get; set; }
    [JsonIgnore] public double StdDev => Count > 1 ? Math.Sqrt(Math.Max(0, M2) / (Count - 1)) : 0;
    [JsonIgnore] public float Confidence => ForetellInferenceCore.CausalConfidence(Count, ExactLinks, MeanDelay, StdDev);
}

public sealed class RawOpcodeMemory
{
    public uint OpcodeFamily { get; set; }
    public long Windows { get; set; }
    public long Packets { get; set; }
    public long PayloadBytes { get; set; }
    public double MeanLength { get; set; }
    public double LengthM2 { get; set; }
    public int MinLength { get; set; } = int.MaxValue;
    public int MaxLength { get; set; }
    public ulong LastSequenceHash { get; set; }
    public long StructuralChanges { get; set; }
    [JsonIgnore] public double LengthStdDev => Packets > 1 ? Math.Sqrt(Math.Max(0, LengthM2) / (Packets - 1)) : 0;
}

public sealed class SourceMemory
{
    public uint OID { get; set; }
    public uint NameID { get; set; }
    public string Name { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))] public SourceKind Kind { get; set; }
    public int Observations { get; set; }
    public int Casts { get; set; }
    public int Signals { get; set; }
    public int Deaths { get; set; }
    // Encounter context is inferred only from observed collision, combat and actor properties. It is kept
    // independently from mechanic identity so boss-arena sources and ordinary trash can be presented separately.
    public uint MaximumHP { get; set; }
    public float MaximumHitboxRadius { get; set; }
    public int ArenaContextObservations { get; set; }
    public int BossCandidateObservations { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
}

public sealed class PhaseMemory
{
    public int Phase { get; set; }
    public int Seen { get; set; }
    public Dictionary<string, int> Signals { get; set; } = [];
}

public sealed class CompositeMechanicMemory
{
    public string Key { get; set; } = "";
    public int Phase { get; set; }
    public List<string> Signals { get; set; } = [];
    public int Count { get; set; }
    public double MeanSkewSeconds { get; set; }
    public double M2 { get; set; }
    public int Forecasts { get; set; }
    public int Hits { get; set; }
    public int Misses { get; set; }
    [JsonIgnore] public double StdDev => Count > 1 ? Math.Sqrt(M2 / (Count - 1)) : 0;
    [JsonIgnore] public float Stability => Count < 2 ? 0 : Math.Clamp(1f - (float)(StdDev / Math.Max(.15, MeanSkewSeconds + .15)), 0, 1);
    [JsonIgnore] public float ForecastReliability => ForetellInferenceCore.WilsonLowerBound(Hits, Hits + Misses);
}

public sealed class PhaseBoundaryMemory
{
    public string Signature { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))] public ObservationKind EvidenceKind { get; set; }
    public int Seen { get; set; }
    public int PullsSeen { get; set; }
    public int LastPull { get; set; } = -1;
    public bool Accepted { get; set; }
    public DateTime LastSeen { get; set; }
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

// Compact, bounded audit trail for the semantic path that cannot be reconstructed from raw transport alone.
// This records decisions, not every observation: exact incoming bytes remain in the compressed raw journal.
public sealed class DecisionAuditEntry
{
    public DateTime At { get; set; }
    public DateTime Activation { get; set; }
    public string SessionID { get; set; } = "";
    public uint TerritoryID { get; set; }
    public long PredictionID { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public DecisionAuditStage Stage { get; set; }
    public string SignalKey { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))] public ObservationKind TriggerKind { get; set; }
    public uint TriggerID { get; set; }
    public string TriggerDetail { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))] public SourceKind SourceKind { get; set; }
    public uint SourceOID { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public MechanicKind Mechanic { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public GeometryKind Geometry { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public GuidanceKind Guidance { get; set; }
    public float P1 { get; set; }
    public float P2 { get; set; }
    public float OriginX { get; set; }
    public float OriginZ { get; set; }
    public float TargetX { get; set; }
    public float TargetZ { get; set; }
    public float Rotation { get; set; }
    public float Confidence { get; set; }
    public bool Anticipated { get; set; }
    public bool DisplayEligible { get; set; }
    public bool? Verified { get; set; }
    public string Label { get; set; } = "";
    public string Evidence { get; set; } = "";
}

public sealed class EncounterMemory
{
    public uint TerritoryID { get; set; }
    public uint ContentFinderConditionID { get; set; }
    public string TerritoryName { get; set; } = "";
    public string ContentName { get; set; } = "";
    public string ContentCategory { get; set; } = "";
    public int Sessions { get; set; }
    public int Pulls { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public Dictionary<ObservationKind, long> ObservationCounts { get; set; } = [];
    public Dictionary<uint, SourceMemory> Sources { get; set; } = [];
    public Dictionary<string, ContextualMechanic> Mechanics { get; set; } = [];
    public Dictionary<string, SignalTimelineEdge> Timeline { get; set; } = [];
    public Dictionary<string, SignalTriggerMemory> TriggerContexts { get; set; } = [];
    public Dictionary<int, PhaseMemory> Phases { get; set; } = [];
    public Dictionary<string, PhaseBoundaryMemory> PhaseBoundaries { get; set; } = [];
    public Dictionary<string, CompositeMechanicMemory> Composites { get; set; } = [];
    public Dictionary<string, CausalEdgeMemory> CausalEdges { get; set; } = [];
    public Dictionary<uint, RawOpcodeMemory> RawOpcodes { get; set; } = [];
    public Dictionary<string, ArenaTopologyMemory> Topologies { get; set; } = [];
    public Dictionary<string, ArenaBoundaryMemory> ArenaBoundaries { get; set; } = [];
    public Dictionary<string, SignalExclusion> ExcludedSignals { get; set; } = [];
}

public sealed class TopologyPoint
{
    public float X { get; set; }
    public float Z { get; set; }
}

public sealed class TopologyContourMemory
{
    public bool Hole { get; set; }
    public List<TopologyPoint> Points { get; set; } = [];
}

public sealed class ArenaTopologyMemory
{
    public string Fingerprint { get; set; } = "";
    public float OriginX { get; set; }
    public float OriginZ { get; set; }
    public float ReferenceY { get; set; }
    public float Resolution { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] Cells { get; set; } = [];
    public short[] HeightCentimeters { get; set; } = [];
    public List<TopologyContourMemory> Contours { get; set; } = [];
    public int PassableCells { get; set; }
    public int BlockedCells { get; set; }
    public int UnknownCells { get; set; }
    public int Components { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public int Observations { get; set; }
}

public sealed class ArenaBoundaryMemory
{
    public string Fingerprint { get; set; } = "";
    public float OriginX { get; set; }
    public float OriginZ { get; set; }
    public float ReferenceY { get; set; }
    public List<TopologyPoint> Points { get; set; } = [];
    public int Rays { get; set; }
    public int Hits { get; set; }
    public float Area { get; set; }
    public float Compactness { get; set; }
    public float AspectRatio { get; set; }
    public bool ArenaLike { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public int Observations { get; set; }
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
    public int Schema { get; set; } = 19;
    public Dictionary<uint, LearnedMechanic> Mechanics { get; set; } = [];
    public Dictionary<string, TimelineEdge> Timeline { get; set; } = [];
    public Dictionary<uint, EncounterMemory> Encounters { get; set; } = [];
    public List<SessionSummary> Sessions { get; set; } = [];
    public List<DecisionAuditEntry> DecisionAudit { get; set; } = [];
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
    public long RawRecords { get; set; }
    public int RawWindows { get; set; }
    public int RawErrors { get; set; }
    public Dictionary<ObservationKind, int> Counts { get; set; } = [];
    public DateTime First { get; set; }
    public DateTime Last { get; set; }
    public string Status { get; set; } = "Not run";
}

public readonly record struct ActivePrediction(
    ulong CasterID, uint ActionID, GeometryKind Geometry, MechanicKind Kind,
    Vector2 Origin, Vector2 Target, float Rotation, float P1, float P2,
    DateTime Activation, float Confidence, string Evidence,
    string SignalKey = "", ulong TargetID = 0, GuidanceKind Guidance = GuidanceKind.None,
    bool Anticipated = false, string Label = "");

internal readonly record struct ActionGeometryPrior(
    uint ActionID, GeometryKind Geometry, float P1, float P2, float Confidence,
    int CastType, int EffectRange, int XAxisModifier, bool TargetArea,
    uint OmenID, string Omen, string Evidence);

internal readonly record struct FitResult(GeometryKind Geometry, Vector2 Origin, float Rotation, float P1, float P2, float Score);
