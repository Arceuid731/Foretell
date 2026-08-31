using System.Text.Json.Serialization;

namespace BossMod.Foretell;

public enum GeometryKind { Unknown, Circle, Donut, Cone, Rectangle }
public enum MechanicKind { Unknown, GroundAOE, Raidwide, Tankbuster, Stack, Spread, Tower, Knockback, Gaze, Tether, Proximity }

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

public sealed class TimelineEdge
{
    public uint From { get; set; }
    public uint To { get; set; }
    public int Count { get; set; }
    public double MeanDelay { get; set; }
    public double M2 { get; set; }
    [JsonIgnore] public double StdDev => Count > 1 ? Math.Sqrt(M2 / (Count - 1)) : 0;
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
    public int Schema { get; set; } = 1;
    public Dictionary<uint, LearnedMechanic> Mechanics { get; set; } = [];
    public Dictionary<string, TimelineEdge> Timeline { get; set; } = [];
    public MLState ML { get; set; } = new();
}

public readonly record struct ActivePrediction(
    ulong CasterID, uint ActionID, GeometryKind Geometry, MechanicKind Kind,
    Vector2 Origin, Vector2 Target, float Rotation, float P1, float P2,
    DateTime Activation, float Confidence, string Evidence);

internal readonly record struct CastSnapshot(
    ulong CasterID, uint ActionID, Vector2 Origin, Vector2 Target, float Rotation,
    DateTime Started, DateTime Activation, double CastSeconds);

internal readonly record struct Sample(Vector2 Position, bool Hit);
internal readonly record struct FitResult(GeometryKind Geometry, Vector2 Origin, float Rotation, float P1, float P2, float Score);
