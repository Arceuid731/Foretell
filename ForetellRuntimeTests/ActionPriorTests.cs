using BossMod.Foretell;

internal static class ActionPriorTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static void Run(DecisionContextSnapshot context, ForetellObservation template)
    {
        var prior = new ActionGeometryPrior(901, GeometryKind.Circle, MechanicKind.GroundAOE, 6, 0, .96f,
            2, 6, 0, false, 0, "", 24, "unmarked client circle");
        DecisionAuditEntry[] Evaluate(ActionGeometryPrior metadata)
        {
            var cast = template.CopyForRecording(); cast.Context = context; cast.Value1 = 5;
            cast.TargetID = 1; cast.TargetX = cast.TargetZ = 0; cast.Prior = metadata;
            var result = ForetellEngine.EvaluateRecordedObservations([cast]);
            return result.Knowledge.DecisionAudit.Where(d => d.Stage == DecisionAuditStage.Proposed).ToArray();
        }
        var ambiguous = Evaluate(prior);
        Check(ambiguous.Length > 0 && ambiguous.All(d => d.Geometry == GeometryKind.Unknown && d.Guidance == GuidanceKind.None),
            "Unmarked targeted circle became an AVOID instruction instead of a watch item");
        var circle = Evaluate(prior with { TargetArea = true, OmenID = 1, Omen = "general_1bf" });
        Check(circle.Any(d => d.Geometry == GeometryKind.Circle && d.Guidance == GuidanceKind.Avoid && d.P1 == 6),
            "Explicit ground circle lost its first-cast footprint");
        var knockback = Evaluate(prior with { P1 = 40, EffectRange = 40, OmenID = 203, Omen = "m0295_nockback_omen01i" });
        Check(knockback.Length > 0 && knockback.All(d => d.Geometry == GeometryKind.Unknown && d.Guidance == GuidanceKind.Knockback),
            "Knockback Omen became a 40-yalm AVOID circle");
        var normalized = ForetellInferenceCore.NormalizeActionPrior(prior);
        Check(ForetellInferenceCore.NormalizeActionPrior(normalized) == normalized, "Prior normalization changed on every replay pass");

        var bad = new ContextualMechanic { Key = "384:CastStart:385", SourceOID = 900, SourceKind = SourceKind.Enemy,
            TriggerKind = ObservationKind.CastStart, TriggerID = 901, TerritoryID = 1,
            Kind = MechanicKind.GroundAOE, Geometry = GeometryKind.Circle, P1 = 6, Observations = 100, Confirmations = 100,
            Forecasts = 20, ForecastHits = 20, PriorKind = prior.Kind, PriorGeometry = prior.Geometry,
            PriorCastType = 2, PriorP1 = 6, PriorEffectRange = 6, PriorConfidence = .96f,
            Stages = [new() { Geometry = GeometryKind.Circle, P1 = 6, Observations = 10 }] };
        var good = new ContextualMechanic { Key = "384:CastStart:386", SourceOID = 900, SourceKind = SourceKind.Enemy,
            TriggerKind = ObservationKind.CastStart, TriggerID = 902, TerritoryID = 1,
            Kind = MechanicKind.GroundAOE, Geometry = GeometryKind.Circle, P1 = 6, Observations = 50,
            PriorKind = prior.Kind, PriorGeometry = prior.Geometry, PriorCastType = 2, PriorP1 = 6,
            PriorEffectRange = 6, PriorConfidence = .96f, PriorTargetArea = true };
        var seed = new ForetellStore { Schema = 24, Encounters = { [1] = new() { TerritoryID = 1, Mechanics = { [bad.Key] = bad, [good.Key] = good } } } };
        var migrated = ForetellEngine.EvaluateRecordedObservations([], seed, learn: false).Knowledge;
        var repaired = migrated.Encounters[1].Mechanics[bad.Key];
        Check(migrated.Schema == 25 && repaired.Kind == MechanicKind.Unknown && repaired.Geometry == GeometryKind.Unknown
            && repaired.ForecastHits == 0 && repaired.Stages.Count == 0 && repaired.Observations == 100,
            "Unsafe stored instruction survived migration or raw observation counts were lost");
        Check(migrated.Encounters[1].Mechanics[good.Key].Geometry == GeometryKind.Circle && good.Observations == 50,
            "Guidance migration changed an unrelated explicit ground telegraph");
        Check(seed.Schema == 24 && bad.ForecastHits == 20, "Detached migration modified the input memory");
        Console.WriteLine("Ambiguous circle, knockback Omen, explicit telegraph and stored-prior migration tests passed.");
    }
}
