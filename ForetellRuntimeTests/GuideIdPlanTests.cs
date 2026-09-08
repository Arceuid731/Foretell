using BossMod;
using BossMod.Foretell;
using System.Numerics;

internal static class GuideIdPlanTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static GuideDocument Document(params GuideBoss[] bosses)
        => new(1, new(1, 2, "Fixture duty"), "Fixture duty", 1, new(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc), GuideNames.Hash("fixture source"), bosses);

    private static GuideMechanic Mechanic(string name, params GuideTrigger[] triggers)
        => new(name, "", "")
        {
            Advice = new(GuideLanguage.English, name, "Follow the documented cue", "", "manual", "", triggers.SelectMany(trigger => trigger.Evidence).ToArray())
            { Triggers = triggers }
        };

    private static GuideBoss Boss(string name, params GuideMechanic[] mechanics) => new(name, "", [new("", "", mechanics)]);
    private static GuideTrigger Trigger(string kind, string name, string cue = "Move away") => new(kind, name, cue, [$"{name}: {cue}."]);

    public static void Run()
    {
        OfficialAliasesAndMultipleIDs();
        ScopeAndUnknownIDs();
        ConditionsAndAmbiguity();
        LegacyAndBossAliases();
        SourceOwnershipAndActionQueue();
        GuideIdReplayReview.RunTests();
        Console.WriteLine("Guide ID plan: official aliases, multiple IDs, guide/boss scope, unknown IDs, conditions, ambiguity, legacy grounding and bounded historical review passed.");
    }

    private static void OfficialAliasesAndMultipleIDs()
    {
        var mechanic = Mechanic("Bomb pattern", Trigger("cast", "Détonation"));
        var boss = Boss("Gardien", mechanic);
        var document = Document(boss);
        var catalog = new GuideIdCatalog([
            new("boss", 10, "Warden", ["Gardien"]),
            new("cast", 101, "Detonation", ["Détonation", "Detonation", "爆発"]),
            new("cast", 102, "Detonation", ["Détonation"]),
            new("status", 101, "Charged", ["Charge"])
        ]);
        var plan = new GuideIdPlan(document, catalog);
        Check(ReferenceEquals(plan.Document, document) && ReferenceEquals(plan.Catalog, catalog), "Plan lost its document/catalog provenance.");
        Check(plan.Boss(10) == boss, "Official translated boss alias did not resolve.");
        foreach (var identifier in new uint[] { 101, 102 })
            Check(plan.Resolve(boss, null, "cast", identifier, 0, 0, false, 0).Mechanic == mechanic,
                "Official translated trigger failed to resolve using only an ID.");
        Check(plan.Entries.Any(entry => entry.Kind == "cast" && entry.IDs.Contains(101u) && entry.IDs.Contains(102u)),
            "Same-name action IDs were collapsed instead of preserved as candidates.");
        Check(plan.Resolve(boss, null, "status", 101, 0, 0, false, 0).Mechanic == null, "Action ID leaked into the status namespace.");
    }

    private static void ScopeAndUnknownIDs()
    {
        var catalog = new GuideIdCatalog([
            new("boss", 10, "Warden", []), new("boss", 20, "Sentinel", []),
            new("cast", 101, "Detonation", []), new("cast", 102, "Detonation II", []), new("cast", 103, "Unrelated", [])
        ]);
        var mechanic = Mechanic("Bomb pattern", Trigger("cast", "Detonation"));
        var boss = Boss("Warden", mechanic);
        var otherBoss = Boss("Sentinel", Mechanic("Other pattern", Trigger("cast", "Unrelated")));
        var document = Document(boss, otherBoss);
        var plan = new GuideIdPlan(document, catalog);
        Check(plan.Resolve(otherBoss, null, "cast", 101, 0, 0, false, 0).Mechanic == null, "An ID crossed boss boundaries.");
        foreach (var identifier in new uint[] { 0, 102, 103, uint.MaxValue })
            Check(plan.Resolve(boss, null, "cast", identifier, 0, 0, false, 0).Mechanic == null, "Unknown, fuzzy or unrelated ID was guessed.");
        Check(plan.Resolve(boss, null, "icon", 101, 0, 0, false, 0).Mechanic == null, "Unsupported event kind became a cast.");
        var foreignBoss = Boss("Warden", Mechanic("Foreign pattern", Trigger("cast", "Unrelated")));
        Check(plan.Resolve(foreignBoss, null, "cast", 103, 0, 0, false, 0).Mechanic == null, "A caller supplied an out-of-document boss.");
        var otherDocument = Document(foreignBoss) with { Duty = new(3, 4, "Other duty"), SourceHash = GuideNames.Hash("other source") };
        var otherPlan = new GuideIdPlan(otherDocument, catalog);
        Check(otherPlan.Resolve(foreignBoss, null, "cast", 101, 0, 0, false, 0).Mechanic == null, "A different duty inherited another guide's ID bindings.");
        Check(otherPlan.Resolve(foreignBoss, null, "cast", 103, 0, 0, false, 0).Mechanic != null, "Independent guide failed to compile its own IDs.");
    }

    private static void ConditionsAndAmbiguity()
    {
        var catalog = new GuideIdCatalog([new("cast", 101, "Detonation", []), new("status", 201, "Charged", ["Charge"])]);
        var self = Trigger("cast", "Detonation", "Move out") with { Target = "self" };
        var other = self with { Target = "other", Cue = "Keep clear of the target" };
        var status = Trigger("status", "Charge", "Spread") with { Target = "self", MinimumStacks = 4 };
        var mechanic = Mechanic("Target pattern", self, other, status);
        var boss = Boss("Warden", mechanic);
        var plan = new GuideIdPlan(Document(boss), catalog);
        Check(plan.Resolve(boss, null, "cast", 101, 10, 10, true, 0).Trigger?.Cue == self.Cue, "Self-target cue was lost.");
        Check(plan.Resolve(boss, null, "cast", 101, 11, 10, true, 0).Trigger?.Cue == other.Cue, "Other party-target cue was lost.");
        Check(plan.Resolve(boss, null, "cast", 101, 99, 10, false, 0).Mechanic == null, "Non-party target satisfied a party cue.");
        Check(plan.Resolve(boss, null, "status", 201, 10, 10, true, 3).Mechanic == null, "Status fired below its stack threshold.");
        Check(plan.Resolve(boss, null, "status", 201, 10, 10, true, 4).Trigger?.Cue == status.Cue, "Translated status lost its threshold cue.");
        Check(plan.Resolve(boss, null, "status", 201, 11, 10, true, 4).Mechanic == null, "Another player's status satisfied self targeting.");
        Check(plan.Resolve(boss, null, "status", 201, 10, 0, true, 4).Mechanic == null, "Unknown player satisfied self targeting.");
        var duplicateBoss = Boss("Warden", mechanic, Mechanic("Second pattern", self));
        Check(new GuideIdPlan(Document(duplicateBoss), catalog).Resolve(duplicateBoss, null, "cast", 101, 10, 10, true, 0).Mechanic == null,
            "Ambiguous mechanic candidates were guessed.");
        var conflictBoss = Boss("Warden", Mechanic("Conflict", self, self with { Cue = "Stay still" }));
        Check(new GuideIdPlan(Document(conflictBoss), catalog).Resolve(conflictBoss, null, "cast", 101, 10, 10, true, 0).Mechanic == null,
            "Conflicting cues on one mechanic were guessed.");
        foreach (var advice in new[] { mechanic.Advice! with { Conflict = "Sources disagree" }, mechanic.Advice! with { ContextOnly = true } })
        {
            var blockedBoss = Boss("Warden", new GuideMechanic("Blocked", "", "") { Advice = advice });
            Check(new GuideIdPlan(Document(blockedBoss), catalog).Resolve(blockedBoss, null, "cast", 101, 10, 10, true, 0).Mechanic == null,
                "Conflicting/context-only advice became an ID instruction.");
        }
        var first = new GuidePhaseDefinition("first", "First", []);
        var second = new GuidePhaseDefinition("second", "Second", []);
        GuideMechanic Phased(string name, string phaseID, string cue) => new(name, "", "")
        {
            Advice = Mechanic(name, Trigger("cast", "Detonation", cue)).Advice,
            PhaseMemberships = [new(phaseID, [])]
        };
        var opening = Phased("Opening", first.ID, "Spread");
        var ending = Phased("Ending", second.ID, "Stack");
        var phasedBoss = Boss("Warden", opening, ending) with { PhaseDefinitions = [first, second] };
        var phasedPlan = new GuideIdPlan(Document(phasedBoss), catalog);
        Check(phasedPlan.Resolve(phasedBoss, null, "cast", 101, 0, 0, false, 0).Mechanic == null, "Unknown phase guessed between repeated IDs.");
        Check(phasedPlan.Resolve(phasedBoss, first, "cast", 101, 0, 0, false, 0).Mechanic == opening, "First phase selected the wrong mechanic.");
        Check(phasedPlan.Resolve(phasedBoss, second, "cast", 101, 0, 0, false, 0).Mechanic == ending, "Second phase selected the wrong mechanic.");
    }

    private static void LegacyAndBossAliases()
    {
        var catalog = new GuideIdCatalog([
            new("boss", 10, "The Warden", ["Gardien"]), new("cast", 101, "Detonation", ["Détonation"]),
            new("status", 201, "Poison", []), new("cast", 102, "Ungrounded", [])
        ]);
        var cached = new GuideMechanic("Detonation", "Raidwide damage", "");
        var manual = new GuideMechanic("Poison", "", "")
        { Advice = new(GuideLanguage.English, "Poison", "Move away", "", "manual", "", ["Poison afflicts the marked player."]) };
        var ungrounded = new GuideMechanic("Ungrounded", "", "")
        { Advice = new(GuideLanguage.English, "Ungrounded", "Move away", "", "manual", "", []) };
        var boss = Boss("Gardien", cached, manual, ungrounded);
        var plan = new GuideIdPlan(Document(boss), catalog);
        Check(plan.Boss(10) == boss && plan.Boss(999) == null, "Boss alias lookup guessed or missed a unique official alias.");
        Check(plan.Resolve(boss, null, "cast", 101, 0, 0, false, 0).Mechanic == cached, "Legacy cached exact mechanic lost its ID grounding.");
        Check(plan.Resolve(boss, null, "status", 201, 0, 0, false, 0).Mechanic == manual, "Grounded manual status was not preserved.");
        Check(plan.Resolve(boss, null, "cast", 102, 0, 0, false, 0).Mechanic == null, "Ungrounded manual advice became a confident ID instruction.");
        Check(plan.Entries.Any(entry => entry.Mechanic == ungrounded.Name && entry.Reason == "NoGroundedTrigger" && entry.IDs.Length == 0),
            "A mechanic without a grounded trigger disappeared from ID diagnostics.");
        var ambiguous = new GuideIdPlan(Document(boss, Boss("The Warden", cached)), catalog);
        Check(ambiguous.Boss(10) == null, "Two guide bosses sharing official aliases were guessed.");
        var grouped = new GuideMechanic("Combined bomb pattern", "", "")
        { Advice = new(GuideLanguage.English, "Combined bomb pattern", "Spread", "", "manual", "", ["Detonation: spread."]) };
        var groupedBoss = Boss("Gardien", grouped);
        Check(new GuideIdPlan(Document(groupedBoss), catalog).Resolve(groupedBoss, null, "cast", 101, 0, 0, false, 0).Mechanic == grouped,
            "Legacy exact quoted action heading lost its official ID grounding under a grouped mechanic name.");
    }

    private static void SourceOwnershipAndActionQueue()
    {
        var mechanic = Mechanic("Bomb pattern", Trigger("cast", "Détonation"));
        var boss = Boss("Gardien", mechanic);
        var otherBoss = Boss("Sentinel");
        var document = Document(boss, otherBoss);
        var catalog = new GuideIdCatalog([
            new("boss", 10, "Warden", ["Gardien"]), new("boss", 11, "Warden", ["Gardien"]),
            new("boss", 20, "Sentinel", []), new("cast", 101, "Detonation", ["Détonation"])
        ]);
        var plan = new GuideIdPlan(document, catalog);
        var owner = new Actor(100, 200, 0, 0, "", 10, ActorType.Enemy, Class.None, 1, Vector4.Zero, resolveGameMetadata: false);
        var alternate = new Actor(101, 201, 0, 0, "", 11, ActorType.Enemy, Class.None, 1, Vector4.Zero, resolveGameMetadata: false);
        var foreign = new Actor(102, 202, 0, 0, "", 20, ActorType.Enemy, Class.None, 1, Vector4.Zero, resolveGameMetadata: false);
        var helper = new Actor(103, 203, 0, 0, "", 30, ActorType.Helper, Class.None, 1, Vector4.Zero, ownerID: owner.InstanceID, resolveGameMetadata: false);
        Check(plan.OwnsSource(boss, owner, null) && plan.OwnsSource(boss, alternate, null), "Alternate official name IDs for the same guide boss were rejected.");
        Check(plan.OwnsSource(boss, helper, owner), "Owned helper failed official boss attribution.");
        Check(!plan.OwnsSource(boss, foreign, null) && !plan.OwnsSource(boss, helper, foreign) && !plan.OwnsSource(boss, helper, null),
            "Foreign or missing owner satisfied official boss attribution.");
        var queue = new GuideActionQueue();
        var frame = new GuideCombatFrame(boss, false, false, null, [], 0);
        var now = document.RetrievedAt;
        Actor? ActorByID(ulong identifier) => identifier == owner.InstanceID ? owner : identifier == helper.InstanceID ? helper : null;
        string RuntimeName(string sheet, uint identifier) => throw new InvalidOperationException("Official ID action unexpectedly requested a runtime name.");
        queue.Enqueue(document.Duty, boss, helper, owner, 101, 500, now);
        var signals = queue.Resolve(document, document.Duty, frame, now, ActorByID, RuntimeName, plan);
        Check(signals is [{ ID: 101, Kind: GuideSignalKind.Action } signal] && signal.Mechanic == mechanic
            && signal.SourceID == helper.InstanceID && signal.OwnerID == owner.InstanceID && signal.TargetID == 500,
            "ID-only action queue lost mechanic/source/owner/target identity.");
        Check(queue.Count == 0 && queue.Resolve(document, document.Duty, frame, now, ActorByID, RuntimeName, plan).Length == 0,
            "ID-only action queue emitted duplicate signals.");
        queue.Enqueue(document.Duty, boss, owner, null, 999, 500, now);
        Check(queue.Resolve(document, document.Duty, frame, now, ActorByID, RuntimeName, plan).Length == 0,
            "Unknown action ID bypassed the official plan.");
        queue.Enqueue(document.Duty, boss, owner, null, 101, 500, now);
        Check(queue.Resolve(document, document.Duty, frame, now, ActorByID, RuntimeName, new GuideIdPlan(document with { Revision = 2 }, catalog)).Length == 0,
            "Action queue accepted another document's ID plan.");
        var typed = new GuideMechanic("Detonation", "Detonation: move away.", "")
        { Advice = Mechanic("Typed", Trigger("status", "Poison")).Advice };
        var typedBoss = Boss("Gardien", typed);
        Check(new GuideIdPlan(Document(typedBoss), catalog).Resolve(typedBoss, null, "cast", 101, 0, 0, false, 0).Mechanic == null,
            "Typed status mechanic fell through to its legacy cast name.");
    }
}
