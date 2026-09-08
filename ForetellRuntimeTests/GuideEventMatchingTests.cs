using BossMod;
using BossMod.Foretell;
using System.Numerics;

internal static class GuideEventMatchingTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static void Run()
    {
        var cast = new GuideTrigger("cast", "Detonation", "Hide behind cover", ["Detonation: hide behind cover."]);
        var status = new GuideTrigger("status", "Charged", "Move away from the party", ["At 4 stacks of Charged, move away from the party."])
            { Target = "self", MinimumStacks = 4 };
        var mechanic = new GuideMechanic("Bomb pattern", "", "")
        {
            Advice = new(GuideLanguage.English, "Bomb pattern", "Cast: hide; Charged: spread", "", "manual", "", [.. cast.Evidence, .. status.Evidence])
            { Triggers = [cast, status] }
        };
        var phase = new GuidePhase("", "", [mechanic]);
        var boss = new GuideBoss("Fixture boss", "", [phase]);
        GuideEventResolution Resolve(string kind, string name, ulong target = 10, bool party = true, int stacks = 0, GuideBoss? other = null)
            => GuideEventMatching.Resolve(other ?? boss, null, kind, name, target, 10, party, stacks);
        Check(Resolve("cast", "Detonation") is { Mechanic: not null, Trigger.Cue: "Hide behind cover" }, "A different named cast did not resolve to the documented pattern");
        Check(Resolve("status", "Charged", stacks: 3) is { Mechanic: null, Reason: "TargetOrStacksNotSatisfied" }, "Status fired before its documented stack threshold");
        Check(Resolve("status", "Charged", stacks: 4).Trigger?.Cue == "Move away from the party", "Status did not choose its specific response");
        Check(Resolve("status", "Charged", target: 11, stacks: 4).Mechanic == null, "Another player's status triggered self instructions");
        Check(Resolve("cast", "Detonations").Mechanic == null && Resolve("cast", "").Reason == "NameUnavailable", "Fuzzy or missing names became confident bindings");
        Check(Resolve("cast", "Charged").Mechanic == null, "Status trigger was treated as a cast");
        var sameNameStatus = new GuideMechanic("Charged", "Charged: move away", "") { Advice = mechanic.Advice with { Triggers = [status] } };
        Check(Resolve("cast", "Charged", other: boss with { Phases = [phase with { Mechanics = [sameNameStatus] }] }).Mechanic == null,
            "Legacy name fallback bypassed an explicit status event kind");
        var targeted = new GuideMechanic("Targeted blast", "", "") { Advice = mechanic.Advice with { Triggers =
            [cast with { Target = "self", Cue = "Move out" }, cast with { Target = "other", Cue = "Keep clear of the target" }] } };
        var targetBoss = boss with { Phases = [phase with { Mechanics = [targeted] }] };
        Check(Resolve("cast", "Detonation", other: targetBoss).Trigger?.Cue == "Move out", "Self target did not choose its own cue");
        Check(Resolve("cast", "Detonation", target: 11, other: targetBoss).Trigger?.Cue == "Keep clear of the target", "Other party target did not choose its own cue");
        Check(Resolve("cast", "Detonation", target: 99, party: false, other: targetBoss).Mechanic == null, "Boss self-target was mistaken for another player");
        var duplicate = new GuideMechanic("Other mechanic", "", "") { Advice = mechanic.Advice };
        Check(Resolve("cast", "Detonation", other: boss with { Phases = [phase with { Mechanics = [mechanic, duplicate] }] }).Reason == "AmbiguousTrigger", "Two mechanics sharing a trigger were guessed");
        var conflict = new GuideMechanic("Conflict", "", "") { Advice = mechanic.Advice with { Conflict = "Sources disagree" } };
        Check(Resolve("cast", "Detonation", other: boss with { Phases = [phase with { Mechanics = [conflict] }] }).Reason == "ConflictingOrContextOnly", "Conflicting source triggered a direction");
        var legacy = new GuideMechanic("Poison", "", "") { Advice = new(GuideLanguage.English, "Poison", "If afflicted: move away", "", "manual", "", ["Poison afflicts the marked player."]) };
        Check(Resolve("status", "Poison", other: boss with { Phases = [phase with { Mechanics = [legacy] }] }).Mechanic == legacy, "A legacy manual entry could not bind to its exact observed debuff name");
        var first = new GuidePhaseDefinition("first", "First", []);
        var second = new GuidePhaseDefinition("second", "Second", []);
        var next = new GuideMechanic("Next phase", "", "") { Advice = mechanic.Advice, PhaseMemberships = [new("second", [])] };
        var phased = boss with { Phases = [phase with { Mechanics = [next] }], PhaseDefinitions = [first, second] };
        Check(GuideEventMatching.Resolve(phased, first, "cast", "Detonation", 10, 10, true, 0).Reason == "MatchedPhaseTransition", "Current phase locked out its successor's exclusive trigger");
        var owner = new Actor(1, 2, 0, 0, "Fixture boss", 3, ActorType.Enemy, Class.None, 1, Vector4.Zero, resolveGameMetadata: false);
        var helper = new Actor(4, 5, 0, 0, "Helper", 6, ActorType.Helper, Class.None, 1, Vector4.Zero, resolveGameMetadata: false) { OwnerID = 1 };
        Check(ForetellEngine.GuideEventSource(owner, null, 3) && ForetellEngine.GuideEventSource(helper, owner, 3), "Boss/owned helper identity rejected");
        helper.OwnerID = 99;
        Check(!ForetellEngine.GuideEventSource(helper, owner, 3), "Unrelated owner linked to current boss");
        var document = new GuideDocument(1, new(1, 2, "Fixture"), "Fixture", 1, DateTime.UtcNow, GuideNames.Hash("source"), [boss]);
        Check(GuideEventMatching.Scope(document, boss) != GuideEventMatching.Scope(document with { SourceHash = GuideNames.Hash("updated") }, boss), "Bindings survived changed sources");
        Check(GuideEventMatching.Scope(document, boss) != GuideEventMatching.Scope(document with { Duty = new(2, 2, "Other") }, boss), "Bindings crossed duties");
        Check(GuideEventMatching.Scope(document, boss) != GuideEventMatching.Scope(document, targetBoss), "Bindings survived changed trigger meaning");
        Console.WriteLine("Guide event binding: multiple exact triggers, conditional cues, stack gates, ambiguity, legacy statuses, phases and source ownership passed.");
    }
}
