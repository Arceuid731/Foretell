using System.Numerics;

namespace BossMod.Foretell;

// Pure deterministic decision helpers shared by the live engine and the standalone regression harness.
// Confidence here means verified forecast reliability, not merely accumulated evidence.
public static class ForetellInferenceCore
{
    public const int OutOfCombatHazardPhase = -1;

    // Raw telemetry remains complete, but only sources that can plausibly own an encounter mechanic may create
    // learned episodes. Player/pet actions are observations and positional context, never mechanic sources.
    public static bool CanStartMechanicEpisode(ObservationKind kind, SourceKind sourceKind, ulong actorID, uint actorOID, ulong targetID = 0)
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
        return sourceKind == SourceKind.Environment && (kind is ObservationKind.MapEffect or ObservationKind.LegacyMapEffect
            or ObservationKind.ObjectEffect || kind == ObservationKind.Icon && targetID != 0);
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
            SourceKind.Environment => kind is ObservationKind.MapEffect or ObservationKind.LegacyMapEffect or ObservationKind.ObjectEffect
                || kind == ObservationKind.Icon && targetID != 0,
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

    public static float PhaseClockStability(int samples, double meanSeconds, double stdDevSeconds)
    {
        if (samples < 2 || !double.IsFinite(meanSeconds) || !double.IsFinite(stdDevSeconds) || meanSeconds < 0 || stdDevSeconds < 0)
            return 0;
        // A one-second wobble is acceptable even for an early mechanic; long phase clocks receive a bounded
        // proportional tolerance so a loosely timed event cannot look stable merely because it happens late.
        var tolerance = Math.Clamp(meanSeconds * .10 + .75, 1.25, 6);
        return Math.Clamp(1f - (float)(stdDevSeconds / tolerance), 0, 1);
    }

    public static float BossHealthStability(int samples, double stdDevRatio)
    {
        if (samples < 2 || !double.IsFinite(stdDevRatio) || stdDevRatio < 0)
            return 0;
        // Six percentage points of dispersion is deliberately treated as fully unstable. Real HP gates tend to
        // cluster much more tightly, while ordinary time-based mechanics drift with group DPS.
        return Math.Clamp(1f - (float)(stdDevRatio / .06), 0, 1);
    }

    public static bool PreferBossHealthTrigger(int timeSamples, double meanSeconds, double timeStdDev,
        int healthSamples, double healthStdDev)
    {
        if (healthSamples < 3)
            return false;
        var health = BossHealthStability(healthSamples, healthStdDev);
        if (health < .65f)
            return false;
        var time = PhaseClockStability(timeSamples, meanSeconds, timeStdDev);
        // When both clocks are equally stable, prefer time: identical group DPS can create a coincidental HP
        // correlation. HP wins only with meaningful timing drift or materially stronger cross-pull evidence.
        var timingDrift = timeStdDev >= Math.Max(1.5, meanSeconds * .08);
        return timeSamples < 3 || timingDrift || health >= time + .15f;
    }

    public static float TriggerForecastConfidence(int samples, float stability, int hits, int misses)
    {
        samples = Math.Max(0, samples);
        stability = float.IsFinite(stability) ? Math.Clamp(stability, 0, 1) : 0;
        hits = Math.Max(0, hits);
        misses = Math.Max(0, misses);
        if (samples < 3 || stability < .55f)
            return 0;
        var attempts = hits + misses;
        if (attempts >= 3)
            return Math.Min(stability, WilsonLowerBound(hits, attempts));
        var evidence = Math.Min(.94f, .70f + samples * .04f);
        return Math.Min(evidence, stability);
    }

    // Normal running (including sprint) must not become knockback evidence when the 250 ms sampler is delayed.
    // Forced movement is characteristically both abrupt and fast; slower movement remains ordinary positional
    // context without contaminating mechanic classification.
    public static bool IsAbruptDisplacement(float distance, double seconds)
    {
        if (!float.IsFinite(distance) || !double.IsFinite(seconds) || seconds is < .05 or > .75)
            return false;
        return distance >= 3f && distance / seconds >= 10f;
    }

    // Wide cones are common (including attacks whose safe region is only a rear wedge). Outcome fitting must cover
    // the full cone family instead of silently stopping at a 180-degree total angle.
    public static float[] ConeHalfAngleCandidatesDegrees()
        => [15f, 22.5f, 30f, 45f, 60f, 90f, 120f, 135f, 150f, 165f];

