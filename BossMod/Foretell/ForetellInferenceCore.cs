using System.Numerics;

namespace BossMod.Foretell;

// Pure deterministic decision helpers shared by the live engine and the standalone regression harness.
// Confidence here means verified forecast reliability, not merely accumulated evidence.
public static class ForetellInferenceCore
{
    public const int OutOfCombatHazardPhase = -1;

    // Raw telemetry remains complete, but only sources that can plausibly own an encounter mechanic may create
    // learned episodes. Player/pet actions are observations and positional context, never mechanic sources.
    public static bool CanStartMechanicEpisode(ObservationKind kind, SourceKind sourceKind, ulong actorID, uint actorOID)
    {
        if (sourceKind is SourceKind.Player or SourceKind.Pet)
            return false;

        var actorBound = actorID != 0 && actorOID != 0;
        if (sourceKind == SourceKind.Enemy)
        {
            if (!actorBound)
                return false;
            return kind is ObservationKind.CastStart or ObservationKind.Icon or ObservationKind.VFX or ObservationKind.TetherStart
                or ObservationKind.StatusGain or ObservationKind.NpcYell
                or ObservationKind.ObjectEffect or ObservationKind.NativeVFXSpawn;
        }

        // Event-object state, animation and timeline changes are valuable causal/timeline signals, but a door,
        // key or shortcut changing state is not itself a mechanic. Only explicit hazard-like surfaces may open an
        // event-object episode; later outcomes still decide whether that episode becomes learned guidance.
        if (sourceKind == SourceKind.EventObject)
        {
            if (!actorBound)
                return false;
            return kind is ObservationKind.Icon or ObservationKind.VFX or ObservationKind.TetherStart
                or ObservationKind.NpcYell or ObservationKind.ObjectEffect or ObservationKind.NativeVFXSpawn;
        }

        // Actorless signals are admitted only for explicit encounter/environment channels. In particular, an
        // actorless CastStart must never turn a player action (player OIDs are zero) into an environment mechanic.
        return sourceKind == SourceKind.Environment && kind is (ObservationKind.MapEffect or ObservationKind.LegacyMapEffect
            or ObservationKind.ObjectEffect);
    }

    public static bool IsMechanicOutcomeEvidence(ObservationKind kind, SourceKind sourceKind)
    {
        // Party motion/death is outcome evidence for knockbacks and lethal zones. Party casts, effects and buffs
        // remain excluded so normal rotations cannot teach or confirm encounter mechanics.
        if (sourceKind is SourceKind.Player or SourceKind.Pet)
            return kind is ObservationKind.Displacement or ObservationKind.DeathChanged;
        return kind is ObservationKind.ActionResolved or ObservationKind.AffectedTarget or ObservationKind.EffectResult
            or ObservationKind.StatusGain or ObservationKind.StatusLose or ObservationKind.TetherStart or ObservationKind.TetherEnd
            or ObservationKind.VFX or ObservationKind.NativeVFXSpawn or ObservationKind.Displacement or ObservationKind.DeathChanged
            or ObservationKind.TargetableChanged or ObservationKind.ModelStateChanged or ObservationKind.ObjectEffect;
    }

    public static int TimelinePhase(bool inPull, int combatPhase)
        => inPull ? Math.Max(0, combatPhase) : OutOfCombatHazardPhase;

    public static bool OpensOutOfCombatHazardContext(ObservationKind kind, SourceKind sourceKind, ulong actorID, ulong targetID)
    {
        if (sourceKind is SourceKind.Player or SourceKind.Pet)
            return false;
        if (kind == ObservationKind.NativeVFXSpawn && actorID == 0 && targetID == 0)
            return false;
        if (kind == ObservationKind.StatusGain && actorID == 0 && targetID == 0)
            return false;
        return sourceKind switch
        {
            SourceKind.Enemy => kind is ObservationKind.CastStart or ObservationKind.Icon or ObservationKind.VFX
                or ObservationKind.TetherStart or ObservationKind.StatusGain or ObservationKind.NpcYell
                or ObservationKind.ObjectEffect or ObservationKind.NativeVFXSpawn,
            SourceKind.EventObject => kind is ObservationKind.Icon or ObservationKind.VFX or ObservationKind.TetherStart
                or ObservationKind.NpcYell or ObservationKind.ObjectEffect or ObservationKind.NativeVFXSpawn,
            SourceKind.Environment => kind is ObservationKind.MapEffect or ObservationKind.LegacyMapEffect or ObservationKind.ObjectEffect,
            _ => false
        };
    }

