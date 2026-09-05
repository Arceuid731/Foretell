using System.Numerics;
using System.Text.Json;
using BossMod.Foretell;

internal static class ProductDecisionTests
{
    private static readonly DateTime At = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
    public static void Run()
    {
        ReliabilityDoesNotCountOccurrencesAsProof();
        CuesPreserveAlternativeExplanations();
        PreImpactInputsAndEvaluationDoNotLeakOutcomes();
        RoutesRespectTimingAndUncertainty();
        FootprintsRequirePositionAndTimeAgreement();
        SequencesRequireSpatialAgreement();
        Console.WriteLine("Foretell product decision tests passed.");
    }

    private static ActivePrediction Circle(Vector2 origin, float radius, double seconds = 2, float confidence = .995f)
        => new(10, 20, GeometryKind.Circle, MechanicKind.GroundAOE, origin, origin, 0, radius, 0,
            At.AddSeconds(seconds), confidence, "test") { CreatedAt = At, Guidance = GuidanceKind.Avoid };
    private static DecisionHazard Hazard(long id, ActivePrediction p, double duration = 1)
        => new(id, p, p.Activation.AddSeconds(duration), p.Geometry != GeometryKind.Unknown, false, "test");
    private static DecisionFrame Frame(params DecisionHazard[] hazards) => new(At, hazards, true, true);

    private static void ReliabilityDoesNotCountOccurrencesAsProof()
    {
        Check.That(ForetellReliability.AdditionalSuccesses(0, 0, .75f) == 12, "75% best-case requirement changed");
        Check.That(ForetellReliability.AdditionalSuccesses(0, 0, .95f) == 73, "95% best-case requirement changed");
        Check.That(ForetellReliability.AdditionalSuccesses(0, 0, .99f) == 381, "99% best-case requirement changed");
        Check.That(ForetellReliability.AdditionalSuccesses(0, 0, 1) == -1, "Finite count promised certainty");
        Check.That(ForetellReliability.AdditionalSuccesses(10, 2, .95f) > ForetellReliability.AdditionalSuccesses(10, 0, .95f), "Errors did not increase evidence requirements");
        var mechanic = new ContextualMechanic { Observations = 1000, Confirmations = 1000 };
        Check.That(ForetellReliability.Describe(mechanic).Verified == 0, "Repeated casts became independent tests");
        mechanic.PriorKind = MechanicKind.GroundAOE; mechanic.PriorGeometry = GeometryKind.Circle;
        mechanic.PriorConfidence = .96f; mechanic.PriorP1 = 5;
        Check.That(ForetellReliability.Describe(mechanic) is { ClientShape: true, Verified: 0 }, "Client prior pretended to have outcome validation");
        mechanic.ForecastHits = 500; mechanic.Forecasts = 500;
        Check.That(mechanic.GuidanceConfidence > .99f, "A client prior permanently capped independent evidence");
        mechanic.RecentContradictions = 2;
        Check.That(mechanic.GuidanceConfidence < .75f && ForetellReliability.Describe(mechanic).Maturity == EvidenceMaturity.Conflicted,
            "Recent contradictions failed to demote strong guidance");
    }

    private static void CuesPreserveAlternativeExplanations()
    {
        var cue = new OutcomeCueSummary(8, 6, 0, 0, true, false, false, false, false, GeometryKind.Unknown, 0, 8);
        var hypotheses = ForetellOutcomeHypotheses.Candidates(cue).Select(c => c.Kind).ToArray();
        Check.That(hypotheses.Contains(MechanicKind.Raidwide) && hypotheses.Contains(MechanicKind.GroundAOE)
            && hypotheses.Contains(MechanicKind.Stack) && hypotheses.Contains(MechanicKind.Spread), "Cue ambiguity collapsed to an unsupported mechanic");
        Check.That(ForetellOutcomeHypotheses.IndependentLabel(cue) == MechanicKind.Unknown, "75% party hits trained a raidwide label");
        Check.That(ForetellOutcomeHypotheses.IndependentLabel(cue with { Geometry = GeometryKind.Circle, FitScore = 1, Affected = 1 })
            == MechanicKind.Unknown, "One victim established a spatial label");
    }

