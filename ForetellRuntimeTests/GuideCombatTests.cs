using BossMod;
using BossMod.Foretell;
using System.IO.Compression;
using System.Numerics;

internal static class GuideCombatTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static void Run()
    {
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
        }
        finally { Directory.Delete(directory, true); }
        SummaryWorker().GetAwaiter().GetResult();
        Console.WriteLine("Guide boss lifecycle, synchronized decision bridge, conditional rules, summary guardrails, measured ETA and archive safety passed.");
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
                service.Update(document, GuideLanguage.French, true, false, false, "Boss");
                await WaitFor(() => service.Snapshot?.Stage == "Ready");
                Check(service.Snapshot?.Summaries.Count == 2, "Progressive local summary or context-only section missing");
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
            }
            var blocked = new FakeModel { Block = true };
            var changed = document with { SourceHash = GuideNames.Hash("new-revision") };
            using (var service = new ForetellGuideSummaries(directory, _ => blocked))
            {
                service.Update(changed, GuideLanguage.French, true, false, false, "Boss");
                await WaitFor(() => blocked.Started != 0);
                service.Update(changed, GuideLanguage.French, true, true, false, "Boss");
                await WaitFor(() => service.Snapshot?.Stage == "PausedInCombat");
                Check(blocked.Canceled != 0, "Combat did not cancel active inference");
                service.Update(document, GuideLanguage.French, true, false, false, "Boss");
                await WaitFor(() => service.Snapshot?.SourceHash == document.SourceHash && service.Snapshot.Stage == "Ready");
                service.Dispose();
                await service.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class FakeModel : IGuideSummaryModel
    {
        public int Started;
        public int Canceled;
        public bool Block;
        public Task Start(Action<GuideModelProgress> progress, CancellationToken cancellation) { Interlocked.Increment(ref Started); return Task.CompletedTask; }
        public async Task<string> Summarize(string source, GuideLanguage language, CancellationToken cancellation)
        {
            if (Block)
            {
                try { await Task.Delay(Timeout.Infinite, cancellation); }
                catch (OperationCanceledException) { Interlocked.Increment(ref Canceled); throw; }
            }
            return "Coup puissant sur le tank.";
        }
        public void Dispose() { }
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
