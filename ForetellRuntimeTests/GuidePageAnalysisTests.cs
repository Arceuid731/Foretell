using BossMod.Foretell;
using System.Text.Json;

internal static class GuidePageAnalysisTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static readonly GuideDuty Duty = new(321, 654, "Unstructured trial");
    private const string Html = "<main><p>Sentinel</p><p>Hammer: Heavy damage to the tank.</p><p>Pulse: Everyone takes damage.</p><div>END OF GUIDE</div></main>";
    private static GuideDocument Source() => ForetellGuideParser.ReadPage(JsonSerializer.Serialize(new { parse = new { title = Duty.EnglishName, revid = 5, text = Html } }), Duty, DateTime.UtcNow);

    public static void Run() => Verify().GetAwaiter().GetResult();

    private static async Task Verify()
    {
        var source = Source();
        Check(source.Bosses.Length == 0 && source.Page!.Text.Contains("END OF GUIDE") && source.Page.Html == Html, "Downloader performed semantic cutting or lost the complete page");
        Check(GuideModelCatalog.Profiles.Select(profile => profile.ID).Distinct().Count() == 3 && GuideModelCatalog.Profiles.All(profile => profile.Asset.Hash.Length == 64 && profile.MaximumContext >= 32768), "Model catalog lacks three pinned profiles");
        var profile = GuideModelCatalog.Get(GuideModelCatalog.DefaultID);
        await GuideAnalysisReviewTests.Run();
        VerifyEvidenceAndConditions(source, profile);
        await VerifyDuskVigilSources(profile);
        using var model = new PageModel();
        GuideDocument? partial = null;
        var prepared = await GuidePageAnalysis.Compile(source, GuideLanguage.French, profile, 32768, model, (_, _) => { }, CancellationToken.None, ready => partial = ready);
        Check(partial?.MechanicCount == 2 && !GuidePageAnalysis.ValidPrepared(partial, source, GuideLanguage.French, profile), "Partial boss output was absent or accepted as a complete cache");
        var memory = new GuideAnalysisMemory();
        using (var resumed = new GuideResumableModel(new PageModel(), memory))
            await GuidePageAnalysis.Compile(source, GuideLanguage.French, profile, 32768, resumed, (_, _) => { }, CancellationToken.None);
        using var replacementModel = new PageModel();
        using (var resumed = new GuideResumableModel(replacementModel, memory))
            await GuidePageAnalysis.Compile(source, GuideLanguage.French, profile, 32768, resumed, (_, _) => { }, CancellationToken.None);
        Check(replacementModel.Inputs.Count == 0, "Resuming after unloading repeated completed model calls");
        Check(ForetellEngine.GuideContentLanguage == GuideLanguage.English, "Guide content unexpectedly enables translation");
        Check(model.Inputs[0].Contains("END OF GUIDE") && model.Inputs[0].Contains("Heavy damage"), "The model did not receive the complete source before semantic grouping");
        using var splitModel = new PageModel { SplitLargeSource = true };
        var splitSource = source with { Page = source.Page! with { Text = source.Page.Text + "\n" + new string('x', 4000) + "\n" + source.Page.Text } };
        var splitPrepared = await GuidePageAnalysis.Compile(splitSource, GuideLanguage.French, profile, 32768, splitModel, (_, _) => { }, CancellationToken.None);
        Check(splitPrepared.Bosses.Length == 1 && splitPrepared.MechanicCount == 2 && splitPrepared.Coverage.Length > 1
            && splitModel.DetailCalls == 1, "Physical source partitions repeated boss analysis instead of validating the complete draft directly");
        Check(prepared.Bosses.Single().Name == "Sentinel" && prepared.MechanicCount == 2, "Model-selected boss/mechanic grouping lost an entry");
        var mechanics = prepared.Bosses.Single().Phases.Single().Mechanics;
        Check(mechanics[0].Advice!.Cue == "Tank : prépare ta mitigation" && mechanics[1].Advice!.Cue == "Soigne le groupe", "AI instructions not preserved for list/central display");
        Check(mechanics[0].Advice!.ShortCue == "Tank : prépare ta mitigation", "The original short cue was not retained");
        Check(GuidePageAnalysis.ValidPrepared(prepared, source, GuideLanguage.French, profile), "Prepared page rejected by cache validation");
        Check(!GuidePageAnalysis.ValidPrepared(prepared with { Coverage = [] }, source, GuideLanguage.French, profile), "Incomplete source coverage accepted");
        Check(!GuidePageAnalysis.ValidPrepared(prepared, source, GuideLanguage.English, profile)
            && !GuidePageAnalysis.ValidPrepared(prepared, source, GuideLanguage.French, GuideModelCatalog.Profiles[1]), "Cache crossed model/language boundaries");
        var wrongAdvice = new GuideMechanic("Hammer", mechanics[0].Text, "") { Advice = mechanics[0].Advice! with { Evidence = ["fabricated excerpt"] } };
        var corrupt = prepared with { Bosses = [prepared.Bosses[0] with { Phases = [new("", "", [wrongAdvice])] }] };
        Check(!GuidePageAnalysis.ValidPrepared(corrupt, source, GuideLanguage.French, profile), "Ungrounded cached instruction accepted");
        var now = DateTime.UtcNow;
        var cast = new GuideCast(Duty, 1, 2, 3, "Sentinel", 4, "Hammer", now.AddSeconds(4));
        Check(GuideSynchronization.Match(prepared, cast, now)?.Mechanic == mechanics[0]
            && GuideSynchronization.Match(prepared, cast with { BossName = "Another Boss" }, now) == null, "AI trigger is not scoped to its boss");
        var tracker = new GuideEncounterTracker();
        tracker.Update(prepared, [new(1, 2, 3, "Sentinel", false, true, 5)], false);
        var signal = new GuideSignal(prepared.Bosses[0], prepared.Bosses[0].Phases[0], mechanics[0], GuideSignalKind.Cast, 1, 2, 3, 4, 0, now.AddSeconds(4), GuidanceKind.Tankbuster, "fixture");
        tracker.Synchronize([signal]); tracker.Resolve(signal); tracker.Synchronize([]);
        Check(tracker.Frame.Active.Length == 0, "Finished mechanic remains highlighted");
        tracker.Synchronize([signal with { Until = now.AddSeconds(20) }]);
        Check(tracker.Frame.Active.Length == 1, "Repeated mechanic was treated as a completed checklist item");
        using var malformed = new PageModel { InvalidRange = true };
        try { await GuidePageAnalysis.Compile(source, GuideLanguage.French, profile, 32768, malformed, (_, _) => { }, CancellationToken.None); throw new Exception("Invalid AI ranges accepted"); }
        catch (InvalidDataException) { }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await GuidePageAnalysis.Compile(source, GuideLanguage.French, profile, 32768, model, (_, _) => { }, canceled.Token); throw new Exception("Canceled analysis ran"); }
        catch (OperationCanceledException) { }
        Check(GuidePageAnalysis.CompleteCoverage([new(0, 5), new(5, 7)], 12)
            && !GuidePageAnalysis.CompleteCoverage([new(0, 5), new(6, 6)], 12)
            && !GuidePageAnalysis.CompleteCoverage([new(0, 7), new(6, 6)], 12), "Physical partition has gaps or overlaps");
        var directory = Path.Combine(Path.GetTempPath(), "foretell-page-tests-" + Guid.NewGuid());
        try
        {
            using (var service = new ForetellGuideSummaries(directory, _ => new PageModel()))
            {
                service.Update(source, GuideLanguage.French, true, false, false, "");
                await WaitFor(() => service.Snapshot?.Stage is "Ready" or "Failed");
                Check(service.Snapshot?.Prepared?.MechanicCount == 2 && service.Runtime.ProcessID == null, "Page worker did not publish/unload the prepared guide");
            }
            using (var service = new ForetellGuideSummaries(directory, _ => throw new Exception("Cached guide loaded a model")))
            {
                service.Update(source, GuideLanguage.French, true, true, false, "");
                await WaitFor(() => service.Snapshot?.Stage is "Ready" or "Failed");
                Check(service.Snapshot?.Prepared?.MechanicCount == 2, "Cache unavailable in combat without a model");
                service.Update(source, GuideLanguage.French, false, true, false, "");
                await WaitFor(() => service.Snapshot?.Stage is "Ready" or "Failed");
                Check(service.Snapshot?.Prepared != null, "Disabling inference hid the prepared cache");
                service.Update(null, GuideLanguage.French, false, false, false, "");
                Check(service.Snapshot == null, "Disabling guides retained prepared state");
            }
            using (var service = new ForetellGuideSummaries(directory, _ => new PageModel()))
            {
                service.Retry(true);
                service.Update(source, GuideLanguage.French, true, true, false, "");
                await WaitFor(() => service.Snapshot?.Stage == "PausedInCombat");
                service.Update(source, GuideLanguage.French, true, false, false, "");
                await WaitFor(() => service.Snapshot?.Stage is "Ready" or "Failed");
                Check(service.Snapshot?.Prepared != null, "Forced refresh did not resume after combat");
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine("Whole-page AI grouping, grounded instructions, repeat mechanics, model-aware offline cache, refresh and combat pause passed.");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static async Task VerifyDuskVigilSources(GuideModelProfile profile)
    {
        var duty = new GuideDuty(1, 1366, "The Dusk Vigil");
        var wiki = string.Join('\n', DuskVigilModel.Names.Zip(DuskVigilModel.Passages, (name, passages) => name + "\n" + string.Join('\n', passages)));
        var workbook = """
            {"Worksheet":"HW Leveling","CellFields":["coordinate","visualStyle","text","formula (if present)"]}
            ["E2",5,"First Boss"]
            ["G2",5,"Second Boss"]
            ["I2",5,"Third Boss"]
            ["A3",16,"Dusk Vigil"]
            ["I3",8,"* When the Whirlwind spawns in the middle, run behind a pile of rubble (LoS the Whirlwind)"]
            """;
        var source = GuideSourceAssembly.Combine(duty, new(
            [new("Console Games Wiki", "https://ffxiv.consolegameswiki.com/mediawiki/index.php?title=The%20Dusk%20Vigil&oldid=1490396", wiki, wiki, "html"),
                new("Community Workbook", "https://docs.google.com/spreadsheets/d/1MX0RjPS4gtT6YI5Szxlsin9hcaohnEQQC7zNdrDHBrQ/edit", workbook, workbook, "xlsx")],
            [new("Console Games Wiki", "Ready", "https://ffxiv.consolegameswiki.com/"),
                new("Community Workbook", "Ready", "https://docs.google.com/"),
                new("Raven's Reminders", "Unavailable", "https://ravensreminders.com/tldr-guides/", "403 (Forbidden)")]), DateTime.UtcNow);
        using var model = new DuskVigilModel();
        var partials = new List<GuideDocument>();
        var prepared = await GuidePageAnalysis.Compile(source, GuideLanguage.English, profile, 32768, model, (_, _) => { }, CancellationToken.None, partials.Add);
        Check(model.OutlineCalls == 1 && model.DetailCalls == 3, "Multiple source excerpts created separate bosses or repeated valid draft analysis");
        Check(prepared.Bosses.Select(boss => boss.Name).SequenceEqual(DuskVigilModel.Names), "Ordinal source mapping changed the named encounter roster/order");
        Check(partials.Count == 3 && partials.All(document => document.Bosses.Select(boss => boss.Name).SequenceEqual(DuskVigilModel.Names)),
            "Partial analysis lost/reordered unprepared boss placeholders");
        Check(partials[0].Bosses[0].Phases.Sum(phase => phase.Mechanics.Length) == 1 && partials[0].Bosses[2].Phases.Length == 0
            && !GuidePageAnalysis.ValidPrepared(partials[0], source, GuideLanguage.English, profile), "First completed boss was mistaken for a fully prepared guide");
        Check(GuidePageAnalysis.ValidPrepared(prepared, source, GuideLanguage.English, profile), "Complete mixed-source Dusk Vigil fixture failed cache validation");
        var opinicus = prepared.Bosses[2];
        var mechanics = opinicus.Phases.SelectMany(phase => phase.Mechanics).ToArray();
        Check(mechanics.Select(mechanic => mechanic.Name).SequenceEqual(DuskVigilModel.Abilities[2]), "Third boss lost a named ability or merged two rubble mechanics");
        var whirl = mechanics.Single(mechanic => mechanic.Name == "Whirling Gaol");
        Check(whirl.Advice!.Cue == "Hide behind a pile of rubble" && whirl.Advice.Evidence.Any(evidence => evidence.Contains(DuskVigilModel.WorkbookWhirlwind, StringComparison.Ordinal)),
            "Anonymous Third Boss strategy did not survive in the named boss instruction");
        Check(GuideSourceAssembly.EvidenceSources(prepared, whirl.Advice).Select(page => page.Provider).ToHashSet()
            .SetEquals(["Console Games Wiki", "Community Workbook"]), "Combined third-boss instruction lost its separate source provenance");
        var now = DateTime.UtcNow;
        foreach (var mechanic in mechanics)
        {
            var cast = new GuideCast(duty, 1, 2, 3, "Opinicus", 4, mechanic.Name, now.AddSeconds(4));
            Check(GuideSynchronization.Match(prepared, cast, now)?.Mechanic == mechanic
                && GuideSynchronization.Match(prepared, cast with { BossName = "Ser Yuhelmeric" }, now) == null,
                "Third-boss source mechanics failed exact live matching or leaked to another boss");
        }
        Console.WriteLine("Dusk Vigil source contract: ordinal workbook context, named roster, partial coverage and distinct third-boss cast cues passed (stub model).");
    }

    private sealed class DuskVigilModel : IGuideSummaryModel
    {
        public static readonly string[] Names = ["Towering Oliphant", "Ser Yuhelmeric", "Opinicus"];
        public static readonly string[][] Abilities = [["Trunk Tawse"], ["Skullsplinter"], ["Golden Talons", "Alpine Draft", "Freefall", "Whirling Gaol", "Winds of Winter"]];
        public static readonly string[][] Passages =
        [
            ["Trunk Tawse: Telegraphed physical tankbuster."],
            ["Skullsplinter: Telegraphed physical tankbuster."],
            ["Golden Talons: Telegraphed physical tankbuster.",
                "[Alpine Draft] [Alpine Draft] [Alpine Draft] Alpine Draft: Telegraphed line AoE at a random player.",
                "Freefall: Opinicus turns to face one player and jumps on them in a large telegraphed AoE.",
                "Whirling Gaol: the outer sides of the room will start pushing you in towards the center, where you will be inflicted with Fetters, then receive a [Wind Resistance Down] [Wind Resistance Down] Wind Resistance Down stack and some damage. Hiding behind some of the crumbled masonry in the room will prevent you from being pushed into the center.",
                "Winds of Winter: Room-wide AoE attack for moderate damage, and stacks [Wind Resistance Down] [Wind Resistance Down] Wind Resistance Down on anyone struck. Avoid by standing so there is a pile of rubble between you and the boss. This attack will destroy all piles of rubble, and new ones will fall from the ceiling."]
        ];
        public const string WorkbookWhirlwind = "* When the Whirlwind spawns in the middle, run behind a pile of rubble (LoS the Whirlwind)";
        public int OutlineCalls;
        public int DetailCalls;
        public GuideModelRuntime Runtime => new();
        public Task Start(Action<GuideModelProgress> progress, CancellationToken cancellation) => Task.CompletedTask;
        public Task<string> Summarize(string source, GuideLanguage language, CancellationToken cancellation) => throw new Exception("Legacy source cutting used");
        public Task<string> Analyze(string system, string source, object schema, int outputTokens, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (system.StartsWith("Read the ENTIRE"))
            {
                ++OutlineCalls;
                Check(Names.All(source.Contains) && source.Contains("[\"I2\",5,\"Third Boss\"]")
                    && source.Contains("[\"I3\",8,\"" + WorkbookWhirlwind + "\"]"), "Outline model did not receive named roster and untouched ordinal worksheet context together");
                var bosses = Names.Select((name, index) => new { name, passages = Passages[index] })
                    .Append(new { name = "Opinicus", passages = new[] { WorkbookWhirlwind } });
                return Task.FromResult(JsonSerializer.Serialize(new { bosses }));
            }
            ++DetailCalls;
            var bossIndex = Array.FindIndex(Names, name => source.StartsWith("Boss: " + name + "\n", StringComparison.Ordinal));
            Check(bossIndex >= 0, "Detail analysis received an anonymous/unknown boss");
            Check(source.Contains(WorkbookWhirlwind) && Names.All(source.Contains), "Full-page boss analysis lost worksheet context or the named roster");
            string Citation(string text)
            {
                var line = source.Split('\n').First(line => line.StartsWith('[') && line.Contains(text, StringComparison.Ordinal));
                return line[1..line.IndexOf(']')];
            }
            var name = Names[bossIndex];
            var cues = bossIndex == 2 ? new[] { "Tank: mitigate", "Dodge the line", "Move out of the marked area", "Hide behind a pile of rubble", "Hide behind rubble from the boss" } : ["Tank: mitigate"];
            var mechanics = Abilities[bossIndex].Select((ability, index) => new
            {
                name = ability, displayName = ability, cue = cues[index], description = cues[index], triggerKind = "cast", triggerName = ability,
                evidence = bossIndex == 2 && ability == "Whirling Gaol" ? new[] { Citation(Passages[bossIndex][index]), Citation(WorkbookWhirlwind) } : [Citation(Passages[bossIndex][index])],
                responses = Array.Empty<GuideResponse>()
            });
            return Task.FromResult(JsonSerializer.Serialize(new { summary = "Prepare for the next boss.", bosses = new[] { new { name, displayName = name, summary = "Prepare for the next boss.", mechanics } } }));
        }
        public void Dispose() { }
    }

    private static void VerifyEvidenceAndConditions(GuideDocument source, GuideModelProfile profile)
    {
        const string original = "Move away so a to avoid the attack around the boss.";
        Check(GuidePageAnalysis.RestoreQuote(original, original.Replace("so a to", "so to")) == original.TrimEnd('.'), "Source typo was not restored before analysis");
        const string negative = "Players must not run toward the boss during this attack.";
        Check(GuidePageAnalysis.RestoreQuote(negative, negative.Replace("must not run", "must run")) == negative.TrimEnd('.'), "Missing source negation was not restored");
        Check(GuidePageAnalysis.RestoreQuote(negative, negative.Replace("toward", "away")) == null, "Changed source direction accepted");
        Check(GuidePageAnalysis.RestoreQuote("Unrelated short page", original) == null, "Unrelated source accepted");
        const string quote = "Hammer: If the weapon is normal, stay away. If it is enlarged, stay near the boss.";
        var conditionalSource = source with { Page = source.Page! with { Text = source.Page.Text + "\n" + quote } };
        var alternatives = new[] { new GuideResponse("Arme normale", "Éloigne-toi"), new GuideResponse("Arme agrandie", "Rapproche-toi") };
        string Response(string[] evidence) => JsonSerializer.Serialize(new { summary = "Surveille l'arme.", bosses = new[] { new
        {
            name = "Sentinel", displayName = "Sentinelle", summary = "Surveille l'arme.", mechanics = new[] { new
            {
                name = "Hammer", displayName = "Marteau", cue = "Surveille l'arme", description = "Arme normale : éloigne-toi. Arme agrandie : rapproche-toi.",
                triggerKind = "cast", triggerName = "Hammer", evidence, responses = alternatives
            } }
        } } });
        var resolved = GuidePageAnalysis.ResolveEvidence(Response(["1"]), [quote]);
        var parsed = GuidePageAnalysis.Parse(conditionalSource, quote, resolved, GuideLanguage.French, profile);
        var advice = parsed.Bosses[0].Phases[0].Mechanics[0].Advice!;
        Check(advice.Cue == "Arme normale : Éloigne-toi ; Arme agrandie : Rapproche-toi" && advice.Responses.SequenceEqual(alternatives), "Conditional central/list instruction dropped a branch");
        Check(advice.ShortCue == "Surveille l'arme", "The original short cue replaced the conditional alternatives");
        var cached = parsed with { Coverage = [new(0, conditionalSource.Page!.Text.Length)] };
        Check(GuidePageAnalysis.ValidPrepared(cached, conditionalSource, GuideLanguage.French, profile), "Conditional response cache rejected");
        var legacyMechanic = new GuideMechanic("Hammer", parsed.Bosses[0].Phases[0].Mechanics[0].Text, "") { Advice = advice with { ShortCue = "" } };
        var legacy = cached with { Bosses = [cached.Bosses[0] with { Phases = [new("", "", [legacyMechanic])] }] };
        Check(GuidePageAnalysis.ValidPrepared(legacy, conditionalSource, GuideLanguage.French, profile) && legacyMechanic.Advice!.Cue == advice.Cue,
            "Adding ShortCue invalidated an old conditional cache or changed its merged cue");
        foreach (var invalid in new[] { "0", "2", "source text" })
        {
            try { GuidePageAnalysis.ResolveEvidence(Response([invalid]), [quote]); throw new Exception("Invalid citation accepted"); }
            catch (InvalidDataException) { }
        }
    }

    private sealed class PageModel : IGuideSummaryModel
    {
        public readonly List<string> Inputs = [];
        public bool InvalidRange;
        public bool SplitLargeSource;
        public int DetailCalls;
        public GuideModelRuntime Runtime => new();
        public Task Start(Action<GuideModelProgress> progress, CancellationToken cancellation) => Task.CompletedTask;
        public Task<string> Summarize(string source, GuideLanguage language, CancellationToken cancellation) => throw new Exception("Legacy pre-cut summary pipeline used");
        public Task<string> Analyze(string system, string source, object schema, int outputTokens, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); Inputs.Add(source);
            if (system.StartsWith("Read the ENTIRE") && SplitLargeSource && source.Length > 4200) throw new GuideContextException(source.Length, 4200);
            if (!system.StartsWith("Read the ENTIRE")) ++DetailCalls;
            if (system.StartsWith("Read the ENTIRE")) return Task.FromResult(JsonSerializer.Serialize(new { bosses = new[] { new { name = "Sentinel", passages = new[] { InvalidRange ? "fabricated quote" : "Sentinel\nHammer: Heavy damage to the tank.\nPulse: Everyone takes damage." } } } }));
            return Task.FromResult(JsonSerializer.Serialize(new { summary = "Prépare les soins du groupe.", bosses = new[]
            {
                new { name = "Sentinel", displayName = "Sentinelle", summary = "Prépare les soins du groupe.", mechanics = new[]
                {
                    new { name = "Hammer", displayName = "Marteau", cue = "Tank : prépare ta mitigation", description = "Le tank subit de lourds dégâts.", triggerKind = "cast", triggerName = "Hammer", evidence = new[] { "2" }, responses = Array.Empty<GuideResponse>() },
                    new { name = "Pulse", displayName = "Pulsation", cue = "Soigne le groupe", description = "Tout le groupe subit des dégâts.", triggerKind = "cast", triggerName = "Pulse", evidence = new[] { "3" }, responses = Array.Empty<GuideResponse>() }
                } }
            } }));
        }
        public void Dispose() { }
    }

    public static void Smoke(string directory, string outputDirectory, string modelID, string[] titles, bool aggregate = false)
        => SmokeAsync(directory, outputDirectory, modelID, titles, aggregate).GetAwaiter().GetResult();

    private static async Task SmokeAsync(string directory, string outputDirectory, string modelID, string[] titles, bool aggregate)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var source = new ForetellWikiSource();
        using var providers = new ForetellGuideProviders(Path.Combine(outputDirectory, "providers"));
        using var model = new ForetellGuideLocalModel(directory, true, 32768, 8, modelID);
        var profile = GuideModelCatalog.Get(modelID);
        if (profile.ID != modelID) throw new ArgumentException("Unknown model ID: " + modelID);
        Directory.CreateDirectory(outputDirectory);
        var call = 0;
        model.AnalysisTrace = (input, output) => File.WriteAllText(Path.Combine(outputDirectory, $"call-{++call:D3}.json"), JsonSerializer.Serialize(new { input, output }));
        var logLock = new object();
        model.RuntimeTrace = line => { lock (logLock) File.AppendAllText(Path.Combine(outputDirectory, "runtime.log"), line + Environment.NewLine); };
        var lastProgress = DateTime.MinValue;
        await model.Start(progress =>
        {
            if ((DateTime.UtcNow - lastProgress).TotalSeconds < 5 && progress.Received != progress.Total) return;
            lastProgress = DateTime.UtcNow;
            Console.WriteLine($"{progress.Stage} {progress.Received}/{progress.Total}");
        }, cancellation.Token);
        Directory.CreateDirectory(outputDirectory);
        var failures = new List<string>();
        foreach (var title in titles)
        {
            try
            {
            var duty = new GuideDuty(1, 1, title);
            var page = aggregate ? GuideSourceAssembly.Combine(duty, await providers.Fetch(duty, cancellation.Token), DateTime.UtcNow)
                : ForetellGuideParser.ReadPage(await source.Fetch(duty, cancellation.Token), duty, DateTime.UtcNow);
            foreach (var provider in page.Providers) Console.WriteLine(provider.Provider + ": " + provider.Status + " " + provider.Error);
            File.WriteAllText(Path.Combine(outputDirectory, "source-" + GuideNames.Hash(title)[..12] + ".json"), JsonSerializer.Serialize(page));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var document = await GuidePageAnalysis.Compile(page, ForetellEngine.GuideContentLanguage, profile, 32768, model,
                (completed, total) => Console.WriteLine($"{title}: {completed}/{total}"), cancellation.Token);
            File.WriteAllText(Path.Combine(outputDirectory, "prepared-" + GuideNames.Hash(title)[..12] + ".json"), JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{title} · {watch.Elapsed.TotalSeconds:F1}s · {document.Bosses.Length} bosses · {document.MechanicCount} mechanics · {model.Runtime.PromptTokens} prompt tokens");
            foreach (var boss in document.Bosses)
            {
                Console.WriteLine(boss.Name + " · " + boss.Summary);
                foreach (var mechanic in boss.Phases.SelectMany(phase => phase.Mechanics))
                    Console.WriteLine($"  {mechanic.Name}: {mechanic.Advice!.Cue} [{mechanic.Advice.TriggerKind}:{mechanic.Advice.TriggerName}]");
            }
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            {
                failures.Add(title + ": " + error.Message);
                Console.WriteLine("FAILED " + title + ": " + error.Message);
            }
        }
        model.Dispose();
        if (model.Runtime.ProcessID != null) throw new InvalidOperationException("Analysis smoke left its model process alive.");
        if (failures.Count > 0) throw new InvalidDataException(string.Join("\n", failures));
    }
}