    private static void PreImpactInputsAndEvaluationDoNotLeakOutcomes()
    {
        var trigger = new ForetellObservation { At = At, Kind = ObservationKind.CastStart, Value1 = 3, ActorID = 10, PrimaryID = 20 };
        var cue = new ForetellObservation { At = At.AddSeconds(-1), Kind = ObservationKind.VFX, SourceKind = SourceKind.Enemy };
        cue.Numeric["animation.speed"] = 2;
        var features = ForetellPreImpactModel.Features(trigger, [Vector2.One], [cue]);
        trigger.Numeric["outcome.damage"] = 99999; trigger.TerritoryID = 999; trigger.ActorID = 999; trigger.PrimaryID = 999;
        var future = new ForetellObservation { At = At.AddSeconds(1), Kind = ObservationKind.VFX, SourceKind = SourceKind.Enemy };
        future.Numeric["animation.speed"] = 999;
        Check.That(features.SequenceEqual(ForetellPreImpactModel.Features(trigger, [Vector2.One], [cue, future])),
            "Future outcome or opaque identity leaked into pre-impact features");
        var memory = new PreImpactMemory(); var model = new ForetellPreImpactModel(memory);
        var guess = model.Predict(features);
        var label = guess.Kind == MechanicKind.GroundAOE ? MechanicKind.Knockback : MechanicKind.GroundAOE;
        model.Resolve(features, guess, label, 3, true);
        Check.That(memory.Classes[guess.Kind].Hits == 0 && memory.Classes[guess.Kind].Assessed == 1 && memory.Model.Updates == 1,
            "Training occurred before evaluating the issued prediction");
        var weights = JsonSerializer.Serialize(memory.Model);
        model.FreezeCalibration();
        var frozen = model.Predict(features);
        for (var i = 0; i < 20; ++i) model.Resolve(features, frozen, frozen.Kind == MechanicKind.Unknown ? label : frozen.Kind, 3, true, train: false);
        Check.That(JsonSerializer.Serialize(memory.Model) == weights && model.Predict(features).Reliability == frozen.Reliability,
            "Frozen evaluation changed weights or calibration used by later decisions");
        var updates = memory.Model.Updates;
        model.Resolve(features, guess, label, 3, false);
        Check.That(memory.Model.Updates == updates && memory.MissingOutcomes > 0, "Incomplete capture trained the model");
    }

    private static void RoutesRespectTimingAndUncertainty()
    {
        var originDanger = Hazard(1, Circle(Vector2.Zero, 1, 2));
        var start = Vector2.Zero; var end = new Vector2(8, 0);
        bool Clear(Vector2 a, Vector2 b) => true;
        Check.That(ForetellDecisionCore.AssessRoute(Frame(originDanger), start, end, Clear).Eligible, "Leaving before activation was rejected");
        var uncertain = Hazard(2, Circle(end, 2, 2, .76f));
        Check.That(!ForetellDecisionCore.AssessRoute(Frame(originDanger, uncertain), start, end, Clear).Eligible, "Lower-confidence credible danger was ignored");
        var crossing = Hazard(3, Circle(new(4, 0), 1, .8));
        Check.That(!ForetellDecisionCore.AssessRoute(Frame(crossing), start, end, Clear).Eligible, "Route crossed an active hazard");
        Check.That(ForetellDecisionCore.AssessRoute(Frame(Hazard(4, crossing.Prediction with { Activation = At.AddSeconds(4) })), start, end, Clear).Eligible,
            "Crossing long before activation was rejected");
        Check.That(!ForetellDecisionCore.AssessRoute(Frame(originDanger), start, end, (_, _) => false).Eligible, "Route crossed unknown terrain");
        Check.That(!ForetellDecisionCore.AssessRoute(Frame(originDanger) with { EvidenceComplete = false }, start, end, Clear).Eligible, "Incomplete capture allowed a route");
        var unknown = Circle(end, 1) with { Geometry = GeometryKind.Unknown, Guidance = GuidanceKind.Stack };
        Check.That(!ForetellDecisionCore.AssessRoute(Frame(Hazard(5, unknown)), start, end, Clear).Eligible, "Unresolved personal constraint allowed a route");
        var moving = Circle(new(4, 10), .25f, 0) with { Velocity = new(0, -10), MotionUntil = At.AddSeconds(2) };
        Check.That(!ForetellDecisionCore.AssessRoute(Frame(Hazard(6, moving, 2)), start, end, Clear).Eligible, "Fast moving hazard was skipped between samples");
        var polygon = Circle(end, 0) with { Geometry = GeometryKind.Polygon, Polygon = [new(-2, -2), new(2, -2), new(2, 2), new(-2, 2)] };
        Check.That(ForetellDecisionCore.Contains(polygon, end, At) && !ForetellDecisionCore.AssessRoute(Frame(Hazard(7, polygon)), start, end, Clear).Eligible,
            "Floor-change polygon did not constrain the destination");
        Check.That(!ForetellDecisionCore.AssessRoute(Frame(Hazard(8, polygon with { Velocity = new(float.NaN, 0) })), start, end, Clear).Eligible,
            "Invalid motion silently became safe");
    }