    // Converts an X/Z world offset to a camera-up radar offset. Camera azimuth 0 looks north (-Z), while screen
    // Y points down; this deliberately matches MiniArena's rotating-radar convention.
    public static Vector2 CameraRelativeRadarOffset(Vector2 worldOffset, float cameraAzimuth)
    {
        var (sin, cos) = MathF.SinCos(float.IsFinite(cameraAzimuth) ? cameraAzimuth : 0);
        return new(worldOffset.X * cos - worldOffset.Y * sin, worldOffset.Y * cos + worldOffset.X * sin);
    }

    public static float WilsonLowerBound(int successes, int attempts, double z = 1.96)
    {
        attempts = Math.Max(0, attempts);
        successes = Math.Clamp(successes, 0, attempts);
        if (attempts == 0) return 0;
        var p = successes / (double)attempts;
        var z2 = z * z;
        var denominator = 1 + z2 / attempts;
        var center = p + z2 / (2 * attempts);
        var margin = z * Math.Sqrt((p * (1 - p) + z2 / (4 * attempts)) / attempts);
        return (float)Math.Clamp((center - margin) / denominator, 0, 1);
    }

    public static float GuidanceConfidence(float evidenceConfidence, int hits, int misses)
    {
        evidenceConfidence = float.IsFinite(evidenceConfidence) ? Math.Clamp(evidenceConfidence, 0, 1) : 0;
        hits = Math.Max(0, hits);
        misses = Math.Max(0, misses);
        var attempts = hits + misses;
        // Unvalidated evidence remains visible as a hypothesis but cannot cross the warning/safe gates.
        if (attempts < 3) return Math.Min(evidenceConfidence, .94f);
        var verified = WilsonLowerBound(hits, attempts);
        return Math.Min(evidenceConfidence, verified);
    }

    public static float CausalConfidence(int count, int exactLinks, double meanDelay, double stdDev)
    {
        if (count <= 0 || !double.IsFinite(meanDelay) || !double.IsFinite(stdDev)) return 0;
        exactLinks = Math.Clamp(exactLinks, 0, count);
        var repetition = 1f - MathF.Exp(-count / 4f);
        var exact = exactLinks / (float)count;
        var timing = (float)Math.Clamp(1 - stdDev / Math.Max(.25, Math.Abs(meanDelay) + .25), 0, 1);
        return Math.Clamp(repetition * .45f + timing * .35f + exact * .20f, 0, 1);
    }

    public static float TimelineProbability(SignalTimelineEdge edge, IEnumerable<SignalTimelineEdge> outgoing)
    {
        var total = outgoing.Where(candidate => candidate.From == edge.From && candidate.Phase == edge.Phase).Sum(candidate => Math.Max(0, candidate.Count));
        return total == 0 ? 0 : Math.Clamp(edge.Count / (float)total, 0, 1);
    }

    public static GuidanceKind GuidanceFor(MechanicKind kind) => kind switch
    {
        MechanicKind.GroundAOE or MechanicKind.TargetedAOE => GuidanceKind.Avoid,
        MechanicKind.Stack or MechanicKind.LineStack => GuidanceKind.Stack,
        MechanicKind.Spread => GuidanceKind.Spread,
        MechanicKind.Tower => GuidanceKind.Soak,
        MechanicKind.Gaze => GuidanceKind.LookAway,
        MechanicKind.Knockback or MechanicKind.ForcedMovement => GuidanceKind.Knockback,
        MechanicKind.Tether => GuidanceKind.Tether,
        MechanicKind.Raidwide or MechanicKind.Tankbuster => GuidanceKind.Raidwide,
        MechanicKind.Debuff => GuidanceKind.Cleanse,
        MechanicKind.Proximity or MechanicKind.Environment or MechanicKind.Transition => GuidanceKind.Move,
        _ => GuidanceKind.None
    };

    public static bool GeometryMatches(GeometryKind predicted, float predictedP1, float predictedP2, GeometryKind actual, float actualP1, float actualP2)
    {
        if (predicted == GeometryKind.Unknown || actual == GeometryKind.Unknown || predicted != actual) return false;
        static bool Close(float expected, float observed)
            => expected <= 0 || observed <= 0 || Math.Abs(expected - observed) / Math.Max(1, Math.Abs(observed)) <= .25f;
        return Close(predictedP1, actualP1) && Close(predictedP2, actualP2);
    }
}
