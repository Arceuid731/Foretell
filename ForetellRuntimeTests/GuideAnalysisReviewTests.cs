using BossMod.Foretell;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class GuideAnalysisReviewTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly GuideModelProfile Profile = GuideModelCatalog.Get(GuideModelCatalog.DefaultID);
    private static readonly string[] Paragraphs =
    [
        "Sentinel", "Opening phase", "Spark: Spread out during Opening phase.",
        "Pulse: Unavoidable damage to everyone throughout the fight.",
        "Intermission", "Barrier: Players are restrained until an ally automatically breaks the shield; no player action is required.",
        "Final phase", "Nova: During Final phase, marked players stack together; unmarked players join the target.",
        "Last Word: Kill the boss before this cast finishes; use Limit Break when the gauge fills."
    ];
    private static GuideDocument Source() => new(GuideDocument.CurrentSchema, new(321, 654, "Synthetic audit trial"), "Synthetic audit trial", 1,
        DateTime.UtcNow, GuideNames.Hash(string.Join('\n', Paragraphs)), [])
    { Page = new("Fixture", "https://example.invalid/guide", string.Join('\n', Paragraphs), "") };
    private static JsonObject Mechanic(string name, string cue, string paragraph, bool contextOnly = false) => JsonSerializer.SerializeToNode(new
    {
        name, displayName = name, cue, description = cue, triggerKind = "cast", triggerName = name,
        evidence = new[] { paragraph }, responses = Array.Empty<GuideResponse>(), roles = Array.Empty<string>(), conflict = "",
        phaseMemberships = Array.Empty<GuidePhaseMembership>(), contextOnly
    })!.AsObject();
    private static JsonObject Draft() => JsonSerializer.SerializeToNode(new
    {
        summary = "Watch the arena.", bosses = new[] { new
        {
            name = "Sentinel", displayName = "Sentinel", summary = "Prepare group healing.",
            phaseDefinitions = new[] { new GuidePhaseDefinition("opening", "Opening phase", ["2"]) },
            mechanics = new[] { Mechanic("Spark", "Spread out", "3"), Mechanic("Pulse", "Heal the group", "4") }
        } }
    }, Json)!.AsObject();
    private static string WithMechanics(params JsonObject[] mechanics)
    {
        var response = Draft();
        response["bosses"]![0]!["mechanics"] = new JsonArray(mechanics.Cast<JsonNode?>().ToArray());
        return response.ToJsonString();
    }
    private static GuideDocument Parse(string response) => GuidePageAnalysis.Parse(Source(), Source().Page!.Text,
        GuidePageAnalysis.ResolveEvidence(GuidePageAnalysis.PreparePhaseMetadata(response, Paragraphs), Paragraphs), GuideLanguage.English, Profile);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid audit output was accepted.");
    }

    public static async Task Run()
    {
        VerifyOutlineReferences();
        VerifyGrounding();
        foreach (var defect in new[] { "none", "missing-heading", "duplicate", "boss", "rename", "evidence", "phase" })
        {
            using var model = new ValidationModel(defect);
            var compiled = await GuidePageAnalysis.Compile(Source(), GuideLanguage.English, Profile, 32768, model, (_, _) => { }, CancellationToken.None);
            var reviewExpected = defect is "none" or "missing-heading" or "phase" ? 0 : 1;
            Check(model.Outlines == 1 && model.Drafts == 1 && model.Reviews == reviewExpected && model.PhaseCalls == (defect == "phase" ? 1 : 0),
                "Validation-first analysis made unnecessary calls for " + defect);
            Check(compiled.Bosses.Single().Name == "Sentinel" && compiled.MechanicCount == 2
                && compiled.Summary == "Watch the arena." && GuidePageAnalysis.ValidPrepared(compiled, Source(), GuideLanguage.English, Profile),
                "Validated analysis lost grounded content for " + defect);
            Check(compiled.Bosses.Single().PhaseDefinitions.Single().Name == "Opening phase",
                "Full source or phase-only repair lost the original phase label.");
            Check(compiled.ModelRevision == Profile.Revision, "Validation-first analysis invalidated unrelated caches.");
        }
        using (var invalid = new ValidationModel("persistent"))
        {
            try
            {
                await GuidePageAnalysis.Compile(Source(), GuideLanguage.English, Profile, 32768, invalid, (_, _) => { }, CancellationToken.None);
                throw new InvalidOperationException("Ungrounded replacement bypassed strict validation.");
            }
            catch (InvalidDataException) { }
            Check(invalid.Outlines == 1 && invalid.Drafts == 1 && invalid.Reviews == 2, "Invalid output exceeded the three-attempt budget.");
        }
        foreach (var defect in new[] { "context", "review-context" })
        {
            using var model = new ValidationModel(defect);
            var large = Source() with { Page = Source().Page! with { Text = Source().Page!.Text + "\nFULL_PAGE_END" } };
            var steps = new List<GuideAnalysisStep>();
            var compiled = await GuidePageAnalysis.Compile(large, GuideLanguage.English, Profile, 32768, model, (_, _) => { }, CancellationToken.None, null, steps.Add);
            Check(model.ContextRejections == 1 && model.Drafts == 2 && model.Reviews == (defect == "context" ? 0 : 1)
                && compiled.MechanicCount == 2 && steps.Any(step => step.Detail.Contains("Full-page context preflight rejected", StringComparison.Ordinal)),
                "Selected-source fallback did not follow an actual context rejection.");
        }
        using (var model = new ValidationModel("persistent-context"))
        {
            var large = Source() with { Page = Source().Page! with { Text = Source().Page!.Text + "\nFULL_PAGE_END" } };
            try
            {
                await GuidePageAnalysis.Compile(large, GuideLanguage.English, Profile, 32768, model, (_, _) => { }, CancellationToken.None);
                throw new InvalidOperationException("Unusable selected-source context was accepted.");
            }
            catch (GuideContextException) { }
            Check(model.ContextRejections == 2 && model.Drafts == 2 && model.Reviews == 0, "Context failure repeated requests indefinitely.");
        }
        VerifyTwoPhaseHierarchy(Source() with { Page = Source().Page! with { Text = """
            The Ultima Weapon
            Phase 1
            Part 1: Titan
            Earthen Fury: Healers should be ready to repair the damages at once.
            Part 2: Garuda
            Aerial Blast: Healers should quickly repair the damage.
            Part 3: Ifrit
            Hellfire: Heal through this ultimate attack.
            Phase 2
            Homing Lasers: This is a high-damage attack on the tank.
            """ } });
        var recording = Environment.GetEnvironmentVariable("FORETELL_ANALYSIS_REPLAY");
        if (!string.IsNullOrWhiteSpace(recording)) Replay(recording);
        Console.WriteLine("Validation-first analysis: two calls for valid drafts, bounded full review on failure, phase-only repair and strict source evidence passed.");
    }

    private static void VerifyGrounding()
    {
        var result = Parse(Draft().ToJsonString());
        Check(result.MechanicCount == 2 && result.Bosses[0].Phases[0].Mechanics[0].Advice!.Cue == "Spread out"
            && result.Bosses[0].Phases[0].Mechanics[1].Advice!.Cue == "Heal the group", "Direct validation changed supported cues.");
        Reject(() => Parse(WithMechanics(Mechanic("Pulse", "Heal the group", "999"))));
        Reject(() => Parse(WithMechanics(Mechanic("Pulse", "Move 999 yalms away", "4"))));
        Reject(() => Parse(WithMechanics(Mechanic("Pulse", "Heal the group", "4"), Mechanic("Pulse", "Heal the group", "4"))));
        Reject(() => Parse(Draft().ToJsonString().Replace("Sentinel", "Foreign Boss", StringComparison.Ordinal)));
        Reject(() => Parse("{}"));
        var conditional = Mechanic("Nova", "Follow the marked target", "8");
        conditional["responses"] = JsonSerializer.SerializeToNode(new[] { new GuideResponse("Marked", "Stack together"), new GuideResponse("Unmarked", "Join the target") }, Json);
        Check(Parse(WithMechanics(conditional)).Bosses[0].Phases[0].Mechanics[0].Advice!.Responses
            .SequenceEqual([new("Marked", "Stack together"), new("Unmarked", "Join the target")]), "Direct validation lost a documented alternative.");
        var prepared = Parse(WithMechanics(Mechanic("Barrier", "Wait for the ally", "6", true), Mechanic("Last Word", "Use Limit Break before the cast finishes", "9")));
        Check(prepared.MechanicCount == 2 && prepared.Bosses[0].Phases[0].Mechanics[0].Advice!.ContextOnly
            && !prepared.Bosses[0].Phases[0].Mechanics[1].Advice!.ContextOnly && prepared.Page!.Text == Source().Page!.Text,
            "Context-only metadata lost source or hid a timed damage check.");
        var cached = prepared with { Coverage = [new(0, Source().Page!.Text.Length)] };
        Check(GuidePageAnalysis.ValidPrepared(JsonSerializer.Deserialize<GuideDocument>(JsonSerializer.Serialize(cached))!, Source(), GuideLanguage.English, Profile),
            "Context-only metadata or evidence did not survive strict cache validation.");
        Reject(() => Parse(WithMechanics(Mechanic("Barrier", "Wait 999 seconds", "6", true))));
        Reject(() => Parse(WithMechanics(Mechanic("Barrier", "Wait for the ally", "999", true))));
        var conflict = Mechanic("Barrier", "Wait for the ally", "6", true);
        conflict["conflict"] = "Sources disagree about the response.";
        Reject(() => Parse(WithMechanics(conflict)));
    }

    private sealed class ValidationModel(string defect) : IGuideSummaryModel
    {
        public int Outlines;
        public int Drafts;
        public int Reviews;
        public int PhaseCalls;
        public int ContextRejections;
        public GuideModelRuntime Runtime => new();
        public Task Start(Action<GuideModelProgress> progress, CancellationToken cancellation) => throw new InvalidOperationException("No inference allowed.");
        public Task<string> Summarize(string source, GuideLanguage language, CancellationToken cancellation) => throw new NotSupportedException();
        public Task<string> Analyze(string system, string source, object schema, int outputTokens, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (system.StartsWith("Read the ENTIRE", StringComparison.Ordinal))
            {
                ++Outlines;
                Check(Paragraphs.Select((paragraph, index) => $"[{index + 1}] {paragraph}").All(paragraph => source.Contains(paragraph, StringComparison.Ordinal)),
                    "Outline did not receive the complete source.");
                return Task.FromResult(JsonSerializer.Serialize(new { bosses = new[] { new { name = "Sentinel",
                    passages = Enumerable.Range(1, Paragraphs.Length).Where(index => defect != "missing-heading" || index != 2)
                        .Select(index => index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray() } } }));
            }
            if (system.StartsWith("Identify ONLY", StringComparison.Ordinal))
            {
                ++PhaseCalls;
                Check(source.Contains("[2] Opening phase", StringComparison.Ordinal) && source.Contains("<mechanic-catalogue>", StringComparison.Ordinal)
                    && Reviews == 0, "Phase repair did not retain whole-source evidence independently of full review.");
                return Task.FromResult("{\"phases\":[{\"name\":\"Opening phase\",\"evidence\":\"2\",\"mechanics\":[\"1\"]}]}");
            }
            if (system.StartsWith("Audit", StringComparison.Ordinal))
            {
                ++Reviews;
                Check(system.Contains("COMPLETE REPLACEMENT", StringComparison.Ordinal) && source.Contains("validation issue", StringComparison.Ordinal)
                    && source.Contains("[2] Opening phase", StringComparison.Ordinal) && source.Contains("Last Word:", StringComparison.Ordinal),
                    "Validation failure did not request a complete grounded replacement.");
                if (defect == "review-context" && source.Contains("FULL_PAGE_END", StringComparison.Ordinal))
                {
                    ++ContextRejections;
                    throw new GuideContextException(40000, 32768);
                }
                var replacement = Draft();
                if (defect == "persistent") replacement["bosses"]![0]!["mechanics"]![0]!["evidence"]![0] = "999";
                return Task.FromResult(replacement.ToJsonString());
            }
            ++Drafts;
            if (defect == "persistent-context" || defect == "context" && source.Contains("FULL_PAGE_END", StringComparison.Ordinal))
            {
                ++ContextRejections;
                throw new GuideContextException(40000, 32768);
            }
            Check(source.Contains("[2] Opening phase", StringComparison.Ordinal), "Full-page draft lost a heading omitted by the outline.");
            var draft = Draft();
            var boss = draft["bosses"]![0]!;
            if (defect == "duplicate") boss["mechanics"]!.AsArray().Add(boss["mechanics"]![0]!.DeepClone());
            if (defect == "boss") boss["name"] = "Foreign Boss";
            if (defect == "rename") boss["mechanics"]![0]!["name"] = "Invented Spark";
            if (defect is "rename" or "evidence" or "persistent" || defect == "review-context" && source.Contains("FULL_PAGE_END", StringComparison.Ordinal))
                boss["mechanics"]![0]!["evidence"]![0] = "999";
            if (defect == "phase") boss["phaseDefinitions"]![0]!["name"] = "Invented phase";
            return Task.FromResult(draft.ToJsonString(new() { WriteIndented = true }));
        }
        public void Dispose() { }
    }

    private static void Replay(string path)
    {
        var report = JsonSerializer.Deserialize<GuideAnalysisReport>(File.ReadAllText(path), GuideAnalysisJournal.Json)
            ?? throw new InvalidDataException("Missing recorded report.");
        var source = report.SourceDocument ?? throw new InvalidDataException("Missing recorded source.");
        if (!report.Calls.Any(call => call.Stage == "Draft" && call.State == "Completed"))
        {
            ReplayOutline(report, source);
            return;
        }
        var draft = report.Calls.First(call => call.Stage == "Draft" && call.State == "Completed");
        var sourceStart = draft.Source.IndexOf("<source>\n", StringComparison.Ordinal) + "<source>\n".Length;
        var sourceEnd = draft.Source.IndexOf("\n</source>", sourceStart, StringComparison.Ordinal);
        Check(sourceStart >= "<source>\n".Length && sourceEnd >= sourceStart, "Recorded numbered draft source is unavailable.");
        var paragraphs = draft.Source[sourceStart..sourceEnd].Split('\n').Select((line, index) =>
        {
            var prefix = $"[{index + 1}] ";
            Check(line.StartsWith(prefix, StringComparison.Ordinal), "Recorded paragraph numbering has changed.");
            return line[prefix.Length..];
        }).ToArray();
        var selected = string.Join('\n', paragraphs);
        var watch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var checkedDraft = GuidePageAnalysis.ResolveEvidence(GuidePageAnalysis.PreparePhaseMetadata(draft.Response, paragraphs, warnings.Add), paragraphs);
        var replayed = GuidePageAnalysis.Parse(source, selected, checkedDraft, GuideLanguage.English, GuideModelCatalog.Get(report.ModelID))
            with { Coverage = [new(0, source.Page!.Text.Length)] };
        Check(GuidePageAnalysis.ValidPrepared(replayed, source, GuideLanguage.English, GuideModelCatalog.Get(report.ModelID)), "Recorded draft failed strict validation.");
        watch.Stop();
        var compactDraft = JsonNode.Parse(draft.Response)!.ToJsonString();
        VerifyTwoPhaseHierarchy(source);
        Console.WriteLine($"Recorded replay {report.ID}: {replayed.MechanicCount} mechanics; {warnings.Count} phase fallback; validation {watch.Elapsed.TotalMilliseconds:F1} ms; no inference.");
        Console.WriteLine($"Recorded timings: {string.Join(", ", report.Calls.Select(call => $"{call.Stage} {call.Seconds:F2}s/{call.OutputTokens} tokens"))}.");
        Console.WriteLine($"Recorded draft {draft.Response.Length} -> {compactDraft.Length} compact characters; validated without a full audit. No new inference timing claimed.");
    }

    private static void VerifyOutlineReferences()
    {
        const string source = "Sentinel\nPhase 1\n[Edit section: Phase 1]\nPart 1\n[Edit section: Part 1]\nSpark: Do not run toward the boss.\nPhase 2\nPulse: Heal the group.";
        var selected = GuidePageAnalysis.ResolveOutlinePassages(source, ["8", "2", "4", "6", "7"]);
        Check(selected.SequenceEqual(["Phase 1", "Part 1", "Spark: Do not run toward the boss.", "Phase 2", "Pulse: Heal the group."]),
            "ID outline lost exact headings, negation or source order.");
        Check(GuidePageAnalysis.ResolveOutlinePassages(source, ["Spark: Do not run toward the boss."]).Single() == selected[2], "Legacy verbatim outline was rejected.");
        Reject(() => GuidePageAnalysis.ResolveOutlinePassages(source, ["Phase 1\nPart 1\nSpark: Do not run toward the boss."]));
        Reject(() => GuidePageAnalysis.ResolveOutlinePassages(source, ["999"]));
        Reject(() => GuidePageAnalysis.ResolveOutlinePassages(source, ["0"]));
        Reject(() => GuidePageAnalysis.ResolveOutlinePassages(source, ["2", "fabricated quote"]));
        Reject(() => GuidePageAnalysis.ResolveOutlinePassages(source, ["Spark: Run away from the boss."]));
        var longSource = new string('x', 6000) + "\nSentinel\nNova: Stack together at the end of the page.";
        var paragraphs = GuidePageAnalysis.Paragraphs(longSource);
        Check(GuidePageAnalysis.ResolveOutlinePassages(longSource, Enumerable.Range(1, paragraphs.Length)
            .Select(index => index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()).SequenceEqual(paragraphs),
            "Numbering truncated a long source paragraph or lost the page tail.");
    }

    private static void ReplayOutline(GuideAnalysisReport report, GuideDocument source)
    {
        var call = report.Calls.First(call => call.Stage == "Outline" && call.State == "Completed");
        var outline = JsonNode.Parse(call.Response)!;
        var paragraphs = GuidePageAnalysis.Paragraphs(source.Page!.Text);
        var watch = Stopwatch.StartNew();
        foreach (var boss in outline["bosses"]!.AsArray())
        {
            var quotations = boss!["passages"]!.AsArray().Select(passage => passage!.GetValue<string>()).ToArray();
            Reject(() => GuidePageAnalysis.ResolveOutlinePassages(source.Page.Text, quotations));
            var references = new List<string>();
            var position = 0;
            foreach (var line in quotations.SelectMany(quote => quote.Split('\n', StringSplitOptions.RemoveEmptyEntries)))
            {
                var index = Array.FindIndex(paragraphs, position, paragraph => paragraph == line);
                Check(index >= 0, "Recorded failed outline contains a changed line, not just missing source separators.");
                references.Add((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                position = index + 1;
            }
            var selected = GuidePageAnalysis.ResolveOutlinePassages(source.Page.Text, references.ToArray());
            Check(selected.All(paragraph => source.Page.Text.Contains(paragraph, StringComparison.Ordinal)), "ID resolution generated a non-verbatim paragraph.");
            VerifyTwoPhaseHierarchy(source with { Page = source.Page with { Text = string.Join('\n', selected) } });
            Console.WriteLine($"Recorded failed outline: {references.Count} exact paragraph IDs; payload {JsonSerializer.Serialize(quotations).Length} -> {JsonSerializer.Serialize(references).Length} characters (simulated IDs, no inference).");
        }
        watch.Stop();
        Console.WriteLine($"Outline regression {report.ID}: strict legacy guard retained; exact ID resolution and two phases validated in {watch.Elapsed.TotalMilliseconds:F1} ms; recorded outline {call.Seconds:F2}s/{call.OutputTokens} tokens.");
    }

    private static void VerifyTwoPhaseHierarchy(GuideDocument source)
    {
        var paragraphs = GuidePageAnalysis.Paragraphs(source.Page!.Text);
        string Heading(string name) => (Array.FindIndex(paragraphs, paragraph => paragraph == name) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string Ability(string name) => (Array.FindIndex(paragraphs, paragraph => paragraph.StartsWith(name + ":", StringComparison.Ordinal)) + 1)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        JsonObject Attack(string name, string cue, string phaseID, string phaseLabel)
        {
            var mechanic = Mechanic(name, cue, Ability(name));
            mechanic["phaseMemberships"] = JsonSerializer.SerializeToNode(new[] { new GuidePhaseMembership(phaseID, [Heading(phaseLabel), Ability(name)]) }, Json);
            return mechanic;
        }
        var draft = Draft();
        var boss = draft["bosses"]![0]!;
        boss["name"] = "The Ultima Weapon";
        boss["displayName"] = "The Ultima Weapon";
        boss["phaseDefinitions"] = JsonSerializer.SerializeToNode(new[]
        {
            new GuidePhaseDefinition("first", "Phase 1", [Heading("Phase 1")]), new GuidePhaseDefinition("second", "Phase 2", [Heading("Phase 2")])
        }, Json);
        boss["mechanics"] = new JsonArray(Attack("Earthen Fury", "Heal the group", "first", "Phase 1"),
            Attack("Aerial Blast", "Heal the group", "first", "Phase 1"), Attack("Hellfire", "Heal the group", "first", "Phase 1"),
            Attack("Homing Lasers", "Tank: mitigate", "second", "Phase 2"));
        var resolved = GuidePageAnalysis.ResolveEvidence(GuidePageAnalysis.PreparePhaseMetadata(draft.ToJsonString(), paragraphs), paragraphs);
        var parsed = GuidePageAnalysis.Parse(source, source.Page.Text, resolved, GuideLanguage.English, Profile);
        var originalAdvice = parsed.Bosses[0].Phases[0].Mechanics.Select(mechanic => mechanic.Advice).ToArray();
        var unphased = parsed with { Bosses = [parsed.Bosses[0] with
        {
            PhaseDefinitions = [], Phases = parsed.Bosses[0].Phases.Select(phase => phase with
            {
                Mechanics = phase.Mechanics.Select(mechanic => new GuideMechanic(mechanic.Name, mechanic.Text, mechanic.Anchor) { Advice = mechanic.Advice }).ToArray()
            }).ToArray()
        }] };
        var plan = JsonSerializer.SerializeToNode(new { phases = new[]
        {
            new { name = "Phase 1", evidence = Heading("Phase 1"), mechanics = new[] { "1", "2", "3" } },
            new { name = "Phase 2", evidence = Heading("Phase 2"), mechanics = new[] { "4" } }
        } })!;
        parsed = GuidePageAnalysis.ApplyPhasePlan(unphased, source.Page.Text, plan.ToJsonString());
        Check(parsed.Bosses[0].Phases[0].Mechanics.Select(mechanic => mechanic.Advice).Zip(originalAdvice).All(pair => ReferenceEquals(pair.First, pair.Second))
            && GuidePageAnalysis.ValidPrepared(parsed with { Coverage = [new(0, source.Page.Text.Length)] }, source, GuideLanguage.English, Profile),
            "Phase-only graft changed core instructions or bypassed evidence validation.");
        foreach (var change in new Action<JsonNode>[]
        {
            invalid => invalid["phases"]![0]!["evidence"] = "999",
            invalid => invalid["phases"]![0]!["mechanics"]![0] = "999",
            invalid => invalid["phases"]![0]!["name"] = "Invented phase",
            invalid => invalid["phases"]![0]!["mechanics"]!.AsArray().Add("1")
        })
        {
            var invalid = plan.DeepClone();
            change(invalid);
            Reject(() => GuidePageAnalysis.ApplyPhasePlan(unphased, source.Page.Text, invalid.ToJsonString()));
        }
        var parsedBoss = parsed.Bosses.Single();
        Check(parsedBoss.PhaseDefinitions.Select(phase => phase.Name).SequenceEqual(["Phase 1", "Phase 2"])
            && parsedBoss.Phases.Single().Mechanics.Take(3).All(mechanic => mechanic.PhaseMemberships.Single().PhaseID == "phase-1"),
            "Nested Titan/Garuda/Ifrit parts were promoted into extra encounter phases.");
        var actor = new GuideActorState(10, 20, 30, parsedBoss.Name, false, true, 5);
        var tracker = new GuideEncounterTracker();
        tracker.Update(parsed, [actor], false);
        var at = DateTime.UtcNow;
        GuideSignal Signal(int index) => new(parsedBoss, parsedBoss.Phases[0], parsedBoss.Phases[0].Mechanics[index], GuideSignalKind.Cast,
            actor.ID, actor.OID, actor.NameID, (uint)(40 + index), 0, at.AddSeconds(5 + index), GuidanceKind.None, "Recorded source phase fixture");
        for (var index = 0; index < 3; ++index)
        {
            tracker.Synchronize([Signal(index)]);
            Check(tracker.Frame.KnownPhase?.Name == "Phase 1", "Changing nested parts left the first documented phase.");
        }
        tracker.Synchronize([Signal(3)]);
        Check(tracker.Frame.KnownPhase?.Name == "Phase 2", "An exclusive second-phase cast did not establish Phase 2.");
        Console.WriteLine("Source hierarchy regression: Phase 1 retains three nested parts; exclusive Homing Lasers establishes Phase 2 (fixture, no inference).");
    }
}