    private static void FootprintsRequirePositionAndTimeAgreement()
    {
        Check.That(ForetellOutcomeValidation.VerifyTiming(At.AddSeconds(30), At.AddSeconds(33), At.AddSeconds(5), true) == false,
            "A signal much earlier than predicted validated timing");
        Check.That(ForetellOutcomeValidation.VerifyTiming(At.AddSeconds(30), At.AddSeconds(33), At.AddSeconds(30), false) == null,
            "Missing signal capture received a timing verdict");
        Check.That(!ForetellDecisionCore.Valid(Circle(Vector2.Zero, 0)) && !ForetellDecisionCore.Valid(Circle(Vector2.Zero, 5) with { Geometry = (GeometryKind)999 }),
            "Incomplete or unknown shape could silently permit a route");
        var p = Circle(Vector2.Zero, 5);
        SpatialOutcomePoint[] outcomes = [new(new(1, 1), true), new(new(-1, -1), true), new(new(10, 0), false), new(new(0, 10), false)];
        Check.That(ForetellOutcomeValidation.Verify(p, MechanicKind.GroundAOE, p.Activation, outcomes, true) == true, "Correct spatial prediction was not assessed");
        Check.That(ForetellOutcomeValidation.Verify(p with { Origin = new(30, 0) }, MechanicKind.GroundAOE, p.Activation, outcomes, true) == false,
            "Correct family at wrong origin counted as correct");
        Check.That(ForetellOutcomeValidation.Verify(p with { Activation = At.AddSeconds(8) }, MechanicKind.GroundAOE, p.Activation, outcomes, true) == false,
            "Incorrect timing counted as correct");
        Check.That(ForetellOutcomeValidation.Verify(p, MechanicKind.GroundAOE, p.Activation, outcomes.Select(o => o with { Hit = false }).ToArray(), true) == null,
            "Successful dodges falsely established the shape");
        Check.That(ForetellOutcomeValidation.Verify(p, MechanicKind.GroundAOE, p.Activation, outcomes, false) == null, "Capture gap became a verdict");
        Check.That(ForetellOutcomeValidation.Verify(p with { CreatedAt = p.Activation }, MechanicKind.GroundAOE, p.Activation, outcomes, true) == null,
            "After-impact prediction received validation credit");
    }

    private static void SequencesRequireSpatialAgreement()
    {
        var stage = new HazardStage { EffectAction = 42, Geometry = GeometryKind.Circle, P1 = 5, Delay = 2 };
        Check.That(ForetellDecisionCore.StageMatches(stage, stage with { Delay = 2.2f }), "Minor stage timing jitter was rejected");
        Check.That(!ForetellDecisionCore.StageMatches(stage, stage with { OffsetX = 10 }), "Moving a stage did not contradict it");
        Check.That(!ForetellDecisionCore.StageMatches(stage, stage with { Delay = 5 }), "Stage at wrong time counted as correct");
        Check.That(!ForetellDecisionCore.StageMatches(stage, stage with { EffectAction = 43 }), "Unrelated action validated a stage");
    }
}