    public static bool GeometryParametersComplete(GeometryKind geometry, float p1, float p2)
    {
        if (!float.IsFinite(p1) || !float.IsFinite(p2) || p1 < 0 || p2 < 0)
            return false;
        return geometry switch
        {
            GeometryKind.Circle => p1 > 0,
            GeometryKind.Donut => p1 > 0 && p2 > p1,
            GeometryKind.Cone => p1 > 0 && p2 is > 0 and < MathF.PI,
            GeometryKind.Rectangle or GeometryKind.Cross => p1 > 0 && p2 > 0,
            _ => false
        };
    }

    // Sparse encounter-defining signals must retain a small reserve beyond the ordinary per-frame semantic
    // budget. In alliance raids, a fan-out of position/effect observations can otherwise consume the whole
    // budget immediately before a boss cast arrives, even though the raw packet journal remains complete.
    public static bool IsPrioritySemanticObservation(ObservationKind kind, SourceKind sourceKind)
    {
        // Player rotations are the dominant CastStart/ActionResolved traffic in a 24-player duty. They remain
        // available through the ordinary budget, but must not consume the reserve intended for sparse boss casts.
        if (kind is ObservationKind.CastStart or ObservationKind.ActionResolved)
            return sourceKind is SourceKind.Enemy or SourceKind.EventObject or SourceKind.Environment;
        // Some encounter statuses intentionally have no source actor (for example alliance eligibility/state
        // effects), so Unknown must remain eligible here. Self/party buffs still stay on the ordinary budget.
        if (kind is ObservationKind.StatusGain or ObservationKind.StatusLose)
            return sourceKind is not SourceKind.Player and not SourceKind.Pet;
        return kind is ObservationKind.DutyStarted or ObservationKind.DutyWiped or ObservationKind.DutyRecommenced or ObservationKind.DutyCompleted
            or ObservationKind.Icon or ObservationKind.VFX or ObservationKind.TetherStart or ObservationKind.TetherEnd
            or ObservationKind.MapEffect or ObservationKind.LegacyMapEffect or ObservationKind.DirectorUpdate
            or ObservationKind.EventObjectState or ObservationKind.EventObjectAnimation or ObservationKind.ObjectEffect
            or ObservationKind.ActionTimelineEvent or ObservationKind.ActionTimelineSync or ObservationKind.NpcYell
            or ObservationKind.TargetableChanged or ObservationKind.DeathChanged or ObservationKind.ModelStateChanged;
    }

    public static bool ShouldSurfaceUnshapedCast(float remainingSeconds)
        => float.IsFinite(remainingSeconds) && remainingSeconds >= 2.5f;

    // A fast radial wall sweep is useful for a sealed boss room, but produces convincing-looking false polygons
    // in courtyards and corridors. The floor mesh remains active everywhere; this accelerator is combat-only.
    public static bool ShouldUseFastArenaBoundary(bool inCombat) => inCombat;

    // Topology may clip only confirmed unreachable space. Missing/streaming cells must never turn into a silent
    // false negative for an otherwise valid warning.
    public static bool ShouldPresentOnTopology(bool? passable) => passable != false;

    // Progressive rescans grow outwards from the player. Keep a useful previous result until the replacement has
    // caught up; a complete scan may legitimately shrink after a bridge/platform disappears.
    public static bool ShouldReplaceTopologyAnalysis(int currentPassableCells, int candidatePassableCells, bool complete)
        => currentPassableCells <= 0 || candidatePassableCells > 0 && (complete
            || candidatePassableCells >= currentPassableCells + Math.Max(1, currentPassableCells / 10));

    // Action.VFX 25 is the client-data gaze marker used by the game's generic cast presentation. A gaze is a
    // semantic instruction, not a ground circle: retaining the EffectRange as an AVOID radius would invert the
    // mechanic for attacks such as Catastrophe's Demon Eye.
    public static bool IsGazeActionVFX(uint vfxID) => vfxID == 25;

    // Large non-targeted CastType 2/5 rows are shared by raidwides, proximity attacks and other arena-scale
    // mechanics. EffectRange describes reach, not necessarily an escapable danger circle.
    public static bool IsAmbiguousLargeCircleAction(int castType, int effectRange, bool targetArea, uint omenID)
        => castType is 2 or 5 && effectRange >= 30 && !targetArea && omenID == 0;

    public static bool IsReliableSpatialActionPrior(MechanicKind kind, GeometryKind geometry, float confidence, float p1, float p2)
        => kind == MechanicKind.GroundAOE && confidence >= .9f && GeometryParametersComplete(geometry, p1, p2);

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
        MechanicKind.Marker => GuidanceKind.Marker,
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
