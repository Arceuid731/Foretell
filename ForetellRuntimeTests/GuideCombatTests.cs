using BossMod;
using BossMod.Foretell;
using System.IO.Compression;
using System.Numerics;

internal static class GuideCombatTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static void Run()
    {
        HeadingBossIdentity();
        ChecklistPresentation();
        CentralSignalSelection();
        OwnedStatusSignals();
        DeferredActionSignals();
        StatusAnnotationTargets();
        GuidePhaseTests.Run();
        var now = DateTime.UtcNow;
        var first = new GuideBoss("First", "", [new("", "", [new("Blast", "A circular AoE around the boss.", ""), new("Pulse", "Raidwide damage.", "")])]);
        var second = new GuideBoss("Second", "", [new("", "", [new("Blast", "A tankbuster.", "")])]);
        var document = new GuideDocument(GuideDocument.CurrentSchema, new(1, 2, "Test"), "Test", 1, now, GuideNames.Hash("test"), [first, second]);
        var tracker = new GuideEncounterTracker();
        Check(tracker.Update(document, [], false) is { Boss.Name: "First", Upcoming: true }, "Entry checklist is not the upcoming boss");
        var firstActor = new GuideActorState(10, 20, 30, "First", false, true, 10);
        var secondActor = new GuideActorState(11, 21, 31, "Second", false, false, 40);
        Check(tracker.Update(document, [firstActor, secondActor], false) is { Boss.Name: "First", Upcoming: false }, "Checklist leaked another boss");
        var signal = new GuideSignal(first, first.Phases[0], first.Phases[0].Mechanics[0], GuideSignalKind.Cast, 10, 20, 30, 40, 0, now.AddSeconds(5), GuidanceKind.Avoid, "test evidence");
        tracker.Synchronize([signal, signal with { Boss = second }]);
        Check(tracker.Frame.Active.Length == 1 && tracker.Frame.Phase == first.Phases[0], "Signal frame contains another boss");
        tracker.Resolve(signal);
        Check(tracker.Resolved(first, signal.Phase, signal.Mechanic), "Action resolution was not checked");
        tracker.Wipe();
        Check(tracker.Frame is { Upcoming: true, Active.Length: 0 } && !tracker.Resolved(first, signal.Phase, signal.Mechanic), "Wipe retained highlights or resolved state");
        Check(tracker.Update(document, [firstActor], false).Upcoming, "Stale combat state re-armed the wiped pull");
        tracker.Update(document, [firstActor with { Engaged = false }], false);
        tracker.Update(document, [firstActor], false);
        Check(tracker.Update(document, [firstActor with { Dead = true, Engaged = false }, secondActor], false) is { Boss.Name: "Second", Upcoming: true, CompletedBosses: 1 }, "Boss death did not advance upcoming checklist");
        Check(tracker.Update(document, [secondActor with { Engaged = true }], false) is { Boss.Name: "Second", Upcoming: false }, "Second boss did not become current");
        Check(tracker.Update(document, [firstActor, secondActor with { Engaged = true }], false) is { Ambiguous: true, Active.Length: 0, Boss: null }, "Simultaneous boss ambiguity was silently resolved");
        tracker.Reset();
        Check(tracker.Update(document, [secondActor with { Engaged = true }], false) is { Boss.Name: "Second" }, "Join in progress incorrectly pinned the first boss");
        tracker.Reset();
        tracker.Update(document, [firstActor], false);
        Check(tracker.Update(document, [], false) is { Boss.Name: "First", CompletedBosses: 0 }, "Despawn was guessed to mean death");
        Check(GuideRules.LiveGuidance(first.Phases[0].Mechanics[0], first.Phases[0]) == GuidanceKind.Avoid, "Guide AoE preparation missing");
        foreach (var text in new[] { "Raidwide damage, unless protected.", "Do not stack. A stack marker appears.", "Raidwide damage. Then spread out.", "Turn away only if marked.", "A tankbuster followed by a raidwide.", "A gaze attack that doesn't require you to look away.", "Never spread out." })
            Check(GuideRules.LiveGuidance(new("Test", text, ""), first.Phases[0]) == GuidanceKind.None, "Conditional/sequence became unconditional: " + text);
        Check(GuideRules.LiveGuidance(new("Test", "Raidwide damage.", ""), new("Phase 2", "If protected, the response changes.", [])) == GuidanceKind.None, "Phase condition ignored");
        var conditionedBoss = new GuideBoss("Conditional", "", [new("", "If marked, the following responses change.", []), first.Phases[0] with { Name = "Abilities" }]);
        Check(GuideRules.LiveGuidance(first.Phases[0].Mechanics[0], conditionedBoss.Phases[1], conditionedBoss) == GuidanceKind.None, "Boss introductory conditions were detached from abilities");
        var statusMechanic = new GuideMechanic("Judgment", "If you have Doom, spread out.", "");
        Check(GuideRules.LiveGuidance(statusMechanic, first.Phases[0]) == GuidanceKind.None, "Status requirement collapsed into a cast instruction");
        Check(GuideRules.StatusGuidance(statusMechanic, first.Phases[0], "Doom") == GuidanceKind.Spread
            && GuideRules.StatusGuidance(statusMechanic, first.Phases[0], "Doom II") == GuidanceKind.None, "Status condition ignored exact observed status name");
        var owner = new Actor(10, 20, 0, 0, "First", 30, ActorType.Enemy, Class.None, 1, Vector4.Zero, resolveGameMetadata: false);
        var helper = new Actor(12, 22, 0, 0, "Helper", 32, ActorType.Helper, Class.None, 1, Vector4.Zero, ownerID: 10, resolveGameMetadata: false)
        { CastInfo = new() { Action = new(ActionType.Spell, 40), TotalTime = 5 } };
        string Name(string sheet, uint id) => sheet == "BNpcName" && id == 30 ? "First" : sheet == "Action" && id == 40 ? "Blast" : "";
        Check(ForetellEngine.MatchOwnedGuideActor(document, document.Duty, helper, owner, now, Name)?.Boss == first, "Explicit boss-owned helper was not matched");
        helper.OwnerID = 99;
        Check(ForetellEngine.MatchOwnedGuideActor(document, document.Duty, helper, owner, now, Name) == null, "Unrelated helper inherited a boss guide");
        var prediction = new ActivePrediction(10, 40, GeometryKind.Circle, MechanicKind.GroundAOE, Vector2.Zero, Vector2.Zero, 0, 8, 0, signal.Until, .94f, "Action sheet", Guidance: GuidanceKind.Avoid);
        var hazard = new DecisionHazard(5, prediction, signal.Until.AddSeconds(1), true, false, "Client geometry");
        var linked = GuideDecisionBridge.Associate(hazard, signal, "Translated Blast", document.SourceUrl);
        Check(linked.Prediction.GuideLinked && linked.Prediction.Label == "Translated Blast" && linked.Prediction.P1 == 8 && linked.Prediction.Confidence == prediction.Confidence, "Guide altered geometry or boosted confidence");
        Check(GuideDecisionBridge.Associate(hazard, signal with { SourceID = 11 }, "Wrong", "source") == hazard, "Guide linked another caster");
        Check(GuideDecisionBridge.Associate(hazard, signal with { Until = now.AddSeconds(7) }, "Wrong", "source") == hazard, "Guide linked another cast occurrence");
        var stack = GuideDecisionBridge.Associate(hazard, signal with { Guidance = GuidanceKind.Stack }, "Stack", "source");
        Check(stack.Prediction.Guidance == GuidanceKind.Avoid, "An arbitrary cast target became a stack anchor");
        var raidwide = GuideDecisionBridge.Associate(hazard, signal with { Guidance = GuidanceKind.Raidwide }, "Pulse", "source");
        Check(raidwide.AdvisoryOnly && raidwide.Prediction.Guidance == GuidanceKind.Raidwide, "Guide semantics lost advisory provenance");
        Check(GuideSummaryValidation.Accept("Écarte-toi si tu portes le marqueur.", "If marked, spread out."), "Bounded summary rejected");
        Check(!GuideSummaryValidation.Accept("Move 30 yalms away.", "Move away."), "Invented numerical instruction accepted");
        Check(!GuideSummaryValidation.Accept("Open https://example.invalid", "Source"), "Summary can introduce remote instructions");
        Check(!GuideSummaryValidation.Accept("Move 30 yalms away.", "The hit deals 300 damage."), "Numeric substring accepted as source grounding");
        Check(!GuideSummaryValidation.LanguageAndConditions("Partage le coup.", "If marked, share the hit instead.", GuideLanguage.French), "Dropped condition/alternative was accepted");
        var annotation = GuideDecisionBridge.Annotation(signal, Vector2.Zero, "Blast", document.SourceUrl);
        Check(!annotation.SpatiallyKnown && annotation.AdvisoryOnly && annotation.Prediction.Geometry == GeometryKind.Unknown
            && annotation.Prediction.P1 == 0 && !ForetellDecisionCore.Contains(annotation.Prediction, Vector2.Zero, signal.Until), "Signal annotation fabricated an AoE");
        var directory = Path.Combine(Path.GetTempPath(), "foretell-guide-combat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var timing = new GuideTimingHistory(directory);
            Check(timing.Estimate == null, "First-run ETA was invented");
            timing.Record(2); timing.Record(4); timing.Record(double.NaN);
            Check(new GuideTimingHistory(directory).Estimate == 4, "Measured preparation times not persisted");
            var zipPath = Path.Combine(directory, "bad.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) zip.CreateEntry("../escape.exe");
            try { ForetellGuideLocalModel.ExtractRuntime(zipPath, Path.Combine(directory, "runtime")); throw new Exception("Zip traversal accepted"); }
            catch (InvalidDataException) { }
            var validZip = Path.Combine(directory, "valid.zip");
            using (var zip = ZipFile.Open(validZip, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("llama-server.exe").Open())) writer.Write("fixture runtime");
            var installed = ForetellGuideLocalModel.ExtractRuntime(validZip, Path.Combine(directory, "installed"));
            using (var locked = new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.Read))
                Check(ForetellGuideLocalModel.ExtractRuntime(validZip, Path.Combine(directory, "installed")) == installed,
                    "Starting another analysis tried to overwrite an unchanged in-use runtime");
            File.WriteAllText(installed, "corrupt runtime");
            ForetellGuideLocalModel.ExtractRuntime(validZip, Path.Combine(directory, "installed"));
            Check(File.ReadAllText(installed) == "fixture runtime", "Corrupted runtime was not repaired");
        }
        finally { Directory.Delete(directory, true); }
        SummaryWorker().GetAwaiter().GetResult();
        GuideModelStateTests.Run();
        Console.WriteLine("Guide boss lifecycle, synchronized decision bridge, conditional rules, summary guardrails, measured ETA and archive safety passed.");
    }

    private static void HeadingBossIdentity()
    {
        var now = DateTime.UtcNow;
        var boss = new GuideBoss("Auspice: Suzaku", "", [new("", "", [new("Pulse", "Raidwide damage.", "")])]);
        var document = new GuideDocument(GuideDocument.CurrentSchema, new(1, 2, "Test"), "Test", 1, now, GuideNames.Hash("heading-combat"), [boss]);
        var actor = new GuideActorState(10, 20, 30, "SUZAKU", false, true, 10);
        Check(new GuideEncounterTracker().Update(document, [actor], false) is { Boss.Name: "Auspice: Suzaku", Upcoming: false, Ambiguous: false },
            "A prepared boss title prevented live encounter engagement");
        foreach (var other in new[] { "Seiryu", "Auspice: Seiryu", "Suzaku (Extreme)", "Suzaku II" })
            Check(new GuideEncounterTracker().Update(document, [actor with { EnglishName = other }], false).Upcoming,
                "A different boss engaged the titled encounter: " + other);
        var duplicate = document with { Bosses = [boss, boss with { Name = "Suzaku" }] };
        var tracker = new GuideEncounterTracker();
        Check(tracker.Update(duplicate, [actor], false).Upcoming, "Colliding titles silently selected an engaged boss");
        tracker.Synchronize([new(boss, boss.Phases[0], boss.Phases[0].Mechanics[0], GuideSignalKind.Cast,
            10, 20, 30, 40, 0, now.AddSeconds(5), GuidanceKind.Raidwide, "fixture")]);
        Check(tracker.Frame.Active.Length == 0, "Colliding titles produced a live signal");
    }

    private static (GuideDocument Document, GuideBoss Boss, Actor Owner, Actor Helper, Actor Player, DateTime Now) SignalFixture()
    {
        var now = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        var doom = new GuideMechanic("Doom", "If you have Doom, spread out.", "")
        { Advice = new(GuideLanguage.English, "Doom", "Spread out", "Spread while afflicted with Doom.", "status", "Doom", ["If you have Doom, spread out."]) };
        var boss = new GuideBoss("First", "", [new("", "", [new("Blast", "A circular AoE around the boss.", ""), doom])]);
        var document = new GuideDocument(GuideDocument.CurrentSchema, new(1, 2, "Test"), "Test", 1, now, GuideNames.Hash("signal-fixture"), [boss]);
        var owner = new Actor(10, 20, 0, 0, "First", 30, ActorType.Enemy, Class.None, 1, Vector4.Zero, resolveGameMetadata: false);
        var helper = new Actor(12, 22, 0, 0, "Helper", 32, ActorType.Helper, Class.None, 1, Vector4.Zero, ownerID: 10, resolveGameMetadata: false);
        var player = new Actor(1, 1, 0, 0, "Player", 1, ActorType.Player, Class.PCT, 100, new(10, 0, 20, 0), resolveGameMetadata: false);
        return (document, boss, owner, helper, player, now);
    }

    private static string SignalName(string sheet, uint id) => (sheet, id) switch
    {
        ("BNpcName", 30) => "First",
        ("BNpcName", 32) => "Helper",
        ("Action", 40) => "Blast",
        ("Status", 60) => "Doom",
        _ => ""
    };

    private static void CentralSignalSelection()
    {
        var fixture = SignalFixture();
        var phase = fixture.Boss.Phases[0];
        var first = new GuideSignal(fixture.Boss, phase, phase.Mechanics[0], GuideSignalKind.Cast, 10, 20, 30, 40, 0, fixture.Now.AddSeconds(3), GuidanceKind.Avoid, "fixture");
        var second = first with { SourceID = 12, ID = 41, Until = first.Until.AddSeconds(1) };
        var third = first with { SourceID = 13, ID = 42, Until = first.Until.AddSeconds(2) };
        var displayed = GuideCentralPresentation.Select([third, second, first], fixture.Player.InstanceID);
        Check(displayed.SequenceEqual([first, second]), "Central alert selection lost its two-signal limit or time ordering");
        var prediction = new ActivePrediction(first.SourceID, first.ID, GeometryKind.Circle, MechanicKind.GroundAOE,
            Vector2.Zero, Vector2.Zero, 0, 8, 0, first.Until, .95f, "fixture", Guidance: GuidanceKind.Avoid);
        Check(GuideCentralPresentation.Owns(prediction, displayed), "A displayed guide alert no longer suppresses its duplicate prediction");
        Check(!GuideCentralPresentation.Owns(prediction with { CasterID = third.SourceID, ActionID = third.ID, Activation = third.Until }, displayed),
            "The third simultaneous cast lost its fallback alert without being displayed");
        Check(!GuideCentralPresentation.Owns(prediction with { Activation = first.Until.AddSeconds(2) }, displayed)
            && !GuideCentralPresentation.Owns(prediction with { CasterID = 99 }, displayed), "Central suppression crossed cast occurrence or caster identity");
        var status = first with { Kind = GuideSignalKind.Status, ID = 60, TargetID = fixture.Player.InstanceID, Until = fixture.Now.AddSeconds(20) };
        displayed = GuideCentralPresentation.Select([third, second, first, status], fixture.Player.InstanceID);
        Check(displayed.SequenceEqual([status, first])
            && !GuideCentralPresentation.Owns(prediction with { CasterID = second.SourceID, ActionID = second.ID, Activation = second.Until }, displayed),
            "A personal status displaced a cast while still suppressing its fallback alert");
        Check(GuideCentralPresentation.Select([first], 0).Length == 0 && !GuideCentralPresentation.Owns(prediction, []),
            "Missing player or disabled guide display still owns predictions");
    }

    private static void OwnedStatusSignals()
    {
        var fixture = SignalFixture();
        var status = new ActorStatus(60, 0, fixture.Now.AddSeconds(10), fixture.Helper.InstanceID);
        GuideSignal? Match(Actor? owner) => GuideSignalMatching.Status(fixture.Boss, fixture.Player, status, fixture.Helper, owner, fixture.Now, SignalName);
        var matched = Match(fixture.Owner);
        Check(matched is { Kind: GuideSignalKind.Status, ID: 60, Guidance: GuidanceKind.Spread } && matched.SourceID == fixture.Helper.InstanceID
            && matched.OwnerID == fixture.Owner.InstanceID && matched.TargetID == fixture.Player.InstanceID, "Boss-owned helper status was not matched to its actual source and player target");
        Check(Match(null) == null, "A helper status matched without its owner");
        fixture.Helper.OwnerID = 99;
        Check(Match(fixture.Owner) == null, "An unrelated helper inherited the current boss status");
        fixture.Helper.OwnerID = fixture.Owner.InstanceID;
        fixture.Owner.IsAlly = true;
        Check(Match(fixture.Owner) == null, "An allied owner supplied an enemy guide status");
        fixture.Owner.IsAlly = false;
        fixture.Owner.IsDead = true;
        Check(Match(fixture.Owner) == null, "A dead owner supplied an enemy guide status");
        fixture.Owner.IsDead = false;
        Check(GuideSignalMatching.Status(fixture.Boss, fixture.Player, status, fixture.Helper, fixture.Owner, fixture.Now,
            (sheet, id) => sheet == "Status" ? "Doom II" : SignalName(sheet, id)) == null, "Status trigger matching accepted a partial name");
        Check(GuideSignalMatching.Status(fixture.Boss, fixture.Player, status, fixture.Helper, fixture.Owner, status.ExpireAt, SignalName) == null,
            "An expired helper status stayed active");
        var directStatus = new ActorStatus(60, 0, status.ExpireAt, fixture.Owner.InstanceID);
        Check(GuideSignalMatching.Status(fixture.Boss, fixture.Player, directStatus, fixture.Owner, null, fixture.Now, SignalName) is { OwnerID: 0 },
            "Direct boss status matching regressed");
        Check(GuideSignalMatching.Status(fixture.Boss, fixture.Player, directStatus, fixture.Helper, fixture.Owner, fixture.Now, SignalName) == null,
            "A status with a different source ID matched the helper");
        var duplicateBoss = fixture.Boss with { Phases = [new("", "", [fixture.Boss.Phases[0].Mechanics[1], fixture.Boss.Phases[0].Mechanics[1]])] };
        Check(GuideSignalMatching.Status(duplicateBoss, fixture.Player, status, fixture.Helper, fixture.Owner, fixture.Now, SignalName) == null,
            "Ambiguous status triggers selected an arbitrary mechanic");
    }

    private static void DeferredActionSignals()
    {
        var fixture = SignalFixture();
        var frame = new GuideCombatFrame(fixture.Boss, false, false, null, [], 0);
        var queue = new GuideActionQueue();
        Actor? ActorByID(ulong id) => id == fixture.Owner.InstanceID ? fixture.Owner : id == fixture.Helper.InstanceID ? fixture.Helper : null;
        void Enqueue() => queue.Enqueue(fixture.Document.Duty, fixture.Boss, fixture.Helper, fixture.Owner, 40, fixture.Player.InstanceID, fixture.Now);
        var budget = 0;
        string BudgetName(string sheet, uint id)
        {
            if (budget <= 0) return "";
            --budget;
            return SignalName(sheet, id);
        }
        Enqueue();
        Check(queue.Resolve(fixture.Document, fixture.Document.Duty, frame, fixture.Now, ActorByID, BudgetName).Length == 0 && queue.Count == 1,
            "Name budget exhaustion permanently discarded an ActionEffect");
        budget = 2;
        var resolved = queue.Resolve(fixture.Document, fixture.Document.Duty, frame, fixture.Now.AddMilliseconds(200), ActorByID, BudgetName);
        Check(resolved is [{ Kind: GuideSignalKind.Action, ID: 40 } action] && action.SourceID == fixture.Helper.InstanceID
            && action.OwnerID == fixture.Owner.InstanceID && action.TargetID == fixture.Player.InstanceID && action.Until == fixture.Now.AddSeconds(4),
            "Deferred ActionEffect lost source, owner, target or original expiry");
        Check(queue.Count == 0 && queue.Resolve(fixture.Document, fixture.Document.Duty, frame, fixture.Now.AddSeconds(1), ActorByID, SignalName).Length == 0,
            "A resolved ActionEffect was emitted twice");
        Enqueue();
        Check(queue.Resolve(fixture.Document, fixture.Document.Duty, frame, fixture.Now.AddSeconds(4), ActorByID, SignalName).Length == 0 && queue.Count == 0,
            "Deferred name resolution revived an expired action");
        foreach (var invalidFrame in new[] { frame with { Upcoming = true }, frame with { Ambiguous = true }, frame with { Boss = fixture.Boss with { Name = "Second" } } })
        {
            Enqueue();
            Check(queue.Resolve(fixture.Document, fixture.Document.Duty, invalidFrame, fixture.Now, ActorByID, SignalName).Length == 0 && queue.Count == 0,
                "Pending actions survived a wipe, ambiguity or boss change");
        }
        Enqueue();
        var otherDuty = new GuideDuty(3, 4, "Other");
        Check(queue.Resolve(fixture.Document with { Duty = otherDuty }, otherDuty, frame, fixture.Now, ActorByID, SignalName).Length == 0 && queue.Count == 0,
            "Pending actions crossed duty identity");
        Enqueue();
        fixture.Helper.OwnerID = 99;
        Check(queue.Resolve(fixture.Document, fixture.Document.Duty, frame, fixture.Now, ActorByID, SignalName).Length == 0 && queue.Count == 0,
            "Pending helper action survived changed ownership");
        fixture.Helper.OwnerID = fixture.Owner.InstanceID;
        Enqueue();
        ++fixture.Owner.NameID;
        Check(queue.Resolve(fixture.Document, fixture.Document.Duty, frame, fixture.Now, ActorByID, SignalName).Length == 0 && queue.Count == 0,
            "Pending helper action inherited a replacement owner");
        --fixture.Owner.NameID;
        Enqueue();
        ++fixture.Helper.OID;
        Check(queue.Resolve(fixture.Document, fixture.Document.Duty, frame, fixture.Now, ActorByID, SignalName).Length == 0 && queue.Count == 0,
            "Pending action inherited a replacement source actor");
        --fixture.Helper.OID;
        Enqueue();
        Check(queue.Resolve(fixture.Document, fixture.Document.Duty, frame, fixture.Now, ActorByID,
            (sheet, id) => sheet == "Action" ? "Blast II" : SignalName(sheet, id)).Length == 0 && queue.Count == 0, "Deferred action matching guessed a partial ability name");
        for (uint actionID = 1; actionID <= 40; ++actionID)
            queue.Enqueue(fixture.Document.Duty, fixture.Boss, fixture.Helper, fixture.Owner, actionID, fixture.Player.InstanceID, fixture.Now);
        Check(queue.Count == 32, "Deferred action queue is unbounded");
        queue.Clear();
        Check(queue.Count == 0, "Context reset retained pending actions");
    }

    private static void StatusAnnotationTargets()
    {
        var fixture = SignalFixture();
        var phase = fixture.Boss.Phases[0];
        var signal = new GuideSignal(fixture.Boss, phase, phase.Mechanics[1], GuideSignalKind.Status, fixture.Helper.InstanceID,
            fixture.Helper.OID, fixture.Helper.NameID, 60, fixture.Player.InstanceID, fixture.Now.AddSeconds(10), GuidanceKind.Spread, "fixture");
        var position = new Vector2(10, 20);
        var annotation = GuideDecisionBridge.Annotation(signal, Vector2.Zero, "Doom", fixture.Document.SourceUrl, position);
        Check(annotation.Prediction.TargetID == fixture.Player.InstanceID && annotation.Prediction.Origin == position && annotation.Prediction.Target == position
            && annotation.Prediction.CasterID == fixture.Helper.InstanceID, "Status annotation points its radar/world marker at the boss instead of the player");
        Check(annotation is { SpatiallyKnown: false, AdvisoryOnly: true } && annotation.Prediction.Geometry == GeometryKind.Unknown
            && annotation.Prediction.P1 == 0 && !ForetellDecisionCore.Contains(annotation.Prediction, position, signal.Until), "Status target annotation fabricated an AoE footprint");
        var cast = GuideDecisionBridge.Annotation(signal with { Kind = GuideSignalKind.Cast }, Vector2.Zero, "Blast", fixture.Document.SourceUrl, position);
        Check(cast.Prediction.TargetID == signal.SourceID && cast.Prediction.Origin == Vector2.Zero, "An arbitrary cast target became a guide marker target");
        var invalid = GuideDecisionBridge.Annotation(signal, Vector2.Zero, "Doom", fixture.Document.SourceUrl, new(float.NaN, 20));
        Check(invalid.Prediction.TargetID == signal.SourceID && ForetellDecisionCore.Valid(invalid.Prediction), "Invalid status target coordinates reached the renderer");
    }

    private static void ChecklistPresentation()
    {
        var phase = new GuidePhase("", "", []);
        Check(GuideRules.LiveGuidance(new("Breath", "A cone that deals damage and inflicts Poison.", ""), phase) == GuidanceKind.Avoid, "Plain-language cone was not prepared");
        Check(GuideRules.LiveGuidance(new("Pulse", "Party-wide damage.", ""), phase) == GuidanceKind.Raidwide, "Party-wide damage was not prepared");
        Check(GuideRules.LiveGuidance(new("Breath", "A cone attack. Do not move out if marked.", ""), phase) == GuidanceKind.None, "Plain-language cone lost its condition");
        Check(GuideRules.LiveGuidance(new("Pulse", "Party-wide damage only if the shield breaks.", ""), phase) == GuidanceKind.None, "Party-wide damage lost its condition");
        Check(GuideRules.LiveGuidance(new("Tower", "Soak the tower to avoid party-wide damage.", ""), phase) == GuidanceKind.Soak, "Damage consequence took precedence over the tower response");
        Check(GuideRules.LiveGuidance(new("Add", "Kill the add to avoid party-wide damage.", ""), phase) == GuidanceKind.None, "Avoidable damage consequence became a raidwide instruction");
        Check(GuideChecklistPresentation.Instruction(GuidanceKind.Avoid, GuideLanguage.French) == "ÉVITE LA ZONE", "Checklist lacks a short action");
        Check(GuideChecklistPresentation.Instruction(GuidanceKind.Avoid, GuideLanguage.French, false) == "SURVEILLE LA ZONE", "Unconfirmed spatial instruction became a movement order");
        Check(GuideChecklistPresentation.Instruction(GuidanceKind.Stack, GuideLanguage.French, false) == "CIBLE À CONFIRMER", "Missing group target became a stack order");
        Check(GuideChecklistPresentation.Instruction(GuidanceKind.None, GuideLanguage.French) == "À VÉRIFIER", "Unresolved response became actionable");
        foreach (var language in Enum.GetValues<GuideLanguage>())
            foreach (var guidance in Enum.GetValues<GuidanceKind>())
            {
                var instruction = GuideChecklistPresentation.Instruction(guidance, language);
                Check(instruction.Length is > 0 and <= 28 && !instruction.Contains('\n'), "Instruction is no longer a single short line");
            }
        Check(GuideChecklistPresentation.Fit("Mechanic", 8, value => value.Length) == "Mechanic", "Fitting name was truncated");
        Check(GuideChecklistPresentation.Fit("Mechanic", 5, value => value.Length) == "Mech…", "Long name is not bounded");
        Check(GuideChecklistPresentation.Fit("Mechanic", 0, value => value.Length) == "", "Tiny viewport overflows");
        Check(GuideChecklistPresentation.Fit("é👩‍🚀abcdef", 3, value => System.Globalization.StringInfo.ParseCombiningCharacters(value).Length) == "é👩‍🚀…", "Name truncation split a Unicode grapheme");
        Check(GuideChecklistPresentation.Preview(string.Join(" ", Enumerable.Repeat("description", 90))).Length <= 320, "Hover description is no longer compact");
        Console.WriteLine("Compact guide cues, spatial/target abstention and Unicode layout passed.");
    }

    private static async Task WaitFor(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(5, timeout.Token);
    }

    private static async Task SummaryWorker()
    {
        var directory = Path.Combine(Path.GetTempPath(), "foretell-summary-tests-" + Guid.NewGuid().ToString("N"));
        var document = new GuideDocument(1, new(1, 2, "Test"), "Test", 1, DateTime.UtcNow, GuideNames.Hash("summary-test"),
            [new("Boss", "", [new("", "", [new("Hit", "Tankbuster.", "")]), new("Strategy", "Stand near the center.", [])])]);
        try
        {
            var fake = new FakeModel();
            using (var service = new ForetellGuideSummaries(directory, _ => fake))
            {
                service.Update(document, GuideLanguage.French, true, true, false, "Boss");
                await WaitFor(() => service.Snapshot?.Stage == "PausedInCombat");
                Check(fake.Started == 0, "Model ran in combat");
                Check(service.Runtime is { Stage: GuideModelStage.Unloaded, ProcessID: null }, "Queued/paused work reports a loaded model");
                service.Update(document, GuideLanguage.French, true, false, false, "Boss");
                await WaitFor(() => service.Snapshot?.Stage == "Ready");
                Check(service.Snapshot?.Summaries.Count == 2, "Progressive local summary or context-only section missing");
                Check(service.Runtime is { Stage: GuideModelStage.Unloaded, ProcessID: null }, "Finished preparation kept model loaded");
                service.Update(null, GuideLanguage.French, false, false, false, "");
                Check(service.Snapshot == null, "Disabled summaries remained attached to a duty");
                service.Dispose();
                await service.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            }
            using (var service = new ForetellGuideSummaries(directory, _ => throw new Exception("Cached summary initialized model")))
            {
                service.Update(document, GuideLanguage.French, true, true, false, "Boss");
                await WaitFor(() => service.Snapshot?.Stage == "Ready");
                Check(service.Snapshot?.Summaries.Count == 2, "Prepared summary unavailable offline/in combat");
                Check(service.Runtime is { Stage: GuideModelStage.Unloaded, ProcessID: null }, "Cache-ready confused with model-loaded");
            }
            var blocked = new FakeModel { Block = true };
            var changed = document with { SourceHash = GuideNames.Hash("new-revision") };
            using (var service = new ForetellGuideSummaries(directory, _ => blocked))
            {
                service.Update(changed, GuideLanguage.French, true, false, false, "Boss");
                await WaitFor(() => service.Runtime.Stage == GuideModelStage.Generating);
                Check(service.Runtime.ProcessID == 123, "Active inference has no runtime process identity");
                service.Update(changed, GuideLanguage.French, true, true, false, "Boss");
                await WaitFor(() => service.Snapshot?.Stage == "PausedInCombat");
                Check(blocked.Canceled != 0, "Combat did not cancel active inference");
                Check(service.Runtime is { Stage: GuideModelStage.Unloaded, ProcessID: null }, "Combat pause did not clear process activity");
                service.Update(document, GuideLanguage.French, true, false, false, "Boss");
                await WaitFor(() => service.Snapshot?.SourceHash == document.SourceHash && service.Snapshot.Stage == "Ready");
                service.Dispose();
                await service.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            }
            var narrative = document with
            {
                Duty = new(3, 4, "Narrative guide"), Title = "Narrative guide", SourceHash = GuideNames.Hash("narrative-summary-test"),
                Bosses = [new("Narrative boss", "", [new("", "Stand near the center.", [])])]
            };
            using (var service = new ForetellGuideSummaries(directory, _ => new FakeModel()))
            {
                service.Update(narrative, GuideLanguage.French, true, false, false, "Narrative boss");
                await WaitFor(() => service.Snapshot?.Stage == "Ready");
                Check(service.Snapshot is { Total: 1, Completed: 1, Summaries.Count: 1 }
                    && service.Snapshot.Summaries.ContainsKey(ForetellGuideSummaries.ContextKey(narrative.Bosses[0], narrative.Bosses[0].Phases[0])),
                    "A wholly narrative guide did not prepare a source-bound context summary");
                service.Dispose();
                await service.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            }
            using (var service = new ForetellGuideSummaries(directory, _ => throw new Exception("Narrative cached summary initialized model")))
            {
                service.Update(narrative, GuideLanguage.French, true, true, false, "Narrative boss");
                await WaitFor(() => service.Snapshot?.Stage == "Ready");
                Check(service.Snapshot?.Summaries.Count == 1 && narrative.MechanicCount == 0, "Narrative summary lost offline/in combat or became a named mechanic");
            }
            var longSource = string.Concat(Enumerable.Repeat("Tankbuster. ", 2000));
            var longDocument = narrative with { SourceHash = GuideNames.Hash(longSource), Bosses = [new("Long boss", "", [new("", longSource, [])])] };
            var longModel = new FakeModel { Reject = true };
            using (var service = new ForetellGuideSummaries(directory, _ => longModel))
            {
                service.Update(longDocument, GuideLanguage.French, true, false, false, "Long boss");
                await WaitFor(() => service.Snapshot?.Stage == "ReadyWithUnresolved");
                Check(longModel.SourceLength == longSource.Length && longModel.SourceLength > 7000, "Worker skipped or clipped a long excerpt before the model tokenizer");
                Check(service.Snapshot?.LastIssue == "Synthetic context overflow" && service.Snapshot.Summaries.Count == 0, "Rejected excerpt disappeared without an explicit reason");
                longModel.Reject = false;
                service.Update(longDocument, GuideLanguage.French, true, false, false, "Long boss", 32768, 8);
                await WaitFor(() => service.Snapshot?.Stage == "Ready");
                Check(longModel.Started == 2 && service.Snapshot?.Summaries.Count == 1, "Changing context did not retry unresolved work");
                service.Dispose();
                await service.Completion.WaitAsync(TimeSpan.FromSeconds(2));
                Check(service.Runtime is { Stage: GuideModelStage.Unloaded, ProcessID: null }, "Disposed worker reports an active process");
            }
            var replacing = new FakeModel { Block = true };
            using (var service = new ForetellGuideSummaries(directory, _ => replacing))
            {
                service.Update(changed, GuideLanguage.French, true, false, false, "Boss", 8192);
                await WaitFor(() => service.Runtime.Stage == GuideModelStage.Generating);
                service.Update(changed, GuideLanguage.French, true, false, false, "Boss", 16384);
                await WaitFor(() => replacing.Canceled > 0 && replacing.Started >= 2 && service.Runtime.Stage == GuideModelStage.Generating);
                service.Update(null, GuideLanguage.French, false, false, false, "Boss");
                await WaitFor(() => service.Runtime is { Stage: GuideModelStage.Unloaded, ProcessID: null });
                Check(service.Snapshot == null, "Disabled worker left guide results attached");
                service.Dispose();
                await service.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class FakeModel : IGuideSummaryModel
    {
        private volatile GuideModelRuntime _runtime = new();
        public GuideModelRuntime Runtime => _runtime;
        public int Started;
        public int Canceled;
        public bool Block;
        public bool Reject;
        public int SourceLength;
        public Task Start(Action<GuideModelProgress> progress, CancellationToken cancellation)
        {
            _runtime = new(GuideModelStage.Loaded, 123, "Fake", GuideModelLimits.DefaultContext);
            Interlocked.Increment(ref Started);
            return Task.CompletedTask;
        }
        public async Task<string> Summarize(string source, GuideLanguage language, CancellationToken cancellation)
        {
            _runtime = _runtime with { Stage = GuideModelStage.Generating };
            SourceLength = source.Length;
            if (Reject) throw new InvalidDataException("Synthetic context overflow");
            if (Block)
            {
                try { await Task.Delay(Timeout.Infinite, cancellation); }
                catch (OperationCanceledException) { Interlocked.Increment(ref Canceled); throw; }
            }
            return "Coup puissant sur le tank.";
        }
        public void Dispose() { _runtime = _runtime with { Stage = GuideModelStage.Unloaded, ProcessID = null }; }
    }

    public static void ModelSmoke(string directory, bool gpu) => ModelSmokeAsync(directory, gpu).GetAwaiter().GetResult();

    private static async Task ModelSmokeAsync(string directory, bool gpu)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var model = new ForetellGuideLocalModel(directory, gpu);
        var last = DateTime.MinValue;
        var stage = "";
        await model.Start(progress =>
        {
            if (progress.Stage != stage || (DateTime.UtcNow - last).TotalSeconds > 5)
            { Console.WriteLine($"{progress.Stage}: {progress.Received}/{progress.Total} · ETA {progress.RemainingSeconds:F1}s"); last = DateTime.UtcNow; stage = progress.Stage; }
        }, cancellation.Token);
        foreach (var source in new[]
        {
            "If marked, spread out. If the shield is broken, share the hit instead. After the second hit, return to the group.",
            "A cone AoE in front of the boss. Stand behind the boss. The next cone targets a random player.",
            "Players receive a status indicating their safe side. Face that side towards the boss before the shot. Do not assume every player has the same safe side."
        })
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var summary = await model.Summarize(source, GuideLanguage.French, cancellation.Token);
            Console.WriteLine($"SUMMARY {watch.Elapsed.TotalSeconds:F2}s: {summary}");
        }
    }

    public static void PipelineSmoke(string directory) => PipelineSmokeAsync(directory).GetAwaiter().GetResult();

    private static async Task PipelineSmokeAsync(string directory)
    {
        var texts = new[]
        {
            "If marked, spread out. If the shield is broken, share the hit instead. After the second hit, return to the group.",
            "A cone AoE in front of the boss. Stand behind the boss. The next cone targets a random player.",
            "Players receive a status indicating their safe side. Face that side towards the boss before the shot. Do not assume every player has the same safe side."
        };
        var document = new GuideDocument(1, new(9999, 9999, "Synthetic guide pipeline"), "Synthetic guide pipeline", 1, DateTime.UtcNow,
            GuideNames.Hash("pipeline:" + string.Join("\n", texts)), [new("Sentinel", "", [new("", "", texts.Select((source, index) => new GuideMechanic("Probe " + index, source, "")).ToArray())])]);
        var cacheDirectory = Path.Combine(directory, "pipeline-" + Guid.NewGuid().ToString("N"));
        var model = new ForetellGuideLocalModel(directory, true);
        using (var service = new ForetellGuideSummaries(cacheDirectory, _ => model))
        {
            service.Update(document, GuideLanguage.French, true, false, true, "Sentinel");
            await WaitFor(() => service.Snapshot?.Stage == "Summarizing");
            var processID = model.ProcessID ?? throw new Exception("No bounded model process");
            service.Update(document, GuideLanguage.French, true, true, true, "Sentinel");
            await WaitFor(() => service.Snapshot?.Stage == "PausedInCombat");
            await WaitFor(() =>
            {
                try { using var process = System.Diagnostics.Process.GetProcessById(processID); return process.HasExited; }
                catch (ArgumentException) { return true; }
            });
            Console.WriteLine("Actual model process stopped on combat; partial cache retained.");
            service.Update(document, GuideLanguage.French, true, false, true, "Sentinel");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (service.Snapshot?.Stage is not ("Ready" or "ReadyWithUnresolved"))
            {
                if (service.Snapshot?.Stage.StartsWith("Unavailable", StringComparison.Ordinal) == true) throw new Exception(service.Snapshot.Stage);
                await Task.Delay(50, deadline.Token);
            }
            Check(service.Snapshot.Summaries.Count == 3, "Real summary pipeline left a probe unresolved");
            foreach (var summary in service.Snapshot.Summaries.Values) Console.WriteLine("PIPELINE FR: " + summary);
            service.Dispose();
            await service.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
        using (var offline = new ForetellGuideSummaries(cacheDirectory, _ => throw new Exception("Offline pipeline attempted model startup")))
        {
            offline.Update(document, GuideLanguage.French, true, true, true, "Sentinel");
            await WaitFor(() => offline.Snapshot?.Stage == "Ready");
            Check(offline.Snapshot?.Summaries.Count == 3, "Actual prepared cache failed offline");
            Console.WriteLine("Actual pipeline cache reloaded in combat without model/network.");
        }
    }
}
