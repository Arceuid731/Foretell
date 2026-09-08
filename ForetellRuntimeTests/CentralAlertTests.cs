using BossMod;
using BossMod.Foretell;
using System.Numerics;

internal static class CentralAlertTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Run()
    {
        var now = new DateTime(2026, 9, 7, 17, 6, 0, DateTimeKind.Utc);
        var prediction = new ActivePrediction(100, 28983, GeometryKind.Circle, MechanicKind.GroundAOE,
            Vector2.Zero, Vector2.Zero, 0, 8, 0, now.AddSeconds(4), .95f, "recorded circle", Guidance: GuidanceKind.Avoid);
        var many = Enumerable.Range(0, 12).Select(index => prediction with { CasterID = (ulong)(100 + index), Origin = new(index, 0) }).ToArray();
        var frame = new DecisionFrame(now, many.Select((entry, index) => new DecisionHazard(index, entry, entry.Activation, true, false, "fixture")).ToArray(), true, true);
        Check(ForetellCentralPresentation.Predictions(many).Length == 1, "Simultaneous helper circles produced duplicate central alerts");
        Check(ForetellDecisionCore.SelectForDisplay(frame, .75f, 2).Length == 12, "Central deduplication removed world/radar footprints");
        Check(ForetellCentralPresentation.Predictions([prediction, prediction with { Activation = prediction.Activation.AddSeconds(2) }]).Length == 2,
            "Distinct attack waves were collapsed");
        Check(ForetellCentralPresentation.Predictions([prediction, prediction with { ActionID = 900 }, prediction with { Guidance = GuidanceKind.Stack }]).Length == 3,
            "Different mechanics or contradictory responses were collapsed");
        Check(ForetellCentralPresentation.Predictions([prediction with { Geometry = GeometryKind.Unknown, Guidance = GuidanceKind.Marker, GuideLinked = true }]).Length == 0,
            "Guide annotation produced another generic text alert");

        var mechanic = new GuideMechanic("Radiant Plume", "Radiant Plume: Avoid circle AoEs.", "")
        {
            Advice = new(GuideLanguage.English, "Radiant Plume", "Avoid circle AoEs", "Avoid the circles.", "cast", "Radiant Plume", ["Radiant Plume: Avoid circle AoEs."])
        };
        var phase = new GuidePhase("", "", [mechanic]);
        var boss = new GuideBoss("The Ultima Weapon", "", [phase]);
        var signal = new GuideSignal(boss, phase, mechanic, GuideSignalKind.Cast, 1, 14587, 2137, 28982, 1,
            prediction.Activation.AddSeconds(-.8), GuidanceKind.Avoid, "exact action name");
        Check(GuideCentralPresentation.OwnsRelated(prediction, [signal], "Radiant Plume", 2137, 0),
            "Boss control cast and same-name helper damage cast both produced central alerts");
        Check(!GuideCentralPresentation.OwnsRelated(prediction, [signal], "Radiant Blaze", 2137, 0)
            && !GuideCentralPresentation.OwnsRelated(prediction, [signal], "Radiant Plume", 999, 0)
            && !GuideCentralPresentation.OwnsRelated(prediction, [signal], "Radiant Plume", 2137, 999)
            && !GuideCentralPresentation.OwnsRelated(prediction with { Activation = prediction.Activation.AddSeconds(2) }, [signal], "Radiant Plume", 2137, 0),
            "Guide duplicate suppression crossed name, boss or occurrence identity");
        Check(GuideCentralPresentation.Select([signal, signal with { SourceID = 2, Until = signal.Until.AddSeconds(.1) }], 10).Length == 1,
            "Multiple matching guide casters produced duplicate alerts");
        Check(GuideCentralPresentation.Select([signal, signal with { SourceID = 2, ID = 28983, Until = signal.Until.AddSeconds(.8) }], 10).Length == 1,
            "Control and damage action IDs split one named mechanic into two central alerts");
        var helper = new Actor(100, 9020, 0, 0, "The Ultima Weapon", 2137, ActorType.Helper, Class.None, 1, Vector4.Zero, resolveGameMetadata: false)
        { CastInfo = new() { Action = new(ActionType.Spell, 28983), TotalTime = 4 } };
        var document = new GuideDocument(GuideDocument.CurrentSchema, new(830, 1048, "the Porta Decumana"), "the Porta Decumana", 1,
            now, GuideNames.Hash("helper-guide"), [boss]);
        var combat = new GuideCombatFrame(boss, false, false, phase, [signal], 0);
        string Name(string sheet, uint identifier) => sheet == "BNpcName" && identifier == 2137 ? "The Ultima Weapon"
            : sheet == "Action" && identifier == 28983 ? "Radiant Plume" : "";
        Check(ForetellEngine.MatchNamedGuideHelper(document, document.Duty, helper, combat, now, Name)?.Mechanic == mechanic,
            "Named current-boss helper lost the guide tip after its control cast finished");
        Check(ForetellEngine.MatchNamedGuideHelper(document, document.Duty, helper, combat with { Upcoming = true }, now, Name) == null
            && ForetellEngine.MatchNamedGuideHelper(document, document.Duty, helper, combat with { Ambiguous = true }, now, Name) == null
            && ForetellEngine.MatchNamedGuideHelper(document, document.Duty, helper, combat, now,
                (sheet, identifier) => sheet == "BNpcName" ? "Other boss" : Name(sheet, identifier)) == null,
            "An unengaged, ambiguous or differently named helper inherited a boss guide");
        helper.OwnerID = 999;
        Check(ForetellEngine.MatchNamedGuideHelper(document, document.Duty, helper, combat, now, Name) == null,
            "Explicit unrelated helper ownership was ignored");
        var warning = new ForetellCentralAlert("Avoid circles", "Plume", 4, 4, now.AddSeconds(4), true, false);
        var grounded = signal with { Instruction = "Hide behind cover", Trigger = new("cast", "Radiant Plume", "Hide behind cover", []) };
        Check(GuideCentralPresentation.Select([grounded, grounded with { SourceID = 2 }], 10).Length == 1,
            "Event-specific instruction duplicated across helper actors");
        Check(GuideCentralPresentation.Select([grounded, grounded with { Instruction = "Move away from the target" }], 10).Length == 2,
            "Distinct event responses were collapsed into an unrelated instruction");
        var laterGuide = warning with { At = now.AddSeconds(8), FromGuide = true };
        Check(ForetellCentralPresentation.Select([warning, laterGuide], 2)[0] == laterGuide,
            "A generic category outranked a guide response with equal personal priority");
        var replacement = warning with { At = now.AddSeconds(1), FromGuide = true,
            Personal = GuideCentralPresentation.OwnsRelated(prediction, [signal], "Radiant Plume", 2137, 0)
                && ForetellCentralPresentation.Personal(prediction, Vector2.Zero, 10) };
        Check(ForetellCentralPresentation.Select([warning with { At = now.AddSeconds(10) }, warning with { At = now.AddSeconds(11) }, replacement], 2)[0] == replacement,
            "A guide replacement lost its spatial personal priority to distant warnings");
        var selected = ForetellCentralPresentation.Select([warning, warning with { At = now.AddSeconds(3), FromGuide = true },
            warning with { At = now.AddSeconds(1), Personal = false }, warning with { At = now.AddSeconds(20) }], 12);
        Check(selected.Length == 2 && selected.All(alert => alert.Personal) && selected[0].At == now.AddSeconds(3),
            "Combined guide/generic alert budget or immediate personal priority regressed");
        Console.WriteLine("Central alert grouping, helper cast ownership, personal priority and independent world geometry passed.");
    }
}
